using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Shell.Bridge;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>execute_code(language, code, timeout?)</c> (2026-09-21): a script in a fresh interpreter
/// process — python, node or powershell, whichever the setting <c>Shell code languages</c> allows
/// and the machine has (the enum the model sees is that intersection, rebuilt when it changes) —
/// that can call the app's other tools while it runs, through a <see cref="BridgeServer"/> alive
/// for the run and a small module written beside the script (<see cref="CodeLaunch"/>). The bridge
/// dispatches by name to the same tool list the turn offers the model (<paramref name="tools"/>),
/// less <c>execute_code</c> itself and <c>ask_user</c>; a nested <c>run_command</c> goes through
/// the gate as usual (the approval pane pops while the script waits on its socket) but never in
/// the background. The same gate judges the script first, under the pseudo-prefix
/// <c>code:&lt;language&gt;</c>, so "allow python scripts for this session" is one pick. The result
/// is the <c>run_command</c> shape with the tool-call count in the header; the run folder under
/// the temp directory goes when the run does. No kernel: state lives in files the script writes.
/// Since 2026-09-22 the police reads the script before the gate (<see cref="PathPolice"/>, the setting
/// <c>Shell police</c>, on by default): a script whose text names a path outside the
/// working directory is refused with <see cref="ShellText.OutsidePath"/>, and the description and the
/// <c>code</c> property say the script stays under it (off, neither says a word about where it may reach).
/// </summary>
public sealed class ExecuteCodeTool : AIFunction
{
    public const string ToolName = "execute_code";
    public const string LanguageArgument = "language";
    public const string CodeArgument = "code";
    public const string TimeoutArgument = "timeout";

    /// <summary>The id prefix of a run: the run folder's name and the spill file's.</summary>
    public const string IdPrefix = "code_";

    /// <summary>The folder every run's folder sits in, under the temp directory.</summary>
    public const string RunsFolderName = "neonsidekick-code";

    /// <summary>The tools a script may not reach: itself (a run inside a run) and the question pane (the script has no screen).</summary>
    public static readonly IReadOnlySet<string> Excluded = new HashSet<string>(StringComparer.Ordinal) { ToolName, AskUserTool.ToolName };

    private readonly ShellRunner _runner;
    private readonly WorkingDirectory _files;
    private readonly CommandGate _gate;
    private readonly Interpreters _interpreters;
    private readonly Func<AppSettingsData> _effective;
    private readonly Func<IReadOnlyList<AIFunction>> _tools;
    private readonly Random _random;
    private readonly string _runsFolder;

    private string _schemaKey = "";
    private JsonElement _schema;

    /// <param name="tools">The tools the turn offers the model, read at each bridge call (the screen's assistant's list).</param>
    /// <param name="runsFolder">Where run folders are made; the temp directory's <see cref="RunsFolderName"/> by default, a temp folder in tests.</param>
    public ExecuteCodeTool(ShellRunner runner, WorkingDirectory files, CommandGate gate, Interpreters interpreters, Func<AppSettingsData> effective, Func<IReadOnlyList<AIFunction>> tools, Random random, string? runsFolder = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _interpreters = interpreters ?? throw new ArgumentNullException(nameof(interpreters));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _runsFolder = runsFolder ?? Path.Combine(Path.GetTempPath(), RunsFolderName);
    }

    public override string Name => ToolName;

    /// <summary>What the model reads with the bridge on and the police on (2026-09-22, the default): the script may call the other tools and stays under the working directory. Pinned.</summary>
    public const string DescriptionWithBridge =
        "Runs a script (python, node or powershell) in a fresh process and returns what it printed. " +
        "The script can call this app's other tools by name through the neon_tools module, so several steps can be done in one call; " +
        "it runs in the working directory and may only name paths under it, the same approval as run_command applies, and a denied or refused script must not be retried or worked around.";

    /// <summary>What the model reads with the bridge off (<c>Shell tool bridge</c>, later on 2026-09-21) and the police on: not a word about calling tools, so it never tries. Pinned.</summary>
    public const string DescriptionWithoutBridge =
        "Runs a script (python, node or powershell) in a fresh process and returns what it printed; " +
        "it runs in the working directory and may only name paths under it, the same approval as run_command applies, and a denied or refused script must not be retried or worked around.";

