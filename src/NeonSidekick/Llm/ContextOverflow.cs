using System.ClientModel;
using System.Globalization;
using System.Text.RegularExpressions;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Llm.OpenAIPlatform;

namespace NeonSidekick.Llm;

/// <summary>
/// A server's "the request is too long" answer, read from its error (2026-11-09, a turn that ran the context window out before the guard
/// fired): <paramref name="Matched"/> names the pattern that matched, a short fixed word for the log (never the server's own message,
/// <see cref="ContextOverflowMatch"/>'s label consts), and the figures it gave, each null when not given — the tokens sent, the server's
/// limit, and the tokens the request asks for the reply (vLLM's <c>max_tokens</c> default, Anthropic's <c>max_tokens</c>, TGI's
/// <c>max_new_tokens</c>). The mid-turn guard shrinks the history and sends the round again (<see cref="Assistant.ContextGuard"/>).
/// </summary>
public sealed record ContextOverflow(string Matched, long? Sent, long? Limit, long? Output)
{
    /// <summary>
    /// What the server counted against its limit (2026-11-10, review P5-6/P7-2): the tokens sent plus the reply asked for when both are
    /// known (the server's own test is prompt + <c>max_tokens</c> ≤ window), else the tokens sent, else null.
    /// </summary>
    public long? Total => Sent is { } sent && Output is { } output ? sent + output : Sent;

    /// <summary>
    /// The figures for a notice, with a leading space: <c> (41,200 of 32,768 tokens)</c> (<see cref="Total"/> of <see cref="Limit"/>), or empty
    /// unless both are known (2026-11-10, review P3-9: one place, so a sum under the limit never reads as "too long").
    /// </summary>
    public string Figures => Total is { } total && Limit is { } limit ? string.Create(CultureInfo.InvariantCulture, $" ({total:N0} of {limit:N0} tokens)") : "";

    /// <summary>
    /// Whether the reply the request asks for is what blocks a retry (2026-11-10, review P2-7: the retry gate's and the final error's rule
    /// in one place): <see cref="Output"/> at half the limit or more — <see cref="Limit"/>, else <paramref name="window"/> — since a shrink of the
    /// history can't make room for a reply that large.
    /// </summary>
    public bool ReplyBlocks(long? window) => Output is { } output && (Limit ?? window) is { } limit && output * 2 >= limit;
}

/// <summary>
/// Reads a failed request as a context overflow (2026-11-09): the server's own fields of a <see cref="ClientResultException"/>'s raw body
/// (<see cref="ErrorBody"/>, 2026-11-10, review P7-1: never the whole body, so an echoed prompt or model output can't read as an overflow;
/// the raw text only when the body is no JSON or has none of those fields), an <see cref="AnthropicApiException"/>'s and an
/// <see cref="OpenAIApiException"/>'s type and message. Any 4xx or 5xx status counts (LM Studio wraps llama-server's error in a 500), and 0
/// (a stream's <c>error</c> event). The Claude Code clients' <see cref="InvalidOperationException"/> is never read: Claude compacts its own
/// conversation. The texts per server were checked against their sources on 2026-11-09 and 2026-11-10 (vLLM 0.20–0.33, SGLang, TGI,
/// Mistral, Gemini's OpenAI-compatible endpoint, OpenAI with tools, OpenRouter, LM Studio); Fireworks' and xAI's couldn't be (P14-1).
/// Every pattern is linear; numbers are kept only between 1 and <see cref="int.MaxValue"/>.
/// </summary>
public static partial class ContextOverflowMatch
{
    /// <summary>The error code OpenAI, TabbyAPI and OpenAI-shaped proxies send for an overflow.</summary>
    public const string OpenAICode = "context_length_exceeded";

    /// <summary>llama-server's error type for an overflow (its <c>code</c> is the number 400, so this is what <c>error.type</c> gives).</summary>
    public const string LlamaCode = "exceed_context_size_error";

