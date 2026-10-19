using System.Text.RegularExpressions;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Settings;

namespace NeonSidekick.Shell;

/// <summary>
/// What the police refused a line with: <paramref name="ModelText"/> the model is told, <paramref name="Shown"/> the user's 👮
/// line — null for an outside path, whose refusal is one sentence for both.
/// </summary>
public sealed record ShellPoliceRefusal(string ModelText, string? Shown);

/// <summary>
/// The police before the gate, in their order (2026-10-09, lifted out of <c>run_command</c> for <c>/cmd</c>, the user's ask: the
/// user's own line passes the same checks): the forbidden strings (<see cref="ForbiddenStrings"/>), the SQLite police
/// (<see cref="SqlitePolice"/>, while the SQLite tools are on), the server-database police (<see cref="ServerDatabasePolice"/>,
/// while a server family's tools are on) and a path outside the working directory (<see cref="PathPolice"/>) — all under the
/// <c>Shell police</c> switch and whatever the policy. Each refusal logs its line; noting it for the gate's refusals is the caller's.
/// </summary>
public static partial class ShellPoliceChecks
{
    /// <summary>The first check <paramref name="command"/> fails, run from <paramref name="workdir"/>; null when it passes them all, or the police are off.</summary>
    public static ShellPoliceRefusal? Judge(CommandRequest request, string command, string workdir, WorkingDirectory files, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workdir);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(effective);
        if (!effective.ShellPolice)
        {
            return null;
        }

