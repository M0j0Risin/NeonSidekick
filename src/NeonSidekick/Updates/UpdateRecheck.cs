using System.Security.Cryptography;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Updates;

/// <summary>What the update's code needs to know about this run, from <c>Program.cs</c> (and in a simulation build, its flags).</summary>
/// <param name="Folder">The install folder's layout.</param>
/// <param name="Running">The running version: <c>SidekickApp.Version</c>, three parts like a tag, since the assembly's own four would read <c>0.5.7</c> as older than <c>0.5.7.0</c>.</param>
/// <param name="Keys">The public keys to trust: <see cref="UpdateKeys.Embedded"/>.</param>
/// <param name="ExeName">The running exe's name, as launched.</param>
/// <param name="MoveAll">A simulation build's <c>--update-move-all</c>: every file counts as changed.</param>
public sealed record UpdateContext(UpdateFolder Folder, UpdateVersion Running, IReadOnlyList<string> Keys, string ExeName, bool MoveAll = false, Action? BetweenSteps = null)
{
    /// <summary>The comparison for paths in this install folder.</summary>
    public StringComparer Comparer => CaseRule.Comparer(Folder.Install);

    /// <summary>
    /// Whether the running exe's name is the one the manifest lists. Compared through the folder's case rule, since a process's own path keeps the casing it was launched with. A renamed copy can't stage, and its exit doesn't swap, since it would change the folder
    /// around an exe the swap can't replace.
    /// </summary>
    public bool IsListedExe(UpdateManifest installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        return IsThisExe(installed.Exe);
    }

    /// <summary>Whether <paramref name="name"/>, at the folder's top, is the running exe. The same file by identity also counts (2026-11-04, review P6-16): a run started as <c>NEONSI~1.EXE</c> keeps the short name in its process path.</summary>
    public bool IsThisExe(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Comparer.Equals(ExeName, name))
        {
            return true;
        }

        return ExeName.Contains('~', StringComparison.Ordinal) && (SameFileAs is { } same ? same(ExeName, name) : OperatingSystem.IsWindows() && SameFile(Path.Combine(Folder.Install, ExeName), Path.Combine(Folder.Install, name)));
    }

    /// <summary>Stands in for the short-name lookup in a test, as a volume can have 8.3 names off (2026-11-04, review P10-13). Null in the app.</summary>
    internal Func<string, string, bool>? SameFileAs { get; init; }

    internal static bool SameFile(string one, string other) => File.Exists(one) && File.Exists(other) && FileIdentity.Of(one) is { } first && first == FileIdentity.Of(other);
}

/// <summary>Finds the install folder.</summary>
public static class UpdateInstall
{
    /// <summary>
    /// The folder of the running exe's real path, with links resolved, so a Mac symlink on <c>PATH</c> still finds it. Null with no process path, and for an exe under a package's <c>.update</c> (2026-11-04, review P15-3): one started from <c>previous/</c> made a lock there, which the rule that nothing unlisted may be held there then took for a reason to refuse every rollback, and it held no lock on the real install.
    /// </summary>
    public static string? Locate(string? processPath) => Find(processPath, out _);

    /// <summary><see cref="Locate"/>, with the install above an exe under a <c>.update</c> in <paramref name="above"/>, from one look at the path (2026-11-05, review P23-13).</summary>
    public static string? Find(string? processPath, out string? above)
    {
        above = null;
        if (FolderOf(processPath) is not { } folder)
        {
            return null;
        }

        above = InstallAbove(folder);
        return above is null ? folder : null;
    }

    /// <summary>The install a folder sits inside the <c>.update</c> of, or null: the folder above that <c>.update</c> holds a manifest or a journal. What the write guard holds for a copy started from there.</summary>
    public static string? InstallAbove(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            if (string.Equals(Path.GetFileName(at), UpdateFolder.Name, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(at) is { } above && new UpdateFolder(above).IsPackageOrMidSwap)
            {
                return above;
            }
        }

        return null;
    }

