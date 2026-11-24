using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>What kind of answer a question takes (2026-11-24, the ask_user plan): one option, one or more, or the user's own words (no options).</summary>
public enum AskKind
{
    Single,
    Multi,
    Text,
}

/// <summary>
/// One option of a single- or multi-choice question (2026-11-24, the ask_user plan): its label, a one-sentence description shown
/// under the list for the cursor's row (null for none), and whether the model recommends it (tagged and the cursor's first stop,
/// never chosen for the user). A bare string is an option with neither.
/// </summary>
public sealed record AskOption(string Label, string? Description = null, bool Recommended = false)
{
    public static implicit operator AskOption(string label) => new(label);
}

/// <summary>One question the model asks: the text, an optional short title (its tab), its kind, and its options (none for <see cref="AskKind.Text"/>, else two to the profile's cap).</summary>
public sealed record AskQuestion(string Question, string? Title, AskKind Kind, IReadOnlyList<AskOption> Options)
{
    /// <summary>A multi-choice question: checkboxes.</summary>
    public bool Multi => Kind == AskKind.Multi;

    /// <summary>A free-text question: no options, the answer typed.</summary>
    public bool IsText => Kind == AskKind.Text;
}

/// <summary>
/// The user's answer to one question: the options picked, in option order, and the words they typed (null for none). Both empty
/// is a question the user saw and left to the model (<see cref="Skipped"/>, 2026-11-24). Built by <see cref="From"/>, the one place
/// a typed answer that is one of the options becomes that option, so the pane and the page agree.
/// </summary>
public sealed record AskAnswer(AskQuestion Question, IReadOnlyList<string> Choices, string? Typed = null)
{
    /// <summary>Nothing picked and nothing typed: the user left it to the model.</summary>
    public bool Skipped => Choices.Count == 0 && Typed is null;

