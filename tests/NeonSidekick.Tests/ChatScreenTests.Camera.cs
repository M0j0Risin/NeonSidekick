using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>The camera on the screen (2026-10-02): <c>/camera</c>, the model's <c>camera_capture</c> under each shutter, the offer.</summary>
public partial class ChatScreenTests
{
    /// <summary>The fake camera the screen gets; null (the default) is a screen with no camera layer.</summary>
    private FakeCameraSystem? _cameraSystem;

    /// <summary>The shots the <c>post</c> preview opened the viewer on.</summary>
    private readonly List<string> _shotsShown = [];

    /// <summary>The camera's live window; null (the default) is a screen with no viewer, as off Windows.</summary>
    private Func<string, Action, ILiveView>? _liveView;

    /// <summary>A live window that remembers being let go.</summary>
    private sealed class FakeLiveView : ILiveView
    {
        private volatile bool _disposed;

        public bool Disposed => _disposed;

        public bool Open => !_disposed;

        public byte[] Rent(int length) => new byte[length];

        public void Post(ViewerBitmap frame)
        {
        }

        public void Freeze(ViewerBitmap shot, string title)
        {
        }

        public void Resume(string title)
        {
        }

        public void Dispose() => _disposed = true;
    }

    [Fact]
    public async Task CtrlAltV_ClosesTheWindowCameraLiveOpened_AndOpensItAgain_TheTypedCommandOnlyOpens()
    {
        // Later on 2026-10-02 (the user's ask): Ctrl+Alt+V toggles /camera live's window; the typed /camera live only says it is live.
        _cameraSystem = new FakeCameraSystem();
        _settings.Update(d => d.TtsOutput = false);
        var views = new List<FakeLiveView>();
        _liveView = (_, _) =>
        {
            var view = new FakeLiveView();
            lock (views)
            {
                views.Add(view);
            }

            return view;
        };
        StepsWhenIdle(
            Key(Keys.CtrlAlt(ConsoleKey.V)),   // live
            Key(Keys.CtrlAlt(ConsoleKey.V)),   // closed
            Key(Keys.CtrlAlt(ConsoleKey.V)),   // live again
            Line("/camera live"),              // still live, no new window
            Line("/camera off"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(2, views.Count);
        Assert.All(views, v => Assert.True(v.Disposed));
        Assert.Contains(CameraText.LiveOff, output);
        Assert.Contains(CameraText.Off(1), output);
    }

    private string CameraFolder => Path.Combine(WorkingDirectory.Resolve("", _settings.ProfileDirectory), _settings.Current.CameraOutputFolder);

    private string[] CameraFiles() => Directory.Exists(CameraFolder) ? Directory.GetFiles(CameraFolder) : [];

    private void CameraPane()
    {
        _cameraSystem = new FakeCameraSystem();
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
    }

    /// <summary>A model reply that is a <c>camera_capture</c> call, then <paramref name="reply"/>; <paramref name="answer"/> pushed at the pane's own wait.</summary>
    private void CameraToolFixture(ConsoleKeyInfo[] answer, string reply, int calls = 1)
    {
        CameraPane();
        _settings.Update(d => d.CameraTools = true);
        for (int i = 1; i <= calls; i++)
        {
            _chat.Enqueue(FakeChatClient.Call("c" + i, CameraCaptureTool.ToolName, new Dictionary<string, object?> { [CameraCaptureTool.PromptArgument] = "Show me the label." }));
        }

        _chat.EnqueueText(reply);
        var input = Scripted();
        StepsWhenIdle(Line("what is this?"), Line("/exit"));
        var idle = input.OnWait!;
        bool answered = false;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                if (!answered)
                {
                    answered = true;
                    input.Push(answer);
                }

                return;
            }

            idle();
        };
    }

    /// <summary>The camera calls' results in a request (the opening calls' left out).</summary>
    private static IEnumerable<FunctionResultContent> Results(IReadOnlyList<ChatMessage> request) =>
        request.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Where(r => r.CallId.StartsWith('c') && r.CallId.Length <= 3);

