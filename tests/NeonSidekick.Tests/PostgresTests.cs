using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Postgres;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using Npgsql;

namespace NeonSidekick.Tests;

/// <summary>The PostgreSQL gate (2026-10-04): one read-only statement, lexed by PostgreSQL's rules.</summary>
public sealed class PostgresReadOnlyGateTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select * from orders where note = 'drop table x; delete' ;")]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT x FROM c")]
    [InlineData("VALUES (1, 2)")]
    [InlineData("TABLE public.orders")]
    [InlineData("SELECT \"update\", \"Into\" FROM t -- INSERT in a comment\n")]
    [InlineData("SELECT /* outer /* inner ; DELETE */ still a comment */ 1")]
    [InlineData("SELECT $$it's; DROP$$, $tag$ ; $tag$")]
    [InlineData("SELECT E'it\\'s; fine', substring('abc' FROM 1 FOR 2)")]
    [InlineData("SELECT * FROM t WHERE id = @id AND tags @> ARRAY['a'] AND x::text = @name")]
    [InlineData("SELECT \"pg_read_file\", \"nextval\" FROM t")]
    public void Allows_OneRead(string sql) => Assert.Null(PostgresReadOnlyGate.Check(sql));

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", "starts with INSERT")]
    [InlineData("SELECT 1; SELECT 2", "is 2 statements")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d", "uses DELETE")]
    [InlineData("SELECT * INTO copy FROM t", "SELECT … INTO makes a table")]
    [InlineData("SELECT * FROM t FOR SHARE", "FOR SHARE")]
    [InlineData("SELECT * FROM t FOR NO KEY UPDATE", "FOR NO")]
    [InlineData("DO $$ BEGIN DROP TABLE t; END $$", "starts with DO")]
    [InlineData("COPY t TO '/tmp/x'", "starts with COPY")]
    [InlineData("SELECT pg_read_file('/etc/passwd')", "pg_read_file()")]
    [InlineData("SELECT lo_import('/etc/passwd')", "lo_import()")]
    [InlineData("SELECT nextval('s')", "nextval()")]
    [InlineData("SELECT pg_advisory_lock(1)", "pg_advisory_lock()")]
    [InlineData("SELECT dblink_exec('x', 'DROP TABLE t')", "dblink_exec()")]
    [InlineData("SELECT \"pg_read_file\"('/etc/passwd')", "pg_read_file()")]
    [InlineData("SELECT pg_catalog.\"set_config\"('search_path', 'x', false)", "set_config()")]
    [InlineData("SELECT \"pg_terminate_backend\"(pid) FROM pg_stat_activity", "pg_terminate_backend()")]
    [InlineData("SELECT \"dblink_exec\"('dbname=postgres', 'DROP TABLE t')", "dblink_exec()")]
    [InlineData("SELECT * FROM pg_ls_waldir()", "pg_ls_waldir()")]
    [InlineData("SELECT * FROM pg_ls_logdir()", "pg_ls_logdir()")]
    [InlineData("SELECT * FROM pg_ls_dir('.')", "pg_ls_dir()")]
    [InlineData("SELECT * FROM t WHERE id = $1", "$1 placeholder")]
    [InlineData("SELECT U&'\\0041'", "U& string")]
    [InlineData("SELECT /* /* nested */ 1", "a comment never ends")]
    [InlineData("SELECT $q$ never closed", "a dollar-quoted string never ends")]
    [InlineData("SELECT E'\\' never ends", "a string never ends")]
    [InlineData("SET ROLE postgres", "starts with SET")]
    [InlineData("SELECT * FROM ts_stat('SELECT pg_terminate_backend(pid)::text::tsvector FROM pg_stat_activity')", "ts_stat()")]
    [InlineData("SELECT ts_rewrite('a & b'::tsquery, 'SELECT t, s FROM aliases')", "ts_rewrite()")]
    [InlineData("SELECT query_to_xmlschema('SELECT pg_reload_conf()', true, true, '')", "query_to_xmlschema()")]
    [InlineData("SELECT * FROM crosstab('SELECT pg_cancel_backend(1), 1, 1') AS c(a int, b int)", "crosstab()")]
    [InlineData("SELECT pg_create_logical_replication_slot('x', 'test_decoding')", "pg_create_logical_replication_slot()")]
    [InlineData("SELECT pg_create_physical_replication_slot('x')", "pg_create_physical_replication_slot()")]
    [InlineData("SELECT pg_drop_replication_slot('x')", "pg_drop_replication_slot()")]
    [InlineData("SELECT * FROM pg_logical_slot_get_changes('x', NULL, NULL)", "pg_logical_slot_get_changes()")]
    [InlineData("SELECT pg_stat_reset()", "pg_stat_reset()")]
    [InlineData("SELECT pg_stat_reset_shared('bgwriter')", "pg_stat_reset_shared()")]
    [InlineData("SELECT pg_stat_statements_reset()", "pg_stat_statements_reset()")]
    [InlineData("SELECT pg_backup_start('x')", "pg_backup_start()")]
    [InlineData("SELECT pg_wal_replay_pause()", "pg_wal_replay_pause()")]
    [InlineData("SELECT * FROM t WHERE v @@pg_read_file('/etc/passwd')::tsquery", "pg_read_file()")]
    public void Refuses_TheRest_WithTheSentence(string sql, string part)
    {
        string? refused = PostgresReadOnlyGate.Check(sql);
        Assert.NotNull(refused);
        Assert.StartsWith("Error: ", refused);
        Assert.Contains(part, refused);
    }

    [Fact]
    public void Binds_AreTheAtNames_AndBodyDropsTheSemicolon()
    {
        Assert.Equal(["id", "name"], PostgresReadOnlyGate.Binds("SELECT @id, @name, @ID, '@not', x @> y"));

        // Straight after an operator character an @name is a placeholder only when params names it: <@tags and @@q are the
        // operators' tails and a column, id=@id is a placeholder once named (Npgsql rewrites it then).
        const string Mixed = "SELECT * FROM t WHERE ARRAY['x'] <@tags AND v @@q AND id=@id AND n = @n";
        Assert.Equal(["n"], PostgresReadOnlyGate.Binds(Mixed));
        Assert.Equal(["id", "n"], PostgresReadOnlyGate.Binds(Mixed, ["ID", "n"]));

        // The unbound ones are the likely cause of an operator or syntax error, said after the server's message (the third
        // 2026-10-04 review: id=@id with no params failed with "operator does not exist: integer =@ integer" alone).
        Assert.Equal(["tags", "q", "id"], PostgresReadOnlyGate.UnboundOperatorBinds(Mixed));
        Assert.Equal(["tags", "q"], PostgresReadOnlyGate.UnboundOperatorBinds(Mixed, ["ID"]));
        Assert.Equal([("id", "<>@"), ("q", "@@")], PostgresReadOnlyGate.UnboundOperators("SELECT * FROM t WHERE id<>@id AND v @@q AND x = @x"));
        const string NoOperator = "operator does not exist: integer =@ integer";
        Assert.Contains("@id", PostgresAccess.OperatorBindHint(PostgresAccess.UndefinedFunction, NoOperator, ["SELECT 1", "SELECT * FROM t WHERE id=@id"], []));
        Assert.Contains("@id", PostgresAccess.OperatorBindHint(PostgresAccess.SyntaxError, "syntax error at or near \"=@\"", ["SELECT * FROM t WHERE id=@id"], ["n"]));
        Assert.Equal("", PostgresAccess.OperatorBindHint(PostgresAccess.UndefinedFunction, NoOperator, ["SELECT * FROM t WHERE id=@id"], ["id"]));
        Assert.Equal("", PostgresAccess.OperatorBindHint("42P01", NoOperator, ["SELECT * FROM t WHERE id=@id"], []));

        // Only an error about that operator gets the hint (the fourth 2026-10-04 review): a misspelt function beside a correct
        // full-text match is not told to bind to_tsquery, and an error about =@ names id alone, not the @@ beside it.
        const string FullText = "SELECT * FROM docs WHERE v @@to_tsquery('x') AND misspelt_fn(1) > 0";
        Assert.Equal("", PostgresAccess.OperatorBindHint(PostgresAccess.UndefinedFunction, "function misspelt_fn(integer) does not exist", [FullText], []));
        Assert.Equal(PostgresText.UnboundOperatorBind(["id"]), PostgresAccess.OperatorBindHint(PostgresAccess.UndefinedFunction, NoOperator, [FullText + " AND id=@id"], []));
        Assert.Equal("", PostgresAccess.OperatorBindHint(PostgresAccess.UndefinedFunction, "operator does not exist: integer =@@ integer", ["SELECT * FROM t WHERE id=@id"], []));
        Assert.Null(PostgresReadOnlyGate.Check(Mixed));
        Assert.Equal("SELECT 1", PostgresReadOnlyGate.Body("SELECT 1 ; "));
    }
}

