using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests;

/// <summary>
/// The real Anthropic API for the live facts (2026-09-27): opt-in — every run is billed to the key — with
/// <see cref="Variable"/> set to a key. A local gate, never something CI relies on.
/// </summary>
internal static class LiveClaudeApi
{
    public const string Variable = "NEONSIDEKICK_TEST_CLAUDE_API_KEY";

    public static readonly string? Key = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } key ? key : null;
}

/// <summary>A fact that runs only with an Anthropic API key in <see cref="LiveClaudeApi.Variable"/>.</summary>
public sealed class LiveClaudeApiFactAttribute : FactAttribute
{
    public LiveClaudeApiFactAttribute()
    {
        if (LiveClaudeApi.Key is null)
        {
            Skip = $"Set {LiveClaudeApi.Variable} to an Anthropic API key to run the live Anthropic API facts (each run is billed).";
        }
    }
}

public class LiveClaudeApiTests
{
    /// <summary>
    /// The preserved-thinking proof (2026-09-27): a turn with a tool call and thinking, a second turn, then the first
    /// turn dropped (what the turn cap and a compact do to the history) and a third — every request accepted, on a
    /// model whose thinking is always on and one that can switch it off.
    /// </summary>
    [LiveClaudeApiFact]
    public async Task ATurnWithATool_ThenATrimmedHistory_IsAccepted()
    {
        foreach (var (model, effort) in new[] { ("claude-sonnet-5", ReasoningEffort.Medium), ("claude-opus-5-5", ReasoningEffort.Low) })
        {
            using var client = new AnthropicChatClient(new LlmEndpoint(ClaudeApi.BaseUrl, model, LiveClaudeApi.Key!, "live"), TimeSpan.FromMinutes(3), 8_000, promptCaching: true);
            var clock = new GetCurrentTimeTool(TimeProvider.System);
            var options = new ChatOptions { Tools = [clock], Reasoning = new ReasoningOptions { Effort = effort } };
            var history = new List<ChatMessage> { new(ChatRole.System, "You are terse."), new(ChatRole.User, "What time is it in UTC? Use the tool.") };

            // Turn 1: the call, its result, the answer.
            var first = await client.GetResponseAsync(history, options);
            history.AddRange(first.Messages);
            var call = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().FirstOrDefault();
            Assert.NotNull(call);
            var result = await clock.InvokeAsync(new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>()));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result is JsonElement e ? e.GetRawText() : result?.ToString())]));
            var answer = await client.GetResponseAsync(history, options);
            history.AddRange(answer.Messages);
            Assert.False(string.IsNullOrWhiteSpace(answer.Text));

            // Turn 2.
            history.Add(new ChatMessage(ChatRole.User, "And in Tokyo? One line."));
            var second = await client.GetResponseAsync(history, options);
            history.AddRange(second.Messages);

            // The first turn trimmed away, then turn 3.
            int turnTwo = history.FindLastIndex(m => m.Role == ChatRole.User && m.Text.StartsWith("And in Tokyo", StringComparison.Ordinal));
            var trimmed = new List<ChatMessage> { history[0] };
            trimmed.AddRange(history.Skip(turnTwo));
            trimmed.Add(new ChatMessage(ChatRole.User, "Thanks. Say done."));
            var third = await client.GetResponseAsync(trimmed, options);
            Assert.False(string.IsNullOrWhiteSpace(third.Text));
            Assert.NotNull(third.Usage?.InputTokenCount);
        }
    }
}
