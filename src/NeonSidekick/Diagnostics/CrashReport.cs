using System.Globalization;
using System.Text;

namespace NeonSidekick.Diagnostics;

/// <summary>
/// The record of a crash: the exception that took the app down, with its stack, appended to
/// <see cref="FileName"/> under the settings directory (<c>%USERPROFILE%\.neonsidekick\crash.log</c>).
///
/// <para>The terminal window closes with the process, so an unhandled exception's stderr print is
/// never read; <c>--log</c> writes an exception's type and message only, never a stack. This file
/// is the one place the stack lands. The writer never throws — a crash handler that throws hides
/// the crash — and every sentence is pinned.</para>
/// </summary>
public static class CrashReport
{
    /// <summary>The file's name under the settings directory.</summary>
    public const string FileName = "crash.log";

    /// <summary>The diagnostic category of the crash lines.</summary>
    public const string Category = "App";

    /// <summary>
    /// Appends <paramref name="exception"/> (its type, message, stack and inner exceptions, as
    /// <see cref="Exception.ToString"/> gives them) under a dated header to <see cref="FileName"/> in
    /// <paramref name="directory"/>, creating both. Returns the file's path, or null when the write
    /// itself failed — nothing is thrown.
    /// </summary>
    public static string? Write(string directory, Exception exception, DateTime utcNow)
    {
        // The hide now, or the exit's end of a private conversation that hid (2026-11-26, the review's #34): a crash after that end keeps no message either.
        return Write(directory, exception, utcNow, PrivateMode.HideCrash);
    }

    /// <summary>
    /// The crash's <c>--log</c> line (<see cref="LogLine"/>, the exception attached), with the log's hide held on around it while
    /// <see cref="PrivateMode.HidAtExit"/> is set (2026-11-26): the exit let the hide go, and the exception's message can quote the conversation.
    /// </summary>
    public static void LogCrash(string? path, Exception exception)
    {
        bool hold = PrivateMode.HidAtExit && !PrivateMode.HideLog;
        if (hold)
        {
            PrivateMode.HideLog = true;
        }

        try
        {
            DiagnosticLog.Error(Category, LogLine(path), exception);
        }
        finally
        {
            if (hold)
            {
                PrivateMode.HideLog = false;
            }
        }
    }

    /// <summary><see cref="Write(string, Exception, DateTime)"/> with the hide switch given: under <paramref name="hide"/> the types and stacks alone, no message (2026-11-25).</summary>
    public static string? Write(string directory, Exception exception, DateTime utcNow, bool hide)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName);
            File.AppendAllText(path, Entry(exception, utcNow, hide), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One entry of the file: <c>--- yyyy-MM-dd HH:mm:ss.fff crash ---</c>, the exception, a blank line. Invariant. Pinned.</summary>
    public static string Entry(Exception exception, DateTime utcNow, bool hide = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return string.Create(CultureInfo.InvariantCulture, $"--- {utcNow:yyyy-MM-dd HH:mm:ss.fff} crash ---{Environment.NewLine}{(hide ? Stacks(exception) : exception.ToString())}{Environment.NewLine}{Environment.NewLine}");
    }

    /// <summary>
    /// The exception as <see cref="Exception.ToString"/> gives it without a message (2026-11-25, a private conversation under <c>hide</c>): each type, the
    /// inner ones after <c> ---&gt; </c>, then the stacks. A message can quote what the conversation said; a type and a stack can't. An
    /// <see cref="AggregateException"/>'s every inner exception is walked, depth first, each after <c> ---&gt; (Inner Exception #n) </c> as
    /// <see cref="Exception.ToString"/> marks them (2026-11-26, the review's #33: only the first was kept), 32 at most.
    /// </summary>
    public static string Stacks(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var chain = new List<(string Mark, Exception Error)>();
        Walk(exception, "", chain);
        var sb = new StringBuilder();
        foreach (var (mark, error) in chain)
        {
            sb.Append(mark).Append(error.GetType().FullName);
        }

        foreach (var (_, error) in chain.AsEnumerable().Reverse())
        {
            if (error.StackTrace is { Length: > 0 } stack)
            {
                sb.Append(Environment.NewLine).Append(stack);
            }
        }

        return sb.ToString();
    }

    private static void Walk(Exception error, string mark, List<(string Mark, Exception Error)> chain)
    {
        if (chain.Count >= 32)
        {
            return;
        }

        chain.Add((mark, error));
        if (error is AggregateException aggregate)
        {
            for (int i = 0; i < aggregate.InnerExceptions.Count; i++)
            {
                Walk(aggregate.InnerExceptions[i], string.Create(CultureInfo.InvariantCulture, $" ---> (Inner Exception #{i}) "), chain);
            }
        }
        else if (error.InnerException is { } inner)
        {
            Walk(inner, " ---> ", chain);
        }
    }

    /// <summary>The line printed on the way out. Pinned.</summary>
    public static string Notice(string? path, Exception exception) => Notice(path, exception, PrivateMode.HideCrash);

    /// <summary>The line printed on the way out, its message left out under <paramref name="hide"/> (2026-11-25). Pinned.</summary>
    public static string Notice(string? path, Exception exception, bool hide)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string where = path is null ? "Details could not be written." : $"Details in {path}.";
        return hide ? $"NeonSidekick crashed: {exception.GetType().Name}. {where}" : $"NeonSidekick crashed: {exception.GetType().Name}: {exception.Message}. {where}";
    }

    /// <summary>The <c>--log</c> line pointing at the file. Pinned.</summary>
    public static string LogLine(string? path) => path is null ? "Crashed; the crash report could not be written." : $"Crashed; details in {path}";
}
