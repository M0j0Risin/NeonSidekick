using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Prompts;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

// ── The Web UI's sheet: prompts, memories and the message history (2026-10-16, the user's ask) ──
// The page's 📣 sheet has three tabs: the saved prompts (save the draft, delete one), the memories (the same) and the message history (↑/↓
// on the page walk it too, and a clear all). Every hook here runs on a Kestrel thread against state that is safe there — the prompts'
// static lock, the memory store's own lock, the history's lock in InputLine, the session store's — and never writes the transcript: the
// PC hears of a change on its hint row (FlashPc), and a cleared history's line is queued for the screen's task (_webUi.Notice).

internal sealed partial class ChatScreen
{
    /// <summary>The page's draft saved as a prompt: the <c>/prompts save</c> answer; its refusal a 422.</summary>
    private WebReply WebSavePrompt(string text, IPAddress? remote)
    {
        string answer = PromptsCommand.SaveAnswer(SavedPromptsNow(), text, out bool refused, out int number);
        if (refused)
        {
            return new WebReply(WebOutcome.Refused, answer);
        }

        _webUi.FlashPc(WebUiText.PromptSavedFlash(number));
        DiagnosticLog.Info(WebUiCategory, WebUiText.PromptSavedLogLine(number, remote));
        return new WebReply(WebOutcome.Ok, answer);
    }

