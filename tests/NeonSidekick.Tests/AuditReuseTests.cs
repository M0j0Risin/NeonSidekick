using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// What the code audit (2026-10-18) changed in the pieces it reuses: <c>PlanSlug</c>'s folder overloads, <c>PlanGapLoop</c>'s focus count,
/// the reviewer page's overload, the sandbox's viewed files and <c>ListFiles</c>, and the compactor's stub of <c>present_audit</c>'s arrays.
/// </summary>
public sealed class AuditReuseTests : IDisposable
{
    private readonly AuditSandbox _box = new();

    public void Dispose() => _box.Dispose();

    // ── PlanSlug ─────────────────────────────────────────────────────────────

    [Fact]
    public void PlanSlug_FolderOverloads_AndThePlansPathsUnchanged()
    {
        Assert.Equal(".neon/audits/x-2.md", PlanSlug.PathFor(AuditFiles.Folder, "x", 2));
        Assert.Equal(".neon/audits/x.md", PlanSlug.Choose(AuditFiles.Folder, "x", _ => false, "s"));
        Assert.Equal(".neon/audits/x-stamp.md", PlanSlug.Choose(AuditFiles.Folder, "x", _ => true, "stamp"));
        Assert.Equal(".neon/plans/x.md", PlanSlug.PathFor("x"));
        Assert.Equal(".neon/plans/x-3.md", PlanSlug.PathFor("x", 3));
        Assert.Equal(".neon/plans/x.md", PlanSlug.Choose("x", _ => false, "s"));
    }

    // ── PlanGapLoop ──────────────────────────────────────────────────────────

