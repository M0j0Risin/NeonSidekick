using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>One running <c>llama-server</c> as its record says: who started it (the owner app) and which process it is.</summary>
public sealed record LlamaRecord(int OwnerPid, long OwnerStartTicks, int ServerPid, long ServerStartTicks, string Executable);

/// <summary>
/// The records of the embedded servers running on a Mac (2026-10-07, the second of the three layers that keep a
/// <c>llama-server</c> from outliving the app there; the others are the exit signals' kill, <c>SidekickApp.KillEmbeddedAtExit</c>,
/// and <see cref="LlamaGuard"/>): one small file per server in <c>&lt;home&gt;/llama/running/</c> — beside the runtime folders,
/// never inside one, so <see cref="EmbeddedModels.PruneOldRuntimes"/> (which takes only <c>b&lt;digits&gt;-…</c>) leaves it be.
/// A file per server, not one for the app, because a multi-server botchat runs several.
///
/// <para><see cref="Sweep"/> runs as the service is made and before every start, and kills a server only when all of this
/// holds (the user's design review that day): its owner is gone — no process by that pid, or one that started at another
/// time, so a second app on the same home keeps its live server; and the pid is still the server — the same executable, under
/// this <c>llama</c> folder, started at the recorded time, so a pid the system handed to something else since is never
/// touched. It covers what the guard cannot: the guard killed together with the app (<c>pkill -9 NeonSidekick</c> takes
/// both, being the same executable). A dead server's record is simply removed.</para>
///
/// <para>Lines of text (<c>owner &lt;pid&gt; &lt;ticks&gt;</c>, <c>server &lt;pid&gt; &lt;ticks&gt;</c>, <c>executable
/// &lt;path&gt;</c>) rather than JSON: nothing else reads them, and they need no serializer context. Start times are UTC ticks
/// from .NET's own <see cref="Process.StartTime"/> (proc_pidinfo underneath on macOS), compared within a second. Paths are
/// kept and compared resolved (<see cref="Files.RealPath"/>): the kernel reports a process's executable with every link along it
/// resolved, so a home under <c>/tmp</c> or <c>/var</c> (links into <c>/private</c>) would otherwise never match. Windows writes
/// none: its job object does this job.</para>
/// </summary>
public static class LlamaRecords
{
    private const string Category = "EmbeddedLlm";

    /// <summary>The folder name under <c>&lt;home&gt;/llama</c>. Pinned.</summary>
    public const string FolderName = "running";

    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    /// <summary>The records' folder for <paramref name="llamaDirectory"/>.</summary>
    public static string Folder(string llamaDirectory) => Path.Combine(llamaDirectory, FolderName);

    /// <summary>The record file of server <paramref name="serverPid"/>.</summary>
    public static string PathOf(string folder, int serverPid) => Path.Combine(folder, serverPid.ToString(CultureInfo.InvariantCulture) + ".txt");

    /// <summary>The record's text. Pure, so the tests pin the format.</summary>
    public static string Format(LlamaRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.Create(CultureInfo.InvariantCulture, $"owner {record.OwnerPid} {record.OwnerStartTicks}\nserver {record.ServerPid} {record.ServerStartTicks}\nexecutable {record.Executable}\n");
    }

    /// <summary>A record read back from <paramref name="text"/>; null for anything that is not one whole record.</summary>
    public static LlamaRecord? Parse(string text)
    {
        (int Pid, long Ticks)? owner = null, server = null;
        string? executable = null;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("executable ", StringComparison.Ordinal))
            {
                executable = line["executable ".Length..];
                continue;
            }

            var parts = line.Split(' ');
            if (parts.Length == 3
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
            {
                if (parts[0] == "owner")
                {
                    owner = (pid, ticks);
                }
                else if (parts[0] == "server")
                {
                    server = (pid, ticks);
                }
            }
        }

        return owner is { } o && server is { } s && !string.IsNullOrEmpty(executable)
            ? new LlamaRecord(o.Pid, o.Ticks, s.Pid, s.Ticks, executable)
            : null;
    }

    /// <summary>
    /// Writes the record of <paramref name="server"/>, started from <paramref name="executable"/> for the app
    /// <paramref name="ownerPid"/>, into <paramref name="folder"/>. The file's path, or null (logged) when it could not be written
    /// — the server still runs, guarded, only unrecorded.
    /// </summary>
    public static string? Write(string folder, int ownerPid, Process server, string executable)
    {
        ArgumentNullException.ThrowIfNull(server);
        try
        {
            if (StartTicks(ownerPid) is not { } ownerStart || StartTicks(server.Id) is not { } serverStart)
            {
                return null;
            }

            Directory.CreateDirectory(folder);
            string path = PathOf(folder, server.Id);
            File.WriteAllText(path, Format(new LlamaRecord(ownerPid, ownerStart, server.Id, serverStart, Resolved(executable))));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, "No record of llama-server " + server.Id.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>Removes a record; nothing when it is gone already or <paramref name="path"/> is null.</summary>
    public static void Remove(string? path)
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
            DiagnosticLog.Debug(Category, "Removing " + path + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Kills every server the records under <paramref name="llamaDirectory"/> show was left behind by an app that is gone
    /// (see the class), and removes the records of servers that are gone. The servers it killed, by pid. Never throws.
    /// <paramref name="startTicks"/> and <paramref name="executableOf"/> read a process (null when it is not running);
    /// <paramref name="kill"/> kills one — the real ones when null, fakes in the tests.
    /// </summary>
    public static IReadOnlyList<int> Sweep(string llamaDirectory, Func<int, long?>? startTicks = null, Func<int, string?>? executableOf = null, Action<int>? kill = null)
    {
        startTicks ??= StartTicks;
        executableOf ??= ExecutableOf;
        kill ??= Kill;
        var killed = new List<int>();
        string folder = Folder(llamaDirectory);
        try
        {
            if (!Directory.Exists(folder))
            {
                return killed;
            }

            string root = Resolved(llamaDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
            {
                // One record at a time: the creation's sweep and a start's may meet on a file, and one gone or unreadable
                // must not end the rest.
                try
                {
                    var record = Parse(File.ReadAllText(file));
                    if (record is not null && Same(startTicks(record.OwnerPid), record.OwnerStartTicks))
                    {
                        continue;   // its app is alive: its server, whoever's it is
                    }

                    if (record is not null
                        && Same(startTicks(record.ServerPid), record.ServerStartTicks)
                        && record.Executable.StartsWith(root, StringComparison.Ordinal)
                        && executableOf(record.ServerPid) is { } running
                        && string.Equals(Resolved(running), record.Executable, StringComparison.Ordinal))
                    {
                        DiagnosticLog.Warn(Category, EmbeddedLlmText.LeftBehind(record.ServerPid, record.OwnerPid));
                        kill(record.ServerPid);
                        killed.Add(record.ServerPid);
                    }

                    Remove(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Debug(Category, "Sweeping " + file + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, "Sweeping " + folder + ": " + ex.Message);
        }

        return killed;
    }

    /// <summary><paramref name="path"/> made whole and every link along it resolved; as made whole where it cannot be resolved (it is gone, or Windows).</summary>
    private static string Resolved(string path)
    {
        string full = Path.GetFullPath(path);
        return Files.RealPath.Of(full) ?? full;
    }

    private static bool Same(long? actual, long recorded) =>
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

    /// <summary>A running process's executable path; null when there is none by that pid or it cannot be read.</summary>
    public static string? ExecutableOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            DiagnosticLog.Debug(Category, "Killing " + pid.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
        }
    }
}
