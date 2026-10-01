using System.Collections.Concurrent;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Shell;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>How a line submitted while a reply runs is handled (<see cref="ChatScreen.MidTurnPolicy"/>).</summary>
public enum MidTurnClass
{
    /// <summary>Not a command: queued, or left for the idle line, and sent when the reply ends.</summary>
    Message,

    /// <summary>A pane that reads the keys until ESC while the reply streams under it (<c>/help</c>, <c>/settings</c>, <c>/memory</c>, a confirmation).</summary>
    Pane,

    /// <summary>Runs at once on the turn's task, its feedback a notice line in the reply (<c>/tts off</c>, <c>/timer 5m</c>).</summary>
    Quick,

    /// <summary>Cancels the turn like ESC and runs at the idle line that follows (<c>/clear</c>, <c>/new</c>, <c>/exit</c>).</summary>
    Cancel,

    /// <summary>
    /// Waits for the reply to end, then runs as if typed at the idle line (<c>/profile</c>, <c>/server</c>, <c>/compact</c> …).
    /// Dropped with a notice until later on 2026-09-27 (the user's call): now the line joins the queue as a message does
    /// (the pending lines with <c>Queue messages</c> off or under <c>/botchat</c>), so it follows <c>Queue cancel mode</c> too.
    /// </summary>
    Deferred,
}

/// <summary>
/// Commands while a reply runs (2026-09-15). The turn's key watcher offers every completed line to
/// <see cref="OnMidTurnLineAsync"/> on its own task; what happens next follows <see cref="MidTurnPolicy"/>.
///
/// <para>Two tasks, one rule each. <b>The turn task is the only transcript writer and the only
/// mutator of turn state</b>: a quick command is posted as an <em>act</em> to <see cref="_acts"/>
/// and run where the turn loop selects on "the next event or an act" (<see cref="NextEventAsync"/>),
/// so <c>/tts off</c> lands within a poll even while the model is inside a buffered tool call, and
/// a notice breaks the streamed paragraph exactly as a timer alert does. <b>The watcher task owns
/// the keys and the overlay</b>: a pane command runs its pane phase there — the pane's writes are
/// under the <see cref="ScreenPane"/>'s lock, its reads are of locked or snapshotted state — and
/// posts its act (a memory wipe, the trash) to the turn task. ESC closes the pane, the next ESC
/// cancels the turn. Anything the pane phase would say to the transcript goes through
/// <see cref="FlowSink"/>, which posts while a turn runs and writes directly otherwise. The
/// <c>ask_user</c> tool goes the other way (<see cref="AskUserAsync"/>, 2026-09-15): it runs on the
/// turn task and hands its pane to the watcher as a request, then waits for the answers.</para>
///
/// <para>A reconnect is never run mid-turn — <see cref="LlmSession.Connect"/> disposes the client
/// the turn streams from — so a switch saves at once, flags <see cref="_deferred"/> and the
/// reconnect follows the turn quietly (<see cref="EndTurnAsync"/>). <c>/settings</c> refuses the
/// rows that would need one (<see cref="SettingsMenu.RefusedMidTurn"/>). Without the pane on the
/// screen nothing here runs: the watcher gets no hook and every line is type-ahead as before.</para>
/// </summary>
internal sealed partial class ChatScreen
{
    // Acts the turn task runs at its next select point; the signal wakes the select. A poster
    // enqueues, then completes the signal; the drain swaps a fresh signal in before it empties the
    // queue, so a post between the two is either drained now or wakes the next wait.
    private readonly ConcurrentQueue<Func<Task>> _acts = new();
    private TaskCompletionSource _actSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // True from a turn's start until its pane phase (if any) closed and its acts were drained.
    private volatile bool _turnRunning;

    // The reconnects the turn's quick switches owe, applied after the turn (the turn task only).
    private SettingsChanges _deferred;

    // Closes a pane opened mid-turn when the turn's end needs the keys back at once (the interrupt's listen).
    private CancellationTokenSource? _paneClose;

    // The transcript for the menus' flow lines and the confirmations' pre-checks (see FlowSink).
    private readonly INoticeSink _flow;

    /// <summary>The notice for a command typed while a reply runs that has to wait for it: the line runs when the reply ends (later on 2026-09-27; it was dropped before). Pinned.</summary>
    public static string MidTurnDeferredNotice(string word) => $"({word} runs when the reply ends)";

    /// <summary>The notice after a switch saved mid-turn: the reconnect it needs follows the reply. Pinned.</summary>
    public static string MidTurnSwitchNotice(string what, bool on)
    {
        // The switch's own glyph, on and off alike (2026-09-22, the user's pick).
        string glyph = what switch
        {
            SpeechOutputWord => NoticeGlyphs.Tts,
            VoiceInputWord => NoticeGlyphs.Stt,
            WakeWordWord => NoticeGlyphs.Wake,
            InterruptWord => NoticeGlyphs.Interrupt,
            _ => "",
        };
        return on ? $"({glyph}{what} on — connecting when this reply ends)" : $"({glyph}{what} off — applies when this reply ends)";
    }

