using System.Globalization;
using System.Text;
using NeonSidekick.Settings;

namespace NeonSidekick.Advisors;

/// <summary>
/// The profile advisors' words (2026-10-10, <c>neon_advisor</c> and <c>/advisor</c>): the tool's description and errors, the
/// advisor's framing and the message it is sent, the transcript's lines and the pane's question. Pinned by the tests.
/// </summary>
public static class AdvisorText
{
    /// <summary>The log category. Pinned.</summary>
    public const string Category = "Advisor";

    /// <summary>The spinner's label while an advisor answers. Pinned.</summary>
    public const string AskingLabel = "advisor";

    /// <summary>
    /// What the advisor is told it is, after its own persona (2026-10-10, the user's call: persona and role): whom it advises, in what
    /// role, and what it can and cannot do, so it answers as an advisor and never offers to act. Pinned.
    /// </summary>
    public static string Framing(string master, string role, bool tools)
    {
        var sb = new StringBuilder();
        sb.Append("You are acting as an advisor");
        if (!string.IsNullOrWhiteSpace(role))
        {
            sb.Append(" in the role of ").Append(role.Trim());
        }

        sb.Append(" to another AI assistant (the profile \"").Append(master).Append("\"), which works for its user and has asked for your view. ");
        sb.Append("Answer its question directly and concisely, from your role: your recommendation first, then the reasons. ");
        sb.Append(tools
            ? "You may use the read-only tools you are offered to look things up, but you cannot change anything, so do not offer to."
            : "You have no tools and cannot change anything, so answer from what you are told and do not offer to act.");
        return sb.ToString();
    }

