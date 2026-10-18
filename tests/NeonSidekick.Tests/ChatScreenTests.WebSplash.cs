using NeonSidekick.App;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The welcome splash on the Web UI's page (2026-10-18, the user's report: the page kept the picture after the first message): its entry is
/// marked as the splash, and goes from the page's copy whenever it goes from the PC — the first sent line (typed or the page's), a delete,
/// a cycle onto a picture that does not load.
/// </summary>
public partial class ChatScreenTests
{
    private List<WebEntry> SplashEntries() => [.. Web.Feed.Snapshot(m => m).Where(e => e.Splash == true)];

    [WindowsFact]
    public async Task WebSplash_IsMarked_AndTheFirstSentLine_TakesItOffThePage()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        SplashOf(2380, 100);
        List<WebEntry> before = [];
        StepsWhenIdle(
            input =>
            {
                before = SplashEntries();
                PushLine(input, "hi");
            },
            Line("/exit"));

        await RunAsync();

        var splash = Assert.Single(before);
        Assert.Equal("picture", splash.Kind);
        Assert.Empty(SplashEntries());
        Assert.DoesNotContain(Web.Feed.Snapshot(m => m), e => e.Seq == splash.Seq);
        Assert.Contains(Web.Feed.Snapshot(m => m), e => e.Kind == "user" && e.Text == "hi");
    }

    [WindowsFact]
    public async Task WebSplash_ALineFromThePage_TakesItOffThePageToo()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        SplashOf(2380, 100);
        _chat.EnqueueText("Hello.");
        var removed = new List<long>();
        StepsWhenIdle(
            _ =>
            {
                Web.Feed.Changed += change =>
                {
                    if (change is FeedChange.Removed gone)
                    {
                        removed.Add(gone.Seq);
                    }
                };
                Assert.Single(SplashEntries());
                Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = "hello from the phone" }, null).Outcome);
            },
            Line("/exit"));

        await RunAsync();

        Assert.Empty(SplashEntries());
        Assert.Single(removed);
        Assert.Equal("hello from the phone", UserText(Assert.Single(_chat.Requests)));
    }

    [WindowsFact]
    public async Task WebSplash_DeleteTwice_LeavesTheNextPictureAlone_OnThePage()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        string folder = _settings.ProfileSplashDirectory;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.bmp"), SmokeChecks.SolidBmp(2380, 60));
        File.WriteAllBytes(Path.Combine(folder, "b.bmp"), SmokeChecks.SolidBmp(2380, 100));
        SplashOf(2380, 100);
        List<WebEntry> before = [];
        List<WebEntry> after = [];
        StepsWhenIdle(
            input =>
            {
                before = SplashEntries();
                input.Push(Keys.Delete);
                input.Push(Keys.Delete);
            },
            input =>
            {
                after = SplashEntries();
                PushLine(input, "/exit");
            });

        await RunAsync();

        var first = Assert.Single(before);
        var next = Assert.Single(after);   // one splash entry, the next picture's: the deleted one's taken out (it stayed until 2026-10-18)
        Assert.NotEqual(first.Seq, next.Seq);
    }

    [WindowsFact]
    public async Task WebSplash_ACycleOntoAPictureThatDoesNotLoad_LeavesNoneOnThePage()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        _random = new Random(7);
        SplashOf((2380, 100), (2380, 200));
        string first = SplashName(new Random(7).Next(2));   // the startup pick, the screen's Random
        string other = first == SplashName(0) ? SplashName(1) : SplashName(0);
        var source = _splash!;
        _splash = source with { Load = name => name == other ? null : source.Load(name) };
        List<WebEntry> before = [];
        List<WebEntry> after = [];
        StepsWhenIdle(
            input =>
            {
                before = SplashEntries();
                input.Push(Keys.Right);   // onto the picture that will not load: the splash is gone from the PC
            },
            input =>
            {
                after = SplashEntries();
                PushLine(input, "/exit");
            });

        await RunAsync();

        Assert.Single(before);
        Assert.Empty(after);
    }
}
