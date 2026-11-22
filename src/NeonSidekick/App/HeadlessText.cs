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

    /// <summary>The run's closing summary of what failed: <c>2 failed: a message (…), /ha (no such entity)</c>.</summary>
    public static string FailedSummary(IReadOnlyList<(string What, string Why)> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Count.ToString(CultureInfo.InvariantCulture) + " failed: "
            + string.Join(", ", failures.Select(f => f.Why.Length == 0 ? f.What : f.What + " (" + f.Why + ")"));
    }

    /// <summary>The why of a server a call needed that still wanted a sign-in.</summary>
    public const string NeedsSignIn = "needs a sign-in";

    /// <summary>The why of a Ralph loop that stopped short of done, and of a review that stopped.</summary>
    public static string Stopped(string how) => "stopped: " + how;

    /// <summary>The line above the piped context in the message.</summary>
    public const string PipedContextHeading = "Piped input:";

    /// <summary>The message's note when the piped context was cut to fit.</summary>
    public static string PipedCutNote(int kept, int total) =>
        $"(the piped input was cut to fit: {kept.ToString("N0", CultureInfo.InvariantCulture)} of {total.ToString("N0", CultureInfo.InvariantCulture)} characters, the start and the end kept)";

    /// <summary>The <c>[notice]</c> when the piped context was cut.</summary>
    public static string PipedCutNotice(int kept, int total) =>
        $"The piped input was cut to {kept.ToString("N0", CultureInfo.InvariantCulture)} of {total.ToString("N0", CultureInfo.InvariantCulture)} characters to fit the model's window.";

    /// <summary>The <c>[notice]</c> when stdin was redirected but nothing came within the wait.</summary>
    public const string NoPipedInput = "Nothing came on stdin within 3 seconds, so the prompt runs alone. Pass slow input with --prompt-file, or skip stdin with < NUL.";

    /// <summary>The <c>[notice]</c> when the prompt is a command that takes no piped text.</summary>
    public static string PipedInputIgnored(string command) => $"The piped input was left out: {command} is a command, and only a message takes it.";

    /// <summary>A blank <c>--prompt</c> or <c>--prompt-file</c>.</summary>
    public const string BlankPrompt = "the prompt is blank";

    /// <summary>A <c>--prompt-file</c> that couldn't be read.</summary>
    public static string PromptFileUnreadable(string path, string reason) => $"--prompt-file {path} couldn't be read: {reason}";

    /// <summary>The <c>[notice]</c> at the end naming the session the run wrote, for the next run's <c>--session</c>.</summary>
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

    /// <summary>The <c>[notice]</c> when the session's folder is gone.</summary>
    public static string SessionFolderGone(string folder) => $"The session's folder {folder} is gone; staying in this one.";

    /// <summary>The <c>[notice]</c> when Session logging is off: the session is carried on but not saved.</summary>
    public const string SessionNotSaved = "Session logging is off, so this run's turns won't be saved to the session.";
}
