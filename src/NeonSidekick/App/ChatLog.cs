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
    /// <summary>
    /// One turn: the user's text and the reply, both trimmed, the thinking blocks the reply was interleaved with, and where the screen
    /// began a fresh reply block (2026-11-01, <see cref="Breaks"/>: after each tool line), so <c>/copy code</c> reads the blocks it drew.
    /// </summary>
    public readonly record struct Exchange(string User, string Reply, IReadOnlyList<Thought> Thoughts, IReadOnlyList<int> Breaks);

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

        private readonly List<int> _breaks = new();

        /// <summary>
        /// The screen began a fresh reply block here, the reply <paramref name="at"/> characters long (2026-11-01: a tool call or its
        /// result commits the live block), so a code block on either side of it is read alone, as drawn.
        /// </summary>
        public void Break(int at)
        {
            if (_breaks.Count == 0 || _breaks[^1] != at)
            {
                _breaks.Add(at);
            }
        }

        /// <summary>The places the reply was cut at, in order.</summary>
        public IReadOnlyList<int> Breaks => _breaks.ToList();

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

    /// <summary>The last recorded reply, trimmed; null when nothing is logged (2026-09-26: what <c>/plan save</c> keeps).</summary>
    public string? LastReply => _exchanges.Count == 0 ? null : _exchanges[^1].Reply;

    /// <summary>
    /// The last reply cut where the screen began fresh reply blocks (<see cref="Exchange.Breaks"/>), each piece trimmed, blank ones left
    /// out; null when nothing is logged (2026-11-01: <c>/copy code</c> parses each piece alone, so its numbers follow the ⧉ glyphs).
    /// </summary>
    public IReadOnlyList<string>? LastReplyPieces
    {
        get
        {
            if (_exchanges.Count == 0)
            {
                return null;
            }

            var last = _exchanges[^1];
            var pieces = new List<string>();
            int from = 0;
            foreach (int at in last.Breaks.Append(last.Reply.Length))
            {
                if (at < from)
                {
                    continue;
                }

                // Line breaks off the front, all whitespace off the end (2026-11-02, review P3-2): an indented first line is a code block.
                string piece = last.Reply[from..at].TrimStart('\r', '\n').TrimEnd();
                if (piece.Length > 0)
                {
                    pieces.Add(piece);
                }

                from = at;
            }

            return pieces;
        }
    }

    /// <summary>
    /// Records a turn. Both texts are trimmed (the renderer skips a reply's leading whitespace
    /// too); a turn whose reply is blank — an error, tool lines only — records nothing, its
    /// thinking with it. Each of <paramref name="thoughts"/> keeps its place in the trimmed reply, as each of <paramref name="breaks"/> does.
    /// </summary>
    public void Add(string user, string reply, IReadOnlyList<Thought>? thoughts = null, IReadOnlyList<int>? breaks = null)
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
        var cuts = (breaks ?? [])
            .Select(at => Math.Clamp(at - lead, 0, trimmedReply.Length))
            .Where(at => at > 0 && at < trimmedReply.Length)
            .Distinct()
            .Order()
            .ToList();
        _exchanges.Add(new Exchange(user.Trim(), trimmedReply, kept, cuts));
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
