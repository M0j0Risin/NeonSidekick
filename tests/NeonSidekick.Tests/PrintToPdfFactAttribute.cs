using NeonSidekick.App;
using NeonSidekick.Printing;

namespace NeonSidekick.Tests;

/// <summary>
/// A fact that needs Windows' own print-to-file printer (2026-09-28): skipped off Windows and where
/// <see cref="SmokeChecks.PrintToPdfPrinter"/> is not installed. A local gate, never something CI relies on.
/// </summary>
public sealed class PrintToPdfFactAttribute : FactAttribute
{
    public PrintToPdfFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Printing is Windows-only.";
            return;
        }

        if (!new WindowsPrintSpooler().Printers().Any(p => string.Equals(p.Name, SmokeChecks.PrintToPdfPrinter, StringComparison.OrdinalIgnoreCase)))
        {
            Skip = $"No {SmokeChecks.PrintToPdfPrinter} printer is installed.";
        }
    }
}