    [Fact]
    public void PlanGapLoop_FocusCount_DrivesTheRecordsRotation()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 0, 1, focusCount: 2);
        var plain = new PlanGapLoop(PlanReviewer.Model, 0, 1);
        foreach (var l in new[] { loop, plain })
        {
            for (int i = 0; i < 6; i++)
            {
                l.BeginPass("b");
                l.MarkClean(["x"]);
                l.Next(endedOnItsOwn: true, userSpoke: false, "b");
            }
        }

        Assert.Equal([-1, 0, 1, 0, 1, 0], loop.History.Select(p => p.Focus));
        Assert.Equal([-1, 0, 1, 2, 3, 0], plain.History.Select(p => p.Focus));
    }

    // ── The reviewer page ────────────────────────────────────────────────────

    [Fact]
    public void ReviewerPage_ThePlansWordsUnchanged_TheAuditsItsOwn()
    {
        var plan = PlanApprovalMenu.ReviewerPage(new PlanPresentation("Guide", ".neon/plans/g.md", 2, "# Guide"));
        Assert.Equal(PlanText.ReviewerTitle, plan.Title);
        Assert.Equal(PlanText.ReviewerRows.Select(Markup.Escape), plan.Rows);
        var audit = AuditDoneMenu.ReviewerPage(new AuditDone("r", ".neon/audits/r.md", "title", "caption", true));
        Assert.Equal(AuditText.ReviewerTitle, audit.Title);
        Assert.Equal(AuditText.ReviewerRows.Select(Markup.Escape), audit.Rows);
        Assert.Equal("caption", audit.Caption);
        Assert.Equal(PlanApprovalMenu.ReviewerHotkeys, audit.Hotkeys);
    }

    [Fact]
    public void DonePane_RowsHotkeysAndStart_FromOneList()
    {
        var with = AuditDoneMenu.Page(new AuditDone("r", "p", "t", "c", HasFix: true));
        Assert.Equal([AuditText.FixRow, AuditText.MoreRow, AuditText.KeepRow, AuditText.DiscardRow], with.Rows);
        Assert.Equal(2, AuditDoneMenu.StartRow(hasFix: true));
        Assert.Equal(AuditText.DoneKeys(true), with.Hint);
        Assert.Equal(new Dictionary<char, int> { ['p'] = 0, ['g'] = 1, ['k'] = 2, ['d'] = 3 }, with.Hotkeys);
        var without = AuditDoneMenu.Page(new AuditDone("r", "p", "t", "c", HasFix: false));
        Assert.Equal([AuditText.MoreRow, AuditText.KeepRow, AuditText.DiscardRow], without.Rows);
        Assert.Equal(1, AuditDoneMenu.StartRow(hasFix: false));
        Assert.Equal(AuditText.DoneKeys(false), without.Hint);
        Assert.Equal(["more", "keep", "discard"], AuditDoneMenu.Entries(false).Select(e => e.Id));
    }

    // ── The sandbox ──────────────────────────────────────────────────────────

    [Fact]
    public void Viewed_AWindowMarksViewedNotSeen_AWholeReadBoth_AChangeUnmarks()
    {
        _box.Write("a.txt", "1\n2\n3\n");
        var read = new ReadFileTool(_box.Files);
        read.Describe("a.txt", 2, 1);
        Assert.True(_box.Files.WasViewed("a.txt"));
        Assert.False(_box.Files.WasSeen("a.txt"));
        read.Describe("a.txt", null, null);
        Assert.True(_box.Files.WasSeen("a.txt"));
        _box.Write("a.txt", "changed, longer\n");
        Assert.False(_box.Files.WasViewed("a.txt"));
        Assert.False(_box.Files.WasSeen("a.txt"));
        read.Describe("a.txt", 1, 1);
        _box.Files.ForgetSeen();
        Assert.False(_box.Files.WasViewed("a.txt"));
    }

    [Fact]
    public void Viewed_UnderSeenOff_MarksNothing()
    {
        _box.Write("a.txt", "1\n");
        using (WorkingDirectory.SeenOff())
        {
            new ReadFileTool(_box.Files).Describe("a.txt", null, null);
        }

        Assert.False(_box.Files.WasViewed("a.txt"));
    }

    [Fact]
    public void ListFiles_SkipsWhatItIsTold_AndSaysWhenCut()
    {
        _box.Write("src/a.cs", "a");
        _box.Write("src/bin/b.cs", "b");
        _box.Write("c.txt", "c");
        var listed = _box.Files.ListFiles("", 100, name => name == "bin", CancellationToken.None);
        Assert.Equal(FileOutcome.Ok, listed.Outcome);
        Assert.Equal(["c.txt", Path.Combine("src", "a.cs")], listed.Files.Order(StringComparer.Ordinal));
        Assert.False(listed.Cut);
        var cut = _box.Files.ListFiles("", 1, null, CancellationToken.None);
        Assert.Single(cut.Files);
        Assert.True(cut.Cut);
        Assert.Equal([Path.Combine("src", "a.cs")], _box.Files.ListFiles("src/a.cs", 10, null, CancellationToken.None).Files);
        Assert.Throws<OperationCanceledException>(() => _box.Files.ListFiles("", 100, null, new CancellationToken(canceled: true)));
        Assert.Equal(FileOutcome.OutsideRoot, _box.Files.ListFiles("..", 10, null, CancellationToken.None).Outcome);
    }

    [WindowsFact]
    public void ListFiles_NeverFollowsALinkOutOfTheSandbox()
    {
        string outside = Path.Combine(_box.Dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_box.Root, "link"), outside);
        }
        catch (IOException)
        {
            return;   // no right to make links on this machine
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.DoesNotContain(_box.Files.ListFiles("", 100, null, CancellationToken.None).Files, f => f.Contains("secret", StringComparison.Ordinal));
    }

    [UnixFact]
    public void ListFiles_KeepsDotNames_OffWindows_ButNeverDotGit()
    {
        _box.Write(".gitignore", "x");
        _box.Write(".git/config", "x");
        var files = _box.Files.ListFiles("", 100, null, CancellationToken.None).Files;
        Assert.Contains(".gitignore", files);
        Assert.DoesNotContain(files, f => f.StartsWith(".git/", StringComparison.Ordinal));
    }

    // ── The compactor ────────────────────────────────────────────────────────

    private static ChatMessage Call(string id, string name, string key, object? value) =>
        new(ChatRole.Assistant, [new FunctionCallContent(id, name, new Dictionary<string, object?> { [key] = value })]);

    private static JsonElement Array(int items)
    {
        string json = "[" + string.Join(",", Enumerable.Range(0, items).Select(i => "{\"title\":\"finding number " + i + " with words\"}")) + "]";
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Compactor_StubsAPresentAuditArray_InAnEarlierIteration_NeverTheLast()
    {
        var history = new ConversationHistory("s");
        history.AddUser("audit");
        var big = Array(80);
        history.AddMessage(Call("a1", PresentAuditTool.ToolName, PresentAuditTool.FindingsArgument, big));
        history.AddToolResults([new FunctionResultContent("a1", "Recorded")]);
        history.AddMessage(Call("q1", AskUserTool.ToolName, "questions", Array(80)));
        history.AddToolResults([new FunctionResultContent("q1", "answered")]);
        history.AddMessage(Call("a2", PresentAuditTool.ToolName, PresentAuditTool.FindingsArgument, Array(80)));
        history.AddToolResults([new FunctionResultContent("a2", "Recorded")]);
        var messages = history.Messages.ToList();

        var (pruned, _) = ConversationCompactor.PruneRecent(messages);

        var stubbed = ((FunctionCallContent)pruned[1].Contents[0]).Arguments![PresentAuditTool.FindingsArgument];
        Assert.Equal(ConversationCompactor.PrunedArgumentStub(big.GetRawText().Length), stubbed);
        Assert.Same(messages[3], pruned[3]);   // ask_user's array is never stubbed
        Assert.Same(messages[5], pruned[5]);   // the last iteration's call
        Assert.Equal([PresentAuditTool.ToolName], ConversationCompactor.RecordedArguments);
    }

    [Fact]
    public void Compactor_LeavesAShortArray_AndAnyOtherShape()
    {
        var history = new ConversationHistory("s");
        history.AddUser("audit");
        history.AddMessage(Call("a1", PresentAuditTool.ToolName, PresentAuditTool.FindingsArgument, Array(2)));
        history.AddToolResults([new FunctionResultContent("a1", "Recorded")]);
        history.AddMessage(Call("a2", PresentAuditTool.ToolName, PresentAuditTool.FindingsArgument, Enumerable.Range(0, 500).Select(i => "a long finding title " + i).ToList()));
        history.AddToolResults([new FunctionResultContent("a2", "Recorded")]);
        history.AddMessage(Call("a3", PresentAuditTool.ToolName, PresentAuditTool.CheckedArgument, Array(1)));
        history.AddToolResults([new FunctionResultContent("a3", "Recorded")]);
        var messages = history.Messages.ToList();

        var (pruned, _) = ConversationCompactor.PruneRecent(messages);

        Assert.Same(messages[1], pruned[1]);
        Assert.Same(messages[3], pruned[3]);   // a CLR list: never serialized to be measured
    }

    [Fact]
    public void Compactor_StubSupersededPlans_LeavesPresentAuditAlone()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "audit"),
            Call("a1", PresentAuditTool.ToolName, PresentAuditTool.FindingsArgument, Array(80)),
            new(ChatRole.Tool, [new FunctionResultContent("a1", "Recorded")]),
            Call("p1", PresentPlanTool.ToolName, PresentPlanTool.MarkdownArgument, new string('x', 3000)),
            new(ChatRole.Tool, [new FunctionResultContent("p1", "saved")]),
        };

        var (stubbed, _, _) = ConversationCompactor.StubSupersededPlans(messages);
        Assert.Same(messages[1], stubbed[1]);
    }
}
