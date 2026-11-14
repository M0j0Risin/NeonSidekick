using System.Globalization;
using System.Xml;

namespace NeonSidekick.Sql;

/// <summary>
/// <c>sql_explain</c>'s plan (2026-11-14, the database-tools plan's #12): SQL Server's estimated showplan. The query runs as the middle of
/// three batches on one connection (<see cref="Batches"/>): <c>SET SHOWPLAN_XML ON</c>, the query (compiled, never run), and
/// <c>SET SHOWPLAN_XML OFF</c> in a <c>finally</c>, since the connection goes back to the pool. The XML is read with
/// <see cref="Office.OfficePackage.ReaderSettings"/> (no DTD, no resolver): every <c>RelOp</c> a step (its <c>PhysicalOp</c>, the first
/// <c>Object</c> it reads, <c>EstimateRows</c> and <c>EstimatedTotalSubtreeCost</c>), nested as the XML nests them. Flags: a table or clustered
/// index scan of a big table, key and RID lookups, a nested loop over a big input, a big sort, the plan's <c>MissingIndexes</c>, an implicit
/// conversion that affects the plan, and a join with no join predicate. Pure.
/// </summary>
public static class SqlServerPlan
{
    public const string On = "SET SHOWPLAN_XML ON";
    public const string Off = "SET SHOWPLAN_XML OFF";

    /// <summary>The three batches, in order: ON, the query, OFF (the caller runs the last in a <c>finally</c>).</summary>
    public static IReadOnlyList<string> Batches(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return [On, sql, Off];
    }

    /// <summary>
    /// The query as the plan batch carries it, its values given as typed <c>DECLARE</c>s in front (2026-11-14, found live on <c>sql_server_dev</c>:
    /// with <c>SHOWPLAN_XML</c> on, a call with parameters, which SqlClient sends as an RPC, returns no plan at all). Each takes the type
    /// <see cref="SqlAccess.Bind"/> binds it as, so the plan is the one <c>sql_query</c> would get. A value is a literal the app writes (a string's
    /// quotes doubled, a number invariant), its name one <see cref="Llm.Tools.SqlQueryTool.ReadParameters"/> passed (letters, digits, <c>_</c>);
    /// and the batch is compiled, never run. The showplan then has a <c>StmtSimple</c> per statement, and only the query's carries a plan.
    /// </summary>
    public static string WithDeclarations(string sql, IReadOnlyList<SqlParameterValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Count == 0)
        {
            return sql;
        }

        var text = new System.Text.StringBuilder();
        foreach (var parameter in parameters)
        {
            text.Append("DECLARE @").Append(parameter.Name).Append(' ').Append(Declared(parameter.Value)).Append(";\n");
        }

