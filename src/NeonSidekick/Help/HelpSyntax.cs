using System.Text.RegularExpressions;

namespace NeonSidekick.Help;

/// <summary>What a stretch of a command form is, as <c>/help</c> colours it (<see cref="HelpSyntax.Runs"/>).</summary>
public enum HelpSyntaxPart
{
    /// <summary>What is typed as it stands: the command, a subcommand or keyword, a <c>--flag</c>.</summary>
    Word,

    /// <summary>A placeholder the user fills in, its angle brackets included: <c>&lt;name&gt;</c>.</summary>
    Slot,

    /// <summary>The notation around them: <c>[</c>, <c>]</c>, <c>|</c>, <c>...</c>.</summary>
    Mark,

    /// <summary>The blanks between them.</summary>
    Space,
}

/// <summary>
/// The command forms' notation (2026-10-05, the user's ask: one way to write every command's forms, shown in their own column of
/// <c>/help</c>'s Commands tabs in two tones). A form is its command word, then: subcommands, keywords and <c>--flags</c> written as
/// typed; placeholders as <c>&lt;lowercase-hyphenated&gt;</c>; optional parts in <c>[ … ]</c>; choices as <c>a|b</c>, no blanks
/// around the bar; a repeat as <c>...</c>. One shape per form, the bare command folded into an optional where it can be
/// (<c>/cwd [&lt;path&gt;|~|browse]</c>). <see cref="HelpCommands"/> holds every form (for <c>neon_help</c> too); this reads them.
/// Pure.
/// </summary>
public static partial class HelpSyntax
{
    /// <summary>
    /// The forms <c>/help</c> shows beside <paramref name="command"/>: <see cref="HelpCommands"/>' syntaxes, or none when the
    /// command's one form is the bare word (<c>/clear</c> says all there is in its description).
    /// </summary>
    public static IReadOnlyList<string> Forms(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var help = HelpCommands.Commands.FirstOrDefault(c => string.Equals(c.Command, command, StringComparison.Ordinal));
        if (help is null || (help.Forms.Count == 1 && help.Forms[0].Syntax == command))
        {
            return [];
        }

        return help.Forms.Select(f => f.Syntax).ToArray();
    }

    /// <summary><paramref name="form"/> cut into its parts, in order; the parts' texts joined give the form back.</summary>
    public static IReadOnlyList<(string Text, HelpSyntaxPart Part)> Runs(string form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var runs = new List<(string, HelpSyntaxPart)>();
        int i = 0;
        while (i < form.Length)
        {
            char c = form[i];
            int end;
            HelpSyntaxPart part;
            if (c == ' ')
            {
                end = i;
                while (end < form.Length && form[end] == ' ')
                {
                    end++;
                }

                part = HelpSyntaxPart.Space;
            }
            else if (c == '<' && form.IndexOf('>', i) is int close and > 0)
            {
                end = close + 1;
                part = HelpSyntaxPart.Slot;
            }
            else if (c is '[' or ']' or '|')
            {
                end = i + 1;
                part = HelpSyntaxPart.Mark;
            }
            else if (string.CompareOrdinal(form, i, "...", 0, 3) == 0)
            {
                end = i + 3;
                part = HelpSyntaxPart.Mark;
            }
            else
            {
                end = i + 1;
                while (end < form.Length && form[end] is not (' ' or '<' or '[' or ']' or '|') && string.CompareOrdinal(form, end, "...", 0, 3) != 0)
                {
                    end++;
                }

                part = HelpSyntaxPart.Word;
            }

            runs.Add((form[i..end], part));
            i = end;
        }

        return runs;
    }

    /// <summary>
    /// Null when <paramref name="form"/> follows the notation for <paramref name="command"/>; else what is wrong with it: it starts
    /// with the command (a blank or the end after it), its brackets pair up and hold something, no blank stands beside a bar, every
    /// placeholder is <c>&lt;lowercase-hyphenated&gt;</c>, and no blank is doubled.
    /// </summary>
    public static string? Problem(string form, string command)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(command);
        if (!(form == command || form.StartsWith(command + " ", StringComparison.Ordinal)))
        {
            return "does not start with " + command;
        }

        if (form.Contains("  ", StringComparison.Ordinal) || form != form.Trim())
        {
            return "has a doubled or an outer blank";
        }

        if (form.Contains(" |", StringComparison.Ordinal) || form.Contains("| ", StringComparison.Ordinal))
        {
            return "has a blank beside a bar";
        }

        if (form.Contains("[]", StringComparison.Ordinal) || form.Contains("<>", StringComparison.Ordinal))
        {
            return "has empty brackets";
        }

        int depth = 0;
        foreach (char c in form)
        {
            depth += c == '[' ? 1 : c == ']' ? -1 : 0;
            if (depth < 0)
            {
                return "closes a bracket it never opened";
            }
        }

        if (depth != 0)
        {
            return "leaves a bracket open";
        }

        foreach (Match slot in AnySlot().Matches(form))
        {
            if (!GoodSlot().IsMatch(slot.Value))
            {
                return "has the placeholder " + slot.Value + ", not <lowercase-hyphenated>";
            }
        }

        return form.Count(c => c == '<') != form.Count(c => c == '>') ? "has an unpaired angle bracket" : null;
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex AnySlot();

    [GeneratedRegex("^<[a-z0-9]+(-[a-z0-9]+)*>$")]
    private static partial Regex GoodSlot();
}
