using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_todo(action, item?, list?)</c> (2026-09-28): Home Assistant's to-do lists — the shopping list — read, added to,
/// ticked off and pruned. With one list in the house it may be left out. Listing is a read and runs under every policy.
/// </summary>
public sealed class HaTodoTool : HaTool
{
    public const string ToolName = "ha_todo";
    public const string ActionArgument = "action";
    public const string ItemArgument = "item";
    public const string ListArgument = "list";

    /// <summary>The actions and the <c>todo</c> service each calls.</summary>
    public static readonly IReadOnlyDictionary<string, string> Actions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["list"] = "get_items",
        ["add"] = "add_item",
        ["complete"] = "update_item",
        ["remove"] = "remove_item",
    };

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "add", "complete", "remove"], "description": "list the items, add one, mark one complete, or remove one." },
            "item": { "type": "string", "description": "The item's text (add), or the item's name as listed (complete, remove)." },
            "list": { "type": "string", "description": "The to-do list's name or id; leave it out when there is only one." }
          },
          "required": ["action"]
        }
        """);

    public HaTodoTool(HaSession ha, Func<string, CancellationToken, Task<bool?>>? confirm) : base(ha, confirm)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Reads and edits a Home Assistant to-do list such as the shopping list: list the items, add one, mark one complete, remove one.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string action = Optional(arguments, ActionArgument)?.ToLowerInvariant() ?? "";
        if (!Actions.TryGetValue(action, out string? service))
        {
            return action.Length == 0 ? HaText.Missing(ActionArgument) : HaText.BadChoice(ActionArgument, action, Actions.Keys);
        }

        string item = Optional(arguments, ItemArgument) ?? "";
        if (action != "list" && item.Length == 0)
        {
            return HaText.Missing(ItemArgument);
        }

        var (snapshot, error) = await Ha.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return error;
        }

        var (list, refused) = List(snapshot, Optional(arguments, ListArgument));
        if (list is null)
        {
            return refused;
        }

        if (action == "list")
        {
            if (await GateAsync("todo", service, [list], null, cancellationToken).ConfigureAwait(false) is { } no)
            {
                return no;
            }

            var reply = await Ha.CallAsync("todo", service, [list.Id], null, cancellationToken, returnResponse: true).ConfigureAwait(false);
            return reply.Ok ? HaText.TodoItems(list, reply.Body) : reply.Error;
        }

        string data = HaJson.Object(w =>
        {
            w.WriteString("item", item);
            if (action == "complete")
            {
                w.WriteString("status", "completed");
            }
        });
        return await RunAsync("todo", service, [list], data, "'" + item + "'", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The list a call means: the one named, else the only one; the failure as an <c>Error:</c> sentence. Pure.</summary>
    public static (HaEntity? List, string? Error) List(HaSnapshot snapshot, string? target)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (target is null)
        {
            var lists = snapshot.Of("todo");
            return lists.Count switch
            {
                0 => (null, HaText.NoneOf("to-do list")),
                1 => (lists[0], null),
                _ => (null, HaText.Ambiguous("to-do list", lists, lists.Count)),
            };
        }

        var match = snapshot.Resolve(target, ["todo"]);
        if (match.Error is not null)
        {
            return (null, match.Error);
        }

        return match.Entities.Count == 1 ? (match.Entities[0], null) : (null, HaText.Ambiguous(target, match.Entities, match.Entities.Count));
    }
}
