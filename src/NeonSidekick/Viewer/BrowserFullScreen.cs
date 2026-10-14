namespace NeonSidekick.Viewer;

/// <summary>
/// The Browser window's full screen (2026-10-14, the user's ask and picks): <see cref="Window"/> is F11's own, which keeps the
/// navigation bar; <see cref="Page"/> is the page's (a video's full-screen button), which takes the window with it and puts the
/// bar aside so it does not sit over the video. Kept apart so a video's full screen begun and left inside F11's leaves F11's as
/// it was — the video window's single flag would drop it. Esc peels one at a time, the page's first; F11 leaves both. Pure; pinned.
/// </summary>
public readonly record struct BrowserFullScreen(bool Window, bool Page)
{
    /// <summary>Whether the window covers its monitor.</summary>
    public bool Covering => Window || Page;

    /// <summary>Whether the navigation bar shows: everywhere but the page's own full screen.</summary>
    public bool BarShown => !Page;

    /// <summary>F11: out of both when either is on, else F11's own on.</summary>
    public BrowserFullScreen Toggled() => Covering ? default : new(Window: true, Page: false);

    /// <summary>Esc: the page's full screen left first, then F11's.</summary>
    public BrowserFullScreen Left() => Page ? this with { Page = false } : default;

    /// <summary>The page entered or left its own full screen; F11's stays as it was.</summary>
    public BrowserFullScreen PageChanged(bool on) => this with { Page = on };
}
