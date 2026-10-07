using Microsoft.Data.SqlClient;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary><c>sql.json</c> (2026-09-23): the shape, the problems it reports, the profile over the home, and the connection string it builds.</summary>
public sealed class SqlConfigFileTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _profile;

    public SqlConfigFileTests()
    {
        _profile = Path.Combine(_home, "profiles", "default");
        Directory.CreateDirectory(_profile);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private void Profile(string json) => File.WriteAllText(SqlConfigFile.ProfilePath(_profile), json);

    private void Global(string json) => File.WriteAllText(SqlConfigFile.GlobalPath(_home), json);

    [Fact]
    public void AMissingFile_IsEmpty_AndTheEmptyShape_LoadsEmpty()
    {
        Assert.Same(SqlCatalog.Empty, SqlConfigFile.Load(SqlConfigFile.ProfilePath(_profile)));
        string path = SqlConfigFile.ProfilePath(_profile);
        Assert.True(SqlConfigFile.EnsureExists(path));
        Assert.False(SqlConfigFile.EnsureExists(path));
        Assert.Equal(SqlConfigFile.EmptyText, File.ReadAllText(path));
        var loaded = SqlConfigFile.Load(path);
        Assert.Empty(loaded.Connections);
        Assert.Empty(loaded.Problems);
    }

    /// <summary>The fresh file's commented examples (later on 2026-09-23): one of each kind, and each a usable connection once its <c>//</c> are gone.</summary>
    [WindowsFact]   // a Mac's template has no runas examples: MacTidyUpTests.OnMacOS_TheSqlTemplate_KeepsWindowsSignIn_DropsRunAs_AndEachExampleLoads (2026-10-06)
    public void TheEmptyShapesExamples_AreEachAUsableConnection_OnceUncommented()
    {
        var example = SqlConfigFile.EmptyText.Split('\n')
            .Where(l => l.StartsWith("  // ", StringComparison.Ordinal))
            .Select(l => l["  // ".Length..])
            .Where(l => l.EndsWith('{') || l.EndsWith(',') || l.EndsWith('}') || l.EndsWith('"') || l.EndsWith("true", StringComparison.Ordinal))
            .Where(l => l.StartsWith('"') || l.StartsWith("  ", StringComparison.Ordinal) || l.StartsWith('}'));
        Profile("{ \"connections\": {\n" + string.Join("\n", example) + "\n} }");

        var loaded = SqlConfigFile.Load(SqlConfigFile.ProfilePath(_profile));

        Assert.Empty(loaded.Problems);
        Assert.Equal(["adventureworks", "reports-me", "reports-admin", "reports-admin-file"], loaded.Connections.Select(c => c.Name));
        Assert.Equal(["sql", "windows", "runas", "runas"], loaded.Connections.Select(c => c.Config.Auth));
        Assert.True(loaded.Connections[2].Config.InCredentialManager);
        Assert.Equal(@"CONTOSO\svc-reader", loaded.Connections[2].Config.User);
        Assert.Contains(@"cmdkey /generic:NeonSidekick/sql/reports-admin /user:CONTOSO\svc-reader /pass", SqlConfigFile.EmptyText);
    }

    [Fact]
    public void Connections_LoadInFileOrder_AndABadEntryIsAProblem_NotAThrow()
    {
        Profile("""
            {
              // comments and a trailing comma are fine
              "connections": {
                "adventureworks": { "server": "127.0.0.1,1433", "database": "AdventureWorks2022", "user": "sa", "password": "p", "trustServerCertificate": true, "encrypt": "optional", "description": "the sample" },
                "corp": { "server": "corp\\inst", "auth": "windows" },
                "nouser": { "server": "x" },
                "noserver": { "user": "u" },
                "badauth": { "server": "x", "auth": "entra", "user": "u" },
                "badencrypt": { "server": "x", "user": "u", "encrypt": "sometimes" },
                "slow": { "server": "x", "user": "u", "connectTimeoutSeconds": 0 },
                "empty": null,
              }
            }
            """);

        var loaded = SqlConfigFile.Load(SqlConfigFile.ProfilePath(_profile));

        Assert.Equal(["adventureworks", "corp"], loaded.Connections.Select(c => c.Name));
        var path = SqlConfigFile.ProfilePath(_profile);
        Assert.Equal(
            [
                new SqlConfigProblem($"{path} (nouser)", SqlText.NoUser, Name: "nouser"),
                new SqlConfigProblem($"{path} (noserver)", SqlText.NoServer, Name: "noserver"),
                new SqlConfigProblem($"{path} (badauth)", SqlText.BadAuth("entra"), Name: "badauth"),
                new SqlConfigProblem($"{path} (badencrypt)", SqlText.BadEncrypt("sometimes"), Name: "badencrypt"),
                new SqlConfigProblem($"{path} (slow)", SqlText.BadConnectTimeout(0, SqlConnectionConfig.MaxConnectTimeoutSeconds), Name: "slow"),
                new SqlConfigProblem($"{path} (empty)", SqlText.NoServer, Name: "empty"),
            ],
            loaded.Problems);
        Assert.True(loaded.Connections[1].Config.IsWindows);
    }

    [Fact]
    public void AFileThatIsNotJson_IsOneProblem()
    {
        Profile("{ not json");
        var loaded = SqlConfigFile.Load(SqlConfigFile.ProfilePath(_profile));
        Assert.Empty(loaded.Connections);
        var problem = Assert.Single(loaded.Problems);
        Assert.StartsWith("the file cannot be read (", problem.Reason);
    }

    [Fact]
    public void TheProfilesFile_WinsByName_OverTheHomes_AndFindPicksByNameThenDefaultThenFirst()
    {
        Profile("""{ "connections": { "Shared": { "server": "profile-server", "user": "u" }, "mine": { "server": "m", "user": "u" } } }""");
        Global("""{ "connections": { "shared": { "server": "home-server", "user": "u" }, "theirs": { "server": "t", "user": "u" } } }""");

        var catalog = SqlConfigFile.LoadCatalog(_profile, _home);

        Assert.Equal(["Shared", "mine", "theirs"], catalog.Connections.Select(c => c.Name));
        Assert.Equal("profile-server", catalog.Find("shared", null)!.Config.Server);   // names are case-insensitive
        Assert.Equal("theirs", catalog.Find(null, "THEIRS")!.Name);
        Assert.Equal("Shared", catalog.Find(null, "gone")!.Name);   // a default no longer there = the first
        Assert.Equal("Shared", catalog.Find(null, "")!.Name);
        Assert.Null(catalog.Find("nope", null));
        Assert.Equal(["Shared", "mine"], SqlConfigFile.LoadCatalog(_profile, null).Connections.Select(c => c.Name));
    }

    [Fact]
    public void TheBuilder_IsReadOnlyIntent_TheDatabaseOverridable_AndTheLoginAsConfigured()
    {
        var sql = new SqlConnectionConfig { Server = " 127.0.0.1,1433 ", Database = "AdventureWorks2022", User = "sa", Password = "secret", Encrypt = "Optional", TrustServerCertificate = true };
        var builder = sql.Builder();
        Assert.Equal("127.0.0.1,1433", builder.DataSource);
        Assert.Equal("AdventureWorks2022", builder.InitialCatalog);
        Assert.Equal(ApplicationIntent.ReadOnly, builder.ApplicationIntent);
        Assert.Equal("NeonSidekick", builder.ApplicationName);
        Assert.Equal(SqlConnectionEncryptOption.Optional, builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
        Assert.Equal(SqlConnectionConfig.DefaultConnectTimeoutSeconds, builder.ConnectTimeout);
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("sa", builder.UserID);
        Assert.Equal("master", sql.Builder("master").InitialCatalog);

        var windows = new SqlConnectionConfig { Server = "corp", Auth = "Windows", User = "ignored" }.Builder();
        Assert.True(windows.IntegratedSecurity);
        Assert.Equal("", windows.UserID);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, windows.Encrypt);   // the default
        Assert.Equal("", windows.InitialCatalog);   // the login's default database
        Assert.Equal(SqlConnectionEncryptOption.Strict, new SqlConnectionConfig { Server = "x", User = "u", Encrypt = "strict" }.Builder().Encrypt);
    }

    [Fact]
    public void RunAs_AndThePasswordStores_AreChecked()
    {
        Assert.Null(new SqlConnectionConfig { Server = "x", Auth = "runas", User = @"CONTOSO\svc" }.Problem);
        Assert.Null(new SqlConnectionConfig { Server = "x", Auth = "RunAs", User = "svc@contoso.com", PasswordStore = "credman" }.Problem);
        Assert.Equal(SqlText.RunAsNeedsDomain("svc"), new SqlConnectionConfig { Server = "x", Auth = "runas", User = "svc" }.Problem);
        Assert.Equal(SqlText.RunAsNeedsDomain(""), new SqlConnectionConfig { Server = "x", Auth = "runas" }.Problem);
        Assert.Equal(SqlText.BadPasswordStore("vault"), new SqlConnectionConfig { Server = "x", User = "u", PasswordStore = "vault" }.Problem);
        Assert.Equal(SqlText.CredmanWithWindows, new SqlConnectionConfig { Server = "x", Auth = "windows", PasswordStore = "credman" }.Problem);
        Assert.Equal(SqlText.BadAuth("entra"), new SqlConnectionConfig { Server = "x", Auth = "entra", User = "u" }.Problem);

        var runas = new SqlConnectionConfig { Server = "sqlhost01,1453", Database = "db", Auth = "runas", User = @"CONTOSO\svc", Password = "never used" }.Builder(password: "p");
        Assert.True(runas.IntegratedSecurity);
        Assert.False(runas.Pooling);   // an integrated pool is keyed by the process's SID, which the netonly token keeps
        Assert.Equal("NeonSidekick (runas)", runas.ApplicationName);
        Assert.Equal("", runas.UserID);
        Assert.Equal("", runas.Password);
        Assert.Equal("p", new SqlConnectionConfig { Server = "x", User = "u", Password = "dpapi:…" }.Builder(password: "p").Password);   // the resolved password, never the stored value
    }

    [WindowsFact]
    public void APlainPassword_IsEncryptedInPlace_TheRestOfTheFileUntouched()
    {
        string text = """
            {
              // the dev box — keep this comment
              "connections": {
                "a": { "server": "x", "user": "sa", "password": "hunter2" },   // trailing note
                "b": { "server": "y", "auth": "windows" },
                "c": { "server": "z", "user": "u", "passwordStore": "credman", "password": "left alone" },
              }
            }
            """;
        Profile(text);
        string path = SqlConfigFile.ProfilePath(_profile);

        var loaded = SqlConfigFile.Load(path);

        string after = File.ReadAllText(path);
        Assert.DoesNotContain("hunter2", after);
        Assert.Contains("// the dev box — keep this comment", after);
        Assert.Contains("},   // trailing note", after);
        Assert.Contains("\"password\": \"left alone\"", after);   // the credman store's password is not the file's to keep
        int start = after.IndexOf("\"password\": \"dpapi:", StringComparison.Ordinal);
        Assert.True(start > 0);
        Assert.Equal(text[..text.IndexOf("\"password\"", StringComparison.Ordinal)], after[..start]);   // everything before the value byte for byte
        Assert.Equal("hunter2", SqlSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.StartsWith("dpapi:", loaded.Connections[0].Config.Password);

        string again = File.ReadAllText(path);
        SqlConfigFile.Load(path);
        Assert.Equal(again, File.ReadAllText(path));   // once: an encrypted value is left as it is
    }

    /// <summary>
    /// The macOS twin of <see cref="APlainPassword_IsEncryptedInPlace_TheRestOfTheFileUntouched"/> (2026-10-06, the macOS build): the
    /// plain password becomes a <c>keychain:</c> value, everything before it byte for byte, and only once.
    /// </summary>
    [UnixFact]
    public void APlainPassword_IsEncryptedInPlace_AsAKeychainValueOnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string text = """
            {
              // the dev box — keep this comment
              "connections": {
                "a": { "server": "x", "user": "sa", "password": "hunter2" },   // trailing note
                "c": { "server": "z", "user": "u", "passwordStore": "credman", "password": "left alone" },
              }
            }
            """;
        Profile(text);
        string path = SqlConfigFile.ProfilePath(_profile);

        var loaded = SqlConfigFile.Load(path);

        string after = File.ReadAllText(path);
        Assert.DoesNotContain("hunter2", after);
        Assert.Contains("},   // trailing note", after);
        Assert.Contains("\"password\": \"left alone\"", after);
        int start = after.IndexOf("\"password\": \"keychain:", StringComparison.Ordinal);
        Assert.True(start > 0);
        Assert.Equal(text[..text.IndexOf("\"password\"", StringComparison.Ordinal)], after[..start]);
        Assert.Equal("hunter2", SqlSecrets.Resolve(loaded.Connections[0]).Value);

        SqlConfigFile.Load(path);
        Assert.Equal(after, File.ReadAllText(path));
    }

    [WindowsFact]
    public void EncryptAll_ReadsTheHomesFile_AndEveryProfiles_NotOnlyTheLoadedOnes()
    {
        const string plain = """{ "connections": { "a": { "server": "x", "user": "u", "password": "hunter2" } } }""";
        string work = Path.Combine(_home, "profiles", "work");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(_home, "profiles", "empty"));   // a profile without a sql.json
        Global(plain);
        Profile(plain);
        File.WriteAllText(SqlConfigFile.ProfilePath(work), plain);

        var read = SqlConfigFile.EncryptAll(_home);

        Assert.Equal([SqlConfigFile.GlobalPath(_home), SqlConfigFile.ProfilePath(_profile), SqlConfigFile.ProfilePath(work)], read);
        foreach (string path in read)
        {
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("hunter2", text);
            Assert.Contains("\"password\": \"dpapi:", text);
        }

        Assert.Empty(SqlConfigFile.EncryptAll(Path.Combine(_home, "nowhere")));   // no home yet: nothing to read
    }

    /// <summary>The macOS twin of <see cref="EncryptAll_ReadsTheHomesFile_AndEveryProfiles_NotOnlyTheLoadedOnes"/> (2026-10-06): <c>keychain:</c> values.</summary>
    [UnixFact]
    public void EncryptAll_ReadsTheHomesFile_AndEveryProfiles_AsKeychainValuesOnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        const string plain = """{ "connections": { "a": { "server": "x", "user": "u", "password": "hunter2" } } }""";
        string work = Path.Combine(_home, "profiles", "work");
        Directory.CreateDirectory(work);
        Global(plain);
        Profile(plain);
        File.WriteAllText(SqlConfigFile.ProfilePath(work), plain);

        var read = SqlConfigFile.EncryptAll(_home);

        Assert.Equal([SqlConfigFile.GlobalPath(_home), SqlConfigFile.ProfilePath(_profile), SqlConfigFile.ProfilePath(work)], read);
        foreach (string path in read)
        {
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("hunter2", text);
            Assert.Contains("\"password\": \"keychain:", text);
        }
    }

    [Fact]
    public void WritePassword_ReplacesTheValue_OrInsertsTheKey()
    {
        Profile("""{ "connections": { "a": { "server": "x", "user": "u", "password": null }, "b": { "server": "y", "user": "v" }, "c": { "server": "z" } } }""");
        string path = SqlConfigFile.ProfilePath(_profile);

        Assert.Null(SqlConfigFile.WritePassword(path, "a", "dpapi:AAA+/="));
        Assert.Null(SqlConfigFile.WritePassword(path, "b", "dpapi:BBB"));
        Assert.Null(SqlConfigFile.WritePassword(path, "c", "dpapi:CCC"));
        Assert.Equal(
            """{ "connections": { "a": { "server": "x", "user": "u", "password": "dpapi:AAA+/=" }, "b": { "server": "y", "user": "v", "password": "dpapi:BBB" }, "c": { "password": "dpapi:CCC", "server": "z" } } }""",
            File.ReadAllText(path));
        Assert.Equal(SqlText.ConnectionNotInFile("nope"), SqlConfigFile.WritePassword(path, "nope", "dpapi:x"));
    }

    /// <summary>
    /// <see cref="SqlConfigFile.AddConnection"/> (later on 2026-09-23, the add-connection wizard): a missing file made with its
    /// commented shape and the entry put inside its empty <c>connections</c>, the comments kept, the password left out; a
    /// second entry after the first, a comma between; the name taken refused, as the loader would match it.
    /// </summary>
    [Fact]
    public void AddConnection_MakesTheFile_KeepsItsComments_AndAppendsAfterTheLast()
    {
        string path = SqlConfigFile.ProfilePath(_profile);
        var first = new SqlConnectionConfig { Server = "127.0.0.1,1433", Database = "AdventureWorks2022", Auth = "sql", User = "reader", Password = "never-written", PasswordStore = "file", Encrypt = "mandatory", TrustServerCertificate = true, Description = "the sample" };

        Assert.Null(SqlConfigFile.AddConnection(path, " aw ", first));
        Assert.Null(SqlConfigFile.AddConnection(path, "admin", new SqlConnectionConfig { Server = "sqlhost01", Auth = "runas", User = @"CONTOSO\svc-reader", PasswordStore = "credman", ConnectTimeoutSeconds = 30 }));

        string text = File.ReadAllText(path);
        Assert.StartsWith(SqlConfigFile.EmptyText[..SqlConfigFile.EmptyText.IndexOf("  \"connections\"", StringComparison.Ordinal)], text);
        Assert.DoesNotContain("never-written", text);
        Assert.Equal("never-written", first.Password);   // the caller's draft keeps it
        Assert.Contains("  \"connections\": {\n    \"aw\": {\n      \"server\": \"127.0.0.1,1433\",\n", text);
        Assert.Contains("      \"description\": \"the sample\"\n    },\n    \"admin\": {\n", text);
        Assert.Contains("\"user\": \"CONTOSO\\\\svc-reader\"", text);
        Assert.EndsWith("      \"connectTimeoutSeconds\": 30\n    }\n  }\n}\n", text);
        var loaded = SqlConfigFile.Load(path);
        Assert.Empty(loaded.Problems);
        Assert.Equal(["aw", "admin"], loaded.Connections.Select(c => c.Name));
        Assert.Equal(@"CONTOSO\svc-reader", loaded.Connections[1].Config.User);
        Assert.True(loaded.Connections[0].Config.TrustServerCertificate);

        Assert.Equal(SqlText.ConnectionAlreadyInFile("AW"), SqlConfigFile.AddConnection(path, "AW", first));
        Assert.Equal(text, File.ReadAllText(path));
    }

    /// <summary>The other shapes: no <c>connections</c> key (made after the root's brace), an empty one holding a comment, the file's CRLF and BOM kept; a file that is not an object refused.</summary>
    [Fact]
    public void AddConnection_FitsTheFilesShape_OrRefusesOne_ItCannotWriteInto()
    {
        string path = SqlConfigFile.ProfilePath(_profile);
        var windows = new SqlConnectionConfig { Server = "y", Auth = "windows" };

        Profile("{ \"other\": 1 }");
        Assert.Null(SqlConfigFile.AddConnection(path, "me", windows));
        Assert.Equal(["me"], SqlConfigFile.Load(path).Connections.Select(c => c.Name));
        Assert.EndsWith("    }\n  }, \"other\": 1 }", File.ReadAllText(path));

        Profile("{}");
        Assert.Null(SqlConfigFile.AddConnection(path, "me", windows));
        Assert.Equal(["me"], SqlConfigFile.Load(path).Connections.Select(c => c.Name));

        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{\r\n  \"connections\": { // none yet\r\n  }\r\n}\r\n")]);
        Assert.Null(SqlConfigFile.AddConnection(path, "me", windows));
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        string text = System.Text.Encoding.UTF8.GetString(bytes[3..]);
        Assert.Contains("\"connections\": {\r\n    \"me\": {\r\n      \"server\": \"y\",\r\n", text);
        Assert.Contains("// none yet", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.Equal(["me"], SqlConfigFile.Load(path).Connections.Select(c => c.Name));

        Profile("""{ "connections": [] }""");
        Assert.Equal(SqlText.ConnectionsNotAnObject, SqlConfigFile.AddConnection(path, "me", windows));
        Profile("[]");
        Assert.Equal(SqlText.FileNotAnObject, SqlConfigFile.AddConnection(path, "me", windows));
        Profile("{ \"connections\": { ");
        Assert.NotNull(SqlConfigFile.AddConnection(path, "me", windows));
    }

    /// <summary>
    /// <see cref="SqlConfigFile.ReplaceConnection"/> (2026-10-05, the wizard's edit): the entry's bytes swapped where they stood, its
    /// indent kept, renamed, its password left out; the comments and the other entries untouched; another entry's name, a missing
    /// entry, a missing file and a file that is no object refused with the file as it was.
    /// </summary>
    [Fact]
    public void ReplaceConnection_SwapsTheEntryInPlace_KeepsTheComments_AndRefusesWhatItCannot()
    {
        string path = SqlConfigFile.ProfilePath(_profile);
        var windows = new SqlConnectionConfig { Server = "y", Auth = "windows" };
        Assert.Equal(SqlText.ConnectionNotInFile("aw"), SqlConfigFile.ReplaceConnection(path, "aw", "aw", windows));   // no file

        Profile("{\n  // mine\n  \"connections\": {\n    \"aw\": { \"server\": \"old\", \"auth\": \"windows\" }, // the sample\n    \"other\": { \"server\": \"o\", \"auth\": \"windows\" }\n  }\n}\n");
        Assert.Null(SqlConfigFile.ReplaceConnection(path, " AW ", "adventure", new SqlConnectionConfig { Server = "new", Auth = "windows", Password = "never-written", Description = "d" }));

        string text = File.ReadAllText(path);
        Assert.StartsWith("{\n  // mine\n  \"connections\": {\n    \"adventure\": {\n      \"server\": \"new\",\n", text);
        Assert.Contains("\n    }, // the sample\n    \"other\": { \"server\": \"o\", \"auth\": \"windows\" }\n  }\n}\n", text);
        Assert.DoesNotContain("never-written", text);
        var loaded = SqlConfigFile.Load(path);
        Assert.Empty(loaded.Problems);
        Assert.Equal(["adventure", "other"], loaded.Connections.Select(c => c.Name));
        Assert.Equal(("new", "d"), (loaded.Connections[0].Config.Server, loaded.Connections[0].Config.Description));

        Assert.Equal(SqlText.ConnectionAlreadyInFile("OTHER"), SqlConfigFile.ReplaceConnection(path, "adventure", "OTHER", windows));
        Assert.Equal(SqlText.ConnectionNotInFile("gone"), SqlConfigFile.ReplaceConnection(path, "gone", "x", windows));
        Assert.Equal(text, File.ReadAllText(path));
        Assert.Null(SqlConfigFile.ReplaceConnection(path, "adventure", "Adventure", windows));   // its own name in another case
        Assert.Equal(["Adventure", "other"], SqlConfigFile.Load(path).Connections.Select(c => c.Name));

        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{\r\n  \"connections\": {\r\n    \"me\": { \"server\": \"x\", \"auth\": \"windows\" }\r\n  }\r\n}\r\n")]);
        Assert.Null(SqlConfigFile.ReplaceConnection(path, "me", "me", windows));
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        string crlf = System.Text.Encoding.UTF8.GetString(bytes[3..]);
        Assert.Contains("    \"me\": {\r\n      \"server\": \"y\",\r\n", crlf);
        Assert.DoesNotContain("\n", crlf.Replace("\r\n", "", StringComparison.Ordinal));

        Profile("[]");
        Assert.Equal(SqlText.FileNotAnObject, SqlConfigFile.ReplaceConnection(path, "me", "me", windows));
        Profile("""{ "connections": [] }""");
        Assert.Equal(SqlText.ConnectionsNotAnObject, SqlConfigFile.ReplaceConnection(path, "me", "me", windows));
    }

    /// <summary>The other families' replace (2026-10-05): the share and database nouns in a refusal, a family's entry renamed.</summary>
    [Fact]
    public void TheOtherFamilies_ReplaceTheirEntries_WithTheirNouns()
    {
        string unc = NeonSidekick.Unc.UncConfigFile.ProfilePath(_profile);
        File.WriteAllText(unc, """{ "shares": { "eng": { "path": "//fs01/eng" } } }""");
        Assert.Null(NeonSidekick.Unc.UncConfigFile.ReplaceShare(unc, "eng", "docs", new NeonSidekick.Unc.UncShareConfig { Path = "//fs01/docs" }));
        Assert.Equal(["docs"], NeonSidekick.Unc.UncConfigFile.Load(unc).Shares.Select(s => s.Name));
        Assert.Equal(SqlText.ConnectionNotInFile("eng", "share"), NeonSidekick.Unc.UncConfigFile.ReplaceShare(unc, "eng", "x", new NeonSidekick.Unc.UncShareConfig { Path = "//fs01/x" }));

        string sqlite = NeonSidekick.Sqlite.SqliteConfigFile.ProfilePath(_profile);
        File.WriteAllText(sqlite, """{ "databases": { "notes": { "path": "notes.db" } } }""");
        Assert.Null(NeonSidekick.Sqlite.SqliteConfigFile.ReplaceDatabase(sqlite, "notes", "journal", new NeonSidekick.Sqlite.SqliteDatabaseConfig { Path = "journal.db" }));
        Assert.Equal(["journal"], NeonSidekick.Sqlite.SqliteConfigFile.Load(sqlite).Databases.Select(d => d.Name));
        Assert.Equal(SqlText.ConnectionNotInFile("notes", "database"), NeonSidekick.Sqlite.SqliteConfigFile.ReplaceDatabase(sqlite, "notes", "x", new NeonSidekick.Sqlite.SqliteDatabaseConfig { Path = "x.db" }));

        string oracle = NeonSidekick.Oracle.OracleConfigFile.ProfilePath(_profile);
        File.WriteAllText(oracle, """{ "connections": { "free": { "dataSource": "localhost:1521/FREEPDB1", "user": "neon" } } }""");
        Assert.Null(NeonSidekick.Oracle.OracleConfigFile.ReplaceConnection(oracle, "free", "pdb", new NeonSidekick.Oracle.OracleConnectionConfig { DataSource = "localhost:1521/FREEPDB1", User = "reader" }));
        var pdb = Assert.Single(NeonSidekick.Oracle.OracleConfigFile.Load(oracle).Connections);
        Assert.Equal(("pdb", "reader"), (pdb.Name, pdb.Config.User));
    }

    [Fact]
    public void TheListing_NamesEveryConnection_TheDefaultMarked_AndNeverThePassword()
    {
        Profile("""{ "connections": { "aw": { "server": "127.0.0.1,1433", "database": "AdventureWorks2022", "user": "sa", "password": "hunter2", "description": "the sample sales database" }, "corp": { "server": "corp\\inst", "auth": "windows" }, "bad": { "server": "x" } } }""");
        var catalog = SqlConfigFile.LoadCatalog(_profile, _home);

        string text = SqlText.Connections(catalog, "corp");

        Assert.Equal(
            "2 SQL connections (every SQL tool takes one by name in \"connection\"; the default is used when it is left out):\n" +
            "- aw: 127.0.0.1,1433 / AdventureWorks2022, sql login sa — the sample sales database\n" +
            "- corp (default): corp\\inst / (the login's default database), windows sign-in\n" +
            $"Skipped {SqlConfigFile.ProfilePath(_profile)} (bad): {SqlText.NoUser}",
            text);
        Assert.DoesNotContain("hunter2", text);
        Assert.Equal(SqlText.NoConnections, SqlText.Connections(SqlCatalog.Empty, null));
    }
}
