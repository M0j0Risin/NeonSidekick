using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>A candidate of the command or skill list: what a pick writes and a dim note beside it (the help summary, the skill's description).</summary>
public sealed record CompletionItem(string Text, string Note);

/// <summary>
/// What the argument source answers for a command's argument (2026-09-17): a word list
/// (<paramref name="Words"/>, drawn with their notes, narrowed by the source) — or, for a command
/// whose argument is a sandbox path (<c>/speak</c>), the <c>@</c>-mention shape:
/// <paramref name="Paths"/> as <c>WorkingDirectory.Complete</c> lists them (folders first, a
/// trailing <c>/</c>, the folder-mode rules on a pick) and <paramref name="Truncated"/> when a
/// cap cut them. <see cref="IsPathList"/> tells the two apart; <see cref="None"/> opens nothing.
/// </summary>
public sealed record ArgumentList(IReadOnlyList<CompletionItem> Words, IReadOnlyList<string>? Paths = null, bool Truncated = false)
{
    public static readonly ArgumentList None = new([]);

    public bool IsPathList => Paths is not null;
}

/// <summary>
/// A completion list open over the input line: the word's span in the draft
/// (<paramref name="Start"/> is the <c>@</c> of a mention, the <c>/</c> of a command, the first
/// letter of a skill name; <paramref name="End"/> one past the word), the <paramref name="Query"/>
/// between the start (after the <c>@</c>) and the cursor, the <paramref name="Matches"/>
/// (relative paths with a trailing <c>/</c> on a folder; commands; skill names),
/// <paramref name="Truncated"/> when a cap cut them, the highlighted row and the viewport's first
/// row. <see cref="Notes"/>, set on a command or skill list, is the dim note per match (null on
/// the path list).
/// </summary>
public sealed record MentionList(int Start, int End, string Query, IReadOnlyList<string> Matches, bool Truncated, int Cursor, int First)
{
    /// <summary>The dim note per match (a command's summary, a skill's description); null on the path list.</summary>
    public IReadOnlyList<string>? Notes { get; init; }

    /// <summary>What a pick writes ahead of the match (2026-09-17): <c>@</c> on the path list, <c>#</c> on the skill-mention list, <c>$</c> on the tool-mention list (2026-09-19), <c>%</c> on the SQL connections' (2026-09-23), <c>^</c> on the ComfyUI workflows' (2026-09-24), nothing on a command or argument list.</summary>
    public string Prefix { get; init; } = "";

    /// <summary>Whether this is a word list with notes (a command, an argument, a <c>#</c>skill), drawn by <see cref="MentionCompleter.WordRows"/>; else the path list.</summary>
    public bool IsWordList => Notes is not null;

    /// <summary>The highlight moved by <paramref name="step"/> rows, wrapping at either end.</summary>
    public MentionList Move(int step)
    {
        if (Matches.Count == 0)
        {
            return this;
        }

        int count = Matches.Count;
        return this with { Cursor = ((Cursor + step) % count + count) % count };
    }
}

/// <summary>
/// The pure side of the input line's @-mention completion: where the <c>@</c>word under the cursor
/// is, what applying a pick writes, and how the list lays out as rows. The disk side is
/// <c>WorkingDirectory.Complete</c>; the keys and the overlay are <see cref="InputLine"/>'s.
/// </summary>
public static class MentionCompleter
{
    /// <summary>The hint row while the list is open. The user's wording (2026-09-16), pinned.</summary>
    public const string Hint = "↑/↓ pick · Enter/Tab apply · ESC close";

    /// <summary>The dim row under the list when a cap cut it. Pinned.</summary>
    public const string TruncatedRow = "… keep typing to narrow the list";

    /// <summary>The most list rows shown, fewer when the window is short (<see cref="ScreenPane.MaxOverlayRows"/>).</summary>
    public const int MaxRows = 8;

    /// <summary>
    /// The <c>@</c>word under the cursor: a word starts at the draft's start or after whitespace
    /// (a line break included) or a paste token, ends at the next of those or the draft's end;
    /// it is a mention when it starts with <c>@</c> and the cursor sits after the <c>@</c> and
    /// no later than its end. <paramref name="query"/> is the text between the <c>@</c> and the
    /// cursor. <c>a@b.com</c> is no mention (the <c>@</c> is inside a word); a bare <c>@</c> is
    /// one with an empty query.
    /// </summary>
    public static bool TryFind(string text, int cursor, out int start, out int end, out string query) =>
        TryFind(text, cursor, '@', out start, out end, out query);

    /// <summary>
    /// The same word rule for any <paramref name="trigger"/> character (2026-09-17): <c>@</c> for
    /// the path list, <c>#</c> for the skill-mention list.
    /// </summary>
    public static bool TryFind(string text, int cursor, char trigger, out int start, out int end, out string query)
    {
        ArgumentNullException.ThrowIfNull(text);
        cursor = Math.Clamp(cursor, 0, text.Length);

        start = cursor;
        while (start > 0 && !IsBoundary(text[start - 1]))
        {
            start--;
        }

        end = cursor;
        while (end < text.Length && !IsBoundary(text[end]))
        {
            end++;
        }

        if (start < text.Length && text[start] == trigger && cursor > start)
        {
            query = text[(start + 1)..cursor];
            return true;
        }

        query = "";
        return false;
    }

    /// <summary>The blank cells between a word and its note on a command or skill row.</summary>
    public const int NoteGap = 2;

    /// <summary>
    /// The command word under the cursor: the draft's first character is <c>/</c> (index 0, no
    /// leading whitespace), the word runs to the first whitespace or paste token (<paramref name="end"/>)
    /// and the cursor sits after the <c>/</c> and no later than the word's end.
    /// <paramref name="query"/> is the text from the <c>/</c> to the cursor (<c>/se</c>).
    /// <c>see /tmp</c> is no command; a cursor past the word (<c>/settings x</c>) is none either.
    /// </summary>
    public static bool TryFindCommand(string text, int cursor, out int end, out string query)
    {
        ArgumentNullException.ThrowIfNull(text);
        cursor = Math.Clamp(cursor, 0, text.Length);
        end = 0;
        query = "";
        if (text.Length == 0 || text[0] != '/' || cursor < 1)
        {
            return false;
        }

        while (end < text.Length && !IsBoundary(text[end]))
        {
            end++;
        }

        if (cursor > end)
        {
            return false;
        }

        query = text[..cursor];
        return true;
    }

    /// <summary>
    /// A command's argument under the cursor: the draft starts with a <c>/</c>word
    /// (<paramref name="command"/>, as typed — an alias included; the app resolves it) and at least
    /// one whitespace character; the argument runs from the first character after that run
    /// (<paramref name="start"/>; the draft's end when nothing follows) and the cursor sits at or
    /// after it; <paramref name="end"/> is the end of the word the cursor is in (the cursor itself
    /// on whitespace). <paramref name="query"/> is the WHOLE argument text from
    /// <paramref name="start"/> to the cursor, spaces included (<c>delete </c>, <c>stop the big</c>),
    /// so a multi-word argument is one candidate and a word after a complete one (<c>on x</c>)
    /// matches nothing. A cursor inside the whitespace run before more text is no argument.
    /// </summary>
    public static bool TryFindArgument(string text, int cursor, out string command, out int start, out int end, out string query)
    {
        ArgumentNullException.ThrowIfNull(text);
        cursor = Math.Clamp(cursor, 0, text.Length);
        command = query = "";
        start = end = 0;
        if (text.Length == 0 || text[0] != '/')
        {
            return false;
        }

        int word = 0;
        while (word < text.Length && !IsBoundary(text[word]))
        {
            word++;
        }

        if (word == text.Length || !char.IsWhiteSpace(text[word]))
        {
            return false;
        }

        start = word;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (cursor < start)
        {
            return false;
        }

        end = cursor;
        while (end < text.Length && !IsBoundary(text[end]))
        {
            end++;
        }

        command = text[..word];
        query = text[start..cursor];
        return true;
    }

    /// <summary>
    /// The items whose text starts with <paramref name="query"/> (case ignored), in their order —
    /// and none at all when the query IS one of them: the word is complete, so the list closes
    /// and Enter sends (<c>/exit</c> exits in one press; a longer word sharing the prefix reopens the list at the next letter).
    /// </summary>
    public static IReadOnlyList<CompletionItem> Matches(IReadOnlyList<CompletionItem> items, string query)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(query);
        var matches = new List<CompletionItem>();
        foreach (var item in items)
        {
            if (string.Equals(item.Text, query, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            if (item.Text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(item);
            }
        }

        return matches;
    }

    /// <summary>
    /// The draft with the word at <paramref name="start"/>..<paramref name="end"/> replaced by
    /// <paramref name="replacement"/> (<c>@path</c>, a command, a skill name) and a space when
    /// <paramref name="space"/> — none for a folder the list stays open on — and the cursor just after it.
    /// </summary>
    public static (string Text, int Cursor) Apply(string text, int start, int end, string replacement, bool space)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(replacement);
        string written = replacement + (space ? " " : "");
        string replaced = string.Concat(text.AsSpan(0, start), written, text.AsSpan(end));
        return (replaced, start + written.Length);
    }

    /// <summary>
    /// The list as overlay rows (markup): the matches in view — <see cref="MenuPane.Viewport"/>
    /// over <paramref name="capacity"/> rows, one fewer when the list is truncated — with
    /// <see cref="MenuPane.Pointer"/> and the menu highlight on the cursor's row, the
    /// <see cref="MenuPane.MoreHint"/> row when the view is cut, then <see cref="TruncatedRow"/>
    /// when a cap cut the matches; and the viewport's new first row. The path list's shape; a
    /// command or skill list draws through <see cref="WordRows"/>.
    /// </summary>
    public static (IReadOnlyList<string> Rows, int First) Rows(MentionList list, int capacity)
    {
        ArgumentNullException.ThrowIfNull(list);
        int room = Math.Max(1, capacity - (list.Truncated ? 1 : 0));
        var (first, shown) = MenuPane.Viewport(list.Matches.Count, list.Cursor, room, list.First);
        var rows = new List<string>(shown + 2);
        for (int i = 0; i < shown; i++)
        {
            int r = first + i;
            rows.Add(MenuPane.RowMarkup(Markup.Escape(list.Matches[r]), r == list.Cursor));
        }

        if (shown < list.Matches.Count)
        {
            rows.Add(Theme.DimMarkup(MenuPane.NoPointer + MenuPane.MoreHint));
        }

        if (list.Truncated)
        {
            rows.Add(Theme.DimMarkup(MenuPane.NoPointer + TruncatedRow));
        }

        return (rows, first);
    }

    /// <summary>
    /// A command or skill list as overlay rows: the matches in view (<see cref="MenuPane.Viewport"/>
    /// over <paramref name="capacity"/> rows), each the pointer or its blank, the word padded to the
    /// longest shown plus <see cref="NoteGap"/>, then its note dim — one <see cref="FittedLine"/>
    /// per row, so a long note is cut at the row's edge with an ellipsis (the user's call,
    /// 2026-09-16; Spectre's <c>Overflow.Ellipsis</c> would wrap it), the cursor's row in the menu
    /// highlight throughout; then the <see cref="MenuPane.MoreHint"/> row when the view is cut; and
    /// the viewport's new first row. <see cref="WordRowText"/> is each row's text.
    /// </summary>
    public static (IReadOnlyList<IRenderable> Rows, int First) WordRows(MentionList list, int capacity)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list.Notes is null)
        {
            throw new ArgumentException("A word list carries a note per match.", nameof(list));
        }

        var (first, shown) = MenuPane.Viewport(list.Matches.Count, list.Cursor, Math.Max(1, capacity), list.First);
        int width = 0;
        for (int i = 0; i < shown; i++)
        {
            width = Math.Max(width, TextCells.Width(list.Matches[first + i]));
        }

        width += NoteGap;
        var rows = new List<IRenderable>(shown + 1);
        for (int i = 0; i < shown; i++)
        {
            int r = first + i;
            bool active = r == list.Cursor;
            rows.Add(new FittedLine(
                WordRowText(list.Matches[r], list.Notes[r], width, active),
                active ? Theme.MenuHighlightDim : Theme.DimText,
                leadCells: TextCells.Width(MenuPane.Pointer) + width,
                leadStyle: active ? Theme.MenuHighlight : Theme.Body));
        }

        if (shown < list.Matches.Count)
        {
            rows.Add(new Markup(Theme.DimMarkup(MenuPane.NoPointer + MenuPane.MoreHint)));
        }

        return (rows, first);
    }

    /// <summary>One row of a word list as plain text: the pointer (or its blank), the word padded to <paramref name="width"/> cells, the note. Pinned.</summary>
    public static string WordRowText(string word, string note, int width, bool active)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(note);
        return (active ? MenuPane.Pointer : MenuPane.NoPointer) + word + new string(' ', Math.Max(0, width - TextCells.Width(word))) + note;
    }

    private static bool IsBoundary(char c) => char.IsWhiteSpace(c) || PasteBlocks.IsToken(c);

    /// <summary>The characters that start a mention: <c>@</c> a path, <c>#</c> a skill, <c>$</c> a tool, <c>%</c> a connection, <c>^</c> a workflow. Pinned.</summary>
    public const string Triggers = "@#$%^";

    /// <summary>
    /// The mention character the word under <paramref name="cursor"/> starts with (2026-09-30, the user's ask: a mention inside a
    /// slash command's text completes as it does in a message), by <see cref="TryFind(string, int, char, out int, out int, out string)"/>'s
    /// word rule; null for a plain word, a cursor still on the character itself, or one in the middle of a word (<c>a@b</c>).
    /// </summary>
    public static char? TriggerAt(string text, int cursor)
    {
        ArgumentNullException.ThrowIfNull(text);
        cursor = Math.Clamp(cursor, 0, text.Length);
        int start = cursor;
        while (start > 0 && !IsBoundary(text[start - 1]))
        {
            start--;
        }

        return start < text.Length && cursor > start && Triggers.Contains(text[start], StringComparison.Ordinal) ? text[start] : null;
    }
}
