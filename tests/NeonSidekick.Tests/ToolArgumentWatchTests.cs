using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The stream's tool calls as <see cref="ToolArgumentWatch"/> reads them (2026-10-29, session 832's runaway call).</summary>
public class ToolArgumentWatchTests
{
    private readonly ManualTimeProvider _time = new();

    private ToolArgumentWatch Watch(Dictionary<string, int>? own = null) =>
        new(name => own is not null && own.TryGetValue(name, out int c) ? c : null, _time);

    private static string Chars(int count) => new('x', count);

    private static TurnEvent.ToolWriting Writing(string name, long chars) => new(name, chars);

    [Fact]
    public void TheFirstCallShowsAtItsName_ThenItsSize_AtMostOnceAnInterval()
    {
        var watch = Watch();
        Assert.Equal(Writing("present_audit", 1), watch.Observe(FakeChatClient.ArgumentDelta(0, "c1", "present_audit", "{")));

        // Within the interval the size waits, however far it grows; past it, the next piece shows it.
        Assert.Null(watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(500))));
        Assert.Null(watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(900))));
        _time.Advance(ToolArgumentWatch.ProgressInterval);
        Assert.Equal(Writing("present_audit", 1_411), watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(10))));
        Assert.Equal(Writing("present_audit", 1_411), watch.Latest);
        Assert.Equal(1_411, watch.Chars);
    }

    /// <summary>Thirty calls named in a burst relabel the busy row once, then once more for the newest after the interval.</summary>
    [Fact]
    public void ABurstOfCalls_ShowsTheFirst_ThenTheNewestAfterTheInterval()
    {
        var watch = Watch();
        var shown = new List<TurnEvent.ToolWriting>();
        for (int i = 0; i < 30; i++)
        {
            if (watch.Observe(FakeChatClient.ArgumentDelta(i, "c" + i, "read_file", "{\"path\":\"a\"}")) is { } writing)
            {
                shown.Add(writing);
            }
        }

        _time.Advance(ToolArgumentWatch.ProgressInterval);
        if (watch.Observe(FakeChatClient.ArgumentDelta(30, "c30", "write_file", "{")) is { } last)
        {
            shown.Add(last);
        }

        Assert.Equal([Writing("read_file", 12), Writing("write_file", 1)], shown);
        Assert.Equal(30 * 12 + 1, watch.Chars);
    }

    /// <summary>The log's tail is the call written last, with its own characters, not the reply's total.</summary>
    [Fact]
    public void Latest_IsTheCallWrittenLast_WithItsOwnCount()
    {
        var watch = Watch();
        watch.Observe(FakeChatClient.ArgumentDelta(0, "c1", "read_file", "{\"path\":\"a\"}"));
        watch.Observe(FakeChatClient.ArgumentDelta(1, "c2", "write_file", Chars(900)));
        watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(100)));

        Assert.Equal(Writing("read_file", 112), watch.Latest);
        Assert.Equal(1_012, watch.Chars);
    }

    /// <summary>A small chunk before the one naming the call shows nothing (no "(unnamed)" flash); the name shows as it arrives.</summary>
    [Fact]
    public void AnUnnamedCall_ShowsNothing_UntilNamedOrPastTheCount()
    {
        var watch = Watch();
        Assert.Null(watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(10))));
        Assert.Equal(Writing("present_audit", 20), watch.Observe(FakeChatClient.ArgumentDelta(0, "c1", "present_audit", Chars(10))));

        var other = Watch();
        Assert.Equal(Writing(TurnText.UnnamedCall, ToolArgumentWatch.CountShownFrom), other.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(ToolArgumentWatch.CountShownFrom))));
    }

    /// <summary>
    /// A chunk with two pieces of one call shows its size after both (2026-10-30, review F70: the shown event is built once, after the
    /// update); the same size again shows nothing, however long after.
    /// </summary>
    [Fact]
    public void AChunkWithTwoPiecesOfOneCall_ShowsItsSizeAfterBoth_AndAnUnchangedSizeNothing()
    {
        var watch = Watch();
        Assert.Equal(Writing("present_audit", 11), watch.Observe(FakeChatClient.ArgumentDeltas((0, "c1", "present_audit", "{"), (0, null, null, Chars(10)))));
        _time.Advance(ToolArgumentWatch.ProgressInterval);
        Assert.Null(watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, "")));
        Assert.Equal(Writing("present_audit", 11), watch.Latest);
    }

    /// <summary>One chunk naming several calls (a server that sends them whole) shows the last.</summary>
    [Fact]
    public void AChunkNamingSeveralCalls_ShowsTheLast()
    {
        var watch = Watch();
        var both = FakeChatClient.ArgumentDeltas(
            (0, "c1", "present_audit", "{}"),
            (1, "c2", "read_file", "{\"path\":\"a\"}"));

        Assert.Equal(Writing("read_file", 12), watch.Observe(both));
    }

    /// <summary>SGLang's shape: the name at once, the whole parameter in one piece at the end.</summary>
    [Fact]
    public void ANameOnlyStream_ShowsTheName_AndTheSizeWhenThePieceArrives()
    {
        var watch = Watch();
        Assert.Equal(Writing("present_audit", 0), watch.Observe(FakeChatClient.ArgumentDelta(0, "c1", "present_audit", null)));
        _time.Advance(ToolArgumentWatch.ProgressInterval);
        Assert.Equal(Writing("present_audit", 1_115), watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(1_115))));
    }

    [Fact]
    public void ACallStreamingPastItsToolsCeiling_IsOver_Once()
    {
        var watch = Watch(own: new() { ["present_audit"] = 2_000 });
        watch.Observe(FakeChatClient.ArgumentDelta(0, "c1", "present_audit", Chars(900)));
        watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(900)));
        Assert.Null(watch.Over);

        watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(201)));
        Assert.Equal(new ToolArgumentWatch.Runaway("present_audit", 2_001, 2_000), watch.Over);

        watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(500)));
        Assert.Equal(2_001, watch.Over!.Chars);
    }

    /// <summary>Only a tool that declares a ceiling is held to one: nothing a tool would take is stopped, and an unnamed call has none.</summary>
    [Fact]
    public void ACallWithNoCeiling_IsNeverOver()
    {
        var watch = Watch(own: new() { ["present_audit"] = 2_000 });
        for (int i = 0; i < 10; i++)
        {
            watch.Observe(FakeChatClient.ArgumentDelta(0, i == 0 ? "c1" : null, i == 0 ? "write_file" : null, Chars(900)));
            watch.Observe(FakeChatClient.ArgumentDelta(1, null, null, Chars(900)));
        }

        Assert.Null(watch.Over);
    }

    /// <summary>
    /// SGLang: every parameter held and sent whole, then a small closing piece past the ceiling. Written already, so nothing is stopped
    /// (the review of e7f741e8's C36).
    /// </summary>
    [Fact]
    public void HeldPiecesPastTheCeiling_ThenASmallClose_AreLetThrough()
    {
        var watch = Watch(own: new() { ["present_audit"] = 2_000 });
        watch.Observe(FakeChatClient.ArgumentDelta(0, "c1", "present_audit", "{\"findings\":"));
        watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(ToolArgumentWatch.WholePiece + 1_500)));
        watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, ",\"summary\":\"" + Chars(600) + "\"}"));

        Assert.Null(watch.Over);
    }

    /// <summary>
    /// vLLM: an array held and sent whole past the ceiling, then a text parameter streaming a runaway. The held piece doesn't switch the
    /// guard off: the streamed text is stopped once it alone passes the ceiling (the review of e7f741e8's C35).
    /// </summary>
    [Fact]
    public void AHeldPiece_ThenAStreamedRunaway_IsStoppedByWhatStreamed()
    {
        var watch = Watch(own: new() { ["present_audit"] = 2_000 });
        watch.Observe(FakeChatClient.ArgumentDelta(0, "c1", "present_audit", "{\"findings\":"));
        watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(5_000)));
        for (int i = 0; i < 3; i++)
        {
            watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(600)));
        }

        Assert.Null(watch.Over);

        watch.Observe(FakeChatClient.ArgumentDelta(0, null, null, Chars(600)));
        Assert.Equal(new ToolArgumentWatch.Runaway("present_audit", 12 + 2_400, 2_000), watch.Over);
    }

    [Fact]
    public void UpdatesWithoutTheSdksChunk_ChangeNothing()
    {
        var watch = Watch();
        Assert.Null(watch.Observe(FakeChatClient.Text("hello")));
        Assert.Null(watch.Observe(FakeChatClient.Progress()));
        Assert.Null(watch.Observe(FakeChatClient.Call("c1", "echo")));

        Assert.Null(watch.Latest);
        Assert.Null(watch.Over);
        Assert.Equal(0, watch.Chars);
    }

    [Fact]
    public void ArgumentsAreCountedInCharacters_NotBytes()
    {
        var watch = Watch();
        watch.Observe(FakeChatClient.ArgumentDelta(0, "c1", "echo", "\"é🐳\""));
        Assert.Equal(5, watch.Latest!.Chars);
    }
}