    // What matched, for the log (2026-11-10, review P15-2: named once). Pinned by the matcher's tests.
    public const string LlamaFields = "llama-server fields";
    public const string LlamaText = "llama-server text";
    public const string LmStudioFields = "LM Studio fields";
    public const string LmStudioText = "LM Studio text";
    public const string TabbyText = "TabbyAPI text";
    public const string VllmText = "vLLM text";
    public const string OpenAIText = "OpenAI-style text";
    public const string OpenRouterText = "OpenRouter text";
    public const string SgLangText = "SGLang text";
    public const string TgiText = "TGI text";
    public const string MistralText = "Mistral text";
    public const string GeminiText = "Gemini text";
    public const string AnthropicText = "Anthropic text";
    public const string OpenAICodeLabel = OpenAICode + " code";

    /// <summary>The overflow <paramref name="ex"/> (or an exception inside it, <see cref="Assistant.ExceptionChain"/>) reports, or null when it is none.</summary>
    public static ContextOverflow? From(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        foreach (var current in Assistant.ExceptionChain(ex))
        {
            var found = current switch
            {
                ClientResultException { Status: 0 or >= 400 } client => Read(client.GetRawResponse()?.Content?.ToString() is { Length: > 0 } body ? body : client.Message, null),
                AnthropicApiException anthropic => Read(anthropic.Message, anthropic.ErrorType),
                OpenAIApiException openAI => Read(openAI.Message, openAI.ErrorType),
                _ => null,
            };

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="text"/> (an error body or message) reads as a context overflow (2026-11-10, review P11-6/P4-2: <see cref="Read"/>
    /// itself, through the same field reader, so the two API clients' refusal exclusions and the matcher judge the same text). The shared test
    /// the two clients use before their own stripped retries, so an overflow isn't read as a thinking refusal.
    /// </summary>
    public static bool IsOverflowText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Read(text, null) is not null;
    }

    /// <summary>The overflow <paramref name="text"/> reports, the error <paramref name="code"/> beside it when the exception carries one.</summary>
    internal static ContextOverflow? Read(string text, string? code)
    {
        ArgumentNullException.ThrowIfNull(text);
        var body = ErrorBody.Parse(text);
        string said = body?.Text is { Length: > 0 } own ? own : text;
        code ??= body?.Code ?? body?.Type;

        // llama-server and TabbyAPI: "exceeds the available context size".
        if (code == LlamaCode || AvailableContext().IsMatch(said))
        {
            if (body is { PromptTokens: { } prompt, ContextTokens: { } context })
            {
                return new ContextOverflow(LlamaFields, Kept(prompt), Kept(context), null);
            }

            if (LlamaMessage().Match(said) is { Success: true } llama)
            {
                return new ContextOverflow(LlamaText, Number(llama.Groups[1]), Number(llama.Groups[2]), null);
            }

            if (TabbyLimit().Match(said) is { Success: true } tabby)
            {
                return new ContextOverflow(TabbyText, PromptLength().Match(said) is { Success: true } length ? Number(length.Groups[1]) : null, Number(tabby.Groups[1]), null);
            }

            // The fields written out as text: llama-server's error escaped inside LM Studio's (or an older build's numberless message).
            var promptField = PromptField().Match(said);
            var contextField = ContextField().Match(said);
            if (promptField.Success || contextField.Success)
            {
                return new ContextOverflow(LmStudioFields, Number(promptField.Groups[1]), Number(contextField.Groups[1]), null);
            }

            return new ContextOverflow(LlamaText, null, null, null);
        }

        // vLLM, OpenAI, OpenRouter and the older forms: "This model's maximum context length is N tokens", then an ending.
        if (MaximumContext().Match(said) is { Success: true } maximum)
        {
            long? limit = Number(maximum.Groups[1]);
            if (RequestHas().Match(said) is { Success: true } has)
            {
                return new ContextOverflow(VllmText, Number(has.Groups[1]), limit, TooLarge().Match(said) is { Success: true } large ? Number(large.Groups[1]) : null);
            }

            if (OutputRequested().Match(said) is { Success: true } output)
            {
                return new ContextOverflow(VllmText, Number(output.Groups[2]), limit, Number(output.Groups[1]));
            }

            if (PromptContains().Match(said) is { Success: true } contains)
            {
                return new ContextOverflow(VllmText, Number(contains.Groups[1]), limit, null);
            }

            if (Requested().Match(said) is { Success: true } requested)
            {
                return new ContextOverflow(OpenAIText, Sum(Number(requested.Groups[2]), requested.Groups[3]), limit, Number(requested.Groups[4]));
            }

            if (OpenRouter().Match(said) is { Success: true } router)
            {
                return new ContextOverflow(OpenRouterText, Sum(Number(router.Groups[1]), router.Groups[2]), limit, Number(router.Groups[3]));
            }

            return new ContextOverflow(OpenAIText, MessagesResulted().Match(said) is { Success: true } resulted ? Number(resulted.Groups[1]) : null, limit, null);
        }

        if (InputLength().Match(said) is { Success: true } input)
        {
            return new ContextOverflow(VllmText, Number(input.Groups[1]), Number(input.Groups[2]), null);
        }

        if (Decoder().Match(said) is { Success: true } decoder)
        {
            return new ContextOverflow(VllmText, Number(decoder.Groups[1]), Number(decoder.Groups[2]), null);
        }

        if (SgLangTotal().Match(said) is { Success: true } total)
        {
            return new ContextOverflow(SgLangText, Number(total.Groups[2]), Number(total.Groups[1]), Number(total.Groups[3]));
        }

        if (SgLangLimit().Match(said) is { Success: true } sglangLimit)
        {
            return new ContextOverflow(SgLangText, null, Number(sglangLimit.Groups[1]), null);
        }

        if (SgLang().Match(said) is { Success: true } sglang)
        {
            return new ContextOverflow(SgLangText, Number(sglang.Groups[1]), Number(sglang.Groups[2]), null);
        }

        if (SgLangAllowed().Match(said) is { Success: true } allowed)
        {
            return new ContextOverflow(SgLangText, Number(allowed.Groups[1]), Number(allowed.Groups[2]), null);
        }

        if (TgiTotal().Match(said) is { Success: true } tgi)
        {
            return new ContextOverflow(TgiText, Number(tgi.Groups[2]), Number(tgi.Groups[1]), Number(tgi.Groups[3]));
        }

        if (TgiInputs().Match(said) is { Success: true } inputs)
        {
            return new ContextOverflow(TgiText, Number(inputs.Groups[2]), Number(inputs.Groups[1]), null);
        }

        if (Mistral().Match(said) is { Success: true } mistral)
        {
            return new ContextOverflow(MistralText, Number(mistral.Groups[1]), Number(mistral.Groups[2]), null);
        }

        if (Gemini().Match(said) is { Success: true } gemini)
        {
            return new ContextOverflow(GeminiText, Number(gemini.Groups[1]), Number(gemini.Groups[2]), null);
        }

        if (AnthropicTooLong().Match(said) is { Success: true } tooLong)
        {
            return new ContextOverflow(AnthropicText, Number(tooLong.Groups[1]), Number(tooLong.Groups[2]), null);
        }

        if (AnthropicLimit().Match(said) is { Success: true } older)
        {
            return new ContextOverflow(AnthropicText, Number(older.Groups[1]), Number(older.Groups[3]), Number(older.Groups[2]));
        }

        if (LmStudioKeep().IsMatch(said))
        {
            return new ContextOverflow(LmStudioText, null, null, null);
        }

        // OpenAI's code names the overflow outright, so it is preferred to a phrase guessed without figures.
        if (code == OpenAICode)
        {
            return new ContextOverflow(OpenAICodeLabel, null, null, null);
        }

        // Every phrase above without its figures (2026-11-10, review P11-6: one list, so IsOverflowText and Read never differ).
        if (Bare().Match(said) is { Success: true } bare)
        {
            return new ContextOverflow(BareLabel(bare.Value), null, null, null);
        }

        return said.Contains(OpenAICode, StringComparison.Ordinal) ? new ContextOverflow(OpenAICodeLabel, null, null, null) : null;
    }

    /// <summary>The label for a phrase matched without its figures.</summary>
    private static string BareLabel(string phrase) =>
        phrase.StartsWith("prompt is too long", StringComparison.OrdinalIgnoreCase) || phrase.StartsWith("exceed context limit", StringComparison.OrdinalIgnoreCase) ? AnthropicText
        : phrase.StartsWith("is longer than the maximum model length", StringComparison.OrdinalIgnoreCase) ? VllmText
        : phrase.StartsWith("is longer than the model's context length", StringComparison.OrdinalIgnoreCase) || phrase.StartsWith("exceeds the maximum allowed length", StringComparison.OrdinalIgnoreCase) ? SgLangText
        : phrase.Contains("max_new_tokens", StringComparison.OrdinalIgnoreCase) || phrase.Contains("must have less than", StringComparison.OrdinalIgnoreCase) ? TgiText
        : phrase.StartsWith("too large for model", StringComparison.OrdinalIgnoreCase) ? MistralText
        : phrase.StartsWith("exceeds the maximum number of tokens", StringComparison.OrdinalIgnoreCase) ? GeminiText
        : OpenAIText;

    /// <summary><paramref name="first"/> plus the figure in <paramref name="more"/> when it matched; <paramref name="first"/> alone otherwise.</summary>
    private static long? Sum(long? first, Group more) => first is { } a && Number(more) is { } b ? Kept(a + b) : first;

    /// <summary>A parsed figure, kept only between 1 and <see cref="int.MaxValue"/> (a zero limit would divide by zero; a larger one would wrap).</summary>
    private static long? Number(Group group) =>
        group.Success && long.TryParse(group.Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out long value) ? Kept(value) : null;

    private static long? Kept(long value) => value is >= 1 and <= int.MaxValue ? value : null;

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"exceeds? the available context size|maximum context length|is longer than the maximum model length|is longer than the model's context length|exceeds the maximum allowed length|prompt is too long|exceed context limit|`max_new_tokens` must be <=|`inputs` must have less than|too large for model with|exceeds the maximum number of tokens allowed|context_length_exceeded|exceed_context_size_error", Options)]
    private static partial Regex Bare();

