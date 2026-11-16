using System.Globalization;
using NeonSidekick.Files;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

// ── The 🗂️ sheet's Files tab (2026-11-16, plan lucid-roaming-lantern #41, the user's ask: open the files the model made from the phone) ──
// The list itself is Files/MadeFiles (fed from the checkpoint journal's hooks, per tool call). Here: today's roots, the serve check every
// listed row and every request passes, and the view. Read on a Kestrel thread: the list is lock-guarded and the folders are read live.
// No PC path reaches the page: a row's folder is relative to its root, and its url is built here.

internal sealed partial class ChatScreen
{
    /// <summary>The working folder and the images folder, as today's settings place them; the made files' list takes only writes under these.</summary>
    private IReadOnlyList<string> MadeFileRoots()
    {
        var roots = new List<string>(2);
        foreach (var directory in (WorkingDirectory[])[_files, _images])
        {
            try
            {
                roots.Add(directory.Root);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                // A setting that names no folder: nothing under it.
            }
        }

        return roots;
    }

    /// <summary>
    /// The serve check (2026-11-16, #41), at every request and for every listed row: <paramref name="full"/> under today's working or images
    /// folder, passing that root's read-side <see cref="WorkingDirectory.Resolve(string, bool, out string)"/> (no link out of it, no stream
    /// name). The folder it is in and the path it resolves to; null: refused, as an unknown token is.
    /// </summary>
    private (string Resolved, string Folder)? MadeFileServable(string full)
    {
        foreach (var (directory, prefix) in (ReadOnlySpan<(WorkingDirectory, string)>)[(_files, ""), (_images, "images")])
        {
            string root;
            try
            {
                root = directory.Root;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                continue;
            }

            if (!MadeFiles.IsUnder(full, root))
            {
                continue;
            }

            string relative = Path.GetRelativePath(root, full);
            if (directory.Resolve(relative, forWrite: false, out string resolved) != FileOutcome.Ok)
            {
                return null;
            }

            // Joined with '/' on every system: a phone sees out/sub, never out\sub.
            string folder = (Path.GetDirectoryName(relative) ?? "").Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            folder = prefix.Length == 0 ? folder : folder.Length == 0 ? prefix : prefix + "/" + folder;
            return (resolved, folder);
        }

        return null;
    }

    /// <summary>The Files tab's view (any thread, reads only): the made files still there and servable, newest first.</summary>
    private WebFilesView WebFilesNow()
    {
        var rows = new List<WebFileRow>();
        foreach (var file in _madeFiles.Listed(full => MadeFileServable(full) is { } served && File.Exists(served.Resolved)))
        {
            if (MadeFileServable(file.Full) is not { } served)
            {
                continue;
            }

            long length;
            try
            {
                length = new FileInfo(served.Resolved).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            string name = Path.GetFileName(file.Full);
            var type = MadeFileTypes.Of(name);
            rows.Add(new WebFileRow(name, served.Folder, FileText.Size(length), type.Kind.ToString().ToLowerInvariant(), MadeFileTypes.Glyph(type.Kind),
                WebUiHost.MadeFilePrefix + file.Token + "/" + Uri.EscapeDataString(name)));
        }

        return new WebFilesView(rows, _madeFiles.Trimmed);
    }

    /// <summary>A listed file's path past the serve check, for <paramref name="token"/> (a Kestrel thread); null: a 404. A file gone is the server's 410.</summary>
    private string? WebMadeFileOf(string token) =>
        _madeFiles.PathOf(token) is { } full && MadeFileServable(full) is { } served ? served.Resolved : null;

    /// <summary>
    /// The Files tab's tag: the list's version and today's two roots, hashed, since a <i>Working directory</i> change hides rows without
    /// touching the list.
    /// </summary>
    private string WebFilesTagNow() =>
        _madeFiles.Version.ToString(CultureInfo.InvariantCulture) + "-"
        + ((uint)WorkingDirectory.PathComparer.GetHashCode(string.Join('\n', MadeFileRoots()))).ToString("x", CultureInfo.InvariantCulture);
}
