using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The picture folders on the screen (2026-10-18, the user's ask): <c>/camera</c>, <c>/screen</c>, <c>/splash</c> and <c>/pasted purge</c> — the
/// Recycle bin under <c>File deletes to Recycle bin</c> (each entry, the folder kept), for good without it, a drive with no bin, a refusal
/// part-way, an empty setting refused, a missing folder — and <c>/splash</c> and <c>/pasted view|thumbs</c>, under a reply too; the bare
/// <c>/comfy</c> as <c>/tools comfy</c>.
/// </summary>
public partial class ChatScreenTests
{
    private string CameraImagesDir => Path.Combine(_settings.ProfileImagesDirectory, AppSettingsData.DefaultCameraOutputFolder);

    private void TwoPhotos(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.jpg"), "12345");
        File.WriteAllText(Path.Combine(dir, "sub", "b.jpg"), "678");
    }

    [Fact]
    public async Task CameraPurge_WithTheBin_SendsEachEntryThere_AndKeepsTheFolder()
    {
        _settings.Update(d => d.TtsOutput = false);
        var bin = new FakeRecycleBin();
        _recycleBin = bin;
        TwoPhotos(CameraImagesDir);
        PushLine("/camera purge");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.PurgePrompt(CameraImagesDir, 2, 1, 8, toBin: true), output);
        Assert.Equal([CameraImagesDir], bin.Probed);   // the drive asked about before the question
        Assert.Equal([Path.Combine(CameraImagesDir, "a.jpg"), Path.Combine(CameraImagesDir, "sub")], bin.Moved);
        Assert.True(Directory.Exists(CameraImagesDir));   // the viewer and the browser watch it
        Assert.Empty(Directory.EnumerateFileSystemEntries(CameraImagesDir));
        Assert.Contains(ChatScreen.PurgedToBinNotice(CameraImagesDir, 2, 1, 8), output);
    }

    [Fact]
    public async Task CameraPurge_WithTheBinOff_IsForGood_AndANoKeeps()
    {
        _settings.Update(d => { d.TtsOutput = false; d.FileDeletesToRecycleBin = false; });
        var bin = new FakeRecycleBin();
        _recycleBin = bin;
        TwoPhotos(CameraImagesDir);
        PushLine("/camera purge");
        _console.Input.PushKey(Keys.Enter);   // No is on the cursor
        PushLine("/camera purge");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.PurgePrompt(CameraImagesDir, 2, 1, 8), output);
        Assert.Contains("  · " + ChatScreen.KeptNotice, output);
        Assert.Contains(ChatScreen.PurgedNotice(CameraImagesDir, 2, 1, 8), output);
        Assert.Empty(bin.Moved);
        Assert.Empty(bin.Probed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(CameraImagesDir));
    }

    /// <summary>
    /// The way is decided before the question and handed to the purge (2026-10-20, the code review): the setting turned off while the question
    /// waits (here, as the drive is probed) changes nothing, and the yes to "send to the bin" sends them there.
    /// </summary>
    [Fact]
    public async Task CameraPurge_TheSettingTurnedOffAfterTheQuestion_StillGoesToTheBin()
    {
        _settings.Update(d => d.TtsOutput = false);
        var bin = new FakeRecycleBin { OnProbe = _ => _settings.Update(d => d.FileDeletesToRecycleBin = false) };
        _recycleBin = bin;
        TwoPhotos(CameraImagesDir);
        PushLine("/camera purge");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.PurgePrompt(CameraImagesDir, 2, 1, 8, toBin: true), output);
        Assert.Equal(2, bin.Moved.Count);
        Assert.Contains(ChatScreen.PurgedToBinNotice(CameraImagesDir, 2, 1, 8), output);
        Assert.DoesNotContain(ChatScreen.PurgedNotice(CameraImagesDir, 2, 1, 8), output);
    }

    /// <summary>A drive with no bin (2026-10-20, the code review): known before the question, which says for good and why; nothing handed to the bin.</summary>
    [Fact]
    public async Task ScreenPurge_OnADriveWithNoBin_AsksForGood_AndSaysWhy()
    {
        _settings.Update(d => d.TtsOutput = false);
        var bin = new FakeRecycleBin { ProbeAnswer = new RecycleResult(RecycleOutcome.NoBin) };
        _recycleBin = bin;
        string dir = Path.Combine(_settings.ProfileImagesDirectory, AppSettingsData.DefaultScreenOutputFolder);
        TwoPhotos(dir);
        PushLine("/screen purge");   // no screen capture in the fixture: the folder alone
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.PurgePrompt(dir, 2, 1, 8, toBin: false, FileText.RecycleNoBinHere), output);
        Assert.Contains(ChatScreen.PurgedNotice(dir, 2, 1, 8), output);
        Assert.Empty(bin.Moved);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        Assert.DoesNotContain(NeonSidekick.Screen.ScreenText.Unsupported, output);
    }

    /// <summary>
    /// A bin that can't be probed (a Mac's Trash) answering no bin at the first move (2026-10-20, the code review: the purge then deleted for good
    /// after a yes to the bin): nothing deleted, the rest asked again for good; a yes deletes them, a no keeps them all.
    /// </summary>
    [Fact]
    public async Task CameraPurge_NoBinFoundAtTheFirstMove_DeletesNothing_AndAsksAgainForGood()
    {
        _settings.Update(d => d.TtsOutput = false);
        var bin = new FakeRecycleBin { Answer = new RecycleResult(RecycleOutcome.NoBin) };
        _recycleBin = bin;
        TwoPhotos(CameraImagesDir);
        StepsWhenIdle(
            input =>
            {
                PushLine(input, "/camera purge");
                input.Push(Keys.Down, Keys.Enter);   // yes, to the bin
                input.Push(Keys.Enter);              // no, not for good
            },
            input =>
            {
                PushLine(input, "/camera purge");
                input.Push(Keys.Down, Keys.Enter);
                input.Push(Keys.Down, Keys.Enter);
            },
            Line("/exit"));

        string output = await RunAsync();

        string again = ChatScreen.PurgePrompt(CameraImagesDir, 2, 1, 8, toBin: false, FileText.RecycleNoBinHere);
        Assert.Contains(again, output);
        Assert.Contains("  · " + ChatScreen.KeptNotice, output);
        Assert.Equal(2, bin.Moved.Count);   // one try each time, the first entry, then nothing more to a bin that isn't there
        Assert.Contains(ChatScreen.PurgedNotice(CameraImagesDir, 2, 1, 8), output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(CameraImagesDir));
    }

    /// <summary>
    /// The bin refusing one entry (2026-10-20, the code review: one refusal stopped the purge): it is left, the rest go on, the notice counts only
    /// what went and names the reason. Never deleted for good behind the bin's refusal.
    /// </summary>
    [Fact]
    public async Task PastedPurge_ABinThatRefusesOneEntry_LeavesIt_AndGoesOn()
    {
        _settings.Update(d => d.TtsOutput = false);
        var bin = new FakeRecycleBin();
        _recycleBin = bin;
        string dir = Path.Combine(_settings.ProfileImagesDirectory, AppSettingsData.DefaultPastedImagesFolder);
        TwoPhotos(dir);
        File.WriteAllText(Path.Combine(dir, "c.jpg"), "9");
        StepsWhenIdle(
            input =>
            {
                PushLine(input, "/pasted purge");
                input.Push(Keys.Down, Keys.Enter);
            },
            Line("/exit"));
        // a.jpg goes; c.jpg meets a refusal; sub goes.
        bin.AnswerFor = path => path.EndsWith("c.jpg", StringComparison.Ordinal) ? new RecycleResult(RecycleOutcome.Failed, "the bin is full") : new RecycleResult(RecycleOutcome.Recycled);

        string output = await RunAsync();

        Assert.Contains(ChatScreen.PurgeLeftError(dir, 2, 3, 1, "the bin is full"), output);
        Assert.True(File.Exists(Path.Combine(dir, "c.jpg")));
        Assert.False(File.Exists(Path.Combine(dir, "a.jpg")));
        Assert.False(Directory.Exists(Path.Combine(dir, "sub")));
    }

    /// <summary>Esc stops a purge between items (2026-10-20, the code review: nothing could stop one): how many went, the rest left.</summary>
    [Fact]
    public async Task CameraPurge_Esc_StopsBetweenItems_AndSaysHowFar()
    {
        _settings.Update(d => d.TtsOutput = false);
        var bin = new FakeRecycleBin();
        _recycleBin = bin;
        TwoPhotos(CameraImagesDir);
        var input = Scripted();
        // Esc typed while the first item moves: that move ends once the watch has read it, and the second never starts.
        bin.AnswerFor = _ =>
        {
            input.Push(Keys.Escape);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (input.Queued > 0 && waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(5);
            }

            Thread.Sleep(200);
            return new RecycleResult(RecycleOutcome.Recycled);
        };
        StepsWhenIdle(
            step =>
            {
                PushLine(step, "/camera purge");
                step.Push(Keys.Down, Keys.Enter);
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(ChatScreen.PurgeStoppedError(CameraImagesDir, 1, 2, ChatScreen.PurgeCancelledWhy), output);
        Assert.Single(bin.Moved);
        Assert.True(Directory.Exists(Path.Combine(CameraImagesDir, "sub")));
    }

    [Fact]
    public async Task PastedPurge_AnEmptySetting_IsTheImagesFolder_AndRefused()
    {
        _settings.Update(d => { d.TtsOutput = false; d.PastedImagesFolder = ""; });
        Directory.CreateDirectory(_settings.ProfileImagesDirectory);
        File.WriteAllText(Path.Combine(_settings.ProfileImagesDirectory, "keep.png"), "stays");
        PushLine("/pasted purge");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ChatScreen.PurgeHereError(ChatScreen.PastedFolderLabel), output);
        Assert.True(File.Exists(Path.Combine(_settings.ProfileImagesDirectory, "keep.png")));
    }

    [Fact]
    public async Task SplashPurge_AMissingFolder_IsNothingIn_WithoutAsking()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/splash purge");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · " + ChatScreen.PurgeEmptyNotice(_settings.ProfileSplashDirectory), output);
        Assert.DoesNotContain(ChatScreen.KeptNotice, output);
    }

    /// <summary><c>/splash bogus</c> is the usage error and wipes nothing: the line sent before it is still in the conversation.</summary>
    [Fact]
    public async Task Splash_AnUnknownWord_IsTheUsage_AndClearsNothing()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("one");
        _chat.EnqueueText("two");
        PushLine("hi");
        PushLine("/splash bogus");
        PushLine("again");
        PushLine("/pasted");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + SplashCommand.Usage, output);
        Assert.Contains("  ✗ " + PastedCommand.Usage, output);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(2, _chat.Requests[1].Count(m => m.Role == Microsoft.Extensions.AI.ChatRole.User));   // "hi" kept
    }

    [Fact]
    public async Task SplashAndPasted_ViewAndThumbs_OpenTheirFolders_MadeWhenMissing_AndAnEmptySplashSaysSo()
    {
        _settings.Update(d => d.TtsOutput = false);
        var viewed = new List<string>();
        var browsed = new List<string>();
        _openViewer = viewed.Add;
        _openThumbs = (folder, _) => browsed.Add(folder);
        string pasted = Path.Combine(_settings.ProfileImagesDirectory, AppSettingsData.DefaultPastedImagesFolder);
        PushLine("/splash view");
        PushLine("/pasted thumbs");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([_settings.ProfileSplashDirectory], viewed);
        Assert.Equal([pasted], browsed);
        Assert.True(Directory.Exists(_settings.ProfileSplashDirectory));
        Assert.True(Directory.Exists(pasted));
        Assert.Equal(1, output.Split(SplashCommand.BuiltInNotice).Length - 1);
    }

    /// <summary>A window that fails to open says why, and nothing of the built-in pictures (2026-10-20, the code review: the note followed the error).</summary>
    [Fact]
    public async Task SplashViewAndThumbs_AWindowThatFails_SaysNothingOfTheBuiltInOnes()
    {
        _settings.Update(d => d.TtsOutput = false);
        _openViewer = _ => throw new InvalidOperationException("no window");
        _openThumbs = (_, _) => throw new InvalidOperationException("no browser");
        PushLine("/splash view");
        PushLine("/splash thumbs");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("no window", output);
        Assert.Contains("no browser", output);
        Assert.DoesNotContain(SplashCommand.BuiltInNotice, output);
    }

    [Fact]
    public async Task SplashThumbs_OnAFolderWithPictures_SaysNothingOfTheBuiltInOnes()
    {
        _settings.Update(d => d.TtsOutput = false);
        _openThumbs = (_, _) => { };
        Directory.CreateDirectory(_settings.ProfileSplashDirectory);
        File.WriteAllBytes(Path.Combine(_settings.ProfileSplashDirectory, "mine.png"), [0x89, 0x50, 0x4E, 0x47]);
        PushLine("/splash thumbs");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(SplashCommand.BuiltInNotice, output);
    }

    /// <summary>Each quick form typed under a running reply opens its window before the reply ends; a bad /splash word's error too.</summary>
    [Theory]
    [InlineData("/splash view")]
    [InlineData("/splash thumbs")]
    [InlineData("/pasted view")]
    [InlineData("/pasted thumbs")]
    public async Task MidTurn_TheFolderWindows_OpenAtOnce(string line)
    {
        int openedDuringTheReply = -1;
        var opened = new List<string>();
        _openViewer = opened.Add;
        _openThumbs = (folder, _) => opened.Add(folder);
        MidTurnFixture(i =>
        {
            if (i == 0)
            {
                PushLine(line);
            }
            else if (i == 2)
            {
                openedDuringTheReply = opened.Count;
            }
        });

        string output = await RunAsync();

        Assert.Equal(1, openedDuringTheReply);
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task MidTurn_ABadSplashWord_IsItsErrorAtOnce_TheReplyRunsOn()
    {
        MidTurnFixture(i =>
        {
            if (i == 0)
            {
                PushLine("/splash bogus");
            }
        });

        string output = await RunAsync();

        Assert.Contains(SplashCommand.Usage, output);
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
        Assert.Single(_chat.Requests);
    }

    /// <summary>The bare <c>/comfy</c> opens <c>/tools comfy</c>'s page (2026-10-18, the user's ask); the status it showed is <c>/comfy status</c>.</summary>
    [Fact]
    public async Task Comfy_Bare_OpensTheComfyToolsPage_AsSlashToolsComfy()
    {
        _settings.Update(d => { d.TtsOutput = false; d.MenuMaxHeight = "full"; });
        _console.Profile.Height = 112;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(input => { PushLine(input, "/comfy"); input.Push(Keys.Escape); }, Line("/exit"));

        string bare = await RunAsync();

        Assert.Contains(SettingsMenu.FieldName(SettingsField.ComfyTools), bare);
        Assert.Empty(_chat.Requests);
    }
}
