using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>One question the model asks: the text, an optional short title (its tab), single or multi-choice, and its options (two to the profile's cap).</summary>
public sealed record AskQuestion(string Question, string? Title, bool Multi, IReadOnlyList<string> Options);

/// <summary>The user's answer to one question: the chosen options in option order, a typed answer of their own last.</summary>
public sealed record AskAnswer(AskQuestion Question, IReadOnlyList<string> Choices);

/// <summary>
/// The caps one <c>ask_user</c> call works under (2026-09-15): the most questions per call
/// (<c>Ask max questions</c>) and the most options per question (<c>Ask max choices per question</c>).
/// The floor on options is the tool's own (<see cref="AskUserTool.MinOptions"/>). The schema, the
/// description, the rule and the error sentences all quote the same pair.
/// </summary>
public readonly record struct AskLimits(int MaxQuestions, int MaxChoices)
{
    /// <summary>A fresh profile's caps: 10 questions of 10 choices.</summary>
    public static readonly AskLimits Default = new(AppSettingsData.DefaultAskMaxQuestions, AppSettingsData.DefaultAskMaxChoices);

    /// <summary>The caps the settings hold, each clamped to its range (a hand-edited file may have left anything).</summary>
    public static AskLimits From(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return new(
            Math.Clamp(effective.AskMaxQuestions, AppSettingsData.MinAskMaxQuestions, AppSettingsData.MaxAskMaxQuestions),
            Math.Clamp(effective.AskMaxChoices, AppSettingsData.MinAskMaxChoices, AppSettingsData.MaxAskMaxChoices));
    }
}

/// <summary>
/// The model's way to put a choice to the user: <c>ask_user(questions)</c> — one to
/// <see cref="AskLimits.MaxQuestions"/> questions, each single-choice (a picker) or multi-choice (a
/// checkbox list) over <see cref="MinOptions"/> to <see cref="AskLimits.MaxChoices"/> options, shown
/// as tabs on the bottom pane with a Submit tab last; the user can type an answer of their own on
/// any of them, and ESC cancels the lot. The tool waits for the pane through the seam it is built
/// over — it runs on the turn task and never reads a key itself — and returns one line per question
/// (<see cref="AskUserText.ResultText"/>) or <see cref="AskUserText.NotAnswered"/>. Offered while the
/// setting <c>Ask user</c> says so and the bottom pane is on: nothing else can draw the questions, and
/// headless never has it. The caps are read off the settings at every use (the
/// <see cref="WebSearchTool"/> shape), so the schema the model sees follows an edit without a
/// reconnect. The <see cref="SaveMemoryTool"/> pattern throughout; the questions array is the one
/// nested schema in the repo, read through <see cref="ToolArguments.TryReadObjectList"/>.
/// </summary>
public sealed class AskUserTool : AIFunction
{
    public const string ToolName = "ask_user";

    public const string QuestionsArgument = "questions";
    public const string QuestionArgument = "question";
    public const string TitleArgument = "title";
    public const string TypeArgument = "type";
    public const string OptionsArgument = "options";
    public const string SingleType = "single";
    public const string MultiType = "multi";

    /// <summary>The fewest options a question may offer — a choice needs two; the most is the setting (<see cref="AskLimits.MaxChoices"/>).</summary>
    public const int MinOptions = AppSettingsData.MinAskMaxChoices;

    private readonly Func<IReadOnlyList<AskQuestion>, CancellationToken, Task<IReadOnlyList<AskAnswer>?>> _ask;
    private readonly Func<AppSettingsData> _effective;

    // The schema for the caps last asked for: a request reads JsonSchema once per tool, and the
    // caps change only when the user edits a row, so one parse per edit.
    private AskLimits _schemaLimits;
    private JsonElement _schema;

    /// <param name="ask">Shows the questions and waits: the answers, or null when the user cancelled (ESC) or nothing could ask them. The token is the turn's.</param>
    /// <param name="effective">The settings the caps are read from (<see cref="AskLimits.From"/>) at every use.</param>
    public AskUserTool(Func<IReadOnlyList<AskQuestion>, CancellationToken, Task<IReadOnlyList<AskAnswer>?>> ask, Func<AppSettingsData> effective)
    {
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    public override string Name => ToolName;

    /// <summary>The caps as the settings stand now.</summary>
    public AskLimits Limits => AskLimits.From(_effective());

    public override string Description => Describe(Limits);

    /// <summary>The description under the given caps. Pinned.</summary>
    public static string Describe(AskLimits limits) =>
        $"Asks the user 1 to {N(limits.MaxQuestions)} multiple-choice questions on screen and waits for the answers. " +
        "Use it when you need a decision, a preference or a clarification with a short list of answers before going on. " +
        $"Each question has {N(MinOptions)} to {N(limits.MaxChoices)} short options; the user can also type their own answer. " +
        "Call it as {\"questions\": [{\"question\": \"Which color?\", \"type\": \"single\", \"options\": [\"red\", \"blue\"]}]} — questions is always a list, even for one question. " +
        "If the result says the questions were not answered, the user declined: go on without them and do not ask again unless asked.";

    public override JsonElement JsonSchema
    {
        get
        {
            var limits = Limits;
            if (_schema.ValueKind == JsonValueKind.Undefined || limits != _schemaLimits)
            {
                _schema = SchemaFor(limits);
                _schemaLimits = limits;
            }

            return _schema;
        }
    }

    /// <summary>The schema under the given caps: the questions array's <c>maxItems</c> and the options array's, with their descriptions. Pinned.</summary>
    public static JsonElement SchemaFor(AskLimits limits) => ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "questions": {
              "type": "array",
              "minItems": 1,
              "maxItems": {{N(limits.MaxQuestions)}},
              "description": "1 to {{N(limits.MaxQuestions)}} questions, asked together on one screen.",
              "items": {
                "type": "object",
                "properties": {
                  "question": { "type": "string", "description": "The question, one sentence." },
                  "title": { "type": "string", "description": "A word or two naming the question (its tab). Optional." },
                  "type": { "type": "string", "enum": ["single", "multi"], "description": "single = exactly one answer (the default); multi = one or more." },
                  "options": { "type": "array", "items": { "type": "string" }, "minItems": {{N(MinOptions)}}, "maxItems": {{N(limits.MaxChoices)}}, "description": "{{N(MinOptions)}} to {{N(limits.MaxChoices)}} short answers to pick from. The user can always type another." }
                },
                "required": ["question", "options"]
              }
            }
          },
          "required": ["questions"]
        }
        """);

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (TryParse(arguments, Limits, out var questions) is { } error)
        {
            return error;
        }

        var answers = await _ask(questions, cancellationToken).ConfigureAwait(false);
        return answers is null ? AskUserText.NotAnswered : AskUserText.ResultText(answers);
    }

    /// <summary>
    /// The questions out of the arguments, or the <c>Error:</c> sentence for a call that cannot be
    /// asked: no list (or one holding something other than objects), more than
    /// <see cref="AskLimits.MaxQuestions"/>, no question text, a type other than <c>single</c> / <c>multi</c>
    /// (missing means single), options that are not all strings, or fewer than
    /// <see cref="MinOptions"/> / more than <see cref="AskLimits.MaxChoices"/> of them. Texts are trimmed
    /// with their line breaks flattened; a blank title reads as none; duplicate options are kept
    /// as sent. Lenient on purpose (2026-09-15, after Qwen3 on SGLang missed the shape on every
    /// first call): a lone object or a hand-written string for <c>questions</c>
    /// (<see cref="ToolArguments.TryReadObjectList"/>), the one question's fields at the top level
    /// with no <c>questions</c> at all, <see cref="TextArgument"/> for the question,
    /// <see cref="ChoicesArgument"/> for the options, a boolean <see cref="MultiArgument"/> /
    /// <see cref="MultipleArgument"/> for the type, options as a string holding the list, and a
    /// number or a boolean as an option's text. The documented shape stays the schema's.
    /// </summary>
    internal static string? TryParse(AIFunctionArguments arguments, AskLimits limits, out IReadOnlyList<AskQuestion> questions)
    {
        var parsed = new List<AskQuestion>();
        questions = parsed;
        if (!ToolArguments.TryReadObjectList(arguments, QuestionsArgument, out var objects, out string raw))
        {
            return AskUserText.NoQuestions(raw, limits.MaxQuestions);
        }

        if (objects.Count == 0)
        {
            // No list: the one question's fields at the top level, else nothing to ask.
            if (arguments.ContainsKey(QuestionArgument) || arguments.ContainsKey(TextArgument))
            {
                string? topLevelError = ParseItem(1, limits, name => arguments.TryGetValue(name, out var value) && value is JsonElement element ? element : null, out var single);
                if (topLevelError is not null)
                {
                    return topLevelError;
                }

                parsed.Add(single!);
                return null;
            }

            return AskUserText.NoQuestions(raw.Length > 0 ? raw : AskUserText.SentKeys(arguments.Keys), limits.MaxQuestions);
        }

        if (objects.Count > limits.MaxQuestions)
        {
            return AskUserText.TooManyQuestions(limits.MaxQuestions);
        }

        for (int i = 0; i < objects.Count; i++)
        {
            var item = objects[i];
            string? error = ParseItem(i + 1, limits, name => item.TryGetProperty(name, out var value) ? value : null, out var question);
            if (error is not null)
            {
                return error;
            }

            parsed.Add(question!);
        }

        return null;
    }

    /// <summary>The alternatives the reader takes for a question's fields (the schema names the first of each pair).</summary>
    public const string TextArgument = "text";
    public const string ChoicesArgument = "choices";
    public const string MultiArgument = "multi";
    public const string MultipleArgument = "multiple";

    /// <summary>One question out of its fields, read through <paramref name="property"/> (null for a missing one); the error sentence or null.</summary>
    private static string? ParseItem(int number, AskLimits limits, Func<string, JsonElement?> property, out AskQuestion? question)
    {
        question = null;
        string text = Flatten(Text(property(QuestionArgument)));
        if (text.Length == 0)
        {
            text = Flatten(Text(property(TextArgument)));
        }

        if (text.Length == 0)
        {
            return AskUserText.NoQuestionText(number);
        }

        string title = Flatten(Text(property(TitleArgument)));
        bool multi;
        string type = Text(property(TypeArgument)).Trim();
        if (type.Length == 0)
        {
            multi = IsTrue(property(MultiArgument)) || IsTrue(property(MultipleArgument));
        }
        else if (string.Equals(type, SingleType, StringComparison.OrdinalIgnoreCase))
        {
            multi = false;
        }
        else if (string.Equals(type, MultiType, StringComparison.OrdinalIgnoreCase))
        {
            multi = true;
        }
        else
        {
            return AskUserText.BadType(number, type);
        }

        var sent = property(OptionsArgument) ?? property(ChoicesArgument);
        using var encoded = sent is { ValueKind: JsonValueKind.String } s && s.GetString() is { } list ? LooseJson.TryParse(list) : null;
        if (encoded is not null)
        {
            // The list written by hand inside a string: what a server's parser hands over when its own parse failed.
            sent = encoded.RootElement;
        }

        if (sent is not { ValueKind: JsonValueKind.Array } array)
        {
            return AskUserText.BadOptions(number, limits.MaxChoices);
        }

        var options = new List<string>();
        foreach (var option in array.EnumerateArray())
        {
            string optionText = option.ValueKind switch
            {
                JsonValueKind.String => Flatten(option.GetString() ?? ""),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => option.GetRawText(),
                _ => "",
            };
            if (optionText.Length == 0)
            {
                return AskUserText.BadOptions(number, limits.MaxChoices);
            }

            options.Add(optionText);
        }

        if (options.Count < MinOptions || options.Count > limits.MaxChoices)
        {
            return AskUserText.OptionCount(number, options.Count, limits.MaxChoices);
        }

        question = new AskQuestion(text, title.Length == 0 ? null : title, multi, options);
        return null;
    }

    /// <summary>A string value's text; empty when missing, null or not a string.</summary>
    private static string Text(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } element ? element.GetString() ?? "" : "";

    /// <summary>A boolean value that is true, or the word <c>true</c>.</summary>
    private static bool IsTrue(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.True } || (value is { ValueKind: JsonValueKind.String } s && string.Equals(s.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>The text on one line, trimmed: a line break inside a question or an option reads as a space.</summary>
    private static string Flatten(string text) => text.ReplaceLineEndings(" ").Trim();
}