    [GeneratedRegex(@"exceeds? the available context size", Options)]
    private static partial Regex AvailableContext();

    [GeneratedRegex(@"\((\d{1,20}) tokens\) exceeds the available context size \((\d{1,20}) tokens\)", Options)]
    private static partial Regex LlamaMessage();

    [GeneratedRegex(@"available context size of (\d{1,20}) tokens", Options)]
    private static partial Regex TabbyLimit();

    [GeneratedRegex(@"Prompt length (\d{1,20})", Options)]
    private static partial Regex PromptLength();

    [GeneratedRegex(@"n_prompt_tokens\\?""\s{0,8}:\s{0,8}(\d{1,20})", Options)]
    private static partial Regex PromptField();

    [GeneratedRegex(@"n_ctx\\?""\s{0,8}:\s{0,8}(\d{1,20})", Options)]
    private static partial Regex ContextField();

    [GeneratedRegex(@"maximum context length is ([\d,]{1,20}) tokens", Options)]
    private static partial Regex MaximumContext();

    [GeneratedRegex(@"your request has ([\d,]{1,20}) input tokens", Options)]
    private static partial Regex RequestHas();

    [GeneratedRegex(@"is too large: ([\d,]{1,20})", Options)]
    private static partial Regex TooLarge();

    // vLLM 0.20–0.26: "you requested M output tokens and your prompt contains (at least) K input tokens".
    [GeneratedRegex(@"you requested ([\d,]{1,20}) output tokens and your prompt contains (?:\(?at least\)? )?([\d,]{1,20}) input tokens", Options)]
    private static partial Regex OutputRequested();