    /// <summary>The message a call sends: the question, the asker's brief and, when asked for, the conversation's last messages. Pinned.</summary>
    public static string Prompt(string question, string context, IReadOnlyList<string>? recent)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").Append(question.Trim());
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.Append("\n\nContext from the assistant:\n").Append(context.Trim());
        }

        if (recent is { Count: > 0 })
        {
            sb.Append("\n\nIts conversation so far (the last ").Append(recent.Count.ToString(CultureInfo.InvariantCulture)).Append(" messages):");
            foreach (var line in recent)
            {
                sb.Append('\n').Append(line);
            }
        }

        return sb.ToString();
    }

    /// <summary>The call's line in the transcript: <c>dba (database architect) › Which index?</c>. Pinned.</summary>
    public static string QuestionNote(AdvisorEntry advisor, string question) => AdvisorRoster.Label(advisor) + " › " + Flat(question);

    /// <summary>A tool the advisor used, dim in the transcript: <c>dba › read_file schema.sql</c>. Pinned.</summary>
    public static string ToolNote(string profile, string name, string detail) =>
        profile + " › " + name + (string.IsNullOrWhiteSpace(detail) ? "" : " " + Clip(Flat(detail), 80));

    /// <summary>The footer under an answer: who answered on what, the tokens, and the shared-model warning when there is one. Pinned.</summary>
    public static string Footer(string profile, string model, Llm.TokenUsage usage, string? warning)
    {
        string text = profile + " · " + model + " · " + usage.Input.ToString("N0", CultureInfo.InvariantCulture) + " in · " + usage.Output.ToString("N0", CultureInfo.InvariantCulture) + " out";
        return warning is null ? text : text + " · " + warning;
    }

    /// <summary>
    /// <c>Advisor embedded mode</c> <c>parent-server</c>'s warning (2026-10-10, the user's call): the advisor's profile named another
    /// embedded model, and it answered on the one running. Pinned.
    /// </summary>
    public static string SharedEmbeddedWarning(string wanted, string running) => $"wanted {wanted}, answered on the running {running}";

    /// <summary>A Claude Code advisor's footer note while the chat itself is on the Claude Code server (2026-10-10, the user's rule). Pinned.</summary>
    public const string MasterOnClaudeCodeNote = "no tools: this chat is on the Claude Code server";

    /// <summary>A Claude Code advisor's footer note while another holds the server's tools (2026-10-10, the user's rule: the first listed keeps them). Pinned.</summary>
    public static string OtherClaudeCodeNote(string holder) => $"no tools: {holder} is the Claude Code advisor with tools";

    /// <summary>The list editor's mark on a Claude Code advisor: whether it gets the tools. Pinned.</summary>
    public static string ClaudeCodeMark(bool tools) => tools ? "Claude Code, tools" : "Claude Code, text only";

    /// <summary>The confirm pane's title (<c>Advisor confirm</c> on). Pinned.</summary>
    public static string ConfirmQuestion(AdvisorEntry advisor, string question) =>
        "Let the model ask " + AdvisorRoster.Label(advisor) + ": " + Clip(Flat(question), 80) + "?";

    /// <summary>A call with no question. Pinned.</summary>
    public const string NoQuestionError = "Error: question is required — what you want the advisor's view on.";

    /// <summary>No advisor is listed. Pinned.</summary>
    public const string NoAdvisorsError = "Error: no advisors are set up (Advisor list in /settings › Advisors). Carry on without one.";

    /// <summary>The words named none, or several. Pinned.</summary>
    public static string UnknownAdvisorError(string words, IReadOnlyList<AdvisorEntry> candidates) =>
        "Error: \"" + words.Trim() + "\" names no single advisor. Name one of: " + string.Join("; ", candidates.Select(AdvisorRoster.Label)) + ".";

    /// <summary>The advisor's profile is gone. Pinned.</summary>
    public static string MissingProfileError(string profile) => $"Error: the advisor {profile}'s profile no longer exists. Carry on without it.";

    /// <summary>The advisor's profile could not be read. Pinned.</summary>
    public static string UnreadableProfileError(string profile, string reason) => $"Error: the advisor {profile}'s profile could not be read: {Flat(reason)}";

    /// <summary>A call past <c>Advisor calls per turn</c>. Pinned.</summary>
    public static string CapError(int cap) =>
        $"Error: neon_advisor was already called {cap.ToString(CultureInfo.InvariantCulture)} time{(cap == 1 ? "" : "s")} this turn, the most allowed. Carry on without it.";

    /// <summary>The user said no on the confirm pane. Pinned.</summary>
    public static string DeclinedError(string profile) => $"Error: the user declined to let you ask {profile}. Carry on without it, and do not ask it again this turn.";

    /// <summary>Confirmation is on and nothing can ask (headless, no pane). Pinned.</summary>
    public const string NotAskedError = "Error: neon_advisor needs the user's yes (Advisor confirm is on) and there is no one to ask here. Carry on without it.";

    /// <summary>The advisor's server could not be reached or its turn failed. Pinned.</summary>
    public static string FailedError(string profile, string reason) => $"Error: the advisor {profile} could not answer: {Flat(reason)}";

    /// <summary>A turn that ended well with no words. Pinned.</summary>
    public static string NoAnswer(string profile) => $"({profile} gave no answer)";

    /// <summary>The tool's description, with the advisors as they are now. Pinned.</summary>
    public static string Description(IReadOnlyList<AdvisorEntry> advisors) =>
        "Asks one of your advisors for its view: another assistant with its own model, in a role of its own. " +
        "Use it when a question falls in an advisor's role, or for a second opinion on a choice that matters. " +
        "The advisor can use your read-only tools but cannot change anything; it remembers your earlier questions to it in this conversation. " +
        "Send one self-contained question and put what it needs to know in context. Advisors: " +
        (advisors.Count == 0 ? "none." : string.Join("; ", advisors.Select(AdvisorRoster.Label)) + ".");

    /// <summary>The <c>advisor</c> argument's description. Pinned.</summary>
    public const string AdvisorArgumentDescription = "Which advisor to ask: its name (before the parentheses in the list) or its role.";

    /// <summary>The <c>question</c> argument's description. Pinned.</summary>
    public const string QuestionDescription = "What you want the advisor's view on, as one self-contained question.";

    /// <summary>The <c>context</c> argument's description. Pinned.</summary>
    public const string ContextDescription = "What the advisor needs to know to answer: the goal, what you tried, what you found. Optional.";

    /// <summary>The log line after a call. Pinned.</summary>
    public static string ResultLog(string profile, string model, Llm.TokenUsage usage, int tools) =>
        $"{profile} answered on {model}: {usage.Input.ToString(CultureInfo.InvariantCulture)} in, {usage.Output.ToString(CultureInfo.InvariantCulture)} out, {tools.ToString(CultureInfo.InvariantCulture)} tool call{(tools == 1 ? "" : "s")}";

    // ── /advisor (2026-10-10) ────────────────────────────────────────────────

    /// <summary>The command's word. Pinned.</summary>
    public const string Word = "/advisor";

    /// <summary>The command's line on <c>/help</c>. Pinned.</summary>
    public const string HelpSummary = "ask one of your advisor profiles";

    /// <summary>A name with no question. Pinned.</summary>
    public const string UsageError = "Usage: /advisor <advisor> <question> — or /advisor alone to list the advisors";

    /// <summary>A bare <c>/advisor</c> with none listed. Pinned.</summary>
    public const string NoAdvisorsNotice = "(no advisors: add one in /settings › Advisors)";

    /// <summary>The list's heading. Pinned.</summary>
    public const string ListHeading = "Advisors (/advisor <name> <question>):";

    /// <summary>One advisor on the list: <c>dba — database architect</c>, a missing profile marked. Pinned.</summary>
    public static string ListLine(AdvisorEntry advisor, bool exists) =>
        "  " + advisor.Profile + (advisor.Role.Length == 0 ? "" : " — " + advisor.Role) + (exists ? "" : "  (missing)");

    /// <summary>The asking line as the model's history keeps it: the exchange read as one put to someone else (as <c>/claude</c>'s). Pinned.</summary>
    public static string HistoryUser(string profile, string question) => "[to " + profile + "] " + question.Trim();

    /// <summary>The answer as the model's history keeps it. Pinned.</summary>
    public static string HistoryReply(string profile, string answer) => "[" + profile + "] " + answer.Trim();

    /// <summary>A tool error as the user reads it: without the <c>Error: </c> the model is told. Pure.</summary>
    public static string ForUser(string error) => error.StartsWith("Error: ", StringComparison.Ordinal) ? error["Error: ".Length..] : error;

    /// <summary>Whether a tool result is a refusal or a failure. Pure.</summary>
    public static bool IsError(string result) => result.StartsWith("Error: ", StringComparison.Ordinal);

    /// <summary><c>/advisor</c>'s arguments: the advisor's word, then the question; a blank question when there is none. Pure.</summary>
    public static (string Advisor, string Question) Split(string args)
    {
        string text = (args ?? "").Trim();
        int space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    private static string Flat(string text) => (text ?? "").ReplaceLineEndings(" ").Trim();

    private static string Clip(string text, int max) => text.Length > max ? text[..(max - 1)] + "…" : text;
}