    /// <summary>The notice after <c>/reasoning</c> saved a level mid-turn (under the menu's own saved line). Pinned.</summary>
    public const string MidTurnAppliesNotice = "(applies when this reply ends)";

    /// <summary>The words <see cref="MidTurnSwitchNotice"/> names the four switches by. Pinned.</summary>
    public const string SpeechOutputWord = "speech output";
    public const string VoiceInputWord = "voice input";
    public const string WakeWordWord = "wake word";
    public const string InterruptWord = "interrupt";

    /// <summary>The tail a confirmation gets on a console without menus, where the answer is typed. Pinned.</summary>
    public const string TypedConfirmSuffix = " y = yes, anything else = keep";

    /// <summary>A confirmation question as the typed-answer path prints it. Pinned.</summary>
    public static string TypedConfirm(string question) => question + TypedConfirmSuffix;

    /// <summary>
    /// What a line does while a reply runs, by command (the user's lists, 2026-09-15): the info
    /// panes, <c>/settings</c>, <c>/memory</c> every way (2026-09-22: <c>forget</c>'s confirmation
    /// is a pane as the list is, and so is <c>copy &lt;profile&gt;</c>'s — it writes another
    /// profile's file, nothing the turn holds —, so the word never changes the class; the standalone
    /// <c>/forget</c> was a pane too, and <c>/memcopy</c> was refused until the word folded in),
    /// <c>/emptytrash</c>'s and <c>/cmdclear</c>'s (2026-09-25) confirmations and the <c>/reasoning</c>
    /// picker and <c>/queue</c> (2026-09-18) are <see cref="MidTurnClass.Pane"/> (<c>/expand</c> and <c>/collapse</c>, 2026-09-22 — <c>/tools expand|collapse</c> until later that day — quick like <c>/queue clear</c>), as is <c>/cmdlist</c> (2026-09-21: the <c>Shell allowed commands</c> row, which <c>/tools</c> edits under a reply too) and <c>/police</c> (2026-09-22, its <c>Shell police outside paths</c> row the same way); the four speech switches, <c>/reasoning</c>
    /// with a level, <c>/queue</c> with a word (<c>clear</c>, 2026-09-21: the drop on the turn task, or the usage error), <c>/copy</c>, <c>/remember</c>, <c>/explore</c>, <c>/log</c> (2026-09-22: an editor launch like <c>/explore</c>'s), <c>/timer</c>, <c>/comfy view</c> (2026-09-27, the string form of this policy) and an unknown
    /// command are <see cref="MidTurnClass.Quick"/>; <c>/clear</c>, <c>/new</c>, <c>/splash</c> (2026-09-19), <c>/rewind</c> (2026-09-30: it rewrites the history the turn appends to, so the reply stops and the picker opens at the idle line) and <c>/exit</c> cancel; the rest
    /// (<c>/profile</c>, <c>/theme</c> (2026-09-23, the user's call: a theme change waits for the reply to end, like its <c>Theme</c> row on the settings pane — it cancelled the reply as <c>/splash</c> does until later that day), <c>/server</c>, <c>/model</c>, <c>/compact</c>, <c>/cwd</c>, <c>/tree</c>, <c>/vault</c> (2026-09-22, as <c>/tree</c>),
    /// <c>/learn</c>, <c>/window</c>, <c>/cmdcopy</c> (2026-09-21), <c>/gituser</c> (2026-09-21), <c>/speak</c> — the turn owns the transcript and the speaker —, <c>/draft</c> (2026-09-19: it would send a message the turn cannot take), <c>/loop</c> (2026-09-21, the same reason), <c>/botchat</c> (2026-09-24, the same again), <c>/plan</c> (2026-09-26: it sends a message too, and flips the tools the running turn was prepared with), the three prompt files) are refused; <c>/skills</c> is a pane (2026-09-16 as <c>/skills</c>, <c>/skill list</c> then the bare <c>/skill</c> on 2026-09-18, the plural again since 2026-09-19; <c>/skill</c> with a name was refused until later on 2026-09-18, when the name form went — an argument was <see cref="SlashCommand.Overloaded"/>, quick like an unknown command, until <c>/skills edit &lt;name&gt;</c> came on 2026-09-21: an editor launch, refused like <c>/profile edit</c>; since it went on 2026-09-23 <c>/skills</c> took none, an argument was <see cref="SlashCommand.Overloaded"/> again, and the bare word is the pane; <c>/skills add</c> is refused since 2026-09-26 — an install writes the roots a running <c>load_skill</c> reads, and its panes would sit over the reply). Pure.
    /// <para>Later on 2026-09-27 (the user's picks): "refused" became <see cref="MidTurnClass.Deferred"/> — the line runs when the
    /// reply ends instead of being dropped (so <c>/learn</c> reflects on the reply it waited for); <c>/window</c> and the bare
    /// <c>/cwd</c> are quick notices; <c>/tree</c> and <c>/vault</c> are panes (the info pane, never the transcript the turn
    /// owns); <c>/cmdcopy</c> and the three prompt files' words are panes — they write another profile or a file the running
    /// turn's prompt was built from already, and ask their yes/no on the pane. <c>/keycopy</c> (2026-09-28) is one the same
    /// way: another profile's file, its yes/no on the pane. <c>/perf</c> (later on 2026-09-29) is quick: display only.</para>
    /// <para><c>/ha</c> (2026-09-30, the user's ask: it waited for the reply) is a <see cref="MidTurnClass.Pane"/> though it opens
    /// none: a quick act runs on the turn task, and a Home Assistant call — up to <c>Home Assistant timeout</c>, 10 s by
    /// default — would hold the streaming reply that long. On the watcher the reply streams on, its lines go through the flow
    /// sink, and the turn's end awaits the watcher's line before the idle line (<see cref="EndTurnAsync"/>), so none is lost;
    /// the keys wait only while the call runs. <c>HaSession</c> holds its own lock, as the completion's background read needs.</para>
    /// </summary>
    public static MidTurnClass MidTurnPolicy(SlashCommand command, bool hasArgs) => command switch
    {
        SlashCommand.None => MidTurnClass.Message,
        SlashCommand.Help or SlashCommand.Settings or SlashCommand.Sys or SlashCommand.Memory
            or SlashCommand.Usage or SlashCommand.About or SlashCommand.EmptyTrash or SlashCommand.CmdClear or SlashCommand.Mcp or SlashCommand.CmdList or SlashCommand.Police or SlashCommand.Tools
            or SlashCommand.Tree or SlashCommand.Vault or SlashCommand.CmdCopy or SlashCommand.KeyCopy or SlashCommand.Persona or SlashCommand.Operata or SlashCommand.Vocalia
            or SlashCommand.HomeAssistant => MidTurnClass.Pane,
        SlashCommand.Reasoning or SlashCommand.Queue or SlashCommand.Sampling => hasArgs ? MidTurnClass.Quick : MidTurnClass.Pane,
        SlashCommand.Session => hasArgs ? MidTurnClass.Deferred : MidTurnClass.Pane,
        SlashCommand.Skills => hasArgs ? MidTurnClass.Deferred : MidTurnClass.Pane,
        SlashCommand.Cwd => hasArgs ? MidTurnClass.Deferred : MidTurnClass.Quick,
        SlashCommand.Tts or SlashCommand.Voice or SlashCommand.Wake or SlashCommand.Interrupt or SlashCommand.Copy
            or SlashCommand.Remember or SlashCommand.Explore or SlashCommand.Log or SlashCommand.Timer or SlashCommand.Expand or SlashCommand.Collapse or SlashCommand.Window
            or SlashCommand.Perf or SlashCommand.Unknown or SlashCommand.Overloaded => MidTurnClass.Quick,
        SlashCommand.Clear or SlashCommand.New or SlashCommand.Splash or SlashCommand.Rewind or SlashCommand.Exit => MidTurnClass.Cancel,
        _ => MidTurnClass.Deferred,
    };

