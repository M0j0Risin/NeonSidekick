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
        // By name, as .git and .neon are (2026-11-04, review P2-6).
        Assert.NotNull(TrustedFilesPolice.Find(command, new TrustedFilesPolice.Guard(Path.Combine(_dir, "home"), _dir, _install)));
        Assert.Null(TrustedFilesPolice.Find(command, new TrustedFilesPolice.Guard(Path.Combine(_dir, "home"), _dir)));
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
}
