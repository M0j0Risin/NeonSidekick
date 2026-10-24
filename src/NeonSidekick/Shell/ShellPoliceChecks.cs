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
    /// <summary>
    /// The first check <paramref name="command"/> fails, run from <paramref name="workdir"/>; null when it passes them all, or the police are off.
    /// <paramref name="missingScriptsPass"/> lets a script that isn't there yet through (<see cref="ScriptChecks"/>).
    /// </summary>
    public static ShellPoliceRefusal? Judge(CommandRequest request, string command, string workdir, WorkingDirectory files, AppSettingsData effective, bool missingScriptsPass = false)
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

        // The line's script files, read once for every police and only if one needs them (2026-10-12).
        var scripts = new Lazy<IReadOnlyList<(string Name, string Text)>>(() => SqlitePolice.ScriptFiles(command, workdir, files.Root));
        var databases = files.Databases?.Invoke();
        var servers = files.ServerDatabases?.Invoke();
        var admin = files.AdminFamilies?.Invoke();
        if (ScriptChecks(request, command, workdir, files, effective, scripts, missingScriptsPass) is { } scripted)
        {
            return scripted;
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

    /// <summary>
    /// What every police reads past the line itself (2026-10-22, review L4): a script the line runs that isn't there yet (the line writes
    /// it, then runs it), one too big to read — refused under the police whatever else is on, since the forbidden strings and the trusted
    /// files read scripts too — the forbidden strings in a script, and the trusted files in the line and its scripts (the fix plan's D8).
    /// Shared with <c>process</c>'s writes. Null when they all pass.
    /// <para>
    /// <paramref name="missingScriptsPass"/> (2026-10-24, the user's report: a Ralph plan whose Step 1 writes <c>hello_world.py</c> and whose
    /// <c>verify:</c> runs it was refused before it started) skips only the missing-script refusal, every other check still read. Only the Ralph
    /// start's pre-check passes true, where nothing runs: each check run asks again without it, after <c>RalphVerifier.NotThereYet</c>.
    /// </para>
    /// </summary>
    public static ShellPoliceRefusal? ScriptChecks(CommandRequest request, string command, string workdir, WorkingDirectory files, AppSettingsData effective, Lazy<IReadOnlyList<(string Name, string Text)>>? scripts = null, bool missingScriptsPass = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(effective);
        if (!effective.ShellPolice)
        {
            return null;
        }

        scripts ??= new Lazy<IReadOnlyList<(string Name, string Text)>>(() => SqlitePolice.ScriptFiles(command, workdir, files.Root));
        if (!missingScriptsPass && SqlitePolice.MissingScripts(command, workdir, files.Root) is [var missing, ..])
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.ScriptMissingLogLine(request, missing));
            return new ShellPoliceRefusal(ShellText.ScriptMissing(missing), null);
        }

        if (SqlitePolice.OversizedScript(command, workdir, files.Root) is { } oversized)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.ScriptTooLargeLogLine(request, oversized));
            return new ShellPoliceRefusal(ShellText.ScriptTooLarge(SqlitePolice.MaxScriptBytes), ShellText.ScriptTooLargeShown(oversized));
        }

        foreach (var (name, text) in scripts.Value)
        {
            if (ForbiddenStrings.Find(text, effective.ShellPoliceForbiddenStrings) is { } forbidden)
            {
                DiagnosticLog.Info(ShellKinds.Category, ShellText.ForbiddenLogLine(request, forbidden));
                return new ShellPoliceRefusal(ShellText.Forbidden, ShellText.ForbiddenShown(forbidden));
            }
        }

        if (TrustedFilesPolice.For(files.Root) is { } trusted)
        {
            if (TrustedFilesPolice.Find(command, trusted) is { } named)
            {
                DiagnosticLog.Info(ShellKinds.Category, ShellText.TrustedLogLine(request, named, null));
                return new ShellPoliceRefusal(ShellText.TrustedPoliced, ShellText.TrustedShown(named, null));
            }

            foreach (var (name, text) in scripts.Value)
            {
                if (TrustedFilesPolice.Find(text, trusted) is { } inScript)
                {
                    DiagnosticLog.Info(ShellKinds.Category, ShellText.TrustedLogLine(request, inScript, name));
                    return new ShellPoliceRefusal(ShellText.TrustedPoliced, ShellText.TrustedShown(inScript, name));
                }
            }
        }

        return null;
    }

    /// <summary>How many spill files <see cref="Spill"/> keeps; the oldest beyond go when it writes one.</summary>
    public const int SpillKept = 50;

    /// <summary>
    /// The whole output into <c>.neon\shell\&lt;id&gt;.log</c> under <paramref name="root"/> (<see cref="ShellText.SpillFolderName"/>; <c>.shell</c> until
    /// 2026-10-21, the user's ask: one app folder in the working directory) (<c>run_command</c>'s, <c>execute_code</c>'s and
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
            // Never through a link that leads out of the working directory (2026-10-22, review L6: a .neon link sent the spill elsewhere).
            if (WorkingDirectory.LinkEscape(root, Path.Combine(folder, id + ".log"), WorkingDirectory.RealLinkTarget) is not null)
            {
                DiagnosticLog.Warn(ShellKinds.Category, $"{id}: {relative} not written: a link on its way leads outside the working directory");
                return null;
            }

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
