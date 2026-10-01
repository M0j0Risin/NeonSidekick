using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.UI;

/// <summary>How much of the window a menu may take (<see cref="MenuHeight"/>).</summary>
public enum MenuHeightStyle
{
    /// <summary>Half the window: the pane, its rules and its hint row included.</summary>
    Half,

    /// <summary>Three quarters of the window.</summary>
    ThreeQuarters,

    /// <summary>Everything but one transcript row (<see cref="ScreenPane.MaxOverlayRows"/>): the pane as it grew before the setting, the default.</summary>
    Full,
}

/// <summary>
/// The setting <c>Menus max height</c> (2026-10-01, the user's ask, with the tabs held at their tallest tab's height so
/// the pane no longer jumps as one tabs through it): <c>half-screen</c>, <c>three-quarters</c> and <c>full-screen</c> (the
/// default; <c>three-quarters</c> until later that day, the user's call), and the rows they leave a menu's content — the <see cref="SplashMode"/> shape. It caps the whole
/// pane of the <see cref="MenuPane"/>, the <see cref="InfoPane"/> and the <see cref="FolderPane"/> (the user's answer:
/// the pane, not only its padding): a list longer than the cap scrolls behind the viewport as it did at the window's edge.
/// The share is of <see cref="ScreenPane.LayoutHeight"/> (the window less the bars under the hint row), and counts the
/// pane's two rules and hint row and an open input slot; never past <see cref="ScreenPane.MaxOverlayRows"/>, and never
/// under <see cref="MinRows"/> while the window has them. An unknown saved word reads as <see cref="Default"/> with one
/// warning per word, not one per draw.
/// </summary>
public static class MenuHeight
{
    /// <summary>The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "full-screen";

    /// <summary>The heights in menu order.</summary>
    public static readonly string[] Names = { "half-screen", "three-quarters", "full-screen" };

    /// <summary>The fewest content rows a capped pane keeps when the window has them: a strip, the spacer and a few rows of a list.</summary>
    public const int MinRows = 6;

    /// <summary>The rows of a pane that are not content: the rule over it, the rule under it, the hint row.</summary>
    public const int ChromeRows = 3;

    private const string Category = "Menu";

    // The last unknown word warned about: the panes read the setting on every draw, so a hand-edited value warns once.
    private static string? s_warned;

    /// <summary>Trims and ignores case; false (and <see cref="MenuHeightStyle.Full"/>, the default) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out MenuHeightStyle style)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "half-screen": style = MenuHeightStyle.Half; return true;
            case "three-quarters": style = MenuHeightStyle.ThreeQuarters; return true;
            case "full-screen": style = MenuHeightStyle.Full; return true;
            default: style = MenuHeightStyle.Full; return false;
        }
    }

    /// <summary>The saved word for <paramref name="style"/>.</summary>
    public static string Name(MenuHeightStyle style) => style switch
    {
        MenuHeightStyle.Half => "half-screen",
        MenuHeightStyle.Full => "full-screen",
        _ => "three-quarters",
    };

    /// <summary>The menu hint next to a height. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "half-screen" => "a menu takes at most half the window",
        "three-quarters" => "a menu takes at most three quarters of the window",
        "full-screen" => "a menu grows to all but one row of the window",
        _ => "",
    };

    /// <summary>The height in force for the saved word <paramref name="text"/>; an unknown word warns (once) and uses <see cref="Default"/>.</summary>
    public static MenuHeightStyle Resolve(string? text)
    {
        if (TryParse(text, out var style))
        {
            return style;
        }

        if (!string.Equals(s_warned, text, StringComparison.Ordinal))
        {
            s_warned = text;
            DiagnosticLog.Warn(Category,
                $"{nameof(AppSettingsData.MenuMaxHeight)}='{text}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        TryParse(Default, out style);
        return style;
    }

    /// <summary>
    /// The content rows a menu may take under <paramref name="style"/> on a window of <paramref name="height"/> rows
    /// (the <see cref="ScreenPane.LayoutHeight"/>) over <paramref name="inputRows"/> input rows (0 without a slot):
    /// <see cref="ScreenPane.MaxOverlayRows"/> for <see cref="MenuHeightStyle.Full"/>; otherwise the share of the height
    /// less the <see cref="ChromeRows"/> and the input rows, at least <see cref="MinRows"/>, never past
    /// <see cref="ScreenPane.MaxOverlayRows"/>. Pure.
    /// </summary>
    public static int ContentRows(MenuHeightStyle style, int height, int inputRows)
    {
        int max = ScreenPane.MaxOverlayRows(height, inputRows);
        int pane = style switch
        {
            MenuHeightStyle.Half => height / 2,
            MenuHeightStyle.ThreeQuarters => height * 3 / 4,
            _ => int.MaxValue,
        };

        if (pane == int.MaxValue)
        {
            return max;
        }

        return Math.Min(max, Math.Max(MinRows, pane - ChromeRows - inputRows));
    }
}
