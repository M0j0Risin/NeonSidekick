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
}
