using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The write guard (2026-10-22, the fix plan's mechanism 1 and D2): the model's writes judged by the file they reach — <c>.git</c>, <c>.neon</c>,
/// the app's home, the skill roots, a database, the project file (asked) — with the app's own writes let through by their scope, and the path
/// forms that used to slip past (a stream name, an 8.3 name, a device name, <c>~/</c>, a vanished root).
/// </summary>
public sealed class WriteGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly WorkingDirectory _files;

    public WriteGuardTests()
    {
        _root = Path.Combine(_dir, "work");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static WriteGuardFacts Facts(string? home = null, string? cwd = null, IReadOnlyList<string>? skills = null, DatabaseGuard? databases = null) =>
        new(() => home, () => cwd, () => skills ?? [], () => databases, () => false, () => null);

    private FileOutcome Write(string relative) => _files.WriteText(relative, "x", overwrite: true).Outcome;

    // ── .git and .neon: the path alone decides, facts or none ─────────────────

    [Theory]
    [InlineData(".neon/plans/x.md", FileOutcome.NeonFilesProtected)]
    [InlineData("app/.neon/x.md", FileOutcome.NeonFilesProtected)]
    [InlineData("app\\.NEON. \\x.md", FileOutcome.NeonFilesProtected)]
    [InlineData("sub/.git/config", FileOutcome.GitProtected)]
    [InlineData("app/neon/x.md", null)]
    [InlineData("", null)]
    public void AGuardedFolder_IsFoundAtAnyDepth(string relative, FileOutcome? expected)
    {
        // 2026-10-29, review P5-3.
        Assert.Equal(expected, WriteGuard.GuardedFolder(relative));
        Assert.Equal(expected is not null, OutputNames.UnderNeon(relative));
    }

    [Fact]
    public void Git_IsNeverWritten_MovedOrRenamed_ButAProjectHoldingOneMoves()
    {
        using var _ = WriteGuard.Configure(null);
        Directory.CreateDirectory(Path.Combine(_root, "proj", ".git", "hooks"));
        Assert.Equal(FileOutcome.GitProtected, Write(Path.Combine("proj", ".git", "hooks", "pre-commit")));
        Assert.Equal(FileOutcome.GitProtected, Write(Path.Combine("proj", ".GIT", "config")));
        Assert.Equal(FileOutcome.GitProtected, _files.Move(Path.Combine("proj", ".git"), "gitstuff", overwrite: false).Outcome);   // review I2
        Assert.Equal(FileOutcome.Ok, _files.Move("proj", "moved", overwrite: false).Outcome);
        Assert.True(Directory.Exists(Path.Combine(_root, "moved", ".git")));
    }

    [Fact]
    public void Neon_IsTheAppsAlone_AndItsScopeLetsTheAppWrite()
    {
        using var _ = WriteGuard.Configure(null);
        string plan = Path.Combine(".neon", "plans", "p.md");
        Assert.Equal(FileOutcome.NeonFilesProtected, Write(plan));
        Assert.Equal(FileOutcome.NeonFilesProtected, _files.Delete(".neon").Outcome);
        Assert.Equal(FileOutcome.Ok, WriteGuard.AsApp(() => _files.WriteText(plan, "x", overwrite: true)).Outcome);
        Assert.Equal(FileText.NeonFilesProtected(plan), FileText.Error(FileOutcome.NeonFilesProtected, plan, "write"));
    }

    /// <summary>A task started inside the app's scope is guarded again once the scope closes: the pass never outlives the call.</summary>
    [Fact]
    public async Task TheAppsScope_NeverOutlivesItsCall()
    {
        using var _ = WriteGuard.Configure(null);
        var release = new TaskCompletionSource();
        Task<FileOutcome> later;
        using (WriteGuard.AppWrites())
        {
            later = Task.Run(async () =>
            {
                await release.Task;
                return Write(Path.Combine(".neon", "late.md"));
            });
        }

        release.SetResult();
        Assert.Equal(FileOutcome.NeonFilesProtected, await later);
        Assert.False(WriteGuard.InAppWrites);
    }

    // ── the facts: home, skills, databases, the project file ─────────────────

    /// <summary>With the working directory over the home (<c>--cwd %USERPROFILE%</c>), the home is refused and the rest is not (review P2, U9).</summary>
    [Fact]
    public void TheHome_UnderTheWorkingDirectory_IsRefused()
    {
        string home = Path.Combine(_root, ".neonsidekick");
        using var _ = WriteGuard.Configure(Facts(home: home));
        Assert.Equal(FileOutcome.AppFilesProtected, Write(Path.Combine(".neonsidekick", "configs", "mcp.json")));
        Assert.Equal(FileOutcome.AppFilesProtected, Write(Path.Combine(".neonsidekick", "configs", "unc.json")));   // D12
        Assert.Equal(FileOutcome.AppFilesProtected, Write(Path.Combine(".neonsidekick", "profiles", "neon", "profile.json")));
        Assert.Equal(FileOutcome.Ok, Write("notes.md"));
    }

    /// <summary>
    /// A root inside the home's app-writable folders (the default <c>&lt;profile&gt;\files</c>, the images folder, a skill folder) writes inside
    /// itself; any other root inside the home does not (2026-10-23, the admin review's M1: this test's root was the home's <c>work</c>, which
    /// the own-root pass covered, as it covered the home and a profile folder themselves).
    /// </summary>
    [Fact]
    public void ARootInsideTheHome_WritesItsOwnFiles_OnlyInTheAppWritableFolders()
    {
        using var _ = WriteGuard.Configure(Facts(home: _dir));
        Assert.Equal(FileOutcome.AppFilesProtected, Write("notes.md"));
        foreach (string writable in new[] { Path.Combine("profiles", "neon", "files"), Path.Combine("profiles", "neon", "images", "edited"), Path.Combine("skills", "x") })
        {
            string root = Path.Combine(_dir, writable);
            Directory.CreateDirectory(root);
            var files = new WorkingDirectory(() => root, new ManualTimeProvider());
            Assert.Equal(FileOutcome.Ok, files.WriteText("notes.md", "x", overwrite: true).Outcome);
        }
    }

    /// <summary>
    /// With the working directory at the home or a profile folder, every app file is refused (2026-10-23, the admin review's M1:
    /// <c>configs\services.json</c> with <c>control: ["*"]</c> was the model's to write); its <c>files</c> folder still writes.
    /// </summary>
    [Fact]
    public void AWorkingDirectoryAtTheHomeOrAProfile_RefusesTheAppsFiles()
    {
        using var _ = WriteGuard.Configure(Facts(home: _dir));
        string profile = Path.Combine(_dir, "profiles", "neon");
        Directory.CreateDirectory(profile);
        foreach (string root in new[] { _dir, profile })
        {
            var files = new WorkingDirectory(() => root, new ManualTimeProvider());
            foreach (string name in new[] { Path.Combine("configs", "services.json"), "profile.json", "persona.md", "notes.md" })
            {
                Assert.Equal(FileOutcome.AppFilesProtected, files.WriteText(name, "{}", overwrite: true).Outcome);
            }
        }

        var atProfile = new WorkingDirectory(() => profile, new ManualTimeProvider());
        Assert.Equal(FileOutcome.Ok, atProfile.WriteText(Path.Combine("files", "notes.md"), "x", overwrite: true).Outcome);
    }

    /// <summary>
    /// The home is known by a <c>.neonsidekick</c> part however the rest is spelled (2026-10-23, the admin review's M2: a share or a mapped
    /// drive's spelling of the home passed), and by its identity: the folder itself found again walking up from a path.
    /// </summary>
    [Fact]
    public void TheHome_IsKnownByItsFolderName_AndByItsIdentity()
    {
        using var _ = WriteGuard.Configure(Facts(home: Path.Combine(_dir, "elsewhere")));
        Assert.Equal(FileOutcome.AppFilesProtected, Write(Path.Combine("share", ".NeonSidekick", "configs", "tasks.json")));
        Assert.Equal(FileOutcome.Ok, Write(Path.Combine("share", "neonsidekick", "notes.md")));
        Assert.True(WriteGuard.AppWritable(Path.Combine("profiles", "neon", "files", "sub")));
        Assert.False(WriteGuard.AppWritable(Path.Combine("profiles", "neon", "configs", "mcp.json")));
        Assert.False(WriteGuard.AppWritable(Path.Combine("profiles", "neon", "files")));
        Assert.False(WriteGuard.AppWritable(""));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            string deep = Path.Combine(_root, "a", "b");
            Directory.CreateDirectory(deep);
            var id = FileIdentity.Of(_root);
            Assert.NotNull(id);
            Assert.Equal(_root, FileIdentity.AncestorWith(Path.Combine(deep, "missing.txt"), id.Value));
            Assert.Null(FileIdentity.AncestorWith(Path.Combine(_dir, "x.txt"), id.Value));
        }
    }

    [Fact]
    public void ASkillRoot_UnderTheWorkingDirectory_IsRefused()
    {
        string skills = Path.Combine(_root, ".agents", "skills");
        using var _ = WriteGuard.Configure(Facts(skills: [skills]));
        Assert.Equal(FileOutcome.SkillFilesProtected, Write(Path.Combine(".agents", "skills", "x", "SKILL.md")));   // review P3
        Assert.Equal(FileOutcome.Ok, Write(Path.Combine(".agents", "notes.md")));
    }

    [Fact]
    public void ADatabase_IsKnownByItsHeader_AndItsNewExtensions()
    {
        File.WriteAllBytes(Path.Combine(_root, "data.bin"), [.. "SQLite format 3\0"u8, 0, 0, 0]);
        using var _ = WriteGuard.Configure(Facts(databases: DatabaseGuard.ByExtension));
        Assert.Equal(FileOutcome.DatabaseProtected, _files.WriteBytes("data.bin", [1, 2], overwrite: true).Outcome);   // review I9
        Assert.Equal(FileOutcome.DatabaseProtected, Write("x.s3db"));
        Assert.Equal(FileOutcome.Ok, Write("plain.bin"));
    }

    /// <summary>The project file asks (review P1, D2): refused with the slot filled, written inside the one-call allow, an ordinary file elsewhere.</summary>
    [Fact]
    public void TheProjectFile_Asks_AndIsWrittenOnlyWhenAllowed()
    {
        using var _ = WriteGuard.Configure(Facts(cwd: _root));
        var slot = new ProjectFileSlot();
        using (WriteGuard.Watch(slot))
        {
            Assert.Equal(FileOutcome.ProjectFileAsk, Write("AGENTS.md"));
        }

        Assert.Equal(Path.Combine(_root, "AGENTS.md"), slot.Path);
        Assert.Equal(FileOutcome.ProjectFileAsk, Write("neon.md"));
        using (WriteGuard.AllowProjectFile())
        {
            Assert.Equal(FileOutcome.Ok, Write("CLAUDE.md"));
        }

        Assert.Equal(FileOutcome.Ok, Write(Path.Combine("sub", "AGENTS.md")));
    }

    /// <summary>Unset (a screen test, a check mode): the path's own rules hold, the facts' stay off.</summary>
    [Fact]
    public void Unset_KeepsThePathsRules_AndNoneOfTheFacts()
    {
        using var _ = WriteGuard.Configure(null);
        Assert.Equal(FileOutcome.Ok, Write("AGENTS.md"));
        Assert.Equal(FileOutcome.GitProtected, Write(Path.Combine(".git", "x")));
    }

    // ── path forms ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("~/notes.md")]
    [InlineData("~")]
    [InlineData("file:///C:/x.md")]
    [InlineData("FILE:notes.md")]
    public void HomeAndUrlForms_AreNoPathsHere(string path)
    {
        Assert.Equal(FileOutcome.NotAPathHere, Write(path));   // review S5
        Assert.False(Directory.Exists(Path.Combine(_root, "~")));
    }

    [WindowsFact]
    public void StreamsAndDevices_AreRefusedByName()
    {
        Assert.Equal(FileOutcome.StreamName, Write("a.txt:payload"));   // review M2
        Assert.Equal(FileOutcome.StreamName, _files.Resolve("setup.exe:Zone.Identifier", forWrite: false, out _));
        Assert.Equal(FileOutcome.DeviceName, Write("nul"));   // review O10
        Assert.Equal(FileOutcome.DeviceName, Write(Path.Combine("x", "COM1.txt")));
        Assert.Equal(FileOutcome.Ok, Write("console.txt"));
    }

    /// <summary>An 8.3 name is judged as its long name (review G2/G3); skipped where the volume makes none.</summary>
    [WindowsFact]
    public void AShortName_IsJudgedAsItsLongName()
    {
        using var _ = WriteGuard.Configure(null);
        string git = Path.Combine(_root, "repo", ".git");
        Directory.CreateDirectory(git);
        // The volume's short name for ".git" is "GIT~1" where it makes 8.3 names at all.
        if (!Directory.Exists(Path.Combine(_root, "repo", "GIT~1")))
        {
            return;   // no 8.3 names on this volume
        }

        Assert.Equal(FileOutcome.GitProtected, _files.Delete(Path.Combine("repo", "GIT~1")).Outcome);
        Assert.Equal(FileOutcome.GitProtected, Write(Path.Combine("repo", "GIT~1", "config")));
        Assert.True(Directory.Exists(git));
    }

    /// <summary>A working directory seen and then deleted is never made again (review S6).</summary>
    [Fact]
    public void AVanishedRoot_IsNeverMadeAgain()
    {
        Assert.Equal(FileOutcome.Ok, Write("a.md"));
        Directory.Delete(_root, recursive: true);
        Assert.Equal(FileOutcome.RootGone, Write("b.md"));
        Assert.False(Directory.Exists(_root));
    }

    // ── roots, case, links ───────────────────────────────────────────────────

    /// <summary>A root that ends in a separator (a drive's) holds what starts with it (review I6); a name that only shares a prefix is outside.</summary>
    [WindowsFact]
    public void ADriveRoot_HoldsItsPaths()
    {
        Assert.True(WorkingDirectory.IsInside(@"E:\", @"E:\x\y.txt"));
        Assert.Equal(@"x\y.txt", WorkingDirectory.Under(@"E:\", @"E:\x\y.txt"));
        Assert.Equal(@"x\y.txt", WorkingDirectory.Under(@"C:\a", @"C:\a\x\y.txt"));
        Assert.False(WorkingDirectory.IsInside(@"C:\a", @"C:\ab"));
        Assert.Equal("", WorkingDirectory.Under(@"C:\a", @"C:\a"));
    }

    [UnixFact]
    public void TheFileSystemRoot_HoldsItsPaths_Unix()
    {
        Assert.True(WorkingDirectory.IsInside("/", "/x/y.txt"));
        Assert.Equal("x/y.txt", WorkingDirectory.Under("/", "/x/y.txt"));
    }

    /// <summary>A case-sensitive folder's names differ by case (review G16): ../FILES is not inside "files" there.</summary>
    [Fact]
    public void ACaseSensitiveRoot_KeepsItsCase()
    {
        string files = Path.Combine(_dir, "files");
        string upper = Path.Combine(_dir, "FILES", "a.txt");
        using (CaseRule.Override(_ => true))
        {
            Assert.False(WorkingDirectory.IsInside(files, upper));
        }

        using (CaseRule.Override(_ => false))
        {
            Assert.True(WorkingDirectory.IsInside(files, upper));
        }
    }

    /// <summary>The links asked again just before a write (review G13): a folder swapped for a junction since is refused.</summary>
    [WindowsFact]
    public void ALinkSwappedIn_IsCaughtBeforeTheWrite()
    {
        string outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        Assert.Equal(FileOutcome.Ok, _files.Resolve(Path.Combine("out", "a.txt"), forWrite: true, out string full));
        Directory.Delete(Path.Combine(_root, "out"));
        Junction.Make(Path.Combine(_root, "out"), outside);
        try
        {
            Assert.Throws<IOException>(() => _files.RecheckLinks(full));
        }
        finally
        {
            Junction.DeleteTree(_root);
        }
    }

    /// <summary>A cloud-only file is listed but never read by a search, which says how many it left (review O1).</summary>
    [Fact]
    public void ACloudOnlyFile_IsLeftUnreadBySearch_AndCounted()
    {
        File.WriteAllText(Path.Combine(_root, "here.txt"), "needle");
        File.WriteAllText(Path.Combine(_root, "cloud.txt"), "needle");
        WorkingDirectory.OfflineOverride = path => path.EndsWith("cloud.txt", StringComparison.Ordinal);
        try
        {
            var result = _files.Search("needle", "", null, false, CancellationToken.None);
            Assert.Equal(1, result.CloudOnly);
            Assert.Single(result.Hits);
            Assert.Contains("1 file left unread: kept only in the cloud", FileText.SearchHits(result, "needle"), StringComparison.Ordinal);
        }
        finally
        {
            WorkingDirectory.OfflineOverride = null;
        }
    }

    /// <summary>A folder delete's walk reads the turn's token (review P9).</summary>
    [Fact]
    public void AFoldersDelete_StopsOnTheToken()
    {
        Directory.CreateDirectory(Path.Combine(_root, "big", "a"));
        File.WriteAllText(Path.Combine(_root, "big", "a", "x.txt"), "x");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => _files.Delete("big", cancelled.Token));
        Assert.True(Directory.Exists(Path.Combine(_root, "big")));
    }

    // ── the vault ────────────────────────────────────────────────────────────

    /// <summary>The vault's writes are judged too: a link in it leading outside (review H5), a database attachment (J14).</summary>
    [WindowsFact]
    public void TheVault_IsJudgedByTheFileItReaches()
    {
        string vault = Path.Combine(_dir, "vault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
        string outside = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(outside);
        Junction.Make(Path.Combine(vault, "away"), outside);
        File.WriteAllBytes(Path.Combine(vault, "app.db"), [1, 2, 3]);
        using var _ = WriteGuard.Configure(Facts(databases: DatabaseGuard.ByExtension));
        var notes = new NeonSidekick.Obsidian.ObsidianVault(() => vault, new ManualTimeProvider());
        try
        {
            Assert.Equal(NeonSidekick.Obsidian.ObsidianText.OutsideVault("away/x.md"), notes.Write("away/x", "secret", NeonSidekick.Obsidian.VaultWriteMode.Create, ""));
            Assert.Empty(Directory.GetFiles(outside));
            Assert.Equal(FileText.DatabaseProtected("app.db"), notes.Delete("app.db"));
            Assert.True(File.Exists(Path.Combine(vault, "app.db")));
        }
        finally
        {
            Junction.DeleteTree(vault);
        }
    }

    // ── the turn's ask ───────────────────────────────────────────────────────

    /// <summary>
    /// The turn asks about the project file only while it offers ask_user: yes runs the call again with the file allowed, no answers the
    /// declined sentence, nobody to ask (null) keeps the refusal.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(null, true)]
    [InlineData(true, false)]
    public async Task TheTurn_AsksAboutTheProjectFile(bool? answer, bool offersAskUser)
    {
        using var _ = WriteGuard.Configure(Facts(cwd: _root));
        var tools = new List<AIFunction> { new WriteFileTool(_files) };
        if (offersAskUser)
        {
            tools.Add(new AskUserTool((_, _) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null), () => new AppSettingsData()));
        }

        var client = new FakeChatClient();
        var assistant = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)), tools);
        int asked = 0;
        assistant.ProjectFileAsk = (_, _) => { asked++; return Task.FromResult(answer); };
        client.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "AGENTS.md", ["content"] = "rules", ["mode"] = "create" }));
        client.EnqueueText("done");

        await foreach (var _e in assistant.RunTurnAsync("write it")) { }

        string result = client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result?.ToString() ?? "";
        Assert.Equal(offersAskUser ? 1 : 0, asked);
        if (answer == true && offersAskUser)
        {
            Assert.Equal("rules", File.ReadAllText(Path.Combine(_root, "AGENTS.md")));
        }
        else
        {
            Assert.False(File.Exists(Path.Combine(_root, "AGENTS.md")));
            Assert.Equal(answer == false && offersAskUser ? FileText.ProjectFileDeclined("AGENTS.md") : FileText.ProjectFileRefused("AGENTS.md"), result);
        }
    }
}
