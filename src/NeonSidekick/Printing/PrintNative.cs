using System.Runtime.InteropServices;

namespace NeonSidekick.Printing;

/// <summary>
/// The winspool/gdi32/shlwapi imports behind <see cref="WindowsPrintSpooler"/> (2026-09-28, the user's ask: <c>/print</c> and
/// <c>print_file</c>). One of the Windows-only layers, like <c>Audio/WinMm*</c> and <c>Viewer/ViewerNative</c>: source-generated
/// <see cref="LibraryImportAttribute"/> over blittable structs only, every pointer an <see cref="IntPtr"/> or a typed pointer,
/// strings passed as <c>char*</c> the caller pins, and the DEVMODE a raw buffer patched at two offsets rather than a struct
/// marshalled by value — nothing here behaves differently once published (the smoke's <c>print:spooler</c> proves it). All
/// three are system libraries: nothing joins <c>SmokeChecks.RequiredNativeLibraries</c>.
/// </summary>
internal static unsafe partial class PrintNative
{
    // EnumPrinters flags and level (winspool.h).
    public const uint PrinterEnumLocal = 0x00000002;
    public const uint PrinterEnumConnections = 0x00000004;

    // GetDeviceCaps indexes (wingdi.h).
    public const int HorzRes = 8;
    public const int VertRes = 10;
    public const int LogPixelsX = 88;
    public const int LogPixelsY = 90;
    public const int PhysicalWidth = 110;
    public const int PhysicalHeight = 111;
    public const int PhysicalOffsetX = 112;
    public const int PhysicalOffsetY = 113;

    // DocumentProperties modes and the two DEVMODEW fields patched (wingdi.h): dmFields at 72, dmOrientation at 76.
    public const uint DmOutBuffer = 2;
    public const uint DmInBuffer = 8;
    public const int DevModeFieldsOffset = 72;
    public const int DevModeOrientationOffset = 76;
    public const uint DmOrientation = 0x00000001;
    public const short DmOrientPortrait = 1;
    public const short DmOrientLandscape = 2;

    // Fonts and drawing.
    public const int FwNormal = 400;
    public const int FwBold = 700;
    public const uint DefaultCharset = 1;
    public const uint OutDefaultPrecis = 0;
    public const uint ClipDefaultPrecis = 0;
    public const uint ProofQuality = 2;
    public const uint FixedPitch = 1;
    public const uint VariablePitch = 2;
    public const uint FfModern = 0x30;
    public const uint FfSwiss = 0x20;
    public const int Transparent = 1;
    public const uint TaBaseline = 24;
    public const int Halftone = 4;
    public const uint DibRgbColors = 0;
    public const uint SrcCopy = 0x00CC0020;
    public const uint Blackness = 0x00000042;

    // AssocQueryString (shlwapi.h).
    public const uint AssocfInitIgnoreUnknown = 0x00000400;
    public const uint AssocStrCommand = 1;
    public const uint AssocStrDelegateExecute = 18;

    /// <summary>PRINTER_INFO_4W: the name, the server (null for a local printer) and the attributes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PrinterInfo4
    {
        public IntPtr pPrinterName;
        public IntPtr pServerName;
        public uint Attributes;
    }

    /// <summary>DOCINFOW: the job's name, and the file a print-to-file printer writes (null: the port's own).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DocInfo
    {
        public int cbSize;
        public char* lpszDocName;
        public char* lpszOutput;
        public char* lpszDatatype;
        public uint fwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public int cx;
        public int cy;
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

    [LibraryImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumPrinters(uint flags, char* name, uint level, byte* pPrinterEnum, uint cbBuf, uint* pcbNeeded, uint* pcReturned);

    [LibraryImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDefaultPrinter(char* pszBuffer, uint* pcchBuffer);

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenPrinter(char* pPrinterName, IntPtr* phPrinter, void* pDefault);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClosePrinter(IntPtr hPrinter);

    [LibraryImport("winspool.drv", EntryPoint = "DocumentPropertiesW", SetLastError = true)]
    public static partial int DocumentProperties(IntPtr hWnd, IntPtr hPrinter, char* pDeviceName, byte* pDevModeOutput, byte* pDevModeInput, uint fMode);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", SetLastError = true)]
    public static partial IntPtr CreateDC(char* pwszDriver, char* pwszDevice, char* pszPort, byte* pdm);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateICW", SetLastError = true)]
    public static partial IntPtr CreateIC(char* pszDriver, char* pszDevice, char* pszPort, byte* pdm);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "StartDocW", SetLastError = true)]
    public static partial int StartDoc(IntPtr hdc, DocInfo* lpdi);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int StartPage(IntPtr hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int EndPage(IntPtr hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int EndDoc(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    public static partial int AbortDoc(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDeviceCaps(IntPtr hdc, int index);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW")]
    public static partial IntPtr CreateFont(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, char* pszFaceName);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);

    [LibraryImport("gdi32.dll", EntryPoint = "ExtTextOutW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ExtTextOut(IntPtr hdc, int x, int y, uint options, void* lprect, char* lpString, uint c, int* lpDx);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTextExtentPoint32(IntPtr hdc, char* lpString, int c, Size* psizl);

    [LibraryImport("gdi32.dll")]
    public static partial int SetBkMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetTextAlign(IntPtr hdc, uint align);

    [LibraryImport("gdi32.dll")]
    public static partial int SetStretchBltMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetBrushOrgEx(IntPtr hdc, int x, int y, void* lppt);

    [LibraryImport("gdi32.dll")]
    public static partial int StretchDIBits(IntPtr hdc, int xDest, int yDest, int destWidth, int destHeight, int xSrc, int ySrc, int srcWidth, int srcHeight, void* lpBits, BitmapInfoHeader* lpbmi, uint iUsage, uint rop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);

    [LibraryImport("shlwapi.dll", EntryPoint = "AssocQueryStringW")]
    public static partial int AssocQueryString(uint flags, uint str, char* pszAssoc, char* pszExtra, char* pszOut, uint* pcchOut);
}
