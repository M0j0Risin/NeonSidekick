using NeonSidekick.UI;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Screen;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The screen capture's pure parts (2026-10-04): the targets, the aiming, the words, the mode, the crop, the stored stand-in.</summary>
public sealed class ScreenPureTests
{
    private readonly FakeScreenSystem _screen = new();

    [Theory]
    [InlineData("", ScreenTargetKind.Screen, "", 0)]
    [InlineData("  screen ", ScreenTargetKind.Screen, "", 0)]
    [InlineData("monitor", ScreenTargetKind.Screen, "", 0)]
    [InlineData("ALL", ScreenTargetKind.All, "", 0)]
    [InlineData("desktop", ScreenTargetKind.All, "", 0)]
    [InlineData("behind", ScreenTargetKind.Behind, "", 0)]
    [InlineData("monitor:2", ScreenTargetKind.Monitor, "2", 2)]
    [InlineData("Monitor : 1", ScreenTargetKind.Monitor, "1", 1)]
    [InlineData("monitor 2", ScreenTargetKind.Monitor, "2", 2)]
    [InlineData("screen:2", ScreenTargetKind.Monitor, "2", 2)]
    [InlineData("window:notepad", ScreenTargetKind.Window, "notepad", 0)]
    [InlineData("window Error - Contoso", ScreenTargetKind.Window, "Error - Contoso", 0)]
    [InlineData("window Untitled: Notepad", ScreenTargetKind.Window, "Untitled: Notepad", 0)]
    [InlineData("window:Untitled: Notepad", ScreenTargetKind.Window, "Untitled: Notepad", 0)]
    [InlineData("window : notepad", ScreenTargetKind.Window, "notepad", 0)]
    [InlineData("monitor :2", ScreenTargetKind.Monitor, "2", 2)]
    public void Parse_TakesEveryForm(string text, ScreenTargetKind kind, string argument, int monitor)
    {
        var target = ScreenTarget.Parse(text, out string? error);

        Assert.Null(error);
        Assert.Equal(new ScreenTarget(kind, argument, monitor), target);
    }

    [Fact]
    public void Parse_RefusesTheRest_WithTheSentence()
    {
        Assert.Null(ScreenTarget.Parse("monitor:zero", out string? bad));
        Assert.Equal(ScreenText.BadMonitor("zero"), bad);
        Assert.Null(ScreenTarget.Parse("monitor:0", out bad));
        Assert.Equal(ScreenText.BadMonitor("0"), bad);
        Assert.Null(ScreenTarget.Parse("printer", out bad));
        Assert.Equal(ScreenText.BadTarget("printer"), bad);
        Assert.Null(ScreenTarget.Parse("window:", out bad));
        Assert.Equal(ScreenText.BadTarget("window:"), bad);
        Assert.StartsWith("'printer' is not a screen target. Use screen (the monitor this app is on, the default), all", bad = ScreenText.BadTarget("printer"));
    }

    [Fact]
    public void Resolve_TheDefault_IsTheAppsMonitor_ThenThePrimary()
    {
        var aim = ScreenAiming.Resolve(new ScreenTarget(ScreenTargetKind.Screen), _screen);
        Assert.Equal(new ScreenRect(1920, 0, 2560, 1440), aim.Area);
        Assert.Null(aim.Window);
        Assert.Equal("monitor 2 (2560x1440, this app's)", aim.Described);

        _screen.OwnNumber = null;
        aim = ScreenAiming.Resolve(new ScreenTarget(ScreenTargetKind.Screen), _screen);
        Assert.Equal("monitor 1 (1920x1080, primary)", aim.Described);
    }

    [Fact]
    public void Resolve_All_IsTheUnion_AMonitorByNumber_AndOnePastTheLastIsTheSentence()
    {
        var all = ScreenAiming.Resolve(new ScreenTarget(ScreenTargetKind.All), _screen);
        Assert.Equal(new ScreenRect(0, 0, 4480, 1440), all.Area);
        Assert.Equal("all 2 monitors (4480x1440)", all.Described);

        Assert.Equal("monitor 1 (1920x1080, primary)", ScreenAiming.Resolve(new ScreenTarget(ScreenTargetKind.Monitor, "1", 1), _screen).Described);
        var e = Assert.Throws<ScreenException>(() => ScreenAiming.Resolve(new ScreenTarget(ScreenTargetKind.Monitor, "3", 3), _screen));
        Assert.Equal("There is no monitor 3: this computer has 2 (monitor:1 to monitor:2).", e.Message);
        Assert.Equal("There is no monitor 2: this computer has 1 (monitor:1).", ScreenText.NoSuchMonitor(2, 1));
    }

