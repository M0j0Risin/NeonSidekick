using System.Text.RegularExpressions;
using NeonSidekick.Settings;

namespace NeonSidekick.Shell;

/// <summary>Which admin families the police guards: those whose tools are on (the event logs since 2026-10-23, the admin review's L1).</summary>
public sealed record AdminGuard(bool Services, bool Tasks, bool Directory, bool EventLog = false)
{
    public bool Any => Services || Tasks || Directory || EventLog;
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
/// <remarks>
/// <b>The Windows pass</b> (2026-10-23, the admin review's mechanism 7): before the word lists, the text is also read as the shell would — the
/// escapes cmd and PowerShell remove undone (a caret, a backtick, a line continued with either) and the quotes taken off (T6: the Mac branch
/// unquoted, Windows did not, so <c>s^c st^op</c> passed); a statement that only looks something up (<c>Get-Help</c>, <c>Get-Command</c>,
/// <c>help</c>, <c>man</c>, a search tool) or carries a bare <c>-WhatIf</c> is no change (DA7, T16: judged per statement, so
/// <c>sc stop x | findstr y</c> is still refused by its first); and a PowerShell encoded command (any prefix of <c>-EncodedCommand</c> from
/// <c>-e</c>, and <c>-ec</c>, after <c>-</c> or <c>/</c>) is decoded from its UTF-16LE base64 and judged too, nested up to two deep — one that
/// will not decode, or past 64 KB, is refused while a family is guarded (DA4, T7; the forbidden string <c>-EncodedCommand</c> still refuses the
/// full spelling on every line).
/// </remarks>
public static class AdminPolice
{
    public const string ServicesFamily = "Services";
    public const string TasksFamily = "Tasks";
    public const string DirectoryFamily = "Directory";
    public const string EventLogFamily = "EventLog";

    /// <summary>The "family" of an encoded command the police could not read (DA4).</summary>
    public const string EncodedFamily = "Encoded";

    /// <summary>The longest encoded command's base64 the police decodes (DA4: its own input stays bounded).</summary>
    public const int MaxEncodedChars = 64 * 1024;

    /// <summary>How deep encoded commands inside encoded commands are decoded (DA4).</summary>
    public const int MaxEncodedDepth = 2;

    private static readonly Lazy<IReadOnlyList<Regex>> s_services = new(() => Compile(ShellText.AdminServiceWords));
    private static readonly Lazy<IReadOnlyList<Regex>> s_tasks = new(() => Compile(ShellText.AdminTaskWords));
    private static readonly Lazy<IReadOnlyList<Regex>> s_directory = new(() => Compile(ShellText.AdminDirectoryWords));
    private static readonly Lazy<IReadOnlyList<Regex>> s_eventLog = new(() => Compile(ShellText.AdminEventLogWords));

    /// <summary>A line continued: cmd's caret or PowerShell's backtick at its end.</summary>
    private static readonly Regex Continuation = new(@"[\^`]\r?\n", RegexOptions.CultureInvariant);

    /// <summary>An escape the shell removes: cmd's caret, PowerShell's backtick, before any character.</summary>
    private static readonly Regex Escape = new(@"[\^`](.)", RegexOptions.CultureInvariant);

    /// <summary>Where one statement ends: a line, <c>;</c>, a pipe, <c>&amp;</c>, <c>&amp;&amp;</c>, <c>||</c>.</summary>
    private static readonly Regex StatementBreak = new(@"\r?\n|&&|\|\||[;&|]", RegexOptions.CultureInvariant);

