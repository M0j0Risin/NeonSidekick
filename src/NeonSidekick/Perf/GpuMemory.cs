namespace NeonSidekick.Perf;

/// <summary>
/// The dedicated memory of the adapter with the most (later on 2026-09-29, for the <c>Embedded VRAM budget</c>): the
/// performance bar's own DXGI pick (<see cref="PdhGpu.BiggestAdapter"/>), so the budget and the bar's VRAM meter mean the
/// same card. Null off Windows, with no hardware adapter, or when DXGI does not load. On a Mac (2026-10-07, the embedded LLM
/// with Metal) the GPU has no memory of its own: the share of unified memory Metal lets it use stands in
/// (<see cref="MetalNative"/>'s recommended working set, what llama.cpp's fit sees as the device's).
/// </summary>
public static class GpuMemory
{
    /// <summary>The biggest adapter's dedicated memory in bytes — on a Mac Metal's working set; null when none is read.</summary>
    public static long? DedicatedBytes()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MetalWorkingSetBytes();
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return PdhGpu.BiggestAdapter() is { } adapter ? (long)adapter.Total : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Metal's recommended working set in bytes (2026-10-07); null with no Metal device or when the frameworks do not load.</summary>
    public static long? MetalWorkingSetBytes()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            return MetalNative.Read() is { WorkingSetBytes: > 0 } device ? device.WorkingSetBytes : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The shared GPU memory process <paramref name="pid"/> holds on the biggest adapter, in bytes (2026-10-01, Embedded VRAM
    /// only: <see cref="PdhGpu.ProcessSharedUsage"/>). The biggest adapter is the budget's and the bar's card, and the one
    /// llama.cpp's Vulkan backend prefers (a dedicated GPU over an integrated one). Null off Windows, with no hardware adapter,
    /// or when PDH does not answer for it.
    /// </summary>
    public static long? ProcessSharedBytes(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return PdhGpu.BiggestAdapter() is { } adapter && PdhGpu.ProcessSharedUsage(pid, adapter.Luid) is { } bytes ? (long)bytes : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
