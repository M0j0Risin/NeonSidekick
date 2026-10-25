using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/profile push|pull mcp|links|videos|monitors|skills</c> (2026-10-25, the user's ask): each section's yes, its overwrite, its nothing to
/// copy (nothing asked) and its no, over the files of two profiles in the fixture's home.
/// </summary>
public sealed partial class ChatScreenTests
{
    private string McpFileHere => McpConfigFile.ProfilePath(_settings.ProfileDirectory);

    private string McpFileOf(string profile) => McpConfigFile.ProfilePath(ProfileDir(profile));

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // ── mcp ──────────────────────────────────────────────────────────────────

    /// <summary>A push adds what the target lacks, keeps a same-named one that differs (any case), and keeps the target's comments.</summary>
    [Fact]
    public async Task ProfilePushMcp_Yes_AddsMissing_KeepsSameNamed_AndTheComments()
    {
        WorkProfile();
        WriteFile(McpFileHere, """
            { "mcpServers": {
                "files": { "command": "files-server", "env": { "TOKEN": "abc" } },
                "docker": { "command": "docker", "args": ["mcp", "gateway", "run"] },
                "same": { "url": "http://localhost:9000/mcp" }
            } }
            """);
        WriteFile(McpFileOf("work"), """
            {
              // the work servers
              "mcpServers": {
                "Docker": { "command": "docker-old" },
                "same": { "url": "http://localhost:9000/mcp" }
              }
            }
            """);
        PushLine("/profile push mcp work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.ProfileMcpPrompt(["files"], 0, "work", pull: false), output);
        Assert.Contains(ChatScreen.ProfileMcpCopiedNotice(1, 0, 1, 1, 0, 0, "work", pull: false), output);
        string text = File.ReadAllText(McpFileOf("work"));
        Assert.Contains("// the work servers", text);
        var load = McpConfigFile.Load(McpFileOf("work"));
        Assert.Equal(["Docker", "same", "files"], load.Servers.Select(s => s.Key));
        Assert.Equal("docker-old", load.Servers[0].Value.Command);
        Assert.Equal("abc", load.Servers[2].Value.Env!["TOKEN"]);
    }

    /// <summary>With overwrite a same-named server (any case) is replaced, the target's spelling kept; MCP off, a note says so.</summary>
    [Fact]
    public async Task ProfilePullMcp_Overwrite_ReplacesSameNamed_KeepingTheTargetsSpelling()
    {
        WorkProfile();
        WriteFile(McpFileHere, """{ "mcpServers": { "Docker": { "command": "docker-old" }, "mine": { "command": "mine" } } }""");
        WriteFile(McpFileOf("work"), """{ "mcpServers": { "docker": { "command": "docker-new" }, "bad": { } } }""");
        PushLine("/profile pull mcp work overwrite");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.ProfileMcpPrompt(["Docker"], 1, "work", pull: true), output);
        Assert.Contains(ChatScreen.ProfileMcpCopiedNotice(0, 1, 0, 0, 1, 0, "work", pull: true), output);   // the bad entry counted
        Assert.Contains(ChatScreen.ProfileMcpOffNotice, output);
        var load = McpConfigFile.Load(McpFileHere);
        Assert.Equal(["Docker", "mine"], load.Servers.Select(s => s.Key));
        Assert.Equal("docker-new", load.Servers[0].Value.Command);
    }

    /// <summary>A pull with MCP on reloads, so the copied server connects at once.</summary>
    [Fact]
    public async Task ProfilePullMcp_WithMcpOn_Reloads()
    {
        WorkProfile();
        McpServer();
        WriteFile(McpFileOf("work"), """{ "mcpServers": { "other": { "command": "other-server" } } }""");
        PushLine("/profile pull mcp work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.ProfileMcpCopiedNotice(1, 0, 0, 0, 0, 0, "work", pull: true), output);
        Assert.Contains(McpText.ReloadedNotice(1, 0, 1), output);
        Assert.Equal(["pipe", "other"], _mcpServers.Requested.Select(r => r.Name));
    }

    /// <summary>Every server there already: said, nothing asked; no servers of its own: said too.</summary>
    [Fact]
    public async Task ProfilePushMcp_NothingNew_OrNone_SaysSo()
    {
        WorkProfile();
        PushLine("/profile push mcp work");   // this profile has no mcp.json
        PushLine("/profile pull mcp work");   // work's is empty
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.ProfileMcpNothingNotice("work", pull: false), output);
        Assert.Contains(ChatScreen.ProfileMcpNothingNotice("work", pull: true), output);
        Assert.DoesNotContain(ChatScreen.KeptNotice, output);
    }

    /// <summary>A target whose mcpServers is null loads as empty but takes no add: the editor's refusal, the file unchanged; a no keeps.</summary>
    [Fact]
    public async Task ProfilePushMcp_ANullSection_IsRefused_AndANoKeeps()
    {
        WorkProfile();
        WriteFile(McpFileHere, """{ "mcpServers": { "files": { "command": "files-server" } } }""");
        const string broken = """{ "mcpServers": null }""";
        WriteFile(McpFileOf("work"), broken);
        PushLine("/profile push mcp work");
        _console.Input.PushKey(Keys.Enter);   // No is on the cursor
        PushLine("/profile push mcp work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · " + ChatScreen.KeptNotice, output);
        Assert.Contains("Could not copy the MCP servers (0 written first): ", output);
        Assert.Equal(broken, File.ReadAllText(McpFileOf("work")));
    }

    /// <summary>A push into a profile with no folder yet makes it, the file from the empty text.</summary>
    [Fact]
    public async Task ProfilePushMcp_IntoAProfileWithNoFolder_MakesTheFile()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());
        WriteFile(McpFileHere, """{ "mcpServers": { "files": { "command": "files-server" } } }""");
        PushLine("/profile push mcp work");
        PickYes();
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(["files"], McpConfigFile.Load(McpFileOf("work")).Servers.Select(s => s.Key));
    }

    // ── links ────────────────────────────────────────────────────────────────

    /// <summary>A push appends in order, skipping an address the target holds; the source is untouched.</summary>
    [Fact]
    public async Task ProfilePushLinks_Yes_AppendsAndSkips()
    {
        WorkProfile();
        var here = new BrowserLinks(_settings.ProfileDirectory);
        here.Add("https://example.com/a", "A", out _);
        here.Add("https://example.com/b", "B", out _);
        new BrowserLinks(ProfileDir("work")).Add("https://EXAMPLE.com/b", "Theirs", out _);
        PushLine("/profile push links work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("Copy 2 saved links into \"work\"?", output);
        Assert.Contains("(1 saved link copied into \"work\", 1 already there)", output);
        Assert.Equal([("https://EXAMPLE.com/b", "Theirs"), ("https://example.com/a", "A")], new BrowserLinks(ProfileDir("work")).List().Select(l => (l.Url, l.Name)));
        Assert.Equal(2, here.List().Count);
    }

    /// <summary>A pull with overwrite replaces this profile's list and tells an open Browser window.</summary>
    [Fact]
    public async Task ProfilePullLinks_Overwrite_Replaces_AndTellsTheWindow()
    {
        WorkProfile();
        _browserWindow = new FakeBrowserWindow();
        new BrowserLinks(_settings.ProfileDirectory).Add("https://example.com/mine", null, out _);
        new BrowserLinks(ProfileDir("work")).Add("https://example.com/theirs", "Theirs", out _);
        PushLine("/profile pull links work overwrite");
        PickYes();
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(["https://example.com/theirs"], new BrowserLinks(_settings.ProfileDirectory).List().Select(l => l.Url));
        Assert.Equal(1, _browserWindow.LinksChangedCount);
    }

    /// <summary>None to copy: said, nothing asked.</summary>
    [Fact]
    public async Task ProfilePullLinks_FromAProfileWithNone_SaysSo()
    {
        WorkProfile();
        PushLine("/profile pull links work");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.ProfileLinksNothingNotice("work", pull: true), output);
    }

    // ── videos ───────────────────────────────────────────────────────────────

    /// <summary>A push appends whole records, skipping an id the target holds; a broken target file is refused and left alone.</summary>
    [Fact]
    public async Task ProfilePushVideos_Yes_AppendsWholeRecords_AndABrokenTargetIsRefused()
    {
        WorkProfile();
        var here = new YouTubeLibrary(_settings.ProfileDirectory);
        here.Add("dQw4w9WgXcQ", "Song", "Rick", 212, position: 60);
        here.Add("aaaaaaaaaaa", "Other");
        new YouTubeLibrary(ProfileDir("work")).Add("aaaaaaaaaaa");
        PushLine("/profile push videos work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("(1 saved video copied into \"work\", 1 already there)", output);
        var theirs = new YouTubeLibrary(ProfileDir("work")).List();
        Assert.Equal(["aaaaaaaaaaa", "dQw4w9WgXcQ"], theirs.Select(v => v.Id));
        Assert.Equal("Other", theirs[0].Title);   // a skipped one's blank filled
        Assert.Equal(60, theirs[1].Position);
    }

    [Fact]
    public async Task ProfilePushVideos_IntoABrokenFile_IsRefused()
    {
        WorkProfile();
        new YouTubeLibrary(_settings.ProfileDirectory).Add("dQw4w9WgXcQ", "Song");
        string path = new YouTubeLibrary(ProfileDir("work")).FilePath;
        WriteFile(path, "{ not json");
        PushLine("/profile push videos work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.ProfileVideosFailedError(YouTubeImportOutcome.Unreadable, "work", pull: false), output);
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    // ── monitors ─────────────────────────────────────────────────────────────

    /// <summary>A push merges by monitor key: the target's own name and PC input kept, the rest added.</summary>
    [Fact]
    public async Task ProfilePushMonitors_Yes_Merges()
    {
        Profiles.Create(_dir, "work", new AppSettingsData
        {
            MonitorInputNames = new() { ["key-a/0f"] = "Desk" },
            MonitorPcInputs = ["key-a/11"],
        });
        _settings.Update(d =>
        {
            d.MonitorInputNames = new() { ["key-a/0f"] = "Laptop", ["key-a/11"] = "PC" };
            d.MonitorInputsHidden = ["key-a/12"];
            d.MonitorPcInputs = ["key-a/0F"];
        });
        PushLine("/profile push monitors work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.ProfileMonitorsPrompt(4, "work", overwrite: false), output);
        Assert.Contains(ChatScreen.ProfileMonitorsCopiedNotice(2, 0, 2, "work", overwrite: false), output);
        var work = Profiles.ReadProfileFile(Profiles.ProfileFile(_dir, "work"));
        Assert.Equal("Desk", work.MonitorInputNames!["key-a/0f"]);
        Assert.Equal("PC", work.MonitorInputNames["key-a/11"]);
        Assert.Equal(["key-a/12"], work.MonitorInputsHidden);
        Assert.Equal(["key-a/11"], work.MonitorPcInputs);
    }

    /// <summary>A pull with overwrite replaces all three; a source with none has nothing to copy.</summary>
    [Fact]
    public async Task ProfilePullMonitors_Overwrite_Replaces_AndNoneSaysSo()
    {
        Profiles.Create(_dir, "work", new AppSettingsData { MonitorInputsHidden = ["key-b/1b"] });
        Profiles.Create(_dir, "empty", new AppSettingsData());
        _settings.Update(d => d.MonitorInputNames = new() { ["key-a/0f"] = "Laptop" });
        PushLine("/profile pull monitors work overwrite");
        PickYes();
        PushLine("/profile pull monitors empty");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Null(_settings.Current.MonitorInputNames);
        Assert.Equal(["key-b/1b"], _settings.Current.MonitorInputsHidden);
        Assert.Contains(ChatScreen.ProfileMonitorsNothingNotice("empty", pull: true), output);
    }

    // ── skills ───────────────────────────────────────────────────────────────

    private static string WriteSkill(string root, string folder, string name)
    {
        string directory = Path.Combine(root, folder);
        WriteFile(Path.Combine(directory, SkillCatalog.FileName), $"---\nname: {name}\ndescription: The {name} skill.\n---\nDo the {name} thing.\n");
        return directory;
    }

    private string SkillsHere => Path.Combine(_settings.ProfileDirectory, SkillRoots.DirectoryName);

    /// <summary>A push copies each skill, skipping a folder twin and a name twin, its markers with it.</summary>
    [Fact]
    public async Task ProfilePushSkills_Yes_Copies_SkippingTwins()
    {
        WorkProfile();
        string notes = WriteSkill(SkillsHere, "notes", "notes");
        File.WriteAllText(Path.Combine(notes, SkillLock.FileName), "");
        WriteSkill(SkillsHere, "todo", "todo");
        WriteSkill(SkillsHere, "plan", "planner");
        string theirs = SkillsMenu.ProfileSkillsRoot(_dir, "work");
        WriteSkill(theirs, "todo", "something-else");   // a folder twin
        WriteSkill(theirs, "planner-skill", "planner");   // a name twin
        PushLine("/profile push skills work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.ProfileSkillsPrompt(["notes"], "work", pull: false), output);
        Assert.Contains(ChatScreen.ProfileSkillsCopiedNotice(["notes"], ["plan", "todo"], "work", pull: false), output);
        Assert.True(File.Exists(Path.Combine(theirs, "notes", SkillLock.FileName)));
        Assert.True(File.Exists(Path.Combine(notes, SkillCatalog.FileName)));   // the original stays
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(theirs)!, "skills.~*"));
    }

    /// <summary>A pull copies into this profile; a folder name with a bidi character is shown escaped.</summary>
    [Fact]
    public async Task ProfilePullSkills_Copies_AndAnOddNameIsEscaped()
    {
        WorkProfile();
        string odd = "evil‮exe";
        WriteSkill(SkillsMenu.ProfileSkillsRoot(_dir, "work"), odd, "odd");
        PushLine("/profile pull skills work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(NameScrub.Display(odd), output);
        Assert.DoesNotContain(odd, output);
        Assert.True(File.Exists(Path.Combine(SkillsHere, odd, SkillCatalog.FileName)));
    }

    /// <summary>None of its own: said, nothing asked; everything there already: said too.</summary>
    [Fact]
    public async Task ProfilePushSkills_NothingToCopy_SaysSo()
    {
        WorkProfile();
        WriteSkill(SkillsMenu.ProfileSkillsRoot(_dir, "work"), "notes", "notes");
        PushLine("/profile push skills work");
        PushLine("/profile pull skills work");   // into this profile, which has none: copied
        PickYes();
        PushLine("/profile pull skills work");   // now every one is here already
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.ProfileSkillsNothingNotice("work", pull: false), output);
        Assert.Contains(ChatScreen.ProfileSkillsNothingNewNotice(["notes"], "work", pull: true), output);
    }
}
