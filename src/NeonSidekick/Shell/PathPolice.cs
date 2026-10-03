using System.Text;
using System.Text.RegularExpressions;
using NeonSidekick.Files;

namespace NeonSidekick.Shell;

/// <summary>
/// The outside-paths police (2026-09-22, the user's ask; the setting <c>Shell police outside paths</c>,
/// on by default): reads the text the model sends a shell — a <c>run_command</c> line, an
/// <c>execute_code</c> script, the text <c>process</c> writes to a background process's stdin — and names
/// the first token that points outside the working directory, so the tool can refuse the call before
/// the gate is asked. It is lexical, and says so: it sees the text, not what runs, so a script that
/// computes a path (<c>os.environ["X"]</c>, a registry read) is not seen; it is a guard against the
/// model's ordinary attempts, paired with tool descriptions and rules that no longer say the shell can
/// reach outside. Pure but for <c>exists</c>, an injected "is there a file or folder here" the
/// single-segment rule needs, and <c>linkTarget</c>, an injected "where does this link lead" (the real
/// file system in <see cref="Judge"/>, fakes in <c>PathPoliceTests</c>).
///
/// <para>A command line is cut into its segments first (<see cref="CommandPrefix.Segments"/>: the pieces
/// between <c>&amp;&amp;</c>, <c>|</c>, <c>;</c>…), a script is one piece; each piece is cut into tokens on
/// whitespace, quotes and the shell's other punctuation (<see cref="Delimiters"/>) — never on <c>:</c>,
/// <c>\</c>, <c>/</c>, <c>.</c>, <c>~</c>, <c>$</c> or <c>%</c>, which paths are made of. A quote is a cut,
/// not a bracket: a <c>'</c> in a comment would otherwise swallow the rest of a script, and a quoted
/// path with a space still starts with the piece the rules read (<c>"C:\Program Files\x"</c> is refused
/// as <c>C:\Program</c>) — unless the whole quoted path lies under the root (rule 10). The rules, each pinned:</para>
/// <list type="number">
/// <item>A drive-absolute token (<c>C:\…</c>, <c>C:/…</c>, the separator required so <c>x[a:b]</c> is not one) is outside unless it lies under the root.</item>
/// <item>A UNC token (<c>\\server\share…</c>, <c>//server/share…</c>: a server <em>and</em> a share separator, so a JS <c>//comment</c> is not one) is outside.</item>
/// <item>A bash drive path (<c>/d/Repo/…</c>) reads as <c>D:\Repo\…</c> and takes rule 1, so Git Bash under the root passes.</item>
/// <item>A rooted token (<c>/etc/hosts</c>, <c>\Windows\x</c>) resolves against the root's drive on Windows, so it is an escape in every shell and interpreter: two or more segments are outside; a bare <c>/</c> or <c>\</c> is outside as a command's first argument (<c>cd /</c>, <c>ls -la /</c>, <c>cd \</c>: the drive root; options are <c>-x</c> and one-letter <c>/x</c> switches) and nothing elsewhere (<c>-replace '\\', '/'</c>, division); a single segment (<c>/s</c>, <c>/MIR</c>, <c>/t:Build</c> — but also <c>/Users</c>) is outside only when something by that name exists at the drive's root, since a switch names nothing on the disk. <c>//…</c> and <c>/*</c> are comments, <c>\\…</c> without a share (<c>\\d+</c>) and a token of separators alone are nothing, and in a script a backslash-rooted token is not read at all — <c>"\t\n"</c> and <c>\d+\s</c> are escapes and regexes far more often than paths.</item>
/// <item>A token with a <c>..</c> segment (<c>../x</c>, <c>sub\..\..\x</c>; <c>...</c> and <c>1..10</c> are not one) resolves against the folder the text runs in and is outside unless it lands under the root.</item>
/// <item><c>~</c>, <c>~/…</c> and <c>~\…</c> are the home folder (<c>~x</c> is a bitwise not).</item>
/// <item>A folder variable anywhere in the text — <see cref="FolderVariables"/> as <c>%NAME%</c>, <c>$env:NAME</c>, <c>${env:NAME}</c>, <c>$NAME</c> or <c>${NAME}</c> — and a runtime call that means the same (<see cref="FolderCalls"/>: <c>Path.home(</c>, <c>expanduser(</c>, <c>GetFolderPath(</c>…) is outside; the app cannot see where it points, and every one of them points away from the root. Any other variable (<c>%PATH%</c>, <c>$env:CI</c>) passes: the user's call, folder variables only.</item>
/// <item>A path inside a token (2026-10-03, the user's pick): a drive-absolute path after a character that is not a letter or a digit (<c>-out:C:\x</c>, <c>@C:\x\args.rsp</c>, <c>FileSystem::C:\x</c>), a <c>\\server\share</c> the same way in a command line, a <c>..</c> after <c>:</c> or <c>@</c> (<c>-o:..\x</c>), and a <c>file:</c> URL (<c>file:///C:/x</c>, <c>file://server/share/x</c>) are judged as the path they hold, so <c>/out:</c> a folder under the root passes where the rooted rule refused it until that day.</item>
/// <item>A bare drive in a command line (2026-10-03): <c>C:</c> on its own (<c>C: &amp;&amp; dir</c>, <c>cd /d C:</c>, <c>Set-Location E:</c>) moves to that drive and is outside unless it is the root's; a stray <c>a:</c> argument is refused with it, the price.</item>
/// <item>A bare <c>cd</c> (2026-10-03): <see cref="BareCdCommands"/> alone in a command line's segment go to the home folder in PowerShell 7 and bash; in cmd, where it only prints the folder, it passes.</item>
/// <item>A quoted path with a space (2026-10-03): a <c>"…"</c> or <c>'…'</c> whose text is a path (absolute, or a first word with a separator) and lies under the root as a whole is read as one token, so a working directory with a space in its name passes; one outside is cut as before and named by its first piece, and a later word a rule would read on its own (a colon, rooted, <c>~</c>, <c>..</c>) keeps the cut.</item>
/// <item>A <c>cd</c> earlier in the line (2026-10-03): after a segment that is one of <see cref="CdCommands"/> to a folder under the root, the segments after it resolve their relative paths from there, so <c>cd sub &amp;&amp; type ..\x</c> is the root's <c>x</c>. Text written to a background shell across calls is not followed: each write resolves from where the process started.</item>
/// <item>A link (2026-10-03): a path is under the root only if no junction or symlink on its way leads outside (<see cref="WorkingDirectory.LinkEscape"/>, the file tools' rule too), so <c>type link\x</c> with <c>link → C:\</c> is outside.</item>
/// </list>
/// A URL never trips a rule: <c>https://host/path</c> starts with its scheme, not a separator, and the letter before its colon is no drive.
/// </summary>
public static partial class PathPolice
{
    /// <summary>The characters that cut the text into tokens, beside whitespace: quotes and the punctuation a shell or a script puts around a path.</summary>
    public const string Delimiters = "\"'`(),;=<>|&[]{}";

