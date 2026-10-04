namespace NeonSidekick.Files;

/// <summary>
/// Where the <c>/cwd browse</c> tree (<see cref="FolderTree"/>) reads folders from — the real
/// disks (<see cref="FileSystemFolders"/>) or a test's dictionary — so the tree and the pane are
/// proven without a filesystem.
/// </summary>
public interface IFolderSource
{
    /// <summary>The top-level entries: the ready drives (<c>C:\</c>, <c>D:\</c>…) on Windows, <c>/</c> elsewhere.</summary>
    IReadOnlyList<string> Roots();

    /// <summary>The subfolders of <paramref name="path"/> as full paths, in list order; throws when the folder cannot be read (the tree marks it denied).</summary>
    IReadOnlyList<string> Children(string path);

    /// <summary>Whether <paramref name="path"/> is a folder — listed by <see cref="Children"/> or not (a hidden one on the way to the working directory is shown anyway).</summary>
    bool Exists(string path);
}

/// <summary>
/// The disks behind <see cref="IFolderSource"/> (2026-09-21): the ready drives, or <c>/</c> off
/// Windows, and each folder's subfolders in <see cref="StringComparer.OrdinalIgnoreCase"/> order
/// (<see cref="WorkingDirectory"/>'s). Under <see cref="FileBrowserVisibility.Default"/> a folder
/// with the Hidden or System attribute, or a name starting with a dot, is left out — Explorer's
/// and Finder's default — so <c>$RECYCLE.BIN</c> and <c>System Volume Information</c> never
/// clutter a drive's list. Access failures propagate: the tree turns them into a denied node.
/// </summary>
public sealed class FileSystemFolders : IFolderSource
{
    private readonly bool _showHidden;

    public FileSystemFolders(FileBrowserVisibility visibility)
    {
        _showHidden = visibility == FileBrowserVisibility.ShowHidden;
    }

    public IReadOnlyList<string> Roots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [Path.DirectorySeparatorChar.ToString()];
        }

        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    roots.Add(drive.Name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that cannot say (a card reader with no card): not a root.
            }
        }

        return roots;
    }

    public IReadOnlyList<string> Children(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var children = new List<string>();
        foreach (var info in new DirectoryInfo(path).EnumerateDirectories())
        {
            if (_showHidden || IsShown(info))
            {
                children.Add(info.FullName);
            }
        }

        children.Sort(StringComparer.OrdinalIgnoreCase);
        return children;
    }

    public bool Exists(string path) => Directory.Exists(path);

    /// <summary>Whether <paramref name="info"/> is listed under <see cref="FileBrowserVisibility.Default"/>: neither hidden nor system, and not a dot-folder. Pure.</summary>
    public static bool IsShown(DirectoryInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return IsShown(info.Name, info.Attributes);
    }

    /// <summary><see cref="IsShown(DirectoryInfo)"/> on the name and the attributes. Pinned.</summary>
    public static bool IsShown(string name, FileAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(name);
        return (attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !name.StartsWith('.');
    }
}

/// <summary>
/// A named root above the drives (later on 2026-09-21, the user's ask): <paramref name="Label"/>
/// is what the row shows, <paramref name="Path"/> the folder it stands for, <paramref name="Glyph"/> the
/// one-cell mark drawn ahead of the label. The screen puts the profile's own <c>files</c> folder there
/// as <c>⌂ profile</c> and, since 2026-09-24 (the user's ask), the profile's <c>splash</c> folder under
/// it as <c>▣ splash</c> — its own glyph, the framed square, the user's pick.
/// </summary>
public readonly record struct FolderShortcut(string Label, string Path, string Glyph);

/// <summary>One folder in the <see cref="FolderTree"/>: a root at depth 0, its subfolders one deeper.</summary>
public sealed class FolderNode
{
    internal FolderNode(string path, string name, int depth, string? shortcutGlyph = null)
    {
        Path = path;
        Name = name;
        Depth = depth;
        ShortcutGlyph = shortcutGlyph;
    }

    /// <summary>A <see cref="FolderShortcut"/>'s root: <see cref="Name"/> is its label, drawn behind <see cref="ShortcutGlyph"/>.</summary>
    public bool IsShortcut => ShortcutGlyph is not null;

    /// <summary>The <see cref="FolderShortcut.Glyph"/> of a shortcut root; null for every other node.</summary>
    public string? ShortcutGlyph { get; }

    /// <summary>The full path, what a pick returns.</summary>
    public string Path { get; }

