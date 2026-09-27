using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm.Anthropic;

/// <summary>
/// How one Claude model takes thinking (2026-09-27). The families differ in ways that 400 a request, so the five
/// <c>LLM reasoning</c> levels are shaped per model id rather than sent as one field:
/// <list type="bullet">
/// <item><see cref="ThinkingFamily.Adaptive"/> (Opus 4.6 on, Sonnet 4.6 on, Fable, Mythos): <c>thinking: adaptive</c>
/// and <c>output_config.effort</c>. <c>budget_tokens</c> is refused on most of them.</item>
/// <item><see cref="ThinkingFamily.Budget"/> (Haiku 4.5, Sonnet 4.5, Opus 4.5 and older): <c>thinking: enabled</c>
/// with a token budget; <c>effort</c> is refused on most.</item>
/// </list>
/// <c>none</c> disables thinking where the model allows it (<see cref="CanDisableThinking"/>); Opus 5.5, Fable and
/// Mythos refuse <c>thinking: disabled</c> at every effort, so there it becomes the lowest effort instead. A model id
/// this table does not know is taken as the newest kind — adaptive, thinking always on — which is the shape a
/// future model is most likely to accept.
/// </summary>
/// <param name="Family">How thinking is asked for.</param>
/// <param name="CanDisableThinking">Whether <c>thinking: disabled</c> is accepted.</param>
/// <param name="SupportsExtraHigh">Whether the <c>xhigh</c> effort exists (Opus 4.7 on); else <c>xhigh</c> is sent as <c>high</c>.</param>
/// <param name="SupportsDisplay">Whether <c>thinking.display</c> is sent; <c>summarized</c> is what makes the thinking readable in the transcript.</param>
public sealed record ClaudeModelRules(ThinkingFamily Family, bool CanDisableThinking, bool SupportsExtraHigh, bool SupportsDisplay)
{
    /// <summary>The rules for <paramref name="modelId"/> (a full id, dated or not). Matched by prefix, most specific first.</summary>
    public static ClaudeModelRules For(string? modelId)
    {
        string id = (modelId ?? "").Trim().ToLowerInvariant();

        // The budget family: thinking by budget_tokens, no effort field.
        if (StartsWithAny(id, "claude-haiku-4-5", "claude-sonnet-4-5", "claude-opus-4-5", "claude-opus-4-1", "claude-opus-4-0", "claude-sonnet-4-0", "claude-opus-4-2", "claude-sonnet-4-2", "claude-3"))
        {
            return new ClaudeModelRules(ThinkingFamily.Budget, CanDisableThinking: true, SupportsExtraHigh: false, SupportsDisplay: false);
        }

        // 4.6: adaptive, but no xhigh yet, and its thinking is summarised by default (display left alone).
        if (StartsWithAny(id, "claude-opus-4-6", "claude-sonnet-4-6"))
        {
            return new ClaudeModelRules(ThinkingFamily.Adaptive, CanDisableThinking: true, SupportsExtraHigh: false, SupportsDisplay: false);
        }

        // Thinking always on: disabled is a 400.
        if (StartsWithAny(id, "claude-opus-5-5", "claude-fable", "claude-mythos"))
        {
            return new ClaudeModelRules(ThinkingFamily.Adaptive, CanDisableThinking: false, SupportsExtraHigh: true, SupportsDisplay: true);
        }

        // Opus 4.7 / 4.8, Opus 5 (disabled accepted at the default effort, which is what none sends), Sonnet 5.
        if (StartsWithAny(id, "claude-opus-4-7", "claude-opus-4-8", "claude-opus-5", "claude-sonnet-5"))
        {
            return new ClaudeModelRules(ThinkingFamily.Adaptive, CanDisableThinking: true, SupportsExtraHigh: true, SupportsDisplay: true);
        }

        return new ClaudeModelRules(ThinkingFamily.Adaptive, CanDisableThinking: false, SupportsExtraHigh: true, SupportsDisplay: true);
    }

    /// <summary>The <c>budget_tokens</c> a <see cref="ThinkingFamily.Budget"/> model gets per level, before the clamp under <c>max_tokens</c>.</summary>
    public static int BudgetFor(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Low => 2_048,
        ReasoningEffort.Medium => 6_144,
        ReasoningEffort.High => 12_288,
        _ => 24_576,
    };

