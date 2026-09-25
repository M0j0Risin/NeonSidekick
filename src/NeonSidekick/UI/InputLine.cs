using System.Text;
using NeonSidekick.Files;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>How a read of the input line ended.</summary>
public abstract record InputResult
{
    private InputResult()
    {
    }

    /// <summary>
    /// Enter on a non-blank line; <see cref="Text"/> has trailing whitespace trimmed and every
    /// token expanded (an image's to its <c>[Image #n]</c> label); <see cref="Images"/> are the
    /// pictures those labels name, in the order they stand in the text.
    /// </summary>
    public sealed record Submitted(string Text, IReadOnlyList<ImageAttachment> Images) : InputResult
    {
        public Submitted(string text)
            : this(text, [])
        {
        }
    }

    /// <summary>ESC on an empty line.</summary>
    public sealed record Cancelled : InputResult;

    /// <summary>No keyboard: stdin is redirected, or a scripted console ran dry.</summary>
    public sealed record EndOfInput : InputResult;

    /// <summary>The push-to-talk key on an empty line.</summary>
    public sealed record PushToTalk : InputResult;

    /// <summary>The wake token fired; <see cref="Draft"/> is whatever was on the line (the caller ignores the wake unless it is empty).</summary>
    public sealed record WakeWord(string Draft) : InputResult;

    /// <summary>The alert token fired (a timer expired); <see cref="Draft"/> is whatever was on the line and comes back on the next read.</summary>
    public sealed record Alert(string Draft) : InputResult;

    /// <summary>Ctrl+C with nothing to copy, stop or cancel, and the <c>interrupt</c> hook declining it: the second press within the window, the app should exit (since 2026-09-17).</summary>
    public sealed record Exit : InputResult;

    /// <summary>
    /// A double-click on the pane's hint row (2026-09-18): <see cref="Hit"/>
    /// says which part — a speech glyph, the model trailer or the row itself (the screen switches the
    /// feature off, opens the model picker or the settings); <see cref="Draft"/> is whatever was on
    /// the line and comes back on the next read.
    /// </summary>
    public sealed record HintRow(string Draft, ScreenPane.HintHit Hit) : InputResult;

    /// <summary>
    /// A double-click on the pane's toolbar (2026-09-21): <see cref="Hit"/>
    /// says which part — a pane glyph, the path or the blanks (the screen opens the pane, the
    /// folder picker, or the settings as for the hint row's blanks — the last since later that
    /// day); <see cref="Draft"/> as <see cref="HintRow"/>'s.
    /// </summary>
    public sealed record ToolbarRow(string Draft, ScreenPane.ToolbarHit Hit) : InputResult;
}

/// <summary>
/// The hand-written input line: the draft, a cursor, editing at the cursor and a session history.
/// Spectre's <c>TextPrompt</c> is not an option: its read loop swallows a bare ESC and it converts
/// through <c>TypeConverter</c> (reflection).
///
/// <para>The line draws nothing itself: every change hands the whole draft and the cursor to the
/// <see cref="ScreenPane"/>. With the pane on the screen the draft word-wraps over the pane's input
/// rows (<see cref="InputLayout"/>), the pane growing upward; without it the draft is one row that
/// scrolls horizontally (<see cref="Layout"/>), drawn where the cursor is, as it always was. Either
/// way no row ever fills the console's width (<see cref="AvailableCells"/>), so the terminal never
/// soft-wraps one — a cursor-left never climbs back over a wrapped line — and the redraws are
/// relative cursor moves plus <see cref="RawText"/>. On Enter the whole text is rendered once more
/// as a normal markup line, which may wrap, and that line is the transcript's record of what the
/// user said.</para>
///
/// <para>Keys: printable inserts; Backspace, Delete, Left, Right, Home, End edit; Up/Down walk the
/// history with the unsent draft kept at the bottom — but on the pane, while the draft wraps over
/// more than one row and the caret is not on the first (Up) or last (Down) row, they move the caret
/// a row instead (2026-09-21, the user's ask; <see cref="ScreenPane.TryStepInputRow"/>), keeping
/// the column of the first press as a goal across the run, Shift extending the selection, and a line
/// just recalled from the history walks on until it is edited; Enter submits, and on the chat line
/// (<c>multiline</c>) Ctrl+Enter types a line break instead (2026-09-22, <see cref="Keys.IsLineBreak"/>;
/// a field takes it as Enter); <b>ESC clears the line, and on an
/// empty line reports <see cref="InputResult.Cancelled"/> — it never quits</b>; no single key does (the
/// chat line's <c>softEscape</c> hook is asked first, so a spoken tail is stopped ahead of both).
/// Ctrl+C (since 2026-09-17) copies the selection when there is one, else asks the chat line's
/// <c>interrupt</c> hook — the screen stops the tail with it, or arms a two-press exit and shows the
/// hint — and reports <see cref="InputResult.Exit"/> only when the hook declines (the second press);
/// with no hook (a settings field) it is ESC. No key
/// is special-cased before the printable branch.
/// The push-to-talk key, when one is passed, is reported only from an <em>empty</em> line and only
/// as a bare key: a key that types a character is never a push-to-talk key.</para>
///
/// <para>Selection: a stretch between an <em>anchor</em> and the cursor, made by Shift+Left / Right /
/// Home / End, by Ctrl+A (everything), or by a drag from a left click on the pane's rows (the app
/// holds the mouse for the whole screen; Shift+drag stays the terminal's own copy-selection, which
/// the app never sees). A wheel notch over the screen scrolls the pane's transcript region
/// (<see cref="ScreenPane.ScrollWheel"/>), PgUp / PgDn a page, Ctrl+End is the bottom again
/// (<see cref="ScreenPane.ScrollToEnd"/>, 2026-09-17); none touches the draft. Delete and Backspace remove the selection, a typed character or a right-click
/// paste replaces it, an unshifted Left / Right collapses to its start / end, Home, End, Up, Down or
/// a click drop it. The pane draws it in <see cref="Theme.SelectedText"/>.</para>
///
/// <para>Paste: the terminal's paste arrives as one <see cref="InputEvent.Paste"/> (a burst of key
/// records, <see cref="PasteBurst"/>) and a right click reads the clipboard; both land on the line
/// through the same rule and <b>neither ever sends</b>: a line break inside a paste is a
/// <c>'\n'</c> in the draft, never Enter. On the chat line (<c>multiline</c>) the block keeps its
/// line breaks (<see cref="PasteText.Normalize"/>) and, past <see cref="PasteBlocks.InlineMaxLines"/>
/// lines or <see cref="PasteBlocks.InlineMaxChars"/> characters, is held in <see cref="Pastes"/>
/// behind one token drawn as <c>[Pasted text #1 +49 lines]</c>; Enter expands the tokens into the
/// text that is sent and the transcript keeps the labels. A settings field flattens a paste to one
/// paragraph (<see cref="PasteText.Flatten"/>).</para>
///
/// <para>Images: a file dropped on the window is a paste of its path, and on the chat line a paste
/// that is nothing but image file paths, one or several (<see cref="ImageFile.TryPastedPaths"/>), is
/// read there and then (<see cref="ImageFile.Load"/> each, the hint row busy meanwhile) and held in
/// <see cref="Pastes"/> behind a token per file drawn as <c>[Image #1]</c>, a space between; Enter hands the pictures over
/// in <see cref="InputResult.Submitted.Images"/> beside the text, in which the label stands. A
/// file that cannot be attached is one notice (the sentence from <see cref="ImageFile"/>) and its
/// path lands as text. A picture on the clipboard comes the same way through the line's own paste —
/// a right click, Ctrl+V where the terminal lets the chord through (Windows Terminal keeps it and
/// pastes text only), Alt+V — which takes the picture first (<c>clipboardImage</c>, named
/// <see cref="ImageFile.ClipboardName"/>) and the text otherwise; a settings field never takes a
/// picture. An Alt-only chord is never a typed character, so Alt+V does not type a <c>v</c>.</para>
/// </summary>
public sealed class InputLine
{
    /// <summary>The prompt glyph, in <see cref="Theme.User"/>. Pinned by tests.</summary>
    public const string PromptGlyph = "› ";

