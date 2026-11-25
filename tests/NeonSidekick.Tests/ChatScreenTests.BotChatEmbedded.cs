using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/botchat</c> multi with embedded bots (later on 2026-09-29, the user's asks): <c>Botchat multi-embedded</c>'s
/// parent-server and multi-server, <c>Botchat multi-embedded kill</c>, and <c>/botchat --kill</c>.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The botchat fixture with ada on the embedded E2B and bob on the embedded E4B QAT, the starter on its own server.</summary>
    private FakeEmbeddedLlm EmbeddedBotsFixture(string mode)
    {
        BotChatFixture();
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b", "gemma-4-e4b-qat"));
        Profiles.WriteProfileFile(Profiles.ProfileFile(_dir, "ada"), new AppSettingsData { LlmUrl = "embedded", LlmModel = "gemma-4-e2b", TtsVoice = "bm_george" });
        Profiles.Create(_dir, "bob", new AppSettingsData { LlmUrl = "embedded", LlmModel = "gemma-4-e4b-qat", TtsVoice = "am_adam" });
        _settings.Update(d => { d.BotChatLlmMode = "multi"; d.BotChatMultiEmbedded = mode; });
        for (int i = 0; i < 4; i++)
        {
            _chat.EnqueueText("Line " + i + ".");
        }

        EscDuringRequest(3);
        return embedded;
    }

    /// <summary>
    /// The vision guard (2026-11-21, the user's pick with Botchat vision enabled on by default): on the embedded model with Embedded vision off,
    /// the app knows the model reads no pictures, so the bots get text alone (one notice) and the camera is never taken (its own notice).
    /// </summary>
    [Fact]
    public async Task BotChat_OnTheEmbeddedModelWithoutVision_TheBotsSeeTextAlone_AndTheCameraStaysOff()
    {
        BotChatFixture();
        UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        _cameraSystem = new FakeCameraSystem();
        _settings.Update(d => { d.LlmUrl = "embedded"; d.LlmModel = "gemma-4-e2b"; d.EmbeddedVision = false; d.BotChatCamera = true; });
        Assert.True(_settings.Current.BotChatVision);   // on by default since 2026-11-21
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.EnqueueText("Neon ", "again");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Equal(1, output.Split(BotChat.VisionUnseenNotice).Length - 1);   // once a chat
        Assert.Equal(1, output.Split(Camera.CameraText.BotChatBlind).Length - 1);
        Assert.DoesNotContain(Camera.CameraText.BotChatOn, output);
        Assert.Equal(0, _cameraSystem.Opens);   // the light never came on
        Assert.All(_chat.Requests, request =>
        {
            Assert.Empty(request[^1].Contents.OfType<Microsoft.Extensions.AI.DataContent>());
            Assert.DoesNotContain(BotChat.CameraCaption(1, 1), request[^1].Text, StringComparison.Ordinal);
            Assert.DoesNotContain(BotChat.CameraRule, request.Single(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task BotChat_Multi_ParentServer_NothingRunning_TheFirstEmbeddedBotStarts_TheNextSharesWithAWarning()
    {
        var embedded = EmbeddedBotsFixture("parent-server");
        embedded.StartGate = async _ => await Task.Yield();   // the links interleave, as the real starts do
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["gemma-4-e2b"], embedded.Starts);   // ada's, the first embedded bot; bob's model never starts
        Assert.Empty(embedded.ExtraStarts);
        Assert.Contains(BotChat.SharedEmbeddedWarning("bob", "Gemma 4 E4B QAT", "Gemma 4 E2B"), output);
        Assert.DoesNotContain("bob sits this one out", output);
        Assert.Contains(BotChat.LinkNotice("bob", EmbeddedEndpoint.BaseUrl, "gemma-4-e2b", "none"), output);
    }

    [EmbeddedHereFact]
    public async Task BotChat_Multi_ABotsLoad_StopsALeftoverContainer_AndSaysSo()
    {
        // 2026-10-26 (the review's R14): a bot's extra loads beside a chosen container as the main model would; the loaded profile's list goes,
        // and the notice comes under the links. The container publishes the launch server's port, so the launch's own clear spares it.
        var embedded = EmbeddedBotsFixture("multi-server");
        var docker = new FakeDockerServers
        {
            List = new Docker.DockerServerList(
                [new Docker.DockerContainer("id-a", "sglang_a", "lmsysorg/sglang:latest", "sha", "running", "Up", 0, [new Docker.DockerPort("0.0.0.0", 1234, 1234, "tcp")], new Dictionary<string, string>(), [], [])],
                [], null),
        };
        _settings.Update(d => { d.DockerServers = true; d.DockerServerContainers = ["sglang_a"]; });
        _session.Dispose();
        _session = new LlmSession(new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)), new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
            (endpoint, _) => { _endpoints.Add(endpoint); return _chat; }, _time, embedded: embedded, dockerServers: docker);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["clear port 1234", "clear settle", "clear settle"], docker.Calls);   // the launch's, then each extra's
        Assert.Contains(Docker.DockerServerText.LeftForThisComputer(["sglang_a"], embedded: true), output);
        Assert.Equal(["gemma-4-e2b", "gemma-4-e4b-qat"], embedded.ExtraStarts);
    }

    [Fact]
    public async Task BotChat_Multi_MultiServer_EachModelGetsItsServer_StoppedWhenTheChatEnds()
    {
        var embedded = EmbeddedBotsFixture("multi-server");
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["gemma-4-e2b", "gemma-4-e4b-qat"], embedded.ExtraStarts);   // nothing ran: both on extras, in cast order
        Assert.Empty(embedded.Starts);
        Assert.DoesNotContain("wanted", output);
        Assert.Contains(BotChat.ExtrasStoppedNotice(["Gemma 4 E2B", "Gemma 4 E4B QAT"]), output);
        Assert.Empty(embedded.Extras);
    }

    [Fact]
    public async Task BotChat_Multi_MultiServer_KillOff_KeepsTheExtras_ForTheNextBotchat()
    {
        var embedded = EmbeddedBotsFixture("multi-server");
        _settings.Update(d => d.BotChatMultiEmbeddedKill = false);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(2, embedded.Extras.Count);   // kept for the next botchat
        Assert.DoesNotContain("stopped 2 extra embedded servers", output);
        Assert.Equal(0, embedded.ExtraStops);
    }

    [Fact]
    public async Task BotChat_Multi_MultiServer_AnExtraThatFails_SitsItsBotOut()
    {
        var embedded = EmbeddedBotsFixture("multi-server");
        embedded.ExtraStartFailure = "out of memory";
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BotChat.SkippedNotice("ada", EmbeddedLlmText.StartFailed("out of memory")), output);
        Assert.Contains(BotChat.SkippedNotice("bob", EmbeddedLlmText.StartFailed("out of memory")), output);
        Assert.Contains(BotChat.TooFewError, output);
    }

    [Fact]
    public async Task BotChat_Kill_StopsTheExtras_ThenSaysThereAreNone_AndMoreWordsAreItsUsageError()
    {
        _settings.Update(d => d.TtsOutput = false);
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        await embedded.StartExtraAsync(EmbeddedModelCatalog.Find("gemma-4-e2b")!, new AppSettingsData(), ExtraOwner.BotChat, null, CancellationToken.None);
        PushLine("/botchat --kill now");
        PushLine("/botchat --KILL");
        PushLine("/botchat --kill");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BotChat.KillUsageError, output);
        Assert.Contains(BotChat.ExtrasStoppedNotice(["Gemma 4 E2B"]), output);
        Assert.Contains(BotChat.NoExtrasNotice, output);
        Assert.Empty(embedded.Extras);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// <c>/botchat --kill</c> and a botchat's end leave an advisor's extra running (2026-10-16, the user's call: it stopped with the
    /// bots' until then), and the 🧟 glyph counts only the bots'.
    /// </summary>
    [Fact]
    public async Task BotChat_Kill_LeavesAnAdvisorsExtra()
    {
        _settings.Update(d => d.TtsOutput = false);
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b", "gemma-4-e4b-qat"));
        await embedded.StartExtraAsync(EmbeddedModelCatalog.Find("gemma-4-e2b")!, new AppSettingsData(), ExtraOwner.BotChat, null, CancellationToken.None);
        await embedded.StartExtraAsync(EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!, new AppSettingsData(), ExtraOwner.Advisor, null, CancellationToken.None);
        PushLine("/botchat --kill");
        PushLine("/botchat --kill");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BotChat.ExtrasStoppedNotice(["Gemma 4 E2B"]), output);
        Assert.Contains(BotChat.NoExtrasNotice, output);
        Assert.Equal("gemma-4-e4b-qat", Assert.Single(embedded.Extras).ModelId);
        Assert.Empty(embedded.ExtrasOf(ExtraOwner.BotChat));
    }

    /// <summary>🧟 on the lower rule (2026-11-25; the strip's until then): drawn with Status unchecked, and a double-click is <c>/botchat --kill</c>.</summary>
    [Fact]
    public async Task BotChat_TheLowerRulesExtras_ADoubleClickStopsThem()
    {
        _settings.Update(d => { d.TtsOutput = false; d.StatusBarItems = ["timers", "usage"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the lower rule at 101
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        await embedded.StartExtraAsync(EmbeddedModelCatalog.Find("gemma-4-e2b")!, new AppSettingsData(), ExtraOwner.BotChat, null, CancellationToken.None);
        var input = WhenIdle(i =>
        {
            i.PushClick(238, 101);   // 🧟 alone on the lower rule: the space after it at the right edge
            i.PushClick(238, 101);
            PushLine(i, "/exit");
        });

        string output = await RunAsync(input);

        Assert.Contains(" " + BotChat.ExtrasGlyph + " ─", output);
        Assert.Contains(BotChat.ExtrasStoppedNotice(["Gemma 4 E2B"]), output);
        Assert.Empty(embedded.Extras);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>The same 🧟 under a reply (2026-11-25): held as Ctrl+Alt+K's line is, said as waiting, and run when the reply ends.</summary>
    [Fact]
    public async Task BotChat_TheLowerRulesExtras_UnderAReply_RunsWhenTheReplyEnds()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        await embedded.StartExtraAsync(EmbeddedModelCatalog.Find("gemma-4-e2b")!, new AppSettingsData(), ExtraOwner.BotChat, null, CancellationToken.None);
        int stillRunning = -1;
        ScriptedInput? keys = null;
        _chat.EnqueueText("Hello ", "there.");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 1 && _chat.Requests.Count == 1)
            {
                keys!.PushClick(238, 101);
                keys.PushClick(238, 101);
                for (int tries = 0; tries < 500 && keys.IsAvailable; tries++)
                {
                    await Task.Delay(10, CancellationToken.None);
                }

                stillRunning = embedded.Extras.Count;
            }
        };
        var input = WhenIdle(i => PushLine(i, "hi"), i => PushLine(i, "/exit"));
        keys = input;

        string output = await RunAsync(input);

        Assert.Equal(1, stillRunning);   // held: not mid-reply
        Assert.Contains(ChatScreen.MidTurnDeferredNotice(SlashCommands.FirstWord(BotChat.KillLine)), output);
        Assert.True(output.IndexOf("there.", StringComparison.Ordinal) < output.IndexOf(BotChat.ExtrasStoppedNotice(["Gemma 4 E2B"]), StringComparison.Ordinal));
        Assert.Empty(embedded.Extras);
    }

    /// <summary>Ctrl+Alt+K (2026-10-11, the user's ask) is <c>/botchat --kill</c> from the keyboard: the extras stopped, then none.</summary>
    [Fact]
    public async Task BotChat_CtrlAltK_StopsTheExtras_AsTheTypedKillDoes()
    {
        _settings.Update(d => d.TtsOutput = false);
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        await embedded.StartExtraAsync(EmbeddedModelCatalog.Find("gemma-4-e2b")!, new AppSettingsData(), ExtraOwner.BotChat, null, CancellationToken.None);
        var input = WhenIdle(i =>
        {
            i.Push(Keys.CtrlAlt(ConsoleKey.K));
            i.Push(Keys.CtrlAlt(ConsoleKey.K));
            PushLine(i, "/exit");
        });

        string output = await RunAsync(input);

        Assert.Contains(BotChat.ExtrasStoppedNotice(["Gemma 4 E2B"]), output);
        Assert.Contains(BotChat.NoExtrasNotice, output);
        Assert.Empty(embedded.Extras);
        Assert.Empty(_chat.Requests);
    }
}
