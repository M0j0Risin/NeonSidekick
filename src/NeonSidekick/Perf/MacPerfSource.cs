using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Perf;

/// <summary>
/// The machine as macOS reports it (2026-10-07, the user's ask: the bar had only PROC on a Mac), <see cref="WindowsPerfSource"/>'s
/// twin over <see cref="MacPerfNative"/>:
/// <list type="bullet">
/// <item>CPU from two Mach <c>HOST_CPU_LOAD_INFO</c> readings a sample apart (<see cref="PerfMath.MachCpuPercent"/>); the first has
/// no previous one, as on Windows.</item>
/// <item>RAM as Activity Monitor's <i>Memory Used</i> (<see cref="PerfMath.MacMemoryUsedBytes"/>, from <c>HOST_VM_INFO64</c>) over
/// <c>hw.memsize</c>.</item>
/// <item>GPU and GMEM from the GPU's IOAccelerator service (<see cref="MacGpu"/>).</item>
/// <item>The network from .NET's adapters with their byte counts read again at 64 bits (<see cref="MacNetworkCounters"/>).</item>
/// </list>
/// The host port, the page size, the memory size and the IOKit entry are taken once, here, and given back in
/// <see cref="Dispose"/>; a sample is a few system calls on the sampler's timer thread.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacPerfSource : IPerfSource
{
    private const string Category = "Perf";

    private readonly uint _host;
    private readonly long? _memoryBytes;
    private readonly long? _pageSize;
    private readonly MacGpu? _gpu;
    private readonly NetworkMeter _net = new(new MacNetworkCounters());
    private uint _user;
    private uint _system;
    private uint _idle;
    private uint _nice;
    private bool _havePrevious;
    private bool _disposed;

    public MacPerfSource()
    {
        _host = MacPerfNative.mach_host_self();
        _memoryBytes = MacPerfNative.SysctlInteger("hw.memsize");
        _pageSize = MacPerfNative.SysctlInteger("hw.pagesize");
        _gpu = MacGpu.TryOpen();
        DiagnosticLog.Info(Category, _gpu is null ? "Performance bar: CPU and RAM (no IOAccelerator statistics)" : "Performance bar: CPU, RAM and GPU through IOKit");
    }

    public PerfSnapshot Sample(PerfReads reads)
    {
        // As on Windows: a reader the checked meters do not need is not run, and the CPU's and the network's rates start over.
        double? cpu = null;
        if ((reads & PerfReads.Cpu) == 0)
        {
            _havePrevious = false;
        }
        else if (ReadCpu() is { } ticks)
        {
            if (_havePrevious)
            {
                cpu = PerfMath.MachCpuPercent(_user, _system, _idle, _nice, ticks.User, ticks.System, ticks.Idle, ticks.Nice);
            }

            (_user, _system, _idle, _nice, _havePrevious) = (ticks.User, ticks.System, ticks.Idle, ticks.Nice, true);
        }

        double? ram = (reads & PerfReads.Ram) != 0 ? ReadRam() : null;
        var (gpu, gmem) = (reads & PerfReads.Gpu) != 0 ? _gpu?.Read() ?? (null, null) : (null, null);
        (double Down, double Up, double Link)? net = null;
        if ((reads & PerfReads.Net) != 0)
        {
            net = _net.Sample();
        }
        else
        {
            _net.Reset();
        }

        return new PerfSnapshot(cpu, ram, gpu, gmem, net?.Down, net?.Up, net?.Link);
    }

    /// <summary>The four tick counters now, every core summed; null when the host would not say.</summary>
    internal (uint User, uint System, uint Idle, uint Nice)? ReadCpu()
    {
        uint* ticks = stackalloc uint[MacPerfNative.HostCpuLoadInfoCount];
        uint count = MacPerfNative.HostCpuLoadInfoCount;
        return MacPerfNative.host_statistics64(_host, MacPerfNative.HostCpuLoadInfo, (int*)ticks, &count) == MacPerfNative.KernSuccess
            && count >= MacPerfNative.HostCpuLoadInfoCount
            ? (ticks[0], ticks[1], ticks[2], ticks[3])
            : null;
    }

    /// <summary>Memory Used as a share of the memory; null when the statistics or the sizes are not there.</summary>
    internal double? ReadRam() =>
        ReadUsedBytes() is { } used && _memoryBytes is { } total ? PerfMath.Percent(used, total) : null;

    /// <summary>Memory Used in bytes (<see cref="PerfMath.MacMemoryUsedBytes"/>); null when unread.</summary>
    internal double? ReadUsedBytes()
    {
        if (_pageSize is not { } pageSize)
        {
            return null;
        }

        byte* stats = stackalloc byte[MacPerfNative.HostVmInfo64Count * sizeof(int)];
        uint count = MacPerfNative.HostVmInfo64Count;
        if (MacPerfNative.host_statistics64(_host, MacPerfNative.HostVmInfo64, (int*)stats, &count) != MacPerfNative.KernSuccess
            || count * sizeof(int) < MacPerfNative.VmInternalPageCount + sizeof(uint))
        {
            return null;
        }

        return PerfMath.MacMemoryUsedBytes(
            *(uint*)(stats + MacPerfNative.VmInternalPageCount), *(uint*)(stats + MacPerfNative.VmPurgeableCount),
            *(uint*)(stats + MacPerfNative.VmWireCount), *(uint*)(stats + MacPerfNative.VmCompressorPageCount), pageSize);
    }

    /// <summary>The machine's memory in bytes (<c>hw.memsize</c>), for the smoke.</summary>
    internal long? MemoryBytes => _memoryBytes;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gpu?.Dispose();
        MacPerfNative.mach_port_deallocate(MacPerfNative.MachTaskSelf(), _host);   // mach_host_self() hands out a send right each call
    }
}

