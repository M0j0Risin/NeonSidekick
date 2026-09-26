using System.Text;

namespace NeonSidekick.App;

/// <summary>
/// What <c>/copy</c> copies: every exchange of the session as the transcript showed it — the
/// user's line and the reply's streamed text after the think-tag filter, one entry per turn
/// (a tool-using turn is still one reply to the user). Not <see cref="Llm.ConversationHistory"/>,
/// which trims to <see cref="Llm.ConversationHistory.MaxTurns"/> turns and holds several
/// assistant messages for one such turn. Cleared with the conversation (<c>/clear</c>, a
/// profile switch). Pure; the screen owns one. Each turn keeps the model's thinking too, where it
/// happened in the reply, for <c>/copy --thinking</c> (2026-09-26, the user's ask) — never copied
/// without it.
/// </summary>
public sealed class ChatLog
{
    /// <summary>One turn: the user's text and the reply, both trimmed, and the thinking blocks the reply was interleaved with.</summary>
    public readonly record struct Exchange(string User, string Reply, IReadOnlyList<Thought> Thoughts);

    /// <summary>A thinking block: its text, trimmed, and the offset into the reply where it began.</summary>
    public readonly record struct Thought(int At, string Text);

    /// <summary>
    /// A turn's thinking as it streams: a piece joins the open block, and <see cref="End"/> (any
    /// other event — text, a tool call) closes it, so the next piece opens a block of its own at the
    /// reply's length then. Pure; the screen keeps one per turn.
    /// </summary>
    public sealed class ThoughtTrail
    {
        private readonly List<(int At, StringBuilder Text)> _blocks = new();
        private bool _open;

        /// <summary>A piece of thinking, the reply <paramref name="at"/> characters long when it came.</summary>
        public void Append(int at, string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (!_open)
            {
                _blocks.Add((at, new StringBuilder()));
                _open = true;
            }

            _blocks[^1].Text.Append(text);
        }

        /// <summary>The open block, if any, is over.</summary>
        public void End() => _open = false;

        /// <summary>The blocks so far, trimmed; a blank one is left out.</summary>
        public IReadOnlyList<Thought> Thoughts =>
            _blocks.Select(b => new Thought(b.At, b.Text.ToString().Trim())).Where(t => t.Text.Length > 0).ToList();
    }

    /// <summary>The line over a copied thinking block's quote. Pinned.</summary>
    public const string ThinkingHeader = "> 💭 **Thinking**";

    /// <summary>Between two copied exchanges: a markdown rule with a blank line either side. Pinned.</summary>
    public const string Separator = "\n\n---\n\n";

    private readonly List<Exchange> _exchanges = new();

    public int Count => _exchanges.Count;

    /// <summary>
    /// Records a turn. Both texts are trimmed (the renderer skips a reply's leading whitespace
    /// too); a turn whose reply is blank — an error, tool lines only — records nothing, its
    /// thinking with it. Each of <paramref name="thoughts"/> keeps its place in the trimmed reply.
    /// </summary>
    public void Add(string user, string reply, IReadOnlyList<Thought>? thoughts = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(reply);
        string trimmedReply = reply.Trim();
        if (trimmedReply.Length == 0)
        {
            return;
        }

        int lead = reply.Length - reply.TrimStart().Length;
        var kept = (thoughts ?? [])
            .Select(t => new Thought(Math.Clamp(t.At - lead, 0, trimmedReply.Length), t.Text.Trim()))
            .Where(t => t.Text.Length > 0)
            .ToList();
        _exchanges.Add(new Exchange(user.Trim(), trimmedReply, kept));
    }

    public void Clear() => _exchanges.Clear();

    /// <summary>How many exchanges a request for <paramref name="count"/> yields: at least one, at most everything, none when empty.</summary>
    public int Take(int count) => Count == 0 ? 0 : Math.Clamp(count, 1, Count);

    /// <summary>
    /// The last <paramref name="count"/> exchanges (clamped by <see cref="Take"/>) as markdown,
    /// oldest first, <see cref="Separator"/> between them: the reply alone, or with
    /// <paramref name="includeUser"/> the user's text as a blockquote, a blank line, then the
    /// reply unindented so its own markdown still renders. With <paramref name="includeThinking"/>
    /// each thinking block is a <see cref="ThinkingQuote"/> where it happened, a blank line either
    /// side. Empty when nothing is logged.
    /// </summary>
    public string Markdown(int count, bool includeUser, bool includeThinking = false)
    {
        int take = Take(count);
        var text = new StringBuilder();
        for (int i = _exchanges.Count - take; i < _exchanges.Count; i++)
        {
            if (text.Length > 0)
            {
                text.Append(Separator);
            }

            var exchange = _exchanges[i];
            if (includeUser)
            {
                text.Append(Quote(exchange.User)).Append("\n\n");
            }

            text.Append(includeThinking ? WithThinking(exchange) : exchange.Reply);
        }

        return text.ToString();
    }

    /// <summary>The reply cut at its thinking blocks' places, the pieces trimmed and joined by a blank line, each block quoted.</summary>
    private static string WithThinking(Exchange exchange)
    {
        if (exchange.Thoughts.Count == 0)
        {
            return exchange.Reply;
        }

        var pieces = new List<string>();
        int from = 0;
        foreach (var thought in exchange.Thoughts.OrderBy(t => t.At))
        {
            pieces.Add(exchange.Reply[from..thought.At].Trim());
            pieces.Add(ThinkingQuote(thought.Text));
            from = thought.At;
        }

        pieces.Add(exchange.Reply[from..].Trim());
        return string.Join("\n\n", pieces.Where(p => p.Length > 0));
    }

    /// <summary>A thinking block as markdown: <see cref="ThinkingHeader"/>, a bare quote line, the text quoted. Pinned.</summary>
    public static string ThinkingQuote(string text) => ThinkingHeader + "\n>\n" + Quote(text.Trim());

    /// <summary>Every line of <paramref name="text"/> as a markdown blockquote line (an empty line is a bare <c>&gt;</c>). Pinned.</summary>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var quoted = new StringBuilder();
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (quoted.Length > 0)
            {
                quoted.Append('\n');
            }

            quoted.Append(line.Length == 0 ? ">" : "> " + line);
        }

        return quoted.ToString();
    }
}