    /// <summary>The last segment; a root is its own name (<c>C:\</c>).</summary>
    public string Name { get; }

    /// <summary>How many folders above it: 0 for a root.</summary>
    public int Depth { get; }

    /// <summary>Its subfolders are listed under it.</summary>
    public bool Expanded { get; internal set; }

    /// <summary>The source could not read it (access denied, a vanished folder): never expanded, drawn dim.</summary>
    public bool Denied { get; internal set; }

    /// <summary>Its subfolders once read; null until the first expand.</summary>
    public IReadOnlyList<FolderNode>? Children => Loaded;

    /// <summary>Known to have nothing under it: read and found empty, or denied. Drawn with the leaf glyph.</summary>
    public bool IsLeaf => Denied || Loaded is { Count: 0 };

    internal List<FolderNode>? Loaded { get; set; }
}

/// <summary>
/// The folder tree under <c>/cwd browse</c> (2026-09-21), pure over an <see cref="IFolderSource"/>:
/// the roots at the top, each folder's subfolders read on its first expand and kept, and
/// <see cref="Visible"/> — the expanded tree flattened, one row per shown node — for the pane
/// to draw and index by row. Every operation takes a row of <see cref="Visible"/> and answers
/// the row the cursor belongs on after it.
/// </summary>
public sealed class FolderTree
{
    private readonly IFolderSource _source;
    private readonly List<FolderNode> _roots;
    private readonly List<FolderNode> _visible = new();
    private readonly StringComparison _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <param name="source">Where the folders come from.</param>
    /// <param name="shortcuts">Named roots listed above the source's, in the order given; none by default.</param>
    public FolderTree(IFolderSource source, IReadOnlyList<FolderShortcut>? shortcuts = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _roots = new List<FolderNode>();
        foreach (var shortcut in shortcuts ?? [])
        {
            _roots.Add(new FolderNode(shortcut.Path, shortcut.Label, 0, shortcut.Glyph));
        }

        foreach (var root in _source.Roots())
        {
            _roots.Add(new FolderNode(root, root, 0));
        }

        Rebuild();
    }

    /// <summary>The rows as the pane shows them: every root, and under an expanded node its children, depth first.</summary>
    public IReadOnlyList<FolderNode> Visible => _visible;

    /// <summary>
    /// Opens the node at <paramref name="row"/>: its subfolders are read on the first expand
    /// (a read that fails marks it <see cref="FolderNode.Denied"/>) and listed under it. True
    /// when the node is expanded after the call and has something under it; false for a denied
    /// or an empty folder, or a row off the list.
    /// </summary>
    public bool Expand(int row)
    {
        if (row < 0 || row >= _visible.Count)
        {
            return false;
        }

        var node = _visible[row];
        if (!Load(node) || node.Loaded!.Count == 0)
        {
            // Denied, or nothing under it: a leaf, never open.
            return false;
        }

        if (!node.Expanded)
        {
            node.Expanded = true;
            Rebuild();
        }

        return true;
    }

    /// <summary>Closes the node at <paramref name="row"/>; its read children are kept for the next expand.</summary>
    public void Collapse(int row)
    {
        if (row < 0 || row >= _visible.Count || !_visible[row].Expanded)
        {
            return;
        }

        _visible[row].Expanded = false;
        Rebuild();
    }

    /// <summary><see cref="Collapse"/> when the node at <paramref name="row"/> is open, else <see cref="Expand"/>; answers whether it is open after.</summary>
    public bool Toggle(int row)
    {
        if (row < 0 || row >= _visible.Count)
        {
            return false;
        }

        if (_visible[row].Expanded)
        {
            Collapse(row);
            return false;
        }

        Expand(row);
        return _visible[row].Expanded;
    }

    /// <summary>Closes every node; answers the row of the root the node at <paramref name="row"/> was under (0 for a row off the list).</summary>
    public int CollapseAll(int row)
    {
        int rootRow = RootOf(row);
        var root = rootRow < 0 ? null : _visible[rootRow];
        foreach (var node in _roots)
        {
            CollapseBelow(node);
        }

        Rebuild();
        // Roots are the first rows after the collapse, so the root's row is its index among the roots.
        return root is null ? 0 : _roots.IndexOf(root);
    }

