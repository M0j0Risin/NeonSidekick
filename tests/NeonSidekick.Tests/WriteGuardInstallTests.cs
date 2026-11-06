using NeonSidekick.Files;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests;

/// <summary>A package's install folder under the write guard and the shell police (2026-11-04, self-update).</summary>
public sealed class WriteGuardInstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _install;

    public WriteGuardInstallTests()
    {
        _install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, "update-manifest.json"), "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private WriteGuardFacts Facts(string? home = null, IReadOnlyList<string>? skills = null, string? install = "") =>
        new(() => home, () => null, () => skills ?? [], () => null, () => false, () => null, InstallFolder: () => install == "" ? _install : install);

    private static WorkingDirectory At(string root) => new(() => root, new ManualTimeProvider());

    [Fact]
    public void AWriteIntoTheFolder_IsRefused_FromOutsideIt()
    {
        using var _ = WriteGuard.Configure(Facts());
        var files = At(_dir);
        Assert.Equal(FileOutcome.InstallFilesProtected, files.WriteText(Path.Combine("install", "NeonSidekick.exe"), "x", overwrite: true).Outcome);
        Assert.Equal(FileOutcome.InstallFilesProtected, files.WriteText(Path.Combine("install", ".update", "swap.json"), "x", overwrite: true).Outcome);
        Assert.Equal(FileOutcome.InstallFilesProtected, files.WriteText(Path.Combine("install", ".update", "staging", "a.dll"), "x", overwrite: true).Outcome);
        Assert.Equal(FileOutcome.InstallFilesProtected, files.Delete(Path.Combine("install", "update-manifest.json")).Outcome);
        Assert.Equal(FileOutcome.Ok, files.WriteText("beside.txt", "x", overwrite: true).Outcome);
        Assert.True(File.Exists(Path.Combine(_install, "update-manifest.json")));
    }

    [Fact]
    public void AWorkingFolderAtOrInsideTheFolder_GetsNoWritePass()
    {
        using var _ = WriteGuard.Configure(Facts());
        Assert.Equal(FileOutcome.InstallFilesProtected, At(_install).WriteText("notes.md", "x", overwrite: true).Outcome);
        string inner = Path.Combine(_install, "docs");
        Directory.CreateDirectory(inner);
        Assert.Equal(FileOutcome.InstallFilesProtected, At(inner).WriteText("notes.md", "x", overwrite: true).Outcome);
    }

    [Fact]
    public void Reads_StayAllowed()
    {
        using var _ = WriteGuard.Configure(Facts());
        Assert.Equal(FileOutcome.Ok, At(_install).ReadText("update-manifest.json", null, null).Outcome);
    }

    [Fact]
    public void AFolderNotDecidedAsAPackage_IsNotGuarded()
    {
        using var _ = WriteGuard.Configure(Facts(install: null));
        Assert.Equal(FileOutcome.Ok, At(_install).WriteText("notes.md", "x", overwrite: true).Outcome);
    }

    [Fact]
    public void TheDecision_GoesByTheManifest_OrAnInterruptedSwap()
    {
        var folder = new UpdateFolder(_install);
        Assert.Equal(_install, UpdateInstall.Guarded(folder));

        File.Delete(folder.Manifest);
        Assert.Null(UpdateInstall.Guarded(folder));

        Directory.CreateDirectory(folder.Root);
        File.WriteAllText(folder.Journal, "{}");
        Assert.Equal(_install, UpdateInstall.Guarded(folder));
    }

    [Fact]
    public void DeletingTheManifestMidRun_DoesNotLiftTheGuard()
    {
        using var _ = WriteGuard.Configure(Facts());
        File.Delete(Path.Combine(_install, "update-manifest.json"));
        Assert.Equal(FileOutcome.InstallFilesProtected, At(_install).WriteText("notes.md", "x", overwrite: true).Outcome);
    }

    [Fact]
    public void AnotherSpellingOfTheFolder_IsFoundByItsIdentity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var _ = WriteGuard.Configure(Facts());
        Directory.CreateDirectory(Path.Combine(_install, ".update"));
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        // \\?\C:\... on Windows; on a Mac the temp folder under /var is reached as /private/var. Neither starts with the spelling the guard holds.
        string target = Path.Combine(_install, ".update", "swap.json");
        string spelled = OperatingSystem.IsWindows() ? @"\\?\" + target : "/private" + target;
        Assert.False(WorkingDirectory.IsInside(_install, spelled));

        Assert.Equal(FileOutcome.InstallFilesProtected, WriteGuard.Judge(elsewhere, spelled, WriteRole.Write));
    }

    [Fact]
    public void AHomeInsideTheFolder_KeepsTheHomesOwnRules()
    {
        string home = Path.Combine(_install, ".neonsidekick");
        string sandbox = Path.Combine(home, "profiles", "neon", "files");
        Directory.CreateDirectory(sandbox);
        using var _ = WriteGuard.Configure(Facts(home));

        Assert.Equal(FileOutcome.Ok, At(sandbox).WriteText("notes.md", "x", overwrite: true).Outcome);

        string profile = Path.Combine(home, "profiles", "neon");
        Assert.Equal(FileOutcome.AppFilesProtected, At(profile).WriteText("profile.json", "x", overwrite: true).Outcome);
    }

    [Fact]
    public void AHomeWithAnotherNameInsideTheFolder_StillWritesItsSandbox()
    {
        string home = Path.Combine(_install, "portable");
        string sandbox = Path.Combine(home, "profiles", "neon", "files");
        Directory.CreateDirectory(sandbox);
        using var _ = WriteGuard.Configure(Facts(home));

        Assert.Equal(FileOutcome.Ok, At(sandbox).WriteText("notes.md", "x", overwrite: true).Outcome);
    }

    [Fact]
    public void AnExternalSkillRootInsideTheFolder_IsRefused_EvenAsTheWorkingFolder()
    {
        string skills = Path.Combine(_install, ".agents", "skills");
        Directory.CreateDirectory(skills);
        using var _ = WriteGuard.Configure(Facts(skills: [skills]));

        Assert.Equal(FileOutcome.InstallFilesProtected, At(skills).WriteText("mine.md", "x", overwrite: true).Outcome);
    }

    [Fact]
    public void TheAppsOwnWrites_GoThrough()
    {
        using var _ = WriteGuard.Configure(Facts());
        Assert.Equal(FileOutcome.Ok, WriteGuard.AsApp(() => At(_install).WriteText("notes.md", "x", overwrite: true).Outcome));
    }

    [Fact]
    public void TheRefusal_IsARefusal_WithItsOwnSentence()
    {
        Assert.True(WriteGuard.IsRefusal(FileOutcome.InstallFilesProtected));
        Assert.Equal(FileText.InstallFilesProtected("a.dll"), FileText.Error(FileOutcome.InstallFilesProtected, "a.dll", "write"));
    }

    [Fact]
    public void APackageUnpackedInTheSandbox_StaysGuarded()
    {
        // The home's pass is for a home kept inside the install, and only that: as "is it in a home" a package in the default sandbox had no guard (2026-11-04, review P4-9).
        string home = Path.Combine(_dir, "home");
        string sandbox = Path.Combine(home, "profiles", "neon", "files");
        string package = Path.Combine(sandbox, "pkg");
        Directory.CreateDirectory(package);
        using var _ = WriteGuard.Configure(Facts(home, install: package));
        var files = At(sandbox);

        Assert.Equal(FileOutcome.InstallFilesProtected, files.WriteText(Path.Combine("pkg", "NeonSidekick.exe"), "x", overwrite: true).Outcome);
        Assert.Equal(FileOutcome.Ok, files.WriteText("notes.md", "x", overwrite: true).Outcome);
    }

    [Fact]
    public void DeletingOrMovingAFolderThatHoldsTheInstall_IsRefused()
    {
        // An ancestor lost every unloaded DLL before failing on the running exe, and on a Mac the Trash took the whole of it (2026-11-04, review P6-1).
        using var _ = WriteGuard.Configure(Facts());
        var files = At(Path.GetDirectoryName(_dir)!);
        string name = Path.GetFileName(_dir);

        Assert.Equal(FileOutcome.InstallFilesProtected, files.Delete(name).Outcome);
        Assert.Equal(FileOutcome.InstallFilesProtected, files.Move(name, name + "-moved", overwrite: false).Outcome);
        Assert.True(File.Exists(Path.Combine(_install, "update-manifest.json")));
    }

    [Fact]
    public void DeletingAFolderThatHoldsTheHome_IsRefused_ButABesideFolderIsnt()
    {
        string home = Path.Combine(_dir, "keep", "home");
        string beside = Path.Combine(_dir, "beside");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(beside);
        using var _ = WriteGuard.Configure(Facts(home, install: null));
        var files = At(_dir);

        Assert.Equal(FileOutcome.AppFilesProtected, files.Delete("keep").Outcome);
        Assert.Equal(FileOutcome.AppFilesProtected, WriteGuard.HoldsAppFolders(Path.Combine(_dir, "keep")));
        Assert.Null(WriteGuard.HoldsAppFolders(beside));
    }

    [Fact]
    public void TheRefusals_SayInOrHolds()
    {
        Assert.Contains("is in or holds", FileText.InstallFilesProtected("x"), StringComparison.Ordinal);
        Assert.Contains("is in or holds", FileText.AppFilesProtected("x"), StringComparison.Ordinal);
    }

    // ---- the shell police

    [Fact]
    public void TheShellPolice_RefusesAFullPathInTheFolder_ButNotARelativeOne()
    {
        var guard = new TrustedFilesPolice.Guard(Path.Combine(_dir, "home"), _dir, _install);
        string exe = Path.Combine(_install, "NeonSidekick.exe");

        Assert.Equal(exe, TrustedFilesPolice.Find("copy evil " + exe, guard));
        Assert.Equal(_install, TrustedFilesPolice.Find("dir " + _install, guard));
        Assert.Null(TrustedFilesPolice.Find("type a.dll", guard));
        Assert.Null(TrustedFilesPolice.Find("copy evil " + Path.Combine(_dir, "other", "a.dll"), guard));
    }

    [Theory]
    [InlineData("type .update/ready")]
    [InlineData("del update-manifest.json")]
    [InlineData("copy evil.dll sub/.update/staging/a.dll")]
    public void TheShellPolice_RefusesTheUpdatesOwnNames(string command)
    {
        // By name, when the word resolves into the install (2026-11-04, reviews P2-6, P14-1): the same words elsewhere are a site's own CSS or manifest.
        Assert.NotNull(TrustedFilesPolice.Find(command, new TrustedFilesPolice.Guard(Path.Combine(_dir, "home"), _install, _install)));
        Assert.Null(TrustedFilesPolice.Find(command, new TrustedFilesPolice.Guard(Path.Combine(_dir, "home"), Path.Combine(_dir, "site"), _install)));
        Assert.Null(TrustedFilesPolice.Find(command, new TrustedFilesPolice.Guard(Path.Combine(_dir, "home"), _install)));
    }

    [Fact]
    public void TheShellPolice_FromInsideTheInstall_RefusesARelativeWordThatExists()
    {
        // `copy evil.dll onnxruntime.dll` from the install's own folder named no full path (review P2-6).
        File.WriteAllText(Path.Combine(_install, "onnxruntime.dll"), "x");
        var guard = new TrustedFilesPolice.Guard(Path.Combine(_dir, "home"), _install, _install);

        Assert.NotNull(TrustedFilesPolice.Find("copy evil.dll onnxruntime.dll", guard));
        Assert.Null(TrustedFilesPolice.Find("copy a.txt new-name.txt", guard));
    }

    [Fact]
    public void TheShellPolice_InAHomeKeptInTheInstall_LeavesTheSandboxAlone()
    {
        // A portable install's sandbox would otherwise refuse every command.
        string home = Path.Combine(_install, "portable");
        string sandbox = Path.Combine(home, "profiles", "neon", "files");
        Directory.CreateDirectory(sandbox);
        File.WriteAllText(Path.Combine(sandbox, "notes.md"), "x");

        Assert.Null(TrustedFilesPolice.Find("type notes.md", new TrustedFilesPolice.Guard(home, sandbox, _install)));
    }

    [Fact]
    public void TheShellPolice_ReadsAPathBehindAnEqualsOrASwitchsColon()
    {
        // A drive's colon is kept (review P7-3): --out=D:\install\x read as \install\x, which named nothing the guard knows.
        var guard = new TrustedFilesPolice.Guard(Path.Combine(_dir, "home"), _dir, _install);
        string exe = Path.Combine(_install, "NeonSidekick.exe");

        Assert.NotNull(TrustedFilesPolice.Find("tool --out=" + exe, guard));
        Assert.NotNull(TrustedFilesPolice.Find("tool -o:" + exe, guard));
    }

    [Fact]
    public void TheShellPolice_LeavesAFolderNotDecided()
    {
        var guard = new TrustedFilesPolice.Guard(Path.Combine(_dir, "home"), _dir);
        Assert.Null(TrustedFilesPolice.Find("copy evil " + Path.Combine(_install, "a.dll"), guard));
    }

    [Fact]
    public void TheShellPolice_ReadsTheSameFacts()
    {
        using var _ = WriteGuard.Configure(Facts());
        Assert.Equal(_install, TrustedFilesPolice.For(_dir)!.InstallFolder);
    }

    // ---- what review passes 8-21 found in the police (2026-11-05)

    private string Home => Path.Combine(_dir, "home");

    // The sandbox elsewhere, the command running in workdir.
    private TrustedFilesPolice.Guard From(string workdir, string? install = null) => new(Home, _dir, install ?? _install, workdir);

    private string Site()
    {
        string site = Path.Combine(_dir, "site");
        Directory.CreateDirectory(site);
        return site;
    }

    [Theory]
    [InlineData("del update-manifest.json")]
    [InlineData("type .update/ready")]
    [InlineData("del *.dll")]
    [InlineData("copy x.txt brand-new.dll")]
    [InlineData("copy x.txt brand-new.dylib")]
    [InlineData("copy x.txt brand-new.exe")]
    public void ARelativeWord_ReadsFromWhereTheCommandRuns(string command)
    {
        // A sandbox elsewhere and a workdir in the install passed every one (reviews P8-3, P16-1), and wildcards and a new library's name counted nowhere (review P8-5).
        Assert.NotNull(TrustedFilesPolice.Find(command, From(_install)));
        Assert.Null(TrustedFilesPolice.Find(command, From(Site())));
    }

    [Theory]
    [InlineData("del ../install/update-manifest.json", true)]
    [InlineData("del ../install/*.dll", true)]
    [InlineData("del ../inst*/a.dll", true)]
    [InlineData("del ../other/a.dll", false)]
    [InlineData("del ../other/*.dll", false)]
    public void ARelativeWord_ThatClimbsIntoTheInstall_Counts(string command, bool refused)
    {
        Assert.Equal(refused, TrustedFilesPolice.Find(command, From(Site())) is not null);
    }

    [Fact]
    public void AWorkdirInsideTheUpdateFolder_IsRefusedOutright()
    {
        string staged = Path.Combine(_install, ".update", "staged");
        Directory.CreateDirectory(staged);

        Assert.Equal(staged, TrustedFilesPolice.Find("echo hi", From(staged), out var finding));
        Assert.Equal(TrustedFinding.Install, finding);
    }

    [Fact]
    public void AnIdentitySpelling_OfTheInstall_IsRefused()
    {
        // The police asks the write guard's own check (reviews P8-4, P16-4, P19-3). \\?\ on Windows, /private over a Mac's temp folder.
        string target = Path.Combine(_install, "a.dll");
        string spelled = OperatingSystem.IsWindows() ? @"\\?\" + target : "/private" + target;

        Assert.NotNull(TrustedFilesPolice.Find("copy x.txt " + spelled, From(Site()), out var finding));
        Assert.Equal(TrustedFinding.Install, finding);
    }

    [Fact]
    public void ALinkToTheInstall_IsRefused()
    {
        string link = Path.Combine(_dir, "shortcut");
        try
        {
            Directory.CreateSymbolicLink(link, _install);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // no right to make links on this machine
        }

        Assert.NotNull(TrustedFilesPolice.Find("copy x.txt " + Path.Combine(link, "a.dll"), From(Site())));
    }

    [Fact]
    public void APackageKeptInAHome_IsStillAnInstall()
    {
        string install = Path.Combine(Home, "app");
        Directory.CreateDirectory(install);

        Assert.NotNull(TrustedFilesPolice.Find("del update-manifest.json", From(install, install)));
    }

    [Fact]
    public void HoldsAppFolders_SeesAnIdentitySpelling()
    {
        // Review P10-12: the earlier test's spelling was the plain one, so it never reached identity.
        string keep = Path.Combine(_dir, "keep");
        string home = Path.Combine(keep, "home");
        Directory.CreateDirectory(home);
        using var _ = WriteGuard.Configure(Facts(home, install: null));

        Assert.Equal(FileOutcome.AppFilesProtected, WriteGuard.HoldsAppFolders(OperatingSystem.IsWindows() ? @"\\?\" + keep : "/private" + keep));
    }

    [Fact]
    public void AWildcard_GoesByTheVolumesCaseRule()
    {
        // Reviews P19-6, P10-18: a case-sensitive volume's other folder is not the install.
        bool ignoreCase = CaseRule.Comparison(_install) == StringComparison.OrdinalIgnoreCase;
        Assert.Equal(ignoreCase, TrustedFilesPolice.Find("del ../INST*/a.dll", From(Site())) is not null);
    }

    [Theory]
    [InlineData("git add CLAUDE.md")]
    [InlineData("git add .neon/plans/x.md")]
    [InlineData("git log .git")]
    [InlineData("FOO=1 git add CLAUDE.md")]
    [InlineData("git.exe add .claude/x")]
    public void AGitCommand_SkipsWhatGitOwns(string command)
    {
        Assert.Null(TrustedFilesPolice.Find(command, From(Site())));
    }

    [Theory]
    [InlineData("git -C {0} add x")]
    [InlineData("FOO=1 git -C {0} add x")]
    [InlineData("git.exe --git-dir={0}/x log")]
    public void AGitCommand_IsStillHeldToTheInstall(string command)
    {
        // Review P9-1: a git segment skipped the police whole.
        string text = string.Format(System.Globalization.CultureInfo.InvariantCulture, command, _install.Replace('\\', '/'));
        Assert.NotNull(TrustedFilesPolice.Find(text, From(Site()), out var finding));
        Assert.Equal(TrustedFinding.Install, finding);
    }

    [WindowsFact]
    public void OnWindows_ALoneSwitch_IsNoFolderAtTheDrivesRoot()
    {
        // Review P16-5: rmdir /s read as naming D:\s, a home there refusing every such line.
        var guard = new TrustedFilesPolice.Guard(Path.GetPathRoot(_dir) + "s", _dir, null, _dir);

        Assert.Null(TrustedFilesPolice.Find("rmdir /s build", guard, out _, "cmd"));
        Assert.Null(TrustedFilesPolice.Find("rmdir /s build", guard));
        Assert.NotNull(TrustedFilesPolice.Find("cat /s", guard, out _, "bash"));   // Git Bash's own /s is a path
    }

    [UnixFact]
    public void OffWindows_ALoneSlashWord_IsAPath()
    {
        var guard = new TrustedFilesPolice.Guard("/s", _dir, null, _dir);
        Assert.NotNull(TrustedFilesPolice.Find("ls /s", guard, out _, "bash"));
    }

    [Theory]
    [InlineData("--git-dir=.git", "--git-dir|.git")]
    [InlineData(".git/hooks/pre=commit", ".git/hooks/pre|commit")]
    [InlineData("/opt/a=b/c.dll", "/opt/a=b/c.dll")]
    [InlineData("\"/opt/a b/c.dll\"", "/opt/a b/c.dll")]
    [InlineData("'/opt/c.dll'", "/opt/c.dll")]
    [InlineData("evil.dll>/opt/x.dll", "evil.dll|/opt/x.dll")]
    [InlineData("1>/opt/x.dll", "1|/opt/x.dll")]
    [InlineData("-o:/opt/x.dll", "-o|/opt/x.dll")]
    [InlineData("a\\b", "a/b")]
    public void ValuesOf_ReadsAWordsPaths(string word, string expected)
    {
        // Reviews P9-4, P14-2, P16-2, tested directly so the current drive doesn't matter (review P21-17).
        Assert.Equal(expected.Split('|'), TrustedFilesPolice.ValuesOf(word));
    }

    [WindowsFact]
    public void ValuesOf_ADriveNeedsItsSlash_OnWindows()
    {
        Assert.Equal(["D:/a=b/c.dll"], TrustedFilesPolice.ValuesOf(@"D:\a=b\c.dll"));
        Assert.Equal(["evil.dll", "D:/Neon/x.dll"], TrustedFilesPolice.ValuesOf("evil.dll>D:/Neon/x.dll"));
        Assert.Equal(["--out", "D:/x"], TrustedFilesPolice.ValuesOf("--out=D:/x"));
        Assert.Equal(["D", "x"], TrustedFilesPolice.ValuesOf("D:x"));
    }

    [UnixFact]
    public void ValuesOf_ADriveMeansNothing_OffWindows()
    {
        Assert.Equal(["D", "/a/c.dll"], TrustedFilesPolice.ValuesOf("D:/a/c.dll"));
    }

    [Fact]
    public void AQuotedRun_StaysOneWord()
    {
        // Review P16-3: --out="C:/My Apps/x.dll" read as two words, neither a path.
        string spaced = Path.Combine(_dir, "My Apps");
        Directory.CreateDirectory(spaced);
        string target = spaced.Replace('\\', '/') + "/x.dll";

        foreach (string kind in new[] { "powershell", "bash", "cmd" })
        {
            Assert.NotNull(TrustedFilesPolice.Find("tool --out=\"" + target + "\"", From(Site(), spaced), out _, kind));
        }
    }

    [Theory]
    [InlineData("Get-Item x # see .git/config", "powershell")]
    [InlineData("ls # see .git/config", "bash")]
    public void AComment_RunsNothing_SoItDoesntTripIt(string command, string kind)
    {
        Assert.Null(TrustedFilesPolice.Find(command, From(Site()), out _, kind));
    }

    [Fact]
    public void AnApostropheInAComment_DoesntHideTheScriptAfterIt()
    {
        // Review P16-3: a second apostrophe further down closed the first, so the program between them read as one string.
        Assert.NotNull(TrustedFilesPolice.Find("// don't touch\ncopy x .git/config\n// it's fine\n", From(Site()), out _, "python", isScript: true));
        Assert.Null(TrustedFilesPolice.Find("# see .git/config\nWrite-Output hi\n", From(Site()), out _, "powershell", isScript: true));
    }

    [Fact]
    public void AnUnbalancedApostrophe_StaysLiteral()
    {
        Assert.NotNull(TrustedFilesPolice.Find("echo don't touch .git/x", From(Site())));
        Assert.Null(TrustedFilesPolice.Find("echo don't panic", From(Site())));
    }

    [UnixFact]
    public void AnEscapedBlank_ReadsAsTheShellDoes()
    {
        // Review P19-4.
        string spaced = Path.Combine(_dir, "My Apps");
        Directory.CreateDirectory(spaced);

        Assert.NotNull(TrustedFilesPolice.Find(@"rm My\ Apps/x.dll", new TrustedFilesPolice.Guard(Home, _dir, spaced, _dir), out _, "bash"));
    }

    // The app's two folders one level under a folder the sandbox can name.
    private TrustedFilesPolice.Guard Holding()
    {
        string apps = Path.Combine(_dir, "apps");
        string home = Path.Combine(apps, "home");
        string install = Path.Combine(apps, "pkg");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(install);
        return new TrustedFilesPolice.Guard(home, _dir, install, _dir);
    }

    [Theory]
    [InlineData("rm -rf apps")]
    [InlineData("rm -rf ./apps")]
    [InlineData("rm -rf *")]
    [InlineData("rm -rf app*")]
    [InlineData("Remove-Item apps -Recurse")]
    [InlineData("ri apps")]
    [InlineData("del apps")]
    [InlineData("rmdir apps")]
    [InlineData("mv apps old")]
    [InlineData("ren apps old")]
    [InlineData("Move-Item -Path apps -Destination old")]
    [InlineData("Rename-Item apps -NewName old")]
    [InlineData("env rm -rf apps")]
    [InlineData("nohup rm -rf apps")]
    [InlineData("sudo rm -rf apps")]
    [InlineData("xargs rm -rf apps")]
    public void ADeleteOrMoveOfAFolderHoldingTheApp_IsRefused(string command)
    {
        // Review P19-5, the user's call.
        Assert.NotNull(TrustedFilesPolice.Find(command, Holding(), out var finding));
        Assert.Equal(TrustedFinding.HoldsAppFolder, finding);
    }

    [Theory]
    [InlineData("mv old apps")]
    [InlineData("Move-Item -Path old -Destination apps")]
    [InlineData("mv -t apps old")]
    [InlineData("ls apps")]
    [InlineData("echo rm apps")]
    [InlineData("rm apps/notes.txt")]
    [InlineData("rm *.tmp")]
    [InlineData("rm -rf other")]
    public void TheRestOfThatList_Passes(string command)
    {
        Assert.Null(TrustedFilesPolice.Find(command, Holding()));
    }

    [WindowsFact]
    public void ACmdDelete_OfAFolderHoldingTheApp_IsRefused()
    {
        Assert.NotNull(TrustedFilesPolice.Find("rmdir /s /q apps", Holding(), out var finding, "cmd"));
        Assert.Equal(TrustedFinding.HoldsAppFolder, finding);
    }

    [Fact]
    public void TheUsersOwnFolder_GoesByTheSameRule()
    {
        // The finding's own line, against the real profile folder; nothing here reaches a shell.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var guard = new TrustedFilesPolice.Guard(Path.Combine(profile, TrustedFilesPolice.HomeFolderName), _dir, null, _dir);

        Assert.NotNull(TrustedFilesPolice.Find("rm -rf ~/*", guard, out var finding));
        Assert.Equal(TrustedFinding.HoldsAppFolder, finding);
        Assert.Null(TrustedFilesPolice.Find("rm ~/*.tmp", guard));
        Assert.Null(TrustedFilesPolice.Find("mv report.txt ~", guard));
        Assert.Null(TrustedFilesPolice.Find("ls ~", guard));
    }

    [Theory]
    [InlineData("dotnet build -c Release")]
    [InlineData("git status")]
    [InlineData("npm run build -- --out=dist/app.dll")]
    [InlineData("python -m http.server 8000")]
    [InlineData("cat report.csv data/a=b.txt")]
    [InlineData("rm build/*.dll")]
    [InlineData("Get-ChildItem -Filter *.dll -Recurse")]
    [InlineData("tar -xf a.tar -C out")]
    [InlineData("copy a.dll b.dll")]
    [InlineData("echo '.update { color: red }' > site.css")]
    [InlineData("cat update-manifest.json .update/x")]
    [InlineData("ls ~")]
    public void AHarmlessLine_Passes(string command)
    {
        foreach (string kind in new[] { "powershell", "bash", "cmd", "zsh" })
        {
            Assert.Null(TrustedFilesPolice.Find(command, From(Site()), out _, kind));
        }
    }

    [Fact]
    public void TheFindings_HaveTheirOwnWords()
    {
        // Prompt text, pinned (review P14-3): the trusted files' sentence sent the model to tools that refuse these too.
        Assert.Equal(
            "Error: refused by the shell police: the command names the app's install folder or its update files, which only the app's own update changes; no tool here changes them, so tell the user the change, and they can make it",
            ShellText.TrustedPolicedFor("x", TrustedFinding.Install));
        Assert.Equal(
            "Error: refused by the shell police: the command deletes or moves a folder holding the app's install or its home, which would take the app's own files with it; name only what should go, one level down",
            ShellText.TrustedPolicedFor("x", TrustedFinding.HoldsAppFolder));
        Assert.Equal(ShellText.TrustedPoliced, ShellText.TrustedPolicedFor("x"));
        Assert.Contains("install folder", ShellText.TrustedShown("x", null, TrustedFinding.Install), StringComparison.Ordinal);
        Assert.Contains("holding the app", ShellText.TrustedShown("x", null, TrustedFinding.HoldsAppFolder), StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedIn_ReadsFromTheWorkdir_AndSaysWhichItFound()
    {
        using var _ = WriteGuard.Configure(Facts(Home));
        var request = new CommandRequest("powershell", "del update-manifest.json", []);

        var refusal = ShellPoliceChecks.TrustedIn(request, request.Command, null, _install, At(_dir), isScript: false);

        Assert.NotNull(refusal);
        Assert.Equal(ShellText.InstallPoliced, refusal.ModelText);
        Assert.Null(ShellPoliceChecks.TrustedIn(request, request.Command, null, Site(), At(_dir), isScript: false));
    }
}