/// <summary>The PostgreSQL family without a server (2026-10-04): the entry, the file, the wording, the tools' refusals, the offer and the rules.</summary>
public sealed class PostgresToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-postgres-").FullName;
    private readonly AppSettingsData _settings = new() { PostgresTools = true, PostgresConnectionsOffered = ["shop"] };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static PostgresConnectionConfig Config() => new() { Host = "db", Database = "shop", User = "reader", Password = "p" };

    [Fact]
    public void AnEntry_SaysWhatIsWrong_AndBuildsAReadOnlySession()
    {
        Assert.Null(Config().Problem);
        Assert.Equal(PostgresText.NoHost, new PostgresConnectionConfig { User = "u" }.Problem);
        Assert.Equal(PostgresText.NoDatabase, new PostgresConnectionConfig { Host = "h", User = "u" }.Problem);   // required since 2026-10-09
        Assert.Equal(PostgresText.NoUser, new PostgresConnectionConfig { Host = "h", Database = "d" }.Problem);
        Assert.Equal(PostgresText.BadPort(0), new PostgresConnectionConfig { Host = "h", Database = "d", User = "u", Port = 0 }.Problem);
        Assert.Equal(PostgresText.BadSslMode("maybe"), new PostgresConnectionConfig { Host = "h", Database = "d", User = "u", SslMode = "maybe" }.Problem);
        Assert.Equal(SqlText.BadPasswordStore("vault"), new PostgresConnectionConfig { Host = "h", Database = "d", User = "u", PasswordStore = "vault" }.Problem);
        Assert.Equal(PostgresText.BadSchemaKey("a b"), new PostgresConnectionConfig { Host = "h", Database = "d", User = "u", Schema = "a b" }.Problem);

        var builder = new PostgresConnectionConfig { Host = "db", Database = "shop", User = "reader", SslMode = "verify-full" }.Builder("secret", 30);
        Assert.Equal(("db", 5432, "shop", "reader", "secret", false), (builder.Host, builder.Port, builder.Database, builder.Username, builder.Password, builder.Pooling));
        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
        Assert.Contains("-c default_transaction_read_only=on", builder.Options);
        Assert.Contains("-c standard_conforming_strings=on", builder.Options);
        Assert.Contains("-c statement_timeout=30000", builder.Options);
        Assert.DoesNotContain("search_path", builder.Options);   // every schema open without a lock

        // A schema lock (2026-10-09) is the session's whole search path: a plain name as it is, another quoted.
        Assert.EndsWith(" -c search_path=sales", new PostgresConnectionConfig { Host = "db", Database = "shop", User = "reader", Schema = " sales " }.Builder(null, 30).Options);
        Assert.EndsWith(" -c search_path=\"Sales\"", new PostgresConnectionConfig { Host = "db", Database = "shop", User = "reader", Schema = "Sales" }.Builder(null, 30).Options);
        Assert.Equal("db:5432", Config().Endpoint);
        Assert.Equal("NeonSidekick/postgres/shop", Config().CredentialTarget("shop"));
    }

    [Fact]
    public void TheFile_LoadsTheConnections_TheProfilesWin_AndAnAddKeepsTheComments()
    {
        string home = Path.Combine(_dir, "home");
        string profile = Path.Combine(home, "profiles", "default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(PostgresConfigFile.GlobalPath(home), """{ "connections": { "shop": { "host": "global", "database": "shop", "user": "u" }, "bad": { "user": "u" } } }""");
        Assert.Null(PostgresConfigFile.AddConnection(PostgresConfigFile.ProfilePath(profile), "shop", new PostgresConnectionConfig { Host = "local", Database = "shop", User = "reader", SslMode = "require" }));
        Assert.Contains("// One entry per connection.", File.ReadAllText(PostgresConfigFile.ProfilePath(profile)));

        var catalog = PostgresConfigFile.LoadCatalog(profile, home);
        var shop = Assert.Single(catalog.Connections);
        Assert.Equal(("local", "require"), (shop.Config.Host, shop.Config.SslMode));
        Assert.Equal(PostgresText.NoHost, Assert.Single(catalog.Problems).Reason);
        Assert.Equal(1, catalog.Offered([]).Hidden);
        Assert.Same(shop, catalog.Find("SHOP"));
        Assert.Same(shop, catalog.Find(null));   // the only one
    }

    [Fact]
    public void Split_TakesSchemaAndName_QuotesKeepTheDots()
    {
        Assert.Equal(("public", "orders"), PostgresAccess.Split("public.orders"));
        Assert.Equal(((string?)null, "orders"), PostgresAccess.Split(" orders "));
        Assert.Equal(("my.schema", "My \"Table\""), PostgresAccess.Split("\"my.schema\".\"My \"\"Table\"\"\""));
        Assert.Null(PostgresAccess.Split("a.b.c"));
        Assert.Null(PostgresAccess.Split("\"open"));
    }

    [Fact]
    public void Bind_TypesEveryValue_ANullAsText()
    {
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Text, PostgresAccess.Bind("x", null).NpgsqlDbType);
        Assert.Equal(DBNull.Value, PostgresAccess.Bind("x", null).Value);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Bigint, PostgresAccess.Bind("x", 5L).NpgsqlDbType);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Numeric, PostgresAccess.Bind("x", 1.5m).NpgsqlDbType);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Boolean, PostgresAccess.Bind("x", true).NpgsqlDbType);
        Assert.Equal("x", PostgresAccess.Bind("x", "v").ParameterName);
    }

    [Fact]
    public void TheWording_IsPinned()
    {
        var run = new SqlRun(SqlOutcome.Timeout, "30", "shop", "sales", [], TimeSpan.Zero);
        Assert.Equal("Error: the query on shop/sales ran past 30 s and was stopped; narrow it (WHERE, LIMIT, fewer joins)", PostgresText.Error(run));
        Assert.Equal(PostgresText.NoConnections, PostgresText.Error(SqlRun.Refused(SqlOutcome.NoConnections, "")));
        Assert.Equal("Error: no PostgreSQL connection is named 'x'; the connections are a, b", PostgresText.Error(SqlRun.Refused(SqlOutcome.UnknownConnection, "a, b", "x")));
        Assert.Equal("Connected to 'shop' as reader in sales: PostgreSQL 17.2", PostgresText.TestOk("shop", "reader", "sales", "PostgreSQL 17.2"));
        Assert.Equal("This account can change data (superuser, INSERT). The tools never will, but a SELECT-only role is the real guard.", PostgresText.CanWrite(["superuser", "INSERT"]));

        var describe = new SqlRun(SqlOutcome.Ok, "", "shop", "sales",
        [
            new SqlGrid(["c", "t", "n", "d", "pk", "comment"], [["id", "integer", "false", "NULL", "1", "NULL"], ["total", "numeric(12,2)", "true", "0", "NULL", "the sum"]], false),
            new SqlGrid(["name", "from", "to", "cols"], [["orders_customer_fk", "customer_id", "customers", "id"]], false),
            new SqlGrid(["from", "cols", "to"], [["lines", "order_id", "id"]], false),
            new SqlGrid(["name", "def"], [["orders_pkey", "CREATE UNIQUE INDEX orders_pkey ON public.orders USING btree (id)"]], false),
            new SqlGrid(["name", "def"], [["total_ck", "CHECK ((total >= (0)::numeric))"]], false),
            new SqlGrid(["comment"], [["Orders placed"]], false),
        ], TimeSpan.Zero);
        string text = PostgresText.Describe("public", "orders", "table", describe, 10_000);
        Assert.StartsWith("public.orders (table, 2 columns) in shop/sales\nOrders placed\n", text);
        Assert.Contains("| id | integer | no | PK 1 |  |  |", text);
        Assert.Contains("| total | numeric(12,2) | yes |  | 0 | the sum |", text);
        Assert.Contains("Foreign keys:\n- orders_customer_fk: (customer_id) → customers (id)", text);
        Assert.Contains("Referenced by:\n- lines (order_id) → (id)", text);
        Assert.Contains("Indexes:\n- CREATE UNIQUE INDEX orders_pkey", text);
        Assert.Contains("Checks:\n- total_ck: CHECK", text);
    }

    [Fact]
    public async Task TheTools_RefuseBeforeAnyServer()
    {
        var catalog = new PostgresCatalog([new PostgresNamedConnection("shop", Config(), "x.json")], []);
        var access = new PostgresAccess(() => catalog.Offered(_settings.PostgresConnectionsOffered));
        var tools = ChatScreen.PostgresTools(access, () => _settings);
        Assert.Equal(9, tools.Count);   // the eight reads (postgres_databases gone 2026-10-09) and postgres_execute (2026-10-05), which plan mode drops
        Assert.Equal(ChatScreen.PostgresToolNames, tools.Select(t => t.Name).ToHashSet());
        Assert.All(tools.Where(t => t is not PostgresExecuteTool), t => Assert.True(PlanTools.Allowed(t.Name), t.Name));
        Assert.StartsWith("1 PostgreSQL connection (every PostgreSQL tool", tools.OfType<PostgresConnectionsTool>().Single().Describe());

        var query = tools.OfType<PostgresQueryTool>().Single();
        Assert.StartsWith("Error: the SQL starts with DELETE", await query.RunAsync("DELETE FROM t", null, [], null, CancellationToken.None));
        Assert.Equal(SqlText.BadMaxRows(PostgresQueryTool.MinRows, PostgresQueryTool.MaxRows), await query.RunAsync("SELECT 1", null, [], 0, CancellationToken.None));
        Assert.Equal(PostgresText.NoPattern, await tools.OfType<PostgresColumnsTool>().Single().InvokeAsync(new AIFunctionArguments()));
        Assert.Equal(PostgresText.NoTable, await tools.OfType<PostgresDescribeTool>().Single().DescribeAsync(" ", null, null, CancellationToken.None));
        Assert.Equal(PostgresText.BadTable("a.b.c"), await tools.OfType<PostgresDescribeTool>().Single().DescribeAsync("a.b.c", null, null, CancellationToken.None));

        _settings.PostgresConnectionsOffered = null;
        Assert.Equal(PostgresText.NoConnections, await tools.OfType<PostgresTablesTool>().Single().DescribeAsync(null, null, null, CancellationToken.None));
        Assert.False(ChatScreen.PostgresOffered(_settings, access));
        _settings.PostgresConnectionsOffered = ["shop"];
        Assert.True(ChatScreen.PostgresOffered(_settings, access));
        Assert.Equal("PostgreSQL · db:5432 / shop", Assert.Single(ChatScreen.PostgresChoices(catalog)).Note);
    }

    /// <summary>
    /// The lock (2026-10-09): every tool refuses a <c>database</c>; the cluster's own catalogs are refused before any server; under a
    /// schema lock another schema — as a tool's <c>schema</c>, a <c>schema.name</c> table or <c>information_schema</c> in the SQL — is
    /// refused at once, and a qualifier the server must be asked about sends for the schemas' names first.
    /// </summary>
    [Fact]
    public async Task TheTools_KeepToTheConnectionsDatabase_AndALockedSchema()
    {
        var config = new PostgresConnectionConfig { Host = "127.0.0.1", Port = 1, Database = "shop", User = "reader", Password = "p", ConnectTimeoutSeconds = 1 };
        var catalog = new PostgresCatalog([new PostgresNamedConnection("shop", config, "x.json")], []);
        var tools = ChatScreen.PostgresTools(new PostgresAccess(() => catalog.Offered(_settings.PostgresConnectionsOffered)), () => _settings);
        foreach (var tool in tools)
        {
            Assert.Equal(PostgresText.NoDatabaseArgument, (string)(await tool.InvokeAsync(new AIFunctionArguments { ["sql"] = "SELECT 1", ["table"] = "t", ["pattern"] = "x", ["database"] = "other" }))!);
        }

        var query = tools.OfType<PostgresQueryTool>().Single();
        Assert.Equal(PostgresText.ServerCatalog("pg_stat_activity"), await query.RunAsync("SELECT query FROM pg_stat_activity", null, [], null, CancellationToken.None));
        Assert.Equal(PostgresText.ServerCatalog("pg_database"), await query.RunAsync("SELECT datname FROM pg_catalog.pg_database", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: could not connect to shop", await query.RunAsync("SELECT * FROM public.t", null, [], null, CancellationToken.None));   // no lock: no names needed
        Assert.Contains("- shop: 127.0.0.1:1 / shop, user reader", tools.OfType<PostgresConnectionsTool>().Single().Describe());

        config.Schema = "sales";
        Assert.Contains("- shop: 127.0.0.1:1 / shop (schema sales alone), user reader", tools.OfType<PostgresConnectionsTool>().Single().Describe());
        Assert.Equal(PostgresText.SchemaElsewhere("public", "sales"), await tools.OfType<PostgresTablesTool>().Single().DescribeAsync(null, "public", null, CancellationToken.None));
        Assert.Equal(PostgresText.SchemaElsewhere("public", "sales"), await tools.OfType<PostgresDescribeTool>().Single().DescribeAsync("public.orders", null, null, CancellationToken.None));
        Assert.Equal(PostgresText.SchemaElsewhere("public", "sales"), (string)(await tools.OfType<PostgresIndexesTool>().Single().InvokeAsync(new AIFunctionArguments { ["schema"] = "public" }))!);
        Assert.Equal(PostgresText.OtherSchema("information_schema", "sales"), await query.RunAsync("SELECT * FROM information_schema.tables", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: could not connect to shop", await query.RunAsync("SELECT * FROM sales.orders o WHERE o.id = 1", null, [], null, CancellationToken.None));   // the alias sends for the names
        Assert.StartsWith("Error: could not connect to shop", await tools.OfType<PostgresDescribeTool>().Single().DescribeAsync("SALES.orders", null, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("SELECT * FROM archive.orders", "sales", "archive")]
    [InlineData("SELECT * FROM \"ARCHIVE\".orders", "sales", null)]   // quoted, as written: no schema "ARCHIVE" (the review, 2026-10-09)
    [InlineData("SELECT * FROM shop.archive.orders", "sales", "archive")]   // a three-part name's schema in the middle (the review)
    [InlineData("SELECT * FROM shop.sales.orders", "sales", null)]
    [InlineData("SELECT o.* FROM orders o", "sales", null)]
    [InlineData("SELECT o.id FROM orders o", "sales", null)]
    [InlineData("SELECT * FROM orders AS archive WHERE archive.id = 1", "sales", "archive")]
    [InlineData("SELECT sales.orders.id FROM sales.orders", "sales", null)]
    [InlineData("SELECT pg_catalog.now(), x::text FROM orders", "sales", null)]
    [InlineData("SELECT * FROM archive.orders", null, null)]
    public void TheBoundary_HoldsALockedSchema(string sql, string? schema, string? other)
    {
        string[] visible = ["sales", "archive", "public", "pg_catalog", "information_schema"];
        Assert.Equal(other is null ? null : PostgresText.OtherSchema(other, schema!), PostgresBoundary.Check(sql, schema, visible));
    }

    /// <summary>
    /// What the review of 2026-10-09 found under a schema lock: a quoted name is a schema of its own case ("Sales" beside sales), and the
    /// XML exports read a schema, a table or the database named in a string; without a lock the exports stay in the database.
    /// </summary>
    [Fact]
    public void TheBoundary_ACaseTwin_AndTheXmlExports()
    {
        Assert.Equal(PostgresText.OtherSchema("Sales", "sales"), PostgresBoundary.Check("SELECT * FROM \"Sales\".t", "sales", ["sales", "Sales"]));
        Assert.Null(PostgresBoundary.Check("SELECT * FROM SALES.t", "sales", ["sales", "Sales"]));   // a bare word folds to sales, the lock's own
        Assert.Equal(PostgresText.OtherSchema("sales", "Sales"), PostgresBoundary.Check("SELECT * FROM sales.t", "Sales", ["sales", "Sales"]));
        foreach (string export in new[] { "schema_to_xml('archive', true, false, '')", "table_to_xml('archive.t', true, false, '')", "database_to_xml(true, false, '')" })
        {
            string name = export[..export.IndexOf('(', StringComparison.Ordinal)];
            Assert.Equal(PostgresText.XmlExport(name, "sales"), PostgresBoundary.Qualifiers("SELECT " + export, "sales").Refused);
            Assert.Null(PostgresBoundary.Check("SELECT " + export, null, []));
        }
    }

    [Theory]
    [InlineData("SELECT * FROM pg_roles", "pg_roles")]
    [InlineData("SELECT usename, passwd FROM pg_shadow", "pg_shadow")]
    [InlineData("SELECT * FROM pg_settings WHERE name = 'data_directory'", "pg_settings")]
    [InlineData("SELECT pg_database_size('other')", "pg_database_size")]
    [InlineData("SELECT pg_stat_get_backend_activity(1)", "pg_stat_get_backend_activity")]
    [InlineData("SELECT * FROM \"pg_stat_activity\"", "pg_stat_activity")]
    public void TheBoundary_RefusesTheClustersCatalogs_LockedOrNot(string sql, string named)
    {
        Assert.Equal(PostgresText.ServerCatalog(named), PostgresBoundary.Check(sql, null, []));
        Assert.Equal(PostgresText.ServerCatalog(named), PostgresBoundary.Qualifiers(sql, "sales").Refused);
        Assert.Null(PostgresBoundary.Check("SELECT relname FROM pg_class", null, []));
    }

    [Fact]
    public void TheRules_NameTheTools_OnlyWhileOffered()
    {
        Assert.Contains(Assistant.PostgresRule, Assistant.DefaultRules(false, tools: true, postgres: true));
        Assert.DoesNotContain(Assistant.PostgresRule, Assistant.DefaultRules(false, tools: true));
        Assert.Contains("postgres_query reads the PostgreSQL databases (not psql)", Assistant.ShellNativeRule(false, false, false, false, postgres: true));
        Assert.Equal("postgres_query", Shell.NativeRedirect.Table["psql"]);
        Assert.Contains(Assistant.PostgresRule, new TurnRules(Postgres: true).DefaultRules(false));
    }

    [Fact]
    public void PrepareTurn_TheMainChatsPrompt_CarriesTheRule_WhileOffered()
    {
        // 2026-10-04: PrepareTurn never passed the PostgreSQL flag to SystemPrompt, so the main chat's rules lacked the sentence the bots had.
        var catalog = new PostgresCatalog([new PostgresNamedConnection("shop", Config(), "x.json")], []);
        var tools = ChatScreen.PostgresTools(new PostgresAccess(() => catalog.Offered(_settings.PostgresConnectionsOffered)), () => _settings);
        var assistant = new Assistant(new Fakes.FakeChatClient(), new ConversationHistory(""), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        string dir = Directory.CreateTempSubdirectory("neon-postgres-").FullName;
        try
        {
            var memory = new NeonSidekick.Memory.MemoryStore(dir);
            ChatScreen.PrepareTurn(assistant, memory, [], [], new PersonaFile(dir), new OperataFile(dir), new VocaliaFile(dir), memoryEnabled: false, speechOutput: false, postgresTools: tools, postgresEnabled: true);
            Assert.Contains(Assistant.PostgresRule, assistant.History.SystemPrompt);

            ChatScreen.PrepareTurn(assistant, memory, [], [], new PersonaFile(dir), new OperataFile(dir), new VocaliaFile(dir), memoryEnabled: false, speechOutput: false, postgresTools: tools, postgresEnabled: false);
            Assert.DoesNotContain(Assistant.PostgresRule, assistant.History.SystemPrompt);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>
/// Resolves once per assembly whether a PostgreSQL server is reachable (2026-10-04): <c>NEONSIDEKICK_TEST_POSTGRES_CONNECTION</c> holds an
/// Npgsql connection string to it whose user owns its database (the dev container: <c>Host=127.0.0.1;Port=5432;Username=postgres;Password=…;Database=postgres</c>)
/// — the fixtures (<c>ns_*</c>) are made once, here, through a plain connection, never through the tools. Skipped without.
/// </summary>
internal static class LivePostgres
{
    public const string Variable = "NEONSIDEKICK_TEST_POSTGRES_CONNECTION";

    public static readonly PostgresConnectionConfig? Config;
    public static readonly string Unavailable = "";

    private static readonly string[] Fixtures =
    [
        "CREATE TABLE ns_customers (id int PRIMARY KEY, name text NOT NULL, email text UNIQUE, created timestamptz DEFAULT now())",
        "COMMENT ON TABLE ns_customers IS 'People who buy things'",
        "COMMENT ON COLUMN ns_customers.email IS 'Where to write'",
        "CREATE TABLE ns_orders (id int PRIMARY KEY, customer_id int NOT NULL CONSTRAINT ns_orders_customer_fk REFERENCES ns_customers(id), total numeric(12,2) CONSTRAINT ns_total_ck CHECK (total >= 0), tags text[], notes text)",
        "CREATE INDEX ns_orders_total_ix ON ns_orders(total)",
        "CREATE SEQUENCE ns_seq",
        "INSERT INTO ns_customers (id, name, email) VALUES (1, 'Ada Lovelace', 'ada@example.com'), (2, 'Alan Turing', 'alan@example.com')",
        "INSERT INTO ns_orders VALUES (10, 1, 42.5, ARRAY['gift','rush'], 'first'), (11, 2, 7, NULL, NULL), (12, 2, 9, NULL, NULL)",
    ];

    static LivePostgres()
    {
        string? text = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(text))
        {
            Unavailable = $"{Variable} is not set.";
            return;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(text) { Timeout = 5 };
            using var source = PostgresAccess.Build(builder.ConnectionString);
            using var connection = source.OpenConnection();
            using (var count = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE tablename = 'ns_orders'", connection))
            {
                if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    foreach (string sql in Fixtures)
                    {
                        using var create = new NpgsqlCommand(sql, connection);
                        create.ExecuteNonQuery();
                    }
                }
            }

            Config = new PostgresConnectionConfig
            {
                Host = builder.Host,
                Port = builder.Port,
                Database = builder.Database,
                User = builder.Username,
                Password = WindowsCredentials.Protect(builder.Password ?? "").Value ?? builder.Password,
                SslMode = "disable",
                ConnectTimeoutSeconds = 5,
            };
        }
        catch (Exception ex) when (ex is NpgsqlException or ArgumentException or InvalidOperationException or FormatException)
        {
            Unavailable = "No PostgreSQL server (or its user may not create the fixtures): " + ex.Message;
        }
    }
}

/// <summary>Skips unless <see cref="LivePostgres"/> connected: the PostgreSQL tools against a real server.</summary>
public sealed class LivePostgresFactAttribute : FactAttribute
{
    public LivePostgresFactAttribute()
    {
        if (LivePostgres.Config is null)
        {
            Skip = LivePostgres.Unavailable;
        }
    }
}

/// <summary>The nine PostgreSQL tools against the fixtures (2026-10-04): what each returns, and every read-only layer under the gate.</summary>
public sealed class LivePostgresTests
{
    private readonly AppSettingsData _settings = new() { PostgresTools = true };
    private readonly PostgresAccess _access;
    private readonly IReadOnlyList<AIFunction> _tools;

    public LivePostgresTests()
    {
        var catalog = LivePostgres.Config is { } config ? new PostgresCatalog([new PostgresNamedConnection("live", config, "test")], []) : PostgresCatalog.Empty;
        _access = new PostgresAccess(() => catalog);
        _tools = ChatScreen.PostgresTools(_access, () => _settings);
    }

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction =>
        (string)(await _tools.OfType<T>().Single().InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    private Task<SqlRun> Raw(string sql, int timeoutSeconds = 30, CancellationToken cancellationToken = default) =>
        _access.RunAsync("live", [sql], [], 10, timeoutSeconds, cancellationToken);

    [LivePostgresFact]
    public async Task TheCatalogTools_FindDescribeAndJoinTheFixtures()
    {
        Assert.Contains("| public |", await Invoke<PostgresSchemasTool>());
        Assert.Equal(PostgresText.NoDatabaseArgument, await Invoke<PostgresTablesTool>(("database", "postgres")));
        Assert.Equal(PostgresText.ServerCatalog("pg_database"), await Invoke<PostgresQueryTool>(("sql", "SELECT datname FROM pg_database")));
        string tables = await Invoke<PostgresTablesTool>(("pattern", "ns_"));
        Assert.Contains("| public.ns_customers | table |", tables);
        Assert.Contains("People who buy things", tables);

        string described = await Invoke<PostgresDescribeTool>(("table", "NS_ORDERS"));
        Assert.StartsWith("public.ns_orders (table, 5 columns) in live/", described);
        Assert.Contains("| id | integer | no | PK 1 |", described);
        Assert.Contains("| total | numeric(12,2) | yes |", described);
        Assert.Contains("| tags | text[] | yes |", described);
        Assert.Contains("- ns_orders_customer_fk: (customer_id) → ns_customers (id)", described);
        Assert.Contains("CREATE INDEX ns_orders_total_ix", described);
        Assert.Contains("- ns_total_ck: CHECK", described);
        Assert.Contains("Referenced by:\n- ns_orders (customer_id) → (id)", await Invoke<PostgresDescribeTool>(("table", "public.ns_customers")));
        Assert.Contains("| public.ns_customers | email | text | true | Where to write |", await Invoke<PostgresColumnsTool>(("pattern", "email")));
        Assert.Contains("ns_orders_customer_fk", await Invoke<PostgresRelationshipsTool>(("table", "ns_orders")));
        Assert.Contains("ns_orders_total_ix", await Invoke<PostgresIndexesTool>(("table", "ns_orders")));
        Assert.StartsWith("Error: no table or view 'nope'", await Invoke<PostgresDescribeTool>(("table", "nope")));
    }

    /// <summary>
    /// A schema lock against a real server (2026-10-09): the session's search path is the schema alone, a statement whose qualifiers are
    /// an alias reads after the schemas' names are read, its own schema written out reads, and information_schema is refused.
    /// </summary>
    [LivePostgresFact]
    public async Task ASchemaLock_SetsTheSearchPath_AndKeepsTheModelInIt()
    {
        var c = LivePostgres.Config!;
        var locked = new PostgresConnectionConfig { Host = c.Host, Port = c.Port, Database = c.Database, User = c.User, Password = c.Password, SslMode = c.SslMode, ConnectTimeoutSeconds = c.ConnectTimeoutSeconds, Schema = "public" };
        var tools = ChatScreen.PostgresTools(new PostgresAccess(() => new PostgresCatalog([new PostgresNamedConnection("live", locked, "test")], [])), () => _settings);
        var query = tools.OfType<PostgresQueryTool>().Single();
        Assert.Contains("| {public} |", await query.RunAsync("SELECT current_schemas(false)::text AS path", null, [], null, CancellationToken.None));
        Assert.StartsWith("1 row", await query.RunAsync("SELECT count(o.id) FROM ns_orders o", null, [], null, CancellationToken.None));
        Assert.StartsWith("1 row", await query.RunAsync("SELECT count(*) FROM public.ns_orders", null, [], null, CancellationToken.None));
        Assert.Equal(PostgresText.OtherSchema("information_schema", "public"), await query.RunAsync("SELECT * FROM information_schema.tables", null, [], null, CancellationToken.None));
        Assert.Contains("| public |", (string)(await tools.OfType<PostgresSchemasTool>().Single().InvokeAsync(new AIFunctionArguments()))!);
    }

    [LivePostgresFact]
    public async Task TheQuery_BindsReadsTypesAndCaps()
    {
        string rows = await _tools.OfType<PostgresQueryTool>().Single().RunAsync("SELECT id, total, tags FROM ns_orders WHERE customer_id = @c ORDER BY id", null, [new("c", 2L)], null, CancellationToken.None);
        Assert.StartsWith("2 rows × 3 columns from live/", rows);
        Assert.Contains("| 11 | 7.00 | NULL |", rows);
        Assert.Contains("{gift, rush}", await _tools.OfType<PostgresQueryTool>().Single().RunAsync("SELECT tags FROM ns_orders WHERE id = 10", null, [], null, CancellationToken.None));
        Assert.Contains("the first 1 shown", await _tools.OfType<PostgresQueryTool>().Single().RunAsync("SELECT * FROM ns_orders", null, [], 1, CancellationToken.None));
        var unbound = await Raw("SELECT * FROM ns_orders WHERE id=@id");
        Assert.Equal(SqlOutcome.Failed, unbound.Outcome);
        Assert.EndsWith(PostgresText.UnboundOperatorBind(["id"]), unbound.Detail);

        var types = await Raw(PostgresCheck.TypeMatrix);
        Assert.Equal(SqlOutcome.Ok, types.Outcome);
        var cells = types.Grids[0].Columns.Zip(types.Grids[0].Rows[0]).ToDictionary(c => c.First, c => c.Second);
        Assert.Equal("{1, 2, 3}", cells["int_array"]);
        Assert.Equal("f81d4fae-7dec-11d0-a765-00a0c91e6bf6", cells["uuid"]);
        Assert.Equal("0xDEADBEEF", cells["bytea"]);
        Assert.Equal(SqlText.Null, cells["none"]);
        Assert.Equal("2026-10-04", cells["date"]);
        Assert.Equal("2026-10-04 08:15:00 UTC", cells["timestamptz"]);
        Assert.Equal("{\"a\": 1}", cells["jsonb"]);
        Assert.StartsWith("(numeric: ", cells["numeric_big"]);
    }

    [LivePostgresFact]
    public async Task EveryLayerUnderTheGate_RefusesAWrite_AndTheTimeoutAndCancelStop()
    {
        var write = await Raw("INSERT INTO ns_orders VALUES (99, 1, 1, NULL, NULL)");
        Assert.Equal(SqlOutcome.Failed, write.Outcome);
        Assert.Contains(PostgresText.ReadOnlyRefused, write.Detail);
        Assert.Equal(SqlOutcome.Failed, (await Raw("SELECT nextval('ns_seq')")).Outcome);
        Assert.Equal(SqlOutcome.Failed, (await Raw("CREATE TEMP TABLE ns_x (a int)")).Outcome);

        var slow = await Raw(PostgresCheck.SlowQuery, timeoutSeconds: 1);
        Assert.Equal(SqlOutcome.Timeout, slow.Outcome);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Raw(PostgresCheck.SlowQuery, 30, cancel.Token));

        var who = await _access.RunAsync("live", PostgresCatalogQueries.WhoAmI, [], 10, 30, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, who.Outcome);
        Assert.StartsWith("PostgreSQL ", who.Grids[0].Rows[0][2]);
    }
}
