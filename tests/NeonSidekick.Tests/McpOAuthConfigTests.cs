using NeonSidekick.Mcp;
using NeonSidekick.Shell;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// An <c>mcp.json</c> entry's <c>oauth</c> object (2026-11-21, feature idea #32): its problems, each the server's own; the Authorization
/// header in any case; its secret sealed in place, revealed at connect and kept out of a reload's "changed"; <c>${NAME}</c> in its id and
/// secret; and the token file on the shell police's tripwire.
/// </summary>
public sealed class McpOAuthConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public McpOAuthConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private McpConfigLoad Load(string servers, bool seal = false)
    {
        string path = Path.Combine(_dir, McpConfigFile.FileName);
        File.WriteAllText(path, "{ \"mcpServers\": { " + servers + " } }");
        return McpConfigFile.Load(path, seal);
    }

    [Fact]
    public void TheOAuthProblems_AreEachTheirServersOwn_AndTheOtherServersStillLoad()
    {
        var load = Load("""
            "ok": { "url": "https://a/mcp", "oauth": { "clientId": "id", "scopes": ["read"], "redirectPort": 9000 } },
            "stdio": { "command": "x", "oauth": { "clientId": "id" } },
            "port": { "url": "https://b/mcp", "oauth": { "redirectPort": 70000 } },
            "half": { "url": "https://b/mcp", "oauth": { "redirectPort": 12.5 } },
            "shape": { "url": "https://c/mcp", "oauth": "yes" },
            "scopes": { "url": "https://d/mcp", "oauth": { "scopes": "read" } },
            "secret": { "url": "https://e/mcp", "oauth": { "clientSecret": "s" } }
            """);
        var ok = Assert.Single(load.Servers).Value;
        Assert.Equal(9000, ok.RedirectPort);
        Assert.Equal(["read"], ok.OAuth!.Scopes!);
        Assert.Equal("id", ok.OAuth.ClientId);
        Assert.Equal(McpText.OAuthNeedsUrl, load.Problems.Single(p => p.Name == "stdio").Reason);
        Assert.Equal(McpText.BadRedirectPort, load.Problems.Single(p => p.Name == "port").Reason);
        Assert.Equal(McpText.BadRedirectPort, load.Problems.Single(p => p.Name == "half").Reason);
        Assert.Equal(McpText.BadOAuth, load.Problems.Single(p => p.Name == "shape").Reason);
        Assert.Equal(McpText.BadOAuth, load.Problems.Single(p => p.Name == "scopes").Reason);
        Assert.Equal(McpText.OAuthSecretNeedsId, load.Problems.Single(p => p.Name == "secret").Reason);   // #79
    }

    /// <summary>An oauth block beside the server's own key (2026-11-22, the review's #53): loaded, the header kept, said once across reads.</summary>
    [Fact]
    public void AnOAuthBlock_BesideAKeyHeader_IsIgnored_SaidOnce()
    {
        var warnings = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> heard = e => { if (e.Level == NeonSidekick.Diagnostics.DiagnosticLevel.Warning && e.Message.Contains("oauth block is ignored", StringComparison.Ordinal)) { lock (warnings) { warnings.Add(e.Message); } } };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += heard;
        try
        {
            string servers = "\"keyed-" + Guid.NewGuid().ToString("N") + "\": { \"url\": \"https://a/mcp\", \"headers\": { \"Authorization\": \"Bearer x\" }, \"oauth\": { \"clientId\": \"id\" } }";
            var first = Load(servers, seal: true);   // the read that logs
            Load(servers + " ", seal: true);   // the file changed: read again

            var config = Assert.Single(first.Servers).Value;
            Assert.Empty(first.Problems);
            Assert.True(config.HasAuthorizationHeader);   // the header kept (sealed in place by the read)
            Assert.False(config.SignInPossible);
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= heard;
        }

        Assert.Single(warnings);
    }

    [Fact]
    public void AnAuthorizationHeader_InAnyCase_MeansNoSignIn_AndTheDefaultPortIsFixed()
    {
        Assert.False(new McpServerConfig { Url = "https://a", Headers = new() { ["authorization"] = "Bearer x" } }.SignInPossible);
        Assert.False(new McpServerConfig { Url = "https://a", Headers = new() { ["AUTHORIZATION"] = "Bearer x" } }.SignInPossible);
        // A key of its own in any header or in the address (2026-11-22, the review's #111): its 401 is the key's, never a sign-in.
        Assert.False(new McpServerConfig { Url = "https://a", Headers = new() { ["X-Key"] = "x" } }.SignInPossible);
        Assert.False(new McpServerConfig { Url = "https://a", Headers = new() { ["X-Api-Key"] = "x" } }.SignInPossible);
        Assert.False(new McpServerConfig { Url = "https://a/mcp?api_key=abc123" }.SignInPossible);
        Assert.True(new McpServerConfig { Url = "https://a", Headers = new() { ["X-Trace"] = "1" } }.SignInPossible);
        Assert.False(new McpServerConfig { Command = "x" }.SignInPossible);
        Assert.Equal(8766, new McpServerConfig { Url = "https://a" }.RedirectPort);
        Assert.Equal(new Uri("http://127.0.0.1:8766/callback"), McpOAuth.RedirectUri(McpOAuth.DefaultRedirectPort));
    }

    [WindowsFact]
    public void TheClientSecret_IsSealedInPlace_RevealedAtConnect_AndASealedCopyIsTheSameConnection()
    {
        var load = Load("""
            "lin": { "url": "https://mcp.linear.app/mcp", "oauth": { "clientId": "id", "clientSecret": "s3cret" } },
            "ref": { "url": "https://x/mcp", "oauth": { "clientId": "${LIN_ID}", "clientSecret": "${LIN_SECRET}" } }
            """, seal: true);
        string text = File.ReadAllText(Path.Combine(_dir, McpConfigFile.FileName));
        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
        Assert.Contains("${LIN_SECRET}", text, StringComparison.Ordinal);   // a reference is no secret: never sealed
        var stored = load.Servers.Single(s => s.Key == "lin").Value;
        Assert.True(WindowsCredentials.IsSealed(stored.OAuth!.ClientSecret));
        var (open, error) = McpSecrets.Reveal(stored);
        Assert.Null(error);
        Assert.Equal("s3cret", open!.OAuth!.ClientSecret);
        Assert.True(WindowsCredentials.IsSealed(stored.OAuth.ClientSecret));   // the stored config never holds it plain
        var plain = new McpServerConfig { Url = "https://mcp.linear.app/mcp", OAuth = new McpOAuthConfig { ClientId = "id", ClientSecret = "s3cret" } };
        Assert.True(McpSecrets.SameConnection(plain, stored));
        Assert.False(McpSecrets.SameConnection(plain, stored.With(oauth: new McpOAuthConfig { ClientId = "other", ClientSecret = stored.OAuth.ClientSecret })));
    }

    [Fact]
    public void ReferencesInTheOAuthIdAndSecret_AreFilledAtConnect_AndAnUnsetOneIsSaid()
    {
        var config = new McpServerConfig { Url = "https://x/mcp", OAuth = new McpOAuthConfig { ClientId = "${LIN_ID}", ClientSecret = "${LIN_SECRET}", RedirectPort = 9100 } };
        Assert.True(McpReferences.Holds(config));
        var (expanded, unset) = McpReferences.Expand(config, name => name == "LIN_ID" ? "abc" : null);
        Assert.Equal("abc", expanded.OAuth!.ClientId);
        Assert.Equal("${LIN_SECRET}", expanded.OAuth.ClientSecret);
        Assert.Equal(9100, expanded.OAuth.RedirectPort);
        Assert.Equal(new McpUnsetReference("oauth.clientSecret", "LIN_SECRET"), Assert.Single(unset));
        Assert.Equal("${LIN_ID}", config.OAuth!.ClientId);   // the catalog's copy keeps its text
    }

    [Fact]
    public void TheTokenFile_IsOnTheShellPolicesTripwire()
    {
        string home = Path.Combine(_dir, "home");
        string tokens = McpTokenStore.PathFor(Path.Combine(home, "profiles", "neon"));
        var guard = new TrustedFilesPolice.Guard(home, Path.Combine(_dir, "work"));
        Assert.NotNull(TrustedFilesPolice.Find("type \"" + tokens + "\"", guard));
    }
}
