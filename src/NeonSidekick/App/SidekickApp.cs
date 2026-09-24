using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Memory;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Speech;
using NeonSidekick.UI;
using NeonSidekick.Web;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The application shell. Everything it draws goes through the injected <see cref="IAnsiConsole"/>
/// so a <c>TestConsole</c> can drive every screen; nothing here touches the static
/// <c>AnsiConsole</c>. The headless REPL reads and writes the injected <see cref="TextReader"/> /
/// <see cref="TextWriter"/> for the same reason.
///
/// <para>Milestone 0: banner, smoke gate. Milestone 1: the headless REPL talks to a
/// real server through <see cref="Assistant"/>. Milestone 2: the interactive screen is
/// <see cref="ChatScreen"/>. Milestone 3: speech output. Milestone 4: push-to-talk voice input.
/// Milestone 5: the wake word, behind the same constructor.</para>
/// </summary>
public sealed class SidekickApp
{
    public const string Name = "NeonSidekick";

    /// <summary>The second line of headless output.</summary>
    public const string HeadlessHint = "Headless mode. Type a message; /clear or /new forgets the conversation; /compact [focus] shrinks it; /exit or EOF exits.";

    /// <summary>Printed once when discovery under <paramref name="scope"/> found nothing. Pinned by tests; shared with the chat screen.</summary>
    public static string HeadlessNoServerLine(ScanScope scope) => LlmSession.NoServerLine(scope);

    /// <summary>The reply to every message when there is no assistant. Pinned by tests.</summary>
    public static readonly string HeadlessNoAssistantReply =
        $"[error] No LLM endpoint. Set {EnvironmentOverrides.LlmUrlVariable} and restart.";

    private const string Category = "App";
    private const string HeadlessReplyPrefix = "Neon: ";

    private readonly IAnsiConsole _console;
    private readonly AppSettings _settings;
    private readonly EnvironmentOverrides _environment;
    private readonly TextReader _headlessInput;
    private readonly TextWriter _headlessOutput;
    private readonly Func<IReadOnlyList<SmokeCheck>> _smokeChecks;
    private readonly LlmEndpointProbe _probe;
    private readonly ContextLengthProbe _contextProbe;
    private readonly Func<LlmEndpoint, LlmTimeouts, IChatClient> _chatClientFactory;
    private readonly Func<PcmFormat, IAudioPlayback> _playbackFactory;
    private readonly Func<SynthesizerRequest, ISpeechSynthesizer> _synthesizerFactory;
    private readonly Func<PcmFormat, IAudioCapture> _captureFactory;
    private readonly Func<string, ISpeechRecognizer> _recognizerFactory;
    private readonly Func<string, VadOptions, IVoiceActivityDetector> _vadFactory;
    private readonly HttpClient _modelHttpClient;
    private readonly WebAccess _web;
    private readonly Func<Mcp.McpServerConfig, string, ModelContextProtocol.Client.IClientTransport> _mcpTransport;
    private readonly Func<Uri, Comfy.ComfyClient>? _comfyClient;
    private readonly string _externalSkills;
    private readonly Func<int> _inputDeviceCount;
    private readonly Func<string, string, IWakeWordDetector> _wakeDetectorFactory;
    private readonly TimeProvider _time;
    private readonly ScreenGeometry? _geometry;
    private readonly IAnsiConsoleInput? _input;
    private readonly Func<string?>? _clipboard;
    private readonly Func<string, bool>? _copyToClipboard;
    private readonly Func<byte[]?>? _clipboardImage;
    private readonly Action<string>? _setTitle;

    /// <summary>Whether the headless cursor sits at the start of a line; diagnostics need their own line.</summary>
    private bool _headlessAtLineStart = true;

    /// <summary>The flags of the current run; <see cref="SidekickOptions.None"/> until <see cref="RunAsync"/>.</summary>
    private SidekickOptions _options = SidekickOptions.None;