    /// <summary>The punctuation trimmed off a token's end (<c>C:\x;</c>); a single sentence-ending dot goes too (<c>C:\x.</c>), the dots of <c>..</c> never, nor the colon of a bare drive (<c>C:</c>).</summary>
    private const string TrailingPunctuation = ",:;?!";

    /// <summary>
    /// The environment variables that name a folder away from the working directory, matched without
    /// case in every spelling a shell or a script reads them by. <c>PROGRAMFILES(X86)</c> is spelled with
    /// its parentheses only in <c>%…%</c>; the other spellings cannot carry them.
    /// </summary>
    public static readonly IReadOnlyList<string> FolderVariables =
    [
        "USERPROFILE", "HOMEPATH", "HOMEDRIVE", "HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR",
        "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432", "PROGRAMDATA", "ALLUSERSPROFILE", "PUBLIC", "ONEDRIVE",
        "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR",
    ];

    /// <summary>The calls that read a folder variable for a script (<c>Path.home()</c>, <c>os.path.expanduser</c>, <c>[Environment]::GetFolderPath</c>, <c>tempfile.gettempdir()</c>…), matched without case.</summary>
    public static readonly IReadOnlyList<string> FolderCalls =
    [
        "expanduser(", "Path.home(", "homedir(", "tmpdir(", "gettempdir(", "GetFolderPath(", "GetTempPath(",
    ];

