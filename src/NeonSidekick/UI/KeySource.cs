using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>What the interrupt watcher saw while a turn was running.</summary>
public enum Interrupt
{
    None,
    /// <summary>ESC: the turn's token was cancelled.</summary>
    Cancel,

    /// <summary>An accept key (push-to-talk again, or Enter) cancelled the accept token; the watcher then ran to its stop.</summary>
    Accept,
}

/// <summary>
/// The only reader of <see cref="IAnsiConsoleInput"/> in the interactive shell, with a type-ahead
/// buffer so keys pressed while a reply streams are kept for the next input line rather than lost.
/// The input is read as <see cref="IInputEvents"/> — keys and mouse clicks in order — when the
/// source is one (<see cref="WindowsConsoleInput"/>), else through the <see cref="KeyEvents"/>
/// adapter; only <see cref="ReadInputAsync"/> (the input line and the two panes) ever sees a click, every other
/// read drops them.
///
/// <para>During a turn <see cref="WatchAsync"/> polls for keys: ESC cancels the turn (unless the
/// caller's soft-cancel hook spends it first — the screen's stop of a reply being read aloud),
/// everything else — a key, a paste — is buffered. It polls <see cref="IAnsiConsoleInput.IsKeyAvailable"/> rather than
/// awaiting <see cref="IAnsiConsoleInput.ReadKeyAsync"/>: a scripted console that has run dry throws
/// from the latter, which would kill the watcher before a later key arrived. A real console with
/// redirected stdin throws from <c>IsKeyAvailable</c> itself, which means "no keyboard" and the
/// watcher simply waits to be stopped. Two things borrow the keys from the watcher, each run on
/// its task with the poll paused and the buffer set aside: a completed line's hook (a mid-turn
/// command's pane) and a pane request (<see cref="RequestPaneAsync"/>: the <c>ask_user</c> tool,
/// which runs on the turn task and must not read keys itself).</para>
///
    /// <para>Given the chat line's editor (<see cref="InputLine.Editor"/>, 2026-09-25, the user's ask: under <c>/botchat</c>
