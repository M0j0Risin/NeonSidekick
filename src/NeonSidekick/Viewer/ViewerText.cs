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

    /// <summary>The button at the left of the picture strip's rule that opens the window (the ComfyUI output folder). The glyph
    /// alone since 2026-09-28 (the user's call: 🎞️, no word, at the rule's left with the close × at its right); it carries
    /// U+FE0F so Windows Terminal draws it as the two-cell emoji <c>TextCells</c> counts. Pinned.</summary>
    public const string StripButton = "🎞️";

    /// <summary>The <c>/comfy</c> argument that opens the window, as the strip's button does. Pinned.</summary>
    public const string ViewWord = "view";

    /// <summary>The argument-list note for <see cref="ViewWord"/>. Pinned.</summary>
    public const string ViewNote = "open a window that shows the output folder's newest picture and follows new ones";

    /// <summary>
    /// The window's title: the picture's name, where it stands in the folder, and whether the window is following the newest
    /// picture (<c>live</c>) or held on an older one (<c>paused</c>) — <c>0001.png — 3/12 (paused) · NeonSidekick pictures</c>.
    /// With no picture, the app's title and the folder. While a Del has armed the picture (<paramref name="deleteArmed"/>),
    /// <see cref="DeleteArmedHint"/> takes the app title's place, so it is not cut off a narrow title bar; while the slide
    /// show runs (<paramref name="slideSeconds"/> not null), <see cref="SlideShowTail"/> does. Pinned.
    /// </summary>
    public static string Title(string? name, int position, int count, bool live, string folder, bool deleteArmed = false, int? slideSeconds = null, bool shuffle = false)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (name is null || count == 0)
        {
            return $"{AppTitle} — {folder}";
        }

        string tail = deleteArmed ? DeleteArmedHint : slideSeconds is int seconds ? SlideShowTail(seconds, shuffle) : AppTitle;
        return $"{name} — {position.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} ({(live ? "live" : "paused")}) · {tail}";
    }

    /// <summary>The title's tail while the slide show runs (later on 2026-09-27): <c>▶ 3 s</c>, <c>▶ 3 s · random</c>. Pinned.</summary>
    public static string SlideShowTail(int seconds, bool shuffle) =>
        $"▶ {seconds.ToString(CultureInfo.InvariantCulture)} s{(shuffle ? " · random" : "")}";

    /// <summary>The title's tail after the first Del (2026-09-27): a second one deletes the picture. Pinned.</summary>
    public const string DeleteArmedHint = "Del again to delete";

    /// <summary>The log's line when a double-Del'd picture could not be deleted.</summary>
    public static string DeleteFailed(string name, string detail) => $"Could not delete {name}: {detail}";

    /// <summary>What the window says in its middle while the folder has no picture. Pinned.</summary>
    public static string Waiting(string folder) => $"Waiting for pictures in {folder}";

    /// <summary>What the window says in its middle when the shown picture could not be read. Pinned.</summary>
    public static string Unreadable(string name) => $"{name} could not be read as a picture";

    /// <summary>
    /// The transcript's line after the window opened (or was brought forward), <see cref="Keys"/> under it. The glyph carries
    /// U+FE0F (later on 2026-09-27, the user's catch): a bare 🖼 defaults to text presentation, which Windows Terminal draws two
    /// cells wide in one, so it ran into "picture" (<c>NoticeGlyphs</c>' rule). The keys moved to their own line the same day,
    /// so the path line no longer wraps mid-sentence. Pinned.
    /// </summary>
    public static string Opened(string folder) => $"(🖼️ picture viewer on {folder})";

    /// <summary>The viewer's keys, the line under <see cref="Opened"/> (later on 2026-09-27). Pinned.</summary>
    public const string Keys = "(← → browse · Home/End · F9 slide show · F10 random · ↑ ↓ slide time · F11 full screen · DEL twice delete · ESC close)";

    /// <summary>
    /// The <c>Image viewer</c> setting's word for the app Windows registers (2026-09-27, the user's call): an empty setting
    /// opens a double-clicked picture in this viewer, this word in the registered editor or viewer as before, any other text
    /// runs as a command. Matched ignoring case. Pinned.
    /// </summary>
    public const string SystemViewerWord = "system";

    /// <summary>The transcript's error when there is no window to open (not Windows). Pinned.</summary>
    public const string Unavailable = "The picture viewer needs Windows.";

    /// <summary>The transcript's error when the output folder could not be made or opened. Pinned.</summary>
    public static string Failed(string folder, string detail) => $"Could not open the picture viewer on {folder}: {detail}";
}
