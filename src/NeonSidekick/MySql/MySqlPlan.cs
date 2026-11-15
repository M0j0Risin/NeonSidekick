using System.Text.Json;
using NeonSidekick.Sql;

namespace NeonSidekick.MySql;

/// <summary>
/// <c>mysql_explain</c>'s plan reader (2026-11-14, the database-tools plan's #12), for the three shapes <c>EXPLAIN FORMAT=JSON</c> returns,
/// one row in one column on every server:
/// <list type="bullet">
/// <item>MySQL's format 1 (8.x's default): a <c>query_block</c> whose <c>table</c>s sit in <c>nested_loop</c>s and operations
/// (<c>ordering_operation</c>, <c>grouping_operation</c>…), each with <c>access_type</c>, <c>rows_examined_per_scan</c> and
/// <c>cost_info</c> (text): a step's own cost is <c>read_cost</c> + <c>eval_cost</c> (#81; <c>prefix_cost</c> is the join's running total).
/// A table holds its own subqueries (<c>attached_subqueries</c>…, #47).</item>
/// <item>MariaDB's: the same <c>query_block</c>, with <c>rows</c> and a numeric <c>cost</c>, and <c>filesort</c>, <c>temporary_table</c> and
/// <c>block-nl-join</c> as containers; a subquery's cache (<c>expression_cache</c> in 10.x, <c>subquery_cache</c> in 11.x) is walked
/// through without a step of its own (#48).</item>
/// <item>MySQL's format 2 (<c>explain_json_format_version=2</c>): an <c>operation</c> tree with <c>inputs</c>, <c>access_type</c>,
/// <c>estimated_rows</c> and <c>estimated_total_cost</c>.</item>
/// </list>
/// Flags: a full scan of a big table, a join through a join buffer (no usable index), a filesort and a temporary table. The steps' labels
/// are <see cref="MySqlText"/>'s (#171). Pure.
/// </summary>
public static class MySqlPlan
{
    /// <summary>What the tool puts in front of the model's statement: a plan, never <c>ANALYZE</c> (which would run it).</summary>
    public const string Prefix = "EXPLAIN FORMAT=JSON ";

    /// <summary>The plan, or null when the text isn't one of the shapes this reads.</summary>
    public static QueryPlan? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = PlanJson.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var steps = new List<QueryPlanStep>();
            var flags = new List<string>();
            if (root.TryGetProperty("query_block", out var block) && block.ValueKind == JsonValueKind.Object)
            {
                Block(block, 0, steps, flags);
                double? total = block.TryGetProperty("cost_info", out var info) && info.ValueKind == JsonValueKind.Object ? Number(info, "query_cost") : Number(block, "cost");
                return new QueryPlan(steps, flags, total, null);
            }