    /// <summary>
    /// <see cref="MidTurnPolicy(SlashCommand, bool)"/> with the argument read where the word decides the class
    /// (2026-09-27, the user's ask): <c>/comfy view</c> is <see cref="MidTurnClass.Quick"/> — the twin of the picture
    /// strip's button, which opens the viewer under a reply already; the window is its own thread and holds nothing
    /// the turn does. The bare <c>/comfy</c> (a spinner over the server check), <c>/comfy purge</c> (a confirmation,
    /// and it deletes what a running <c>generate_image</c> may be writing) and <c>/comfy edit</c> still wait. <c>/view</c> with a
    /// path and no <c>--chat</c> (later on 2026-09-27, when <c>/view</c> took to the viewer) is quick for the same reason;
    /// <c>/view --chat</c> draws in the transcript the turn owns, and the bare <c>/view</c> is refused as before. The bare
    /// <c>/sessions title</c> (2026-09-28, the rename box a double-click on the upper rule's session name opens) is a
    /// <see cref="MidTurnClass.Pane"/>: the store holds its own lock and the model's title never lands over a typed one;
    /// <c>/sessions title &lt;text&gt;</c> still waits. Pure.
    /// </summary>
    public static MidTurnClass MidTurnPolicy(SlashCommand command, string args) => command switch
    {
        SlashCommand.Comfy when string.Equals(args.Trim(), Viewer.ViewerText.ViewWord, StringComparison.OrdinalIgnoreCase) => MidTurnClass.Quick,
        SlashCommand.View when ParseViewArgs(args) is { Chat: false, Path.Length: > 0 } => MidTurnClass.Quick,
        SlashCommand.Session when ParseSessionArgs(args).Kind == SessionActionKind.TitlePane => MidTurnClass.Pane,
        _ => MidTurnPolicy(command, args.Length > 0),
    };

