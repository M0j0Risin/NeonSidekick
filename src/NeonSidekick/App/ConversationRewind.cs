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
/// "Changes something" means every tool plan mode drops (<see cref="PlanTools.Allowed"/>), so an MCP tool counts too.
/// Rewinding does not undo what they did.</item>
/// <item><paramref name="HadClaude"/>: a <c>/claude</c> exchange was among them.</item>
/// <item><paramref name="HadAdvisor"/>: a <c>claude_advisor</c> call was among them.</item>
/// <item><paramref name="FirstOrdinal"/>: the store ordinal of the first stamped turn
/// (<see cref="ConversationHistory.TurnOrdinalKey"/>).</item>
/// <item><paramref name="KeptStamped"/>: whether any turn that stays carries a stamp.</item>
/// </list>
/// </summary>
public sealed record RewindCut(int Turns, IReadOnlyList<string> ChangingTools, bool HadClaude, bool HadAdvisor, int? FirstOrdinal, bool KeptStamped);

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
/// </summary>
public static class ConversationRewind
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
    /// (<see cref="ConversationCompactor.TurnProgressPreamble"/>). A <c>/claude</c> exchange's <c>[to Claude] </c> tag becomes
    /// <c>/claude </c> again. Picture labels (<c>[Image #1]</c>) stay in the text.
    /// </summary>
    public static string UserLine(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string text = string.Concat(message.Contents
            .Where(c => c is TextContent text && !ConversationCompactor.IsTurnProgress(c) && !text.Text.StartsWith(ConversationCompactor.TurnProgressPreamble, StringComparison.Ordinal))
            .Select(c => ((TextContent)c).Text));
        return ClaudeText.HistoryUserPrompt(text) is { } prompt ? "/claude " + prompt : text;
    }

    /// <summary>The pictures <paramref name="message"/> carries, in order: what the line takes back to the input.</summary>
    public static IReadOnlyList<DataContent> Images(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")).ToList();
    }

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
        for (int i = start; i < messages.Count; i++)
        {
            var message = messages[i];
            if (!ConversationHistory.IsTurnStart(message))
            {
                continue;
            }

            turns++;
            claude |= ClaudeText.HistoryUserPrompt(message.Text) is not null;
            first ??= ConversationHistory.TurnOrdinal(message);
        }

        var calls = Calls(messages, start, messages.Count).ToList();
        var changing = calls.Select(c => c.Name).Where(name => !PlanTools.Allowed(name)).Distinct(StringComparer.Ordinal).ToList();
        bool advisor = calls.Any(c => string.Equals(c.Name, ClaudeAdvisorTool.ToolName, StringComparison.Ordinal));
        bool keptStamped = false;
        for (int i = 0; i < start && !keptStamped; i++)
        {
            keptStamped = ConversationHistory.IsTurnStart(messages[i]) && ConversationHistory.TurnOrdinal(messages[i]) is not null;
        }

        return new RewindCut(turns, changing, claude, advisor, first, keptStamped);
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

    /// <summary>The model's calls in <paramref name="messages"/>[<paramref name="from"/>..<paramref name="to"/>), the seeded opening pairs left out.</summary>
    private static IEnumerable<FunctionCallContent> Calls(IReadOnlyList<ChatMessage> messages, int from, int to)
    {
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
                    yield return call;
                }
            }
        }
    }
}
