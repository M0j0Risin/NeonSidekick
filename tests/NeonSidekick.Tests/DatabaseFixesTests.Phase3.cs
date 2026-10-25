using System.Text;
using NeonSidekick.App;
using NeonSidekick.Computers;
using NeonSidekick.MySql;
using NeonSidekick.Oracle;
using NeonSidekick.Postgres;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;

namespace NeonSidekick.Tests;

/// <summary>
/// Phase 3 of the database review's fixes (2026-10-24): the connection files, the secrets and the sign-in — mechanisms 1 to 3, decisions DB9
/// and DB10. Each test cites its finding.
/// </summary>
public sealed class DatabaseConnectionFilesTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _profile;
    private readonly string _target = "NeonSidekick/mysql/tests-" + Guid.NewGuid().ToString("N");
    private readonly IDisposable _cache = ConfigCache.Scope();
    private readonly IDisposable _guard = SignInGuard.Scope();

    public DatabaseConnectionFilesTests()
    {
        _profile = Path.Combine(_home, "profiles", "default");
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_profile));
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_home));
    }

    public void Dispose()
    {
        _cache.Dispose();
        _guard.Dispose();
        WindowsCredentials.DeleteGeneric(_target);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string MySqlProfile => MySqlConfigFile.ProfilePath(_profile);

    private string MySqlHome => MySqlConfigFile.GlobalPath(_home);

    /// <summary>K2: a key the entry does not know refuses it, naming the nearest; a key beside <c>connections</c> is a problem; the rest load.</summary>
    [Fact]
    public void AnUnknownKey_RefusesTheEntry_NamingTheNearest()
    {
        File.WriteAllText(PostgresConfigFile.ProfilePath(_profile), """
            { "connections": {
                "locked": { "host": "h", "database": "d", "user": "u", "password": "dpapi:x", "schma": "sales" },
                "fine": { "host": "h", "database": "d", "user": "u", "password": "dpapi:x" } },
              "conections": {} }
            """);
        var loaded = PostgresConfigFile.Load(PostgresConfigFile.ProfilePath(_profile));
        Assert.Equal(["fine"], loaded.Connections.Select(c => c.Name));
        var problem = Assert.Single(loaded.Problems, p => p.Name == "locked");
        Assert.Equal(ComputerText.UnknownKey("schma", "schema"), problem.Reason);
        Assert.Contains(loaded.Problems, p => p.Name is null && p.Reason.Contains("conections", StringComparison.Ordinal));
    }

    /// <summary>D2 and N5: a name given twice refuses it; a section given twice refuses the file whole.</summary>
    [Fact]
    public void ANameTwice_AndASectionTwice_AreRefused()
    {
        File.WriteAllText(MySqlProfile, """{ "connections": { "a": { "host": "h", "database": "d", "user": "u" }, "A": { "host": "h2", "database": "d", "user": "u" }, "b": { "host": "h", "database": "d", "user": "u" } } }""");
        var loaded = MySqlConfigFile.Load(MySqlProfile);
        Assert.Equal(["b"], loaded.Connections.Select(c => c.Name));
        Assert.Contains(loaded.Problems, p => p.Reason == ComputerText.NamedTwice);

        File.WriteAllText(MySqlProfile, """{ "connections": { "a": { "host": "h", "database": "d", "user": "u" } }, "Connections": { "b": { "host": "h", "database": "d", "user": "u" } } }""");
        var twice = MySqlConfigFile.Load(MySqlProfile);
        Assert.Empty(twice.Connections);
        Assert.Equal(SqlText.SectionTwice("connections"), Assert.Single(twice.Problems).Reason);
        Assert.Equal(SqlText.SectionTwice("connections"), MySqlConfigFile.AddConnection(MySqlProfile, "c", new MySqlConnectionConfig { Host = "h", Database = "d", User = "u" }));
    }

    /// <summary>D1: a profile entry with a problem still hides the home's of its name (a broken lock-down never falls back to the home's).</summary>
    [Fact]
    public void ARefusedProfileEntry_HidesTheHomes()
    {
        File.WriteAllText(MySqlProfile, """{ "connections": { "prod": { "host": "h", "user": "u" } } }""");   // no database: refused
        File.WriteAllText(MySqlHome, """{ "connections": { "prod": { "host": "h", "database": "d", "user": "u", "access": "readwrite" }, "other": { "host": "h", "database": "d", "user": "u" } } }""");
        var catalog = MySqlConfigFile.LoadCatalog(_profile, _home);
        Assert.Equal(["other"], catalog.Connections.Select(c => c.Name));
        Assert.Contains(catalog.Problems, p => p.Name == "prod");

        // H9: the police still sees both prods' hosts.
        Assert.Equal(3, MySqlConfigFile.Family.Everything(_profile, _home).Count);
    }

    /// <summary>J5 and N11: an offered catalog keeps only the ticked entries' problems; a null name in the list is skipped.</summary>
    [Fact]
    public void AnOfferedCatalog_KeepsOnlyTheTickedProblems()
    {
        File.WriteAllText(MySqlProfile, """{ "connections": { "ticked": { "host": "h", "user": "u" }, "unticked": { "host": "h", "user": "secret-admin" }, "ok": { "host": "h", "database": "d", "user": "u" } } }""");
        var offered = MySqlConfigFile.LoadCatalog(_profile, null).Offered(["ticked", null!, " ", "ok"]);
        Assert.Equal(["ok"], offered.Connections.Select(c => c.Name));
        Assert.Equal(["ticked"], offered.Problems.Select(p => p.Name));
    }

    /// <summary>A16: the encryption writes only over the very value it read; a password changed meanwhile is left as it is.</summary>
    [Fact]
    public void WritePassword_OverAnExpectedValue_LeavesANewerOneAlone()
    {
        File.WriteAllText(MySqlProfile, """{ "connections": { "a": { "host": "h", "database": "d", "user": "u", "password": "newer" } } }""");
        Assert.Equal(ConnectionsFileEdit.PasswordChanged, ConnectionsFileEdit.WritePassword(MySqlProfile, "a", "dpapi:sealed", expected: "older"));
        Assert.Contains("\"newer\"", File.ReadAllText(MySqlProfile));
        Assert.Null(ConnectionsFileEdit.WritePassword(MySqlProfile, "a", "dpapi:sealed", expected: "newer"));
        Assert.Contains("\"dpapi:sealed\"", File.ReadAllText(MySqlProfile));
    }

    /// <summary>N3: a UTF-16 file is spliced, and written back UTF-16.</summary>
    [Fact]
    public void AUtf16File_IsEdited_AndStaysUtf16()
    {
        File.WriteAllText(MySqlProfile, """{ "connections": { "a": { "host": "h", "database": "d", "user": "u" } } }""", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        Assert.Null(MySqlConfigFile.AddConnection(MySqlProfile, "b", new MySqlConnectionConfig { Host = "h2", Database = "d", User = "u" }));
        byte[] bytes = File.ReadAllBytes(MySqlProfile);
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.Equal(["a", "b"], MySqlConfigFile.Load(MySqlProfile).Connections.Select(c => c.Name));
    }

    /// <summary>N15: an emptied file is filled again, so an add works on it.</summary>
    [Fact]
    public void AWhitespaceFile_IsFilledAgain()
    {
        File.WriteAllText(MySqlProfile, "  \r\n");
        Assert.True(ConnectionsFileEdit.EnsureExists(MySqlProfile, MySqlConfigFile.EmptyText));
        Assert.Null(MySqlConfigFile.AddConnection(MySqlProfile, "a", new MySqlConnectionConfig { Host = "h", Database = "d", User = "u" }));
        Assert.False(ConnectionsFileEdit.EnsureExists(MySqlProfile, MySqlConfigFile.EmptyText));
    }

    /// <summary>N4: another process holding the file for its own edit makes this one wait, then say so; nothing is written.</summary>
    [Fact]
    public void AnEdit_WaitsForAnotherProcesssEdit_ThenSaysSo()
    {
        File.WriteAllText(MySqlProfile, MySqlConfigFile.EmptyText);
        var wait = ConnectionsFileEdit.CrossProcessWait;
        ConnectionsFileEdit.CrossProcessWait = TimeSpan.FromMilliseconds(200);
        try
        {
            using (new FileStream(MySqlProfile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(SqlText.FileBusy(MySqlProfile), MySqlConfigFile.AddConnection(MySqlProfile, "a", new MySqlConnectionConfig { Host = "h", Database = "d", User = "u" }));
            }

            Assert.Null(MySqlConfigFile.AddConnection(MySqlProfile, "a", new MySqlConnectionConfig { Host = "h", Database = "d", User = "u" }));
            Assert.False(File.Exists(MySqlProfile + ".lock"));
        }
        finally
        {
            ConnectionsFileEdit.CrossProcessWait = wait;
            File.Delete(MySqlProfile + ".lock");
        }
    }

    /// <summary>B13 and B14: an add with a sealed password writes both in one go, and the entry text never touches the caller's object.</summary>
    [Fact]
    public void AnAdd_CarriesTheSealedPassword_AndLeavesTheCallersEntryAlone()
    {
        var config = new MySqlConnectionConfig { Host = "h", Database = "d", User = "u", Password = "plain-typed" };
        Assert.Null(MySqlConfigFile.Family.AddConnection(MySqlProfile, "a", config, sealedPassword: "dpapi:sealed"));
        Assert.Equal("plain-typed", config.Password);
        string text = File.ReadAllText(MySqlProfile);
        Assert.Contains("\"dpapi:sealed\"", text);
        Assert.DoesNotContain("plain-typed", text);
    }

    /// <summary>D10: only a value with the shape this layer writes counts as sealed; a typed one that merely starts the prefix is plain.</summary>
    [Fact]
    public void OnlyAValueOfTheSealedShape_CountsAsSealed()
    {
        Assert.False(WindowsCredentials.IsSealed("dpapi:hunter2"));
        Assert.False(WindowsCredentials.IsSealed("keychain:abc"));
        Assert.False(WindowsCredentials.IsSealed("plain"));
        byte[] dpapi = [0x01, 0x00, 0x00, 0x00, 0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11, 0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB, 0x09];
        Assert.True(WindowsCredentials.IsSealed("dpapi:" + Convert.ToBase64String(dpapi)));
        Assert.True(WindowsCredentials.IsSealed("keychain:" + Convert.ToBase64String(new byte[16 + 16 + 32])));
    }

    /// <summary>D10 and N1 on a real store: a typed <c>dpapi:</c> value and a refused entry's plain password are both encrypted in place.</summary>
    [WindowsFact]
    public void EveryPlainPassword_IsEncryptedInPlace_ARefusedEntrysToo()
    {
        File.WriteAllText(MySqlProfile, """{ "connections": { "typed": { "host": "h", "database": "d", "user": "u", "password": "dpapi:hunter2" }, "broken": { "host": "h", "user": "u", "password": "s3cret" } } }""");
        var loaded = MySqlConfigFile.Load(MySqlProfile);
        string text = File.ReadAllText(MySqlProfile);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("s3cret", text);
        Assert.Equal("dpapi:hunter2", MySqlSecrets.Resolve(loaded.Connections[0]).Value);
    }

    /// <summary>K3: a credential with no mark is read by its bytes — UTF-8 when it is valid UTF-8 with no zero byte, else UTF-16.</summary>
    [Fact]
    public void ACredentialWithNoMark_IsReadByItsBytes()
    {
        Assert.Equal("ab", CredentialMark.Decode(Encoding.UTF8.GetBytes("ab")));   // even length, UTF-8 (the parity rule read it as UTF-16)
        Assert.Equal("ab", CredentialMark.Decode(Encoding.Unicode.GetBytes("ab")));
        Assert.Equal("密码", CredentialMark.Decode(Encoding.Unicode.GetBytes("密码")));
        Assert.Equal("pässword", CredentialMark.Decode(Encoding.UTF8.GetBytes("pässword")));
        var mark = CredentialMark.Parse(CredentialMark.Format(wide: true, "db1:5432"));
        Assert.Equal((true, "db1:5432"), (mark.Wide, mark.Host));
        Assert.Equal(default, CredentialMark.Parse("someone else's note"));
        Assert.True(CredentialMark.SameHost(" DB1:5432 ", "db1:5432"));
    }

    /// <summary>DB9, I14, K4 and DB10 on Windows' store: the host is recorded and checked, the user checked, an empty secret refused, the prefix held.</summary>
    [WindowsFact]
    public void AStoredPassword_IsSentOnlyToItsHost_AsItsUser()
    {
        // The app's own per-entry target (NeonSidekick/mysql/<name>): a custom credential, which entries may share, is tied to no host since the
        // review of these fixes (2026-10-24).
        var config = new MySqlConnectionConfig { Host = "db1", Port = 3306, Database = "d", User = "u", PasswordStore = "credman" };
        var named = new MySqlNamedConnection(_target["NeonSidekick/mysql/".Length..], config, MySqlProfile);
        Assert.Equal(_target, config.CredentialTarget(named.Name));
        Assert.True(MySqlSecrets.Save(named, "pw").Saved);
        var read = MySqlSecrets.Resolve(named);
        Assert.Equal(("pw", "db1:3306"), (read.Value, read.Host));

        config.Host = "elsewhere";
        Assert.Equal(SqlText.CredentialHostMismatch(_target, "db1:3306", "elsewhere:3306", "/tools › Databases › MySQL"), MySqlSecrets.Resolve(named).Error);

        config.Host = "db1";
        config.User = "another";
        Assert.StartsWith(ComputerText.CredentialUserMismatch(_target, "u", "another")[..20], MySqlSecrets.Resolve(named).Error);

        Assert.Equal(SqlText.EmptyCredential(_target), WindowsCredentials.WriteGeneric(_target, "u", "").Error);

        File.WriteAllText(MySqlProfile, """{ "connections": { "elsewhere": { "host": "h", "database": "d", "user": "u", "passwordStore": "credman", "credential": "Team/shared" }, "file": { "host": "h", "database": "d", "user": "u", "credential": "Team/unused" } } }""");
        var loaded = MySqlConfigFile.Load(MySqlProfile);
        Assert.Equal(["file"], loaded.Connections.Select(c => c.Name));   // a credential is read only under credman
        Assert.Equal(ComputerText.CredentialOutsidePrefix(MySqlConnectionConfig.CredentialPrefix), Assert.Single(loaded.Problems).Reason);
    }

    /// <summary>N12: a credential an entry of either file still reads is in use, so a rename does not delete it.</summary>
    [Fact]
    public void ACredentialAnotherFileStillReads_IsInUse()
    {
        File.WriteAllText(MySqlHome, """{ "connections": { "prod": { "host": "h", "database": "d", "user": "u", "passwordStore": "credman" } } }""");
        File.WriteAllText(MySqlProfile, MySqlConfigFile.EmptyText);
        Assert.True(SettingsMenu.CredentialInUse(MySqlConfigFile.Family, "NeonSidekick/mysql/prod", MySqlProfile, MySqlHome));
        Assert.False(SettingsMenu.CredentialInUse(MySqlConfigFile.Family, "NeonSidekick/mysql/gone", MySqlProfile, MySqlHome));
    }

    /// <summary>N7: a choice page opens on the stored word whatever its case.</summary>
    [Fact]
    public void AChoicePage_OpensOnTheStoredWord_WhateverItsCase()
    {
        Assert.Equal(3, SettingsMenu.WordIndex(MySqlConnectionConfig.SslModeWords, " Verify-Full "));
        Assert.Equal(0, SettingsMenu.WordIndex(MySqlConnectionConfig.SslModeWords, null));
    }

    /// <summary>N16: the hint's command quotes a target or user with a space; a plain one stands bare.</summary>
    [WindowsFact]
    public void TheCredentialHint_QuotesWhatNeedsIt()
    {
        Assert.Equal("cmdkey /generic:NeonSidekick/sql/aw /user:reader /pass", SqlText.CredentialCommand("NeonSidekick/sql/aw", "reader"));
        Assert.Equal("cmdkey /generic:\"NeonSidekick/sql/my db\" /user:\"Jane Doe\" /pass", SqlText.CredentialCommand("NeonSidekick/sql/my db", "Jane Doe"));
    }

    /// <summary>K1: a refused password is held — the next call sends nothing until the password changes — and an unproven one goes one at a time.</summary>
    [Fact]
    public async Task ARefusedPassword_IsHeld_UntilItChanges()
    {
        int opens = 0;
        Task Refused(CancellationToken _)
        {
            opens++;
            throw new InvalidOperationException("access denied");
        }

        var wrong = DatabaseSignIn.Call("mysql", "db1:3306", "u", "old", ex => ex is InvalidOperationException);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSignIn.OpenAsync(wrong, Refused, CancellationToken.None));
        Assert.True(await DatabaseSignIn.OpenAsync(wrong, Refused, CancellationToken.None));   // held: nothing sent
        Assert.Equal(1, opens);

        var changed = DatabaseSignIn.Call("mysql", "db1:3306", "u", "new", ex => ex is InvalidOperationException);
        Assert.False(await DatabaseSignIn.OpenAsync(changed, _ => { opens++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(2, opens);

        // A Windows sign-in sends no password and is never held.
        Assert.Null(DatabaseSignIn.Call("sql", "host", null, "", _ => true).HoldKey);
    }

    /// <summary>I12 and D8: a <c>~</c> path is the user's home; a path no file can have refuses the entry, and its full path never throws.</summary>
    [Fact]
    public void SqlitePaths_ExpandTheHome_AndRefuseAnImpossiblePath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(Path.Join(home, "data/app.db")), SqliteNamedDatabase.Expand("~/data/app.db", "C:/elsewhere"));
        var bad = new SqliteDatabaseConfig { Path = "a\0b.db" };
        Assert.Equal(SqliteText.BadPath("a\0b.db"), bad.Problem);
        Assert.Equal("a\0b.db", new SqliteNamedDatabase("x", bad, Path.Combine(_home, "sqlite.json")).FullPath);
    }

    /// <summary>H9 for SQLite: the guard watches the home's database a profile entry shadows, and a refused entry's.</summary>
    [Fact]
    public void TheSqliteGuard_WatchesShadowedAndRefusedEntries()
    {
        File.WriteAllText(SqliteConfigFile.ProfilePath(_profile), """{ "databases": { "shop": { "path": "C:/data/profile-shop.db" }, "typo": { "path": "C:/data/typo.db", "pth": 1 } } }""");
        File.WriteAllText(SqliteConfigFile.GlobalPath(_home), """{ "databases": { "shop": { "path": "C:/data/home-shop.db" } } }""");
        var files = SqliteConfigFile.EveryFile(_profile, _home);
        Assert.Contains(files, f => f.EndsWith("profile-shop.db", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("home-shop.db", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("typo.db", StringComparison.Ordinal));
        Assert.Equal(["shop"], SqliteConfigFile.LoadCatalog(_profile, _home).Databases.Select(d => d.Name));
    }
}
