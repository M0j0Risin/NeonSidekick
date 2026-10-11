using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The shopping tools' base (2026-10-11, the user's ask: the shopping list a feature of its own): the list's operations
/// (<see cref="ShoppingList"/>, the profile's store read at every call), the refusal while the file cannot be opened, and the store an
/// argument names (the active one when it names none). Each tool answers in <see cref="ShoppingText"/>'s words; <c>Error: </c> leads a refusal.
/// </summary>
public abstract class ShoppingTool : AIFunction
{
    public const string StoreArgument = "store";

    protected ShoppingTool(ShoppingList list) => List = list ?? throw new ArgumentNullException(nameof(list));

    protected ShoppingList List { get; }

    /// <summary>The <c>store</c> argument's schema line, shared.</summary>
    protected const string StoreProperty = "\"store\": { \"type\": \"string\", \"description\": \"A store's name; leave it out for the active store.\" }";

    protected sealed override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!List.Store.Available)
        {
            return ShoppingText.Unavailable;
        }

        // The store is SQLite under a lock: a call is quick, but it runs off the turn's thread like the session tool's reads.
        return await Task.Run(() => RunAsync(arguments, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The call, once the file is open.</summary>
    protected abstract ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken);

    /// <summary>The store the <c>store</c> argument names, else the active one; null with <paramref name="error"/> set when there is none to use.</summary>
    protected ShopStore? StoreOf(AIFunctionArguments arguments, out string? error)
    {
        var shop = List.StoreFor(ToolArguments.ReadString(arguments, StoreArgument), out error);
        if (shop is null && error is null)
        {
            error = ShoppingText.NoStore;
        }

        return shop;
    }

    /// <summary>The <c>items</c> argument's names (blank ones dropped); null with <paramref name="error"/> set when it is no list.</summary>
    protected static IReadOnlyList<string>? ItemsOf(AIFunctionArguments arguments, string name, out string? error)
    {
        error = null;
        if (!ToolArguments.TryReadStringList(arguments, name, out var values, out string raw))
        {
            error = "Error: " + name + " must be a list of names, not " + raw + ".";
            return null;
        }

        var names = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (names.Count == 0)
        {
            error = "Error: " + ShoppingText.NoItems + ".";
            return null;
        }

        return names;
    }
}
