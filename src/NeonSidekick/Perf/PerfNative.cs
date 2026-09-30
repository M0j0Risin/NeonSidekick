using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Perf;

/// <summary>
/// The native calls behind the performance bar (2026-09-29), four system libraries:
/// kernel32 for the CPU and memory, NVIDIA's <c>nvml.dll</c> (the driver installs it) for an NVIDIA GPU, and for any other
/// GPU <c>dxgi.dll</c> (the adapters, their LUIDs and dedicated memory) and <c>pdh.dll</c> (Windows' GPU counters).
/// Source-generated <see cref="LibraryImportAttribute"/> stubs over blittable structs; the one COM interface (DXGI's) is
/// called through its vtable with unmanaged function pointers, nothing for AOT to marshal. Windows-only, like the other
/// layers the portability note lists; <see cref="WindowsPerfSource"/> is the guarded way in, and the published exe's smoke
/// proves the layouts (<c>perf:kernel32</c>, <c>perf:nvml</c>, <c>perf:pdh-dxgi</c>). System libraries, so none joins
/// <c>SmokeChecks.RequiredNativeLibraries</c> (the <c>PrintNative</c> precedent).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class PerfNative
{
    // ── kernel32 ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Ticks => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    // ── NVML ─────────────────────────────────────────────────────────────────

    internal const int NvmlSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [LibraryImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    internal static partial int NvmlInit();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    internal static partial int NvmlShutdown();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    internal static partial int NvmlDeviceGetCount(out uint count);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    internal static partial int NvmlDeviceGetHandleByIndex(uint index, out nint device);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    internal static partial int NvmlDeviceGetUtilizationRates(nint device, out NvmlUtilization utilization);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    internal static partial int NvmlDeviceGetMemoryInfo(nint device, out NvmlMemory memory);

    // ── DXGI ─────────────────────────────────────────────────────────────────

    /// <summary><c>IID_IDXGIFactory1</c>.</summary>
    internal static readonly Guid DxgiFactory1Iid = new("770aae78-f26f-4dba-a829-253c83d1b387");

    internal const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    internal const uint DxgiAdapterFlagSoftware = 2;

    // Vtable slots: IUnknown 0–2, IDXGIObject 3–6, IDXGIFactory 7–11, IDXGIFactory1 12 (EnumAdapters1);
    // IDXGIAdapter 7–9, IDXGIAdapter1 10 (GetDesc1).
    private const int ReleaseSlot = 2;
    private const int EnumAdapters1Slot = 12;
    private const int GetDesc1Slot = 10;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    [LibraryImport("dxgi.dll")]
    internal static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    /// <summary>The factory's adapter <paramref name="index"/> (an added reference) or <see cref="DxgiErrorNotFound"/> past the last.</summary>
    internal static int EnumAdapters1(nint factory, uint index, out nint adapter)
    {
        nint found;
        int hr = ((delegate* unmanaged<nint, uint, nint*, int>)Slot(factory, EnumAdapters1Slot))(factory, index, &found);
        adapter = hr >= 0 ? found : 0;
        return hr;
    }

    internal static int GetDesc1(nint adapter, out DxgiAdapterDesc1 desc)
    {
        DxgiAdapterDesc1 read;
        int hr = ((delegate* unmanaged<nint, DxgiAdapterDesc1*, int>)Slot(adapter, GetDesc1Slot))(adapter, &read);
        desc = read;
        return hr;
    }

    internal static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            _ = ((delegate* unmanaged<nint, uint>)Slot(unknown, ReleaseSlot))(unknown);
        }
    }

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];

    // ── PDH ──────────────────────────────────────────────────────────────────

    internal const uint PdhFmtDouble = 0x00000200;
    internal const uint PdhFmtNoCap100 = 0x00008000;
    internal const uint PdhMoreData = 0x800007D2;
    internal const uint PdhCStatusValidData = 0;
    internal const uint PdhCStatusNewData = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhFmtCounterValueItem
    {
        public char* Name;
        public PdhFmtCounterValue Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    internal static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    internal static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, void* items);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    internal static partial uint PdhCloseQuery(nint query);
}
