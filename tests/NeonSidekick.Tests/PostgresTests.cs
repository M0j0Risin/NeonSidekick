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

    private static PostgresConnectionConfig Config() => new() { Host = "db", User = "reader", Password = "p" };

    [Fact]
    public void AnEntry_SaysWhatIsWrong_AndBuildsAReadOnlySession()
    {
        Assert.Null(Config().Problem);
        Assert.Equal(PostgresText.NoHost, new PostgresConnectionConfig { User = "u" }.Problem);
        Assert.Equal(PostgresText.NoUser, new PostgresConnectionConfig { Host = "h" }.Problem);
        Assert.Equal(PostgresText.BadPort(0), new PostgresConnectionConfig { Host = "h", User = "u", Port = 0 }.Problem);
        Assert.Equal(PostgresText.BadSslMode("maybe"), new PostgresConnectionConfig { Host = "h", User = "u", SslMode = "maybe" }.Problem);
        Assert.Equal(SqlText.BadPasswordStore("vault"), new PostgresConnectionConfig { Host = "h", User = "u", PasswordStore = "vault" }.Problem);

        var builder = new PostgresConnectionConfig { Host = "db", User = "reader", SslMode = "verify-full" }.Builder("secret", null, 30);
        Assert.Equal(("db", 5432, "postgres", "reader", "secret", false), (builder.Host, builder.Port, builder.Database, builder.Username, builder.Password, builder.Pooling));
        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
        Assert.Contains("-c default_transaction_read_only=on", builder.Options);
        Assert.Contains("-c standard_conforming_strings=on", builder.Options);
        Assert.Contains("-c statement_timeout=30000", builder.Options);
        Assert.Equal("shop", new PostgresConnectionConfig { Host = "db", User = "reader", Database = "shop" }.Builder(null, null, 30).Database);
        Assert.Equal("other", new PostgresConnectionConfig { Host = "db", User = "reader", Database = "shop" }.Builder(null, "other", 30).Database);
        Assert.Equal("db:5432", Config().Endpoint);
        Assert.Equal("NeonSidekick/postgres/shop", Config().CredentialTarget("shop"));
    }

    [Fact]
    public void TheFile_LoadsTheConnections_TheProfilesWin_AndAnAddKeepsTheComments()
    {
        string home = Path.Combine(_dir, "home");
        string profile = Path.Combine(home, "profiles", "default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(PostgresConfigFile.GlobalPath(home), """{ "connections": { "shop": { "host": "global", "user": "u" }, "bad": { "user": "u" } } }""");
        Assert.Null(PostgresConfigFile.AddConnection(PostgresConfigFile.ProfilePath(profile), "shop", new PostgresConnectionConfig { Host = "local", User = "reader", SslMode = "require" }));
        Assert.Contains("// One entry per connection.", File.ReadAllText(PostgresConfigFile.ProfilePath(profile)));

        var catalog = PostgresConfigFile.LoadCatalog(profile, home);
        var shop = Assert.Single(catalog.Connections);
        Assert.Equal(("local", "require"), (shop.Config.Host, shop.Config.SslMode));
        Assert.Equal(PostgresText.NoHost, Assert.Single(catalog.Problems).Reason);
        Assert.Equal(1, catalog.Offered([]).Hidden);
        Assert.Same(shop, catalog.Find(null, "SHOP"));
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
        Assert.Equal(9, tools.Count);
        Assert.Equal(ChatScreen.PostgresToolNames, tools.Select(t => t.Name).ToHashSet());
        Assert.All(tools, t => Assert.True(PlanTools.Allowed(t.Name), t.Name));
        Assert.StartsWith("1 PostgreSQL connection (every PostgreSQL tool", tools.OfType<PostgresConnectionsTool>().Single().Describe());

        var query = tools.OfType<PostgresQueryTool>().Single();
        Assert.StartsWith("Error: the SQL starts with DELETE", await query.RunAsync("DELETE FROM t", null, null, [], null, CancellationToken.None));
        Assert.Equal(SqlText.BadMaxRows(PostgresQueryTool.MinRows, PostgresQueryTool.MaxRows), await query.RunAsync("SELECT 1", null, null, [], 0, CancellationToken.None));
        Assert.Equal(PostgresText.NoPattern, await tools.OfType<PostgresColumnsTool>().Single().InvokeAsync(new AIFunctionArguments()));
        Assert.Equal(PostgresText.NoTable, await tools.OfType<PostgresDescribeTool>().Single().DescribeAsync(" ", null, null, null, CancellationToken.None));
        Assert.Equal(PostgresText.BadTable("a.b.c"), await tools.OfType<PostgresDescribeTool>().Single().DescribeAsync("a.b.c", null, null, null, CancellationToken.None));

        _settings.PostgresConnectionsOffered = null;
        Assert.Equal(PostgresText.NoConnections, await tools.OfType<PostgresTablesTool>().Single().DescribeAsync(null, null, null, null, CancellationToken.None));
        Assert.False(ChatScreen.PostgresOffered(_settings, access));
        _settings.PostgresConnectionsOffered = ["shop"];
        Assert.True(ChatScreen.PostgresOffered(_settings, access));
        Assert.Equal("PostgreSQL · db:5432", Assert.Single(ChatScreen.PostgresChoices(catalog)).Note);
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
        _access.RunAsync("live", null, null, [sql], [], 10, timeoutSeconds, cancellationToken);

    [LivePostgresFact]
    public async Task TheCatalogTools_FindDescribeAndJoinTheFixtures()
    {
        Assert.Contains("| public |", await Invoke<PostgresSchemasTool>());
        Assert.Contains("| postgres |", await Invoke<PostgresDatabasesTool>());
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

    [LivePostgresFact]
    public async Task TheQuery_BindsReadsTypesAndCaps()
    {
        string rows = await _tools.OfType<PostgresQueryTool>().Single().RunAsync("SELECT id, total, tags FROM ns_orders WHERE customer_id = @c ORDER BY id", null, null, [new("c", 2L)], null, CancellationToken.None);
        Assert.StartsWith("2 rows × 3 columns from live/", rows);
        Assert.Contains("| 11 | 7.00 | NULL |", rows);
        Assert.Contains("{gift, rush}", await _tools.OfType<PostgresQueryTool>().Single().RunAsync("SELECT tags FROM ns_orders WHERE id = 10", null, null, [], null, CancellationToken.None));
        Assert.Contains("the first 1 shown", await _tools.OfType<PostgresQueryTool>().Single().RunAsync("SELECT * FROM ns_orders", null, null, [], 1, CancellationToken.None));

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

        var who = await _access.RunAsync("live", null, null, PostgresCatalogQueries.WhoAmI, [], 10, 30, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, who.Outcome);
        Assert.StartsWith("PostgreSQL ", who.Grids[0].Rows[0][2]);
    }
}
