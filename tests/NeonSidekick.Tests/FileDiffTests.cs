using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The diff under a file edit (2026-10-03, the user's ask: Claude Code's look): <see cref="FileDiff"/> made where the write
/// happens, carried beside the model's sentence (<see cref="ToolDiffResult"/>, <see cref="TurnEvent.ToolResult.Diff"/>), drawn by
/// <see cref="DiffView"/> and counted once by a tool run.
/// </summary>
public sealed class FileDiffTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly WorkingDirectory _files;

    public FileDiffTests()
    {
        _root = Path.Combine(_dir, "files");
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Put(string relative, string text)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + "\n"));

    private static string Numbers(int n, Func<int, string?>? change = null) =>
        Lines(Enumerable.Range(1, n).Select(i => change?.Invoke(i) ?? "line " + i).ToArray());

    private static string[] Render(IRenderable view, int width = 60)
    {
        var console = new TestConsole();
        console.Profile.Width = width;
        console.Write(view);
        return console.Lines.Select(l => l.TrimEnd()).ToArray();
    }

    private static List<Segment> Segments(IRenderable view, int width = 60)
    {
        var console = new TestConsole();
        return view.Render(RenderOptions.Create(console, console.Profile.Capabilities), width).ToList();
    }

    // ── FileDiff ───────────────────────────────────────────────────────────

    [Fact]
    public void Of_AnEdit_HasItsHunksAndCounts_NothingChanged_IsNull()
    {
        var diff = FileDiff.Of("a.txt", Lines("one", "two", "three"), Lines("one", "2", "2.5", "three"));
        Assert.NotNull(diff);
        Assert.False(diff!.Created);
        Assert.Equal((2, 1), (diff.Added, diff.Removed));
        Assert.Equal([" one", "-two", "+2", "+2.5", " three"], Assert.Single(diff.Hunks).Lines);

        Assert.Null(FileDiff.Of("a.txt", Lines("same"), Lines("same")));
        Assert.Null(FileDiff.Of("a.txt", "one\r\ntwo\r\n", "one\ntwo\n"));   // a CRLF-only change shows nothing, the git tools' rule
    }

    [Fact]
    public void Of_ANewFile_IsCreated_EveryLineAdded()
    {
        var diff = FileDiff.Of("new.cs", null, Lines("a", "b"));
        Assert.True(diff!.Created);
        Assert.Equal((2, 0), (diff.Added, diff.Removed));
        Assert.Null(FileDiff.Of("empty.txt", null, ""));
    }

    [Fact]
    public void Appended_NumbersTheAddedLinesAfterTheOldOnes()
    {
        var diff = FileDiff.Appended("log.md", 7, "x\ny\n")!;
        var rows = DiffView.RowsOf(diff, null);
        Assert.Equal([8, 9], rows.Select(r => r!.Value.Number));
        Assert.All(rows, r => Assert.Equal('+', r!.Value.Sign));
        Assert.False(diff.Created);
        Assert.True(FileDiff.Appended("log.md", 0, "x")!.Created);
        Assert.Null(FileDiff.Appended("log.md", 3, ""));
    }

    // ── Where the write happens ────────────────────────────────────────────

    [Fact]
    public void EditText_CarriesTheDiff_OnlyWhenItWrote()
    {
        Put("notes.txt", Numbers(20));
        var edited = _files.EditText("notes.txt", "line 10\n", "line ten\nline 10.5\n");
        Assert.Equal(FileOutcome.Ok, edited.Outcome);
        Assert.Equal((2, 1), (edited.Diff!.Added, edited.Diff.Removed));
        Assert.Equal("notes.txt", edited.Diff.Path);

        var all = _files.EditText("notes.txt", "line 2", "LINE 2", replaceAll: true);   // line 2 and line 20
        Assert.Equal(FileOutcome.Ok, all.Outcome);
        Assert.NotNull(all.Diff);

        Assert.Null(_files.EditText("notes.txt", "not there at all", "x").Diff);
    }

    [Fact]
    public void WriteText_ANewFile_IsCreated_AnOverwrite_DiffsAgainstWhatWasThere()
    {
        var created = _files.WriteText("a.cs", Lines("class A", "{", "}"), overwrite: false);
        Assert.True(created.Diff!.Created);
        Assert.Equal(3, created.Diff.Added);

        var replaced = _files.WriteText("a.cs", Lines("class A", "{", "    int x;", "}"), overwrite: true);
        Assert.False(replaced.Diff!.Created);
        Assert.Equal((1, 0), (replaced.Diff.Added, replaced.Diff.Removed));

        Assert.Null(_files.WriteText("a.cs", "x", overwrite: false).Diff);   // refused: nothing written, nothing to show
    }

    [Fact]
    public void WriteText_OverABinaryFile_HasNoDiff_ButStillWrites()
    {
        string full = Path.Combine(_root, "blob.bin");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(full, [1, 0, 2, 0, 3]);
        var wrote = _files.WriteText("blob.bin", "text now\n", overwrite: true);
        Assert.Equal(FileOutcome.Ok, wrote.Outcome);
        Assert.Null(wrote.Diff);
        Assert.Equal("text now\n", File.ReadAllText(full));
    }

    [Fact]
    public void AppendText_NumbersContinueAfterTheOldLastLine()
    {
        Put("log.md", Lines("one", "two", "three"));
        var appended = _files.AppendText("log.md", "four\nfive\n");
        var rows = DiffView.RowsOf(appended.Diff!, null);
        Assert.Equal([4, 5], rows.Select(r => r!.Value.Number));

        // A file without a final newline gets one first; the numbers still follow on.
        Put("bare.md", "one\ntwo");
        var bare = _files.AppendText("bare.md", "three");
        Assert.Equal(3, Assert.Single(DiffView.RowsOf(bare.Diff!, null))!.Value.Number);
    }

    // ── The tools ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PatchFile_AnswersTheSentenceAndTheDiff_ARefusalTheSentenceAlone()
    {
        Put("x.txt", Lines("alpha", "beta"));
        var tool = new PatchFileTool(_files);
        object? done = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = "x.txt", ["old_text"] = "beta", ["new_text"] = "gamma" }));
        var changed = Assert.IsType<ToolDiffResult>(done);
        Assert.StartsWith("edited x.txt", changed.Text);
        Assert.Equal((1, 1), (changed.Diff.Added, changed.Diff.Removed));

        object? refused = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = "x.txt", ["old_text"] = "nope", ["new_text"] = "x" }));
        Assert.IsType<string>(refused);
    }

    [Fact]
    public async Task WriteFile_AnswersTheDiff_ItsDescribeStaysText()
    {
        var tool = new WriteFileTool(_files);
        object? done = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = "n.md", ["content"] = "hi\n" }));
        Assert.True(Assert.IsType<ToolDiffResult>(done).Diff.Created);
        Assert.StartsWith("replaced n.md", tool.Describe("n.md", "bye\n", WriteMode.Overwrite));
    }

    // ── The turn ───────────────────────────────────────────────────────────

    /// <summary>Answers a <see cref="ToolDiffResult"/>: the shape of <c>patch_file</c>, without a file.</summary>
    private sealed class DiffTool(FileDiff diff) : AIFunction
    {
        private static readonly JsonElement Schema = NeonSidekick.Llm.Tools.ToolSchema.Parse("""{"type":"object","properties":{}}""");

        public override string Name => "differ";

        public override string Description => "Changes a file.";

        public override JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => new(new ToolDiffResult("edited a.txt (line 2)", diff));
    }

    [Fact]
    public async Task Assistant_TheModelGetsTheSentence_TheEventCarriesTheDiff()
    {
        var diff = FileDiff.Of("a.txt", "a\nb\n", "a\nc\n")!;
        var client = new FakeChatClient();
        var history = new ConversationHistory("sys");
        var assistant = new Assistant(client, history, new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), [new DiffTool(diff)]);
        client.Enqueue(FakeChatClient.Call("call-1", "differ", new Dictionary<string, object?>()));
        client.EnqueueText("done");

        var events = new List<TurnEvent>();
        await foreach (var evt in assistant.RunTurnAsync("change it")) events.Add(evt);

        var result = Assert.Single(events.OfType<TurnEvent.ToolResult>());
        Assert.Equal("edited a.txt (line 2)", result.Text);
        Assert.Same(diff, result.Diff);
        var sent = Assert.Single(client.Requests[1][3].Contents.OfType<FunctionResultContent>());
        Assert.Equal("edited a.txt (line 2)", sent.Result);

        // The two-part form a side loop calls answers the sentence.
        var (text, _) = await Assistant.InvokeToolAsync([new DiffTool(diff)], new FunctionCallContent("c", "differ", null), CancellationToken.None);
        Assert.Equal("edited a.txt (line 2)", text);
    }

    // ── The wording ────────────────────────────────────────────────────────

    [Fact]
    public void Summary_InClaudeCodesWords()
    {
        Assert.Equal("Added 3 lines, removed 1 line", FileText.DiffSummary(new FileDiff("x", false, [], 3, 1)));
        Assert.Equal("Added 1 line", FileText.DiffSummary(new FileDiff("x", false, [], 1, 0)));
        Assert.Equal("Removed 2 lines", FileText.DiffSummary(new FileDiff("x", false, [], 0, 2)));
        Assert.Equal("Wrote 12 lines", FileText.DiffSummary(new FileDiff("x", true, [], 12, 0)));
        Assert.Equal("… 5 more lines", FileText.DiffMore(5));
        Assert.Equal("… 1 more line", FileText.DiffMore(1));
    }

    [Fact]
    public void Note_IsTheResultsFirstLine_ItsColonDropped()
    {
        Assert.Equal("edited x.cs (lines 3–4, now 9 lines, 20 words)", FileText.DiffNote("edited x.cs (lines 3–4, now 9 lines, 20 words):\n2: a\n3: b"));
        Assert.Equal("wrote n.md (3 bytes, 1 line, 1 word)", FileText.DiffNote("wrote n.md (3 bytes, 1 line, 1 word)"));
    }

    // ── DiffView ───────────────────────────────────────────────────────────

    [Fact]
    public void View_NumbersEachRow_TheOldNumberForARemovedLine()
    {
        var diff = FileDiff.Of("a.txt", Numbers(12), Numbers(12).Replace("line 9\n", "nine\nnine and a half\n"))!;
        string[] lines = Render(new DiffView(null, diff, 40));
        Assert.Equal(
        [
            "     └ Added 2 lines, removed 1 line",
            "      6   line 6",
            "      7   line 7",
            "      8   line 8",
            "      9 - line 9",
            "      9 + nine",
            "     10 + nine and a half",
            "     11   line 10",
            "     12   line 11",
            "     13   line 12",
        ], lines);
    }

    [Fact]
    public void View_HunksArePartedByAGap_AndTheCapEndsIt()
    {
        var diff = FileDiff.Of("a.txt", Numbers(30), Numbers(30, i => i is 2 or 28 ? "x" + i : null))!;
        Assert.Equal(2, diff.Hunks.Count);
        string[] all = Render(new DiffView(null, diff, 40));
        Assert.Contains("     " + " " + DiffView.HunkGap, all);   // under the two-digit gutter

        string[] cut = Render(new DiffView(null, diff, 3));
        Assert.Equal(5, cut.Length);   // the header, three rows, the tail
        Assert.Equal("     " + FileText.DiffMore(diff.Hunks.Sum(h => h.Lines.Count) - 3), cut[^1]);

        Assert.Equal(["     └ Added 2 lines, removed 2 lines"], Render(new DiffView(null, diff, 0)));   // 0: the header alone
    }

    [Fact]
    public void View_ALongLineWrapsUnderTheTextColumn()
    {
        var diff = FileDiff.Of("a.txt", "", new string('x', 50) + "\n")!;
        string[] lines = Render(new DiffView(null, diff, 40), width: 30);
        // "     " + "1 + " is nine cells: 21 of text on the first row, the rest under it.
        Assert.Equal("     1 + " + new string('x', 21), lines[1]);
        Assert.Equal("         " + new string('x', 21), lines[2]);
        Assert.Equal("         " + new string('x', 8), lines[3]);
    }

    [Fact]
    public void View_AddedAndRemovedRows_SitOnTheirSlabs_ToTheEdge()
    {
        var diff = FileDiff.Of("a.cs", "int a = 1;\n", "int a = 2;\n")!;
        var segments = Segments(new DiffView(null, diff, 40), width: 40);
        var added = Theme.DiffAdded.Background;
        var removed = Theme.DiffRemoved.Background;
        Assert.NotEqual(added, removed);
        Assert.Contains(segments, s => s.Text == "2" && s.Style.Background == added);       // the new literal, coloured by the lexer on the green
        Assert.Contains(segments, s => s.Text == "1" && s.Style.Background == removed);
        Assert.Equal(Theme.CodeStyle(NeonSidekick.UI.Markdown.CodeTokenKind.Number).Foreground, segments.First(s => s.Text == "2").Style.Foreground);

        // Each slab row is filled to the width.
        var rows = Segment.SplitLines(segments);
        Assert.All(rows.Skip(1), row => Assert.Equal(40, row.Sum(s => s.CellCount())));
    }

    [Fact]
    public void View_TheHeadLineLeads_AndAControlCharacterNeverReachesTheTerminal()
    {
        var diff = FileDiff.Of("a.txt", "a\n", "a\u001b[2Jb\tc\n")!;
        string[] lines = Render(new DiffView(new Text("  note"), diff, 40));
        Assert.Equal("  note", lines[0]);
        Assert.Contains("·[2Jb    c", lines[^1]);
    }

    [Fact]
    public void LanguageOf_TheExtension_NoneForAPatch()
    {
        Assert.Equal("csharp", DiffView.LanguageOf("src/A.cs")!.Name);
        Assert.Null(DiffView.LanguageOf("fix.patch"));
        Assert.Null(DiffView.LanguageOf("README"));
        Assert.NotNull(DiffView.LanguageOf("Directory.Build.props"));
    }

    // ── The tool run counts a write once ───────────────────────────────────

    [Fact]
    public void Scrollback_AWriteOfManyLines_IsOneMemberOfTheRun()
    {
        var store = new Scrollback();
        store.BeginGroup(2);
        store.SetGroupSummary([new Segment("  S")], [new Segment("  E")]);
        store.Append([new Segment("  note\n     └ Added 1 line\n     1 + x\n")], 40, member: true);
        store.Append([new Segment("  m2\n")], 40, member: true);
        store.EndGroup();

        // Two writes against a keep of 2: not folded, every line shows.
        Assert.Equal(["  note", "     └ Added 1 line", "     1 + x", "  m2"], store.Rows(40).Select(r => string.Concat(r.Select(s => s.Text))).ToArray());
    }
}