    /// <summary>The commands that change folder (rule 11), matched without case: the cmd, bash and PowerShell spellings.</summary>
    public static readonly IReadOnlyList<string> CdCommands = ["cd", "chdir", "pushd", "Set-Location", "sl", "Push-Location"];

    /// <summary>The ones of <see cref="CdCommands"/> that go home with nothing after them (rule 9): PowerShell 7's <c>Set-Location</c> and its aliases, bash's <c>cd</c>.</summary>
    public static readonly IReadOnlyList<string> BareCdCommands = ["cd", "chdir", "Set-Location", "sl"];

    // What stands for a space inside a quoted path under the root (rule 10): not whitespace and not a delimiter, so the path is one token; never in real text.
    private const char Joiner = '\u0001';

    // %NAME% | $env:NAME | ${env:NAME} | $NAME | ${NAME}, the name not continued by a word character ($HOMEPAGE is not $HOME).
    [GeneratedRegex(@"(?:%(?:USERPROFILE|HOMEPATH|HOMEDRIVE|HOME|APPDATA|LOCALAPPDATA|TEMP|TMP|TMPDIR|PROGRAMFILES|PROGRAMFILES\(X86\)|PROGRAMW6432|PROGRAMDATA|ALLUSERSPROFILE|PUBLIC|ONEDRIVE|SYSTEMROOT|SYSTEMDRIVE|WINDIR)%)|(?:\$\{?(?:env:)?(?:USERPROFILE|HOMEPATH|HOMEDRIVE|HOME|APPDATA|LOCALAPPDATA|TEMP|TMP|TMPDIR|PROGRAMFILES|PROGRAMW6432|PROGRAMDATA|ALLUSERSPROFILE|PUBLIC|ONEDRIVE|SYSTEMROOT|SYSTEMDRIVE|WINDIR)(?![A-Za-z0-9_])\}?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FolderVariablePattern();

    // \\server\share or //server/share: two separators, a server, a separator — the share is what makes it a path.
    [GeneratedRegex(@"^(?:\\\\|//)[^\\/\s]+[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex UncPattern();

    /// <summary>
    /// The first token of <paramref name="text"/> that names a path outside <paramref name="root"/>, or
    /// null when every path stays under it. <paramref name="baseFolder"/> is the folder a relative path
    /// resolves against (the command's workdir; the root for a script or a process); <paramref name="isScript"/>
    /// reads the text as one piece and leaves backslash-rooted tokens alone; <paramref name="exists"/>
    /// answers whether a full path names a file or a folder (the single-segment rule); <paramref name="linkTarget"/>
    /// answers where a link leads (a full path), null for anything that is not one (rule 12); <paramref name="bareCdGoesHome"/>
    /// is false for cmd, where a bare <c>cd</c> only prints the folder (rule 9's <c>cd</c>).
    /// </summary>
    public static string? FirstOutside(string text, string root, string baseFolder, bool isScript, Func<string, bool> exists, Func<string, string?> linkTarget, bool bareCdGoesHome = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(baseFolder);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(linkTarget);
        var variable = FolderVariablePattern().Match(text);
        if (variable.Success)
        {
            return variable.Value;
        }

        foreach (string call in FolderCalls)
        {
            if (text.Contains(call, StringComparison.OrdinalIgnoreCase))
            {
                return call;
            }
        }

        string folder = baseFolder;
        foreach (string piece in isScript ? [text] : CommandPrefix.Segments(text))
        {
            var tokens = Tokens(JoinQuotedInside(piece, root, folder, isScript, exists, linkTarget)).Select(Unjoin).ToList();
            if (!isScript && bareCdGoesHome && tokens.Count == 1 && IsOneOf(tokens[0], BareCdCommands))
            {
                return tokens[0];
            }

            int firstArgument = FirstArgument(tokens);
            for (int i = 0; i < tokens.Count; i++)
            {
                if (IsOutside(tokens[i], root, folder, isScript, firstArgument: !isScript && i == firstArgument, exists, linkTarget))
                {
                    return tokens[i];
                }
            }

            if (!isScript && CdTarget(tokens, root, folder, linkTarget) is { } moved)
            {
                folder = moved;
            }
        }

        return null;
    }

    /// <summary>The app's entry: <see cref="FirstOutside"/> over the sandbox's root with the real file system answering <c>exists</c> and <c>linkTarget</c>; <paramref name="shell"/> is the shell's name (<see cref="ShellKinds.Name"/>), cmd's bare <c>cd</c> printing rather than going home.</summary>
    public static string? Judge(string text, WorkingDirectory files, string baseFolder, bool isScript, string? shell = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        bool bareCdGoesHome = !string.Equals(shell, ShellKinds.Name(ShellKind.Cmd), StringComparison.OrdinalIgnoreCase);
        return FirstOutside(text, files.Root, baseFolder, isScript, static path => Directory.Exists(path) || File.Exists(path), WorkingDirectory.RealLinkTarget, bareCdGoesHome);
    }

    /// <summary>
    /// Whether one token, as cut by <see cref="Tokens"/>, names a path outside the root (rules 1 to 6, 8, 9's drive and 12);
    /// <paramref name="firstArgument"/> says it is a command's first argument after its options, where a
    /// bare <c>/</c> or <c>\</c> is the drive root.
    /// </summary>
    public static bool IsOutside(string token, string root, string baseFolder, bool isScript, bool firstArgument, Func<string, bool> exists, Func<string, string?> linkTarget)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(baseFolder);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(linkTarget);
        if (token.Length == 0 || IsDevice(token))
        {
            return false;
        }

        // 6. The home folder.
        if (token == "~" || token.StartsWith("~/", StringComparison.Ordinal) || token.StartsWith("~\\", StringComparison.Ordinal))
        {
            return true;
        }

        // 9. A bare drive in a command line: that drive's own folder, the root's drive alone staying home.
        if (!isScript && token.Length == 2 && char.IsAsciiLetter(token[0]) && token[1] == ':')
        {
            return !(root.Length >= 2 && root[1] == ':' && char.ToUpperInvariant(root[0]) == char.ToUpperInvariant(token[0]));
        }

        // 1. A drive-absolute path: under the root or not.
        if (IsDriveAbsolute(token))
        {
            return !Under(root, Collapse(token), linkTarget);
        }

        // 8. A file: URL is the path it holds (file:///C:/x, file:///etc/x, file://server/share/x).
        if (token.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = token[5..];
            if (rest.StartsWith("///", StringComparison.Ordinal))
            {
                rest = rest[2..];
                if (IsDriveAbsolute(rest.AsSpan(1)))
                {
                    rest = rest[1..];
                }
            }

            return rest.Length > 0 && IsOutside(rest, root, baseFolder, isScript: false, firstArgument: false, exists, linkTarget);
        }

        // 8. A path inside the token (-out:C:\x, @C:\x, FileSystem::C:\x, -o:..\x): judged as itself, the switch around it ignored.
        if (EmbeddedStart(token, isScript) is var at && at > 0)
        {
            return IsOutside(token[at..], root, baseFolder, isScript, firstArgument: false, exists, linkTarget);
        }

        // In a script a token that opens with a backslash is an escape ("\t\n") or a regex (\d+\s) far more often than a path.
        if (token[0] == '\\' && isScript)
        {
            return false;
        }

        // 2. A UNC path is never under a local root; a UNC root would be spelled the same, so the judge still runs.
        if (UncPattern().IsMatch(token))
        {
            return !Under(root, token, linkTarget);
        }

        if (token[0] == '/' || token[0] == '\\')
        {
            // 3. Git Bash's drive form.
            if (token.Length >= 3 && token[0] == '/' && char.IsAsciiLetter(token[1]) && token[2] == '/')
            {
                return !Under(root, char.ToUpperInvariant(token[1]) + ":\\" + Collapse(token[3..]), linkTarget);
            }

            // Comments, not paths: // and /*; two backslashes without a share (\\d+, a regex) are not a UNC path either.
            if (token.StartsWith("//", StringComparison.Ordinal) || token.StartsWith("/*", StringComparison.Ordinal) || token.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return false;
            }

            // 4. Rooted. Separators alone: the drive root as a command's first argument, nothing elsewhere.
            if (token.AsSpan().IndexOfAnyExcept(Separators) < 0)
            {
                return firstArgument && (token == "/" || token == "\\");
            }

            string drive = Path.GetPathRoot(root) ?? root;
            if (token.IndexOfAny(Separators, 1) >= 0)
            {
                return !Under(root, Path.Combine(drive, Collapse(token)[1..]), linkTarget);
            }

            // One segment: a switch (dir /s, msbuild /t:Build) names nothing on the disk; /Users does.
            return Full(Path.Combine(drive, token[1..])) is { } rooted && !Under(root, rooted, linkTarget) && exists(rooted);
        }

        // 5. A .. segment: resolved from where the text runs.
        if (HasParentSegment(token))
        {
            return !Under(root, Path.Combine(baseFolder, Collapse(token)), linkTarget);
        }

        // 12. A plain relative name is under the root by its spelling; a link on its way may still lead out (dir link, type link\x).
        // Not one with a colon: a:b is a slice or a key far more often than a drive-relative path, and Combine would root it.
        return token.IndexOf(':') < 0 && !Under(root, Path.Combine(baseFolder, Collapse(token)), linkTarget);
    }

    /// <summary>The tokens of <paramref name="text"/>: cut on whitespace and <see cref="Delimiters"/>, trailing punctuation off, the empty ones dropped.</summary>
    public static IReadOnlyList<string> Tokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || Delimiters.Contains(c))
            {
                Flush(tokens, current);
            }
            else
            {
                current.Append(c);
            }
        }

        Flush(tokens, current);
        return tokens;
    }

    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// The devices a redirect names, which are no folder anywhere (2026-10-03): cmd's <c>nul</c>, <c>con</c>, <c>prn</c>, <c>aux</c>,
    /// <c>com1</c>–<c>com9</c>, <c>lpt1</c>–<c>lpt9</c> (with any extension, as Windows reads them), and bash's <c>/dev/null</c>,
    /// <c>/dev/stdin</c>, <c>/dev/stdout</c>, <c>/dev/stderr</c>, <c>/dev/tty</c>. Without it <c>&gt;nul</c> made full is <c>\\.\nul</c>, outside,
    /// and <c>&gt;/dev/null</c> was refused by the rooted rule from the start. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> Devices =
    [
        "nul", "con", "prn", "aux", "conin$", "conout$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        "/dev/null", "/dev/stdin", "/dev/stdout", "/dev/stderr", "/dev/tty",
    ];

