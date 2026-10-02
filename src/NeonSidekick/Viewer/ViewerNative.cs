using System.Runtime.InteropServices;

namespace NeonSidekick.Viewer;

/// <summary>
/// The user32/gdi32/kernel32/dwmapi imports behind <see cref="PictureWindow"/> (2026-09-27; dwmapi later that day, for the
/// themed title bar; ole32/shell32 on 2026-09-28, for <see cref="PictureWindowDrag"/>). One of the Windows-only layers, like
/// <c>Audio/WinMm*</c>: source-generated <see cref="LibraryImportAttribute"/> over blittable structs only, every pointer an
/// <see cref="IntPtr"/> or a typed pointer, and the window procedure an <c>[UnmanagedCallersOnly]</c> function pointer — no
/// delegate is marshalled, so nothing here behaves differently once published (the smoke's <c>viewer:window</c> proves it).
/// All six are system libraries: nothing joins <c>SmokeChecks.RequiredNativeLibraries</c>. The drag's one COM object is the
/// shell's own, held as an <see cref="IntPtr"/> and called through its vtable (<see cref="PictureWindowDrag"/>): no COM
/// class of ours, no <c>ComWrappers</c>.
/// </summary>
internal static unsafe partial class ViewerNative
{
    public const uint WmCreate = 0x0001;
    public const uint WmDestroy = 0x0002;
    public const uint WmSize = 0x0005;
    public const uint WmActivate = 0x0006;
    public const uint WmPaint = 0x000F;
    public const uint WmClose = 0x0010;
    public const uint WmEraseBackground = 0x0014;
    public const uint WmNcCreate = 0x0081;
    public const uint WmKeyDown = 0x0100;
    public const uint WmSysKeyDown = 0x0104;
    public const uint WmTimer = 0x0113;
    public const uint WmMouseMove = 0x0200;
    public const uint WmLeftButtonDown = 0x0201;
    public const uint WmLeftButtonUp = 0x0202;
    public const uint WmLeftButtonDoubleClick = 0x0203;
    public const uint WmCaptureChanged = 0x0215;
    public const uint WmApp = 0x8000;

    public const uint CsVRedraw = 0x0001;
    public const uint CsHRedraw = 0x0002;
    public const uint CsDoubleClicks = 0x0008;

    public const uint WsOverlappedWindow = 0x00CF0000;
    public const uint WsPopup = 0x80000000;
    public const uint WsVisible = 0x10000000;
    public const int CwUseDefault = unchecked((int)0x80000000);

    public const int GwlStyle = -16;
    public const int GwlpUserData = -21;

    public const int SwHide = 0;
    public const int SwShow = 5;
    public const int SwShowNoActivate = 4;
    public const int SwRestore = 9;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;

    public static readonly IntPtr HwndTop = IntPtr.Zero;
    public static readonly IntPtr HwndTopmost = new(-1);
    public static readonly IntPtr HwndNoTopmost = new(-2);

    public const uint MonitorDefaultToNearest = 2;
    public const int BlackBrush = 4;
    public const int Halftone = 4;
    public const uint DibRgbColors = 0;
    public const uint SrcCopy = 0x00CC0020;
    public const int Transparent = 1;
    public const uint DtCenter = 0x0001;
    public const uint DtVCenter = 0x0004;
    public const uint DtWordBreak = 0x0010;
    public const uint DtSingleLine = 0x0020;
    public const uint DtNoPrefix = 0x0800;

    public const uint ImageIcon = 1;
    public const uint LrDefaultSize = 0x0040;
    public const uint LrShared = 0x8000;

    /// <summary>The icon id the compiler gives an <c>ApplicationIcon</c> in the exe's Win32 resources.</summary>
    public const int ApplicationIconId = 32512;

    /// <summary>The idc arrow cursor.</summary>
    public const int IdcArrow = 32512;

    /// <summary>MK_LBUTTON: the left button is down, in a mouse message's wParam.</summary>
    public const int MkLeftButton = 0x0001;

    /// <summary>SM_CXDRAG / SM_CYDRAG: the rectangle, centred on a press, a move must leave before it is a drag.</summary>
    public const int SmCxDrag = 68;
    public const int SmCyDrag = 69;

    /// <summary>DROPEFFECT_COPY: the only effect the viewer's drag offers (2026-09-28, the user's ask: copied, never moved).</summary>
    public const uint DropEffectCopy = 1;

    /// <summary>DRAGDROP_S_DROP / DRAGDROP_S_CANCEL: how a drag ended.</summary>
    public const int DragDropDropped = 0x00040100;
    public const int DragDropCancelled = 0x00040101;

