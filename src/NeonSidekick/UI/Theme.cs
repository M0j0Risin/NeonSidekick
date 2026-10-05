using System.Globalization;
using System.Text;
using NeonSidekick.UI.Markdown;
using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// The visual identity: the palette in force (<see cref="ThemePalette"/>, <see cref="ThemeName.Default"/> at start),
/// a gradient title and styled panels shared by every screen. No code names a colour (the themes are
/// <c>assets/themes</c>' files since 2026-10-05, <see cref="ThemeLibrary"/>); every file composes markup
/// through the helpers here so the whole look can be tuned in one place.
/// <para>
/// Themes (2026-09-23, the user's ask): every colour and style is a property over the current
/// palette's style set, swapped whole by <see cref="Use"/> — one reference assignment, so a
/// line rendered on another thread mid-swap is at worst one line in two themes. A swap does not
/// re-colour what is already drawn (the scrollback keeps styled segments), which is why a theme
/// change starts over the way <c>/clear</c> does, with the welcome splash when it is on.
/// </para>
/// </summary>
public static class Theme
{
    private static volatile ThemeStyles s_current = new(ThemeLibrary.Default);

    /// <summary>The last set <see cref="StylesOf"/> built for a palette not in force.</summary>
    private static volatile ThemeStyles? s_other;

    /// <summary>The palette in force.</summary>
    public static ThemePalette Current => s_current.Palette;

