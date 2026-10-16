using System.Net;
using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// Later on 2026-10-15 on the screen: the PC's point (the user's ask: the page's selection follows the PC's) — its strip's step and click, its
/// viewer's keys on any picture, its thumbnail browser's pick, <c>/view</c> on a picture, never the page's own pick, a step that lets the
/// highlight go or a folder — and the page's 🗑️: to the bin (a fake) or directly as <i>File deletes to Recycle bin</i> says, the strip dropping the
/// picture, the picture no longer served, a second delete not found, and no point.
/// </summary>
public partial class ChatScreenTests
{
    private FakeRecycleBin? _recycleBin;

    private static List<WebPoint?> PointEvents(WebEventHub.Subscription subscription)
    {
        var points = new List<WebPoint?>();
        while (subscription.Reader.TryRead(out var item))
        {
            if (item.Name == "point")
            {
                points.Add(JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebPoint));
            }
        }

        return points;
    }

    [Fact]
    public async Task ThePcsStrip_StepAndClick_PointAtThePicturesFile_AndAStepOffTheNewest_AtNothing()
    {
        ComfyServer();
        PaneOf40Rows();
        WebEventHub.Subscription? subscription = null;
        WebPoint? afterRight = null;
        StepsWhenIdle(Line("/imagine a cat --seed 5"), input =>
        {
            subscription = Web.Events.Subscribe();
            input.Push(Keys.Right);   // the newest highlighted: a point
        }, input =>
        {
            afterRight = _running!.PcPointForTests;
            input.Push(Keys.Left);    // ← off the newest lets the highlight go: no point
        }, Line("/exit"));

        await RunAsync();

        string key = WebPictureKey.Of(ComfyPicture("pony-5.png"));
        Assert.Equal(key, afterRight!.Key);
        Assert.Equal(afterRight, _running!.PcPointForTests);   // the ← made none
        Assert.Equal(key, Web.Snapshot().Strip!.Pictures.Single().Key);   // the strip's picture carries the same key
        Assert.Contains(PointEvents(subscription!), p => p?.Key == key);
        subscription!.Dispose();
    }

    [Fact]
    public async Task ThePagesOwnPick_IsNoPoint_ButTheViewersKeysAndTheBrowsersPick_Are_FromTheirThreads()
    {
        ComfyServer();
        PaneOf40Rows();
        _followViewer = _ => { };
        string other = Path.Combine(_dir, "elsewhere", "dog.png");
        StepsWhenIdle(Line("/imagine a cat --seed 5"), input =>
        {
            var strip = Web.Snapshot().Strip!;
            Assert.Equal(WebOutcome.Ok, Web.SelectStripPicture(new WebStripSelect { Id = strip.Pictures[0].Id }, IPAddress.Loopback).Outcome);
            WaitUntil(() => Web.Snapshot().Strip!.Selected == strip.Pictures[0].Id);
            Assert.Null(_running!.PcPointForTests);   // the page's own pick: no echo
            Task.Run(() => _running!.ViewerBrowsed(other)).Wait();
            Assert.Equal((WebPictureKey.Of(other), 1L), (_running!.PcPointForTests!.Key, _running.PcPointForTests.Seq));
            Task.Run(() => _running!.ThumbsPicked(ComfyPicture("pony-5.png"))).Wait();
            Assert.Equal((WebPictureKey.Of(ComfyPicture("pony-5.png")), 2L), (_running!.PcPointForTests!.Key, _running.PcPointForTests.Seq));
            PushLine(input, "/exit");
        });

        await RunAsync();

        Assert.Equal(2, _running!.PcPointForTests!.Seq);
    }

    [WindowsFact]
    public async Task ViewOnAPicture_PointsAtIt_AndOnAFolder_AtNothing()
    {
        _settings.Update(d => d.TtsOutput = false);
        string files = Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);
        Directory.CreateDirectory(Path.Combine(files, "docs"));
        File.WriteAllBytes(Path.Combine(files, "docs", "square.bmp"), SmokeChecks.SolidBmp(4, 4));
        _viewPicture = _ => { };
        _openViewer = _ => { };
        WebPoint? afterFolder = null;
        StepsWhenIdle(Line("/view docs"), input =>
        {
            afterFolder = _running!.PcPointForTests;
            PushLine(input, "/view docs/square.bmp");
        }, Line("/exit"));

        await RunAsync();

        Assert.Null(afterFolder);
        Assert.Equal(WebPictureKey.Of(Path.Combine(files, "docs", "square.bmp")), _running!.PcPointForTests!.Key);
    }

    [Fact]
    public async Task ThePagesDelete_ToTheBin_DropsTheStripPicture_StopsServingIt_AndMakesNoPoint()
    {
        ComfyServer();
        PaneOf40Rows();
        _recycleBin = new FakeRecycleBin { Folder = Path.Combine(_dir, "bin") };
        WebReply first = default;
        WebReply second = default;
        int id = -1;
        WebEventHub.Subscription? subscription = null;
        StepsWhenIdle(Line("/imagine a cat --seed 5"), input =>
        {
            subscription = Web.Events.Subscribe();
            var picture = Web.Snapshot().Strip!.Pictures.Single();
            Assert.True(picture.Deletable);
            id = picture.Id;
            first = Web.DeletePicture(new WebPictureDelete { Id = id }, IPAddress.Loopback);
            second = Web.DeletePicture(new WebPictureDelete { Id = id }, IPAddress.Loopback);
            PushLine(input, "/exit");
        });

        await RunAsync();

        Assert.Equal((WebOutcome.Ok, WebUiText.PictureDeleted("pony-5.png", DeleteWay.Recycled)), (first.Outcome, first.Message));
        Assert.Equal(WebOutcome.NotFound, second.Outcome);
        Assert.Equal([ComfyPicture("pony-5.png")], _recycleBin.Moved);
        Assert.False(File.Exists(ComfyPicture("pony-5.png")));
        Assert.Null(Web.Snapshot().Strip);   // the strip's only picture gone
        Assert.Null(Web.Picture(id));
        Assert.Contains(StripEvents(subscription!), s => s is null);
        Assert.Null(_running!.PcPointForTests);   // the page's own act: no point
        subscription!.Dispose();
    }

    [Fact]
    public async Task ThePagesDelete_WithTheSettingOff_DeletesDirectly()
    {
        ComfyServer();
        PaneOf40Rows();
        _recycleBin = new FakeRecycleBin();
        _settings.Update(d => d.FileDeletesToRecycleBin = false);
        WebReply reply = default;
        StepsWhenIdle(Line("/imagine a cat --seed 5"), input =>
        {
            reply = Web.DeletePicture(new WebPictureDelete { Id = Web.Snapshot().Strip!.Pictures.Single().Id }, null);
            PushLine(input, "/exit");
        });

        await RunAsync();

        Assert.Equal((WebOutcome.Ok, "Deleted pony-5.png."), (reply.Outcome, reply.Message));
        Assert.Empty(_recycleBin.Moved);
        Assert.False(File.Exists(ComfyPicture("pony-5.png")));
    }

    /// <summary>The page's saved prompts sheet (later on 2026-10-15): the profile's prompts, numbered as the PC's pane numbers them.</summary>
    [Fact]
    public async Task ThePagesPromptsSheet_ListsTheProfilesPrompts_NumberedAsAtThePc()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/prompts save Summarize this.");
        PushLine("/prompts save Review the diff");
        PushLine("/exit");

        await RunAsync();

        var items = Web.Prompts().Items;
        Assert.Equal([(1, "Summarize this."), (2, "Review the diff")], items.Select(p => (p.Number, p.Text)));
        // Each with the store's own id (2026-10-16), what the sheet's 🗑️ names.
        Assert.Equal(new NeonSidekick.Prompts.SavedPrompts(_settings.ProfileDirectory, _time).List().Select(p => p.Id), items.Select(p => p.Id));
    }
}