    [Fact]
    public void Resolve_Behind_IsTheWindowUnderTheAppsOwn_AndNoneIsTheSentence()
    {
        var aim = ScreenAiming.Resolve(new ScreenTarget(ScreenTargetKind.Behind), _screen);
        Assert.Equal(200, aim.Window);
        Assert.Equal("window \"notes.txt - Notepad\" (Notepad)", aim.Described);

        _screen.Own = 300;
        Assert.Equal(ScreenText.NothingBehind, Assert.Throws<ScreenException>(() => ScreenAiming.Resolve(new ScreenTarget(ScreenTargetKind.Behind), _screen)).Message);
        _screen.Own = null;
        Assert.Equal(ScreenText.NothingBehind, Assert.Throws<ScreenException>(() => ScreenAiming.Resolve(new ScreenTarget(ScreenTargetKind.Behind), _screen)).Message);
    }

    [Fact]
    public void FindWindow_ById_ByTitleWords_ByProcess_ExactWins_AndTooManyListsThem()
    {
        var windows = _screen.WindowList;
        Assert.Equal(300, ScreenAiming.FindWindow("300", windows).Id);
        Assert.Equal(200, ScreenAiming.FindWindow("NOTEPAD", windows).Id);
        Assert.Equal(300, ScreenAiming.FindWindow("\"contoso\"", windows).Id);
        Assert.Equal(100, ScreenAiming.FindWindow("windowsterminal", windows).Id);   // no title has it: the process name
        Assert.Equal(ScreenText.NoSuchWindow("excel"), Assert.Throws<ScreenException>(() => ScreenAiming.FindWindow("excel", windows)).Message);

        var many = new List<ScreenWindow>(windows) { new(400, "Neon docs", "browser", new ScreenRect(0, 0, 10, 10)) };
        var e = Assert.Throws<ScreenException>(() => ScreenAiming.FindWindow("neo", many));
        Assert.Equal("2 windows' titles have 'neo'; name one by its id: window:100 \"Neon\" (WindowsTerminal); window:400 \"Neon docs\" (browser).", e.Message);
        Assert.Equal(100, ScreenAiming.FindWindow("neon", many).Id);   // the exact title
        Assert.EndsWith("; and 3 more.", ScreenText.AmbiguousWindow("x", windows.Take(1).ToList(), 4));
    }

    [Fact]
    public void List_NamesEveryTarget_TheAppsOwnMarked_AndCapsTheWindows()
    {
        var lines = ScreenText.List(_screen.Monitors(), _screen.OwnMonitor(), _screen.Windows(), _screen.OwnWindow());

        Assert.Equal(
        [
            "Monitors:",
            "  monitor:1  1920x1080 at 0,0, primary",
            "  monitor:2  2560x1440 at 1920,0, this app's",
            "Windows (front to back):",
            "  window:100  \"Neon\" (WindowsTerminal) 800x600, this app",
            "  window:200  \"notes.txt - Notepad\" (Notepad) 640x480",
            "  window:300  \"Error - Contoso Browser\" (browser) 1200x900",
        ], lines);
        var crowd = Enumerable.Range(1, ScreenText.MaxListed + 2).Select(i => new ScreenWindow(i, "w", "", new ScreenRect(0, 0, 1, 1))).ToList();
        var capped = ScreenText.List([], null, crowd, null);
        Assert.Equal("  and 2 more", capped[^1]);
        Assert.Equal("Windows: none", ScreenText.List([], null, [], null)[1]);
    }

