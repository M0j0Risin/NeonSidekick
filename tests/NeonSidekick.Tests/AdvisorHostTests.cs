using System.Net;
using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="AdvisorHost"/> over a real <see cref="LlmSession"/> (2026-10-10): profiles read without loading them, links kept, and the
/// Claude Code gate — the first listed advisor on the Claude Code server holds it while the chat is elsewhere, the rest answer text-only.
/// </summary>
public sealed class AdvisorHostTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly StubHttpMessageHandler _http = new();
    private readonly List<AppSettingsData?> _built = [];
    private AppSettingsData _effective = new();

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private LlmSession Session() => new(
        new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        (LlmEndpoint _, LlmTimeouts _, AppSettingsData? profile) =>
        {
            _built.Add(profile);
            return new FakeChatClient();
        },
        claudeCliOffered: _ => true);

    private static AppSettingsData OnClaudeCode() => new() { LlmUrl = ClaudeCliEndpoint.BaseUrl.ToString(), LlmModel = "sonnet" };

    [Fact]
    public async Task TheFirstListedOnClaudeCode_HoldsTheServer_TheOthersAnswerTextOnly()
    {
        Profiles.Create(_home, "dba", OnClaudeCode());
        Profiles.Create(_home, "qa", OnClaudeCode());
        _effective = new AppSettingsData { AdvisorList = [new AdvisorEntry("dba", "architect"), new AdvisorEntry("qa", "tester")] };
        using var session = Session();
        using var host = new AdvisorHost(session, () => _home, () => _effective);

        var (dba, _) = await host.LinkAsync(host.Read("dba")!, CancellationToken.None);
        var (qa, _) = await host.LinkAsync(host.Read("QA")!, CancellationToken.None);

        Assert.True(dba!.Tools && dba.Session);
        Assert.Null(dba.Warning);
        Assert.Equal("sonnet", dba.Model);
        Assert.False(qa!.Tools || qa.Session);
        Assert.Equal(AdvisorText.OtherClaudeCodeNote("dba"), qa.Warning);
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
        Assert.True(first!.Tools);
        Assert.False(first.Session);
        Assert.Equal("qwen", first.Model);
        Assert.Same(profile.Settings, _built[0]);   // its own server: built for its own profile

        host.Forget("ux");
        await host.LinkAsync(profile, CancellationToken.None);
        Assert.Equal(2, _built.Count);

        File.SetLastWriteTimeUtc(Profiles.ProfileFile(_home, "ux"), DateTime.UtcNow.AddMinutes(1));
        await host.LinkAsync(profile, CancellationToken.None);
        Assert.Equal(3, _built.Count);   // the profile file changed: linked anew
    }
}
