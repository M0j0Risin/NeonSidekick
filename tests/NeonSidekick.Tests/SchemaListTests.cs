using NeonSidekick.Oracle;
using NeonSidekick.Postgres;
using NeonSidekick.Sql;
using NpgsqlTypes;

namespace NeonSidekick.Tests;

/// <summary>
/// A list of schemas on an Oracle or PostgreSQL connection (2026-11-14, feature idea #18), without a server: the entry's refusals, the
/// boundary over two places, Oracle's schema check and its owner binds, the write pane's caption, PostgreSQL's search path and its
/// array bind, and the list surviving the encrypt-in-place rewrite. The server's half is in the live facts.
/// </summary>
public sealed class SchemaListTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public SchemaListTests() => Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_home));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private static List<string?> Many(int count) => Enumerable.Range(0, count).Select(i => (string?)("S" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();

    // ─── the entry ───────────────────────────────────────────────────────────

    [Fact]
    public void Oracle_TheEntry_RefusesABadList_AndTakesAGoodOne()
    {
        Assert.Equal(OracleText.BothSchemaKeys, OracleConnectionConfig.LockProblem("HR", ["OE"]));
        Assert.Equal(OracleText.EmptySchemas, OracleConnectionConfig.LockProblem(null, []));
        Assert.Equal(OracleText.RepeatedSchema("hr"), OracleConnectionConfig.LockProblem(null, ["HR", "hr"]));   // the same name once folded
        Assert.Null(OracleConnectionConfig.LockProblem(null, ["HR", "\"hr\""]));                                 // a quoted one is its own
        Assert.Equal(OracleText.BadSchemaKey("not a name", "schemas"), OracleConnectionConfig.LockProblem(null, ["HR", "not a name"]));
        Assert.Equal(OracleText.BadSchemaKey("", "schemas"), OracleConnectionConfig.LockProblem(null, ["HR", " "]));
        Assert.Equal(OracleText.TooManySchemas(SchemaLock.MaxSchemas), OracleConnectionConfig.LockProblem(null, Many(SchemaLock.MaxSchemas + 1)));
        Assert.Null(OracleConnectionConfig.LockProblem(null, Many(SchemaLock.MaxSchemas)));
        foreach (string system in new[] { "SYS", "system", "XDB", "MDSYS", "CTXSYS", "AUDSYS" })
        {
            Assert.Equal(OracleText.SystemSchema(system, "schemas"), OracleConnectionConfig.LockProblem(null, ["HR", system]));
            Assert.Null(OracleConnectionConfig.LockProblem(system, null));   // #159: a saved single schema loads as it did before the list
            Assert.Equal(OracleText.SystemSchema(system), OracleConnectionConfig.PickProblem([system]));   // the wizard still refuses it
        }

        Assert.Equal(OracleText.BadSchemaKey("not a name"), OracleConnectionConfig.LockProblem("not a name", null));
        Assert.Null(OracleConnectionConfig.PickProblem([]));
        Assert.Equal(OracleText.RepeatedSchema("hr"), OracleConnectionConfig.PickProblem(["HR", "hr"]));
        Assert.Null(new OracleConnectionConfig { DataSource = "x:1/y", User = "neon", Password = "p", Schema = "XDB" }.Problem);

        var entry = new OracleConnectionConfig { DataSource = "x:1/y", User = "neon", Password = "p", Schemas = ["HR", "OE"] };
        Assert.Null(entry.Problem);
        entry.Schema = "HR";
        Assert.Equal(OracleText.BothSchemaKeys, entry.Problem);
    }

    [Fact]
    public void Postgres_TheEntry_RefusesABadList_AndTakesAGoodOne()
    {
        Assert.Equal(PostgresText.BothSchemaKeys, PostgresConnectionConfig.LockProblem("public", ["sales"]));
        Assert.Equal(PostgresText.EmptySchemas, PostgresConnectionConfig.LockProblem(null, []));
        Assert.Equal(PostgresText.RepeatedSchema("sales"), PostgresConnectionConfig.LockProblem(null, ["sales", "public", "sales"]));
        Assert.Null(PostgresConnectionConfig.LockProblem(null, ["sales", "Sales"]));   // exact: two schemas of their own case
        Assert.Equal(PostgresText.BadSchemaKey("a b", "schemas"), PostgresConnectionConfig.LockProblem(null, ["sales", "a b"]));
        Assert.Equal(PostgresText.BadSchemaKey("a,b", "schemas"), PostgresConnectionConfig.LockProblem(null, ["a,b"]));   // the option would split
        Assert.Equal(PostgresText.TooManySchemas(SchemaLock.MaxSchemas), PostgresConnectionConfig.LockProblem(null, Many(SchemaLock.MaxSchemas + 1)));
        foreach (string system in new[] { "pg_catalog", "pg_toast", "information_schema", "$user" })
        {
            Assert.Equal(PostgresText.SystemSchema(system, "schemas"), PostgresConnectionConfig.LockProblem(null, ["sales", system]));
            Assert.Null(PostgresConnectionConfig.LockProblem(system, null));   // #160: a saved single schema loads as it did before the list
            Assert.Equal(PostgresText.SystemSchema(system), PostgresConnectionConfig.PickProblem([system]));   // the wizard still refuses it
        }

        Assert.Equal(PostgresText.BadSchemaKey("a b"), PostgresConnectionConfig.LockProblem("a b", null));
        Assert.Null(PostgresConnectionConfig.PickProblem([]));
        Assert.Equal(PostgresText.BadSchemaKey("a,b"), PostgresConnectionConfig.PickProblem(["a,b"]));
        Assert.Null(new PostgresConnectionConfig { Host = "db", Database = "shop", User = "u", Password = "p", Schema = "$user" }.Problem);

        var entry = new PostgresConnectionConfig { Host = "db", Database = "shop", User = "u", Password = "p", Schemas = [" sales ", "public"] };
        Assert.Null(entry.Problem);
        Assert.Equal(["sales", "public"], entry.Locks);
        Assert.Equal("sales, public", entry.LockText);
        entry.HoldTo(["sales"]);
        Assert.Null(entry.Schema);   // #29: a list stays a list, one name or many
        Assert.Equal(["sales"], entry.Schemas);
        entry.HoldTo([]);
        Assert.Null(entry.LockText);
        entry.HoldTo(["sales"]);
        Assert.Equal("sales", entry.Schema);   // no list before: one name is a schema
        Assert.Null(entry.Schemas);
        entry.HoldTo(["sales", "public"]);
        Assert.Equal(["sales", "public"], entry.Schemas);
        Assert.Null(entry.Schema);
    }

    [Fact]
    public void Oracle_HoldTo_KeepsTheEntrysForm()
    {
        // 2026-11-15, the database-tools review's #29.
        var listed = new OracleConnectionConfig { DataSource = "db", User = "u", Schemas = ["HR"] };
        listed.HoldTo(["OE"]);
        Assert.Equal(["OE"], listed.Schemas);
        Assert.Null(listed.Schema);
        var single = new OracleConnectionConfig { DataSource = "db", User = "u", Schema = "HR" };
        single.HoldTo(["OE"]);
        Assert.Equal("OE", single.Schema);
        Assert.Null(single.Schemas);
        Assert.Equal((null, null), SchemaLock.Hold([], asList: true));
    }

    [Fact]
    public void TheSharedListRules_RunInOrder()
    {
        // #44: both keys, then empty, then the cap, then each name, then a repeat (by the family's sameness).
        var words = new SchemaLock.Words("both", "empty", n => "many " + n, s => "twice " + s);
        Assert.Equal("both", SchemaLock.ListProblem("a", [], words, _ => null, s => s));
        Assert.Equal("empty", SchemaLock.ListProblem(null, [], words, _ => null, s => s));
        Assert.Equal("many 16", SchemaLock.ListProblem(null, Enumerable.Range(0, 17).Select(i => (string?)("s" + i)).ToList(), words, _ => null, s => s));
        Assert.Equal("bad x", SchemaLock.ListProblem(null, ["a", "x", "a"], words, n => n == "x" ? "bad x" : null, s => s));
        Assert.Equal("twice A", SchemaLock.ListProblem(null, ["a", " A "], words, _ => null, s => s.ToUpperInvariant()));
        Assert.Null(SchemaLock.ListProblem(null, ["a", "A"], words, _ => null, s => s));
    }

    /// <summary>#95: the relationships list holds only the key's owner to the lock, so an FK into another owner's table is shown.</summary>
    [Fact]
    public void Oracle_Relationships_HoldOnlyTheKeysOwner()
    {
        Assert.Contains("k.owner IN (:neon_owners)", OracleCatalogQueries.Relationships, StringComparison.Ordinal);
        Assert.DoesNotContain("r.owner IN", OracleCatalogQueries.Relationships, StringComparison.Ordinal);
    }

    // ─── the boundary ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT * FROM hr.employees e JOIN oe.orders o ON o.emp = e.id", null)]
    [InlineData("SELECT * FROM employees", null)]
    [InlineData("SELECT * FROM sh.sales", "SH")]
    [InlineData("SELECT * FROM hr.employees, sh.sales", "SH")]
    public void Oracle_TheBoundary_HoldsTwoPlaces(string sql, string? other)
    {
        IReadOnlyList<string> places = ["HR", "OE"];
        string[] visible = ["NEON", "HR", "OE", "SH"];
        Assert.Equal(other is null ? null : OracleText.OtherSchema(other, places), OracleBoundary.Check(sql, places, visible, []));
        Assert.Equal(other is null ? null : OracleText.OtherSchema(other, places), OracleBoundary.Check(sql, places, visible.Select(v => new[] { OracleBoundary.UserKind, v })));
    }

    [Fact]
    public void Oracle_TheRefusals_NameEveryPlace()
    {
        Assert.Contains("schemas 'HR', 'OE'", OracleText.OtherSchema("SH", ["HR", "OE"]), StringComparison.Ordinal);
        Assert.Contains("schemas 'HR', 'OE'", OracleText.TableElsewhere("SH", ["HR", "OE"]), StringComparison.Ordinal);
        Assert.Contains("held to schema 'READER'; give the table as NAME or READER.NAME", OracleText.TableElsewhere("SH", ["READER"]), StringComparison.Ordinal);   // one place reads as before
        Assert.Contains("OE.ORDERS", OracleText.GiveItQualified("orders", ["OE.ORDERS"]), StringComparison.Ordinal);
        Assert.Equal("schema 'HR'", OracleText.HeldTo(["HR"]));
        Assert.Contains("visible in free's schemas 'HR', 'OE';", OracleText.TableNotFound("t", "free", ["HR", "OE"]), StringComparison.Ordinal);   // #116
        Assert.Contains("visible in free/HR;", OracleText.TableNotFound("t", "free", ["HR"]), StringComparison.Ordinal);
        Assert.Contains("The tools keep to schemas HR, OE;", OracleText.Reach(["HR", "OE"], ["SH"], []), StringComparison.Ordinal);   // #101: told by the list
        Assert.Contains("The tools keep to schema HR;", OracleText.Reach(["HR"], ["SH"], []), StringComparison.Ordinal);
        Assert.Contains("than HR's and", OracleText.Reach(["HR"], [], []), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT * FROM public.customers c JOIN ns_other.refs r ON r.customer_id = c.id", null)]
    [InlineData("SELECT * FROM refs", null)]
    [InlineData("SELECT * FROM ns_third.hidden", "ns_third")]
    [InlineData("SELECT * FROM public.customers, ns_third.hidden", "ns_third")]
    public void Postgres_TheBoundary_HoldsTwoSchemas(string sql, string? other)
    {
        IReadOnlyList<string> locks = ["public", "ns_other"];
        string[] visible = ["public", "ns_other", "ns_third", "pg_catalog"];
        Assert.Equal(other is null ? null : PostgresText.OtherSchema(other, locks), PostgresBoundary.CheckUnder(sql, locks, visible));
        Assert.Contains("schemas 'public', 'ns_other'", PostgresText.OtherSchema("ns_third", locks), StringComparison.Ordinal);
        Assert.Contains("held to schema 'sales';", PostgresText.OtherSchema("x", ["sales"]), StringComparison.Ordinal);
    }

    // ─── Oracle's schema check and its binds ────────────────────────────────

    [Fact]
    public void Oracle_TheSchemaCheck_RefusesAMissingOrMaintainedSchema()
    {
        Assert.Contains("u.oracle_maintained", OracleCatalogQueries.SchemaCheckFor("23.26.0.0.0", 2), StringComparison.Ordinal);
        Assert.Contains("u.oracle_maintained", OracleCatalogQueries.SchemaCheckFor("12.1.0.2.0", 2), StringComparison.Ordinal);
        Assert.DoesNotContain("oracle_maintained", OracleCatalogQueries.SchemaCheckFor("11.2.0.4.0", 2), StringComparison.Ordinal);   // #64: 11g has no such column
        Assert.Contains(":neon_s1", OracleCatalogQueries.SchemaCheckFor("11.2.0.4.0", 2), StringComparison.Ordinal);
        Assert.Null(OracleText.SchemaCheck([["HR", "N"], ["OE", "N"]], ["HR", "OE"]));
        Assert.Contains("schema OE doesn't exist", OracleText.SchemaCheck([["HR", "N"]], ["HR", "OE"]), StringComparison.Ordinal);
        Assert.Contains("schema SYSTEM is Oracle's own", OracleText.SchemaCheck([["HR", "N"], ["SYSTEM", "Y"]], ["HR", "SYSTEM"]), StringComparison.Ordinal);
        Assert.Contains("schema hr doesn't exist", OracleText.SchemaCheck([["HR", "N"]], ["hr"]), StringComparison.Ordinal);   // as the dictionary spells it
    }

    [Fact]
    public void Oracle_BeforeTwelve_EveryQueryReadsMaintainedAsN()
    {
        // 2026-11-15, #64/#118: the wizard's listing and the catalogs join ALL_USERS.ORACLE_MAINTAINED, which 11g lacks.
        Assert.Contains("'N' = 'N'", OracleCatalogQueries.ForServer(OracleCatalogQueries.UserSchemas, "11.2.0.4.0"), StringComparison.Ordinal);
        Assert.DoesNotContain("oracle_maintained", OracleCatalogQueries.ForServer(OracleCatalogQueries.Columns, "11.2.0.4.0"), StringComparison.Ordinal);
        Assert.Equal(OracleCatalogQueries.UserSchemas, OracleCatalogQueries.ForServer(OracleCatalogQueries.UserSchemas, "12.1.0.2.0"));
        Assert.Equal(OracleCatalogQueries.UserSchemas, OracleCatalogQueries.ForServer(OracleCatalogQueries.UserSchemas, null));
    }

    [Fact]
    public void Postgres_TheSavedLookup_IsTheListingsRule_OverTheSavedNames()
    {
        // #146: "usable" means what it means in the listing; the filter comes before the ORDER BY.
        Assert.StartsWith(PostgresCatalogQueries.UsableSchemas[..^" ORDER BY 1".Length], PostgresCatalogQueries.SavedSchemas, StringComparison.Ordinal);
        Assert.EndsWith(" AND n.nspname = ANY(@saved::text[]) ORDER BY 1", PostgresCatalogQueries.SavedSchemas, StringComparison.Ordinal);
    }

    /// <summary>Every app-written statement holding the owner marker gets one bind per place and no other name it can't be given.</summary>
    [Fact]
    public void Oracle_EveryOwnerMarker_IsWrittenOut_AndBound()
    {
        IReadOnlyList<string> places = ["HR", "OE", "SH"];
        string[] statements = [OracleCatalogQueries.SchemaCheck, OracleCatalogQueries.Tables, OracleCatalogQueries.Resolve, OracleCatalogQueries.Relationships,
            OracleCatalogQueries.Columns, OracleCatalogQueries.Indexes, OracleCatalogQueries.IndexUsage, .. OracleCatalogQueries.Reach, .. OracleCatalogQueries.Describe];
        var owners = OracleCatalogQueries.OwnerBinds(places).Select(b => b.Name).ToList();
        Assert.Equal(["neon_s0", "neon_s1", "neon_s2"], owners);
        Assert.Equal(places, OracleCatalogQueries.OwnerBinds(places).Select(b => (string)b.Value!));
        foreach (string sql in statements)
        {
            string written = OracleCatalogQueries.ForPlaces(sql, places.Count);
            Assert.DoesNotContain(OracleCatalogQueries.OwnerList, written, StringComparison.Ordinal);
            var used = OracleReadOnlyGate.Binds(written).Where(n => n.StartsWith(OracleCatalogQueries.OwnerBindPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.All(used, n => Assert.Contains(n, owners, StringComparer.OrdinalIgnoreCase));
            if (sql.Contains(OracleCatalogQueries.OwnerList, StringComparison.Ordinal))
            {
                Assert.Equal(owners.Count, used.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
        }

        Assert.Contains("IN (:neon_s0)", OracleCatalogQueries.ForPlaces(OracleCatalogQueries.Tables, 1), StringComparison.Ordinal);
        Assert.Contains("IN (:neon_s0, :neon_s1)", OracleCatalogQueries.ForPlaces(OracleCatalogQueries.Tables, 2), StringComparison.Ordinal);
        Assert.Contains("NOT IN (:neon_s0, :neon_s1)", OracleCatalogQueries.ForPlaces(OracleCatalogQueries.Reach[0], 2), StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_ThePlaces_FirstIsCurrent_AndThePlaceTextNamesThemAll()
    {
        var listed = new OracleConnectionConfig { DataSource = "x:1/y", User = "neon", Schemas = ["hr", "\"Lab\""] };
        Assert.Equal(["HR", "Lab"], OracleAccess.Places(listed));
        Assert.Equal("HR", OracleAccess.Place(listed));
        Assert.Equal("HR, Lab", OracleAccess.PlaceText(listed));
        var own = new OracleConnectionConfig { DataSource = "x:1/y", User = "neon" };
        Assert.Equal(["NEON"], OracleAccess.Places(own));
        Assert.Equal("NEON", OracleAccess.PlaceText(own));
    }

    // ─── writes ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheAllowCaption_NamesEveryListedSchema()
    {
        string one = ServerWriteText.AllowCaption(OracleStatementKinds.Family, "shop", "HR", "DELETE FROM t", "db:1521");
        Assert.Contains("change shop/HR (", one, StringComparison.Ordinal);
        Assert.Contains("schema HR)", one, StringComparison.Ordinal);
        string two = ServerWriteText.AllowCaption(OracleStatementKinds.Family, "shop", "HR, OE", "DELETE FROM oe.t", "db:1521", places: 2);
        Assert.Contains("change shop (", two, StringComparison.Ordinal);
        Assert.Contains("schemas HR, OE)", two, StringComparison.Ordinal);
    }

    // ─── PostgreSQL's session ───────────────────────────────────────────────

    [Fact]
    public void Postgres_TheSearchPath_IsTheList_InOrder_JoinedByABareComma()
    {
        var entry = new PostgresConnectionConfig { Host = "db", Database = "shop", User = "u", Schemas = ["sales", "Archive", "public"] };
        string options = entry.Builder("p", 15).Options!;
        Assert.Contains("-c search_path=sales,\"Archive\",public", options, StringComparison.Ordinal);
        Assert.DoesNotContain(", ", options[options.IndexOf("search_path", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotContain("search_path", new PostgresConnectionConfig { Host = "db", Database = "shop", User = "u" }.Builder("p", 15).Options ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_TheLockList_BindsAsATextArray()
    {
        var list = PostgresAccess.Bind("lock", new[] { "public", "ns_other" });
        Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Text, list.NpgsqlDbType);
        Assert.Equal(new[] { "public", "ns_other" }, list.Value);
        var none = PostgresAccess.Bind("lock", null);
        Assert.Equal(NpgsqlDbType.Text, none.NpgsqlDbType);
        Assert.Equal(DBNull.Value, none.Value);
    }

    // ─── the file ────────────────────────────────────────────────────────────

    [Fact]
    public void TheList_AndItsOrder_SurviveTheEncryptInPlaceRewrite()
    {
        File.WriteAllText(OracleConfigFile.GlobalPath(_home), """{ "connections": { "a": { "dataSource": "x:1/y", "user": "u", "password": "one", "schemas": ["OE", "HR", "SH"] } } }""");
        File.WriteAllText(PostgresConfigFile.GlobalPath(_home), """{ "connections": { "b": { "host": "db", "database": "shop", "user": "u", "password": "two", "schemas": ["sales", "public"] } } }""");

        OracleConfigFile.EncryptAll(_home);
        PostgresConfigFile.EncryptAll(_home);

        Assert.DoesNotContain("\"one\"", File.ReadAllText(OracleConfigFile.GlobalPath(_home)));
        Assert.DoesNotContain("\"two\"", File.ReadAllText(PostgresConfigFile.GlobalPath(_home)));
        var oracle = Assert.Single(OracleConfigFile.Load(OracleConfigFile.GlobalPath(_home)).Connections);
        Assert.Equal(["OE", "HR", "SH"], oracle.Config.Schemas);
        var postgres = Assert.Single(PostgresConfigFile.Load(PostgresConfigFile.GlobalPath(_home)).Connections);
        Assert.Equal(["sales", "public"], postgres.Config.Schemas);
    }

    [Fact]
    public void ABadList_IsAProblem_NotAConnection()
    {
        File.WriteAllText(PostgresConfigFile.GlobalPath(_home), """{ "connections": { "b": { "host": "db", "database": "shop", "user": "u", "password": "p", "schemas": [] } } }""");
        var catalog = PostgresConfigFile.Load(PostgresConfigFile.GlobalPath(_home));
        Assert.Empty(catalog.Connections);
        Assert.Contains(PostgresText.EmptySchemas, Assert.Single(catalog.Problems).Reason, StringComparison.Ordinal);
    }
}
