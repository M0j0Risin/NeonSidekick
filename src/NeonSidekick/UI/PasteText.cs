using System.Text;

namespace NeonSidekick.UI;

/// <summary>
/// What pasted text becomes on the input line. <see cref="Normalize"/> keeps the block's shape:
/// every line break (<c>\r\n</c>, <c>\r</c>, <c>\n</c>) is one <c>'\n'</c>, a tab is
/// <see cref="TabSpaces"/> spaces (indentation of pasted code survives; a tab has no cell width),
/// trailing line breaks are dropped (a copied block ends with one, and an empty last row is
/// noise), every control character is dropped (private-use characters too until 2026-10-04, since the line's
/// paste tokens live there; the line now keeps one as a literal token, <see cref="PasteBlocks.Literal"/>, the user's report: pasted
/// Nerd Font glyphs vanished), nothing else is trimmed (what landed is visible
/// and editable). <see cref="Flatten"/> is the one-paragraph form for a single-line field: line
/// breaks and tabs are one space each. Both pinned.
/// </summary>
public static class PasteText
{
    /// <summary>What a pasted tab becomes.</summary>
    public const int TabSpaces = 4;

    public static string Normalize(string text) => Clean(text, oneLine: false);

    public static string Flatten(string text) => Clean(text, oneLine: true);

    private static string Clean(string text, bool oneLine)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                result.Append(oneLine ? ' ' : '\n');
            }
            else if (c == '\n')
            {
                result.Append(oneLine ? ' ' : '\n');
            }
            else if (c == '\t')
            {
                result.Append(' ', oneLine ? 1 : TabSpaces);
            }
            else if (!char.IsControl(c))
            {
                result.Append(c);
            }
        }

        if (!oneLine)
        {
            while (result.Length > 0 && result[^1] == '\n')
            {
                result.Length--;
            }
        }

        return result.ToString();
    }
}
