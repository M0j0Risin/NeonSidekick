namespace NeonSidekick.Files;

/// <summary>
/// <c>/cwd</c>'s path list (2026-10-05, the user's ask: <c>/cwd D:\</c> lists the folders under <c>D:\</c>): the folders of
/// any local drive, not the sandbox's — <c>/cwd</c> takes a full path, and <see cref="WorkingDirectory.Complete"/> reaches
/// nothing outside the working directory. The typed text up to its last <c>\</c> or <c>/</c> is the folder, kept as typed so
/// a pick replaces the whole argument; the rest is a name prefix. One level only (no subtree walk, unlike the <c>@</c> list:
/// a drive's tree is too big to walk per keystroke), folders only, by name ignoring case, each row the folder text, the
/// name and the separator the user typed last (<c>\</c> unless a <c>/</c>), so the folder mode sees a folder. A bare
/// <c>X:</c> offers <c>X:\</c> when that drive is there. What is hidden follows the folder picker's
/// <see cref="FileBrowserVisibility"/> (<see cref="FileSystemFolders.IsShown(DirectoryInfo)"/>), so the list and
/// <c>/cwd browse</c> show the same folders. A UNC path and a mapped network drive list nothing: the list is read on the
/// input row's thread at each keystroke, and a slow share would stall typing. A folder that is missing, a file or
/// unreadable lists nothing.
/// </summary>
public static class FolderCompleter
{
    /// <summary>The rows <paramref name="typed"/> completes to; at most <see cref="WorkingDirectory.MaxMentionMatches"/>, the read stopping after <see cref="WorkingDirectory.MaxMentionVisited"/> folders (either cut sets <c>Truncated</c>).</summary>
    public static MentionResult Complete(string typed, FileBrowserVisibility visibility = FileBrowserVisibility.Default)
    {
        string text = (typed ?? "").TrimStart();
        if (!IsDrivePath(text))
        {
            return Nothing;
        }

        string root = char.ToUpperInvariant(text[0]) + @":\";
        if (IsNetworkDrive(root))
        {
            return Nothing;
        }

        if (text.Length == 2)
        {
            return Directory.Exists(root) ? new MentionResult(FileOutcome.Ok, [text + @"\"], false) : Nothing;
        }

        int cut = text.LastIndexOfAny(['\\', '/']);
        string folder = text[..(cut + 1)];
        string prefix = text[(cut + 1)..];
        char separator = text[cut];
        try
        {
            var directory = new DirectoryInfo(folder);
            if (!directory.Exists)
            {
                return Nothing;
            }

            var names = new List<string>();
            bool truncated = false;
            int seen = 0;
            foreach (var child in directory.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None }))   // IsShown decides what is hidden
            {
                if (++seen > WorkingDirectory.MaxMentionVisited)
                {
                    truncated = true;
                    break;
                }

                if (child.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && (visibility == FileBrowserVisibility.ShowHidden || FileSystemFolders.IsShown(child)))
                {
                    names.Add(child.Name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            if (names.Count > WorkingDirectory.MaxMentionMatches)
            {
                names.RemoveRange(WorkingDirectory.MaxMentionMatches, names.Count - WorkingDirectory.MaxMentionMatches);
                truncated = true;
            }

            return new MentionResult(FileOutcome.Ok, names.Select(name => folder + name + separator).ToList(), truncated);
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return new MentionResult(FileOutcome.Failed, [], false, ex.Message);
        }
    }

    /// <summary>Whether <paramref name="text"/> starts as a drive path: a letter and a colon, then nothing or a separator. Pure.</summary>
    public static bool IsDrivePath(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text.Length == 2 || text[2] is '\\' or '/');
    }

    private static readonly MentionResult Nothing = new(FileOutcome.Ok, [], false);

    private static bool IsNetworkDrive(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
