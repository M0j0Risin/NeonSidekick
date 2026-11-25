using System.Globalization;

namespace NeonSidekick.Diagnostics;

/// <summary>
/// Appends every <see cref="DiagnosticLog"/> line to a file: <c>--log &lt;path&gt;</c>.
///
/// <para>The TUI forwards only warnings and errors to the transcript, so the Debug and Info lines
/// that explain a voice problem in the field ("Wake recogniser heard: …", "Wake word ignored …")
/// are otherwise invisible. Lines run on whichever thread produced them (an audio callback, the
/// LLM task), so the write is short and serialised by one lock; the first failing write is
/// reported once as a warning (<see cref="LogStoppedWarning"/>, which the transcript shows) and
/// every later line is dropped, because a logger that can take down a turn is worse than a lost
/// line. The stream flushes per line: the file is read while the app is still running. The file
/// opens with <see cref="OpenedLine"/> and closes with <see cref="ClosedLine"/>, both written
/// straight to it under the <c>Log</c> category.</para>
/// </summary>
public sealed class DiagnosticFileSink : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _subscribed;
    private bool _failed;

    private DiagnosticFileSink(StreamWriter writer)
    {
        _writer = writer;
    }

    /// <summary>
    /// Opens <paramref name="path"/> for append (UTF-8, auto-flush), writes a header line and
    /// subscribes. Throws what the file system throws; the caller decides whether to go on.
    /// </summary>
    public static DiagnosticFileSink Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var stream = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        var sink = new DiagnosticFileSink(writer);
        sink.WriteLine(Format(new DiagnosticEvent(DateTime.UtcNow, DiagnosticLevel.Info, Category, OpenedLine, null)));
        DiagnosticLog.Emitted += sink.OnEmitted;
        sink._subscribed = true;
        return sink;
    }

    /// <summary>The category of the sink's own lines.</summary>
    public const string Category = "Log";

    /// <summary>The first line of a run in the file.</summary>
    public const string OpenedLine = "--- log opened ---";

    /// <summary>The last line of a run in the file, written by <see cref="Dispose"/>.</summary>
    public const string ClosedLine = "--- log closed ---";

    /// <summary>The one warning when the file stops taking lines: the transcript shows it, the file cannot. Pinned.</summary>
    public static string LogStoppedWarning(string detail) =>
        $"--log: the file could not be written ({detail}); nothing more is logged to it.";

    /// <summary>One line per event: <c>yyyy-MM-dd HH:mm:ss.fff [Level] Category: Message</c>, the exception (type and message) on the same line. Invariant. Pinned by tests.</summary>
    public static string Format(DiagnosticEvent evt)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{evt.TimestampUtc:yyyy-MM-dd HH:mm:ss.fff} [{evt.Level}] {evt.Category}: {evt.Message}");
        return evt.Exception is { } ex ? line + " (" + ex.GetType().Name + ": " + ex.Message + ")" : line;
    }

    /// <summary>
    /// The file's own rule (2026-11-25, the private-conversations plan): while <see cref="PrivateMode.HideLog"/> is on, every line but an audit line keeps its
    /// time, level and category and reads <see cref="PrivateMode.Placeholder"/>, an exception its type alone — fail-closed, whatever the line said. Here and not in
    /// <see cref="Format"/>: the <c>/log</c> buffer formats with that too, and shows the lines live.
    /// </summary>
    private void OnEmitted(DiagnosticEvent evt) => WriteLine(PrivateMode.HideLog && !evt.Audit ? FormatHidden(evt) : Format(evt));

    /// <summary>A hidden line: <c>yyyy-MM-dd HH:mm:ss.fff [Level] Category: [private]</c>, the exception's type alone. Pinned.</summary>
    public static string FormatHidden(DiagnosticEvent evt)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{evt.TimestampUtc:yyyy-MM-dd HH:mm:ss.fff} [{evt.Level}] {evt.Category}: {PrivateMode.Placeholder}");
        return evt.Exception is { } ex ? line + " (" + ex.GetType().Name + ")" : line;
    }

    private void WriteLine(string line)
    {
        string? failure = null;
        lock (_gate)
        {
            if (_failed)
            {
                return;
            }

            try
            {
                _writer.WriteLine(line);
            }
            catch (Exception ex)
            {
                // Disk full, file deleted underneath us: stop trying, keep the app running.
                _failed = true;
                failure = ex.Message;
            }
        }

        if (failure is not null)
        {
            // Outside the lock, and the sink is failed already, so its own subscriber drops this
            // line: the transcript is the one place it can still be seen.
            DiagnosticLog.Warn(Category, LogStoppedWarning(failure));
        }
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            DiagnosticLog.Emitted -= OnEmitted;
            _subscribed = false;
            WriteLine(Format(new DiagnosticEvent(DateTime.UtcNow, DiagnosticLevel.Info, Category, ClosedLine, null)));
        }

        lock (_gate)
        {
            try
            {
                _writer.Dispose();
            }
            catch
            {
                // Nothing left to do with a writer that cannot close.
            }
        }
    }
}
