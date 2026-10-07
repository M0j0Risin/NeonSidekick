using System.Buffers.Binary;
using System.Globalization;
using NeonSidekick.UI;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>clipboard:pasteboard</c> (2026-10-07, the Mac's clipboard over NSPasteboard): the general pasteboard reached through AppKit in
    /// the published binary (its change count; nothing of the user's clipboard is read or written), and a TIFF with the picture at
    /// two sizes made a PNG of the larger through ImageIO — Preview's copy pasted. Skipped off a Mac.
    /// </summary>
    public static SmokeCheck ProbePasteboard()
    {
        const string name = "clipboard:pasteboard";
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck(name, true, "skipped: not macOS");
        }

        try
        {
            if (MacClipboard.ChangeCount() is not { } count)
            {
                return new SmokeCheck(name, false, "the pasteboard did not answer");
            }

            byte[]? png = MacClipboard.TiffOf((32, 24), (64, 48)) is { } tiff ? MacClipboard.PngFromImage(tiff) : null;
            bool sane = png is { Length: > 24 } && BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)) == 64 && BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)) == 48;
            return new SmokeCheck(name, sane, string.Create(CultureInfo.InvariantCulture,
                $"change count {count}; a two-frame TIFF {(sane ? "made a 64×48 PNG" : "was not made a PNG of its larger frame")}"));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
