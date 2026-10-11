using NeonSidekick.Shopping;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── The shopping stores' manager (2026-10-11, the user's ask) ─────────────

internal sealed partial class SettingsMenu
{
    /// <summary>The shopping list's operations, set by the screen: the store manager's stores (null leaves the row saying so).</summary>
    public ShoppingList? Shopping { get; set; }

    /// <summary>The stores' reference documents, set by the screen: the manager's reindex (null: no reindex).</summary>
    public ShoppingReference? ShoppingReference { get; set; }

    /// <summary>The manager's buttons, in the order drawn, each with its glyph. Pinned.</summary>
    public static readonly IReadOnlyList<MenuButton> StoreButtons =
    [
        new(ShoppingText.RenameStoreButton, ShoppingText.RenameStoreKey),
        new(ShoppingText.RemoveStoreButton, ShoppingText.RemoveStoreKey),
        new(ShoppingText.FolderButton, ShoppingText.FolderKey),
        new(ShoppingText.ReindexButton, ShoppingText.ReindexKey),
    ];

    private const int RenameStoreIndex = 0;
    private const int RemoveStoreIndex = 1;
    private const int FolderIndex = 2;
    private const int ReindexIndex = 3;

    /// <summary>
    /// The shopping stores' manager (2026-10-11, the user's ask: stores added, renamed and removed somewhere other than a typed line):
    /// the <c>Shopping stores</c> row of <c>/tools</c> › Core › Shopping and the <c>/shopping</c> pane's <c>⇄ store</c> both open it.
    /// The first row adds a store (its name typed on the input row; its reference folder made); Enter on a store makes it the one the list
    /// walks through. The buttons rename the highlighted store (its folders follow the name, <see cref="ShoppingList.RenameStore"/>), remove it after a yes (its layout and cached
    /// pictures go, its reference documents stay), open its reference folder, and read its documents again. The band under a store says
    /// what its layout holds. Without the pane there are no buttons: the add row and Enter still work. True when anything changed.
    /// </summary>
    public async Task<bool> ManageShoppingStoresAsync(CancellationToken cancellationToken)
    {
        if (Shopping is not { } list || !list.Store.Available)
        {
            Sink.Error(ShoppingText.Unavailable["Error: ".Length..]);
            return false;
        }

        bool changed = false;
        long? on = list.Store.ActiveStore()?.Id;
        while (true)
        {
            var store = list.Store;
            var stores = store.Stores();
            var active = store.ActiveStore();
            IReadOnlyList<string> rows = [Markup.Escape(ShoppingText.AddStoreRow), .. stores.Select(s => Markup.Escape(ShoppingText.StoreRow(s, s.Id == active?.Id)))];
            int cursor = on is { } id && stores.ToList().FindIndex(s => s.Id == id) is >= 0 and var at ? at + 1 : 0;
            var page = new MenuPage(Crumb(ShoppingText.StoresTitle), rows, ShoppingText.StoresKeys)
            {
                Footer = (_, row) => row > 0 && row <= stores.Count ? StoreFooter(store, stores[row - 1]) : null,
            };
            if (await PickChecklistAsync(page, cursor, cancellationToken, StoreButtons).ConfigureAwait(false) is not { } pick)
            {
                return changed;
            }

            var shop = pick.Row > 0 && pick.Row <= stores.Count ? stores[pick.Row - 1] : null;
            on = shop?.Id ?? on;
            if (pick.Button < 0 && shop is null)
            {
                // The add row: a name typed under the list.
                if (await TypeStoreNameAsync(page, 0, "", cancellationToken).ConfigureAwait(false) is { } name)
                {
                    string added = list.AddStore(name);
                    Say(added);
                    changed |= !added.StartsWith("Error: ", StringComparison.Ordinal);
                    on = list.Store.FindStore(name)?.Id ?? on;
                }

                continue;
            }

            if (shop is null)
            {
                continue;   // a button on the add row: nothing to act on
            }

            switch (pick.Button)
            {
                case < 0:
                    Say(list.UseStore(shop));
                    changed = true;
                    break;
                case RenameStoreIndex:
                    if (await TypeStoreNameAsync(page, pick.Row, shop.Name, cancellationToken).ConfigureAwait(false) is { } renamed && renamed != shop.Name)
                    {
                        string answer = list.RenameStore(shop, renamed);
                        Say(answer);
                        changed |= !answer.StartsWith("Error: ", StringComparison.Ordinal);
                    }

                    break;
                case RemoveStoreIndex:
                    if (await ConfirmAsync(ShoppingText.RemoveStorePrompt(shop, store.Placements(shop.Id).Count), cancellationToken).ConfigureAwait(false))
                    {
                        string answer = list.RemoveStore(shop);
                        Say(answer);
                        changed |= !answer.StartsWith("Error: ", StringComparison.Ordinal);
                        on = null;
                    }
                    else
                    {
                        Sink.Notice(ChatScreen.KeptNotice);
                    }

                    break;
                case FolderIndex:
                    OpenStoreFolder(store.ReferenceFolder(shop));
                    break;
                case ReindexIndex:
                    if (ShoppingReference is not { } reference)
                    {
                        Sink.Error(ShoppingText.NoBrowserForPdf);
                        break;
                    }

                    Sink.Notice(ShoppingText.Reading(shop));
                    var summary = await reference.RefreshAsync(shop, force: true, cancellationToken).ConfigureAwait(false);
                    Say(ShoppingText.ReferenceRead(shop, summary));
                    break;
            }
        }
    }

