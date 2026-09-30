using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The eight vault tools over a temp vault (2026-09-22): the pinned names and schemas, resolution, every read and write, the daily note, the move with its link rewrites, and the refusals.</summary>
public sealed class ObsidianToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { FileSafeEdits = true, ObsidianTools = true };   // the switch off by default since 2026-09-29
    private readonly ObsidianVault _vault;
    private readonly IReadOnlyList<AIFunction> _tools;

    public ObsidianToolsTests()
    {
        _root = Path.Combine(_dir, "vault");
        Directory.CreateDirectory(Path.Combine(_root, ".obsidian"));
        _settings.ObsidianVault = _root;
        _vault = new ObsidianVault(() => _settings.ObsidianVault, _time);
        _tools = App.ChatScreen.ObsidianTools(_vault, () => _settings);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private T Tool<T>() where T : AIFunction => _tools.OfType<T>().Single();

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs) => new(pairs.ToDictionary(p => p.Name, p => p.Value));

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction =>
        (string)(await Tool<T>().InvokeAsync(Args(pairs)))!;

    private void Put(string relative, string content)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Get(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    // ---- the shape ----

    [Fact]
    public void Names_Schemas_AndDescriptions_ArePinned()
    {
        Assert.Equal(ObsidianToolNames.All, _tools.Select(t => t.Name));
        Assert.Equal(["vault_search", "vault_list", "vault_read", "vault_links", "vault_daily", "vault_write", "vault_properties", "vault_move", "vault_delete"], ObsidianToolNames.All);   // vault_delete the ninth, later on 2026-09-22
        Assert.All(_tools, t => Assert.Contains("Obsidian vault", t.Description, StringComparison.Ordinal));
        Assert.All(_tools, t => Assert.Equal("object", t.JsonSchema.GetProperty("type").GetString()));
        Assert.True(ObsidianToolNames.All.All(App.ChatScreen.ObsidianToolNames.Contains));

        static string[] Props(AIFunction t) => t.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        static string[] Required(AIFunction t) => t.JsonSchema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];

        Assert.Equal(["query", "tag", "folder", "max_results"], Props(Tool<VaultSearchTool>()));
        Assert.Equal(["query"], Required(Tool<VaultSearchTool>()));
        Assert.Equal(["what", "folder", "tag", "property", "value", "max_results"], Props(Tool<VaultListTool>()));
        Assert.Empty(Required(Tool<VaultListTool>()));
        Assert.Equal(["note", "heading", "start_line", "max_lines"], Props(Tool<VaultReadTool>()));
        Assert.Equal(["note"], Props(Tool<VaultLinksTool>()));
        Assert.Equal(["date", "append"], Props(Tool<VaultDailyTool>()));
        Assert.Equal(["note", "content", "mode", "heading"], Props(Tool<VaultWriteTool>()));
        Assert.Equal(["note", "content"], Required(Tool<VaultWriteTool>()));
        Assert.Equal(["note", "set", "remove"], Props(Tool<VaultPropertiesTool>()));
        Assert.Equal(["note", "to"], Props(Tool<VaultMoveTool>()));
        Assert.Equal(["note", "to"], Required(Tool<VaultMoveTool>()));
        Assert.Equal(["note"], Props(Tool<VaultDeleteTool>()));
        Assert.Equal(["note"], Required(Tool<VaultDeleteTool>()));
    }

    // ---- vault_delete (later on 2026-09-22) ----

    [Fact]
    public void AllowDelete_IsOnByDefault_AndTheOfferDropsTheToolWhileItIsOff()
    {
        Assert.True(new AppSettingsData().ObsidianAllowDelete);   // on by default since 2026-09-23 (off from 2026-09-22)
        _settings.ObsidianAllowDelete = false;
        Assert.Equal(ObsidianToolNames.WithoutDelete, App.ChatScreen.ObsidianToolsFor(_tools, _settings).Select(t => t.Name));
        _settings.ObsidianAllowDelete = true;
        Assert.Same(_tools, App.ChatScreen.ObsidianToolsFor(_tools, _settings));
    }

    [Fact]
    public async Task Delete_WithTheSettingOff_IsRefused_AndNothingMoves()
    {
        Put("Plan.md", "# Plan");
        _settings.ObsidianAllowDelete = false;   // on by default since 2026-09-23

        Assert.Equal(ObsidianText.DeleteOff, await Invoke<VaultDeleteTool>(("note", "Plan")));
        Assert.True(File.Exists(Path.Combine(_root, "Plan.md")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".trash")));
    }

    [Fact]
    public async Task Delete_MovesTheNoteIntoTheTrash_AndNamesTheNotesStillLinkingToIt()
    {
        _settings.ObsidianAllowDelete = true;
        Put("Projects/Plan.md", "# Plan");
        Put("Home.md", "See [[Plan]].");
        Put("Log.md", "Also [the plan](Projects/Plan.md).");
        Put("Other.md", "Nothing here.");

        string result = await Invoke<VaultDeleteTool>(("note", "[[Plan]]"));

        Assert.Equal("deleted Projects/Plan.md (moved to .trash/Plan.md); 2 notes still link to it:\nHome.md\nLog.md", result);
        Assert.False(File.Exists(Path.Combine(_root, "Projects", "Plan.md")));
        Assert.Equal("# Plan", Get(".trash/Plan.md"));
        Assert.Equal("See [[Plan]].", Get("Home.md"));   // the links are left as they are

        // A second note of the same name takes the next free trash name; Home's [[Plan]] now resolves to it.
        Put("Plan.md", "# Again");
        Assert.Equal("deleted Plan.md (moved to .trash/Plan 1.md); 1 note still links to it:\nHome.md", await Invoke<VaultDeleteTool>(("note", "Plan.md")));
        Assert.Equal("# Again", Get(".trash/Plan 1.md"));
        Assert.Equal("deleted Plan.md (moved to .trash/Plan 1.md); nothing links to it", ObsidianText.Note(ObsidianText.Deleted("Plan.md", ".trash/Plan 1.md", [])));
    }

    [Fact]
    public async Task Delete_TakesAnAttachmentByItsPath_KeepingItsExtension()
    {
        _settings.ObsidianAllowDelete = true;
        Put("assets/diagram.png", "png");
        Put("Home.md", "![[diagram.png]]");

        string result = await Invoke<VaultDeleteTool>(("note", "assets/diagram.png"));

        Assert.Equal("deleted assets/diagram.png (moved to .trash/diagram.png); 1 note still links to it:\nHome.md", result);
        Assert.Equal("png", Get(".trash/diagram.png"));
        Assert.False(File.Exists(Path.Combine(_root, "assets", "diagram.png")));
    }

    [Fact]
    public async Task Delete_RefusesAFolder_ADotFolder_OutsideTheVault_AndANameNothingMatches()
    {
        _settings.ObsidianAllowDelete = true;
        Put("Projects/Plan.md", "# Plan");
        Put(".obsidian/app.json", "{}");

        Assert.Equal(ObsidianText.IsAFolder("Projects"), await Invoke<VaultDeleteTool>(("note", "Projects")));
        Assert.Equal(ObsidianText.HiddenPath(".obsidian/app.json"), await Invoke<VaultDeleteTool>(("note", ".obsidian/app.json")));
        Assert.Equal(ObsidianText.OutsideVault("../outside.md"), await Invoke<VaultDeleteTool>(("note", "../outside.md")));
        Assert.StartsWith("Error: no note matches \"Nope\"", await Invoke<VaultDeleteTool>(("note", "Nope")), StringComparison.Ordinal);
        Assert.Equal(ObsidianText.Required("note"), await Invoke<VaultDeleteTool>(("note", "")));
        Assert.True(File.Exists(Path.Combine(_root, "Projects", "Plan.md")));
        Assert.Equal("Error: Projects is a folder; vault_delete takes one note or attachment at a time.", ObsidianText.IsAFolder("Projects"));
    }

    [Fact]
    public void Offered_NeedsTheSwitch_AndAFolderWithDotObsidian()
    {
        Assert.True(App.ChatScreen.ObsidianOffered(_settings));
        Assert.False(App.ChatScreen.ObsidianOffered(new AppSettingsData { ObsidianVault = _root, ObsidianTools = false }));
        Assert.False(App.ChatScreen.ObsidianOffered(new AppSettingsData { ObsidianVault = _dir, ObsidianTools = true }));
        Assert.False(App.ChatScreen.ObsidianOffered(new AppSettingsData { ObsidianVault = _root }));   // the switch is off in a fresh profile (2026-09-29)
        Assert.False(App.ChatScreen.ObsidianOffered(new AppSettingsData()));
    }

    [Fact]
    public async Task NoVault_AndNotAVault_AreRefused()
    {
        _settings.ObsidianVault = "";
        Assert.Equal(ObsidianText.NoVault, await Invoke<VaultListTool>());
        _settings.ObsidianVault = _dir;
        Assert.Equal(ObsidianText.NotAVault(_dir), await Invoke<VaultListTool>());
    }

    // ---- reading ----

    [Fact]
    public async Task Read_ResolvesByName_Path_Wikilink_AndAlias()
    {
        Put("Projects/Plan.md", "---\naliases: [Roadmap]\n---\n# Plan\nline\n");
        foreach (var note in new[] { "Plan", "plan", "Projects/Plan", "Projects/Plan.md", "[[Plan#Goals|x]]", "Roadmap" })
        {
            string result = await Invoke<VaultReadTool>(("note", note));
            Assert.StartsWith("Projects/Plan.md (5 lines):", result, StringComparison.Ordinal);
        }

        Assert.Equal(ObsidianText.NotFound("Nope", []), await Invoke<VaultReadTool>(("note", "Nope")));
        Assert.Equal(ObsidianText.NotFound("Pla", ["Projects/Plan.md"]), await Invoke<VaultReadTool>(("note", "Pla")));
    }

    [Fact]
    public async Task Read_AmbiguousName_PicksTheShortestPath_AndNamesTheOthers()
    {
        Put("Work/Plan.md", "work\n");
        Put("Old/Archive/Plan.md", "old\n");
        string result = await Invoke<VaultReadTool>(("note", "Plan"));
        Assert.Equal("Work/Plan.md (1 line):\nwork\n" + ObsidianText.AlsoNamed("Plan", ["Old/Archive/Plan.md"]), result);
        Assert.StartsWith("Old/Archive/Plan.md (1 line):", await Invoke<VaultReadTool>(("note", "Archive/Plan")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_AHeadingsSection_AndAWindow()
    {
        Put("Plan.md", "# Plan\nintro\n## Goals\ng1\ng2\n### Sub\ns\n## Steps\nst\n");
        Assert.Equal("Plan.md (lines 3–7 of 9):\n## Goals\ng1\ng2\n### Sub\ns", await Invoke<VaultReadTool>(("note", "Plan"), ("heading", "goals")));
        Assert.Equal("Plan.md (lines 4–5 of 9; next: start_line 6):\ng1\ng2", await Invoke<VaultReadTool>(("note", "Plan"), ("heading", "## Goals"), ("start_line", 4), ("max_lines", 2)));
        Assert.Equal(ObsidianText.HeadingNotFound("Plan.md", "Budget", ["Plan", "Goals", "Sub", "Steps"]), await Invoke<VaultReadTool>(("note", "Plan"), ("heading", "Budget")));
        Assert.Equal(ClockText.BadInteger("max_lines", "lots"), await Invoke<VaultReadTool>(("note", "Plan"), ("max_lines", "lots")));
    }

    [Fact]
    public async Task DotFolders_AreNeverListed_OrRead()
    {
        Put(".obsidian/workspace.md", "x\n");
        Put(".trash/Old.md", "x\n");
        Put("Note.md", "x\n");
        Assert.Equal("1 note:\nNote.md", await Invoke<VaultListTool>());
        Assert.StartsWith("Error: no note matches", await Invoke<VaultReadTool>(("note", "Old")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_FindsLinesAndNames_WithinATagOrFolder()
    {
        Put("Work/Alpha.md", "---\ntags: [project]\n---\nThe budget is tight.\n");
        Put("Home/Budget.md", "groceries\n");
        Put("Work/Beta.md", "no match here #other\n");
        Assert.Equal("2 matches in 2 notes for \"budget\":\nHome/Budget.md: (name)\nWork/Alpha.md:4: The budget is tight.", await Invoke<VaultSearchTool>(("query", "budget")));
        Assert.Equal("1 match in 1 note for \"budget\" tagged #project:\nWork/Alpha.md:4: The budget is tight.", await Invoke<VaultSearchTool>(("query", "budget"), ("tag", "#project")));
        Assert.Equal("no matches for \"budget\" in Work/, tagged #other", await Invoke<VaultSearchTool>(("query", "budget"), ("folder", "Work"), ("tag", "other")));
        Assert.Equal(ObsidianText.SearchNeedsQuery, await Invoke<VaultSearchTool>(("query", " ")));
    }

    [Fact]
    public async Task List_NotesByProperty_Tags_AndProperties()
    {
        Put("A.md", "---\nstatus: draft\ntags: [project/alpha]\n---\n#inline\n");
        Put("B.md", "---\nstatus: done\n---\n");
        Put("C.md", "#project\n");
        Assert.Equal("1 note with status: Draft:\nA.md · status: draft", await Invoke<VaultListTool>(("property", "status"), ("value", "Draft")));
        Assert.Equal("2 notes tagged #project:\nA.md\nC.md", await Invoke<VaultListTool>(("tag", "project")));
        Assert.Equal("3 tags:\n#inline (1)\n#project (1)\n#project/alpha (1)", await Invoke<VaultListTool>(("what", "tags")));
        Assert.Equal("2 properties:\nstatus (2)\ntags (1)", await Invoke<VaultListTool>(("what", "properties")));
        Assert.Equal(ObsidianText.BadChoice("what", "folders", VaultListTool.WhatChoices), await Invoke<VaultListTool>(("what", "folders")));
    }

    [Fact]
    public async Task Links_ShowsOutgoing_Unresolved_Embeds_AndBacklinks()
    {
        Put("Plan.md", "See [[Roadmap]] and [[Missing]].\n![[chart.png]]\n");
        Put("Roadmap.md", "Back to [[Plan|the plan]].\n");
        Put("Daily/2026-09-11.md", "Worked on [plan](../Plan.md).\n");
        Put("assets/chart.png", "png");
        string result = await Invoke<VaultLinksTool>(("note", "Plan"));
        Assert.Equal(
            "Plan.md: 2 links out (1 unresolved), 1 embed, 2 backlinks\n" +
            "Links out:\n  [[Roadmap]] → Roadmap.md (line 1)\n  [[Missing]] → unresolved (line 1)\n" +
            "Embeds:\n  ![[chart.png]] → assets/chart.png (line 2)\n" +
            "Backlinks:\n  Daily/2026-09-11.md:1: [plan](../Plan.md)\n  Roadmap.md:1: [[Plan|the plan]]",
            result);
    }

    // ---- writing ----

    [Fact]
    public async Task Write_CreatesANote_AndRefusesAnExistingOne()
    {
        Assert.Equal("created Ideas/New.md (2 lines, 4 words)", await Invoke<VaultWriteTool>(("note", "Ideas/New"), ("content", "# New\nsome text")));
        Assert.Equal("# New\nsome text\n", Get("Ideas/New.md"));
        Assert.Equal(ObsidianText.Exists("Ideas/New.md"), await Invoke<VaultWriteTool>(("note", "Ideas/New.md"), ("content", "x")));
        Assert.Equal(ObsidianText.BadChoice("mode", "replace", VaultWriteTool.ModeChoices), await Invoke<VaultWriteTool>(("note", "X"), ("content", "x"), ("mode", "replace")));
    }

    [Fact]
    public async Task Write_ABareName_GoesWhereAppJsonPutsNewNotes()
    {
        Put(".obsidian/app.json", "{ \"newFileLocation\": \"folder\", \"newFileFolderPath\": \"Inbox\" }");
        Assert.StartsWith("created Inbox/Thought.md", await Invoke<VaultWriteTool>(("note", "Thought"), ("content", "x")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_RefusesOutsideTheVault_DotFolders_AndOtherExtensions()
    {
        Assert.Equal(ObsidianText.OutsideVault("../escape.md"), await Invoke<VaultWriteTool>(("note", "../escape"), ("content", "x")));
        Assert.Equal(ObsidianText.HiddenPath(".obsidian/evil.md"), await Invoke<VaultWriteTool>(("note", ".obsidian/evil"), ("content", "x")));
        Assert.Equal(ObsidianText.NotANote("notes.txt"), await Invoke<VaultWriteTool>(("note", "notes.txt"), ("content", "x")));
        Assert.False(File.Exists(Path.Combine(_dir, "escape.md")));
    }

    [Fact]
    public async Task Overwrite_KeepsThePreviousVersionInTheVaultsTrash_UnderSafeEdits()
    {
        Put("Plan.md", "old\n");
        Assert.Equal("replaced Plan.md (1 line, 1 word)" + ObsidianText.KeptIn(".trash/Plan.md"), await Invoke<VaultWriteTool>(("note", "Plan"), ("content", "new"), ("mode", "overwrite")));
        Assert.Equal("old\n", Get(".trash/Plan.md"));
        Assert.Equal("new\n", Get("Plan.md"));
        _settings.FileSafeEdits = false;
        Assert.Equal("replaced Plan.md (1 line, 1 word)", await Invoke<VaultWriteTool>(("note", "Plan"), ("content", "newer"), ("mode", "overwrite")));
        Assert.False(File.Exists(Path.Combine(_root, ".trash", "Plan 1.md")));
    }

    [Fact]
    public async Task AppendAndPrepend_AtTheEnds_AndUnderAHeading_KeepingCrlfAndTheBom()
    {
        File.WriteAllBytes(Path.Combine(_root, "Plan.md"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("---\ntags: [x]\n---\n# Goals\n- a\n\n# Steps\n- s\n".Replace("\n", "\r\n", StringComparison.Ordinal))]);
        Assert.StartsWith("appended to Plan.md under \"Goals\"", await Invoke<VaultWriteTool>(("note", "Plan"), ("content", "- b"), ("mode", "append"), ("heading", "Goals")), StringComparison.Ordinal);
        Assert.StartsWith("prepended to Plan.md", await Invoke<VaultWriteTool>(("note", "Plan"), ("content", "top"), ("mode", "prepend")), StringComparison.Ordinal);
        await Invoke<VaultWriteTool>(("note", "Plan"), ("content", "end"), ("mode", "append"));
        await Invoke<VaultWriteTool>(("note", "Plan"), ("content", "first step"), ("mode", "prepend"), ("heading", "Steps"));
        byte[] bytes = File.ReadAllBytes(Path.Combine(_root, "Plan.md"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("---\r\ntags: [x]\r\n---\r\ntop\r\n# Goals\r\n- a\r\n- b\r\n\r\n# Steps\r\nfirst step\r\n- s\r\nend\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.Equal(ObsidianText.HeadingNotFound("Plan.md", "Budget", ["Goals", "Steps"]), await Invoke<VaultWriteTool>(("note", "Plan"), ("content", "x"), ("mode", "append"), ("heading", "Budget")));
    }

    [Fact]
    public async Task Append_ToAMissingNote_CreatesIt()
    {
        Assert.StartsWith("created Log.md", await Invoke<VaultWriteTool>(("note", "Log"), ("content", "first"), ("mode", "append")), StringComparison.Ordinal);
        Assert.Equal("first\n", Get("Log.md"));
    }

    [Fact]
    public async Task Properties_List_Set_AndRemove()
    {
        Put("Plan.md", "---\nstatus: draft\n# keep me\nold: 1\n---\nBody #inline\n");
        Assert.Equal("Plan.md: 2 properties\nstatus: draft\nold: 1\n(inline tags: #inline)", await Invoke<VaultPropertiesTool>(("note", "Plan")));
        string result = await Invoke<VaultPropertiesTool>(("note", "Plan"), ("set", Json("{\"status\": \"done\", \"tags\": [\"a\", \"b\"]}")), ("remove", Json("[\"old\", \"gone\"]")));
        Assert.Equal("set status, tags; removed old; no gone to remove on Plan.md\nPlan.md: 2 properties\nstatus: done\ntags: [a, b]\n(inline tags: #inline)", result);
        Assert.Equal("---\nstatus: done\n# keep me\ntags:\n  - a\n  - b\n---\nBody #inline\n", Get("Plan.md"));
        Assert.Equal(ObsidianText.BadProperty("meta"), await Invoke<VaultPropertiesTool>(("note", "Plan"), ("set", Json("{\"meta\": {\"a\": 1}}"))));
        Assert.Equal(ObsidianText.BadSet, await Invoke<VaultPropertiesTool>(("note", "Plan"), ("set", Json("[{\"a\": 1}, {\"b\": 2}]"))));
    }

    [Fact]
    public async Task Properties_SetAsAString_TheWayAServerParserHandsItOver()
    {
        Put("Plan.md", "Body\n");
        await Invoke<VaultPropertiesTool>(("note", "Plan"), ("set", "{'done': True}"));
        Assert.Equal("---\ndone: true\n---\nBody\n", Get("Plan.md"));
    }

    // ---- the daily note ----

    [Fact]
    public async Task Daily_CreatesTodaysNoteFromTheTemplate_InTheConfiguredFolder()
    {
        Put(".obsidian/daily-notes.json", "{ \"folder\": \"Journal\", \"format\": \"YYYY-MM-DD dddd\", \"template\": \"Templates/Day\" }");
        Put("Templates/Day.md", "# {{title}}\n## Log\n");
        string result = await Invoke<VaultDailyTool>();
        Assert.Equal("Journal/2026-09-11 Friday.md (created from Templates/Day.md, 2 lines):\n# 2026-09-11 Friday\n## Log", result);
        Assert.Equal("Journal/2026-09-11 Friday.md (appended, 3 lines):\n# 2026-09-11 Friday\n## Log\n- called Sam", await Invoke<VaultDailyTool>(("append", "- called Sam")));
        Assert.Equal("Journal/2026-09-10 Thursday.md (created from Templates/Day.md, 2 lines):\n# 2026-09-10 Thursday\n## Log", await Invoke<VaultDailyTool>(("date", "yesterday")));
        Assert.Equal(ObsidianText.BadDate("soon"), await Invoke<VaultDailyTool>(("date", "soon")));
    }

    [Fact]
    public async Task Daily_WithoutConfig_IsTheRootAndIsoDates()
    {
        Assert.Equal("2026-09-11.md (created, appended, 1 line):\nhello", await Invoke<VaultDailyTool>(("append", "hello")));
    }

    // ---- moving ----

    [Fact]
    public async Task Move_RewritesEveryLinkForm_AndKeepsSubpathsAndAliases()
    {
        Put("Projects/Plan.md", "Self [[Plan#Goals]]. See [notes](../Notes/Ref.md).\n# Goals\n");
        Put("Notes/Ref.md", "ref\n");
        Put("Index.md", "[[Plan]] [[Plan#Goals|goals]] ![[Plan]] [[Projects/Plan]] [p](Projects/Plan.md) `[[Plan]]`\n");
        Put("Notes/Deep.md", "[up](../Projects/Plan.md#Goals)\r\n");
        string result = await Invoke<VaultMoveTool>(("note", "Plan"), ("to", "Archive/Old Plan"));
        Assert.Equal("moved Projects/Plan.md to Archive/Old Plan.md; updated 8 links in 3 notes:\nIndex.md (5)\nNotes/Deep.md (1)\nArchive/Old Plan.md (2)", result);
        Assert.False(File.Exists(Path.Combine(_root, "Projects", "Plan.md")));
        Assert.Equal("[[Old Plan]] [[Old Plan#Goals|goals]] ![[Old Plan]] [[Archive/Old Plan]] [p](Archive/Old%20Plan.md) `[[Plan]]`\n", Get("Index.md"));
        Assert.Equal("[up](../Archive/Old%20Plan.md#Goals)\r\n", Get("Notes/Deep.md"));
        Assert.Equal("Self [[Old Plan#Goals]]. See [notes](../Notes/Ref.md).\n# Goals\n", Get("Archive/Old Plan.md"));
    }

    [Fact]
    public async Task Move_IntoAFolder_KeepsAUniqueNameAsItIs_AndReAimsRelativeLinks()
    {
        Put("Plan.md", "[r](Notes/Ref.md)\n");
        Put("Notes/Ref.md", "[[Plan]]\n");
        Assert.Equal("moved Plan.md to Archive/2026/Plan.md; updated 1 link in 1 note:\nArchive/2026/Plan.md (1)", await Invoke<VaultMoveTool>(("note", "Plan"), ("to", "Archive/2026/")));
        Assert.Equal("[[Plan]]\n", Get("Notes/Ref.md"));
        Assert.Equal("[r](../../Notes/Ref.md)\n", Get("Archive/2026/Plan.md"));
    }

    [Fact]
    public async Task Move_ToANameAnotherNoteHas_WritesThePath()
    {
        Put("A/Plan.md", "a\n");
        Put("B/Other.md", "b\n");
        Put("Index.md", "[[Other]]\n");
        await Invoke<VaultMoveTool>(("note", "Other"), ("to", "Plan"));
        Assert.Equal("[[B/Plan]]\n", Get("Index.md"));
    }

    [Fact]
    public async Task DottedNames_AreNotes_AndNamesObsidianForbids_AreRefused()
    {
        Assert.StartsWith("created 2026.09.22.md", await Invoke<VaultWriteTool>(("note", "2026.09.22"), ("content", "x")), StringComparison.Ordinal);
        Assert.StartsWith("created v1.2 plan.md", await Invoke<VaultWriteTool>(("note", "v1.2 plan"), ("content", "y")), StringComparison.Ordinal);
        Assert.StartsWith("2026.09.22.md (1 line):", await Invoke<VaultReadTool>(("note", "2026.09.22")), StringComparison.Ordinal);
        Assert.Equal(ObsidianText.BadName("a:b"), await Invoke<VaultWriteTool>(("note", "a:b"), ("content", "x")));   // an NTFS stream otherwise
        Assert.Equal(ObsidianText.BadName("why?"), await Invoke<VaultWriteTool>(("note", "Q/why?"), ("content", "x")));
        Assert.False(File.Exists(Path.Combine(_root, "a")));
    }

    [Fact]
    public async Task Move_KeepsATablesEscapedPipe()
    {
        Put("Plan.md", "p\n");
        Put("Index.md", "| a | [[Plan\\|the plan]] |\n");
        await Invoke<VaultMoveTool>(("note", "Plan"), ("to", "Roadmap"));
        Assert.Equal("| a | [[Roadmap\\|the plan]] |\n", Get("Index.md"));
    }

    [Fact]
    public async Task Move_Refusals()
    {
        Put("Plan.md", "a\n");
        Put("Taken.md", "b\n");
        Assert.Equal(ObsidianText.Exists("Taken.md"), await Invoke<VaultMoveTool>(("note", "Plan"), ("to", "Taken")));
        Assert.Equal(ObsidianText.NothingToMove, await Invoke<VaultMoveTool>(("note", "Plan"), ("to", "Plan.md")));
        Assert.Equal(ObsidianText.HiddenPath(".trash/Plan.md"), await Invoke<VaultMoveTool>(("note", "Plan"), ("to", ".trash/")));
        Assert.Equal(ObsidianText.Required("to"), await Invoke<VaultMoveTool>(("note", "Plan"), ("to", "")));
        Assert.True(File.Exists(Path.Combine(_root, "Plan.md")));
    }

    [Fact]
    public async Task TheIndex_SeesAnEditMadeOutsideTheTools()
    {
        Put("Plan.md", "one\n");
        Assert.Equal("1 note:\nPlan.md", await Invoke<VaultListTool>());
        Put("Later.md", "#new\n");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "Plan.md"), DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(Path.Combine(_root, "Plan.md"), "two #changed\n");
        Assert.Equal("2 tags:\n#changed (1)\n#new (1)", await Invoke<VaultListTool>(("what", "tags")));
    }
}
