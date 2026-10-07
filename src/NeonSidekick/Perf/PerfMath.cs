using System.Buffers.Binary;
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

    /// <summary>
    /// The CPU's busy share between two Mach <c>HOST_CPU_LOAD_INFO</c> readings (2026-10-07, the bar on a Mac): ticks summed over
    /// every core in four states, user, system, idle and nice; busy is everything but idle, over all four. Each is a 32-bit
    /// <c>natural_t</c> that wraps (an M4's idle stood at 2.0e9 after a few weeks up), so each delta is taken modulo 2³² — not
    /// <see cref="CpuPercent"/>'s "went backwards, no reading", which is Windows' 64-bit rule. Null when no tick passed.
    /// </summary>
    public static double? MachCpuPercent(uint user0, uint system0, uint idle0, uint nice0, uint user1, uint system1, uint idle1, uint nice1)
    {
        double busy = unchecked(user1 - user0) + (double)unchecked(system1 - system0) + unchecked(nice1 - nice0);
        double total = busy + unchecked(idle1 - idle0);
        return total <= 0 ? null : Clamp(busy / total * 100);
    }

    /// <summary>
    /// The memory in use on a Mac, in bytes (2026-10-07), as Activity Monitor's <i>Memory Used</i> counts it: app memory
    /// (anonymous pages less the purgeable ones, which the system takes back at will) plus wired plus compressed (the pages the
    /// compressor occupies, not the larger amount it holds). File cache and free pages are left out: Activity Monitor counts
    /// them as available, and a Mac keeps its cache full, so counting it would read near 100 % at rest.
    /// </summary>
    public static double MacMemoryUsedBytes(uint internalPages, uint purgeablePages, uint wiredPages, uint compressorPages, long pageSize) =>
        ((double)Math.Max(0L, (long)internalPages - purgeablePages) + wiredPages + compressorPages) * pageSize;

    /// <summary>
    /// An adapter's 64-bit byte totals from <c>net.link.generic.ifdata.&lt;index&gt;.general</c> (2026-10-07): the packed
    /// <c>ifmibdata</c>'s <c>if_data64</c>, received at byte 116 and sent at 124 (offsetof on macOS 15.7). Read there, not
    /// through <c>NetworkInterface</c> or <c>NET_RT_IFLIST2</c>, both of which hand an ordinary app 32-bit
    /// counts rounded to KiB (measured: 533 MB where netstat said 90.7 GB), which wrap every two minutes at 300 Mbit/s. Null for
    /// a buffer too short.
    /// </summary>
    public static (long Received, long Sent)? IfMibBytes(ReadOnlySpan<byte> ifmibdata) =>
        ifmibdata.Length < IfMibSentAt + 8 ? null
            : ((long)BinaryPrimitives.ReadUInt64LittleEndian(ifmibdata[IfMibReceivedAt..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(ifmibdata[IfMibSentAt..]));

    /// <summary>Where <see cref="IfMibBytes"/> reads the received bytes in an <c>ifmibdata</c>.</summary>
    public const int IfMibReceivedAt = 116;

    /// <summary>Where <see cref="IfMibBytes"/> reads the sent bytes in an <c>ifmibdata</c>.</summary>
    public const int IfMibSentAt = 124;

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

    /// <summary>
    /// Whether a PDH GPU counter's instance (<c>pid_16764_luid_0x00000000_0x00015985_phys_0</c>) is process
    /// <paramref name="pid"/>'s (2026-10-01, Embedded VRAM only): its <c>pid_</c> part names that id exactly, so pid 16 is
    /// not pid 167.
    /// </summary>
    public static bool IsProcessInstance(string? instance, int pid) =>
        instance is not null
        && instance.StartsWith(string.Create(CultureInfo.InvariantCulture, $"pid_{pid}_"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Process <paramref name="pid"/>'s values on adapter <paramref name="luid"/> in a PDH GPU counter's instances, summed;
    /// null when it has none there (2026-10-01, Embedded VRAM only: the review's finding — summed over every adapter, memory
    /// a Vulkan instance committed on a laptop's integrated GPU read as the model's spill).
    /// </summary>
    public static double? ProcessAdapterSum(IEnumerable<(string Instance, double Value)> values, int pid, long luid)
    {
        ArgumentNullException.ThrowIfNull(values);
        double sum = 0;
        bool any = false;
        foreach (var (instance, value) in values)
        {
            if (IsProcessInstance(instance, pid) && TryParseLuid(instance, out long id) && id == luid)
            {
                sum += value;
                any = true;
            }
        }

        return any ? sum : null;
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

    /// <summary>
    /// A byte counter's rate in bits/s between two readings <paramref name="elapsed"/> apart (2026-09-30, the network meters):
    /// null with no time between them; 0 for a counter that went back (an adapter reset).
    /// </summary>
    public static double? Rate(long before, long now, TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero ? null : now < before ? 0 : (now - before) * 8.0 / elapsed.TotalSeconds;

    /// <summary>A rate as a share of the link's speed (both bits/s), 0–100; null when either is unknown or the link is none.</summary>
    public static double? LinkPercent(double? rate, double? link) =>
        rate is { } r && link is { } l && l > 0 ? Clamp(r / l * 100) : null;

    /// <summary>NET: the busier direction as a share of the link, null when the link is unknown.</summary>
    public static double? NetPercent(double? down, double? up, double? link) =>
        down is null && up is null ? null : LinkPercent(Math.Max(down ?? 0, up ?? 0), link);
}
