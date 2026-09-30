namespace NeonSidekick.Perf;

/// <summary>
/// One reading of the machine (2026-09-29, the performance bar): each meter as a percentage from 0 to 100, or null when
/// this machine cannot tell it (no GPU the readers know, the first CPU sample before there is a delta). The bar leaves a
/// null meter out. The network's (2026-09-30) are rates: <paramref name="NetDown"/> and <paramref name="NetUp"/> in bits/s
/// and <paramref name="NetLink"/> the link's speed in bits/s, from which the bar takes its shares (<see cref="PerfMath.LinkPercent"/>).
/// </summary>
public readonly record struct PerfSnapshot(double? Cpu, double? Ram, double? Gpu, double? Vram, double? NetDown = null, double? NetUp = null, double? NetLink = null)
{
    /// <summary>Nothing read yet.</summary>
    public static readonly PerfSnapshot None;
}

/// <summary>
/// Which of the machine's readers a sample runs (later on 2026-09-30, the review's catch: the network's adapter walk ran every
/// second under a bar of CPU and RAM alone): those the checked meters need, so an unchecked meter costs nothing. GPU and VRAM
/// share one reader, as do the three network meters.
/// </summary>
[Flags]
public enum PerfReads
{
    None = 0,
    Cpu = 1,
    Ram = 2,
    Gpu = 4,
    Net = 8,
    All = Cpu | Ram | Gpu | Net,
}

/// <summary>Where the sampler reads the machine: <see cref="WindowsPerfSource"/> on Windows, <see cref="NullPerfSource"/> elsewhere, a fake in the tests.</summary>
public interface IPerfSource : IDisposable
{
    /// <summary>The meters <paramref name="reads"/> asks for now, the rest null. Called on the sampler's timer thread, never two at once.</summary>
    PerfSnapshot Sample(PerfReads reads);
}

/// <summary>The source where nothing can be read: every meter null, so the bar draws its row with nothing in it.</summary>
public sealed class NullPerfSource : IPerfSource
{
    public PerfSnapshot Sample(PerfReads reads) => PerfSnapshot.None;

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
