using NeonSidekick.App;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/sessions export</c> (2026-10-30, plan i-want-to-plan-effervescent-adleman): a stored session written into the working folder.</summary>
public partial class ChatScreenTests
{
    private string ExportsFolder => Path.Combine(_settings.ProfileDirectory, "files");

    private string ExportStem(long id)
    {
        using var store = OpenSessions();
        return SessionExport.Stem(store.Summary(id)!, _time.LocalTimeZone);
    }

    [Fact]
    public async Task Session_Export_WritesTheNoteAndItsPictures_AndASecondExportTakesDash2()
    {
        _settings.Update(d => d.TtsOutput = false);
        long id = SeedPictureSession();
        StepsWhenIdle(Line("/sessions export " + id), Line("/sessions export #" + id + " md"), Line("/exit"));

        string output = await RunAsync();

        string stem = ExportStem(id);
        Assert.Contains(SessionText.ExportedNotice(id, stem + ".md"), output);
        Assert.Contains(SessionText.ExportedNotice(id, stem + "-2.md"), output);
        Assert.True(File.Exists(Path.Combine(ExportsFolder, stem + "-pictures", "turn-1-1.png")));
        Assert.True(File.Exists(Path.Combine(ExportsFolder, stem + "-2-pictures", "turn-2-1.png")));
        Assert.Contains("title: look", File.ReadAllText(Path.Combine(ExportsFolder, stem + ".md")));
    }

    [Fact]
    public async Task Session_ExportHtml_OfThisConversation_IsOnePage()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("Hello.");
        StepsWhenIdle(Line("hi"), Line("/sessions export html"), Line("/exit"));

        string output = await RunAsync();

        string stem = ExportStem(1);
        Assert.Contains(SessionText.ExportedNotice(1, stem + ".html"), output);
        Assert.Contains("<p>Hello.</p>", File.ReadAllText(Path.Combine(ExportsFolder, stem + ".html")));
        Assert.Single(Directory.EnumerateFileSystemEntries(ExportsFolder));
    }

    [Fact]
    public async Task Session_Export_OfAMissingId_OrABadWord_WritesNothing()
    {
        _settings.Update(d => d.TtsOutput = false);
        StepsWhenIdle(Line("/sessions export 99"), Line("/sessions export 99 pdf"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(ChatScreen.SessionMissingError(99), output);
        Assert.Contains(ChatScreen.SessionUsageError, output);
        Assert.False(Directory.Exists(ExportsFolder) && Directory.EnumerateFileSystemEntries(ExportsFolder).Any());
    }

    [Fact]
    public async Task Session_Export_BeforeTheFirstTurn_SaysNoSessionYet()
    {
        _settings.Update(d => d.TtsOutput = false);
        StepsWhenIdle(Line("/sessions export"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(ChatScreen.SessionNoneYetNotice, output);
    }

    /// <summary>With <c>Session logging</c> off nothing on screen is saved, so a bare export says so; a stored one by its id still exports.</summary>
    [Fact]
    public async Task Session_Export_WithLoggingOff_SaysThisOneIsntSaved_AndAnIdStillWorks()
    {
        _settings.Update(d => { d.TtsOutput = false; d.SessionLogging = false; });
        long id = SeedSession();
        _chat.EnqueueText("Hello.");
        StepsWhenIdle(Line("hi"), Line("/sessions export"), Line("/sessions export " + id), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(SessionText.NotSavedNotice, output);
        Assert.Contains(SessionText.ExportedNotice(id, ExportStem(id) + ".md"), output);
    }

    /// <summary>
    /// The working folder moved after the export began (2026-10-30, review F8): the note, its pictures and the notice's path all stay in the
    /// folder the export started in.
    /// </summary>
    [Fact]
    public async Task Session_Export_TheFolderMovedMidExport_KeepsTheNoteAndItsPicturesTogether()
    {
        _settings.Update(d => d.TtsOutput = false);
        long id = SeedPictureSession();
        string stem = ExportStem(id);
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        _sessionLoad = loaded =>
        {
            _settings.Update(d => d.WorkingDirectory = elsewhere);
            using var store = OpenSessions();
            return store.Load(loaded);
        };
        StepsWhenIdle(Line("/sessions export " + id), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(SessionText.ExportedNotice(id, stem + ".md"), output);
        Assert.True(File.Exists(Path.Combine(ExportsFolder, stem + ".md")));
        Assert.True(File.Exists(Path.Combine(ExportsFolder, stem + "-pictures", "turn-1-1.png")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    /// <summary>
    /// An export that throws is an error line on the slash form and on the Sessions pane alike (2026-10-30, review F10/F11: the pane awaited
    /// the export bare), logged, and the pane stays open.
    /// </summary>
    [Fact]
    public async Task Session_Export_ALoadThatThrows_IsAnError_OnTheSlashFormAndThePane()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        long id = SeedSession();
        _sessionLoad = _ => throw new InvalidOperationException("the disk went away");
        StepsWhenIdle(
            Line("/sessions export " + id),
            Line("/sessions"), Key(Keys.Enter), Key(Keys.Down), Key(Keys.Down), Key(Keys.Enter), Key(Keys.Enter),   // the row page's export, Markdown
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(2, output.Split("  ✗ " + SessionText.ExportFailedError(id)).Length - 1);
        Assert.False(Directory.Exists(ExportsFolder) && Directory.EnumerateFileSystemEntries(ExportsFolder).Any());
    }
}
