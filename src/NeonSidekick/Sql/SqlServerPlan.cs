using System.Data;
using System.Globalization;
using System.Xml;

namespace NeonSidekick.Sql;

/// <summary>
/// <c>sql_explain</c>'s plan (2026-11-14, the database-tools plan's #12): SQL Server's estimated showplan. The query runs between
/// <c>SET SHOWPLAN_XML ON</c> and <c>SET SHOWPLAN_XML OFF</c>, each its own batch on one connection (<see cref="SqlAccess.RunAsync"/>): compiled,
/// never run, and OFF sent in a <c>finally</c>, since the connection goes back to the pool. The XML is read with
/// <see cref="Office.OfficePackage.ReaderSettings"/> (no DTD, no resolver): every <c>RelOp</c> a step (its <c>PhysicalOp</c>, the first
/// <c>Object</c> it reads, <c>EstimateRows</c> and <c>EstimatedTotalSubtreeCost</c>), nested as the XML nests them. A scalar function's own
/// plan (<c>&lt;UDF&gt;</c>, inside the statement that calls it) is skipped whole (#69). Flags: a scan of a big table or index (never a
/// columnstore's, #94), key and RID lookups past <see cref="LookupRows"/> (#80), a nested loop over a big outer input (#49), a big sort, the
/// plan's <c>MissingIndexes</c>, a conversion that keeps a seek from being used (#70), columns with no statistics (#59) and a join with no
/// join predicate. Pure.
/// </summary>
public static class SqlServerPlan
{
    public const string On = "SET SHOWPLAN_XML ON";
    public const string Off = "SET SHOWPLAN_XML OFF";

    /// <summary>The rows a lookup reads over all its runs from which it is flagged (2026-11-15, #80: a seek plus a few lookups is fine).</summary>
    public const double LookupRows = 1_000;

