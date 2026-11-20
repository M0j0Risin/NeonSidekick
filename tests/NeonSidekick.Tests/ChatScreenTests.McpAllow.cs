using Microsoft.Extensions.AI;
using NeonSidekick.Camera;
using NeonSidekick.Mcp;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// MCP calls that ask, on the screen (2026-11-19, feature idea #28): the allow pane titled with the server, Allow once and Allow for this
/// session (one tool of one server, ordinal), Deny as the model's answer, <c>/new</c> forgetting, the caption's notes, and the page's answer.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>
    /// <paramref name="json"/> as the profile's <c>mcp.json</c>, the pane on, the model's <paramref name="calls"/> (tool name, text) then
    /// <paramref name="reply"/>, the <paramref name="lines"/> typed in turn; each of <paramref name="answers"/> pushed at one new pane (any of
    /// <paramref name="titles"/>), so one ask more than answers leaves the script dry.
    /// </summary>
    private void McpAllowFixture(string json, string[] titles, ConsoleKeyInfo[][] answers, string[] lines, params (string Tool, string Text)[] calls)
    {
        _settings.Update(d => d.TtsOutput = false);
        McpServer(json);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);   // the pane: nothing can ask without it
        for (int i = 0; i < calls.Length; i++)
        {
            _chat.Enqueue(FakeChatClient.Call("c" + i, calls[i].Tool, new Dictionary<string, object?> { [McpPipeServer.TextArgument] = calls[i].Text }));
        }

        var input = Scripted();
        StepsWhenIdle([.. lines.Select(Line), Line("/exit")]);
        var idle = input.OnWait!;
        var pending = new Queue<ConsoleKeyInfo[]>(answers);
        int asked = 0;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                int shown = titles.Sum(t => CountOf(_console.Output, t));
                if (shown > asked && pending.Count > 0)
                {
                    asked = shown;
                    input.Push(pending.Dequeue());
                }

                return;
            }

            idle();
        };
    }

    private const string PipeOnly = """{ "mcpServers": { "pipe": { "command": "pipe-server" } } }""";

    private static readonly ConsoleKeyInfo[] Once = [Keys.Char('o'), Keys.Enter];
    private static readonly ConsoleKeyInfo[] ForSession = [Keys.Char('s'), Keys.Enter];
    private static readonly ConsoleKeyInfo[] Deny = [Keys.Enter];

    /// <summary><see cref="RunAsync"/>, with the MCP audit lines it logged (one per call that asked, by its outcome).</summary>
    private async Task<(string Output, List<string> Audit)> RunMcpAsync()
    {
        var audit = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e =>
        {
            if (e.Category == McpConfigFile.Category)
            {
                lock (audit)
                {
                    audit.Add(e.Message);
                }
            }
        };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            string output = await RunAsync();
            lock (audit)
            {
                return (output, [.. audit]);
            }
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }
    }

    private static int Asked(List<string> audit, string name, string outcome) =>
        audit.Count(l => l.StartsWith(McpText.AuditPrefix(name, outcome), StringComparison.Ordinal));

    private List<string> McpResults() =>
        [.. _chat.Requests.SelectMany(r => r).SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => (string)r.Result!).Distinct()];

    [Fact]
    public async Task McpAllow_Once_RunsTheCall_AndTheNextCallAsksAgain_TheCaptionSayingTheServerDoesntSay()
    {
        McpAllowFixture(PipeOnly, [McpText.AllowTitle("pipe")], [Once, Once], ["please run it twice"], ("pipe__echo", "a"), ("pipe__echo", "b"));
        _chat.EnqueueText("Both echoed.");

        var (output, audit) = await RunMcpAsync();

        Assert.Equal(2, Asked(audit, "pipe__echo", McpText.AllowedOnce));
        Assert.Contains(McpText.AllowCaption("pipe", "echo", McpChangeNote.Unstated, cut: false), output);
        Assert.Equal([("echo", """{"text":"a"}"""), ("echo", """{"text":"b"}""")], _mcpServers.Server("pipe").Calls);
    }

    [Fact]
    public async Task McpAllow_ForTheSession_HoldsForThatToolAlone_AndDenyIsTheModelsAnswer()
    {
        McpAllowFixture(PipeOnly, [McpText.AllowTitle("pipe")], [ForSession, Deny], ["please run them"],
            ("pipe__echo", "a"), ("pipe__echo", "b"), ("pipe__fail", "c"));
        _chat.EnqueueText("Declined.");

        var (_, audit) = await RunMcpAsync();

        Assert.Equal(1, Asked(audit, "pipe__echo", McpText.AllowedForSession));
        Assert.Equal(1, Asked(audit, "pipe__echo", McpText.AllowedEarlier));   // the second echo, never shown the pane
        Assert.Equal(1, Asked(audit, "pipe__fail", McpText.DeniedWord));
        Assert.Equal([("echo", """{"text":"a"}"""), ("echo", """{"text":"b"}""")], _mcpServers.Server("pipe").Calls);
        Assert.Contains(McpText.ErrorPrefix + McpText.DeniedError("pipe__fail"), McpResults());
    }

    [Fact]
    public async Task McpAllow_ForTheSession_NeverReachesAServerWhoseNameDiffersOnlyInCase()
    {
        McpAllowFixture("""{ "mcpServers": { "pipe": { "command": "pipe-server" }, "Pipe": { "command": "pipe-server" } } }""",
            [McpText.AllowTitle("pipe"), McpText.AllowTitle("Pipe")], [ForSession, Deny], ["please run both"], ("pipe__echo", "a"), ("Pipe__echo", "b"));
        _chat.EnqueueText("One ran.");

        string output = await RunAsync();

        Assert.Contains(McpText.AllowTitle("Pipe"), output);
        Assert.Contains(McpText.ErrorPrefix + McpText.DeniedError("Pipe__echo"), McpResults());
        Assert.Empty(_mcpServers.Server("Pipe").Calls);
    }

    [Fact]
    public async Task McpAllow_New_ForgetsTheSessionsAllow()
    {
        McpAllowFixture(PipeOnly, [McpText.AllowTitle("pipe")], [ForSession, Once], ["please run it", "/new", "please run it again"], ("pipe__echo", "a"));
        _chat.EnqueueText("Echoed.");
        _chat.Enqueue(FakeChatClient.Call("c9", "pipe__echo", new Dictionary<string, object?> { [McpPipeServer.TextArgument] = "b" }));
        _chat.EnqueueText("Echoed again.");

        var (_, audit) = await RunMcpAsync();

        Assert.Equal(1, Asked(audit, "pipe__echo", McpText.AllowedForSession));
        Assert.Equal(1, Asked(audit, "pipe__echo", McpText.AllowedOnce));   // asked again after /new
        Assert.Equal(0, Asked(audit, "pipe__echo", McpText.AllowedEarlier));
        Assert.Equal(2, _mcpServers.Server("pipe").Calls.Count);
    }

    [Fact]
    public async Task McpAllow_AWideArgument_IsCutInTheView_AndTheCaptionSaysSo()
    {
        McpAllowFixture(PipeOnly, [McpText.AllowTitle("pipe")], [Deny], ["please run the long one"], ("pipe__echo", new string('w', 5000)));
        _chat.EnqueueText("Declined.");

        string output = await RunAsync();

        Assert.Contains(McpText.AllowCaption("pipe", "echo", McpChangeNote.Unstated, cut: true), output);
        Assert.Contains(McpText.ViewCutNote, McpText.AllowCaption("pipe", "echo", McpChangeNote.Unstated, cut: true));
        Assert.Empty(_mcpServers.Server("pipe").Calls);
    }

    [Fact]
    public async Task McpAllow_AnsweredFromThePage_RunsTheCall()
    {
        var failures = OnThePage("allow", (web, ask) => Answered(web, ask, "once"));
        McpAllowFixture(PipeOnly, [McpText.AllowTitle("pipe")], [], ["please run it from the phone"], ("pipe__echo", "p"));
        _chat.EnqueueText("Echoed.");

        string output = await RunAsync();

        Assert.Empty(failures);
        Assert.Equal([("echo", """{"text":"p"}""")], _mcpServers.Server("pipe").Calls);
        Assert.Contains(WebUiText.AnsweredNotice(CameraText.AllowOnceRow), output);
    }
    /// <summary>An unattended Ralph pass (no checkpoint) is offered no MCP tool that would ask; a read-only one stays (2026-11-19).</summary>
    [Fact]
    public async Task McpAllow_AnUnattendedRalphPass_LeavesOutTheAskingMcpTools()
    {
        RalphPlan();
        _mcpServers.ReadOnly("pipe", "echo");
        McpServer(PipeOnly);
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphPass("1", "blocked", "Stop here.");

        await RunAsync();

        var offered = OfferedNames(_chat.Options[0]);
        Assert.Contains("pipe__echo", offered);
        Assert.DoesNotContain("pipe__fail", offered);
    }

    /// <summary>
    /// A server's notes (2026-11-19, feature idea #59) ride the turn's system message, fenced, while one of its tools is offered; with
    /// every tool of it off by name, none of its notes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpNotes_RideTheTurn_UnlessEveryToolOfTheServerIsOff(bool allOff)
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolsDisabled = allOff ? ["pipe__echo", "pipe__fail"] : ["pipe__fail"]; });
        _mcpServers.ReadOnly("pipe", "echo");   // offered with no pane to ask on (2026-11-20, #55)
        _mcpServers.Instructions("pipe", "Call echo with short text.");
        McpServer(PipeOnly);
        _chat.EnqueueText("Noted.");
        PushLine("hello there");
        PushLine("/exit");

        await RunAsync();

        string system = _chat.Requests[0][0].Text;
        string section = McpText.NotesSection([new McpServerNote("pipe", "Call echo with short text.")]);
        if (allOff)
        {
            Assert.DoesNotContain(McpText.NotesHeading, system);
        }
        else
        {
            Assert.Contains("\n\n" + section, system);
        }
    }
}
