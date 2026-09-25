using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class AskUserToolTests
{
    private readonly List<IReadOnlyList<AskQuestion>> _asked = new();
    private IReadOnlyList<AskAnswer>? _answers;
    // The settings the caps are read from at every use (the Ask tab's two rows), fresh-profile values.
    private readonly AppSettingsData _effective = new();
    private readonly AskUserTool _tool;

    public AskUserToolTests()
    {
        _tool = new AskUserTool((questions, _) =>
        {
            _asked.Add(questions);
            return Task.FromResult(_answers);
        }, () => _effective);
    }

    private const string TwoQuestions = """
        [
          { "question": "Which colour?", "title": "Colour", "options": ["red", "blue"] },
          { "question": "Toppings?", "type": "multi", "options": ["cheese", "olives", "ham"] }
        ]
        """;

    private static AIFunctionArguments Args(object? questions) =>
        new(new Dictionary<string, object?> { [AskUserTool.QuestionsArgument] = questions });

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private Task<object?> InvokeAsync(string questionsJson) => _tool.InvokeAsync(Args(Json(questionsJson)), CancellationToken.None).AsTask();

    [Fact]
    public void Name_Description_AndSchema_ArePinned()
    {
        Assert.Equal("ask_user", AskUserTool.ToolName);
        Assert.Equal(AskUserTool.ToolName, _tool.Name);
        Assert.Contains("Asks the user 1 to 10 multiple-choice questions", _tool.Description);
        Assert.Contains("Each question has 2 to 10 short options", _tool.Description);
        Assert.Contains("not answered", _tool.Description);
        Assert.Equal(2, AskUserTool.MinOptions);
        Assert.Equal(new AskLimits(10, 10), AskLimits.Default);
        Assert.Equal(AskLimits.Default, _tool.Limits);

        var schema = _tool.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(new[] { "questions" }, schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
        var questions = schema.GetProperty("properties").GetProperty("questions");
        Assert.Equal("array", questions.GetProperty("type").GetString());
        Assert.Equal(1, questions.GetProperty("minItems").GetInt32());
        Assert.Equal(10, questions.GetProperty("maxItems").GetInt32());
        Assert.Equal("1 to 10 questions, asked together on one screen.", questions.GetProperty("description").GetString());
        var item = questions.GetProperty("items");
        Assert.Equal("object", item.GetProperty("type").GetString());
        Assert.Equal(new[] { "question", "options" }, item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
        var properties = item.GetProperty("properties");
        Assert.Equal(new[] { "question", "title", "type", "options" }, properties.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "single", "multi" }, properties.GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
        var options = properties.GetProperty("options");
        Assert.Equal("array", options.GetProperty("type").GetString());
        Assert.Equal("string", options.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(2, options.GetProperty("minItems").GetInt32());
        Assert.Equal(10, options.GetProperty("maxItems").GetInt32());
        Assert.Equal("2 to 10 short answers to pick from. The user can always type another.", options.GetProperty("description").GetString());
    }

    [Fact]
    public void Limits_FollowTheSettings_ClampedToTheRange_AndTheSchemaAndDescriptionQuoteThem()
    {
        // The caps the Ask tab holds reach the schema, the description and the limits at the next read: no reconnect, no new tool.
        _effective.AskMaxQuestions = 3;
        _effective.AskMaxChoices = 15;
        Assert.Equal(new AskLimits(3, 15), _tool.Limits);
        Assert.Contains("1 to 3 multiple-choice questions", _tool.Description);
        Assert.Contains("2 to 15 short options", _tool.Description);
        var schema = _tool.JsonSchema;
        var questions = schema.GetProperty("properties").GetProperty("questions");
        Assert.Equal(3, questions.GetProperty("maxItems").GetInt32());
        Assert.Equal("1 to 3 questions, asked together on one screen.", questions.GetProperty("description").GetString());
        var options = questions.GetProperty("items").GetProperty("properties").GetProperty("options");
        Assert.Equal(15, options.GetProperty("maxItems").GetInt32());
        Assert.Equal("2 to 15 short answers to pick from. The user can always type another.", options.GetProperty("description").GetString());
        Assert.Equal(schema.GetRawText(), _tool.JsonSchema.GetRawText());

        // A hand-edited profile.json past the range is clamped, never refused.
        _effective.AskMaxQuestions = 0;
        _effective.AskMaxChoices = 99;
        Assert.Equal(new AskLimits(AppSettingsData.MinAskMaxQuestions, AppSettingsData.MaxAskMaxChoices), _tool.Limits);
        _effective.AskMaxQuestions = 99;
        _effective.AskMaxChoices = 1;
        Assert.Equal(new AskLimits(AppSettingsData.MaxAskMaxQuestions, AppSettingsData.MinAskMaxChoices), _tool.Limits);
        Assert.Equal(10, _tool.JsonSchema.GetProperty("properties").GetProperty("questions").GetProperty("maxItems").GetInt32());
        Assert.Equal(AskLimits.From(_effective), _tool.Limits);
    }

    [Fact]
    public async Task Invoke_TheCapsAreTheSettings_NotTheDefaults()
    {
        _effective.AskMaxQuestions = 1;
        _effective.AskMaxChoices = 2;
        Assert.Equal("Error: at most 1 questions per call; ask the most important ones first.", await InvokeAsync(TwoQuestions));
        Assert.Equal("Error: question 1 has 3 options; give 2 to 2.", await InvokeAsync("""[{ "question": "Toppings?", "options": ["cheese", "olives", "ham"] }]"""));
        Assert.Equal("Error: question 1: options must be a list of 2 to 2 short strings.", await InvokeAsync("""[{ "question": "Toppings?" }]"""));
        Assert.Empty(_asked);

        _effective.AskMaxChoices = 3;
        _answers = [];
        await InvokeAsync("""[{ "question": "Toppings?", "options": ["cheese", "olives", "ham"] }]""");
        Assert.Single(_asked);
    }

    [Fact]
    public async Task Invoke_HandsTheQuestionsToTheSeam_AndAnswersOneLinePerQuestion()
    {
        var colour = new AskQuestion("Which colour?", "Colour", false, ["red", "blue"]);
        var toppings = new AskQuestion("Toppings?", null, true, ["cheese", "olives", "ham"]);
        _answers = [new AskAnswer(colour, ["blue"]), new AskAnswer(toppings, ["cheese", "olives"])];

        object? answer = await InvokeAsync(TwoQuestions);

        Assert.Equal("Which colour? — blue\nToppings? — cheese, olives", answer);
        // A record over a list compares by reference: the fields are compared flat.
        var asked = Assert.Single(_asked);
        static (string, string?, bool, string) Flat(AskQuestion q) => (q.Question, q.Title, q.Multi, string.Join("|", q.Options));
        Assert.Equal(new[] { Flat(colour), Flat(toppings) }, asked.Select(Flat).ToArray());
    }

    [Fact]
    public async Task Invoke_NullFromTheSeam_IsNotAnswered()
    {
        _answers = null;
        Assert.Equal(AskUserText.NotAnswered, await InvokeAsync(TwoQuestions));
        Assert.Single(_asked);
    }

    [Fact]
    public async Task Invoke_ACancelledSeam_Propagates()
    {
        var tool = new AskUserTool((_, ct) => Task.FromCanceled<IReadOnlyList<AskAnswer>?>(ct), () => _effective);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeAsync(Args(Json(TwoQuestions)), cts.Token).AsTask());
    }

    [Fact]
    public async Task Invoke_TypeDefaultsToSingle_IsCaseInsensitive_TextsTrimmedAndFlattened_TitleBlankIsNone()
    {
        _answers = [];
        await InvokeAsync("""
            [
              { "question": "  Which\ncolour? ", "title": "  ", "type": " MULTI ", "options": [" red ", "light\r\nblue"] },
              { "question": "Size?", "options": ["s", "m"] }
            ]
            """);

        var asked = Assert.Single(_asked);
        Assert.Equal("Which colour?", asked[0].Question);
        Assert.Null(asked[0].Title);
        Assert.True(asked[0].Multi);
        Assert.Equal(["red", "light blue"], asked[0].Options);
        Assert.False(asked[1].Multi);
        Assert.Null(asked[1].Title);
    }

    [Fact]
    public async Task Invoke_ADoubleEncodedList_IsRead()
    {
        _answers = [];
        await _tool.InvokeAsync(Args(Json("\"[{\\\"question\\\":\\\"Q?\\\",\\\"options\\\":[\\\"a\\\",\\\"b\\\"]}]\"")), CancellationToken.None);
        Assert.Equal("Q?", Assert.Single(_asked).Single().Question);
    }

    [Theory]
    [InlineData("missing", "Error: questions must be a list of 1 to 10 objects like [{\"question\": \"…\", \"type\": \"single\", \"options\": [\"a\", \"b\"]}]; got: (nothing)")]
    [InlineData("[]", "Error: questions must be a list of 1 to 10 objects like [{\"question\": \"…\", \"type\": \"single\", \"options\": [\"a\", \"b\"]}]; got: []")]
    [InlineData("\"x\"", "Error: questions must be a list of 1 to 10 objects like [{\"question\": \"…\", \"type\": \"single\", \"options\": [\"a\", \"b\"]}]; got: x")]
    [InlineData("{}", "Error: question 1 has no question text.")]
    [InlineData("[1]", "Error: questions must be a list of 1 to 10 objects like [{\"question\": \"…\", \"type\": \"single\", \"options\": [\"a\", \"b\"]}]; got: [1]")]
    [InlineData("7", "Error: questions must be a list of 1 to 10 objects like [{\"question\": \"…\", \"type\": \"single\", \"options\": [\"a\", \"b\"]}]; got: 7")]
    [InlineData("[{\"question\":\"a\",\"options\":[\"x\",\"y\"]},{\"question\":\"b\",\"options\":[\"x\",\"y\"]},{\"question\":\"c\",\"options\":[\"x\",\"y\"]},{\"question\":\"d\",\"options\":[\"x\",\"y\"]},{\"question\":\"e\",\"options\":[\"x\",\"y\"]},{\"question\":\"f\",\"options\":[\"x\",\"y\"]},{\"question\":\"g\",\"options\":[\"x\",\"y\"]},{\"question\":\"h\",\"options\":[\"x\",\"y\"]},{\"question\":\"i\",\"options\":[\"x\",\"y\"]},{\"question\":\"j\",\"options\":[\"x\",\"y\"]},{\"question\":\"k\",\"options\":[\"x\",\"y\"]}]", "Error: at most 10 questions per call; ask the most important ones first.")]
    [InlineData("[{\"question\":\"a\",\"options\":[\"x\",\"y\"]},{\"options\":[\"x\",\"y\"]}]", "Error: question 2 has no question text.")]
    [InlineData("[{\"question\":\" \",\"options\":[\"x\",\"y\"]}]", "Error: question 1 has no question text.")]
    [InlineData("[{\"question\":\"a\",\"type\":\"either\",\"options\":[\"x\",\"y\"]}]", "Error: question 1: type must be single or multi, not 'either'.")]
    [InlineData("[{\"question\":\"a\"}]", "Error: question 1: options must be a list of 2 to 10 short strings.")]
    [InlineData("[{\"question\":\"a\",\"options\":\"x\"}]", "Error: question 1: options must be a list of 2 to 10 short strings.")]
    [InlineData("[{\"question\":\"a\",\"options\":[\"x\", {\"v\": 2}]}]", "Error: question 1: options must be a list of 2 to 10 short strings.")]
    [InlineData("[{\"question\":\"a\",\"options\":[\"x\", \" \"]}]", "Error: question 1: options must be a list of 2 to 10 short strings.")]
    [InlineData("[{\"question\":\"a\",\"options\":[\"x\"]}]", "Error: question 1 has 1 options; give 2 to 10.")]
    [InlineData("[{\"question\":\"a\",\"options\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\",\"8\",\"9\",\"10\",\"11\"]}]", "Error: question 1 has 11 options; give 2 to 10.")]
    public async Task Invoke_AModelsMistake_IsAnErrorSentence_AndNothingIsAsked(string questions, string expected)
    {
        object? answer = questions == "missing"
            ? await _tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None)
            : await InvokeAsync(questions);

        Assert.Equal(expected, answer);
        Assert.Empty(_asked);
    }

    // ── The lenient shapes (2026-09-15: Qwen3 on SGLang missed the strict one on every first call) ──

    [Fact]
    public async Task Invoke_ALoneObject_IsAListOfOne()
    {
        _answers = [];
        await InvokeAsync("""{ "question": "Q?", "options": ["a", "b"] }""");
        Assert.Equal("Q?", Assert.Single(_asked).Single().Question);
    }

    [Fact]
    public async Task Invoke_TheFieldsAtTheTopLevel_AreOneQuestion()
    {
        _answers = [];
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["question"] = Json("\"Which colour?\""),
            ["type"] = Json("\"multi\""),
            ["options"] = Json("[\"red\", \"blue\"]"),
        });
        await _tool.InvokeAsync(arguments, CancellationToken.None);
        var asked = Assert.Single(_asked).Single();
        Assert.Equal(("Which colour?", true, "red|blue"), (asked.Question, asked.Multi, string.Join("|", asked.Options)));

        // Neither questions nor question: the sentence names the keys that were sent.
        _asked.Clear();
        object? answer = await _tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["prompts"] = Json("[]"), ["count"] = Json("2") }), CancellationToken.None);
        Assert.Equal(AskUserText.NoQuestions("no questions key; the arguments were prompts, count", 10), answer);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Invoke_TheAlternativeKeys_AndAStringHoldingTheOptions_AreRead()
    {
        _answers = [];
        await InvokeAsync("""
            [
              { "text": "Colour?", "multi": true, "choices": ["red", "blue"] },
              { "question": "Size?", "multiple": "true", "options": "['s', 'm', 'l']" },
              { "question": "How many?", "options": [1, 2, true] }
            ]
            """);
        var asked = Assert.Single(_asked);
        Assert.Equal(("Colour?", true, "red|blue"), (asked[0].Question, asked[0].Multi, string.Join("|", asked[0].Options)));
        Assert.Equal(("Size?", true, "s|m|l"), (asked[1].Question, asked[1].Multi, string.Join("|", asked[1].Options)));
        Assert.Equal(("How many?", false, "1|2|true"), (asked[2].Question, asked[2].Multi, string.Join("|", asked[2].Options)));
    }

    [Fact]
    public async Task Invoke_APythonStyleListInAString_IsRead()
    {
        _answers = [];
        await _tool.InvokeAsync(Args("[{'question': 'It\\'s which?', 'type': 'single', 'options': ['a', \"b\"],}]"), CancellationToken.None);
        var asked = Assert.Single(_asked).Single();
        Assert.Equal(("It's which?", false, "a|b"), (asked.Question, asked.Multi, string.Join("|", asked.Options)));
    }

    [Fact]
    public void Description_ShowsTheShape()
    {
        Assert.Contains("Call it as {\"questions\": [{\"question\": \"Which colour?\", \"type\": \"single\", \"options\": [\"red\", \"blue\"]}]} — questions is always a list, even for one question.", _tool.Description);
    }

    [Fact]
    public void Text_IsPinned()
    {
        Assert.Equal("Error: questions must be a list of 1 to 5 objects like [{\"question\": \"…\", \"type\": \"single\", \"options\": [\"a\", \"b\"]}]; got: (nothing)", AskUserText.NoQuestions("  ", 5));
        Assert.Equal("Error: questions must be a list of 1 to 10 objects like " + AskUserText.Shape + "; got: a b", AskUserText.NoQuestions("a\r\nb", 10));
        // A long text is cut in the middle (2026-09-16): the tail shows too, where a parser's stray brace sits.
        string cut = AskUserText.NoQuestions(new string('x', 100) + new string('y', 100), 10);
        Assert.EndsWith("; got: " + new string('x', 59) + "…" + new string('y', 60), cut);
        Assert.Equal(120, cut[(cut.IndexOf("; got: ", StringComparison.Ordinal) + 7)..].Length);
        Assert.Equal(120, AskUserText.RawCells);
        Assert.Equal("(nothing)", AskUserText.SentKeys([]));
        Assert.Equal("no questions key; the arguments were a, b", AskUserText.SentKeys(["a", "b"]));
        Assert.Equal("cheese, olives", AskUserText.AnswerText(["cheese", "olives"]));
        Assert.Equal("Which colour? — blue", AskUserText.AnswerLine("Which colour?", "blue"));
        var q = new AskQuestion("Q?", null, false, ["a", "b"]);
        Assert.Equal("Q? — a\nQ? — b", AskUserText.ResultText([new AskAnswer(q, ["a"]), new AskAnswer(q, ["b"])]));
        Assert.Equal("Error: question 3: type must be single or multi, not 'x'.", AskUserText.BadType(3, " x "));
        Assert.Equal("Error: question 2 has 12 options; give 2 to 10.", AskUserText.OptionCount(2, 12, 10));
        Assert.Equal("Error: question 2 has 16 options; give 2 to 15.", AskUserText.OptionCount(2, 16, 15));
        Assert.Equal("Error: at most 3 questions per call; ask the most important ones first.", AskUserText.TooManyQuestions(3));
        Assert.Equal("Error: question 4: options must be a list of 2 to 7 short strings.", AskUserText.BadOptions(4, 7));
    }
}