    /// <summary>
    /// The query as the plan batch carries it, its values given as typed <c>DECLARE</c>s in front (2026-11-14, found live on <c>sql_server_dev</c>:
    /// with <c>SHOWPLAN_XML</c> on, a call with parameters, which SqlClient sends as an RPC — <c>sp_executesql</c> — returns only an
    /// <c>EXECUTE PROC</c> statement with no plan). Each takes the type <see cref="SqlAccess.Bind"/> binds it as (#76). A declared variable is
    /// not sniffed, so the plan is the one for an unknown value: a guess from the column's statistics, not the given value (#2, settled live
    /// 2026-11-15: <c>&lt; @p</c> with <c>@p = 5</c> estimated 87 rows where <c>&lt; 5</c> estimated 4, <c>OPTION (RECOMPILE)</c> or not). A
    /// value is a literal the app writes (a string's quotes doubled, a number invariant), its name one
    /// <see cref="Llm.Tools.SqlQueryTool.ReadParameters"/> passed (letters, digits, <c>_</c>, no two alike but for case, #28); and the batch is
    /// compiled, never run. The showplan then has a <c>StmtSimple</c> per statement, and only the query's carries a plan.
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
            text.Append("DECLARE @").Append(parameter.Name).Append(' ').Append(Declared(parameter)).Append(";\n");
        }

        return text.Append(sql).ToString();
    }

    /// <summary>A declaration's type, from the parameter <see cref="SqlAccess.Bind"/> makes, and its value as a literal.</summary>
    private static string Declared(SqlParameterValue parameter)
    {
        var bound = SqlAccess.Bind(parameter);
        string type = bound.SqlDbType switch
        {
            SqlDbType.NVarChar => bound.Size is -1 or > 4000 ? "nvarchar(max)" : "nvarchar(" + Invariant(bound.Size is > 0 ? bound.Size : 4000) + ")",
            SqlDbType.Decimal => "decimal(" + Invariant(bound.Precision) + ", " + Invariant(bound.Scale) + ")",
            SqlDbType.VarBinary or SqlDbType.Binary => "varbinary(max)",
            var other => other.ToString().ToLowerInvariant(),
        };
        return type + " = " + Literal(parameter.Value);
    }

    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        string s => Unicode(s),
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double f => f.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        DateTime t => Unicode(t.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
        var other => Unicode(Convert.ToString(other, CultureInfo.InvariantCulture) ?? ""),
    };

    private static string Unicode(string s) => "N'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Invariant(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>SQL Server's "permission denied" for SHOWPLAN.</summary>
    public const int NoShowplanPermission = 262;

    /// <summary>What the reader keeps of one step beside the step itself.</summary>
    private sealed class StepFacts
    {
        public double? Read;
        public double Executions = 1;
        public string? Table;
        public string? Storage;
        public int FirstChild = -1;
    }

    /// <summary>The plan, or null when the text isn't showplan XML.</summary>
    public static QueryPlan? Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var steps = new List<QueryPlanStep>();
        var facts = new List<StepFacts>();
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
        List<string>? noStatistics = null;
        int? skipping = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Office.OfficePackage.ReaderSettings);
            while (reader.Read())
            {
                // A scalar function's own plan (#69): nothing inside it is the statement's.
                if (skipping is int udf)
                {
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == udf && reader.LocalName == "UDF")
                    {
                        skipping = null;
                    }

                    continue;
                }

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
                    }

                    if (reader.LocalName == "ColumnsWithNoStatistics" && noStatistics is not null)
                    {
                        flags.Add(QueryPlanText.NoStatistics(noStatistics));
                        noStatistics = null;
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
                    case "UDF" when !reader.IsEmptyElement:
                        skipping = reader.Depth;
                        break;
                    case "StmtSimple":
                        statementCost = QueryPlan.Number(reader.GetAttribute("StatementSubTreeCost"));
                        statementRows = QueryPlan.Number(reader.GetAttribute("StatementEstRows"));
                        break;
                    case "QueryPlan":
                        sawPlan = true;
                        inStatementWithPlan = true;
                        break;
                    case "RelOp":
                        RelOp(reader, open, steps, facts, flags);
                        break;
                    case "IndexScan" or "TableScan" when open.Count > 0:
                        int scanned = open.Peek().Step;
                        facts[scanned].Storage ??= reader.GetAttribute("Storage");
                        // #73: a lookup is a Key Lookup only into a clustered index; a heap's stays a RID Lookup.
                        if (reader.LocalName == "IndexScan" && reader.GetAttribute("Lookup") is "1" or "true" && steps[scanned].Operation == "Clustered Index Seek")
                        {
                            steps[scanned] = steps[scanned] with { Operation = "Key Lookup" };
                        }

                        break;
                    case "Object" when open.Count > 0 && steps[open.Peek().Step].Target is null:
                        Object(reader, open.Peek().Step, steps, facts);
                        break;
                    case "MissingIndexGroup":
                        missingImpact = QueryPlan.Number(reader.GetAttribute("Impact"));
                        break;
                    case "MissingIndex":
                        // #97: each suggestion's lists start empty, and an empty one suggests nothing.
                        equality = [];
                        inequality = [];
                        include = [];
                        missingTable = reader.IsEmptyElement ? null : Name(reader.GetAttribute("Schema"), reader.GetAttribute("Table"));
                        break;
                    case "ColumnGroup":
                        usage = reader.GetAttribute("Usage");
                        break;
                    case "Column" when missingTable is not null:
                        string column = Bare(reader.GetAttribute("Name"));
                        (usage switch { "EQUALITY" => equality, "INEQUALITY" => inequality, _ => include }).Add(column);
                        break;
                    case "ColumnsWithNoStatistics" when !reader.IsEmptyElement:
                        noStatistics = [];
                        break;
                    case "ColumnReference" when noStatistics is not null:
                        string table = Bare(reader.GetAttribute("Table"));
                        string name = Bare(reader.GetAttribute("Column"));
                        noStatistics.Add(reader.GetAttribute("Table") is null ? name : table + "." + name);
                        break;
                    case "PlanAffectingConvert" when reader.GetAttribute("ConvertIssue") == "Seek Plan":
                        flags.Add(QueryPlanText.ImplicitConversion(ConvertCall(reader.GetAttribute("Expression") ?? "CONVERT_IMPLICIT")));
                        break;
                    case "Warnings" when reader.GetAttribute("NoJoinPredicate") is "1" or "true":
                        flags.Add(QueryPlanText.NoJoinPredicate);
                        break;
                    case "UnmatchedIndexes":
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

        // Once every step's inputs and objects are known: the scans by the rows they read, the nested loops by their outer input, the lookups
        // by their rows a run times their runs (one per outer row).
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var fact = facts[i];
            double? read = fact.Read ?? step.Rows;
            if (step.Operation is "Table Scan" or "Clustered Index Scan" or "Index Scan" && fact.Storage != "ColumnStore" && read >= QueryPlan.BigRows)
            {
                flags.Add(QueryPlanText.FullScan(fact.Table ?? "a table", read));
            }

            if (step.Operation.StartsWith("Nested Loops", StringComparison.Ordinal) && fact.FirstChild >= 0 && steps[fact.FirstChild].Rows is { } outer and >= QueryPlan.BigRows)
            {
                flags.Add(QueryPlanText.NestedLoop(outer));
            }

            if (step.Operation is "Key Lookup" or "RID Lookup" && step.Rows * fact.Executions is { } looked and >= LookupRows)
            {
                flags.Add(QueryPlanText.KeyLookup(step.Target ?? "a table", looked));
            }
        }

        return new QueryPlan(steps, flags, cost > 0 ? cost : null, rows > 0 ? rows : null);
    }

    /// <remarks>
    /// <c>EstimateRows</c> is what a step returns, a run; a scan's rows read are <c>EstimatedRowsRead</c> (else <c>TableCardinality</c>), kept
    /// for the scan flag (found live: a scan of 121,317 rows that returns 15 says <c>EstimateRows="15.1189"</c>), and its runs
    /// (1 + <c>EstimateRebinds</c> + <c>EstimateRewinds</c>) for the lookup flag. A step's first input is its parent's first child (a nested
    /// loop's outer input, #49).
    /// </remarks>
    private static void RelOp(XmlReader reader, Stack<(int XmlDepth, int Step)> open, List<QueryPlanStep> steps, List<StepFacts> facts, List<string> flags)
    {
        string physical = reader.GetAttribute("PhysicalOp") ?? "?";
        string logical = reader.GetAttribute("LogicalOp") ?? "";
        double? rows = QueryPlan.Number(reader.GetAttribute("EstimateRows"));
        double? cost = QueryPlan.Number(reader.GetAttribute("EstimatedTotalSubtreeCost"));
        string operation = logical.Length > 0 && logical != physical && physical is "Hash Match" or "Nested Loops" or "Merge Join" ? physical + " (" + logical + ")" : physical;
        int index = steps.Count;
        var fact = new StepFacts
        {
            Read = QueryPlan.Number(reader.GetAttribute("EstimatedRowsRead")) ?? QueryPlan.Number(reader.GetAttribute("TableCardinality")),
            Executions = 1 + (QueryPlan.Number(reader.GetAttribute("EstimateRebinds")) ?? 0) + (QueryPlan.Number(reader.GetAttribute("EstimateRewinds")) ?? 0),
        };
        if (open.Count > 0 && facts[open.Peek().Step].FirstChild < 0)
        {
            facts[open.Peek().Step].FirstChild = index;
        }

        open.Push((reader.Depth, index));
        steps.Add(new QueryPlanStep(open.Count - 1, operation, null, rows, cost));
        facts.Add(fact);
        if (physical == "Sort" && rows >= QueryPlan.BigRows)
        {
            flags.Add(QueryPlanText.BigSort(rows));
        }

        if (reader.IsEmptyElement)
        {
            open.Pop();
        }
    }

    private static void Object(XmlReader reader, int step, List<QueryPlanStep> steps, List<StepFacts> facts)
    {
        string table = Name(reader.GetAttribute("Schema"), reader.GetAttribute("Table"));
        string? index = reader.GetAttribute("Index") is { } i ? Bare(i) : null;
        steps[step] = steps[step] with { Target = index is null ? table : table + " using " + index };
        facts[step].Table = table;
    }

    /// <summary>
    /// The <c>CONVERT_IMPLICIT(…)</c> call of a conversion's expression, cut at its matching parenthesis (#70: the same conversion comes once as
    /// <c>…=[@1]</c> and once as <c>…=(14417807)</c>, and the call alone folds them); the whole expression when it holds none.
    /// </summary>
    internal static string ConvertCall(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        int start = expression.IndexOf("CONVERT_IMPLICIT(", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return expression;
        }

        int depth = 0;
        for (int i = start + "CONVERT_IMPLICIT".Length; i < expression.Length; i++)
        {
            if (expression[i] == '(')
            {
                depth++;
            }
            else if (expression[i] == ')' && --depth == 0)
            {
                return expression[start..(i + 1)];
            }
        }

        return expression[start..];
    }

    /// <summary><c>[Sales].[Orders]</c> as <c>Sales.Orders</c>; the table alone when no schema is given.</summary>
    private static string Name(string? schema, string? table)
    {
        string bareTable = Bare(table);
        return schema is null ? bareTable : Bare(schema) + "." + bareTable;
    }

    private static string Bare(string? name) =>
        name is { Length: >= 2 } n && n[0] == '[' && n[^1] == ']' ? n[1..^1].Replace("]]", "]", StringComparison.Ordinal) : name ?? "?";
}
