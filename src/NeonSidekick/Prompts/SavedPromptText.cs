using System.Globalization;
using NeonSidekick.UI;

namespace NeonSidekick.Prompts;

/// <summary>
/// The saved prompts' wording (2026-10-13, the user's ask: <see cref="SavedPrompts"/>): <c>/prompts</c>' pane, its verbs' answers and the
/// <c>/profile push|pull prompts</c> lines. An answer leading with <c>Error: </c> is shown as an error, the rest as a notice (the
/// <c>/browser</c> and <c>/youtube</c> answers' rule).
/// </summary>
public static class SavedPromptText
{
    private const string ErrorPrefix = "Error: ";

    // ── The pane ─────────────────────────────────────────────────────────────

    /// <summary>The pane's title: the toolbar item's 📣, as <c>⭐ Saved links</c> is the saved links'.</summary>
    public const string PaneTitle = "📣 Saved prompts";

    public const string AddButton = "⊕ add";
    public const char AddKey = 'a';
    public const string SaveDraftButton = "⇪ save draft";
    public const char SaveDraftKey = 's';
    public const string EditButton = "✎ edit";
    public const char EditKey = 'e';

    /// <summary>The remove button: the lists' <c>✖  remove</c> (two spaces: Windows Terminal draws ✖ two cells wide).</summary>
    public const string RemoveButton = YouTube.YouTubeText.RemoveButton;
    public const char RemoveKey = 'd';

    /// <summary>
    /// The pane's hint row while nothing is typed. Shift+↑↓ moves a prompt but has no room here (2026-10-16, the user's ask: every hint
    /// fits a 120-column window; INTERFACE.md's pane keys name it).
    /// </summary>
    public const string PaneKeys = "Enter = use · a = add · s = save draft · e = edit · d = remove · c = clear · " + MenuFilter.TypeOrShiftAndCloseKeys;

    /// <summary>The one dim row of an empty list, under the same buttons, so add and save draft work on it.</summary>
    public const string EmptyRow = "No saved prompts yet: a adds one, and s saves the draft.";

    /// <summary>No saved prompts, on the transcript (a printed list, a clear of none).</summary>
    public const string NoneSaved = "No saved prompts yet: /prompts save <text> adds one, and the /prompts pane saves the draft.";

    /// <summary>The count over the pane's rows and the printed list.</summary>
    public static string Caption(int count) =>
        $"{Prompts(count)}; Enter puts one on the draft line.";

    /// <summary>The widest a row's first line is drawn before it is cut with an ellipsis; the footer shows more.</summary>
    public const int RowWidth = 100;

