using System.Text;
using NeonSidekick.Files;
using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// A line sent from the live editor while a reply ran (2026-09-25, the user's ask: the input row is the idle line's editor
/// under every reply): <see cref="Draft"/> is its token form — what the history recalls, a collapsed paste or a picture
/// as its token —, <see cref="Text"/> what the model is sent (the tokens expanded, trailing whitespace trimmed) and
/// <see cref="Images"/> the pictures its labels name, in the order they stand; <see cref="Label"/> is what a pane row shows
/// (the tokens as their labels, every line break one space). Nothing is committed to the transcript until
/// <see cref="InputLine.SendAsync"/> sends it at the idle line.
/// </summary>
public sealed record SubmittedLine(string Draft, string Text, IReadOnlyList<ImageAttachment> Images, string Label)
{
    /// <summary>
    /// The line as a command would read it: the trimmed draft when it holds no paste token and no line break (a
    /// collapsed paste, a picture or a multi-line message is never a command, the watcher's old rule), else null.
    /// </summary>
    public string? CommandText => Draft.Contains('\n', StringComparison.Ordinal) || Draft.Any(PasteBlocks.IsToken) ? null : Draft.Trim();
}

/// <summary>What one event did to an <see cref="InputLine.Editor"/> (2026-09-25).</summary>
public abstract record EditOutcome
{
    private EditOutcome()
    {
    }

    /// <summary>The event was the editor's: the draft, the cursor, the list or nothing moved.</summary>
    public static readonly EditOutcome Handled = new HandledOutcome();

    /// <summary>A live editor would not take the event (a bare ESC with no list open, a click off the input rows): the caller's.</summary>
    public static readonly EditOutcome Declined = new DeclinedOutcome();

    private sealed record HandledOutcome : EditOutcome;

    private sealed record DeclinedOutcome : EditOutcome;

    /// <summary>Enter on a live editor with something to send: nothing committed, remembered or cleared yet (<see cref="InputLine.Editor.Accept"/>).</summary>
    public sealed record Submit(SubmittedLine Line) : EditOutcome;

    /// <summary>An idle read's end, as <see cref="InputLine.ReadAsync"/> returns it.</summary>
    public sealed record End(InputResult Result) : EditOutcome;
}

public sealed partial class InputLine
{
    /// <summary>
    /// What one read asks of the editor: <see cref="ReadAsync"/>'s flags and hooks, and <see cref="Live"/> — the editor
    /// fed by the key watcher while a reply runs (2026-09-25), where Enter hands the line back (<see cref="EditOutcome.Submit"/>)
    /// and every idle-only hook is off.
    /// </summary>
    internal sealed record ReadOptions(
        bool Live = false,
        bool Remember = true,
        bool AllowEmpty = false,
        ConsoleKey? PushToTalk = null,
        bool EscapeCancels = false,
        bool Multiline = false,
        MentionFolderAction? Mentions = null,
        int PastePreview = 0,
        Func<bool>? SoftEscape = null,
        Func<bool>? Interrupt = null,
        Func<string, CancellationToken, Task<string?>>? Intercept = null,
        Action? BeforeCommit = null,
        Func<int, bool>? EmptyArrow = null,
        bool Mask = false,
        Func<bool, bool>? EmptyDelete = null,
        Func<bool>? EmptyEnter = null,
        bool Shortcuts = false,
        bool PushToTalkOverDraft = false);

    /// <summary>
    /// The input line's editing state and its keys, pulled out of <see cref="ReadAsync"/> on 2026-09-25 (the user's ask:
    /// under <c>/botchat</c> — every reply, as it turned out — the arrows, the history, the lists and the mouse did nothing
    /// until the turn ended, the watcher only buffering keys). The draft, the cursor, the selection's anchor, the history walk
    /// and the completion list live here; <see cref="FeedAsync"/> is one event, exactly as the read's loop handled it.
    ///
    /// <para>The chat line's editor is one for the session (<see cref="Chat"/>): the idle read feeds it, and while a reply
    /// runs the key watcher does (<see cref="KeySource.WatchAsync(CancellationTokenSource, CancellationToken, Func{ConsoleKeyInfo, bool}?, CancellationTokenSource?, Func{KeySource.WatchedLine, Task{bool}}?, Func{bool}?, Func{InputEvent, bool}?, Func{ConsoleKeyInfo, bool}?, Func{InputEvent.Click, string?}?, Editor?)"/>),
    /// so the draft, its cursor and its tokens carry from one to the other. Never two feeders at once: the watcher is joined
    /// (and its pending line awaited) before the idle read starts, and the idle read has ended before a turn's watcher starts.
    /// The history and the pastes belong to whoever feeds it. Every other read (a settings value, a question's own answer)
    /// gets an editor of its own, so a pane opened under a reply never touches the chat draft.</para>
    /// </summary>
    public sealed class Editor
    {
        private readonly InputLine _line;
        private readonly StringBuilder _text;
        private int _cursor;
        // The selection's other end; −1 = none. There is a selection only while it differs from the cursor.
        private int _anchor = -1;
        private int _historyIndex;
        private string _draft = "";
        // The Up/Down row moves (2026-09-21): the cell column of the run's first press, kept while the
        // arrows repeat so a short row in between does not lose it; and whether the last arrow recalled
        // a history line, in which case the next one walks on rather than climbing the recalled rows.
        // Any other input ends both.
        private int _goalCol = -1;
        private bool _walking;
        // The last event was an empty Delete that emptyDelete spent (2026-09-24): the next one's repeat flag.
        private bool _deleteSpent;
        // The word a double-click selected, while its second press is held (2026-09-30): a drag then moves by words.
        private (int Start, int End)? _heldWord;
        // A picture dragged toward the line (2026-09-28): the id a left press on a picture caught (−1 = none), the cell
        // it was pressed at, and whether a drag has left that cell yet (the hint row says so while it has).
        private int _pressedPicture = -1;
        private (int X, int Y) _pressedAt;
        private bool _draggingPicture;
        // The completion list (@-mention, command, argument, #skill, $tool, %connection, ^workflow): open while `_list` is
        // set; `_dismissed` is the word ESC closed it on, so a cursor move over the same word does not bring it straight back.
        private MentionList? _list;
        private (int Start, string Query)? _dismissed;
        private ReadOptions _o = new();
        // The empty-line hooks a live read keeps (2026-09-28, SetLiveHooks): the picture strip's keys under a reply.
        private Func<int, bool>? _liveEmptyArrow;
        private Func<bool>? _liveEmptyEnter;
        private bool _completing, _commanding, _arguing, _hashing, _dollaring, _percenting, _careting, _starring;