        // The forbidden strings first (Shell police forbidden strings, 2026-10-03): the model is told it was refused, never which string.
        if (ForbiddenStrings.Find(command, effective.ShellPoliceForbiddenStrings) is { } forbidden)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.ForbiddenLogLine(request, forbidden));
            return new ShellPoliceRefusal(ShellText.Forbidden, ShellText.ForbiddenShown(forbidden));
        }

        // The line's script files, read once for both database polices and only if one needs them (2026-10-12).
        var scripts = new Lazy<IReadOnlyList<(string Name, string Text)>>(() => SqlitePolice.ScriptFiles(command, workdir, files.Root));
        var databases = files.Databases?.Invoke();
        var servers = files.ServerDatabases?.Invoke();
        var admin = files.AdminFamilies?.Invoke();

        // A script file too big for them to read is refused while any of them is on (later on 2026-10-19, the third shell review): it
        // was passed over unread and so passed every one of them.
        if ((databases is not null || servers is not null || admin is not null) && SqlitePolice.OversizedScript(command, workdir, files.Root) is { } oversized)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.ScriptTooLargeLogLine(request, oversized));
            return new ShellPoliceRefusal(ShellText.ScriptTooLarge(SqlitePolice.MaxScriptBytes), ShellText.ScriptTooLargeShown(oversized));
        }

        // The SQLite police (2026-10-05, the user's call), while the SQLite tools are on: a line, or a script
        // file it runs, that reaches SQLite is refused whatever the policy, so a database is reached only through the sqlite_ tools.
        if (databases is not null && SqlitePolice.Judge(command, scripts, databases) is { } sqlite)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.SqliteLogLine(request, sqlite.Token, sqlite.File));
            return new ShellPoliceRefusal(ShellText.SqlitePoliced, ShellText.SqliteShown(sqlite.Token, sqlite.File));
        }

        // The server-database police (2026-10-05, SQLite's rule mirrored): while a server family's tools are on, a line, or a script
        // file it runs, that reaches one (its clients, its drivers, its connections' hosts) is refused whatever the policy.
        if (servers is not null && ServerDatabasePolice.Judge(command, scripts, servers) is { } server)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.ServerDatabaseLogLine(request, server.Family, server.Token, server.File));
            return new ShellPoliceRefusal(ShellText.ServerDatabasePoliced(ServerDatabasePolice.FamilyOf(servers, server.Family)), ShellText.ServerDatabaseShown(server.Family, server.Token, server.File));
        }

        // The admin police (2026-10-15, the admin-tools plan): while the services, tasks or directory tools are on, a line, or a script file it
        // runs, that changes one of them is refused whatever the policy; the families' tools (their checks, their pane) are the one way.
        if (admin is not null && AdminPolice.Judge(command, scripts, admin) is { } changed)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.AdminLogLine(request, changed.Family, changed.Token, changed.File));
            return new ShellPoliceRefusal(ShellText.AdminPoliced(changed.Family), ShellText.AdminShown(changed.Family, changed.Token, changed.File));
        }

        // A line naming a path outside the working directory (Shell police, 2026-09-22): refused, the pane never asked about it.
        if (PathPolice.Judge(command, files, workdir, isScript: false, request.Kind) is { } outside)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.PolicedLogLine(request, outside));
            return new ShellPoliceRefusal(ShellText.OutsidePath(outside), null);
        }

        return null;
    }

    /// <summary>How many spill files <see cref="Spill"/> keeps; the oldest beyond go when it writes one.</summary>
    public const int SpillKept = 50;

    /// <summary>
    /// The whole output into <c>.shell\&lt;id&gt;.log</c> under <paramref name="root"/> (<c>run_command</c>'s, <c>execute_code</c>'s and
    /// <c>/cmd</c>'s cut output); the relative path, or null when it could not be written (logged). Since 2026-10-12 (the shell
    /// review: the folder grew a file a long run, for good, in what is often the user's repository) it keeps the newest
    /// <see cref="SpillKept"/> and holds a <c>.gitignore</c> of <c>*</c>, so <c>git status</c> never lists it. Since 2026-10-19 (the second
    /// shell review: a repository's own <c>.shell</c> folder lost its older logs and was hidden from git) only the app's own logs
    /// (<see cref="IsSpillName"/>) are counted and deleted, and the <c>.gitignore</c> goes only into a folder holding nothing else.
    /// </summary>
    public static string? Spill(string root, string id, string body)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        string relative = Path.Combine(ShellText.SpillFolderName, id + ".log");
        string folder = Path.Combine(root, ShellText.SpillFolderName);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, id + ".log"), body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DiagnosticLog.Warn(ShellKinds.Category, $"{id}: could not write {relative} ({ex.Message})");
            return null;
        }

        Tidy(folder);
        return relative;
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> is a spill this app writes: <c>run_</c>, <c>proc_</c>, <c>code_</c> or <c>cmd_</c> and six hex
    /// digits (<see cref="ShellRunner.NewId"/>), then <c>.log</c>. Pinned against each prefix.
    /// </summary>
    public static bool IsSpillName(string fileName) => SpillNamePattern().IsMatch(fileName);

    [GeneratedRegex(@"^(?:run|proc|code|cmd)_[0-9a-f]{6}\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex SpillNamePattern();

    /// <summary>The spill folder's <c>.gitignore</c> written when missing and nothing but spills is there, and the spills past <see cref="SpillKept"/> deleted, oldest first; each failure is logged and the rest go on.</summary>
    private static void Tidy(string folder)
    {
        FileInfo[] files;
        bool hasFolders;
        try
        {
            var directory = new DirectoryInfo(folder);
            files = directory.GetFiles();
            hasFolders = directory.EnumerateDirectories().Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DiagnosticLog.Debug(ShellKinds.Category, $"could not tidy {folder} ({ex.Message})");
            return;
        }

        string ignore = Path.Combine(folder, ".gitignore");
        if (!hasFolders && files.All(f => IsSpillName(f.Name)))
        {
            try
            {
                File.WriteAllText(ignore, "*\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                DiagnosticLog.Debug(ShellKinds.Category, $"could not write {ignore} ({ex.Message})");
            }
        }

        foreach (var old in files.Where(f => IsSpillName(f.Name)).OrderByDescending(f => f.LastWriteTimeUtc).Skip(SpillKept))
        {
            try
            {
                old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                DiagnosticLog.Debug(ShellKinds.Category, $"could not delete {old.FullName} ({ex.Message})");
            }
        }
    }
}
