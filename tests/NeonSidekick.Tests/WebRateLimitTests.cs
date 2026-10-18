using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's rate limits (2026-10-15): a token bucket per address and kind on a test clock — the burst, the refill, the retry, the paths each
/// counts, the idle sweep and the cap, and one log line a minute.
/// </summary>
public sealed class WebRateLimitTests
{
    private readonly ManualTimeProvider _time = new();
    private static readonly IPAddress Phone = IPAddress.Parse("192.168.1.50");
    private static readonly IPAddress Other = IPAddress.Parse("192.168.1.51");

    [Fact]
    public void Taps_HaveABurstOfFifteen_ThenRefillFiveASecond()
    {
        var limit = new WebRateLimit(_time);
        for (int i = 0; i < 15; i++)
        {
            Assert.True(limit.TryTake(Phone, WebRateBucket.Act, out _));
        }

        Assert.False(limit.TryTake(Phone, WebRateBucket.Act, out var retry));
        Assert.Equal(TimeSpan.FromSeconds(0.2), retry);
        Assert.True(limit.TryTake(Other, WebRateBucket.Act, out _));   // another address has its own

        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(limit.TryTake(Phone, WebRateBucket.Act, out _));
        Assert.False(limit.TryTake(Phone, WebRateBucket.Act, out _));
    }

    [Fact]
    public void Questions_AreOneEveryTwoSeconds_AndSignInsFiveAMinute()
    {
        var limit = new WebRateLimit(_time);
        Assert.True(limit.TryTake(Phone, WebRateBucket.Say, out _));
        Assert.False(limit.TryTake(Phone, WebRateBucket.Say, out var retry));
        Assert.Equal(TimeSpan.FromSeconds(2), retry);
        Assert.True(limit.TryTake(Phone, WebRateBucket.Act, out _));   // a kind of its own
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(limit.TryTake(Phone, WebRateBucket.Say, out _));

        for (int i = 0; i < 5; i++)
        {
            Assert.True(limit.TryTake(Phone, WebRateBucket.SignIn, out _));
        }

        Assert.False(limit.TryTake(Phone, WebRateBucket.SignIn, out var signIn));
        Assert.Equal(TimeSpan.FromSeconds(12), signIn);
    }

    [Theory]
    [InlineData("/api/shopping/check", WebRateBucket.Act)]
    [InlineData("/api/shopping/store", WebRateBucket.Act)]
    [InlineData("/api/shopping/add", WebRateBucket.Act)]   // the list's own controls (2026-10-16)
    [InlineData("/api/shopping/readd", WebRateBucket.Act)]
    [InlineData("/api/shopping/forget", WebRateBucket.Act)]
    [InlineData("/api/shopping/quantity", WebRateBucket.Act)]
    [InlineData("/api/shopping/rename", WebRateBucket.Act)]
    [InlineData("/api/shopping/remove", WebRateBucket.Act)]
    [InlineData("/api/shopping/checkall", WebRateBucket.Act)]
    [InlineData("/api/shopping/clear", WebRateBucket.Act)]
    [InlineData("/api/home/tap", WebRateBucket.Act)]
    [InlineData("/api/home/act", WebRateBucket.Act)]
    [InlineData("/api/home/scene", WebRateBucket.Act)]
    [InlineData("/api/home/video", WebRateBucket.Act)]
    [InlineData("/api/prompts/save", WebRateBucket.Act)]   // the sheet's (2026-10-16)
    [InlineData("/api/prompts/delete", WebRateBucket.Act)]
    [InlineData("/api/memory/save", WebRateBucket.Act)]
    [InlineData("/api/memory/delete", WebRateBucket.Act)]
    [InlineData("/api/memory/edit", WebRateBucket.Act)]
    [InlineData("/api/memory/toggle", WebRateBucket.Act)]
    [InlineData("/api/history/clear", WebRateBucket.Act)]
    [InlineData("/api/learn/cancel", WebRateBucket.Act)]   // the status row's 🧠 (2026-10-17)
    [InlineData("/api/queue/resume", WebRateBucket.Act)]   // the held queue's Resume (2026-10-17)
    [InlineData("/api/home/say", WebRateBucket.Say)]
    [InlineData("/signin", WebRateBucket.SignIn)]
    public void TheLimitedPosts_CountAgainstTheirKind(string path, WebRateBucket bucket)
    {
        Assert.Equal(bucket, WebRateLimit.BucketOf(path, post: true));
    }

    [Theory]
    [InlineData("/api/send", true)]
    [InlineData("/api/answer", true)]
    [InlineData("/signin", false)]
    [InlineData("/api/home", false)]
    [InlineData("/api/history", false)]
    [InlineData("/api/memory", false)]
    [InlineData("/api/shopping", false)]
    [InlineData("/api/shopping/bought", false)]
    public void EverythingElse_IsNotLimited(string path, bool post)
    {
        Assert.Null(WebRateLimit.BucketOf(path, post));
    }

    [Fact]
    public void AnIdleAddress_IsDropped_AndTheTableHoldsAtItsCap()
    {
        var limit = new WebRateLimit(_time);
        Assert.True(limit.TryTake(Phone, WebRateBucket.Act, out _));
        Assert.Equal(1, limit.Count);
        _time.Advance(WebRateLimit.IdleAfter);
        Assert.True(limit.TryTake(Other, WebRateBucket.Act, out _));
        Assert.Equal(1, limit.Count);   // the phone's went

        for (int i = 0; i < WebRateLimit.MaxEntries + 50; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
            Assert.True(limit.TryTake(new IPAddress(BitConverter.GetBytes(0x0A000000 + i)), WebRateBucket.Act, out _));
        }

        Assert.True(limit.Count <= WebRateLimit.MaxEntries);
    }

    [Fact]
    public void ARefusal_IsLoggedOnceAMinute()
    {
        var lines = new List<string>();
        void Record(DiagnosticEvent e)
        {
            if (e.Message.StartsWith("Web UI: too many", StringComparison.Ordinal))
            {
                lines.Add(e.Message);
            }
        }

        DiagnosticLog.Emitted += Record;
        try
        {
            var limit = new WebRateLimit(_time);
            Assert.True(limit.TryTake(Phone, WebRateBucket.Say, out _));
            Assert.False(limit.TryTake(Phone, WebRateBucket.Say, out _));
            Assert.False(limit.TryTake(Phone, WebRateBucket.Say, out _));
            Assert.Equal([WebUiText.RateLimitedLogLine(Phone, WebRateBucket.Say)], lines);

            _time.Advance(TimeSpan.FromMinutes(1));
            Assert.True(limit.TryTake(Phone, WebRateBucket.Say, out _));
            Assert.False(limit.TryTake(Phone, WebRateBucket.Say, out _));
            Assert.Equal(2, lines.Count);
        }
        finally
        {
            DiagnosticLog.Emitted -= Record;
        }
    }
}
