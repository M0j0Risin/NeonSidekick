using System.Globalization;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture menu's words (2026-10-04, <see cref="PictureMenu"/>): every row, and the chat's lines for what a row did. Pinned.
/// </summary>
public static class PictureMenuText
{
    public const string OpenInViewer = "Open in the viewer";
    public const string Rotate = "Rotate and flip";
    public const string RotateRight = "Rotate right 90°";
    public const string RotateLeft = "Rotate left 90°";
    public const string Rotate180 = "Rotate 180°";
    public const string FlipHorizontal = "Flip horizontally";
    public const string FlipVertical = "Flip vertically";
    public const string Colour = "Colour";
    public const string Grey = "Greyscale";
    public const string Sepia = "Sepia";
    public const string Negative = "Negative";
    public const string Polaroid = "Polaroid";
    public const string Resize = "Resize";
    public const string Half = "50%";
    public const string Quarter = "25%";
    public const string Convert = "Convert to";
    public const string Shrink = "Shrink the file";

    /// <summary>The lossless strip's row (2026-10-05): greyed for a picture that is not a JPEG, PNG, WebP or GIF.</summary>
    public const string StripMetadata = "Strip metadata (lossless)";
    public const string CopyPath = "Copy the path";
    public const string ShowInExplorer = "Show in Explorer";

    /// <summary>The same row on a Mac (2026-10-07): Finder opened on the picture.</summary>
    public const string ShowInFinder = "Show in Finder";
    public const string Attach = "Attach to the chat";
    public const string Print = "Print";
    public const string Delete = "Delete";

    /// <summary>A Fit preset's row: <c>Fit in 1920 px</c>.</summary>
    public static string Fit(int side) => "Fit in " + side.ToString(CultureInfo.InvariantCulture) + " px";

    /// <summary>A Shrink preset's row: <c>Under 2 MB</c>, <c>Under 500 KB</c>.</summary>
    public static string Under(int kb) => "Under " + (kb >= 1024 && kb % 1024 == 0 ? (kb / 1024).ToString(CultureInfo.InvariantCulture) + " MB" : kb.ToString(CultureInfo.InvariantCulture) + " KB");

    /// <summary>The menu's last row, never chosen: where edits go (<c>Image edit mode</c>), so an overwrite is never a surprise.</summary>
    public static string ModeNote(string mode) => "Edits: " + mode;

    /// <summary>The chat's line for an edit that worked: <see cref="Images.ImageText.Written"/>'s, in brackets.</summary>
    public static string Edited(string line) => $"(🖼️ {line})";

    /// <summary>The chat's line for an edit or an action that failed: the picture's name and the reason.</summary>
    public static string Failed(string name, string detail) => $"{name}: {detail}";

    /// <summary>The chat's line for a picture the menu deleted.</summary>
    public static string Deleted(string name) => $"(🖼️ deleted {name})";

    /// <summary>The chat's line for a path the menu copied.</summary>
    public static string Copied(string path) => $"(🖼️ copied {path})";

    /// <summary>The chat's line when the clipboard was busy.</summary>
    public const string CopyFailed = "Could not copy the path: the clipboard is busy";
}
