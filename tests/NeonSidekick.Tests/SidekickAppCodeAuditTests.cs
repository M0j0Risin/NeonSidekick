using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/code-audit</c> headless (2026-10-18): passes chained as the next line with no stdin read, the end said with the no-pane lines; a limit
/// of 0 is the most the setting allows, <c>Advisor confirm</c> makes the model the reviewer, and the screen's refusals hold.
/// </summary>
public partial class SidekickAppTests
{
    private string HeadlessAuditRoot => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);

    private void HeadlessAuditFiles()
    {
        _settings.Update(d => d.FileTools = true);
        Directory.CreateDirectory(Path.Combine(HeadlessAuditRoot, "src"));
        File.WriteAllText(Path.Combine(HeadlessAuditRoot, "src", "a.cs"), "class A\n{\n    void Run(string name) => Execute(\"SELECT \" + name);\n}\n");
    }

    private static ChatResponseUpdate HeadlessAuditCall(string id, string json)
    {
        using var document = JsonDocument.Parse(json);
        return FakeChatClient.Call(id, PresentAuditTool.ToolName, document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));
    }

    private string HeadlessAuditPath => AuditFiles.List(new WorkingDirectory(() => HeadlessAuditRoot, TimeProvider.System)).Single().Path;

    [Fact]
    public async Task Headless_CodeAudit_ChainsPasses_UntilClean_ThenTheNoPaneLines()
    {
        _settings.Update(d => d.CodeAuditCleanPasses = 2);   // the confirming pass is this test's subject (1 is the default since 2026-11-19)
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _chat.Enqueue(FakeChatClient.Call("r1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "src/a.cs" }));
        _chat.Enqueue(HeadlessAuditCall("a1", "{\"findings\":[{\"severity\":\"high\",\"dimension\":\"security\",\"file\":\"src/a.cs\",\"line\":3,\"title\":\"SQL built from input\",\"detail\":\"Joined.\"}]}"));
        _chat.EnqueueText("Pass 1.");
        _chat.Enqueue(HeadlessAuditCall("a2", "{\"checked\":[\"the queries\"]}"));
        _chat.EnqueueText("Pass 2.");
        _chat.Enqueue(HeadlessAuditCall("a3", "{\"checked\":[\"the rest\"]}"));
        _chat.EnqueueText("Pass 3.");
        var shadow = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Message.StartsWith(AuditText.ShadowLogPrefix, StringComparison.Ordinal)) shadow.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        string output;
        try
        {
            output = await Headless("/code-audit src\n/code-audit open\n");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        // The shadow rule's line every pass (2026-11-17, step 8): pass 1 serious, passes 2 and 3 clean, the third's "would converge".
        Assert.Equal(3, shadow.Count);
        Assert.Contains("pass 1: 1 high (serious); shadow streak 0 of 2", shadow[0]);
        Assert.Contains("pass 3: no findings; shadow streak 2 of 2, would converge; real: ", shadow[2]);
        Assert.Equal(7, _chat.Requests.Count);
        string path = HeadlessAuditPath;
        string name = Path.GetFileNameWithoutExtension(path);
        Assert.Equal(AuditText.PassMessage("src", path, 1, 5, PlanReviewer.Model, "all"), HeadlessLastUser(_chat.Requests[0]));
        Assert.Contains("CODE AUDIT is on", _chat.Requests[0][0].Text);
        Assert.Contains(PresentAuditTool.ToolName, _chat.Options[0]!.Tools!.Select(t => t.Name));
        Assert.DoesNotContain(WriteFileTool.ToolName, _chat.Options[0]!.Tools!.Select(t => t.Name));
        Assert.Contains("You: " + AuditText.PassMessage("src", path, 2, 5, PlanReviewer.Model, "all"), output);
        Assert.Contains("[notice] " + AuditText.ConvergedNotice(name, 3, 2, 1, 1, pane: false), output);
        Assert.Contains("[notice] " + AuditText.NoPaneLines(name, anyOpen: true)[0], output);
        Assert.Contains("converged · 1 high", output);   // /code-audit open's list
    }

    /// <summary>
    /// Confirm focus by change in a headless audit (2026-11-18, Phase 3 item 3): pass 1 a maintainability finding, pass 2 clean, so the confirming
    /// pass 3 is told maintainability, with a <c>code audit focus:</c> line saying it was picked by change; off, it is security, in turn.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Headless_CodeAudit_ConfirmFocusByChange_TheLastFindingsDimension_ElseSecurity(bool byChange)
    {
        _settings.Update(d => d.CodeAuditCleanPasses = 2);   // the confirming pass is this test's subject (1 is the default since 2026-11-19)
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _settings.Update(d => d.ConfirmFocusByChange = byChange);
        _chat.Enqueue(FakeChatClient.Call("r1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "src/a.cs" }));
        _chat.Enqueue(HeadlessAuditCall("a1", "{\"findings\":[{\"severity\":\"medium\",\"dimension\":\"maintainability\",\"file\":\"src/a.cs\",\"line\":1,\"title\":\"One class does three jobs\",\"detail\":\"Split it.\"}]}"));
        _chat.EnqueueText("Pass 1.");
        _chat.Enqueue(HeadlessAuditCall("a2", "{\"checked\":[\"the class\"]}"));
        _chat.EnqueueText("Pass 2.");
        _chat.Enqueue(HeadlessAuditCall("a3", "{\"checked\":[\"the rest\"]}"));
        _chat.EnqueueText("Pass 3.");
        var focus = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Message.StartsWith(AuditText.FocusLogPrefix, StringComparison.Ordinal)) focus.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        string output;
        try
        {
            output = await Headless("/code-audit src\n");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        string dimension = byChange ? AuditFocus.Maintainability : AuditFocus.Security;
        Assert.Contains("You: " + AuditText.PassMessage("src", HeadlessAuditPath, 3, 5, PlanReviewer.Model, dimension, 1), output);
        Assert.Equal([byChange ? "code audit focus: pass 3: maintainability, by change (maintainability 1)" : "code audit focus: pass 3: security, in turn"], focus);
    }

    [Fact]
    public async Task Headless_CodeAudit_LowKeepsStreak_ALowPassCounts_AndNeverSaysNothingNew()
    {
        _settings.Update(d => d.CodeAuditCleanPasses = 2);   // the confirming pass is this test's subject (1 is the default since 2026-11-19)
        // 2026-11-17, phase 3 (Code audit low keeps streak on): pass 1 serious, pass 2 only a low finding (clean to the streak), pass 3 a low
        // revision that converges. The pass lines, the log and the stop say the low changes; the model hears what any recorded call hears.
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _settings.Update(d => d.CodeAuditLowKeepsStreak = true);
        _chat.Enqueue(FakeChatClient.Call("r1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "src/a.cs" }));
        _chat.Enqueue(HeadlessAuditCall("a1", "{\"findings\":[{\"severity\":\"high\",\"dimension\":\"security\",\"file\":\"src/a.cs\",\"line\":3,\"title\":\"SQL built from input\",\"detail\":\"Joined.\"}]}"));
        _chat.EnqueueText("Pass 1.");
        _chat.Enqueue(HeadlessAuditCall("a2", "{\"findings\":[{\"severity\":\"low\",\"dimension\":\"maintainability\",\"file\":\"src/a.cs\",\"line\":1,\"title\":\"Class not sealed\",\"detail\":\"Could be sealed.\"}]}"));
        _chat.EnqueueText("Pass 2.");
        _chat.Enqueue(HeadlessAuditCall("a3", "{\"revise\":[{\"id\":\"A2\",\"fix\":\"Seal it.\"}]}"));
        _chat.EnqueueText("Pass 3.");
        var log = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Message.StartsWith("code audit", StringComparison.Ordinal)) log.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        string output;
        try
        {
            output = await Headless("/code-audit src\n");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        string path = HeadlessAuditPath;
        string name = Path.GetFileNameWithoutExtension(path);
        Assert.Contains("[notice] " + AuditText.CleanPassNotice(2, 5, 1, 2, light: 1), output);
        Assert.Contains(log, l => l.StartsWith("code audit: pass 2 ended, 1 low change only", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("code audit: pass 2 clean (low changes only); new: A2 Class not sealed", StringComparison.Ordinal));
        // Pass 3 is the confirming one: told the last pass found only low changes; its result the recorded part, then the convergence.
        Assert.Contains("You: " + AuditText.PassMessage("src", path, 3, 5, PlanReviewer.Model, AuditFocus.PassFocus(AuditFocus.Dimensions, 1).Short, 1, lastLight: 1), output);
        var results = _chat.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString() ?? "").ToList();
        Assert.Contains(results, r => r.StartsWith("Recorded in " + path + ": A2 revised. Nothing new but low changes: the audit converged after 3 passes (2 clean passes in a row)", StringComparison.Ordinal));
        Assert.Contains("[notice] " + AuditText.ConvergedNotice(name, 3, 2, 1, 1, pane: false, light: true), output);
        Assert.DoesNotContain(log, l => l.StartsWith("code audit: pass 2 ended, nothing new", StringComparison.Ordinal) || l.StartsWith("code audit: pass 3 ended, nothing new", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith(AuditText.ShadowLogPrefix + " pass 3: no findings; shadow streak 2 of 2, would converge; real: converged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Headless_CodeAudit_TheWorkingDirectoryChangedMidPass_StopsInItsOwnFolder()
    {
        // 2026-10-19, after the Ralph loop's reviews: the report and its coverage followed the working directory as the settings have it.
        ServerOn1234("llama");
        HeadlessAuditFiles();
        string elsewhere = Path.Combine(_settings.ProfileDirectory, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        _chat.Enqueue(FakeChatClient.Call("r1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "src/a.cs" }));
        _chat.Enqueue(HeadlessAuditCall("a1", "{\"findings\":[{\"severity\":\"high\",\"dimension\":\"security\",\"file\":\"src/a.cs\",\"line\":3,\"title\":\"SQL built from input\",\"detail\":\"Joined.\"}]}"));
        _chat.EnqueueText("Pass 1.");
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 1 && update == 0)
            {
                _settings.Update(d => d.WorkingDirectory = elsewhere);
            }

            return Task.CompletedTask;
        };

        string output = await Headless("/code-audit src\n", exitCode: 4);

        Assert.Equal(3, _chat.Requests.Count);
        var report = AuditFiles.Read(new WorkingDirectory(() => HeadlessAuditRoot, TimeProvider.System), HeadlessAuditPath)!;
        Assert.Equal(AuditStatus.Stopped, report.Header.Status);
        Assert.Empty(report.Open);
        string moved = AuditText.WorkdirMovedNotice(Path.GetFileNameWithoutExtension(HeadlessAuditPath), "<root>");
        Assert.Contains(moved[..moved.IndexOf("<root>", StringComparison.Ordinal)], output);
        Assert.False(Directory.Exists(Path.Combine(elsewhere, ".neon")));
    }

    [Fact]
    public async Task Headless_CodeAudit_AMoveWithTheLockTakenOver_WritesNothing()
    {
        // 2026-10-30, review F111: the moved path merged the sidecar and saved the header without checking the hold.
        ServerOn1234("llama");
        HeadlessAuditFiles();
        string elsewhere = Path.Combine(_settings.ProfileDirectory, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        _chat.Enqueue(FakeChatClient.Call("r1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "src/a.cs" }));
        _chat.Enqueue(HeadlessAuditCall("a1", "{\"findings\":[{\"severity\":\"high\",\"dimension\":\"security\",\"file\":\"src/a.cs\",\"line\":3,\"title\":\"SQL built from input\",\"detail\":\"Joined.\"}]}"));
        _chat.EnqueueText("Pass 1.");
        string? path = null;
        string? before = null;
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 1 && update == 0)
            {
                path = HeadlessAuditPath;
                string lockFile = Path.Combine(HeadlessAuditRoot, AuditFiles.LockPathFor(path).Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllText(lockFile, "pid: 424242\nstarted: 2026-10-29T09:00:00.0000000+00:00\n");
                before = File.ReadAllText(Path.Combine(HeadlessAuditRoot, path.Replace('/', Path.DirectorySeparatorChar)));
                _settings.Update(d => d.WorkingDirectory = elsewhere);
            }

            return Task.CompletedTask;
        };
        var alive = OwnerLock.LiveOwner;
        OwnerLock.LiveOwner = owner => owner.ProcessId == 424242 || owner == OwnerLock.Me;
        string output;
        try
        {
            output = await Headless("/code-audit src\n", exitCode: 4);
        }
        finally
        {
            OwnerLock.LiveOwner = alive;
        }

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(AuditText.LockLostNotice(Path.GetFileNameWithoutExtension(path!)), output);
        Assert.Equal(before, File.ReadAllText(Path.Combine(HeadlessAuditRoot, path!.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(HeadlessAuditRoot, AuditFiles.CoveragePath(path).Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Headless_CodeAudit_ATransientFailure_IsRetried_WithItsReadsForgotten()
    {
        // 2026-11-17, the gap-audit-reliability plan's step 2.
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _settings.Update(d => d.CodeAuditPasses = 1);
        _chat.Enqueue(FakeChatClient.Call("r1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "src/a.cs" }));   // 0
        _chat.EnqueueText("(never seen)");                                                                                           // 1: refused
        _chat.Enqueue(FakeChatClient.Call("r2", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "src/a.cs" }));   // 2: the retry
        _chat.Enqueue(HeadlessAuditCall("a1", "{\"findings\":[{\"severity\":\"high\",\"dimension\":\"security\",\"file\":\"src/a.cs\",\"line\":3,\"title\":\"SQL built from input\",\"detail\":\"Joined.\"}]}"));
        _chat.EnqueueText("Pass 1.");
        _chat.BeforeUpdateOf = (request, update, _) => request == 1
            ? throw new System.Net.Http.HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))
            : Task.CompletedTask;

        string output = await Headless("/code-audit src\n");

        Assert.Equal(5, _chat.Requests.Count);
        string path = HeadlessAuditPath;
        Assert.Contains("[notice] " + AuditText.RetryNotice(Path.GetFileNameWithoutExtension(path), 1, TimeSpan.Zero), output);
        Assert.Equal(AuditText.PassMessage("src", path, 1, 1, PlanReviewer.Model, "all"), HeadlessLastUser(_chat.Requests[2]));
        Assert.Contains("- Not read by any pass yet (", _chat.Requests[2][0].Text);
        Assert.Contains("[notice] " + AuditText.StopNotice(PlanGapStop.Limit, Path.GetFileNameWithoutExtension(path), 1), output);
    }

    [Fact]
    public async Task Headless_CodeAudit_NoLimitMeansTheMost_AndAdvisorConfirmFallsBack()
    {
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _settings.Update(d =>
        {
            d.CodeAuditPasses = 0;
            d.Advisors = true;
            d.AdvisorConfirm = true;
            d.AdvisorList = [new AdvisorEntry("dba", "database architect")];
        });
        _chat.EnqueueText("Which database is this?");
        _chat.EnqueueText("I need the database first.");   // the pass's one follow-up (2026-11-17): still no report

        string output = await Headless("/code-audit advisor src\n");

        Assert.Contains("[notice] " + AuditText.ConfirmFallbackNotice, output);
        Assert.Contains("[notice] " + AuditText.HeadlessCapNotice(AppSettingsData.MaxCodeAuditPasses, fromCommand: false), output);
        Assert.Equal(AuditText.PassMessage("src", HeadlessAuditPath, 1, AppSettingsData.MaxCodeAuditPasses, PlanReviewer.Model, "all"), HeadlessLastUser(_chat.Requests[0]));
        Assert.Equal(AuditText.FollowUpMessage(Path.GetFileNameWithoutExtension(HeadlessAuditPath), 1), HeadlessLastUser(_chat.Requests[1]));
        Assert.Contains("[notice] " + AuditText.StopNotice(PlanGapStop.NoRevision, Path.GetFileNameWithoutExtension(HeadlessAuditPath), 1), output);
    }

    [Fact]
    public async Task Headless_CodeAudit_TheRefusals()
    {
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _settings.Update(d => d.ToolsDisabled = [ReadFileTool.ToolName]);
        _chat.EnqueueText("What should it cover?");

        string output = await Headless("/code-audit src\n/plan Add a guide\n/code-audit src\n", exitCode: 4);

        Assert.Contains("[error] " + AuditText.NeedsReadFileError, output);
        Assert.Contains("[error] " + AuditText.PlanningError, output);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task Headless_CodeAudit_ACommitScope_ItsNoticeAndDiffCall_AndOnResumeTheFilesChangedSince()
    {
        // 2026-11-25, /code-audit commit: the notice after the start's, the directive's gitlib_diff call with GitLib tools on, and after an edit
        // the resume's notice names the file changed since.
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _settings.Update(d => { d.CodeAuditPasses = 1; d.GitLibTools = true; });
        string file = Path.Combine(HeadlessAuditRoot, "src", "a.cs");
        GitAccessTests.Init(HeadlessAuditRoot);
        string sha = GitAccessTests.CommitFile(HeadlessAuditRoot, "src/a.cs", File.ReadAllText(file), "first");
        _chat.Enqueue(FakeChatClient.Call("r1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "src/a.cs" }));
        _chat.Enqueue(HeadlessAuditCall("a1", "{\"findings\":[{\"severity\":\"high\",\"dimension\":\"security\",\"file\":\"src/a.cs\",\"line\":3,\"title\":\"SQL built from input\",\"detail\":\"Joined.\"}]}"));
        _chat.EnqueueText("Pass 1.");
        _chat.Enqueue(HeadlessAuditCall("a2", "{\"checked\":[\"the rest\"]}"));
        _chat.EnqueueText("Pass 2.");
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 2)
            {
                File.AppendAllText(file, "// edited after the commit\n");
            }

            return Task.CompletedTask;
        };

        string output = await Headless("/code-audit commit HEAD\n/code-audit resume\n");

        string shortSha = sha[..NeonSidekick.Git.GitAccess.ShortShaLength];
        Assert.Contains($"[notice] {AuditText.Glyph} HEAD is {shortSha}.", output);
        Assert.Contains($"gitlib_diff with ref={sha} shows exactly what it changed.", _chat.Requests[0][0].Text);
        Assert.Contains($"[notice] {AuditText.Glyph} 1 of the files has changed since {shortSha}", output);
        Assert.Contains("- These files changed after it (1 file): ", _chat.Requests[3][0].Text);
    }
}
