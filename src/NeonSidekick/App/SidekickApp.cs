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
    public const string HeadlessHint = "Headless mode. Type a message; /clear or /new forgets the conversation; /compact [focus] shrinks it; /plan <requirement> plans before doing (/plan approve [--fresh] | cancel | show | save [name] | open [name]); /skills add <source> [--global] [--yes] installs a skill; /claude <message> asks Claude Code; /rewind [n] goes back n messages; /exit or EOF exits.";

    /// <summary>Printed once when discovery under <paramref name="scope"/> found nothing. Pinned by tests; shared with the chat screen.</summary>
    public static string HeadlessNoServerLine(ScanScope scope) => LlmSession.HeadlessNoServerLine(scope);

    /// <summary>The reply to every message when there is no assistant. Pinned by tests.</summary>
    public static readonly string HeadlessNoAssistantReply =
        $"[error] No LLM endpoint. Set {EnvironmentOverrides.LlmUrlVariable} and restart.";

    /// <summary>The last reply in <paramref name="messages"/> (headless <c>/print reply</c>, 2026-09-28): the newest assistant message with text, or null.</summary>
    public static string? LastReplyText(IEnumerable<ChatMessage>? messages) =>
        messages?.LastOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))?.Text;

    private const string Category = "App";
    private const string HeadlessReplyPrefix = "Neon: ";

    /// <summary>
    /// The exit code of a headless run in which a shell command was refused (2026-09-26, the user's call): not
    /// approved under <c>ask</c> with nothing to ask, or stopped by the path police. The run still reads every line;
    /// the code comes at the end, after a <see cref="Shell.ShellText.RefusedSummary"/> notice, so a script can tell
    /// "a command did not run" from success (0) and from a bad argument or unknown profile (2).
    /// </summary>
    public const int HeadlessRefusedExitCode = 3;

    private readonly IAnsiConsole _console;
    private readonly AppSettings _settings;
    private readonly EnvironmentOverrides _environment;
    private readonly TextReader _headlessInput;
    private readonly TextWriter _headlessOutput;
    private readonly Func<IReadOnlyList<SmokeCheck>> _smokeChecks;
    private readonly LlmEndpointProbe _probe;
    private readonly ContextLengthProbe _contextProbe;
    private readonly ServerSamplingProbe _samplingProbe;
    private readonly Func<LlmEndpoint, LlmTimeouts, IChatClient> _chatClientFactory;
    private readonly Func<PcmFormat, IAudioPlayback> _playbackFactory;
    private readonly Func<SynthesizerRequest, ISpeechSynthesizer> _synthesizerFactory;
    private readonly Func<PcmFormat, IAudioCapture> _captureFactory;
    private readonly Func<string, ISpeechRecognizer> _recognizerFactory;
    private readonly Func<string, VadOptions, IVoiceActivityDetector> _vadFactory;
    private readonly HttpClient _modelHttpClient;
    private readonly Func<EmbeddedLlm.IEmbeddedLlm>? _embeddedLlm;
    private readonly Func<Claude.IClaudeServerHost> _claudeServerFactory;

    // The Claude CLI server's process for this run (2026-09-30): made at the run's start, disposed at its end; the chat
    // clients built over the Claude CLI endpoint all drive this one.
    private Claude.IClaudeServerHost? _claudeServer;
    private readonly WebAccess _web;
    private readonly Func<Mcp.McpServerConfig, string, ModelContextProtocol.Client.IClientTransport> _mcpTransport;
    private readonly Func<Uri, Comfy.ComfyClient>? _comfyClient;
    private readonly Func<Uri, string, HomeAssistant.HaClient>? _haClient;
    private readonly Func<string, Docker.DockerClient>? _dockerClient;
    private readonly Camera.ICameraSystem? _camera;
    private readonly Func<string, Action, Viewer.ILiveView>? _liveView;
    private readonly Action<string>? _showShot;

    /// <summary><c>/log</c>'s window (2026-10-02, <c>Viewer.LogWindow.Show</c> over the run's buffer), or null where there is none.</summary>
    private readonly Action? _openLogWindow;

    /// <summary>The log window and the picture viewer closed, true when one was open (later on 2026-10-02, Ctrl+Alt+G and U close what they opened), or null where there is none.</summary>
    private readonly Func<bool>? _closeLogWindow;
    private readonly Func<bool>? _closeViewer;
    private readonly Func<Docker.IDockerServers>? _dockerServers;

    // The run's session while one is live (2026-10-02): what ConsoleClosing stops the container through, from the console's control thread.
    private volatile LlmSession? _liveSession;
    private readonly Printing.IPrintSpooler _printSpooler;
    private readonly Func<Perf.IPerfSource>? _perfSource;
    private readonly UI.IFrameHold? _frames;
    private readonly Claude.IClaudeCli? _claude;
    private readonly Action<string>? _openViewer;
    private readonly Action<string>? _viewPicture;
    private readonly Action<string>? _followViewer;

    // The interactive screen while it runs (2026-09-28): the viewer's keys reach its strip through ViewerBrowsed.
    private volatile ChatScreen? _screen;
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
    /// <param name="frames">Where the screen's frames are held and let go as one write (2026-09-29, the rows' flicker at a turn's end): <c>Program.cs</c> passes the <see cref="UI.FrameWriter"/> it made stdout; null (tests) holds nothing.</param>
    /// <param name="embeddedLlm">Makes the embedded model's service for one run (2026-09-29): <c>Program.cs</c> passes <see cref="EmbeddedLlm.EmbeddedLlmService.Create"/> on Windows x64, tests a fake; null offers no embedded model.</param>
    /// <param name="claudeServer">Makes the Claude CLI server's process host for one run (2026-09-30): null makes the app's own (<see cref="Claude.ClaudeServerHost"/>, which starts nothing until a turn asks), tests a fake.</param>
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
        Func<Uri, Comfy.ComfyClient>? comfyClient = null,
        Claude.IClaudeCli? claude = null,
        Action<string>? openViewer = null,
        Action<string>? viewPicture = null,
        ServerSamplingProbe? samplingProbe = null,
        Func<Uri, string, HomeAssistant.HaClient>? haClient = null,
        Action<string>? followViewer = null,
        Printing.IPrintSpooler? printSpooler = null,
        Func<EmbeddedLlm.IEmbeddedLlm>? embeddedLlm = null,
        Func<Perf.IPerfSource>? perfSource = null,
        UI.IFrameHold? frames = null,
        Func<Claude.IClaudeServerHost>? claudeServer = null,
        Func<string, Docker.DockerClient>? dockerClient = null,
        Func<Docker.IDockerServers>? dockerServers = null,
        Camera.ICameraSystem? camera = null,
        Func<string, Action, Viewer.ILiveView>? liveView = null,
        Action<string>? showShot = null,
        Action? openLogWindow = null,
        Func<bool>? closeLogWindow = null,
        Func<bool>? closeViewer = null)
    {
        // The camera (2026-10-02): Media Foundation in the app on Windows, a fake in tests, none elsewhere; its previews in the
        // picture viewer (live, and a shot opened without the keyboard), none in tests.
        _camera = camera;
        _liveView = liveView;
        _showShot = showShot;
        _openLogWindow = openLogWindow;
        _closeLogWindow = closeLogWindow;
        _closeViewer = closeViewer;
        // The Claude CLI server (2026-09-30): a host over the real CLI, with this executable as its MCP relay, unless a test gives its own.
        _claudeServerFactory = claudeServer ?? (() => new Claude.ClaudeServerHost(Claude.ClaudeServerHost.OwnRelayCommand));
        _frames = frames;
        // The performance bar's readings (2026-09-29): kernel32, NVML or PDH/DXGI in the app on Windows; none in tests.
        _perfSource = perfSource;
        // The embedded model (2026-09-29): llama-server under the app's own job in the app, a fake in tests, none by default.
        _embeddedLlm = embeddedLlm;
        // The printers (2026-09-28): winspool and GDI in the app on Windows; none in tests, so nothing reaches a real printer.
        _printSpooler = printSpooler ?? Printing.NullPrintSpooler.Instance;
        // The /sampling pane's server defaults (2026-09-28): a real HttpClient in the app, like the context probe; tests pass one over a stub.
        _samplingProbe = samplingProbe ?? new ServerSamplingProbe(new HttpClient());
        // The picture viewer (2026-09-27): PictureWindow.Open in the app on Windows, null in tests and elsewhere.
        _openViewer = openViewer;
        // A double-clicked picture in that viewer (later on 2026-09-27): PictureWindow.OpenAt in the app on Windows, null in tests and elsewhere.
        _viewPicture = viewPicture;
        // The strip's arrows or a click on a tile moving an open viewer (2026-09-28): PictureWindow.Follow in the app on Windows, null in tests and elsewhere.
        _followViewer = followViewer;
        // The ComfyUI client (2026-09-24): over its own transport in the app, a stub handler in tests.
        _comfyClient = comfyClient;
        // The Home Assistant client (2026-09-28): over its own transport in the app, a stub handler in tests.
        _haClient = haClient;
        // The Docker engine's client (2026-10-02): over the engine's named pipe in the app, a stub handler in tests.
        _dockerClient = dockerClient;
        // The chosen Docker containers' switcher (2026-10-02): over the engine's pipe in the app, a fake in the session tests.
        _dockerServers = dockerServers;
        // Claude Code headless for /claude (2026-09-27): the real CLI when null, a fake in tests.
        _claude = claude;
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
        _chatClientFactory = chatClientFactory ?? DefaultChatClient;
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

    /// <summary>
    /// Where the Claude CLI server runs (2026-09-30): <c>&lt;home&gt;/claude-cli</c>, one folder for every profile. It has no
    /// file tools, so nothing reads the folder; the CLI keeps its per-folder state (its sessions) under its name.
    /// </summary>
    public string ClaudeCliDirectory => Path.Combine(_settings.StorageDirectory, "claude-cli");

    /// <summary>Whether the Claude CLI is offered for <paramref name="effective"/>: the switch on and the CLI found (<see cref="Claude.ClaudeCliEndpoint.Offered"/>).</summary>
    public bool ClaudeCliOffered(AppSettingsData effective) => Claude.ClaudeCliEndpoint.Offered(effective, _environment.System);

    /// <summary>Long-term memory lives in the loaded profile's directory (under the home, so <c>NEONSIDEKICK_HOME</c> moves it too). Headless builds one; the screen binds its own.</summary>
    private MemoryStore BuildMemoryStore() => new(_settings.ProfileDirectory);

    private PersonaFile BuildPersonaFile() => new(_settings.ProfileDirectory);

    private OperataFile BuildOperataFile() => new(_settings.ProfileDirectory);

    /// <summary>Read like the other two so the profile is bound the same way; headless never speaks, so the text never reaches the prompt.</summary>
    private VocaliaFile BuildVocaliaFile() => new(_settings.ProfileDirectory);

    /// <summary>
    /// The skills as headless takes them per turn (<see cref="ChatScreen.SkillsForTurn"/>): the catalog and the tools over the live
    /// roots, the project file over the sandbox. With <paramref name="records"/> (2026-09-30) the tools record what they write and
    /// load, and the records are reconciled with the folders here, at the run's start, as the screen does at a profile load.
    /// </summary>
    private ChatScreen.SkillsForTurn BuildSkills(WorkingDirectory files, SkillRecords? records = null)
    {
        Func<SkillRoots> roots = () => SkillRoots.For(_settings, _externalSkills);
        var catalog = new SkillCatalog(roots);
        if (records is not null)
        {
            catalog.Scan(EffectiveSettings.ExternalSkills);
            records.Reconcile(catalog.Skills.Concat(catalog.Shadowed));
        }

        return new ChatScreen.SkillsForTurn(catalog, ChatScreen.SkillTools(catalog, roots, () => EffectiveSettings.AgentSkills && EffectiveSettings.ExternalSkills, new Llm.Tools.SkillFileAccess(_time), records), new ProjectFile(() => files.Root), EffectiveSettings.AgentSkills, EffectiveSettings.AgentSkills && EffectiveSettings.ExternalSkills, EffectiveSettings.ProjectFile);
    }

    /// <summary>The headless run's skill records (2026-09-30): what <c>/skills add</c>'s host tells of an install; null outside a run.</summary>
    private SkillRecords? _headlessSkillRecords;

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
        ThemeName.Apply(EffectiveSettings, _settings.ThemesDirectory);

        if (options.Headless)
        {
            return await RunHeadlessAsync(cancellationToken).ConfigureAwait(false);
        }

        // The interactive screen starts on a wiped alternate buffer (the chat screen's own doing);
        // the diagnostic modes print a banner on the terminal as it is, because their output is
        // captured (build.ps1 reads --smoke) and an erase sequence there is noise.
        bool interactive = !options.IsCheck;
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
                EncryptOraclePasswords();
                EncryptMySqlPasswords();
                EncryptUncPasswords();
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

        if (options.SqlCheck is { } sqlConnection)
        {
            // The whole catalog, as the Oracle and MySQL checks (2026-10-03): the check proves the driver, whatever this profile offers the model.
            var catalog = Sql.SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            return await SqlCheck.RunAsync(_console, catalog, sqlConnection, EffectiveSettings.SqlQueryTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        if (options.OracleCheck is { } oracleConnection)
        {
            // The whole catalog, not the offered list: the check proves the driver, whatever this profile offers the model.
            var catalog = Oracle.OracleConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            return await OracleCheck.RunAsync(_console, catalog, oracleConnection, EffectiveSettings.OracleQueryTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        if (options.MySqlCheck is { } mysqlConnection)
        {
            var catalog = MySql.MySqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            return await MySqlCheck.RunAsync(_console, catalog, mysqlConnection, EffectiveSettings.MySqlQueryTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        if (options.DockerCheck)
        {
            // The live proof on the published exe (2026-10-02): reads only, over the pipe the settings name.
            using var dockerCheck = new Docker.DockerSession(() => EffectiveSettings, _dockerClient, _time);
            return await DockerCheck.RunAsync(_console, dockerCheck, cancellationToken).ConfigureAwait(false);
        }

        if (options.CameraCheck)
        {
            // The live proof on the published exe (2026-10-02): the camera the settings name, nothing saved.
            using var cameraCheck = new Camera.CameraSession(_camera, () => Camera.CameraSettings.Options(EffectiveSettings), _time);
            return await CameraCheck.RunAsync(_console, cameraCheck, cancellationToken).ConfigureAwait(false);
        }

        if (options.UncCheck is { } uncShare)
        {
            // The whole catalog, not the offered list, as the database checks: the check proves the share, whatever this profile offers the model.
            var catalog = Unc.UncConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            return await UncCheck.RunAsync(_console, catalog, uncShare, _time, cancellationToken).ConfigureAwait(false);
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
    /// whose count of the rows the banner takes is what keeps the input row at the bottom. With
    /// <c>Show header</c> off (2026-10-01, the user's ask) the wipe alone: the transcript starts at the top row.
    /// </summary>
    public void RenderScreen(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        ClearScreen(console);
        if (EffectiveSettings.ShowHeader)
        {
            RenderBanner(console);
        }
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
        EncryptOraclePasswords();
        EncryptMySqlPasswords();
        EncryptUncPasswords();
        await using var embedded = _embeddedLlm?.Invoke();
        await using var claudeServer = _claudeServerFactory();
        _claudeServer = claudeServer;
        using var dockerServers = BuildDockerServers();
        using var session = new LlmSession(_probe, _contextProbe, _chatClientFactory, _time, _samplingProbe, embedded, claudeServer, ClaudeCliOffered, dockerServers);
        // The MCP servers (2026-09-20): connected after the LLM, their tools offered per turn like the screen's; disposed after the loop.
        await using var mcp = new McpSession(_settings, _mcpTransport, _time);
        var memory = BuildMemoryStore();
        var memoryTools = ChatScreen.MemoryTools(memory);
        // No timers headless: nothing could deliver the alert (the same rule as speech and voice); PrepareTurn drops the prompt's timer sentence with them (2026-09-20).
        var clockTools = ChatScreen.ClockTools(_time);
        // The file tools need no console, so headless has them; the editor opener is the real one.
        // The setting File tools decides per turn, like Web tools; the clock alone is standing.
        var files = BuildWorkingDirectory();
        // The UNC shares' door first (2026-10-01): open reaches the offered shares too.
        var unc = new Unc.UncAccess(() => Unc.UncConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(EffectiveSettings.UncSharesOffered), _time);
        var fileTools = ChatScreen.FileTools(files, () => WorkingDirectory.IsDefault(EffectiveSettings.WorkingDirectory), PersonaFile.OpenInEditor, () => EffectiveSettings, unc);
        // The app's own manual beside the clock (2026-10-02): it reads documentation only, so headless offers it too.
        IReadOnlyList<AIFunction> standingTools = [.. clockTools, .. ChatScreen.HelpTools()];
        // The skills need no console either (2026-09-16); the two settings decide per turn. The skill records (2026-09-30) are the home's.
        using var skillStore = new SkillRecordStore(_settings.StorageDirectory);
        _headlessSkillRecords = new SkillRecords(skillStore, () => SkillRoots.For(_settings, _externalSkills), _time);
        var skills = BuildSkills(files, _headlessSkillRecords);
        // The web tools need no console either; the setting Web tools decides per turn.
        var webTools = ChatScreen.WebTools(_web, files, () => EffectiveSettings);
        var git = new Git.GitAccess(files, _time);
        var gitTools = ChatScreen.GitTools(git, () => EffectiveSettings);
        var vaultTools = ChatScreen.ObsidianTools(new Obsidian.ObsidianVault(() => EffectiveSettings.ObsidianVault, _time), () => EffectiveSettings);
        var sql = new Sql.SqlAccess(() => Sql.SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(EffectiveSettings.SqlConnectionsOffered));
        var sqlTools = ChatScreen.SqlTools(sql, () => EffectiveSettings);
        var oracle = new Oracle.OracleAccess(() => Oracle.OracleConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(EffectiveSettings.OracleConnectionsOffered));
        var oracleTools = ChatScreen.OracleTools(oracle, () => EffectiveSettings);
        var mysql = new MySql.MySqlAccess(() => MySql.MySqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(EffectiveSettings.MySqlConnectionsOffered));
        var mysqlTools = ChatScreen.MySqlTools(mysql, () => EffectiveSettings);
        // The UNC tools (2026-09-30): no console needed, so headless has them too; every change still needs UNC writes and a readwrite share.
        var uncTools = ChatScreen.UncTools(unc, files, () => EffectiveSettings);
        // The image tools (2026-09-24): no console needed, so headless has them too.
        using var comfy = new Comfy.ComfyStudio(ChatScreen.ComfyCatalog(_settings), files, () => EffectiveSettings, _comfyClient);
        var comfyTools = ChatScreen.ComfyTools(comfy, files, () => _settings.ProfileSplashDirectory);
        // The Home Assistant tools (2026-09-28): no pane to ask on, so an asked call is refused; the policy's safe list runs.
        using var ha = new HomeAssistant.HaSession(() => EffectiveSettings, _haClient, _time);
        var haTools = ChatScreen.HomeAssistantTools(ha, confirm: null);
        // The Docker tools (2026-10-02): no pane to ask on, so every change is refused; the reads and /docker work.
        using var docker = new Docker.DockerSession(() => EffectiveSettings, _dockerClient, _time);
        // The camera (2026-10-02): /camera list only; a photo needs the screen's panes, so camera_capture is never offered here.
        using var camera = new Camera.CameraSession(_camera, () => Camera.CameraSettings.Options(EffectiveSettings), _time);
        var dockerTools = ChatScreen.DockerTools(docker, confirm: null);
        // The print tools (2026-09-28): no pane to ask on, so under ask (the default) the model's print is refused; /print works.
        var print = new Printing.PrintService(_printSpooler, files, () => EffectiveSettings, _time);
        var printTools = ChatScreen.PrintTools(print, confirm: null);
        // The shell tools (2026-09-21): headless has no pane to ask on, so the gate has no asker — under ask the
        // allow list alone decides, and NEONSIDEKICK_COMMAND_POLICY=yolo is how a scripted run says yes.
        var interpreters = new Shell.Interpreters(_environment.System);
        var allowList = new Shell.CommandAllowList(() => EffectiveSettings.ShellCommandAllowed, allowed => _settings.Update(d => d.ShellCommandAllowed = [.. allowed]));
        var runner = new Shell.ShellRunner(_time);
        // The background processes (2026-09-21): nothing to signal headless (the next line is read when it is read); the exits print as notices at the loop top and ride the next turn as seeded polls.
        using var processes = new Shell.ProcessRegistry(runner, Random.Shared, () => { });
        // Kept (2026-09-26): what it refused is the run's summary notice and exit code 3 at the end.
        var gate = new Shell.CommandGate(() => EffectiveSettings, allowList, null);
        var shellTools = ChatScreen.ShellTools(runner, processes, files, gate, interpreters, () => EffectiveSettings, Random.Shared, () => session.Assistant?.Tools ?? []);
        var persona = BuildPersonaFile();
        var operata = BuildOperataFile();
        var vocalia = BuildVocaliaFile();
        // The session store (2026-09-18): headless logs its turns and offers the tool like the
        // screen; no /sessions, no restore, the first line always the title.
        using var sessions = new Sessions.SessionStore(_settings.ProfileDirectory, _time);
        long? sessionId = null;
        // The Claude conversation /claude resumes (2026-09-27), as the screen keeps it; dropped with the session.
        string? claudeSessionId = null;
        // The Claude CLI server's session (2026-09-30), as the screen keeps it: minted at the first turn over it, dropped with the session.
        string? claudeServerSessionId = null;
        var claude = _claude ?? new Claude.ClaudeProcess(_environment.System);
        // claude_advisor (2026-09-27): its own thread, as the screen keeps it; no one to confirm with, so a call under
        // Claude advisor tool confirm is refused; Claude's tools and the footer as [tool] / [notice] lines, the answer the result's line.
        var advisorThread = new Claude.ClaudeAdvisorThread();
        var advisorTools = ChatScreen.ClaudeAdvisorTools(claude, () => EffectiveSettings, () => files.Root, advisorThread, (usage, usd) => session.Usage.AddClaude(usage, usd), () => session.History.Messages, null, new HeadlessAdvisorView(this));
        var sessionTools = ChatScreen.SessionTools(sessions, () => EffectiveSettings, () => sessionId, _time);
        // Plan mode (2026-09-26): no pane to approve on, so a presented plan is saved and the user approves it with
        // /plan approve; the saved line is printed once the turn is over, never inside the streamed reply.
        var plan = new Plans.PlanSession();
        var presentPlan = new Llm.Tools.PresentPlanTool(plan, files, _time, (_, _) => Task.FromResult(new Plans.PlanVerdict(Plans.PlanChoice.Saved)));
        var planState = new HeadlessPlanState();
        _liveSession = session;
        try
        {
            await HeadlessLineAsync(VersionLine).ConfigureAwait(false);
            await HeadlessLineAsync(HeadlessHint).ConfigureAwait(false);

            // A chosen Docker container (2026-10-02) takes minutes to load: each step of its switch is a notice, said once.
            Action<string>? phase = Docker.DockerEndpoint.Chosen(EffectiveSettings) ? HeadlessDockerPhase : null;
            await session.ConnectAsync(EffectiveSettings, phase, cancellationToken).ConfigureAwait(false);
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

            // The conversation forgotten (/clear, /new, /splash, and a /test run's clean slate since 2026-09-30): the history, the
            // usage, the session row and the Claude threads; plan mode left with its notice.
            async Task ForgetConversationAsync()
            {
                assistant?.History.Clear();
                session.Usage.ResetConversation();
                sessionId = null;
                claudeSessionId = null;
                claudeServerSessionId = null;
                advisorThread.SessionId = null;
                planState.Executing = null;
                planState.Draft = null;
                if (plan.Active)
                {
                    await HeadlessNoticeLineAsync("[notice] " + Plans.PlanText.LeftOnResetNotice(plan.Path)).ConfigureAwait(false);
                    plan.Exit();
                }
            }

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
                    await ForgetConversationAsync().ConfigureAwait(false);
                    await HeadlessLineAsync(HeadlessReplyPrefix + (isClear ? "(conversation cleared)" : ChatScreen.NewConversationNotice)).ConfigureAwait(false);
                    continue;
                }

                // /skills add (2026-09-26): ahead of the server check, since an install needs no LLM; the screen's flow,
                // several hits listed as ids and nothing written without --yes. A bare /skills still goes to the model.
                if (SlashCommands.Parse(text) is (SlashCommand.Skills, var skillArgs)
                    && skillArgs.Split(' ', 2)[0].Equals(Skills.SkillInstallText.AddWord, StringComparison.OrdinalIgnoreCase))
                {
                    await HeadlessSkillsAddAsync(skillArgs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // /claude (2026-09-27): ahead of the server check too — Claude Code is its own model; the history it joins is the session's.
                if (SlashCommands.Parse(text) is (SlashCommand.Claude, var claudeArgs))
                {
                    var claudeTurn = await HeadlessClaudeAsync(claude, session, claudeArgs, claudeSessionId, files.Root, cancellationToken).ConfigureAwait(false);
                    claudeSessionId = claudeTurn.SessionId;
                    if (claudeTurn.Reply is { } claudeReply && EffectiveSettings.SessionLogging)
                    {
                        sessionId ??= sessions.Begin(Sessions.SessionText.FirstLineTitle(text), session.Endpoint?.ModelId ?? "");
                        if (sessionId is { } claudeRow)
                        {
                            ChatScreen.StampTurn(session.History, sessions.AppendTurn(claudeRow, text, claudeReply, 0, [], [], 0, claudeTurn.Usage.Input, claudeTurn.Usage.Output, claudeTurn.Cancelled));
                        }
                    }

                    if (sessionId is { } claudeSaved)
                    {
                        sessions.SaveHistory(claudeSaved, Sessions.SessionHistory.ToJson(session.History.Messages, plan.ToStored(), planState.Executing, claudeSessionId, advisorThread.SessionId, EffectiveSettings.SessionSaveThinking, claudeServerSessionId));
                    }

                    continue;
                }

                // /rewind [n] (2026-09-30): ahead of the server check too, since cutting the history needs no LLM. There is no
                // picker and no input row, so it goes n messages back at once (1 by default) and prints the dropped message.
                if (SlashCommands.Parse(text) is (SlashCommand.Rewind, var rewindArgs))
                {
                    var held = ConversationRewind.Turns(session.History.Messages);
                    if (held.Count == 0)
                    {
                        await HeadlessLineAsync(HeadlessReplyPrefix + RewindText.NothingNotice).ConfigureAwait(false);
                        continue;
                    }

                    if (RewindText.ParseCount(rewindArgs) is not { } back || back > held.Count)
                    {
                        await HeadlessLineAsync("[error] " + RewindText.UsageError(held.Count)).ConfigureAwait(false);
                        continue;
                    }

                    var picked = held[^back];
                    var (cut, _) = ConversationRewind.Apply(session.History, picked, sessionId is null ? null : sessions, sessionId);
                    claudeServerSessionId = null;
                    if (cut.HadClaude)
                    {
                        claudeSessionId = null;
                    }

                    if (cut.HadAdvisor)
                    {
                        advisorThread.SessionId = null;
                    }

                    session.Usage.ForgetContext();
                    DiagnosticLog.Info(ChatScreen.AppCategory, RewindText.RewoundLogLine(cut.Turns, picked.Number));
                    if (sessionId is { } rewound)
                    {
                        sessions.SaveHistory(rewound, Sessions.SessionHistory.ToJson(session.History.Messages, plan.ToStored(), planState.Executing, claudeSessionId, advisorThread.SessionId, EffectiveSettings.SessionSaveThinking, claudeServerSessionId));
                    }

                    await HeadlessLineAsync(HeadlessReplyPrefix + RewindText.RewoundNotice(cut.Turns, picked.Text)).ConfigureAwait(false);
                    if (cut.ChangingTools.Count > 0)
                    {
                        await HeadlessNoticeLineAsync("[notice] " + RewindText.ChangesStayWarning(cut.ChangingTools)).ConfigureAwait(false);
                    }

                    await HeadlessNoticeLineAsync("[notice] " + RewindText.HeadlessLineNotice(picked.Text)).ConfigureAwait(false);
                    continue;
                }

                // /ha (2026-09-28): ahead of the server check too — the house needs no LLM.
                if (SlashCommands.Parse(text) is (SlashCommand.HomeAssistant, var haArgs))
                {
                    var haResult = await HomeAssistant.HaCommand.RunAsync(ha, haArgs, cancellationToken).ConfigureAwait(false);
                    foreach (string haLine in haResult.Lines)
                    {
                        await HeadlessLineAsync((haResult.Failed ? "[error] " : "") + haLine).ConfigureAwait(false);
                    }

                    continue;
                }

                // /camera (2026-10-02): ahead of the server check too; headless lists the cameras and nothing else.
                if (SlashCommands.Parse(text) is (SlashCommand.Camera, var cameraArgs))
                {
                    if (Camera.CameraCommand.Parse(cameraArgs).Verb != Camera.CameraVerb.List)
                    {
                        await HeadlessLineAsync("[error] " + Camera.CameraText.NeedsScreen).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var (cameraLine, cameraError) in await Camera.CameraCommand.ListAsync(camera, EffectiveSettings.CameraDevice, cancellationToken).ConfigureAwait(false))
                    {
                        await HeadlessLineAsync((cameraError ? "[error] " : "") + cameraLine).ConfigureAwait(false);
                    }

                    continue;
                }

                // /docker (2026-10-02): ahead of the server check too — the engine needs no LLM; the bare word lists.
                if (SlashCommands.Parse(text) is (SlashCommand.Docker, var dockerArgs))
                {
                    var dockerResult = await Docker.DockerCommand.RunAsync(docker, dockerArgs, cancellationToken).ConfigureAwait(false);
                    foreach (string dockerLine in dockerResult.Lines)
                    {
                        await HeadlessLineAsync((dockerResult.Failed ? "[error] " : "") + dockerLine).ConfigureAwait(false);
                    }

                    continue;
                }

                // /print (2026-09-28): ahead of the server check too — only /print reply needs a reply to print.
                if (SlashCommands.Parse(text) is (SlashCommand.Print, var printArgs))
                {
                    var printResult = await Printing.PrintCommand.RunAsync(print, printArgs, () => LastReplyText(assistant?.History.Messages), cancellationToken).ConfigureAwait(false);
                    foreach (string printLine in printResult.Lines)
                    {
                        await HeadlessLineAsync((printResult.Failed ? "[error] " : "") + printLine).ConfigureAwait(false);
                    }

                    continue;
                }

                // /test (2026-09-28): ahead of the server check, since the listing and the saved runs need no LLM; a run refuses without one.
                if (SlashCommands.Parse(text) is (SlashCommand.Test, var testArgs))
                {
                    await HeadlessTestAsync(session, testArgs, ForgetConversationAsync, cancellationToken).ConfigureAwait(false);
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

                if (command == SlashCommand.Plan)
                {
                    // /plan headless (2026-09-26): the screen's words, the approval typed; a line that starts a turn comes back as its text.
                    Assistant live = assistant;
                    if (await HeadlessPlanAsync(plan, planState, files, args, () => { live.History.Clear(); session.Usage.ResetConversation(); sessionId = null; }).ConfigureAwait(false) is not { } planned)
                    {
                        if (sessionId is { } planSession)
                        {
                            sessions.SaveHistory(planSession, Sessions.SessionHistory.ToJson(assistant.History.Messages, plan.ToStored(), planState.Executing, claudeSessionId, advisorThread.SessionId, EffectiveSettings.SessionSaveThinking, claudeServerSessionId));
                        }

                        continue;
                    }

                    text = planned;
                    planState.RevisionShown = plan.Revision;
                }

                // As the screen does before a message: the last reply's context past the LLM auto compact (%) share compacts first.
                int share = EffectiveSettings.LlmAutoCompactPercent;
                assistant.History.MaxTurns = ChatScreen.TurnCapFor(EffectiveSettings, session.ContextLength);
                if (!Claude.ClaudeCliEndpoint.IsClaudeCli(session.Endpoint?.BaseUrl) && ConversationCompactor.ShouldAutoCompact(session.Usage.LastRequest, session.ContextLength, share))
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
                ChatScreen.PrepareTurn(assistant, memory, memoryTools, standingTools, persona, operata, vocalia, EffectiveSettings.Memory, speechOutput: false, EffectiveSettings.LlmMaxToolIterations, EffectiveSettings.LlmOfferTools, webTools, EffectiveSettings.WebTools, ChatScreen.ContextGuardFor(EffectiveSettings, session.ContextLength), fileTools, EffectiveSettings.FileTools, skills: skills with { Enabled = EffectiveSettings.AgentSkills, External = EffectiveSettings.AgentSkills && EffectiveSettings.ExternalSkills }, sessionTools: sessionTools, sessionsEnabled: EffectiveSettings.SessionTool, disabledTools: ToolsText.DisabledSet(EffectiveSettings.ToolsDisabled), mcpTools: mcp.Tools, mcpEnabled: EffectiveSettings.McpServers, gitTools: gitTools, gitEnabled: EffectiveSettings.GitLibTools, shellTools: shellTools, shellEnabled: ChatScreen.ShellOffered(EffectiveSettings), processes: processes, shellBridge: EffectiveSettings.ShellToolBridge, shellPolice: EffectiveSettings.ShellPoliceOutsidePaths, obsidianTools: ChatScreen.ObsidianToolsFor(vaultTools, EffectiveSettings), obsidianEnabled: ChatScreen.ObsidianOffered(EffectiveSettings), sqlTools: sqlTools, sqlEnabled: ChatScreen.SqlOffered(EffectiveSettings, sql), comfyTools: comfyTools, comfyEnabled: ChatScreen.ComfyOffered(EffectiveSettings, comfy), shellNative: EffectiveSettings.ShellPreferNative, plan: plan.Turn(presentPlan), advisorTools: advisorTools, advisorEnabled: EffectiveSettings.ClaudeAdvisor, preserveThinking: EffectiveSettings.LlmPreserveThinking, sampling: LlmSampling.Resolve(EffectiveSettings, session.Endpoint?.ModelId), homeTools: haTools, homeEnabled: ChatScreen.HomeAssistantOffered(EffectiveSettings), printTools: printTools, printEnabled: ChatScreen.PrintOffered(EffectiveSettings), oracleTools: oracleTools, oracleEnabled: ChatScreen.OracleOffered(EffectiveSettings, oracle), mysqlTools: mysqlTools, mysqlEnabled: ChatScreen.MySqlOffered(EffectiveSettings, mysql), uncTools: ChatScreen.UncToolsFor(uncTools, EffectiveSettings, unc.Catalog(), EffectiveSettings.FileTools), uncEnabled: ChatScreen.UncOffered(EffectiveSettings, unc), dockerTools: ChatScreen.DockerToolsFor(dockerTools, EffectiveSettings), dockerEnabled: ChatScreen.DockerOffered(EffectiveSettings));

                // The Claude CLI server (2026-09-30), as the screen does: the turn names its session, no guard over a history the CLI does not read.
                assistant.ConversationId = Claude.ClaudeCliEndpoint.IsClaudeCli(session.Endpoint?.BaseUrl) ? claudeServerSessionId ??= Guid.NewGuid().ToString("D") : null;
                if (assistant.ConversationId is not null)
                {
                    assistant.ContextGuard = null;
                }

                var turn = await RunHeadlessTurnAsync(session, assistant, text, cancellationToken).ConfigureAwait(false);
                if (plan.Active && plan.Path is { } planPath && plan.Revision > planState.RevisionShown)
                {
                    planState.RevisionShown = plan.Revision;
                    await HeadlessNoticeLineAsync("[notice] " + Plans.PlanText.SavedNotice(planPath, plan.Revision) + " — /plan approve [--fresh] starts it").ConfigureAwait(false);
                }

                // Round two (2026-09-26), as the screen does after a turn: a plan being carried out marked done or incomplete,
                // and a planning reply that reads as a plan the model never presented hinted (/plan save keeps it).
                foreach (string planLine in HeadlessPlanAfterTurn(plan, planState, files, turn.Reply, turn.Trace.ToolNames, turn.Cancelled))
                {
                    await HeadlessNoticeLineAsync("[notice] " + planLine).ConfigureAwait(false);
                }

                if (EffectiveSettings.SessionLogging)
                {
                    sessionId ??= sessions.Begin(Sessions.SessionText.FirstLineTitle(text), session.Endpoint?.ModelId ?? "");
                    if (sessionId is { } id)
                    {
                        var usage = session.Usage.LastRequest;
                        ChatScreen.StampTurn(assistant.History, sessions.AppendTurn(id, text, turn.Reply, turn.Trace.ToolCalls, turn.Trace.ToolNames, turn.Trace.LoadedSkills, turn.Trace.Errors, usage.Input, usage.Output, turn.Cancelled));
                        sessions.SaveHistory(id, Sessions.SessionHistory.ToJson(assistant.History.Messages, plan.ToStored(), planState.Executing, claudeSessionId, advisorThread.SessionId, EffectiveSettings.SessionSaveThinking, claudeServerSessionId));
                    }
                }
            }

            var refused = gate.Refusals;
            if (refused.Count > 0)
            {
                string summary = Shell.ShellText.RefusedSummary(refused);
                DiagnosticLog.Info(ChatScreen.AppCategory, summary);
                await HeadlessNoticeLineAsync("[notice] " + summary).ConfigureAwait(false);
            }

            await EnsureHeadlessLineStartAsync().ConfigureAwait(false);
            await _headlessOutput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return refused.Count > 0 ? HeadlessRefusedExitCode : 0;
        }
        finally
        {
            _liveSession = null;
            // Docker server stop on exit (2026-10-02): off by default, the container keeps running.
            foreach (string stopped in await StopDockerAtExitAsync(session, DockerExitBudget).ConfigureAwait(false))
            {
                await HeadlessNoticeLineAsync("[notice] " + DockerServerExitNotice(stopped)).ConfigureAwait(false);
            }

            // The MCP servers go while the log still forwards through the headless writer: a
            // `Stopped docker` line after the echo came back would land on stdout on its own.
            await mcp.DisposeAsync().ConfigureAwait(false);
            DiagnosticLog.Emitted -= forward;
            DiagnosticLog.EchoToConsole = previousEcho;
        }
    }

    /// <summary>
    /// <c>/plan</c> headless (2026-09-26): <see cref="ChatScreen.ParsePlanArgs"/> as on the screen, its lines as
    /// <c>[notice]</c> / <c>[error]</c>. Returns the text to send as a turn — the requirement, more detail, or the
    /// approved plan's message (after <paramref name="forget"/> for <c>--fresh</c>) — or null when the line is done.
    /// </summary>
    private async Task<string?> HeadlessPlanAsync(Plans.PlanSession plan, HeadlessPlanState state, WorkingDirectory files, string args, Action forget)
    {
        var command = ChatScreen.ParsePlanArgs(args, plan.Active, out string rest);
        string? found = command == ChatScreen.PlanCommand.Open ? Plans.PlanFiles.Resolve(files, rest) : null;
        if (command == ChatScreen.PlanCommand.Open && found is null)
        {
            if (!rest.Any(char.IsWhiteSpace))
            {
                await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + Plans.PlanText.NoSuchPlanError(rest)).ConfigureAwait(false);
                await HeadlessListPlansAsync(files).ConfigureAwait(false);
                return null;
            }

            command = plan.Active ? ChatScreen.PlanCommand.Detail : ChatScreen.PlanCommand.Enter;
        }

        switch (command)
        {
            case ChatScreen.PlanCommand.List:
                await HeadlessListPlansAsync(files).ConfigureAwait(false);
                return null;
            case ChatScreen.PlanCommand.Open:
            {
                if (!EffectiveSettings.LlmOfferTools)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + Plans.PlanText.NeedsToolsError).ConfigureAwait(false);
                    return null;
                }

                if (Plans.PlanFiles.ReadWhole(files, found!) is not { } opened)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + Plans.PlanText.PlanUnreadableError(found!)).ConfigureAwait(false);
                    return null;
                }

                var header = Plans.PlanDocument.TryParse(opened);
                string body = Plans.PlanDocument.Body(opened);
                var (done, total) = Plans.PlanDocument.Progress(body);
                string title = Plans.PlanDocument.FirstHeading(body) ?? Path.GetFileNameWithoutExtension(found!);
                string requirement = header is { Requirement.Length: > 0 } ? header.Requirement : title;
                if (plan.Active && !string.Equals(plan.Path, found, StringComparison.Ordinal))
                {
                    await HeadlessNoticeLineAsync("[notice] " + Plans.PlanText.SwitchedNotice(plan.Path)).ConfigureAwait(false);
                }

                state.Executing = null;
                state.Draft = null;
                plan.Open(found!, title, requirement, header?.Revision ?? 1, header is { Created: var created } && created != DateTimeOffset.MinValue ? created : null);
                state.RevisionShown = plan.Revision;
                if (Plans.PlanFiles.MarkFile(files, found!, Plans.PlanStatus.Draft, _time.GetLocalNow(), requirement) is { } problem)
                {
                    await HeadlessNoticeLineAsync("[notice] " + problem).ConfigureAwait(false);
                }

                await HeadlessNoticeLineAsync("[notice] " + Plans.PlanText.OpenedNotice(found!, header?.Status, done, total)).ConfigureAwait(false);
                return Plans.PlanText.OpenMessage(found!, done, total);
            }

            case ChatScreen.PlanCommand.Save:
            {
                if (state.Draft is not { } reply)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + Plans.PlanText.NothingToSaveError).ConfigureAwait(false);
                    return null;
                }

                var (saved, error) = Plans.PlanFiles.Save(plan, files, _time, Plans.PlanDocument.FirstHeading(reply) ?? plan.Requirement, reply, rest.Length == 0 ? null : rest);
                if (saved is null)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + Plans.PlanText.CouldNotSaveResult(error ?? "")).ConfigureAwait(false);
                    return null;
                }

                state.Draft = null;
                state.RevisionShown = saved.Revision;
                await HeadlessNoticeLineAsync("[notice] " + Plans.PlanText.SavedNotice(saved.Path, saved.Revision) + " — /plan approve [--fresh] starts it").ConfigureAwait(false);
                return null;
            }

            case ChatScreen.PlanCommand.Usage:
                await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + Plans.PlanText.UsageError).ConfigureAwait(false);
                return null;
            case ChatScreen.PlanCommand.NotPlanning:
                await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + Plans.PlanText.NotPlanningError).ConfigureAwait(false);
                return null;
            case ChatScreen.PlanCommand.Show:
                foreach (string line in Plans.PlanText.ShowLines(plan))
                {
                    await HeadlessNoticeLineAsync("[notice] " + line.Trim()).ConfigureAwait(false);
                }

                return null;
            case ChatScreen.PlanCommand.Cancel:
            {
                string? path = plan.Path;
                if (path is not null && Plans.PlanFiles.MarkFile(files, path, Plans.PlanStatus.Cancelled, _time.GetLocalNow(), plan.Requirement) is { } problem)
                {
                    await HeadlessNoticeLineAsync("[notice] " + problem).ConfigureAwait(false);
                }

                plan.Exit();
                await HeadlessNoticeLineAsync("[notice] " + Plans.PlanText.CancelledNotice(path)).ConfigureAwait(false);
                return null;
            }

            case ChatScreen.PlanCommand.Approve or ChatScreen.PlanCommand.ApproveFresh:
            {
                if (plan.Path is not { } path)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + Plans.PlanText.NothingPresentedError).ConfigureAwait(false);
                    return null;
                }

                if (Plans.PlanFiles.ReadWhole(files, path) is not { } text)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + Plans.PlanText.PlanUnreadableError(path)).ConfigureAwait(false);
                    return null;
                }

                if (Plans.PlanFiles.MarkFile(files, path, Plans.PlanStatus.Approved, _time.GetLocalNow(), plan.Requirement) is { } problem)
                {
                    await HeadlessNoticeLineAsync("[notice] " + problem).ConfigureAwait(false);
                }

                bool fresh = ChatScreen.ParsePlanArgs(args, planning: true) == ChatScreen.PlanCommand.ApproveFresh;
                // Tracked until every step is ticked (round two): HeadlessPlanAfterTurn marks it done or incomplete.
                state.Executing = new Sessions.StoredPlan { Path = path, Requirement = plan.Requirement };
                state.Shown = null;
                state.Draft = null;
                plan.Exit();
                await HeadlessNoticeLineAsync("[notice] " + Plans.PlanText.ApprovedNotice(path, fresh)).ConfigureAwait(false);
                if (!fresh)
                {
                    return Plans.PlanText.ExecuteMessage(path);
                }

                forget();
                return Plans.PlanText.ExecuteFreshMessage(path, Plans.PlanDocument.Body(text));
            }

            case ChatScreen.PlanCommand.Enter:
                if (!EffectiveSettings.LlmOfferTools)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + Plans.PlanText.NeedsToolsError).ConfigureAwait(false);
                    return null;
                }

                plan.Enter(args);
                state.Executing = null;
                state.Draft = null;
                await HeadlessNoticeLineAsync("[notice] " + Plans.PlanText.EnteredNotice).ConfigureAwait(false);
                return plan.Requirement;

            default:
                return args;
        }
    }

    /// <summary>Headless plan mode's own state beside the session (2026-09-26, round two): the plan being carried out, the ticked count last said, an unpresented plan reply and the revision last announced.</summary>
    private sealed class HeadlessPlanState
    {
        public Sessions.StoredPlan? Executing { get; set; }
        public (int Done, int Total)? Shown { get; set; }
        public string? Draft { get; set; }
        public int RevisionShown { get; set; }
    }

    /// <summary><c>/plan open</c> alone headless: one <c>[notice]</c> line per plan, or that there are none.</summary>
    private async Task HeadlessListPlansAsync(WorkingDirectory files)
    {
        var plans = Plans.PlanFiles.List(files);
        if (plans.Count == 0)
        {
            await HeadlessNoticeLineAsync("[notice] " + Plans.PlanText.NoPlansNotice).ConfigureAwait(false);
            return;
        }

        foreach (var listed in plans)
        {
            await HeadlessNoticeLineAsync("[notice] " + Plans.PlanText.ListLine(listed).Trim()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The screen's after-turn checks, headless (round two): the plan being carried out counted and marked — done ends
    /// the tracking, incomplete is said when the count moves or the turn was cut — and a planning reply that reads as
    /// a plan from a turn without <c>present_plan</c> kept for <c>/plan save</c> with the hint. The lines to print.
    /// </summary>
    private List<string> HeadlessPlanAfterTurn(Plans.PlanSession plan, HeadlessPlanState state, WorkingDirectory files, string reply, IReadOnlyList<string> tools, bool cancelled)
    {
        var lines = new List<string>(2);
        if (state.Executing is { Path: { } path } executing)
        {
            if (Plans.PlanFiles.ReadWhole(files, path) is not { } text)
            {
                state.Executing = null;
            }
            else
            {
                var progress = Plans.PlanDocument.Progress(Plans.PlanDocument.Body(text));
                if (progress.Total == 0)
                {
                    state.Executing = null;
                }
                else if (progress.Done == progress.Total)
                {
                    Plans.PlanFiles.MarkFile(files, path, Plans.PlanStatus.Done, _time.GetLocalNow(), executing.Requirement, progress);
                    lines.Add(Plans.PlanText.DoneNotice(path));
                    state.Executing = null;
                    state.Shown = null;
                }
                else
                {
                    Plans.PlanFiles.MarkFile(files, path, Plans.PlanStatus.Incomplete, _time.GetLocalNow(), executing.Requirement, progress);
                    if (cancelled || state.Shown != progress)
                    {
                        lines.Add(Plans.PlanText.IncompleteNotice(path, progress.Done, progress.Total));
                        state.Shown = progress;
                    }
                }
            }
        }

        state.Draft = null;
        if (plan.Active && !cancelled && !tools.Contains(Llm.Tools.PresentPlanTool.ToolName, StringComparer.Ordinal) && Plans.PlanDocument.LooksLikePlan(reply))
        {
            state.Draft = reply.Trim();
            lines.Add(Plans.PlanText.UnpresentedHint);
        }

        return lines;
    }

    /// <summary>
    /// <c>/compact</c> headless, and the automatic one: the same <see cref="ConversationCompactor"/>
    /// as the screen, no spinner, Ctrl+C the only cancel; the outcome as one line of
    /// <see cref="CompactionText"/>, the failure line included, and — under <c>LLM compact show
    /// summary</c> (2026-09-21) — the detail lines the screen prints under its notice
    /// (<see cref="CompactionText.DetailLines"/>), empty otherwise.
    /// </summary>
    /// <summary>
    /// <c>/test</c> headless (2026-09-28): the screen's words — the listing, the saved runs, or a run with a <c>[notice]</c>
    /// line per test as it finishes (a fail's answer under it) and the table after as plain markdown — saved to the
    /// profile's <c>tests.json</c> as the screen saves it. Ctrl+C stops a run; what finished is shown and saved.
    /// </summary>
    private async Task HeadlessTestAsync(LlmSession session, string args, Func<Task> forgetConversation, CancellationToken cancellationToken)
    {
        var history = new Bench.BenchHistory(_settings.ProfileDirectory);
        if (args.Length == 0)
        {
            string? model = session.Endpoint?.ModelId;
            await HeadlessLineAsync(Bench.BenchText.Listing(model is null ? new Dictionary<string, (Bench.BenchResult, DateTimeOffset)>() : history.Latest(model), model)).ConfigureAwait(false);
            return;
        }

        if (args.Equals(Bench.BenchCatalog.HistoryWord, StringComparison.OrdinalIgnoreCase))
        {
            var runs = history.Runs();
            await HeadlessLineAsync(runs.Count == 0 ? "[notice] " + Bench.BenchText.NoRuns : Bench.BenchText.History(runs, history.FilePath)).ConfigureAwait(false);
            return;
        }

        var tests = Bench.BenchCatalog.Resolve(args);
        if (tests.Count == 0)
        {
            await HeadlessLineAsync("[error] " + Bench.BenchText.UnknownTest(args)).ConfigureAwait(false);
            return;
        }

        if (session.Assistant is not { } assistant || session.Endpoint is not { } endpoint)
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + HeadlessNoAssistantReply).ConfigureAwait(false);
            return;
        }

        // A run starts from a clean slate, as the screen's does (2026-09-30): the conversation forgotten first.
        await forgetConversation().ConfigureAwait(false);
        var context = new Bench.BenchContext(session.ContextLength?.Tokens);
        bool claudeApi = Llm.Anthropic.ClaudeApi.IsClaudeApi(endpoint.BaseUrl);
        if (tests.Any(t => t.Category == Bench.BenchCategory.LongContext))
        {
            await HeadlessNoticeLineAsync("[notice] " + Bench.BenchText.ContextLine(context)).ConfigureAwait(false);
        }

        var run = Bench.BenchRunner.NewRun(assistant, endpoint, context, _time);
        try
        {
            foreach (var test in tests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await Bench.BenchRunner.RunOneAsync(assistant, test, context, claudeApi, _time, cancellationToken).ConfigureAwait(false);
                run.Results.Add(result);
                await HeadlessNoticeLineAsync("[notice] " + Bench.BenchText.ResultLine(result)).ConfigureAwait(false);
                if (Bench.BenchText.AnswerLine(result) is { } answer)
                {
                    await HeadlessNoticeLineAsync("[notice] " + answer).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Cancelled = true;
            await HeadlessNoticeLineAsync("[notice] " + Bench.BenchText.Cancelled(run.Results.Count, tests.Count)).ConfigureAwait(false);
        }

        if (run.Results.Count > 0)
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + Bench.BenchText.Summary(run)).ConfigureAwait(false);
            if (!history.Append(run))
            {
                await HeadlessNoticeLineAsync("[notice] " + Bench.BenchText.NotSaved).ConfigureAwait(false);
            }
        }

        DiagnosticLog.Info("Test", $"/test {args}: {run.Passed}/{run.Counted} passed on {run.Model}{(run.Cancelled ? ", cancelled" : "")}.");
    }

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
                // The model's thinking is the TUI's to show (LLM show thinking, 2026-09-26): stdout carries the reply alone.
                if (events.Current is TurnEvent.ThinkingDelta)
                {
                    continue;
                }

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
                    case TurnEvent.Compacted compacted:
                        // The mid-turn guard summarised (2026-09-28): billed like the automatic compact, one notice line.
                        session.Usage.AddCompaction(compacted.Result.Usage);
                        await HeadlessNoticeLineAsync("[notice] " + (compacted.ThisTurn ? CompactionText.TurnNotice(compacted.Result, compacted.Percent) : CompactionText.Notice(compacted.Result, compacted.Percent))).ConfigureAwait(false);
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

    /// <summary>How a headless <c>/claude</c> ended: the thread to resume next (null for none), the reply to log (null for none), its tokens.</summary>
    private readonly record struct HeadlessClaudeTurn(string? SessionId, string? Reply, TokenUsage Usage, bool Cancelled);

    /// <summary><c>Claude: </c>, ahead of a <c>/claude</c> reply on stdout, as <see cref="HeadlessReplyPrefix"/> is ahead of the local model's. Pinned.</summary>
    public const string HeadlessClaudePrefix = "Claude: ";

    /// <summary>
    /// <c>/claude</c> headless (2026-09-27): the screen's run over lines — the reply streamed after <see cref="HeadlessClaudePrefix"/>,
    /// each tool Claude used a <c>[tool]</c> line, the footer, a denial and a failure <c>[notice]</c> / <c>[error]</c> lines; the pair
    /// into the session's history, tagged; a lost resume tried once anew. <c>/claude new</c> drops the thread.
    /// </summary>
    private async Task<HeadlessClaudeTurn> HeadlessClaudeAsync(Claude.IClaudeCli claude, LlmSession session, string args, string? claudeSessionId, string root, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            await HeadlessNoticeLineAsync("[error] " + Claude.ClaudeText.UsageError).ConfigureAwait(false);
            return new HeadlessClaudeTurn(claudeSessionId, null, default, false);
        }

        if (string.Equals(args, Claude.ClaudeText.NewWord, StringComparison.OrdinalIgnoreCase))
        {
            await HeadlessNoticeLineAsync("[notice] " + Claude.ClaudeText.NewThreadNotice).ConfigureAwait(false);
            return new HeadlessClaudeTurn(null, null, default, false);
        }

        var effective = EffectiveSettings;
        bool resume = claudeSessionId is not null;
        string id = claudeSessionId ?? Guid.NewGuid().ToString("D");
        for (int attempt = 0; ; attempt++)
        {
            var level = Claude.ClaudePermission.Resolve(effective);
            var request = new Claude.ClaudeRequest(args, id, resume, root, level, effective.ClaudeExecutable,
                string.IsNullOrWhiteSpace(effective.ClaudeModel) ? null : effective.ClaudeModel.Trim(), Claude.ClaudeEffort.Resolve(effective.ClaudeEffort));
            var reply = new StringBuilder();
            Claude.ClaudeEvent.Result? result = null;
            bool prefixed = false;
            try
            {
                await foreach (var evt in claude.RunAsync(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case Claude.ClaudeEvent.TextDelta delta:
                            reply.Append(delta.Text);
                            string shown = delta.Text;
                            if (!prefixed)
                            {
                                // A block after a tool line starts with the parser's paragraph break: the prefix is the break here.
                                shown = shown.TrimStart('\r', '\n');
                                await EnsureHeadlessLineStartAsync().ConfigureAwait(false);
                                await _headlessOutput.WriteAsync(HeadlessClaudePrefix).ConfigureAwait(false);
                                prefixed = true;
                            }

                            await _headlessOutput.WriteAsync(shown).ConfigureAwait(false);
                            await _headlessOutput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                            _headlessAtLineStart = shown.EndsWith('\n');
                            break;
                        case Claude.ClaudeEvent.ToolActivity tool:
                            await HeadlessNoticeLineAsync("[tool] " + Claude.ClaudeText.ToolNote(tool.Name, tool.Detail)).ConfigureAwait(false);
                            prefixed = false;
                            break;
                        case Claude.ClaudeEvent.Result end:
                            result = end;
                            break;
                    }
                }
            }
            catch (Claude.ClaudeStartException ex)
            {
                await HeadlessNoticeLineAsync("[error] " + ex.Message).ConfigureAwait(false);
                return new HeadlessClaudeTurn(claudeSessionId, null, default, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await HeadlessNoticeLineAsync("(cancelled)").ConfigureAwait(false);
                AddClaudeExchange(session, args, reply);
                return new HeadlessClaudeTurn(claudeSessionId, reply.Length > 0 ? reply.ToString() : null, default, true);
            }

            await EnsureHeadlessLineStartAsync().ConfigureAwait(false);
            if (result is { } done)
            {
                session.Usage.AddClaude(done.Usage, done.CostUsd);
            }

            if (result is null or { IsError: true })
            {
                string error = result?.Error ?? Claude.ClaudeText.UnknownFailure;
                if (resume && attempt == 0 && reply.Length == 0 && error.Contains("No conversation found", StringComparison.OrdinalIgnoreCase))
                {
                    await HeadlessNoticeLineAsync("[notice] " + Claude.ClaudeText.ResumeLostNotice).ConfigureAwait(false);
                    resume = false;
                    id = Guid.NewGuid().ToString("D");
                    continue;
                }

                await HeadlessNoticeLineAsync("[error] " + Claude.ClaudeText.Failed(error)).ConfigureAwait(false);
                return new HeadlessClaudeTurn(resume ? claudeSessionId : null, null, default, false);
            }

            if (reply.Length == 0 && result.Text.Length > 0)
            {
                reply.Append(result.Text);
                await HeadlessLineAsync(HeadlessClaudePrefix + result.Text).ConfigureAwait(false);
            }

            if (result.Denied.Count > 0)
            {
                await HeadlessNoticeLineAsync("[notice] " + Claude.ClaudeText.DeniedNotice(result.Denied, Claude.ClaudePermission.Name(level))).ConfigureAwait(false);
            }

            await HeadlessNoticeLineAsync("[notice] " + Claude.ClaudeText.Footer(result.CostUsd, result.Usage)).ConfigureAwait(false);
            AddClaudeExchange(session, args, reply);
            return new HeadlessClaudeTurn(result.SessionId ?? id, reply.Length > 0 ? reply.ToString() : null, result.Usage, false);
        }
    }

    /// <summary>The pair into the session's history, tagged as the screen tags it; nothing for an empty reply.</summary>
    private static void AddClaudeExchange(LlmSession session, string prompt, StringBuilder reply)
    {
        if (reply.Length > 0)
        {
            session.History.AddUser(Claude.ClaudeText.HistoryUser(prompt));
            session.History.AddAssistant(Claude.ClaudeText.HistoryReply(reply.ToString()));
        }
    }

    private async Task WriteHeadlessPrefixAsync()
    {
        await _headlessOutput.WriteAsync(HeadlessReplyPrefix).ConfigureAwait(false);
        _headlessAtLineStart = false;
    }

    /// <summary>
    /// <c>/skills add &lt;source&gt; [--global] [--yes]</c> headless (2026-09-26): <see cref="Skills.SkillInstallFlow"/>
    /// over lines — the preview as plain lines, the rest as <c>[notice]</c> / <c>[warning]</c> / <c>[error]</c>.
    /// </summary>
    private async Task HeadlessSkillsAddAsync(string args, CancellationToken cancellationToken)
    {
        string rest = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: 2 } parts ? parts[1] : "";
        var host = new HeadlessSkillHost(this);
        if (rest.Length == 0)
        {
            host.Error(Skills.SkillInstallText.UsageError);
        }
        else
        {
            await new Skills.SkillInstallFlow(new Skills.SkillHub(_web.Fetcher)).RunAsync(rest, host, cancellationToken).ConfigureAwait(false);
        }

        foreach (string line in host.Lines)
        {
            await HeadlessNoticeLineAsync(line).ConfigureAwait(false);
        }
    }

    /// <summary>The install flow's host headless: every line kept, written once the flow is done.</summary>
    private sealed class HeadlessSkillHost(SidekickApp app) : Skills.ISkillInstallHost
    {
        public List<string> Lines { get; } = [];

        public bool Headless => true;

        public SkillRoots Roots => SkillRoots.For(app._settings, app._externalSkills);

        public bool External => app.EffectiveSettings.ExternalSkills;

        public bool SkillsEnabled => app.EffectiveSettings.AgentSkills;

        public Web.FetchOptions FetchOptions => WebAccess.Options(app.EffectiveSettings);

        public TimeProvider Time => app._time;

        public void Notice(string text) => Lines.Add("[notice] " + text);

        public void Warning(string text) => Lines.Add("[warning] " + text);

        public void Error(string text) => Lines.Add("[error] " + text);

        public void Preview(string markdown) => Lines.AddRange(markdown.Split('\n'));

        public Task<T> SpinAsync<T>(string label, Func<Task<T>> work) => work();

        public Task<int?> PickAsync(string title, IReadOnlyList<string> rows, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<Skills.SkillScope?> ConfirmAsync(Skills.SkillCandidate candidate, Skills.SkillSource source, Skills.SkillInstallCheck check, Skills.SkillScope? preselect, CancellationToken cancellationToken) =>
            Task.FromResult<Skills.SkillScope?>(null);

        // Nothing to rescan: headless's catalog is scanned at every turn.
        public void Rescan()
        {
        }

        public void Installed(Skills.SkillInstallResult result) => app._headlessSkillRecords?.Installed(result);
    }

    /// <summary>
    /// <c>claude_advisor</c> headless (2026-09-27): each tool Claude uses as a <c>[tool] Claude › …</c> line and the footer as a
    /// <c>[notice]</c>; the question and the answer are the generic call and result lines. Synchronous, as
    /// <see cref="OnHeadlessDiagnostic"/> is: the tool runs between the turn loop's awaited writes, never mid-line.
    /// </summary>
    private sealed class HeadlessAdvisorView(SidekickApp app) : Claude.IClaudeAdvisorView
    {
        public void Began(string question)
        {
        }

        public void Tool(string name, string detail) => app.HeadlessSyncLine("[tool] " + Claude.ClaudeText.ToolNote(name, detail));

        public void Answered(string answer, Claude.ClaudeEvent.Result result) =>
            app.HeadlessSyncLine("[notice] " + Claude.ClaudeText.Footer(result.CostUsd, result.Usage, Claude.ClaudeText.AdvisorName));
    }

    /// <summary>One line written at once, on a line of its own (the advisor's view).</summary>
    private void HeadlessSyncLine(string line)
    {
        if (!_headlessAtLineStart)
        {
            _headlessOutput.WriteLine();
        }

        _headlessOutput.WriteLine(line);
        _headlessOutput.Flush();
        _headlessAtLineStart = true;
    }

    /// <summary>
    /// The chosen Docker containers' switcher for a run (2026-10-02): the session's own <see cref="Docker.DockerSession"/> over the
    /// engine pipe the settings name (the <c>/docker</c> tools keep theirs), its readiness asked through the endpoint probe with
    /// the settings' key. A test's own when it hands one in.
    /// </summary>
    private Docker.IDockerServers BuildDockerServers() =>
        _dockerServers?.Invoke() ?? new Docker.DockerServerHost(
            new Docker.DockerSession(() => EffectiveSettings, _dockerClient, _time),
            (url, ct) => _probe.ProbeAsync(url, LlmEndpoint.KeyOf(EffectiveSettings), ct),
            _time);

    /// <summary>How long the exit waits for <c>Docker server stop on exit</c>: the stop's own timeout and its grace, then it gives up.</summary>
    private TimeSpan DockerExitBudget =>
        TimeSpan.FromSeconds(Math.Clamp(EffectiveSettings.DockerServerStopTimeoutSeconds, 0, AppSettingsData.MaxDockerServerStopTimeoutSeconds)) + Docker.DockerServerHost.StopGrace;

    /// <summary>
    /// <c>Docker server stop on exit</c> at the run's end (2026-10-02), under its own deadline rather than the app's token (which
    /// may be the one that ended the run): the names stopped; empty when off, none in use, or anything went wrong (logged).
    /// </summary>
    private async Task<IReadOnlyList<string>> StopDockerAtExitAsync(LlmSession session, TimeSpan deadline)
    {
        try
        {
            using var budget = new CancellationTokenSource(deadline);
            return await session.StopDockerAtExitAsync(EffectiveSettings, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn(ChatScreen.AppCategory, "Docker server stop on exit: " + Llm.Assistant.Explain(ex));
            return [];
        }
    }

    /// <summary>The exit's notice for a container it stopped (<c>Docker server stop on exit</c>). Pinned.</summary>
    public static string DockerServerExitNotice(string name) => Docker.DockerText.Glyph + " stopped " + name + " (Docker server stop on exit)";

    /// <summary>
    /// How long the window's close waits for <c>Docker server stop on exit</c> (2026-10-02): inside the 5 s Windows gives a
    /// console process after <c>CTRL_CLOSE_EVENT</c>, and long enough for the list and the stop request. The engine finishes a
    /// stop whose request was dropped (moby's <c>containerStop</c> runs under <c>context.WithoutCancel</c>), kill after the
    /// timeout included, so a container still stopping when this runs out stops all the same.
    /// </summary>
    public static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(3);

    /// <summary>The log line as the window's close asks the engine to stop the container in use. Pinned.</summary>
    public static string DockerServerClosingLine(string name) => Docker.DockerText.Glyph + " the window closed: asking Docker to stop " + name;

    /// <summary>
    /// The console window closing (2026-10-02, the user's report: its X button never stopped the container): Program's
    /// <c>SIGHUP</c> registration, which is <c>CTRL_CLOSE_EVENT</c> on Windows, calls this on the console's control thread,
    /// and the process lives until it returns — no <c>finally</c> of the run's will. With <c>Docker server stop on exit</c> on
    /// and a container in use, the stop is asked under <see cref="CloseBudget"/>. Blocks; never throws. Nothing when no run is live.
    /// </summary>
    public void ConsoleClosing()
    {
        if (_liveSession is not { } session || !EffectiveSettings.DockerServerStopOnExit || session.DockerInUse is not { } name)
        {
            return;
        }

        try
        {
            DiagnosticLog.Info(ChatScreen.AppCategory, DockerServerClosingLine(name));
            foreach (string stopped in Task.Run(() => StopDockerAtExitAsync(session, CloseBudget)).GetAwaiter().GetResult())
            {
                DiagnosticLog.Info(ChatScreen.AppCategory, DockerServerExitNotice(stopped));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn(ChatScreen.AppCategory, "Docker server stop on exit: " + Llm.Assistant.Explain(ex));
        }
    }

    /// <summary>A Docker switch's step, headless (2026-10-02): a notice line, written as it happens (the connect awaits nothing between).</summary>
    private void HeadlessDockerPhase(string phase)
    {
        if (!_headlessAtLineStart)
        {
            _headlessOutput.WriteLine();
        }

        _headlessOutput.WriteLine("[notice] " + Docker.DockerText.Glyph + " " + phase);
        _headlessAtLineStart = true;
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
        await using var embedded = _embeddedLlm?.Invoke();
        await using var claudeServer = _claudeServerFactory();
        _claudeServer = claudeServer;
        using var dockerServers = BuildDockerServers();
        using var session = new LlmSession(_probe, _contextProbe, _chatClientFactory, _time, _samplingProbe, embedded, claudeServer, ClaudeCliOffered, dockerServers);
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
        var screen = new ChatScreen(_console, _settings, () => EffectiveSettings, OverriddenBy, session, speech, new KeySource(_input ?? _console.Input), voice, PersonaFile.OpenInEditor, RenderScreen, _time, _geometry, _clipboard, mouse is null ? null : mouse.Capture, _copyToClipboard, clipboardImage: _clipboardImage, web: _web, setTitle: _setTitle, externalSkills: _externalSkills, holdWheel: mouse is null ? null : mouse.HoldWheel, splash: SplashImages.Source, editDraft: PersonaFile.EditAndWaitAsync, mcp: mcp, environment: _environment.System, logFile: _options.LogPath is { } logPath ? Path.GetFullPath(logPath) : null, comfyClient: _comfyClient, openImage: PersonaFile.OpenImage, claude: _claude, openViewer: _openViewer, viewPicture: _viewPicture, followViewer: _followViewer, haClient: _haClient, printSpooler: _printSpooler, perfSource: _perfSource, frames: _frames, dockerClient: _dockerClient, camera: _camera, liveView: _liveView, showShot: _showShot, openLogWindow: _openLogWindow, closeLogWindow: _closeLogWindow, closeViewer: _closeViewer);
        if (mouse is not null)
        {
            mouse.ModeChanged = screen.FlushConsole;
        }

        _screen = screen;
        _liveSession = session;
        try
        {
            return await screen.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _liveSession = null;
            _screen = null;
            if (mouse is not null)
            {
                mouse.ModeChanged = null;
            }
        }
    }

    /// <summary>
    /// The picture the viewer's keys moved to (2026-09-28, <see cref="Viewer.PictureWindow.Browsed"/>, which <c>Program</c> points
    /// here): handed to the running screen's strip (<see cref="ChatScreen.ViewerBrowsed"/>); nothing outside the interactive
    /// screen. Any thread.
    /// </summary>
    public void ViewerBrowsed(string path) => _screen?.ViewerBrowsed(path);

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

    /// <summary>A plain password typed into any <c>oracle.json</c> encrypted before the first screen or turn (2026-09-30), as <see cref="EncryptSqlPasswords"/> does for <c>sql.json</c>.</summary>
    private void EncryptOraclePasswords() => Oracle.OracleConfigFile.EncryptAll(_settings.StorageDirectory);

    /// <summary>A plain password typed into any <c>mysql.json</c> encrypted before the first screen or turn (2026-09-30), as for <c>sql.json</c> and <c>oracle.json</c>.</summary>
    private void EncryptMySqlPasswords() => MySql.MySqlConfigFile.EncryptAll(_settings.StorageDirectory);

    /// <summary>A plain runas password typed into any <c>unc.json</c> encrypted before the first screen or turn (2026-09-30), as <see cref="EncryptSqlPasswords"/> does for <c>sql.json</c>.</summary>
    private void EncryptUncPasswords() => Unc.UncConfigFile.EncryptAll(_settings.StorageDirectory);

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

    /// <summary>
    /// The production chat client for <paramref name="endpoint"/> (2026-09-27): the Claude API's own client on its host
    /// (<see cref="Llm.Anthropic.ClaudeApi.IsClaudeApi(Uri?)"/>), with the output cap and caching the effective settings
    /// hold at the connect; the Claude CLI's (<see cref="Claude.ClaudeCliChatClient"/>, 2026-09-30) over this run's one
    /// process on its sentinel; the OpenAI-compatible one everywhere else. Every connect and every <c>/botchat</c> link
    /// comes through here, so the provider is the URL's wherever it came from.
    /// </summary>
    private IChatClient DefaultChatClient(LlmEndpoint endpoint, LlmTimeouts timeouts)
    {
        if (Claude.ClaudeCliEndpoint.IsClaudeCli(endpoint.BaseUrl))
        {
            var host = _claudeServer ?? throw new InvalidOperationException(Claude.ClaudeCliText.NotRunningError);
            var context = new Claude.ClaudeCliContext(
                () => Claude.ClaudeExecutable.Locate(EffectiveSettings.ClaudeExecutable, _environment.System, File.Exists),
                ClaudeCliDirectory);
            return new Claude.ClaudeCliChatClient(endpoint, host, context, timeouts.Request);
        }

        if (!Llm.Anthropic.ClaudeApi.IsClaudeApi(endpoint.BaseUrl))
        {
            // The reasoning estimate is read at each request (2026-09-29): a change of the setting needs no reconnect.
            return new OpenAICompatibleChatClient(endpoint, timeouts.Request, reasoningEstimate: () => ReasoningEstimates.Resolve(EffectiveSettings));
        }

        var effective = EffectiveSettings;
        return new Llm.Anthropic.AnthropicChatClient(endpoint, timeouts.Request, effective.ClaudeApiMaxTokens, effective.ClaudeApiPromptCaching, time: _time);
    }

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
        // The preset row writes the four below it (2026-09-27): the first of their variables that is set shadows a pick.
        SettingsField.TtsVoicePreset => OverriddenBy(SettingsField.TtsVoice) ?? OverriddenBy(SettingsField.TtsVoice2) ?? OverriddenBy(SettingsField.TtsVoiceMix) ?? OverriddenBy(SettingsField.TtsSpeed),
        SettingsField.SttInterruptEchoGuard => _environment.SttInterruptEchoGuard is not null ? EnvironmentOverrides.InterruptEchoVariable : null,
        SettingsField.SttInterruptConfirmMs => _environment.SttInterruptConfirmMs is not null ? EnvironmentOverrides.InterruptConfirmVariable : null,
        SettingsField.TtsVoice2 => _environment.TtsVoice2 is not null ? EnvironmentOverrides.TtsVoice2Variable : null,
        SettingsField.TtsVoiceMix => _environment.TtsVoiceMix is not null ? EnvironmentOverrides.TtsMixVariable : null,
        SettingsField.SttWhisperModel => _environment.SttWhisperModel is not null ? EnvironmentOverrides.WhisperModelVariable : null,
        SettingsField.LlmReasoning => _environment.LlmReasoning is not null ? EnvironmentOverrides.LlmReasoningVariable : null,
        SettingsField.LlmSampling => _environment.LlmSampling is not null ? EnvironmentOverrides.LlmSamplingVariable : null,
        SettingsField.WorkingDirectory => _options.WorkingDirectory is not null ? SidekickOptions.CwdFlag : null,
        SettingsField.WebSearxngUrl => _environment.WebSearxngUrl is not null ? EnvironmentOverrides.SearxngUrlVariable : null,
        SettingsField.ShellCommandPolicy => _options.Yolo ? SidekickOptions.YoloFlag : _environment.ShellCommandPolicy is not null ? EnvironmentOverrides.CommandPolicyVariable : null,
        SettingsField.ShellPoliceOutsidePaths => _options.NoPolice ? SidekickOptions.NoPoliceFlag : _environment.ShellPolice is not null ? EnvironmentOverrides.ShellPoliceVariable : null,
        SettingsField.ShellPreferNative => _environment.ShellNative is not null ? EnvironmentOverrides.ShellNativeVariable : null,
        SettingsField.ObsidianVault => _environment.ObsidianVault is not null ? EnvironmentOverrides.ObsidianVaultVariable : null,
        SettingsField.ComfyUrl => _environment.ComfyUrl is not null ? EnvironmentOverrides.ComfyUrlVariable : null,
        SettingsField.ClaudeExecutable => _environment.ClaudeExecutable is not null ? EnvironmentOverrides.ClaudeExeVariable : null,
        SettingsField.ClaudePermissions => _environment.ClaudePermissions is not null ? EnvironmentOverrides.ClaudePermissionsVariable : null,
        SettingsField.ClaudeAdvisor => _environment.ClaudeAdvisor is not null ? EnvironmentOverrides.ClaudeAdvisorVariable : null,
        SettingsField.ClaudeApi => _environment.ClaudeApi is not null ? EnvironmentOverrides.ClaudeApiVariable : null,
        SettingsField.ClaudeApiKey => _environment.ClaudeApiKey is not null ? EnvironmentOverrides.ClaudeApiKeyVariable : null,
        SettingsField.ClaudeCliServer => _environment.ClaudeCliServer is not null ? EnvironmentOverrides.ClaudeCliServerVariable : null,
        SettingsField.DockerEnginePipe => _environment.DockerPipe is not null ? EnvironmentOverrides.DockerPipeVariable : null,
        _ => null,
    };

    /// <summary>The banner rule's width: <see cref="TranscriptRenderer.RuleWidth"/>, shared with the transcript's own rule (<c>/new</c>).</summary>
    private int Width() => TranscriptRenderer.RuleWidth(_console.Profile.Width);
}
