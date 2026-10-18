using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.Web;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/browser list</c> (later on 2026-10-13, the user's ask: the Browser window's saved links on a pane like <c>/youtube list</c>'s): the
/// profile's saved links (<see cref="BrowserLinks"/>) on a <see cref="MenuPage"/>, name and address in uniform columns
/// (<see cref="BrowserLinkText.PaneRow"/>), typing filters (Shift for a letter a button has). Enter or a double-click opens the highlighted
/// one in the Browser window through the open the screen hands in, the pane staying open with its answer on the status line. The buttons
/// (<see cref="Buttons"/>, a click or the key): add (an address typed on the input row), name and address (each typed, the
/// <see cref="ShoppingMenu"/>'s rename; a blank name takes it off), remove (after a yes) and clear all (after a yes, then the pane closes
/// with its answer on the transcript, <see cref="YouTubeSavedMenu"/>'s). The order is the user's: <see cref="MenuPage.Moves"/> while
/// nothing is typed (Shift/Alt+↑↓, Shift/Alt+Home/End, silently; Delete removes). An empty list shows one dim row under the same
/// buttons, so add works on it, and the last link removed leaves the pane there. The list is read again after every act, the cursor on the
/// same link, and every change tells the window (its ☆), through <c>changed</c>.
/// </summary>
internal sealed class BrowserLinksMenu
{
    /// <summary>The page's buttons in the order drawn: add, name, address, remove, clear all. Pinned.</summary>
    public static readonly IReadOnlyList<MenuButton> Buttons =
    [
        new(BrowserLinkText.AddButton, BrowserLinkText.AddKey),
        new(BrowserLinkText.NameButton, BrowserLinkText.NameKey),
        new(BrowserLinkText.AddressButton, BrowserLinkText.AddressKey),
        new(BrowserLinkText.RemoveButton, BrowserLinkText.RemoveKey),
        new(QueueMenu.ClearAllButton, QueueMenu.ClearAllKey),
    ];

    private const int Add = 0;
    private const int Name = 1;
    private const int Address = 2;
    private const int Remove = 3;
    private const int ClearAll = 4;

    private const string ErrorPrefix = "Error: ";

    private readonly Func<BrowserLinks> _links;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly Func<BrowserLink, string>? _open;
    private readonly InputLine? _input;
    private readonly Action? _changed;
    private readonly TimeZoneInfo _zone;
    private readonly bool _window;

    /// <param name="links">The loaded profile's saved links, read when the pane opens and after every act.</param>
    /// <param name="transcript">Where the lines outside the pane go (clear all's, which closes it).</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="open">What Enter hands a link to: the screen's open in the Browser window, answering at once (never waiting for the visit), or in the default browser where there is no window (2026-10-17, the Mac pass); null: Enter says there is no Browser window.</param>
    /// <param name="input">The input row an address or a name is typed on; null: they say so.</param>
    /// <param name="changed">Called after every change: the open window reads its ☆ again.</param>
    /// <param name="zone">The zone the footer's date is shown in.</param>
    /// <param name="window">Whether there is a Browser window: false words the caption and the empty row without it (2026-10-17, the Mac pass).</param>
    public BrowserLinksMenu(Func<BrowserLinks> links, INoticeSink transcript, MenuPane pane, Func<BrowserLink, string>? open, InputLine? input = null, Action? changed = null, TimeZoneInfo? zone = null, bool window = true)
    {
        _links = links ?? throw new ArgumentNullException(nameof(links));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _open = open;
        _input = input;
        _changed = changed;
        _window = window;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>Shows the list until ESC.</summary>
    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        string? on = null;   // the link the cursor follows, by address
        int stood = 0;       // the row it stood on, for when that link is gone
        string filter = "";
        try
        {
            while (true)
            {
                var links = _links().List();
                bool empty = links.Count == 0;
                var shown = Enumerable.Range(0, links.Count).Where(i => BrowserLinkText.Matches(filter, links[i])).ToList();
                var columns = BrowserLinkText.Columns(links);
                List<string> rows = empty ? [Theme.DimMarkup(_window ? BrowserLinkText.EmptyRow : BrowserLinkText.EmptyRowNoWindow)]
                    : shown.Count == 0 ? [MenuFilter.NoMatchRow(filter)]
                    : shown.Select(i => Markup.Escape(BrowserLinkText.PaneRow(links[i], columns.Name, columns.Address))).ToList();
                int at = on is null ? -1 : shown.FindIndex(i => BrowserLinks.Key(links[i].Url) == BrowserLinks.Key(on));
                int cursor = at >= 0 ? at : Math.Clamp(stood, 0, Math.Max(0, rows.Count - 1));
                var page = new MenuPage(BrowserLinkText.PaneTitle, rows, MenuFilter.Hint(BrowserLinkText.PaneKeys, filter))
                {
                    Buttons = Buttons,
                    Filter = empty ? null : filter,
                    Caption = empty ? null : MenuFilter.CaptionOrNull(filter, shown.Count, links.Count) ?? BrowserLinkText.Caption(links.Count, _window),
                    Footer = (_, row) => !empty && row < shown.Count ? BrowserLinkText.Footer(links[shown[row]], _zone) : null,
                    Moves = !empty && filter.Length == 0,   // the order is the whole list's: no moves while a filter shows part of it
                    Removes = !empty && shown.Count > 0,     // Delete removes a filtered row too (2026-10-16, the user's ask)
                };
                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return;
                }

                if (pick.Filter is { } typed)
                {
                    filter = typed;
                    on = null;
                    stood = 0;
                    continue;
                }

                var link = !empty && pick.Row >= 0 && pick.Row < shown.Count ? links[shown[pick.Row]] : null;
                stood = Math.Max(0, pick.Row);
                on = link?.Url ?? on;
                if (pick.Button == Add)
                {
                    on = await AddAsync(page, Math.Max(0, pick.Row), cancellationToken).ConfigureAwait(false) ?? on;
                    continue;
                }

                if (pick.Button == ClearAll)
                {
                    if (empty)
                    {
                        continue;
                    }

                    if (await ConfirmAsync(BrowserLinkText.ClearPrompt(links.Count), cancellationToken).ConfigureAwait(false))
                    {
                        var outcome = _links().Clear(out int cleared);
                        _changed?.Invoke();
                        Tell(BrowserLinkText.Refusal(outcome) ?? (cleared == 0 ? BrowserLinkText.NoneSavedFor(_window) : BrowserLinkText.Cleared(cleared)));
                        return;
                    }

                    _pane.Notice(ChatScreen.KeptNotice);
                    continue;
                }

                if (link is null)
                {
                    continue;   // the empty list's row, the no-match row
                }

                if (pick.Move != 0)
                {
                    // A move: silent, the row going with the cursor.
                    if (BrowserLinkText.Refusal(_links().Move(link.Url, pick.Move)) is { } refused)
                    {
                        Say(refused);
                    }

                    _changed?.Invoke();
                }
                else if (pick.Button == Name)
                {
                    if (await TypeAsync(page, pick.Row, link.Name ?? "", allowEmpty: true, cancellationToken).ConfigureAwait(false) is { } named && named != (link.Name ?? ""))
                    {
                        var outcome = _links().Rename(link.Url, named);
                        Say(BrowserLinkText.Refusal(outcome, link.Url) ?? BrowserLinkText.Renamed(link, BrowserLinks.CleanName(named)));
                        _changed?.Invoke();
                    }
                }
                else if (pick.Button == Address)
                {
                    if (await TypeAsync(page, pick.Row, link.Url, allowEmpty: false, cancellationToken).ConfigureAwait(false) is { Length: > 0 } typedAddress)
                    {
                        if (WebFetcher.ParseUrl(typedAddress) is not { } url)
                        {
                            Say(ErrorPrefix + BrowserLinkText.NotWeb);
                        }
                        else if (BrowserLinks.Key(url.AbsoluteUri) != BrowserLinks.Key(link.Url))
                        {
                            var outcome = _links().Readdress(link.Url, url.AbsoluteUri);
                            Say(BrowserLinkText.Refusal(outcome, link.Url) ?? BrowserLinkText.Readdressed(link, url.AbsoluteUri));
                            on = outcome == BrowserLinkOutcome.Done ? url.AbsoluteUri : on;
                            _changed?.Invoke();
                        }
                    }
                }
                else if (pick.Button == Remove || pick.Remove)
                {
                    if (await ConfirmAsync(BrowserLinkText.RemovePrompt(link), cancellationToken).ConfigureAwait(false))
                    {
                        var outcome = _links().Remove(link.Url);
                        Say(BrowserLinkText.Refusal(outcome, link.Url) ?? BrowserLinkText.Removed(link));
                        on = null;   // the row it stood on: the link after it, or the one before at the end
                        _changed?.Invoke();
                    }
                    else
                    {
                        _pane.Notice(ChatScreen.KeptNotice);
                    }
                }
                else
                {
                    Say(_open is null ? ErrorPrefix + BotCheckText.BrowserNotHere : _open(link));
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    // An address typed on the input row and saved; the link the cursor goes to (the new one, or the one saved already), or null.
    private async Task<string?> AddAsync(MenuPage page, int row, CancellationToken cancellationToken)
    {
        if (await TypeAsync(page, row, "", allowEmpty: false, cancellationToken).ConfigureAwait(false) is not { Length: > 0 } typed)
        {
            return null;
        }

        if (WebFetcher.ParseUrl(typed) is not { } url)
        {
            Say(ErrorPrefix + BrowserLinkText.NotWeb);
            return null;
        }

        var outcome = _links().Add(url.AbsoluteUri, null, out var saved);
        Say(BrowserLinkText.Refusal(outcome) ?? (outcome == BrowserLinkOutcome.AlreadySaved ? BrowserLinkText.AlreadySaved(saved!) : BrowserLinkText.Saved(saved!)));
        _changed?.Invoke();
        return saved?.Url;
    }

    /// <summary>Text typed on the input row under the list (<paramref name="initial"/> to start from); null on ESC, or with no input row (said so).</summary>
    private async Task<string?> TypeAsync(MenuPage page, int row, string initial, bool allowEmpty, CancellationToken cancellationToken)
    {
        if (_input is null)
        {
            _pane.Error(BrowserLinkText.NeedsInput);
            return null;
        }

        var typed = await _pane.EditAsync(page with { Hint = SettingsMenu.EditKeys }, row, _input, initial, allowEmpty, cancellationToken).ConfigureAwait(false);
        return typed is InputResult.Submitted { Text: var text } ? text.Trim() : null;
    }

    /// <summary>An answer on the transcript, for one the pane closes after: an error as one, anything else a notice.</summary>
    private void Tell(string result)
    {
        if (result.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            _transcript.Error(result[ErrorPrefix.Length..]);
        }
        else
        {
            _transcript.Notice(result);
        }
    }

    /// <summary>An answer on the pane's status line: an error as one, anything else a notice.</summary>
    private void Say(string result)
    {
        if (result.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            _pane.Error(result[ErrorPrefix.Length..]);
        }
        else
        {
            _pane.Notice(result);
        }
    }

    /// <summary>The yes/no page kept under the list (<see cref="SettingsMenu.ConfirmAsync"/>'s rows, keys and hotkeys, the cursor on No): true for Enter on Yes alone.</summary>
    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        var page = new MenuPage(question, SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys) { Hotkeys = SettingsMenu.ConfirmHotkeys };
        var picked = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
        return picked is { Row: 1 };
    }
}