    // vLLM 0.33: "your prompt contains (at least) K input tokens, which exceeds this limit even without any output tokens".
    [GeneratedRegex(@"your prompt contains (?:\(?at least\)? )?([\d,]{1,20}) input tokens", Options)]
    private static partial Regex PromptContains();

    // OpenAI, with tools: "you requested N tokens (A in the messages, F in the functions, and B in the completion)".
    [GeneratedRegex(@"you requested ([\d,]{1,20}) tokens \(([\d,]{1,20}) in the messages, (?:([\d,]{1,20}) in the functions, and )?([\d,]{1,20}) in the completion\)", Options)]
    private static partial Regex Requested();

    // OpenRouter: "you requested about N tokens (A of text input, B of tool input, C in the output)".
    [GeneratedRegex(@"you requested about [\d,]{1,20} tokens \(([\d,]{1,20}) of text input, (?:([\d,]{1,20}) of tool input, )?([\d,]{1,20}) in the output\)", Options)]
    private static partial Regex OpenRouter();

    [GeneratedRegex(@"your messages resulted in ([\d,]{1,20}) tokens", Options)]
    private static partial Regex MessagesResulted();

    // vLLM 0.33: "Input length (N) exceeds model's maximum context length (M)".
    [GeneratedRegex(@"Input length \(([\d,]{1,20})\) exceeds model's maximum context length \(([\d,]{1,20})\)", Options)]
    private static partial Regex InputLength();

