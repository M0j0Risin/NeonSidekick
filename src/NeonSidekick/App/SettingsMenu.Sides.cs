using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// The Hint bar's and the Tool bar's checklists in two groups (2026-10-13, the user's ask: "any item for the bar can be freely
/// assigned to pin left or right … within either the left or right group, the sort applies freely"): a heading, the items pinned
/// left in their order, a heading, the items pinned right in theirs (<see cref="ItemOrder.Sided"/>). Shift+←/→ pins the cursor's item
/// to the other side (to the right group's start, the left group's end: next to the gap); Shift+↑↓ moves it within its group,
/// crossing into the other at the group's edge; Shift+Home/End to the top or the bottom of its group. The checks, Delete and the
/// title row's buttons are the checklists' as before; <c>⊡ default</c> puts back the sides with the checks and the order.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The checklist's first heading (2026-10-13). Pinned.</summary>
    public const string PinnedLeftHeading = "Pinned left";

    /// <summary>The checklist's second heading (2026-10-13). Pinned.</summary>
    public const string PinnedRightHeading = "Pinned right";

    /// <summary>The note on a group's heading while nothing is pinned there (2026-10-13). Pinned.</summary>
    public static string EmptySideNote(bool right) => "none: Shift+" + (right ? "→" : "←") + " pins the item under the cursor here";

    /// <summary>
    /// The rows of a checklist in two groups: <see cref="PinnedLeftHeading"/>'s rule, a <paramref name="label"/> row per left item, then
    /// <see cref="PinnedRightHeading"/>'s and the right items'; the two headings' indices. Each heading counts its group, and an empty
    /// group's says so (<see cref="EmptySideNote"/>). Pure.
    /// </summary>
    public static (List<string> Rows, HashSet<int> Headings) SidedRows(ItemOrder.Sided groups, Func<string, string> label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var rows = new List<string>(groups.Left.Count + groups.Right.Count + 2);
        var headings = new HashSet<int>();
        foreach (var (title, items, right) in new[] { (PinnedLeftHeading, groups.Left, false), (PinnedRightHeading, groups.Right, true) })
        {
            headings.Add(rows.Count);
            rows.Add(SectionRule.Markup(title, items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), items.Count == 0 ? EmptySideNote(right) : null));
            rows.AddRange(items.Select(label));
        }

        return (rows, headings);
    }

    /// <summary>The item (an index into <see cref="ItemOrder.Sided.All"/>) on page row <paramref name="row"/> of <see cref="SidedRows"/>; −1 on a heading. Pure.</summary>
    public static int SidedItemAt(ItemOrder.Sided groups, int row)
    {
        int left = groups.Left.Count;
        return row >= 1 && row <= left ? row - 1
            : row >= left + 2 && row < left + 2 + groups.Right.Count ? row - 2
            : -1;
    }

    /// <summary>The page row of <see cref="SidedRows"/> that shows item <paramref name="index"/> (into <see cref="ItemOrder.Sided.All"/>). Pure.</summary>
    public static int SidedRowOf(ItemOrder.Sided groups, int index) => index < groups.Left.Count ? index + 1 : index + 2;

    /// <summary>What a sided checklist reads and writes: the field it saves under, its ids and labels, and its three saved lists.</summary>
    private sealed record SidedChecklist(
        SettingsField Field,
        string Hint,
        IReadOnlyList<string> Names,
        IReadOnlyList<string> Defaults,
        Func<AppSettingsData, IReadOnlySet<string>> On,
        Func<AppSettingsData, ItemOrder.Sided> Sides,
        Func<string, bool, string> Label,
        Action<AppSettingsData, IReadOnlySet<string>> SaveOn,
        Action<AppSettingsData, ItemOrder.Sided> SaveSides,
        Action<AppSettingsData> Reset,
        Func<AppSettingsData, bool> IsDefault);

    /// <summary>The Tool bar's checklist (<see cref="EditToolbarItemsAsync"/>).</summary>
    private static readonly SidedChecklist ToolbarChecklist = new(
        SettingsField.ToolbarItems,
        ToolbarToggleKeys,
        App.ToolbarItems.Names,
        App.ToolbarItems.Defaults,
        d => App.ToolbarItems.Resolve(d.ToolbarItems),
        d => App.ToolbarItems.Sides(d.ToolbarOrder, d.ToolbarRight),
        App.ToolbarItems.Label,
        (d, on) => d.ToolbarItems = App.ToolbarItems.Save(on),
        (d, sides) => { d.ToolbarOrder = App.ToolbarItems.SaveOrder(sides.All); d.ToolbarRight = App.ToolbarItems.SaveRight(sides.Right); },
        d => { d.ToolbarItems = null; d.ToolbarOrder = null; d.ToolbarRight = null; },
        d => App.ToolbarItems.Resolve(d.ToolbarItems).SetEquals(App.ToolbarItems.Defaults)
            && App.ToolbarItems.SaveOrder(App.ToolbarItems.Sides(d.ToolbarOrder, d.ToolbarRight).All) is null
            && App.ToolbarItems.SaveRight(App.ToolbarItems.ResolveRight(d.ToolbarRight)) is null);

    /// <summary>The Hint bar's checklist (<see cref="EditHintBarAsync"/>).</summary>
    private static readonly SidedChecklist HintBarChecklist = new(
        SettingsField.HintBarItems,
        HintBarToggleKeys,
        App.HintBarItems.Names,
        App.HintBarItems.Defaults,
        d => App.HintBarItems.Resolve(d.HintBarItems),
        d => App.HintBarItems.Sides(d.HintBarOrder, d.HintBarRight),
        App.HintBarItems.Label,
        (d, on) => d.HintBarItems = App.HintBarItems.Save(on),
        (d, sides) => { d.HintBarOrder = App.HintBarItems.SaveOrder(sides.All); d.HintBarRight = App.HintBarItems.SaveRight(sides.Right); },
        d => { d.HintBarItems = null; d.HintBarOrder = null; d.HintBarRight = null; },
        d => App.HintBarItems.Resolve(d.HintBarItems).SetEquals(App.HintBarItems.Defaults)
            && App.HintBarItems.SaveOrder(App.HintBarItems.Sides(d.HintBarOrder, d.HintBarRight).All) is null
            && App.HintBarItems.SaveRight(App.HintBarItems.ResolveRight(d.HintBarRight)) is null);

    /// <summary>
    /// A checklist in two groups under the settings list (2026-10-13): Enter or Space flips the cursor's item and saves at once, the
    /// move keys and Shift+←/→ (<see cref="MenuPage.Sides"/>) reorder it and save, Delete unchecks it, the title row's select all,
    /// select none and default; re-shown until ESC, the bar redrawing under it. True when anything changed.
    /// </summary>
    private async Task<bool> EditSidedChecklistAsync(SidedChecklist list, CancellationToken cancellationToken)
    {
        bool changed = false;
        int index = 0;
        while (true)
        {
            var saved = _settings.Current;
            var on = list.On(saved);
            var sides = list.Sides(saved);
            var all = sides.All;
            var (rows, headings) = SidedRows(sides, id => list.Label(id, on.Contains(id)));
            var page = new MenuPage(Crumb(FieldName(list.Field)), rows, list.Hint) { SpaceToggles = true, Headings = headings };
            var picked = await PickSidedChecklistAsync(page, SidedRowOf(sides, Math.Clamp(index, 0, all.Count - 1)), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            int at = SidedItemAt(sides, pick.Row);
            if (pick.Button == DefaultsIndex)
            {
                if (!list.IsDefault(saved))
                {
                    Apply(list.Field, list.Reset);
                    changed = true;
                }

                continue;   // the default checks, order and sides already: nothing to save
            }

            if (pick.Button < 0 && at < 0)
            {
                continue;   // a heading (the prompt without the pane lists them too): nothing there
            }

            index = Math.Max(0, at);
            if (pick.Move != 0 || pick.Side != 0)
            {
                var (moved, landed) = pick.Side != 0 ? ItemOrder.ToSide(sides, at, toRight: pick.Side > 0) : ItemOrder.MoveInGroups(sides, at, pick.Move);
                index = landed;
                if (!moved.All.SequenceEqual(all, StringComparer.Ordinal) || moved.Left.Count != sides.Left.Count)
                {
                    Apply(list.Field, d => list.SaveSides(d, moved));
                    changed = true;
                }

                continue;
            }

            string row = all[index];
            var next = pick.Button == SelectAllIndex ? list.Names.ToHashSet(StringComparer.Ordinal)
                : pick.Button == SelectNoneIndex ? new HashSet<string>(StringComparer.Ordinal)
                : pick.Remove ? list.Names.Where(n => on.Contains(n) && n != row).ToHashSet(StringComparer.Ordinal)
                : list.Names.Where(n => on.Contains(n) != (n == row)).ToHashSet(StringComparer.Ordinal);
            if (next.SetEquals(on))
            {
                continue;   // select all with every item on, none with none, Delete on an unchecked row: nothing to save
            }

            Apply(list.Field, d => list.SaveOn(d, next));
            changed = true;
        }
    }

    /// <summary>
    /// <see cref="PickOrderedChecklistAsync"/> for a checklist in two groups (2026-10-13): <see cref="MenuPage.Sides"/> on too, the pick
    /// whole. Without the pane, the row alone (no buttons, moves or sides).
    /// </summary>
    private async Task<MenuPick?> PickSidedChecklistAsync(MenuPage page, int cursor, CancellationToken cancellationToken)
    {
        if (_pane.Enabled)
        {
            return await _pane.PickAsync(page with { Buttons = DefaultChecklistButtons, Moves = true, Sides = true }, cursor, cancellationToken).ConfigureAwait(false);
        }

        return await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is { } row ? new MenuPick(0, row) : null;
    }
}
