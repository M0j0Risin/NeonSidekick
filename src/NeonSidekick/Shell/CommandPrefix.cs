using System.Text;
using System.Text.RegularExpressions;

namespace NeonSidekick.Shell;

/// <summary>
/// The prefix of a command line, the unit the allow list works in (2026-09-21, the user's call):
/// the program's name — its first token, the folder and an executable's extension stripped, lower
/// case — plus the subcommand for the programs that take one (<see cref="VerbPrograms"/>: <c>git
/// push</c>, <c>dotnet build</c>), so allowing <c>git status</c> never allows <c>git push</c>. A
/// compound line (<c>dotnet build &amp;&amp; rm -rf x</c>) is split on its separators outside quotes and
/// every segment's prefix must be allowed (<see cref="All"/>). Pure; every rule is pinned by
/// <c>CommandPrefixTests</c>. What a command does beyond its first word (a script it runs) is not
/// read: the approval pane shows the whole line for that.
///
/// <para>Substitutions are read (2026-10-12, the shell review): until then a <c>$(…)</c> was the line's own text, so with
/// <c>git log</c> allowed, <c>git log $(Remove-Item -Recurse src)</c> ran without the pane — the pane that was to show the whole
/// line is never shown for an allowed prefix. <see cref="All"/> now takes the shell and adds the prefixes of every command a
/// substitution runs, nested ones too: <c>$(…)</c> everywhere but in single quotes; in bash and zsh a backtick pair and
/// <c>&lt;(…)</c>/<c>&gt;(…)</c>; in zsh also <c>=(…)</c> where it opens an argument (the same day, the Mac's twin pass); in PowerShell <c>@(…)</c>, a bare <c>(…)</c> and a script block <c>{…}</c> (not <c>${…}</c>, not
/// <c>@{…}</c>), where a statement that is only a value (<c>$_.Length -gt 1</c>, <c>"x"</c>, <c>42</c>) runs no command and
/// <c>$x = Remove-Item y</c> runs its right side. cmd has none. What cannot be read — a substitution left open, nesting past
/// <see cref="MaxDepth"/>, a command whose name or verb is a substitution's output (bash's <c>$(…) -rf x</c>, PowerShell's
/// <c>&amp; $x</c>) — is <see cref="Unreadable"/>, which no allow list holds. A verb program's options before its verb are
/// skipped on the way to it, with the value of those in <see cref="ValueOptions"/> (the same day): <c>git -C sub push</c> was
/// <c>git</c>, so allowing that line once allowed every git verb.</para>
/// </summary>
public static partial class CommandPrefix
{
    /// <summary>
    /// The programs whose first argument is a verb the prefix keeps: allowing <c>git status</c> is
    /// not allowing <c>git push</c>. Shells and interpreters are not here on purpose: <c>python -c …</c>
    /// allows exactly <c>python</c>, which is what the pane shows. <c>net</c> since 2026-10-03, so
    /// <see cref="NativeRedirect"/> can send <c>net use</c> to <c>unc_shares</c> and leave <c>net start</c>
    /// the shell's — and allowing <c>net view</c> is no longer allowing <c>net user</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> VerbPrograms = new HashSet<string>(StringComparer.Ordinal)
    {
        "git", "dotnet", "npm", "npx", "pnpm", "yarn", "pip", "pip3", "uv", "poetry", "gh", "docker", "podman",
        "cargo", "go", "winget", "choco", "scoop", "kubectl", "az", "aws", "gcloud", "terraform", "make", "net",
    };

    /// <summary>The extensions stripped from a first token: <c>build.ps1</c> reads as <c>build</c>, <c>Foo.EXE</c> as <c>foo</c>.</summary>
    private static readonly string[] ExecutableExtensions = { ".exe", ".cmd", ".bat", ".com", ".ps1", ".sh" };

