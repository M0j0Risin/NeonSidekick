using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// What every window of the app's own does to its frame (2026-10-02, moved out of <see cref="PictureWindowThread"/> when
/// the log window joined it, so the two cannot drift): full screen and back (F11, a double-click, Esc), the place it last
/// closed restored and remembered, the theme on the title bar through DWM, and the two ways of coming forward — taking the
/// keyboard from the terminal or not. One per window, used on its thread only; <see cref="Window"/> is set once the window
/// is made, and every call before that does nothing.
/// </summary>
internal sealed unsafe class WindowChrome(string owner)
{
    private IntPtr _savedStyle;
    private WindowPlacement _savedPlacement;
    private ViewerStyle? _caption;

    /// <summary>The window, once made.</summary>
    public IntPtr Window { get; set; }

    /// <summary>Whether the window is full screen (<see cref="SetFullScreen"/>).</summary>
    public bool FullScreen { get; private set; }

    /// <summary>Borderless over the whole monitor and back to the placement it had (FolderPictureViewer's F11).</summary>
    public void SetFullScreen(bool on)
    {
        if (on == FullScreen || Window == IntPtr.Zero)
        {
            return;
        }

        if (on)
        {
            _savedStyle = GetWindowLongPtr(Window, GwlStyle);
            var placement = new WindowPlacement { length = (uint)sizeof(WindowPlacement) };
            GetWindowPlacement(Window, &placement);
            _savedPlacement = placement;
            var monitor = new MonitorInfo { cbSize = (uint)sizeof(MonitorInfo) };
            GetMonitorInfoW(MonitorFromWindow(Window, MonitorDefaultToNearest), &monitor);
            SetWindowLongPtr(Window, GwlStyle, new IntPtr((long)(WsPopup | WsVisible)));
            var r = monitor.rcMonitor;
            SetWindowPos(Window, HwndTop, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SwpFrameChanged | SwpShowWindow);
        }
        else
        {
            SetWindowLongPtr(Window, GwlStyle, _savedStyle);
            var placement = _savedPlacement;
            SetWindowPlacement(Window, &placement);
            SetWindowPos(Window, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
        }

        FullScreen = on;
    }

    /// <summary>
    /// The window, not yet shown, moved to where the last one closed (<paramref name="position"/>, the app's hook): its
    /// placement's restored rectangle carried to the corner at its own size, the show state left hidden for the ShowWindow
    /// that follows. SetWindowPlacement keeps a window that would land off every monitor on one, so a corner saved on a
    /// monitor since unplugged still opens in view.
    /// </summary>
    public void RestorePosition(Func<(int X, int Y)?>? position)
    {
        (int X, int Y)? at;
        try
        {
            at = position?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not read the {owner}'s last position: {ex.Message}");
            return;
        }

        var placement = new WindowPlacement { length = (uint)sizeof(WindowPlacement) };
        if (at is not { } corner || Window == IntPtr.Zero || !GetWindowPlacement(Window, &placement))
        {
            return;
        }

        var r = placement.rcNormalPosition;
        placement.rcNormalPosition = new Rect { Left = corner.X, Top = corner.Y, Right = corner.X + (r.Right - r.Left), Bottom = corner.Y + (r.Bottom - r.Top) };
        placement.flags = 0;
        placement.showCmd = (uint)SwHide;
        SetWindowPlacement(Window, &placement);
    }

    /// <summary>
    /// Where the window is as it closes, for the next one (<paramref name="placed"/>, the app's hook): the restored placement's
    /// corner — the one saved before F11 when it is full screen — never the maximized or minimized frame.
    /// </summary>
    public void RememberPosition(Action<int, int>? placed)
    {
        if (placed is null || Window == IntPtr.Zero)
        {
            return;
        }

        var placement = _savedPlacement;
        if (!FullScreen)
        {
            placement = new WindowPlacement { length = (uint)sizeof(WindowPlacement) };
            if (!GetWindowPlacement(Window, &placement))
            {
                return;
            }
        }

        try
        {
            placed(placement.rcNormalPosition.Left, placement.rcNormalPosition.Top);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not keep the {owner}'s position: {ex.Message}");
        }
    }

    /// <summary>
    /// <paramref name="style"/>'s bar on the window through DWM; false (and nothing done) when it is the bar it has. The
    /// HRESULTs are ignored: an older Windows refuses the colours and keeps its bar.
    /// </summary>
    public bool ApplyCaption(ViewerStyle style)
    {
        if (_caption == style || Window == IntPtr.Zero)
        {
            return false;
        }

        _caption = style;
        int dark = 1;
        uint caption = style.Caption, text = style.CaptionText, border = style.Border;
        DwmSetWindowAttribute(Window, DwmwaUseImmersiveDarkMode, &dark, sizeof(int));
        DwmSetWindowAttribute(Window, DwmwaCaptionColor, &caption, sizeof(uint));
        DwmSetWindowAttribute(Window, DwmwaTextColor, &text, sizeof(uint));
        DwmSetWindowAttribute(Window, DwmwaBorderColor, &border, sizeof(uint));
        return true;
    }

    /// <summary>Whether the window wears the theme: <see cref="PictureWindow.Themed"/>, the one switch for every window, read safely.</summary>
    public bool Themed()
    {
        try
        {
            return PictureWindow.Themed();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Viewer", $"Could not read Themed external windows: {ex.Message}");
            return true;
        }
    }

    /// <summary>The window sized to <paramref name="width"/> × <paramref name="height"/> at 96 DPI, scaled for the monitor it is on, without moving or activating it.</summary>
    public void SizeForDpi(int width, int height)
    {
        uint dpi = Math.Max(96u, GetDpiForWindow(Window));
        SetWindowPos(Window, HwndTop, 0, 0, (int)(width * dpi / 96), (int)(height * dpi / 96), SwpNoMove | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>Above the other windows without the keyboard (2026-10-02): the camera pane in the terminal keeps its keys.</summary>
    public void RaiseQuietly()
    {
        SetWindowPos(Window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        SetWindowPos(Window, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// In front of the terminal. Windows lets only the foreground thread take the foreground, and that is the terminal's,
    /// not ours (the click reached it, not us): the input is shared with it for the call, and a moment on top covers the
    /// case where even that is refused.
    /// </summary>
    public void BringForward()
    {
        if (IsIconic(Window))
        {
            ShowWindow(Window, SwRestore);
        }

        IntPtr foreground = GetForegroundWindow();
        uint theirs = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint ours = GetCurrentThreadId();
        bool attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        try
        {
            SetWindowPos(Window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
            SetWindowPos(Window, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
            BringWindowToTop(Window);
            SetForegroundWindow(Window);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(ours, theirs, false);
            }
        }
    }
}
