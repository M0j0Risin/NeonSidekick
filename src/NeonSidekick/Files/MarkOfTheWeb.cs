using NeonSidekick.Diagnostics;

namespace NeonSidekick.Files;

/// <summary>
/// The "came from the internet" mark carried from an archive to what is unzipped from it (2026-10-22, review O5): Windows' Mark of the
/// Web (the <c>Zone.Identifier</c> stream SmartScreen, Office's Protected View and the shell read) and a Mac's <c>com.apple.quarantine</c>
/// (Gatekeeper's), as Explorer and Archive Utility carry them. Unzip wrote neither, so a downloaded archive's program or document opened
/// from its folder with no prompt. An archive with no mark gives none. The mark is the app's write, straight through the file API, never
/// <see cref="WorkingDirectory.Resolve"/> (which refuses a stream name). Best effort: a volume with no streams (FAT, a share) or no extended
/// attributes keeps the files unmarked, said in the log.
/// </summary>
public static class MarkOfTheWeb
{
    /// <summary>The stream that carries the mark on Windows.</summary>
    public const string ZoneStream = ":Zone.Identifier";

    /// <summary>The extended attribute that carries it on a Mac.</summary>
    public const string QuarantineAttribute = "com.apple.quarantine";

    /// <summary>The mark on <paramref name="source"/>, or null for none (or on a system that has none).</summary>
    public static byte[]? Read(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string stream = source + ZoneStream;
                return File.Exists(stream) ? File.ReadAllBytes(stream) : null;
            }

            return OperatingSystem.IsMacOS() ? MacQuarantine.Read(source, QuarantineAttribute) : null;
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Puts <paramref name="mark"/> on each of <paramref name="targets"/>; a target that takes none is counted in the log.</summary>
    public static void Carry(byte[]? mark, IEnumerable<string> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (mark is null)
        {
            return;
        }

        int failed = 0;
        foreach (string target in targets)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    File.WriteAllBytes(target + ZoneStream, mark);
                }
                else if (OperatingSystem.IsMacOS() && !MacQuarantine.Write(target, QuarantineAttribute, mark))
                {
                    failed++;
                }
            }
            catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
            {
                failed++;
            }
        }

        if (failed > 0)
        {
            DiagnosticLog.Warn("Files", $"{failed.ToString(System.Globalization.CultureInfo.InvariantCulture)} unzipped files took no internet mark (the volume keeps none)");
        }
    }
}
