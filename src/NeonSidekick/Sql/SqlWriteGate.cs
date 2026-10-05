using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace NeonSidekick.Sql;

/// <summary>
/// Whether <c>sql_execute</c> may run a text (2026-10-05, <c>SQL mode</c> <c>read-write</c>, the user's ask: SQLite's
/// <c>SqliteWriteGate</c> mirrored): parsed by ScriptDom, as <see cref="SqlReadOnlyGate"/> is — never matched by pattern, so T-SQL's
/// statements without <c>;</c> are seen for what they are. One batch, one statement (a <c>CREATE PROCEDURE</c>'s body is one), its
/// kind told by its type — an allow-list: a statement of a type this does not name is refused as unknown. Every data-changing part of
/// it counts (<see cref="Changes"/>: an <c>INSERT … SELECT FROM (MERGE … OUTPUT …)</c> deletes too; <c>INSERT … EXEC</c> runs a
/// procedure). Refused whatever is ticked: a transaction's own statements, <c>SET</c>/<c>USE</c>, <c>GRANT</c>/<c>DENY</c>/<c>REVOKE</c>,
/// <c>EXECUTE AS</c>, logins, users and roles, databases, <c>DBCC</c>, <c>BACKUP</c>/<c>RESTORE</c>, <c>BULK INSERT</c>, <c>KILL</c>,
/// <c>SHUTDOWN</c>, <c>RECONFIGURE</c>, <c>WAITFOR</c>; <c>EXEC</c> of a string (dynamic SQL), of <c>sp_executesql</c>, of an
/// <c>xp_</c> or <c>sp_</c> system procedure but the few that act on the database's own objects (<see cref="AllowedSystemProcedures"/>),
/// or <c>AT</c> a linked server; and what the read gate refuses anywhere (<c>OPENROWSET</c>, <c>OPENQUERY</c>,
/// <c>OPENDATASOURCE</c>, <c>OPENXML</c>, a four-part name) — bar <c>NEXT VALUE FOR</c>, which an insert may use.
/// </summary>
public static class SqlWriteGate
{
    /// <summary>The <c>sp_</c> procedures <c>EXEC</c> may run: they act on the database's own objects. Every other <c>sp_</c> and <c>xp_</c> one is refused.</summary>
    public static readonly IReadOnlySet<string> AllowedSystemProcedures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sp_rename", "sp_recompile", "sp_updatestats", "sp_refreshview", "sp_refreshsqlmodule", "sp_help", "sp_helptext", "sp_helpindex", "sp_columns",
        "sp_addextendedproperty", "sp_updateextendedproperty", "sp_dropextendedproperty",
    };

    /// <summary>Null when <paramref name="sql"/> may run under <paramref name="allowed"/> (null allows every kind); else the <c>Error:</c> sentence.</summary>
    public static string? Check(string sql, IReadOnlyList<ServerStatementKind>? allowed = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var family = SqlStatementKinds.Family;
        if (Parse(sql, out var error) is not { } statement)
        {
            return error;
        }

        var forbidden = new Forbidden();
        statement.Accept(forbidden);
        if (forbidden.Found is { } found)
        {
            return ServerWriteText.Forbidden(family, found.What, found.Why);
        }

        if (Refusal(statement) is { } refused)
        {
            return ServerWriteText.Forbidden(family, refused.What, refused.Why);
        }

        if (Kinds(statement) is not { } kinds)
        {
            return ServerWriteText.UnknownStatement(family, SqlReadOnlyGate.StatementWord(statement));
        }

        if (allowed is not null)
        {
            foreach (var kind in kinds)
            {
                if (!allowed.Contains(kind))
                {
                    return ServerWriteText.KindNotAllowed(family, kind, allowed);
                }
            }
        }

        return null;
    }

    /// <summary>The kinds <paramref name="sql"/> needs; null when it does not parse, is refused, or is none <c>sql_execute</c> runs.</summary>
    public static IReadOnlyList<ServerStatementKind>? Kinds(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (Parse(sql, out _) is not { } statement || Refusal(statement) is not null)
        {
            return null;
        }

        var forbidden = new Forbidden();
        statement.Accept(forbidden);
        return forbidden.Found is null ? Kinds(statement) : null;
    }

    /// <summary>The one statement of one batch, or null with the <c>Error:</c> sentence.</summary>
    private static TSqlStatement? Parse(string sql, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(sql))
        {
            error = ServerWriteText.NoStatement;
            return null;
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
            error = SqlText.ParseError(errors[0].Line, errors[0].Column, errors[0].Message);
            return null;
        }

        if (fragment is not TSqlScript script || script.Batches.Count == 0 || script.Batches.Sum(b => b.Statements.Count) == 0)
        {
            error = ServerWriteText.NoStatement;
            return null;
        }

        int count = script.Batches.Sum(b => b.Statements.Count);
        if (script.Batches.Count > 1 || count > 1)
        {
            error = ServerWriteText.NotOneStatement(SqlStatementKinds.Family, Math.Max(2, count));
            return null;
        }

        return script.Batches[0].Statements[0];
    }

    /// <summary>What is refused by the statement's type, with why; null for the rest.</summary>
    private static (string What, string Why)? Refusal(TSqlStatement statement)
    {
        string word = SqlReadOnlyGate.StatementWord(statement);
        string type = statement.GetType().Name;
        return statement switch
        {
            BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement or SaveTransactionStatement or BeginEndBlockStatement
                => (word, ServerWriteText.OwnTransaction),
            GrantStatement or DenyStatement or RevokeStatement or ExecuteAsStatement or RevertStatement or AlterAuthorizationStatement
                => (word, ServerWriteText.Accounts),
            UseStatement or PredicateSetStatement => (word, ServerWriteText.Session),
            _ when type.StartsWith("Set", StringComparison.Ordinal) => (word, ServerWriteText.Session),   // SET @v, SET IDENTITY_INSERT, SET ROWCOUNT, …
            BulkInsertStatement or InsertBulkStatement => (word, ServerWriteText.Outside),
            WaitForStatement => (word, ServerWriteText.Denied),
            DbccStatement or BackupStatement or RestoreStatement or KillStatement or ShutdownStatement or ReconfigureStatement or CheckpointStatement
                => (word, ServerWriteText.ServerWide),
            ExecuteStatement execute => ExecuteRefusal(execute.ExecuteSpecification),
            _ when type.Contains("Login", StringComparison.Ordinal) || type.Contains("User", StringComparison.Ordinal) || type.Contains("Role", StringComparison.Ordinal)
                || type.Contains("Certificate", StringComparison.Ordinal) || type.Contains("Credential", StringComparison.Ordinal) || type.Contains("Key", StringComparison.Ordinal)
                => (word, ServerWriteText.Accounts),
            _ when type.Contains("Database", StringComparison.Ordinal) || type.Contains("Server", StringComparison.Ordinal) || type.Contains("Endpoint", StringComparison.Ordinal)
                || type.StartsWith("AlterResourceGovernor", StringComparison.Ordinal) || type.Contains("EventSession", StringComparison.Ordinal)
                => (word, ServerWriteText.ServerWide),
            _ => null,
        };
    }

    /// <summary>An <c>EXEC</c>'s refusal: a string (dynamic SQL), <c>AT</c> a linked server, <c>sp_executesql</c>, a system procedure not allowed.</summary>
    private static (string What, string Why)? ExecuteRefusal(ExecuteSpecification? specification)
    {
        if (specification is null)
        {
            return null;
        }

        if (specification.LinkedServer is not null)
        {
            return ("EXEC … AT a linked server", ServerWriteText.Outside);
        }

        switch (specification.ExecutableEntity)
        {
            case ExecutableStringList:
                return ("EXEC of a string", ServerWriteText.Dynamic);
            case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: { } name }:
                if (name.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
                {
                    return ("sp_executesql", ServerWriteText.Dynamic);
                }

                if ((name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)) && !AllowedSystemProcedures.Contains(name))
                {
                    return (name, ServerWriteText.Denied);
                }

                return null;
            case ExecutableProcedureReference:
                return ("EXEC of a procedure named by a variable", ServerWriteText.Dynamic);
            default:
                return null;
        }
    }

    /// <summary>The kinds the statement needs: its own by type, and every data change inside it; null for a type this does not run.</summary>
    private static IReadOnlyList<ServerStatementKind>? Kinds(TSqlStatement statement)
    {
        string type = statement.GetType().Name;
        ServerStatementKind? own = statement switch
        {
            SelectStatement { Into: null } => ServerStatementKind.Read,
            SelectStatement => ServerStatementKind.Create,   // SELECT … INTO makes a table
            InsertStatement or UpdateStatement or MergeStatement => ServerStatementKind.Data,
            DeleteStatement or TruncateTableStatement => ServerStatementKind.Delete,
            UpdateStatisticsStatement or CreateStatisticsStatement or AlterIndexStatement => ServerStatementKind.Upkeep,
            ExecuteStatement => ServerStatementKind.Procedures,
            CreateProcedureStatement or CreateOrAlterProcedureStatement or AlterProcedureStatement
                or CreateFunctionStatement or CreateOrAlterFunctionStatement or AlterFunctionStatement
                or CreateTriggerStatement or CreateOrAlterTriggerStatement or AlterTriggerStatement or EnableDisableTriggerStatement => ServerStatementKind.Procedures,
            CreateTableStatement or CreateIndexStatement or CreateColumnStoreIndexStatement or CreateXmlIndexStatement or CreateSpatialIndexStatement
                or CreateViewStatement or CreateOrAlterViewStatement or CreateSequenceStatement or CreateTypeTableStatement or CreateTypeUddtStatement
                or CreateSchemaStatement or CreateSynonymStatement => ServerStatementKind.Create,
            AlterViewStatement or AlterSequenceStatement or AlterSchemaStatement => ServerStatementKind.Alter,
            _ when type.StartsWith("AlterTable", StringComparison.Ordinal) => ServerStatementKind.Alter,
            DropTableStatement or DropIndexStatement or DropViewStatement or DropSequenceStatement or DropTypeStatement or DropSchemaStatement
                or DropSynonymStatement or DropProcedureStatement or DropFunctionStatement or DropTriggerStatement or DropStatisticsStatement => ServerStatementKind.Drop,
            _ => null,
        };
        if (own is not { } kind)
        {
            return null;
        }

        var found = new HashSet<ServerStatementKind> { kind };
        if (kind is ServerStatementKind.Data or ServerStatementKind.Delete || statement is SelectStatement)
        {
            var changes = new Changes();
            statement.Accept(changes);
            found.UnionWith(changes.Kinds);
            if (statement is MergeStatement && changes.Kinds.Count > 0)
            {
                found = changes.Kinds;   // a MERGE is what its branches do
            }

            if (found.Count > 1)
            {
                found.Remove(ServerStatementKind.Read);
            }
        }

        return Enum.GetValues<ServerStatementKind>().Where(found.Contains).ToList();
    }

    /// <summary>Every data change inside a statement: inserts and updates, deletes, a MERGE's branches, and a procedure an INSERT … EXEC runs.</summary>
    private sealed class Changes : TSqlFragmentVisitor
    {
        public HashSet<ServerStatementKind> Kinds { get; } = [];

        public override void Visit(InsertSpecification node)
        {
            Kinds.Add(ServerStatementKind.Data);
            base.Visit(node);
        }

        public override void Visit(UpdateSpecification node)
        {
            Kinds.Add(ServerStatementKind.Data);
            base.Visit(node);
        }

        public override void Visit(DeleteSpecification node)
        {
            Kinds.Add(ServerStatementKind.Delete);
            base.Visit(node);
        }

        public override void Visit(InsertMergeAction node)
        {
            Kinds.Add(ServerStatementKind.Data);
            base.Visit(node);
        }

        public override void Visit(UpdateMergeAction node)
        {
            Kinds.Add(ServerStatementKind.Data);
            base.Visit(node);
        }

        public override void Visit(DeleteMergeAction node)
        {
            Kinds.Add(ServerStatementKind.Delete);
            base.Visit(node);
        }

        public override void Visit(ExecuteInsertSource node)
        {
            Kinds.Add(ServerStatementKind.Procedures);
            base.Visit(node);
        }
    }

    /// <summary>What is refused anywhere in the statement: the read gate's doors out (not <c>NEXT VALUE FOR</c>), and an <c>INSERT … EXEC</c> of a string or a system procedure.</summary>
    private sealed class Forbidden : TSqlFragmentVisitor
    {
        public (string What, string Why)? Found { get; private set; }

        private void Mark(string what, string why) => Found ??= (what, why);

        public override void Visit(OpenRowsetTableReference node) => Mark("OPENROWSET", ServerWriteText.Outside);

        public override void Visit(BulkOpenRowset node) => Mark("OPENROWSET(BULK …)", ServerWriteText.Outside);

        public override void Visit(InternalOpenRowset node) => Mark("OPENROWSET", ServerWriteText.Outside);

        public override void Visit(OpenQueryTableReference node) => Mark("OPENQUERY", ServerWriteText.Outside);

        public override void Visit(AdHocTableReference node) => Mark("OPENDATASOURCE", ServerWriteText.Outside);

        public override void Visit(OpenXmlTableReference node) => Mark("OPENXML", ServerWriteText.Outside);

        public override void Visit(ExecuteSpecification node)
        {
            if (ExecuteRefusal(node) is { } refused)
            {
                Mark(refused.What, refused.Why);
            }

            base.Visit(node);
        }

        public override void Visit(SchemaObjectName node)
        {
            if (node.ServerIdentifier is not null)
            {
                Mark("a linked server (a four-part name)", ServerWriteText.Outside);
            }

            base.Visit(node);
        }
    }
}

