using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

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
        await embedded.StartExtraAsync(EmbeddedModelCatalog.Find("gemma-4-e2b")!, new AppSettingsData(), null, CancellationToken.None);
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
}
