using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The shell tools on the screen (2026-09-21): <c>run_command</c> offered after the git tools with its
/// rule in the prompt, the approval pane under the spinner with its four rows and hotkeys, the allows
/// (once, for the session, for good) and the deny, the quiet note being the result's header, the
/// policy switch and the pane-off refusal. Real <c>cmd.exe</c> children (built-ins alone), the manual
/// clock, the pinned words.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The model calls <c>run_command</c> once (<paramref name="command"/> in cmd), the pane is answered with <paramref name="keys"/>, then the reply.</summary>
    private void ShellFixture(ConsoleKeyInfo[] keys, string reply, string command = "echo hi", params string[] moreCommands)
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.Enqueue(FakeChatClient.Call("c1", RunCommandTool.ToolName, new Dictionary<string, object?> { [RunCommandTool.CommandArgument] = command, [RunCommandTool.ShellArgument] = "cmd" }));
        int n = 2;
        foreach (string more in moreCommands)
        {
            _chat.Enqueue(FakeChatClient.Call("c" + n++, RunCommandTool.ToolName, new Dictionary<string, object?> { [RunCommandTool.CommandArgument] = more, [RunCommandTool.ShellArgument] = "cmd" }));
        }

        _chat.EnqueueText(reply);
        var input = Scripted();
        StepsWhenIdle(Line("run it"), Line("/exit"));
        var idle = input.OnWait!;
        bool answered = false;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                if (!answered)
                {
                    answered = true;
                    input.Push(keys);
                }

                return;
            }

            idle();
        };
    }

    private void Dump(string output) =>
        File.WriteAllText(@"C:/Users/cnels/AppData/Local/Temp/claude/D--Repo-NeonSidekick/e2513b71-3e43-4d3a-9ccd-4e1839dace2b/scratchpad/screen.txt", output + Environment.NewLine + "=====" + Environment.NewLine + string.Join(Environment.NewLine + "----" + Environment.NewLine, _chat.Requests.Select(r => string.Join(Environment.NewLine, r.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(x => x.Result)))));

    private static string ToolResult(IReadOnlyList<ChatMessage> request, string callId) =>
        (string)Assert.Single(request.SelectMany(m => m.Contents.OfType<FunctionResultContent>()), r => r.CallId == callId).Result!;

    [Fact]
    public async Task RunCommand_ThePaneAsksUnderTheSpinner_AllowOnce_RunsIt_AndTheNoteIsTheHeader()
    {
        ShellFixture([Keys.Down, Keys.Enter], "It said hi.");

        string output = await RunAsync();

        // The pane over the reply: the title, the command as the caption, the four rows with Deny under the cursor, the keys.
        Assert.Contains("\n" + Titled(ShellText.ApprovalTitle) + "\ncmd › echo hi\n \n▸ Deny\n  Allow once\n  Allow \"echo\" for this session\n  Allow \"echo\" always (saved to the profile)\n", output);
        Assert.Contains(" " + ScreenPane.BusyRow(RunCommandTool.ToolName, TimeSpan.Zero, ShellText.ApprovalKeys), output);
        // The quiet note: the result's header, never the tool's name or the output.
        Assert.Contains("🛠️ exit 0 in 0.0 s (cmd): echo hi\n", output);
        Assert.DoesNotContain("🛠️ " + RunCommandTool.ToolName, output);
        Assert.DoesNotContain("\nhi\n", output);
        Assert.Contains("It said hi.", output);
        Assert.DoesNotContain("(allowed", output);
        // The model: the tool after the git tools with its rule, the result under the call id.
        var offered = _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToList();
        Assert.Equal(offered.IndexOf(GitDeleteTool.ToolName) + 1, offered.IndexOf(RunCommandTool.ToolName));
        Assert.Contains(Assistant.ShellRuleWithoutBridge, _chat.Requests[0][0].Text!, StringComparison.Ordinal);   // the bridge off by default (later on 2026-09-21)
        Assert.Equal(SkilledPrompt(false, [], web: true, ask: AskLimits.Default, markdown: true), _chat.Requests[1][0].Text);
        Assert.Equal("exit 0 in 0.0 s (cmd): echo hi\nhi", ToolResult(_chat.Requests[1], "c1"));
        Assert.Empty(_settings.Current.ShellCommandAllowed);
    }

    [Fact]
    public async Task RunCommand_EscDenies_TheModelGetsTheSentence_AndTheReplyRunsOn()
    {
        ShellFixture([Keys.Down, Keys.Escape], "Fine, I will not.");

        string output = await RunAsync();

        Assert.Contains("🛠️ Error: the command was denied by the user: echo hi; do not retry it or work around the refusal\n", output);
        Assert.Contains("Fine, I will not.", output);
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
        Assert.Equal("Error: the command was denied by the user: echo hi; do not retry it or work around the refusal", ToolResult(_chat.Requests[1], "c1"));
        Assert.DoesNotContain("\nhi\n", output);
    }

    /// <summary>The outside-paths police (2026-09-22): a line naming a path outside the sandbox is refused before the pane opens, the transcript line wears 👮, and the rules carry the policed head; off, nothing is policed and the head says only where a command starts.</summary>
    [Fact]
    public async Task RunCommand_OutsidePath_IsRefusedByThePolice_BehindTheOfficer_AndNeverAsked()
    {
        ShellFixture([Keys.Escape], "Staying inside.", command: @"type C:\Windows\win.ini");

        string output = await RunAsync();

        Assert.Contains(@"👮 Error: outside the working directory: 'C:\Windows\win.ini' — a command or a script may only name paths under it" + "\n", output);
        Assert.DoesNotContain("🛠️ Error: outside", output);
        Assert.DoesNotContain(ShellText.ApprovalTitle, output);   // the pane never opened
        Assert.Contains("Staying inside.", output);
        Assert.Equal(@"Error: outside the working directory: 'C:\Windows\win.ini' — a command or a script may only name paths under it", ToolResult(_chat.Requests[1], "c1"));
        Assert.Contains(Assistant.ShellRuleWithoutBridge, _chat.Requests[0][0].Text!, StringComparison.Ordinal);   // the policed head, on by default
        Assert.DoesNotContain("reach the whole computer", _chat.Requests[0][0].Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommand_PoliceOff_TheLineReachesThePane_AndTheRulesSayOnlyWhereACommandStarts()
    {
        _settings.Update(d => d.ShellPoliceOutsidePaths = false);
        ShellFixture([Keys.Down, Keys.Escape], "Denied then.", command: @"type C:\Windows\win.ini");

        string output = await RunAsync();

        Assert.Contains(@"🛠️ Error: the command was denied by the user: type C:\Windows\win.ini; do not retry it or work around the refusal" + "\n", output);
        Assert.DoesNotContain("👮", output);
        Assert.Contains(Assistant.ShellRuleWithoutBridgeUnpoliced, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("may only name paths under it", _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("reach the whole computer", _chat.Requests[0][0].Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommand_EnterOnDeny_Denies_AndTheHotkeyD_IsTheSame()
    {
        ShellFixture([Keys.Char('o'), Keys.Char('d'), Keys.Enter], "Ok.");

        await RunAsync();

        Assert.StartsWith("Error: the command was denied by the user", ToolResult(_chat.Requests[1], "c1"));
    }

    [Fact]
    public async Task RunCommand_AllowSession_NotesIt_AndTheNextCallOfThePrefixNeverAsks()
    {
        ShellFixture([Keys.Char('s'), Keys.Enter], "Twice.", "echo hi", "echo again");

        string output = await RunAsync();

        Assert.Contains("(🔓 allowed for this session: echo)\n", output);
        Assert.Contains("cmd › echo hi\n", output);
        Assert.DoesNotContain("cmd › echo again", output);   // the second echo rode the session's allow
        Assert.Equal("exit 0 in 0.0 s (cmd): echo hi\nhi", ToolResult(_chat.Requests[1], "c1"));
        Assert.Equal("exit 0 in 0.0 s (cmd): echo again\nagain", ToolResult(_chat.Requests[2], "c2"));
        Assert.Contains("🛠️ exit 0 in 0.0 s (cmd): echo again\n", output);
        Assert.Empty(_settings.Current.ShellCommandAllowed);   // the session's, not the file's
    }

    [Fact]
    public async Task RunCommand_AllowAlways_SavesThePrefix_AndNotesTheTab()
    {
        ShellFixture([Keys.Char('a'), Keys.Enter], "Saved.");

        string output = await RunAsync();

        Assert.Contains("(🔓 allowed always: echo — the Shell tab of /tools)\n", output);
        Assert.Equal(["echo"], _settings.Current.ShellCommandAllowed);
        Assert.Equal("exit 0 in 0.0 s (cmd): echo hi\nhi", ToolResult(_chat.Requests[1], "c1"));
    }

    [Fact]
    public async Task RunCommand_APrefixOnTheProfilesList_NeverAsks()
    {
        _settings.Update(d => d.ShellCommandAllowed = ["echo"]);
        ShellFixture([], "Ran.");

        string output = await RunAsync();

        Assert.DoesNotContain(ShellText.ApprovalTitle, output);
        Assert.Equal("exit 0 in 0.0 s (cmd): echo hi\nhi", ToolResult(_chat.Requests[1], "c1"));
    }

    [Fact]
    public async Task RunCommand_Yolo_NeverAsks()
    {
        _settings.Update(d => { d.ShellCommandPolicy = "yolo"; d.ShellPreferNative = false; });   // dir is search_files' line since 2026-09-26: the setting off, it runs
        ShellFixture([], "Ran.", "dir /b nothing-here-*");

        string output = await RunAsync();

        Assert.DoesNotContain(ShellText.ApprovalTitle, output);
        Assert.Equal("exit 1 in 0.0 s (cmd): dir /b nothing-here-*\n--- stderr ---\nFile Not Found", ToolResult(_chat.Requests[1], "c1"));
    }

    /// <summary>
    /// Shell prefer native tools (2026-09-26): under ask, a line a native tool the turn offers covers comes back not run and the
    /// pane is never asked; the same line again in the turn is put to the pane (denied here), so a real need reaches the user.
    /// </summary>
    [Fact]
    public async Task RunCommand_ALineANativeToolCovers_GoesBackToIt_TheSameLineAgainReachesThePane()
    {
        ShellFixture([Keys.Down, Keys.Escape], "Read it natively.", "type notes.txt", "type notes.txt");

        string output = await RunAsync();

        Assert.Equal(ShellText.UseNative("type", ReadFileTool.ToolName), ToolResult(_chat.Requests[1], "c1"));
        Assert.Equal("Error: the command was denied by the user: type notes.txt; do not retry it or work around the refusal", ToolResult(_chat.Requests[2], "c2"));
        Assert.Contains("\n" + Titled(ShellText.ApprovalTitle) + "\ncmd › type notes.txt\n", output);   // the second was put to the pane
        Assert.Contains("Call run_command only for what no other tool does: read_file and search_files", _chat.Requests[0][0].Text!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A background run with notify (phase B, 2026-09-21): the start line at once, the ⚡ alert at the idle
    /// line when the child exits, and the next turn opening with a seeded <c>process poll</c> pair the model
    /// reads — a real call/result pair under a nine-character id, rendered above the reply like the openers.
    /// </summary>
    [Fact]
    public async Task RunCommand_Background_Notify_AlertsAtIdle_AndTheNextTurnOpensWithTheSeededPoll()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ShellCommandPolicy = "yolo"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.Enqueue(FakeChatClient.Call("c1", RunCommandTool.ToolName, new Dictionary<string, object?> { ["command"] = "echo bg", ["shell"] = "cmd", ["background"] = true, ["notify"] = true }));
        _chat.EnqueueText("Started.");
        _chat.EnqueueText("It ended.");
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                return;
            }

            switch (step)
            {
                case 0:
                    step = 1;
                    PushLine(input, "start it");
                    break;
                case 1:
                    // The idle line waits for the exit: the registry's signal ends the read, the alert prints, the read re-arms and asks again.
                    if (Output.Contains(TranscriptRenderer.ProcessGlyph, StringComparison.Ordinal))
                    {
                        step = 2;
                        PushLine(input, "and?");
                    }

                    break;
                case 2:
                    step = 3;
                    PushLine(input, "/exit");
                    break;
            }
        };

        string output = await RunAsync();

        string started = ToolResult(_chat.Requests[1], "c1");
        Assert.Matches("^started proc_[0-9a-f]{6} \\(cmd, pid [0-9]+\\): echo bg\n", started);
        Assert.EndsWith("; you will be told when it exits.", started);
        string id = started.Substring(8, 11);
        Assert.Contains("🛠️ started " + id + " (cmd, pid ", output);
        Assert.Contains("  ⚡ " + id + " exited 0 after ", output);
        Assert.Contains(": echo bg\n", output);
        // The next turn: the seeded poll pair first, then the user's line — the model reads the output it never asked for.
        var second = _chat.Requests[2];
        var call = second.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Single(c => c.CallId == Assistant.PendingCallId(id));
        Assert.Equal(ProcessTool.ToolName, call.Name);
        Assert.Equal("poll", call.Arguments!["action"]?.ToString());
        Assert.Equal(id, call.Arguments["session_id"]?.ToString());
        string polled = ToolResult(second, Assistant.PendingCallId(id));
        Assert.StartsWith(id + " exited 0 after ", polled);
        Assert.EndsWith(" (cmd): echo bg — 1 new line\nbg", polled);
        Assert.Contains("🛠️ " + id + " exited 0 after ", output);
        Assert.Equal(1, second.Count(m => m.Role == ChatRole.User && m.Text == "and?"));
        var messages = second.ToList();
        Assert.True(messages.FindIndex(m => m.Contents.Contains(call)) > messages.FindLastIndex(m => m.Role == ChatRole.User));   // the pair after the user's line, as the openers ride
        Assert.Equal(1, second.Count(m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId.StartsWith(Assistant.PendingCallIdPrefix, StringComparison.Ordinal))));   // seeded once
    }

    /// <summary>A script (phase C, 2026-09-21): the pane titles it as one, its caption counts the lines, the session allow is the language's pseudo-prefix, and the note is the header with the tool-call count.</summary>
    [Fact]
    public async Task ExecuteCode_ThePaneAsksForTheScript_AllowSession_RunsIt_AndTheNextScriptNeverAsks()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ShellToolBridge = true; });   // the bridge on (later on 2026-09-21 it starts off): the script calls a tool
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        string code = "$d = Invoke-NeonTool get_working_directory\nWrite-Output \"seen: $($d.Length -gt 0)\"";
        _chat.Enqueue(FakeChatClient.Call("c1", ExecuteCodeTool.ToolName, new Dictionary<string, object?> { ["language"] = "powershell", ["code"] = code }));
        _chat.Enqueue(FakeChatClient.Call("c2", ExecuteCodeTool.ToolName, new Dictionary<string, object?> { ["language"] = "powershell", ["code"] = "Write-Output again" }));
        _chat.EnqueueText("Ran twice.");
        var input = Scripted();
        StepsWhenIdle(Line("script it"), Line("/exit"));
        var idle = input.OnWait!;
        bool answered = false;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                if (!answered)
                {
                    answered = true;
                    input.Push(Keys.Char('s'), Keys.Enter);
                }

                return;
            }

            idle();
        };

        string output = await RunAsync();

        Assert.Contains("\n" + Titled(ShellText.ScriptApprovalTitle) + "\npowershell · 2 lines · first line: $d = Invoke-NeonTool get_working_directory\n \n▸ Deny\n  Allow once\n  Allow powershell scripts for this session\n  Allow powershell scripts always (saved to the profile)\n", output);
        Assert.Contains("(🔓 allowed for this session: code:powershell)\n", output);
        Assert.DoesNotContain("first line: Write-Output again", output);   // the second script rode the session's allow
        string first = ToolResult(_chat.Requests[1], "c1");
        Assert.Equal("exit 0 in 0.0 s (powershell, 1 tool call): $d = Invoke-NeonTool get_working_directory\nseen: True", first);
        Assert.Equal("exit 0 in 0.0 s (powershell, 0 tool calls): Write-Output again\nagain", ToolResult(_chat.Requests[2], "c2"));
        Assert.Contains("🛠️ exit 0 in 0.0 s (powershell, 1 tool call): $d = Invoke-NeonTool get_working_directory\n", output);
        Assert.DoesNotContain("seen: True\n", output);
        Assert.Contains(ExecuteCodeTool.ToolName, _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
        Assert.Empty(_settings.Current.ShellCommandAllowed);
        Assert.Contains(Assistant.ShellRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);   // the bridge on: the rule promises neon_tools
    }

    /// <summary>Shell tool bridge off (later on 2026-09-21, the default): the script runs on its own, the header has no tool-call clause, the rules and the tool never name neon_tools.</summary>
    [Fact]
    public async Task ExecuteCode_BridgeOff_RunsTheScriptAlone_AndNothingMentionsNeonTools()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ShellCommandPolicy = "yolo"; });
        _chat.Enqueue(FakeChatClient.Call("c1", ExecuteCodeTool.ToolName, new Dictionary<string, object?> { ["language"] = "powershell", ["code"] = "Write-Output \"bridge: [$env:NEONSIDEKICK_BRIDGE_ADDRESS]\"" }));
        _chat.EnqueueText("Ran.");
        StepsWhenIdle(Line("script it"), Line("/exit"));

        string output = await RunAsync();

        Assert.Equal("exit 0 in 0.0 s (powershell): Write-Output \"bridge: [$env:NEONSIDEKICK_BRIDGE_ADDRESS]\"\nbridge: []", ToolResult(_chat.Requests[1], "c1"));
        Assert.Contains("🛠️ exit 0 in 0.0 s (powershell): Write-Output \"bridge: [$env:NEONSIDEKICK_BRIDGE_ADDRESS]\"\n", output);
        var code = _chat.Options[0]!.Tools!.Cast<AIFunction>().Single(t => t.Name == ExecuteCodeTool.ToolName);
        Assert.Equal(ExecuteCodeTool.DescriptionWithoutBridge, code.Description);
        Assert.DoesNotContain("neon_tools", code.JsonSchema.GetRawText());
        Assert.Contains(Assistant.ShellRuleWithoutBridge, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("neon_tools", _chat.Requests[0][0].Text!, StringComparison.Ordinal);
    }

    /// <summary>The policy off: no shell tool offered, the rule gone, the group noted on /sys and /tools (2026-09-21).</summary>
    [Fact]
    public async Task Turn_PolicyOff_OffersNoShellTool_AndTheRulesLoseTheShellSentence()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ShellCommandPolicy = "off"; });
        _chat.EnqueueText("Hello.");
        _console.Profile.Height = 110;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(
            Line("hi"),
            Line("/sys"),
            input => input.Push(Keys.Right, Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        var offered = _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray();
        Assert.DoesNotContain(RunCommandTool.ToolName, offered);
        Assert.Contains(GitStatusTool.ToolName, offered);
        Assert.Equal(SkilledPrompt(false, [], web: true, ask: AskLimits.Default, markdown: true, shell: false), _chat.Requests[0][0].Text);
        Assert.DoesNotContain(Assistant.ShellRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("Shell tools — ", output);   // no Shell tools heading on the Prompt tab since 2026-09-26
        Assert.DoesNotMatch(GroupHeading("Shell"), output);   // not offered: left out of the Tools tab (2026-09-26)
    }

    /// <summary>Both shell tools switched off by name on /tools: the group is emptied, the rule goes with it (the git shape); one alone keeps the rule.</summary>
    [Fact]
    public async Task Turn_ShellToolsDisabledInTools_DropTheRule_OnlyWhenTheGroupIsEmpty()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolsDisabled = [.. d.ToolsDisabled, RunCommandTool.ToolName, ProcessTool.ToolName, ExecuteCodeTool.ToolName]; });
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        Assert.DoesNotContain(RunCommandTool.ToolName, _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
        Assert.DoesNotContain(ProcessTool.ToolName, _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
        Assert.DoesNotContain(ExecuteCodeTool.ToolName, _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
        Assert.DoesNotContain(Assistant.ShellRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turn_RunCommandAloneDisabled_KeepsProcess_AndTheRule()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolsDisabled = [.. d.ToolsDisabled, RunCommandTool.ToolName]; });
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        Assert.DoesNotContain(RunCommandTool.ToolName, _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
        Assert.Contains(ProcessTool.ToolName, _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
        Assert.Contains(Assistant.ShellRuleWithoutBridge, _chat.Requests[0][0].Text!, StringComparison.Ordinal);   // the bridge off by default (later on 2026-09-21)
    }

    /// <summary>Without the pane nothing can ask: under ask the tool answers the no-screen sentence, the allow list still lets a prefix through.</summary>
    [Fact]
    public async Task RunCommand_WithoutThePane_IsRefusedUnderAsk_UnlessThePrefixIsAllowed()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.Enqueue(FakeChatClient.Call("c1", RunCommandTool.ToolName, new Dictionary<string, object?> { ["command"] = "echo hi", ["shell"] = "cmd" }));
        _chat.Enqueue(FakeChatClient.Call("c2", RunCommandTool.ToolName, new Dictionary<string, object?> { ["command"] = "ver", ["shell"] = "cmd" }));
        _chat.EnqueueText("Done.");
        _settings.Update(d => d.ShellCommandAllowed = ["ver"]);
        PushLine("run");
        PushLine("/exit");
        var events = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category is ChatScreen.AppCategory or ShellKinds.Category) { events.Add(e); } };
        DiagnosticLog.Emitted += capture;
        string output;
        try
        {
            output = await RunAsync();
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(NeonSidekick.Shell.ShellText.NotAskable(["ver"]), ToolResult(_chat.Requests[1], "c1"));
        Assert.StartsWith("exit 0 in 0.0 s (cmd): ver\n", ToolResult(_chat.Requests[2], "c2"));
        Assert.Contains(events, e => e.Message == "approval: refused (never asked) — cmd \"echo hi\"");
        Assert.Contains(events, e => e.Message == "approval: on the allow list — cmd \"ver\"");
        Assert.Contains("🛠️ Error: the command was not approved", output);
    }

}