        internal Editor(InputLine line, string initialText)
        {
            _line = line;
            _text = new StringBuilder(initialText);
            _cursor = _text.Length;
            _historyIndex = line._history.Count;
        }

        /// <summary>The draft in its token form.</summary>
        public string Text => _text.ToString();

        /// <summary>The cursor, a UTF-16 index into <see cref="Text"/>.</summary>
        public int Cursor => _cursor;

        /// <summary>Whether a stretch is selected.</summary>
        public bool HasSelection => _anchor >= 0 && _anchor != _cursor;

        /// <summary>Whether a completion list is open over the row.</summary>
        public bool ListOpen => _list is not null;

        /// <summary>A read starts: its options, the history walk at the bottom again; the draft, cursor and selection kept.</summary>
        internal void Begin(ReadOptions options)
        {
            _o = options;
            bool onPane = options.Mentions is not null && _line._pane.Enabled;
            _completing = onPane && _line._mentions is not null;
            _commanding = onPane && _line._commands is not null;
            _arguing = onPane && _line._arguments is not null;
            _hashing = onPane && _line._skills is not null;
            _dollaring = onPane && _line._tools is not null;
            _percenting = onPane && _line._connections is not null;
            _careting = onPane && _line._workflows is not null;
            _starring = onPane && _line._shares is not null;
            _historyIndex = _line._history.Count;
            _draft = "";
            _goalCol = -1;
            _walking = false;
            _deleteSpent = false;
            _dismissed = null;
            _anchor = Math.Min(_anchor, _text.Length);
            _cursor = Math.Clamp(_cursor, 0, _text.Length);
        }

        /// <summary>
        /// The watcher takes the keys (2026-09-25): the last idle read's line options (the paste rule, the lists, the
        /// preview, the history) with every idle-only hook off — no push-to-talk, no splash keys, no intercept, no hint or
        /// toolbar results — and Enter handing the line back. The <see cref="SetLiveHooks"/> pair is kept (2026-09-28, the
        /// user's report: the picture strip's arrows did nothing under a reply). A draft is drawn again at once: the sent
        /// line's commit emptied the row it stands on.
        /// </summary>
        public void BeginLive()
        {
            Begin(new ReadOptions(Live: true, Remember: _o.Remember, Multiline: true, Mentions: _o.Mentions, PastePreview: _o.PastePreview,
                EmptyArrow: _liveEmptyArrow, EmptyEnter: _liveEmptyEnter));
            if (_text.Length > 0)
            {
                Redraw();
            }
        }

        /// <summary>
        /// The empty-line hooks <see cref="BeginLive"/> keeps (2026-09-28): a plain Left or Right, and an Enter, over an empty
        /// draft while a reply runs — the chat line's picture strip. Called on the watcher's thread, as the editor's keys are.
        /// </summary>
        public void SetLiveHooks(Func<int, bool>? emptyArrow, Func<bool>? emptyEnter)
        {
            _liveEmptyArrow = emptyArrow;
            _liveEmptyEnter = emptyEnter;
        }

        /// <summary>Ctrl+C under a reply (2026-09-25): the selection copied, as at idle — true when there was one (a failed copy says so); false with none.</summary>
        public bool TryCopySelection()
        {
            if (!HasSelection)
            {
                return false;
            }

            CopySelection();
            return true;
        }

        /// <summary>ESC under a reply (2026-09-25), after the voice: an open list closes and the word is dismissed; false with none.</summary>
        public bool TryCloseList()
        {
            if (_list is not { } open)
            {
                return false;
            }

            _dismissed = (open.Start, open.Query);
            CloseList();
            return true;
        }

        /// <summary>A live line taken (queued, run as a command, left for the idle line): remembered as the idle Enter remembers, the row emptied.</summary>
        public void Accept(SubmittedLine line)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (_o.Remember)
            {
                _line.Remember(line.Draft);
            }

            _historyIndex = _line._history.Count;
            _draft = "";
            _text.Clear();
            _cursor = 0;
            _anchor = -1;
            _dismissed = null;
            Redraw();
        }

        /// <summary>
        /// <paramref name="draft"/> on the row, the cursor at its end (a replacement the typo intercept offers, a withdrawn
        /// line coming back). <paramref name="prependToCurrent"/> puts it ahead of what is there, a line break between.
        /// Not drawn: the next read, or <see cref="Redraw"/>, draws it.
        /// </summary>
        public void Load(string draft, bool prependToCurrent = false)
        {
            ArgumentNullException.ThrowIfNull(draft);
            string current = _text.ToString();
            string value = prependToCurrent && current.Length > 0 ? draft + "\n" + current : draft;
            _text.Clear().Append(value);
            _cursor = prependToCurrent && current.Length > 0 ? draft.Length : _text.Length;
            _anchor = -1;
            _dismissed = null;
        }

        /// <summary>The keys go to a pane (a mid-turn command's, a tool's question): the list closes; <see cref="Redraw"/> brings the row back after.</summary>
        public void Suspend() => CloseList();

        /// <summary>The draft on the row again (the pane's own input slot wiped it), the list with it where the word still has one.</summary>
        public void Redraw()
        {
            string draft = _text.ToString();
            if (_o.Mask)
            {
                // A secret (later on 2026-09-23): one glyph per character, so the cursor and the selection keep their columns.
                _line._pane.ShowInput(new string(MaskGlyph, draft.Length), _cursor, _anchor);
                return;
            }

            var pastes = _line._pastes;
            _line._pane.ShowInput(pastes.Display(draft, unbreakable: true), pastes.ToDisplayIndex(draft, _cursor), _anchor < 0 ? -1 : pastes.ToDisplayIndex(draft, _anchor), pastes.LabelRanges(draft));
            RefreshList();
        }

        /// <summary>The row emptied on the screen (a read's end); the draft itself stays.</summary>
        internal void EndRow() => _line._pane.ClearInput();

        /// <summary>The completion list gone, overlay and all (every exit of a read, and a watcher's end).</summary>
        internal void CloseList()
        {
            if (_list is null)
            {
                return;
            }

            _list = null;
            _line._pane.CloseOverlay();
        }