    /// <summary>The first word of a typed line, for the notices that name a command.</summary>
    private static string CommandWord(string text) => text.Trim().Split(' ', 2)[0];

    /// <summary>
    /// The watcher's line hook (<see cref="KeySource.WatchAsync(CancellationTokenSource, CancellationToken, Func{ConsoleKeyInfo, bool}?, CancellationTokenSource?, Func{KeySource.WatchedLine, Task{bool}}?, Func{bool}?, Func{InputEvent, bool}?, Func{ConsoleKeyInfo, bool}?, Func{InputEvent.Click, string?}?, InputLine.Editor?)"/>),
    /// on the watcher task. True when the line was taken (a pane ran, an act was posted, a refusal
    /// was posted, a message was queued under <c>Queue messages</c> — a line holding a paste is always one, its
    /// <c>Text</c> null — or left for the idle line, <see cref="_pendingLines"/>); false leaves it where it was —
    /// on the live row (2026-09-25: the typo intercept's replacement is there instead), or type-ahead for a clicked word.
    /// Since the row became the idle line's editor under a reply (2026-09-25) a message with the queue off and
    /// <c>/clear</c> / <c>/new</c> / <c>/exit</c> (after cancelling the turn) wait in <see cref="_pendingLines"/>, which the
    /// idle loop sends first; they were type-ahead before.
    /// </summary>
    private async Task<bool> OnMidTurnLineAsync(KeySource.WatchedLine line, CancellationTokenSource turnCts, CancellationToken paneToken)
    {
        if (line.Text is not { } text)
        {
            // A paste in the line: never a command (the README's rule); queued like a message
            // under the switch, left for the idle line without it.
            return QueueLine(line);
        }

        var (command, args) = ParseLine(text);
        var policy = MidTurnPolicy(command, args);
        if (policy != MidTurnClass.Message)
        {
            DiagnosticLog.Debug(AppCategory, MidTurnCommandLogLine(CommandWord(text), policy));
        }

        switch (policy)
        {
            case MidTurnClass.Message:
                // The typo intercept at the Enter (2026-09-25; the idle read's alone before, a queued line meeting it when
                // replayed): a replacement is the row's draft again, the line never sent.
                if (line.Line is not null && await TypoInterceptAsync(text, paneToken).ConfigureAwait(false) is { } replacement)
                {
                    _input.Chat.Load(replacement);
                    return false;
                }

                return QueueLine(line);
            case MidTurnClass.Cancel:
                // The queue goes with the conversation whatever Queue cancel mode says (2026-09-18):
                // the line waits for the idle loop, which sends it before the queue, so under drain
                // a queued message would otherwise reach the conversation about to be forgotten.
                if (_queue.Clear() is > 0 and var dropped)
                {
                    Post(() => _transcript.Notice(QueueDroppedNotice(dropped)));
                }

                bool pended = line.Line is { } cancelLine && Pend(cancelLine);
                VoiceSession.SafeCancel(turnCts);
                return pended;
            case MidTurnClass.Deferred:
                // Runs when the reply ends (later on 2026-09-27; dropped before): queued as a message is, so it keeps its place
                // behind lines typed before it; pending under /botchat, whose queue is read as the user's interjections.
                // A line with no live row behind it is left where it was, as QueueLine leaves a message (2026-09-28, code
                // review: the notice promised a run and nothing held the line). A clicked word has no live row: the toolbar's
                // 🪪 /profile (later on 2026-09-29) under a reply is nothing, as a click gets no refusal notice; every watch in
                // the app hands the editor in.
                if (line.Line is not { } deferred)
                {
                    return false;
                }

                Post(() => _transcript.Notice(MidTurnDeferredNotice(CommandWord(text))));
                if (_botChatRunning || !QueueLine(line))
                {
                    Pend(deferred);
                }

                return true;
            case MidTurnClass.Quick:
                Post(() => HandleQuickAsync(command, args, text, paneToken));
                return true;
            default:
                await RunPaneAsync(command, args, paneToken).ConfigureAwait(false);
                return true;
        }
    }

