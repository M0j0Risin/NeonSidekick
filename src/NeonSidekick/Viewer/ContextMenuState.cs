namespace NeonSidekick.Viewer;

/// <summary>
/// One row of a picture menu (2026-10-04, <see cref="ContextMenuState"/>): its words, the command it runs, the rows of the
/// submenu it opens instead (one level only), whether it can be chosen, or a separator line.
/// </summary>
public sealed record ContextMenuItem(string Label, int Command = 0, IReadOnlyList<ContextMenuItem>? Children = null, bool Enabled = true, bool IsSeparator = false)
{
    /// <summary>A separator line between groups.</summary>
    public static ContextMenuItem Separator { get; } = new("", Enabled: false, IsSeparator: true);

    /// <summary>Whether it opens a submenu.</summary>
    public bool HasChildren => Children is { Count: > 0 };

    /// <summary>Whether the keys and the mouse can land on it.</summary>
    public bool Selectable => Enabled && !IsSeparator;
}

/// <summary>What a key or a click did to a menu (<see cref="ContextMenuState"/>).</summary>
public enum MenuOutcome
{
    /// <summary>Nothing: the key is not the menu's.</summary>
    None,

    /// <summary>The highlight or the open submenu changed: draw again.</summary>
    Changed,

    /// <summary>The menu closed with nothing chosen (Esc, a click outside).</summary>
    Close,

    /// <summary>A row was chosen: <see cref="ContextMenuState.Chosen"/> holds its command.</summary>
    Chosen,
}

/// <summary>
/// The picture menu's state, with no window in it (2026-10-04, the user's ask: a right-click on a thumbnail, or on the picture in
/// the viewer, runs the image tools; the user's pick: drawn in the theme, not Windows' own menu). A root list of rows and at most
/// one open submenu: the mouse highlights and opens a submenu by hovering, a click chooses; the keys move as Windows' menus do —
/// ↑ ↓ (separators and disabled rows skipped, wrapping), → or Enter into a submenu, ← or Esc out of it, Esc again closes, Home and End
/// the ends. The layout's arithmetic (<see cref="ItemTop"/>, <see cref="ItemAt"/>, <see cref="Place"/>, <see cref="PlaceSub"/>) is here too,
/// so <see cref="ContextMenuWindow"/> only draws. Pure; one thread (the owner window's).
/// </summary>
public sealed class ContextMenuState
{
    public ContextMenuState(IReadOnlyList<ContextMenuItem> items, bool keyboard = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
        if (keyboard)
        {
            Hot = Next(items, -1, 1);
        }
    }

    /// <summary>The root rows.</summary>
    public IReadOnlyList<ContextMenuItem> Items { get; }

    /// <summary>The highlighted root row, -1 for none.</summary>
    public int Hot { get; private set; } = -1;

    /// <summary>The root row whose submenu shows; null with none open.</summary>
    public int? Open { get; private set; }

    /// <summary>The highlighted submenu row, -1 for none.</summary>
    public int SubHot { get; private set; } = -1;

    /// <summary>Whether the keys move in the submenu (it was entered) rather than the root.</summary>
    public bool InSub { get; private set; }

    /// <summary>The open submenu's rows; null with none open.</summary>
    public IReadOnlyList<ContextMenuItem>? SubItems => Open is int open ? Items[open].Children : null;

    /// <summary>The chosen row's command, once <see cref="MenuOutcome.Chosen"/> was answered.</summary>
    public int Chosen { get; private set; }

    // Virtual-key codes the menu answers.
    public const int VkReturn = 0x0D;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkEnd = 0x23;
    public const int VkHome = 0x24;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;

    /// <summary>A key: what it did. A key the menu has no use for is <see cref="MenuOutcome.None"/>.</summary>
    public MenuOutcome Key(int virtualKey)
    {
        switch (virtualKey)
        {
            case VkUp or VkDown:
            {
                int step = virtualKey == VkDown ? 1 : -1;
                if (InSub && SubItems is { } sub)
                {
                    SubHot = Next(sub, SubHot, step);
                }
                else
                {
                    Hot = Next(Items, Hot, step);
                    Open = null;
                }

                return MenuOutcome.Changed;
            }

            case VkHome or VkEnd:
            {
                var list = InSub && SubItems is { } sub ? sub : Items;
                int at = virtualKey == VkHome ? Next(list, -1, 1) : Next(list, list.Count, -1);
                if (InSub)
                {
                    SubHot = at;
                }
                else
                {
                    Hot = at;
                    Open = null;
                }

                return MenuOutcome.Changed;
            }

            case VkRight when !InSub:
                return Enter();
            case VkRight:
                return MenuOutcome.Changed;
            case VkLeft when InSub:
                return Leave();
            case VkLeft:
                return MenuOutcome.Changed;
            case VkReturn or VkSpace:
                if (InSub)
                {
                    return SubItems is { } items && SubHot >= 0 && SubHot < items.Count && items[SubHot].Selectable ? Choose(items[SubHot]) : MenuOutcome.Changed;
                }

                if (Hot >= 0 && Items[Hot] is { Selectable: true } row)
                {
                    return row.HasChildren ? Enter() : Choose(row);
                }

                return MenuOutcome.Changed;
            case VkEscape:
                return InSub || Open is not null ? Leave() : MenuOutcome.Close;
            default:
                return MenuOutcome.None;
        }
    }

