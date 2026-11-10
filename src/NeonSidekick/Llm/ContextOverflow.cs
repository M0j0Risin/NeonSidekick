using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Llm.OpenAIPlatform;

namespace NeonSidekick.Llm;

/// <summary>
/// A server's "the request is too long" answer, read from its error (2026-11-09, a turn that ran the context window out before the guard
/// fired): <paramref name="Matched"/> names the pattern that matched, a short fixed word for the log (never the server's own message), and
/// the figures it gave, each null when not given — the tokens sent, the server's limit, and the tokens it keeps for the reply (vLLM's
/// <c>max_tokens</c> default, Anthropic's <c>max_tokens</c>). The mid-turn guard shrinks the history and sends the round again
/// (<see cref="Assistant.ContextGuard"/>).
/// </summary>
public sealed record ContextOverflow(string Matched, long? Sent, long? Limit, long? Output);

/// <summary>
/// Reads a failed request as a context overflow (2026-11-09, <see cref="Assistant.LooksLikeContextOverflow"/>): the raw body of a
/// <see cref="ClientResultException"/> (not <see cref="Assistant.ServerDetail"/>, which keeps only a cut <c>message</c> and drops
/// llama-server's token fields), an <see cref="AnthropicApiException"/>'s and an <see cref="OpenAIApiException"/>'s type and message. Any
/// 4xx or 5xx status counts (LM Studio wraps llama-server's error in a 500), and 0 (a stream's <c>error</c> event). The Claude Code
/// clients' <see cref="InvalidOperationException"/> is never read: Claude compacts its own conversation. The texts per server were checked
/// against their sources on 2026-11-09. Every pattern is linear; numbers are kept only between 1 and <see cref="int.MaxValue"/>.
/// </summary>
public static partial class ContextOverflowMatch
{
    /// <summary>The error code OpenAI, TabbyAPI and OpenAI-shaped proxies send for an overflow.</summary>
    public const string OpenAICode = "context_length_exceeded";

    /// <summary>llama-server's error type for an overflow (its <c>code</c> is the number 400, so this is what <c>error.type</c> gives).</summary>
    public const string LlamaCode = "exceed_context_size_error";

