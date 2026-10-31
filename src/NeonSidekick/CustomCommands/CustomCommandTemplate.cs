using System.Globalization;
using System.Text;

namespace NeonSidekick.CustomCommands;

/// <summary>
/// A custom command's body filled with what was typed after its name, by Claude Code's rules (code.claude.com/docs/en/slash-commands, read
/// 2026-10-30), so a Claude command file behaves the same here:
/// <list type="bullet">
/// <item><c>$ARGUMENTS</c> is the typed text, trimmed; <c>$ARGUMENTS[N]</c> and <c>$N</c> the Nth word, 0-based (<c>$0</c> the first), a word
/// grouped by <c>"…"</c> or <c>'…'</c> with its quotes taken off (<see cref="SplitWords"/>); an indexed placeholder with no such word stays
/// as written.</item>
/// <item>A declared name (<c>arguments: [issue, branch]</c>) makes <c>$issue</c> the first word and <c>$branch</c> the second, empty when there
/// is none. The whole identifier after the <c>$</c> is read, so <c>$pathname</c> is never <c>$path</c> + <c>name</c>, and a <c>$word</c> that
/// is no declared name (a <c>$web_search</c> tool mention) is left alone.</item>
/// <item>One <c>\</c> right before a placeholder makes it literal, the backslash dropped; <c>\\$1</c> keeps both and still expands; a <c>\</c>
/// before any other <c>$</c> is left as it is.</item>
/// <item>One pass, left to right over the body alone: a typed value holding <c>$1</c> goes in as text.</item>
/// <item>No placeholder took a value and words were typed: <c>\n\nARGUMENTS: &lt;text&gt;</c> is added (a named one counts even when empty; an
/// indexed one left as written doesn't).</item>
/// </list>
/// The result is always sent as a message and never parsed again, so a body starting with <c>/</c> runs no command. Pure.
/// </summary>
public static class CustomCommandTemplate
{
    private const string ArgumentsWord = "ARGUMENTS";

    /// <summary>The line added when no placeholder took the typed text.</summary>
    public const string AppendedLabel = "ARGUMENTS: ";

    /// <summary>
    /// <paramref name="body"/> filled from <paramref name="arguments"/> (the text after the name), <paramref name="names"/> the declared ones:
    /// the prompt to send, the <c>ARGUMENTS:</c> line added when no placeholder took a value (decided here, 2026-10-31, review P6-10).
    /// </summary>
    public static string Expand(string body, string arguments, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(names);
        string typed = arguments.Trim();
        var words = SplitWords(typed);
        var declared = new HashSet<string>(names, StringComparer.Ordinal);
        var sb = new StringBuilder(body.Length + typed.Length);
        bool filled = false;
        int i = 0;
        while (i < body.Length)
        {
            char c = body[i];
            if (c == '\\')
            {
                int run = i;
                while (run < body.Length && body[run] == '\\')
                {
                    run++;
                }

                int slashes = run - i;
                if (run < body.Length && body[run] == '$' && TryPlaceholder(body, run, declared, out int end, out _))
                {
                    if (slashes == 1)
                    {
                        sb.Append(body, run, end - run);   // escaped: the placeholder as written, its backslash dropped
                        i = end;
                        continue;
                    }

                    sb.Append(body, i, slashes);   // \\$1: both kept, and the placeholder expands below
                    i = run;
                    continue;
                }

                sb.Append(body, i, slashes);
                i = run;
                continue;
            }

            if (c == '$' && TryPlaceholder(body, i, declared, out int stop, out var placeholder))
            {
                string? value = placeholder.Kind switch
                {
                    PlaceholderKind.All => typed,
                    PlaceholderKind.Index => placeholder.Index < words.Count ? words[placeholder.Index] : null,
                    _ => IndexOf(names, placeholder.Name) is var at && at < words.Count ? words[at] : "",
                };

                if (value is null)
                {
                    sb.Append(body, i, stop - i);   // no such word: as written
                }
                else
                {
                    sb.Append(value);
                    filled = true;
                }

                i = stop;
                continue;
            }

            sb.Append(c);
            i++;
        }

        string text = sb.ToString();
        if (!filled && typed.Length > 0)
        {
            text = text.TrimEnd() + "\n\n" + AppendedLabel + typed;
        }

        return text;
    }