    /// <summary><see cref="DescriptionWithBridge"/> with the police off (<c>Shell police</c>, 2026-09-22): the text until that day — not a word about where the script may reach. Pinned.</summary>
    public const string DescriptionWithBridgeUnpoliced =
        "Runs a script (python, node or powershell) in a fresh process and returns what it printed. " +
        "The script can call this app's other tools by name through the neon_tools module, so several steps can be done in one call; " +
        "the same approval as run_command applies, and a denied script must not be retried or worked around.";

    /// <summary><see cref="DescriptionWithoutBridge"/> with the police off: neither the bridge nor the confinement is named. Pinned.</summary>
    public const string DescriptionWithoutBridgeUnpoliced =
        "Runs a script (python, node or powershell) in a fresh process and returns what it printed; " +
        "the same approval as run_command applies, and a denied script must not be retried or worked around.";

    /// <summary>The four descriptions by the two settings.</summary>
    public static string DescribeTool(bool bridge, bool police) => (bridge, police) switch
    {
        (true, true) => DescriptionWithBridge,
        (false, true) => DescriptionWithoutBridge,
        (true, false) => DescriptionWithBridgeUnpoliced,
        (false, false) => DescriptionWithoutBridgeUnpoliced,
    };

    /// <summary>The bridge's on/off, read at each look — the description, the schema and the run all follow the setting.</summary>
    private bool Bridge => _effective().ShellToolBridge;

    public override string Description => DescribeTool(Bridge, _effective().ShellPolice);

    /// <summary>The languages the model may name right now: the setting's, whose interpreter is found, in <see cref="CodeLanguages.Names"/> order.</summary>
    public IReadOnlyList<string> AvailableLanguages => _interpreters.AvailableLanguages(CodeLanguages.Resolve(_effective())).Select(CodeLanguages.Name).ToList();

    public override JsonElement JsonSchema
    {
        get
        {
            var languages = AvailableLanguages;
            var effective = _effective();
            int cap = Math.Clamp(effective.ShellCodeTimeoutSeconds, AppSettingsData.MinShellCodeTimeoutSeconds, AppSettingsData.MaxShellCodeTimeoutSeconds);
            bool bridge = effective.ShellToolBridge;
            bool police = effective.ShellPolice;
            string key = string.Join(",", languages) + "|" + cap.ToString(CultureInfo.InvariantCulture) + (bridge ? "|bridge" : "") + (police ? "|police" : "");
            if (_schema.ValueKind == JsonValueKind.Undefined || !string.Equals(key, _schemaKey, StringComparison.Ordinal))
            {
                _schema = SchemaFor(languages, cap, bridge, police);
                _schemaKey = key;
            }

            return _schema;
        }
    }

    /// <summary>The <c>code</c> property's description with the bridge on: how each language calls a tool. Pinned.</summary>
    public const string CodeDescriptionWithBridge = "The script. It can call this app's tools: Python `from neon_tools import call, read_file, run_command` (call('tool_name', arg=value) for any tool); Node `const neon = require('neon_tools'); await neon.call('tool_name', { arg: value })` inside neon.run(async () => { ... }); PowerShell `Invoke-NeonTool tool_name @{ arg = value }`. Every tool returns its text; print what you want back.";

    /// <summary>… and with the bridge off: the script alone. Pinned.</summary>
    public const string CodeDescriptionWithoutBridge = "The script; print what you want back.";

    /// <summary>What the <c>code</c> property's description gains with the police on (2026-09-22): the one rule a script must keep. Pinned.</summary>
    public const string CodeDescriptionPolicedSuffix = " Every path in it must stay under the working directory.";