    [Fact]
    public void Words_ArePinned()
    {
        Assert.Equal("Error: screen_capture needs the app's screen to ask the user; there is none here.", ScreenText.NoScreen);
        Assert.Equal("The model asks to see your screen", ScreenText.AllowTitle);
        Assert.Equal("Monitor 1 (1920x1080, primary): To read the error.", ScreenText.AllowCaption("monitor 1 (1920x1080, primary)", "To read the error."));
        Assert.Equal("[screenshot not kept in the session: D:\\w\\screen_images\\a.jpg]", ScreenText.NotKept("D:\\w\\screen_images\\a.jpg"));
        Assert.Equal("🖥️ screen_images/a.jpg is on the input line.", ScreenText.Attached("screen_images/a.jpg"));
        Assert.Equal("ask", ScreenAskMode.Default);
        Assert.Equal(["ask", "allow"], ScreenAskMode.Names);
        Assert.All(ScreenAskMode.Names, n => Assert.NotEmpty(ScreenAskMode.Describe(n)));
        Assert.Equal(ScreenAsk.Allow, ScreenAskMode.Resolve(new AppSettingsData { ScreenAsk = " ALLOW " }));
        Assert.Equal(ScreenAsk.Ask, ScreenAskMode.Resolve(new AppSettingsData { ScreenAsk = "maybe" }));
    }

