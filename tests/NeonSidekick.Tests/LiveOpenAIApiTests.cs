using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.OpenAIPlatform;
using NeonSidekick.Llm.Tools;
using Xunit.Abstractions;

namespace NeonSidekick.Tests;

/// <summary>
/// The real OpenAI API for the live facts (2026-10-03): opt-in — every run is billed to the key — with <see cref="Variable"/>
/// set to a key. <see cref="SweepVariable"/> set too runs the reasoning sweep over every chat model the account lists. A local
/// gate, never something CI relies on.
/// </summary>
internal static class LiveOpenAIApi
{
    public const string Variable = "NEONSIDEKICK_TEST_OPENAI_API_KEY";

    public const string SweepVariable = "NEONSIDEKICK_TEST_OPENAI_SWEEP";

    public static readonly string? Key = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } key ? key : null;

    public static bool Sweep => Environment.GetEnvironmentVariable(SweepVariable) is { Length: > 0 };

    /// <summary>The account's chat models, as <c>/server</c> lists them.</summary>
    public static async Task<IReadOnlyList<string>> ModelsAsync()
    {
        using var http = new HttpClient();
        var result = await new LlmEndpointProbe(http).ProbeAsync(OpenAIApi.BaseUrl, Key, CancellationToken.None);
        Assert.True(result.Exists, result.Detail);
        return result.ModelIds;
    }

    public static OpenAIApiChatClient Client(string model, int maxTokens = 0) =>
        new(new LlmEndpoint(OpenAIApi.BaseUrl, model, Key!, "live"), TimeSpan.FromMinutes(3), maxTokens);
}

/// <summary>A fact that runs only with an OpenAI API key in <see cref="LiveOpenAIApi.Variable"/>.</summary>
public sealed class LiveOpenAIApiFactAttribute : FactAttribute
{
    public LiveOpenAIApiFactAttribute()
    {
        if (LiveOpenAIApi.Key is null)
        {
            Skip = $"Set {LiveOpenAIApi.Variable} to an OpenAI API key to run the live OpenAI API facts (each run is billed).";
        }
    }
}

/// <summary>A fact that also needs <see cref="LiveOpenAIApi.SweepVariable"/>: it asks every listed model, a few cents a run.</summary>
public sealed class LiveOpenAIApiSweepFactAttribute : FactAttribute
{
    public LiveOpenAIApiSweepFactAttribute()
    {
        if (LiveOpenAIApi.Key is null || !LiveOpenAIApi.Sweep)
        {
            Skip = $"Set {LiveOpenAIApi.Variable} and {LiveOpenAIApi.SweepVariable}=1 to sweep every listed model's reasoning shapes (billed).";
        }
    }
}

public class LiveOpenAIApiTests(ITestOutputHelper output)
{
    /// <summary>The cheap models the tool turn runs on, each where the account lists it: the reasoning shapes <c>minimal</c>, <c>none</c>, the lowest and no effort at all.</summary>
    private static readonly string[] CheapModels = ["gpt-5-nano", "gpt-5.4-nano", "gpt-6-luna", "gpt-4.1-nano", "o4-mini"];

    [LiveOpenAIApiFact]
    public async Task TheModelList_IsChatModels()
    {
        var models = await LiveOpenAIApi.ModelsAsync();
        output.WriteLine(string.Join(", ", models));
        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.True(OpenAIModelRules.IsChatModel(m), m));
    }

    /// <summary>A tool call, its result and the answer, then a second turn, streamed, at the lowest and the highest level.</summary>
    [LiveOpenAIApiFact]
    public async Task ATurnWithATool_IsAccepted_AndReportsUsageAndCost()
    {
        var listed = await LiveOpenAIApi.ModelsAsync();
        var models = CheapModels.Where(listed.Contains).ToList();
        Assert.NotEmpty(models);
        foreach (string model in models)
        {
            foreach (var effort in new[] { ReasoningEffort.None, ReasoningEffort.High })
            {
                using var client = LiveOpenAIApi.Client(model);
                var clock = new GetCurrentTimeTool(TimeProvider.System);
                var options = new ChatOptions { Tools = [clock], Reasoning = new ReasoningOptions { Effort = effort }, Temperature = 0.4f };
                var history = new List<ChatMessage> { new(ChatRole.System, "You are terse."), new(ChatRole.User, "What time is it in UTC? Use the tool.") };

                var first = await client.GetResponseAsync(history, options);
                history.AddRange(first.Messages);
                var call = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().FirstOrDefault();
                Assert.True(call is not null, $"{model} at {effort} called no tool: {first.Text}");
                var result = await clock.InvokeAsync(new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>()));
                history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result is JsonElement e ? e.GetRawText() : result?.ToString())]));

                UsageDetails? usage = null;
                var text = new System.Text.StringBuilder();
                await foreach (var update in client.GetStreamingResponseAsync(history, options))
                {
                    text.Append(update.Text);
                    usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
                }

                Assert.False(string.IsNullOrWhiteSpace(text.ToString()), $"{model} at {effort} answered nothing");
                Assert.NotNull(usage?.InputTokenCount);
                var tokens = TokenUsage.From(usage!, TimeSpan.Zero, TimeSpan.Zero);
                output.WriteLine($"{model} {effort}: {tokens.Input} in ({tokens.CacheRead} cached), {tokens.Output} out ({tokens.Reasoning} reasoning), ${tokens.CostUsd}");
                Assert.Equal(OpenAIPrice.For(model) is not null, tokens.CostUsd is not null);
            }
        }
    }

    [LiveOpenAIApiFact]
    public async Task TheOutputCap_IsSentAndKept()
    {
        var listed = await LiveOpenAIApi.ModelsAsync();
        string model = CheapModels.First(listed.Contains);
        using var client = LiveOpenAIApi.Client(model, maxTokens: 1024);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Count from 1 to 5000, one number per line.")], new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } });

        Assert.True(response.Usage?.OutputTokenCount <= 1024, $"{response.Usage?.OutputTokenCount} tokens");
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
    }

    /// <summary>Every listed chat model at the lowest and highest level: the rules' words are accepted (a 400 is the failure).</summary>
    [LiveOpenAIApiSweepFact]
    public async Task EveryListedModel_TakesItsReasoningWords()
    {
        var failures = new List<string>();
        foreach (string model in await LiveOpenAIApi.ModelsAsync())
        {
            foreach (var effort in new ReasoningEffort?[] { null, ReasoningEffort.None, ReasoningEffort.ExtraHigh })
            {
                using var client = LiveOpenAIApi.Client(model, maxTokens: 1024);
                try
                {
                    await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Say hi.")], new ChatOptions { Reasoning = effort is { } level ? new ReasoningOptions { Effort = level } : null });
                    output.WriteLine($"ok   {model} {effort?.ToString() ?? "default"} → {OpenAIModelRules.For(model).EffortWord(effort) ?? "(none sent)"}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{model} {effort}: {ex.Message}");
                    output.WriteLine($"FAIL {model} {effort?.ToString() ?? "default"}: {ex.Message}");
                }
            }
        }

        Assert.Empty(failures);
    }
}
