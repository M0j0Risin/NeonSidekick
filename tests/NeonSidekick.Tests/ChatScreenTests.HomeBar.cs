using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The home bar on the screen (2026-10-12): <c>/homebar</c> and Ctrl+W hide it and show it again with its items; the bar
/// draws the chips over the live feed (a scripted Home Assistant), a click shows a chip's note, a double-click calls its
/// service over the socket and the chip follows the change; an unlock asks first.
/// </summary>
public partial class ChatScreenTests
{

    private FakeHaServer HomeServer()
    {
        var server = new FakeHaServer()
            .Area("den", "Den")
            .Entity("light.desk", "off", """{"friendly_name":"Desk"}""", area: "den")
            .Entity("lock.front", "locked", """{"friendly_name":"Front Door"}""");
        _haWire = server.Connect;
        _settings.Update(d =>
        {
            d.TtsOutput = false;
            d.HomeAssistantUrl = "http://ha.test:8123";
            d.HomeAssistantToken = "secret";
            d.ToolbarItems = [];
            d.PerformanceBarItems = [];
        });
        return server;
    }

    /// <summary>
    /// The home bar with two cameras (2026-10-27, the ring-integration plan's step 4): <c>camera.porch</c> ("Porch Camera") and an unavailable
    /// <c>camera.garage</c>, their stills over a REST stub (<paramref name="still"/>, else a JPEG), so nothing reaches the network; the pane on.
    /// </summary>
    private StubHttpMessageHandler CameraServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? still = null)
    {
        HomeServer()
            .Entity("camera.porch", "idle", """{"friendly_name":"Porch Camera"}""")
            .Entity("camera.garage", "unavailable", """{"friendly_name":"Garage Camera"}""");
        var stub = new StubHttpMessageHandler().Map("http://ha.test:8123/api/camera_proxy/",
            still ?? ((_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, StripFixtures.Jpeg(), "image/jpeg"))));
        _haClient = (url, token) => new HaClient(url, token, new HttpClient(stub));
        _settings.Update(d => d.HomeBarItems = ["entity:camera.porch", "entity:camera.garage"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        return stub;
    }

    private static int Stills(StubHttpMessageHandler stub)
    {
        lock (stub.Requests)
        {
            return stub.Requests.Count(r => r.Uri.AbsolutePath.StartsWith("/api/camera_proxy/", StringComparison.Ordinal));
        }
    }

    private string HomeCameraFolder => Path.Combine(_settings.ProfileImagesDirectory, HaCamera.FolderName);

    /// <summary>
    /// The read's waits as stages: each runs when <c>ready</c> holds at a wait, else the wait passes (a camera's still wakes the idle read when
    /// it lands). The first waits for the chips drawn (<paramref name="drawn"/>, the Porch chip's text, an alias's since 2026-11-13), ticking the
    /// clock; after it the clock stands, so the 2 s reuse holds.
    /// </summary>
    private void CameraStages(params (Func<bool> Ready, Action<ScriptedInput> Act)[] stages) => CameraStages("📹 Porch Camera", stages);

    private void CameraStages(string drawn, params (Func<bool> Ready, Action<ScriptedInput> Act)[] stages)
    {
        var input = Scripted();
        int next = 0;
        input.OnWait = () =>
        {
            if (next == 0)
            {
                TickUntil(() => Output.Contains(drawn, StringComparison.Ordinal));
            }

            if (next < stages.Length && stages[next].Ready())
            {
                stages[next++].Act(input);
            }
        };
    }

    [Fact]
    public async Task HomeBar_ACamerasTap_FetchesSavesAndOpensTheStill_AndAnotherWithinTwoSecondsOpensTheSameFile()
    {
        var stub = CameraServer();
        var viewed = new List<string>();
        _viewPicture = path => { lock (viewed) { viewed.Add(path); } };
        int Viewed() { lock (viewed) { return viewed.Count; } }
        CameraStages(
            (() => true, input => PushLine(input, "/homebar tap Porch Camera")),
            (() => Viewed() == 1, input => PushLine(input, "/homebar show entity:camera.porch")),   // a right-click's line
            (() => Viewed() == 2, input => PushLine(input, "/exit")));

        string output = await RunAsync();

        string saved = Assert.Single(Directory.GetFiles(HomeCameraFolder));
        Assert.StartsWith(Path.Combine(HomeCameraFolder, "porch-"), saved);
        Assert.Equal([saved, saved], viewed);
        Assert.Equal(1, Stills(stub));
        Assert.Contains("Porch Camera: a still, saved as ", output);   // the path wraps at the window's width
        Assert.DoesNotContain("▀▀▀▀", output);   // opened in the window, not drawn
        Assert.Empty(_chat.Requests);   // nothing goes to the conversation
    }

    [Fact]
    public async Task HomeBar_ACamerasStill_WithNoPictureWindow_IsDrawnInTheTranscript()
    {
        CameraServer();
        int mark = -1;
        CameraStages(
            (() => true, input => { mark = Output.Length; PushLine(input, "/homebar tap Porch Camera"); }),
            (() => Output[mark..].Contains("▀▀▀▀", StringComparison.Ordinal), input => PushLine(input, "/exit")));

        string output = await RunAsync();

        string saved = Assert.Single(Directory.GetFiles(HomeCameraFolder));
        Assert.Contains("Porch Camera: a still, saved as ", output);   // the path wraps at the window's width
        Assert.Contains("▀▀▀▀", output[mark..]);
    }

    [Fact]
    public async Task HomeBar_ACameraHeldPastTheWait_StaysPending_IsFetchedOnce_AndNeverReadsAsNoChange()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = CameraServer(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, StripFixtures.Jpeg(), "image/jpeg");
        });
        var viewed = new List<string>();
        _viewPicture = path => { lock (viewed) { viewed.Add(path); } };
        int Viewed() { lock (viewed) { return viewed.Count; } }
        var logs = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { lock (logs) { logs.Add(e.Message); } };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            CameraStages(
                (() => true, input => PushLine(input, "/homebar tap Porch Camera")),
                (() => true, input => PushLine(input, "/homebar tap Porch Camera")),   // pending: left alone
                (() => true, input =>
                {
                    for (int i = 0; i < 8; i++)
                    {
                        _time.Advance(TimeSpan.FromSeconds(1));   // past HomePendingWait, the bar drawn meanwhile
                        Thread.Sleep(20);
                    }

                    release.SetResult();
                }),
                (() => Viewed() == 1, input => PushLine(input, "/exit")));

            await RunAsync();
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(1, Stills(stub));
        Assert.Equal(1, Viewed());
        lock (logs)
        {
            Assert.DoesNotContain(HomeBarText.NoChange("Porch Camera"), logs);
        }
    }

    [Fact]
    public async Task HomeBar_AProfileSwitchUnderACamerasFetch_KeepsAndShowsNothing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool asked = false;
        var stub = CameraServer(async (_, ct) =>
        {
            asked = true;
            await release.Task.WaitAsync(ct);
            return StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, StripFixtures.Jpeg(), "image/jpeg");
        });
        WorkProfile();
        string before = HomeCameraFolder;
        var viewed = new List<string>();
        _viewPicture = path => { lock (viewed) { viewed.Add(path); } };
        CameraStages(
            (() => true, input => PushLine(input, "/homebar tap Porch Camera")),
            (() => true, input => { TickUntil(() => Volatile.Read(ref asked)); PushLine(input, "/profile work"); }),
            (() => true, input => { release.TrySetResult(); Thread.Sleep(100); PushLine(input, "/exit"); }));

        string output = await RunAsync();

        Assert.Equal(1, Stills(stub));
        Assert.False(Directory.Exists(before));
        Assert.False(Directory.Exists(HomeCameraFolder));
        Assert.Empty(viewed);
        Assert.DoesNotContain("Porch Camera: a still", output);
    }

    [Fact]
    public async Task HomeBar_TheSettingsMovingToAnotherProfileUnderAFetch_KeepTheStillInTheProfileItWasFetchedFor()
    {
        // The settings switch with no screen switch, so nothing forgets the fetch: only the folder taken at the tap keeps the still where it
        // was asked for (2026-10-27, the review).
        var stub = CameraServer(async (_, _) =>
        {
            await _settings.SwitchProfileAsync("work");
            return StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, StripFixtures.Jpeg(), "image/jpeg");
        });
        WorkProfile();
        string first = HomeCameraFolder;
        var viewed = new List<string>();
        _viewPicture = path => { lock (viewed) { viewed.Add(path); } };
        int Viewed() { lock (viewed) { return viewed.Count; } }
        CameraStages(
            (() => true, input => PushLine(input, "/homebar tap Porch Camera")),
            (() => Viewed() == 1, input => PushLine(input, "/exit")));

        await RunAsync();

        Assert.NotEqual(first, HomeCameraFolder);
        string saved = Assert.Single(Directory.GetFiles(first));
        Assert.Equal([saved], viewed);
        Assert.False(Directory.Exists(HomeCameraFolder));
        Assert.Equal(1, Stills(stub));
    }

    [Fact]
    public async Task HomeBar_ACameraWithNoPicture_FlashesWhy_AndKeepsNothing()
    {
        var stub = CameraServer((_, _) => Task.FromResult(StubHttpMessageHandler.Json(System.Net.HttpStatusCode.InternalServerError, "500: Internal Server Error", "text/plain")));
        string flash = HomeBarText.Failed("Porch Camera", HaText.CameraNoPicture)[..60];
        bool flashed = false;
        CameraStages(
            (() => true, input => PushLine(input, "/homebar tap Porch Camera")),
            (() => true, input =>
            {
                TickUntil(() => Output.Contains(flash, StringComparison.Ordinal));
                flashed = Output.Contains(flash, StringComparison.Ordinal);
                PushLine(input, "/exit");
            }));

        await RunAsync();

        Assert.True(flashed);
        Assert.Equal(1, Stills(stub));
        Assert.False(Directory.Exists(HomeCameraFolder));
    }

    // ─── /ha camera (2026-11-13, the user's ask: what a chip's double-click does, for any camera) ─────────────────────

    /// <summary>
    /// <see cref="CameraServer"/> with the REST reads <c>/ha camera</c> resolves over: Porch and the unavailable Garage (both on the bar) and
    /// <c>camera.yard</c> ("Yard", not on it); no areas.
    /// </summary>
    private StubHttpMessageHandler CameraCommandServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? still = null) =>
        CameraServer(still)
            .Map("http://ha.test:8123/api/states", System.Net.HttpStatusCode.OK, """
                [{"entity_id":"camera.porch","state":"idle","attributes":{"friendly_name":"Porch Camera"}},
                 {"entity_id":"camera.garage","state":"unavailable","attributes":{"friendly_name":"Garage Camera"}},
                 {"entity_id":"camera.yard","state":"idle","attributes":{"friendly_name":"Yard"}},
                 {"entity_id":"light.den","state":"on","attributes":{"friendly_name":"Den"}}]
                """)
            .Map("http://ha.test:8123/api/template", System.Net.HttpStatusCode.OK, "[]", "text/plain");

    [Fact]
    public async Task HaCamera_ACameraOffTheBar_SaysItsComing_ThenSavesAndOpensTheStill_AndARepeatOpensTheSameFile()
    {
        var stub = CameraCommandServer();
        var viewed = new List<string>();
        _viewPicture = path => { lock (viewed) { viewed.Add(path); } };
        int Viewed() { lock (viewed) { return viewed.Count; } }
        CameraStages(
            (() => true, input => PushLine(input, "/ha camera Yard")),
            (() => Viewed() == 1, input => PushLine(input, "/ha camera yard")),   // within 2 s: the same still, the same file
            (() => Viewed() == 2, input => PushLine(input, "/exit")));

        string output = await RunAsync();

        string saved = Assert.Single(Directory.GetFiles(HomeCameraFolder));
        Assert.StartsWith(Path.Combine(HomeCameraFolder, "yard-"), saved);
        Assert.Equal([saved, saved], viewed);
        Assert.Equal(1, Stills(stub));
        Assert.Contains(HomeBarText.CameraComing("Yard"), output);
        Assert.True(output.IndexOf(HomeBarText.CameraComing("Yard"), StringComparison.Ordinal) < output.IndexOf("Yard: a still, saved as ", StringComparison.Ordinal));
        Assert.Empty(_chat.Requests);   // nothing goes to the conversation
    }

    [Fact]
    public async Task HaCamera_AChipsAlias_OpensItsCamera_AndItsDrawnChipShowsTheWait_NoLine()
    {
        var stub = CameraCommandServer();
        _settings.Update(d => d.HomeBarAliases = new Dictionary<string, string> { ["entity:camera.porch"] = "Door" });
        var viewed = new List<string>();
        _viewPicture = path => { lock (viewed) { viewed.Add(path); } };
        int Viewed() { lock (viewed) { return viewed.Count; } }
        CameraStages("📹 Door",
            (() => true, input => PushLine(input, "/ha camera door")),
            (() => Viewed() == 1, input => PushLine(input, "/exit")));

        string output = await RunAsync();

        Assert.StartsWith(Path.Combine(HomeCameraFolder, "porch-"), Assert.Single(viewed));
        Assert.Contains("Porch Camera: a still, saved as ", output);   // Home Assistant's name, as the chip's notice has it
        Assert.DoesNotContain(HomeBarText.CameraComing("Porch Camera"), output);
        Assert.Equal(1, Stills(stub));
    }

    [Fact]
    public async Task HaCamera_Refusals_AreErrorLines_AndFetchNothing()
    {
        var stub = CameraCommandServer();
        CameraStages(
            (() => true, input =>
            {
                PushLine(input, "/ha camera Garage Camera");
                PushLine(input, "/ha camera Shed");
                PushLine(input, "/ha camera");
                PushLine(input, "/ha camera light.den");
                PushLine(input, "/exit");
            }));

        string output = await RunAsync();

        Assert.Contains("  ✗ " + HaText.CameraUnavailable("Garage Camera", "unavailable"), output);
        Assert.Contains("  ✗ Error: nothing named 'Shed'", output);
        Assert.Contains("  ✗ Error: 'camera' could be 3 things", output);   // bare, with three cameras
        Assert.Contains("  ✗ Error: Den (light.den) is not a camera; /ha camera takes", output);
        Assert.Equal(0, Stills(stub));
        Assert.False(Directory.Exists(HomeCameraFolder));
    }

    [Fact]
    public async Task HaCamera_NotConfigured_IsItsErrorLine()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HomeAssistantUrl = ""; });
        PushLine("/ha camera Yard");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + HaText.NotConfigured[..60], output);
    }

    [Fact]
    public async Task HaCamera_WithThePaneOff_SaysItsComing_ThenTheSavedNotice()
    {
        var stub = CameraCommandServer();
        _geometry = null;   // CameraServer turns the pane on; off, the bar never draws, so CameraStages' first wait would never pass
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            if (step == 0)
            {
                step++;
                PushLine(input, "/ha camera Yard");
            }
            else if (step == 1 && Output.Contains("Yard: a still, saved as ", StringComparison.Ordinal))
            {
                step++;
                PushLine(input, "/exit");
            }
        };

        string output = await RunAsync();

        Assert.Contains(HomeBarText.CameraComing("Yard"), output);
        Assert.True(output.IndexOf(HomeBarText.CameraComing("Yard"), StringComparison.Ordinal) < output.IndexOf("Yard: a still, saved as ", StringComparison.Ordinal));
        Assert.Equal(1, Stills(stub));
    }

    [Fact]
    public async Task HaCamera_AStillThatFails_IsAnErrorLineAfterItsComingLine_AndAnotherTryFetchesAfresh()
    {
        var stub = CameraCommandServer((_, _) => Task.FromResult(StubHttpMessageHandler.Json(System.Net.HttpStatusCode.InternalServerError, "500: Internal Server Error", "text/plain")));
        string failed = HomeBarText.Failed("Yard", HaText.CameraNoPicture)[..50];
        int Count(string text) => (Output.Length - Output.Replace(text, "", StringComparison.Ordinal).Length) / text.Length;
        // Counted as error lines, and the second stage waits for the second fetch too (2026-11-13, the review: a flash redrawn each tick
        // could pass a bare count after one failure, and /exit then cancelled the second fetch).
        CameraStages(
            (() => true, input => PushLine(input, "/ha camera Yard")),
            (() => Count("  ✗ " + failed) >= 1, input => PushLine(input, "/ha camera Yard")),   // the failure leaves no mark to block it
            (() => Stills(stub) == 2 && Count("  ✗ " + failed) >= 2, input => PushLine(input, "/exit")));

        string output = await RunAsync();

        Assert.Equal(2, Stills(stub));
        Assert.Contains("  ✗ " + failed, output);   // a line, the pane on: no chip shows this camera's wait
        Assert.Equal(Count("  ✗ " + failed), Count(failed));   // and no flash beside it (2026-11-13, the review)
        Assert.True(output.IndexOf(HomeBarText.CameraComing("Yard"), StringComparison.Ordinal) < output.IndexOf("  ✗ " + failed, StringComparison.Ordinal));
        Assert.DoesNotContain(HomeBarText.CameraFetching("Yard"), output);
        Assert.False(Directory.Exists(HomeCameraFolder));
    }

    /// <summary>
    /// A window too short for the home row (the review of 2026-11-13): the bar's row is still built, but not drawn, so no chip shows the wait —
    /// a camera on the bar gets the "fetching" line, then its saved notice.
    /// </summary>
    [Fact]
    public async Task HaCamera_AShortWindowWithNoHomeRow_SaysItsComing_ForACameraOnTheBar()
    {
        var stub = CameraCommandServer();
        _console.Profile.Height = 5;   // under the pane's 4 rows and 2 for the transcript: no row left for the home bar
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            if (step == 0)
            {
                step++;
                int ticks = 0;
                TickUntil(() => ++ticks > 30);   // the pane's draws first, so the bar's row is built (and dropped)
                PushLine(input, "/ha camera Porch Camera");
            }
            else if (step == 1 && Output.Contains("Porch Camera: a still, saved as ", StringComparison.Ordinal))
            {
                step++;
                PushLine(input, "/exit");
            }
        };

        string output = await RunAsync();

        Assert.Contains(HomeBarText.CameraComing("Porch Camera"), output);
        Assert.Equal(1, Stills(stub));
    }

    /// <summary>
    /// <c>/ha camera</c> joining a chip's fetch while the chip shows the wait (the review of 2026-11-13): no "already on its way" line, as the
    /// chip's own second tap writes none with the pane on; one fetch, one still.
    /// </summary>
    [Fact]
    public async Task HaCamera_JoiningAChipsFetch_WhileItsChipShowsTheWait_WritesNoLine()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = CameraCommandServer(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, StripFixtures.Jpeg(), "image/jpeg");
        });
        var viewed = new List<string>();
        _viewPicture = path => { lock (viewed) { viewed.Add(path); } };
        int Viewed() { lock (viewed) { return viewed.Count; } }
        CameraStages(
            (() => true, input => PushLine(input, "/homebar tap Porch Camera")),
            (() => Stills(stub) == 1, input => PushLine(input, "/ha camera Porch Camera")),
            (() => true, _ => release.TrySetResult()),   // the command has run: the read waits again only after it
            (() => Viewed() == 1, input => PushLine(input, "/exit")));

        string output = await RunAsync();

        Assert.DoesNotContain(HomeBarText.CameraFetching("Porch Camera"), output);
        Assert.DoesNotContain(HomeBarText.CameraComing("Porch Camera"), output);
        Assert.Equal(1, Stills(stub));
        Assert.Single(viewed);
    }

    [Fact]
    public async Task HomeBar_AnUnavailableCamerasPane_IsTheEmptyOne_AndNothingIsFetched()
    {
        var stub = CameraServer();
        CameraStages(
            (() => true, input => { PushLine(input, "/homebar show Garage Camera"); input.Push(Keys.Escape); }),
            (() => true, input => PushLine(input, "/exit")));

        string output = await RunAsync();

        Assert.Contains(HomeBarText.DetailTitle("Garage Camera", "unavailable"), output);
        Assert.Contains(HomeBarText.NothingToDo, output);
        Assert.Equal(0, Stills(stub));
    }

    /// <summary>Waits on the real clock, the screen's ticking on the manual one, until <paramref name="done"/>.</summary>
    private void TickUntil(Func<bool> done)
    {
        var give = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < give)
        {
            _time.Advance(ScreenPane.Tick);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public async Task HomeBar_TogglesKeepingItsItems()   // Ctrl+W was the bare word until 2026-10-18, when the bar chords went
    {
        HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:light.desk"]);
        var shown = new List<int>();
        Action<ScriptedInput> Then(Action<ScriptedInput> step) => input =>
        {
            shown.Add(HomeBarItems.Resolve(_settings.Current.HomeBarItems).Count);
            step(input);
        };
        StepsWhenIdle(
            Line("/homebar"),
            Then(input => PushLine(input, "/homebar")),
            Then(input => PushLine(input, "/homebar off")),
            Then(input => PushLine(input, "/homebar on")),
            Then(input => PushLine(input, "/homebar sideways")),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([0, 1, 0, 1], shown);
        Assert.Contains(HomeBarText.Hidden, output);
        Assert.Contains(HomeBarText.Shown, output);
        Assert.Contains(HomeBarText.Usage, output);
        Assert.Equal(["entity:light.desk"], _settings.Current.HomeBarItems);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task HomeBar_UnderTheHintRow_AClickShowsTheNote_ADoubleClickSwitchesTheLight()
    {
        var server = HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:light.desk"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the hint row at 102, the home bar at 103, its chips centred (2026-10-12)
        bool drawn = false;
        bool noted = false;
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            TickUntil(() => Output.Contains("  💡 Desk", StringComparison.Ordinal) && server.Subscribed.Count > 0);
            drawn = Output.Contains("  💡 Desk", StringComparison.Ordinal);
            input.PushClick(58, 103);
            input.OnWait = () =>
            {
                noted = Output.Contains("Desk: off — double-click to switch on", StringComparison.Ordinal);
                input.OnWait = () =>
                {
                    input.OnWait = null;
                    TickUntil(() => server.StateOf("light.desk") == "on");
                    PushLine(input, "/exit");
                };
                input.PushClick(58, 103);
            };
        };

        await RunAsync();

        Assert.True(drawn);
        Assert.True(noted);
        Assert.Equal("light.toggle", Assert.Single(server.Calls).Service);
        Assert.Equal("on", server.StateOf("light.desk"));
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// The groups on the screen (later on 2026-10-13, the user's ask): a chip pinned left starts the row, one pinned right ends it,
    /// and a click finds each where it is drawn.
    /// </summary>
    [Fact]
    public async Task HomeBar_PinnedLeftAndRight_DrawsAtTheRowsEnds_AndAClickFindsEach()
    {
        HomeServer();
        _settings.Update(d =>
        {
            d.HomeBarItems = ["entity:light.desk", "entity:lock.front"];
            d.HomeBarLeft = ["entity:light.desk"];
            d.HomeBarRight = ["entity:lock.front"];
        });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the home bar at 103: 119 cells, the lock's 15 from 104
        bool desk = false;
        bool front = false;
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            TickUntil(() => Output.Contains(" 🔒 Front Door ", StringComparison.Ordinal));
            input.PushClick(1, 103);
            input.OnWait = () =>
            {
                desk = Output.Contains("Desk: off — double-click to switch on", StringComparison.Ordinal);
                input.OnWait = () =>
                {
                    front = Output.Contains("Front Door: locked", StringComparison.Ordinal);
                    input.OnWait = null;
                    PushLine(input, "/exit");
                };
                input.PushClick(117, 103);
            };
        };

        await RunAsync();

        Assert.True(desk);
        Assert.True(front);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// A double-click on the home bar's blank space opens <c>/settings</c> (2026-10-12, the user's ask: as on the other bars; a
    /// spent click until then), and switches nothing.
    /// </summary>
    [Fact]
    public async Task HomeBar_ADoubleClickOnItsBlanks_OpensTheSettings()
    {
        var server = HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:light.desk"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the hint row at 102, the home bar at 103, its chips centred (2026-10-12)
        int mark = -1;
        bool opened = false;
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            TickUntil(() => Output.Contains("  💡 Desk", StringComparison.Ordinal) && server.Subscribed.Count > 0);
            mark = Output.Length;
            input.PushClick(20, 103);
            input.PushClick(20, 103);
            input.OnWait = () =>
            {
                opened = Output[mark..].Contains(SettingsMenu.Title + " │ Client · Servers · Advisors · Automation · Voice", StringComparison.Ordinal);
                input.OnWait = null;
                input.Push(Keys.Escape);
                PushLine(input, "/exit");
            };
        };

        await RunAsync();

        Assert.True(mark >= 0);
        Assert.True(opened);
        Assert.Empty(server.Calls);
        Assert.Equal("off", server.StateOf("light.desk"));
    }

    [Fact]
    public async Task HomeBar_AnUnlock_AsksFirst_AndANoLeavesItLocked()
    {
        var server = HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:lock.front"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        bool asked = false;
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            TickUntil(() => Output.Contains("  🔒 Front Door", StringComparison.Ordinal) && server.Subscribed.Count > 0);
            input.PushClick(58, 103);
            input.PushClick(58, 103);
            input.OnWait = () =>
            {
                asked = Output.Contains("Unlock Front Door?", StringComparison.Ordinal);
                input.OnWait = null;
                input.Push(Keys.Escape);   // no
                PushLine(input, "/exit");
            };
        };

        await RunAsync();

        Assert.True(asked);
        Assert.Empty(server.Calls);
        Assert.Equal("locked", server.StateOf("lock.front"));
    }

    [Fact]
    public async Task HomeBar_TheRoomPane_DrawsAChangeThatComesWhileItWaits_ItsRowAndItsCount()
    {
        // The user's report (2026-10-12): a light switched from its own page still read off back on the room's list, since the
        // call's answer came after the list was drawn. Here the change comes from the server while the list waits, as that
        // answer does (a call's own timing would race the redraw).
        var server = HomeServer();
        _settings.Update(d => d.HomeBarItems = ["area:den"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        bool before = false;
        bool followed = false;
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            TickUntil(() => Output.Contains("  🏠 Den ", StringComparison.Ordinal) && server.Subscribed.Count > 0);
            PushLine(input, "/homebar show den");
            input.OnWait = () =>
            {
                before = Output.Contains("💡 Desk  off", StringComparison.Ordinal) && Output.Contains("Den · 0/1", StringComparison.Ordinal);
                server.Change("light.desk", "on");

                // The clock ticks until the bar has it (the pane's watcher sees it on the same ticks), then the read returns
                // with nothing pushed, so the cancelled wait draws the list again and reads once more.
                TickUntil(() => Output.Contains("  🏠 Den 1/1", StringComparison.Ordinal));
                for (int i = 0; i < 3; i++)
                {
                    _time.Advance(ScreenPane.Tick);
                    Thread.Sleep(10);
                }

                input.OnWait = () =>
                {
                    input.OnWait = null;
                    followed = Output.Contains("💡 Desk  on", StringComparison.Ordinal) && Output.Contains("Den · 1/1", StringComparison.Ordinal);
                    input.Push(Keys.Escape);
                    PushLine(input, "/exit");
                };
            };
        };

        await RunAsync();

        Assert.True(before);
        Assert.True(followed);
    }

    [Fact]
    public async Task HomeBar_Name_LabelsAChip_TakesItOff_AndRefusesWhatIsNotOnTheBar()
    {
        HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:light.desk"]);
        string? afterSet = null;
        StepsWhenIdle(
            Line("/homebar name entity:light.desk = D"),
            input =>
            {
                afterSet = _settings.Current.HomeBarAliases?.GetValueOrDefault("entity:light.desk");
                PushLine(input, "/homebar name D =");   // the emoji alone (later on 2026-10-12)
            },
            Line("/homebar name Desk = default"),
            Line("/homebar name Kettle = K"),
            Line("/homebar name Desk"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal("D", afterSet);
        Assert.Null(_settings.Current.HomeBarAliases);
        Assert.Contains("as \"D\"", output);
        Assert.Contains("as its emoji alone", output);
        Assert.Contains("by its own name again", output);
        Assert.Contains(HomeBarText.NotOnBar("Kettle"), output);
        Assert.Contains(HomeBarText.Usage, output);   // no "="
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public void HomeRoomKey_ChangesWithAMembersState_OrTheCount_NotWithAnotherEntity()
    {
        static HaLiveEntity Light(string id, string state) => new(id, state, new Dictionary<string, System.Text.Json.JsonElement>(), null);
        string[] members = ["light.a", "light.b"];
        var drawn = ChatScreen.HomeRoomKey(members, new Dictionary<string, HaLiveEntity> { ["light.a"] = Light("light.a", "off"), ["light.b"] = Light("light.b", "off") }, "0/2");

        Assert.NotEqual(drawn, ChatScreen.HomeRoomKey(members, new Dictionary<string, HaLiveEntity> { ["light.a"] = Light("light.a", "on"), ["light.b"] = Light("light.b", "off") }, "0/2"));
        Assert.NotEqual(drawn, ChatScreen.HomeRoomKey(members, new Dictionary<string, HaLiveEntity> { ["light.a"] = Light("light.a", "off"), ["light.b"] = Light("light.b", "off") }, "1/2"));
        Assert.Equal(drawn, ChatScreen.HomeRoomKey(members, new Dictionary<string, HaLiveEntity> { ["light.a"] = Light("light.a", "off"), ["light.b"] = Light("light.b", "off"), ["switch.x"] = Light("switch.x", "on") }, "0/2"));
    }

    /// <summary><c>/homebar emoji</c> (later on 2026-10-12, the user's ask): sets an item's emoji, the bar draws it, default or a blank takes it off, a word is refused.</summary>
    [Fact]
    public async Task HomeBar_Emoji_SetsAChipsEmoji_TakesItOff_AndRefusesAWord()
    {
        HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:light.desk"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        string? afterSet = null;
        bool drawn = false;
        StepsWhenIdle(
            Line("/homebar emoji entity:light.desk = 🦊"),
            input =>
            {
                afterSet = _settings.Current.HomeBarGlyphs?.GetValueOrDefault("entity:light.desk");
                TickUntil(() => Output.Contains("  🦊 Desk", StringComparison.Ordinal));
                drawn = Output.Contains("  🦊 Desk", StringComparison.Ordinal);
                PushLine(input, "/homebar emoji Desk = default");
            },
            Line("/homebar emoji Desk = 🐙"),
            Line("/homebar emoji Desk ="),
            Line("/homebar emoji Desk = fox"),
            Line("/homebar emoji Kettle = 🦊"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal("🦊", afterSet);
        Assert.True(drawn);   // the bar drew it
        Assert.Null(_settings.Current.HomeBarGlyphs);
        Assert.Contains(" as 🦊", output);
        Assert.Contains(HomeBarText.GlyphSet("Desk", "🐙"), output);   // by its friendly name, which only its chip has
        Assert.Contains(HomeBarText.GlyphReset("Desk", HomeChips.LightGlyph), output);
        Assert.Contains(HomeBarText.NotEmoji("fox"), output);
        Assert.Contains(HomeBarText.NotOnBar("Kettle"), output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// The shopping list's 🛒 (later on 2026-10-12, the user's ask): no Home Assistant, the lines to get counted as /shopping changes
    /// them, 🛒 0 still on the bar; a double-click on it opens the /shopping pane, as /homebar show (a right-click's line) does; it has no
    /// name for /homebar name.
    /// </summary>
    [Fact]
    public async Task HomeBar_TheShoppingChip_CountsTheLinesToGet_AndOpensTheList()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HomeBarItems = [HomeBarItems.Shopping]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the home bar at 103, "🛒 0" centred
        bool one = false;
        bool none = false;
        int mark = -1;
        bool clicked = false;
        StepsWhenIdle(
            Line("/shopping add milk"),
            Line("/shopping add eggs"),
            Line("/shopping tick milk"),
            input =>
            {
                TickUntil(() => Output.Contains("  🛒 1", StringComparison.Ordinal));
                one = Output.Contains("  🛒 1", StringComparison.Ordinal);
                PushLine(input, "/shopping tick eggs");
            },
            input =>
            {
                TickUntil(() => Output.Contains("  🛒 0", StringComparison.Ordinal));
                none = Output.Contains("  🛒 0", StringComparison.Ordinal);
                mark = Output.Length;
                input.PushClick(59, 103);
                input.PushClick(59, 103);
                input.Push(Keys.Escape);
            },
            input =>
            {
                clicked = Output[mark..].Contains(Shopping.ShoppingText.PaneCaption(0, 2), StringComparison.Ordinal);
                PushLine(input, "/homebar show " + HomeBarItems.Shopping);
                input.Push(Keys.Escape);
            },
            Line("/homebar name Shopping list = Cart"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.True(one);
        Assert.True(none);
        Assert.True(clicked);   // the double-click: the list's own pane
        Assert.Contains(Shopping.ShoppingText.PaneCaption(0, 2), output[mark..]);
        Assert.Contains(HomeBarText.Nameless(HomeBarText.ShoppingName), output);
        Assert.Null(_settings.Current.HomeBarAliases);
        Assert.Empty(_chat.Requests);
    }
}
