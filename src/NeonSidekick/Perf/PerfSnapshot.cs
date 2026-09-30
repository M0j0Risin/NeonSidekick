namespace NeonSidekick.Perf;

/// <summary>
/// One reading of the machine (2026-09-29, the performance bar): each meter as a percentage from 0 to 100, or null when
/// this machine cannot tell it (no GPU the readers know, the first CPU sample before there is a delta). The bar leaves a
/// null meter out.
/// </summary>
public readonly record struct PerfSnapshot(double? Cpu, double? Ram, double? Gpu, double? Vram)
{
    /// <summary>Nothing read yet.</summary>
    public static readonly PerfSnapshot None;
}

/// <summary>Where the sampler reads the machine: <see cref="WindowsPerfSource"/> on Windows, <see cref="NullPerfSource"/> elsewhere, a fake in the tests.</summary>
public interface IPerfSource : IDisposable
{
    /// <summary>The meters now. Called on the sampler's timer thread, never two at once.</summary>
    PerfSnapshot Sample();
}

/// <summary>The source where nothing can be read: every meter null, so the bar draws its row with nothing in it.</summary>
public sealed class NullPerfSource : IPerfSource
{
    public PerfSnapshot Sample() => PerfSnapshot.None;

    public void Dispose()
    {
    }
}

/// <summary>The machine's own source: <see cref="WindowsPerfSource"/> on Windows, else <see cref="NullPerfSource"/>.</summary>
public static class PerfSources
{
    /// <summary>A new source for this machine; the sampler owns and disposes it.</summary>
    public static IPerfSource CreateDefault() => OperatingSystem.IsWindows() ? new WindowsPerfSource() : new NullPerfSource();
}
