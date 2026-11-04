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
        return Comparer.Equals(ExeName, installed.Exe);
    }
}

/// <summary>Finds the install folder.</summary>
public static class UpdateInstall
{
    /// <summary>The folder of the running exe's real path, with links resolved, so a Mac symlink on <c>PATH</c> still finds it. Null with no process path.</summary>
    public static string? Locate(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        try
        {
            string real = File.ResolveLinkTarget(processPath, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(processPath);
            return Path.GetDirectoryName(real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetDirectoryName(Path.GetFullPath(processPath));
        }
    }

    /// <summary>
    /// The folder the tools' guard holds: <paramref name="folder"/>'s install while it's a package, else null. The start asks once, after its step has undone any interrupted swap, and the value is kept, so deleting the manifest mid-run can't lift the guard.
    /// </summary>
    public static string? Guarded(UpdateFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.IsPackageOrMidSwap ? folder.Install : null;
    }

    /// <summary>The running exe's file name, with links resolved the same way.</summary>
    public static string ExeName(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return "";
        }

        try
        {
            return Path.GetFileName(File.ResolveLinkTarget(processPath, returnFinalTarget: true)?.FullName ?? processPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFileName(processPath);
        }
    }
}

/// <summary>
/// A version's proof files (2026-11-04, self-update): its <c>.sha256</c>, <c>.sig</c> and notes, in <c>release/</c> or <c>previous/release/</c>. They count only if the <c>.sha256</c> names the version its manifest does. Otherwise, as after a package unpacked by hand over
/// the folder, they're read as none, like a copy installed by hand.
/// </summary>
public static class UpdateProofs
{
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

            var sha = UpdateShaFile.TryParse(File.ReadAllText(path));
            return sha is not null && sha.ArchiveName == UpdateCheck.ArchiveName(manifest.ParsedVersion, manifest.Rid) ? sha : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The running version's notes as saved at staging, or null with none: an update keeps them with the version, so <c>/update notes</c> works offline and always matches what runs.</summary>
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
            return File.Exists(path) ? UntrustedText.Scrub(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What the recheck hands the swap.</summary>
public sealed record UpdatePlanned(UpdateManifest Installed, UpdateManifest Staged, List<string> Moved, List<string> Dropped);

/// <summary>
/// The recheck at exit (2026-11-04, self-update). A manifest in the stage can't vouch for itself, since whoever changed a file could change it too, so the chain ends at the embedded keys: the <c>.sig</c> over the <c>.sha256</c>, the stage's manifest against the <c>.sha256</c>'s
/// second line, the moved files against the manifest, the installed manifest still the one the install hashes were taken against (a Mac doesn't lock a running exe, so a package can be unpacked over the folder during a session), and the version still newer than the running one. Any
/// mismatch drops the stage. It costs one signature check and hashing the files that move.
/// </summary>
public static class UpdateRecheck
{
    /// <summary>The chain, or null with the reason. <paramref name="cancellationToken"/> is the window close's time left: a recheck that can't finish throws, and nothing moves.</summary>
    public static UpdatePlanned? Check(UpdateContext context, CancellationToken cancellationToken, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folder = context.Folder;
        try
        {
            byte[] shaBytes = File.ReadAllBytes(folder.StagedSha);
            if (!UpdateSignature.Verify(shaBytes, File.ReadAllText(folder.StagedSig), context.Keys))
            {
                reason = UpdateText.NotSigned;
                return null;
            }

            var sha = UpdateShaFile.TryParse(Encoding.UTF8.GetString(shaBytes));
            if (sha is null || !UpdateHash.Same(UpdateHash.OfFile(folder.StagingManifest), sha.ManifestHash))
            {
                reason = UpdateText.StageChanged;
                return null;
            }

            var staged = UpdateManifest.ReadFile(folder.StagingManifest, out reason);
            if (staged is null)
            {
                return null;
            }

            if (sha.ArchiveName != UpdateCheck.ArchiveName(staged.ParsedVersion, staged.Rid))
            {
                reason = UpdateText.StageChanged;
                return null;
            }

            if (staged.ParsedVersion <= context.Running)
            {
                reason = UpdateText.NoLongerNewer(staged.Version);
                return null;
            }

            var installed = UpdateManifest.ReadFile(folder.Manifest, out reason);
            if (installed is null)
            {
                return null;
            }

            var hashes = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(folder.StagedInstallHashes), UpdateJsonContext.Default.UpdateInstallHashes);
            if (hashes is null || !UpdateHash.Same(UpdateHash.OfFile(folder.Manifest), hashes.ManifestHash))
            {
                reason = UpdateText.InstallChanged;
                return null;
            }

            var moved = UpdateSwap.Changed(installed, staged, hashes.Files, context.Comparer, context.MoveAll);
            var listed = staged.Hashes(context.Comparer);
            foreach (string path in moved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string full = Path.Combine(folder.Staging, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full) || !UpdateHash.Same(UpdateHash.OfFile(full), listed[path]))
                {
                    reason = UpdateText.StageChanged;
                    return null;
                }
            }

            return new UpdatePlanned(installed, staged, moved, UpdateSwap.Dropped(installed, staged, context.Comparer));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            reason = UpdateText.StageChanged;
            return null;
        }
    }
}

/// <summary>What a rollback's check hands the swap.</summary>
public sealed record UpdateRollbackPlan(UpdateManifest Installed, UpdateManifest Target, List<string> Added);

/// <summary>
/// The check before going back (2026-11-04, self-update), at the command and again at exit: the same recheck less its "newer" and install-hashes rules, using the old version's manifest, <c>.sha256</c> and <c>.sig</c> kept in <c>previous</c>. It hashes only the files that move, so a file altered
/// by hand that both versions share stays as it is: a rollback doesn't repair the install. A version installed by hand has no signature files, because signing comes after packaging, so going back to one checks its manifest's hashes only.
/// </summary>
public static class UpdateRollback
{
    /// <summary>The plan, or null with the reason. <paramref name="cancellationToken"/> as in <see cref="UpdateRecheck.Check"/>.</summary>
    public static UpdateRollbackPlan? Check(UpdateContext context, CancellationToken cancellationToken, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folder = context.Folder;
        if (!File.Exists(folder.PreviousManifest))
        {
            reason = UpdateText.NothingToGoBackTo;
            return null;
        }

        try
        {
            var target = UpdateManifest.ReadFile(folder.PreviousManifest, out reason);
            if (target is null)
            {
                return null;
            }

            var installed = UpdateManifest.ReadFile(folder.Manifest, out reason);
            if (installed is null)
            {
                return null;
            }

            // Signed versions must still check out. A key this build has dropped fails here, since the bad key is why it went.
            if (UpdateProofs.Read(folder.PreviousRelease, target) is { } sha)
            {
                string shaPath = Path.Combine(folder.PreviousRelease, UpdateFolder.ShaName);
                string sigPath = Path.Combine(folder.PreviousRelease, UpdateFolder.SigName);
                if (!File.Exists(sigPath) || !UpdateSignature.Verify(File.ReadAllBytes(shaPath), File.ReadAllText(sigPath), context.Keys))
                {
                    reason = UpdateText.OldVersionNotSigned(target.Version);
                    return null;
                }

                if (!UpdateHash.Same(UpdateHash.OfFile(folder.PreviousManifest), sha.ManifestHash))
                {
                    reason = UpdateText.OldVersionAltered(UpdateManifest.FileName);
                    return null;
                }
            }

            // Every file the two versions disagree on must be there, or going back would leave a mix of versions (a package unpacked by hand over the folder), and every listed file held must match.
            var comparer = context.Comparer;
            var current = installed.Hashes(comparer);
            foreach (var file in target.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string full = Path.Combine(folder.Previous, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    if (!UpdateHash.Same(UpdateHash.OfFile(full), file.Sha256))
                    {
                        reason = UpdateText.OldVersionAltered(file.Path);
                        return null;
                    }
                }
                else if (!current.TryGetValue(file.Path, out string? now) || !UpdateHash.Same(now, file.Sha256))
                {
                    reason = UpdateText.OldVersionIncomplete(file.Path);
                    return null;
                }
            }

            var was = target.Hashes(comparer);
            return new UpdateRollbackPlan(installed, target, [.. installed.Files.Select(f => f.Path).Where(p => !was.ContainsKey(p))]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return null;
        }
    }
}
