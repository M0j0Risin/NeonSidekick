using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The native calls the Browser window's navigation bar adds to <see cref="ViewerNative"/>'s (2026-10-13): the address box, a stock
/// <c>EDIT</c> child (its caret, selection, IME and clipboard Windows' own), focus between it and the page, and the buttons' tooltips —
/// comctl32's <c>tooltips_class32</c>, the first common control the app makes, registered with <c>InitCommonControlsEx</c> (a system
/// library, always there, so no entry in <c>SmokeChecks.RequiredNativeLibraries</c>; <c>browser:webview2</c> makes both). No manifest
/// asks for common controls 6, so the tooltip is version 5's, which takes <see cref="ToolInfo"/> without its last field.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class BrowserBarNative
{
    public const uint WsChild = 0x40000000;
    public const uint EsAutoHScroll = 0x0080;
    public const uint WmSetFocus = 0x0007;
    public const uint WmSetFont = 0x0030;
    public const uint WmCommand = 0x0111;
    public const uint WmCtlColorEdit = 0x0133;
    public const uint EmSetSel = 0x00B1;
    public const uint EnSetFocus = 0x0100;
    public const uint EnChange = 0x0300;
    public const uint MbOk = 0x00000000;

    /// <summary>The address box's control id, the low word of its <c>WM_COMMAND</c>.</summary>
    public const int AddressId = 100;

    // The tooltip (commctrl.h).
    public const uint TtsAlwaysTip = 0x01;
    public const uint TtsNoPrefix = 0x02;
    public const uint TtfSubclass = 0x0010;
    public const uint TtmAddToolW = 0x0400 + 50;
    public const uint TtmNewToolRectW = 0x0400 + 52;
    public const uint TtmUpdateTipTextW = 0x0400 + 57;
    public const uint IccBarClasses = 0x00000004;
    public const uint IccTabClasses = 0x00000008;

    /// <summary>TOOLINFOW up to <c>lParam</c>: <c>TTTOOLINFOW_V2_SIZE</c>, what common controls 5 takes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ToolInfo
    {
        public uint cbSize;
        public uint uFlags;
        public IntPtr hwnd;
        public nuint uId;
        public Rect rect;
        public IntPtr hinst;
        public char* lpszText;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InitCommonControlsInfo
    {
        public uint dwSize;
        public uint dwICC;
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetFocus();

    [LibraryImport("user32.dll")]
    public static partial int GetWindowTextLengthW(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowTextW(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MessageBeep(uint uType);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCommonControlsEx(InitCommonControlsInfo* picce);

    /// <summary>A window's text; "" when it has none.</summary>
    public static string TextOf(IntPtr hwnd)
    {
        int length = GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            int read = GetWindowTextW(hwnd, text, buffer.Length);
            return new string(text, 0, Math.Max(0, read));
        }
    }

    /// <summary>The tooltip class registered; false when comctl32 would not.</summary>
    public static bool InitTooltips()
    {
        var info = new InitCommonControlsInfo { dwSize = (uint)sizeof(InitCommonControlsInfo), dwICC = IccBarClasses | IccTabClasses };
        return InitCommonControlsEx(&info);
    }

    /// <summary>A tooltip for <paramref name="owner"/>'s tools; zero when it could not be made.</summary>
    public static IntPtr MakeTooltip(IntPtr owner, IntPtr instance) =>
        InitTooltips()
            ? CreateWindowExW(WsExTopmost, "tooltips_class32", null, WsPopup | TtsAlwaysTip | TtsNoPrefix, CwUseDefault, CwUseDefault, CwUseDefault, CwUseDefault, owner, IntPtr.Zero, instance, IntPtr.Zero)
            : IntPtr.Zero;

    /// <summary>One of <paramref name="owner"/>'s rectangles given its words (the tooltip watches the mouse itself, <see cref="TtfSubclass"/>); false when refused.</summary>
    public static bool AddTool(IntPtr tooltip, IntPtr owner, int id, Rect rect, string text) => ToolCall(tooltip, TtmAddToolW, owner, id, rect, text);

    /// <summary>A tool's rectangle moved (a resize, a DPI change).</summary>
    public static void MoveTool(IntPtr tooltip, IntPtr owner, int id, Rect rect) => ToolCall(tooltip, TtmNewToolRectW, owner, id, rect, null);

    /// <summary>A tool's words changed (reload became stop).</summary>
    public static void RenameTool(IntPtr tooltip, IntPtr owner, int id, string text) => ToolCall(tooltip, TtmUpdateTipTextW, owner, id, default, text);

    private static bool ToolCall(IntPtr tooltip, uint message, IntPtr owner, int id, Rect rect, string? text)
    {
        fixed (char* words = text)
        {
            var info = new ToolInfo
            {
                cbSize = (uint)sizeof(ToolInfo),
                uFlags = TtfSubclass,
                hwnd = owner,
                uId = (nuint)id,
                rect = rect,
                lpszText = words,
            };
            return SendMessageW(tooltip, message, IntPtr.Zero, (IntPtr)(&info)) != IntPtr.Zero;
        }
    }
}
