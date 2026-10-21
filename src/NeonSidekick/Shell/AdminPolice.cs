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

    /// <summary>What names Active Directory or the search node on a line (<see cref="ShellText.MacDirectoryNodePattern"/>).</summary>
    private static readonly Regex MacDirectoryNode = new(ShellText.MacDirectoryNodePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A Mac directory tool run, with the rest of its command: by any path, never a lookup of it (<c>which dscl</c>, <c>man dscl</c>, <c>command -v dscl</c>).</summary>
    private static readonly Regex MacDirectoryProgram = new(
        @"(?<![\w.-])(?<!\b(?:which|man|type|whereis|apropos|info)\s+)(?<!(?:^|\s)-[vV]\s+)(?<name>dscl|dsconfigad|dseditgroup)(?![\w.-])(?<rest>[^\r\n;&|]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>One shell word: quoted runs, escaped characters and plain ones glued together (<c>/Active\ Directory/"LAB"</c> is one).</summary>
    private static readonly Regex ShellWord = new(@"(?:""(?:[^""\\]|\\.)*""?|'[^']*'?|\\.|[^\s""'\\])+", RegexOptions.CultureInvariant);

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

            if (OperatingSystem.IsMacOS() && MacDirectoryChange(text) is { } macDirectory)
            {
                return (DirectoryFamily, macDirectory);   // dscl, dsconfigad and dseditgroup (2026-10-18; read as commands since 2026-10-20)
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

    /// <summary>
    /// The first Mac directory tool run in <paramref name="text"/> that can change Active Directory, as written; null when none can (2026-10-20,
    /// the code review: a pattern wanted the node before the verb, so <c>dscl localhost -create "/Active Directory/…"</c>, zsh's
    /// <c>/Active\ Directory</c>, <c>-createpl</c>, <c>-mcxset</c> and an interactive <c>dscl</c> fed <c>create …</c> got past it). Read as a
    /// command: <c>dscl</c>'s options (<c>-u</c>, <c>-P</c>, <c>-f</c> with a value), its datasource and its verb. A <c>dscl</c> whose verb is
    /// none of <see cref="ShellText.DsclReadVerbs"/> or <see cref="ShellText.DsclWriteVerbs"/> reads its commands from its input and is refused;
    /// a write is refused on any datasource but the local one (<c>.</c>, <c>localonly</c>, <c>/Local/…</c>, a <c>-f</c> file), and anywhere on a
    /// line naming a directory node (<see cref="ShellText.MacDirectoryNodePattern"/>, so a variable holding the node too). <c>dsconfigad</c> with
    /// any option but <see cref="ShellText.DsconfigadReadOptions"/>; <c>dseditgroup</c> changing a group (<see cref="ShellText.DseditgroupWriteOperations"/>)
    /// on a node not the local one, or on such a line. Pure, so every machine's tests read it; erring toward refusing, a tripwire still: text
    /// built in pieces gets past it.
    /// </summary>
    public static string? MacDirectoryChange(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool namesNode = MacDirectoryNode.IsMatch(text);
        foreach (Match run in MacDirectoryProgram.Matches(text))
        {
            var words = ShellWord.Matches(run.Groups["rest"].Value).Select(w => Unquote(w.Value)).ToList();
            bool changes = run.Groups["name"].Value.ToLowerInvariant() switch
            {
                "dscl" => DsclChanges(words, namesNode),
                "dsconfigad" => words.Any(w => w.Length > 1 && w[0] == '-' && !ShellText.DsconfigadReadOptions.Contains(w)),
                _ => DseditgroupChanges(words, namesNode),
            };
            if (changes)
            {
                return run.Value.Trim();
            }
        }

        return null;
    }

    private static bool DsclChanges(List<string> words, bool namesNode)
    {
        int i = 0;
        bool file = false;
        while (i < words.Count && words[i].Length > 1 && words[i][0] == '-' && !IsDsclVerb(words[i]))
        {
            file |= words[i] == "-f";
            i += words[i] is "-u" or "-P" or "-f" ? 2 : 1;
        }

        string? source = i < words.Count ? words[i] : null;
        string? verb = i + 1 < words.Count ? words[i + 1].TrimStart('-') : null;
        if (verb is null || !IsDsclVerb(verb))
        {
            return true;   // no command: it runs the ones it reads (a pipe, a redirection, a later process write)
        }

        return ShellText.DsclWriteVerbs.Contains(verb) && (namesNode || !(file || IsLocalNode(source)));
    }

    private static bool IsDsclVerb(string word)
    {
        string verb = word.TrimStart('-');
        return ShellText.DsclWriteVerbs.Contains(verb) || ShellText.DsclReadVerbs.Contains(verb);
    }

    private static bool DseditgroupChanges(List<string> words, bool namesNode)
    {
        int operation = words.IndexOf("-o");
        if (operation < 0 || operation + 1 >= words.Count || !ShellText.DseditgroupWriteOperations.Contains(words[operation + 1]))
        {
            return false;
        }

        int node = words.IndexOf("-n");
        return namesNode || (node >= 0 && (node + 1 >= words.Count || !IsLocalNode(words[node + 1])));
    }

    /// <summary>Whether a datasource or node is the Mac's own: <c>.</c>, <c>localonly</c>, <c>/Local</c> and the nodes under it.</summary>
    private static bool IsLocalNode(string? node) =>
        node is "." or "localonly" || (node is not null && (node.Equals("/Local", StringComparison.OrdinalIgnoreCase) || node.StartsWith("/Local/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>A shell word as the program gets it: quotes off, escapes undone.</summary>
    private static string Unquote(string word)
    {
        var plain = new System.Text.StringBuilder(word.Length);
        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            if (c == '\\' && i + 1 < word.Length)
            {
                plain.Append(word[++i]);
            }
            else if (c is not ('"' or '\''))
            {
                plain.Append(c);
            }
        }

        return plain.ToString();
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
