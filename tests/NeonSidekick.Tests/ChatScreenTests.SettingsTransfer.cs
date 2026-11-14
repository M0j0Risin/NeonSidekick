using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/settings export</c> and <c>/settings import</c> on the screen (2026-11-13, plan parsed-yawning-papert): the verbs, the report, the deferral and completion.</summary>
public partial class ChatScreenTests
{
    /// <summary>The Downloads folder the screen sees: a temp folder beside the test's home (an export inside the home is refused), never the real one.</summary>
    private string DownloadsForTests => _dir + "-downloads";

    private IReadOnlyList<SettingsReportLine> _startNotices = [];

    private ImportEnvironment? _importEnvironment = SettingsArchiveTests.Fake();

    [Fact]
    public async Task SettingsExport_WritesTheZipToDownloads_AndSaysWhere()
    {
        _settings.Update(d => d.TtsOutput = false);
        File.WriteAllText(Path.Combine(_settings.ProfileDirectory, "persona.md"), "be kind");
        StepsWhenIdle(Line("/settings export"), Line("/exit"));

        string output = await RunAsync();

        string zip = Directory.EnumerateFiles(DownloadsForTests, "*.zip").Single();
        Assert.StartsWith(SettingsTransferText.FilePrefix, Path.GetFileName(zip), StringComparison.Ordinal);
        Assert.Contains("Settings exported to", output);
        Assert.Contains("keep it private", output);
    }

    [Fact]
    public async Task SettingsImport_Bare_ListsTheZips_AndImportsNothing()
    {
        _settings.Update(d => d.TtsOutput = false);
        StepsWhenIdle(Line("/settings import"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("No settings zips", output);
        Assert.Equal(["neon"], Directory.EnumerateDirectories(Profiles.Root(_dir)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task SettingsImport_AddsTheProfile_AndAMissingZipIsAnError()
    {
        _settings.Update(d => d.TtsOutput = false);
        string other = Path.Combine(_dir, "other-home");
        Directory.CreateDirectory(Profiles.Directory(other, "larry"));
        File.WriteAllText(Path.Combine(Profiles.Directory(other, "larry"), "persona.md"), "arr");
        Directory.CreateDirectory(DownloadsForTests);
        Assert.Null(SettingsArchive.ExportToFile(other, "larry.zip", DownloadsForTests, "1.0.0", DateTimeOffset.Now, CancellationToken.None).Error);
        StepsWhenIdle(Line("/settings import larry.zip"), Line("/settings import nope.zip"), Line("/exit"));

        string output = await RunAsync();

        Assert.Equal("arr", File.ReadAllText(Path.Combine(Profiles.Directory(_dir, "larry"), "persona.md")));
        Assert.Contains("Added profile: larry", output);
        Assert.Contains("There's no file at", output);
    }

    [Fact]
    public async Task StartNotices_AreShownAfterTheFirstScreen()
    {
        _settings.Update(d => d.TtsOutput = false);
        _startNotices = [new("Added profile: larry. Switch with /profile <name>."), new("Reset for this computer: x.", Warn: true)];
        StepsWhenIdle(Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("Added profile: larry", output);
        Assert.Contains("Reset for this computer", output);
    }

    [Fact]
    public void ArgumentItems_Settings_OffersTheVerbs_AndTheZipsAfterImport()
    {
        var sources = Sources() with { SettingsZips = () => ["neonsidekick-settings-2.zip", "neonsidekick-settings-1.zip"] };

        Assert.Equal([SettingsMenu.ChangedWord, SettingsTransferText.ExportWord, SettingsTransferText.ImportWord], Texts(ChatScreen.ArgumentItems("/settings", "", sources)));
        Assert.Equal(["import neonsidekick-settings-2.zip", "import neonsidekick-settings-1.zip"], Texts(ChatScreen.ArgumentItems("/settings", "import ", sources)));
    }

    /// <summary>The zip is named from the app's clock (the review's #30), not the machine's.</summary>
    [Fact]
    public async Task SettingsExport_NamesTheZip_FromTheAppsClock()
    {
        _settings.Update(d => d.TtsOutput = false);
        _time.Advance(TimeSpan.FromDays(400));
        StepsWhenIdle(Line("/settings export"), Line("/exit"));

        await RunAsync();

        Assert.Equal(SettingsTransferText.DefaultFileName(_time.GetLocalNow().DateTime), Path.GetFileName(Directory.EnumerateFiles(DownloadsForTests, "*.zip").Single()));
    }

    /// <summary>A home command the import adds runs at once (the review's #47: the catalog was cached until a restart).</summary>
    [Fact]
    public async Task SettingsImport_AHomeCommand_RunsAtOnce()
    {
        _settings.Update(d => d.TtsOutput = false);
        string other = Path.Combine(_dir, "other-home");
        Directory.CreateDirectory(Path.Combine(other, "commands"));
        File.WriteAllText(Path.Combine(other, "commands", "hello.md"), "Say hello to $0.");
        Directory.CreateDirectory(DownloadsForTests);
        Assert.Null(SettingsArchive.ExportToFile(other, "cmds.zip", DownloadsForTests, "1.0.0", DateTimeOffset.Now, CancellationToken.None).Error);
        _chat.EnqueueText("Hello!");
        StepsWhenIdle(Line("/hello world"), Line("/settings import cmds.zip"), Line("/hello world"), Line("/exit"));

        await RunAsync();

        Assert.Equal("Say hello to world.", UserText(Assert.Single(_chat.Requests)));
    }
}
