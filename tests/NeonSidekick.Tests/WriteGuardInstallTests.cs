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
