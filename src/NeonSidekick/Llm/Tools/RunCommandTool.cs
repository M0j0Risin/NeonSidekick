using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>run_command(command, shell?, workdir?, timeout?, background?, notify?)</c> (2026-09-21): a command
/// line in a shell on the user's machine, its exit code and output back as one text
/// (<see cref="ShellText.Result"/>) — or, with <c>background</c> (or a <c>timeout</c> over the
/// foreground cap, which promotes the call rather than refusing it), started under the
/// <see cref="ProcessRegistry"/> and answered at once with its id for the <c>process</c> tool; with
/// <c>notify</c> the user sees an alert when it exits and the model a seeded poll at its next turn. It starts in the working directory (or a folder under it —
/// <c>workdir</c> goes through the sandbox's judge) but is not confined to it: the sandbox is where
/// a command begins, and the guard is the gate (<see cref="CommandGate"/>) — under the default
/// policy the user approves a command whose prefixes are not on the allow list before it runs,
/// and a denial is an <c>Error:</c> sentence the model is told not to work around. Since 2026-09-22
/// the police stands before the gate (<see cref="PathPolice"/>, the setting <c>Shell police outside
/// paths</c>, on by default): a line whose text names a path outside the working directory is refused
/// with <see cref="ShellText.OutsidePath"/> and never put to the pane; since 2026-09-26 a line a native tool offered
/// that turn covers (<see cref="NativeRedirect"/>, the setting <c>Shell prefer native tools</c>) is sent back to that tool
/// once a turn, the pane not asked either — lexical, the text and not what
/// runs — and the description says the command stays under the working directory (off, it says only
/// where the command starts). The shell is the
/// argument's, else the setting <c>Shell default</c>; the <c>shell</c> enum the model sees is the
/// shells actually installed (<see cref="Interpreters.AvailableShells"/>), rebuilt when that set or
/// the default changes (the <see cref="AskUserTool"/> shape). The wait is bounded by <c>timeout</c>
/// (the setting <c>Shell timeout (s)</c> by default, the foreground cap at most); on the timeout
/// the child and everything it started are killed and what it wrote so far comes back under a
/// timed-out header. Output over <c>Shell output max chars</c> keeps its head and tail, the whole
/// text written to <c>.shell\&lt;id&gt;.log</c> under the working directory, where <c>read_file</c>
/// can reach it. Stdin is closed at once: a command that reads it sees end of input, never a hang.
/// </summary>
public sealed class RunCommandTool : AIFunction
{
    public const string ToolName = "run_command";
    public const string CommandArgument = "command";
    public const string ShellArgument = "shell";
    public const string WorkdirArgument = "workdir";
    public const string TimeoutArgument = "timeout";
    public const string BackgroundArgument = "background";
    public const string NotifyArgument = "notify";

    /// <summary>The id prefix of a foreground run: the spill file's name and the log's.</summary>
    public const string IdPrefix = "run_";

    /// <summary>How long a killed child gets to drain its pipes before the result is built from what arrived.</summary>
    public static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    private readonly ShellRunner _runner;
    private readonly ProcessRegistry _registry;
    private readonly WorkingDirectory _files;
    private readonly CommandGate _gate;
    private readonly Interpreters _interpreters;
    private readonly Func<AppSettingsData> _effective;
    private readonly Random _random;

    // The turn's offer and the lines already sent back to a native tool in it (Shell prefer native tools, 2026-09-26): BeginTurn resets both; the lock because a turn may call tools side by side.
    private readonly Lock _turnLock = new();
    private IReadOnlySet<string> _offered = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _redirected = new(StringComparer.Ordinal);

    // The schema for the shells last seen: a request reads JsonSchema once per tool, and the set
    // changes only on an install or a settings edit, so one parse per change.
    private string _schemaKey = "";
    private JsonElement _schema;

    public RunCommandTool(ShellRunner runner, ProcessRegistry registry, WorkingDirectory files, CommandGate gate, Interpreters interpreters, Func<AppSettingsData> effective, Random random)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _interpreters = interpreters ?? throw new ArgumentNullException(nameof(interpreters));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public override string Name => ToolName;

