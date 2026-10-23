using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Monitors;

// The CCD (Connecting and Configuring Displays) structs of wingdi.h and dxva2's PHYSICAL_MONITOR, blittable, at namespace level
// outside the [SupportedOSPlatform] class so a portable test can measure them (MonitorNativeLayoutTests pins every size).

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayLuid
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayPoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayRational
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplaySize
{
    public uint Cx;
    public uint Cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayPathSourceInfo
{
    public DisplayLuid AdapterId;
    public uint Id;
    public uint ModeInfoIdx;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayPathTargetInfo
{
    public DisplayLuid AdapterId;
    public uint Id;
    public uint ModeInfoIdx;
    public uint OutputTechnology;
    public uint Rotation;
    public uint Scaling;
    public DisplayRational RefreshRate;
    public uint ScanLineOrdering;
    public int TargetAvailable;
    public uint StatusFlags;
}

/// <summary>DISPLAYCONFIG_PATH_INFO, 72 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayPathInfo
{
    public DisplayPathSourceInfo SourceInfo;
    public DisplayPathTargetInfo TargetInfo;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayVideoSignalInfo
{
    public ulong PixelRate;
    public DisplayRational HSyncFreq;
    public DisplayRational VSyncFreq;
    public DisplaySize ActiveSize;
    public DisplaySize TotalSize;
    public uint VideoStandard;
    public uint ScanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplaySourceMode
{
    public uint Width;
    public uint Height;
    public uint PixelFormat;
    public DisplayPoint Position;
}

/// <summary>DISPLAYCONFIG_MODE_INFO, 64 bytes: a header, then a union of the target, source and desktop-image modes.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct DisplayModeInfo
{
    [FieldOffset(0)] public uint InfoType;
    [FieldOffset(4)] public uint Id;
    [FieldOffset(8)] public DisplayLuid AdapterId;
    [FieldOffset(16)] public DisplayVideoSignalInfo TargetMode;
    [FieldOffset(16)] public DisplaySourceMode SourceMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayDeviceInfoHeader
{
    public uint Type;
    public uint Size;
    public DisplayLuid AdapterId;
    public uint Id;
}

/// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME, 84 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DisplaySourceDeviceName
{
    public DisplayDeviceInfoHeader Header;
    public fixed char ViewGdiDeviceName[32];
}

/// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME, 420 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DisplayTargetDeviceName
{
    public DisplayDeviceInfoHeader Header;
    public uint Flags;
    public uint OutputTechnology;
    public ushort EdidManufactureId;
    public ushort EdidProductCodeId;
    public uint ConnectorInstance;
    public fixed char MonitorFriendlyDeviceName[64];
    public fixed char MonitorDevicePath[128];
}

/// <summary>PHYSICAL_MONITOR (declared under <c>#pragma pack(1)</c>), 264 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct PhysicalMonitor
{
    public IntPtr Handle;
    public fixed char Description[128];
}

/// <summary>
/// The user32 CCD imports and dxva2's DDC/CI ones behind <see cref="WindowsMonitorSystem"/> (2026-10-23, the monitors plan; moved over from
/// the MonitorTrials <c>DisplaySwitcherTest</c>, b2efa06..c6ede4b, in <c>Screen/ScreenNative</c>'s style: source-generated
/// <see cref="LibraryImportAttribute"/> over blittable structs, every handle an <see cref="IntPtr"/>). <c>EnumDisplayMonitors</c> and
/// <c>GetMonitorInfo</c> are <c>ScreenNative</c>'s, not declared again. Both are system libraries: nothing joins
/// <c>SmokeChecks.RequiredNativeLibraries</c>; the smoke's <c>monitor:ccd</c> and <c>monitor:ddc</c> prove the imports on the published exe.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class MonitorNative
{
    public const int ErrorSuccess = 0;
    public const int ErrorInsufficientBuffer = 122;

    public const uint QdcAllPaths = 0x00000001;
    public const uint QdcOnlyActivePaths = 0x00000002;
    public const uint PathActive = 0x00000001;
    public const uint PathModeIdxInvalid = 0xFFFFFFFF;
    public const uint ModeInfoTypeSource = 1;
    public const uint DeviceInfoGetSourceName = 1;
    public const uint DeviceInfoGetTargetName = 2;

    public const uint SdcTopologyClone = 0x00000002;
    public const uint SdcTopologyExtend = 0x00000004;
    public const uint SdcTopologySupplied = 0x00000010;
    public const uint SdcUseSuppliedDisplayConfig = 0x00000020;
    public const uint SdcValidate = 0x00000040;
    public const uint SdcApply = 0x00000080;
    public const uint SdcSaveToDatabase = 0x00000200;
    public const uint SdcAllowChanges = 0x00000400;
    public const uint SdcPathPersistIfRequired = 0x00000800;
    public const uint SdcAllowPathOrderChanges = 0x00002000;

    [LibraryImport("user32.dll")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    public static partial int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, DisplayPathInfo* pathArray, ref uint numModeInfoArrayElements, DisplayModeInfo* modeInfoArray, IntPtr currentTopologyId);

    [LibraryImport("user32.dll")]
    public static partial int SetDisplayConfig(uint numPathArrayElements, DisplayPathInfo* pathArray, uint numModeInfoArrayElements, DisplayModeInfo* modeInfoArray, uint flags);

    [LibraryImport("user32.dll")]
    public static partial int DisplayConfigGetDeviceInfo(DisplayDeviceInfoHeader* requestPacket);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint numberOfPhysicalMonitors);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint physicalMonitorArraySize, PhysicalMonitor* physicalMonitorArray);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode, uint* codeType, out uint currentValue, out uint maximumValue);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint newValue);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint capabilitiesStringLengthInCharacters);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, byte* asciiCapabilitiesString, uint capabilitiesStringLengthInCharacters);
}
