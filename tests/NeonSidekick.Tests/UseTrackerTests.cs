using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The embedded server's request count (2026-11-16, plan lucid-roaming-lantern #44): a plain request counted until it ends, a stream from its
/// first read until its enumerator is disposed (read to the end or left half-read), the updates the inner client's own objects, two clients
/// on one count held until both end, and a retired count calling nothing.
/// </summary>
public class UseTrackerTests
{
    private int _began;
    private int _ended;

    private UseCount Count() => new(() => _began++, () => _ended++);

    [Fact]
    public async Task APlainRequest_IsCountedUntilItEnds_EvenAFailedOne()
    {
        var count = Count();
        var inner = new FakeChatClient().EnqueueText("hi");
        using var client = new UseTracker(inner, count);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        Assert.Equal((1, 1, 0), (_began, _ended, count.InFlight));

        inner.EnqueueText("again");
        inner.Failure = new InvalidOperationException("down");
        inner.ThrowAt = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
        Assert.Equal((2, 2, 0), (_began, _ended, count.InFlight));
    }

    [Fact]
    public async Task AStream_CountsFromItsFirstRead_UntilDisposed_ItsUpdatesPassedByReference()
    {
        var count = Count();
        var marker = FakeChatClient.Progress();
        var inner = new FakeChatClient().Enqueue(marker, FakeChatClient.Text("one"), FakeChatClient.Text("two"));
        using var client = new UseTracker(inner, count);

        var stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]).GetAsyncEnumerator();
        Assert.Equal(0, count.InFlight);   // nothing until the first read
        Assert.True(await stream.MoveNextAsync());
        Assert.Same(marker, stream.Current);
        Assert.Equal((1, 0, 1), (_began, _ended, count.InFlight));

        await stream.DisposeAsync();       // left half-read
        Assert.Equal((1, 1, 0), (_began, _ended, count.InFlight));
    }

    [Fact]
    public async Task TwoClientsOnOneCount_HoldItUntilBothEnd()
    {
        var count = Count();
        using var a = new UseTracker(new FakeChatClient().EnqueueText("a", "b"), count);
        using var b = new UseTracker(new FakeChatClient().EnqueueText("c", "d"), count);
        var first = a.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "1")]).GetAsyncEnumerator();
        var second = b.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "2")]).GetAsyncEnumerator();
        await first.MoveNextAsync();
        await second.MoveNextAsync();
        Assert.Equal(2, count.InFlight);

        await first.DisposeAsync();
        Assert.Equal((0, 1), (_ended, count.InFlight));
        await second.DisposeAsync();
        Assert.Equal((1, 0), (_ended, count.InFlight));
    }

    [Fact]
    public async Task ARetiredCount_CallsNothing()
    {
        var count = Count();
        using var client = new UseTracker(new FakeChatClient().EnqueueText("a", "b"), count);
        var stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "1")]).GetAsyncEnumerator();
        await stream.MoveNextAsync();
        count.Retire();
        await stream.DisposeAsync();
        Assert.Equal((1, 0, 0), (_began, _ended, count.InFlight));
    }
}
