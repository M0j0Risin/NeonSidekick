using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm;

/// <summary>How a reasoning count the server did not report is estimated (<see cref="ReasoningEstimates"/>).</summary>
public enum ReasoningEstimate
{
    /// <summary>No estimate: the count stays unreported (<c>—</c> in <c>/usage</c>).</summary>
    Off,

    /// <summary>The streamed thinking's characters over <see cref="ConversationCompactor.CharsPerToken"/>.</summary>
    Chars,

    /// <summary>The server's own tokenizer through llama.cpp's <c>POST /tokenize</c>; <see cref="Chars"/> where that does not answer.</summary>
    Tokenize,
}

/// <summary>
/// The setting <c>LLM reasoning estimate</c> (2026-09-29, the user's ask: llama.cpp streams a model's thinking but
/// reports no reasoning count, so <c>/usage</c> read <c>—</c> for Gemma 4 on the local LLM; "make it a setting to choose
/// between" the heuristic and the tokenizer). <c>chars</c> (the default) divides the thinking's characters by four,
/// <c>tokenize</c> asks the server's <c>/tokenize</c> for the exact count, <c>off</c> leaves the count unreported. An
/// estimate is marked <c>~</c> wherever it shows, and a server's own count — <c>0</c> included — is never replaced.
/// Read at each request (<see cref="OpenAICompatibleChatClient"/>), so a change needs no reconnect. The
/// <see cref="App.MidTurnUsageMode"/> shape: <see cref="Resolve"/> is the one place the saved string becomes the enum.
/// </summary>
public static class ReasoningEstimates
{
    /// <summary>The compiled default. Pinned.</summary>
    public const string Default = "chars";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = ["off", "chars", "tokenize"];

    private const string Category = "Llm";

    /// <summary>Trims and ignores case; false (and <see cref="ReasoningEstimate.Chars"/>, the default) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out ReasoningEstimate mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "off": mode = ReasoningEstimate.Off; return true;
            case "chars": mode = ReasoningEstimate.Chars; return true;
            case "tokenize": mode = ReasoningEstimate.Tokenize; return true;
            default: mode = ReasoningEstimate.Chars; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "off" => "a server that doesn't count its thinking shows —",
        "chars" => "the streamed thinking's characters ÷ 4 (instant; shown as ~)",
        "tokenize" => "llama.cpp's /tokenize counts it exactly (one short request); else ÷ 4",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static ReasoningEstimate Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.LlmReasoningEstimate, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.LlmReasoningEstimate)}='{effective.LlmReasoningEstimate}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return ReasoningEstimate.Chars;
    }
}
