using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The reasoning count the app estimates when the server streams thinking but counts none (2026-09-29, the setting
/// <c>LLM reasoning estimate</c>): llama.cpp's shape through the real SDK and adapter, the characters' estimate, the
/// <c>/tokenize</c> count and its fallback, the flag through <see cref="TokenUsage"/> and the <c>~</c> in <c>/usage</c>.
/// </summary>
public class ReasoningEstimateTests
{
    private const string ChatUrl = "http://127.0.0.1:1234/v1/chat/completions";
    private const string TokenizeUrl = "http://127.0.0.1:1234/tokenize";

    private const string Head = "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"my-model\",";

    /// <summary>llama-server's usage chunk: no reasoning count at all.</summary>
    private const string LlamaUsage = "{\"prompt_tokens\":10,\"completion_tokens\":50,\"total_tokens\":60}";

    private static string Chunk(string delta) =>
        Head + "\"choices\":[{\"index\":0,\"delta\":" + delta + ",\"logprobs\":null,\"finish_reason\":null}]}\n\n";

    /// <summary>Two thinking chunks (15 characters), an answer, the stop and the usage chunk, as llama-server streams them.</summary>
    private static string Stream(string usage = LlamaUsage, string answer = "Four.", params string[] thinking) =>
        string.Concat((thinking.Length == 0 ? ["abcdefghij", "klmno"] : thinking).Select(t => Chunk("{\"reasoning_content\":\"" + t + "\"}")))
        + Chunk("{\"content\":\"" + answer + "\"}")
        + Head + "\"choices\":[{\"index\":0,\"delta\":{},\"logprobs\":null,\"finish_reason\":\"stop\"}]}\n\n"
        + Head + "\"choices\":[],\"usage\":" + usage + "}\n\n"
        + "data: [DONE]\n\n";

    private readonly StubHttpMessageHandler _http = new();

    private OpenAICompatibleChatClient Client(ReasoningEstimate? mode, LlmEndpoint? endpoint = null) =>
        new(endpoint ?? new LlmEndpoint(new Uri("http://127.0.0.1:1234"), "my-model", "k", "test"), TimeSpan.FromSeconds(5), new HttpClient(_http), mode is { } m ? () => m : null);

    private static async Task<TokenUsage> Turn(IChatClient client)
    {
        var assistant = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
        TurnEvent.Usage? reported = null;
        await foreach (var evt in assistant.RunTurnAsync("q"))
        {
            if (evt is TurnEvent.Usage u) reported = u;
        }

        return Assert.IsType<TurnEvent.Usage>(reported).Tokens;
    }

    private int TokenizeRequests => _http.Requests.Count(r => r.Uri.AbsoluteUri == TokenizeUrl);

    // ── The characters ──────────────────────────────────────────────────────

    [Fact]
    public async Task Chars_EstimatesTheStreamedThinking_AndMarksIt()
    {
        _http.Map(ChatUrl, HttpStatusCode.OK, Stream(), "text/event-stream");
        using var client = Client(ReasoningEstimate.Chars);

        var usage = await Turn(client);

        Assert.Equal(4, usage.Reasoning);   // 15 characters over 4, rounded up
        Assert.True(usage.ReasoningEstimated);
        Assert.Equal((10, 50, 60), (usage.Input, usage.Output, usage.Total));   // the server's own counts untouched
        Assert.Contains("(~4 reasoning)", Assistant.UsageLogLine(usage));
        Assert.Equal(0, TokenizeRequests);
    }

    [Fact]
    public async Task Off_OrNoSetting_LeavesTheCountUnreported()
    {
        _http.Map(ChatUrl, HttpStatusCode.OK, Stream(), "text/event-stream");
        using var off = Client(ReasoningEstimate.Off);
        using var none = Client(null);

        Assert.Null((await Turn(off)).Reasoning);
        Assert.Null((await Turn(none)).Reasoning);
        Assert.False((await Turn(none)).ReasoningEstimated);
    }

    [Fact]
    public async Task AServersOwnCount_IsNeverReplaced()
    {
        _http.Map(ChatUrl, HttpStatusCode.OK, Stream(UsageStreamTests.VllmUsage), "text/event-stream");
        using var client = Client(ReasoningEstimate.Tokenize);

        var usage = await Turn(client);

        Assert.Equal(135, usage.Reasoning);
        Assert.False(usage.ReasoningEstimated);
        Assert.Equal(0, TokenizeRequests);
    }

    [Fact]
    public async Task NoThinking_IsNoEstimate()
    {
        _http.Map(ChatUrl, HttpStatusCode.OK,
            Chunk("{\"content\":\"Four.\"}") + Head + "\"choices\":[],\"usage\":" + LlamaUsage + "}\n\ndata: [DONE]\n\n", "text/event-stream");
        using var client = Client(ReasoningEstimate.Chars);

        Assert.Null((await Turn(client)).Reasoning);
    }

    [Fact]
    public async Task ThinkingLeftInTags_IsEstimatedToo_AndTheTextGoesOnUnchanged()
    {
        _http.Map(ChatUrl, HttpStatusCode.OK,
            Chunk("{\"content\":\"<think>abcdefgh\"}") + Chunk("{\"content\":\"</think>Four.\"}")
            + Head + "\"choices\":[],\"usage\":" + LlamaUsage + "}\n\ndata: [DONE]\n\n", "text/event-stream");
        using var client = Client(ReasoningEstimate.Chars);
        var text = new System.Text.StringBuilder();
        UsageDetails? details = null;

        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")]))
        {
            text.Append(update.Text);
            details ??= update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details;
        }

        Assert.Equal("<think>abcdefgh</think>Four.", text.ToString());
        Assert.Equal(2, details!.ReasoningTokenCount);   // 8 characters
        Assert.Equal(1, details.AdditionalCounts![OpenAICompatibleChatClient.ReasoningEstimatedKey]);
    }

    [Fact]
    public async Task ANonStreamedResponse_IsEstimatedTheSameWay()
    {
        _http.Map(ChatUrl, HttpStatusCode.OK, StubHttpMessageHandler.CompletionJson("<think>abcdefghijkl</think>Four."));
        using var client = Client(ReasoningEstimate.Chars);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        Assert.Equal(3, response.Usage!.ReasoningTokenCount);   // 12 characters
        Assert.True(TokenUsage.From(response.Usage, TimeSpan.Zero, TimeSpan.Zero).ReasoningEstimated);
    }

    // ── /tokenize ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Tokenize_AsksTheServer_WithItsKey_AndUsesItsCount()
    {
        _http.Map(ChatUrl, HttpStatusCode.OK, Stream(), "text/event-stream");
        _http.Map(TokenizeUrl, HttpStatusCode.OK, "{\"tokens\":[1,2,3]}");
        using var client = Client(ReasoningEstimate.Tokenize);

        var usage = await Turn(client);

        Assert.Equal(3, usage.Reasoning);
        Assert.True(usage.ReasoningEstimated);
        var asked = Assert.Single(_http.Requests, r => r.Uri.AbsoluteUri == TokenizeUrl);
        Assert.Equal(HttpMethod.Post, asked.Method);
        Assert.Equal("Bearer k", asked.Authorization);
        Assert.Equal("{\"content\":\"abcdefghijklmno\",\"add_special\":false,\"parse_special\":false}", asked.Body);
    }

    [Fact]
    public async Task Tokenize_ThatDoesNotAnswer_FallsBackToTheCharacters_AndIsNotAskedAgain()
    {
        _http.Map(ChatUrl, HttpStatusCode.OK, Stream(), "text/event-stream");
        _http.Map(TokenizeUrl, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
        using var client = Client(ReasoningEstimate.Tokenize);

        Assert.Equal(4, (await Turn(client)).Reasoning);
        Assert.Equal(4, (await Turn(client)).Reasoning);

        Assert.Equal(1, TokenizeRequests);
    }

    [Fact]
    public async Task Tokenize_OnTheLocalLlm_GoesToItsLivePort()
    {
        _http.Map("http://127.0.0.1:5555/v1/chat/completions", HttpStatusCode.OK, Stream(), "text/event-stream");
        _http.Map("http://127.0.0.1:5555/tokenize", HttpStatusCode.OK, "{\"tokens\":[7,8,9,10,11]}");
        var local = new LlmEndpoint(LocalLlm.LocalEndpoint.BaseUrl, "gemma-4-e2b", "per-start-key", "local") { LiveUrl = new Uri("http://127.0.0.1:5555/v1") };
        using var client = Client(ReasoningEstimate.Tokenize, local);

        Assert.Equal(5, (await Turn(client)).Reasoning);
        Assert.Equal("Bearer per-start-key", Assert.Single(_http.Requests, r => r.Uri.AbsolutePath == "/tokenize").Authorization);
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host == LocalLlm.LocalEndpoint.Host);
    }

    [Fact]
    public void TheTokenizeHelpers_ArePinned()
    {
        Assert.Equal(3, OpenAICompatibleChatClient.TokenCount("{\"tokens\":[1,2,3]}"));
        Assert.Equal(2, OpenAICompatibleChatClient.TokenCount("{\"tokens\":[{\"id\":1,\"piece\":\"a\"},{\"id\":2,\"piece\":\"b\"}]}"));
        Assert.Null(OpenAICompatibleChatClient.TokenCount(null));
        Assert.Null(OpenAICompatibleChatClient.TokenCount("not json"));
        Assert.Null(OpenAICompatibleChatClient.TokenCount("{\"error\":\"x\"}"));
        Assert.Null(OpenAICompatibleChatClient.TokenCount("[1,2]"));
        Assert.Equal("{\"content\":\"a\\u0022b\",\"add_special\":false,\"parse_special\":false}", OpenAICompatibleChatClient.TokenizeBody("a\"b"));   // the writer's safe escape, still JSON
        Assert.Equal(0, OpenAICompatibleChatClient.CharacterEstimate(""));
        Assert.Equal(1, OpenAICompatibleChatClient.CharacterEstimate("abc"));
        Assert.Equal(1, OpenAICompatibleChatClient.CharacterEstimate("abcd"));
        Assert.Equal(2, OpenAICompatibleChatClient.CharacterEstimate("abcde"));
    }

    // ── The flag through the tally and the display ──────────────────────────

    [Fact]
    public void TheFlag_IsReadFromTheReport_AndASumWithAnEstimateInItIsAnEstimate()
    {
        var counted = TokenUsage.From(new UsageDetails { InputTokenCount = 1, OutputTokenCount = 9, ReasoningTokenCount = 5 }, TimeSpan.Zero, TimeSpan.Zero);
        var estimated = TokenUsage.From(new UsageDetails { InputTokenCount = 1, OutputTokenCount = 9, ReasoningTokenCount = 3, AdditionalCounts = new() { [OpenAICompatibleChatClient.ReasoningEstimatedKey] = 1 } }, TimeSpan.Zero, TimeSpan.Zero);
        var unflaggedNothing = TokenUsage.From(new UsageDetails { AdditionalCounts = new() { [OpenAICompatibleChatClient.ReasoningEstimatedKey] = 1 } }, TimeSpan.Zero, TimeSpan.Zero);

        Assert.False(counted.ReasoningEstimated);
        Assert.True(estimated.ReasoningEstimated);
        Assert.False(unflaggedNothing.ReasoningEstimated);   // no count, nothing to mark
        Assert.Equal(8, (counted + estimated).Reasoning);
        Assert.True((counted + estimated).ReasoningEstimated);
        Assert.False((counted + counted).ReasoningEstimated);

        var tally = new TokenTally();
        tally.Add(counted);
        tally.Add(estimated);
        Assert.True(tally.Session.ReasoningEstimated);
    }

    [Fact]
    public void Usage_ShowsAnEstimateWithTheMark()
    {
        var estimated = new TokenUsage(100, 150, 250, 1, TimeSpan.Zero, TimeSpan.Zero, Reasoning: 1234, ReasoningEstimated: true);
        var counted = estimated with { ReasoningEstimated = false };

        Assert.Equal("~1,234", UsageText.ReasoningValue(estimated));
        Assert.Equal("1,234", UsageText.ReasoningValue(counted));
        Assert.Null(UsageText.ReasoningValue(counted with { Reasoning = null }));
        Assert.Contains(("Reasoning", "~1,234"), UsageText.Rows(estimated, averaged: false));
        Assert.Contains(("Reasoning", "1,234"), UsageText.Rows(counted, averaged: false));
    }

    // ── The setting ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("off", ReasoningEstimate.Off)]
    [InlineData(" Chars ", ReasoningEstimate.Chars)]
    [InlineData("TOKENIZE", ReasoningEstimate.Tokenize)]
    public void TheSetting_Parses(string saved, ReasoningEstimate mode)
    {
        Assert.Equal(mode, ReasoningEstimates.Resolve(new AppSettingsData { LlmReasoningEstimate = saved }));
    }

    [Fact]
    public void TheSetting_DefaultsToChars_AndReadsAnythingElseAsChars()
    {
        Assert.Equal("chars", new AppSettingsData().LlmReasoningEstimate);
        Assert.Equal(["off", "chars", "tokenize"], ReasoningEstimates.Names);
        Assert.False(ReasoningEstimates.TryParse("exact", out var mode));
        Assert.Equal(ReasoningEstimate.Chars, mode);
        Assert.Equal(ReasoningEstimate.Chars, ReasoningEstimates.Resolve(new AppSettingsData { LlmReasoningEstimate = "exact" }));
        Assert.All(ReasoningEstimates.Names, n => Assert.False(string.IsNullOrEmpty(ReasoningEstimates.Describe(n))));
        Assert.Equal("tokenize  [#9A8BB8]llama.cpp's /tokenize counts it exactly (one short request); else ÷ 4[/]", SettingsMenu.ReasoningEstimateLabel("tokenize"));
        Assert.Equal("LLM reasoning estimate", SettingsMenu.FieldName(SettingsField.LlmReasoningEstimate));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmReasoningEstimate));   // read at each request: no reconnect
        Assert.False(SettingsMenu.IsToggle(SettingsField.LlmReasoningEstimate));
        Assert.Equal("tokenize", AppSettings.Copy(new AppSettingsData { LlmReasoningEstimate = "tokenize" }).LlmReasoningEstimate);
    }
}
