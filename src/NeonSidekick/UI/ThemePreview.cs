using System.Globalization;
using NeonSidekick.Files;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The theme pickers' preview (2026-10-02, the user's ask: "show a preview of the theme in an area to the right of the list,
/// like the Theme Atlas", without the screen starting over at every row): a small mock screen in one palette's own styles
/// (<see cref="Theme.StylesOf"/>, never <see cref="Theme.Use"/>) — the banner and its rule in the gradient, a user line, a
/// reply with bold, code, italic, a bullet and a quote, a highlighted code block, a file edit's diff (2026-10-03, the user's ask:
/// its note, summary and a removed and an added row on their slabs, as <see cref="DiffView"/> draws them; past <c>Diff collapse count</c>
/// its summary is the fold's unfolded row, 2026-10-04, the user's ask), the thinking slab, the notice, good, warning
/// and error lines, the pane rule, a highlighted menu row with a setting's value on it, the input row with a selection, the
/// spinner and a paste label, the hint row and the ghost text. Every cell is on the palette's <see cref="ThemePalette.Bg"/>, the way the Atlas paints each
/// screen: the card reads as the theme's own window, apart from the list beside it. With <c>Themed background</c> off
/// (2026-10-03, the user's ask) the card is on the terminal's own background instead, as the screen will be; the fills a style
/// carries itself (the code block, the thinking slab, the highlighted row, the selection) stay.
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
    /// banner reads <paramref name="banner"/> and <c>v</c><paramref name="version"/>. None for no rows. On the palette's
    /// <see cref="ThemePalette.Bg"/> under <paramref name="themedBackground"/>, else on the terminal's default background. A
    /// <paramref name="diffCollapseCount"/> the sample diff's rows pass (2026-10-04, <c>Diff collapse count</c>; 0 never) heads the
    /// diff with its fold's row, unfolded so the slabs still show: <c>▾ Added 1 line, removed 1 line · 2 rows</c> for the elbow's.
    /// </summary>
    public static IReadOnlyList<IRenderable> Lines(ThemePalette palette, int width, int rows, string banner, string version, bool themedBackground = true, int diffCollapseCount = 0)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(banner);
        ArgumentNullException.ThrowIfNull(version);
        if (rows <= 0)
        {
            return [];
        }

        var screen = Screen(palette, Math.Max(1, width - 2 * Margin), banner, version, diffCollapseCount);
        IEnumerable<Line> kept = screen.Count <= rows
            ? screen
            : screen.Select((line, index) => (line, index)).OrderBy(x => x.line.Rank).ThenBy(x => x.index).Take(rows).OrderBy(x => x.index).Select(x => x.line);
        var bg = themedBackground ? palette.Bg : Color.Default;
        var lines = kept.Select(line => (IRenderable)new PreviewLine(line.Pieces, bg)).ToList();
        while (lines.Count < rows)
        {
            lines.Add(new PreviewLine([], bg));
        }

        return lines;
    }

    /// <summary>
    /// The whole mock screen's rows (2026-11-24, the <c>/theme edit</c> pane's preview fills the pane), read from <see cref="Screen"/> so it follows
    /// any row added: 32 with the fold off. <paramref name="diffCollapseCount"/> is <c>Diff collapse count</c>, which adds the sample diff's fold row
    /// when the diff is longer than it. Each line is one row whatever the width, the palette, the banner or the version.
    /// </summary>
    public static int ScreenRows(int diffCollapseCount) => Screen(ThemeLibrary.Default, 40, "neon", "0", diffCollapseCount).Count;

    /// <summary>One row of the mock screen and its rank (0 is kept longest).</summary>
    private sealed record Line(int Rank, IReadOnlyList<Piece> Pieces);

    /// <summary>A run of text in one style.</summary>
    private readonly record struct Piece(string Text, Style Style);

    /// <summary>The whole mock screen, top to bottom, for <paramref name="inner"/> cells inside the margins.</summary>
    private static List<Line> Screen(ThemePalette p, int inner, string banner, string version, int diffCollapseCount)
    {
        var s = Theme.StylesOf(p);
        Piece P(ThemeStyleSlot slot, string text) => new(text, s(slot));
        Line L(int rank, params Piece[] pieces) => new(rank, pieces);
        Line Empty() => new(Blank, []);

        var bannerPieces = Gradient(banner, p.GradientStops);
        bannerPieces.Add(P(ThemeStyleSlot.DimText, "  v" + version));

        // The code block: every row one slab of the block's fill, its longest line plus SlabPad. The three rows lexed as one block, as a
        // reply's code is (2026-11-20: CodeLexer.LexLines through Theme.CodeSlotOf, the memory-remember review's #10, #30, #31; hand-assigned
        // pieces until then), so the preview wears what a reply shows. The language is the label's (#67): a label no lexer reads is a bug.
        var language = CodeLanguages.Find(ThemeText.PreviewCodeLabel)
            ?? throw new System.Diagnostics.UnreachableException("The preview's code label names no lexer: " + ThemeText.PreviewCodeLabel);
        Piece[] Lexed(IReadOnlyList<(string Text, CodeTokenKind Kind)> runs) => [.. runs.Select(run => P(Theme.CodeSlotOf(run.Kind), run.Text))];
        Piece[][] code = [.. CodeLexer.LexLines([ThemeText.PreviewCodeComment, ThemeText.PreviewCodeLine, ThemeText.PreviewCodeLastName + ThemeText.PreviewDiffNew], language).Select(Lexed)];
        int slab = code.Max(row => row.Sum(piece => TextCells.Width(piece.Text))) + SlabPad;
        Piece[] Slab(Piece[] row) => [.. row, P(ThemeStyleSlot.MarkdownCodeBlock, new string(' ', Math.Max(0, slab - row.Sum(piece => TextCells.Width(piece.Text)))))];

        // The code block's last line as an edit changed it (2026-10-03, the user's ask), drawn as DiffView draws a row: the
        // number and the sign on the slab, the code's own colours on it, the slab to the card's edge.
        Piece[] Last(string value) => Lexed(CodeLexer.LexLines([ThemeText.PreviewCodeLastName + value], language)[0]);
        Piece[] DiffRow(char sign, ThemeStyleSlot slot, string value)
        {
            var on = s(slot);
            string lead = ThemeText.PreviewDiffLine.ToString(CultureInfo.InvariantCulture) + " " + sign + " ";
            var row = new List<Piece> { new(DiffView.Indent, Style.Plain), new(lead, on) };
            row.AddRange(Last(value).Select(piece => new Piece(piece.Text, new Style(piece.Style.Foreground, on.Background, piece.Style.Decoration))));
            int used = row.Sum(piece => TextCells.Width(piece.Text));
            row.Add(new Piece(new string(' ', Math.Max(0, inner - used)), new Style(background: on.Background)));
            return [.. row];
        }

        string LastText(string value) => ThemeText.PreviewCodeLastName + value + "\n";
        var diff = FileDiff.Of(ThemeText.PreviewDiffFile, LastText(ThemeText.PreviewDiffOld), LastText(ThemeText.PreviewDiffNew))!;

        // The diff's summary row as the screen draws it: its fold's, unfolded, when the count folds it (DiffView.Fold's rule).
        int diffRows = DiffView.RowsOf(diff, null).Count(row => row is not null);
        string diffSummary = diffCollapseCount > 0 && diffRows > diffCollapseCount
            ? DiffFoldText.Summary(diff, diffRows, expanded: true)
            : DiffView.Indent + DiffView.Elbow + FileText.DiffSummary(diff);

        // The menu row: the highlight across the card, as the pane draws the cursor's row, with a value on it as a changed
        // setting's reads on /settings (2026-10-16, the contrast review: MenuPane.RowMarkup keeps a row's own colours over the bar,
        // so a bar that hides them shows here, a user theme's too). The value before the description, so a narrow card cuts the
        // description first.
        string menuName = MenuPane.Pointer + p.Name.PadRight(Math.Max(10, p.Name.Length + 2));
        var menuValue = new Piece(ThemeText.PreviewMenuValue, new Style(p.Secondary, s(ThemeStyleSlot.MenuHighlight).Background));
        string menuNote = ("  " + p.Description).PadRight(Math.Max(0, inner - TextCells.Width(menuName) - TextCells.Width(menuValue.Text)));

        return
        [
            L(0, [.. bannerPieces]),
            L(0, [.. RulePieces(inner, p.GradientStops)]),
            Empty(),
            L(1, P(ThemeStyleSlot.User, ThemeText.PreviewUser)),
            Empty(),
            L(3, P(ThemeStyleSlot.MarkdownHeading1, ThemeText.PreviewHeading)),
            L(2,
                P(ThemeStyleSlot.Assistant, ThemeText.PreviewProse1), P(ThemeStyleSlot.MarkdownBold, ThemeText.PreviewProseBold),
                P(ThemeStyleSlot.Assistant, ThemeText.PreviewProse2), P(ThemeStyleSlot.MarkdownCode, ThemeText.PreviewProseCode),
                P(ThemeStyleSlot.Assistant, ThemeText.PreviewProse3), P(ThemeStyleSlot.MarkdownItalic, ThemeText.PreviewProseItalic),
                P(ThemeStyleSlot.Assistant, ThemeText.PreviewProse4)),
            // The bullet gives way before the code (2026-11-20, the memory-remember review's #29, #41): tertiary shows on the quote bar too.
            L(6, P(ThemeStyleSlot.MarkdownBullet, "  • "), P(ThemeStyleSlot.Assistant, ThemeText.PreviewBullet)),
            L(5, P(ThemeStyleSlot.MarkdownQuoteBar, "  ▌ "), P(ThemeStyleSlot.MarkdownQuote, ThemeText.PreviewQuote)),
            // A reply's rule (2026-11-20, a blank row until then): deep, which the pane rule hides under Gradient rules. Rank 4 with the
            // placeholder, so a card shorter than the editor's 18 rows drops its role's one row last.
            L(4, P(ThemeStyleSlot.MarkdownRule, new string('─', inner))),
            // The code label and its three rows at the role rows' rank (2026-11-20, #29, #41): an 18-row card shows the whole block.
            L(5, P(ThemeStyleSlot.MarkdownCodeLabel, ThemeText.PreviewCodeLabel)),
            L(5, Slab(code[0])),
            L(4, Slab(code[1])),
            L(5, Slab(code[2])),
            Empty(),
            L(7, P(ThemeStyleSlot.DimText, TranscriptRenderer.ToolGlyph + ThemeText.PreviewDiffNote)),
            L(6, P(ThemeStyleSlot.DimText, diffSummary)),
            // The diff's slabs give way before the code (2026-11-20, #29, #41): their roles are blends, shown whole in a 22-row card.
            L(6, DiffRow('-', ThemeStyleSlot.DiffRemoved, ThemeText.PreviewDiffOld)),
            L(6, DiffRow('+', ThemeStyleSlot.DiffAdded, ThemeText.PreviewDiffNew)),
            Empty(),
            L(8, P(ThemeStyleSlot.Thinking, ThemeText.PreviewThinking)),   // 7 until 2026-11-20: dim and panelBg show elsewhere
            Empty(),
            L(3, P(ThemeStyleSlot.SystemText, "  " + ThemeText.PreviewNotice(p.Name))),
            L(2, P(ThemeStyleSlot.GoodText, "  " + ThemeText.PreviewGood)),
            L(4, P(ThemeStyleSlot.WarnText, "  " + ThemeText.PreviewWarn)),
            L(2, P(ThemeStyleSlot.ErrorText, "  " + ThemeText.PreviewError)),
            Empty(),
            Theme.RuleGradientOn && Theme.QuietStops(p) is { } quiet ? L(6, [.. RulePieces(inner, quiet)]) : L(6, P(ThemeStyleSlot.PaneRule, new string('─', inner))),
            L(3, P(ThemeStyleSlot.MenuHighlight, menuName), menuValue, P(ThemeStyleSlot.MenuHighlightDim, menuNote)),
            L(5,
                P(ThemeStyleSlot.User, "› /theme "), P(ThemeStyleSlot.SelectedText, p.Name),
                P(ThemeStyleSlot.Spinner, "  " + Theme.SpinnerFrames[0]), P(ThemeStyleSlot.PasteLabel, "  " + ThemeText.PreviewPaste)),
            L(6, P(ThemeStyleSlot.Hint, "  " + ThemeText.PreviewHint), P(ThemeStyleSlot.TrailerMark, "◆"), P(ThemeStyleSlot.Hint, ThemeText.PreviewTrailer)),
            L(4, P(ThemeStyleSlot.Placeholder, "  " + ThemeText.PreviewPlaceholder)),   // 8 until 2026-11-20 (5 that day, then 4, #41): dimmer's one row, kept in a short card
        ];
    }

    /// <summary>A rule of <paramref name="width"/> cells in <paramref name="stops"/>' colors, as <see cref="Theme.RuleRuns"/> splits it (2026-11-01: one splitter for the screen and the preview).</summary>
    private static List<Piece> RulePieces(int width, Color[] stops) =>
        Theme.RuleRuns(width, stops).Select(run => new Piece(run.Text, new Style(foreground: run.Color))).ToList();

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