    /// <summary>A saved prompt deleted by its id: the sentence names it, so it is found first; NotFound once it is gone.</summary>
    private WebReply WebDeletePrompt(string id, IPAddress? remote)
    {
        var prompts = SavedPromptsNow();
        if (prompts.List().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal)) is not { } prompt)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.PromptGone);
        }

        if (SavedPromptText.Refusal(prompts.Remove(prompt.Id)) is { } refused)
        {
            return new WebReply(WebOutcome.Refused, refused);
        }

        _webUi.FlashPc(WebUiText.PromptDeletedFlash);
        DiagnosticLog.Info(WebUiCategory, WebUiText.PromptDeletedLogLine(remote));
        return new WebReply(WebOutcome.Ok, SavedPromptText.Removed(prompt));
    }

    /// <summary>The memories as the sheet's 💾 tab lists them: the PC pane's order, the turned-off ones marked, and whether the mode is off.</summary>
    private WebMemories WebMemoriesNow() =>
        new([.. _memory.EntriesSnapshot().Select(e => new WebMemory(e.Text, e.Disabled))], !MemoryMode.Enabled(_effective()), Memory.MemoryStore.MaxTextLength);

    /// <summary>
    /// The page's draft saved as a memory: <c>/remember</c>'s rule and words (refused while <i>Memory mode</i> is off; a turned-off one turned
    /// on again), an empty draft answered as the page's own empty draft rather than with <c>/remember</c>'s usage line.
    /// </summary>
    private WebReply WebSaveMemory(string text, IPAddress? remote)
    {
        if (!MemoryMode.Enabled(_effective()))
        {
            return new WebReply(WebOutcome.Refused, MemoryOffNotice);
        }

        var result = _memory.Add(text, enableDisabled: true);
        if (result.Outcome == Memory.MemoryAddOutcome.Empty)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.DraftEmpty);
        }

        var (answer, error) = RememberAnswer(result);
        if (error)
        {
            return new WebReply(WebOutcome.Refused, answer);
        }

        if (result.Outcome != Memory.MemoryAddOutcome.Duplicate)
        {
            _webUi.FlashPc(WebUiText.MemorySavedFlash);
            DiagnosticLog.Info(WebUiCategory, WebUiText.MemorySavedLogLine(remote));
        }

        return new WebReply(WebOutcome.Ok, answer);
    }

    /// <summary>A memory deleted by its text (memories have no ids; the store matches ignoring case), in any mode, as the PC pane's forget.</summary>
    private WebReply WebDeleteMemory(string text, IPAddress? remote)
    {
        var memory = _memory;
        bool removed;
        try
        {
            removed = memory.Remove(text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error(WebUiCategory, "Web UI: a memory could not be removed: " + e.Message, e);
            return new WebReply(WebOutcome.Refused, WebUiText.MemoryNotRemoved);
        }

        if (!removed)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.MemoryGone);
        }

        _webUi.FlashPc(WebUiText.MemoryDeletedFlash);
        DiagnosticLog.Info(WebUiCategory, WebUiText.MemoryDeletedLogLine(remote));
        return new WebReply(WebOutcome.Ok, MemoryMenu.RemovedNotice(text));
    }

    /// <summary>
    /// A memory's text changed from the page (2026-10-18, the user's ask), in any mode, as the PC pane's edit: <see cref="Memory.MemoryStore.Edit"/>'s
    /// outcome in the pane's words — its date and its off mark kept, a duplicate or an empty text refused, a memory gone meanwhile NotFound.
    /// </summary>
    private WebReply WebEditMemory(string old, string text, IPAddress? remote)
    {
        Memory.MemoryEditResult result;
        try
        {
            result = _memory.Edit(old, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error(WebUiCategory, "Web UI: a memory could not be edited: " + e.Message, e);
            return new WebReply(WebOutcome.Refused, MemoryMenu.ChangeFailedError(e.Message));
        }

        switch (result.Outcome)
        {
            case Memory.MemoryEditOutcome.Edited:
                _webUi.FlashPc(WebUiText.MemoryEditedFlash);
                DiagnosticLog.Info(WebUiCategory, WebUiText.MemoryEditedLogLine(remote));
                return new WebReply(WebOutcome.Ok, MemoryMenu.EditedNotice(result.Text));
            case Memory.MemoryEditOutcome.Duplicate:
                return new WebReply(WebOutcome.Refused, MemoryMenu.DuplicateError(result.Text));
            case Memory.MemoryEditOutcome.Missing:
                return new WebReply(WebOutcome.NotFound, WebUiText.MemoryGone);
            case Memory.MemoryEditOutcome.Empty:
                return new WebReply(WebOutcome.Refused, WebUiText.MemoryEditEmpty);
            default:
                return new WebReply(WebOutcome.Ok, KeptNotice);   // unchanged
        }
    }

    /// <summary>
    /// A memory turned off or on from the page (2026-10-18, the user's ask), in any mode, as the PC pane's toggle: the state asked for, so a
    /// repeat answers as the change did, without the flash or the audit; a memory gone meanwhile is NotFound.
    /// </summary>
    private WebReply WebToggleMemory(string text, bool off, IPAddress? remote)
    {
        var memory = _memory;
        bool changed;
        try
        {
            changed = memory.SetDisabled(text, off);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error(WebUiCategory, "Web UI: a memory could not be turned " + (off ? "off" : "on") + ": " + e.Message, e);
            return new WebReply(WebOutcome.Refused, MemoryMenu.ChangeFailedError(e.Message));
        }

        string normalized = Memory.MemoryStore.Normalize(text);
        if (memory.EntriesSnapshot().FirstOrDefault(e => string.Equals(e.Text, normalized, StringComparison.OrdinalIgnoreCase)) is not { } entry || entry.Disabled != off)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.MemoryGone);
        }

        if (changed)
        {
            _webUi.FlashPc(off ? WebUiText.MemoryDisabledFlash : WebUiText.MemoryEnabledFlash);
            DiagnosticLog.Info(WebUiCategory, WebUiText.MemoryToggledLogLine(off, remote));
        }

        return new WebReply(WebOutcome.Ok, off ? MemoryMenu.DisabledNotice(entry.Text) : MemoryMenu.EnabledNotice(entry.Text));
    }

    /// <summary>
    /// The message history as the page gets it: the drafts the PC set aside left out (ESC'd or replaced, never stored, so never sent either),
    /// and the lines holding a paste or picture token (whose block is the PC's alone).
    /// </summary>
    private IReadOnlyList<string> WebHistoryNow() => [.. _input.HistorySnapshot(includeDrafts: false).Where(line => !line.Any(PasteBlocks.IsToken))];

    /// <summary>The 🕘 tab's clear all: <c>/msgclear</c>'s wipe, the page's two taps its yes; the PC's line queued for its own task.</summary>
    private WebReply WebClearHistory(IPAddress? remote)
    {
        int count = ClearMessageHistory();
        if (count == 0)
        {
            return new WebReply(WebOutcome.Ok, MsgClearNothingNotice);
        }

        _webUi.Notice(MsgClearedNotice(count));
        _webUi.FlashPc(WebUiText.HistoryClearedFlash(count));
        DiagnosticLog.Info(WebUiCategory, WebUiText.HistoryClearedLogLine(count, remote));
        return new WebReply(WebOutcome.Ok, MsgClearedNotice(count));
    }
}
