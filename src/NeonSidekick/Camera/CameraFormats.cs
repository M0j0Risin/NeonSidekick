using System.Globalization;

namespace NeonSidekick.Camera;

/// <summary>
/// Which of a camera's own formats the stream runs at, and the attribute arithmetic around it (2026-10-02). Pure: the native
/// layer reads the formats and asks <see cref="Pick"/>, the tests feed it lists.
/// </summary>
public static class CameraFormats
{
    /// <summary>The frame rate a camera is run at when it offers a choice: enough for a live view, no more CPU than needed.</summary>
    public const double PreferredFps = 30;

    /// <summary>Under this a format is taken only when nothing faster offers the size.</summary>
    public const double MinimumFps = 15;

    /// <summary>
    /// The format nearest <paramref name="target"/>: the exact size when offered; else the smallest size at least as large both
    /// ways (the encode scales it down); else the largest smaller one. Among the formats of that size, a rate of at least
    /// <see cref="MinimumFps"/> nearest <see cref="PreferredFps"/>, then NV12 over YUY2 over MJPG over the rest (no decode
    /// beats a decode). Null for an empty list.
    /// </summary>
    public static CameraFormat? Pick(IReadOnlyList<CameraFormat> formats, CameraSize target)
    {
        ArgumentNullException.ThrowIfNull(formats);
        if (formats.Count == 0)
        {
            return null;
        }

        var sizes = formats.Select(f => f.Size).Distinct().ToList();
        CameraSize size = sizes.Contains(target) ? target
            : sizes.Where(s => s.Width >= target.Width && s.Height >= target.Height).OrderBy(s => s.Area).Cast<CameraSize?>().FirstOrDefault()
              ?? sizes.OrderByDescending(s => s.Area).First();

        return formats.Where(f => f.Size == size)
            .OrderBy(f => f.Fps >= MinimumFps ? 0 : 1)
            .ThenBy(f => Math.Abs(f.Fps - PreferredFps))
            .ThenBy(f => SubtypeRank(f.Subtype))
            .First();
    }

    /// <summary>
    /// NV12 0, YUY2 1, MJPG 2, anything else 3. A Mac's FourCCs rank with their Windows twins (2026-10-07): <c>420v</c>/<c>420f</c>
    /// are NV12's bi-planar 4:2:0, <c>yuvs</c>/<c>2vuy</c> packed 4:2:2 as YUY2, <c>dmb1</c> Motion JPEG. Windows never names them.
    /// </summary>
    public static int SubtypeRank(string subtype) => subtype switch
    {
        "NV12" or "420v" or "420f" => 0,
        "YUY2" or "yuvs" or "2vuy" => 1,
        "MJPG" or "dmb1" => 2,
        _ => 3,
    };

    /// <summary><c>MF_MT_FRAME_SIZE</c>'s packing: the width in the high 32 bits, the height in the low.</summary>
    public static CameraSize UnpackSize(ulong packed) => new((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));

    public static ulong PackSize(CameraSize size) => ((ulong)(uint)size.Width << 32) | (uint)size.Height;

    /// <summary><c>MF_MT_FRAME_RATE</c>'s packing: the numerator high, the denominator low; 0 for a zero denominator.</summary>
    public static double UnpackRate(ulong packed)
    {
        uint denominator = (uint)(packed & 0xFFFFFFFF);
        return denominator == 0 ? 0 : (double)(uint)(packed >> 32) / denominator;
    }

    /// <summary>
    /// A media subtype's name: the FourCC its first field holds when that is four printable characters (<c>NV12</c>,
    /// <c>MJPG</c>), else the GUID.
    /// </summary>
    public static string SubtypeName(Guid subtype)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = subtype.TryWriteBytes(bytes);
        for (int i = 0; i < 4; i++)
        {
            if (bytes[i] is < 0x20 or > 0x7E)
            {
                return subtype.ToString("D", CultureInfo.InvariantCulture);
            }
        }

        return string.Create(4, bytes[..4].ToArray(), static (span, b) =>
        {
            for (int i = 0; i < 4; i++)
            {
                span[i] = (char)b[i];
            }
        });
    }

    /// <summary>
    /// A <c>Camera resolution</c> value read: <c>1280x720</c> (an <c>×</c> or a capital X too, spaces ignored); false for
    /// anything else or a side outside 16–8192.
    /// </summary>
    public static bool TryParseSize(string? text, out CameraSize size)
    {
        size = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Replace(" ", "", StringComparison.Ordinal).Split(['x', 'X', '×']);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            || width is < 16 or > 8192 || height is < 16 or > 8192)
        {
            return false;
        }

        size = new CameraSize(width, height);
        return true;
    }

    /// <summary>A format as the check and the device list print it: <c>1280x720 MJPG 30 fps</c>.</summary>
    public static string Describe(CameraFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return $"{format.Size} {format.Subtype} {format.Fps.ToString("0.##", CultureInfo.InvariantCulture)} fps";
    }
}