    /// <summary>The row of the node one up from the node at <paramref name="row"/>; −1 for a root or a row off the list.</summary>
    public int ParentOf(int row)
    {
        if (row < 0 || row >= _visible.Count)
        {
            return -1;
        }

        int depth = _visible[row].Depth;
        for (int i = row - 1; i >= 0; i--)
        {
            if (_visible[i].Depth < depth)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Opens the way down to <paramref name="path"/> — every folder above it expanded — and
    /// answers its row; the deepest folder found when the path runs out (a drive not listed is
    /// row 0). A folder on the way the source does not list (hidden under the default mode, as
    /// the profile's <c>.neonsidekick</c> is) is added to its parent's list when it exists, so
    /// the working directory is always in view.
    /// </summary>
    public int ExpandTo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_roots.Count == 0)
        {
            return 0;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return 0;
        }

        // The first root the path is under — a shortcut ahead of the drive it lives on, so the
        // working directory under the profile's folder opens under the profile row.
        FolderNode? node = null;
        string rest = full;
        foreach (var root in _roots)
        {
            if (IsUnder(full, root.Path))
            {
                node = root;
                rest = full[root.Path.Length..];
                break;
            }
        }

        if (node is null)
        {
            Rebuild();
            return 0;
        }

        foreach (var segment in rest.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Load(node))
            {
                break;
            }

            var child = FindChild(node, segment);
            if (child is null)
            {
                string candidate = Path.Combine(node.Path, segment);
                if (!_source.Exists(candidate))
                {
                    break;
                }

                child = new FolderNode(candidate, segment, node.Depth + 1);
                Insert(node, child);
            }

            node.Expanded = true;
            node = child;
        }

        Rebuild();
        return _visible.IndexOf(node);
    }

    /// <summary>
    /// The type-ahead: the next row after <paramref name="row"/>, wrapping at the end, whose
    /// name starts with <paramref name="c"/> (case folded); −1 when no other row does.
    /// </summary>
    public int JumpFrom(int row, char c) => UI.TypeAhead.Next(_visible.Count, i => _visible[i].Name, row, c);

    /// <summary><paramref name="full"/> is <paramref name="root"/> or a folder under it — at a separator, so <c>C:\x\files2</c> is not under <c>C:\x\files</c>.</summary>
    private bool IsUnder(string full, string root)
    {
        if (!full.StartsWith(root, _comparison))
        {
            return false;
        }

        return full.Length == root.Length
            || root[^1] is var last && (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
            || full[root.Length] is var next && (next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar);
    }

    /// <summary>The row of the root above the node at <paramref name="row"/>; −1 for a row off the list.</summary>
    public int RootOf(int row)
    {
        if (row < 0 || row >= _visible.Count)
        {
            return -1;
        }

        int rootRow = 0;
        for (int i = 0; i <= row; i++)
        {
            if (_visible[i].Depth == 0)
            {
                rootRow = i;
            }
        }

        return rootRow;
    }

    // Reads the node's children once; false when it is denied (now or before).
    private bool Load(FolderNode node)
    {
        if (node.Denied)
        {
            return false;
        }

        if (node.Loaded is not null)
        {
            return true;
        }

        try
        {
            var loaded = new List<FolderNode>();
            foreach (var path in _source.Children(node.Path))
            {
                loaded.Add(new FolderNode(path, NameOf(path), node.Depth + 1));
            }

            node.Loaded = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            node.Denied = true;
            node.Loaded = null;
            return false;
        }
    }

    private FolderNode? FindChild(FolderNode node, string name)
    {
        foreach (var child in node.Loaded!)
        {
            if (string.Equals(child.Name, name, _comparison))
            {
                return child;
            }
        }

        return null;
    }

    // Keeps the parent's list in its order: the child goes before the first name after it.
    private static void Insert(FolderNode parent, FolderNode child)
    {
        var list = parent.Loaded!;
        int at = 0;
        while (at < list.Count && StringComparer.OrdinalIgnoreCase.Compare(list[at].Name, child.Name) < 0)
        {
            at++;
        }

        list.Insert(at, child);
    }

    private static void CollapseBelow(FolderNode node)
    {
        node.Expanded = false;
        if (node.Loaded is { } children)
        {
            foreach (var child in children)
            {
                CollapseBelow(child);
            }
        }
    }

    private void Rebuild()
    {
        _visible.Clear();
        foreach (var root in _roots)
        {
            Append(root);
        }
    }

    private void Append(FolderNode node)
    {
        _visible.Add(node);
        if (node.Expanded && node.Loaded is { } children)
        {
            foreach (var child in children)
            {
                Append(child);
            }
        }
    }

    private static string NameOf(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return name.Length == 0 ? path : name;
    }
}
