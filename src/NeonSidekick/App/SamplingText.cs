using System.Text.Json;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// The wording of <c>/sampling</c> and its pane (<see cref="SamplingMenu"/>, 2026-09-28, the user's ask: sampling overrides
/// per model): the labels, the values as the rows show them, the notices and the refusals. Everything the user reads
/// about sampling is here, so the tests can pin it.
/// </summary>
public static class SamplingText
{
    /// <summary>The pane's title and the crumb of its edits. Pinned.</summary>
    public const string Label = "🎲 Sampling";

    /// <summary>The <c>*</c> entry's tab title. Pinned.</summary>
    public const string AnyModelTabTitle = "any model (*)";

    /// <summary>A field no layer sets: the server's own default stands. Pinned.</summary>
    public const string ServerDefault = "(server default)";

    /// <summary>The <c>LLM sampling</c> settings row with no entry at all. Pinned.</summary>
    public const string ServerDefaults = "(server defaults)";

    /// <summary>The extra body's row name. Pinned.</summary>
    public const string ExtraRowName = "extra body";

    /// <summary>The last row of every tab: the tab's entry removed. Pinned.</summary>
    public const string ClearRowName = "clear all on this tab";

    /// <summary>The hint of the pane, which edits every row by typing. Pinned.</summary>
    public const string Keys = "Enter = edit · blank = server default · ←/→ tabs · ESC = close";

    /// <summary>The caption while the Anthropic API is connected: its requests carry none of this. Pinned.</summary>
    public const string ClaudeApiCaption = "The Anthropic API ignores sampling; these apply to OpenAI-compatible servers.";

    /// <summary>The caption while the OpenAI API is the server (2026-10-03): its client sends none of these. Pinned.</summary>
    public const string OpenAIApiCaption = "The OpenAI API ignores sampling; these apply to local servers.";

    /// <summary>The label column: the longest row name plus two cells.</summary>
    public static readonly int LabelWidth = SamplingField.All.Select(f => f.Wire.Length).Append(ExtraRowName.Length).Max() + 2;

    /// <summary>A tab's title: the model id, or <see cref="AnyModelTabTitle"/> for <c>*</c>; a long id cut to 32 cells with an ellipsis.</summary>
    public static string TabTitle(string key) =>
        key == LlmSampling.AnyModel ? AnyModelTabTitle : key.Length <= 32 ? key : key[..31] + "…";

    /// <summary>
    /// What a named field's row shows for <paramref name="key"/>'s tab: its own value; else, on a model's tab, the
    /// <c>*</c> value it inherits (<c>0.7 (from *)</c>); else, on the connected model's tab, what the server said it
    /// applies (<paramref name="server"/>, 2026-09-28: <see cref="ServerValue"/>); else <see cref="ServerDefault"/>.
    /// </summary>
    public static string FieldValue(SamplingField field, LlmSamplingEntry? entry, LlmSamplingEntry? any, bool anyTab, ServerSampling? server = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Get(entry) is { } own)
        {
            return SamplingField.Format(own);
        }

        if (!anyTab && field.Get(any) is { } inherited)
        {
            return SamplingField.Format(inherited) + " (from *)";
        }

