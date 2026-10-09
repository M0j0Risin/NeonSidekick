using NeonSidekick.Printing;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The print seam's fake (2026-09-28): three printers (the first the Windows default), US Letter paper turned for landscape,
/// text measured at a fixed advance (<see cref="FixedMeasure"/>), and every job, opened surface and shell print recorded. Only
/// <see cref="ShellTypes"/> have a program to print them. <see cref="FailWith"/> makes the next job fail with that sentence.
/// A job with an output file (the PDF fallback, 2026-10-03) writes <see cref="OutputBytes"/> there, or nothing when it is null.
/// </summary>
public sealed class FakePrintSpooler : IPrintSpooler
{
    public const string Laser = "Office Laser";
    public const string Color = "Office Color";
    public const string Pdf = "Microsoft Print to PDF";

    public List<PrinterInfo> Installed { get; } = [new(Laser, true), new(Color, false), new(Pdf, false)];

    public List<PrintJob> Jobs { get; } = [];

    public List<(string Printer, bool Landscape)> Opened { get; } = [];

    public List<string> ShellPrinted { get; } = [];

    public HashSet<string> ShellTypes { get; } = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    public string? FailWith { get; set; }

    /// <summary>What a print-to-file job writes to its output file: a tiny PDF by default; null writes nothing.</summary>
    public byte[]? OutputBytes { get; set; } = "%PDF-1.7\n%fake\n"u8.ToArray();

    public IReadOnlyList<PrinterInfo> Printers() => Installed;

    public IPrintSurface? Open(string printer, bool landscape, out string error)
    {
        if (!Installed.Any(p => p.Name == printer))
        {
            error = PrintText.CannotOpen(printer, "no such printer");
            return null;
        }

        Opened.Add((printer, landscape));
        error = "";
        return new Surface(PageMetrics.Letter(landscape));
    }

    /// <summary>A slow printer (2026-10-08): <see cref="Print"/> waits for its token (30 s at most) and throws, as a job withdrawn mid-send does.</summary>
    public bool WaitForCancel { get; set; }

    /// <summary>With <see cref="WaitForCancel"/>: the job goes through anyway once cancelled, as one the printer had before the key.</summary>
    public bool FinishAnyway { get; set; }

    /// <summary>Told when a job starts, before it waits: the test pushes its ESC there, so nothing else can take the key first.</summary>
    public Action? Started { get; set; }

    public string? Print(PrintJob job, CancellationToken cancellationToken)
    {
        Started?.Invoke();
        if (WaitForCancel && !cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("the print was never cancelled");
        }

        if (!FinishAnyway)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        Jobs.Add(job);
        if (FailWith is null && job.OutputFile is not null && OutputBytes is not null)
        {
            File.WriteAllBytes(job.OutputFile, OutputBytes);
        }

        return FailWith;
    }

    public bool CanShellPrint(string path) => ShellTypes.Contains(Path.GetExtension(path));

    /// <summary>A Mac's spooler (2026-10-08): a PDF goes as it is, with a printer, copies and pages. Off: Windows' shell verb.</summary>
    public bool TakesOptions { get; set; }

    /// <summary>The pages a file sent as it is has, by file name; one missing counts 0 (unreadable).</summary>
    public Dictionary<string, int> PageCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every file sent as it is, with its options.</summary>
    public List<(string Path, string Printer, int Copies, IReadOnlyList<int>? Pages)> SentAsItIs { get; } = [];

    public bool ShellPrintTakesOptions => TakesOptions;

    public int ShellPageCount(string path) => PageCounts.GetValueOrDefault(Path.GetFileName(path));

    public string? ShellPrint(string path, string printer, int copies, IReadOnlyList<int>? pages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SentAsItIs.Add((path, printer, copies, pages));
        return FailWith;
    }

    public string? ShellPrint(string path)
    {
        ShellPrinted.Add(path);
        return null;
    }

    private sealed class Surface(PageMetrics page) : IPrintSurface
    {
        public PageMetrics Page { get; } = page;

        public double Width(string text, PrintFont font) => FixedMeasure.Instance.Width(text, font);

        public void Dispose()
        {
        }
    }
}

/// <summary>A fixed advance per character: 0.6 of the size in the fixed face, 0.5 in the other — the layout tests' arithmetic.</summary>
public sealed class FixedMeasure : ITextMeasure
{
    public static readonly FixedMeasure Instance = new();

    public double Width(string text, PrintFont font) => text.Length * font.Size * (font.Face == PrintFace.Mono ? 0.6 : 0.5);
}
