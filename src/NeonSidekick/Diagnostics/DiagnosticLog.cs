namespace NeonSidekick.Diagnostics;

/// <summary>Severity of a <see cref="DiagnosticEvent"/>, ordered least to most severe.</summary>
public enum DiagnosticLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>One diagnostic line, as it left the code that produced it.</summary>
/// <param name="TimestampUtc">When the line was produced, not when a subscriber saw it.</param>
/// <param name="Level">Severity.</param>
/// <param name="Category">A display facet, not a type: "Startup", "Settings", "Llm", "Voice".</param>
/// <param name="Message">The text, without the bracketed category.</param>
/// <param name="Exception">The cause, when the line came from a catch block.</param>
public readonly record struct DiagnosticEvent(
    DateTime TimestampUtc,
    DiagnosticLevel Level,
    string Category,
    string Message,
    Exception? Exception);

/// <summary>
/// The one place diagnostics leave this codebase. <c>Console.WriteLine</c> is not an option
/// anywhere else: it writes to the stdout the TUI owns.
///
/// <para>The class references nothing but <c>System</c>. The TUI subscribes to
/// <see cref="Emitted"/> and forwards from there; never reverse that direction.</para>
/// </summary>
public static class DiagnosticLog
{
    /// <summary>
    /// Raised for every line. Subscribers run synchronously on whichever thread produced the
    /// line — an audio callback, the LLM task, the UI thread — so a subscriber must not block
    /// and must marshal its own UI work.
    /// </summary>
    public static event Action<DiagnosticEvent>? Emitted;

    /// <summary>
    /// Whether lines are still written to stdout. Defaults to <c>true</c> so every path with no
    /// UI (<c>--headless</c>, <c>--smoke</c>, a plain <c>dotnet run</c> that crashes early) prints
    /// with no opt-in. The TUI shell clears it on start and restores it on exit.
    /// </summary>
    public static bool EchoToConsole { get; set; } = true;

    /// <summary>Emits a line whose console form is <c>[Category] Message</c>.</summary>
    public static void Write(DiagnosticLevel level, string category, string message, Exception? exception = null)
    {
        // A name in a message never sends the terminal a sequence or disguises itself (2026-10-22, review P5); its line breaks stay.
        message = Files.NameScrub.Display(message, keepLineBreaks: true);
        if (EchoToConsole)
        {
            Console.WriteLine($"[{category}] {message}");
        }

        Raise(new DiagnosticEvent(DateTime.UtcNow, level, category, message, exception));
    }

    public static void Trace(string category, string message) => Write(DiagnosticLevel.Trace, category, message);

    public static void Debug(string category, string message) => Write(DiagnosticLevel.Debug, category, message);

    public static void Info(string category, string message) => Write(DiagnosticLevel.Info, category, message);

    public static void Warn(string category, string message, Exception? exception = null) =>
        Write(DiagnosticLevel.Warning, category, message, exception);

    public static void Error(string category, string message, Exception? exception = null) =>
        Write(DiagnosticLevel.Error, category, message, exception);

    /// <summary>
    /// Delivers to subscribers, swallowing whatever they throw.
    ///
    /// <para>A subscriber here is a UI append running on somebody else's thread, in the middle
    /// of work that must not be abandoned: a throw escaping a log call inside the tool loop
    /// would abort the turn between a tool call and the result answering it. The delegate is
    /// snapshotted so an unsubscribe racing the raise cannot null it, and each subscriber is
    /// isolated so one bad handler does not deprive the others. No lock is held across the
    /// callback.</para>
    /// </summary>
    private static void Raise(DiagnosticEvent evt)
    {
        var handlers = Emitted;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<DiagnosticEvent>)handler)(evt);
            }
            catch
            {
                // A logger that can take down a turn is worse than a dropped log line.
            }
        }
    }
}
