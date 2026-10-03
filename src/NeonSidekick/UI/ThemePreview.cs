using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The theme pickers' preview (2026-10-02, the user's ask: "show a preview of the theme in an area to the right of the list,
/// like the Theme Atlas", without the screen starting over at every row): a small mock screen in one palette's own styles
/// (<see cref="Theme.StylesOf"/>, never <see cref="Theme.Use"/>) — the banner and its rule in the gradient, a user line, a
/// reply with bold, code, italic, a bullet and a quote, a highlighted code block, the thinking slab, the notice, good, warning
/// and error lines, the pane rule, a highlighted menu row, the input row with a selection, the spinner and a paste label, the
/// hint row and the ghost text. Every cell is on the palette's <see cref="ThemePalette.Bg"/>, the way the Atlas paints each
/// screen: the card reads as the theme's own window, apart from the list beside it.
/// <para>
/// <see cref="Lines"/> gives exactly the rows asked for: all of the screen and blank rows under it when there is room, else the
/// lines that tell a theme apart most (the banner and the rule first, blank rows last), kept in screen order. Each line is one
/// row of the width it is drawn at, cut or padded to it. Pure; <see cref="MenuPage.Side"/> draws it beside the list.
/// </para>
/// </summary>
public static class ThemePreview
{
    /// <summary>The cells kept clear at the card's left and right edges.</summary>
    public const int Margin = 1;

    /// <summary>The cells the code block's slab runs past its longest line.</summary>
    private const int SlabPad = 2;

    /// <summary>A blank row's rank: the first to go.</summary>
    private const int Blank = 9;

    /// <summary>
    /// <paramref name="rows"/> lines of <paramref name="palette"/>'s preview for a card <paramref name="width"/> cells wide: the
    /// banner reads <paramref name="banner"/> and <c>v</c><paramref name="version"/>. None for no rows.
    /// </summary>
    public static IReadOnlyList<IRenderable> Lines(ThemePalette palette, int width, int rows, string banner, string version)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(banner);
        ArgumentNullException.ThrowIfNull(version);
        if (rows <= 0)
        {
            return [];
        }

        var screen = Screen(palette, Math.Max(1, width - 2 * Margin), banner, version);
        IEnumerable<Line> kept = screen.Count <= rows
            ? screen
            : screen.Select((line, index) => (line, index)).OrderBy(x => x.line.Rank).ThenBy(x => x.index).Take(rows).OrderBy(x => x.index).Select(x => x.line);
        var lines = kept.Select(line => (IRenderable)new PreviewLine(line.Pieces, palette.Bg)).ToList();
        while (lines.Count < rows)
        {
            lines.Add(new PreviewLine([], palette.Bg));
        }