    /// <summary>
    /// The schema over the languages offered; with <paramref name="bridge"/> the <c>code</c> property says how a script
    /// calls a tool, without it not a word (later on 2026-09-21); with <paramref name="police"/> it ends with
    /// <see cref="CodeDescriptionPolicedSuffix"/> (2026-09-22). Pinned.
    /// </summary>
    public static JsonElement SchemaFor(IReadOnlyList<string> languages, int defaultTimeout, bool bridge = true, bool police = true)
    {
        ArgumentNullException.ThrowIfNull(languages);
        string names = string.Join(", ", languages.Select(l => "\"" + l + "\""));
        string code = (bridge ? CodeDescriptionWithBridge : CodeDescriptionWithoutBridge) + (police ? CodeDescriptionPolicedSuffix : "");
        return ToolSchema.Parse(
            $$"""
            {
              "type": "object",
              "properties": {
                "language": { "type": "string", "enum": [{{names}}], "description": "Which interpreter runs the script." },
                "code": { "type": "string", "description": "{{code}}" },
                "timeout": { "type": "integer", "description": "Seconds before the script is stopped, {{AppSettingsData.MinShellCodeTimeoutSeconds}} to {{AppSettingsData.MaxShellCodeTimeoutSeconds}} (default {{defaultTimeout}})." }
              },
              "required": ["language", "code"]
            }
            """);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var effective = _effective();
        string code = ToolArguments.ReadString(arguments, CodeArgument);
        if (code.Trim().Length == 0)
        {
            return ShellText.CodeRequired;
        }

        string languageText = ToolArguments.ReadString(arguments, LanguageArgument).Trim();
        if (!CodeLanguages.TryParse(languageText, out var language))
        {
            return FileText.BadChoice(LanguageArgument, languageText, string.Join(", ", CodeLanguages.Names));
        }

        if (!CodeLanguages.Resolve(effective).Contains(language))
        {
            return ShellText.LanguageNotEnabled(CodeLanguages.Name(language));
        }

        if (_interpreters.Locate(language) is not { } executable)
        {
            return ShellText.LanguageNotInstalled(language);
        }

        if (!ToolArguments.TryReadInt32(arguments, TimeoutArgument, out int? timeoutSeconds, out _)
            || (timeoutSeconds is { } given && (given < AppSettingsData.MinShellCodeTimeoutSeconds || given > AppSettingsData.MaxShellCodeTimeoutSeconds)))
        {
            return ShellText.BadTimeout(AppSettingsData.MinShellCodeTimeoutSeconds, AppSettingsData.MaxShellCodeTimeoutSeconds);
        }

        int seconds = timeoutSeconds ?? Math.Clamp(effective.ShellCodeTimeoutSeconds, AppSettingsData.MinShellCodeTimeoutSeconds, AppSettingsData.MaxShellCodeTimeoutSeconds);
        var timeout = TimeSpan.FromSeconds(seconds);
        int maxCalls = Math.Clamp(effective.ShellCodeMaxToolCalls, AppSettingsData.MinShellCodeMaxToolCalls, AppSettingsData.MaxShellCodeMaxToolCalls);
        int maxChars = Math.Clamp(effective.ShellOutputMaxChars, AppSettingsData.MinShellOutputMaxChars, AppSettingsData.MaxShellOutputMaxChars);
        string name = CodeLanguages.Name(language);
        string firstLine = ShellText.FirstLine(code);

        string workingDirectory;
        try
        {
            workingDirectory = _files.EnsureExists();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return FileText.CouldNot("create", _files.Root, ex.Message);
        }

        var request = new CommandRequest(name, code, [ShellText.ScriptPrefix(name)], IsScript: true);
        // The forbidden strings first (Shell police forbidden strings, 2026-10-03), anywhere in the script, under the police's own switch.
        if (effective.ShellPolice && ForbiddenStrings.Find(code, effective.ShellPoliceForbiddenStrings) is { } forbidden)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.ForbiddenLogLine(request, forbidden));
            _gate.NoteRefused(request);
            return new ToolShownResult(ShellText.Forbidden, ShellText.ForbiddenShown(forbidden));
        }

        // The SQLite police (2026-10-05), under Shell police while the SQLite tools are on: a script that reaches SQLite is refused whatever the policy.
        if (effective.ShellPolice && _files.Databases?.Invoke() is { } databases && SqlitePolice.Find(code, script: true, databases) is { } sqlite)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.SqliteLogLine(request, sqlite, null));
            _gate.NoteRefused(request);
            return new ToolShownResult(ShellText.SqlitePoliced, ShellText.SqliteShown(sqlite, null));
        }

        // The police before the gate (Shell police, 2026-09-22): a script naming a path outside the working directory is refused, and the pane is never asked about it.
        if (effective.ShellPolice && PathPolice.Judge(code, _files, workingDirectory, isScript: true) is { } outside)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.PolicedLogLine(request, outside));
            _gate.NoteRefused(request);
            return ShellText.OutsidePath(outside);
        }

        var verdict = await _gate.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!verdict.Allowed)
        {
            return verdict.Error;
        }

        string id = ShellRunner.NewId(_random, IdPrefix);
        string runFolder = Path.Combine(_runsFolder, id);
        // The bridge (Shell tool bridge, later on 2026-09-21): off, no module is written, no server started and no address or token handed down — the script is on its own.
        bool bridgeOn = effective.ShellToolBridge;
        var files = CodeLaunch.Files(language, code, runFolder, bridgeOn);
        try
        {
            Directory.CreateDirectory(runFolder);
            // A BOM for PowerShell alone: 5.1 reads a .ps1 without one as ANSI, and an é in the script would come out wrong; python and node take plain UTF-8.
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: language == CodeLanguage.PowerShell);
            if (files.ModulePath is { } relative)
            {
                string modulePath = Path.Combine(runFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(modulePath)!);
                File.WriteAllText(modulePath, files.ModuleText, encoding);
            }

            File.WriteAllText(Path.Combine(runFolder, files.ScriptName), files.ScriptText, encoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ShellText.CouldNotWriteScript(ex.Message);
        }

        try
        {
            await using var bridge = bridgeOn ? BridgeServer.Start(DispatchAsync, maxCalls) : null;
            ProcessSession session;
            try
            {
                session = _runner.Start(CodeLaunch.For(language, executable, runFolder, files.ScriptName, workingDirectory, bridge?.Address, bridge?.Token, firstLine), id);
            }
            catch (ShellStartException ex)
            {
                return ex.Message;
            }

            using (session)
            {
                session.CloseInput();
                bool exited;
                try
                {
                    exited = await session.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    session.Kill();
                    DiagnosticLog.Info(ShellKinds.Category, ShellText.ScriptLogLine(name, firstLine, "cancelled after " + ShellText.Elapsed(session.Elapsed), bridge?.Calls, session.Output.TotalChars));
                    throw;
                }

                if (!exited)
                {
                    session.Kill();
                    await session.WaitAsync(RunCommandTool.KillGrace, CancellationToken.None).ConfigureAwait(false);
                }

                int? calls = bridge?.Calls;
                string header = exited
                    ? ShellText.ScriptExitHeader(name, session.ExitCode ?? -1, session.Elapsed, calls, firstLine)
                    : ShellText.ScriptTimedOutHeader(name, timeout, calls, firstLine);
                var lines = session.Output.Lines();
                string body = ShellText.Body(lines);
                string? spill = body.Length > maxChars ? Spill(id, body) : null;
                DiagnosticLog.Info(ShellKinds.Category, ShellText.ScriptLogLine(name, firstLine, exited ? "exit " + (session.ExitCode ?? -1).ToString(CultureInfo.InvariantCulture) + " in " + ShellText.Elapsed(session.Elapsed) : "timed out after " + ShellText.Elapsed(timeout), calls, body.Length));
                return ShellText.Result(header, lines, maxChars, spill);
            }
        }
        finally
        {
            TryDelete(runFolder);
        }
    }

    /// <summary>One bridge call: the tool by exact name among the turn's, less <see cref="Excluded"/>; a nested <c>run_command</c> never in the background.</summary>
    private async Task<string> DispatchAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        if (Excluded.Contains(tool))
        {
            return ShellText.UnknownTool(tool);
        }

        var offered = _tools().Where(t => !Excluded.Contains(t.Name)).ToList();
        if (!offered.Any(t => string.Equals(t.Name, tool, StringComparison.Ordinal)))
        {
            return ShellText.UnknownTool(tool);
        }

        var own = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
        if (string.Equals(tool, RunCommandTool.ToolName, StringComparison.Ordinal))
        {
            if (own.TryGetValue(RunCommandTool.BackgroundArgument, out var background) && IsTrue(background))
            {
                return ShellText.BackgroundNotFromScript;
            }

            own.Remove(RunCommandTool.BackgroundArgument);
            own.Remove(RunCommandTool.NotifyArgument);
        }

        var call = new FunctionCallContent("bridge", tool, own);
        var (text, _) = await Assistant.InvokeToolAsync(offered, call, cancellationToken).ConfigureAwait(false);
        return text;
    }

    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.String } s => string.Equals(s.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
        string text => string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>The whole output into <c>.shell\&lt;id&gt;.log</c> under the working directory; the relative path, or null when it could not be written (logged).</summary>
    private string? Spill(string id, string body)
    {
        string relative = Path.Combine(ShellText.SpillFolderName, id + ".log");
        try
        {
            string folder = Path.Combine(_files.Root, ShellText.SpillFolderName);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, id + ".log"), body);
            return relative;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DiagnosticLog.Warn(ShellKinds.Category, $"{id}: could not write {relative} ({ex.Message})");
            return null;
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A child still holding a file: the temp folder is the OS's to sweep.
            DiagnosticLog.Debug(ShellKinds.Category, $"could not remove {folder} ({ex.Message})");
        }
    }
}
