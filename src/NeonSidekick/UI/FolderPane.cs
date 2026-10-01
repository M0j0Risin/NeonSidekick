using NeonSidekick.Files;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The folder picker under <c>/cwd browse</c> (2026-09-21, the user's ask): a <see cref="FolderTree"/>
/// on the bottom pane — the strip with its one button, the highlighted node's path, then the
/// tree's visible rows in a viewport — read until a folder is chosen or the pane backs out.
/// Not a <see cref="MenuPane"/> page: that list is flat and spends ←/→ and Space on tabs and
/// toggles; here they open and close nodes. What the two share (the pointer row, the viewport,
/// the more row, the strip's layout and hit test, the double-click's pairing) is reused.
/// <para>
/// Keys: ↑/↓, PgUp/PgDn, Home/End move; → opens a node (or steps to its first child once open),
/// ← closes it (or steps to its parent once closed); Space toggles; Enter chooses the highlighted
/// folder; <c>-</c> collapses every node; a letter or digit jumps to the next name starting with
/// it; ESC and Ctrl+C choose nothing. Mouse (while the pane holds it): a left click on the
/// glyph's side of a row toggles that node, on the name moves the cursor there — a second on the
/// same row within <see cref="DoubleClick.Interval"/> toggles it too (Explorer's double-click,
/// the user's call later on 2026-09-21 over the app's double-click-picks: only Enter chooses, so
/// a folder is never picked by a slip of the mouse); one on the strip's button collapses all;
/// the × and two off the pane close; the wheel moves the cursor a row per notch.
/// </para>
/// </summary>
public sealed class FolderPane
{
    /// <summary>The overlay rows above the first tree row: the strip and the path row.</summary>
    public const int HeaderRows = 2;

    private const int DefaultHeight = MenuPane.DefaultHeight;

    private readonly ScreenPane _pane;
    private readonly KeySource _keys;
    private readonly Action<bool>? _mouse;
    private readonly DoubleClick _clicks;
    private FolderTree? _tree;
    private int _cursor;
    private int _first;
    private int _shown;
    private string? _status;

    /// <param name="mouse">Takes (true) or hands back (false) the console's mouse; null when the screen has none to take.</param>
    public FolderPane(ScreenPane pane, KeySource keys, Action<bool>? mouse = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _mouse = mouse;
        _clicks = new DoubleClick(pane.Time);
    }

    /// <summary>The pane can host the picker: the screen has the bottom pane.</summary>
    public bool Enabled => _pane.Enabled;

    /// <summary>The strip: the label and the one button, drawn as a dim tab nobody is on. Pinned.</summary>
    public static string StripMarkup() => InfoPane.TabStripMarkup(FolderText.Title, [FolderText.CollapseAllButton], -1);

    /// <summary>Whether column <paramref name="x"/> of the strip is the button (<see cref="InfoPane.TabAt"/>'s layout). Pure.</summary>
    public static bool ButtonAt(int x) => InfoPane.TabAt(FolderText.Title, [FolderText.CollapseAllButton], x) == 0;

    /// <summary>
    /// Shows <paramref name="tree"/> with row <paramref name="cursor"/> highlighted and reads
    /// keys until Enter (the highlighted node's full path), ESC, the token, or the end of input
    /// (null); the pane is closed on every path.
    /// </summary>
    public async Task<string?> PickAsync(FolderTree tree, int cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (!Enabled)
        {
            throw new InvalidOperationException("The folder pane needs the bottom pane.");
        }

        _tree = tree;
        _cursor = Clamp(cursor);
        _first = 0;
        _status = null;
        _clicks.Reset();
        _mouse?.Invoke(true);
        try
        {
            Show();
            while (true)
            {
                // A command chord (2026-10-01, the user's ask: as everywhere else) is done in place, or nothing is picked as
                // on ESC and the screen runs it (KeySource.ReadPaneInputAsync).
                var input = await _keys.ReadPaneInputAsync(_pane, cancellationToken).ConfigureAwait(false);
                if (input is null)
                {
                    return null;
                }

                if (input is InputEvent.Click click)
                {
                    if (click.Button != MouseButton.Left)
                    {
                        _clicks.Reset();
                        continue;
                    }

                    if (_pane.TryHitClose(click.X, click.Y))
                    {
                        return null;
                    }

                    if (!_pane.TryHitOverlay(click.X, click.Y, out int at))
                    {
                        // Two off the pane within the interval close it, like every pane's — on the
                        // same part (later on 2026-09-21), the dismiss keeping it for the screen's
                        // close-or-switch; a gap is nothing.
                        if (!_pane.TryHitOutside(click.X, click.Y))
                        {
                            _clicks.Reset();
                        }
                        else if (_clicks.Second(_pane.OutsideKey(click.X, click.Y)))
                        {
                            _pane.Dismiss(click.X, click.Y);
                            return null;
                        }

                        continue;
                    }

                    if (at == 0)
                    {
                        _clicks.Reset();
                        if (ButtonAt(click.X))
                        {
                            CollapseAll();
                        }
                    }
                    else if (at - HeaderRows is int i && i >= 0 && i < _shown)
                    {
                        int row = _first + i;
                        var node = tree.Visible[row];
                        if (click.X < FolderText.NameColumn(node.Depth))
                        {
                            // The glyph's side: the node opens or closes, the cursor comes along.
                            _clicks.Reset();
                            _cursor = row;
                            Toggle();
                        }
                        else
                        {
                            // The name: the cursor; a second click on the same row within the interval opens or closes it.
                            MoveTo(row);
                            if (_clicks.Second(row))
                            {
                                Toggle();
                            }
                        }
                    }
                    else
                    {
                        _clicks.Reset();
                    }

                    continue;
                }

                if (input is InputEvent.Drag or InputEvent.Release or InputEvent.Paste)
                {
                    // A jiggle between the two presses of a double-click keeps the pair, as does the release between
                    // them (2026-09-28, the button's release became an event); a paste has nowhere to land.
                    continue;
                }

                _clicks.Reset();
                if (input is InputEvent.Wheel wheel)
                {
                    MoveTo(Clamp(_cursor - wheel.Notches));
                    continue;
                }

                if ((input as InputEvent.Key)?.Info is not { } k || Keys.IsCancel(k) || Keys.IsInterrupt(k))
                {
                    return null;
                }

                if (k.Key == ConsoleKey.Enter)
                {
                    return tree.Visible.Count == 0 ? null : tree.Visible[_cursor].Path;
                }

                if (k.KeyChar == ' ')
                {
                    Toggle();
                    continue;
                }

                if (k.KeyChar == FolderText.CollapseAllKey)
                {
                    CollapseAll();
                    continue;
                }

                switch (k.Key)
                {
                    case ConsoleKey.RightArrow: StepIn(); continue;
                    case ConsoleKey.LeftArrow: StepOut(); continue;
                    case ConsoleKey.DownArrow: MoveTo(Clamp(_cursor + 1)); continue;
                    case ConsoleKey.UpArrow: MoveTo(Clamp(_cursor - 1)); continue;
                    case ConsoleKey.Home: MoveTo(0); continue;
                    case ConsoleKey.End: MoveTo(Clamp(int.MaxValue)); continue;
                    case ConsoleKey.PageDown: MoveTo(Clamp(_cursor + Math.Max(1, _shown))); continue;
                    case ConsoleKey.PageUp: MoveTo(Clamp(_cursor - Math.Max(1, _shown))); continue;
                }

                if (k.KeyChar is not '\0' && !char.IsControl(k.KeyChar) && tree.JumpFrom(_cursor, k.KeyChar) is int jump && jump >= 0)
                {
                    MoveTo(jump);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // No keyboard: the read ran dry, the pane closes.
            return null;
        }
        finally
        {
            _tree = null;
            _pane.CloseOverlay();
            _mouse?.Invoke(false);
        }
    }

    private int Clamp(int row) => _tree is { Visible.Count: > 0 and var count } ? Math.Clamp(row, 0, count - 1) : 0;

    // The cursor onto another row: redrawn; the same row is nothing.
    private void MoveTo(int row)
    {
        if (row == _cursor)
        {
            return;
        }

        _cursor = row;
        Show();
    }

    // Space, or a click on the glyph: the node opens or closes; a refused read leaves the notice.
    private void Toggle()
    {
        var tree = _tree!;
        if (tree.Visible.Count == 0)
        {
            return;
        }

        var node = tree.Visible[_cursor];
        if (node.Expanded)
        {
            tree.Collapse(_cursor);
        }
        else if (!tree.Expand(_cursor) && node.Denied)
        {
            _status = FolderText.DeniedNotice(node.Path);
        }

        Show();
    }

    // →: open the node, or step onto its first child once it is open.
    private void StepIn()
    {
        var tree = _tree!;
        if (tree.Visible.Count == 0)
        {
            return;
        }

        var node = tree.Visible[_cursor];
        if (node.Expanded)
        {
            MoveTo(Clamp(_cursor + 1));
            return;
        }

        Toggle();
    }

    // ←: close the node, or step onto its parent once it is closed.
    private void StepOut()
    {
        var tree = _tree!;
        if (tree.Visible.Count == 0)
        {
            return;
        }

        if (tree.Visible[_cursor].Expanded)
        {
            tree.Collapse(_cursor);
            Show();
            return;
        }

        if (tree.ParentOf(_cursor) is int parent && parent >= 0)
        {
            MoveTo(parent);
        }
    }

    private void CollapseAll()
    {
        _cursor = _tree!.CollapseAll(_cursor);
        _first = 0;
        Show();
    }

    /// <summary>The window less the toolbar's row (<see cref="ScreenPane.LayoutHeight"/>, 2026-09-21): what the pane's caps are counted over.</summary>
    private int Height => _pane.Profile.Height > 0 ? _pane.LayoutHeight : DefaultHeight;

    /// <summary>The pane laid out for the window: the strip, the path row (or the status), the rows in view, the more row.</summary>
    private void Show()
    {
        var tree = _tree!;
        int count = tree.Visible.Count;
        int capacity = _pane.MenuContentRows(Height, 0) - HeaderRows;   // within Menus max height (2026-10-01)
        (_first, _shown) = MenuPane.Viewport(count, _cursor, capacity, _first);

        var lines = new List<IRenderable>(HeaderRows + _shown + 1) { new Markup(StripMarkup()) };
        string second = _status is { } status ? TranscriptRenderer.ErrorMarkup(status)
            : count == 0 ? " "
            : FolderText.PathMarkup(tree.Visible[_cursor].Path);
        _status = null;
        lines.Add(new FittedMarkup(second));
        for (int i = 0; i < _shown; i++)
        {
            int r = _first + i;
            lines.Add(new FittedMarkup(MenuPane.RowMarkup(FolderText.RowMarkup(tree.Visible[r]), r == _cursor)));
        }

        if (_shown < count)
        {
            lines.Add(new Markup(Theme.DimMarkup(MenuPane.NoPointer + MenuPane.MoreHint)));
        }

        _pane.ShowOverlay(new Rows(lines), FolderText.Hint, close: true);
    }
}