    /// <summary>
    /// A prompt as the pane's row: its number in the whole list, right-aligned to <paramref name="numberWidth"/> digits, its first line
    /// cut to <see cref="RowWidth"/> cells, and <c>+3 lines</c> when it has more. Plain text: the pane escapes it.
    /// </summary>
    public static string PaneRow(int number, SavedPrompt prompt, int numberWidth)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        string at = number.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, numberWidth)) + ". ";
        return at + FirstLine(prompt, RowWidth);
    }

    /// <summary>The digits the largest number in a list of <paramref name="count"/> takes, so the rows' texts line up.</summary>
    public static int NumberWidth(int count) => Math.Max(1, count).ToString(CultureInfo.InvariantCulture).Length;

    /// <summary>
    /// The footer under the list for <paramref name="prompt"/>: its text from the start (the band is <see cref="MenuPane.FooterRows"/> rows:
    /// a one-line prompt wraps, a longer one keeps its lines, <see cref="MenuFooter.KeepLines"/>), then <c>saved 2026-10-13 18:00 · 3 lines</c>.
    /// </summary>
    public static MenuFooter Footer(SavedPrompt prompt, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(zone);
        int lines = LineCount(prompt.Text);
        return new MenuFooter(prompt.Text, "saved " + Sessions.SessionText.Moment(prompt.Added, zone) + " · " + Lines(lines)) { KeepLines = lines > 1 };
    }

    /// <summary>Whether <paramref name="prompt"/> stays under <paramref name="filter"/>: its text holds it.</summary>
    public static bool Matches(string filter, SavedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return MenuFilter.Matches(filter, prompt.Text, null);
    }

    /// <summary>A line of the printed list (<c>/prompts list</c>, the pane off, headless): <c>3. Summarize this … +2 lines</c>.</summary>
    public static string PrintedRow(int number, SavedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return number.ToString(CultureInfo.InvariantCulture) + ". " + FirstLine(prompt, RowWidth);
    }

    /// <summary>The prompt's first line cut to <paramref name="width"/> cells, with <c>+2 lines</c> after it when there are more.</summary>
    public static string FirstLine(SavedPrompt prompt, int width)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        int newline = prompt.Text.IndexOf('\n', StringComparison.Ordinal);
        string first = newline < 0 ? prompt.Text : prompt.Text[..newline];
        int more = LineCount(prompt.Text) - 1;
        return ScreenPane.Fit(first, Math.Max(1, width)) + (more > 0 ? " +" + Lines(more) : "");
    }

    /// <summary>The lines in a text: one more than its line breaks.</summary>
    public static int LineCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Count(c => c == '\n') + 1;
    }

    // ── The answers ──────────────────────────────────────────────────────────

    /// <summary>How a prompt is named in a sentence: its first line, short, in quotes.</summary>
    public static string Name(SavedPrompt prompt) => "\"" + FirstLine(prompt, 40) + "\"";

    public static string Saved(SavedPrompt prompt, int number) =>
        $"Saved prompt {number.ToString(CultureInfo.InvariantCulture)}: {Name(prompt)}.";

    public static string AlreadySaved(SavedPrompt prompt, int number) =>
        $"That prompt is saved already, as number {number.ToString(CultureInfo.InvariantCulture)}.";

    public static string Removed(SavedPrompt prompt) => $"Removed {Name(prompt)} from the saved prompts.";

    public static string Edited(SavedPrompt prompt) => $"Changed {Name(prompt)}.";

    public const string Unchanged = "The prompt is unchanged.";

    /// <summary>The question before the pane's remove.</summary>
    public static string RemovePrompt(SavedPrompt prompt) => $"📣 Remove {Name(prompt)} from the saved prompts?";

    /// <summary>The question before the pane's clear all and <c>/prompts list --clear</c>.</summary>
    public static string ClearPrompt(int count) => count == 1 ? "📣 Remove the one saved prompt?" : $"📣 Remove all {Prompts(count)}?";

    /// <summary>Every saved prompt taken off.</summary>
    public static string Cleared(int count) => $"Removed {Prompts(count)}.";

    /// <summary>What names no saved prompt: the list's size with the number given.</summary>
    public static string NotSaved(string? text, int count) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? ErrorPrefix + $"There is no saved prompt {text!.Trim()}: " + (count == 0 ? "none is saved yet." : $"the list runs 1 to {count.ToString(CultureInfo.InvariantCulture)}.")
            : ErrorPrefix + $"\"{text}\" is no saved prompt: give its number in /prompts list.";

    /// <summary>Save draft on a blank draft (or one of pictures alone), or on the row a typed <c>/prompts</c> holds under a reply.</summary>
    public const string DraftEmpty = "The draft is empty. To save what you are typing, open this pane with Ctrl+Alt+W or the toolbar's 📣, which keep the draft.";

    /// <summary>A saved draft that held pictures or PDFs (named since 2026-10-14, the PDF plan's gap 53): the text alone is kept.</summary>
    public const string PicturesNotKept = "The draft's pictures and PDFs are not saved, only its text.";

    /// <summary>A multi-line prompt's edit under a reply: the editor waits for the idle line.</summary>
    public const string EditorAtIdle = "A prompt of several lines is edited in the draft editor, which opens at the idle line; edit it once the reply ends.";

    /// <summary>A multi-line prompt's edit with no editor to open.</summary>
    public const string EditorUnavailable = "A prompt of several lines is edited in the draft editor, which is not available here.";

    /// <summary>The spinner's label while the draft editor has a prompt open (<c>/draft</c>'s, with the prompt's words).</summary>
    public const string EditingLabel = "editing the prompt in your editor…  save and close to keep it   Esc = stop waiting";

    public static string EditorFailed(string detail) => $"Could not open the prompt in the editor: {detail}";

    public const string EditorCancelled = "Stopped waiting for the editor; the prompt is unchanged.";

    /// <summary>The list could not be read or written (the log has why).</summary>
    public const string SaveFailed = "Could not write the saved prompts (" + SavedPrompts.FileName + "); the log says why.";

    /// <summary>The file is a newer NeonSidekick's: left alone, nothing changed.</summary>
    public const string NewerFile = SavedPrompts.FileName + " was saved by a newer NeonSidekick; update to change it.";

    public const string Empty = "There is nothing to save: the prompt is blank.";

    public static readonly string TooLong =
        $"The prompt is too long to save: at most {SavedPrompts.MaxLength.ToString("N0", CultureInfo.InvariantCulture)} characters.";

    public const string Duplicate = "Another saved prompt has that text already.";

    /// <summary>A refusal's answer, led by <c>Error: </c>; null for an outcome that is no refusal.</summary>
    public static string? Refusal(SavedPromptOutcome outcome, string? text = null, int count = 0) => outcome switch
    {
        SavedPromptOutcome.Empty => ErrorPrefix + Empty,
        SavedPromptOutcome.TooLong => ErrorPrefix + TooLong,
        SavedPromptOutcome.Duplicate => ErrorPrefix + Duplicate,
        SavedPromptOutcome.Newer => ErrorPrefix + NewerFile,
        SavedPromptOutcome.Failed => ErrorPrefix + SaveFailed,
        SavedPromptOutcome.NotSaved => NotSaved(text, count),
        _ => null,
    };

    /// <summary><c>/prompts</c>' usage.</summary>
    public const string Usage = "Usage: /prompts · /prompts <n> puts a prompt on the draft line · /prompts save <text> · /prompts remove <n> · /prompts list [--clear]";

    /// <summary>The pane's add or edit with no input row to type on.</summary>
    public const string NeedsInput = "Typing a prompt needs the input row: /prompts save <text> adds one.";

    /// <summary>Headless <c>/prompts &lt;n&gt;</c>: no draft line, so the prompt is printed (as headless <c>/rewind</c> prints its message).</summary>
    public static string HeadlessPrompt(int number, SavedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return $"prompt {number.ToString(CultureInfo.InvariantCulture)}: {prompt.Text}";
    }

    // ── /profile push|pull prompts ───────────────────────────────────────────
    // The memory section's shape (ChatScreen.ProfileMemory*), with the pane's 📣 leading each line.

    /// <summary><c>prompts</c> with nothing to copy: <c>(📣 nothing to copy: this profile has no saved prompts)</c>, or <c>"work"</c> on a pull.</summary>
    public static string ProfileNothing(string profile, bool pull) =>
        $"({Glyph}nothing to copy: {(pull ? $"\"{profile}\"" : "this profile")} has no saved prompts)";

    /// <summary>The question before a <c>prompts</c> copy: <c>📣 Copy 3 saved prompts into "work"?</c>, or a replace, or the pull's.</summary>
    public static string ProfilePrompt(int count, string profile, bool overwrite, bool pull) => Glyph + (pull, overwrite) switch
    {
        (false, false) => $"Copy {Prompts(count)} into \"{profile}\"?",
        (false, true) => $"Replace \"{profile}\"'s saved prompts with these {Prompts(count)}?",
        (true, false) => $"Copy {Prompts(count)} from \"{profile}\" into this profile?",
        (true, true) => $"Replace this profile's saved prompts with \"{profile}\"'s {Prompts(count)}?",
    };

    /// <summary>A <c>prompts</c> copy done: <c>(📣 3 saved prompts copied into "work", 1 already there)</c>; an overwrite <c>(📣 replaced "work"'s saved prompts with 3 saved prompts)</c>.</summary>
    public static string ProfileCopied(SavedPromptImport result, string profile, bool overwrite, bool pull)
    {
        ArgumentNullException.ThrowIfNull(result);
        string done = (pull, overwrite) switch
        {
            (false, false) => $"{Prompts(result.Added)} copied into \"{profile}\"",
            (false, true) => $"replaced \"{profile}\"'s saved prompts with {Prompts(result.Added)}",
            (true, false) => $"{Prompts(result.Added)} copied from \"{profile}\"",
            (true, true) => $"replaced this profile's saved prompts with {Prompts(result.Added)} from \"{profile}\"",
        };
        string skipped = result.Skipped == 0 ? "" : $", {result.Skipped.ToString(CultureInfo.InvariantCulture)} already there";
        return "(" + Glyph + done + skipped + ")";
    }

    /// <summary>A copy that changed nothing: the target's file refused it (a newer version's, one that cannot be read or written).</summary>
    public static string ProfileFailed(SavedPromptOutcome outcome, string profile, bool pull) =>
        $"Could not copy into {(pull ? "this profile" : $"\"{profile}\"")}'s saved prompts: " + (Refusal(outcome) is { } refusal ? refusal[ErrorPrefix.Length..] : SaveFailed);

    // The glyph that leads the copy's lines, the pane's.
    private const string Glyph = "📣 ";

    private static string Prompts(int count) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} saved prompt{(count == 1 ? "" : "s")}";

    private static string Lines(int count) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} line{(count == 1 ? "" : "s")}";
}
