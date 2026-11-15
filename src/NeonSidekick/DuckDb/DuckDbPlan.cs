using System.Text.Json;
using NeonSidekick.Sql;

namespace NeonSidekick.DuckDb;

/// <summary>
/// <c>duckdb_explain</c>'s plan reader (2026-11-14, the database-tools plan's #12): <c>EXPLAIN (FORMAT json)</c> returns two columns,
/// <c>explain_key</c> (<c>physical_plan</c>) and <c>explain_value</c>, the plan in the second (the run reads the last column). It is an array of
/// nodes, each a <c>name</c>, its <c>children</c> and an <c>extra_info</c> object (<c>Table</c>, <c>Function</c>, <c>Estimated Cardinality</c>
/// as text; seen on 1.5.6). DuckDB gives no costs, so the costliest steps are the ones with the most rows. <c>PROJECTION</c> nodes are left
/// out (they pick columns, and a plan holds many), their children taking their place. Flags: a sequential scan of a big table (a file's scan
/// has no index to suggest, #72), a nested loop over a big input, a cross product that makes many rows from two inputs of more than one
/// row each (#50: a one-row side, a scalar subquery's, is harmless), a big sort. The plan's rows are its root's (#170). Pure.
/// </summary>
public static class DuckDbPlan
{
    /// <summary>What the tool puts in front of the model's statement: a plan, never <c>ANALYZE</c> (which would run it).</summary>
    public const string Prefix = "EXPLAIN (FORMAT json) ";

    /// <summary>The plan, or null when the text isn't the JSON this reads.</summary>
    public static QueryPlan? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = PlanJson.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Object || !root[0].TryGetProperty("name", out _))
            {
                return null;
            }

            var steps = new List<QueryPlanStep>();
            var flags = new List<string>();
            foreach (var node in root.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object))
            {
                Walk(node, 0, steps, flags);
            }

            return new QueryPlan(steps, flags, null, Rows(root[0]));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Walk(JsonElement node, int depth, List<QueryPlanStep> steps, List<string> flags)
    {
        string name = PlanJson.Text(node, "name")?.Trim() ?? "?";
        var info = node.TryGetProperty("extra_info", out var extra) && extra.ValueKind == JsonValueKind.Object ? extra : default;
        double? rows = Rows(node);
        bool shown = name != "PROJECTION";
        if (shown)
        {
            string? table = PlanJson.Text(info, "Table");
            string? target = table ?? PlanJson.Text(info, "Function");
            steps.Add(new QueryPlanStep(depth, name, target, rows, null));
            if (name is "SEQ_SCAN" or "TABLE_SCAN" && table is not null && PlanJson.Text(info, "Type") != "Index Scan" && rows >= QueryPlan.BigRows)
            {
                flags.Add(QueryPlanText.FullScan(table, rows));
            }

            if (name == "ORDER_BY" && Biggest(node) is { } sorted and >= QueryPlan.BigRows)
            {
                flags.Add(QueryPlanText.BigSort(sorted));
            }

            if (name is "NESTED_LOOP_JOIN" or "BLOCKWISE_NL_JOIN" && Biggest(node) is { } outer and >= QueryPlan.BigRows)
            {
                flags.Add(QueryPlanText.NestedLoop(outer));
            }

            if (name == "CROSS_PRODUCT" && rows >= QueryPlan.BigRows && Smallest(node) > 1)
            {
                flags.Add(QueryPlanText.CrossProduct(rows));
            }
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object))
            {
                Walk(child, shown ? depth + 1 : depth, steps, flags);
            }
        }
    }

    /// <summary>A node's own estimate (<c>extra_info</c>'s <c>Estimated Cardinality</c>), or null.</summary>
    private static double? Rows(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("extra_info", out var info) ? PlanJson.Number(info, "Estimated Cardinality") : null;

    /// <summary>The estimates of <paramref name="node"/>'s direct inputs (a PROJECTION input by its own estimate, the same rows as below it).</summary>
    private static IEnumerable<double> Inputs(JsonElement node) =>
        node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array
            ? children.EnumerateArray().Select(Rows).OfType<double>()
            : [];

    /// <summary>The most rows any direct input of <paramref name="node"/> is estimated to bring.</summary>
    private static double? Biggest(JsonElement node) => Inputs(node).Select(r => (double?)r).Max();

    /// <summary>The fewest rows any direct input of <paramref name="node"/> is estimated to bring (#50).</summary>
    private static double? Smallest(JsonElement node) => Inputs(node).Select(r => (double?)r).Min();
}
