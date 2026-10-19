using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Files;

/// <summary>
/// What a Mac's two kinds of "left behind" records share (2026-10-12, the code review: <see cref="EmbeddedLlm.LlamaRecords"/> and
/// <see cref="Shell.ShellRecords"/> had each their own copy): a process's start time and the one-second rule that says it is the
/// recorded one, the <c>&lt;word&gt; &lt;pid&gt; &lt;ticks&gt;</c> lines both files are made of, a folder's records read one at a time,
/// a record removed, and the guards' wait for the end of their stdin (<see cref="EmbeddedLlm.LlamaGuard"/>, <see cref="Shell.ShellGuard"/>).
/// The two guards stay two processes: the embedded LLM's is its server's parent and pipe, started per server, the shell's is one per run.
/// </summary>
public static class RecordFiles
{
    /// <summary>How far apart a running process's start and the recorded one may be and still be the same process.</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    /// <summary>Whether <paramref name="actual"/> (null: not running) is the start <paramref name="recorded"/>.</summary>
    public static bool SameStart(long? actual, long recorded) =>
        actual is { } ticks && Math.Abs(ticks - recorded) <= StartTolerance.Ticks;

    /// <summary>A running process's start time in UTC ticks; null when there is none by that pid or it cannot be read.</summary>
    public static long? StartTicks(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The <c>&lt;word&gt; &lt;pid&gt; &lt;ticks&gt;</c> lines of <paramref name="text"/> by word, the last of each kept; any other line ignored.</summary>
    public static IReadOnlyDictionary<string, (int Pid, long Ticks)> Pairs(string? text)
    {
        var pairs = new Dictionary<string, (int, long)>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split(' ');
            if (parts.Length == 3
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
            {
                pairs[parts[0]] = (pid, ticks);
            }
        }

        return pairs;
    }

    /// <summary>Every <c>.txt</c> under <paramref name="folder"/> with its record (null for one <paramref name="parse"/> refuses); one unreadable file never ends the rest. Never throws.</summary>
    public static List<(string File, T? Record)> ReadAll<T>(string folder, Func<string, T?> parse, string category) where T : class
    {
        ArgumentNullException.ThrowIfNull(parse);
        var records = new List<(string, T?)>();
        try
        {
            if (!Directory.Exists(folder))
            {
                return records;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
            {
                // One record at a time: two sweeps may meet on a file, and one gone or unreadable must not end the rest.
                try
                {
                    records.Add((file, parse(File.ReadAllText(file))));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Debug(category, "Reading " + file + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(category, "Reading " + folder + ": " + ex.Message);
        }

        return records;
    }

    /// <summary>
    /// Removes a record; nothing when it is gone already or <paramref name="path"/> is null. A failure is a warning (2026-10-19, the second
    /// shell review): the record now outlives its process, and the readers' start rule is all that keeps its pid from naming a stranger.
    /// </summary>
    public static void Remove(string? path, string category)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(category, "Removing " + path + ": " + ex.Message);
        }
    }

    /// <summary>A guard's wait: its stdin read to its end, which the kernel brings when the app that holds the pipe dies, however it dies. Nothing is ever sent.</summary>
    public static Task WaitForEndOfInputAsync(Stream stdin)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        return Task.Run(() =>
        {
            var buffer = new byte[256];
            while (stdin.Read(buffer) > 0)
            {
                // Only the end of the pipe means anything.
            }
        });
    }
}
