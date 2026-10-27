using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Monitors;

// NVIDIA's driver API structs (2026-10-27, the NVIDIA plan), blittable, at namespace level outside the [SupportedOSPlatform] class so a
// portable test can measure them (MonitorNativeLayoutTests pins each size). Sizes and versions from NVIDIA's MIT nvapi.h, proven by the spike.

/// <summary><c>NV_I2C_INFO_V3</c>: 64 bytes on x64, version 3.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NvI2cInfo
{
    public uint Version;
    public uint DisplayMask;
    public byte IsDdcPort;
    public byte DevAddress;
    public byte* RegAddress;
    public uint RegAddrSize;
    public byte* Data;
    public uint Size;
    public uint Speed;
    public int SpeedKhz;
    public byte PortId;
    public uint IsPortIdSet;
}

/// <summary><c>NV_GPU_DISPLAYIDS</c>: 16 bytes, version 3 (its <c>VER2</c> macro); the flag word's bits in <see cref="NvapiNative"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvDisplayIds
{
    public uint Version;
    public int ConnectorType;
    public uint DisplayId;
    public uint Flags;
}

/// <summary><c>NV_EDID_V3</c>: 272 bytes (256 of data), version 3.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NvEdid
{
    public uint Version;
    public fixed byte Data[256];
    public uint Size;
    public uint EdidId;
    public uint Offset;
}

/// <summary><c>NV_LOGICAL_GPU_DATA_V1</c>: 568 bytes on x64, version 1; <see cref="OsAdapterId"/> points at an 8-byte LUID the call fills.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NvLogicalGpuData
{
    public uint Version;
    public void* OsAdapterId;
    public uint PhysicalGpuCount;
    public fixed long PhysicalGpuHandles[64];
    public fixed uint Reserved[8];
}

