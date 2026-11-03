using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.CustomCommands;

/// <summary>What a <see cref="CustomCommandEditor"/> call did.</summary>
public enum CustomCommandEditOutcome
{
    Created,
    Saved,
    Renamed,
    Moved,
    Copied,
    Deleted,

    /// <summary>The name is no valid command name (<see cref="CustomCommandName.IsValid"/>).</summary>
    BadName,

    /// <summary>The name is a built-in command's word.</summary>
    BuiltIn,

    /// <summary>The folder holds that name already (any case); nothing is ever replaced.</summary>
    Exists,

    /// <summary>The file is gone, or not under its folder.</summary>
    Missing,

    /// <summary>The file, or a subfolder on the way to the new one, is a link: never written through.</summary>
    Linked,

    /// <summary>The text to save is blank.</summary>
    Blank,

    /// <summary>The text to save is over <see cref="CustomCommandFile.MaxBytes"/>.</summary>
    TooLarge,

    /// <summary>The file is where it would go already.</summary>
    NothingToChange,

    /// <summary>
    /// The two folders a move or copy goes between are one folder (2026-10-31, review P7-2: both linked to one place), so there is nothing to
    /// do; an info notice, not an error.
    /// </summary>
    SharedFolder,

    /// <summary>A file failure: <see cref="CustomCommandEditResult.Detail"/> says what.</summary>
    Failed,
}

/// <summary>
/// The outcome, the command's name and scope, the file's path when one was written or moved, a failure's detail; a delete's way
/// (<see cref="Way"/>), and whether a saved text held a <c>\$</c> that may read differently (<see cref="MayReadDifferently"/>).
/// </summary>
public sealed record CustomCommandEditResult(CustomCommandEditOutcome Outcome, string Name, CustomCommandScope Scope, string Path = "", string Detail = "")
{
    public DeleteWay Way { get; init; }

    public bool MayReadDifferently { get; init; }

    /// <summary>For <c>Linked</c>: the file itself is the link, not a folder on the way (2026-10-31, review P4-3).</summary>
    public bool LinkIsFile { get; init; }

    /// <summary>Whether it did what was asked.</summary>
    public bool Done => Outcome is CustomCommandEditOutcome.Created or CustomCommandEditOutcome.Saved or CustomCommandEditOutcome.Renamed
        or CustomCommandEditOutcome.Moved or CustomCommandEditOutcome.Copied or CustomCommandEditOutcome.Deleted;
}

