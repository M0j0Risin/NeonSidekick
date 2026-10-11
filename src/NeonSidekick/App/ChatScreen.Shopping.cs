using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Shopping;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── The shopping list (2026-10-11, the user's ask) ─────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The shopping list's operations over the loaded profile's store (<see cref="_shoppingStore"/>), shared by the tools and <c>/shopping</c>.</summary>
    private readonly ShoppingList _shopping;

    /// <summary>The shopping tools, built once over <see cref="_shopping"/>; offered while <c>Shopping tools</c> is on.</summary>
    private readonly IReadOnlyList<AIFunction> _shoppingTools;

    /// <summary>The loaded profile's shopping store, opened again when the profile moves.</summary>
    private readonly ShoppingStoreHolder _shoppingStore;

    /// <summary>
    /// The shopping tools (2026-10-11): the list, add, check, remove and clear, then the store's facts — locate, place, the stores and
    /// their layouts. <paramref name="confirm"/> asks before <c>shopping_clear</c> empties the whole list; null where nothing can ask
    /// (headless), and the whole list is then refused. Shared with headless.
    /// </summary>
    public static IReadOnlyList<AIFunction> ShoppingTools(ShoppingList list, Func<string, CancellationToken, Task<bool?>>? confirm) =>
    [
        new ShoppingListTool(list),
        new ShoppingAddTool(list),
        new ShoppingCheckTool(list),
        new ShoppingRemoveTool(list),
        new ShoppingClearTool(list, confirm),
        new ShoppingLocateTool(list),
        new ShoppingPlaceTool(list),
        new ShoppingStoresTool(list),
        new ShoppingLayoutTool(list),
    ];

    /// <summary>Closes the shopping store at the screen's end.</summary>
    private void DisposeShopping() => _shoppingStore.Dispose();

    /// <summary>
    /// The model's whole-list clear's question, on the turn task (2026-10-11): the yes/no pane through the watcher —
    /// <see cref="ConfirmDockerAsync"/>'s way, the cursor on No, ESC a no. Null (never asked) without the pane or a watcher to run it.
    /// </summary>
    private async Task<bool?> ConfirmShoppingAsync(string question, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        var paneToken = _paneClose?.Token ?? CancellationToken.None;
        bool? yes = null;
        var pending = _keys.RequestPaneAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(paneToken, turnToken);
            try
            {
                yes = await _menu.ConfirmAsync(question, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !linked.IsCancellationRequested)
            {
                DiagnosticLog.Error(ScreenPane.Category, "The shopping confirm pane failed: " + Llm.Assistant.Explain(ex), ex);
            }
        });
        try
        {
            await pending.WaitAsync(turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
        {
            // No watcher to run the pane: never asked.
            return null;
        }

        return yes ?? false;
    }

    /// <summary>
    /// <c>/shopping</c> (2026-10-11): the bare word opens the list's pane (<see cref="ShoppingMenu"/>) where menus open; every verb is
    /// <see cref="ShoppingCommandRunner"/>'s, its lines through <paramref name="sink"/> (a line led by <c>Error: </c> as an error). The
    /// user's own hand: neither <c>Shopping tools</c> nor plan mode judges it. The whole list's clear and a store's removal ask first;
    /// <c>print</c> goes through <c>/print</c>'s service under the spinner.
    /// </summary>
    private async Task HandleShoppingAsync(string args, INoticeSink sink, bool spinner, CancellationToken cancellationToken)
    {
        var line = ShoppingCommand.Parse(args);
        if (line.Verb == ShoppingVerb.Pane && line.Error is null && _pane.Enabled && _shopping.Store.Available)
        {
            await new ShoppingMenu(_shopping, _menuPane).ShowAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Func<string, string, CancellationToken, Task<string>>? print = Printing.PrintText.Supported
            ? (markdown, title, token) =>
            {
                var run = () => _print.RunAsync(new Printing.PrintRequest(null, Markdown: markdown, Title: title), token);
                return spinner ? _transcript.WithSpinnerAsync(Printing.PrintText.Working, run) : run();
            }
            : null;
        foreach (string result in await ShoppingCommandRunner.RunAsync(_shopping, line, _files, ConfirmAsync, print, cancellationToken).ConfigureAwait(false))
        {
            Show(result, sink);
        }
    }

    /// <summary><c>/shopping</c>'s argument list (2026-10-11): <see cref="ShoppingCommand.Complete"/> over the stores and the list's lines as they are.</summary>
    private IReadOnlyList<CompletionItem> ShoppingChoices(string argText)
    {
        var store = _shopping.Store;
        return store.Available
            ? ShoppingCommand.Complete(argText, store.Stores().Select(s => s.Name).ToList(), store.Items().Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            : ShoppingCommand.Complete(argText, [], []);
    }

    /// <summary>Whether the shopping group is offered: <c>Shopping tools</c> on.</summary>
    public static bool ShoppingOffered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.ShoppingTools;
    }
}
