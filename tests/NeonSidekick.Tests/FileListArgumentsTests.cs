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

    private static string Block(string name, string read) => FileText.BatchSeparator(name) + "\n" + read;

    [Fact]
    public async Task ReadFile_Paths_ReadsEachInOrder_UnderOneSummaryLine_PathAndPathsMergedAndDeduped()
    {
        Put("a.txt", "alpha\n");
        Put("b.txt", "beta\n");
        var read = Tool<ReadFileTool>();
        string a = read.Describe("a.txt", null, null), b = read.Describe("b.txt", null, null);
        string both = FileText.ReadMany(2, 2, 0, ["a.txt", "b.txt"]) + "\n" + Block("a.txt", a) + "\n\n" + Block("b.txt", b);

        Assert.Equal(both, await Invoke(read, ("paths", Json("""["a.txt", "b.txt"]"""))));
        Assert.Equal(both, await Invoke(read, ("path", "a.txt"), ("paths", Json("""["a.txt", " b.txt ", ""]"""))));
        Assert.Equal(both, await Invoke(read, ("paths", "[\"a.txt\", \"b.txt\"]")));   // the list written as text
        Assert.Equal(a, await Invoke(read, ("paths", Json("""["a.txt"]"""))));   // one file reads exactly as path does
        Assert.Equal(a, await Invoke(read, ("paths", Json("""["a.txt", "./a.txt"]"""))));   // #15: one file by where it resolves
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(a, await Invoke(read, ("paths", Json("""["a.txt", "A.TXT"]"""))));   // #16: case ignored where the disk ignores it
        }

        Assert.Equal("read 2 files: a.txt, b.txt", FileText.ReadMany(2, 2, 0, ["a.txt", "b.txt"]));
    }

    [Fact]
    public async Task ReadFile_Paths_ARefusedFile_IsCounted_AndAllRefused_IsAnError()
    {
        Put("a.txt", "alpha\n");
        var read = Tool<ReadFileTool>();

        string some = await Invoke(read, ("paths", Json("""["a.txt", "gone.txt"]""")));
        Assert.StartsWith("read 1 of 2 files (1 refused): a.txt\n", some, StringComparison.Ordinal);
        Assert.EndsWith(Block("gone.txt", read.Describe("gone.txt", null, null)), some, StringComparison.Ordinal);
        Assert.StartsWith("Error: ", read.Describe("gone.txt", null, null), StringComparison.Ordinal);

        string none = await Invoke(read, ("paths", Json("""["gone.txt", "nope.txt"]""")));
        Assert.StartsWith("Error: none of the 2 files could be read:\n", none, StringComparison.Ordinal);
        Assert.Equal(FileText.NoneRead(2) + "\n" + Block("gone.txt", read.Describe("gone.txt", null, null)) + "\n\n" + Block("nope.txt", read.Describe("nope.txt", null, null)), none);
    }

    [Fact]
    public async Task ReadFile_Paths_ShareOneLimit_TheFirstOverItIsCut_AndTheRestAreNamed()
    {
        Put("big.txt", Lines(250, 99));    // 25,000 characters
        Put("mid.txt", new string('y', 10_000) + "\ntail\n");    // over what big.txt leaves: read cut to it
        Put("late.txt", "never\n");    // the limit is spent by then
        var read = Tool<ReadFileTool>();

        string answer = await Invoke(read, ("paths", Json("""["big.txt", "mid.txt", "late.txt"]""")));

        Assert.StartsWith("read 2 of 3 files: big.txt, mid.txt\n", answer, StringComparison.Ordinal);
        Assert.Contains("mid.txt (lines 1–1 of 2; cut at ", answer, StringComparison.Ordinal);
        Assert.Contains("; next: read it alone with start_line 2):", answer, StringComparison.Ordinal);   // #122: a window goes with one file
        Assert.EndsWith("\n\n" + FileText.NotRead(["late.txt"], [], WorkingDirectory.MaxReadChars, FileTool.MaxPaths), answer, StringComparison.Ordinal);
        Assert.Equal("1 more not read (1 past the 32,000-character limit the files share); call again with paths: late.txt", FileText.NotRead(["late.txt"], [], 32_000, 20));
        Assert.InRange(answer.Length, 0, WorkingDirectory.MaxReadChars + 1_000);   // #14: the blocks are counted whole
        Assert.True(_files.WasSeen("big.txt"));
        Assert.False(_files.WasSeen("mid.txt"));   // a cut read is never seen: write_file still asks before replacing it
    }

    /// <summary>
    /// The tool-arguments review's #14, #83: once the limit is nearly spent, a file that fits whole is still read and a refusal still shows in
    /// place; only a file too large for what is left goes on the not-read list, which names it once for the next call.
    /// </summary>
    [Fact]
    public async Task ReadFile_Paths_PastTheFloor_SmallFilesAreStillRead_AndRefusalsShown()
    {
        Put("big.txt", Lines(310, 99));    // 31,000 characters: under 1,000 left after it
        Put("small.txt", "hi\n");
        Put("large.txt", Lines(50, 99));
        var read = Tool<ReadFileTool>();

        string answer = await Invoke(read, ("paths", Json("""["big.txt", "large.txt", "gone.txt", "small.txt"]""")));

        Assert.StartsWith("read 2 of 4 files (1 refused): big.txt, small.txt\n", answer, StringComparison.Ordinal);
        Assert.Contains(Block("gone.txt", read.Describe("gone.txt", null, null)), answer, StringComparison.Ordinal);
        Assert.Contains(Block("small.txt", read.Describe("small.txt", null, null)), answer, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + FileText.NotRead(["large.txt"], [], WorkingDirectory.MaxReadChars, FileTool.MaxPaths), answer, StringComparison.Ordinal);
        Assert.False(_files.WasSeen("large.txt"));
    }

    [Fact]
    public async Task ReadFile_Paths_PastTheCap_AreNamed_AWindowIsRefused_AndNothingIsRefused()
    {
        var names = Enumerable.Range(1, FileTool.MaxPaths + 2).Select(i => "f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt").ToList();
        names.ForEach(n => Put(n, n + "\n"));
        var read = Tool<ReadFileTool>();

        string answer = await Invoke(read, ("paths", names.ToArray()));
        Assert.StartsWith("read 20 of 22 files: f1.txt, f2.txt", answer, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + FileText.NotRead([], ["f21.txt", "f22.txt"], WorkingDirectory.MaxReadChars, FileTool.MaxPaths), answer, StringComparison.Ordinal);

        Assert.Equal(FileText.WindowNeedsOneFile, await Invoke(read, ("paths", Json("""["f1.txt", "f2.txt"]""")), ("start_line", 2)));
        Assert.Equal(FileText.WindowNeedsOneFile, await Invoke(read, ("paths", Json("""["f1.txt", "f2.txt"]""")), ("max_lines", 2)));
        // #151: the values the one-file read takes as its defaults are no window.
        Assert.StartsWith("read 2 files:", await Invoke(read, ("paths", Json("""["f1.txt", "f2.txt"]""")), ("start_line", 1), ("max_lines", 0)), StringComparison.Ordinal);
        Assert.Equal(FileText.PathsNamedNothing, await Invoke(read, ("paths", Json("[]"))));   // #143
        Assert.Equal(FileText.PathsNamedNothing, await Invoke(read, ("paths", Json("""["", " "]"""))));
        Assert.Equal(FileText.PathsRequired, await Invoke(read, ("path", " ")));
        Assert.Equal("Error: path (or paths, for several) is required: the file, relative to the working directory", FileText.PathsRequired);   // #127
        Assert.Contains("(up to " + FileTool.MaxPaths.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")", read.Description, StringComparison.Ordinal);
    }

    /// <summary>The tool-arguments review's #157: the first line names the files read, within the transcript note's width, the rest counted.</summary>
    [Fact]
    public async Task ReadFile_Paths_TheFirstLine_NamesTheFiles_WithinTheNotesWidth()
    {
        var names = Enumerable.Range(1, FileTool.MaxPaths).Select(i => new string('n', 30) + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt").ToList();
        names.ForEach(n => Put(n, "x\n"));

        string first = (await Invoke(Tool<ReadFileTool>(), ("paths", names.ToArray()))).Split('\n')[0];

        Assert.StartsWith("read 20 files: " + names[0] + ", " + names[1], first, StringComparison.Ordinal);
        Assert.EndsWith(" more", first, StringComparison.Ordinal);
        Assert.InRange(NeonSidekick.UI.TextCells.Width(first), 0, NeonSidekick.UI.TranscriptRenderer.ToolTextLimit);
    }

    /// <summary>The tool-arguments review's #51: a batch marks nothing while it runs; the tool applies the marks once its answer is in hand.</summary>
    [Fact]
    public async Task ReadFile_Paths_TheBatchItself_MarksNothing()
    {
        Put("a.txt", "alpha\n");
        Put("b.txt", "beta\n");
        var read = Tool<ReadFileTool>();

        var batch = read.ReadMany(["a.txt", "b.txt"], CancellationToken.None);
        Assert.False(_files.WasSeen("a.txt"));
        Assert.Equal(2, batch.Marks.Count);

        await Invoke(read, ("paths", Json("""["a.txt", "b.txt"]""")));
        Assert.True(_files.WasSeen("a.txt") && _files.WasSeen("b.txt"));
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
    public async Task FileInfo_Paths_OneLineEach_UnderASummary_ErrorsInline_AndNothingIsTheRoot()
    {
        Put("a.txt", "alpha\n");
        Put(Path.Combine("sub", "b.txt"), "beta\n");
        var info = Tool<FileInfoTool>();
        string a = info.Describe("a.txt"), sub = info.Describe("sub"), gone = info.Describe("gone.txt");

        Assert.Equal(FileText.InfoMany(3, 3, 1) + "\n" + a + "\n" + sub + "\n" + gone, await Invoke(info, ("paths", Json("""["a.txt", "sub", "gone.txt"]"""))));
        Assert.Equal("inspected 3 paths (1 with an error):", FileText.InfoMany(3, 3, 1));
        Assert.StartsWith("Error: ", gone, StringComparison.Ordinal);
        Assert.Equal(info.Describe(""), await Invoke(info));
        Assert.Equal(info.Describe(""), await Invoke(info, ("path", "")));
        Assert.Equal(a, await Invoke(info, ("paths", "a.txt")));   // a lone string is a one-item list
        Assert.Equal(a, await Invoke(info, ("paths", Json("""["a.txt", "./a.txt"]"""))));   // #15: once by where it resolves
        Assert.Equal(FileText.PathsNamedNothing, await Invoke(info, ("paths", Json("[]"))));   // #143: not the root
        Assert.Equal(a, await Invoke(info, ("path", "a.txt"), ("paths", Json("[]"))));
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

    /// <summary>
    /// Every edit already in the file is the single form's answer; one beside edits that still apply refuses the whole call (2026-11-15, the
    /// tool-arguments review's #5, #137: it was skipped, which broke all or none), nothing saved.
    /// </summary>
    [Fact]
    public async Task PatchFile_Edits_AllInTheFileAlready_IsAlreadyApplied_AndOneBesideOthersRefusesTheCall()
    {
        Put("x.txt", "the first line\ntwo\n");
        var patch = Tool<PatchFileTool>();

        Assert.Equal(FileText.AlreadyApplied("x.txt"), await Invoke(patch, ("path", "x.txt"), ("edits", Json("""[{"old_text": "line one", "new_text": "the first line"}]"""))));
        string answer = await Invoke(patch, ("path", "x.txt"), ("edits", Json("""[{"old_text": "two", "new_text": "2"}, {"old_text": "line one", "new_text": "the first line"}]""")));
        Assert.Equal(FileText.EditOfList(2, 2, FileText.NewTextOnly("x.txt"), shifted: false), answer);
        Assert.Equal("the first line\ntwo\n", Read("x.txt"));
    }

    /// <summary>Edits that leave the file as it was (#61, #134) save nothing and say so, not an error.</summary>
    [Fact]
    public async Task PatchFile_Edits_ThatUndoEachOther_SaveNothing()
    {
        Put("x.txt", "foo\n");
        byte[] before = File.ReadAllBytes(Path.Combine(_root, "x.txt"));
        DateTime written = File.GetLastWriteTimeUtc(Path.Combine(_root, "x.txt"));
        string answer = await Invoke(Tool<PatchFileTool>(), ("path", "x.txt"), ("edits", Json("""[{"old_text": "foo", "new_text": "bar"}, {"old_text": "bar", "new_text": "foo"}]""")));
        Assert.Equal(FileText.NoNetChange("x.txt"), answer);
        Assert.DoesNotContain("Error", answer, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_root, "x.txt")));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(_root, "x.txt")));
    }

    /// <summary>
    /// The answer (#129, #138, #154): a replace_all says how many places it changed, a removal where, a new text ending in a line break counts
    /// its lines alone, and edits whose regions don't fit under the cap are named while the others are shown.
    /// </summary>
    [Fact]
    public async Task PatchFile_Edits_TheAnswerNamesPlacesRemovals_AndTheEditsNotShown()
    {
        Put("x.txt", "a\nb\na\nc\nd\n");
        string answer = await Invoke(Tool<PatchFileTool>(), ("path", "x.txt"), ("edits", Json("""[{"old_text": "a", "new_text": "A", "replace_all": true}, {"old_text": "c\n", "new_text": ""}, {"old_text": "d", "new_text": "d1\nd2"}]""")));
        Assert.Equal("A\nb\nA\nd1\nd2\n", Read("x.txt"));
        Assert.Contains("(edit 1 replaced 2 places)", answer, StringComparison.Ordinal);
        Assert.Contains("(edit 2 removed at line 4)", answer, StringComparison.Ordinal);

        Put("y.txt", "one\ntwo\n");
        string grown = await Invoke(Tool<PatchFileTool>(), ("path", "y.txt"), ("edits", Json("""[{"old_text": "two\n", "new_text": "2a\n2b\n"}]""")));
        Assert.StartsWith("edited y.txt with 1 edit (lines 2–3;", grown, StringComparison.Ordinal);

        string lines = string.Concat(Enumerable.Range(1, 200).Select(i => "line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"));
        Put("z.txt", lines);
        // The new texts as JSON escapes (\n), so the list is one JSON text.
        string big = string.Concat(Enumerable.Range(1, 70).Select(i => "new " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\\n"));
        string list = "[{\"old_text\": \"line 10\\n\", \"new_text\": \"ten\\n\"}, {\"old_text\": \"line 100\\n\", \"new_text\": \"" + big + "\"}, {\"old_text\": \"line 150\\n\", \"new_text\": \"one fifty\\n\"}]";
        string capped = await Invoke(Tool<PatchFileTool>(), ("path", "z.txt"), ("edits", Json(list)));
        Assert.Contains(FileText.RegionsNotShown([2]), capped, StringComparison.Ordinal);
        Assert.Contains(": ten", capped, StringComparison.Ordinal);
        Assert.Contains(": one fifty", capped, StringComparison.Ordinal);
    }

    /// <summary>A key with no real value doesn't count toward a form (#22, #69, #84): an empty old_text or replace_all: false beside edits runs the list.</summary>
    [Fact]
    public async Task PatchFile_AnEmptySingleFormKey_BesideEdits_RunsTheList()
    {
        Put("x.txt", "one\n");
        string answer = await Invoke(Tool<PatchFileTool>(), ("path", "x.txt"), ("old_text", ""), ("replace_all", false), ("edits", Json("""[{"old_text": "one", "new_text": "1"}]""")));
        Assert.StartsWith("edited x.txt with 1 edit", answer, StringComparison.Ordinal);
        Assert.Equal(FileText.EditEmpty, await Invoke(Tool<PatchFileTool>(), ("path", "x.txt"), ("old_text", ""), ("new_text", "")));
    }

    /// <summary>
    /// An item's keys follow the call check's rules (#12): another agent's name is hinted, a commentary key or an empty value is left aside; and
    /// the item schema is built from the keys the reader takes, for both tools (#141, #148).
    /// </summary>
    [Fact]
    public async Task PatchFile_AnItemsKeys_AreJudgedAsACallsAre_AndTheSchemaHoldsThem()
    {
        Put("x.txt", "one\n");
        var patch = Tool<PatchFileTool>();
        Assert.Equal(
            "Error: edit 1 of 1: an edit takes old_text, new_text and replace_all, not 'old_string'; did you mean 'old_text'?",
            await Invoke(patch, ("path", "x.txt"), ("edits", Json("""[{"old_string": "one", "new_text": "1"}]"""))));
        Assert.StartsWith("edited x.txt", await Invoke(patch, ("path", "x.txt"), ("edits", Json("""[{"old_text": "one", "new_text": "1", "explanation": "a fix", "note": ""}]"""))), StringComparison.Ordinal);

        foreach (var schema in new[] { patch.JsonSchema, new UncPatchTool(new NeonSidekick.Unc.UncAccess(() => new NeonSidekick.Unc.UncCatalog([], []), TimeProvider.System), () => new NeonSidekick.Settings.AppSettingsData()).JsonSchema })
        {
            var edits = schema.GetProperty("properties").GetProperty("edits");
            Assert.Equal(["old_text", "new_text", "replace_all"], edits.GetProperty("items").GetProperty("properties").EnumerateObject().Select(p => p.Name));
            Assert.Contains("at most " + WorkingDirectory.MaxEdits, edits.GetProperty("description").GetString(), StringComparison.Ordinal);
        }
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
        Assert.Equal(FileText.EditItem(1, 1, FileText.PrunedStubSent("edit 1's new_text")), await Edits("[{\"old_text\": \"one\", \"new_text\": \"" + ConversationCompactor.PrunedArgumentStub(9000) + "\"}]"));
        Assert.Equal(FileText.EditItem(1, 1, FileText.PrunedStubSent("edit 1's old_text")), await Edits("[{\"old_text\": \"" + ConversationCompactor.PrunedArgumentStub(9000) + "\", \"new_text\": \"1\"}]"));
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
        // Found before any edit ran (#68): no "as earlier edits left them".
        Assert.Equal("Error: edit 2 of 2: " + FileText.EditEmpty["Error: ".Length..], FileText.Edited(blank));
        Assert.Equal(1, _files.EditTexts("x.txt", [new EditStep("one", "one", false)]).FailedEdit);

        // The size cap is per edit, and names it (#29).
        string huge = new('x', WorkingDirectory.MaxWriteChars + 1);
        var tooLong = _files.EditTexts("x.txt", [new EditStep("one", "1", false), new EditStep("n", huge, false)]);
        Assert.Equal((FileOutcome.TooLong, 2), (tooLong.Outcome, tooLong.FailedEdit));
        Assert.Equal("one\n", Read("x.txt"));
    }

    /// <summary>Zero steps or more than the cap are a caller's mistake (#101): the tools answer the model first, so this throws.</summary>
    [Fact]
    public void EditTexts_ZeroOrTooManySteps_Throw()
    {
        Put("x.txt", "one\n");
        Assert.Throws<ArgumentOutOfRangeException>(() => _files.EditTexts("x.txt", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => _files.EditTexts("x.txt", [.. Enumerable.Repeat(new EditStep("a", "b"), WorkingDirectory.MaxEdits + 1)]));
    }

    /// <summary>A one-character replace_all over a large file stays fast (#49): its line numbers come from one pass, its spans by binary search.</summary>
    [Fact]
    public void EditTexts_AOneCharacterReplaceAll_OverALargeFile_IsLinear()
    {
        Put("big.txt", string.Concat(Enumerable.Repeat("a b\n", 200_000)) + "end\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var all = _files.EditTexts("big.txt", [new EditStep("b", "c", ReplaceAll: true), new EditStep("end", "END", false)]);
        Assert.Equal(FileOutcome.Ok, all.Outcome);
        Assert.Equal(200_000, all.Edits[0].Places);
        Assert.Equal([(200_001, 200_001)], all.Edits[1].Ranges);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
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
