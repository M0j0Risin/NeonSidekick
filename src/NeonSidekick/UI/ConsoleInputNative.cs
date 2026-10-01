using System.Runtime.InteropServices;

namespace NeonSidekick.UI;

/// <summary>
/// The console input buffer and the clipboard, for <see cref="WindowsConsoleInput"/> and
/// <see cref="WindowsClipboard"/>. Blittable structs only, source-generated imports, the
/// <c>WinMmNative</c> conventions: nothing here is marshalled.
///
/// <para><see cref="InputRecord"/> is the Win32 <c>INPUT_RECORD</c>: a 16-bit event type, two
/// bytes of padding, then a 16-byte union; <see cref="KeyEventRecord"/> and
/// <see cref="MouseEventRecord"/> overlay the union. Field names follow the Win32 structs so the
/// documentation reads across.</para>
/// </summary>
public static partial class ConsoleInputNative
{
    public const int StdInputHandle = -10;
    public static readonly IntPtr InvalidHandleValue = new(-1);

    public const ushort KeyEvent = 0x0001;
    public const ushort MouseEvent = 0x0002;

    // Console input modes.
    public const uint EnableProcessedInput = 0x0001;
    public const uint EnableLineInput = 0x0002;
    public const uint EnableEchoInput = 0x0004;
    public const uint EnableMouseInput = 0x0010;
    public const uint EnableQuickEditMode = 0x0040;
    public const uint EnableExtendedFlags = 0x0080;
    public const uint EnableVirtualTerminalInput = 0x0200;

    // MOUSE_EVENT_RECORD.dwButtonState / dwEventFlags.
    public const uint FromLeft1stButtonPressed = 0x0001;
    public const uint RightmostButtonPressed = 0x0002;
    public const uint MouseMoved = 0x0001;
    public const uint DoubleClick = 0x0002;
    public const uint MouseWheeled = 0x0004;
    public const uint MouseHWheeled = 0x0008;

    // KEY_EVENT_RECORD.dwControlKeyState.
    public const uint RightAltPressed = 0x0001;
    public const uint LeftAltPressed = 0x0002;
    public const uint RightCtrlPressed = 0x0004;
    public const uint LeftCtrlPressed = 0x0008;
    public const uint ShiftPressed = 0x0010;

    // Virtual keys the translation looks at.
    public const ushort VkShift = 0x10;
    public const ushort VkControl = 0x11;
    public const ushort VkMenu = 0x12;
    public const ushort VkCapital = 0x14;
    public const ushort VkNumLock = 0x90;
    public const ushort VkScroll = 0x91;
    public const ushort VkClear = 0x0C;
    public const ushort VkNumpad0 = 0x60;
    public const ushort VkNumpad9 = 0x69;

    public const uint WaitObject0 = 0;
    public const uint ClipboardUnicodeText = 13;

    /// <summary><c>CF_DIB</c>: a <c>BITMAPINFOHEADER</c> (or a V4/V5 one) and the pixels, no file header; the system synthesises it from <c>CF_BITMAP</c> and <c>CF_DIBV5</c>.</summary>
    public const uint ClipboardDib = 8;

    /// <summary>The registered clipboard format a PNG file is put on the clipboard under (Snipping Tool, browsers, ShareX).</summary>
    public const string ClipboardPngFormatName = "PNG";

    /// <summary><c>GMEM_MOVEABLE</c>: what <c>SetClipboardData</c> requires of the memory it is handed.</summary>
    public const uint GlobalMoveable = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyEventRecord
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        /// <summary>The UTF-16 unit (a <c>char</c>); kept a <see cref="ushort"/> so the struct stays blittable for the generated import.</summary>
        public ushort UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseEventRecord
    {
        public short X;
        public short Y;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct InputRecord
    {
        [FieldOffset(0)]
        public ushort EventType;

        [FieldOffset(4)]
        public KeyEventRecord KeyEvent;

        [FieldOffset(4)]
        public MouseEventRecord MouseEvent;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadConsoleInputW(IntPtr hConsoleInput, out InputRecord lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    /// <summary>How many records the input buffer holds right now: what tells a paste's burst from a keystroke.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpcNumberOfEvents);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    /// <summary>On success the system owns <paramref name="hMem"/>; free it only when this fails.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalFree(IntPtr hMem);

    /// <summary>The size of a global block in bytes, 0 for a bad handle: how much of a clipboard block is there to read.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint GlobalSize(IntPtr hMem);

    /// <summary>The id of a named clipboard format, 0 on failure; the same name gives the same id system-wide.</summary>
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormatW(string lpszFormat);
}
