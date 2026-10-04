using NeonSidekick.Diagnostics;

namespace NeonSidekick.Claude;

/// <summary>
/// The <c>Claude CLI advisor tool context</c> setting (2026-09-27, the user's call: a setting, not a fixed choice): what an
/// advisor call sends Claude besides the model's question. <c>brief</c> — the question and the model's own
/// <c>context</c> argument, nothing else; <c>recent</c> — those and the last <see cref="ClaudeText.AdvisorRecentMessages"/>
/// messages of the conversation. Pure; an unknown saved word reads as <see cref="Default"/> with a warning.
/// </summary>
public static class ClaudeAdvisorContext
{
    public const string Brief = "brief";
    public const string Recent = "recent";

    /// <summary>A fresh profile's: the model writes the brief.</summary>
    public const string Default = Brief;

    /// <summary>The picker's rows, in order.</summary>
    public static readonly string[] Names = { Brief, Recent };

    /// <summary>What each word does, for the picker's hint. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        Brief => "the model's question and its own brief",
        Recent => $"those and the last {ClaudeText.AdvisorRecentMessages} messages",
        _ => "",
    };

    /// <summary>Whether <paramref name="saved"/> asks for the recent messages; anything but the two words warns and reads as <see cref="Brief"/>.</summary>
    public static bool IsRecent(string? saved)
    {
        string word = saved?.Trim().ToLowerInvariant() ?? "";
        if (word == Recent)
        {
            return true;
        }

        if (word != Brief)
        {
            DiagnosticLog.Warn(ClaudeText.Category, $"ClaudeCliAdvisorContext='{saved}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return false;
    }
}

/// <summary>
/// The advisor's own Claude conversation (2026-09-27, the user's call: its own thread, apart from <c>/claude</c>'s):
/// the id its first call minted, resumed by the next, stored with the session and read back by a restore. The screen
/// and headless each hold one and drop it with the session (<c>/new</c>, <c>/clear</c>, a profile switch).
/// </summary>
public sealed class ClaudeAdvisorThread
{
    /// <summary>The thread to resume, or null for a new one at the next call.</summary>
    public string? SessionId { get; set; }
}

/// <summary>
/// Where an advisor call shows itself while it runs (2026-09-27): the screen's transcript, headless's stdout lines.
/// Called on the turn's task, between the call's line and its result, as the tool goes; the tests pass none.
/// </summary>
public interface IClaudeAdvisorView
{
    /// <summary>The call is about to run: the model's question.</summary>
    void Began(string question);

    /// <summary>Claude used one of its (read-only) tools.</summary>
    void Tool(string name, string detail);

    /// <summary>Claude answered: the answer as the model receives it and the run's result (cost, tokens).</summary>
    void Answered(string answer, ClaudeEvent.Result result);
}
