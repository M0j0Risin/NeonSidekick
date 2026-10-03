using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// The colour roles of a <see cref="ThemePalette"/> (2026-10-01, the user's themes): what a theme file's <c>colors</c>
/// section names and what a style's <c>fg</c>/<c>bg</c> may name instead of a hex. <see cref="ThemeKeys"/> holds the
/// words, which are contract ids: a user's file depends on them.
/// </summary>
public enum ThemeColorSlot
{
    Primary,
    Secondary,
    Tertiary,
    Deep,
    Highlight,
    Warm,
    Tint,
    Ink,
    Dim,
    Dimmer,
    Bg,
    PanelBg,
    Good,
    Bad,
    Warn,
}

/// <summary>
/// Every style <see cref="Theme"/> hands out, one slot each (2026-10-01, the user's ask: a theme file may restyle any of
/// them, not only recolour the roles). The aliases (<see cref="User"/> is <see cref="AccentSecondary"/>'s style,
/// <see cref="Hint"/> is <see cref="DimText"/>'s …) are slots of their own so a file can part them, and each comes after
/// its source here: an alias takes its source's <em>final</em> style, so a file restyling <see cref="AccentSecondary"/>
/// restyles the user's lines, the spinner and the headings with it unless it restyles those too
/// (<see cref="ThemeKeys.AliasOf"/>). Order matters only for that; the words are in <see cref="ThemeKeys"/>.
/// </summary>
public enum ThemeStyleSlot
{
    Body,
    DimText,
    Accent,
    AccentSecondary,
    AccentTertiary,
    Label,
    User,
    Assistant,
    SystemText,
    ErrorText,
    GoodText,
    WarnText,
    SectionHeading,
    Border,
    TableHeader,
    Spinner,
    PaneRule,
    Hint,
    TrailerMark,
    MenuHighlight,
    MenuHighlightDim,
    MenuDisabled,
    SelectedText,
    PasteLabel,
    Placeholder,
    MarkdownBold,
    MarkdownItalic,
    MarkdownCode,
    MarkdownCodeBlock,
    MarkdownCodeLabel,
    Thinking,
    MarkdownHeading1,
    MarkdownHeading,
    MarkdownBullet,
    MarkdownQuoteBar,
    MarkdownQuote,
    MarkdownLinkUrl,
    MarkdownRule,
    CodeKeyword,
    CodeType,
    CodeString,
    CodeNumber,
    CodeComment,
    CodePunctuation,
    CodeFunction,
    CodeVariable,
    CodeAttribute,
    CodeTag,
    CodeHeading,
    CodeInserted,
    CodeDeleted,
    DiffAdded,
    DiffRemoved,
}

/// <summary>
/// One style's change in a theme file (2026-10-01): a foreground and a background that replace the derived ones when
/// given, and the decorations it turns on (<see cref="Set"/>) and off (<see cref="Clear"/>); what it leaves out stays as
/// the theme derives it. Colours are resolved when the file is read, so a role name means the colour of the theme that
/// names it.
/// </summary>
public sealed record StyleOverride(Color? Foreground = null, Color? Background = null, Decoration Set = Decoration.None, Decoration Clear = Decoration.None)
{
    /// <summary><paramref name="style"/> with this change on top.</summary>
    public Style ApplyTo(Style style) =>
        new(Foreground ?? style.Foreground, Background ?? style.Background, (style.Decoration | Set) & ~Clear);

    /// <summary>This change over <paramref name="under"/> (a base theme's change to the same slot): what this one says wins, the rest is the base's.</summary>
    public StyleOverride Over(StyleOverride? under) => under is null
        ? this
        : new StyleOverride(
            Foreground ?? under.Foreground,
            Background ?? under.Background,
            (under.Set & ~Clear) | Set,
            (under.Clear & ~Set) | Clear);
}

/// <summary>
/// The words a theme file uses for <see cref="ThemeColorSlot"/>s and <see cref="ThemeStyleSlot"/>s (2026-10-01): camelCase,
/// written out rather than taken from the enum names so a rename in code never breaks a user's file. Pinned by
/// <c>ThemeFileTests</c>; matched without regard to case.
/// </summary>
public static class ThemeKeys
{
    /// <summary>The colour words, in <see cref="ThemeColorSlot"/> order.</summary>
    public static readonly IReadOnlyList<string> Colors =
    [
        "primary", "secondary", "tertiary", "deep", "highlight", "warm", "tint", "ink", "dim", "dimmer", "bg", "panelBg", "good", "bad", "warn",
    ];

