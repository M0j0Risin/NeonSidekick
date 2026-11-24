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
    public const string Colour = "Color";
    public const string Grey = "Grayscale";
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

    /// <summary>The row that copies the picture into the profile's splash images (2026-11-23, the user's ask).</summary>
    public const string AddToSplash = "Add to the splash images";
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

    /// <summary>The chat's line for a picture the menu deleted: to the Recycle bin (the Trash on a Mac), or for good (later on 2026-10-15).</summary>
    public static string Deleted(string name, Files.DeleteWay way = Files.DeleteWay.Deleted) => way switch
    {
        Files.DeleteWay.Recycled => $"(🖼️ moved {name} to the {Files.FileText.RecycleBinName})",
        Files.DeleteWay.DeletedNoBin => $"(🖼️ deleted {name}: no {Files.FileText.RecycleBinName} on that drive)",
        _ => $"(🖼️ deleted {name})",
    };

    /// <summary>The chat's line for a path the menu copied.</summary>
    public static string Copied(string path) => $"(🖼️ copied {path})";

    /// <summary>The chat's line when the clipboard was busy.</summary>
    public const string CopyFailed = "Could not copy the path: the clipboard is busy";

    /// <summary>
    /// The chat's line after Add to the splash images (2026-11-23): the copy's name; the only picture there replaces the built-in ones; and
    /// while <c>Welcome splash</c> is <c>robot</c>, when they show.
    /// </summary>
    public static string AddedToSplash(string name, bool only, bool robot) =>
        $"(🖼️ added {name} to the splash images"
        + (only ? "; it's the only one, so it replaces the built-in pictures" : "")
        + (robot ? "; they show while Welcome splash is image" : "")
        + ")";

    /// <summary>A picture already in the splash folder (2026-11-23).</summary>
    public static string SplashAlready(string name) => $"{name} is already one of the splash images";

    /// <summary>A copy into the splash folder that failed on disk (2026-11-23).</summary>
    public static string SplashFailed(string name, string detail) => $"Could not add {name} to the splash images: {Diagnostics.LogText.Excerpt(detail)}";
}