    /// <param name="console">The console every screen renders to.</param>
    /// <param name="settings">The saved settings store.</param>
    /// <param name="environment">Per-launch overrides; applied over <paramref name="settings"/> for display and use.</param>
    /// <param name="headlessInput">stdin for <c>--headless</c>; defaults to <see cref="Console.In"/>.</param>
    /// <param name="headlessOutput">stdout for <c>--headless</c>; defaults to <see cref="Console.Out"/>.</param>
    /// <param name="smokeChecks">The <c>--smoke</c> checks; defaults to <see cref="SmokeChecks.Run"/> over the binary's directory.</param>
    /// <param name="probe">Endpoint discovery; defaults to a real <see cref="HttpClient"/>. Tests pass one over a stub handler.</param>
    /// <param name="contextProbe">The context-window probe run after each connect; defaults to a real <see cref="HttpClient"/>. Tests pass one over the same stub.</param>
    /// <param name="chatClientFactory">Builds the chat client for a resolved endpoint; defaults to <see cref="OpenAICompatibleChatClient"/>. Tests return a fake.</param>
    /// <param name="playbackFactory">Builds the speaker output for a format; defaults to <see cref="WinMmAudioPlayback"/>. Tests return a fake.</param>
    /// <param name="synthesizerFactory">Builds the synthesizer for a <see cref="SynthesizerRequest"/>; defaults to <see cref="KokoroHttpSynthesizer"/> over a URL and <see cref="KokoroInProcessSynthesizer"/> over a model path. Tests return a fake.</param>
    /// <param name="captureFactory">Builds the microphone for a format; defaults to <see cref="WinMmAudioCapture"/>. Tests return a fake.</param>
    /// <param name="recognizerFactory">Builds the recognizer for a model path; defaults to <see cref="WhisperNetTranscriber"/>.</param>
    /// <param name="vadFactory">Builds the voice activity detector for a model path; defaults to <see cref="SileroVad"/>.</param>
    /// <param name="modelHttpClient">Downloads model files; defaults to a client with no timeout (a 500 MB model over a slow link must not be cut at 100 s).</param>
    /// <param name="inputDeviceCount">How many microphones there are; defaults to <see cref="WinMmAudioCapture.InputDeviceCount"/>.</param>
    /// <param name="wakeDetectorFactory">Builds the wake-word recogniser for a model directory and phrase; defaults to <see cref="VoskWakeWordDetector"/>.</param>
    /// <param name="time">The clock the tools and the timers read; defaults to <see cref="TimeProvider.System"/>. Tests pass a manual one.</param>
    /// <param name="geometry">Where the console's cursor is, for the chat screen's bottom pane; <c>Program.cs</c> passes <see cref="ScreenGeometry.ForConsole"/>, tests a scripted one or (the default) none, which draws the input line where the transcript ends.</param>
    /// <param name="clipboard">The text the input row's own paste (a right click, Ctrl+V, Alt+V) puts on the line; <c>Program.cs</c> passes <see cref="WindowsClipboard.TryReadText"/>.</param>
    /// <param name="copyToClipboard">What <c>/copy</c> writes with; <c>Program.cs</c> passes <see cref="WindowsClipboard.TrySetText"/>, tests a recorder; null = every copy fails.</param>
    /// <param name="clipboardImage">The picture the same paste takes ahead of the text, as an image file's bytes; <c>Program.cs</c> passes <see cref="WindowsClipboard.TryReadImage"/>; null = never.</param>
    /// <param name="setTitle">What the interactive screen sets the terminal window's title with (the loaded profile's name, <see cref="ChatScreen.WindowTitle"/>); <c>Program.cs</c> passes <see cref="ConsoleTitle.TrySet"/>, tests a recorder; null = never. Headless and the checks never set one.</param>
    /// <param name="mcpTransport">What an MCP server's config becomes on the wire (<see cref="McpSession.DefaultTransport"/> in the app; tests a pipe into an in-process server); null = the app's.</param>
    public SidekickApp(
        IAnsiConsole console,
        AppSettings settings,
        EnvironmentOverrides environment,
        TextReader? headlessInput = null,
        TextWriter? headlessOutput = null,
        Func<IReadOnlyList<SmokeCheck>>? smokeChecks = null,
        LlmEndpointProbe? probe = null,
        ContextLengthProbe? contextProbe = null,
        Func<LlmEndpoint, LlmTimeouts, IChatClient>? chatClientFactory = null,
        Func<PcmFormat, IAudioPlayback>? playbackFactory = null,
        Func<SynthesizerRequest, ISpeechSynthesizer>? synthesizerFactory = null,
        Func<PcmFormat, IAudioCapture>? captureFactory = null,
        Func<string, ISpeechRecognizer>? recognizerFactory = null,
        Func<string, VadOptions, IVoiceActivityDetector>? vadFactory = null,
        HttpClient? modelHttpClient = null,
        Func<int>? inputDeviceCount = null,
        Func<string, string, IWakeWordDetector>? wakeDetectorFactory = null,
        TimeProvider? time = null,
        ScreenGeometry? geometry = null,
        IAnsiConsoleInput? input = null,
        Func<string?>? clipboard = null,
        Func<string, bool>? copyToClipboard = null,
        Func<byte[]?>? clipboardImage = null,
        WebAccess? web = null,
        Action<string>? setTitle = null,
        string? externalSkills = null,
        Func<Mcp.McpServerConfig, string, ModelContextProtocol.Client.IClientTransport>? mcpTransport = null,
        Func<Uri, Comfy.ComfyClient>? comfyClient = null)
    {
        // The ComfyUI client (2026-09-24): over its own transport in the app, a stub handler in tests.
        _comfyClient = comfyClient;
        _console = console ?? throw new ArgumentNullException(nameof(console));
        // The MCP servers' transport (2026-09-20): the SDK's stdio child or streamable HTTP in the app, a pipe to an in-process server in tests.
        _mcpTransport = mcpTransport ?? McpSession.DefaultTransport;
        // The cross-client skills folder, resolved once: %USERPROFILE%\.agents\skills in the app, a temp folder in tests.
        _externalSkills = externalSkills ?? SkillRoots.DefaultExternalDirectory();
        _geometry = geometry;
        _input = input;
        _clipboard = clipboard;
        _copyToClipboard = copyToClipboard;
        _clipboardImage = clipboardImage;
        _setTitle = setTitle;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _headlessInput = headlessInput ?? Console.In;
        _headlessOutput = headlessOutput ?? Console.Out;
        _smokeChecks = smokeChecks ?? (() => SmokeChecks.Run(AppContext.BaseDirectory, ModelsDirectory));
        _probe = probe ?? new LlmEndpointProbe(new HttpClient());
        _contextProbe = contextProbe ?? new ContextLengthProbe(new HttpClient());
        _chatClientFactory = chatClientFactory ?? ((endpoint, timeouts) => new OpenAICompatibleChatClient(endpoint, timeouts.Request));
        _playbackFactory = playbackFactory ?? (format => new WinMmAudioPlayback(format));
        _synthesizerFactory = synthesizerFactory ?? (request => request.Engine == TtsEngine.InProcess ? new KokoroInProcessSynthesizer(request.ModelPath!) : new KokoroHttpSynthesizer(request.Url!));
        _captureFactory = captureFactory ?? (format => new WinMmAudioCapture(format));
        _recognizerFactory = recognizerFactory ?? (path => new WhisperNetTranscriber(path));
        _vadFactory = vadFactory ?? ((path, options) => new SileroVad(path, options));
        _modelHttpClient = modelHttpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _inputDeviceCount = inputDeviceCount ?? WinMmAudioCapture.InputDeviceCount;
        _wakeDetectorFactory = wakeDetectorFactory ?? ((directory, phrase) => new VoskWakeWordDetector(directory, phrase));
        _time = time ?? TimeProvider.System;
        // The web tools' client and browser, one for the app: the screen and headless share the page cache.
        _web = web ?? WebAccess.Create(() => Web.NetworkMode.Resolve(EffectiveSettings), _time);
    }

    /// <summary>The models directory: <see cref="AppSettings.ModelsDirectory"/>, the folder <c>/about</c> names.</summary>
    public string ModelsDirectory => _settings.ModelsDirectory;

    /// <summary>Long-term memory lives in the loaded profile's directory (under the home, so <c>NEONSIDEKICK_HOME</c> moves it too). Headless builds one; the screen binds its own.</summary>
    private MemoryStore BuildMemoryStore() => new(_settings.ProfileDirectory);