            if (root.TryGetProperty("operation", out _))
            {
                Operation(root, 0, steps, flags);
                return new QueryPlan(steps, flags, Number(root, "estimated_total_cost"), Number(root, "estimated_rows"));
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ─── format 1 and MariaDB ───────────────────────────────────────────────────

    /// <summary>What a container's temporary table is for, where it says.</summary>
    private static readonly Dictionary<string, string> Purposes = new(StringComparer.Ordinal)
    {
        ["ordering_operation"] = "ORDER BY",
        ["grouping_operation"] = "GROUP BY",
        ["duplicates_removal"] = "DISTINCT",
        ["windowing"] = "window functions",
    };

    /// <summary>The containers that hold tables and blocks, named as the step they stand for.</summary>
    private static IReadOnlyDictionary<string, string> Containers => MySqlText.PlanContainers;

    /// <summary>
    /// The arrays of subqueries a block or a table holds (MySQL's <c>opt_explain_json.cc</c>, 8.4, names the SELECT kinds; #47 adds
    /// <c>group_by_subqueries</c>). <c>optimized_away_subqueries</c> stays out: those ran while planning and aren't part of the plan.
    /// </summary>
    private static readonly HashSet<string> SubqueryArrays = new(StringComparer.Ordinal)
    {
        "nested_loop", "query_specifications", "subqueries", "attached_subqueries", "order_by_subqueries", "select_list_subqueries",
        "having_subqueries", "group_by_subqueries",
    };

    private static void Block(JsonElement block, int depth, List<QueryPlanStep> steps, List<string> flags)
    {
        foreach (var property in block.EnumerateObject())
        {
            Member(property.Name, property.Value, depth, steps, flags, buffered: false);
        }
    }

    private static void Member(string name, JsonElement value, int depth, List<QueryPlanStep> steps, List<string> flags, bool buffered)
    {
        switch (name)
        {
            case "table" when value.ValueKind == JsonValueKind.Object:
                Table(value, depth, steps, flags, buffered);
                return;
            case "query_block" when value.ValueKind == JsonValueKind.Object:
                Block(value, depth, steps, flags);
                return;
            case "expression_cache" or "subquery_cache" when value.ValueKind == JsonValueKind.Object:
                // MariaDB's cache around a subquery (#48): what it holds, at the cache's own depth.
                foreach (var property in value.EnumerateObject())
                {
                    Member(property.Name, property.Value, depth, steps, flags, buffered);
                }

                return;
            case var array when SubqueryArrays.Contains(array) && value.ValueKind == JsonValueKind.Array:
                foreach (var item in value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    foreach (var property in item.EnumerateObject())
                    {
                        Member(property.Name, property.Value, depth, steps, flags, buffered);
                    }
                }

                return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (Containers.TryGetValue(name, out string? step))
        {
            if (name is "filesort" || PlanJson.True(value, "using_filesort"))
            {
                flags.Add(QueryPlanText.Filesort(null));
            }

            if (name is "temporary_table" || PlanJson.True(value, "using_temporary_table"))
            {
                flags.Add(QueryPlanText.TempTable(Purposes.GetValueOrDefault(name)));
            }

            steps.Add(new QueryPlanStep(depth, step, null, null, null));
            foreach (var property in value.EnumerateObject())
            {
                Member(property.Name, property.Value, depth + 1, steps, flags, buffered || name == "block-nl-join");
            }
        }
    }

    private static void Table(JsonElement table, int depth, List<QueryPlanStep> steps, List<string> flags, bool buffered)
    {
        string name = PlanJson.Text(table, "table_name") ?? "?";
        string access = PlanJson.Text(table, "access_type") ?? "";
        string? key = PlanJson.Text(table, "key");
        double? rows = PlanJson.Number(table, "rows_examined_per_scan") ?? PlanJson.Number(table, "rows");
        double? cost = table.TryGetProperty("cost_info", out var info) && info.ValueKind == JsonValueKind.Object
            ? PlanJson.Number(info, "read_cost") is { } read ? read + (PlanJson.Number(info, "eval_cost") ?? 0) : null
            : PlanJson.Number(table, "cost");
        steps.Add(new QueryPlanStep(depth, Access(access), key is null ? name : name + " using " + key, rows, cost));
        if (access == "ALL" && rows >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.FullScan(name, rows));
        }

        if ((buffered || PlanJson.Text(table, "using_join_buffer") is not null) && access is "ALL" or "index")
        {
            flags.Add(QueryPlanText.JoinBuffer(name, rows));
        }

        // A derived table's or a subquery's own block sits inside the table, and so do the subqueries its condition runs (#47).
        foreach (var property in table.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object && property.Name is "materialized_from_subquery" or "materialized" or "derived" or "query_block")
            {
                var inner = property.Value.TryGetProperty("query_block", out var block) ? block : property.Value;
                Block(inner, depth + 1, steps, flags);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array && SubqueryArrays.Contains(property.Name))
            {
                Member(property.Name, property.Value, depth + 1, steps, flags, buffered: false);
            }
        }
    }

    /// <summary>An <c>access_type</c> as a step's operation.</summary>
    private static string Access(string access) => access switch
    {
        "ALL" => MySqlText.PlanFullTableScan,
        "index" => MySqlText.PlanFullIndexScan,
        "range" => MySqlText.PlanIndexRange,
        "ref" or "eq_ref" or "ref_or_null" or "const" or "system" => MySqlText.PlanIndexLookup(access),
        "" => MySqlText.PlanTable,
        _ => access,
    };

    // ─── format 2 ───────────────────────────────────────────────────────────────

    private static void Operation(JsonElement node, int depth, List<QueryPlanStep> steps, List<string> flags)
    {
        string operation = PlanJson.Text(node, "operation") ?? "?";
        string access = PlanJson.Text(node, "access_type") ?? "";
        string? table = PlanJson.Text(node, "table_name");
        string? index = PlanJson.Text(node, "index_name");
        double? rows = PlanJson.Number(node, "estimated_rows");
        steps.Add(new QueryPlanStep(depth, operation, table is null ? index : index is null ? table : table + " using " + index, rows, PlanJson.Number(node, "estimated_total_cost")));
        if (access == "table" && table is not null && rows >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.FullScan(table, rows));
        }

        if (access == "sort" && rows >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.BigSort(rows));
        }

        if (node.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var input in inputs.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                Operation(input, depth + 1, steps, flags);
            }
        }
    }

    /// <summary>A number written as one (MariaDB) or as text (MySQL's <c>"1.20"</c>).</summary>
    private static double? Number(JsonElement node, string name) => PlanJson.Number(node, name);
}
