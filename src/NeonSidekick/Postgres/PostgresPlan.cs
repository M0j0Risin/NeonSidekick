using System.Text.Json;
using NeonSidekick.Sql;

namespace NeonSidekick.Postgres;

/// <summary>
/// <c>postgres_explain</c>'s plan reader (2026-11-14, the database-tools plan's #12): <c>EXPLAIN (FORMAT JSON)</c>'s one array, each
/// element a <c>Plan</c> node whose <c>Plans</c> are its inputs. A step is the node type and the relation (or index, CTE, function) it reads,
/// with <c>Plan Rows</c> and <c>Total Cost</c>. Flags: a sequential scan of a big table, a nested loop over a big input, a big sort. Pure.
/// </summary>
public static class PostgresPlan
{
    /// <summary>What the tool puts in front of the model's statement: a plan, never <c>ANALYZE</c> (which would run it).</summary>
    public const string Prefix = "EXPLAIN (FORMAT JSON) ";

    /// <summary>
    /// The total cost from which a filtered sequential scan is flagged although it keeps few rows (2026-11-14): its <c>Plan Rows</c> are the
    /// rows kept, not read (found live: a scan of 200,000 rows keeping 40 costs 3,716). At the default costs this is a table of about
    /// 10,000 rows.
    /// </summary>
    public const double BigScanCost = 200;

    /// <summary>The plan, or null when the text isn't the JSON this reads.</summary>
    public static QueryPlan? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var first = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("Plan", out var plan) || plan.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var steps = new List<QueryPlanStep>();
            var flags = new List<string>();
            Walk(plan, 0, steps, flags);
            return new QueryPlan(steps, flags, Double(plan, "Total Cost"), Double(plan, "Plan Rows"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Walk(JsonElement node, int depth, List<QueryPlanStep> steps, List<string> flags)
    {
        string type = Text(node, "Node Type") ?? "?";
        if (Text(node, "Join Type") is { } join && type is "Hash Join" or "Merge Join" or "Nested Loop" && join != "Inner")
        {
            type += " (" + join + ")";
        }

        double? rows = Double(node, "Plan Rows");
        steps.Add(new QueryPlanStep(depth, type, Target(node), rows, Double(node, "Total Cost")));

        string nodeType = Text(node, "Node Type") ?? "";
        if (nodeType == "Seq Scan" && rows >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.FullScan(Relation(node) ?? "a table", rows));
        }
        else if (nodeType == "Seq Scan" && node.TryGetProperty("Filter", out _) && Double(node, "Total Cost") >= BigScanCost)
        {
            flags.Add(QueryPlanText.FilteredScan(Relation(node) ?? "a table", rows));
        }

        if (nodeType == "Sort" && rows >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.BigSort(rows));
        }

        if (node.TryGetProperty("Plans", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
        {
            if (nodeType == "Nested Loop" && inputs.GetArrayLength() > 0 && Double(inputs[0], "Plan Rows") is { } outer and >= QueryPlan.BigRows)
            {
                flags.Add(QueryPlanText.NestedLoop(outer));
            }

            foreach (var input in inputs.EnumerateArray())
            {
                if (input.ValueKind == JsonValueKind.Object)
                {
                    Walk(input, depth + 1, steps, flags);
                }
            }
        }
    }

    private static string? Relation(JsonElement node) =>
        Text(node, "Relation Name") is { } relation ? (Text(node, "Schema") is { } schema ? schema + "." + relation : relation) : null;

    private static string? Target(JsonElement node)
    {
        string? relation = Relation(node);
        string? index = Text(node, "Index Name");
        if (relation is not null)
        {
            return index is null ? relation : relation + " using " + index;
        }

        return index ?? Text(node, "CTE Name") ?? Text(node, "Function Name") ?? Text(node, "Subplan Name");
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Double(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) ? d : null;
}
