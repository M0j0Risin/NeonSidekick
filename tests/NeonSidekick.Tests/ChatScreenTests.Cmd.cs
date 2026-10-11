using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/cmd</c> (2026-10-09): the user's own shell line through the model's checks — the policy, the police, the allow list and the
/// approval pane — drawn live, ESC killing it, then into the history as a tagged pair. Real <c>cmd.exe</c> children (built-ins
/// alone) on Windows, zsh on a Mac; the manual clock.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>
    /// Types <paramref name="line"/>, answers a pane it opens with <paramref name="paneKeys"/>, and once <paramref name="done"/> is on
    /// the screen types <paramref name="after"/> (a message for the model, when given) and <c>/exit</c> — never while the line runs,
    /// where <c>/exit</c> would stop it. A read that comes while the line runs holds the reader (as <see cref="StepsOverTheTail"/>
    /// does) until the pane opens or the line is done: a read that returned empty-handed is not offered the wait again.
    /// </summary>
    private void CmdFixture(string line, string done, ConsoleKeyInfo[]? paneKeys = null, string? after = null)
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var input = Scripted();
        var steps = new Queue<string>(after is null ? ["/exit"] : [after, "/exit"]);
        bool typed = false;
        bool answered = false;
        input.OnWait = () =>
        {
            if (!typed)
            {
                typed = true;
                PushLine(input, line);
                return;
            }

            bool Pane() => _keys is { PendingLine.IsCompleted: false };
            SpinWait.SpinUntil(() => (Pane() && !answered && paneKeys is not null) || _console.Output.Contains(done, StringComparison.Ordinal), TimeSpan.FromSeconds(20));
            if (Pane())
            {
                if (!answered && paneKeys is not null)
                {
                    answered = true;
                    input.Push(paneKeys);
                }

                return;
            }

            if (steps.Count > 0 && _console.Output.Contains(done, StringComparison.Ordinal))
            {
                PushLine(input, steps.Dequeue());
            }
        };
    }

    private static string Header(string command, string kind = "cmd") => ShellText.ExitHeader(kind, 0, TimeSpan.Zero, command);

    private string SandboxRoot => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);

    /// <summary>Under yolo the line runs at once: its output under the indent, the exit as a notice, and the pair in the next request.</summary>
    [WindowsFact]
    public async Task Cmd_UnderYolo_RunsTheLine_DrawsItsOutput_AndTheModelSeesThePair()
    {
        _settings.Update(d => d.ShellCommandPolicy = "yolo");
        _chat.EnqueueText("It said hi.");
        CmdFixture("/cmd --shell cmd echo hi", Header("echo hi"), after: "what did it say?");

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.ToolAnswerIndent +"hi\n", output);
        Assert.Contains(TranscriptRenderer.NoticeGlyph + Header("echo hi") + "\n", output);
        Assert.DoesNotContain(ShellText.ApprovalTitle, output);
        var request = Assert.Single(_chat.Requests);
        Assert.Contains(request, m => m.Role == ChatRole.User && m.Text == "[I ran in the shell (cmd)] echo hi");
        Assert.Contains(request, m => m.Role == ChatRole.Assistant && m.Text == "[shell output] " + Header("echo hi") + "\nhi");
        Assert.Contains("It said hi.", output);
    }

    /// <summary>The Unix twin of <see cref="Cmd_UnderYolo_RunsTheLine_DrawsItsOutput_AndTheModelSeesThePair"/>: zsh.</summary>
    [UnixFact]
    public async Task Cmd_UnderYolo_RunsTheLine_DrawsItsOutput_AndTheModelSeesThePair_Unix()
    {
        _settings.Update(d => d.ShellCommandPolicy = "yolo");
        _chat.EnqueueText("It said hi.");
        CmdFixture("/cmd --shell zsh echo hi", Header("echo hi", "zsh"), after: "what did it say?");

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.ToolAnswerIndent +"hi\n", output);
        var request = Assert.Single(_chat.Requests);
        Assert.Contains(request, m => m.Role == ChatRole.User && m.Text == "[I ran in the shell (zsh)] echo hi");
        Assert.Contains(request, m => m.Role == ChatRole.Assistant && m.Text == "[shell output] " + Header("echo hi", "zsh") + "\nhi");
    }

    /// <summary>Under ask the line goes to the approval pane as the model's would; Allow once runs it.</summary>
    [WindowsFact]
    public async Task Cmd_UnderAsk_ThePaneAsks_AllowOnce_RunsIt()
    {
        _settings.Update(d => d.ShellCommandPolicy = "ask");
        CmdFixture("/cmd --shell cmd echo hi", Header("echo hi"), paneKeys: [Keys.Down, Keys.Enter]);

        string output = await RunAsync();

        Assert.Contains("\n" + Titled(ShellText.ApprovalTitle + ApprovalButtons) + "\ncmd › echo hi\n", output);
        Assert.Contains(TranscriptRenderer.ToolAnswerIndent +"hi\n", output);
        Assert.Contains(Header("echo hi"), output);
        Assert.Empty(_settings.Current.ShellCommandAllowed);
    }

    /// <summary>A deny on the pane runs nothing and keeps nothing: the next request carries no pair.</summary>
    [WindowsFact]
    public async Task Cmd_UnderAsk_Deny_RunsNothing_AndKeepsNothing()
    {
        _settings.Update(d => d.ShellCommandPolicy = "ask");
        _chat.EnqueueText("Fine.");
        CmdFixture("/cmd --shell cmd echo hi", ShellText.CmdDenied, paneKeys: [Keys.Escape], after: "ok");

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.NoticeGlyph + ShellText.CmdDenied + "\n", output);
        Assert.DoesNotContain(Header("echo hi"), output);
        Assert.DoesNotContain(_chat.Requests[0], m => m.Text.Contains("[I ran in the shell", StringComparison.Ordinal));
    }

    /// <summary>A prefix on the allow list runs without the pane, as the model's line does.</summary>
    [WindowsFact]
    public async Task Cmd_UnderAsk_AnAllowedPrefix_RunsWithoutThePane()
    {
        _settings.Update(d => { d.ShellCommandPolicy = "ask"; d.ShellCommandAllowed = ["echo"]; });
        CmdFixture("/cmd --shell cmd echo hi", Header("echo hi"));

        string output = await RunAsync();

        Assert.DoesNotContain(ShellText.ApprovalTitle, output);
        Assert.Contains(Header("echo hi"), output);
    }

    /// <summary>Shell command policy off refuses the user's line too (the user's pick), naming where to change it.</summary>
    [WindowsFact]
    public async Task Cmd_UnderOff_IsRefused_NothingRuns()
    {
        _settings.Update(d => d.ShellCommandPolicy = "off");
        CmdFixture("/cmd --shell cmd echo hi", ShellText.CmdPolicyOff);

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.ErrorGlyph + ShellText.CmdPolicyOff + "\n", output);
        Assert.DoesNotContain(Header("echo hi"), output);
    }

    /// <summary>The police refuse a path outside the working directory whatever the policy, behind the officer, and the pane is never asked.</summary>
    [WindowsFact]
    public async Task Cmd_AnOutsidePath_IsRefusedByThePolice_EvenUnderYolo()
    {
        _settings.Update(d => d.ShellCommandPolicy = "yolo");
        string refused = ShellText.OutsidePath(@"C:\Windows\win.ini");
        CmdFixture(@"/cmd --shell cmd type C:\Windows\win.ini", refused);

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.PoliceGlyph + refused + "\n", output);
        Assert.DoesNotContain("exit 0", output);
    }

    /// <summary>A forbidden string is named to the user on the 👮 line, and the line never runs.</summary>
    [WindowsFact]
    public async Task Cmd_AForbiddenString_IsRefused_TheOfficerNamesIt()
    {
        _settings.Update(d => { d.ShellCommandPolicy = "yolo"; d.ShellPoliceForbiddenStrings = ["xyzzy"]; });
        CmdFixture("/cmd --shell cmd echo XYZZY", ShellText.ForbiddenShown("xyzzy"));

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.PoliceGlyph + ShellText.ForbiddenShown("xyzzy") + "\n", output);
        Assert.DoesNotContain("exit 0", output);
    }

    /// <summary>With the police off, the outside path is the shell's: it runs under yolo.</summary>
    [WindowsFact]
    public async Task Cmd_WithThePoliceOff_AnOutsidePathRuns()
    {
        _settings.Update(d => { d.ShellCommandPolicy = "yolo"; d.ShellPolice = false; });
        CmdFixture(@"/cmd --shell cmd if exist C:\Windows\win.ini echo there", Header(@"if exist C:\Windows\win.ini echo there"));

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.ToolAnswerIndent +"there\n", output);
        Assert.DoesNotContain(TranscriptRenderer.PoliceGlyph, output);
    }

    /// <summary>
    /// ESC kills a line that runs on: the cancelled header as a warning, long before the line would have ended. Pressed from another
    /// thread on the test console once the line has begun (the /test ESC test's shape: the watcher reads the console as it comes).
    /// </summary>
    [WindowsFact]
    public async Task Cmd_Esc_KillsTheLine()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ShellCommandPolicy = "yolo"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/cmd --shell cmd ping -n 30 127.0.0.1");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pressing = Task.Run(async () =>
        {
            while (!_console.Output.Contains("Pinging 127.0.0.1", StringComparison.Ordinal) && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(20);
            }

            _console.Input.PushKey(Keys.Escape);
            PushLine("/exit");
        });

        string output = await RunAsync();
        await pressing;

        Assert.Contains(TranscriptRenderer.WarningGlyph + ShellText.CmdCancelledHeader("cmd", TimeSpan.Zero, "ping -n 30 127.0.0.1"), output);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(25), "the line ran its 30 pings: " + clock.Elapsed);
    }

    /// <summary>Output over Shell output max chars stops being drawn at one notice, and the whole of it goes to the spill file the history names.</summary>
    [WindowsFact]
    public async Task Cmd_OutputOverTheCap_StopsDrawing_AndSpills()
    {
        _settings.Update(d => { d.ShellCommandPolicy = "yolo"; d.ShellOutputMaxChars = AppSettingsData.MinShellOutputMaxChars; });
        _chat.EnqueueText("Long.");
        string command = "for /L %i in (1,1,400) do @echo line number %i";
        CmdFixture("/cmd --shell cmd " + command, Header(command), after: "ok");

        string output = await RunAsync();

        Assert.Contains(ShellText.CmdOutputCut(AppSettingsData.MinShellOutputMaxChars), output);
        Assert.Contains(TranscriptRenderer.ToolAnswerIndent + "line number 1\n", output);
        Assert.DoesNotContain(TranscriptRenderer.ToolAnswerIndent + "line number 400\n", output);
        string spill = Assert.Single(Directory.GetFiles(Path.Combine(SandboxRoot, ShellText.SpillFolderName), ChatScreen.CmdIdPrefix + "*.log"));
        Assert.Contains("line number 400", File.ReadAllText(spill), StringComparison.Ordinal);
        var reply = Assert.Single(_chat.Requests[0], m => m.Role == ChatRole.Assistant && m.Text.StartsWith("[shell output] ", StringComparison.Ordinal));
        Assert.Contains("the whole output is in .shell\\" + ChatScreen.CmdIdPrefix, reply.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// /cmd --bg (2026-10-11): the start notice and the chat back at once; the end comes as the report at the idle loop top — the
    /// header as a notice, the output under the indent — and the pair, tagged as a background run, in the next request. The read is
    /// left to the alert's signal (no spin in the script: the report is drawn only once the read gives way).
    /// </summary>
    [WindowsFact]
    public async Task Cmd_Bg_StartsInTheBackground_ThenReportsTheEnd_AndTheModelSeesThePair()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ShellCommandPolicy = "yolo"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("It said hi.");
        var input = Scripted();
        var steps = new Queue<string>(["/cmd --bg --shell cmd echo hi", "what did it say?", "/exit"]);
        input.OnWait = () =>
        {
            // The command first; the message once the report is on the screen; /exit once the reply is.
            bool ready = steps.Count switch
            {
                3 => true,
                2 => _console.Output.Contains(Header("echo hi"), StringComparison.Ordinal),
                _ => _console.Output.Contains("It said hi.", StringComparison.Ordinal),
            };
            if (ready && steps.Count > 0)
            {
                PushLine(input, steps.Dequeue());
            }
        };

        string output = await RunAsync();

        Assert.Contains("started " + ProcessRegistry.IdPrefix, output);
        Assert.Contains(" in the background (cmd, pid ", output);
        Assert.Contains(TranscriptRenderer.NoticeGlyph + Header("echo hi") + "\n", output);
        Assert.Contains(TranscriptRenderer.ToolAnswerIndent + "hi\n", output);
        var request = Assert.Single(_chat.Requests);
        Assert.Contains(request, m => m.Role == ChatRole.User && m.Text == "[I ran in the shell (cmd, in the background)] echo hi");
        Assert.Contains(request, m => m.Role == ChatRole.Assistant && m.Text == "[shell output] " + Header("echo hi") + "\nhi");
        Assert.DoesNotContain(request, m => m.Contents.OfType<FunctionCallContent>().Any());   // no seeded process poll

    }

    /// <summary>--bg passes the same checks before anything starts: policy off refuses it, as the police do.</summary>
    [WindowsFact]
    public async Task Cmd_Bg_UnderOff_IsRefused_NothingStarts()
    {
        _settings.Update(d => d.ShellCommandPolicy = "off");
        CmdFixture("/cmd --bg --shell cmd echo hi", ShellText.CmdPolicyOff);

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.ErrorGlyph + ShellText.CmdPolicyOff + "\n", output);
        Assert.DoesNotContain(" in the background (", output);
    }

    /// <summary>The background switch: before or after --shell, alone the usage error, and a word that only starts with it is the command.</summary>
    [Fact]
    public void Cmd_TheArgument_ParsesTheBackgroundSwitch()
    {
        var effective = new AppSettingsData { ShellDefault = ShellKinds.PlatformDefault };
        Assert.False(ChatScreen.TryParseCmd("--bg", effective, out _, out _, out bool background, out string? bare));
        Assert.Equal(ShellText.CmdUsage, bare);
        Assert.False(ChatScreen.TryParseCmd("--bg --shell bash", effective, out _, out _, out _, out string? noCommand));
        Assert.Equal(ShellText.CmdUsage, noCommand);

        Assert.True(ChatScreen.TryParseCmd("--BG git status", effective, out var kind, out string command, out background, out _));
        Assert.True(background);
        Assert.Equal(ShellKinds.Resolve(effective), kind);
        Assert.Equal("git status", command);
        Assert.True(ChatScreen.TryParseCmd("--bg --shell bash ls -l", effective, out kind, out command, out background, out _));
        Assert.True(background);
        Assert.Equal(ShellKind.Bash, kind);
        Assert.Equal("ls -l", command);
        Assert.True(ChatScreen.TryParseCmd("--shell bash --bg ls -l", effective, out kind, out command, out background, out _));
        Assert.True(background);
        Assert.Equal(ShellKind.Bash, kind);
        Assert.Equal("ls -l", command);
        Assert.True(ChatScreen.TryParseCmd("--bgx", effective, out _, out command, out background, out _));
        Assert.False(background);
        Assert.Equal("--bgx", command);
        Assert.True(ChatScreen.TryParseCmd("echo --bg", effective, out _, out command, out background, out _));
        Assert.False(background);
        Assert.Equal("echo --bg", command);
    }

    /// <summary>A bare /cmd, and --shell with nothing after it, are the usage error; an unknown shell is named with the choices.</summary>
    [Fact]
    public void Cmd_TheArgument_ParsesTheShellOption()
    {
        var effective = new AppSettingsData { ShellDefault = ShellKinds.PlatformDefault };
        Assert.False(ChatScreen.TryParseCmd("", effective, out _, out _, out string? bare));
        Assert.Equal(ShellText.CmdUsage, bare);
        Assert.False(ChatScreen.TryParseCmd("--shell", effective, out _, out _, out string? noName));
        Assert.Equal(ShellText.CmdUsage, noName);
        Assert.False(ChatScreen.TryParseCmd("--shell bash", effective, out _, out _, out string? noCommand));
        Assert.Equal(ShellText.CmdUsage, noCommand);
        Assert.False(ChatScreen.TryParseCmd("--shell fish ls", effective, out _, out _, out string? unknown));
        Assert.Equal(FileText.BadChoice("shell", "fish", string.Join(", ", ShellKinds.Names)), unknown);

        Assert.True(ChatScreen.TryParseCmd("--SHELL bash  echo  two  spaces ", effective, out var kind, out string command, out _));
        Assert.Equal(ShellKind.Bash, kind);
        Assert.Equal("echo  two  spaces", command);
        Assert.True(ChatScreen.TryParseCmd("git status", effective, out kind, out command, out _));
        Assert.Equal(ShellKinds.Resolve(effective), kind);
        Assert.Equal("git status", command);
        Assert.True(ChatScreen.TryParseCmd("--shellish", effective, out _, out command, out _));   // not the option: a command of that name
        Assert.Equal("--shellish", command);
    }

    /// <summary>The argument list: the options not yet given while a leading word is typed (--bg since 2026-10-11), then the shells, then nothing.</summary>
    [Fact]
    public void Cmd_TheArgumentList_OffersTheOptionsThenTheShells()
    {
        Assert.Equal(new[] { ShellText.CmdBgOption, ShellText.CmdShellOption }, ChatScreen.CmdChoices("").Select(i => i.Text));
        Assert.Equal(new[] { ShellText.CmdShellOption }, ChatScreen.CmdChoices("--sh").Select(i => i.Text));
        Assert.Equal(new[] { ShellText.CmdBgOption }, ChatScreen.CmdChoices("--b").Select(i => i.Text));
        Assert.Equal(ShellKinds.Names.Select(n => "--shell " + n), ChatScreen.CmdChoices("--shell ").Select(i => i.Text));
        Assert.Equal(new[] { "--bg --shell" }, ChatScreen.CmdChoices("--bg ").Select(i => i.Text));
        Assert.Equal(ShellKinds.Names.Select(n => "--bg --shell " + n), ChatScreen.CmdChoices("--bg --shell ").Select(i => i.Text));
        Assert.Equal(new[] { "--shell bash --bg" }, ChatScreen.CmdChoices("--shell bash ").Select(i => i.Text));
        Assert.Empty(ChatScreen.CmdChoices("git "));
        Assert.Empty(ChatScreen.CmdChoices("--shell bash ls"));
        Assert.Empty(ChatScreen.CmdChoices("--bg --shell bash ls"));
        Assert.Empty(ChatScreen.CmdChoices("--bg git"));
    }

    /// <summary>The history's tag reads back as the line that runs it again (/rewind), the shell named; any other text is not one.</summary>
    [Fact]
    public void Cmd_TheHistoryTag_ReadsBackAsTheLine()
    {
        Assert.Equal("[I ran in the shell (cmd)] dir /b", ShellText.CmdHistoryUser("cmd", "dir /b"));
        Assert.Equal("/cmd --shell cmd dir /b", ShellText.CmdHistoryCommand(ShellText.CmdHistoryUser("cmd", "dir /b")));
        Assert.Null(ShellText.CmdHistoryCommand("[I ran in the shell] nothing"));
        Assert.Null(ShellText.CmdHistoryCommand("plain text"));
        Assert.Equal("/cmd --shell powershell Get-Date", ConversationRewind.UserLine(new ChatMessage(ChatRole.User, ShellText.CmdHistoryUser("powershell", "Get-Date"))));
        // A background run (2026-10-11) reads back with its switch.
        Assert.Equal("[I ran in the shell (bash, in the background)] make", ShellText.CmdHistoryUser("bash", "make", background: true));
        Assert.Equal("/cmd --bg --shell bash make", ShellText.CmdHistoryCommand(ShellText.CmdHistoryUser("bash", "make", background: true)));
    }

    /// <summary>The registration: one word, an argument, A to Z on /help, and a reply's end waited for.</summary>
    [Fact]
    public void Cmd_IsACommand_ThatTakesItsLine_AndWaitsForTheReply()
    {
        Assert.Equal((SlashCommand.Cmd, "--shell cmd dir"), SlashCommands.Parse("/cmd --shell cmd dir"));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.Cmd));
        Assert.Contains("/cmd", SlashCommands.Words);
        Assert.Contains(SlashCommands.HelpEntries, e => e.Command == "/cmd");
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Cmd, hasArgs: true));
    }

    /// <summary>A /cmd line's output keeps its text and drops what the terminal would act on.</summary>
    [Fact]
    public void Cmd_TheOutputMarkup_ExpandsTabs_AndDropsControlCharacters()
    {
        Assert.Equal(Theme.ColorMarkup(Theme.Ink, TranscriptRenderer.ToolAnswerIndent + "a    b [x]"), TranscriptRenderer.ShellOutputMarkup("a\tb \u001b[x]\r", isError: false));
        Assert.Equal(Theme.ColorMarkup(Theme.Warn, TranscriptRenderer.ToolAnswerIndent + "oops"), TranscriptRenderer.ShellOutputMarkup("oops", isError: true));
    }
}