    /// <summary>The folder of the running exe's real path, whatever it is, for <see cref="InstallAbove"/>.</summary>
    public static string? FolderOf(string? processPath) => string.IsNullOrEmpty(processPath) ? null : Path.GetDirectoryName(RealPath(processPath));

    /// <summary>
    /// The folder the tools' guard holds: <paramref name="folder"/>'s install while it's a package, else null. The start asks once, after its step has undone any interrupted swap, and the value is kept, so deleting the manifest mid-run can't lift the guard.
    /// </summary>
    public static string? Guarded(UpdateFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.IsPackageOrMidSwap ? folder.Install : null;
    }

    /// <summary>The running exe's file name, with links resolved the same way.</summary>
    public static string ExeName(string? processPath) => string.IsNullOrEmpty(processPath) ? "" : Path.GetFileName(RealPath(processPath));

    private static string RealPath(string processPath)
    {
        try
        {
            return File.ResolveLinkTarget(processPath, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(processPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(processPath);
        }
    }
}

/// <summary>What a proofs folder says of a manifest.</summary>
public enum UpdateChainAnswer
{
    /// <summary>The <c>.sig</c> verifies under a key this copy trusts, the <c>.sha256</c> names this manifest's hash and its version's archive.</summary>
    Holds,

    /// <summary>The <c>.sha256</c> or the <c>.sig</c> isn't there.</summary>
    Missing,

    /// <summary>No line of the <c>.sig</c> verifies under a key this copy trusts.</summary>
    NotSigned,

    /// <summary>The signed text doesn't name this manifest, or its version's archive.</summary>
    OtherManifest,
}

/// <summary>
/// A version's proof files (2026-11-04, self-update): its <c>.sha256</c>, <c>.sig</c> and notes, in <c>release/</c>, <c>staged/</c> or <c>previous/release/</c>. <see cref="Chain"/> is the walk to the embedded keys, one for both directions (review P4-20: two hand-written copies
/// had drifted, the rollback's failing open, review P2-1).
/// </summary>
public static class UpdateProofs
{
    /// <summary>
    /// The chain from <paramref name="proofs"/> to <paramref name="manifest"/>: the <c>.sig</c> over the <c>.sha256</c>'s bytes under <paramref name="keys"/>, the hash of <paramref name="manifestBytes"/>, what the manifest was parsed from (2026-11-04, review P13-8: a second read of the file could vouch for other bytes than the plan used), against the <c>.sha256</c>'s second line, the archive name against the manifest's own version and platform. A proof over <see cref="UpdateFolder.ReadCap"/> is no proof.
    /// </summary>
    public static UpdateChainAnswer Chain(string proofs, byte[] manifestBytes, UpdateManifest manifest, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        byte[]? shaBytes;
        string? sigText;
        try
        {
            shaBytes = UpdateFolder.ReadCapped(Path.Combine(proofs, UpdateFolder.ShaName));
            sigText = UpdateFolder.ReadCappedText(Path.Combine(proofs, UpdateFolder.SigName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return UpdateChainAnswer.Missing;
        }

        if (shaBytes is null || sigText is null || !UpdateSignature.Verify(shaBytes, sigText, keys))
        {
            return UpdateChainAnswer.NotSigned;
        }

        var sha = UpdateShaFile.TryParse(Encoding.UTF8.GetString(shaBytes));
        return sha is not null && UpdateHash.Same(UpdateHash.Of(manifestBytes), sha.ManifestHash) && sha.ArchiveName == UpdateCheck.ArchiveName(manifest.ParsedVersion, manifest.Rid)
            ? UpdateChainAnswer.Holds
            : UpdateChainAnswer.OtherManifest;
    }

    /// <summary>The signed text in <paramref name="folder"/>, if it's <paramref name="manifest"/>'s.</summary>
    public static UpdateShaFile? Read(string folder, UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        try
        {
            string path = Path.Combine(folder, UpdateFolder.ShaName);
            if (!File.Exists(path))
            {
                return null;
            }

            var sha = UpdateFolder.ReadCappedText(path) is { } text ? UpdateShaFile.TryParse(text) : null;
            return sha is not null && sha.ArchiveName == UpdateCheck.ArchiveName(manifest.ParsedVersion, manifest.Rid) ? sha : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The running version's notes as saved at staging, or null with none: an update keeps them with the version, so <c>/update notes</c> works offline and always matches what runs. Scrubbed here, the one place they're read from disk (2026-11-04, review P2-18: they were scrubbed three times, and no layer owned it).</summary>
    public static string? Notes(UpdateFolder folder, UpdateManifest installed)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (Read(folder.Release, installed) is null)
        {
            return null;
        }

        try
        {
            string path = Path.Combine(folder.Release, UpdateFolder.NotesName);
            return File.Exists(path) && UpdateFolder.ReadCappedText(path) is { } notes ? UntrustedText.Scrub(notes) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What the recheck hands the swap.</summary>
/// <param name="Installed">The installed manifest.</param>
/// <param name="Staged">The stage's.</param>
/// <param name="Moved">The staged files that move in.</param>
/// <param name="Dropped">What only the installed version lists.</param>
/// <param name="Verified">The installed files that match the installed manifest now: the only ones that go to <c>previous/</c>.</param>
/// <param name="ProofsVouch">Whether <c>release/</c> vouches for the installed manifest, or there's none.</param>
public sealed record UpdatePlanned(UpdateManifest Installed, UpdateManifest Staged, List<string> Moved, List<string> Dropped, List<string> Verified, bool ProofsVouch);

/// <summary>What became of a recheck that didn't pass.</summary>
public enum UpdateRecheckFailure
{
    /// <summary>The stage is at fault: it's dropped.</summary>
    Drop,

    /// <summary>A failure to read isn't a mismatch (2026-11-04, review P2-4: a scanner's brief sharing violation cost a whole download): the stage stays and the next exit tries again.</summary>
    Keep,
}

/// <summary>
/// The recheck at exit (2026-11-04, self-update). A manifest in the stage can't vouch for itself, since whoever changed a file could change it too, so the chain ends at the embedded keys: the <c>.sig</c> over the <c>.sha256</c>, the stage's manifest against the <c>.sha256</c>'s
/// second line, the moved files against the manifest, the installed manifest still the one the install hashes were taken against (a Mac doesn't lock a running exe, so a package can be unpacked over the folder during a session), the same platform as the install, and the version still newer than the running one. Any
/// mismatch drops the stage. It costs one signature check and hashing the files that move, and any file whose length or time changed since staging.
/// </summary>
public static class UpdateRecheck
{
    /// <summary>
    /// The chain, or null with the reason, and <paramref name="failure"/> saying whether a refusal should drop the stage. <paramref name="cancellationToken"/> is the window close's time left: a recheck that can't finish throws, and nothing moves. <paramref name="stagedRead"/> is the stage's manifest if the caller has read it (2026-11-04, review P9-19), so the Installing line and this check share one parse and can't disagree.
    /// </summary>
    public static UpdatePlanned? Check(UpdateContext context, CancellationToken cancellationToken, out string? reason, out UpdateRecheckFailure failure, UpdateManifestLoad? stagedRead = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folder = context.Folder;
        failure = UpdateRecheckFailure.Drop;
        try
        {
            // A held manifest keeps the stage (review P2-4), which the reader says for itself now (review P8-8).
            var stagedLoad = stagedRead ?? UpdateManifest.Load(folder.StagingManifest);
            if (stagedLoad.Unreadable)
            {
                reason = UpdateText.StageUnreadable(stagedLoad.Problem ?? UpdateText.ManifestHeld);
                failure = UpdateRecheckFailure.Keep;
                return null;
            }

            if (stagedLoad is not { Manifest: { } staged, Bytes: { } stagedBytes })
            {
                reason = UpdateText.StageChanged;
                return null;
            }

            switch (UpdateProofs.Chain(folder.Staged, stagedBytes, staged, context.Keys))
            {
                case UpdateChainAnswer.NotSigned:
                    reason = UpdateText.NotSigned;
                    return null;
                case UpdateChainAnswer.Missing or UpdateChainAnswer.OtherManifest:
                    reason = UpdateText.StageChanged;
                    return null;
            }

            // A manifest that's missing, held or bad isn't the stage's fault (2026-11-04, review P20-2: a by-hand unpack's moment without one cost a verified stage, blamed on the download).
            var installedLoad = UpdateManifest.Load(folder.Manifest);
            if (installedLoad is not { Manifest: { } installed, Bytes: { } installedBytes })
            {
                reason = installedLoad.Unreadable ? UpdateText.InstallManifestHeld : UpdateText.DamagedInstall(installedLoad.Problem ?? UpdateText.ManifestUnreadable);
                failure = UpdateRecheckFailure.Keep;
                return null;
            }

            // Against what the folder holds, not what's running (2026-11-04, reviews P11-7, P15-1): over a package unpacked mid-session the running version said "newer", and a quit took 0.5.9 down to 0.5.8, journaled from a version the exe on disk wasn't. A folder that doesn't match the running version is no basis for a swap, and that's no fault of the stage.
            if (installed.ParsedVersion != context.Running)
            {
                reason = UpdateText.FolderNotThisVersion(installed.Version, context.Running.ToString());
                failure = UpdateRecheckFailure.Keep;
                return null;
            }

            if (staged.ParsedVersion <= installed.ParsedVersion)
            {
                reason = UpdateText.NoLongerNewer(staged.Version);
                return null;
            }

            // A validly signed package for another platform, swapped into the stage after its probe, passes every check above (2026-11-04, review P1-10).
            if (!string.Equals(staged.Rid, installed.Rid, StringComparison.Ordinal))
            {
                reason = UpdateText.StageChanged;
                return null;
            }

            var hashes = UpdateInstallHashes.TryRead(folder);
            if (hashes is null || !UpdateHash.Same(UpdateHash.Of(installedBytes), hashes.ManifestHash))
            {
                reason = UpdateText.InstallChanged;
                return null;
            }

            var current = hashes.Current(folder.Install, installed, cancellationToken);
            var moved = UpdateSwap.Changed(installed, staged, current, context.Comparer, context.MoveAll);
            var listed = staged.Hashes(context.Comparer);
            foreach (string path in moved)
            {
                string full = Path.Combine(folder.Staging, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full) || !UpdateHash.Same(UpdateHash.OfFile(full, cancellationToken), listed[path]))
                {
                    reason = UpdateText.StageChanged;
                    return null;
                }
            }

            // Once more after the last file (review P7-6): a recheck that ran out with its last hash moves nothing, or the swap starts with no time left.
            cancellationToken.ThrowIfCancellationRequested();
            bool vouch = !Directory.Exists(folder.Release) || UpdateProofs.Chain(folder.Release, installedBytes, installed, context.Keys) == UpdateChainAnswer.Holds;
            var verified = installed.Files.Where(file => current.TryGetValue(file.Path, out string? now) && UpdateHash.Same(now, file.Sha256)).Select(file => file.Path).ToList();
            reason = null;
            return new UpdatePlanned(installed, staged, moved, UpdateSwap.Dropped(installed, staged, context.Comparer), verified, vouch);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            reason = UpdateText.StageChanged;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = UpdateText.StageUnreadable(ex.Message);
            failure = UpdateRecheckFailure.Keep;
            return null;
        }
        catch (CryptographicException ex)
        {
            reason = UpdateText.StageUnreadable(ex.Message);
            failure = UpdateRecheckFailure.Keep;
            return null;
        }
    }
}

/// <summary>What a rollback's check hands the swap.</summary>
/// <param name="Installed">The running version's manifest.</param>
/// <param name="Target">The version going back to.</param>
/// <param name="Incoming">The files in <c>previous</c> that passed and move in.</param>
/// <param name="Added">What only the running version lists.</param>
/// <param name="Verified">The current files that leave and match the running manifest: the only ones that go to <c>outgoing/</c>.</param>
/// <param name="DocsStaying">Docs the old version's copy of can't come back: altered or gone. The current copy stays, and the Going back line says so.</param>
public sealed record UpdateRollbackPlan(UpdateManifest Installed, UpdateManifest Target, List<string> Incoming, List<string> Added, List<string> Verified, List<string> DocsStaying);

/// <summary>
/// The check before going back (2026-11-04, self-update), at the command and again at exit: the same recheck less its "newer" and install-hashes rules, using the old version's manifest, <c>.sha256</c> and <c>.sig</c> kept in <c>previous</c>. It hashes the files that move, so a file altered
/// by hand that both versions share stays as it is: a rollback doesn't repair the install. It also hashes the current files that leave, under the same limit, and any that doesn't match goes to <c>kept/</c> rather than with <c>outgoing/</c>'s delete. A version installed by hand has no signature files, because signing comes after packaging, so going
/// back to one checks its manifest's hashes only. Only an absent folder reads that way (review P2-1): a swap moves proofs that don't vouch to <c>outgoing/</c>, so proofs here that don't are tampering.
///
/// <para>An altered or missing doc doesn't block it (review P2-2, the user's call): an update that repairs an edited <c>docs/x.md</c> moved the edit to <c>kept/</c>, and an edit to a doc shouldn't cost the way back. The exe and libraries still refuse.</para>
/// </summary>
public static class UpdateRollback
{
    /// <summary>
    /// The plan, or null with the reason, and <paramref name="failure"/> saying whether a refusal should drop a pending rollback: a failure to read isn't a mismatch (2026-11-04, review P8-9: a scanner in <c>previous/</c> deleted the request at quit, with a raw exception for a reason). <paramref name="cancellationToken"/> as in <see cref="UpdateRecheck.Check"/>.
    /// </summary>
    public static UpdateRollbackPlan? Check(UpdateContext context, CancellationToken cancellationToken, out string? reason, out UpdateRecheckFailure failure, UpdateManifestLoad? targetRead = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folder = context.Folder;
        failure = UpdateRecheckFailure.Drop;
        if (!File.Exists(folder.PreviousManifest))
        {
            reason = UpdateText.NothingToGoBackTo;
            return null;
        }

        try
        {
            var targetLoad = targetRead ?? UpdateManifest.Load(folder.PreviousManifest);
            if (targetLoad.Unreadable)
            {
                reason = UpdateText.OldVersionUnreadable(UpdateManifest.FileName);
                failure = UpdateRecheckFailure.Keep;
                return null;
            }

            if (targetLoad is not { Manifest: { } target, Bytes: { } targetBytes })
            {
                reason = targetLoad.Problem;
                return null;
            }

            var installedLoad = UpdateManifest.Load(folder.Manifest);
            if (installedLoad.Manifest is not { } installed)
            {
                reason = installedLoad.Unreadable ? UpdateText.InstallManifestHeld : UpdateText.DamagedInstall(installedLoad.Problem ?? UpdateText.ManifestUnreadable);
                failure = UpdateRecheckFailure.Keep;
                return null;
            }

            // Only back: a package of the kept version unpacked by hand would journal A to A, and a kill mid-swap would read as finished, leaving a mix (2026-11-04, review P1-5).
            if (target.ParsedVersion >= context.Running)
            {
                reason = UpdateText.NothingToGoBackTo;
                return null;
            }

            // What the journal would call its source (2026-11-04, review P15-1): a package unpacked over the folder mid-session.
            if (installed.ParsedVersion != context.Running)
            {
                reason = UpdateText.FolderNotThisVersion(installed.Version, context.Running.ToString());
                return null;
            }

            // As the forward recheck asks (2026-11-04, review P9-2): another platform's signed package in previous/ passed every check here, and the exit swapped in a folder with no exe that runs.
            if (!string.Equals(target.Rid, installed.Rid, StringComparison.Ordinal))
            {
                reason = UpdateText.OldVersionOtherPlatform(target.Rid);
                return null;
            }

            // Signed versions must still check out. A key this build has dropped fails here, since the bad key is why it went.
            if (Directory.Exists(folder.PreviousRelease))
            {
                switch (UpdateProofs.Chain(folder.PreviousRelease, targetBytes, target, context.Keys))
                {
                    case UpdateChainAnswer.Missing or UpdateChainAnswer.NotSigned:
                        reason = UpdateText.OldVersionNotSigned(target.Version);
                        return null;
                    case UpdateChainAnswer.OtherManifest:
                        reason = UpdateText.OldVersionAltered(UpdateManifest.FileName);
                        return null;
                }
            }

            // Every file the two versions disagree on must be there, or going back would leave a mix of versions (a package unpacked by hand over the folder), and every listed file held must match. Nothing else may be held (review P2-7): previous/ holds only verified old files, so an unlisted one would land beside the exe unchecked.
            var comparer = context.Comparer;
            var current = installed.Hashes(comparer);
            var was = target.Hashes(comparer);
            foreach (string held in UpdateSwap.FilesHeldIn(folder.Previous))
            {
                if (!was.ContainsKey(held))
                {
                    reason = UpdateText.OldVersionUnlisted(held);
                    return null;
                }
            }

            var incoming = new List<string>();
            var docs = new List<string>();
            foreach (var file in target.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string full = Path.Combine(folder.Previous, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    if (UpdateHash.Same(UpdateHash.OfFile(full, cancellationToken), file.Sha256))
                    {
                        incoming.Add(file.Path);
                    }
                    else if (IsDoc(file.Path))
                    {
                        docs.Add(file.Path);
                    }
                    else
                    {
                        reason = UpdateText.OldVersionAltered(file.Path);
                        return null;
                    }
                }
                else if (!current.TryGetValue(file.Path, out string? now) || !UpdateHash.Same(now, file.Sha256) || !File.Exists(folder.Full(file.Path)))
                {
                    // The last test is review P7-4: the manifests agreed on a shared library deleted since, and the swap then took away the only way back.
                    if (!IsDoc(file.Path))
                    {
                        reason = UpdateText.OldVersionIncomplete(file.Path);
                        return null;
                    }

                    docs.Add(file.Path);
                }
            }

            var added = installed.Files.Select(f => f.Path).Where(p => !was.ContainsKey(p)).ToList();
            var verified = new List<string>();
            foreach (string path in incoming.Concat(added))
            {
                string full = folder.Full(path);
                if (current.TryGetValue(path, out string? listed) && File.Exists(full) && UpdateHash.Same(UpdateHash.OfFile(full, cancellationToken), listed))
                {
                    verified.Add(path);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            reason = null;

            // Only docs that are there (2026-11-04, review P11-15): an edited doc the new version dropped is in kept/, and "the current one stays" named a file that wasn't.
            return new UpdateRollbackPlan(installed, target, incoming, added, verified, [.. docs.Where(doc => File.Exists(folder.Full(doc)))]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            reason = UpdateText.OldVersionUnreadable(ex.Message);
            failure = UpdateRecheckFailure.Keep;
            return null;
        }
    }

    /// <summary>Whether <paramref name="path"/> is a doc: a <c>.md</c>, <c>LICENSE*</c> or <c>README*</c> file, whose copy not coming back breaks nothing.</summary>
    internal static bool IsDoc(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = path[(path.LastIndexOf('/') + 1)..];
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("README", StringComparison.OrdinalIgnoreCase);
    }
}