    /// <summary>
    /// A message typed under the reply (2026-09-18), on the watcher task: under <c>Queue messages</c>
    /// it goes into the queue and the idle loop sends it once the reply ends; with the switch off it waits in
    /// <see cref="_pendingLines"/> (2026-09-25: it was type-ahead until the live row) and is sent as the reply ends
    /// all the same, only never shown as queued. The setting is read here, at each Enter. Under <c>/botchat</c>
    /// (2026-09-24) a line is always queued whatever the switch says: the chat takes it between two
    /// replies as the user's interjection. A blank line, or one with no live row behind it, stays where it was.
    /// </summary>
    private bool QueueLine(KeySource.WatchedLine line)
    {
        string label = line.Label.Trim();
        if (line.Line is not { } submitted || label.Length == 0)
        {
            return false;
        }

        if (!_effective().QueueMessages && !_botChatRunning)
        {
            return Pend(submitted);
        }

        _queue.Enqueue(new QueuedMessage(label, submitted));
        if (_botChatRunning)
        {
            // /botchat's speech wait takes the line at once (later on 2026-09-24): its select wakes on the act signal.
            Volatile.Read(ref _actSignal).TrySetResult();
        }

        return true;
    }

    /// <summary>
    /// The lines left for the idle line (2026-09-25): a message with <c>Queue messages</c> off, a command that cancels the
    /// reply (<c>/clear</c>, <c>/new</c>, <c>/exit</c>…), or a line sent under a spinner that takes no command. Filled on the
    /// watcher task, emptied by the idle loop ahead of every other line, oldest first.
    /// </summary>
    private readonly ConcurrentQueue<SubmittedLine> _pendingLines = new();

    /// <summary>Leaves <paramref name="line"/> for the idle line; always taken.</summary>
    private bool Pend(SubmittedLine line)
    {
        _pendingLines.Enqueue(line);
        return true;
    }

    /// <summary>The line hook of a watch that takes no command (a spinner's, a listen's): every line waits for the idle line (2026-09-25).</summary>
    private Task<bool> PendLineAsync(KeySource.WatchedLine line) => Task.FromResult(line.Line is { } submitted && Pend(submitted));

    /// <summary>
    /// The pane phase of a mid-turn pane command, on the watcher task; its acts are posted. Then
    /// what a double-click off the pane named (later on 2026-09-21, as <c>HandleAsync</c> at idle):
    /// the pane's own word ends there; another <see cref="MidTurnClass.Pane"/> command's opens that
    /// pane; a word refused under the reply (<c>/model</c>, <c>/cwd browse</c>) is the close alone —
    /// a click deserves no refusal notice.
    /// </summary>
    private async Task RunPaneAsync(SlashCommand command, string args, CancellationToken cancellationToken)
    {
        while (true)
        {
            await RunPaneOnceAsync(command, args, cancellationToken).ConfigureAwait(false);
            if (_pane.TakeDismissHit() is not { } hit || OffPaneLine(hit) is not { } next)
            {
                return;
            }

            var (nextCommand, nextArgs) = ParseLine(next);
            if (nextCommand == command || MidTurnPolicy(nextCommand, nextArgs) != MidTurnClass.Pane)
            {
                return;
            }

            command = nextCommand;
            args = nextArgs;
        }
    }

