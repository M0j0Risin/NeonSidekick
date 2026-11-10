using System.Text.Json;

namespace NeonSidekick.Llm;

/// <summary>
/// A server's error body, read once (2026-11-10, the context-overflow review's P10-3/P5-5/P4-4/P1-12: three readers each parsed it their own
/// way, and P7-1: the overflow matcher ran over the whole body, so an echoed prompt could read as an overflow). The server's own fields,
/// wherever the shapes in use put them: <c>error.message</c>/<c>type</c>/<c>code</c> (OpenAI, newer vLLM, LM Studio, TabbyAPI, Anthropic's
/// <c>{"type":"error","error":{…}}</c>), a top-level <c>message</c>/<c>type</c>/<c>code</c> (older vLLM's and SGLang's <c>{"object":"error",…}</c>),
/// <c>error</c> as a plain string with <c>error_type</c> (TGI), a top-level <c>detail</c> string (FastAPI servers), and llama-server's
/// <c>n_prompt_tokens</c>/<c>n_ctx</c> beside the message. A root array is read as its first object: Gemini's OpenAI-compatible endpoint
/// sends <c>[{"error":{…}}]</c>. A numeric <c>code</c> (llama-server's 400) is not a code here. Pure.
/// </summary>
internal sealed record ErrorBody(string? Message, string? Type, string? Code, long? PromptTokens, long? ContextTokens)
{
    /// <summary>The server's own words for a matcher: the message, the type and the code, one a line; empty when none was given.</summary>
    public string Text => string.Join('\n', new[] { Message, Type, Code }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>The fields of <paramref name="body"/>, or null when it is no JSON object (nor an array whose first item is one) or has none of them.</summary>
    public static ErrorBody? Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string text = body.TrimStart();
        if (text.Length == 0 || (text[0] != '{' && text[0] != '['))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                root = root.GetArrayLength() > 0 ? root[0] : default;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? message, type, code;
            var fields = root;
            if (root.TryGetProperty("error"u8, out var error) && error.ValueKind == JsonValueKind.Object)
            {
                fields = error;
                (message, type, code) = (Str(error, "message"u8), Str(error, "type"u8), Str(error, "code"u8));
            }
            else if (error.ValueKind == JsonValueKind.String)
            {
                (message, type, code) = (error.GetString(), Str(root, "error_type"u8), null);
            }
            else
            {
                (message, type, code) = (Str(root, "message"u8), Str(root, "type"u8), Str(root, "code"u8));
            }

            message ??= Str(root, "detail"u8);
            long? prompt = Num(fields, "n_prompt_tokens"u8) ?? Num(root, "n_prompt_tokens"u8);
            long? context = Num(fields, "n_ctx"u8) ?? Num(root, "n_ctx"u8);
            return message is null && type is null && code is null && prompt is null && context is null
                ? null
                : new ErrorBody(message, type, code, prompt, context);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A non-empty string property, or null.</summary>
    private static string? Str(JsonElement element, ReadOnlySpan<byte> name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>A whole-number property, or null; any number fits <see cref="long"/> or is left out.</summary>
    private static long? Num(JsonElement element, ReadOnlySpan<byte> name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;
}
