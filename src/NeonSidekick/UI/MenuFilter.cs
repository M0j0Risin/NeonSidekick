namespace NeonSidekick.UI;

/// <summary>
/// The type-to-filter of a list tab (2026-10-03, the user's ask: "type a string like 'haiku' and it would only show me the
/// ones with haiku in the name", name or description by the user's pick): <c>/tools</c>' Offered tab, <c>/skills</c>' Offered
/// tab and <c>/mcp</c>'s Tools tab. A tab that filters carries its text as <see cref="MenuTab.Filter"/>; the pane hands each
/// typed character, Backspace and ESC back as <see cref="MenuPick.Filter"/> (<see cref="Edit"/>), and the menu builds the tab's
/// rows again with the rows that <see cref="Matches"/>, the way the embedded model lists' filter buttons rebuild theirs.
/// The wording is here so the tests can pin it.
/// </summary>
public static class MenuFilter
{
    /// <summary>The most characters a filter holds; a key past it is nothing.</summary>
    public const int MaxLength = 40;

    /// <summary>The hint row's piece while nothing is typed. Pinned.</summary>
    public const string TypeKeys = "type = filter";

    /// <summary>The hint row's piece while a filter is typed. Pinned.</summary>
    public const string FilteringKeys = "Backspace = erase · ESC = clear filter";

    /// <summary>
    /// Whether a row whose name is <paramref name="name"/> and description <paramref name="description"/> stays under
    /// <paramref name="filter"/>: an empty filter keeps every row; otherwise either text holds the filter, case folded. Pure.
    /// </summary>
    public static bool Matches(string filter, string name, string? description)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(name);
        return filter.Length == 0
            || name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// The filter after <paramref name="key"/>, or null when the key is no edit of it (the pane goes on with the key as
    /// before): Backspace takes the last character off a filter that has one; a character that types (not a control
    /// character, not the console's <c>'\0'</c>) goes on the end — a space never first, and never on a page where Space is a
    /// flip (<paramref name="spaceToggles"/>) — up to <see cref="MaxLength"/>. Pure.
    /// </summary>
    public static string? Edit(string filter, ConsoleKeyInfo key, bool spaceToggles)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (key.Key == ConsoleKey.Backspace)
        {
            return filter.Length > 0 ? filter[..^1] : null;
        }

        char c = key.KeyChar;
        if (c == '\0' || char.IsControl(c) || (c == ' ' && (spaceToggles || filter.Length == 0)) || filter.Length >= MaxLength)
        {
            return null;
        }

        return filter + c;
    }

    /// <summary>The tab's caption while a filter is typed: the text and how many of the rows it keeps (<c>Filter: haiku · 3 of 128</c>). Pinned.</summary>
    public static string Caption(string filter, int shown, int total) =>
        "Filter: " + filter + " · " + shown.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of " + total.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The one row a filter that keeps nothing leaves, dim. Pinned.</summary>
    public static string NoMatchLine(string filter) => "Nothing here has \"" + filter + "\" in its name or description.";

    /// <summary><see cref="NoMatchLine"/> as a dim markup row.</summary>
    public static string NoMatchRow(string filter) => Theme.DimMarkup(NoMatchLine(filter));

    /// <summary>The no-match row's line on a list whose rows have a name alone (the model picker, 2026-10-03). Pinned.</summary>
    public static string NoMatchNameLine(string filter) => "Nothing here has \"" + filter + "\" in its name.";

    /// <summary><see cref="NoMatchNameLine"/> as a dim markup row.</summary>
    public static string NoMatchNameRow(string filter) => Theme.DimMarkup(NoMatchNameLine(filter));

    /// <summary>The end of a filtering tab's own hint while nothing is typed (<c>ToolsText.OfferedKeys</c>, <c>SkillsMenu.LoadedKeys</c>). Pinned.</summary>
    public const string TypeAndCloseKeys = TypeKeys + " · ESC = close";

    /// <summary>The end of a filtering picker's hint where ESC keeps what is in use (the model picker, 2026-10-03). Pinned.</summary>
    public const string TypeAndKeepKeys = TypeKeys + " · ESC = keep";

    /// <summary>
    /// A filtering tab's hint: <paramref name="keys"/> (which ends in <see cref="TypeAndCloseKeys"/> or <see cref="TypeAndKeepKeys"/>)
    /// as it is while nothing is typed, else with <see cref="FilteringKeys"/> in place of that end (ESC clears the filter first). Pure.
    /// </summary>
    public static string Hint(string keys, string filter)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Length > 0)
        {
            foreach (string end in (ReadOnlySpan<string>)[TypeAndCloseKeys, TypeAndKeepKeys])
            {
                if (keys.EndsWith(end, StringComparison.Ordinal))
                {
                    return keys[..^end.Length] + FilteringKeys;
                }
            }
        }

        return keys;
    }

    /// <summary>The caption for a filtering tab: null while nothing is typed, else <see cref="Caption"/>.</summary>
    public static string? CaptionOrNull(string filter, int shown, int total) => filter.Length == 0 ? null : Caption(filter, shown, total);
}