        return server?.Value(field.Key) is { } reported ? ServerValue(reported, server.Source) : ServerDefault;
    }

    /// <summary>A value the server reported: <c>0.8 (server)</c>, or <c>0.6 (Hugging Face)</c> from the model card. Pinned.</summary>
    public static string ServerValue(double value, ServerSamplingSource source) =>
        SamplingField.Format(value) + (source == ServerSamplingSource.HuggingFace ? " (Hugging Face)" : " (server)");

    /// <summary>The connected model's tab caption: where its <see cref="ServerValue"/>s were read. Pinned.</summary>
    public static string SourceCaption(ServerSampling server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Source switch
        {
            ServerSamplingSource.LlamaProps => "Defaults from llama.cpp's /props.",
            ServerSamplingSource.OllamaShow => "Defaults from Ollama's /api/show (the Modelfile's parameters).",
            _ => $"Defaults from huggingface.co/{server.Detail} (generation_config.json).",
        };
    }

    /// <summary>The extra body as its row shows it and its edit starts from: compact JSON, or empty for none.</summary>
    public static string ExtraJson(Dictionary<string, JsonElement>? extra)
    {
        if (extra is not { Count: > 0 })
        {
            return "";
        }

        return "{" + string.Join(",", extra.Select(p => "\"" + JsonEncodedText.Encode(p.Key).ToString() + "\"" + ":" + p.Value.GetRawText())) + "}";
    }

    /// <summary>The extra body row's value: its JSON, or the inherited <c>*</c> one's keys, or <see cref="ServerDefault"/>'s word for none.</summary>
    public static string ExtraValue(LlmSamplingEntry? entry, LlmSamplingEntry? any, bool anyTab) =>
        entry?.Extra is { Count: > 0 } own ? ExtraJson(own)
        : !anyTab && any?.Extra is { Count: > 0 } inherited ? ExtraJson(inherited) + " (from *)"
        : "(none)";

    /// <summary>The <c>LLM sampling</c> settings row: the keys that have an entry (<c>*</c> first), or <see cref="ServerDefaults"/>. Pinned.</summary>
    public static string Summary(Dictionary<string, LlmSamplingEntry>? map)
    {
        var keys = map?.Where(p => p.Value is { IsEmpty: false }).Select(p => p.Key).ToList() ?? [];
        if (keys.Count == 0)
        {
            return ServerDefaults;
        }

        return string.Join(", ", keys.OrderBy(k => k == LlmSampling.AnyModel ? 0 : 1).ThenBy(k => k, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The status line after a save: <c>🖥️ Sampling for qwen3-8b: temperature 0.6</c>. Pinned.</summary>
    public static string SavedNotice(string key, string name, string value) => $"{NoticeGlyphs.Llm}Sampling for {key}: {name} {value}";

    /// <summary>The status line after a field is cleared. Pinned.</summary>
    public static string ClearedNotice(string key, string name) => $"{NoticeGlyphs.Llm}Sampling for {key}: {name} back to the server's default";

    /// <summary>The status line after a whole tab is cleared. Pinned.</summary>
    public static string ClearedAllNotice(string key) => $"{NoticeGlyphs.Llm}Sampling for {key}: every override cleared";

    /// <summary>The yes/no before a tab is cleared. Pinned.</summary>
    public static string ClearQuestion(string key) => $"Clear every sampling override for {key}?";

    /// <summary>The refusal of a value out of range, the saved one kept. Pinned.</summary>
    public static string RangeError(SamplingField field, string keeping)
    {
        ArgumentNullException.ThrowIfNull(field);
        return $"{field.Wire} must be {field.RangeText}; keeping {keeping}.";
    }

    /// <summary>The refusal of an extra body. Pinned.</summary>
    public static string ExtraError(string problem, string keeping) => $"The {ExtraRowName} {problem}; keeping {keeping}.";

    /// <summary>What <c>/sampling</c> with arguments answers when nothing is connected to set them for. Pinned.</summary>
    public const string NoModelError = "No model connected: /sampling with a value sets the connected model's. Use /sampling to edit any model (*) instead.";

    /// <summary>What <c>/sampling</c> answers to arguments it cannot read. Pinned.</summary>
    public static readonly string UsageError =
        "/sampling takes nothing (the pane), <field> <value>, <field> clear or clear, the field one of "
        + string.Join(", ", SamplingField.All.Select(f => f.Wire)) + " or extra (a JSON object).";

    /// <summary>The word that clears a field, or the whole entry alone. Pinned.</summary>
    public const string ClearWord = "clear";

    /// <summary>The word for the extra body in <c>/sampling extra {…}</c>. Pinned.</summary>
    public const string ExtraWord = "extra";

    /// <summary>The note beside a field in <c>/sampling</c>'s completion: its range.</summary>
    public static string CompletionNote(SamplingField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.RangeText;
    }

    /// <summary>The note beside <see cref="ExtraWord"/> in the completion.</summary>
    public const string ExtraCompletionNote = "a JSON object of extra request fields";

    /// <summary>The note beside <see cref="ClearWord"/> in the completion.</summary>
    public const string ClearCompletionNote = "every override of the connected model";
}
