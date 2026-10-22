using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.UI;

/// <summary>How the welcome splash is drawn (<see cref="SplashMode"/>).</summary>
public enum SplashStyle
{
    /// <summary>One picture, centered, as large as the transcript under the banner allows; Left / Right walk the set.</summary>
    Image,

    /// <summary>The animated robot (<see cref="Companion.RobotAnimator"/>) just above the draft line, following the mouse and the caret, until the first line takes it away.</summary>
    Robot,

    /// <summary>No splash at startup (<c>/splash</c> still draws the picture).</summary>
    Disabled,
}

/// <summary>
/// The setting <c>Welcome splash</c> (2026-09-24, the user's ask: an on/off became three words) and its mapping to
/// <see cref="SplashStyle"/> — the <see cref="App.QueueCancelMode"/> shape. <see cref="Resolve"/> is the one place the saved
/// string becomes the enum: a hand-edited value that is none of them falls back to <see cref="Default"/> with a warning.
/// On 2026-10-20 (the user's call) <c>tiled</c> (a page of thumbnails) was retired, <c>fullsize</c> became <c>image</c>, and
/// <c>robot</c> came in: the animated robot from the AnimationTrials proof of concept (<see cref="Companion.ICompanion"/>).
/// </summary>
public static class SplashMode
{
    /// <summary>The compiled default, pinned by <c>AppSettingsTests</c>: <c>off</c> since 2026-10-21 (the user's call), <c>image</c> before.</summary>
    public const string Default = "off";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "image", "robot", "off" };

    /// <summary>
    /// The retired words, still read: old → new (<see cref="RetiredWords"/>). <c>disabled</c> went on 2026-10-13 (the settings
    /// verbiage pass); <c>fullsize</c> and <c>tiled</c> on 2026-10-20 (the tiled splash is gone; its pictures show one at a time).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["disabled"] = "off",
        ["fullsize"] = "image",
        ["tiled"] = "image",
    };

    private const string Category = "Splash";

    /// <summary>Trims and ignores case; false (and <see cref="SplashStyle.Image"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out SplashStyle style)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "image": style = SplashStyle.Image; return true;
            case "robot": style = SplashStyle.Robot; return true;
            case "off": style = SplashStyle.Disabled; return true;
            default: style = SplashStyle.Image; return false;
        }
    }

    /// <summary>The saved word for <paramref name="style"/>.</summary>
    public static string Name(SplashStyle style) => style switch
    {
        SplashStyle.Robot => "robot",
        SplashStyle.Disabled => "off",
        _ => "image",
    };

    /// <summary>The menu hint next to a mode, the user's words since 2026-10-20 (the image hint's "← → step through them" went with it). Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "image" => "a picture fills the transcript pane at startup",
        "robot" => "an animated robot occupies the transcript pane at startup",
        "off" => "the transcript pane is clear at startup",
        _ => "",
    };

    /// <summary>
    /// The mode in force for <paramref name="effective"/>; an unknown saved value uses <see cref="Default"/> (<c>off</c> since 2026-10-21, so a misspelled
    /// value hides the splash) and warns, naming the value, once for each value (2026-10-22, the user's call after a review: the line it reads by,
    /// not one at every draw).
    /// </summary>
    public static SplashStyle Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.WelcomeSplashMode, out var style))
        {
            return style;
        }

        if (!string.Equals(Interlocked.Exchange(ref s_warned, effective.WelcomeSplashMode), effective.WelcomeSplashMode, StringComparison.Ordinal))
        {
            DiagnosticLog.Warn(Category,
                $"{nameof(AppSettingsData.WelcomeSplashMode)}='{effective.WelcomeSplashMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        TryParse(Default, out style);
        return style;
    }

    /// <summary>The unknown value warned about last: each is said once.</summary>
    private static string? s_warned;
}