        return text.Append(sql).ToString();
    }

    private static string Declared(object? value) => value switch
    {
        null => "nvarchar(4000) = NULL",
        string s => (s.Length <= 4000 ? "nvarchar(4000)" : "nvarchar(max)") + " = " + Unicode(s),
        long l => "bigint = " + l.ToString(CultureInfo.InvariantCulture),
        int i => "int = " + i.ToString(CultureInfo.InvariantCulture),
        decimal d => "decimal(38, " + d.Scale.ToString(CultureInfo.InvariantCulture) + ") = " + d.ToString(CultureInfo.InvariantCulture),
        double f => "float = " + f.ToString("R", CultureInfo.InvariantCulture),
        bool b => "bit = " + (b ? "1" : "0"),
        var other => "nvarchar(4000) = " + Unicode(Convert.ToString(other, CultureInfo.InvariantCulture) ?? ""),
    };

    private static string Unicode(string s) => "N'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>SQL Server's "permission denied" for SHOWPLAN.</summary>
    public const int NoShowplanPermission = 262;

    /// <summary>The plan, or null when the text isn't showplan XML.</summary>
    public static QueryPlan? Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var steps = new List<QueryPlanStep>();
        var flags = new List<string>();
        var open = new Stack<(int XmlDepth, int Step)>();
        double cost = 0;
        double rows = 0;
        bool sawPlan = false;
        bool inStatementWithPlan = false;
        double? statementCost = null;
        double? statementRows = null;
        string? missingTable = null;
        double? missingImpact = null;
        List<string> equality = [], inequality = [], include = [];
        string? usage = null;
        var reads = new Dictionary<int, double>();
        var executions = new Dictionary<int, double>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Office.OfficePackage.ReaderSettings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    while (open.Count > 0 && open.Peek().XmlDepth >= reader.Depth)
                    {
                        open.Pop();
                    }

                    if (reader.LocalName == "MissingIndex" && missingTable is not null)
                    {
                        flags.Add(QueryPlanText.MissingIndex(missingTable, equality, inequality, include, missingImpact));
                        missingTable = null;
                        equality = [];
                        inequality = [];
                        include = [];
                    }

                    if (reader.LocalName == "StmtSimple" && inStatementWithPlan)
                    {
                        cost += statementCost ?? 0;
                        rows = Math.Max(rows, statementRows ?? 0);
                        inStatementWithPlan = false;
                    }

                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "StmtSimple":
                        statementCost = Number(reader.GetAttribute("StatementSubTreeCost"));
                        statementRows = Number(reader.GetAttribute("StatementEstRows"));
                        break;
                    case "QueryPlan":
                        sawPlan = true;
                        inStatementWithPlan = true;
                        break;
                    case "RelOp":
                        RelOp(reader, open, steps, flags, reads, executions);
                        break;
                    case "IndexScan" when open.Count > 0 && reader.GetAttribute("Lookup") is "1" or "true":
                        var lookup = steps[open.Peek().Step];
                        steps[open.Peek().Step] = lookup with { Operation = "Key Lookup" };
                        break;
                    case "Object" when open.Count > 0 && steps[open.Peek().Step].Target is null:
                        Object(reader, open.Peek().Step, steps, flags, reads);
                        break;
                    case "MissingIndexGroup":
                        missingImpact = Number(reader.GetAttribute("Impact"));
                        break;
                    case "MissingIndex":
                        missingTable = Name(reader.GetAttribute("Schema"), reader.GetAttribute("Table"));
                        break;
                    case "ColumnGroup":
                        usage = reader.GetAttribute("Usage");
                        break;
                    case "Column" when missingTable is not null:
                        string column = Bare(reader.GetAttribute("Name"));
                        (usage switch { "EQUALITY" => equality, "INEQUALITY" => inequality, _ => include }).Add(column);
                        break;
                    case "PlanAffectingConvert":
                        flags.Add(QueryPlanText.ImplicitConversion(reader.GetAttribute("Expression") ?? "CONVERT_IMPLICIT"));
                        break;
                    case "Warnings" when reader.GetAttribute("NoJoinPredicate") is "1" or "true":
                        flags.Add(QueryPlanText.NoJoinPredicate);
                        break;
                    case "UnmatchedIndexes" or "SpillToTempDb" or "MemoryGrantWarning":
                        flags.Add(QueryPlanText.Warning(reader.LocalName));
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return null;
        }

        if (!sawPlan)
        {
            return null;
        }

        // The lookup flag once the step's object is known: its rows a run times its runs (one per outer row).
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Operation is "Key Lookup" or "RID Lookup")
            {
                flags.Add(QueryPlanText.KeyLookup(steps[i].Target ?? "a table", steps[i].Rows * executions.GetValueOrDefault(i, 1)));
            }
        }

        return new QueryPlan(steps, flags.Distinct(StringComparer.Ordinal).ToList(), cost > 0 ? cost : null, rows > 0 ? rows : null);
    }

    /// <remarks>
    /// <c>EstimateRows</c> is what a step returns, a run; a scan's rows read are <c>EstimatedRowsRead</c> (else <c>TableCardinality</c>), kept in
    /// <paramref name="reads"/> for the scan flag (found live: a scan of 121,317 rows that returns 15 says <c>EstimateRows="15.1189"</c>), and its
    /// runs (1 + <c>EstimateRebinds</c> + <c>EstimateRewinds</c>) in <paramref name="executions"/> for the lookup flag.
    /// </remarks>
    private static void RelOp(XmlReader reader, Stack<(int XmlDepth, int Step)> open, List<QueryPlanStep> steps, List<string> flags, Dictionary<int, double> reads, Dictionary<int, double> executions)
    {
        string physical = reader.GetAttribute("PhysicalOp") ?? "?";
        string logical = reader.GetAttribute("LogicalOp") ?? "";
        double? rows = Number(reader.GetAttribute("EstimateRows"));
        double? cost = Number(reader.GetAttribute("EstimatedTotalSubtreeCost"));
        string operation = logical.Length > 0 && logical != physical && physical is "Hash Match" or "Nested Loops" or "Merge Join" ? physical + " (" + logical + ")" : physical;
        int index = steps.Count;
        if ((Number(reader.GetAttribute("EstimatedRowsRead")) ?? Number(reader.GetAttribute("TableCardinality"))) is { } read)
        {
            reads[index] = read;
        }

        executions[index] = 1 + (Number(reader.GetAttribute("EstimateRebinds")) ?? 0) + (Number(reader.GetAttribute("EstimateRewinds")) ?? 0);
        open.Push((reader.Depth, index));
        steps.Add(new QueryPlanStep(open.Count - 1, operation, null, rows, cost));
        if (physical == "Sort" && rows >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.BigSort(rows));
        }

        if (physical == "Nested Loops" && rows >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.NestedLoop(rows));
        }

        if (reader.IsEmptyElement)
        {
            open.Pop();
        }
    }

    private static void Object(XmlReader reader, int step, List<QueryPlanStep> steps, List<string> flags, Dictionary<int, double> reads)
    {
        string table = Name(reader.GetAttribute("Schema"), reader.GetAttribute("Table"));
        string? index = reader.GetAttribute("Index") is { } i ? Bare(i) : null;
        var current = steps[step];
        steps[step] = current with { Target = index is null ? table : table + " using " + index };
        double? read = reads.TryGetValue(step, out double r) ? r : current.Rows;
        if (current.Operation is "Table Scan" or "Clustered Index Scan" && read >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.FullScan(table, read));
        }
    }

    /// <summary><c>[Sales].[Orders]</c> as <c>Sales.Orders</c>; the table alone when no schema is given.</summary>
    private static string Name(string? schema, string? table)
    {
        string bareTable = Bare(table);
        return schema is null ? bareTable : Bare(schema) + "." + bareTable;
    }

    private static string Bare(string? name) =>
        name is { Length: >= 2 } n && n[0] == '[' && n[^1] == ']' ? n[1..^1].Replace("]]", "]", StringComparison.Ordinal) : name ?? "?";

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
}
