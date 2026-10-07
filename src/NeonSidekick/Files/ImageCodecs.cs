namespace NeonSidekick.Files;

/// <summary>
/// Whether pictures can be decoded and encoded here (2026-10-06, the macOS build). Every picture the app reads or writes goes
/// through MagicScaler, and MagicScaler's codecs are Windows' own (WIC): elsewhere it has none, so a decode throws and every
/// caller already reads that as "could not be read". The macOS build leaves pictures out until it has a backend of its own
/// (ImageIO, or SkiaSharp); this is the one place that says so, so <see cref="ImageFile"/> can give the real reason and the
/// turn can leave <c>view_image</c>, <c>image_info</c> and <c>image_edit</c> off the model's list rather than offer tools that
/// always fail.
/// </summary>
public static class ImageCodecs
{
    /// <summary>True where MagicScaler has codecs: Windows.</summary>
    public static bool Available => OperatingSystem.IsWindows();

    /// <summary>Why a picture was not read off Windows. Pinned.</summary>
    public const string Unavailable = "pictures need Windows for now";
}
