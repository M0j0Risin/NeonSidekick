using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Git;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Mcp;
using NeonSidekick.Memory;
using NeonSidekick.Obsidian;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Sql;
using NeonSidekick.Comfy;
using NeonSidekick.Skills;
using NeonSidekick.Speech;
using NeonSidekick.Timers;
using NeonSidekick.UI;
using NeonSidekick.Web;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

/// <summary>
/// How a turn ended, for the loop that ran it. Top-level (not nested in the internal screen) so
/// the test project can pin <see cref="ChatScreen.TurnEndNotice"/> in a public theory.
/// </summary>
public enum TurnOutcome
{
    Continue,
    Exit,

    /// <summary>The wake phrase cut the spoken reply short; listen for the request next.</summary>
    Interrupted,

    /// <summary>ESC before the model's first event: the message is withdrawn and goes back to the line.</summary>
    Withdrawn,
}

/// <summary>What a <c>/profile</c> argument asks for. Top-level, like <see cref="TurnOutcome"/>, so the test project can pin the grammar in a public theory.</summary>
public enum ProfileActionKind
{
    /// <summary>No argument: the picker.</summary>
    Pick,
    Switch,
    Add,
    Delete,

    /// <summary><c>reset</c> alone (<c>Name</c> empty: the loaded profile) or <c>reset &lt;name&gt;</c>.</summary>
    Reset,

    /// <summary><c>rename &lt;name&gt; &lt;new-name&gt;</c>: <c>Name</c> the profile, <c>NewName</c> what it becomes.</summary>
    Rename,

    /// <summary><c>edit</c> (2026-09-21): the loaded profile's <c>profile.json</c> in the editor, the pending save written first.</summary>
    Edit,

    /// <summary><c>reload</c> (2026-09-21): the loaded profile's <c>profile.json</c> read back from disk, the sessions whose settings changed reconnected.</summary>
    Reload,

    /// <summary>Anything the grammar does not cover; <c>ChatScreen.ProfileUsageError</c>.</summary>
    Invalid,
}

/// <summary>The parsed <c>/profile</c> argument; <paramref name="NewName"/> is set by <see cref="ProfileActionKind.Rename"/> alone.</summary>
public readonly record struct ProfileAction(ProfileActionKind Kind, string Name, string NewName = "");

/// <summary>What a <c>/git</c> argument asks for (2026-09-21). Top-level like <see cref="ProfileActionKind"/>, so the test project can pin the grammar.</summary>
public enum GitActionKind
{
    /// <summary><c>user</c> or <c>user force</c>: the identity settings into the repository's config.</summary>
    User,

    /// <summary>Anything else, a bare <c>/git</c> included; <c>ChatScreen.GitUsageError</c>.</summary>
    Invalid,
}

/// <summary>The parsed <c>/git</c> argument; <paramref name="Force"/> is the <c>force</c> word after <c>user</c>.</summary>
public readonly record struct GitAction(GitActionKind Kind, bool Force = false);

/// <summary>What a <c>/sessions</c> argument asks for (2026-09-18). Top-level like <see cref="ProfileAction"/>, so the test project can pin the grammar.</summary>
public enum SessionActionKind
{
    /// <summary>No argument: the pane.</summary>
    Pane,

    /// <summary><c>&lt;id&gt;</c> (<c>12</c> or <c>#12</c>): restore that session.</summary>
    Restore,

    /// <summary><c>purge &lt;id&gt;</c>.</summary>
    Purge,

    /// <summary><c>purge older &lt;age&gt;</c>: <c>Age</c> how long since the last turn, zero and up (<see cref="Sessions.SessionText.TryParseAge"/>).</summary>
    PurgeOlder,

    /// <summary><c>purge all</c>.</summary>
    PurgeAll,

    /// <summary><c>title &lt;text&gt;</c>: <c>Text</c> the new title of the session on screen.</summary>
    Title,

    /// <summary>Anything the grammar does not cover; <c>ChatScreen.SessionUsageError</c>.</summary>
    Invalid,
}

/// <summary>The parsed <c>/sessions</c> argument; <paramref name="Id"/> for a restore or a purge, <paramref name="Age"/> for <see cref="SessionActionKind.PurgeOlder"/>, <paramref name="Text"/> for <see cref="SessionActionKind.Title"/>.</summary>
public readonly record struct SessionAction(SessionActionKind Kind, long Id = 0, TimeSpan Age = default, string Text = "");

/// <summary>What a <c>/timer</c> argument asks for. Top-level like <see cref="ProfileAction"/>, so the test project can pin the grammar.</summary>
public enum TimerActionKind
{
    /// <summary>No argument: the timers as they stand.</summary>
    List,
    Start,
    Stop,
    StopAll,

    /// <summary>Anything the grammar does not cover; <c>ChatScreen.TimerUsageError</c>.</summary>
    Invalid,
}

/// <summary><see cref="Duration"/> is set for <see cref="TimerActionKind.Start"/>; <see cref="Name"/> for Start (may be empty: the default name) and Stop.</summary>
public readonly record struct TimerAction(TimerActionKind Kind, TimeSpan Duration, string Name);

/// <summary>What a <c>/cwd</c> argument asks for. Top-level like <see cref="TimerAction"/>, so the test project can pin the grammar.</summary>
public enum CwdActionKind
{
    /// <summary>No argument: the working directory in force.</summary>
    Show,

    /// <summary><c>default</c> or <c>~</c>: back to the profile's own folder.</summary>
    Reset,

    /// <summary>A path.</summary>
    Set,

    /// <summary><c>browse</c> (2026-09-21): the folder picker on the pane.</summary>
    Browse,
}

/// <summary><see cref="Path"/> is set for <see cref="CwdActionKind.Set"/>.</summary>
public readonly record struct CwdAction(CwdActionKind Kind, string Path);

/// <summary>What a <c>/copy</c> argument asks for. Top-level like <see cref="CwdAction"/>, so the test project can pin the grammar.</summary>
public enum CopyActionKind
{
    /// <summary>The last <see cref="CopyAction.Count"/> exchanges (at least one; more than there are means all).</summary>
    Count,

    /// <summary><c>all</c>: every exchange of the session.</summary>
    All,

    /// <summary>Anything else; <see cref="ChatScreen.CopyUsageError"/>.</summary>
    Invalid,
}

/// <summary><see cref="Count"/> is set for <see cref="CopyActionKind.Count"/>, and is at least 1.</summary>
public readonly record struct CopyAction(CopyActionKind Kind, int Count);

/// <summary>What a <c>/queue</c> argument asks for (2026-09-21). Top-level like <see cref="CopyAction"/>, so the test project can pin the grammar.</summary>
public enum QueueAction
{
    /// <summary>Nothing: the pane.</summary>
    List,

    /// <summary><c>clear</c>: every queued message dropped.</summary>
    Clear,

    /// <summary>Anything else; <see cref="ChatScreen.QueueUsageError"/>.</summary>
    Invalid,
}

/// <summary>
/// What a <c>/memory</c> line asks for (2026-09-22, the user's ask, twice: <c>/forget</c> folded
/// into <c>/memory</c> as a word that morning, <c>/memcopy</c> as <c>copy &lt;profile&gt;
/// [overwrite]</c> later that day, and both standalone commands went). The <see cref="QueueAction"/>
/// shape until the copy brought a target with it; <see cref="ProfileActionKind"/>'s since.
/// </summary>
public enum MemoryActionKind
{
    /// <summary>Nothing: the pane, one row per memory.</summary>
    List,

    /// <summary><c>forget</c>: every memory erased, after the confirmation <c>/forget</c> asked.</summary>
    Forget,

    /// <summary><c>copy &lt;profile&gt; [overwrite]</c>: every memory into another profile's, after the confirmation <c>/memcopy</c> asked.</summary>
    Copy,

    /// <summary><c>edit</c> (2026-09-23): <c>memory.json</c> in the editor, created first when it is not there; the store reads the edit back on its next use.</summary>
    Edit,

    /// <summary>Anything else; <see cref="ChatScreen.MemoryUsageError"/>.</summary>
    Invalid,
}

/// <summary>The parsed <c>/memory</c> argument; <paramref name="Profile"/> and <paramref name="Overwrite"/> are set by <see cref="MemoryActionKind.Copy"/> alone.</summary>
public readonly record struct MemoryAction(MemoryActionKind Kind, string Profile = "", bool Overwrite = false);

/// <summary>
/// The interactive chat: connect, read a line, dispatch a command or run a turn, repeat. Owns the
/// transcript, the input line and the menus; <see cref="SidekickApp"/> owns the banner and the
/// mode switch.
///
/// <para>Two rules keep the console coherent. <b>The loop is the only writer</b>: diagnostics from
/// other threads are queued and drained here at safe points, never written under a live spinner.
/// <b>The spinner runs before the reply glyph</b>: Spectre's <c>Status</c> erases the line it
/// started on when it ends, so the glyph is written only after the first event has arrived.</para>
///
/// <para>ESC during a reply cancels the turn's token (the partial reply is kept as context);
/// the app token cancels it and exits. Keys typed during a reply wait for the next input line. With
/// speech on, the turn ends with the reply's text: the audio still owed — the tail — plays on
/// under the input line (<see cref="SpeechSession.Playing"/>), and leaving the line stops it: a
/// sent line silently, ESC with <c>(speech stopped)</c> — draft or not, the draft kept, the next
/// ESC clears it —, the push-to-talk key before the microphone opens, the wake phrase as an
/// interruption. Typing does not.</para>
///
/// <para>Push-to-talk: the configured key on an <em>empty</em> input line listens under a spinner
/// until the VAD hears the end of the utterance (the key again, or Enter, ends it early; ESC
/// discards), then the transcript is shown as a <c>›</c> line, remembered in the history and sent
/// as a message. <b>Never as a slash command</b>: a mis-heard "/clear" must not clear.</para>
///
/// <para>The wake word: while the input line waits with nothing playing (never during a turn,
/// never over a tail, never while a menu is open), <see cref="VoiceSession.ArmWake"/> keeps the
/// microphone open and the phrase ends the read with <see cref="InputResult.WakeWord"/>. The
/// disarm sits in a <c>finally</c> around the read, so a menu or a listen always starts with the
/// microphone closed. A wake heard with text on the line is ignored, like F4 with text on the
/// line, and the draft comes back. Otherwise the listener's pre-roll seeds the same listen as
/// push-to-talk; when the request was spoken with the phrase the seed is transcribed at once.</para>
///
/// <para>The interrupt (M6): the second and last arm site is a <em>spoken</em> turn with
/// <c>/interrupt</c> on. <see cref="RunTurnAsync"/> arms the same listener with the turn's token
/// and disarms it in the turn's <c>finally</c>; the phrase cancels the turn the way ESC does, the
/// hit's audio is discarded, and <see cref="RunMessageAsync"/> listens for the request next. Over
/// the tail the idle read arms it again (<see cref="ReadLineAsync"/>, keyword mode, the turn's
/// probe), and <see cref="HandleTailInterruptAsync"/> does the same from the input line.</para>
///
/// <para>Timers: the <see cref="TimerBoard"/> is the screen's (not the profile's, not the
/// conversation's), so a timer outlives <c>/clear</c> and a profile switch. Its expiry, on the
/// clock's thread, only queues an alert and cancels the idle read's alert token (the wake-word
/// shape: <see cref="InputResult.Alert"/> brings the draft back; the tail's end uses the same
/// nudge); the loop prints the alerts at its top and speaks them as a tail — no microphone over
/// a tail without the interrupt — and prints (never speaks) those that land mid-reply. Any input
/// at the line silences a ringing timer; until then it repeats.</para>
/// </summary>
internal sealed partial class ChatScreen
{
    public const string CancelledNotice = "(cancelled)";
    public const string WithdrawnNotice = "(cancelled — your message is back on the line)";
    public const string SpeechStoppedNotice = "(" + NoticeGlyphs.Tts + "speech stopped)";   // the speaker since 2026-09-22
    /// <summary>The hint row after one Ctrl+C with nothing to copy, stop or cancel; the next within <see cref="ExitConfirmWindow"/> exits (2026-09-17).</summary>
    public const string ExitHint = "Press Ctrl+C again to exit";
    /// <summary>
    /// The hint row while the welcome splash stands, the draft is empty and Left / Right would
    /// walk the pictures (2026-09-20, the user's ask and word): the arrows, then the timers /
    /// usage / reading line after <see cref="HintJoin"/> when there is one (<see cref="SplashHintLine"/>).
    /// Ranked after <see cref="ExitHint"/>; an open list's or the scroll's hint hides it like both.
    /// </summary>
    public const string SplashHint = "← → slideshow";

    /// <summary>
    /// The hint row's lead while the tiled splash stands with two or more pages (2026-09-24,
    /// <c>Welcome splash</c> <c>tiled</c>): the arrows and where they are — <c>← → page 2 of 5</c>,
    /// one-based. Stands where <see cref="SplashHint"/> does, under the same rules. Pinned.
    /// </summary>
    public static string SplashPageHint(int page, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"← → page {page + 1} of {count}");

    /// <summary>
    /// The hint row after one Delete over a profile's splash picture; the next Delete in a row within
    /// <see cref="SplashDeleteWindow"/> moves the file into the folder's <c>.trash</c> (2026-09-24). The
    /// only hint the feature has: the idle row never advertises it (the user's call). Pinned.
    /// </summary>
    public static string SplashDeleteArmedHint(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "DEL again to trash " + name;
    }
    /// <summary>
    /// A connect or model download cancelled by Ctrl+C under its spinner (2026-09-17); the app stays.
    /// Since 2026-09-22 (the user's pick) it wears the glyph of what was connecting —
    /// <c>(🖥️ cancelled)</c> the LLM, <c>(🔊 cancelled)</c> speech, <c>(🎤 cancelled)</c> voice,
    /// <c>(🔌 cancelled)</c> MCP. Pinned.
    /// </summary>
    public static string ConnectCancelledNotice(string glyph) => "(" + glyph + "cancelled)";
    public const string HeardNothingNotice = "(" + NoticeGlyphs.Stt + "heard nothing)";   // the microphone since 2026-09-22
    public const string VoiceDiscardedNotice = "(" + NoticeGlyphs.Stt + "discarded)";
    public const string ThinkingLabel = "thinking";

    /// <summary>The ghost text on the empty idle input row (<see cref="ScreenPane.Placeholder"/>): dim, gone with the first key, never under the spinner. Pinned.</summary>
    public const string InputPlaceholder = "Type a message or /help for more info";

    public const string ConnectingLabel = "looking for an LLM server";
    public const string ServerSearchLabel = "looking for LLM servers";

    /// <summary>The spinner over a scan that reaches the network (<see cref="ScanScope.Remote"/> / <see cref="ScanScope.Both"/>): a wave of a /24 × six ports takes a few seconds, and the row should say why.</summary>
    public const string ScanningLabel = "scanning the local network for LLM servers";
    public const string SpeechConnectingLabel = "looking for the TTS server";

    /// <summary>The spinner's first label under <c>TTS source</c> = <c>in-process</c>; the session renames it while the model downloads and loads.</summary>
    public const string SpeechLoadingLabel = "preparing in-process Kokoro";
    public const string VoiceConnectingLabel = "preparing voice input";
    public const string TurnFailedPrefix = "Turn failed: ";
    public const string TtsUsageError = "/tts takes on or off, or nothing to toggle.";
    public const string VoiceUsageError = "/stt takes on or off, or nothing to toggle.";
    public const string VoiceOffHint = NoticeGlyphs.Stt + "Voice input is off; /stt turns it on.";
    public const string WakeUsageError = "/wake takes on or off, or nothing to toggle.";
    public const string WakeOnNeedsVoiceNotice = NoticeGlyphs.Wake + "Wake word on; it listens once voice input is on (/stt).";
    public const string WakeOffNotice = NoticeGlyphs.Wake + "Wake word off.";
    public const string InterruptUsageError = "/interrupt takes on or off, or nothing to toggle.";
    public const string InterruptOnNeedsVoiceNotice = NoticeGlyphs.Interrupt + "Interrupting on; it works once voice input (/stt) and speech output (/tts) are on.";
    public const string InterruptOffNotice = NoticeGlyphs.Interrupt + "Interrupting off.";
    public const string InterruptNeedsSpeechNotice = NoticeGlyphs.Interrupt + "Interrupting works only while a reply is spoken; /tts turns speech output on.";
    public const string InterruptNeedsWakeNotice = NoticeGlyphs.Interrupt + "Interrupting needs the wake word; /wake on turns it on.";
    public const string InterruptOffWithWakeNotice = NoticeGlyphs.Interrupt + "Interrupting off with the wake word.";
    public const string InterruptedNotice = "(" + NoticeGlyphs.Interrupt + "interrupted)";
    public const string InterruptDisabledReason = "switched off after two interruptions heard nothing";
    public const string RememberUsageError = "/remember takes the text to keep: /remember <text>";
    public const string MemoryOffNotice = NoticeGlyphs.Memory + "Memory is off; turn it on in /settings (the Memory row).";
    public const string MemoryFullError = "Memory is full (" + MaxMemoriesText + " entries); /memory forget clears it.";
    public const string MemoryFailedError = "Could not save the memory; the log has the reason.";
    public const string NothingToForgetNotice = "(" + NoticeGlyphs.Memory + "nothing to forget)";
    public const string KeptNotice = "(kept)";
    public const string ProfileUsageError = "/profile takes nothing (pick), a name, add <name>, delete <name>, rename <name> <new-name>, reset [name], edit or reload.";

    // The /loop words and lines (2026-09-21, the user's ask). Pinned.
    public const string LoopInfiniteWord = "infinite";
    public const string LoopInfiniteNote = "send the message until ESC or Ctrl+C stops it: /loop infinite [delay] <message>";
    public const string LoopUsageError = "Usage: /loop <count> [delay] <message>, or /loop infinite [delay] <message>; delay like 30s, 5m, 1h30m (ESC or Ctrl+C stops it).";
    /// <summary>The longest wait between passes: <c>/timer</c>'s ceiling, 24 hours (2026-09-24).</summary>
    public static readonly TimeSpan LoopMaxDelay = TimeSpan.FromHours(24);
    /// <summary>The notice before the wait between two passes.</summary>
    public static string LoopWaitNotice(TimeSpan delay) => $"(loop waiting {TimerText.Describe(delay)})";
    /// <summary>The notice above each pass: the count so far, of the total when there is one.</summary>
    public static string LoopTurnNotice(int n, int? total) => total is null ? $"(loop {n})" : $"(loop {n} of {total})";
    /// <summary>The notice after the last pass of a counted loop.</summary>
    public static string LoopDoneNotice(int total) => $"(loop done: {UsageText.Plural(total, "message", "messages")} sent)";
    /// <summary>The notice when a pass ended the loop early: cancelled, withdrawn or failed; <paramref name="ran"/> counts that pass.</summary>
    public static string LoopStoppedNotice(int ran) => $"(loop stopped after {UsageText.Plural(ran, "message", "messages")})";

    // A looped command (2026-09-25, the user's ask: /loop infinite 1s /imagine …, no model in between). Pinned.
    /// <summary>The commands <c>/loop</c> runs itself rather than send to the model — the user's pick, the two that make sense again and again.</summary>
    public static readonly IReadOnlyList<SlashCommand> LoopableCommands = [SlashCommand.Imagine, SlashCommand.Speak];
    /// <summary>The notice after the last pass of a counted command loop.</summary>
    public static string LoopCommandDoneNotice(int total) => $"(loop done: {UsageText.Plural(total, "run", "runs")})";
    /// <summary>The notice when a pass ended a command loop early: cancelled or failed; <paramref name="ran"/> counts that pass.</summary>
    public static string LoopCommandStoppedNotice(int ran) => $"(loop stopped after {UsageText.Plural(ran, "run", "runs")})";
    /// <summary>A <c>/loop</c> over a command it does not run.</summary>
    public static string LoopCommandNotLoopableError(string token) => $"Only /imagine and /speak can be looped; {token} cannot.";
    /// <summary>The watched gap between command passes when the loop names no delay: a pass that returns at once (<c>/speak</c> with speech off) still leaves ESC a moment to stop an infinite loop.</summary>
    public static readonly TimeSpan LoopCommandMinGap = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// <c>/loop</c>'s grammar (2026-09-21): the first word is <see cref="LoopInfiniteWord"/> (any
    /// case; <paramref name="count"/> null) or a whole number of 1 or more (invariant digits, no
    /// sign), and what follows, trimmed, is the message — never empty. False for anything else. Pure.
    ///
    /// <para>The delay (2026-09-24, the user's ask): the word after the count is the gap to wait
    /// after each reply before the next pass when <see cref="TimerText.TryParseDuration(string, out TimeSpan)"/>
    /// takes it and it is not all digits (a bare number is <c>/timer</c>'s minutes, too easily read as
    /// part of the message) — <c>30s</c>, <c>5m</c>, <c>1h30m</c>, one word, at most
    /// <see cref="LoopMaxDelay"/>. A word that is no duration is the message's first, as before; a
    /// duration past the ceiling, or with nothing after it, is a usage error.</para>
    /// </summary>
    public static bool TryParseLoopArgs(string args, out int? count, out TimeSpan? delay, out string message)
    {
        ArgumentNullException.ThrowIfNull(args);
        count = null;
        delay = null;
        message = "";
        string trimmed = args.Trim();
        int split = trimmed.IndexOfAny([' ', '\t']);
        if (split < 0)
        {
            return false;
        }

        string first = trimmed[..split];
        string rest = trimmed[(split + 1)..].Trim();
        if (rest.Length == 0)
        {
            return false;
        }

        if (!string.Equals(first, LoopInfiniteWord, StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1)
            {
                return false;
            }

            count = n;
        }

        int wordEnd = rest.IndexOfAny([' ', '\t']);
        string word = wordEnd < 0 ? rest : rest[..wordEnd];
        if (!word.All(char.IsAsciiDigit) && TimerText.TryParseDuration(word, out var wait))
        {
            string after = wordEnd < 0 ? "" : rest[(wordEnd + 1)..].Trim();
            if (wait > LoopMaxDelay || after.Length == 0)
            {
                count = null;
                return false;
            }

            delay = wait;
            rest = after;
        }

        message = rest;
        return true;
    }

    // The /git words (2026-09-21). Pinned.
    public const string GitUserWord = "user";
    public const string GitForceWord = ForceWord;

    /// <summary>The <c>force</c> word: <c>/git user force</c> (2026-09-21) and, later that day, <c>/persona copy &lt;profile&gt; force</c> and its siblings. Pinned.</summary>
    public const string ForceWord = "force";
    public const string GitUsageError = "/git takes user [force].";
    public const string GitUserNote = "write the Git native email and Git native name settings into this repository's .git/config";
    public const string GitUserForceNote = "the same, replacing a [user] section already there";
    public const string TimerUsageError = "/timer takes nothing (list), <duration> [name], stop <name> or stop all; a duration is 10m, 90s, 1h30m, or minutes as a number.";
    public const string NoTimersNotice = "(" + NoticeGlyphs.Timer + "no timers)";

    /// <summary>The <c>/cwd</c> word that clears the setting back to the profile's own folder: the shell's home word, bare only (<c>~/x</c> is a path). <c>default</c> was a second word until 2026-09-16 (the user's call); it reads as a relative path now, which the save refuses.</summary>
    public const string CwdHomeWord = "~";

    /// <summary>The <c>/cwd</c> word that opens the folder picker (<see cref="FolderPane"/>, 2026-09-21); case folded.</summary>
    public const string CwdBrowseWord = "browse";

    /// <summary>The <c>/queue</c> word that drops every queued message (2026-09-21, the user's ask); case folded.</summary>
    public const string QueueClearWord = "clear";
    public const string QueueClearNote = "drop every queued message";
    public const string QueueUsageError = "/queue lists the queued messages; /queue clear drops them all.";


    // The /memory grammar's words, their notes on the argument list and the usage error (2026-09-22,
    // the user's ask: the wipe was the standalone /forget, and the copy the standalone /memcopy, until
    // that day). The /queue trio's shape; the copy's words are CopyWord and OverwriteWord, shared with
    // the prompt files and /cmdcopy. Pinned.
    public const string MemoryForgetWord = "forget";
    public const string MemoryForgetNote = "forget every memory";
    public const string MemoryEditWord = "edit";   // 2026-09-23, the user's ask: memory.json in the editor, as /profile edit opens profile.json
    public const string MemoryEditNote = "open memory.json in your editor";
    public const string MemoryUsageError = "/memory lists the memories, /memory forget forgets them all, /memory edit opens memory.json in your editor, and /memory copy <profile> [overwrite] copies them into another profile.";

    /// <summary>The <c>/copy</c> word for every exchange.</summary>
    public const string CopyAllWord = "all";
    public const string CopyUsageError = "/copy copies the last reply; /copy <n> the last n; /copy all every one.";
    public const string NothingToCopyNotice = "(nothing to copy yet)";
    public const string CopyFailedError = "Could not write to the clipboard; try again.";
    public static readonly string ProfileNameError = "Profile name " + Profiles.NameError + ".";
    private const string MaxMemoriesText = "200";

    /// <summary>The window title while the <see cref="Profiles.DefaultName"/> profile is loaded. Pinned.</summary>
    public const string DefaultWindowTitle = "Neon";

    /// <summary>The most cells a window title takes, the ellipsis included.</summary>
    public const int WindowTitleCells = 25;

    /// <summary>
    /// The terminal window's title for the loaded profile: <see cref="DefaultWindowTitle"/> for the
    /// default profile, else the name itself, cut to <see cref="WindowTitleCells"/> with an
    /// ellipsis (<see cref="ScreenPane.Fit"/>) when longer. Pinned.
    /// </summary>
    public static string WindowTitle(string profileName)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        return Profiles.IsDefault(profileName) ? DefaultWindowTitle : ScreenPane.Fit(profileName, WindowTitleCells);
    }

    /// <summary>The pause between silencing the speaker and opening the microphone: its last buffer and the room's reverb.</summary>
    public static readonly TimeSpan InterruptSettle = TimeSpan.FromMilliseconds(300);

    /// <summary>Under the no-server line. Pinned.</summary>
    public static readonly string NoServerHint = $"{NoticeGlyphs.Llm}Set the URL with /settings or {EnvironmentOverrides.LlmUrlVariable}.";

    /// <summary>Under the no-server line when <c>LLM scan mode</c> is <c>disabled</c> (2026-09-15): the two ways out. Pinned.</summary>
    public static readonly string ScanDisabledHint = $"{NoticeGlyphs.Llm}Set the URL with /settings (LLM URL, or /server <url>) or {EnvironmentOverrides.LlmUrlVariable}, or set LLM scan mode to local, remote or both.";

    /// <summary>The hint under <see cref="LlmSession.NoServerLine"/> for <paramref name="scope"/>: <see cref="ScanDisabledHint"/> when nothing was looked for, else <see cref="NoServerHint"/>.</summary>
    public static string NoServerHintFor(ScanScope scope) => Llm.LlmScanMode.Scans(scope) ? NoServerHint : ScanDisabledHint;

    /// <summary>The reply to a message when there is no assistant. Pinned.</summary>
    public static readonly string NoAssistantError = $"No LLM endpoint. Set the URL with /settings or {EnvironmentOverrides.LlmUrlVariable}.";

    private readonly ScreenPane _pane;
    private readonly AppSettings _settings;
    private readonly Func<AppSettingsData> _effective;
    private readonly Func<SettingsField, string?> _overriddenBy;
    private readonly LlmSession _session;
    private readonly SpeechSession _speech;
    private readonly VoiceSession _voice;
    private readonly McpSession _mcp;
    private readonly bool _ownsMcp;
    private readonly Action<string> _openFile;

    /// <summary>The <c>--log</c> file, full path (2026-09-22): <c>/log</c> opens it, and only while it is set is <c>/log</c> a command, in <c>/help</c> and in the completion list. Null = started without <c>--log</c>.</summary>
    private readonly string? _logFile;
    private readonly Func<string, string, CancellationToken, Task>? _editDraft;
    private readonly Random _random;
    private readonly SplashSource? _splash;

    // The welcome splash is on the screen (drawn at startup, gone with the first sent line or any
    // redraw of the banner); the flag is what DismissSplash reads, the name is the picture Left /
    // Right step from (CycleSplash, 2026-09-19); the forced flag says /splash drew it (later that
    // day), so the arrows walk it whatever Welcome splash says; the count is the source's picture
    // count at the show, what the hint row reads on every tick in place of a scan of the profile's
    // folder (CycleSplash reads the folder live, so a file dropped or removed mid-session moves the
    // walk and not the hint until the next show). The folder (2026-09-24) is the source's
    // SplashSource.Directory at the show — the profile's own splash folder, null over the embedded
    // set — what Delete Delete removes the named file from. The page (2026-09-24) is the tiled
    // splash's (Welcome splash tiled): the page on screen, zero-based, null while one picture stands —
    // then the count is the pages', the name and the folder null (no one picture to step from or to
    // trash). All six fall together (ForgetSplash). The tiles are the tiled splash's pictures read
    // once for a source and a box (ShowSplashPage), kept across a page flip and the redraw it makes —
    // registered for a double-click once, too — and read again when the names, the folder or the box change.
    private bool _splashShown;
    private bool _splashForced;
    private string? _splashName;
    private int _splashCount;
    private string? _splashFolder;
    private int? _splashPage;
    private SplashTiles? _splashTiles;

    /// <summary>The tiled splash's decoded pictures for one source and one thumbnail box, with their double-click ids.</summary>
    private sealed record SplashTiles(IReadOnlyList<string> Names, string? Directory, ThumbnailBox Box, List<ImageThumbnail> Thumbnails, List<int> Ids);
    private readonly IReadOnlyList<AIFunction> _clockTools;
    private readonly WorkingDirectory _files;
    private readonly IReadOnlyList<AIFunction> _fileTools;
    private readonly WebAccess _web;
    private readonly IReadOnlyList<AIFunction> _webTools;
    private readonly GitAccess _git;
    private readonly IReadOnlyList<AIFunction> _gitTools;
    private readonly ObsidianVault _vault;
    private readonly IReadOnlyList<AIFunction> _vaultTools;
    private readonly SqlAccess _sql;
    private readonly IReadOnlyList<AIFunction> _sqlTools;
    private readonly ComfyStudio _comfy;
    private readonly IReadOnlyList<AIFunction> _comfyTools;

    // What /imagine made since the last message (2026-09-24): the result lines and the pictures, handed to the model with the next one.
    private readonly List<string> _imagineNotes = [];
    private readonly List<ImageAttachment> _imagineImages = [];
    private readonly Interpreters _interpreters;
    private readonly ShellRunner _runner;
    private readonly ProcessRegistry _processes;
    private readonly CommandAllowList _allowList;
    private readonly CommandGate _gate;
    private readonly IReadOnlyList<AIFunction> _shellTools;
    private readonly CommandApprovalMenu _approvalMenu;
    private readonly IReadOnlyList<AIFunction> _askTools;
    private readonly SkillCatalog _catalog;
    private readonly IReadOnlyList<AIFunction> _skillTools;
    private readonly ProjectFile _project;
    private readonly QuestionMenu _questionMenu;
    private readonly KeySource _keys;
    private readonly TranscriptRenderer _transcript;
    private readonly InputLine _input;
    private readonly Action<string, string> _openImage;

    /// <summary>
    /// A picture drawn in the transcript, for a double-click to open (later on 2026-09-24): its name, the file it came from
    /// when there is one, and its bytes for when there is none. An open prints nothing; only an error does (2026-09-24, the user's call).
    /// </summary>
    private sealed record PictureSource(string Name, string? FullPath, byte[] Bytes);

    // Every picture drawn, by id (its index); read on the watcher task too, so under its own lock.
    private readonly List<PictureSource> _pictures = [];

    /// <summary>The session's ComfyUI pictures over the pane's upper rule (later still on 2026-09-24): filled by <see cref="AddToPictureStrip"/>, emptied with every new conversation.</summary>
    private readonly PictureStrip _pictureStrip = new();
    private readonly InfoPane _info;
    private readonly FolderPane _folderPane;
    private readonly MenuPane _menuPane;
    private readonly SettingsMenu _menu;

    // Bound to the loaded profile by BindProfile: rebuilt on every switch.
    private MemoryStore _memory = null!;
    private PersonaFile _persona = null!;
    private OperataFile _operata = null!;
    private VocaliaFile _vocalia = null!;
    private IReadOnlyList<AIFunction> _memoryTools = null!;
    private MemoryMenu _memoryMenu = null!;
    private SessionStore _sessions = null!;
    private IReadOnlyList<AIFunction> _sessionTools = null!;
    private SessionsMenu _sessionsMenu = null!;

    /// <summary>The store row the conversation on screen is written to (2026-09-18): null until its first completed turn and after every conversation clear, so an empty conversation is never stored.</summary>
    private long? _sessionId;

    /// <summary>
    /// The current session's summary as last read — its title and where it came from, for the rule
    /// above the input row (2026-09-18): set with <see cref="_sessionId"/> by <see cref="RefreshSessionTitle"/>
    /// at every place a title is written (the first turn, the model's answer from the pool, a rename,
    /// a restore) and dropped with it by <see cref="ForgetSession"/>. Volatile: the pane's tick reads it.
    /// </summary>
    private volatile SessionSummary? _sessionTitle;

    /// <summary>Reads the current session's summary again (null without a session or a row).</summary>
    private void RefreshSessionTitle() => _sessionTitle = _sessionId is { } id ? _sessions.Summary(id) : null;

    /// <summary>The conversation on screen is no longer a stored session: the next turn begins a new row.</summary>
    private void ForgetSession()
    {
        _sessionId = null;
        _sessionTitle = null;
    }

    /// <summary>The saved <c>Session show name</c> word last resolved and what it meant: the pane reads the setting on every draw and tick, and <see cref="SessionShowName.Resolve"/> warns on a hand-edited value — once per value this way, not once per tick.</summary>
    private sealed record ShowNameCache(string Text, SessionNameDisplay Display);

    private volatile ShowNameCache? _showName;

    /// <summary><see cref="SessionShowName.Resolve"/> over <paramref name="effective"/>, memoised on the saved word.</summary>
    private SessionNameDisplay ShowNameDisplay(AppSettingsData effective)
    {
        string text = effective.SessionShowName;
        if (_showName is { } cached && string.Equals(cached.Text, text, StringComparison.Ordinal))
        {
            return cached.Display;
        }

        var display = SessionShowName.Resolve(effective);
        _showName = new ShowNameCache(text, display);
        return display;
    }

    /// <summary>
    /// What the upper rule shows for <paramref name="session"/> under <paramref name="display"/>
    /// (<c>Session show name</c>, 2026-09-18): nothing without a session or under <c>none</c>; every
    /// title under <c>all-names</c>; under <c>model-written</c> a model-written or typed title, never
    /// the automatic first line. Pinned.
    /// </summary>
    public static string SessionRuleTitle(SessionSummary? session, SessionNameDisplay display) => session switch
    {
        null => "",
        _ when display == SessionNameDisplay.None => "",
        { TitleSource: TitleSource.FirstLine } when display == SessionNameDisplay.ModelWritten => "",
        _ => session.Title,
    };

    // The messages queued while a reply runs (2026-09-18): the screen's, never a profile's, so a
    // switch drops rather than rebinds it. Enqueued on the watcher task, drained by the idle loop.
    private readonly MessageQueue _queue = new();
    private readonly QueueMenu _queueMenu;
    private readonly SkillsMenu _skillsMenu;
    private readonly ToolsMenu _toolsMenu;
    private readonly McpMenu _mcpMenu;

    /// <summary>Set by <see cref="RunTurnAsync"/>'s end: the reply was cancelled, interrupted or withdrawn, so <see cref="RunMessageAsync"/> applies <c>Queue cancel mode</c> instead of releasing a hold.</summary>
    private bool _lastTurnCancelled;

    /// <summary>Set beside <see cref="_lastTurnCancelled"/>: the reply failed (a thrown turn, or a server error the assistant reported as a notice), so a <c>/loop</c> stops rather than send the same message to a server that is down (2026-09-21).</summary>
    private bool _lastTurnFailed;

    /// <summary>The pair of clicks on the busy row's queued count or the scroll's hint (<see cref="HintClickLine"/>), on the watcher task alone; reset at each watcher's start.</summary>
    private readonly DoubleClick _queuedClicks;
    private readonly Action<IAnsiConsole> _renderScreen;
    private readonly ConcurrentQueue<DiagnosticEvent> _pending = new();
    private readonly InterruptTracker _interrupts = new();
    private readonly TimerBoard _timers;
    private readonly ChatLog _log = new();
    private readonly Func<string, bool> _copy;
    private readonly Action<string>? _setTitle;
    private readonly TimeProvider _time;

    // The turn's LLM show thinking, read at its start, and when the thinking block now streaming began
    // (null between blocks): the fold's "thought for 4.2s" (2026-09-26). Written and read on the turn's thread.
    private bool _showThinking;
    private long? _thinkingSince;

    /// <summary>How long after a first idle Ctrl+C the second one exits (the hint shows meanwhile).</summary>
    public static readonly TimeSpan ExitConfirmWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The exit arm: the UTC tick until which a second Ctrl+C at the idle line exits, 0 = none.
    /// Written on the read's task (<see cref="ReadLineAsync"/>'s hook), read by <see cref="HintText"/>
    /// on the pane's timer thread — a long under <see cref="Volatile"/>, never a struct.
    /// </summary>
    private long _exitArmedUntil;

    /// <summary>How long after a first Delete over a profile's splash picture the second one deletes it (2026-09-24, as <see cref="ExitConfirmWindow"/>).</summary>
    public static readonly TimeSpan SplashDeleteWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The splash delete's arm (2026-09-24): the UTC tick until which a second Delete in a row removes
    /// the picture on screen, 0 = none. Written on the read's task (<see cref="DeleteSplash"/>), read by
    /// <see cref="HintText"/> on the pane's timer thread, as <see cref="_exitArmedUntil"/> is.
    /// </summary>
    private long _splashDeleteArmedUntil;

    /// <summary>
    /// A console write that draws nothing new, for the moment the console's input mode changes
    /// (the mouse taken or handed back): ConPTY applies the change with the next write. Safe from
    /// any thread; <see cref="SidekickApp"/> hands it to the console input as its callback.
    /// </summary>
    public Action FlushConsole => _pane.Touch;

    // The console input's mouse hooks (WindowsConsoleInput.Capture / HoldWheel; null without a real console).
    private readonly Action<bool>? _mouse;
    private readonly Action<bool>? _holdWheel;

    /// <summary>
    /// The chat line's editor for a key watcher (2026-09-25, the user's ask: the arrows, the history, the lists and the
    /// mouse did nothing under a <c>/botchat</c>): the row stays the idle line's editor under every reply and spinner. Only
    /// on the pane — without it the row is not drawn under a reply and the keys stay type-ahead, as before.
    /// </summary>
    private InputLine.Editor? LiveEditor => _pane.Enabled ? _input.Chat : null;

    /// <summary>The line hook of a watch that runs no command (a spinner's, 2026-09-25): a line sent there waits for the idle line. Null without the pane.</summary>
    private Func<KeySource.WatchedLine, Task<bool>>? LiveLineHook => _pane.Enabled ? PendLineAsync : null;

    /// <summary>
    /// The key watcher's <c>spend</c> hook: PgUp/PgDn page the transcript region, a wheel notch
    /// scrolls it (<see cref="ScreenPane.WheelRows"/>) and Ctrl+End is the bottom again
    /// (<see cref="ScreenPane.ScrollToEnd"/>) while a turn, a compact or a recording runs, as they
    /// do on the idle line; true = spent, never type-ahead.
    /// </summary>
    public bool ScrollInput(InputEvent input)
    {
        switch (input)
        {
            case InputEvent.Key { Info.Key: ConsoleKey.PageUp }:
                _pane.ScrollPage(-1);
                return true;
            case InputEvent.Key { Info.Key: ConsoleKey.PageDown }:
                _pane.ScrollPage(1);
                return true;
            case InputEvent.Key { Info: { Key: ConsoleKey.End } end } when (end.Modifiers & ConsoleModifiers.Control) != 0:
                // Ctrl+End: the bottom again, the reply streaming into view (2026-09-17); a plain End is the draft's, type-ahead.
                _pane.ScrollToEnd();
                return true;
            case InputEvent.Key { Info: { Key: ConsoleKey.Home } home } when (home.Modifiers & ConsoleModifiers.Control) != 0:
                // Ctrl+Home: the transcript's first rows (2026-09-18); a plain Home is the draft's.
                _pane.ScrollToTop();
                return true;
            case InputEvent.Wheel wheel:
                _pane.ScrollWheel(wheel.Notches);
                return true;
            case InputEvent.Key { Info: var toggle } when Keys.IsToolToggle(toggle):
                // Ctrl+O (2026-09-22): every tool run unfolded or folded, the reply's own included.
                _pane.ToggleToolGroups();
                return true;
            default:
                return false;
        }
    }

    private readonly IReadOnlyList<AIFunction> _timerTools;

    /// <summary>The idle read's alert source while one is waiting; the board's signal cancels it from the clock's thread.</summary>
    private CancellationTokenSource? _alertSignal;

    /// <summary>
    /// Whether the speaker now playing had the interrupt armed by its turn, so the idle read arms
    /// it again over the tail. Set by every <see cref="RunTurnAsync"/>; false for an alert's speech.
    /// </summary>
    private bool _tailInterrupt;

    /// <summary>
    /// The last <c>/speak</c> reading (2026-09-17): the file, its sentences, where it is. Kept
    /// across turns — a bare <c>/speak</c> resumes it, <c>/speak &lt;n&gt;</c> seeks in it — until
    /// another file, <c>/clear</c>, <c>/new</c>, a profile switch or a <c>/cwd</c> change.
    /// </summary>
    private SpeakReading? _reading;

    /// <summary>
    /// The last <c>/echo</c> (2026-09-17): a reading of a typed line, kept only so its status can
    /// be read; a bare <c>/speak</c> never resumes it.
    /// </summary>
    private SpeakReading? _echo;

    /// <summary>
    /// The reading whose status is on the hint row (<see cref="HintLine"/>'s reading part) — the
    /// reading or the echo started last — from its <c>/speak</c> or <c>/echo</c> until the next
    /// turn, when the row is the turn's again (the user's rule); the reading itself is
    /// remembered on. Null = nothing there.
    /// </summary>
    private volatile SpeakReading? _hintReading;

    /// <param name="effective">The settings in force (flags over variables over the file), read fresh on every connect and turn.</param>
    /// <param name="overriddenBy">For the menus: the variable or flag that outranks a saved field, or null.</param>
    /// <param name="openFile">Opens a file in an external editor without waiting for it; <c>/persona</c>, <c>/operata</c> and <c>/vocalia</c> call it with the file's path. The app passes <see cref="PersonaFile.OpenInEditor"/>; tests record the call. A throw is reported as an error line.</param>
    /// <param name="renderScreen">Wipes the terminal and draws the start-of-app view (the banner) on the console it is given; <c>/clear</c> calls it with the pane, so the rows are counted. The screen never draws the banner itself.</param>
    /// <param name="time">The clock the clock tools, the timers and the pane's tick read; tests pass a manual one.</param>
    /// <param name="geometry">Where the console's cursor is, for the bottom pane; null (tests, a redirected console) draws the input line where the transcript ends.</param>
    /// <param name="clipboard">The text the input row's own paste (a right click, Ctrl+V where the terminal lets it through, Alt+V) puts on the line; null = nothing.</param>
    /// <param name="mouse">Takes the mouse (true) for the screen's run and under every pane (<see cref="WindowsConsoleInput.Capture"/> in the app; the <c>Mouse in menus</c> setting that could hand it back under a pane went on 2026-09-21); null = the terminal keeps it.</param>
    /// <param name="copyToClipboard">What <c>/copy</c> writes the markdown with, true on success (<see cref="WindowsClipboard.TrySetText"/> in the app; tests record the text); null = every copy fails.</param>
    /// <param name="random">What picks the thinking spinner's verb under <see cref="AppSettingsData.LlmUseFunVerbs"/>; null = <see cref="Random.Shared"/> (tests seed one).</param>
    /// <param name="clipboardImage">The picture the same paste takes ahead of the text, as an image file's bytes (<see cref="WindowsClipboard.TryReadImage"/> in the app; tests a lambda); null = never.</param>
    /// <param name="web">What the web tools run over (the client, the headless browser, the page cache); null = the app's own over the live <c>Web browser network mode</c> setting. Tests pass one over a stub client.</param>
    /// <param name="setTitle">What sets the terminal window's title to <see cref="WindowTitle"/> at launch and after every profile switch (<see cref="ConsoleTitle.TrySet"/> in the app; tests record the titles); null = never.</param>
    /// <param name="externalSkills">The cross-client skills folder (<see cref="SkillRoots.DefaultExternalDirectory"/> in the app; tests a temp folder); null = the app's.</param>
    /// <param name="splash">The welcome splash pictures (<see cref="SplashImages.Source"/> in the app: the embedded names and their loader — one picked at random with <paramref name="random"/> at startup, the others walked by Left / Right; tests a name list over generated pictures); null = no splash whatever <see cref="AppSettingsData.WelcomeSplashMode"/> says.</param>
    /// <param name="editDraft">Opens <c>/draft</c>'s temporary file (the path, the <c>Draft editor</c> command line — blank for the shell's default — and a token) and completes when the editor is done with it (<see cref="PersonaFile.EditAndWaitAsync"/> in the app; tests a lambda that writes the file, or waits on the token); null = <c>/draft</c> answers <see cref="DraftUnavailableError"/>.</param>
    /// <param name="mcp">The MCP servers' session (2026-09-20; <see cref="SidekickApp"/> builds one beside the LLM session and disposes it after the screen); null = the screen builds its own over the real transports and disposes it when it closes (the tests', with nothing configured in their temp home).</param>
    /// <param name="environment">Reads a system variable for the shell probe (<c>PATH</c>, <c>PATHEXT</c>; <see cref="EnvironmentOverrides.System"/> in the app, 2026-09-21); null = no PATH at all, which still finds <c>cmd.exe</c> and Windows PowerShell under the system folder (the tests' deterministic pair).</param>
    /// <param name="logFile">The <c>--log</c> file, full path (2026-09-22): <c>/log</c> opens it with <paramref name="openFile"/>, and only while it is given is <c>/log</c> a command, in <c>/help</c> and in the completion list; null = started without <c>--log</c> (and the tests).</param>
    public ChatScreen(
        IAnsiConsole console,
        AppSettings settings,
        Func<AppSettingsData> effective,
        Func<SettingsField, string?> overriddenBy,
        LlmSession session,
        SpeechSession speech,
        KeySource keys,
        VoiceSession voice,
        Action<string> openFile,
        Action<IAnsiConsole> renderScreen,
        TimeProvider time,
        ScreenGeometry? geometry = null,
        Func<string?>? clipboard = null,
        Action<bool>? mouse = null,
        Func<string, bool>? copyToClipboard = null,
        Random? random = null,
        Func<byte[]?>? clipboardImage = null,
        WebAccess? web = null,
        Action<string>? setTitle = null,
        string? externalSkills = null,
        Action<bool>? holdWheel = null,
        SplashSource? splash = null,
        Func<string, string, CancellationToken, Task>? editDraft = null,
        McpSession? mcp = null,
        Func<string, string?>? environment = null,
        string? logFile = null,
        Func<Uri, ComfyClient>? comfyClient = null,
        Action<string, string>? openImage = null)
    {
        _logFile = logFile;
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _random = random ?? Random.Shared;
        _splash = splash;
        _setTitle = setTitle;
        ArgumentNullException.ThrowIfNull(console);
        _renderScreen = renderScreen ?? throw new ArgumentNullException(nameof(renderScreen));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _overriddenBy = overriddenBy ?? throw new ArgumentNullException(nameof(overriddenBy));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
        _openFile = openFile ?? throw new ArgumentNullException(nameof(openFile));
        _editDraft = editDraft;
        // A double-clicked picture's opener (later on 2026-09-24): the image editor in the app; without one, the plain opener (the command unused).
        _openImage = openImage ?? ((path, _) => _openFile(path));
        _ownsMcp = mcp is null;
        _mcp = mcp ?? new McpSession(settings, McpSession.DefaultTransport, time);
        _clockTools = ClockTools(time);
        // The sandbox reads the live setting and profile directory on every call: a /cwd save or
        // a profile switch changes the root with nothing to rebind.
        _files = new WorkingDirectory(() => WorkingDirectory.Resolve(_effective().WorkingDirectory, _settings.ProfileDirectory), time);
        _fileTools = FileTools(_files, () => WorkingDirectory.IsDefault(_effective().WorkingDirectory), _openFile, _effective);
        _timers = new TimerBoard(time, SignalAlert);
        _timerTools = TimerTools(_timers);
        _web = web ?? WebAccess.Create(() => Web.NetworkMode.Resolve(_effective()), time);
        _webTools = WebTools(_web, _files, _effective);
        // The git tools (2026-09-20) sit on the same sandbox: the repository is looked for from a sandbox path, never above the root.
        _git = new GitAccess(_files, time);
        _gitTools = GitTools(_git, _effective);
        // The vault tools (2026-09-22): their own root, the setting Obsidian vault read at every call.
        _vault = new ObsidianVault(() => _effective().ObsidianVault, time);
        _vaultTools = ObsidianTools(_vault, _effective);
        // The SQL tools (2026-09-23): the loaded profile's sql.json over the home's, read at every call, so a profile switch or an edit needs no rebuild.
        // Narrowed to the connections the profile offers (later that day): every tool, the rules and the %-mention see only those.
        _sql = new SqlAccess(() => SqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(_effective().SqlConnectionsOffered));
        _sqlTools = SqlTools(_sql, _effective);
        // The image tools (2026-09-24): the profile's comfy folder over the home's, rescanned at every call; the client made for the ComfyUI URL in force.
        // A pasted picture as generate_image's input (later still on 2026-09-24): the line's store at full size, read at
        // call time — the input line is built below, so the lambda reads the field then, not now.
        _comfy = new ComfyStudio(ComfyCatalog(settings), _files, _effective, comfyClient, _random, pasted: n => _input?.Pastes.Original(n), time: _time);
        _comfyTools = ComfyTools(_comfy, _files, () => _settings.ProfileSplashDirectory);
        // The shell tools (2026-09-21): the runner is the one process-start site of the group; the allow
        // list lives for the process (a /clear or a profile switch keeps the session's allows, the permanent
        // ones are the loaded profile's); the gate asks through the approval pane (ApproveCommandAsync).
        _interpreters = new Interpreters(environment ?? (_ => null));
        _runner = new ShellRunner(time);
        // The background processes (phase B): the board signals the idle read like the timers, and is killed off with the screen.
        _processes = new ProcessRegistry(_runner, _random, SignalAlert);
        _allowList = new CommandAllowList(() => _effective().ShellCommandAllowed, allowed => _settings.Update(d => d.ShellCommandAllowed = [.. allowed]));
        _gate = new CommandGate(_effective, _allowList, ApproveCommandAsync);
        _shellTools = ShellTools(_runner, _processes, _files, _gate, _interpreters, _effective, _random, () => _session.Assistant?.Tools ?? []);
        // The skills read the live roots too: the profile's folder moves with a switch, the
        // project file with the sandbox's root.
        string external = externalSkills ?? SkillRoots.DefaultExternalDirectory();
        Func<SkillRoots> roots = () => SkillRoots.For(_settings, external);
        _catalog = new SkillCatalog(roots);
        _skillTools = SkillTools(_catalog, roots, () => { var e = _effective(); return e.AgentSkills && e.ExternalSkills; });
        _project = new ProjectFile(() => _files.Root);
        _copy = copyToClipboard ?? (_ => false);
        // Everything the screen shows goes through the pane: the transcript flows above it, the
        // input row and the hint line stay on the window's last rows. The profile's name is in the
        // window title (WindowTitle), not on the row (2026-09-15).
        _pane = new ScreenPane(console, geometry, time)
        {
            Hint = HintText,
            // The strip at the row's start in every state (the spinner and a menu's hint included):
            // the brain while a reflection runs, the tag while the model writes a session title,
            // then the speech switches as of the last connect, the wake word and the interrupt
            // once ready. The tick re-reads it, so the brain and the tag come and go with their
            // jobs (LlmSession.IsLearning / IsTitling), nothing pushed.
            Strip = () => StripGlyphs(_session.IsLearning, _session.IsTitling, _speech.Enabled, _voice.Enabled, _voice.WakeReady, _voice.InterruptReady),
            // The model at the row's right edge, from the live connection: empty until one lands;
            // the reasoning glyph after it in its own colour, none with the model.
            // The session's name at the right edge of the rule above the input row (2026-09-18, the user's
            // ask), as Session show name allows; read per draw and on the tick, so the model's title lands
            // from the pool and a flipped setting shows at once.
            RuleTitle = () => SessionRuleTitle(_sessionTitle, ShowNameDisplay(_effective())),
            Trailer = () => ModelLabel(_session.Endpoint?.ModelId),
            TrailerMark = () => ModelMark(_session.Endpoint?.ModelId, _effective().LlmReasoning),
            // The queued count after the row's lead in both states (2026-09-18): the pane draws it
            // and records where, so a double-click on it can open /queue mid-turn and at idle.
            Queued = () => _queue.Count is > 0 and var queued ? QueuedHintPart(queued) : "",
            // The tally as HintText carries it (2026-09-21): the pane finds it in the drawn row and
            // records where, so a double-click on it (or on the spinner under a turn) opens /usage.
            Usage = () => UsageText.HintPart(_session.Usage, _session.ContextLength) ?? "",
            // The tally on the busy row too (2026-09-25, the user's ask), as LLM mid-turn usage says: re-read on every
            // tick, so the estimate moves with the stream and a flipped setting shows at once. TryParse, not Resolve: the
            // draw must not warn on a hand-edited word, the turn's start does. None under a /botchat turn — not tallied.
            BusyUsage = () => _botTurnRunning ? "" : UsageText.BusyHintPart(_session.Usage, _session.ContextLength, MidTurnUsageMode.TryParse(_effective().LlmMidTurnUsage, out var mode) ? mode : MidTurnUsage.LastKnown, _session.Meter.Read()) ?? "",
            // A ComfyUI generation's label after the tally (2026-09-25, the user's call): /imagine, a botchat picture and the model's generate_image alike.
            LabelAfterUsage = label => string.Equals(label, ComfyText.GeneratingLabel, StringComparison.Ordinal),
            // The toolbar under the hint row (2026-09-21): the pane glyphs, the working directory in
            // force (the resolved path, what /cwd prints and the banner shows) and the folder; read
            // per draw and on the tick, so a /cwd change or a flipped Show toolbar shows at once —
            // and the lock (later still that day) follows Shell command policy the same way; TryParse,
            // not Resolve: the draw must not warn on a hand-edited word, the turn does. The disk and
            // the officer (2026-09-22) follow Memory and Shell police outside paths the same way.
            Toolbar = () => _effective() is { ShowToolbar: true } shown ? new ScreenPane.ToolbarParts(ToolbarStripFor(shown.Memory, ToolbarPolicy(shown), shown.ShellPoliceOutsidePaths), WorkingDirectory.Resolve(shown.WorkingDirectory, _settings.ProfileDirectory)) : null,
            // The picture strip over the upper rule (later still on 2026-09-24): while ComfyUI picture strip is on;
            // read per draw and on the tick, so a flip shows at once.
            PictureStrip = () => _effective().ComfyPictureStrip ? _pictureStrip : null,
            Placeholder = InputPlaceholder,
        };
        _keys.Mirror = _pane;
        _transcript = new TranscriptRenderer(_pane)
        {
            // Tool collapse count (2026-09-22): read when a tool run opens, clamped as the menu saves it.
            ToolCollapseCount = () => Math.Clamp(_effective().ToolCollapseCount, AppSettingsData.MinToolCollapseCount, AppSettingsData.MaxToolCollapseCount),
            // Code collapse count (later on 2026-09-22): read when a reply opens, clamped the same way.
            CodeCollapseCount = () => Math.Clamp(_effective().CodeCollapseCount, AppSettingsData.MinCodeCollapseCount, AppSettingsData.MaxCodeCollapseCount),
        };
        // The @-mention list asks the sandbox as it stands at the keystroke (the root is a live read too);
        // the command and #-mention lists the catalog and the two Skills-tab switches (2026-09-17);
        // Ctrl+C over a selection writes the clipboard with /copy's writer.
        _input = new InputLine(_pane, keys, clipboard, _transcript, clipboardImage, query => _files.Complete(query), CommandChoices, ArgumentChoices, HashChoices, DollarChoices, _copy, PercentChoices, CaretChoices);
        _input.Remembered = StoreCommand;
        _input.OpenPicture = OpenPicture;
        _mouse = mouse;
        _holdWheel = holdWheel;
        // The screen holds the mouse and the wheel from its start (RunAsync; the user's call,
        // 2026-09-17, once the transcript was the app's to scroll). A pane keeps the hold: its
        // open and its close both re-assert it (until 2026-09-21 the Mouse in menus setting could
        // hand the mouse to the terminal under a pane; the user made the pane's mouse permanent).
        Action<bool>? menuMouse = mouse is null ? null : _ =>
        {
            mouse(true);
            holdWheel?.Invoke(true);
        };
        _info = new InfoPane(_pane, keys, menuMouse);
        _folderPane = new FolderPane(_pane, keys, menuMouse);
        _menuPane = new MenuPane(_pane, keys, menuMouse);
        // The question tool's pane and the tool itself: built always (the /sys Tools tab
        // lists it either way), offered only while the setting Ask user and the pane say so (RunTurnAsync).
        _questionMenu = new QuestionMenu(_menuPane, _input);
        _approvalMenu = new CommandApprovalMenu(_menuPane);
        _askTools = AskTools(AskUserAsync, _effective);
        // Menus read console.Input themselves; over the key source they also see type-ahead.
        _flow = new FlowSink(this);
        _queueMenu = new QueueMenu(_queue, _flow, _menuPane);
        _queuedClicks = new DoubleClick(_pane.Time);
        _menu = new SettingsMenu(new ConsoleWithInput(_pane, keys), settings, overriddenBy, _input, _transcript, speech, _menuPane, _web.Browser.Locate, () => _interpreters.AvailableShells().Select(ShellKinds.Name).ToHashSet(StringComparer.Ordinal), () => _interpreters.AvailableLanguages([CodeLanguage.PowerShell, CodeLanguage.Python, CodeLanguage.Node]).Select(CodeLanguages.Name).ToHashSet(StringComparer.Ordinal), BrowseWorkingDirectoryAsync, BrowseVaultAsync, _openFile, comfyClient: _comfy.Client)
        {
            // A picker opened mid-turn closes on the watcher task: its saved line waits for the turn task.
            Flow = _flow,
        };
        // Built once: the roots ride the facts, so a profile switch needs no rebind; the Options rows through the settings menu (2026-09-19).
        _skillsMenu = new SkillsMenu(SkillsFacts, settings, _menu, _flow, _menuPane, _input, _openFile, name => SkillsMenu.UsageCaption(_sessions, name, _effective().SessionLogging, _time.LocalTimeZone));
        // The /tools pane (2026-09-19): the tool list over the live facts, the Ask / Files / Web rows through the settings menu.
        _toolsMenu = new ToolsMenu(ToolsFacts, settings, _menu, _flow, _menuPane);
        // The /mcp pane (2026-09-20): the servers and their tools over the session's snapshot, the Options rows through the settings menu.
        _mcpMenu = new McpMenu(McpFacts, _mcp, settings, _menu, _flow, _menuPane, _openFile, _effective);
        BindProfile();
    }

    /// <summary>
    /// The standing hint under the input line: the timers while any run, then the <c>/speak</c>
    /// reading's status (<see cref="SpeakReading.StatusLine"/>, 2026-09-17) while it is on the
    /// row, else the token tally (<see cref="UsageText.HintPart"/>) once something was counted —
    /// empty at a silent idle line. The timers take the usage part's place, as they always did;
    /// the reading takes it too, and rides after the timers with <see cref="HintJoin"/> between,
    /// so a running timer is never hidden by a long reading.
    /// The speech strip (<see cref="SpeechGlyphs"/>) is not part of it since 2026-09-15: it is the
    /// pane's <see cref="ScreenPane.Strip"/>, drawn ahead of it in every state. Pinned.
    /// </summary>
    public static string HintLine(string? timers, string? usage = null, string? reading = null) =>
        reading is null ? timers ?? usage ?? ""
        : timers is null ? reading
        : timers + HintJoin + reading;

    /// <summary>Between the timers and the reading on the hint row. Pinned.</summary>
    public const string HintJoin = " · ";

    /// <summary>
    /// The hint row under the welcome splash (2026-09-20): <see cref="SplashHint"/> alone when
    /// <paramref name="rest"/> (the timers / usage / reading line) is empty, else the two with
    /// <see cref="HintJoin"/> between — <c>← → slideshow · 4.6k / 151.4k · 3%</c>. Pinned.
    /// </summary>
    public static string SplashHintLine(string rest) => SplashHintLine(rest, SplashHint);

    /// <summary>As <see cref="SplashHintLine(string)"/> with <paramref name="lead"/> in place of <see cref="SplashHint"/> (the tiled splash's <see cref="SplashPageHint"/>, 2026-09-24).</summary>
    public static string SplashHintLine(string rest, string lead)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(lead);
        return rest.Length == 0 ? lead : lead + HintJoin + rest;
    }

    /// <summary>
    /// The hint row's speech strip (<see cref="ScreenPane.Strip"/>), one colour emoji per feature
    /// that is on (every one a surrogate pair, two cells). Since 2026-09-18 the same glyphs lead the
    /// speech status lines (<see cref="SpeechSession"/>'s <c>TTS:</c>, <see cref="VoiceSession"/>'s
    /// <c>STT:</c> / <c>Wake word:</c> / <c>Interrupt:</c>), so a change here reaches both.
    /// </summary>
    public const string TtsGlyph = "🔊";
    public const string SttGlyph = "🎤";
    public const string WakeGlyph = "👂";
    public const string InterruptGlyph = "✋";
    public const string GlyphSeparator = " ";

    /// <summary>
    /// The brain on the strip while a reflection runs (2026-09-18), ahead of the speech glyphs;
    /// <see cref="LearnGlyph"/> is the same glyph with the space the notices need after it. Pinned.
    /// </summary>
    public const string LearnStripGlyph = "🧠";

    /// <summary>
    /// The tag on the strip while the model writes the session's title (2026-09-18, the user's
    /// ask), right after the brain and ahead of the speech glyphs; it follows
    /// <see cref="LlmSession.IsTitling"/> through the pane's tick like the brain. U+1F3F7 with the
    /// variation selector (the 🗑️ shape; <see cref="UI.TextCells"/> counts the selector as zero).
    /// No click of its own: the job ends by itself within seconds. Pinned.
    /// </summary>
    public const string TitleStripGlyph = "🏷️";

    /// <summary>
    /// The toolbar under the hint row (2026-09-21, the user's ask): the pane glyphs pinned at its
    /// left in the user's order — settings, tools, MCP, skills, system prompt, and (later on
    /// 2026-09-21, the user's ask) sessions, the speech balloon its pane's title already wore — and the working
    /// directory pinned at its right (cut from the front, as the banner's). A double-click on a
    /// glyph opens the pane (<see cref="ToolbarWord"/> names the command), one on the path is
    /// <c>/cwd browse</c> (a folder glyph carried that until later that day; the user's call).
    /// Every glyph is two cells: a surrogate pair, or a character with the variation selector
    /// (the gear, the tools — the user's picks; the masks took the detective's place for the
    /// system prompt later on 2026-09-21), which <see cref="UI.TextCells"/>
    /// counts as the terminal draws it and the pane's strip walk keeps with its glyph.
    /// <c>Show toolbar</c> in the settings hides the row. Pinned.
    /// After the six a seventh comes and goes with <c>Shell command policy</c> (later still on
    /// 2026-09-21, the user's ask): the closed lock under <c>ask</c>, the open one under <c>yolo</c>,
    /// nothing under <c>off</c> — the row reads the policy at each draw (<see cref="ToolbarStripFor"/>),
    /// so a change on the Tools pane swaps the lock as the pane closes. Either lock's double-click is
    /// <c>/cmdlist</c>: the <c>Shell allowed commands</c> list opened straight, the typed word too.
    /// Two more come and go the same way (2026-09-22, the user's ask): the disk between the balloon
    /// and the lock while <c>Memory</c> is on — its pane's title already wore it — whose double-click
    /// is <c>/memory</c>, the list Enter prunes; and the officer last of all while <c>Shell police
    /// outside paths</c> is on, the glyph the transcript's refusal line wears, whose double-click is
    /// <c>/police</c> (later on 2026-09-22, the user's ask: no click of its own until then), that row's
    /// on/off page opened straight. Both follow their switch at each draw, so a flip on its pane shows
    /// as the pane closes; the columns after the balloon move with the disk, which the hit-test walk
    /// and the column-keyed pairing take as they come.
    /// </summary>
    public const string SettingsToolGlyph = "⚙️";
    public const string ToolsToolGlyph = "🛠️";
    public const string McpToolGlyph = McpText.Glyph;
    public const string SkillsToolGlyph = "🎓";
    public const string SysToolGlyph = "🎭";
    public const string SessionsToolGlyph = "💬";
    public const string MemoryToolGlyph = "💾";
    public const string CmdAskToolGlyph = "🔒";
    public const string CmdYoloToolGlyph = "🔓";
    public const string PoliceToolGlyph = "👮";
    public static readonly string ToolbarStrip = string.Join(GlyphSeparator, SettingsToolGlyph, ToolsToolGlyph, McpToolGlyph, SkillsToolGlyph, SysToolGlyph, SessionsToolGlyph);

    /// <summary>
    /// The strip drawn for the switches, in the strip's order: <see cref="ToolbarStrip"/>, the disk
    /// while <paramref name="memory"/> is on, the closed lock under <c>ask</c> or the open one under
    /// <c>yolo</c> (neither under <c>off</c>), the officer while <paramref name="police"/> is on and the policy is not <c>off</c>
    /// (later on 2026-09-22, the user's ask: with no shell tool offered there is nothing to police).
    /// The six alone with everything off. Pinned.
    /// </summary>
    public static string ToolbarStripFor(bool memory, Shell.CommandPolicyMode policy, bool police)
    {
        var strip = new StringBuilder(ToolbarStrip);
        if (memory)
        {
            strip.Append(GlyphSeparator).Append(MemoryToolGlyph);
        }

        switch (policy)
        {
            case Shell.CommandPolicyMode.Ask:
                strip.Append(GlyphSeparator).Append(CmdAskToolGlyph);
                break;
            case Shell.CommandPolicyMode.Yolo:
                strip.Append(GlyphSeparator).Append(CmdYoloToolGlyph);
                break;
        }

        if (police && policy != Shell.CommandPolicyMode.Off)
        {
            strip.Append(GlyphSeparator).Append(PoliceToolGlyph);
        }

        return strip.ToString();
    }

    /// <summary>The policy the toolbar's lock shows for <paramref name="shown"/>: the saved word parsed, a hand-edited one read as <c>ask</c> without a warning (<see cref="Shell.CommandPolicy.Resolve"/> warns once, at the turn).</summary>
    private static Shell.CommandPolicyMode ToolbarPolicy(AppSettingsData shown)
    {
        Shell.CommandPolicy.TryParse(shown.ShellCommandPolicy, out var policy);
        return policy;
    }

    /// <summary>The line the path's double-click runs: <c>/cwd browse</c>, the picker on the pane. Pinned.</summary>
    public const string CwdBrowseLine = "/cwd " + CwdBrowseWord;

    /// <summary>
    /// The command a double-click off an open pane names (later on 2026-09-21, the user's ask): a
    /// toolbar glyph's word, the path's <see cref="CwdBrowseLine"/>, the toolbar's blanks
    /// <c>/settings</c>; on the hint row the model name <c>/server</c> (2026-09-22), its reasoning mark
    /// <c>/reasoning</c>, the blanks <c>/settings</c> (scrolled or not). A strip glyph names
    /// nothing (the switches are the idle line's), nor do the queued count and the tally (never
    /// drawn under a pane). The screen closes the pane the word owns, or switches to the one it
    /// names (<see cref="HandleAsync"/>, <see cref="RunPaneAsync"/>). Pinned.
    /// </summary>
    public static string? OffPaneLine(ScreenPane.OffPaneHit hit) => hit switch
    {
        { Toolbar: { } tool } => tool.Zone switch
        {
            ScreenPane.ToolbarZone.Path => CwdBrowseLine,
            ScreenPane.ToolbarZone.Row => SlashCommands.SettingsWord,
            _ => ToolbarWord(tool.Glyph),
        },
        { Hint: { } row } => row.Zone switch
        {
            ScreenPane.HintZone.Trailer => SlashCommands.ServerWord,
            ScreenPane.HintZone.Mark => SlashCommands.ReasoningWord,
            ScreenPane.HintZone.Row or ScreenPane.HintZone.Scrolled => SlashCommands.SettingsWord,
            _ => null,
        },
        _ => null,
    };

    /// <summary>The command a double-click on a toolbar glyph runs (2026-09-21), as the typed word; null for anything else. The officer's is <c>/police</c> since later on 2026-09-22 (nothing until then). Pinned.</summary>
    public static string? ToolbarWord(string glyph) => glyph switch
    {
        SettingsToolGlyph => SlashCommands.SettingsWord,
        SkillsToolGlyph => SlashCommands.SkillsWord,
        ToolsToolGlyph => SlashCommands.ToolsWord,
        McpToolGlyph => SlashCommands.McpWord,
        SysToolGlyph => SlashCommands.SysWord,
        SessionsToolGlyph => SlashCommands.SessionsWord,
        MemoryToolGlyph => SlashCommands.MemoryWord,
        CmdAskToolGlyph or CmdYoloToolGlyph => SlashCommands.CmdListWord,
        PoliceToolGlyph => SlashCommands.PoliceWord,
        _ => null,
    };

    /// <summary>
    /// The switch a double-click on a strip glyph turns off (2026-09-18): the glyph is drawn only
    /// while its feature is on, so the click is always <c>/x off</c> — <c>/tts</c>, <c>/stt</c>,
    /// <c>/wake</c>, <c>/interrupt</c>; null for anything else, the brain included (its click is
    /// <see cref="LlmSession.CancelLearning"/>, no command) and the tag (no action: a click on it
    /// is the row's, <c>/settings</c>). Pinned.
    /// </summary>
    public static SlashCommand? SwitchForGlyph(string glyph) => glyph switch
    {
        TtsGlyph => SlashCommand.Tts,
        SttGlyph => SlashCommand.Voice,
        WakeGlyph => SlashCommand.Wake,
        InterruptGlyph => SlashCommand.Interrupt,
        _ => null,
    };

    /// <summary>
    /// The speech strip: 🔊 while speech output is on, 🎤 while the voice is on, 👂 once the wake
    /// word is ready as well and ✋ once the interrupt is too — the ear and the hand never
    /// without the microphone. Empty with everything off. <see cref="VoiceSession.ReadyLine"/>
    /// leads with the same rule (2026-09-18). Pinned.
    /// </summary>
    public static string SpeechGlyphs(bool ttsOn, bool sttOn, bool wakeReady, bool interruptReady)
    {
        var glyphs = new List<string>(4);
        if (ttsOn)
        {
            glyphs.Add(TtsGlyph);
        }

        if (sttOn)
        {
            glyphs.Add(SttGlyph);
            if (wakeReady)
            {
                glyphs.Add(WakeGlyph);
                if (interruptReady)
                {
                    glyphs.Add(InterruptGlyph);
                }
            }
        }

        return string.Join(GlyphSeparator, glyphs);
    }

    /// <summary>
    /// The hint row's strip (2026-09-18): 🧠 while a reflection runs, 🏷️ while the model writes
    /// the session's title (later that day, the user's place: right after the brain), then
    /// <see cref="SpeechGlyphs"/> — each part only while its job or switch is on, joined by
    /// <see cref="GlyphSeparator"/>, empty with nothing. The speech status lines keep
    /// <see cref="SpeechGlyphs"/> (no brain, no tag there). Pinned.
    /// </summary>
    public static string StripGlyphs(bool learning, bool titling, bool ttsOn, bool sttOn, bool wakeReady, bool interruptReady)
    {
        var parts = new List<string>(3);
        if (learning)
        {
            parts.Add(LearnStripGlyph);
        }

        if (titling)
        {
            parts.Add(TitleStripGlyph);
        }

        string speech = SpeechGlyphs(ttsOn, sttOn, wakeReady, interruptReady);
        if (speech.Length != 0)
        {
            parts.Add(speech);
        }

        return string.Join(GlyphSeparator, parts);
    }

    /// <summary>
    /// The hint row's trailer: the model id's last path segment (LM Studio's <c>lyf/Qwen…</c>,
    /// llama.cpp's file path) — <c>Qwen3-30B</c>; empty while no server is connected. Pinned.
    /// </summary>
    public static string ModelLabel(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return "";
        }

        int cut = modelId.LastIndexOfAny(['/', '\\']);
        return cut >= 0 && cut < modelId.Length - 1 ? modelId[(cut + 1)..] : modelId;
    }

    /// <summary>
    /// The mark after the trailer: the reasoning level's glyph (<see cref="ReasoningLevel.Glyph"/>,
    /// the empty circle for <c>none</c> since 2026-09-21) — <c>Qwen3-30B ◕</c> in place of
    /// <c>(high)</c>, the user's call 2026-09-15; empty while no server is connected, like the label.
    /// A double-click on it opens <c>/reasoning</c>, one on the name <c>/model</c>. Pinned.
    /// </summary>
    public static string ModelMark(string? modelId, string reasoning)
    {
        ArgumentNullException.ThrowIfNull(reasoning);
        return string.IsNullOrEmpty(modelId) ? "" : ReasoningLevel.Glyph(reasoning);
    }

    /// <summary>
    /// The standing hint: <see cref="ExitHint"/> while the exit is armed (the pane's tick re-reads
    /// it, so the row clears itself at the window's end — hidden behind an open list's or the
    /// scroll's hint, which the pane ranks first), else <see cref="SplashDeleteArmedHint"/> while a first
    /// Delete over a profile's splash picture is fresh (2026-09-24, cleared by the same tick), else
    /// <see cref="SplashHint"/> ahead of the rest
    /// while the welcome splash stands with an empty draft and the arrows would walk it
    /// (<see cref="SplashArrowsOffered"/>, <see cref="ScreenPane.DraftEmpty"/> — the pane redraws
    /// the row on the key that empties or fills the draft), else the timers / usage / reading line.
    /// </summary>
    private string HintText()
    {
        if (ExitArmed())
        {
            return ExitHint;
        }

        if (SplashDeleteArmed() && _pane.DraftEmpty && _splashName is { } armed)
        {
            return SplashDeleteArmedHint(armed);
        }

        string rest = HintLine(TimerText.StatusLine(_timers.Snapshot()), UsageText.HintPart(_session.Usage, _session.ContextLength), _hintReading?.StatusLine());
        if (!SplashArrowsOffered() && _pane.DraftEmpty && PictureStripOffered())
        {
            // The picture strip's arrows (later still on 2026-09-24): the splash keeps them while it stands.
            int at = _pictureStrip.Selected;
            return SplashHintLine(rest, at >= 0 ? ComfyText.StripSelectedHint(at + 1, _pictureStrip.Count) : ComfyText.StripHint);
        }

        if (!SplashArrowsOffered() || !_pane.DraftEmpty)
        {
            return rest;
        }

        return _splashPage is { } page ? SplashHintLine(rest, SplashPageHint(page, _splashCount)) : SplashHintLine(rest);
    }

    /// <summary>A first Ctrl+C is still fresh: the next one exits.</summary>
    private bool ExitArmed() => _time.GetUtcNow().UtcTicks < Volatile.Read(ref _exitArmedUntil);

    /// <summary>Forgets a first Ctrl+C (a line sent, a turn started): the next one is a first again.</summary>
    private void DisarmExit() => Volatile.Write(ref _exitArmedUntil, 0);

    // ── The info pane (/help) ───────────────────────────────────────────────

    /// <summary>
    /// The Keys tab: the keys with what each does — the user's wording (2026-09-16), pinned. The push-to-talk
    /// key and the wake phrase appear only while they apply, after <c>PgUp / PgDn</c> and before <c>Ctrl+Home</c>.
    /// The Mouse, Drag, Drop, <c>@</c>, <c>#</c> and <c>$</c> rows went and the Ctrl+Home / Ctrl+End rows came
    /// later on 2026-09-20, the user's list; six rows reworded shorter the same day, the user's words.
    /// Ctrl+Enter after Enter (2026-09-22): a line break in the draft (<see cref="Keys.IsLineBreak"/>).
    /// Ctrl+O after Ctrl+End (later that day): the tool runs unfolded or folded (<see cref="Keys.IsToolToggle"/>).
    /// </summary>
    public static (string Key, string Meaning)[] KeyRows(bool voiceOn, ConsoleKey pushToTalk, bool wakeReady, string wakePhrase)
    {
        var rows = new List<(string, string)>
        {
            ("Enter", "send the line · change/update a setting"),
            ("Ctrl+Enter", "new line in the message"),
            ("ESC", "stop the speech · clear the line · cancel the reply · back out of a menu"),
            ("Up / Down", "earlier lines · the draft's rows when it wraps · scroll in menus"),
            ("Left / Right", "change tabs in menus · hold Shift to select text"),
            ("Home / End", "hold Shift to select text to the beginning or end of the line starting from the cursor"),
            ("PgUp / PgDn", "scroll the transcript a page at a time"),
        };
        if (voiceOn)
        {
            rows.Add((pushToTalk.ToString(), "talk (push-to-talk key)"));
        }

        if (wakeReady)
        {
            rows.Add(($"say \"{wakePhrase}\"", "talk without a key; during a spoken reply, cut it short (/interrupt)"));
        }

        rows.Add(("Ctrl+Home", "scroll to top of the chat pane"));
        rows.Add(("Ctrl+End", "scroll to bottom of the chat pane"));
        rows.Add(("Ctrl+O", "expand or collapse the tool calls, code blocks and thinking (or click a summary line)"));
        rows.Add(("Alt+V", "paste content (text or images)"));
        rows.Add(("Ctrl+A", "select all text on the line"));
        rows.Add(("Ctrl+X", "cut the selected text"));
        rows.Add(("Ctrl+C", "copy the selected text · stop the speech · cancel the reply · twice to exit"));
        return rows.ToArray();
    }

    /// <summary>The tabs <c>/help</c> opens; each builds its content when shown, from the live state.</summary>
    private IReadOnlyList<InfoTab> HelpTabs() =>
    [
        new("Commands", () => CommandsTab(log: _logFile is not null)),
        new("Keys", KeysTab),
    ];

    /// <summary>
    /// The Commands tab: <see cref="SlashCommands.HelpGroups"/> as two columns, a blank row between the groups
    /// (<see cref="SlashCommands.HelpGroupsWithLog"/> under <paramref name="log"/>, the app started with <c>--log</c>, 2026-09-22).
    /// One grid for every group, so the label column is measured once across them all.
    /// </summary>
    public static IRenderable CommandsTab(bool log = false)
    {
        var groups = SlashCommands.HelpGroupsFor(log);
        var grid = TwoColumns();
        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0)
            {
                // A one-space cell: an empty one renders no line and the row would collapse.
                grid.AddRow(new Text(" "), Text.Empty);
            }

            foreach (var entry in groups[i])
            {
                grid.AddRow(new Text(entry.Label, Theme.AccentSecondary), new Text(entry.Summary, Theme.Body));
            }
        }

        return grid;
    }

    private IRenderable KeysTab()
    {
        var grid = TwoColumns();
        foreach (var (key, meaning) in KeyRows(_voice.Enabled, _voice.PushToTalk, _voice.WakeReady, _voice.WakePhrase))
        {
            grid.AddRow(new Text(key, Theme.AccentSecondary), new Text(meaning, Theme.Body));
        }

        return grid;
    }

    // Text cells, never Markup: a summary may hold brackets ("/timer <duration> [name]").
    internal static Grid TwoColumns() =>
        new Grid().AddColumn(new GridColumn().NoWrap().PadRight(SlashCommands.HelpColumnGap)).AddColumn(new GridColumn().PadRight(0));

    /// <summary>The tabs <c>/sys</c> opens: the system message the next turn sends, and the tools it offers; both from the live state.</summary>
    private IReadOnlyList<InfoTab> SysPromptTabs() =>
    [
        new(SystemPromptSummary.PromptTabTitle, () => SystemPromptSummary.PromptTab(SystemPromptFacts())),
        new(SystemPromptSummary.ToolsTabTitle, () => SystemPromptSummary.ToolsTab(ToolGroups())),
    ];

    /// <summary>The tabs <c>/usage</c> opens: the tally as it stands when shown, and the notes on how it is measured.</summary>
    private IReadOnlyList<InfoTab> UsageTabs() =>
    [
        new(UsageText.TokensTabTitle, () => UsageText.TokensTab(_session.Usage, _session.ContextLength)),
        new(UsageText.NotesTabTitle, UsageText.NotesTab),
    ];

    /// <summary>
    /// <c>/usage</c>: the pane on <see cref="UsageTabs"/>, or — with no pane to open (a redirected
    /// console) — the tally in the transcript. The typed command's path and, since 2026-09-21, the
    /// double-click's on the hint row's tally (<see cref="ScreenPane.HintZone.Usage"/>).
    /// </summary>
    private async Task ShowUsageAsync(CancellationToken cancellationToken)
    {
        if (_pane.Enabled)
        {
            await _info.ShowAsync(UsageText.Label, UsageTabs(), 0, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var line in UsageText.Lines(_session.Usage, _session.ContextLength))
        {
            _transcript.Notice(line);
        }
    }

    /// <summary>The tabs <c>/about</c> opens: the app and its folders (read when shown: a profile switch moves one), the third-party parts, the licence.</summary>
    private IReadOnlyList<InfoTab> AboutTabs() =>
    [
        new(AboutText.AboutTabTitle, () => AboutText.AboutTab(AboutFacts())),
        new(AboutText.ComponentsTabTitle, AboutText.ComponentsTab),
        new(AboutText.LicenseTabTitle, AboutText.LicenseTab),
    ];

    /// <summary>This process and the loaded profile's folders, as <c>/about</c> shows them.</summary>
    private AboutFacts AboutFacts() =>
        App.AboutFacts.Runtime(SidekickApp.Version, _settings.StorageDirectory, _settings.ProfileDirectory, _settings.ModelsDirectory);

    /// <summary>The skills as the next turn would see them (a rescan while the setting says so, like <see cref="SystemPromptFacts"/>), for <c>/skills</c> (<see cref="SkillsMenu"/>, which reads it again after a move, a delete or a flip). The notes are read whatever <c>Project file</c> says — the Project tab shows what is on disk beside the toggle.</summary>
    private SkillsFacts SkillsFacts()
    {
        var effective = _effective();
        var skills = Catalog(effective);
        return new SkillsFacts(effective.AgentSkills, effective.ProjectFile, skills, effective.AgentSkills ? _catalog.Shadowed : [], effective.AgentSkills ? _catalog.Problems : [], _catalog.Roots, effective.AgentSkills ? _project.ReadNotes() : null);
    }

    /// <summary>The refusal for <c>/learn</c> while the setting <c>Agent skills</c> is off (<c>/skill &lt;name&gt;</c>'s too, until later on 2026-09-18). Pinned.</summary>
    public const string SkillsOffError = "Agent skills is off (the Options tab of /skills).";

    /// <summary>The refusal for <c>/learn</c> while the setting <c>LLM offer tools</c> is off: nothing could carry the skill. Pinned.</summary>
    public const string SkillsNeedToolsError = "LLM offer tools is off (the LLM tab of /settings): a skill rides a tool result, so none can be loaded.";

    /// <summary>
    /// The line a withdrawn turn hands back (<see cref="TurnOutcome.Withdrawn"/>): put on the chat line's row at once,
    /// ahead of anything typed under the reply (2026-09-25; the next read opened with it before), and kept here while it
    /// is there — the queue waits until it is sent or cleared. The token form the history recalls, so a pasted block or a
    /// picture comes back as its token, not inlined.
    /// </summary>
    private string? _restoreDraft;

    /// <summary>
    /// The last line sent, as the history holds it (2026-09-25): what a withdrawn turn hands back. The history's last
    /// entry was that line until the row became a live editor under the reply — a line typed there is remembered at its
    /// Enter, ahead of the withdrawn one's return.
    /// </summary>
    private string? _sentDraft;

    /// <summary>
    /// What <c>/draft</c> left to send (2026-09-19): the saved text as a paste and its Enter
    /// (<see cref="DraftFile.Events"/>), replayed through the next idle read ahead of the console
    /// and the queue — exactly a typed line's path, so the token, the preview, the history and the
    /// expansion are the paste's — and then routed to the model whatever it reads (a draft is a
    /// message, never a command). Taken by the very next loop iteration, so nothing can drop it.
    /// </summary>
    private IReadOnlyList<InputEvent>? _draftReplay;

    /// <summary>
    /// The shape of the last turn that ran to its end (<see cref="TurnTrace"/>), what the
    /// skill-learning reflection judges and <c>/learn</c> needs; null before the first turn, after
    /// a cut or failed one, and once the conversation it belongs to is gone (<c>/new</c>, <c>/clear</c>,
    /// a profile switch, a compact).
    /// </summary>
    private TurnTrace? _lastTrace;

    /// <summary>
    /// The running tally of the turns since the last reflection (<see cref="TurnTrace.Absorb"/>),
    /// what the automatic trigger reads: a task of small turns adds up to one reflection, and a
    /// sliding window never re-fires on calls already reflected on (the watermark). Null = nothing
    /// since. Spent when a reflection starts or is queued, when a turn kept its own lesson, and
    /// where the conversation is gone (<c>/new</c>, <c>/clear</c>, a profile switch, the tools
    /// switch) — not on a compact, which changes the evidence's shape, not the count.
    /// </summary>
    private TurnTrace? _learnTrace;

    /// <summary>
    /// The reflections' notices waiting for a safe point: the turn task is the one transcript
    /// writer, so the pool posts here and <see cref="SignalAlert"/> nudges the idle read (the
    /// timer alerts' path): the outcome line and, when the model returned one, the summary line
    /// under it — every line here prints (the <c>Reflection verbose</c> switch went later still on
    /// 2026-09-19; a reflection's start prints nothing, the strip's brain says it).
    /// </summary>
    private readonly ConcurrentQueue<string> _learnNotices = new();

    /// <summary>What every reflection line opens with, inside its parentheses (2026-09-18): the brain (<see cref="LearnStripGlyph"/>, the strip's) and a space. U+1F9E0 is two cells wide, so no trailing-space fix as the gear needs. Pinned.</summary>
    public const string LearnGlyph = LearnStripGlyph + " ";

    /// <summary>What the hint row's queued part opens with (2026-09-18): the incoming envelope. U+1F4E8 is two cells wide, a surrogate pair with no variation selector. Pinned.</summary>
    public const string QueueGlyph = "📨";

    /// <summary>The hint row's queued part, after the speech strip at idle and after the spinner's label under a turn: <c>📨 2 queued</c>. Pinned.</summary>
    public static string QueuedHintPart(int count) => $"{QueueGlyph} {count.ToString(CultureInfo.InvariantCulture)} queued";

    /// <summary>The transcript's notice when the queue is dropped — a cancelled reply under <c>Queue cancel mode</c> <c>empty</c>, or a conversation forgotten. Pinned.</summary>
    public static string QueueDroppedNotice(int count) =>
        count == 1 ? "(" + NoticeGlyphs.Queue + "1 queued message dropped)" : $"({NoticeGlyphs.Queue}{count.ToString(CultureInfo.InvariantCulture)} queued messages dropped)";

    /// <summary>
    /// The watcher's click hook (2026-09-18), on the watcher task: two left clicks on the busy row's
    /// queued count within <see cref="DoubleClick.Interval"/> (like the idle row's) answer <see cref="SlashCommands.QueueWord"/>, which the line hook runs as the
    /// typed command — the Queue pane; two on the scroll's hint (<see cref="ScreenPane.HintZone.Scrolled"/>,
    /// later that day) are the bottom again, as Ctrl+End through <see cref="ScrollInput"/> — spent
    /// here, nothing answered; two on the spinner and its label (<see cref="ScreenPane.HintZone.Usage"/>,
    /// 2026-09-21) answer <see cref="SlashCommands.UsageWord"/> — the Usage pane under the reply;
    /// two on a toolbar glyph (later on 2026-09-21) answer its <see cref="ToolbarWord"/> — the
    /// pane under the reply, as the typed command's — while the path is inert there (<c>/cwd</c>
    /// is refused mid-turn, and a click deserves no refusal notice); two on the strip's brain
    /// (2026-09-24) cancel the running reflection, nothing answered — the other strip glyphs stay inert here.
    /// Any other click ends a pair. Every watcher passes it (a reply, a
    /// compact, a recording): a word answered without a line hook is dropped.
    /// </summary>
    private string? HintClickLine(InputEvent.Click click)
    {
        if (click.Button == MouseButton.Left)
        {
            if (_pane.TryToggleToolGroupAt(click.X, click.Y))
            {
                // A tool run's summary (2026-09-22): one click unfolds or folds it, nothing answered.
                _queuedClicks.Reset();
                return null;
            }

            if (_pane.PictureAt(click.X, click.Y) is int picture)
            {
                // A picture in the transcript (later on 2026-09-24): a double-click opens it in the image editor.
                if (_queuedClicks.Second(InputLine.PicturePairKey(picture)))
                {
                    OpenPicture(picture);
                }

                return null;
            }

            if (_pane.TryHitQueued(click.X, click.Y))
            {
                return _queuedClicks.Second(0) ? SlashCommands.QueueWord : null;
            }

            if (_pane.TryHitToolbar(click.X, click.Y, out var tool))
            {
                // The path (the folder picker) is inert here: a glyph's word answers, and the
                // blanks' /settings (later on 2026-09-21, a pane under the reply as at idle).
                string? word = tool.Zone switch
                {
                    ScreenPane.ToolbarZone.Glyph => ToolbarWord(tool.Glyph),
                    ScreenPane.ToolbarZone.Row => SlashCommands.SettingsWord,
                    _ => null,
                };
                if (word is not null)
                {
                    return _queuedClicks.Second(InputLine.ToolbarPairKey(tool)) ? word : null;
                }

                _queuedClicks.Reset();
                return null;
            }

            if (_pane.TryHitHint(click.X, click.Y, out var hit) && hit.Zone == ScreenPane.HintZone.Scrolled)
            {
                if (_queuedClicks.Second(1))
                {
                    _pane.ScrollToEnd();
                }

                return null;
            }

            if (hit.Zone == ScreenPane.HintZone.Usage)
            {
                return _queuedClicks.Second(2) ? SlashCommands.UsageWord : null;
            }

            if (hit.Zone == ScreenPane.HintZone.Strip && hit.Glyph == LearnStripGlyph)
            {
                // The brain on the busy row (2026-09-24, the user's ask: a reflection sharing the server
                // can hold the reply up): the pair cancels it, as at idle; the line waits in the
                // reflections' queue for the turn's end, since the turn task is the one transcript writer.
                if (_queuedClicks.Second(InputLine.HintPairKey(hit)))
                {
                    ForgetPausedLearn();
                    _session.CancelLearning();
                    _learnNotices.Enqueue(LearnCancelledNotice);
                }

                return null;
            }
        }

        _queuedClicks.Reset();
        return null;
    }

    /// <summary>
    /// A cancelled reply's effect on the queue (2026-09-18), on the turn task: <c>hold</c> sets the
    /// hold the next normal end releases (inert with nothing queued), <c>drain</c> leaves the loop top
    /// to send the next one, <c>empty</c> drops them all with the notice.
    /// </summary>
    private void ApplyQueueCancelMode()
    {
        var mode = QueueCancelMode.Resolve(_effective());
        if (_queue.Count > 0)
        {
            DiagnosticLog.Debug(AppCategory, QueueCancelLogLine(mode, _queue.Count));
        }

        switch (mode)
        {
            case QueueCancel.Hold:
                _queue.Held = true;
                break;
            case QueueCancel.Empty:
                DropQueue();
                break;
        }
    }

    /// <summary><c>Queue cancel mode hold applied after a cancelled reply (2 waiting)</c> — only with something queued. Pinned.</summary>
    public static string QueueCancelLogLine(QueueCancel mode, int waiting) =>
        string.Create(CultureInfo.InvariantCulture, $"Queue cancel mode {QueueCancelMode.Name(mode)} applied after a cancelled reply ({waiting} waiting)");

    /// <summary>Every queued message dropped with <see cref="QueueDroppedNotice"/> — nothing said when there was none. Every conversation clear calls it: a forgotten conversation never receives stale questions.</summary>
    private void DropQueue()
    {
        if (_queue.Clear() is > 0 and var dropped)
        {
            _transcript.Notice(QueueDroppedNotice(dropped));
        }
    }

    /// <summary>The <c>/queue</c> grammar, pure (2026-09-21): nothing ⇒ the pane; <c>clear</c> (ignoring case) ⇒ drop every one; anything else ⇒ invalid.</summary>
    public static QueueAction ParseQueueArgs(string args)
    {
        string text = (args ?? "").Trim();
        return text.Length == 0 ? QueueAction.List
            : text.Equals(QueueClearWord, StringComparison.OrdinalIgnoreCase) ? QueueAction.Clear
            : QueueAction.Invalid;
    }

    /// <summary>
    /// <c>/expand</c> or <c>/collapse</c> (2026-09-22, the user's ask: <c>/tools expand|collapse</c>
    /// until later that day), idle or as a quick act under a reply: every tool run and code block in
    /// the transcript unfolded or folded at once, the ones to come following (Ctrl+O flips the same
    /// state), and a notice saying so.
    /// </summary>
    private void SetFolds(bool expanded)
    {
        _pane.SetToolGroupsExpanded(expanded);
        _transcript.Notice(ToolGroupText.ExpandedNotice(expanded));
    }

    /// <summary>
    /// The <c>/memory</c> grammar, pure (2026-09-22): nothing ⇒ the pane; <c>forget</c> ⇒ the wipe,
    /// after its confirmation; <c>copy &lt;profile&gt;</c>, and <c>overwrite</c> after it, ⇒ the copy
    /// into that profile, after its own (later that day, the user's ask: what <c>/memcopy</c> did);
    /// <c>edit</c> ⇒ <c>memory.json</c> in the editor (2026-09-23). Every word folds case, and the profile is passed on as typed — <see cref="Profiles.Resolve"/>
    /// is the one that judges a name. Anything else ⇒ invalid.
    /// </summary>
    public static MemoryAction ParseMemoryArgs(string args)
    {
        string[] words = (args ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return new(MemoryActionKind.List);
        }

        if (words.Length == 1 && words[0].Equals(MemoryForgetWord, StringComparison.OrdinalIgnoreCase))
        {
            return new(MemoryActionKind.Forget);
        }

        if (words.Length == 1 && words[0].Equals(MemoryEditWord, StringComparison.OrdinalIgnoreCase))
        {
            return new(MemoryActionKind.Edit);
        }

        if (words[0].Equals(CopyWord, StringComparison.OrdinalIgnoreCase) && words.Length is 2 or 3)
        {
            bool overwrite = words.Length == 3 && words[2].Equals(OverwriteWord, StringComparison.OrdinalIgnoreCase);
            if (words.Length == 2 || overwrite)
            {
                return new(MemoryActionKind.Copy, words[1], overwrite);
            }
        }

        return new(MemoryActionKind.Invalid);
    }

    /// <summary>
    /// <c>/queue</c> at the idle line: the pane, or with <c>clear</c> the drop and its notice —
    /// <see cref="QueueMenu.EmptyNotice"/> when there was nothing to drop, as the pane says it —, or
    /// <see cref="QueueUsageError"/>. The pane's clear-all button is the same drop from inside the pane.
    /// </summary>
    private async Task HandleQueueAsync(string args, CancellationToken cancellationToken)
    {
        switch (ParseQueueArgs(args))
        {
            case QueueAction.List:
                await _queueMenu.ShowAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                HandleQueueArgs(args);
                break;
        }
    }

    /// <summary>The half of <see cref="HandleQueueAsync"/> that needs no pane: the mid-turn quick act for <c>/queue clear</c> (2026-09-21), on the turn task.</summary>
    private void HandleQueueArgs(string args)
    {
        switch (ParseQueueArgs(args))
        {
            case QueueAction.Clear:
                if (_queue.Count == 0)
                {
                    _transcript.Notice(QueueMenu.EmptyNotice);
                }
                else
                {
                    DropQueue();
                }

                break;
            case QueueAction.Invalid:
                _transcript.Error(QueueUsageError);
                break;
        }
    }

    /// <summary><c>(🧠 learned: created skill 'x' (profile, 1,234 bytes))</c> — the editor's own words, without the "from the next reply on" clause. Pinned.</summary>
    public static string LearnedNotice(SkillEditResult edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        string sentence = SkillText.Edited(edit);
        int clause = sentence.IndexOf("; it is in the list", StringComparison.Ordinal);
        return "(" + LearnGlyph + "learned: " + (clause < 0 ? sentence : sentence[..clause]) + ")";
    }

    /// <summary>
    /// <c>(🧠 summary: Added the retry after a 429 and the header the key goes in.)</c> — the
    /// model's own sentence on what it changed (the tool's <c>summary</c> argument, cleaned by
    /// <see cref="SkillText.CleanSummary"/>), under the learned line whenever the model gave one
    /// (2026-09-19, the user's ask; behind <c>Reflection verbose</c> for an automatic reflection until
    /// later still that day). Pinned.
    /// </summary>
    public static string LearnSummaryNotice(string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        return "(" + LearnGlyph + "summary: " + summary + ")";
    }

    /// <summary>When the model was still calling tools at the reflection's cap. Pinned.</summary>
    public const string LearnExhaustedNotice = "(" + LearnGlyph + "learning stopped: the model kept calling tools without writing a skill)";

    /// <summary>Ahead of the explanation when the reflection's request failed. Pinned.</summary>
    public const string LearnFailedPrefix = "(" + LearnGlyph + "learning failed: ";

    /// <summary>Under the reply when a reflection is queued behind the one running (one slot; <see cref="_pendingLearn"/>). Pinned.</summary>
    public const string LearnQueuedNotice = "(" + LearnGlyph + "learning from this turn follows the one running)";

    /// <summary>After a double-click on the strip's brain (2026-09-18): the running reflection cancelled, the one waiting in the slot untouched. Pinned.</summary>
    public const string LearnCancelledNotice = "(" + LearnGlyph + "learning cancelled)";

    /// <summary>The refusal for <c>/learn</c> with no turn to learn from. Pinned.</summary>
    public const string LearnNoTurnError = "Nothing to learn from yet; send a message first.";

    /// <summary>The word after <c>/learn</c> that makes a pass over the stored sessions (2026-09-19). Pinned.</summary>
    public const string LearnSessionsWord = "sessions";

    /// <summary>How many of the newest stored sessions a bare <c>/learn sessions</c> reads. Pinned.</summary>
    public const int LearnSessionsDefault = 5;

    /// <summary>The most sessions one pass may read (<c>/learn sessions N</c>). Pinned.</summary>
    public const int MaxLearnSessions = 20;

    /// <summary>The <c>sessions</c> row of <c>/learn</c>'s completion list. Pinned.</summary>
    public const string LearnSessionsNote = "learn from the last 5 stored sessions, or N, or the text to search for";

    /// <summary>The refusal for <c>/learn sessions</c> while nothing is written to the store. Pinned.</summary>
    public const string LearnSessionsOffError = "Session logging is off (the Sessions tab of /settings); /learn sessions reads the store.";

    /// <summary>The refusal for <c>/learn sessions</c> with nothing stored, or nothing matching. Pinned.</summary>
    public const string LearnNoSessionsError = "No stored session to learn from yet.";

    /// <summary>The refusal for a count past the range. Pinned.</summary>
    public const string LearnSessionsCountError = "/learn sessions takes a count from 1 to 20 or the text to search for.";

    /// <summary>Under the line when a pass is queued behind the reflection running. Pinned.</summary>
    public const string LearnSessionsQueuedNotice = "(" + LearnGlyph + "learning from the sessions follows the one running)";

    /// <summary><c>No reflection: cooling down (docker-deploy updated 12 minutes ago; 18 minutes to go).</c> — the Debug line when the cooldown skips one; <c>; the turns loaded it</c> before the close under <c>last-written-skill</c>. Pinned.</summary>
    public static string CooldownLogLine(ReflectionMark mark, TimeSpan age, TimeSpan remaining, bool loaded = false)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return string.Create(CultureInfo.InvariantCulture, $"No reflection: cooling down ({mark.Skill} {mark.Action} {Math.Max(0, (int)age.TotalMinutes)} minutes ago; {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} minutes to go{(loaded ? "; the turns loaded it" : "")}).");
    }

    /// <summary>What <c>/learn</c>'s argument asked for (<see cref="ParseLearnArgs"/>).</summary>
    public enum LearnKind
    {
        /// <summary>The turn reflection, the text (if any) its focus.</summary>
        Turn,

        /// <summary>A pass over the newest <c>Count</c> stored sessions, or the sessions matching <c>Query</c>.</summary>
        Sessions,

        /// <summary><c>/learn sessions 0</c>, <c>/learn sessions 99</c>: a number past the range.</summary>
        BadCount,
    }

    /// <summary>The parsed <c>/learn</c> argument: the kind, the note for a turn, the count or the query for a pass.</summary>
    public sealed record LearnAction(LearnKind Kind, string? Note = null, int Count = 0, string? Query = null);

    /// <summary>
    /// <c>/learn</c>'s grammar (2026-09-19): nothing = the turn reflection; <c>sessions</c> alone = the
    /// newest <see cref="LearnSessionsDefault"/> stored sessions; <c>sessions N</c> (1 to <see cref="MaxLearnSessions"/>)
    /// = that many; <c>sessions</c> and any other text = the sessions matching it; anything else = the
    /// turn reflection with the text as its focus (so a note may not open with the word <c>sessions</c>). Pure.
    /// </summary>
    public static LearnAction ParseLearnArgs(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string text = args.Trim();
        if (text.Length == 0)
        {
            return new LearnAction(LearnKind.Turn);
        }

        int space = text.IndexOf(' ');
        string head = space < 0 ? text : text[..space];
        if (!string.Equals(head, LearnSessionsWord, StringComparison.OrdinalIgnoreCase))
        {
            return new LearnAction(LearnKind.Turn, text);
        }

        string rest = space < 0 ? "" : text[(space + 1)..].Trim();
        if (rest.Length == 0)
        {
            return new LearnAction(LearnKind.Sessions, Count: LearnSessionsDefault);
        }

        if (rest.All(char.IsAsciiDigit))
        {
            return int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count >= 1 && count <= MaxLearnSessions
                ? new LearnAction(LearnKind.Sessions, Count: count)
                : new LearnAction(LearnKind.BadCount);
        }

        return new LearnAction(LearnKind.Sessions, Query: rest);
    }

    /// <summary>The notice for a reflection's result, or null for nothing to say (nothing to keep, cancelled). Pinned.</summary>
    public static string? LearnNotice(SkillLearnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            SkillLearnOutcome.Learned when result.Edit is { } edit => LearnedNotice(edit),
            SkillLearnOutcome.Exhausted => LearnExhaustedNotice,
            SkillLearnOutcome.Failed => LearnFailedPrefix + result.Detail + ")",
            _ => null,
        };
    }

    /// <summary>
    /// Everything a reflection is started with, captured when it is decided (<see cref="MaybeLearn"/>):
    /// the last turn's messages as a copy, the focus, whether <c>/learn</c> asked for it, the roots
    /// of that moment (a profile switch later still writes the old profile, the rule a running
    /// reflection follows too), the external flag, the level, the request cap. The one pending slot holds one of these
    /// while a reflection runs (<see cref="_pendingLearn"/>).
    /// </summary>
    private sealed record PendingLearn(ReflectionMaterial Material, bool Forced, SkillRoots Roots, bool External, ReasoningEffort Effort, int MaxRequests, CancellationToken Token, SessionEvidence? Sessions, SessionStore? Store, long? SessionId, int TurnOrdinal)
    {
        /// <summary>The queued line: the turn's or the pass's — the one progress line (a start prints nothing since later still on 2026-09-19).</summary>
        public string QueuedNotice => Material is ReflectionMaterial.Sessions ? LearnSessionsQueuedNotice : LearnQueuedNotice;
    }

    /// <summary>The <c>reflections</c> row for a finished reflection (nothing for a cancelled one): the outcome word, the skill and the action when it wrote one, the cost.</summary>
    public static ReflectionRow? ReflectionRowFor(long? sessionId, int turnOrdinal, bool forced, SkillLearnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string outcome = result.Outcome switch
        {
            SkillLearnOutcome.Learned => ReflectionRow.Learned,
            SkillLearnOutcome.Nothing => ReflectionRow.NothingOutcome,
            SkillLearnOutcome.Exhausted => ReflectionRow.Exhausted,
            SkillLearnOutcome.Failed => ReflectionRow.Failed,
            _ => "",
        };
        if (outcome.Length == 0)
        {
            return null;
        }

        string skill = result.Edit?.Name ?? "";
        string action = result.Edit?.Outcome switch
        {
            SkillEditOutcome.Created => ReflectionRow.Created,
            SkillEditOutcome.Updated => ReflectionRow.Updated,
            _ => "",
        };
        return new ReflectionRow(sessionId, turnOrdinal, forced, outcome, skill, action, result.Requests, result.Usage.Input, result.Usage.Output);
    }

    /// <summary>
    /// The reflection waiting for the running one to finish: one slot, the newest automatic one
    /// wins, a <c>/learn</c> is never displaced by an automatic one and displaces anything. Started
    /// by <see cref="DrainLearn"/> at the next safe point; cleared only by that or the exit.
    /// </summary>
    private PendingLearn? _pendingLearn;

    /// <summary>
    /// The one decider of a skill-learning reflection (<see cref="SkillLearner"/>), on the turn task
    /// after a turn (<paramref name="forced"/> false: the switch and the trigger decide) or on
    /// <c>/learn</c> (<paramref name="forced"/> true: the trigger is skipped, the switch has no say).
    /// Needs <c>Agent skills</c>, <c>LLM offer tools</c> and a last turn. The snapshot (the last turn's
    /// messages, the roots, the level) is taken here, before the next turn can append; with a
    /// reflection already running it goes into <see cref="_pendingLearn"/> (<see cref="LearnQueuedNotice"/>),
    /// else <see cref="StartLearn"/> runs it now.
    /// </summary>
    private void MaybeLearn(string? focus, bool forced, CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (!effective.AgentSkills || !effective.LlmOfferTools || _lastTrace is not { } trace || _session.Assistant is not { } assistant)
        {
            return;
        }

        // The automatic trigger reads the tally since the last reflection, not the last turn alone.
        var tally = _learnTrace ?? trace;
        int minCalls = ReflectionMinToolCalls.Resolve(effective);
        if (!forced && (!effective.ReflectionAutoLearn || !SkillLearner.ShouldLearn(tally, minCalls)))
        {
            DiagnosticLog.Debug(SkillCatalog.Category, "No reflection: " + tally + (effective.ReflectionAutoLearn ? " (below " + minCalls.ToString(CultureInfo.InvariantCulture) + " calls, no recovered error)." : "; Skills auto learn is off."));
            return;
        }

        // The cooldown (2026-09-19): a skill written a moment ago by a reflection holds the next
        // automatic one back; the tally stands, so the next qualifying turn past it fires.
        if (!forced && CoolingDown(effective, tally, out string cooling))
        {
            DiagnosticLog.Debug(SkillCatalog.Category, cooling);
            return;
        }

        var turn = assistant.History.LastTurns(ReflectionWindow.Resolve(effective));
        if (turn.Count == 0)
        {
            if (forced)
            {
                _transcript.Error(LearnNoTurnError);
            }

            return;
        }

        // The evidence (2026-09-19): the earlier sessions found for the turn's user line, read here
        // on the turn task, and the store itself for the tool and the catalog's usage lines.
        var evidence = SessionEvidenceFor(effective);
        var (query, result) = evidence is null ? (null, null) : SkillLearner.Evidence(evidence, LastUserLine(turn));
        var material = new ReflectionMaterial.Turn(turn, focus, query, result);
        var pending = new PendingLearn(material, forced, _catalog.Roots, effective.ExternalSkills, ReflectionReasoning.Resolve(effective), ReflectionMaxRequests.Resolve(effective), cancellationToken,
            evidence, effective.SessionLogging ? _sessions : null, _sessionId, _sessionId is { } sessionId ? _sessions.Summary(sessionId)?.Turns ?? 0 : 0);
        string log = tally + (result is null ? "" : "; with the earlier sessions found for the turn");
        if (QueueOrStart(pending, log))
        {
            _learnTrace = null;
        }
    }

    /// <summary>The user's line the last turn of <paramref name="turn"/> opened with — the words the evidence search reads.</summary>
    private static string LastUserLine(IReadOnlyList<ChatMessage> turn)
    {
        for (int i = turn.Count - 1; i >= 0; i--)
        {
            if (ConversationHistory.IsTurnStart(turn[i]))
            {
                return turn[i].Text;
            }
        }

        return "";
    }

    /// <summary>
    /// The store's side of a reflection when <c>Reflection includes sessions</c> and <c>Session logging</c>
    /// are on and the store opens; null otherwise — the reflection then reads the conversation alone.
    /// </summary>
    private SessionEvidence? SessionEvidenceFor(AppSettingsData effective) =>
        effective.SessionLogging && effective.ReflectionIncludesSessions && _sessions.Available
            ? new SessionEvidence(_sessions, _effective, _sessionId, _time)
            : null;

    /// <summary>
    /// Whether the newest reflection that wrote a skill is younger than <c>Reflection cooldown (minutes)</c>
    /// and <c>Reflection cooldown mode</c> says that holds this one back: <c>all-skills</c> always,
    /// <c>last-written-skill</c> only when <paramref name="tally"/> (the turns since the last
    /// reflection) loaded that skill. Nothing without the store (<c>Session logging</c> off) or at 0.
    /// </summary>
    private bool CoolingDown(AppSettingsData effective, TurnTrace tally, out string detail)
    {
        detail = "";
        var cooldown = ReflectionCooldown.Resolve(effective);
        if (!effective.SessionLogging || cooldown <= TimeSpan.Zero || _sessions.LastReflectionWrite() is not { } mark)
        {
            return false;
        }

        var age = _time.GetUtcNow() - mark.At;
        if (age >= cooldown)
        {
            return false;
        }

        bool scoped = ReflectionCooldownMode.Resolve(effective) == ReflectionCooldownScope.LastWrittenSkill;
        if (scoped && !tally.LoadedSkills.Contains(mark.Skill, StringComparer.Ordinal))
        {
            return false;
        }

        detail = CooldownLogLine(mark, age, cooldown - age, scoped);
        return true;
    }

    /// <summary>
    /// The slot logic behind every reflection (a turn's or a pass's), on the turn task: started now
    /// when nothing runs, else queued in the one slot — the user's own ask always takes it, an
    /// automatic turn never takes it from a <c>/learn</c> and takes it from an earlier automatic
    /// one (the newer lesson). <paramref name="log"/> names what is reflected on, for the Info lines.
    /// False when the slot refused it (a <c>/learn</c> waiting there): the caller keeps its tally.
    /// </summary>
    private bool QueueOrStart(PendingLearn pending, string log)
    {
        // A paused one takes its place in the slot first, so a start here never loses its outcome.
        ResumePausedLearn();
        bool forced = pending.Forced;
        bool running = _session.IsLearning;
        if (!running && _pendingLearn is null && !LearnWaitsForQueue)
        {
            DiagnosticLog.Info(SkillCatalog.Category, (forced ? "Reflection (/learn): " : "Reflection: ") + log + ".");
            StartLearn(pending);
            return true;
        }

        if (!forced && _pendingLearn is { Forced: true })
        {
            DiagnosticLog.Info(SkillCatalog.Category, "No reflection: a /learn is already waiting in the slot (" + log + ").");
            return false;
        }

        string where = running ? " behind the running one (" : " for the queued messages (";
        DiagnosticLog.Info(SkillCatalog.Category, (forced ? "Reflection (/learn) queued" : "Reflection queued") + where + log + ")" + (_pendingLearn is null ? "." : "; the earlier waiting turn is displaced."));
        _pendingLearn = pending;
        if (running)
        {
            // Waiting on the queue says nothing (2026-09-24): the brain comes up once the messages are answered.
            _transcript.Notice(pending.QueuedNotice);
        }

        // The running one may have ended between the check above and now, its nudge already
        // spent: start from the slot here rather than at the next line.
        DrainLearn();
        return true;
    }

    /// <summary>
    /// The reflection now running, as it was started (<see cref="StartLearn"/>): what a turn's start
    /// puts aside when it pauses it (<see cref="YieldLearn"/>). Stale once it ends; read only while
    /// <see cref="LlmSession.IsLearning"/>. The turn task's.
    /// </summary>
    private PendingLearn? _runningLearn;

    /// <summary>
    /// The reflection a turn's start paused (<c>Reflection yields to turns</c>, 2026-09-24, the user's
    /// ask: on a one-slot server a reflection holds the reply up), waiting for its job to answer so
    /// <see cref="ResumePausedLearn"/> can put it back in the slot. Set on the turn task; a double-click
    /// on the brain clears it from the watcher task too (a cancel the user asked for is final), hence
    /// <see cref="Interlocked.Exchange{T}(ref T, T)"/> on every take.
    /// </summary>
    private PendingLearn? _pausedLearn;

    /// <summary>The Info line when a turn's start pauses the running reflection. Pinned.</summary>
    public const string LearnPausedLogLine = "Reflection paused for the turn; it runs again after the reply.";

    /// <summary>The Info line when the paused reflection goes back in the slot. Pinned.</summary>
    public const string LearnResumedLogLine = "The paused reflection waits in the slot to run again.";

    /// <summary>The Info line when the paused reflection had already finished as the pause landed, so it never runs again. Pinned.</summary>
    public const string LearnPauseMissedLogLine = "The paused reflection had already finished; it does not run again.";

    /// <summary>The Info line when a newer reflection holds the slot the paused one would go back to. Pinned.</summary>
    public const string LearnPauseDisplacedLogLine = "The paused reflection does not run again: a newer one waits in the slot.";

    /// <summary>
    /// At a turn's start (2026-09-24): under <c>Reflection yields to turns</c> the running reflection
    /// is cancelled so the reply has the server, and its snapshot is kept to run again
    /// (<see cref="ResumePausedLearn"/>). Nothing printed — the brain leaving the strip says it.
    /// </summary>
    private void YieldLearn(AppSettingsData effective)
    {
        if (!effective.ReflectionYieldsToTurns || !_session.IsLearning || _runningLearn is not { } running)
        {
            return;
        }

        Volatile.Write(ref _pausedLearn, running);
        _session.CancelLearning();
        DiagnosticLog.Info(SkillCatalog.Category, LearnPausedLogLine);
    }

    /// <summary>
    /// The paused reflection back in the slot once its job has answered, on the turn task. Only a
    /// job that answered <see cref="SkillLearnOutcome.Cancelled"/> runs again: one that finished as
    /// the pause landed wrote its skill (or found nothing) already. The slot's rules hold, and the
    /// paused one is the older: it takes the slot only as a <c>/learn</c> over an automatic one.
    /// </summary>
    private void ResumePausedLearn()
    {
        if (_session.IsLearning || Volatile.Read(ref _pausedLearn) is null || Interlocked.Exchange(ref _pausedLearn, null) is not { } paused)
        {
            return;
        }

        if (_session.Learning is not { IsCompletedSuccessfully: true } job || job.Result.Outcome != SkillLearnOutcome.Cancelled)
        {
            DiagnosticLog.Info(SkillCatalog.Category, LearnPauseMissedLogLine);
            return;
        }

        if (_pendingLearn is { } waiting && (waiting.Forced || !paused.Forced))
        {
            DiagnosticLog.Info(SkillCatalog.Category, LearnPauseDisplacedLogLine);
            return;
        }

        DiagnosticLog.Info(SkillCatalog.Category, LearnResumedLogLine);
        _pendingLearn = paused;
    }

    /// <summary>
    /// Whether the slot waits for the queue (2026-09-24): under <c>Reflection yields to turns</c> a
    /// reflection started with messages about to go would be paused by each of them in turn, so it
    /// starts once the loop top has sent the last (the same test as the loop's own take).
    /// </summary>
    private bool LearnWaitsForQueue =>
        _effective().ReflectionYieldsToTurns && _restoreDraft is null && !_queue.Held && _queue.Count > 0;

    /// <summary>A cancel the user asked for (the brain's double-click): the paused reflection, if any, never runs again. Safe from the watcher task.</summary>
    private void ForgetPausedLearn() => Interlocked.Exchange(ref _pausedLearn, null);

    /// <summary>
    /// The one starter: the job under <see cref="LlmSession.StartLearning"/> (nothing printed — the
    /// strip's brain shows it running) and the continuation on the pool that bills the usage and
    /// posts the notice into <see cref="_learnNotices"/> for <see cref="DrainLearn"/> — never a
    /// transcript write from there. Nothing when the session refused it (no assistant, or one
    /// running after all).
    /// </summary>
    private void StartLearn(PendingLearn pending)
    {
        var job = _session.StartLearning((a, token) => SkillLearner.RunAsync(a, pending.Material, pending.Roots, pending.External, pending.Effort, token, pending.MaxRequests, pending.Sessions), pending.Token);
        if (job is null)
        {
            return;
        }

        _runningLearn = pending;
        _ = job.ContinueWith(t =>
        {
            var result = t.Result;
            // The row first (the store captured with the job, never the field a profile switch rebinds).
            if (pending.Store is { } store && ReflectionRowFor(pending.SessionId, pending.TurnOrdinal, pending.Forced, result) is { } row)
            {
                store.RecordReflection(row);
            }

            if (LearnNotice(result) is { } notice)
            {
                _learnNotices.Enqueue(notice);
                // The model's own words on the change, whenever it gave them (2026-09-19).
                if (result.Edit is { Summary.Length: > 0 } edit)
                {
                    _learnNotices.Enqueue(LearnSummaryNotice(edit.Summary));
                }
            }

            // The notice first, the figures second: once /usage counts the request, the line is queued.
            _session.Usage.AddLearning(result.Usage, result.Requests);
            // With or without a notice: a waiting reflection starts at the idle line only once nudged.
            SignalAlert();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>
    /// <c>/learn [note]</c>: the reflection over the last turn whatever its shape, the note as its
    /// focus; <c>/learn sessions [N | text]</c> (2026-09-19): a pass over the stored sessions
    /// (<see cref="ParseLearnArgs"/>, <see cref="LearnFromSessions"/>). The same refusals as
    /// <c>/skills</c> for the skills or the tools off; with no turn to learn from, <see cref="LearnNoTurnError"/>.
    /// </summary>
    private void HandleLearn(string args, CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (!effective.AgentSkills)
        {
            _transcript.Error(SkillsOffError);
            return;
        }

        if (!effective.LlmOfferTools)
        {
            _transcript.Error(SkillsNeedToolsError);
            return;
        }

        var action = ParseLearnArgs(args);
        switch (action.Kind)
        {
            case LearnKind.BadCount:
                _transcript.Error(LearnSessionsCountError);
                return;

            case LearnKind.Sessions:
                LearnFromSessions(action, effective, cancellationToken);
                return;
        }

        if (_lastTrace is null)
        {
            _transcript.Error(LearnNoTurnError);
            return;
        }

        MaybeLearn(action.Note, forced: true, cancellationToken);
    }

    /// <summary>
    /// The pass (2026-09-19): the newest <c>Count</c> stored sessions, or those matching <c>Query</c>
    /// (<c>Session search max results</c> of them), the one on screen left out, each loaded whole on
    /// the turn task into a <see cref="ReflectionMaterial.Sessions"/>; <c>session_manager</c> and
    /// the usage lines ride along whatever <c>Reflection includes sessions</c> says (the store IS
    /// the material). Always forced: the cooldown never holds it. One write ends it.
    /// </summary>
    private void LearnFromSessions(LearnAction action, AppSettingsData effective, CancellationToken cancellationToken)
    {
        if (!effective.SessionLogging)
        {
            _transcript.Error(LearnSessionsOffError);
            return;
        }

        if (_session.Assistant is null)
        {
            _transcript.Error(NoAssistantError);
            return;
        }

        var ids = new List<long>();
        if (action.Query is { } query)
        {
            foreach (var hit in _sessions.Search(query, SessionManagerTool.DefaultCount(effective), _sessionId))
            {
                ids.Add(hit.Session.Id);
            }
        }
        else
        {
            foreach (var summary in _sessions.List(action.Count + 1))
            {
                if (summary.Id != _sessionId && ids.Count < action.Count)
                {
                    ids.Add(summary.Id);
                }
            }
        }

        var records = new List<SessionRecord>(ids.Count);
        foreach (long id in ids)
        {
            if (_sessions.Load(id) is { } record && record.Turns.Count > 0)
            {
                records.Add(record);
            }
        }

        if (records.Count == 0)
        {
            _transcript.Error(LearnNoSessionsError);
            return;
        }

        var material = new ReflectionMaterial.Sessions(records, action.Query);
        var evidence = new SessionEvidence(_sessions, _effective, _sessionId, _time);
        var pending = new PendingLearn(material, true, _catalog.Roots, effective.ExternalSkills, ReflectionReasoning.Resolve(effective), ReflectionMaxRequests.Resolve(effective), cancellationToken, evidence, _sessions, null, 0);
        QueueOrStart(pending, action.Query is null ? "the last " + SessionText.Sessions(records.Count) : SessionText.Sessions(records.Count) + " matching " + LogText.Quoted(action.Query));
    }

    /// <summary>
    /// The reflections' safe point, on the turn task at the loop top and at a turn's end (never
    /// mid-reply: a learned line is not an alert): the notices waiting as lines, then the pending
    /// reflection started when nothing runs any more.
    /// </summary>
    private void DrainLearn()
    {
        while (_learnNotices.TryDequeue(out var line))
        {
            _transcript.Notice(line);
        }

        ResumePausedLearn();
        _learnAwaitsQueue = LearnWaitsForQueue;
        if (_pendingLearn is { } next && !_session.IsLearning && !_learnAwaitsQueue)
        {
            _pendingLearn = null;
            DiagnosticLog.Info(SkillCatalog.Category, (next.Forced ? "Reflection (/learn)" : "Reflection") + " starts from the slot.");
            StartLearn(next);
        }
    }

    /// <summary>Whether a reflection's notice or a queued reflection waits for the loop top (the idle read's arm-time check).</summary>
    private bool LearnPending =>
        !_learnNotices.IsEmpty
        || (!_session.IsLearning && !_learnAwaitsQueue && (Volatile.Read(ref _pausedLearn) is not null || _pendingLearn is not null));

    /// <summary>
    /// <see cref="LearnWaitsForQueue"/> as the last <see cref="DrainLearn"/> found it (2026-09-24): the loop
    /// top drains before it takes the next queued message, so the read that replays the last one sees
    /// the queue empty — read live, <see cref="LearnPending"/> would cut that read short. The last
    /// queued turn's end drains again and starts the slot.
    /// </summary>
    private bool _learnAwaitsQueue;

    /// <summary>
    /// The state a turn would be prepared from right now, read as <see cref="RunTurnAsync"/> reads it
    /// before <see cref="PrepareTurn"/> — a read only: no speech turn begins, nothing is sent.
    /// </summary>
    private SystemPromptFacts SystemPromptFacts()
    {
        var effective = _effective();
        var clock = _clockTools.OfType<GetCurrentTimeTool>().FirstOrDefault();
        var cwd = _fileTools.OfType<GetWorkingDirectoryTool>().FirstOrDefault();
        var disabled = ToolsText.DisabledSet(effective.ToolsDisabled);
        return new SystemPromptFacts(
            _persona.Read(),
            _operata.Read(),
            _vocalia.Read(),
            effective.Memory,
            effective.Memory ? _memory.Snapshot() : [],
            effective.TtsOutput,
            _speech.IsReady,
            _session.History.TurnCount,
            clock?.Describe("") ?? "",
            ReasoningLevel.Resolve(effective),
            cwd?.Describe() ?? "",
            effective.LlmOfferTools,
            effective.FileTools && Without(FileToolsFor(_fileTools, effective.FileSafeEdits), disabled).Count > 0,   // the turn's own rule: every file tool off on /tools (restore gone with File safe edits off) reads as the switch off
            effective.AgentSkills,
            Catalog(effective),
            effective.AgentSkills && effective.ProjectFile ? _project.ReadNotes() : null,
            effective.TranscriptMarkdown,
            _pane.Enabled,
            disabled,
            effective.ProjectFile,
            effective.McpServers,
            _mcp.ServerTools.Count,
            Without(_mcp.Tools, disabled).Count,
            effective.FileSafeEdits,
            effective.GitNativeTools,
            Without(_gitTools, disabled).Count,
            ShellOffered(effective),
            Without(ShellToolsFor(_shellTools), disabled).Count,
            effective.ShellToolBridge,
            effective.ShellPoliceOutsidePaths,
            ObsidianOffered(effective),
            Without(ObsidianToolsFor(_vaultTools, effective), disabled).Count,
            effective.ObsidianAllowDelete,
            SqlOffered(effective, _sql),
            Without(_sqlTools, disabled).Count,
            effective.ShellPreferNative);
    }

    /// <summary>
    /// The input line's skill list (the <c>#</c>-mention's source): the catalog as name + description,
    /// in its order, rescanned like <see cref="SkillsFacts"/> so a skill just written counts — and
    /// nothing while the skills or the tools are off, the two refusals <c>/learn</c> makes
    /// (<see cref="SkillsOffError"/>, <see cref="SkillsNeedToolsError"/>).
    /// </summary>
    private IReadOnlyList<CompletionItem> SkillChoices()
    {
        var effective = _effective();
        if (!effective.AgentSkills || !effective.LlmOfferTools)
        {
            return [];
        }

        return Catalog(effective).Select(skill => new CompletionItem(skill.Name, skill.Description)).ToList();
    }

    /// <summary>
    /// The input line's <c>#</c>-mention list (2026-09-17): <see cref="SkillChoices"/> while
    /// <c>#-mention enabled</c> says so, else nothing — read at the keystroke, so the switch needs no
    /// reconnect. A pick is text (<c>#name</c>), nothing is seeded.
    /// </summary>
    private IReadOnlyList<CompletionItem> HashChoices() => _effective().SkillHashMention ? SkillChoices() : [];

    /// <summary>
    /// The input line's tool list (the <c>$</c>-mention's source, 2026-09-19): every tool the next turn
    /// would offer, as <see cref="PrepareTurn"/> decides it — <c>LLM offer tools</c> on, the group's switch
    /// on, not switched off on <c>/tools</c>, <c>download_file</c> only with a file tool left, <c>load_skill</c>
    /// only with a skill installed, <c>ask_user</c> only on the pane (<see cref="ToolGroup.Offers"/> folds
    /// every one of those in) — in the turn's order, name + description. Neither <see cref="ToolGroups"/>
    /// (no <c>skillInstalled</c>) nor <see cref="ToolsFacts"/> (the whole web list) is exactly that, hence
    /// its own build; the catalog rescan per keystroke is <see cref="SkillChoices"/>' precedent.
    /// </summary>
    private IReadOnlyList<CompletionItem> ToolChoices()
    {
        var effective = _effective();
        if (!effective.LlmOfferTools)
        {
            return [];
        }

        var disabled = ToolsText.DisabledSet(effective.ToolsDisabled);
        var fileTools = FileToolsFor(_fileTools, effective.FileSafeEdits);   // restore only with File safe edits on (later still on 2026-09-20)
        bool files = effective.FileTools && Without(fileTools, disabled).Count > 0;
        var groups = SystemPromptSummary.ToolGroups(_clockTools, _timerTools, fileTools, _memoryTools, effective.Memory, effective.LlmOfferTools, WebToolsFor(_webTools, files), effective.WebTools, effective.FileTools, _askTools, effective.AskUser, _pane.Enabled, _skillTools, effective.AgentSkills, _sessionTools, effective.SessionTool, disabled, skillInstalled: Catalog(effective).Count > 0, mcp: _mcp.ServerTools, mcpEnabled: effective.McpServers, git: _gitTools, gitEnabled: effective.GitNativeTools, shell: _shellTools, shellEnabled: ShellOffered(effective), codeAvailable: CodeAvailable(), obsidian: ObsidianToolsFor(_vaultTools, effective), obsidianEnabled: ObsidianOffered(effective), sql: _sqlTools, sqlEnabled: SqlOffered(effective, _sql), comfy: _comfyTools, comfyEnabled: ComfyOffered(effective, _comfy));
        return groups.SelectMany(g => g.Tools.Where(t => g.Offers(t.Name)).Select(t => new CompletionItem(t.Name, t.Description))).ToList();
    }

    /// <summary>
    /// The input line's <c>$</c>-mention list (2026-09-19): <see cref="ToolChoices"/> while
    /// <c>$-mention enabled</c> says so, else nothing — read at the keystroke, so the switch needs no
    /// reconnect. A pick is text (<c>$name</c>), nothing is seeded.
    /// </summary>
    private IReadOnlyList<CompletionItem> DollarChoices() => _effective().ToolsDollarMention ? ToolChoices() : [];

    /// <summary>
    /// The input line's <c>%</c>-mention list (later on 2026-09-23): <see cref="SqlChoices"/> while the setting
    /// <c>SQL %-mention enabled</c> and <c>SQL tools</c> are both on, else nothing — <c>%</c> is ordinary text then.
    /// </summary>
    private IReadOnlyList<CompletionItem> PercentChoices()
    {
        var effective = _effective();
        return effective.SqlPercentMention && effective.SqlTools ? SqlChoices(_sql.Catalog()) : [];
    }

    /// <summary>The SQL connections as mention items (later on 2026-09-23): each name with <see cref="SqlText.MentionNote"/>, in the catalog's order (the profile's first). Pure.</summary>
    public static IReadOnlyList<CompletionItem> SqlChoices(SqlCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Connections.Select(c => new CompletionItem(c.Name, SqlText.MentionNote(c))).ToList();
    }

    /// <summary>
    /// The input line's <c>^</c>-mention list (later still on 2026-09-24): the offered ComfyUI workflows (<see cref="ComfyChoices"/>)
    /// while the setting <c>ComfyUI ^-mention enabled</c> is on and the image tools are offered (<see cref="ComfyOffered"/>), else
    /// nothing — <c>^</c> is ordinary text then. A hidden workflow is left out: the model could not run it.
    /// </summary>
    private IReadOnlyList<CompletionItem> CaretChoices()
    {
        var effective = _effective();
        return effective.ComfyCaretMention && ComfyOffered(effective, _comfy) ? ComfyChoices(_comfy.OfferedWorkflows()) : [];
    }

    /// <summary>The workflows as mention items (later still on 2026-09-24): each name with <see cref="ComfyText.CompletionNote"/>, in the catalog's order. Pure.</summary>
    public static IReadOnlyList<CompletionItem> ComfyChoices(IReadOnlyList<ComfyWorkflow> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        return workflows.Select(w => new CompletionItem(w.Name, ComfyText.CompletionNote(w, offered: true))).ToList();
    }

    /// <summary>
    /// The input line's command list (2026-09-17): the base commands — less <c>/exit</c> under
    /// <c>Hide /exit autocomplete</c> (2026-09-18) and less <c>/queue</c> while <c>Queue messages</c>
    /// is off (later that day; a pane there would list nothing). Every switch is read at each call
    /// (every keystroke). The loaded skills sat in it as <c>/name</c> under <c>Skill slash commands</c>
    /// until later on 2026-09-18, when the switch went (the user's call: the <c>#</c>-mention covers it).
    /// </summary>
    private IReadOnlyList<CompletionItem> CommandChoices()
    {
        var effective = _effective();
        return CommandItems(effective.HideExitAutocomplete, hideQueue: !effective.QueueMessages, showLog: _logFile is not null);
    }

    /// <summary>
    /// <see cref="SlashCommands.Completions"/> (or <see cref="SlashCommands.CompletionsWithoutExit"/>
    /// under <paramref name="hideExit"/>; less <see cref="SlashCommands.QueueWord"/> under
    /// <paramref name="hideQueue"/>; the <c>…WithLog</c> lists, <c>/log</c> in them, under
    /// <paramref name="showLog"/> — the app started with <c>--log</c>, 2026-09-22). With no flag the base list itself comes back. Pure; pinned.
    /// </summary>
    public static IReadOnlyList<CompletionItem> CommandItems(bool hideExit = false, bool hideQueue = false, bool showLog = false)
    {
        var baseList = (hideExit, showLog) switch
        {
            (false, false) => SlashCommands.Completions,
            (true, false) => SlashCommands.CompletionsWithoutExit,
            (false, true) => SlashCommands.CompletionsWithLog,
            (true, true) => SlashCommands.CompletionsWithoutExitWithLog,
        };
        if (hideQueue)
        {
            baseList = baseList.Where(item => item.Text != SlashCommands.QueueWord).ToArray();
        }

        return baseList;
    }

    // ── The typo intercept (2026-09-18) ─────────────────────────────────────

    /// <summary>The typo pane's title over the one command it offers. Pinned.</summary>
    public static string TypoTitle(string command) => $"Did you mean {command}?";

    /// <summary>The typo pane's hint row. Pinned. (Ctrl+C backs out like ESC — the line is sent as typed.)</summary>
    public const string TypoKeys = "Enter = use the command · ESC = send as typed";

    /// <summary>The typo pane's one row: the command and, after <see cref="MentionCompleter.NoteGap"/> cells, its summary dim — the completion list's shape; the pane adds the pointer. Pinned.</summary>
    public static string TypoRow(CompletionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Markup.Escape(item.Text) + new string(' ', MentionCompleter.NoteGap) + Theme.DimMarkup(item.Note);
    }

    /// <summary>
    /// The command a typed line is a slashless copy of, or null (the setting <c>Command typo
    /// intercept</c>, 2026-09-18): the trimmed text is one word — no whitespace inside — that does
    /// not start with <c>/</c> and equals an item's text less its slash, the case ignored
    /// (<c>Clear</c> is the same typo; the command offered is the item's own spelling), the first
    /// hit. <paramref name="commands"/> is the <c>/</c> completion table (<see cref="CommandItems"/>);
    /// <c>//</c> is not in it, so a bare <c>/</c> never matches, and a loaded skill's name is text
    /// (it counted while <c>Skill slash commands</c> stood, until later on 2026-09-18). Pure; pinned.
    /// </summary>
    public static CompletionItem? TypoCommand(string text, IReadOnlyList<CompletionItem> commands)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(commands);
        string word = text.Trim();
        if (word.Length == 0 || word[0] == '/' || word.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return CommandNamed(word, commands);
    }

    /// <summary>
    /// The command a line typed with extra slashes means, and the line it becomes (later still on 2026-09-24, the user's
    /// ask: <c>//tools</c>, <c>///settings</c>): the trimmed text starts with two or more <c>/</c>, and the first word after
    /// them — up to the first whitespace — is an item's text less its slash, the case ignored, the first hit
    /// (<see cref="TypoCommand"/>'s match). The line is the item's own spelling, a space, then the rest of the line as
    /// typed (the user's call: <c>//profile work</c> → <c>/profile work</c>); a bare <c>/tools </c> when nothing follows,
    /// a completion pick's shape. No word after the slashes is no match, so a bare <c>//</c> stays <c>/settings</c>'
    /// alias and <c>// tools</c> is text. Pure; pinned.
    /// </summary>
    public static (CompletionItem Item, string Line)? TypoSlashes(string text, IReadOnlyList<CompletionItem> commands)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(commands);
        string line = text.Trim();
        if (!line.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        string after = line.TrimStart('/');
        int end = 0;
        while (end < after.Length && !char.IsWhiteSpace(after[end]))
        {
            end++;
        }

        if (end == 0 || CommandNamed(after[..end], commands) is not { } item)
        {
            return null;
        }

        return (item, item.Text + " " + after[end..].TrimStart());
    }

    /// <summary>The <c>/</c> item whose text less its slash is <paramref name="word"/>, the case ignored; the first hit, or null.</summary>
    private static CompletionItem? CommandNamed(string word, IReadOnlyList<CompletionItem> commands) =>
        commands.FirstOrDefault(item => item.Text.Length == word.Length + 1 && item.Text[0] == '/'
            && item.Text.AsSpan(1).Equals(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <see cref="InputLine.ReadAsync"/>'s <c>intercept</c> hook: under the setting, on the pane, a
    /// line that is a command's bare name (<see cref="TypoCommand"/> over the <c>/</c> table with
    /// <c>/exit</c> kept whatever <c>Hide /exit autocomplete</c> says) — or a command typed with extra slashes, its
    /// arguments kept (<see cref="TypoSlashes"/>, later still on 2026-09-24; the title then names the whole line) — opens a one-row
    /// <see cref="MenuPane"/> — Enter puts the command and a space on the line (a completion pick's
    /// shape, so its argument list opens where there is one), ESC / Ctrl+C sends the line as typed
    /// (null). Never headless, never mid-turn (the hook is the idle read's alone), never without the pane.
    /// </summary>
    private async Task<string?> TypoInterceptAsync(string text, CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (!effective.CommandTypoIntercept || !_menuPane.Enabled)
        {
            return null;
        }

        var commands = CommandItems();
        CompletionItem item;
        string line;
        if (TypoCommand(text, commands) is { } bare)
        {
            (item, line) = (bare, bare.Text + " ");
        }
        else if (TypoSlashes(text, commands) is { } slashed)
        {
            (item, line) = slashed;
        }
        else
        {
            return null;
        }

        var page = new MenuPage(TypoTitle(line.TrimEnd()), [TypoRow(item)], TypoKeys);
        try
        {
            var picked = await _menuPane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
            return picked is null ? null : line;
        }
        finally
        {
            _menuPane.Close();
        }
    }

    // ── The argument list on the line (2026-09-16) ──────────────────────────

    /// <summary>
    /// What the argument table reads that is not a constant: the profile names and the loaded one,
    /// the running timers' names, the skill catalog (<see cref="SkillChoices"/>, for <c>/skills edit</c>, 2026-09-21), the sandbox's
    /// folders for a prefix (<c>WorkingDirectory.Complete</c>, folders only, for <c>/tree</c> and
    /// <c>/explore</c>) and its <c>@</c>-mention walk over the text files alone
    /// (<c>Complete(query, WorkingDirectory.IsTextFile)</c>, for <c>/speak</c>) or the image files alone
    /// (<c>Complete(query, ImageFile.IsImagePath)</c>, for <c>/view</c>) — <see cref="ArgumentPaths"/> —
    /// the disk reads behind a function each, so <c>/tts o</c> scans no catalog.
    /// </summary>
    public sealed record ArgumentSources(Func<IReadOnlyList<string>> Profiles, string LoadedProfile, IReadOnlyList<string> Timers, Func<string, IReadOnlyList<string>> Folders, Func<string, MentionResult> TextFiles, Func<string, MentionResult> ImageFiles, Func<IReadOnlyList<CompletionItem>>? Sessions = null, Func<IReadOnlyList<CompletionItem>>? Skills = null, Func<string, IReadOnlyList<string>>? VaultFolders = null, Func<IReadOnlyList<CompletionItem>>? Workflows = null);

    /// <summary>The note beside <c>on</c> / <c>off</c> on a switch's list: what the switch is. Pinned.</summary>
    public static string SwitchSubject(SlashCommand command) => command switch
    {
        SlashCommand.Tts => "speech output",
        SlashCommand.Voice => "speech input",
        SlashCommand.Wake => "the wake word",
        SlashCommand.Interrupt => "the wake word interrupt",
        _ => "",
    };

    /// <summary>The <c>/git</c> list (2026-09-21): <c>user</c>, and <c>user force</c> once the word is typed. Pinned.</summary>
    public static readonly IReadOnlyList<CompletionItem> GitVerbs =
    [
        new(GitUserWord, GitUserNote),
        new(GitUserWord + " " + GitForceWord, GitUserForceNote),
    ];

    /// <summary>The <c>/profile</c> verbs on its list, each with its note (<c>edit</c> and <c>reload</c> since 2026-09-21). Pinned.</summary>
    public static readonly IReadOnlyList<CompletionItem> ProfileVerbs =
    [
        new("add", "add a profile: /profile add <name>"),
        new("delete", "delete a profile: /profile delete <name>"),
        new("edit", "open this profile's profile.json in your editor: /profile edit"),
        new("reload", "read this profile's profile.json back from disk: /profile reload"),
        new("rename", "rename a profile: /profile rename <name> <new-name>"),
        new("reset", "reset a profile to the defaults: /profile reset [name]"),
    ];

    /// <summary>The notes on a profile name on the <c>/profile</c> list. Pinned.</summary>
    public const string SwitchToProfileNote = "switch to it";

    // The /sessions grammar's words (2026-09-18). Pinned.
    public const string SessionPurgeWord = "purge";
    public const string SessionOlderWord = "older";
    public const string SessionAllWord = "all";
    public const string SessionTitleWord = "title";
    public const string SessionPurgeAllNote = "purge every session: /sessions purge all";
    public const string SessionPurgeOlderNote = "purge the sessions older than an age: /sessions purge older <age> (30 = days, 12h, 90m, 1d 6h)";

    /// <summary>The verbs the <c>/sessions</c> argument list offers after the ids; the ids' rows come first (<see cref="SessionChoices"/>).</summary>
    public static readonly IReadOnlyList<CompletionItem> SessionVerbs =
    [
        new(SessionPurgeWord, "purge a session: /sessions purge <id> | older <days> | all"),
        new(SessionTitleWord, "rename this session: /sessions title <text>"),
    ];

    /// <summary>The note beside a session's id on the list: its title, and <see cref="SessionsMenu.CurrentNote"/> for the one on screen.</summary>
    public static string SessionNote(SessionSummary session, long? current) => session.Id == current ? session.Title + " (" + SessionsMenu.CurrentNote + ")" : session.Title;

    /// <summary>The stored sessions as completion items, newest first: <c>#12</c> with the title as its note.</summary>
    private IReadOnlyList<CompletionItem> SessionChoices() =>
        _sessions.List(0).Select(session => new CompletionItem(SessionText.Id(session.Id), SessionNote(session, _sessionId))).ToList();
    public const string LoadedProfileNote = "the loaded profile";

    /// <summary>The <c>/memory copy</c> list's notes: the word, then a target profile, then <c>copy &lt;name&gt; overwrite</c>. Pinned.</summary>
    public const string MemoryCopyNote = "copy every memory into another profile";
    public const string MemoryCopyTargetNote = "copy this profile's memory into it";
    public const string MemoryCopyOverwriteNote = "replace its memory instead of adding to it";

    /// <summary>The <c>/cmdcopy</c> notes (2026-09-21), the shape of the <c>/memory copy</c> pair.</summary>
    public const string CmdCopyTargetNote = "copy this profile's allowed commands into it";
    public const string CmdCopyOverwriteNote = "replace its allowed commands instead of adding to it";
    public const string CmdCopyHistoryNote = "copy this profile's command history into it instead";
    public const string CmdCopyHistoryOverwriteNote = "replace its command history instead of adding to it";

    /// <summary>The <c>/timer</c> list's entries. Pinned.</summary>
    public const string TimerStopNote = "stop a timer: /timer stop <name> | all";
    public const string TimerStopAllNote = "stop every timer";

    /// <summary>The <c>/cwd</c> list's note on <c>~</c>. Pinned.</summary>
    public const string CwdDefaultNote = "the profile's files folder";

    /// <summary>The <c>/copy</c> list's note on <c>all</c>. Pinned.</summary>
    public const string CopyAllNote = "every reply";

    /// <summary>The note on <c>reset</c> after a prompt-file command. Pinned.</summary>
    public static string PromptFileResetNote(string fileName) => $"remove {fileName} and go back to the default";

    /// <summary>The notes on <c>copy</c>, the profile after it and <c>force</c> after that (2026-09-21). Pinned.</summary>
    public static string PromptFileCopyNote(string fileName) => $"copy {fileName} into another profile";
    public static string PromptFileCopyTargetNote(string fileName) => $"copy {fileName} into it";
    public static string PromptFileCopyForceNote(string fileName) => $"replace its {fileName} if it has one";

    /// <summary>
    /// The input line's argument list (<see cref="MentionCompleter.TryFindArgument"/>): what
    /// <paramref name="command"/> (as typed — an alias resolves) can take after
    /// <paramref name="argText"/>, the argument so far. Every candidate is the whole argument to
    /// write, narrowed by <see cref="MentionCompleter.Matches"/> (a prefix; the argument typed in
    /// full closes the list). Finite arguments only: on | off for the four speech switches, the reasoning
    /// levels, the profile names and verbs (and <c>delete | rename | reset &lt;name&gt;</c> as a second
    /// level), <c>stop</c> then <c>stop all | &lt;name&gt;</c> for the timers, <c>~</c> for
    /// <c>/cwd</c> (a path is free text and resolves against the process directory, not the
    /// sandbox), the sandbox's folders for <c>/tree</c> and <c>/explore</c>, the vault's for <c>/vault</c> (2026-09-23), <c>all</c> for <c>/copy</c>,
    /// <c>reset</c> for the three prompt files. Free text (a URL, a
    /// memory, a focus, a new name, a duration, a message; <c>/loop</c> lists <c>infinite</c> alone and <c>/skills</c> <c>edit</c> then <c>edit &lt;name&gt;</c> over the catalog, 2026-09-21) and <c>/model</c>'s ids (a network probe,
    /// nothing cached; the picker lists them) get nothing — and so do <c>/speak</c> and <c>/view</c>
    /// here: their argument is a path list, <see cref="ArgumentPaths"/>. Pure.
    /// </summary>
    public static IReadOnlyList<CompletionItem> ArgumentItems(string command, string argText, ArgumentSources sources)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(sources);
        var (kind, _) = SlashCommands.Parse(command);
        switch (kind)
        {
            case SlashCommand.Tts or SlashCommand.Voice or SlashCommand.Wake or SlashCommand.Interrupt:
            {
                string subject = SwitchSubject(kind);
                return MentionCompleter.Matches([new("on", subject + " on"), new("off", subject + " off")], argText);
            }

            case SlashCommand.Reasoning:
                return MentionCompleter.Matches(ReasoningLevel.Levels.Select(level => new CompletionItem(level, ReasoningLevel.Describe(level))).ToList(), argText);

            case SlashCommand.Comfy:
            {
                // /comfy edit json|markdown <workflow> (later still on 2026-09-24): the verb, the kind, then every installed workflow.
                foreach (string fileKind in (string[])[ComfyJsonWord, ComfyMarkdownWord])
                {
                    string head = ComfyEditWord + " " + fileKind + " ";
                    if (argText.StartsWith(head, StringComparison.OrdinalIgnoreCase))
                    {
                        var names = (sources.Workflows?.Invoke() ?? []).Select(w => new CompletionItem(head + w.Text, w.Note)).ToList();
                        return MentionCompleter.Matches(names, argText);
                    }
                }

                if (argText.StartsWith(ComfyEditWord + " ", StringComparison.OrdinalIgnoreCase))
                {
                    return MentionCompleter.Matches([new(ComfyEditWord + " " + ComfyJsonWord, ComfyJsonNote), new(ComfyEditWord + " " + ComfyMarkdownWord, ComfyMarkdownNote)], argText);
                }

                return MentionCompleter.Matches([new(ComfyEditWord, ComfyEditNote), new(ComfyPurgeWord, ComfyPurgeNote)], argText);
            }

            case SlashCommand.Imagine:
                // The workflow names (later on 2026-09-24, the user's ask): only while the first word is typed; the prompt after it is free text.
                return argText.Contains(' ', StringComparison.Ordinal) ? [] : MentionCompleter.Matches(sources.Workflows?.Invoke() ?? [], argText);

            case SlashCommand.Theme:
                return MentionCompleter.Matches(ThemeName.Names.Select(name => new CompletionItem(name, ThemeName.Describe(name))).ToList(), argText);

            case SlashCommand.Profile:
            {
                foreach (var verb in ProfileVerbs)
                {
                    if (verb.Text is "add" or "edit" or "reload")
                    {
                        // No name follows: a new one is free text, edit and reload take none.
                        continue;
                    }

                    if (argText.StartsWith(verb.Text + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        // The second level: the verb with each name; rename's new name is free text.
                        // reset leaves default out unless it is the loaded one (2026-09-22, Profiles.ResetRefusal);
                        // delete leaves it out always (later that day, the user's call: it can never be deleted),
                        // and so does rename (later still, the user's call: Profiles.RenameRefusal refuses it from any profile).
                        var names = sources.Profiles().Where(name => verb.Text switch
                        {
                            ResetWord => Profiles.ResetRefusal(name, sources.LoadedProfile) is null,
                            "delete" or "rename" => !Profiles.IsDefault(name),
                            _ => true,
                        });
                        return MentionCompleter.Matches(names.Select(name => new CompletionItem(verb.Text + " " + name, ProfileNote(name, sources.LoadedProfile))).ToList(), argText);
                    }
                }

                var items = sources.Profiles().Select(name => new CompletionItem(name, ProfileNote(name, sources.LoadedProfile))).Concat(ProfileVerbs).ToList();
                return MentionCompleter.Matches(items, argText);
            }

            case SlashCommand.Git:
                // The one verb, its force form once "user " is typed (2026-09-21).
                return MentionCompleter.Matches(argText.StartsWith(GitUserWord + " ", StringComparison.OrdinalIgnoreCase) ? [GitVerbs[1]] : [GitVerbs[0]], argText);

            case SlashCommand.Session:
            {
                var sessions = sources.Sessions?.Invoke() ?? [];
                if (argText.StartsWith(SessionPurgeWord + " ", StringComparison.OrdinalIgnoreCase))
                {
                    // The second level: purge with all, older, or an id; older's age is free text.
                    var purges = new List<CompletionItem> { new(SessionPurgeWord + " " + SessionAllWord, SessionPurgeAllNote), new(SessionPurgeWord + " " + SessionOlderWord, SessionPurgeOlderNote) };
                    purges.AddRange(sessions.Select(item => new CompletionItem(SessionPurgeWord + " " + item.Text, item.Note)));
                    return MentionCompleter.Matches(purges, argText);
                }

                if (argText.StartsWith(SessionTitleWord + " ", StringComparison.OrdinalIgnoreCase))
                {
                    return [];
                }

                return MentionCompleter.Matches(sessions.Concat(SessionVerbs).ToList(), argText);
            }

            case SlashCommand.Timer:
            {
                if (argText.StartsWith("stop ", StringComparison.OrdinalIgnoreCase))
                {
                    var stops = new List<CompletionItem> { new("stop all", TimerStopAllNote) };
                    stops.AddRange(sources.Timers.Select(name => new CompletionItem("stop " + name, "")));
                    return MentionCompleter.Matches(stops, argText);
                }

                return MentionCompleter.Matches([new("stop", TimerStopNote)], argText);
            }

            case SlashCommand.CmdCopy:
            {
                // Every profile but the loaded one (the source); after a name and a space, the one word that replaces.
                // /memcopy had the grammar from 2026-09-17 until it became /memory copy on 2026-09-22, and this branch was the pair's.
                var targets = sources.Profiles().Where(name => !Profiles.NameEquals(name, sources.LoadedProfile)).ToList();
                foreach (var name in targets)
                {
                    if (argText.StartsWith(name + " " + HistorySwitch + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        return MentionCompleter.Matches([new(name + " " + HistorySwitch + " " + OverwriteWord, CmdCopyHistoryOverwriteNote)], argText);
                    }

                    if (argText.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        // --history (2026-09-25): the command history instead of the allowed commands.
                        return MentionCompleter.Matches([new(name + " " + OverwriteWord, CmdCopyOverwriteNote), new(name + " " + HistorySwitch, CmdCopyHistoryNote)], argText);
                    }
                }

                return MentionCompleter.Matches(targets.Select(name => new CompletionItem(name, CmdCopyTargetNote)).ToList(), argText);
            }

            case SlashCommand.Cwd:
                return MentionCompleter.Matches([new(CwdHomeWord, CwdDefaultNote), new(CwdBrowseWord, FolderText.BrowseNote)], argText);

            case SlashCommand.Tree or SlashCommand.Explore:
                return MentionCompleter.Matches(sources.Folders(argText).Select(folder => new CompletionItem(folder, "")).ToList(), argText);

            case SlashCommand.Vault:
                // The vault's folders, as /tree's are the sandbox's (2026-09-23, the user's ask).
                return MentionCompleter.Matches((sources.VaultFolders?.Invoke(argText) ?? []).Select(folder => new CompletionItem(folder, "")).ToList(), argText);

            case SlashCommand.Copy:
                return MentionCompleter.Matches([new("all", CopyAllNote)], argText);

            case SlashCommand.Queue:
                return MentionCompleter.Matches([new(QueueClearWord, QueueClearNote)], argText);

            case SlashCommand.Memory:
            {
                // forget or copy; after "copy " every profile but the loaded one; after "copy <name> " the
                // overwrite word (2026-09-22, the prompt files' branch below, which /memcopy's fold follows).
                if (argText.StartsWith(CopyWord + " ", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = argText[(CopyWord.Length + 1)..].TrimStart();
                    var targets = sources.Profiles().Where(name => !Profiles.NameEquals(name, sources.LoadedProfile)).ToList();
                    foreach (var name in targets)
                    {
                        if (rest.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
                        {
                            return MentionCompleter.Matches([new(CopyWord + " " + name + " " + OverwriteWord, MemoryCopyOverwriteNote)], argText);
                        }
                    }

                    return MentionCompleter.Matches(targets.Select(name => new CompletionItem(CopyWord + " " + name, MemoryCopyTargetNote)).ToList(), argText);
                }

                return MentionCompleter.Matches([new(MemoryForgetWord, MemoryForgetNote), new(CopyWord, MemoryCopyNote), new(MemoryEditWord, MemoryEditNote)], argText);
            }

            case SlashCommand.Persona or SlashCommand.Operata or SlashCommand.Vocalia:
            {
                // reset or copy; after "copy " every profile but the loaded one; after "copy <name> " the force word (2026-09-21).
                string fileName = kind switch { SlashCommand.Persona => PersonaFile.FileName, SlashCommand.Operata => OperataFile.FileName, _ => VocaliaFile.FileName };
                if (argText.StartsWith(CopyWord + " ", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = argText[(CopyWord.Length + 1)..].TrimStart();
                    var targets = sources.Profiles().Where(name => !Profiles.NameEquals(name, sources.LoadedProfile)).ToList();
                    foreach (var name in targets)
                    {
                        if (rest.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
                        {
                            return MentionCompleter.Matches([new(CopyWord + " " + name + " " + ForceWord, PromptFileCopyForceNote(fileName))], argText);
                        }
                    }

                    return MentionCompleter.Matches(targets.Select(name => new CompletionItem(CopyWord + " " + name, PromptFileCopyTargetNote(fileName))).ToList(), argText);
                }

                return MentionCompleter.Matches([new(ResetWord, PromptFileResetNote(fileName)), new(CopyWord, PromptFileCopyNote(fileName))], argText);
            }

            case SlashCommand.Learn:
                return MentionCompleter.Matches([new(LearnSessionsWord, LearnSessionsNote)], argText);

            case SlashCommand.Loop:
                // The one word; a count and the message are free text (2026-09-21).
                return MentionCompleter.Matches([new(LoopInfiniteWord, LoopInfiniteNote)], argText);

            case SlashCommand.BotChat:
                return BotChatChoices(argText, sources);

            default:
                return [];
        }
    }

    private static string ProfileNote(string name, string loaded) =>
        string.Equals(name, loaded, StringComparison.OrdinalIgnoreCase) ? LoadedProfileNote : SwitchToProfileNote;

    /// <summary>
    /// The path list for a command whose argument is a sandbox path — <c>/speak</c> (2026-09-17),
    /// the user's ask for the <c>@</c>-mention's shape over <c>/tree</c>'s flat word list, and
    /// <c>/view</c> (later that day): the text before the last <c>/</c> is the folder to look in,
    /// the rest a name prefix, folders first and the files the command can take alone — the text
    /// files (<see cref="ArgumentSources.TextFiles"/>) or the image files
    /// (<see cref="ArgumentSources.ImageFiles"/>), so a pick always reads; a folder pick follows
    /// <c>File @-mention folder mode</c>. Null for every other command.
    /// An argument ending in whitespace lists nothing — the argument is done: a folder applied
    /// under <c>Apply</c> (<c>docs/ </c>), a name followed by a space — and the next letter reopens
    /// it (a name with a space inside completes on); a path typed in full (one match, equal to
    /// the text) closes it so Enter sends, as a word list closes. Pure.
    /// </summary>
    public static MentionResult? ArgumentPaths(string command, string argText, ArgumentSources sources)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(sources);
        var kind = SlashCommands.Parse(command).Command;
        if (kind is not (SlashCommand.Speak or SlashCommand.View))
        {
            return null;
        }

        if (argText.Length > 0 && char.IsWhiteSpace(argText[^1]))
        {
            return new MentionResult(FileOutcome.Ok, [], false);
        }

        var found = kind == SlashCommand.Speak ? sources.TextFiles(argText) : sources.ImageFiles(argText);
        if (found.Paths.Count == 1 && string.Equals(found.Paths[0], argText, StringComparison.OrdinalIgnoreCase))
        {
            return new MentionResult(FileOutcome.Ok, [], false);
        }

        return found;
    }

    /// <summary>The argument list's live sources: the profiles on disk, the board's timers, the sandbox's folders, its text files and its image files.</summary>
    private ArgumentList ArgumentChoices(string command, string argText)
    {
        var sources = new ArgumentSources(
            () => Profiles.List(_settings.StorageDirectory),
            _settings.ProfileName,
            _timers.Snapshot().Select(timer => timer.Name).ToList(),
            prefix => _files.Complete(prefix).Paths.Where(path => path.EndsWith('/')).ToList(),
            prefix => _files.Complete(prefix, WorkingDirectory.IsTextFile),
            prefix => _files.Complete(prefix, ImageFile.IsImagePath),
            SessionChoices,
            SkillChoices,
            VaultFolderChoices,
            WorkflowChoices);
        return ArgumentPaths(command, argText, sources) is { } paths
            ? new ArgumentList([], paths.Paths, paths.Truncated)
            : new ArgumentList(ArgumentItems(command, argText, sources));
    }

    /// <summary>The catalog as the next turn would see it: rescanned now while the setting says so (a read, like <see cref="SystemPromptFacts"/>), else empty.</summary>
    private IReadOnlyList<Skill> Catalog(AppSettingsData effective)
    {
        if (!effective.AgentSkills)
        {
            return [];
        }

        _catalog.Scan(effective.ExternalSkills);
        return _catalog.Skills;
    }

    private IReadOnlyList<ToolGroup> ToolGroups()
    {
        var effective = _effective();
        var disabled = ToolsText.DisabledSet(effective.ToolsDisabled);
        var fileTools = FileToolsFor(_fileTools, effective.FileSafeEdits);   // restore only with File safe edits on (later still on 2026-09-20): /sys shows the list cut, Files (14)
        bool files = effective.FileTools && Without(fileTools, disabled).Count > 0;   // the turn's rule (PrepareTurn): an emptied file group is the switch off
        return SystemPromptSummary.ToolGroups(_clockTools, _timerTools, fileTools, _memoryTools, effective.Memory, effective.LlmOfferTools, WebToolsFor(_webTools, files), effective.WebTools, effective.FileTools, _askTools, effective.AskUser, _pane.Enabled, _skillTools, effective.AgentSkills, _sessionTools, effective.SessionTool, disabled, mcp: _mcp.ServerTools, mcpEnabled: effective.McpServers, git: _gitTools, gitEnabled: effective.GitNativeTools, shell: _shellTools, shellEnabled: ShellOffered(effective), codeAvailable: CodeAvailable(), obsidian: ObsidianOffered(effective) ? ObsidianToolsFor(_vaultTools, effective) : null, sql: SqlOffered(effective, _sql) ? _sqlTools : null, comfy: ComfyOffered(effective, _comfy) ? _comfyTools : null);   // the vault group only with a vault (2026-09-22): /sys stays as it was for a profile that never names one
    }

    /// <summary>Whether <c>execute_code</c> has a language to run (2026-09-21): the setting's languages, one of them installed.</summary>
    private bool CodeAvailable() => _shellTools.OfType<ExecuteCodeTool>().FirstOrDefault() is not { AvailableLanguages.Count: 0 };

    /// <summary>Whether the shell group is offered (2026-09-21): the setting <c>Shell command policy</c> is not <c>off</c>.</summary>
    public static bool ShellOffered(AppSettingsData effective) => CommandPolicy.Resolve(effective) != CommandPolicyMode.Off;

    /// <summary>
    /// What <c>/tools</c>' Offered tab lists (<see cref="ToolsMenu"/>, read again after every flip): the
    /// groups as <see cref="ToolGroups"/> builds them but over the whole web list (so <c>download_file</c>
    /// is shown with its reason under <c>File tools</c> off, not dropped) and with <c>load_skill</c> noted
    /// while no skill is installed.
    /// </summary>
    /// <summary>The <c>/mcp</c> pane's facts (2026-09-20): the session's rows and problems, the two switches, the per-tool list and the two file paths; re-read after every act.</summary>
    private McpFacts McpFacts()
    {
        var effective = _effective();
        return new McpFacts(_mcp.Servers, _mcp.Problems, effective.McpServers, effective.LlmOfferTools, ToolsText.DisabledSet(effective.ToolsDisabled), _mcp.ProfilePath, _mcp.GlobalPath);
    }

    private ToolsFacts ToolsFacts()
    {
        var effective = _effective();
        var disabled = ToolsText.DisabledSet(effective.ToolsDisabled);
        // The whole file list, restore noted under File safe edits off (later still on 2026-09-20): the row stays, dim, with its reason — the download_file shape.
        _interpreters.Refresh();
        var groups = SystemPromptSummary.ToolGroups(_clockTools, _timerTools, _fileTools, _memoryTools, effective.Memory, effective.LlmOfferTools, _webTools, effective.WebTools, effective.FileTools, _askTools, effective.AskUser, _pane.Enabled, _skillTools, effective.AgentSkills, _sessionTools, effective.SessionTool, disabled, skillInstalled: Catalog(effective).Count > 0, git: _gitTools, gitEnabled: effective.GitNativeTools, safeEdits: effective.FileSafeEdits, shell: _shellTools, shellEnabled: ShellOffered(effective), codeAvailable: CodeAvailable(), obsidian: ObsidianToolsFor(_vaultTools, effective), obsidianEnabled: ObsidianOffered(effective), sql: _sqlTools, sqlEnabled: SqlOffered(effective, _sql), comfy: _comfyTools, comfyEnabled: ComfyOffered(effective, _comfy));
        return new ToolsFacts(groups, effective.LlmOfferTools, disabled);
    }

    /// <summary>The skills as the next turn would take them (<see cref="PrepareTurn"/>), from the live settings.</summary>
    private SkillsForTurn SkillsFor(AppSettingsData effective) =>
        new(_catalog, _skillTools, _project, effective.AgentSkills, effective.AgentSkills && effective.ExternalSkills, effective.ProjectFile);

    /// <summary>
    /// Everything that lives in the profile's directory, built over <see cref="AppSettings.ProfileDirectory"/>:
    /// the memory store (shared by <c>/remember</c>, <c>/memory</c>, <c>/memory forget</c> and the model's
    /// <c>save_memory</c>), its tool and menu, and the persona, operating-rules and voice-directive files. Called once at construction
    /// and again after every switch, so a command always acts on the loaded profile.
    /// </summary>
    private void BindProfile()
    {
        _memory = new MemoryStore(_settings.ProfileDirectory);
        _persona = new PersonaFile(_settings.ProfileDirectory);
        _operata = new OperataFile(_settings.ProfileDirectory);
        _vocalia = new VocaliaFile(_settings.ProfileDirectory);
        _memoryTools = MemoryTools(_memory);
        _memoryMenu = new MemoryMenu(new ConsoleWithInput(_pane, _keys), _memory, _flow, _menuPane);
        // The session store (2026-09-18): the old profile's handle closed, the new one opened lazily
        // by its first use; the retention purge runs here, at startup and after every switch.
        _sessions?.Dispose();
        _sessions = new SessionStore(_settings.ProfileDirectory, _time);
        _sessionTools = SessionTools(_sessions, _effective, () => _sessionId, _time);
        _sessionsMenu = new SessionsMenu(_sessions, () => _sessionId, _flow, _menuPane, _input, _time, id => { if (_sessionId == id) { ForgetSession(); } });
        PurgeExpiredSessions();
        LoadCommandHistory();
    }

    /// <summary>
    /// The profile's command history onto the input line (2026-09-25, <c>Keep command history</c>), at startup and after
    /// every switch — the user's call: Up recalls the loaded profile's lines, never the last one's. Off, the stored lines
    /// go (the setting's promise: deleted at the next load) and the line starts empty.
    /// </summary>
    private void LoadCommandHistory()
    {
        if (_effective().KeepCommandHistory)
        {
            _input.ReplaceHistory(_sessions.CommandHistory());
            return;
        }

        int cleared = _sessions.ClearCommandHistory();
        if (cleared > 0)
        {
            DiagnosticLog.Debug(SessionsCategory, CommandHistoryDroppedLogLine(cleared));
        }

        _input.ReplaceHistory([]);
    }

    /// <summary>
    /// A line the input line's history gained, stored while <c>Keep command history</c> is on (2026-09-25) — unless it holds
    /// a collapsed paste or a picture, whose block is this session's alone. <see cref="_sessions"/> is read at each line, so
    /// the store follows a profile switch.
    /// </summary>
    private void StoreCommand(string line)
    {
        if (_effective().KeepCommandHistory && !line.Any(PasteBlocks.IsToken))
        {
            _sessions.AppendCommand(line);
        }
    }

    /// <summary>The Debug line when a load with <c>Keep command history</c> off drops the stored lines. Pinned.</summary>
    public static string CommandHistoryDroppedLogLine(int count) =>
        $"Keep command history is off: dropped {CommandLines(count)} from {SessionStore.FileName}.";

    /// <summary>The session tool (<c>session_manager</c>, 2026-09-18), offered while the setting <c>Session tool</c> is on; the conversation on screen (<paramref name="current"/>) is left out of its answers. Shared with headless.</summary>
    public static IReadOnlyList<AIFunction> SessionTools(SessionStore store, Func<AppSettingsData> effective, Func<long?> current, TimeProvider time) => new AIFunction[]
    {
        new SessionManagerTool(store, effective, current, time),
    };

    /// <summary><c>Session 12 restored: 6 turns</c>. Pinned.</summary>
    public static string SessionRestoredLogLine(long id, int turns) =>
        string.Create(CultureInfo.InvariantCulture, $"Session {id} restored: {turns} turn{(turns == 1 ? "" : "s")}");

    /// <summary>The retention purge (<c>Session retention (days)</c>): sessions last updated longer ago go without a word — a log line alone. Nothing at 0.</summary>
    private void PurgeExpiredSessions()
    {
        int days = _effective().SessionRetentionDays;
        if (days <= 0)
        {
            return;
        }

        int purged = _sessions.PurgeOlderThan(_time.GetUtcNow().AddDays(-days));
        if (purged > 0)
        {
            DiagnosticLog.Info(SessionsCategory, $"Retention: purged {SessionText.Sessions(purged)} older than {days.ToString(CultureInfo.InvariantCulture)} days.");
        }
    }

    /// <summary>The terminal's tab names the loaded profile (<see cref="WindowTitle"/>); at launch and after every switch, like <see cref="BindProfile"/>.</summary>
    private void ApplyWindowTitle() => _setTitle?.Invoke(WindowTitle(_settings.ProfileName));

    public static string UnknownCommandError(string token) => $"Unknown command {token}. /help lists them.";

    /// <summary><c>/log</c>'s notice once the <c>--log</c> file is handed to the editor (2026-09-22). Pinned.</summary>
    public static string LogOpenedNotice(string path) => $"({NoticeGlyphs.Log}opened the log {path} in your editor)";

    /// <summary><c>/log</c> when the <c>--log</c> file is not there — it could not be opened at startup, or was deleted since. Pinned.</summary>
    public static string LogMissingError(string path) => $"The log file {path} does not exist; --log could not open it.";

    /// <summary><c>/log</c> when the editor launch fails. Pinned.</summary>
    public static string LogOpenFailedError(string detail) => $"Could not open the log file: {detail}";

    /// <summary>
    /// <c>/log</c> (2026-09-22, the user's ask): the <c>--log</c> file in the editor Windows associates with it,
    /// through the same opener as <c>/persona</c> — no wait, the file keeps growing while it is read (the sink
    /// shares it for reading and flushes per line). Only reached under <c>--log</c>: without it <c>/log</c> parses as unknown.
    /// Quick under a reply, as <c>/explore</c> is.
    /// </summary>
    private void HandleLog()
    {
        if (_logFile is not { } path)
        {
            return;
        }

        if (!File.Exists(path))
        {
            _transcript.Error(LogMissingError(path));
            return;
        }

        try
        {
            _openFile(path);
            _transcript.Notice(LogOpenedNotice(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _transcript.Error(LogOpenFailedError(ex.Message));
        }
    }

    /// <summary>A command we know, given an argument it does not take (<c>/about me</c>, 2026-09-17): the command is named, not called unknown.</summary>
    public static string NoArgumentError(string token) => $"{token} takes no argument; /help shows each command's form.";

    /// <summary>The tools a turn offers while memory is on: the save and, since 2026-09-17, the recall (also the opening memory call's). Shared with headless.</summary>
    public static IReadOnlyList<AIFunction> MemoryTools(MemoryStore memory) => new AIFunction[] { new SaveMemoryTool(memory), new RecallMemoryTool(memory) };

    /// <summary>The clock tools, offered on every turn: no switch, they read a clock and write nothing. Shared with headless.</summary>
    public static IReadOnlyList<AIFunction> ClockTools(TimeProvider time) => new AIFunction[]
    {
        new GetCurrentTimeTool(time),
        new ShiftDateTool(time),
        new DaysBetweenTool(time),
    };

    /// <summary>The timer tools, offered on every interactive turn (headless has none: nothing there could deliver the alert).</summary>
    public static IReadOnlyList<AIFunction> TimerTools(TimerBoard board) => new AIFunction[]
    {
        new StartTimerTool(board),
        new StopTimerTool(board),
        new ListTimersTool(board),
    };

    /// <summary>
    /// The file tools over the working directory, offered on every turn (headless too: they need
    /// no console). <paramref name="isDefault"/> says whether the root in force is the profile's
    /// own folder; <paramref name="openFile"/> is the shell's "open with" for <c>open</c>;
    /// <paramref name="effective"/> is where the read and edit tools read the Files-tab switches
    /// (<c>File safe edits</c>) and <c>view_image</c> its cap (<c>File view image max (per call)</c>) at every call.
    /// </summary>
    public static IReadOnlyList<AIFunction> FileTools(WorkingDirectory files, Func<bool> isDefault, Action<string> openFile, Func<AppSettingsData> effective) => new AIFunction[]
    {
        new GetWorkingDirectoryTool(files, isDefault),
        new SearchFilesTool(files),
        new FileInfoTool(files),
        new ReadFileTool(files),
        new ViewImageTool(files, effective),
        new WriteFileTool(files, effective),
        new PatchFileTool(files, effective),
        new CreateDirectoryTool(files),
        new MoveTool(files, effective),
        new CopyTool(files, effective),
        new DeleteTool(files, effective),
        new RestoreTool(files, effective),
        new ZipTool(files),
        new UnzipTool(files),
        new OpenTool(files, openFile),
    };

    /// <summary>
    /// The four web tools, offered on every turn while the setting <c>Web tools</c> is on (headless too:
    /// they need no console) — the last, <c>download_file</c> (2026-09-18), only with <c>File tools</c>
    /// on as well, since it writes the sandbox (<see cref="WebToolsFor"/>). Each reads the settings in
    /// force at the call, so a mode, a browser path, the network mode or a SearXNG URL typed in
    /// <c>/settings</c> applies at the next call.
    /// </summary>
    public static IReadOnlyList<AIFunction> WebTools(WebAccess web, WorkingDirectory files, Func<AppSettingsData> effective) => new AIFunction[]
    {
        new WebSearchTool(web, effective),
        new WebFetchTool(web, effective),
        new OpenUrlTool(web),
        new DownloadFileTool(web, files, effective),
    };

    /// <summary>
    /// <paramref name="webTools"/> as a turn offers them: whole with the file tools on, less
    /// <see cref="DownloadFileTool"/> with them off (2026-09-18) — a download is a file write, and
    /// <c>File tools</c> off means none. The same list itself when nothing is dropped. Pure.
    /// </summary>
    public static IReadOnlyList<AIFunction> WebToolsFor(IReadOnlyList<AIFunction> webTools, bool filesEnabled)
    {
        ArgumentNullException.ThrowIfNull(webTools);
        return filesEnabled || !webTools.Any(t => t is DownloadFileTool) ? webTools : webTools.Where(t => t is not DownloadFileTool).ToList();
    }

    /// <summary>
    /// <paramref name="fileTools"/> as a turn offers them: whole with <c>File safe edits</c> on, less
    /// <see cref="RestoreTool"/> with it off (later still on 2026-09-20, the user's ask) — nothing lands in
    /// <c>.trash</c> then, so the model gets no tool that reaches it and no sentence naming it
    /// (<see cref="Assistant.FileRuleDeleteInPlace"/>, the five descriptions). The <see cref="WebToolsFor"/>
    /// shape: the same list itself when nothing is dropped. Pure.
    /// </summary>
    public static IReadOnlyList<AIFunction> FileToolsFor(IReadOnlyList<AIFunction> fileTools, bool safeEdits)
    {
        ArgumentNullException.ThrowIfNull(fileTools);
        return safeEdits || !fileTools.Any(t => t is RestoreTool) ? fileTools : fileTools.Where(t => t is not RestoreTool).ToList();
    }

    /// <summary>
    /// The eleven git tools (2026-09-20), offered on every turn while the setting <c>Git native tools</c> (<c>Git tools</c> until 2026-09-21) is on
    /// (headless too): the reads first, then the writes, the two that lose work last — those two are off
    /// by name in a fresh profile's <c>ToolsDisabled</c>. Each reads the settings in force at the call.
    /// </summary>
    public static IReadOnlyList<AIFunction> GitTools(GitAccess git, Func<AppSettingsData> effective) => new AIFunction[]
    {
        new GitStatusTool(git, effective),
        new GitLogTool(git, effective),
        new GitShowTool(git, effective),
        new GitDiffTool(git, effective),
        new GitBlameTool(git, effective),
        new GitBranchTool(git, effective),
        new GitStageTool(git, effective),
        new GitCommitTool(git, effective),
        new GitStashTool(git, effective),
        new GitDiscardTool(git, effective),
        new GitDeleteTool(git, effective),
    };

    /// <summary>The git tools' names: their result's first line is the transcript's note (<see cref="GitText.Note"/>).</summary>
    public static readonly IReadOnlySet<string> GitToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        GitStatusTool.ToolName,
        GitLogTool.ToolName,
        GitShowTool.ToolName,
        GitDiffTool.ToolName,
        GitBlameTool.ToolName,
        GitBranchTool.ToolName,
        GitStageTool.ToolName,
        GitCommitTool.ToolName,
        GitStashTool.ToolName,
        GitDiscardTool.ToolName,
        GitDeleteTool.ToolName,
    };

    /// <summary>
    /// The nine vault tools (2026-09-22; vault_delete the ninth later that day, offered only under Obsidian allow delete — <see cref="ObsidianToolsFor"/>), offered on every turn while <see cref="ObsidianOffered"/> says so (headless
    /// too): the reads first, then the writes, the move last. Each reads the settings in force at the call.
    /// </summary>
    public static IReadOnlyList<AIFunction> ObsidianTools(ObsidianVault vault, Func<AppSettingsData> effective) => new AIFunction[]
    {
        new VaultSearchTool(vault, effective),
        new VaultListTool(vault, effective),
        new VaultReadTool(vault, effective),
        new VaultLinksTool(vault, effective),
        new VaultDailyTool(vault, effective),
        new VaultWriteTool(vault, effective),
        new VaultPropertiesTool(vault, effective),
        new VaultMoveTool(vault, effective),
        new VaultDeleteTool(vault, effective),
    };

    /// <summary>
    /// <paramref name="tools"/> less <c>vault_delete</c> while the setting <c>Obsidian allow delete</c> is off (2026-09-22,
    /// the user's ask: off by default; the <see cref="ShellToolsFor"/> shape), so the tool shows nowhere — not the turn,
    /// <c>/tools</c>' Offered tab, <c>/sys</c> or the <c>$</c>-mentions — until it is on. Pure.
    /// </summary>
    public static IReadOnlyList<AIFunction> ObsidianToolsFor(IReadOnlyList<AIFunction> tools, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(effective);
        return effective.ObsidianAllowDelete ? tools : tools.Where(t => t is not VaultDeleteTool).ToList();
    }

    /// <summary>The vault tools' names: their result's first line is the transcript's note (<see cref="ObsidianText.Note"/>).</summary>
    public static readonly IReadOnlySet<string> ObsidianToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        VaultSearchTool.ToolName,
        VaultListTool.ToolName,
        VaultReadTool.ToolName,
        VaultLinksTool.ToolName,
        VaultDailyTool.ToolName,
        VaultWriteTool.ToolName,
        VaultPropertiesTool.ToolName,
        VaultMoveTool.ToolName,
        VaultDeleteTool.ToolName,
    };

    /// <summary>Whether the vault group is offered (2026-09-22): the setting <c>Obsidian tools</c> on and <c>Obsidian vault</c> naming a folder with <c>.obsidian</c> — a vault gone since it was set reads as none.</summary>
    public static bool ObsidianOffered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.ObsidianTools && ObsidianVault.IsVault(effective.ObsidianVault);
    }

    /// <summary>
    /// The eight SQL tools (2026-09-23; <c>sql_columns</c> and <c>sql_indexes</c> later that day), offered on every turn while
    /// <see cref="SqlOffered"/> says so (headless too): the connections first, then the catalog from the server down to a
    /// table and its indexes, the query last. Each reads the settings
    /// in force at the call and <c>sql.json</c> afresh.
    /// </summary>
    public static IReadOnlyList<AIFunction> SqlTools(SqlAccess sql, Func<AppSettingsData> effective) => new AIFunction[]
    {
        new SqlConnectionsTool(sql, effective),
        new SqlDatabasesTool(sql, effective),
        new SqlTablesTool(sql, effective),
        new SqlColumnsTool(sql, effective),
        new SqlDescribeTool(sql, effective),
        new SqlRelationshipsTool(sql, effective),
        new SqlIndexesTool(sql, effective),
        new SqlQueryTool(sql, effective),
    };

    /// <summary>The SQL tools' names: their result's first line is the transcript's note (<see cref="SqlText.Note"/>).</summary>
    public static readonly IReadOnlySet<string> SqlToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        SqlConnectionsTool.ToolName,
        SqlDatabasesTool.ToolName,
        SqlTablesTool.ToolName,
        SqlColumnsTool.ToolName,
        SqlDescribeTool.ToolName,
        SqlRelationshipsTool.ToolName,
        SqlIndexesTool.ToolName,
        SqlQueryTool.ToolName,
    };

    /// <summary>Whether the SQL group is offered (2026-09-23): the setting <c>SQL tools</c> on and at least one usable connection in <c>sql.json</c> (the profile's or the home's).</summary>
    public static bool SqlOffered(AppSettingsData effective, SqlAccess sql)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(sql);
        return effective.SqlTools && sql.Catalog().Connections.Count > 0;
    }

    /// <summary>
    /// <c>/imagine</c>'s argument list (later on 2026-09-24): every installed workflow — <c>/imagine</c> may name any —
    /// with what it is, the ones the model is not offered noted (<see cref="ComfyText.CompletionNote"/>).
    /// </summary>
    private IReadOnlyList<CompletionItem> WorkflowChoices()
    {
        var offered = _comfy.OfferedWorkflows();
        return _comfy.Catalog.Workflows.Select(w => new CompletionItem(w.Name, ComfyText.CompletionNote(w, offered.Contains(w)))).ToList();
    }

    /// <summary>The ComfyUI workflows catalog over the loaded profile's <c>comfy</c> folder, then the home's (2026-09-24), asked afresh at each scan so a profile switch needs no rebuild.</summary>
    public static ComfyWorkflowCatalog ComfyCatalog(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ComfyWorkflowCatalog(() => [settings.ProfileComfyDirectory, settings.GlobalComfyDirectory]);
    }

    /// <summary>
    /// The image tools (2026-09-24): <c>generate_image</c> over the ComfyUI server and the workflows, and <c>set_splash_image</c>
    /// into the loaded profile's splash folder, offered on every turn while <see cref="ComfyOffered"/> says so (headless too).
    /// </summary>
    public static IReadOnlyList<AIFunction> ComfyTools(ComfyStudio studio, WorkingDirectory files, Func<string> splashDirectory) => new AIFunction[]
    {
        new GenerateImageTool(studio),
        new SetSplashImageTool(files, splashDirectory),
    };

    /// <summary>Whether the image group is offered (2026-09-24): the setting <c>ComfyUI tools</c> on, <c>ComfyUI URL</c> an http(s) URL and at least one workflow in a <c>comfy</c> folder.</summary>
    public static bool ComfyOffered(AppSettingsData effective, ComfyStudio studio)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(studio);
        return effective.ComfyTools && ComfyStudio.ServerOf(effective) is not null && studio.OfferedWorkflows().Count > 0;
    }

    /// <summary>
    /// The shell tools (2026-09-21): <c>run_command</c>, <c>process</c> and <c>execute_code</c>, offered on every turn
    /// while the setting <c>Shell command policy</c> is not <c>off</c> (headless too, where the gate has no asker and
    /// the allow list alone decides); <c>execute_code</c> only while a language it may run is installed
    /// (<see cref="ShellToolsFor"/>). Each reads the settings in force at the call; <paramref name="turnTools"/> is
    /// what a script's bridge dispatches to — the turn's own list.
    /// </summary>
    public static IReadOnlyList<AIFunction> ShellTools(ShellRunner runner, ProcessRegistry processes, WorkingDirectory files, CommandGate gate, Interpreters interpreters, Func<AppSettingsData> effective, Random random, Func<IReadOnlyList<AIFunction>> turnTools, string? runsFolder = null) => new AIFunction[]
    {
        new RunCommandTool(runner, processes, files, gate, interpreters, effective, random),
        new ProcessTool(processes, files, effective, gate),
        new ExecuteCodeTool(runner, files, gate, interpreters, effective, turnTools, random, runsFolder),
    };

    /// <summary><paramref name="tools"/> less <c>execute_code</c> while no language it may run is installed (2026-09-21, the <see cref="WebToolsFor"/> shape). Pure.</summary>
    public static IReadOnlyList<AIFunction> ShellToolsFor(IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Any(t => t is ExecuteCodeTool { AvailableLanguages.Count: 0 }) ? tools.Where(t => t is not ExecuteCodeTool).ToList() : tools;
    }

    /// <summary>The shell tools' names: their result's first line is the transcript's note (<see cref="ShellText.Note"/>).</summary>
    public static readonly IReadOnlySet<string> ShellToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        RunCommandTool.ToolName,
        ProcessTool.ToolName,
        ExecuteCodeTool.ToolName,
    };

    /// <summary>
    /// The seeded polls for the notified exits since the last turn (2026-09-21): one <c>process poll</c>
    /// call/result pair each at the next turn's start (<see cref="Assistant.PendingCalls"/>), so the model
    /// learns what ended without being asked — taken only while the process tool is among
    /// <paramref name="offered"/>, else they wait (the user saw the alert line either way).
    /// </summary>
    public static IReadOnlyList<Assistant.OpeningCall> PendingProcessPolls(ProcessRegistry processes, IReadOnlyList<AIFunction> offered)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(offered);
        if (offered.OfType<ProcessTool>().FirstOrDefault() is not { } tool)
        {
            return [];
        }

        return processes.TakeNotes().Select(id => new Assistant.OpeningCall(tool, Assistant.PendingCallId(id), ProcessTool.PollArguments(id))).ToList();
    }

    /// <summary>
    /// The question tool (<c>ask_user</c>, 2026-09-15), offered while the setting <c>Ask user</c> is on
    /// and the bottom pane is on: nothing else can draw the questions, and headless never has it.
    /// <paramref name="ask"/> shows them and waits — the screen's <see cref="AskUserAsync"/>, which
    /// hands the pane phase to the turn's key watcher; <paramref name="effective"/> the settings the
    /// caps (<c>Ask max questions</c>, <c>Ask max choices per question</c>) are read from at every use.
    /// </summary>
    public static IReadOnlyList<AIFunction> AskTools(Func<IReadOnlyList<AskQuestion>, CancellationToken, Task<IReadOnlyList<AskAnswer>?>> ask, Func<AppSettingsData> effective) => new AIFunction[]
    {
        new AskUserTool(ask, effective),
    };

    /// <summary>
    /// The tools whose call line the transcript skips, showing the result alone as one dim
    /// <c>🛠️</c> line: the result sentence says it all and the arguments would repeat it
    /// (<c>ask_user</c>'s answers one line each, <see cref="TranscriptRenderer.ToolNotes"/>).
    /// Headless keeps the generic lines.
    /// </summary>
    public static readonly IReadOnlySet<string> QuietTools = new HashSet<string>(StringComparer.Ordinal)
    {
        SaveMemoryTool.ToolName,
        RecallMemoryTool.ToolName,
        AskUserTool.ToolName,
        WebSearchTool.ToolName,
        WebFetchTool.ToolName,
        OpenUrlTool.ToolName,
        DownloadFileTool.ToolName,
        SessionManagerTool.ToolName,
        GetCurrentTimeTool.ToolName,
        ShiftDateTool.ToolName,
        DaysBetweenTool.ToolName,
        StartTimerTool.ToolName,
        StopTimerTool.ToolName,
        ListTimersTool.ToolName,
        GetWorkingDirectoryTool.ToolName,
        SearchFilesTool.ToolName,
        FileInfoTool.ToolName,
        ReadFileTool.ToolName,
        ViewImageTool.ToolName,
        GenerateImageTool.ToolName,
        SetSplashImageTool.ToolName,
        WriteFileTool.ToolName,
        PatchFileTool.ToolName,
        CreateDirectoryTool.ToolName,
        MoveTool.ToolName,
        CopyTool.ToolName,
        DeleteTool.ToolName,
        RestoreTool.ToolName,
        ZipTool.ToolName,
        UnzipTool.ToolName,
        OpenTool.ToolName,
        LoadSkillTool.ToolName,
        SkillEditorTool.ToolName,
        GitStatusTool.ToolName,
        GitLogTool.ToolName,
        GitShowTool.ToolName,
        GitDiffTool.ToolName,
        GitBlameTool.ToolName,
        GitBranchTool.ToolName,
        GitStageTool.ToolName,
        GitCommitTool.ToolName,
        GitStashTool.ToolName,
        GitDiscardTool.ToolName,
        GitDeleteTool.ToolName,
        RunCommandTool.ToolName,
        ProcessTool.ToolName,
        ExecuteCodeTool.ToolName,
    };

    /// <summary>
    /// What a turn sees, decided once at its start like the speech flag: the standing tools
    /// (the clock, and on the screen the timers) always; with memory on, the memory tool and the
    /// memories in the system prompt too; off, neither; and the persona, the operating rules and the voice
    /// directive as <c>persona.md</c>, <c>operata.md</c> and <c>vocalia.md</c> stand right now (<see cref="PromptFile.Read"/>); and the clock and the working directory,
    /// each when it is offered, as the calls every conversation opens with
    /// (<see cref="Assistant.OpeningCalls"/>); and the round-trip cap (<see cref="Assistant.MaxToolIterations"/>)
    /// from the setting <c>LLM max tool iterations</c>. With <paramref name="toolsEnabled"/> false (the
    /// setting <c>LLM offer tools</c> off) the turn offers no tool, seeds no opening call and reads the
    /// tool-free defaults (<see cref="Assistant.PlainTextRule"/>) — nothing stands in for
    /// the clock or the path. The web tools (<paramref name="webTools"/>) go the way of the memory
    /// tool: offered, and <see cref="Assistant.WebRule"/> appended to the default rules, while
    /// <paramref name="webEnabled"/> (the setting <c>Web tools</c>) says so; the file tools
    /// (<paramref name="fileTools"/>, 2026-09-15) the same way while <paramref name="filesEnabled"/> (the
    /// setting <c>File tools</c>) says so — offered right after the standing tools, the working-directory
    /// opening call seeded and <see cref="Assistant.FileRule"/> kept in the default rules; off, none of
    /// the three (<see cref="Assistant.OperatingRulesWithoutFiles"/>); and the mid-turn context
    /// guard (<paramref name="contextGuard"/>, <see cref="ContextGuardFor"/>) the tool loop measures
    /// each request against; and the question tool (<paramref name="askTools"/>, 2026-09-15) last of
    /// all when the screen passes it — the setting <c>Ask user</c> on and the bottom pane on — with
    /// <see cref="Assistant.AskRule"/> ending the default rules under the caps the tool itself reads
    /// (<see cref="AskUserTool.Limits"/>: the rule quotes what the schema says); null (the setting or
    /// the pane off, headless): neither. The skills (<paramref name="skills"/>, 2026-09-16): with the
    /// setting <c>Agent skills</c> on the catalog is rescanned (the external root too under
    /// <c>Use external skills</c>), the working directory's <c>NEON.md</c> / <c>AGENTS.md</c> read
    /// into the prompt tools or not, and — tools on — <c>skill_editor</c> offered after the memory
    /// tool with <c>load_skill</c> ahead of it while any skill is installed, the catalog in the prompt
    /// (<see cref="Skills.SkillsPrompt"/>); off, none of it. With <paramref name="markdown"/> (2026-09-16:
    /// the setting <c>Transcript markdown</c> on, the pane on, the turn not spoken — the screen's
    /// decision, the same one that styles the reply) the default rules ask for light Markdown
    /// (<see cref="Assistant.MarkdownRule"/>) instead of plain text; headless never passes it, its
    /// stdout being plain. The screen's own styling is decided apart (<see cref="StyledReply"/>). Since 2026-09-19
    /// <paramref name="disabledTools"/> (the <c>/tools</c> list, <c>ToolsDisabled</c>) drops its names from every
    /// group ahead of the per-group decisions (<see cref="Without"/>), so an emptied group loses its rule and its
    /// opening call as if its switch were off, a lone <c>get_current_time</c> / <c>get_working_directory</c> /
    /// <c>recall_memory</c> loses its opening call (with the memory list back in the prompt), <c>download_file</c>
    /// its rule, and <c>ask_user</c> reads as <c>Ask user</c> off; a rule naming another disabled tool stands
    /// (a call answers <c>Error: unknown tool</c>). The MCP tools (<paramref name="mcpTools"/>, 2026-09-20: every connected
    /// server's, prefixed <c>&lt;server&gt;__&lt;tool&gt;</c>) go after the session tool and before the question tool while
    /// <paramref name="mcpEnabled"/> (the setting <c>MCP servers</c>) says so, the <c>/tools</c> list dropping names from them
    /// as from any group, with <see cref="Assistant.McpRule"/> ending the default rules; no opening call. <paramref name="safeEdits"/>
    /// (the setting <c>File safe edits</c>, 2026-09-20) picks the file rule's <c>delete</c> clause: into <c>.trash</c>, or
    /// <see cref="Assistant.FileRuleDeleteInPlace"/> while it is off and <c>delete</c> is offered — and, later still that day,
    /// drops <c>restore</c> from the file list (<see cref="FileToolsFor"/>) ahead of the group decision, so the model never
    /// hears of the trash while the setting is off. The timer sentence
    /// (<see cref="Assistant.TimerRule"/>, 2026-09-20) rides only while a timer tool is among <paramref name="standingTools"/>:
    /// headless passes the clock alone (nothing could ring the alert), and the pane loses the three on <c>/tools</c>. With the shell offered,
    /// <c>run_command</c> is told the turn's tool names (<see cref="RunCommandTool.BeginTurn"/>, 2026-09-26) and, with <paramref name="shellNative"/>
    /// (the setting <c>Shell prefer native tools</c>), the rules gain <see cref="Assistant.ShellNativeRule"/> after the shell sentence. Shared with headless.
    /// </summary>
    public static void PrepareTurn(Assistant assistant, MemoryStore memory, IReadOnlyList<AIFunction> memoryTools, IReadOnlyList<AIFunction> standingTools, PersonaFile persona, OperataFile operata, VocaliaFile vocalia, bool memoryEnabled, bool speechOutput, int maxToolIterations = Assistant.DefaultMaxToolIterations, bool toolsEnabled = true, IReadOnlyList<AIFunction>? webTools = null, bool webEnabled = false, Assistant.TurnContextGuard? contextGuard = null, IReadOnlyList<AIFunction>? fileTools = null, bool filesEnabled = false, IReadOnlyList<AIFunction>? askTools = null, SkillsForTurn? skills = null, bool markdown = false, IReadOnlyList<AIFunction>? sessionTools = null, bool sessionsEnabled = false, IReadOnlySet<string>? disabledTools = null, IReadOnlyList<AIFunction>? mcpTools = null, bool mcpEnabled = false, bool safeEdits = true, IReadOnlyList<AIFunction>? gitTools = null, bool gitEnabled = false, IReadOnlyList<AIFunction>? shellTools = null, bool shellEnabled = false, ProcessRegistry? processes = null, bool shellBridge = false, bool shellPolice = true, IReadOnlyList<AIFunction>? obsidianTools = null, bool obsidianEnabled = false, IReadOnlyList<AIFunction>? sqlTools = null, bool sqlEnabled = false, IReadOnlyList<AIFunction>? comfyTools = null, bool comfyEnabled = false, bool shellNative = false)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(memoryTools);
        ArgumentNullException.ThrowIfNull(standingTools);
        ProjectNotes? project = null;
        IReadOnlyList<AIFunction> skillTools = [];
        IReadOnlyList<Skill>? catalog = null;
        if (skills is { Enabled: true })
        {
            skills.Catalog.Scan(skills.External);
            project = skills.ProjectFile ? skills.Project.ReadNotes() : null;
            catalog = skills.Catalog.Skills;
            skillTools = catalog.Count > 0 ? skills.Tools : skills.Tools.Where(t => !string.Equals(t.Name, LoadSkillTool.ToolName, StringComparison.Ordinal)).ToList();
        }

        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(operata);
        ArgumentNullException.ThrowIfNull(vocalia);
        assistant.MaxToolIterations = maxToolIterations;
        assistant.ContextGuard = contextGuard;
        if (!toolsEnabled)
        {
            assistant.Tools = [];
            assistant.OpeningCalls = [];
            assistant.History.SystemPrompt = Assistant.SystemPrompt(speechOutput, memoryEnabled ? memory.Snapshot() : null, persona.Read(), operata.Read(), vocalia.Read(), tools: false, project: project, markdown: markdown);
            return;
        }

        if (disabledTools is { Count: > 0 })
        {
            // The /tools list (2026-09-19): every group loses its switched-off names first, so what follows reads an emptied group as its switch off.
            standingTools = Without(standingTools, disabledTools);
            fileTools = fileTools is null ? null : Without(fileTools, disabledTools);
            webTools = webTools is null ? null : Without(webTools, disabledTools);
            gitTools = gitTools is null ? null : Without(gitTools, disabledTools);
            shellTools = shellTools is null ? null : Without(shellTools, disabledTools);
            obsidianTools = obsidianTools is null ? null : Without(obsidianTools, disabledTools);
            sqlTools = sqlTools is null ? null : Without(sqlTools, disabledTools);
            comfyTools = comfyTools is null ? null : Without(comfyTools, disabledTools);
            memoryTools = Without(memoryTools, disabledTools);
            skillTools = Without(skillTools, disabledTools);
            sessionTools = sessionTools is null ? null : Without(sessionTools, disabledTools);
            askTools = askTools is null ? null : Without(askTools, disabledTools);
            mcpTools = mcpTools is null ? null : Without(mcpTools, disabledTools);
        }

        // restore rides only with File safe edits on (later still on 2026-09-20): cut ahead of the group decision, so restore alone left on reads as the group emptied.
        fileTools = fileTools is null ? null : FileToolsFor(fileTools, safeEdits);
        bool files = filesEnabled && fileTools is { Count: > 0 };
        // The timer sentence rides only with a timer tool (2026-09-20): headless has none, the pane loses all three on /tools.
        bool timers = standingTools.Any(t => t is StartTimerTool or StopTimerTool or ListTimersTool);
        // The download tool rides the web list only while the file tools are offered (2026-09-18).
        webTools = webTools is null ? null : WebToolsFor(webTools, files);
        bool web = webEnabled && webTools is { Count: > 0 };
        bool download = web && webTools!.Any(t => t is DownloadFileTool);
        // The delete/restore clause of the file rule rides only while delete is offered (2026-09-20; off in a fresh profile until later on 2026-09-21).
        bool delete = files && fileTools!.Any(t => t is DeleteTool);
        // … and says what delete does: into .trash, or gone for good while File safe edits is off (2026-09-20, safeEdits).
        // The rule quotes the caps the offered tool itself reads, so the two never disagree.
        AskLimits? ask = askTools is { Count: > 0 } ? askTools.OfType<AskUserTool>().FirstOrDefault()?.Limits ?? AskLimits.Default : null;
        IReadOnlyList<AIFunction> offered = files ? [.. standingTools, .. fileTools!] : standingTools;
        // The git tools right after the file tools (2026-09-20): the sandbox's tools together, the setting Git native tools a per-group offer.
        bool git = gitEnabled && gitTools is { Count: > 0 };
        offered = git ? [.. offered, .. gitTools!] : offered;
        // The shell tools right after the git tools (2026-09-21): the setting Shell command policy is the group's switch; execute_code rides only with an interpreter to run.
        shellTools = shellTools is null ? null : ShellToolsFor(shellTools);
        bool shell = shellEnabled && shellTools is { Count: > 0 };
        offered = shell ? [.. offered, .. shellTools!] : offered;
        // The vault tools after the shell tools (2026-09-22): the setting Obsidian tools and a vault set are the group's switch.
        bool obsidian = obsidianEnabled && obsidianTools is { Count: > 0 };
        offered = obsidian ? [.. offered, .. obsidianTools!] : offered;
        // The vault rule's delete sentence rides only while vault_delete is offered (later on 2026-09-22): Obsidian allow delete on, the tool not switched off.
        bool obsidianDelete = obsidian && obsidianTools!.Any(t => t is VaultDeleteTool);
        // The SQL tools after the vault tools (2026-09-23): the setting SQL tools and a connection in sql.json are the group's switch.
        bool sql = sqlEnabled && sqlTools is { Count: > 0 };
        offered = sql ? [.. offered, .. sqlTools!] : offered;
        // The image tools after the SQL tools (2026-09-24): ComfyUI tools, a URL and a workflow are the group's switch; no rule — the description carries the workflows and the prompt styles.
        bool comfy = comfyEnabled && comfyTools is { Count: > 0 };
        offered = comfy ? [.. offered, .. comfyTools!] : offered;
        // The shell rule's execute_code sentence promises neon_tools only while the setting Shell tool bridge is on (later on 2026-09-21).
        bool bridge = shell && shellBridge;
        // … and its head says the shell stays under the working directory only while the setting Shell police outside paths is on (2026-09-22); off, it says a command starts there and no more.
        bool police = !shell || shellPolice;
        IReadOnlyList<AIFunction> tools = (web, memoryEnabled) switch
        {
            (true, true) => [.. offered, .. webTools!, .. memoryTools],
            (true, false) => [.. offered, .. webTools!],
            (false, true) => [.. offered, .. memoryTools],
            _ => offered,
        };
        tools = skillTools.Count > 0 ? [.. tools, .. skillTools] : tools;
        // The session tool after the skills (2026-09-18): the setting Session tool, a per-group offer.
        bool sessions = sessionsEnabled && sessionTools is { Count: > 0 };
        tools = sessions ? [.. tools, .. sessionTools!] : tools;
        // The MCP servers' tools after the session tool (2026-09-20): the setting MCP servers, a per-group offer over what is connected.
        bool mcp = mcpEnabled && mcpTools is { Count: > 0 };
        tools = mcp ? [.. tools, .. mcpTools!] : tools;
        assistant.Tools = ask is not null ? [.. tools, .. askTools!] : tools;
        // run_command learns the turn's offer (Shell prefer native tools, 2026-09-26): what it may send a line back to, and the once-a-turn rule starts over.
        (shell ? shellTools!.OfType<RunCommandTool>().FirstOrDefault() : null)?.BeginTurn(assistant.Tools.Select(t => t.Name));
        // … and the rules say so after the shell sentence, naming the groups offered.
        bool native = shell && shellNative;

        // An assistant built without the clock or the sandbox (tests over other tools, or the
        // file tools switched off) opens without that call.
        var clock = offered.FirstOrDefault(t => string.Equals(t.Name, GetCurrentTimeTool.ToolName, StringComparison.Ordinal));
        var cwd = offered.OfType<GetWorkingDirectoryTool>().FirstOrDefault();
        var opening = new List<Assistant.OpeningCall>(3);
        if (clock is not null)
        {
            opening.Add(new(clock, Assistant.OpeningClockCallId));
        }

        if (cwd is not null)
        {
            opening.Add(new(cwd, Assistant.OpeningCwdCallId));
            // The seeded path is kept current: after /cwd or the settings row changed it since the
            // first message, the result's text is replaced in place, so the model never holds a
            // stale path and no second pair is added. Silent: /cwd already printed the new path.
            assistant.History.TryReplaceToolResult(Assistant.OpeningCwdCallId, cwd.Describe());
        }

        // The memories last (2026-09-17): the freshest context before the first reply, where a
        // first-turn question finds them — a list far back in the system prompt went unread. The
        // result is kept current the cwd way, so a save_memory, /remember, /memory, /memory forget or
        // /memory copy since the first message is in the next request. Memory switched on
        // mid-conversation seeds nothing (as File tools does); the model has the tool.
        var recall = memoryEnabled ? memoryTools.OfType<RecallMemoryTool>().FirstOrDefault() : null;
        if (recall is not null)
        {
            opening.Add(new(recall, Assistant.OpeningMemoryCallId));
            assistant.History.TryReplaceToolResult(Assistant.OpeningMemoryCallId, recall.Describe());
        }

        assistant.OpeningCalls = opening;
        // The notified exits since the last turn ride in as seeded polls (2026-09-21), on every turn, while process is offered.
        assistant.PendingCalls = processes is null ? [] : PendingProcessPolls(processes, assistant.Tools);
        assistant.History.SystemPrompt = Assistant.SystemPrompt(speechOutput, memoryEnabled ? memory.Snapshot() : null, persona.Read(), operata.Read(), vocalia.Read(), web: web, files: files, ask: ask, project: project, skills: catalog, markdown: markdown, sessions: sessions, download: download, recall: recall is not null, delete: delete, mcp: mcp, safeEdits: safeEdits, timers: timers, git: git, shell: shell, bridge: bridge, police: police, obsidian: obsidian, obsidianDelete: obsidianDelete, sql: sql, native: native);
    }

    /// <summary>
    /// <paramref name="tools"/> less every one whose name is in <paramref name="disabled"/> (the
    /// <c>/tools</c> list, 2026-09-19). The same list itself when nothing is dropped. Pure.
    /// </summary>
    public static IReadOnlyList<AIFunction> Without(IReadOnlyList<AIFunction> tools, IReadOnlySet<string> disabled)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(disabled);
        return disabled.Count == 0 || !tools.Any(t => disabled.Contains(t.Name)) ? tools : tools.Where(t => !disabled.Contains(t.Name)).ToList();
    }

    /// <summary>
    /// Whether a turn's reply is <em>asked for</em> as light Markdown (<see cref="Assistant.MarkdownRule"/>):
    /// the setting <c>Transcript markdown</c>, the pane on the screen, and no speaker — a spoken turn
    /// keeps the plain-text rule, since the voice directive forbids Markdown and the prompt must not
    /// contradict itself. The screen is a separate question (<see cref="StyledReply"/>): what a model
    /// writes anyway is styled whatever the prompt asked for. Pure; pinned by tests.
    /// </summary>
    public static bool MarkdownTurn(bool transcriptMarkdown, bool paneEnabled, bool spoken) =>
        transcriptMarkdown && paneEnabled && !spoken;

    /// <summary>
    /// Whether a turn's reply is <em>shown</em> as styled Markdown (the pane's live slot): the setting
    /// and the pane, spoken or not — a model told to reply in plain text still leaks a fence or a
    /// <c>**bold**</c> now and then (a Svelte sample under TTS, 2026-09-16), and the spoken text is
    /// stripped of the markers anyway (<see cref="Speech.SpeakableText"/>), so the screen may as well
    /// read well. Pure; pinned by tests.
    /// </summary>
    public static bool StyledReply(bool transcriptMarkdown, bool paneEnabled) =>
        transcriptMarkdown && paneEnabled;

    /// <summary>
    /// What <see cref="PrepareTurn"/> needs for the skills: the catalog to rescan, the two tools
    /// (<see cref="SkillTools"/>), the project file, and the three settings as of this turn
    /// (<c>Agent skills</c>, <c>Use external skills</c>, and <c>Project file</c> — whether the notes
    /// are read at all, later on 2026-09-19). Shared with headless.
    /// </summary>
    public sealed record SkillsForTurn(SkillCatalog Catalog, IReadOnlyList<AIFunction> Tools, ProjectFile Project, bool Enabled, bool External, bool ProjectFile = true);

    /// <summary>The skill tools (2026-09-16): <c>load_skill</c> over the catalog and <c>skill_editor</c> over the live roots and the live external switch (whether <c>.agents\skills</c> is read, so a skill there blocks its name). Shared with headless.</summary>
    public static IReadOnlyList<AIFunction> SkillTools(SkillCatalog catalog, Func<SkillRoots> roots, Func<bool> external) => new AIFunction[]
    {
        new LoadSkillTool(catalog),
        new SkillEditorTool(roots, external),
    };

    /// <summary>
    /// The tool loop's guard for a turn (<see cref="Assistant.ContextGuard"/>): the window
    /// (<see cref="LlmSession.ContextLength"/> — the LLM tab's figure else the server's), the share
    /// <c>LLM auto compact (%)</c> and the mode <c>LLM tool compact type</c>; null while the window is unknown.
    /// </summary>
    public static Assistant.TurnContextGuard? ContextGuardFor(AppSettingsData effective, ContextLength? window)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return window is { Tokens: > 0 } w ? new Assistant.TurnContextGuard(w.Tokens, effective.LlmAutoCompactPercent, ToolCompactType.Resolve(effective), SkillCompactMode.Resolve(effective)) : null;
    }

    public static string RememberedNotice(string text) => $"({NoticeGlyphs.Memory}remembered: {text})";

    /// <summary>After <c>/settings</c> flipped <c>LLM offer tools</c>: the conversation went with it. Pinned.</summary>
    public static string ToolsChangedNotice(bool on) => on ? "(LLM offer tools on; conversation cleared)" : "(LLM offer tools off; conversation cleared)";

    /// <summary>Under the rule <c>/new</c> draws: the conversation is forgotten, the transcript stays. Pinned; headless prints it too.</summary>
    public const string NewConversationNotice = "(new conversation)";

    /// <summary>After <c>/persona</c> opened an existing file. Pinned.</summary>
    public const string PersonaOpenedNotice = "(" + NoticeGlyphs.Profile + "opened persona.md in your editor; save it and the next reply uses it)";

    /// <summary>After <c>/persona</c> created the file with the default persona and opened it. Pinned.</summary>
    public const string PersonaCreatedNotice = "(" + NoticeGlyphs.Profile + "created persona.md with the default persona and opened it in your editor; edit it, save, and the next reply uses it; /persona reset goes back to the default)";

    public static string PersonaOpenFailedError(string detail) => $"Could not open persona.md: {detail}";

    /// <summary>After <c>/operata</c> opened an existing file. Pinned.</summary>
    public const string OperataOpenedNotice = "(" + NoticeGlyphs.Operata + "opened operata.md in your editor; save it and the next reply uses it)";

    /// <summary>After <c>/operata</c> created the file with the default operating rules and opened it. Pinned.</summary>
    public const string OperataCreatedNotice = "(" + NoticeGlyphs.Operata + "created operata.md with the default operating rules and opened it in your editor; edit it, save, and the next reply uses it; /operata reset goes back to the default)";

    public static string OperataOpenFailedError(string detail) => $"Could not open operata.md: {detail}";

    /// <summary>After <c>/vocalia</c> opened an existing file. Pinned.</summary>
    public const string VocaliaOpenedNotice = "(" + NoticeGlyphs.Vocalia + "opened vocalia.md in your editor; save it and the next spoken reply uses it)";

    /// <summary>After <c>/vocalia</c> created the file with the default voice directive and opened it. Pinned.</summary>
    public const string VocaliaCreatedNotice = "(" + NoticeGlyphs.Vocalia + "created vocalia.md with the default voice directive and opened it in your editor; edit it, save, and the next spoken reply uses it; /vocalia reset goes back to the default)";

    public static string VocaliaOpenFailedError(string detail) => $"Could not open vocalia.md: {detail}";

    /// <summary>The word after <c>/persona</c>, <c>/operata</c> or <c>/vocalia</c> that removes the file (2026-09-16).</summary>
    public const string ResetWord = "reset";

    /// <summary>The word after <c>/persona</c>, <c>/operata</c> or <c>/vocalia</c> that copies the file into another profile: <c>copy &lt;profile&gt; [force]</c> (2026-09-21).</summary>
    public const string CopyWord = "copy";

    /// <summary>The usage line for a prompt-file command with words that are neither <see cref="ResetWord"/> nor <c>copy &lt;profile&gt; [force]</c>. Pinned.</summary>
    public static string PromptFileUsageError(string command, string fileName) => $"{command} takes nothing (open {fileName} in your editor), {ResetWord}, or {CopyWord} <profile> [{ForceWord}].";

    /// <summary>The copy names the loaded profile as its target. Pinned.</summary>
    public static string PromptFileCopySelfError(string command) => $"{command} {CopyWord} copies into another profile; that one is loaded.";

    /// <summary>A copy with no file to copy: the default is in use here. Pinned.</summary>
    public static string PromptFileNothingToCopyNotice(string fileName, string defaultLabel) => $"({NoticeGlyphs.PromptFile(fileName)}{fileName} is not there; the default {defaultLabel} is in use, so there is nothing to copy)";

    /// <summary>The target has the file and <c>force</c> was not given (the user's rule, 2026-09-21): an error naming the way past it, nothing written. Pinned.</summary>
    public static string PromptFileTargetExistsError(string command, string fileName, string profile) => $"\"{profile}\" already has a {fileName}; {command} {CopyWord} {profile} {ForceWord} replaces it.";

    /// <summary>The question before a copy (the yes/no pane's title; <see cref="TypedConfirm"/> where menus cannot open); <paramref name="replacing"/> when the target's file goes under it. Pinned.</summary>
    public static string PromptFileCopyPrompt(string fileName, string profile, bool replacing) =>
        replacing ? $"Replace \"{profile}\"'s {fileName} with this one?" : $"Copy {fileName} into \"{profile}\"?";

    /// <summary><c>(copied persona.md into "work")</c>, or <c>(replaced "work"'s persona.md)</c>. Pinned.</summary>
    public static string PromptFileCopiedNotice(string fileName, string profile, bool replacing) =>
        replacing ? $"(replaced \"{profile}\"'s {fileName})" : $"(copied {fileName} into \"{profile}\")";

    public static string PromptFileCopyFailedError(string fileName, string detail) => $"Could not copy {fileName}: {detail}";

    /// <summary>The question before a <c>/persona reset</c> (the yes/no pane's title; <see cref="TypedConfirm"/> where menus cannot open); <c>y</c> or <c>yes</c> removes, anything else keeps. Pinned.</summary>
    public static string PromptFileResetPrompt(string fileName, string defaultLabel) => $"{NoticeGlyphs.PromptFile(fileName)}Remove {fileName} and go back to the default {defaultLabel}?";

    /// <summary>The notice after the file went; <paramref name="spoken"/> for the voice directive, which only a spoken reply carries. Pinned.</summary>
    public static string PromptFileResetNotice(string fileName, string defaultLabel, bool spoken) => $"({NoticeGlyphs.PromptFile(fileName)}removed {fileName}; the next {(spoken ? "spoken " : "")}reply uses the default {defaultLabel})";

    /// <summary>The notice for a reset with no file to remove. Pinned.</summary>
    public static string PromptFileAbsentNotice(string fileName, string defaultLabel) => $"({NoticeGlyphs.PromptFile(fileName)}{fileName} is not there; the default {defaultLabel} is already in use)";

    public static string PromptFileResetFailedError(string fileName, string detail) => $"Could not remove {fileName}: {detail}";

    /// <summary>
    /// <c>/persona</c>, <c>/operata</c> and <c>/vocalia</c> with their argument: nothing opens the
    /// file (<see cref="OpenPromptFile"/>); <see cref="ResetWord"/> removes it after a confirmation
    /// (the user's call, 2026-09-16: the file may hold a hand-written text and the profile folder
    /// has no trash) so the default is back at the next turn; <c>copy &lt;profile&gt; [force]</c>
    /// (2026-09-21, the user's ask) is <see cref="CopyPromptFileAsync"/>; any other words are the usage line.
    /// </summary>
    private async Task HandlePromptFileAsync(PromptFile file, string command, string args, string createdNotice, string openedNotice, Func<string, string> openFailedError, bool spoken, CancellationToken cancellationToken)
    {
        string[] words = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            OpenPromptFile(file, createdNotice, openedNotice, openFailedError);
            return;
        }

        string fileName = file.CurrentFileName;
        if (words[0].Equals(CopyWord, StringComparison.OrdinalIgnoreCase) && words.Length is 2 or 3)
        {
            bool force = words.Length == 3 && words[2].Equals(ForceWord, StringComparison.OrdinalIgnoreCase);
            if (words.Length == 2 || force)
            {
                await CopyPromptFileAsync(file, command, words[1], force, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (words.Length != 1 || !words[0].Equals(ResetWord, StringComparison.OrdinalIgnoreCase))
        {
            _transcript.Error(PromptFileUsageError(command, fileName));
            return;
        }

        if (!File.Exists(file.FilePath))
        {
            _transcript.Notice(PromptFileAbsentNotice(fileName, file.DefaultLabel));
            return;
        }

        if (!await ConfirmAsync(PromptFileResetPrompt(fileName, file.DefaultLabel), cancellationToken).ConfigureAwait(false))
        {
            _transcript.Notice(KeptNotice);
            return;
        }

        try
        {
            file.Delete();
            _transcript.Notice(PromptFileResetNotice(fileName, file.DefaultLabel, spoken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _transcript.Error(PromptFileResetFailedError(fileName, ex.Message));
        }
    }

    /// <summary>
    /// <c>/persona copy &lt;profile&gt; [force]</c> and its siblings (2026-09-21, the user's ask): the loaded
    /// profile's file into another profile's folder, byte for byte (<see cref="PromptFile.CopyTo"/>). The
    /// target is named as <c>/profile</c> resolves it and is never the loaded one; no file here is a notice
    /// (the default is in use, nothing to copy); a file already there is an error unless <c>force</c> (the
    /// user's rule: a hand-written text, no trash); either way the copy asks first (the user's call, like
    /// <c>/memory copy</c>). <c>force</c> on a target without the file is a plain copy, and reads as one.
    /// </summary>
    private async Task CopyPromptFileAsync(PromptFile file, string command, string typed, bool force, CancellationToken cancellationToken)
    {
        string fileName = file.CurrentFileName;
        string home = _settings.StorageDirectory;
        if (Profiles.Resolve(home, typed) is not { } target)
        {
            _transcript.Error(ProfileMissingError(typed));
            return;
        }

        if (Profiles.NameEquals(target, _settings.ProfileName))
        {
            _transcript.Error(PromptFileCopySelfError(command));
            return;
        }

        if (!File.Exists(file.FilePath))
        {
            _transcript.Notice(PromptFileNothingToCopyNotice(fileName, file.DefaultLabel));
            return;
        }

        string directory = Profiles.Directory(home, target);
        bool replacing = File.Exists(Path.Combine(directory, fileName));
        if (replacing && !force)
        {
            _transcript.Error(PromptFileTargetExistsError(command, fileName, target));
            return;
        }

        if (!await ConfirmAsync(PromptFileCopyPrompt(fileName, target, replacing), cancellationToken).ConfigureAwait(false))
        {
            _transcript.Notice(KeptNotice);
            return;
        }

        try
        {
            file.CopyTo(directory);
            _transcript.Notice(PromptFileCopiedNotice(fileName, target, replacing));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _transcript.Error(PromptFileCopyFailedError(fileName, ex.Message));
        }
    }

    /// <summary>
    /// <c>/persona</c>, <c>/operata</c> and <c>/vocalia</c>: make sure the file exists (seeded with its default), then
    /// hand it to the editor. Nothing waits; the next turn reads whatever was saved.
    /// </summary>
    private void OpenPromptFile(PromptFile file, string createdNotice, string openedNotice, Func<string, string> openFailedError)
    {
        try
        {
            bool created = file.EnsureExists();
            _openFile(file.FilePath);
            _transcript.Notice(created ? createdNotice : openedNotice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _transcript.Error(openFailedError(ex.Message));
        }
    }

    public static string AlreadyRememberedNotice(string text) => $"({NoticeGlyphs.Memory}already remembered: {text})";

    /// <summary>The question before a <c>/memory forget</c> (the yes/no pane's title; <see cref="TypedConfirm"/> where menus cannot open, the answer typed — <c>y</c> or <c>yes</c> clears, anything else keeps). Pinned.</summary>
    public static string ForgetPrompt(int count) => $"{NoticeGlyphs.Memory}Forget {Memories(count)}?";

    public static string ForgotNotice(int count) => $"({NoticeGlyphs.Memory}forgot {Memories(count)})";

    public static string ForgetFailedError(string detail) => $"Could not clear the memories: {detail}";

    // ── /draft (2026-09-19) ─────────────────────────────────────────────────

    /// <summary>The hint row while the editor holds the draft (the <see cref="ListeningLabel"/> shape). Pinned.</summary>
    public const string DraftingLabel = "drafting in your editor…  save and close to send   ESC = cancel";

    /// <summary>The file came back empty, whitespace alone, or never saved: nothing sent. Pinned.</summary>
    public const string DraftEmptyNotice = "(nothing sent: the draft is empty)";

    /// <summary>ESC or Ctrl+C under the wait: the editor stays open, the file is gone, nothing sent. Pinned.</summary>
    public const string DraftCancelledNotice = "(draft cancelled; nothing sent)";

    /// <summary>The screen was built with no editor seam (tests, a host with none). Pinned.</summary>
    public const string DraftUnavailableError = "/draft needs an editor; not available here.";

    /// <summary>The temp file could not be made or read, or the editor could not be started. Pinned.</summary>
    public static string DraftFailedError(string detail) => $"Could not open the draft: {detail}";

    /// <summary><c>Draft opened: neon-draft-….txt in code --wait</c> / <c>… in the shell's default editor</c>, at Debug under <see cref="AppCategory"/>. Pinned.</summary>
    public static string DraftOpenedLogLine(string name, string editorCommand) =>
        "Draft opened: " + name + " in " + (string.IsNullOrWhiteSpace(editorCommand) ? DefaultEditorWords : editorCommand.Trim());

    /// <summary>How <see cref="DraftOpenedLogLine"/> names a blank <c>Draft editor</c>. Pinned.</summary>
    public const string DefaultEditorWords = "the shell's default editor";

    /// <summary><c>Draft sent: 3 lines, 120 chars</c>. Pinned.</summary>
    public static string DraftSentLogLine(int lines, int chars) =>
        string.Create(CultureInfo.InvariantCulture, $"Draft sent: {lines} line{(lines == 1 ? "" : "s")}, {chars} char{(chars == 1 ? "" : "s")}");

    /// <summary><c>Draft dropped: neon-draft-….txt (empty)</c> / <c>(cancelled)</c>. Pinned.</summary>
    public static string DraftDroppedLogLine(string name, string reason) => "Draft dropped: " + name + " (" + reason + ")";

    /// <summary>The reasons <see cref="DraftDroppedLogLine"/> names. Pinned.</summary>
    public const string DraftEmptyReason = "empty";
    public const string DraftCancelledReason = "cancelled";

    /// <summary>
    /// <c>/draft</c> (2026-09-19, the user's ask): an empty <see cref="DraftFile"/> in the temp
    /// folder handed to the editor (<c>_editDraft</c>: the shell's default for <c>.txt</c>, or the
    /// <c>Draft editor</c> command line) and waited for under the hint row's spinner — ESC or
    /// Ctrl+C cancels the wait alone (<see cref="WaitUnderWatchAsync"/> over
    /// <see cref="KeySource.IsTurnCancel"/>; the editor is left open). Back, the file is read and
    /// removed whatever it holds; blank (<see cref="DraftFile.IsBlank"/> after
    /// <see cref="PasteText.Normalize"/>) is <see cref="DraftEmptyNotice"/>, else its events wait in
    /// <see cref="_draftReplay"/> for the idle loop, which replays them through the next read and
    /// sends the result as a message. Nothing here writes a transcript row: the replayed Enter does.
    /// </summary>
    private async Task HandleDraftAsync(CancellationToken cancellationToken)
    {
        if (_editDraft is null)
        {
            _transcript.Error(DraftUnavailableError);
            return;
        }

        string path;
        try
        {
            path = DraftFile.Create(Path.GetTempPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _transcript.Error(DraftFailedError(ex.Message));
            return;
        }

        string editorCommand = _effective().DraftEditor;
        string name = DraftFile.Name(path);
        DiagnosticLog.Debug(AppCategory, DraftOpenedLogLine(name, editorCommand));
        string text;
        try
        {
            if (await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(DraftingLabel, async () =>
                {
                    await _editDraft(path, editorCommand, token).ConfigureAwait(false);
                    return true;
                }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false))
            {
                DraftFile.TryDelete(path);
                DiagnosticLog.Debug(AppCategory, DraftDroppedLogLine(name, DraftCancelledReason));
                _transcript.Notice(DraftCancelledNotice);
                return;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DraftFile.TryDelete(path);
            _transcript.Error(DraftFailedError(ex.Message));
            return;
        }

        DraftFile.TryDelete(path);
        string normalized = PasteText.Normalize(text);
        if (DraftFile.IsBlank(normalized))
        {
            DiagnosticLog.Debug(AppCategory, DraftDroppedLogLine(name, DraftEmptyReason));
            _transcript.Notice(DraftEmptyNotice);
            return;
        }

        DiagnosticLog.Debug(AppCategory, DraftSentLogLine(normalized.Count(c => c == '\n') + 1, normalized.Length));
        _draftReplay = DraftFile.Events(normalized);
    }

    // ── /memory copy (2026-09-17 as /memcopy, the word folded in 2026-09-22) ─

    /// <summary>The last word of <c>/memory copy</c> and <c>/cmdcopy</c> that replaces the target's list instead of adding to it.</summary>
    public const string OverwriteWord = "overwrite";

    public const string MemoryCopySelfError = "/memory copy copies into another profile; that one is loaded.";

    public const string MemoryCopyNothingNotice = "(" + NoticeGlyphs.Memory + "nothing to copy: this profile has no memory)";

    /// <summary>The question before a copy (the yes/no pane's title; <see cref="TypedConfirm"/> where menus cannot open). Pinned.</summary>
    public static string MemoryCopyPrompt(int count, string profile, bool overwrite) =>
        NoticeGlyphs.Memory + (overwrite ? $"Replace \"{profile}\"'s memory with these {Memories(count)}?" : $"Copy {Memories(count)} into \"{profile}\"?");

    /// <summary>
    /// <c>(12 memories copied into "work")</c>; <c>(9 memories copied into "work", 3 already there, 2 dropped: its memory is full)</c>;
    /// an overwrite reads <c>replaced "work"'s memory with 12 memories</c>. Pinned.
    /// </summary>
    public static string MemoryCopiedNotice(MemoryImportResult result, string profile, bool overwrite)
    {
        var sb = new StringBuilder("(" + NoticeGlyphs.Memory);
        sb.Append(overwrite ? $"replaced \"{profile}\"'s memory with {Memories(result.Added)}" : $"{Memories(result.Added)} copied into \"{profile}\"");
        if (result.Duplicates > 0)
        {
            sb.Append(", ").Append(result.Duplicates.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" already there");
        }

        if (result.Dropped > 0)
        {
            sb.Append(", ").Append(result.Dropped.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" dropped: its memory is full");
        }

        return sb.Append(')').ToString();
    }

    public static string MemoryCopyFailedError(string detail) => $"Could not write the profile's memory: {detail}";

    // ── /sessions (2026-09-18) ──────────────────────────────────────────────

    public const string SessionUsageError = "/sessions lists the sessions, or /sessions <id> | purge <id> | purge older <age> | purge all | title <text>";

    public static string SessionMissingError(long id) => $"No session {SessionText.Id(id)}; /sessions lists them.";

    public static string SessionRestoreFailedError(long id, string detail) => $"Could not restore session {SessionText.Id(id)}: {detail}";

    /// <summary>Under the fresh banner, ahead of the replayed rows: <c>(restored session #12 "Title" · 12 turns · 2026-09-18 14:05)</c>.</summary>
    public static string SessionRestoredNotice(SessionSummary session, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(session);
        return $"({NoticeGlyphs.Session}restored session {SessionText.Id(session.Id)} \"{session.Title}\" · {SessionText.Turns(session.Turns)} · {SessionText.Moment(session.UpdatedAt, zone)})";
    }

    public static string PurgeOlderPrompt(TimeSpan age, int count) => $"{NoticeGlyphs.Session}Purge {SessionText.Sessions(count)} older than {SessionText.Age(age)}?";

    public static string PurgeAllPrompt(int count) => $"{NoticeGlyphs.Session}Purge all {SessionText.Sessions(count)}?";

    public static string SessionsPurgedNotice(int count) => $"({TrashGlyph}purged {SessionText.Sessions(count)})";

    public static string SessionsPurgedOlderNotice(int count, TimeSpan age) => $"({TrashGlyph}purged {SessionText.Sessions(count)} older than {SessionText.Age(age)})";

    public static string NoSessionsOlderNotice(TimeSpan age) => $"({NoticeGlyphs.Session}no sessions older than {SessionText.Age(age)})";

    public const string SessionNoneYetNotice = "(" + NoticeGlyphs.Session + "no session yet: send a message first)";

    /// <summary>
    /// The <c>/sessions</c> grammar: nothing = the pane; <c>12</c> or <c>#12</c> = restore; <c>purge 12</c>,
    /// <c>purge older 30</c> (or <c>12h</c>, <c>90m</c>, <c>1d 6h</c>: the rest of the line is one
    /// <see cref="SessionText.TryParseAge"/> age, 2026-09-21), <c>purge all</c>; <c>title</c> and the
    /// rest of the line. Case-insensitive words; an id is a positive whole number. Pure; pinned by tests.
    /// </summary>
    public static SessionAction ParseSessionArgs(string args)
    {
        string text = (args ?? "").Trim();
        var tokens = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return new(SessionActionKind.Pane);
        }

        if (tokens[0].Equals(SessionTitleWord, StringComparison.OrdinalIgnoreCase))
        {
            string title = text[SessionTitleWord.Length..].Trim();
            return title.Length > 0 ? new(SessionActionKind.Title, Text: title) : new(SessionActionKind.Invalid);
        }

        if (tokens.Length == 1)
        {
            return TryParseSessionId(tokens[0], out long id) ? new(SessionActionKind.Restore, id) : new(SessionActionKind.Invalid);
        }

        if (!tokens[0].Equals(SessionPurgeWord, StringComparison.OrdinalIgnoreCase))
        {
            return new(SessionActionKind.Invalid);
        }

        switch (tokens.Length)
        {
            case 2 when tokens[1].Equals(SessionAllWord, StringComparison.OrdinalIgnoreCase):
                return new(SessionActionKind.PurgeAll);
            case 2 when TryParseSessionId(tokens[1], out long id):
                return new(SessionActionKind.Purge, id);
            case >= 3 when tokens[1].Equals(SessionOlderWord, StringComparison.OrdinalIgnoreCase)
                && SessionText.TryParseAge(string.Join(' ', tokens.Skip(2)), out var age):
                return new(SessionActionKind.PurgeOlder, Age: age);
            default:
                return new(SessionActionKind.Invalid);
        }
    }

    /// <summary><c>12</c> or <c>#12</c>, a positive whole number.</summary>
    public static bool TryParseSessionId(string token, out long id)
    {
        ArgumentNullException.ThrowIfNull(token);
        string digits = token.StartsWith('#') ? token[1..] : token;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary><c>/sessions</c>: the pane (a <c>restore</c> picked there lands here), or the typed forms of <see cref="ParseSessionArgs"/>. At the idle line only (refused mid-turn with an argument; the pane alone mid-turn).</summary>
    private async Task HandleSessionAsync(string args, CancellationToken cancellationToken)
    {
        var action = ParseSessionArgs(args);
        switch (action.Kind)
        {
            case SessionActionKind.Pane:
                if (await _sessionsMenu.ShowAsync(cancellationToken).ConfigureAwait(false) is { } picked)
                {
                    RestoreSession(picked);
                }
                else
                {
                    RefreshSessionTitle();   // the pane may have renamed this conversation
                }

                break;
            case SessionActionKind.Restore:
                RestoreSession(action.Id);
                break;
            case SessionActionKind.Purge:
                await PurgeSessionAsync(action.Id, cancellationToken).ConfigureAwait(false);
                break;
            case SessionActionKind.PurgeOlder:
                await PurgeOlderSessionsAsync(action.Age, cancellationToken).ConfigureAwait(false);
                break;
            case SessionActionKind.PurgeAll:
                await PurgeAllSessionsAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SessionActionKind.Title:
                TitleSession(action.Text);
                break;
            default:
                _transcript.Error(SessionUsageError);
                break;
        }

        DrainDiagnostics();
    }

    /// <summary>
    /// The stored session onto the screen: the conversation on screen forgotten as <c>/clear</c>
    /// forgets it (it is in the store already; no confirmation, the user's call), the screen
    /// redrawn, the history set to the stored messages with the <c>/skill</c> counter
    /// (<see cref="ConversationHistory.Restore"/>; the opening pairs are in the list, so the next turn
    /// keeps them current rather than seeding them again), then each turn replayed under the
    /// notice — the user's row, one <c>🛠️ N tool calls</c> line when the model called any, the reply
    /// through the live slot as <c>/speak</c> prints a file (styled as a live reply would be). The
    /// pictures of a turn are not redrawn. New turns append to the restored row. The one on screen
    /// already is <see cref="SessionsMenu.CurrentNotice"/>.
    /// </summary>
    private void RestoreSession(long id)
    {
        if (_sessionId == id)
        {
            _transcript.Notice(SessionsMenu.CurrentNotice);
            return;
        }

        if (_sessions.Load(id) is not { } record)
        {
            _transcript.Error(SessionMissingError(id));
            return;
        }

        List<ChatMessage> messages;
        try
        {
            messages = SessionHistory.FromJson(record.HistoryJson);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            _transcript.Error(SessionRestoreFailedError(id, ex.Message));
            return;
        }

        var effective = _effective();
        bool styled = StyledReply(effective.TranscriptMarkdown, _pane.Enabled);
        using (_pane.Batch())
        {
            RedrawScreen();
            _session.History.Restore(messages);
            DropQueue();
            _lastTrace = null;
            _learnTrace = null;
            _session.Usage.ResetConversation();
            _pictureStrip.Clear();
            _log.Clear();
            ForgetReading();
            _sessionId = id;
            _sessionTitle = record.Summary;
            _transcript.Notice(SessionRestoredNotice(record.Summary, _time.LocalTimeZone));
            DiagnosticLog.Info(SessionsCategory, SessionRestoredLogLine(record.Summary.Id, record.Summary.Turns));
            foreach (var turn in record.Turns)
            {
                _transcript.User(turn.UserText);
                if (turn.ToolCalls > 0)
                {
                    _transcript.ToolNote(SessionText.ToolCallsNote(turn.ToolCalls));
                }

                if (turn.ReplyText.Length > 0)
                {
                    _transcript.BeginAssistant(styled);
                    _transcript.AppendDelta(turn.ReplyText);
                    _transcript.EndAssistant();
                }

                _log.Add(turn.UserText, turn.ReplyText);
            }
        }
    }

    /// <summary><c>/sessions purge &lt;id&gt;</c>: the row and its turns, after a yes/no; the one on screen forgets its row (the next turn starts a new one).</summary>
    private async Task PurgeSessionAsync(long id, CancellationToken cancellationToken)
    {
        if (_sessions.Load(id) is not { } record)
        {
            _transcript.Error(SessionMissingError(id));
            return;
        }

        if (!await ConfirmAsync(SessionsMenu.PurgePrompt(record.Summary), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        if (_sessions.Purge(id))
        {
            if (_sessionId == id)
            {
                ForgetSession();
            }

            _transcript.Notice(SessionsMenu.PurgedNotice(id));
        }
        else
        {
            _transcript.Error(SessionsMenu.PurgeFailedError(id));
        }
    }

    /// <summary>
    /// <c>/sessions purge older &lt;age&gt;</c>: every session last updated longer ago, after a yes/no
    /// naming the count; an age of 0 is every session but one updated this instant. An age past the
    /// start of the calendar (a six-digit day count) means everything, not a throw from the subtraction.
    /// </summary>
    private async Task PurgeOlderSessionsAsync(TimeSpan age, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var cutoff = age > now - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : now - age;
        var sessions = _sessions.List(0);
        int count = sessions.Count(session => session.UpdatedAt < cutoff);
        if (count == 0)
        {
            _transcript.Notice(NoSessionsOlderNotice(age));
            return;
        }

        if (!await ConfirmAsync(PurgeOlderPrompt(age, count), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        int purged = _sessions.PurgeOlderThan(cutoff);
        if (_sessionId is { } current && sessions.Any(session => session.Id == current && session.UpdatedAt < cutoff))
        {
            ForgetSession();
        }

        _transcript.Notice(SessionsPurgedOlderNotice(purged, age));
    }

    /// <summary><c>/sessions purge all</c>: every session, after a yes/no naming the count; the conversation on screen goes on and starts a new row at its next turn.</summary>
    private async Task PurgeAllSessionsAsync(CancellationToken cancellationToken)
    {
        int count = _sessions.Count;
        if (count == 0)
        {
            _transcript.Notice(SessionsMenu.EmptyNotice);
            return;
        }

        if (!await ConfirmAsync(PurgeAllPrompt(count), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        int purged = _sessions.PurgeAll();
        ForgetSession();
        _transcript.Notice(SessionsPurgedNotice(purged));
    }

    /// <summary><c>/sessions title &lt;text&gt;</c>: the session on screen renamed by the user (never overwritten by the model's title afterwards); nothing to rename before its first turn.</summary>
    private void TitleSession(string text)
    {
        if (_sessionId is not { } id)
        {
            _transcript.Notice(SessionNoneYetNotice);
            return;
        }

        string title = SessionText.FirstLineTitle(text);
        if (_sessions.SetTitle(id, title, TitleSource.User))
        {
            RefreshSessionTitle();
            _transcript.Notice(SessionsMenu.RenamedNotice(title));
        }
        else
        {
            _transcript.Error(SessionsMenu.RenameFailedError(id));
        }
    }

    /// <summary>
    /// <c>/memory copy &lt;profile&gt; [overwrite]</c> (<c>/memcopy</c>, its own command, from
    /// 2026-09-17 until the word folded in on 2026-09-22): this profile's memory into another's,
    /// after a confirmation either way (the user's call). The target is named as <c>/profile</c>
    /// resolves it; <c>default</c> is an ordinary target, so any profile can push into it and it
    /// into any. Appending skips what the target already holds (<see cref="MemoryStore.Import"/>);
    /// the <c>Memory</c> switch has no say (a file operation, like <c>/memory forget</c>). Asked
    /// under a reply the copy runs there (the fold's call: every <c>/memory</c> form is a pane
    /// mid-turn, and this one only writes another profile's file), so the lines go through
    /// <see cref="_flow"/> and the write lands on the turn task, as <see cref="ForgetAsync"/>'s does.
    /// </summary>
    private async Task CopyMemoryAsync(string typed, bool overwrite, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        if (Profiles.Resolve(home, typed) is not { } target)
        {
            _flow.Error(ProfileMissingError(typed));
            return;
        }

        if (Profiles.NameEquals(target, _settings.ProfileName))
        {
            _flow.Error(MemoryCopySelfError);
            return;
        }

        var entries = _memory.EntriesSnapshot();
        if (entries.Count == 0)
        {
            _flow.Notice(MemoryCopyNothingNotice);
            return;
        }

        if (!await ConfirmAsync(MemoryCopyPrompt(entries.Count, target, overwrite), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        RunOrPost(() =>
        {
            try
            {
                var result = new MemoryStore(Profiles.Directory(home, target)).Import(entries, overwrite);
                _transcript.Notice(MemoryCopiedNotice(result, target, overwrite));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _transcript.Error(MemoryCopyFailedError(ex.Message));
            }
            finally
            {
                DrainDiagnostics();
            }
        });
    }

    private static string Memories(int count) => count == 1 ? "1 memory" : $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} memories";

    // ── /cmdcopy (2026-09-21) ───────────────────────────────────────────────

    public const string CmdCopyUsageError = "/cmdcopy takes a profile name, --history to copy the command history instead of the allowed commands, and overwrite to replace the target's: /cmdcopy <profile> [--history] [overwrite]";

    /// <summary>The <c>/cmdcopy</c> switch that copies the command history instead of the allowed commands (2026-09-25). Pinned.</summary>
    public const string HistorySwitch = "--history";

    public const string CmdCopySelfError = "/cmdcopy copies into another profile; that one is loaded.";

    public const string CmdCopyNothingNotice = "(nothing to copy: this profile has no allowed commands)";

    /// <summary>The question before a copy (the yes/no pane's title; <see cref="TypedConfirm"/> where menus cannot open). Pinned.</summary>
    public static string CmdCopyPrompt(int count, string profile, bool overwrite) =>
        overwrite ? $"Replace \"{profile}\"'s allowed commands with these {AllowedCommands(count)}?" : $"Copy {AllowedCommands(count)} into \"{profile}\"?";

    /// <summary>
    /// <c>(3 allowed commands copied into "work")</c>; <c>(2 allowed commands copied into "work", 1 already there)</c>;
    /// an overwrite reads <c>replaced "work"'s allowed commands with 3 allowed commands</c>. No "dropped" bucket:
    /// the list has no cap, unlike the memory's. Pinned.
    /// </summary>
    public static string CmdCopiedNotice(int added, int duplicates, string profile, bool overwrite)
    {
        var sb = new StringBuilder("(");
        sb.Append(overwrite ? $"replaced \"{profile}\"'s allowed commands with {AllowedCommands(added)}" : $"{AllowedCommands(added)} copied into \"{profile}\"");
        if (duplicates > 0)
        {
            sb.Append(", ").Append(duplicates.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" already there");
        }

        return sb.Append(')').ToString();
    }

    public static string CmdCopyFailedError(string detail) => $"Could not write the profile's settings: {detail}";

    /// <summary>
    /// <c>/cmdcopy &lt;profile&gt; [overwrite]</c> (2026-09-21, the user's ask): this profile's
    /// <c>Shell allowed commands</c> — the prefixes allowed for good on the approval pane — into
    /// another's, the grammar, the confirmation and the append-or-replace of <c>/memory copy</c>. The
    /// target's <c>profile.json</c> is read whole (<see cref="Profiles.ReadProfileFile"/>: a corrupt
    /// one is an error, never overwritten), the one field changed, the file written back
    /// (<see cref="Profiles.WriteProfileFile"/>); the loaded profile is never the target, so no
    /// pending save is at stake. Appending skips what the target holds (<see cref="CommandAllowList.Contains"/>);
    /// both lists come out normalised (<see cref="CommandAllowList.Merge"/>: lower case, sorted, no doubles).
    /// <c>--history</c> after the name (2026-09-25) copies the command history instead (<see cref="CopyCommandHistoryAsync"/>).
    /// </summary>
    private async Task HandleCmdCopyAsync(string args, CancellationToken cancellationToken)
    {
        string[] all = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // --history anywhere after the name (2026-09-25): the rest is the allowed commands' grammar as ever.
        string[] words = all.Where(word => !string.Equals(word, HistorySwitch, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool history = words.Length < all.Length;
        bool overwrite = words.Length == 2 && string.Equals(words[1], OverwriteWord, StringComparison.OrdinalIgnoreCase);
        // The switch once, and never in the name's place.
        bool misplaced = history && (all.Length - words.Length > 1 || string.Equals(all[0], HistorySwitch, StringComparison.OrdinalIgnoreCase));
        if (misplaced || words.Length == 0 || words.Length > 2 || (words.Length == 2 && !overwrite))
        {
            _transcript.Error(CmdCopyUsageError);
            return;
        }

        string home = _settings.StorageDirectory;
        if (Profiles.Resolve(home, words[0]) is not { } target)
        {
            _transcript.Error(ProfileMissingError(words[0]));
            return;
        }

        if (Profiles.NameEquals(target, _settings.ProfileName))
        {
            _transcript.Error(CmdCopySelfError);
            return;
        }

        if (history)
        {
            await CopyCommandHistoryAsync(home, target, overwrite, cancellationToken).ConfigureAwait(false);
            return;
        }

        var source = CommandAllowList.Merge(_settings.Current.ShellCommandAllowed, []);
        if (source.Count == 0)
        {
            _flow.Notice(CmdCopyNothingNotice);
            return;
        }

        if (!await ConfirmAsync(CmdCopyPrompt(source.Count, target, overwrite), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        try
        {
            string path = Profiles.ProfileFile(home, target);
            var data = Profiles.ReadProfileFile(path);
            int duplicates = overwrite ? 0 : source.Count(prefix => CommandAllowList.Contains(data.ShellCommandAllowed, prefix));
            data.ShellCommandAllowed = overwrite ? source : CommandAllowList.Merge(data.ShellCommandAllowed, source);
            Profiles.WriteProfileFile(path, data);
            _transcript.Notice(CmdCopiedNotice(source.Count - duplicates, duplicates, target, overwrite));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _transcript.Error(CmdCopyFailedError(ex.Message));
        }
        finally
        {
            DrainDiagnostics();
        }
    }

    private static string AllowedCommands(int count) => count == 1 ? "1 allowed command" : $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} allowed commands";

    // ── /cmdcopy --history and /cmdclear (2026-09-25) ───────────────────────

    public const string CmdCopyHistoryNothingNotice = "(nothing to copy: this profile has no command history)";

    /// <summary><c>/cmdcopy --history</c> into a profile whose <c>Keep command history</c> is off: its next load would drop the lines. Pinned.</summary>
    public static string CmdCopyHistoryOffError(string profile) =>
        $"\"{profile}\" has Keep command history off, so its next load would drop the lines; switch it on there first.";

    /// <summary>The question before a history copy (the yes/no pane's title; <see cref="TypedConfirm"/> where menus cannot open). Pinned.</summary>
    public static string CmdCopyHistoryPrompt(int count, string profile, bool overwrite) =>
        overwrite ? $"Replace \"{profile}\"'s command history with these {CommandLines(count)}?" : $"Copy {CommandLines(count)} of command history into \"{profile}\"?";

    /// <summary><c>(12 command lines copied into "work")</c>; an overwrite reads <c>(replaced "work"'s command history with 12 command lines)</c>. Pinned.</summary>
    public static string CmdHistoryCopiedNotice(int added, string profile, bool overwrite) =>
        overwrite ? $"(replaced \"{profile}\"'s command history with {CommandLines(added)})" : $"({CommandLines(added)} copied into \"{profile}\")";

    public static string CmdCopyHistoryFailedError(string profile) => $"Could not open \"{profile}\"'s {SessionStore.FileName}; nothing was copied.";

    public const string CmdClearNothingNotice = "(nothing to clear: the command history is empty)";

    /// <summary>The question before a <c>/cmdclear</c> (the yes/no pane's title; <see cref="TypedConfirm"/> where menus cannot open). Pinned.</summary>
    public static string CmdClearPrompt(int count) => $"Clear {CommandLines(count)} of command history?";

    /// <summary><c>(cleared 12 command lines)</c>. Pinned.</summary>
    public static string CmdClearedNotice(int count) => $"(cleared {CommandLines(count)})";

    private static string CommandLines(int count) => count == 1 ? "1 command line" : $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} command lines";

    /// <summary>
    /// <c>/cmdcopy &lt;profile&gt; --history [overwrite]</c> (2026-09-25, the user's ask): this profile's stored command
    /// history into the other profile's <c>sessions.db</c> — appended, a line equal to the one before it skipped, or in
    /// place of it — after a confirmation. The stored lines, not the line's list: what the setting keeps is what is copied.
    /// Refused when the target has <c>Keep command history</c> off (its next load would drop them). The target's store is
    /// opened for the copy alone; the loaded profile is never the target.
    /// </summary>
    private async Task CopyCommandHistoryAsync(string home, string target, bool overwrite, CancellationToken cancellationToken)
    {
        var lines = _sessions.CommandHistory();
        if (lines.Count == 0)
        {
            _flow.Notice(CmdCopyHistoryNothingNotice);
            return;
        }

        try
        {
            if (!Profiles.ReadProfileFile(Profiles.ProfileFile(home, target)).KeepCommandHistory)
            {
                _transcript.Error(CmdCopyHistoryOffError(target));
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _transcript.Error(CmdCopyFailedError(ex.Message));
            return;
        }

        if (!await ConfirmAsync(CmdCopyHistoryPrompt(lines.Count, target, overwrite), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        using (var store = new SessionStore(Profiles.Directory(home, target), _time))
        {
            if (store.Available)
            {
                _transcript.Notice(CmdHistoryCopiedNotice(store.AddCommandHistory(lines, overwrite), target, overwrite));
            }
            else
            {
                _transcript.Error(CmdCopyHistoryFailedError(target));
            }
        }

        DrainDiagnostics();
    }

    /// <summary>
    /// <c>/cmdclear</c> (2026-09-25, the user's ask): one confirmation, then the stored command history and the input line's
    /// list both emptied (the user's call: Up recalls nothing after it), whatever <c>Keep command history</c> says. The count
    /// asked about is the larger of the two, so a session with the setting off still has something to clear.
    /// </summary>
    private async Task CmdClearAsync(CancellationToken cancellationToken)
    {
        int count = Math.Max(_sessions.CommandHistoryCount, _input.History.Count);
        if (count == 0)
        {
            _flow.Notice(CmdClearNothingNotice);
            return;
        }

        if (!await ConfirmAsync(CmdClearPrompt(count), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        // The wipe and its line on the turn task when the question was asked mid-turn.
        RunOrPost(() =>
        {
            _sessions.ClearCommandHistory();
            _input.ReplaceHistory([]);
            _transcript.Notice(CmdClearedNotice(count));
            DrainDiagnostics();
        });
    }

    /// <summary>Whether a typed confirmation means yes.</summary>
    public static bool IsYes(string text) =>
        text.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) || text.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);

    // ── /timer ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>/timer</c> grammar, pure: nothing ⇒ list; <c>stop all</c> ⇒ stop all; <c>stop &lt;name&gt;</c>
    /// ⇒ stop; otherwise the longest leading run of tokens (up to six) that
    /// <see cref="TimerText.TryParseDuration"/> accepts is the duration and the rest is the name
    /// (<c>10m cooking</c>, <c>1h 30m tea</c>, <c>5 min</c>, <c>10 eggs</c> = ten minutes); anything
    /// else ⇒ invalid. The name is not validated here (the board says why it is refused).
    /// </summary>
    public static TimerAction ParseTimerArgs(string args)
    {
        var tokens = (args ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return new(TimerActionKind.List, TimeSpan.Zero, "");
        }

        if (tokens[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Length == 1)
            {
                return new(TimerActionKind.Invalid, TimeSpan.Zero, "");
            }

            string target = string.Join(' ', tokens.Skip(1));
            return TimerText.NameEquals(target, TimerText.AllName)
                ? new(TimerActionKind.StopAll, TimeSpan.Zero, "")
                : new(TimerActionKind.Stop, TimeSpan.Zero, target);
        }

        for (int take = Math.Min(6, tokens.Length); take >= 1; take--)
        {
            if (TimerText.TryParseDuration(string.Join(' ', tokens.Take(take)), out var duration))
            {
                return new(TimerActionKind.Start, duration, string.Join(' ', tokens.Skip(take)));
            }
        }

        return new(TimerActionKind.Invalid, TimeSpan.Zero, "");
    }

    /// <summary>
    /// <c>/timer</c>: the list (one notice per timer), a start, a stop by name or of all. The
    /// sentences are the tools' (<see cref="TimerText"/>), as notices in parentheses when they
    /// did something and as errors when they begin with <c>Error:</c>.
    /// </summary>
    private void HandleTimer(string args)
    {
        var action = ParseTimerArgs(args);
        switch (action.Kind)
        {
            case TimerActionKind.List:
                var timers = _timers.Snapshot();
                if (timers.Count == 0)
                {
                    _transcript.Notice(NoTimersNotice);
                    break;
                }

                foreach (var timer in timers)
                {
                    _transcript.Notice(NoticeGlyphs.Timer + TimerText.Line(timer));
                }

                break;

            case TimerActionKind.Start:
                var result = _timers.Start(action.Name.Length > 0 ? action.Name : TimerText.DefaultName(action.Duration), action.Duration);
                string sentence = StartTimerTool.Describe(result);
                if (result.Outcome == TimerStartOutcome.Started)
                {
                    _transcript.Notice("(" + NoticeGlyphs.Timer + sentence + ")");
                }
                else
                {
                    _transcript.Error(sentence);
                }

                break;

            case TimerActionKind.Stop:
                if (_timers.Stop(action.Name, out var removed))
                {
                    _transcript.Notice("(" + NoticeGlyphs.Timer + TimerText.Stopped(removed) + ")");
                }
                else
                {
                    _transcript.Error(TimerText.NoSuchTimer(TimerText.NormalizeName(action.Name), _timers.Snapshot()));
                }

                break;

            case TimerActionKind.StopAll:
                _transcript.Notice(TimerText.StoppedAll(_timers.StopAll()));
                break;

            default:
                _transcript.Error(TimerUsageError);
                break;
        }
    }

    // ── /cwd ────────────────────────────────────────────────────────────────

    /// <summary>The <c>/cwd</c> grammar, pure: nothing ⇒ show; <c>~</c> ⇒ reset; <c>browse</c> (any case) ⇒ the picker; anything else ⇒ that path.</summary>
    public static CwdAction ParseCwdArgs(string args)
    {
        string text = (args ?? "").Trim();
        if (text.Length == 0)
        {
            return new(CwdActionKind.Show, "");
        }

        return text == CwdHomeWord ? new(CwdActionKind.Reset, "")
            : string.Equals(text, CwdBrowseWord, StringComparison.OrdinalIgnoreCase) ? new(CwdActionKind.Browse, "")
            : new(CwdActionKind.Set, text);
    }

    /// <summary>The <c>/cwd</c> line: the resolved path, and how it came to be in force. Pinned.</summary>
    public static string CwdNotice(string path, bool isDefault, string? overriddenBy) =>
        NoticeGlyphs.Folder + "Working directory: " + path
        + (overriddenBy is not null ? $"  ({overriddenBy} this launch)" : isDefault ? "  " + SettingsMenu.ProfileFolderNote : "");

    /// <summary>
    /// <c>/cwd</c>: show the directory in force, or save one through the settings menu's one save
    /// path (<see cref="SettingsMenu.TrySaveWorkingDirectory"/>: created now, saved full; the
    /// menu prints the saved notice and, under <c>--cwd</c>, the override warning) — typed, or
    /// picked on the <see cref="FolderPane"/> (<c>browse</c>, 2026-09-21): the tree opens on the
    /// directory in force, lists what <c>File browser mode</c> allows, and a pick is the typed
    /// path's save; nothing picked keeps the directory and says so. The profile's own <c>files</c>
    /// folder heads the tree as <c>profile</c> (later that day, the user's ask), created now so it
    /// opens; picked, it is the reset — an empty setting, as <c>~</c> saves — so the directory
    /// keeps following the profile.
    /// </summary>
    private async Task HandleCwdAsync(string args, CancellationToken cancellationToken)
    {
        var action = ParseCwdArgs(args);
        var effective = _effective();
        switch (action.Kind)
        {
            case CwdActionKind.Show:
                _transcript.Notice(CwdNotice(
                    WorkingDirectory.Resolve(effective.WorkingDirectory, _settings.ProfileDirectory),
                    WorkingDirectory.IsDefault(effective.WorkingDirectory),
                    _overriddenBy(SettingsField.WorkingDirectory)));
                break;

            case CwdActionKind.Reset:
                if (_menu.TrySaveWorkingDirectory(""))
                {
                    ForgetReading();
                }

                break;

            case CwdActionKind.Browse:
                if (!_folderPane.Enabled)
                {
                    _transcript.Notice(FolderText.NeedsPaneNotice);
                    break;
                }

                if (await BrowseWorkingDirectoryAsync(cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    _transcript.Notice(FolderText.KeptNotice);
                }
                else if (_menu.TrySaveWorkingDirectory(picked))
                {
                    ForgetReading();
                }

                break;

            default:
                if (_menu.TrySaveWorkingDirectory(action.Path))
                {
                    ForgetReading();
                }

                break;
        }
    }

    /// <summary>
    /// The folder picker behind <c>/cwd browse</c> and, since 2026-09-22 (the user's ask), the
    /// Settings pane's <c>Working directory (cwd)</c> row — which asked for a typed path until then
    /// (<c>/cwd &lt;path&gt;</c> still takes one). The tree is the drives <c>File browser roots</c>
    /// allows with the profile's own <c>files</c> folder as a shortcut above them, opened on the
    /// directory in force. Returns what <see cref="SettingsMenu.TrySaveWorkingDirectory"/> should
    /// save — <c>""</c> for the profile's folder, so the row keeps reading as the default, or the
    /// full path — or null when the pane closed with nothing chosen. The caller checks
    /// <see cref="FolderPane.Enabled"/>: with no pane there is no picker.
    /// </summary>
    private async Task<string?> BrowseWorkingDirectoryAsync(CancellationToken cancellationToken)
    {
        string? picked = await BrowseFolderAsync(WorkingDirectory.Resolve(_effective().WorkingDirectory, _settings.ProfileDirectory), cancellationToken).ConfigureAwait(false);
        string profileFiles = WorkingDirectory.Resolve("", _settings.ProfileDirectory);
        return picked is null ? null
            : string.Equals(picked, profileFiles, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? "" : picked;
    }

    /// <summary>
    /// The folder picker behind the <c>Obsidian vault</c> row (later on 2026-09-22, the user's report: it opened on the
    /// working directory, never on the vault just chosen): opened on <paramref name="openOn"/> — the vault in force, or the
    /// working directory while none is set — and returning the full path picked, or null for nothing chosen.
    /// </summary>
    private Task<string?> BrowseVaultAsync(string openOn, CancellationToken cancellationToken) =>
        BrowseFolderAsync(string.IsNullOrWhiteSpace(openOn) ? WorkingDirectory.Resolve(_effective().WorkingDirectory, _settings.ProfileDirectory) : openOn, cancellationToken);

    /// <summary>
    /// The tree both pickers show (the drives <c>File browser roots</c> allows; above them the profile's <c>files</c>
    /// folder and, since 2026-09-24, its <c>splash</c> folder as shortcuts), opened on <paramref name="openOn"/>; the full
    /// path picked, or null.
    /// </summary>
    private async Task<string?> BrowseFolderAsync(string openOn, CancellationToken cancellationToken)
    {
        var effective = _effective();
        string profileFiles = WorkingDirectory.Resolve("", _settings.ProfileDirectory);
        string splash = _settings.ProfileSplashDirectory;
        foreach (string folder in (string[])[profileFiles, splash])
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left to the tree: the row opens as denied.
            }
        }

        var tree = new FolderTree(
            new FileSystemFolders(FileBrowserMode.Resolve(effective)),
            [new FolderShortcut(FolderText.ProfileLabel, profileFiles, FolderText.ShortcutGlyph), new FolderShortcut(FolderText.SplashLabel, splash, FolderText.SplashGlyph)]);
        int cursor = tree.ExpandTo(openOn);
        return await _folderPane.PickAsync(tree, cursor, cancellationToken).ConfigureAwait(false);
    }

    // ── /tree ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>/tree [path]</c>: the folders and files under the working directory (or the folder named,
    /// resolved through the sandbox) as notice lines, <see cref="TreeText"/>'s picture; the walk
    /// stops at <c>File /tree max length</c> entries and a file's size rides along under <c>File /tree show sizes</c>.
    /// What it lists follows <c>File browser/tree mode</c>, as the folder browsers do (2026-09-23, the user's call):
    /// <c>default</c> leaves out hidden and system entries and every dot-file and dot-folder, <c>show-hidden</c> lists
    /// them all (<c>.git</c> too). A path outside the root, missing or a file is the usual file error.
    /// </summary>
    private void HandleTree(string args)
    {
        var effective = _effective();
        int cap = Math.Clamp(effective.FileTreeMaxLength, WorkingDirectory.MinTreeLength, WorkingDirectory.MaxTreeLength);
        bool showHidden = FileBrowserMode.Resolve(effective) == FileBrowserVisibility.ShowHidden;
        var result = _files.FileTree(args, cap, hideDotEntries: !showHidden, showHidden: showHidden);
        if (result.Outcome != FileOutcome.Ok)
        {
            _transcript.Error(TreeText.Error(result));
            return;
        }

        foreach (var line in TreeText.Lines(result, effective.FileTreeShowSizes, cap))
        {
            _transcript.Notice(line);
        }
    }

    // ── /vault (2026-09-22) ─────────────────────────────────────────────────

    /// <summary><c>/vault</c> while the setting <c>Obsidian tools</c> is off. Pinned.</summary>
    public const string VaultToolsOffError = "Obsidian tools is off; /vault shows nothing until it is on (the Obsidian tab of /tools).";

    /// <summary><c>/vault</c> with the setting <c>Obsidian vault</c> empty. Pinned.</summary>
    public const string VaultNotSetError = "No Obsidian vault is set; set Obsidian vault on the Obsidian tab of /tools.";

    /// <summary><c>/vault</c> when the vault's folder is not there — a drive unplugged, a share offline, a path mistyped. Pinned.</summary>
    public static string VaultUnreachableError(string root) => $"The Obsidian vault {root} cannot be reached.";

    /// <summary><c>/vault</c> when the folder is there but holds no <c>.obsidian</c> folder. Pinned.</summary>
    public static string VaultNotAVaultError(string root) => $"{root} is not an Obsidian vault (it has no .obsidian folder).";

    /// <summary>
    /// <c>/vault</c> (2026-09-22, the user's ask: "similar to tree"): the vault's folders and notes as
    /// notice lines, <see cref="TreeText"/>'s picture over a <see cref="WorkingDirectory"/> rooted at the vault,
    /// every dot-entry left out (<c>.obsidian</c>, <c>.trash</c>, <c>.git</c> — Obsidian's own, which the vault
    /// tools never list either). The walk is <c>/tree</c>'s, capped by <c>File /tree max length</c>, sizes under
    /// <c>File /tree show sizes</c>. <c>Obsidian tools</c> off, no <c>Obsidian vault</c>, a folder that cannot be
    /// reached or holds no <c>.obsidian</c> is an error, checked in that order; the folder is never created.
    /// Since 2026-09-23 (the user's ask: "work like /tree") <c>/vault &lt;path&gt;</c> walks a folder under the
    /// vault, resolved as <c>/tree</c> resolves one under the sandbox — outside the vault, missing or a file is
    /// <c>/tree</c>'s error line — and a path through a dot-folder (<c>.obsidian</c>, <c>.trash/…</c>) is the
    /// missing one, since the vault tools never show those.
    /// </summary>
    private void HandleVault(string args)
    {
        var effective = _effective();
        if (VaultRoot(effective, out string? error) is not { } root)
        {
            _transcript.Error(error!);
            return;
        }

        if (NamesADotFolder(args))
        {
            _transcript.Error(TreeText.Error(new FileTreeResult(FileOutcome.Missing, args, "", [], false)));
            return;
        }

        int cap = Math.Clamp(effective.FileTreeMaxLength, WorkingDirectory.MinTreeLength, WorkingDirectory.MaxTreeLength);
        var result = new WorkingDirectory(() => root, _time).FileTree(args, cap, hideDotEntries: true);
        if (result.Outcome != FileOutcome.Ok)
        {
            _transcript.Error(TreeText.Error(result));
            return;
        }

        foreach (var line in TreeText.Lines(result, effective.FileTreeShowSizes, cap))
        {
            _transcript.Notice(line);
        }
    }

    /// <summary>
    /// The vault <c>/vault</c> walks and its argument list offers, trimmed; null with the error line when
    /// <c>Obsidian tools</c> is off, no <c>Obsidian vault</c> is set, the folder cannot be reached or holds no
    /// <c>.obsidian</c> — checked in that order, the folder never created.
    /// </summary>
    private static string? VaultRoot(AppSettingsData effective, out string? error)
    {
        error = null;
        if (!effective.ObsidianTools)
        {
            error = VaultToolsOffError;
            return null;
        }

        string root = effective.ObsidianVault.Trim();
        if (root.Length == 0)
        {
            error = VaultNotSetError;
            return null;
        }

        bool reachable;
        try
        {
            reachable = Directory.Exists(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            reachable = false;
        }

        if (!reachable)
        {
            error = VaultUnreachableError(root);
            return null;
        }

        if (!ObsidianVault.IsVault(root))
        {
            error = VaultNotAVaultError(root);
            return null;
        }

        return root;
    }

    /// <summary>Whether a vault path goes through a dot-folder (<c>.obsidian</c>, <c>.trash/x</c>) — <c>.</c> and <c>..</c> are not names, and resolve as usual. Pure.</summary>
    public static bool NamesADotFolder(string path) =>
        (path ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.StartsWith('.') && segment is not ("." or ".."));

    /// <summary>
    /// The <c>/vault</c> argument list's folders for <paramref name="prefix"/> (2026-09-23): the vault's
    /// <see cref="WorkingDirectory.Complete"/> kept to folders, none through a dot-folder; empty while
    /// <see cref="VaultRoot"/> has no vault to offer.
    /// </summary>
    private IReadOnlyList<string> VaultFolderChoices(string prefix)
    {
        if (VaultRoot(_effective(), out _) is not { } root)
        {
            return [];
        }

        return new WorkingDirectory(() => root, _time).Complete(prefix).Paths
            .Where(path => path.EndsWith('/') && !NamesADotFolder(path))
            .ToList();
    }

    // ── /git ────────────────────────────────────────────────────────────────

    /// <summary>The <c>/git</c> grammar (2026-09-21): <c>user</c>, <c>user force</c> (either case), anything else invalid. Pure.</summary>
    public static GitAction ParseGitArgs(string args)
    {
        var tokens = (args ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return tokens switch
        {
            [var user] when user.Equals(GitUserWord, StringComparison.OrdinalIgnoreCase) => new(GitActionKind.User),
            [var user, var force] when user.Equals(GitUserWord, StringComparison.OrdinalIgnoreCase) && force.Equals(GitForceWord, StringComparison.OrdinalIgnoreCase) => new(GitActionKind.User, Force: true),
            _ => new(GitActionKind.Invalid),
        };
    }

    /// <summary>Which of the two settings is empty: <c>Git native email and Git native name are not set; set them on the Git (native) tab of /tools.</c>, or the one. Pinned.</summary>
    public static string GitIdentityUnsetError(bool email, bool name) =>
        (email && name ? "Git native email and Git native name are" : email ? "Git native email is" : "Git native name is") + " not set; set " + (email && name ? "them" : "it") + " on the Git (native) tab of /tools.";

    /// <summary>The setting <c>Git native tools</c> is off (later on 2026-09-21, the user's call): <c>/git user</c> writes nothing and says why. Pinned.</summary>
    public const string GitNativeToolsOffError = "Git native tools is off; /git user does nothing until it is on (the Git (native) tab of /tools).";

    public static string GitIdentityWrittenNotice(string name, string email) => $"({NoticeGlyphs.Git}git user set for this repository: {name} <{email}>)";

    /// <summary>A <c>[user]</c> section was there and <c>force</c> was not given: what it holds, and the way past it.</summary>
    /// <remarks>An error since 2026-09-22 (the user's call; a notice before): nothing was written.</remarks>
    public static string GitIdentityPresentError(string name, string email) => $"Git repository already has a [user] section: {name} <{email}>; /git user force replaces it.";

    public static string GitNoRepositoryError(string root) => $"'{root}' is not inside a git repository; /cwd into one first.";

    public static string GitIdentityFailedError(string detail) => $"Could not write the git identity: {detail}";

    /// <summary>
    /// <c>/git user [force]</c> (2026-09-21): the <c>Git native email</c> and <c>Git native name</c> settings into the
    /// working directory's repository config (<see cref="GitAccess.SetLocalIdentity"/>). <c>Git native tools</c> off
    /// is an error before anything else (later that day: the switch gates the command as it gates the tools);
    /// either setting empty is an error naming it; no repository at the root is an error; a <c>[user]</c> section already
    /// there is a notice that names it and the <c>force</c> word, and nothing is written. Refused mid-turn.
    /// </summary>
    private void HandleGit(string args)
    {
        var action = ParseGitArgs(args);
        if (action.Kind != GitActionKind.User)
        {
            _transcript.Error(GitUsageError);
            return;
        }

        var effective = _effective();
        if (!effective.GitNativeTools)
        {
            _transcript.Error(GitNativeToolsOffError);
            return;
        }

        string email = effective.GitNativeEmail.Trim();
        string name = effective.GitNativeName.Trim();
        if (email.Length == 0 || name.Length == 0)
        {
            _transcript.Error(GitIdentityUnsetError(email.Length == 0, name.Length == 0));
            return;
        }

        var result = _git.SetLocalIdentity(email, name, action.Force);
        switch (result.Outcome)
        {
            case GitOutcome.Ok when result.Written:
                _transcript.Notice(GitIdentityWrittenNotice(result.Name, result.Email));
                break;
            case GitOutcome.Ok:
                _transcript.Error(GitIdentityPresentError(result.Name, result.Email));
                break;
            case GitOutcome.NoRepository:
                _transcript.Error(GitNoRepositoryError(_files.Root));
                break;
            default:
                _transcript.Error(GitIdentityFailedError(result.Detail.Length > 0 ? result.Detail : GitText.Error(result.Outcome, result.Detail)));
                break;
        }

        DrainDiagnostics();
    }

    // ── /explore ────────────────────────────────────────────────────────────

    /// <summary>The <c>/explore</c> notice: the folder as the file tools name it (blank = the working directory). Pinned.</summary>
    public static string ExploreOpenedNotice(string relative) => "(" + NoticeGlyphs.Folder + "opened " + FileText.Name(relative) + " in your file browser)";

    /// <summary>
    /// <c>/explore [path]</c>: the working directory (or a folder under it, resolved through the
    /// sandbox) handed to the same shell-execute opener <c>/persona</c>, <c>/operata</c>, <c>/vocalia</c> and the <c>open</c> tool use —
    /// Explorer on Windows, <c>open</c> / <c>xdg-open</c> elsewhere. A file is refused with the
    /// <c>is a file, not a folder</c> line: this command browses. A path outside the root or missing,
    /// and an opener that throws, are the usual file errors.
    /// </summary>
    private void HandleExplore(string args)
    {
        var result = _files.Open(args, _openFile, foldersOnly: true);
        if (result.Outcome != FileOutcome.Ok)
        {
            _transcript.Error(FileText.Opened(result));
            return;
        }

        _transcript.Notice(ExploreOpenedNotice(result.Relative));
    }

    // ── /speak (2026-09-17) ─────────────────────────────────────────────────

    /// <summary>A bare <c>/speak</c> with nothing to resume. Pinned.</summary>
    public const string SpeakUsageError = "Usage: /speak <file> [n]; then /speak alone resumes it and /speak <n> starts at sentence n";

    /// <summary><c>/speak &lt;n&gt;</c> with no file read yet. Pinned.</summary>
    public const string SpeakNothingToSeekError = "Nothing to jump to: /speak <file> first";

    /// <summary><c>/speak &lt;n&gt;</c> past the file's sentences (or 0). Pinned.</summary>
    public static string SpeakBeyondEndError(string file, int total, int at) =>
        $"{file} has {FileText.Count(total, "sentence", "sentences")}; nothing at {at.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The notice for a file with nothing to say: blank, whitespace, or punctuation alone. Pinned.</summary>
    /// <remarks>An error since 2026-09-22 (the user's call; a notice before): nothing is read.</remarks>
    public static string SpeakEmptyError(string relative) => $"{relative} is empty";

    /// <summary>The dim line under a block <c>/speak</c> cut at <see cref="WorkingDirectory.MaxReadChars"/>. Pinned.</summary>
    public static string SpeakCutNotice(int chars) => $"(cut at {chars.ToString("N0", CultureInfo.InvariantCulture)} characters)";

    /// <summary>
    /// <c>/speak &lt;file&gt;</c>: a text file under the working directory printed as a reply — the
    /// glyph and the text, styled like one (<see cref="StyledReply"/>) — and, with speech on and
    /// ready, read aloud as the tail under the input line: the timer alert's shape
    /// (<see cref="AnnounceAlertsAsync"/>), a speaker begun outside a turn, with the difference
    /// that the tail is armed for the interrupt (<see cref="_tailInterrupt"/>, the echo probe on
    /// the speaker) so the wake phrase cuts a long reading short as it cuts a reply. The model
    /// never sees the file: nothing enters the history, the <c>/copy</c> log or the reflection
    /// trace (<c>read_file</c> is the model's way). The file goes through
    /// <see cref="WorkingDirectory.ReadText"/> — the sandbox rule, the binary probe, the whole
    /// file up to <see cref="WorkingDirectory.MaxReadChars"/> with <see cref="SpeakCutNotice"/>
    /// under a cut one — and a bad path is the same <c>Error:</c> line <c>/tree</c> prints. The line
    /// that sent the command has silenced any tail (the <c>Submitted</c> arm), so the speaker
    /// begun here never finds one.
    ///
    /// <para>The reading is kept (<see cref="_reading"/>, <see cref="SpeakReading"/>): its sentences
    /// are the speech's own chunks, fed one by one (<see cref="SpeechOutput.Speak"/>) so the play
    /// head names one; the hint row shows <c>reading notes.md 3/40</c> while it plays and keeps
    /// <c>stopped notes.md 3/40</c> after a stop until the next turn. A bare <c>/speak</c> resumes
    /// at the stopped sentence (the top again after a full read); <c>/speak &lt;n&gt;</c> — digits
    /// alone; a file named so is <c>./5</c> — starts at sentence <em>n</em>, the block printed from
    /// there.</para>
    ///
    /// <para>True when the reading started, false when an error line was printed instead — a looped
    /// <c>/speak</c> (2026-09-25) stops on false.</para>
    /// </summary>
    private bool HandleSpeak(string args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            if (_reading is not { } resume)
            {
                _transcript.Error(SpeakUsageError);
                return false;
            }

            StartReading(resume, resume.Resume, cancellationToken);
            return true;
        }

        if (SpeakReading.TryParsePosition(args, out int at))
        {
            if (_reading is not { } seek)
            {
                _transcript.Error(SpeakNothingToSeekError);
                return false;
            }

            if (at < 1 || at > seek.Count)
            {
                _transcript.Error(SpeakBeyondEndError(seek.File, seek.Count, at));
                return false;
            }

            StartReading(seek, at, cancellationToken);
            return true;
        }

        // The whole argument as a path first — a file named `notes 5` is read whole — then, when
        // nothing is there, `<file> <n>`: the head read and the number the start sentence.
        var read = _files.ReadText(args, null, null);
        int from = 1;
        if (read.Outcome == FileOutcome.Missing && SpeakReading.TrySplitPosition(args, out string head, out int position))
        {
            read = _files.ReadText(head, null, null);
            from = position;
        }

        if (read.Outcome != FileOutcome.Ok)
        {
            _transcript.Error(FileText.Error(read.Outcome, read.Relative, "read", read.Detail));
            return false;
        }

        var reading = new SpeakReading(read.Relative, read.Text) { Cut = read.Truncated };
        if (reading.Count == 0)
        {
            _transcript.Error(SpeakEmptyError(read.Relative));
            return false;
        }

        // Remembered even when the number is bad, so /speak <n> works on it next.
        _reading = reading;
        if (from < 1 || from > reading.Count)
        {
            _transcript.Error(SpeakBeyondEndError(reading.File, reading.Count, from));
            return false;
        }

        StartReading(reading, from, cancellationToken);
        return true;
    }

    /// <summary>The block from sentence <paramref name="from"/> on, then the sentences to the speaker; the status onto the hint row.</summary>
    private void StartReading(SpeakReading reading, int from, CancellationToken cancellationToken)
    {
        var effective = _effective();
        var speaker = effective.TtsOutput && _speech.IsReady ? _speech.BeginTurn(cancellationToken) : null;
        _transcript.BeginAssistant(StyledReply(effective.TranscriptMarkdown, _pane.Enabled));
        _transcript.AppendDelta(reading.Text[reading.Sentences[from - 1].Offset..]);
        _transcript.EndAssistant();
        if (reading.Cut)
        {
            _transcript.Notice(SpeakCutNotice(WorkingDirectory.MaxReadChars));
        }

        if (speaker is not null)
        {
            // The probe a spoken turn gives its speaker (RunTurnAsync): the idle read's interrupt
            // over the tail reads it through IsEcho. Null when the interrupt is not ready.
            speaker.Probe = _voice.CreateEchoProbe(speaker.Format, TimeSpan.FromMilliseconds(effective.SttInterruptConfirmMs));
            for (int i = from - 1; i < reading.Count; i++)
            {
                speaker.Speak(reading.Sentences[i].Text);
            }

            speaker.CompleteAdding();
        }

        reading.Started(from, speaker);
        _hintReading = reading;
        _tailInterrupt = speaker is not null && _voice.InterruptReady;
        DrainDiagnostics();
        _pane.RefreshHint();
    }

    // ── /echo (2026-09-17) ──────────────────────────────────────────────────

    /// <summary>A bare <c>/echo</c>. Pinned.</summary>
    public const string EchoUsageError = "Usage: /echo <text>";

    /// <summary>
    /// <c>/echo &lt;text&gt;</c>: the line printed as a reply and, with speech on, read aloud as the
    /// tail — <c>/speak</c>'s block and voice over typed text (<see cref="StartReading"/>, a
    /// <see cref="SpeakReading.Kind.Echo"/> reading): the same styling, sentences, probe and stop
    /// rules, the hint row reading <c>💬 1/2 speaking</c>. Kept apart from the file reading
    /// (<see cref="_echo"/>), so a bare <c>/speak</c> resumes the last file, never the echo; an echo
    /// is only said again by another <c>/echo</c>. A line with nothing speakable (punctuation
    /// alone) is printed and nothing more. The model never sees it; Refused mid-turn; not headless.
    /// </summary>
    private void HandleEcho(string args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _transcript.Error(EchoUsageError);
            return;
        }

        var echo = new SpeakReading("", args) { Source = SpeakReading.Kind.Echo };
        if (echo.Count == 0)
        {
            var effective = _effective();
            _transcript.BeginAssistant(StyledReply(effective.TranscriptMarkdown, _pane.Enabled));
            _transcript.AppendDelta(args);
            _transcript.EndAssistant();
            return;
        }

        _echo = echo;
        StartReading(echo, 1, cancellationToken);
    }

    // ── /view (2026-09-17) ──────────────────────────────────────────────────

    /// <summary>A bare <c>/view</c>. Pinned.</summary>
    public const string ViewUsageError = "Usage: /view <image>";

    /// <summary>The codecs read the file for the model but refused it for the screen. Pinned.</summary>
    public static string ViewNotDrawnError(string relative) => $"Could not draw '{relative}'";

    /// <summary>
    /// <c>/view &lt;image&gt;</c>: one picture under the working directory drawn in the transcript
    /// as large as the window allows — <see cref="ThumbnailSize.Fit"/> over the console's size less
    /// the pane's rows, <see cref="ImageThumbnail.Read"/> scaling to fit and never enlarging, so a
    /// small picture stays small — centred in the window, the picture alone, nothing above or below it (the user's picks).
    /// The file goes through <see cref="WorkingDirectory.ReadImage"/> as <c>view_image</c>'s do (the
    /// sandbox rule, the bytes deciding, the model's downscale), and a bad path is the same
    /// <c>Error:</c> line. The model never sees it, and <c>Show image thumbnails</c> is not
    /// consulted: the command is the ask. Refused mid-turn; not headless.
    /// </summary>
    private void HandleView(string args)
    {
        if (args.Length == 0)
        {
            _transcript.Error(ViewUsageError);
            return;
        }

        var result = _files.ReadImage(args);
        if (result.Outcome != FileOutcome.Ok || result.Image is not { } image)
        {
            _transcript.Error(FileText.Error(result.Outcome, result.Relative, "view", result.Detail));
            return;
        }

        var box = WindowBox();
        if (ImageThumbnail.Read(image, box.Columns, box.MaxRows) is not { } picture)
        {
            _transcript.Error(ViewNotDrawnError(result.Relative));
            return;
        }

        _transcript.Picture(picture, RegisterPicture(image, sandbox: true));
    }

    // ── /imagine and /comfy (2026-09-24) ────────────────────────────────────

    /// <summary>
    /// <c>/imagine [workflow] &lt;prompt&gt; [-- &lt;negative&gt;] [--seed N] …</c> (2026-09-24, the user's ask: their own prompts —
    /// <c>score_9, score_8_up, …</c> — sent as typed, no model in between): the prompt straight to ComfyUI through the same
    /// engine as <c>generate_image</c> (<see cref="ComfyStudio"/>), under a spinner ESC cancels, the picture drawn as large as the
    /// window allows (several as a strip), the result line a notice. The result and the pictures ride with the next message
    /// (<see cref="TakeImagineNotes"/>), so the model knows what was made and can look at it. Refused mid-turn; not headless.
    ///
    /// <para>Under <c>/loop</c> (2026-09-25, the user's ask: <c>/loop infinite 1s /imagine …</c> with no model in between)
    /// <paramref name="loopBase"/> is how much was queued when the loop began: each pass trims the queue back to it first, so
    /// the next message carries the last pass's pictures alone, never an infinite loop's pile. Every picture is still drawn,
    /// saved and put in the strip. The outcome tells the loop whether to go on.</para>
    /// </summary>
    private async Task<LoopPass> HandleImagineAsync(string args, CancellationToken cancellationToken, (int Notes, int Images)? loopBase = null)
    {
        var (request, error) = ComfyImagine.Parse(args, _comfy.Catalog.Workflows, ComfyStudio.MaxCountOf(_effective()));
        if (request is null)
        {
            _transcript.Error(error!);
            return LoopPass.Failed;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        _queuedClicks.Reset();
        var watcher = _keys.WatchAsync(cts, stop.Token, null, null, LiveLineHook, spend: e => { _queuedClicks.Reset(); return ScrollInput(e); }, onClick: _pane.Enabled ? HintClickLine : null, editor: LiveEditor);
        ComfyGeneration? generation = null;
        bool cancelled = false;
        try
        {
            generation = await _transcript.WithSpinnerAsync(ComfyText.GeneratingLabel, () => _comfy.GenerateAsync(request, cts.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            stop.Cancel();
            await watcher.ConfigureAwait(false);
        }

        LoopPass pass;
        if (cancelled || generation is null)
        {
            _transcript.Notice(ComfyText.Cancelled);
            pass = LoopPass.Cancelled;
        }
        else if (!generation.Ok)
        {
            _transcript.Error(generation.Text);
            pass = LoopPass.Failed;
        }
        else
        {
            ComfyLines(generation.Text, line => _transcript.Notice(ComfyText.Glyph + line));
            // The strip first: the picture's window box then leaves its rows.
            AddToPictureStrip(generation.Images);
            ShowPictures(generation.Images);
            if (loopBase is { } keep)
            {
                // The last looped pass's batch goes; what was queued before the loop stays.
                TrimTo(_imagineNotes, keep.Notes);
                TrimTo(_imagineImages, keep.Images);
            }

            _imagineNotes.Add(ComfyText.ImagineNote(generation.Text));
            _imagineImages.AddRange(generation.Images);
            pass = LoopPass.Ok;
        }

        DrainDiagnostics();
        return pass;

        static void TrimTo<T>(List<T> list, int count)
        {
            if (list.Count > count)
            {
                list.RemoveRange(count, list.Count - count);
            }
        }
    }

    /// <summary>
    /// The box of a picture as large as the transcript allows: <see cref="ThumbnailSize.Fit"/> over the console's size less
    /// the pane's rows. <c>/view</c>'s, a lone <c>/imagine</c> picture's, and every thumbnail's under <c>fullsize</c> (2026-09-24).
    /// </summary>
    private ThumbnailBox WindowBox() =>
        ThumbnailSize.Fit(_pane.Profile.Width, _pane.Profile.Height, _pane.Enabled ? ScreenPane.PaneRows + _pane.InputRows + _pane.ToolbarRows + _pane.StripRows : 0);

    /// <summary>One picture as large as the window allows (the <c>/view</c> box), several as a thumbnail strip.</summary>
    private void ShowPictures(IReadOnlyList<ImageAttachment> images)
    {
        if (images.Count == 1)
        {
            var box = WindowBox();
            if (ImageThumbnail.Read(images[0], box.Columns, box.MaxRows) is { } picture)
            {
                _transcript.Picture(picture, RegisterPicture(images[0], sandbox: true));
            }

            return;
        }

        var (tiles, ids) = ReadThumbnails(images, ThumbnailSize.Resolve(_effective(), WindowBox()), sandbox: true);
        _transcript.Images(tiles, ids);
    }

    /// <summary>The message as the model gets it (2026-09-24): the <c>/imagine</c> notes since the last one ahead of the text, their pictures after the user's own; then the notes are spent.</summary>
    private (string Text, IReadOnlyList<ImageAttachment> Images) TakeImagineNotes(string text, IReadOnlyList<ImageAttachment> images)
    {
        if (_imagineNotes.Count == 0)
        {
            return (text, images);
        }

        string notes = string.Join("\n", _imagineNotes);
        IReadOnlyList<ImageAttachment> all = [.. images, .. _imagineImages];
        _imagineNotes.Clear();
        _imagineImages.Clear();
        return (notes + "\n\n" + text, all);
    }

    /// <summary>The first verb <c>/comfy</c> took (later still on 2026-09-24). Pinned.</summary>
    public const string ComfyEditWord = "edit";

    /// <summary><c>/comfy purge</c> (later still on 2026-09-24, the user's ask): the output folder emptied, <c>.pasted</c> included. Pinned.</summary>
    public const string ComfyPurgeWord = "purge";
    public const string ComfyPurgeNote = "delete every picture in the ComfyUI output folder, the pasted inputs too";

    /// <summary>The two kinds of file <c>/comfy edit</c> opens (the user's call, later still that day: the graph too, not only the settings). Pinned.</summary>
    public const string ComfyJsonWord = "json";
    public const string ComfyMarkdownWord = "markdown";

    /// <summary>The argument-list notes. Pinned.</summary>
    public const string ComfyEditNote = "open a workflow's .json or .md in your editor";
    public const string ComfyJsonNote = "open a workflow's .json (its ComfyUI graph) in your editor";
    public const string ComfyMarkdownNote = "open a workflow's .md (its settings and tips) in your editor";

    /// <summary>A <c>/comfy</c> argument that is not <c>edit json|markdown &lt;workflow&gt;</c>. Pinned.</summary>
    public const string ComfyUsageError = "Usage: /comfy, /comfy purge, or /comfy edit json|markdown <workflow>";

    /// <summary>The line after <c>/comfy edit</c> opened a workflow's file; <paramref name="created"/> when an <c>.md</c> had to be made first. Pinned.</summary>
    public static string ComfyEditNotice(string name, string path, bool json, bool created) =>
        $"({ComfyText.Glyph}{(created ? "made and opened" : "opened")} {path}; " +
        (json ? $"{name}'s graph is read again at your next message (a save ComfyUI's API format cannot read is skipped, with a warning)" : $"{name}'s settings and tips are read at your next message") + ")";

    /// <summary>A <c>/comfy edit</c> whose file could not be made or opened. Pinned.</summary>
    public static string ComfyEditFailedError(string path, string detail) => $"Could not open {path}: {detail}";

    /// <summary>
    /// <c>/comfy edit json|markdown &lt;workflow&gt;</c> (later still on 2026-09-24, the user's ask): the workflow's <c>.json</c>
    /// (its ComfyUI graph) or <c>.md</c> (its settings and tips) — whichever <c>comfy</c> folder holds it — in the editor Windows
    /// associates with it, as <c>/persona</c> opens <c>persona.md</c>. A workflow with no <c>.md</c> is given one first, its family
    /// written in, so there is somewhere to put the settings and tips. <c>md</c> is taken for <c>markdown</c>. Any installed
    /// workflow, offered to the model or not; nothing waits, the next turn reads what was saved.
    /// </summary>
    private void HandleComfyVerb(string args)
    {
        string[] words = args.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        bool? json = words.Length < 2 ? null : words[1].ToLowerInvariant() switch
        {
            ComfyJsonWord => true,
            ComfyMarkdownWord or "md" => false,
            _ => null,
        };
        if (!string.Equals(words[0], ComfyEditWord, StringComparison.OrdinalIgnoreCase) || json is null || words.Length < 3)
        {
            _transcript.Error(ComfyUsageError);
            return;
        }

        var installed = _comfy.Catalog.Workflows;
        if (installed.FirstOrDefault(w => string.Equals(w.Name, words[2].Trim(), StringComparison.OrdinalIgnoreCase)) is not { } workflow)
        {
            _transcript.Error(ComfyText.UnknownWorkflow(words[2].Trim(), installed).Replace("Error: ", "", StringComparison.Ordinal));
            return;
        }

        string path = json.Value ? workflow.FilePath : Path.ChangeExtension(workflow.FilePath, ".md");
        try
        {
            bool created = !json.Value && !File.Exists(path);
            if (created)
            {
                File.WriteAllText(path, "---\nfamily: " + ComfyFamilies.Name(workflow.Family) + "\n---\n");
            }

            _openFile(path);
            _transcript.Notice(ComfyEditNotice(workflow.Name, path, json.Value, created));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _transcript.Error(ComfyEditFailedError(path, ex.Message));
        }
    }

    /// <summary>The confirmation line before a <c>/comfy purge</c>: the folder and what is in it; <c>y</c> or <c>yes</c> deletes, anything else keeps. Pinned.</summary>
    public static string ComfyPurgePrompt(string path, int files, int folders, long bytes, bool truncated) =>
        $"{TrashGlyph}Delete everything in {path} for good — {TrashContents(files, folders, bytes)}"
        + (truncated ? $", counted the first {WorkingDirectory.MaxInfoEntries.ToString("N0", CultureInfo.InvariantCulture)} entries only" : "")
        + "?";

    public static string ComfyPurgeEmptyNotice(string path) => $"({TrashGlyph}nothing in {path})";

    public static string ComfyPurgedNotice(string path, int files, int folders, long bytes) => $"({TrashGlyph}purged {path}: {TrashContents(files, folders, bytes)})";

    /// <summary>A <c>/comfy purge</c> with the output folder set to the working directory itself: refused, it would take everything. Pinned.</summary>
    public const string ComfyPurgeHereError = "Not purging: the ComfyUI output folder is the working directory itself; set a folder under it first.";

    public static string ComfyPurgeFailedError(string path, string detail) => $"Could not purge {path}: {detail}";

    /// <summary>
    /// <c>/comfy purge</c> (later still on 2026-09-24, the user's ask): everything in the ComfyUI output folder — the pictures,
    /// the <c>.pasted</c> inputs, anything else put there — deleted for good, the folder kept; shaped like <c>/emptytrash</c>
    /// (the count, one typed confirmation, then <see cref="WorkingDirectory.PurgeFolder"/>). An output folder that is the working
    /// directory itself is refused rather than asked about. A paste saved before is written again at its next use.
    /// </summary>
    private async Task PurgeComfyAsync(CancellationToken cancellationToken)
    {
        string folder = ComfyStudio.OutputFolder(_effective().ComfyOutputFolder);
        var outcome = _files.Resolve(folder, forWrite: true, out string full);
        if (outcome == FileOutcome.Ok && string.Equals(full, _files.Root, StringComparison.OrdinalIgnoreCase))
        {
            _flow.Error(ComfyPurgeHereError);
            return;
        }

        if (outcome != FileOutcome.Ok)
        {
            _flow.Error(FileText.Error(outcome, folder, "purge"));
            return;
        }

        var info = _files.Info(folder);
        if (info.Outcome == FileOutcome.Missing || (info.Outcome == FileOutcome.Ok && info.Files == 0 && info.Folders == 0))
        {
            _flow.Notice(ComfyPurgeEmptyNotice(full));
            return;
        }

        if (info.Outcome != FileOutcome.Ok)
        {
            _flow.Error(ComfyPurgeFailedError(full, info.Detail));
            return;
        }

        if (!info.IsDirectory)
        {
            _flow.Error(ComfyPurgeFailedError(full, "it is a file, not a folder"));
            return;
        }

        if (!await ConfirmAsync(ComfyPurgePrompt(full, info.Files, info.Folders, info.Bytes, info.Truncated), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        RunOrPost(() =>
        {
            var result = _files.PurgeFolder(folder);
            if (result.Outcome == FileOutcome.Ok)
            {
                _transcript.Notice(ComfyPurgedNotice(full, result.Files, result.Folders, result.Bytes));
            }
            else
            {
                _transcript.Error(ComfyPurgeFailedError(full, result.Detail.Length > 0 ? result.Detail : result.Outcome.ToString()));
            }

            DrainDiagnostics();
        });
    }

    /// <summary>
    /// <c>/comfy</c> (2026-09-24): the ComfyUI server's status (<c>/system_stats</c>), the workflows found with their family, input,
    /// size and placeholders, the files skipped and why, and the folders a workflow goes in — what to look at when the image tools are
    /// not offered.
    /// </summary>
    private async Task HandleComfyAsync(CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (_comfy.Client() is { } client)
        {
            var (ok, status) = await _transcript.WithSpinnerAsync(ComfyText.CheckingServer, () => client.StatusAsync(cancellationToken)).ConfigureAwait(false);
            if (ok)
            {
                _transcript.Notice(ComfyText.Glyph + client.BaseUrl + " — " + status);
            }
            else
            {
                _transcript.Error(status);
            }
        }
        else
        {
            _transcript.Error(ComfyText.NoServer);
        }

        var (workflows, problems) = _comfy.Catalog.Scan();
        foreach (var line in ComfyText.StatusLines(workflows, problems, _comfy.Catalog.Roots, effective.ComfyTools, effective.ComfyWorkflowsOffered))
        {
            _transcript.Notice(line);
        }
    }

    // ── /emptytrash ─────────────────────────────────────────────────────────

    /// <summary>The confirmation line before an <c>/emptytrash</c>: the folder and what is in it; <c>y</c> or <c>yes</c> empties, anything else keeps. Pinned.</summary>
    public static string EmptyTrashPrompt(string trashPath, int files, int folders, long bytes, bool truncated) =>
        $"{TrashGlyph}Empty {trashPath} — {TrashContents(files, folders, bytes)}"
        + (truncated ? $", counted the first {WorkingDirectory.MaxInfoEntries.ToString("N0", CultureInfo.InvariantCulture)} entries only" : "")
        + "?";

    public static string TrashAlreadyEmptyNotice(string trashPath) => $"({TrashGlyph}nothing in {trashPath})";

    /// <summary>
    /// What the emptied-trash line opens with, inside its parentheses (2026-09-18, the reflection lines' shape,
    /// <see cref="LearnGlyph"/>): the wastebasket with its variation selector — U+1F5D1 alone is text-presentation,
    /// one narrow monochrome cell in Windows Terminal; U+FE0F makes it the two-cell colour emoji, and
    /// <see cref="TextCells"/> counts the selector as zero so the scrollback wraps as the terminal draws. Pinned.
    /// </summary>
    public const string TrashGlyph = "🗑️ ";

    public static string TrashEmptiedNotice(int files, int folders, long bytes) => $"({TrashGlyph}emptied the trash: {TrashContents(files, folders, bytes)})";

    public static string EmptyTrashFailedError(string detail) => $"Could not empty the trash: {detail}";

    private static string TrashContents(int files, int folders, long bytes) =>
        FileText.Count(files, "file", "files") + ", " + FileText.Count(folders, "folder", "folders") + ", " + FileText.Size(bytes);

    /// <summary>
    /// <c>/emptytrash</c>: what the trash holds (the same walk <c>info</c> uses), one typed
    /// confirmation on the input line (ESC or anything but <c>y</c> keeps), then everything under
    /// <c>.trash</c> is deleted for good and the folder kept. An empty or absent trash says so and
    /// asks nothing. Runs only from the input line: no turn in flight, the microphone closed.
    /// </summary>
    private async Task EmptyTrashAsync(CancellationToken cancellationToken)
    {
        string trashPath = _files.TrashPath;
        var info = _files.Info(WorkingDirectory.TrashFolderName);
        if (info.Outcome == FileOutcome.Missing || (info.Outcome == FileOutcome.Ok && info.Files == 0 && info.Folders == 0))
        {
            _flow.Notice(TrashAlreadyEmptyNotice(trashPath));
            return;
        }

        if (info.Outcome != FileOutcome.Ok)
        {
            _flow.Error(EmptyTrashFailedError(info.Detail));
            return;
        }

        if (!await ConfirmAsync(EmptyTrashPrompt(trashPath, info.Files, info.Folders, info.Bytes, info.Truncated), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        // The deletion and its line on the turn task when the question was asked mid-turn.
        RunOrPost(() =>
        {
            var result = _files.EmptyTrash();
            if (result.Outcome == FileOutcome.Ok)
            {
                _transcript.Notice(TrashEmptiedNotice(result.Files, result.Folders, result.Bytes));
            }
            else
            {
                _transcript.Error(EmptyTrashFailedError(result.Detail));
            }

            DrainDiagnostics();
        });
    }

    // ── /window ─────────────────────────────────────────────────────────────

    /// <summary>The <c>/window</c> line (<c>/windowsize</c> until later on 2026-09-19): the console profile's width and height, the numbers the pane lays out by. Pinned.</summary>
    public static string WindowNotice(int width, int height) =>
        $"{NoticeGlyphs.Window}Terminal window: {width.ToString(CultureInfo.InvariantCulture)} columns × {height.ToString(CultureInfo.InvariantCulture)} rows";

    // ── /copy ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>/copy</c> grammar, pure: nothing ⇒ the last exchange; <c>all</c> (ignoring case) ⇒
    /// every one; an integer ⇒ that many, where less than one is one and more than <see cref="int.MaxValue"/>
    /// is all (the clamp to what exists is <see cref="ChatLog.Take"/>'s); anything else ⇒ invalid.
    /// </summary>
    public static CopyAction ParseCopyArgs(string args)
    {
        string text = (args ?? "").Trim();
        if (text.Length == 0)
        {
            return new(CopyActionKind.Count, 1);
        }

        if (text.Equals(CopyAllWord, StringComparison.OrdinalIgnoreCase))
        {
            return new(CopyActionKind.All, 0);
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long count))
        {
            return new(CopyActionKind.Invalid, 0);
        }

        return count > int.MaxValue
            ? new(CopyActionKind.All, 0)
            : new(CopyActionKind.Count, (int)Math.Max(count, 1));
    }

    /// <summary>The line after a copy: how many went, of how many there are, as replies or exchanges. Pinned.</summary>
    public static string CopiedNotice(int copied, int total, bool withUserText)
    {
        string noun = withUserText ? "exchange" : "reply";
        string nouns = withUserText ? "exchanges" : "replies";
        return copied == 1 ? $"(copied the last {noun} to the clipboard)"
            : copied < total ? $"(copied the last {copied} {nouns} to the clipboard)"
            : $"(copied all {copied} {nouns} to the clipboard)";
    }

    private void HandleCopy(string args)
    {
        var action = ParseCopyArgs(args);
        if (action.Kind == CopyActionKind.Invalid)
        {
            _transcript.Error(CopyUsageError);
            return;
        }

        if (_log.Count == 0)
        {
            _transcript.Notice(NothingToCopyNotice);
            return;
        }

        bool withUserText = _effective().CopyUserPrompt;
        int copied = _log.Take(action.Kind == CopyActionKind.All ? int.MaxValue : action.Count);
        // The log keeps the model's line endings; the clipboard gets the CF_UNICODETEXT convention.
        string markdown = _log.Markdown(copied, withUserText).ReplaceLineEndings("\r\n");
        if (_copy(markdown))
        {
            _transcript.Notice(CopiedNotice(copied, _log.Count, withUserText));
        }
        else
        {
            _transcript.Error(CopyFailedError);
        }
    }

    // ── /profile ────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>/profile</c> grammar, pure: nothing ⇒ pick; <c>add &lt;name&gt;</c> / <c>delete &lt;name&gt;</c>
    /// / <c>reset &lt;name&gt;</c> (the word ignoring case) ⇒ that; <c>reset</c> alone ⇒ reset the loaded
    /// profile (an empty name); <c>rename &lt;name&gt; &lt;new-name&gt;</c> ⇒ rename (the only three-token
    /// form; <c>rename</c> with fewer is invalid, never a switch — it is a reserved word); one other
    /// token ⇒ switch; anything else ⇒ invalid. The names are not validated here (the handler says
    /// why one is refused).
    /// </summary>
    public static ProfileAction ParseProfileArgs(string args)
    {
        var tokens = (args ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        switch (tokens.Length)
        {
            case 0:
                return new(ProfileActionKind.Pick, "");
            case 1 when tokens[0].Equals("reset", StringComparison.OrdinalIgnoreCase):
                return new(ProfileActionKind.Reset, "");
            case 1 when tokens[0].Equals("edit", StringComparison.OrdinalIgnoreCase):
                return new(ProfileActionKind.Edit, "");
            case 1 when tokens[0].Equals("reload", StringComparison.OrdinalIgnoreCase):
                return new(ProfileActionKind.Reload, "");
            case 1:
                return Profiles.ReservedNames.Contains(tokens[0], StringComparer.OrdinalIgnoreCase)
                    ? new(ProfileActionKind.Invalid, "")
                    : new(ProfileActionKind.Switch, tokens[0]);
            case 2 when tokens[0].Equals("add", StringComparison.OrdinalIgnoreCase):
                return new(ProfileActionKind.Add, tokens[1]);
            case 2 when tokens[0].Equals("delete", StringComparison.OrdinalIgnoreCase):
                return new(ProfileActionKind.Delete, tokens[1]);
            case 2 when tokens[0].Equals("reset", StringComparison.OrdinalIgnoreCase):
                return new(ProfileActionKind.Reset, tokens[1]);
            case 3 when tokens[0].Equals("rename", StringComparison.OrdinalIgnoreCase):
                return new(ProfileActionKind.Rename, tokens[1], tokens[2]);
            default:
                return new(ProfileActionKind.Invalid, "");
        }
    }

    public static string ProfileMissingError(string name) => $"No profile named \"{name}\"; /profile lists them.";

    // /profile edit and /profile reload (2026-09-21). Pinned.
    public static string ProfileEditOpenedNotice(string name) => $"({NoticeGlyphs.Profile}opened profile \"{name}\"'s profile.json in your editor; /profile reload reads it back)";
    public static string ProfileEditCreatedNotice(string name) => $"({NoticeGlyphs.Profile}created and opened profile \"{name}\"'s profile.json in your editor; /profile reload reads it back)";
    public static string ProfileEditFailedError(string detail) => $"Could not open profile.json: {detail}";

    // /memory edit (2026-09-23): no reload word — the store reads the file back when it changed. Pinned.
    public const string MemoryEditOpenedNotice = "(" + NoticeGlyphs.Memory + "opened memory.json in your editor; your changes are read back on its next use)";
    public const string MemoryEditCreatedNotice = "(" + NoticeGlyphs.Memory + "created and opened memory.json in your editor; your changes are read back on its next use)";
    public static string MemoryEditFailedError(string detail) => $"Could not open memory.json: {detail}";
    public static string ProfileReloadedNotice(string name, int changed) =>
        $"({NoticeGlyphs.Profile}reloaded profile \"{name}\"; " + (changed == 0 ? "nothing changed" : UsageText.Plural(changed, "setting", "settings") + " changed") + ")";

    /// <summary>
    /// What a reload's changes ask the screen to rebuild (2026-09-21): each <see cref="SettingsDiff.Changes"/>
    /// line names the property before its colon, and the properties are named as their
    /// <see cref="SettingsField"/> is, so the menu's own groupings say which session a change belongs
    /// to — the LLM, TTS and voice fields, the MCP switch, and <c>LLM offer tools</c> for the
    /// conversation. A property with no field (a list, a limit read at each call) asks nothing. Pure.
    /// </summary>
    public static SettingsChanges ReloadChanges(IReadOnlyList<string> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var result = SettingsChanges.None;
        foreach (string change in changes)
        {
            int colon = change.IndexOf(':', StringComparison.Ordinal);
            string name = colon < 0 ? change : change[..colon];
            if (!Enum.TryParse<SettingsField>(name, ignoreCase: false, out var field))
            {
                continue;
            }

            if (SettingsMenu.IsLlmField(field))
            {
                result |= SettingsChanges.Llm;
            }

            if (SettingsMenu.IsTtsField(field))
            {
                result |= SettingsChanges.Tts;
            }

            if (SettingsMenu.IsVoiceField(field))
            {
                result |= SettingsChanges.Voice;
            }

            if (SettingsMenu.IsMcpField(field))
            {
                result |= SettingsChanges.Mcp;
            }

            if (field == SettingsField.LlmOfferTools)
            {
                result |= SettingsChanges.Conversation;
            }

            if (field == SettingsField.Theme)
            {
                result |= SettingsChanges.Theme;
            }
        }

        return result;
    }

    public static string ProfileExistsError(string name) => $"A profile named \"{name}\" already exists; /profile {name} switches to it.";

    public static string ProfileCreatedNotice(string name) => $"({NoticeGlyphs.Profile}created profile \"{name}\" from the current settings)";

    /// <summary>
    /// The created notice naming what came along under the <c>advanced</c> <see cref="NewProfileMode"/>:
    /// <paramref name="copiedFiles"/> is <see cref="Profiles.CopySidekickFiles"/>'s answer, each file
    /// in <see cref="Profiles.Describe"/>'s word. Nothing copied reads like <see cref="ProfileCreatedNotice(string)"/>.
    /// </summary>
    public static string ProfileCreatedNotice(string name, IReadOnlyList<string> copiedFiles) =>
        $"({NoticeGlyphs.Profile}created profile \"{name}\" from the current {SidekickWords(copiedFiles)})";

    public static string ProfileDeletedNotice(string name) => $"({NoticeGlyphs.Profile}deleted profile \"{name}\")";

    public static string ProfileFailedError(string detail) => $"Could not change profiles: {detail}";

    public static string ProfileDeleteFailedError(string detail) => $"Could not delete the profile: {detail}";

    /// <summary>The confirmation line before a <c>/profile delete</c>; <c>y</c> or <c>yes</c> deletes, anything else keeps.</summary>
    public static string DeleteProfilePrompt(string name) =>
        $"{NoticeGlyphs.Profile}Delete profile \"{name}\" and everything in it (settings, memories, persona, operating rules, voice directive, MCP servers, sessions)?";

    /// <summary>
    /// The confirmation line before a <c>/profile reset</c> — the user's wording (2026-09-20, later
    /// that day: <c>the default settings</c> says what goes back; the memories, persona, operating
    /// rules and voice directive stay, and the line no longer lists them); <c>y</c> or <c>yes</c>
    /// resets, anything else keeps. Pinned.
    /// </summary>
    public static string ResetProfilePrompt(string name) =>
        $"{NoticeGlyphs.Profile}Reset profile \"{name}\" to the default settings?";

    /// <summary>
    /// The notice after a reset, the user's wording (2026-09-20): <c>; conversation cleared</c> when it
    /// was the loaded profile (the reset is a switch in all but the name). Pinned.
    /// </summary>
    public static string ProfileResetNotice(string name, bool loaded) =>
        $"({NoticeGlyphs.Profile}reset profile \"{name}\" to the defaults{(loaded ? "; conversation cleared" : "")})";

    public static string ProfileResetFailedError(string detail) => $"Could not reset the profile: {detail}";

    /// <summary>The notice after a <c>/profile rename</c>: the old spelling as the disk had it, the new one as typed. Pinned.</summary>
    public static string ProfileRenamedNotice(string name, string newName) => $"({NoticeGlyphs.Profile}renamed profile \"{name}\" to \"{newName}\")";

    public static string ProfileRenameFailedError(string detail) => $"Could not rename the profile: {detail}";

    /// <summary><c>settings</c>, then each file's word: the created notice's list and the reset prompt's.</summary>
    private static string SidekickWords(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var words = new StringBuilder("settings");
        foreach (var file in files)
        {
            words.Append(", ").Append(Profiles.Describe(file));
        }

        return words.ToString();
    }

    /// <summary>The spinner while listening. Pinned.</summary>
    public static string ListeningLabel(string key) => $"listening…  {key} or Enter = done   ESC = discard";

    /// <summary>The spinner while listening after the wake word. Pinned.</summary>
    public static string WakeListeningLabel(string phrase, string key) => $"heard \"{phrase}\"…  {key} or Enter = done   ESC = discard";

    /// <summary>The spinner while listening after an interruption: the request is what is said now, the phrase itself is not it. Pinned.</summary>
    public static string InterruptedLabel(string key) => $"interrupted — say your request…  {key} or Enter = done   ESC = discard";

    /// <summary>
    /// What to print when a turn ends, and what to do next, decided once after everything has
    /// stopped. Keys win over the microphone: ESC prints what it always printed, and a hit that
    /// landed after the reply had fully played changes nothing. ESC before the model's first
    /// event (<paramref name="returned"/> false) withdraws the message instead
    /// (<see cref="TurnOutcome.Withdrawn"/>): the keys alone (ESC, and Ctrl+C the same since
    /// 2026-09-17), never the phrase or the app token. Pure; pinned by tests.
    /// </summary>
    /// <param name="cancelled">The turn's token fired before the text finished.</param>
    /// <param name="stoppedEarly">The spoken tail was cut short (the device silenced with audio still owed).</param>
    /// <param name="interrupt">What the key watcher saw.</param>
    /// <param name="heardPhrase">The interrupt listener heard the phrase.</param>
    /// <param name="appCancelled">The app token: Ctrl+Break, or Ctrl+C where the console input is not the app's.</param>
    /// <param name="returned">The model's first event (after the opening calls) had arrived.</param>
    /// <summary>
    /// The turn's reason line in the log (2026-09-19), decided from the same facts as
    /// <see cref="TurnEndNotice"/>: the keys after the model's first event, the keys before it (the
    /// message withdrawn), the wake phrase, the app token, or a mid-turn command's cancel (a cancel
    /// with no key and no phrase). Null for a reply that ran to its end.
    /// </summary>
    public static string? TurnOutcomeLogLine(TurnOutcome outcome, bool cancelled, Interrupt interrupt, bool heardPhrase, bool appCancelled)
    {
        if (outcome == TurnOutcome.Exit || appCancelled)
        {
            return TurnEndedByAppTokenLog;
        }

        if (outcome == TurnOutcome.Withdrawn)
        {
            return TurnWithdrawnLog;
        }

        if (outcome == TurnOutcome.Interrupted || heardPhrase)
        {
            return TurnInterruptedLog;
        }

        if (interrupt == Interrupt.Cancel)
        {
            return TurnCancelledByKeysLog;
        }

        return cancelled ? TurnCancelledByCommandLog : null;
    }

    public const string TurnCancelledByKeysLog = "Turn cancelled by the keys (ESC or Ctrl+C) after the model's first event.";
    public const string TurnWithdrawnLog = "Turn withdrawn: the keys before the model's first event; the message goes back to the line.";
    public const string TurnInterruptedLog = "Turn interrupted by the wake phrase.";
    public const string TurnEndedByAppTokenLog = "Turn ended by the app token.";
    public const string TurnCancelledByCommandLog = "Turn cancelled by a mid-turn command.";

    /// <summary>The first ESC over a reply being heard (the log's Debug line; the transcript's is <see cref="SpeechStoppedNotice"/>).</summary>
    public const string SpeechStoppedLogLine = "Speech stopped by the first ESC; the text streamed on.";

    /// <summary>The idle line's ESC over the tail.</summary>
    public const string TailStoppedLogLine = "Tail stopped by ESC.";

    public static (string? Notice, TurnOutcome Outcome) TurnEndNotice(bool cancelled, bool stoppedEarly, Interrupt interrupt, bool heardPhrase, bool appCancelled, bool returned)
    {
        string? keyNotice = cancelled ? CancelledNotice : stoppedEarly ? SpeechStoppedNotice : null;
        if (appCancelled)
        {
            return (keyNotice, TurnOutcome.Exit);
        }

        if (interrupt == Interrupt.Cancel)
        {
            return cancelled && !returned ? (WithdrawnNotice, TurnOutcome.Withdrawn) : (keyNotice, TurnOutcome.Continue);
        }

        if (heardPhrase && (cancelled || stoppedEarly))
        {
            return (InterruptedNotice, TurnOutcome.Interrupted);
        }

        return (keyNotice, TurnOutcome.Continue);
    }

    /// <summary>One listen for a request: the text (empty when nothing usable was heard), or null when discarded or failed; <see cref="Exit"/> when the app token ended it.</summary>
    public sealed record ListenOutcome(string? Text, bool Exit, bool Discarded);

    /// <summary><c>/tts</c>, <c>/stt</c> and <c>/wake</c> arguments: nothing (toggle, null), <c>on</c> or <c>off</c>. False for anything else.</summary>
    public static bool TryParseSwitch(string args, out bool? on)
    {
        switch ((args ?? "").Trim().ToLowerInvariant())
        {
            case "":
                on = null;
                return true;
            case "on":
                on = true;
                return true;
            case "off":
                on = false;
                return true;
            default:
                on = null;
                return false;
        }
    }

    /// <summary>Runs until <c>/exit</c>, the keyboard going away, or <paramref name="cancellationToken"/>. Always 0.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        bool previousEcho = DiagnosticLog.EchoToConsole;
        DiagnosticLog.EchoToConsole = false;
        Action<DiagnosticEvent> enqueue = OnDiagnostic;
        DiagnosticLog.Emitted += enqueue;
        try
        {
            // The alternate buffer first, then the banner into it through the pane (a count of
            // its rows keeps the input row at the bottom); the finally's Close leaves the buffer.
            _pane.Open();
            using (_pane.Batch())
            {
                _renderScreen(_pane);
            }

            _pane.Show();
            // The mouse and the wheel are the screen's from here: a click on the row, the wheel
            // over the transcript, the panes' rows (Shift keeps the terminal's own selection).
            _mouse?.Invoke(true);
            _holdWheel?.Invoke(true);
            ApplyWindowTitle();

            await ConnectLlmAsync(cancellationToken, quiet: true, startup: true).ConfigureAwait(false);
            await ConnectSpeechAsync(cancellationToken, quiet: true).ConfigureAwait(false);
            await ConnectVoiceAsync(cancellationToken, quiet: true).ConfigureAwait(false);
            await ConnectMcpAsync(cancellationToken).ConfigureAwait(false);
            // After the connects: a failed probe's line sits under the banner and the picture
            // fills what is left, and no picture write lands under a spinner.
            ShowSplash();

            while (!cancellationToken.IsCancellationRequested)
            {
                DrainDiagnostics();
                if (await AnnounceAlertsAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                IReadOnlyList<InputEvent>? replay = null;
                SubmittedLine? send = null;
                // A draft the editor just handed back (2026-09-19) goes first: the user is
                // waiting on it, and /draft ran at this idle line, so no withdrawn draft stands.
                bool fromDraft = _draftReplay is not null;
                if (fromDraft)
                {
                    replay = _draftReplay;
                    _draftReplay = null;
                }
                else if (_pendingLines.TryDequeue(out var pendingLine))
                {
                    // A line left for the idle line under the reply (2026-09-25): a message with the queue
                    // off, or the command that cancelled the reply — sent before anything else.
                    send = pendingLine;
                }
                else if (_restoreDraft is null && !_queue.Held && _queue.TryDequeue(out var queued))
                {
                    // The next queued message is sent now (2026-09-18; its events were replayed through
                    // a read until the row became a live editor, 2026-09-25): the › row with a paste's
                    // label and preview, then the Submitted arm as for any line. A withdrawn draft is
                    // sent or cleared first: it came back to the row ahead of the queue.
                    send = queued.Line;
                }

                InputResult result;
                WakeHit? hit = null;
                bool tailArmed = false;
                if (send is not null)
                {
                    // Nothing to listen for, no microphone armed: the line was typed already. The
                    // draft on the row, if any, stays for the next read.
                    result = _input.Send(send, DismissSplash, Math.Clamp(_effective().PastePreviewLines, 0, PasteBlocks.MaxPreviewLines));
                    _sentDraft = send.Draft;
                }
                else
                {
                    (result, hit, tailArmed) = await ReadLineAsync(cancellationToken, replay).ConfigureAwait(false);
                    if (result is InputResult.Submitted && _input.History.Count > 0)
                    {
                        _sentDraft = _input.History[^1];
                    }

                    if (_input.Chat.Text.Length == 0)
                    {
                        // The withdrawn draft was sent or cleared: the queue may go on.
                        _restoreDraft = null;
                    }
                }

                // Leaving the line stops the tail: a sent line (a message or a command, and the
                // ringing timer's Enter) silently — the next thing is the feedback; the push-to-talk
                // key and the idle wake before the microphone opens (a drained speaker forgotten
                // there, nothing plays under an idle wake); the wake phrase over a tail as an
                // interruption. ESC stops it inside the read (ReadLineAsync's hook, the draft kept,
                // the notice printed there). Typing does not, and a read ended by a nudge keeps it.
                switch (result)
                {
                    case InputResult.EndOfInput:
                        _exitReason = ExitByEndOfInput;
                        return 0;
                    case InputResult.Exit:
                        // The second Ctrl+C inside the window (ReadLineAsync's hook declined it): /exit's path.
                        _exitReason = ExitByInterrupt;
                        return 0;
                    case InputResult.Cancelled:
                        // The cleared row is the feedback; the hint row already names the key. The
                        // hook has stopped any tail before this press reached the row: a safety net.
                        _timers.Acknowledge();
                        if (await _speech.StopAsync().ConfigureAwait(false))
                        {
                            _transcript.Notice(SpeechStoppedNotice);
                        }

                        break;
                    case InputResult.PushToTalk:
                        _timers.Acknowledge();
                        await _speech.StopAsync().ConfigureAwait(false);
                        if (await HandlePushToTalkAsync(cancellationToken).ConfigureAwait(false))
                        {
                            return 0;
                        }

                        break;
                    case InputResult.WakeWord wake when tailArmed:
                        _timers.Acknowledge();
                        if (await HandleTailInterruptAsync(wake.Draft, hit, cancellationToken).ConfigureAwait(false))
                        {
                            return 0;
                        }

                        break;
                    case InputResult.WakeWord wake:
                        if (wake.Draft.Length > 0 || hit is null)
                        {
                            // Like F4 with text on the line: ignored, and the draft comes back (it is the chat line's).
                            break;
                        }

                        _timers.Acknowledge();
                        await _speech.StopAsync().ConfigureAwait(false);
                        if (await HandleWakeAsync(hit, cancellationToken).ConfigureAwait(false))
                        {
                            return 0;
                        }

                        break;
                    case InputResult.Alert alert:
                        // A timer went off under the read, or the tail ended; the loop top prints
                        // what is queued and the draft comes back (the chat line keeps it).
                        break;
                    case InputResult.HintRow hint:
                        // A double-click on the hint row (2026-09-18), as if the
                        // command were sent — the tail silenced, the timers acknowledged — without
                        // the transcript row or the history; the draft comes back after: the model
                        // name is /server (2026-09-22, the user's call: server, model, then reasoning,
                        // as the typed command; /model before), the reasoning mark after it /reasoning (2026-09-21), the
                        // brain the reflection's cancel, a speech glyph its switch off, the token
                        // tally /usage (2026-09-21), anywhere else /settings.
                        // The pane zones go through HandleAsync as the typed word would (later on
                        // 2026-09-21), so a double-click off the pane it opens can switch panes.
                        _timers.Acknowledge();
                        DisarmExit();
                        await _speech.StopAsync().ConfigureAwait(false);
                        if (hint.Hit.Zone == ScreenPane.HintZone.Strip && hint.Hit.Glyph == LearnStripGlyph)
                        {
                            // The brain is drawn only while a reflection runs, so the click is its
                            // cancel (the busy row's too since 2026-09-24, through HintClickLine). The job
                            // answers Cancelled, which LearnNotice keeps quiet, so the line is written here.
                            ForgetPausedLearn();
                            _session.CancelLearning();
                            _transcript.Notice(LearnCancelledNotice);
                        }
                        else if (hint.Hit.Zone == ScreenPane.HintZone.Strip && SwitchForGlyph(hint.Hit.Glyph) is { } glyphSwitch)
                        {
                            await HandleSwitchAsync(glyphSwitch, "off", midTurn: false, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            string hintLine = hint.Hit.Zone switch
                            {
                                ScreenPane.HintZone.Trailer => SlashCommands.ServerWord,
                                ScreenPane.HintZone.Mark => SlashCommands.ReasoningWord,
                                ScreenPane.HintZone.Queued => SlashCommands.QueueWord,   // the held count (2026-09-18): /queue, as the typed command
                                ScreenPane.HintZone.Usage => SlashCommands.UsageWord,
                                _ => SlashCommands.SettingsWord,
                            };
                            if (await HandleAsync(hintLine, [], cancellationToken).ConfigureAwait(false))
                            {
                                return 0;
                            }
                        }

                        break;
                    case InputResult.ToolbarRow tool:
                        // A double-click on the toolbar (2026-09-21): a pane glyph's word, the
                        // path's /cwd browse, or the blanks' /settings (later that day, as the hint
                        // row's blanks), through the dispatch as the typed line — without the
                        // transcript row or the history, the draft back after, as the hint row's.
                        // A glyph the strip does not name is nothing (the officer was, until /police later on 2026-09-22).
                        _timers.Acknowledge();
                        DisarmExit();
                        await _speech.StopAsync().ConfigureAwait(false);
                        string? toolbarLine = tool.Hit.Zone switch
                        {
                            ScreenPane.ToolbarZone.Path => CwdBrowseLine,
                            ScreenPane.ToolbarZone.Row => SlashCommands.SettingsWord,
                            _ => ToolbarWord(tool.Hit.Glyph),
                        };
                        if (toolbarLine is not null && await HandleAsync(toolbarLine, [], cancellationToken).ConfigureAwait(false))
                        {
                            return 0;
                        }

                        break;
                    case InputResult.Submitted submitted:
                        _timers.Acknowledge();
                        DisarmExit();
                        await _speech.StopAsync().ConfigureAwait(false);
                        if (submitted.Text.Length == 0)
                        {
                            // Enter on the empty line, allowed only while a timer rings: the silence itself.
                            break;
                        }

                        // A draft is a message whatever it reads (a file holding "/help" asks the
                        // model about /help): straight to the turn, never the command parser.
                        if (await (fromDraft ? RunMessageAsync(submitted.Text, submitted.Images, cancellationToken) : HandleAsync(submitted.Text, submitted.Images, cancellationToken)).ConfigureAwait(false))
                        {
                            return 0;
                        }

                        break;
                }
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app token (Ctrl+Break; Ctrl+C is a key here since 2026-09-17): leave quietly, the caller still flushes settings.
            return 0;
        }
        finally
        {
            // A reflection still running has no screen to report to; its request aborts, and one waiting never starts.
            _pendingLearn = null;
            ForgetPausedLearn();
            _session.CancelLearning();
            _timers.Dispose();
            // The background processes go with the screen (2026-09-21): what still runs is killed, tree and all.
            _processes.Dispose();
            _comfy.Dispose();
            _sessions.Dispose();
            if (_ownsMcp)
            {
                await _mcp.DisposeAsync().ConfigureAwait(false);
            }

            // The tail, if any, silenced before the screen goes (a stopped speaker completes at once).
            await _speech.StopAsync().ConfigureAwait(false);
            DiagnosticLog.Info(AppCategory, ScreenClosedLogLine(_exitReason ?? ExitByAppToken));
            DrainDiagnostics();
            _pane.Close();
            _pane.Dispose();
            DiagnosticLog.Emitted -= enqueue;
            DiagnosticLog.EchoToConsole = previousEcho;
        }
    }

    /// <summary>
    /// One input line, with the microphone armed around it and the timer alert source published
    /// for the board's signal. With nothing playing, the wake word is armed when it is ready. Over
    /// a tail (the last reply still playing under the line) the turn's interrupt is armed again —
    /// keyword mode, the same echo guard over the turn's own probe, if it had one — when the turn
    /// armed it (<see cref="_tailInterrupt"/>; <c>TailArmed</c> in the result); a tail without it
    /// (the interrupt off, a timer alert) arms nothing, so the device plays with the microphone
    /// closed. The tail's end nudges the read through the alert signal, and the next read arms the
    /// idle wake. The push-to-talk key is read per line (/stt and /settings can change it between
    /// two prompts). The disarm is the one exit: whatever ended the read, the microphone is closed
    /// before anything else runs. Enter on an empty line is accepted only while a timer rings (it
    /// is the silence). ESC over a tail stops it first, draft or not (the line's <c>softEscape</c>
    /// hook, <c>StopTailFirst</c>): the speaker's end nudges the read out with the draft kept, and
    /// <see cref="SpeechStoppedNotice"/> is printed here once the microphone is closed; the next
    /// ESC clears the draft. The flag, not the speaker's completion, decides the second press.
    /// </summary>
    private async Task<(InputResult Result, WakeHit? Hit, bool TailArmed)> ReadLineAsync(CancellationToken cancellationToken, IReadOnlyList<InputEvent>? replay = null)
    {
        using var wake = new CancellationTokenSource();
        using var alert = new CancellationTokenSource();
        bool armed = false;
        bool tailArmed = false;
        var tail = _speech.Playing;
        if (replay is not null)
        {
            // A queued line read at once (2026-09-18): nothing to listen for, no microphone armed.
        }
        else if (tail is not null && _tailInterrupt && _voice.InterruptReady)
        {
            var effective = _effective();
            var phrase = _voice.WakePhrase;
            int echoMatch = effective.SttInterruptEchoGuard;
            var confirm = TimeSpan.FromMilliseconds(effective.SttInterruptConfirmMs);
            var probe = tail.Probe;
            armed = _voice.ArmWake(wake, _ => IsEcho(tail, probe, phrase, echoMatch), WakeDetectorMode.Keyword, confirm);
            tailArmed = armed;
            if (!armed && _voice.InterruptStatusLine() is { } line)
            {
                _transcript.Warning(line);
            }
        }
        else if (tail is null && _voice.WakeReady)
        {
            armed = _voice.ArmWake(wake);
            if (!armed && _voice.WakeStatusLine() is { } line)
            {
                _transcript.Warning(line);
            }
        }

        Volatile.Write(ref _alertSignal, alert);
        if (_timers.HasAlerts || _processes.HasAlerts || LearnPending)
        {
            // Queued between the loop's drain and this arm: the read returns at once.
            alert.Cancel();
        }

        if (tail is not null)
        {
            // The tail's end is a nudge like an alert: the draft comes back and the next read
            // arms the microphone for an idle line. One from an earlier read costs a re-read.
            _ = tail.Completion.ContinueWith(static (_, state) => ((ChatScreen)state!).SignalAlert(), this, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        // The first ESC over a tail: the speech stops, the read goes on with the draft. Written
        // and read on the read's own task. Any speaker still owed audio counts (a spoken alert
        // not yet at the device too): at the idle line ESC has no cancel to protect.
        bool tailStopped = false;
        bool StopTailFirst()
        {
            if (tailStopped || _speech.Playing is null)
            {
                return false;
            }

            tailStopped = true;
            _speech.Stop();
            DiagnosticLog.Debug(SpeechSession.Category, TailStoppedLogLine);
            return true;
        }

        // Ctrl+C with no selection (2026-09-17): the tail first, as ESC; then, with nothing to
        // stop, the first press arms the exit and shows the hint, the next inside the window is
        // the exit (false: the read ends as InputResult.Exit). Typing in between does not disarm;
        // a sent line and a turn's start do.
        bool InterruptFirst()
        {
            if (StopTailFirst())
            {
                return true;
            }

            long now = _time.GetUtcNow().UtcTicks;
            if (now < Volatile.Read(ref _exitArmedUntil))
            {
                return false;
            }

            Volatile.Write(ref _exitArmedUntil, now + ExitConfirmWindow.Ticks);
            _pane.RefreshHint();
            return true;
        }

        WakeHit? hit = null;
        InputResult result;
        try
        {
            result = await _input.ReadAsync(
                allowEmpty: _timers.HasRinging,
                pushToTalk: _voice.Enabled ? _voice.PushToTalk : null,
                cancellationToken: cancellationToken,
                wake: armed ? wake.Token : default,
                alert: alert.Token,
                multiline: true,
                mentions: MentionFolderMode.Resolve(_effective()),
                pastePreview: Math.Clamp(_effective().PastePreviewLines, 0, PasteBlocks.MaxPreviewLines),
                softEscape: StopTailFirst,
                interrupt: InterruptFirst,
                intercept: TypoInterceptAsync,
                beforeCommit: DismissSplash,
                replay: replay,
                emptyArrow: step => CycleSplash(step) || StepPictureStrip(step),
                emptyDelete: DeleteSplash,
                emptyEnter: OpenStripPicture,
                // The chat line's own editor (2026-09-25): its draft lives on under the replies. A /draft replay reads on
                // a fresh one, so what was typed under the editor's wait stays on the row, unsent.
                editor: replay is null ? _input.Chat : null).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _alertSignal, null);
            if (armed)
            {
                hit = _voice.DisarmWake();
            }
        }

        if (tailStopped && await _speech.StopAsync().ConfigureAwait(false))
        {
            // The listener that reads the probe is disarmed; the speaker can be waited for and forgotten.
            _transcript.Notice(SpeechStoppedNotice);
        }

        return (result, hit, tailArmed);
    }

    /// <summary>The board's signal, on the clock's thread — and the tail's end, on the pool: end the idle read, if one is waiting. Never writes.</summary>
    private void SignalAlert()
    {
        if (Volatile.Read(ref _alertSignal) is { } source)
        {
            VoiceSession.SafeCancel(source);
        }
    }

    /// <summary>The queued alerts as lines, no speech: the mid-reply drain (the reply owns the device; the repeat is spoken at the idle line).</summary>
    private void PrintAlerts()
    {
        while (_timers.TryTakeAlert(out var alert))
        {
            _transcript.Alert(TimerText.AlertLine(alert));
        }

        PrintProcessAlerts();
    }

    /// <summary>The exits of notified background processes as lines (2026-09-21), never spoken: the model hears of them through the next turn's seeded poll.</summary>
    private void PrintProcessAlerts()
    {
        while (_processes.TryTakeAlert(out var alert))
        {
            _transcript.ProcessAlert(ShellText.AlertLine(alert));
        }
    }

    /// <summary>
    /// The loop top: the queued alerts as lines, then spoken as a tail under the input line when
    /// speech is on — the read opens at once, and Enter, ESC or a sent line silence it like any
    /// tail; a repeat replaces the tail before it. The read arms no microphone over a tail without
    /// the interrupt, so the device plays with the microphone closed. True when the app token ended it.
    /// </summary>
    private async Task<bool> AnnounceAlertsAsync(CancellationToken cancellationToken)
    {
        DrainLearn();
        PrintProcessAlerts();
        var sentences = new List<string>();
        while (_timers.TryTakeAlert(out var alert))
        {
            _transcript.Alert(TimerText.AlertLine(alert));
            sentences.Add(TimerText.AlertSpeech(alert));
        }

        if (sentences.Count == 0 || !(_effective().TtsOutput && _speech.IsReady))
        {
            return false;
        }

        await _speech.StopAsync().ConfigureAwait(false);
        var speaker = _speech.BeginTurn(cancellationToken);
        _tailInterrupt = false;
        if (speaker is not null)
        {
            speaker.Feed(string.Join(" ", sentences));
            speaker.CompleteAdding();
        }

        DrainDiagnostics();
        return cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// <c>/compact</c>, and the automatic one (<paramref name="autoPercent"/> is the share that
    /// fired, and the recent turns lose their older tool results too — the automatic compact must
    /// shrink a turn that filled the context by itself): the history shrunk per the LLM compact type (<see cref="ConversationCompactor"/>)
    /// under a spinner, ESC cancelling the summariser the way it cancels a reply, then one notice
    /// (<see cref="CompactionText"/>) written after the spinner, as the rule is. Nothing to do is a
    /// notice when asked and a log line when automatic (it would otherwise repeat on every
    /// message). A cancelled or failed compact leaves the history as it was.
    /// </summary>
    private async Task CompactAsync(string? focus, int? autoPercent, CancellationToken cancellationToken)
    {
        if (_session.Assistant is not { } assistant)
        {
            _transcript.Error(NoAssistantError);
            return;
        }

        var effective = _effective();
        var mode = CompactType.Resolve(effective);
        using var compactCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        _queuedClicks.Reset();
        var watcher = _keys.WatchAsync(compactCts, stop.Token, null, null, LiveLineHook, spend: e => { _queuedClicks.Reset(); return ScrollInput(e); }, onClick: _pane.Enabled ? HintClickLine : null, editor: LiveEditor);
        ConversationCompactor.Result? result = null;
        string? failure = null;
        bool cancelled = false;
        try
        {
            result = await _transcript.WithSpinnerAsync(CompactionText.CompactingLabel,
                () => ConversationCompactor.RunAsync(assistant, _session.Usage, mode, effective.LlmCompactKeepRecent, focus, compactCts.Token, pruneRecent: autoPercent is not null, protectSkills: SkillCompactMode.Resolve(effective))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (compactCts.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = assistant.ExplainFailure(ex);
        }
        finally
        {
            stop.Cancel();
            await watcher.ConfigureAwait(false);
        }

        if (cancelled)
        {
            DiagnosticLog.Info("Llm", CompactionText.Cancelled);
            _transcript.Notice(CompactionText.Cancelled);
        }
        else if (failure is not null)
        {
            DiagnosticLog.Info("Llm", CompactionText.FailedPrefix + failure);
            _transcript.Error(CompactionText.FailedPrefix + failure);
        }
        else if (result is null)
        {
            if (autoPercent is null)
            {
                _transcript.Notice(CompactionText.NothingToCompact);
            }
            else
            {
                DiagnosticLog.Info("Llm", "Auto-compact: nothing to compact.");
            }
        }
        else
        {
            string notice = CompactionText.Notice(result, autoPercent);
            DiagnosticLog.Info("Llm", notice);
            _transcript.Notice(notice);
            if (effective.LlmCompactShowSummary)
            {
                // LLM compact show summary (2026-09-21): what the compact did, dim under its notice —
                // the summary's lines, or one line per pruned result. The log keeps the notice alone.
                foreach (string line in CompactionText.DetailLines(result))
                {
                    _transcript.Notice(line);
                }
            }
            // The last turn is a summary or a stubbed shape now: nothing for /learn to read.
            _lastTrace = null;
            // The store follows: the rewritten history replaces the row's (2026-09-18).
            SaveSessionHistory(assistant);
        }

        DrainDiagnostics();
    }

    /// <summary>
    /// <c>/clear</c>: the start-of-app view again (terminal wiped, banner, then only what the
    /// settings cannot say: a failed probe's line, a discovered endpoint, the timers; no
    /// re-probe), then the conversation is forgotten. The wipe is the feedback. No spinner is
    /// live here, so writing is safe.
    /// </summary>
    private void ClearAndRefresh()
    {
        using (_pane.Batch())
        {
            RedrawScreen();
            _session.History.Clear();
            DropQueue();
            _lastTrace = null;
            _learnTrace = null;
            _session.Usage.ResetConversation();
            _pictureStrip.Clear();
            _log.Clear();
            ForgetReading();
            ForgetSession();
        }
    }

    /// <summary>
    /// A theme change (2026-09-23, the user's call: <c>/theme</c> and the Settings pane's <c>Theme</c>
    /// row work just like <c>/splash</c>): what is already drawn keeps the colours it was drawn in (the
    /// scrollback stores styled segments), so the screen starts over in the new ones — /clear's wipe
    /// and forgetting, the splash whatever <c>Welcome splash</c> says, and the saved line under it.
    /// The session was stored turn by turn, so <c>/sessions</c> brings it back, replayed in the new theme.
    /// </summary>
    private void RestartInTheme()
    {
        ThemeName.Apply(_effective());
        ClearAndRefresh();
        ShowSplash(force: true);
        _transcript.Notice(SettingsMenu.SavedNotice(SettingsField.Theme, _settings.Current, _settings.ProfileDirectory));
    }

    /// <summary>
    /// The start-of-app view again without the forgetting: the terminal wiped, the banner, then only
    /// what the settings cannot say — <see cref="ClearAndRefresh"/>'s screen half, and what a sent
    /// line over the welcome splash draws (<see cref="DismissSplash"/>). Any redraw of the banner
    /// takes the splash with it (the picture is drawn once, at startup).
    /// </summary>
    private void RedrawScreen()
    {
        using (_pane.Batch())
        {
            _renderScreen(_pane);
            ReportLlm(quiet: true);
            ReportSpeech(quiet: true);
            ReportVoice(quiet: true);
            ReportTimers();
        }

        ForgetSplash();
    }

    /// <summary>The splash is off the screen: its flags, its name, its count, its folder, its page and the delete's arm fall together (the tiles stay: they are read again only when their source or box changes).</summary>
    private void ForgetSplash()
    {
        _splashShown = false;
        _splashForced = false;
        _splashName = null;
        _splashCount = 0;
        _splashFolder = null;
        _splashPage = null;
        Volatile.Write(ref _splashDeleteArmedUntil, 0);
    }

    /// <summary>
    /// The welcome splash (2026-09-18): one of the embedded pictures (<see cref="SplashImages"/>,
    /// picked with the screen's <see cref="Random"/>) drawn once at startup, centred, filling the
    /// rows of the transcript region under the banner and the startup lines — the <c>/view</c>
    /// shape (<see cref="ThumbnailSize.Fit"/> with the flow's rows, <see cref="ScreenPane.FlowRow"/>, reserved too), so the aspect is
    /// kept and the banner stays. Behind <see cref="AppSettingsData.WelcomeSplashMode"/> and the pane;
    /// nothing without a picture source (headless, tests that pass none) or a picture the codecs
    /// refuse (logged at Trace by the loader). <c>Show image thumbnails</c> is not consulted.
    /// <paramref name="force"/> is <c>/splash</c>'s (later on 2026-09-19, the user's ask): the
    /// picture whatever <c>Welcome splash</c> says, and the arrows walk it whatever it says too
    /// (<see cref="CycleSplash"/> reads <c>_splashForced</c>). Since 2026-09-24 <c>Welcome splash</c> is
    /// a pick (<see cref="SplashMode"/>): <c>fullsize</c> is all of the above, <c>disabled</c> the old off,
    /// and <c>tiled</c> the first page of thumbnails instead (<see cref="ShowSplashPage"/>) — forced too,
    /// so <c>/splash</c> shows the style chosen, one picture when that is <c>disabled</c>.
    /// </summary>
    private void ShowSplash(bool force = false)
    {
        var style = SplashMode.Resolve(_effective());
        if (!_pane.Enabled || (!force && style == SplashStyle.Disabled) || CurrentSplash(log: true) is not { } source)
        {
            return;
        }

        if (style == SplashStyle.Tiled)
        {
            ShowSplashPage(source, 0);
            _splashForced = force && _splashShown;
            return;
        }

        if (SplashImages.Pick(_random, source.Names) is { } name)
        {
            ShowSplash(source, name);
            _splashForced = force && _splashShown;
        }
    }

    /// <summary>
    /// The pictures in force (later on 2026-09-19, the user's ask): the loaded profile's own
    /// <c>splash</c> folder while it holds an image file (<see cref="SplashImages.FromDirectory"/>,
    /// read live so a switch or a dropped file counts), else the source the screen was built with
    /// — the embedded set in the app, a test's list, or nothing (headless, tests that pass none:
    /// then no folder is looked at either).
    /// </summary>
    private SplashSource? CurrentSplash(bool log = false)
    {
        if (_splash is null)
        {
            return null;
        }

        string folder = Path.Combine(_settings.ProfileDirectory, SplashImages.ProfileFolderName);
        if (SplashImages.FromDirectory(folder) is { } own)
        {
            if (log)
            {
                DiagnosticLog.Debug("Splash", SplashImages.FolderLogLine(own.Names.Count, folder));
            }

            return own;
        }

        return _splash;
    }

    /// <summary>The named picture through <paramref name="source"/>'s loader, drawn as above; a picture that does not load draws nothing and leaves the splash gone.</summary>
    private void ShowSplash(SplashSource source, string name)
    {
        if (source.Load(name) is not { } image)
        {
            return;
        }

        var box = SplashArea();
        if (ImageThumbnail.Read(image, box.Columns, box.MaxRows) is not { } picture)
        {
            return;
        }

        _transcript.Picture(picture, RegisterPicture(image, sandbox: false));
        _splashShown = true;
        _splashName = name;
        _splashCount = source.Names.Count;
        _splashFolder = source.Directory;
        _splashPage = null;
    }

    /// <summary>The transcript's rows under the banner and the startup lines: the <c>/view</c> box with the flow's rows reserved too — the whole splash's, and the tiled splash's page.</summary>
    private ThumbnailBox SplashArea() =>
        ThumbnailSize.Fit(_pane.Profile.Width, _pane.Profile.Height, _pane.FlowRow + ScreenPane.PaneRows + _pane.InputRows + _pane.ToolbarRows);

    /// <summary>
    /// The tiled splash (2026-09-24, the user's ask: <c>Welcome splash</c> <c>tiled</c>): page
    /// <paramref name="page"/> (wrapped into range, so a resize or a removed file that leaves fewer pages
    /// still lands on one) of <paramref name="source"/>'s pictures as thumbnails at <c>Image thumbnail size</c>
    /// (<see cref="ThumbnailSize.Resolve"/> over <see cref="SplashArea"/>, so <c>fullsize</c> is one picture a
    /// page), in name order, only as many as fit under the banner (<see cref="SplashImages.Paginate"/>),
    /// tiled left to right by the sent pictures' own strip (<see cref="TranscriptRenderer.Images"/>) — a
    /// double-click opens one. The pictures are read once per source and box (<c>_splashTiles</c>); one
    /// that does not load is left out. Nothing, and the splash gone, when none loads.
    /// </summary>
    private void ShowSplashPage(SplashSource source, int page)
    {
        var area = SplashArea();
        var box = ThumbnailSize.Resolve(_effective(), area);
        var tiles = _splashTiles;
        if (tiles is null || tiles.Box != box || !string.Equals(tiles.Directory, source.Directory, StringComparison.Ordinal)
            || !tiles.Names.SequenceEqual(source.Names, StringComparer.Ordinal))
        {
            var thumbnails = new List<ImageThumbnail>(source.Names.Count);
            var ids = new List<int>(source.Names.Count);
            foreach (string name in source.Names)
            {
                if (source.Load(name) is { } image && ImageThumbnail.Read(image, box.Columns, box.MaxRows) is { } thumbnail)
                {
                    thumbnails.Add(thumbnail);
                    ids.Add(RegisterPicture(image, sandbox: false));
                }
            }

            tiles = _splashTiles = new SplashTiles(source.Names.ToArray(), source.Directory, box, thumbnails, ids);
        }

        var pages = SplashImages.Paginate(tiles.Thumbnails, _pane.Profile.Width, area.MaxRows);
        if (pages.Count == 0)
        {
            return;
        }

        int at = (page % pages.Count + pages.Count) % pages.Count;
        var (start, count) = pages[at];
        _transcript.Images(tiles.Thumbnails.GetRange(start, count), tiles.Ids.GetRange(start, count));
        _splashShown = true;
        _splashName = null;
        _splashCount = pages.Count;
        _splashFolder = null;
        _splashPage = at;
    }

    /// <summary>
    /// Whether Left / Right would walk the splash now (2026-09-20, the one rule under
    /// <see cref="CycleSplash"/> and the hint row): the splash on screen, the pane, the setting on
    /// — read live, so a flip mid-session stops the walk — unless <c>/splash</c> drew the picture,
    /// and two or more pictures at the show.
    /// </summary>
    private bool SplashArrowsOffered() =>
        _splashShown && _pane.Enabled && (_splashForced || SplashMode.Resolve(_effective()) != SplashStyle.Disabled) && _splashCount >= 2;

    /// <summary>
    /// Left or Right at an empty idle line while the welcome splash stands (2026-09-19, the user's
    /// ask): the previous (<paramref name="step"/> −1) or next (+1) picture of the source in force
    /// (<see cref="CurrentSplash"/>: the profile's folder or the embedded set) in
    /// <see cref="SplashSource.Names"/>' order, wrapping (<see cref="SplashImages.Next"/>) — the screen
    /// redrawn as the dismissal redraws it, under one batch, so the banner and the startup lines
    /// sit above the new picture and the pane comes back with the row. False — the key is the
    /// line's, a no-op on an empty draft — once the splash is gone, without the pane or a source,
    /// with <c>Welcome splash</c> off (read live, so a flip mid-session stops the walk — unless
    /// <c>/splash</c> drew the picture, which the setting never gates) or with
    /// fewer than two pictures. The input line's <c>emptyArrow</c> hook. Over the tiled splash
    /// (2026-09-24) the arrows page instead: the previous or next set, wrapping, re-paged live
    /// (<see cref="ShowSplashPage"/>), so a resize or a dropped file counts.
    /// </summary>
    private bool CycleSplash(int step)
    {
        if (_splashPage is { } page)
        {
            if (!SplashArrowsOffered() || CurrentSplash() is not { } pages)
            {
                return false;
            }

            bool pageForced = _splashForced;
            using (_pane.Batch())
            {
                RedrawScreen();
                ShowSplashPage(pages, page + step);
                _splashForced = pageForced && _splashShown;
            }

            return true;
        }

        if (!SplashArrowsOffered() || CurrentSplash() is not { } source || source.Names.Count < 2)
        {
            return false;
        }

        if (SplashImages.Next(source.Names, _splashName, step) is not { } next)
        {
            return false;
        }

        bool forced = _splashForced;
        using (_pane.Batch())
        {
            RedrawScreen();
            ShowSplash(source, next);
            _splashForced = forced && _splashShown;
        }

        return true;
    }

    /// <summary>
    /// Whether Delete would remove the splash picture now (2026-09-24): the splash on screen, the pane,
    /// and the picture one of the profile's own files (<see cref="SplashSource.Directory"/>) — never an
    /// embedded one. <c>Welcome splash</c> is not read: the picture is already there, and it is the user's file.
    /// </summary>
    private bool SplashDeleteOffered() =>
        _splashShown && _pane.Enabled && _splashFolder is not null && _splashName is not null;

    /// <summary>A first Delete over the profile's splash picture is still fresh: the next one in a row deletes it.</summary>
    private bool SplashDeleteArmed() => _time.GetUtcNow().UtcTicks < Volatile.Read(ref _splashDeleteArmedUntil);

    /// <summary>
    /// Delete at an empty idle line while one of the profile's own splash pictures stands (2026-09-24,
    /// the user's ask) — the input line's <c>emptyDelete</c> hook. The first press arms and shows
    /// <see cref="SplashDeleteArmedHint"/>; a second in a row (<paramref name="repeat"/>: the input line
    /// saw no other event between) within <see cref="SplashDeleteWindow"/> moves the file into the folder's
    /// <see cref="SplashImages.TrashFolderName"/> subfolder (<see cref="SplashImages.SetAside"/> — the
    /// user's call over a <c>File.Delete</c>: out of the walk, back by hand) and the screen is redrawn
    /// as <see cref="CycleSplash"/> redraws it over the folder's next picture in name order or, the folder
    /// now empty, a random one of the embedded set (<see cref="CurrentSplash"/> falls back by itself; the
    /// user's call too). A file that will not go is a Warning in the log and the picture stays. False —
    /// the key is the line's, nothing on an empty draft — over an embedded picture, once the splash is
    /// gone or without the pane.
    /// </summary>
    private bool DeleteSplash(bool repeat)
    {
        if (!SplashDeleteOffered() || _splashFolder is not { } folder || _splashName is not { } name)
        {
            return false;
        }

        if (!repeat || !SplashDeleteArmed())
        {
            Volatile.Write(ref _splashDeleteArmedUntil, _time.GetUtcNow().UtcTicks + SplashDeleteWindow.Ticks);
            _pane.RefreshHint();
            return true;
        }

        Volatile.Write(ref _splashDeleteArmedUntil, 0);
        // The neighbour is taken from the walk before the file goes, so the next picture is the one Right would show.
        IReadOnlyList<string> before = CurrentSplash() is { Directory: not null } walk ? walk.Names : [name];
        string? after = SplashImages.Next(before, name, +1);
        string path = Path.Combine(folder, name);
        string movedTo;
        try
        {
            movedTo = SplashImages.SetAside(folder, name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Splash", SplashImages.DeleteFailedLogLine(path, e));
            _pane.RefreshHint();
            return true;
        }

        DiagnosticLog.Info("Splash", SplashImages.DeletedLogLine(path, movedTo));
        bool forced = _splashForced;
        using (_pane.Batch())
        {
            RedrawScreen();
            if (CurrentSplash() is { } source)
            {
                string? next = source.Directory is not null && after is not null && source.Names.Contains(after, StringComparer.Ordinal)
                    ? after
                    : source.Directory is not null ? source.Names[0] : SplashImages.Pick(_random, source.Names);
                if (next is not null)
                {
                    ShowSplash(source, next);
                    _splashForced = forced && _splashShown;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The first sent line over the welcome splash — typed (the input line's <c>beforeCommit</c> hook,
    /// a message or a command alike) or spoken — wipes the screen back to the banner first, so the
    /// line lands under it; nothing once the splash is gone. <c>/clear</c> redraws
    /// the banner on its own and never brings the picture back; <c>/splash</c> is the one command
    /// that does (later on 2026-09-19).
    /// </summary>
    private void DismissSplash()
    {
        if (_splashShown)
        {
            RedrawScreen();
        }
    }

    /// <summary>
    /// <c>/new</c> (2026-09-16, the user's call): the conversation is forgotten as <c>/clear</c>
    /// forgets it — the history and the <c>/usage</c> conversation scope, so the next message seeds
    /// the opening clock and working-directory calls again — but the screen stays: the banner's
    /// sunset rule and <see cref="NewConversationNotice"/> under the transcript are the boundary and
    /// the feedback. Nothing is reprinted (no <c>LLM:</c> line, no timers: the lines that said so are
    /// still on screen) and the <c>/copy</c> log is kept, since the replies it indexes are too.
    /// </summary>
    private void StartNewConversation()
    {
        using (_pane.Batch())
        {
            _transcript.Rule();
            _transcript.Notice(NewConversationNotice);
        }

        _session.History.Clear();
        DropQueue();

        _lastTrace = null;
        _learnTrace = null;
        _session.Usage.ResetConversation();
        _pictureStrip.Clear();
        ForgetReading();
        ForgetSession();
    }

    /// <summary>The <c>/speak</c> reading and its hint part dropped: the conversation, the profile or the working directory changed under it.</summary>
    private void ForgetReading()
    {
        _reading = null;
        _echo = null;
        _hintReading = null;
    }

    /// <summary>
    /// Whether the saved settings name the endpoint: both the URL and the model. When they do, a
    /// quiet connect prints no <c>LLM:</c> line (the hint row's trailer shows the model, <c>/settings</c>
    /// the URL); a discovered server or a resolved model is news, and the <c>LLM:</c> line under the
    /// banner is the only place it appears. Pinned.
    /// </summary>
    public static bool SettingsNameEndpoint(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return !string.IsNullOrWhiteSpace(effective.LlmUrl) && !string.IsNullOrWhiteSpace(effective.LlmModel);
    }

    /// <summary>The <c>⏰</c> timers line after <c>/clear</c>, only when there are any: they survived, and the transcript that named them did not.</summary>
    private void ReportTimers()
    {
        if (TimerText.StatusLine(_timers.Snapshot()) is { } line)
        {
            _transcript.Notice(line);
        }
    }

    /// <summary>
    /// Resolves the LLM endpoint under the spinner and reports the outcome as transcript lines.
    /// With a blank URL and menus available the local ports are probed here (not in
    /// <see cref="LlmSession.ConnectAsync"/>) so that, when several servers answer — or any does at
    /// the app's start (<paramref name="startup"/>, 2026-09-23, the user's call: starting with no server
    /// set walks <c>/server</c>'s server, model, reasoning; a profile switch or a closed settings pane
    /// with one answer still connects to it quietly) — the user picks one after the spinner is gone: the
    /// pick is saved as the URL, the model and reasoning pickers follow (since 2026-09-23), and it connects
    /// as configured; ESC connects to the first in list order, unsaved, with neither picker, as a single
    /// answer off the start does.
    /// A configured URL, an override or a console without menus take the session's own path. A blank
    /// URL under <c>LLM scan mode</c> <c>disabled</c> asks nothing: the session's connect drops the
    /// old endpoint and resolves null at once, no spinner, and the report says why (2026-09-15).
    /// </summary>
    /// <param name="quiet">The banner was just drawn above, or the settings pane just closed: report only what the settings cannot say.</param>
    /// <param name="startup">The app's first connect (<see cref="RunAsync"/>): the server picker opens for a single answer too.</param>
    private async Task ConnectLlmAsync(CancellationToken cancellationToken, bool quiet = false, bool startup = false)
    {
        var effective = _effective();
        bool blankUrl = string.IsNullOrWhiteSpace(effective.LlmUrl);
        if (blankUrl && !Llm.LlmScanMode.Scans(Llm.LlmScanMode.Resolve(effective)))
        {
            await _session.ConnectAsync(effective, cancellationToken).ConfigureAwait(false);
            DrainDiagnostics();
            ReportLlm(quiet);
            return;
        }

        if (!blankUrl || !_menu.CanShowMenus())
        {
            if (await ConnectUnderWatchAsync(NoticeGlyphs.Llm, token => _transcript.WithSpinnerAsync(ConnectingLabel, () => _session.ConnectAsync(effective, token)), cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            ReportLlm(quiet);
            return;
        }

        IReadOnlyList<LlmServer> servers = Array.Empty<LlmServer>();
        if (await ConnectUnderWatchAsync(NoticeGlyphs.Llm, async token => servers = await _transcript.WithSpinnerAsync(SearchLabel(effective, ConnectingLabel), () => _session.DiscoverAsync(effective, token)).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (servers.Count > 0)
        {
            var picked = servers.Count > 1 || startup
                ? await _menu.PickServerAsync(servers, null, SettingsMenu.StartupServerTitle, cancellationToken).ConfigureAwait(false)
                : null;
            LlmEndpoint endpoint;
            if (picked is null)
            {
                endpoint = LlmEndpointProbe.Endpoint(servers[0], effective.LlmApiKey, ConfiguredModel(effective), configured: false);
            }
            else
            {
                // /server's walk (2026-09-23, the user's call): the URL saved, then the model over the list the
                // discovery already holds, then the reasoning level — one connect after, no second request.
                _menu.SaveServer(picked.BaseUrl);
                await _menu.PickModelFromListAsync(picked.Result, _settings.Current.LlmModel, cancellationToken).ConfigureAwait(false);
                await _menu.PickReasoningAsync("", _effective().LlmReasoning, cancellationToken).ConfigureAwait(false);
                effective = _effective();
                endpoint = LlmEndpointProbe.Endpoint(picked, effective.LlmApiKey, ConfiguredModel(effective), configured: true);
            }

            // The connect itself is instant; the spinner covers the context-window probe that follows it.
            if (await ConnectUnderWatchAsync(NoticeGlyphs.Llm, token => _transcript.WithSpinnerAsync(ConnectingLabel, () => _session.ConnectAsync(effective, endpoint, token)), cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        ReportLlm(quiet);
    }

    /// <summary>
    /// Runs one connect (its spinner inside <paramref name="connect"/>) under a Ctrl+C watcher
    /// (2026-09-17, the user's call: a model download or a slow probe is cancelled by the key, the
    /// app stays — the compact's shape, <see cref="Keys.IsInterrupt"/> alone as the cancel so an
    /// ESC typed under it stays type-ahead as it always has). The token handed to the connect is
    /// linked to the app's; the sessions either throw on it or swallow it into a "cancelled"
    /// result (the LLM probes do), so the token is read after, not only caught. True when the key
    /// cancelled it — <see cref="ConnectCancelledNotice"/> printed with <paramref name="glyph"/> (what was connecting, 2026-09-22), the caller reports nothing;
    /// the app token still propagates. Drains the diagnostics either way. No <c>spend</c> hook:
    /// nothing scrolls under a connect, and PgUp / PgDn typed there stay type-ahead for the pane
    /// they were meant for. <see cref="WaitUnderWatchAsync"/> is the body; <c>/draft</c>'s wait
    /// shares it with ESC as a cancel too (2026-09-19).
    /// </summary>
    private async Task<bool> ConnectUnderWatchAsync(string glyph, Func<CancellationToken, Task> connect, CancellationToken cancellationToken)
    {
        if (await WaitUnderWatchAsync(connect, Keys.IsInterrupt, cancellationToken).ConfigureAwait(false))
        {
            _transcript.Notice(ConnectCancelledNotice(glyph));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Runs <paramref name="work"/> (its spinner inside it) under a key watcher whose
    /// <paramref name="cancel"/> keys cancel the token handed to it — linked to the app's, read
    /// after the work as well as caught, since a session may swallow the cancel into a result.
    /// True when a key cancelled it (the caller prints its own notice); the app token still
    /// propagates. Drains the diagnostics either way.
    ///
    /// <para><paramref name="pointer"/> (2026-09-24, the user's ask: a <c>/loop</c> wait froze the
    /// transcript's pictures) gives the watcher a reply's mouse — the wheel and scroll keys through
    /// <see cref="ScrollInput"/>, clicks through <see cref="HintClickLine"/> (a picture's double-click
    /// opens it, a tool run folds) — for a wait long enough to read the transcript through. A word a
    /// click answers is dropped: no line hook runs panes here. It gives the row the chat line's live editor too
    /// (2026-09-25), a line sent there waiting for the idle line; a connect's short wait keeps the keys type-ahead,
    /// so an ESC typed under it still reaches the picker after it.</para>
    /// </summary>
    private async Task<bool> WaitUnderWatchAsync(Func<CancellationToken, Task> work, Func<ConsoleKeyInfo, bool> cancel, CancellationToken cancellationToken, bool pointer = false)
    {
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        if (pointer)
        {
            _queuedClicks.Reset();
        }

        var watcher = pointer
            ? _keys.WatchAsync(workCts, stop.Token, null, null, LiveLineHook, spend: e => { _queuedClicks.Reset(); return ScrollInput(e); }, cancel: cancel, onClick: _pane.Enabled ? HintClickLine : null, editor: LiveEditor)
            : _keys.WatchAsync(workCts, stop.Token, null, null, cancel: cancel);
        try
        {
            await work(workCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (workCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            stop.Cancel();
            await watcher.ConfigureAwait(false);
        }

        DrainDiagnostics();
        return workCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
    }

    /// <summary>The spinner label over a discovery: <see cref="ScanningLabel"/> when the settings' scan mode reaches the network, else <paramref name="fallback"/>.</summary>
    public static string SearchLabel(AppSettingsData effective, string fallback)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Llm.LlmScanMode.TryParse(effective.LlmScanMode, out var scope) && Llm.LlmScanMode.IncludesNetwork(scope) ? ScanningLabel : fallback;
    }

    /// <summary>The model id the settings name, or null for "the first listed" — <see cref="LlmEndpointProbe.ResolveAsync"/>'s rule.</summary>
    private static string? ConfiguredModel(AppSettingsData effective) =>
        string.IsNullOrWhiteSpace(effective.LlmModel) ? null : effective.LlmModel.Trim();

    /// <summary>
    /// <c>/server</c>: the local ports (and the endpoint in use, when it sits elsewhere) probed
    /// under a spinner, a pick from the ones that answered — or <c>/server &lt;url&gt;</c>, one URL
    /// probed and taken even when it does not answer, the configured-URL contract — saved as the
    /// LLM URL (the model id cleared when the server changed), then the model picker over the
    /// list the probe already holds, then the reasoning picker (2026-09-21, the user's call: a
    /// server switch sets URL, model and effort in one pass; ESC keeps the level, and it is offered
    /// whether or not the model step picked, like the model menu itself), then ONE reconnect that
    /// carries both saves. When a variable or flag overrides the URL the save and its warning are
    /// all that happens: nothing changed for this launch.
    /// </summary>
    private async Task HandleServerAsync(string args, CancellationToken cancellationToken)
    {
        var effective = _effective();
        LlmServer picked;
        if (string.IsNullOrWhiteSpace(args))
        {
            var scope = Llm.LlmScanMode.Resolve(effective);
            if (!Llm.LlmScanMode.Scans(scope))
            {
                // Disabled entirely (the user's call, 2026-09-15): no spinner, no request, the session as it was.
                _transcript.Error(LlmSession.NoServerLine(scope));
                _transcript.Notice(ScanDisabledHint);
                return;
            }

            var servers = await _transcript.WithSpinnerAsync(SearchLabel(effective, ServerSearchLabel), () => _session.ProbeServersAsync(effective, _session.Endpoint?.BaseUrl, cancellationToken)).ConfigureAwait(false);
            DrainDiagnostics();
            if (servers.Count == 0)
            {
                _transcript.Error(LlmSession.NoServerLine(scope));
                _transcript.Notice(NoServerHint);
                return;
            }

            var choice = await _menu.PickServerAsync(servers, _session.Endpoint?.BaseUrl, SettingsMenu.ServerTitle, cancellationToken).ConfigureAwait(false);
            if (choice is null)
            {
                return;
            }

            picked = choice;
        }
        else
        {
            Uri url;
            try
            {
                url = LlmEndpoint.NormalizeBaseUrl(args);
            }
            catch (ArgumentException ex)
            {
                _transcript.Error(SettingsMenu.ServerUrlError(ex.Message));
                return;
            }

            picked = await _transcript.WithSpinnerAsync(ServerSearchLabel, () => _session.ProbeServerAsync(url, effective, cancellationToken)).ConfigureAwait(false);
            DrainDiagnostics();
            if (!picked.Result.Exists)
            {
                _transcript.Warning(SettingsMenu.ServerNotAnsweringWarning(picked.BaseUrl, picked.Result.Detail));
            }
        }

        _menu.SaveServer(picked.BaseUrl);
        if (_overriddenBy(SettingsField.LlmUrl) is not null)
        {
            return;
        }

        await _menu.PickModelFromListAsync(picked.Result, _settings.Current.LlmModel, cancellationToken).ConfigureAwait(false);
        await _menu.PickReasoningAsync("", _effective().LlmReasoning, cancellationToken).ConfigureAwait(false);
        await ConnectLlmAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>LLM:</c> line for the endpoint as it stands. Quiet (<paramref name="quiet"/>: under the
    /// banner, or after the settings pane) only the no-server error and an endpoint the saved settings
    /// do not name (<see cref="SettingsNameEndpoint"/>: the discovered server, the resolved model) are
    /// printed; a configured server that did not answer already drained its warning.
    /// </summary>
    private void ReportLlm(bool quiet = false)
    {
        if (_session.Endpoint is null)
        {
            var scope = Llm.LlmScanMode.Resolve(_effective());
            _transcript.Error(LlmSession.NoServerLine(scope));
            _transcript.Notice(NoServerHintFor(scope));
        }
        else if (!quiet || !SettingsNameEndpoint(_effective()))
        {
            _transcript.Notice(NoticeGlyphs.Llm + LlmSession.ConnectedLine(_session.Endpoint));   // the screen's glyph (2026-09-22); the log and headless keep the bare line
        }
    }

    /// <summary>
    /// Readies speech output (under a spinner, only when speech is on: the server probed, or the
    /// in-process model downloaded — the label following the download and load as the voice
    /// connect's does — and loaded) and prints the <c>TTS:</c> line. Cancelling a download is
    /// Ctrl+C under the spinner (<see cref="ConnectUnderWatchAsync"/>); the app stays.
    /// </summary>
    /// <param name="quiet">The banner was just drawn above, or the settings pane just closed: only a warning is printed.</param>
    private async Task ConnectSpeechAsync(CancellationToken cancellationToken, bool quiet = false)
    {
        var effective = _effective();
        if (effective.TtsOutput)
        {
            string label = TtsSource.Resolve(effective) == TtsEngine.InProcess ? SpeechLoadingLabel : SpeechConnectingLabel;
            if (await ConnectUnderWatchAsync(NoticeGlyphs.Tts, token => _transcript.WithSpinnerAsync(label, async setLabel =>
            {
                await _speech.ConnectAsync(effective, setLabel, token).ConfigureAwait(false);
                return true;
            }), cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        else
        {
            await _speech.ConnectAsync(effective, null, cancellationToken).ConfigureAwait(false);
            DrainDiagnostics();
        }

        ReportSpeech(quiet);
    }

    /// <summary>The <c>TTS:</c> line for the speech session as it stands; quiet, only when it is a warning (the settings tabs show the rest).</summary>
    private void ReportSpeech(bool quiet = false)
    {
        if (_speech.StatusIsWarning)
        {
            _transcript.Warning(_speech.StatusLine());
        }
        else if (!quiet)
        {
            _transcript.Notice(_speech.StatusLine());
        }
    }

    /// <summary>
    /// Prepares voice input (under a spinner whose label follows the model download and load,
    /// only when voice input is on) and prints the <c>Voice:</c> line. Cancelling a download is
    /// Ctrl+C under the spinner (<see cref="ConnectUnderWatchAsync"/>); the app stays.
    /// </summary>
    /// <param name="quiet">The banner was just drawn above, or the settings pane just closed: only the warnings are printed.</param>
    private async Task ConnectVoiceAsync(CancellationToken cancellationToken, bool quiet = false)
    {
        var effective = _effective();
        if (effective.SttInput)
        {
            if (await ConnectUnderWatchAsync(NoticeGlyphs.Stt, token => _transcript.WithSpinnerAsync(VoiceConnectingLabel, async setLabel =>
            {
                await _voice.ConnectAsync(effective, setLabel, token).ConfigureAwait(false);
                return true;
            }), cancellationToken).ConfigureAwait(false))
            {
                _interrupts.Reset();
                return;
            }
        }
        else
        {
            await _voice.ConnectAsync(effective, null, cancellationToken).ConfigureAwait(false);
            DrainDiagnostics();
        }

        _interrupts.Reset();   // a probe is the operator's "try again"
        ReportVoice(quiet);
    }

    /// <summary>The <c>Voice:</c> line (and the wake / interrupt warnings) for the voice session as it stands; quiet, the warnings alone.</summary>
    private void ReportVoice(bool quiet = false)
    {
        if (_voice.StatusIsWarning)
        {
            _transcript.Warning(_voice.StatusLine());
        }
        else if (!quiet)
        {
            _transcript.Notice(_voice.StatusLine());
        }

        if (_voice.WakeStatusLine() is { } wakeLine)
        {
            _transcript.Warning(wakeLine);
        }

        if (_voice.InterruptStatusLine() is { } interruptLine)
        {
            _transcript.Warning(interruptLine);
        }
    }

    /// <summary>
    /// Readies the MCP servers (2026-09-20): every server that is on started under a spinner
    /// (<see cref="McpText.ConnectingLabel"/>, the label following the count), the fourth startup
    /// connect after the LLM, TTS and STT ones and the same after a profile switch, a master-switch
    /// flip or a <c>/mcp</c> edit that asks for it. Ctrl+C under the spinner cancels the wave
    /// (<see cref="ConnectUnderWatchAsync"/>): the app stays, the rows read <c>failed: cancelled</c>.
    /// With nothing on (the switch off, no server named, every one disabled or shadowed) the
    /// session still runs its connect — it drops whatever ran — and nothing is printed.
    /// </summary>
    private async Task ConnectMcpAsync(CancellationToken cancellationToken)
    {
        var effective = _effective();
        var merged = _mcp.Read();
        var disabled = ToolsText.DisabledSet(effective.McpServersDisabled);
        bool any = effective.McpServers && merged.Entries.Any(e => e.Startable && !disabled.Contains(e.Name));
        if (any)
        {
            if (await ConnectUnderWatchAsync(NoticeGlyphs.Mcp, token => _transcript.WithSpinnerAsync(McpText.ConnectingLabel, async setLabel =>
            {
                await _mcp.ConnectAllAsync(effective, setLabel, token).ConfigureAwait(false);
                return true;
            }), cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        else
        {
            try
            {
                await _mcp.ConnectAllAsync(effective, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The app token: nothing to connect anyway; the caller's loop ends at once.
                return;
            }

            DrainDiagnostics();
        }

        ReportMcp();
    }

    /// <summary>
    /// The <c>🔌 MCP:</c> line for the session as it stands — printed whenever a server was attempted,
    /// quiet or not (no settings tab names the outcome, the <see cref="ReportLlm"/> reasoning) — and one
    /// warning per failed server; nothing when no server was on.
    /// </summary>
    private void ReportMcp()
    {
        if (_mcp.StatusLine() is { } line)
        {
            _transcript.Notice(line);
        }

        foreach (string warning in _mcp.WarningLines())
        {
            _transcript.Warning(warning);
        }
    }

    /// <summary>The notice when a /command was sent with pictures on the line: they go with a message only. Pinned.</summary>
    public static string ImagesIgnoredNotice(int count) =>
        count == 1 ? "(the image was ignored: a /command takes none)" : $"({count.ToString(CultureInfo.InvariantCulture)} images were ignored: a /command takes none)";

    /// <summary>The model picker (<c>/model [id]</c>; the hint row's model name was its double-click from 2026-09-18 until it became <c>/server</c>'s, 2026-09-22) and the reconnect a pick asks for.</summary>
    private async Task PickModelAsync(string args, CancellationToken cancellationToken)
    {
        if (await _menu.PickModelAsync(_session, args, cancellationToken).ConfigureAwait(false))
        {
            await ConnectLlmAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The reasoning picker (<c>/reasoning [level]</c>, or a double-click on the reasoning mark in
    /// the hint row, 2026-09-21) and the quiet reconnect a pick asks for: the saved notice is the
    /// feedback and the endpoint is unchanged, so the reconnect prints only what went wrong (the
    /// <c>/settings</c> rule; <c>/model</c>'s <c>LLM:</c> line IS its answer).
    /// </summary>
    private async Task PickReasoningAsync(string args, CancellationToken cancellationToken)
    {
        if (await _menu.PickReasoningAsync(args, _effective().LlmReasoning, cancellationToken).ConfigureAwait(false))
        {
            await ConnectLlmAsync(cancellationToken, quiet: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The settings menu (<c>/settings</c>, or a double-click on the hint row, 2026-09-18) and what its changes ask for afterwards: a profile switch reconnects everything,
    /// an LLM / TTS / STT change its own session quietly, the tools switch forgets the conversation.
    /// </summary>
    private async Task OpenSettingsAsync(CancellationToken cancellationToken)
    {
        var changes = await _menu.ShowAsync(cancellationToken).ConfigureAwait(false);

        // The command's line silenced any reply tail, so a speaker still playing here is
        // the voice picker's preview: a tail without the turn's interrupt, like an alert.
        _tailInterrupt = false;
        if (changes.HasFlag(SettingsChanges.Profile))
        {
            // Another profile: every session may differ, so all three reconnect here.
            await AfterProfileSwitchAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await ApplySettingsChangesAsync(changes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The reconnects a settings change asks for, short of a profile switch: the LLM, speech, voice
    /// and MCP sessions each when their fields changed, quietly (the pane that closed — or the
    /// <c>/profile reload</c> notice, 2026-09-21 — was the feedback, so a reconnect prints only what
    /// went wrong), and the conversation forgotten when the tools switch flipped.
    /// </summary>
    private async Task ApplySettingsChangesAsync(SettingsChanges changes, CancellationToken cancellationToken)
    {
        if (changes.HasFlag(SettingsChanges.Llm))
        {
            await ConnectLlmAsync(cancellationToken, quiet: true).ConfigureAwait(false);
        }

        if (changes.HasFlag(SettingsChanges.Tts))
        {
            await ConnectSpeechAsync(cancellationToken, quiet: true).ConfigureAwait(false);
        }

        if (changes.HasFlag(SettingsChanges.Voice))
        {
            await ConnectVoiceAsync(cancellationToken, quiet: true).ConfigureAwait(false);
        }

        if (changes.HasFlag(SettingsChanges.Mcp))
        {
            await ConnectMcpAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changes.HasFlag(SettingsChanges.Theme))
        {
            // The fresh start forgets the conversation too, so the tools switch below has nothing left to clear.
            RestartInTheme();
            return;
        }

        if (changes.HasFlag(SettingsChanges.Conversation))
        {
            // The history's shape follows the LLM offer tools switch (see SettingsChanges.Conversation):
            // forgotten like /clear, without the wipe — the pane's status line was the feedback
            // for the row, this line is it for the conversation.
            _session.History.Clear();
            DropQueue();
            _lastTrace = null;
            _learnTrace = null;
            _session.Usage.ResetConversation();
            _pictureStrip.Clear();
            _log.Clear();
            ForgetSession();
            _transcript.Notice(ToolsChangedNotice(_effective().LlmOfferTools));
        }
    }

    /// <summary>A typed line classified, <c>/log</c> a command only under <c>--log</c> (<see cref="_logFile"/>, 2026-09-22).</summary>
    private (SlashCommand Command, string Args) ParseLine(string text) => SlashCommands.Parse(text, _logFile is not null);

    /// <summary>
    /// Dispatches one submitted line (<see cref="HandleOnceAsync"/>), then what a double-click off
    /// the pane it opened named (later on 2026-09-21, the user's ask): the pane's own word — its
    /// toolbar glyph, the model name under the model picker, the path under the folder picker, the
    /// blanks under the settings — ends there, the pane closed; another pane's word opens that
    /// pane, which may be switched from in turn (<see cref="ScreenPane.TakeDismissHit"/>,
    /// <see cref="OffPaneLine"/>). Returns true when the shell should exit.
    /// </summary>
    private async Task<bool> HandleAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (command, _) = ParseLine(text);
            if (await HandleOnceAsync(text, images, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (_pane.TakeDismissHit() is not { } hit || OffPaneLine(hit) is not { } next || ParseLine(next).Command == command)
            {
                return false;
            }

            text = next;
            images = [];
        }
    }

    /// <summary>
    /// Dispatches one submitted line; <paramref name="images"/> are the pictures its <c>[Image #n]</c>
    /// labels name, which only a message carries (a command drops them with a notice; its
    /// arguments keep the labels). Returns true when the shell should exit.
    /// </summary>
    private async Task<bool> HandleOnceAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        var (command, args) = ParseLine(text);
        if (command != SlashCommand.None)
        {
            DiagnosticLog.Debug(AppCategory, CommandLogLine(command, text));
        }

        if (command != SlashCommand.None && images.Count > 0)
        {
            _transcript.Notice(ImagesIgnoredNotice(images.Count));
        }

        switch (command)
        {
            case SlashCommand.Help:
                if (_pane.Enabled)
                {
                    // The info pane over the input row; the read has ended, so the microphone is
                    // closed and no watcher runs — the pane reads the keys until ESC.
                    await _info.ShowAsync(InfoPane.Title, HelpTabs(), 0, cancellationToken).ConfigureAwait(false);
                    return false;
                }

                // No pane to open (a redirected console): the list in the transcript.
                foreach (var line in (_logFile is null ? SlashCommands.HelpText : SlashCommands.HelpTextWithLog).Split('\n'))
                {
                    _transcript.Notice(line);
                }

                return false;

            case SlashCommand.Clear:
                ClearAndRefresh();
                return false;

            case SlashCommand.New:
                StartNewConversation();
                return false;

            case SlashCommand.Splash:
                // The startup view over a fresh conversation (later on 2026-09-19, the user's ask):
                // /clear's wipe and forgetting, then the picture whatever Welcome splash says.
                ClearAndRefresh();
                ShowSplash(force: true);
                return false;

            case SlashCommand.Theme:
                if (await _menu.PickThemeAsync(args, cancellationToken).ConfigureAwait(false))
                {
                    RestartInTheme();
                }

                return false;

            case SlashCommand.Compact:
                await CompactAsync(args.Length > 0 ? args : null, autoPercent: null, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Exit:
                _exitReason = ExitByCommand;
                return true;

            case SlashCommand.Server:
                await HandleServerAsync(args, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Model:
                await PickModelAsync(args, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Reasoning:
                await PickReasoningAsync(args, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Settings:
                await OpenSettingsAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Tts or SlashCommand.Voice or SlashCommand.Wake or SlashCommand.Interrupt:
                await HandleSwitchAsync(command, args, midTurn: false, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Remember:
                Remember(args);
                return false;

            case SlashCommand.Memory:
                await HandleMemoryAsync(args, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Queue:
                await HandleQueueAsync(args, cancellationToken).ConfigureAwait(false);
                return false;
            case SlashCommand.Session:
                await HandleSessionAsync(args, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Persona:
                await HandlePromptFileAsync(_persona, "/persona", args, PersonaCreatedNotice, PersonaOpenedNotice, PersonaOpenFailedError, spoken: false, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Operata:
                await HandlePromptFileAsync(_operata, "/operata", args, OperataCreatedNotice, OperataOpenedNotice, OperataOpenFailedError, spoken: false, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Vocalia:
                await HandlePromptFileAsync(_vocalia, "/vocalia", args, VocaliaCreatedNotice, VocaliaOpenedNotice, VocaliaOpenFailedError, spoken: true, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Sys:
                if (_pane.Enabled)
                {
                    await _info.ShowAsync(SystemPromptSummary.Label, SysPromptTabs(), 0, cancellationToken).ConfigureAwait(false);
                    return false;
                }

                // No pane to open (a redirected console): the summary in the transcript.
                foreach (var line in SystemPromptSummary.PromptLines(SystemPromptFacts()))
                {
                    _transcript.Notice(line);
                }

                foreach (var line in SystemPromptSummary.ToolLines(ToolGroups()))
                {
                    _transcript.Notice(line);
                }

                return false;

            case SlashCommand.Profile:
                await HandleProfileAsync(args, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.CmdCopy:
                await HandleCmdCopyAsync(args, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Timer:
                HandleTimer(args);
                return false;

            case SlashCommand.Cwd:
                await HandleCwdAsync(args, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Tree:
                HandleTree(args);
                return false;

            case SlashCommand.Vault:
                HandleVault(args);
                return false;

            case SlashCommand.Explore:
                HandleExplore(args);
                return false;

            case SlashCommand.Git:
                HandleGit(args);
                return false;

            case SlashCommand.Speak:
                _ = HandleSpeak(args, cancellationToken);
                return false;

            case SlashCommand.View:
                HandleView(args);
                return false;

            case SlashCommand.Imagine:
                _ = await HandleImagineAsync(args, cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Comfy:
                if (string.Equals(args.Trim(), ComfyPurgeWord, StringComparison.OrdinalIgnoreCase))
                {
                    await PurgeComfyAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }

                if (args.Length > 0)
                {
                    HandleComfyVerb(args);
                    return false;
                }

                await HandleComfyAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Echo:
                HandleEcho(args, cancellationToken);
                return false;

            case SlashCommand.Copy:
                HandleCopy(args);
                return false;

            case SlashCommand.Draft:
                await HandleDraftAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.EmptyTrash:
                await EmptyTrashAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.CmdClear:
                await CmdClearAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Log:
                HandleLog();
                return false;

            case SlashCommand.Window:
                _transcript.Notice(WindowNotice(_pane.Profile.Width, _pane.Profile.Height));
                return false;

            case SlashCommand.Usage:
                await ShowUsageAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Expand or SlashCommand.Collapse:
                // The transcript's tool runs and code blocks (2026-09-22; /tools expand|collapse until later that day), no pane.
                SetFolds(command == SlashCommand.Expand);
                return false;

            case SlashCommand.Tools:
                // The Tools pane (2026-09-19): every tool on or off by name, the Ask / Files / Web rows after it; the four tabs as lines without the pane.
                await _toolsMenu.ShowAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.CmdList:
                // The Shell allowed commands list straight (2026-09-21): the Tools pane's row without the pane around it, the toolbar lock's word.
                await _toolsMenu.ShowAllowedCommandsAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Police:
                // The Shell police outside paths page straight (2026-09-22): the Tools pane's row without the pane around it, the toolbar officer's word.
                await _toolsMenu.ShowPoliceAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Mcp:
                // The MCP pane (2026-09-20): the servers, their tools, the two Options rows; the master switch saved there reconnects here, as /settings' rows do after the pane closes.
                if ((await _mcpMenu.ShowAsync(cancellationToken).ConfigureAwait(false)).HasFlag(SettingsChanges.Mcp))
                {
                    await ConnectMcpAsync(cancellationToken).ConfigureAwait(false);
                }

                return false;

            case SlashCommand.Skills:
                // The tabbed menu on the pane (a skill row's Enter opens the scope page), the three
                // tabs as lines without one, whatever the switches say. No argument since later on
                // 2026-09-18 (the user's call): /skill <name> [message] seeded the skill's load_skill
                // pair ahead of the reply; the #-mention is the way now. /skills edit <name> opened
                // the skill's SKILL.md from 2026-09-21 until 2026-09-23, when the scope page's edit row
                // took over (the user's call); /skills takes no argument since, so one is the
                // Overloaded line, as for /tools.
                await _skillsMenu.ShowAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case SlashCommand.Loop:
                return await HandleLoopAsync(args, images, cancellationToken).ConfigureAwait(false);

            case SlashCommand.BotChat:
                return await HandleBotChatAsync(args, cancellationToken).ConfigureAwait(false);

            case SlashCommand.Learn:
                HandleLearn(args, cancellationToken);
                return false;

            case SlashCommand.About:
                if (_pane.Enabled)
                {
                    await _info.ShowAsync(AboutText.Label, AboutTabs(), 0, cancellationToken).ConfigureAwait(false);
                    return false;
                }

                // No pane to open (a redirected console): the three tabs in the transcript.
                foreach (var line in AboutText.Lines(AboutFacts()))
                {
                    _transcript.Notice(line);
                }

                return false;

            case SlashCommand.Unknown:
                _transcript.Error(UnknownCommandError(text.Trim().Split(' ', 2)[0]));
                return false;
            case SlashCommand.Overloaded:
                _transcript.Error(NoArgumentError(text.Trim().Split(' ', 2)[0]));
                return false;

            default:
                return await RunMessageAsync(text, images, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The four speech switches — <c>/tts</c>, <c>/stt</c>, <c>/wake</c>, <c>/interrupt</c> — one
    /// body for the idle line and the turn (<paramref name="midTurn"/>): the setting is saved
    /// either way; the reconnect that shows it runs here at the idle line and is owed to the
    /// turn's end mid-turn (<see cref="Defer"/>, with <see cref="MidTurnSwitchNotice"/> as the
    /// line). <c>/tts off</c> mid-turn also silences the reply now, as ESC on a tail does.
    /// </summary>
    private async Task HandleSwitchAsync(SlashCommand command, string args, bool midTurn, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case SlashCommand.Tts:
                if (!TryParseSwitch(args, out bool? on))
                {
                    _transcript.Error(TtsUsageError);
                    return;
                }

                bool ttsWanted = on ?? !_settings.Current.TtsOutput;
                _settings.Update(d => d.TtsOutput = ttsWanted);
                if (midTurn)
                {
                    if (!ttsWanted && _speech.Playing is not null)
                    {
                        _speech.Stop();
                        _transcript.Notice(SpeechStoppedNotice);
                    }

                    Defer(SettingsChanges.Tts, MidTurnSwitchNotice(SpeechOutputWord, ttsWanted));
                    return;
                }

                await ConnectSpeechAsync(cancellationToken).ConfigureAwait(false);
                return;

            case SlashCommand.Voice:
                if (!TryParseSwitch(args, out bool? voiceOn))
                {
                    _transcript.Error(VoiceUsageError);
                    return;
                }

                bool voiceWanted = voiceOn ?? !_settings.Current.SttInput;
                _settings.Update(d => d.SttInput = voiceWanted);
                if (midTurn)
                {
                    Defer(SettingsChanges.Voice, MidTurnSwitchNotice(VoiceInputWord, voiceWanted));
                    return;
                }

                await ConnectVoiceAsync(cancellationToken).ConfigureAwait(false);
                return;

            case SlashCommand.Wake:
                if (!TryParseSwitch(args, out bool? wakeOn))
                {
                    _transcript.Error(WakeUsageError);
                    return;
                }

                bool wakeWanted = wakeOn ?? !_settings.Current.SttWake;
                bool takesInterrupt = !wakeWanted && _settings.Current.SttInterrupt;   // the interrupt needs the wake word: off with it
                _settings.Update(d =>
                {
                    d.SttWake = wakeWanted;
                    if (!wakeWanted)
                    {
                        d.SttInterrupt = false;
                    }
                });
                if (!_effective().SttInput)
                {
                    // Nothing to probe: the switch is saved and takes effect with /stt.
                    _transcript.Notice(wakeWanted ? WakeOnNeedsVoiceNotice : WakeOffNotice);
                    if (takesInterrupt)
                    {
                        _transcript.Notice(InterruptOffWithWakeNotice);
                    }

                    return;
                }

                if (midTurn)
                {
                    Defer(SettingsChanges.Voice, MidTurnSwitchNotice(WakeWordWord, wakeWanted));
                }
                else
                {
                    await ConnectVoiceAsync(cancellationToken).ConfigureAwait(false);
                }

                if (takesInterrupt)
                {
                    _transcript.Notice(InterruptOffWithWakeNotice);
                }

                return;

            case SlashCommand.Interrupt:
                if (!TryParseSwitch(args, out bool? interruptOn))
                {
                    _transcript.Error(InterruptUsageError);
                    return;
                }

                bool interruptWanted = interruptOn ?? !_settings.Current.SttInterrupt;
                if (interruptWanted && !_settings.Current.SttWake)
                {
                    // Nothing saved: the interrupt is the wake phrase during a reply, so it needs the wake word on.
                    _transcript.Notice(InterruptNeedsWakeNotice);
                    return;
                }

                _settings.Update(d => d.SttInterrupt = interruptWanted);
                if (!_effective().SttInput)
                {
                    _transcript.Notice(_settings.Current.SttInterrupt ? InterruptOnNeedsVoiceNotice : InterruptOffNotice);
                    return;
                }

                if (midTurn)
                {
                    Defer(SettingsChanges.Voice, MidTurnSwitchNotice(InterruptWord, interruptWanted));
                }
                else
                {
                    await ConnectVoiceAsync(cancellationToken).ConfigureAwait(false);
                }

                if (_settings.Current.SttInterrupt && !_effective().TtsOutput)
                {
                    _transcript.Notice(InterruptNeedsSpeechNotice);
                }

                return;
        }
    }

    /// <summary>
    /// <c>/profile</c>: the picker, a switch by name, <c>add</c> (a copy of the loaded profile's
    /// saved settings — never the effective ones, a variable must not be baked into a file — then
    /// the switch), <c>delete</c> (refused for the default and the loaded profile, confirmed on
    /// the input line like <c>/memory forget</c>), <c>reset [name]</c> (any profile, the loaded one
    /// without a name, confirmed the same way) or <c>rename &lt;name&gt; &lt;new-name&gt;</c> (another
    /// profile's directory moved, no confirmation — nothing is lost). Runs only from the input
    /// line: no turn in flight, the microphone already closed.
    /// </summary>
    private async Task HandleProfileAsync(string args, CancellationToken cancellationToken)
    {
        var action = ParseProfileArgs(args);
        string home = _settings.StorageDirectory;
        try
        {
            switch (action.Kind)
            {
                case ProfileActionKind.Pick:
                    if (await _menu.PickProfileAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await AfterProfileSwitchAsync(cancellationToken).ConfigureAwait(false);
                    }

                    return;

                case ProfileActionKind.Switch:
                    if (Profiles.Resolve(home, action.Name) is not { } target)
                    {
                        _transcript.Error(ProfileMissingError(action.Name));
                        return;
                    }

                    if (await _menu.SwitchProfileAsync(target).ConfigureAwait(false))
                    {
                        await AfterProfileSwitchAsync(cancellationToken).ConfigureAwait(false);
                    }

                    return;

                case ProfileActionKind.Add:
                    if (!Profiles.IsValidName(action.Name))
                    {
                        _transcript.Error(ProfileNameError);
                        return;
                    }

                    if (Profiles.Resolve(home, action.Name) is { } existing)
                    {
                        _transcript.Error(ProfileExistsError(existing));
                        return;
                    }

                    // The saved values, never the effective ones; and its own files folder, not this one's
                    // (a sandbox is the sidekick's own). The memories come along under either mode, the
                    // persona, operating rules and voice directive under the advanced one, each when it
                    // exists on disk (every one is written synchronously, so the disk is current).
                    var seed = AppSettings.Copy(_settings.Current);
                    seed.WorkingDirectory = "";
                    Profiles.Create(home, action.Name, seed);
                    IReadOnlyList<string> copied = Profiles.CopySidekickFiles(
                        _settings.ProfileDirectory, home, action.Name, NewProfileMode.FilesFor(NewProfileMode.Resolve(_effective())));
                    if (await _menu.SwitchProfileAsync(action.Name).ConfigureAwait(false))
                    {
                        await AfterProfileSwitchAsync(cancellationToken, ProfileCreatedNotice(action.Name, copied)).ConfigureAwait(false);
                    }

                    return;

                case ProfileActionKind.Delete:
                    await DeleteProfileAsync(action.Name, cancellationToken).ConfigureAwait(false);
                    return;

                case ProfileActionKind.Reset:
                    await ResetProfileAsync(action.Name, cancellationToken).ConfigureAwait(false);
                    return;

                case ProfileActionKind.Rename:
                    RenameProfile(action.Name, action.NewName);
                    return;

                case ProfileActionKind.Edit:
                {
                    // The pending save written first, so the editor opens the current values; a file
                    // not there yet (a profile never saved) is created by that same write.
                    bool existed = File.Exists(_settings.FilePath);
                    await _settings.FlushAsync().ConfigureAwait(false);
                    try
                    {
                        _openFile(_settings.FilePath);
                        _transcript.Notice(existed ? ProfileEditOpenedNotice(_settings.ProfileName) : ProfileEditCreatedNotice(_settings.ProfileName));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        _transcript.Error(ProfileEditFailedError(ex.Message));
                    }

                    return;
                }

                case ProfileActionKind.Reload:
                {
                    // The file over the memory, then only what changed is rebuilt — a settings edit,
                    // not a switch: the conversation, the screen and the usage stay.
                    var before = _settings.Current;
                    _settings.Reload();
                    var changes = SettingsDiff.Changes(before, _settings.Current);
                    _transcript.Notice(ProfileReloadedNotice(_settings.ProfileName, changes.Count));
                    await ApplySettingsChangesAsync(ReloadChanges(changes), cancellationToken).ConfigureAwait(false);
                    return;
                }

                default:
                    _transcript.Error(ProfileUsageError);
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _transcript.Error(ProfileFailedError(ex.Message));
        }
        finally
        {
            DrainDiagnostics();
        }
    }

    private async Task DeleteProfileAsync(string name, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        if (Profiles.Resolve(home, name) is not { } target)
        {
            _transcript.Error(ProfileMissingError(name));
            return;
        }

        if (Profiles.DeleteRefusal(target, _settings.ProfileName) is { } refusal)
        {
            _transcript.Error(refusal);
            return;
        }

        if (!await ConfirmAsync(DeleteProfilePrompt(target), cancellationToken).ConfigureAwait(false))
        {
            _transcript.Notice(KeptNotice);
            return;
        }

        try
        {
            Profiles.Delete(home, target);
            _transcript.Notice(ProfileDeletedNotice(target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _transcript.Error(ProfileDeleteFailedError(ex.Message));
        }
    }

    /// <summary>
    /// <c>/profile rename &lt;name&gt; &lt;new-name&gt;</c>: the named profile's directory moved under the
    /// new name, everything in it along (<see cref="Profiles.Rename"/>). The checks in order: the
    /// profile must exist; <c>default</c> and the loaded one are refused (<see cref="Profiles.RenameRefusal"/>
    /// — the loaded one's directory is in use, switch first); the new name must be a valid one; and
    /// it must be free — the default always resolves, and names compare ignoring case, so
    /// <c>work</c> → <c>Work</c> is "already exists" too. No confirmation (nothing is lost), no
    /// switch, no reconnect: the loaded profile did not change.
    /// </summary>
    private void RenameProfile(string name, string newName)
    {
        string home = _settings.StorageDirectory;
        if (Profiles.Resolve(home, name) is not { } target)
        {
            _transcript.Error(ProfileMissingError(name));
            return;
        }

        if (Profiles.RenameRefusal(target, _settings.ProfileName) is { } refusal)
        {
            _transcript.Error(refusal);
            return;
        }

        if (!Profiles.IsValidName(newName))
        {
            _transcript.Error(ProfileNameError);
            return;
        }

        if (Profiles.Resolve(home, newName) is { } existing)
        {
            _transcript.Error(ProfileExistsError(existing));
            return;
        }

        try
        {
            Profiles.Rename(home, target, newName);
            _transcript.Notice(ProfileRenamedNotice(target, newName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _transcript.Error(ProfileRenameFailedError(ex.Message));
        }
    }

    /// <summary>
    /// <c>/profile reset [name]</c>: the loaded profile when <paramref name="name"/> is empty, else the
    /// named one — but <c>default</c> only while it is the loaded one (2026-09-22, the user's call:
    /// <see cref="Profiles.ResetRefusal"/>; another profile cannot wipe it). The prompt says the settings alone go back (the memories and the three prompt files stay, 2026-09-20); a typed <c>y</c> resets
    /// through the store (<see cref="AppSettings.ResetProfileAsync"/>: the pending save flushed first
    /// when it is the loaded one, then the defaults reloaded). The loaded profile then takes the
    /// switch's tail — rebind, a cleared conversation, a fresh screen, the reconnects — under the
    /// reset notice; another profile is disk only, its notice alone.
    /// </summary>
    private async Task ResetProfileAsync(string name, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        string target;
        if (name.Length == 0)
        {
            target = _settings.ProfileName;
        }
        else if (Profiles.Resolve(home, name) is { } resolved)
        {
            target = resolved;
        }
        else
        {
            _transcript.Error(ProfileMissingError(name));
            return;
        }

        if (Profiles.ResetRefusal(target, _settings.ProfileName) is { } refusal)
        {
            _transcript.Error(refusal);
            return;
        }

        if (!await ConfirmAsync(ResetProfilePrompt(target), cancellationToken).ConfigureAwait(false))
        {
            _transcript.Notice(KeptNotice);
            return;
        }

        try
        {
            await _settings.ResetProfileAsync(target).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _transcript.Error(ProfileResetFailedError(ex.Message));
            return;
        }

        bool loaded = Profiles.NameEquals(target, _settings.ProfileName);
        if (loaded)
        {
            await AfterProfileSwitchAsync(cancellationToken, notice: ProfileResetNotice(target, loaded: true)).ConfigureAwait(false);
        }
        else
        {
            _transcript.Notice(ProfileResetNotice(target, loaded: false));
        }
    }

    /// <summary>
    /// After the store loaded another profile (or reset the loaded one): rebind memory, persona, operating rules and voice directive, forget the
    /// conversation (a different persona and memory set is a different sidekick), start on a
    /// fresh screen the way <c>/clear</c> does (the window title names the loaded profile), announce
    /// the switch — <paramref name="notice"/> in place of the switch line when given (a reset), and
    /// <paramref name="preface"/> first, when <c>add</c> created the profile — printed before the wipe
    /// it would be lost — then connect LLM, TTS and voice from the new profile's settings, and name
    /// the timers that survived.
    /// </summary>
    private async Task AfterProfileSwitchAsync(CancellationToken cancellationToken, string? preface = null, string? notice = null)
    {
        ForgetSession();
        BindProfile();
        DiagnosticLog.Debug(AppSettings.Category, AppSettings.NotDefaultLogLine(SettingsDiff.NotDefault(_effective())));
        // The new profile's theme (2026-09-23) before the wipe below, so the fresh screen wears it.
        ThemeName.Apply(_effective());
        ApplyWindowTitle();
        _session.History.Clear();
        DropQueue();
        _lastTrace = null;
        _learnTrace = null;
        _session.Usage.ResetConversation();
        _pictureStrip.Clear();
        _log.Clear();
        ForgetReading();
        ForgetSplash();
        using (_pane.Batch())
        {
            _renderScreen(_pane);
            if (preface is not null)
            {
                _transcript.Notice(preface);
            }

            _transcript.Notice(notice ?? SettingsMenu.SwitchedNotice(_settings.ProfileName));
        }

        await ConnectLlmAsync(cancellationToken, quiet: true).ConfigureAwait(false);
        await ConnectSpeechAsync(cancellationToken, quiet: true).ConfigureAwait(false);
        await ConnectVoiceAsync(cancellationToken, quiet: true).ConfigureAwait(false);
        await ConnectMcpAsync(cancellationToken).ConfigureAwait(false);
        ReportTimers();
        // The welcome splash again under the fresh banner (later on 2026-09-19, the user's ask): the
        // new profile's own folder or the embedded set, the arrows walking it, the first sent line
        // wiping it — the startup's shape, after the connects for the same reason.
        ShowSplash();
    }

    /// <summary><c>/remember &lt;text&gt;</c>: straight into the store, no model involved. Off means nothing is saved.</summary>
    private void Remember(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            _transcript.Error(RememberUsageError);
            return;
        }

        if (!_effective().Memory)
        {
            _transcript.Notice(MemoryOffNotice);
            return;
        }

        var result = _memory.Add(args);
        switch (result.Outcome)
        {
            case MemoryAddOutcome.Added:
                _transcript.Notice(RememberedNotice(result.Text));
                break;
            case MemoryAddOutcome.Duplicate:
                _transcript.Notice(AlreadyRememberedNotice(result.Text));
                break;
            case MemoryAddOutcome.Full:
                _transcript.Error(MemoryFullError);
                break;
            case MemoryAddOutcome.Empty:
                _transcript.Error(RememberUsageError);
                break;
            default:
                _transcript.Error(MemoryFailedError);
                break;
        }

        DrainDiagnostics();
    }

    /// <summary>
    /// <c>/memory</c> (2026-09-22, the user's ask, twice): bare, the list pane; <c>forget</c>, the
    /// wipe the standalone <c>/forget</c> did, confirmation and all; <c>copy &lt;profile&gt;
    /// [overwrite]</c>, the copy the standalone <c>/memcopy</c> did, confirmation and all; <c>edit</c>
    /// (2026-09-23), <c>memory.json</c> in the editor; anything else <see cref="MemoryUsageError"/>. The one method both dispatches call — the idle line's
    /// and the mid-turn pane phase's — so every word behaves the same under a reply; the error goes
    /// through <see cref="_flow"/> for that reason, as <see cref="EmptyTrashAsync"/>'s does.
    /// </summary>
    private async Task HandleMemoryAsync(string args, CancellationToken cancellationToken)
    {
        switch (ParseMemoryArgs(args))
        {
            case { Kind: MemoryActionKind.List }:
                await _memoryMenu.ShowAsync(cancellationToken).ConfigureAwait(false);
                DrainDiagnostics();
                break;

            case { Kind: MemoryActionKind.Forget }:
                await ForgetAsync(cancellationToken).ConfigureAwait(false);
                break;

            case { Kind: MemoryActionKind.Copy } copy:
                await CopyMemoryAsync(copy.Profile, copy.Overwrite, cancellationToken).ConfigureAwait(false);
                break;

            case { Kind: MemoryActionKind.Edit }:
                // /profile edit's shape (2026-09-23): the file written first when there is none, so
                // the editor opens the right shape; never waits for the editor.
                try
                {
                    bool existed = _memory.EnsureFile();
                    _openFile(_memory.FilePath);
                    _flow.Notice(existed ? MemoryEditOpenedNotice : MemoryEditCreatedNotice);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _flow.Error(MemoryEditFailedError(ex.Message));
                }

                DrainDiagnostics();
                break;

            default:
                _flow.Error(MemoryUsageError);
                break;
        }
    }

    /// <summary>
    /// <c>/memory forget</c>: one typed confirmation on the input line (ESC or anything but <c>y</c>
    /// keeps), then the file is deleted. Works with memory off too — the switch governs use, not the
    /// file. A delete that fails is reported as such, never as done.
    /// </summary>
    private async Task ForgetAsync(CancellationToken cancellationToken)
    {
        int count = _memory.Count;
        if (count == 0)
        {
            _flow.Notice(NothingToForgetNotice);
            return;
        }

        if (!await ConfirmAsync(ForgetPrompt(count), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        // The wipe and its line on the turn task when the question was asked mid-turn.
        RunOrPost(() =>
        {
            try
            {
                _transcript.Notice(ForgotNotice(_memory.Clear()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _transcript.Error(ForgetFailedError(ex.Message));
            }

            DrainDiagnostics();
        });
    }

    /// <summary>
    /// <c>/loop</c> (2026-09-21, the user's ask): the message sent <c>count</c> times, or until
    /// something ends it, each pass a whole <see cref="RunMessageAsync"/> — the auto-compact, the
    /// reply, the interrupt follow-up — with the message echoed as a user row first (the typed line
    /// was the command, not the message; the spoken path's precedent). Ends early on a cancelled or
    /// withdrawn pass (ESC, Ctrl+C, the wake phrase — the whole loop, not the pass), on a failed one
    /// (<see cref="_lastTurnFailed"/>: a server that is down is not asked again and again) and on the
    /// app token. A withdrawn pass restores the <c>/loop</c> line itself (the history's last line), so
    /// the loop is one Enter from a re-run. The images go with the first pass alone. Messages queued
    /// mid-turn wait for the loop's end, as they wait for any reply. With a delay (2026-09-24,
    /// the user's ask) each pass after the first waits that long first — the gap after the last reply
    /// ends, not a period between starts — and ESC or Ctrl+C in the wait stops the loop as it would a
    /// reply. Returns true when the shell should exit.
    /// </summary>
    private async Task<bool> HandleLoopAsync(string args, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        if (!TryParseLoopArgs(args, out int? count, out TimeSpan? delay, out string message))
        {
            _transcript.Error(LoopUsageError);
            return false;
        }

        if (message.StartsWith('/'))
        {
            var (command, commandArgs) = ParseLine(message);
            string token = message.Split(' ', 2)[0];
            if (command == SlashCommand.Unknown)
            {
                _transcript.Error(UnknownCommandError(token));
                return false;
            }

            if (command != SlashCommand.None)
            {
                if (!LoopableCommands.Contains(command))
                {
                    _transcript.Error(LoopCommandNotLoopableError(token));
                    return false;
                }

                if (images.Count > 0)
                {
                    _transcript.Notice(ImagesIgnoredNotice(images.Count));
                }

                await RunLoopedCommandAsync(command, commandArgs, count, delay, cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        for (int n = 1; count is null || n <= count; n++)
        {
            if (n > 1 && delay is { } wait)
            {
                // The gap after the last reply, never after the final pass (2026-09-24): on the
                // clock, under the turn's keys, so ESC or Ctrl+C ends the loop here as mid-reply.
                _transcript.Notice(LoopWaitNotice(wait));
                if (await WaitUnderWatchAsync(ct => Task.Delay(wait, _time, ct), KeySource.IsTurnCancel, cancellationToken, pointer: true).ConfigureAwait(false)
                    || cancellationToken.IsCancellationRequested)
                {
                    _transcript.Notice(LoopStoppedNotice(n - 1));
                    return false;
                }
            }

            _transcript.Notice(LoopTurnNotice(n, count));
            _transcript.User(message);
            if (await RunMessageAsync(message, n == 1 ? images : [], cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (_lastTurnCancelled || _lastTurnFailed || _restoreDraft is not null || cancellationToken.IsCancellationRequested)
            {
                _transcript.Notice(LoopStoppedNotice(n));
                return false;
            }
        }

        _transcript.Notice(LoopDoneNotice(count!.Value));
        return false;
    }

    /// <summary>How one pass of a looped command went (2026-09-25): <see cref="Ok"/> goes on, the others stop the loop.</summary>
    private enum LoopPass
    {
        Ok,
        Cancelled,
        Failed,
    }

    /// <summary>
    /// <c>/loop</c> over one of <see cref="LoopableCommands"/> (2026-09-25, the user's ask: <c>/loop infinite 1s /imagine …</c>
    /// went to the model as a message): the command run straight, pass after pass, no model in between and no user row
    /// echoed — the command's own lines are the pass. <c>/imagine</c> under its own ESC watcher, its queue for the next message
    /// kept to the last pass's pictures (the base taken here, before the first); <c>/speak</c> waits for the reading's audio to
    /// end, under ESC, so the next pass does not cut it short. A cancelled or failed pass ends the loop, as a failed reply ends
    /// a message loop. Between passes the delay as ever, or — none named — <see cref="LoopCommandMinGap"/>, watched and
    /// unannounced, so an instant pass cannot spin past ESC.
    /// </summary>
    private async Task RunLoopedCommandAsync(SlashCommand command, string args, int? count, TimeSpan? delay, CancellationToken cancellationToken)
    {
        var loopBase = (_imagineNotes.Count, _imagineImages.Count);
        for (int n = 1; count is null || n <= count; n++)
        {
            if (n > 1)
            {
                var wait = delay ?? LoopCommandMinGap;
                if (delay is not null)
                {
                    _transcript.Notice(LoopWaitNotice(wait));
                }

                if (await WaitUnderWatchAsync(ct => Task.Delay(wait, _time, ct), KeySource.IsTurnCancel, cancellationToken, pointer: true).ConfigureAwait(false)
                    || cancellationToken.IsCancellationRequested)
                {
                    _transcript.Notice(LoopCommandStoppedNotice(n - 1));
                    return;
                }
            }

            _transcript.Notice(LoopTurnNotice(n, count));
            var pass = command == SlashCommand.Imagine
                ? await HandleImagineAsync(args, cancellationToken, loopBase).ConfigureAwait(false)
                : await SpeakPassAsync(args, cancellationToken).ConfigureAwait(false);
            if (pass != LoopPass.Ok || cancellationToken.IsCancellationRequested)
            {
                _transcript.Notice(LoopCommandStoppedNotice(n));
                return;
            }
        }

        _transcript.Notice(LoopCommandDoneNotice(count!.Value));
    }

    /// <summary>One looped <c>/speak</c> (2026-09-25): the reading started, then its audio waited out under ESC, which stops it and the loop.</summary>
    private async Task<LoopPass> SpeakPassAsync(string args, CancellationToken cancellationToken)
    {
        if (!HandleSpeak(args, cancellationToken))
        {
            return LoopPass.Failed;
        }

        if (_speech.Playing is not { } playing)
        {
            return LoopPass.Ok;
        }

        if (await WaitUnderWatchAsync(ct => playing.Completion.WaitAsync(ct), KeySource.IsTurnCancel, cancellationToken, pointer: true).ConfigureAwait(false))
        {
            // As ESC over a tail at the idle line: silenced, waited for, and the notice when audio was still owed.
            if (await _speech.StopAsync().ConfigureAwait(false))
            {
                _transcript.Notice(SpeechStoppedNotice);
            }

            _pane.RefreshHint();
            return LoopPass.Cancelled;
        }

        return LoopPass.Ok;
    }

    /// <summary>A <c>/botchat</c> turn is running (2026-09-24): its usage events stay out of the main conversation's tally. Set and cleared on the turn task.</summary>
    private bool _botTurnRunning;

    /// <summary><c>/botchat</c> is running (2026-09-24): a line typed under a reply is queued whatever <c>Queue messages</c> says (<c>QueueLine</c>). Read on the watcher task.</summary>
    private volatile bool _botChatRunning;

    /// <summary>The last turn's reply as shown (what <c>/copy</c> keeps), for <c>/botchat</c>'s shared transcript.</summary>
    private string _lastReply = "";

    /// <summary>
    /// <c>/botchat [profile …] [topic]</c> (2026-09-24, the user's ask; <see cref="BotChat"/> has the rules):
    /// the profiles talk to each other until ESC or Ctrl+C ends it — up <see cref="BotEscLadder"/> since 2026-09-25 (the
    /// user's ask): a press stops the speaking bot's voice, the next cuts the bot replying short and the chat goes on with
    /// the next one (<see cref="BotChat.CutShortNotice"/>, the words so far kept as its line), the next — before that next
    /// bot has shown or said anything — ends the chat. The input row stays the idle line's editor throughout. Every turn runs through <see cref="RunTurnAsync"/>
    /// on an assistant of its own over this profile's one client (<see cref="LlmSession.CreateAssistant(ConversationHistory)"/>:
    /// the user's call, every bot on the starting profile's server and model) — or, with <c>Botchat LLM mode</c> <c>multi</c>
    /// (later on 2026-09-25, the user's ask), each bot but the starter over its own profile's (<see cref="LinkBotsAsync"/>),
    /// a bot whose server does not answer sitting the chat out — strictly one after another,
    /// with a system prompt built afresh from the speaker's own <c>persona.md</c> and no tools; its reply
    /// speaks in the speaker's own voice. A message typed meanwhile is queued (<see cref="_botChatRunning"/>)
    /// and joins the chat before the next reply as the user's line — at once when it is sent while the last
    /// reply is still being heard (<see cref="WaitForBotSpeechAsync"/>). With speech on, the next speaker waits for
    /// the last one's audio to end, under ESC. Stops as <c>/loop</c> does — a cancelled, withdrawn or failed
    /// reply, or the app token. With <c>Session logging</c> on the chat is a session of its own (each reply a
    /// turn, the starter's view as its history, so <c>/sessions</c> brings it back as a chat with the starter);
    /// the main conversation is never touched. Returns true when the shell should exit.
    ///
    /// <para>Pictures (2026-09-25, the user's ask): with <c>Botchat images enabled</c> on and the ComfyUI tools offered
    /// (<see cref="ComfyOffered"/>), <c>Botchat image mode</c> says who draws. The bots, offered <c>generate_image</c> alone
    /// (<see cref="BotImageTool"/>), draw through the turn like the main chat's model; the app, after every reply, has the
    /// model write an image prompt from it (<see cref="WriteBotPictureAsync"/>) and draws it with <c>Botchat image workflow</c>.
    /// With <c>Botchat image async</c> off (later on 2026-09-25, the user's ask: the picture before the words) the reply is
    /// written unseen first (<see cref="CollectBotTurnAsync"/>), its picture made, and then the turn is replayed — the name,
    /// the picture, the reply shown and spoken; on, the reply streams as ever and its picture renders while the next bot
    /// answers, drawn when nothing streams (<see cref="ShowReadyBotPictures"/>). The pictures never reach the bots nor the session row.
    /// Under <c>autonomous</c> (later on 2026-09-25, the user's ask: the reply should be true) a reply that talks about a picture its
    /// bot did not draw (<see cref="BotChat.MentionsPicture"/>, <see cref="BotChat.PictureAttempt"/> — a failed call counts as not
    /// drawn) gets it from the app, the prompt written by <see cref="BotChat.PromisedPictureInstruction"/> — or nothing, when the model
    /// finds no picture promised. A call the bot writes out as text runs as a real one (<see cref="Assistant.TextToolCalls"/>).</para>
    ///
    /// <para>Resuming (2026-09-25, the user's ask: a stopped chat could only start over): however a chat ends, its cast, topic,
    /// lines, last speaker, reply count and session row are kept (<see cref="_lastBotChat"/>, this run only), and
    /// <c>/botchat --resume [line]</c> carries it on — the cast rebuilt afresh (<see cref="BuildBotCast"/>), the line joining as the
    /// user's. Only from the profile that started it.</para>
    /// </summary>
    private async Task<bool> HandleBotChatAsync(string args, CancellationToken cancellationToken)
    {
        string starter = _settings.ProfileName;
        var (resume, resumeLine) = BotChat.ParseResume(args);
        var saved = resume ? _lastBotChat : null;
        if (resume && saved is null)
        {
            _transcript.Error(BotChat.NothingToResumeError);
            return false;
        }

        if (saved is not null && !Profiles.NameEquals(saved.Starter, starter))
        {
            _transcript.Error(BotChat.ResumeOtherProfileError(saved.Starter));
            return false;
        }

        var (names, topic) = saved is not null ? (saved.Cast, saved.Topic) : BotChat.ParseArgs(args, Profiles.List(_settings.StorageDirectory), starter);
        var effective = _effective();
        var cast = BuildBotCast(names, starter, effective);
        if (cast.Count < 2)
        {
            _transcript.Error(BotChat.TooFewError);
            return false;
        }

        if (_session.Assistant is null)
        {
            _transcript.Error(NoAssistantError);
            return false;
        }

        // Multi (later on 2026-09-25): the bots' own LLMs, one per bot but the starter (null), aligned with the cast; read at
        // the start, so a resume re-reads the mode and reaches them afresh. Disposed with the chat.
        var links = new List<BotLink?>();
        if (BotChatLlmMode.Resolve(effective) == BotLlmMode.Multi)
        {
            var (linked, linkCancelled) = await LinkBotsAsync(cast, cancellationToken).ConfigureAwait(false);
            if (linkCancelled || linked is null)
            {
                _transcript.Notice(CancelledNotice);
                return false;
            }

            var kept = new List<BotParticipant>();
            for (int i = 0; i < cast.Count; i++)
            {
                var (link, problem) = linked[i];
                if (cast[i].Profile is not null && link is null)
                {
                    _transcript.Notice(BotChat.SkippedNotice(cast[i].Name, problem ?? ""));
                    continue;
                }

                if (link is not null)
                {
                    _transcript.Notice(BotChat.LinkNotice(cast[i].Name, link.Endpoint.BaseUrl, link.Endpoint.ModelId, ReasoningLevel.Name(link.Reasoning)));
                }

                kept.Add(cast[i]);
                links.Add(link);
            }

            cast = kept;
            if (cast.Count < 2)
            {
                links.ForEach(link => link?.Dispose());
                _transcript.Error(BotChat.TooFewError);
                return false;
            }
        }

        var castNames = cast.Select(bot => bot.Name).ToList();
        string opening = saved is not null ? BotChat.ResumeNotice(castNames, topic, saved.Replies) : BotChat.StartNotice(castNames, topic);
        _transcript.Notice(opening);
        DiagnosticLog.Info(AppCategory, opening);
        // A resumed chat (2026-09-25) carries on its own session row; a new one begins when there is none and logging is on.
        long? sessionId = saved?.SessionId ?? (effective.SessionLogging ? _sessions.Begin(BotChat.SessionTitle(castNames), _session.Endpoint?.ModelId ?? "") : null);
        var lines = saved is not null ? saved.Lines.ToList() : new List<BotChatLine>();
        int? last = null;
        if (saved?.LastSpeaker is { } lastName)
        {
            // By name: a cast rebuilt without the last speaker simply has no last speaker.
            int spoke = castNames.FindIndex(name => Profiles.NameEquals(name, lastName));
            last = spoke >= 0 ? spoke : null;
        }

        int replies = saved?.Replies ?? 0;
        if (resumeLine.Length > 0)
        {
            // The line after --resume joins the chat as the user's, as a line typed under a reply does.
            _transcript.User(resumeLine);
            lines.Add(new BotChatLine(BotChat.UserName, resumeLine, IsUser: true));
        }

        // The app's pictures still rendering (Botchat image async), oldest first; ending the chat cancels them.
        using var pictureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pictures = new List<(BotParticipant Bot, Task<ComfyGeneration?> Job)>();
        // Their pace with no voice to wait for (2026-09-25): one at a time, a second's rest after each.
        var pacer = new BotPicturePacer(_time);
        // ESC's ladder (2026-09-25): the voice, then the bot replying, then the chat.
        var ladder = new BotEscLadder();
        _botNoWorkflowTold = false;
        _botChatRunning = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // The lines typed under the last reply join the chat as the user's, in order; then the pictures made meanwhile.
                TakeInterjections(lines);
                ShowReadyBotPictures(pictures);

                // A bot the last line names answers next (2026-09-25); anyone but the last speaker otherwise.
                int next = last is null ? 0 : BotChat.NextSpeaker(cast.Count, last, _random, BotChat.Addressed(lines, castNames));
                var bot = cast[next];
                var others = castNames.Where(name => !Profiles.NameEquals(name, bot.Name)).ToList();
                // Read per turn: a /tts or Transcript markdown switch mid-chat holds from the next reply.
                effective = _effective();
                bool speaking = effective.TtsOutput && _speech.IsReady;
                bool markdown = MarkdownTurn(effective.TranscriptMarkdown, _pane.Enabled, speaking);
                var (prior, turnText) = BotChat.BuildView(bot.Name, lines, others, topic);
                // The others' pronouns from their first voices (2026-09-25); never the speaker's own.
                string pronouns = BotChat.PronounsLine(cast.Where(b => b != bot).Select(b => (b.Name, b.Gender)).ToList());
                // Pictures (2026-09-25): read per reply, so a switch mid-chat holds from the next one.
                var imageMode = BotChatImageMode.Resolve(effective);
                bool pictured = effective.BotChatImages && ComfyOffered(effective, _comfy);
                var imageTool = pictured && BotChatImageMode.Offers(imageMode) ? BotImageTool(effective) : null;
                var history = new ConversationHistory(BotChat.SystemPrompt(bot.Persona, bot.Name, others, topic, speaking, bot.VoiceDirective, markdown, pronouns, images: imageTool is not null));
                history.Replace(prior);
                if ((links.Count > 0 && links[next] is { } own ? _session.CreateAssistant(history, own) : _session.CreateAssistant(history)) is not { } assistant)
                {
                    _transcript.Error(NoAssistantError);
                    break;
                }

                if (imageTool is not null)
                {
                    assistant.Tools = [imageTool];
                    assistant.MaxToolIterations = BotImageToolIterations;
                    // A bot that writes generate_image(…) out as text (later on 2026-09-25, the user's report) has it run, unseen and unspoken.
                    assistant.TextToolCalls = true;
                }

                DiagnosticLog.Info(AppCategory, BotChat.TurnLogLine(replies + 1, bot.Name));
                int turnId = ladder.BeginBot();
                // Async off (later on 2026-09-25): the reply is written unseen, its picture made, then both shown — picture first.
                bool held = pictured && BotChatImageMode.Draws(imageMode) && !effective.BotChatImageAsync;
                IReadOnlyList<TurnEvent>? replay = null;
                ComfyGeneration? picture = null;
                if (held)
                {
                    var (collected, thinkingCancelled) = await UnderWatchAsync(BotChat.ThinkingSpinner(bot.Name), token => CollectBotTurnAsync(assistant, turnText, token), cancellationToken,
                        softCancel: () => ladder.Press(turnId, shown: false, voiceAudible: false, responding: true) == BotPress.StopVoice).ConfigureAwait(false);
                    if (thinkingCancelled || collected is null)
                    {
                        // ESC over the unseen reply is the ladder's, as over a streaming one (2026-09-25): this bot cut
                        // short and the next one asked, or — the press after a skip, a command that cancels — the chat's end.
                        if (!cancellationToken.IsCancellationRequested && ladder.Skipped(turnId) && !ladder.EndRequested)
                        {
                            _transcript.Notice(BotChat.CutShortNotice(bot.Name));
                            last = next;
                            continue;
                        }

                        if (!cancellationToken.IsCancellationRequested)
                        {
                            _transcript.Notice(CancelledNotice);
                        }

                        break;
                    }

                    replay = collected;
                    string written = BotChat.ReplyText(collected);
                    if (written.Length > 0 && !collected.Any(e => e is TurnEvent.Notice { IsError: true }))
                    {
                        picture = await PaintBotPictureAsync(assistant, bot, written, topic, effective, pictureCts.Token).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }

                var outcome = await RunTurnAsync(assistant, turnText, [], cancellationToken, bot, replay, picture, ladder, turnId).ConfigureAwait(false);
                await EndTurnAsync(closePane: outcome is not (TurnOutcome.Continue or TurnOutcome.Withdrawn), cancellationToken).ConfigureAwait(false);
                if (outcome == TurnOutcome.Exit)
                {
                    return true;
                }

                string reply = _lastReply.Trim();
                // Cut short by the ladder (2026-09-25): the chat goes on. Any other cancel — the press that ends the chat, a
                // command that cancels (/exit, /clear), the wake phrase — or a failure stops it, as before.
                bool skipped = ladder.Skipped(turnId) && !ladder.EndRequested;
                bool stopped = ladder.EndRequested || _lastTurnFailed || outcome is TurnOutcome.Exit or TurnOutcome.Interrupted || (_lastTurnCancelled && !skipped);
                if (outcome != TurnOutcome.Withdrawn || skipped)
                {
                    // Even an empty reply passes the turn on: the same bot is never asked twice running.
                    last = next;
                }

                if ((outcome != TurnOutcome.Withdrawn || skipped) && reply.Length > 0)
                {
                    lines.Add(new BotChatLine(bot.Name, reply));
                    replies++;
                    if (sessionId is { } id)
                    {
                        _sessions.AppendTurn(id, turnText, BotChat.Signed(lines[^1]), 0, [], [], _lastTurnFailed ? 1 : 0, 0, 0, _lastTurnCancelled);
                        _sessions.SaveHistory(id, SessionHistory.ToJson(BotChat.StoredHistory(starter, lines, castNames, topic)));
                    }
                }

                if (stopped || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (skipped)
                {
                    // A reply cut short gets no picture and has no voice left to wait for: the next bot answers.
                    continue;
                }

                if (pictured && BotChatImageMode.Draws(imageMode) && !held && reply.Length > 0)
                {
                    await DrawBotPictureAsync(assistant, bot, reply, topic, effective, pictures, pacer, pictureCts.Token).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                // Autonomous (2026-09-25, the user's ask: a bot said it drew something and had not): a reply that talks about
                // a picture its bot never called generate_image for gets that picture from the app, so the reply is true. A call
                // that failed (no prompt, later the same day) drew nothing and is intent enough: the word sieve is skipped.
                var attempt = BotChat.PictureAttempt(history.Messages);
                if (imageMode == BotImageMode.Autonomous && imageTool is not null && reply.Length > 0
                    && !attempt.Drew && (attempt.Tried || BotChat.MentionsPicture(reply)))
                {
                    if (effective.BotChatImageAsync)
                    {
                        await DrawBotPictureAsync(assistant, bot, reply, topic, effective, pictures, pacer, pictureCts.Token, promised: true).ConfigureAwait(false);
                    }
                    else if (await PaintBotPictureAsync(assistant, bot, reply, topic, effective, pictureCts.Token, promised: true).ConfigureAwait(false) is { } promised)
                    {
                        // The reply has streamed already: its picture goes under it.
                        ShowBotPicture(bot, promised, late: false);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                // Speech on: the next voice waits for this one's audio to end — under ESC, which stops the chat,
                // and under the line hook, so a line sent meanwhile joins the chat at once.
                if (_speech.Playing is { } playing)
                {
                    if (await WaitForBotSpeechAsync(playing, lines, pictures, ladder, turnId, cancellationToken).ConfigureAwait(false))
                    {
                        await _speech.StopAsync().ConfigureAwait(false);
                        break;
                    }
                }
                else if (!speaking && BotPause(effective) is { } pause
                    && await WaitForBotSpeechAsync(null, lines, pictures, ladder, turnId, cancellationToken, pause).ConfigureAwait(false))
                {
                    // Speech off (2026-09-26, the user's ask): the same watched wait, a pause in place of a voice.
                    break;
                }
            }
        }
        finally
        {
            _botChatRunning = false;
            // Kept for /botchat --resume (2026-09-25), however the chat ended; one with nothing said has nothing to carry on.
            if (lines.Count > 0)
            {
                // The last speaker is the last bot line's: a reply cancelled before a word never joined the lines, so its bot has not spoken.
                _lastBotChat = new BotChatState(starter, castNames, topic, lines.ToList(), lines.LastOrDefault(line => !line.IsUser)?.Speaker, replies, sessionId);
            }

            // The pictures still rendering are dropped with the chat; those already made are drawn below.
            await pictureCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pictures.Select(p => p.Job)).ConfigureAwait(false);
            // The bots' own LLMs (multi) go with the chat, after the last picture prompt that could use one.
            links.ForEach(link => link?.Dispose());
        }

        ShowReadyBotPictures(pictures);
        _transcript.Notice(BotChat.StoppedNotice(replies));
        return false;
    }

    /// <summary>The last <c>/botchat</c> of this run, kept when it stopped, for <see cref="BotChat.ResumeSwitch"/> (2026-09-25); null before the first.</summary>
    private BotChatState? _lastBotChat;

    /// <summary>
    /// The <c>/botchat</c> rest after a reply with no voice (<see cref="AppSettingsData.BotChatNonTtsDelaySeconds"/>,
    /// 2026-09-26), a hand-edited value clamped to its range; null at 0, no pause. Pure.
    /// </summary>
    internal static TimeSpan? BotPause(AppSettingsData effective)
    {
        int seconds = Math.Clamp(effective.BotChatNonTtsDelaySeconds, AppSettingsData.MinBotChatNonTtsDelaySeconds, AppSettingsData.MaxBotChatNonTtsDelaySeconds);
        return seconds == 0 ? null : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The bots for <paramref name="names"/>, in order (pulled out of <see cref="HandleBotChatAsync"/> on 2026-09-25 for
    /// <see cref="BotChat.ResumeSwitch"/>, which rebuilds a stopped chat's cast): each read afresh, so an edit since shows. A profile
    /// that cannot be read sits the chat out with <see cref="BotChat.SkippedNotice"/>.
    /// </summary>
    private List<BotParticipant> BuildBotCast(IReadOnlyList<string> names, string starter, AppSettingsData effective)
    {
        string home = _settings.StorageDirectory;
        var cast = new List<BotParticipant>();
        foreach (var name in names)
        {
            if (Profiles.NameEquals(name, starter))
            {
                // The starter is the loaded profile: its files as bound, its voice as the session holds it.
                cast.Add(new BotParticipant(name, _persona.Read(), _vocalia.Read(), _speech.VoiceSpec, _speech.Speed, cast.Count, BotChat.GenderOf(effective.TtsVoice)));
                continue;
            }

            string directory = Profiles.Directory(home, name);
            AppSettingsData data;
            try
            {
                data = Profiles.ReadProfileFile(Profiles.ProfileFile(home, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                _transcript.Notice(BotChat.SkippedNotice(name, ex.Message));
                continue;
            }

            // The persona, the spoken-reply directive and the voice are read; the LLM settings ride along in the profile,
            // read only under Botchat LLM mode multi (later on 2026-09-25, LinkBotsAsync).
            string voice = VoiceMix.Spec(data.TtsVoice, data.TtsVoice2, data.TtsVoiceMix);
            cast.Add(new BotParticipant(name, new PersonaFile(directory).Read(), new VocaliaFile(directory).Read(), voice, data.TtsSpeed, cast.Count, BotChat.GenderOf(data.TtsVoice), data));
        }

        return cast;
    }

    /// <summary>
    /// The bots' own LLMs (<c>Botchat LLM mode</c> <c>multi</c>, later on 2026-09-25, the user's ask): every bot with a
    /// <see cref="BotParticipant.Profile"/> (all but the starter) reached through <see cref="LlmSession.LinkAsync"/>, together,
    /// under a spinner ESC cancels; one result per bot, in cast order — the starter's empty. Cancelled, the links made are disposed.
    /// </summary>
    private async Task<((BotLink? Link, string? Problem)[]? Results, bool Cancelled)> LinkBotsAsync(List<BotParticipant> cast, CancellationToken cancellationToken)
    {
        Task<(BotLink? Link, string? Problem)>[] jobs = [];
        var (results, cancelled) = await UnderWatchAsync(BotChat.LinkingSpinner, token =>
        {
            jobs = cast.Select(bot => bot.Profile is { } profile ? _session.LinkAsync(profile, token) : Task.FromResult<(BotLink?, string?)>((null, null))).ToArray();
            return Task.WhenAll(jobs);
        }, cancellationToken).ConfigureAwait(false);
        if (cancelled)
        {
            foreach (var job in jobs.Where(job => job.IsCompletedSuccessfully))
            {
                job.Result.Link?.Dispose();
            }
        }

        return (results, cancelled);
    }

    /// <summary>The model's round trips a bot's turn may take while it is offered <c>generate_image</c> (2026-09-25): a picture or two, then its words.</summary>
    private const int BotImageToolIterations = 3;

    /// <summary>Whether <see cref="BotChat.NoWorkflowNotice"/> was shown this chat: once is enough.</summary>
    private bool _botNoWorkflowTold;

    /// <summary>
    /// The one tool a bot is offered (2026-09-25, <c>Botchat image mode</c> <c>autonomous</c>): the screen's
    /// <c>generate_image</c>, the one the main chat's model gets; null when it is switched off by name on <c>/tools</c>.
    /// </summary>
    private AIFunction? BotImageTool(AppSettingsData effective) =>
        Without(_comfyTools, ToolsText.DisabledSet(effective.ToolsDisabled)).FirstOrDefault(tool => string.Equals(tool.Name, GenerateImageTool.ToolName, StringComparison.Ordinal));

    /// <summary>
    /// The app's picture of one <c>/botchat</c> reply with <c>Botchat image async</c> on (2026-09-25): the prompt
    /// (<see cref="WriteBotPictureAsync"/>), then the generation left running in <paramref name="pictures"/>, drawn when
    /// nothing streams (<see cref="ShowReadyBotPictures"/>). With no voice to pace the chat (2026-09-25, the user's ask:
    /// <see cref="BotPicturePacer.Applies"/>, read at the send) the generation goes through <paramref name="pacer"/>: after
    /// the one before it, and a second after that one was made.
    /// </summary>
    private async Task DrawBotPictureAsync(Assistant assistant, BotParticipant bot, string reply, string topic, AppSettingsData effective, List<(BotParticipant Bot, Task<ComfyGeneration?> Job)> pictures, BotPicturePacer pacer, CancellationToken pictureToken, bool promised = false)
    {
        if (await WriteBotPictureAsync(assistant, bot, reply, topic, effective, pictureToken, promised).ConfigureAwait(false) is { } job)
        {
            bool paced = BotPicturePacer.Applies(_effective(), _speech.IsReady);
            pictures.Add((bot, GenerateBotPictureAsync(job, pictureToken, paced ? pacer : null)));
        }
    }

    /// <summary>
    /// The app's picture of one held <c>/botchat</c> reply, <c>Botchat image async</c> off (later on 2026-09-25): the prompt
    /// (<see cref="WriteBotPictureAsync"/>), then the generation under a spinner, not drawn — <see cref="RunTurnAsync"/> draws
    /// it under the speaker's name, above the reply. Null when there is no picture; ESC skips it alone, the reply still shows.
    /// </summary>
    private async Task<ComfyGeneration?> PaintBotPictureAsync(Assistant assistant, BotParticipant bot, string reply, string topic, AppSettingsData effective, CancellationToken pictureToken, bool promised = false)
    {
        if (await WriteBotPictureAsync(assistant, bot, reply, topic, effective, pictureToken, promised).ConfigureAwait(false) is not { } job)
        {
            return null;
        }

        var (generation, skipped) = await UnderWatchAsync(ComfyText.GeneratingLabel, token => GenerateBotPictureAsync(job, token), pictureToken).ConfigureAwait(false);
        if (skipped || generation is null)
        {
            _transcript.Notice(ComfyText.Cancelled);
            return null;
        }

        return generation;
    }

    /// <summary>
    /// The request for the app's picture of one <c>/botchat</c> reply (2026-09-25): the workflow (<see cref="BotChat.ImageWorkflow"/>;
    /// none → a notice, once), then the image prompt the model writes from the reply (<see cref="BotChat.ImagePromptInstruction"/>,
    /// one side request under a spinner — always before the next turn, so the server is never asked two things at once). Null,
    /// with its notice, when there is no workflow, no prompt came back, or ESC skipped it; the chat goes on.
    /// <paramref name="promised"/> (2026-09-25, <c>autonomous</c>: a picture the bot talked about but did not draw) asks with
    /// <see cref="BotChat.PromisedPictureInstruction"/> instead, and its <see cref="BotChat.NoPictureAnswer"/> is no picture, quietly.
    /// </summary>
    private async Task<ComfyRequest?> WriteBotPictureAsync(Assistant assistant, BotParticipant bot, string reply, string topic, AppSettingsData effective, CancellationToken pictureToken, bool promised = false)
    {
        if (BotChat.ImageWorkflow(_comfy.OfferedWorkflows(), effective.BotChatImageWorkflow) is not { } workflow)
        {
            if (!_botNoWorkflowTold)
            {
                _botNoWorkflowTold = true;
                _transcript.Notice(BotChat.NoWorkflowNotice);
            }

            return null;
        }

        var request = new List<ChatMessage>
        {
            new(ChatRole.System, promised ? BotChat.PromisedPictureInstruction(workflow) : BotChat.ImagePromptInstruction(workflow)),
            new(ChatRole.User, BotChat.ImagePromptRequest(bot.Name, reply, topic)),
        };
        var (written, cancelled) = await UnderWatchAsync(BotChat.PromptSpinner(bot.Name), async token =>
        {
            try
            {
                return (await assistant.RequestAsync(request, [], ReasoningEffort.None, token).ConfigureAwait(false)).Text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DiagnosticLog.Warn(AppCategory, "The botchat image prompt failed: " + Assistant.Explain(ex));
                return null;
            }
        }, pictureToken).ConfigureAwait(false);
        if (cancelled)
        {
            _transcript.Notice(ComfyText.Cancelled);
            return null;
        }

        string prompt = BotChat.CleanImagePrompt(written);
        if (promised)
        {
            if (BotChat.IsNoPicture(prompt))
            {
                // The picture word was only a word: the reply promised nothing, so nothing is owed.
                return null;
            }

            DiagnosticLog.Info(AppCategory, BotChat.PromisedPictureLogLine(bot.Name, workflow.Name, prompt));
            return new ComfyRequest(prompt, Workflow: workflow.Name);
        }

        if (prompt.Length == 0)
        {
            _transcript.Notice(BotChat.NoPromptNotice);
            return null;
        }

        DiagnosticLog.Info(AppCategory, BotChat.ImagePromptLogLine(bot.Name, workflow.Name, prompt));
        return new ComfyRequest(prompt, Workflow: workflow.Name);
    }

    /// <summary>
    /// A bot's turn run unseen (later on 2026-09-25, <c>Botchat image async</c> off): every event the assistant yields, kept for
    /// <see cref="RunTurnAsync"/> to replay once the picture is made. An exception is kept as the error notice a streamed turn
    /// would show; a cancel propagates.
    /// </summary>
    private static async Task<IReadOnlyList<TurnEvent>> CollectBotTurnAsync(Assistant assistant, string text, CancellationToken cancellationToken)
    {
        var events = new List<TurnEvent>();
        try
        {
            await foreach (var evt in assistant.RunTurnAsync(text, [], cancellationToken).ConfigureAwait(false))
            {
                events.Add(evt);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            events.Add(new TurnEvent.Notice(TurnFailedPrefix + Assistant.Explain(ex), IsError: true));
        }

        return events;
    }

    /// <summary>A held turn's events as <see cref="RunTurnAsync"/> reads a live one: in order, each after a yield, so the keys and the acts keep their turns.</summary>
    private static async IAsyncEnumerable<TurnEvent> ReplayAsync(IReadOnlyList<TurnEvent> events, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var evt in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return evt;
        }
    }

    /// <summary>
    /// One app picture's generation; null when it was cancelled (the chat ended, or ESC). Every other failure is an <c>Error:</c>
    /// text, never a throw. <paramref name="pacer"/> (2026-09-25), when given, holds the send for the picture before it and its rest.
    /// </summary>
    private async Task<ComfyGeneration?> GenerateBotPictureAsync(ComfyRequest request, CancellationToken cancellationToken, BotPicturePacer? pacer = null)
    {
        try
        {
            return pacer is null
                ? await _comfy.GenerateAsync(request, cancellationToken).ConfigureAwait(false)
                : await pacer.RunAsync(token => _comfy.GenerateAsync(request, token), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // The engine answers every failure in text; anything else still must not escape a picture left running.
            return new ComfyGeneration("Error: " + ex.Message, []);
        }
    }

    /// <summary>
    /// <paramref name="work"/> under a spinner and ESC, <see cref="HandleImagineAsync"/>'s shape (2026-09-25, for <c>/botchat</c>'s
    /// pictures): the watcher's ESC or Ctrl+C cancels the work alone — unless <paramref name="softCancel"/> spends it (the
    /// held reply's press asks the chat's <see cref="BotEscLadder"/>). <c>Cancelled</c> is true when it did, or
    /// <paramref name="cancellationToken"/> ended; the result is then default. The row is the live editor, and a line sent
    /// there goes through the mid-turn policy as under a reply: an interjection queued for the chat, a pane here, an act
    /// at the wait's end (<see cref="EndTurnAsync"/>).
    /// </summary>
    private async Task<(T? Result, bool Cancelled)> UnderWatchAsync<T>(string label, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken, Func<bool>? softCancel = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        // A line typed under the spinner is the chat's, as under a reply (2026-09-25): an interjection queued, a command run
        // by the mid-turn policy — its pane here, its act at the wait's end.
        _turnRunning = true;
        _paneClose?.Dispose();
        _paneClose = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var paneToken = _paneClose.Token;
        _queuedClicks.Reset();
        var watcher = _keys.WatchAsync(cts, stop.Token, null, null, _pane.Enabled ? line => OnMidTurnLineAsync(line, cts, paneToken) : null, softCancel, spend: e => { _queuedClicks.Reset(); return ScrollInput(e); }, onClick: _pane.Enabled ? HintClickLine : null, editor: LiveEditor);
        try
        {
            var result = await _transcript.WithSpinnerAsync(label, () => work(cts.Token)).ConfigureAwait(false);
            return cts.IsCancellationRequested ? (default, true) : (result, false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return (default, true);
        }
        finally
        {
            stop.Cancel();
            await watcher.ConfigureAwait(false);
            await EndTurnAsync(closePane: cts.IsCancellationRequested, cancellationToken).ConfigureAwait(false);
            DrainDiagnostics();
        }
    }

    /// <summary>The app's pictures that are done, drawn oldest first (<c>Botchat image async</c>, 2026-09-25); one still rendering holds back those after it, so they keep their order.</summary>
    private void ShowReadyBotPictures(List<(BotParticipant Bot, Task<ComfyGeneration?> Job)> pictures)
    {
        while (pictures.Count > 0 && pictures[0].Job.IsCompleted)
        {
            var (bot, job) = pictures[0];
            pictures.RemoveAt(0);
            if (job.Result is { } generation)
            {
                ShowBotPicture(bot, generation, late: true);
            }
        }
    }

    /// <summary>
    /// One app picture in the transcript (2026-09-25), <see cref="HandleImagineAsync"/>'s tail: a failure as an error, else the
    /// result lines, the strip, the picture. <paramref name="late"/> (drawn after later lines, <c>Botchat image async</c>) heads it
    /// with <see cref="BotChat.PictureNotice"/>, whose reply it pictures. Drawn at <c>Image thumbnail size</c> (later on
    /// 2026-09-25, the user's report: <see cref="ShowPictures"/>, <c>/imagine</c>'s drawer, filled the window whatever the
    /// setting said), as a bot's own <c>generate_image</c> result is; <c>fullsize</c> is the window's box.
    /// </summary>
    private void ShowBotPicture(BotParticipant bot, ComfyGeneration generation, bool late)
    {
        if (!generation.Ok)
        {
            _transcript.Error(generation.Text);
            return;
        }

        if (late)
        {
            _transcript.Notice(BotChat.PictureNotice(bot.Name));
        }

        ComfyLines(generation.Text, line => _transcript.Notice(ComfyText.Glyph + line));
        // The strip first: a fullsize box then leaves its rows.
        AddToPictureStrip(generation.Images);
        var (tiles, ids) = ReadThumbnails(generation.Images, ThumbnailSize.Resolve(_effective(), WindowBox()), sandbox: true);
        _transcript.Images(tiles, ids);
    }

    /// <summary>
    /// The wait for a bot's audio to end (later on 2026-09-24, the user's report: lines typed while a bot spoke
    /// reached the chat only after ESC, and then the main conversation). With speech on this wait is most of a
    /// turn, and <see cref="WaitUnderWatchAsync"/>'s watcher has no line hook, so an Enter there stayed type-ahead
    /// until the idle line. Here the keys are watched as under a reply — <see cref="OnMidTurnLineAsync"/>, the
    /// panes, the pointer — and each line queued meanwhile is taken at once (<c>QueueLine</c> wakes the act
    /// signal while <see cref="_botChatRunning"/>): echoed and added to the chat, the bot speaking on to its end
    /// (the user's call: let it finish, then the next bot answers).
    ///
    /// <para>ESC or Ctrl+C (2026-09-25, the user's ask) goes up the chat's <see cref="BotEscLadder"/>: the first press skips
    /// this bot's voice alone — the speech stops, <see cref="SpeechStoppedNotice"/> under it, and the chat goes on to the
    /// next bot, spoken as ever. A second press in the same wait, or at the next bot before it has shown or said anything,
    /// ends the chat. True when the wait was cancelled — that second press, or a command that cancels, <c>/clear</c> and
    /// the like; the caller stops the speech and the chat.</para>
    ///
    /// <para>With no voice (2026-09-26, the user's ask: <see cref="AppSettingsData.BotChatNonTtsDelaySeconds"/>),
    /// <paramref name="playing"/> is null and the wait is <paramref name="pause"/> on the screen's clock, watched the same
    /// way; ESC there finds no voice to stop, so the ladder ends the chat.</para>
    /// </summary>
    private async Task<bool> WaitForBotSpeechAsync(SpeechOutput? playing, List<BotChatLine> lines, List<(BotParticipant Bot, Task<ComfyGeneration?> Job)> pictures, BotEscLadder ladder, int turnId, CancellationToken cancellationToken, TimeSpan pause = default)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task done = playing?.Completion ?? Task.Delay(pause, _time, waitCts.Token);
        using var stop = new CancellationTokenSource();
        _turnRunning = true;
        _paneClose?.Dispose();
        _paneClose = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var paneToken = _paneClose.Token;
        _queuedClicks.Reset();
        // Written on the watcher task, read once it is joined (the finally).
        bool skipped = false;
        bool SkipSpeech()
        {
            // The ladder's (2026-09-25): the reply is written, so a press is the voice or the chat's end.
            if (ladder.Press(turnId, shown: true, voiceAudible: playing is not null && !skipped && !playing.Completion.IsCompleted, responding: false) != BotPress.StopVoice)
            {
                return false;
            }

            skipped = true;
            _speech.Stop();
            return true;
        }

        var watcher = _keys.WatchAsync(waitCts, stop.Token, null, null,
            onLine: _pane.Enabled ? line => OnMidTurnLineAsync(line, waitCts, paneToken) : null,
            softCancel: SkipSpeech,
            spend: e => { _queuedClicks.Reset(); return ScrollInput(e); },
            onClick: _pane.Enabled ? HintClickLine : null,
            editor: LiveEditor);
        var cancelled = Task.Delay(Timeout.Infinite, waitCts.Token);
        try
        {
            while (!done.IsCompleted && !waitCts.IsCancellationRequested)
            {
                var signal = Volatile.Read(ref _actSignal).Task;
                // A picture still rendering (Botchat image async, 2026-09-25) is drawn the moment it is done, under the voice.
                Task picture = pictures.Count > 0 ? pictures[0].Job : cancelled;
                await Task.WhenAny(done, signal, cancelled, picture).ConfigureAwait(false);
                await DrainActsAsync().ConfigureAwait(false);
                TakeInterjections(lines);
                ShowReadyBotPictures(pictures);
            }
        }
        finally
        {
            stop.Cancel();
            await watcher.ConfigureAwait(false);
            await EndTurnAsync(closePane: waitCts.IsCancellationRequested, cancellationToken).ConfigureAwait(false);
        }

        bool ended = waitCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
        if (skipped && !ended)
        {
            // The device silent before the next bot speaks; the line says why this voice ended early.
            await _speech.StopAsync().ConfigureAwait(false);
            _transcript.Notice(SpeechStoppedNotice);
        }

        TakeInterjections(lines);
        DrainDiagnostics();
        return ended;
    }

    /// <summary>The lines queued under a <c>/botchat</c> reply or its speech: each echoed as the user's row and added to the chat, in order.</summary>
    private void TakeInterjections(List<BotChatLine> lines)
    {
        while (!_queue.Held && _queue.TryDequeue(out var queued))
        {
            _transcript.User(queued.Label);
            lines.Add(new BotChatLine(BotChat.UserName, queued.Label, IsUser: true));
        }
    }

    /// <summary>A <c>/botchat</c> speaker's name colour: the palette's bright hues in turn, read at draw time so a theme change follows.</summary>
    private static Spectre.Console.Color SpeakerColor(int index) => (index % 6) switch
    {
        0 => Theme.Primary,
        1 => Theme.Secondary,
        2 => Theme.Tertiary,
        3 => Theme.Highlight,
        4 => Theme.Warm,
        _ => Theme.Tint,
    };

    /// <summary>
    /// <c>/botchat</c>'s argument list (2026-09-24): after the names already typed, every other profile but the
    /// loaded one (it always joins); nothing once a word that is no profile starts the topic. Pure.
    /// </summary>
    public static IReadOnlyList<CompletionItem> BotChatChoices(string argText, ArgumentSources sources)
    {
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(sources);
        var profiles = sources.Profiles();
        var words = argText.Split([' ', '\t'], StringSplitOptions.None);
        var typed = words[..^1].Where(word => word.Length > 0).ToList();
        if (typed.Any(word => !profiles.Any(name => Profiles.NameEquals(name, word))))
        {
            return [];
        }

        string prefix = argText[..(argText.Length - words[^1].Length)];
        var offered = profiles
            .Where(name => !Profiles.NameEquals(name, sources.LoadedProfile) && !typed.Any(word => Profiles.NameEquals(word, name)))
            .Select(name => new CompletionItem(prefix + name, BotChat.ProfileNote))
            .ToList();
        if (typed.Count == 0)
        {
            // Only as the first word (2026-09-25): after a name it would be the topic's.
            offered.Add(new CompletionItem(BotChat.ResumeSwitch, BotChat.ResumeNote));
        }

        return MentionCompleter.Matches(offered, argText);
    }

    /// <summary>
    /// A message for the model, typed or spoken. Returns true when the shell should exit.
    ///
    /// <para>A loop, not a call: a spoken reply cut short by the wake phrase is followed by a
    /// listen for the request, and the request by another turn, which may be interrupted in its
    /// turn. Looping keeps a long exchange off the stack. The interruption is a signal, not a
    /// request: nothing captured while the speaker played is transcribed; after a short settle
    /// the microphone opens for what is said next, with a short no-speech window. Two
    /// interruptions in a row that hear nothing switch interrupting off for the session.</para>
    /// </summary>
    private async Task<bool> RunMessageAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        if (_session.Assistant is not { } assistant)
        {
            _transcript.Error(NoAssistantError);
            return false;
        }

        // Once per message, before its turn: the last reply's context at or past the LLM compact
        // at share of a known window compacts first. A failed compact is reported and the turn
        // still runs; a compact leaves the context in use zeroed, so it cannot fire twice in a row.
        int share = _effective().LlmAutoCompactPercent;
        if (ConversationCompactor.ShouldAutoCompact(_session.Usage.LastRequest, _session.ContextLength, share))
        {
            int percent = UsageText.Percent(_session.Usage.LastRequest.Total, _session.ContextLength) ?? share;
            await CompactAsync(focus: null, autoPercent: percent, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return true;
            }
        }

        while (true)
        {
            var outcome = await RunTurnAsync(assistant, text, images, cancellationToken).ConfigureAwait(false);
            // A pane opened mid-turn is closed when the keys are needed now (the listen below, the
            // exit) and left to the user otherwise; then the acts and the owed reconnects.
            await EndTurnAsync(closePane: outcome is not (TurnOutcome.Continue or TurnOutcome.Withdrawn), cancellationToken).ConfigureAwait(false);
            if (_lastTurnCancelled)
            {
                // What a cancelled reply does to the queue (2026-09-18): the setting's word, here
                // ahead of the branches so an interruption's follow-up listen sees it applied.
                ApplyQueueCancelMode();
            }
            else if (outcome == TurnOutcome.Continue)
            {
                // The one release of a hold: a reply that ended on its own, whoever sent its message.
                _queue.Held = false;
            }

            if (outcome == TurnOutcome.Exit)
            {
                return true;
            }

            if (outcome == TurnOutcome.Withdrawn)
            {
                // ESC before the model's first event: the line comes back for the next read, as
                // the history recalls it (its tokens intact; a spoken request was remembered too).
                // A drained message went through the same Enter arm, so it comes back the same way,
                // and the idle loop reads that draft before it drains the next (2026-09-18).
                _restoreDraft = _sentDraft ?? text;
                // Back on the row at once (2026-09-25), ahead of whatever was typed under the reply.
                _input.Chat.Load(_restoreDraft, prependToCurrent: true);
                return false;
            }

            if (outcome == TurnOutcome.Continue)
            {
                MaybeLearn(focus: null, forced: false, cancellationToken);
                return false;
            }

            var (exit, request) = await InterruptFollowUpAsync(cancellationToken).ConfigureAwait(false);
            if (exit)
            {
                return true;
            }

            if (request is null)
            {
                return false;
            }

            text = request;
            images = [];
        }
    }

    /// <summary>
    /// After the wake phrase cut a spoken reply short — mid-stream, or in the tail under the
    /// input line: the settle, then the listen for the request. <c>Exit</c> when the app token
    /// ended it; <c>Text</c> the request to run, or null when there is nothing to run (a key
    /// discarded the listen, it failed, or it heard nothing — the tracker counts the silences
    /// and switches interrupting off at the second in a row).
    /// </summary>
    private async Task<(bool Exit, string? Text)> InterruptFollowUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InterruptSettle, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (true, null);
        }

        var listen = await ListenForRequestAsync(InterruptedLabel(_voice.PushToTalkName), seed: null, requestSpoken: false, stripWakeWord: true, _voice.InterruptOptions, cancellationToken).ConfigureAwait(false);
        if (listen.Exit)
        {
            return (true, null);
        }

        if (listen.Text is null)
        {
            // Discarded (a human at the keyboard) or failed (already reported): either way, not silence.
            _interrupts.Note(hadRequest: true);
            return (false, null);
        }

        if (listen.Text.Length == 0)
        {
            if (_interrupts.Note(hadRequest: false))
            {
                _transcript.Notice(InterruptTracker.SilentHint(_interrupts.SilentInARow));
                _transcript.Warning(InterruptTracker.DisabledWarning);
                DiagnosticLog.Info(VoiceSession.Category, InterruptDisabledLogLine);
                _voice.MarkInterruptUnavailable(InterruptDisabledReason);
            }
            else
            {
                _transcript.Notice(InterruptTracker.SilentHint(_interrupts.SilentInARow));
            }

            return (false, null);
        }

        _interrupts.Note(hadRequest: true);
        return (false, listen.Text);
    }

    /// <summary>
    /// The wake phrase heard over the tail under the input line: the speaker is silenced, then —
    /// on an empty line, with audio still owed, as in <see cref="TurnEndNotice"/> — it is an
    /// interruption: <c>(interrupted)</c>, the follow-up listen and the request's turn. With a
    /// draft on the line the phrase only means "stop talking": <c>(speech stopped)</c>, and the
    /// caller keeps the draft. True when the app token ended it.
    /// </summary>
    private async Task<bool> HandleTailInterruptAsync(string draft, WakeHit? hit, CancellationToken cancellationToken)
    {
        bool stoppedEarly = await _speech.StopAsync().ConfigureAwait(false);
        if (!stoppedEarly)
        {
            // The reply had been heard by the time the hit landed: nothing to cut short.
            return false;
        }

        if (draft.Length > 0 || hit is null)
        {
            _transcript.Notice(SpeechStoppedNotice);
            return false;
        }

        _transcript.Notice(InterruptedNotice);
        var (exit, request) = await InterruptFollowUpAsync(cancellationToken).ConfigureAwait(false);
        if (exit)
        {
            return true;
        }

        return request is not null && await RunMessageAsync(request, [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The push-to-talk key on an empty line. Listens under a spinner while the key watcher
    /// treats the key (or Enter) as "done" and ESC as "discard"; the app token exits. The transcript
    /// goes to the model exactly as a typed line would, minus the slash-command parse.
    /// </summary>
    private async Task<bool> HandlePushToTalkAsync(CancellationToken cancellationToken)
    {
        if (!_voice.Enabled)
        {
            _transcript.Notice(VoiceOffHint);
            return false;
        }

        if (!_voice.IsReady)
        {
            _transcript.Warning(_voice.StatusLine());
            return false;
        }

        return await ListenAndSendAsync(ListeningLabel(_voice.PushToTalkName), seed: null, requestSpoken: false, stripWakeWord: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The wake word, heard on an empty line. The same listen as push-to-talk, seeded with the
    /// listener's pre-roll; when the request came with the phrase nothing is listened for and the
    /// seed is transcribed at once. The phrase (as Whisper heard it) is stripped from the front.
    /// </summary>
    private Task<bool> HandleWakeAsync(WakeHit hit, CancellationToken cancellationToken) =>
        ListenAndSendAsync(WakeListeningLabel(_voice.WakePhrase, _voice.PushToTalkName), hit.Seed, hit.HasRequest, stripWakeWord: true, cancellationToken);

    /// <summary>One spoken message: listen, then send. Returns true when the shell should exit.</summary>
    private async Task<bool> ListenAndSendAsync(string label, byte[]? seed, bool requestSpoken, bool stripWakeWord, CancellationToken cancellationToken)
    {
        var listen = await ListenForRequestAsync(label, seed, requestSpoken, stripWakeWord, options: null, cancellationToken).ConfigureAwait(false);
        if (listen.Exit)
        {
            return true;
        }

        if (listen.Text is null)
        {
            return false;
        }

        if (listen.Text.Length == 0)
        {
            _transcript.Notice(HeardNothingNotice);
            return false;
        }

        return await RunMessageAsync(listen.Text, [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One listen under a spinner. Two tokens, two meanings: <c>finish</c> ends listening and
    /// transcribes (the push-to-talk key or Enter; already cancelled when the request was spoken
    /// with the wake word), <c>discard</c> (linked to the app token; ESC) abandons the utterance.
    /// Both are created and disposed here, per utterance. A usable transcript is shown as a
    /// <c>›</c> line and remembered; a discard or a failure is reported here and returns null.
    /// </summary>
    private async Task<ListenOutcome> ListenForRequestAsync(string label, byte[]? seed, bool requestSpoken, bool stripWakeWord, VoicePipelineOptions? options, CancellationToken cancellationToken)
    {
        using var discard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var finish = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        if (requestSpoken)
        {
            finish.Cancel();
        }

        var key = _voice.PushToTalk;
        _queuedClicks.Reset();
        var watcher = _keys.WatchAsync(discard, stop.Token, k => k.Key == key || k.Key == ConsoleKey.Enter, finish, spend: e => { _queuedClicks.Reset(); return ScrollInput(e); }, onClick: _pane.Enabled ? HintClickLine : null, editor: LiveEditor);

        ListenResult? result = null;
        try
        {
            result = await _transcript.WithSpinnerAsync(
                label,
                setLabel => _voice.ListenAsync(finish.Token, setLabel, discard.Token, seed, options)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (discard.IsCancellationRequested)
        {
            // ESC or the app token: nothing to send.
        }
        finally
        {
            stop.Cancel();
            await watcher.ConfigureAwait(false);
            DrainDiagnostics();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ListenOutcome(null, Exit: true, Discarded: false);
        }

        if (result is null)
        {
            DiagnosticLog.Info(VoiceSession.Category, ListenDiscardedLogLine);
            _transcript.Notice(VoiceDiscardedNotice);
            return new ListenOutcome(null, Exit: false, Discarded: true);
        }

        if (!result.Ok)
        {
            _transcript.Error(result.Detail);
            _transcript.Warning(_voice.StatusLine());
            return new ListenOutcome(null, Exit: false, Discarded: false);
        }

        string text = stripWakeWord ? SpeechTranscript.StripLeadingWakeWord(result.Text, _voice.WakePhrase) : result.Text;
        if (text.Length == 0)
        {
            return new ListenOutcome("", Exit: false, Discarded: false);
        }

        DismissSplash();
        _transcript.User(text);
        _input.Remember(text);
        _sentDraft = text;
        return new ListenOutcome(text, Exit: false, Discarded: false);
    }

    /// <summary>A listen ended by ESC before anything was transcribed.</summary>
    public const string ListenDiscardedLogLine = "Listen discarded (ESC).";

    /// <summary>The tracker's backstop: two interruptions in a row that heard nothing.</summary>
    public const string InterruptDisabledLogLine = "Interrupt switched off for this session: two interruptions in a row heard nothing.";

    /// <summary>
    /// The interrupt's echo guard, on the capture thread: the text at the play head spells
    /// something close to the phrase, or the probe found the assistant's own audio decoding as
    /// the phrase near the play head. Logged so a <c>--log</c> run shows what was taken for an echo.
    /// </summary>
    private static bool IsEcho(SpeechOutput speaker, EchoProbe? probe, string phrase, int echoMatch)
    {
        if (WakeWordMatch.SoundsLike(speaker.SpokenNear(SpeechOutput.DefaultEchoLookBack), phrase, echoMatch, out var near))
        {
            DiagnosticLog.Info("Voice", $"Interrupt ignored: the assistant just said \"{near}\", close to \"{phrase}\".");
            return true;
        }

        if (probe is not null && probe.HeardNear(speaker.PlayedBytes, EchoProbe.DefaultLookBack, EchoProbe.DefaultLookAhead, out long mark))
        {
            DiagnosticLog.Info("Voice", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Interrupt ignored: the assistant's own voice decodes as \"{phrase}\" at {probe.Seconds(mark):F1}s (play head {probe.Seconds(speaker.PlayedBytes):F1}s)."));
            return true;
        }

        return false;
    }

    /// <summary>
    /// One turn. <see cref="TurnOutcome.Exit"/> when the app token ended it. The turn
    /// budget lives inside <see cref="Assistant"/> as a deadline; the cancellation sources here
    /// are ESC, the app token and, for a spoken turn with interrupting on, the wake phrase.
    /// ESC before the model's first event is <see cref="TurnOutcome.Withdrawn"/>: the message
    /// leaves the history and <see cref="RunMessageAsync"/> hands it back to the line.
    ///
    /// <para>Whether the turn speaks is decided once, here: the saved switch and a server that
    /// answered. Toggling mid-reply cannot silence half of it. The speaker is the session's
    /// (<see cref="SpeechSession.BeginTurn"/>): the turn's token stops it while the turn runs
    /// (the registration below), and the audio still owed when the text ends — the tail — plays
    /// on under the input line, where <see cref="RunAsync"/> stops it. ESC while the reply is
    /// being heard stops the speech alone (the watcher's soft cancel, <c>StopSpeechFirst</c>: audio
    /// has reached the device and the speaker is not done) and the text streams on, silent, with
    /// <see cref="SpeechStoppedNotice"/> under it at the end; the next ESC — or the first one
    /// before any sound, or on a text-only turn — cancels the turn. The interrupt listener is
    /// armed before the thinking spinner (its warning, if any, prints outside every spinner) and
    /// stays armed until the turn ends; its hit cancels the turn's token from the thread pool,
    /// the same path as ESC's cancel; the idle read re-arms it over the tail. The <c>finally</c> is
    /// the one exit, in this order: the speech queue is completed, the listener is disarmed (which
    /// joins the capture pump), the key watcher is stopped and joined, then — for a cancelled turn,
    /// a hit, or a speech ESC stopped — the device is silenced and waited for, and only then is the
    /// reason decided and printed (<see cref="TurnEndNotice"/>): keys win over the microphone.</para>
    /// </summary>
    private async Task<TurnOutcome> RunTurnAsync(Assistant assistant, string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken, BotParticipant? bot = null, IReadOnlyList<TurnEvent>? replay = null, ComfyGeneration? picture = null, BotEscLadder? ladder = null, int ladderTurn = 0)
    {
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        // The mid-turn line hook (the pane only: without it nothing can be shown or typed under
        // a reply): a pane it opens reads the keys under the app token and the close signal the
        // turn's end may send (EndTurnAsync). The previous turn's pane was awaited before this one.
        _turnRunning = true;
        // The hint row is the turn's again (the usage part); the reading stays remembered for /speak.
        _hintReading = null;
        // A first Ctrl+C before the turn is forgotten: mid-turn the key cancels, and the next one at the line is a first again.
        DisarmExit();
        _paneClose?.Dispose();
        _paneClose = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var paneToken = _paneClose.Token;

        var effective = _effective();
        // The server is the reply's (2026-09-24): a running reflection pauses and runs again after it.
        YieldLearn(effective);
        // A /botchat turn (2026-09-24) speaks in its bot's own voice through the one synthesizer.
        var speaker = !(effective.TtsOutput && _speech.IsReady) ? null
            : bot is null ? _speech.BeginTurn(cancellationToken) : _speech.BeginTurn(cancellationToken, bot.Voice, bot.Speed);
        // The cancel (the second ESC, the wake phrase, the app token) silences the reply exactly as
        // before the tail outlived the turn; the registration goes with the turn, the speaker may not.
        using var stopSpeech = speaker is null ? default : turnCts.Token.Register(_speech.Stop);
        // The first ESC over a reply being heard: the speech stops, the turn runs on. Written on
        // the watcher task, read once the watcher is joined (the finally). The flag, not the
        // speaker's completion, decides the second press: the stop completes it asynchronously,
        // and an ESC inside that window must cancel, not stop again.
        bool speechStopped = false;
        bool StopSpeechFirst()
        {
            if (speaker is null || speechStopped || speaker.WrittenBytes == 0 || speaker.Completion.IsCompleted)
            {
                return false;
            }

            speechStopped = true;
            _speech.Stop();
            return true;
        }

        // A /botchat turn's press goes up the chat's ladder (2026-09-25, BotEscLadder): the voice, then this bot, then the
        // chat — the last two cancel the turn, HandleBotChatAsync telling them apart. Whether this bot has shown a word is
        // written on the turn task (the render loop) and read here, on the watcher's.
        bool shown = false;
        bool LadderPress()
        {
            bool heard = speaker is not null && speaker.WrittenBytes > 0;
            bool audible = heard && !speechStopped && !speaker!.Completion.IsCompleted;
            if (ladder!.Press(ladderTurn, Volatile.Read(ref shown) || heard, audible, responding: true) != BotPress.StopVoice)
            {
                return false;
            }

            speechStopped = true;
            _speech.Stop();
            return true;
        }

        // The queued count's double-click (2026-09-18): the pair is timed here on the pane's
        // clock and a key or a notch between the two ends it — the spend hook sees every one.
        _queuedClicks.Reset();
        var watcher = _keys.WatchAsync(turnCts, stop.Token, null, null, _pane.Enabled ? text => OnMidTurnLineAsync(text, turnCts, paneToken) : null, ladder is not null ? LadderPress : speaker is null ? null : StopSpeechFirst,
            e => { _queuedClicks.Reset(); return ScrollInput(e); }, onClick: _pane.Enabled ? HintClickLine : null, editor: LiveEditor);
        bool markdown = MarkdownTurn(effective.TranscriptMarkdown, _pane.Enabled, speaker is not null);
        bool styled = StyledReply(effective.TranscriptMarkdown, _pane.Enabled);
        // The shells found are probed afresh per turn (2026-09-21): an install during the session shows without a restart, and the schema and the run agree.
        // A /botchat turn's prompt and (empty) tool list are its own, set by HandleBotChatAsync.
        if (bot is null)
        {
            _interpreters.Refresh();
            PrepareTurn(assistant, _memory, _memoryTools, [.. _clockTools, .. _timerTools], _persona, _operata, _vocalia, effective.Memory, speaker is not null, effective.LlmMaxToolIterations, effective.LlmOfferTools, _webTools, effective.WebTools, ContextGuardFor(effective, _session.ContextLength), _fileTools, effective.FileTools, _pane.Enabled && effective.AskUser ? _askTools : null, SkillsFor(effective), markdown, _sessionTools, effective.SessionTool, ToolsText.DisabledSet(effective.ToolsDisabled), _mcp.Tools, effective.McpServers, effective.FileSafeEdits, _gitTools, effective.GitNativeTools, _shellTools, ShellOffered(effective), _processes, effective.ShellToolBridge, effective.ShellPoliceOutsidePaths, ObsidianToolsFor(_vaultTools, effective), ObsidianOffered(effective), _sqlTools, SqlOffered(effective, _sql), _comfyTools, ComfyOffered(effective, _comfy), effective.ShellPreferNative);
        }

        bool armed = false;
        EchoProbe? probe = null;
        if (speaker is not null && _voice.InterruptReady)
        {
            var phrase = _voice.WakePhrase;
            int echoMatch = effective.SttInterruptEchoGuard;
            // Keyword mode: under the speakers the microphone never hears the silence a final
            // result needs, so the interrupt listens for partial results over a phrase-only grammar.
            // The echo guard (capture thread: arithmetic and a log line, never the console) drops
            // a hit when the text at the play head holds the phrase or something close to it — the
            // grammar forces a near-sounding stretch of the assistant's own voice into the phrase —
            // or when the probe, the same grammar over the assistant's own audio, decoded the
            // phrase near the play head.
            var confirm = TimeSpan.FromMilliseconds(effective.SttInterruptConfirmMs);
            probe = _voice.CreateEchoProbe(speaker.Format, confirm);
            speaker.Probe = probe;
            armed = _voice.ArmWake(turnCts, _ => IsEcho(speaker, probe, phrase, echoMatch), WakeDetectorMode.Keyword, confirm);
            if (!armed && _voice.InterruptStatusLine() is { } line)
            {
                _transcript.Warning(line);
            }
        }

        _tailInterrupt = armed;

        // The pictures under the user's line, before the spinner (nothing writes while it runs);
        // with the toggle off nothing is even decoded. Read once, switch and size: a picture a
        // tool fetches mid-turn is drawn by the same switch at the same size.
        ThumbnailBox? thumbnails = effective.ShowImageThumbnails ? ThumbnailSize.Resolve(effective, WindowBox()) : null;
        if (thumbnails is { } box)
        {
            // A dropped picture's path is full; a pasted one has none (its bytes are kept for a double-click).
            var (tiles, ids) = ReadThumbnails(images, box, sandbox: false);
            _transcript.Images(tiles, ids);
        }

        // A /botchat turn's tokens stay out of the main conversation's usage (its auto-compact reads the last request).
        _botTurnRunning = bot is not null;
        if (bot is null)
        {
            _session.Usage.BeginTurn();
            // The busy row reads the mode quietly on every tick (BusyUsage); a hand-edited word is warned about here, once a turn.
            _ = MidTurnUsageMode.Resolve(_effective());
        }
        else
        {
            _transcript.Speaker(bot.Name, SpeakerColor(bot.ColorIndex));
            // A held /botchat reply's picture (Botchat image async off, later on 2026-09-25): under the name, above the words.
            if (picture is not null)
            {
                ShowBotPicture(bot, picture, late: false);
            }
        }

        // The spinner over the whole turn (the pane): its count keeps moving through the streamed
        // text, a buffered tool call and the next request's wait, so a quiet stretch never reads as
        // a stall. The label follows the turn's stage (TurnStages: thinking, writing, a tool's name)
        // and is renamed on the scope as the events arrive; the count runs on across them.
        var stages = new TurnStages(effective.LlmUseFunVerbs, _random);
        string label = stages.Start();
        _showThinking = effective.LlmShowThinking;
        _thinkingSince = null;
        using var busy = _transcript.BeginBusy(label);
        // What /imagine made since the last message rides with this one (2026-09-24): the notes ahead of the text, the pictures after the user's own.
        var (sentText, sentImages) = bot is null ? TakeImagineNotes(text, images) : (text, images);
        // A held /botchat turn (later on 2026-09-25) replays the events it already has: the model is not asked again.
        var events = (replay is null ? assistant.RunTurnAsync(sentText, sentImages, turnCts.Token) : ReplayAsync(replay, turnCts.Token)).GetAsyncEnumerator(turnCts.Token);
        // The next event, selected against the mid-turn acts (NextEventAsync): a quick command
        // runs between two events, however long the model takes over the next one.
        Task<bool> NextAsync() => NextEventAsync(events.MoveNextAsync().AsTask());
        Interrupt interrupt;
        TurnOutcome outcome;
        bool cancelled = false;
        bool failed = false;
        bool sawError = false;
        bool stoppedEarly = false;
        // The model's first event (after the opening calls) arrived: an ESC before it withdraws
        // the message rather than cancelling a reply (TurnEndNotice).
        bool returned = false;
        WakeHit? hit = null;
        var reply = new StringBuilder();
        var opening = new List<TurnEvent>(4);
        var trace = new TurnTrace();
        _lastTrace = null;

        // The opening calls' 🛠️ lines above the reply's glyph, once, whichever way the wait ended.
        void RenderOpening()
        {
            foreach (var evt in opening)
            {
                Render(evt, speaker, reply, thumbnails);
            }

            opening.Clear();
        }

        // The opening calls' events come first and at once; the spinner stays over the model's
        // wait behind them, and they are shown after it, above the glyph. Without the pane the
        // thinking is not shown (2026-09-26): Spectre's spinner stays over it too, as it always did.
        async Task<bool> FirstWaitAsync()
        {
            bool next = await NextAsync().ConfigureAwait(false);
            while (next && (Assistant.IsOpeningEvent(events.Current) || (busy is null && events.Current is TurnEvent.ThinkingDelta)))
            {
                if (Assistant.IsOpeningEvent(events.Current))
                {
                    opening.Add(events.Current);
                }

                next = await NextAsync().ConfigureAwait(false);
            }

            return next;
        }

        try
        {
            // Without the pane the spinner is Spectre's Status, which nothing may be written under:
            // it covers the first wait alone, and the reply streams with no spinner, as it always did.
            bool more = busy is null
                ? await _transcript.WithSpinnerAsync(label, FirstWaitAsync).ConfigureAwait(false)
                : await FirstWaitAsync().ConfigureAwait(false);
            returned = true;
            RenderOpening();
            _transcript.BeginAssistant(styled);
            while (more)
            {
                // The stage ahead of the event's own lines (the 🛠️ line under a tool's name, not a
                // stale one); the opening calls above never reach it — buffered, already done.
                if (busy is not null && stages.Advance(events.Current) is { } stage)
                {
                    busy.SetLabel(stage);
                }

                Render(events.Current, speaker, reply, thumbnails);
                if (events.Current is TurnEvent.TextDelta { Text.Length: > 0 })
                {
                    Volatile.Write(ref shown, true);
                }

                sawError |= events.Current is TurnEvent.Notice { IsError: true };
                trace.Observe(events.Current);
                DrainDiagnostics();
                PrintAlerts();
                more = await NextAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested)
        {
            RenderOpening();
            _transcript.BeginAssistant(styled);
            cancelled = true;
        }
        catch (Exception ex)
        {
            // Assistant turns server failures into Notice events; anything reaching here is ours.
            RenderOpening();
            _transcript.BeginAssistant(styled);
            _transcript.Error(TurnFailedPrefix + Assistant.Explain(ex));
            failed = true;
        }
        finally
        {
            try
            {
                await events.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A cancelled iterator has nothing left to clean up.
            }

            // The text is done: the trailing sentence is queued and the queue closed.
            speaker?.CompleteAdding();

            if (armed)
            {
                hit = _voice.DisarmWake();
            }

            stop.Cancel();
            interrupt = await watcher.ConfigureAwait(false);
            // A cancelled turn (ESC, the phrase, the app token) has silenced the device through
            // the registration, a speech ESC through the hook; wait for the consumer so the reason
            // below is final. Otherwise the tail plays on under the input line and the session
            // keeps the speaker. After the disarm: the stop disposes the probe, whose guard ran on
            // the capture thread DisarmWake joined. After the watcher: the hook's flag is read once
            // its task is done, so an ESC landing as the text ended still counts. A hit the disarm
            // returned counts even before its pool continuation reached the token.
            if (speaker is not null && (turnCts.IsCancellationRequested || hit is not null || speechStopped))
            {
                stoppedEarly = await _speech.StopAsync().ConfigureAwait(false);
            }

            string? notice;
            (notice, outcome) = TurnEndNotice(cancelled, stoppedEarly, interrupt, hit is not null, cancellationToken.IsCancellationRequested, returned);
            if (bot is not null && notice is not null && (cancelled || outcome == TurnOutcome.Withdrawn))
            {
                // A bot has no line to take back (2026-09-25): cut short by the ladder, or cancelled.
                notice = ladder?.Skipped(ladderTurn) == true ? BotChat.CutShortNotice(bot.Name) : CancelledNotice;
            }
            // Whether this reply was cut short for the queue's sake (2026-09-18): the key or the
            // phrase cancelled it, or it was withdrawn — Continue covers a key cancel after the
            // first event, so the outcome alone cannot say. Read once by RunMessageAsync.
            _lastTurnCancelled = cancelled || outcome is TurnOutcome.Interrupted or TurnOutcome.Withdrawn;
            _lastTurnFailed = failed || sawError;
            // Why it ended, for the --log file (2026-09-19): the assistant's own closing line says
            // "cancelled" and no more; the reason is the screen's to know.
            if (TurnOutcomeLogLine(outcome, cancelled, interrupt, hit is not null, cancellationToken.IsCancellationRequested) is { } reason)
            {
                DiagnosticLog.Info(Assistant.TurnCategory, reason);
            }

            if (speechStopped)
            {
                DiagnosticLog.Debug(SpeechSession.Category, SpeechStoppedLogLine);
            }
            if (notice is not null)
            {
                _transcript.Notice(notice);
            }

            if (outcome == TurnOutcome.Withdrawn)
            {
                // The message goes back to the line (RunMessageAsync), so it leaves the history
                // too: re-sent, the model sees it once. Committed before the request, so it is
                // the last turn; on a first turn the opening pairs go with it and are seeded again.
                assistant.History.RemoveLastTurn();
            }

            _transcript.EndAssistant();
            // A reflection that ended during the reply reports here, under it, never inside it.
            DrainLearn();
            // A reply that ran to its end with no usage report is counted as unreported; a cut or
            // failed one is not (its report never had the chance to arrive).
            _botTurnRunning = false;
            if (!cancelled && !failed && !sawError && bot is null)
            {
                _session.Usage.EndTurn();
                // The turn the reflection may learn from: whole, and not cut by the wake phrase.
                if (outcome == TurnOutcome.Continue)
                {
                    _lastTrace = trace;
                    if (trace.WroteSkill)
                    {
                        // The turn kept its own lesson: the calls since the last reflection are spent with it.
                        DiagnosticLog.Info(SkillCatalog.Category, "The turn wrote a skill itself (" + trace + "); the tally starts over.");
                        _learnTrace = null;
                    }
                    else
                    {
                        (_learnTrace ??= new TurnTrace()).Absorb(trace);
                    }
                }
            }

            // What /copy sees: the reply as shown, partial or whole, with the line that asked for it.
            _log.Add(text, reply.ToString());
            _lastReply = reply.ToString();
            // What the session store keeps (2026-09-18): the same pair, the model's call count and
            // the request's tokens, then the whole history as it stands. A withdrawn turn left the
            // history already and is not written.
            if (outcome != TurnOutcome.Withdrawn && bot is null)
            {
                LogTurn(assistant, text, reply.ToString(), trace, cancelled, effective, cancellationToken);
            }

            DrainDiagnostics();
        }

        return outcome;
    }

    private const string SessionsCategory = "Sessions";

    /// <summary>The log category of the screen's own lines: the commands, the close.</summary>
    public const string AppCategory = "App";

    /// <summary>Why the screen closed, set at the return that decides it; null = the app token (every other return 0).</summary>
    private string? _exitReason;

    public const string ExitByCommand = "/exit";
    public const string ExitByInterrupt = "Ctrl+C twice";
    public const string ExitByEndOfInput = "end of input";
    public const string ExitByAppToken = "the app token";

    /// <summary>The screen's last line in the log: <c>Screen closed: /exit</c>. Pinned.</summary>
    public static string ScreenClosedLogLine(string reason) => "Screen closed: " + reason;

    /// <summary>
    /// A sent command in the log: <c>Command /sessions: purge older 7</c> — the word as typed, the
    /// argument cut to <see cref="CommandArgumentChars"/>; <c>(unknown)</c> / <c>(overloaded)</c> after
    /// the word for one the parser refused. Pinned.
    /// </summary>
    public static string CommandLogLine(SlashCommand command, string text)
    {
        string trimmed = text.Trim();
        int space = trimmed.IndexOf(' ');
        string word = space < 0 ? trimmed : trimmed[..space];
        string argument = space < 0 ? "" : LogText.Excerpt(trimmed[(space + 1)..], CommandArgumentChars);
        string note = command switch
        {
            SlashCommand.Unknown => " (unknown)",
            SlashCommand.Overloaded => " (overloaded)",
            _ => "",
        };
        return "Command " + word + note + (argument.Length == 0 ? "" : ": " + argument);
    }

    public const int CommandArgumentChars = 80;

    /// <summary>A command sent under a reply: <c>Mid-turn command /compact: Refused</c>. Pinned.</summary>
    public static string MidTurnCommandLogLine(string word, MidTurnClass policy) => "Mid-turn command " + word + ": " + policy;

    /// <summary>
    /// The turn into the store: the session row begun at the first completed turn (the first line
    /// its title, the connected model), the turn appended, the history saved whole. Under
    /// <c>Session naming mode</c> = <c>model-written</c> the first turn also starts the title request
    /// (<see cref="StartTitling"/>). Nothing while <c>Session logging</c> is off — a session begun
    /// earlier is left as it was.
    /// </summary>
    private void LogTurn(Assistant assistant, string text, string reply, TurnTrace trace, bool cancelled, AppSettingsData effective, CancellationToken appToken)
    {
        if (!effective.SessionLogging)
        {
            return;
        }

        bool first = _sessionId is null;
        _sessionId ??= _sessions.Begin(SessionText.FirstLineTitle(text), _session.Endpoint?.ModelId ?? "");
        if (_sessionId is not { } id)
        {
            return;
        }

        if (first)
        {
            RefreshSessionTitle();   // the first line on the rule above the input row from this turn on
        }

        var usage = _session.Usage.LastReply;
        _sessions.AppendTurn(id, text, reply, trace.ToolCalls, trace.ToolNames, trace.LoadedSkills, trace.Errors, usage.Input, usage.Output, cancelled);
        SaveSessionHistory(assistant);
        if (first && !cancelled && SessionNamingMode.Resolve(effective) == SessionNaming.ModelWritten)
        {
            StartTitling(id, text, reply, appToken);
        }
    }

    /// <summary>The history as it stands into the current session's row (after every turn and every compact); nothing without a row.</summary>
    private void SaveSessionHistory(Assistant assistant)
    {
        if (_sessionId is { } id)
        {
            var messages = assistant.History.Messages;
            _sessions.SaveHistory(id, SessionHistory.ToJson(messages));
        }
    }

    /// <summary>
    /// The model-written title (2026-09-18): one background request over the first line and the
    /// first reply (<see cref="SessionText.TitleRequest"/>), no tools, no reasoning, in
    /// <see cref="LlmSession.StartTitling"/>'s slot. The answer lands only while the row still
    /// carries its first-line title (<see cref="SessionStore.SetTitle"/>); blank, failed or
    /// cancelled leaves the first line standing. Nothing in the transcript: a Debug line alone.
    /// </summary>
    private void StartTitling(long id, string text, string reply, CancellationToken appToken)
    {
        _session.StartTitling(async (assistant, token) =>
        {
            try
            {
                var request = new List<ChatMessage>
                {
                    new(ChatRole.System, SessionText.TitleInstruction),
                    new(ChatRole.User, SessionText.TitleRequest(text, reply)),
                };
                var response = await assistant.RequestAsync(request, [], ReasoningEffort.None, token).ConfigureAwait(false);
                if (SessionText.CleanTitle(response.Text) is { } title && _sessions.SetTitle(id, title, TitleSource.Model))
                {
                    DiagnosticLog.Debug(SessionsCategory, $"Session {SessionText.Id(id)} titled by the model: {title}");
                    if (_sessionId == id)
                    {
                        RefreshSessionTitle();   // the rule follows at the pane's next tick; a conversation cleared meanwhile keeps nothing
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The app or a reconnect: the first line stands.
            }
            catch (Exception ex)
            {
                DiagnosticLog.Debug(SessionsCategory, $"The title request for session {SessionText.Id(id)} failed; the first line stands: {ex.Message}");
            }
        }, appToken);
    }

    /// <summary>
    /// The thumbnails of <paramref name="images"/> scaled to fit <paramref name="box"/>, each registered for a double-click
    /// (<see cref="RegisterPicture"/>, <paramref name="sandbox"/> saying whether the paths are the working directory's);
    /// a picture the codecs refuse is left out.
    /// </summary>
    private (List<ImageThumbnail> Tiles, List<int> Ids) ReadThumbnails(IReadOnlyList<ImageAttachment> images, ThumbnailBox box, bool sandbox)
    {
        var tiles = new List<ImageThumbnail>(images.Count);
        var ids = new List<int>(images.Count);
        foreach (var image in images)
        {
            if (ImageThumbnail.Read(image, box.Columns, box.MaxRows) is { } thumbnail)
            {
                tiles.Add(thumbnail);
                ids.Add(RegisterPicture(image, sandbox));
            }
        }

        return (tiles, ids);
    }

    // ── The picture strip (later still on 2026-09-24) ───────────────────────

    /// <summary>
    /// The pictures ComfyUI made (a <c>generate_image</c> result, an <c>/imagine</c>) into the strip over the input line,
    /// in the order they came, so the last one lands leftmost; each registered for a double-click as the transcript's are.
    /// Kept whatever <c>ComfyUI picture strip</c> says — the setting hides the strip, it does not stop the gathering, so a
    /// flip back on shows the session's pictures. <c>Show image thumbnails</c> is not consulted. Any thread.
    /// </summary>
    private void AddToPictureStrip(IReadOnlyList<ImageAttachment> images)
    {
        foreach (var image in images)
        {
            if (ImageThumbnail.Read(image, PictureStrip.MaxColumns, PictureStrip.Rows) is { } tile)
            {
                _pictureStrip.Add(tile, RegisterPicture(image, sandbox: true));
            }
        }

        _pane.RedrawStrip();
    }

    /// <summary>Whether the strip is on the screen now: the pane, the setting, and a window that has room for it (<see cref="ScreenPane.StripRows"/> as last drawn).</summary>
    private bool PictureStripOffered() =>
        _pane.Enabled && _effective().ComfyPictureStrip && _pictureStrip.Count > 0 && _pane.StripRows > 0;

    /// <summary>
    /// Left or Right at an empty idle line with the strip on the screen and the splash declining the key (the input line's
    /// <c>emptyArrow</c> hook, after <see cref="CycleSplash"/>): the highlight moves (<see cref="PictureStrip.Step"/>) and the
    /// pane follows at once. False — the key is the line's — without the strip.
    /// </summary>
    private bool StepPictureStrip(int step)
    {
        if (!PictureStripOffered() || !_pictureStrip.Step(step))
        {
            return false;
        }

        _pane.RedrawStrip();
        return true;
    }

    /// <summary>Enter with nothing to send (the input line's <c>emptyEnter</c> hook): the highlighted picture opened as a double-click opens it. False without the strip or a highlight.</summary>
    private bool OpenStripPicture()
    {
        if (!PictureStripOffered() || _pictureStrip.SelectedId is not { } id)
        {
            return false;
        }

        OpenPicture(id);
        return true;
    }

    // ── Opening a picture from the transcript (later on 2026-09-24) ─────────

    /// <summary>A double-clicked picture whose file is gone since it was drawn. Pinned.</summary>
    public static string PictureGoneError(string path) => $"{path} is no longer there";

    /// <summary>A double-clicked picture whose editor would not start. Pinned.</summary>
    public static string PictureOpenFailedError(string name, string detail) => $"Could not open {name}: {detail}";

    /// <summary>The folder a picture with no file of its own (pasted, the bundled splash) is written into to be opened.</summary>
    public static string PictureTempFolder => Path.Combine(Path.GetTempPath(), "NeonSidekick", "pictures");

    /// <summary>
    /// Keeps <paramref name="image"/> for a double-click and returns its id. The file is found now — the working directory may
    /// change later: <paramref name="sandbox"/> resolves the path under it; else a fully qualified path that exists is taken
    /// as it is. A picture with neither (pasted from the clipboard, the bundled splash) keeps its bytes.
    /// </summary>
    private int RegisterPicture(ImageAttachment image, bool sandbox)
    {
        string? full = null;
        if (sandbox)
        {
            if (_files.Resolve(image.Path, forWrite: false, out string resolved) == FileOutcome.Ok && File.Exists(resolved))
            {
                full = resolved;
            }
        }
        else if (Path.IsPathFullyQualified(image.Path) && File.Exists(image.Path))
        {
            full = image.Path;
        }

        string name = Path.GetFileName(image.Path.Replace('/', Path.DirectorySeparatorChar));
        lock (_pictures)
        {
            _pictures.Add(new PictureSource(name.Length > 0 ? name : "picture.png", full, image.Bytes));
            return _pictures.Count - 1;
        }
    }

    /// <summary>
    /// A double-click on picture <paramref name="id"/>: its file (one without, written into <see cref="PictureTempFolder"/> first)
    /// handed to the <c>Image viewer</c> setting's command, or the one Windows registers. An open prints nothing (2026-09-24,
    /// the user's call); only an error does, through the flow sink, so it waits for a running reply. Any thread.
    /// </summary>
    private void OpenPicture(int id)
    {
        PictureSource? source;
        lock (_pictures)
        {
            source = id >= 0 && id < _pictures.Count ? _pictures[id] : null;
        }

        if (source is null)
        {
            return;
        }

        string editor = _effective().ImageEditor;
        try
        {
            string path;
            if (source.FullPath is { } full)
            {
                if (!File.Exists(full))
                {
                    _flow.Error(PictureGoneError(full));
                    return;
                }

                path = full;
            }
            else
            {
                Directory.CreateDirectory(PictureTempFolder);
                path = Path.Combine(PictureTempFolder, source.Name);
                File.WriteAllBytes(path, source.Bytes);
            }

            _openImage(path, editor);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _flow.Error(PictureOpenFailedError(source.Name, ex.Message));
        }
    }

    /// <param name="thumbnails">The thumbnail box, read once at the turn's start (null with <c>Show image thumbnails</c> off): the pictures a tool fetched are drawn under its 🛠️ line the way sent ones are drawn under the user's.</param>
    private void Render(TurnEvent evt, SpeechOutput? speaker, StringBuilder reply, ThumbnailBox? thumbnails)
    {
        if (evt is TurnEvent.ToolCall counted)
        {
            // Every call, the quiet ones too: the tool run's summary counts calls, not lines (2026-09-22).
            _transcript.CountToolCall(counted.Name);
        }

        if (evt is not TurnEvent.ThinkingDelta)
        {
            // The thinking block is over; the next one (after a tool call) counts its own time.
            _thinkingSince = null;
        }

        switch (evt)
        {
            case TurnEvent.ThinkingDelta thinking:
                // Shown only, and only when asked (2026-09-26): never fed to the speaker, the reply,
                // /copy or the session log. The next write of anything else folds the block.
                if (_showThinking)
                {
                    long now = _time.GetTimestamp();
                    _thinkingSince ??= now;
                    _transcript.AppendThinking(thinking.Text, _time.GetElapsedTime(_thinkingSince.Value, now));
                }

                break;
            case TurnEvent.TextDelta delta:
                _transcript.AppendDelta(delta.Text);
                speaker?.Feed(delta.Text);
                reply.Append(delta.Text);
                break;
            case TurnEvent.ToolCall call when QuietTools.Contains(call.Name):
                // The result line says it all (🛠️ remembered: …, 🛠️ Friday 11 September 2026, …).
                break;
            case TurnEvent.ToolCall call:
                _transcript.Tool(call.Name, call.ArgumentsJson);
                break;
            case TurnEvent.ToolResult result when string.Equals(result.Name, AskUserTool.ToolName, StringComparison.Ordinal):
                // The answers one dim line each (a Q&A reads on after the pane has gone); ahead of the quiet case, which would fold them into one.
                _transcript.ToolNotes(result.Text);
                break;
            case TurnEvent.ToolResult result when string.Equals(result.Name, LoadSkillTool.ToolName, StringComparison.Ordinal):
                // The instructions are the model's to read; the line says which skill (LoadSkillTool.Note), behind the skills' glyph (later on 2026-09-21).
                _transcript.SkillNote(LoadSkillTool.Note(result.Text));
                break;
            case TurnEvent.ToolResult result when string.Equals(result.Name, SkillEditorTool.ToolName, StringComparison.Ordinal):
                // created / updated / renamed / deleted skill 'x' (SkillText): a quiet tool's one line, behind the skills' glyph too.
                _transcript.SkillNote(result.Text);
                break;
            case TurnEvent.ToolResult result when string.Equals(result.Name, RecallMemoryTool.ToolName, StringComparison.Ordinal):
                // The list is the model's to read (/memory shows it); the line says how many (RecallMemoryTool.Note).
                _transcript.ToolNote(RecallMemoryTool.Note(result.Text));
                break;
            case TurnEvent.ToolResult result when string.Equals(result.Name, SessionManagerTool.ToolName, StringComparison.Ordinal):
                // The sessions are the model's to read; the line is the result's header (SessionManagerTool.Note).
                _transcript.ToolNote(SessionManagerTool.Note(result.Text));
                break;
            case TurnEvent.ToolResult result when GitToolNames.Contains(result.Name):
                // A status, a log, a patch is the model's to read; the line is the result's header (GitText.Note, 2026-09-20).
                _transcript.ToolNote(GitText.Note(result.Text));
                break;
            case TurnEvent.ToolResult result when ObsidianToolNames.Contains(result.Name):
                // A note, a search, a backlink list is the model's to read; the line is the result's header (ObsidianText.Note, 2026-09-22).
                _transcript.ToolNote(ObsidianText.Note(result.Text));
                break;
            case TurnEvent.ToolResult result when SqlToolNames.Contains(result.Name):
                // A table of rows is the model's to read; the line is the result's header (SqlText.Note, 2026-09-23).
                _transcript.ToolNote(SqlText.Note(result.Text));
                break;
            case TurnEvent.ToolResult result when ShellToolNames.Contains(result.Name) && ShellText.IsOutside(result.Text):
                // The outside-paths police refused it (2026-09-22): the same one line, behind the officer rather than the tools' glyph.
                _transcript.PoliceNote(ShellText.Note(result.Text));
                break;
            case TurnEvent.ToolResult result when ShellToolNames.Contains(result.Name):
                // A command's output is the model's to read; the line is the result's header: the exit code, the time, the command (ShellText.Note, 2026-09-21).
                _transcript.ToolNote(ShellText.Note(result.Text));
                break;
            case TurnEvent.ToolResult result when string.Equals(result.Name, GenerateImageTool.ToolName, StringComparison.Ordinal):
                // The picture's line, then — with ComfyUI show prompts on (later still on 2026-09-24) — what was sent, in full.
                ComfyLines(result.Text, _transcript.ToolNote);
                ToolThumbnails(result, thumbnails);
                if (result.Images is { Count: > 0 } made)
                {
                    AddToPictureStrip(made);
                }

                break;
            case TurnEvent.ToolResult result when QuietTools.Contains(result.Name):
                _transcript.ToolNote(result.Text);
                ToolThumbnails(result, thumbnails);
                break;
            case TurnEvent.ToolResult result:
                _transcript.ToolResult(result.Name, result.Text);
                break;
            case TurnEvent.Notice notice when notice.IsError:
                _transcript.Error(notice.Text);
                break;
            case TurnEvent.Notice notice:
                _transcript.Notice(notice.Text);
                break;
            case TurnEvent.Usage usage when !_botTurnRunning:
                _session.Usage.Add(usage.Tokens);
                break;
        }
    }

    /// <summary>The pictures a tool fetched or made, drawn under its line (null <paramref name="thumbnails"/> with <c>Show image thumbnails</c> off).</summary>
    private void ToolThumbnails(TurnEvent.ToolResult result, ThumbnailBox? thumbnails)
    {
        if (thumbnails is { } box && result.Images is { Count: > 0 } fetched)
        {
            var (tiles, ids) = ReadThumbnails(fetched, box, sandbox: true);
            _transcript.Images(tiles, ids);
        }
    }

    /// <summary>
    /// A generation's result on the transcript (later still on 2026-09-24): the picture's line through <paramref name="head"/>,
    /// then, with <c>ComfyUI show prompts</c> on, the <c>prompt:</c> and <c>negative:</c> lines whole (<see cref="TranscriptRenderer.ToolDetail"/>).
    /// </summary>
    private void ComfyLines(string result, Action<string> head)
    {
        var (line, details) = ComfyText.SplitGenerated(result);
        head(line);
        if (_effective().ComfyShowPrompts)
        {
            foreach (string detail in details)
            {
                _transcript.ToolDetail(detail);
            }
        }
    }

    /// <summary>Runs on whichever thread logged; only queues. Warning and above reach the transcript.</summary>
    private void OnDiagnostic(DiagnosticEvent evt)
    {
        if (evt.Level >= DiagnosticLevel.Warning)
        {
            _pending.Enqueue(evt);
        }
    }

    private void DrainDiagnostics()
    {
        while (_pending.TryDequeue(out var evt))
        {
            _transcript.Diagnostic(evt);
        }
    }
}
