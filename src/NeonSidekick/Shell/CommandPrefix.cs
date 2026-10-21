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
///
/// <para>A program named by a path keeps its folder (later on 2026-10-19, the third shell review): <c>.\git.cmd status</c> read as
/// <c>git status</c>, so a <c>git.cmd</c> the model wrote into the working folder ran under the user's allowed <c>git status</c>. It is
/// now <c>./git status</c> (<see cref="Program"/>), a prefix of its own; a bare name is the one found on PATH, cmd's working-folder
/// search being turned off for every shell child (<see cref="ChildEnvironment"/>'s <c>NoDefaultCurrentDirectoryInExePath</c>). A leading
/// <c>NAME=value</c> is skipped only in bash and zsh, where it is an assignment: in PowerShell it is the command's name. In cmd the
/// name ends at a <c>=</c>, <c>,</c> or <c>;</c>, and since 2026-10-20 (the code review) a first word with text glued after one
/// (<c>FOO=1</c>, <c>git,push</c>) is <see cref="Unreadable"/>: the program, not cmd, decides whether that text is an argument.</para>
///
/// <para>A program that runs another command is read through to it, or asks (later on 2026-10-20, the code review: an allowed
/// <c>env</c>, <c>xargs</c>, <c>eval</c>, <c>iex</c> or <c>source</c> covered whatever came after it): <c>env</c>, <c>nohup</c>, <c>nice</c>,
/// <c>timeout</c> and the like by their options to the command's own prefix (<see cref="RunnerSkip"/>); <c>sudo</c>, <c>xargs</c>, <c>eval</c>,
/// PowerShell's <c>Invoke-Expression</c> and <c>Start-Process</c> as <see cref="Unreadable"/>; a sourced script (<c>. ./a.sh</c>, <c>source
/// a.sh</c>, PowerShell's <c>. .\a.ps1</c>) with its path in the prefix, cmd's <c>call</c> as the command it calls.</para>
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
        ["docker"] = Set("-H", "--host", "-c", "--context", "--config"),   // a context names another daemon, as -H does (2026-10-20, the code review)
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
        "NoDefaultCurrentDirectoryInExePath", "PATHEXT", "COMSPEC",
        // The environment twins of DangerousOptions and the other variables that make an allowed verb run another program or reach
        // another server (the fourth shell review, 2026-10-19): git's -c and --exec-path, kubectl's --kubeconfig, docker's -H and
        // --config, the editors and pagers a tool starts, a config folder holding those (HOME: ~/.gitconfig's core.pager), the
        // compilers and loaders a build runs. Families too wide to list are DangerousEnvPrefixes.
        "GIT_CONFIG_PARAMETERS", "GIT_EXEC_PATH", "KUBECONFIG", "DOCKER_HOST", "DOCKER_CONFIG", "DOCKER_CONTEXT", "CONTAINER_HOST",
        "EDITOR", "VISUAL", "PAGER", "MANPAGER", "BROWSER", "LESSOPEN", "LESSCLOSE", "SHELL", "SSH_ASKPASS", "SUDO_ASKPASS", "PS4",
        "HOME", "XDG_CONFIG_HOME", "XDG_CONFIG_DIRS", "MAKEFILES", "MAKEFLAGS", "MFLAGS", "GNUMAKEFLAGS", "CC", "CXX", "CPP", "LD",
        "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_ROOT", "RUSTC", "RUSTC_WRAPPER", "RUSTC_WORKSPACE_WRAPPER", "RUSTDOC",
        "RUSTFLAGS", "RUSTDOCFLAGS", "GOFLAGS", "GOROOT", "GOPATH", "GOTOOLCHAIN", "GOENV", "GOPROXY", "NODE_PATH", "RUBYOPT", "RUBYLIB",
        "PERLLIB", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "AWS_CONFIG_FILE", "AWS_SHARED_CREDENTIALS_FILE",
        "AZURE_CONFIG_DIR", "TF_PLUGIN_CACHE_DIR", "TF_DATA_DIR",
    };

    /// <summary>
    /// The variable families <see cref="DangerousEnvVars"/> takes by the start of the name (the fourth shell review, 2026-10-19), any
    /// case: git's, npm's <c>npm_config_*</c> (its <c>script_shell</c>), yarn's (<c>YARN_YARN_PATH</c> runs a file), docker's and
    /// podman's, kubectl's, cargo's linkers and wrappers, .NET's hosts and profilers, MSBuild's and NuGet's paths, Python's and pip's,
    /// the dynamic loaders', gcloud's (<c>CLOUDSDK_PYTHON</c>), gh's (its aliases may be shell commands), terraform's CLI config. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> DangerousEnvPrefixes =
    [
        "GIT_", "NPM_CONFIG_", "YARN_", "PNPM_", "COREPACK_", "DOCKER_", "CONTAINER_", "CONTAINERS_", "KUBE", "CARGO_", "CGO_",
        "DOTNET_", "COREHOST_", "CORECLR_", "COMPLUS_", "MSBUILD", "NUGET_", "PYTHON", "PIP_", "UV_", "POETRY_", "LD_", "DYLD_",
        "PERL5", "CLOUDSDK_", "GH_", "TF_CLI_",
    ];

    /// <summary>
    /// Whether a variable named <paramref name="name"/> can redirect what an allowed program runs: one of <see cref="DangerousEnvVars"/>
    /// or of a <see cref="DangerousEnvPrefixes"/> family, any case (cmd's and PowerShell's names are case-blind; in bash a lower-case
    /// twin only asks more).
    /// </summary>
    public static bool IsDangerousName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (DangerousEnvVars.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string family in DangerousEnvPrefixes)
        {
            if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The commands that set a variable for the rest of the line (later on 2026-10-19, the third shell review): cmd's <c>set</c> and
    /// bash's and zsh's <c>export</c>, <c>declare</c>, <c>typeset</c>, <c>readonly</c>, <c>local</c> (the last two the fourth review's).
    /// One that sets a <see cref="DangerousEnvVars"/> name (<c>set PATH=…&amp; git status</c>) reads as <see cref="Unreadable"/>, so an
    /// allowed <c>set</c> never lets the next segment run another program. Pinned.
    /// </summary>
    public static readonly IReadOnlySet<string> SettingPrograms = new HashSet<string>(StringComparer.Ordinal) { "set", "export", "declare", "typeset", "readonly", "local" };

    /// <summary>
    /// PowerShell's item cmdlets and their aliases, which set or remove a variable through the <c>env:</c> drive (the fourth shell review,
    /// 2026-10-19): <c>Set-Item env:PATH …</c> or <c>Remove-Item env:NoDefaultCurrentDirectoryInExePath</c> under an allowed
    /// <c>set-item</c> changed what the next segment ran. One naming a dangerous variable reads as <see cref="Unreadable"/>.
    /// </summary>
    private static readonly IReadOnlySet<string> EnvDrivePrograms = new HashSet<string>(StringComparer.Ordinal)
    {
        "set-item", "si", "new-item", "ni", "remove-item", "ri", "rm", "del", "erase", "clear-item", "cli", "rename-item", "rni", "copy-item", "cpi",
    };

    /// <summary>
    /// The words bash and zsh run the command after (the fourth shell review, 2026-10-19): read past on the way to it, so an allowed
    /// <c>then</c> or <c>!</c> (from one approved <c>if …; then …</c>) never covers the command it leads. <c>time</c> takes <c>-p</c>,
    /// <c>exec</c> its options, <c>command</c> <c>-p</c> (<c>command -v</c> runs nothing and is <c>command</c>). zsh's precommand
    /// modifiers too. A word alone is its own prefix.
    /// </summary>
    private static readonly IReadOnlySet<string> LeadingWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "!", "{", "then", "do", "else", "elif", "if", "while", "until", "time", "builtin", "exec", "command",
    };

    private static readonly IReadOnlySet<string> ZshLeadingWords = new HashSet<string>(StringComparer.Ordinal) { "nocorrect", "noglob", "-" };

    /// <summary>
    /// The bash and zsh words whose command cannot be told from the words (the fourth shell review): <c>case</c> (its patterns lead
    /// commands), <c>coproc</c>, <c>function</c> (a function named <c>git</c> runs under <c>git status</c>). The line asks.
    /// </summary>
    private static readonly IReadOnlySet<string> UnreadableWords = new HashSet<string>(StringComparer.Ordinal) { "case", "coproc", "function" };

    /// <summary>zsh's short loops (on by default): <c>for x (a b) rm $x</c>, <c>repeat 3 rm x</c> run a command in the header's segment. The line asks.</summary>
    private static readonly IReadOnlySet<string> ZshUnreadableWords = new HashSet<string>(StringComparer.Ordinal) { "for", "foreach", "repeat", "select" };

    /// <summary>The zsh words whose short form runs a command after a <c>[[ … ]]</c>, <c>(( … ))</c>, <c>( … )</c> or before a <c>{ … }</c> (<c>if [[ -f x ]] rm x</c>).</summary>
    private static readonly IReadOnlySet<string> ZshShortForms = new HashSet<string>(StringComparer.Ordinal) { "if", "elif", "while", "until" };

    /// <summary>PowerShell's statements that take a pipeline (measured: <c>return whoami</c>, <c>throw whoami</c>, <c>exit /b</c> each run it), any case.</summary>
    private static readonly IReadOnlySet<string> PowerShellLeadingWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "exit", "return", "throw" };

    /// <summary>
    /// The options a verb program takes ahead of its verb with no value (2026-10-19, the second shell review): skipped alone on the way to
    /// the verb. An option in neither this list nor <see cref="ValueOptions"/> (by its name, before any <c>=value</c>) reads as
    /// <see cref="Unreadable"/> when a word follows it, since an unknown one may take that word as its value: <c>dotnet --roll-forward test
    /// x.dll</c> ran <c>x.dll</c> under an allowed <c>dotnet test</c>, <c>npm --registry test publish</c> was <c>npm test</c>, and npm's
    /// <c>--script-shell=…</c> picks the shell its scripts run in. Pinned.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> FlagOptions = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["git"] = Set("--no-pager", "-p", "--paginate", "-P", "--bare", "--no-replace-objects", "--literal-pathspecs", "--glob-pathspecs",
            "--noglob-pathspecs", "--icase-pathspecs", "--no-optional-locks", "--no-advice", "--version", "--help"),
        ["docker"] = Set("-D", "--debug", "--tls", "--tlsverify"),
        ["podman"] = Set("--remote", "-r"),
        ["kubectl"] = Set("--insecure-skip-tls-verify"),
        ["npm"] = Set("-g", "--global", "-s", "--silent", "-q", "--quiet", "-ws", "--workspaces", "-y", "--yes"),
        ["pnpm"] = Set("-r", "--recursive", "-w", "--workspace-root", "-s", "--silent"),
        ["yarn"] = Set("-s", "--silent"),
        ["dotnet"] = Set("-d", "--diagnostics"),
        ["cargo"] = Set("-q", "--quiet", "-v", "--verbose", "--frozen", "--locked", "--offline"),
        ["uv"] = Set("-q", "--quiet", "-v", "--verbose", "--offline", "--no-cache"),
        ["poetry"] = Set("-q", "--quiet", "-v", "--verbose", "-n", "--no-interaction", "--ansi", "--no-ansi"),
        ["pip"] = Set("-q", "--quiet", "-v", "--verbose", "--isolated", "--no-cache-dir", "--disable-pip-version-check"),
        ["pip3"] = Set("-q", "--quiet", "-v", "--verbose", "--isolated", "--no-cache-dir", "--disable-pip-version-check"),
        ["make"] = Set("-s", "--silent", "-k", "--keep-going", "-n", "--dry-run", "-B", "--always-make"),
        ["aws"] = Set("--debug", "--no-cli-pager", "--no-paginate"),
        ["gcloud"] = Set("-q", "--quiet"),
        ["terraform"] = Set("-chdir"),
    };

    private static IReadOnlySet<string> Set(params string[] options) => new HashSet<string>(options, StringComparer.Ordinal);

    /// <summary>The character that escapes the next one in <paramref name="shell"/>: PowerShell's backtick, cmd's caret, else a backslash. Matches <see cref="Substitutions"/>.</summary>
    private static char EscapeChar(ShellKind shell) => shell switch
    {
        ShellKind.PowerShell => '`',
        ShellKind.Cmd => '^',
        _ => '\\',
    };

    /// <summary>
    /// The kind of quote <paramref name="c"/> is in <paramref name="shell"/>: <c>'</c>, <c>"</c>, or <c>\0</c> for none (2026-10-19, the
    /// second shell review). PowerShell takes the typographic ones too (U+2018–U+201B single, U+201C–U+201E double), any of a kind closing
    /// any other, so a <c>'…’</c> string closed where the old ASCII-only reading saw it still open and hid a <c>;</c>. cmd has no single
    /// quote: a <c>'…&amp; x &amp;…'</c> was read as quoted while cmd split on each <c>&amp;</c>. Every scan here goes through this.
    /// </summary>
    private static char QuoteKind(char c, ShellKind shell) => c switch
    {
        '"' => '"',
        '\'' => shell == ShellKind.Cmd ? '\0' : '\'',
        '\u2018' or '\u2019' or '\u201A' or '\u201B' => shell == ShellKind.PowerShell ? '\'' : '\0',
        '\u201C' or '\u201D' or '\u201E' => shell == ShellKind.PowerShell ? '"' : '\0',
        _ => '\0',
    };

    /// <summary>
    /// Whether <paramref name="c"/> is a blank between words in <paramref name="shell"/> (2026-10-19, measured): bash and zsh only a space, a
    /// tab and a line break (a no-break space is part of a word); PowerShell .NET's white space but U+001C–U+001F, which it reads as text.
    /// </summary>
    private static bool IsBlank(char c, ShellKind shell) => shell switch
    {
        ShellKind.PowerShell => char.IsWhiteSpace(c) && c is not ('\u001C' or '\u001D' or '\u001E' or '\u001F'),
        _ => c is ' ' or '\t' or '\n',
    };

    /// <summary>Whether a line ends at <paramref name="c"/> in <paramref name="shell"/>: a line feed, and in PowerShell a carriage return too (measured).</summary>
    private static bool EndsLine(char c, ShellKind shell) => c == '\n' || (c == '\r' && shell == ShellKind.PowerShell);

    // A PowerShell here-string's head: @' or @" (a typographic quote too) with nothing but blanks after it on its line.
    [GeneratedRegex("@[\"'\u2018-\u201E][^\\S\\r\\n]*[\\r\\n]", RegexOptions.CultureInvariant)]
    private static partial Regex HereStringPattern();

    // A bash or zsh here-document (<<, <<-, not the here-string <<<): its body's quotes are text up to a line naming its end, which no scan here follows.
    [GeneratedRegex("(?<!<)<<(?!<)", RegexOptions.CultureInvariant)]
    private static partial Regex HereDocumentPattern();

    private enum Comment
    {
        None,
        Opens,
        /// <summary>A <c>#</c> whose reading turns on what came before (measured): in PowerShell after a <c>)</c>, <c>}</c>, <c>=</c>, a quote or a substitution (<c>"a"#x</c> is a comment, <c>a"b"#x</c> isn't; <c>$y=#x</c> is, <c>a=#x</c> isn't); in bash and zsh after a <c>)</c>, a subshell's or a substitution's (<c>$(…)#x</c> isn't).</summary>
        Maybe,
    }

    /// <summary>
    /// Whether a comment opens at <paramref name="at"/> outside quotes, and <paramref name="end"/> the index just past it (a line comment
    /// stops before its line break) — one rule for every scan here (2026-10-19, the second shell review: <see cref="JoinedSegments"/> read a
    /// comment's apostrophe as a quote that hid the line breaks after it, while <see cref="Substitutions"/> passed over it). A <c>#</c> (or
    /// PowerShell's <c>&lt;#</c>) opens one at a word's start, measured per shell: after the line's start or a blank; in bash and zsh after
    /// <c>; &amp; | ( &lt; &gt;</c>; in PowerShell after <c>; &amp; | , ( {</c>, with <see cref="Comment.Maybe"/> for the predecessors it
    /// depends on. Never after an escaped character (<paramref name="escapedAt"/>); cmd has none.
    /// </summary>
    private static Comment CommentAt(string text, int at, ShellKind shell, int escapedAt, out int end)
    {
        end = at;
        bool powerShell = shell == ShellKind.PowerShell;
        bool block = powerShell && text[at] == '<' && at + 1 < text.Length && text[at + 1] == '#';
        if (shell == ShellKind.Cmd || (text[at] != '#' && !block))
        {
            return Comment.None;
        }

        Comment kind;
        if (at == 0)
        {
            kind = Comment.Opens;
        }
        else if (at - 1 == escapedAt)
        {
            return Comment.None;
        }
        else
        {
            char before = text[at - 1];
            kind = IsBlank(before, shell) || EndsLine(before, shell) ? Comment.Opens
                : !powerShell ? (before is ';' or '&' or '|' or '(' or '<' or '>' ? Comment.Opens : before is ')' or Hole ? Comment.Maybe : Comment.None)
                : before is ';' or '&' or '|' or ',' or '(' or '{' ? Comment.Opens
                : before is ')' or '}' or '=' or Hole || QuoteKind(before, shell) != '\0' ? Comment.Maybe
                : Comment.None;
        }

        if (kind == Comment.None)
        {
            return kind;
        }

        if (block)
        {
            int close = text.IndexOf("#>", at + 2, StringComparison.Ordinal);
            end = close >= 0 ? close + 2 : text.Length;
            return kind;
        }

        end = at;
        while (end < text.Length && !EndsLine(text[end], shell))
        {
            end++;
        }

        return kind;
    }

    /// <summary>Whether any of <paramref name="text"/>[<paramref name="from"/>..<paramref name="to"/>] is a quote in <paramref name="shell"/>.</summary>
    private static bool HasQuote(string text, int from, int to, ShellKind shell)
    {
        for (int i = from; i < to; i++)
        {
            if (QuoteKind(text[i], shell) != '\0')
            {
                return true;
            }
        }

        return false;
    }

    private static string OptionName(string option)
    {
        int eq = option.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? option : option[..eq];
    }

    private static bool IsDangerousAssignment(string token)
    {
        int eq = token.IndexOf('=', StringComparison.Ordinal);
        return eq > 0 && IsDangerousName(token[..eq].TrimEnd('+'));
    }

    /// <summary>Whether a <see cref="SettingPrograms"/> word's arguments set a dangerous name (<see cref="IsDangerousName"/>); a quote around the pair is read through.</summary>
    private static bool SetsDangerous(List<string> tokens, int from)
    {
        for (int i = from; i < tokens.Count; i++)
        {
            string word = tokens[i].Trim('"', '\'');
            int eq = word.IndexOf('=', StringComparison.Ordinal);
            string name = eq < 0 ? word : word[..eq].TrimEnd('+');
            if (name.Length > 0 && IsDangerousName(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether an <see cref="EnvDrivePrograms"/> cmdlet's arguments name a dangerous variable on the <c>env:</c> drive (<c>env:PATH</c>, <c>Env:\PATH</c>, <c>-Path env:PATH</c>).</summary>
    private static bool TouchesDangerousEnv(List<string> tokens, int from)
    {
        for (int i = from; i < tokens.Count; i++)
        {
            string word = tokens[i].Trim('"', '\'');
            int colon = word.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && word.AsSpan(0, colon).EndsWith("env", StringComparison.OrdinalIgnoreCase)
                && word[(colon + 1)..].TrimStart('\\', '/') is { Length: > 0 } name && IsDangerousName(name.TrimEnd(',')))
            {
                return true;
            }
        }

        return false;
    }

    // A bash or zsh word that assigns a subscript (a[1]=x): bash refuses it as a prefix and zsh reads it its own way; neither is the program.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\[[^\]]*\]\+?=", RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptAssignmentPattern();

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
        bool ansi = false;
        int escapedAt = -1;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (ansi)
            {
                // Inside bash's and zsh's $'…' a backslash escapes, so \' never closes it (the fourth shell review, 2026-10-19: the
                // plain single-quote rule saw the string close there and open again at the real close, hiding the separators after it).
                current.Append(c);
                if (c == '\\' && i + 1 < command.Length)
                {
                    current.Append(command[++i]);
                }
                else if (c == '\'')
                {
                    ansi = false;
                    quote = '\0';
                }

                continue;
            }

            // An escaped character is literal: never a quote that opens a string, never a separator (bash/zsh \, PowerShell `, cmd ^,
            // which is literal inside double quotes). Mirrors Substitutions, so a desync can no longer hide a separator inside what the
            // segmenter thinks is a string (the code review, 2026-10-19).
            if (c == escape && i + 1 < command.Length && quote != '\'' && (shell != ShellKind.Cmd || quote == '\0'))
            {
                current.Append(c).Append(command[++i]);
                escapedAt = i;
                continue;
            }

            if (quote != '\0')
            {
                current.Append(c);
                if (QuoteKind(c, shell) == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            // A comment runs nothing and its quotes are text: left out of the segment, the line break after it still a separator (2026-10-19,
            // the second shell review). One whose reading is in doubt (PowerShell's Maybe) is read as code: Collect asks for it when it holds a quote.
            if (CommentAt(command, i, shell, escapedAt, out int commentEnd) == Comment.Opens)
            {
                current.Append(' ');
                i = commentEnd - 1;
                continue;
            }

            if (OpensAnsiC(command, i, shell))
            {
                current.Append(c).Append(command[++i]);
                quote = '\'';
                ansi = true;
                continue;
            }

            if (QuoteKind(c, shell) is var kind and not '\0')
            {
                quote = kind;
                current.Append(c);
                continue;
            }

            switch (c)
            {
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

    /// <summary>Whether bash's or zsh's ANSI-C string <c>$'…'</c> opens at <paramref name="at"/> (outside quotes; the caller checks), where a backslash escapes up to its close.</summary>
    private static bool OpensAnsiC(string text, int at, ShellKind shell) =>
        shell is ShellKind.Bash or ShellKind.Zsh && text[at] == '$' && at + 1 < text.Length && text[at + 1] == '\'';

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
        // segment of only assignments — either can redirect what the command after it runs. Only in bash and zsh (later that day, the
        // third review): PowerShell runs a command of that name and cmd one named up to the =, so the program after it was judged instead.
        bool assigns = shell is ShellKind.Bash or ShellKind.Zsh;
        while (first < tokens.Count)
        {
            string word = tokens[first];
            if (assigns && IsAssignment(word))
            {
                if (IsDangerousAssignment(word))
                {
                    return Unreadable;
                }

                first++;
                continue;
            }

            if (assigns && SubscriptAssignmentPattern().IsMatch(word))
            {
                return Unreadable;
            }

            // A subshell's or a group's opening paren glued to the name ((rm x) in bash and zsh, (del x) and @del x in cmd) is not the
            // name's (the fourth shell review); bash's (( … )) is arithmetic and stays its own prefix.
            if ((assigns || shell == ShellKind.Cmd) && word.Length > 1 && ((word[0] == '@' && shell == ShellKind.Cmd) || (word[0] == '(' && word[1] != '(')))
            {
                tokens[first] = word[1..];
                continue;
            }

            // A program that runs the command after it (env, nohup, sudo, xargs…) or a string as code (eval, iex): read through, or the line
            // asks (2026-10-20, the code review: env was the prefix, so an allowed env ran any program with any variable).
            int runs = RunnerSkip(tokens, first, shell);
            if (runs == AskSkip)
            {
                return Unreadable;
            }

            if (runs > 0)
            {
                if (first + runs >= tokens.Count)
                {
                    return Program(word);   // the runner with only its options (nice -n 5, env FOO=1): it runs nothing else
                }

                first += runs;
                continue;
            }

            int skip = LeadingSkip(tokens, first, shell);
            if (skip == 0)
            {
                break;
            }

            if (skip < 0)
            {
                return skip == AloneSkip ? word.ToLowerInvariant() : Unreadable;
            }

            if (shell == ShellKind.PowerShell)
            {
                // exit/return/throw run the pipeline after them: read as a statement, so a value (exit $LASTEXITCODE, throw "x") is the
                // word's own prefix and a command or a method call (return $x.Delete()) is read.
                return StatementPrefix(string.Join(' ', tokens.Skip(first + 1))) ?? word.ToLowerInvariant();
            }

            if (first + skip >= tokens.Count)
            {
                return word.ToLowerInvariant();   // a word alone (then, fi's then, a bare !) runs nothing
            }

            first += skip;
        }

        if (first >= tokens.Count)
        {
            return first > 0 ? Unreadable : "";
        }

        string token = tokens[first];
        if (shell == ShellKind.Cmd && token.IndexOfAny(CmdNameEnds) is > 0 and var end && QuoteKind(token[0], shell) == '\0')
        {
            // cmd ends a command's name at = and , as at a blank: FOO=1 runs foo. Text glued after the cut asks (2026-10-20, the code
            // review): whether a program reads it as an argument (cmd's split) or as part of its own name (the C runtime's, blanks only)
            // is the program's, so either reading has a hole under the other — git,push read as git, which an allowed git or git
            // --version then covered, and set,PATH=… as set, the PATH never seen by SetsDangerous. A trailing delimiter alone (git, status)
            // is still read.
            if (token[end..].TrimStart(CmdNameEnds).Length > 0)
            {
                return Unreadable;
            }

            token = token[..end];
        }

        string program = Program(token);
        if (program.Length == 0)
        {
            return "";
        }

        // A path's program is judged by its folder too; its options and verb by its file's name. A substitution in the folder asks.
        int slash = program.LastIndexOf('/');
        string baseName = program[(slash + 1)..];
        if (slash >= 0 && program.Contains(Hole))
        {
            return Unreadable;
        }

        // A script sourced into the shell is the script (2026-10-20, the code review: . ./a.sh, source a.sh and PowerShell's . .\a.ps1 read as
        // the bare word, so one approval covered every script): the word and the script's own reading (. ./a), one approval per script. A
        // script that is a value (. $x, source "$(…)", . <(curl …), a glob) asks.
        if ((shell is ShellKind.Bash or ShellKind.Zsh && slash < 0 && baseName is "." or "source") || (shell == ShellKind.PowerShell && program == "."))
        {
            return first + 1 >= tokens.Count ? program
                : IsValueWord(tokens[first + 1], shell) ? Unreadable
                : program + " " + Program(tokens[first + 1]);
        }

        if ((SettingPrograms.Contains(baseName) && SetsDangerous(tokens, first + 1))
            || (shell == ShellKind.PowerShell && EnvDrivePrograms.Contains(baseName) && TouchesDangerousEnv(tokens, first + 1)))
        {
            return Unreadable;
        }

        if (VerbPrograms.Contains(baseName))
        {
            var values = ValueOptions.GetValueOrDefault(baseName);
            var flags = FlagOptions.GetValueOrDefault(baseName);
            var dangerous = DangerousOptions.GetValueOrDefault(baseName);
            for (int i = first + 1; i < tokens.Count; i++)
            {
                string verb = tokens[i];
                if (verb.Length > 1 && verb[0] == '-' && skipOptions)
                {
                    string name = OptionName(verb);
                    if (dangerous is not null && dangerous.Contains(name))
                    {
                        // -c / --config-env / -H / --kubeconfig / -f …: the option itself lets the verb run another program, so the verb
                        // is no longer what the line does. The pane shows the whole line (2026-10-19, the code review).
                        return Unreadable;
                    }

                    // An option ahead of the verb (2026-10-12): skipped, and its value with it when it takes one as the next word. One
                    // this list doesn't know may take the next word as its value, so the verb can't be told and the line asks; a last
                    // word (git --version) is only an option (2026-10-19, the second shell review).
                    bool takesValue = values is not null && values.Contains(verb);
                    bool known = takesValue || (values is not null && values.Contains(name)) || (flags is not null && flags.Contains(name));
                    if (!known)
                    {
                        return i + 1 < tokens.Count ? Unreadable : program;
                    }

                    i += takesValue ? 1 : 0;
                    continue;
                }

                if (verb.Length > 0 && QuoteKind(verb[0], shell) != '\0')
                {
                    // A quoted verb (git "push"): the quotes stripped and read, so a bare `git` never covers it (2026-10-19, the code
                    // review; it used to stop the scan and leave the prefix the bare program). A quoted option-looking word asks.
                    string unquoted = string.Concat(verb.Where(ch => QuoteKind(ch, shell) == '\0'));
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

    // LeadingSkip's answers besides a count: the line asks; the word is the prefix (command -v, a word with nothing after it).
    private const int AskSkip = -1;
    private const int AloneSkip = -2;

    /// <summary>
    /// How many words at <paramref name="at"/> lead the command after them rather than being it (the fourth shell review, 2026-10-19):
    /// 0 for none (the word is the program), <see cref="AskSkip"/> for a word whose command cannot be told, <see cref="AloneSkip"/> for one
    /// that is its own prefix. bash and zsh: <see cref="LeadingWords"/> (with <c>time -p</c>, <c>exec</c>'s options, <c>command -p</c>), zsh's
    /// precommand modifiers, a bare <c>(</c>; <see cref="UnreadableWords"/>, a function's definition, zsh's short forms ask. cmd's
    /// <c>if</c> and <c>for</c> ask, a bare <c>(</c> is skipped. PowerShell's <see cref="PowerShellLeadingWords"/> are one word.
    /// </summary>
    private static int LeadingSkip(List<string> tokens, int at, ShellKind shell)
    {
        string word = tokens[at];
        string? next = at + 1 < tokens.Count ? tokens[at + 1] : null;
        switch (shell)
        {
            case ShellKind.PowerShell:
                return PowerShellLeadingWords.Contains(word) ? 1 : 0;
            case ShellKind.Cmd:
                string lower = word.ToLowerInvariant();
                return lower is "if" or "for" || lower.StartsWith("if/", StringComparison.Ordinal) || lower.StartsWith("for/", StringComparison.Ordinal) ? AskSkip
                    : word == "(" ? 1
                    : 0;
        }

        bool zsh = shell == ShellKind.Zsh;
        if (word.Contains("()", StringComparison.Ordinal) || (next is not null && next.StartsWith("()", StringComparison.Ordinal))
            || UnreadableWords.Contains(word) || (zsh && ZshUnreadableWords.Contains(word)))
        {
            return AskSkip;
        }

        if (zsh && ZshShortForms.Contains(word)
            && ((next is not null && (next[0] is '(' or '{' || next.StartsWith("[[", StringComparison.Ordinal)))
                || tokens.Skip(at + 1).Any(t => t is "{" or "}")))
        {
            return AskSkip;
        }

        if (word == "(" || (zsh && ZshLeadingWords.Contains(word)))
        {
            return 1;
        }

        if (!LeadingWords.Contains(word))
        {
            return 0;
        }

        int skip = 1;
        switch (word)
        {
            case "time":
                skip += next == "-p" ? 1 : 0;
                break;
            case "command" when next is "-v" or "-V":
                return AloneSkip;   // a lookup: runs nothing
            case "command":
                skip += next == "-p" ? 1 : 0;
                break;
            case "exec":
                // exec [-cl] [-a name] command
                while (at + skip < tokens.Count && tokens[at + skip] is { Length: > 1 } option && option[0] == '-')
                {
                    skip += option == "-a" ? 2 : 1;
                }

                break;
        }

        return skip;
    }

    /// <summary>
    /// How a program that runs the command after it is read on the way to that command: its options with no value, those that take one (the
    /// next word, glued to a short one, or after <c>=</c>), how many words then stand before the command (<c>timeout</c>'s duration), and
    /// whether a bare <c>-N</c> is an option (<c>nice -10</c>).
    /// </summary>
    private sealed record RunnerGrammar(IReadOnlySet<string> Flags, IReadOnlySet<string> Values, int Positionals = 0, bool NumericOption = false);

    /// <summary>
    /// The programs bash and zsh read through to the command they run (2026-10-20, the code review: <c>env FOO=1 prog</c> was the prefix
    /// <c>env</c>, so an allowed <c>env</c>, approved once to list the environment, ran any program and set any variable past
    /// <see cref="DangerousEnvVars"/>; <c>nohup</c>, <c>nice</c>, <c>timeout</c> and the rest the same). Their options are read by these
    /// grammars, an option they don't list asks (its value may be the command: <c>env -S</c>, <c>env -C</c>, <c>env -P</c>), and env's
    /// <c>NAME=value</c> words are judged as a leading assignment is. The prefix is then the command's own. Named bare or from
    /// <c>/usr/bin</c>, <c>/bin</c>, <c>/usr/sbin</c> or <c>/sbin</c>; a runner by another path is a program of its own (a model's
    /// <c>./env</c>). <c>time</c> by its path only: the bare word is bash's keyword (<see cref="LeadingWords"/>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, RunnerGrammar> ReadThroughRunners = new Dictionary<string, RunnerGrammar>(StringComparer.Ordinal)
    {
        ["env"] = new(Set("-0", "--null", "-v", "--debug", "-i", "-", "--ignore-environment", "--default-signal", "--ignore-signal", "--block-signal", "--list-signal-handling"), Set("-u", "--unset")),
        ["nohup"] = new(Set(), Set()),
        ["nice"] = new(Set(), Set("-n", "--adjustment"), NumericOption: true),
        ["timeout"] = new(Set("--preserve-status", "--foreground", "-f", "-p", "-v", "--verbose"), Set("-s", "--signal", "-k", "--kill-after"), Positionals: 1),
        ["gtimeout"] = new(Set("--preserve-status", "--foreground", "-f", "-p", "-v", "--verbose"), Set("-s", "--signal", "-k", "--kill-after"), Positionals: 1),
        ["setsid"] = new(Set("-c", "--ctty", "-f", "--fork", "-w", "--wait"), Set()),
        ["stdbuf"] = new(Set(), Set("-i", "-o", "-e", "--input", "--output", "--error")),
        ["caffeinate"] = new(Set("-d", "-i", "-m", "-s", "-u"), Set("-t", "-w")),
        ["time"] = new(Set("-p", "-l", "-h", "-a", "--append", "-v", "--verbose", "-q", "--quiet", "--portability"), Set("-o", "--output", "-f", "--format")),
    };

    /// <summary>
    /// The programs whose command cannot be read from the words, in every shell (2026-10-20, the code review): the ones that run it as another
    /// user or under another root (<c>sudo</c>, Windows' too; <c>doas</c>, <c>su</c>, <c>chroot</c>…), build it from their input
    /// (<c>xargs</c>, <c>parallel</c>), run it through a shell or a string (<c>watch</c>, <c>flock</c>, <c>script</c>), or under a tracer or a
    /// sandbox of their own. With a word after them the line asks; alone each is its own prefix. Bare or from a system folder, as
    /// <see cref="ReadThroughRunners"/>. Pinned.
    /// </summary>
    public static readonly IReadOnlySet<string> UnreadableRunners = new HashSet<string>(StringComparer.Ordinal)
    {
        "sudo", "doas", "su", "pkexec", "runuser", "xargs", "parallel", "watch", "flock", "script", "unbuffer", "chrt", "taskset", "ionice",
        "numactl", "chroot", "arch", "sandbox-exec", "systemd-run", "nsenter", "unshare", "firejail", "strace", "ltrace", "dtruss", "busybox",
    };

    /// <summary>
    /// PowerShell's cmdlets and aliases that run what they are given (2026-10-20, the code review): <c>Start-Process</c> a program,
    /// <c>Invoke-Item</c> a file by what opens it, <c>Set-Alias</c> a name under another one (<c>Set-Alias git Remove-Item; git status</c>). With
    /// a word after them the line asks. <c>Invoke-Expression</c> runs a string and asks always, alone at a pipeline's end too (<c>… | iex</c>).
    /// </summary>
    private static readonly IReadOnlySet<string> PowerShellRunners = new HashSet<string>(StringComparer.Ordinal)
    {
        "start-process", "saps", "start", "invoke-item", "ii", "set-alias", "new-alias", "sal", "nal",
    };

    /// <summary>
    /// The options with which a program runs another command or deletes (2026-10-20, the code review: an allowed <c>find</c> ran
    /// <c>find . -exec rm {} \;</c> and <c>find / -delete</c>). A line carrying one asks. A tripwire over the common ones, not every program's
    /// every hook.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> CommandOptions = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["find"] = Set("-exec", "-execdir", "-ok", "-okdir", "-delete"),
        ["gfind"] = Set("-exec", "-execdir", "-ok", "-okdir", "-delete"),
        ["fd"] = Set("-x", "--exec", "-X", "--exec-batch"),
        ["fdfind"] = Set("-x", "--exec", "-X", "--exec-batch"),
        ["rg"] = Set("--pre"),
    };

    /// <summary>
    /// How many words at <paramref name="at"/> a program that runs another command takes before that command (2026-10-20, the code review): 0
    /// when the word is no such program (or stands alone), <see cref="AskSkip"/> when the command cannot be read. bash and zsh read
    /// <see cref="ReadThroughRunners"/> through and ask for <c>eval</c> (code from a string), a <c>trap</c> that sets an action (the same) and
    /// an <c>alias</c> that defines one (zsh expands aliases in a script: <c>alias git=rm</c> then <c>git status</c>, as a function's
    /// definition asks); PowerShell asks for <c>Invoke-Expression</c> and its <see cref="PowerShellRunners"/>, cmd for <c>start</c>, and
    /// cmd's <c>call</c> is read through (<c>call a.bat</c> is <c>a</c>, as <c>a.bat</c> is), a value after it asking (<c>call %x%</c>).
    /// <see cref="UnreadableRunners"/> ask in every shell; in PowerShell and cmd the read-through runners ask too, but Windows' own
    /// <c>timeout /t 5</c>. Any program with one of its <see cref="CommandOptions"/> asks.
    /// </summary>
    private static int RunnerSkip(List<string> tokens, int at, ShellKind shell)
    {
        string word = tokens[at];
        string program = Program(word);
        int slash = program.LastIndexOf('/');
        string name = program[(slash + 1)..];
        if (CommandOptions.TryGetValue(name, out var runsCommand) && tokens.Skip(at + 1).Any(t => runsCommand.Contains(OptionName(t))))
        {
            return AskSkip;
        }

        if (shell == ShellKind.PowerShell && name is "invoke-expression" or "iex")
        {
            return AskSkip;   // any qualifier too (Microsoft.PowerShell.Utility\Invoke-Expression)
        }

        // A program by a relative path (./env, sub/script.sh) is the working folder's own, never the system's runner; alone, a runner is its own
        // prefix (env lists the environment, nohup and eval do nothing).
        string folder = program[..(slash + 1)];
        bool absolute = folder.StartsWith('/') || folder.StartsWith('~') || (folder.Length > 2 && char.IsAsciiLetter(folder[0]) && folder[1] == ':');
        bool moduleQualified = shell == ShellKind.PowerShell && slash >= 0 && !absolute && !folder.StartsWith('.');   // Microsoft.PowerShell.Management\Start-Process
        if ((slash >= 0 && !absolute && !moduleQualified) || at + 1 >= tokens.Count)
        {
            return 0;
        }

        string next = tokens[at + 1];
        bool readThrough = ReadThroughRunners.TryGetValue(name, out var grammar);
        // Read through only from the system's folders: a runner anywhere else by its full path (Homebrew's gtimeout, C:\Windows\System32\sudo.exe,
        // a script of that name the model wrote) asks rather than being read as the system's.
        if (UnreadableRunners.Contains(name) || (readThrough && slash >= 0 && folder is not ("/usr/bin/" or "/bin/" or "/usr/sbin/" or "/sbin/")))
        {
            return AskSkip;
        }
        switch (shell)
        {
            case ShellKind.PowerShell:
                return PowerShellRunners.Contains(name) || (readThrough && name != "time" && !(name == "timeout" && WindowsTimeout(tokens, at))) ? AskSkip : 0;
            case ShellKind.Cmd when name == "call":
                return IsValueWord(next, shell) ? AskSkip : 1;
            case ShellKind.Cmd:
                return name == "start" || (readThrough && name != "time" && !(name == "timeout" && WindowsTimeout(tokens, at))) ? AskSkip : 0;
        }

        if (name == "eval" || (name == "trap" && next is not ("-l" or "-p" or "-")) || (name == "alias" && tokens.Skip(at + 1).Any(t => t.Contains('=', StringComparison.Ordinal))))
        {
            return AskSkip;
        }

        return !readThrough || (name == "time" && slash < 0) ? 0 : ReadThrough(tokens, at, grammar!, env: name == "env");
    }

    /// <summary>Whether a <c>timeout</c> line is Windows' wait (<c>timeout /t 5 /nobreak</c>, <c>timeout 5</c>), which runs nothing.</summary>
    private static bool WindowsTimeout(List<string> tokens, int at) =>
        tokens.Skip(at + 1).All(t => t.ToLowerInvariant() is "/t" or "-t" or "/nobreak" or "-nobreak" || (t.TrimStart('-').Length > 0 && t.TrimStart('-').All(char.IsAsciiDigit)));

    /// <summary>
    /// The words a <see cref="ReadThroughRunners"/> runner at <paramref name="at"/> takes before its command, by <paramref name="grammar"/>: its
    /// options, its positionals, and for <c>env</c> its <c>NAME=value</c> words (a dangerous one, a quoted one or an <c>-u</c> of a dangerous
    /// name asks). <see cref="AskSkip"/> for an option the grammar doesn't know.
    /// </summary>
    private static int ReadThrough(List<string> tokens, int at, RunnerGrammar grammar, bool env)
    {
        int i = at + 1;
        while (i < tokens.Count)
        {
            string word = tokens[i];
            if (word == "--")
            {
                i++;
                break;
            }

            if (word.Length == 0 || word[0] != '-' || (word == "-" && !grammar.Flags.Contains(word)))
            {
                break;
            }

            string name = OptionName(word);
            if (grammar.Flags.Contains(name))
            {
                i++;
                continue;
            }

            if (grammar.Values.Contains(name))
            {
                bool glued = word.Length > name.Length;
                string? value = glued ? word[(name.Length + 1)..] : i + 1 < tokens.Count ? tokens[i + 1] : null;
                if (env && value is not null && IsDangerousName(value.Trim('"', '\'')))
                {
                    return AskSkip;   // env -u PATH: what the command's name resolves by
                }

                i += glued ? 1 : 2;
                continue;
            }

            if (word.Length > 2 && word[1] != '-')
            {
                // A short option glued to its value (-n5, -oL, -sKILL), short flags together (-dims), nice's -10.
                if (grammar.Values.Contains(word[..2]))
                {
                    if (env && IsDangerousName(word[2..].Trim('"', '\'')))
                    {
                        return AskSkip;
                    }

                    i++;
                    continue;
                }

                if (word.Skip(1).All(c => grammar.Flags.Contains("-" + c)) || (grammar.NumericOption && word.Skip(1).All(char.IsAsciiDigit)))
                {
                    i++;
                    continue;
                }
            }
            else if (word.Length == 2 && grammar.NumericOption && char.IsAsciiDigit(word[1]))
            {
                i++;
                continue;
            }

            return AskSkip;
        }

        i = Math.Min(tokens.Count, i + grammar.Positionals);
        while (env && i < tokens.Count && tokens[i].Contains('=', StringComparison.Ordinal))
        {
            // env sets every NAME=value before its command, whatever the shell: judged as a leading assignment is; a quoted one asks.
            if (QuoteKind(tokens[i][0], ShellKind.Bash) != '\0' || IsDangerousAssignment(tokens[i]))
            {
                return AskSkip;
            }

            i++;
        }

        return i - at;
    }

    /// <summary>Whether a word is a value rather than a name written out: a variable, a substitution, a glob, or cmd's <c>%x%</c>/<c>!x!</c>.</summary>
    private static bool IsValueWord(string word, ShellKind shell) =>
        word.IndexOfAny(shell == ShellKind.Cmd ? ['%', '!'] : ['$', '`', Hole, '*', '?', '[']) >= 0;

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
        // A PowerShell here-string's or a bash/zsh here-document's quotes are text up to a line of their own, which no scan here follows:
        // such a line asks (2026-10-19, the second shell review; an apostrophe in either hid the commands after it).
        unreadable |= shell == ShellKind.PowerShell ? HereStringPattern().IsMatch(text) : shell != ShellKind.Cmd && HereDocumentPattern().IsMatch(text);
        foreach (string segment in Segments(outer, shell))
        {
            // A PowerShell statement is read as one at every depth (the fourth shell review, 2026-10-19: at the top, $null = Remove-Item x was
            // the prefix $null, so an allowed $null let any right side run); at the top a value keeps Of's prefix ($x), never none.
            string? read = shell != ShellKind.PowerShell ? Of(segment, shell: shell)
                : depth > 0 ? StatementPrefix(segment)
                : StatementPrefix(segment) ?? Of(segment, shell: shell);
            if (read is not { Length: > 0 } prefix)
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
                    // A statement that opens with a (…), $(…) or @(…) is a value, unless it calls a method on it: (Get-Item src).Delete()
                    // ran with only get-item read (2026-10-19, the second shell review).
                    unreadable |= InvokesMethodPattern().IsMatch(segment);
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
    /// A PowerShell statement: <c>$x = …</c> is its right side's statement (typed, <c>[string]$s = …</c>, and several targets, <c>$a, $b =
    /// …</c>, too: measured to run it; one that sets a dangerous <c>$env:</c> variable asks); any other one opening with a value
    /// (<c>$_.Name -like '*.cs'</c>, <c>"x"</c>, <c>42</c>, <c>-not …</c>) runs none (null); the rest are <see cref="Of"/>'s.
    /// </summary>
    private static string? StatementPrefix(string segment)
    {
        string statement = segment.TrimStart();
        if (statement.Length == 0)
        {
            return null;
        }

        var assignment = AssignmentPattern().Match(statement);
        if (assignment.Success)
        {
            foreach (Match target in EnvTargetPattern().Matches(assignment.Groups["targets"].Value))
            {
                if (IsDangerousName(target.Groups["name"].Value))
                {
                    return Unreadable;   // $env:PATH = …; git status: the next segment's program is no longer the one allowed
                }
            }

            return StatementPrefix(statement[assignment.Length..]);
        }

        if (statement[0] == '$')
        {
            // A method call on a $ value runs code ($x.Delete(), $items.ForEach({…})): not a plain value, so it reads as Unreadable
            // rather than nothing (2026-10-19, the code review). A property read ($_.Length -gt 1) has no call and stays a value.
            return InvokesMethodPattern().IsMatch(statement) ? Unreadable : null;
        }

        return char.IsAsciiDigit(statement[0]) || QuoteKind(statement[0], ShellKind.PowerShell) != '\0' || statement[0] is '-' or '!' or '@' ? null : Of(statement);
    }

    // A method call on a value: .Name( or ::Name( — the paren is a substitution by the time this runs (replaced by Hole), so match
    // either. A bare property read ($_.Length, $_.Name -like …) has neither and stays a value. The name is any word up to the paren
    // (2026-10-19, the second shell review), so a quoted or variable one (.'Delete'(), .$m()) is a call too.
    [GeneratedRegex("(?:\\.|::)[^\\s.(\u0002]+[(\u0002]", RegexOptions.CultureInvariant)]
    private static partial Regex InvokesMethodPattern();

    // $name = , $a.b += , $x ??= … , [string]$s = , $a, [int]$b = : an assignment and the space after it; never == or -eq. A target is
    // casts (one level of nested brackets: [List[string]]) then $ and anything up to a blank, = or , (a subscript's $i, a Hole).
    [GeneratedRegex(@"^(?<targets>(?:\[(?:[^\[\]]|\[[^\[\]]*\])*\]\s*)*\$[^\s=,]+(?:\s*,\s*(?:\[(?:[^\[\]]|\[[^\[\]]*\])*\]\s*)*\$[^\s=,]+)*)\s*(?:[-+*/%]|\?\?)?=(?!=)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentPattern();

    // An assignment target on the env: drive: $env:NAME, ${env:NAME}, any case.
    [GeneratedRegex(@"\$\{?env:(?<name>[^\s}=,\[\]]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EnvTargetPattern();

    /// <summary>
    /// <paramref name="text"/> with every substitution <paramref name="shell"/> would run replaced by <see cref="Hole"/>, its body added
    /// to <paramref name="bodies"/>; <paramref name="unreadable"/> when one is left open, or (PowerShell) a call operator runs a value
    /// (<c>&amp; $x</c>, <c>&amp; (…)</c>). Quotes and escapes are the shell's: nothing in single quotes, only <c>$(…)</c> (and bash's
    /// backticks) in double ones; a comment is passed over.
    /// </summary>
    private static string Substitutions(string text, ShellKind shell, List<string> bodies, out bool unreadable)
    {
        unreadable = false;
        if (shell == ShellKind.Cmd || text.IndexOfAny(['$', '@', '(', '{', '`', '&', '#']) < 0)
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
                quote = QuoteKind(c, shell) == '\'' ? '\0' : quote;
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

            if (quote == '\0' && QuoteKind(c, shell) is var kind and not '\0')
            {
                quote = kind;
                outer.Append(c);
                continue;
            }

            if (quote == '"' && QuoteKind(c, shell) == '"')
            {
                quote = '\0';
                outer.Append(c);
                continue;
            }

            if (quote == '\0' && CommentAt(text, i, shell, escapedAt, out int commentEnd) is var comment and not Comment.None)
            {
                if (comment == Comment.Opens)
                {
                    outer.Append(text, i, commentEnd - i);
                    i = commentEnd - 1;
                    continue;
                }

                // Read as code, as JoinedSegments reads it; a quote in it may be a comment's text to PowerShell, and then the two
                // readings part, so the line asks (2026-10-19, the second shell review).
                unreadable |= HasQuote(text, i, commentEnd, shell);
            }

            if (powerShell && quote == '\0' && (c == '&' || c == '.') && CallsAValue(text, i))
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
        bool ansi = false;
        int escapedAt = -1;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (ansi)
            {
                // $'…': a backslash escapes, so a \' (or a \)) inside never ends it or the body (the fourth shell review).
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '\'')
                {
                    ansi = false;
                    quote = '\0';
                }

                continue;
            }

            if (quote != '\'' && c == escape)
            {
                escapedAt = ++i;
                continue;
            }

            if (quote != '\0')
            {
                quote = QuoteKind(c, shell) == quote ? '\0' : quote;
                continue;
            }

            if (OpensAnsiC(text, i, shell))
            {
                ansi = true;
                quote = '\'';
                i++;
            }
            else if (CommentAt(text, i, shell, escapedAt, out int commentEnd) == Comment.Opens)
            {
                // A comment's parens and quotes are text (2026-10-19, the second shell review): the scans agree on where a body ends.
                i = commentEnd - 1;
            }
            else if (QuoteKind(c, shell) is var kind and not '\0')
            {
                quote = kind;
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

            if (quote == '\0' && OpensAnsiC(text, i, shell))
            {
                // $'…' is text whose \' does not close it (the fourth shell review): passed over whole.
                for (i += 2; i < text.Length && text[i] != '\''; i++)
                {
                    i += text[i] == '\\' ? 1 : 0;
                }

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

    /// <summary>
    /// PowerShell's call operator or dot-source operator on a value (<c>&amp; $x</c>, <c>&amp; (…)</c>, <c>. $x</c>, <c>.$x</c>, <c>&amp;
    /// "$dir\tool"</c>): the command it runs is whatever the value holds. Not <c>&amp;&amp;</c>; a <c>.</c> only where a statement starts
    /// (the fourth shell review, 2026-10-19: <c>. $x</c> read as the prefix <c>.</c>, so an allowed <c>. .\script.ps1</c> let any value
    /// run), never a member's or a range's dot.
    /// </summary>
    private static bool CallsAValue(string text, int at)
    {
        if (text[at] == '&' && ((at > 0 && text[at - 1] == '&') || (at + 1 < text.Length && text[at + 1] == '&')))
        {
            return false;
        }

        if (text[at] == '.' && (!AtStatementStart(text, at) || (at + 1 < text.Length && text[at + 1] == '.')))
        {
            return false;
        }

        int next = at + 1;
        while (next < text.Length && (text[next] == ' ' || text[next] == '\t'))
        {
            next++;
        }

        if (next >= text.Length)
        {
            return false;
        }

        char value = text[next];
        if (value is '$' or '(' || (value == '@' && next + 1 < text.Length && text[next + 1] == '('))
        {
            return true;
        }

        if (QuoteKind(value, ShellKind.PowerShell) != '"')
        {
            return false;
        }

        // A double-quoted name with a variable in it is a value too; an escaped $ is text. A string left open asks.
        for (int i = next + 1; i < text.Length; i++)
        {
            if (text[i] == '`')
            {
                i++;
            }
            else if (text[i] == '$')
            {
                return true;
            }
            else if (QuoteKind(text[i], ShellKind.PowerShell) == '"')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether only blanks stand between a statement's start (the text's, a line's, a separator, a <c>(</c>, <c>{</c>, <c>=</c> or <c>,</c>) and <paramref name="at"/>.</summary>
    private static bool AtStatementStart(string text, int at)
    {
        int i = at - 1;
        while (i >= 0 && (text[i] == ' ' || text[i] == '\t'))
        {
            i--;
        }

        return i < 0 || text[i] is ';' or '|' or '&' or '(' or '{' or '=' or ',' or '\n' or '\r';
    }

    // The characters that end a command's name in cmd besides a blank (a space, a tab, and the VT, FF and no-break space Tokens splits at as
    // white space): cmd's documented delimiters , ; = (2026-10-20, the code review: ; was missing, so a word with text glued after a ; in a
    // segment of its own read as one program name; the segmenter cuts at a bare ; first, so only Of's own callers met it).
    private static readonly char[] CmdNameEnds = ['=', ',', ';'];

    /// <summary>
    /// A token as a program name: the quotes off, one of <see cref="ExecutableExtensions"/> off, lower case, and its folder kept with
    /// <c>/</c> for either separator (later on 2026-10-19, the third shell review; stripped until then, so a model's own <c>.\git.cmd</c>
    /// was <c>git</c>). <c>Foo.EXE</c> is <c>foo</c>, <c>./build.sh</c> and <c>.\build.ps1</c> are <c>./build</c>,
    /// <c>"C:\Tools\Foo.EXE"</c> is <c>c:/tools/foo</c>.
    /// </summary>
    public static string Program(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        string name = token.Trim().Trim('"', '\'');
        int cut = name.LastIndexOfAny(['\\', '/']);
        string folder = cut >= 0 ? name[..(cut + 1)].Replace('\\', '/') : "";
        string file = cut >= 0 ? name[(cut + 1)..] : name;
        foreach (string extension in ExecutableExtensions)
        {
            if (file.Length > extension.Length && file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                file = file[..^extension.Length];
                break;
            }
        }

        return (folder + file).ToLowerInvariant();
    }

    /// <summary>
    /// Whether a bash or zsh word is <c>NAME=value</c> or <c>NAME+=value</c> (the append, the fourth shell review: read as the program
    /// <c>x+=1</c> until then, so one approved <c>X+=1 ls</c> let <c>X+=1 &lt;anything&gt;</c> run).
    /// </summary>
    private static bool IsAssignment(string token)
    {
        int eq = token.IndexOf('=');
        if (eq <= 0 || token[0] == '"' || token[0] == '\'')
        {
            return false;
        }

        eq -= token[eq - 1] == '+' && eq > 1 ? 1 : 0;
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
        bool ansi = false;
        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            if (ansi)
            {
                // $'…' as JoinedSegments reads it: a backslash escapes, so \' never closes it.
                current.Append(c);
                if (c == '\\' && i + 1 < segment.Length)
                {
                    current.Append(segment[++i]);
                }
                else if (c == '\'')
                {
                    ansi = false;
                    quote = '\0';
                }

                continue;
            }

            if (c == escape && i + 1 < segment.Length && quote != '\'' && (shell != ShellKind.Cmd || quote == '\0'))
            {
                current.Append(c).Append(segment[++i]);
                continue;
            }

            if (quote == '\0' && OpensAnsiC(segment, i, shell))
            {
                current.Append(c).Append(segment[++i]);
                quote = '\'';
                ansi = true;
                continue;
            }

            if (quote != '\0')
            {
                current.Append(c);
                if (QuoteKind(c, shell) == quote)
                {
                    quote = '\0';
                }
            }
            else if (QuoteKind(c, shell) is var kind and not '\0')
            {
                quote = kind;
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
