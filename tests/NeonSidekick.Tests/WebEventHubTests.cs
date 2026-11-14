using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The hub's pages (2026-11-13, the webui-voice plan): a targeted event reaches its page alone, a reconnect from the same run replaces the
/// older stream quietly, a duplicated tab gets a fresh id, and the voice side hears a page join and its last stream leave.
/// </summary>
public sealed class WebEventHubTests
{
    private const string Page = "page-0123456789abcdef";
    private const string Load = "load-0123456789abcdef";

    private static List<string> Drain(WebEventHub.Subscription sub)
    {
        var names = new List<string>();
        while (sub.Reader.TryRead(out var item))
        {
            names.Add(item.Name + ":" + item.Json.Value);
        }

        return names;
    }

    [Fact]
    public void ATargetedEvent_ReachesItsPageAlone_AndABroadcastReachesEveryone()
    {
        var hub = new WebEventHub();
        using var mine = hub.Subscribe(Page, Load);
        using var other = hub.Subscribe("other-0123456789abcdef", Load);
        using var bare = hub.Subscribe();

        hub.Publish(new WebEvent("voice", () => "1") { Page = Page });
        hub.Publish(new WebEvent("state", () => "2"));

        Assert.Equal(["voice:1", "state:2"], Drain(mine));
        Assert.Equal(["state:2"], Drain(other));
        Assert.Equal(["state:2"], Drain(bare));
    }

    [Fact]
    public void AReconnectFromTheSameRun_ReplacesTheOlderStream_WithoutALeave()
    {
        var hub = new WebEventHub();
        var joined = new List<(string, string)>();
        var left = new List<string>();
        hub.PageJoined = (page, load) => joined.Add((page, load));
        hub.PageLeft = left.Add;

        using var first = hub.Subscribe(Page, Load);
        using var second = hub.Subscribe(Page, Load);

        Assert.Equal(Page, second.Page);
        Assert.True(first.Reader.Completion.IsCompleted);
        Assert.Equal(1, hub.Count);
        first.Dispose();   // the replaced stream's own end raises nothing
        Assert.Empty(left);
        Assert.Equal([(Page, Load), (Page, Load)], joined);

        second.Dispose();
        Assert.Equal([Page], left);
        Assert.False(hub.HasPage(Page));
    }

    [Fact]
    public void ADuplicatedTab_GetsAFreshId_AndTheFirstPageKeepsItsOwn()
    {
        var hub = new WebEventHub();
        using var first = hub.Subscribe(Page, Load);
        using var copy = hub.Subscribe(Page, "load-fedcba9876543210");

        Assert.Equal(Page, first.Page);
        Assert.NotEqual(Page, copy.Page);
        Assert.True(WebVideoPlayer.IsId(copy.Page));
        Assert.True(hub.HasPage(copy.Page, "load-fedcba9876543210"));
        Assert.True(hub.HasPage(Page, Load));
        Assert.False(hub.HasPage(Page, "load-fedcba9876543210"));
    }

    [Fact]
    public void AnIdOfTheWrongShape_IsABareSubscriber()
    {
        var hub = new WebEventHub();
        var joined = 0;
        hub.PageJoined = (_, _) => joined++;

        using var sub = hub.Subscribe("short", Load);

        Assert.Null(sub.Page);
        Assert.Equal(0, joined);
        hub.Publish(new WebEvent("voice", () => "x") { Page = "short" });
        Assert.Empty(Drain(sub));
    }

    [Fact]
    public void AHandlerThatThrows_NeverTakesTheSubscribeDown()
    {
        var hub = new WebEventHub { PageJoined = (_, _) => throw new InvalidOperationException("boom") };

        using var sub = hub.Subscribe(Page, Load);

        Assert.True(hub.HasPage(Page));
    }
}