    /// <summary>What a wrapped continuation row starts with: the glyph's width in spaces. Pinned by tests.</summary>
    public const string ContinuationIndent = "  ";

    private readonly ScreenPane _pane;
    private readonly KeySource _keys;
    private readonly Func<string?>? _clipboard;
    private readonly Func<byte[]?>? _clipboardImage;
    private readonly Func<string, MentionResult>? _mentions;
    private readonly Func<IReadOnlyList<CompletionItem>>? _commands;
    private readonly Func<string, string, ArgumentList>? _arguments;
    private readonly Func<IReadOnlyList<CompletionItem>>? _skills;
    private readonly Func<IReadOnlyList<CompletionItem>>? _tools;
    private readonly Func<IReadOnlyList<CompletionItem>>? _connections;
    private readonly Func<IReadOnlyList<CompletionItem>>? _workflows;
    private readonly INoticeSink? _notices;
    private readonly Func<string, bool>? _copy;

    /// <summary>What a masked read draws for each character (one cell). Pinned.</summary>
    public const char MaskGlyph = '•';
    private readonly DoubleClick _hintClicks;
    private readonly List<string> _history = new();
    private readonly PasteBlocks _pastes = new();

    /// <summary>A line drawn where the cursor is (a pass-through pane over <paramref name="console"/>); <paramref name="copyToClipboard"/> as the pane ctor's.</summary>
    public InputLine(IAnsiConsole console, KeySource keys, Func<string, bool>? copyToClipboard = null)
        : this(console as ScreenPane ?? new ScreenPane(console ?? throw new ArgumentNullException(nameof(console)), null, TimeProvider.System), keys, copyToClipboard: copyToClipboard)
    {
    }

    /// <summary>
    /// A line drawn by <paramref name="pane"/>: on its input rows when the pane is on the screen.
    /// <paramref name="clipboard"/> is the text the line's own paste (a right click — the app holds
    /// the mouse for the whole screen since 2026-09-17, so the terminal never pastes — Ctrl+V, Alt+V)
    /// puts on the line; null = nothing. <paramref name="clipboardImage"/> is the picture the same paste
    /// takes first, as an image file's bytes (<see cref="WindowsClipboard.TryReadImage"/> in the app);
    /// null = a picture is never looked for.
    /// <paramref name="notices"/> is where the line says why a dropped or pasted image was not attached;
    /// null = a path just lands as text, a picture is dropped quietly.
    /// <paramref name="mentions"/> answers the @-mention list (<c>WorkingDirectory.Complete</c> in
    /// the app: the text after the <c>@</c> in, the matching paths out); null = no list ever.
    /// <paramref name="commands"/> is the command list's source (<c>SlashCommands.Completions</c>:
    /// the base commands with their summaries), read when a <c>/</c>word is typed at the start of
    /// the draft; <paramref name="arguments"/> the argument list's (<c>ChatScreen.ArgumentChoices</c>:
    /// the command word as typed and the argument text so far in, the candidates out — already
    /// narrowed, the whole argument each, since the source knows which commands take what; or,
    /// for a command whose argument is a sandbox path, the <c>@</c>-mention shape, <see cref="ArgumentList.IsPathList"/>,
    /// drawn and applied as the <c>@</c> list is — the folder mode included), read
    /// when something is typed after a <c>/</c>word; null = that list never opens.
    /// <paramref name="skills"/> is the <c>#</c>-mention list's source (2026-09-17; <c>ChatScreen.HashChoices</c>:
    /// the loaded skills with their descriptions, empty while the setting is off), read when a
    /// <c>#</c>word is under the cursor and narrowed here; a pick writes <c>#name</c> and a space —
    /// text, nothing seeded; null = that list never opens.
    /// <paramref name="tools"/> is the <c>$</c>-mention list's source (2026-09-19; <c>ChatScreen.DollarChoices</c>:
    /// the tools the next turn offers with their descriptions, empty while the setting is off), read
    /// when a <c>$</c>word is under the cursor and narrowed here; a pick writes <c>$name</c> and a
    /// space — text, nothing seeded; null = that list never opens.
    /// <paramref name="connections"/> is the <c>%</c>-mention list's source (later on 2026-09-23; <c>ChatScreen.PercentChoices</c>:
    /// the SQL connections of <c>sql.json</c> with their server, database and description, empty while the setting or the
    /// SQL tools are off), the <c>$</c> shape: a pick writes <c>%name</c> and a space; null = that list never opens.
    /// <paramref name="workflows"/> is the <c>^</c>-mention list's source (later still on 2026-09-24; <c>ChatScreen.CaretChoices</c>:
    /// the ComfyUI workflows the model is offered with their family, shape and size, empty while the setting is off or the
    /// image tools are not offered), the same shape: a pick writes <c>^name</c> and a space; null = that list never opens.
    /// <paramref name="copyToClipboard"/> is what Ctrl+C over a selection writes the selected text
    /// with (2026-09-17; <see cref="WindowsClipboard.TrySetText"/> in the app, the same writer as
    /// <c>/copy</c>'s; tests record the text), true on success; null = every copy fails and says so.
    /// </summary>
    public InputLine(ScreenPane pane, KeySource keys, Func<string?>? clipboard = null, INoticeSink? notices = null, Func<byte[]?>? clipboardImage = null, Func<string, MentionResult>? mentions = null, Func<IReadOnlyList<CompletionItem>>? commands = null, Func<string, string, ArgumentList>? arguments = null, Func<IReadOnlyList<CompletionItem>>? skills = null, Func<IReadOnlyList<CompletionItem>>? tools = null, Func<string, bool>? copyToClipboard = null, Func<IReadOnlyList<CompletionItem>>? connections = null, Func<IReadOnlyList<CompletionItem>>? workflows = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _clipboard = clipboard;
        _clipboardImage = clipboardImage;
        _mentions = mentions;
        _commands = commands;
        _arguments = arguments;
        _skills = skills;
        _tools = tools;
        _connections = connections;
        _workflows = workflows;
        _notices = notices;
        _copy = copyToClipboard;
        _hintClicks = new DoubleClick(pane.Time);
    }