/// <summary>
/// The Mac's GPU through IOKit (2026-10-07): the IOAccelerator service's <c>PerformanceStatistics</c> dictionary, which any user
/// may read (no root, no entitlement). <c>Device Utilization %</c> is the load, what Activity Monitor's GPU History draws (an M4:
/// 4 % at rest, 98–99 % under llama-bench). GMEM, the bar's VRAM meter on a Mac, is <c>In use system memory</c> — the unified
/// memory the GPU's allocations hold now (0.9 GB at rest, 4.3–4.8 GB with Gemma 4 E2B loaded) — over Metal's recommended
/// working set (<see cref="GpuMemory.MetalWorkingSetBytes"/>, the embedded LLM's budget: 10,922 MiB of 16 GiB), so the meter
/// fills as the embedded model's own budget does. The one property is fetched per sample
/// (<c>IORegistryEntryCreateCFProperty</c>, 14 µs measured), not every property of the entry (1.1 ms); the entry and the
/// two key strings are held for the source's life.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacGpu : IDisposable
{
    private readonly uint _entry;
    private readonly nint _statistics;
    private readonly nint _utilization;
    private readonly nint _inUse;
    private readonly long? _workingSet;
    private bool _disposed;

    private MacGpu(uint entry, long? workingSet)
    {
        _entry = entry;
        _workingSet = workingSet;
        _statistics = MacPerfNative.CFStringCreateWithCString(0, "PerformanceStatistics", MacPerfNative.Utf8);
        _utilization = MacPerfNative.CFStringCreateWithCString(0, "Device Utilization %", MacPerfNative.Utf8);
        _inUse = MacPerfNative.CFStringCreateWithCString(0, "In use system memory", MacPerfNative.Utf8);
    }

    /// <summary>The GPU's reader; null with no IOAccelerator service, or one whose statistics carry no load (a VM's paravirtual GPU).</summary>
    public static MacGpu? TryOpen()
    {
        try
        {
            uint entry = MacPerfNative.IOServiceGetMatchingService(0, MacPerfNative.IOServiceMatching("IOAccelerator"));
            if (entry == 0)
            {
                return null;
            }

            var gpu = new MacGpu(entry, GpuMemory.MetalWorkingSetBytes());
            if (gpu.Read().Load is null)
            {
                gpu.Dispose();
                return null;
            }

            return gpu;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The load and GMEM, each a percentage or null.</summary>
    public (double? Load, double? Vram) Read()
    {
        var (load, inUse) = ReadRaw();
        return (load is { } l ? PerfMath.Clamp(l) : null, inUse is { } bytes && _workingSet is { } total ? PerfMath.Percent(bytes, total) : null);
    }

    /// <summary>The two statistics as IOKit gives them: the load in percent and the memory in use in bytes, each null when missing.</summary>
    public (long? Load, long? InUseBytes) ReadRaw()
    {
        nint statistics = MacPerfNative.IORegistryEntryCreateCFProperty(_entry, _statistics, 0, 0);   // +1: released below
        if (statistics == 0)
        {
            return (null, null);
        }

        try
        {
            return MacPerfNative.CFGetTypeID(statistics) != MacPerfNative.CFDictionaryGetTypeID()
                ? (null, null)
                : (Number(statistics, _utilization), Number(statistics, _inUse));
        }
        finally
        {
            MacPerfNative.CFRelease(statistics);
        }
    }

    /// <summary>Metal's working set GMEM is a share of, in bytes; null without one.</summary>
    public long? WorkingSetBytes => _workingSet;

    private static long? Number(nint dictionary, nint key)
    {
        nint number = MacPerfNative.CFDictionaryGetValue(dictionary, key);   // borrowed: not released
        long value = 0;
        return number != 0 && MacPerfNative.CFGetTypeID(number) == MacPerfNative.CFNumberGetTypeID()
            && MacPerfNative.CFNumberGetValue(number, MacPerfNative.NumberSInt64, &value) != 0
            ? value
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (nint key in (ReadOnlySpan<nint>)[_statistics, _utilization, _inUse])
        {
            if (key != 0)
            {
                MacPerfNative.CFRelease(key);
            }
        }

        MacPerfNative.IOObjectRelease(_entry);
    }
}

/// <summary>
/// The network adapters on a Mac (2026-10-07): <see cref="NetworkCounters"/>' pick (up, not loopback or a tunnel, a gateway) and
/// link speeds — on Wi-Fi the radio's current rate (304 Mbit/s measured), so NET works there too — with each adapter's byte counts
/// read again at 64 bits from <c>net.link.generic.ifdata</c> (<see cref="PerfMath.IfMibBytes"/>). .NET's own counts are 32-bit on
/// macOS and would read a wrap as a second of nothing every two minutes of a large download. An adapter whose 64-bit counts
/// cannot be read is left out of that reading rather than mixed in at 32 bits. The VPN's <c>utun</c> adapters report as
/// <c>Unknown</c>, not <c>Tunnel</c>, with a gateway, so they pass the pick as a Windows VPN adapter does; they report no link
/// speed, so while one is the busiest NET has no share and NET↓/NET↑ still show its rate.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacNetworkCounters : INetworkCounters
{
    private readonly NetworkCounters _adapters = new();

    public IReadOnlyList<NetworkAdapterReading> Read()
    {
        var readings = new List<NetworkAdapterReading>();
        foreach (var adapter in _adapters.Read())
        {
            if (Bytes(adapter.Id) is { } bytes)
            {
                readings.Add(adapter with { Received = bytes.Received, Sent = bytes.Sent });
            }
        }

        return readings;
    }

    /// <summary>The adapter's 64-bit totals by its BSD name (<c>en0</c>: .NET's adapter id on a Mac); null when unread.</summary>
    public static (long Received, long Sent)? Bytes(string name)
    {
        uint index = MacPerfNative.if_nametoindex(name);
        if (index == 0)
        {
            return null;
        }

        int* mib = stackalloc int[] { MacPerfNative.CtlNet, MacPerfNative.PfLink, MacPerfNative.NetLinkGeneric, MacPerfNative.IfMibIfData, (int)index, MacPerfNative.IfDataGeneral };
        byte* data = stackalloc byte[MacPerfNative.IfMibDataSize];
        nuint length = MacPerfNative.IfMibDataSize;
        return MacPerfNative.sysctl(mib, 6, data, &length, null, 0) == 0
            ? PerfMath.IfMibBytes(new ReadOnlySpan<byte>(data, (int)length))
            : null;
    }
}
