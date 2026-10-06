using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.UI;

/// <summary>
/// The terminal's line discipline and stdin, for <see cref="UnixConsoleInput"/> (2026-10-06, the macOS build): libc's
/// <c>tcgetattr</c>/<c>tcsetattr</c>, <c>isatty</c>, <c>poll</c> and <c>read</c> over blittable structs and typed pointers,
/// nothing marshalled by value. The layout and the flag values are <b>macOS's</b> (<c>tcflag_t</c> and <c>speed_t</c> are
/// <c>unsigned long</c>, <c>NCCS</c> is 20, ICANON is 0x100): Linux differs in every one, so the reader is macOS-only until a
/// Linux layout sits beside this. The smoke's <c>console:termios</c> reads the attributes back on the published binary.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class TermiosNative
{
    private const string LibC = "libc";

    public const int StdinFileNo = 0;

    /// <summary><c>tcsetattr</c>'s "now" and "now, dropping unread input".</summary>
    public const int TcsaNow = 0;

    // c_iflag
    public const nuint Brkint = 0x2;
    public const nuint Inpck = 0x10;
    public const nuint Istrip = 0x20;
    public const nuint Icrnl = 0x100;
    public const nuint Ixon = 0x200;

    // c_lflag
    public const nuint Echo = 0x8;
    public const nuint Isig = 0x80;
    public const nuint Icanon = 0x100;
    public const nuint Iexten = 0x400;

    // c_cc
    public const int Vmin = 16;
    public const int Vtime = 17;

    public const short PollIn = 0x1;

    /// <summary>errno for a call a signal interrupted.</summary>
    public const int Eintr = 4;

    /// <summary>macOS's <c>struct termios</c>: 72 bytes, the speeds 8-aligned after the 20 control characters.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Termios
    {
        public nuint IFlag;
        public nuint OFlag;
        public nuint CFlag;
        public nuint LFlag;
        public fixed byte Cc[20];
        public nuint ISpeed;
        public nuint OSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short REvents;
    }

    [LibraryImport(LibC, EntryPoint = "isatty")]
    public static partial int IsATty(int fd);

    [LibraryImport(LibC, EntryPoint = "tcgetattr", SetLastError = true)]
    public static partial int TcGetAttr(int fd, Termios* termios);

    [LibraryImport(LibC, EntryPoint = "tcsetattr", SetLastError = true)]
    public static partial int TcSetAttr(int fd, int optionalActions, Termios* termios);

    [LibraryImport(LibC, EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(PollFd* fds, nuint count, int timeoutMs);

    [LibraryImport(LibC, EntryPoint = "read", SetLastError = true)]
    public static partial nint Read(int fd, byte* buffer, nuint count);

    /// <summary>
    /// <paramref name="original"/> in raw mode for the app: no line editing or echo, Ctrl+C/Ctrl+Z/Ctrl+\ as bytes rather than
    /// signals, Ctrl+S/Ctrl+Q as keys rather than flow control, CR not turned into LF, a read that returns whatever is there.
    /// Output processing stays as it was (the frames rely on the terminal's own newline handling). Pinned.
    /// </summary>
    public static Termios Raw(Termios original)
    {
        var raw = original;
        raw.IFlag &= ~(Brkint | Icrnl | Inpck | Istrip | Ixon);
        raw.LFlag &= ~(Echo | Icanon | Isig | Iexten);
        raw.Cc[Vmin] = 1;
        raw.Cc[Vtime] = 0;
        return raw;
    }

    /// <summary>Whether <paramref name="current"/> is still the app's raw mode (nothing put line editing, echo or signals back).</summary>
    public static bool IsRaw(Termios current) => (current.LFlag & (Echo | Icanon | Isig)) == 0 && (current.IFlag & (Icrnl | Ixon)) == 0;
}