    /// <summary>The overflow <paramref name="ex"/> (or an exception inside it) reports, or null when it is none.</summary>
    public static ContextOverflow? From(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                current = aggregate.InnerExceptions[0];
            }

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
    /// Whether <paramref name="text"/> (an error body or message) reads as a context overflow: one of the servers' phrases or codes. The
    /// shared test the two API clients use before their own stripped retries, so an overflow isn't read as a thinking refusal.
    /// </summary>
    public static bool IsOverflowText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Phrase().IsMatch(text);
    }

    /// <summary>The overflow <paramref name="text"/> reports, the error <paramref name="code"/> beside it when the exception carries one.</summary>
    internal static ContextOverflow? Read(string text, string? code)
    {
        ArgumentNullException.ThrowIfNull(text);
        code ??= text.TrimStart().StartsWith('{') ? OpenAIApiChatClient.ErrorCode(text) : null;

        // llama-server and TabbyAPI: "exceeds the available context size".
        if (code == LlamaCode || AvailableContext().IsMatch(text))
        {
            if (JsonFields(text) is { } fields)
            {
                return new ContextOverflow("llama-server fields", fields.Sent, fields.Limit, null);
            }

            if (LlamaMessage().Match(text) is { Success: true } llama)
            {
                return new ContextOverflow("llama-server text", Number(llama.Groups[1]), Number(llama.Groups[2]), null);
            }

            if (TabbyLimit().Match(text) is { Success: true } tabby)
            {
                return new ContextOverflow("TabbyAPI text", PromptLength().Match(text) is { Success: true } length ? Number(length.Groups[1]) : null, Number(tabby.Groups[1]), null);
            }

            // The fields written out as text (an older build's numberless message, or llama-server's error escaped inside LM Studio's).
            var promptField = PromptField().Match(text);
            var contextField = ContextField().Match(text);
            if (promptField.Success || contextField.Success)
            {
                return new ContextOverflow("llama-server fields", Number(promptField.Groups[1]), Number(contextField.Groups[1]), null);
            }

            return new ContextOverflow("llama-server text", null, null, null);
        }

        // vLLM and the older OpenAI-style forms: "This model's maximum context length is N tokens", then an ending.
        if (MaximumContext().Match(text) is { Success: true } maximum)
        {
            long? limit = Number(maximum.Groups[1]);
            if (RequestHas().Match(text) is { Success: true } has)
            {
                return new ContextOverflow("vLLM text", Number(has.Groups[1]), limit, TooLarge().Match(text) is { Success: true } large ? Number(large.Groups[1]) : null);
            }

            if (Requested().Match(text) is { Success: true } requested)
            {
                return new ContextOverflow("OpenAI-style text", Number(requested.Groups[2]), limit, Number(requested.Groups[3]));
            }

            return new ContextOverflow("OpenAI-style text", MessagesResulted().Match(text) is { Success: true } resulted ? Number(resulted.Groups[1]) : null, limit, null);
        }

        if (Decoder().Match(text) is { Success: true } decoder)
        {
            return new ContextOverflow("vLLM text", Number(decoder.Groups[1]), Number(decoder.Groups[2]), null);
        }

        if (SgLang().Match(text) is { Success: true } sglang)
        {
            return new ContextOverflow("SGLang text", Number(sglang.Groups[1]), Number(sglang.Groups[2]), null);
        }

        if (AnthropicTooLong().Match(text) is { Success: true } tooLong)
        {
            return new ContextOverflow("Anthropic text", Number(tooLong.Groups[1]), Number(tooLong.Groups[2]), null);
        }

        if (AnthropicLimit().Match(text) is { Success: true } older)
        {
            return new ContextOverflow("Anthropic text", Number(older.Groups[1]), Number(older.Groups[3]), Number(older.Groups[2]));
        }

        if (AnthropicTooLongBare().IsMatch(text))
        {
            return new ContextOverflow("Anthropic text", null, null, null);
        }

        return code == OpenAICode || text.Contains(OpenAICode, StringComparison.Ordinal) ? new ContextOverflow(OpenAICode + " code", null, null, null) : null;
    }

    /// <summary>llama-server's <c>n_prompt_tokens</c> and <c>n_ctx</c> as JSON numbers, top-level or under <c>error</c>; null unless both are there.</summary>
    private static (long? Sent, long? Limit)? JsonFields(string text)
    {
        if (!text.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error"u8, out var error) && error.ValueKind == JsonValueKind.Object)
            {
                root = error;
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("n_prompt_tokens"u8, out var prompt) && prompt.ValueKind == JsonValueKind.Number && prompt.TryGetInt64(out long sent)
                && root.TryGetProperty("n_ctx"u8, out var context) && context.ValueKind == JsonValueKind.Number && context.TryGetInt64(out long limit))
            {
                return (Kept(sent), Kept(limit));
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>A parsed figure, kept only between 1 and <see cref="int.MaxValue"/> (a zero limit would divide by zero; a larger one would wrap).</summary>
    private static long? Number(Group group) =>
        group.Success && long.TryParse(group.Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? Kept(value) : null;

    private static long? Kept(long value) => value is >= 1 and <= int.MaxValue ? value : null;

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"exceeds? the available context size|maximum context length is|is longer than the maximum model length|is longer than the model's context length|prompt is too long|exceed context limit|context_length_exceeded|exceed_context_size_error", Options)]
    private static partial Regex Phrase();

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

    [GeneratedRegex(@"you requested ([\d,]{1,20}) tokens \(([\d,]{1,20}) in the messages, ([\d,]{1,20}) in the completion\)", Options)]
    private static partial Regex Requested();

    [GeneratedRegex(@"your messages resulted in ([\d,]{1,20}) tokens", Options)]
    private static partial Regex MessagesResulted();

    [GeneratedRegex(@"decoder prompt \(length (\d{1,20})\) is longer than the maximum model length of (\d{1,20})", Options)]
    private static partial Regex Decoder();

    [GeneratedRegex(@"The input \((\d{1,20}) tokens\) is longer than the model's context length \((\d{1,20}) tokens\)", Options)]
    private static partial Regex SgLang();

    [GeneratedRegex(@"prompt is too long: (\d{1,20}) tokens > (\d{1,20}) maximum", Options)]
    private static partial Regex AnthropicTooLong();

    [GeneratedRegex(@"prompt is too long", Options)]
    private static partial Regex AnthropicTooLongBare();

    [GeneratedRegex(@"exceed context limit: (\d{1,20}) \+ (\d{1,20}) > (\d{1,20})", Options)]
    private static partial Regex AnthropicLimit();
}
