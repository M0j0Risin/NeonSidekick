using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_check(items, checked?)</c> (2026-10-11): lines ticked as bought (or unticked with <c>checked: false</c>), each named as
/// <see cref="ShoppingList.Find"/> reads it. A name matching several lines ticks them all when they are one product folded alike, and
/// is refused otherwise.
/// </summary>
public sealed class ShoppingCheckTool(ShoppingList list) : ShoppingTool(list)
{
    public const string ToolName = "shopping_check";
    public const string ItemsArgument = "items";
    public const string CheckedArgument = "checked";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "items": { "type": "array", "items": { "type": "string" }, "description": "The lines to tick, by name or by their number on the list." },
            "checked": { "type": "boolean", "description": "false unticks them; true (the default) ticks them as bought." }
          },
          "required": ["items"]
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description => "Ticks lines of the user's shopping list as bought, or unticks them; ticked lines stay on the list until cleared.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (ItemsOf(arguments, ItemsArgument, out string? error) is not { } names)
        {
            return ValueTask.FromResult(error!);
        }

        if (!ToolArguments.TryReadBoolean(arguments, CheckedArgument, out bool? on, out string raw))
        {
            return ValueTask.FromResult("Error: checked must be true or false, not " + raw + ".");
        }

        return ValueTask.FromResult(Apply(List, names, item => List.Check(item, on ?? true) ? ShoppingText.Checked(item, on ?? true) : null));
    }

    /// <summary>
    /// <paramref name="act"/> on every line each of <paramref name="names"/> names: one line, or several folded alike (one product written
    /// twice); several different lines are refused for that name. One sentence per line; <c>Error: </c> leads a refusal. Shared with
    /// <c>shopping_remove</c>.
    /// </summary>
    public static string Apply(ShoppingList list, IReadOnlyList<string> names, Func<ShopItem, string?> act)
    {
        ArgumentNullException.ThrowIfNull(list);
        var sb = new StringBuilder();
        foreach (string name in names)
        {
            var found = list.Find(name);
            string answer;
            if (found.Count == 0)
            {
                answer = ShoppingText.NotOnList(name);
            }
            else if (found.Select(i => ShoppingPlacer.Key(i.Name)).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                answer = ShoppingText.Ambiguous(name, found);
            }
            else
            {
                answer = string.Join("\n", found.Select(item => act(item) ?? "Error: " + ShoppingText.WriteFailed));
            }

            sb.Append(sb.Length > 0 ? "\n" : "").Append(answer);
        }

        return sb.ToString();
    }
}
