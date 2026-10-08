using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// The transcript's find (2026-10-07, the user's ask, phase 5 of the UI round): <c>/find [words]</c> or Ctrl+Shift+F at the idle
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
    public const string EmptyHint = "type = find · ESC = done";

    /// <summary>The find row's label. Pinned.</summary>
    public const string Label = "find:";

    /// <summary>
    /// The hint row: which match of how many the view is on, counted from the newest (1 is the one nearest the bottom), and the
    /// keys; with none, the way out. Pinned.
    /// </summary>
    public static string Hint(int ordinal, int count) =>
        count > 0
            ? $"{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)} of {count.ToString(System.Globalization.CultureInfo.InvariantCulture)} · Enter = older · {InfoPane.FindBack} = newer · PgUp/PgDn = scroll · ESC = done"
            : "no match · Backspace = erase · ESC = done";

    /// <summary>The find row: the label, the text and a bar where the next character goes.</summary>
    public static string RowMarkup(string find) =>
        $"[{Theme.Label.ToMarkup()}]{Label}[/] {Markup.Escape(find)}[{Theme.Accent.ToMarkup()}]▏[/]";

    private readonly ScreenPane _pane;
    private readonly KeySource _keys;

    public TranscriptFind(ScreenPane pane, KeySource keys)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
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

        // The places again over the rows as they are now, the view on the newest (the one nearest the bottom). The typed /find
        // line that opened it is left out: it holds the words, and it is never what is looked for.
        void Search()
        {
            var rows = _pane.TranscriptRows();
            int own = OwnLine(rows);
            hits = TextFind.Matches(rows, find);
            hits.RemoveAll(found => found.Line >= own);
            hit = hits.Count - 1;
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

        void Show()
        {
            var at = hit >= 0 && hit < hits.Count ? hits[hit] : new TextFind.Hit(-1, -1);
            _pane.ShowOverlay(new Markup(RowMarkup(find)), find.Length == 0 ? EmptyHint : Hint(hits.Count - hit, hits.Count), close: true);
            _pane.ShowFind(find, at.Line, at.Start);
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

                if (input is InputEvent.Wheel wheel)
                {
                    _pane.ScrollWheel(wheel.Notches);
                    continue;
                }

                if (input is InputEvent.Click { Button: MouseButton.Left } click && _pane.TryHitClose(click.X, click.Y))
                {
                    return;
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

    /// <summary>
    /// Where the typed <c>/find</c> line that opened the find starts — the last row with text, when it is one — else the row
    /// count: the rows from there on are not searched. Ctrl+Shift+F writes no line, so nothing is left out then.
    /// </summary>
    internal static int OwnLine(IReadOnlyList<IReadOnlyList<Spectre.Console.Rendering.Segment>> rows)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            string text = TextFind.LineText(rows[i]);
            if (text.Trim().Length == 0)
            {
                continue;
            }

            return text.StartsWith(InputLine.PromptGlyph + "/find", StringComparison.Ordinal) ? i : rows.Count;
        }

        return rows.Count;
    }

    /// <summary>The longest find (the info pane's).</summary>
    private const int TextFindMax = InfoPane.FindMaxLength;
}