    /// <summary>
    /// What the model reads with the setting <c>Shell police outside paths</c> on (2026-09-22): the command may only
    /// name paths under the working directory, and a refusal is final like a denial. Until that day the one
    /// description said the command "is not confined to" the working directory; neither variant says so now. Pinned.
    /// </summary>
    public const string DescriptionPoliced =
        "Runs a command line in a shell on the user's computer and returns its exit code and output. " +
        "It runs in the working directory and may only name paths under it (relative, or absolute under it); the user approves a command before it runs and may deny it. " +
        "Use it for a program, a build, a test or a script the user asks for, never for what another tool does (files, git, the web, SQL); a denied or refused command must not be retried or worked around. " +
        "Use background for a server or a long job and the process tool to read it.";

    /// <summary>… and with the police off: the command starts in the working directory, and not a word about where it may reach. Pinned.</summary>
    public const string DescriptionUnpoliced =
        "Runs a command line in a shell on the user's computer and returns its exit code and output. " +
        "It starts in the working directory; the user approves a command before it runs and may deny it. " +
        "Use it for a program, a build, a test or a script the user asks for, never for what another tool does (files, git, the web, SQL); a denied command must not be retried or worked around. " +
        "Use background for a server or a long job and the process tool to read it.";

    /// <summary>The two descriptions by the setting (<see cref="DescriptionPoliced"/>, <see cref="DescriptionUnpoliced"/>).</summary>
    public static string DescribeTool(bool police) => police ? DescriptionPoliced : DescriptionUnpoliced;

    public override string Description => DescribeTool(_effective().ShellPoliceOutsidePaths);

    /// <summary>The shells the model may name right now, in <see cref="ShellKinds.Names"/> order.</summary>
    public IReadOnlyList<string> AvailableShells => _interpreters.AvailableShells().Select(ShellKinds.Name).ToList();

    /// <summary>The default shell as the settings stand now.</summary>
    public string DefaultShell => ShellKinds.Name(ShellKinds.Resolve(_effective()));

    /// <summary>
    /// A new turn (2026-09-26): the names it offers, which <see cref="NativeRedirect"/> may send a line to, and no line
    /// sent back yet — the once-a-turn rule of <c>Shell prefer native tools</c> starts over. Called by
    /// <see cref="App.ChatScreen.PrepareTurn"/>, headless too; never called (tests over the tool alone), nothing is offered and nothing redirected.
    /// </summary>
    public void BeginTurn(IEnumerable<string> offeredNames)
    {
        ArgumentNullException.ThrowIfNull(offeredNames);
        var offered = new HashSet<string>(offeredNames, StringComparer.Ordinal);
        lock (_turnLock)
        {
            _offered = offered;
            _redirected.Clear();
        }
    }

    /// <summary>The native tool this line goes back to, or null: the first time this turn a line a tool offered this turn covers is seen.</summary>
    private (string Prefix, string Tool)? Redirect(string command)
    {
        lock (_turnLock)
        {
            return NativeRedirect.For(command, _offered) is { } native && _redirected.Add(command) ? native : null;
        }
    }

    public override JsonElement JsonSchema
    {
        get
        {
            var shells = AvailableShells;
            string fallback = DefaultShell;
            int cap = ForegroundCap(_effective());
            string key = string.Join(",", shells) + "|" + fallback + "|" + N(cap);
            if (_schema.ValueKind == JsonValueKind.Undefined || !string.Equals(key, _schemaKey, StringComparison.Ordinal))
            {
                _schema = SchemaFor(shells, fallback, AppSettingsData.MinShellTimeoutSeconds, AppSettingsData.MaxShellTimeoutSeconds, cap);
                _schemaKey = key;
            }

            return _schema;
        }
    }

