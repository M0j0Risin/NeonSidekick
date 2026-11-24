using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class AskUserToolTests
{
    private readonly List<IReadOnlyList<AskQuestion>> _asked = new();
    private AskReply? _reply;
    // The settings the caps are read from at every use (the Ask tab's two rows), fresh-profile values.
    private readonly AppSettingsData _effective = new();
    private readonly AskUserTool _tool;

    public AskUserToolTests()
    {
        _tool = new AskUserTool((questions, _) =>
        {
            _asked.Add(questions);
            return Task.FromResult(_reply);
        }, () => _effective);
    }

    private const string TwoQuestions = """
        [
          { "question": "Which color?", "title": "Color", "options": ["red", "blue"] },
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

    private Task<object?> InvokeAsync(string questionsJson, CancellationToken cancellationToken = default) =>
        _tool.InvokeAsync(Args(Json(questionsJson)), cancellationToken).AsTask();

    private static string Labels(AskQuestion q) => string.Join("|", q.Options.Select(o => o.Label));

    /// <summary>
    /// A question's field beside a non-empty questions list would be skipped (2026-11-15, the tool-arguments review's #110: a top-level
    /// multiple: true ran single-choice), so the call is refused and nothing asked; an empty value beside it is harmless.
    /// </summary>
    [Fact]
    public async Task AQuestionsFieldBesideTheList_IsRefused_NothingAsked()
    {
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            [AskUserTool.QuestionsArgument] = Json(TwoQuestions),
            [AskUserTool.MultipleArgument] = true,
            [AskUserTool.TitleArgument] = "",
        });
        string answer = (string)(await _tool.InvokeAsync(arguments, CancellationToken.None))!;
        Assert.StartsWith("Error: ", answer, StringComparison.Ordinal);
        Assert.Contains("'multiple'", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("'title'", answer, StringComparison.Ordinal);
        Assert.Empty(_asked);
    }

    [Fact]
    public void Name_Description_AndSchema_ArePinned()
    {
        Assert.Equal("ask_user", AskUserTool.ToolName);
        Assert.Equal(AskUserTool.ToolName, _tool.Name);
        Assert.Equal(
            "Asks the user 1 to 10 questions on screen and waits for the answers. " +
            "Use it when you need a decision, a preference or a clarification before going on. " +
            "A question is single (one answer, the default), multi (one or more) or text (a free answer, no options). Single and multi take 2 to 10 options, " +
            "each a short string or {\"label\": \"…\", \"description\": \"one short sentence\", \"recommended\": true} (at most one recommended on a single); the user can also type their own answer. " +
            "Call it as {\"questions\": [{\"question\": \"Which color?\", \"type\": \"single\", \"options\": [\"red\", \"blue\"]}]} — questions is always a list, even for one question. " +
            "In the result, (typed) marks the user's own words and (skipped) means they left that question to you: decide it yourself. " +
            "If the result says the questions were not answered, the user declined: go on without them, act on their note if there is one, and do not ask again unless asked.",
            _tool.Description);
        Assert.Equal(2, AskUserTool.MinOptions);
        Assert.Equal(new AskLimits(10, 10), AskLimits.Default);
        Assert.Equal(AskLimits.Default, _tool.Limits);
        Assert.Equal(("single", "multi", "text"), (AskUserTool.SingleType, AskUserTool.MultiType, AskUserTool.TextType));
        Assert.Equal(("label", "description", "recommended"), (AskUserTool.LabelArgument, AskUserTool.DescriptionArgument, AskUserTool.RecommendedArgument));

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
        // Options are required unless the type is text (2026-11-24): only the question is.
        Assert.Equal(new[] { "question" }, item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
        var properties = item.GetProperty("properties");
        Assert.Equal(new[] { "question", "title", "type", "options" }, properties.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "single", "multi", "text" }, properties.GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
        var options = properties.GetProperty("options");
        Assert.Equal("array", options.GetProperty("type").GetString());
        Assert.Equal(2, options.GetProperty("minItems").GetInt32());
        Assert.Equal(10, options.GetProperty("maxItems").GetInt32());
        Assert.Equal("2 to 10 answers to pick from; required unless type is text. The user can always type another.", options.GetProperty("description").GetString());
        // An option is a string or an object with a label (2026-11-24); the anyOf sits inside items, never at the schema's top.
        var anyOf = options.GetProperty("items").GetProperty("anyOf").EnumerateArray().ToArray();
        Assert.Equal("string", anyOf[0].GetProperty("type").GetString());
        Assert.Equal("object", anyOf[1].GetProperty("type").GetString());
        Assert.Equal(new[] { "label", "description", "recommended" }, anyOf[1].GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("boolean", anyOf[1].GetProperty("properties").GetProperty("recommended").GetProperty("type").GetString());
        Assert.Equal(new[] { "label" }, anyOf[1].GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(schema.TryGetProperty("anyOf", out _));
    }

    [Fact]
    public void Limits_FollowTheSettings_ClampedToTheRange_AndTheSchemaAndDescriptionQuoteThem()
    {
        // The caps the Ask tab holds reach the schema, the description and the limits at the next read: no reconnect, no new tool.
        _effective.AskMaxQuestions = 3;
        _effective.AskMaxChoices = 15;
        Assert.Equal(new AskLimits(3, 15), _tool.Limits);
        Assert.Contains("Asks the user 1 to 3 questions", _tool.Description);
        Assert.Contains("Single and multi take 2 to 15 options", _tool.Description);
        var schema = _tool.JsonSchema;
        var questions = schema.GetProperty("properties").GetProperty("questions");
        Assert.Equal(3, questions.GetProperty("maxItems").GetInt32());
        Assert.Equal("1 to 3 questions, asked together on one screen.", questions.GetProperty("description").GetString());
        var options = questions.GetProperty("items").GetProperty("properties").GetProperty("options");
        Assert.Equal(15, options.GetProperty("maxItems").GetInt32());
        Assert.Equal("2 to 15 answers to pick from; required unless type is text. The user can always type another.", options.GetProperty("description").GetString());
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
        Assert.Equal("Error: question 1: options must be a list of 2 to 2 options, each a short string or {\"label\": \"…\", \"description\": \"…\"}.", await InvokeAsync("""[{ "question": "Toppings?", "options": "cheese" }]"""));
        Assert.Empty(_asked);

        _effective.AskMaxChoices = 3;
        _reply = AskReply.Answered([]);
        await InvokeAsync("""[{ "question": "Toppings?", "options": ["cheese", "olives", "ham"] }]""");
        Assert.Single(_asked);
    }

    [Fact]
    public async Task Invoke_HandsTheQuestionsToTheSeam_AndAnswersOneLinePerQuestion()
    {
        var colour = new AskQuestion("Which color?", "Color", AskKind.Single, ["red", "blue"]);
        var toppings = new AskQuestion("Toppings?", null, AskKind.Multi, ["cheese", "olives", "ham"]);
        _reply = AskReply.Answered([new AskAnswer(colour, ["blue"]), new AskAnswer(toppings, ["cheese", "olives"])]);

        object? answer = await InvokeAsync(TwoQuestions);

        Assert.Equal("Which color? — blue\nToppings? — cheese, olives", answer);
        // A record over a list compares by reference: the fields are compared flat.
        var asked = Assert.Single(_asked);
        static (string, string?, AskKind, string) Flat(AskQuestion q) => (q.Question, q.Title, q.Kind, Labels(q));
        Assert.Equal(new[] { Flat(colour), Flat(toppings) }, asked.Select(Flat).ToArray());
    }

    [Fact]
    public async Task Invoke_NullFromTheSeam_OrADecline_IsNotAnswered_ANoteRidesAlong()
    {
        _reply = null;
        Assert.Equal(AskUserText.NotAnswered, await InvokeAsync(TwoQuestions));
        _reply = AskReply.NotShown;
        Assert.Equal(AskUserText.NotAnswered, await InvokeAsync(TwoQuestions));
        _reply = AskReply.Declined();
        Assert.Equal(AskUserText.NotAnswered, await InvokeAsync(TwoQuestions));
        // E (2026-11-24): the note on one line.
        _reply = AskReply.Declined("  use the defaults\nfor now ");
        Assert.Equal("(questions not answered: use the defaults for now)", await InvokeAsync(TwoQuestions));
        _reply = AskReply.Declined("   ");
        Assert.Equal(AskUserText.NotAnswered, await InvokeAsync(TwoQuestions));
        Assert.Equal(5, _asked.Count);
    }

    [Fact]
    public async Task Invoke_ACancelledSeam_Propagates()
    {
        var tool = new AskUserTool((_, ct) => Task.FromCanceled<AskReply?>(ct), () => _effective);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeAsync(Args(Json(TwoQuestions)), cts.Token).AsTask());
    }

    [Fact]
    public async Task Invoke_TypeDefaultsToSingle_IsCaseInsensitive_TextsTrimmedAndFlattened_TitleBlankIsNone()
    {
        _reply = AskReply.Answered([]);
        await InvokeAsync("""
            [
              { "question": "  Which\ncolor? ", "title": "  ", "type": " MULTI ", "options": [" red ", "light\r\nblue"] },
              { "question": "Size?", "options": ["s", "m"] }
            ]
            """);

        var asked = Assert.Single(_asked);
        Assert.Equal("Which color?", asked[0].Question);
        Assert.Null(asked[0].Title);
        Assert.True(asked[0].Multi);
        Assert.Equal("red|light blue", Labels(asked[0]));
        Assert.Equal(AskKind.Single, asked[1].Kind);
        Assert.Null(asked[1].Title);
    }

    // ── 2026-11-24, the ask_user plan: text questions, option objects, the recommended mark ──

    [Fact]
    public async Task Invoke_ATextQuestion_TakesNoOptions_AndNoTypeNoOptions_IsText()
    {
        _reply = AskReply.Answered([]);
        await InvokeAsync("""
            [
              { "question": "Name the file?", "type": "Text" },
              { "question": "Anything else?" },
              { "question": "Why?", "type": "text", "options": [] },
              { "question": "Toppings?", "options": null, "type": "text" },
              { "question": "When?", "options": [] },
              { "question": "Where?", "options": "[]" },
              { "question": "How?", "type": "text", "choices": " " }
            ]
            """);
        var asked = Assert.Single(_asked);
        Assert.Equal(7, asked.Count);   // the last-7 review's #25: an empty list, in a string too, is no options; with no type, a free-text question
        Assert.All(asked, q => Assert.Equal(AskKind.Text, q.Kind));
        Assert.All(asked, q => Assert.Empty(q.Options));
        Assert.True(asked[0].IsText);

        // Options on a text question: the model meant a choice.
        _asked.Clear();
        Assert.Equal("Error: question 2 is type text, which takes no options; use single or multi to offer choices.",
            await InvokeAsync("""[{ "question": "a?", "options": ["x", "y"] }, { "question": "b?", "type": "text", "options": ["x", "y"] }]"""));
        // A single or multi question still needs its options.
        Assert.Equal(AskUserText.BadOptions(1, 10), await InvokeAsync("""[{ "question": "a?", "type": "single" }]"""));
        Assert.Equal(AskUserText.BadOptions(1, 10), await InvokeAsync("""[{ "question": "a?", "multi": true }]"""));

        // A list under a key ask_user doesn't read, with no options or type (the last-7 review's #20): the model is told, not asked a text question.
        Assert.Equal("Error: question 2 sends a list as \"answers\", which ask_user doesn't read; send the choices as \"options\", or leave the list out for a free-text question.",
            await InvokeAsync("""[{ "question": "a?" }, { "question": "b?", "answers": ["x", "y"] }]"""));
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Invoke_OptionObjects_LabelOrText_ADescription_AndTheRecommendedMark()
    {
        _reply = AskReply.Answered([]);
        await InvokeAsync("""
            [
              { "question": "Which color?", "options": [
                  { "label": " red ", "description": " Warm.\nBold. ", "recommended": true },
                  { "text": "blue", "description": "  ", "recommended": "true" },
                  "green" ] },
              { "question": "Toppings?", "type": "multi", "options": [
                  { "label": "cheese", "recommended": true }, { "label": "olives", "recommended": "TRUE" }, { "label": "ham", "recommended": false } ] }
            ]
            """);
        var asked = Assert.Single(_asked);
        Assert.Equal(new AskOption("red", "Warm. Bold.", true), asked[0].Options[0]);
        // A single keeps its first recommended option only; a blank description is none.
        Assert.Equal(new AskOption("blue", null, false), asked[0].Options[1]);
        Assert.Equal(new AskOption("green"), asked[0].Options[2]);
        // A multi may recommend several.
        Assert.Equal(new[] { true, true, false }, asked[1].Options.Select(o => o.Recommended).ToArray());
    }

    [Fact]
    public async Task Invoke_ADoubleEncodedList_IsRead()
    {
        _reply = AskReply.Answered([]);
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
    [InlineData("[{\"question\":\"a\",\"type\":\"either\",\"options\":[\"x\",\"y\"]}]", "Error: question 1: type must be single, multi or text, not 'either'.")]
    [InlineData("[{\"question\":\"a\",\"options\":\"x\"}]", "Error: question 1: options must be a list of 2 to 10 options, each a short string or {\"label\": \"…\", \"description\": \"…\"}.")]
    [InlineData("[{\"question\":\"a\",\"options\":[\"x\", {\"v\": 2}]}]", "Error: question 1: options must be a list of 2 to 10 options, each a short string or {\"label\": \"…\", \"description\": \"…\"}.")]
    [InlineData("[{\"question\":\"a\",\"options\":[\"x\", {\"label\": \" \"}]}]", "Error: question 1: options must be a list of 2 to 10 options, each a short string or {\"label\": \"…\", \"description\": \"…\"}.")]
    [InlineData("[{\"question\":\"a\",\"options\":[\"x\", \" \"]}]", "Error: question 1: options must be a list of 2 to 10 options, each a short string or {\"label\": \"…\", \"description\": \"…\"}.")]
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
        _reply = AskReply.Answered([]);
        await InvokeAsync("""{ "question": "Q?", "options": ["a", "b"] }""");
        Assert.Equal("Q?", Assert.Single(_asked).Single().Question);
    }

    [Fact]
    public async Task Invoke_TheFieldsAtTheTopLevel_AreOneQuestion()
    {
        _reply = AskReply.Answered([]);
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["question"] = Json("\"Which color?\""),
            ["type"] = Json("\"multi\""),
            ["options"] = Json("[\"red\", \"blue\"]"),
        });
        await _tool.InvokeAsync(arguments, CancellationToken.None);
        var asked = Assert.Single(_asked).Single();
        Assert.Equal(("Which color?", true, "red|blue"), (asked.Question, asked.Multi, Labels(asked)));

        // Neither questions nor question: the sentence names the keys that were sent.
        _asked.Clear();
        object? answer = await _tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["prompts"] = Json("[]"), ["count"] = Json("2") }), CancellationToken.None);
        Assert.Equal(AskUserText.NoQuestions("no questions key; the arguments were prompts, count", 10), answer);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Invoke_TheAlternativeKeys_AndAStringHoldingTheOptions_AreRead()
    {
        _reply = AskReply.Answered([]);
        await InvokeAsync("""
            [
              { "text": "Color?", "multi": true, "choices": ["red", "blue"] },
              { "question": "Size?", "multiple": "true", "options": "['s', 'm', 'l']" },
              { "question": "How many?", "options": [1, 2, true] }
            ]
            """);
        var asked = Assert.Single(_asked);
        Assert.Equal(("Color?", true, "red|blue"), (asked[0].Question, asked[0].Multi, Labels(asked[0])));
        Assert.Equal(("Size?", true, "s|m|l"), (asked[1].Question, asked[1].Multi, Labels(asked[1])));
        Assert.Equal(("How many?", false, "1|2|true"), (asked[2].Question, asked[2].Multi, Labels(asked[2])));
    }

    [Fact]
    public async Task Invoke_APythonStyleListInAString_IsRead()
    {
        _reply = AskReply.Answered([]);
        await _tool.InvokeAsync(Args("[{'question': 'It\\'s which?', 'type': 'single', 'options': ['a', \"b\"],}]"), CancellationToken.None);
        var asked = Assert.Single(_asked).Single();
        Assert.Equal(("It's which?", false, "a|b"), (asked.Question, asked.Multi, Labels(asked)));
    }

    [Fact]
    public void Description_ShowsTheShape()
    {
        Assert.Contains("Call it as {\"questions\": [{\"question\": \"Which color?\", \"type\": \"single\", \"options\": [\"red\", \"blue\"]}]} — questions is always a list, even for one question.", _tool.Description);
    }

    // ── The answers (2026-11-24): typed marked, skipped said, a typed option read as the option ──

    [Fact]
    public void ResultText_TagsTypedWords_SaysSkipped_AndATextAnswerIsTyped()
    {
        var colour = new AskQuestion("Which color?", null, AskKind.Multi, ["red", "blue"]);
        var size = new AskQuestion("Which size?", null, AskKind.Single, ["s", "m"]);
        var name = new AskQuestion("What should the file be called?", null, AskKind.Text, []);
        var answers = new[]
        {
            AskAnswer.From(colour, [1], " teal-ish "),
            AskAnswer.From(size, [], "  "),
            AskAnswer.From(name, [], "notes-2026"),
        };
        Assert.Equal("Which color? — blue, teal-ish (typed)\nWhich size? — (skipped)\nWhat should the file be called? — notes-2026 (typed)", AskUserText.ResultText(answers));
        Assert.True(answers[1].Skipped);
        Assert.False(answers[2].Skipped);
    }

    [Fact]
    public void From_ATypedOption_IsThatOption_OnSingleAndMulti_AnythingElseStaysTyped()
    {
        var single = new AskQuestion("Q?", null, AskKind.Single, ["red", "light  blue"]);
        var multi = new AskQuestion("Q?", null, AskKind.Multi, ["red", "blue"]);
        var text = new AskQuestion("Q?", null, AskKind.Text, []);

        // " Blue " against "blue": the option, no tag; on a single it replaces the mark.
        var one = AskAnswer.From(single, [0], " LIGHT blue ");
        Assert.Equal(["light  blue"], one.Choices);
        Assert.Null(one.Typed);
        var many = AskAnswer.From(multi, [0], " Blue ");
        Assert.Equal(["red", "blue"], many.Choices);
        Assert.Null(many.Typed);
        Assert.Equal("blue", AskUserText.AnswerText(AskAnswer.From(multi, [1], "blue")));
        // Anything else stays typed; a text question never matches.
        var teal = AskAnswer.From(multi, [5, -1, 0, 0], "teal");
        Assert.Equal(["red"], teal.Choices);
        Assert.Equal("teal", teal.Typed);
        Assert.Equal("blue (typed)", AskUserText.AnswerText(AskAnswer.From(text, [], "blue")));
        Assert.Equal(-1, AskAnswer.MatchOption(text, "x"));
        Assert.Equal(1, AskAnswer.MatchOption(single, "light blue"));
    }

    // ── F, the Ask repeat guard (2026-11-24) ──

    [Fact]
    public async Task RepeatGuard_On_ARepeatAfterADecline_IsRefusedUnasked_WithTheNote()
    {
        _effective.AskRepeatGuard = true;
        using var turn = new CancellationTokenSource();
        _reply = AskReply.Declined("not now");
        Assert.Equal("(questions not answered: not now)", await InvokeAsync(TwoQuestions, turn.Token));

        // The same questions (spacing and case aside): refused, the seam never called.
        _reply = AskReply.Answered([]);
        string again = """[{ "question": " which  COLOR? ", "options": ["red", "blue"] }]""";
        Assert.Equal("The user declined these questions earlier in this turn; go on without them. Their note: not now", await InvokeAsync(again, turn.Token));
        Assert.Single(_asked);

        // One new question among them: asked.
        Assert.Equal("", await InvokeAsync("""[{ "question": "Which color?", "options": ["red", "blue"] }, { "question": "Size?" }]""", turn.Token));
        Assert.Equal(2, _asked.Count);

        // A later decline of another question with no note (2026-11-24, the last-7 review's #21): the color keeps its own note, the
        // size has none, and the two asked together carry the one note there is.
        _reply = AskReply.Declined();
        await InvokeAsync("""[{ "question": "Size?" }]""", turn.Token);
        Assert.Equal(AskUserText.AlreadyDeclined("not now"), await InvokeAsync(again, turn.Token));
        Assert.Equal(AskUserText.AlreadyDeclined(null), await InvokeAsync("""[{ "question": "Size?" }]""", turn.Token));
        Assert.Equal(AskUserText.AlreadyDeclined("not now"), await InvokeAsync("""[{ "question": "Size?" }, { "question": "Which color?", "options": ["red", "blue"] }]""", turn.Token));
        Assert.Equal(3, _asked.Count);

        // Two notes for the questions asked together: both, in the order asked.
        _reply = AskReply.Declined("too big");
        await InvokeAsync("""[{ "question": "Shape?" }]""", turn.Token);
        Assert.Equal(AskUserText.AlreadyDeclined("too big; not now"), await InvokeAsync("""[{ "question": "Shape?" }, { "question": "Which color?", "options": ["red", "blue"] }]""", turn.Token));
        Assert.Equal(4, _asked.Count);

        // A new turn asks again.
        using var next = new CancellationTokenSource();
        _reply = AskReply.Answered([]);
        await InvokeAsync(again, next.Token);
        Assert.Equal(5, _asked.Count);
    }

    [Fact]
    public async Task RepeatGuard_NeverCounts_ANotShownReply_AnUncancellableToken_OrTheSettingOff()
    {
        _effective.AskRepeatGuard = true;
        using var turn = new CancellationTokenSource();
        // A pane that never opened (or closed under the user) is no decline.
        _reply = AskReply.NotShown;
        Assert.Equal(AskUserText.NotAnswered, await InvokeAsync(TwoQuestions, turn.Token));
        Assert.Equal(AskUserText.NotAnswered, await InvokeAsync(TwoQuestions, turn.Token));
        Assert.Equal(2, _asked.Count);

        // No turn to tell apart: nothing kept.
        _reply = AskReply.Declined();
        await InvokeAsync(TwoQuestions, CancellationToken.None);
        await InvokeAsync(TwoQuestions, CancellationToken.None);
        Assert.Equal(4, _asked.Count);

        // Off: every call shows the pane.
        _effective.AskRepeatGuard = false;
        await InvokeAsync(TwoQuestions, turn.Token);
        await InvokeAsync(TwoQuestions, turn.Token);
        Assert.Equal(6, _asked.Count);
        Assert.False(new AppSettingsData().AskRepeatGuard);
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
        Assert.Equal("Which color? — blue", AskUserText.AnswerLine("Which color?", "blue"));
        var q = new AskQuestion("Q?", null, AskKind.Single, ["a", "b"]);
        Assert.Equal("Q? — a\nQ? — b", AskUserText.ResultText([new AskAnswer(q, ["a"]), new AskAnswer(q, ["b"])]));
        Assert.Equal("(questions not answered)", AskUserText.NotAnswered);
        Assert.Equal(" (typed)", AskUserText.TypedTag);
        Assert.Equal("(skipped)", AskUserText.Skipped);
        Assert.Equal("(questions not answered: later)", AskUserText.Declined(" later "));
        Assert.Equal("The user declined these questions earlier in this turn; go on without them.", AskUserText.AlreadyDeclined(" "));
        Assert.Equal("ask_user: repeat refused", AskUserText.RepeatRefusedLogLine);
        Assert.Equal("Error: question 3: type must be single, multi or text, not 'x'.", AskUserText.BadType(3, " x "));
        Assert.Equal("Error: question 2 has 12 options; give 2 to 10.", AskUserText.OptionCount(2, 12, 10));
        Assert.Equal("Error: question 2 has 16 options; give 2 to 15.", AskUserText.OptionCount(2, 16, 15));
        Assert.Equal("Error: at most 3 questions per call; ask the most important ones first.", AskUserText.TooManyQuestions(3));
        Assert.Equal("Error: question 4: options must be a list of 2 to 7 options, each a short string or {\"label\": \"…\", \"description\": \"…\"}.", AskUserText.BadOptions(4, 7));
        Assert.Equal("Error: question 1 is type text, which takes no options; use single or multi to offer choices.", AskUserText.TextTakesNoOptions(1));
    }
}
