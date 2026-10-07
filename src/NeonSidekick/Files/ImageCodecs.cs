namespace NeonSidekick.Files;

/// <summary>
/// Whether pictures can be decoded and encoded here (2026-10-06, the macOS build). Every picture the app reads or writes goes
/// through MagicScaler, whose own codecs are Windows' (WIC). Since 2026-10-07 a Mac has codecs too: Apple's ImageIO registered
/// behind MagicScaler (<see cref="Images.ImageIOCodecs"/>), so the same pipeline runs there. Elsewhere (Linux) there are none, a
/// decode throws, and every caller already reads that as "could not be read"; this is the one place that says so, so
/// <see cref="ImageFile"/> can give the real reason and the turn can leave <c>view_image</c>, <c>image_info</c> and
/// <c>image_edit</c> off the model's list rather than offer tools that always fail.
/// </summary>
public static class ImageCodecs
{
    /// <summary>True where MagicScaler has codecs: Windows (WIC), and a Mac once ImageIO is registered.</summary>
    public static bool Available => OperatingSystem.IsWindows() || Images.ImageIOCodecs.Registered;

    /// <summary>Why a picture was not read where there are no codecs (only Linux now, since 2026-10-07). Pinned.</summary>
    public const string Unavailable = "pictures need Windows or macOS for now";
}
