using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's pictures, the pure parts (later on 2026-10-14): the feed's count of the entries holding each picture, the session's lookup and
/// addresses, the renderer's entry (a picture the PC does not draw too), the server's sniffing and encoding, and the files' tag.
/// </summary>
public sealed class WebUiPictureTests
{
    private static WebPicture Pic(int id) => new(id, "p" + id + ".png", "/t", "/m", "/f", 4, 4, false);

    [Fact]
    public void APictureEntry_CarriesItsPictures_InTheEntryAndTheSnapshot()
    {
        var feed = new TranscriptFeed();
        var appended = new List<WebEntry>();
        feed.Changed += c => { if (c is FeedChange.Appended a) appended.Add(a.Entry); };

        feed.Add(FeedKind.Picture, "2 pictures", pictures: [Pic(3), Pic(4)]);

        Assert.Equal([3, 4], appended.Single().Pictures!.Select(p => p.Id));
        Assert.Equal([3, 4], feed.Snapshot(m => m).Single().Pictures!.Select(p => p.Id));
        Assert.True(feed.Holds(3) && feed.Holds(4));
        Assert.False(feed.Holds(5));
    }

    [Fact]
    public void APicture_IsLetGo_OnlyWhenItsLastEntryGoes()
    {
        var feed = new TranscriptFeed();
        var released = new List<int>();
        feed.PictureReleased += released.Add;
        long first = feed.Add(FeedKind.Picture, "a picture", pictures: [Pic(1)]);
        long second = feed.Add(FeedKind.Picture, "a picture", pictures: [Pic(1)]);   // the splash drawn again, the same id

        Assert.True(feed.Remove(first));
        Assert.Empty(released);
        Assert.True(feed.Holds(1));

        Assert.True(feed.Remove(second));
        Assert.Equal([1], released);
        Assert.False(feed.Holds(1));
        Assert.False(feed.Remove(second));   // gone already
    }

    [Fact]
    public void ATrimmedPictureEntry_LetsItsPictureGo()
    {
        var feed = new TranscriptFeed();
        var released = new List<int>();
        feed.PictureReleased += released.Add;
        feed.Add(FeedKind.Picture, "a picture", pictures: [Pic(7)]);
        for (int i = 0; i < TranscriptFeed.MaxEntries; i++)
        {
            feed.Add(FeedKind.Notice, "n" + i);
        }

        Assert.Equal([7], released);
        Assert.False(feed.Holds(7));
    }

    [Fact]
    public void ALongReply_TrimmingAPictureEntry_LetsItGo()
    {
        var feed = new TranscriptFeed();
        var released = new List<int>();
        feed.PictureReleased += released.Add;
        feed.Add(FeedKind.Picture, "a picture", pictures: [Pic(2)]);
        feed.Begin(null);

        feed.Append(new string('x', TranscriptFeed.MaxChars + 10));

        Assert.Equal([2], released);
    }

    [Fact]
    public void Clear_LetsEveryPictureGo()
    {
        var feed = new TranscriptFeed();
        var released = new List<int>();
        feed.PictureReleased += released.Add;
        feed.Add(FeedKind.Picture, "2 pictures", pictures: [Pic(1), Pic(2)]);

        feed.Clear();

        Assert.Equal([1, 2], released.Order());
        Assert.False(feed.Holds(1));
    }

    [Fact]
    public void TheSession_ServesOnlyAPictureTheFeedHolds_AndBuildsItsAddresses()
    {
        using var session = new WebUiSession(
            () => new WebState("p", "", "", "m", false, false, false, "", [], null, "1"),
            _ => WebReply.Ok, () => null, () => { });
        var source = new WebPictureSource("a.png", null, [1, 2, 3], "image/png");
        session.PictureSource = id => id == 9 ? source : null;

        Assert.Null(session.Picture(9));   // not on the page
        var picture = session.Describe(9, "a.png", 4, 3, false);
        session.Feed.Add(FeedKind.Picture, "a picture", pictures: [picture]);

        Assert.Same(source, session.Picture(9));
        Assert.Null(session.Picture(8));
        Assert.Equal(WebUiHost.PicturePrefix + session.PictureRun + "/9/thumb", picture.Thumb);
        Assert.Equal(WebUiHost.PicturePrefix + session.PictureRun + "/9/mid", picture.Mid);
        Assert.Equal(WebUiHost.PicturePrefix + session.PictureRun + "/9/full", picture.Full);
        Assert.Equal(32, session.PictureRun.Length);   // 128 bits
        Assert.Equal(WebUiText.Words, session.Snapshot().Words);
    }

