using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_clear(all?)</c> (2026-10-11): the ticked lines taken off the shopping list (a trip done); <c>all: true</c> empties the whole
/// list, and only after the user's yes on the pane (<paramref name="confirm"/>; null where nothing can ask, headless, and the whole list is then
/// refused). Every clear is audited.
/// </summary>
public sealed class ShoppingClearTool(ShoppingList list, Func<string, CancellationToken, Task<bool?>>? confirm) : ShoppingTool(list)
{
    public const string ToolName = "shopping_clear";
    public const string AllArgument = "all";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "all": { "type": "boolean", "description": "true empties the whole list (the user is asked first); false (the default) removes only the ticked lines." }
          }
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description => "Removes the ticked lines from the user's shopping list once they are bought, or with all the whole list after the user agrees.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryReadBoolean(arguments, AllArgument, out bool? all, out string raw))
        {
            return "Error: all must be true or false, not " + raw + ".";
        }

        if (all is true)
        {
            int count = List.Store.Items().Count;
            if (count == 0)
            {
                return ShoppingText.Cleared(0, all: true);
            }

            if (confirm is null)
            {
                return ShoppingText.ClearAllNoPane;
            }

            switch (await confirm(ShoppingText.ClearAllQuestion(count), cancellationToken).ConfigureAwait(false))
            {
                case null:
                    return ShoppingText.ClearAllNoPane;
                case false:
                    return ShoppingText.ClearAllDeclined;
            }
        }

        return List.Clear(all is true) is { } cleared ? ShoppingText.Cleared(cleared, all is true) : "Error: " + ShoppingText.WriteFailed;
    }
}
