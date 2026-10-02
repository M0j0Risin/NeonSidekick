using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// Sampling overrides per model (2026-09-28, the user's ask): the resolution (model, then <c>*</c>, then the server's),
/// the parsing and its refusals, the wire (the standard fields through the adapter, top_k, min_p, the repetition penalty
/// under both names and the extra body raw, <c>chat_template_kwargs</c> merged), the variable, the assistant's requests,
/// the Claude API left alone, and the <c>/sampling</c> pane.
/// </summary>
public sealed class LlmSamplingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static AppSettingsData With(params (string Key, LlmSamplingEntry Entry)[] entries) => new()
    {
        LlmSampling = entries.ToDictionary(e => e.Key, e => e.Entry, StringComparer.Ordinal),
    };

    // ── Resolution ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NothingSaved_IsNone()
    {
        Assert.Same(LlmSampling.None, LlmSampling.Resolve(new AppSettingsData(), "qwen3"));
        Assert.True(LlmSampling.None.IsEmpty);
        Assert.False(LlmSampling.None.HasRawFields);
        Assert.Same(LlmSampling.None, LlmSampling.Resolve(With(("gemma", new() { Temperature = 1 })), "qwen3"));   // another model's entry says nothing here
    }

    [Fact]
    public void Resolve_TheModelsValue_ThenAnyModels_FieldByField_IgnoringCase()
    {
        var data = With(
            ("Qwen3-8B", new() { Temperature = 0.6, TopK = 20, Extra = new() { ["typical_p"] = Json("0.9"), ["seed"] = Json("1") } }),
            ("*", new() { Temperature = 0.7, MinP = 0.05, Extra = new() { ["seed"] = Json("42"), ["mirostat"] = Json("0") } }));

        var sampling = LlmSampling.Resolve(data, "qwen3-8b");
        Assert.Equal(0.6, sampling.Temperature);   // the model's own wins
        Assert.Equal(20, sampling.TopK);
        Assert.Equal(0.05, sampling.MinP);         // inherited from *
        Assert.Null(sampling.TopP);                // nobody set it: the server's
        Assert.Equal(1, sampling.Extra["seed"].GetInt32());   // the model's key wins
        Assert.Equal(0.9, sampling.Extra["typical_p"].GetDouble());
        Assert.Equal(0, sampling.Extra["mirostat"].GetInt32());

        var other = LlmSampling.Resolve(data, "gemma-3");
        Assert.Equal(0.7, other.Temperature);
        Assert.Null(other.TopK);
        Assert.Equal(42, other.Extra["seed"].GetInt32());
    }

    [Fact]
    public void Resolve_AHandEditedValueOutOfRange_IsSkipped_NeverClamped()
    {
        var data = With(
            ("m", new() { Temperature = 9, TopP = 0, Extra = new() { ["messages"] = Json("[]"), ["ok"] = Json("true") } }),
            ("*", new() { Temperature = 0.8 }));

        var sampling = LlmSampling.Resolve(data, "m");
        Assert.Equal(0.8, sampling.Temperature);   // the model's 9 is refused, so * stands
        Assert.Null(sampling.TopP);                // 0 is not above 0
        Assert.False(sampling.Extra.ContainsKey("messages"));
        Assert.True(sampling.Extra.ContainsKey("ok"));
    }

    [Fact]
    public void Describe_ListsTheSetFields_InWireNames_ExtraByName()
    {
        var sampling = LlmSampling.Resolve(With(("m", new() { Temperature = 0.6, TopK = 20, RepetitionPenalty = 1.05, Extra = new() { ["typical_p"] = Json("0.9") } })), "m");
        Assert.Equal("temperature 0.6 · top_k 20 · repetition_penalty 1.05 · typical_p", sampling.Describe());
        Assert.Equal("Sampling: temperature 0.6 · top_k 20 · repetition_penalty 1.05 · typical_p", LlmSession.SamplingLogLine(sampling));
        Assert.Equal("", LlmSampling.None.Describe());
    }

    // ── Fields and parsing ──────────────────────────────────────────────────

    [Fact]
    public void Fields_AreTheSevenInThePanesOrder_WithTheirRanges()
    {
        Assert.Equal(["temperature", "top_p", "top_k", "min_p", "presence_penalty", "frequency_penalty", "repetition_penalty"], SamplingField.All.Select(f => f.Wire));
        Assert.Equal("0 to 5", SamplingField.For(SamplingKey.Temperature).RangeText);
        Assert.Equal("above 0, up to 1", SamplingField.For(SamplingKey.TopP).RangeText);
        Assert.Equal("a whole number from -1 to 100000", SamplingField.For(SamplingKey.TopK).RangeText);
        Assert.Equal("-2 to 2", SamplingField.For(SamplingKey.PresencePenalty).RangeText);
        Assert.Same(SamplingField.For(SamplingKey.RepetitionPenalty), SamplingField.ByWire("REPEAT_PENALTY"));   // llama.cpp's name reads as the field
        Assert.Null(SamplingField.ByWire("typical_p"));

        var topK = SamplingField.For(SamplingKey.TopK);
        Assert.True(topK.TryParse("-1", out _));
        Assert.False(topK.TryParse("2.5", out _));
        Assert.True(SamplingField.For(SamplingKey.Temperature).TryParse("0.6", out double t));
        Assert.Equal(0.6, t);
        Assert.False(SamplingField.For(SamplingKey.Temperature).TryParse("0,6", out _));   // invariant culture only
        Assert.False(SamplingField.For(SamplingKey.MinP).TryParse("NaN", out _));
    }

    [Fact]
    public void TryParseEntry_NamedFieldsInRange_TheRestIsTheExtraBody()
    {
        Assert.True(LlmSampling.TryParseEntry("""{"temperature":0.6,"top_k":20,"repeat_penalty":1.1,"top_p":null,"typical_p":0.9}""", out var entry, out _));
        Assert.Equal(0.6, entry.Temperature);
        Assert.Equal(20, entry.TopK);
        Assert.Equal(1.1, entry.RepetitionPenalty);
        Assert.Null(entry.TopP);
        Assert.Equal(0.9, entry.Extra!["typical_p"].GetDouble());

        Assert.False(LlmSampling.TryParseEntry("""{"temperature":7}""", out _, out string range));
        Assert.Equal("temperature must be 0 to 5", range);
        Assert.False(LlmSampling.TryParseEntry("""{"top_k":"20"}""", out _, out _));
        Assert.False(LlmSampling.TryParseEntry("""{"stream":false}""", out _, out string reserved));
        Assert.Equal("stream " + LlmSampling.ReservedProblem, reserved);
        Assert.False(LlmSampling.TryParseEntry("[1]", out _, out string array));
        Assert.Equal(LlmSampling.NotAnObject, array);
        Assert.False(LlmSampling.TryParseEntry("{nope", out _, out string broken));
        Assert.StartsWith("is not JSON", broken, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseExtra_RefusesANamedField_AReservedOne_AndKwargsThatAreNoObject()
    {
        Assert.True(LlmSampling.TryParseExtra("""{"dry_multiplier":0.8,"chat_template_kwargs":{"thinking_budget":512}}""", out var extra, out _));
        Assert.Equal(2, extra!.Count);
        Assert.True(LlmSampling.TryParseExtra("{}", out var none, out _));
        Assert.Null(none);

        Assert.False(LlmSampling.TryParseExtra("""{"temperature":0.5}""", out _, out string named));
        Assert.Equal("temperature is a named field: set temperature itself", named);
        Assert.False(LlmSampling.TryParseExtra("""{"model":"x"}""", out _, out _));
        Assert.False(LlmSampling.TryParseExtra("""{"chat_template_kwargs":true}""", out _, out string kwargs));
        Assert.Equal("chat_template_kwargs " + LlmSampling.TemplateKwargsNotObject, kwargs);
    }

    // ── The wire ────────────────────────────────────────────────────────────

    private static async Task<JsonElement> BodyFor(ChatOptions options)
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, StubHttpMessageHandler.CompletionJson("pong", "my-model"));
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(new LlmEndpoint(new Uri("http://127.0.0.1:1234"), "my-model", "k", "test"), TimeSpan.FromSeconds(5), http);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], options);
        string body = Assert.Single(stub.Requests).Body!;
        Assert.DoesNotContain("neonsidekick.", body);   // the keys never reach the server
        return Json(body);
    }

    private static ChatOptions Options(LlmSampling sampling, ReasoningEffort? effort = null)
    {
        var options = new ChatOptions { Reasoning = effort is { } e ? new ReasoningOptions { Effort = e } : null };
        sampling.ApplyTo(options);
        return options;
    }

    [Fact]
    public async Task EveryField_ReachesTheBody_TheRepetitionPenaltyUnderBothNames()
    {
        var sampling = new LlmSampling { Temperature = 0.6, TopP = 0.95, TopK = 20, MinP = 0.05, PresencePenalty = 0.5, FrequencyPenalty = -0.25, RepetitionPenalty = 1.05 };
        var body = await BodyFor(Options(sampling));

        Assert.Equal(0.6, body.GetProperty("temperature").GetDouble(), 5);
        Assert.Equal(0.95, body.GetProperty("top_p").GetDouble(), 5);
        Assert.Equal(0.5, body.GetProperty("presence_penalty").GetDouble(), 5);
        Assert.Equal(-0.25, body.GetProperty("frequency_penalty").GetDouble(), 5);
        Assert.Equal(20, body.GetProperty("top_k").GetInt32());
        Assert.Equal(0.05, body.GetProperty("min_p").GetDouble());
        Assert.Equal(1.05, body.GetProperty("repetition_penalty").GetDouble());
        Assert.Equal(1.05, body.GetProperty("repeat_penalty").GetDouble());
        Assert.Equal(1, body.EnumerateObject().Count(p => p.Name == "top_k"));   // the adapter writes no top_k of its own
        Assert.False(body.TryGetProperty("chat_template_kwargs", out _));
        Assert.Equal("ping", body.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TheStandardFieldsAlone_NeedNoRawFields()
    {
        var sampling = new LlmSampling { Temperature = 0.2 };
        var options = Options(sampling);
        Assert.Null(OpenAICompatibleChatClient.WithRawFields(options)!.RawRepresentationFactory);

        var body = await BodyFor(options);
        Assert.Equal(0.2, body.GetProperty("temperature").GetDouble(), 5);
        Assert.False(body.TryGetProperty("top_k", out _));
        Assert.False(body.TryGetProperty("repeat_penalty", out _));
    }

    [Fact]
    public async Task TheExtraBody_GoesOutAsItIs_ItsKwargsMergedUnderTheAppsSwitches()
    {
        var sampling = new LlmSampling
        {
            Extra = new Dictionary<string, JsonElement>
            {
                ["dry_multiplier"] = Json("0.8"),
                ["stop"] = Json("""["</s>"]"""),
                ["chat_template_kwargs"] = Json("""{"thinking_budget":512,"enable_thinking":true}"""),
            },
        };
        var body = await BodyFor(Options(sampling, ReasoningEffort.None));

        Assert.Equal(0.8, body.GetProperty("dry_multiplier").GetDouble());
        Assert.Equal("</s>", body.GetProperty("stop")[0].GetString());
        var kwargs = body.GetProperty("chat_template_kwargs");
        Assert.Equal(512, kwargs.GetProperty("thinking_budget").GetInt32());
        Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());   // reasoning none wins over the extra body's switch
        Assert.Equal(2, kwargs.EnumerateObject().Count());
        Assert.Equal("none", body.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void RawFieldsJson_WithoutSampling_IsTheTemplateSwitchesAlone()
    {
        Assert.Equal("""{"chat_template_kwargs":{"enable_thinking":false}}""", System.Text.Encoding.UTF8.GetString(OpenAICompatibleChatClient.RawFieldsJson(true, false, null)));
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(OpenAICompatibleChatClient.RawFieldsJson(false, false, LlmSampling.None)));
    }

    [Fact]
    public void ApplyTo_None_LeavesTheOptionsAlone()
    {
        var options = new ChatOptions();
        LlmSampling.None.ApplyTo(options);
        Assert.Null(options.AdditionalProperties);
        Assert.Null(options.Temperature);
    }

    [Fact]
    public void TheClaudeApi_IsNotAffected()
    {
        var options = Options(new LlmSampling { Temperature = 0.3, TopK = 5, Extra = new Dictionary<string, JsonElement> { ["typical_p"] = Json("0.9") } });
        string json = System.Text.Encoding.UTF8.GetString(AnthropicRequest.Write([new ChatMessage(ChatRole.User, "hi")], options, "claude-sonnet-5", 4096, false).ToArray());
        Assert.DoesNotContain("temperature", json);
        Assert.DoesNotContain("top_k", json);
        Assert.DoesNotContain("typical_p", json);
    }

    // ── The assistant ───────────────────────────────────────────────────────

    [Fact]
    public async Task EveryRequestOfTheAssistant_CarriesItsSampling()
    {
        var client = new FakeChatClient();
        var assistant = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
        client.Enqueue(FakeChatClient.Text("one"));
        await foreach (var _ in assistant.RunTurnAsync("q")) { }
        Assert.Null(client.Options[^1]!.Temperature);

        var sampling = new LlmSampling { Temperature = 0.4, MinP = 0.1 };
        assistant.Sampling = sampling;
        client.Enqueue(FakeChatClient.Text("two"));
        await foreach (var _ in assistant.RunTurnAsync("q2")) { }
        Assert.Equal(0.4f, client.Options[^1]!.Temperature);
        Assert.Same(sampling, OpenAICompatibleChatClient.SamplingOf(client.Options[^1]));

        client.Enqueue(FakeChatClient.Text("side"));
        await assistant.RequestAsync([new ChatMessage(ChatRole.User, "go")], [], ReasoningEffort.None, CancellationToken.None);
        Assert.Same(sampling, OpenAICompatibleChatClient.SamplingOf(client.Options[^1]));

        client.Enqueue(FakeChatClient.Text("A summary."));
        await assistant.SummarizeAsync([new ChatMessage(ChatRole.User, "long talk")], null, CancellationToken.None);
        Assert.Same(sampling, OpenAICompatibleChatClient.SamplingOf(client.Options[^1]));
    }

    // ── The variable ────────────────────────────────────────────────────────

    [Fact]
    public void TheVariable_OverlaysEveryEntryAndAnyModel_TheSavedRestKept()
    {
        var env = new EnvironmentOverrides(name => name == EnvironmentOverrides.LlmSamplingVariable ? """{"temperature":0.2,"seed":7}""" : null);
        var saved = With(("qwen3", new() { Temperature = 0.6, TopK = 20 }));

        var effective = env.ApplyTo(saved);
        Assert.Equal(0.2, LlmSampling.Resolve(effective, "qwen3").Temperature);
        Assert.Equal(20, LlmSampling.Resolve(effective, "qwen3").TopK);
        Assert.Equal(0.2, LlmSampling.Resolve(effective, "anything").Temperature);
        Assert.Equal(7, LlmSampling.Resolve(effective, "anything").Extra["seed"].GetInt32());
        Assert.Equal(0.6, saved.LlmSampling!["qwen3"].Temperature);   // the saved snapshot is not touched
        Assert.Contains(EnvironmentOverrides.LlmSamplingVariable, env.ActiveVariables());

        var bad = new EnvironmentOverrides(name => name == EnvironmentOverrides.LlmSamplingVariable ? """{"temperature":99}""" : null);
        Assert.Null(bad.LlmSampling);
        Assert.Empty(bad.ActiveVariables());
        Assert.Null(bad.ApplyTo(new AppSettingsData()).LlmSampling);
    }

    // ── The data and the pane ───────────────────────────────────────────────

    [Fact]
    public void Mutate_ReusesTheStoredSpelling_AndDropsAnEntryThatSaysNothing()
    {
        var data = With(("Qwen3", new() { Temperature = 0.6 }));
        SamplingMenu.Mutate(data, "qwen3", e => e.TopK = 20);
        Assert.Equal(["Qwen3"], data.LlmSampling!.Keys);
        Assert.Equal(20, data.LlmSampling["Qwen3"].TopK);

        SamplingMenu.Mutate(data, "QWEN3", e => { e.Temperature = null; e.TopK = null; });
        Assert.Null(data.LlmSampling);
    }

    [Fact]
    public void Copy_IsDeep()
    {
        var source = With(("m", new() { Temperature = 0.5, Extra = new() { ["a"] = Json("1") } }));
        var copy = AppSettings.Copy(source);
        copy.LlmSampling!["m"].Temperature = 1;
        copy.LlmSampling["m"].Extra!["b"] = Json("2");
        Assert.Equal(0.5, source.LlmSampling!["m"].Temperature);
        Assert.Single(source.LlmSampling["m"].Extra!);
    }

    [Fact]
    public void TabKeys_TheConnectedModelFirst_ThenAnyModel_ThenTheRestAToZ()
    {
        var map = With(("zeta", new() { TopK = 1 }), ("*", new() { TopK = 2 }), ("Alpha", new() { TopK = 3 }), ("qwen3", new() { TopK = 4 })).LlmSampling;
        Assert.Equal(["qwen3", "*", "Alpha", "zeta"], SamplingMenu.TabKeys(map, "QWEN3"));
        Assert.Equal(["new-model", "*", "Alpha", "qwen3", "zeta"], SamplingMenu.TabKeys(map, "new-model"));
        Assert.Equal(["*"], SamplingMenu.TabKeys(null, null));
        Assert.Equal(SamplingText.AnyModelTabTitle, SamplingText.TabTitle("*"));
    }

    [Fact]
    public void Summary_NamesTheModelsWithOverrides_AnyModelFirst()
    {
        Assert.Equal(SamplingText.ServerDefaults, SamplingText.Summary(null));
        Assert.Equal("*, gemma, qwen3", SamplingText.Summary(With(("qwen3", new() { TopK = 1 }), ("gemma", new() { TopK = 1 }), ("*", new() { TopK = 1 }), ("empty", new())).LlmSampling));
        Assert.Equal("*, gemma, qwen3", SettingsMenu.FieldValue(SettingsField.LlmSampling, With(("qwen3", new() { TopK = 1 }), ("gemma", new() { TopK = 1 }), ("*", new() { TopK = 1 })), ""));
        Assert.Equal("LLM sampling", SettingsMenu.FieldName(SettingsField.LlmSampling));
        Assert.Contains(SettingsField.LlmSampling, SettingsMenu.TabFields[(int)SettingsTab.Llm]);
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.LlmSampling));
    }

    [Fact]
    public void Lines_ShowOwnInheritedAndDefaultValues()
    {
        var map = With(("qwen3", new() { Temperature = 0.6 }), ("*", new() { Temperature = 0.7, TopP = 0.9, Extra = new() { ["seed"] = Json("1") } })).LlmSampling;
        var lines = SamplingMenu.Lines(map, "qwen3").ToList();
        int w = SamplingText.LabelWidth;
        Assert.Equal("qwen3", lines[0]);
        Assert.Equal("  " + "temperature".PadRight(w) + "0.6", lines[1]);
        Assert.Equal("  " + "top_p".PadRight(w) + "0.9 (from *)", lines[2]);
        Assert.Equal("  " + "top_k".PadRight(w) + SamplingText.ServerDefault, lines[3]);
        Assert.Equal("  " + SamplingText.ExtraRowName.PadRight(w) + """{"seed":1} (from *)""", lines[8]);
        Assert.Equal(SamplingText.AnyModelTabTitle, lines[9]);
        Assert.Equal("  " + "temperature".PadRight(w) + "0.7", lines[10]);
    }

    // ── The pane, scripted ──────────────────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public readonly TestConsole Console = new TestConsole().Interactive();
        public readonly AppSettings Settings;
        public readonly SpeechSession Speech;
        public readonly ScreenPane Pane;
        public readonly SamplingMenu Menu;
        public int Refreshed;

        public Rig(string dir, LlmEndpoint? endpoint, string? overriddenBy = null)
        {
            Console.Profile.Width = 120;
            Console.Profile.Height = 40;
            Settings = new AppSettings(dir);
            Speech = new SpeechSession(_ => new FakeSynthesizer(), _ => new FakeAudioPlayback(), new ModelStore(Path.Combine(dir, "models"), new HttpClient(new StubHttpMessageHandler())));
            Pane = new ScreenPane(Console, new ScreenGeometry(() => null), new ManualTimeProvider()) { Hint = () => "idle" };
            var keys = new KeySource(Console.Input, TimeSpan.FromMilliseconds(1));
            var menuPane = new MenuPane(Pane, keys);
            var input = new InputLine(Pane, keys);
            var settings = new SettingsMenu(new ConsoleWithInput(Pane, keys), Settings, _ => null, input, new TranscriptRenderer(Pane), Speech, menuPane, _ => null);
            Menu = new SamplingMenu(Settings, settings, new TranscriptRenderer(Pane), menuPane, input, () => endpoint, () => overriddenBy, () => Refreshed++);
            Pane.Show();
        }

        public void Push(params ConsoleKeyInfo[] keys)
        {
            foreach (var key in keys)
            {
                Console.Input.PushKey(key);
            }
        }

        public void Type(string text)
        {
            foreach (char c in text)
            {
                Push(Keys.Char(c));
            }
        }

        public void Dispose()
        {
            Pane.Dispose();
            Speech.Dispose();
            Settings.Dispose();
            Console.Dispose();
        }
    }

    private static readonly LlmEndpoint Qwen = new(new Uri("http://127.0.0.1:8000/v1"), "qwen3-8b", "k", "configured");

    [Fact]
    public async Task OnThePane_EnterTypesAValue_ForTheConnectedModel_ABadOneRefused_BlankClears()
    {
        using var rig = new Rig(_dir, Qwen);
        rig.Push(Keys.Enter);                  // temperature, empty
        rig.Type("9");
        rig.Push(Keys.Enter);                  // refused
        rig.Push(Keys.Enter);
        rig.Type("0.6");
        rig.Push(Keys.Enter);                  // saved
        rig.Push(Keys.Down, Keys.Down, Keys.Enter);
        rig.Type("20");
        rig.Push(Keys.Enter);                  // top_k
        rig.Push(Keys.Up, Keys.Up, Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Enter);   // temperature blank: cleared
        rig.Push(Keys.Escape);

        await rig.Menu.ShowAsync(CancellationToken.None);

        var entry = Assert.Single(rig.Settings.Current.LlmSampling!);
        Assert.Equal("qwen3-8b", entry.Key);
        Assert.Null(entry.Value.Temperature);
        Assert.Equal(20, entry.Value.TopK);
        Assert.Contains(SamplingText.RangeError(SamplingField.For(SamplingKey.Temperature), SamplingText.ServerDefault), rig.Console.Output);
        Assert.Contains(SamplingText.SavedNotice("qwen3-8b", "temperature", "0.6"), rig.Console.Output);
        Assert.Contains(SamplingText.ClearedNotice("qwen3-8b", "temperature"), rig.Console.Output);
        Assert.Equal(3, rig.Refreshed);
    }

    [Fact]
    public async Task OnThePane_TheAnyModelTab_AndTheExtraBody_AndClearAfterAYes()
    {
        using var rig = new Rig(_dir, Qwen);
        rig.Push(Keys.Right);                                      // any model (*)
        for (int i = 0; i < SamplingMenu.ExtraRow; i++)
        {
            rig.Push(Keys.Down);
        }

        rig.Push(Keys.Enter);
        rig.Type("""{"model":"x"}""");
        rig.Push(Keys.Enter);                                      // refused: reserved
        rig.Push(Keys.Enter);
        rig.Type("""{"seed":7}""");
        rig.Push(Keys.Enter);                                      // saved
        rig.Push(Keys.Down, Keys.Enter, Keys.Char('y'), Keys.Enter);   // clear, yes
        rig.Push(Keys.Escape);

        await rig.Menu.ShowAsync(CancellationToken.None);

        Assert.Null(rig.Settings.Current.LlmSampling);
        Assert.Contains(SamplingText.SavedNotice("*", SamplingText.ExtraRowName, """{"seed":7}"""), rig.Console.Output);
        Assert.Contains(SamplingText.ClearedAllNotice("*"), rig.Console.Output);
        Assert.Contains(SamplingText.AnyModelTabTitle, rig.Console.Output);
    }

    [Fact]
    public void Quick_SetsTheConnectedModels_ClearsAField_OrTheWholeEntry()
    {
        using var rig = new Rig(_dir, Qwen, overriddenBy: EnvironmentOverrides.LlmSamplingVariable);
        Assert.True(rig.Menu.Quick("temperature 0.6"));
        Assert.True(rig.Menu.Quick("REPEAT_PENALTY 1.1"));
        Assert.True(rig.Menu.Quick("extra {\"seed\":3}"));
        var entry = rig.Settings.Current.LlmSampling!["qwen3-8b"];
        Assert.Equal(0.6, entry.Temperature);
        Assert.Equal(1.1, entry.RepetitionPenalty);
        Assert.Equal(3, entry.Extra!["seed"].GetInt32());
        Assert.Contains(SettingsMenu.OverrideNotice(EnvironmentOverrides.LlmSamplingVariable), rig.Console.Output);

        Assert.True(rig.Menu.Quick("temperature clear"));
        Assert.Null(rig.Settings.Current.LlmSampling!["qwen3-8b"].Temperature);
        Assert.False(rig.Menu.Quick("top_p 2"));
        Assert.False(rig.Menu.Quick("typical_p 0.9"));
        Assert.False(rig.Menu.Quick("temperature"));
        Assert.Contains(SamplingText.UsageError[..40], rig.Console.Output);   // the line wraps at the fixture's width

        Assert.True(rig.Menu.Quick("clear"));
        Assert.Null(rig.Settings.Current.LlmSampling);
    }

    [Fact]
    public void Quick_WithNothingConnected_SaysSo()
    {
        using var rig = new Rig(_dir, endpoint: null);
        Assert.False(rig.Menu.Quick("temperature 0.6"));
        Assert.Contains(SamplingText.NoModelError, rig.Console.Output);
        Assert.Null(rig.Settings.Current.LlmSampling);
    }

    [Fact]
    public void Sampling_IsASlashCommand_QuickWithArguments_APaneWithout()
    {
        Assert.Equal((SlashCommand.Sampling, "temperature 0.6"), SlashCommands.Parse("/sampling temperature 0.6"));
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.Sampling, hasArgs: true));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Sampling, hasArgs: false));
        Assert.Contains("/sampling", SlashCommands.Words);
    }
}
