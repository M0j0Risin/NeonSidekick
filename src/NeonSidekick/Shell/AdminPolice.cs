using System.Text.RegularExpressions;
using NeonSidekick.Settings;

namespace NeonSidekick.Shell;

/// <summary>Which admin families the police guards: those whose tools are on.</summary>
public sealed record AdminGuard(bool Services, bool Tasks, bool Directory)
{
    public bool Any => Services || Tasks || Directory;
}

/// <summary>
/// The shell police's admin rule (2026-10-15, the admin-tools plan, the user's call: <see cref="ServerDatabasePolice"/>'s shape): while a family's
/// tools are on and <c>Shell police</c> is, a <c>run_command</c> line (and the script files it runs), an <c>execute_code</c> script or a
/// <c>process</c> write that changes a service, a scheduled task or the directory is refused before the gate is asked, whatever the policy —
/// so changes go through the family's tools, whose order of checks and approval pane then hold. Reads pass (<c>sc query</c>, <c>Get-Service</c>,
/// <c>schtasks /query</c>, <c>Get-AD*</c>, <c>net user x /domain</c>). Each family's words are a pinned list (<see cref="ShellText.AdminServiceWords"/>,
/// <see cref="ShellText.AdminTaskWords"/>, <see cref="ShellText.AdminDirectoryWords"/>), each a regular expression found case-blind. A tripwire,
/// like the forbidden strings, not a sandbox: text built in pieces gets past it. Pure but for the script files' reads.
/// </summary>
public static class AdminPolice
{
    public const string ServicesFamily = "Services";
    public const string TasksFamily = "Tasks";
    public const string DirectoryFamily = "Directory";

    private static readonly Lazy<IReadOnlyList<Regex>> s_services = new(() => Compile(ShellText.AdminServiceWords));
    private static readonly Lazy<IReadOnlyList<Regex>> s_tasks = new(() => Compile(ShellText.AdminTaskWords));
    private static readonly Lazy<IReadOnlyList<Regex>> s_directory = new(() => Compile(ShellText.AdminDirectoryWords));
    private static readonly Lazy<IReadOnlyList<Regex>> s_directoryMac = new(() => Compile(ShellText.AdminDirectoryWordsMac));

    /// <summary>A <c>net user|group … /domain</c> line, judged apart: a read unless it changes something.</summary>
    private static readonly Regex NetDomain = new(@"\bnet1?(?:\.exe)?\s+(?<what>user|group)\b(?<rest>[^\r\n;&|]*?)/domain\b(?<tail>[^\r\n;&|]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NetChange = new(@"/(?:add|del|delete|active|expires|times|passwordchg|passwordreq|random|fullname|comment|homedir|scriptpath|profilepath|workstations|usercomment|countrycode)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>One word of a <c>net</c> line: a quoted run, or a run of non-blanks.</summary>
    private static readonly Regex NetWord = new("\"[^\"]*\"|\\S+", RegexOptions.CultureInvariant);

    /// <summary>The guard for <paramref name="effective"/>: the families whose tools are on; null when none is.</summary>
    public static AdminGuard? For(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var guard = new AdminGuard(effective.ServiceTools, effective.TaskTools, effective.DirectoryTools);
        return guard.Any ? guard : null;
    }

    /// <summary>The verdict on a line: what in it changes a guarded family, else what in one of its script files does; null when neither does.</summary>
    public static (string Family, string Token, string? File)? Judge(string command, Lazy<IReadOnlyList<(string Name, string Text)>> scripts, AdminGuard guard)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(guard);
        if (Find(command, guard) is { } found)
        {
            return (found.Family, found.Token, null);
        }

        foreach (var (name, text) in scripts.Value)
        {
            if (Find(text, guard) is { } inScript)
            {
                return (inScript.Family, inScript.Token, name);
            }
        }

        return null;
    }

    /// <summary>The first change in <paramref name="text"/> to a guarded family: its family and the words as written.</summary>
    public static (string Family, string Token)? Find(string text, AdminGuard guard)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(guard);
        if (guard.Services && First(text, s_services.Value) is { } service)
        {
            return (ServicesFamily, service);
        }

        if (guard.Tasks && First(text, s_tasks.Value) is { } task)
        {
            return (TasksFamily, task);
        }

        if (guard.Directory)
        {
            if (First(text, s_directory.Value) is { } directory)
            {
                return (DirectoryFamily, directory);
            }

            if (OperatingSystem.IsMacOS() && First(text, s_directoryMac.Value) is { } macDirectory)
            {
                return (DirectoryFamily, macDirectory);   // dscl and dsconfigad (2026-10-18)
            }

            foreach (Match m in NetDomain.Matches(text))
            {
                // A name and a second word (a user's password, a group's member) is a change; a quoted name with a space is one word.
                string args = m.Groups["rest"].Value + " " + m.Groups["tail"].Value;
                int positional = NetWord.Matches(args).Count(w => !w.Value.StartsWith('/'));
                if (NetChange.IsMatch(args) || positional >= 2)
                {
                    return (DirectoryFamily, m.Value.Trim());
                }
            }
        }

        return null;
    }

    private static string? First(string text, IReadOnlyList<Regex> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.Match(text) is { Success: true } m)
            {
                return m.Value.Trim();
            }
        }

        return null;
    }

    private static IReadOnlyList<Regex> Compile(IReadOnlyList<string> patterns) =>
        patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
}
