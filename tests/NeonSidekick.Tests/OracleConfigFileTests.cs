using NeonSidekick.Oracle;
using NeonSidekick.Sql;
using Oracle.ManagedDataAccess.Client;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>oracle.json</c> (2026-09-30): the shape, the problems it reports (SYS among them), the profile over the home, the
/// connection string, the passwords kept as <c>sql.json</c>'s are, the wizard's append, and Oracle's names.
/// </summary>
public sealed class OracleConfigFileTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _profile;
    private readonly string _target = "NeonSidekick.Tests/oracle/" + Guid.NewGuid().ToString("N");

    public OracleConfigFileTests()
    {
        _profile = Path.Combine(_home, "profiles", "default");
        Directory.CreateDirectory(_profile);
    }

    public void Dispose()
    {
        WindowsCredentials.DeleteGeneric(_target);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private string ProfilePath => OracleConfigFile.ProfilePath(_profile);

    private void Profile(string json) => File.WriteAllText(ProfilePath, json);

    private void Global(string json) => File.WriteAllText(OracleConfigFile.GlobalPath(_home), json);

    private static OracleNamedConnection Named(OracleConnectionConfig config, string source = "test") => new("prod", config, source);

    [Fact]
    public void AMissingFile_IsEmpty_AndTheEmptyShape_LoadsEmpty()
    {
        Assert.Same(OracleCatalog.Empty, OracleConfigFile.Load(ProfilePath));
        Assert.True(OracleConfigFile.EnsureExists(ProfilePath));
        Assert.False(OracleConfigFile.EnsureExists(ProfilePath));
        Assert.Equal(OracleConfigFile.EmptyText, File.ReadAllText(ProfilePath));
        var loaded = OracleConfigFile.Load(ProfilePath);
        Assert.Empty(loaded.Connections);
        Assert.Empty(loaded.Problems);
    }

    [Fact]
    public void TheEmptyShapesExamples_AreEachAUsableConnection_OnceUncommented()
    {
        var example = OracleConfigFile.EmptyText.Split('\n')
            .Where(l => l.StartsWith("  // ", StringComparison.Ordinal))
            .Select(l => l["  // ".Length..])
            .Where(l => l.EndsWith('{') || l.EndsWith(',') || l.EndsWith('}') || l.EndsWith('"'))
            .Where(l => l.StartsWith('"') || l.StartsWith("  ", StringComparison.Ordinal) || l.StartsWith('}'));
        Profile("{ \"connections\": {\n" + string.Join("\n", example) + "\n} }");

        var loaded = OracleConfigFile.Load(ProfilePath);

        Assert.Empty(loaded.Problems);
        Assert.Equal(["hr", "ledger"], loaded.Connections.Select(c => c.Name));
        Assert.StartsWith(WindowsCredentials.ProtectedPrefix, loaded.Connections[0].Config.Password);   // the plain example was encrypted in place
        Assert.True(loaded.Connections[1].Config.InCredentialManager);
        Assert.StartsWith("(DESCRIPTION=", loaded.Connections[1].Config.DataSource);
        Assert.Contains("cmdkey /generic:NeonSidekick/oracle/ledger /user:ledger_ro /pass", OracleConfigFile.EmptyText);
    }

    [Fact]
    public void Connections_LoadInFileOrder_AndABadEntryIsAProblem_NotAThrow()
    {
        Profile("""
            {
              // comments and a trailing comma are fine
              "connections": {
                "free": { "dataSource": "localhost:1521/FREEPDB1", "user": "neon", "schema": "hr", "description": "the container" },
                "nosource": { "user": "u" },
                "nouser": { "dataSource": "x:1521/y" },
                "sys": { "dataSource": "x:1521/y", "user": "Sys" },
                "sysdba": { "dataSource": "x:1521/y", "user": "system as sysdba" },
                "badstore": { "dataSource": "x:1521/y", "user": "u", "passwordStore": "vault" },
                "slow": { "dataSource": "x:1521/y", "user": "u", "connectTimeoutSeconds": 121 },
                "badschema": { "dataSource": "x:1521/y", "user": "u", "schema": "not a name" },
                "empty": null,
              }
            }
            """);

        var loaded = OracleConfigFile.Load(ProfilePath);

        var free = Assert.Single(loaded.Connections);
        Assert.Equal("free", free.Name);
        Assert.Equal(ProfilePath, free.Source);
        Assert.Equal(
            [OracleText.NoDataSource, OracleText.NoUser, OracleText.SysRefused("Sys"), OracleText.SysRefused("system as sysdba"), SqlText.BadPasswordStore("vault"), SqlText.BadConnectTimeout(121, 120), OracleText.BadSchemaKey("not a name"), OracleText.NoDataSource],
            loaded.Problems.Select(p => p.Reason));
        Assert.Equal(SqlText.ConnectionSource(ProfilePath, "sys"), loaded.Problems[2].Source);
    }

    [Fact]
    public void AFileThatIsNotJson_IsOneProblem()
    {
        Profile("{ not json");
        var loaded = OracleConfigFile.Load(ProfilePath);
        Assert.Empty(loaded.Connections);
        Assert.StartsWith("the file cannot be read (", Assert.Single(loaded.Problems).Reason);
    }

    [Fact]
    public void TheProfilesFile_WinsByName_OverTheHomes_AndFindPicksByNameThenDefaultThenFirst()
    {
        Profile("""{ "connections": { "ledger": { "dataSource": "profile:1521/x", "user": "u" } } }""");
        Global("""{ "connections": { "LEDGER": { "dataSource": "home:1521/x", "user": "u" }, "hr": { "dataSource": "home:1521/hr", "user": "u" } } }""");

        var catalog = OracleConfigFile.LoadCatalog(_profile, _home);

        Assert.Equal(["ledger", "hr"], catalog.Connections.Select(c => c.Name));
        Assert.Equal("profile:1521/x", catalog.Find("Ledger", null)!.Config.DataSource);
        Assert.Equal("hr", catalog.Find(null, "HR")!.Name);
        Assert.Equal("ledger", catalog.Find(null, "gone")!.Name);
        Assert.Null(catalog.Find("gone", null));

        var narrowed = catalog.Offered(["hr", "never-there"]);
        Assert.Equal(["hr"], narrowed.Connections.Select(c => c.Name));
        Assert.Equal(1, narrowed.Hidden);
        Assert.Empty(catalog.Offered(null).Connections);   // null offers none (2026-10-01)
    }

    [Fact]
    public void TheBuilder_NamesTheSourceAndUser_PoolsSessions_AndNeverAsksForADbaPrivilege()
    {
        var config = new OracleConnectionConfig { DataSource = " localhost:1521/FREEPDB1 ", User = " neon ", ConnectTimeoutSeconds = 7 };
        var builder = config.Builder("secret");
        Assert.Equal("localhost:1521/FREEPDB1", builder.DataSource);
        Assert.Equal("neon", builder.UserID);
        Assert.Equal("secret", builder.Password);
        Assert.Equal(7, builder.ConnectionTimeout);
        Assert.True(builder.Pooling);
        Assert.Equal("", builder.DBAPrivilege);
        Assert.Equal(OracleConnectionConfig.DefaultConnectTimeoutSeconds, new OracleConnectionConfig { DataSource = "x", User = "u" }.Builder().ConnectionTimeout);
    }

    [Fact]
    public void APlainPassword_IsEncryptedInPlace_TheRestOfTheFileUntouched()
    {
        Profile("""
            {
              // keep me
              "connections": {
                "free": { "dataSource": "localhost:1521/FREEPDB1", "user": "neon", "password": "plain-secret" }
              }
            }
            """);

        var loaded = OracleConfigFile.Load(ProfilePath);

        string text = File.ReadAllText(ProfilePath);
        Assert.DoesNotContain("plain-secret", text);
        Assert.Contains("// keep me", text);
        Assert.StartsWith(WindowsCredentials.ProtectedPrefix, loaded.Connections[0].Config.Password);
        Assert.Equal("plain-secret", OracleSecrets.Resolve(loaded.Connections[0]).Value);
    }

    [Fact]
    public void EncryptAll_ReadsTheHomesFile_AndEveryProfiles()
    {
        string other = Path.Combine(_home, "profiles", "work");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(_home, "settings.json"), "{}");
        Global("""{ "connections": { "a": { "dataSource": "x:1/y", "user": "u", "password": "one" } } }""");
        File.WriteAllText(OracleConfigFile.ProfilePath(other), """{ "connections": { "b": { "dataSource": "x:1/y", "user": "u", "password": "two" } } }""");

        var read = OracleConfigFile.EncryptAll(_home);

        Assert.Contains(OracleConfigFile.GlobalPath(_home), read);
        Assert.Contains(OracleConfigFile.ProfilePath(other), read);
        Assert.DoesNotContain("\"one\"", File.ReadAllText(OracleConfigFile.GlobalPath(_home)));
        Assert.DoesNotContain("\"two\"", File.ReadAllText(OracleConfigFile.ProfilePath(other)));
    }

    [Fact]
    public void AddConnection_MakesTheFile_KeepsItsComments_AndLeavesThePasswordOut()
    {
        var config = new OracleConnectionConfig { DataSource = "localhost:1521/FREEPDB1", User = "neon", Password = "never-written", PasswordStore = "credman", Description = "the container" };

        Assert.Null(OracleConfigFile.AddConnection(ProfilePath, "free", config));
        Assert.Null(OracleConfigFile.AddConnection(ProfilePath, "second", new OracleConnectionConfig { DataSource = "x:1/y", User = "u" }));
        Assert.Equal(SqlText.ConnectionAlreadyInFile("FREE"), OracleConfigFile.AddConnection(ProfilePath, "FREE", config));

        string text = File.ReadAllText(ProfilePath);
        Assert.StartsWith(OracleConfigFile.EmptyText[..40], text);   // the commented shape kept
        Assert.DoesNotContain("never-written", text);
        Assert.Equal("never-written", config.Password);   // the caller's object is left as it was
        var loaded = OracleConfigFile.Load(ProfilePath);
        Assert.Equal(["free", "second"], loaded.Connections.Select(c => c.Name));
        Assert.True(loaded.Connections[0].Config.InCredentialManager);
        Assert.Equal("the container", loaded.Connections[0].Config.Description);
    }

    [Fact]
    public void Secrets_ResolveFromTheStore_SaveIntoIt_AndNameTheFixWhenEmpty()
    {
        Assert.Equal("plain", OracleSecrets.Resolve(Named(new OracleConnectionConfig { DataSource = "x", User = "u", Password = "plain" })).Value);
        Assert.Equal("kept", OracleSecrets.Resolve(Named(new OracleConnectionConfig { DataSource = "x", User = "u", Password = WindowsCredentials.Protect("kept").Value })).Value);
        Assert.Equal(OracleText.NoPassword("prod"), OracleSecrets.Resolve(Named(new OracleConnectionConfig { DataSource = "x", User = "u" })).Error);
        Assert.Contains("the Oracle tab of /tools", OracleSecrets.Resolve(Named(new OracleConnectionConfig { DataSource = "x", User = "u", Password = WindowsCredentials.ProtectedPrefix + "bm90LWRwYXBp" })).Error);

        var credman = Named(new OracleConnectionConfig { DataSource = "x", User = "ledger_ro", PasswordStore = "credman", Credential = _target });
        Assert.Equal(OracleText.NoCredential(_target), OracleSecrets.Resolve(credman).Error);
        Assert.Equal((true, SqlText.PasswordSavedToCredman("prod", _target)), OracleSecrets.Save(credman, "typed"));
        Assert.Equal("typed", OracleSecrets.Resolve(credman).Value);
        Assert.Equal("NeonSidekick/oracle/prod", new OracleConnectionConfig().CredentialTarget("prod"));

        Profile("""{ "connections": { "prod": { "dataSource": "x:1/y", "user": "u" } } }""");
        Assert.Equal((true, SqlText.PasswordSavedToFile("prod", ProfilePath)), OracleSecrets.Save(Named(new OracleConnectionConfig { DataSource = "x", User = "u" }, ProfilePath), "typed"));
        var loaded = OracleConfigFile.Load(ProfilePath);
        Assert.StartsWith(WindowsCredentials.ProtectedPrefix, loaded.Connections[0].Config.Password);
        Assert.Equal("typed", OracleSecrets.Resolve(loaded.Connections[0]).Value);
    }

    [Theory]
    [InlineData("hr", "HR")]
    [InlineData(" Sales_2026$ ", "SALES_2026$")]
    [InlineData("\"Mixed Case\"", "Mixed Case")]
    [InlineData("a#b", "A#B")]
    [InlineData("", null)]
    [InlineData("1abc", null)]
    [InlineData("two words", null)]
    [InlineData("x;drop", null)]
    [InlineData("\"a\"\"b\"", null)]
    [InlineData("\"\"", null)]
    public void Names_NormalizeAsTheDictionarySpellsThem(string name, string? normal) => Assert.Equal(normal, OracleIdentifier.Normalize(name));

    [Fact]
    public void Names_QuoteAndSplit()
    {
        Assert.Equal("\"HR\"", OracleIdentifier.Quote("hr"));
        Assert.Null(OracleIdentifier.Quote("hr; ALTER"));
        Assert.Equal(("HR", "EMPLOYEES"), OracleIdentifier.Split("hr.employees")!.Value);
        Assert.Equal(((string?)null, "EMPLOYEES"), OracleIdentifier.Split("employees")!.Value);
        Assert.Equal(("Hr", "Emp.Log"), OracleIdentifier.Split("\"Hr\".\"Emp.Log\"")!.Value);
        Assert.Null(OracleIdentifier.Split("a.b.c"));
        Assert.Null(OracleIdentifier.Split("hr.\"open"));
        Assert.Null(OracleIdentifier.Split(" "));
    }

    [Fact]
    public void AParameter_IsTypedByItsValue_BoundByName()
    {
        Assert.Equal(OracleDbType.Varchar2, OracleAccess.Bind(new SqlParameterValue("n", "x")).OracleDbType);
        Assert.Equal(OracleDbType.Clob, OracleAccess.Bind(new SqlParameterValue("n", new string('x', 4001))).OracleDbType);
        Assert.Equal(OracleDbType.Int64, OracleAccess.Bind(new SqlParameterValue("n", 5L)).OracleDbType);
        Assert.Equal(OracleDbType.Decimal, OracleAccess.Bind(new SqlParameterValue("n", 1.5m)).OracleDbType);
        Assert.Equal(OracleDbType.BinaryDouble, OracleAccess.Bind(new SqlParameterValue("n", 1.5d)).OracleDbType);
        var flag = OracleAccess.Bind(new SqlParameterValue("n", true));
        Assert.Equal((OracleDbType.Int32, (object)1), (flag.OracleDbType, flag.Value));
        Assert.Equal(DBNull.Value, OracleAccess.Bind(new SqlParameterValue("n", null)).Value);
        Assert.Equal("id", OracleAccess.Bind(new SqlParameterValue("id", 1L)).ParameterName);
        Assert.Equal(23, OracleAccess.Release("23.26.3.0.0"));
        Assert.Equal(19, OracleAccess.Release("19.0.0.0.0"));
        Assert.Equal(0, OracleAccess.Release(null));
    }
}