    /// <summary>CF_HDROP, DVASPECT_CONTENT, TYMED_HGLOBAL: the file list a drop target reads, as <see cref="FormatEtc"/> asks for it.</summary>
    public const ushort CfHDrop = 15;
    public const uint DvAspectContent = 1;
    public const uint TymedHGlobal = 1;

    /// <summary>E_FAIL.</summary>
    public const int EFail = unchecked((int)0x80004005);

    /// <summary>IID_IDataObject.</summary>
    public static readonly Guid IidDataObject = new("0000010e-0000-0000-C000-000000000046");

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    public static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public IntPtr hdc;
        public int fErase;
        public Rect rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreateStruct
    {
        public IntPtr lpCreateParams;
        public IntPtr hInstance;
        public IntPtr hMenu;
        public IntPtr hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public IntPtr lpszName;
        public IntPtr lpszClass;
        public uint dwExStyle;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FormatEtc
    {
        public ushort cfFormat;
        public IntPtr ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPlacement
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public Point ptMinPosition;
        public Point ptMaxPosition;
        public Rect rcNormalPosition;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial IntPtr GetModuleHandle(IntPtr lpModuleName);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(WndClassEx* lpwcx);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr CreateWindowExW(uint dwExStyle, IntPtr lpClassName, IntPtr lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(Msg* lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(Msg* lpMsg);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DispatchMessageW(Msg* lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowTextW(IntPtr hWnd, string lpString);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, Rect* lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(IntPtr hWnd, Rect* lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [LibraryImport("user32.dll")]
    public static partial IntPtr BeginPaint(IntPtr hWnd, PaintStruct* lpPaint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(IntPtr hWnd, PaintStruct* lpPaint);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(IntPtr hDC, Rect* lprc, IntPtr hbr);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawTextW(IntPtr hdc, string lpchText, int cchText, Rect* lprc, uint format);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(IntPtr hMonitor, MonitorInfo* lpmi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowPlacement(IntPtr hWnd, WindowPlacement* lpwndpl);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPlacement(IntPtr hWnd, WindowPlacement* lpwndpl);

    [LibraryImport("user32.dll")]
    public static partial IntPtr LoadImageW(IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport("user32.dll")]
    public static partial IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr GetStockObject(int i);

    /// <summary>The stock GUI font, the one a dialog's text uses.</summary>
    public const int DefaultGuiFont = 17;

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll")]
    public static partial int SetStretchBltMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetBrushOrgEx(IntPtr hdc, int x, int y, Point* lppt);

    [LibraryImport("gdi32.dll")]
    public static partial int StretchDIBits(IntPtr hdc, int xDest, int yDest, int destWidth, int destHeight, int xSrc, int ySrc, int srcWidth, int srcHeight, void* lpBits, BitmapInfoHeader* lpbmi, uint iUsage, uint rop);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetTextColor(IntPtr hdc, uint color);

    [LibraryImport("gdi32.dll")]
    public static partial int SetBkMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetCapture(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("ole32.dll")]
    public static partial int OleInitialize(IntPtr pvReserved);

    [LibraryImport("ole32.dll")]
    public static partial void OleUninitialize();

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(string pszName, IntPtr pbc, IntPtr* ppidl, uint sfgaoIn, uint* psfgaoOut);

    [LibraryImport("shell32.dll")]
    public static partial int SHCreateDataObject(IntPtr pidlFolder, uint cidl, IntPtr* apidl, IntPtr pdtInner, Guid* riid, IntPtr* ppv);

    [LibraryImport("shell32.dll")]
    public static partial int SHDoDragDrop(IntPtr hwnd, IntPtr pdata, IntPtr pdsrc, uint dwEffect, uint* pdwEffect);

    [LibraryImport("shell32.dll")]
    public static partial IntPtr ILClone(IntPtr pidl);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ILRemoveLastID(IntPtr pidl);

    [LibraryImport("shell32.dll")]
    public static partial IntPtr ILFindLastID(IntPtr pidl);

    [LibraryImport("shell32.dll")]
    public static partial void ILFree(IntPtr pidl);

    // DWMWINDOWATTRIBUTE (dwmapi.h): the dark bar from Windows 10 20H1, the colours from Windows 11 (22000); older
    // builds answer E_INVALIDARG and keep their bar.
    public const uint DwmwaUseImmersiveDarkMode = 20;
    public const uint DwmwaBorderColor = 34;
    public const uint DwmwaCaptionColor = 35;
    public const uint DwmwaTextColor = 36;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, uint dwAttribute, void* pvAttribute, uint cbAttribute);
}
