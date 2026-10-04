using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/server</c> and the Claude CLI server (2026-09-30): the row while offered, the refusal while not, and nothing started before a message.</summary>
public partial class ChatScreenTests
{
    /// <summary>The screen over a session whose Claude CLI is offered or not, its process a <see cref="FakeClaudeServerHost"/>.</summary>
    private FakeClaudeServerHost UseClaudeCli(bool offered = true)
    {
        var host = new FakeClaudeServerHost();
        _session.Dispose();
        _session = new LlmSession(new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)), new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
            (endpoint, _) => { _endpoints.Add(endpoint); return _chat; }, _time, claudeServer: host, claudeCliOffered: _ => offered);
        return host;
    }

    [Fact]
    public async Task Server_ClaudeCli_PicksItsModel_SavesTheSentinel_AndStartsNothingYet()
    {
        _settings.Update(d => d.TtsOutput = false);
        var host = UseClaudeCli();
        PushLine("/server claude-cli");
        _console.Input.PushKey(Keys.Enter);    // the model picker's cursor on the CLI's first, the default, sonnet (A to Z since 2026-10-03)
        _console.Input.PushKey(Keys.Escape);   // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal("http://claude-cli.localhost/v1", _settings.Current.LlmUrl);
        Assert.Equal(ClaudeCliEndpoint.DefaultModel, _settings.Current.LlmModel);
        Assert.Equal(ClaudeCliEndpoint.BaseUrl, _endpoints[^1].BaseUrl);
        Assert.Contains("LLM: http://claude-cli.localhost/v1 model=sonnet", output);
        Assert.Equal(new ContextLength(ClaudeCliEndpoint.DefaultContextWindow, LlmSession.ClaudeCliSource), _session.ContextLength);
        Assert.Empty(host.Starts);   // the CLI starts with the first message, its prompt and tools known
    }

    [Fact]
    public async Task Server_ClaudeCli_NotOffered_SaysHowToTurnItOn()
    {
        _settings.Update(d => d.TtsOutput = false);
        string url = _settings.Current.LlmUrl;
        UseClaudeCli(offered: false);
        PushLine("/server claude-cli");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("The Claude CLI server is not offered", output);
        Assert.Equal(url, _settings.Current.LlmUrl);
    }
}