    [Fact]
    public void TheRenderer_OffersEveryPicture_ThoughItDrawsNone()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        var feed = new TranscriptFeed();
        var renderer = new TranscriptRenderer(console) { Feed = feed, WebPicture = id => id == 5 ? null : Pic(id) };

        long? seq = renderer.Images([], webIds: [1, 2]);
        renderer.WebOnlyPictures([3, 5]);
        Assert.Null(renderer.WebOnlyPictures([]));

        var entries = feed.Snapshot(m => m);
        Assert.Equal(seq, entries[0].Seq);
        Assert.Equal([1, 2], entries[0].Pictures!.Select(p => p.Id));
        Assert.Equal(TranscriptRenderer.PictureWords(2), entries[0].Text);
        Assert.Equal([3], entries[1].Pictures!.Select(p => p.Id));   // an id the screen does not know is left out
        Assert.Equal(2, entries.Count);
        Assert.Equal("", console.Output.Trim());
    }

    [Fact]
    public void WithoutTheHook_APictureEntryKeepsItsWordsAlone()
    {
        var feed = new TranscriptFeed();
        var renderer = new TranscriptRenderer(new Spectre.Console.Testing.TestConsole()) { Feed = feed };

        renderer.WebOnlyPictures([1]);

        var entry = Assert.Single(feed.Snapshot(m => m));
        Assert.Equal("a picture", entry.Text);
        Assert.Null(entry.Pictures);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0, 0, 0, 0, 0, 0 }, "image/gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { 0, 0, 0, 0x1C, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f' }, "image/avif")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "image/bmp")]
    [InlineData(new byte[] { 0, 0, 0, 0x1C, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, null)]   // HEIC: re-encoded
    [InlineData(new byte[] { (byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, null)]   // TIFF
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g', 0, 0, 0, 0, 0, 0, 0, 0 }, null)]   // never SVG
    public void Sniff_NamesOnlyWhatEveryBrowserDraws(byte[] head, string? type)
    {
        Assert.Equal(type, PictureServer.Sniff(head));
    }

    [Theory]
    [InlineData("thumb", PictureSize.Thumb)]
    [InlineData("mid", PictureSize.Mid)]
    [InlineData("full", PictureSize.Full)]
    [InlineData("big", null)]
    public void TheSizes_ReadTheirWords(string word, PictureSize? size)
    {
        Assert.Equal(size, PictureServer.SizeOf(word));
    }

    [WindowsFact]
    public void Encode_ShrinksToTheThumbnail_AndNeverEnlarges()
    {
        var big = PictureServer.Encode(App.SmokeChecks.SolidBmp(1280, 640), PictureSize.Thumb)!;
        var small = PictureServer.Encode(App.SmokeChecks.SolidBmp(40, 20), PictureSize.Thumb)!;

        Assert.Equal("image/jpeg", big.ContentType);
        var bigInfo = Images.ImageEditor.Info(big.Bytes)!;
        Assert.Equal((PictureServer.ThumbSide, PictureServer.ThumbSide / 2), (bigInfo.Width, bigInfo.Height));
        var smallInfo = Images.ImageEditor.Info(small.Bytes)!;
        Assert.Equal((40, 20), (smallInfo.Width, smallInfo.Height));
        Assert.Null(PictureServer.Encode([1, 2, 3], PictureSize.Thumb));
    }

    [Fact]
    public void TheFilesTag_FollowsTheirBytes()
    {
        string tag = WebUiAssets.Tag(["\"a\"", "\"b\""]);

        Assert.Equal(16, tag.Length);
        Assert.Equal(tag, WebUiAssets.Tag(["\"a\"", "\"b\""]));
        Assert.NotEqual(tag, WebUiAssets.Tag(["\"a\"", "\"c\""]));
        Assert.Equal(WebUiAssets.Tag(WebUiAssets.Names.Select(n => WebUiAssets.Find(n)!.ETag)), WebUiAssets.Current);
    }
}
