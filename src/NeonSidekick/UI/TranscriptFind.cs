using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// The transcript's find (2026-10-07, the user's ask, phase 5 of the UI round): <c>/find [words]</c> or Ctrl+F (Ctrl+Shift+F until 2026-10-18) at the idle
/// line opens a find row over the input row, and the transcript scrolls to what it finds — every place marked, the one it is on
/// in the selection's colours (<see cref="ScreenPane.ShowFind"/>). Typing narrows it, newest first: the view goes to the match
/// nearest the bottom, Enter or F3 to the next one up (older), Shift with either back down (newer), both wrapping; PgUp/PgDn and
/// the wheel scroll as on the line. A fold hides what it holds, so the first Enter after the text changes (and <c>/find</c>'s
/// words at once) opens every folded tool run, code block, diff and thinking block that holds the text
/// (<see cref="ScreenPane.UnfoldMatching"/>); the find folds them again as it ends. ESC (or Ctrl+C) ends it, the transcript back
/// at the bottom and the input row back. The rows are what the screen shows, so a word the window wraps across two rows is not
/// found, as in the info pane's find (<see cref="TextFind"/>).
/// </summary>
public sealed class TranscriptFind
{
    /// <summary>The hint row while nothing is typed. Pinned.</summary>
    public const string EmptyHint = "type = find · Esc = done";

    /// <summary>The find row's label. Pinned.</summary>
    public const string Label = "find:";

    /// <summary>
    /// The hint row: which match of how many the view is on, counted from the newest (1 is the one nearest the bottom), and the
    /// keys; with none, the way out. Pinned.
    /// </summary>
    public static string Hint(int ordinal, int count) =>
        count > 0
            ? $"{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)} of {count.ToString(System.Globalization.CultureInfo.InvariantCulture)} · Enter = older · {InfoPane.FindBack} = newer · PgUp/PgDn = scroll · Esc = done"
            : "no match · Backspace = erase · Esc = done";

    /// <summary>The find row: the label, the text and a bar where the next character goes.</summary>
    public static string RowMarkup(string find) =>
        $"[{Theme.Label.ToMarkup()}]{Label}[/] {Markup.Escape(find)}[{Theme.Accent.ToMarkup()}]▏[/]";

    private readonly ScreenPane _pane;
    private readonly KeySource _keys;
    private readonly Func<string?>? _clipboard;
    private readonly DoubleClick _clicks;

    /// <param name="clipboard">The clipboard's text a right-click pastes into the find (2026-10-14, the user's call); null = a right-click is nothing.</param>
    public TranscriptFind(ScreenPane pane, KeySource keys, Func<string?>? clipboard = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _clipboard = clipboard;
        _clicks = new DoubleClick(pane.Time);
    }

