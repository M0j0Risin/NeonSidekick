using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// The tokens in <c>mcp.json</c> (2026-11-19, feature idea #29): encrypted in place by the load, every other byte kept; revealed only at
/// connect; <see cref="ConnectionsFileEdit.WriteValue"/>'s ordinal key path, its repeat guard and its newer-value check.
/// </summary>
public class McpSecretsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public McpSecretsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            /* best effort */
        }
    }

    private string Write(string text, string name = "mcp.json")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private const string Plain = """
        {
          // keep this comment
          "mcpServers": {
            "github": {
              "command": "docker",
              "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_plain123", "PATH": "/usr/bin", "API_KEY": "${KEY}" },   // a note
            },
            "gateway": { "url": "http://localhost:8811/mcp", "headers": { "Authorization": "Bearer tok456", "Accept": "text/plain" } },
          }
        }
        """;

    [WindowsFact]
    public void APlainToken_IsEncryptedInPlace_TheRestOfTheFileUntouched_DpapiOnWindows() => EncryptsInPlace(WindowsCredentials.ProtectedPrefix);

    [MacFact]
    public void APlainToken_IsEncryptedInPlace_TheRestOfTheFileUntouched_KeychainOnAMac() => EncryptsInPlace(WindowsCredentials.KeychainPrefix);

    private void EncryptsInPlace(string prefix)
    {
        string path = Write(Plain);
        var lines = new List<string>();
        Action<DiagnosticEvent> capture = e => { lock (lines) { lines.Add(e.Message); } };
        DiagnosticLog.Emitted += capture;
        McpConfigLoad load;
        try
        {
            load = McpConfigFile.Load(path);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        string after = File.ReadAllText(path);
        Assert.DoesNotContain("ghp_plain123", after);
        Assert.DoesNotContain("tok456", after);
        Assert.Contains("// keep this comment", after);
        Assert.Contains("},   // a note", after);
        Assert.Contains("\"PATH\": \"/usr/bin\"", after);
        Assert.Contains("\"API_KEY\": \"${KEY}\"", after);   // a reference is left alone
        Assert.Contains("\"Accept\": \"text/plain\"", after);
        // Every other byte kept: the file with its two values put back is the file it was.
        var github = load.Servers.Single(s => s.Key == "github").Value;
        var gateway = load.Servers.Single(s => s.Key == "gateway").Value;
        Assert.StartsWith(prefix, github.Env!["GITHUB_PERSONAL_ACCESS_TOKEN"]);
        Assert.StartsWith(prefix, gateway.Headers!["Authorization"]);
        Assert.Equal(Plain, after.Replace(github.Env["GITHUB_PERSONAL_ACCESS_TOKEN"], "ghp_plain123").Replace(gateway.Headers["Authorization"], "Bearer tok456"));
        lock (lines)
        {
            Assert.DoesNotContain(lines, l => l.Contains("ghp_plain123", StringComparison.Ordinal) || l.Contains("tok456", StringComparison.Ordinal));
            Assert.Contains(McpText.EncryptedLogLine(path, "github", "GITHUB_PERSONAL_ACCESS_TOKEN"), lines);
        }

        // Revealed for the transport alone; the stored config keeps the sealed value.
        var (open, error) = McpSecrets.Reveal(github);
        Assert.Null(error);
        Assert.Equal("ghp_plain123", open!.Env!["GITHUB_PERSONAL_ACCESS_TOKEN"]);
        Assert.Equal("/usr/bin", open.Env["PATH"]);
        Assert.StartsWith(prefix, github.Env["GITHUB_PERSONAL_ACCESS_TOKEN"]);

        // A second load writes nothing.
        var stamp = File.GetLastWriteTimeUtc(path);
        McpConfigFile.Load(path);
        Assert.Equal(after, File.ReadAllText(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void TheSecretNames_AreTheHeaderAndEnvWords_AnywhereInTheName()
    {
        Assert.True(McpSecrets.IsSecretName("Authorization", header: true));
        Assert.True(McpSecrets.IsSecretName("Proxy-Authorization", header: true));
        Assert.True(McpSecrets.IsSecretName("X-API-Key", header: true));
        Assert.True(McpSecrets.IsSecretName("Cookie", header: true));
        Assert.False(McpSecrets.IsSecretName("Accept", header: true));
        Assert.True(McpSecrets.IsSecretName("GITHUB_PERSONAL_ACCESS_TOKEN", header: false));
        Assert.True(McpSecrets.IsSecretName("db_connection", header: false));
        Assert.True(McpSecrets.IsSecretName("OAUTH_CLIENT", header: false));
        Assert.False(McpSecrets.IsSecretName("PATH", header: false));
        Assert.False(McpSecrets.NeedsSealing(""));
        Assert.False(McpSecrets.NeedsSealing("${GITHUB_TOKEN}"));
        Assert.True(McpSecrets.NeedsSealing("dpapi:not-a-blob"));   // the prefix without the shape is plain text
    }

    [WindowsFact]
    public void ARepeatedKey_LeavesThatServerPlain_WithAWarn_AndAServerWithAProblemIsEncryptedToo()
    {
        string path = Write("""
            { "mcpServers": {
                "twice": { "command": "x", "env": { "TOKEN": "first", "TOKEN": "second" } },
                "typo": { "command": "x", "ask": "sometimes", "env": { "TOKEN": "typo-secret" } }
            } }
            """);
        var warns = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Level == DiagnosticLevel.Warning) { lock (warns) { warns.Add(e.Message); } } };
        DiagnosticLog.Emitted += capture;
        McpConfigLoad load;
        try
        {
            load = McpConfigFile.Load(path);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        string after = File.ReadAllText(path);
        Assert.Contains("\"first\"", after);
        Assert.Contains("\"second\"", after);
        Assert.DoesNotContain("typo-secret", after);   // skipped over a typo, its token still encrypted
        Assert.Contains(load.Problems, p => p.Reason == McpText.BadAsk);
        lock (warns)
        {
            Assert.Contains(McpText.RepeatedKeyLogLine(path, "twice"), warns);
        }
    }

    [WindowsFact]
    public void AReadOnlyFile_LeavesTheValuesPlain_WithAWarn_AndLoadStillReturnsTheServers()
    {
        string path = Write("""{ "mcpServers": { "gh": { "command": "x", "env": { "GITHUB_TOKEN": "plain-ro" } } } }""");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var load = McpConfigFile.Load(path);

        Assert.Equal("plain-ro", Assert.Single(load.Servers).Value.Env!["GITHUB_TOKEN"]);
        Assert.Contains("plain-ro", File.ReadAllText(path));
    }

    [Fact]
    public void AValueThatCantBeRevealed_IsTheFailedRowsText()
    {
        var config = new McpServerConfig { Command = "x", Env = new() { ["TOKEN"] = OperatingSystem.IsMacOS() ? "dpapi:" + Convert.ToBase64String(new byte[64]) : "keychain:" + Convert.ToBase64String(new byte[64]) } };
        var (open, error) = McpSecrets.Reveal(config);

        Assert.Null(open);
        Assert.StartsWith("TOKEN in mcp.json: ", error);
        Assert.EndsWith("; type it again in plain text", error);
    }

    [WindowsFact]
    public void EncryptAll_CoversTheHomeAndEveryProfile()
    {
        string home = Path.Combine(_dir, "home");
        Profiles.Create(home, "work", new AppSettingsData());
        string global = McpConfigFile.GlobalPath(home);
        string work = McpConfigFile.ProfilePath(Profiles.Directory(home, "work"));
        Directory.CreateDirectory(Path.GetDirectoryName(global)!);
        Directory.CreateDirectory(Path.GetDirectoryName(work)!);
        File.WriteAllText(global, """{ "mcpServers": { "a": { "command": "x", "env": { "A_TOKEN": "home-secret" } } } }""");
        File.WriteAllText(work, """{ "mcpServers": { "b": { "url": "http://h/mcp", "headers": { "Authorization": "work-secret" } } } }""");

        var read = McpConfigFile.EncryptAll(home);

        Assert.Equal(2, read.Count);
        Assert.DoesNotContain("home-secret", File.ReadAllText(global));
        Assert.DoesNotContain("work-secret", File.ReadAllText(work));
    }

    [WindowsFact]
    public void TwoEncryptedCopiesOfOneToken_AreTheSameServer()
    {
        var a = new McpServerConfig { Command = "x", Env = new() { ["TOKEN"] = WindowsCredentials.Protect("same").Value! } };
        var b = new McpServerConfig { Command = "x", Env = new() { ["TOKEN"] = WindowsCredentials.Protect("same").Value! } };
        var c = new McpServerConfig { Command = "x", Env = new() { ["TOKEN"] = WindowsCredentials.Protect("other").Value! } };

        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());   // DPAPI is randomized
        Assert.True(McpSecrets.SameServer(a, b));
        Assert.False(McpSecrets.SameServer(a, c));
    }

    // ── WriteValue ─────────────────────────────────────────────────────────

    [Fact]
    public void WriteValue_ReplacesAtDepthFour_Ordinally_AndKeepsEveryOtherByte()
    {
        string text = """
            { "mcpServers": { "gh": { "env": { "GITHUB_TOKEN": "upper", "github_token": "lower" } }, " gh": { "env": { "GITHUB_TOKEN": "spaced" } } } }
            """;
        string path = Write(text);

        Assert.Null(ConnectionsFileEdit.WriteValue(path, ["mcpServers", "gh", "env", "github_token"], "sealed", "lower"));
        Assert.Null(ConnectionsFileEdit.WriteValue(path, ["mcpServers", " gh", "env", "GITHUB_TOKEN"], "sealed2", "spaced"));

        Assert.Equal(text.Replace("\"lower\"", "\"sealed\"").Replace("\"spaced\"", "\"sealed2\""), File.ReadAllText(path));
        Assert.Equal(ConnectionsFileEdit.ValueNotInFile, ConnectionsFileEdit.WriteValue(path, ["mcpServers", "GH", "env", "GITHUB_TOKEN"], "x", "upper"));
    }

    [Fact]
    public void WriteValue_RefusesARepeatedKeyAlongThePath_AndLeavesANewerValueAlone()
    {
        string path = Write("""{ "mcpServers": { "gh": { "env": { "T": "a" } }, "gh": { "env": { "T": "b" } } } }""");
        Assert.Equal(ConnectionsFileEdit.KeyRepeated, ConnectionsFileEdit.WriteValue(path, ["mcpServers", "gh", "env", "T"], "x", "b"));

        path = Write("""{ "mcpServers": { "gh": { "env": { "T": "newer" } } } }""", "two.json");
        Assert.Equal(ConnectionsFileEdit.PasswordChanged, ConnectionsFileEdit.WriteValue(path, ["mcpServers", "gh", "env", "T"], "x", "older"));
        Assert.Contains("\"newer\"", File.ReadAllText(path));
    }

    [Fact]
    public void WriteValue_KeepsAUtf16FileUtf16()
    {
        string path = Path.Combine(_dir, "wide.json");
        File.WriteAllText(path, """{ "mcpServers": { "gh": { "env": { "T": "plain" } } } }""", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        Assert.Null(ConnectionsFileEdit.WriteValue(path, ["mcpServers", "gh", "env", "T"], "sealed", "plain"));

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.Equal("""{ "mcpServers": { "gh": { "env": { "T": "sealed" } } } }""", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
    }
}
