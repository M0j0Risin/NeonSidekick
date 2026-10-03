using System.Text;

namespace NeonSidekick.Shell;

/// <summary>
/// The prefix of a command line, the unit the allow list works in (2026-09-21, the user's call):
/// the program's name — its first token, the folder and an executable's extension stripped, lower
/// case — plus the subcommand for the programs that take one (<see cref="VerbPrograms"/>: <c>git
/// push</c>, <c>dotnet build</c>), so allowing <c>git status</c> never allows <c>git push</c>. A
/// compound line (<c>dotnet build &amp;&amp; rm -rf x</c>) is split on its separators outside quotes and
/// every segment's prefix must be allowed (<see cref="All"/>). Pure; every rule is pinned by
/// <c>CommandPrefixTests</c>. What a command does beyond its first word (a script it runs, a
/// <c>$(…)</c> inside it) is not read: the approval pane shows the whole line for that.
/// </summary>
public static class CommandPrefix
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
    /// The segments of <paramref name="command"/>: the pieces between <c>&amp;&amp;</c>, <c>||</c>,
    /// <c>|</c>, <c>&amp;</c>, <c>;</c> and line breaks outside single or double quotes, trimmed,
    /// the empty ones dropped (a leading PowerShell <c>&amp;</c> call operator leaves one).
    /// </summary>
    public static IReadOnlyList<string> Segments(string command) => JoinedSegments(command).Select(s => s.Segment).ToList();

    /// <summary>
    /// <see cref="Segments"/> with the separator that follows each one (2026-10-03, the shell police's <c>cd</c> rule needs to
    /// know whether the next segment runs where the <c>cd</c> left it): <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>&amp;</c>,
    /// <c>;</c>, <c>\n</c> (a <c>\r</c> too), or empty after the last. An empty segment dropped leaves the join before it as it was.
    /// </summary>
    public static IReadOnlyList<(string Segment, string Join)> JoinedSegments(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var segments = new List<(string, string)>();
        var current = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
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
    /// The prefix of one segment: its program name (<see cref="Program"/>), with the next token
    /// after it when the program is one of <see cref="VerbPrograms"/> and that token is a word
    /// rather than an option (<c>git -C x status</c> reads as <c>git</c>). Empty for a blank segment.
    /// </summary>
    public static string Of(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var tokens = Tokens(segment);
        int first = 0;
        // A leading NAME=value is an environment assignment (bash): the program is the token after it.
        while (first < tokens.Count && IsAssignment(tokens[first]))
        {
            first++;
        }

        if (first >= tokens.Count)
        {
            return "";
        }

        string program = Program(tokens[first]);
        if (program.Length == 0)
        {
            return "";
        }

        if (VerbPrograms.Contains(program) && first + 1 < tokens.Count)
        {
            string verb = tokens[first + 1];
            if (verb.Length > 0 && verb[0] != '-' && verb[0] != '/' && verb[0] != '"' && verb[0] != '\'')
            {
                return program + " " + verb.ToLowerInvariant();
            }
        }

        return program;
    }

    /// <summary>Every distinct prefix of <paramref name="command"/>, in the order its segments come; empty for a blank line.</summary>
    public static IReadOnlyList<string> All(string command)
    {
        var prefixes = new List<string>();
        foreach (string segment in Segments(command))
        {
            string prefix = Of(segment);
            if (prefix.Length > 0 && !prefixes.Contains(prefix, StringComparer.Ordinal))
            {
                prefixes.Add(prefix);
            }
        }

        return prefixes;
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

    /// <summary>The whitespace-separated tokens of a segment, a quoted stretch kept as one token with its quotes.</summary>
    private static List<string> Tokens(string segment)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        foreach (char c in segment)
        {
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