    /// <summary>
    /// Makes <paramref name="palette"/> the palette in force; every style after this reads it. A palette with the same look
    /// as the one in force (<see cref="ThemePalette.Equals(ThemePalette?)"/>, a user theme read afresh) keeps the instance.
    /// </summary>
    public static void Use(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!s_current.Palette.Equals(palette))
        {
            s_current = new ThemeStyles(palette);
        }
    }

    // ── Palette ─────────────────────────────────────────────────────────────
    /// <summary>The main accent (synthwave's hot magenta).</summary>
    public static Color Primary => s_current.Palette.Primary;
    /// <summary>The cool counter-accent (synthwave's neon cyan).</summary>
    public static Color Secondary => s_current.Palette.Secondary;
    /// <summary>The third accent, mid-tone of the gradient (synthwave's violet).</summary>
    public static Color Tertiary => s_current.Palette.Tertiary;
    /// <summary>The receding / structural colour (synthwave's deep violet).</summary>
    public static Color Deep => s_current.Palette.Deep;
    /// <summary>The warm highlight (synthwave's sunset amber).</summary>
    public static Color Highlight => s_current.Palette.Highlight;
    /// <summary>Between the highlight and the primary (synthwave's orange).</summary>
    public static Color Warm => s_current.Palette.Warm;
    /// <summary>A soft accent (synthwave's sunset red-pink, the lower disc of the sun).</summary>
    public static Color Tint => s_current.Palette.Tint;

    /// <summary>Body text (readable on the dark bg).</summary>
    public static Color Ink => s_current.Palette.Ink;
    /// <summary>Secondary / dim text.</summary>
    public static Color Dim => s_current.Palette.Dim;
    /// <summary>A step darker than <see cref="Dim"/> — the ghost text on the empty input row, quieter than the hint row.</summary>
    public static Color Dimmer => s_current.Palette.Dimmer;

    /// <summary>The page background.</summary>
    public static Color Bg => s_current.Palette.Bg;
    /// <summary>Panel fill, a touch lifted from the page background.</summary>
    public static Color PanelBg => s_current.Palette.PanelBg;

    /// <summary>Success, enabled, connected.</summary>
    public static Color Good => s_current.Palette.Good;
    /// <summary>Failure, error.</summary>
    public static Color Bad => s_current.Palette.Bad;
    /// <summary>A warning (synthwave reuses the sunset gold).</summary>
    public static Color Warn => s_current.Palette.Warn;

    /// <summary>The title gradient's five stops (synthwave's sunset, cyan → violet → magenta → orange → amber).</summary>
    public static Color[] GradientStops => s_current.Palette.GradientStops;

    // ── Styles ──────────────────────────────────────────────────────────────
    /// <summary>
    /// The style in force for <paramref name="slot"/>: what the named property below hands out (2026-10-01, for the check
    /// that walks every slot of the shipped example themes in <c>assets/themes</c>).
    /// </summary>
    public static Style Of(ThemeStyleSlot slot) => s_current[slot];

    /// <summary>
    /// The styles <paramref name="palette"/> would hand out once in force, without putting it in force (2026-10-02, the user's
    /// ask: the theme pickers' preview beside the list, <see cref="ThemePreview"/>): the same derivation, aliases and
    /// <see cref="ThemePalette.Styles"/> changes as <see cref="Use"/>. The palette in force answers from its own set; any other
    /// from the last one built, so a pane drawn again on the same row builds nothing.
    /// </summary>
    public static Func<ThemeStyleSlot, Style> StylesOf(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var styles = s_current;
        if (!ReferenceEquals(styles.Palette, palette))
        {
            styles = s_other;
            if (styles is null || !ReferenceEquals(styles.Palette, palette))
            {
                styles = new ThemeStyles(palette);
                s_other = styles;
            }
        }

        return slot => styles[slot];
    }

    public static Style Body => s_current[ThemeStyleSlot.Body];
    public static Style DimText => s_current[ThemeStyleSlot.DimText];
    public static Style Accent => s_current[ThemeStyleSlot.Accent];
    public static Style AccentSecondary => s_current[ThemeStyleSlot.AccentSecondary];
    public static Style AccentTertiary => s_current[ThemeStyleSlot.AccentTertiary];
    public static Style Label => s_current[ThemeStyleSlot.Label];

    /// <summary>The user's own lines in the transcript.</summary>
    public static Style User => s_current[ThemeStyleSlot.User];
    /// <summary>The assistant's streamed reply text.</summary>
    public static Style Assistant => s_current[ThemeStyleSlot.Assistant];
    /// <summary>Notices, hints, status — anything neither party said.</summary>
    public static Style SystemText => s_current[ThemeStyleSlot.SystemText];
    /// <summary>An error line.</summary>
    public static Style ErrorText => s_current[ThemeStyleSlot.ErrorText];
    /// <summary>A success line (connected, enabled, passed).</summary>
    public static Style GoodText => s_current[ThemeStyleSlot.GoodText];
    /// <summary>A warning line.</summary>
    public static Style WarnText => s_current[ThemeStyleSlot.WarnText];
    /// <summary>A section heading over cyan row labels in an info pane (<c>/usage</c>'s Context / Last reply, <c>/sys</c>'s Persona / Clock (3) …): violet bold, so the heading and its rows read as two tiers — the user's call, 2026-09-16.</summary>
    public static Style SectionHeading => s_current[ThemeStyleSlot.SectionHeading];

    /// <summary>The border for panels.</summary>
    public static Style BorderStyle => s_current[ThemeStyleSlot.Border];
    /// <summary>Table header text.</summary>
    public static Style TableHeader => s_current[ThemeStyleSlot.TableHeader];

    /// <summary>Braille spinner frames, 80 ms cadence.</summary>
    public static readonly string[] SpinnerFrames = { "⠋", "⠙", "⠸", "⠴", "⠧", "⠇", "⠏" };

    /// <summary>The <c>Status</c> spinner while the model thinks or a server is probed.</summary>
    public static Style SpinnerStyle => s_current[ThemeStyleSlot.Spinner];
    /// <summary>The rule above the input row.</summary>
    public static Style PaneRule => s_current[ThemeStyleSlot.PaneRule];
    /// <summary>The hint row under the input row.</summary>
    public static Style Hint => s_current[ThemeStyleSlot.Hint];
    /// <summary>
    /// A toolbar tool switch that is off (2026-10-03, the user's pick after trying faint, a grey colour and the text
    /// presentation in Windows Terminal: only a background reads on a colour emoji): the hint's colour on the palette's panel
    /// fill, the slab a code block sits on, so every theme has its own.
    /// </summary>
    public static Style ToolbarOff => new(Hint.Foreground, MarkdownCodeBlock.Background);
    /// <summary>The mark after the trailer on the hint row (the reasoning glyph beside the model): violet, plain — the user's call, 2026-09-15.</summary>
    public static Style TrailerMark => s_current[ThemeStyleSlot.TrailerMark];
    /// <summary>A command form's typed words on <c>/help</c>'s Commands tabs (2026-10-05, the user's pick): the tertiary accent, <see cref="TrailerMark"/>'s.</summary>
    public static Style HelpForm => s_current[ThemeStyleSlot.HelpForm];
    /// <summary>A command form's placeholders and brackets (<c>&lt;name&gt;</c>, <c>[ ] | ...</c>), dimmed beside <see cref="HelpForm"/>.</summary>
    public static Style HelpSlot => s_current[ThemeStyleSlot.HelpSlot];
    /// <summary>The highlighted row of a selection menu.</summary>
    public static Style MenuHighlight => s_current[ThemeStyleSlot.MenuHighlight];
    /// <summary>The dim part of a highlighted row (the note beside a command or skill name on the input line's list, 2026-09-16).</summary>
    public static Style MenuHighlightDim => s_current[ThemeStyleSlot.MenuHighlightDim];
    /// <summary>A disabled menu row.</summary>
    public static Style MenuDisabled => s_current[ThemeStyleSlot.MenuDisabled];
    /// <summary>The selected stretch of the input row (a drag or Shift+arrows): the user's colour inverted.</summary>
    public static Style SelectedText => s_current[ThemeStyleSlot.SelectedText];
    /// <summary>A pasted block's placeholder on the input row (<c>[Pasted text #1 +49 lines]</c>): violet, so it reads as a thing and not as typed text.</summary>
    public static Style PasteLabel => s_current[ThemeStyleSlot.PasteLabel];
    /// <summary>The ghost text on the empty input row (<c>Type a message or /help for more info</c>): a step dimmer than the hint row (the user's call, 2026-09-14), never the user's colour.</summary>
    public static Style Placeholder => s_current[ThemeStyleSlot.Placeholder];

    // ── The styled transcript (UI/Markdown, `Transcript markdown`) ──────────
    /// <summary>Bold text in a reply (<c>**bold**</c>): the body colour, bold — emphasis, not a colour change.</summary>
    public static Style MarkdownBold => s_current[ThemeStyleSlot.MarkdownBold];
    /// <summary>Italic text in a reply (<c>*italic*</c>).</summary>
    public static Style MarkdownItalic => s_current[ThemeStyleSlot.MarkdownItalic];
    /// <summary>Inline code (<c>`code`</c>): cyan, so a name stands out of the prose.</summary>
    public static Style MarkdownCode => s_current[ThemeStyleSlot.MarkdownCode];
    /// <summary>The lines of a fenced code block: body text on the lifted panel fill.</summary>
    public static Style MarkdownCodeBlock => s_current[ThemeStyleSlot.MarkdownCodeBlock];
    /// <summary>The language label above a fenced code block.</summary>
    public static Style MarkdownCodeLabel => s_current[ThemeStyleSlot.MarkdownCodeLabel];
    /// <summary>The model's thinking (2026-09-26): dim italic on the code block's lifted fill — a panel like code, told apart by its ink.</summary>
    public static Style ThinkingText => s_current[ThemeStyleSlot.Thinking];
    /// <summary>A first-level heading.</summary>
    public static Style MarkdownHeading1 => s_current[ThemeStyleSlot.MarkdownHeading1];
    /// <summary>Every other heading level.</summary>
    public static Style MarkdownHeading => s_current[ThemeStyleSlot.MarkdownHeading];

    /// <summary>A third-level heading and below (2026-10-04): the body ink, bold.</summary>
    public static Style MarkdownHeading3 => s_current[ThemeStyleSlot.MarkdownHeading3];
    /// <summary>The bullet or number ahead of a list item.</summary>
    public static Style MarkdownBullet => s_current[ThemeStyleSlot.MarkdownBullet];
    /// <summary>The gutter bar of a blockquote.</summary>
    public static Style MarkdownQuoteBar => s_current[ThemeStyleSlot.MarkdownQuoteBar];
    /// <summary>The text of a blockquote.</summary>
    public static Style MarkdownQuote => s_current[ThemeStyleSlot.MarkdownQuote];
    /// <summary>The URL shown after a link's text.</summary>
    public static Style MarkdownLinkUrl => s_current[ThemeStyleSlot.MarkdownLinkUrl];
    /// <summary>A thematic break (<c>---</c>) in a reply.</summary>
    public static Style MarkdownRule => s_current[ThemeStyleSlot.MarkdownRule];

    // ── Code highlighting (UI/Markdown/CodeLexer, fenced blocks with a known language) ──
    // Every style keeps the block's lifted panel fill, so a highlighted block reads as one slab
    // like a plain one; the accents carry the classes (2026-09-22): primary keywords, secondary
    // types and keys, highlight strings, warm numbers, tertiary calls, dim italic comments.
    /// <summary>A keyword (<c>if</c>, <c>class</c>, <c>SELECT</c>) or a directive (<c>#include</c>).</summary>
    public static Style CodeKeyword => s_current[ThemeStyleSlot.CodeKeyword];
    /// <summary>A type or builtin name, a Rust lifetime, a shell builtin.</summary>
    public static Style CodeType => s_current[ThemeStyleSlot.CodeType];
    /// <summary>A string literal.</summary>
    public static Style CodeString => s_current[ThemeStyleSlot.CodeString];
    /// <summary>A numeric literal, a CSS colour.</summary>
    public static Style CodeNumber => s_current[ThemeStyleSlot.CodeNumber];
    /// <summary>A comment: dim and italic, so it recedes behind the code.</summary>
    public static Style CodeComment => s_current[ThemeStyleSlot.CodeComment];
    /// <summary>Operators and brackets: dim, so the words carry the line.</summary>
    public static Style CodePunctuation => s_current[ThemeStyleSlot.CodePunctuation];
    /// <summary>A call (<c>name(</c>), a macro, a PowerShell cmdlet.</summary>
    public static Style CodeFunction => s_current[ThemeStyleSlot.CodeFunction];
    /// <summary>A <c>$variable</c>.</summary>
    public static Style CodeVariable => s_current[ThemeStyleSlot.CodeVariable];
    /// <summary>A key, an attribute, a decorator, a command-line flag.</summary>
    public static Style CodeAttribute => s_current[ThemeStyleSlot.CodeAttribute];
    /// <summary>A markup element name, a CSS selector.</summary>
    public static Style CodeTag => s_current[ThemeStyleSlot.CodeTag];
    /// <summary>An INI/TOML section, a diff's file header or hunk.</summary>
    public static Style CodeHeading => s_current[ThemeStyleSlot.CodeHeading];
    /// <summary>A diff's added line.</summary>
    public static Style CodeInserted => s_current[ThemeStyleSlot.CodeInserted];
    /// <summary>A diff's removed line.</summary>
    public static Style CodeDeleted => s_current[ThemeStyleSlot.CodeDeleted];
    /// <summary>
    /// An added line under a file edit (2026-10-03, the user's ask: diffs in the transcript, Claude Code's look): a slab of the
    /// palette's own good colour a quarter of the way up from the page, so every theme has its own green; the line's text keeps
    /// its code colours over it (<see cref="DiffView"/> takes only the background).
    /// </summary>
    public static Style DiffAdded => s_current[ThemeStyleSlot.DiffAdded];
    /// <summary>A removed line under a file edit: <see cref="DiffAdded"/>'s slab in the palette's bad colour.</summary>
    public static Style DiffRemoved => s_current[ThemeStyleSlot.DiffRemoved];

    /// <summary>How far a diff slab goes from the page colour toward good or bad: enough to read, faint enough for code over it.</summary>
    private const double DiffSlabShare = 0.28;

    /// <summary>The style of a <see cref="CodeTokenKind"/>; plain text is <see cref="MarkdownCodeBlock"/>.</summary>
    public static Style CodeStyle(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Keyword => CodeKeyword,
        CodeTokenKind.Type => CodeType,
        CodeTokenKind.String => CodeString,
        CodeTokenKind.Number => CodeNumber,
        CodeTokenKind.Comment => CodeComment,
        CodeTokenKind.Punctuation => CodePunctuation,
        CodeTokenKind.Function => CodeFunction,
        CodeTokenKind.Variable => CodeVariable,
        CodeTokenKind.Attribute => CodeAttribute,
        CodeTokenKind.Tag => CodeTag,
        CodeTokenKind.Heading => CodeHeading,
        CodeTokenKind.Inserted => CodeInserted,
        CodeTokenKind.Deleted => CodeDeleted,
        _ => MarkdownCodeBlock,
    };

    /// <summary>A table in a reply: the pane's rule colour, fitted to its content — never expanded to the window.</summary>
    public static Table MarkdownTable()
    {
        return new Table
        {
            Border = TableBorder.Rounded,
            BorderStyle = PaneRule,
            Expand = false,
        };
    }

    /// <summary>Menus show at most this many rows before paging.</summary>
    public const int MenuPageSize = 15;

    // ── Markup helpers ──────────────────────────────────────────────────────
    /// <summary>Renders a string as a per-character gradient. Used for the title.</summary>
    public static string GradientMarkup(string text) => GradientMarkup(text, GradientStops);

    /// <summary>As <see cref="GradientMarkup(string)"/> over <paramref name="stops"/>, any palette's (2026-10-02, the theme preview).</summary>
    public static string GradientMarkup(string text, Color[] stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        var sb = new StringBuilder();
        int n = Math.Max(text.Length, 1);
        int idx = 0;
        foreach (var ch in text)
        {
            Color c = SampleGradient((double)idx / Math.Max(1, n - 1), stops);
            sb.Append('[').Append(ToHex(c)).Append(']');
            sb.Append(Markup.Escape(ch.ToString()));
            sb.Append("[/]");
            idx++;
        }

        return sb.ToString();
    }

    /// <summary>A short accent label (the primary, bold). The text is escaped.</summary>
    public static string AccentMarkup(string text) =>
        string.Concat("[", ToHex(Primary), " bold]", Markup.Escape(text), "[/]");

    /// <summary>Dim secondary text. The text is escaped.</summary>
    public static string DimMarkup(string text) =>
        string.Concat("[", ToHex(Dim), "]", Markup.Escape(text), "[/]");

    /// <summary>
    /// Text in a theme slot's style (2026-10-04: <see cref="SystemText"/> and <see cref="ErrorText"/> had been defined and
    /// never used, so a user theme's override of either changed nothing). A style that is a colour alone writes exactly
    /// <see cref="ColorMarkup"/>'s form; anything more (bold, a background) its full markup. The text is escaped.
    /// </summary>
    public static string StyleMarkup(Style style, string text) =>
        style.Decoration == Decoration.None && style.Background == Color.Default
            ? ColorMarkup(style.Foreground, text)
            : string.Concat("[", style.ToMarkup(), "]", Markup.Escape(text), "[/]");

    /// <summary>Text in an arbitrary palette colour. The text is escaped.</summary>
    public static string ColorMarkup(Color color, string text) =>
        string.Concat("[", ToHex(color), "]", Markup.Escape(text), "[/]");

    /// <summary>
    /// A horizontal gradient rule of <paramref name="width"/> cells, split into five
    /// segments. Used as the divider under the header.
    /// </summary>
    public static string Rule(int width, char glyph = '─') => Rule(width, GradientStops, glyph);

    /// <summary>As <see cref="Rule(int, char)"/> over <paramref name="stops"/>, any palette's (2026-10-02, the theme preview).</summary>
    public static string Rule(int width, Color[] stops, char glyph = '─')
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (width <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        const int segments = 5;
        int per = Math.Max(1, width / segments);
        for (int s = 0; s < segments; s++)
        {
            int count = (s == segments - 1) ? width - s * per : per;
            if (count <= 0)
            {
                continue;
            }

            Color c = SampleGradient(s / (double)(segments - 1), stops);
            sb.Append('[').Append(ToHex(c)).Append(']');
            sb.Append(new string(glyph, count));
            sb.Append("[/]");
        }

        return sb.ToString();
    }

    // ── Widget factories ────────────────────────────────────────────────────
    /// <summary>
    /// Applies the theme's look to a selection menu: highlight, disabled style, page size and
    /// wrap-around. Spectre's default highlight is plain blue. Search stays off, so J/K move and
    /// a stray key is not treated as a filter.
    /// </summary>
    public static SelectionPrompt<T> Selection<T>(SelectionPrompt<T> prompt) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(prompt);
        prompt.HighlightStyle = MenuHighlight;
        prompt.DisabledStyle = MenuDisabled;
        prompt.PageSize = MenuPageSize;
        prompt.WrapAround = true;
        return prompt;
    }

    /// <summary>A table styled with the primary border and the secondary header.</summary>
    public static Table SunsetTable(string? title = null, Style? borderStyle = null)
    {
        var t = new Table
        {
            Border = TableBorder.Rounded,
            BorderStyle = borderStyle ?? BorderStyle,
            Expand = true,
        };
        if (!string.IsNullOrEmpty(title))
        {
            t.Title = new TableTitle(title, AccentSecondary);
        }

        return t;
    }

    /// <summary>A <see cref="TableColumn"/> with the header style.</summary>
    public static TableColumn HeaderColumn(string name, Justify? alignment = null)
    {
        var c = new TableColumn(new Markup(Markup.Escape(name), TableHeader));
        if (alignment is { } a)
        {
            c.Alignment = a;
        }

        return c;
    }

    // ── Internals ───────────────────────────────────────────────────────────
    /// <summary>Linearly samples a gradient stop array at t ∈ [0,1].</summary>
    internal static Color SampleGradient(double t, Color[] stops)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        double scaled = t * (stops.Length - 1);
        int idx = (int)Math.Floor(scaled);
        if (idx >= stops.Length - 1)
        {
            return stops[^1];
        }

        double frac = scaled - idx;
        return Lerp(stops[idx], stops[idx + 1], frac);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        int r = (int)Math.Round(a.R + (b.R - a.R) * t);
        int g = (int)Math.Round(a.G + (b.G - a.G) * t);
        int bl = (int)Math.Round(a.B + (b.B - a.B) * t);
        return new Color((byte)r, (byte)g, (byte)bl);
    }

    /// <summary>Hex markup colour (e.g. <c>#FF2E97</c>) for a Spectre <see cref="Color"/>.</summary>
    public static string ToHex(Color c) => string.Concat("#", ToHex2(c.R), ToHex2(c.G), ToHex2(c.B));

    private static string ToHex2(byte v) => v.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>
    /// The styles of one palette, built once when it is put in force, one per <see cref="ThemeStyleSlot"/>, with the
    /// compositions the synthwave look always had. Since 2026-10-01 (the user's themes) each slot then takes the palette's
    /// <see cref="ThemePalette.Styles"/> change, and an alias slot (<see cref="User"/>, <see cref="Hint"/>,
    /// <see cref="MarkdownHeading"/> …) starts from its source's final style (<see cref="ThemeKeys.AliasOf"/>), so with no
    /// change it is the very same style as before.
    /// </summary>
    private sealed class ThemeStyles
    {
        private readonly Style[] _styles;

        public ThemeStyles(ThemePalette p)
        {
            Palette = p;
            _styles = new Style[ThemeKeys.Styles.Count];
            for (int i = 0; i < _styles.Length; i++)
            {
                var slot = (ThemeStyleSlot)i;
                Style style = ThemeKeys.AliasOf(slot) is { } source ? _styles[(int)source] : Derive(slot, p);
                if (p.Styles is { } changes && changes.TryGetValue(slot, out var change))
                {
                    style = change.ApplyTo(style);
                }

                _styles[i] = style;
            }
        }

        public ThemePalette Palette { get; }

        public Style this[ThemeStyleSlot slot] => _styles[(int)slot];

        /// <summary>The style the palette gives a slot that is no alias.</summary>
        private static Style Derive(ThemeStyleSlot slot, ThemePalette p) => slot switch
        {
            ThemeStyleSlot.Body => new(foreground: p.Ink),
            ThemeStyleSlot.DimText => new(foreground: p.Dim),
            ThemeStyleSlot.Accent => new(foreground: p.Primary, decoration: Decoration.Bold),
            ThemeStyleSlot.AccentSecondary => new(foreground: p.Secondary, decoration: Decoration.Bold),
            ThemeStyleSlot.AccentTertiary => new(foreground: p.Tertiary, decoration: Decoration.Bold),
            ThemeStyleSlot.Label => new(foreground: p.Dim, decoration: Decoration.Bold),
            ThemeStyleSlot.ErrorText => new(foreground: p.Bad, decoration: Decoration.Bold),
            ThemeStyleSlot.GoodText => new(foreground: p.Good, decoration: Decoration.Bold),
            ThemeStyleSlot.WarnText => new(foreground: p.Warn, decoration: Decoration.Bold),
            ThemeStyleSlot.Border => new(foreground: p.Primary),
            ThemeStyleSlot.TableHeader => new(foreground: p.Secondary, decoration: Decoration.Bold),
            ThemeStyleSlot.PaneRule => new(foreground: p.Deep),
            ThemeStyleSlot.TrailerMark => new(foreground: p.Tertiary),
            ThemeStyleSlot.MenuHighlight => new(foreground: p.Ink, background: p.PanelBg),
            ThemeStyleSlot.MenuHighlightDim => new(foreground: p.Dim, background: p.PanelBg),
            ThemeStyleSlot.MenuDisabled => new(foreground: p.Dim, background: p.Bg),
            ThemeStyleSlot.SelectedText => new(foreground: p.Bg, background: p.Secondary),
            ThemeStyleSlot.PasteLabel => new(foreground: p.Tertiary, decoration: Decoration.Bold),
            ThemeStyleSlot.Placeholder => new(foreground: p.Dimmer),
            ThemeStyleSlot.MarkdownBold => new(foreground: p.Ink, decoration: Decoration.Bold),
            ThemeStyleSlot.MarkdownItalic => new(foreground: p.Ink, decoration: Decoration.Italic),
            ThemeStyleSlot.MarkdownCode => new(foreground: p.Secondary),
            ThemeStyleSlot.MarkdownCodeBlock => new(foreground: p.Ink, background: p.PanelBg),
            ThemeStyleSlot.Thinking => new(foreground: p.Dim, background: p.PanelBg, decoration: Decoration.Italic),
            ThemeStyleSlot.CodeKeyword => new(foreground: p.Primary, background: p.PanelBg),
            ThemeStyleSlot.CodeType => new(foreground: p.Secondary, background: p.PanelBg),
            ThemeStyleSlot.CodeString => new(foreground: p.Highlight, background: p.PanelBg),
            ThemeStyleSlot.CodeNumber => new(foreground: p.Warm, background: p.PanelBg),
            ThemeStyleSlot.CodeComment => new(foreground: p.Dim, background: p.PanelBg, decoration: Decoration.Italic),
            ThemeStyleSlot.CodePunctuation => new(foreground: p.Dim, background: p.PanelBg),
            ThemeStyleSlot.CodeFunction => new(foreground: p.Tertiary, background: p.PanelBg),
            ThemeStyleSlot.CodeVariable => new(foreground: p.Tint, background: p.PanelBg),
            ThemeStyleSlot.CodeHeading => new(foreground: p.Tertiary, background: p.PanelBg, decoration: Decoration.Bold),
            ThemeStyleSlot.CodeInserted => new(foreground: p.Good, background: p.PanelBg),
            ThemeStyleSlot.CodeDeleted => new(foreground: p.Bad, background: p.PanelBg),
            ThemeStyleSlot.DiffAdded => new(foreground: p.Ink, background: Lerp(p.Bg, p.Good, DiffSlabShare)),
            ThemeStyleSlot.DiffRemoved => new(foreground: p.Ink, background: Lerp(p.Bg, p.Bad, DiffSlabShare)),
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "An alias slot has no style of its own."),
        };
    }
}