        return lines;
    }

    /// <summary>One row of the mock screen and its rank (0 is kept longest).</summary>
    private sealed record Line(int Rank, IReadOnlyList<Piece> Pieces);

    /// <summary>A run of text in one style.</summary>
    private readonly record struct Piece(string Text, Style Style);

    /// <summary>The whole mock screen, top to bottom, for <paramref name="inner"/> cells inside the margins.</summary>
    private static List<Line> Screen(ThemePalette p, int inner, string banner, string version)
    {
        var s = Theme.StylesOf(p);
        Piece P(ThemeStyleSlot slot, string text) => new(text, s(slot));
        Line L(int rank, params Piece[] pieces) => new(rank, pieces);
        Line Empty() => new(Blank, []);

        var bannerPieces = Gradient(banner, p.GradientStops);
        bannerPieces.Add(P(ThemeStyleSlot.DimText, "  v" + version));

        // The code block: every row one slab of the block's fill, its longest line plus SlabPad.
        Piece[][] code =
        [
            [P(ThemeStyleSlot.CodeComment, "// one stop per letter")],
            [
                P(ThemeStyleSlot.CodeKeyword, "static"), P(ThemeStyleSlot.MarkdownCodeBlock, " "), P(ThemeStyleSlot.CodeType, "Color"),
                P(ThemeStyleSlot.MarkdownCodeBlock, " "), P(ThemeStyleSlot.CodeFunction, "Sample"), P(ThemeStyleSlot.CodePunctuation, "("),
                P(ThemeStyleSlot.CodeType, "double"), P(ThemeStyleSlot.MarkdownCodeBlock, " t"), P(ThemeStyleSlot.CodePunctuation, ")"),
                P(ThemeStyleSlot.MarkdownCodeBlock, " "), P(ThemeStyleSlot.CodePunctuation, "=>"), P(ThemeStyleSlot.MarkdownCodeBlock, " stops"),
                P(ThemeStyleSlot.CodePunctuation, "[("), P(ThemeStyleSlot.CodeKeyword, "int"), P(ThemeStyleSlot.CodePunctuation, ")("),
                P(ThemeStyleSlot.MarkdownCodeBlock, "t "), P(ThemeStyleSlot.CodePunctuation, "*"), P(ThemeStyleSlot.MarkdownCodeBlock, " "),
                P(ThemeStyleSlot.CodeNumber, "13"), P(ThemeStyleSlot.CodePunctuation, ")];"),
            ],
            [
                P(ThemeStyleSlot.CodeKeyword, "string"), P(ThemeStyleSlot.MarkdownCodeBlock, " seal "), P(ThemeStyleSlot.CodePunctuation, "="),
                P(ThemeStyleSlot.MarkdownCodeBlock, " "), P(ThemeStyleSlot.CodeString, "\"inkwash\""), P(ThemeStyleSlot.CodePunctuation, ";"),
            ],
        ];
        int slab = code.Max(row => row.Sum(piece => TextCells.Width(piece.Text))) + SlabPad;
        Piece[] Slab(Piece[] row) => [.. row, P(ThemeStyleSlot.MarkdownCodeBlock, new string(' ', Math.Max(0, slab - row.Sum(piece => TextCells.Width(piece.Text)))))];

        // The menu row: the highlight across the card, as the pane draws the cursor's row.
        string menuName = MenuPane.Pointer + p.Name.PadRight(Math.Max(10, p.Name.Length + 1));
        string menuNote = p.Description.PadRight(Math.Max(0, inner - TextCells.Width(menuName)));

        return
        [
            L(0, [.. bannerPieces]),
            L(0, [.. Runs(Rule(inner), p.GradientStops)]),
            Empty(),
            L(1, P(ThemeStyleSlot.User, ThemeText.PreviewUser)),
            Empty(),
            L(3, P(ThemeStyleSlot.MarkdownHeading1, ThemeText.PreviewHeading)),
            L(2,
                P(ThemeStyleSlot.Assistant, ThemeText.PreviewProse1), P(ThemeStyleSlot.MarkdownBold, ThemeText.PreviewProseBold),
                P(ThemeStyleSlot.Assistant, ThemeText.PreviewProse2), P(ThemeStyleSlot.MarkdownCode, ThemeText.PreviewProseCode),
                P(ThemeStyleSlot.Assistant, ThemeText.PreviewProse3), P(ThemeStyleSlot.MarkdownItalic, ThemeText.PreviewProseItalic),
                P(ThemeStyleSlot.Assistant, ThemeText.PreviewProse4)),
            L(5, P(ThemeStyleSlot.MarkdownBullet, "  • "), P(ThemeStyleSlot.Assistant, ThemeText.PreviewBullet)),
            L(5, P(ThemeStyleSlot.MarkdownQuoteBar, "  ▌ "), P(ThemeStyleSlot.MarkdownQuote, ThemeText.PreviewQuote)),
            Empty(),
            L(6, P(ThemeStyleSlot.MarkdownCodeLabel, ThemeText.PreviewCodeLabel)),
            L(6, Slab(code[0])),
            L(4, Slab(code[1])),
            L(6, Slab(code[2])),
            Empty(),
            L(7, P(ThemeStyleSlot.Thinking, ThemeText.PreviewThinking)),
            Empty(),
            L(3, P(ThemeStyleSlot.SystemText, "  " + ThemeText.PreviewNotice(p.Name))),
            L(2, P(ThemeStyleSlot.GoodText, "  " + ThemeText.PreviewGood)),
            L(4, P(ThemeStyleSlot.WarnText, "  " + ThemeText.PreviewWarn)),
            L(2, P(ThemeStyleSlot.ErrorText, "  " + ThemeText.PreviewError)),
            Empty(),
            L(6, P(ThemeStyleSlot.PaneRule, new string('─', inner))),
            L(3, P(ThemeStyleSlot.MenuHighlight, menuName), P(ThemeStyleSlot.MenuHighlightDim, menuNote)),
            L(5,
                P(ThemeStyleSlot.User, "› /theme "), P(ThemeStyleSlot.SelectedText, p.Name),
                P(ThemeStyleSlot.Spinner, "  " + Theme.SpinnerFrames[0]), P(ThemeStyleSlot.PasteLabel, "  " + ThemeText.PreviewPaste)),
            L(6, P(ThemeStyleSlot.Hint, "  " + ThemeText.PreviewHint), P(ThemeStyleSlot.TrailerMark, "◆"), P(ThemeStyleSlot.Hint, ThemeText.PreviewTrailer)),
            L(8, P(ThemeStyleSlot.Placeholder, "  " + ThemeText.PreviewPlaceholder)),
        ];
    }

    /// <summary>The rule's five runs of <c>─</c>, as <see cref="Theme.Rule(int, Color[], char)"/> splits them.</summary>
    private static List<(string Text, double At)> Rule(int width)
    {
        const int segments = 5;
        int per = Math.Max(1, width / segments);
        var runs = new List<(string, double)>(segments);
        for (int s = 0; s < segments; s++)
        {
            int count = s == segments - 1 ? width - s * per : per;
            if (count > 0)
            {
                runs.Add((new string('─', count), s / (double)(segments - 1)));
            }
        }

        return runs;
    }

    /// <summary><paramref name="runs"/> each in the gradient's colour at its point.</summary>
    private static List<Piece> Runs(List<(string Text, double At)> runs, Color[] stops) =>
        runs.Select(run => new Piece(run.Text, new Style(foreground: Theme.SampleGradient(run.At, stops)))).ToList();

    /// <summary><paramref name="text"/> a character at a time across the gradient, as <see cref="Theme.GradientMarkup(string, Color[])"/> colours the banner.</summary>
    private static List<Piece> Gradient(string text, Color[] stops)
    {
        int n = Math.Max(text.Length, 1);
        return text.Select((ch, i) => new Piece(ch.ToString(), new Style(foreground: Theme.SampleGradient((double)i / Math.Max(1, n - 1), stops)))).ToList();
    }

    /// <summary>
    /// One row of the card: <see cref="Margin"/>, the pieces cut to what the width leaves (a cell at a time, never inside a wide
    /// character), then the palette's background to the edge. A piece without a background of its own takes the palette's.
    /// </summary>
    private sealed class PreviewLine(IReadOnlyList<Piece> pieces, Color bg) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            var fill = new Style(background: bg);
            var segments = new List<Segment>(pieces.Count + 3);
            int room = Math.Max(0, maxWidth - 2 * Margin);
            int used = 0;
            if (maxWidth >= Margin)
            {
                segments.Add(new Segment(new string(' ', Margin), fill));
            }

            foreach (var piece in pieces)
            {
                if (used >= room)
                {
                    break;
                }

                string text = Fit(piece.Text, room - used, out int cells);
                if (cells > 0)
                {
                    var style = piece.Style.Background == Color.Default ? new Style(piece.Style.Foreground, bg, piece.Style.Decoration) : piece.Style;
                    segments.Add(new Segment(text, style));
                    used += cells;
                }
            }

            int rest = Math.Max(0, maxWidth - Math.Min(maxWidth, Margin) - used);
            if (rest > 0)
            {
                segments.Add(new Segment(new string(' ', rest), fill));
            }

            return segments;
        }

        /// <summary>The longest head of <paramref name="text"/> within <paramref name="room"/> cells, and its width.</summary>
        private static string Fit(string text, int room, out int cells)
        {
            cells = 0;
            int i = 0;
            while (i < text.Length)
            {
                int width = TextCells.ElementWidth(text, i, out int length);
                if (cells + width > room)
                {
                    break;
                }

                cells += width;
                i += length;
            }

            return text[..i];
        }
    }
}
