using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class StreamMeterTests
{
    [Fact]
    public void Reading_IsIdleOutsideARequest_AndTheChunksAndTheirRateWithin()
    {
        var clock = new ManualTimeProvider();
        var meter = new StreamMeter(clock);
        Assert.Equal(StreamMeter.Reading.Idle, meter.Read());

        // Sent, nothing back yet (the prefill): streaming, no rate.
        meter.Begin();
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(new StreamMeter.Reading(true, 0, null), meter.Read());

        // The first chunk starts the rate's span; the wait before it is not in it.
        meter.Chunk();
        Assert.Equal(new StreamMeter.Reading(true, 1, null), meter.Read());
        for (int i = 0; i < 39; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            meter.Chunk();
        }

        var reading = meter.Read();
        Assert.Equal(40, reading.Chunks);
        Assert.Equal(40 / 1.95, reading.ChunksPerSecond!.Value, 6);

        meter.End();
        Assert.Equal(StreamMeter.Reading.Idle, meter.Read());

        // The next request counts from nothing.
        meter.Begin();
        meter.Chunk();
        Assert.Equal(new StreamMeter.Reading(true, 1, null), meter.Read());
    }

    /// <summary>2026-10-26: a request's first chunks often come together, and their rate over a few milliseconds was absurd.</summary>
    [Fact]
    public void TheRate_WaitsForHalfASecond_AndIsNeverPastTheCeiling()
    {
        var clock = new ManualTimeProvider();
        var meter = new StreamMeter(clock);
        meter.Begin();
        for (int i = 0; i < 50; i++)
        {
            meter.Chunk();
        }

        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(new StreamMeter.Reading(true, 50, null), meter.Read());   // 5,000/s over 10 ms: not given yet

        clock.Advance(StreamMeter.MinRateSpan - TimeSpan.FromMilliseconds(10));
        Assert.Equal(100, meter.Read().ChunksPerSecond!.Value, 6);

        // A burst past the ceiling, even after the half second.
        for (int i = 0; i < 3_000; i++)
        {
            meter.Chunk();
        }

        Assert.Null(meter.Read().ChunksPerSecond);
        Assert.Equal(3_050, meter.Read().Chunks);
    }
}
