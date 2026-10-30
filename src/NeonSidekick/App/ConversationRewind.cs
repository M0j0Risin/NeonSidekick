using Microsoft.Extensions.AI;
using NeonSidekick.Claude;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Sessions;

namespace NeonSidekick.App;

/// <summary>
/// One turn <c>/rewind</c> offers: <paramref name="Number"/>, its place among the offered turns counting from 1;
/// <paramref name="Start"/>, the index of its user message in the history; <paramref name="Text"/>, that message as the
/// user typed it (<see cref="ConversationRewind.UserLine"/>); and <paramref name="ToolCalls"/>, the model's calls in the
/// turn, the opening pairs left out.
/// </summary>
public sealed record RewindTurn(int Number, int Start, string Text, int ToolCalls);

/// <summary>
/// What a rewind to a turn takes away, read before the cut so the confirmation can say it.
/// <list type="bullet">
/// <item><paramref name="Turns"/>: how many turns go.</item>
/// <item><paramref name="ChangingTools"/>: every tool those turns called that changes something, in the order first called.
/// "Changes something" means every tool plan mode drops (<see cref="PlanTools.Allowed"/>), so an MCP tool counts too, and a tracked tool
/// that saved a file with <c>to</c> (a query's workbook, a directory listing; 2026-10-29, review P5-8: the queries read, so plan mode keeps them).
/// Rewinding does not undo what they did.</item>
/// <item><paramref name="HadClaude"/>: a <c>/claude</c> exchange was among them.</item>
/// <item><paramref name="FirstOrdinal"/>: the store ordinal of the first stamped turn
/// (<see cref="ConversationHistory.TurnOrdinalKey"/>).</item>
/// <item><paramref name="KeptStamped"/>: whether any turn that stays carries a stamp.</item>
/// <item><paramref name="Checkpoints"/> (2026-10-28, the checkpoints plan): the cut turns' file checkpoint ids
/// (<see cref="ConversationHistory.CheckpointKey"/>), newest first, collected before the history is cut, so "conversation only" still knows
/// which to drop; null for none.</item>
/// </list>
/// </summary>
public sealed record RewindCut(int Turns, IReadOnlyList<string> ChangingTools, bool HadClaude, int? FirstOrdinal, bool KeptStamped, bool HadAdvisors = false, IReadOnlyList<string>? Checkpoints = null);

/// <summary>One turn of the history as the screen replays it after a rewind when no session row holds the turns.</summary>
public sealed record RewindReplay(string UserText, string ReplyText, int ToolCalls, bool Summary);

/// <summary>
/// The core of <c>/rewind</c> (2026-09-30, the user's ask: "rewind a conversation to a given turn", Claude Code style). The
/// conversation goes back to just before a turn the user picks: that turn and everything after it leave the history, and the
/// session store loses the same turns. The screen (<c>ChatScreen.Rewind</c>) and the headless REPL share it. Each keeps its own
/// Claude ids, saves the history its own way, and decides what happens to the picked line.
/// <para>Only turns the history still holds can be picked. A compact's summary turn is not offered: the turns it stands for are
/// gone from the history, and rewinding to just before it is <c>/clear</c>. Turns <see cref="ConversationHistory.MaxTurns"/>
/// trimmed off the front are gone too.</para>
/// <para>Only the conversation rewinds. Files a tool wrote, commands it ran and commits it made stay as they are, and
/// <see cref="RewindCut.ChangingTools"/> names the tools that may have done so (the user's pick: conversation only, with a warning).</para>
/// <para>Since 2026-10-28 (the checkpoints plan) the file tools' changes can come back with it: when the cut turns hold file checkpoints
/// (<see cref="RewindCut.Checkpoints"/>), the screen and headless offer "conversation and files", which restores them before the cut.</para>
/// </summary>
public static partial class ConversationRewind
{
    /// <summary>Whether <paramref name="message"/> is a compact's summary turn (<see cref="ConversationCompactor.SummaryPreamble"/>).</summary>
    public static bool IsSummary(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Text.StartsWith(ConversationCompactor.SummaryPreamble, StringComparison.Ordinal);
    }