/// the arrows, the history, the lists and the mouse did nothing until the turn ended), the watcher buffers nothing: every
/// key, paste, drag and input-row click edits the draft exactly as the idle line would, Enter hands the edited line to the
/// line hook, and the draft outlives the watch. Without one (no pane) the keys are type-ahead as before.</para>
///
/// <para>It is itself an <see cref="IAnsiConsoleInput"/> that serves the buffer first, so a Spectre
/// prompt shown on a <see cref="ConsoleWithInput"/> over it sees type-ahead too.</para>
///
/// <para>Windows assumption: <c>Console.ReadKey</c> decodes key events, so an ESC keypress is
/// unambiguous. On a raw VT stream ESC is also the first byte of every terminal reply and would
/// need a disambiguation timeout.</para>
/// </summary>
public sealed class KeySource : IAnsiConsoleInput
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(15);

    private readonly IInputEvents _events;
    private readonly TimeSpan _poll;
    private readonly Queue<InputEvent> _buffer = new();
    private Task _pendingLine = Task.CompletedTask;

    // The pane requests (RequestPaneAsync), posted from the turn task and served by the watcher:
    // the queue, the signal the no-keyboard wait wakes on, and whether a watcher runs at all.
    private readonly object _requestGate = new();
    private readonly Queue<PaneRequest> _requests = new();
    private TaskCompletionSource _requestSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _watching;

    public KeySource(IAnsiConsoleInput input, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        _events = input as IInputEvents ?? new KeyEvents(input);
        _poll = pollInterval ?? DefaultPollInterval;
        if (_poll <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
    }

    /// <summary>Keys and pastes the watcher set aside for the next read.</summary>
    public int Buffered => _buffer.Count;

    /// <summary>
    /// The line hook still running after <see cref="WatchAsync(CancellationTokenSource, CancellationToken, Func{ConsoleKeyInfo, bool}?, CancellationTokenSource?, Func{string, Task{bool}}?)"/>
    /// returned (its <c>stop</c> fired while a pane the hook opened was reading the keys); a
    /// completed task when none. The hook owns the keys until it completes: await this before
    /// the next reader (the input line, a listen) starts.
    /// </summary>
    public Task PendingLine => _pendingLine;

    /// <summary>
    /// The text a run of typed-ahead events reads as, as one line: printable characters in order,
    /// Backspace taking the last one back; null when the run holds a paste (a pasted line is
    /// never a mid-turn command — it is type-ahead for the input line, which lays a paste out) or a
    /// typed line break (Ctrl+Enter, 2026-09-22: a message of several lines is no command either).
    /// The Enter that ends the run is not part of it. Pinned.
    /// </summary>
    public static string? LineText(IEnumerable<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var text = new System.Text.StringBuilder();
        foreach (var e in events)
        {
            if (e is InputEvent.Paste || (e is InputEvent.Key { Info: var b } && Keys.IsLineBreak(b)))
            {
                return null;
            }

            if (e is not InputEvent.Key { Info: var k })
            {
                continue;
            }

            if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
            {
                text.Append(k.KeyChar);
            }
            else if (k.Key == ConsoleKey.Backspace && text.Length > 0)
            {
                text.Length -= char.IsLowSurrogate(text[^1]) && text.Length > 1 ? 2 : 1;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Where the watcher shows what was typed ahead: after every key it buffers, the text the
    /// buffer will read as goes to <see cref="ScreenPane.PreviewInput"/>, so a line typed during a
    /// reply is seen on the input row while it waits. Null (the default) shows nothing.
    /// </summary>
    public ScreenPane? Mirror { get; set; }

    /// <summary>
    /// What a run of typed-ahead events reads as: printable characters in order, Backspace taking
    /// the last one back, and only the text after the last Enter (the lines before it are sent
    /// first, one per read); a Ctrl+Enter is a <c>'\n'</c> in the line, never its end (2026-09-22). A paste is its normalised text when the line will show it as text,
    /// else <see cref="PastePreview"/>-shaped (<c>[Pasted text +49 lines]</c>: the line numbers it
    /// when it lands), unbreakable as on the line (<paramref name="labels"/> says where those
    /// stand); Backspace takes a whole paste back. Pinned.
    /// </summary>
    public static string PreviewText(IEnumerable<InputEvent> events, out IReadOnlyList<(int Start, int Length)> labels)
    {
        ArgumentNullException.ThrowIfNull(events);
        var text = new System.Text.StringBuilder();
        // Each element's start and length in the text: a character (or pair) or a whole paste.
        var elements = new List<(int Start, int Length, bool Label)>();
        foreach (var e in events)
        {
            if (e is InputEvent.Paste paste)
            {
                string block = PasteText.Normalize(paste.Text);
                if (block.Length == 0)
                {
                    continue;
                }

                bool collapses = PasteBlocks.Collapses(block);
                string shown = collapses ? PasteBlocks.Unbreakable(PastePreview(block)) : block;
                elements.Add((text.Length, shown.Length, collapses));
                text.Append(shown);
                continue;
            }

            if (e is not InputEvent.Key { Info: var k })
            {
                continue;
            }

            if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
            {
                if (elements.Count > 0 && !elements[^1].Label && char.IsLowSurrogate(k.KeyChar) && text.Length > 0 && char.IsHighSurrogate(text[^1]))
                {
                    elements[^1] = (elements[^1].Start, elements[^1].Length + 1, false);
                }
                else
                {
                    elements.Add((text.Length, 1, false));
                }

                text.Append(k.KeyChar);
            }
            else if (Keys.IsLineBreak(k))
            {
                elements.Add((text.Length, 1, false));
                text.Append('\n');
            }
            else if (k.Key == ConsoleKey.Enter)
            {
                text.Clear();
                elements.Clear();
            }
            else if (k.Key == ConsoleKey.Backspace && elements.Count > 0)
            {
                text.Length = elements[^1].Start;
                elements.RemoveAt(elements.Count - 1);
            }
        }

        labels = elements.Where(x => x.Label).Select(x => (x.Start, x.Length)).ToList();
        return text.ToString();
    }

    /// <summary>What a long paste reads as while it waits: the line's label without the number it will get. Pinned.</summary>
    public static string PastePreview(string block)
    {
        ArgumentNullException.ThrowIfNull(block);
        int lines = PasteBlocks.Lines(block);
        return $"[Pasted text +{lines} {(lines == 1 ? "line" : "lines")}]";
    }

    /// <summary>
    /// A line the watcher offers to its line hook (2026-09-18): <see cref="Events"/> are the line's
    /// events with its Enter last (what the buffer held since the previous Enter), <see cref="Text"/>
    /// is <see cref="LineText"/> of them — null when the line holds a paste, which is never a
    /// command — and <see cref="Label"/> what the line reads as on a pane row (<see cref="LineLabel"/>).
    /// The events are what the screen keeps to send the line later, through the input line again.
    /// </summary>
    public sealed record WatchedLine(string? Text, IReadOnlyList<InputEvent> Events, SubmittedLine? Line = null)
    {
        /// <summary>The line as a pane row shows it.</summary>
        public string Label => Line?.Label ?? LineLabel(Events);
    }

    /// <summary>
    /// What a line's events read as on one row: <see cref="PreviewText"/> over them without the
    /// Enter that ends the run — printable characters, Backspace taking a character or a whole
    /// paste back, a collapsing paste as <c>[Pasted text +N lines]</c>, an inline paste as its
    /// text — with every line break folded to one space and the label's unbreakable blanks plain again, so a queue row stays one row. Pinned.
    /// </summary>
    public static string LineLabel(IReadOnlyList<InputEvent> line)
    {
        ArgumentNullException.ThrowIfNull(line);
        int count = line.Count > 0 && line[^1] is InputEvent.Key { Info: var last } && Keys.IsSend(last) ? line.Count - 1 : line.Count;
        string text = PreviewText(line.Take(count), out _);
        return text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Replace(' ', ' ');
    }

    /// <summary>
    /// The next event: a key or a paste from the type-ahead buffer first, then whatever the console has —
    /// a key, a paste, a click or a wheel notch. Returns <c>null</c> when <paramref name="cancellationToken"/> is
    /// cancelled; throws <see cref="InvalidOperationException"/> when there is no keyboard
    /// (redirected stdin, or a scripted console that ran dry). The input line, the menu pane and the
    /// info pane read here; they are the only readers that answer a click, the two panes the only
    /// ones that answer the wheel.
    ///
    /// <para>A source may throw <see cref="OperationCanceledException"/> when the token fires
    /// (Spectre's real console does, from a <c>Task.Delay</c> poll loop) or return null; both are
    /// the null this method promises. The wake word's first live run crashed the published exe
    /// through the throwing shape. <b>[scar]</b></para>
    /// </summary>
    public async Task<InputEvent?> ReadInputAsync(CancellationToken cancellationToken)
    {
        if (_buffer.Count > 0)
        {
            return _buffer.Dequeue();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            return await _events.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The next key, clicks dropped: <see cref="ReadInputAsync"/> for whatever reads keys only (the
    /// Spectre prompts through <see cref="IAnsiConsoleInput"/>). Same contract: null on cancellation,
    /// <see cref="InvalidOperationException"/> without a keyboard.
    /// </summary>
    public async Task<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var e = await ReadInputAsync(cancellationToken).ConfigureAwait(false);
            if (e is null)
            {
                return null;
            }

            if (e is InputEvent.Key key)
            {
                return key.Info;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsKeyAvailable()
    {
        // A Spectre prompt reads keys: a typed-ahead paste means nothing to it.
        while (_buffer.Count > 0 && _buffer.Peek() is not InputEvent.Key)
        {
            _buffer.Dequeue();
        }

        if (_buffer.Count > 0)
        {
            return true;
        }

        DropMouse();
        return _events.IsAvailable;
    }

    /// <inheritdoc/>
    public ConsoleKeyInfo? ReadKey(bool intercept)
    {
        while (_buffer.Count > 0)
        {
            if (_buffer.Dequeue() is InputEvent.Key buffered)
            {
                return buffered.Info;
            }
        }

        DropMouse();
        return _events.Read() is InputEvent.Key key ? key.Info : null;
    }

    /// <summary>Mouse events (clicks, drags, wheel notches) at the head of the source are nobody's but the line's and the panes'; every other reader skips them.</summary>
    private void DropMouse()
    {
        while (_events.NextIsMouse)
        {
            _events.Read();
        }
    }

    /// <inheritdoc/>
    Task<ConsoleKeyInfo?> IAnsiConsoleInput.ReadKeyAsync(bool intercept, CancellationToken cancellationToken) => ReadKeyAsync(cancellationToken);

    /// <summary>
    /// Watches the keyboard while a turn runs. ESC or Ctrl+C (<see cref="IsTurnCancel"/>) cancels
    /// <paramref name="turnCts"/> and returns <see cref="Interrupt.Cancel"/>; any other key, and a
    /// paste, is buffered. Returns <see cref="Interrupt.None"/> once <paramref name="stop"/> is
    /// cancelled. Never throws.
    /// </summary>
    public Task<Interrupt> WatchAsync(CancellationTokenSource turnCts, CancellationToken stop) =>
        WatchAsync(turnCts, stop, null, null);

    /// <summary>The turn's cancel keys: ESC (<see cref="Keys.IsCancel"/>) and Ctrl+C (<see cref="Keys.IsInterrupt"/>, since 2026-09-17), one path.</summary>
    public static bool IsTurnCancel(ConsoleKeyInfo key) => Keys.IsCancel(key) || Keys.IsInterrupt(key);

    /// <summary>
    /// <see cref="WatchAsync(CancellationTokenSource, CancellationToken)"/> with an accept key:
    /// the first key <paramref name="accept"/> matches cancels <paramref name="acceptCts"/>
    /// (push-to-talk's "done") and the watcher keeps polling, so ESC can still discard during
    /// transcription; later accept keys are dropped rather than buffered. Returns
    /// <see cref="Interrupt.Accept"/> when stopped after an accept.
    ///
    /// <para><paramref name="onLine"/> is the mid-turn line hook: after every Enter it buffers, the
    /// line the Enter ended (a <see cref="WatchedLine"/>: the events since the previous Enter, and
    /// their <see cref="LineText"/> — null when one is a paste, since 2026-09-18; before that a
    /// pasted line was never offered) is offered to it on this task. It returns true to consume the
    /// line — its events are taken off the buffer's tail, everything before them stays type-ahead —
    /// or false to leave it. While it runs the poll pauses and the buffer is set aside, so a pane the hook opens reads
    /// the keys through <see cref="ReadInputAsync"/> without eating earlier type-ahead; the mirror is
    /// redrawn after. Should <paramref name="stop"/> fire meanwhile, the watcher returns at once and
    /// the hook runs on as <see cref="PendingLine"/>. Null (the default): every line is type-ahead.</para>
    ///
    /// <para><paramref name="softCancel"/> is asked before a cancel key cancels the turn, on this task: true
    /// means the key was spent on something else (the screen stops the reply's speech with it) and
    /// the watch goes on, so the next one is the cancel; false or null cancels
    /// <paramref name="turnCts"/> as before. It never touches the console and never throws.</para>
    ///
    /// <para><paramref name="cancel"/> names the cancel keys: null is <see cref="IsTurnCancel"/> (ESC
    /// or Ctrl+C, the turn's pair); a connect's spinner passes <see cref="Keys.IsInterrupt"/> alone,
    /// so an ESC typed under it stays type-ahead for the picker or the line after it, as it always has.</para>
    ///
    /// <para><paramref name="spend"/> is asked for every other key ahead of the buffer, and for a
    /// wheel notch ahead of the drop, on this task: true means the event was spent (the screen
    /// scrolls the transcript with PgUp/PgDn and the wheel) — it is never type-ahead, never part
    /// of a line, never mirrored. A pane repaint under the pane's lock, the class of the mirror's
    /// own; it never throws. A drag is nobody's whatever the hook.</para>
    ///
    /// <para><paramref name="onClick"/> is asked for every click ahead of the drop, on this task
    /// (2026-09-18): a line text it answers is offered to <paramref name="onLine"/> exactly as a
    /// typed line would be — the same phase, nothing to hand back — so the pane that hook opens
    /// runs here under the pane's lock (the screen answers <c>/queue</c> to a double-click on the
    /// hint row's queued count, pairing the clicks itself on the pane's clock); null, or no
    /// <paramref name="onLine"/>, and the click is nobody's as before. The hook is asked with or
    /// without <paramref name="onLine"/> (later on 2026-09-18): a pair on the scroll's hint is
    /// spent inside it — the bottom again, a pane repaint on this task, <paramref name="spend"/>'s
    /// class — and answers nothing. It never throws.</para>
    ///
    /// <para><paramref name="editor"/> (2026-09-25) is the live row: set, the keys, pastes, drags and releases that are not a cancel,
    /// an accept or spent go to it instead of the buffer, a click to it first (the input rows' caret and selection, a
    /// right-click paste) and to <paramref name="onClick"/> when it declines; a line it hands back is offered to
    /// <paramref name="onLine"/> as a <see cref="WatchedLine"/> with its <see cref="SubmittedLine"/> — taken, the row empties;
    /// kept, the draft stays. A cancel key is spent first on the row where it means something there: Ctrl+C over a selection
    /// copies it, and ESC — after <paramref name="softCancel"/> — closes an open list; the draft itself is never cleared
    /// under a reply. A bare ESC the row declines (under a watch whose cancel key is Ctrl+C alone) is type-ahead as ever.</para>
    /// </summary>
    public async Task<Interrupt> WatchAsync(CancellationTokenSource turnCts, CancellationToken stop, Func<ConsoleKeyInfo, bool>? accept, CancellationTokenSource? acceptCts, Func<WatchedLine, Task<bool>>? onLine = null, Func<bool>? softCancel = null, Func<InputEvent, bool>? spend = null, Func<ConsoleKeyInfo, bool>? cancel = null, Func<InputEvent.Click, string?>? onClick = null, InputLine.Editor? editor = null)
    {
        ArgumentNullException.ThrowIfNull(turnCts);
        cancel ??= IsTurnCancel;
        bool accepted = false;
        lock (_requestGate)
        {
            _watching = true;
        }

        var previous = _editor;
        _editor = editor;
        editor?.BeginLive();

        try
        {
            while (!stop.IsCancellationRequested)
            {
                // A pane request (a tool asking the user) is served ahead of the keys: the phase
                // runs here with them, exactly as a line's hook does.
                while (TryTakeRequest(out var request))
                {
                    if (!await ServiceAsync(RequestPhase(request), stop, request.Done).ConfigureAwait(false))
                    {
                        return accepted ? Interrupt.Accept : Interrupt.None;
                    }
                }

                bool available;
                try
                {
                    available = _events.IsAvailable;
                }
                catch (InvalidOperationException)
                {
                    // No keyboard. Nothing can interrupt this turn; wait for it to end — a pane
                    // request still runs its phase (its reads run dry at once, so the pane answers nothing).
                    while (!stop.IsCancellationRequested)
                    {
                        while (TryTakeRequest(out var request))
                        {
                            if (!await ServiceAsync(RequestPhase(request), stop, request.Done).ConfigureAwait(false))
                            {
                                return accepted ? Interrupt.Accept : Interrupt.None;
                            }
                        }

                        await Task.WhenAny(WaitForStopAsync(stop), RequestSignal()).ConfigureAwait(false);
                    }

                    return accepted ? Interrupt.Accept : Interrupt.None;
                }

                if (available)
                {
                    InputEvent? e;
                    try
                    {
                        e = _events.Read();
                    }
                    catch (InvalidOperationException)
                    {
                        e = null;
                    }

                    if (e is InputEvent.Key { Info: var k })
                    {
                        if (Keys.ShortcutLine(k) is { } shortcut)
                        {
                            // A command chord (2026-09-30, the user's ask): /clear, /new, /splash or a pane's word offered to the
                            // line hook as a line sent from the row would be — with a SubmittedLine, so the screen can leave it for
                            // the idle line as it cancels the reply, or open its pane over it — the draft untouched. With no hook it is dropped, never type-ahead that would
                            // fire at the next idle line.
                            var chord = new SubmittedLine(shortcut, shortcut, [], shortcut);
                            if (onLine is not null
                                && !await ServiceAsync(LinePhase(onLine, new WatchedLine(shortcut, [], chord), []), stop, null).ConfigureAwait(false))
                            {
                                return accepted ? Interrupt.Accept : Interrupt.None;
                            }

                            continue;
                        }

                        if (cancel(k))
                        {
                            if (editor is not null && Keys.IsInterrupt(k) && editor.TryCopySelection())
                            {
                                // Ctrl+C over a selection on the live row (2026-09-25): the copy, as at the idle line.
                                continue;
                            }

                            if (softCancel is not null && softCancel())
                            {
                                // The key was spent (the speech stopped); the next one cancels.
                                continue;
                            }

                            if (editor is not null && Keys.IsCancel(k) && editor.TryCloseList())
                            {
                                // ESC over an open list on the live row (2026-09-25): the list goes, the reply runs on.
                                continue;
                            }

                            Cancel(turnCts);
                            return Interrupt.Cancel;
                        }

                        if (accept is not null && accept(k))
                        {
                            if (!accepted && acceptCts is not null)
                            {
                                accepted = true;
                                Cancel(acceptCts);
                            }

                            continue;
                        }

                        if (spend is not null && spend(e))
                        {
                            // Spent on the screen (a page of the transcript): never type-ahead.
                            continue;
                        }
                    }

                    if (editor is not null && e is not (null or InputEvent.Wheel or InputEvent.Click))
                    {
                        // The live row (2026-09-25): a key, a paste, a drag or a release (2026-09-28) edits the draft as at the idle line.
                        if (!await FeedAsync(editor, e, onLine, stop).ConfigureAwait(false))
                        {
                            return accepted ? Interrupt.Accept : Interrupt.None;
                        }

                        continue;
                    }

                    if (e is InputEvent.Key)
                    {
                        // Type-ahead, below.
                    }
                    else if (e is InputEvent.Wheel)
                    {
                        // The wheel scrolls the transcript under a reply (the hook); with none it is nobody's.
                        _ = spend?.Invoke(e);
                        continue;
                    }
                    else if (e is InputEvent.Click click)
                    {
                        // The live row's own clicks first (2026-09-25): the caret, a selection's anchor, a right-click paste.
                        if (editor is not null && await editor.FeedAsync(click, stop).ConfigureAwait(false) != EditOutcome.Declined)
                        {
                            continue;
                        }

                        // A click during a reply is nobody's — the reply owns the screen — unless
                        // the hook makes a line of it (a double-click on the queued count): that
                        // line runs the line hook as a typed one would, nothing to hand back. The
                        // hook is asked with or without a line hook (it spends a pair on the scroll's
                        // hint itself); a line it answers with none to run it is dropped.
                        if (onClick?.Invoke(click) is { } clicked && onLine is not null
                            && !await ServiceAsync(LinePhase(onLine, new WatchedLine(clicked, []), []), stop, null).ConfigureAwait(false))
                        {
                            return accepted ? Interrupt.Accept : Interrupt.None;
                        }

                        continue;
                    }
                    else if (e is not InputEvent.Paste)
                    {
                        // A drag is nobody's.
                        continue;
                    }

                    _buffer.Enqueue(e);
                    Preview();
                    if (onLine is not null && e is InputEvent.Key { Info: var sent } && Keys.IsSend(sent))
                    {
                        var line = TakeLastLine();
                        if (!await ServiceAsync(LinePhase(onLine, new WatchedLine(LineText(line), line), line), stop, null).ConfigureAwait(false))
                        {
                            // The turn ended under a pane the hook opened: the hook keeps the keys until it closes.
                            return accepted ? Interrupt.Accept : Interrupt.None;
                        }
                    }

                    continue;
                }

                try
                {
                    await Task.Delay(_poll, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return accepted ? Interrupt.Accept : Interrupt.None;
        }
        finally
        {
            // The list does not outlive the watch; the draft does (the idle read draws it again).
            editor?.CloseList();
            _editor = previous;
            lock (_requestGate)
            {
                // A request the watcher never reached is cancelled, never left for the next turn.
                _watching = false;
                while (_requests.TryDequeue(out var request))
                {
                    request.Done.TrySetCanceled();
                }
            }
        }
    }

    // ── The pane request ────────────────────────────────────────────────────

    /// <summary>
    /// A pane phase for the running watcher (the <c>ask_user</c> tool: it runs on the turn task and
    /// must not read keys itself). The watcher runs <paramref name="phase"/> on its own task at its
    /// next poll with the keys — the buffer set aside, <see cref="PendingLine"/> published first,
    /// the poll paused — exactly as a mid-turn line's hook is run, so the phase reads through
    /// <see cref="ReadInputAsync"/>. The task completes once the phase has ended and the buffer
    /// is restored; a phase that throws is swallowed like a hook that does. It is <em>cancelled</em>
    /// when no watcher is running or the watcher ends before reaching it: the phase never ran.
    /// A request posted while a line's hook holds the keys (a pane the user opened) runs after it.
    /// </summary>
    public Task RequestPaneAsync(Func<Task> phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        var request = new PaneRequest(phase, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_requestGate)
        {
            if (!_watching)
            {
                request.Done.TrySetCanceled();
                return request.Done.Task;
            }

            _requests.Enqueue(request);
            _requestSignal.TrySetResult();
        }

        return request.Done.Task;
    }

    private sealed record PaneRequest(Func<Task> Phase, TaskCompletionSource Done);

    /// <summary>The next request, if any; with none the signal is re-armed so the no-keyboard wait sees the next one.</summary>
    private bool TryTakeRequest(out PaneRequest request)
    {
        lock (_requestGate)
        {
            if (_requests.TryDequeue(out request!))
            {
                return true;
            }

            if (_requestSignal.Task.IsCompleted)
            {
                _requestSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return false;
        }
    }

    private Task RequestSignal()
    {
        lock (_requestGate)
        {
            return _requestSignal.Task;
        }
    }

    /// <summary>A request's phase for <see cref="ServiceAsync"/>: nothing goes back on the buffer, a throw is swallowed.</summary>
    private static Func<Task<IReadOnlyList<InputEvent>>> RequestPhase(PaneRequest request) => async () =>
    {
        try
        {
            await request.Phase().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The phase's own business (a pane that failed): the keys come back either way.
        }

        return [];
    };

    /// <summary>
    /// A line's hook for <see cref="ServiceAsync"/>: <paramref name="line"/> (the events the Enter
    /// ended, taken off the buffer's tail) goes back on it when the hook declined the line or
    /// failed — a hook that fails leaves the line as type-ahead.
    /// </summary>
    private static Func<Task<IReadOnlyList<InputEvent>>> LinePhase(Func<WatchedLine, Task<bool>> onLine, WatchedLine offered, IReadOnlyList<InputEvent> line) => async () =>
    {
        bool consumed;
        try
        {
            consumed = await onLine(offered).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            consumed = false;
        }

        return consumed ? [] : line;
    };

    /// <summary>
    /// Runs <paramref name="phase"/> on this task with the keys: <see cref="PendingLine"/> is
    /// published BEFORE the phase starts (it runs synchronously into its first wait — a pane's key
    /// read — and whoever that wait tells must already see it pending), the buffer is set aside
    /// while it runs and restored after — what was held, then what the phase hands back, then
    /// what its reader left, in the order typed — and the mirror redrawn; <paramref name="done"/>
    /// (a request's) completes with the restore. True when the phase ended; false when
    /// <paramref name="stop"/> fired first — the phase runs on as <see cref="PendingLine"/>.
    /// </summary>
    private async Task<bool> ServiceAsync(Func<Task<IReadOnlyList<InputEvent>>> phase, CancellationToken stop, TaskCompletionSource? done)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingLine = pending.Task;
        var aside = RunAsideAsync(phase);
        _ = aside.ContinueWith(static (_, state) =>
        {
            var (pending, done) = ((TaskCompletionSource, TaskCompletionSource?))state!;
            pending.TrySetResult();
            done?.TrySetResult();
        }, (pending, done), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var ended = await Task.WhenAny(aside, WaitForStopAsync(stop)).ConfigureAwait(false);
        if (ended != aside)
        {
            return false;
        }

        _pendingLine = Task.CompletedTask;
        await aside.ConfigureAwait(false);
        return true;
    }

    private async Task RunAsideAsync(Func<Task<IReadOnlyList<InputEvent>>> phase)
    {
        var held = _buffer.ToArray();
        _buffer.Clear();
        // A live row's list closes while a pane reads the keys (2026-09-25); the row is drawn again after.
        var editor = _editor;
        editor?.Suspend();
        var back = await phase().ConfigureAwait(false);

        // What the phase's reader left (nothing, normally: a pane reads until ESC) comes after
        // what was set aside, in the order it was typed.
        var after = _buffer.ToArray();
        _buffer.Clear();
        foreach (var e in held)
        {
            _buffer.Enqueue(e);
        }

        foreach (var e in back)
        {
            _buffer.Enqueue(e);
        }

        foreach (var e in after)
        {
            _buffer.Enqueue(e);
        }

        if (editor is not null)
        {
            // The pane's own slot emptied the row; the watch may have ended meanwhile, and the draft is still the session's.
            editor.Redraw();
        }
        else
        {
            Preview();
        }
    }

    /// <summary>The editor the running watch feeds (2026-09-25); null with none, or no watch.</summary>
    private InputLine.Editor? _editor;

    /// <summary>
    /// One event on the live row (2026-09-25): the editor's, and a line it hands back (Enter) is offered to
    /// <paramref name="onLine"/> as a <see cref="WatchedLine"/> — taken, the editor empties the row and remembers it
    /// (<see cref="InputLine.Editor.Accept"/>); kept, the draft stays (the hook may have put another there). With no hook
    /// the Enter is nothing: the draft waits for the idle line. A key the editor declines (a bare ESC under a watch whose
    /// cancel key is Ctrl+C alone) is type-ahead as ever. False when <paramref name="stop"/> fired under the hook's pane.
    /// </summary>
    private async Task<bool> FeedAsync(InputLine.Editor editor, InputEvent e, Func<WatchedLine, Task<bool>>? onLine, CancellationToken stop)
    {
        var outcome = await editor.FeedAsync(e, stop).ConfigureAwait(false);
        if (outcome == EditOutcome.Declined)
        {
            if (e is InputEvent.Key)
            {
                _buffer.Enqueue(e);
            }

            return true;
        }

        if (outcome is not EditOutcome.Submit { Line: var line } || onLine is null)
        {
            return true;
        }

        async Task<IReadOnlyList<InputEvent>> Phase()
        {
            bool taken;
            try
            {
                taken = await onLine(new WatchedLine(line.CommandText, [], line)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A hook that fails keeps the line on the row.
                taken = false;
            }

            if (taken)
            {
                editor.Accept(line);
            }

            return [];
        }

        return await ServiceAsync(Phase, stop, null).ConfigureAwait(false);
    }

    /// <summary>The buffer's last line — the events after the last Enter before the tail's Enter, that Enter included — taken off the tail.</summary>
    private List<InputEvent> TakeLastLine()
    {
        var all = _buffer.ToArray();
        int start = all.Length - 1;
        while (start > 0 && !(all[start - 1] is InputEvent.Key { Info: var ended } && Keys.IsSend(ended)))
        {
            start--;
        }

        var line = new List<InputEvent>(all.Length - start);
        for (int i = start; i < all.Length; i++)
        {
            line.Add(all[i]);
        }

        _buffer.Clear();
        for (int i = 0; i < start; i++)
        {
            _buffer.Enqueue(all[i]);
        }

        return line;
    }

    private void Preview()
    {
        string preview = PreviewText(_buffer, out var labels);
        Mirror?.PreviewInput(preview, labels);
    }

    private static void Cancel(CancellationTokenSource turnCts)
    {
        try
        {
            turnCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn finished as we asked it to stop.
        }
    }

    private static async Task WaitForStopAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }
}