    private async Task RunPaneOnceAsync(SlashCommand command, string args, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case SlashCommand.Help:
                await _info.ShowAsync(InfoPane.Title, HelpTabs(), 0, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Sys:
                await _info.ShowAsync(SystemPromptSummary.Label, SysPromptTabs(), 0, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Usage:
                await _info.ShowAsync(UsageText.Label, UsageTabs(), 0, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.About:
                await _info.ShowAsync(AboutText.Label, AboutTabs(), 0, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Skills:
                // A bare /skills: the list shows; a scope pick is refused under the reply (a move could race load_skill).
                await _skillsMenu.ShowAsync(cancellationToken, midTurn: true).ConfigureAwait(false);
                break;
            case SlashCommand.Tools:
                // /tools (2026-09-19): a flip saves and is read at the next turn; the settings rows edit as on /settings mid-turn (the
                // Claude API's reconnect rows, 2026-09-29, are refused there, so nothing comes back to apply).
                await _toolsMenu.ShowAsync(cancellationToken, midTurn: true).ConfigureAwait(false);
                break;
            case SlashCommand.Sampling:
                // /sampling (2026-09-28): an edit saves and is read at the next turn; nothing reconnects.
                await _samplingMenu.ShowAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Police:
                // /police (2026-09-22): the Shell police outside paths row alone, which /tools edits under a reply already; a flip is read at the next call.
                await _toolsMenu.ShowPoliceAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.CmdList:
                // /cmdlist (2026-09-21): the allowed-commands row alone, which /tools edits under a reply already; a removal is read at the next approval.
                await _toolsMenu.ShowAllowedCommandsAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Mcp:
                // /mcp (2026-09-20): a tool flip saves for the next turn and the edit rows open the files; a server flip, a retry, a reload and the master switch are refused under the reply.
                await _mcpMenu.ShowAsync(cancellationToken, midTurn: true).ConfigureAwait(false);
                break;
            case SlashCommand.Settings:
                // The rows that would reconnect, switch the profile, move the sandbox or reshape
                // the history are refused on the pane, so the flags come back empty.
                await _menu.ShowAsync(cancellationToken, midTurn: true).ConfigureAwait(false);
                break;
            case SlashCommand.Memory:
                // /memory (2026-09-22): the list pane, or forget's or copy's confirmation — the panes
                // /forget opened and /memcopy was refused for, so all three forms are panes under a reply;
                // edit (2026-09-23) opens the editor and a notice, safe under a reply too.
                await HandleMemoryAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Queue:
                await _queueMenu.ShowAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Session when ParseSessionArgs(args).Kind == SessionActionKind.TitlePane:
                // The rename box (2026-09-28): the upper rule's session name double-clicked, or the bare /sessions title typed.
                await RenameSessionAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Session:
                // The list alone: every pick is refused there, so nothing comes back to restore.
                await _sessionsMenu.ShowAsync(cancellationToken, midTurn: true).ConfigureAwait(false);
                break;
            case SlashCommand.EmptyTrash:
                await EmptyTrashAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.CmdClear:
                // /cmdclear (2026-09-25): the confirmation is a pane; the wipe is posted to the turn task.
                await CmdClearAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Tree:
                // /tree and /vault under a reply (later on 2026-09-27): the walk on the info pane, not in the reply.
                await ShowTreePaneAsync(TreeLines(args, out string? treeError), treeError, TreeText.PaneLabel("/tree", args), cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Vault:
                await ShowTreePaneAsync(VaultLines(args, out string? vaultError), vaultError, TreeText.PaneLabel("/vault", args), cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.CmdCopy:
                // /cmdcopy and the prompt files (later on 2026-09-27): another profile's file, or one the running turn's prompt
                // was read from already; the yes/no is a pane and every line goes through the flow sink.
                await HandleCmdCopyAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.KeyCopy:
                // /keycopy (2026-09-28): /cmdcopy's reason — another profile's file, the yes/no on the pane.
                await HandleKeyCopyAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Persona:
                await HandlePromptFileAsync(_persona, "/persona", args, PersonaCreatedNotice, PersonaOpenedNotice, PersonaOpenFailedError, spoken: false, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Operata:
                await HandlePromptFileAsync(_operata, "/operata", args, OperataCreatedNotice, OperataOpenedNotice, OperataOpenFailedError, spoken: false, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Vocalia:
                await HandlePromptFileAsync(_vocalia, "/vocalia", args, VocaliaCreatedNotice, VocaliaOpenedNotice, VocaliaOpenFailedError, spoken: true, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Reasoning:
                if (await _menu.PickReasoningAsync("", _effective().LlmReasoning, cancellationToken).ConfigureAwait(false))
                {
                    Post(() => Defer(SettingsChanges.Llm, MidTurnAppliesNotice));
                }

                break;
            case SlashCommand.HomeAssistant:
                // /ha (2026-09-30): the call on the watcher, the reply streaming on; its lines through the flow sink.
                await HandleHomeAssistantMidTurnAsync(args, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>A quick command's act, on the turn task: the handler the idle line runs, with a reconnect deferred where one would follow.</summary>
    private async Task HandleQuickAsync(SlashCommand command, string args, string text, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case SlashCommand.Tts or SlashCommand.Voice or SlashCommand.Wake or SlashCommand.Interrupt:
                await HandleSwitchAsync(command, args, midTurn: true, cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Reasoning:
                if (await _menu.PickReasoningAsync(args, _effective().LlmReasoning, cancellationToken).ConfigureAwait(false))
                {
                    Defer(SettingsChanges.Llm, MidTurnAppliesNotice);
                }

                break;
            case SlashCommand.Queue:
                HandleQueueArgs(args);
                break;
            case SlashCommand.Perf:
                HandlePerf(args);
                break;
            case SlashCommand.Sampling:
                // /sampling <field> <value> (2026-09-28): the connected model's entry, read at the next turn.
                _samplingMenu.Quick(args);
                break;
            case SlashCommand.Expand or SlashCommand.Collapse:
                // The tool runs and code blocks above the reply fold or unfold as it streams (2026-09-22).
                SetFolds(command == SlashCommand.Expand);
                break;
            case SlashCommand.Copy:
                HandleCopy(args);
                break;
            case SlashCommand.Remember:
                Remember(args);
                break;
            case SlashCommand.Explore:
                HandleExplore(args);
                break;
            case SlashCommand.Log:
                HandleLog();
                break;
            case SlashCommand.Timer:
                HandleTimer(args);
                break;
            case SlashCommand.Window:
                // The window's size in the reply (later on 2026-09-27): read-only.
                _transcript.Notice(WindowNotice(_pane.Profile.Width, _pane.Profile.Height));
                break;
            case SlashCommand.Cwd:
                // The bare /cwd alone reaches here (later on 2026-09-27): the path in force, read-only.
                await HandleCwdAsync("", cancellationToken).ConfigureAwait(false);
                break;
            case SlashCommand.Comfy:
                // /comfy view alone reaches here (2026-09-27, MidTurnPolicy's string form); the notice lands in the reply.
                OpenViewer(notice: true);
                break;
            case SlashCommand.View:
                // /view <path> alone reaches here (later on 2026-09-27): the window, never the transcript the turn owns.
                OpenInViewer(ParseViewArgs(args).Path);
                break;
            case SlashCommand.Unknown:
                _transcript.Error(UnknownCommandError(CommandWord(text)));
                break;
            case SlashCommand.Overloaded:
                _transcript.Error(NoArgumentError(CommandWord(text)));
                break;
        }
    }

    /// <summary><c>/tree</c> or <c>/vault</c> under a reply (later on 2026-09-27): the walk's lines on the info pane, or its error line through the flow sink.</summary>
    private async Task ShowTreePaneAsync(IReadOnlyList<string>? lines, string? error, string label, CancellationToken cancellationToken)
    {
        if (lines is null)
        {
            _flow.Error(error ?? "");
            return;
        }

        string text = string.Join('\n', lines);
        await _info.ShowAsync(label, [new InfoTab(label, () => new Spectre.Console.Text(text))], 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A switch saved mid-turn: the reconnect it needs is owed to the turn's end, and the line says so.</summary>
    private void Defer(SettingsChanges change, string notice)
    {
        _deferred |= change;
        _transcript.Notice(notice);
    }

    /// <summary>Queues an act for the turn task and wakes its select.</summary>
    private void Post(Func<Task> act)
    {
        _acts.Enqueue(act);
        Volatile.Read(ref _actSignal).TrySetResult();
    }

    private void Post(Action act) => Post(() =>
    {
        act();
        return Task.CompletedTask;
    });

    /// <summary>Runs <paramref name="act"/> here at the idle line, or posts it to the turn task while a turn runs (the pane phase of a confirmation).</summary>
    private void RunOrPost(Action act)
    {
        if (_turnRunning)
        {
            Post(act);
        }
        else
        {
            act();
        }
    }

    /// <summary>
    /// The turn loop's wait: <paramref name="pending"/> (the enumerator's next event) or an act,
    /// whichever comes first; the acts are run here and the wait resumes until the event is in.
    /// The same pending task is awaited through every wake, so no event is lost.
    /// </summary>
    private async Task<bool> NextEventAsync(Task<bool> pending)
    {
        while (true)
        {
            var signal = Volatile.Read(ref _actSignal).Task;
            await Task.WhenAny(pending, signal).ConfigureAwait(false);
            await DrainActsAsync().ConfigureAwait(false);
            if (pending.IsCompleted)
            {
                return await pending.ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs every queued act, a fresh signal armed first. A failing act is one error line, never the turn's end.</summary>
    private async Task DrainActsAsync()
    {
        Interlocked.Exchange(ref _actSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        while (_acts.TryDequeue(out var act))
        {
            try
            {
                await act().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DiagnosticLog.Error(ScreenPane.Category, "A mid-turn command failed: " + Llm.Assistant.Explain(ex), ex);
            }
        }
    }

    /// <summary>
    /// After a turn: a pane still open on the watcher task is closed when the keys are needed at
    /// once (<paramref name="closePane"/>: the interrupt's listen, the exit) and awaited otherwise
    /// (the reply ended under <c>/help</c>; the user closes it), then the acts posted meanwhile run
    /// and the reconnects the quick switches owe follow, quietly.
    /// </summary>
    private async Task EndTurnAsync(bool closePane, CancellationToken cancellationToken)
    {
        if (closePane && _paneClose is { } close)
        {
            VoiceSession.SafeCancel(close);
        }

        await _keys.PendingLine.ConfigureAwait(false);
        _turnRunning = false;
        await DrainActsAsync().ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var owed = _deferred;
        _deferred = SettingsChanges.None;
        if (owed.HasFlag(SettingsChanges.Llm))
        {
            await ConnectLlmAsync(cancellationToken, quiet: true).ConfigureAwait(false);
        }

        if (owed.HasFlag(SettingsChanges.Tts))
        {
            await ConnectSpeechAsync(cancellationToken, quiet: true).ConfigureAwait(false);
        }

        if (owed.HasFlag(SettingsChanges.Voice))
        {
            await ConnectVoiceAsync(cancellationToken, quiet: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The <c>ask_user</c> tool's wait (2026-09-15), on the turn task: the questions go to the
    /// watcher as a pane request (<see cref="KeySource.RequestPaneAsync"/>), which runs
    /// <see cref="QuestionMenu.AskAsync"/> on its own task with the keys — the third thing that
    /// runs there, after a line's pane and its acts — under a token linked to the turn's and the
    /// pane-close signal, so the wake phrase, the app token or the turn's end close the pane; ESC
    /// (or Ctrl+C, since 2026-09-17) inside it closes the pane alone (null: not answered) and the
    /// reply runs on. The wait itself is
    /// under the turn token: cancelled, the tool's cancellation ends the turn as ESC does. Null
    /// too when no watcher could run the pane (never mid-turn on the screen; a guard). A pane that
    /// fails is one error line and null, never the turn's end.
    /// </summary>
    private async Task<IReadOnlyList<AskAnswer>?> AskUserAsync(IReadOnlyList<AskQuestion> questions, CancellationToken turnToken)
    {
        var paneToken = _paneClose?.Token ?? CancellationToken.None;
        IReadOnlyList<AskAnswer>? answers = null;
        var request = _keys.RequestPaneAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(paneToken, turnToken);
            try
            {
                answers = await _questionMenu.AskAsync(questions, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !linked.IsCancellationRequested)
            {
                DiagnosticLog.Error(ScreenPane.Category, "The question pane failed: " + Llm.Assistant.Explain(ex), ex);
            }
        });
        try
        {
            await request.WaitAsync(turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
        {
            // No watcher to run the pane: never asked.
            DiagnosticLog.Info(AppCategory, AskUserNotAskedLogLine);
            return null;
        }

        DiagnosticLog.Info(AppCategory, answers is null ? AskUserNotAnsweredLogLine : AskUserAnsweredLogLine(questions.Count));
        return answers;
    }

    /// <summary><c>ask_user: 2 questions answered</c>. Pinned.</summary>
    public static string AskUserAnsweredLogLine(int questions) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ask_user: {questions} question{(questions == 1 ? "" : "s")} answered");

    public const string AskUserNotAnsweredLogLine = "ask_user: not answered (ESC).";
    public const string AskUserNotAskedLogLine = "ask_user: never asked (no watcher to run the pane).";

    /// <summary>
    /// The gate's asker (2026-09-21), on the turn task: the <see cref="AskUserAsync"/> shape over the
    /// approval pane (<see cref="CommandApprovalMenu"/>) — the request goes to the watcher as a pane
    /// request, the wait is under the turn token, ESC inside the pane is a deny and the reply runs
    /// on with the tool's refusal. Null (never asked) when no watcher could run the pane: the gate
    /// answers with the no-screen sentence. A Session or Permanent pick is noted on the transcript
    /// here, on its way back to the gate that records it — the pane is gone by then, and the line
    /// reads above the tool's own.
    /// </summary>
    private async Task<CommandChoice?> ApproveCommandAsync(CommandRequest request, CancellationToken turnToken)
    {
        var paneToken = _paneClose?.Token ?? CancellationToken.None;
        CommandChoice? choice = null;
        var pending = _keys.RequestPaneAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(paneToken, turnToken);
            try
            {
                choice = await _approvalMenu.AskAsync(request, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !linked.IsCancellationRequested)
            {
                DiagnosticLog.Error(ScreenPane.Category, "The approval pane failed: " + Llm.Assistant.Explain(ex), ex);
            }
        });
        try
        {
            await pending.WaitAsync(turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
        {
            // No watcher to run the pane: never asked.
            DiagnosticLog.Info(AppCategory, ApprovalNotAskedLogLine);
            return null;
        }

        switch (choice)
        {
            case CommandChoice.Session:
                _transcript.Notice(ShellText.SessionAllowedNotice(request.Prefixes));
                break;
            case CommandChoice.Permanent:
                _transcript.Notice(ShellText.PermanentAllowedNotice(request.Prefixes));
                break;
        }

        return choice;
    }

    public const string ApprovalNotAskedLogLine = "run_command: never asked (no watcher to run the pane).";

    /// <summary>A yes/no question: the pane (<see cref="SettingsMenu.ConfirmAsync"/>) where menus open, else the question with <see cref="TypedConfirmSuffix"/> and a typed <c>y</c> on the input line.</summary>
    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        if (_menu.CanShowMenus())
        {
            return await _menu.ConfirmAsync(question, cancellationToken).ConfigureAwait(false);
        }

        _transcript.Notice(TypedConfirm(question));
        var answer = await _input.ReadAsync(remember: false, allowEmpty: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        return answer is InputResult.Submitted submitted && IsYes(submitted.Text);
    }

    /// <summary>
    /// The transcript for code that may run on the watcher task (a menu's flow lines, a
    /// confirmation's pre-check): written directly at the idle line, posted to the turn task while
    /// a turn runs, so the streamed reply is never written into from two tasks.
    /// </summary>
    private sealed class FlowSink(ChatScreen screen) : INoticeSink
    {
        public void Notice(string text) => screen.RunOrPost(() => screen._transcript.Notice(text));

        public void Warning(string text) => screen.RunOrPost(() => screen._transcript.Warning(text));

        public void Error(string text) => screen.RunOrPost(() => screen._transcript.Error(text));
    }
}
