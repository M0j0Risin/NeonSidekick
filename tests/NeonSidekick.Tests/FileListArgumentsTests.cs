using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The file tools' lists (2026-11-15, feature ideas #31, #45 and #30): <c>read_file</c> and <c>file_info</c> with <c>paths</c> (one shared limit,
/// the files left over named, a window refused), and <c>patch_file</c>'s <c>edits</c> (in order, all or none, each form and item refusal).
/// </summary>
public sealed class FileListArgumentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly IReadOnlyList<AIFunction> _tools;
    private readonly AppSettingsData _settings = new();

    public FileListArgumentsTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        _tools = ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings);
    }

    public void Dispose() => GitAccessTests.DeleteTree(_dir);

    private T Tool<T>() where T : AIFunction => _tools.OfType<T>().Single();

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static async Task<string> Invoke(AIFunction tool, params (string Name, object? Value)[] values) =>
        ToolAnswers.Text(await tool.InvokeAsync(new AIFunctionArguments(values.ToDictionary(v => v.Name, v => v.Value)), CancellationToken.None));

    private void Put(string relative, string text)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    private static string Lines(int count, int width) => string.Concat(Enumerable.Range(0, count).Select(_ => new string('x', width) + "\n"));

    // ---- read_file paths ----

    [Fact]
    public async Task ReadFile_Paths_ReadsEachInOrder_UnderOneSummaryLine_PathAndPathsMergedAndDeduped()
    {
        Put("a.txt", "alpha\n");
        Put("b.txt", "beta\n");
        var read = Tool<ReadFileTool>();
        string a = read.Describe("a.txt", null, null), b = read.Describe("b.txt", null, null);

        Assert.Equal(FileText.ReadMany(2, 0) + "\n" + a + "\n\n" + b, await Invoke(read, ("paths", Json("""["a.txt", "b.txt"]"""))));
        Assert.Equal(FileText.ReadMany(2, 0) + "\n" + a + "\n\n" + b, await Invoke(read, ("path", "a.txt"), ("paths", Json("""["a.txt", " b.txt ", ""]"""))));
        Assert.Equal(FileText.ReadMany(2, 0) + "\n" + a + "\n\n" + b, await Invoke(read, ("paths", "[\"a.txt\", \"b.txt\"]")));   // the list written as text
        Assert.Equal(a, await Invoke(read, ("paths", Json("""["a.txt"]"""))));   // one file reads exactly as path does
        Assert.Equal("read 2 files:", FileText.ReadMany(2, 0));
    }

    [Fact]
    public async Task ReadFile_Paths_ARefusedFile_IsCounted_AndAllRefused_IsAnError()
    {
        Put("a.txt", "alpha\n");
        var read = Tool<ReadFileTool>();

        string some = await Invoke(read, ("paths", Json("""["a.txt", "gone.txt"]""")));
        Assert.StartsWith("read 2 files (1 refused):\n", some, StringComparison.Ordinal);
        Assert.EndsWith(read.Describe("gone.txt", null, null), some, StringComparison.Ordinal);
        Assert.StartsWith("Error: ", read.Describe("gone.txt", null, null), StringComparison.Ordinal);

        string none = await Invoke(read, ("paths", Json("""["gone.txt", "nope.txt"]""")));
        Assert.StartsWith("Error: none of the 2 files could be read:\n", none, StringComparison.Ordinal);
        Assert.Equal(FileText.NoneRead(2) + "\n" + read.Describe("gone.txt", null, null) + "\n\n" + read.Describe("nope.txt", null, null), none);
    }

    [Fact]
    public async Task ReadFile_Paths_ShareOneLimit_ALaterFileIsCut_AndTheRestAreNamed()
    {
        Put("big.txt", Lines(250, 99));    // 25,000 characters
        Put("mid.txt", new string('y', 10_000) + "\ntail\n");    // a 10,000-character first line: 7,001 of it fit
        Put("late.txt", "never\n");
        var read = Tool<ReadFileTool>();

        string answer = await Invoke(read, ("paths", Json("""["big.txt", "mid.txt", "late.txt"]""")));

        Assert.StartsWith("read 2 files:\n", answer, StringComparison.Ordinal);
        Assert.Contains("mid.txt (lines 1–1 of 2; cut at 7,001 characters; line 1 is 10,000 characters long, cut at 7,001; next: start_line 2):", answer, StringComparison.Ordinal);   // what big.txt left (its 24,999 characters as read), not 32,000
        Assert.EndsWith("\n\n" + FileText.NotRead(["late.txt"], WorkingDirectory.MaxReadChars), answer, StringComparison.Ordinal);
        Assert.Equal("1 more not read, past the 32,000-character limit the files share; call again with paths: late.txt", FileText.NotRead(["late.txt"], 32_000));
        Assert.True(_files.WasSeen("big.txt"));
        Assert.False(_files.WasSeen("mid.txt"));   // a cut read is never seen: write_file still asks before replacing it
    }

    [Fact]
    public async Task ReadFile_Paths_PastTheCap_AreNamed_AWindowIsRefused_AndNothingIsPathsRequired()
    {
        var names = Enumerable.Range(1, ReadFileTool.MaxPaths + 2).Select(i => "f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt").ToList();
        names.ForEach(n => Put(n, n + "\n"));
        var read = Tool<ReadFileTool>();

        string answer = await Invoke(read, ("paths", names.ToArray()));
        Assert.StartsWith("read 20 files:\n", answer, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + FileText.MorePaths(["f21.txt", "f22.txt"]), answer, StringComparison.Ordinal);

        Assert.Equal(FileText.WindowNeedsOneFile, await Invoke(read, ("paths", Json("""["f1.txt", "f2.txt"]""")), ("start_line", 2)));
        Assert.Equal(FileText.WindowNeedsOneFile, await Invoke(read, ("paths", Json("""["f1.txt", "f2.txt"]""")), ("max_lines", 2)));
        Assert.Equal(FileText.PathsRequired, await Invoke(read, ("paths", Json("[]"))));
        Assert.Equal(FileText.PathsRequired, await Invoke(read, ("path", " ")));
        Assert.Equal("Error: path (or paths, for several) is required: the file or folder, relative to the working directory", FileText.PathsRequired);
    }

    [Fact]
    public async Task ReadFile_ABadList_IsEchoedShort()
    {
        string answer = await Invoke(Tool<ReadFileTool>(), ("paths", Json("[" + string.Join(",", Enumerable.Repeat("1", 50_000)) + "]")));
        Assert.StartsWith("Error: '[1,1,1", answer, StringComparison.Ordinal);
        Assert.EndsWith("' is not a list of paths for 'paths'", answer, StringComparison.Ordinal);
        Assert.True(answer.Length < 200, answer.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- file_info paths ----

    [Fact]
    public async Task FileInfo_Paths_OneLineEach_ErrorsInline_AndNothingIsTheRoot()
    {
        Put("a.txt", "alpha\n");
        Put(Path.Combine("sub", "b.txt"), "beta\n");
        var info = Tool<FileInfoTool>();
        string a = info.Describe("a.txt"), sub = info.Describe("sub"), gone = info.Describe("gone.txt");

        Assert.Equal(a + "\n" + sub + "\n" + gone, await Invoke(info, ("paths", Json("""["a.txt", "sub", "gone.txt"]"""))));
        Assert.StartsWith("Error: ", gone, StringComparison.Ordinal);
        Assert.Equal(info.Describe(""), await Invoke(info));
        Assert.Equal(info.Describe(""), await Invoke(info, ("path", "")));
        Assert.Equal(a, await Invoke(info, ("paths", "a.txt")));   // a lone string is a one-item list
        Assert.Contains("paths inspects several at once", info.Description, StringComparison.Ordinal);
    }

    // ---- patch_file edits ----

    [Fact]
    public async Task PatchFile_Edits_ApplyInOrder_OneSave_AndTheAnswerNamesEveryEditsLines()
    {
        Put("x.txt", "one\ntwo\nthree\nfour\n");
        var patch = Tool<PatchFileTool>();

        string answer = await Invoke(patch, ("path", "x.txt"), ("edits", Json("""[{"old_text": "one", "new_text": "1"}, {"old_text": "three", "new_text": "3"}]""")));

        Assert.Equal("1\ntwo\n3\nfour\n", Read("x.txt"));
        Assert.StartsWith("edited x.txt with 2 edits (lines 1, 3; now 4 lines", answer, StringComparison.Ordinal);
        Assert.Contains("1: 1\n2: two\n3: 3", answer, StringComparison.Ordinal);
        Assert.True(_files.WasSeen("x.txt"));
        Assert.Contains("send edits instead", patch.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchFile_Edits_ALaterEditMatchesWhatAnEarlierLeft_AndALoneObjectOrTextIsAList()
    {
        Put("x.txt", "one\n");
        var patch = Tool<PatchFileTool>();

        await Invoke(patch, ("path", "x.txt"), ("edits", Json("""[{"old_text": "one", "new_text": "uno"}, {"old_text": "uno", "new_text": "eins"}]""")));
        Assert.Equal("eins\n", Read("x.txt"));

        await Invoke(patch, ("path", "x.txt"), ("edits", Json("""{"old_text": "eins", "new_text": "un"}""")));
        Assert.Equal("un\n", Read("x.txt"));

        await Invoke(patch, ("path", "x.txt"), ("edits", """[{"old_text": "un", "new_text": "ett"}]"""));
        Assert.Equal("ett\n", Read("x.txt"));
    }

    [Fact]
    public async Task PatchFile_Edits_AMiss_SavesNothing_AndNamesTheEdit()
    {
        Put("x.txt", "one\ntwo\n");
        byte[] before = File.ReadAllBytes(Path.Combine(_root, "x.txt"));
        var patch = Tool<PatchFileTool>();

        string answer = await Invoke(patch, ("path", "x.txt"), ("edits", Json("""[{"old_text": "one", "new_text": "1"}, {"old_text": "nope", "new_text": "x"}]""")));

        Assert.StartsWith("Error: edit 2 of 2 (lines as edit 1 left them): ", answer, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_root, "x.txt")));
        Assert.Equal("Error: edit 3 of 5 (lines as edits 1–2 left them): x", FileText.EditOfList(3, 5, "Error: x"));
        Assert.Equal("Error: edit 1 of 5: x", FileText.EditOfList(1, 5, "Error: x"));
    }

    [Fact]
    public async Task PatchFile_Edits_AllInTheFileAlready_IsAlreadyApplied_AndOneSkippedIsNoted()
    {
        Put("x.txt", "the first line\ntwo\n");
        var patch = Tool<PatchFileTool>();

        Assert.Equal(FileText.AlreadyApplied("x.txt"), await Invoke(patch, ("path", "x.txt"), ("edits", Json("""[{"old_text": "line one", "new_text": "the first line"}]"""))));
        string answer = await Invoke(patch, ("path", "x.txt"), ("edits", Json("""[{"old_text": "line one", "new_text": "the first line"}, {"old_text": "two", "new_text": "2"}]""")));
        Assert.Contains("(edit 1 was in the file already; skipped)", answer, StringComparison.Ordinal);
        Assert.Equal("the first line\n2\n", Read("x.txt"));
    }

    [Fact]
    public async Task PatchFile_Forms_AndItems_AreRefusedByName()
    {
        Put("x.txt", "one\n");
        var patch = Tool<PatchFileTool>();
        Task<string> Edits(string json) => Invoke(patch, ("path", "x.txt"), ("edits", Json(json)));

        Assert.Equal(FileText.EditFormsBoth, await Invoke(patch, ("path", "x.txt"), ("old_text", "one"), ("new_text", "1"), ("edits", Json("""[{"old_text": "one", "new_text": "1"}]"""))));
        Assert.Equal(FileText.EditFormRequired, await Invoke(patch, ("path", "x.txt")));
        Assert.Equal(FileText.NewTextRequired, await Invoke(patch, ("path", "x.txt"), ("old_text", "one")));   // the single form keeps its answers
        Assert.Equal(FileText.EditsEmpty, await Edits("[]"));
        Assert.Equal(FileText.TooManyEdits(51, 50), await Edits("[" + string.Join(",", Enumerable.Repeat("""{"old_text": "a", "new_text": "b"}""", 51)) + "]"));
        Assert.Equal("Error: edits holds 51 edits, more than the 50 one call takes; split them over several calls", FileText.TooManyEdits(51, 50));
        Assert.Equal("Error: edit 1 of 1: an edit takes old_text, new_text and replace_all, not 'old_txet'; did you mean 'old_text'?", await Edits("""[{"old_txet": "one", "new_text": "1"}]"""));
        Assert.Equal("Error: edit 2 of 2: an edit takes old_text, new_text and replace_all, not 'zzz'", await Edits("""[{"old_text": "one", "new_text": "1"}, {"zzz": 1, "new_text": "1"}]"""));
        Assert.Equal(FileText.EditItem(1, 1, FileText.NewTextRequired), await Edits("""[{"old_text": "one"}]"""));
        Assert.Equal(FileText.EditItem(1, 1, FileText.EditNotText), await Edits("""[{"old_text": "one", "new_text": 1}]"""));
        Assert.Equal(FileText.EditItem(1, 1, FileText.BadBoolean("replace_all", "maybe")), await Edits("""[{"old_text": "one", "new_text": "1", "replace_all": "maybe"}]"""));
        Assert.Equal(FileText.EditItem(1, 1, FileText.PrunedStubSent("edits[1].new_text")), await Edits("[{\"old_text\": \"one\", \"new_text\": \"" + ConversationCompactor.PrunedArgumentStub(9000) + "\"}]"));
        Assert.Equal(FileText.EditItem(1, 1, FileText.EditEmpty), await Edits("""[{"new_text": "1"}]"""));
        Assert.StartsWith("Error: '[1, 2]' is not a list of {old_text, new_text} edits for 'edits'", await Edits("[1, 2]"), StringComparison.Ordinal);
        Assert.Equal("one\n", Read("x.txt"));
    }

    // ---- WorkingDirectory.EditTexts ----

    [Fact]
    public void EditTexts_KeepsTheEncodingAndLineEndings_AndACancelLeavesTheFile()
    {
        string full = Path.Combine(_root, "crlf.txt");
        byte[] bom = [0xEF, 0xBB, 0xBF];
        File.WriteAllBytes(full, [.. bom, .. System.Text.Encoding.UTF8.GetBytes("one\r\ntwo\r\n")]);

        var result = _files.EditTexts("crlf.txt", [new EditStep("one", "1", false), new EditStep("two", "2\nmore", false)]);

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Count);
        Assert.Equal([.. bom, .. System.Text.Encoding.UTF8.GetBytes("1\r\n2\r\nmore\r\n")], File.ReadAllBytes(full));
        Assert.NotNull(result.Diff);

        byte[] before = File.ReadAllBytes(full);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _files.EditTexts("crlf.txt", [new EditStep("1", "x", false)], cancelled.Token));
        Assert.Equal(before, File.ReadAllBytes(full));
    }

    [Fact]
    public void EditTexts_RefusesBeforeReading_ABlankOrUnchangedEdit_AndTooMuchText()
    {
        Put("x.txt", "one\n");
        var blank = _files.EditTexts("x.txt", [new EditStep("one", "1", false), new EditStep(" ", "2", false)]);
        Assert.Equal((FileOutcome.Empty, 2), (blank.Outcome, blank.FailedEdit));
        Assert.Equal("Error: edit 2 of 2 (lines as edit 1 left them): " + FileText.EditEmpty["Error: ".Length..], FileText.Edited(blank));
        Assert.Equal(1, _files.EditTexts("x.txt", [new EditStep("one", "one", false)]).FailedEdit);
        string huge = new('x', WorkingDirectory.MaxWriteChars / 2 + 1);
        Assert.Equal(FileOutcome.TooLong, _files.EditTexts("x.txt", [new EditStep("one", huge, false), new EditStep("n", huge, false)]).Outcome);
        Assert.Equal("one\n", Read("x.txt"));
    }

    [Fact]
    public void Shift_MovesASpanPastEarlierReplacements_AndGrowsOneOverlapped()
    {
        // "abc" (0..3) became five characters: a span at 10..12 moves two on.
        Assert.Equal(new TextSpan(12, 14), WorkingDirectory.Shift(new TextSpan(10, 12), [new TextSpan(0, 3)], [new TextSpan(0, 5)]));
        // 4..8 became one character at 4..5: a span 2..6 that overlaps it ends where the replacement now ends.
        Assert.Equal(new TextSpan(2, 5), WorkingDirectory.Shift(new TextSpan(2, 6), [new TextSpan(4, 8)], [new TextSpan(4, 5)]));
        // A span before every replacement stays put.
        Assert.Equal(new TextSpan(0, 2), WorkingDirectory.Shift(new TextSpan(0, 2), [new TextSpan(4, 8)], [new TextSpan(4, 5)]));
    }

    [Fact]
    public async Task WriteFile_SentEdits_IsPointedAtPatchFile()
    {
        Assert.Equal(FileText.ContentRequired(PatchFileTool.ToolName, true), await Invoke(Tool<WriteFileTool>(), ("path", "x.txt"), ("edits", Json("""[{"old_text": "a", "new_text": "b"}]"""))));
    }
}
