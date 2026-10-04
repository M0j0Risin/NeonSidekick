using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm.OpenAIPlatform;

/// <summary>
/// What one OpenAI model takes (2026-10-03, from OpenAI's model pages that day; <see cref="Anthropic.ClaudeModelRules"/>'s
/// twin). The families differ in ways that 400 a request, so the <c>LLM reasoning</c> levels are shaped per model id:
/// <list type="bullet">
/// <item>No reasoning (GPT-4.1, GPT-4o, chatgpt-4o): no <c>reasoning_effort</c> at all — the field is a 400 there.</item>
/// <item>GPT-5, 5-mini, 5-nano: <c>minimal</c> is their lowest word; no <c>xhigh</c>.</item>
/// <item>GPT-5.1: <c>none</c>; no <c>xhigh</c>. GPT-5.2 on (5.4, 5.5, 5.6, GPT-6 Luna): <c>none</c> and <c>xhigh</c>.</item>
/// <item>GPT-6 Astra, Sol and 6.1 Sol: no <c>none</c> (a 400), so it becomes <c>low</c>; <c>xhigh</c> exists.</item>
/// <item>o1, o3, o3-mini, o4-mini: <c>low</c> to <c>high</c>.</item>
/// </list>
/// <c>default</c> sends no effort and the model's own applies. A model id this table does not know is taken as the newest
/// kind — reasoning, <c>none</c> refused, <c>xhigh</c> there — the shape a future model is most likely to accept.
/// </summary>
/// <param name="Reasons">Whether the model takes <c>reasoning_effort</c> at all.</param>
/// <param name="NoneWord">The wire word of the <c>none</c> level (<c>none</c> or <c>minimal</c>), or null where the model has neither and the lowest level stands in.</param>
/// <param name="SupportsExtraHigh">Whether <c>xhigh</c> exists; else it is sent as <c>high</c>.</param>
/// <param name="LowestWord">The lowest effort the model takes, what <c>none</c> becomes where <paramref name="NoneWord"/> is null.</param>
public sealed record OpenAIModelRules(bool Reasons, string? NoneWord, bool SupportsExtraHigh, string LowestWord = "low")
{
    private static readonly OpenAIModelRules NoReasoning = new(false, null, false);

    /// <summary>The rules for <paramref name="modelId"/> (a full id, dated or not). Matched by prefix, most specific first.</summary>
    public static OpenAIModelRules For(string? modelId)
    {
        string id = Normal(modelId);
        if (StartsWithAny(id, "gpt-4", "chatgpt-", "gpt-3"))
        {
            return NoReasoning;
        }

        if (StartsWithAny(id, "o1", "o3", "o4"))
        {
            return new OpenAIModelRules(true, null, false);
        }

        if (StartsWithAny(id, "gpt-6-luna", "gpt-5.6", "gpt-5.5", "gpt-5.4", "gpt-5.3", "gpt-5.2"))
        {
            return new OpenAIModelRules(true, "none", true);
        }

        if (id.StartsWith("gpt-5.1", StringComparison.Ordinal))
        {
            return new OpenAIModelRules(true, "none", false);
        }

        if (id.StartsWith("gpt-5", StringComparison.Ordinal))
        {
            return new OpenAIModelRules(true, "minimal", false, LowestWord: "minimal");
        }

        return new OpenAIModelRules(true, null, true);
    }

    /// <summary>
    /// The <c>reasoning_effort</c> word for <paramref name="effort"/> on this model, or null to send none: a model that does
    /// not reason, or no level asked (the model's own default then).
    /// </summary>
    public string? EffortWord(ReasoningEffort? effort)
    {
        if (!Reasons || effort is not { } level)
        {
            return null;
        }

        return level switch
        {
            ReasoningEffort.None => NoneWord ?? LowestWord,
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => SupportsExtraHigh ? "xhigh" : "high",
            _ => null,
        };
    }

