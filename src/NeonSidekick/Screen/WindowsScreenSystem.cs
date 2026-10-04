using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static NeonSidekick.Screen.ScreenNative;

namespace NeonSidekick.Screen;

/// <summary>
/// The screen through GDI (2026-10-04, <c>screen_capture</c>): monitors by <c>EnumDisplayMonitors</c>, windows by <c>EnumWindows</c>
/// (visible, titled, not a tool window, not cloaked — a UWP window parked on another virtual desktop is cloaked), an area by
/// <c>BitBlt</c> from the screen DC with <c>CAPTUREBLT</c>, a window by <c>PrintWindow(PW_RENDERFULLCONTENT)</c> into a DIB the size
/// of its rectangle, cut to its DWM frame (Windows 10's invisible resize borders left out), so it comes out whole when covered.
/// Every call runs per-monitor DPI aware on its thread and puts the thread's awareness back after, so coordinates and pixels
/// are physical whatever the console host is. Protected content (DRM video, some HDR surfaces) comes out black: Windows
/// keeps it from every screenshot. The app's own window is the terminal's (the console window's root owner, the
/// <c>Viewer/TerminalHandoff</c> way).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsScreenSystem : IScreenSystem
{
    /// <summary>The largest side a capture may have, guarding the DIB against a nonsense rectangle (16K: four 4K monitors side by side).</summary>
    public const int MaxSide = 16_384;

    public IReadOnlyList<ScreenMonitor> Monitors() => Aware(MonitorsCore);

    public IReadOnlyList<ScreenWindow> Windows() => Aware(WindowsCore);

    public long? OwnWindow()
    {
        IntPtr own = Own();
        return own == IntPtr.Zero ? null : own.ToInt64();
    }

    public int? OwnMonitor() => Aware<int?>(() =>
    {
        IntPtr own = Own();
        if (own == IntPtr.Zero)
        {
            return null;
        }

        IntPtr monitor = MonitorFromWindow(own, MonitorDefaultToNearest);
        var list = new List<(IntPtr Handle, ScreenMonitor Monitor)>();
        Enumerate(list);
        int at = list.FindIndex(m => m.Handle == monitor);
        return at < 0 ? null : list[at].Monitor.Number;
    });

    public ScreenFrame CaptureArea(ScreenRect area) => Aware(() => Blit(area));

    public ScreenFrame CaptureWindow(long id) => Aware(() =>
    {
        var hwnd = new IntPtr(id);
        if (!IsWindow(hwnd))
        {
            throw new ScreenException(ScreenText.WindowGone);
        }

        if (IsIconic(hwnd))
        {
            throw new ScreenException(ScreenText.WindowMinimized);
        }

        Rect outer;
        if (!GetWindowRect(hwnd, &outer))
        {
            throw new ScreenException(ScreenText.WindowGone);
        }

        var frame = Frame(hwnd, outer);
        int width = outer.Right - outer.Left;
        int height = outer.Bottom - outer.Top;
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
        {
            throw new ScreenException(ScreenText.WindowGone);
        }

        // The whole window rectangle drawn by the window itself, then cut to the frame DWM shows (the resize borders are invisible).
        byte[]? whole = Draw(width, height, dc => PrintWindow(hwnd, dc, PwRenderFullContent));
        if (whole is null)
        {
            // Some windows refuse WM_PRINT: what the screen shows of the frame instead (covered parts come out as what covers them).
            return Blit(frame);
        }

        return ScreenPixels.Crop(whole, width, new ScreenRect(frame.Left - outer.Left, frame.Top - outer.Top, frame.Width, frame.Height));
    });

    /// <summary>The terminal's top-level window: the console window's root owner (Windows Terminal), else the console window itself.</summary>
    private static IntPtr Own()
    {
        IntPtr console = GetConsoleWindow();
        if (console == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr root = GetAncestor(console, GaRootOwner);
        return root != IntPtr.Zero && IsWindowVisible(root) ? root : console;
    }

    private static T Aware<T>(Func<T> body)
    {
        IntPtr old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            return body();
        }
        finally
        {
            if (old != IntPtr.Zero)
            {
                _ = SetThreadDpiAwarenessContext(old);
            }
        }
    }

    private static IReadOnlyList<ScreenMonitor> MonitorsCore()
    {
        var list = new List<(IntPtr Handle, ScreenMonitor Monitor)>();
        Enumerate(list);
        if (list.Count == 0)
        {
            throw new ScreenException(ScreenText.NoMonitors);
        }

        return list.Select(m => m.Monitor).ToList();
    }

    private static void Enumerate(List<(IntPtr Handle, ScreenMonitor Monitor)> list)
    {
        var handle = GCHandle.Alloc(list);
        try
        {
            _ = EnumDisplayMonitors(IntPtr.Zero, null, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(IntPtr monitor, IntPtr dc, Rect* clip, IntPtr data)
    {
        var list = (List<(IntPtr, ScreenMonitor)>)GCHandle.FromIntPtr(data).Target!;
        var info = new MonitorInfoEx { cbSize = (uint)sizeof(MonitorInfoEx) };
        if (GetMonitorInfo(monitor, &info))
        {
            var r = info.rcMonitor;
            string name = new string(info.szDevice).TrimEnd('\0');
            list.Add((monitor, new ScreenMonitor(list.Count + 1, name, new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), (info.dwFlags & MonitorInfoPrimary) != 0)));
        }

        return 1;
    }

    private static IReadOnlyList<ScreenWindow> WindowsCore()
    {
        var handles = new List<IntPtr>();
        var handle = GCHandle.Alloc(handles);
        try
        {
            _ = EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        var windows = new List<ScreenWindow>();
        var names = new Dictionary<uint, string>();
        foreach (IntPtr hwnd in handles)
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
            {
                continue;
            }

            int cloaked = 0;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                continue;
            }

            string title = Title(hwnd);
            Rect outer;
            if (title.Length == 0 || !GetWindowRect(hwnd, &outer))
            {
                continue;
            }

            var frame = Frame(hwnd, outer);
            if (frame.Width <= 1 || frame.Height <= 1)
            {
                continue;
            }

            uint pid = 0;
            _ = GetWindowThreadProcessId(hwnd, &pid);
            windows.Add(new ScreenWindow(hwnd.ToInt64(), title, ProcessName(pid, names), frame));
        }

        return windows;
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(IntPtr hwnd, IntPtr data)
    {
        ((List<IntPtr>)GCHandle.FromIntPtr(data).Target!).Add(hwnd);
        return 1;
    }

    private static string Title(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            int read = GetWindowText(hwnd, p, buffer.Length);
            return new string(buffer, 0, Math.Max(0, read)).Trim();
        }
    }

    private static string ProcessName(uint pid, Dictionary<uint, string> names)
    {
        if (names.TryGetValue(pid, out var known))
        {
            return known;
        }

        string name;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            name = "";
        }

        names[pid] = name;
        return name;
    }

    /// <summary>The window's visible frame (DWM's extended frame bounds), its rectangle when DWM will not say.</summary>
    private static ScreenRect Frame(IntPtr hwnd, Rect outer)
    {
        Rect frame;
        var r = DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, &frame, sizeof(Rect)) == 0 && frame.Right > frame.Left ? frame : outer;
        return new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private static ScreenFrame Blit(ScreenRect area)
    {
        if (area.Width <= 0 || area.Height <= 0 || area.Width > MaxSide || area.Height > MaxSide)
        {
            throw new ScreenException(ScreenText.Failed("the area is empty or too large"));
        }

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            throw new ScreenException(ScreenText.Failed("GetDC"));
        }

        try
        {
            byte[] pixels = Draw(area.Width, area.Height, dc => BitBlt(dc, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, SrcCopy | CaptureBlt))
                ?? throw new ScreenException(ScreenText.Failed("BitBlt"));
            return new ScreenFrame(area.Width, area.Height, pixels);
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// A top-down 32-bit DIB of <paramref name="width"/>×<paramref name="height"/> selected into a memory DC, drawn by
    /// <paramref name="paint"/>, copied out as BGRX rows; null when the paint reports failure.
    /// </summary>
    private static byte[]? Draw(int width, int height, Func<IntPtr, bool> paint)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            throw new ScreenException(ScreenText.Failed("CreateCompatibleDC"));
        }

        var header = new BitmapInfoHeader
        {
            biSize = (uint)sizeof(BitmapInfoHeader),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        void* bits;
        IntPtr bitmap = CreateDIBSection(dc, &header, DibRgbColors, &bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits is null)
        {
            _ = DeleteDC(dc);
            throw new ScreenException(ScreenText.Failed("CreateDIBSection"));
        }

        IntPtr old = SelectObject(dc, bitmap);
        try
        {
            if (!paint(dc))
            {
                return null;
            }

            _ = GdiFlush();
            var pixels = new byte[width * height * 4];
            new ReadOnlySpan<byte>(bits, pixels.Length).CopyTo(pixels);
            return pixels;
        }
        finally
        {
            _ = SelectObject(dc, old);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(dc);
        }
    }
}