    /// <summary>
    /// The typed text cut into words at white space, shell-style, with two rules for English text (2026-10-31, review P6-2, P6-4):
    /// <list type="bullet">
    /// <item>A <c>"</c> or <c>'</c>, at a word's start or inside one, opens a group only when the next same quote ends a word (white space
    /// or the end after it); the quotes come off. Any other quote is kept as written, so <c>'cause it's broken</c> is three words with its
    /// apostrophes, <c>--title="two words" v2</c> is <c>--title=two words</c> and <c>v2</c>, and Claude's documented <c>"hello world" second</c>
    /// is two. A real shell would open a quote at <c>it's</c>; this is deliberate.</item>
    /// <item>An attachment label (<c>[Image #1]</c>, <c>[PDF #2 a b.pdf]</c>, review P3-1) is one word, spaces and all, so the prompt names
    /// the picture whole and nothing is added back for it.</item>
    /// </list>
    /// Pure.
    /// </summary>
    public static IReadOnlyList<string> SplitWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var word = new StringBuilder();
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                char c = text[i];
                if (UI.PasteBlocks.LabelLengthAt(text, i) is > 0 and var label)
                {
                    word.Append(text, i, label);
                    i += label;
                }
                else if (c is '"' or '\'' && ClosingQuote(text, i) is { } close)
                {
                    word.Append(text, i + 1, close - i - 1);
                    i = close + 1;
                }
                else
                {
                    word.Append(c);
                    i++;
                }
            }

            words.Add(word.ToString());
        }

        return words;
    }

    // The next quote like text[open] when it ends a word (white space or the end after it), else null.
    private static int? ClosingQuote(string text, int open)
    {
        int close = text.IndexOf(text[open], open + 1);
        return close > open && (close + 1 == text.Length || char.IsWhiteSpace(text[close + 1])) ? close : null;
    }

    /// <summary>
    /// <paramref name="text"/> made safe as a body that, run with nothing typed, sends it back as it is (<c>/commands save</c>): a <c>\</c> before
    /// each <c>$digit</c> and <c>$ARGUMENTS</c>. <paramref name="mayReadDifferently"/> says a <c>\</c> already stood before one (<c>\$1</c>), which
    /// Claude's rules can't write back: <c>\\$1</c> still expands. Pure.
    /// </summary>
    public static string Escape(string text, out bool mayReadDifferently)
    {
        ArgumentNullException.ThrowIfNull(text);
        var none = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder(text.Length + 8);
        mayReadDifferently = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '$' && TryPlaceholder(text, i, none, out _, out _))
            {
                if (i > 0 && text[i - 1] == '\\')
                {
                    mayReadDifferently = true;
                }

                sb.Append('\\');
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    private enum PlaceholderKind
    {
        All,
        Index,
        Named,
    }

    private readonly record struct Placeholder(PlaceholderKind Kind, int Index, string Name);

    // A placeholder starting at body[at] (a '$'): $ARGUMENTS[N], $ARGUMENTS, $N, or a declared $name; end is past it.
    private static bool TryPlaceholder(string body, int at, IReadOnlySet<string> declared, out int end, out Placeholder placeholder)
    {
        end = at;
        placeholder = default;
        int start = at + 1;
        if (start >= body.Length)
        {
            return false;
        }

        if (char.IsAsciiDigit(body[start]))
        {
            int stop = start;
            while (stop < body.Length && char.IsAsciiDigit(body[stop]))
            {
                stop++;
            }

            end = stop;
            placeholder = new Placeholder(PlaceholderKind.Index, Index(body[start..stop]), "");
            return true;
        }

        int word = start;
        while (word < body.Length && (char.IsAsciiLetterOrDigit(body[word]) || body[word] == '_'))
        {
            word++;
        }

        if (word == start)
        {
            return false;
        }

        string identifier = body[start..word];
        if (identifier == ArgumentsWord)
        {
            if (word < body.Length && body[word] == '[')
            {
                int digits = word + 1;
                while (digits < body.Length && char.IsAsciiDigit(body[digits]))
                {
                    digits++;
                }

                if (digits > word + 1 && digits < body.Length && body[digits] == ']')
                {
                    end = digits + 1;
                    placeholder = new Placeholder(PlaceholderKind.Index, Index(body[(word + 1)..digits]), "");
                    return true;
                }
            }

            end = word;
            placeholder = new Placeholder(PlaceholderKind.All, 0, "");
            return true;
        }

        if (declared.Contains(identifier))
        {
            end = word;
            placeholder = new Placeholder(PlaceholderKind.Named, 0, identifier);
            return true;
        }

        return false;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    // A word index; one too large to be any word reads as past the end.
    private static int Index(string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;
}