    /// <summary>The schema over the shells installed, naming the default and the foreground cap. Pinned.</summary>
    public static JsonElement SchemaFor(IReadOnlyList<string> shells, string defaultShell, int minTimeout, int maxTimeout, int cap)
    {
        ArgumentNullException.ThrowIfNull(shells);
        string names = string.Join(", ", shells.Select(s => "\"" + s + "\""));
        return ToolSchema.Parse(
            $$"""
            {
              "type": "object",
              "properties": {
                "command": { "type": "string", "description": "The command line, as you would type it in that shell." },
                "shell": { "type": "string", "enum": [{{names}}], "description": "The shell that runs it; leave it out for the user's default ({{defaultShell}})." },
                "workdir": { "type": "string", "description": "A folder relative to the working directory to run in; leave it out for the working directory itself." },
                "timeout": { "type": "integer", "description": "Seconds to wait before the command is stopped, {{N(minTimeout)}} to {{N(maxTimeout)}}; leave it out for the user's default. Over {{N(cap)}} the command runs in the background instead." },
                "background": { "type": "boolean", "description": "true starts it and returns a session id at once (a server, a watcher, a long build); read it with the process tool." },
                "notify": { "type": "boolean", "description": "With background: true tells the user and you when it exits." }
              },
              "required": ["command"]
            }
            """);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var effective = _effective();
        string command = ToolArguments.ReadString(arguments, CommandArgument).Trim();
        if (command.Length == 0)
        {
            return ShellText.CommandRequired;
        }

        string shellText = ToolArguments.ReadString(arguments, ShellArgument).Trim();
        ShellKind kind;
        if (shellText.Length == 0)
        {
            kind = ShellKinds.Resolve(effective);
        }
        else if (!ShellKinds.TryParse(shellText, out kind))
        {
            return FileText.BadChoice(ShellArgument, shellText, string.Join(", ", ShellKinds.Names));
        }

        if (kind == ShellKind.PowerShell && command.Length > ShellCommandLine.MaxCommandChars)
        {
            return ShellText.CommandTooLong(ShellCommandLine.MaxCommandChars);
        }

        if (_interpreters.Locate(kind) is not { } executable)
        {
            return ShellText.ShellNotInstalled(kind);
        }

        string workdirText = ToolArguments.ReadString(arguments, WorkdirArgument).Trim();
        string workdir;
        if (workdirText.Length == 0)
        {
            try
            {
                workdir = _files.EnsureExists();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return FileText.CouldNot("create", _files.Root, ex.Message);
            }
        }
        else
        {
            if (_files.Resolve(workdirText, forWrite: false, out workdir) == FileOutcome.OutsideRoot)
            {
                return ShellText.WorkdirOutside(workdirText);
            }

            if (!Directory.Exists(workdir))
            {
                return ShellText.WorkdirNotFolder(workdirText);
            }
        }

        int cap = ForegroundCap(effective);
        if (!ToolArguments.TryReadInt32(arguments, TimeoutArgument, out int? timeoutSeconds, out _)
            || (timeoutSeconds is { } given && (given < AppSettingsData.MinShellTimeoutSeconds || given > AppSettingsData.MaxShellTimeoutSeconds)))
        {
            return ShellText.BadTimeout(AppSettingsData.MinShellTimeoutSeconds, AppSettingsData.MaxShellTimeoutSeconds);
        }

        if (!ToolArguments.TryReadBoolean(arguments, BackgroundArgument, out bool? backgroundFlag, out string rawBackground))
        {
            return FileText.BadChoice(BackgroundArgument, rawBackground, "true, false");
        }

        if (!ToolArguments.TryReadBoolean(arguments, NotifyArgument, out bool? notifyFlag, out string rawNotify))
        {
            return FileText.BadChoice(NotifyArgument, rawNotify, "true, false");
        }

        // The argument, else the setting clamped to its range and to the foreground cap; an argument over the cap promotes the call to the background (2026-09-21, the Hermes shape).
        bool promoted = timeoutSeconds is { } asked && asked > cap;
        bool background = backgroundFlag == true || promoted;
        int seconds = timeoutSeconds ?? Math.Min(cap, Math.Clamp(effective.ShellTimeoutSeconds, AppSettingsData.MinShellTimeoutSeconds, AppSettingsData.MaxShellTimeoutSeconds));
        var timeout = TimeSpan.FromSeconds(seconds);

        var request = new CommandRequest(ShellKinds.Name(kind), command, CommandPrefix.All(command));
        // The police before the gate (Shell police outside paths, 2026-09-22): a line naming a path outside the working directory is refused, and the pane is never asked about it.
        if (effective.ShellPoliceOutsidePaths && PathPolice.Judge(command, _files, workdir, isScript: false, request.Kind) is { } outside)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.PolicedLogLine(request, outside));
            _gate.NoteRefused(request);
            return ShellText.OutsidePath(outside);
        }

        // Then a native tool's own line (Shell prefer native tools, 2026-09-26): sent back once a turn, before the pane is asked. A path outside the working directory
        // with the police off is the shell's alone, no native tool reaching there.
        if (effective.ShellPreferNative
            && (effective.ShellPoliceOutsidePaths || PathPolice.Judge(command, _files, workdir, isScript: false, request.Kind) is null)
            && Redirect(command) is { } native)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.NativeLogLine(request, native.Prefix, native.Tool));
            return ShellText.UseNative(native.Prefix, native.Tool);
        }

        var verdict = await _gate.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!verdict.Allowed)
        {
            return verdict.Error;
        }

        var launch = ShellCommandLine.For(kind, command, executable, workdir);
        if (background)
        {
            bool notify = notifyFlag == true;
            try
            {
                var started = _registry.Start(launch, notify);
                DiagnosticLog.Info(ShellKinds.Category, ShellText.RunLogLine(request.Kind, command, "started " + started.Id + " in the background", 0));
                return promoted
                    ? ShellText.Promoted(started.Id, request.Kind, started.Pid, command, timeoutSeconds!.Value, cap, notify)
                    : ShellText.Started(started.Id, request.Kind, started.Pid, command, notify);
            }
            catch (ShellStartException ex)
            {
                return ex.Message;
            }
        }

        ProcessSession session;
        try
        {
            session = _runner.Start(launch, ShellRunner.NewId(_random, IdPrefix));
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
                // ESC ended the turn: the child goes with it.
                session.Kill();
                DiagnosticLog.Info(ShellKinds.Category, ShellText.RunLogLine(request.Kind, command, "cancelled after " + ShellText.Elapsed(session.Elapsed), session.Output.TotalChars));
                throw;
            }

            if (!exited)
            {
                session.Kill();
                await session.WaitAsync(KillGrace, CancellationToken.None).ConfigureAwait(false);
            }

            string header = exited
                ? ShellText.ExitHeader(request.Kind, session.ExitCode ?? -1, session.Elapsed, command)
                : ShellText.TimedOutHeader(request.Kind, timeout, command);
            int maxChars = Math.Clamp(effective.ShellOutputMaxChars, AppSettingsData.MinShellOutputMaxChars, AppSettingsData.MaxShellOutputMaxChars);
            var lines = session.Output.Lines();
            string body = ShellText.Body(lines);
            string? spill = body.Length > maxChars ? Spill(session.Id, body) : null;
            DiagnosticLog.Info(ShellKinds.Category, ShellText.RunLogLine(request.Kind, command, exited ? "exit " + (session.ExitCode ?? -1).ToString(CultureInfo.InvariantCulture) + " in " + ShellText.Elapsed(session.Elapsed) : "timed out after " + ShellText.Elapsed(timeout), body.Length));
            return ShellText.Result(header, lines, maxChars, spill);
        }
    }

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

    /// <summary>The setting <c>Shell foreground cap (s)</c>, clamped to its range: the most a foreground wait may be.</summary>
    public static int ForegroundCap(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.ShellForegroundCapSeconds, AppSettingsData.MinShellForegroundCapSeconds, AppSettingsData.MaxShellForegroundCapSeconds);
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
