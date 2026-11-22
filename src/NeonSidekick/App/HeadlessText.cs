using System.Globalization;

namespace NeonSidekick.App;

/// <summary>
/// The headless run's own words (2026-11-21, feature ideas #41 to #44): the failure summary, the piped context's heading and its notices,
/// and the session's. The older headless strings stay constants on <see cref="SidekickApp"/>.
/// </summary>
public static class HeadlessText
{
    /// <summary>What failed, when it was a message rather than a command.</summary>
    public const string AMessage = "a message";

    /// <summary>
    /// The run's closing summary of what failed: <c>3 failed: a message ×2 (…), /ha (no such entity)</c>. The number is the failures, a
    /// repeat counted (2026-11-22, the review's #132), so it can be more than the entries named.
    /// </summary>
    public static string FailedSummary(IReadOnlyList<(string What, string Why, int Count)> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Sum(f => f.Count).ToString(CultureInfo.InvariantCulture) + " failed: "
            + string.Join(", ", failures.Select(f => f.What + (f.Count > 1 ? " ×" + f.Count.ToString(CultureInfo.InvariantCulture) : "") + (f.Why.Length == 0 ? "" : " (" + f.Why + ")")));
    }

    /// <summary>What failed when a session's save did (2026-11-22, the review's #125).</summary>
    public const string TheSession = "the session";

    /// <summary>Why: <c>not saved: database is locked</c>.</summary>
    public static string NotSaved(string? reason) => "not saved" + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason);

    /// <summary>
    /// The app's own tags at a line's start (2026-11-22, the review's #29): only these route a line to the info writer under <c>--quiet</c>;
    /// an answer that starts with a Markdown link stays a reply.
    /// </summary>
    public static readonly IReadOnlyList<string> Tags = ["[error] ", "[notice] ", "[warning] ", "[tool] "];

    /// <summary>The why of a server a call needed that still wanted a sign-in.</summary>
    public const string NeedsSignIn = "needs a sign-in";

    /// <summary>The why of a Ralph loop that stopped short of done, and of a review that stopped.</summary>
    public static string Stopped(string how) => "stopped: " + how;

    /// <summary>The line above the piped context in the message.</summary>
    public const string PipedContextHeading = "Piped input:";

    /// <summary>The message's note when the piped context was cut to fit.</summary>
    public static string PipedCutNote(long kept, long total) =>
        $"(the piped input was cut to fit: {kept.ToString("N0", CultureInfo.InvariantCulture)} of {total.ToString("N0", CultureInfo.InvariantCulture)} characters, the start and the end kept)";

    /// <summary>The <c>[notice]</c> when the piped context was cut.</summary>
    public static string PipedCutNotice(long kept, long total) =>
        $"The piped input was cut to {kept.ToString("N0", CultureInfo.InvariantCulture)} of {total.ToString("N0", CultureInfo.InvariantCulture)} characters to fit the model's window.";

    /// <summary>
    /// The <c>[notice]</c> when stdin was redirected but nothing came within the wait: the wait's seconds and the shell's empty input, <c>&lt; NUL</c>
    /// on Windows, <c>&lt; /dev/null</c> elsewhere (2026-11-22, the review's #51).
    /// </summary>
    public static string NoPipedInput(TimeSpan wait, bool windows) =>
        $"Nothing came on stdin within {((int)wait.TotalSeconds).ToString(CultureInfo.InvariantCulture)} seconds, so the prompt runs alone. Pass slow input with --prompt-file, or skip stdin with {(windows ? "< NUL" : "< /dev/null")}.";

    /// <summary>A redirected stdin that couldn't be read (2026-11-22, the review's #135): a bad start, exit 2.</summary>
    public static string PipedUnreadable(string reason) => $"stdin couldn't be read: {reason}";

    /// <summary>A <c>--session</c> run whose sessions store couldn't be opened (2026-11-22, the review's #62): a bad start, exit 2.</summary>
    public const string SessionsUnavailable = "--session: the sessions store couldn't be opened (see the log)";

    /// <summary>The <c>[notice]</c> when the prompt is a command that takes no piped text.</summary>
    public static string PipedInputIgnored(string command) => $"The piped input was left out: {command} is a command, and only a message takes it.";

    /// <summary>A blank <c>--prompt</c> or <c>--prompt-file</c>.</summary>
    public const string BlankPrompt = "the prompt is blank";

    /// <summary>A <c>--prompt-file</c> that couldn't be read.</summary>
    public static string PromptFileUnreadable(string path, string reason) => $"--prompt-file {path} couldn't be read: {reason}";

    /// <summary>The <c>[notice]</c> at the end naming the session the run wrote, for the next run's <c>--session</c>.</summary>
    /// <summary>Why a headless code audit stopped short (2026-11-22, the review's #15: out of the loop's code).</summary>
    public const string FolderMovedWord = "the working folder moved";

    public const string PassStalledWord = "a pass didn't end on its own";

    public static string SessionLine(long id) => "session #" + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A <c>--session</c> naming no stored session.</summary>
    public static string NoSuchSession(long id) => $"--session: no stored session #{id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary><c>--session last</c> with nothing stored.</summary>
    public const string NoSessions = "--session last: no sessions are stored";

    /// <summary>A stored session whose history couldn't be read.</summary>
    public static string SessionUnreadable(long id, string reason) => $"--session: session #{id.ToString(CultureInfo.InvariantCulture)} couldn't be read: {reason}";

    /// <summary>The <c>[notice]</c> naming the session carried on.</summary>
    public static string SessionResumed(long id, int turns) =>
        $"Carrying on session #{id.ToString(CultureInfo.InvariantCulture)} ({turns.ToString(CultureInfo.InvariantCulture)} {(turns == 1 ? "turn" : "turns")}).";

    /// <summary>The <c>[notice]</c> when the session's folder is used for this run.</summary>
    public static string SessionFolder(string folder) => $"Working in the session's folder for this run: {folder}";

    /// <summary>The <c>[notice]</c> when the session's folder is gone, or is written for the other system (2026-11-22, the review's #82).</summary>
    public static string SessionFolderGone(string folder) => $"The session's folder {folder} is gone; staying in this one.";

    /// <summary>The <c>[notice]</c> when Session logging is off: the session is carried on but not saved.</summary>
    public const string SessionNotSaved = "Session logging is off, so this run's turns won't be saved to the session.";
}