    /// <summary>
    /// The options a verb program takes its value after, as a separate word, ahead of its verb (2026-10-12): skipped with that word
    /// on the way to the verb, so <c>git -C sub status</c> is <c>git status</c>. Case kept: git's <c>-C</c> and <c>-c</c> both take one.
    /// An option written <c>--name=value</c> is one word and needs no entry. Pinned.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ValueOptions = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["git"] = Set("-C", "-c", "--git-dir", "--work-tree", "--namespace", "--config-env", "--super-prefix"),
        ["docker"] = Set("-H", "--host", "-c", "--context", "--config", "-l", "--log-level", "--tlscacert", "--tlscert", "--tlskey"),
        ["podman"] = Set("-c", "--connection", "--url", "--root", "--runroot", "--log-level", "--identity"),
        ["kubectl"] = Set("-n", "--namespace", "--context", "--cluster", "--user", "--kubeconfig", "-s", "--server", "--token"),
        ["npm"] = Set("--prefix", "-w", "--workspace"),
        ["pnpm"] = Set("-C", "--dir", "--filter", "-F"),
        ["yarn"] = Set("--cwd"),
        ["uv"] = Set("--directory", "--project"),
        ["poetry"] = Set("-C", "--directory", "-P", "--project"),
        ["make"] = Set("-C", "--directory", "-f", "--file", "--makefile"),
        ["aws"] = Set("--profile", "--region", "--output", "--endpoint-url"),
        ["gcloud"] = Set("--project", "--account", "--configuration"),
        ["az"] = Set("--subscription"),
        ["gh"] = Set("-R", "--repo"),
    };

    /// <summary>
    /// The prefix that stands for a command no reading can name (2026-10-12): a substitution left open, nesting past
    /// <see cref="MaxDepth"/>, or a command whose name or verb is a substitution's output. <see cref="CommandAllowList"/> never holds
    /// it and never adds it, so a line carrying it always goes to the pane. Pinned.
    /// </summary>
    public const string Unreadable = "(unreadable substitution)";

    /// <summary>How deep substitutions inside substitutions are followed before the rest reads as <see cref="Unreadable"/>.</summary>
    public const int MaxDepth = 8;

    // What a substitution's span is replaced by in the text around it, so its separators never cut that text's segments; never in real text.
    private const char Hole = '\u0002';

    /// <summary>
    /// The options that let a verb program's run become another program (2026-10-19, the code review found 2026-10-12's option skip had
    /// opened a hole): skipping them on the way to the verb let an allowed <c>git status</c> cover a <c>git -c core.pager=…</c> that runs a
    /// program, a <c>docker -H</c> aimed at another daemon, a <c>make -f</c> of an arbitrary makefile. A line carrying one reads as
    /// <see cref="Unreadable"/> instead, so it always goes to the pane. Checked by the option's name before any <c>=value</c>. Pinned.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> DangerousOptions = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["git"] = Set("-c", "--config-env", "--exec-path"),
        ["docker"] = Set("-H", "--host", "--config"),
        ["podman"] = Set("-c", "--connection", "--url", "--identity"),
        ["kubectl"] = Set("--kubeconfig", "-s", "--server", "--token"),
        ["make"] = Set("-C", "--directory", "-f", "--file", "--makefile"),
    };

    /// <summary>
    /// The environment variables a leading <c>NAME=value</c> can set to redirect what an allowed program runs or where its name resolves
    /// (2026-10-19, the code review): PATH and the dynamic loader's, and the ones git, a shell or an interpreter reads to run another
    /// program. A leading assignment to one of these reads as <see cref="Unreadable"/> rather than being skipped on the way to the program
    /// after it; a benign <c>FOO=1</c> is still skipped. Pinned.
    /// </summary>
    public static readonly IReadOnlySet<string> DangerousEnvVars = new HashSet<string>(StringComparer.Ordinal)
    {
        "PATH", "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH", "DYLD_FRAMEWORK_PATH",
        "GIT_SSH", "GIT_SSH_COMMAND", "GIT_EXTERNAL_DIFF", "GIT_PAGER", "GIT_EDITOR", "GIT_PROXY_COMMAND",
        "GIT_CONFIG", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_COUNT", "GIT_DIR",
        "BASH_ENV", "ENV", "ZDOTDIR", "PYTHONPATH", "PYTHONSTARTUP", "NODE_OPTIONS", "PERL5LIB", "PERL5OPT", "IFS",
    };

    private static IReadOnlySet<string> Set(params string[] options) => new HashSet<string>(options, StringComparer.Ordinal);

    /// <summary>The character that escapes the next one in <paramref name="shell"/>: PowerShell's backtick, cmd's caret, else a backslash. Matches <see cref="Substitutions"/>.</summary>
    private static char EscapeChar(ShellKind shell) => shell switch
    {
        ShellKind.PowerShell => '`',
        ShellKind.Cmd => '^',
        _ => '\\',
    };

    private static string OptionName(string option)
    {
        int eq = option.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? option : option[..eq];
    }

    private static bool IsDangerousAssignment(string token)
    {
        int eq = token.IndexOf('=', StringComparison.Ordinal);
        return eq > 0 && DangerousEnvVars.Contains(token[..eq]);
    }

    /// <summary>
    /// The segments of <paramref name="command"/>: the pieces between <c>&amp;&amp;</c>, <c>||</c>,
    /// <c>|</c>, <c>&amp;</c>, <c>;</c> and line breaks outside single or double quotes, trimmed,
    /// the empty ones dropped (a leading PowerShell <c>&amp;</c> call operator leaves one).
    /// </summary>
    public static IReadOnlyList<string> Segments(string command, ShellKind shell = ShellKind.PowerShell) => JoinedSegments(command, shell).Select(s => s.Segment).ToList();

    /// <summary>
    /// <see cref="Segments"/> with the separator that follows each one (2026-10-03, the shell police's <c>cd</c> rule needs to
    /// know whether the next segment runs where the <c>cd</c> left it): <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>&amp;</c>,
    /// <c>;</c>, <c>\n</c> (a <c>\r</c> too), or empty after the last. An empty segment dropped leaves the join before it as it was.
    /// </summary>
    public static IReadOnlyList<(string Segment, string Join)> JoinedSegments(string command, ShellKind shell = ShellKind.PowerShell)
    {
        ArgumentNullException.ThrowIfNull(command);
        char escape = EscapeChar(shell);
        var segments = new List<(string, string)>();
        var current = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            // An escaped character is literal: never a quote that opens a string, never a separator (bash/zsh \, PowerShell `, cmd ^,
            // which is literal inside double quotes). Mirrors Substitutions, so a desync can no longer hide a separator inside what the
            // segmenter thinks is a string (the code review, 2026-10-19).
            if (c == escape && i + 1 < command.Length && quote != '\'' && (shell != ShellKind.Cmd || quote == '\0'))
            {
                current.Append(c).Append(command[++i]);
                continue;
            }

            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    current.Append(c);
                    break;
                case '&':
                case '|':
                    bool doubled = i + 1 < command.Length && command[i + 1] == c;
                    Flush(segments, current, doubled ? new string(c, 2) : c.ToString());
                    i += doubled ? 1 : 0;
                    break;
                case ';':
                    Flush(segments, current, ";");
                    break;
                case '\n':
                case '\r':
                    Flush(segments, current, "\n");
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        Flush(segments, current, "");
        return segments;
    }

    private static void Flush(List<(string, string)> segments, StringBuilder current, string join)
    {
        string segment = current.ToString().Trim();
        current.Clear();
        if (segment.Length > 0)
        {
            segments.Add((segment, join));
        }
    }

    /// <summary>
    /// The prefix of one segment: its program name (<see cref="Program"/>), with its verb when the program is one of
    /// <see cref="VerbPrograms"/>: the first word after its options (and their <see cref="ValueOptions"/> values), so
    /// <c>git -C x status</c> reads as <c>git status</c> (2026-10-12; <c>git</c> until then). <paramref name="skipOptions"/> false
    /// takes only a verb straight after the program, the old reading: <see cref="NativeRedirect"/>'s, whose tools take no
    /// <c>-C</c>. Empty for a blank segment.
    /// </summary>
    public static string Of(string segment, bool skipOptions = true, ShellKind shell = ShellKind.PowerShell)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var tokens = Tokens(segment, shell);
        int first = 0;
        // A leading NAME=value is an environment assignment (bash/zsh): the program is the token after it. One that sets PATH, the dynamic
        // loader's or git/shell/interpreter run-a-program variables reads as Unreadable instead (2026-10-19, the code review), as does a
        // segment of only assignments — either can redirect what the command after it runs.
        while (first < tokens.Count && IsAssignment(tokens[first]))
        {
            if (IsDangerousAssignment(tokens[first]))
            {
                return Unreadable;
            }

            first++;
        }

        if (first >= tokens.Count)
        {
            return first > 0 ? Unreadable : "";
        }

        string program = Program(tokens[first]);
        if (program.Length == 0)
        {
            return "";
        }

        if (VerbPrograms.Contains(program))
        {
            var values = ValueOptions.GetValueOrDefault(program);
            var dangerous = DangerousOptions.GetValueOrDefault(program);
            for (int i = first + 1; i < tokens.Count; i++)
            {
                string verb = tokens[i];
                if (verb.Length > 1 && verb[0] == '-' && skipOptions)
                {
                    if (dangerous is not null && dangerous.Contains(OptionName(verb)))
                    {
                        // -c / --config-env / -H / --kubeconfig / -f …: the option itself lets the verb run another program, so the verb
                        // is no longer what the line does. The pane shows the whole line (2026-10-19, the code review).
                        return Unreadable;
                    }

                    // An option ahead of the verb (2026-10-12): skipped, and its value with it when it takes one as the next word.
                    i += values is not null && values.Contains(verb) ? 1 : 0;
                    continue;
                }

                if (verb.Length > 0 && (verb[0] == '"' || verb[0] == '\''))
                {
                    // A quoted verb (git "push"): the quotes stripped and read, so a bare `git` never covers it (2026-10-19, the code
                    // review; it used to stop the scan and leave the prefix the bare program). A quoted option-looking word asks.
                    string unquoted = verb.Replace("\"", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal);
                    return unquoted.Length > 0 && unquoted[0] != '-' && unquoted[0] != '/'
                        ? program + " " + unquoted.ToLowerInvariant()
                        : Unreadable;
                }

                if (verb.Length > 0 && verb[0] != '-' && verb[0] != '/')
                {
                    return program + " " + verb.ToLowerInvariant();
                }

                break;
            }
        }

        return program;
    }

    /// <summary>
    /// Every distinct prefix of <paramref name="command"/> as <paramref name="shell"/> runs it: its segments' in the order they come,
    /// then those of the commands its substitutions run (<see cref="Unreadable"/> for one that cannot be read); empty for a blank line.
    /// </summary>
    public static IReadOnlyList<string> All(string command, ShellKind shell = ShellKind.PowerShell)
    {
        ArgumentNullException.ThrowIfNull(command);
        var prefixes = new List<string>();
        Collect(command, shell, depth: 0, prefixes);
        return prefixes;
    }

    private static void Collect(string text, ShellKind shell, int depth, List<string> prefixes)
    {
        if (depth > MaxDepth)
        {
            Add(prefixes, Unreadable);
            return;
        }

        var bodies = new List<string>();
        string outer = Substitutions(text, shell, bodies, out bool unreadable);
        unreadable |= ExpandsCode(text, shell);
        foreach (string segment in Segments(outer, shell))
        {
            if ((depth > 0 && shell == ShellKind.PowerShell ? StatementPrefix(segment) : Of(segment, shell: shell)) is not { Length: > 0 } prefix)
            {
                continue;
            }

            if (prefix.Contains(Hole))
            {
                int space = prefix.IndexOf(' ');
                string program = space < 0 ? prefix : prefix[..space];
                // A name or a verb that is a substitution's output runs in bash and zsh; in PowerShell a (…) glued to a name is its
                // arguments (Remove-Item("x"), [IO.File]::Delete("x")) and a statement that opens with one is a value, but a verb is not.
                if (shell != ShellKind.PowerShell || (space >= 0 && prefix.IndexOf(Hole, space) >= 0))
                {
                    unreadable = true;
                    continue;
                }

                program = program[..program.IndexOf(Hole)];
                if (program.Length == 0)
                {
                    continue;
                }

                prefix = space < 0 ? program : program + prefix[space..];
            }

            Add(prefixes, prefix);
        }

        if (unreadable)
        {
            Add(prefixes, Unreadable);
        }

        foreach (string body in bodies)
        {
            Collect(body, shell, depth + 1, prefixes);
        }
    }

    private static void Add(List<string> prefixes, string prefix)
    {
        if (!prefixes.Contains(prefix, StringComparer.Ordinal))
        {
            prefixes.Add(prefix);
        }
    }

    /// <summary>
    /// A PowerShell statement inside a substitution or a script block: <c>$x = …</c> is its right side's command; any other one
    /// opening with a value (<c>$_.Name -like '*.cs'</c>, <c>"x"</c>, <c>42</c>, <c>-not …</c>) runs none (null); the rest are <see cref="Of"/>'s.
    /// </summary>
    private static string? StatementPrefix(string segment)
    {
        string statement = segment.TrimStart();
        if (statement.Length == 0)
        {
            return null;
        }

        if (statement[0] == '$')
        {
            var assignment = AssignmentPattern().Match(statement);
            if (assignment.Success)
            {
                return Of(statement[assignment.Length..]);
            }

            // A method call on a $ value runs code ($x.Delete(), $items.ForEach({…})): not a plain value, so it reads as Unreadable
            // rather than nothing (2026-10-19, the code review). A property read ($_.Length -gt 1) has no call and stays a value.
            return InvokesMethodPattern().IsMatch(statement) ? Unreadable : null;
        }

        return char.IsAsciiDigit(statement[0]) || statement[0] is '"' or '\'' or '-' or '!' or '@' ? null : Of(statement);
    }

    // A method call on a value: .Name( or ::Name( — the paren is a substitution by the time this runs (replaced by Hole), so match
    // either. A bare property read ($_.Length, $_.Name -like …) has neither and stays a value.
    [GeneratedRegex("(?:\\.|::)[A-Za-z_]\\w*\\s*[(\u0002]", RegexOptions.CultureInvariant)]
    private static partial Regex InvokesMethodPattern();

    // $name = , $a.b += , $x ??= … : an assignment and the space after it; never == or -eq.
    [GeneratedRegex(@"^\$[\w:.\[\]{}]+\s*(?:[-+*/%]|\?\?)?=(?!=)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentPattern();

    /// <summary>
    /// <paramref name="text"/> with every substitution <paramref name="shell"/> would run replaced by <see cref="Hole"/>, its body added
    /// to <paramref name="bodies"/>; <paramref name="unreadable"/> when one is left open, or (PowerShell) a call operator runs a value
    /// (<c>&amp; $x</c>, <c>&amp; (…)</c>). Quotes and escapes are the shell's: nothing in single quotes, only <c>$(…)</c> (and bash's
    /// backticks) in double ones; a comment is passed over.
    /// </summary>
    private static string Substitutions(string text, ShellKind shell, List<string> bodies, out bool unreadable)
    {
        unreadable = false;
        if (shell == ShellKind.Cmd || text.IndexOfAny(['$', '@', '(', '{', '`', '&']) < 0)
        {
            return text;
        }

        bool powerShell = shell == ShellKind.PowerShell;
        char escape = powerShell ? '`' : '\\';
        var outer = new StringBuilder(text.Length);
        char quote = '\0';
        int escapedAt = -1;   // the index of the char an escape just consumed, so a # right after it is not read as a comment
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote == '\'')
            {
                outer.Append(c);
                quote = c == '\'' ? '\0' : quote;
                continue;
            }

            if (c == escape && i + 1 < text.Length)
            {
                outer.Append(c).Append(text[++i]);
                escapedAt = i;
                continue;
            }

            if (!powerShell && quote == '\0' && c == '$' && i + 1 < text.Length && text[i + 1] == '\'')
            {
                // bash/zsh $'…': a backslash escapes inside, so \' does not close it. Scanned here so the real closing ' is found and a
                // $(…) after it is not mis-scanned as running a command (2026-10-19, the code review).
                outer.Append(c).Append('\'');
                i += 2;
                while (i < text.Length)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        outer.Append(text[i]).Append(text[i + 1]);
                        i += 2;
                        continue;
                    }

                    outer.Append(text[i]);
                    if (text[i] == '\'')
                    {
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (quote == '\0' && (c == '"' || c == '\''))
            {
                quote = c;
                outer.Append(c);
                continue;
            }

            if (quote == '"' && c == '"')
            {
                quote = '\0';
                outer.Append(c);
                continue;
            }

            if (quote == '\0' && IsCommentStart(text, i, powerShell, escapedAt, out int commentEnd))
            {
                outer.Append(text, i, commentEnd - i);
                i = commentEnd - 1;
                continue;
            }

            if (powerShell && quote == '\0' && c == '&' && CallsAValue(text, i))
            {
                unreadable = true;
            }

            int start = BodyStart(text, i, quote == '"', shell, out char closer);
            if (start < 0)
            {
                outer.Append(c);
                continue;
            }

            int close = FindClose(text, start, closer, shell);
            if (close < 0)
            {
                unreadable = true;
                outer.Append(text, i, text.Length - i);
                break;
            }

            bodies.Add(text[start..close]);
            outer.Append(Hole);
            i = close;
        }

        return outer.ToString();
    }

    /// <summary>Where the body of a substitution opening at <paramref name="at"/> starts, and the character that closes it; -1 for none.</summary>
    private static int BodyStart(string text, int at, bool inDoubleQuotes, ShellKind shell, out char closer)
    {
        closer = ')';
        char c = text[at];
        char next = at + 1 < text.Length ? text[at + 1] : '\0';
        bool powerShell = shell == ShellKind.PowerShell;
        if (c == '$' && next == '(')
        {
            // bash's $(( … )) is arithmetic; a $( inside it is read as the scan goes on.
            return !powerShell && at + 2 < text.Length && text[at + 2] == '(' ? -1 : at + 2;
        }

        if (!powerShell && c == '`')
        {
            closer = '`';
            return at + 1;
        }

        if (inDoubleQuotes)
        {
            return -1;
        }

        if (!powerShell)
        {
            return ((c == '<' || c == '>') && next == '(') || (shell == ShellKind.Zsh && c == '=' && next == '(' && StartsZshArgument(text, at)) ? at + 2 : -1;
        }

        if (c == '@' && next == '(')
        {
            return at + 2;
        }

        if (c == '(')
        {
            return at + 1;
        }

        char before = at > 0 ? text[at - 1] : '\0';
        if (c == '{' && before != '$' && before != '@')
        {
            closer = '}';
            return at + 1;
        }

        return -1;
    }

    /// <summary>The index of the <paramref name="closer"/> that ends a body starting at <paramref name="from"/>, nesting and quotes kept; -1 when it stays open.</summary>
    private static int FindClose(string text, int from, char closer, ShellKind shell)
    {
        if (closer == '`')
        {
            for (int i = from; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                }
                else if (text[i] == '`')
                {
                    return i;
                }
            }

            return -1;
        }

        char escape = shell == ShellKind.PowerShell ? '`' : '\\';
        char opener = closer == ')' ? '(' : '{';
        int depth = 0;
        char quote = '\0';
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\'' && c == escape)
            {
                i++;
                continue;
            }

            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
            }
            else if (c == opener)
            {
                depth++;
            }
            else if (c == closer)
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
            }
        }

        return -1;
    }

    /// <summary>A comment from <paramref name="at"/>: a <c>#</c> opening a word, to its line's end; PowerShell's <c>&lt;# … #&gt;</c> to its close. A <c>#</c> after an <em>escaped</em> blank (<paramref name="escapedBefore"/>) is part of a word, not a comment (2026-10-19).</summary>
    private static bool IsCommentStart(string text, int at, bool powerShell, int escapedBefore, out int end)
    {
        end = at;
        if (text[at] == '#' && (at == 0 || (char.IsWhiteSpace(text[at - 1]) && at - 1 != escapedBefore)))
        {
            int line = text.IndexOf('\n', at);
            end = line >= 0 ? line : text.Length;
            return true;
        }

        if (powerShell && text[at] == '<' && at + 1 < text.Length && text[at + 1] == '#')
        {
            int close = text.IndexOf("#>", at + 2, StringComparison.Ordinal);
            end = close >= 0 ? close + 2 : text.Length;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether zsh's <c>=(…)</c> at <paramref name="at"/> is a substitution (2026-10-12): everywhere outside quotes but straight after a
    /// name's character or a subscript's close. <c>zshexpn(1)</c> says the start of an argument, but zsh 5.9 on macOS 15 runs it in more
    /// places: after <c>;</c>, a redirection, an assignment's <c>=</c> (<c>x==(…)</c>, <c>x+==(…)</c>, <c>a[1]==(…)</c>) and a parameter
    /// expansion's default (<c>${x:-=(…)}</c>, <c>${x:+=(…)}</c>, <c>${x-=(…)}</c>: the code review that day, which got
    /// <c>git log ${:-=(touch x)}</c> past an allowed <c>git log</c>). Measured not to run, and not read: <c>files=(a b)</c> and
    /// <c>a[1]=(…)</c>, arrays; <c>x=(…)</c> inside an argument, a glob qualifier. Whatever else is read, reading more only asks.
    /// </summary>
    private static bool StartsZshArgument(string text, int at) =>
        at == 0 || !(char.IsAsciiLetterOrDigit(text[at - 1]) || text[at - 1] is '_' or ']');

    /// <summary>
    /// Whether <paramref name="text"/> uses a bash or zsh expansion that runs code, outside single quotes (2026-10-19, the code review):
    /// the <c>@P</c> prompt expansion (<c>${x@P}</c>) and zsh's <c>(e)</c>/<c>(e:…:)</c> eval flag on a parameter or a glob. These run
    /// whatever a variable holds, which <see cref="Substitutions"/> does not follow, so a line using one reads with an extra
    /// <see cref="Unreadable"/> and goes to the pane. Heuristic and erring toward the pane; single-quoted text is left alone.
    /// </summary>
    private static bool ExpandsCode(string text, ShellKind shell)
    {
        if (shell is ShellKind.PowerShell or ShellKind.Cmd)
        {
            return false;
        }

        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote == '\'')
            {
                quote = c == '\'' ? '\0' : quote;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                quote = quote == '"' ? '\0' : '"';
                continue;
            }

            // ${x@P} — the prompt expansion runs the variable's content as a prompt string, which can embed command substitutions.
            if (c == '@' && i + 1 < text.Length && text[i + 1] == 'P' && (i + 2 >= text.Length || !char.IsAsciiLetterOrDigit(text[i + 2])))
            {
                return true;
            }

            // zsh's (e) / (e:…:) eval flag on a parameter (${(e)x}) or a glob (*(e:'code':)) evaluates code.
            if (shell == ShellKind.Zsh && c == '(' && i + 2 < text.Length && text[i + 1] == 'e' && (text[i + 2] == ')' || text[i + 2] == ':'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>PowerShell's call operator on a value (<c>&amp; $x</c>, <c>&amp; (…)</c>): the command it runs is whatever the value holds. Not <c>&amp;&amp;</c>.</summary>
    private static bool CallsAValue(string text, int at)
    {
        if ((at > 0 && text[at - 1] == '&') || (at + 1 < text.Length && text[at + 1] == '&'))
        {
            return false;
        }

        int next = at + 1;
        while (next < text.Length && (text[next] == ' ' || text[next] == '\t'))
        {
            next++;
        }

        return next < text.Length && (text[next] == '$' || text[next] == '(');
    }

    /// <summary>
    /// A token as a program name: the quotes off, the folder off (either separator), one of
    /// <see cref="ExecutableExtensions"/> off, lower case. <c>"C:\Tools\Foo.EXE"</c> is <c>foo</c>, <c>./build.sh</c> is <c>build</c>.
    /// </summary>
    public static string Program(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        string name = token.Trim().Trim('"', '\'');
        int cut = name.LastIndexOfAny(['\\', '/']);
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }

        foreach (string extension in ExecutableExtensions)
        {
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^extension.Length];
                break;
            }
        }

        return name.ToLowerInvariant();
    }

    private static bool IsAssignment(string token)
    {
        int eq = token.IndexOf('=');
        if (eq <= 0 || token[0] == '"' || token[0] == '\'')
        {
            return false;
        }

        for (int i = 0; i < eq; i++)
        {
            char c = token[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The whitespace-separated tokens of a segment, a quoted stretch kept as one token with its quotes; an escaped character is literal (2026-10-19), so an escaped space never splits a token and an escaped quote never opens one.</summary>
    private static List<string> Tokens(string segment, ShellKind shell = ShellKind.PowerShell)
    {
        char escape = EscapeChar(shell);
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            if (c == escape && i + 1 < segment.Length && quote != '\'' && (shell != ShellKind.Cmd || quote == '\0'))
            {
                current.Append(c).Append(segment[++i]);
                continue;
            }

            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
                current.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
