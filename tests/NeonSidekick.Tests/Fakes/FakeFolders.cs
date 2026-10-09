using NeonSidekick.Files;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// An <see cref="IFolderSource"/> over a dictionary: the roots, each folder's child names, the
/// folders that refuse a read. Paths are built with <see cref="Path.Combine"/> so the same test
/// runs over <c>C:\</c> and <c>/</c>. <see cref="Reads"/> counts the child reads, to prove the
/// tree reads a folder once.
/// </summary>
public sealed class FakeFolders : IFolderSource
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly List<string> _roots = new();
    private readonly Dictionary<string, List<string>> _children = new(PathComparer);
    private readonly HashSet<string> _denied = new(PathComparer);
    private readonly Dictionary<string, List<string>> _files = new(PathComparer);

    /// <summary>The platform's first root: <c>C:\</c> on Windows, <c>/</c> elsewhere.</summary>
    public static string RootPath => OperatingSystem.IsWindows() ? @"C:\" : "/";

    /// <summary>A second root on Windows (<c>D:\</c>); off Windows there is only one, so the same path.</summary>
    public static string SecondRootPath => OperatingSystem.IsWindows() ? @"D:\" : "/";

    public int Reads { get; private set; }

    public FakeFolders Root(string root)
    {
        if (!_roots.Contains(root, PathComparer))
        {
            _roots.Add(root);
        }

        return this;
    }

    /// <summary>Lists <paramref name="names"/> under <paramref name="parent"/> (in the order given), the parent registered as a folder.</summary>
    public FakeFolders Add(string parent, params string[] names)
    {
        if (!_children.TryGetValue(parent, out var list))
        {
            list = new List<string>();
            _children[parent] = list;
        }

        foreach (var name in names)
        {
            list.Add(name);
            _children.TryAdd(Path.Combine(parent, name), new List<string>());
        }

        return this;
    }

    /// <summary>Every read of <paramref name="path"/> throws, as an unreadable folder's does.</summary>
    public FakeFolders Deny(string path)
    {
        _denied.Add(path);
        _children.TryAdd(path, new List<string>());
        return this;
    }

    public IReadOnlyList<string> Roots() => _roots;

    public IReadOnlyList<string> Children(string path)
    {
        Reads++;
        if (_denied.Contains(path))
        {
            throw new UnauthorizedAccessException("Access to the path '" + path + "' is denied.");
        }

        var names = _children.GetValueOrDefault(path) ?? [];
        return names.Select(name => Path.Combine(path, name)).ToList();
    }

    public bool Exists(string path) => _children.ContainsKey(path) || _roots.Contains(path, PathComparer);

    /// <summary>Lists the files <paramref name="names"/> under <paramref name="parent"/> (in the order given): what a tree made <c>withFiles</c> reads (2026-10-09).</summary>
    public FakeFolders AddFiles(string parent, params string[] names)
    {
        if (!_files.TryGetValue(parent, out var list))
        {
            list = new List<string>();
            _files[parent] = list;
        }

        list.AddRange(names);
        return this;
    }

    public IReadOnlyList<string> Files(string path) => (_files.GetValueOrDefault(path) ?? []).Select(name => Path.Combine(path, name)).ToList();
}