    [Fact]
    public async Task CameraList_NumbersTheCameras()
    {
        _cameraSystem = new FakeCameraSystem();
        PushLine("/camera list");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(CameraText.ListHeader, output);
        Assert.Contains("  1. Fake Cam  ← chosen", output);
        Assert.Contains("  2. Other Cam", output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task CameraUse_SavesTheCamera_AndAnUnknownOneIsAnError()
    {
        _cameraSystem = new FakeCameraSystem();
        PushLine("/camera use 2");
        PushLine("/camera use Nope");
        PushLine("/camera zoom");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal("Other Cam", _settings.Current.CameraDevice);
        Assert.Contains(CameraText.Using("Other Cam"), output);
        Assert.Contains(CameraText.NoSuchCamera("Nope"), output);
        Assert.Contains(CameraText.Unknown("zoom"), output);
    }

    [WindowsFact]
    public async Task Camera_OnThePane_SpaceTakesIt_EnterPutsItOnTheLine_AndTheMessageCarriesItMarked()
    {
        CameraPane();
        _settings.Update(d => d.CameraPreview = "post");
        PushLine("/camera");
        _console.Input.PushKey(Keys.Char(' '));   // snap
        _console.Input.PushKey(Keys.Enter);       // the first row: on the input line
        PushLine("what is this?");
        PushLine("/exit");
        _chat.EnqueueText("A label.");

        string output = await RunAsync();

        string file = Assert.Single(CameraFiles());
        Assert.Contains("\n" + Titled(CameraText.PaneTitle + " │ " + CameraText.SnapButton + " · " + CameraText.RetakeButton + " "), output);
        Assert.Contains(CameraText.OwnPrompt, output);
        Assert.Contains(CameraText.Attached(Path.Combine(AppSettingsData.DefaultCameraOutputFolder, Path.GetFileName(file))), output);
        Assert.Equal([file], _shotsShown);   // post: the viewer on the shot
        var user = _chat.Requests[0].Last(m => m.Role == ChatRole.User);
        Assert.StartsWith("[Image #1] what is this?", user.Text);
        var picture = Assert.Single(user.Contents.OfType<DataContent>());
        Assert.Equal(file, ConversationHistory.CameraPath(picture));
        Assert.Equal(1, _cameraSystem!.Opens);
    }

    [WindowsFact]
    public async Task CameraSnap_SavesInTheCameraOutputFolder_EvenTheComfyOne()
    {
        _cameraSystem = new FakeCameraSystem();
        _settings.Update(d => d.CameraOutputFolder = d.ComfyOutputFolder);
        PushLine("/camera snap");
        PushLine("/exit");

        string output = await RunAsync();

        string file = Assert.Single(CameraFiles());
        Assert.Equal(Path.Combine(WorkingDirectory.Resolve("", _settings.ProfileDirectory), AppSettingsData.DefaultComfyOutputFolder), Path.GetDirectoryName(file));
        Assert.Contains(CameraText.Attached(Path.Combine(AppSettingsData.DefaultComfyOutputFolder, Path.GetFileName(file))), output);
    }

    [WindowsFact]
    public async Task Camera_ARetakeDeletesTheFirst_AndEscDeletesTheSecond()
    {
        CameraPane();
        PushLine("/camera");
        _console.Input.PushKey(Keys.Char(' '));
        _console.Input.PushKey(Keys.Char('r'));
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(CameraFiles());
        Assert.DoesNotContain("is on the input line", output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Camera_AFailedCamera_IsThePanesError_AndEscReportsIt()
    {
        CameraPane();
        _cameraSystem!.OpenFailure = new CameraException(CameraFailure.InUse, null);
        PushLine("/camera");
        _console.Input.PushKey(Keys.Char(' '));
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(CameraText.Failure(CameraFailure.InUse, null), output);
        Assert.Empty(CameraFiles());
    }

    [WindowsFact]
    public async Task CameraSnap_WithoutThePane_TakesItAtOnce_ToTheLine()
    {
        _cameraSystem = new FakeCameraSystem();
        PushLine("/camera snap");
        PushLine("/exit");

        string output = await RunAsync();

        string file = Assert.Single(CameraFiles());
        Assert.Contains(CameraText.Attached(Path.Combine(AppSettingsData.DefaultCameraOutputFolder, Path.GetFileName(file))), output);
    }

    /// <summary>F9 is <c>/camera snap</c> (2026-10-05, the user's pick): the photo taken and put on the line, as typed.</summary>
    [WindowsFact]
    public async Task F9_SnapsAPhoto_ToTheLine()
    {
        _cameraSystem = new FakeCameraSystem();
        _settings.Update(d => d.TtsOutput = false);
        StepsWhenIdle(
            Key(new ConsoleKeyInfo('\0', ConsoleKey.F9, shift: false, alt: false, control: false)),
            input => input.Push(Keys.Escape),   // the photo's draft cleared
            Line("/exit"));

        string output = await RunAsync();

        string file = Assert.Single(CameraFiles());
        Assert.Contains(CameraText.Attached(Path.Combine(AppSettingsData.DefaultCameraOutputFolder, Path.GetFileName(file))), output);
    }

    /// <summary>
    /// The Camera tool page's live and snap buttons (2026-10-05, the user's ask), from the toolbar's 📸: L opens the live window
    /// with the page kept, L again closes it, S closes the page and the photo lands on the line.
    /// </summary>
    [WindowsFact]
    public async Task TheCameraToolPage_LiveTogglesTheWindow_SnapClosesThePane_AndSnaps()
    {
        _cameraSystem = new FakeCameraSystem();
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = ["camera"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 200;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the toolbar at 103: 📸 at 0
        var views = new List<FakeLiveView>();
        _liveView = (_, _) =>
        {
            var view = new FakeLiveView();
            lock (views)
            {
                views.Add(view);
            }

            return view;
        };
        StepsWhenIdle(
            input => { input.PushClick(0, 103); input.PushClick(0, 103); },   // 📸: the Camera tool page
            input => input.Push(Keys.Char('l'), Keys.Char('l'), Keys.Char('s')),   // live on, off, then snap
            input => input.Push(Keys.Escape),                                      // the photo's draft cleared
            Line("/exit"));

        string output = await RunAsync();

        var view = Assert.Single(views);
        Assert.True(view.Disposed);
        Assert.Contains(CameraText.LiveOff, output);
        string strip = " │ " + SettingsMenu.CameraWatchButtonTitle + " · " + SettingsMenu.CameraLiveButtonTitle + " · " + SettingsMenu.CameraSnapButtonTitle + " ";
        Assert.Contains("\n" + Titled(ToolsText.Label + " › " + SettingsMenu.FieldName(SettingsField.CameraTools) + strip) + "\n", output);
        string file = Assert.Single(CameraFiles());
        Assert.Contains(CameraText.Attached(Path.Combine(AppSettingsData.DefaultCameraOutputFolder, Path.GetFileName(file))), output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Camera_WithoutALayer_SaysSo()
    {
        PushLine("/camera snap");
        PushLine("/camera list");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(CameraText.Failure(CameraFailure.Unsupported, null), output);
    }

    [WindowsFact]
    public async Task CameraTool_UserShutter_SpaceThenEnter_SendsThePhoto_TheModelSeesIt()
    {
        CameraToolFixture([Keys.Char(' '), Keys.Enter], "It says MILK.");

        string output = await RunAsync();

        Assert.Contains("\n" + Titled(CameraText.ModelPaneTitle + " │ " + CameraText.SnapButton + " · " + CameraText.RetakeButton + " "), output);
        Assert.Contains("Show me the label.", output);
        Assert.Contains("It says MILK.", output);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Contains(CameraCaptureTool.ToolName, _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
        var result = Assert.Single(Results(_chat.Requests[1]));
        Assert.StartsWith("photo taken with Fake Cam (64x48), saved as camera", result.Result as string);
        var carrier = _chat.Requests[1].Last(m => m.Role == ChatRole.User);
        Assert.True(ConversationHistory.IsImageCarrier(carrier));
        Assert.NotNull(ConversationHistory.CameraPath(Assert.Single(carrier.Contents.OfType<DataContent>())));
        Assert.Single(CameraFiles());
    }

    [Fact]
    public async Task CameraTool_UserShutter_Esc_Declines_AndTheTurnsNextCallIsNotAsked()
    {
        CameraToolFixture([Keys.Escape], "Never mind.", calls: 2);

        string output = await RunAsync();

        var results = Results(_chat.Requests[^1]).ToList();
        Assert.Equal([CameraText.Declined, CameraText.AlreadyDeclined], results.Select(r => r.Result as string));
        Assert.Contains("Never mind.", output);
        Assert.Empty(CameraFiles());
    }

    [WindowsFact]
    public async Task CameraTool_ModelShutter_AllowForTheSession_TakesTwoWithoutAskingAgain()
    {
        CameraToolFixture([Keys.Char('s'), Keys.Enter], "Two photos.", calls: 2);
        _settings.Update(d => d.CameraShutter = "model");

        string output = await RunAsync();

        Assert.Contains(CameraText.AllowTitle, output);
        var results = Results(_chat.Requests[^1]).Select(r => r.Result as string).ToList();
        Assert.All(results, r => Assert.StartsWith("photo taken with Fake Cam", r));
        Assert.Equal(2, results.Count);
        Assert.Equal(2, CameraFiles().Length);
    }

    [Fact]
    public async Task CameraTool_ModelShutter_Deny_IsTheModelsAnswer()
    {
        CameraToolFixture([Keys.Enter], "Understood.");
        _settings.Update(d => d.CameraShutter = "model");

        await RunAsync();

        Assert.Equal(CameraText.Denied, Assert.Single(Results(_chat.Requests[^1])).Result);
        Assert.Empty(CameraFiles());
    }

    /// <summary>
    /// Watch mode's script: <c>/camera watch 5</c>, then at the next wait the clock's first tick (the first sample) and
    /// <paramref name="afterSample"/> — what the screen is waiting on then — then <paramref name="rest"/> at the waits after.
    /// </summary>
    private void WatchFixture(Action<ScriptedInput> afterSample, params Action<ScriptedInput>[] rest)
    {
        _cameraSystem = new FakeCameraSystem();
        _settings.Update(d => d.TtsOutput = false);
        var input = Scripted();
        int stage = 0;
        input.OnWait = () =>
        {
            switch (stage++)
            {
                case 0:
                    PushLine(input, "/camera watch 5");
                    break;
                case 1:
                    _time.Advance(TimeSpan.Zero);
                    afterSample(input);
                    break;
                default:
                    if (stage - 3 < rest.Length)
                    {
                        rest[stage - 3](input);
                    }

                    break;
            }
        };
    }

    [WindowsFact]
    public async Task Watch_TheChangedPicture_RidesTheNextMessage_WithItsCaption()
    {
        _chat.EnqueueText("I see you.");
        WatchFixture(input =>
        {
            // The sample runs on the camera's thread: wait for its frame, then a moment for the grid.
            SpinWait.SpinUntil(() => _cameraSystem!.Reads >= 2, 5000);
            Thread.Sleep(200);
            PushLine(input, "what now?");
        }, Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(CameraText.WatchOn(5, 8, false), output);
        var user = _chat.Requests[0].Last(m => m.Role == ChatRole.User);
        Assert.StartsWith("what now?\n\n(Attached: the user's camera at ", user.Text);
        Assert.NotNull(ConversationHistory.CameraPath(Assert.Single(user.Contents.OfType<DataContent>())));
        Assert.Empty(CameraFiles());   // memory only
    }

    private string WatchFolder => Path.Combine(CameraFolder, CameraWatch.FolderName);

    [WindowsFact]
    public async Task Watch_APicturesThumbnail_DoubleClicked_OpensFromCameraWatch_AndWatchOffClearsIt()
    {
        // The fixture's clock names the picture; an older build wrote it to the system temp folder, so a copy there would prove nothing.
        // File.Delete throws on a missing folder, and a fresh machine (the v0.4.1 release run, 2026-10-06) has no temp pictures folder until a test makes one.
        string stale = Path.Combine(ChatScreen.PictureTempFolder, CameraWatch.PictureName(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone)));
        if (File.Exists(stale))
        {
            File.Delete(stale);
        }
        _chat.EnqueueText("I see you.");
        string? seen = null;
        WatchFixture(input =>
        {
            SpinWait.SpinUntil(() => _cameraSystem!.Reads >= 2, 5000);
            Thread.Sleep(200);
            PushLine(input, "what now?");
        }, DoubleClickDown(4), DoubleClickDown(10), input =>
        {
            // Opened and still there before watch mode stops.
            seen = Directory.Exists(WatchFolder) ? string.Join("|", Directory.GetFiles(WatchFolder)) : null;
            PushLine(input, "/camera watch off");
        }, Line("/exit"));
        PaneOf40Rows();

        string output = await RunAsync();

        string opened = Assert.Single(_openedFiles.Distinct());
        Assert.Equal(WatchFolder, Path.GetDirectoryName(opened));
        Assert.StartsWith("camera-watch-", Path.GetFileName(opened));
        Assert.EndsWith(".jpg", opened);
        Assert.Equal(opened, seen);
        Assert.False(Directory.Exists(WatchFolder));   // cleared by /camera watch off
        Assert.Contains(CameraText.WatchOff, output);
        Assert.DoesNotContain("Could not open", output);
        Assert.False(File.Exists(Path.Combine(ChatScreen.PictureTempFolder, Path.GetFileName(opened))));
    }

    [Fact]
    public async Task TheWatchFolder_IsClearedAsTheProfileLoads_AndCameraOffClearsItToo()
    {
        Directory.CreateDirectory(WatchFolder);
        await File.WriteAllBytesAsync(Path.Combine(WatchFolder, "camera-watch-101010.jpg"), [1, 2, 3]);
        _cameraSystem = new FakeCameraSystem();
        bool clearedAtStart = false;
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            switch (step++)
            {
                case 0:
                    clearedAtStart = !Directory.Exists(WatchFolder);
                    Directory.CreateDirectory(WatchFolder);   // as a double-click would have left it
                    PushLine(input, "/camera off");
                    break;
                case 1:
                    PushLine(input, "/exit");
                    break;
            }
        };

        await RunAsync();

        Assert.True(clearedAtStart);
        Assert.False(Directory.Exists(WatchFolder));
        Assert.True(Directory.Exists(CameraFolder) || !Directory.Exists(CameraFolder));   // the camera folder itself is left alone
    }

    [WindowsFact]
    public async Task Watch_SpeaksUp_WhenAllowed_TheModelShownTheChangeUnasked()
    {
        _chat.EnqueueText("You waved.");
        _settings.Update(d => d.CameraWatchUnprompted = true);
        WatchFixture(_ => { }, Line("/camera watch off"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(CameraText.WatchNudgeNotice, output);
        var user = _chat.Requests[0].Last(m => m.Role == ChatRole.User);
        Assert.Equal(CameraText.WatchNudgeMessage, user.Text);
        Assert.Single(user.Contents.OfType<DataContent>());
        Assert.Contains("You waved.", output);
        Assert.Contains(CameraText.WatchOff, output);
    }

    [Fact]
    public async Task CameraOff_LetsTheWatchGo_AndABadIntervalIsAnError()
    {
        _cameraSystem = new FakeCameraSystem();
        PushLine("/camera watch 1");
        PushLine("/camera watch off");
        PushLine("/camera watch");
        PushLine("/camera off");
        PushLine("/camera off");
        PushLine("/camera live");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(CameraText.BadSeconds("1"), output);
        Assert.Contains(CameraText.WatchNotOn, output);
        Assert.Contains(CameraText.WatchOn(10, 8, false), output);
        Assert.Contains(CameraText.Off(1), output);
        Assert.Contains(CameraText.Off(0), output);
        Assert.Contains(CameraText.NoViewer, output);
    }

    [WindowsFact]
    public async Task BotChatCamera_EachBotsTurn_CarriesAFreshPictureOfTheUser_Last_WithItsCaption()
    {
        BotChatFixture();
        _cameraSystem = new FakeCameraSystem();
        _settings.Update(d => d.BotChatCamera = true);
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.EnqueueText("Neon ", "again");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(CameraText.BotChatOn, output);
        Assert.Equal(3, _chat.Requests.Count);
        Assert.All(_chat.Requests, request =>
        {
            var turn = request[^1];
            Assert.NotNull(ConversationHistory.CameraPath(Assert.Single(turn.Contents.OfType<DataContent>())));
            Assert.EndsWith(BotChat.CameraCaption(1, 1), turn.Text, StringComparison.Ordinal);
            Assert.EndsWith(BotChat.CameraRule, request.Single(m => m.Role == ChatRole.System).Text, StringComparison.Ordinal);
        });
        Assert.Equal(1, _cameraSystem.Opens);
        Assert.Empty(CameraFiles());   // memory only
    }

    [Fact]
    public async Task BotChatCamera_AFailedCamera_IsOneWarning_AndTheChatGoesOnWithout()
    {
        BotChatFixture();
        _cameraSystem = new FakeCameraSystem { OpenFailure = new CameraException(CameraFailure.InUse, null) };
        _settings.Update(d => d.BotChatCamera = true);
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.EnqueueText("Neon ", "again");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(1, output.Split(CameraText.BotChatFailed(CameraText.Failure(CameraFailure.InUse, null))).Length - 1);
        Assert.Equal(3, _chat.Requests.Count);
        Assert.All(_chat.Requests, request => Assert.Empty(request[^1].Contents.OfType<DataContent>()));
    }

    /// <summary>The camera fixture with a pane tall and wide enough for the whole Offered tab and /sys' Tools tab on one screen.</summary>
    private void TallCameraPane(bool on)
    {
        CameraPane();
        _settings.Update(d => d.CameraTools = on);
        _console.Profile.Height = 200;
        _console.Profile.Width = 400;
    }

    [Fact]
    public async Task ToolsOffered_ListsCameraCapture_InTheCameraGroup()
    {
        TallCameraPane(on: true);
        PushLine("/tools");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("\n── Camera · 1 ─", output);
        Assert.Matches("\n[ ▸] " + CameraCaptureTool.ToolName + " ", output);   // the first row, under the cursor, since the groups went alphabetical (2026-10-04)
        Assert.DoesNotContain(SystemPromptSummary.CameraOffSuffix, output);
    }

    [Fact]
    public async Task ToolsOffered_ShowsTheCameraGroupNotOffered_WhileCameraToolIsOff()
    {
        TallCameraPane(on: false);
        PushLine("/tools");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Matches("\n[ ▸] " + CameraCaptureTool.ToolName + " ", output);
        Assert.Contains("── Camera · 1 ── off: " + ToolsText.SwitchOffReason(SettingsField.CameraTools) + " ─", output);   // the one reason, not the list of them (2026-10-04)
    }

    [Fact]
    public async Task SysTools_ListsCameraCapture_OnlyWhileItIsOffered()
    {
        TallCameraPane(on: true);
        PushLine("/sys");
        _console.Input.PushKey(Keys.Right);   // the Tools tab
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("── Camera · 1 ─", output);
        Assert.Contains(CameraCaptureTool.ToolName, output);
    }

    [Fact]
    public async Task SysTools_LeavesTheCameraOut_WhileCameraToolIsOff()
    {
        TallCameraPane(on: false);
        PushLine("/sys");
        _console.Input.PushKey(Keys.Right);
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(CameraCaptureTool.ToolName, output);
    }

    [Fact]
    public async Task CameraCapture_SwitchedOffByName_IsNotOffered()
    {
        CameraPane();
        _settings.Update(d => { d.CameraTools = true; d.ToolsDisabled = [CameraCaptureTool.ToolName]; });
        _chat.EnqueueText("hi");
        PushLine("hello");
        PushLine("/exit");

        await RunAsync();

        Assert.DoesNotContain(CameraCaptureTool.ToolName, _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
    }

    [Theory]
    [InlineData("live", MidTurnClass.Quick)]
    [InlineData("watch", MidTurnClass.Quick)]
    [InlineData("watch 5", MidTurnClass.Quick)]
    [InlineData("watch off", MidTurnClass.Quick)]
    [InlineData("off", MidTurnClass.Quick)]
    [InlineData("list", MidTurnClass.Pane)]    // its list on the info pane over the reply since 2026-10-04 (the user's pick)
    [InlineData("use 2", MidTurnClass.Quick)]
    [InlineData("zoom", MidTurnClass.Quick)]   // a word /camera does not know: its error at once
    [InlineData("", MidTurnClass.Deferred)]    // the pane would take the keys from the reply
    [InlineData("snap", MidTurnClass.Deferred)]   // the photo goes on the idle line
    public void Camera_UnderAReply_RunsAtOnce_ButThePaneAndSnapWait(string args, MidTurnClass expected) =>
        Assert.Equal(expected, ChatScreen.MidTurnPolicy(SlashCommand.Camera, args));

    [Fact]
    public async Task MidTurn_CameraWatchAndOff_RunWhileTheReplyStreams()
    {
        _cameraSystem = new FakeCameraSystem();
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                PushLine("/camera watch 5");
                PushLine("/camera list");
            }
            else if (i == 2)
            {
                Scripted().Push(Keys.Escape);   // the list's pane over the reply (2026-10-04) closed
                PushLine("/camera off");
            }
        });

        string output = await RunAsync();

        Assert.Contains(CameraText.WatchOn(5, 8, false), output);
        Assert.Contains("  1. Fake Cam  ← chosen", output);
        Assert.Contains(CameraText.Off(1), output);
        Assert.DoesNotContain(ChatScreen.MidTurnDeferredNotice("/camera"), output);
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task CameraTool_IsNotOffered_WhenSwitchedOff_OrWithoutALayer()
    {
        CameraPane();
        _cameraSystem = null;
        _settings.Update(d => d.CameraTools = true);
        _chat.EnqueueText("hi");
        PushLine("hello");
        PushLine("/exit");

        await RunAsync();

        Assert.DoesNotContain(CameraCaptureTool.ToolName, _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
    }

    /// <summary>
    /// The toolbar's 📸 opens the Camera tool page with the watch button (2026-10-04, the user's ask): W starts /camera watch at
    /// the setting's interval, its line on the pane, and the typed /camera watch off then finds it running.
    /// </summary>
    [Fact]
    public async Task TheCameraToolPage_TheWatchButton_StartsWatch()
    {
        _cameraSystem = new FakeCameraSystem();
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = ["camera"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 200;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the toolbar at 103: 📸 at 0
        StepsWhenIdle(
            input => { input.PushClick(0, 103); input.PushClick(0, 103); },   // 📸: the Camera tool page
            input => input.Push(Keys.Char('w'), Keys.Escape),                  // watch on, the page left
            Line("/camera watch off"),
            Line("/exit"));

        string output = await RunAsync();

        var saved = _settings.Current;
        Assert.Contains("\n" + Titled(ToolsText.Label + " › " + SettingsMenu.FieldName(SettingsField.CameraTools) + " │ " + SettingsMenu.CameraWatchButtonTitle + " · " + SettingsMenu.CameraSnapButtonTitle + " ") + "\n", output);
        Assert.Contains(CameraText.WatchOn(saved.CameraWatchSeconds, saved.CameraWatchThreshold, saved.CameraWatchUnprompted), output);
        Assert.Contains(CameraText.WatchOff, output);
        Assert.DoesNotContain(CameraText.WatchNotOn, output);
        Assert.Empty(_chat.Requests);
    }
}
