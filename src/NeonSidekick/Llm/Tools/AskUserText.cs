using System.Globalization;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The words <see cref="AskUserTool"/> speaks: the result the model reads (and, line by line, the
/// dim <c>🛠️</c> lines the transcript shows) and the error sentences for a call the tool cannot
/// put to the user. Pure statics, every string pinned.
/// </summary>
public static class AskUserText
{
    /// <summary>The tool's result — and the one transcript line — when the user cancelled the questions (ESC), or nothing could ask them. Pinned.</summary>
    public const string NotAnswered = "(questions not answered)";

    /// <summary>The joiner between an answer's choices: <c>cheese, olives</c>.</summary>
    public const string ChoiceSeparator = ", ";

    /// <summary>One question's choices as the result prints them: the options in their order, a typed answer last.</summary>
    public static string AnswerText(IReadOnlyList<string> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return string.Join(ChoiceSeparator, choices);
    }

    /// <summary>One line of the result: <c>Which colour? — blue</c>.</summary>
    public static string AnswerLine(string question, string answer) => $"{question} — {answer}";

    /// <summary>The tool's result for answered questions: one <see cref="AnswerLine"/> per question in the order asked, joined by a line break.</summary>
    public static string ResultText(IReadOnlyList<AskAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        return string.Join('\n', answers.Select(a => AnswerLine(a.Question.Question, AnswerText(a.Choices))));
    }

    // ── The model's mistakes ────────────────────────────────────────────────

    /// <summary>The most cells of what was sent that the <see cref="NoQuestions"/> sentence repeats, on one line; a longer text is cut in the middle — its head and its tail with an ellipsis between (2026-09-16: the fault SGLang's parser leaves is a brace at the very end, which a head alone hid).</summary>
    public const int RawCells = 120;

    /// <summary>What the sentence says was sent when nothing was.</summary>
    public const string Nothing = "(nothing)";

    /// <summary>The shape the sentence shows. Pinned.</summary>
    public const string Shape = "[{\"question\": \"…\", \"type\": \"single\", \"options\": [\"a\", \"b\"]}]";

    /// <summary>The sentence for a <c>questions</c> that is no list of question objects, naming the shape and the cap (<c>Ask max questions</c>) and repeating what was sent (<see cref="RawCells"/>, one line).</summary>
    public static string NoQuestions(string raw, int maxQuestions)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string flat = raw.ReplaceLineEndings(" ").Trim();
        if (flat.Length == 0)
        {
            flat = Nothing;
        }
        else if (flat.Length > RawCells)
        {
            int head = (RawCells - 1) / 2;
            flat = flat[..head] + "…" + flat[^(RawCells - 1 - head)..];
        }

        return $"Error: questions must be a list of 1 to {N(maxQuestions)} objects like {Shape}; got: {flat}";
    }

    /// <summary>What the sentence repeats when the arguments carry no <c>questions</c> at all: the names that were sent.</summary>
    public static string SentKeys(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string names = string.Join(", ", keys);
        return names.Length == 0 ? Nothing : "no questions key; the arguments were " + names;
    }

    /// <summary>More questions than the cap (<c>Ask max questions</c>) allows.</summary>
    /// <summary>
    /// A question's field sent at the top level beside a non-empty <c>questions</c> list (2026-11-15, the tool-arguments review's #110):
    /// it would be skipped, so the call is refused and the fields named, each echoed.
    /// </summary>
    public static string FieldsBesideQuestions(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string names = string.Join(", ", keys.Select(key => "'" + ArgumentText.Echo(key) + "'"));
        return $"Error: {names} sent beside questions; a question's fields go inside its item of questions. Nothing was asked.";
    }

    public static string TooManyQuestions(int maxQuestions) => $"Error: at most {N(maxQuestions)} questions per call; ask the most important ones first.";

    public static string NoQuestionText(int number) => $"Error: question {N(number)} has no question text.";

    public static string BadType(int number, string raw) => $"Error: question {N(number)}: type must be single or multi, not '{raw.Trim()}'.";

    /// <summary>Options that are no list of texts; the range is <see cref="AskUserTool.MinOptions"/> to the cap (<c>Ask max choices per question</c>).</summary>
    public static string BadOptions(int number, int maxChoices) => $"Error: question {N(number)}: options must be a list of {N(AskUserTool.MinOptions)} to {N(maxChoices)} short strings.";

    /// <summary>Too few or too many options for one question, the range as <see cref="BadOptions"/> names it.</summary>
    public static string OptionCount(int number, int count, int maxChoices) => $"Error: question {N(number)} has {N(count)} options; give {N(AskUserTool.MinOptions)} to {N(maxChoices)}.";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
