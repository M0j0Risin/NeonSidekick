using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Screen;

/// <summary>
/// The user32/gdi32/dwmapi/kernel32 imports behind <see cref="WindowsScreenSystem"/> (2026-10-04, <c>screen_capture</c>). One of
/// the Windows-only layers, <c>Viewer/ViewerNative</c>'s shape: source-generated <see cref="LibraryImportAttribute"/> over
/// blittable structs only, every handle an <see cref="IntPtr"/>, and the two enumeration callbacks
/// <c>[UnmanagedCallersOnly]</c> function pointers — no delegate is marshalled, so nothing behaves differently once published
/// (the smoke's <c>screen:gdi</c> and <c>screen:windows</c> prove it). GDI, not DXGI Desktop Duplication: the app has no D3D11
/// device, and a <c>BitBlt</c> from the screen DC (composed by DWM since Windows 8) into a top-down 32-bit DIB section gives the
/// tightly packed BGRX rows <c>CameraJpeg</c> already encodes. All four are system libraries: nothing joins
/// <c>SmokeChecks.RequiredNativeLibraries</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class ScreenNative
{
    public const uint SrcCopy = 0x00CC0020;

    /// <summary>Layered windows (tooltips, some overlays) come with the blit, as they are on the screen.</summary>
    public const uint CaptureBlt = 0x40000000;

    /// <summary><c>PrintWindow</c>'s flag for DWM-composed content (Windows 8.1+): a hardware-drawn window (a browser) is not black.</summary>
    public const uint PwRenderFullContent = 0x00000002;

    public const uint DibRgbColors = 0;
    public const uint MonitorInfoPrimary = 1;
    public const uint MonitorDefaultToNearest = 2;
    public const uint GaRootOwner = 3;
    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x00000080;
    public const int DwmwaExtendedFrameBounds = 9;
    public const int DwmwaCloaked = 14;
    public static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfoEx
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(IntPtr hdc, Rect* lprcClip, delegate* unmanaged<IntPtr, IntPtr, Rect*, IntPtr, int> lpfnEnum, IntPtr dwData);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, MonitorInfoEx* lpmi);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, Rect* lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, uint* lpdwProcessId);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetConsoleWindow();

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, void* pvAttribute, int cbAttribute);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateDIBSection(IntPtr hdc, BitmapInfoHeader* pbmi, uint usage, void** ppvBits, IntPtr hSection, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);
}
