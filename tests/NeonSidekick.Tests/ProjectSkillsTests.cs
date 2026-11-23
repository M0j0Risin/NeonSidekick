using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Shell;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// A repo's own skills (2026-11-23, feature idea #16): the walk from the working folder up to the repo root, the per-folder trust and its file, the
/// catalog's offered and pending lists, <c>load_skill</c>'s checks at the load, the editor's refusals, the pane's words and the shell's tripwire.
/// </summary>
public class ProjectSkillsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly SkillRoots _roots;
    private readonly SkillCatalog _catalog;
    private readonly ProjectSkillTrust _trust;
    private readonly string _repo;
    private string _working;

    public ProjectSkillsTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, "home", ".agents", "skills"));
        _catalog = new SkillCatalog(() => _roots);
        _trust = new ProjectSkillTrust(() => Path.Combine(_dir, "profile", "configs"), _time);
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        _working = _repo;
        Directory.CreateDirectory(Path.Combine(_dir, "home"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ProjectSkillSource Source => new(() => _working, Path.Combine(_dir, "home"), null, _trust);

    private void Scan(bool refresh = true) => _catalog.Scan(new SkillScan(false, Source, refresh));

    private static string Skill(string name, string description = "Does things.", string extra = "") =>
        "---\nname: " + name + "\ndescription: " + description + "\n" + extra + "---\n# " + name + "\n\nThe steps.\n";

    /// <summary>A skill under <paramref name="level"/>'s <c>.agents/skills</c> (or <c>.claude/skills</c>).</summary>
    private static string PutProject(string level, string folder, string text, bool claude = false)
    {
        string directory = Path.Combine(level, claude ? ".claude" : ".agents", "skills", folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SkillCatalog.FileName), text);
        return directory;
    }

    private void PutOwn(SkillScope scope, string name)
    {
        string directory = Path.Combine(_roots.Of(scope), name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SkillCatalog.FileName), Skill(name, "Yours."));
    }

    /// <summary>Trusts <paramref name="level"/> as the question would: what it reads now.</summary>
    private void TrustFolder(string level)
    {
        string bound = ProjectSkillWalk.RepoRoot(_working, Path.Combine(_dir, "home"), null) ?? _working;
        var state = _catalog.ReadProjectFolder(level, bound);
        Assert.Equal(ProjectTrustSave.Saved, _trust.Trust(level, state.Fingerprint, () => _catalog.ReadProjectFolder(level, bound)));
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values) =>
        new(values.ToDictionary(v => v.Name, v => v.Value));

    // ── The walk ──

    [Fact]
    public void Walk_FromASubfolder_ReadsEachLevelUpToTheRepoRoot_NearestFirst_AgentsBeforeClaude()
    {
        string sub = Path.Combine(_repo, "src", "app");
        Directory.CreateDirectory(sub);

        var levels = ProjectSkillWalk.Levels(sub, Path.Combine(_dir, "home"), null);

        Assert.Equal([sub, sub, Path.Combine(_repo, "src"), Path.Combine(_repo, "src"), _repo, _repo], levels.Select(l => l.Level));
        Assert.Equal([false, true, false, true, false, true], levels.Select(l => l.Claude));
        Assert.Equal(Path.Combine(_repo, ".agents", "skills"), levels[4].SkillsFolder);
        Assert.Equal(_repo, ProjectSkillWalk.RepoRoot(sub, Path.Combine(_dir, "home"), null));
    }

    [Fact]
    public void Walk_OutsideARepo_ReadsTheWorkingFolderAlone()
    {
        string plain = Path.Combine(_dir, "plain", "folder");
        Directory.CreateDirectory(plain);

        Assert.Equal([plain], ProjectSkillWalk.LevelFolders(plain, Path.Combine(_dir, "home"), null));
    }

    [Fact]
    public void Walk_AGitInTheUserHome_IsIgnored_TheWorkingFolderAlone()
    {
        string home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(Path.Combine(home, ".git"));
        string project = Path.Combine(home, "notes");
        Directory.CreateDirectory(project);

        Assert.Null(ProjectSkillWalk.RepoRoot(project, home, null));
        Assert.Equal([project], ProjectSkillWalk.LevelFolders(project, home, null));
    }

    [Fact]
    public void Walk_TheAppHomeKeptInGit_TheDefaultFilesFolderReadsOnlyItself()
    {
        string app = Path.Combine(_dir, "apphome");
        Directory.CreateDirectory(Path.Combine(app, ".git"));
        string files = Path.Combine(app, "profiles", "default", "files");
        Directory.CreateDirectory(files);

        Assert.Null(ProjectSkillWalk.RepoRoot(files, Path.Combine(_dir, "home"), app));
        Assert.Equal([files], ProjectSkillWalk.LevelFolders(files, Path.Combine(_dir, "home"), app));
    }

    [Fact]
    public void Walk_AWorktreesGitfile_CountsAsTheRepoRoot()
    {
        string worktree = Path.Combine(_dir, "worktree");
        Directory.CreateDirectory(Path.Combine(worktree, "sub"));
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(_repo, ".git", "worktrees", "w") + "\n");

        Assert.Equal(worktree, ProjectSkillWalk.RepoRoot(Path.Combine(worktree, "sub"), Path.Combine(_dir, "home"), null));
    }

    /// <summary>
    /// The scope page's <c>project</c> row's level (2026-11-23, the user's ask): the working folder when the walk reads it first (a repo's subfolder, a plain
    /// folder, the app home's default files folder); none at the user's home or the app's, where the walk skips it, in a repo or out of one.
    /// </summary>
    [Fact]
    public void MoveTarget_IsTheWorkingFolder_WhenTheWalkReadsIt_NeverAHome()
    {
        string home = Path.Combine(_dir, "home");
        string sub = Path.Combine(_repo, "src");
        Directory.CreateDirectory(sub);
        string app = Path.Combine(_dir, "apphome");
        string files = Path.Combine(app, "profiles", "default", "files");
        Directory.CreateDirectory(files);

        Assert.Equal(sub, ProjectSkillWalk.MoveTarget(sub, home, app));
        Assert.Equal(_repo, ProjectSkillWalk.MoveTarget(_repo + Path.DirectorySeparatorChar, home, app));
        Assert.Equal(files, ProjectSkillWalk.MoveTarget(files, home, app));
        Assert.Null(ProjectSkillWalk.MoveTarget(home, home, app));   // outside a repo: the walk's list is empty
        Assert.Null(ProjectSkillWalk.MoveTarget(app, home, app));
        Directory.CreateDirectory(Path.Combine(app, ".git"));
        Assert.Null(ProjectSkillWalk.MoveTarget(app, home, app));    // a home kept in git: still skipped
    }

    /// <summary>
    /// The after-check a move or delete trusts by (2026-11-23): the same folder but for the one skill passes; another skill changed, added (hashed, or past
    /// the cap in the order alone) or removed does not.
    /// </summary>
    [Fact]
    public void SameButFor_PassesTheOneSkillAlone()
    {
        const string key = ".agents/skills/moved";
        var before = new ProjectFolderState("f1", new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, new Dictionary<string, string>(), ["a", "b"]);
        var withMoved = new ProjectFolderState("f2", new Dictionary<string, string> { ["a"] = "1", [key] = "9", ["b"] = "2" }, new Dictionary<string, string>(), ["a", key, "b"]);

        Assert.True(SkillsMenu.SameButFor(before, withMoved, key));
        Assert.True(SkillsMenu.SameButFor(withMoved, before, key));   // a delete: the key gone
        Assert.False(SkillsMenu.SameButFor(before, withMoved with { Hashes = new Dictionary<string, string> { ["a"] = "1", [key] = "9", ["b"] = "3" } }, key));
        Assert.False(SkillsMenu.SameButFor(before, withMoved with { Hashes = new Dictionary<string, string> { ["a"] = "1", [key] = "9", ["b"] = "2", ["c"] = "4" }, Keys = ["a", key, "b", "c"] }, key));
        Assert.False(SkillsMenu.SameButFor(before, withMoved with { Keys = ["a", key, "b", "past-the-cap"] }, key));
        Assert.False(SkillsMenu.SameButFor(before, withMoved with { Hashes = new Dictionary<string, string> { ["a"] = "1", [key] = "9" }, Keys = ["a", key] }, key));
    }

    /// <summary>
    /// The editor's move into a repo (2026-11-23): copied without the app's markers, the source gone; a source that won't delete leaves both and answers
    /// Copied with the reason; a locked one is refused, nothing copied.
    /// </summary>
    [Fact]
    public void MoveToProject_Copies_DropsTheMarkers_DeletesTheSource_OrKeepsBothWhenItWontGo()
    {
        PutOwn(SkillScope.Profile, "haiku");
        string source = Path.Combine(_roots.Profile, "haiku");
        File.WriteAllText(Path.Combine(source, SkillProvenance.FileName), "{}");
        string destination = Path.Combine(_repo, ".agents", "skills");
        _catalog.Scan(false);

        var moved = SkillEditor.MoveToProject(_roots, _catalog.Find("haiku")!, destination);

        Assert.Equal(SkillEditOutcome.Moved, moved.Outcome);
        Assert.Equal(SkillScope.Project, moved.Scope);
        Assert.True(File.Exists(Path.Combine(destination, "haiku", SkillCatalog.FileName)));
        Assert.False(File.Exists(Path.Combine(destination, "haiku", SkillProvenance.FileName)));
        Assert.False(Directory.Exists(source));

        PutOwn(SkillScope.Global, "pdf");
        string global = Path.Combine(_roots.Global, "pdf");
        _catalog.Scan(false);
        var pdf = _catalog.Find("pdf")!;
        SkillEditResult kept;
        if (OperatingSystem.IsWindows())
        {
            // A file held open without delete sharing: the copy reads it, the delete can't take it.
            using var held = new FileStream(Path.Combine(global, SkillCatalog.FileName), FileMode.Open, FileAccess.Read, FileShare.Read);
            kept = SkillEditor.MoveToProject(_roots, pdf, destination);
        }
        else
        {
            File.SetUnixFileMode(_roots.Global, UnixFileMode.UserRead | UnixFileMode.UserExecute);   // the folder can't lose an entry
            try
            {
                kept = SkillEditor.MoveToProject(_roots, pdf, destination);
            }
            finally
            {
                File.SetUnixFileMode(_roots.Global, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Assert.Equal(SkillEditOutcome.Copied, kept.Outcome);
        Assert.NotEmpty(kept.Detail);
        Assert.True(File.Exists(Path.Combine(destination, "pdf", SkillCatalog.FileName)));
        Assert.True(File.Exists(Path.Combine(global, SkillCatalog.FileName)));

        PutOwn(SkillScope.Profile, "locked");
        SkillLock.Set(Path.Combine(_roots.Profile, "locked"), true, _time.GetUtcNow());
        _catalog.Scan(false);
        Assert.Equal(SkillEditOutcome.Locked, SkillEditor.MoveToProject(_roots, _catalog.Find("locked")!, destination).Outcome);
        Assert.False(Directory.Exists(Path.Combine(destination, "locked")));
    }

    // ── The catalog ──

    [Fact]
    public void Untrusted_ProjectSkills_AreListedPending_NeverOffered()
    {
        PutProject(_repo, "deploy", Skill("deploy"));

        Scan();

        Assert.Empty(_catalog.Skills);
        Assert.Null(_catalog.Find("deploy"));
        var pending = Assert.Single(_catalog.ProjectPending);
        Assert.Equal(ProjectPendingReason.NotTrusted, pending.Reason);
        Assert.Equal(SkillScope.Project, pending.Skill.Scope);
        Assert.Same(pending, _catalog.FindPending("deploy"));
        var folder = Assert.Single(_catalog.ProjectFolders);
        Assert.Equal(ProjectFolderTrust.Untrusted, folder.Trust);
        Assert.Empty(_catalog.TrustedProjectFolders);
    }

    [Fact]
    public void Trusted_ProjectSkills_AreOffered_RankedLast_YoursWinAClash()
    {
        PutOwn(SkillScope.Profile, "deploy");
        PutProject(_repo, "deploy", Skill("deploy", "The repo's."));
        PutProject(_repo, "lint", Skill("lint"));
        Scan();
        TrustFolder(_repo);

        Scan();

        Assert.Equal(["deploy", "lint"], _catalog.Skills.Select(s => s.Name));
        Assert.Equal(SkillScope.Profile, _catalog.Find("deploy")!.Scope);
        Assert.Equal(SkillScope.Project, _catalog.Find("lint")!.Scope);
        var hidden = Assert.Single(_catalog.Shadowed);
        Assert.Equal("The repo's.", hidden.Description);
        Assert.Equal(SkillScope.Profile, hidden.ShadowedBy);
        Assert.Equal([Path.Combine(_repo, ".agents", "skills")], _catalog.TrustedProjectFolders);
    }

    [Fact]
    public void AnUntrustedSubfoldersSkill_NeverHidesTheTrustedRootsOne_AndOnceTrusted_TheNearerWins()
    {
        string sub = Path.Combine(_repo, "sub");
        PutProject(_repo, "x", Skill("x", "The root's."));
        PutProject(sub, "x", Skill("x", "The subfolder's."));
        _working = sub;
        Scan();
        TrustFolder(_repo);

        Scan();
        Assert.Equal("The root's.", _catalog.Find("x")!.Description);

        TrustFolder(sub);
        Scan();
        Assert.Equal("The subfolder's.", _catalog.Find("x")!.Description);
        Assert.Equal("The root's.", Assert.Single(_catalog.Shadowed).Description);
    }

    [Fact]
    public void AnEditAfterTheTrust_DropsTheFolderToChanged_AndOffersNothing()
    {
        string directory = PutProject(_repo, "deploy", Skill("deploy"));
        Scan();
        TrustFolder(_repo);
        Scan();
        Assert.NotNull(_catalog.Find("deploy"));

        File.WriteAllText(Path.Combine(directory, SkillCatalog.FileName), Skill("deploy", "Now it runs rm -rf."));
        Scan();

        Assert.Null(_catalog.Find("deploy"));
        Assert.Equal(ProjectFolderTrust.Changed, Assert.Single(_catalog.ProjectFolders).Trust);
        Assert.Equal(ProjectPendingReason.NotTrusted, Assert.Single(_catalog.ProjectPending).Reason);
    }

    [Fact]
    public void AClaudeSkill_WithNoFrontmatter_IsOfferedUnderItsFolderName_ItsFirstLineTheDescription()
    {
        PutProject(_repo, "release-notes", "# Write the release notes\n\nRead the log, then write them.\n", claude: true);
        PutProject(_repo, "bare", "Just text.\n");   // .agents keeps the standard's rule
        Scan();
        TrustFolder(_repo);

        Scan();

        var skill = _catalog.Find("release-notes");
        Assert.NotNull(skill);
        Assert.Equal("Write the release notes", skill.Description);
        Assert.True(skill.ClaudeRules);
        Assert.Null(_catalog.Find("bare"));
        Assert.Contains(_catalog.Problems, p => p.Directory.EndsWith("bare", StringComparison.Ordinal));
    }

    [Fact]
    public void AClaudeSkill_WhenToUse_JoinsItsDescription_AndAManualOnlyOne_IsNotOffered()
    {
        PutProject(_repo, "a", Skill("a", "Short.", "when_to_use: When the build breaks.\n"), claude: true);
        PutProject(_repo, "b", Skill("b", "By hand.", "disable-model-invocation: true\n"), claude: true);
        Scan();
        TrustFolder(_repo);

        Scan();

        Assert.Equal("Short. When the build breaks.", _catalog.Find("a")!.Description);
        Assert.Null(_catalog.Find("b"));
        Assert.Equal(ProjectPendingReason.ManualOnly, _catalog.FindPending("b")!.Reason);
    }

    [Fact]
    public void Past50_TheRestAreListedOverTheCap()
    {
        for (int i = 0; i < 60; i++)
        {
            PutProject(_repo, "s" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), Skill("s" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)));
        }

        Scan();
        TrustFolder(_repo);
        Scan();

        Assert.Equal(ProjectSkillTrust.ProjectSkillCap, _catalog.Skills.Count);

        // One folder's 51st on are past its own first 50 (review #70), not the repo-wide count.
        Assert.Equal(10, _catalog.ProjectPending.Count(p => p.Reason == ProjectPendingReason.PastFolderCap));
        Assert.DoesNotContain(_catalog.ProjectPending, p => p.Reason == ProjectPendingReason.OverCap);
    }

    [Fact]
    public void Past50AcrossTheRepo_TheRestAreOverTheCap()
    {
        string sub = Path.Combine(_repo, "sub");
        _working = sub;
        for (int i = 0; i < 30; i++)
        {
            string n = i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            PutProject(sub, "near" + n, Skill("near" + n));
            PutProject(_repo, "far" + n, Skill("far" + n));
        }

        Scan();
        TrustFolder(sub);
        TrustFolder(_repo);
        Scan();

        Assert.Equal(ProjectSkillTrust.ProjectSkillCap, _catalog.Skills.Count);
        Assert.Equal(10, _catalog.ProjectPending.Count(p => p.Reason == ProjectPendingReason.OverCap));
    }

    /// <summary>Review #70: 52 folders at one level with 3 of the first 50 unparseable: 47 offered, 51-52 past the folder's cap, none over the repo's.</summary>
    [Fact]
    public void FiftyTwoFolders_ThreeUnparseable_47Offered_TheLastTwoPastTheFolderCap()
    {
        for (int i = 0; i < 52; i++)
        {
            string n = "s" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            PutProject(_repo, n, i is 3 or 9 or 27 ? "no frontmatter here\n" : Skill(n));
        }

        Scan();
        TrustFolder(_repo);
        Scan();

        Assert.Equal(47, _catalog.Skills.Count);
        Assert.Equal(["s50", "s51"], _catalog.ProjectPending.Where(p => p.Reason == ProjectPendingReason.PastFolderCap).Select(p => p.Skill.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(_catalog.ProjectPending, p => p.Reason == ProjectPendingReason.OverCap);
    }

    [Fact]
    public void ARepoSkillsLockAndDisableMarkers_MeanNothing()
    {
        string directory = PutProject(_repo, "deploy", Skill("deploy"));
        File.WriteAllText(Path.Combine(directory, SkillLock.FileName), "{}");
        File.WriteAllText(Path.Combine(directory, SkillDisable.FileName), "{}");
        Scan();
        TrustFolder(_repo);

        Scan();

        var skill = _catalog.Find("deploy");
        Assert.NotNull(skill);
        Assert.False(skill.Locked);
        Assert.False(skill.Disabled);
        Assert.DoesNotContain(SkillsPrompt.LockedTag, SkillsPrompt.Catalog(_catalog.Skills));
        Assert.Contains(SkillsPrompt.ProjectTag, SkillsPrompt.Catalog(_catalog.Skills));
    }

    [Fact]
    public void ADisable_IsKeptInTheProfile_AndSurvivesAnUntrustAndATrust()
    {
        PutProject(_repo, "deploy", Skill("deploy"));
        Scan();
        TrustFolder(_repo);

        _trust.SetDisabled(_repo, "deploy", disabled: true);
        _trust.Untrust(_repo);
        TrustFolder(_repo);
        Scan();

        Assert.Null(_catalog.Find("deploy"));
        Assert.True(Assert.Single(_catalog.All).Disabled);
        Assert.False(File.Exists(Path.Combine(_repo, ".agents", "skills", "deploy", SkillDisable.FileName)));
    }

    [Fact]
    public void ASkillMdOver1Mb_IsAProblem_NeverRead()
    {
        string directory = PutProject(_repo, "huge", Skill("huge") + new string('x', 2 * 1024 * 1024));

        Scan();

        Assert.Empty(_catalog.ProjectPending);
        Assert.Contains(_catalog.Problems, p => p.Directory == directory && p.Reason.Contains("too large to read", StringComparison.Ordinal));
    }

    [Fact]
    public void ALongName_IsPending_AndTheRowCutsIt()
    {
        string name = new('a', 200);
        PutProject(_repo, "long", Skill(name));
        Scan();
        TrustFolder(_repo);

        Scan();

        Assert.Equal(ProjectPendingReason.BadName, Assert.Single(_catalog.ProjectPending).Reason);
        Assert.Equal(new string('a', 64) + "…", SkillsText.ShownName(name));
    }

    /// <summary>Review #45: a right-to-left override (a format character, no control) makes the name pending, and the row drops it.</summary>
    [Fact]
    public void ANameWithAnRtlOverride_IsBadName_AndTheRowDropsIt()
    {
        string name = "deploy\u202Eexe.sh";
        PutProject(_repo, "rtl", Skill(name));
        Scan();
        TrustFolder(_repo);

        Scan();

        Assert.Equal(ProjectPendingReason.BadName, Assert.Single(_catalog.ProjectPending).Reason);
        Assert.Equal("deployexe.sh…", SkillsText.ShownName(name));
        Assert.False(SkillCatalog.IsBoundedName("a\u2028b"));
    }

    /// <summary>Review #60: each file is framed by its length, so bytes moved across a file's edge hash differently.</summary>
    [Fact]
    public void ShiftedByteLayouts_HashDifferently()
    {
        string one = PutProject(_repo, "one", Skill("x"));
        File.WriteAllText(Path.Combine(one, "a.md"), "x");
        File.WriteAllText(Path.Combine(one, "b.md"), "y");
        string two = PutProject(_repo, "two", Skill("x"));
        File.WriteAllText(Path.Combine(two, "a.md"), "xb.md\0y");

        Assert.NotEqual(_catalog.ProjectSkillHash(one), _catalog.ProjectSkillHash(two));
    }

    /// <summary>Review #40/#25b: a same-size edit with its write time put back is refused at the load; the stamp cache never answers the load's check.</summary>
    [Fact]
    public void ASameSizeEdit_WithTheWriteTimePutBack_IsRefusedAtTheLoad()
    {
        string directory = PutProject(_repo, "deploy", Skill("deploy", "Does AAAA."));
        Scan();
        TrustFolder(_repo);
        Scan();
        var skill = _catalog.Find("deploy")!;
        string file = Path.Combine(directory, SkillCatalog.FileName);
        var written = File.GetLastWriteTimeUtc(file);

        File.WriteAllText(file, Skill("deploy", "Does BBBB."));
        File.SetLastWriteTimeUtc(file, written);

        Assert.False(_catalog.StillTrusted(skill));
    }

    /// <summary>Review #25b: a SKILL.md past the old 128 KB budget is hashed by its bytes, so a touch alone changes nothing, even on a cold cache.</summary>
    [Fact]
    public void ALargeSkillMd_OnlyTouched_HashesTheSame_OnAColdCache()
    {
        string directory = PutProject(_repo, "big", Skill("big") + new string('x', 200 * 1024));
        string before = _catalog.ProjectSkillHash(directory);
        File.SetLastWriteTimeUtc(Path.Combine(directory, SkillCatalog.FileName), DateTime.UtcNow.AddHours(1));

        Assert.Equal(before, new SkillCatalog(() => _roots).ProjectSkillHash(directory));
    }

    /// <summary>Review #54: Retain keeps only the folders it's given.</summary>
    [Fact]
    public void Retain_KeepsOnlyTheFoldersGiven()
    {
        var hasher = new ProjectSkillHasher();
        string a = PutProject(_repo, "a", Skill("a"));
        string b = PutProject(_repo, "b", Skill("b"));
        hasher.SkillHash(a);
        hasher.SkillHash(b);
        hasher.SkillHash(b, fresh: true);

        hasher.Retain([a]);

        Assert.Equal([a], hasher.CachedFolders);
    }

    /// <summary>Review #46: a project SKILL.md stays in the parse cache, so a second scan logs no new "read from" line.</summary>
    [Fact]
    public void ASecondScan_LogsNoNewReadFromLine()
    {
        PutProject(_repo, "deploy", Skill("deploy"));
        Scan();
        var lines = new List<string>();
        void Capture(NeonSidekick.Diagnostics.DiagnosticEvent e) => lines.Add(e.Message);
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += Capture;
        try
        {
            Scan();
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= Capture;
        }

        Assert.DoesNotContain(lines, l => l.Contains("read from", StringComparison.Ordinal));
    }

    /// <summary>Review #73: a SKILL.md that is a link, even to a file inside the repo, is listed and never offered.</summary>
    [SymlinkFact]
    public void ALinkedSkillMd_IsPending_NeverOffered()
    {
        string real = Path.Combine(_repo, "docs");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "deploy.md"), Skill("deploy"));
        string directory = Path.Combine(_repo, ".agents", "skills", "deploy");
        Directory.CreateDirectory(directory);
        File.CreateSymbolicLink(Path.Combine(directory, SkillCatalog.FileName), Path.Combine(real, "deploy.md"));
        Scan();
        TrustFolder(_repo);

        Scan();

        Assert.Null(_catalog.Find("deploy"));
        Assert.Equal(ProjectPendingReason.LinkedFile, Assert.Single(_catalog.ProjectPending).Reason);
    }

    /// <summary>Review #84: on a case-sensitive volume, REF.md is another file than the listed ref.md.</summary>
    [Fact]
    public void ReadResource_OnACaseSensitiveVolume_RefusesAnotherCaseOfAListedFile()
    {
        string directory = PutProject(_repo, "deploy", Skill("deploy"));
        File.WriteAllText(Path.Combine(directory, "ref.md"), "the notes");
        Scan();
        TrustFolder(_repo);
        Scan();
        var skill = _catalog.Find("deploy")!;

        using (NeonSidekick.Files.CaseRule.Override(_ => true))
        {
            Assert.Equal(SkillCatalog.ReadOutcome.NotListed, SkillCatalog.ReadResource(skill, "REF.md").Outcome);
            Assert.Equal(SkillCatalog.ReadOutcome.Ok, SkillCatalog.ReadResource(skill, "ref.md").Outcome);
        }
    }

    /// <summary>Review #57: a SKILL.md over the read cap says so by the skill's name, not an empty path.</summary>
    [Fact]
    public void ASkillMdOverTheCap_AtTheLoad_SaysBodyTooBig()
    {
        string directory = PutProject(_repo, "deploy", Skill("deploy") + new string('x', (int)ProjectSkillTrust.MaxSkillFileBytes));
        var skill = new Skill("deploy", "", SkillScope.Project, directory);

        var read = SkillCatalog.ReadBody(skill);

        Assert.Equal(SkillCatalog.ReadOutcome.TooBig, read.Outcome);
        Assert.Equal(SkillText.BodyTooBig("deploy"), SkillText.ReadError(skill, null, read));
    }

    [Fact]
    public void WithNoSource_NoProjectFolderIsRead()
    {
        PutProject(_repo, "deploy", Skill("deploy"));

        _catalog.Scan(new SkillScan(false));

        Assert.Empty(_catalog.ProjectPending);
        Assert.Empty(_catalog.ProjectFolders);
    }

    [Fact]
    public void ACompletionsScan_ReusesTheKeptPart_TheTurnsScanReadsAgain()
    {
        PutProject(_repo, "deploy", Skill("deploy"));
        Scan();
        int hashed = _catalog.HashesComputed;
        PutProject(_repo, "lint", Skill("lint"));

        Scan(refresh: false);
        Assert.Single(_catalog.ProjectPending);
        Assert.Equal(hashed, _catalog.HashesComputed);

        Scan();
        Assert.Equal(2, _catalog.ProjectPending.Count);
    }

    [Fact]
    public void AnUntouchedFolder_IsNotHashedAgain()
    {
        PutProject(_repo, "deploy", Skill("deploy"));
        Scan();
        int hashed = _catalog.HashesComputed;

        Scan();
        Scan();

        Assert.Equal(hashed, _catalog.HashesComputed);
    }

    // ── The trust file ──

    [Fact]
    public void TwoStoresOnOneFile_EachTrustAFolder_AndBothSurvive()
    {
        string other = Path.Combine(_dir, "other");
        PutProject(_repo, "a", Skill("a"));
        PutProject(other, "b", Skill("b"));
        var second = new ProjectSkillTrust(() => Path.Combine(_dir, "profile", "configs"), _time);

        TrustFolder(_repo);
        var state = _catalog.ReadProjectFolder(other, other);
        second.Trust(other, state.Fingerprint, () => _catalog.ReadProjectFolder(other, other));

        Assert.NotNull(_trust.Trusted(_repo));
        Assert.NotNull(_trust.Trusted(other));
        Assert.NotNull(second.Trusted(_repo));
    }

    [Fact]
    public void AFileThatWontParse_IsMovedAside_AndNothingIsTrusted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_trust.FilePath)!);
        File.WriteAllText(_trust.FilePath, "{ not json");

        Assert.Null(_trust.Trusted(_repo));
        Assert.True(File.Exists(_trust.FilePath + ProjectSkillTrust.BadSuffix));
        Assert.False(File.Exists(_trust.FilePath));
    }

    [Fact]
    public void ATrust_OfAFolderChangedSinceTheQuestionRead_SavesNothing()
    {
        string directory = PutProject(_repo, "deploy", Skill("deploy"));
        var shown = _catalog.ReadProjectFolder(_repo, _repo);
        File.WriteAllText(Path.Combine(directory, SkillCatalog.FileName), Skill("deploy", "Edited after the question opened."));

        Assert.Equal(ProjectTrustSave.Changed, _trust.Trust(_repo, shown.Fingerprint, () => _catalog.ReadProjectFolder(_repo, _repo)));
        Assert.Null(_trust.Trusted(_repo));
    }

    [Fact]
    public void ATrust_KeepsACopyOfEachSkillMd_NamedByHash_AndAnUntrustDeletesThem()
    {
        PutProject(_repo, "deploy", Skill("../../x"));
        TrustFolder(_repo);

        string copies = Path.Combine(_trust.CopiesPath, ProjectSkillTrust.CopiesFolderFor(ProjectSkillTrust.KeyOf(_repo), _trust.Trusted(_repo)!.Fingerprint));
        Assert.True(Directory.Exists(copies));
        Assert.Equal(Skill("../../x"), _trust.CopyOf(_repo, ".agents/skills/deploy"));
        Assert.All(Directory.EnumerateFiles(copies), f => Assert.StartsWith(copies, f, StringComparison.Ordinal));

        _trust.Untrust(_repo);

        Assert.Null(_trust.Trusted(_repo));
        Assert.False(Directory.Exists(copies));
    }

    [Fact]
    public void AnEntryWhoseFolderIsGone_IsPrunedAtTheNextSave()
    {
        string gone = Path.Combine(_dir, "gone");
        PutProject(gone, "a", Skill("a"));
        var state = _catalog.ReadProjectFolder(gone, gone);
        _trust.Trust(gone, state.Fingerprint, () => _catalog.ReadProjectFolder(gone, gone));
        Directory.Delete(gone, recursive: true);

        PutProject(_repo, "b", Skill("b"));
        TrustFolder(_repo);

        Assert.Null(_trust.Trusted(gone));
        Assert.NotNull(_trust.Trusted(_repo));
    }

    /// <summary>Review #7/#68: a re-trust whose save fails leaves the old entry with its own copies, for a Trust and for an Untrust.</summary>
    [Fact]
    public void ASaveThatFails_KeepsTheOldEntrysCopies_ForATrustAndAnUntrust()
    {
        string directory = PutProject(_repo, "deploy", Skill("deploy"));
        TrustFolder(_repo);
        File.WriteAllText(Path.Combine(directory, SkillCatalog.FileName), Skill("deploy", "Edited since."));
        var state = _catalog.ReadProjectFolder(_repo, _repo);
        File.SetAttributes(_trust.FilePath, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<IOException>(() => _trust.Trust(_repo, state.Fingerprint, () => _catalog.ReadProjectFolder(_repo, _repo)));
            Assert.Equal(Skill("deploy"), _trust.CopyOf(_repo, ".agents/skills/deploy"));

            Assert.ThrowsAny<IOException>(() => _trust.Untrust(_repo));
            Assert.Equal(Skill("deploy"), _trust.CopyOf(_repo, ".agents/skills/deploy"));
        }
        finally
        {
            File.SetAttributes(_trust.FilePath, FileAttributes.Normal);
        }

        // The next save that lands takes the new copies and sweeps the old and the failed try's.
        Assert.Equal(ProjectTrustSave.Saved, _trust.Trust(_repo, state.Fingerprint, () => _catalog.ReadProjectFolder(_repo, _repo)));
        Assert.Equal(Skill("deploy", "Edited since."), _trust.CopyOf(_repo, ".agents/skills/deploy"));
        Assert.Single(Directory.EnumerateDirectories(_trust.CopiesPath));
    }

    /// <summary>Review #47: copies are named by the entry's own spelling, so a re-trust through another case still finds them.</summary>
    [Fact]
    public void ARetrust_UnderAnotherCase_KeepsCopyOfWorking()
    {
        using var rule = NeonSidekick.Files.CaseRule.Override(_ => false);
        string directory = PutProject(_repo, "deploy", Skill("deploy"));
        TrustFolder(_repo);
        File.WriteAllText(Path.Combine(directory, SkillCatalog.FileName), Skill("deploy", "Edited since."));
        string other = _repo.ToUpperInvariant();
        var state = _catalog.ReadProjectFolder(_repo, _repo);

        Assert.Equal(ProjectTrustSave.Saved, _trust.Trust(other, state.Fingerprint, () => _catalog.ReadProjectFolder(_repo, _repo)));

        Assert.Equal(Skill("deploy", "Edited since."), _trust.CopyOf(_repo, ".agents/skills/deploy"));
        Assert.Equal(Skill("deploy", "Edited since."), _trust.CopyOf(other, ".agents/skills/deploy"));
    }

    /// <summary>Review #7: copies kept before the per-fingerprint names still give the re-ask its diff, and go once the entry is saved again.</summary>
    [Fact]
    public void CopiesUnderTheBareFolderName_AreReadUntilTheEntryIsSavedAgain()
    {
        string directory = PutProject(_repo, "deploy", Skill("deploy"));
        TrustFolder(_repo);
        string key = ProjectSkillTrust.KeyOf(_repo);
        string named = Path.Combine(_trust.CopiesPath, ProjectSkillTrust.CopiesFolderFor(key, _trust.Trusted(_repo)!.Fingerprint));
        string bare = Path.Combine(_trust.CopiesPath, ProjectSkillTrust.FolderHash(key));
        Directory.Move(named, bare);

        Assert.Equal(Skill("deploy"), _trust.CopyOf(_repo, ".agents/skills/deploy"));

        File.WriteAllText(Path.Combine(directory, SkillCatalog.FileName), Skill("deploy", "Edited since."));
        TrustFolder(_repo);

        Assert.False(Directory.Exists(bare));
        Assert.Equal(Skill("deploy", "Edited since."), _trust.CopyOf(_repo, ".agents/skills/deploy"));
    }

    /// <summary>Review #94/#95: a hand-edited file's null folder, malformed folder, null lists and bad fingerprints are left out; the good entry stays.</summary>
    [Fact]
    public void AFileWithNullsAndBadFingerprints_ReadsSafely_KeepingItsGoodEntries()
    {
        PutProject(_repo, "deploy", Skill("deploy"));
        TrustFolder(_repo);
        string good = File.ReadAllText(_trust.FilePath).Trim();
        string other = Path.Combine(_dir, "other").Replace("\\", "\\\\", StringComparison.Ordinal);
        string json = "[" + good.TrimStart('[').TrimEnd(']') + ","
            + "{\"folder\": null},"
            + "{\"folder\": \"a\\u0000b\"},"
            + "{\"folder\": \"" + other + "\", \"kinds\": null, \"disabled\": null},"
            + "{\"folder\": \"" + other + "x\", \"kinds\": {\"skills\": {\"fingerprint\": null, \"trustedAt\": null, \"skills\": null}}},"
            + "{\"folder\": \"" + other + "y\", \"kinds\": {\"skills\": {\"fingerprint\": \"abc\"}}}]";
        File.WriteAllText(_trust.FilePath, json);

        Assert.NotNull(_trust.Trusted(_repo));
        Assert.Null(_trust.Trusted(Path.Combine(_dir, "otherx")));
        Assert.Null(_trust.Trusted(Path.Combine(_dir, "othery")));
        Assert.Empty(_trust.DisabledIn(Path.Combine(_dir, "other")));
        Assert.Null(_trust.CopyOf(Path.Combine(_dir, "othery"), ".agents/skills/deploy"));
        Assert.Equal(Skill("deploy"), _trust.CopyOf(_repo, ".agents/skills/deploy"));

        // A save reads the same file and sweeps without throwing.
        _trust.SetDisabled(_repo, "deploy", true);
        Assert.Contains("deploy", _trust.DisabledIn(_repo));
        Assert.Equal(Skill("deploy"), _trust.CopyOf(_repo, ".agents/skills/deploy"));
    }

    /// <summary>Review #93: disabling a name already listed writes nothing.</summary>
    [Fact]
    public void ASecondDisable_OfTheSameName_LeavesTheFileAsItWas()
    {
        _trust.SetDisabled(_repo, "deploy", true);
        byte[] before = File.ReadAllBytes(_trust.FilePath);
        var written = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_trust.FilePath, written);

        _trust.SetDisabled(_repo, "deploy", true);

        Assert.Equal(before, File.ReadAllBytes(_trust.FilePath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(_trust.FilePath));
    }

    /// <summary>Review #11: untrusting a folder never trusted logs no "untrusted" line.</summary>
    [Fact]
    public void AnUntrust_OfAFolderNeverTrusted_LogsNothing()
    {
        var lines = new List<string>();
        void Capture(NeonSidekick.Diagnostics.DiagnosticEvent e) => lines.Add(e.Message);
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += Capture;
        try
        {
            _trust.Untrust(_repo);
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= Capture;
        }

        Assert.DoesNotContain(lines, l => l.Contains("untrusted", StringComparison.Ordinal));
    }

    /// <summary>Review #62: a kept copy is whole, so a long skill whose resource alone changed is told apart from its SKILL.md's.</summary>
    [Fact]
    public void ALongSkill_WithOnlyAResourceEdited_IsMarkedFilesChanged()
    {
        string body = Skill("deploy", extra: "") + new string('x', 60_000) + "\n";
        string directory = PutProject(_repo, "deploy", body);
        File.WriteAllText(Path.Combine(directory, "ref.md"), "one");
        TrustFolder(_repo);
        File.WriteAllText(Path.Combine(directory, "ref.md"), "two");

        var state = _catalog.ReadProjectFolder(_repo, _repo);

        // The re-ask marks "files changed" exactly when the kept copy equals the SKILL.md read now (SkillsMenu's ReadForTrust).
        Assert.NotEqual(_trust.Trusted(_repo)!.Fingerprint, state.Fingerprint);
        Assert.Equal(state.Copies[".agents/skills/deploy"], _trust.CopyOf(_repo, ".agents/skills/deploy"));
    }

    [WindowsFact]
    public void OnWindows_ACaseTwin_IsTheSameFolder()
    {
        PutProject(_repo, "a", Skill("a"));
        TrustFolder(_repo);

        Assert.NotNull(_trust.Trusted(_repo.ToUpperInvariant()));
    }

    [Fact]
    public void VolumeOf_ADriveOrShareOnWindows_VolumesOrRootElsewhere()
    {
        Assert.Equal(@"D:\", ProjectSkillTrust.VolumeOf(@"D:\Repo\x", windows: true));
        Assert.Equal(@"\\server\share", ProjectSkillTrust.VolumeOf(@"\\server\share\repo", windows: true));
        Assert.Equal("/Volumes/Work", ProjectSkillTrust.VolumeOf("/Volumes/Work/repo", windows: false));
        Assert.Equal("/", ProjectSkillTrust.VolumeOf("/Users/me/repo", windows: false));
    }

    // ── load_skill ──

    [Fact]
    public async Task LoadSkill_AnUntrustedSkill_SaysItIsntOffered_AndReadsNothing()
    {
        PutProject(_repo, "deploy", Skill("deploy"));
        Scan();
        var tool = new LoadSkillTool(_catalog);

        string result = (string)(await tool.InvokeAsync(Args(("name", "deploy")), CancellationToken.None))!;

        Assert.Equal(SkillText.ProjectNotOffered("deploy", ProjectPendingReason.NotTrusted), result);
        Assert.DoesNotContain("The steps.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadSkill_ATrustedSkill_EditedAfterTheScan_IsRefused()
    {
        string directory = PutProject(_repo, "deploy", Skill("deploy"));
        Scan();
        TrustFolder(_repo);
        Scan();
        var tool = new LoadSkillTool(_catalog);
        Assert.Contains("The steps.", (string)(await tool.InvokeAsync(Args(("name", "deploy")), CancellationToken.None))!, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(directory, SkillCatalog.FileName), Skill("deploy", "Pulled in after the scan."));
        string result = (string)(await tool.InvokeAsync(Args(("name", "deploy")), CancellationToken.None))!;

        Assert.Equal(SkillText.ProjectChanged("deploy"), result);
    }

    [Fact]
    public async Task LoadSkill_AProjectSkillsFile_TooDeepToBeListed_IsRefused()
    {
        string directory = PutProject(_repo, "deploy", Skill("deploy"));
        string deep = Path.Combine(directory, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "x.md"), "secret");
        File.WriteAllText(Path.Combine(directory, "notes.md"), "listed");
        Scan();
        TrustFolder(_repo);
        Scan();
        var tool = new LoadSkillTool(_catalog);

        string listed = (string)(await tool.InvokeAsync(Args(("name", "deploy"), ("file", "notes.md")), CancellationToken.None))!;
        string refused = (string)(await tool.InvokeAsync(Args(("name", "deploy"), ("file", "a/b/c/d/e/x.md")), CancellationToken.None))!;

        Assert.Contains("listed", listed, StringComparison.Ordinal);
        Assert.Equal(SkillText.FileNotListed("deploy", "a/b/c/d/e/x.md"), refused);
    }

    // ── The editor ──

    [Fact]
    public void SkillEditor_CreatingATrustedProjectSkillsName_IsRefused_AndAnUpdateIsTheRepos()
    {
        var roots = _roots with { ProjectNames = ["deploy"] };

        var created = SkillEditor.Create(roots, SkillScope.Profile, "deploy", "Mine.", "Steps.", external: false);
        var updated = SkillEditor.Update(roots, SkillScope.Profile, "deploy", "Mine.", "Steps.", external: false);

        Assert.Equal(SkillEditOutcome.ExternalReadOnly, created.Outcome);
        Assert.Equal(SkillScope.Project, created.Scope);
        Assert.Equal(SkillText.ProjectNameTaken("deploy"), SkillText.Edited(created));
        Assert.Equal(SkillScope.Project, updated.Scope);
        Assert.Equal(SkillText.ProjectReadOnly("deploy"), SkillText.Edited(updated));
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "deploy")));
    }

    [Fact]
    public void ReadOnlyScopes_AreExternalAndProject_NeverWritable()
    {
        foreach (var scope in Enum.GetValues<SkillScope>())
        {
            Assert.Equal(scope is SkillScope.External or SkillScope.Project, SkillScopes.IsReadOnly(scope));
            Assert.Equal(!SkillScopes.IsReadOnly(scope), _roots.WritableScopes.Contains(scope));
        }

        Assert.Equal("project", SkillScopes.Name(SkillScope.Project));
        Assert.Throws<InvalidOperationException>(() => _roots.Of(SkillScope.Project));
    }

    [Fact]
    public void CopyFolder_FromARepo_LeavesTheMarkersOut_AndWritesAClaudeSkillsDefaultsIn()
    {
        string directory = PutProject(_repo, "notes", "# Write the notes\n\nSteps.\n", claude: true);
        File.WriteAllText(Path.Combine(directory, SkillProvenance.FileName), "{}");
        File.WriteAllText(Path.Combine(directory, SkillLock.FileName), "{}");
        File.WriteAllText(Path.Combine(directory, SkillDisable.FileName), "{}");

        var copied = SkillEditor.CopyFolder(directory, _roots.Profile, dropMarkers: true, rewriteSkill: text => SkillsMenu.ClaudeDefaults(text, "notes"));

        Assert.Equal(SkillEditOutcome.Copied, copied.Outcome);
        string copy = Path.Combine(_roots.Profile, "notes");
        Assert.False(File.Exists(Path.Combine(copy, SkillProvenance.FileName)));
        Assert.False(File.Exists(Path.Combine(copy, SkillLock.FileName)));
        Assert.False(File.Exists(Path.Combine(copy, SkillDisable.FileName)));
        _catalog.Scan(false);
        Assert.Equal("Write the notes", _catalog.Find("notes")!.Description);
        Assert.StartsWith("# Write the notes", File.ReadAllText(Path.Combine(directory, SkillCatalog.FileName)), StringComparison.Ordinal);   // the repo's untouched
    }

    // ── The words ──

    [Fact]
    public void FolderLabel_ThisFolder_AnAncestorRelative_TheRepoRootNoted()
    {
        string sub = Path.Combine(_repo, "a", "b");

        Assert.Equal(SkillsText.ThisFolderLabel, SkillsText.FolderLabel(sub, sub, _repo));
        Assert.Equal(Path.Combine("..", "..") + SkillsText.RepoRootNote, SkillsText.FolderLabel(_repo, sub, _repo));
        Assert.Equal("..", SkillsText.FolderLabel(Path.Combine(_repo, "a"), sub, _repo));
    }

    [Fact]
    public void TrustNotices_OncePerFolderAndFingerprint_TheNotTrustedAndChangedForms()
    {
        PutProject(_repo, "a", Skill("a"));
        PutProject(_repo, "b", Skill("b"));
        Scan();
        var noted = new HashSet<string>(StringComparer.Ordinal);

        var first = SkillsText.ProjectTrustNotices(_catalog.ProjectFolders, _catalog.ProjectPending, _repo, _repo, noted);
        var again = SkillsText.ProjectTrustNotices(_catalog.ProjectFolders, _catalog.ProjectPending, _repo, _repo, noted);
        TrustFolder(_repo);
        File.WriteAllText(Path.Combine(_repo, ".agents", "skills", "a", SkillCatalog.FileName), Skill("a", "Changed."));
        Scan();
        var changed = SkillsText.ProjectTrustNotices(_catalog.ProjectFolders, _catalog.ProjectPending, _repo, _repo, noted);
        var headless = SkillsText.ProjectTrustNotices(_catalog.ProjectFolders, _catalog.ProjectPending, _repo, _repo, new HashSet<string>(), headless: true);

        Assert.Equal([SkillsText.ProjectNotTrustedNotice(SkillsText.ThisFolderLabel, 2)], first);
        Assert.Empty(again);
        Assert.Equal([SkillsText.ProjectChangedNotice(SkillsText.ThisFolderLabel)], changed);
        Assert.Equal([SkillsText.HeadlessChangedNotice(SkillsText.ThisFolderLabel)], headless);
        Assert.Equal("(🎓 This folder has 2 skills not trusted yet: /skills › project at the PC to review)", SkillsText.ProjectNotTrustedNotice(SkillsText.ThisFolderLabel, 2));
    }

    [Fact]
    public void ProjectRows_GroupByFolder_TheTrustRowFirst_AndTheReposTextIsNeverMarkup()
    {
        string odd = Path.Combine(_dir, "[x]");
        Directory.CreateDirectory(Path.Combine(odd, ".git"));
        PutProject(odd, "deploy", Skill("deploy", "[red]hi"));
        _working = odd;
        Scan();
        var facts = new SkillsFacts(true, _catalog.All, _catalog.Shadowed, _catalog.Problems, _roots, Pending: _catalog.ProjectPending, Folders: _catalog.ProjectFolders, WorkingFolder: odd, RepoRoot: odd);

        var rows = SkillsText.ProjectRows(facts);

        Assert.Equal([ProjectRowKind.Heading, ProjectRowKind.Trust, ProjectRowKind.Pending], rows.Select(r => r.Kind));
        Assert.All(rows, r => _ = new Markup(r.Markup));   // Spectre would throw on a stray [
        Assert.Contains(SkillScope.Project, SkillsText.ScopeTabs(facts));
        Assert.Equal(1, SkillsText.ScopeCount(facts, SkillScope.Project));
    }

    [Fact]
    public void TheSkillsStatus_CountsTheProjectSkills_OfferedAndNot()
    {
        var skill = new Skill("lint", "d", SkillScope.Project, Path.Combine(_repo, "lint"));
        var facts = new SystemPromptFacts(null, null, null, Memory: false, Memories: [], TtsOutput: false, SpeechReady: false, Skills: [skill], ProjectNotOffered: 2);

        Assert.Equal("on, 1 skill (1 project, 2 project not offered)", SystemPromptSummary.SkillsStatus(facts));
    }

    [Fact]
    public void TheLiveLine_NamesTheProjectSkills()
    {
        Assert.Equal("Right now: offered; project skills: 3 trusted, 2 not trusted, over the cap: 10 not offered.", Help.HelpText.LiveOfferedProjectSkills(3, 2, 10));
        Assert.Equal(Help.HelpText.LiveOffered, Help.HelpText.LiveOfferedProjectSkills(0, 0, 0));
    }

    // ── The shell's tripwire ──

    [Fact]
    public void TheTripwire_NamesTheTrustFileAndItsCopies_WhereverTheyPoint()
    {
        var guard = new TrustedFilesPolice.Guard(Path.Combine(_dir, "home-app"), _repo);

        Assert.NotNull(TrustedFilesPolice.Find("Set-Content ../configs/project-trust.json '[]'", guard));
        Assert.NotNull(TrustedFilesPolice.Find("del ..\\configs\\project-trust.json.bad", guard));
        Assert.NotNull(TrustedFilesPolice.Find("copy x ../configs/project-trust/abc/def.md", guard));
        Assert.Null(TrustedFilesPolice.Find("type project-notes.md", guard));
    }

    /// <summary>The copies folder's arm needs configs right before it (2026-11-23, review #10): a repo's own src/project-trust passes.</summary>
    [Fact]
    public void TheTripwire_TheCopiesArm_NeedsConfigsBeforeIt()
    {
        var guard = new TrustedFilesPolice.Guard(Path.Combine(_dir, "home-app"), _repo);

        Assert.Null(TrustedFilesPolice.Find("type src/project-trust/x.txt", guard));
        Assert.NotNull(TrustedFilesPolice.Find("type configs/project-trust/abc/SKILL.md", guard));
        Assert.NotNull(TrustedFilesPolice.Find("type configs/project-trust.json", guard));
    }

    /// <summary>A folder whose only skill is manual-only (review #56): a trust would offer nothing, so no notice; one that would offer a skill still has one.</summary>
    [Fact]
    public void ProjectTrustNotices_AManualOnlySkillAlone_GivesNone()
    {
        PutProject(_repo, "b", Skill("b", "By hand.", "disable-model-invocation: true\n"), claude: true);
        Scan();

        Assert.Empty(SkillsText.ProjectTrustNotices(_catalog.ProjectFolders, _catalog.ProjectPending, _repo, _repo, new HashSet<string>(StringComparer.Ordinal)));

        PutProject(_repo, "a", Skill("a"));
        Scan();

        Assert.Single(SkillsText.ProjectTrustNotices(_catalog.ProjectFolders, _catalog.ProjectPending, _repo, _repo, new HashSet<string>(StringComparer.Ordinal)));
    }
}
