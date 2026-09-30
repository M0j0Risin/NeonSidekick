using System.Globalization;

namespace NeonSidekick.Perf;

/// <summary>
/// The performance bar's arithmetic (2026-09-29), apart from the native calls so the tests can pin it: CPU from two
/// <c>GetSystemTimes</c> readings, a used-of-total share, and the GPU load from PDH's per-engine counters the way Task
/// Manager reads them.
/// </summary>
public static class PerfMath
{
    /// <summary>
    /// The CPU's busy share between two <c>GetSystemTimes</c> readings (100 ns ticks, every core summed): kernel time
    /// includes idle, so busy is kernel plus user less idle, over kernel plus user. Null when no time passed.
    /// </summary>
    public static double? CpuPercent(ulong idle0, ulong kernel0, ulong user0, ulong idle1, ulong kernel1, ulong user1)
    {
        if (idle1 < idle0 || kernel1 < kernel0 || user1 < user0)
        {
            return null;   // a counter went backwards: no delta to trust
        }

        double total = (kernel1 - kernel0) + (double)(user1 - user0);
        if (total <= 0)
        {
            return null;
        }

        return Clamp((total - (idle1 - idle0)) / total * 100);
    }

    /// <summary><paramref name="used"/> of <paramref name="total"/> as a percentage; null when the total is unknown.</summary>
    public static double? Percent(double used, double total) => total <= 0 ? null : Clamp(used / total * 100);

    /// <summary>
    /// The adapter id in a PDH GPU counter's instance name (<c>pid_4_luid_0x00000000_0x0000D1B5_phys_0_eng_3_engtype_Copy</c>
    /// or <c>luid_0x00000000_0x0000D1B5_phys_0</c>): the LUID's high part, then its low part, as DXGI reports them.
    /// </summary>
    public static bool TryParseLuid(string? instance, out long luid)
    {
        luid = 0;
        if (instance is null)
        {
            return false;
        }

        int at = instance.IndexOf("luid_0x", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || at + 7 + 8 + 3 + 8 > instance.Length || string.Compare(instance, at + 15, "_0x", 0, 3, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        if (!uint.TryParse(instance.AsSpan(at + 7, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint high)
            || !uint.TryParse(instance.AsSpan(at + 18, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint low))
        {
            return false;
        }

        luid = Luid(high, low);
        return true;
    }

    /// <summary>A LUID as one number: the high part over the low.</summary>
    public static long Luid(uint high, uint low) => (long)(((ulong)high << 32) | low);

    /// <summary>
    /// The GPU's load from <c>\GPU Engine(*)\Utilization Percentage</c>: each engine of the adapter <paramref name="luid"/>
    /// (its <c>phys_…_eng_…</c> part) summed over the processes using it, the busiest engine the answer — Task Manager's
    /// reading. Null when no engine of the adapter answered.
    /// </summary>
    public static double? GpuEnginePercent(IEnumerable<(string Instance, double Value)> engines, long luid)
    {
        ArgumentNullException.ThrowIfNull(engines);
        var byEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in engines)
        {
            if (!TryParseLuid(instance, out long id) || id != luid)
            {
                continue;
            }

            int phys = instance.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
            int type = instance.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            string engine = phys >= 0 && type > phys ? instance[phys..type] : instance;
            byEngine[engine] = byEngine.GetValueOrDefault(engine) + Math.Max(0, value);
        }

        return byEngine.Count == 0 ? null : Clamp(byEngine.Values.Max());
    }

    /// <summary>A reading kept inside 0–100.</summary>
    public static double Clamp(double percent) => double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100);
}
