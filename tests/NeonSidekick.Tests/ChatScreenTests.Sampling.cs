using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/sampling</c> on the screen (2026-09-28): the connected model's values set by the line, carried by the next turn; the completion.</summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task Sampling_TheLineSetsTheConnectedModels_AndTheNextTurnCarriesIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("One.");
        _chat.EnqueueText("Two.");
        PushLine("hi");
        PushLine("/sampling temperature 0.6");
        PushLine("/sampling top_k 20");
        PushLine("again");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Null(OpenAICompatibleChatClient.SamplingOf(_chat.Options[0]));
        var sampling = OpenAICompatibleChatClient.SamplingOf(_chat.Options[^1]);
        Assert.NotNull(sampling);
        Assert.Equal(0.6, sampling.Temperature);
        Assert.Equal(20, sampling.TopK);
        Assert.Equal(0.6f, _chat.Options[^1]!.Temperature);
        var entry = Assert.Single(_settings.Current.LlmSampling!);
        Assert.Equal(0.6, entry.Value.Temperature);
        Assert.Contains("Sampling for " + entry.Key + ": temperature 0.6", output);
    }

    [Fact]
    public void Sampling_CompletesTheFieldNames_ThenExtraAndClear()
    {
        var items = ChatScreen.ArgumentItems("/sampling", "", Sources());
        Assert.Equal([.. SamplingField.All.Select(f => f.Wire), SamplingText.ExtraWord, SamplingText.ClearWord], items.Select(i => i.Text));
        Assert.Equal([new CompletionItem("top_p", "above 0, up to 1"), new CompletionItem("top_k", "a whole number from -1 to 100000")], ChatScreen.ArgumentItems("/sampling", "top", Sources()));
    }
}