    [Fact]
    public void Crop_CutsTheFrameOutOfTheWholeWindow_ClampedToIt()
    {
        // A 4x3 picture whose pixel (x, y) has blue = x and green = y.
        var pixels = new byte[4 * 3 * 4];
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                pixels[((y * 4) + x) * 4] = (byte)x;
                pixels[(((y * 4) + x) * 4) + 1] = (byte)y;
            }
        }

        var cut = ScreenPixels.Crop(pixels, 4, new ScreenRect(1, 1, 2, 5));
        Assert.Equal((2, 2), (cut.Width, cut.Height));
        Assert.Equal((1, 1), (cut.Bgrx[0], cut.Bgrx[1]));
        Assert.Equal((2, 2), (cut.Bgrx[12], cut.Bgrx[13]));
        Assert.Same(pixels, ScreenPixels.Crop(pixels, 4, new ScreenRect(0, 0, 4, 3)).Bgrx);
        Assert.Equal(default, ScreenRect.Union([]));
    }

    [Fact]
    public void AScreenshot_IsMarked_AndAStoredSessionKeepsALineNamingIt_UnlessKept()
    {
        var image = ScreenCapture.Attachment(new ScreenFrame(4000, 1000, new byte[4000 * 1000 * 4])) with { Path = "D:\\w\\screen_images\\a.jpg" };
        Assert.True(image.Screen);
        Assert.False(image.Camera);
        Assert.Equal(ImageFile.Jpeg, image.MediaType);
        Assert.Equal((ImageFile.MaxSide, 512), (image.Width, image.Height));   // scaled to fit, never enlarged
        var part = ConversationHistory.ImagePart(image);
        Assert.Equal(image.Path, ConversationHistory.ScreenPath(part));
        Assert.Null(ConversationHistory.CameraPath(part));

        var message = new ChatMessage(ChatRole.User, [new TextContent("look"), part]);
        string stored = SessionHistory.ToJson([message]);
        Assert.Contains("screenshot not kept in the session", stored);
        Assert.DoesNotContain(Convert.ToBase64String(image.Bytes)[..40], stored);
        Assert.DoesNotContain("screenshot not kept", SessionHistory.ToJson([message], keepScreen: true));
        Assert.Throws<ScreenException>(() => ScreenCapture.Attachment(new ScreenFrame(0, 0, [])));
    }

    [Fact]
    public void PlanMode_KeepsBothTools_AsReadOnly()
    {
        Assert.True(PlanTools.Allowed(ScreenCaptureTool.ToolName));
        Assert.True(PlanTools.Allowed(ScreenListTool.ToolName));
    }

    [Fact]
    public void Offered_NeedsTheSetting_AScreen_ThePane_AndEyes()
    {
        var on = new AppSettingsData { ScreenTools = true };
        Assert.True(ChatScreen.ScreenOffered(on, available: true, pane: true, blind: false));
        Assert.False(ChatScreen.ScreenOffered(new AppSettingsData(), true, true, false));
        Assert.False(ChatScreen.ScreenOffered(on, false, true, false));
        Assert.False(ChatScreen.ScreenOffered(on, true, false, false));
        Assert.False(ChatScreen.ScreenOffered(on, true, true, true));
    }

    // ── /screen's argument list (2026-10-04) ───────────────────────────────

    private static readonly ScreenMonitor[] TwoMonitors =
    [
        new(1, "D1", new ScreenRect(0, 0, 1920, 1080), true),
        new(2, "D2", new ScreenRect(1920, 0, 2560, 1440), false),
    ];

    private static readonly ScreenWindow[] ThreeWindows =
    [
        new(100, "Neon", "WindowsTerminal", new ScreenRect(0, 0, 10, 10)),
        new(200, "notes.txt - Notepad", "Notepad", new ScreenRect(0, 0, 10, 10)),
        new(2001, "Error - Contoso Browser", "browser", new ScreenRect(0, 0, 10, 10)),
    ];

    [Fact]
    public void Complete_TheWordsFirst_NarrowedAsTyped_AndASpaceEndsIt()
    {
        var words = ScreenTarget.Complete("", TwoMonitors, 2, ThreeWindows, 100);
        Assert.Equal(["list", "screen", "all", "behind", "monitor:", "window:"], words.Select(i => i.Text));
        Assert.Equal("a window by id or title", words[^1].Note);
        Assert.Equal(["window:"], ScreenTarget.Complete("WIN", TwoMonitors, 2, ThreeWindows, 100).Select(i => i.Text));
        Assert.Equal(["screen"], ScreenTarget.Complete("s", TwoMonitors, 2, ThreeWindows, 100).Select(i => i.Text));
        Assert.Empty(ScreenTarget.Complete("all", TwoMonitors, 2, ThreeWindows, 100));        // typed in full
        Assert.Empty(ScreenTarget.Complete("monitor 2", TwoMonitors, 2, ThreeWindows, 100));  // the spaced form is typed as it is
        Assert.Empty(ScreenTarget.Complete("window no", TwoMonitors, 2, ThreeWindows, 100));
    }

    [Fact]
    public void Complete_AfterMonitor_TheMonitorsWithTheirSize()
    {
        var monitors = ScreenTarget.Complete("monitor:", TwoMonitors, 2, ThreeWindows, 100);
        Assert.Equal([new("monitor:1", "1920x1080, primary"), new CompletionItem("monitor:2", "2560x1440, this app's")], monitors);
        Assert.Equal(["monitor:2"], ScreenTarget.Complete("MONITOR:", [TwoMonitors[1]], null, [], null).Select(i => i.Text));   // any case
        Assert.Empty(ScreenTarget.Complete("Monitor:2", TwoMonitors, null, [], null));                                        // typed in full
        Assert.Empty(ScreenTarget.Complete("monitor:", [], null, [], null));
    }

    [Fact]
    public void Complete_AfterWindow_TheWindowsFrontToBack_TheAppsOwnLeftOut_ByIdOrTitleOrProcess()
    {
        var all = ScreenTarget.Complete("window:", TwoMonitors, 2, ThreeWindows, 100);
        Assert.Equal([new("window:200", "\"notes.txt - Notepad\" (Notepad)"), new CompletionItem("window:2001", "\"Error - Contoso Browser\" (browser)")], all);

        Assert.Equal(["window:200", "window:2001"], ScreenTarget.Complete("window:20", TwoMonitors, 2, ThreeWindows, 100).Select(i => i.Text));  // the id's start
        Assert.Equal(["window:200"], ScreenTarget.Complete("window:NOTE", TwoMonitors, 2, ThreeWindows, 100).Select(i => i.Text));             // the title, any case
        Assert.Equal(["window:2001"], ScreenTarget.Complete("window:browser", TwoMonitors, 2, ThreeWindows, 100).Select(i => i.Text));         // the process
        Assert.Empty(ScreenTarget.Complete("window:200", TwoMonitors, 2, ThreeWindows, 100));                                                // typed in full
        Assert.Equal(["window:100", "window:200", "window:2001"], ScreenTarget.Complete("window:", [], null, ThreeWindows, null).Select(i => i.Text));  // no own window known
    }

    [Fact]
    public void Complete_AfterWindow_AtMostTheListsCap()
    {
        var many = Enumerable.Range(1, ScreenText.MaxListed + 5).Select(i => new ScreenWindow(i, "w" + i, "p", new ScreenRect(0, 0, 1, 1))).ToList();
        Assert.Equal(ScreenText.MaxListed, ScreenTarget.Complete("window:", [], null, many, null).Count);
    }
}