    /// <summary>
    /// The shopping list's pane from <c>/tools</c> › Core › Shopping's <c>Shopping list</c> row (2026-10-11, the user's ask): the bare
    /// <c>/shopping</c>'s checklist (<see cref="ShoppingMenu"/>), its store button this manager, ESC back to the row. Without the pane,
    /// the command that shows the list.
    /// </summary>
    public async Task ManageShoppingListAsync(CancellationToken cancellationToken)
    {
        if (Shopping is not { } list || !list.Store.Available)
        {
            Sink.Error(ShoppingText.Unavailable["Error: ".Length..]);
            return;
        }

        if (!_pane.Enabled)
        {
            Sink.Notice(ShoppingText.ListNeedsPane);
            return;
        }

        await new ShoppingMenu(list, _pane, ManageShoppingStoresAsync, closeWhenDone: false, input: _input).ShowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The band under a store's row: <see cref="ShoppingText.StoreFooter"/> over its layout's counts and its documents.</summary>
    private static MenuFooter StoreFooter(ShoppingStore store, ShopStore shop)
    {
        var (text, folder) = ShoppingText.StoreFooter(store.Locations(shop.Id).Count, store.Sections(shop.Id).Count, store.Placements(shop.Id).Count, store.RefDocs(shop.Id).Count, store.ReferenceFolder(shop));
        return new MenuFooter(text, folder);
    }

    /// <summary>A store's name typed on the input row (the prompt line without the pane), <paramref name="initial"/> to start from; null on ESC or a blank line.</summary>
    private async Task<string?> TypeStoreNameAsync(MenuPage page, int row, string initial, CancellationToken cancellationToken)
    {
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Hint = EditKeys }, row, _input, initial, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(ShoppingText.StoresTitle, EditKeys));
            result = await _input.ReadAsync(initial, remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true).ConfigureAwait(false);
        }

        return result is InputResult.Submitted submitted && submitted.Text.Trim().Length > 0 ? submitted.Text.Trim() : null;
    }

    /// <summary>A store's reference folder made and opened in the file manager (the screen's shell-open, <c>/explore</c>'s).</summary>
    private void OpenStoreFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            if (_openFile is null)
            {
                Sink.Error(SqlEditFailedError(folder, "no file manager to open it in"));
                return;
            }

            _openFile(folder);
            Sink.Notice(ShoppingText.FolderOpened(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Sink.Error(SqlEditFailedError(folder, ex.Message));
        }
    }

    /// <summary>An answer on the status line: an error as one, anything else a notice.</summary>
    private void Say(string result)
    {
        const string Prefix = "Error: ";
        if (result.StartsWith(Prefix, StringComparison.Ordinal))
        {
            Sink.Error(result[Prefix.Length..]);
        }
        else
        {
            Sink.Notice(result);
        }
    }
}
