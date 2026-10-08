using System.Globalization;
using System.Runtime.InteropServices;
using NeonSidekick.Perf;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>perf:kernel32</c> (2026-09-29): the performance bar's CPU and RAM readers in the published binary —
    /// <c>GetSystemTimes</c> twice a moment apart and <c>GlobalMemoryStatusEx</c> with its length set, each struct read back
    /// sane (the idle time within the kernel time, the memory's load and totals in range), which proves the source-generated
    /// stubs kept their layouts under AOT.
    /// </summary>
    public static SmokeCheck ProbePerfKernel32()
    {
        const string name = "perf:kernel32";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            if (!PerfNative.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return new SmokeCheck(name, false, $"GetSystemTimes failed ({Marshal.GetLastPInvokeError()})");
            }

            Thread.Sleep(50);
            PerfNative.GetSystemTimes(out var idle2, out var kernel2, out var user2);
            double? cpu = PerfMath.CpuPercent(idle.Ticks, kernel.Ticks, user.Ticks, idle2.Ticks, kernel2.Ticks, user2.Ticks);

            var memory = new PerfNative.MemoryStatusEx { Length = (uint)Marshal.SizeOf<PerfNative.MemoryStatusEx>() };
            if (!PerfNative.GlobalMemoryStatusEx(ref memory))
            {
                return new SmokeCheck(name, false, $"GlobalMemoryStatusEx failed ({Marshal.GetLastPInvokeError()})");
            }

            bool sane = idle.Ticks <= kernel.Ticks && kernel.Ticks > 0 && memory.MemoryLoad <= 100 && memory.TotalPhys > 0 && memory.AvailPhys <= memory.TotalPhys;
            return new SmokeCheck(name, sane, string.Create(CultureInfo.InvariantCulture,
                $"cpu {(cpu is { } c ? c.ToString("0", CultureInfo.InvariantCulture) + "%" : "n/a")}, memory load {memory.MemoryLoad}% of {memory.TotalPhys / (1024 * 1024)} MB{(sane ? "" : ": a reading out of range")}"));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>perf:nvml</c> (2026-09-29): NVIDIA's NVML in the published binary — started, a device's load and memory read
    /// (used within total), stopped. Passes, skipped, where no NVIDIA driver answers: the vendor-neutral reader is then the
    /// bar's, and <c>perf:pdh-dxgi</c> proves it.
    /// </summary>
    public static SmokeCheck ProbePerfNvml()
    {
        const string name = "perf:nvml";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            using var gpu = NvmlGpu.TryOpen();
            if (gpu is null)
            {
                return new SmokeCheck(name, true, "skipped: no NVIDIA GPU answered");
            }

            var (load, vram) = gpu.Read();
            bool sane = load is not null && vram is not null;
            return new SmokeCheck(name, sane, Readings(load, vram) + (sane ? "" : ": a reading is missing"));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>perf:pdh-dxgi</c> (2026-09-29): the vendor-neutral GPU reader in the published binary — DXGI's factory and
    /// adapters through their vtables (the adapter with the most dedicated memory, its LUID), PDH's two GPU counters
    /// opened, collected twice and read for that LUID. Passes, skipped, on a machine with no hardware adapter.
    /// </summary>
    public static SmokeCheck ProbePerfPdhDxgi()
    {
        const string name = "perf:pdh-dxgi";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            if (PdhGpu.BiggestAdapter() is not { } adapter)
            {
                return new SmokeCheck(name, true, "skipped: no hardware adapter");
            }

            using var gpu = PdhGpu.TryOpen();
            if (gpu is null)
            {
                return new SmokeCheck(name, false, "the GPU counters did not open");
            }

            Thread.Sleep(100);
            var (load, vram) = gpu.Read();
            return new SmokeCheck(name, vram is not null, string.Create(CultureInfo.InvariantCulture,
                $"adapter luid 0x{adapter.Luid:X}, {adapter.Total / (1024 * 1024 * 1024):0.#} GB dedicated; ") + Readings(load, vram) + (vram is null ? ": no memory reading for the adapter" : ""));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Readings(double? load, double? vram) =>
        $"load {Reading(load)}, vram {Reading(vram)}";

    private static string Reading(double? value) => value is { } v ? v.ToString("0", CultureInfo.InvariantCulture) + "%" : "n/a";

    /// <summary>
    /// <c>perf:mach</c> (2026-10-07, the bar on a Mac): the CPU and RAM readers in the published binary — Mach's
    /// <c>HOST_CPU_LOAD_INFO</c> twice a moment apart and <c>HOST_VM_INFO64</c> with <c>hw.memsize</c> and <c>hw.pagesize</c>, the
    /// memory in use read back within the machine's memory, which proves the buffers' offsets and the stubs under AOT.
    /// </summary>
    public static SmokeCheck ProbePerfMach()
    {
        const string name = "perf:mach";
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck(name, true, "skipped: not macOS");
        }

        try
        {
            using var source = new MacPerfSource();
            if (source.ReadCpu() is not { } before)
            {
                return new SmokeCheck(name, false, "HOST_CPU_LOAD_INFO did not answer");
            }

            Thread.Sleep(50);
            var after = source.ReadCpu() ?? before;
            double? cpu = PerfMath.MachCpuPercent(before.User, before.System, before.Idle, before.Nice, after.User, after.System, after.Idle, after.Nice);
            if (source.ReadUsedBytes() is not { } used || source.MemoryBytes is not { } total)
            {
                return new SmokeCheck(name, false, "HOST_VM_INFO64 or hw.memsize did not answer");
            }

            bool sane = used > 0 && used <= total;
            return new SmokeCheck(name, sane, string.Create(CultureInfo.InvariantCulture,
                $"cpu {Reading(cpu)}, memory used {used / 1_048_576:0} of {total / 1_048_576} MB{(sane ? "" : ": a reading out of range")}"));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>perf:iokit</c> (2026-10-07): the GPU reader in the published binary — the IOAccelerator service found, its
    /// <c>PerformanceStatistics</c> read through IOKit and CoreFoundation, the load within 0–100 and the memory in use within
    /// Metal's working set's order. A Mac with no such statistics (a CI virtual machine's paravirtual GPU) passes as not
    /// exercised: the frameworks loaded and answered.
    /// </summary>
    public static SmokeCheck ProbePerfIOKit()
    {
        const string name = "perf:iokit";
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck(name, true, "skipped: not macOS");
        }

        try
        {
            using var gpu = MacGpu.TryOpen();
            if (gpu is null)
            {
                return new SmokeCheck(name, true, "not exercised: no IOAccelerator statistics");
            }

            var (load, inUse) = gpu.ReadRaw();
            bool sane = load is >= 0 and <= 100 && inUse is >= 0;
            return new SmokeCheck(name, sane, string.Create(CultureInfo.InvariantCulture,
                $"load {(load is { } l ? l + "%" : "n/a")}, in use {(inUse is { } b ? (b / 1_048_576).ToString(CultureInfo.InvariantCulture) + " MiB" : "n/a")}, working set {(gpu.WorkingSetBytes is { } w ? (w / 1_048_576).ToString(CultureInfo.InvariantCulture) + " MiB" : "n/a")}{(sane ? "" : ": a reading out of range")}"));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>perf:network</c> (2026-09-30): the performance bar's network meters in the published binary — .NET's own adapter
    /// counters (<see cref="NetworkCounters"/>: <c>NetworkInterface</c>, the adapters that are up with a gateway, their byte
    /// totals and link speeds) read twice a moment apart through <see cref="NetworkMeter"/>, which proves the runtime's path
    /// survives trimming. A machine with no adapter carrying traffic passes, saying so. On a Mac (2026-10-07) the counts are
    /// <see cref="MacNetworkCounters"/>' 64-bit ones, so the sysctl behind them is proved too.
    /// </summary>
    public static SmokeCheck ProbePerfNetwork()
    {
        const string name = "perf:network";
        try
        {
            INetworkCounters counters = OperatingSystem.IsMacOS() ? new MacNetworkCounters() : new NetworkCounters();
            var adapters = counters.Read();
            var meter = new NetworkMeter(counters);
            meter.Sample();
            Thread.Sleep(50);
            var rates = meter.Sample();
            bool sane = adapters.All(a => a.Received >= 0 && a.Sent >= 0 && a.LinkBits >= 0) && (rates is null || rates.Value.Down >= 0 && rates.Value.Up >= 0);
            string detail = adapters.Count == 0
                ? "no adapter with a gateway: the network meters are left out"
                : string.Create(CultureInfo.InvariantCulture, $"{adapters.Count} adapter{(adapters.Count == 1 ? "" : "s")} with a gateway, fastest link {PerfText.Rate(adapters.Max(a => (double)a.LinkBits)).Trim()}");
            return new SmokeCheck(name, sane, sane ? detail : detail + ": a reading out of range");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
