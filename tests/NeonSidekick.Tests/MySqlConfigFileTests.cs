using MySqlConnector;
using NeonSidekick.MySql;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>mysql.json</c> (2026-09-30): the shape, the problems it reports, the profile over the home, the connection string and its
/// hardening, the passwords kept as the other two files keep theirs, the wizard's append, and the access's pure helpers.
/// </summary>
public sealed class MySqlConfigFileTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _profile;
    private readonly string _target = "NeonSidekick.Tests/mysql/" + Guid.NewGuid().ToString("N");

    public MySqlConfigFileTests()
    {
        _profile = Path.Combine(_home, "profiles", "default");
        Directory.CreateDirectory(_profile);
    }

    public void Dispose()
    {
        WindowsCredentials.DeleteGeneric(_target);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private string ProfilePath => MySqlConfigFile.ProfilePath(_profile);

    private void Profile(string json) => File.WriteAllText(ProfilePath, json);

    private static MySqlNamedConnection Named(MySqlConnectionConfig config, string source = "test") => new("prod", config, source);

    [WindowsFact]
    public void TheEmptyShapesExamples_AreEachAUsableConnection_OnceUncommented()
    {
        Assert.True(MySqlConfigFile.EnsureExists(ProfilePath));
        Assert.Empty(MySqlConfigFile.Load(ProfilePath).Connections);
        var example = MySqlConfigFile.EmptyText.Split('\n')
            .Where(l => l.StartsWith("  // ", StringComparison.Ordinal))
            .Select(l => l["  // ".Length..])
            .Where(l => l.EndsWith('{') || l.EndsWith(',') || l.EndsWith('}') || l.EndsWith('"'))
            .Where(l => l.StartsWith('"') || l.StartsWith("  ", StringComparison.Ordinal) || l.StartsWith('}'));
        Profile("{ \"connections\": {\n" + string.Join("\n", example) + "\n} }");

        var loaded = MySqlConfigFile.Load(ProfilePath);

        Assert.Empty(loaded.Problems);
        Assert.Equal(["shop", "billing"], loaded.Connections.Select(c => c.Name));
        Assert.StartsWith(WindowsCredentials.ProtectedPrefix, loaded.Connections[0].Config.Password);
        Assert.True(loaded.Connections[1].Config.InCredentialManager);
        Assert.Equal("verify-full", loaded.Connections[1].Config.SslMode);
    }

    /// <summary>The macOS twin of <see cref="TheEmptyShapesExamples_AreEachAUsableConnection_OnceUncommented"/> (2026-10-06): the plain example becomes a <c>keychain:</c> value.</summary>
    [UnixFact]
    public void TheEmptyShapesExamples_AreEachAUsableConnection_TheKeychainOnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var example = MySqlConfigFile.EmptyText.Split('\n')
            .Where(l => l.StartsWith("  // ", StringComparison.Ordinal))
            .Select(l => l["  // ".Length..])
            .Where(l => l.EndsWith('{') || l.EndsWith(',') || l.EndsWith('}') || l.EndsWith('"'))
            .Where(l => l.StartsWith('"') || l.StartsWith("  ", StringComparison.Ordinal) || l.StartsWith('}'));
        Profile("{ \"connections\": {\n" + string.Join("\n", example) + "\n} }");

        var loaded = MySqlConfigFile.Load(ProfilePath);

        Assert.Empty(loaded.Problems);
        Assert.Equal(["shop", "billing"], loaded.Connections.Select(c => c.Name));
        Assert.StartsWith(WindowsCredentials.KeychainPrefix, loaded.Connections[0].Config.Password);
        Assert.True(loaded.Connections[1].Config.InCredentialManager);
    }

    [Fact]
    public void Connections_LoadInFileOrder_AndABadEntryIsAProblem_NotAThrow()
    {
        Profile("""
            {
              "connections": {
                "shop": { "host": "localhost", "port": 3307, "database": "shop", "user": "reader" },
                "nohost": { "user": "u" },
                "nouser": { "host": "h" },
                "badport": { "host": "h", "user": "u", "port": 70000 },
                "badtls": { "host": "h", "user": "u", "sslMode": "sometimes" },
                "badstore": { "host": "h", "user": "u", "passwordStore": "vault" },
                "slow": { "host": "h", "user": "u", "connectTimeoutSeconds": 0 },
                "empty": null,
              }
            }
            """);

        var loaded = MySqlConfigFile.Load(ProfilePath);

        Assert.Equal("shop", Assert.Single(loaded.Connections).Name);
        Assert.Equal("localhost:3307", loaded.Connections[0].Config.Endpoint);
        Assert.Equal(
            [MySqlText.NoHost, MySqlText.NoUser, MySqlText.BadPort(70000), MySqlText.BadSslMode("sometimes"), SqlText.BadPasswordStore("vault"), SqlText.BadConnectTimeout(0, 120), MySqlText.NoHost],
            loaded.Problems.Select(p => p.Reason));
    }

    [Fact]
    public void TheProfilesFile_WinsByName_OverTheHomes()
    {
        Profile("""{ "connections": { "shop": { "host": "profile", "user": "u" } } }""");
        File.WriteAllText(MySqlConfigFile.GlobalPath(_home), """{ "connections": { "SHOP": { "host": "home", "user": "u" }, "billing": { "host": "home", "user": "u" } } }""");

        var catalog = MySqlConfigFile.LoadCatalog(_profile, _home);

        Assert.Equal(["shop", "billing"], catalog.Connections.Select(c => c.Name));
        Assert.Equal("profile", catalog.Find("Shop", null)!.Config.Host);
        Assert.Equal("billing", catalog.Find(null, "BILLING")!.Name);
        Assert.Equal(1, catalog.Offered(["billing"]).Hidden);
    }

    [Fact]
    public void TheBuilder_IsHardened_AndTheTlsModeIsTheServersWord()
    {
        var builder = new MySqlConnectionConfig { Host = " db ", User = " reader ", Database = "shop", SslMode = "verify-full", ConnectTimeoutSeconds = 7 }.Builder("secret");
        Assert.Equal("db", builder.Server);
        Assert.Equal(3306u, builder.Port);
        Assert.Equal("reader", builder.UserID);
        Assert.Equal("secret", builder.Password);
        Assert.Equal("shop", builder.Database);
        Assert.Equal(7u, builder.ConnectionTimeout);
        Assert.Equal(MySqlSslMode.VerifyFull, builder.SslMode);
        Assert.False(builder.AllowLoadLocalInfile);
        Assert.False(builder.AllowUserVariables);
        Assert.False(builder.AllowPublicKeyRetrieval);
        Assert.True(builder.ConvertZeroDateTime);
        Assert.Equal(MySqlSslMode.Preferred, new MySqlConnectionConfig { Host = "h", User = "u" }.Builder().SslMode);
        Assert.Equal(MySqlSslMode.None, new MySqlConnectionConfig { Host = "h", User = "u", SslMode = "None" }.Builder().SslMode);
        Assert.True(new MySqlConnectionConfig { Host = "h", User = "u", AllowPublicKeyRetrieval = true }.Builder().AllowPublicKeyRetrieval);
    }

    [Fact]
    public void AddConnection_AndThePasswordStores()
    {
        var config = new MySqlConnectionConfig { Host = "localhost", Database = "shop", User = "reader", Password = "never-written", Description = "the sample" };
        Assert.Null(MySqlConfigFile.AddConnection(ProfilePath, "shop", config));
        Assert.DoesNotContain("never-written", File.ReadAllText(ProfilePath));
        Assert.Equal(SqlText.ConnectionAlreadyInFile("SHOP"), MySqlConfigFile.AddConnection(ProfilePath, "SHOP", config));

        var saved = MySqlConfigFile.Load(ProfilePath).Connections[0];
        Assert.Equal(MySqlText.NoPassword("shop"), MySqlSecrets.Resolve(saved).Error);
        Assert.Equal((true, SqlText.PasswordSavedToFile("shop", ProfilePath)), MySqlSecrets.Save(saved, "typed"));
        Assert.Equal("typed", MySqlSecrets.Resolve(MySqlConfigFile.Load(ProfilePath).Connections[0]).Value);

        var credman = Named(new MySqlConnectionConfig { Host = "h", User = "ro", PasswordStore = "credman", Credential = _target });
        Assert.Equal(MySqlText.NoCredential(_target), MySqlSecrets.Resolve(credman).Error);
        Assert.Equal((true, SqlText.PasswordSavedToCredman("prod", _target)), MySqlSecrets.Save(credman, "kept"));
        Assert.Equal("kept", MySqlSecrets.Resolve(credman).Value);
        Assert.Equal("NeonSidekick/mysql/prod", new MySqlConnectionConfig().CredentialTarget("prod"));
    }

    [Theory]
    [InlineData("orders", null, "orders")]
    [InlineData("shop.orders", "shop", "orders")]
    [InlineData("`my.db`.`odd``name`", "my.db", "odd`name")]
    [InlineData(" shop . orders ", "shop", "orders")]
    public void TableReferences_Split(string reference, string? database, string name) =>
        Assert.Equal((database, name), MySqlAccess.Split(reference)!.Value);

    [Theory]
    [InlineData("a.b.c")]
    [InlineData("`open")]
    [InlineData("shop.")]
    [InlineData(" ")]
    public void ANonName_DoesNotSplit(string reference) => Assert.Null(MySqlAccess.Split(reference));

    [Fact]
    public void TheSessionStatement_StripsTheRiskyModes_AndCapsInTheServersOwnWord()
    {
        Assert.Equal("SET SESSION sql_mode = REPLACE(REPLACE(@@SESSION.sql_mode, 'NO_BACKSLASH_ESCAPES', ''), 'ANSI_QUOTES', ''), SESSION max_execution_time = 30000", MySqlAccess.SessionStatement("8.4.11", 30));
        Assert.EndsWith("SESSION max_statement_time = 30", MySqlAccess.SessionStatement("11.8.9-MariaDB-ubu2404", 30));
        Assert.True(MySqlAccess.IsMariaDb("10.11.6-MariaDB"));
        Assert.False(MySqlAccess.IsMariaDb("8.0.36"));
        Assert.Equal(MySqlDbType.Int64, MySqlAccess.Bind(new SqlParameterValue("id", 5L)).MySqlDbType);
        Assert.Equal("@id", MySqlAccess.Bind(new SqlParameterValue("id", 5L)).ParameterName);
        Assert.Equal(MySqlDbType.Bool, MySqlAccess.Bind(new SqlParameterValue("b", true)).MySqlDbType);
        Assert.Equal(DBNull.Value, MySqlAccess.Bind(new SqlParameterValue("n", null)).Value);
    }

    [Fact]
    public void WritePowers_ReadTheGrants()
    {
        Assert.Empty(MySqlText.WritePowers(["GRANT USAGE ON *.* TO `shop_reader`@`%`", "GRANT SELECT, SHOW VIEW ON `shop`.* TO `shop_reader`@`%`"]));
        Assert.Equal(["ALL PRIVILEGES ON `neon`.*"], MySqlText.WritePowers(["GRANT USAGE ON *.* TO `neon`@`%` IDENTIFIED BY PASSWORD '*79'", "GRANT ALL PRIVILEGES ON `neon`.* TO `neon`@`%`"]));
        Assert.Equal(["INSERT, UPDATE ON `shop`.`orders`"], MySqlText.WritePowers(["GRANT SELECT, INSERT, UPDATE ON `shop`.`orders` TO `app`@`%`", "GRANT `role_ro`@`%` TO `app`@`%`"]));
    }
}