/// <summary>What each statement kind covers in SQL Server (2026-10-05), and the family as the write path names it. Pinned.</summary>
public static class SqlStatementKinds
{
    public static string Statements(ServerStatementKind kind) => kind switch
    {
        ServerStatementKind.Data => "INSERT, UPDATE, MERGE",
        ServerStatementKind.Delete => "DELETE, TRUNCATE TABLE, a MERGE that deletes",
        ServerStatementKind.Create => "CREATE TABLE, INDEX, VIEW, SEQUENCE, TYPE, SCHEMA, SYNONYM; SELECT … INTO",
        ServerStatementKind.Alter => "ALTER TABLE, VIEW, SEQUENCE, SCHEMA",
        ServerStatementKind.Drop => "DROP TABLE, INDEX, VIEW, SEQUENCE, TYPE, SCHEMA, SYNONYM, PROCEDURE, FUNCTION, TRIGGER",
        ServerStatementKind.Upkeep => "UPDATE STATISTICS, CREATE STATISTICS, ALTER INDEX",
        ServerStatementKind.Procedures => "EXEC of a procedure (sp_rename and a few system ones); CREATE or ALTER PROCEDURE, FUNCTION, TRIGGER",
        _ => "SELECT",
    };

    public static readonly ServerWriteFamily Family = new(
        "SQL", "SQL Server", "sql_execute", "sql.json", "database", SqlConfigFile.Category,
        nameof(Settings.AppSettingsData.SqlMode), Statements);
}
