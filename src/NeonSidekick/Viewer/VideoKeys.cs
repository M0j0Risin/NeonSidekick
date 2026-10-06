namespace NeonSidekick.Viewer;

/// <summary>What a key does in the video window (<see cref="VideoKeys.ActionFor"/>).</summary>
public enum VideoKeyAction
{
    /// <summary>Not the window's: the player's own (Space, the arrows, F, M…), or <see cref="TerminalHandoff"/>'s for a chord.</summary>
    None,

    /// <summary>F11: the window full screen or back.</summary>
    ToggleFullScreen,

    /// <summary>Esc in full screen: back to the window.</summary>
    LeaveFullScreen,

    /// <summary>Esc in the window: closed, as the picture viewer and the log window do.</summary>
    Close,
}

/// <summary>
/// The video window's keys (2026-10-05): the picture viewer's rules where they mean the same — F11 for full screen, Esc out of
/// it and then closing — and the player's for the rest. YouTube's own keys (Space or K to pause, ← → to skip, F for its full
/// screen, M to mute) reach the player because WebView2 hands the window only accelerators (a key that types nothing, or one
/// with Ctrl or Alt held); a Ctrl or Alt chord goes to the chat (<see cref="TerminalHandoff"/>). TAB, which types a character,
/// stays the page's. Pure.
/// </summary>
public static class VideoKeys
{
    /// <summary>The key's action; <paramref name="fullScreen"/> is the window's or the page's own full screen.</summary>
    public static VideoKeyAction ActionFor(int virtualKey, bool fullScreen, bool control, bool alt) => virtualKey switch
    {
        _ when control || alt => VideoKeyAction.None,
        ViewerState.VkF11 => VideoKeyAction.ToggleFullScreen,
        ViewerState.VkEscape => fullScreen ? VideoKeyAction.LeaveFullScreen : VideoKeyAction.Close,
        _ => VideoKeyAction.None,
    };
}
