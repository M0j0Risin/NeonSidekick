using NeonSidekick.App;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/settings export|import</c>'s pure parts (2026-11-13, the settings-transfer review's #99: they sat in the screen's partial): the parser,
/// the mid-reply classes, the Web UI's refusal, the start flag, the Downloads folder and a Mac key read on Windows.
/// </summary>
public sealed class SettingsTransferTests
{
    [Theory]
    [InlineData("export", "export", "")]
    [InlineData("EXPORT D:\\My Stuff\\x.zip", "export", "D:\\My Stuff\\x.zip")]
    [InlineData("import", "import", "")]
    [InlineData("import  \"a b.zip\"", "import", "\"a b.zip\"")]
    public void Parse_ReadsTheFirstWord(string args, string verb, string path) =>
        Assert.Equal((verb, path), SettingsTransferCommand.Parse(args));

    [Theory]
    [InlineData("")]
    [InlineData("changed")]
    [InlineData("comfy workflow")]
    [InlineData("exported")]
    public void Parse_LeavesOtherWordsASearch(string args) => Assert.Null(SettingsTransferCommand.Parse(args));

    [Fact]
    public void SettingsTransfer_WaitsForTheReply_ABareImportListsAtOnce_AndOtherSettingsAreThePane()
    {
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Settings, "export"));
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Settings, "import x.zip"));
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.Settings, "import"));   // the review's #94: a list prints at once
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Settings, "changed"));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Settings, ""));
    }

    [Fact]
    public void WebUi_RefusesSettingsTransfer_AndStillPanesTheRest()
    {
        var on = new WebCommandSettings(Commands: true, FileTools: true, ObsidianTools: true);
        var export = WebCommands.Check("/settings export", on);
        Assert.Equal(WebCommandKind.Refused, export.Kind);
        Assert.Equal(WebUi.WebUiText.PcOnly("/settings export"), export.Reason);
        Assert.Equal(WebUi.WebUiText.PcOnly("/settings import"), WebCommands.Check("/settings import x.zip", on).Reason);
        Assert.NotEqual(WebUi.WebUiText.PcOnly("/settings changed"), WebCommands.Check("/settings changed", on).Reason);
    }

    [Fact]
    public void ImportSettingsFlag_Parses_BothForms_AndNeedsAValue()
    {
        Assert.Equal("a.zip", SidekickOptions.Parse(["--import-settings", "a.zip"]).ImportSettings);
        Assert.Equal("b.zip", SidekickOptions.Parse(["--import-settings=b.zip"]).ImportSettings);
        Assert.NotNull(SidekickOptions.Parse(["--import-settings"]).Error);
        var options = SidekickOptions.Parse(["--import-settings", "a.zip"]);
        Assert.Contains(SidekickOptions.ImportSettingsFlag, options.ActiveFlags());
        Assert.Contains("--import-settings a.zip", options.Describe());
    }

    [Fact]
    public void DownloadsFolder_PicksTheKnownFolder_ElseTheProfilesDownloads()
    {
        Assert.Equal(Path.Combine("home", "Downloads"), Files.DownloadsFolder.Pick(null, "home"));
        Assert.Equal(Path.Combine("home", "Downloads"), Files.DownloadsFolder.Pick(" ", "home"));
        Assert.Equal("D:\\Dl", Files.DownloadsFolder.Pick("D:\\Dl", "home"));
    }

    [WindowsFact]
    public void DownloadsFolder_KnownFolder_IsAFullPath()
    {
        Assert.True(Path.IsPathFullyQualified(Files.DownloadsFolder.KnownFolder()!));
    }

    [WindowsFact]
    public void AMacKeychainValue_ReadOnWindows_SaysWhereItCameFrom()
    {
        var result = Sql.WindowsCredentials.Unprotect("keychain:AAAA");
        Assert.Equal(Sql.SqlText.CannotDecrypt("it was encrypted on a Mac"), result.Error);
    }
}
