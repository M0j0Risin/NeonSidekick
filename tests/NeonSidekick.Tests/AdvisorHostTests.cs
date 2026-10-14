using System.Net;
using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="AdvisorHost"/> over a real <see cref="LlmSession"/> (2026-10-10): profiles read without loading them, links kept and dropped,
/// and every advisor on the Claude Code server under its own conversation (2026-10-13; until then the first listed held the chat's process
/// and the rest answered text-only).
/// </summary>
public sealed class AdvisorHostTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly StubHttpMessageHandler _http = new();
    private readonly List<ChatClientFor> _built = [];
    private readonly List<FakeChatClient> _clients = [];
    private AppSettingsData _effective = new();

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private LlmSession Session() => new(
        new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        (LlmEndpoint _, LlmTimeouts _, ChatClientFor owner) =>
        {
            _built.Add(owner);
            var client = new FakeChatClient();
            _clients.Add(client);
            return client;
        },
        claudeCliOffered: _ => true);

    private static AppSettingsData OnClaudeCode() => new() { LlmUrl = ClaudeCliEndpoint.BaseUrl.ToString(), LlmModel = "sonnet" };

    [Fact]
    public async Task EveryAdvisorOnClaudeCode_GetsASessionLink_OnALinkedClient()
    {
        Profiles.Create(_home, "dba", OnClaudeCode());
        Profiles.Create(_home, "qa", OnClaudeCode());
        _effective = new AppSettingsData { AdvisorList = [new AdvisorEntry("dba", "architect"), new AdvisorEntry("qa", "tester")] };
        using var session = Session();
        using var host = new AdvisorHost(session, () => _home, () => _effective);

        var (dba, _) = await host.LinkAsync(host.Read("dba")!, CancellationToken.None);
        var (qa, _) = await host.LinkAsync(host.Read("QA")!, CancellationToken.None);

        Assert.True(dba!.Session);
        Assert.Null(dba.Warning);
        Assert.Equal("sonnet", dba.Model);
        Assert.True(qa!.Session);   // the second listed too (text-only until 2026-10-13)
        Assert.Null(qa.Warning);
        Assert.All(_built, owner => Assert.True(owner.Linked));   // each on a process of its own (SidekickApp.ClaudeHostFor)
        Assert.Equal("qa", host.Read("QA")!.Name);   // the spelling on disk
        Assert.Null(host.Read("nobody"));
    }

    [Fact]
    public async Task ALink_IsKept_UntilForgotten_OrItsProfileChanges()
    {
        _http.Map("http://127.0.0.1:8000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("qwen"));
        Profiles.Create(_home, "ux", new AppSettingsData { LlmUrl = "http://127.0.0.1:8000/v1" });
        _effective = new AppSettingsData { AdvisorList = [new AdvisorEntry("ux", "designer")] };
        using var session = Session();
        using var host = new AdvisorHost(session, () => _home, () => _effective);
        var profile = host.Read("ux")!;

        var (first, _) = await host.LinkAsync(profile, CancellationToken.None);
        await host.LinkAsync(profile, CancellationToken.None);
        Assert.Single(_built);   // kept
        Assert.False(first!.Session);
        Assert.Equal("qwen", first.Model);
        Assert.Same(profile.Settings, _built[0].Profile);   // its own server: built for its own profile

        host.Forget("ux");
        await host.LinkAsync(profile, CancellationToken.None);
        Assert.Equal(2, _built.Count);

        File.SetLastWriteTimeUtc(Profiles.ProfileFile(_home, "ux"), DateTime.UtcNow.AddMinutes(1));
        await host.LinkAsync(profile, CancellationToken.None);
        Assert.Equal(3, _built.Count);   // the profile file changed: linked anew
    }

    [Fact]
    public async Task Reset_DisposesEveryLink_AndTheNextCallLinksAnew()
    {
        // 2026-10-13: a Claude Code advisor's link owns its process, so Reset disposes the links outside the lock, side by side.
        Profiles.Create(_home, "dba", OnClaudeCode());
        Profiles.Create(_home, "qa", OnClaudeCode());
        _effective = new AppSettingsData { AdvisorList = [new AdvisorEntry("dba", "architect"), new AdvisorEntry("qa", "tester")] };
        using var session = Session();
        using var host = new AdvisorHost(session, () => _home, () => _effective);
        await host.LinkAsync(host.Read("dba")!, CancellationToken.None);
        await host.LinkAsync(host.Read("qa")!, CancellationToken.None);
        Assert.Equal(2, _clients.Count);

        host.Reset();

        Assert.All(_clients, client => Assert.True(client.Disposed));
        await host.LinkAsync(host.Read("dba")!, CancellationToken.None);
        Assert.Equal(3, _clients.Count);   // nothing kept
        Assert.False(_clients[2].Disposed);

        host.Forget("dba");
        Assert.True(_clients[2].Disposed);
    }
}
