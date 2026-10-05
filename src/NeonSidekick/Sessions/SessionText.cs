using System.Globalization;
using System.Text;

namespace NeonSidekick.Sessions;

/// <summary>
/// Every sentence the session store shows the model or the user, pinned: <c>session_manager</c>'s
/// results and errors (the <see cref="Files.FileText"/> shape — a mistake is an <c>Error:</c>
/// sentence, never a Warning), the title helpers, the row labels the pane and the tool share.
/// Times are shown in the local zone the caller passes (<see cref="Moment"/>); the store keeps UTC.
/// </summary>
public static class SessionText
{
    /// <summary>The most characters a title keeps, from either source.</summary>
    public const int MaxTitleChars = 60;

    /// <summary>The most characters of the first user line and the first reply the title request shows the model.</summary>
    public const int TitleSampleChars = 600;

    /// <summary>The most characters a <c>read</c> answers with; older turns past it are cut with <see cref="ReadCut"/>.</summary>
    public const int MaxReadChars = 12_000;

    /// <summary>The most words <see cref="SearchWords"/> keeps of a user line for the reflection's evidence search (2026-09-19).</summary>
    public const int MaxSearchWords = 12;

    /// <summary>A word shorter than this is left out of <see cref="SearchWords"/> — the articles, the pronouns, the prepositions carry no topic.</summary>
    public const int MinSearchWordLength = 4;

    /// <summary>The system line of the model-written title request. Pinned.</summary>
    public const string TitleInstruction =
        "Write a title of at most six words for the conversation below, in the language it is in. Answer with the title alone: no quotes, no full stop, no explanation.";

    /// <summary>The title of a session whose first line was blank.</summary>
    public const string Untitled = "(untitled)";

    public const string NoSessions = "No earlier sessions are stored.";
    public const string NoQuery = "Error: give the text to search for";
    public const string NoId = "Error: give the session id to read";

    // ---- titles ----

    /// <summary>The first non-blank line of <paramref name="text"/>, its spaces collapsed, cut to <see cref="MaxTitleChars"/> with an ellipsis; <see cref="Untitled"/> for none.</summary>
    public static string FirstLineTitle(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string line in text.Split('\n'))
        {
            string collapsed = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (collapsed.Length > 0)
            {
                return Cut(collapsed, MaxTitleChars);
            }
        }

