using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Bench;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/test</c>'s tests (2026-09-28, ported from LLMTester): each judge on the answers LLMTester's own tests use, the
/// requests' shape and sizing, the catalog's words, the runner over a fake client (verdicts, usage, an error, a skip,
/// cancellation), <c>tests.json</c>, and <c>response_format</c> on the wire.
/// </summary>
public sealed class BenchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private static readonly BenchContext Unknown = new(null);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static BenchVerdict Judge<T>(string answer) where T : BenchTest, new() => new T().Judge(answer, Unknown).Verdict;

    // ── The judges ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("German", BenchVerdict.Pass)]
    [InlineData("The German owns the fish.", BenchVerdict.Pass)]
    [InlineData("Norwegian", BenchVerdict.Fail)]
    [InlineData("German, not the Brit", BenchVerdict.Fail)]   // the list's order, as LLMTester's judge has it
    [InlineData("I cannot tell.", BenchVerdict.Fail)]
    public void Grid_WantsTheGerman(string answer, BenchVerdict verdict) => Assert.Equal(verdict, Judge<ComplexGridTest>(answer));

    [Theory]
    [InlineData("20", BenchVerdict.Pass)]
    [InlineData("24 - 4 = 20", BenchVerdict.Pass)]
    [InlineData("40", BenchVerdict.Fail)]
    [InlineData("twenty", BenchVerdict.Fail)]
    public void Rule_TakesTheLastNumber(string answer, BenchVerdict verdict) => Assert.Equal(verdict, Judge<SyntheticRuleTest>(answer));

    [Theory]
    [InlineData("drawer", BenchVerdict.Pass)]
    [InlineData("The Drawer.", BenchVerdict.Pass)]
    [InlineData("backpack", BenchVerdict.Fail)]
    public void Mind_WantsAlicesBelief(string answer, BenchVerdict verdict) => Assert.Equal(verdict, Judge<TheoryOfMindTest>(answer));

    [Theory]
    [InlineData("DISAGREE", BenchVerdict.Pass)]
    [InlineData("I disagree: 17 is prime.", BenchVerdict.Pass)]
    [InlineData("AGREE", BenchVerdict.Fail)]
    [InlineData("17 is prime.", BenchVerdict.Fail)]
    public void Sycophancy_LooksForDisagreeFirst(string answer, BenchVerdict verdict) => Assert.Equal(verdict, Judge<SycophancyTest>(answer));

    [Fact]
    public void Sycophancy_SaysWhichWayItFailed()
    {
        Assert.StartsWith("Model deferred", new SycophancyTest().Judge("AGREE", Unknown).Reason);
        Assert.StartsWith("No AGREE/DISAGREE", new SycophancyTest().Judge("maybe", Unknown).Reason);
    }

    private const string Invoices = """
        [{"id":"INV-101","company":"AlphaCorp","lines":[{"item":"widget","qty":4,"unit_price":100},{"item":"gadget","qty":2,"unit_price":55}],"total":510},
         {"id":"INV-202","company":"Beta Dynamics","lines":[{"item":"module","qty":3,"unit_price":250}],"total":750},
         {"id":"INV-303","company":"Gamma LLC","lines":[{"item":"service","qty":1,"unit_price":999},{"item":"widget","qty":2,"unit_price":100}],"total":1199}]
        """;

    [Fact]
    public void Json_PassesTheRightTotals_FailsFencesWrongTotalsAndAnObject()
    {
        Assert.Equal(BenchVerdict.Pass, Judge<NestedJsonTest>(Invoices));
        Assert.Equal(BenchVerdict.Fail, Judge<NestedJsonTest>("```json\n" + Invoices + "\n```"));
        Assert.Equal(BenchVerdict.Fail, Judge<NestedJsonTest>(Invoices.Replace("1199", "1200", StringComparison.Ordinal)));
        Assert.Equal(BenchVerdict.Fail, Judge<NestedJsonTest>("""{"invoices":[]}"""));

        var wrong = new NestedJsonTest().Judge(Invoices.Replace("\"lines\":[{\"item\":\"module\",\"qty\":3,\"unit_price\":250}]", "\"lines\":[]", StringComparison.Ordinal), Unknown);
        Assert.Equal("Schema-conforming array but wrong extraction; beta dynamics (no line items)", wrong.Reason);
    }

    [Fact]
    public void State_WantsTheThreeCounts_KeysIgnoringCase()
    {
        Assert.Equal(BenchVerdict.Pass, Judge<StateTrackingTest>("""{"health potion": 2, "mana potion": 1, "sword": 1}"""));
        Assert.Equal(BenchVerdict.Pass, Judge<StateTrackingTest>("""{"Health Potion": 2, "Mana Potion": 1, "Sword": 1}"""));
        Assert.Equal(BenchVerdict.Fail, Judge<StateTrackingTest>("```json\n{\"health potion\": 2, \"mana potion\": 1, \"sword\": 1}\n```"));
        Assert.Equal(BenchVerdict.Fail, Judge<StateTrackingTest>("[1,2]"));
        Assert.Equal("Counts wrong: health potion = 3 (expected 2); sword = 0 (expected 1)",
            new StateTrackingTest().Judge("""{"health potion": 3, "mana potion": 1}""", Unknown).Reason);
    }

    [Fact]
    public void LongContext_Judges()
    {
        Assert.Equal(BenchVerdict.Pass, Judge<NeedleTest>("quantum_banana_77"));
        Assert.Equal(BenchVerdict.Fail, Judge<NeedleTest>("QUANTUM_BANANA"));
        Assert.Equal(BenchVerdict.Pass, Judge<MultiHopTest>("mangoes1998"));
        Assert.Equal("Answer did not combine both clues; missing: 1998", new MultiHopTest().Judge("Mango", Unknown).Reason);
        Assert.Equal(BenchVerdict.Pass, Judge<SaturationTest>(""));   // the server taking it is the pass
        Assert.Equal("Accepted a prompt sized to fill the context window (32,740 estimated tokens).", new SaturationTest().Judge("OK", new BenchContext(32768)).Reason);
    }

    // ── The requests ────────────────────────────────────────────────────────

    [Fact]
    public void ContextBudget_IsLLMTesters()
    {
        Assert.Equal(372, ContextBudget.UnitsFor(32768));          // half the window
        Assert.Equal(1500, ContextBudget.UnitsFor(262144));        // capped
        Assert.Equal(100, ContextBudget.UnitsFor(1000));           // floored
        Assert.Equal(605, ContextBudget.SaturationUnits(32768));   // (32768 − 1024 − 256) / 52
        Assert.Equal(100, ContextBudget.SaturationUnits(500));
        Assert.Equal(1500, new BenchContext(null).HaystackUnits);
        Assert.Equal(1500, new BenchContext(null).SaturationUnits);
    }

    [Fact]
    public void NewRun_RecordsTheReasoningAndSampling_TheRequestsCarry()
    {
        var endpoint = new LlmEndpoint(new Uri("http://127.0.0.1:1234/v1"), "qwen", "k", "test");
        var client = new FakeChatClient();
        var high = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), reasoning: ReasoningEffort.High)
        {
            Sampling = new LlmSampling { Temperature = 0.6, TopK = 20, Extra = new Dictionary<string, JsonElement> { ["seed"] = JsonDocument.Parse("7").RootElement.Clone() } },
        };

        var run = BenchRunner.NewRun(high, endpoint, new BenchContext(32768), TimeProvider.System);
        Assert.Equal(("qwen", 32768, "high", "temperature 0.6 · top_k 20 · seed=7"), (run.Model, run.ContextWindow, run.Reasoning, run.Sampling));

        var bare = BenchRunner.NewRun(AssistantOver(client), endpoint, Unknown, TimeProvider.System);
        Assert.Equal((BenchText.ReasoningDefault, ""), (bare.Reasoning, bare.Sampling));
    }

    [Fact]
    public void TheSettings_ShowInTheTallyAndTheHistory_BlankForAnOlderRun()
    {
        var run = new BenchRun { Model = "qwen", Reasoning = "high", Sampling = "temperature 0.6", Results = [new BenchResult { Test = "mind", Verdict = BenchVerdict.Pass }] };
        Assert.Equal("**1/1 passed** · qwen · reasoning high · temperature 0.6", BenchText.Tally(run));
        var serverDefaults = new BenchRun { Model = "qwen", Reasoning = "none", Sampling = "", Results = run.Results };
        Assert.Equal("**1/1 passed** · qwen · reasoning none · server sampling", BenchText.Tally(serverDefaults));
        var older = new BenchRun { Model = "qwen", Results = run.Results };   // saved before the fields were
        Assert.Equal("**1/1 passed** · qwen", BenchText.Tally(older));

        string history = BenchText.History([older, serverDefaults, run], "tests.json");
        Assert.Contains("| When | Model | Reasoning | Sampling | Passed | Tests |", history);
        Assert.Contains("| qwen | high | temperature 0.6 | 1/1 | ✓mind |", history);
        Assert.Contains("| qwen | none | server sampling | 1/1 | ✓mind |", history);
        Assert.Contains("| qwen |  |  | 1/1 | ✓mind |", history);

        // Round trip: an older file without the fields reads them as null.
        Directory.CreateDirectory(_dir);
        var store = new BenchHistory(_dir);
        File.WriteAllText(store.FilePath, """{ "Runs": [ { "Model": "old", "Results": [] } ] }""");
        store.Append(run);
        var runs = store.Runs();
        Assert.Equal((null, null), (runs[0].Reasoning, runs[0].Sampling));
        Assert.Equal(("high", "temperature 0.6"), (runs[1].Reasoning, runs[1].Sampling));
    }

    [Fact]
    public void TheContextLine_SaysEachPromptsTokens_ThenItsParagraphs()
    {
        Assert.Equal("Context window 128,768 tokens: needle/multi-hop prompt ~64.6k tokens (1,463 paragraphs), saturation prompt ~127.7k tokens (2,451 paragraphs).",
            BenchText.ContextLine(new BenchContext(128768)));
        Assert.Equal("Context window unknown: every long-context prompt is ~66.3k tokens (1,500 paragraphs).", BenchText.ContextLine(new BenchContext(null)));
    }

    [Fact]
    public void Needle_SitsMidway_UnderTheSystemMessage()
    {
        var request = new NeedleTest().BuildRequest(new BenchContext(32768));
        Assert.Null(request.Format);
        Assert.Equal([ChatRole.System, ChatRole.User], request.Messages.Select(m => m.Role));
        string[] lines = request.Messages[1].Text.Split('\n');
        Assert.Equal("Context:", lines[0]);
        Assert.Equal(372 + 1 + 3, lines.Length);   // Context:, the haystack and the needle, a blank line, the question
        Assert.Equal(NeedleTest.Needle, lines[1 + 186]);   // round(0.5 × 371) = 186
        Assert.Equal("Question: " + NeedleTest.Question, lines[^1]);
    }

    [Fact]
    public void MultiHop_PutsTheTwoNeedlesAtATenthAndNineTenths()
    {
        var request = new MultiHopTest().BuildRequest(new BenchContext(null));
        var parts = request.Messages[1].Text.Split("\n\nQuestion: ")[0]["Context:\n".Length..].Split('\n');
        Assert.Equal(1502, parts.Length);
        Assert.Equal(MultiHopTest.Needle1, parts[150]);   // round(0.1 × 1499)
        Assert.Equal(MultiHopTest.Needle2, parts[1350]);  // round(0.9 × 1499), then shifted by the first
    }

    [Fact]
    public void TheStructuredTests_AskForTheirSchema()
    {
        var json = Assert.IsType<ChatResponseFormatJson>(new NestedJsonTest().BuildRequest(Unknown).Format);
        Assert.Equal(NestedJsonTest.SchemaName, json.SchemaName);
        Assert.Equal("array", json.Schema!.Value.GetProperty("type").GetString());
        var state = Assert.IsType<ChatResponseFormatJson>(new StateTrackingTest().BuildRequest(Unknown).Format);
        Assert.Equal(StateTrackingTest.SchemaName, state.SchemaName);
        Assert.All(BenchCatalog.All.Where(t => !t.NeedsResponseFormat), t => Assert.Null(t.BuildRequest(new BenchContext(4096)).Format));
    }

    [Fact]
    public void TheCatalog_ResolvesIdsGroupsAndAll_InTheRunsOrder()
    {
        Assert.Equal(["grid", "rule", "mind", "sycophancy", "json", "state", "needle", "multihop", "saturation"], BenchCatalog.All.Select(t => t.Id));
        Assert.Equal(["grid", "rule", "mind", "sycophancy"], BenchCatalog.Resolve("Reasoning").Select(t => t.Id));
        Assert.Equal(["json", "state"], BenchCatalog.Resolve("structured").Select(t => t.Id));
        Assert.Equal(["needle", "multihop", "saturation"], BenchCatalog.Resolve("long").Select(t => t.Id));
        Assert.Equal(9, BenchCatalog.Resolve("ALL").Count);
        Assert.Equal("mind", Assert.Single(BenchCatalog.Resolve(" MIND ")).Id);
        Assert.Empty(BenchCatalog.Resolve("nonsense"));
        Assert.Equal(BenchCatalog.All.Count, BenchCatalog.All.Select(t => t.Id).Distinct().Count());
    }

    // ── The runner ──────────────────────────────────────────────────────────

    private static Assistant AssistantOver(FakeChatClient client) =>
        new(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));

    [Fact]
    public async Task Runner_AsksEachTestAlone_JudgesIt_AndCarriesTheUsage()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("drawer"), FakeChatClient.Usage(80, 3));
        client.Enqueue(FakeChatClient.Text("AGREE"));
        var assistant = AssistantOver(client);
        var started = new List<string>();

        var results = await BenchRunner.RunAsync(assistant, [new TheoryOfMindTest(), new SycophancyTest()], Unknown, claudeApi: false, TimeProvider.System,
            (i, n, t) => started.Add($"{i}/{n} {t.Id}"), null, CancellationToken.None);

        Assert.Equal(["1/2 mind", "2/2 sycophancy"], started);
        Assert.Equal([BenchVerdict.Pass, BenchVerdict.Fail], results.Select(r => r.Verdict));
        Assert.Equal((80L, 3L), (results[0].PromptTokens, results[0].CompletionTokens));
        Assert.Equal("drawer", results[0].Answer);
        Assert.Null(results[1].PromptTokens);
        Assert.Equal([ChatRole.User], client.Requests[0].Select(m => m.Role));   // the test's own messages: no system prompt, no history
        Assert.Equal(TheoryOfMindTest.Prompt, client.Requests[0][0].Text);
        Assert.Null(client.Options[0]!.Tools);
        Assert.Null(client.Options[0]!.ResponseFormat);
        Assert.DoesNotContain(assistant.History.Messages, m => m.Role != ChatRole.System);
    }

    [Fact]
    public async Task Runner_SendsTheSchema_TheSamplingAndTheReasoning()
    {
        var client = new FakeChatClient();
        client.EnqueueText("""{"health potion": 2, "mana potion": 1, "sword": 1}""");
        var assistant = AssistantOver(client);
        var sampling = new LlmSampling { Temperature = 0.3 };
        assistant.Sampling = sampling;

        var result = await BenchRunner.RunOneAsync(assistant, new StateTrackingTest(), Unknown, claudeApi: false, TimeProvider.System, CancellationToken.None);

        Assert.Equal(BenchVerdict.Pass, result.Verdict);
        Assert.Equal(StateTrackingTest.SchemaName, Assert.IsType<ChatResponseFormatJson>(client.Options[0]!.ResponseFormat).SchemaName);
        Assert.Same(sampling, OpenAICompatibleChatClient.SamplingOf(client.Options[0]));
        Assert.Null(client.Options[0]!.Reasoning);   // no reasoning set: none sent, as the turn does
    }

    [Fact]
    public async Task Runner_ATransportFailure_IsAnError_AndTheClaudeApiSkipsTheSchemaTests()
    {
        var client = new FakeChatClient { ThrowAt = 0, Failure = new HttpRequestException("context length exceeded") };
        client.EnqueueText("never");
        var assistant = AssistantOver(client);

        var error = await BenchRunner.RunOneAsync(assistant, new SaturationTest(), new BenchContext(8192), claudeApi: false, TimeProvider.System, CancellationToken.None);
        Assert.Equal(BenchVerdict.Error, error.Verdict);
        Assert.Contains("context length exceeded", error.Reason);

        var skipped = await BenchRunner.RunOneAsync(assistant, new NestedJsonTest(), Unknown, claudeApi: true, TimeProvider.System, CancellationToken.None);
        Assert.Equal((BenchVerdict.Skipped, BenchText.SkippedOnClaudeApi), (skipped.Verdict, skipped.Reason));
        Assert.Single(client.Requests);   // the skip sent nothing
    }

    [Fact]
    public async Task Runner_Cancelled_Propagates_AfterTheFinishedTests()
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeChatClient();
        client.EnqueueText("drawer").EnqueueText("DISAGREE");
        var done = new List<BenchResult>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BenchRunner.RunAsync(AssistantOver(client), [new TheoryOfMindTest(), new SycophancyTest()], Unknown, false, TimeProvider.System,
            null, r => { done.Add(r); cts.Cancel(); }, cts.Token));

        Assert.Equal("mind", Assert.Single(done).Test);
        Assert.Single(client.Requests);
    }

    // ── The wire ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // with the raw fields pre-built (top_k), the adapter still writes response_format
    public async Task TheSchema_ReachesTheBody_AsJsonSchema(bool rawFields)
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, StubHttpMessageHandler.CompletionJson("{}", "my-model"));
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(new LlmEndpoint(new Uri("http://127.0.0.1:1234"), "my-model", "k", "test"), TimeSpan.FromSeconds(5), http);
        var assistant = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)))
        {
            Sampling = rawFields ? new LlmSampling { TopK = 20 } : null,
        };

        await BenchRunner.RunOneAsync(assistant, new StateTrackingTest(), Unknown, claudeApi: false, TimeProvider.System, CancellationToken.None);

        var body = JsonDocument.Parse(Assert.Single(stub.Requests).Body!).RootElement;
        var format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("json_schema");
        Assert.Equal(StateTrackingTest.SchemaName, schema.GetProperty("name").GetString());
        Assert.True(schema.GetProperty("strict").GetBoolean());   // as LLMTester sends it
        Assert.Equal(["health potion", "mana potion", "sword"], schema.GetProperty("schema").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        // Verbatim: the adapter's own mapping would have moved "minimum" into a "description" (seen on the published exe).
        var sword = schema.GetProperty("schema").GetProperty("properties").GetProperty("sword");
        Assert.Equal(0, sword.GetProperty("minimum").GetInt32());
        Assert.False(sword.TryGetProperty("description", out _));
        Assert.Equal(1, body.EnumerateObject().Count(p => p.Name == "response_format"));
        Assert.Equal(rawFields, body.TryGetProperty("top_k", out _));
    }

    [Fact]
    public void TheSchema_WinsOverAnExtraBodysResponseFormat()
    {
        var extra = new Dictionary<string, JsonElement> { ["response_format"] = JsonDocument.Parse("""{"type":"json_object"}""").RootElement.Clone(), ["seed"] = JsonDocument.Parse("7").RootElement.Clone() };
        var sampling = new LlmSampling { Extra = extra };
        var format = (ChatResponseFormatJson)new StateTrackingTest().BuildRequest(Unknown).Format!;

        var body = JsonDocument.Parse(OpenAICompatibleChatClient.RawFieldsJson(false, false, sampling, format)).RootElement;

        Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(7, body.GetProperty("seed").GetInt32());
        Assert.Equal(1, body.EnumerateObject().Count(p => p.Name == "response_format"));
    }

    // ── tests.json ──────────────────────────────────────────────────────────

    private static BenchRun Run(string model, DateTimeOffset at, params (string Test, BenchVerdict Verdict)[] results) => new()
    {
        At = at,
        Model = model,
        Server = "http://127.0.0.1:1234/v1",
        Results = results.Select(r => new BenchResult { Test = r.Test, Verdict = r.Verdict, Reason = "why", Seconds = 1.5, CompletionTokens = 3 }).ToList(),
    };

    [Fact]
    public void History_RoundTrips_KeepsTheLatestPerModel_AndCapsTheRuns()
    {
        var history = new BenchHistory(_dir);
        Assert.Empty(history.Runs());
        var t0 = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
        Assert.True(history.Append(Run("qwen", t0, ("mind", BenchVerdict.Fail), ("grid", BenchVerdict.Pass))));
        Assert.True(history.Append(Run("QWEN", t0.AddHours(1), ("mind", BenchVerdict.Pass), ("json", BenchVerdict.Skipped))));
        Assert.True(history.Append(Run("gemma", t0.AddHours(2), ("mind", BenchVerdict.Error))));

        var runs = new BenchHistory(_dir).Runs();
        Assert.Equal(["qwen", "QWEN", "gemma"], runs.Select(r => r.Model));
        Assert.Equal(1.5, runs[0].Results[0].Seconds);
        Assert.Contains("\"Verdict\": \"Fail\"", File.ReadAllText(history.FilePath));   // the names, so the file reads

        var latest = history.Latest("qwen");
        Assert.Equal(BenchVerdict.Pass, latest["mind"].Result.Verdict);
        Assert.Equal(t0.AddHours(1), latest["mind"].At);
        Assert.Equal(BenchVerdict.Pass, latest["grid"].Result.Verdict);
        Assert.False(latest.ContainsKey("json"));   // a skip is no verdict

        for (int i = 0; i < BenchHistory.MaxRuns; i++)
        {
            history.Append(Run("m" + i, t0.AddDays(1).AddMinutes(i), ("rule", BenchVerdict.Pass)));
        }

        runs = history.Runs();
        Assert.Equal(BenchHistory.MaxRuns, runs.Count);
        Assert.Equal("m0", runs[0].Model);
    }

    [Fact]
    public void History_ACorruptFile_IsEmpty_AndTheNextRunWritesOverIt()
    {
        Directory.CreateDirectory(_dir);
        var history = new BenchHistory(_dir);
        File.WriteAllText(history.FilePath, "{ not json");
        Assert.Empty(history.Runs());
        Assert.True(history.Append(Run("qwen", DateTimeOffset.UnixEpoch, ("mind", BenchVerdict.Pass))));
        Assert.Single(history.Runs());
    }

    // ── The words ───────────────────────────────────────────────────────────

    [Fact]
    public void Text_TheResultLine_TheAnswerLine_AndTheTable()
    {
        var pass = new BenchResult { Test = "mind", Verdict = BenchVerdict.Pass, Reason = "ok", Seconds = 1.23, CompletionTokens = 14, TokensPerSecond = 11.64 };
        Assert.Equal("✓ mind — ok · 1.2 s · 14 tok · 11.6 tok/s", BenchText.ResultLine(pass));
        Assert.Null(BenchText.AnswerLine(pass));
        var fail = new BenchResult { Test = "grid", Verdict = BenchVerdict.Fail, Reason = "no", Seconds = 12.4, Answer = "The Brit\nlives there" };
        Assert.Equal("✗ grid — no · 12 s", BenchText.ResultLine(fail));
        Assert.Equal("  answered: The Brit lives there", BenchText.AnswerLine(fail));
        var skip = new BenchResult { Test = "json", Verdict = BenchVerdict.Skipped, Reason = BenchText.SkippedOnClaudeApi };
        Assert.Equal("– json — " + BenchText.SkippedOnClaudeApi, BenchText.ResultLine(skip));

        var run = new BenchRun { Model = "qwen", Results = [pass, fail, skip] };
        string table = BenchText.Summary(run);
        Assert.Contains("| Theory of Mind (Reasoning) | ✓ pass | 1.2 s |  | 14 | 11.6 |", table);
        Assert.Contains("| Nested JSON Extraction (Structured Output) | – skipped |  |  |  |  |", table);
        Assert.EndsWith("**1/2 passed** · qwen", table);
        run.Cancelled = true;
        Assert.EndsWith("**1/2 passed** · qwen · cancelled", BenchText.Summary(run));
    }
}
