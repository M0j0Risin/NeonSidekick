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
/// with no hook (a settings field) it is ESC. Ctrl+X (since 2026-09-25) cuts the selection — copied,
/// then removed; kept when the copy fails — and does nothing without one. No key
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
public sealed partial class InputLine
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
            Remembered?.Invoke(text);
        }
    }

    /// <summary>
    /// Told each line the history gained — typed, sent from under a reply or spoken — after the de-duplication, so never
    /// a repeat of the line before (2026-09-25, <c>Keep command history</c>: the screen stores it). Null = nobody listens.
    /// </summary>
    public Action<string>? Remembered { get; set; }

    /// <summary>
    /// The history replaced by <paramref name="lines"/>, oldest first, a line equal to the one before it skipped
    /// (2026-09-25: the profile's stored history at startup and after a switch, empty after <c>/cmdclear</c>).
    /// <see cref="Remembered"/> is not told. A walk starts at the bottom again at the next read.
    /// </summary>
    public void ReplaceHistory(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _history.Clear();
        foreach (string line in lines)
        {
            if (line.Length > 0 && (_history.Count == 0 || !string.Equals(_history[^1], line, StringComparison.Ordinal)))
            {
                _history.Add(line);
            }
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
    /// <paramref name="editor"/> (2026-09-25) is the editor the read feeds: the chat line passes <see cref="Chat"/>, whose
    /// draft, cursor and tokens outlive the read (the key watcher edits them under a reply) and <paramref name="initialText"/>
    /// is ignored; null (every other read) starts a fresh one over <paramref name="initialText"/>. A sent line empties it.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task<InputResult> ReadAsync(string initialText = "", bool remember = true, bool allowEmpty = false, ConsoleKey? pushToTalk = null, CancellationToken cancellationToken = default, CancellationToken wake = default, CancellationToken alert = default, bool escapeCancels = false, bool multiline = false, MentionFolderAction? mentions = null, int pastePreview = 0, Func<bool>? softEscape = null, Func<bool>? interrupt = null, Func<string, CancellationToken, Task<string?>>? intercept = null, Action? beforeCommit = null, IReadOnlyList<InputEvent>? replay = null, Func<int, bool>? emptyArrow = null, bool mask = false, Func<bool, bool>? emptyDelete = null, Func<bool>? emptyEnter = null, Editor? editor = null)
    {
        ArgumentNullException.ThrowIfNull(initialText);

        // The chat line's own editor carries its draft from read to read (2026-09-25); any other read starts one over initialText.
        var line = editor ?? new Editor(this, initialText);
        line.Begin(new ReadOptions(false, remember, allowEmpty, pushToTalk, escapeCancels, multiline, mentions, pastePreview, softEscape, interrupt, intercept, beforeCommit, emptyArrow, mask, emptyDelete, emptyEnter));

        using var linked = wake.CanBeCanceled || alert.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wake, alert) : null;
        var readToken = linked?.Token ?? cancellationToken;
        // A queued line's events, ahead of the console (2026-09-18).
        var pending = replay is { Count: > 0 } ? new Queue<InputEvent>(replay) : null;

        _pane.BeginInput();
        _hintClicks.Reset();
        try
        {
            line.Redraw();

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
                        line.EndRow();
                        return new InputResult.EndOfInput();
                    }
                }

                if (input is null)
                {
                    line.EndRow();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (wake.IsCancellationRequested)
                    {
                        return new InputResult.WakeWord(line.Text);
                    }

                    if (alert.IsCancellationRequested)
                    {
                        return new InputResult.Alert(line.Text);
                    }

                    return new InputResult.EndOfInput();
                }

                if (await line.FeedAsync(input, cancellationToken).ConfigureAwait(false) is EditOutcome.End end)
                {
                    return end.Result;
                }
            }
        }
        finally
        {
            // Every exit — Enter, ESC, the wake word, an alert, no keyboard, cancellation — takes the list with it.
            line.CloseList();
        }
    }

    /// <summary>
    /// The chat line's editor, one for the session (2026-09-25): the idle read passes it (<c>editor:</c>) and the key
    /// watcher feeds it under a reply, so the draft, its cursor and its tokens carry across a turn. See <see cref="Editor"/>.
    /// </summary>
    public Editor Chat => _chat ??= new Editor(this, "");

    private Editor? _chat;

    /// <summary>The line a live Enter hands back (2026-09-25): <paramref name="draftText"/> (trimmed, the token form) with its expansion, pictures and pane label.</summary>
    internal SubmittedLine Line(string draftText) =>
        new(draftText, _pastes.Expand(draftText).TrimEnd(), _pastes.ImagesIn(draftText), _pastes.Display(draftText).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' '));

    /// <summary>
    /// A line taken under a reply, sent at the idle line (2026-09-25; the queued events were replayed through a read
    /// before): <paramref name="beforeCommit"/>, then the transcript's <c>›</c> row with the start of each collapsed
    /// paste under it (<paramref name="pastePreview"/> lines) — the idle Enter's commit — and the result that Enter
    /// returns. The history has it already (<see cref="Editor.Accept"/>). The chat draft is untouched; the caller
    /// redraws it (<see cref="Editor.Redraw"/>), the commit having emptied the row.
    /// </summary>
    public InputResult.Submitted Send(SubmittedLine line, Action? beforeCommit = null, int pastePreview = 0)
    {
        ArgumentNullException.ThrowIfNull(line);
        beforeCommit?.Invoke();
        _pane.CommitInput(_pastes.Display(line.Draft), PreviewText(_pastes.Previews(line.Draft, pastePreview)));
        return new InputResult.Submitted(line.Text, line.Images);
    }

    /// <summary>
    /// What a paste becomes on the line (the text to insert; empty for nothing): the chat line (<paramref name="multiline"/>)
    /// keeps the block's line breaks, holds a long block behind a token and reads a dropped image file into one; a field
    /// takes one paragraph.
    /// </summary>
    private async Task<string> PasteBlockAsync(string pasted, bool multiline)
    {
        string block = multiline ? PasteText.Normalize(pasted) : PasteText.Flatten(pasted);
        if (block.Length == 0)
        {
            return "";
        }

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

            return string.Join(' ', pieces);
        }

        return multiline && PasteBlocks.Collapses(block) ? _pastes.Add(block).ToString() : block;
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
