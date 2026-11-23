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
        Assert.Equal(10, _catalog.ProjectPending.Count(p => p.Reason == ProjectPendingReason.OverCap));
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

        string copies = Path.Combine(_trust.CopiesPath, ProjectSkillTrust.FolderHash(ProjectSkillTrust.KeyOf(_repo)));
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
}
