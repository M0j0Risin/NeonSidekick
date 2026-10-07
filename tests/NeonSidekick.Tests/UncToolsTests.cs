using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Unc;

namespace NeonSidekick.Tests;

/// <summary>
/// The UNC tools (2026-09-30) over local folders standing in for shares: their names and schemas, what a turn offers of them
/// (the reads; fetch and put with the file tools; the changes only under both keys), each read scoped to its share, the write
/// gate at every call, changes permanent and audited, a move kept within one share, the rule and the group.
/// </summary>
public sealed class UncToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _eng;
    private readonly string _data;
    private readonly string _sandboxRoot;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { UncTools = true, FileTools = true };
    private readonly WorkingDirectory _sandbox;
    private UncCatalog _catalog;
    private readonly IReadOnlyList<AIFunction> _tools;

    public UncToolsTests()
    {
        _eng = Path.Combine(_dir, "eng");
        _data = Path.Combine(_dir, "data");
        _sandboxRoot = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_eng);
        Directory.CreateDirectory(_data);
        Put(_eng, @"specs\a.md", "alpha needle\nbeta\n");
        Put(_eng, @"specs\b.md", "gamma\n");
        Put(_data, "notes.txt", "one\ntwo\n");
        _catalog = new UncCatalog([Share("eng", _eng), Share("data", _data, "readwrite")], []);
        _sandbox = new WorkingDirectory(() => _sandboxRoot, _time);
        _tools = ChatScreen.UncTools(new UncAccess(() => _catalog, _time), _sandbox, () => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static UncNamedShare Share(string name, string path, string? access = null, string? description = null) =>
        new(name, new UncShareConfig { Path = path, Access = access, Description = description }, "test");

    private static string Put(string root, string relative, string text)
    {
        string full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
        return full;
    }

    private T Tool<T>() where T : AIFunction => _tools.OfType<T>().Single();

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction =>
        ToolAnswers.Text(await Tool<T>().InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))));

    private string EngName => UncText.ShareName(_catalog.Shares[0]);

    private string DataName => UncText.ShareName(_catalog.Shares[1]);

    [Fact]
    public void Names_AndSchemas_ArePinned()
    {
        Assert.Equal(UncToolNames.All, _tools.Select(t => t.Name));
        Assert.Equal(UncToolNames.Writes.Order(StringComparer.Ordinal), ChatScreen.UncWriteToolNames.Order(StringComparer.Ordinal));
        static string[] Properties(AIFunction tool) => tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["check"], Properties(Tool<UncSharesTool>()));
        Assert.Equal(["share", "text", "path", "files", "regex", "context", "output", "order", "limit", "depth"], Properties(Tool<UncSearchTool>()));
        Assert.Equal(["share", "path"], Properties(Tool<UncInfoTool>()));
        Assert.Equal(["share", "path", "start_line", "max_lines"], Properties(Tool<UncReadTool>()));
        Assert.Equal(["share", "path", "to", "overwrite"], Properties(Tool<UncFetchTool>()));
        Assert.Equal(["share", "path", "content", "mode"], Properties(Tool<UncWriteTool>()));
        Assert.Equal(["share", "path", "old_text", "new_text", "replace_all"], Properties(Tool<UncPatchTool>()));
        Assert.Equal(["share", "path"], Properties(Tool<UncCreateDirectoryTool>()));
        Assert.Equal(["share", "from", "to", "overwrite"], Properties(Tool<UncMoveTool>()));
        Assert.Equal(["share", "from", "to", "overwrite"], Properties(Tool<UncCopyTool>()));
        Assert.Equal(["share", "path"], Properties(Tool<UncDeleteTool>()));
        Assert.Equal(["from", "share", "to", "overwrite"], Properties(Tool<UncPutTool>()));
        Assert.Contains(@"\\server\share", Tool<UncReadTool>().JsonSchema.GetProperty("properties").GetProperty("share").GetProperty("description").GetString());
        Assert.Contains("gone for good", Tool<UncDeleteTool>().Description);
        Assert.All(UncToolNames.Reads, name => Assert.True(NeonSidekick.Plans.PlanTools.Allowed(name), name));
        Assert.All(UncToolNames.All.Except(UncToolNames.Reads), name => Assert.Contains(name, NeonSidekick.Plans.PlanTools.Mutating));
    }

    [WindowsFact]
    public void ATurnOffers_TheReads_FetchAndPutWithTheFileTools_AndTheChangesOnlyUnderBothKeys()
    {
        IEnumerable<string> Offered(AppSettingsData effective, UncCatalog catalog, bool files) => ChatScreen.UncToolsFor(_tools, effective, catalog, files).Select(t => t.Name);

        var readOnly = new UncCatalog([Share("eng", _eng)], []);
        Assert.Equal(["unc_shares", "unc_search", "unc_info", "unc_read", "unc_fetch"], Offered(_settings, _catalog, files: true));   // UNC writes off
        Assert.Equal(UncToolNames.Reads, Offered(_settings, _catalog, files: false));
        var writes = new AppSettingsData { UncTools = true, UncWrites = true };
        Assert.Equal(["unc_shares", "unc_search", "unc_info", "unc_read", "unc_fetch"], Offered(writes, readOnly, files: true));   // no readwrite share
        Assert.Equal(UncToolNames.All, Offered(writes, _catalog, files: true));
        Assert.Equal(UncToolNames.All.Where(n => n is not ("unc_fetch" or "unc_put")), Offered(writes, _catalog, files: false));

        Assert.True(ChatScreen.UncOffered(_settings, new UncAccess(() => _catalog, _time)));
        Assert.False(ChatScreen.UncOffered(new AppSettingsData(), new UncAccess(() => _catalog, _time)));   // off by default
        Assert.False(ChatScreen.UncOffered(_settings, new UncAccess(() => _catalog.Offered([]), _time)));
        Assert.False(new AppSettingsData().UncWrites);
    }

    /// <summary>No UNC on a Mac (2026-10-06, the tidy-up before the first Mac release): the switch on and a share named, the group is still not offered, and <c>/tools</c> says why.</summary>
    [UnixFact]
    public void OnMacOS_TheGroupIsNeverOffered()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.False(ChatScreen.UncOffered(_settings, new UncAccess(() => _catalog, _time)));
        Assert.Equal("it needs Windows", ToolsText.NeedsWindowsReason);
        Assert.Contains(UncDeleteTool.ToolName, new AppSettingsData().ToolsDisabled);   // opt-in even under UNC writes
    }

    [WindowsFact]
    public async Task UncShares_ListsEachShare_AndCheckReachesThem()
    {
        _catalog = new UncCatalog([Share("eng", _eng, description: "specs"), Share("data", _data, "readwrite"), Share("gone", Path.Combine(_dir, "nowhere"))], []);

        string listed = await Invoke<UncSharesTool>();
        Assert.Equal(UncText.Shares(_catalog, "", writesOn: false), listed);
        Assert.Contains($"- eng (default): {_eng}, as you, read-only — specs", listed);
        Assert.Contains($"- data: {_data}, as you, read-only (readwrite, but UNC writes is off)", listed);
        Assert.EndsWith("\nUNC writes is off: every share is read-only.", listed);

        string checkedOut = await Invoke<UncSharesTool>(("check", true));
        Assert.Contains($"- eng (default): {_eng}, as you, read-only — specs\n  " + UncText.Reached(1), checkedOut);   // specs\
        Assert.Contains("\n  " + UncText.RootMissing("gone", Path.Combine(_dir, "nowhere")), checkedOut);
        Assert.Equal(FileText.BadBoolean("check", "yes"), await Invoke<UncSharesTool>(("check", "yes")));
    }

    [WindowsFact]
    public async Task TheReads_AreTheFileToolsShapes_ScopedToTheShare()
    {
        string listing = await Invoke<UncSearchTool>();
        Assert.StartsWith(EngName + " (1 entry):", listing);   // the default share, its root's listing

        string hits = await Invoke<UncSearchTool>(("text", "needle"));
        Assert.Contains(@"specs\a.md:1: alpha needle", hits);
        Assert.Contains("under " + EngName, hits);

        string full = await Invoke<UncSearchTool>(("text", "one"), ("path", Path.Combine(_data, "notes.txt")));
        Assert.Contains("notes.txt:1: one", full);   // the share found from a full path

        Assert.Equal(FileText.Read(new WorkingDirectory(() => _eng, _time).ReadText(@"specs\a.md", null, null)), await Invoke<UncReadTool>(("path", @"specs\a.md")));
        Assert.StartsWith(@"specs\b.md — 6 bytes, 1 line, 1 word", await Invoke<UncInfoTool>(("share", "eng"), ("path", @"specs\b.md")));
        Assert.Equal(UncText.Scoped(FileText.OutsideRoot(@"..\data\notes.txt"), _catalog.Shares[0]), await Invoke<UncReadTool>(("path", @"..\data\notes.txt")));
        Assert.Contains("outside " + EngName, await Invoke<UncReadTool>(("path", @"..\data\notes.txt")));
        Assert.Equal(FileText.PathRequired("path"), await Invoke<UncReadTool>());
        Assert.Equal(UncText.UnknownShare("hr", "eng, data"), await Invoke<UncReadTool>(("share", "hr"), ("path", "x.txt")));
        Assert.Equal(FileText.BadBoolean("regex", "maybe"), await Invoke<UncSearchTool>(("regex", "maybe")));
    }

    [WindowsFact]
    public async Task UncSearch_Limit_FollowsFileSearchMaxResults_AndTheSchemaQuotesIt()
    {
        // search_files' setting is unc_search's too (2026-10-01, the user's ask); the share's budgets stay fixed.
        string bulk = Path.Combine(_eng, "bulk");
        Directory.CreateDirectory(bulk);
        for (int i = 0; i < 250; i++)
        {
            File.WriteAllText(Path.Combine(bulk, $"n{i:000}.txt"), "needle");
        }

        static string Limit(AIFunction tool) => tool.JsonSchema.GetProperty("properties").GetProperty("limit").GetProperty("description").GetString()!;
        static int Rows(string text) => text.Split('\n').Count(line => line.Contains(@"bulk\n", StringComparison.Ordinal));
        Assert.Equal(SearchFilesTool.LimitDescription(200), Limit(Tool<UncSearchTool>()));
        Assert.Equal(200, Rows(await Invoke<UncSearchTool>(("text", "needle"), ("path", "bulk"), ("limit", 250))));

        _settings.FileSearchMaxResults = 2000;
        Assert.Equal(SearchFilesTool.LimitDescription(2000), Limit(Tool<UncSearchTool>()));
        Assert.Equal(250, Rows(await Invoke<UncSearchTool>(("text", "needle"), ("path", "bulk"), ("limit", 250))));
        Assert.Equal(250, Rows(await Invoke<UncSearchTool>(("files", "*.txt"), ("path", "bulk"), ("limit", 250))));
    }

    [WindowsFact]
    public async Task UncFetch_CopiesIntoTheWorkingDirectory_ReplacingOnlyWithOverwrite()
    {
        string fetched = await Invoke<UncFetchTool>(("share", "eng"), ("path", @"specs\a.md"));
        Assert.Equal($"fetched specs\\a.md from {EngName} to a.md in the working directory", fetched);
        Assert.Equal("alpha needle\nbeta\n", File.ReadAllText(Path.Combine(_sandboxRoot, "a.md")));

        Put(_eng, @"specs\a.md", "changed\n");
        string again = await Invoke<UncFetchTool>(("share", "eng"), ("path", @"specs\a.md"), ("overwrite", true));
        Assert.Equal($"fetched specs\\a.md from {EngName} to a.md in the working directory", again);   // nothing kept (File safe edits went 2026-10-01)
        Assert.False(Directory.Exists(Path.Combine(_sandboxRoot, ".trash")));
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(_sandboxRoot, "a.md")));

        Assert.Equal(UncText.Scoped(FileText.Error(FileOutcome.OutsideRoot, @"..\data", "fetch"), _catalog.Shares[0]), await Invoke<UncFetchTool>(("path", @"..\data")));
        Assert.Contains("already", await Invoke<UncFetchTool>(("share", "eng"), ("path", @"specs\a.md")));   // the working directory's end, its own words
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(_eng, @"specs\a.md")));   // nothing on the share changed
    }

    [Fact]
    public async Task EveryChange_NeedsUncWrites_AndAReadWriteShare_CheckedAtTheCall()
    {
        Assert.Equal(UncText.WritesOff, await Invoke<UncWriteTool>(("share", "data"), ("path", "x.txt"), ("content", "x")));
        _settings.UncWrites = true;
        Assert.Equal(UncText.ReadOnlyShare("eng"), await Invoke<UncWriteTool>(("share", "eng"), ("path", "x.txt"), ("content", "x")));
        Assert.Equal(UncText.ReadOnlyShare("eng"), await Invoke<UncDeleteTool>(("path", @"specs\a.md")));   // the default share, read-only
        Assert.Equal(UncText.ReadOnlyShare("eng"), await Invoke<UncPutTool>(("from", "nothing.txt"), ("share", "eng")));
        Assert.False(File.Exists(Path.Combine(_eng, "x.txt")));
        Assert.True(File.Exists(Path.Combine(_eng, @"specs\a.md")));
    }

    [WindowsFact]
    public async Task OnAReadWriteShare_ChangesArePermanent_NothingKept_AndEachIsAudited()
    {
        _settings.UncWrites = true;
        var audit = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == UncConfigFile.Category && e.Level == DiagnosticLevel.Info) audit.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.StartsWith("wrote", await Invoke<UncWriteTool>(("share", "data"), ("path", @"new\x.txt"), ("content", "x")));
            Assert.Contains("already", await Invoke<UncWriteTool>(("share", "data"), ("path", @"new\x.txt"), ("content", "y")));   // create refuses
            Assert.StartsWith("replaced", await Invoke<UncWriteTool>(("share", "data"), ("path", @"new\x.txt"), ("content", "y"), ("mode", "overwrite")));
            Assert.StartsWith("appended", await Invoke<UncWriteTool>(("share", "data"), ("path", @"new\x.txt"), ("content", "z"), ("mode", "append")));
            Assert.StartsWith("Edited", await Invoke<UncPatchTool>(("share", "data"), ("path", "notes.txt"), ("old_text", "two"), ("new_text", "deux")), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("created", await Invoke<UncCreateDirectoryTool>(("share", "data"), ("path", "made")));
            Assert.StartsWith("copied", await Invoke<UncCopyTool>(("share", "data"), ("from", "notes.txt"), ("to", @"made\copy.txt")));
            Assert.StartsWith("renamed", await Invoke<UncMoveTool>(("share", "data"), ("from", @"made\copy.txt"), ("to", @"made\moved.txt")));
            string deleted = await Invoke<UncDeleteTool>(("share", "data"), ("path", "new"));
            Assert.DoesNotContain(".trash", deleted);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal("one\ndeux\n", File.ReadAllText(Path.Combine(_data, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(_data, @"made\moved.txt")));
        Assert.False(Directory.Exists(Path.Combine(_data, "new")));   // gone for good
        Assert.False(Directory.Exists(Path.Combine(_data, ".trash")));
        string me = Environment.UserDomainName + "\\" + Environment.UserName;
        Assert.Equal(
            [
                UncText.AuditLogLine("data", me, "wrote", @"new\x.txt"),
                UncText.AuditLogLine("data", me, "wrote over", @"new\x.txt"),
                UncText.AuditLogLine("data", me, "appended to", @"new\x.txt"),
                UncText.AuditLogLine("data", me, "patched", "notes.txt"),
                UncText.AuditLogLine("data", me, "created", "made"),
                UncText.AuditLogLine("data", me, "copied", "notes.txt"),
                UncText.AuditLogLine("data", me, "moved", @"made\copy.txt"),
                UncText.AuditLogLine("data", me, "deleted", "new"),
            ],
            audit);   // the refused create left no line
    }

    [WindowsFact]
    public async Task AMoveOrCopy_StaysWithinOneShare_AndPutCarriesFromTheWorkingDirectory()
    {
        _settings.UncWrites = true;
        string elsewhere = Path.Combine(_eng, "stolen.txt");
        Assert.Equal(UncText.CrossShare(elsewhere), await Invoke<UncMoveTool>(("share", "data"), ("from", "notes.txt"), ("to", elsewhere)));
        Assert.Equal(UncText.CrossShare(elsewhere), await Invoke<UncCopyTool>(("share", "data"), ("from", "notes.txt"), ("to", elsewhere)));
        Assert.False(File.Exists(elsewhere));
        Assert.True(File.Exists(Path.Combine(_data, "notes.txt")));

        Put(_sandboxRoot, "report.md", "# report\n");
        string put = await Invoke<UncPutTool>(("from", "report.md"), ("share", "data"), ("to", @"out\report.md"));
        Assert.Equal($"put report.md into {DataName} as out\\report.md", put);
        Assert.Equal("# report\n", File.ReadAllText(Path.Combine(_data, @"out\report.md")));
        Assert.Equal($"put report.md into {DataName} as report.md", await Invoke<UncPutTool>(("from", "report.md"), ("share", "data")));
        Assert.Equal(FileText.Error(FileOutcome.OutsideRoot, @"..\eng\specs\a.md", "put"), await Invoke<UncPutTool>(("from", @"..\eng\specs\a.md"), ("share", "data")));
        Assert.Contains("already", await Invoke<UncPutTool>(("from", "report.md"), ("share", "data")));
    }

    [Fact]
    public void TheRules_NameTheTools_AndRideOnlyWithTheGroup_TheWriteSentenceOnlyWithAChange()
    {
        Assert.All(UncToolNames.Reads, name => Assert.Contains(name, Assistant.UncRule));
        Assert.Contains(@"\\server\share", Assistant.UncRule);
        Assert.Contains("unc_fetch", Assistant.UncFetchRule);
        Assert.Contains("permanently", Assistant.UncWriteRule);
        Assert.DoesNotContain(Assistant.UncRule, Assistant.DefaultRules(markdown: false, tools: true, mysql: true));
        string reads = Assistant.DefaultRules(markdown: false, tools: true, mysql: true, unc: true);
        Assert.True(reads.IndexOf(Assistant.MySqlRule, StringComparison.Ordinal) < reads.IndexOf(Assistant.UncRule, StringComparison.Ordinal));
        Assert.DoesNotContain(Assistant.UncWriteRule, reads);
        Assert.DoesNotContain(Assistant.UncFetchRule, reads);
        string all = Assistant.DefaultRules(markdown: false, tools: true, unc: true, uncFetch: true, uncWrite: true);
        Assert.Contains(Assistant.UncRule + " " + Assistant.UncFetchRule + " " + Assistant.UncWriteRule, all);
        Assert.DoesNotContain(Assistant.UncWriteRule, Assistant.DefaultRules(markdown: false, tools: true, uncWrite: true));   // no group, no sentence
        Assert.Contains(@"the unc_ tools list and reach the user's network shares (not net use, net share, net view, Get-SmbShare, dir \\server or copy \\server)", Assistant.ShellNativeRule(false, false, false, false, unc: true));
        Assert.Contains("never net use, net share or net view", Assistant.UncRule);   // 2026-10-03: "my UNC shares" went to net use
        Assert.StartsWith("When the user asks about their UNC shares", Tool<UncSharesTool>().Description, StringComparison.Ordinal);
        Assert.Contains("net use, net share, net view and Get-SmbShare", Tool<UncSharesTool>().Description);

        var facts = new SystemPromptFacts(null, null, null, false, [], false, false, UncEnabled: true, UncTools: 4);
        Assert.True(facts.Unc);
        Assert.Contains(Assistant.UncRule, SystemPromptSummary.SystemPrompt(facts));
        Assert.DoesNotContain(Assistant.UncWriteRule, SystemPromptSummary.SystemPrompt(facts));
        Assert.Contains(Assistant.UncWriteRule, SystemPromptSummary.SystemPrompt(facts with { UncWrite = true }));
        var group = Assert.Single(SystemPromptSummary.ToolGroups([], [], [], [], false, unc: _tools, uncEnabled: false), g => g.Label == ToolsText.UncTabTitle);
        Assert.Contains(SystemPromptSummary.UncOffSuffix, group.Note);
        Assert.Equal("UNC tools is off or no share in unc.json is offered", SystemPromptSummary.UncOffSuffix);
    }

    [Fact]
    public void PrepareTurn_OffersTheGroup_AfterTheDatabases_FetchOnlyWithTheFileTools()
    {
        var assistant = new Assistant(new FakeChatClient(), new ConversationHistory(""), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        var memory = new MemoryStore(_dir);
        string home = Path.Combine(_dir, "profile");
        var writes = new AppSettingsData { UncTools = true, UncWrites = true };
        var offered = ChatScreen.UncToolsFor(_tools, writes, _catalog, files: true);
        void Prepare(bool files) => ChatScreen.PrepareTurn(assistant, memory, [], [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: false, speechOutput: false, fileTools: ChatScreen.FileTools(_sandbox, () => true, _ => { }, () => new()), filesEnabled: files, uncTools: offered, uncEnabled: true);

        Prepare(files: true);
        var names = assistant.Tools.Select(t => t.Name).ToList();
        Assert.Equal(UncToolNames.All, names.Where(n => n.StartsWith("unc_", StringComparison.Ordinal)));
        Assert.Contains(Assistant.UncRule + " " + Assistant.UncFetchRule + " " + Assistant.UncWriteRule, assistant.History.SystemPrompt);

        Prepare(files: false);
        Assert.Equal(UncToolNames.All.Where(n => n is not ("unc_fetch" or "unc_put")), assistant.Tools.Select(t => t.Name).Where(n => n.StartsWith("unc_", StringComparison.Ordinal)));
        Assert.DoesNotContain(Assistant.UncFetchRule, assistant.History.SystemPrompt);
        Assert.Contains(Assistant.UncWriteRule, assistant.History.SystemPrompt);
    }

    [WindowsFact]
    public void TheMentionList_AndTheShareLines_ArePinned()
    {
        var choices = ChatScreen.UncChoices(_catalog);
        Assert.Equal(["eng", "data"], choices.Select(c => c.Text));
        Assert.Equal(_eng, choices[0].Note);
        Assert.Equal(_data + " · readwrite", choices[1].Note);
        Assert.Equal($"share 'eng' ({_eng})", EngName);
        Assert.Equal(EngName + " (2 entries):", UncText.Scoped(FileText.RootName + " (2 entries):", _catalog.Shares[0]));
        Assert.Equal("1 more share in unc.json is switched off for this profile (the UNC tab of /tools).", UncText.HiddenShares(1));
        Assert.Equal("reachable (3 entries at its root)", UncText.Reached(3));
        Assert.Equal(UncText.NoShares, UncText.Shares(UncCatalog.Empty, null, writesOn: true));
        Assert.Equal("Error: UNC writes is off, so every share is read-only; the user turns it on (the UNC tab of /tools)", UncText.WritesOff);
        Assert.Equal("Error: share 'eng' is read-only (\"access\": \"read\" in unc.json); the user makes it readwrite to allow changes", UncText.ReadOnlyShare("eng"));
    }

    /// <summary>
    /// <c>open</c> on a share (2026-10-01, the user's ask): the schema carries <c>share</c> only while a share is offered; a share's
    /// file, a full path under a share and a share's root open through the opener, worded for the share; a <c>runas</c> share on the
    /// network is refused unopened (the user's call); a share named while none is offered is refused; a relative path without one
    /// stays the working directory's.
    /// </summary>
    [WindowsFact]
    public async Task Open_OnAShare_OpensThroughTheOpener_RefusesARunAsNetworkShare_AndTheSchemaFollowsTheGroup()
    {
        var opened = new List<string>();
        var open = new OpenTool(_sandbox, opened.Add, new UncAccess(() => _catalog, _time), () => _settings);
        async Task<string> Open(params (string Name, object? Value)[] pairs) =>
            ToolAnswers.Text(await open.InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))));
        static string[] Properties(AIFunction tool) => tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(["path", "share"], Properties(open));
        Assert.Contains("UNC share", open.Description);
        Assert.Equal(@"opened specs\a.md in the user's editor", await Open(("share", "ENG"), ("path", @"specs\a.md")));
        Assert.Equal(@"opened specs\b.md in the user's editor", await Open(("path", Path.Combine(_eng, "specs", "b.md"))));   // the share a full path lies in
        Assert.Equal($"opened {DataName} in Explorer", await Open(("share", "data")));
        Assert.Equal(UncText.UnknownShare("nope", "eng, data"), await Open(("share", "nope"), ("path", "x")));
        Assert.Equal([Path.Combine(_eng, "specs", "a.md"), Path.Combine(_eng, "specs", "b.md"), _data], opened);

        _catalog = new UncCatalog([.. _catalog.Shares, new UncNamedShare("fin", new UncShareConfig { Path = @"\\fs02\fin", Auth = "runas", User = @"CORP\svc" }, "test")], []);
        Assert.Equal(UncText.OpenRunAsRefused("fin"), await Open(("share", "fin"), ("path", "q3.xlsx")));
        Assert.Equal("Error: share 'fin' signs in as another account, which a program the shell starts cannot use; unc_fetch the file into the working directory, then open the copy there", UncText.OpenRunAsRefused("fin"));
        Assert.Equal(3, opened.Count);   // nothing opened, nothing reached

        _settings.UncTools = false;
        Assert.Equal(["path"], Properties(open));
        Assert.DoesNotContain("UNC share", open.Description);
        Assert.Equal(UncText.NoSharesOffered, await Open(("share", "eng"), ("path", @"specs\a.md")));
        Assert.Equal(FileText.Missing("nope"), await Open(("path", "nope")));   // the working directory's, as ever
    }
}
