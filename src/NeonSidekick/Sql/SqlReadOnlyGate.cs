using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace NeonSidekick.Sql;

/// <summary>
/// Whether <c>sql_query</c> may run a text (2026-09-23): parsed by ScriptDom — the T-SQL parser SSMS and
/// SqlPackage use, fully managed, fine under NativeAOT in the spike — never matched by pattern. The
/// <c>mcp-mssql-read</c> server this family follows checked the first word and walked parentheses, and let
/// through <c>SELECT 1 DELETE FROM t</c> (T-SQL needs no <c>;</c>), <c>SELECT … INTO</c>, a CTE ahead of a
/// <c>DELETE</c> and an <c>EXEC</c> after a <c>SELECT</c>; a parse sees each for what it is. Allowed: one batch,
/// one statement, a <see cref="SelectStatement"/> (a CTE's included) with no <c>INTO</c>, reaching nothing
/// outside the server (<c>OPENROWSET</c>, <c>OPENQUERY</c>, <c>OPENDATASOURCE</c>, <c>OPENROWSET(BULK …)</c> — a
/// file on the server's disk — or a linked server's four-part name) and bumping no sequence (<c>NEXT VALUE FOR</c> is not rolled back). The gate is
/// the first of three layers: the call also runs in a transaction that is always rolled back, with read-only
/// intent (<see cref="SqlAccess"/>), and a read-only login is the guard the README asks for.
/// </summary>
public static class SqlReadOnlyGate
{
    /// <summary>Null when <paramref name="sql"/> may run; else the <c>Error:</c> sentence the model reads.</summary>
    public static string? Check(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlText.NoSql;
        }

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(sql))
        {
            fragment = parser.Parse(reader, out errors);
        }

        if (errors.Count > 0)
        {
            var first = errors[0];
            return SqlText.ParseError(first.Line, first.Column, first.Message);
        }

        if (fragment is not TSqlScript script || script.Batches.Count == 0)
        {
            return SqlText.NoSql;
        }

        if (script.Batches.Count > 1)
        {
            return SqlText.NotOneStatement(script.Batches.Sum(b => b.Statements.Count));
        }

        var statements = script.Batches[0].Statements;
        if (statements.Count != 1)
        {
            return statements.Count == 0 ? SqlText.NoSql : SqlText.NotOneStatement(statements.Count);
        }

        if (statements[0] is not SelectStatement select)
        {
            return SqlText.NotASelect(StatementWord(statements[0]));
        }

        if (select.Into is not null)
        {
            return SqlText.SelectInto;
        }

        var visitor = new Forbidden();
        select.Accept(visitor);
        return visitor.Found is { } found ? SqlText.Forbidden(found) : null;
    }

    /// <summary>What the model is told it wrote: the statement's type name less <c>Statement</c>, in words (<c>DeleteStatement</c> → <c>DELETE</c>).</summary>
    public static string StatementWord(TSqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        string name = statement.GetType().Name;
        if (name.EndsWith("Statement", StringComparison.Ordinal))
        {
            name = name[..^"Statement".Length];
        }

        var words = new System.Text.StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                words.Append(' ');
            }

            words.Append(char.ToUpperInvariant(name[i]));
        }

        return words.ToString();
    }

    /// <summary>The first construct in a SELECT that reaches outside the server or changes state no rollback undoes.</summary>
    private sealed class Forbidden : TSqlFragmentVisitor
    {
        public string? Found { get; private set; }

        private void Mark(string what) => Found ??= what;

        public override void Visit(OpenRowsetTableReference node) => Mark("OPENROWSET");

        public override void Visit(BulkOpenRowset node) => Mark("OPENROWSET(BULK …)");

        public override void Visit(InternalOpenRowset node) => Mark("OPENROWSET");

        public override void Visit(OpenQueryTableReference node) => Mark("OPENQUERY");

        public override void Visit(AdHocTableReference node) => Mark("OPENDATASOURCE");

        public override void Visit(OpenXmlTableReference node) => Mark("OPENXML");

        public override void Visit(NextValueForExpression node) => Mark("NEXT VALUE FOR");

        /// <summary>
        /// A locking table hint (2026-10-24, the database review's B1): TABLOCKX, XLOCK, UPDLOCK, HOLDLOCK (and its SERIALIZABLE and
        /// REPEATABLEREAD kin) and PAGLOCK take or keep locks to the transaction's end — an exclusive one blocks every other user until the
        /// rollback or the timeout.
        /// </summary>
        public override void Visit(TableHint node)
        {
            if (node.HintKind is TableHintKind.TabLockX or TableHintKind.XLock or TableHintKind.UpdLock or TableHintKind.HoldLock
                or TableHintKind.Serializable or TableHintKind.RepeatableRead or TableHintKind.PagLock)
            {
                Mark(node.HintKind.ToString().ToUpperInvariant() + " (a locking hint)");
            }

            base.Visit(node);
        }

        /// <summary>A four-part name (<c>server.db.schema.table</c>) is a linked server: another machine, under whatever login the link maps to.</summary>
        public override void Visit(SchemaObjectName node)
        {
            if (node.ServerIdentifier is not null)
            {
                Mark("a linked server (a four-part name)");
            }

            base.Visit(node);
        }
    }
}