    /// <summary>
    /// The find, from <paramref name="initial"/> (its folds opened at once when it is not empty), until ESC, the token or the end
    /// of input; the transcript at the bottom again, the folds it opened shut and the input row back on every path. Nothing
    /// without the pane.
    /// </summary>
    public async Task RunAsync(string initial, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (!_pane.Enabled)
        {
            return;
        }

        string find = initial.Length > TextFindMax ? initial[..TextFindMax] : initial;
        var opened = new List<Scrollback.Unfolded>();
        string unfoldedFor = "";
        List<TextFind.Hit> hits = [];
        int hit = -1;
        int laidOut = -1;

        // The places again over the rows as they are now, the view on the newest (the one nearest the bottom). The typed /find
        // line that opened it is left out: it holds the words, and it is never what is looked for.
        // The rows, their layout and the pane's place are read together (2026-10-29, the resize review); the place comes back for Nearest.
        TextFind.Hit Search()
        {
            var rows = _pane.TranscriptRows(out laidOut, out var place);
            int own = OwnLine(rows, _pane.UserGlyph());
            hits = TextFind.Matches(rows, find);
            hits.RemoveAll(found => found.Line >= own);
            hit = hits.Count - 1;
            return place;
        }

        // The folds holding the text opened once for each text; true when any did (the rows moved).
        bool Unfold()
        {
            if (find.Length == 0 || find == unfoldedFor)
            {
                return false;
            }

            unfoldedFor = find;
            var now = _pane.UnfoldMatching(find);
            opened.AddRange(now);
            return now.Count > 0;
        }

        // The window can change width between the rows read and the find shown (2026-10-29, the resize review): the pane then refuses the
        // stale row, and the places are found again near the place it moved its own find to — a few tries, then the pane's place stays.
        void Show()
        {
            for (int tries = 0; ; tries++)
            {
                var at = hit >= 0 && hit < hits.Count ? hits[hit] : new TextFind.Hit(-1, -1);
                _pane.ShowOverlay(new Markup(RowMarkup(find)), find.Length == 0 ? EmptyHint : Hint(hits.Count - hit, hits.Count), close: true);
                if (_pane.ShowFind(find, at.Line, at.Start, laidOut) || tries == 2)
                {
                    return;
                }

                hit = Nearest(hits, Search());
            }
        }

        // The rows moved since the last search (a resize, 2026-10-29): found again, the view on the one the pane moved its find to.
        void Again()
        {
            var place = Search();
            hit = Nearest(hits, place);
        }

        try
        {
            Unfold();
            Search();
            Show();
            while (true)
            {
                var input = await _keys.ReadPaneInputAsync(_pane, cancellationToken).ConfigureAwait(false);
                if (input is null)
                {
                    return;
                }

                if (_pane.LayoutVersion != laidOut && !SearchesAnyway(input))
                {
                    // The window changed width since the last search (2026-10-29): the rows moved, so the places are found again and the
                    // view stays on the one the pane moved its find to — Enter and Shift+Enter walk on from what is shown — and the
                    // "n of m" hint is drawn afresh (the resize review). Typing, Backspace and a paste search on their own.
                    Again();
                    Show();
                }

                if (input is InputEvent.Wheel wheel)
                {
                    _pane.ScrollWheel(wheel.Notches);
                    continue;
                }

                if (input is InputEvent.Click { Button: MouseButton.Left } click && _pane.TryHitClose(click.X, click.Y))
                {
                    return;
                }

                if (input is InputEvent.Click { Button: MouseButton.Left } outside && _pane.TryHitOutside(outside.X, outside.Y))
                {
                    // Two off the pane within the interval close it, as every pane's do (2026-10-14, the user's ask: only the × did), on the
                    // same part, the dismiss kept for the screen's close-or-switch.
                    if (_clicks.Second(_pane.OutsideKey(outside.X, outside.Y)))
                    {
                        _pane.Dismiss(outside.X, outside.Y);
                        return;
                    }

                    continue;
                }

                if (input is InputEvent.Drag or InputEvent.Release)
                {
                    // A jiggle between the two presses of a double-click keeps the pair, as does the release between them.
                    continue;
                }

                _clicks.Reset();
                string? pastedText = input switch
                {
                    InputEvent.Paste paste => paste.Text,
                    InputEvent.Click { Button: MouseButton.Right } => _clipboard?.Invoke(),
                    _ => null,
                };
                if (pastedText is not null)
                {
                    // A paste, Ctrl+V's or a right-click's (2026-10-14, the user's call: neither landed): its first line onto the find, the
                    // typed find's cap (MenuFilter.Pasted, the menus' filter's rule).
                    if (MenuFilter.Pasted(find, pastedText) is { } grown)
                    {
                        find = grown;
                        Search();
                        Show();
                    }

                    continue;
                }

                if (input is not InputEvent.Key { Info: var k })
                {
                    continue;
                }

                if (Keys.IsCancel(k) || Keys.IsInterrupt(k))
                {
                    return;
                }

                if (k.Key is ConsoleKey.PageUp or ConsoleKey.PageDown)
                {
                    _pane.ScrollPage(k.Key == ConsoleKey.PageUp ? -1 : 1);
                    continue;
                }

                if (InfoPane.FindStep(k) is int older)
                {
                    if (Unfold())
                    {
                        // The rows moved under the folds just opened: counted again, from the newest.
                        Search();
                    }
                    else if (hits.Count > 0)
                    {
                        hit = (hit - older + hits.Count) % hits.Count;
                    }
                }
                else if (k.Key == ConsoleKey.Backspace)
                {
                    if (find.Length == 0)
                    {
                        continue;
                    }

                    find = find[..^1];
                    Search();
                }
                else if (InfoPane.FindChar(k) is char typed)
                {
                    var grown = new System.Text.StringBuilder(find).Append(typed);
                    while (_keys.TakeQueued(e => e is InputEvent.Key { Info: var q } && InfoPane.FindChar(q) is not null) is InputEvent.Key { Info: var more })
                    {
                        grown.Append(more.KeyChar);
                    }

                    find = grown.Length > TextFindMax ? grown.ToString(0, TextFindMax) : grown.ToString();
                    Search();
                }
                else
                {
                    continue;
                }

                Show();
            }
        }
        catch (InvalidOperationException)
        {
            // No keyboard: the read ran dry, the find ends.
        }
        finally
        {
            _pane.CloseOverlay();
            _pane.Refold(opened);
            _pane.EndFind();
        }
    }

    /// <summary>The input searches the rows again on its own (2026-10-29, the resize review): typing, Backspace, a paste or a right-click's.</summary>
    private static bool SearchesAnyway(InputEvent input) => input switch
    {
        InputEvent.Paste => true,
        InputEvent.Click { Button: MouseButton.Right } => true,
        InputEvent.Key { Info: var k } => k.Key == ConsoleKey.Backspace || InfoPane.FindChar(k) is not null,
        _ => false,
    };

    /// <summary>
    /// Where the typed <c>/find</c> line that opened the find starts — the last row with text, when it is one — else the row
    /// count: the rows from there on are not searched. Ctrl+F writes no line, so nothing is left out then. <paramref name="glyph"/>
    /// is what a sent line opens with (<see cref="ScreenPane.UserGlyph"/>, the user avatar since 2026-10-12).
    /// </summary>
    internal static int OwnLine(IReadOnlyList<IReadOnlyList<Spectre.Console.Rendering.Segment>> rows, string glyph = InputLine.PromptGlyph)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            string text = TextFind.LineText(rows[i]);
            if (text.Trim().Length == 0)
            {
                continue;
            }

            return text.StartsWith(glyph + "/find", StringComparison.Ordinal) ? i : rows.Count;
        }

        return rows.Count;
    }

    /// <summary>
    /// The match at <paramref name="place"/> (2026-10-29, the pane's find after a re-layout, <see cref="ScreenPane.FindPlace"/>), else the
    /// last one before it, else the first; the newest with no place. −1 with no matches.
    /// </summary>
    internal static int Nearest(IReadOnlyList<TextFind.Hit> hits, TextFind.Hit place)
    {
        if (hits.Count == 0 || place.Line < 0)
        {
            return hits.Count - 1;
        }

        int nearest = 0;
        for (int i = 0; i < hits.Count; i++)
        {
            var found = hits[i];
            if (found.Line < place.Line || (found.Line == place.Line && found.Start <= place.Start))
            {
                nearest = i;
            }
        }

        return nearest;
    }

    /// <summary>The longest find (the info pane's).</summary>
    private const int TextFindMax = InfoPane.FindMaxLength;
}