    /// <summary>The style words, in <see cref="ThemeStyleSlot"/> order.</summary>
    public static readonly IReadOnlyList<string> Styles =
    [
        "body", "dimText", "accent", "accentSecondary", "accentTertiary", "label", "user", "assistant", "systemText", "errorText",
        "goodText", "warnText", "sectionHeading", "border", "tableHeader", "spinner", "paneRule", "hint", "trailerMark", "menuHighlight",
        "menuHighlightDim", "menuDisabled", "selectedText", "pasteLabel", "placeholder", "markdownBold", "markdownItalic", "markdownCode",
        "markdownCodeBlock", "markdownCodeLabel", "thinking", "markdownHeading1", "markdownHeading", "markdownBullet", "markdownQuoteBar",
        "markdownQuote", "markdownLinkUrl", "markdownRule", "codeKeyword", "codeType", "codeString", "codeNumber", "codeComment",
        "codePunctuation", "codeFunction", "codeVariable", "codeAttribute", "codeTag", "codeHeading", "codeInserted", "codeDeleted",
        "diffAdded", "diffRemoved",
    ];

    /// <summary>The word of <paramref name="slot"/>.</summary>
    public static string Of(ThemeColorSlot slot) => Colors[(int)slot];

    /// <summary>The word of <paramref name="slot"/>.</summary>
    public static string Of(ThemeStyleSlot slot) => Styles[(int)slot];

    /// <summary>The colour slot named <paramref name="key"/>, any case.</summary>
    public static bool TryParseColor(string? key, out ThemeColorSlot slot)
    {
        int index = IndexOf(Colors, key);
        slot = (ThemeColorSlot)Math.Max(index, 0);
        return index >= 0;
    }

    /// <summary>The style slot named <paramref name="key"/>, any case.</summary>
    public static bool TryParseStyle(string? key, out ThemeStyleSlot slot)
    {
        int index = IndexOf(Styles, key);
        slot = (ThemeStyleSlot)Math.Max(index, 0);
        return index >= 0;
    }

    /// <summary>The slot whose final style an alias slot starts from; null for a slot the palette derives itself.</summary>
    public static ThemeStyleSlot? AliasOf(ThemeStyleSlot slot) => slot switch
    {
        ThemeStyleSlot.User => ThemeStyleSlot.AccentSecondary,
        ThemeStyleSlot.Assistant => ThemeStyleSlot.Body,
        ThemeStyleSlot.SystemText => ThemeStyleSlot.DimText,
        ThemeStyleSlot.SectionHeading => ThemeStyleSlot.AccentTertiary,
        ThemeStyleSlot.Spinner => ThemeStyleSlot.AccentSecondary,
        ThemeStyleSlot.Hint => ThemeStyleSlot.DimText,
        ThemeStyleSlot.MarkdownCodeLabel => ThemeStyleSlot.DimText,
        ThemeStyleSlot.MarkdownHeading1 => ThemeStyleSlot.Accent,
        ThemeStyleSlot.MarkdownHeading => ThemeStyleSlot.AccentSecondary,
        ThemeStyleSlot.MarkdownBullet => ThemeStyleSlot.TrailerMark,
        ThemeStyleSlot.MarkdownQuoteBar => ThemeStyleSlot.TrailerMark,
        ThemeStyleSlot.MarkdownQuote => ThemeStyleSlot.DimText,
        ThemeStyleSlot.MarkdownLinkUrl => ThemeStyleSlot.DimText,
        ThemeStyleSlot.MarkdownRule => ThemeStyleSlot.PaneRule,
        ThemeStyleSlot.CodeAttribute => ThemeStyleSlot.CodeType,
        ThemeStyleSlot.CodeTag => ThemeStyleSlot.CodeKeyword,
        _ => null,
    };

    private static int IndexOf(IReadOnlyList<string> words, string? key)
    {
        string? word = key?.Trim();
        for (int i = 0; i < words.Count; i++)
        {
            if (string.Equals(words[i], word, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
