using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Viewer;

/// <summary>Where a video file goes (<see cref="VideoFilesPlayIn"/>).</summary>
public enum VideoFilesPlayInKind
{
    /// <summary>The app's video window: the default.</summary>
    VideoWindow,

    /// <summary>The Web UI's Video tab, on the user's phone or browser.</summary>
    WebUiWindow,

    /// <summary>The file's default app, through the shell.</summary>
    DefaultApp,
}

/// <summary>
/// The setting <c>Video files play in</c> (2026-11-12, the video-files plan; <see cref="YouTube.YouTubeOpenLinksIn"/>'s shape): <c>video-window</c>,
/// <c>webui-window</c> or <c>default-app</c>. It moves every file play: <c>/view</c> and <c>/video</c> of a video, and <c>play_video</c>.
/// <c>video-window</c> is the window YouTube's setting calls <c>youtube-window</c>; a file isn't YouTube, and the docs already name it the video
/// window. <c>webui-window</c> is YouTube's own word for the tab. <c>webui-window</c> falls back to the video window while the Web UI isn't
/// serving or its Video tab is off; <c>video-window</c> to the default app where the window can't play files (a Mac) or there is none. So every
/// fallback ends at the default app, and a file always has a place to play. <see cref="Resolve"/> is the one place the saved string becomes
/// the enum: a hand-edited value that is none of them reads as <see cref="Default"/>, with a warning once. Pure; pinned.
/// </summary>
public static class VideoFilesPlayIn
{
    public const string VideoWindow = "video-window";
    public const string WebUiWindow = "webui-window";
    public const string DefaultApp = "default-app";

    /// <summary>The video window: the compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = VideoWindow;

    /// <summary>The choices in menu order.</summary>
    public static readonly string[] Names = { VideoWindow, WebUiWindow, DefaultApp };

    private const string Category = "Video";

    /// <summary>The unknown value last warned of, so <see cref="Resolve"/> warns once per value (it is read at every play).</summary>
    private static string? s_warned;

    /// <summary>Trims and ignores case; false (and <see cref="VideoFilesPlayInKind.VideoWindow"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out VideoFilesPlayInKind kind)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case VideoWindow: kind = VideoFilesPlayInKind.VideoWindow; return true;
            case WebUiWindow: kind = VideoFilesPlayInKind.WebUiWindow; return true;
            case DefaultApp: kind = VideoFilesPlayInKind.DefaultApp; return true;
            default: kind = VideoFilesPlayInKind.VideoWindow; return false;
        }
    }

    /// <summary>The saved word for <paramref name="kind"/>.</summary>
    public static string Name(VideoFilesPlayInKind kind) => kind switch
    {
        VideoFilesPlayInKind.WebUiWindow => WebUiWindow,
        VideoFilesPlayInKind.DefaultApp => DefaultApp,
        _ => VideoWindow,
    };

    /// <summary>The menu hint next to a choice, with its fallback.</summary>
    public static string Describe(string name) => VideoText.PlayInHint(name);

    /// <summary>The choice in force for <paramref name="effective"/>; an unknown saved value warns once and uses <see cref="Default"/>.</summary>
    public static VideoFilesPlayInKind Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.VideoFilesPlayIn, out var kind))
        {
            return kind;
        }

        if (!string.Equals(Interlocked.Exchange(ref s_warned, effective.VideoFilesPlayIn), effective.VideoFilesPlayIn, StringComparison.Ordinal))
        {
            DiagnosticLog.Warn(Category, $"{nameof(AppSettingsData.VideoFilesPlayIn)}='{effective.VideoFilesPlayIn}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return VideoFilesPlayInKind.VideoWindow;
    }
}
