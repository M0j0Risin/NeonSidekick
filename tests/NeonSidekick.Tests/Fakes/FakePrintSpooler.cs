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

    public string? Print(PrintJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Jobs.Add(job);
        if (FailWith is null && job.OutputFile is not null && OutputBytes is not null)
        {
            File.WriteAllBytes(job.OutputFile, OutputBytes);
        }

        return FailWith;
    }

    public bool CanShellPrint(string path) => ShellTypes.Contains(Path.GetExtension(path));

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
