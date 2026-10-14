using System.Globalization;
using NeonSidekick.Prompts;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>What a <c>/prompts</c> line asks for (<see cref="PromptsCommand.Parse"/>).</summary>
public enum PromptsVerb
{
    /// <summary>The line is wrong: <see cref="PromptsCommandLine.Error"/> says why.</summary>
    Unknown,

    /// <summary>The saved prompts' pane (printed without one).</summary>
    Pane,

    /// <summary>Saved prompt <see cref="PromptsCommandLine.Number"/> put on the draft line.</summary>
    Fill,

    /// <summary><see cref="PromptsCommandLine.Text"/> saved.</summary>
    Save,

    /// <summary>The saved prompt <see cref="PromptsCommandLine.Text"/> numbers taken off.</summary>
    Remove,

    /// <summary>The saved prompts printed.</summary>
    List,

    /// <summary>Every saved prompt taken off, after a yes (headless: at once).</summary>
    ClearList,
}

/// <summary>A parsed <c>/prompts</c> line: the verb and what it takes, or the error to show.</summary>
public sealed record PromptsCommandLine(PromptsVerb Verb, int? Number = null, string Text = "", string? Error = null);

/// <summary>
/// <c>/prompts</c>' words (2026-10-13, the user's ask: the saved prompts, <c>/browser</c>'s saved-link verbs as the model), pure: the bare
/// word opens the pane, <c>&lt;n&gt;</c> puts that prompt on the draft line, <c>save &lt;text&gt;</c> (the rest of the line whole, line
/// breaks and a pasted block too), <c>remove &lt;n&gt;</c>, <c>list [--clear]</c>.
/// </summary>
public static class PromptsCommand
{
    public const string Word = "/prompts";

    public const string SaveWord = "save";
    public const string RemoveWord = "remove";
    public const string ListWord = "list";

    /// <summary>The switch after <c>list</c> that empties the list, <c>/browser list --clear</c>'s.</summary>
    public const string ClearSwitch = "--clear";

    /// <summary>The words in the order the completion offers them, with their notes.</summary>
    public static readonly IReadOnlyList<(string Word, string Note)> Words =
    [
        (SaveWord, "save a prompt: /prompts save <text>"),
        (ListWord, "print the saved prompts; /prompts list --clear empties the list"),
        (RemoveWord, "take a saved prompt off the list: /prompts remove <n>"),
    ];

    public static PromptsCommandLine Parse(string? args)
    {
        string text = (args ?? "").Trim();
        if (text.Length == 0)
        {
            return new PromptsCommandLine(PromptsVerb.Pane);
        }

        // The first word ends at any white space, so save's text may start on the next line.
        int gap = 0;
        while (gap < text.Length && !char.IsWhiteSpace(text[gap]))
        {
            gap++;
        }

        string first = text[..gap];
        string rest = text[gap..].Trim();
        switch (first.ToLowerInvariant())
        {
            case SaveWord when rest.Length == 0:
                return Error(SavedPromptText.Usage);
            case SaveWord:
                return new PromptsCommandLine(PromptsVerb.Save, Text: rest);
            case RemoveWord when rest.Length == 0:
                return Error(SavedPromptText.Usage);
            case RemoveWord:
                return new PromptsCommandLine(PromptsVerb.Remove, Text: rest);
            case ListWord when rest.Length == 0:
                return new PromptsCommandLine(PromptsVerb.List);
            case ListWord when rest.Equals(ClearSwitch, StringComparison.OrdinalIgnoreCase):
                return new PromptsCommandLine(PromptsVerb.ClearList);
            case ListWord:
                return Error(SavedPromptText.Usage);
        }

        if (text.All(char.IsAsciiDigit))
        {
            return new PromptsCommandLine(PromptsVerb.Fill, int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : int.MaxValue, text);
        }

        return Error(SavedPromptText.Usage);
    }

    /// <summary>Whether the line is a pane on the watcher under a reply: the pane itself, and <c>list --clear</c>'s question.</summary>
    public static bool IsPane(string? args) => Parse(args).Verb is PromptsVerb.Pane or PromptsVerb.ClearList;

    /// <summary>
    /// Whether the line puts a prompt on the draft by its number (<c>/prompts &lt;n&gt;</c>): under a reply it runs on the watcher ahead of
    /// the policy, so its fill lands before the live row would empty itself (<c>ChatScreen.OnMidTurnLineAsync</c>).
    /// </summary>
    public static bool Fills(string? args) => Parse(args).Verb == PromptsVerb.Fill;