    // vLLM: "The decoder prompt (length N) is longer than …", and 0.33's "… prompt (length N) plus the number of requested output tokens (at least 1) is longer than …".
    [GeneratedRegex(@"prompt \(length (\d{1,20})\)(?: plus the number of requested output tokens \(at least \d{1,20}\))? is longer than the maximum model length of (\d{1,20})", Options)]
    private static partial Regex Decoder();

    // SGLang: "… maximum context length of C tokens. You requested a total of T tokens: I tokens from the input messages and M tokens for the completion".
    [GeneratedRegex(@"maximum context length of ([\d,]{1,20}) tokens\. You requested a total of [\d,]{1,20} tokens: ([\d,]{1,20}) tokens from the input messages and ([\d,]{1,20}) tokens for the completion", Options)]
    private static partial Regex SgLangTotal();

    [GeneratedRegex(@"exceeds the model's maximum context length of ([\d,]{1,20}) tokens", Options)]
    private static partial Regex SgLangLimit();

    [GeneratedRegex(@"The input \((\d{1,20}) tokens\) is longer than the model's context length \((\d{1,20}) tokens\)", Options)]
    private static partial Regex SgLang();

    // SGLang: "Input length (N tokens) exceeds the maximum allowed length (M tokens)".
    [GeneratedRegex(@"Input length \(([\d,]{1,20}) tokens\) exceeds the maximum allowed length \(([\d,]{1,20}) tokens\)", Options)]
    private static partial Regex SgLangAllowed();

    // TGI: "`inputs` tokens + `max_new_tokens` must be <= N. Given: I `inputs` tokens and M `max_new_tokens`".
    [GeneratedRegex(@"`max_new_tokens` must be <= ([\d,]{1,20})\. Given: ([\d,]{1,20}) `inputs` tokens and ([\d,]{1,20}) `max_new_tokens`", Options)]
    private static partial Regex TgiTotal();

    // TGI: "`inputs` must have less than N tokens. Given: I".
    [GeneratedRegex(@"`inputs` must have less than ([\d,]{1,20}) tokens\. Given: ([\d,]{1,20})", Options)]
    private static partial Regex TgiInputs();

    // Mistral: "Prompt contains N tokens, too large for model with M maximum context length".
    [GeneratedRegex(@"Prompt contains ([\d,]{1,20}) tokens, too large for model with ([\d,]{1,20}) maximum context length", Options)]
    private static partial Regex Mistral();

    // Gemini (OpenAI-compatible): "The input token count (N) exceeds the maximum number of tokens allowed (M)".
    [GeneratedRegex(@"input token count \(([\d,]{1,20})\) exceeds the maximum number of tokens allowed \(([\d,]{1,20})\)", Options)]
    private static partial Regex Gemini();

    [GeneratedRegex(@"prompt is too long: (\d{1,20}) tokens > (\d{1,20}) maximum", Options)]
    private static partial Regex AnthropicTooLong();

    [GeneratedRegex(@"exceed context limit: (\d{1,20}) \+ (\d{1,20}) > (\d{1,20})", Options)]
    private static partial Regex AnthropicLimit();

    // LM Studio: "The number of tokens to keep from the initial prompt is greater than the context length" (no figures).
    [GeneratedRegex(@"tokens to keep from the initial prompt is greater than the context length", Options)]
    private static partial Regex LmStudioKeep();
}
