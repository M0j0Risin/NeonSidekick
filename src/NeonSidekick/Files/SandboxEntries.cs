namespace NeonSidekick.Files;

/// <summary>
/// The working directory behind <c>/browse</c>'s tree (2026-10-09, the user's ask: <c>/tree</c>'s reach, <c>/cwd browse</c>'s pane):
/// one root, the sandbox's, and under each folder its subfolders and then its files, each list in
/// <see cref="StringComparer.OrdinalIgnoreCase"/> order. What is listed follows <c>File browser/tree mode</c> as <c>/tree</c> does —
/// under <see cref="FileBrowserVisibility.Default"/> a hidden or system entry and every dot-name is left out
/// (<see cref="FileSystemFolders.IsShown(string, FileAttributes)"/>) — and a <c>.git</c> folder is left out either way, as
/// <c>/tree</c> leaves it. A link or junction is never listed (the walks' <c>ReparsePoint</c> skip), so the tree never shows a
/// name from outside the root; an open goes through <see cref="WorkingDirectory.Open"/> all the same. Access failures propagate:
/// the tree turns them into a denied node.
/// </summary>
public sealed class SandboxEntries : IFolderSource
{
    private readonly string _root;
    private readonly bool _showHidden;

    /// <param name="root">The sandbox's root, a full path (<see cref="WorkingDirectory.Root"/>).</param>
    /// <param name="visibility">What <c>File browser/tree mode</c> lists.</param>
    public SandboxEntries(string root, FileBrowserVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        _showHidden = visibility == FileBrowserVisibility.ShowHidden;
    }

    public IReadOnlyList<string> Roots() => [_root];

    public IReadOnlyList<string> Children(string path) =>
        List(new DirectoryInfo(path).EnumerateDirectories(), info => !string.Equals(info.Name, ".git", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> Files(string path) => List(new DirectoryInfo(path).EnumerateFiles(), _ => true);

    public bool Exists(string path) => Directory.Exists(path);

    private List<string> List<T>(IEnumerable<T> entries, Func<T, bool> keep)
        where T : FileSystemInfo
    {
        var list = new List<string>();
        foreach (var info in entries)
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || !keep(info))
            {
                continue;
            }

            if (_showHidden || FileSystemFolders.IsShown(info.Name, info.Attributes))
            {
                list.Add(info.FullName);
            }
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}
