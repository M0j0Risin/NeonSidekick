using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The words of <c>/rewind</c> (2026-09-30, the user's ask): the picker, its confirmation, the notices, the armed hint
/// and the usage error. One place, so the tests can pin them.
/// </summary>
public static class RewindText
{
    /// <summary>The command word. Pinned.</summary>
    public const string Word = "/rewind";

    /// <summary>The picker's title. Pinned.</summary>
    public const string Title = NoticeGlyphs.Rewind + "Rewind";

    /// <summary>The picker's key hints. Pinned.</summary>
    public const string Keys = "Enter = rewind to before this message · ESC = back";

    /// <summary>
    /// The picker's caption over the rows (2026-10-03, the user's wording). What is not undone is the confirmation's to say
    /// (<see cref="ConfirmCaption"/>), naming the tools.
    /// </summary>
    public const string Caption = "Rewinds the conversation to the response just before the picked message.";

    /// <summary>With no turn the history can go back to (none yet, or only a compact's summary). Pinned.</summary>
    public const string NothingNotice = "(" + NoticeGlyphs.Rewind + "nothing to rewind)";

    /// <summary>
    /// The hint row while a first ESC on an empty line is fresh (the double Ctrl+C's shape): the next ESC opens the picker.
    /// Pinned.
    /// </summary>
    public const string ArmedHint = "ESC again to rewind the conversation";

    /// <summary>What the replay draws in place of a compact's summary turn when no session row holds the turns. Pinned.</summary>
    public const string CompactedNotice = "(" + NoticeGlyphs.Rewind + "earlier turns were compacted into a summary)";

    /// <summary>The <c>/help</c> row's summary. Pinned.</summary>
    public const string HelpSummary = "go back to an earlier message";

    /// <summary>The Keys tab's row for the double ESC. Pinned.</summary>
    public const string KeyMeaning = "on an empty line, rewind to an earlier message";   // its command, /rewind, in the tab's own column since 2026-10-05

    /// <summary>The Keys tab's key column for the double ESC. Pinned.</summary>
    public const string KeyLabel = "ESC ESC";

    /// <summary><c>/rewind abc</c>, <c>/rewind 0</c> or a count past the turns held. Pinned.</summary>
    public static string UsageError(int turns) =>
        turns switch
        {
            0 => "/rewind takes a number of messages back, and there is none to go back to yet.",
            1 => "/rewind takes a number of messages back: 1 is the only one.",
            _ => string.Create(CultureInfo.InvariantCulture, $"/rewind takes a number of messages back, from 1 to {turns}."),
        };

    /// <summary>
    /// One picker row as markup: the turn's number dimmed, two spaces, the first line of the message escaped (the pane cuts it
    /// at the edge), then the tool count dimmed when the model called any. Pinned.
    /// </summary>
    public static string RowMarkup(RewindTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        string calls = turn.ToolCalls > 0 ? "  " + Theme.DimMarkup(Markup.Escape("🛠️ " + turn.ToolCalls.ToString(CultureInfo.InvariantCulture))) : "";
        return Theme.DimMarkup("#" + turn.Number.ToString(CultureInfo.InvariantCulture)) + "  " + Markup.Escape(FirstLine(turn.Text)) + calls;
    }

    /// <summary>
    /// The footer under the picker for <paramref name="turn"/> (2026-10-07, the user's ask: the row shows the first line, cut at the
    /// edge): the whole message, its lines joined by a space, then <c>message #3 · 4 tool calls</c> (<c>no tools</c> for none). Pinned.
    /// </summary>
    public static MenuFooter Footer(RewindTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        string text = string.Join(' ', turn.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        string calls = turn.ToolCalls switch
        {
            0 => "no tools",
            1 => "1 tool call",
            _ => turn.ToolCalls.ToString(CultureInfo.InvariantCulture) + " tool calls",
        };
        return new MenuFooter(text, "message #" + turn.Number.ToString(CultureInfo.InvariantCulture) + " · " + calls);
    }

    /// <summary>The confirmation's question: <c>↩️ Rewind to before #3? 2 messages will be removed.</c> Pinned.</summary>
    public static string ConfirmPrompt(RewindTurn turn, RewindCut cut)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(cut);
        return string.Create(CultureInfo.InvariantCulture, $"{NoticeGlyphs.Rewind}Rewind to before #{turn.Number}? {Messages(cut.Turns)} will be removed.");
    }

    /// <summary>
    /// The confirmation's caption: the tools those turns called that may have changed something, which the rewind does not
    /// undo. Null with none. Pinned.
    /// </summary>
    public static string? ConfirmCaption(RewindCut cut)
    {
        ArgumentNullException.ThrowIfNull(cut);
        return cut.ChangingTools.Count == 0 ? null : "Not undone: what " + string.Join(", ", cut.ChangingTools) + " changed stays as it is.";
    }

    /// <summary>After the rewind: <c>(↩️ rewound 2 messages: "first line…")</c>, the picked message quoted. Pinned.</summary>
    public static string RewoundNotice(int turns, string line) =>
        "(" + NoticeGlyphs.Rewind + "rewound " + Messages(turns) + ": " + LogText.Quoted(FirstLine(line)) + ")";

    /// <summary>The warning under the rewound notice when the dropped turns called tools that change things. Pinned.</summary>
    public static string ChangesStayWarning(IReadOnlyList<string> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return "What " + string.Join(", ", tools) + " changed stays as it is: rewinding does not undo it.";
    }

    /// <summary>The headless REPL's line with the dropped message, since there is no input row to take it back. Pinned.</summary>
    public static string HeadlessLineNotice(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return "the message was: " + line;
    }

    /// <summary>The diagnostic line: <c>Rewound 2 turns (to before turn 3)</c>. Pinned.</summary>
    public static string RewoundLogLine(int turns, int number) =>
        string.Create(CultureInfo.InvariantCulture, $"Rewound {turns} turn{(turns == 1 ? "" : "s")} (to before turn {number})");

    /// <summary>
    /// <c>/rewind</c>'s argument: empty is 1 (the picker on the last message, or the headless REPL's one message back), a
    /// positive whole number is itself, anything else null (the usage error).
    /// </summary>
    public static int? ParseCount(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string trimmed = args.Trim();
        if (trimmed.Length == 0)
        {
            return 1;
        }

        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0 ? count : null;
    }

    private static string Messages(int count) =>
        count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " message" : " messages");

    private static string FirstLine(string text)
    {
        string trimmed = text.TrimStart();
        int end = trimmed.IndexOfAny(['\r', '\n']);
        return end < 0 ? trimmed.TrimEnd() : trimmed[..end].TrimEnd() + " …";
    }
}
