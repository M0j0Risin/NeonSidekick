namespace NeonSidekick.Perf;

/// <summary>
/// The dedicated memory of the adapter with the most (later on 2026-09-29, for the <c>Embedded VRAM budget</c>): the
/// performance bar's own DXGI pick (<see cref="PdhGpu.BiggestAdapter"/>), so the budget and the bar's VRAM meter mean the
/// same card. Null off Windows, with no hardware adapter, or when DXGI does not load.
/// </summary>
public static class GpuMemory
{
    /// <summary>The biggest adapter's dedicated memory in bytes; null when none is read.</summary>
    public static long? DedicatedBytes()
    {
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

    /// <summary>
    /// The shared GPU memory process <paramref name="pid"/> holds, in bytes (2026-10-01, Embedded VRAM only:
    /// <see cref="PdhGpu.ProcessSharedUsage"/>); null off Windows or when PDH does not answer for it.
    /// </summary>
    public static long? ProcessSharedBytes(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return PdhGpu.ProcessSharedUsage(pid) is { } bytes ? (long)bytes : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
