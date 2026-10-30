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
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _chat.Enqueue(FakeChatClient.Call("r1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "src/a.cs" }));
        _chat.Enqueue(HeadlessAuditCall("a1", "{\"findings\":[{\"severity\":\"high\",\"dimension\":\"security\",\"file\":\"src/a.cs\",\"line\":3,\"title\":\"SQL built from input\",\"detail\":\"Joined.\"}]}"));
        _chat.EnqueueText("Pass 1.");
        _chat.Enqueue(HeadlessAuditCall("a2", "{\"checked\":[\"the queries\"]}"));
        _chat.EnqueueText("Pass 2.");
        _chat.Enqueue(HeadlessAuditCall("a3", "{\"checked\":[\"the rest\"]}"));
        _chat.EnqueueText("Pass 3.");

        string output = await Headless("/code-audit src\n/code-audit open\n");

        Assert.Equal(7, _chat.Requests.Count);
        string path = HeadlessAuditPath;
        string name = Path.GetFileNameWithoutExtension(path);
        Assert.Equal(AuditText.PassMessage("src", path, 1, 5, PlanReviewer.Model, "all"), HeadlessLastUser(_chat.Requests[0]));
        Assert.Contains("CODE AUDIT is on", _chat.Requests[0][0].Text);
        Assert.Contains(PresentAuditTool.ToolName, _chat.Options[0]!.Tools!.Select(t => t.Name));
        Assert.DoesNotContain(WriteFileTool.ToolName, _chat.Options[0]!.Tools!.Select(t => t.Name));
        Assert.Contains("You: " + AuditText.PassMessage("src", path, 2, 5, PlanReviewer.Model, "all"), output);
        Assert.Contains("[notice] " + AuditText.ConvergedNotice(name, 3, 2, 1, 1), output);
        Assert.Contains("[notice] " + AuditText.NoPaneLines(name, anyOpen: true)[0], output);
        Assert.Contains("converged · 1 high", output);   // /code-audit open's list
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

        string output = await Headless("/code-audit src\n");

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
            output = await Headless("/code-audit src\n");
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

        string output = await Headless("/code-audit advisor src\n");

        Assert.Contains("[notice] " + AuditText.ConfirmFallbackNotice, output);
        Assert.Contains("[notice] " + AuditText.HeadlessCapNotice(AppSettingsData.MaxCodeAuditPasses, fromCommand: false), output);
        Assert.Equal(AuditText.PassMessage("src", HeadlessAuditPath, 1, AppSettingsData.MaxCodeAuditPasses, PlanReviewer.Model, "all"), HeadlessLastUser(_chat.Requests[0]));
        Assert.Contains("[notice] " + AuditText.StopNotice(PlanGapStop.NoRevision, Path.GetFileNameWithoutExtension(HeadlessAuditPath), 1), output);
    }

    [Fact]
    public async Task Headless_CodeAudit_TheRefusals()
    {
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _settings.Update(d => d.ToolsDisabled = [ReadFileTool.ToolName]);
        _chat.EnqueueText("What should it cover?");

        string output = await Headless("/code-audit src\n/plan Add a guide\n/code-audit src\n");

        Assert.Contains("[error] " + AuditText.NeedsReadFileError, output);
        Assert.Contains("[error] " + AuditText.PlanningError, output);
        Assert.Single(_chat.Requests);
    }
}
