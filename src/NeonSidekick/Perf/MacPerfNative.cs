using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Perf;

/// <summary>
/// The native calls behind the performance bar on a Mac (2026-10-07, the user's ask): libSystem's Mach host statistics and
/// <c>sysctl</c>, IOKit's registry and CoreFoundation's dictionaries. Source-generated <see cref="LibraryImportAttribute"/>
/// stubs over pointers and integers only — the structs (<c>host_cpu_load_info</c>, <c>vm_statistics64</c>,
/// <c>ifmibdata</c>) are read as plain buffers at the offsets <c>clang</c>'s <c>offsetof</c> gave on macOS 15.7 (arm64), so
/// nothing is marshalled by layout. System libraries, so none joins <c>SmokeChecks.RequiredNativeLibraries</c>; the
/// published exe's smoke proves them (<c>perf:mach</c>, <c>perf:iokit</c>, <c>perf:network</c>). Never a shell-out to
/// <c>top</c>, <c>vm_stat</c> or <c>ioreg</c> (the user's rule): those print what these calls return.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacPerfNative
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const int KernSuccess = 0;

    // ── Mach host statistics ─────────────────────────────────────────────────

    /// <summary><c>HOST_CPU_LOAD_INFO</c>: four <c>natural_t</c> tick counters, user, system, idle, nice (<c>CPU_STATE_*</c>).</summary>
    public const int HostCpuLoadInfo = 3;

    public const int HostCpuLoadInfoCount = 4;

    /// <summary><c>HOST_VM_INFO64</c>: <c>vm_statistics64</c>, 160 bytes, 40 <c>integer_t</c>s (the kernel fills 38 on macOS 15).</summary>
    public const int HostVmInfo64 = 4;

    public const int HostVmInfo64Count = 40;

    // vm_statistics64's natural_t fields the bar reads, as byte offsets (offsetof on macOS 15.7 arm64).
    public const int VmWireCount = 12;
    public const int VmPurgeableCount = 88;
    public const int VmCompressorPageCount = 128;
    public const int VmInternalPageCount = 140;

    [LibraryImport(LibSystem)]
    public static partial uint mach_host_self();

    [LibraryImport(LibSystem)]
    public static partial int host_statistics64(uint host, int flavor, int* info, uint* count);

    [LibraryImport(LibSystem)]
    public static partial int mach_port_deallocate(uint task, uint name);

    /// <summary>
    /// <c>mach_task_self()</c>, a macro over the global <c>mach_task_self_</c>: read through the export, since a variable
    /// cannot be imported as a function.
    /// </summary>
    public static uint MachTaskSelf()
    {
        nint library = NativeLibrary.Load(LibSystem);
        return *(uint*)NativeLibrary.GetExport(library, "mach_task_self_");
    }

    // ── sysctl ───────────────────────────────────────────────────────────────

    // net.link.generic.ifdata.<index>.general: an ifmibdata (180 bytes, packed) whose if_data64 carries the 64-bit byte counts.
    public const int CtlNet = 4;
    public const int PfLink = 18;
    public const int NetLinkGeneric = 0;
    public const int IfMibIfData = 2;
    public const int IfDataGeneral = 1;
    public const int IfMibDataSize = 180;

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sysctlbyname(string name, void* oldValue, nuint* oldLength, void* newValue, nuint newLength);

    [LibraryImport(LibSystem)]
    public static partial int sysctl(int* name, uint length, void* oldValue, nuint* oldLength, void* newValue, nuint newLength);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint if_nametoindex(string name);

    /// <summary>A whole-number sysctl by name (<c>hw.memsize</c>, <c>hw.pagesize</c>), 4 or 8 bytes as the kernel gives it; null when unread.</summary>
    public static long? SysctlInteger(string name)
    {
        long value = 0;
        nuint length = sizeof(long);
        if (sysctlbyname(name, &value, &length, null, 0) != 0)
        {
            return null;
        }

        return length switch
        {
            sizeof(long) => value,
            sizeof(int) => *(int*)&value,
            _ => null,
        };
    }

    // ── IOKit and CoreFoundation ─────────────────────────────────────────────

    /// <summary><c>kCFStringEncodingUTF8</c>.</summary>
    public const uint Utf8 = 0x08000100;

    /// <summary><c>kCFNumberSInt64Type</c>.</summary>
    public const nint NumberSInt64 = 4;

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint IOServiceMatching(string name);

    /// <summary>The first service matching; consumes <paramref name="matching"/>'s reference. 0 for none.</summary>
    [LibraryImport(IOKit)]
    public static partial uint IOServiceGetMatchingService(uint mainPort, nint matching);

    [LibraryImport(IOKit)]
    public static partial nint IORegistryEntryCreateCFProperty(uint entry, nint key, nint allocator, uint options);

    [LibraryImport(IOKit)]
    public static partial int IOObjectRelease(uint entry);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFDictionaryGetValue(nint dictionary, nint key);

    [LibraryImport(CoreFoundation)]
    public static partial byte CFNumberGetValue(nint number, nint type, void* value);

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFGetTypeID(nint value);

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFDictionaryGetTypeID();

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(nint value);
}