    /// <summary>
    /// Whether a line holding a paste is <c>/prompts save</c> all the same (the user's pick, 2026-10-13): its expanded text parses as
    /// <c>/prompts</c> with <c>save</c> as the first word. Every other line holding a paste is a message, the input line's rule
    /// (<see cref="SubmittedLine.CommandText"/>); this one form is read as the command it reads as, so a long prompt pasted after
    /// <c>/prompts save </c> is saved rather than sent to the model or queued.
    /// </summary>
    public static bool SavesPaste(SubmittedLine? line)
    {
        if (line is null || line.CommandText is not null)
        {
            return false;   // no line, or one that is a command (or a message) by the usual rule
        }

        return SlashCommands.Parse(line.Text) is (SlashCommand.Prompts, var args) && Parse(args).Verb == PromptsVerb.Save;
    }

    /// <summary>The input line's argument list after <c>/prompts </c>: the verbs while the first word is typed, then <c>--clear</c> after <c>list</c>.</summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        if (argText.StartsWith(ListWord + " ", StringComparison.OrdinalIgnoreCase))
        {
            return MentionCompleter.Matches([new(ListWord + " " + ClearSwitch, "take every saved prompt off the list, after a yes")], argText);
        }

        return argText.Contains(' ', StringComparison.Ordinal) ? [] : MentionCompleter.Matches(Words.Select(w => new CompletionItem(w.Word, w.Note)).ToList(), argText);
    }

    /// <summary>
    /// A <c>/prompts</c> line headless (2026-10-13): no pane and no draft line, so the bare word and <c>list</c> print the list,
    /// <c>&lt;n&gt;</c> prints the prompt (headless <c>/rewind</c>'s way), <c>list --clear</c> runs at once (<c>/browser</c>'s headless rule),
    /// and <c>save</c> and <c>remove</c> work as on the screen. The lines to print, an error's led by <c>Error: </c>.
    /// </summary>
    public static IReadOnlyList<string> Headless(SavedPrompts prompts, string? args)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        var line = Parse(args);
        if (line.Error is { } error)
        {
            return ["Error: " + error];
        }

        switch (line.Verb)
        {
            case PromptsVerb.Save:
                return [SaveAnswer(prompts, line.Text)];
            case PromptsVerb.Remove:
                return [RemoveAnswer(prompts, line.Text)];
            case PromptsVerb.ClearList:
            {
                var outcome = prompts.Clear(out int cleared);
                return [SavedPromptText.Refusal(outcome) ?? (cleared == 0 ? SavedPromptText.NoneSaved : SavedPromptText.Cleared(cleared))];
            }

            case PromptsVerb.Fill:
                return prompts.Resolve(line.Text) is { } prompt
                    ? [SavedPromptText.HeadlessPrompt(line.Number!.Value, prompt)]
                    : [SavedPromptText.NotSaved(line.Text, prompts.List().Count)];
            default:
                return Printed(prompts.List());
        }
    }

    /// <summary>The list as lines: the count, then a numbered line each (<see cref="SavedPromptText.PrintedRow"/>); none says so.</summary>
    public static IReadOnlyList<string> Printed(IReadOnlyList<SavedPrompt> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list.Count == 0 ? [SavedPromptText.NoneSaved] : [SavedPromptText.Caption(list.Count), .. list.Select((p, i) => SavedPromptText.PrintedRow(i + 1, p))];
    }

    /// <summary><c>save &lt;text&gt;</c>'s answer, the screen's and headless.</summary>
    public static string SaveAnswer(SavedPrompts prompts, string text)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        var outcome = prompts.Add(text, out var saved);
        if (SavedPromptText.Refusal(outcome) is { } refused)
        {
            return refused;
        }

        int number = NumberOf(prompts, saved!);
        return outcome == SavedPromptOutcome.AlreadySaved ? SavedPromptText.AlreadySaved(saved!, number) : SavedPromptText.Saved(saved!, number);
    }

    /// <summary><c>remove &lt;n&gt;</c>'s answer, the screen's and headless.</summary>
    public static string RemoveAnswer(SavedPrompts prompts, string text)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        return prompts.Resolve(text) is { } prompt
            ? SavedPromptText.Refusal(prompts.Remove(prompt.Id), text) ?? SavedPromptText.Removed(prompt)
            : SavedPromptText.NotSaved(text, prompts.List().Count);
    }

    /// <summary>The prompt's number in the list now (from 1), 0 when it is not on it.</summary>
    public static int NumberOf(SavedPrompts prompts, SavedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(prompt);
        var list = prompts.List();
        int at = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id == prompt.Id)
            {
                at = i;
                break;
            }
        }

        return at + 1;
    }

    private static PromptsCommandLine Error(string error) => new(PromptsVerb.Unknown, Error: error);
}
