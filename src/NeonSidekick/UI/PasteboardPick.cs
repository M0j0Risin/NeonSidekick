namespace NeonSidekick.UI;

/// <summary>
/// What <see cref="MacClipboard"/> takes off a Mac's pasteboard (2026-10-07, pasting a picture on a Mac, the user's ask), apart from
/// the native calls so the tests pin it on any OS. Pure.
/// </summary>
public static class PasteboardPick
{
    /// <summary>A PNG on the pasteboard: a screenshot (⌃⇧⌘4), a browser's Copy Image.</summary>
    public const string PngType = "public.png";

    /// <summary>A TIFF on the pasteboard: Preview's and most AppKit apps' copy of a picture, often several sizes of it.</summary>
    public const string TiffType = "public.tiff";

    /// <summary>A file Finder copied (⌘C on it), one item each.</summary>
    public const string FileUrlType = "public.file-url";

    /// <summary>Plain text (<c>NSPasteboardTypeString</c>).</summary>
    public const string TextType = "public.utf8-plain-text";

    /// <summary>
    /// The files a Finder copy put on the pasteboard, one POSIX path a line — what a drop of them pastes, so a picture among them
    /// is attached by its path as a dropped one is (<see cref="Files.ImageFile.TryPastedPaths"/>), with its name; null when there is
    /// no file. Finder adds a TIFF of each file's icon beside the URLs, which is why a file wins over the picture: pasted, the
    /// icon would be attached instead of the photo. A URL that is not a <c>file:</c> one is left out.
    /// </summary>
    public static string? FilePaths(IEnumerable<string?> fileUrls)
    {
        ArgumentNullException.ThrowIfNull(fileUrls);
        var paths = new List<string>();
        foreach (string? url in fileUrls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                paths.Add(uri.LocalPath.Length > 1 ? uri.LocalPath.TrimEnd('/') : uri.LocalPath);   // a folder's URL ends in /
            }
        }

        return paths.Count == 0 ? null : string.Join('\n', paths);
    }

    /// <summary>
    /// The frame of a TIFF to paste: the one with the most pixels, the first of equals. Preview's TIFF of a Retina copy holds the
    /// picture at more than one size, and the first need not be the largest. -1 for none.
    /// </summary>
    public static int Largest(IReadOnlyList<(int Width, int Height)> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        int best = -1;
        long most = -1;
        for (int i = 0; i < frames.Count; i++)
        {
            long pixels = (long)Math.Max(0, frames[i].Width) * Math.Max(0, frames[i].Height);
            if (pixels > most)
            {
                (best, most) = (i, pixels);
            }
        }

        return best;
    }
}