    /// <summary>The turns <paramref name="messages"/> holds that a rewind can go back to, oldest first: every turn except a compact's summary.</summary>
    public static IReadOnlyList<RewindTurn> Turns(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var starts = Starts(messages);
        var turns = new List<RewindTurn>(starts.Count);
        for (int k = 0; k < starts.Count; k++)
        {
            var message = messages[starts[k]];
            if (IsSummary(message))
            {
                continue;
            }

            int end = k + 1 < starts.Count ? starts[k + 1] : messages.Count;
            turns.Add(new RewindTurn(turns.Count + 1, starts[k], UserLine(message), Calls(messages, starts[k] + 1, end).Count()));
        }

        return turns;
    }

    /// <summary>
    /// The text of a turn's user message as the user typed it: its text parts in order. A mid-turn compact's progress note is
    /// left out, both the tagged part and the plain part a restored session has
    /// (<see cref="ConversationCompactor.TurnProgressPreamble"/>), and an <c>/undo</c> note ahead of the text (2026-10-28, <see cref="ConversationHistory.WithoutUndoNote"/>). A <c>/claude</c> exchange's <c>[to Claude] </c> tag becomes
    /// <c>/claude </c> again, and a <c>/cmd</c> run's tag the line that runs it again (2026-10-09). Picture labels (<c>[Image #1]</c>) stay in the text.
    /// </summary>
    public static string UserLine(ChatMessage message)
    {
        string text = TypedText(message);
        return ClaudeText.HistoryUserPrompt(text) is { } prompt ? "/claude " + prompt : Shell.ShellText.CmdHistoryCommand(text) ?? text;
    }

    /// <summary>
    /// The words the user's turn <paramref name="message"/> carries, as the model read them less what the app put there: no progress note,
    /// no restored PDF, no <c>/undo</c> note. <see cref="UserLine"/> without its <c>/claude</c> and <c>/cmd</c> rewrite (2026-10-28, review
    /// P12-08: the evidence search read the line's <c>/cmd …</c> spelling, not the words the model had).
    /// </summary>
    public static string TypedText(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        // A restored session's attached PDF is a text part of its own, never what the user typed (2026-10-20, review: its whole text
        // went into the replay's user row and onto the line).
        string text = string.Concat(message.Contents
            .Where(c => c is TextContent text && !ConversationCompactor.IsTurnProgress(c) && !text.Text.StartsWith(ConversationCompactor.TurnProgressPreamble, StringComparison.Ordinal)
                && ConversationHistory.RestoredDocumentPath(c) is null)
            .Select(c => ((TextContent)c).Text));
        return ConversationHistory.WithoutUndoNote(message, text);
    }

    /// <summary>The pictures <paramref name="message"/> carries, in order: what the line takes back to the input.</summary>
    public static IReadOnlyList<DataContent> Images(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")).ToList();
    }

    /// <summary>The attached PDFs <paramref name="message"/> carries, in order (2026-10-14, the PDF plan's gap 49): their files go back on the line.</summary>
    public static IReadOnlyList<DataContent> Documents(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Contents.OfType<DataContent>().Where(ConversationHistory.IsDocument).ToList();
    }

    /// <summary>
    /// The files of the attached PDFs <paramref name="message"/> carries, in order: each live part's path (<see cref="Documents"/>), and each
    /// restored session's PDF text part's (<see cref="ConversationHistory.RestoredDocumentPath"/>; empty when it named none), so a rewind after a
    /// restore puts the file back on the line too (2026-10-20, review).
    /// </summary>
    public static IReadOnlyList<string> DocumentPaths(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var paths = new List<string>();
        foreach (var content in message.Contents)
        {
            if (ConversationHistory.IsDocument(content))
            {
                paths.Add(ConversationHistory.PicturePath(content) ?? "");
            }
            else if (ConversationHistory.RestoredDocumentPath(content) is { } restored)
            {
                paths.Add(restored);
            }
        }

        return paths;
    }