/// <summary>
/// NVIDIA's driver API (2026-10-27, the NVIDIA plan): <c>nvapi64.dll</c>, which the driver installs in System32, loaded from there only
/// (<see cref="NativeLibrary.TryLoad(string, System.Reflection.Assembly, DllImportSearchPath?, out nint)"/>, so a stray copy beside the exe or
/// in the working folder is never loaded), its one export <c>nvapi_QueryInterface</c> asked for the nine functions by id, each called through a
/// <c>delegate* unmanaged[Cdecl]</c> cast (no marshalling stubs, which NativeAOT doesn't make for a delegate type at run time). The load and
/// <c>NvAPI_Initialize</c> run once, on the first call (never at startup), and are the process's; a machine without the library (AMD, Intel, a
/// runner), or a driver that answers null for one of the ids, is not reachable, remembered. <c>NvAPI_Unload</c> is never called: the process
/// ends through <c>ProcessEnd</c>. Excluded from coverage: the smoke's <c>monitor:nvapi</c> and the live fact prove it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class NvapiNative
{
    public const int Ok = 0;

    /// <summary><c>NVAPI_LIBRARY_NOT_FOUND</c>, <c>NVAPI_NO_IMPLEMENTATION</c>, <c>NVAPI_NVIDIA_DEVICE_NOT_FOUND</c>: no NVIDIA here.</summary>
    public const int LibraryNotFound = -2;

    public const int NoImplementation = -3;

    public const int DeviceNotFound = -6;

    /// <summary><c>NVAPI_INCOMPATIBLE_STRUCT_VERSION</c>: a struct's layout the driver doesn't take (the smoke's failure).</summary>
    public const int IncompatibleStructVersion = -9;

    /// <summary><c>NVAPI_INSUFFICIENT_BUFFER</c>: a display came between the count and the fill.</summary>
    public const int InsufficientBuffer = -174;

    /// <summary><c>NV_GPU_CONNECTED_IDS_FLAG_UNCACHED</c>: the connected displays asked afresh.</summary>
    public const uint ConnectedUncached = 1;

    /// <summary><c>NV_GPU_DISPLAYIDS</c>' flag bits: on the desktop, connected, physically connected.</summary>
    public const uint FlagActive = 1u << 2;

    public const uint FlagConnected = 1u << 6;

    public const uint FlagPhysical = 1u << 17;

    /// <summary>Most physical GPUs a call lists (<c>NVAPI_MAX_PHYSICAL_GPUS</c>).</summary>
    public const int MaxGpus = 64;

    public static readonly uint I2cVersion = (uint)sizeof(NvI2cInfo) | (3u << 16);
    public static readonly uint DisplayIdsVersion = (uint)sizeof(NvDisplayIds) | (3u << 16);
    public static readonly uint EdidVersion = (uint)sizeof(NvEdid) | (3u << 16);
    public static readonly uint LogicalGpuVersion = (uint)sizeof(NvLogicalGpuData) | (1u << 16);

    private const uint InitializeId = 0x0150e828;
    private const uint EnumPhysicalGpusId = 0xe5ac921f;
    private const uint GetConnectedDisplayIdsId = 0x0078dba2;
    private const uint GetGpuAndOutputIdFromDisplayIdId = 0x112ba1a5;
    private const uint GetEdidId = 0x37d32e69;
    private const uint GetLogicalGpuFromPhysicalGpuId = 0xadd604d1;
    private const uint GetLogicalGpuInfoId = 0x842b066e;
    private const uint I2cReadId = 0x2fde12c5;
    private const uint I2cWriteId = 0xe812eb07;

    private static readonly object Gate = new();
    private static bool _tried;
    private static int _initialized = DeviceNotFound;
    private static nint _enumGpus, _connected, _gpuAndOutput, _edid, _logical, _logicalInfo, _i2cRead, _i2cWrite;

    /// <summary>
    /// The library loaded, its functions found and <c>NvAPI_Initialize</c> run, once: its status (<see cref="Ok"/>), or
    /// <see cref="LibraryNotFound"/> when the library or a function isn't there. The first answer is kept for the process.
    /// </summary>
    public static int Initialize()
    {
        lock (Gate)
        {
            if (_tried)
            {
                return _initialized;
            }

            _tried = true;
            if (!NativeLibrary.TryLoad("nvapi64.dll", typeof(NvapiNative).Assembly, DllImportSearchPath.System32, out nint library)
                || !NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out nint queryInterface))
            {
                DiagnosticLog.Debug("Monitors", "NVIDIA's driver API isn't on this PC (no nvapi64.dll in System32)");
                _initialized = LibraryNotFound;
                return _initialized;
            }

            var query = (delegate* unmanaged[Cdecl]<uint, nint>)queryInterface;
            nint initialize = query(InitializeId);
            (_enumGpus, _connected, _gpuAndOutput, _edid) = (query(EnumPhysicalGpusId), query(GetConnectedDisplayIdsId), query(GetGpuAndOutputIdFromDisplayIdId), query(GetEdidId));
            (_logical, _logicalInfo, _i2cRead, _i2cWrite) = (query(GetLogicalGpuFromPhysicalGpuId), query(GetLogicalGpuInfoId), query(I2cReadId), query(I2cWriteId));
            if (initialize == 0 || _enumGpus == 0 || _connected == 0 || _gpuAndOutput == 0 || _edid == 0 || _logical == 0 || _logicalInfo == 0 || _i2cRead == 0 || _i2cWrite == 0)
            {
                DiagnosticLog.Debug("Monitors", "NVIDIA's driver API lacks a function the app needs (an older driver)");
                _initialized = LibraryNotFound;
                return _initialized;
            }

            _initialized = ((delegate* unmanaged[Cdecl]<int>)initialize)();
            DiagnosticLog.Debug("Monitors", "NVIDIA's driver API loaded; initialize " + _initialized.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return _initialized;
        }
    }

    /// <summary><c>NvAPI_EnumPhysicalGPUs</c>: up to <see cref="MaxGpus"/> handles.</summary>
    public static int EnumPhysicalGpus(nint* handles, uint* count) => ((delegate* unmanaged[Cdecl]<nint*, uint*, int>)_enumGpus)(handles, count);

    /// <summary><c>NvAPI_GPU_GetConnectedDisplayIds</c>: <paramref name="ids"/> null to count.</summary>
    public static int GetConnectedDisplayIds(nint gpu, NvDisplayIds* ids, uint* count, uint flags) =>
        ((delegate* unmanaged[Cdecl]<nint, NvDisplayIds*, uint*, uint, int>)_connected)(gpu, ids, count, flags);

    /// <summary><c>NvAPI_SYS_GetGpuAndOutputIdFromDisplayId</c>.</summary>
    public static int GetGpuAndOutputIdFromDisplayId(uint displayId, nint* gpu, uint* output) =>
        ((delegate* unmanaged[Cdecl]<uint, nint*, uint*, int>)_gpuAndOutput)(displayId, gpu, output);

    /// <summary><c>NvAPI_GPU_GetEDID</c>.</summary>
    public static int GetEdid(nint gpu, uint output, NvEdid* edid) => ((delegate* unmanaged[Cdecl]<nint, uint, NvEdid*, int>)_edid)(gpu, output, edid);

    /// <summary><c>NvAPI_GetLogicalGPUFromPhysicalGPU</c>.</summary>
    public static int GetLogicalGpuFromPhysicalGpu(nint gpu, nint* logical) => ((delegate* unmanaged[Cdecl]<nint, nint*, int>)_logical)(gpu, logical);

    /// <summary><c>NvAPI_GPU_GetLogicalGpuInfo</c>.</summary>
    public static int GetLogicalGpuInfo(nint logical, NvLogicalGpuData* data) => ((delegate* unmanaged[Cdecl]<nint, NvLogicalGpuData*, int>)_logicalInfo)(logical, data);

    /// <summary><c>NvAPI_I2CRead</c>.</summary>
    public static int I2cRead(nint gpu, NvI2cInfo* info) => ((delegate* unmanaged[Cdecl]<nint, NvI2cInfo*, int>)_i2cRead)(gpu, info);

    /// <summary><c>NvAPI_I2CWrite</c>.</summary>
    public static int I2cWrite(nint gpu, NvI2cInfo* info) => ((delegate* unmanaged[Cdecl]<nint, NvI2cInfo*, int>)_i2cWrite)(gpu, info);
}