    /// <summary>
    /// The answer out of the rows picked (indexes into the options; out-of-range ones dropped) and the text typed (2026-11-24, the user's
    /// call): the text is flattened and trimmed, empty for none, and one that is an option's label — trimmed, spaces collapsed, case
    /// ignored — counts as that option: the mark on a single question, one more checked box on a multi, never a typed answer.
    /// </summary>
    public static AskAnswer From(AskQuestion question, IEnumerable<int> picked, string? typed)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(picked);
        var rows = new SortedSet<int>(picked.Where(p => p >= 0 && p < question.Options.Count));
        string? text = typed?.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = null;
        }
        else
        {
            text = Files.TextCut.Safe(text, AskUserText.NoteChars);
        }

        if (text is not null)
        {
            int match = MatchOption(question, text);
            if (match >= 0)
            {
                if (!question.Multi)
                {
                    rows.Clear();
                }

                rows.Add(match);
                text = null;
            }
        }

        return new AskAnswer(question, rows.Select(r => question.Options[r].Label).ToList(), text);
    }

    /// <summary>The option <paramref name="typed"/> names — trimmed, spaces collapsed, case ignored — or −1 (always on a text question).</summary>
    public static int MatchOption(AskQuestion question, string typed)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(typed);
        string wanted = Collapse(typed);
        if (question.IsText || wanted.Length == 0)
        {
            return -1;
        }

        for (int i = 0; i < question.Options.Count; i++)
        {
            if (string.Equals(Collapse(question.Options[i].Label), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Text with every run of whitespace one space, trimmed: how a typed answer and a question are compared.</summary>
    internal static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// What the ask seam hands back (2026-11-24, the ask_user plan): the answers, one per question in the order asked, or null for a
/// decline — with the user's note when they wrote one. <see cref="Shown"/> says the user saw the questions and chose that: Esc,
/// the decline row, the page's Decline. A pane that never opened, failed, or was closed under the user (the wake phrase) is
/// <see cref="NotShown"/>: the model reads the same <see cref="AskUserText.NotAnswered"/>, but the repeat guard never counts it.
/// </summary>
public sealed record AskReply(IReadOnlyList<AskAnswer>? Answers, string? DeclineNote, bool Shown)
{
    /// <summary>Nothing asked or nothing chosen by the user.</summary>
    public static readonly AskReply NotShown = new(null, null, false);

    /// <summary>The user's Submit.</summary>
    public static AskReply Answered(IReadOnlyList<AskAnswer> answers) => new(answers ?? throw new ArgumentNullException(nameof(answers)), null, true);

    /// <summary>The user's decline, with their note (flattened and trimmed, cut to <see cref="AskUserText.NoteChars"/>; blank reads as none).</summary>
    public static AskReply Declined(string? note = null)
    {
        string? flat = note?.ReplaceLineEndings(" ").Trim();
        return new(null, string.IsNullOrEmpty(flat) ? null : Files.TextCut.Safe(flat, AskUserText.NoteChars), true);
    }
}

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
/// The model's way to put questions to the user: <c>ask_user(questions)</c> — one to
/// <see cref="AskLimits.MaxQuestions"/> questions, each single-choice (a picker), multi-choice (a
/// checkbox list) over <see cref="MinOptions"/> to <see cref="AskLimits.MaxChoices"/> options, or free
/// text (2026-11-24, no options), shown as tabs on the bottom pane with a Submit tab last; the user can
/// type an answer of their own on any of them, skip any at Submit, and Esc declines the lot (a note with
/// it if they like). The tool waits for the pane through the seam it is built over — it runs on the turn
/// task and never reads a key itself — and returns one line per question (<see cref="AskUserText.ResultText"/>)
/// or the decline (<see cref="AskUserText.ReplyText"/>). Offered while the setting <c>Ask user</c> says so and
/// the bottom pane is on: nothing else can draw the questions, and headless never has it. The caps are read
/// off the settings at every use (the <see cref="WebSearchTool"/> shape), so the schema the model sees
/// follows an edit without a reconnect. With <c>Ask repeat guard</c> on (2026-11-24) the tool remembers the
/// questions the user declined in this turn and refuses a call that asks only those again. The
/// <see cref="SaveMemoryTool"/> pattern throughout; the questions array is read through
/// <see cref="ToolArguments.TryReadObjectList"/>.
/// </summary>
public sealed class AskUserTool : AIFunction, IAcceptsArguments
{
    public const string ToolName = "ask_user";

    /// <summary>
    /// The one question's fields the tool reads at the top level, when <see cref="QuestionsArgument"/> is absent (2026-11-15, the
    /// tool-arguments review's #110: it accepted any key, so a misspelled one went unanswered). Beside a non-empty list they are refused
    /// (<see cref="AskUserText.FieldsBesideQuestions"/>).
    /// </summary>
    public IReadOnlyCollection<string> ExtraArguments => TopLevelFields;

    private static readonly string[] TopLevelFields = [QuestionArgument, TextArgument, TitleArgument, TypeArgument, OptionsArgument, ChoicesArgument, MultiArgument, MultipleArgument];

    public const string QuestionsArgument = "questions";
    public const string QuestionArgument = "question";
    public const string TitleArgument = "title";
    public const string TypeArgument = "type";
    public const string OptionsArgument = "options";
    public const string SingleType = "single";
    public const string MultiType = "multi";
    public const string TextType = "text";

    /// <summary>An option object's keys (2026-11-24): the label (or <see cref="TextArgument"/>), its description, the recommended mark.</summary>
    public const string LabelArgument = "label";
    public const string DescriptionArgument = "description";
    public const string RecommendedArgument = "recommended";

    /// <summary>The fewest options a question may offer — a choice needs two; the most is the setting (<see cref="AskLimits.MaxChoices"/>).</summary>
    public const int MinOptions = AppSettingsData.MinAskMaxChoices;

    private readonly Func<IReadOnlyList<AskQuestion>, CancellationToken, Task<AskReply?>> _ask;
    private readonly Func<AppSettingsData> _effective;

    // The schema for the caps last asked for: a request reads JsonSchema once per tool, and the
    // caps change only when the user edits a row, so one parse per edit.
    private AskLimits _schemaLimits;
    private JsonElement _schema;

    // The repeat guard's memory (2026-11-24, F): the turn's token, the questions declined under it (collapsed, case folded), each with
    // the note its own decline carried (the last-7 review's #21: one shared note answered a repeat with another decline's words). A new
    // token replaces the lot, so nothing outlives its turn; tool calls run one at a time, the lock a guard.
    private readonly Lock _declinedLock = new();
    private CancellationToken _declinedTurn;
    private readonly Dictionary<string, string?> _declined = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="ask">Shows the questions and waits: the reply (<see cref="AskReply"/>), or null when nothing could ask them. The token is the turn's.</param>
    /// <param name="effective">The settings the caps and the repeat guard are read from at every use.</param>
    public AskUserTool(Func<IReadOnlyList<AskQuestion>, CancellationToken, Task<AskReply?>> ask, Func<AppSettingsData> effective)
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
        $"Asks the user 1 to {N(limits.MaxQuestions)} questions on screen and waits for the answers. " +
        "Use it when you need a decision, a preference or a clarification before going on. " +
        $"A question is single (one answer, the default), multi (one or more) or text (a free answer, no options). Single and multi take {N(MinOptions)} to {N(limits.MaxChoices)} options, " +
        "each a short string or {\"label\": \"…\", \"description\": \"one short sentence\", \"recommended\": true} (at most one recommended on a single); the user can also type their own answer. " +
        "Call it as {\"questions\": [{\"question\": \"Which color?\", \"type\": \"single\", \"options\": [\"red\", \"blue\"]}]} — questions is always a list, even for one question. " +
        "In the result, (typed) marks the user's own words and (skipped) means they left that question to you: decide it yourself. " +
        "If the result says the questions were not answered, the user declined: go on without them, act on their note if there is one, and do not ask again unless asked.";

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

    /// <summary>
    /// The schema under the given caps: the questions array's <c>maxItems</c> and the options array's, with their descriptions. An option
    /// is a string or an object (2026-11-24); the <c>anyOf</c> sits inside <c>items</c>, never at the top, so
    /// <see cref="ToolArgumentCheck"/> still judges the call's keys. Pinned.
    /// </summary>
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
                  "type": { "type": "string", "enum": ["single", "multi", "text"], "description": "single = exactly one answer (the default); multi = one or more; text = a free answer, no options." },
                  "options": {
                    "type": "array",
                    "minItems": {{N(MinOptions)}},
                    "maxItems": {{N(limits.MaxChoices)}},
                    "description": "{{N(MinOptions)}} to {{N(limits.MaxChoices)}} answers to pick from; required unless type is text. The user can always type another.",
                    "items": {
                      "anyOf": [
                        { "type": "string" },
                        {
                          "type": "object",
                          "properties": {
                            "label": { "type": "string", "description": "The option, a few words." },
                            "description": { "type": "string", "description": "One short sentence on what choosing it means. Optional." },
                            "recommended": { "type": "boolean", "description": "Your recommendation; at most one on a single question. Optional." }
                          },
                          "required": ["label"]
                        }
                      ]
                    }
                  }
                },
                "required": ["question"]
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

        // The repeat guard (2026-11-24, F): only under a turn's own token, which tells one turn from the next.
        bool guard = _effective().AskRepeatGuard && cancellationToken.CanBeCanceled;
        if (guard && AlreadyDeclined(questions, cancellationToken, out string? note))
        {
            DiagnosticLog.Info(AskUserText.LogCategory, AskUserText.RepeatRefusedLogLine);
            return AskUserText.AlreadyDeclined(note);
        }

        var reply = await _ask(questions, cancellationToken).ConfigureAwait(false) ?? AskReply.NotShown;
        if (guard && reply.Answers is null && reply.Shown)
        {
            Remember(questions, cancellationToken, reply.DeclineNote);
        }

        return AskUserText.ReplyText(reply);
    }

    /// <summary>
    /// Whether every one of <paramref name="questions"/> was declined under <paramref name="turn"/>, with the notes those declines carried
    /// (distinct, in the order asked, joined with "; "; null for none).
    /// </summary>
    private bool AlreadyDeclined(IReadOnlyList<AskQuestion> questions, CancellationToken turn, out string? note)
    {
        lock (_declinedLock)
        {
            note = null;
            if (turn != _declinedTurn || _declined.Count == 0)
            {
                return false;
            }

            var notes = new List<string>();
            foreach (var question in questions)
            {
                if (!_declined.TryGetValue(AskAnswer.Collapse(question.Question), out string? own))
                {
                    return false;
                }

                if (own is not null && !notes.Contains(own, StringComparer.Ordinal))
                {
                    notes.Add(own);
                }
            }

            note = notes.Count > 0 ? string.Join("; ", notes) : null;
            return true;
        }
    }

    /// <summary>Notes a decline under <paramref name="turn"/>; a new turn's token starts the memory afresh.</summary>
    private void Remember(IReadOnlyList<AskQuestion> questions, CancellationToken turn, string? note)
    {
        lock (_declinedLock)
        {
            if (turn != _declinedTurn)
            {
                _declined.Clear();
                _declinedTurn = turn;
            }

            foreach (var question in questions)
            {
                _declined[AskAnswer.Collapse(question.Question)] = note;
            }
        }
    }

    /// <summary>
    /// The questions out of the arguments, or the <c>Error:</c> sentence for a call that cannot be
    /// asked: no list (or one holding something other than objects), more than
    /// <see cref="AskLimits.MaxQuestions"/>, no question text, a type other than <c>single</c> / <c>multi</c> /
    /// <c>text</c> (missing means single, or text when no options are sent), options that are no list of
    /// strings or option objects, options on a text question, or fewer than <see cref="MinOptions"/> / more than
    /// <see cref="AskLimits.MaxChoices"/> of them on a choice. Texts are trimmed with their line breaks flattened;
    /// a blank title or description reads as none; duplicate options are kept as sent; a single question keeps
    /// its first recommended option only. Lenient on purpose (2026-09-15, after Qwen3 on SGLang missed the shape
    /// on every first call): a lone object or a hand-written string for <c>questions</c>
    /// (<see cref="ToolArguments.TryReadObjectList"/>), the one question's fields at the top level
    /// with no <c>questions</c> at all, <see cref="TextArgument"/> for the question (and for an option's label),
    /// <see cref="ChoicesArgument"/> for the options, a boolean <see cref="MultiArgument"/> /
    /// <see cref="MultipleArgument"/> for the type, options as a string holding the list, a number or a boolean
    /// as an option's text, and <c>"true"</c> for <c>recommended</c>. The documented shape stays the schema's.
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
                // No stray list to look for at the top level: ToolArgumentCheck already refuses an unknown key there.
                string? topLevelError = ParseItem(1, limits, name => arguments.TryGetValue(name, out var value) && value is JsonElement element ? element : null, null, out var single);
                if (topLevelError is not null)
                {
                    return topLevelError;
                }

                parsed.Add(single!);
                return null;
            }

            return AskUserText.NoQuestions(raw.Length > 0 ? raw : AskUserText.SentKeys(arguments.Keys), limits.MaxQuestions);
        }

        // A question's field beside the list (#110) would be skipped: questions: [...] with a top-level multiple: true ran single-choice.
        var beside = TopLevelFields.Where(name => arguments.TryGetValue(name, out var value) && !ToolArgumentCheck.Harmless(value)).ToList();
        if (beside.Count > 0)
        {
            return AskUserText.FieldsBesideQuestions(beside);
        }

        if (objects.Count > limits.MaxQuestions)
        {
            return AskUserText.TooManyQuestions(limits.MaxQuestions);
        }

        for (int i = 0; i < objects.Count; i++)
        {
            var item = objects[i];
            string? error = ParseItem(i + 1, limits, name => item.TryGetProperty(name, out var value) ? value : null, StrayList(item), out var question);
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

    /// <summary>The fields a question item may carry: a list under any other name is a misnamed options list (<see cref="StrayList"/>).</summary>
    private static readonly HashSet<string> ItemFields = new(StringComparer.Ordinal)
    {
        QuestionArgument, TextArgument, TitleArgument, OptionsArgument, ChoicesArgument, TypeArgument, MultiArgument, MultipleArgument,
    };

    /// <summary>
    /// The name of the first list-valued key of <paramref name="item"/> that is none of <see cref="ItemFields"/> (<c>answers</c>, <c>items</c>):
    /// with no options and no type it was meant as the options (2026-11-24, the last-7 review's #20), not a free-text question. Null for none.
    /// </summary>
    private static string? StrayList(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in item.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array && !ItemFields.Contains(property.Name))
            {
                return property.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// One question out of its fields, read through <paramref name="property"/> (null for a missing one); the error sentence or null.
    /// <paramref name="strayList"/> names a list sent under a key the item doesn't know (<see cref="StrayList"/>), null for none.
    /// </summary>
    private static string? ParseItem(int number, AskLimits limits, Func<string, JsonElement?> property, string? strayList, out AskQuestion? question)
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
        var sent = property(OptionsArgument) ?? property(ChoicesArgument);
        if (sent is { ValueKind: JsonValueKind.Null })
        {
            sent = null;
        }

        // The list written by hand inside a string: what a server's parser hands over when its own parse failed. Unwrapped before the
        // kind is decided (2026-11-24, the last-7 review's #25), so "[]" in a string is the empty list it says.
        using var encoded = sent is { ValueKind: JsonValueKind.String } s && s.GetString() is { } list ? LooseJson.TryParse(list, closeOpen: true) : null;
        if (encoded is not null)
        {
            sent = encoded.RootElement;
        }

        // No options: none sent, a blank string, or an empty list. With no type that is a free-text question; a choice's own errors
        // still read what was sent.
        bool noOptions = sent is null
            || (sent is { ValueKind: JsonValueKind.String } blank && string.IsNullOrWhiteSpace(blank.GetString()))
            || (sent is { ValueKind: JsonValueKind.Array } empty && empty.GetArrayLength() == 0);

        AskKind kind;
        string type = Text(property(TypeArgument)).Trim();
        if (type.Length == 0)
        {
            // No type: multi when the booleans say so, else single — or text when no options came either (2026-11-24).
            bool multi = IsTrue(property(MultiArgument)) || IsTrue(property(MultipleArgument));
            if (!multi && noOptions && strayList is not null)
            {
                return AskUserText.StrayList(number, strayList);
            }

            kind = multi ? AskKind.Multi : noOptions ? AskKind.Text : AskKind.Single;
        }
        else if (string.Equals(type, SingleType, StringComparison.OrdinalIgnoreCase))
        {
            kind = AskKind.Single;
        }
        else if (string.Equals(type, MultiType, StringComparison.OrdinalIgnoreCase))
        {
            kind = AskKind.Multi;
        }
        else if (string.Equals(type, TextType, StringComparison.OrdinalIgnoreCase))
        {
            kind = AskKind.Text;
        }
        else
        {
            return AskUserText.BadType(number, type);
        }

        if (kind == AskKind.Text)
        {
            if (!noOptions)
            {
                return AskUserText.TextTakesNoOptions(number);
            }

            question = new AskQuestion(text, title.Length == 0 ? null : title, AskKind.Text, []);
            return null;
        }

        if (sent is not { ValueKind: JsonValueKind.Array } array)
        {
            return AskUserText.BadOptions(number, limits.MaxChoices);
        }

        var options = new List<AskOption>();
        bool recommendedTaken = false;
        foreach (var item in array.EnumerateArray())
        {
            var option = Option(item);
            if (option is null)
            {
                return AskUserText.BadOptions(number, limits.MaxChoices);
            }

            if (option.Recommended)
            {
                // A single question keeps its first recommended option (one answer, one recommendation); a multi may tag several.
                if (kind == AskKind.Single && recommendedTaken)
                {
                    option = option with { Recommended = false };
                }

                recommendedTaken = true;
            }

            options.Add(option);
        }

        if (options.Count < MinOptions || options.Count > limits.MaxChoices)
        {
            return AskUserText.OptionCount(number, options.Count, limits.MaxChoices);
        }

        question = new AskQuestion(text, title.Length == 0 ? null : title, kind, options);
        return null;
    }

    /// <summary>One option: a string (or a number or a boolean as its text), or an object with a label (or text), a description and a recommended mark; null for a blank one or any other shape.</summary>
    private static AskOption? Option(JsonElement item)
    {
        switch (item.ValueKind)
        {
            case JsonValueKind.String:
                string label = Flatten(item.GetString() ?? "");
                return label.Length == 0 ? null : new AskOption(label);
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return new AskOption(item.GetRawText());
            case JsonValueKind.Object:
                string named = Flatten(Text(item.TryGetProperty(LabelArgument, out var l) ? l : null));
                if (named.Length == 0)
                {
                    named = Flatten(Text(item.TryGetProperty(TextArgument, out var t) ? t : null));
                }

                if (named.Length == 0)
                {
                    return null;
                }

                string description = Flatten(Text(item.TryGetProperty(DescriptionArgument, out var d) ? d : null));
                bool recommended = IsTrue(item.TryGetProperty(RecommendedArgument, out var r) ? r : null);
                return new AskOption(named, description.Length == 0 ? null : description, recommended);
            default:
                return null;
        }
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
