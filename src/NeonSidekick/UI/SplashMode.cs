using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.UI;

/// <summary>How the welcome splash is drawn (<see cref="SplashMode"/>).</summary>
public enum SplashStyle
{
    /// <summary>One picture, centred, as large as the transcript under the banner allows; Left / Right walk the set.</summary>
    FullSize,

    /// <summary>The pictures as thumbnails at <c>Image thumbnail size</c>, as many as fit on the screen; Left / Right page through them in sets.</summary>
    Tiled,

    /// <summary>No splash at startup (<c>/splash</c> still draws one).</summary>
    Disabled,
}

/// <summary>
/// The setting <c>Welcome splash</c> (2026-09-24, the user's ask: an on/off became three words): <c>fullsize</c>
/// (the one picture it always was, the default — the user's name for it over "normal"), <c>tiled</c> (a page of
/// thumbnails, paged by the arrows) and <c>disabled</c>, and their mapping to <see cref="SplashStyle"/> — the
/// <see cref="App.QueueCancelMode"/> shape. <see cref="Resolve"/> is the one place the saved string becomes the
/// enum: a hand-edited value that is none of them falls back to <see cref="Default"/> with a warning. The word
/// <c>fullsize</c> is spelled as <see cref="ThumbnailSize.FullSize"/> but is this setting's own.
/// </summary>
public static class SplashMode
{
    /// <summary>The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "fullsize";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "fullsize", "tiled", "disabled" };

    private const string Category = "Splash";

    /// <summary>Trims and ignores case; false (and <see cref="SplashStyle.FullSize"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out SplashStyle style)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "fullsize": style = SplashStyle.FullSize; return true;
            case "tiled": style = SplashStyle.Tiled; return true;
            case "disabled": style = SplashStyle.Disabled; return true;
            default: style = SplashStyle.FullSize; return false;
        }
    }

    /// <summary>The saved word for <paramref name="style"/>.</summary>
    public static string Name(SplashStyle style) => style switch
    {
        SplashStyle.Tiled => "tiled",
        SplashStyle.Disabled => "disabled",
        _ => "fullsize",
    };

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "fullsize" => "one picture fills the screen under the banner at startup; ← → walk them",
        "tiled" => "the pictures as thumbnails at the image thumbnail size, a screenful at a time; ← → page",
        "disabled" => "the banner alone at startup",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static SplashStyle Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.WelcomeSplashMode, out var style))
        {
            return style;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.WelcomeSplashMode)}='{effective.WelcomeSplashMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out style);
        return style;
    }
}