        /// <summary>
        /// One event. Idle (<see cref="ReadOptions.Live"/> off), exactly <see cref="ReadAsync"/>'s loop body before
        /// 2026-09-25: an <see cref="EditOutcome.End"/> ends the read. Live, Enter on a line with something to send is
        /// <see cref="EditOutcome.Submit"/>, a bare ESC with no list and a click off the input rows are
        /// <see cref="EditOutcome.Declined"/>, and the idle-only hooks never run. A null key event is the read's own
        /// business (the wake, the alert, the end of the keyboard): never passed here.
        /// </summary>
        public async ValueTask<EditOutcome> FeedAsync(InputEvent input, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(input);
            var pane = _line._pane;
            var hintClicks = _line._hintClicks;
            if (input is not InputEvent.Drag)
            {
                // A double-click's word is held only while its second press is (2026-09-30): the drags that follow it.
                _heldWord = null;
            }

            if (input is InputEvent.Release release)
            {
                // The left button let go (2026-09-28): the drop of a picture dragged here, else nothing. It ends no pair
                // and moves no cursor — it falls between the two presses of every double-click.
                return await DropAsync(release).ConfigureAwait(false);
            }

            if (input is not (InputEvent.Drag or InputEvent.Click))
            {
                // A key, a paste or a wheel notch ends a picture's drag whose release never came (the pointer left the window).
                EndPictureDrag();
            }

            if (input is not InputEvent.Key)
            {
                _goalCol = -1;
                _walking = false;
            }

            // Whether the event before this one was an empty Delete the screen spent (2026-09-24):
            // any event at all in between — a key, a click, a paste, a wheel notch — ends the pair.
            bool deleteRepeat = _deleteSpent;
            _deleteSpent = false;

            if (input is InputEvent.Paste paste)
            {
                // The terminal's paste: one block on the line, never a submission.
                hintClicks.Reset();
                await InsertPasteAsync(paste.Text).ConfigureAwait(false);
                return EditOutcome.Handled;
            }

            if (!_o.Live && input is InputEvent.Click { Button: MouseButton.Left } close && pane.TryHitClose(close.X, close.Y))
            {
                // The × at an overlay's corner (a typed settings value under its menu, 2026-09-18)
                // is the ESC key: the same path, hooks and all.
                input = new InputEvent.Key(Keys.Escape);
            }
            else if (!_o.Live && input is InputEvent.Click { Button: MouseButton.Left } outside && pane.TryHitOutside(outside.X, outside.Y))
            {
                // Off the pane under a typed value (later on 2026-09-18): the second click within
                // the interval closes every level (ScreenPane.Dismiss) through the ESC key's own
                // path; the first is nothing. Ahead of the click branch, whose miss resets the pair.
                // The pair is per part (later on 2026-09-21, ScreenPane.OutsideKey): two on the
                // same toolbar glyph, say, and the dismiss remembers it for the screen's switch.
                _anchor = -1;
                if (!hintClicks.Second(pane.OutsideKey(outside.X, outside.Y)))
                {
                    return EditOutcome.Handled;
                }

                pane.Dismiss(outside.X, outside.Y);
                input = new InputEvent.Key(Keys.Escape);
            }

            if (input is InputEvent.Click click)
            {
                return await ClickAsync(click).ConfigureAwait(false);
            }

            if (input is InputEvent.Drag drag)
            {
                if (_pressedPicture >= 0)
                {
                    // A picture on its way to the line (2026-09-28): the first move off the pressed cell puts the hint
                    // up; the draft, the cursor and the pairs are left alone until the release.
                    if (!_draggingPicture && (drag.X, drag.Y) != _pressedAt)
                    {
                        _draggingPicture = true;
                        pane.SetDragHint(DropOnLineHint);
                    }

                    return EditOutcome.Handled;
                }

                // The button is still down from a click on the area: the cursor follows, the anchor
                // stays. Off the rows, or after a click that missed, the drag is nothing. After a double-click the
                // selection moves by whole words and always keeps the word the double-click took (2026-09-30, as Windows'
                // edit controls do): a jitter of the second press inside it leaves it whole.
                if (_anchor >= 0 && pane.TryHitInput(drag.X, drag.Y, out int to, out int toUnder))
                {
                    int anchor = _anchor;
                    int at = DraftIndex(to);
                    if (_heldWord is { } held)
                    {
                        var over = WordUnder(_text.ToString(), toUnder);
                        (anchor, at) = over.Start < held.Start ? (held.End, over.Start)
                            : over.End > held.End ? (held.Start, over.End)
                            : (held.Start, held.End);
                    }

                    if (at != _cursor || anchor != _anchor)
                    {
                        _anchor = anchor;
                        _cursor = at;
                        Redraw();
                    }
                }

                return EditOutcome.Handled;
            }

            // Anything but a click or a drag ends a hint-row pair: the next click there is a first again.
            hintClicks.Reset();
            if (input is InputEvent.Wheel wheel)
            {
                // The wheel scrolls the transcript region (the app holds the mouse for the
                // screen, 2026-09-17), the draft and the list untouched — unless a pane is open
                // over the line (a settings slot under a menu: the pane is modal, the notch is
                // nothing there). Never the cancel the null key below means.
                if (!pane.OverlayOpen)
                {
                    pane.ScrollWheel(wheel.Notches);
                }

                return EditOutcome.Handled;
            }

            if (input is not InputEvent.Key { Info: var k })
            {
                // No other event reaches a read; nothing to do with one that did.
                return EditOutcome.Handled;
            }

            if (k.Key is not (ConsoleKey.UpArrow or ConsoleKey.DownArrow))
            {
                _goalCol = -1;
                _walking = false;
            }

            // A click's anchor lives for the drag that may follow it; once a key arrives, an anchor
            // on the cursor is no selection (a Backspace or a plain arrow would otherwise move the
            // cursor off it and leave it behind as a phantom selection — past the text's end after a
            // click at the end, where the next edit threw out of the read).
            if (_anchor == _cursor)
            {
                _anchor = -1;
            }

            bool alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;
            bool control = (k.Modifiers & ConsoleModifiers.Control) != 0;
            // Printable characters first, before any key is matched by name. A key event that
            // carries a printable character IS that character: a scripted '.' arrives as
            // ConsoleKey.Delete.
            // The one exception is a chord with Alt alone, which the console reports with the
            // letter in it (Alt+V is 'v' + Alt): that is a chord, never typing. AltGr is Ctrl+Alt
            // and still types, and an Alt+NumPad character arrives on the Alt release, Alt up.
            if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar) && !(alt && !control))
            {
                DeleteSelection();
                _text.Insert(_cursor, _line._pastes.Literal(k.KeyChar));   // a typed private-use glyph is a literal token (2026-10-04)
                _cursor++;
                Redraw();
                return EditOutcome.Handled;
            }

            // Only from an empty line: with text on the row the key is just ignored (pinned) — unless the read lets it listen
            // over a draft (2026-10-02, STT destination draft: the transcript is appended to the draft, which stays here).
            if (_o.PushToTalk is { } ptt && k.Key == ptt && (_text.Length == 0 || _o.PushToTalkOverDraft))
            {
                EndRow();
                return new EditOutcome.End(new InputResult.PushToTalk());
            }

            // ESC's first job is silence: the hook is asked ahead of the list and the draft.
            if (k.Key == ConsoleKey.Escape && _o.SoftEscape is not null && _o.SoftEscape())
            {
                // The key was spent (the tail stopped); the draft and the list stay.
                return EditOutcome.Handled;
            }

            // With the list open five keys are its: the arrows move the highlight, Enter and Tab
            // apply it, ESC closes it. Everything else edits the line as ever, and the redraw
            // refreshes the list from the draft.
            if (_list is { } open)
            {
                switch (k.Key)
                {
                    case ConsoleKey.UpArrow:
                        _list = open.Move(-1);
                        ShowList();
                        return EditOutcome.Handled;

                    case ConsoleKey.DownArrow:
                        _list = open.Move(+1);
                        ShowList();
                        return EditOutcome.Handled;

                    case ConsoleKey.Enter:
                    case ConsoleKey.Tab:
                        ApplyMention(open);
                        return EditOutcome.Handled;

                    case ConsoleKey.Escape:
                        _dismissed = (open.Start, open.Query);
                        CloseList();
                        return EditOutcome.Handled;
                }
            }

            if (_o.Shortcuts && !_o.Live && Keys.ShortcutLine(k) is { } shortcut)
            {
                // A command chord (2026-09-30, the user's ask): /clear, /new, /splash or a pane's word, run by the screen as typed; the draft
                // comes back after. An AltGr key that types a character was typed above and never reaches here.
                EndRow();
                return new EditOutcome.End(new InputResult.Shortcut(_text.ToString(), shortcut));
            }

            bool shift = (k.Modifiers & ConsoleModifiers.Shift) != 0;
            switch (k.Key)
            {
                case ConsoleKey.Enter when _o.Multiline && Keys.IsLineBreak(k):
                    // Ctrl+Enter (2026-09-22): a line break in the draft, as a pasted one is.
                    Insert("\n");
                    break;

                case ConsoleKey.Enter:
                {
                    // The draft keeps its tokens (the history recalls them); what is sent has the
                    // blocks in their place; the transcript keeps the labels.
                    string draftText = _text.ToString().TrimEnd();
                    string submitted = _line._pastes.Expand(draftText).TrimEnd();
                    if (submitted.Length == 0 && !_o.AllowEmpty)
                    {
                        if (_text.Length == 0 && _o.EmptyEnter is not null && _o.EmptyEnter())
                        {
                            return EditOutcome.Handled;
                        }

                        // Nothing to send; a row of spaces is not worth keeping either.
                        _text.Clear();
                        _cursor = 0;
                        _anchor = -1;
                        Redraw();
                        break;
                    }

                    if (_o.Live)
                    {
                        // Under a reply (2026-09-25): the line goes back to the watcher's hook, which decides.
                        return new EditOutcome.Submit(_line.Line(draftText));
                    }

                    // The typo intercept's chance (2026-09-18): a replacement is the new draft and
                    // the line was never sent — nothing committed, nothing remembered.
                    if (_o.Intercept is not null && await _o.Intercept(submitted, cancellationToken).ConfigureAwait(false) is { } replacement)
                    {
                        _text.Clear().Append(replacement);
                        _cursor = _text.Length;
                        _anchor = -1;
                        _dismissed = null;
                        Redraw();
                        break;
                    }

                    // The row becomes a normal markup line: it may wrap now, and it is what
                    // the transcript keeps — with the start of each collapsed paste under it.
                    _o.BeforeCommit?.Invoke();
                    // A masked line is committed as its glyphs (later on 2026-09-23): the flow keeps the row, never the secret.
                    pane.CommitInput(_o.Mask ? new string(MaskGlyph, draftText.Length) : _line._pastes.Display(draftText), _o.Mask ? "" : PreviewText(_line._pastes.Previews(draftText, _o.PastePreview)));
                    if (_o.Remember && !_o.Mask)
                    {
                        _line.Remember(draftText);
                    }

                    var images = _line._pastes.ImagesIn(draftText);
                    // The chat line's draft is the session's (2026-09-25): sent, the row starts empty.
                    _text.Clear();
                    _cursor = 0;
                    _anchor = -1;
                    _dismissed = null;
                    return new EditOutcome.End(new InputResult.Submitted(submitted, images));
                }

                case ConsoleKey.Escape:
                    if (_o.Live)
                    {
                        // Under a reply the draft stays (2026-09-25, the user's call): the key is the watcher's.
                        return EditOutcome.Declined;
                    }

                    if (_text.Length == 0 || _o.EscapeCancels)
                    {
                        EndRow();
                        return new EditOutcome.End(new InputResult.Cancelled());
                    }

                    _text.Clear();
                    _cursor = 0;
                    _anchor = -1;
                    Redraw();
                    break;

                case ConsoleKey.Backspace:
                {
                    if (HasSelection)
                    {
                        DeleteSelection();
                        Redraw();
                        break;
                    }

                    int len = TextCells.ElementLengthBefore(_text.ToString(), _cursor);
                    if (len > 0)
                    {
                        _text.Remove(_cursor - len, len);
                        _cursor -= len;
                        Redraw();
                    }

                    break;
                }

                case ConsoleKey.Delete:
                    // A plain Delete over an empty draft has nothing to remove: the screen may spend
                    // it on the splash picture (2026-09-24), told whether the key before was one too.
                    if (_text.Length == 0 && !shift && !control && !alt && _o.EmptyDelete is not null && _o.EmptyDelete(deleteRepeat))
                    {
                        _deleteSpent = true;
                        return EditOutcome.Handled;
                    }

                    if (HasSelection)
                    {
                        DeleteSelection();
                        Redraw();
                        break;
                    }

                    if (_cursor < _text.Length)
                    {
                        TextCells.ElementWidth(_text.ToString(), _cursor, out int len);
                        _text.Remove(_cursor, len);
                        Redraw();
                    }

                    break;

                case ConsoleKey.LeftArrow:
                    // A plain arrow over an empty draft has nothing to move: the screen may spend
                    // it on the welcome splash (2026-09-19).
                    if (_text.Length == 0 && !shift && !control && !alt && _o.EmptyArrow is not null && _o.EmptyArrow(-1))
                    {
                        return EditOutcome.Handled;
                    }

                    if (shift)
                    {
                        Anchor();
                        _cursor -= TextCells.ElementLengthBefore(_text.ToString(), _cursor);
                    }
                    else if (HasSelection)
                    {
                        // Collapse onto the selection's start, as every editor does.
                        _cursor = Math.Min(_anchor, _cursor);
                        _anchor = -1;
                    }
                    else
                    {
                        _cursor -= TextCells.ElementLengthBefore(_text.ToString(), _cursor);
                    }

                    Redraw();
                    break;

                case ConsoleKey.RightArrow:
                    if (_text.Length == 0 && !shift && !control && !alt && _o.EmptyArrow is not null && _o.EmptyArrow(+1))
                    {
                        return EditOutcome.Handled;
                    }

                    if (!shift && HasSelection)
                    {
                        _cursor = Math.Max(_anchor, _cursor);
                        _anchor = -1;
                        Redraw();
                    }
                    else if (_cursor < _text.Length)
                    {
                        if (shift)
                        {
                            Anchor();
                        }

                        TextCells.ElementWidth(_text.ToString(), _cursor, out int len);
                        _cursor += len;
                        Redraw();
                    }

                    break;

                case ConsoleKey.Home when control:
                    // Ctrl+Home (2026-09-18): the transcript's first rows; the draft, cursor and
                    // selection untouched. Nothing on a transcript that fits.
                    pane.ScrollToTop();
                    break;

                case ConsoleKey.Home:
                    _anchor = shift ? Anchor() : -1;
                    _cursor = LineHome(_text.ToString(), _cursor);
                    Redraw();
                    break;

                case ConsoleKey.O when Keys.IsToolToggle(k):
                    // Ctrl+O (2026-09-22): every tool run in the transcript unfolded or folded;
                    // the draft, cursor and selection untouched.
                    pane.ToggleToolGroups();
                    break;

                case ConsoleKey.End when control:
                    // Ctrl+End (2026-09-17): the scrolled transcript back to the bottom, so the
                    // rows arriving show again; the draft, cursor and selection untouched. Nothing
                    // while not scrolled.
                    pane.ScrollToEnd();
                    break;

                case ConsoleKey.End:
                    _anchor = shift ? Anchor() : -1;
                    _cursor = LineEnd(_text.ToString(), _cursor);
                    Redraw();
                    break;

                case ConsoleKey.PageUp:
                    // The transcript region, a page back; the pane clamps (nothing on a
                    // short transcript). A push-to-talk PgUp took the empty line above.
                    pane.ScrollPage(-1);
                    break;

                case ConsoleKey.PageDown:
                    pane.ScrollPage(1);
                    break;

                case ConsoleKey.A when control:
                    // Select all (the terminal's own select-all is Ctrl+Shift+A, so this one arrives).
                    if (_text.Length > 0)
                    {
                        _anchor = 0;
                        _cursor = _text.Length;
                        Redraw();
                    }

                    break;

                case ConsoleKey.C when control && !alt:
                    // Ctrl+C (2026-09-17): the selection copied first — the terminal's own copy
                    // never sees a plain drag, so this is it — and kept, as a terminal keeps
                    // one; success is silent, the highlight is the feedback. Without one the
                    // screen's hook decides (the tail stopped, the exit armed) and a decline is
                    // the exit; a read with no hook (a settings field) treats the key as ESC.
                    // Ctrl+Alt+C is AltGr+C, a character on some layouts, typed above.
                    if (HasSelection)
                    {
                        CopySelection();
                        break;
                    }

                    if (_o.Live)
                    {
                        // The watcher's cancel key: it only comes here under a watcher that does not cancel with it.
                        return EditOutcome.Declined;
                    }

                    if (_o.Interrupt is null)
                    {
                        goto case ConsoleKey.Escape;
                    }

                    if (_o.Interrupt())
                    {
                        break;
                    }

                    EndRow();
                    return new EditOutcome.End(new InputResult.Exit());

                case ConsoleKey.X when control && !alt:
                    // Ctrl+X (2026-09-25, the user's ask): the selection cut — copied, then removed as
                    // Backspace would. A failed copy (a masked field, no clipboard writer) says so and
                    // keeps the text, so nothing is lost. Nothing without a selection; the same on the
                    // live row under a reply, where the key is no watcher's. Ctrl+Alt+X is AltGr+X, typed above.
                    if (HasSelection && CopySelection())
                    {
                        DeleteSelection();
                        Redraw();
                    }

                    break;

                case ConsoleKey.V when control ^ alt:
                    // The line's own paste. Ctrl+V arrives only where the terminal lets it through
                    // (Windows Terminal keeps it and pastes text as key records instead); Alt+V is
                    // the chord that always does. Ctrl+Alt+V is AltGr+V, a character on some layouts.
                    await PasteFromClipboardAsync().ConfigureAwait(false);
                    break;

                case ConsoleKey.UpArrow:
                case ConsoleKey.DownArrow:
                {
                    // A row move first (2026-09-21): the pane answers while the draft wraps and the
                    // target row is there; a line just recalled walks on instead. Else the history.
                    int delta = k.Key == ConsoleKey.UpArrow ? -1 : +1;
                    if (!_walking && pane.TryStepInputRow(delta, _goalCol, out int at, out int col))
                    {
                        _goalCol = col;
                        if (shift)
                        {
                            Anchor();
                        }
                        else
                        {
                            _anchor = -1;
                        }

                        _cursor = DraftIndex(at);
                        Redraw();
                        break;
                    }

                    var history = _line._history;
                    // ReplaceHistory under a live walk (a /cmdclear under a reply, 2026-09-25) can leave the index past the end.
                    _historyIndex = Math.Min(_historyIndex, history.Count);
                    if (delta < 0)
                    {
                        if (_historyIndex > 0)
                        {
                            if (_historyIndex == history.Count)
                            {
                                _draft = _text.ToString();
                            }

                            _historyIndex--;
                            Replace(history[_historyIndex]);
                            _walking = true;
                        }
                    }
                    else if (_historyIndex < history.Count)
                    {
                        _historyIndex++;
                        Replace(_historyIndex == history.Count ? _draft : history[_historyIndex]);
                        _walking = true;
                    }

                    break;
                }

                default:
                    // Function keys, Tab, Ctrl chords: nothing to type.
                    break;
            }

            return EditOutcome.Handled;
        }

        private async ValueTask<EditOutcome> ClickAsync(InputEvent.Click click)
        {
            var pane = _line._pane;
            var hintClicks = _line._hintClicks;
            EndPictureDrag();
            // A left click on the area puts the cursor under it and anchors a selection there
            // (a drag extends it); a right click pastes there, over the selection if any.
            // Neither is typing: the draft, the history and the push-to-talk rule see nothing.
            // A second left click on the hint row within the interval ends the read (2026-09-18).
            if (click.Button != MouseButton.Left)
            {
                hintClicks.Reset();
                await PasteFromClipboardAsync().ConfigureAwait(false);
                return EditOutcome.Handled;
            }

            if (pane.TryHitInput(click.X, click.Y, out int at, out int under))
            {
                _cursor = DraftIndex(at);
                _anchor = _cursor;

                // A second click on the same word within the interval selects it (2026-09-30, the user's ask): paired by the
                // word's start, so the two presses may land on different letters of it. The word is the one under the
                // pointer, not the caret's side of it: the whole of a paste's label is its token, and past a row's end
                // it is the row's last word, never the space a wrap dropped there.
                var word = WordUnder(_text.ToString(), under);
                if (hintClicks.Second(DraftWordPairKey(word.Start)) && word.End > word.Start)
                {
                    _anchor = word.Start;
                    _cursor = word.End;
                    _heldWord = word;
                }

                Redraw();
                return EditOutcome.Handled;
            }

            if (_o.Multiline && !_o.Mask && _line.PictureFile is not null && pane.PictureAt(click.X, click.Y) is int pressed)
            {
                // A press on a picture — a strip tile or one in the transcript — may start a drag onto the line
                // (2026-09-28); the click itself goes on to its pairing below (or the watcher's, under a reply) as ever.
                _pressedPicture = pressed;
                _pressedAt = (click.X, click.Y);
            }

            if (_o.Live)
            {
                // Under a reply the rest of the screen is the watcher's click hook's (2026-09-25).
                _anchor = -1;
                return EditOutcome.Declined;
            }

            if (pane.TryHitHint(click.X, click.Y, out var hit))
            {
                // Paired per part: two clicks on different glyphs, or one on the model
                // and one on the row, are two firsts.
                _anchor = -1;
                if (hintClicks.Second(HintPairKey(hit)))
                {
                    if (hit.Zone == ScreenPane.HintZone.Scrolled)
                    {
                        // The scroll's hint: the bottom again, as Ctrl+End — the read
                        // goes on with the draft and the cursor where they were.
                        pane.ScrollToEnd();
                        return EditOutcome.Handled;
                    }

                    EndRow();
                    return new EditOutcome.End(new InputResult.HintRow(_text.ToString(), hit));
                }
            }
            else if (pane.TryHitToolbar(click.X, click.Y, out var tool))
            {
                // The toolbar (2026-09-21): a glyph, the path or the blanks (a pair
                // there since later that day: the screen's /settings, as the hint
                // row's blanks), paired per part like the hint row's.
                _anchor = -1;
                if (hintClicks.Second(ToolbarPairKey(tool)))
                {
                    EndRow();
                    return new EditOutcome.End(new InputResult.ToolbarRow(_text.ToString(), tool));
                }
            }
            else if (pane.TryHitStripButton(click.X, click.Y))
            {
                // The picture strip's button (2026-09-27): one click opens the picture viewer, as a button does.
                _anchor = -1;
                hintClicks.Reset();
                _line.OpenViewer?.Invoke();
            }
            else if (pane.TryHitStripClose(click.X, click.Y))
            {
                // The strip's close × (2026-09-28): one click puts the strip away until the next picture.
                _anchor = -1;
                hintClicks.Reset();
                _line.CloseStrip?.Invoke();
            }
            else if (pane.TryHitStripReopen(click.X, click.Y))
            {
                // The upper rule's 🎞️ (2026-10-03): one click brings the closed strip back.
                _anchor = -1;
                hintClicks.Reset();
                _line.ReopenStrip?.Invoke();
            }
            else if (pane.TryHitFoldButton(click.X, click.Y))
            {
                // The upper rule's ⤡ (2026-09-28 as ↘️ / ↖️; one button since 2026-09-29, the user's call): one click is
                // Ctrl+O — everything unfolded when anything is folded, else everything folded.
                _anchor = -1;
                hintClicks.Reset();
                pane.ToggleToolGroups();
            }
            else if (pane.TryHitRuleTitle(click.X, click.Y))
            {
                // The session's name on the upper rule (2026-09-28, the user's ask): a double-click opens the rename box,
                // as a bare /sessions title does; the draft comes back after, as the toolbar's.
                _anchor = -1;
                if (hintClicks.Second(RuleTitlePairKey))
                {
                    EndRow();
                    return new EditOutcome.End(new InputResult.RuleTitle(_text.ToString()));
                }
            }
            else if (pane.PictureAt(click.X, click.Y) is int picture)
            {
                // A picture in the transcript (later on 2026-09-24, the user's ask): a double-click
                // opens it in the image editor; the draft is untouched. One on the strip is highlighted
                // at the first click too (2026-09-28, SelectPicture), so a double-click highlights and opens.
                _anchor = -1;
                if (pane.TryHitStrip(click.X, click.Y, out _))
                {
                    _line.SelectPicture?.Invoke(picture);
                }

                if (hintClicks.Second(PicturePairKey(picture)))
                {
                    _line.OpenPicture?.Invoke(picture);
                }
            }
            else
            {
                // A tool run's summary in the transcript (2026-09-22): one click unfolds
                // or folds it; the draft is untouched. Any other row ends a pair.
                pane.TryToggleToolGroupAt(click.X, click.Y);
                hintClicks.Reset();
                _anchor = -1;
            }

            return EditOutcome.Handled;
        }

        private void Replace(string value)
        {
            _text.Clear().Append(value);
            _cursor = _text.Length;
            _anchor = -1;
            Redraw();
        }

        // The anchor for a Shift+move: where the cursor is now unless a selection already has one.
        private int Anchor() => _anchor = _anchor < 0 ? _cursor : _anchor;

        // The selection copied, kept; a failure says so (false). A masked draft never reaches the clipboard.
        private bool CopySelection()
        {
            int start = Math.Min(Math.Min(_anchor, _cursor), _text.Length);
            int end = Math.Min(Math.Max(_anchor, _cursor), _text.Length);
            if (_o.Mask || _line._copy is null || !_line._copy(_line._pastes.Expand(_text.ToString(start, end - start))))
            {
                _line._notices?.Notice(CopyFailedNotice);
                return false;
            }

            return true;
        }

        // The selected stretch removed, the cursor at its start; nothing without a selection.
        private void DeleteSelection()
        {
            if (HasSelection)
            {
                // Both ends held to the text: a selection can never reach past it, and a removal
                // that could must not take the read down.
                int start = Math.Min(Math.Min(_anchor, _cursor), _text.Length);
                int end = Math.Min(Math.Max(_anchor, _cursor), _text.Length);
                _text.Remove(start, end - start);
                _cursor = start;
            }

            _anchor = -1;
        }

        // The line's own paste (a right click, Ctrl+V, Alt+V): the picture on the clipboard first,
        // on the chat line only; else its text through the paste rule; nothing when it holds neither.
        private async Task PasteFromClipboardAsync()
        {
            if (_o.Multiline && _line._clipboardImage?.Invoke() is { } picture)
            {
                await InsertClipboardImageAsync(picture).ConfigureAwait(false);
            }
            else if (_line._clipboard?.Invoke() is { } clip)
            {
                await InsertPasteAsync(clip).ConfigureAwait(false);
            }
        }

        // A picture off the clipboard becomes one image token at the cursor, read like a dropped
        // file (off the thread, the hint row busy) and numbered with them; one that cannot be
        // attached is one notice and nothing on the line — there is no path to leave behind.
        private Task InsertClipboardImageAsync(byte[] picture) =>
            InsertImageAsync(null, picture, ImageFile.ClipboardName(_line._pastes.ImageCount + 1));

        // One picture as one image token at the cursor, read off the thread with the hint row busy: a file as a dropped
        // one is read and kept by its path, else bytes as a clipboard picture's are, kept beside it (later still on
        // 2026-09-24: generate_image's input at full size). One that cannot be attached is one notice and nothing on the
        // line. The clipboard's picture and a picture dragged off the screen (2026-09-28) share it.
        private async Task InsertImageAsync(string? path, byte[] bytes, string name)
        {
            var pastes = _line._pastes;
            string? error = null;
            ImageAttachment? image;
            using (_line._pane.BeginBusy(ReadingImage))
            {
                image = await Task.Run(() => path is not null ? ImageFile.Load(path, out error) : ImageFile.Load(bytes, name, out error)).ConfigureAwait(false);
            }

            if (image is null)
            {
                if (error is not null)
                {
                    _line._notices?.Notice(error);
                }

                return;
            }

            Insert((path is not null ? pastes.AddImage(image, sourcePath: path) : pastes.AddImage(image, original: bytes)).ToString());
        }

        // The left button let go (2026-09-28): a picture dragged off the strip or the transcript and let go on the input
        // rows lands at the cursor as if its file had been dropped on the window (or, with no file, as a clipboard
        // picture); let go anywhere else, or with no drag under way, it is nothing. The drag ends either way.
        private async ValueTask<EditOutcome> DropAsync(InputEvent.Release release)
        {
            int id = _pressedPicture;
            bool dragging = _draggingPicture;
            EndPictureDrag();
            if (!dragging || !_line._pane.TryHitInput(release.X, release.Y, out _) || _line.PictureFile?.Invoke(id) is not { } picture)
            {
                return EditOutcome.Handled;
            }

            // The press that started the drag was a first click on the picture: a drop is no half of a double-click.
            _line._hintClicks.Reset();
            await InsertImageAsync(picture.Path, picture.Bytes, picture.Name).ConfigureAwait(false);
            return EditOutcome.Handled;
        }

        // A picture's drag over: the press forgotten and the hint row given back.
        private void EndPictureDrag()
        {
            _pressedPicture = -1;
            if (_draggingPicture)
            {
                _draggingPicture = false;
                _line._pane.SetDragHint(null);
            }
        }

        // What a paste becomes on the line, in place of the selection: the chat line keeps the
        // block's line breaks, holds a long block behind a token and reads a dropped image file
        // into one; a field takes one paragraph.
        private async Task InsertPasteAsync(string pasted)
        {
            string inserted = await _line.PasteBlockAsync(pasted, _o.Multiline).ConfigureAwait(false);
            if (inserted.Length > 0)
            {
                Insert(inserted);
            }
        }

        // Typed-in-one-go text at the cursor, in place of the selection.
        private void Insert(string inserted)
        {
            DeleteSelection();
            _text.Insert(_cursor, inserted);
            _cursor += inserted.Length;
            Redraw();
        }

        // A click's index comes from the drawn (display) rows; the draft's index is behind the labels.
        private int DraftIndex(int displayIndex) => Math.Clamp(_line._pastes.ToDraftIndex(_text.ToString(), displayIndex), 0, _text.Length);

        // The word a double-click at display element `under` takes (2026-09-30, DraftWords): a masked value whole, its
        // words' edges never shown.
        private (int Start, int End) WordUnder(string draft, int under) =>
            _o.Mask ? (0, draft.Length) : DraftWords.At(draft, _line._pastes.ToDraftElement(draft, under));

        // The list follows the draft: the word under the cursor — a command at the start, its
        // argument after it, else an @word — is looked up when it changes, and the list goes
        // when there is none (or nothing matches, or ESC dismissed this very word).
        private void RefreshList()
        {
            if (!_completing && !_commanding && !_arguing && !_hashing && !_dollaring && !_percenting && !_careting && !_starring)
            {
                return;
            }

            string draft = _text.ToString();
            int start, end;
            string query;
            // The command list is narrowed here; an argument source narrows itself (it knows the
            // command) and may answer the path shape (ArgumentList.IsPathList), drawn as the @ list.
            Func<IReadOnlyList<CompletionItem>>? words = null;
            Func<ArgumentList>? argument = null;
            string prefix = "";
            if (_commanding && MentionCompleter.TryFindCommand(draft, _cursor, out end, out query))
            {
                start = 0;
                string typed = query;
                words = () => MentionCompleter.Matches(_line._commands!(), typed);
            }
            else if (_arguing && MentionCompleter.TryFindArgument(draft, _cursor, out string command, out start, out end, out query) && !MentionInArgument(draft, command))
            {
                string typed = query;
                argument = () => _line._arguments!(command, typed);
            }
            else if (_hashing && MentionCompleter.TryFind(draft, _cursor, '#', out start, out end, out query))
            {
                // A #skill mention (2026-09-17): a word list like a command's, re-prefixed on a pick.
                string typed = query;
                prefix = "#";
                words = () => MentionCompleter.Matches(_line._skills!(), typed);
            }
            else if (_dollaring && MentionCompleter.TryFind(draft, _cursor, '$', out start, out end, out query))
            {
                // A $tool mention (2026-09-19): the #skill shape over the tools the next turn offers.
                string typed = query;
                prefix = "$";
                words = () => MentionCompleter.Matches(_line._tools!(), typed);
            }
            else if (_percenting && MentionCompleter.TryFind(draft, _cursor, '%', out start, out end, out query))
            {
                // A %connection mention (later on 2026-09-23): the $tool shape over the database connections (SQL, Oracle, MySQL).
                string typed = query;
                prefix = "%";
                words = () => MentionCompleter.Matches(_line._connections!(), typed);
            }
            else if (_careting && MentionCompleter.TryFind(draft, _cursor, '^', out start, out end, out query))
            {
                // A ^workflow mention (later still on 2026-09-24): the %connection shape over the offered ComfyUI workflows.
                string typed = query;
                prefix = "^";
                words = () => MentionCompleter.Matches(_line._workflows!(), typed);
            }
            else if (_starring && MentionCompleter.TryFind(draft, _cursor, '*', out start, out end, out query))
            {
                // A *share mention (2026-10-01, the user's ask): the %connection shape over the offered UNC shares, once part of %'s list.
                string typed = query;
                prefix = "*";
                words = () => MentionCompleter.Matches(_line._shares!(), typed);
            }
            else if (!_completing || !MentionCompleter.TryFind(draft, _cursor, out start, out end, out query))
            {
                // The dismissal stands: the cursor may come back over the same word.
                CloseList();
                return;
            }

            if (_dismissed is { } gone && gone.Start == start && gone.Query == query)
            {
                CloseList();
                return;
            }

            _dismissed = null;
            if (_list is { } open && open.Start == start && open.Query == query)
            {
                _list = open with { End = end };
                return;
            }

            if (argument is not null)
            {
                var answer = argument();
                if (answer.IsPathList)
                {
                    // The @ shape for a path argument (/speak): no notes, no prefix, the folder mode on a pick.
                    if (answer.Paths!.Count == 0)
                    {
                        CloseList();
                        return;
                    }

                    _list = new MentionList(start, end, query, answer.Paths, answer.Truncated, 0, 0);
                    ShowList();
                    return;
                }

                words = () => answer.Words;
            }

            if (words is not null)
            {
                var items = words();
                if (items.Count == 0)
                {
                    CloseList();
                    return;
                }

                _list = new MentionList(start, end, query, items.Select(i => i.Text).ToList(), false, 0, 0) { Notes = items.Select(i => i.Note).ToList(), Prefix = prefix };
                ShowList();
                return;
            }

            var found = _line._mentions!(query);
            if (found.Paths.Count == 0)
            {
                CloseList();
                return;
            }

            _list = new MentionList(start, end, query, found.Paths, found.Truncated, 0, 0) { Prefix = "@" };
            ShowList();
        }

        /// <summary>
        /// Whether the word under the cursor in a slash command's text is a mention to complete as one (2026-09-30, the user's
        /// ask: <c>/loop infinite 1s append the time to @file.txt</c> completes the <c>@</c> as a message would): its character's
        /// list is on, and the command's argument is no path of its own (<see cref="InputLine.PathArgument"/>: <c>/speak</c>,
        /// <c>/view</c>, <c>/print</c> keep their file list and <c>/tree</c>, <c>/explore</c>, <c>/vault</c> their folder list,
        /// and none of them takes an <c>@</c>).
        /// </summary>
        private bool MentionInArgument(string draft, string command) =>
            MentionCompleter.TriggerAt(draft, _cursor) switch
            {
                '@' => _completing,
                '#' => _hashing,
                '$' => _dollaring,
                '%' => _percenting,
                '^' => _careting,
                '*' => _starring,
                _ => false,
            }
            && _line.PathArgument?.Invoke(command) != true;

        private void ShowList()
        {
            var pane = _line._pane;
            int height = pane.Profile.Height > 0 ? pane.LayoutHeight : MenuPane.DefaultHeight;   // less the toolbar's row (2026-09-21)
            int capacity = Math.Min(MentionCompleter.MaxRows, ScreenPane.MaxOverlayRows(height, pane.InputRows));
            if (_list!.IsWordList)
            {
                // A command or skill row is cut at the edge with an ellipsis, never wrapped.
                var (fitted, at) = MentionCompleter.WordRows(_list, capacity);
                _list = _list with { First = at };
                pane.ShowOverlay(new Rows(fitted), MentionCompleter.Hint, input: true);
                return;
            }

            var (rows, first) = MentionCompleter.Rows(_list, capacity);
            _list = _list with { First = first };
            pane.ShowOverlay(new Rows(rows.Select(r => new Markup(r).Overflow(Overflow.Ellipsis))), MentionCompleter.Hint, input: true);
        }

        // The highlighted word in place of the typed one — a command or a skill name with a space
        // after it, a path as @path, a skill mention as #name; the redraw then closes the list (a word and a space, a file,
        // or a folder under folder-apply) or re-lists the folder (folder-remain). A command applied
        // as /skill lands the cursor on its argument, so the skill list opens on the same redraw.
        private void ApplyMention(MentionList open)
        {
            string pick = open.Matches[open.Cursor];
            bool remain = !open.IsWordList && pick.EndsWith('/') && _o.Mentions == MentionFolderAction.Remain;
            (string replaced, int at) = MentionCompleter.Apply(_text.ToString(), open.Start, open.End, open.Prefix + pick, !remain);
            _text.Clear().Append(replaced);
            _cursor = at;
            _anchor = -1;
            _dismissed = null;
            Redraw();
        }
    }
}