    private PersonaFile BuildPersonaFile() => new(_settings.ProfileDirectory);

    private OperataFile BuildOperataFile() => new(_settings.ProfileDirectory);

    /// <summary>Read like the other two so the profile is bound the same way; headless never speaks, so the text never reaches the prompt.</summary>
    private VocaliaFile BuildVocaliaFile() => new(_settings.ProfileDirectory);

    /// <summary>The skills as headless takes them per turn (<see cref="ChatScreen.SkillsForTurn"/>): the catalog and the tools over the live roots, the project file over the sandbox.</summary>
    private ChatScreen.SkillsForTurn BuildSkills(WorkingDirectory files)
    {
        Func<SkillRoots> roots = () => SkillRoots.For(_settings, _externalSkills);
        var catalog = new SkillCatalog(roots);
        return new ChatScreen.SkillsForTurn(catalog, ChatScreen.SkillTools(catalog, roots, () => EffectiveSettings.AgentSkills && EffectiveSettings.ExternalSkills), new ProjectFile(() => files.Root), EffectiveSettings.AgentSkills, EffectiveSettings.AgentSkills && EffectiveSettings.ExternalSkills, EffectiveSettings.ProjectFile);
    }

    /// <summary>The file tools' sandbox over the live effective setting (flag &gt; saved) and the loaded profile's directory.</summary>
    public WorkingDirectory BuildWorkingDirectory() =>
        new(() => WorkingDirectory.Resolve(EffectiveSettings.WorkingDirectory, _settings.ProfileDirectory), _time);

    /// <summary>A voice session over this app's factories and models directory; the caller owns it. Never built in headless mode.</summary>
    private VoiceSession BuildVoiceSession() =>
        new(_captureFactory, _recognizerFactory, _vadFactory, new ModelStore(ModelsDirectory, _modelHttpClient), _inputDeviceCount, _wakeDetectorFactory);

    /// <summary>The assembly version as <c>major.minor.patch</c>.</summary>
    public static string Version
    {
        get
        {
            var v = typeof(SidekickApp).Assembly.GetName().Version;
            return v is null
                ? "0.0.0"
                : string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{v.Build}");
        }
    }

    /// <summary>What <c>--version</c> prints.</summary>
    public static string VersionLine => $"{Name} {Version}";

    /// <summary>
    /// The settings actually in force: flag > variable > saved setting > compiled default, as one
    /// expression so no code path can consult the file "first".
    /// </summary>
    public AppSettingsData EffectiveSettings => _options.ApplyTo(_environment.ApplyTo(_settings.Current));

