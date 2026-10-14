using NeonSidekick.Diagnostics;
using NeonSidekick.Prompts;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /prompts (2026-10-13) ─────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    // The loaded profile's saved prompts: the one made for its directory, or a new one once a profile switch moved it.
    private SavedPrompts? _savedPrompts;

    // A pick from /prompts' pane run on the watcher (under a reply): kept for the hook, which fills the draft after the pane phase,
    // where the live row's own emptying cannot reach it (OnMidTurnLineAsync). Watcher-only.
    private SavedPrompt? _promptPick;

    // While a typed line's pane phase runs under a reply: the live row holds that command (/prompts, or /memory before a switch to the
    // prompts pane), not a draft, so save draft finds none. Watcher-only.
    private bool _promptsRowIsCommand;

    /// <summary>
    /// <c>/prompts</c> (2026-10-13, the user's ask: messages sent often, picked onto the draft line), parsed by <see cref="PromptsCommand"/>:
    /// the bare word the pane (<see cref="SavedPromptsMenu"/>; printed with no pane), <c>&lt;n&gt;</c> that prompt on the draft line,
    /// <c>save &lt;text&gt;</c>, <c>remove &lt;n&gt;</c>, <c>list [--clear]</c>. The model has no tool for the list. At the idle line
    /// (<paramref name="onWatcher"/> false) a pick goes on the draft at once; on the watcher under a reply it is only kept
    /// (<see cref="_promptPick"/>) for the hook to put there after the pane phase, and the draft editor cannot run, so a prompt of several
    /// lines is not edited there. <paramref name="sink"/> is the flow sink under a reply, the transcript at the idle line.
    /// </summary>
    private async Task HandlePromptsAsync(string args, INoticeSink sink, CancellationToken cancellationToken, bool onWatcher = false)
    {
        var line = PromptsCommand.Parse(args);
        if (line.Error is { } error)
        {
            sink.Error(error);
            return;
        }

        var prompts = SavedPromptsNow();
        switch (line.Verb)
        {
            case PromptsVerb.Pane when _pane.Enabled:
            {
                var menu = new SavedPromptsMenu(
                    SavedPromptsNow,
                    sink,
                    _menuPane,
                    _input,
                    DraftForSaving,
                    onWatcher || _editDraft is null ? null : EditPromptInEditorAsync,
                    onWatcher ? SavedPromptText.EditorAtIdle : SavedPromptText.EditorUnavailable,
                    _time.LocalTimeZone);
                if (await menu.ShowAsync(cancellationToken).ConfigureAwait(false) is { } picked)
                {
                    if (onWatcher)
                    {
                        _promptPick = picked;
                    }
                    else
                    {
                        FillDraft(picked.Text, keepOld: true);
                    }
                }

                return;
            }

            case PromptsVerb.Pane:
            case PromptsVerb.List:
                foreach (string row in PromptsCommand.Printed(prompts.List()))
                {
                    sink.Notice(row);
                }

                return;
            case PromptsVerb.Fill:
                if (prompts.Resolve(line.Text) is { } prompt)
                {
                    FillDraft(prompt.Text, keepOld: true);
                }
                else
                {
                    Show(SavedPromptText.NotSaved(line.Text, prompts.List().Count), sink);
                }

                return;
            case PromptsVerb.Save:
                Show(PromptsCommand.SaveAnswer(prompts, line.Text), sink);
                return;
            case PromptsVerb.Remove:
                Show(PromptsCommand.RemoveAnswer(prompts, line.Text), sink);
                return;
            case PromptsVerb.ClearList:
                await ClearSavedPromptsAsync(prompts, sink, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>The loaded profile's saved prompts: the one made for its directory, or a new one once a profile switch moved it.</summary>
    private SavedPrompts SavedPromptsNow()
    {
        var prompts = _savedPrompts;
        if (prompts is null || !string.Equals(prompts.Directory, Path.GetFullPath(_settings.ProfileDirectory), StringComparison.OrdinalIgnoreCase))
        {
            prompts = new SavedPrompts(_settings.ProfileDirectory, _time);
            _savedPrompts = prompts;
        }

        return prompts;
    }

    /// <summary>
    /// <paramref name="text"/> on the chat line as the draft, the cursor at its end, in place of what was there (the user's pick): with
    /// <paramref name="keepOld"/> a draft that was not blank and was not this prompt goes to the input history first, memory only, as an
    /// ESC-cleared draft does (<see cref="InputLine.RememberDraft"/>), so Up brings it back. The text is loaded raw and editable, never
    /// collapsed to a paste token; a private-use character in it becomes its literal token (<see cref="PasteBlocks.Literals"/>). Not drawn:
    /// the idle line's next read draws it, and under a reply the watcher's aside redraws the live row after the hook's phase.
    /// </summary>
    private void FillDraft(string text, bool keepOld)
    {
        string old = _input.Chat.Text;
        if (keepOld && !string.IsNullOrWhiteSpace(old) && !string.Equals(_input.Pastes.Expand(old), text, StringComparison.Ordinal))
        {
            _input.RememberDraft(old);
        }

        _input.Chat.Load(_input.Pastes.Literals(text));
    }

    /// <summary>
    /// The chat draft as save draft keeps it: the pictures' tokens taken out (only the text is saved), then the text pastes and literal
    /// characters expanded (<see cref="PasteBlocks.Expand"/>); empty while a typed line's pane phase runs under a reply, the row holding
    /// that command rather than a draft (<see cref="_promptsRowIsCommand"/>).
    /// </summary>
    private (string Text, bool PicturesDropped) DraftForSaving()
    {
        if (_promptsRowIsCommand)
        {
            return ("", false);
        }

        string draft = _input.Chat.Text;
        var kept = new System.Text.StringBuilder(draft.Length);
        bool dropped = false;
        foreach (char c in draft)
        {
            if (PasteBlocks.IsToken(c) && _input.Pastes.ImageOf(c) is not null)
            {
                dropped = true;
                continue;
            }

            kept.Append(c);
        }

        return (_input.Pastes.Expand(kept.ToString()), dropped);
    }

    /// <summary>
    /// A saved prompt of several lines edited in the draft editor (2026-10-13): <see cref="HandleDraftAsync"/>'s round trip with the prompt
    /// written in the file first — the wait under the spinner (ESC stops waiting, the editor left open), the file read back and removed.
    /// The text it holds (<see cref="PasteText.Normalize"/>d), null when blank (kept unchanged), or the answer to show instead.
    /// </summary>
    private async Task<SavedPromptsMenu.EditorResult> EditPromptInEditorAsync(string text, CancellationToken cancellationToken)
    {
        if (_editDraft is null)
        {
            return new(null, "Error: " + SavedPromptText.EditorUnavailable);
        }

        string path;
        try
        {
            path = DraftFile.Create(Path.GetTempPath(), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, "Error: " + SavedPromptText.EditorFailed(ex.Message));
        }

        string editorCommand = _effective().DraftEditor;
        string name = DraftFile.Name(path);
        DiagnosticLog.Debug(AppCategory, DraftOpenedLogLine(name, editorCommand));
        try
        {
            if (await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(SavedPromptText.EditingLabel, async () =>
                {
                    await _editDraft(path, editorCommand, token).ConfigureAwait(false);
                    return true;
                }), KeySource.IsTurnCancel, cancellationToken, kind: KillScopeKind.Edit).ConfigureAwait(false))
            {
                return new(null, SavedPromptText.EditorCancelled);
            }

            string written = PasteText.Normalize(File.ReadAllText(path));
            return new(DraftFile.IsBlank(written) ? null : written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(null, "Error: " + SavedPromptText.EditorFailed(ex.Message));
        }
        finally
        {
            DraftFile.TryDelete(path);
        }
    }

    /// <summary><c>/prompts list --clear</c>: one yes/no, then every saved prompt taken off; none saved is said, asked nothing.</summary>
    private async Task ClearSavedPromptsAsync(SavedPrompts prompts, INoticeSink sink, CancellationToken cancellationToken)
    {
        int count = prompts.List().Count;
        if (count == 0)
        {
            sink.Notice(SavedPromptText.NoneSaved);
            return;
        }

        if (!await ConfirmAsync(SavedPromptText.ClearPrompt(count), cancellationToken).ConfigureAwait(false))
        {
            sink.Notice(KeptNotice);
            return;
        }

        var outcome = prompts.Clear(out int cleared);
        Show(SavedPromptText.Refusal(outcome) ?? (cleared == 0 ? SavedPromptText.NoneSaved : SavedPromptText.Cleared(cleared)), sink);
    }

    /// <summary>
    /// Under a reply, a line that puts a prompt on the draft by its number (<c>/prompts &lt;n&gt;</c>, <see cref="PromptsCommand.Fills"/>), on
    /// the watcher inside the hook, ahead of the policy: the prompt goes on the draft, and for a typed line the hook answers "kept"
    /// (false) so the live row does not empty itself over it, the typo intercept's way; a chord or a click is taken (true), or its word
    /// would come back as type-ahead. A number that names no prompt is its error, the line taken.
    /// </summary>
    private bool FillFromNumberMidTurn(string args, bool typed)
    {
        var prompts = SavedPromptsNow();
        var line = PromptsCommand.Parse(args);
        if (prompts.Resolve(line.Text) is not { } prompt)
        {
            Show(SavedPromptText.NotSaved(line.Text, prompts.List().Count), _flow);
            return true;
        }

        FillDraft(prompt.Text, keepOld: !typed);
        return !typed;
    }

    /// <summary>
    /// After a pane phase under a reply: a prompt picked on <c>/prompts</c>' pane (reached by its own line or by a switch from another
    /// pane) goes on the draft now, the hook answering "kept" for a typed line so the live row does not empty itself over it. True when
    /// the line is taken: with no pick, or for a chord or a click.
    /// </summary>
    private bool TakePromptPick(bool typed)
    {
        if (_promptPick is not { } picked)
        {
            return true;
        }

        _promptPick = null;
        FillDraft(picked.Text, keepOld: !typed);
        return !typed;
    }

    /// <summary>
    /// <c>/profile push|pull prompts [overwrite]</c> (2026-10-13, the memory section's shape): the source's saved prompts into the target's
    /// <c>prompts.json</c> — appended, a text it holds already skipped, or in place of its list — after a yes. A push reads the loaded
    /// profile's and writes the other's; a pull reads the other's and writes the loaded one's. A target that refuses (a newer version's
    /// file, one that cannot be read or written) changes nothing and says why.
    /// </summary>
    private async Task CopySavedPromptsSectionAsync(string home, string other, bool overwrite, bool pull, CancellationToken cancellationToken)
    {
        var theirs = new SavedPrompts(Profiles.Directory(home, other), _time);
        var ours = SavedPromptsNow();
        var prompts = (pull ? theirs : ours).List();
        if (prompts.Count == 0)
        {
            _flow.Notice(SavedPromptText.ProfileNothing(other, pull));
            return;
        }

        if (!await ConfirmAsync(SavedPromptText.ProfilePrompt(prompts.Count, other, overwrite, pull), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        var result = (pull ? ours : theirs).Import(prompts, overwrite);
        if (result.Outcome == SavedPromptOutcome.Done)
        {
            _flow.Notice(SavedPromptText.ProfileCopied(result, other, overwrite, pull));
        }
        else
        {
            _flow.Error(SavedPromptText.ProfileFailed(result.Outcome, other, pull));
        }
    }
}
