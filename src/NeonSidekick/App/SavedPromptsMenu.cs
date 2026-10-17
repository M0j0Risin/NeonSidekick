using NeonSidekick.Prompts;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/prompts</c> (2026-10-13, the user's ask: messages sent often, kept like the saved links and picked onto the draft line): the
/// profile's saved prompts (<see cref="SavedPrompts"/>) on a <see cref="MenuPage"/>, a numbered row each (its first line,
/// <see cref="SavedPromptText.PaneRow"/>), the highlighted one's text in the footer, typing filters (Shift for a letter a button has).
/// Enter or a double-click closes the pane and returns the prompt to send (2026-10-16, the user's ask: a pick is sent as if typed, queued
/// under a reply; it went on the draft line until then); a single click only moves the highlight. The buttons (<see cref="Buttons"/>, a
/// click or the key): to draft (the old pick, the prompt on the draft line to edit first, <see cref="SavedPromptPick.ToDraft"/>), add
/// (typed on the input row, one line), save draft (the chat draft as it stands, line breaks kept and pictures dropped; the screen hands it
/// in), edit (a one-line prompt on the input row, a longer one in the draft editor, which closes the pane around its wait and opens it
/// again on the same prompt), remove (after a yes; <c>r</c> since 2026-10-16) and clear all (after a yes, then the pane closes with its
/// answer on the transcript). The order is the user's: <see cref="MenuPage.Moves"/> while nothing is typed; Delete removes, while a filter
/// shows part of the list too (<see cref="MenuPage.Removes"/>, 2026-10-16). An empty list shows one dim row under the same buttons, so add
/// and save draft work on it. <c>BrowserLinksMenu</c>'s shape throughout.
/// </summary>
internal sealed class SavedPromptsMenu
{
    /// <summary>The page's buttons in the order drawn: to draft (2026-10-16), add, save draft, edit, remove, clear all.</summary>
    public static readonly IReadOnlyList<MenuButton> Buttons =
    [
        new(SavedPromptText.ToDraftButton, SavedPromptText.ToDraftKey),
        new(SavedPromptText.AddButton, SavedPromptText.AddKey),
        new(SavedPromptText.SaveDraftButton, SavedPromptText.SaveDraftKey),
        new(SavedPromptText.EditButton, SavedPromptText.EditKey),
        new(SavedPromptText.RemoveButton, SavedPromptText.RemoveKey),
        new(QueueMenu.ClearAllButton, QueueMenu.ClearAllKey),
    ];

    private const int ToDraft = 0;
    private const int Add = 1;
    private const int SaveDraft = 2;
    private const int Edit = 3;
    private const int Remove = 4;
    private const int ClearAll = 5;

    private const string ErrorPrefix = "Error: ";

    private readonly Func<SavedPrompts> _prompts;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly InputLine? _input;
    private readonly Func<(string Text, bool PicturesDropped)> _draft;
    private readonly Func<string, CancellationToken, Task<EditorResult>>? _editInEditor;
    private readonly string _editorRefusal;
    private readonly TimeZoneInfo _zone;

    /// <param name="prompts">The loaded profile's saved prompts, read when the pane opens and after every act.</param>
    /// <param name="transcript">Where the lines outside the pane go (clear all's, which closes it).</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="input">The input row a prompt is typed on; null: add and a one-line edit say so.</param>
    /// <param name="draft">The chat draft for save draft: its text with the pastes expanded and the pictures dropped, and whether any were.</param>
    /// <param name="editInEditor">A multi-line prompt's round trip through the draft editor; null where it cannot run (under a reply, no editor).</param>
    /// <param name="editorRefusal">What a multi-line edit says when <paramref name="editInEditor"/> is null.</param>
    /// <param name="zone">The zone the footer's date is shown in.</param>
    public SavedPromptsMenu(
        Func<SavedPrompts> prompts,
        INoticeSink transcript,
        MenuPane pane,
        InputLine? input,
        Func<(string Text, bool PicturesDropped)> draft,
        Func<string, CancellationToken, Task<EditorResult>>? editInEditor,
        string editorRefusal,
        TimeZoneInfo? zone = null)
    {
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _input = input;
        _draft = draft ?? throw new ArgumentNullException(nameof(draft));
        _editInEditor = editInEditor;
        _editorRefusal = editorRefusal ?? throw new ArgumentNullException(nameof(editorRefusal));
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>What the draft editor's round trip came back with: the text written (null: unchanged or blank), or the line to show instead.</summary>
    public sealed record EditorResult(string? Text, string? Answer = null);

    /// <summary>
    /// A pick off the pane (2026-10-16): <see cref="Prompt"/> to send (Enter or a double-click), or with <see cref="ToDraft"/> to put on the
    /// draft line (the to-draft button, <c>d</c>).
    /// </summary>
    public sealed record SavedPromptPick(SavedPrompt Prompt, bool ToDraft);

    /// <summary>Shows the list until Esc (null) or a pick (the prompt and what to do with it, the pane closed).</summary>
    public async Task<SavedPromptPick?> ShowAsync(CancellationToken cancellationToken)
    {
        string? on = null;      // the prompt the cursor follows, by id
        int stood = 0;          // the row it stood on, for when that prompt is gone
        string filter = "";
        string? answer = null;  // an answer to show once the pane is open again (the editor's round trip closed it)
        try
        {
            while (true)
            {
                var prompts = _prompts().List();
                bool empty = prompts.Count == 0;
                var shown = Enumerable.Range(0, prompts.Count).Where(i => SavedPromptText.Matches(filter, prompts[i])).ToList();
                int numberWidth = SavedPromptText.NumberWidth(prompts.Count);
                List<string> rows = empty ? [Theme.DimMarkup(SavedPromptText.EmptyRow)]
                    : shown.Count == 0 ? [MenuFilter.NoMatchRow(filter)]
                    : shown.Select(i => Markup.Escape(SavedPromptText.PaneRow(i + 1, prompts[i], numberWidth))).ToList();
                int at = on is null ? -1 : shown.FindIndex(i => prompts[i].Id == on);
                int cursor = at >= 0 ? at : Math.Clamp(stood, 0, Math.Max(0, rows.Count - 1));
                var page = new MenuPage(SavedPromptText.PaneTitle, rows, MenuFilter.Hint(SavedPromptText.PaneKeys, filter))
                {
                    Buttons = Buttons,
                    Filter = empty ? null : filter,
                    Caption = empty ? null : MenuFilter.CaptionOrNull(filter, shown.Count, prompts.Count) ?? SavedPromptText.PaneCaption(prompts.Count),
                    Footer = (_, row) => !empty && row < shown.Count ? SavedPromptText.Footer(prompts[shown[row]], _zone) : null,
                    Moves = !empty && filter.Length == 0,   // the order is the whole list's: no moves while a filter shows part of it
                    Removes = !empty && shown.Count > 0,     // Delete removes a filtered row too (2026-10-16, the user's ask)
                };
                if (answer is not null)
                {
                    Say(answer);
                    answer = null;
                }

                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return null;
                }

                if (pick.Filter is { } typed)
                {
                    filter = typed;
                    on = null;
                    stood = 0;
                    continue;
                }

                var prompt = !empty && pick.Row >= 0 && pick.Row < shown.Count ? prompts[shown[pick.Row]] : null;
                stood = Math.Max(0, pick.Row);
                on = prompt?.Id ?? on;
                if (pick.Button == Add)
                {
                    if (await TypeAsync(page, Math.Max(0, pick.Row), "", cancellationToken).ConfigureAwait(false) is { Length: > 0 } added)
                    {
                        on = Save(added) ?? on;
                    }

                    continue;
                }

                if (pick.Button == SaveDraft)
                {
                    var (text, picturesDropped) = _draft();
                    if (SavedPrompts.Clean(text).Length == 0)
                    {
                        _pane.Error(SavedPromptText.DraftEmpty);
                        continue;
                    }

                    on = Save(text) ?? on;
                    if (picturesDropped)
                    {
                        _pane.Notice(SavedPromptText.PicturesNotKept);
                    }

                    continue;
                }

                if (pick.Button == ClearAll)
                {
                    if (empty)
                    {
                        continue;
                    }

                    if (await ConfirmAsync(SavedPromptText.ClearPrompt(prompts.Count), cancellationToken).ConfigureAwait(false))
                    {
                        var outcome = _prompts().Clear(out int cleared);
                        Tell(SavedPromptText.Refusal(outcome) ?? (cleared == 0 ? SavedPromptText.NoneSaved : SavedPromptText.Cleared(cleared)));
                        return null;
                    }

                    _pane.Notice(ChatScreen.KeptNotice);
                    continue;
                }

                if (prompt is null)
                {
                    continue;   // the empty list's row, the no-match row
                }

                if (pick.Button == ToDraft)
                {
                    return new SavedPromptPick(prompt, ToDraft: true);
                }

                if (pick.Move != 0)
                {
                    // A move: silent, the row going with the cursor.
                    if (SavedPromptText.Refusal(_prompts().Move(prompt.Id, pick.Move)) is { } refused)
                    {
                        Say(refused);
                    }
                }
                else if (pick.Button == Edit)
                {
                    answer = await EditAsync(page, pick.Row, prompt, cancellationToken).ConfigureAwait(false);
                }
                else if (pick.Button == Remove || pick.Remove)
                {
                    if (await ConfirmAsync(SavedPromptText.RemovePrompt(prompt), cancellationToken).ConfigureAwait(false))
                    {
                        Say(SavedPromptText.Refusal(_prompts().Remove(prompt.Id)) ?? SavedPromptText.Removed(prompt));
                        on = null;   // the row it stood on: the prompt after it, or the one before at the end
                    }
                    else
                    {
                        _pane.Notice(ChatScreen.KeptNotice);
                    }
                }
                else
                {
                    return new SavedPromptPick(prompt, ToDraft: false);   // Enter or a double-click: sent (2026-10-16; to the draft line until then)
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    // A text saved, its answer on the status line; the id the cursor goes to (the new prompt, or the one saved already), or null.
    private string? Save(string text)
    {
        var prompts = _prompts();
        string answer = PromptsCommand.SaveAnswer(prompts, text);
        Say(answer);
        string clean = SavedPrompts.Clean(text);
        return prompts.List().FirstOrDefault(p => p.Text == clean)?.Id;
    }

    // The edit: a one-line prompt on the input row, a longer one in the draft editor. The answer for the status line, or null.
    private async Task<string?> EditAsync(MenuPage page, int row, SavedPrompt prompt, CancellationToken cancellationToken)
    {
        string? text;
        if (SavedPromptText.LineCount(prompt.Text) == 1)
        {
            text = await TypeAsync(page, row, prompt.Text, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                return null;   // ESC on the input row: nothing to say
            }
        }
        else if (_editInEditor is null)
        {
            return ErrorPrefix + _editorRefusal;
        }
        else
        {
            // The round trip's spinner is on the transcript's row: the pane closes around it and opens again at the loop's top.
            _pane.Close();
            var result = await _editInEditor(prompt.Text, cancellationToken).ConfigureAwait(false);
            if (result.Answer is { } said)
            {
                return said;
            }

            text = result.Text;
            if (text is null)
            {
                return SavedPromptText.Unchanged;
            }
        }

        if (SavedPrompts.Clean(text) == prompt.Text)
        {
            return SavedPromptText.Unchanged;
        }

        var outcome = _prompts().Edit(prompt.Id, text);
        return SavedPromptText.Refusal(outcome) ?? SavedPromptText.Edited(prompt with { Text = SavedPrompts.Clean(text) });
    }

    /// <summary>A line typed on the input row under the list (<paramref name="initial"/> to start from); null on ESC, or with no input row (said so).</summary>
    private async Task<string?> TypeAsync(MenuPage page, int row, string initial, CancellationToken cancellationToken)
    {
        if (_input is null)
        {
            _pane.Error(SavedPromptText.NeedsInput);
            return null;
        }

        var typed = await _pane.EditAsync(page with { Hint = SettingsMenu.EditKeys }, row, _input, initial, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        return typed is InputResult.Submitted { Text: var text } ? text.Trim() : null;
    }

    /// <summary>An answer on the transcript, for one the pane closes after: an error as one, anything else a notice.</summary>
    private void Tell(string result)
    {
        if (result.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            _transcript.Error(result[ErrorPrefix.Length..]);
        }
        else
        {
            _transcript.Notice(result);
        }
    }

    /// <summary>An answer on the pane's status line: an error as one, anything else a notice.</summary>
    private void Say(string result)
    {
        if (result.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            _pane.Error(result[ErrorPrefix.Length..]);
        }
        else
        {
            _pane.Notice(result);
        }
    }

    /// <summary>The yes/no page kept under the list (<see cref="SettingsMenu.ConfirmAsync"/>'s rows, keys and hotkeys, the cursor on No): true for Enter on Yes alone.</summary>
    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        var page = new MenuPage(question, SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys) { Hotkeys = SettingsMenu.ConfirmHotkeys };
        var picked = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
        return picked is { Row: 1 };
    }
}