    /// <summary>The mouse over root row <paramref name="index"/> (-1 for none): highlighted, its submenu opened, another one closed.</summary>
    public MenuOutcome HoverRoot(int index)
    {
        if (index < 0 || index >= Items.Count || !Items[index].Selectable)
        {
            return MenuOutcome.None;
        }

        if (Hot == index && !InSub)
        {
            return MenuOutcome.None;
        }

        Hot = index;
        InSub = false;
        SubHot = -1;
        Open = Items[index].HasChildren ? index : null;
        return MenuOutcome.Changed;
    }

    /// <summary>The mouse over row <paramref name="index"/> of the open submenu: highlighted, the keys moving there.</summary>
    public MenuOutcome HoverSub(int index)
    {
        if (SubItems is not { } items || index < 0 || index >= items.Count || !items[index].Selectable || (SubHot == index && InSub))
        {
            return MenuOutcome.None;
        }

        SubHot = index;
        InSub = true;
        return MenuOutcome.Changed;
    }

    /// <summary>A click on root row <paramref name="index"/>: its submenu opened, or it chosen.</summary>
    public MenuOutcome ClickRoot(int index)
    {
        if (index < 0 || index >= Items.Count || !Items[index].Selectable)
        {
            return MenuOutcome.None;
        }

        Hot = index;
        if (Items[index].HasChildren)
        {
            Open = index;
            return MenuOutcome.Changed;
        }

        return Choose(Items[index]);
    }

    /// <summary>A click on row <paramref name="index"/> of the open submenu: it chosen.</summary>
    public MenuOutcome ClickSub(int index) =>
        SubItems is { } items && index >= 0 && index < items.Count && items[index].Selectable ? Choose(items[index]) : MenuOutcome.None;

    /// <summary>The next selectable row from <paramref name="from"/> by <paramref name="step"/>, wrapping; -1 when there is none. Pure.</summary>
    public static int Next(IReadOnlyList<ContextMenuItem> items, int from, int step)
    {
        ArgumentNullException.ThrowIfNull(items);
        int count = items.Count;
        for (int i = 1; i <= count; i++)
        {
            int at = ((from + step * i) % count + count) % count;
            if (items[at].Selectable)
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>The top of row <paramref name="index"/> in its popup: <paramref name="padding"/>, then every row before it. Pure.</summary>
    public static int ItemTop(IReadOnlyList<ContextMenuItem> items, int index, int itemHeight, int separatorHeight, int padding)
    {
        ArgumentNullException.ThrowIfNull(items);
        int top = padding;
        for (int i = 0; i < index && i < items.Count; i++)
        {
            top += items[i].IsSeparator ? separatorHeight : itemHeight;
        }

        return top;
    }

    /// <summary>A popup's whole height for <paramref name="items"/>. Pure.</summary>
    public static int Height(IReadOnlyList<ContextMenuItem> items, int itemHeight, int separatorHeight, int padding) =>
        ItemTop(items, items.Count, itemHeight, separatorHeight, padding) + padding;

    /// <summary>The selectable row at <paramref name="y"/> in a popup, -1 for none (a separator, a disabled row, the padding). Pure.</summary>
    public static int ItemAt(IReadOnlyList<ContextMenuItem> items, int y, int itemHeight, int separatorHeight, int padding)
    {
        ArgumentNullException.ThrowIfNull(items);
        int top = padding;
        for (int i = 0; i < items.Count; i++)
        {
            int height = items[i].IsSeparator ? separatorHeight : itemHeight;
            if (y >= top && y < top + height)
            {
                return items[i].Selectable ? i : -1;
            }

            top += height;
        }

        return -1;
    }

    /// <summary>
    /// Where a popup of <paramref name="width"/> × <paramref name="height"/> opened at (<paramref name="x"/>, <paramref name="y"/>) goes:
    /// right of and below the point, flipped left or up where the work area (<paramref name="workLeft"/>…<paramref name="workBottom"/>)
    /// ends, and never past its edges. Pure.
    /// </summary>
    public static (int X, int Y) Place(int x, int y, int width, int height, int workLeft, int workTop, int workRight, int workBottom)
    {
        int left = x + width > workRight ? x - width : x;
        int top = y + height > workBottom ? y - height : y;
        return (Math.Max(workLeft, Math.Min(left, workRight - width)), Math.Max(workTop, Math.Min(top, workBottom - height)));
    }

    /// <summary>
    /// Where a submenu goes: beside its parent popup (<paramref name="parentLeft"/>, <paramref name="parentRight"/>), on the right when it
    /// fits and on the left otherwise, its top level with its row's (<paramref name="rowTop"/>, screen), kept inside the work area. Pure.
    /// </summary>
    public static (int X, int Y) PlaceSub(int parentLeft, int parentRight, int rowTop, int width, int height, int workLeft, int workTop, int workRight, int workBottom)
    {
        int left = parentRight + width <= workRight ? parentRight : parentLeft - width;
        return (Math.Max(workLeft, Math.Min(left, workRight - width)), Math.Max(workTop, Math.Min(rowTop, workBottom - height)));
    }

    private MenuOutcome Enter()
    {
        if (Hot < 0 || !Items[Hot].HasChildren)
        {
            return MenuOutcome.Changed;
        }

        Open = Hot;
        InSub = true;
        SubHot = Next(Items[Hot].Children!, -1, 1);
        return MenuOutcome.Changed;
    }

    private MenuOutcome Leave()
    {
        InSub = false;
        SubHot = -1;
        Open = null;
        return MenuOutcome.Changed;
    }

    private MenuOutcome Choose(ContextMenuItem item)
    {
        Chosen = item.Command;
        return MenuOutcome.Chosen;
    }
}
