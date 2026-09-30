using NeonSidekick.Perf;

namespace NeonSidekick.Tests.Fakes;

/// <summary>A performance-bar source that answers what the test set, counting its samples and whether it was disposed.</summary>
public sealed class FakePerfSource : IPerfSource
{
    public PerfSnapshot Next { get; set; }

    public int Samples { get; private set; }

    /// <summary>What the last sample was asked to read.</summary>
    public PerfReads LastReads { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>When set, the next sample throws it.</summary>
    public Exception? Throw { get; set; }

    public PerfSnapshot Sample(PerfReads reads)
    {
        Samples++;
        LastReads = reads;
        if (Throw is { } ex)
        {
            Throw = null;
            throw ex;
        }

        return Next;
    }

    public void Dispose() => Disposed = true;
}