    /// <summary>Ctrl+C over a selection when the clipboard would not take it. Pinned.</summary>
    public const string CopyFailedNotice = "Could not write the selection to the clipboard; try again.";

    /// <summary>The hint row's label while a dropped image is read. Pinned.</summary>
    public const string ReadingImage = "reading image…";

    /// <summary>The hint row while several dropped files are read.</summary>
    public static string ReadingImages(int count) => $"reading {count} images…";

    /// <summary>Submitted lines this session, oldest first; consecutive duplicates collapsed. A collapsed paste is its token here (the block is in <see cref="Pastes"/>).</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>The long pastes of this session, behind the tokens the drafts hold.</summary>
    public PasteBlocks Pastes => _pastes;

    /// <summary>Adds a line the user did not type (a spoken one) to the history, with the same de-duplication as Enter.</summary>
    public void Remember(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && (_history.Count == 0 || !string.Equals(_history[^1], text, StringComparison.Ordinal)))
        {
            _history.Add(text);
        }
    }

    /// <summary>The transcript line for something the user said, its lines after the first under the glyph (<see cref="ContinuationIndent"/>). Escaped.</summary>
    /// <summary>
    /// What two hint-row clicks must share to pair (<see cref="DoubleClick.Second"/>): the zone, or
    /// for a strip glyph its column past every zone's number (2026-09-18; <c>2 + column</c> until the
    /// queued zone, when the first glyph's key was the trailer's). Pinned.
    /// </summary>
    public static int HintPairKey(ScreenPane.HintHit hit) => hit.Zone == ScreenPane.HintZone.Strip ? 8 + hit.Column : (int)hit.Zone;

    /// <summary>The hint-pair key of a click off an open pane under a typed value (later on 2026-09-18): below every zone's number, and never the pairing's own −1.</summary>
    public const int OutsidePairKey = -2;

    /// <summary>
    /// What two toolbar clicks must share to pair (2026-09-21), in the hint pairing's own key space:
    /// below <see cref="OutsidePairKey"/>, never the pairing's −1 and never a <see cref="HintPairKey"/>
    /// value — the path, the blanks (their own key since later that day: a pair there is
    /// <c>/settings</c>, as the hint row's blanks are), then one key per glyph column. Pinned.
    /// </summary>
    public static int ToolbarPairKey(ScreenPane.ToolbarHit hit) => hit.Zone switch
    {
        ScreenPane.ToolbarZone.Path => -3,
        ScreenPane.ToolbarZone.Row => -4,
        _ => -5 - hit.Column,
    };

    /// <summary>
    /// What two clicks on a transcript picture must share to pair (later on 2026-09-24): the picture's id, far below every
    /// toolbar key (<see cref="ToolbarPairKey"/> goes down one per column), so no two parts ever share one. Pinned.
    /// </summary>
    public static int PicturePairKey(int id) => -1_000_000 - id;

    /// <summary>What a double-click on a transcript picture does with its id (later on 2026-09-24): the screen's opener; null = nothing.</summary>
    public Action<int>? OpenPicture { get; set; }

    public static string SubmittedMarkup(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat("[", Theme.ToHex(Theme.Secondary), " bold]", Markup.Escape(PromptGlyph + text.Replace("\n", "\n" + ContinuationIndent, StringComparison.Ordinal)), "[/]");
    }

    /// <summary>
    /// The previews of a sent line's collapsed pastes (<see cref="PasteBlocks.Previews"/>) as one
    /// text under it: a single block is its preview alone (it sits right under its label); two or
    /// more are each headed by their label, so the reader knows where one ends. Empty when there
    /// is nothing to show.
    /// </summary>
    public static string PreviewText(IReadOnlyList<(string Label, string Preview)> previews)
    {
        ArgumentNullException.ThrowIfNull(previews);
        if (previews.Count == 1)
        {
            return previews[0].Preview;
        }

        var text = new StringBuilder();
        foreach (var (label, preview) in previews)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(label).Append('\n').Append(preview);
        }

        return text.ToString();
    }

    /// <summary>The transcript's preview under a sent line (<see cref="PreviewText"/>): every line under the glyph (<see cref="ContinuationIndent"/>), dim. Escaped.</summary>
    public static string PreviewMarkup(string preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return string.Concat("[", Theme.ToHex(Theme.Dim), "]", Markup.Escape(ContinuationIndent + preview.Replace("\n", "\n" + ContinuationIndent, StringComparison.Ordinal)), "[/]");
    }

    /// <summary>
    /// Reads one line. <paramref name="initialText"/> is shown and editable (settings fields);
    /// <paramref name="remember"/> false keeps the line out of <see cref="History"/>;
    /// <paramref name="allowEmpty"/> lets Enter submit an empty line (clearing a URL is a meaningful
    /// act in a settings field, and never in chat); <paramref name="pushToTalk"/>, when set, makes
    /// that key on an empty line return <see cref="InputResult.PushToTalk"/> (settings fields never
    /// pass it). <paramref name="wake"/>, when it fires, ends the row and returns
    /// <see cref="InputResult.WakeWord"/> with the draft; it is the wake-word listener's signal and
    /// never throws. <paramref name="alert"/> does the same with <see cref="InputResult.Alert"/> (a
    /// timer expired, or the spoken tail under the line ended and the loop re-arms the microphone;
    /// the wake wins when both fired, its hit is waiting and the alert keeps).
    /// <paramref name="escapeCancels"/> makes ESC end the read with <see cref="InputResult.Cancelled"/>
    /// at once, draft or not (a settings value: the saved one is kept); the chat line's ESC clears
    /// the draft first and cancels only from an empty row. <paramref name="multiline"/> is the chat
    /// line's paste rule (line breaks kept, long blocks behind a token); off, a paste is one paragraph.
    /// <paramref name="mentions"/> turns the @-mention list on (the chat line on the pane; null =
    /// off, every other read): an <c>@</c>word under the cursor lists the working directory's
    /// matches above the row (<see cref="MentionCompleter"/>); Up/Down move the highlight, Enter
    /// and Tab apply it — Enter never sends while the list is open — and ESC closes the list,
    /// the draft kept; the value says what applying a folder does. The list is derived from the
    /// draft after every edit, never from the key (a fast <c>@a</c> arrives as one paste), and
    /// closed by every exit of the read. What is sent is the literal text, <c>@path</c> included.
    /// The same switch turns on the command list (a <c>/</c>word at the start of the draft lists
    /// the base commands, <see cref="MentionCompleter.TryFindCommand"/>) and the argument list (the
    /// text after a <c>/</c>word, <see cref="MentionCompleter.TryFindArgument"/>: a skill name, on |
    /// off, a profile, a timer to stop …) when the line has their sources; a pick writes the word
    /// and a space, and a word typed in full closes its list so Enter sends
    /// (<see cref="MentionCompleter.Matches"/>).
    /// <paramref name="pastePreview"/> is how many lines of each collapsed paste the transcript shows
    /// under the sent line (<see cref="PasteBlocks.Preview"/>, drawn by <see cref="PreviewMarkup"/>);
    /// 0 keeps the label alone (every read but the chat line).
    /// <paramref name="softEscape"/> is asked on every ESC before the key does anything else, on
    /// this task: true means the key was spent (the screen stops the spoken tail with it) and the
    /// read goes on with the draft and any open list untouched; false or null is ESC as ever —
    /// close the list, clear the draft, or <see cref="InputResult.Cancelled"/> from an empty row.
    /// It never touches the console and never throws; a settings field never passes it.
    /// <paramref name="interrupt"/> is asked on a Ctrl+C with no selection to copy (2026-09-17), on
    /// this task, the same contract: true means the key was spent (the screen stops the spoken
    /// tail with it, or arms its two-press exit and shows the hint) and the read goes on with the
    /// draft and any open list untouched; false ends the read as <see cref="InputResult.Exit"/>;
    /// null (a settings field) makes the key ESC.
    /// <paramref name="intercept"/> is asked once per Enter with the text about to be sent (the
    /// pastes expanded), on this task, ahead of the transcript row and the history (2026-09-18):
    /// null means send as typed; a string means the line is swallowed — never committed, never
    /// remembered — and the string is the draft, the cursor at its end, the read going on. The hook
    /// may read the keys itself (the screen opens a pane on it: the typo intercept) since the read
    /// is not reading while it awaits; it gets <paramref name="cancellationToken"/>, never the wake
    /// or alert token, so an alert firing while it is up cannot decide the line. A settings field
    /// never passes it.
    /// The pane's hint row has a meaning (2026-09-18; under the <c>Mouse in menus</c> setting and
    /// a <c>hintDoubleClick</c> flag until 2026-09-21, on every read since — a menu's slot never
    /// draws the row, so its reads never see a hit): two left clicks on it within <see cref="DoubleClick.Interval"/>
    /// end the read as <see cref="InputResult.HintRow"/> with the draft (the screen opens the
    /// settings and reads again with it — nothing committed, nothing remembered); a first click there
    /// is nothing, and a key, a wheel notch or a click anywhere else ends the pair. A pair on the
    /// scroll's hint (<see cref="ScreenPane.HintZone.Scrolled"/>, later on 2026-09-18) never ends the
    /// read: it is the bottom again, as Ctrl+End, the draft kept. Under a menu's typed-value slot
    /// two left clicks off the pane (<see cref="ScreenPane.TryHitOutside"/>) are
    /// <see cref="ScreenPane.Dismiss"/> and the ESC key (later on 2026-09-18).
    /// <paramref name="beforeCommit"/> (2026-09-18) runs once per Enter that sends, after the intercept
    /// let the line through and ahead of the transcript row and the history — the screen wipes the
    /// welcome splash with it, so the sent line lands under the banner. An empty Enter and an
    /// intercepted line never reach it; a settings field never passes it.
    /// <paramref name="replay"/> (2026-09-18) is the events of a queued line — what the watcher took
    /// off its buffer while a reply ran, its Enter last — handled first, exactly as if typed now:
    /// the paste arm, the characters, the Enter arm with the intercept, the transcript row, the
    /// history. While any is pending the console and the wake / alert tokens are not looked at,
    /// so the line goes whole; a run without an Enter leaves its text as the draft and the read goes on.
    /// <paramref name="emptyArrow"/> (2026-09-19) is asked on a plain Left or Right — no Shift, Ctrl
    /// or Alt — over an empty draft (nothing to move, no selection to make), on this task, with −1
    /// for Left and +1 for Right: true means the key was spent (the screen turns the welcome splash
    /// to the previous or next picture; the pane's own draw puts the row back) and the read goes on;
    /// false or null is the key as ever, which on an empty draft is nothing. A settings field never passes it.
    /// <paramref name="emptyDelete"/> (2026-09-24, the user's ask) is asked the same way on a plain Delete over an
    /// empty draft, with whether the event just before it was a Delete it spent — the "twice in a row" is the
    /// line's to say, since the screen never sees the keys in between: true means the key was spent (the screen
    /// arms, or deletes the profile's splash picture on screen), false or null the key as ever.
    /// <paramref name="emptyEnter"/> (later still on 2026-09-24, the picture strip) is asked on an Enter that has nothing
    /// to send over an empty draft — a row of spaces is not asked (and <paramref name="allowEmpty"/> off): true means the key was spent (the screen opens the highlighted
    /// picture) and the read goes on; false or null the key as ever — the blank draft cleared.
    /// <paramref name="mask"/> (later on 2026-09-23, the SQL tab's <c>SQL set password</c>) draws every character as
    /// <see cref="MaskGlyph"/> — the cursor, the selection and the editing keys as ever — never remembers the line, and
    /// never copies a selection of it (Ctrl+C over one is the copy-failed notice, not the secret on the clipboard).
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task<InputResult> ReadAsync(string initialText = "", bool remember = true, bool allowEmpty = false, ConsoleKey? pushToTalk = null, CancellationToken cancellationToken = default, CancellationToken wake = default, CancellationToken alert = default, bool escapeCancels = false, bool multiline = false, MentionFolderAction? mentions = null, int pastePreview = 0, Func<bool>? softEscape = null, Func<bool>? interrupt = null, Func<string, CancellationToken, Task<string?>>? intercept = null, Action? beforeCommit = null, IReadOnlyList<InputEvent>? replay = null, Func<int, bool>? emptyArrow = null, bool mask = false, Func<bool, bool>? emptyDelete = null, Func<bool>? emptyEnter = null)
    {
        ArgumentNullException.ThrowIfNull(initialText);

        var text = new StringBuilder(initialText);
        int cursor = text.Length;
        // The selection's other end; −1 = none. There is a selection only while it differs from the cursor.
        int anchor = -1;
        int historyIndex = _history.Count;
        string draft = "";
        // The Up/Down row moves (2026-09-21): the cell column of the run's first press, kept while the
        // arrows repeat so a short row in between does not lose it; and whether the last arrow recalled
        // a history line, in which case the next one walks on rather than climbing the recalled rows.
        // Any other input ends both.
        int goalCol = -1;
        bool walking = false;
        // The last event was an empty Delete that emptyDelete spent (2026-09-24): the next one's repeat flag.
        bool deleteSpent = false;
        // The completion list (@-mention, command, argument, #skill, $tool): open while `list` is set;
        // `dismissed` is the word ESC closed it on, so a cursor move over the same word does not
        // bring it straight back. The chat line on the pane is the only read with any of the five.
        bool onPane = mentions is not null && _pane.Enabled;
        bool completing = onPane && _mentions is not null;
        bool commanding = onPane && _commands is not null;
        bool arguing = onPane && _arguments is not null;
        bool hashing = onPane && _skills is not null;
        bool dollaring = onPane && _tools is not null;
        bool percenting = onPane && _connections is not null;
        bool careting = onPane && _workflows is not null;
        MentionList? list = null;
        (int Start, string Query)? dismissed = null;

        using var linked = wake.CanBeCanceled || alert.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wake, alert) : null;
        var readToken = linked?.Token ?? cancellationToken;
        // A queued line's events, ahead of the console (2026-09-18).
        var pending = replay is { Count: > 0 } ? new Queue<InputEvent>(replay) : null;

        _pane.BeginInput();
        _hintClicks.Reset();
        try
        {
            Redraw();

            while (true)
            {
                InputEvent? input;
                if (pending is { Count: > 0 })
                {
                    input = pending.Dequeue();
                }
                else
                {
                    try
                    {
                        input = await _keys.ReadInputAsync(readToken).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        EndRow();
                        return new InputResult.EndOfInput();
                    }
                }

                if (input is not InputEvent.Key)
                {
                    goalCol = -1;
                    walking = false;
                }

                // Whether the event before this one was an empty Delete the screen spent (2026-09-24):
                // any event at all in between — a key, a click, a paste, a wheel notch — ends the pair.
                bool deleteRepeat = deleteSpent;
                deleteSpent = false;

                if (input is InputEvent.Paste paste)
                {
                    // The terminal's paste: one block on the line, never a submission.
                    _hintClicks.Reset();
                    await InsertPasteAsync(paste.Text).ConfigureAwait(false);
                    continue;
                }

                if (input is InputEvent.Click { Button: MouseButton.Left } close && _pane.TryHitClose(close.X, close.Y))
                {
                    // The × at an overlay's corner (a typed settings value under its menu, 2026-09-18)
                    // is the ESC key: the same path, hooks and all.
                    input = new InputEvent.Key(Keys.Escape);
                }
                else if (input is InputEvent.Click { Button: MouseButton.Left } outside && _pane.TryHitOutside(outside.X, outside.Y))
                {
                    // Off the pane under a typed value (later on 2026-09-18): the second click within
                    // the interval closes every level (ScreenPane.Dismiss) through the ESC key's own
                    // path; the first is nothing. Ahead of the click branch, whose miss resets the pair.
                    // The pair is per part (later on 2026-09-21, ScreenPane.OutsideKey): two on the
                    // same toolbar glyph, say, and the dismiss remembers it for the screen's switch.
                    anchor = -1;
                    if (!_hintClicks.Second(_pane.OutsideKey(outside.X, outside.Y)))
                    {
                        continue;
                    }

                    _pane.Dismiss(outside.X, outside.Y);
                    input = new InputEvent.Key(Keys.Escape);
                }

                if (input is InputEvent.Click click)
                {
                    // A left click on the area puts the cursor under it and anchors a selection there
                    // (a drag extends it); a right click pastes there, over the selection if any.
                    // Neither is typing: the draft, the history and the push-to-talk rule see nothing.
                    // A second left click on the hint row within the interval ends the read (2026-09-18).
                    if (click.Button == MouseButton.Left)
                    {
                        if (_pane.TryHitInput(click.X, click.Y, out int at))
                        {
                            _hintClicks.Reset();
                            cursor = DraftIndex(at);
                            anchor = cursor;
                            Redraw();
                        }
                        else if (_pane.TryHitHint(click.X, click.Y, out var hit))
                        {
                            // Paired per part: two clicks on different glyphs, or one on the model
                            // and one on the row, are two firsts.
                            anchor = -1;
                            if (_hintClicks.Second(HintPairKey(hit)))
                            {
                                if (hit.Zone == ScreenPane.HintZone.Scrolled)
                                {
                                    // The scroll's hint: the bottom again, as Ctrl+End — the read
                                    // goes on with the draft and the cursor where they were.
                                    _pane.ScrollToEnd();
                                    continue;
                                }

                                EndRow();
                                return new InputResult.HintRow(text.ToString(), hit);
                            }
                        }
                        else if (_pane.TryHitToolbar(click.X, click.Y, out var tool))
                        {
                            // The toolbar (2026-09-21): a glyph, the path or the blanks (a pair
                            // there since later that day: the screen's /settings, as the hint
                            // row's blanks), paired per part like the hint row's.
                            anchor = -1;
                            if (_hintClicks.Second(ToolbarPairKey(tool)))
                            {
                                EndRow();
                                return new InputResult.ToolbarRow(text.ToString(), tool);
                            }
                        }
                        else if (_pane.PictureAt(click.X, click.Y) is int picture)
                        {
                            // A picture in the transcript (later on 2026-09-24, the user's ask): a double-click
                            // opens it in the image editor; the draft is untouched.
                            anchor = -1;
                            if (_hintClicks.Second(PicturePairKey(picture)))
                            {
                                OpenPicture?.Invoke(picture);
                            }
                        }
                        else
                        {
                            // A tool run's summary in the transcript (2026-09-22): one click unfolds
                            // or folds it; the draft is untouched. Any other row ends a pair.
                            _pane.TryToggleToolGroupAt(click.X, click.Y);
                            _hintClicks.Reset();
                            anchor = -1;
                        }
                    }
                    else
                    {
                        _hintClicks.Reset();
                        await PasteFromClipboardAsync().ConfigureAwait(false);
                    }

                    continue;
                }

                if (input is InputEvent.Drag drag)
                {
                    // The button is still down from a click on the area: the cursor follows, the anchor
                    // stays. Off the rows, or after a click that missed, the drag is nothing.
                    if (anchor >= 0 && _pane.TryHitInput(drag.X, drag.Y, out int to))
                    {
                        int at = DraftIndex(to);
                        if (at != cursor)
                        {
                            cursor = at;
                            Redraw();
                        }
                    }

                    continue;
                }

                // Anything but a click or a drag ends a hint-row pair: the next click there is a first again.
                _hintClicks.Reset();
                if (input is InputEvent.Wheel wheel)
                {
                    // The wheel scrolls the transcript region (the app holds the mouse for the
                    // screen, 2026-09-17), the draft and the list untouched — unless a pane is open
                    // over the line (a settings slot under a menu: the pane is modal, the notch is
                    // nothing there). Never the cancel the null key below means.
                    if (!_pane.OverlayOpen)
                    {
                        _pane.ScrollWheel(wheel.Notches);
                    }

                    continue;
                }

                ConsoleKeyInfo? key = (input as InputEvent.Key)?.Info;
                if (key is null)
                {
                    EndRow();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (wake.IsCancellationRequested)
                    {
                        return new InputResult.WakeWord(text.ToString());
                    }

                    if (alert.IsCancellationRequested)
                    {
                        return new InputResult.Alert(text.ToString());
                    }

                    return new InputResult.EndOfInput();
                }

                var k = key.Value;
                if (k.Key is not (ConsoleKey.UpArrow or ConsoleKey.DownArrow))
                {
                    goalCol = -1;
                    walking = false;
                }

                // A click's anchor lives for the drag that may follow it; once a key arrives, an anchor
                // on the cursor is no selection (a Backspace or a plain arrow would otherwise move the
                // cursor off it and leave it behind as a phantom selection — past the text's end after a
                // click at the end, where the next edit threw out of the read).
                if (anchor == cursor)
                {
                    anchor = -1;
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
                    text.Insert(cursor, k.KeyChar);
                    cursor++;
                    Redraw();
                    continue;
                }

                // Only from an empty line: with text on the row the key is just ignored (pinned).
                if (pushToTalk is { } ptt && k.Key == ptt && text.Length == 0)
                {
                    EndRow();
                    return new InputResult.PushToTalk();
                }

                // ESC's first job is silence: the hook is asked ahead of the list and the draft.
                if (k.Key == ConsoleKey.Escape && softEscape is not null && softEscape())
                {
                    // The key was spent (the tail stopped); the draft and the list stay.
                    continue;
                }

                // With the list open five keys are its: the arrows move the highlight, Enter and Tab
                // apply it, ESC closes it. Everything else edits the line as ever, and the redraw
                // refreshes the list from the draft.
                if (list is { } open)
                {
                    switch (k.Key)
                    {
                        case ConsoleKey.UpArrow:
                            list = open.Move(-1);
                            ShowList();
                            continue;

                        case ConsoleKey.DownArrow:
                            list = open.Move(+1);
                            ShowList();
                            continue;

                        case ConsoleKey.Enter:
                        case ConsoleKey.Tab:
                            ApplyMention(open);
                            continue;

                        case ConsoleKey.Escape:
                            dismissed = (open.Start, open.Query);
                            CloseList();
                            continue;
                    }
                }

                bool shift = (k.Modifiers & ConsoleModifiers.Shift) != 0;
                switch (k.Key)
                {
                    case ConsoleKey.Enter when multiline && Keys.IsLineBreak(k):
                        // Ctrl+Enter (2026-09-22): a line break in the draft, as a pasted one is.
                        Insert("\n");
                        break;

                    case ConsoleKey.Enter:
                    {
                        // The draft keeps its tokens (the history recalls them); what is sent has the
                        // blocks in their place; the transcript keeps the labels.
                        string draftText = text.ToString().TrimEnd();
                        string submitted = _pastes.Expand(draftText).TrimEnd();
                        if (submitted.Length == 0 && !allowEmpty)
                        {
                            if (text.Length == 0 && emptyEnter is not null && emptyEnter())
                            {
                                continue;
                            }

                            // Nothing to send; a row of spaces is not worth keeping either.
                            text.Clear();
                            cursor = 0;
                            anchor = -1;
                            Redraw();
                            break;
                        }

                        // The typo intercept's chance (2026-09-18): a replacement is the new draft and
                        // the line was never sent — nothing committed, nothing remembered.
                        if (intercept is not null && await intercept(submitted, cancellationToken).ConfigureAwait(false) is { } replacement)
                        {
                            text.Clear().Append(replacement);
                            cursor = text.Length;
                            anchor = -1;
                            dismissed = null;
                            Redraw();
                            break;
                        }

                        // The row becomes a normal markup line: it may wrap now, and it is what
                        // the transcript keeps — with the start of each collapsed paste under it.
                        beforeCommit?.Invoke();
                        // A masked line is committed as its glyphs (later on 2026-09-23): the flow keeps the row, never the secret.
                        _pane.CommitInput(mask ? new string(MaskGlyph, draftText.Length) : _pastes.Display(draftText), mask ? "" : PreviewText(_pastes.Previews(draftText, pastePreview)));
                        if (remember && !mask && (_history.Count == 0 || !string.Equals(_history[^1], draftText, StringComparison.Ordinal)))
                        {
                            _history.Add(draftText);
                        }

                        return new InputResult.Submitted(submitted, _pastes.ImagesIn(draftText));
                    }

                    case ConsoleKey.Escape:
                        if (text.Length == 0 || escapeCancels)
                        {
                            EndRow();
                            return new InputResult.Cancelled();
                        }

                        text.Clear();
                        cursor = 0;
                        anchor = -1;
                        Redraw();
                        break;

                    case ConsoleKey.Backspace:
                    {
                        if (HasSelection())
                        {
                            DeleteSelection();
                            Redraw();
                            break;
                        }

                        int len = TextCells.ElementLengthBefore(text.ToString(), cursor);
                        if (len > 0)
                        {
                            text.Remove(cursor - len, len);
                            cursor -= len;
                            Redraw();
                        }

                        break;
                    }

                    case ConsoleKey.Delete:
                        // A plain Delete over an empty draft has nothing to remove: the screen may spend
                        // it on the splash picture (2026-09-24), told whether the key before was one too.
                        if (text.Length == 0 && !shift && !control && !alt && emptyDelete is not null && emptyDelete(deleteRepeat))
                        {
                            deleteSpent = true;
                            continue;
                        }

                        if (HasSelection())
                        {
                            DeleteSelection();
                            Redraw();
                            break;
                        }

                        if (cursor < text.Length)
                        {
                            TextCells.ElementWidth(text.ToString(), cursor, out int len);
                            text.Remove(cursor, len);
                            Redraw();
                        }

                        break;

                    case ConsoleKey.LeftArrow:
                        // A plain arrow over an empty draft has nothing to move: the screen may spend
                        // it on the welcome splash (2026-09-19).
                        if (text.Length == 0 && !shift && !control && !alt && emptyArrow is not null && emptyArrow(-1))
                        {
                            continue;
                        }

                        if (shift)
                        {
                            Anchor();
                            cursor -= TextCells.ElementLengthBefore(text.ToString(), cursor);
                        }
                        else if (HasSelection())
                        {
                            // Collapse onto the selection's start, as every editor does.
                            cursor = Math.Min(anchor, cursor);
                            anchor = -1;
                        }
                        else
                        {
                            cursor -= TextCells.ElementLengthBefore(text.ToString(), cursor);
                        }

                        Redraw();
                        break;

                    case ConsoleKey.RightArrow:
                        if (text.Length == 0 && !shift && !control && !alt && emptyArrow is not null && emptyArrow(+1))
                        {
                            continue;
                        }

                        if (!shift && HasSelection())
                        {
                            cursor = Math.Max(anchor, cursor);
                            anchor = -1;
                            Redraw();
                        }
                        else if (cursor < text.Length)
                        {
                            if (shift)
                            {
                                Anchor();
                            }

                            TextCells.ElementWidth(text.ToString(), cursor, out int len);
                            cursor += len;
                            Redraw();
                        }

                        break;

                    case ConsoleKey.Home when control:
                        // Ctrl+Home (2026-09-18): the transcript's first rows; the draft, cursor and
                        // selection untouched. Nothing on a transcript that fits.
                        _pane.ScrollToTop();
                        break;

                    case ConsoleKey.Home:
                        anchor = shift ? Anchor() : -1;
                        cursor = 0;
                        Redraw();
                        break;

                    case ConsoleKey.O when Keys.IsToolToggle(k):
                        // Ctrl+O (2026-09-22): every tool run in the transcript unfolded or folded;
                        // the draft, cursor and selection untouched.
                        _pane.ToggleToolGroups();
                        break;

                    case ConsoleKey.End when control:
                        // Ctrl+End (2026-09-17): the scrolled transcript back to the bottom, so the
                        // rows arriving show again; the draft, cursor and selection untouched. Nothing
                        // while not scrolled.
                        _pane.ScrollToEnd();
                        break;

                    case ConsoleKey.End:
                        anchor = shift ? Anchor() : -1;
                        cursor = text.Length;
                        Redraw();
                        break;

                    case ConsoleKey.PageUp:
                        // The transcript region, a page back; the pane clamps (nothing on a
                        // short transcript). A push-to-talk PgUp took the empty line above.
                        _pane.ScrollPage(-1);
                        break;

                    case ConsoleKey.PageDown:
                        _pane.ScrollPage(1);
                        break;

                    case ConsoleKey.A when control:
                        // Select all (the terminal's own select-all is Ctrl+Shift+A, so this one arrives).
                        if (text.Length > 0)
                        {
                            anchor = 0;
                            cursor = text.Length;
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
                        if (HasSelection())
                        {
                            int start = Math.Min(Math.Min(anchor, cursor), text.Length);
                            int end = Math.Min(Math.Max(anchor, cursor), text.Length);
                            if (mask || _copy is null || !_copy(_pastes.Expand(text.ToString(start, end - start))))
                            {
                                _notices?.Notice(CopyFailedNotice);
                            }

                            break;
                        }

                        if (interrupt is null)
                        {
                            goto case ConsoleKey.Escape;
                        }

                        if (interrupt())
                        {
                            break;
                        }

                        EndRow();
                        return new InputResult.Exit();

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
                        if (!walking && _pane.TryStepInputRow(delta, goalCol, out int at, out int col))
                        {
                            goalCol = col;
                            if (shift)
                            {
                                Anchor();
                            }
                            else
                            {
                                anchor = -1;
                            }

                            cursor = DraftIndex(at);
                            Redraw();
                            break;
                        }

                        if (delta < 0)
                        {
                            if (historyIndex > 0)
                            {
                                if (historyIndex == _history.Count)
                                {
                                    draft = text.ToString();
                                }

                                historyIndex--;
                                Replace(_history[historyIndex]);
                                walking = true;
                            }
                        }
                        else if (historyIndex < _history.Count)
                        {
                            historyIndex++;
                            Replace(historyIndex == _history.Count ? draft : _history[historyIndex]);
                            walking = true;
                        }

                        break;
                    }

                    default:
                        // Function keys, Tab, Ctrl chords: nothing to type.
                        break;
                }
            }
        }
        finally
        {
            // Every exit — Enter, ESC, the wake word, an alert, no keyboard, cancellation — takes the list with it.
            CloseList();
        }

        void Replace(string value)
        {
            text.Clear().Append(value);
            cursor = text.Length;
            anchor = -1;
            Redraw();
        }

        bool HasSelection() => anchor >= 0 && anchor != cursor;

        // The anchor for a Shift+move: where the cursor is now unless a selection already has one.
        int Anchor() => anchor = anchor < 0 ? cursor : anchor;

        // The selected stretch removed, the cursor at its start; nothing without a selection.
        void DeleteSelection()
        {
            if (HasSelection())
            {
                // Both ends held to the text: a selection can never reach past it, and a removal
                // that could must not take the read down.
                int start = Math.Min(Math.Min(anchor, cursor), text.Length);
                int end = Math.Min(Math.Max(anchor, cursor), text.Length);
                text.Remove(start, end - start);
                cursor = start;
            }

            anchor = -1;
        }

        // The line's own paste (a right click, Ctrl+V, Alt+V): the picture on the clipboard first,
        // on the chat line only; else its text through the paste rule; nothing when it holds neither.
        async Task PasteFromClipboardAsync()
        {
            if (multiline && _clipboardImage?.Invoke() is { } picture)
            {
                await InsertClipboardImageAsync(picture).ConfigureAwait(false);
            }
            else if (_clipboard?.Invoke() is { } clip)
            {
                await InsertPasteAsync(clip).ConfigureAwait(false);
            }
        }

        // A picture off the clipboard becomes one image token at the cursor, read like a dropped
        // file (off the thread, the hint row busy) and numbered with them; one that cannot be
        // attached is one notice and nothing on the line — there is no path to leave behind.
        async Task InsertClipboardImageAsync(byte[] picture)
        {
            string? error = null;
            string name = ImageFile.ClipboardName(_pastes.ImageCount + 1);
            ImageAttachment? image;
            using (_pane.BeginBusy(ReadingImage))
            {
                image = await Task.Run(() => ImageFile.Load(picture, name, out error)).ConfigureAwait(false);
            }

            if (image is null)
            {
                if (error is not null)
                {
                    _notices?.Notice(error);
                }

                return;
            }

            // The clipboard's own bytes kept beside it (later still on 2026-09-24): generate_image's input at full size.
            Insert(_pastes.AddImage(image, original: picture).ToString());
        }

        // What a paste becomes on the line, in place of the selection: the chat line keeps the
        // block's line breaks, holds a long block behind a token and reads a dropped image file
        // into one; a field takes one paragraph.
        async Task InsertPasteAsync(string pasted)
        {
            string block = multiline ? PasteText.Normalize(pasted) : PasteText.Flatten(pasted);
            if (block.Length == 0)
            {
                return;
            }

            string inserted = block;
            if (multiline && ImageFile.TryPastedPaths(block, out var paths))
            {
                // One token per file, a space between; a file that cannot be read stays as its
                // path (the notice says why), so nothing dropped is lost from the line.
                var pieces = new List<string>(paths.Count);
                using (_pane.BeginBusy(paths.Count == 1 ? ReadingImage : ReadingImages(paths.Count)))
                {
                    foreach (var path in paths)
                    {
                        string? error = null;
                        var image = await Task.Run(() => ImageFile.Load(path, out error)).ConfigureAwait(false);
                        if (image is not null)
                        {
                            pieces.Add(_pastes.AddImage(image, sourcePath: path).ToString());
                        }
                        else
                        {
                            if (error is not null)
                            {
                                _notices?.Notice(error);
                            }

                            pieces.Add(path);
                        }
                    }
                }

                inserted = string.Join(' ', pieces);
            }
            else if (multiline && PasteBlocks.Collapses(block))
            {
                inserted = _pastes.Add(block).ToString();
            }

            Insert(inserted);
        }

        // Typed-in-one-go text at the cursor, in place of the selection.
        void Insert(string inserted)
        {
            DeleteSelection();
            text.Insert(cursor, inserted);
            cursor += inserted.Length;
            Redraw();
        }

        // A click's index comes from the drawn (display) rows; the draft's index is behind the labels.
        int DraftIndex(int displayIndex) => Math.Clamp(_pastes.ToDraftIndex(text.ToString(), displayIndex), 0, text.Length);

        void Redraw()
        {
            string draft = text.ToString();
            if (mask)
            {
                // A secret (later on 2026-09-23): one glyph per character, so the cursor and the selection keep their columns.
                _pane.ShowInput(new string(MaskGlyph, draft.Length), cursor, anchor);
                return;
            }

            _pane.ShowInput(_pastes.Display(draft, unbreakable: true), _pastes.ToDisplayIndex(draft, cursor), anchor < 0 ? -1 : _pastes.ToDisplayIndex(draft, anchor), _pastes.LabelRanges(draft));
            RefreshList();
        }

        // The list follows the draft: the word under the cursor — a command at the start, its
        // argument after it, else an @word — is looked up when it changes, and the list goes
        // when there is none (or nothing matches, or ESC dismissed this very word).
        void RefreshList()
        {
            if (!completing && !commanding && !arguing && !hashing && !dollaring && !percenting && !careting)
            {
                return;
            }

            string draft = text.ToString();
            int start, end;
            string query;
            // The command list is narrowed here; an argument source narrows itself (it knows the
            // command) and may answer the path shape (ArgumentList.IsPathList), drawn as the @ list.
            Func<IReadOnlyList<CompletionItem>>? words = null;
            Func<ArgumentList>? argument = null;
            string prefix = "";
            if (commanding && MentionCompleter.TryFindCommand(draft, cursor, out end, out query))
            {
                start = 0;
                string typed = query;
                words = () => MentionCompleter.Matches(_commands!(), typed);
            }
            else if (arguing && MentionCompleter.TryFindArgument(draft, cursor, out string command, out start, out end, out query))
            {
                string typed = query;
                argument = () => _arguments!(command, typed);
            }
            else if (hashing && MentionCompleter.TryFind(draft, cursor, '#', out start, out end, out query))
            {
                // A #skill mention (2026-09-17): a word list like a command's, re-prefixed on a pick.
                string typed = query;
                prefix = "#";
                words = () => MentionCompleter.Matches(_skills!(), typed);
            }
            else if (dollaring && MentionCompleter.TryFind(draft, cursor, '$', out start, out end, out query))
            {
                // A $tool mention (2026-09-19): the #skill shape over the tools the next turn offers.
                string typed = query;
                prefix = "$";
                words = () => MentionCompleter.Matches(_tools!(), typed);
            }
            else if (percenting && MentionCompleter.TryFind(draft, cursor, '%', out start, out end, out query))
            {
                // A %connection mention (later on 2026-09-23): the $tool shape over the SQL connections of sql.json.
                string typed = query;
                prefix = "%";
                words = () => MentionCompleter.Matches(_connections!(), typed);
            }
            else if (careting && MentionCompleter.TryFind(draft, cursor, '^', out start, out end, out query))
            {
                // A ^workflow mention (later still on 2026-09-24): the %connection shape over the offered ComfyUI workflows.
                string typed = query;
                prefix = "^";
                words = () => MentionCompleter.Matches(_workflows!(), typed);
            }
            else if (!completing || !MentionCompleter.TryFind(draft, cursor, out start, out end, out query))
            {
                // The dismissal stands: the cursor may come back over the same word.
                CloseList();
                return;
            }

            if (dismissed is { } gone && gone.Start == start && gone.Query == query)
            {
                CloseList();
                return;
            }

            dismissed = null;
            if (list is { } open && open.Start == start && open.Query == query)
            {
                list = open with { End = end };
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

                    list = new MentionList(start, end, query, answer.Paths, answer.Truncated, 0, 0);
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

                list = new MentionList(start, end, query, items.Select(i => i.Text).ToList(), false, 0, 0) { Notes = items.Select(i => i.Note).ToList(), Prefix = prefix };
                ShowList();
                return;
            }

            var found = _mentions!(query);
            if (found.Paths.Count == 0)
            {
                CloseList();
                return;
            }

            list = new MentionList(start, end, query, found.Paths, found.Truncated, 0, 0) { Prefix = "@" };
            ShowList();
        }

        void ShowList()
        {
            int height = _pane.Profile.Height > 0 ? _pane.LayoutHeight : MenuPane.DefaultHeight;   // less the toolbar's row (2026-09-21)
            int capacity = Math.Min(MentionCompleter.MaxRows, ScreenPane.MaxOverlayRows(height, _pane.InputRows));
            if (list!.IsWordList)
            {
                // A command or skill row is cut at the edge with an ellipsis, never wrapped.
                var (fitted, at) = MentionCompleter.WordRows(list, capacity);
                list = list with { First = at };
                _pane.ShowOverlay(new Rows(fitted), MentionCompleter.Hint, input: true);
                return;
            }

            var (rows, first) = MentionCompleter.Rows(list, capacity);
            list = list with { First = first };
            _pane.ShowOverlay(new Rows(rows.Select(r => new Markup(r).Overflow(Overflow.Ellipsis))), MentionCompleter.Hint, input: true);
        }

        void CloseList()
        {
            if (list is null)
            {
                return;
            }

            list = null;
            _pane.CloseOverlay();
        }

        // The highlighted word in place of the typed one — a command or a skill name with a space
        // after it, a path as @path, a skill mention as #name; the redraw then closes the list (a word and a space, a file,
        // or a folder under folder-apply) or re-lists the folder (folder-remain). A command applied
        // as /skill lands the cursor on its argument, so the skill list opens on the same redraw.
        void ApplyMention(MentionList open)
        {
            string pick = open.Matches[open.Cursor];
            bool remain = !open.IsWordList && pick.EndsWith('/') && mentions == MentionFolderAction.Remain;
            (string replaced, int at) = MentionCompleter.Apply(text.ToString(), open.Start, open.End, open.Prefix + pick, !remain);
            text.Clear().Append(replaced);
            cursor = at;
            anchor = -1;
            dismissed = null;
            Redraw();
        }

        void EndRow()
        {
            _pane.ClearInput();
        }
    }

    /// <summary>Cells the text may use on a row of <paramref name="width"/> (after the glyph or the indent): one is kept spare so the last cell never triggers a pending wrap.</summary>
    public static int AvailableCells(int width) => Math.Max(1, width - TextCells.Width(PromptGlyph) - 1);

    /// <summary>
    /// The single-row layout (the pane-less console): the slice of <paramref name="text"/> to show
    /// in <paramref name="availableCells"/> cells so that the cursor (a UTF-16 index) is visible,
    /// and the cursor's cell within that slice. When the text fits it is returned whole. The slice
    /// never splits a wide character or a surrogate pair, and the cursor is kept at least one cell
    /// inside the right edge. The pane's own rows are <see cref="InputLayout.Wrap"/>.
    /// </summary>
    public static (string Visible, int CursorCell) Layout(string text, int cursor, int availableCells)
    {
        ArgumentNullException.ThrowIfNull(text);
        cursor = Math.Clamp(cursor, 0, text.Length);
        availableCells = Math.Max(1, availableCells);

        // A pasted line break has no row of its own here: it shows as a space.
        text = text.Replace('\n', ' ');
        if (TextCells.Width(text) <= availableCells)
        {
            return (text, TextCells.Width(text[..cursor]));
        }

        // Advance the start until the cells from it to the cursor leave room for the cursor itself.
        int start = 0;
        while (start < cursor && TextCells.Width(text[start..cursor]) > availableCells - 1)
        {
            TextCells.ElementWidth(text, start, out int len);
            start += len;
        }

        // Then take as many whole elements as fit.
        int end = start;
        int cells = 0;
        while (end < text.Length)
        {
            int w = TextCells.ElementWidth(text, end, out int len);
            if (cells + w > availableCells)
            {
                break;
            }

            cells += w;
            end += len;
        }

        return (text[start..end], TextCells.Width(text[start..cursor]));
    }
}
