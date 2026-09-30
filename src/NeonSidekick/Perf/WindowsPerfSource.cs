using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Perf;

/// <summary>
/// The machine as Windows reports it (2026-09-29, the performance bar): the CPU from two <c>GetSystemTimes</c> readings a
/// sample apart, RAM from <c>GlobalMemoryStatusEx</c>, and the GPU from the first reader that answers — NVML where an
/// NVIDIA GPU is (<see cref="NvmlGpu"/>: its own load and memory counts), else Windows' counters for the adapter with the
/// most dedicated memory (<see cref="PdhGpu"/>: works for AMD and Intel too, and skips an integrated GPU beside a discrete
/// one), else no GPU meters (the user's call: NVML when it can, the vendor-neutral way otherwise).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPerfSource : IPerfSource
{
    private const string Category = "Perf";

    private readonly IGpuReader? _gpu;
    private ulong _idle;
    private ulong _kernel;
    private ulong _user;
    private bool _havePrevious;

    public WindowsPerfSource()
    {
        _gpu = NvmlGpu.TryOpen() ?? (IGpuReader?)PdhGpu.TryOpen();
        DiagnosticLog.Info(Category, _gpu is null ? "Performance bar: CPU and RAM (no GPU reader answered)" : $"Performance bar: CPU, RAM and GPU through {_gpu.Name}");
    }

    /// <summary>The GPU reader in use: <c>nvml</c>, <c>pdh</c>, or null.</summary>
    public string? GpuReader => _gpu?.Name;

    public PerfSnapshot Sample()
    {
        double? cpu = null;
        if (PerfNative.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (_havePrevious)
            {
                cpu = PerfMath.CpuPercent(_idle, _kernel, _user, idle.Ticks, kernel.Ticks, user.Ticks);
            }

            (_idle, _kernel, _user, _havePrevious) = (idle.Ticks, kernel.Ticks, user.Ticks, true);
        }

        double? ram = null;
        var memory = new PerfNative.MemoryStatusEx { Length = (uint)Marshal.SizeOf<PerfNative.MemoryStatusEx>() };
        if (PerfNative.GlobalMemoryStatusEx(ref memory))
        {
            ram = PerfMath.Percent(memory.TotalPhys - memory.AvailPhys, memory.TotalPhys);
        }

        var (gpu, vram) = _gpu?.Read() ?? (null, null);
        return new PerfSnapshot(cpu, ram, gpu, vram);
    }

    public void Dispose() => _gpu?.Dispose();
}

/// <summary>One way to read the GPU: its load and its memory in use, each a percentage or null.</summary>
internal interface IGpuReader : IDisposable
{
    /// <summary>A short name for the log and the smoke: <c>nvml</c> or <c>pdh</c>.</summary>
    string Name { get; }

    (double? Load, double? Vram) Read();
}

/// <summary>
/// An NVIDIA GPU through NVML (2026-09-29): every device's load (the busiest one's) and memory (summed over them all).
/// <see cref="TryOpen"/> is null where <c>nvml.dll</c> is missing, fails to start, or finds no device.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NvmlGpu : IGpuReader
{
    private readonly nint[] _devices;
    private bool _disposed;

    private NvmlGpu(nint[] devices) => _devices = devices;

    public string Name => "nvml";

    public static NvmlGpu? TryOpen()
    {
        try
        {
            if (PerfNative.NvmlInit() != PerfNative.NvmlSuccess)
            {
                return null;
            }

            if (PerfNative.NvmlDeviceGetCount(out uint count) != PerfNative.NvmlSuccess || count == 0)
            {
                PerfNative.NvmlShutdown();
                return null;
            }

            var devices = new List<nint>();
            for (uint i = 0; i < count; i++)
            {
                if (PerfNative.NvmlDeviceGetHandleByIndex(i, out nint device) == PerfNative.NvmlSuccess)
                {
                    devices.Add(device);
                }
            }

            if (devices.Count == 0)
            {
                PerfNative.NvmlShutdown();
                return null;
            }

            return new NvmlGpu([.. devices]);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;   // no NVIDIA driver here: the vendor-neutral reader's turn
        }
    }

    public (double? Load, double? Vram) Read()
    {
        double? load = null;
        double used = 0;
        double total = 0;
        foreach (nint device in _devices)
        {
            if (PerfNative.NvmlDeviceGetUtilizationRates(device, out var utilization) == PerfNative.NvmlSuccess)
            {
                load = Math.Max(load ?? 0, utilization.Gpu);
            }

            if (PerfNative.NvmlDeviceGetMemoryInfo(device, out var memory) == PerfNative.NvmlSuccess)
            {
                used += memory.Used;
                total += memory.Total;
            }
        }

        return (load is { } l ? PerfMath.Clamp(l) : null, PerfMath.Percent(used, total));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            PerfNative.NvmlShutdown();
        }
    }
}

