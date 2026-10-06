using System.Text;
using System.Text.RegularExpressions;
using NeonSidekick.Files;

namespace NeonSidekick.Shell;

/// <summary>
/// The outside-paths police (2026-09-22, the user's ask; the setting <c>Shell police</c>,
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
/// <item>A bash drive path (<c>/d/Repo/…</c>) reads as <c>D:\Repo\…</c> and takes rule 1, so Git Bash under the root passes; in a bash command line <c>/d</c> alone is the D drive too (2026-10-03, the review: <c>cd /c</c>), and there no one-letter <c>/x</c> is an option.</item>
/// <item>A rooted token (<c>/etc/hosts</c>, <c>\Windows\x</c>) resolves against the root's drive on Windows, so it is an escape in every shell and interpreter: two or more segments are outside; a bare <c>/</c> or <c>\</c> is outside as a command's first argument (<c>cd /</c>, <c>ls -la /</c>, <c>cd \</c>: the drive root; options are <c>-x</c> and one-letter <c>/x</c> switches) and nothing elsewhere (<c>-replace '\\', '/'</c>, division); a single segment (<c>/s</c>, <c>/MIR</c>, <c>/t:Build</c> — but also <c>/Users</c>) is outside only when something by that name exists at the drive's root, since a switch names nothing on the disk — but a <c>cd</c>'s folder argument is a folder, never a switch (<c>cd /etc</c>, 2026-10-03, the review: in Git Bash <c>/etc</c> is its own install's). <c>//…</c> and <c>/*</c> are comments, <c>\\…</c> without a share (<c>\\d+</c>) and a token of separators alone are nothing, and in a script a backslash-rooted token is not read at all — <c>"\t\n"</c> and <c>\d+\s</c> are escapes and regexes far more often than paths.</item>
/// <item>A token with a <c>..</c> segment (<c>../x</c>, <c>sub\..\..\x</c>; <c>...</c> and <c>1..10</c> are not one) resolves against the folder the text runs in and is outside unless it lands under the root.</item>
/// <item><c>~</c>, <c>~/…</c> and <c>~\…</c> are the home folder (<c>~x</c> is a bitwise not).</item>
/// <item>A folder variable anywhere in the text — <see cref="FolderVariables"/> as <c>%NAME%</c>, <c>$env:NAME</c>, <c>${env:NAME}</c>, <c>$NAME</c> or <c>${NAME}</c> — and a runtime call that means the same (<see cref="FolderCalls"/>: <c>Path.home(</c>, <c>expanduser(</c>, <c>GetFolderPath(</c>…) is outside; the app cannot see where it points, and every one of them points away from the root. Any other variable (<c>%PATH%</c>, <c>$env:CI</c>) passes: the user's call, folder variables only.</item>
/// <item>A path inside a token (2026-10-03, the user's pick): a drive-absolute path after a character that is not a letter or a digit (<c>-out:C:\x</c>, <c>@C:\x\args.rsp</c>, <c>FileSystem::C:\x</c>), a <c>\\server\share</c> the same way in a command line, a <c>..</c> after <c>:</c> or <c>@</c> (<c>-o:..\x</c>), any of the three glued to a one-letter switch (<c>-oC:\x</c>, <c>-IC:\x</c>, <c>-I..\x</c>: 2026-10-03, the review), and a <c>file:</c> URL (<c>file:///C:/x</c>, <c>file://server/share/x</c>) are judged as the path they hold, so <c>/out:</c> a folder under the root passes where the rooted rule refused it until that day.</item>
/// <item>A bare drive in a command line (2026-10-03): <c>C:</c> on its own (<c>C: &amp;&amp; dir</c>, <c>cd /d C:</c>, <c>Set-Location E:</c>) moves to that drive and is outside unless it is the root's; a stray <c>a:</c> argument is refused with it, the price.</item>
/// <item>A bare <c>cd</c> (2026-10-03): <see cref="BareCdCommands"/> with no folder argument in a command line's segment — alone, or with only options and redirects (<c>cd -P</c>, <c>cd &gt;/dev/null</c>, <c>Set-Location -PassThru</c>: 2026-10-03, the review) — go to the home folder in PowerShell 7 and bash; in cmd, where it only prints the folder, it passes. <c>cd -</c> is an argument, the previous folder, and not followed.</item>
/// <item>A quoted path with a space (2026-10-03): a <c>"…"</c> or <c>'…'</c> whose text is a path (absolute, or a first word with a separator) and lies under the root as a whole is read as one token, so a working directory with a space in its name passes; one outside is cut as before and named by its first piece, and a later word a rule would read on its own (a colon, rooted, <c>~</c>, <c>..</c>) or that is outside by itself (a link on its way, 2026-10-03, the review: <c>bash -c "./tool.sh link/x"</c> hands the words over apart) keeps the cut.</item>
/// <item>A <c>cd</c> earlier in the line (2026-10-03): after a segment that is one of <see cref="CdCommands"/> to a folder under the root, the segments after it resolve their relative paths from there, so <c>cd sub &amp;&amp; type ..\x</c> is the root's <c>x</c> — only when the next segment surely runs there (<c>&amp;&amp;</c>, <c>;</c>, a line break, cmd's <c>&amp;</c>; no subshell; the folder there). Otherwise, since 2026-10-03 (the review: <c>cd sub | type ..\x</c> runs in the root), a later path must stay under the root from the old folder and the new alike. Text written to a background shell across calls is not followed: each write resolves from where the process started.</item>
/// <item>A link (2026-10-03): a path is under the root only if no junction or symlink on its way leads outside (<see cref="WorkingDirectory.LinkEscape"/>, the file tools' rule too), so <c>type link\x</c> with <c>link → C:\</c> is outside.</item>
/// </list>
/// A URL never trips a rule: <c>https://host/path</c> starts with its scheme, not a separator, and the letter before its colon is no drive.
///
/// <para>Off Windows (2026-10-06, the macOS build) the drive rules have nothing to read: there are no drives, so Git Bash's
/// <c>/d/…</c> (rule 3) is the absolute path it spells and is judged by rule 4 like <c>/etc/hosts</c>, a bare <c>C:</c> (rule 9) is a
/// name, a one-letter <c>/x</c> is a path and never a switch, and a backslash only escapes (a command line's token is read with
/// its backslashes undone, as the shell reads it: <c>\/etc</c> is judged as <c>/etc</c>). Rule 4 resolves a rooted token from <c>/</c>, so every absolute path outside the root is outside and one
/// under it passes. Rule 6 adds <c>~name</c> in a command line, another account's home in every Unix shell; rule 7 adds
/// <c>OLDPWD</c> (<c>cd $OLDPWD</c>, <c>cd -</c>'s folder); the devices add <c>/dev/zero</c>, <c>/dev/random</c> and <c>/dev/urandom</c>.</para>
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
        "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "OLDPWD",
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
    [GeneratedRegex(@"(?:%(?:USERPROFILE|HOMEPATH|HOMEDRIVE|HOME|APPDATA|LOCALAPPDATA|TEMP|TMP|TMPDIR|PROGRAMFILES|PROGRAMFILES\(X86\)|PROGRAMW6432|PROGRAMDATA|ALLUSERSPROFILE|PUBLIC|ONEDRIVE|SYSTEMROOT|SYSTEMDRIVE|WINDIR|OLDPWD)%)|(?:\$\{?(?:env:)?(?:USERPROFILE|HOMEPATH|HOMEDRIVE|HOME|APPDATA|LOCALAPPDATA|TEMP|TMP|TMPDIR|PROGRAMFILES|PROGRAMW6432|PROGRAMDATA|ALLUSERSPROFILE|PUBLIC|ONEDRIVE|SYSTEMROOT|SYSTEMDRIVE|WINDIR|OLDPWD)(?![A-Za-z0-9_])\}?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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
    /// answers where a link leads (a full path), null for anything that is not one (rule 12); <paramref name="shell"/> is the
    /// shell a command line runs in: cmd's bare <c>cd</c> only prints the folder and its <c>&amp;</c> runs the next command in
    /// turn (rules 9 and 11), and Git Bash reads <c>/c</c> as the C drive (rule 3).
    /// </summary>
    public static string? FirstOutside(string text, string root, string baseFolder, bool isScript, Func<string, bool> exists, Func<string, string?> linkTarget, ShellKind shell = ShellKind.PowerShell)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(baseFolder);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(linkTarget);
        var judging = new Judging(root, isScript, shell, exists, linkTarget);
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

        // Every folder the next segment may run in (rule 11): one, until a cd leaves it in doubt whether it moved.
        List<string> folders = [baseFolder];
        foreach (var (piece, join) in isScript ? [(text, "")] : CommandPrefix.JoinedSegments(text))
        {
            string joined = JoinQuotedInside(piece, judging, folders);
            var tokens = Tokens(joined).Select(Unjoin).ToList();
            bool isCd = !isScript && tokens.Count > 0 && IsOneOf(tokens[0], CdCommands);
            string? cdArgument = isCd ? CdArgument(joined, shell) : null;
            if (isCd && cdArgument is null && shell != ShellKind.Cmd && IsOneOf(tokens[0], BareCdCommands))
            {
                return tokens[0];
            }

            int firstArgument = FirstArgument(tokens, shell);
            int cdIndex = cdArgument is null ? -1 : tokens.IndexOf(cdArgument, 1);
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                bool first = !isScript && i == firstArgument;
                if (folders.Any(folder => IsOutside(token, judging, folder, first, folderArgument: i == cdIndex)))
                {
                    return token;
                }
            }

            if (cdArgument is not null)
            {
                folders = Moved(folders, cdArgument, piece, join, judging);
            }
        }

        return null;
    }

    /// <summary>What a judgement holds for every token of one text: the root, the kind of text, the shell, and the disk's two answers.</summary>
    private sealed record Judging(string Root, bool IsScript, ShellKind Shell, Func<string, bool> Exists, Func<string, string?> LinkTarget);

    /// <summary>The app's entry: <see cref="FirstOutside"/> over the sandbox's root with the real file system answering <c>exists</c> and <c>linkTarget</c>; <paramref name="shell"/> is the shell's name (<see cref="ShellKinds.Name"/>), cmd's bare <c>cd</c> printing rather than going home.</summary>
    public static string? Judge(string text, WorkingDirectory files, string baseFolder, bool isScript, string? shell = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ShellKinds.TryParse(shell, out var kind);
        return FirstOutside(text, files.Root, baseFolder, isScript, static path => Directory.Exists(path) || File.Exists(path), WorkingDirectory.RealLinkTarget, kind);
    }

    /// <summary>
    /// Whether one token, as cut by <see cref="Tokens"/>, names a path outside the root (rules 1 to 6, 8, 9's drive and 12)
    /// when the text runs in <paramref name="baseFolder"/>; <paramref name="firstArgument"/> says it is a command's first
    /// argument after its options, where a bare <c>/</c> or <c>\</c> is the drive root, and <paramref name="folderArgument"/>
    /// that it is a <c>cd</c>'s folder, where a rooted single segment (<c>cd /etc</c>) is a folder whatever the drive holds.
    /// </summary>
    private static bool IsOutside(string token, Judging judging, string baseFolder, bool firstArgument, bool folderArgument = false)
    {
        string root = judging.Root;
        bool isScript = judging.IsScript;
        var linkTarget = judging.LinkTarget;
        // Off Windows a command line's backslash escapes the next character, as the shell reads an unquoted word: \/etc/passwd
        // is /etc/passwd there (2026-10-06, the macOS build). Undone first, so every rule reads the word the shell will.
        if (!isScript && !OperatingSystem.IsWindows() && token.Contains('\\'))
        {
            token = Unescape(token);
        }

        if (token.Length == 0 || IsDevice(token))
        {
            return false;
        }

        // 6. The home folder; off Windows ~name in a command line too, that account's home.
        if (token == "~" || token.StartsWith("~/", StringComparison.Ordinal) || token.StartsWith("~\\", StringComparison.Ordinal)
            || (!isScript && !OperatingSystem.IsWindows() && token.Length > 1 && token[0] == '~' && (char.IsAsciiLetter(token[1]) || token[1] == '_')))
        {
            return true;
        }

        // 9. A bare drive in a command line: that drive's own folder, the root's drive alone staying home. Windows only: elsewhere C: is a name.
        if (!isScript && OperatingSystem.IsWindows() && token.Length == 2 && char.IsAsciiLetter(token[0]) && token[1] == ':')
        {
            return !(root.Length >= 2 && root[1] == ':' && char.ToUpperInvariant(root[0]) == char.ToUpperInvariant(token[0]));
        }

        // 1. A drive-absolute path: under the root or not.
        if (IsDriveAbsolute(token))
        {
            return !Under(root, Located(token, baseFolder, judging)!, linkTarget);
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

            return rest.Length > 0 && IsOutside(rest, judging with { IsScript = false }, baseFolder, firstArgument: false);
        }

        // 8. A path inside the token (-out:C:\x, @C:\x, FileSystem::C:\x, -o:..\x, -oC:\x): judged as itself, the switch around it ignored.
        if (EmbeddedStart(token, isScript) is var at && at > 0)
        {
            return IsOutside(token[at..], judging, baseFolder, firstArgument: false);
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
            // 3. Git Bash's drive form (/d/Repo, and in bash /d itself).
            if (IsBashDrive(token, judging))
            {
                return !Under(root, Located(token, baseFolder, judging)!, linkTarget);
            }

            // Comments, not paths: // and /*; two backslashes without a share (\\d+, a regex) are not a UNC path either.
            if (Located(token, baseFolder, judging) is not { } rooted)
            {
                return false;
            }

            // 4. Rooted. Separators alone: the drive root as a command's first argument, nothing elsewhere.
            if (token.AsSpan().IndexOfAnyExcept(Separators) < 0)
            {
                return firstArgument && (token == "/" || token == "\\");
            }

            // One segment: a switch (dir /s, msbuild /t:Build) names nothing on the disk; /Users does. A cd's is a folder (cd /etc).
            bool oneSegment = token.IndexOfAny(Separators, 1) < 0;
            return oneSegment && !folderArgument
                ? Full(rooted) is { } full && !Under(root, full, linkTarget) && judging.Exists(full)
                : !Under(root, rooted, linkTarget);
        }

        // 5. A .. segment: resolved from where the text runs.
        if (HasParentSegment(token))
        {
            return !Under(root, Located(token, baseFolder, judging) ?? Path.Combine(baseFolder, Collapse(token)), linkTarget);
        }

        // 12. A plain relative name is under the root by its spelling; a link on its way may still lead out (dir link, type link\x).
        // Not one with a colon: a:b is a slice or a key far more often than a drive-relative path, and Combine would root it.
        return Located(token, baseFolder, judging) is { } located && !Under(root, located, linkTarget);
    }

    /// <summary>
    /// The one reading of a token as a path (2026-10-03, the review: the cd rule kept a copy of these forms until then): where
    /// it points when the text runs in <paramref name="folder"/>, not yet made full — a drive-absolute path, Git Bash's drive
    /// form, a rooted path on the root's drive, a relative one under the folder; null for what names no place the text shows
    /// (<c>~</c>, a variable, a colon that is no drive's, a comment, two backslashes, which the UNC rule reads).
    /// </summary>
    private static string? Located(string token, string folder, Judging judging)
    {
        if (IsDriveAbsolute(token))
        {
            return Collapse(token);
        }

        if (IsBashDrive(token, judging))
        {
            return char.ToUpperInvariant(token[1]) + ":\\" + (token.Length > 3 ? Collapse(token[3..]) : "");
        }

        if (token[0] is '/' or '\\')
        {
            return token.StartsWith("//", StringComparison.Ordinal) || token.StartsWith("/*", StringComparison.Ordinal) || token.StartsWith("\\\\", StringComparison.Ordinal)
                ? null
                : Path.Combine(Path.GetPathRoot(judging.Root) ?? judging.Root, Collapse(token)[1..]);
        }

        return token[0] == '~' || token.Contains('$') || token.Contains('%') || token.Contains(':') ? null : Path.Combine(folder, Collapse(token));
    }

    /// <summary>
    /// Git Bash's drive form: <c>/d/…</c> in every shell (rule 3 from the start), and <c>/d</c> alone in a bash command line,
    /// where it is the D drive (2026-10-03, the review: <c>cd /c</c>); anywhere else a lone <c>/d</c> is a switch.
    /// </summary>
    private static bool IsBashDrive(string token, Judging judging) =>
        OperatingSystem.IsWindows()
        && token.Length >= 2 && token[0] == '/' && char.IsAsciiLetter(token[1])
        && (token.Length == 2 ? !judging.IsScript && judging.Shell == ShellKind.Bash : token[2] == '/');

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
        "/dev/null", "/dev/stdin", "/dev/stdout", "/dev/stderr", "/dev/tty", "/dev/zero", "/dev/random", "/dev/urandom",
    ];

    /// <summary>
    /// Whether <paramref name="token"/> is a device: one of <see cref="Devices"/> exactly, or a cmd device with an extension
    /// (<c>nul.txt</c>) — but never a token with a separator in it (2026-10-03, the review: <c>con.x\..\..\secret.txt</c> was
    /// cut at its first dot and passed as <c>con</c>, the <c>..</c> after it unread; Win32 collapses them and reads above the root).
    /// </summary>
    private static bool IsDevice(string token)
    {
        if (token[0] == '/' || token.IndexOfAny(Separators) >= 0)
        {
            return IsOneOf(token, Devices);
        }

        int dot = token.IndexOf('.');
        return IsOneOf(dot > 0 ? token[..dot] : token, Devices);
    }

    private static bool IsOneOf(string token, IReadOnlyList<string> names) =>
        names.Any(name => string.Equals(token, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The index of a command's first argument: the token after its program and its options (<see cref="IsOption"/>), or -1.</summary>
    private static int FirstArgument(IReadOnlyList<string> tokens, ShellKind shell)
    {
        for (int i = 1; i < tokens.Count; i++)
        {
            if (!IsOption(tokens[i], shell))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// An option: <c>-x</c> or <c>--x</c> (a lone <c>-</c> is an argument, <c>cd -</c>'s previous folder), and a one-letter
    /// <c>/x</c> switch everywhere but bash, where <c>/c</c> is the C drive (rule 3) — and only on Windows: elsewhere <c>/x</c> is a path.
    /// </summary>
    private static bool IsOption(string token, ShellKind shell) =>
        (token.Length > 1 && token[0] == '-') || (OperatingSystem.IsWindows() && shell != ShellKind.Bash && token.Length == 2 && token[0] == '/' && char.IsAsciiLetter(token[1]));

    // A redirect and its target (>x, 2>>x, &>x, >&2, <x, a dangling 2> when Segments cut 2>&1 at its &): no argument of the command's.
    [GeneratedRegex(@"(?:\d|&)?>>?(?:&\d+|\s*[^\s<>|&;]*)|\d?<\s*[^\s<>|&;]*", RegexOptions.CultureInvariant)]
    private static partial Regex RedirectPattern();

    /// <summary>
    /// The folder argument of a <see cref="CdCommands"/> segment, or null when it has none — its options and redirects set aside
    /// (2026-10-03, the review: <c>cd --</c>, <c>cd -P</c>, <c>cd &gt;/dev/null</c> and <c>Set-Location -PassThru</c> go home as a
    /// bare <c>cd</c> does, rule 9).
    /// </summary>
    private static string? CdArgument(string segment, ShellKind shell)
    {
        var tokens = Tokens(RedirectPattern().Replace(segment, " ")).Select(Unjoin).ToList();
        return FirstArgument(tokens, shell) is > 0 and var index ? tokens[index] : null;
    }

    /// <summary>
    /// Rule 11: the folders the segments after a <c>cd</c> to <paramref name="argument"/> may run in. The <c>cd</c> is followed —
    /// the folders replaced by where it leads — only when the next segment surely runs there: joined by <c>&amp;&amp;</c>, <c>;</c>
    /// or a line break (cmd's <c>&amp;</c> too, which runs in turn), not in a subshell (no <c>( ) { }</c> or backtick in the
    /// segment), and to a folder that is there. Otherwise (2026-10-03, the review: a pipe's sides run in children, <c>||</c>
    /// runs only when the <c>cd</c> failed, bash's <c>&amp;</c> backgrounds it, a missing folder leaves the shell where it was)
    /// where it leads is added beside the folders, so a later path must stay under the root from each. A <c>cd</c> whose place
    /// cannot be read (<c>cd -</c>, a variable the rules let by) leaves them as they were.
    /// </summary>
    private static List<string> Moved(List<string> folders, string argument, string segment, string join, Judging judging)
    {
        var targets = new List<string>();
        foreach (string folder in folders)
        {
            if (argument == "-" || Located(argument, folder, judging) is not { } located || Full(located) is not { } full)
            {
                return folders;
            }

            targets.Add(full);
        }

        bool runsNext = join is "&&" or ";" or "\n" || (join == "&" && judging.Shell == ShellKind.Cmd);
        bool sure = runsNext && segment.IndexOfAny(['(', ')', '{', '}', '`']) < 0 && targets.All(judging.Exists);
        return (sure ? targets : folders.Concat(targets)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Rule 10: <paramref name="piece"/> with every quoted path that holds a space and lies under the root joined
    /// into one token (its whitespace swapped for <see cref="Joiner"/>, which <see cref="Unjoin"/> puts back). Only a path
    /// whose later words are plain (no <c>:</c>, none rooted, no <c>~</c>, no <c>..</c>) and each under the root on its own,
    /// its first word by its links, is joined (2026-10-03, the review: <c>bash -c "./tool.sh link/secret"</c> runs the words
    /// apart, so a link a later word goes through may not hide in the join); judged from every folder the piece may run in.
    /// An unpaired quote leaves the rest as it was, cut as before — never a pass the old rules would not give.
    /// </summary>
    private static string JoinQuotedInside(string piece, Judging judging, IReadOnlyList<string> folders)
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
                && folders.All(folder => !IsOutside(content, judging, folder, firstArgument: false) && WordsStayInside(content, judging, folder)))
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

    /// <summary>A Unix shell's unquoted word with its backslashes undone: each escapes the character after it, a last one alone goes.</summary>
    internal static string Unescape(string token)
    {
        var sb = new StringBuilder(token.Length);
        for (int i = 0; i < token.Length; i++)
        {
            if (token[i] == '\\')
            {
                if (i + 1 < token.Length)
                {
                    sb.Append(token[++i]);
                }
            }
            else
            {
                sb.Append(token[i]);
            }
        }

        return sb.ToString();
    }

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

    // Each word on its own: the later ones under the root by every rule, the first by its links alone (its spelling may leave
    // the root only as the whole path's start does: "..\My Project\a.txt" from a root named My Project).
    private static bool WordsStayInside(string content, Judging judging, string folder)
    {
        var words = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool firstStays = Located(words[0], folder, judging) is not { } located || Full(located) is not { } full
            || !WorkingDirectory.IsInside(judging.Root, full) || WorkingDirectory.LinkEscape(judging.Root, full, judging.LinkTarget) is null;
        return firstStays && words.Skip(1).All(word => !IsOutside(word, judging, folder, firstArgument: false));
    }

    /// <summary>
    /// Rule 8: where a path inside <paramref name="token"/> starts, or -1 — a drive-absolute path after a character that is
    /// no letter or digit (so <c>HKLM:\</c> and <c>https:</c> are not one), a <c>\\server\share</c> the same way in a command
    /// line, a <c>..</c> after <c>:</c> or <c>@</c>; and any of the three straight after a one-letter switch, its letter glued to
    /// the path (2026-10-03, the review: 7-Zip's <c>-oC:\x</c>, a compiler's <c>-IC:\x</c> and <c>-I..\x</c>).
    /// </summary>
    private static int EmbeddedStart(string token, bool isScript)
    {
        for (int i = 1; i < token.Length; i++)
        {
            char before = token[i - 1];
            bool afterSwitch = i == 2 && token[0] is ('-' or '/') && char.IsAsciiLetter(before);
            if (char.IsAsciiLetterOrDigit(before) && !afterSwitch)
            {
                continue;
            }

            var rest = token.AsSpan(i);
            if (IsDriveAbsolute(rest)
                || (!isScript && rest.StartsWith(@"\\", StringComparison.Ordinal) && UncPattern().IsMatch(token[i..]))
                || ((before == ':' || before == '@' || afterSwitch) && rest.StartsWith("..", StringComparison.Ordinal) && (rest.Length == 2 || rest[2] is '/' or '\\')))
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
