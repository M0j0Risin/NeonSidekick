using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.EmbeddedLlm;

namespace NeonSidekick.Shell;

/// <summary>One background command's group as its record says: the app that started it and the group's leader (pgid = its pid).</summary>
public sealed record ShellRecord(int OwnerPid, long OwnerStartTicks, int LeaderPid, long LeaderStartTicks);

/// <summary>
/// The records of a Mac's background command groups (2026-10-12, the crash watcher, the user's ask): one small file per group in
/// <c>&lt;home&gt;/shell/running/&lt;pgid&gt;.txt</c>, written by <see cref="ShellGroups.Track"/> as a background command starts and
/// removed before its leader is reaped (<see cref="GroupChild"/>). So a record only ever names a group whose leader is unreaped —
/// alive or a zombie — and whose id therefore cannot have been handed to anyone else while the app lives.
///
/// <para>Two readers. <see cref="ShellGuard"/>, the watcher, at the app's death: every record of its owner, its group killed at once
/// (<see cref="KillOwned"/>) — the zombies are launchd's by then, but a group id is not reused while any member lives, and nothing could
/// have wrapped the pid space in the milliseconds since. And the next start, after the app and its guard died together (<c>pkill -9
/// NeonSidekick</c> takes both): <see cref="Sweep"/> kills a group only when its owner is gone and its leader is still the recorded process
/// (the pid running and started at the recorded time, the <see cref="LlamaRecords"/> rule); a group whose leader is gone may still have
/// members, but its id can no longer be proven ours, so they are named in the log and left alone.</para>
///
/// <para>Lines of text, like <see cref="LlamaRecords"/>' (<c>owner &lt;pid&gt; &lt;ticks&gt;</c>, <c>leader &lt;pid&gt; &lt;ticks&gt;</c>);
/// no executable is kept, since zsh hands its last command its own process (<c>exec</c>) and the leader's program changes. Windows writes
/// none: its kill-on-close job does this job.</para>
/// </summary>
public static class ShellRecords
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    /// <summary>The records' folder under <paramref name="home"/>. Pinned.</summary>
    public static string FolderIn(string home) => Path.Combine(home, "shell", "running");

    /// <summary>The record file of group <paramref name="pgid"/>.</summary>
    public static string PathOf(string folder, int pgid) => Path.Combine(folder, pgid.ToString(CultureInfo.InvariantCulture) + ".txt");

    /// <summary>The record's text. Pure, so the tests pin the format.</summary>
    public static string Format(ShellRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.Create(CultureInfo.InvariantCulture, $"owner {record.OwnerPid} {record.OwnerStartTicks}\nleader {record.LeaderPid} {record.LeaderStartTicks}\n");
    }

    /// <summary>A record read back from <paramref name="text"/>; null for anything that is not one whole record.</summary>
    public static ShellRecord? Parse(string text)
    {
        (int Pid, long Ticks)? owner = null, leader = null;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split(' ');
            if (parts.Length == 3
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
            {
                if (parts[0] == "owner")
                {
                    owner = (pid, ticks);
                }
                else if (parts[0] == "leader")
                {
                    leader = (pid, ticks);
                }
            }
        }

        return owner is { } o && leader is { } l && o.Pid > 0 && l.Pid > 1 ? new ShellRecord(o.Pid, o.Ticks, l.Pid, l.Ticks) : null;
    }

    /// <summary>
    /// Writes the record of group <paramref name="leaderPid"/> started by this app into <paramref name="folder"/>. The file's path, or
    /// null (logged) when it could not be written: the group still runs, killed with its session as before, only unwatched.
    /// <paramref name="startTicks"/> reads a process's start (<see cref="LlamaRecords.StartTicks"/> when null).
    /// </summary>
    public static string? Write(string folder, int leaderPid, Func<int, long?>? startTicks = null)
    {
        startTicks ??= LlamaRecords.StartTicks;
        try
        {
            int owner = Environment.ProcessId;
            if (startTicks(owner) is not { } ownerStart || startTicks(leaderPid) is not { } leaderStart)
            {
                DiagnosticLog.Debug(ShellKinds.Category, "No record of group " + leaderPid.ToString(CultureInfo.InvariantCulture) + ": its start time could not be read");
                return null;
            }

            Directory.CreateDirectory(folder);
            string path = PathOf(folder, leaderPid);
            File.WriteAllText(path, Format(new ShellRecord(owner, ownerStart, leaderPid, leaderStart)));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(ShellKinds.Category, "No record of group " + leaderPid.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
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
            DiagnosticLog.Debug(ShellKinds.Category, "Removing " + path + ": " + ex.Message);
        }
    }

    /// <summary>
    /// The guard's work as its app dies (see the class): every group the records under <paramref name="folder"/> show
    /// <paramref name="ownerPid"/> started is killed through <paramref name="killGroup"/>, and its record removed. The groups killed.
    /// Never throws.
    /// </summary>
    public static IReadOnlyList<int> KillOwned(string folder, int ownerPid, Action<int> killGroup)
    {
        ArgumentNullException.ThrowIfNull(killGroup);
        var killed = new List<int>();
        foreach (var (file, record) in Read(folder))
        {
            if (record?.OwnerPid != ownerPid)
            {
                continue;
            }

            killGroup(record.LeaderPid);
            killed.Add(record.LeaderPid);
            Remove(file);
        }

        return killed;
    }

    /// <summary>
    /// The start's sweep (see the class): for each record whose owner is gone, the group is killed through <paramref name="killGroup"/>
    /// when its leader is still the recorded process, and named in the log when only <paramref name="groupAlive"/> says it may run;
    /// either way the record goes. A live owner's records are left (a second app on the same home). The groups killed. Never throws.
    /// </summary>
    public static IReadOnlyList<int> Sweep(string folder, Func<int, long?> startTicks, Action<int> killGroup, Func<int, bool> groupAlive)
    {
        ArgumentNullException.ThrowIfNull(startTicks);
        ArgumentNullException.ThrowIfNull(killGroup);
        ArgumentNullException.ThrowIfNull(groupAlive);
        var killed = new List<int>();
        foreach (var (file, record) in Read(folder))
        {
            if (record is not null && Same(startTicks(record.OwnerPid), record.OwnerStartTicks))
            {
                continue;   // its app is alive: its group, whoever's it is
            }

            if (record is not null && Same(startTicks(record.LeaderPid), record.LeaderStartTicks))
            {
                DiagnosticLog.Warn(ShellKinds.Category, ShellText.GroupLeftBehind(record.LeaderPid, record.OwnerPid));
                killGroup(record.LeaderPid);
                killed.Add(record.LeaderPid);
            }
            else if (record is not null && groupAlive(record.LeaderPid))
            {
                DiagnosticLog.Warn(ShellKinds.Category, ShellText.GroupUnproven(record.LeaderPid, record.OwnerPid));
            }

            Remove(file);
        }

        return killed;
    }

    /// <summary>Every <c>.txt</c> under <paramref name="folder"/> with its record (null for one that does not parse); one unreadable file never ends the rest.</summary>
    private static List<(string File, ShellRecord? Record)> Read(string folder)
    {
        var records = new List<(string, ShellRecord?)>();
        try
        {
            if (!Directory.Exists(folder))
            {
                return records;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
            {
                try
                {
                    records.Add((file, Parse(File.ReadAllText(file))));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Debug(ShellKinds.Category, "Reading " + file + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(ShellKinds.Category, "Reading " + folder + ": " + ex.Message);
        }

        return records;
    }

    private static bool Same(long? actual, long recorded) =>
        actual is { } ticks && Math.Abs(ticks - recorded) <= StartTolerance.Ticks;
}