    /// <summary>
    /// Whether <paramref name="modelId"/> is a chat model the app can use, so <c>/server</c> and <c>/model</c> list it: the GPT and
    /// o-series text models, not the audio, realtime, speech, transcription, image, video, search, embedding, moderation or
    /// legacy-completion ones, nor the <c>-pro</c> and <c>codex</c> models (left out of the live sweep of 2026-10-03: the first take minutes and many times the price per reply,
    /// the second are coding-agent models), nor the retired <c>-chat-latest</c> ids and <c>gpt-live</c> the list still names — the account's list holds them all.
    /// </summary>
    public static bool IsChatModel(string? modelId)
    {
        string id = Normal(modelId);
        if (!(StartsWithAny(id, "gpt-", "chatgpt-") || (id.Length > 1 && id[0] == 'o' && char.IsAsciiDigit(id[1]))))
        {
            return false;
        }

        foreach (string word in ExcludedWords)
        {
            if (id.Contains(word, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return !id.EndsWith("-pro", StringComparison.Ordinal) && !id.Contains("-pro-", StringComparison.Ordinal)
            && !id.EndsWith("-chat-latest", StringComparison.Ordinal);   // listed but retired: a 404 "deprecated" (the live sweep, 2026-10-03)
    }

    private static readonly string[] ExcludedWords =
        ["audio", "realtime", "gpt-live", "tts", "transcribe", "image", "dall-e", "whisper", "sora", "search", "embedding", "moderation", "instruct", "codex", "computer-use", "deep-research", "babbage", "davinci", "gpt-3.5"];

    /// <summary>
    /// The prompt a model takes, in tokens, for the context window the app compacts against (the API's <c>/v1/models</c> names
    /// none): the model page's maximum input where it gives one (922,000 for GPT-6 and 5.6), else its context window. A model
    /// the table does not know gets <see cref="FallbackContextWindow"/>, the smallest of the current families.
    /// </summary>
    public static int ContextWindow(string? modelId)
    {
        string id = Normal(modelId);
        foreach (var (prefix, window) in ContextTable)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                return window;
            }
        }

        return FallbackContextWindow;
    }

    /// <summary>The window of a model <see cref="ContextWindow"/> does not name.</summary>
    public const int FallbackContextWindow = 128_000;

    private static readonly (string Prefix, int Window)[] ContextTable =
    [
        ("gpt-6", 922_000),
        ("gpt-5.6", 922_000),
        ("gpt-5.5", 1_050_000),
        ("gpt-5.4-mini", 400_000),
        ("gpt-5.4-nano", 400_000),
        ("gpt-5.4", 1_050_000),
        ("gpt-5", 400_000),
        ("gpt-4.1", 1_047_576),
        ("gpt-4o", 128_000),
        ("chatgpt-4o", 128_000),
        ("o1", 200_000),
        ("o3", 200_000),
        ("o4", 200_000),
    ];

    private static string Normal(string? modelId) => (modelId ?? "").Trim().ToLowerInvariant();

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

/// <summary>
/// What an OpenAI model costs, US dollars per million tokens (2026-10-03, OpenAI's Standard-tier list prices that day — the
/// user's call: a table in the code, <c>/usage</c> in dollars as for the Claude API; a model it does not name shows tokens
/// only). A cache read is the model's cached-input price; a cache write is billed (1.25 times the input) only from GPT-5.6
/// on, and is zero here before. The 5.4 / 5.5 / 5.6 / 6 families double the input (and add half the output) for a prompt
/// over <see cref="LongContextThreshold"/> tokens: the long figures, where there are some.
/// </summary>
/// <param name="Input">Uncached prompt tokens.</param>
/// <param name="CachedInput">Prompt tokens read from the cache (the input price where the model has no cached price).</param>
/// <param name="Output">Completion tokens, reasoning included.</param>
/// <param name="CacheWrite">Prompt tokens written to the cache (GPT-5.6 on), else 0.</param>
/// <param name="Long">The long-context prices, or null where the model has one tier.</param>
public sealed record OpenAIPrice(decimal Input, decimal CachedInput, decimal Output, decimal CacheWrite = 0m, OpenAIPrice? Long = null)
{
    /// <summary>The prompt size above which the long-context prices apply.</summary>
    public const long LongContextThreshold = 272_000;

    /// <summary>The price table, most specific prefix first.</summary>
    private static readonly (string Prefix, OpenAIPrice Price)[] Table =
    [
        ("gpt-6-astra", new(10m, 1m, 50m, 12.5m, new(20m, 2m, 75m, 25m))),
        ("gpt-6.1-sol", new(2m, 0.10m, 10m, 2.5m, new(4m, 0.20m, 15m, 5m))),
        ("gpt-6-sol", new(2m, 0.20m, 10m, 2.5m, new(4m, 0.40m, 15m, 5m))),
        ("gpt-6-luna", new(0.10m, 0.01m, 0.50m, 0.125m, new(0.20m, 0.02m, 0.75m, 0.25m))),
        ("gpt-5.6-sol", new(4m, 0.40m, 20m, 5m, new(8m, 0.80m, 30m, 10m))),
        ("gpt-5.6-terra", new(2m, 0.20m, 12m, 2.5m, new(4m, 0.40m, 18m, 5m))),
        ("gpt-5.6-luna", new(0.20m, 0.02m, 1.20m, 0.25m, new(0.40m, 0.04m, 1.80m, 0.50m))),
        ("gpt-5.5", new(5m, 0.50m, 30m, Long: new(10m, 1m, 45m))),
        ("gpt-5.4-mini", new(0.75m, 0.075m, 4.50m)),
        ("gpt-5.4-nano", new(0.20m, 0.02m, 1.25m)),
        ("gpt-5.4", new(2.50m, 0.25m, 15m, Long: new(5m, 0.50m, 22.50m))),
        ("gpt-5.2", new(1.75m, 0.175m, 14m)),
        ("gpt-5.1", new(1.25m, 0.125m, 10m)),
        ("gpt-5-mini", new(0.25m, 0.025m, 2m)),
        ("gpt-5-nano", new(0.05m, 0.005m, 0.40m)),
        ("gpt-5", new(1.25m, 0.125m, 10m)),
        ("gpt-4.1-mini", new(0.40m, 0.10m, 1.60m)),
        ("gpt-4.1-nano", new(0.10m, 0.025m, 0.40m)),
        ("gpt-4.1", new(2m, 0.50m, 8m)),
        ("gpt-4o-mini", new(0.15m, 0.075m, 0.60m)),
        ("gpt-4o", new(2.50m, 1.25m, 10m)),
        ("o1", new(15m, 7.50m, 60m)),
        ("o3-mini", new(1.10m, 0.55m, 4.40m)),
        ("o3", new(2m, 0.50m, 8m)),
        ("o4-mini", new(1.10m, 0.275m, 4.40m)),
    ];

    /// <summary>The price of <paramref name="modelId"/>, or null for a model the table does not name.</summary>
    public static OpenAIPrice? For(string? modelId)
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

    /// <summary>
    /// What one request cost: <paramref name="prompt"/> prompt tokens in all (the API's <c>prompt_tokens</c>), of them
    /// <paramref name="cacheRead"/> read from and <paramref name="cacheWrite"/> written to the cache, and <paramref name="output"/>
    /// completion tokens. The long prices apply to the whole request once the prompt passes the threshold.
    /// </summary>
    public decimal Cost(long prompt, long cacheRead, long cacheWrite, long output)
    {
        var tier = prompt > LongContextThreshold && Long is { } longer ? longer : this;
        long uncached = Math.Max(0, prompt - cacheRead - cacheWrite);
        return (uncached * tier.Input + cacheRead * tier.CachedInput + cacheWrite * (tier.CacheWrite > 0 ? tier.CacheWrite : tier.Input) + output * tier.Output) / 1_000_000m;
    }
}