        return Untitled;
    }

    /// <summary>
    /// The model's title answer as a slug (the user's call, 2026-09-18): the first non-blank line,
    /// a leading <c>Title:</c> dropped, then <see cref="Slug"/> — always lower-case, letters and
    /// digits alone with one hyphen between the words (<c>Pip the Frog's Secret Rhythm</c> →
    /// <c>pip-the-frogs-secret-rhythm</c>), cut to <see cref="MaxTitleChars"/>; null when nothing
    /// usable is left (the first line stays). The first-line and typed titles are never slugged.
    /// </summary>
    public static string? CleanTitle(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
            {
                line = line["Title:".Length..];
            }

            if (Slug(line) is { } slug)
            {
                return slug;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="text"/> as a lower-case kebab-case slug: apostrophes vanish (<c>Frog's</c> →
    /// <c>frogs</c>), every other run of characters that are not letters or digits (accented
    /// letters survive) becomes one hyphen, none at either end, cut to <see cref="MaxTitleChars"/>
    /// with no hyphen left at the cut; null when nothing is left. Pinned.
    /// </summary>
    public static string? Slug(string text) => SlugOf(text);

    /// <summary>
    /// A title as the screen shows it (2026-10-04, the UI review: the rule read <c>summary-of-notes-file</c>): a model-written slug as
    /// words, its first letter raised (<c>Summary of notes file</c>); a first-line or typed title as it is. The store keeps the slug. Pure.
    /// </summary>
    public static string DisplayTitle(SessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.TitleSource == TitleSource.Model ? Words(session.Title) : session.Title;
    }

    /// <summary>A slug's words: hyphens as spaces, the first letter raised. Pure.</summary>
    public static string Words(string slug)
    {
        ArgumentNullException.ThrowIfNull(slug);
        string words = slug.Replace('-', ' ').Trim();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string? SlugOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        bool pendingHyphen = false;
        foreach (char c in text.ToLowerInvariant())
        {
            if (c is '\'' or '’')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingHyphen && sb.Length > 0)
                {
                    sb.Append('-');
                }

                pendingHyphen = false;
                sb.Append(c);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        if (sb.Length > MaxTitleChars)
        {
            sb.Length = MaxTitleChars;
        }

        string slug = sb.ToString().TrimEnd('-');
        return slug.Length > 0 ? slug : null;
    }

    /// <summary>The user message of the title request: the first line and the first reply, each cut to <see cref="TitleSampleChars"/>. Pinned.</summary>
    public static string TitleRequest(string userText, string replyText)
    {
        ArgumentNullException.ThrowIfNull(userText);
        ArgumentNullException.ThrowIfNull(replyText);
        return "User: " + Cut(userText.Trim(), TitleSampleChars) + "\n\nAssistant: " + Cut(replyText.Trim(), TitleSampleChars);
    }

    /// <summary>
    /// The words of a user line worth searching the earlier sessions for (2026-09-19, the reflection's
    /// evidence): every run of letters and digits of at least <see cref="MinSearchWordLength"/>
    /// characters, lower-cased, distinct, in order, at most <see cref="MaxSearchWords"/> of them.
    /// Pure and pinned; <c>SessionStore.FtsQueryAny</c> quotes them.
    /// </summary>
    public static IReadOnlyList<string> SearchWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                word.Append(char.ToLowerInvariant(text[i]));
                continue;
            }

            if (word.Length >= MinSearchWordLength && seen.Add(word.ToString()))
            {
                words.Add(word.ToString());
                if (words.Count == MaxSearchWords)
                {
                    break;
                }
            }

            word.Clear();
        }

        return words;
    }

    // ---- labels ----

    /// <summary><c>2026-09-18 14:05</c>: the stored UTC moment in <paramref name="zone"/>.</summary>
    public static string Moment(DateTimeOffset at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary><c>#12</c>.</summary>
    public static string Id(long id) => "#" + id.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>1 turn</c> / <c>12 turns</c>.</summary>
    public static string Turns(int turns) => turns.ToString(CultureInfo.InvariantCulture) + (turns == 1 ? " turn" : " turns");

    /// <summary><c>1 session</c> / <c>12 sessions</c>.</summary>
    public static string Sessions(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " session" : " sessions");

    // ---- ages (2026-09-21) ----

    /// <summary>
    /// The age <c>/sessions purge older</c> takes: a bare number is days (the 2026-09-18 grammar,
    /// <c>purge older 30</c>, kept so the README and habit still hold; <c>0</c> stays legal, every
    /// session but one updated this instant), otherwise a <see cref="Timers.TimerText.TryParseDuration(string, bool, out TimeSpan)"/>
    /// duration with days on: <c>12h</c>, <c>90m</c>, <c>2 hours</c>, <c>1d 6h</c>, <c>1h30m</c>,
    /// each unit at most once and never zero. Up to six digits a number, as the timers allow.
    /// </summary>
    public static bool TryParseAge(string text, out TimeSpan age)
    {
        age = TimeSpan.Zero;
        if (text is null)
        {
            return false;
        }

        string s = text.Trim();
        if (s.Length > 0 && s.All(char.IsAsciiDigit))
        {
            if (s.Length > 6)
            {
                return false;
            }

            age = TimeSpan.FromDays(int.Parse(s, CultureInfo.InvariantCulture));
            return true;
        }

        return Timers.TimerText.TryParseDuration(s, withDays: true, out age);
    }

    /// <summary>
    /// <c>30 days</c>, <c>12 hours</c>, <c>1 day 6 hours</c>, <c>45 minutes</c>, <c>1 hour 30 minutes 10 seconds</c>;
    /// the non-zero parts largest first, and <c>0 days</c> for nothing at all. Its own formatter, not
    /// <see cref="Timers.TimerText.Describe"/>: a timer has no day unit and says <c>26 hours</c>.
    /// </summary>
    public static string Age(TimeSpan age)
    {
        long total = (long)Math.Round(age.TotalSeconds);
        long d = total / 86400, h = total % 86400 / 3600, m = total % 3600 / 60, s = total % 60;
        var parts = new List<string>(4);
        if (d > 0)
        {
            parts.Add(Llm.Tools.ClockText.Count((int)d, "day"));
        }

        if (h > 0)
        {
            parts.Add(Llm.Tools.ClockText.Count((int)h, "hour"));
        }

        if (m > 0)
        {
            parts.Add(Llm.Tools.ClockText.Count((int)m, "minute"));
        }

        if (s > 0)
        {
            parts.Add(Llm.Tools.ClockText.Count((int)s, "second"));
        }

        return parts.Count == 0 ? Llm.Tools.ClockText.Count(0, "day") : string.Join(' ', parts);
    }

    /// <summary>The one-line label of a session the tool and the pane share: <c>#12 · 2026-09-18 14:05 · 12 turns · Title</c>. Pinned.</summary>
    public static string Label(SessionSummary session, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Id(session.Id) + " · " + Moment(session.UpdatedAt, zone) + " · " + Turns(session.Turns) + " · " + session.Title;
    }

    /// <summary>The replay's tool line under a restored user row: <c>1 tool call</c> / <c>3 tool calls</c>.</summary>
    public static string ToolCallsNote(int calls) => calls.ToString(CultureInfo.InvariantCulture) + (calls == 1 ? " tool call" : " tool calls");

    // ---- the tool's results ----

    public static string SearchHeader(string query, int count) => "Searched \"" + query + "\" (" + Sessions(count) + "):";

    public static string NoHits(string query) => "Searched \"" + query + "\": no earlier session matches";

    /// <summary>The numbered hits under <see cref="SearchHeader"/>: the label, then the matching turn's snippet indented under it.</summary>
    public static string SearchResults(string query, IReadOnlyList<SessionHit> hits, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0)
        {
            return NoHits(query);
        }

        var sb = new StringBuilder(SearchHeader(query, hits.Count));
        foreach (var hit in hits)
        {
            sb.Append('\n').Append(Label(hit.Session, zone));
            sb.Append("\n   turn ").Append(hit.Turn.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(Flatten(hit.Snippet));
        }

        return sb.ToString();
    }

    public static string ListHeader(int count) => "Sessions, newest first (" + Sessions(count) + "):";

    /// <summary>The list under <see cref="ListHeader"/>, one <see cref="Label"/> per session; <see cref="NoSessions"/> for none.</summary>
    public static string ListResults(IReadOnlyList<SessionSummary> sessions, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
        {
            return NoSessions;
        }

        var sb = new StringBuilder(ListHeader(sessions.Count));
        foreach (var session in sessions)
        {
            sb.Append('\n').Append(Label(session, zone));
        }

        return sb.ToString();
    }

    /// <summary><c>Session #12 "Title" (2026-09-18 14:05, 12 turns), turns 3–5:</c>.</summary>
    public static string ReadHeader(SessionSummary session, int from, int to, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(session);
        return "Session " + Id(session.Id) + " \"" + session.Title + "\" (" + Moment(session.StartedAt, zone) + ", " + Turns(session.Turns) + "), turns "
            + from.ToString(CultureInfo.InvariantCulture) + "–" + to.ToString(CultureInfo.InvariantCulture) + ":";
    }

    /// <summary>The cut line when a read passes <see cref="MaxReadChars"/>: <c>… (turns 7–12 cut; call read with from_turn 7)</c>.</summary>
    public static string ReadCut(int from, int to) =>
        "… (turns " + from.ToString(CultureInfo.InvariantCulture) + "–" + to.ToString(CultureInfo.InvariantCulture) + " cut; call read with from_turn " + from.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>
    /// The turns <paramref name="from"/>..<paramref name="to"/> (1-based, inclusive, clamped to what
    /// the session holds) under <see cref="ReadHeader"/>: <c>Turn n</c>, <c>You:</c>, <c>Neon:</c>
    /// per turn, the whole cut at <see cref="MaxReadChars"/> with <see cref="ReadCut"/> naming the
    /// turns left out. <see cref="NoTurns"/> for a range past the end.
    /// </summary>
    public static string Read(SessionRecord record, int from, int to, TimeZoneInfo zone) => Read(record, from, to, zone, MaxReadChars);

    /// <summary><see cref="Read(SessionRecord, int, int, TimeZoneInfo)"/> cut at <paramref name="maxChars"/> instead — the reflection's pass reads several sessions at once (2026-09-19).</summary>
    public static string Read(SessionRecord record, int from, int to, TimeZoneInfo zone, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        int last = record.Turns.Count;
        from = Math.Max(1, from);
        to = to <= 0 ? last : Math.Min(to, last);
        if (from > to)
        {
            return NoTurns(record.Summary, last);
        }

        var sb = new StringBuilder(ReadHeader(record.Summary, from, to, zone));
        for (int i = from; i <= to; i++)
        {
            var turn = record.Turns[i - 1];
            string block = "\n\nTurn " + i.ToString(CultureInfo.InvariantCulture) + (turn.Cancelled ? " (cut short)" : "") + "\nYou: " + turn.UserText.Trim() + "\nNeon: " + turn.ReplyText.Trim();
            if (sb.Length + block.Length > maxChars)
            {
                sb.Append("\n\n").Append(ReadCut(i, to));
                break;
            }

            sb.Append(block);
        }

        return sb.ToString();
    }

    public static string NoTurns(SessionSummary session, int last) =>
        "Error: session " + Id(session.Id) + " has " + Turns(last) + "; from_turn is past the end";

    public static string Missing(long id) => "Error: no session " + Id(id) + "; call list or search first";

    public static string BadAction(string raw) => "Error: '" + raw.Trim() + "' is not one of search, list, read for 'action'";

    public static string BadResultCount(int min, int max) =>
        "Error: max_results must be " + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture);

    /// <summary>The text cut to <paramref name="max"/> characters with an ellipsis in the last place.</summary>
    public static string Cut(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    private static string Flatten(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
