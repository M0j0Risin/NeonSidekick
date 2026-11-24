using System.Globalization;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The words <see cref="AskUserTool"/> speaks: the result the model reads (and, line by line, the
/// dim <c>🛠️</c> lines the transcript shows) and the error sentences for a call the tool cannot
/// put to the user. Pure statics, every string pinned.
/// </summary>
public static class AskUserText
{
    /// <summary>The tool's result — and the one transcript line — when the user declined the questions (Esc), or nothing could ask them. Pinned.</summary>
    public const string NotAnswered = "(questions not answered)";

    /// <summary>
    /// The most characters a decline's note or a typed answer keeps (2026-11-24, the last-7 review's #11): a paragraph or two, far
    /// under the page's 64 KB body cap, so one answer can't fill the model's context. Cut whole-character (<see cref="Files.TextCut.Safe(string, int)"/>).
    /// </summary>
    public const int NoteChars = 2_000;

    /// <summary>The joiner between an answer's choices: <c>cheese, olives</c>.</summary>
    public const string ChoiceSeparator = ", ";

    /// <summary>The tag after words the user typed (2026-11-24, the user's pick: trailing), so the model tells them from its own options. Pinned.</summary>
    public const string TypedTag = " (typed)";

    /// <summary>The answer of a question the user saw and left to the model (2026-11-24). Pinned.</summary>
    public const string Skipped = "(skipped)";

    /// <summary>
    /// One question's answer as the result prints it: the options picked in their order, then the typed words with <see cref="TypedTag"/>;
    /// <see cref="Skipped"/> for neither. The pane's Submit tab shows the same text, so what the user checks is what is sent.
    /// </summary>
    public static string AnswerText(AskAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Skipped)
        {
            return Skipped;
        }

        var parts = new List<string>(answer.Choices);
        if (answer.Typed is { } typed)
        {
            parts.Add(typed + TypedTag);
        }

        return string.Join(ChoiceSeparator, parts);
    }

    /// <summary>One line of the result: <c>Which color? — blue</c>.</summary>
    public static string AnswerLine(string question, string answer) => $"{question} — {answer}";

    /// <summary>The tool's result for answered questions: one <see cref="AnswerLine"/> per question in the order asked, joined by a line break.</summary>
    public static string ResultText(IReadOnlyList<AskAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        return string.Join('\n', answers.Select(a => AnswerLine(a.Question.Question, AnswerText(a))));
    }

    /// <summary>A decline (2026-11-24, E): <see cref="NotAnswered"/>, or with the user's note <c>(questions not answered: the note)</c>, on one line. Pinned.</summary>
    public static string Declined(string? note)
    {
        string? flat = note?.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrEmpty(flat) ? NotAnswered : $"(questions not answered: {flat})";
    }

    /// <summary>The tool's result for a reply: the answers' lines, or the decline (its note, if any).</summary>
    public static string ReplyText(AskReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return reply.Answers is { } answers ? ResultText(answers) : Declined(reply.DeclineNote);
    }

    /// <summary>
    /// The result for a call the repeat guard refused (2026-11-24, F, <c>Ask repeat guard</c> on): every question was declined earlier in
    /// this turn, so nothing was shown; the latest decline's note follows when it had one. Pinned.
    /// </summary>
    public static string AlreadyDeclined(string? note)
    {
        const string Sentence = "The user declined these questions earlier in this turn; go on without them.";
        string? flat = note?.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrEmpty(flat) ? Sentence : Sentence + " Their note: " + flat;
    }

    /// <summary>The log's category for the tool's own lines (the screen's other <c>ask_user</c> lines use the same).</summary>
    public const string LogCategory = "App";

    /// <summary>The log line for a refused repeat (2026-11-24, F). Pinned.</summary>
    public const string RepeatRefusedLogLine = "ask_user: repeat refused";

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

    public static string BadType(int number, string raw) => $"Error: question {N(number)}: type must be single, multi or text, not '{raw.Trim()}'.";

    /// <summary>
    /// Options that are no list of options; the range is <see cref="AskUserTool.MinOptions"/> to the cap (<c>Ask max choices per question</c>), each a
    /// string or an object with a label (2026-11-24: "short strings" misled a model that sent objects).
    /// </summary>
    public static string BadOptions(int number, int maxChoices) =>
        $"Error: question {N(number)}: options must be a list of {N(AskUserTool.MinOptions)} to {N(maxChoices)} options, each a short string or {{\"label\": \"…\", \"description\": \"…\"}}.";

    /// <summary>Options sent on a text question (2026-11-24): the model meant a choice. Pinned.</summary>
    public static string TextTakesNoOptions(int number) => $"Error: question {N(number)} is type text, which takes no options; use single or multi to offer choices.";

    /// <summary>
    /// A question with no options and no type but a list under another key (2026-11-24, the last-7 review's #20): the model misnamed its
    /// options, so it is told, never asked a free-text question it didn't mean. Pinned.
    /// </summary>
    public static string StrayList(int number, string key) =>
        $"Error: question {N(number)} sends a list as \"{key}\", which ask_user doesn't read; send the choices as \"options\", or leave the list out for a free-text question.";

    /// <summary>Too few or too many options for one question, the range as <see cref="BadOptions"/> names it.</summary>
    public static string OptionCount(int number, int count, int maxChoices) => $"Error: question {N(number)} has {N(count)} options; give {N(AskUserTool.MinOptions)} to {N(maxChoices)}.";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