/// <summary>Screenshots taken and saved, and the two tools, over the fake (2026-10-04).</summary>
public sealed class ScreenCaptureTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeScreenSystem _screen = new();
    private readonly AppSettingsData _settings = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-screen-").FullName;
    private readonly WorkingDirectory _files;

    public ScreenCaptureTests()
    {
        _files = new WorkingDirectory(() => _dir, _time);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private ScreenCapture Capture() => new(_screen, () => _files, () => _settings.ScreenOutputFolder, _time);

    [Fact]
    public async Task AShot_IsSavedUnderTheOutputFolder_StampedLocally_AndAClashNumbered()
    {
        var capture = Capture();
        var aim = await capture.AimAsync(new ScreenTarget(ScreenTargetKind.Window, "notepad"), CancellationToken.None);

        var first = await capture.TakeAsync(aim, CancellationToken.None);
        var second = await capture.TakeAsync(aim, CancellationToken.None);

        string stem = CameraCapture.Stem(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone));
        Assert.Equal(Path.Combine("screen_images", stem + ".jpg"), first.RelativePath);
        Assert.Equal(Path.Combine("screen_images", stem + "-2.jpg"), second.RelativePath);
        Assert.Equal(first.FullPath, first.Image.Path);
        Assert.True(first.Image.Screen);
        Assert.Equal((640, 480), (first.Image.Width, first.Image.Height));
        Assert.Equal(first.Image.Bytes, await File.ReadAllBytesAsync(first.FullPath));
        Assert.Equal(["window 200", "window 200"], _screen.Captures);
        Assert.Equal($"screenshot of window \"notes.txt - Notepad\" (Notepad) as 640x480, saved as {first.RelativePath}: the picture is in the next message", ScreenText.Taken(first));
        Assert.Same(_screen, capture.Screen);

        _settings.ScreenOutputFolder = "";
        var here = await capture.TakeAsync(await capture.AimAsync(new ScreenTarget(ScreenTargetKind.Monitor, "1", 1), CancellationToken.None), CancellationToken.None);
        Assert.Equal(_dir, Path.GetDirectoryName(here.FullPath));
        Assert.Equal("area 0,0 1920x1080", _screen.Captures[^1]);
    }

    [Fact]
    public async Task AFailure_IsTheSentence_AndASaveOutsideTheSandboxIsRefused()
    {
        var capture = Capture();
        var aim = await capture.AimAsync(new ScreenTarget(ScreenTargetKind.Screen), CancellationToken.None);
        _screen.Fail = ScreenText.WindowMinimized;
        Assert.Equal(ScreenText.WindowMinimized, (await Assert.ThrowsAsync<ScreenException>(() => capture.TakeAsync(aim, CancellationToken.None))).Message);

        _settings.ScreenOutputFolder = "../outside";
        var e = await Assert.ThrowsAsync<ScreenException>(() => capture.TakeAsync(aim, CancellationToken.None));
        Assert.StartsWith("The screenshot could not be saved: ", e.Message);
    }

    [Fact]
    public async Task TheTool_AnswersEachOutcome_AndReadsItsArguments()
    {
        string? target = null;
        string? prompt = null;
        ScreenAnswer next = new ScreenAnswer.Denied();
        var tool = new ScreenCaptureTool((t, p, _) =>
        {
            (target, prompt) = (t, p);
            return Task.FromResult(next);
        });

        Assert.Equal("screen_capture", tool.Name);
        Assert.Equal(ScreenText.Denied, await tool.InvokeAsync(new AIFunctionArguments { ["window"] = " notepad ", ["reason"] = "Read\nit." }));
        Assert.Equal(("notepad", "Read it."), (target, prompt));
        next = new ScreenAnswer.AlreadyDeclined();
        Assert.Equal(ScreenText.AlreadyDeclined, await tool.InvokeAsync(new AIFunctionArguments()));
        Assert.Equal(("", ScreenText.DefaultPrompt), (target, prompt));
        next = new ScreenAnswer.Failed("nope");
        Assert.Equal("Error: nope", await tool.InvokeAsync(new AIFunctionArguments()));
        next = new ScreenAnswer.NoScreen();
        Assert.Equal(ScreenText.NoScreen, await tool.InvokeAsync(new AIFunctionArguments()));
        Assert.Equal(ScreenText.NoScreen, await new ScreenCaptureTool(null).InvokeAsync(new AIFunctionArguments()));

        var shot = await Capture().TakeAsync(new ScreenAim(new ScreenRect(0, 0, 10, 10), null, "monitor 1"), CancellationToken.None);
        next = new ScreenAnswer.Shot(shot);
        var result = Assert.IsType<ToolImageResult>(await tool.InvokeAsync(new AIFunctionArguments()));
        Assert.Equal(ScreenText.Taken(shot), result.Text);
        Assert.Same(shot.Image, Assert.Single(result.Images));
    }

    [Fact]
    public async Task TheListTool_ListsTheTargets_OrSaysThereIsNoScreen()
    {
        string listed = (string)(await new ScreenListTool(_screen).InvokeAsync(new AIFunctionArguments()))!;
        Assert.StartsWith("Monitors:\n  monitor:1  1920x1080 at 0,0, primary\n", listed);
        Assert.Contains("window:300  \"Error - Contoso Browser\" (browser) 1200x900", listed);
        Assert.Equal("Error: " + ScreenText.Unsupported, await new ScreenListTool(null).InvokeAsync(new AIFunctionArguments()));
        Assert.Equal("screen_list", new ScreenListTool(null).Name);
    }

    [Fact]
    public async Task ARefusal_StopsTheAim_AsksTheSystem_AndCapturesNothing()
    {
        _screen.Refused = ScreenText.NoPermission("Terminal");
        var capture = Capture();

        var e = await Assert.ThrowsAsync<ScreenException>(() => capture.AimAsync(new ScreenTarget(ScreenTargetKind.Screen), CancellationToken.None));

        Assert.Equal(ScreenText.NoPermission("Terminal"), e.Message);
        Assert.Equal([true], _screen.Asked);
        Assert.Empty(_screen.Captures);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task TheListing_WhileRefused_IsTheMonitorsAndTheRefusal_WithoutAsking()
    {
        Assert.Equal(ScreenText.List(_screen.Monitors(), 2, _screen.Windows(), 100), ScreenCapture.Listing(_screen));
        _screen.Refused = ScreenText.NoPermission(null);

        string listed = (string)(await new ScreenListTool(_screen).InvokeAsync(new AIFunctionArguments()))!;

        Assert.Equal(
            "Monitors:\n  monitor:1  1920x1080 at 0,0, primary\n  monitor:2  2560x1440 at 1920,0, this app's\n" +
            "Windows: not listed. Screen Recording is off for your terminal app, so macOS would show only the wallpaper; nothing was captured. " +
            "Turn your terminal app on in System Settings › Privacy & Security › Screen & System Audio Recording, then quit and reopen your terminal app.",
            listed);
        Assert.Equal([false, false], _screen.Asked);
    }

    [Fact]
    public void TheMacSentences_ArePinned_AndWindowsKeepsItsOwn()
    {
        Assert.Equal(
            "Screen Recording is off for iTerm2, so macOS would show only the wallpaper; nothing was captured. " +
            "Turn iTerm2 on in System Settings › Privacy & Security › Screen & System Audio Recording, then quit and reopen iTerm2.",
            ScreenText.NoPermission("iTerm2"));
        Assert.Equal(
            OperatingSystem.IsMacOS() ? "There is no screen capture on this Mac: it needs macOS 14 or later." : "There is no screen capture on this system (Windows only).",
            ScreenText.Unsupported);
    }

    [Fact]
    public void Jpeg_FromBareRows_MatchesTheFrameOverload()
    {
        var pixels = new byte[8 * 6 * 4];
        var frame = new CameraFrame(8, 6, pixels, 1, DateTimeOffset.UnixEpoch);
        Assert.Equal(CameraJpeg.Encode(frame), CameraJpeg.Encode(8, 6, pixels));
        Assert.Throws<ArgumentException>(() => CameraJpeg.Encode(8, 6, new byte[4]));
    }

}
