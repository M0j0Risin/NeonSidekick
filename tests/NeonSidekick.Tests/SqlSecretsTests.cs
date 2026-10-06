using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// The SQL passwords (later on 2026-09-23): DPAPI in <c>sql.json</c>, Windows Credential Manager outside it, the
/// <c>runas</c> account's netonly logon, and <see cref="SqlSecrets"/> choosing between them per connection. Every
/// Credential Manager entry a test writes is under a fresh <c>NeonSidekick.Tests/…</c> target and removed after.
/// </summary>
public sealed class SqlSecretsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _target = "NeonSidekick.Tests/" + Guid.NewGuid().ToString("N");

    public SqlSecretsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        WindowsCredentials.DeleteGeneric(_target);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static SqlNamedConnection Named(SqlConnectionConfig config, string source = "test") => new("prod", config, source);

    [WindowsFact]
    public void Dpapi_RoundTrips_AndRefusesWhatItDidNotWrite()
    {
        var encrypted = WindowsCredentials.Protect("pä$$ wörd 🔑");
        Assert.Null(encrypted.Error);
        Assert.StartsWith(WindowsCredentials.ProtectedPrefix, encrypted.Value);
        Assert.DoesNotContain("wörd", encrypted.Value);
        Assert.Equal("pä$$ wörd 🔑", WindowsCredentials.Unprotect(encrypted.Value!).Value);
        Assert.NotEqual(encrypted.Value, WindowsCredentials.Protect("pä$$ wörd 🔑").Value);   // salted: the same secret twice is two values

        Assert.Equal(SqlText.NotProtected, WindowsCredentials.Unprotect("hunter2").Error);
        Assert.Equal(SqlText.NotProtected, WindowsCredentials.Unprotect("dpapi:not base64!").Error);
        string? garbled = WindowsCredentials.Unprotect("dpapi:" + Convert.ToBase64String(new byte[64])).Error;
        Assert.StartsWith("the password cannot be decrypted — it was saved by another Windows user or on another machine (", garbled);
    }

    [Fact]
    public void CredentialManager_Writes_Reads_AndDeletes_AGenericEntry()
    {
        Assert.True(WindowsCredentials.ReadGeneric(_target).NotFound);
        Assert.Equal(SqlText.NoCredential(_target), WindowsCredentials.ReadGeneric(_target).Error);
        Assert.Null(WindowsCredentials.WriteGeneric(_target, @"CONTOSO\svc-test", "s3cret ✓").Error);
        Assert.Equal("s3cret ✓", WindowsCredentials.ReadGeneric(_target).Value);
        Assert.Null(WindowsCredentials.WriteGeneric(_target, @"CONTOSO\svc-test", "second").Error);   // a write replaces
        Assert.Equal("second", WindowsCredentials.ReadGeneric(_target).Value);
        Assert.True(WindowsCredentials.DeleteGeneric(_target));
        Assert.True(WindowsCredentials.ReadGeneric(_target).NotFound);
        Assert.True(WindowsCredentials.DeleteGeneric(_target));   // gone already: still removed
    }

    [WindowsFact]
    public void AnAccount_SplitsAsLogonUserTakesIt_AndANetOnlyLogon_KeepsTheLocalIdentity()
    {
        Assert.Equal(("CONTOSO", "svc-reader"), WindowsCredentials.SplitAccount(@" CONTOSO\svc-reader ")!.Value);
        Assert.Equal(((string?)null, "svc-reader@contoso.com"), WindowsCredentials.SplitAccount("svc-reader@contoso.com")!.Value);
        Assert.Null(WindowsCredentials.SplitAccount("svc-reader"));
        Assert.Null(WindowsCredentials.SplitAccount(@"CONTOSO\"));
        Assert.Null(WindowsCredentials.SplitAccount(@"A\B\C"));
        Assert.Null(WindowsCredentials.SplitAccount(null));

        Assert.Null(WindowsCredentials.LogonNetOnly("nobody", "x", out string? refused));
        Assert.Equal(SqlText.RunAsNeedsDomain("nobody"), refused);
        using var token = WindowsCredentials.LogonNetOnly(@"NEONSIDEKICK-TEST\nobody", "x", out string? error);
        Assert.Null(error);
        Assert.NotNull(token);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(CurrentName(), System.Security.Principal.WindowsIdentity.RunImpersonated(token!, CurrentName));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string CurrentName() => System.Security.Principal.WindowsIdentity.GetCurrent().Name;

    [Fact]
    public void Resolve_ReadsTheConnectionsStore_AndNamesTheFixWhenItIsEmpty()
    {
        Assert.Equal("", SqlSecrets.Resolve(Named(new SqlConnectionConfig { Server = "x", Auth = "windows" })).Value);
        Assert.Equal("plain", SqlSecrets.Resolve(Named(new SqlConnectionConfig { Server = "x", User = "u", Password = "plain" })).Value);   // not rewritten yet: still works
        Assert.Equal("kept", SqlSecrets.Resolve(Named(new SqlConnectionConfig { Server = "x", User = "u", Password = WindowsCredentials.Protect("kept").Value })).Value);
        Assert.Equal(SqlText.NoPassword("prod"), SqlSecrets.Resolve(Named(new SqlConnectionConfig { Server = "x", User = "u" })).Error);

        var credman = new SqlConnectionConfig { Server = "x", Auth = "runas", User = @"CONTOSO\svc-test", PasswordStore = "credman", Credential = _target, Password = "ignored" };
        Assert.Equal(SqlText.NoCredential(_target), SqlSecrets.Resolve(Named(credman)).Error);
        WindowsCredentials.WriteGeneric(_target, @"CONTOSO\svc-test", "from credman");
        Assert.Equal("from credman", SqlSecrets.Resolve(Named(credman)).Value);
        Assert.Equal("NeonSidekick/sql/prod", new SqlConnectionConfig().CredentialTarget("prod"));
    }

    [WindowsFact]
    public void Save_WritesToTheConnectionsStore()
    {
        var credman = Named(new SqlConnectionConfig { Server = "x", Auth = "runas", User = @"CONTOSO\svc-test", PasswordStore = "credman", Credential = _target });
        Assert.Equal((true, SqlText.PasswordSavedToCredman("prod", _target)), SqlSecrets.Save(credman, "typed"));
        Assert.Equal("typed", WindowsCredentials.ReadGeneric(_target).Value);

        string path = Path.Combine(_dir, SqlConfigFile.FileName);
        File.WriteAllText(path, """{ "connections": { "prod": { "server": "x", "user": "u" } } }""");
        Assert.Equal((true, SqlText.PasswordSavedToFile("prod", path)), SqlSecrets.Save(Named(new SqlConnectionConfig { Server = "x", User = "u" }, path), "typed"));
        var loaded = SqlConfigFile.Load(path);
        Assert.StartsWith(WindowsCredentials.ProtectedPrefix, loaded.Connections[0].Config.Password);
        Assert.Equal("typed", SqlSecrets.Resolve(loaded.Connections[0]).Value);

        var (saved, notice) = SqlSecrets.Save(Named(new SqlConnectionConfig { Server = "x", User = "u" }, path) with { Name = "gone" }, "typed");
        Assert.False(saved);
        Assert.Equal(SqlText.PasswordSaveFailed("gone", SqlText.ConnectionNotInFile("gone")), notice);
    }

    /// <summary>
    /// The macOS twin of <see cref="Save_WritesToTheConnectionsStore"/> (2026-10-06, the macOS build): a <c>credman</c> password is a
    /// Keychain generic password and a file's is a <c>keychain:</c> value, each read back by <see cref="SqlSecrets.Resolve"/>.
    /// </summary>
    [UnixFact]
    public void Save_WritesToTheConnectionsStore_TheKeychainOnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var credman = Named(new SqlConnectionConfig { Server = "x", User = "u", PasswordStore = "credman", Credential = _target });
        Assert.Equal((true, SqlText.PasswordSavedToCredman("prod", _target)), SqlSecrets.Save(credman, "typed"));
        Assert.Equal("typed", WindowsCredentials.ReadGeneric(_target).Value);
        Assert.Equal("typed", SqlSecrets.Resolve(credman).Value);

        string path = Path.Combine(_dir, SqlConfigFile.FileName);
        File.WriteAllText(path, """{ "connections": { "prod": { "server": "x", "user": "u" } } }""");
        Assert.Equal((true, SqlText.PasswordSavedToFile("prod", path)), SqlSecrets.Save(Named(new SqlConnectionConfig { Server = "x", User = "u" }, path), "typed"));
        var loaded = SqlConfigFile.Load(path);
        Assert.StartsWith(WindowsCredentials.KeychainPrefix, loaded.Connections[0].Config.Password);
        Assert.Equal("typed", SqlSecrets.Resolve(loaded.Connections[0]).Value);
    }

    [UnixFact]
    public void OnAMac_TheTestsKey_IsTheirOwn_NeverTheAppsKeychainItem()
    {
        // Later on 2026-10-06: the suite made the app's real key (NeonSidekick / master-key) in the login Keychain.
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal("NeonSidekick", MacKeychain.Service);
        Assert.Equal(ModuleInit.TestKeychainService, MacKeychain.KeyService);
        Assert.NotEqual(MacKeychain.Service, MacKeychain.KeyService);
    }
}
