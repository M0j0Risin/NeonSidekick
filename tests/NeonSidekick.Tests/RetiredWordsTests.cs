using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.UI;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// The setting words retired on 2026-10-13 (the settings verbiage pass, the user's call): each picker's old word still parses to
/// what it meant, a profile file holding old words loads with the new ones, and the new words are what the menus offer.
/// </summary>
public class RetiredWordsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EveryOldWord_ParsesAsItsSuccessor()
    {
        Assert.True(NetworkMode.TryParse("local_area_network", out var reach) && reach == NetworkReach.LocalAreaNetwork);
        Assert.True(BrowserMode.TryParse("default", out var e1) && e1 == FetchEngine.Default);
        Assert.True(BrowserMode.TryParse("httpclient", out var e2) && e2 == FetchEngine.HttpClient);
        Assert.True(BrowserMode.TryParse("chromium", out var e3) && e3 == FetchEngine.Chromium);
        Assert.True(BotcheckWindow.TryParse("disabled", out var b) && b == BotcheckKind.Disabled);
        Assert.True(LlmScanMode.TryParse("remote", out var s1) && s1 == ScanScope.Remote);
        Assert.True(LlmScanMode.TryParse("disabled", out var s2) && s2 == ScanScope.Disabled);
        Assert.True(ToolCompactType.TryParse("nothing", out var t) && t == ToolCompactMode.Nothing);
        Assert.True(FileBrowserMode.TryParse("default", out var f) && f == FileBrowserVisibility.Default);
        Assert.True(MentionFolderMode.TryParse("folder-apply", out var m1) && m1 == MentionFolderAction.Apply);
        Assert.True(MentionFolderMode.TryParse("folder-remain", out var m2) && m2 == MentionFolderAction.Remain);
        Assert.True(ReflectionConfirmMode.TryParse("confirm-none", out var r1) && r1 == ReflectionConfirmScope.None);
        Assert.True(ReflectionConfirmMode.TryParse("confirm-all", out var r2) && r2 == ReflectionConfirmScope.All);
        Assert.True(ReflectionConfirmMode.TryParse("confirm-new", out var r3) && r3 == ReflectionConfirmScope.New);
        Assert.True(ReflectionConfirmMode.TryParse("confirm-edit", out var r4) && r4 == ReflectionConfirmScope.Edit);
        Assert.True(SessionShowName.TryParse("all-names", out var n) && n == SessionNameDisplay.AllNames);
        Assert.True(MenuHeight.TryParse("half-screen", out var h1) && h1 == MenuHeightStyle.Half);
        Assert.True(MenuHeight.TryParse("full-screen", out var h2) && h2 == MenuHeightStyle.Full);
        Assert.True(SplashMode.TryParse("disabled", out var sp) && sp == SplashStyle.Disabled);
        Assert.True(MemoryMode.TryParse("disabled", out var mem) && mem == MemoryAccess.Disabled);
        Assert.True(QueueMode.TryParse(" Disabled ", out var q) && q == Queueing.Disabled);   // trimmed, any case, as before
        Assert.True(BotChatImageMentionMode.TryParse("disabled", out var im) && im == BotImageMention.Disabled);
        Assert.True(CameraPreviewMode.TryParse("disabled", out var cp) && cp == CameraPreview.Disabled);
    }

    [Fact]
    public void TheNewWords_AreTheOnesOffered()
    {
        Assert.Equal(["internet", "lan", "both"], NetworkMode.Names);
        Assert.Equal(["auto", "http", "browser"], BrowserMode.Names);
        Assert.Equal(["ask", "automatic", "off"], BotcheckWindow.Names);
        Assert.Equal(["local", "lan", "both", "off"], LlmScanMode.Names);
        Assert.Equal(["compact", "prune", "stop", "off"], ToolCompactType.Names);
        Assert.Equal(["hide-hidden", "show-hidden"], FileBrowserMode.Names);
        Assert.Equal(["apply", "remain"], MentionFolderMode.Names);
        Assert.Equal(["none", "all", "new", "edit"], ReflectionConfirmMode.Names);
        Assert.Equal(["all", "model-written", "none"], SessionShowName.Names);
        Assert.Equal(["half", "three-quarters", "full"], MenuHeight.Names);
        Assert.Equal(["image", "robot", "off"], SplashMode.Names);
        Assert.Equal(["read-write", "read-only", "off"], MemoryMode.Names);
        Assert.Equal(["automatic", "manual", "off"], QueueMode.Names);
        Assert.Equal(["off", "generate"], BotChatImageMentionMode.Names);
        Assert.Equal(["live", "post", "off"], CameraPreviewMode.Names);
    }

    [Fact]
    public void AProfileFileWithOldWords_LoadsWithTheNewOnes()
    {
        string path = Profiles.ProfileFile(_dir, Profiles.DefaultName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "WebBrowserNetworkMode": "local_area_network",
              "WebBrowserMode": "chromium",
              "WebBrowserBotcheckWindow": "disabled",
              "LlmScanMode": "remote",
              "LlmToolCompactType": "nothing",
              "FileBrowserMode": "default",
              "FileMentionFolderMode": "folder-apply",
              "ReflectionConfirmMode": "confirm-edit",
              "SessionShowName": "all-names",
              "MenuMaxHeight": "half-screen",
              "WelcomeSplashMode": "disabled",
              "MemoryMode": "Disabled",
              "QueueMode": "disabled",
              "BotChatImageMentionMode": "disabled",
              "CameraPreview": "disabled"
            }
            """);

        using var settings = new AppSettings(_dir);
        var d = settings.Current;
        Assert.Equal(
            ["lan", "browser", "off", "lan", "off", "hide-hidden", "apply", "edit", "all", "half", "off", "off", "off", "off", "off"],
            [d.WebBrowserNetworkMode, d.WebBrowserMode, d.WebBrowserBotcheckWindow, d.LlmScanMode, d.LlmToolCompactType, d.FileBrowserMode,
             d.FileMentionFolderMode, d.ReflectionConfirmMode, d.SessionShowName, d.MenuMaxHeight, d.WelcomeSplashMode, d.MemoryMode,
             d.QueueMode, d.BotChatImageMentionMode, d.CameraPreview]);

        // Another profile's file read for a copy (/profile push|pull, botchat, advisors) reads the same way.
        Assert.Equal("lan", Profiles.ReadProfileFile(path).WebBrowserNetworkMode);
    }

    [Theory]
    [InlineData("fullsize")]
    [InlineData("tiled")]
    [InlineData("Tiled")]
    public void TheSplashsOldPictureWords_LoadAsImage(string word)
    {
        // 2026-10-20 (the user's call): tiled retired, fullsize renamed image; a profile saved before reads as image.
        var data = new AppSettingsData { WelcomeSplashMode = word };
        RetiredWords.Upgrade(data);
        Assert.Equal("image", data.WelcomeSplashMode);
    }

    [Fact]
    public void AnUnknownWord_IsLeftAsItIs()
    {
        var data = new AppSettingsData { QueueMode = "sometimes", MemoryMode = "read-only" };
        RetiredWords.Upgrade(data);
        Assert.Equal("sometimes", data.QueueMode);   // Resolve warns and uses the default, as before
        Assert.Equal("read-only", data.MemoryMode);
    }
}
