using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/code-audit</c> on the screen (2026-10-18): read-only passes, each one turn, until enough clean passes in a row; <c>present_audit</c>'s
/// notices instead of its call line; the done pane's four choices; the stops, the refusals, and the refusal under a running <c>/botchat</c>.
/// </summary>
public partial class ChatScreenTests
{
    private const string AuditSource = "class A\n{\n    void Run(string name) => Execute(\"SELECT \" + name);\n}\n";

    private string AuditRoot => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);

    private WorkingDirectory AuditFiles() => new(() => AuditRoot, _time);

    /// <summary>The working directory with <c>src/a.cs</c>, the pane on, steps at the idle line.</summary>
    private void AuditFixture(params Action<ScriptedInput>[] steps)
    {
        Directory.CreateDirectory(Path.Combine(AuditRoot, "src"));
        File.WriteAllText(Path.Combine(AuditRoot, "src", "a.cs"), AuditSource);
        PlanGapsFixture([], steps);
    }

    private static ChatResponseUpdate ReadCall(string id, string path = "src/a.cs") =>
        FakeChatClient.Call(id, ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = path });

    private static ChatResponseUpdate AuditCall(string id, string json)
    {
        using var document = JsonDocument.Parse(json);
        return FakeChatClient.Call(id, PresentAuditTool.ToolName, document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));
    }

    private static ChatResponseUpdate FindingCall(string id, string title = "SQL built from input") => AuditCall(id,
        "{\"findings\":[{\"severity\":\"high\",\"dimension\":\"security\",\"file\":\"src/a.cs\",\"line\":3,\"title\":\"" + title + "\",\"detail\":\"The name is joined into the query.\",\"evidence\":\"Execute(\\\"SELECT \\\" + name)\"}]}");

    private static ChatResponseUpdate CleanCall(string id) => AuditCall(id, "{\"checked\":[\"the queries\"]}");

    private static Action<ScriptedInput> Keys2(params ConsoleKeyInfo[] keys) => input => input.Push(keys);

    /// <summary>The one report the run made: its path relative to the working directory.</summary>
    private string AuditPath => NeonSidekick.Audits.AuditFiles.List(AuditFiles()).Single().Path;

    private AuditReport AuditReportNow => NeonSidekick.Audits.AuditFiles.Read(AuditFiles(), AuditPath)!;

    /// <summary>Pass 1 finds one, pass 2 is clean, pass 3 confirms: the audit converges and the done pane opens.</summary>
    private void ThreePasses()
    {
        _chat.Enqueue(ReadCall("r1"));        // 0: pass 1 reads the file
        _chat.Enqueue(FindingCall("a1"));     // 1: and reports one finding
        _chat.EnqueueText("Pass 1 done.");    // 2
        _chat.Enqueue(CleanCall("a2"));       // 3: pass 2, clean (1 of 2)
        _chat.EnqueueText("Pass 2 done.");    // 4
        _chat.Enqueue(CleanCall("a3"));       // 5: pass 3 confirms: converged
        _chat.EnqueueText("Pass 3 done.");    // 6
    }

    [Fact]
    public async Task CodeAudit_ConvergesAfterTwoCleanPasses_ThePaneOpensOnKeep()
    {
        AuditFixture(Line("/code-audit src security"), Key(Keys.Enter), Line("/exit"));
        ThreePasses();

        string output = await RunAsync();

        Assert.Equal(7, _chat.Requests.Count);
        string path = AuditPath;
        Assert.Equal(AuditText.PassMessage("src", path, 1, 5, PlanReviewer.Model, "security"), LastUserText(_chat.Requests[0]));
        Assert.Equal(AuditText.PassMessage("src", path, 2, 5, PlanReviewer.Model, "security"), LastUserText(_chat.Requests[3]));
        Assert.Equal(AuditText.PassMessage("src", path, 3, 5, PlanReviewer.Model, "security: " + AuditText.ConfirmAngles[0], cleanSoFar: 1), LastUserText(_chat.Requests[5]));
        Assert.Contains("CODE AUDIT is on: a read-only review of src, pass 1 of 5.", _chat.Requests[0][0].Text);
        Assert.Contains("this pass checks again (clean pass 2 of 2)", _chat.Requests[5][0].Text);
        // The pass's tools: the reads and present_audit, nothing that writes, no web with web research off.
        var tools = OfferedNames(_chat.Options[0]);
        Assert.Contains(PresentAuditTool.ToolName, tools);
        Assert.Contains(ReadFileTool.ToolName, tools);
        Assert.DoesNotContain(WriteFileTool.ToolName, tools);
        Assert.DoesNotContain(RunCommandTool.ToolName, tools);
        Assert.DoesNotContain(WebSearchTool.ToolName, tools);
        Assert.DoesNotContain(PresentPlanTool.ToolName, tools);
        // present_audit's notices, never its call line.
        Assert.Contains("🔎 Pass 1: A1 [high] src/a.cs:3 — SQL built from input", output);
        Assert.DoesNotContain(TranscriptRenderer.ToolGlyph.TrimStart() + PresentAuditTool.ToolName, output);
        Assert.Contains(AuditText.PassDoneNotice(1, 5, 1, 0, 1, 1), output);
        Assert.Contains(AuditText.CleanPassNotice(2, 5, 1, 2), output);
        string name = Path.GetFileNameWithoutExtension(path);
        Assert.Contains(AuditText.ConvergedNotice(name, 3, 2, 1, 1), output);
        Assert.Contains(AuditText.Glyph + " Code audit, 3 passes", output);
        // The findings table before the pane, the pane's title and keys, Enter on Keep.
        Assert.Contains("  A1    high     src/a.cs:3  SQL built from input", output);
        Assert.Contains(AuditText.DoneTitle(name, [(AuditSeverity.High, 1)], 1, 1), output);
        Assert.Contains(AuditText.DoneKeys(hasFix: true), output);
        Assert.Contains(AuditText.KeptNotice(name), output);
        var report = AuditReportNow;
        Assert.Equal(AuditStatus.Converged, report.Header.Status);
        Assert.Equal("3 passes, 2 clean in a row, 1 of 1 files read (100%)", report.Header.Passes);
        Assert.Equal(["src/a.cs"], NeonSidekick.Audits.AuditFiles.ReadCoverage(AuditFiles(), path));
    }

    [Fact]
    public async Task CodeAudit_TheWorkingDirectoryChangedMidPass_RecordsNothing_AndStopsInItsOwnFolder()
    {
        // 2026-10-19, after the Ralph loop's reviews: the audit read and wrote its report through the working directory as the settings have it, so a
        // change mid-audit sent its writes and coverage to the new folder's .neon/audits/, where an audit of the same name could be.
        string elsewhere = Path.Combine(_settings.ProfileDirectory, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        AuditFixture(Line("/code-audit src security"), Line("/exit"));
        _chat.Enqueue(ReadCall("r1"));        // 0: pass 1 reads the file
        _chat.Enqueue(FindingCall("a1"));     // 1: the working directory moves first; then present_audit
        _chat.EnqueueText("Pass 1 done.");    // 2
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 1 && update == 0)
            {
                _settings.Update(d => d.WorkingDirectory = elsewhere);
            }

            return Task.CompletedTask;
        };

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);   // no second pass
        string path = AuditPath;                 // the audit's own folder's report
        string name = Path.GetFileNameWithoutExtension(path);
        string moved = AuditText.WorkdirMovedNotice(name, "<root>");
        Assert.Contains(moved[..moved.IndexOf("<root>", StringComparison.Ordinal)], output);
        var report = AuditReportNow;
        Assert.Equal(AuditStatus.Stopped, report.Header.Status);
        Assert.Empty(report.Open);               // present_audit recorded nothing
        Assert.Contains(_chat.Requests[2], m => m.Contents.OfType<FunctionResultContent>().Any(r => r.Result?.ToString() == AuditText.WorkdirMovedResult));
        Assert.False(Directory.Exists(Path.Combine(elsewhere, ".neon")));   // nothing written in the new folder
        // The pass is in the summary (2026-10-20, a review: it was left out while the report counted it).
        Assert.Contains(AuditText.Glyph + " Code audit, 1 pass", output);
    }

    [Fact]
    public async Task CodeAudit_AMessageFromThePageDuringAPass_StopsItAfterThePass_ThenRuns()
    {
        // 2026-10-20, a review: only the PC's queue (and only when it drained) stopped an audit, so a page's message waited out every pass.
        AuditFixture(Line("/code-audit src security"), Line("/exit"));
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 0 && update == 0)
            {
                Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = "how is it going?" }, null).Outcome);
            }

            return Task.CompletedTask;
        };
        _chat.Enqueue(ReadCall("r1"));
        _chat.Enqueue(FindingCall("a1"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.EnqueueText("Going well.");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Contains(AuditText.StopNotice(PlanGapStop.UserSpoke, Path.GetFileNameWithoutExtension(AuditPath), 1), output);
        Assert.Equal("how is it going?", LastUserText(_chat.Requests[3]));
        Assert.DoesNotContain(PresentAuditTool.ToolName, OfferedNames(_chat.Options[3]));
    }

    [Fact]
    public async Task CodeAudit_FixViaPlan_EntersPlanMode_OnTheOpenFindings()
    {
        AuditFixture(Line("/code-audit src/"), Keys2(Keys.Char('p'), Keys.Enter), Line("/exit"));
        ThreePasses();
        _chat.EnqueueText("Here is how I'd fix it.");   // 7: plan mode's first turn

        string output = await RunAsync();

        Assert.Equal(8, _chat.Requests.Count);
        string path = AuditPath;
        Assert.Equal(AuditText.FixRequirement(path, ["A1"]), LastUserText(_chat.Requests[7]));
        Assert.Contains("PLAN MODE is on.", _chat.Requests[7][0].Text);
        Assert.Contains(PresentPlanTool.ToolName, OfferedNames(_chat.Options[7]));
        Assert.Equal(AuditStatus.Fixing, AuditReportNow.Header.Status);
        Assert.Contains(AuditText.FixingNotice(Path.GetFileNameWithoutExtension(path), newest: false), output);
    }

    [Fact]
    public async Task CodeAudit_Discard_AsksThenDeletesTheReportAndItsCoverage()
    {
        AuditFixture(Line("/code-audit src/"), Keys2(Keys.Char('d'), Keys.Enter), Keys2(Keys.Char('y'), Keys.Enter), Line("/exit"));
        ThreePasses();

        string output = await RunAsync();

        Assert.Empty(NeonSidekick.Audits.AuditFiles.List(AuditFiles()));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(AuditRoot, ".neon", "audits")));
        Assert.Contains(AuditText.Glyph + " .neon/audits/", output);
    }

    [Fact]
    public async Task CodeAudit_MorePasses_RunsAFreshLoopOnTheSameReport()
    {
        _settings.Update(d => d.CodeAuditCleanPasses = 1);
        AuditFixture(Line("/code-audit src/"), Keys2(Keys.Char('g'), Keys.Enter), Key(Keys.Escape), Line("/exit"));
        _chat.Enqueue(ReadCall("r1"));        // 0
        _chat.Enqueue(FindingCall("a1"));     // 1
        _chat.EnqueueText("Pass 1.");         // 2
        _chat.Enqueue(CleanCall("a2"));       // 3: pass 2 clean: converged on one
        _chat.EnqueueText("Pass 2.");         // 4: the pane; More passes…
        _chat.Enqueue(CleanCall("a3"));       // 5: the fresh loop's pass 1: converged again
        _chat.EnqueueText("Again.");          // 6: the pane; Esc keeps

        string output = await RunAsync();

        Assert.Equal(7, _chat.Requests.Count);
        Assert.Equal(AuditText.PassMessage("src", AuditPath, 1, 5, PlanReviewer.Model, "all"), LastUserText(_chat.Requests[5]));
        Assert.Contains("- Open findings (1)", _chat.Requests[5][0].Text);
        Assert.Single(NeonSidekick.Audits.AuditFiles.List(AuditFiles()));
        Assert.Equal(AuditStatus.Converged, AuditReportNow.Header.Status);
    }

    [Fact]
    public async Task CodeAudit_AtTheLimit_ThePaneComesBack_AndANoReportPassStops()
    {
        _settings.Update(d => d.CodeAuditPasses = 1);
        AuditFixture(Line("/code-audit src/"), Key(Keys.Escape), Line("/code-audit resume"), Line("/exit"));
        _chat.Enqueue(ReadCall("r1"));        // 0
        _chat.Enqueue(FindingCall("a1"));     // 1
        _chat.EnqueueText("Pass 1.");         // 2: the limit: the pane; Esc keeps
        _chat.EnqueueText("Which database is this?");   // 3: resume's pass 1 asks in prose: no report

        string output = await RunAsync();

        string name = Path.GetFileNameWithoutExtension(AuditPath);
        Assert.Contains(AuditText.StopNotice(PlanGapStop.Limit, name, 1), output);
        Assert.Contains(AuditText.ResumedNotice(name, newest: true), output);
        Assert.Contains(AuditText.StopNotice(PlanGapStop.NoRevision, name, 1), output);
        Assert.Contains("ended without calling present_audit", output);
        Assert.Equal(AuditStatus.Stopped, AuditReportNow.Header.Status);
        Assert.EndsWith("; no report", AuditReportNow.Header.Passes);
    }

    [Fact]
    public async Task CodeAudit_TheStrip_AndTheResumeOfAGoneScope()
    {
        Assert.Equal("🔎 2/5" + ChatScreen.GlyphSeparator + "x", ChatScreen.AuditStrip("2/5", "x"));
        Assert.Equal("🔎 2", ChatScreen.AuditStrip("2", ""));
        Assert.Equal("x", ChatScreen.AuditStrip(null, "x"));

        _settings.Update(d => d.CodeAuditPasses = 1);
        AuditFixture(Line("/code-audit src/"), Key(Keys.Escape), Line("/code-audit resume"), Line("/exit"));
        _chat.Enqueue(ReadCall("r1"));
        _chat.Enqueue(FindingCall("a1"));
        _chat.BeforeUpdate = (i, _) =>
        {
            if (_chat.Requests.Count == 3 && i == 0)
            {
                Directory.Delete(Path.Combine(AuditRoot, "src"), recursive: true);   // the scope's folder gone before resume
            }

            return Task.CompletedTask;
        };
        _chat.EnqueueText("Pass 1.");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(AuditText.NoSuchPath("src"), output);
    }

    [Fact]
    public async Task CodeAudit_WebResearchOn_OffersTheWeb_AndAskUserOff_NoAsking()
    {
        _settings.Update(d => { d.WebTools = true; d.CodeAuditWebResearch = true; d.CodeAuditAskUser = false; d.CodeAuditCleanPasses = 1; });
        AuditFixture(Line("/code-audit src/"), Key(Keys.Escape), Line("/exit"));
        _chat.Enqueue(CleanCall("a1"));
        _chat.EnqueueText("Clean.");

        await RunAsync();

        var tools = OfferedNames(_chat.Options[0]);
        Assert.Contains(WebSearchTool.ToolName, tools);
        Assert.DoesNotContain(AskUserTool.ToolName, tools);
        Assert.Contains(AuditText.NoAskLine, _chat.Requests[0][0].Text);
    }

    [Fact]
    public async Task CodeAudit_TheRefusals()
    {
        AuditFixture(Line("/code-audit .neon"), Line("/code-audit src/nope.cs"), Line("/code-audit model advisor"), Line("/code-audit diff"), Line("/code-audit fix"), Line("/code-audit open"), Line("/plan Add a guide"), Line("/code-audit src/"), Line("/exit"));
        _chat.EnqueueText("What should it cover?");

        string output = await RunAsync();

        Assert.Single(_chat.Requests);   // only /plan's turn
        Assert.Contains(AuditText.AppFolderPath, output);
        Assert.Contains(AuditText.NoSuchPath("src/nope.cs"), output);
        Assert.Contains(AuditText.TwoReviewers, output);
        Assert.Contains("is not inside a git repository", output);
        Assert.Contains(AuditText.NoReportsNotice, output);
        Assert.Contains(AuditText.PlanningError, output);
    }

    [Fact]
    public async Task CodeAudit_RefusedWithToolsOff_AndWithReadFileOff()
    {
        AuditFixture(Line("/code-audit src/"), Line("/exit"));
        _settings.Update(d => d.ToolsDisabled = [ReadFileTool.ToolName]);
        string output = await RunAsync();
        Assert.Empty(_chat.Requests);
        Assert.Contains(AuditText.NeedsReadFileError, output);
    }

    [Fact]
    public async Task CodeAudit_RefusedWithOfferToolsOff()
    {
        AuditFixture(Line("/code-audit src/"), Line("/exit"));
        _settings.Update(d => d.LlmOfferTools = false);
        string output = await RunAsync();
        Assert.Empty(_chat.Requests);
        Assert.Contains(AuditText.NeedsToolsError, output);
    }

    [Fact]
    public async Task CodeAudit_APassWithdrawnByEsc_PutsNoDraftBack()
    {
        // 2026-10-19, the second review: the audit stopped, and the pass message came back on the row, so Enter sent it as an ordinary turn.
        _settings.Update(d => d.TtsOutput = false);
        AuditFixture(Line("/code-audit src/"), Key(Keys.Enter), Line("/exit"));
        _chat.EnqueueText("Never shown.");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 0 && _chat.Requests.Count == 1)
            {
                _scripted!.Push(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };

        string output = await RunAsync();

        Assert.Single(_chat.Requests);
        Assert.Contains(AuditText.StopNotice(PlanGapStop.Stopped, Path.GetFileNameWithoutExtension(AuditPath), 1), output);
        Assert.Equal(AuditStatus.Stopped, AuditReportNow.Header.Status);
    }

    [Fact]
    public void AuditSession_InPass_HoldsThroughTheConvergingPass()
    {
        // 2026-10-19, the second review: the tools' narrowing read Reviewing, false once present_audit converged, so an advisor asked later in that
        // turn was offered the web. InPass holds until the audit stops.
        var files = AuditFiles();
        var session = new AuditSession(files);
        var scope = new AuditScope(AuditScopeKind.Tree, [], null, ["src/a.cs"], 0, [], 0, 0, 0, false);
        var loop = session.Start(".neon/audits/a.md", scope, AuditFocus.Dimensions, "", PlanReviewer.Model, 5, 1);
        Assert.False(session.InPass);   // pending
        loop.BeginPass("");
        Assert.True(session.InPass && session.Reviewing);
        loop.MarkConverged();
        Assert.False(session.Reviewing);
        Assert.True(session.InPass);
        session.Stop();
        Assert.False(session.InPass);
    }

    [Fact]
    public async Task CodeAudit_ANewMidPass_StopsIt_SayingHowToGoOn()
    {
        AuditFixture(Line("/code-audit src/"), Line("/exit"));
        _chat.Enqueue(ReadCall("r1"));
        _chat.EnqueueText("Reading on…");
        PushLineDuringTurn(2, "/new");

        string output = await RunAsync();

        Assert.Contains(AuditText.StopNotice(PlanGapStop.Stopped, Path.GetFileNameWithoutExtension(AuditPath), 1), output);
        Assert.Equal(AuditStatus.Stopped, AuditReportNow.Header.Status);
    }

    [Fact]
    public async Task CodeAudit_UnderABotChat_IsRefusedAtOnce_ButOpenWaits()
    {
        BotChatFixture();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        Directory.CreateDirectory(Path.Combine(AuditRoot, "src"));
        File.WriteAllText(Path.Combine(AuditRoot, "src", "a.cs"), AuditSource);
        _chat.EnqueueText("Hello ", "from Neon.");
        _chat.EnqueueText("Hello ", "from Ada.");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 1 && i == 1)
            {
                PushLine("/code-audit src/");
                PushLine("/code-audit open");
                for (int wait = 0; wait < 1000 && !Output.Contains(AuditText.BotChatError[..30], StringComparison.Ordinal); wait++)
                {
                    await Task.Delay(10, CancellationToken.None);
                }
            }
            else if (_chat.Requests.Count == 2 && i == 1)
            {
                // The chat's stop: the first Esc cuts this bot short, the second ends the chat as the next turn starts.
                _console.Input.PushKey(Keys.Escape);
                _console.Input.PushKey(Keys.Escape);
                PushLine("/exit");
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/botchat the best pizza");

        string output = await RunAsync();

        Assert.Contains(AuditText.BotChatError[..30], output);   // the line wraps at the console's width
        Assert.Contains(AuditText.NoReportsNotice, output);       // open was held and ran after the chat
        Assert.DoesNotContain(_chat.Requests, r => r.Any(m => (m.Text ?? "").Contains("Code audit pass", StringComparison.Ordinal)));
        Assert.Empty(NeonSidekick.Audits.AuditFiles.List(AuditFiles()));
    }

    [Fact]
    public void Pend_TheRefusalsTest_ReadsTheBotchatDrainsShape()
    {
        Assert.True(AuditCommand.StartsPasses("src"));
        Assert.False(AuditCommand.StartsPasses("open"));
        // A multi-line draft or one holding a paste token is no command (CommandText null), so it is never refused.
        Assert.Null(new SubmittedLine("/code-audit\nmore", "/code-audit\nmore", [], "").CommandText);
    }
}
