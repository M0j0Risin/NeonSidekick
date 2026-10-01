using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The fourteen file tools (2026-09-19: 19 less <c>list_directory</c>, <c>recent_files</c>, <c>append_file</c>, <c>edit_file</c> and <c>edit_lines</c>, folded into <c>search_files</c>, <c>write_file</c> and <c>patch_file</c>; <c>restore</c> went with File safe edits on 2026-10-01) over a real temp folder: schemas pinned, every <c>Describe</c> exercised once, the argument shapes a model sends.</summary>
public sealed class FileToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly List<string> _opened = new();
    private readonly IReadOnlyList<AIFunction> _tools;
    private readonly AppSettingsData _settings = new();
    private bool _isDefault = true;

    public FileToolsTests()
    {
        _root = Path.Combine(_dir, "files");
        _files = new WorkingDirectory(() => _root, _time);
        _tools = ChatScreen.FileTools(_files, () => _isDefault, _opened.Add, () => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private T Tool<T>() where T : AIFunction => _tools.OfType<T>().Single();

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var (name, value) in values)
        {
            dictionary[name] = value;
        }

        return new AIFunctionArguments(dictionary);
    }

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static async Task<string> Invoke(AIFunction tool, params (string Name, object? Value)[] values) =>
        (string)(await tool.InvokeAsync(Args(values), CancellationToken.None))!;

    private void Put(string relative, string text)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public void FileTools_AreTheFourteen_InOrder_AllQuiet()
    {
        Assert.Equal(FileToolNames.All, _tools.Select(t => t.Name));
        Assert.Equal(14, _tools.Count);   // fifteen until 2026-10-01, when restore went with File safe edits
        Assert.All(_tools, t => Assert.Contains(t.Name, ChatScreen.QuietTools));
        Assert.All(_tools, t => Assert.Contains("working directory", t.Description));
        Assert.All(_tools, t => Assert.Equal("object", t.JsonSchema.GetProperty("type").GetString()));
    }

    [Theory]
    [InlineData(GetWorkingDirectoryTool.ToolName, "", "")]
    [InlineData(SearchFilesTool.ToolName, "text,path,files,regex,context,output,order,limit,depth", "")]
    [InlineData(FileInfoTool.ToolName, "path", "path")]
    [InlineData(ReadFileTool.ToolName, "path,start_line,max_lines", "path")]
    [InlineData(ViewImageTool.ToolName, "path,paths", "")]
    [InlineData(WriteFileTool.ToolName, "path,content,mode", "path,content")]
    [InlineData(PatchFileTool.ToolName, "path,old_text,new_text,replace_all", "path,old_text,new_text")]
    [InlineData(CreateDirectoryTool.ToolName, "path", "path")]
    [InlineData(MoveTool.ToolName, "from,to,overwrite", "from,to")]
    [InlineData(CopyTool.ToolName, "from,to,overwrite", "from,to")]
    [InlineData(DeleteTool.ToolName, "path", "path")]
    [InlineData(ZipTool.ToolName, "path,to,overwrite", "path")]
    [InlineData(UnzipTool.ToolName, "path,to,overwrite", "path")]
    [InlineData(OpenTool.ToolName, "path", "")]
    public void Schemas_ArePinned(string name, string properties, string required)
    {
        var tool = _tools.Single(t => t.Name == name);
        var schema = tool.JsonSchema;
        string[] expected = properties.Length == 0 ? [] : properties.Split(',');
        Assert.Equal(expected, schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.All(schema.GetProperty("properties").EnumerateObject(), p => Assert.False(string.IsNullOrWhiteSpace(p.Value.GetProperty("description").GetString())));
        if (required.Length == 0)
        {
            Assert.False(schema.TryGetProperty("required", out _));
        }
        else
        {
            Assert.Equal(required.Split(','), schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        }

        // The integer and boolean arguments are typed so a model sends them as such; the word arguments list their choices.
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            string type = property.Value.GetProperty("type").GetString()!;
            Assert.Equal(property.Name is "depth" or "limit" or "start_line" or "max_lines" or "context" ? "integer" : property.Name is "overwrite" or "regex" or "replace_all" ? "boolean" : property.Name is "paths" ? "array" : "string", type);
            if (property.Name is "mode" or "output" or "order")
            {
                string[] choices = property.Value.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
                Assert.Equal(property.Name switch { "mode" => ["create", "overwrite", "append"], "output" => ["content", "files"], _ => ["name", "modified"] }, choices);
            }
        }
    }

    [Fact]
    public async Task GetWorkingDirectory_CreatesTheRoot_AndNamesTheDefault()
    {
        Assert.False(Directory.Exists(_root));
        Assert.Equal(FileText.Describe(_root, isDefault: true), await Invoke(Tool<GetWorkingDirectoryTool>()));
        Assert.True(Directory.Exists(_root));
        _isDefault = false;
        Assert.Equal(FileText.Describe(_root, isDefault: false), await Invoke(Tool<GetWorkingDirectoryTool>()));

        Directory.Delete(_root);
        File.WriteAllText(_root, "in the way");
        Assert.StartsWith("Error: could not create '" + _root + "': ", await Invoke(Tool<GetWorkingDirectoryTool>()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchFiles_WithoutText_ListsTheFolder_NestedByDepth_CutByLimit()
    {
        Put(@"docs\a.txt", "aa");
        Put(@"docs\sub\b.txt", "b");
        var search = Tool<SearchFilesTool>();

        // No text, no files, no order (2026-09-19, list_directory folded in): the folder's own entries, folders first with sizes.
        Assert.Equal("the working directory (1 entry):\ndocs\\", await Invoke(search));
        Assert.Equal("docs\\ (2 entries):\nsub\\\na.txt  2 B", await Invoke(search, ("path", "docs")));
        Assert.Equal("docs\\ (2 entries):\nsub\\\na.txt  2 B", await Invoke(search, ("path", Json("\"docs\"")), ("text", " ")));
        Assert.Equal(FileText.Missing(@"nope\"), await Invoke(search, ("path", "nope")));
        Assert.Equal(FileText.OutsideRoot(".."), await Invoke(search, ("path", "..")));

        // depth: 1 or none is the flat listing; deeper nests the entries, files and all; the cap clamps.
        Assert.Equal("the working directory (1 entry):\ndocs\\", await Invoke(search, ("depth", Json("1"))));
        Assert.Equal("the working directory (4 entries, 3 levels):\ndocs\\\n  sub\\\n    b.txt  1 B\n  a.txt  2 B", await Invoke(search, ("depth", Json("3"))));
        Assert.Equal("the working directory (4 entries, 4 levels):\ndocs\\\n  sub\\\n    b.txt  1 B\n  a.txt  2 B", await Invoke(search, ("depth", "99")));
        Assert.Equal("the working directory (3 entries, 2 levels):\ndocs\\\n  sub\\\n  a.txt  2 B", await Invoke(search, ("depth", Json("2"))));
        Assert.Equal("docs\\ (3 entries, 2 levels):\nsub\\\n  b.txt  1 B\na.txt  2 B", await Invoke(search, ("path", "docs"), ("depth", Json("2"))));
        Assert.Equal(FileText.IsAFile(@"docs\a.txt"), await Invoke(search, ("path", @"docs\a.txt"), ("depth", Json("2"))));
        Assert.Equal(ClockText.BadInteger("depth", "deep"), await Invoke(search, ("depth", "deep")));

        // limit cuts every shape, the tail counting what is shown.
        Assert.Equal("docs\\ (1 entry):\nsub\\\n" + FileText.OnlyFirst(1, "entries"), await Invoke(search, ("path", "docs"), ("limit", Json("1"))));
        Assert.Equal("the working directory (2 entries, 3 levels):\ndocs\\\n  sub\\\n" + FileText.OnlyFirst(2, "entries"), await Invoke(search, ("depth", Json("3")), ("limit", Json("2"))));
        Assert.Equal(ClockText.BadInteger("limit", "lots"), await Invoke(search, ("limit", "lots")));
        Assert.Equal(FileText.BadChoice("output", "lines", SearchFilesTool.OutputChoices), await Invoke(search, ("output", "lines")));
        Assert.Equal(FileText.BadChoice("order", "newest", SearchFilesTool.OrderChoices), await Invoke(search, ("order", "newest")));
        Assert.Equal("Error: 'newest' is not one of name or modified for 'order'", FileText.BadChoice("order", "newest", SearchFilesTool.OrderChoices));
    }

    [Fact]
    public async Task SearchFiles_FindsNames_ListsTheRecent_AndSearchesInside()
    {
        Put("a.md", "needle here");
        Put(@"docs\b.md", "nothing");
        Put(@"docs\c.txt", "Needle again\nand needle");
        var search = Tool<SearchFilesTool>();

        // The name search (2026-09-18, find_files folded in): files with no text lists the names that match, every level; depth limits it.
        Assert.Equal("2 files match '*.md' under the working directory:\na.md\ndocs\\b.md", await Invoke(search, ("files", "*.md")));
        Assert.Equal("1 file matches '*.md' under docs\\:\ndocs\\b.md", await Invoke(search, ("files", "*.md"), ("path", "docs"), ("text", "")));
        Assert.Equal("1 file matches '*.md' under the working directory:\na.md", await Invoke(search, ("files", "*.md"), ("depth", Json("1"))));
        string cut = await Invoke(search, ("files", "*"), ("limit", Json("1")));
        Assert.StartsWith("1 file matches '*' under the working directory:\n", cut, StringComparison.Ordinal);
        Assert.EndsWith("\n" + FileText.OnlyFirst(1, "matches"), cut, StringComparison.Ordinal);
        Assert.Equal("1 file matches 'docs/*.txt' under the working directory:\ndocs\\c.txt", await Invoke(search, ("files", "docs/*.txt")));
        Assert.Equal("1 file matches '**/*.txt' under the working directory:\ndocs\\c.txt", await Invoke(search, ("files", "**/*.txt")));
        // Both blank is the listing now (EmptySearch went 2026-09-19).
        Assert.Equal("the working directory (2 entries):\ndocs\\\na.md  11 B", await Invoke(search, ("files", " "), ("text", " ")));

        // The recent list (recent_files folded in): order modified, files only, newest first.
        string recent = await Invoke(search, ("order", "modified"), ("limit", Json("2")));
        Assert.StartsWith("most recently changed under the working directory, newest first:\n", recent, StringComparison.Ordinal);
        Assert.Equal(3, recent.Split('\n').Length);
        Assert.Equal(4, (await Invoke(search, ("order", "MODIFIED"))).Split('\n').Length);
        Assert.Equal(2, (await Invoke(search, ("order", "modified"), ("depth", Json("1")))).Split('\n').Length);

        string hits = await Invoke(search, ("text", "needle"));
        Assert.StartsWith("3 matches for 'needle' in 2 files (searched 3 files under the working directory in ", hits, StringComparison.Ordinal);
        Assert.EndsWith(" s):\na.md:1: needle here\ndocs\\c.txt:1: Needle again\ndocs\\c.txt:2: and needle", hits);
        Assert.Contains("1 match for 'needle' in 1 file", await Invoke(search, ("text", "needle"), ("files", "*.md")));
        Assert.Contains("1 match for 'needle' in 1 file (searched 1 file under", await Invoke(search, ("text", "needle"), ("depth", Json("1"))));
        Assert.Contains("2 matches for 'n..dle'", await Invoke(search, ("text", "n..dle"), ("path", "docs"), ("regex", Json("true"))));
        Assert.Contains("0 matches for 'n..dle'", await Invoke(search, ("text", "n..dle"), ("path", "docs"), ("regex", "false")));
        Assert.Equal(FileText.BadBoolean("regex", "sure"), await Invoke(search, ("text", "x"), ("regex", "sure")));
        Assert.StartsWith("Error: the regular expression is invalid: ", await Invoke(search, ("text", "("), ("regex", Json("true"))), StringComparison.Ordinal);
        // Context lines (2026-09-17) in grep's shape, and a path glob for files.
        Assert.EndsWith(" s):\ndocs\\c.txt:1: Needle again\ndocs\\c.txt-2- and needle", await Invoke(search, ("text", "again"), ("context", Json("2"))));
        Assert.Contains("2 matches for 'needle' in 1 file", await Invoke(search, ("text", "needle"), ("files", "docs/**/*.txt")));
        Assert.Contains("0 matches for 'needle'", await Invoke(search, ("text", "needle"), ("files", "docs/**/*.md")));
        Assert.Equal(ClockText.BadInteger("context", "lots"), await Invoke(search, ("text", "x"), ("context", "lots")));
        // One file as the path (2026-09-18): searched alone, the header naming it.
        string one = await Invoke(search, ("text", "needle"), ("path", @"docs\c.txt"), ("files", "*.md"));
        Assert.StartsWith("2 matches for 'needle' in docs\\c.txt (", one, StringComparison.Ordinal);
        Assert.EndsWith(" s):\ndocs\\c.txt:1: Needle again\ndocs\\c.txt:2: and needle", one);
        // limit cuts the hits; output files counts per file (2026-09-19).
        Assert.EndsWith("\n" + FileText.OnlyFirst(2, "matches") + FileText.NarrowHint, await Invoke(search, ("text", "needle"), ("limit", Json("2"))));
        string files = await Invoke(search, ("text", "needle"), ("output", "files"));
        Assert.StartsWith("2 files hold 'needle' (searched 3 files under the working directory in ", files, StringComparison.Ordinal);
        Assert.EndsWith(" s):\na.md  1 match\ndocs\\c.txt  2 matches", files);
        Assert.EndsWith("\n" + FileText.OnlyFirst(1, "files") + FileText.NarrowHint, await Invoke(search, ("text", "needle"), ("output", "FILES"), ("limit", Json("1"))));
        Assert.Contains(" (no matches)", await Invoke(search, ("text", "zzz"), ("output", "files")));

        string info = await Invoke(Tool<FileInfoTool>(), ("path", @"docs\c.txt"));
        Assert.StartsWith("docs\\c.txt — 23 bytes, 2 lines, 4 words, LF, modified ", info, StringComparison.Ordinal);
        File.WriteAllBytes(Path.Combine(_root, "crlf.txt"), [0xEF, 0xBB, 0xBF, .. "a\r\nb\r\n"u8.ToArray()]);
        Assert.StartsWith("crlf.txt — 9 bytes, 2 lines, 2 words, CRLF, UTF-8 BOM, modified ", await Invoke(Tool<FileInfoTool>(), ("path", "crlf.txt")), StringComparison.Ordinal);
        Assert.StartsWith("docs\\ — 2 files in 0 folders, 30 B, last modified ", await Invoke(Tool<FileInfoTool>(), ("path", "docs")), StringComparison.Ordinal);
        Assert.Equal(FileText.Missing("zzz"), await Invoke(Tool<FileInfoTool>(), ("path", "zzz")));
    }

    [Fact]
    public async Task ReadWritePatch()
    {
        var write = Tool<WriteFileTool>();
        // Every write-side sentence carries the file's line and word counts (2026-09-18), so no file_info follows it.
        Assert.Equal("wrote notes.txt (11 bytes, 3 lines, 3 words)", await Invoke(write, ("path", "notes.txt"), ("content", "one\ntwo\nthr")));
        Assert.Equal(FileText.PathRequired("path"), await Invoke(write, ("content", "no path")));
        Assert.Equal(FileText.PathRequired("path"), await Invoke(write, ("path", " "), ("content", "no path")));
        Assert.Equal("Error: path is required: the file or folder, relative to the working directory", FileText.PathRequired("path"));
        // mode (2026-09-19, append_file folded in): create refuses a file that is there and names the other two modes.
        Assert.Equal(FileText.WriteExists("notes.txt"), await Invoke(write, ("path", "notes.txt"), ("content", "x")));
        Assert.Equal(FileText.WriteExists("notes.txt"), await Invoke(write, ("path", "notes.txt"), ("content", "x"), ("mode", "create")));
        Assert.Equal("Error: 'notes.txt' already exists; call again with mode overwrite to replace it, or mode append to add to its end", FileText.WriteExists("notes.txt"));
        Assert.Equal(FileText.BadChoice("mode", "upsert", WriteFileTool.ModeChoices), await Invoke(write, ("path", "notes.txt"), ("content", "x"), ("mode", "upsert")));
        // An overwrite replaces the file in place, nothing kept (the .trash copy went with File safe edits, 2026-10-01).
        Assert.Equal("replaced notes.txt (13 bytes, 3 lines, 3 words)", await Invoke(write, ("path", "notes.txt"), ("content", "one\ntwo\nthree"), ("mode", "overwrite")));
        Assert.Equal("replaced notes.txt (13 bytes, 3 lines, 3 words)", await Invoke(write, ("path", "notes.txt"), ("content", "one\ntwo\nthree"), ("mode", Json("\"OVERWRITE\""))));
        Assert.False(Directory.Exists(Path.Combine(_root, ".trash")));

        // A read is the bare text (never numbered since 2026-09-19); a leftover numbered argument is ignored like any unknown one.
        var read = Tool<ReadFileTool>();
        Assert.Equal("notes.txt (3 lines):\none\ntwo\nthree", await Invoke(read, ("path", "notes.txt")));
        Assert.Equal("notes.txt (lines 2–2 of 3; next: start_line 3):\ntwo", await Invoke(read, ("path", "notes.txt"), ("start_line", Json("2")), ("max_lines", Json("1"))));
        Assert.Equal("notes.txt (lines 3–3 of 3):\nthree", await Invoke(read, ("path", "notes.txt"), ("start_line", "-1")));
        Assert.Equal("notes.txt (3 lines):\none\ntwo\nthree", await Invoke(read, ("path", "notes.txt"), ("numbered", Json("true"))));
        Assert.Equal(ClockText.BadInteger("start_line", "top"), await Invoke(read, ("path", "notes.txt"), ("start_line", "top")));
        Assert.Equal(ClockText.BadInteger("max_lines", "all"), await Invoke(read, ("path", "notes.txt"), ("max_lines", "all")));
        Assert.Equal(FileText.Missing("gone.txt"), await Invoke(read, ("path", "gone.txt")));

        Assert.Equal("appended 5 bytes to notes.txt (now 4 lines, 4 words)", await Invoke(write, ("path", "notes.txt"), ("content", "four"), ("mode", "append")));   // the newline before it counts
        Assert.Equal("created log.txt (2 bytes, 1 line, 1 word)", await Invoke(write, ("path", "log.txt"), ("content", "hi"), ("mode", "append")));
        Assert.Equal("one\ntwo\nthree\nfour", File.ReadAllText(Path.Combine(_root, "notes.txt")));

        // An edit's result: the new line, then the region around it numbered (two lines each side).
        var patch = Tool<PatchFileTool>();
        Assert.Equal("edited notes.txt (line 2; now 4 lines, 4 words):\n1: one\n2: 2\n3: three\n4: four", await Invoke(patch, ("path", "notes.txt"), ("old_text", "two"), ("new_text", "2")));
        Assert.Equal("one\n2\nthree\nfour", File.ReadAllText(Path.Combine(_root, "notes.txt")));
        Assert.Equal(FileText.EditNotFound("notes.txt"), await Invoke(patch, ("path", "notes.txt"), ("old_text", "nine hundred"), ("new_text", "9")));
        Assert.Equal(FileText.EditEmpty, await Invoke(patch, ("path", "notes.txt"), ("new_text", "9")));
        Assert.Equal(FileText.EditEmpty, await Invoke(patch, ("path", "notes.txt"), ("old_text", " \n "), ("new_text", "9")));
        Assert.Equal(FileText.EditSame, await Invoke(patch, ("path", "notes.txt"), ("old_text", "three"), ("new_text", "three")));
        Assert.Equal(FileText.PathRequired("path"), await Invoke(patch, ("old_text", "three"), ("new_text", "3")));
        // An edit already in the file (2026-09-19): done, nothing written, no error.
        Assert.Equal(FileText.AlreadyApplied("notes.txt"), await Invoke(patch, ("path", "notes.txt"), ("old_text", "one\nnever here\nthree"), ("new_text", "one\n2\nthree")));
        Assert.Equal("one\n2\nthree\nfour", File.ReadAllText(Path.Combine(_root, "notes.txt")));

        // old_text nowhere as written is found by the chain (2026-09-19): here with each line trimmed, the new lines taking the file's indentation.
        Put("style.css", ".a {\n    color: red;\n    margin: 0;\n}\n.b {\n  color: red;\n}\n");
        Assert.Equal(
            "edited style.css (lines 2–3; now 7 lines, 12 words)" + FileText.LineTrimmedNote + ":\n1: .a {\n2:     color: blue;\n3:     margin: 1px;\n4: }\n5: .b {",
            await Invoke(patch, ("path", "style.css"), ("old_text", "  color: red;\n  margin: 0;"), ("new_text", "  color: blue;\n  margin: 1px;")));
        Assert.Equal(".a {\n    color: blue;\n    margin: 1px;\n}\n.b {\n  color: red;\n}\n", File.ReadAllText(Path.Combine(_root, "style.css")));
        // Several loose matches without replace_all: the refusal names them.
        Assert.Equal(FileText.EditAmbiguous("style.css", 2, [new MatchLocation(4, "}"), new MatchLocation(7, "}")]), await Invoke(patch, ("path", "style.css"), ("old_text", "      }"), ("new_text", "}}")));
        Assert.Equal("Error: old_text appears 2 times in 'style.css'; include enough surrounding text to make it unique, or pass replace_all true:\n  line 4: }\n  line 7: }", FileText.EditAmbiguous("style.css", 2, [new MatchLocation(4, "}"), new MatchLocation(7, "}")]));
        Assert.Equal(FileText.EditNotFound("style.css"), await Invoke(patch, ("path", "style.css"), ("old_text", "  padding: 9em 9em;"), ("new_text", "x")));
        // replace_all takes a deterministic strategy's matches too (Hermes' rule), each landing with its own line's indentation.
        Put("dup.txt", "a a");
        Assert.Equal(FileText.EditAmbiguous("dup.txt", 2, [new MatchLocation(1, "a a"), new MatchLocation(1, "a a")]), await Invoke(patch, ("path", "dup.txt"), ("old_text", "a"), ("new_text", "b")));
        Assert.Equal(FileText.BadBoolean("replace_all", "all"), await Invoke(patch, ("path", "dup.txt"), ("old_text", "a"), ("new_text", "b"), ("replace_all", "all")));
        Assert.Equal("replaced 2 occurrences of old_text in dup.txt (lines 1, 1; now 1 line, 2 words)", await Invoke(patch, ("path", "dup.txt"), ("old_text", "a"), ("new_text", "b"), ("replace_all", Json("true"))));
        Assert.Equal("b b", File.ReadAllText(Path.Combine(_root, "dup.txt")));
        Assert.Equal("edited dup.txt (line 1; now 1 line, 2 words):\n1: c b", await Invoke(patch, ("path", "dup.txt"), ("old_text", "b b"), ("new_text", "c b"), ("replace_all", Json("true"))));   // one occurrence: the plain shape
        Put("indent.txt", "  a\n a\n");
        Assert.Equal("replaced 2 occurrences of old_text in indent.txt (lines 1, 2; now 2 lines, 2 words)" + FileText.LineTrimmedNote, await Invoke(patch, ("path", "indent.txt"), ("old_text", "    a"), ("new_text", "b"), ("replace_all", Json("true"))));
        Assert.Equal("  b\n b\n", File.ReadAllText(Path.Combine(_root, "indent.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".trash")));
    }

    [Fact]
    public async Task CreateMoveCopyDelete()
    {
        Assert.Equal("created docs\\notes\\", await Invoke(Tool<CreateDirectoryTool>(), ("path", @"docs\notes")));
        Assert.Equal("docs\\ already exists", await Invoke(Tool<CreateDirectoryTool>(), ("path", "docs")));
        Put("a.txt", "a");

        var move = Tool<MoveTool>();
        Assert.Equal("renamed a.txt to b.txt", await Invoke(move, ("from", "a.txt"), ("to", "b.txt")));
        Assert.Equal("moved b.txt to docs\\b.txt", await Invoke(move, ("from", "b.txt"), ("to", @"docs\b.txt")));
        Assert.Equal("renamed docs\\ to papers\\", await Invoke(move, ("from", "docs"), ("to", "papers")));
        Put("c.txt", "c");
        Assert.Equal(FileText.Exists(@"papers\b.txt"), await Invoke(move, ("from", "c.txt"), ("to", @"papers\b.txt")));
        // With overwrite a file in the way is replaced in place, nothing kept, and a folder in the way is refused (2026-09-20).
        Assert.Equal("moved c.txt to papers\\b.txt", await Invoke(move, ("from", "c.txt"), ("to", @"papers\b.txt"), ("overwrite", Json("true"))));
        Assert.Equal("c", File.ReadAllText(Path.Combine(_root, "papers", "b.txt")));
        Put("e.txt", "e");
        Assert.Equal(FileText.FolderInTheWay(@"papers\"), await Invoke(move, ("from", "e.txt"), ("to", "papers"), ("overwrite", Json("true"))));
        Assert.Equal("Error: 'papers\\' is a folder in the way — move it aside first", FileText.FolderInTheWay(@"papers\"));
        Assert.Equal("moved e.txt to papers\\b.txt", await Invoke(move, ("from", "e.txt"), ("to", @"papers\b.txt"), ("overwrite", Json("true"))));
        Assert.False(Directory.Exists(Path.Combine(_root, ".trash")));
        Assert.Equal(FileText.BadBoolean("overwrite", "y"), await Invoke(move, ("from", "x"), ("to", "y"), ("overwrite", "y")));
        Assert.Equal(FileText.Missing("x"), await Invoke(move, ("from", "x"), ("to", "y")));
        Assert.Equal(FileText.PathRequired("from"), await Invoke(move, ("to", "y")));
        Assert.Equal(FileText.PathRequired("to"), await Invoke(move, ("from", "x")));
        Assert.Equal(FileText.PathRequired("path"), await Invoke(Tool<CreateDirectoryTool>()));
        Assert.Equal(FileText.PathRequired("path"), await Invoke(Tool<UnzipTool>()));
        Assert.Equal(FileText.PathRequired("path"), await Invoke(Tool<WriteFileTool>(), ("content", "x"), ("mode", "append")));
        Assert.Equal(FileText.PathRequired("path"), await Invoke(Tool<ReadFileTool>()));

        var copy = Tool<CopyTool>();
        Assert.Equal("copied papers\\b.txt to d.txt", await Invoke(copy, ("from", @"papers\b.txt"), ("to", "d.txt")));
        Assert.Equal("copied papers\\ to backup\\", await Invoke(copy, ("from", "papers"), ("to", "backup")));
        Assert.Equal(FileText.Exists("d.txt"), await Invoke(copy, ("from", @"papers\b.txt"), ("to", "d.txt")));
        Assert.Equal(FileText.PathRequired("from"), await Invoke(copy, ("to", "d.txt")));
        Assert.Equal(FileText.IntoItself(@"papers\", @"papers\inner\"), await Invoke(copy, ("from", "papers"), ("to", @"papers\inner")));

        // delete removes for good — a folder with everything in it (in place since 2026-09-20 with File safe edits off, always since 2026-10-01).
        var delete = Tool<DeleteTool>();
        Assert.Equal("deleted d.txt", await Invoke(delete, ("path", "d.txt")));   // no "(File safe edits is off: nothing was kept)" since 2026-09-21
        Assert.False(File.Exists(Path.Combine(_root, "d.txt")));
        Assert.Equal(FileText.Missing("d.txt"), await Invoke(delete, ("path", "d.txt")));
        Assert.Equal(FileText.RootItself, await Invoke(delete));
        Assert.Equal("deleted the folder backup\\ and everything in it", await Invoke(delete, ("path", "backup")));
        Assert.False(Directory.Exists(Path.Combine(_root, "backup")));
        // A .trash left from before 2026-10-01 is a folder like any other: deleted like one.
        Put(@".trash\20260911-140530\old.txt", "old");
        Assert.Equal(@"deleted .trash\20260911-140530\old.txt", await Invoke(delete, ("path", @".trash\20260911-140530\old.txt")));
        Assert.Equal("deleted the folder .trash\\ and everything in it", await Invoke(delete, ("path", ".trash")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".trash")));
    }

    [Fact]
    public void Descriptions_NameNoTrashNoRestoreNoSetting()
    {
        // 2026-09-21 (the user's ask): no setting, nor that nothing brings a file back — the model confused itself over a restore it could not reach;
        // since 2026-10-01 (File safe edits gone) the one form, which no description of any file tool contradicts.
        var write = Tool<WriteFileTool>();
        var move = Tool<MoveTool>();
        var copy = Tool<CopyTool>();
        Assert.Equal(
            "Deletes a file or folder under the working directory (the user's cwd / current directory) for good; a folder goes with everything in it." +
            " A .git folder, anything in it, or a folder holding one is never deleted.",   // the .git note since 2026-09-23 (the user's call)
            Tool<DeleteTool>().Description);
        Assert.EndsWith(DeleteTool.GitNote, DeleteTool.DescriptionText, StringComparison.Ordinal);
        Assert.Equal(
            "Writes a text file under the working directory (the user's cwd / current directory), creating any missing folders. " +
            "mode create (the default) leaves a file that already exists alone; mode overwrite replaces it; mode append adds the content at its end on a new line, creating the file if missing — for journals, logs and lists. " +
            "The result reports the file's size, line count and word count, so nothing else is needed to check them. To change part of a file use patch_file.",
            write.Description);
        Assert.Equal(
            "Renames or moves a file or a folder under the working directory (the user's cwd / current directory): to is the new path " +
            "(a new name in the same place is a rename; a folder moves with everything in it). Refuses to replace something already at the new path unless overwrite is true; " +
            "a file is replaced in place and a folder in the way is refused.",
            move.Description);
        Assert.Equal(
            "Copies a file or a folder (with everything in it) under the working directory (the user's cwd / current directory) to a new path. " +
            "Refuses to replace something already at the new path unless overwrite is true; a file is replaced in place and a folder in the way is refused (a folder copied over a folder merges into it).",
            copy.Description);
        foreach (var tool in _tools)
        {
            Assert.DoesNotContain("trash", tool.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("restore", tool.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("safe edits", tool.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("brings it back", tool.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ZipUnzipOpen()
    {
        Put(@"docs\a.txt", "alpha");
        Put(@"docs\b.txt", "beta");

        Assert.StartsWith("zipped docs\\ into docs.zip (2 entries, ", await Invoke(Tool<ZipTool>(), ("path", "docs")), StringComparison.Ordinal);
        Assert.Equal(FileText.Exists("docs.zip"), await Invoke(Tool<ZipTool>(), ("path", "docs")));
        Assert.StartsWith("zipped docs\\ into out\\d.zip (2 entries, ", await Invoke(Tool<ZipTool>(), ("path", "docs"), ("to", @"out\d.zip"), ("overwrite", Json("true"))), StringComparison.Ordinal);
        Assert.Equal(FileText.BadBoolean("overwrite", "1"), await Invoke(Tool<ZipTool>(), ("path", "docs"), ("overwrite", Json("1"))));
        Assert.Equal(FileText.Missing("nope"), await Invoke(Tool<ZipTool>(), ("path", "nope")));

        Assert.Equal("unzipped docs.zip into restore\\ (2 entries)", await Invoke(Tool<UnzipTool>(), ("path", "docs.zip"), ("to", "restore")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_root, "restore", "docs", "b.txt")));
        Assert.Equal(FileText.Exists(@"restore\docs\a.txt"), await Invoke(Tool<UnzipTool>(), ("path", "docs.zip"), ("to", "restore")));
        Assert.Equal("unzipped docs.zip into restore\\ (2 entries)", await Invoke(Tool<UnzipTool>(), ("path", "docs.zip"), ("to", "restore"), ("overwrite", Json("true"))));
        Assert.Equal(FileText.NotAnArchive(@"docs\a.txt"), await Invoke(Tool<UnzipTool>(), ("path", @"docs\a.txt")));
        Assert.Equal(FileText.BadBoolean("overwrite", "no way"), await Invoke(Tool<UnzipTool>(), ("path", "docs.zip"), ("overwrite", "no way")));

        Assert.Equal("opened docs\\a.txt in the user's editor", await Invoke(Tool<OpenTool>(), ("path", @"docs\a.txt")));
        Assert.Equal("opened docs\\ in Explorer", await Invoke(Tool<OpenTool>(), ("path", "docs")));
        Assert.Equal("opened the working directory in Explorer", await Invoke(Tool<OpenTool>()));
        Assert.Equal(FileText.Missing("nope"), await Invoke(Tool<OpenTool>(), ("path", "nope")));
        Assert.Equal(new[] { Path.Combine(_root, "docs", "a.txt"), Path.Combine(_root, "docs"), _root }, _opened);
    }

    [Fact]
    public async Task Search_HonoursTheTurnsToken()
    {
        Put("a.txt", "needle");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tool<SearchFilesTool>().InvokeAsync(Args(("text", "needle")), cts.Token).AsTask());
    }

    [Fact]
    public void Constructors_RefuseNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SearchFilesTool(null!));
        Assert.Throws<ArgumentNullException>(() => new GetWorkingDirectoryTool(_files, null!));
        Assert.Throws<ArgumentNullException>(() => new MoveTool(null!));
        Assert.Throws<ArgumentNullException>(() => new CopyTool(null!));
        Assert.Throws<ArgumentNullException>(() => new OpenTool(_files, null!));
        Assert.Throws<ArgumentNullException>(() => new WorkingDirectory(null!, _time));
        Assert.Throws<ArgumentNullException>(() => new WorkingDirectory(() => _root, null!));
    }

    [Fact]
    public async Task ViewImage_ReturnsAToolImageResult_OrTheErrorSentence()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "square.bmp"), SmokeChecks.SolidBmp(4, 4));
        Put("notes.txt", "not a picture");
        var view = Tool<ViewImageTool>();

        object? value = await view.InvokeAsync(Args(("path", "square.bmp")), CancellationToken.None);
        var picture = Assert.IsType<ToolImageResult>(value);
        Assert.StartsWith("square.bmp (4×4 image/png, ", picture.Text, StringComparison.Ordinal);
        Assert.EndsWith("): " + FileText.ImageFollows, picture.Text, StringComparison.Ordinal);
        var image = Assert.Single(picture.Images);
        Assert.Equal("square.bmp", image.Path);
        Assert.Equal(ImageFile.Png, image.MediaType);
        Assert.Equal((4, 4), (image.Width, image.Height));
        Assert.Equal(picture.Text, view.Describe(["square.bmp"]));

        // Every failure is a plain sentence, so the loop's string path takes it as it does any tool's.
        Assert.Equal(FileText.NotAnImage("notes.txt"), await Invoke(view, ("path", "notes.txt")));
        Assert.Equal(FileText.Missing("gone.png"), await Invoke(view, ("path", "gone.png")));
        Assert.Equal(FileText.OutsideRoot("../up.png"), await Invoke(view, ("path", "../up.png")));
        Assert.Equal(FileText.NotText("square.bmp"), await Invoke(Tool<ReadFileTool>(), ("path", "square.bmp")));
        Assert.EndsWith(FileText.ViewImageHint, FileText.NotText("square.bmp"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewImage_Paths_FetchesABatch_OneLinePerPath_FailuresInline()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "a.bmp"), SmokeChecks.SolidBmp(4, 4));
        File.WriteAllBytes(Path.Combine(_root, "b.bmp"), SmokeChecks.SolidBmp(2, 2));
        var view = Tool<ViewImageTool>();

        object? value = await view.InvokeAsync(Args(("paths", Json("[\"a.bmp\", \"gone.png\", \"b.bmp\"]"))), CancellationToken.None);
        var batch = Assert.IsType<ToolImageResult>(value);
        string[] lines = batch.Text.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("a.bmp (4×4 image/png, ", lines[0], StringComparison.Ordinal);
        Assert.Equal(FileText.Missing("gone.png"), lines[1]);
        Assert.StartsWith("b.bmp (2×2 image/png, ", lines[2], StringComparison.Ordinal);
        Assert.Equal(["a.bmp", "b.bmp"], batch.Images.Select(i => i.Path));
        Assert.Equal(batch.Text, view.Describe(["a.bmp", "gone.png", "b.bmp"]));

        // path and paths together: path first; a CLR list from a direct caller reads the same.
        var both = Assert.IsType<ToolImageResult>(await view.InvokeAsync(Args(("path", "b.bmp"), ("paths", new[] { "a.bmp" })), CancellationToken.None));
        Assert.Equal(["b.bmp", "a.bmp"], both.Images.Select(i => i.Path));

        // Every path failing is a plain string, like any tool's error.
        Assert.Equal(FileText.Missing("x.png") + "\n" + FileText.Missing("y.png"), await Invoke(view, ("paths", Json("[\"x.png\", \"y.png\"]"))));
        Assert.Equal(ViewImageTool.NoPathError, await Invoke(view));
        Assert.Equal(ViewImageTool.NoPathError, await Invoke(view, ("path", ""), ("paths", Json("[]"))));
        // Under the default cap (View image max per call, 10 since 2026-09-19; a constant 4 until then) six paths all load.
        Assert.Equal(10, view.Limit);
        string six = await Invoke(view, ("paths", Json("[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\"]")));
        Assert.Equal(string.Join("\n", new[] { "1", "2", "3", "4", "5", "6" }.Select(FileText.Missing)), six);

        // Over the cap (2026-09-18): the first that many load, the rest are named for the next call. The cap is read at the call.
        _settings.FileViewImageMaxPerCall = 4;
        Assert.Equal(4, view.Limit);
        string over = await Invoke(view, ("paths", Json("[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\"]")));
        Assert.Equal(FileText.Missing("1") + "\n" + FileText.Missing("2") + "\n" + FileText.Missing("3") + "\n" + FileText.Missing("4") + "\n" + FileText.MorePictures(["5", "6"]), over);
        Assert.Equal("2 more not shown; call again with paths: 5, 6", FileText.MorePictures(["5", "6"]));
        var five = Assert.IsType<ToolImageResult>(await view.InvokeAsync(Args(("paths", Json("[\"gone.png\", \"a.bmp\", \"gone.png\", \"b.bmp\", \"a.bmp\"]"))), CancellationToken.None));
        Assert.Equal(["a.bmp", "b.bmp"], five.Images.Select(i => i.Path));
        Assert.EndsWith("\n" + FileText.MorePictures(["a.bmp"]), five.Text, StringComparison.Ordinal);
        Assert.Equal(FileText.BadStringList("paths", "[1]"), await Invoke(view, ("paths", Json("[1]"))));
    }

    [Fact]
    public void ViewImage_Cap_IsTheSetting_Clamped_AndQuotedByTheDescriptionAndTheSchema()
    {
        var view = Tool<ViewImageTool>();
        Assert.Equal(10, AppSettingsData.DefaultViewImageMaxPerCall);
        Assert.Equal(1, AppSettingsData.MinViewImageMaxPerCall);
        Assert.Equal(100, AppSettingsData.MaxViewImageMaxPerCall);
        Assert.Equal(10, ViewImageTool.LimitOf(new AppSettingsData()));
        Assert.Equal(1, ViewImageTool.LimitOf(new AppSettingsData { FileViewImageMaxPerCall = 0 }));      // a hand-edited value clamped
        Assert.Equal(100, ViewImageTool.LimitOf(new AppSettingsData { FileViewImageMaxPerCall = 500 }));

        Assert.Equal(
            "Shows you image files (png, jpg, gif, webp, bmp) under the working directory (the user's cwd / current directory): one as path, or up to 10 per call as paths " +
            "(more than 10 shows the first 10 and names the rest for the next call; a batch per call, not a call per picture). They are attached to the message after the result, so you can describe or analyse them; read_file cannot read images.",
            view.Description);
        Assert.Equal(ViewImageTool.Describe(10), view.Description);
        Assert.Contains("(at most 10)", view.JsonSchema.GetProperty("properties").GetProperty("paths").GetProperty("description").GetString());

        // The row edited: the next request's description and schema carry the new cap.
        _settings.FileViewImageMaxPerCall = 3;
        Assert.Equal(ViewImageTool.Describe(3), view.Description);
        Assert.Contains("up to 3 per call", view.Description);
        Assert.Contains("(at most 3)", view.JsonSchema.GetProperty("properties").GetProperty("paths").GetProperty("description").GetString());
        Assert.Equal(ViewImageTool.SchemaFor(3).GetRawText(), view.JsonSchema.GetRawText());
    }
}