/// <summary>
/// Any GPU through Windows' own counters (2026-09-29): DXGI names the hardware adapter with the most dedicated memory (the
/// discrete card, not an integrated GPU beside it) with its LUID and total; PDH's <c>\GPU Engine(*)\Utilization Percentage</c>
/// and <c>\GPU Adapter Memory(*)\Dedicated Usage</c>, filtered to that LUID, give the load (<see cref="PerfMath.GpuEnginePercent"/>)
/// and the memory in use. <see cref="TryOpen"/> is null where there is no hardware adapter or the counters do not open.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class PdhGpu : IGpuReader
{
    private const string EngineCounter = @"\GPU Engine(*)\Utilization Percentage";
    private const string MemoryCounter = @"\GPU Adapter Memory(*)\Dedicated Usage";

    private readonly nint _query;
    private readonly nint _engines;
    private readonly nint _memory;
    private readonly long _luid;
    private readonly double _total;
    private bool _disposed;

    private PdhGpu(nint query, nint engines, nint memory, long luid, double total)
    {
        (_query, _engines, _memory, _luid, _total) = (query, engines, memory, luid, total);
    }

    public string Name => "pdh";

    /// <summary>The adapter the counters are read for: its LUID and dedicated memory in bytes.</summary>
    internal (long Luid, double Total) Adapter => (_luid, _total);

    public static PdhGpu? TryOpen()
    {
        try
        {
            if (BiggestAdapter() is not { } adapter)
            {
                return null;
            }

            var (luid, total) = adapter;

            if (PerfNative.PdhOpenQuery(null, 0, out nint query) != 0)
            {
                return null;
            }

            if (PerfNative.PdhAddEnglishCounter(query, EngineCounter, 0, out nint engines) != 0
                || PerfNative.PdhAddEnglishCounter(query, MemoryCounter, 0, out nint memory) != 0)
            {
                PerfNative.PdhCloseQuery(query);
                return null;
            }

            PerfNative.PdhCollectQueryData(query);   // the first of the two a rate counter needs
            return new PdhGpu(query, engines, memory, luid, total);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The hardware adapter with the most dedicated memory, by DXGI: its LUID and that memory in bytes; null when there is none.</summary>
    internal static (long Luid, double Total)? BiggestAdapter()
    {
        if (PerfNative.CreateDXGIFactory1(PerfNative.DxgiFactory1Iid, out nint factory) < 0 || factory == 0)
        {
            return null;
        }

        try
        {
            (long Luid, double Total)? best = null;
            for (uint i = 0; PerfNative.EnumAdapters1(factory, i, out nint adapter) != PerfNative.DxgiErrorNotFound; i++)
            {
                if (adapter == 0)
                {
                    break;
                }

                try
                {
                    if (PerfNative.GetDesc1(adapter, out var desc) >= 0
                        && (desc.Flags & PerfNative.DxgiAdapterFlagSoftware) == 0
                        && desc.DedicatedVideoMemory > 0
                        && (best is null || desc.DedicatedVideoMemory > best.Value.Total))
                    {
                        best = (PerfMath.Luid(unchecked((uint)desc.LuidHighPart), desc.LuidLowPart), desc.DedicatedVideoMemory);
                    }
                }
                finally
                {
                    PerfNative.Release(adapter);
                }
            }

            return best;
        }
        finally
        {
            PerfNative.Release(factory);
        }
    }

    public (double? Load, double? Vram) Read()
    {
        if (PerfNative.PdhCollectQueryData(_query) != 0)
        {
            return (null, null);
        }

        double? load = PerfMath.GpuEnginePercent(Values(_engines), _luid);
        double used = 0;
        bool any = false;
        foreach (var (instance, value) in Values(_memory))
        {
            if (PerfMath.TryParseLuid(instance, out long luid) && luid == _luid)
            {
                used += value;
                any = true;
            }
        }

        return (load, any ? PerfMath.Percent(used, _total) : null);
    }

    /// <summary>A wildcard counter's instances and values now; the instances whose value is not valid are left out.</summary>
    private static List<(string Instance, double Value)> Values(nint counter)
    {
        var values = new List<(string, double)>();
        uint bytes = 0;
        uint status = PerfNative.PdhGetFormattedCounterArray(counter, PerfNative.PdhFmtDouble | PerfNative.PdhFmtNoCap100, ref bytes, out _, null);
        if (status != PerfNative.PdhMoreData || bytes == 0)
        {
            return values;
        }

        byte[] buffer = new byte[bytes];
        fixed (byte* items = buffer)
        {
            if (PerfNative.PdhGetFormattedCounterArray(counter, PerfNative.PdhFmtDouble | PerfNative.PdhFmtNoCap100, ref bytes, out uint count, items) != 0)
            {
                return values;
            }

            var item = (PerfNative.PdhFmtCounterValueItem*)items;
            for (uint i = 0; i < count; i++)
            {
                if (item[i].Value.CStatus is PerfNative.PdhCStatusValidData or PerfNative.PdhCStatusNewData && item[i].Name is not null)
                {
                    values.Add((new string(item[i].Name), item[i].Value.DoubleValue));
                }
            }
        }

        return values;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            PerfNative.PdhCloseQuery(_query);
        }
    }
}
