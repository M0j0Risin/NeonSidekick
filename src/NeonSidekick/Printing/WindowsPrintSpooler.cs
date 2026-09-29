using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Printing;

/// <summary>
/// The real <see cref="IPrintSpooler"/> (2026-09-28): the printers from winspool, a measuring surface that is a GDI information
/// context on the printer, the job drawn page by page on a printer device context — text with <c>ExtTextOutW</c> in the
/// printer's own Segoe UI and Consolas, boxes with <c>PatBlt</c>, pictures with <c>StretchDIBits</c> — and, for a file the app
/// cannot draw, the shell's <c>print</c> verb (a process-start site of its own, <see cref="ShellPrint"/>). The orientation is
/// written into the driver's own DEVMODE (<c>DocumentPropertiesW</c>), so the paper, the tray and the rest stay the user's.
/// Proven by the smoke's <c>print:spooler</c> on the published exe and a live fact, not by line coverage.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed unsafe class WindowsPrintSpooler : IPrintSpooler
{
    public const string SansFace = "Segoe UI";
    public const string MonoFace = "Consolas";

    private const int ErrorCancelled = 1223;

    public IReadOnlyList<PrinterInfo> Printers()
    {
        try
        {
            string? fallback = DefaultPrinterName();
            uint needed = 0;
            uint count = 0;
            uint flags = PrintNative.PrinterEnumLocal | PrintNative.PrinterEnumConnections;
            PrintNative.EnumPrinters(flags, null, 4, null, 0, &needed, &count);
            if (needed == 0)
            {
                return [];
            }

            var buffer = new byte[needed];
            fixed (byte* bytes = buffer)
            {
                if (!PrintNative.EnumPrinters(flags, null, 4, bytes, needed, &needed, &count))
                {
                    DiagnosticLog.Warn(PrintText.Category, "EnumPrinters failed: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
                    return [];
                }

                var printers = new List<PrinterInfo>((int)count);
                var infos = (PrintNative.PrinterInfo4*)bytes;
                for (int i = 0; i < count; i++)
                {
                    string? name = Marshal.PtrToStringUni(infos[i].pPrinterName);
                    if (!string.IsNullOrEmpty(name))
                    {
                        printers.Add(new PrinterInfo(name, string.Equals(name, fallback, StringComparison.OrdinalIgnoreCase)));
                    }
                }

                return printers;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn(PrintText.Category, "The printers could not be listed: " + ex.Message);
            return [];
        }
    }

    /// <summary>The Windows default printer's name, or null for none.</summary>
    public static string? DefaultPrinterName()
    {
        uint size = 0;
        PrintNative.GetDefaultPrinter(null, &size);
        if (size == 0)
        {
            return null;
        }

        var buffer = new char[size];
        fixed (char* chars = buffer)
        {
            return PrintNative.GetDefaultPrinter(chars, &size) ? new string(chars) : null;
        }
    }

    public IPrintSurface? Open(string printer, bool landscape, out string error)
    {
        ArgumentNullException.ThrowIfNull(printer);
        byte[]? devMode = DevMode(printer, landscape);
        IntPtr ic;
        fixed (char* name = printer)
        fixed (byte* dm = devMode)
        {
            ic = PrintNative.CreateIC(null, name, null, dm);
        }

        if (ic == IntPtr.Zero)
        {
            error = PrintText.CannotOpen(printer, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            return null;
        }

        error = "";
        return new Surface(ic);
    }

    /// <summary>
    /// Sends <paramref name="job"/> on a thread of its own in a single-threaded apartment: a print-to-file driver (Microsoft Print
    /// to PDF, the XPS writer) shows its Save As dialog from inside <c>StartDocW</c>, and driver UI wants an STA, never a pool thread.
    /// The caller waits; a throw on that thread (the cancellation) is rethrown here.
    /// </summary>
    public string? Print(PrintJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return PrintHere(job, cancellationToken);
        }

        string? result = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = PrintHere(job, cancellationToken);
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "NeonSidekick print",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    private string? PrintHere(PrintJob job, CancellationToken cancellationToken)
    {
        byte[]? devMode = DevMode(job.Printer, job.Landscape);
        IntPtr hdc;
        fixed (char* name = job.Printer)
        fixed (byte* dm = devMode)
        {
            hdc = PrintNative.CreateDC(null, name, null, dm);
        }

        if (hdc == IntPtr.Zero)
        {
            return PrintText.CannotOpen(job.Printer, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        using var device = new Surface(hdc);
        bool started = false;
        try
        {
            int id;
            fixed (char* title = job.Title)
            fixed (char* output = job.OutputFile)
            {
                var info = new PrintNative.DocInfo { cbSize = sizeof(PrintNative.DocInfo), lpszDocName = title, lpszOutput = output };
                id = PrintNative.StartDoc(hdc, &info);
            }

            if (id <= 0)
            {
                int code = Marshal.GetLastPInvokeError();
                return code == ErrorCancelled ? PrintText.PrinterCancelled(job.Printer) : PrintText.Failed(job.Printer, new Win32Exception(code).Message);
            }

            started = true;
            PrintNative.SetBkMode(hdc, PrintNative.Transparent);
            // Text is placed by its baseline, so a code span and the prose round it line up.
            PrintNative.SetTextAlign(hdc, PrintNative.TaBaseline);
            for (int copy = 0; copy < Math.Max(1, job.Copies); copy++)
            {
                foreach (var page in job.Pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (PrintNative.StartPage(hdc) <= 0)
                    {
                        return Abort(hdc, ref started, PrintText.Failed(job.Printer, new Win32Exception(Marshal.GetLastPInvokeError()).Message));
                    }

                    foreach (var op in page.Ops)
                    {
                        device.Draw(op);
                    }

                    if (PrintNative.EndPage(hdc) <= 0)
                    {
                        return Abort(hdc, ref started, PrintText.Failed(job.Printer, new Win32Exception(Marshal.GetLastPInvokeError()).Message));
                    }
                }
            }

            started = false;
            return PrintNative.EndDoc(hdc) > 0 ? null : PrintText.Failed(job.Printer, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }
        finally
        {
            if (started)
            {
                // Cancelled, or a throw mid-page: the half-sent document is withdrawn, never printed.
                PrintNative.AbortDoc(hdc);
            }
        }
    }

    private static string Abort(IntPtr hdc, ref bool started, string error)
    {
        PrintNative.AbortDoc(hdc);
        started = false;
        return error;
    }

    public bool CanShellPrint(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path);
        if (extension.Length == 0)
        {
            return false;
        }

        return Registered(extension, PrintNative.AssocStrCommand) || Registered(extension, PrintNative.AssocStrDelegateExecute);
    }

    private static bool Registered(string extension, uint what)
    {
        uint size = 0;
        int hr;
        fixed (char* assoc = extension)
        fixed (char* verb = "print")
        {
            hr = PrintNative.AssocQueryString(PrintNative.AssocfInitIgnoreUnknown, what, assoc, verb, null, &size);
        }

        // S_OK or S_FALSE (the size alone was asked for): the verb is there.
        return hr is 0 or 1 && size > 1;
    }

    /// <summary>
    /// The shell's <c>print</c> verb on <paramref name="path"/> (2026-09-28, the user's call: a PDF or an Office file goes to the
    /// program Windows registers for it, on the Windows default printer). A deliberate process-start site: the file's own
    /// handler is started, never waited for.
    /// </summary>
    public string? ShellPrint(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print", WindowStyle = ProcessWindowStyle.Minimized });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return PrintText.ShellFailed(Path.GetFileName(path), ex.Message);
        }
    }

    /// <summary>
    /// The driver's own DEVMODE for <paramref name="printer"/> with the orientation set, or null when the printer will not give
    /// one (the device context then takes the printer's defaults). dmFields and dmOrientation sit at fixed offsets in every
    /// DEVMODEW, so the buffer is patched in place and handed back to the driver to merge.
    /// </summary>
    private static byte[]? DevMode(string printer, bool landscape)
    {
        IntPtr handle = IntPtr.Zero;
        fixed (char* name = printer)
        {
            if (!PrintNative.OpenPrinter(name, &handle, null))
            {
                return null;
            }

            try
            {
                int size = PrintNative.DocumentProperties(IntPtr.Zero, handle, name, null, null, 0);
                if (size < PrintNative.DevModeOrientationOffset + sizeof(short))
                {
                    return null;
                }

                var buffer = new byte[size];
                fixed (byte* dm = buffer)
                {
                    if (PrintNative.DocumentProperties(IntPtr.Zero, handle, name, dm, null, PrintNative.DmOutBuffer) < 0)
                    {
                        return null;
                    }

                    *(uint*)(dm + PrintNative.DevModeFieldsOffset) |= PrintNative.DmOrientation;
                    *(short*)(dm + PrintNative.DevModeOrientationOffset) = landscape ? PrintNative.DmOrientLandscape : PrintNative.DmOrientPortrait;
                    PrintNative.DocumentProperties(IntPtr.Zero, handle, name, dm, dm, PrintNative.DmInBuffer | PrintNative.DmOutBuffer);
                }

                return buffer;
            }
            finally
            {
                PrintNative.ClosePrinter(handle);
            }
        }
    }

    /// <summary>A printer DC or IC: the paper in points, the fonts made once each, text measured and ops drawn in device units.</summary>
    private sealed class Surface : IPrintSurface
    {
        private readonly IntPtr _dc;
        private readonly double _dpiX;
        private readonly double _dpiY;
        private readonly int _offsetX;
        private readonly int _offsetY;
        private readonly Dictionary<PrintFont, IntPtr> _fonts = new();
        private IntPtr _selected;

        public Surface(IntPtr dc)
        {
            _dc = dc;
            _dpiX = Math.Max(1, PrintNative.GetDeviceCaps(dc, PrintNative.LogPixelsX));
            _dpiY = Math.Max(1, PrintNative.GetDeviceCaps(dc, PrintNative.LogPixelsY));
            _offsetX = PrintNative.GetDeviceCaps(dc, PrintNative.PhysicalOffsetX);
            _offsetY = PrintNative.GetDeviceCaps(dc, PrintNative.PhysicalOffsetY);
            int width = PrintNative.GetDeviceCaps(dc, PrintNative.PhysicalWidth);
            int height = PrintNative.GetDeviceCaps(dc, PrintNative.PhysicalHeight);
            int printableWidth = PrintNative.GetDeviceCaps(dc, PrintNative.HorzRes);
            int printableHeight = PrintNative.GetDeviceCaps(dc, PrintNative.VertRes);
            if (width <= 0 || height <= 0)
            {
                // A display-like driver without the physical caps: the printable area is the sheet.
                width = printableWidth;
                height = printableHeight;
            }

            Page = new PageMetrics(
                PointsX(width), PointsY(height),
                PointsX(_offsetX), PointsY(_offsetY),
                PointsX(_offsetX + printableWidth), PointsY(_offsetY + printableHeight));
        }

        public PageMetrics Page { get; }

        private double PointsX(int device) => device * 72.0 / _dpiX;

        private double PointsY(int device) => device * 72.0 / _dpiY;

        private int DeviceX(double points) => (int)Math.Round(points * _dpiX / 72.0) - _offsetX;

        private int DeviceY(double points) => (int)Math.Round(points * _dpiY / 72.0) - _offsetY;

        private void Select(PrintFont font)
        {
            if (!_fonts.TryGetValue(font, out IntPtr handle))
            {
                string face = font.Face == PrintFace.Mono ? MonoFace : SansFace;
                uint pitch = font.Face == PrintFace.Mono ? PrintNative.FixedPitch | PrintNative.FfModern : PrintNative.VariablePitch | PrintNative.FfSwiss;
                fixed (char* name = face)
                {
                    handle = PrintNative.CreateFont(-(int)Math.Round(font.Size * _dpiY / 72.0), 0, 0, 0, font.Bold ? PrintNative.FwBold : PrintNative.FwNormal, font.Italic ? 1u : 0u, 0, 0, PrintNative.DefaultCharset, PrintNative.OutDefaultPrecis, PrintNative.ClipDefaultPrecis, PrintNative.ProofQuality, pitch, name);
                }

                _fonts[font] = handle;
            }

            if (handle != _selected && handle != IntPtr.Zero)
            {
                PrintNative.SelectObject(_dc, handle);
                _selected = handle;
            }
        }

        public double Width(string text, PrintFont font)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(font);
            if (text.Length == 0)
            {
                return 0;
            }

            Select(font);
            PrintNative.Size size;
            fixed (char* chars = text)
            {
                if (!PrintNative.GetTextExtentPoint32(_dc, chars, text.Length, &size))
                {
                    // No metrics: an average advance, never zero, so the layout still wraps.
                    return text.Length * font.Size * 0.55;
                }
            }

            return PointsX(size.cx);
        }

        public void Draw(PrintOp op)
        {
            switch (op)
            {
                case PrintTextOp text when text.Text.Length > 0:
                    Select(text.Font);
                    fixed (char* chars = text.Text)
                    {
                        PrintNative.ExtTextOut(_dc, DeviceX(text.X), DeviceY(text.Y), 0, null, chars, (uint)text.Text.Length, null);
                    }

                    break;
                case PrintBoxOp box:
                {
                    int x = DeviceX(box.X);
                    int y = DeviceY(box.Y);
                    int w = Math.Max(1, DeviceX(box.X + box.Width) - x);
                    int h = Math.Max(1, DeviceY(box.Y + box.Height) - y);
                    PrintNative.PatBlt(_dc, x, y, w, h, PrintNative.Blackness);
                    break;
                }

                case PrintImageOp image:
                {
                    int x = DeviceX(image.X);
                    int y = DeviceY(image.Y);
                    int w = Math.Max(1, DeviceX(image.X + image.Width) - x);
                    int h = Math.Max(1, DeviceY(image.Y + image.Height) - y);
                    PrintNative.SetStretchBltMode(_dc, PrintNative.Halftone);
                    PrintNative.SetBrushOrgEx(_dc, 0, 0, null);
                    var header = new PrintNative.BitmapInfoHeader
                    {
                        biSize = (uint)sizeof(PrintNative.BitmapInfoHeader),
                        biWidth = image.Bitmap.Width,
                        biHeight = -image.Bitmap.Height,   // negative: top row first
                        biPlanes = 1,
                        biBitCount = 32,
                    };
                    fixed (byte* bits = image.Bitmap.Bgrx)
                    {
                        PrintNative.StretchDIBits(_dc, x, y, w, h, 0, 0, image.Bitmap.Width, image.Bitmap.Height, bits, &header, PrintNative.DibRgbColors, PrintNative.SrcCopy);
                    }

                    break;
                }
            }
        }

        public void Dispose()
        {
            // The DC first: a font still selected into it cannot be deleted.
            PrintNative.DeleteDC(_dc);
            foreach (IntPtr font in _fonts.Values)
            {
                if (font != IntPtr.Zero)
                {
                    PrintNative.DeleteObject(font);
                }
            }

            _fonts.Clear();
        }
    }
}