    /// <summary>Where <paramref name="text"/> holds an attached PDF's label (<see cref="Pdf.PdfText.DocumentLabel"/>, <c>[PDF #2 report.pdf]</c>), in order.</summary>
    public static IReadOnlyList<(int Start, int Length)> DocumentLabels(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. DocumentLabelPattern().Matches(text).Select(m => (m.Index, m.Length))];
    }

    // Up to the first ".pdf]" (2026-10-20, review): a PDF's name may hold a ']' of its own, as PasteBlocks' sent label's pattern.
    [System.Text.RegularExpressions.GeneratedRegex(@"\[PDF #\d+ [^\r\n]*?\.pdf\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DocumentLabelPattern();

    /// <summary>
    /// Where <paramref name="text"/> holds a picture's label (<see cref="UI.PasteBlocks.ImageLabel"/>, <c>[Image #3]</c>), in order:
    /// the places a picture goes back into the line as a token.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> ImageLabels(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        const string open = "[Image #";
        var labels = new List<(int, int)>();
        int at = 0;
        while ((at = text.IndexOf(open, at, StringComparison.Ordinal)) >= 0)
        {
            int digits = at + open.Length;
            int end = digits;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            if (end > digits && end < text.Length && text[end] == ']')
            {
                labels.Add((at, end + 1 - at));
                at = end + 1;
            }
            else
            {
                at = digits;
            }
        }

        return labels;
    }

    /// <summary>What cutting <paramref name="messages"/> at <paramref name="start"/> (a turn start) would take away. Changes nothing.</summary>
    public static RewindCut Preview(IReadOnlyList<ChatMessage> messages, int start)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, messages.Count);
        int turns = 0;
        bool claude = false;
        int? first = null;
        var checkpoints = new List<string>();
        for (int i = start; i < messages.Count; i++)
        {
            var message = messages[i];
            if (!ConversationHistory.IsTurnStart(message))
            {
                continue;
            }

            turns++;
            claude |= ClaudeText.HistoryUserPrompt(ConversationHistory.WithoutUndoNote(message, message.Text)) is not null;
            first ??= ConversationHistory.TurnOrdinal(message);
            if (ConversationHistory.TurnCheckpoint(message) is { } id)
            {
                checkpoints.Insert(0, id);
            }
        }

        var calls = Calls(messages, start, messages.Count).ToList();
        var changing = calls.Where(c => Changes(c.Call, c.Result)).Select(c => c.Call.Name).Distinct(StringComparer.Ordinal).ToList();
        bool advisors = calls.Any(c => string.Equals(c.Call.Name, NeonAdvisorTool.ToolName, StringComparison.Ordinal));
        bool keptStamped = false;
        for (int i = 0; i < start && !keptStamped; i++)
        {
            keptStamped = ConversationHistory.IsTurnStart(messages[i]) && ConversationHistory.TurnOrdinal(messages[i]) is not null;
        }

        return new RewindCut(turns, changing, claude, first, keptStamped, advisors, checkpoints.Count == 0 ? null : checkpoints);
    }

    /// <summary>
    /// How many of a session's <paramref name="storedTurns"/> rows stay after <paramref name="cut"/>:
    /// <list type="bullet">
    /// <item>A stamped turn among those cut: the rows before its ordinal stay.</item>
    /// <item>No stamp in the cut but one in what stays: none of the cut turns wrote a row (logging was off, or they were
    /// <c>/botchat</c>'s), so every row stays.</item>
    /// <item>No stamp anywhere (a session saved before stamping, 2026-09-30): one row per cut turn, counted back from the end.</item>
    /// </list>
    /// </summary>
    public static int KeepThrough(RewindCut cut, int storedTurns)
    {
        ArgumentNullException.ThrowIfNull(cut);
        if (cut.FirstOrdinal is { } ordinal)
        {
            return Math.Clamp(ordinal - 1, 0, Math.Max(0, storedTurns));
        }

        return cut.KeptStamped ? Math.Max(0, storedTurns) : Math.Max(0, storedTurns - cut.Turns);
    }

    /// <summary>
    /// The rewind itself: <paramref name="history"/> cut at <paramref name="turn"/>'s start, and the session row
    /// <paramref name="sessionId"/> in <paramref name="store"/> cut to match (<see cref="SessionStore.TruncateTurns"/>), when there is one.
    /// Returns what went and the picked user message. Saving the history and the Claude ids is the caller's job.
    /// </summary>
    public static (RewindCut Cut, ChatMessage Picked) Apply(ConversationHistory history, RewindTurn turn, SessionStore? store, long? sessionId)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(turn);
        var messages = history.Messages;
        if (turn.Start >= messages.Count || !ConversationHistory.IsTurnStart(messages[turn.Start]))
        {
            throw new ArgumentException("The turn is not a turn start of this history.", nameof(turn));
        }

        var cut = Preview(messages, turn.Start);
        var picked = messages[turn.Start];
        history.TruncateAt(turn.Start);
        if (store is not null && sessionId is { } id && store.Summary(id) is { } summary)
        {
            store.TruncateTurns(id, KeepThrough(cut, summary.Turns));
        }

        return (cut, picked);
    }

    /// <summary>
    /// The turns of <paramref name="messages"/> as the screen replays them: what the user typed (<see cref="UserLine"/>), the
    /// turn's last assistant text (a <c>/claude</c> reply without its tag), and the call count. A compact's summary is marked so
    /// the screen can draw a notice in its place.
    /// </summary>
    public static IReadOnlyList<RewindReplay> Replay(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var starts = Starts(messages);
        var replay = new List<RewindReplay>(starts.Count);
        for (int k = 0; k < starts.Count; k++)
        {
            var message = messages[starts[k]];
            int end = k + 1 < starts.Count ? starts[k + 1] : messages.Count;
            if (IsSummary(message))
            {
                replay.Add(new RewindReplay("", "", 0, Summary: true));
                continue;
            }

            string reply = "";
            for (int i = end - 1; i > starts[k]; i--)
            {
                if (messages[i].Role == ChatRole.Assistant && messages[i].Text.Length > 0)
                {
                    reply = ClaudeText.HistoryReplyText(messages[i].Text);
                    break;
                }
            }

            replay.Add(new RewindReplay(UserLine(message), reply, Calls(messages, starts[k] + 1, end).Count(), Summary: false));
        }

        return replay;
    }

    private static List<int> Starts(IReadOnlyList<ChatMessage> messages)
    {
        var starts = new List<int>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (ConversationHistory.IsTurnStart(messages[i]))
            {
                starts.Add(i);
            }
        }

        return starts;
    }

    /// <summary>
    /// Whether a call changed something: a tool plan mode drops, whatever it answered (a <c>run_command</c> that changed files can still
    /// answer "Error: exit code 1"), or a tracked tool given a <c>to</c> whose <paramref name="result"/> isn't a refusal (2026-10-30, review
    /// F58: a query refused its export in plan mode was listed as a change). No result (a turn stopped mid-call) counts, the safe side.
    /// </summary>
    public static bool Changes(FunctionCallContent call, FunctionResultContent? result)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!PlanTools.Allowed(call.Name))
        {
            return true;
        }

        bool named = Files.CheckpointTools.Tracked.Contains(call.Name) && call.Arguments?.TryGetValue("to", out var to) == true && to switch
        {
            string text => !string.IsNullOrWhiteSpace(text),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } json => !string.IsNullOrWhiteSpace(json.GetString()),
            _ => false,
        };
        return named && !(ResultText(result)?.StartsWith("Error:", StringComparison.Ordinal) ?? false);
    }

    /// <summary>A call's answer as text: a string, or a JSON string (a restored session's); null for none or any other shape.</summary>
    private static string? ResultText(FunctionResultContent? result) => result?.Result switch
    {
        string text => text,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } json => json.GetString(),
        _ => null,
    };

    /// <summary>
    /// The model's calls in <paramref name="messages"/>[<paramref name="from"/>..<paramref name="to"/>), the seeded opening pairs left out, each
    /// with its result from the same range (the same call id), or null when it has none.
    /// </summary>
    private static IEnumerable<(FunctionCallContent Call, FunctionResultContent? Result)> Calls(IReadOnlyList<ChatMessage> messages, int from, int to)
    {
        var results = new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
        for (int i = from; i < to; i++)
        {
            if (messages[i].Role == ChatRole.Tool)
            {
                foreach (var content in messages[i].Contents)
                {
                    if (content is FunctionResultContent result && result.CallId is { } id)
                    {
                        results.TryAdd(id, result);
                    }
                }
            }
        }

        for (int i = from; i < to; i++)
        {
            if (messages[i].Role != ChatRole.Assistant)
            {
                continue;
            }

            foreach (var content in messages[i].Contents)
            {
                if (content is FunctionCallContent call && !Assistant.IsOpeningCallId(call.CallId))
                {
                    yield return (call, call.CallId is { } id && results.TryGetValue(id, out var result) ? result : null);
                }
            }
        }
    }
}