    /// <summary>Runs the mode selected by <paramref name="options"/> and returns the process exit code.</summary>
    public async Task<int> RunAsync(SidekickOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        // The saved theme in force before anything is drawn (2026-09-23): the banner, the check
        // modes' output and the headless REPL wear it as the screen does.
        ThemeName.Apply(EffectiveSettings);

        if (options.Headless)
        {
            return await RunHeadlessAsync(cancellationToken).ConfigureAwait(false);
        }

        // The interactive screen starts on a wiped alternate buffer (the chat screen's own doing);
        // the diagnostic modes print a banner on the terminal as it is, because their output is
        // captured (build.ps1 reads --smoke) and an erase sequence there is noise.
        bool interactive = !options.AudioCheck && !options.VoiceCheck && !options.Smoke;
        if (interactive)
        {
            // The TUI owns stdout from the banner on: an Info line echoed there (the per-turn
            // "[Persona] Persona loaded …", for one) would land in the transcript. The chat
            // screen forwards Warning+ from its own subscription; --log keeps every line.
            bool previousEcho = DiagnosticLog.EchoToConsole;
            DiagnosticLog.EchoToConsole = false;
            try
            {
                LogStartup();
                EncryptSqlPasswords();
                // The screen wipes and draws the banner itself, inside the alternate buffer its
                // pane enters (RenderScreen(IAnsiConsole) through the pane), so the shell's screen is untouched.
                return await RunInteractiveAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DiagnosticLog.EchoToConsole = previousEcho;
            }
        }

        // The check modes keep the console echo, so the startup line prints ahead of the banner.
        LogStartup();
        RenderBanner();

        if (options.AudioCheck)
        {
            return await AudioCheck.RunAsync(_console, _playbackFactory(PcmFormat.Kokoro), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (options.VoiceCheck)
        {
            using var voice = BuildVoiceSession();
            return await VoiceCheck.RunAsync(_console, voice, EffectiveSettings, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return RunSmoke();
    }

    // ── Screens ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The start-of-app view: the screen wiped, then the banner. The only place that clears the
    /// console; the chat screen calls it at its start (inside the alternate buffer its pane
    /// entered), for <c>/clear</c> and for a profile switch.
    /// </summary>
    public void RenderScreen() => RenderScreen(_console);

    /// <summary>
    /// <see cref="RenderScreen()"/> on <paramref name="console"/>: the chat screen passes its pane,
    /// whose count of the rows the banner takes is what keeps the input row at the bottom.
    /// </summary>
    public void RenderScreen(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        ClearScreen(console);
        RenderBanner(console);
    }

    /// <summary>
    /// Wipes the terminal and homes the cursor. With <c>AnsiSupport.Detect</c> a redirected stdout
    /// selects Spectre's legacy backend, whose clear is <c>System.Console.Clear()</c> and throws
    /// with no console handle; that is the "no keyboard, exit 0" path, so the failure is swallowed
    /// (the same shape as the UTF-8 forcing in <c>Program.cs</c>).
    /// </summary>
    private static void ClearScreen(IAnsiConsole console)
    {
        try
        {
            console.Clear(home: true);
        }
        catch (IOException)
        {
            // Nothing to clear; the banner follows whatever is there.
        }
    }

    /// <summary>
    /// The gradient title with the version on the same line and, under <c>Show working directory</c>
    /// (2026-09-18), the working directory in force at the line's right edge (<see cref="BannerTitleMarkup"/>),
    /// then the one sunset rule and a blank line; the transcript starts under it. No status rows
    /// since 2026-09-14 (the user's call): the hint row's trailer names the model and the reasoning
    /// level, the window title the profile, <c>/settings</c> the URL and any override. The keys are
    /// the pane's hint row, not the banner's. The banner is a flow write, so the path is the one at
    /// the draw — startup, <c>/clear</c>, a profile switch, the splash dismissal —; a <c>/cwd</c>
    /// change shows at the next of those (the user's call over a rewrite in place).
    /// </summary>
    public void RenderBanner() => RenderBanner(_console);

    public void RenderBanner(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var effective = EffectiveSettings;
        string? directory = effective.ShowWorkingDirectory
            ? WorkingDirectory.Resolve(effective.WorkingDirectory, _settings.ProfileDirectory)
            : null;
        int width = Width();
        console.WriteLine();
        console.MarkupLine(BannerTitleMarkup(Version, directory, width));
        console.MarkupLine(Theme.Rule(width));
        console.WriteLine();
    }

    /// <summary>The banner's title, the two-space margin included.</summary>
    public const string BannerTitle = "  N E O N   S I D E K I C K";

    /// <summary>The cells kept blank between the version and the working directory on the title line.</summary>
    public const int BannerPathGap = 2;

    /// <summary>Under this many cells of room the working directory is left off the title line: a lone ellipsis says nothing.</summary>
    public const int BannerPathMinCells = 8;

    /// <summary>
    /// The banner's title line: <see cref="BannerTitle"/> in the gradient, <c>  v</c> and the version
    /// dim, and — with a <paramref name="workingDirectory"/> and the room for it — the directory dim
    /// at the right edge of a line <paramref name="width"/> cells wide, cut from the front to fit
    /// (<see cref="BannerPath"/>). Without one the line is the title and the version alone. Pure; pinned.
    /// </summary>
    public static string BannerTitleMarkup(string version, string? workingDirectory, int width)
    {
        ArgumentNullException.ThrowIfNull(version);
        string versionText = "  v" + version;
        string left = Theme.GradientMarkup(BannerTitle) + Theme.DimMarkup(versionText);
        int leftCells = TextCells.Width(BannerTitle) + TextCells.Width(versionText);
        string path = BannerPath(workingDirectory, width - leftCells - BannerPathGap);
        if (path.Length == 0)
        {
            return left;
        }

        return left + new string(' ', width - leftCells - TextCells.Width(path)) + Theme.DimMarkup(path);
    }

    /// <summary>
    /// What the title line shows of <paramref name="path"/> in <paramref name="room"/> cells: the path
    /// whole when it fits, its tail behind an ellipsis when not (<see cref="ScreenPane.FitTail"/>),
    /// nothing for no path or a room under <see cref="BannerPathMinCells"/>. Pure; pinned.
    /// </summary>
    public static string BannerPath(string? path, int room) =>
        string.IsNullOrWhiteSpace(path) || room < BannerPathMinCells ? "" : ScreenPane.FitTail(path, room);

    // ── Modes ───────────────────────────────────────────────────────────────

    /// <summary>Prints one PASS/FAIL line per check and returns 0 only if every check passed.</summary>
    private int RunSmoke()
    {
        IReadOnlyList<SmokeCheck> checks;
        try
        {
            checks = _smokeChecks();
        }
        catch (Exception ex)
        {
            checks = new[] { new SmokeCheck("smoke:run", false, $"{ex.GetType().Name}: {ex.Message}") };
        }

        int failed = 0;
        foreach (var check in checks)
        {
            if (!check.Passed)
            {
                failed++;
            }

            string verdict = check.Passed
                ? Theme.ColorMarkup(Theme.Good, "PASS")
                : Theme.ColorMarkup(Theme.Bad, "FAIL");
            _console.MarkupLine($"  {verdict}  {Markup.Escape(check.Name)}  {Theme.DimMarkup(check.Detail)}");
        }

        _console.WriteLine();
        string summary = failed == 0
            ? Theme.ColorMarkup(Theme.Good, $"SMOKE PASS  {checks.Count} checks")
            : Theme.ColorMarkup(Theme.Bad, $"SMOKE FAIL  {failed} of {checks.Count} checks failed");
        _console.MarkupLine(summary);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// A stdin/stdout REPL with no TUI. stdout <em>is</em> the interface here, so these are the
    /// only sanctioned direct console writes in the codebase. A null line (closed stdin) is the
    /// correct exit for a piped run. Diagnostics of Warning and above are forwarded to the same
    /// writer on their own lines; the raw echo is off while this mode owns stdout.
    /// </summary>
    private async Task<int> RunHeadlessAsync(CancellationToken cancellationToken)
    {
        bool previousEcho = DiagnosticLog.EchoToConsole;
        DiagnosticLog.EchoToConsole = false;
        Action<DiagnosticEvent> forward = OnHeadlessDiagnostic;
        DiagnosticLog.Emitted += forward;
        LogStartup();
        EncryptSqlPasswords();
        using var session = new LlmSession(_probe, _contextProbe, _chatClientFactory, _time);
        // The MCP servers (2026-09-20): connected after the LLM, their tools offered per turn like the screen's; disposed after the loop.
        await using var mcp = new McpSession(_settings, _mcpTransport, _time);
        var memory = BuildMemoryStore();
        var memoryTools = ChatScreen.MemoryTools(memory);
        // No timers headless: nothing could deliver the alert (the same rule as speech and voice); PrepareTurn drops the prompt's timer sentence with them (2026-09-20).
        var clockTools = ChatScreen.ClockTools(_time);
        // The file tools need no console, so headless has them; the editor opener is the real one.
        // The setting File tools decides per turn, like Web tools; the clock alone is standing.
        var files = BuildWorkingDirectory();
        var fileTools = ChatScreen.FileTools(files, () => WorkingDirectory.IsDefault(EffectiveSettings.WorkingDirectory), PersonaFile.OpenInEditor, () => EffectiveSettings);
        var standingTools = clockTools;
        // The skills need no console either (2026-09-16); the two settings decide per turn.
        var skills = BuildSkills(files);
        // The web tools need no console either; the setting Web tools decides per turn.
        var webTools = ChatScreen.WebTools(_web, files, () => EffectiveSettings);
        var git = new Git.GitAccess(files, _time);
        var gitTools = ChatScreen.GitTools(git, () => EffectiveSettings);
        var vaultTools = ChatScreen.ObsidianTools(new Obsidian.ObsidianVault(() => EffectiveSettings.ObsidianVault, _time), () => EffectiveSettings);
        var sql = new Sql.SqlAccess(() => Sql.SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(EffectiveSettings.SqlConnectionsOffered));
        var sqlTools = ChatScreen.SqlTools(sql, () => EffectiveSettings);
        // The image tools (2026-09-24): no console needed, so headless has them too.
        using var comfy = new Comfy.ComfyStudio(ChatScreen.ComfyCatalog(_settings), files, () => EffectiveSettings, _comfyClient);
        var comfyTools = ChatScreen.ComfyTools(comfy, files, () => _settings.ProfileSplashDirectory);
        // The shell tools (2026-09-21): headless has no pane to ask on, so the gate has no asker — under ask the
        // allow list alone decides, and NEONSIDEKICK_COMMAND_POLICY=yolo is how a scripted run says yes.
        var interpreters = new Shell.Interpreters(_environment.System);
        var allowList = new Shell.CommandAllowList(() => EffectiveSettings.ShellCommandAllowed, allowed => _settings.Update(d => d.ShellCommandAllowed = [.. allowed]));
        var runner = new Shell.ShellRunner(_time);
        // The background processes (2026-09-21): nothing to signal headless (the next line is read when it is read); the exits print as notices at the loop top and ride the next turn as seeded polls.
        using var processes = new Shell.ProcessRegistry(runner, Random.Shared, () => { });
        var shellTools = ChatScreen.ShellTools(runner, processes, files, new Shell.CommandGate(() => EffectiveSettings, allowList, null), interpreters, () => EffectiveSettings, Random.Shared, () => session.Assistant?.Tools ?? []);
        var persona = BuildPersonaFile();
        var operata = BuildOperataFile();
        var vocalia = BuildVocaliaFile();
        // The session store (2026-09-18): headless logs its turns and offers the tool like the
        // screen; no /sessions, no restore, the first line always the title.
        using var sessions = new Sessions.SessionStore(_settings.ProfileDirectory, _time);
        long? sessionId = null;
        var sessionTools = ChatScreen.SessionTools(sessions, () => EffectiveSettings, () => sessionId, _time);
        try
        {
            await HeadlessLineAsync(VersionLine).ConfigureAwait(false);
            await HeadlessLineAsync(HeadlessHint).ConfigureAwait(false);

            await session.ConnectAsync(EffectiveSettings, cancellationToken).ConfigureAwait(false);
            await HeadlessLineAsync(session.Endpoint is null ? HeadlessNoServerLine(LlmScanMode.Resolve(EffectiveSettings)) : LlmSession.ConnectedLine(session.Endpoint)).ConfigureAwait(false);
            try
            {
                await mcp.ConnectAllAsync(EffectiveSettings, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The app token during the wave (or before it): the loop below ends at once, the servers marked cancelled.
            }

            if (mcp.StatusLine() is { } mcpLine)
            {
                await HeadlessLineAsync(mcpLine).ConfigureAwait(false);
            }

            foreach (string warning in mcp.WarningLines())
            {
                await HeadlessLineAsync(warning).ConfigureAwait(false);
            }

            var assistant = session.Assistant;

            while (!cancellationToken.IsCancellationRequested)
            {
                while (processes.TryTakeAlert(out var alert))
                {
                    await HeadlessNoticeLineAsync("[notice] " + Shell.ShellText.AlertLine(alert)).ConfigureAwait(false);
                }

                await _headlessOutput.WriteAsync("You: ").ConfigureAwait(false);
                await _headlessOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                _headlessAtLineStart = false;

                string? line;
                try
                {
                    line = await _headlessInput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Ctrl+C. The line was never going to be answered.
                    DiagnosticLog.Info(ChatScreen.AppCategory, HeadlessExitLogLine(ChatScreen.ExitByAppToken));
                    break;
                }
                // A real console echoed the user's Enter; a pipe did not, and that is the caller's
                // rendering problem, not ours.
                _headlessAtLineStart = true;

                if (line is null || line.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase))
                {
                    DiagnosticLog.Info(ChatScreen.AppCategory, HeadlessExitLogLine(line is null ? ChatScreen.ExitByEndOfInput : ChatScreen.ExitByCommand));
                    break;
                }

                string text = line.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                // /clear, /new and /splash are one act here — there is no screen to keep, wipe or draw a picture on — under their own lines.
                bool isClear = text.Equals("/clear", StringComparison.OrdinalIgnoreCase) || text.Equals("/splash", StringComparison.OrdinalIgnoreCase);
                if (isClear || text.Equals("/new", StringComparison.OrdinalIgnoreCase))
                {
                    assistant?.History.Clear();
                    session.Usage.ResetConversation();
                    sessionId = null;
                    await HeadlessLineAsync(HeadlessReplyPrefix + (isClear ? "(conversation cleared)" : ChatScreen.NewConversationNotice)).ConfigureAwait(false);
                    continue;
                }

                if (assistant is null)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + HeadlessNoAssistantReply).ConfigureAwait(false);
                    continue;
                }

                var (command, args) = SlashCommands.Parse(text);
                if (command == SlashCommand.Compact)
                {
                    var (outcome, details) = await CompactHeadlessAsync(session, assistant, args.Length > 0 ? args : null, autoPercent: null, cancellationToken).ConfigureAwait(false);
                    await HeadlessLineAsync(HeadlessReplyPrefix + outcome).ConfigureAwait(false);
                    foreach (string detail in details)
                    {
                        await HeadlessNoticeLineAsync("[notice] " + detail).ConfigureAwait(false);
                    }

                    continue;
                }

                // As the screen does before a message: the last reply's context past the LLM auto compact (%) share compacts first.
                int share = EffectiveSettings.LlmAutoCompactPercent;
                if (ConversationCompactor.ShouldAutoCompact(session.Usage.LastRequest, session.ContextLength, share))
                {
                    int percent = UsageText.Percent(session.Usage.LastRequest.Total, session.ContextLength) ?? share;
                    var (outcome, details) = await CompactHeadlessAsync(session, assistant, null, percent, cancellationToken).ConfigureAwait(false);
                    if (outcome != CompactionText.NothingToCompact)
                    {
                        await HeadlessNoticeLineAsync("[notice] " + outcome).ConfigureAwait(false);
                        foreach (string detail in details)
                        {
                            await HeadlessNoticeLineAsync("[notice] " + detail).ConfigureAwait(false);
                        }
                    }
                }

                // Per turn, as the screen does: a memory saved in this turn is in the next one's prompt.
                ChatScreen.PrepareTurn(assistant, memory, memoryTools, standingTools, persona, operata, vocalia, EffectiveSettings.Memory, speechOutput: false, EffectiveSettings.LlmMaxToolIterations, EffectiveSettings.LlmOfferTools, webTools, EffectiveSettings.WebTools, ChatScreen.ContextGuardFor(EffectiveSettings, session.ContextLength), fileTools, EffectiveSettings.FileTools, skills: skills with { Enabled = EffectiveSettings.AgentSkills, External = EffectiveSettings.AgentSkills && EffectiveSettings.ExternalSkills }, sessionTools: sessionTools, sessionsEnabled: EffectiveSettings.SessionTool, disabledTools: ToolsText.DisabledSet(EffectiveSettings.ToolsDisabled), mcpTools: mcp.Tools, mcpEnabled: EffectiveSettings.McpServers, safeEdits: EffectiveSettings.FileSafeEdits, gitTools: gitTools, gitEnabled: EffectiveSettings.GitNativeTools, shellTools: shellTools, shellEnabled: ChatScreen.ShellOffered(EffectiveSettings), processes: processes, shellBridge: EffectiveSettings.ShellToolBridge, shellPolice: EffectiveSettings.ShellPoliceOutsidePaths, obsidianTools: ChatScreen.ObsidianToolsFor(vaultTools, EffectiveSettings), obsidianEnabled: ChatScreen.ObsidianOffered(EffectiveSettings), sqlTools: sqlTools, sqlEnabled: ChatScreen.SqlOffered(EffectiveSettings, sql), comfyTools: comfyTools, comfyEnabled: ChatScreen.ComfyOffered(EffectiveSettings, comfy));
                var turn = await RunHeadlessTurnAsync(session, assistant, text, cancellationToken).ConfigureAwait(false);
                if (EffectiveSettings.SessionLogging)
                {
                    sessionId ??= sessions.Begin(Sessions.SessionText.FirstLineTitle(text), session.Endpoint?.ModelId ?? "");
                    if (sessionId is { } id)
                    {
                        var usage = session.Usage.LastRequest;
                        sessions.AppendTurn(id, text, turn.Reply, turn.Trace.ToolCalls, turn.Trace.ToolNames, turn.Trace.LoadedSkills, turn.Trace.Errors, usage.Input, usage.Output, turn.Cancelled);
                        sessions.SaveHistory(id, Sessions.SessionHistory.ToJson(assistant.History.Messages));
                    }
                }
            }

            await EnsureHeadlessLineStartAsync().ConfigureAwait(false);
            await _headlessOutput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            // The MCP servers go while the log still forwards through the headless writer: a
            // `Stopped docker` line after the echo came back would land on stdout on its own.
            await mcp.DisposeAsync().ConfigureAwait(false);
            DiagnosticLog.Emitted -= forward;
            DiagnosticLog.EchoToConsole = previousEcho;
        }
    }

    /// <summary>
    /// <c>/compact</c> headless, and the automatic one: the same <see cref="ConversationCompactor"/>
    /// as the screen, no spinner, Ctrl+C the only cancel; the outcome as one line of
    /// <see cref="CompactionText"/>, the failure line included, and — under <c>LLM compact show
    /// summary</c> (2026-09-21) — the detail lines the screen prints under its notice
    /// (<see cref="CompactionText.DetailLines"/>), empty otherwise.
    /// </summary>
    private async Task<(string Outcome, IReadOnlyList<string> Details)> CompactHeadlessAsync(LlmSession session, Assistant assistant, string? focus, int? autoPercent, CancellationToken cancellationToken)
    {
        string outcome;
        IReadOnlyList<string> details = [];
        try
        {
            var effective = EffectiveSettings;
            var result = await ConversationCompactor.RunAsync(assistant, session.Usage, CompactType.Resolve(effective), effective.LlmCompactKeepRecent, focus, cancellationToken, pruneRecent: autoPercent is not null, protectSkills: SkillCompactMode.Resolve(effective)).ConfigureAwait(false);
            outcome = result is null ? CompactionText.NothingToCompact : CompactionText.Notice(result, autoPercent);
            if (result is not null && effective.LlmCompactShowSummary)
            {
                details = CompactionText.DetailLines(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = CompactionText.Cancelled;
        }
        catch (Exception ex)
        {
            outcome = CompactionText.FailedPrefix + assistant.ExplainFailure(ex);
        }

        // The same line the screen logs for its compacts (2026-09-19).
        DiagnosticLog.Info("Llm", outcome);
        return (outcome, details);
    }

    /// <summary>
    /// One turn on the headless writer: <c>Neon: </c> then every delta as it arrives (flushed, so
    /// a pipe sees tokens live), tool activity and notices on their own bracketed lines, and a
    /// newline at the end. Cancellation ends the line with <c>(cancelled)</c>. The opening calls'
    /// <c>[tool]</c> lines (the first turn of a conversation) come <em>before</em> the prefix,
    /// so <c>Neon: </c> and the reply stay on one line for a script that splits on it.
    /// </summary>
    /// <summary>What the session store keeps of a headless turn: the streamed reply, the trace (the model's own tool calls, their names, the skills loaded, the errors), whether Ctrl+C cut it.</summary>
    private readonly record struct HeadlessTurn(string Reply, TurnTrace Trace, bool Cancelled);

    private async Task<HeadlessTurn> RunHeadlessTurnAsync(LlmSession session, Assistant assistant, string text, CancellationToken cancellationToken)
    {
        await using var events = assistant.RunTurnAsync(text, cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool prefixed = false;
        var reply = new StringBuilder();
        var trace = new TurnTrace();
        bool cancelled = false;

        try
        {
            while (await events.MoveNextAsync().ConfigureAwait(false))
            {
                if (!prefixed && !Assistant.IsOpeningEvent(events.Current))
                {
                    await WriteHeadlessPrefixAsync().ConfigureAwait(false);
                    prefixed = true;
                }

                trace.Observe(events.Current);
                switch (events.Current)
                {
                    case TurnEvent.TextDelta delta:
                        reply.Append(delta.Text);
                        await _headlessOutput.WriteAsync(delta.Text).ConfigureAwait(false);
                        await _headlessOutput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                        _headlessAtLineStart = delta.Text.EndsWith('\n');
                        break;
                    case TurnEvent.ToolCall call:
                        await HeadlessNoticeLineAsync($"[tool] {call.Name} {call.ArgumentsJson}").ConfigureAwait(false);
                        break;
                    case TurnEvent.ToolResult result:
                        await HeadlessNoticeLineAsync($"[tool] {result.Name} -> {result.Text}").ConfigureAwait(false);
                        break;
                    case TurnEvent.Notice notice:
                        await HeadlessNoticeLineAsync((notice.IsError ? "[error] " : "[notice] ") + notice.Text).ConfigureAwait(false);
                        break;
                    case TurnEvent.Usage usage:
                        // Tallied for the automatic compact's share; nothing is printed.
                        session.Usage.Add(usage.Tokens);
                        break;
                }
            }

            if (!prefixed)
            {
                await WriteHeadlessPrefixAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            DiagnosticLog.Info(Assistant.TurnCategory, ChatScreen.TurnEndedByAppTokenLog);
            if (!prefixed)
            {
                await WriteHeadlessPrefixAsync().ConfigureAwait(false);
            }

            await HeadlessNoticeLineAsync("(cancelled)").ConfigureAwait(false);
        }

        await EnsureHeadlessLineStartAsync().ConfigureAwait(false);
        await _headlessOutput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return new HeadlessTurn(reply.ToString(), trace, cancelled);
    }

    private async Task WriteHeadlessPrefixAsync()
    {
        await _headlessOutput.WriteAsync(HeadlessReplyPrefix).ConfigureAwait(false);
        _headlessAtLineStart = false;
    }

    private async Task HeadlessLineAsync(string line)
    {
        await _headlessOutput.WriteLineAsync(line).ConfigureAwait(false);
        _headlessAtLineStart = true;
    }

    private async Task HeadlessNoticeLineAsync(string line)
    {
        await EnsureHeadlessLineStartAsync().ConfigureAwait(false);
        await HeadlessLineAsync(line).ConfigureAwait(false);
    }

    private async Task EnsureHeadlessLineStartAsync()
    {
        if (!_headlessAtLineStart)
        {
            await _headlessOutput.WriteLineAsync().ConfigureAwait(false);
            _headlessAtLineStart = true;
        }
    }

    /// <summary>
    /// Warnings and errors reach the headless writer as <c>[category] message</c> lines; anything
    /// quieter is dropped. Synchronous because <see cref="DiagnosticLog"/> calls subscribers
    /// inline; the turn loop awaits between writes, so the two never interleave mid-line.
    /// </summary>
    private void OnHeadlessDiagnostic(DiagnosticEvent evt)
    {
        if (evt.Level < DiagnosticLevel.Warning)
        {
            return;
        }

        if (!_headlessAtLineStart)
        {
            _headlessOutput.WriteLine();
        }

        _headlessOutput.WriteLine($"[{evt.Category}] {evt.Message}");
        _headlessOutput.Flush();
        _headlessAtLineStart = true;
    }

    /// <summary>
    /// The interactive screen: the chat loop in <see cref="ChatScreen"/>, over one
    /// <see cref="LlmSession"/>, one <see cref="SpeechSession"/> and one <see cref="VoiceSession"/>
    /// that live as long as the screen. Headless never constructs speech or voice: stdout is its
    /// whole interface.
    /// </summary>
    private async Task<int> RunInteractiveAsync(CancellationToken cancellationToken)
    {
        using var session = new LlmSession(_probe, _contextProbe, _chatClientFactory, _time);
        using var speech = new SpeechSession(_synthesizerFactory, _playbackFactory, new ModelStore(ModelsDirectory, _modelHttpClient));
        using var voice = BuildVoiceSession();
        // The MCP servers' session (2026-09-20), disposed after the screen: its stdio children end once the alternate buffer is left.
        await using var mcp = new McpSession(_settings, _mcpTransport, _time);
        // The interactive keys come from the injected input when there is one (the real console's
        // own buffer, mouse included), else from the console's Input (a TestConsole's, or Spectre's).
        // The real console's input also owns the mouse: the input line takes it while a draft is
        // on the row and hands it back to the terminal otherwise, so the terminal's own selection
        // and right-click copy work whenever there is nothing to click into.
        var mouse = _input as WindowsConsoleInput;
        var screen = new ChatScreen(_console, _settings, () => EffectiveSettings, OverriddenBy, session, speech, new KeySource(_input ?? _console.Input), voice, PersonaFile.OpenInEditor, RenderScreen, _time, _geometry, _clipboard, mouse is null ? null : mouse.Capture, _copyToClipboard, clipboardImage: _clipboardImage, web: _web, setTitle: _setTitle, externalSkills: _externalSkills, holdWheel: mouse is null ? null : mouse.HoldWheel, splash: SplashImages.Source, editDraft: PersonaFile.EditAndWaitAsync, mcp: mcp, environment: _environment.System, logFile: _options.LogPath is { } logPath ? Path.GetFullPath(logPath) : null, comfyClient: _comfyClient, openImage: PersonaFile.OpenImage);
        if (mouse is not null)
        {
            mouse.ModeChanged = screen.FlushConsole;
        }

        try
        {
            return await screen.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (mouse is not null)
            {
                mouse.ModeChanged = null;
            }
        }
    }

    /// <summary>
    /// The flag or variable that outranks the saved value of <paramref name="field"/> this launch,
    /// or null. Flags beat variables, so a flag is named first.
    /// </summary>
    /// <summary>The headless loop's last line in the log: <c>Headless exit: end of input</c>. Pinned.</summary>
    public static string HeadlessExitLogLine(string reason) => "Headless exit: " + reason;

    /// <summary>The category of the one startup block in the log.</summary>
    public const string StartupCategory = "Startup";

    /// <summary>
    /// The run's opening lines in the log, once the mode is known and (interactive, headless) the
    /// console echo is off: the process facts and this launch's flags (<see cref="StartupLogLine"/>),
    /// the environment variables in force (<see cref="EnvironmentOverrides.Describe"/>, under
    /// <c>Environment</c>) and the settings that are not the defaults
    /// (<see cref="AppSettings.NotDefaultLogLine"/>, Debug under <c>Settings</c> — the effective
    /// snapshot, so a flag's or a variable's value shows as the setting it became).
    /// </summary>
    /// <summary>
    /// A plain password typed into any <c>sql.json</c> — the home's or any profile's — encrypted before the first
    /// screen or turn (later on 2026-09-23, the user's ask), whatever the SQL tools' switch says: a secret on disk
    /// is the thing to fix, not only a secret about to be used. The interactive and headless modes only.
    /// </summary>
    private void EncryptSqlPasswords() => Sql.SqlConfigFile.EncryptAll(_settings.StorageDirectory);

    private void LogStartup()
    {
        var facts = AboutFacts.Runtime(Version, _settings.StorageDirectory, _settings.ProfileDirectory, ModelsDirectory);
        DiagnosticLog.Info(StartupCategory, StartupLogLine(facts, _options, _settings.ProfileName, _console.Profile.Width, _console.Profile.Height));
        if (_environment.Describe() is { } overrides)
        {
            DiagnosticLog.Info(EnvironmentOverrides.Category, EnvironmentLogLine(overrides));
        }

        DiagnosticLog.Debug(AppSettings.Category, AppSettings.NotDefaultLogLine(SettingsDiff.NotDefault(EffectiveSettings)));
    }

    /// <summary>
    /// <c>NeonSidekick 0.2.0 (native, .NET 10.0.0, x64, Microsoft Windows 10.0.26200), mode interactive,
    /// flags --cwd D:\x, home C:\…\.neonsidekick, profile default, console 240×60</c>; without a value
    /// flag the <c>flags</c> part reads <c>no flags</c>.
    /// </summary>
    public static string StartupLogLine(AboutFacts facts, SidekickOptions options, string profile, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        string flags = options.Describe() is { } given ? "flags " + given : "no flags";
        return $"{Name} {facts.Version} ({(facts.NativeAot ? "native" : "JIT")}, {facts.Framework}, {facts.Architecture}, {facts.Os}), mode {options.Mode}, {flags}, home {facts.HomeDirectory}, profile {profile}, console {width}×{height}";
    }

    /// <summary>The variables in force: <c>Overrides in force: NEONSIDEKICK_LLM_URL=…</c>.</summary>
    public static string EnvironmentLogLine(string overrides) => "Overrides in force: " + overrides;

    public string? OverriddenBy(SettingsField field) => field switch
    {
        SettingsField.LlmUrl => _options.Url is not null ? SidekickOptions.UrlFlag : _environment.LlmUrl is not null ? EnvironmentOverrides.LlmUrlVariable : null,
        SettingsField.LlmModel => _options.Model is not null ? SidekickOptions.ModelFlag : _environment.LlmModel is not null ? EnvironmentOverrides.LlmModelVariable : null,
        SettingsField.LlmApiKey => _environment.LlmApiKey is not null ? EnvironmentOverrides.LlmApiKeyVariable : null,
        SettingsField.LlmRequestTimeoutSeconds => _environment.LlmRequestTimeoutSeconds is not null ? EnvironmentOverrides.RequestTimeoutVariable : null,
        SettingsField.LlmTurnTimeoutSeconds => _environment.LlmTurnTimeoutSeconds is not null ? EnvironmentOverrides.TurnTimeoutVariable : null,
        SettingsField.LlmContextLength => _environment.LlmContextLength is not null ? EnvironmentOverrides.LlmContextVariable : null,
        SettingsField.TtsHttpUrl => _environment.TtsHttpUrl is not null ? EnvironmentOverrides.TtsUrlVariable : null,
        SettingsField.TtsVoice => _environment.TtsVoice is not null ? EnvironmentOverrides.TtsVoiceVariable : null,
        SettingsField.TtsSpeed => _environment.TtsSpeed is not null ? EnvironmentOverrides.TtsSpeedVariable : null,
        SettingsField.SttInterruptEchoGuard => _environment.SttInterruptEchoGuard is not null ? EnvironmentOverrides.InterruptEchoVariable : null,
        SettingsField.SttInterruptConfirmMs => _environment.SttInterruptConfirmMs is not null ? EnvironmentOverrides.InterruptConfirmVariable : null,
        SettingsField.TtsVoice2 => _environment.TtsVoice2 is not null ? EnvironmentOverrides.TtsVoice2Variable : null,
        SettingsField.TtsVoiceMix => _environment.TtsVoiceMix is not null ? EnvironmentOverrides.TtsMixVariable : null,
        SettingsField.SttWhisperModel => _environment.SttWhisperModel is not null ? EnvironmentOverrides.WhisperModelVariable : null,
        SettingsField.LlmReasoning => _environment.LlmReasoning is not null ? EnvironmentOverrides.LlmReasoningVariable : null,
        SettingsField.WorkingDirectory => _options.WorkingDirectory is not null ? SidekickOptions.CwdFlag : null,
        SettingsField.WebSearxngUrl => _environment.WebSearxngUrl is not null ? EnvironmentOverrides.SearxngUrlVariable : null,
        SettingsField.ShellCommandPolicy => _environment.ShellCommandPolicy is not null ? EnvironmentOverrides.CommandPolicyVariable : null,
        SettingsField.ObsidianVault => _environment.ObsidianVault is not null ? EnvironmentOverrides.ObsidianVaultVariable : null,
        SettingsField.ComfyUrl => _environment.ComfyUrl is not null ? EnvironmentOverrides.ComfyUrlVariable : null,
        _ => null,
    };

    /// <summary>The banner rule's width: <see cref="TranscriptRenderer.RuleWidth"/>, shared with the transcript's own rule (<c>/new</c>).</summary>
    private int Width() => TranscriptRenderer.RuleWidth(_console.Profile.Width);
}