    private static bool IsDevice(string token)
    {
        int dot = token[0] == '/' ? -1 : token.IndexOf('.');
        return IsOneOf(dot > 0 ? token[..dot] : token, Devices);
    }

    private static bool IsOneOf(string token, IReadOnlyList<string> names) =>
        names.Any(name => string.Equals(token, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The index of a command's first argument: the token after its program and its options (<c>-x</c>, <c>--x</c>, a one-letter <c>/x</c>), or -1.</summary>
    private static int FirstArgument(IReadOnlyList<string> tokens)
    {
        for (int i = 1; i < tokens.Count; i++)
        {
            if (!IsOption(tokens[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsOption(string token) =>
        token[0] == '-' || (token.Length == 2 && token[0] == '/' && char.IsAsciiLetter(token[1]));

    /// <summary>
    /// Rule 11: the folder a <see cref="CdCommands"/> segment moves to, full, when it lies under the root; null for any other
    /// segment, a <c>cd</c> to a variable or home (the rules refused those already) or one that cannot be worked out.
    /// </summary>
    private static string? CdTarget(IReadOnlyList<string> tokens, string root, string folder, Func<string, string?> linkTarget)
    {
        if (tokens.Count < 2 || !IsOneOf(tokens[0], CdCommands) || FirstArgument(tokens) is not (> 0 and var index))
        {
            return null;
        }

        string argument = tokens[index];
        string? path = IsDriveAbsolute(argument) ? Collapse(argument)
            : argument.Length >= 3 && argument[0] == '/' && char.IsAsciiLetter(argument[1]) && argument[2] == '/' ? char.ToUpperInvariant(argument[1]) + ":\\" + Collapse(argument[3..])
            : argument[0] is '/' or '\\' or '~' || argument.Contains('$') || argument.Contains('%') || argument.Contains(':') ? null
            : Path.Combine(folder, Collapse(argument));
        return path is not null && Full(path) is { } full && Under(root, full, linkTarget) ? full : null;
    }

    /// <summary>
    /// Rule 10: <paramref name="piece"/> with every quoted path that holds a space and lies under the root joined
    /// into one token (its whitespace swapped for <see cref="Joiner"/>, which <see cref="Unjoin"/> puts back). Only a path
    /// whose later words are plain (no <c>:</c>, none rooted, no <c>~</c>, no <c>..</c>) is joined, so nothing outside can hide in one;
    /// an unpaired quote leaves the rest as it was, cut as before — never a pass the old rules would not give.
    /// </summary>
    private static string JoinQuotedInside(string piece, string root, string folder, bool isScript, Func<string, bool> exists, Func<string, string?> linkTarget)
    {
        if (piece.IndexOfAny(['"', '\'']) < 0)
        {
            return piece;
        }

        char[]? joined = null;
        int at = 0;
        while (at < piece.Length)
        {
            char quote = piece[at];
            if (quote != '"' && quote != '\'')
            {
                at++;
                continue;
            }

            int close = piece.IndexOf(quote, at + 1);
            if (close < 0)
            {
                break;
            }

            string content = piece[(at + 1)..close];
            if (content.Any(char.IsWhiteSpace) && LooksLikePath(content) && PlainAfterFirstWord(content)
                && !IsOutside(content, root, folder, isScript, firstArgument: false, exists, linkTarget))
            {
                joined ??= piece.ToCharArray();
                for (int i = at + 1; i < close; i++)
                {
                    if (char.IsWhiteSpace(joined[i]))
                    {
                        joined[i] = Joiner;
                    }
                }
            }

            at = close + 1;
        }

        return joined is null ? piece : new string(joined);
    }

    private static string Unjoin(string token) => token.Replace(Joiner, ' ');

    // A path, not a sentence: absolute, a file: URL, or a first word with a separator in it ("..\My Project\x").
    private static bool LooksLikePath(string content)
    {
        if (IsDriveAbsolute(content) || UncPattern().IsMatch(content) || content.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string first = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.IndexOfAny(Separators) > 0;
    }

    // The words after the first carry nothing a rule would read on its own: no colon, none rooted or home, no .. segment.
    private static bool PlainAfterFirstWord(string content)
    {
        var words = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Skip(1).All(word => !word.Contains(':') && word[0] is not ('/' or '\\' or '~') && !HasParentSegment(word));
    }

    /// <summary>
    /// Rule 8: where a path inside <paramref name="token"/> starts, or -1 — a drive-absolute path after a character that is
    /// no letter or digit (so <c>HKLM:\</c> and <c>https:</c> are not one), a <c>\\server\share</c> the same way in a command
    /// line, a <c>..</c> after <c>:</c> or <c>@</c>.
    /// </summary>
    private static int EmbeddedStart(string token, bool isScript)
    {
        for (int i = 1; i < token.Length; i++)
        {
            char before = token[i - 1];
            if (char.IsAsciiLetterOrDigit(before))
            {
                continue;
            }

            var rest = token.AsSpan(i);
            if (IsDriveAbsolute(rest)
                || (!isScript && rest.StartsWith(@"\\", StringComparison.Ordinal) && UncPattern().IsMatch(token[i..]))
                || ((before == ':' || before == '@') && rest.StartsWith("..", StringComparison.Ordinal) && (rest.Length == 2 || rest[2] is '/' or '\\')))
            {
                return i;
            }
        }

        return -1;
    }

    private static void Flush(List<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        string token = current.ToString();
        current.Clear();
        int end = token.Length;
        while (end > 1)
        {
            char c = token[end - 1];
            if (c == ':' && end == 2 && char.IsAsciiLetter(token[0]))
            {
                // A bare drive (C:) keeps its colon: rule 9 reads it.
                break;
            }

            if (TrailingPunctuation.Contains(c))
            {
                end--;
            }
            else if (c == '.' && token[end - 2] != '.' && token[end - 2] != '/' && token[end - 2] != '\\')
            {
                // One sentence-ending dot after a name (C:\x.) goes; the dots of .., ..\.. and x... stay.
                end--;
            }
            else
            {
                break;
            }
        }

        tokens.Add(token[..end]);
    }

    private static bool IsDriveAbsolute(ReadOnlySpan<char> token) =>
        token.Length >= 3 && char.IsAsciiLetter(token[0]) && token[1] == ':' && (token[2] == '\\' || token[2] == '/');

    /// <summary>Whether a segment of <paramref name="token"/> is exactly <c>..</c>.</summary>
    private static bool HasParentSegment(string token)
    {
        int at = 0;
        while (true)
        {
            int next = token.IndexOfAny(Separators, at);
            int length = (next < 0 ? token.Length : next) - at;
            if (length == 2 && token[at] == '.' && token[at + 1] == '.')
            {
                return true;
            }

            if (next < 0)
            {
                return false;
            }

            at = next + 1;
        }
    }

    /// <summary>Repeated separators folded to one past the first character, so a Python <c>"D:\\Repo\\x"</c> reads as the path it means; a leading <c>\\</c> (UNC) is kept.</summary>
    private static string Collapse(string token)
    {
        if (token.IndexOf("\\\\", 1, StringComparison.Ordinal) < 0 && token.IndexOf("//", 1, StringComparison.Ordinal) < 0)
        {
            return token;
        }

        var sb = new StringBuilder(token.Length);
        sb.Append(token[0]);
        for (int i = 1; i < token.Length; i++)
        {
            char c = token[i];
            if (i > 1 && (c == '\\' || c == '/') && (sb[^1] == '\\' || sb[^1] == '/'))
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Whether <paramref name="path"/>, made full, lies under <paramref name="root"/> — by its spelling and by every link on its
    /// way (<see cref="WorkingDirectory.LinkEscape"/>, rule 12); a path that cannot be made full is not.
    /// </summary>
    private static bool Under(string root, string path, Func<string, string?> linkTarget) =>
        Full(path) is { } full && WorkingDirectory.IsInside(root, full) && WorkingDirectory.LinkEscape(root, full, linkTarget) is null;

    private static string? Full(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
