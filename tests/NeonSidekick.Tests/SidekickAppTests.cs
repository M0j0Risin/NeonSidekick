using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public partial class SidekickAppTests : IDisposable
{
    private const string LmStudioModels = "http://127.0.0.1:1234/v1/models";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new();
    private readonly AppSettings _settings;

    /// <summary>Every headless test runs over this stub, so nothing here ever touches the network.</summary>
    private readonly StubHttpMessageHandler _http = new();
    private readonly FakeChatClient _chat = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeRecognizer _recognizer = new();
    private readonly FakeVad _vad = new();
    private readonly FakeWakeWordDetector _wake = new();

    public SidekickAppTests()
    {
        _console.Profile.Width = 100;
        _settings = new AppSettings(_dir);
        // The model-written session title is the default (2026-09-18): the title request after an interactive script's first
        // turn would dequeue its next reply, so the fixture opts out, as ChatScreenTests does (headless never titles).
        _settings.Update(d => d.SessionNamingMode = "first-line");
        // delete is off in a fresh profile (2026-09-20); the headless scripts pin the full file rule, so the fixture opts it back on —
        // and File safe edits with it, since the rule's clause reads "into .trash" only under the setting (later on 2026-09-20).
        _settings.Update(d => { d.ToolsDisabled = []; d.FileSafeEdits = true; d.GitNativeTools = true; });   // Git native tools off by default since 2026-09-21: the headless turns opt in
    }

    public void Dispose()
    {
        _mcpServers.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _settings.Dispose();
        _console.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SidekickApp App(
        EnvironmentOverrides? env = null,
        TextReader? stdin = null,
        TextWriter? stdout = null,
        Func<IReadOnlyList<SmokeCheck>>? smoke = null,
        Func<LlmEndpoint, LlmTimeouts, IChatClient>? chat = null,
        Func<PcmFormat, IAudioPlayback>? playback = null,
        Func<SynthesizerRequest, ISpeechSynthesizer>? synth = null,
        Func<PcmFormat, IAudioCapture>? capture = null,
        Func<string, ISpeechRecognizer>? recognizer = null,
        Func<string, VadOptions, IVoiceActivityDetector>? vad = null,
        Func<int>? microphones = null,
        Func<string, string, IWakeWordDetector>? wake = null)
        => new(
            _console,
            _settings,
            env ?? EnvironmentOverrides.Empty,
            stdin,
            stdout,
            smoke,
            new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
            new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
            chat ?? ((_, _) => _chat),
            playback ?? (_ => new FakeAudioPlayback()),
            // No speech server by default, so a test that turns the saved switch on still never touches port 8880.
            synth ?? (_ => new FakeSynthesizer { Exists = false }),
            capture ?? (_ => _capture),
            recognizer ?? (_ => _recognizer),
            vad ?? ((_, _) => _vad),
            // Model downloads go through the same stub: nothing mapped, so a download is a refused connection.
            new HttpClient(_http),
            microphones ?? (() => 1),
            // Never a real Vosk model in tests: a bad directory would abort the process, not throw.
            wake ?? ((_, _) => _wake),
            // The manual clock, so the opening call's line is the same on every machine.
            new ManualTimeProvider(),
            setTitle: _titles.Add,
            externalSkills: Path.Combine(_dir, "agents-skills"),
            mcpTransport: _mcpServers.Transport,
            // The web over the same stub (2026-09-26, /skills add headless): every host public, nothing reaches the network.
            web: new NeonSidekick.Web.WebAccess(new HttpClient(_http), new FakeHeadlessBrowser(), new ManualTimeProvider(), (_, _) => Task.FromResult(new[] { IPAddress.Parse("140.82.112.9") })),
            // /claude over a script (2026-09-27): no test starts the real CLI.
            claude: _claudeCli);

    /// <summary>The MCP seam (2026-09-20): in-process pipe servers behind every session the app builds; nothing configured in the temp home, so nothing connects unless a test writes an mcp.json.</summary>
    private readonly InProcessMcpServers _mcpServers = new();

    /// <summary>The prompt as headless builds it: Agent skills on by default, no skill installed, so every prompt with tools ends with the skills block over an empty catalog (2026-09-16); no timer tool, so the tool rules lose their timer sentence (2026-09-20).</summary>
    private static string SkilledPrompt(bool speechOutput, IReadOnlyList<string>? memories, string? persona = null, string? operatingRules = null, string? voiceDirective = null, bool tools = true, bool web = false, bool files = true, AskLimits? ask = null, ProjectNotes? project = null, IReadOnlyList<Skill>? skills = null, bool sessions = true, bool mcp = false, bool git = true, bool shell = true) =>
        Assistant.SystemPrompt(speechOutput, memories, persona, operatingRules, voiceDirective, tools, web, files, ask, project, skills ?? [], sessions: sessions && tools, mcp: mcp && tools, timers: false, git: git && tools, shell: shell && tools, native: shell && tools);   // Shell prefer native tools on by default (2026-09-26)

    /// <summary>Every window title the app set (the <c>setTitle</c> seam): the interactive screen's launch, never headless.</summary>
    private readonly List<string> _titles = new();

    /// <summary>Both fake ggml files under the app's models directory, so a voice session over the fakes is ready without HTTP.</summary>
    private void FakeModelsPresent() => FakeModelFiles.WriteBoth(Path.Combine(_dir, "models"));

    private void ServerOn1234(params string[] models) =>
        _http.Map(LmStudioModels, HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson(models));

    /// <summary>
    /// <see cref="ServerOn1234"/> with the URL saved (2026-09-23): a blank URL walks the server, model and reasoning
    /// pickers at the app's start now, so the interactive scripts that only want a server to talk to name it.
    /// </summary>
    private void InteractiveServerOn1234(params string[] models)
    {
        ServerOn1234(models);
        _settings.Update(d => d.LlmUrl = "http://127.0.0.1:1234/v1");
    }

    private async Task<string> Headless(string input, EnvironmentOverrides? env = null, Func<LlmEndpoint, LlmTimeouts, IChatClient>? chat = null, SidekickOptions? options = null, int exitCode = 0)
    {
        var stdout = new StringWriter();
        var app = App(env, new StringReader(input), stdout, chat: chat);
        int code = await app.RunAsync((options ?? SidekickOptions.None) with { Headless = true }, CancellationToken.None);
        Assert.Equal(exitCode, code);
        Assert.Empty(_console.Output); // headless never touches the TUI console
        return stdout.ToString();
    }

    /// <summary>
    /// A plain password in any <c>sql.json</c> — here a profile that is not the loaded one — is encrypted when a session
    /// starts (later on 2026-09-23), and a diagnostic run (<c>--smoke</c>) leaves the file alone.
    /// </summary>
    [Fact]
    public async Task Startup_EncryptsThePlainSqlPasswords_OfEveryProfile_ButASmokeRunLeavesThem()
    {
        string other = Path.Combine(_dir, "profiles", "other");
        Directory.CreateDirectory(other);
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(other);
        File.WriteAllText(path, """{ "connections": { "a": { "server": "x", "user": "u", "password": "hunter2" } } }""");

        Assert.Equal(0, await App(smoke: () => [new SmokeCheck("a", true, "ok")]).RunAsync(SidekickOptions.None with { Smoke = true }, CancellationToken.None));
        Assert.Contains("hunter2", File.ReadAllText(path));

        // Headless (the smoke run above wrote to the fixture's console, so not through Headless(), which pins it empty).
        Assert.Equal(0, await App(stdin: new StringReader(""), stdout: new StringWriter()).RunAsync(SidekickOptions.None with { Headless = true }, CancellationToken.None));

        Assert.DoesNotContain("hunter2", File.ReadAllText(path));
        Assert.Contains("\"password\": \"dpapi:", File.ReadAllText(path));
    }

    [Fact]
    public void RenderBanner_ShowsTitleVersionAndRule_NoKeyHints()
    {
        App().RenderBanner();
        string output = _console.Output;
        Assert.Contains("N E O N   S I D E K I C K", output);
        Assert.Contains("N E O N   S I D E K I C K  v" + SidekickApp.Version, output);   // the version shares the title line
        Assert.Contains("─", output);
        Assert.DoesNotContain("ESC", output);   // the keys live in the pane's hint row
    }

    [Fact]
    public async Task Smoke_AllPass_ReturnsZero()
    {
        var app = App(smoke: () => new[]
        {
            new SmokeCheck("a", true, "ok"),
            new SmokeCheck("b", true, "ok"),
        });

        int code = await app.RunAsync(SidekickOptions.None with { Smoke = true }, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("SMOKE PASS", _console.Output);
        Assert.DoesNotContain("FAIL", _console.Output);
    }

    [Fact]
    public async Task AudioCheck_UsesThePlaybackFactory_AndExitsWithItsCode()
    {
        var fake = new FakeAudioPlayback();
        PcmFormat? requested = null;
        var app = App(playback: format => { requested = format; return fake; });

        int code = await app.RunAsync(SidekickOptions.None with { AudioCheck = true }, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(PcmFormat.Kokoro, requested);
        Assert.Equal(1, fake.Started);
        Assert.True(fake.Disposed);
        Assert.Contains(AudioCheck.DrainedLine, _console.Output);
        Assert.Contains("N E O N   S I D E K I C K", _console.Output);
    }

    [Fact]
    public async Task VoiceCheck_Heard_ExitsZero_ThroughTheFactories()
    {
        FakeModelsPresent();
        _vad.EndAfterBuffers = 2;
        _capture.OnStart = (c, _) => { c.Deliver(c.Silence(50), 1600); c.Deliver(c.Silence(50), 1600); return Task.CompletedTask; };
        PcmFormat? requested = null;
        var app = App(capture: f => { requested = f; return _capture; });

        int code = await app.RunAsync(SidekickOptions.None with { VoiceCheck = true }, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(PcmFormat.Whisper, requested);
        Assert.Contains("RESULT: heard \"hello\"", _console.Output);
        Assert.Contains("N E O N   S I D E K I C K", _console.Output);
        Assert.True(_capture.Disposed);
        Assert.Equal(Path.Combine(_dir, "models"), app.ModelsDirectory);
    }

    [Fact]
    public async Task VoiceCheck_RecognizerFails_ExitsOne()
    {
        FakeModelsPresent();
        _vad.EndAfterBuffers = 1;
        _recognizer.Fail = true;
        _capture.OnStart = (c, _) => { c.Deliver(c.Silence(50), 1600); return Task.CompletedTask; };

        int code = await App().RunAsync(SidekickOptions.None with { VoiceCheck = true }, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Contains(VoiceCheck.NoTranscriptLine, _console.Output);
    }

    [Fact]
    public async Task VoiceCheck_ModelDownloadRefused_ExitsOne_WithTheStatusLine()
    {
        int code = await App().RunAsync(SidekickOptions.None with { VoiceCheck = true }, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Contains("STT: model ggml-base.en.bin missing", _console.Output);
    }

    [Fact]
    public async Task Smoke_AnyFailure_ReturnsOneAndNamesIt()
    {
        var app = App(smoke: () => new[]
        {
            new SmokeCheck("native:libvosk.dll", false, "missing"),
            new SmokeCheck("b", true, "ok"),
        });

        int code = await app.RunAsync(SidekickOptions.None with { Smoke = true }, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Contains("SMOKE FAIL", _console.Output);
        Assert.Contains("native:libvosk.dll", _console.Output);
    }

    [Fact]
    public async Task Smoke_CheckRunnerThrows_IsAFailureNotACrash()
    {
        var app = App(smoke: () => throw new InvalidOperationException("kaboom"));
        int code = await app.RunAsync(SidekickOptions.None with { Smoke = true }, CancellationToken.None);
        Assert.Equal(1, code);
        Assert.Contains("kaboom", _console.Output);
    }

    [Fact]
    public async Task Headless_LogsItsTurnsToTheSessionStore_AndNewStartsAnother()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Hello").EnqueueText("Again").EnqueueText("Fresh");

        await Headless("hello there\nand again\n/new\nfresh\n");

        using var store = new NeonSidekick.Sessions.SessionStore(_settings.ProfileDirectory);
        var sessions = store.List(0);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(("fresh", 1), (sessions[0].Title, sessions[0].Turns));
        Assert.Equal(("hello there", 2, "llama"), (sessions[1].Title, sessions[1].Turns, sessions[1].Model));
        var record = store.Load(sessions[1].Id)!;
        Assert.Equal(new[] { ("hello there", "Hello"), ("and again", "Again") }, record.Turns.Select(t => (t.UserText, t.ReplyText)));
        Assert.Equal(2, NeonSidekick.Sessions.SessionHistory.FromJson(record.HistoryJson).Count(ConversationHistory.IsTurnStart));
    }

    [Fact]
    public async Task Headless_SessionLoggingOff_WritesNothing()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.SessionLogging = false);
        _chat.EnqueueText("Hello");

        await Headless("hello\n");

        Assert.False(File.Exists(Path.Combine(_settings.ProfileDirectory, NeonSidekick.Sessions.SessionStore.FileName)));
    }

    [Fact]
    public async Task Headless_StreamsARealReplyPerLine_AndExitsOnEof()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Hel", "lo").EnqueueText("World");

        string output = await Headless("hello\n\nworld\n");

        Assert.Contains(SidekickApp.VersionLine, output);
        Assert.Contains(SidekickApp.HeadlessHint, output);
        Assert.Contains("LLM: http://127.0.0.1:1234/v1 model=llama (probed http://127.0.0.1:1234/v1)", output);
        Assert.Contains("Neon: Hello", output);
        Assert.Contains("Neon: World", output);
        Assert.Equal(2, output.Split("Neon:").Length - 1); // the blank line produced no reply
        Assert.DoesNotContain("[error]", output);

        // The conversation accumulates: the second request carries the first exchange.
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.User }, _chat.Requests[1].Select(m => m.Role));
        Assert.Equal(SkilledPrompt(false, Array.Empty<string>(), web: true), _chat.Requests[1][0].Text);
        Assert.True(_chat.Disposed);
    }

    /// <summary>
    /// <c>/skills add</c> headless (2026-09-26): no server needed; without <c>--yes</c> the preview and the hint, nothing
    /// written; with it the skill installed where <c>--global</c> says; several skills listed as ids; the rest's usage error.
    /// </summary>
    [Fact]
    public async Task Headless_SkillsAdd_PreviewsWithoutYes_InstallsWithIt_AndNeedsNoServer()
    {
        _http.Map("https://codeload.github.com/anthropics/skills/zip/", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, SkillZip.Repo("pdf", "docx"), "application/zip")));

        string output = await Headless("/skills add anthropics/skills/pdf\n/skills add anthropics/skills/pdf --global --yes\n/skills add anthropics/skills\n/skills add\n");

        Assert.Contains("**pdf** — Does a thing. Use when asked.", output);
        Assert.Contains("[notice] " + SkillInstallText.HeadlessNeedsYes("pdf"), output);
        string folder = Path.Combine(Path.GetFullPath(_settings.GlobalSkillsDirectory), "pdf");
        Assert.Contains("[notice] " + SkillInstallText.InstalledNotice("pdf", SkillScope.Global, folder), output);
        Assert.True(File.Exists(Path.Combine(folder, "scripts", "run.py")));
        Assert.False(Directory.Exists(Path.Combine(_settings.ProfileSkillsDirectory, "pdf")));
        Assert.Contains("[notice]   anthropics/skills/docx", output);
        Assert.Contains("[error] " + SkillInstallText.UsageError, output);
        Assert.DoesNotContain(SidekickApp.HeadlessNoAssistantReply, output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>The model's thinking is the TUI's to show (2026-09-26): headless prints the reply alone, whatever LLM show thinking says.</summary>
    [Fact]
    public async Task Headless_PrintsNoThinking()
    {
        ServerOn1234("llama");
        _chat.Enqueue(
            new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new TextReasoningContent("Pondering.") }),
            new ChatResponseUpdate(ChatRole.Assistant, "Hello"));

        string output = await Headless("hi\n");

        Assert.Contains("Neon: Hello", output);
        Assert.DoesNotContain("Pondering", output);
    }

    /// <summary>Headless offers the git tools over the sandbox's repository like the screen (2026-09-20): a call is a generic tool line there, the rule in the prompt.</summary>
    [Fact]
    public async Task Headless_GitStatus_ReadsTheSandboxRepository_AndPrintsTheToolLine()
    {
        ServerOn1234("llama");
        string files = Path.Combine(_settings.ProfileDirectory, "files");
        GitAccessTests.Init(files);
        GitAccessTests.CommitFile(files, "notes.txt", "one\n", "first");
        _chat.Enqueue(FakeChatClient.Call("c1", "git_status", new Dictionary<string, object?>()));
        _chat.EnqueueText("Clean.");

        string output = await Headless("git status?\n");

        Assert.Contains("[tool] git_status {}", output);
        Assert.Contains("[tool] git_status -> On branch main: clean", output);
        Assert.Contains(Assistant.GitRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.Contains("git_commit", _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
        Assert.Contains("git_discard", _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));   // the fixture opts every tool on; a fresh profile keeps the two opt-ins off
    }

    /// <summary>
    /// Headless has no pane to ask on (2026-09-21): under <c>ask</c> a command whose prefix is not allowed is refused with the
    /// no-screen sentence; the tool and its rule are offered like the screen's. Since 2026-09-26 the run ends with the
    /// refused-commands notice and exit code 3.
    /// </summary>
    [Fact]
    public async Task Headless_RunCommand_UnderAsk_IsRefusedWithoutAScreen_AndExitsWith3()
    {
        ServerOn1234("llama");
        _chat.Enqueue(FakeChatClient.Call("c1", "run_command", new Dictionary<string, object?> { ["command"] = "echo hi", ["shell"] = "cmd" }));
        _chat.EnqueueText("Then not.");

        string output = await Headless("run it\n", exitCode: SidekickApp.HeadlessRefusedExitCode);

        Assert.Contains("[tool] run_command -> " + NeonSidekick.Shell.ShellText.NotAskable([]), output);
        Assert.EndsWith("[notice] " + NeonSidekick.Shell.ShellText.RefusedSummary(["echo hi"]) + Environment.NewLine, output);
        Assert.Equal(3, SidekickApp.HeadlessRefusedExitCode);
        Assert.Contains(Assistant.ShellRuleWithoutBridge, _chat.Requests[0][0].Text!, StringComparison.Ordinal);   // the bridge off by default (later on 2026-09-21)
        var offered = _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToList();
        Assert.Equal(offered.IndexOf("git_delete") + 1, offered.IndexOf("run_command"));
    }

    /// <summary>The variable says yolo (2026-09-21): the command runs headless, its result a generic tool line — the header, then the output flattened.</summary>
    [Fact]
    public async Task Headless_RunCommand_UnderYoloFromTheVariable_Runs()
    {
        ServerOn1234("llama");
        var env = new EnvironmentOverrides(n => n == EnvironmentOverrides.CommandPolicyVariable ? "yolo" : null);
        _chat.Enqueue(FakeChatClient.Call("c1", "run_command", new Dictionary<string, object?> { ["command"] = "echo hi", ["shell"] = "cmd" }));
        _chat.EnqueueText("It said hi.");

        string output = await Headless("run it\n", env);

        Assert.Contains("[tool] run_command -> exit 0 in 0.0 s (cmd): echo hi", output);
        Assert.Equal(EnvironmentOverrides.CommandPolicyVariable, App(env).OverriddenBy(SettingsField.ShellCommandPolicy));
        Assert.Null(App().OverriddenBy(SettingsField.ShellCommandPolicy));
        Assert.DoesNotContain("[notice] ", output);   // nothing refused: no summary, exit 0 (Headless' default)
    }

    /// <summary>
    /// <c>--yolo</c> (2026-09-26): the same as the variable for this launch, outranking a variable that says ask; the
    /// settings row names the flag; nothing is saved.
    /// </summary>
    [Fact]
    public async Task Headless_RunCommand_UnderTheYoloFlag_Runs_OverAVariableSayingAsk()
    {
        ServerOn1234("llama");
        var env = new EnvironmentOverrides(n => n == EnvironmentOverrides.CommandPolicyVariable ? "ask" : null);
        _chat.Enqueue(FakeChatClient.Call("c1", "run_command", new Dictionary<string, object?> { ["command"] = "echo hi", ["shell"] = "cmd" }));
        _chat.EnqueueText("It said hi.");
        var yolo = SidekickOptions.None with { Yolo = true };

        string output = await Headless("run it\n", env, options: yolo);

        Assert.Contains("[tool] run_command -> exit 0 in 0.0 s (cmd): echo hi", output);
        var app = App(env, new StringReader(""), new StringWriter());
        Assert.Equal(0, await app.RunAsync(yolo with { Headless = true }, CancellationToken.None));
        Assert.Equal(SidekickOptions.YoloFlag, app.OverriddenBy(SettingsField.ShellCommandPolicy));
        Assert.Equal("ask", _settings.Current.ShellCommandPolicy);   // never saved
    }

    /// <summary>The path police still stands under <c>--yolo</c> (the user's call, 2026-09-26), and what it refuses counts toward exit code 3.</summary>
    [Fact]
    public async Task Headless_UnderTheYoloFlag_ThePoliceStillRefuses_AndExitsWith3()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.ShellPoliceOutsidePaths = true);
        _chat.Enqueue(FakeChatClient.Call("c1", "run_command", new Dictionary<string, object?> { ["command"] = @"type C:\Windows\win.ini", ["shell"] = "cmd" }));
        _chat.EnqueueText("Refused.");

        string output = await Headless("read it\n", options: SidekickOptions.None with { Yolo = true }, exitCode: SidekickApp.HeadlessRefusedExitCode);

        Assert.Contains("[tool] run_command -> " + NeonSidekick.Shell.ShellText.OutsideHead, output);
        Assert.Contains("[notice] " + NeonSidekick.Shell.ShellText.RefusedSummary([@"type C:\Windows\win.ini"]), output);
    }

    /// <summary>
    /// <c>--yolo --no-police</c> (2026-09-26): the line the police refuses above runs; nothing is saved; the settings row
    /// names the flag, else the variable, else nothing.
    /// </summary>
    [Fact]
    public async Task Headless_UnderYoloAndNoPolice_AnOutsidePathRuns_AndNothingIsSaved()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.ShellPoliceOutsidePaths = true);
        _chat.Enqueue(FakeChatClient.Call("c1", "run_command", new Dictionary<string, object?> { ["command"] = @"type C:\Windows\win.ini", ["shell"] = "cmd" }));
        _chat.EnqueueText("Read it.");
        var loose = SidekickOptions.None with { Yolo = true, NoPolice = true };

        string output = await Headless("read it\n", options: loose);

        Assert.Contains(@"[tool] run_command -> exit 0 in 0.0 s (cmd): type C:\Windows\win.ini", output);
        Assert.DoesNotContain("[notice] ", output);
        Assert.True(_settings.Current.ShellPoliceOutsidePaths);   // never saved

        var flagged = App(null, new StringReader(""), new StringWriter());
        Assert.Equal(0, await flagged.RunAsync(loose with { Headless = true }, CancellationToken.None));
        Assert.Equal(SidekickOptions.NoPoliceFlag, flagged.OverriddenBy(SettingsField.ShellPoliceOutsidePaths));
        var variable = App(new EnvironmentOverrides(n => n == EnvironmentOverrides.ShellPoliceVariable ? "off" : null), new StringReader(""), new StringWriter());
        Assert.Equal(0, await variable.RunAsync(SidekickOptions.None with { Headless = true }, CancellationToken.None));
        Assert.Equal(EnvironmentOverrides.ShellPoliceVariable, variable.OverriddenBy(SettingsField.ShellPoliceOutsidePaths));
        Assert.Null(App().OverriddenBy(SettingsField.ShellPoliceOutsidePaths));
        // Shell prefer native tools has its variable too (later on 2026-09-26), no flag.
        var native = App(new EnvironmentOverrides(n => n == EnvironmentOverrides.ShellNativeVariable ? "off" : null), new StringReader(""), new StringWriter());
        Assert.Equal(0, await native.RunAsync(SidekickOptions.None with { Headless = true }, CancellationToken.None));
        Assert.Equal(EnvironmentOverrides.ShellNativeVariable, native.OverriddenBy(SettingsField.ShellPreferNative));
        Assert.Null(App().OverriddenBy(SettingsField.ShellPreferNative));
    }

    /// <summary>A background run headless (phase B): the start line, the exit as a <c>[notice]</c> at the loop top, and the seeded poll on the next turn.</summary>
    [Fact]
    public async Task Headless_RunCommand_Background_NoticesTheExit_AndSeedsThePoll()
    {
        ServerOn1234("llama");
        var env = new EnvironmentOverrides(n => n == EnvironmentOverrides.CommandPolicyVariable ? "yolo" : null);
        _chat.Enqueue(FakeChatClient.Call("c1", "run_command", new Dictionary<string, object?> { ["command"] = "echo bg", ["shell"] = "cmd", ["background"] = true, ["notify"] = true }));
        _chat.EnqueueText("Started.");
        _chat.EnqueueText("It ended.");
        var reader = new WaitingReader(["start it\n", "and?\n"], () => _chat.Requests.Count >= 2 ? Task.Delay(500) : Task.CompletedTask);

        var stdout = new StringWriter();
        var app = App(env, reader, stdout);
        Assert.Equal(0, await app.RunAsync(SidekickOptions.None with { Headless = true }, CancellationToken.None));
        string output = stdout.ToString();

        Assert.Matches("\\[tool\\] run_command -> started proc_[0-9a-f]{6} \\(cmd, pid [0-9]+\\): echo bg", output);
        Assert.Matches("\\[notice\\] proc_[0-9a-f]{6} exited 0 after [0-9.]+ s: echo bg", output);
        var call = _chat.Requests[2].SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Single(c => c.CallId.StartsWith(Assistant.PendingCallIdPrefix, StringComparison.Ordinal));
        Assert.Equal("process", call.Name);
        Assert.Contains("[tool] process -> proc_", output);
        Assert.Contains("— 1 new line", output);
    }

    /// <summary>A stdin whose lines come one at a time, each after <paramref name="before"/> has run: the second line waits for a background child to exit.</summary>
    private sealed class WaitingReader(IReadOnlyList<string> lines, Func<Task> before) : TextReader
    {
        private int _next;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_next >= lines.Count)
            {
                return null;
            }

            await before();
            return lines[_next++].TrimEnd('\n');
        }

        public override string? ReadLine() => _next < lines.Count ? lines[_next++].TrimEnd('\n') : null;
    }

    /// <summary>A prefix on the profile's list runs headless under <c>ask</c> (2026-09-21): the list is the only gate there.</summary>
    [Fact]
    public async Task Headless_RunCommand_UnderAsk_RunsAnAllowedPrefix()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.ShellCommandAllowed = ["echo"]);
        _chat.Enqueue(FakeChatClient.Call("c1", "run_command", new Dictionary<string, object?> { ["command"] = "echo hi", ["shell"] = "cmd" }));
        _chat.EnqueueText("It said hi.");

        string output = await Headless("run it\n");

        Assert.Contains("[tool] run_command -> exit 0 in 0.0 s (cmd): echo hi", output);
    }

    /// <summary>Headless connects the MCP servers after the LLM (2026-09-20): the status line, the tools offered like the screen's, a call printed as any tool's, the rule in the prompt.</summary>
    [Fact]
    public async Task Headless_McpServer_ConnectsAfterTheLlm_OffersItsTools_AndACallIsATooLine()
    {
        ServerOn1234("llama");
        File.WriteAllText(NeonSidekick.Mcp.McpConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "mcpServers": { "pipe": { "command": "pipe-server" } } }""");
        _settings.Update(d => d.McpServers = true);   // off by default since 2026-09-21: the profile opts in
        _chat.Enqueue(FakeChatClient.Call("c1", "pipe__echo", new Dictionary<string, object?> { ["text"] = "ping" }));
        _chat.EnqueueText("It said ping.");

        string output = await Headless("echo ping\n");

        Assert.Contains("LLM: http://127.0.0.1:1234/v1 model=llama (probed http://127.0.0.1:1234/v1)" + Environment.NewLine + "🔌 MCP: 1 server, 2 tools" + Environment.NewLine, output);
        Assert.Contains("[tool] pipe__echo {\"text\":\"ping\"}", output);
        Assert.Contains("[tool] pipe__echo -> echo: ping", output);
        Assert.Contains("[tool] pipe__echo -> echo: ping" + Environment.NewLine + "It said ping.", output);   // the Neon: prefix went out before the tool lines, as for any tool
        var offered = _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray();
        Assert.Equal(["pipe__echo", "pipe__fail"], offered[^2..]);
        Assert.Equal(SkilledPrompt(false, [], web: true, mcp: true), _chat.Requests[0][0].Text);
        Assert.Equal([("echo", """{"text":"ping"}""")], _mcpServers.Server("pipe").Calls);
    }

    [Fact]
    public async Task Headless_McpServerFails_IsALine_AndTheMasterOffConnectsNothing()
    {
        ServerOn1234("llama");
        File.WriteAllText(NeonSidekick.Mcp.McpConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "mcpServers": { "pipe": { "command": "pipe-server" } } }""");
        _settings.Update(d => d.McpServers = true);   // off by default since 2026-09-21: the profile opts in
        _mcpServers.Failing.Add("pipe");
        _chat.EnqueueText("hi");

        string output = await Headless("hi\n");

        Assert.Contains("🔌 MCP: no server connected" + Environment.NewLine + "🔌 MCP: pipe failed: no such command: pipe-server" + Environment.NewLine, output);
        Assert.DoesNotContain("pipe__", string.Join(",", _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name)));

        _mcpServers.Failing.Clear();
        _settings.Update(d => d.McpServers = false);
        _chat.EnqueueText("hi");
        string off = await Headless("hi\n");
        Assert.DoesNotContain("🔌", off);
        Assert.DoesNotContain("pipe__", string.Join(",", _chat.Options[1]!.Tools!.Cast<AIFunction>().Select(t => t.Name)));
    }

    [Fact]
    public async Task Headless_MemoryReachesThePrompt_AndATooledSaveShowsOnItsOwnLine()
    {
        ServerOn1234("llama");
        new MemoryStore(_settings.ProfileDirectory).Add("Their name is Chris.");
        _chat.Enqueue(FakeChatClient.Call("c1", "save_memory", new Dictionary<string, object?> { ["text"] = "They live in Leeds." }));
        _chat.EnqueueText("Noted.");
        _chat.EnqueueText("Leeds.");

        string output = await Headless("I live in Leeds\nwhere do I live?\n");

        Assert.Contains("[tool] save_memory -> remembered: They live in Leeds.", output);
        Assert.Contains("Noted.", output);
        Assert.Contains("Neon: Leeds.", output);
        Assert.Equal(11 + FileToolNames.All.Length + GitToolNames.All.Length + ShellToolNames.All.Length, _chat.Options[0]!.Tools!.Count);   // clock ×3, the files, the git tools (2026-09-20), the four web tools, the two memory tools, skill_editor, session_manager
        Assert.Equal(SkilledPrompt(false, new[] { "Their name is Chris." }, web: true), _chat.Requests[0][0].Text);
        // The list rides the opening recall_memory pair, the last of the three (2026-09-17), and the
        // pair is kept current: the save of the first turn is in the second turn's result, in place.
        Assert.DoesNotContain("Their name is Chris.", _chat.Requests[0][0].Text);
        Assert.Contains(MemoryPrompt.Directive, _chat.Requests[0][0].Text);
        var recall = Assert.Single(_chat.Requests[0][6].Contents.OfType<FunctionCallContent>());
        Assert.Equal((Assistant.OpeningMemoryCallId, RecallMemoryTool.ToolName), (recall.CallId, recall.Name));
        // The first turn's result as printed then (the fake holds the same content object, replaced in place since).
        Assert.Contains("[tool] recall_memory -> " + MemoryPrompt.Heading + "\n- Their name is Chris." + Environment.NewLine + "Neon: ", output);
        Assert.Equal(Assistant.OpeningMemoryCallId, Assert.Single(_chat.Requests[0][7].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.Equal(SkilledPrompt(false, new[] { "Their name is Chris.", "They live in Leeds." }, web: true), _chat.Requests[2][0].Text);
        Assert.Equal(Assistant.OpeningMemoryCallId, Assert.Single(_chat.Requests[2][7].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.Equal(MemoryPrompt.Heading + "\n- Their name is Chris.\n- They live in Leeds.", Assert.Single(_chat.Requests[2][7].Contents.OfType<FunctionResultContent>()).Result);
        Assert.Equal(1, _chat.Requests[2].Count(m => m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == Assistant.OpeningMemoryCallId)));
        // Headless has no pane and no slot: the plain-text rule whatever Transcript markdown (on by default) says.
        Assert.True(_settings.Current.TranscriptMarkdown);
        Assert.Contains(Assistant.PlainTextRule, _chat.Requests[0][0].Text!);
        Assert.DoesNotContain(Assistant.MarkdownRule, _chat.Requests[0][0].Text!);
        // Headless offers no timer tool (nothing could ring the alert), so the prompt names none (2026-09-20).
        Assert.DoesNotContain(StartTimerTool.ToolName, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.Contains(Assistant.ToolRulesWithoutTimers + " " + Assistant.FileRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.DoesNotContain(_chat.Options[0]!.Tools!.Cast<AIFunction>(), t => t.Name == StartTimerTool.ToolName);
        Assert.Equal(new[] { "Their name is Chris.", "They live in Leeds." }, new MemoryStore(_settings.ProfileDirectory).Snapshot());
    }

    [Fact]
    public async Task Headless_WebOff_OffersNoWebTool_AndTheRulesLoseTheWebSentence()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.WebTools = false);
        _chat.EnqueueText("Hi.");

        await Headless("hello\n");

        Assert.Equal(
            (string[])["get_current_time", "shift_date", "days_between", .. FileToolNames.All, .. GitToolNames.All, .. ShellToolNames.All, "save_memory", "recall_memory", "skill_editor", "session_manager"],
            _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray());
        Assert.DoesNotContain(Assistant.WebRule, _chat.Requests[0][0].Text!);
    }

    [Fact]
    public async Task Headless_ToolsDisabled_FiltersTheOfferedList_TheSameAsTheScreen()
    {
        // The /tools list (2026-09-19) reaches headless through PrepareTurn: read_file and get_current_time gone, the clock call not seeded, the file rule standing.
        ServerOn1234("llama");
        _settings.Update(d => d.ToolsDisabled = ["get_current_time", "read_file"]);
        _chat.EnqueueText("Hi.");

        string output = await Headless("hello\n");

        var offered = _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray();
        Assert.DoesNotContain("get_current_time", offered);
        Assert.DoesNotContain("read_file", offered);
        Assert.Contains("write_file", offered);
        Assert.Contains("shift_date", offered);
        var request = _chat.Requests[0];
        Assert.Equal(SkilledPrompt(false, [], web: true), request[0].Text);
        Assert.Contains(Assistant.FileRule, request[0].Text!);
        // The cwd and the memory pairs open the conversation, no clock ahead of them.
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, request.Select(m => m.Role));
        Assert.Equal(Assistant.OpeningCwdCallId, Assert.Single(request[3].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.Equal(Assistant.OpeningMemoryCallId, Assert.Single(request[5].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.Contains("Neon: Hi.", output);
    }

    [Fact]
    public async Task Headless_FilesOff_OffersNoFileTool_SeedsNoCwdCall_AndTheRulesLoseTheFileSentences()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.FileTools = false);
        _chat.EnqueueText("Hi.");

        string output = await Headless("hello\n");

        Assert.Equal(
            (string[])["get_current_time", "shift_date", "days_between", .. GitToolNames.All, .. ShellToolNames.All, "web_search", "web_fetch", "open_url", "save_memory", "recall_memory", "skill_editor", "session_manager"],
            _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray());
        var request = _chat.Requests[0];
        Assert.Equal(SkilledPrompt(false, [], web: true, files: false), request[0].Text);
        Assert.DoesNotContain(Assistant.FileRule, request[0].Text!);
        Assert.Contains(Assistant.WebRule, request[0].Text!);
        // The clock and the memory pairs open the conversation, no cwd between them.
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, request.Select(m => m.Role));
        Assert.Equal(Assistant.OpeningClockCallId, Assert.Single(request[3].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.Equal(Assistant.OpeningMemoryCallId, Assert.Single(request[5].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.DoesNotContain("[tool] get_working_directory", output);
        Assert.Contains("Neon: Hi.", output);
    }

    [Fact]
    public async Task Headless_Skills_TheCatalogTheToolsAndTheNotes_LikeTheScreen()
    {
        ServerOn1234("llama");
        string skills = Path.Combine(_settings.ProfileDirectory, "skills", "haiku");
        Directory.CreateDirectory(skills);
        File.WriteAllText(Path.Combine(skills, "SKILL.md"), "---\nname: haiku\ndescription: Writes haiku.\n---\nFive, seven, five.\n");
        string files = Path.Combine(_settings.ProfileDirectory, "files");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "AGENTS.md"), "The notes.");
        _chat.Enqueue(FakeChatClient.Call("c1", "load_skill", new Dictionary<string, object?> { ["name"] = "haiku" }));
        _chat.EnqueueText("Old pond.");

        string output = await Headless("a haiku\n");

        Assert.Equal(
            (string[])["get_current_time", "shift_date", "days_between", .. FileToolNames.All, .. GitToolNames.All, .. ShellToolNames.All, "web_search", "web_fetch", "open_url", "download_file", "save_memory", "recall_memory", "load_skill", "skill_editor", "session_manager"],
            _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray());
        var haiku = new Skill("haiku", "Writes haiku.", SkillScope.Profile, skills);
        Assert.Equal(Assistant.SystemPrompt(false, [], web: true, project: new ProjectNotes("AGENTS.md", "The notes."), skills: [haiku], sessions: true, timers: false, git: true, shell: true, native: true), _chat.Requests[0][0].Text);
        Assert.Contains("[tool] load_skill -> <skill_content name=\"haiku\">", output);
        Assert.True(ConversationHistory.IsSkillResult(Assert.Single(_chat.Requests[1][^1].Contents.OfType<FunctionResultContent>())));
        Assert.Contains("Old pond.", output);

        // Agent skills off: none of it.
        _settings.Update(d => d.AgentSkills = false);
        _chat.EnqueueText("Hi.");
        await Headless("hello\n");
        Assert.DoesNotContain("skill_editor", _chat.Options[2]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
        Assert.Equal(Assistant.SystemPrompt(false, [], web: true, sessions: true, timers: false, git: true, shell: true, native: true), _chat.Requests[2][0].Text);
    }

    [Fact]
    public async Task Headless_MemoryOff_OffersTheClockToolsOnly()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.Memory = false);
        _chat.EnqueueText("Hi.");

        await Headless("hello\n");

        Assert.Equal(
            (string[])["get_current_time", "shift_date", "days_between", .. FileToolNames.All, .. GitToolNames.All, .. ShellToolNames.All, "web_search", "web_fetch", "open_url", "download_file", "skill_editor", "session_manager"],
            _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray());
        Assert.Equal(SkilledPrompt(false, null, web: true), _chat.Requests[0][0].Text);
    }

    [Fact]
    public async Task Headless_LlmToolsOff_SendsNoToolsAndNoOpeningPairs()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.LlmOfferTools = false);
        _chat.EnqueueText("Hi.");

        string output = await Headless("hello\n");

        Assert.Null(_chat.Options[0]!.Tools);
        Assert.Equal([ChatRole.System, ChatRole.User], _chat.Requests[0].Select(m => m.Role));
        Assert.Equal(Assistant.DefaultPersona + " " + Assistant.PlainTextRule + "\n\n" + MemoryPrompt.DirectiveWithoutTool, _chat.Requests[0][0].Text);
        Assert.Contains("Neon: Hi.", output);
    }

    [Fact]
    public async Task Headless_PersonaFile_ReplacesTheIdentity_PerTurn()
    {
        ServerOn1234("llama");
        Directory.CreateDirectory(_settings.ProfileDirectory);
        string personaPath = Path.Combine(_settings.ProfileDirectory, PersonaFile.FileName);
        File.WriteAllText(personaPath, "You are Rex, a gruff pirate.");
        _chat.EnqueueText("Arr.");
        _chat.EnqueueText("Hello.");
        _chat.BeforeUpdate = (_, _) =>
        {
            if (_chat.Requests.Count == 1)
            {
                File.Delete(personaPath);
            }

            return Task.CompletedTask;
        };

        string output = await Headless("who are you?\nand now?\n");

        Assert.Contains("Neon: Arr.", output);
        Assert.Equal(SkilledPrompt(false, Array.Empty<string>(), "You are Rex, a gruff pirate.", web: true), _chat.Requests[0][0].Text);
        Assert.Equal(SkilledPrompt(false, Array.Empty<string>(), web: true), _chat.Requests[1][0].Text);
    }

    [Fact]
    public async Task Headless_OperataFile_ReplacesTheRules_PerTurn()
    {
        ServerOn1234("llama");
        Directory.CreateDirectory(_settings.ProfileDirectory);
        string operataPath = Path.Combine(_settings.ProfileDirectory, OperataFile.FileName);
        File.WriteAllText(operataPath, "Answer in haiku.");
        _chat.EnqueueText("Hi.");
        _chat.EnqueueText("Hello.");
        _chat.BeforeUpdate = (_, _) =>
        {
            if (_chat.Requests.Count == 1)
            {
                File.Delete(operataPath);
            }

            return Task.CompletedTask;
        };

        string output = await Headless("how do you answer?\nand now?\n");

        Assert.Contains("Neon: Hi.", output);
        Assert.Equal(SkilledPrompt(false, Array.Empty<string>(), null, "Answer in haiku.", web: true), _chat.Requests[0][0].Text);
        Assert.StartsWith(Assistant.DefaultPersona + "\n\nAnswer in haiku.\n\n", _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.Equal(SkilledPrompt(false, Array.Empty<string>(), web: true), _chat.Requests[1][0].Text);
    }

    [Fact]
    public async Task Headless_VocaliaFile_IsReadButNeverSpoken()
    {
        ServerOn1234("llama");
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(Path.Combine(_settings.ProfileDirectory, VocaliaFile.FileName), "Speak like a pirate.");
        _chat.EnqueueText("Hi.");

        string output = await Headless("hello\n");

        Assert.Contains("Neon: Hi.", output);
        Assert.Equal(SkilledPrompt(false, Array.Empty<string>(), web: true), _chat.Requests[0][0].Text);
        Assert.DoesNotContain("pirate", _chat.Requests[0][0].Text!);
        Assert.DoesNotContain(Assistant.VoiceDirective, _chat.Requests[0][0].Text!);
    }

    [Fact]
    public async Task Headless_AClockCall_PrintsTheGenericToolLines()
    {
        ServerOn1234("llama");
        _chat.Enqueue(FakeChatClient.Call("c1", "days_between", new Dictionary<string, object?> { ["from"] = "2026-09-11", ["to"] = "2026-12-25" }));
        _chat.EnqueueText("105 days.");

        string output = await Headless("how long until christmas?\n");

        Assert.Contains("[tool] days_between {", output);
        Assert.Contains("[tool] days_between -> 105 days (15 weeks) from 2026-09-11 to 2026-12-25", output);
        Assert.Contains("105 days.", output);
    }

    /// <summary>The picture travels in the history, so headless has view_image like every file tool: the generic lines, and the carrier on the next request.</summary>
    [Fact]
    public async Task Headless_ViewImage_PrintsTheGenericLines_AndTheCarrierFollowsTheResult()
    {
        ServerOn1234("llama");
        string folder = Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "square.bmp"), SmokeChecks.SolidBmp(4, 4));
        _chat.Enqueue(FakeChatClient.Call("c1", ViewImageTool.ToolName, new Dictionary<string, object?> { ["path"] = "square.bmp" }));
        _chat.EnqueueText("A pink square.");

        string output = await Headless("describe square.bmp\n");

        Assert.Contains("[tool] view_image {\"path\":\"square.bmp\"}", output);
        Assert.Contains("[tool] view_image -> square.bmp (4×4 image/png, ", output);
        Assert.Contains("A pink square.", output);
        var second = _chat.Requests[1];
        Assert.Equal(ChatRole.Tool, second[^2].Role);
        Assert.True(ConversationHistory.IsImageCarrier(second[^1]));
        Assert.Single(second[^1].Contents.OfType<DataContent>());
    }

    [Fact]
    public async Task Headless_HonoursTheToolIterationCap()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.LlmMaxToolIterations = 1);
        _chat.Enqueue(FakeChatClient.Call("c1", "days_between", new Dictionary<string, object?> { ["from"] = "2026-09-11", ["to"] = "2026-12-25" }));
        _chat.Enqueue(FakeChatClient.Call("c2", "days_between", new Dictionary<string, object?> { ["from"] = "2026-09-11", ["to"] = "2026-12-25" }));

        string output = await Headless("how long?\n");

        Assert.Contains("[error] Stopped after 1 tool iterations without a final answer.", output);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task Headless_FirstTurn_PrintsTheOpeningClockAndCwdLines_AboveThePrefix()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Hello.").EnqueueText("Again.");

        string output = await Headless("hi\nhi again\n");

        // The three pairs before "Neon: " (the memory last, 2026-09-17), so the prefix and the reply stay on one line; once per conversation.
        string nl = Environment.NewLine;
        string cwd = FileText.Describe(Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName), isDefault: true);
        Assert.Contains(
            "[tool] get_current_time {}" + nl + "[tool] get_current_time -> Friday 11 September 2026, 14:05 (Pacific Daylight Time, UTC-07:00)" + nl
            + "[tool] get_working_directory {}" + nl + "[tool] get_working_directory -> " + cwd + nl
            + "[tool] recall_memory {}" + nl + "[tool] recall_memory -> " + MemoryPrompt.NothingRemembered + nl + "Neon: Hello." + nl,
            output);
        Assert.Contains("Neon: Again.", output);
        Assert.Equal(6, output.Split("[tool]").Length - 1);
        var request = _chat.Requests[0];
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, request.Select(m => m.Role));
        Assert.Equal(Assistant.OpeningClockCallId, Assert.Single(request[3].Contents.OfType<FunctionResultContent>()).CallId);
        var cwdResult = Assert.Single(request[5].Contents.OfType<FunctionResultContent>());
        Assert.Equal(Assistant.OpeningCwdCallId, cwdResult.CallId);
        Assert.Equal(cwd, cwdResult.Result);
        var memoryResult = Assert.Single(request[7].Contents.OfType<FunctionResultContent>());
        Assert.Equal(Assistant.OpeningMemoryCallId, memoryResult.CallId);
        Assert.Equal(MemoryPrompt.NothingRemembered, memoryResult.Result);
    }

    [Fact]
    public async Task Headless_NoServer_SaysSo_AndAnswersEveryLineWithAnError()
    {
        string output = await Headless("hello\n");

        Assert.Contains(SidekickApp.HeadlessNoServerLine(ScanScope.Local), output);
        Assert.Contains("Neon: " + SidekickApp.HeadlessNoAssistantReply, output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Headless_ScanDisabled_SaysSo_AndAsksNothing()
    {
        // The server on :1234 is up; with LLM scan mode disabled and no URL, headless connects nothing and says why (2026-09-15).
        ServerOn1234("llama");
        _settings.Update(d => d.LlmScanMode = "disabled");

        string output = await Headless("hello\n");

        Assert.Contains(SidekickApp.HeadlessNoServerLine(ScanScope.Disabled), output);
        Assert.Contains("Neon: " + SidekickApp.HeadlessNoAssistantReply, output);
        Assert.Empty(_http.Requests);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Headless_NoServerLine_NamesThePortsAndTheVariable()
    {
        Assert.Equal("LLM: no server found on 127.0.0.1 ports 1234, 8000, 30000, 8080, 11434, 8888; set NEONSIDEKICK_LLM_URL.", SidekickApp.HeadlessNoServerLine(ScanScope.Local));
        Assert.Equal("[error] No LLM endpoint. Set NEONSIDEKICK_LLM_URL and restart.", SidekickApp.HeadlessNoAssistantReply);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Headless_ClearForgetsTheConversation()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("one").EnqueueText("two");

        string output = await Headless("first\n/clear\nsecond\n");

        Assert.Contains("Neon: (conversation cleared)", output);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, _chat.Requests[1].Select(m => m.Role));
    }

    [Fact]
    public async Task Headless_NewForgetsTheConversation_UnderItsOwnLine()
    {
        // No screen to keep or wipe: /new is /clear here, under the screen's notice (2026-09-16).
        ServerOn1234("llama");
        _chat.EnqueueText("one").EnqueueText("two");

        string output = await Headless("first\n/NEW\nsecond\n");

        Assert.Contains("Neon: " + ChatScreen.NewConversationNotice, output);
        Assert.DoesNotContain("(conversation cleared)", output);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, _chat.Requests[1].Select(m => m.Role));
    }

    [Fact]
    public async Task Headless_SplashForgetsTheConversation_AsClearDoes()
    {
        // No screen to wipe and no pane to draw a picture on: /splash is /clear here (2026-09-19).
        ServerOn1234("llama");
        _chat.EnqueueText("one").EnqueueText("two");

        string output = await Headless("first\n/splash\nsecond\n");

        Assert.Contains("Neon: (conversation cleared)", output);
        Assert.DoesNotContain(ChatScreen.NewConversationNotice, output);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, _chat.Requests[1].Select(m => m.Role));
    }

    [Fact]
    public async Task Headless_Compact_SummarisesTheOlderTurns_AndNoticesOnTheReplyLine()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.LlmCompactKeepRecent = 1);
        _chat.EnqueueText("one").EnqueueText("two");
        _chat.Enqueue(FakeChatClient.Text("A summary."), FakeChatClient.Usage(300, 20));
        _chat.EnqueueText("three");

        string output = await Headless("first\nsecond\n/compact the first question\nthird\n");

        Assert.Contains("Neon: (🗜️ compacted: 10 messages → 9 · 300 → 20 tokens)", output);
        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(ConversationCompactor.SummaryRequest("the first question"), _chat.Requests[2][^1].Text);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.Assistant, ChatRole.User }, _chat.Requests[3].Select(m => m.Role));
        Assert.StartsWith(ConversationCompactor.SummaryPreamble, _chat.Requests[3][1].Text);
        Assert.Contains("Neon: three", output);
    }

    [Fact]
    public async Task Headless_Compact_ShowSummary_PrintsTheSummarysLines_AsNotices()
    {
        // LLM compact show summary headless (2026-09-21): the detail lines follow the reply line, each a [notice] row.
        ServerOn1234("llama");
        _settings.Update(d => { d.LlmCompactKeepRecent = 1; d.LlmCompactShowSummary = true; });
        _chat.EnqueueText("one").EnqueueText("two");
        _chat.Enqueue(FakeChatClient.Text("A summary.\nOf two lines."), FakeChatClient.Usage(300, 20));
        _chat.EnqueueText("three");

        string output = await Headless("first\nsecond\n/compact\nthird\n");

        Assert.Contains("Neon: (🗜️ compacted: 10 messages → 9 · 300 → 20 tokens)" + Environment.NewLine + "[notice] A summary." + Environment.NewLine + "[notice] Of two lines." + Environment.NewLine + "[notice] (🗜️ 6 messages protected at the start)" + Environment.NewLine + "[notice] (🗜️ 2 messages protected at the end)" + Environment.NewLine, output);
    }

    [Fact]
    public async Task Headless_Compact_NothingOlder_AndAFailure_AreReplyLines()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.LlmCompactKeepRecent = 1);
        _chat.EnqueueText("one").EnqueueText("two");
        _chat.Enqueue(FakeChatClient.Text("half"));
        _chat.BeforeUpdate = (i, _) => _chat.Requests.Count == 3 && i == 0 ? throw new HttpRequestException("gone") : Task.CompletedTask;

        string output = await Headless("/compact\nfirst\nsecond\n/compact\n");

        Assert.Contains("Neon: " + CompactionText.NothingToCompact, output);
        Assert.Contains("Neon: 🗜️ Compact failed: HttpRequestException: gone", output);
        Assert.Equal(3, _chat.Requests.Count);
    }

    [Fact]
    public async Task Headless_AutoCompact_FiresPastTheShare_AsANoticeLine()
    {
        ServerOn1234("llama");
        _http.Map("http://127.0.0.1:1234/api/v0/models", HttpStatusCode.OK,
            "{\"object\":\"list\",\"data\":[{\"id\":\"llama\",\"object\":\"model\",\"state\":\"loaded\",\"max_context_length\":4096,\"loaded_context_length\":100}]}");
        _settings.Update(d => { d.LlmCompactKeepRecent = 1; d.LlmAutoCompactPercent = 20; });
        _chat.Enqueue(FakeChatClient.Text("one"), FakeChatClient.Usage(20, 5));
        _chat.EnqueueText("two");
        _chat.Enqueue(FakeChatClient.Text("A summary."), FakeChatClient.Usage(30, 4));
        _chat.EnqueueText("three");

        string output = await Headless("first\nsecond\nthird\n");

        Assert.Contains("[notice] (🗜️ auto-compacted at 25%: 10 messages → 9 · 30 → 4 tokens)", output);
        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(ConversationCompactor.SummaryInstruction, _chat.Requests[2][0].Text);
    }

    [Fact]
    public async Task Headless_ToolCompact_PrunesMidTurn_AsANoticeLine_AndTheAutoCompactPrunesTheKeptTurn()
    {
        ServerOn1234("llama");
        _http.Map("http://127.0.0.1:1234/api/v0/models", HttpStatusCode.OK,
            "{\"object\":\"list\",\"data\":[{\"id\":\"llama\",\"object\":\"model\",\"state\":\"loaded\",\"max_context_length\":4096,\"loaded_context_length\":100}]}");
        _settings.Update(d => d.LlmAutoCompactPercent = 20);   // the default type: prune
        string folder = Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "big.txt"), new string('x', 600));
        _chat.Enqueue(FakeChatClient.Call("c1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "big.txt" }), FakeChatClient.Usage(10, 5));
        _chat.Enqueue(FakeChatClient.Call("c2", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "big.txt" }), FakeChatClient.Usage(30, 5));
        _chat.Enqueue(FakeChatClient.Call("c3", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "big.txt" }), FakeChatClient.Usage(31, 5));
        _chat.EnqueueText("read thrice");
        _chat.EnqueueText("later");

        string output = await Headless("first\nsecond\n");

        Assert.Contains("[notice] (✂️ context at 35%: pruned 1 tool result from this turn)", output);
        Assert.Contains("[notice] (✂️ context at 36%: pruned 1 tool result from this turn)", output);
        Assert.Contains("read thrice", output);
        Assert.Equal(5, _chat.Requests.Count);
        static string ResultOf(IReadOnlyList<ChatMessage> request, string callId) =>
            (string)request.Where(m => m.Role == ChatRole.Tool).SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == callId).Result!;
        Assert.Equal("(a 618-character result, pruned by /compact)", ResultOf(_chat.Requests[2], "c1"));
        Assert.EndsWith(new string('x', 600), ResultOf(_chat.Requests[2], "c2"));
        Assert.Equal("(a 618-character result, pruned by /compact)", ResultOf(_chat.Requests[3], "c2"));
        Assert.EndsWith(new string('x', 600), ResultOf(_chat.Requests[3], "c3"));
        // The second message: the automatic compact (36 % in use) has nothing older (one turn, two kept) and nothing left to stub — c3 is the last iteration and stays — so it is silent.
        Assert.DoesNotContain("auto-compacted", output);
        Assert.EndsWith(new string('x', 600), ResultOf(_chat.Requests[4], "c3"));
    }

    [Fact]
    public async Task Headless_ToolActivityAndNotices_GetTheirOwnBracketedLines()
    {
        ServerOn1234("llama");
        _chat.Enqueue(FakeChatClient.Call("c1", "nope"));
        _chat.EnqueueText("after");
        _chat.EnqueueText("par", "tial");

        string output = await Headless("one\ntwo\n");

        Assert.Contains("[tool] nope {}", output);
        Assert.Contains("[tool] nope -> Error: unknown tool 'nope'.", output);
        Assert.Contains("Neon: " + Environment.NewLine + "[tool]", output); // the bracketed line starts on its own line
        Assert.Contains("after", output);
        Assert.Contains("Neon: partial", output);
    }

    [Fact]
    public async Task Headless_ServerErrorMidStream_BecomesAnErrorLine_AndTheLoopSurvives()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("par", "tial").EnqueueText("fine");
        _chat.ThrowAt = 1;

        string output = await Headless("one\ntwo\n");

        // Not asserted as adjacent lines: DiagnosticLog is process-wide, and a Warning raised by a
        // test running in parallel can be forwarded between these two.
        Assert.Contains("Neon: par" + Environment.NewLine, output);
        Assert.Contains("[error] Model error: HttpRequestException: scripted failure", output);
        Assert.Contains("Neon: fine", output);
    }

    [Fact]
    public async Task Headless_WarningsGoToStdoutAsLines_AndTheEchoIsRestored()
    {
        // A configured URL that does not answer produces a Warning from the probe.
        var env = new EnvironmentOverrides(n => n == EnvironmentOverrides.LlmUrlVariable ? "http://127.0.0.1:9" : null);
        bool echoBefore = DiagnosticLog.EchoToConsole;

        string output = await Headless("/exit\n", env);

        Assert.Contains("[Llm] http://127.0.0.1:9/v1 did not answer /v1/models", output);
        Assert.Contains("LLM: http://127.0.0.1:9/v1 model=local-model (configured, not answering)", output);
        Assert.Equal(echoBefore, DiagnosticLog.EchoToConsole);
    }

    [Fact]
    public async Task Headless_ChatClientFactoryThrows_IsAnErrorNotACrash()
    {
        ServerOn1234("llama");
        string output = await Headless("hi\n", chat: (_, _) => throw new InvalidOperationException("no client"));

        Assert.Contains("[App] Could not create the chat client: InvalidOperationException: no client", output);
        Assert.Contains("Neon: " + SidekickApp.HeadlessNoAssistantReply, output);
    }

    [Fact]
    public async Task Headless_UsesTheResolvedTimeouts_ForTheClient()
    {
        ServerOn1234("llama");
        _settings.Update(d => { d.LlmRequestTimeoutSeconds = 100; d.LlmTurnTimeoutSeconds = 90; });
        LlmTimeouts? seen = null;

        await Headless("/exit\n", chat: (_, t) => { seen = t; return _chat; });

        Assert.Equal(new LlmTimeouts(TimeSpan.FromSeconds(75), TimeSpan.FromSeconds(90)), seen);
    }

    [Fact]
    public async Task Headless_QuitCommandExits()
    {
        string output = await Headless("/exit\nnever read\n");
        Assert.DoesNotContain("never read", output);
    }

    // ── Interactive ─────────────────────────────────────────────────────────

    private void PushLine(string text)
    {
        _console.Input.PushText(text);
        _console.Input.PushKey(Keys.Enter);
    }

    /// <summary>
    /// Runs the chat screen over the pushed keys; a scripted console running dry exits 0 on its
    /// own. Wide, because the spinner's clean-up leaves blanking spaces in a TestConsole (the
    /// cursor codes are stripped) and the line after it must not wrap.
    /// </summary>
    private async Task<string> InteractiveAsync(EnvironmentOverrides? env = null, SidekickOptions? options = null, Func<LlmEndpoint, LlmTimeouts, IChatClient>? chat = null, CancellationToken cancellationToken = default)
    {
        _console.Interactive();
        _console.Profile.Width = 240;
        int code = await App(env, chat: chat).RunAsync(options ?? SidekickOptions.None, cancellationToken);
        Assert.Equal(0, code);
        return _console.Output;
    }

    /// <summary>Ctrl+Q used to quit; now neither ESC nor Ctrl+Q ends the session, only /exit does.</summary>
    [Fact]
    public async Task Interactive_EscapeAndCtrlQ_DoNotExit_QuitDoes()
    {
        _console.Input.PushKey(Keys.Escape);
        _console.Input.PushKey(Keys.Ctrl(ConsoleKey.Q));
        PushLine("still here");
        PushLine("/exit");
        PushLine("never sent");

        string output = await InteractiveAsync();

        Assert.DoesNotContain("ESC = ", output);   // ESC on the line prints nothing; the hint row names the key
        Assert.Contains("› still here", output);
        Assert.Contains(ChatScreen.NoAssistantError, output);   // no server: the message was still handled
        Assert.DoesNotContain("› never sent", output);
        Assert.DoesNotContain("Ctrl+Q", output);
    }

    [Fact]
    public async Task Interactive_NoInputAvailable_ExitsCleanly()
    {
        string output = await InteractiveAsync();
        Assert.Contains(LlmSession.NoServerLine(ScanScope.Local), output);
    }

    [Fact]
    public async Task Interactive_SetsTheWindowTitle_ToTheLoadedProfile()
    {
        PushLine("/exit");
        await InteractiveAsync();
        Assert.Equal(new[] { ChatScreen.DefaultWindowTitle }, _titles);
    }

    [Fact]
    public async Task Headless_NeverSetsTheWindowTitle()
    {
        await Headless("");
        Assert.Empty(_titles);
    }

    [Fact]
    public async Task Interactive_TypeEnterReply_ThenQuit()
    {
        InteractiveServerOn1234("llama");
        _chat.EnqueueText("Hi ", "there");
        PushLine("hello");
        PushLine("/exit");

        string output = await InteractiveAsync();

        Assert.Contains("LLM: http://127.0.0.1:1234/v1 model=llama (first listed)", output);
        Assert.Contains("› hello", output);
        Assert.Contains("● Hi there", output);
        Assert.Single(_chat.Requests);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, _chat.Requests[0].Select(m => m.Role));
        Assert.True(_chat.Disposed);
    }

    [Fact]
    public async Task Interactive_EscapeMidStream_CommitsThePartial_SaysCancelled_AndTheLoopContinues()
    {
        InteractiveServerOn1234("llama");
        _chat.EnqueueText("par", "tial").EnqueueText("next");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 1)
            {
                _console.Input.PushKey(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("one");
        PushLine("two");
        PushLine("/exit");

        string output = await InteractiveAsync();

        Assert.Contains("● par\n  · " + ChatScreen.CancelledNotice, output);
        Assert.Contains("● next", output);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.User }, _chat.Requests[1].Select(m => m.Role));
        Assert.Equal("par", _chat.Requests[1][8].Text);   // after the three opening pairs
    }

    /// <summary>The app token (Ctrl+C in Program.cs) mid-stream: the partial reply is committed and the session leaves.</summary>
    [Fact]
    public async Task Interactive_AppTokenMidStream_Exits()
    {
        InteractiveServerOn1234("llama");
        using var shutdown = new CancellationTokenSource();
        _chat.EnqueueText("par", "tial");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 1)
            {
                shutdown.Cancel();
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("one");
        PushLine("never sent");

        string output = await InteractiveAsync(cancellationToken: shutdown.Token);

        Assert.Contains(ChatScreen.CancelledNotice, output);
        Assert.Single(_chat.Requests);
        Assert.DoesNotContain("› never sent", output);
    }

    [Fact]
    public async Task Interactive_Clear_ForgetsTheConversation()
    {
        InteractiveServerOn1234("llama");
        _chat.EnqueueText("one").EnqueueText("two");
        PushLine("a");
        PushLine("/clear");
        PushLine("b");
        PushLine("/exit");

        string output = await InteractiveAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, _chat.Requests[1].Select(m => m.Role));

        // The banner again (start, then /clear); the wipe is the feedback, no notice. The settings
        // name no URL, so the discovered endpoint's LLM: line follows the banner both times.
        Assert.Equal(2, output.Split("N E O N   S I D E K I C K").Length - 1);
        Assert.Equal(2, output.Split("LLM: ").Length - 1);
        Assert.DoesNotContain("(conversation cleared)", output);
        Assert.True(output.LastIndexOf("N E O N   S I D E K I C K", StringComparison.Ordinal) < output.LastIndexOf("› b", StringComparison.Ordinal));
    }

    /// <summary>
    /// The interactive screen starts on a wiped terminal (the erase sequence precedes the banner);
    /// the diagnostic modes do not, because their output is captured. <c>TestConsole</c> appends
    /// the sequence to its output rather than truncating it, which is what makes this observable.
    /// </summary>
    [Fact]
    public async Task Interactive_StartsOnAClearedScreen_SmokeDoesNot()
    {
        PushLine("/exit");
        string output = await InteractiveAsync();
        int erase = output.IndexOf(EraseDisplay, StringComparison.Ordinal);
        Assert.True(erase >= 0, "no erase sequence in the interactive output");
        Assert.True(erase < output.IndexOf("N E O N   S I D E K I C K", StringComparison.Ordinal));

        using var smokeConsole = new TestConsole();
        var app = new SidekickApp(smokeConsole, _settings, EnvironmentOverrides.Empty, smokeChecks: () => new[] { new SmokeCheck("a", true, "ok") });
        Assert.Equal(0, await app.RunAsync(SidekickOptions.None with { Smoke = true }, CancellationToken.None));
        Assert.DoesNotContain(EraseDisplay, smokeConsole.Output);
        Assert.Contains("N E O N   S I D E K I C K", smokeConsole.Output);
    }

    /// <summary>ANSI "erase in display", the head of what <c>IAnsiConsole.Clear</c> writes.</summary>
    private const string EraseDisplay = "[2J";

    [Fact]
    public async Task Interactive_ThePersonaRead_IsNotEchoedToStdout_AndTheEchoIsRestored()
    {
        // The turn reads persona.md and PersonaFile logs an Info line for it; before the fix (when
        // the old status rows read it too) the line went to stdout above the banner.
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(Path.Combine(_settings.ProfileDirectory, PersonaFile.FileName), "You are Rex, a gruff pirate.");
        InteractiveServerOn1234("llama");
        _chat.EnqueueText("Arr.");
        bool echoBefore = DiagnosticLog.EchoToConsole;
        var echoAtPersonaLoad = new List<bool>();
        Action<DiagnosticEvent> record = evt =>
        {
            if (evt.Category == PersonaFile.Category && evt.Message.StartsWith("Persona loaded", StringComparison.Ordinal))
            {
                echoAtPersonaLoad.Add(DiagnosticLog.EchoToConsole);
            }
        };
        DiagnosticLog.Emitted += record;
        try
        {
            PushLine("hello");
            PushLine("/exit");
            string output = await InteractiveAsync();

            Assert.Contains("● Arr.", output);                        // the turn ran
            Assert.NotEmpty(echoAtPersonaLoad);                       // and read the file
            Assert.All(echoAtPersonaLoad, echo => Assert.False(echo)); // and nothing echoed to stdout
            Assert.Equal(echoBefore, DiagnosticLog.EchoToConsole);
        }
        finally
        {
            DiagnosticLog.Emitted -= record;
        }
    }

    [Fact]
    public async Task Interactive_HelpAndUnknownAndLaterCommands()
    {
        InteractiveServerOn1234("llama");
        PushLine("/help");
        PushLine("/bogus now");
        PushLine("/stt");
        PushLine("/wake");
        PushLine("/exit");

        string output = await InteractiveAsync();

        Assert.Contains("  · Commands:", output);
        Assert.True(_settings.Current.SttWake);   // /wake toggled the saved switch; voice was on, so it re-probed
        Assert.Equal(2, output.Split("STT: model ggml-base.en.bin missing").Length - 1);
        Assert.Contains("/settings", output);
        Assert.Contains("  ✗ " + ChatScreen.UnknownCommandError("/bogus"), output);
        Assert.Contains("STT: model ggml-base.en.bin missing", output);   // /stt toggled the switch on; the stub refuses the download
        Assert.True(_settings.Current.SttInput);
        Assert.DoesNotContain("TTS: ", output);   // speech output is off by default, so nothing to say
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Interactive_SpeechOn_NoServer_PrintsTheWarning()
    {
        InteractiveServerOn1234("llama");
        _settings.Update(d => { d.TtsOutput = true; d.TtsSource = "http"; });
        PushLine("/exit");

        string output = await InteractiveAsync();

        Assert.Contains("  ! " + SpeechSession.NoServerLine("http://localhost:8880/v1", "No connection could be made because the target machine actively refused it"), output);
    }

    [Fact]
    public async Task Interactive_SpeechServerFound_ProbesOnce_AndPrintsNothingUnderThePanel()
    {
        InteractiveServerOn1234("llama");
        _settings.Update(d => { d.TtsOutput = true; d.TtsSource = "http"; });
        PushLine("/exit");
        _console.Interactive();
        _console.Profile.Width = 240;
        var synth = new FakeSynthesizer();

        int code = await App(synth: _ => synth).RunAsync(SidekickOptions.None, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(1, synth.ListCalls);
        Assert.DoesNotContain("TTS: ", _console.Output);   // a successful probe prints nothing; /tts names the server and the voice
    }

    /// <summary>A fresh install: speech output is off by default, so nothing is probed and no TTS line is printed.</summary>
    [Fact]
    public async Task Interactive_FreshInstall_SpeechIsOff_AndNeverProbes()
    {
        InteractiveServerOn1234("llama");
        PushLine("/exit");
        _console.Interactive();
        _console.Profile.Width = 240;
        var synth = new FakeSynthesizer();

        int code = await App(synth: _ => synth).RunAsync(SidekickOptions.None, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.False(_settings.Current.TtsOutput);
        Assert.DoesNotContain("TTS: ", _console.Output);
        Assert.Equal(0, synth.ListCalls);
    }

    [Fact]
    public async Task Headless_NeverConstructsSpeech()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("pong");
        var stdout = new StringWriter();
        var app = App(
            stdin: new StringReader("ping\n"),
            stdout: stdout,
            playback: _ => throw new InvalidOperationException("no audio in headless"),
            synth: _ => throw new InvalidOperationException("no speech in headless"),
            capture: _ => throw new InvalidOperationException("no microphone in headless"),
            recognizer: _ => throw new InvalidOperationException("no whisper in headless"),
            microphones: () => throw new InvalidOperationException("no device probe in headless"),
            wake: (_, _) => throw new InvalidOperationException("no wake word in headless"));

        int code = await app.RunAsync(SidekickOptions.None with { Headless = true }, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("pong", stdout.ToString());
        Assert.DoesNotContain("TTS", stdout.ToString());
        Assert.DoesNotContain("Voice", stdout.ToString());
    }

    [Fact]
    public async Task Interactive_NoServer_SaysSo_AndAnswersWithAnError()
    {
        PushLine("hi");
        PushLine("/exit");

        string output = await InteractiveAsync();

        Assert.Contains("✗ " + LlmSession.NoServerLine(ScanScope.Local), output);   // no indent asserted: spinner residue precedes it
        Assert.Contains(ChatScreen.NoServerHint, output);
        Assert.Contains("  ✗ " + ChatScreen.NoAssistantError, output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Interactive_WarningDuringTheReply_LandsAfterTheTokenNotInsideIt()
    {
        InteractiveServerOn1234("llama");
        string category = "Cat" + Guid.NewGuid().ToString("N")[..6];
        _chat.EnqueueText("par", "tial");
        _chat.BeforeUpdate = (i, _) =>
        {
            if (i == 1)
            {
                DiagnosticLog.Warn(category, "careful");
            }

            return Task.CompletedTask;
        };
        PushLine("one");
        PushLine("/exit");

        string output = await InteractiveAsync();

        Assert.Contains("● partial", output);
        Assert.Contains($"  [{category}] careful", output);
        Assert.True(output.IndexOf("● partial", StringComparison.Ordinal) < output.IndexOf($"[{category}]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Interactive_ServerErrorMidStream_IsAnErrorLine_AndTheLoopSurvives()
    {
        InteractiveServerOn1234("llama");
        _chat.EnqueueText("par", "tial").EnqueueText("fine");
        _chat.ThrowAt = 1;
        PushLine("one");
        PushLine("two");
        PushLine("/exit");

        string output = await InteractiveAsync();

        Assert.Contains("  ✗ Model error: HttpRequestException: scripted failure", output);
        Assert.Contains("● fine", output);
        Assert.Equal(2, _chat.Requests.Count);
    }

    [Fact]
    public async Task Interactive_TypeAheadDuringAReply_BecomesTheNextMessage()
    {
        InteractiveServerOn1234("llama");
        _chat.EnqueueText("first reply").EnqueueText("second reply");
        _chat.BeforeUpdate = async (i, _) =>
        {
            if (i == 0)
            {
                PushLine(_chat.Requests.Count == 1 ? "typed early" : "/exit");
                await Task.Delay(40, CancellationToken.None);   // let the watcher buffer it
            }
        };
        PushLine("first");

        string output = await InteractiveAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("typed early", _chat.Requests[1][^1].Text);
        Assert.Contains("› typed early", output);
        Assert.Contains("● second reply", output);
    }

    [Fact]
    public async Task Interactive_ModelPick_SavesAndReconnectsWithTheNewModel()
    {
        InteractiveServerOn1234("alpha", "beta");
        var endpoints = new List<LlmEndpoint>();
        var clients = new List<FakeChatClient>();
        Func<LlmEndpoint, LlmTimeouts, IChatClient> factory = (e, _) =>
        {
            endpoints.Add(e);
            var c = new FakeChatClient().EnqueueText("ok");
            clients.Add(c);
            return c;
        };
        PushLine("hello");
        PushLine("/model");
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Enter);
        PushLine("again");
        PushLine("/exit");

        string output = await InteractiveAsync(chat: factory);

        Assert.Equal(new[] { "alpha", "beta" }, endpoints.Select(e => e.ModelId));
        Assert.Equal("beta", _settings.Current.LlmModel);
        Assert.True(clients[0].Disposed);
        Assert.Contains("model=beta (configured)", output);
        // The conversation survived the reconnect: the second client saw the first exchange.
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.User }, clients[1].Requests[0].Select(m => m.Role));
    }

    [Fact]
    public async Task Interactive_ReasoningPick_SavesAndReconnectsQuietly_WithTheNewEffort()
    {
        ServerOn1234("alpha");
        // Configured, so the panel names the endpoint and a quiet reconnect has nothing to add.
        _settings.Update(d => { d.LlmUrl = "http://127.0.0.1:1234"; d.LlmModel = "alpha"; });
        var clients = new List<FakeChatClient>();
        Func<LlmEndpoint, LlmTimeouts, IChatClient> factory = (_, _) =>
        {
            var c = new FakeChatClient().EnqueueText("ok");
            clients.Add(c);
            return c;
        };
        PushLine("hello");
        PushLine("/reasoning");
        _console.Input.PushKey(Keys.Down);   // none (the default, where the cursor opens) -> low
        _console.Input.PushKey(Keys.Enter);
        PushLine("again");
        PushLine("/reasoning xhigh");
        PushLine("/reasoning lots");
        PushLine("/exit");

        string output = await InteractiveAsync(chat: factory);

        Assert.Equal("xhigh", _settings.Current.LlmReasoning);
        Assert.Equal(3, clients.Count);   // the pick and the direct form each reconnected; the bad word did not
        Assert.True(clients[0].Disposed);
        Assert.Contains(SettingsMenu.ReasoningTitle, output);
        Assert.Contains("· 🖥️ LLM reasoning: low", output);
        Assert.Contains("· 🖥️ LLM reasoning: xhigh", output);
        Assert.Contains("✗ " + SettingsMenu.ReasoningLevelError, output);
        Assert.DoesNotContain("LLM: ", output);   // the reconnects are quiet and the panel names the endpoint: the notice is the feedback

        // The first client asked for the default; the one built after the pick carries the new effort.
        Assert.Equal(ReasoningEffort.None, clients[0].Options[0]!.Reasoning!.Effort);
        Assert.Equal(ReasoningEffort.Low, clients[1].Options[0]!.Reasoning!.Effort);
        // The conversation survived the reconnect: the second client saw the first exchange.
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.User }, clients[1].Requests[0].Select(m => m.Role));
    }

    [Fact]
    public async Task Interactive_ServerPick_ThenModel_ThenReasoning_ReconnectsOnce_AndKeepsTheConversation()
    {
        ServerOn1234("alpha");
        _http.Map("http://127.0.0.1:11434/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("phi", "gemma"));
        var endpoints = new List<LlmEndpoint>();
        var clients = new List<FakeChatClient>();
        Func<LlmEndpoint, LlmTimeouts, IChatClient> factory = (e, _) =>
        {
            endpoints.Add(e);
            var c = new FakeChatClient().EnqueueText("ok");
            clients.Add(c);
            return c;
        };
        _console.Input.PushKey(Keys.Escape);    // startup: two servers answered; ESC = the first listed, unsaved
        PushLine("hello");
        PushLine("/server");
        _console.Input.PushKey(Keys.Down);      // Ollama
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Down);      // gemma
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Down);      // the reasoning menu follows (2026-09-21): none -> low
        _console.Input.PushKey(Keys.Enter);
        PushLine("again");
        PushLine("/exit");

        string output = await InteractiveAsync(chat: factory);

        Assert.Contains(SettingsMenu.StartupServerTitle, output);
        Assert.Contains(SettingsMenu.ReasoningTitle, output);
        Assert.Contains("LLM: http://127.0.0.1:1234/v1 model=alpha (probed http://127.0.0.1:1234/v1)", output);
        Assert.Contains("LLM: http://127.0.0.1:11434/v1 model=gemma (configured)", output);
        Assert.Equal(2, clients.Count);                                                     // one reconnect carried the model and the effort
        Assert.Equal(new[] { "alpha", "gemma" }, endpoints.Select(e => e.ModelId));
        Assert.Equal("http://127.0.0.1:11434/v1", _settings.Current.LlmUrl);
        Assert.Equal("gemma", _settings.Current.LlmModel);
        Assert.Equal("low", _settings.Current.LlmReasoning);
        Assert.Equal(ReasoningEffort.None, clients[0].Options[0]!.Reasoning!.Effort);
        Assert.Equal(ReasoningEffort.Low, clients[1].Options[0]!.Reasoning!.Effort);
        Assert.True(clients[0].Disposed);
        // The conversation survived the switch: the second client saw the first exchange.
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.User }, clients[1].Requests[0].Select(m => m.Role));
    }

    [Fact]
    public async Task Interactive_UrlFlag_BeatsTheVariable()
    {
        ServerOn1234("llama");
        var env = new EnvironmentOverrides(n => n == EnvironmentOverrides.LlmUrlVariable ? "http://127.0.0.1:9" : null);
        PushLine("/exit");

        string output = await InteractiveAsync(env, SidekickOptions.None with { Url = "http://127.0.0.1:1234" });

        Assert.Contains("LLM: http://127.0.0.1:1234/v1 model=llama (first listed)", output);
    }

    [Fact]
    public async Task Interactive_CancelledToken_ExitsZero_InBothModes()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(0, await App().RunAsync(SidekickOptions.None with { Headless = true }, cts.Token));
        _console.Interactive();
        Assert.Equal(0, await App().RunAsync(SidekickOptions.None, cts.Token));
    }

    [Fact]
    public void OverriddenBy_NamesTheWhisperVariable()
    {
        var env = new EnvironmentOverrides(n => n == EnvironmentOverrides.WhisperModelVariable ? "ggml-tiny.en.bin" : null);
        Assert.Equal(EnvironmentOverrides.WhisperModelVariable, App(env).OverriddenBy(SettingsField.SttWhisperModel));
        Assert.Null(App().OverriddenBy(SettingsField.SttWhisperModel));
    }

    [Fact]
    public void OverriddenBy_NamesTheTtsVariables_TheMixIncluded()
    {
        var values = new Dictionary<string, string>
        {
            [EnvironmentOverrides.TtsVoice2Variable] = "af_sky",
            [EnvironmentOverrides.TtsMixVariable] = "100",
            [EnvironmentOverrides.InterruptEchoVariable] = "80",
            [EnvironmentOverrides.InterruptConfirmVariable] = "600",
        };
        var env = new EnvironmentOverrides(n => values.GetValueOrDefault(n));
        var app = App(env);

        Assert.Equal(EnvironmentOverrides.InterruptEchoVariable, app.OverriddenBy(SettingsField.SttInterruptEchoGuard));
        Assert.Equal(EnvironmentOverrides.InterruptConfirmVariable, app.OverriddenBy(SettingsField.SttInterruptConfirmMs));
        Assert.Equal(EnvironmentOverrides.TtsVoice2Variable, app.OverriddenBy(SettingsField.TtsVoice2));
        Assert.Equal(EnvironmentOverrides.TtsMixVariable, app.OverriddenBy(SettingsField.TtsVoiceMix));
        Assert.Null(App().OverriddenBy(SettingsField.TtsVoice2));
        Assert.Null(App().OverriddenBy(SettingsField.TtsVoiceMix));
        Assert.Null(App().OverriddenBy(SettingsField.SttInterruptEchoGuard));
        Assert.Null(App().OverriddenBy(SettingsField.SttInterruptConfirmMs));

        // A mix of 100 sends the primary alone.
        var effective = app.EffectiveSettings;
        Assert.Equal("af_heart", VoiceMix.Spec(effective.TtsVoice, effective.TtsVoice2, effective.TtsVoiceMix));
    }

    [Fact]
    public void VersionLine_NamesTheAppAndAThreePartVersion()
    {
        Assert.StartsWith(SidekickApp.Name + " ", SidekickApp.VersionLine);
        Assert.Equal(3, SidekickApp.Version.Split('.').Length);
    }

    [Fact]
    public void RenderBanner_IsTitleAndOneRule_NoStatusRows()
    {
        App().RenderBanner();

        string[] lines = _console.Output.Split('\n');
        int title = Array.FindIndex(lines, l => l.Contains("N E O N   S I D E K I C K", StringComparison.Ordinal));
        int[] rules = lines.Select((l, i) => (l, i)).Where(t => t.l.StartsWith("──", StringComparison.Ordinal)).Select(t => t.i).ToArray();
        Assert.Equal(new[] { title + 1 }, rules);                             // the one rule, right under the title; no closing rule
        Assert.All(lines[(title + 2)..], l => Assert.True(string.IsNullOrWhiteSpace(l)));   // nothing under it
        Assert.DoesNotContain("(probe localhost)", _console.Output);          // no status rows since 2026-09-14
        Assert.DoesNotContain("(first listed)", _console.Output);
        Assert.DoesNotContain("╭", _console.Output);                          // no panel border
        Assert.DoesNotContain("status", _console.Output);                     // no panel header
    }

    // ── The working directory on the title line (2026-09-18) ────────────────

    /// <summary>The last banner's title line (the test console keeps every draw).</summary>
    private string TitleLine() => _console.Output.Split('\n').Last(l => l.Contains("N E O N   S I D E K I C K", StringComparison.Ordinal));

    [Fact]
    public void RenderBanner_ShowsTheWorkingDirectoryAtTheRightEdge_WithTheSwitchOn()
    {
        // The switch is off out of the box since 2026-09-21 (the toolbar carries the path); on, the banner's title line ends with it.
        _settings.Update(d => d.ShowWorkingDirectory = true);
        App().RenderBanner();

        string title = TitleLine();
        string expected = WorkingDirectory.Resolve("", _settings.ProfileDirectory);
        Assert.StartsWith("  N E O N   S I D E K I C K  v" + SidekickApp.Version + "  ", title);   // the version still shares the line, then the gap
        Assert.EndsWith(SidekickApp.BannerPath(expected, 100 - TextCells.Width("  N E O N   S I D E K I C K  v" + SidekickApp.Version) - SidekickApp.BannerPathGap), title);
        Assert.EndsWith(@"\profiles\default\files", title);   // the resolved path's tail (the fixture's temp home is long: the head may be cut)
        Assert.Equal(100, TextCells.Width(title));            // flush with the right edge
    }

    [Fact]
    public async Task RenderBanner_ShowsTheConfiguredDirectory_AndTheCwdFlagOverIt()
    {
        string configured = Path.Combine(_dir, "elsewhere");
        _settings.Update(d => { d.WorkingDirectory = configured; d.ShowWorkingDirectory = true; });
        App().RenderBanner();
        Assert.EndsWith(@"\elsewhere", TitleLine());

        // The flag is read at RunAsync: the smoke mode draws the same banner first.
        await App(smoke: () => []).RunAsync(SidekickOptions.None with { Smoke = true, WorkingDirectory = Path.Combine(_dir, "flagged") }, CancellationToken.None);
        Assert.EndsWith(@"\flagged", TitleLine());
    }

    [Fact]
    public void RenderBanner_ShowWorkingDirectoryOff_IsTheTitleAndTheVersionAlone_ByDefault()
    {
        // Off out of the box since 2026-09-21 (the user's call): the fixture's default is the case.
        Assert.False(_settings.Current.ShowWorkingDirectory);
        App().RenderBanner();

        string title = TitleLine();
        Assert.Equal("  N E O N   S I D E K I C K  v" + SidekickApp.Version, title.TrimEnd());
        Assert.DoesNotContain(@"\files", title);
    }

    [Fact]
    public void BannerTitleMarkup_PinsTheLayout()
    {
        string left = "  N E O N   S I D E K I C K  v1.2.3";   // 35 cells

        // No path: today's line, nothing appended.
        Assert.Equal(Theme.GradientMarkup("  N E O N   S I D E K I C K") + Theme.DimMarkup("  v1.2.3"), SidekickApp.BannerTitleMarkup("1.2.3", null, 80));
        Assert.Equal(SidekickApp.BannerTitleMarkup("1.2.3", null, 80), SidekickApp.BannerTitleMarkup("1.2.3", "  ", 80));

        // A path that fits: blanks up to the right edge, the path dim; the whole line exactly the width.
        string markup = SidekickApp.BannerTitleMarkup("1.2.3", @"D:\Repo\NeonSidekick", 80);
        string plain = Markup.Remove(markup);
        Assert.Equal(80, TextCells.Width(plain));
        Assert.StartsWith(left + "  ", plain);
        Assert.EndsWith(@"D:\Repo\NeonSidekick", plain);
        Assert.EndsWith(Theme.DimMarkup(@"D:\Repo\NeonSidekick"), markup);

        // A long path is cut from the front with the gap kept; a bracket in the path is escaped, not markup.
        string cut = Markup.Remove(SidekickApp.BannerTitleMarkup("1.2.3", @"D:\Some\Very\Long\Folder\Tree\That\Never\Ends\NeonSidekick", 80));
        Assert.Equal(80, TextCells.Width(cut));
        Assert.StartsWith(left + "  …", cut);
        Assert.EndsWith(@"\NeonSidekick", cut);
        Assert.EndsWith("[x]", Markup.Remove(SidekickApp.BannerTitleMarkup("1.2.3", @"D:\a\[x]", 80)));

        // Under BannerPathMinCells of room the path is left off: the title and the version alone again.
        Assert.Equal(SidekickApp.BannerTitleMarkup("1.2.3", null, 44), SidekickApp.BannerTitleMarkup("1.2.3", @"D:\Repo", 35 + SidekickApp.BannerPathGap + SidekickApp.BannerPathMinCells - 1));
        Assert.EndsWith("  …ick\\src", Markup.Remove(SidekickApp.BannerTitleMarkup("1.2.3", @"D:\Repo\NeonSidekick\src", 35 + SidekickApp.BannerPathGap + SidekickApp.BannerPathMinCells)));   // exactly the minimum room
    }

    [Theory]
    [InlineData(null, 40, "")]
    [InlineData("", 40, "")]
    [InlineData(@"D:\Repo", 7, "")]                       // under the minimum room
    [InlineData(@"D:\Repo", 8, "D:\\Repo")]
    [InlineData(@"D:\Repo\NeonSidekick", 40, "D:\\Repo\\NeonSidekick")]
    [InlineData(@"D:\Repo\NeonSidekick", 10, "…nSidekick")]
    public void BannerPath_IsPinned(string? path, int room, string expected) => Assert.Equal(expected, SidekickApp.BannerPath(path, room));

    [Fact]
    public async Task Interactive_CwdFlag_OverridesTheSetting()
    {
        InteractiveServerOn1234("llama");
        _settings.Update(d => d.WorkingDirectory = @"D:\saved");
        string launch = Path.Combine(_dir, "launch");
        PushLine("/cwd");
        PushLine("/exit");

        string output = await InteractiveAsync(options: SidekickOptions.None with { WorkingDirectory = launch });

        Assert.Contains(launch, output);   // the /cwd notice
        Assert.Contains(SidekickOptions.CwdFlag, output);   // the /cwd notice's tag
        Assert.Contains(ChatScreen.CwdNotice(launch, false, SidekickOptions.CwdFlag), output);
        Assert.Equal(@"D:\saved", _settings.Current.WorkingDirectory);
    }

    [Fact]
    public void OverriddenBy_NamesTheCwdFlag()
    {
        var app = App();
        Assert.Null(app.OverriddenBy(SettingsField.WorkingDirectory));
        Assert.Equal(Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName), app.BuildWorkingDirectory().Root);
    }

    [Fact]
    public async Task Headless_OffersTheFileTools_AndAModelWriteLandsInTheProfilesFolder()
    {
        ServerOn1234("llama");
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "note.txt", ["content"] = "hi" }));
        _chat.EnqueueText("Done.");

        string output = await Headless("write hi to note.txt\n");

        Assert.Contains("[tool] write_file -> wrote note.txt (2 bytes, 1 line, 1 word)", output);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, "note.txt")));
    }
    [Fact]
    public void StartupLogLine_IsPinned()
    {
        var facts = new AboutFacts("0.2.0", ".NET 10.0.0", true, "x64", "Microsoft Windows 10.0.26200", null, @"C:\h\.neonsidekick", @"C:\h\.neonsidekick\profiles\default", @"C:\h\.neonsidekick\models");
        Assert.Equal(@"NeonSidekick 0.2.0 (native, .NET 10.0.0, x64, Microsoft Windows 10.0.26200), mode interactive, flags --cwd D:\x, home C:\h\.neonsidekick, profile default, console 240×60",
            SidekickApp.StartupLogLine(facts, SidekickOptions.Parse(["--cwd", @"D:\x"]), "default", 240, 60));
        Assert.Equal(@"NeonSidekick 0.2.0 (JIT, .NET 10.0.0, x64, Microsoft Windows 10.0.26200), mode headless, no flags, home C:\h\.neonsidekick, profile work, console 80×25",
            SidekickApp.StartupLogLine(facts with { NativeAot = false }, SidekickOptions.Parse(["--headless"]), "work", 80, 25));
        Assert.Equal("Startup", SidekickApp.StartupCategory);
        Assert.Equal("Overrides in force: NEONSIDEKICK_TTS_SPEED=1.3", SidekickApp.EnvironmentLogLine("NEONSIDEKICK_TTS_SPEED=1.3"));
        Assert.Equal("Headless exit: end of input", SidekickApp.HeadlessExitLogLine(ChatScreen.ExitByEndOfInput));
    }

    [Fact]
    public async Task Headless_LogsTheStartup_TheTurn_AndTheExit()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Arr.");
        var lines = new List<NeonSidekick.Diagnostics.DiagnosticEvent>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category is SidekickApp.StartupCategory or Assistant.TurnCategory or ChatScreen.AppCategory) lines.Add(e); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            await Headless("hello\n");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        var startup = Assert.Single(lines, e => e.Category == SidekickApp.StartupCategory);
        Assert.Equal(NeonSidekick.Diagnostics.DiagnosticLevel.Info, startup.Level);
        Assert.StartsWith("NeonSidekick " + SidekickApp.Version + " (", startup.Message, StringComparison.Ordinal);
        Assert.Contains(", mode headless, ", startup.Message, StringComparison.Ordinal);
        Assert.Contains(lines, e => e.Message.StartsWith("Turn 1 started: \"hello\"", StringComparison.Ordinal));
        Assert.Contains(lines, e => e.Message.StartsWith("Turn 1 ended after", StringComparison.Ordinal) && e.Message.EndsWith(Assistant.TurnCompleted, StringComparison.Ordinal));
        // The session's disconnect (the LlmSession's own App line) follows the exit line on dispose.
        Assert.Contains(lines, e => e.Category == ChatScreen.AppCategory && e.Level == NeonSidekick.Diagnostics.DiagnosticLevel.Info && e.Message == SidekickApp.HeadlessExitLogLine(ChatScreen.ExitByEndOfInput));
    }
}
