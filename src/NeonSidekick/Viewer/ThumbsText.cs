using System.Globalization;

namespace NeonSidekick.Viewer;

/// <summary>
/// The thumbnail browser's words (2026-10-04, <c>/view --thumbs</c> — <c>/thumbs</c> until later that day — and <c>/comfy thumbs</c>): the window's title, the line it shows with
/// nothing to show, and the chat's lines. Pinned.
/// </summary>
public static class ThumbsText
{
    /// <summary>What the window's title ends with, and its whole title before the folder has a picture. Pinned.</summary>
    public const string AppTitle = "NeonSidekick thumbnails";

    /// <summary>The <c>/comfy</c> argument that opens the window on the output folder, <see cref="ViewerText.ViewWord"/>'s twin. Pinned.</summary>
    public const string ThumbsWord = "thumbs";

    /// <summary>The argument-list note for <see cref="ThumbsWord"/>. Pinned.</summary>
    public const string ThumbsNote = "open a window of the output folder's pictures as thumbnails";

    /// <summary>
    /// The window's title: the selected picture, where it stands (the oldest 1, the grid's order) and the count —
    /// <c>0001.png — 3/12 · NeonSidekick thumbnails</c>; with none selected the folder and the count; with no picture the app's
    /// title and the folder. While a Del has armed the selected picture (<paramref name="deleteArmed"/>, 2026-10-05), the viewer's
    /// <see cref="ViewerText.DeleteArmedHint"/> takes the app title's place, as in the viewer's title. Pinned.
    /// </summary>
    public static string Title(string folder, int count, string? name, int position, bool deleteArmed = false)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (count == 0)
        {
            return $"{AppTitle} — {folder}";
        }

        return name is null
            ? $"{folder} — {Pictures(count)} · {AppTitle}"
            : $"{name} — {position.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} · {(deleteArmed ? ViewerText.DeleteArmedHint : AppTitle)}";
    }

    /// <summary>What the window says in its middle while the folder has no picture. Pinned.</summary>
    public static string Empty(string folder) => $"No pictures in {folder} yet";

    /// <summary>What a tile shows when its picture could not be read. Pinned.</summary>
    public const string Unreadable = "×";

    /// <summary>The transcript's line after the window opened (or was brought forward), <see cref="Keys"/> under it. Pinned.</summary>
    public static string Opened(string folder) => $"(🖼️ thumbnails of {folder})";

    /// <summary>The window's keys and mouse, the line under <see cref="Opened"/>. Pinned.</summary>
    public const string Keys = "(click shows a picture in the viewer · double-click or Enter opens it · arrows move · right-click for the picture menu · + − or Ctrl+wheel size · Del twice deletes · F5 refresh · F11 full screen · TAB terminal · ESC close)";

    /// <summary>The transcript's line after the toolbar's 🪟 closed the window (2026-10-04), <see cref="ViewerText.Closed"/>'s twin. Pinned.</summary>
    public const string Closed = "(🖼️ thumbnails closed)";

    /// <summary>The transcript's error when there is no window to open (not Windows). Pinned.</summary>
    public const string Unavailable = "The thumbnail browser needs Windows.";

    /// <summary>The transcript's error when the folder could not be opened. Pinned.</summary>
    public static string Failed(string folder, string detail) => $"Could not open the thumbnail browser on {folder}: {detail}";

    private static string Pictures(int count) => count == 1 ? "1 picture" : count.ToString(CultureInfo.InvariantCulture) + " pictures";
}
