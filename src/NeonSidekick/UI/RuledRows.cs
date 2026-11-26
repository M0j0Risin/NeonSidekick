namespace NeonSidekick.UI;

/// <summary>
/// A page's rows in groups with a bare rule (<see cref="SectionRule.Bare"/>) between each two that have rows (2026-11-24, the
/// user's ask, from a screenshot of the UNC add/edit page: the pages that run several kinds of rows together get the rules the
/// advisor page, the skill page and <c>/usage</c> › Models gained on 2026-11-23). An empty group leaves no rule, so there is never a
/// rule at the top or bottom or two in a row. The groups come in as markup, each row escaped as its page escapes it;
/// <see cref="Rows"/> is never escaped again (a rule is markup). A page keeps its cursor and its picks as items (the rows less the
/// rules): <see cref="RowOf"/> turns an item into its page row and <see cref="ItemOf"/> a picked row back. Pure.
/// </summary>
internal sealed class RuledRows
{
    private readonly List<int> _itemRows = [];

    private RuledRows(List<string> rows, HashSet<int> rules)
    {
        Rows = rows;
        Headings = rules.Count == 0 ? null : rules;
        for (int r = 0; r < rows.Count; r++)
        {
            if (!rules.Contains(r))
            {
                _itemRows.Add(r);
            }
        }
    }

    /// <summary>The page's rows: the groups' rows with the rules between them.</summary>
    public IReadOnlyList<string> Rows { get; }

    /// <summary>The rule rows, for <see cref="MenuPage.Headings"/>; null when there are none.</summary>
    public IReadOnlySet<int>? Headings { get; }

    /// <summary>How many items (rows that aren't rules) there are.</summary>
    public int Items => _itemRows.Count;

    /// <summary><paramref name="groups"/>' rows, a <see cref="SectionRule.Bare"/> between each two non-empty groups.</summary>
    public static RuledRows Of(IReadOnlyList<IReadOnlyList<string>> groups) => Of(groups, null);

    /// <summary>
    /// <see cref="Of(IReadOnlyList{IReadOnlyList{string}})"/> with a heading (<see cref="SectionRule.Markup(string, string?, string?)"/>) in place of
    /// the bare rule before each group <paramref name="headings"/> names (2026-11-25, the ComfyUI workflows list's <c>Skipped</c>); a named first
    /// group gets its heading at the top. A null or missing heading is a bare rule, as before; an empty group still leaves nothing.
    /// </summary>
    public static RuledRows Of(IReadOnlyList<IReadOnlyList<string>> groups, IReadOnlyList<string?>? headings)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var rows = new List<string>();
        var rules = new HashSet<int>();
        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            if (group is null || group.Count == 0)
            {
                continue;
            }

            string? heading = headings is not null && g < headings.Count ? headings[g] : null;
            if (rows.Count > 0 || heading is not null)
            {
                rules.Add(rows.Count);
                rows.Add(heading is null ? SectionRule.Bare : SectionRule.Markup(heading));
            }

            rows.AddRange(group);
        }

        return new RuledRows(rows, rules);
    }

    /// <summary>The item a page row stands for: -1 for a rule; a negative row (Esc, no pick) is itself.</summary>
    public int ItemOf(int row)
    {
        if (row < 0)
        {
            return row;
        }

        if (Headings is { } rules && rules.Contains(row))
        {
            return -1;
        }

        int item = _itemRows.BinarySearch(row);
        return item >= 0 ? item : _itemRows.Count == 0 ? row : row - (Rows.Count - _itemRows.Count);
    }

    /// <summary>
    /// The page row of <paramref name="item"/>, clamped: past the last item gives the last item's row, a negative one the first's, so a
    /// cursor kept from before a removal never lands on a rule or off the page. 0 with no items, as the pane's own empty page.
    /// </summary>
    public int RowOf(int item) => _itemRows.Count == 0 ? 0 : _itemRows[Math.Clamp(item, 0, _itemRows.Count - 1)];

    /// <summary>The same keys as <paramref name="byItem"/>, each at its item's page row.</summary>
    public IReadOnlyDictionary<char, int> Hotkeys(IReadOnlyDictionary<char, int> byItem)
    {
        ArgumentNullException.ThrowIfNull(byItem);
        return byItem.ToDictionary(p => p.Key, p => RowOf(p.Value));
    }
}