/// <summary>
/// The write side of the custom commands (2026-10-30, the <c>Skills/SkillEditor</c> shape): <see cref="Create"/> writes the template,
/// <see cref="Save"/> a text as a command, and the pane's acts — <see cref="Rename"/>, <see cref="Move"/>, <see cref="MoveToProfile"/>,
/// <see cref="CopyToProfile"/>, <see cref="Delete"/>. A built-in word, a name the folder holds already (any case), a missing file and a link
/// are refused, never overwritten. A <c>:</c> name writes into its subfolder, made as needed, the root too when it's gone; a subfolder on the
/// way that is a link is refused (the scan skips it), the root itself may be one and is written through. What <see cref="Create"/> and
/// <see cref="Save"/> write is UTF-8 without a BOM with <c>\n</c> line endings on both systems; a rename, move or copy keeps a file's bytes.
/// A subfolder a rename, move or delete empties is removed (only when empty). A file failure is an outcome, never an exception.
/// </summary>
public static class CustomCommandEditor
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The longest description <see cref="Save"/> takes from the text's first line.</summary>
    public const int SavedDescriptionLength = 60;

    /// <summary>A new command from the template (<see cref="CustomCommandText.Template"/>), in <paramref name="scope"/>'s folder.</summary>
    public static CustomCommandEditResult Create(CustomCommandRoots roots, CustomCommandScope scope, string name)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(name);
        return Write(roots.Of(scope), scope, name.Trim(), CustomCommandText.Template(name.Trim()), CustomCommandEditOutcome.Created, mayReadDifferently: false);
    }

    /// <summary>
    /// <paramref name="text"/> (a sent message, a saved prompt) as a new command: the template's how-to lines, a <c>description</c> from its first
    /// line, then the text made safe (<see cref="CustomCommandTemplate.Escape"/>), so running it with nothing typed sends the text back.
    /// </summary>
    public static CustomCommandEditResult Save(CustomCommandRoots roots, CustomCommandScope scope, string name, string text)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(text);
        name = name.Trim();
        string body = CustomCommandFile.CleanBody(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        if (body.Trim().Length == 0)
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.Blank, name, scope);
        }

        string content = SavedFile(body, out bool mayReadDifferently);
        if (Utf8NoBom.GetByteCount(content) > CustomCommandFile.MaxBytes)
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.TooLarge, name, scope);
        }

        return Write(roots.Of(scope), scope, name, content, CustomCommandEditOutcome.Saved, mayReadDifferently);
    }

    /// <summary>The whole file <see cref="Save"/> writes for <paramref name="body"/> (already folded and cleaned). Pure.</summary>
    public static string SavedFile(string body, out bool mayReadDifferently)
    {
        ArgumentNullException.ThrowIfNull(body);
        string first = CustomCommandFile.FallbackDescription(body) ?? "";
        first = new string(first.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (first.Length > SavedDescriptionLength)
        {
            // Never between a surrogate pair (2026-10-31, review P3-5: an emoji on the cut wrote half of one).
            first = TextCut.Safe(first, SavedDescriptionLength).TrimEnd() + "…";
        }

        var sb = new StringBuilder();
        sb.Append("---\n");
        foreach (string line in CustomCommandText.TemplateHowTo)
        {
            sb.Append(line).Append('\n');
        }

        // The frontmatter's own scalar rule (2026-10-31, review P4-9), plain when that reads back the same, so the reader and writer agree.
        sb.Append(CustomCommandFile.DescriptionKey).Append(": ").Append(Skills.SkillFrontmatter.Scalar(first)).Append('\n');
        sb.Append("---\n");
        sb.Append(CustomCommandTemplate.Escape(body, out mayReadDifferently)).Append('\n');
        return sb.ToString();
    }

    /// <summary>The file at <paramref name="file"/> renamed to <paramref name="newName"/> under its own folder, its bytes kept.</summary>
    public static CustomCommandEditResult Rename(CustomCommandRoots roots, CustomCommandScope scope, string file, string newName)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(newName);
        return Relocate(roots.Of(scope), scope, file, roots.Of(scope), scope, newName.Trim(), copy: false, CustomCommandEditOutcome.Renamed);
    }

    /// <summary>The file moved to the other scope's folder under the same name (<c>to global</c>, <c>to this profile</c>).</summary>
    public static CustomCommandEditResult Move(CustomCommandRoots roots, CustomCommandScope scope, string file, CustomCommandScope to)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(file);
        if (to == scope)
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.NothingToChange, NameOf(roots.Of(scope), file), scope);
        }

        if (roots.Same)
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.SharedFolder, NameOf(roots.Of(scope), file), scope);
        }

        return Relocate(roots.Of(scope), scope, file, roots.Of(to), to, null, copy: false, CustomCommandEditOutcome.Moved);
    }

    /// <summary>The file moved into another profile's <c>commands</c> folder (<paramref name="profileRoot"/>), made when it's not there.</summary>
    public static CustomCommandEditResult MoveToProfile(CustomCommandRoots roots, CustomCommandScope scope, string file, string profileRoot)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        if (CustomCommandRoots.SameFolder(roots.Of(scope), profileRoot))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.SharedFolder, NameOf(roots.Of(scope), file), scope);
        }

        return Relocate(roots.Of(scope), scope, file, profileRoot, CustomCommandScope.Profile, null, copy: false, CustomCommandEditOutcome.Moved);
    }

    /// <summary>The file copied into another profile's <c>commands</c> folder; the original stays.</summary>
    public static CustomCommandEditResult CopyToProfile(CustomCommandRoots roots, CustomCommandScope scope, string file, string profileRoot)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        if (CustomCommandRoots.SameFolder(roots.Of(scope), profileRoot))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.SharedFolder, NameOf(roots.Of(scope), file), scope);
        }

        return Relocate(roots.Of(scope), scope, file, profileRoot, CustomCommandScope.Profile, null, copy: true, CustomCommandEditOutcome.Copied);
    }

    /// <summary>
    /// A profile copy's one file (<c>/profile push|pull commands</c>): <paramref name="file"/> (under <paramref name="sourceRoot"/>) copied byte
    /// for byte to <paramref name="relative"/> under <paramref name="targetRoot"/>, its folders made. One there already, any case, is
    /// <c>Exists</c> unless <paramref name="overwrite"/>, which replaces that one; a link on the way, or a target that is one, is <c>Linked</c>,
    /// never written through. The same checks as the pane's copy (<see cref="CopyChecked"/>, 2026-10-31, review P6-8).
    /// </summary>
    public static CustomCommandEditResult CopyInto(string sourceRoot, string file, string targetRoot, string relative, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentNullException.ThrowIfNull(relative);
        string name = CustomCommandName.FromRelativePath(relative) ?? relative;
        string target = Path.GetFullPath(Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnder(targetRoot, target))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.BadName, name, CustomCommandScope.Profile, target);
        }

        return CopyChecked(sourceRoot, CustomCommandScope.Profile, file, targetRoot, CustomCommandScope.Profile, target, name, overwrite, CustomCommandEditOutcome.Copied);
    }

    /// <summary>
    /// The one copy (2026-10-31, review P6-8: the pane's copy and the profile copy checked apart): the source there, a plain file and under its
    /// root; no link on the way to <paramref name="target"/> nor at it; a file there already in any case (<see cref="Existing"/>) <c>Exists</c>,
    /// or replaced with <paramref name="overwrite"/>; a file failure an outcome, a file made there since the check <c>Exists</c>.
    /// </summary>
    private static CustomCommandEditResult CopyChecked(string root, CustomCommandScope scope, string file, string toRoot, CustomCommandScope toScope, string target, string targetName, bool overwrite, CustomCommandEditOutcome done)
    {
        if (Refusal(root, scope, file, targetName) is { } refused)
        {
            return refused;
        }

        if (!RegularFiles.IsPlain(file))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.Failed, targetName, scope, file, CustomCommandText.NotAFileProblem);
        }

        try
        {
            if (LinkedOnTheWay(toRoot, target))
            {
                return new CustomCommandEditResult(CustomCommandEditOutcome.Linked, targetName, toScope, target);
            }

            if (Existing(toRoot, Path.GetRelativePath(toRoot, target)) is { } there)
            {
                if (!overwrite)
                {
                    return new CustomCommandEditResult(CustomCommandEditOutcome.Exists, targetName, toScope, there);
                }

                if (WorkingDirectory.IsLink(new FileInfo(there)) || Directory.Exists(there))
                {
                    return new CustomCommandEditResult(CustomCommandEditOutcome.Linked, targetName, toScope, there) { LinkIsFile = true };
                }

                target = there;   // the one there, in its own case, is what overwrite replaces
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
            return new CustomCommandEditResult(done, targetName, toScope, target);
        }
        catch (IOException ex) when (!overwrite && FileErrors.IsExists(ex))
        {
            // Only a name taken since the check is a clash (2026-11-02, review P13-8: any failure with the file there was, a partial copy on a
            // full disk too).
            return new CustomCommandEditResult(CustomCommandEditOutcome.Exists, targetName, toScope, target);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            if (!overwrite)
            {
                DeleteQuietly(target);   // a partial copy of this call's (none stood there: Existing said so)
            }

            return new CustomCommandEditResult(CustomCommandEditOutcome.Failed, targetName, toScope, target, ex.Message);
        }
    }

    /// <summary>A file this call made and couldn't finish, removed; a failure to remove it is left (the next scan reads it as it is).</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // Left behind.
        }
    }

    /// <summary>
    /// The new file's stream, never over one there (<see cref="FileMode.CreateNew"/>): a seam for the tests (2026-11-02, review P13-4), which
    /// hand back a stream whose write fails.
    /// </summary>
    internal static Func<string, Stream> OpenNew { get; set; } = static path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    /// <summary>The file deleted, through the bin when one is given (<see cref="FileDeletes.Remove"/>); a subfolder it empties goes too.</summary>
    public static CustomCommandEditResult Delete(CustomCommandRoots roots, CustomCommandScope scope, string file, IRecycleBin? bin)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(file);
        string root = roots.Of(scope);
        string name = NameOf(root, file);
        if (Refusal(root, scope, file, name) is { } refused)
        {
            return refused;
        }

        try
        {
            var way = FileDeletes.Remove(Path.GetFullPath(file), isDirectory: false, isLink: false, bin);
            RemoveEmptied(root, Path.GetDirectoryName(Path.GetFullPath(file))!);
            return new CustomCommandEditResult(CustomCommandEditOutcome.Deleted, name, scope, file) { Way = way };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.Failed, name, scope, file, ex.Message);
        }
    }

    /// <summary>
    /// The refusal a new name meets before anything is written, or null: not a valid name, a built-in's word, or a name <paramref name="root"/>
    /// holds already. The pane's slots ask this before their next question.
    /// </summary>
    public static CustomCommandEditOutcome? NameRefusal(string root, string name)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(name);
        if (!CustomCommandName.IsValid(name))
        {
            return CustomCommandEditOutcome.BadName;
        }

        if (CustomCommandCatalog.BuiltInWord(name) is not null)
        {
            return CustomCommandEditOutcome.BuiltIn;
        }

        return Taken(root, name) ? CustomCommandEditOutcome.Exists : null;
    }

    /// <summary>Whether <paramref name="root"/> holds <paramref name="name"/>'s file already, any case (<see cref="Existing"/>).</summary>
    public static bool Taken(string root, string name)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(name);
        return Existing(root, CustomCommandName.RelativePath(name)) is not null;
    }

    /// <summary>
    /// The entry at <paramref name="relative"/> under <paramref name="root"/> found case-blind segment by segment, its path as it is on disk,
    /// or null (2026-10-31, review P5-5, P7-1: on a case-sensitive volume <c>Front/card.md</c> and <c>front/card.md</c> are two paths for one
    /// command name, and only the stem was compared). Names are lower case, so any case of a path is the one command. A folder on the way
    /// must be a folder; the last segment may be a file or a folder (either blocks a write).
    /// </summary>
    public static string? Existing(string root, string relative)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relative);
        string[] segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        string current = root;
        try
        {
            for (int i = 0; i < segments.Length; i++)
            {
                if (!Directory.Exists(current))
                {
                    return null;
                }

                // The entry as the disk spells it: the exact spelling first (a case-sensitive volume may hold both), else any case.
                bool last = i == segments.Length - 1;
                var matches = Directory.EnumerateFileSystemEntries(current)
                    .Where(e => string.Equals(Path.GetFileName(e), segments[i], StringComparison.OrdinalIgnoreCase) && (last || Directory.Exists(e)))
                    .ToList();
                if (matches.Count == 0)
                {
                    return null;
                }

                current = matches.FirstOrDefault(e => string.Equals(Path.GetFileName(e), segments[i], StringComparison.Ordinal)) ?? matches[0];
            }

            return current;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>The command name <paramref name="file"/> has under <paramref name="root"/> (its raw name when it's no valid one).</summary>
    public static string NameOf(string root, string file)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            return CustomCommandName.RawName(Path.GetRelativePath(root, file));
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        }
    }

    private static CustomCommandEditResult Write(string root, CustomCommandScope scope, string name, string content, CustomCommandEditOutcome done, bool mayReadDifferently)
    {
        if (NameRefusal(root, name) is { } refused)
        {
            return new CustomCommandEditResult(refused, name, scope);
        }

        string target = Path.Combine(root, CustomCommandName.RelativePath(name));
        if (LinkedOnTheWay(root, target))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.Linked, name, scope, target);
        }

        // Whether this call made the file (2026-11-02, review P13-4: a write that failed after the create read as "exists", left a cut
        // command for the next scan, and every retry said "exists").
        bool created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            byte[] bytes = Utf8NoBom.GetBytes(content.Replace("\r\n", "\n", StringComparison.Ordinal));
            // CreateNew: a file that appeared since the check is never replaced.
            using (var stream = OpenNew(target))
            {
                created = true;
                stream.Write(bytes);
            }

            return new CustomCommandEditResult(done, name, scope, target) { MayReadDifferently = mayReadDifferently };
        }
        catch (IOException ex) when (!created && (FileErrors.IsExists(ex) || File.Exists(target)))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.Exists, name, scope, target);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            if (created)
            {
                DeleteQuietly(target);
            }

            return new CustomCommandEditResult(CustomCommandEditOutcome.Failed, name, scope, target, ex.Message);
        }
    }

    // A rename (newName set), a move or a copy (the same name, another root): the file's bytes kept.
    private static CustomCommandEditResult Relocate(string root, CustomCommandScope scope, string file, string toRoot, CustomCommandScope toScope, string? newName, bool copy, CustomCommandEditOutcome done)
    {
        string name = NameOf(root, file);
        if (Refusal(root, scope, file, name) is { } refused)
        {
            return refused;
        }

        string target = newName is null
            ? Path.Combine(toRoot, Path.GetRelativePath(root, Path.GetFullPath(file)))
            : Path.Combine(toRoot, CustomCommandName.RelativePath(newName));
        string targetName = newName ?? name;
        // By the volume's case rule (2026-10-31, review P2-8): on a case-sensitive one Review.md → review.md is a rename.
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(file), CaseRule.Comparison(root)))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.NothingToChange, targetName, toScope, file);
        }

        if (copy)
        {
            return CopyChecked(root, scope, file, toRoot, toScope, target, targetName, overwrite: false, done);
        }

        if (newName is not null && NameRefusal(toRoot, newName) is { } bad)
        {
            return new CustomCommandEditResult(bad, newName, toScope);
        }

        if (newName is null && Taken(toRoot, name))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.Exists, targetName, toScope, target);
        }

        if (LinkedOnTheWay(toRoot, target))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.Linked, targetName, toScope, target);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: false);
            RemoveEmptied(root, Path.GetDirectoryName(Path.GetFullPath(file))!);

            return new CustomCommandEditResult(done, targetName, toScope, target);
        }
        catch (IOException ex) when (FileErrors.IsExists(ex))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.Exists, targetName, toScope, target);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // A move across volumes copies, then deletes the source (2026-11-02, review P13-8): when that delete failed the command stood in
            // both places and read as "exists already". The copy goes, so it stays where it was, and the failure is said as one.
            if (File.Exists(file) && File.Exists(target))
            {
                DeleteQuietly(target);
            }

            return new CustomCommandEditResult(CustomCommandEditOutcome.Failed, targetName, toScope, target, ex.Message);
        }
    }

    // The checks every act on an existing file makes: there, a plain file (no link), under its folder.
    private static CustomCommandEditResult? Refusal(string root, CustomCommandScope scope, string file, string name)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || !IsUnder(root, info.FullName))
            {
                return new CustomCommandEditResult(CustomCommandEditOutcome.Missing, name, scope, file);
            }

            if (WorkingDirectory.IsLink(info))
            {
                return new CustomCommandEditResult(CustomCommandEditOutcome.Linked, name, scope, file) { LinkIsFile = true };
            }

            if (LinkedOnTheWay(root, info.FullName))
            {
                return new CustomCommandEditResult(CustomCommandEditOutcome.Linked, name, scope, file);
            }

            return null;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new CustomCommandEditResult(CustomCommandEditOutcome.Failed, name, scope, file, ex.Message);
        }
    }

    // Whether a folder between root (not counted: it may be a link) and the file at target is a link.
    private static bool LinkedOnTheWay(string root, string target)
    {
        string top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? folder = Path.GetDirectoryName(Path.GetFullPath(target));
        while (folder is not null && folder.Length > top.Length && IsUnder(top, folder))
        {
            if (Directory.Exists(folder) && WorkingDirectory.RealLinkTarget(folder) is not null)
            {
                return true;
            }

            folder = Path.GetDirectoryName(folder);
        }

        return false;
    }

    // Each folder from folder up to (not including) root removed while it is empty and no link.
    private static void RemoveEmptied(string root, string folder)
    {
        string top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? current = Path.TrimEndingDirectorySeparator(folder);
        while (current is not null && current.Length > top.Length && IsUnder(top, current))
        {
            try
            {
                if (!Directory.Exists(current) || WorkingDirectory.RealLinkTarget(current) is not null || Directory.EnumerateFileSystemEntries(current).Any())
                {
                    return;
                }

                Directory.Delete(current);
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                return;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    // Strictly under root, the root itself not (2026-10-31, review P2-8: the sandbox's own test, so a root at a drive's root, E:\, holds its files).
    private static bool IsUnder(string root, string path)
    {
        string top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return !string.Equals(top, full, CaseRule.Comparison(top)) && WorkingDirectory.IsInside(top, full);
    }

    private static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}
