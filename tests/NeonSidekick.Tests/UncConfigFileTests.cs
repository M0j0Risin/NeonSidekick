using NeonSidekick.Sql;
using NeonSidekick.Unc;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>unc.json</c> (2026-09-30): the shape, the problems it reports, the profile over the home, the paths a share may name, the
/// catalog's lookups by name and by full path, the runas passwords kept as <c>sql.json</c>'s are, and the wizard's append.
/// </summary>
public sealed class UncConfigFileTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _profile;
    private readonly string _target = "NeonSidekick.Tests/unc/" + Guid.NewGuid().ToString("N");

    public UncConfigFileTests()
    {
        _profile = Path.Combine(_home, "profiles", "default");
        Directory.CreateDirectory(_profile);
    }

    public void Dispose()
    {
        WindowsCredentials.DeleteGeneric(_target);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private string ProfilePath => UncConfigFile.ProfilePath(_profile);

    private void Profile(string json) => File.WriteAllText(ProfilePath, json);

    private static UncNamedShare Named(string name, string path, string? access = null) => new(name, new UncShareConfig { Path = path, Access = access }, "test");

    [WindowsFact]
    public void TheEmptyShapesExamples_AreEachAUsableShare_OnceUncommented()
    {
        Assert.True(UncConfigFile.EnsureExists(ProfilePath));
        Assert.Empty(UncConfigFile.Load(ProfilePath).Shares);
        var example = UncConfigFile.EmptyText.Split('\n')
            .Where(l => l.StartsWith("  // ", StringComparison.Ordinal))
            .Select(l => l["  // ".Length..])
            .Where(l => l.EndsWith('{') || l.EndsWith(',') || l.EndsWith('}') || l.EndsWith('"'))
            .Where(l => l.StartsWith('"') || l.StartsWith("  ", StringComparison.Ordinal) || l.StartsWith('}'));
        Profile("{ \"shares\": {\n" + string.Join("\n", example) + "\n} }");

        var loaded = UncConfigFile.Load(ProfilePath);

        Assert.Empty(loaded.Problems);
        Assert.Equal(["eng", "finance", "data"], loaded.Shares.Select(s => s.Name));
        Assert.Equal(@"\\fs01\eng", loaded.Shares[0].Config.Root);
        Assert.True(loaded.Shares[0].Config.IsUnc);
        Assert.False(loaded.Shares[0].Config.IsReadWrite);
        Assert.True(loaded.Shares[1].Config.IsRunAs);
        Assert.True(loaded.Shares[1].Config.InCredentialManager);
        Assert.Equal(@"CORP\svc_reader", loaded.Shares[1].Config.User);
        Assert.Equal(@"D:\Data", loaded.Shares[2].Config.Root);
        Assert.True(loaded.Shares[2].Config.IsReadWrite);
        Assert.Contains("\"shares\": {}", UncConfigFile.EmptyText);
    }

    [WindowsFact]
    public void Shares_LoadInFileOrder_AndABadEntryIsAProblem_NotAThrow()
    {
        Profile("""
            {
              "shares": {
                "eng": { "path": "\\\\fs01\\eng\\specs\\", "description": "specs" },
                "nopath": { "description": "x" },
                "server": { "path": "\\\\fs01" },
                "device": { "path": "\\\\?\\C:\\data" },
                "relative": { "path": "data\\reports" },
                "rooted": { "path": "\\data" },
                "driverel": { "path": "C:data" },
                "drive": { "path": "D:\\" },
                "stream": { "path": "D:\\data:secret" },
                "auth": { "path": "D:\\data", "auth": "kerberos" },
                "access": { "path": "D:\\data", "access": "write" },
                "store": { "path": "D:\\data", "auth": "runas", "user": "CORP\\me", "passwordStore": "vault" },
                "nouser": { "path": "D:\\data", "auth": "runas" },
                "nodomain": { "path": "D:\\data", "auth": "runas", "user": "me" },
                "credwin": { "path": "D:\\data", "passwordStore": "credman" },
                "empty": null,
              }
            }
            """);

        var loaded = UncConfigFile.Load(ProfilePath);

        var eng = Assert.Single(loaded.Shares);
        Assert.Equal(@"\\fs01\eng\specs", eng.Config.Root);   // the trailing separator trimmed
        Assert.Equal(
            [
                UncText.NoPath, UncText.NoShareName(@"\\fs01"), UncText.DevicePath(@"\\?\C:\data"), UncText.NotAbsolute(@"data\reports"),
                UncText.NotAbsolute(@"\data"), UncText.NotAbsolute("C:data"), UncText.WholeDrive("D:"), UncText.BadPath(@"D:\data:secret"),
                UncText.BadAuth("kerberos"), UncText.BadAccess("write"), SqlText.BadPasswordStore("vault"), UncText.RunAsNoUser,
                SqlText.RunAsNeedsDomain("me"), UncText.CredmanWithWindows, UncText.NoPath,
            ],
            loaded.Problems.Select(p => p.Reason));
        Assert.Equal(SqlText.ConnectionSource(ProfilePath, "nopath"), loaded.Problems[0].Source);
    }

    [Fact]
    public void ACorruptFile_IsOneProblem_ThatNamesTheBackslashRule()
    {
        Profile("""{ "shares": { "eng": { "path": "\\fs01\eng" } } }""");   // single backslashes: \e is no JSON escape

        var loaded = UncConfigFile.Load(ProfilePath);

        Assert.Empty(loaded.Shares);
        var problem = Assert.Single(loaded.Problems);
        Assert.Equal(ProfilePath, problem.Source);
        Assert.EndsWith("; a backslash in JSON is written \\\\ (or use /)", problem.Reason);
        Assert.Same(UncCatalog.Empty, UncConfigFile.Load(Path.Combine(_home, "missing.json")));
    }

    [WindowsFact]
    public void TheProfilesFile_WinsByName_OverTheHomes_AndOfferedNarrowsIt()
    {
        Profile("""{ "shares": { "eng": { "path": "//profile/eng" } } }""");
        File.WriteAllText(UncConfigFile.GlobalPath(_home), """{ "shares": { "ENG": { "path": "//home/eng" }, "data": { "path": "D:/Data" } } }""");

        var catalog = UncConfigFile.LoadCatalog(_profile, _home);

        Assert.Equal(["eng", "data"], catalog.Shares.Select(s => s.Name));
        Assert.Equal(@"\\profile\eng", catalog.Find("Eng", null)!.Config.Root);
        Assert.Equal("data", catalog.Find(null, "DATA")!.Name);
        Assert.Equal("eng", catalog.Find(null, "gone")!.Name);   // the first, when the default names none
        Assert.Null(catalog.Find("gone", null));
        var offered = catalog.Offered(["data"]);
        Assert.Equal(["data"], offered.Shares.Select(s => s.Name));
        Assert.Equal(1, offered.Hidden);
        Assert.Empty(catalog.Offered(null).Shares);   // null offers none (2026-10-01)
    }

    [WindowsFact]
    public void Locate_IsTheLongestRootAPathLiesUnder_BySpelling()
    {
        var catalog = new UncCatalog([Named("eng", @"\\fs01\eng"), Named("specs", @"\\fs01\eng\specs"), Named("data", @"D:\Data")], []);

        Assert.Equal("eng", catalog.Locate(@"\\fs01\eng\drawings\a.dwg")!.Name);
        Assert.Equal("specs", catalog.Locate(@"\\FS01\ENG\Specs\b.md")!.Name);   // the longer root wins, any case
        Assert.Equal("specs", catalog.Locate("//fs01/eng/specs")!.Name);         // / reads as \
        Assert.Equal("eng", catalog.Locate(@"\\fs01\eng")!.Name);
        Assert.Equal("data", catalog.Locate(@"d:\data\x\..\y.csv")!.Name);
        Assert.Null(catalog.Locate(@"\\fs01\eng2\a.txt"));                        // a prefix by letters alone is not under it
        Assert.Equal("eng", catalog.Locate(@"\\fs01\eng\..\fin\a.txt")!.Name);    // .. never climbs past a share's root: \\fs01\eng\fin\a.txt
        Assert.Equal("eng", catalog.Locate(@"\\fs01\eng\specs\..\..\..\x")!.Name); // out of specs, never out of eng
        Assert.Null(catalog.Locate(@"specs\b.md"));                               // not a full path
        Assert.Null(catalog.Locate(""));
        Assert.True(UncCatalog.IsAbsolute(@"\\s\x"));
        Assert.True(UncCatalog.IsAbsolute(@"C:\x"));
        Assert.False(UncCatalog.IsAbsolute(@"C:x"));
        Assert.False(UncCatalog.IsAbsolute(@"\x"));
    }

    [WindowsFact]
    public void TheWarnings_NameARunAsOnALocalFolder_AndNothingForAUncPath()
    {
        Assert.Null(new UncShareConfig { Path = @"\\fs01\eng", Auth = "runas", User = @"CORP\me" }.Warning);
        Assert.Equal(UncText.RunAsLocalWarning, new UncShareConfig { Path = _home, Auth = "runas", User = @"CORP\me" }.Warning);
        Assert.Null(new UncShareConfig { Path = _home }.Warning);
        Assert.Null(new UncShareConfig { Path = "" }.Warning);   // a problem, not a warning
        Assert.Equal("runas only changes who the network sees; a local folder is read as you", UncText.RunAsLocalWarning);
        Assert.Equal(@"Z: is a mapped network drive; give its \\server\share path instead — a mapping belongs to a sign-in, and a runas token or an elevated app may not see it", UncText.MappedDriveWarning("Z:"));
    }

    [WindowsFact]
    public void AddShare_AndTheRunAsPasswordStores()
    {
        var config = new UncShareConfig { Path = "//fs02/finance", Auth = "runas", User = @"CORP\svc", Password = "never-written", Access = "readwrite", Description = "money" };
        Assert.Null(UncConfigFile.AddShare(ProfilePath, "finance", config));
        string text = File.ReadAllText(ProfilePath);
        Assert.DoesNotContain("never-written", text);
        Assert.Contains("\"shares\": {", text);
        Assert.Equal("never-written", config.Password);   // the caller's object as it was
        Assert.Equal(SqlText.ConnectionAlreadyInFile("FINANCE", "share"), UncConfigFile.AddShare(ProfilePath, "FINANCE", config));
        Assert.Equal("a share named 'FINANCE' is already in the file", SqlText.ConnectionAlreadyInFile("FINANCE", "share"));

        var saved = UncConfigFile.Load(ProfilePath).Shares[0];
        Assert.Equal(@"\\fs02\finance", saved.Config.Root);
        Assert.True(saved.Config.IsReadWrite);
        Assert.Equal(UncText.NoPassword("finance"), UncSecrets.Resolve(saved).Error);
        Assert.Equal((true, SqlText.PasswordSavedToFile("finance", ProfilePath)), UncSecrets.Save(saved, "typed"));
        Assert.Contains(WindowsCredentials.ProtectedPrefix, File.ReadAllText(ProfilePath));
        Assert.Equal("typed", UncSecrets.Resolve(UncConfigFile.Load(ProfilePath).Shares[0]).Value);

        var credman = new UncNamedShare("fin", new UncShareConfig { Path = @"\\s\x", Auth = "runas", User = @"CORP\ro", PasswordStore = "credman", Credential = _target }, "test");
        Assert.Equal(UncText.NoCredential(_target), UncSecrets.Resolve(credman).Error);
        Assert.Equal((true, SqlText.PasswordSavedToCredman("fin", _target)), UncSecrets.Save(credman, "kept"));
        Assert.Equal("kept", UncSecrets.Resolve(credman).Value);
        Assert.Equal("NeonSidekick/unc/prod", new UncShareConfig().CredentialTarget("prod"));
        Assert.Equal("", UncSecrets.Resolve(Named("eng", @"\\s\x")).Value);   // windows needs none
        Assert.Equal(SqlText.ConnectionNotInFile("gone", "share"), UncConfigFile.WritePassword(ProfilePath, "gone", "x"));
    }

    [WindowsFact]
    public void APlainRunAsPassword_IsEncryptedInPlace_AtTheFirstRead_AndEncryptAllReadsEveryProfile()
    {
        Profile("""
            {
              // a comment that stays
              "shares": { "fin": { "path": "//s/fin", "auth": "runas", "user": "CORP\\me", "password": "plain" },
                          "eng": { "path": "//s/eng", "password": "ignored" } }
            }
            """);
        File.WriteAllText(UncConfigFile.GlobalPath(_home), """{ "shares": {} }""");

        var read = UncConfigFile.EncryptAll(_home);

        Assert.Equal([UncConfigFile.GlobalPath(_home), ProfilePath], read);
        string text = File.ReadAllText(ProfilePath);
        Assert.Contains("// a comment that stays", text);
        Assert.DoesNotContain("\"plain\"", text);
        Assert.Contains("\"ignored\"", text);   // a windows share's stray password is not its to keep
        Assert.Equal("plain", UncSecrets.Resolve(UncConfigFile.Load(ProfilePath).Shares[0]).Value);
    }

    [Fact]
    public void ConnectionsFileEdit_UnderAnotherSection_RefusesItsShape_InItsOwnWords()
    {
        Profile("""{ "shares": [] }""");
        Assert.Equal(SqlText.SectionNotAnObject("shares"), UncConfigFile.AddShare(ProfilePath, "x", new UncShareConfig { Path = "//s/x" }));
        Assert.Equal("\"shares\" in the file is not an object", SqlText.SectionNotAnObject("shares"));

        Profile("""{ "other": 1 }""");
        Assert.Null(UncConfigFile.AddShare(ProfilePath, "x", new UncShareConfig { Path = "//s/x" }));
        Assert.Equal("x", Assert.Single(UncConfigFile.Load(ProfilePath).Shares).Name);
    }
}