    /// <summary>A bare <c>-WhatIf</c> (or <c>-WhatIf:$true</c>), never <c>-WhatIf:$false</c>.</summary>
    private static readonly Regex WhatIf = new(@"(?<![\w-])-WhatIf(?::\$true)?(?![\w:$-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A PowerShell run with an encoded command: any prefix of <c>-EncodedCommand</c> from <c>-e</c>, or <c>-ec</c>, after <c>-</c> or <c>/</c>.</summary>
    private static readonly Regex EncodedRun = new(
        @"\b(?:powershell|pwsh)(?:\.exe)?\b(?<gap>[^\r\n;&|]*?)(?<![\w-])[-/](?:ec|e(?:n(?:c(?:o(?:d(?:e(?:d(?:c(?:o(?:m(?:m(?:a(?:n(?:d)?)?)?)?)?)?)?)?)?)?)?)?)?)(?![\w-])[\s:]+(?<body>[^\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A PowerShell parameter after which the rest of the line is the command's own text (2026-10-23, the branch review of DP7): a <c>-e</c> past
    /// <c>-Command</c> or <c>-File</c> is another program's flag (<c>pwsh -c docker run -e FOO=bar</c>), never an encoded command.
    /// </summary>
    private static readonly Regex CommandText = new(
        @"(?<![\w-])[-/](?:c(?:o(?:m(?:m(?:a(?:n(?:d)?)?)?)?)?)?|cwa|commandwithargs|f(?:i(?:l(?:e)?)?)?)(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// What makes a statement more than a lookup or a dry run (2026-10-23, the branch review of DP7): a parenthesis or brace, which PowerShell
    /// evaluates in an argument (<c>help (Get-Date)</c> runs Get-Date), so a statement holding one is always judged.
    /// </summary>
    private static readonly Regex Evaluates = new(@"[(){}]", RegexOptions.CultureInvariant);

    /// <summary>A lookup's flags that run another program: rg's preprocessor, man's pager and browser (DP7).</summary>
    private static readonly Regex RunsAProgram = new(@"(?<![\w-])(?:--pre(?:=|(?![\w-]))|--pager\b|--html\b|-[PH](?![\w-]))", RegexOptions.CultureInvariant);

    /// <summary>The commands that only look something up (DA7).</summary>
    private static readonly HashSet<string> Lookups = new(StringComparer.OrdinalIgnoreCase)
    {
        "get-help", "help", "man", "get-command", "gcm", "grep", "egrep", "fgrep", "findstr", "select-string", "sls", "rg",
    };

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
        var guard = new AdminGuard(effective.ServiceTools, effective.TaskTools, effective.DirectoryTools, effective.EventLogTools);
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
        return Find(text, guard, depth: 0);
    }

    private static (string Family, string Token)? Find(string text, AdminGuard guard, int depth)
    {
        // As written, then as the shell reads it; the lookups out of both (mechanism 7).
        string written = WithoutLookups(text);
        if (FindIn(written, guard, macToo: true) is { } found)
        {
            return found;
        }

        string read = WithoutLookups(Unescaped(text));
        if (read != written && FindIn(read, guard, macToo: false) is { } readFound)
        {
            return readFound;
        }

        foreach (Match run in guard.Any ? EncodedRun.Matches(read) : Enumerable.Empty<Match>())
        {
            if (CommandText.IsMatch(run.Groups["gap"].Value))
            {
                continue;   // the command's own text, never PowerShell's flag (DP7)
            }

            string body = run.Groups["body"].Value;
            if (depth >= MaxEncodedDepth || Decode(body) is not { } decoded)
            {
                return (EncodedFamily, run.Value.Length <= 80 ? run.Value.Trim() : run.Value[..80].Trim() + "…");
            }

            if (Find(decoded, guard, depth + 1) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    /// <summary><paramref name="text"/> as cmd and PowerShell read it: continued lines joined, carets and backticks undone, quotes off (T6).</summary>
    public static string Unescaped(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string joined = Continuation.Replace(text, "");
        return Escape.Replace(joined, "$1").Replace("\"", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="text"/> without the statements that change nothing (DA7, T16): a lookup by its command (<see cref="Lookups"/>), or one with
    /// a bare <c>-WhatIf</c>. The breaks between statements kept, so a pipeline stays one line for the patterns that read a line. Since the branch
    /// review of 2026-10-23 (DP7) a statement that evaluates something (<see cref="Evaluates"/>) is always kept, a <c>-WhatIf</c> counts only
    /// before a <c>#</c> comment, and a lookup whose flags run a program (<see cref="RunsAProgram"/>) is kept.
    /// </summary>
    public static string WithoutLookups(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var kept = new System.Text.StringBuilder(text.Length);
        int at = 0;
        foreach (Match split in StatementBreak.Matches(text))
        {
            Keep(text[at..split.Index]);
            kept.Append(split.Value);
            at = split.Index + split.Length;
        }

        Keep(text[at..]);
        return kept.ToString();

        void Keep(string statement)
        {
            bool exempt = !Evaluates.IsMatch(statement)
                && ((IsLookup(statement) && !RunsAProgram.IsMatch(statement)) || WhatIf.IsMatch(BeforeComment(statement)));
            if (!exempt)
            {
                kept.Append(statement);
            }
        }
    }

    /// <summary>A statement up to PowerShell's comment: a <c>#</c> at its start or after a blank (DP7).</summary>
    private static string BeforeComment(string statement)
    {
        for (int i = 0; i < statement.Length; i++)
        {
            if (statement[i] == '#' && (i == 0 || char.IsWhiteSpace(statement[i - 1])))
            {
                return statement[..i];
            }
        }

        return statement;
    }

    private static bool IsLookup(string statement)
    {
        string trimmed = statement.TrimStart(' ', '\t', '&', '.', '(', '@', '$', '{');
        int end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] is not ('(' or ')'))
        {
            end++;
        }

        string command = trimmed[..end].Trim('"', '\'');
        command = command[(command.LastIndexOfAny(['/', '\\']) + 1)..];
        if (command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            command = command[..^4];
        }

        return Lookups.Contains(command);
    }

    /// <summary>An encoded command's text (base64 of UTF-16LE), or null when it is not one or is too long to read (DA4).</summary>
    public static string? Decode(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length == 0 || body.Length > MaxEncodedChars)
        {
            return null;
        }

        var bytes = new byte[body.Length];
        if (!Convert.TryFromBase64String(body, bytes, out int written) || written % 2 != 0)
        {
            return null;
        }

        return System.Text.Encoding.Unicode.GetString(bytes, 0, written);
    }

    /// <summary>One view of the text judged against each guarded family's words; <paramref name="macToo"/> runs the Mac's directory reader.</summary>
    private static (string Family, string Token)? FindIn(string text, AdminGuard guard, bool macToo)
    {
        if (guard.EventLog && First(text, s_eventLog.Value) is { } eventLog)
        {
            return (EventLogFamily, eventLog);
        }

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

            if (macToo && OperatingSystem.IsMacOS() && MacDirectoryChange(text) is { } macDirectory)
            {
                return (DirectoryFamily, macDirectory);   // dscl, dsconfigad and dseditgroup (2026-10-18; read as commands since 2026-10-20)
            }

            // On the line as written only: its quotes keep "Bob Jones" one word.
            foreach (Match m in macToo ? NetDomain.Matches(text) : Enumerable.Empty<Match>())
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
