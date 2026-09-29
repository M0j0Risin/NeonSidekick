namespace NeonSidekick.Printing;

/// <summary>
/// The spooler with no printers (2026-09-28): what the screen and headless print through until the composition root hands
/// them <see cref="WindowsPrintSpooler"/>, so a test that never names a spooler can never reach the machine's printers.
/// </summary>
public sealed class NullPrintSpooler : IPrintSpooler
{
    public static readonly NullPrintSpooler Instance = new();

    public IReadOnlyList<PrinterInfo> Printers() => [];

    public IPrintSurface? Open(string printer, bool landscape, out string error)
    {
        error = PrintText.NoPrinter;
        return null;
    }

    public string? Print(PrintJob job, CancellationToken cancellationToken) => PrintText.NoPrinter;

    public bool CanShellPrint(string path) => false;

    public string? ShellPrint(string path) => PrintText.NoPrinter;
}
