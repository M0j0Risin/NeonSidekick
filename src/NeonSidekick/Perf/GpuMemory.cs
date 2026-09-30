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
}
