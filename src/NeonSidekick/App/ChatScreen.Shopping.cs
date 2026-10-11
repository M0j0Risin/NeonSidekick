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

    /// <summary>Whether the shopping group is offered: <c>Shopping tools</c> on.</summary>
    public static bool ShoppingOffered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.ShoppingTools;
    }
}
