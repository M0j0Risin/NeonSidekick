using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Speech;

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
/// <item><c>kept/&lt;version&gt;/</c>: the user's own files a swap had to move out of the way (a stray, an edited copy), a folder per swap. Nothing of the app's is here, and the app never deletes it.</item>
/// <item><c>swap.json</c>, <c>rollback</c> (a pending rollback), <c>swap-failed.txt</c> (why a window close's swap failed).</item>
/// </list>
/// </summary>
public sealed class UpdateFolder
{
    /// <summary>The folder's own name, under the install.</summary>
    public const string Name = ".update";

    /// <summary>The proof folder's name, in <c>.update/</c> and <c>previous/</c>; a package can't list a top-level folder of this name (2026-11-04, review P6-13: the name was a literal in four places, and a rename missed in one would move proofs into the install).</summary>
    public const string ReleaseName = "release";

    /// <summary>The <c>.sha256</c>'s fixed name in <c>staged/</c>, <c>release/</c> and <c>previous/release/</c>; the text inside names the asset.</summary>
    public const string ShaName = "release.sha256";

    /// <summary>The <c>.sig</c>'s fixed name.</summary>
    public const string SigName = "release.sha256.sig";

    /// <summary>The saved release notes' fixed name.</summary>
    public const string NotesName = "notes.txt";

    /// <summary>The install hashes' name in <c>staged/</c>.</summary>
    public const string InstallHashesName = "install-hashes.json";

    /// <summary>
    /// The most any file read under <c>.update</c>, or the manifest, may hold (2026-11-04, review P13-19): far above any real one, since a manifest of hundreds of files is under 100 KB and notes are capped at 2 MB from the network. A stray 1 GB file was read whole on the start's synchronous path, in every mode, and the start's catch lets out-of-memory through.
    /// </summary>
    public const long ReadCap = 16 * 1024 * 1024;

    private static readonly EnumerationOptions OneLevel = new() { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };

    public UpdateFolder(string install)
    {
        Install = install ?? throw new ArgumentNullException(nameof(install));
        Root = Path.Combine(install, Name);
    }

    public string Install { get; }

    public string Root { get; }

    public string Manifest => Path.Combine(Install, UpdateManifest.FileName);

    public string Staging => Path.Combine(Root, "staging");

    public string StagingParts => ModelStore.PartsPath(Staging);

    public string Staged => Path.Combine(Root, "staged");

    public string Ready => Path.Combine(Root, "ready");

    public string Release => Path.Combine(Root, ReleaseName);

    public string Previous => Path.Combine(Root, "previous");

    public string PreviousOld => Path.Combine(Root, "previous.old");

    public string Outgoing => Path.Combine(Root, "outgoing");

    /// <summary>Where a swap puts the user's files it had to move: <c>kept/&lt;version&gt;</c>, a new folder per swap.</summary>
    public string Kept => Path.Combine(Root, "kept");

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

    public string PreviousRelease => Path.Combine(Previous, ReleaseName);

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

    /// <summary>The file at <paramref name="path"/> read under <see cref="ReadCap"/>, or null when it holds more. Throws as a read does.</summary>
    public static byte[]? ReadCapped(string path) => RegularFiles.ReadAtMost(path, ReadCap);

    /// <summary><see cref="ReadCapped"/> as text.</summary>
    public static string? ReadCappedText(string path) => ReadCapped(path) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>Whether <paramref name="path"/> is a symbolic link or a junction, to a file or a folder, its target there or not.</summary>
    public static bool IsLink(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether anything is at <paramref name="path"/>: a file, a folder, or a link whose target is gone.</summary>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || IsLink(path);

    /// <summary>
    /// The files and links under <paramref name="folder"/>, hidden ones too, a link counted as one entry and never walked through (2026-11-04, reviews P8-10, P10-16): a junction to Documents in a stray folder had a swap plan moves of the user's real documents, and a link loop walked until the path was too long. A folder it can't read throws.
    /// </summary>
    public static IEnumerable<string> EntriesUnder(string folder)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", OneLevel))
        {
            if (Directory.Exists(entry) && !IsLink(entry))
            {
                foreach (string below in EntriesUnder(entry))
                {
                    yield return below;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }

    /// <summary>The real folders under <paramref name="folder"/>, by the same walk: a link to a folder is an entry, not one of these.</summary>
    public static IEnumerable<string> FoldersUnder(string folder)
    {
        foreach (string entry in Directory.EnumerateDirectories(folder, "*", OneLevel))
        {
            if (!IsLink(entry))
            {
                yield return entry;
                foreach (string below in FoldersUnder(entry))
                {
                    yield return below;
                }
            }
        }
    }

    /// <summary>
    /// Deletes a file or folder of this layout, the one delete for the update's code (2026-11-04, reviews P5-5, P1-20, P9-16). A read-only file would otherwise fail the delete for good: <c>previous.old</c> stuck that way stopped every later swap with "still in use".
    /// So a failure clears the attributes, the folder's own too, and tries once more. True when it's gone; what's left is logged, and something retries it next time. A link goes as itself.
    /// </summary>
    public static bool DeleteQuietly(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            if (!Directory.Exists(path))
            {
                File.Delete(path);
                return true;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ClearReadOnly(path);
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, UpdateText.CleanupLeft(path, ex.Message));
            return false;
        }
    }

    // A link stays as it is: on Unix a chmod would follow it, onto what it points at outside the install.
    private static void ClearReadOnly(string folder)
    {
        File.SetAttributes(folder, File.GetAttributes(folder) & ~FileAttributes.ReadOnly);
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            if (IsLink(entry))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                ClearReadOnly(entry);
            }
            else
            {
                File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
            }
        }
    }
}
