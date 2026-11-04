namespace NeonSidekick.Updates;

/// <summary>
/// Where everything lives under <c>&lt;install&gt;/.update</c> (2026-11-04, self-update), on the install's volume because the swap renames files. The layout
/// is a lasting contract: the old exe stages, probes and swaps the new one.
/// <list type="bullet">
/// <item><c>in-use.lock</c>, <c>staging.lock</c>: the two locks; neither is ever deleted, since a <c>flock</c> belongs to the file and a copy holding a deleted one would lock nothing another copy sees.</item>
/// <item><c>staging/</c>, <c>staging.parts/</c>, <c>*.extracting</c>: the stage and its download.</item>
/// <item><c>staged/</c>: the stage's <c>.sha256</c>, <c>.sig</c>, notes and install hashes, and the probe's files, outside <c>staging/</c> so they never land among the checked files; <c>ready</c> beside it.</item>
/// <item><c>release/</c>: the installed version's <c>.sha256</c>, <c>.sig</c> and notes, empty for a copy installed by hand.</item>
/// <item><c>previous/</c>: the old version's changed files, its manifest and its <c>release/</c>. <c>previous.old/</c> holds the older set during a swap, and <c>outgoing/</c> the newer files during a rollback, for an undo.</item>
/// <item><c>swap.json</c>, <c>rollback</c> (a pending rollback), <c>swap-failed.txt</c> (why a window close's swap failed).</item>
/// </list>
/// </summary>
public sealed class UpdateFolder
{
    /// <summary>The folder's own name, under the install.</summary>
    public const string Name = ".update";

    /// <summary>The <c>.sha256</c>'s fixed name in <c>staged/</c>, <c>release/</c> and <c>previous/release/</c>; the text inside names the asset.</summary>
    public const string ShaName = "release.sha256";

    /// <summary>The <c>.sig</c>'s fixed name.</summary>
    public const string SigName = "release.sha256.sig";

    /// <summary>The saved release notes' fixed name.</summary>
    public const string NotesName = "notes.txt";

    /// <summary>The install hashes' name in <c>staged/</c>.</summary>
    public const string InstallHashesName = "install-hashes.json";

    public UpdateFolder(string install)
    {
        Install = install ?? throw new ArgumentNullException(nameof(install));
        Root = Path.Combine(install, Name);
    }

    public string Install { get; }

    public string Root { get; }

    public string Manifest => Path.Combine(Install, UpdateManifest.FileName);

    public string Staging => Path.Combine(Root, "staging");

    public string StagingParts => Staging + ".parts";

    public string Staged => Path.Combine(Root, "staged");

    public string Ready => Path.Combine(Root, "ready");

    public string Release => Path.Combine(Root, "release");

    public string Previous => Path.Combine(Root, "previous");

    public string PreviousOld => Path.Combine(Root, "previous.old");

    public string Outgoing => Path.Combine(Root, "outgoing");

    public string Journal => Path.Combine(Root, "swap.json");

    public string Rollback => Path.Combine(Root, "rollback");

    public string SwapFailed => Path.Combine(Root, "swap-failed.txt");

    public string InUseLock => Path.Combine(Root, "in-use.lock");

    public string StagingLock => Path.Combine(Root, "staging.lock");

    public string ProbeResult => Path.Combine(Staged, "probe-result");

    public string ProbeResultInstalled => Path.Combine(Staged, "probe-result-installed");

    public string ProbeStderr => Path.Combine(Staged, "probe-stderr");

    public string StagedSha => Path.Combine(Staged, ShaName);

    public string StagedSig => Path.Combine(Staged, SigName);

    public string StagedNotes => Path.Combine(Staged, NotesName);

    public string StagedInstallHashes => Path.Combine(Staged, InstallHashesName);

    public string StagingManifest => Path.Combine(Staging, UpdateManifest.FileName);

    public string PreviousManifest => Path.Combine(Previous, UpdateManifest.FileName);

    public string PreviousRelease => Path.Combine(Previous, "release");

    /// <summary>A path under the install, from a relative one with forward slashes.</summary>
    public string Full(string relative) => Path.Combine(Install, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Whether this folder holds a package's manifest: what decides a copy can update, and that the tools' guard holds here.</summary>
    public bool IsPackage => File.Exists(Manifest);

    /// <summary>
    /// <see cref="IsPackage"/>, or a swap's journal with the manifest gone: a swap moves the old manifest aside before the new one comes in, so a kill in between leaves none, and that folder is still a package with an undo to run and a guard to keep
    /// (2026-11-04, self-update, caught by <c>UpdateFlowTests</c>: the start's step read it as no package and never undid the swap).
    /// </summary>
    public bool IsPackageOrMidSwap => IsPackage || File.Exists(Journal);

    /// <summary>A relative path, with forward slashes, for a path under the install (the journal holds these, so moving the folder changes nothing).</summary>
    public string Relative(string full) => Path.GetRelativePath(Install, full).Replace(Path.DirectorySeparatorChar, '/');
}