    /// <summary>The smallest <c>budget_tokens</c> the API accepts.</summary>
    public const int MinBudget = 1_024;

    /// <summary>The wire word of a level for <c>output_config.effort</c> on this model: <c>xhigh</c> falls to <c>high</c> where it does not exist.</summary>
    public string EffortWord(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.ExtraHigh => SupportsExtraHigh ? "xhigh" : "high",
        _ => "low",
    };

    private static bool StartsWithAny(string id, params string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>How a Claude model is asked to think.</summary>
public enum ThinkingFamily
{
    /// <summary><c>thinking: {type: "adaptive"}</c> plus <c>output_config.effort</c>.</summary>
    Adaptive,

    /// <summary><c>thinking: {type: "enabled", budget_tokens: N}</c>.</summary>
    Budget,
}

/// <summary>
/// What a Claude model costs, US dollars per million tokens (2026-09-27, Anthropic's first-party list prices as of
/// that day — the user's call: a table in the code, <c>/usage</c> in dollars; a model it does not name shows tokens
/// only). A cache write (five minutes) is 1.25 times the input price; a cache read is the model's own figure where it
/// has one, else a tenth of the input price.
/// </summary>
/// <param name="Input">Uncached prompt tokens.</param>
/// <param name="Output">Completion tokens, thinking included.</param>
/// <param name="CacheRead">Prompt tokens read from the cache.</param>
public sealed record ClaudePrice(decimal Input, decimal Output, decimal CacheRead)
{
    /// <summary>The price table, most specific prefix first.</summary>
    private static readonly (string Prefix, ClaudePrice Price)[] Table =
    [
        ("claude-fable-5-1", new ClaudePrice(10m, 50m, 0.25m)),
        ("claude-mythos-5-1", new ClaudePrice(10m, 50m, 0.25m)),
        ("claude-fable", new ClaudePrice(10m, 50m, 1m)),
        ("claude-mythos", new ClaudePrice(10m, 50m, 1m)),
        ("claude-opus-5-5", new ClaudePrice(4m, 20m, 0.20m)),
        ("claude-opus-5", new ClaudePrice(5m, 25m, 0.50m)),
        ("claude-opus-4-8", new ClaudePrice(5m, 25m, 0.50m)),
        ("claude-opus-4-7", new ClaudePrice(5m, 25m, 0.50m)),
        ("claude-opus-4-6", new ClaudePrice(5m, 25m, 0.50m)),
        ("claude-opus-4-5", new ClaudePrice(5m, 25m, 0.50m)),
        ("claude-opus-4", new ClaudePrice(15m, 75m, 1.50m)),
        ("claude-sonnet-5", new ClaudePrice(2m, 10m, 0.20m)),
        ("claude-sonnet-4", new ClaudePrice(3m, 15m, 0.30m)),
        ("claude-haiku-4-5", new ClaudePrice(1m, 5m, 0.10m)),
        ("claude-3-5-haiku", new ClaudePrice(0.8m, 4m, 0.08m)),
        ("claude-3-haiku", new ClaudePrice(0.25m, 1.25m, 0.03m)),
    ];

    /// <summary>The price of <paramref name="modelId"/>, or null for a model the table does not name.</summary>
    public static ClaudePrice? For(string? modelId)
    {
        string id = (modelId ?? "").Trim().ToLowerInvariant();
        foreach (var (prefix, price) in Table)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                return price;
            }
        }

        return null;
    }

    /// <summary>A cache write's price per million: 1.25 times the input's.</summary>
    public decimal CacheWrite => Input * 1.25m;

    /// <summary>What one request cost: <paramref name="uncached"/> input, <paramref name="cacheWrite"/> and <paramref name="cacheRead"/> prompt tokens, <paramref name="output"/> completion tokens.</summary>
    public decimal Cost(long uncached, long cacheWrite, long cacheRead, long output) =>
        (uncached * Input + cacheWrite * CacheWrite + cacheRead * CacheRead + output * Output) / 1_000_000m;
}
