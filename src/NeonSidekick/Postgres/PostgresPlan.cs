using System.Text.Json;
using NeonSidekick.Sql;

namespace NeonSidekick.Postgres;

/// <summary>
/// <c>postgres_explain</c>'s plan reader (2026-11-14, the database-tools plan's #12): <c>EXPLAIN (FORMAT JSON)</c>'s one array, each
/// element a <c>Plan</c> node whose <c>Plans</c> are its inputs. A step is the node type and the relation (or index, CTE, function) it reads,
/// with <c>Plan Rows</c> and <c>Total Cost</c>. Flags: a sequential scan of a big table, a nested loop over a big input whose inner side
/// uses no index (#82), a big sort. A parallel-aware node's <c>Plan Rows</c> is one process's share, so it is scaled back to the whole (#79,
/// <see cref="ParallelDivisor"/>) for the step and its flags. Pure.
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
            using var document = PlanJson.Parse(json);
            var root = document.RootElement;
            var first = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("Plan", out var plan) || plan.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var steps = new List<QueryPlanStep>();
            var flags = new List<string>();
            Walk(plan, 0, 1, steps, flags);
            return new QueryPlan(steps, flags, PlanJson.Number(plan, "Total Cost"), PlanJson.Number(plan, "Plan Rows"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the planner divides a parallel node's rows by (<c>costsize.c</c>'s <c>get_parallel_divisor</c>): the workers, plus the leader's
    /// share of 1 − 0.3 per worker while it is above 0 (1.7 for one worker, 2.4 for two, the workers alone from four on). The plan doesn't say
    /// whether <c>parallel_leader_participation</c> is on, so its default (on) is assumed.
    /// </summary>
    public static double ParallelDivisor(int workers) => workers <= 0 ? 1 : workers + Math.Max(0, 1 - 0.3 * workers);

    private static void Walk(JsonElement node, int depth, double divisor, List<QueryPlanStep> steps, List<string> flags)
    {
        string nodeType = PlanJson.Text(node, "Node Type") ?? "";
        string type = nodeType.Length > 0 ? nodeType : "?";
        if (PlanJson.Text(node, "Join Type") is { } join && type is "Hash Join" or "Merge Join" or "Nested Loop" && join != "Inner")
        {
            type += " (" + join + ")";
        }

        double? rows = Rows(node, divisor);
        steps.Add(new QueryPlanStep(depth, type, Target(node), rows, PlanJson.Number(node, "Total Cost")));

        if (nodeType == "Seq Scan" && rows >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.FullScan(Relation(node) ?? "a table", rows));
        }
        else if (nodeType == "Seq Scan" && node.TryGetProperty("Filter", out _) && PlanJson.Number(node, "Total Cost") >= BigScanCost)
        {
            flags.Add(QueryPlanText.FilteredScan(Relation(node) ?? "a table", rows));
        }

        if (nodeType == "Sort" && rows >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.BigSort(rows));
        }

        // Below a Gather, its workers share the parallel nodes' rows.
        double below = nodeType is "Gather" or "Gather Merge" && PlanJson.Number(node, "Workers Planned") is { } workers ? ParallelDivisor((int)workers) : divisor;
        if (node.TryGetProperty("Plans", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
        {
            if (nodeType == "Nested Loop" && inputs.GetArrayLength() > 0 && Rows(inputs[0], below) is { } outer and >= QueryPlan.BigRows && !IndexedInner(inputs))
            {
                flags.Add(QueryPlanText.NestedLoop(outer));
            }

            foreach (var input in inputs.EnumerateArray())
            {
                if (input.ValueKind == JsonValueKind.Object)
                {
                    Walk(input, depth + 1, below, steps, flags);
                }
            }
        }
    }

    /// <summary>A node's <c>Plan Rows</c>, a parallel-aware one's scaled by <paramref name="divisor"/> to the whole (#79).</summary>
    private static double? Rows(JsonElement node, double divisor) =>
        PlanJson.Number(node, "Plan Rows") is { } rows ? (PlanJson.True(node, "Parallel Aware") ? rows * divisor : rows) : null;

    /// <summary>
    /// Whether a nested loop's inner side already reads through an index (#82): the input whose <c>Parent Relationship</c> is <c>Inner</c> is an
    /// index scan, an index-only scan or a bitmap heap scan, or a <c>Memoize</c> over one (found live on 17: the inner side is Memoize, the
    /// index scan its own child).
    /// </summary>
    private static bool IndexedInner(JsonElement inputs)
    {
        foreach (var input in inputs.EnumerateArray())
        {
            if (PlanJson.Text(input, "Parent Relationship") != "Inner")
            {
                continue;
            }

            var side = input;
            if (PlanJson.Text(side, "Node Type") == "Memoize" && side.TryGetProperty("Plans", out var under) && under.ValueKind == JsonValueKind.Array && under.GetArrayLength() > 0)
            {
                side = under[0];
            }

            return PlanJson.Text(side, "Node Type") is "Index Scan" or "Index Only Scan" or "Bitmap Heap Scan";
        }

        return false;
    }

    private static string? Relation(JsonElement node) => PlanJson.Text(node, "Relation Name");

    private static string? Target(JsonElement node)
    {
        string? relation = Relation(node);
        string? index = PlanJson.Text(node, "Index Name");
        if (relation is not null)
        {
            return index is null ? relation : relation + " using " + index;
        }

        return index ?? PlanJson.Text(node, "CTE Name") ?? PlanJson.Text(node, "Function Name") ?? PlanJson.Text(node, "Subplan Name");
    }
}
