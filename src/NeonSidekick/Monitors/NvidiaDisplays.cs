using System.Globalization;

namespace NeonSidekick.Monitors;

/// <summary>
/// One display NVIDIA's driver lists (2026-10-27, the NVIDIA plan): its EDID's PnP id (<see cref="NvidiaDisplays.PnpId"/>, null when the EDID
/// didn't read), its adapter's LUID as Windows writes it in a target (sixteen hex digits), whether NVIDIA has it on the desktop, and the GPU
/// handle and output mask its I2C calls take.
/// </summary>
public sealed record NvidiaDisplay(string? PnpId, string Adapter, bool Active, nint Gpu, uint Output);

/// <summary>
/// Matching NVIDIA's displays to Windows' targets (2026-10-27, the NVIDIA plan; the spike's <c>match.cs</c>): the EDID's PnP id is the model
/// id in a target's device path (<c>AUSAA34</c>, as <see cref="MonitorKey.Model"/> keeps it lower-cased), and NVIDIA's logical GPU gives the
/// adapter LUID Windows puts in each target id. The XG27's EDID serial is a placeholder (<c>01010101</c>), so serials are no use. Pure.
/// </summary>
public static class NvidiaDisplays
{
    /// <summary>
    /// The PnP id of an EDID's header: bytes 8–9 big-endian, three 5-bit letters with 1 = A, then bytes 10–11 little-endian as four hex
    /// digits (<c>AUSAA34</c>); null for an EDID shorter than its header or a letter out of range.
    /// </summary>
    public static string? PnpId(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 12)
        {
            return null;
        }

        int maker = (edid[8] << 8) | edid[9];
        Span<char> letters = stackalloc char[3];
        for (int i = 0; i < 3; i++)
        {
            int letter = (maker >> (10 - (5 * i))) & 31;
            if (letter is < 1 or > 26)
            {
                return null;
            }

            letters[i] = (char)('A' - 1 + letter);
        }

        int product = edid[10] | (edid[11] << 8);
        return new string(letters) + product.ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The NVIDIA display behind <paramref name="monitor"/> (off the desktop) in <paramref name="snapshot"/>, or null: reached only when exactly
    /// one of NVIDIA's displays off its desktop and exactly one of Windows' targets off the desktop share the monitor's model id and adapter.
    /// Two identical monitors both off the desktop, a monitor with no key, one on the desktop, or one on another maker's card stay unreached.
    /// </summary>
    public static NvidiaDisplay? Match(IReadOnlyList<NvidiaDisplay> displays, MonitorSnapshot snapshot, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(monitor);
        if (monitor.Active || monitor.Key is not { } key || Adapter(monitor.Target) is not { } adapter)
        {
            return null;
        }

        string model = MonitorKey.Model(key);
        bool Same(MonitorInfo m) => !m.Active && m.Key is { } k && string.Equals(MonitorKey.Model(k), model, StringComparison.Ordinal)
            && string.Equals(Adapter(m.Target), adapter, StringComparison.OrdinalIgnoreCase);

        if (snapshot.Monitors.Count(Same) != 1 || !Same(snapshot.ByTarget(monitor.Target) ?? monitor))
        {
            return null;
        }

        var found = displays.Where(d => !d.Active && string.Equals(d.PnpId, model, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Adapter, adapter, StringComparison.OrdinalIgnoreCase)).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>A LUID as a target id writes it: the high part then the low part, eight hex digits each.</summary>
    public static string Luid(long luid) =>
        ((uint)(luid >> 32)).ToString("X8", CultureInfo.InvariantCulture) + ((uint)luid).ToString("X8", CultureInfo.InvariantCulture);

    // The adapter part of a target id ("00000000000152C3:4355" → its first sixteen hex digits), or null for another shape.
    private static string? Adapter(string target)
    {
        int colon = target.IndexOf(':', StringComparison.Ordinal);
        return colon == 16 ? target[..16] : null;
    }
}
