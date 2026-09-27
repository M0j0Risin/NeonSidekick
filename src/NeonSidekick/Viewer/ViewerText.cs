using System.Globalization;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture viewer's words (2026-09-27): the window's title, the line it shows with nothing to show, the strip's
/// button and <c>/comfy view</c>'s lines. Pinned.
/// </summary>
public static class ViewerText
{
    /// <summary>What the window's title opens with, and its whole title before the folder has a picture. Pinned.</summary>
    public const string AppTitle = "NeonSidekick pictures";

    /// <summary>The label on the picture strip's rule, the button that opens the window (the ComfyUI output folder). Two spaces after
    /// the glyph (later on 2026-09-27, the user's call): Windows Terminal draws 🖼 wider than it counts, and one space let it touch the word. Pinned.</summary>
    public const string StripButton = "🖼  viewer";

    /// <summary>The <c>/comfy</c> argument that opens the window, as the strip's button does. Pinned.</summary>
    public const string ViewWord = "view";

    /// <summary>The argument-list note for <see cref="ViewWord"/>. Pinned.</summary>
    public const string ViewNote = "open a window that shows the output folder's newest picture and follows new ones";

    /// <summary>
    /// The window's title: the picture's name, where it stands in the folder, and whether the window is following the newest
    /// picture (<c>live</c>) or held on an older one (<c>paused</c>) — <c>0001.png — 3/12 (paused) · NeonSidekick pictures</c>.
    /// With no picture, the app's title and the folder. Pinned.
    /// </summary>
    public static string Title(string? name, int position, int count, bool live, string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (name is null || count == 0)
        {
            return $"{AppTitle} — {folder}";
        }

        return $"{name} — {position.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} ({(live ? "live" : "paused")}) · {AppTitle}";
    }

    /// <summary>What the window says in its middle while the folder has no picture. Pinned.</summary>
    public static string Waiting(string folder) => $"Waiting for pictures in {folder}";

    /// <summary>What the window says in its middle when the shown picture could not be read. Pinned.</summary>
    public static string Unreadable(string name) => $"{name} could not be read as a picture";

    /// <summary>The transcript's line after the window opened (or was brought forward). Pinned.</summary>
    public static string Opened(string folder) => $"(🖼 picture viewer on {folder}: ← → browse, Home/End, F11 or a double-click for full screen, Esc to close)";

    /// <summary>The transcript's error when there is no window to open (not Windows). Pinned.</summary>
    public const string Unavailable = "The picture viewer needs Windows.";

    /// <summary>The transcript's error when the output folder could not be made or opened. Pinned.</summary>
    public static string Failed(string folder, string detail) => $"Could not open the picture viewer on {folder}: {detail}";
}
