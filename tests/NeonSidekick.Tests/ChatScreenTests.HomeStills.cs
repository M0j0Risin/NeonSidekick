using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/ha stills view|thumbs|purge</c> on the screen (2026-10-27, the user's ask): the camera stills' folder opened or emptied with no Home
/// Assistant URL set, the other picture folders left alone, a No keeping them; and the chip's 2 s reuse never reopening a still deleted
/// under it (a purge, or by hand), while a No keeps reusing the same file.
/// </summary>
public partial class ChatScreenTests
{
    private string HomeStillsDir => Path.Combine(_settings.ProfileImagesDirectory, HaCamera.FolderName);

    [Fact]
    public async Task HomeStills_Purge_WithNoHomeAssistant_EmptiesTheStills_ToTheBin_AndLeavesTheOtherPictures()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HomeAssistantUrl = ""; });
        var bin = new FakeRecycleBin();
        _recycleBin = bin;
        TwoPhotos(HomeStillsDir);
        string other = Path.Combine(_settings.ProfileImagesDirectory, AppSettingsData.DefaultCameraOutputFolder, "keep.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "stays");
        PushLine("/ha stills purge");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.PurgePrompt(HomeStillsDir, 2, 1, 8, toBin: true), output);
        Assert.Contains(ChatScreen.PurgedToBinNotice(HomeStillsDir, 2, 1, 8), output);
        Assert.Equal(2, bin.Moved.Count);
        Assert.True(Directory.Exists(HomeStillsDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(HomeStillsDir));
        Assert.True(File.Exists(other));
        Assert.DoesNotContain(HaText.NotConfigured, output);
    }

    [Fact]
    public async Task HomeStills_Purge_ANo_KeepsThem()
    {
        _settings.Update(d => d.TtsOutput = false);
        _recycleBin = new FakeRecycleBin();
        TwoPhotos(HomeStillsDir);
        PushLine("/ha stills purge");
        _console.Input.PushKey(Keys.Enter);   // No is on the cursor
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · " + ChatScreen.KeptNotice, output);
        Assert.True(File.Exists(Path.Combine(HomeStillsDir, "a.jpg")));
    }

    [Fact]
    public async Task HomeStills_Purge_AMissingFolder_IsNothingIn_WithoutAsking()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/ha stills purge");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · " + ChatScreen.PurgeEmptyNotice(HomeStillsDir), output);
        Assert.DoesNotContain(ChatScreen.KeptNotice, output);
    }

    [Fact]
    public async Task HomeStills_ViewAndThumbs_OpenTheFolder_MadeWhenMissing_AndBareIsTheUsage()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HomeAssistantUrl = ""; });
        var viewed = new List<string>();
        var browsed = new List<string>();
        _openViewer = viewed.Add;
        _openThumbs = (folder, _) => browsed.Add(folder);
        PushLine("/ha stills view");
        PushLine("/ha STILLS thumbs");
        PushLine("/ha stills");
        PushLine("/ha stills bogus");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([HomeStillsDir], viewed);
        Assert.Equal([HomeStillsDir], browsed);
        Assert.True(Directory.Exists(HomeStillsDir));
        Assert.Equal(2, output.Split("  ✗ " + HaText.StillsUsage).Length - 1);
        Assert.DoesNotContain(HaText.NotConfigured, output);
    }

    /// <summary>A purge inside the 2 s reuse: the next tap (still one request) saves the still again rather than opening the deleted path.</summary>
    [Fact]
    public async Task HomeBar_ACamerasStillPurged_TheReuseSavesItAgain()
    {
        var stub = CameraServer();
        _recycleBin = new FakeRecycleBin();
        var viewed = new List<string>();
        _viewPicture = path => { lock (viewed) { viewed.Add(path); } };
        int Viewed() { lock (viewed) { return viewed.Count; } }
        CameraStages(
            (() => true, input => PushLine(input, "/homebar tap Porch Camera")),
            (() => Viewed() == 1, input => { PushLine(input, "/ha stills purge"); input.Push(Keys.Down, Keys.Enter); }),
            (() => !Directory.EnumerateFiles(HomeCameraFolder).Any(), input => PushLine(input, "/homebar tap Porch Camera")),
            (() => Viewed() == 2, input => PushLine(input, "/exit")));

        await RunAsync();

        Assert.Equal(1, Stills(stub));   // reused, not fetched again
        string saved = Assert.Single(Directory.GetFiles(HomeCameraFolder));
        Assert.Equal(saved, viewed[1]);
        Assert.True(File.Exists(viewed[1]));
    }

    /// <summary>The kept still deleted by hand (the viewer's delete, Explorer): the next tap saves it anew.</summary>
    [Fact]
    public async Task HomeBar_ACamerasStillDeletedByHand_TheReuseSavesItAgain()
    {
        var stub = CameraServer();
        var viewed = new List<string>();
        _viewPicture = path => { lock (viewed) { viewed.Add(path); } };
        int Viewed() { lock (viewed) { return viewed.Count; } }
        CameraStages(
            (() => true, input => PushLine(input, "/homebar tap Porch Camera")),
            (() => Viewed() == 1, input => { File.Delete(viewed[0]); PushLine(input, "/homebar tap Porch Camera"); }),
            (() => Viewed() == 2, input => PushLine(input, "/exit")));

        await RunAsync();

        Assert.Equal(1, Stills(stub));
        Assert.Single(Directory.GetFiles(HomeCameraFolder));
        Assert.True(File.Exists(viewed[1]));
    }

    /// <summary>A purge answered No: the reuse opens the same file, and no second copy is written.</summary>
    [Fact]
    public async Task HomeBar_ACamerasStill_APurgeAnsweredNo_TheReuseOpensTheSameFile()
    {
        var stub = CameraServer();
        _recycleBin = new FakeRecycleBin();
        var viewed = new List<string>();
        _viewPicture = path => { lock (viewed) { viewed.Add(path); } };
        int Viewed() { lock (viewed) { return viewed.Count; } }
        CameraStages(
            (() => true, input => PushLine(input, "/homebar tap Porch Camera")),
            (() => Viewed() == 1, input => { PushLine(input, "/ha stills purge"); input.Push(Keys.Enter); }),
            (() => Output.Contains(ChatScreen.KeptNotice, StringComparison.Ordinal), input => PushLine(input, "/homebar tap Porch Camera")),
            (() => Viewed() == 2, input => PushLine(input, "/exit")));

        await RunAsync();

        Assert.Equal(1, Stills(stub));
        string saved = Assert.Single(Directory.GetFiles(HomeCameraFolder));
        Assert.Equal([saved, saved], viewed);
    }
}
