using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;

namespace NeonSidekick.Llm;

/// <summary>
/// The single-agent turn loop: stream one model response, run whatever tools it asked for, feed
/// the results back, repeat — at most <see cref="MaxToolIterations"/> times — yielding text as it
/// arrives. Hand-written rather than <c>FunctionInvokingChatClient</c> so every branch is visible
/// and the loop stays free of reflection.
///
/// <para>Rules this loop keeps:</para>
/// <list type="bullet">
/// <item>Every tool result goes back as a <see cref="FunctionResultContent"/> carrying the
/// originating <c>CallId</c> — on the unknown-tool, bad-arguments and exception paths too. A call
/// without a result makes the server reject the next request.</item>
/// <item>Calls are collected from <em>every</em> assistant message in the response, not the
/// first. A reasoning model routinely thinks in one message and calls in the next.</item>
/// <item>The turn budget is a deadline checked at the loop boundary, never a linked
/// <see cref="CancellationTokenSource"/>: cancelling the token would surface as
/// <see cref="OperationCanceledException"/>, which means barge-in on the voice path.</item>
/// <item>An empty tool list is sent as <c>null</c>, not <c>[]</c>; several local servers reject
/// an empty <c>tools</c> array.</item>
/// <item>A picture a tool fetched (<see cref="Tools.ViewImageTool"/>, a <see cref="Tools.ToolImageResult"/>)
/// goes in a user-role <em>carrier</em> message right after the iteration's tool results
/// (<see cref="ConversationHistory.AddToolImages"/>): an OpenAI-format tool message is text only,
/// and the adapter drops any image part in one. The result text itself still carries the <c>CallId</c>.</item>
/// </list>
/// </summary>
public sealed class Assistant
{
    /// <summary>
    /// The default persona: the one part of the prompt <c>persona.md</c> (<see cref="PersonaFile"/>)
    /// replaces, and what <c>/persona</c> seeds that file with. Since 2026-10-03 (the user's ask) it is the repo's
    /// <c>assets/prompts/persona.md</c>, embedded by the project file as <see cref="DefaultPersonaResourceName"/> and read
    /// once: CRLF folded to LF and trimmed, as <see cref="PersonaFile.Normalize(string)"/> reads a file, so a seeded
    /// <c>persona.md</c> reads back as this text. It was one identity sentence before, joined to the rules with a space;
    /// a persona of headed sections is its own block now (<see cref="SystemPrompt(bool, IReadOnlyList{string}?, string?, string?, string?, bool, bool, bool, AskLimits?, ProjectNotes?, IReadOnlyList{Skills.Skill}?, bool)"/>).
    /// It must stay under <see cref="PersonaFile.MaxLength"/>, or a seeded file is cut; a test checks. The voice directive
    /// is appended <em>last</em> so it wins against anything here.
    /// </summary>
    public static readonly string DefaultPersona = ReadDefaultPersona();

    /// <summary>The manifest name <c>assets/prompts/persona.md</c> is embedded under.</summary>
    public const string DefaultPersonaResourceName = "prompts/persona.md";

    private static string ReadDefaultPersona()
    {
        using var stream = typeof(Assistant).Assembly.GetManifestResourceStream(DefaultPersonaResourceName)
            ?? throw new InvalidOperationException("The default persona " + DefaultPersonaResourceName + " is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return PersonaFile.Normalize(reader.ReadToEnd());
    }

    /// <summary>
    /// The one operating rule that names no tool: the whole of the default rules while the
    /// setting <c>LLM offer tools</c> is off (<see cref="PlainTextRule"/>), the first
    /// sentence of <see cref="OperatingRules"/> otherwise. Pinned.
    /// </summary>
    public const string PlainTextRule = "Reply in plain text: no Markdown headings or tables, and no code fences unless the user asks for code.";

    /// <summary>
    /// The sentence that takes <see cref="PlainTextRule"/>'s place while the transcript renders
    /// Markdown (the setting <c>Transcript markdown</c> on, the pane on the screen, the turn not
    /// spoken; 2026-09-16): light Markdown the styled transcript shows well, no headings or tables
    /// unasked. A custom <c>operata.md</c> stands verbatim either way. Pinned.
    /// </summary>
    public const string MarkdownRule = "Reply in light Markdown: **bold** for emphasis, a bulleted or numbered list where one helps, and a fenced code block with its language for code; no headings or tables unless the user asks for them.";

    /// <summary>The reply-format sentence for a turn: <see cref="MarkdownRule"/> or <see cref="PlainTextRule"/>.</summary>
    public static string TextRule(bool markdown) => markdown ? MarkdownRule : PlainTextRule;

    /// <summary>
    /// The tool, clock and timer sentences of the default rules — everything of
    /// <see cref="OperatingRulesWithoutFiles"/> after its reply-format sentence:
    /// <see cref="ToolRulesWithoutTimers"/> and <see cref="TimerRule"/> (byte-identical to the one const
    /// they were until 2026-09-20). Pinned.
    /// </summary>
    public const string ToolRules = ToolRulesWithoutTimers + " " + TimerRule;

    /// <summary>
    /// The tool and clock sentences of <see cref="ToolRules"/> without its timer sentence: the default
    /// rules while no timer tool is offered (2026-09-20) — headless, which has no timers since nothing
    /// could ring the alert, and the pane with all three timer rows switched off on <c>/tools</c> (the
    /// group emptied drops its rule, as every other group does). Pinned.
    /// </summary>
    public const string ToolRulesWithoutTimers =
        "Use a tool when it helps; otherwise answer directly. " +
        "You do not know the current date or time; call " + NeonSidekick.Llm.Tools.GetCurrentTimeTool.ToolName + " when a question depends on it, " +
        "and use " + NeonSidekick.Llm.Tools.ShiftDateTool.ToolName + " or " + NeonSidekick.Llm.Tools.DateDifferenceTool.ToolName + " for calendar arithmetic instead of counting yourself.";

    /// <summary>
    /// The timer sentence of <see cref="ToolRules"/>: in the default rules while any timer tool is offered,
    /// gone with them (<see cref="ToolRulesWithoutTimers"/>); a custom <c>operata.md</c> stands verbatim
    /// either way. Pinned.
    /// </summary>
    public const string TimerRule =
        "For a countdown, use " + NeonSidekick.Llm.Tools.StartTimerTool.ToolName + ", " + NeonSidekick.Llm.Tools.StopTimerTool.ToolName + " and " + NeonSidekick.Llm.Tools.ListTimersTool.ToolName + "; never guess what is left on a timer.";

    /// <summary>
    /// The manual's sentence (2026-10-02): in the default rules after the clock, timer and file sentences (so <see cref="OperatingRules"/> stays their start) while <c>neon_help</c> is
    /// offered (<see cref="NeonSidekick.Llm.Tools.NeonHelpTool"/>), gone when <c>/tools</c> switches it off; a custom
    /// <c>operata.md</c> stands verbatim either way. It names the app, since a model knows nothing of it. Pinned.
    /// </summary>
    public const string HelpRule =
        "You run inside NeonSidekick, a terminal chat app. When the user asks how to do something in it (a slash command, a setting, a key, a pane), " +
        "call " + NeonSidekick.Llm.Tools.NeonHelpTool.ToolName + " and answer from what it returns; never invent a command or setting name, and give a setting's place as pane › tab › row.";

    /// <summary>
    /// The default operating rules for a turn without the file tools (the setting <c>File tools</c>
    /// off, 2026-09-15): the plain-text, tool, clock and timer sentences — <see cref="OperatingRules"/>
    /// less <see cref="FileRule"/>. Nothing stands in for the working directory. Pinned.
    /// </summary>
    public const string OperatingRulesWithoutFiles = PlainTextRule + " " + ToolRules;

    /// <summary>
    /// The two sentences of <see cref="OperatingRules"/> that name the file tools and the working
    /// directory: in the default rules while the setting <c>File tools</c> is on (the file tools
    /// offered), gone with them; a custom <c>operata.md</c> stands verbatim either way. Its <c>delete</c> clause
    /// says the truth: a file or a folder goes for good (<c>FileRuleDeleteInPlace</c>'s words since 2026-09-20, under
    /// <c>File safe edits</c> off; the only form since 2026-10-01, when that setting, its <c>.trash</c> and <c>restore</c>
    /// went, the user's call). It names no trash and no setting (2026-09-21, the user's ask: told <c>File safe edits is
    /// off</c>, the model reasoned about a switch it cannot reach). Pinned.
    /// </summary>
    public const string FileRule =
        "The user's working directory — also called the cwd, the current directory or the current working directory — is a folder on this computer where you may read, search, write and organise files with the file tools " +
        "(" + NeonSidekick.Llm.Tools.GetWorkingDirectoryTool.ToolName + " gives its path); every path you pass is relative to it and the file tools reach nothing outside it; " +
        NeonSidekick.Llm.Tools.DeleteTool.ToolName + " removes a file or a folder for good, with everything in it. " +
        "To look at a picture (png, jpg, gif, webp, bmp) in the working directory call " + NeonSidekick.Llm.Tools.ViewImageTool.ToolName + " (several at once with " + NeonSidekick.Llm.Tools.ViewImageTool.PathsArgument + "); " + NeonSidekick.Llm.Tools.ReadFileTool.ToolName + " cannot read one.";

    /// <summary>
    /// <see cref="FileRule"/> without its <c>delete</c> clause: the default rules while
    /// <c>delete</c> is switched off on <c>/tools</c> (2026-09-20 — off in a fresh profile until later on
    /// 2026-09-21, when the user asked for it on out of the box), the <see cref="DownloadRule"/> shape; <see cref="OperatingRules"/> stays
    /// byte-identical. Pinned.
    /// </summary>
    public const string FileRuleWithoutDelete =
        "The user's working directory — also called the cwd, the current directory or the current working directory — is a folder on this computer where you may read, search, write and organise files with the file tools " +
        "(" + NeonSidekick.Llm.Tools.GetWorkingDirectoryTool.ToolName + " gives its path); every path you pass is relative to it and the file tools reach nothing outside it. " +
        "To look at a picture (png, jpg, gif, webp, bmp) in the working directory call " + NeonSidekick.Llm.Tools.ViewImageTool.ToolName + " (several at once with " + NeonSidekick.Llm.Tools.ViewImageTool.PathsArgument + "); " + NeonSidekick.Llm.Tools.ReadFileTool.ToolName + " cannot read one.";

    /// <summary>
    /// The operating rules that follow the persona, default or custom: <see cref="OperatingRulesWithoutFiles"/>
    /// and <see cref="FileRule"/> (byte-identical to the one const they were until 2026-09-15). The date
    /// sentence is load-bearing: without it a small model states a date from its training data.
    /// </summary>
    public const string OperatingRules = OperatingRulesWithoutFiles + " " + FileRule;

    /// <summary>
    /// The sentence the default rules gain while the setting <c>Web tools</c> is on (the web tools
    /// offered): appended after <see cref="OperatingRules"/> by <see cref="SystemPrompt(bool, IReadOnlyList{string}?, string?, string?, string?, bool, bool, bool, AskLimits?)"/>;
    /// a custom <c>operata.md</c> stands verbatim and names the tools itself or not at all. Pinned.
    /// </summary>
    public const string WebRule =
        "For anything on the web — current facts, news, prices, documentation, a page the user names — search with " + NeonSidekick.Llm.Tools.WebSearchTool.ToolName +
        " and read a page with " + NeonSidekick.Llm.Tools.WebFetchTool.ToolName + " (" + NeonSidekick.Llm.Tools.WebFetchTool.OffsetArgument + " continues a long one); say what you looked at. " +
        "To open a page for the user in their browser — when they ask to open, show or see a link rather than have it read out — call " + NeonSidekick.Llm.Tools.OpenUrlTool.ToolName + ".";

    /// <summary>
    /// The sentence the default rules gain while the web tools AND the file tools are offered
    /// (2026-09-18, <c>download_file</c> rides the web list only then): appended after <see cref="WebRule"/>
    /// by <see cref="DefaultRules"/>; a custom <c>operata.md</c> stands verbatim. Pinned.
    /// </summary>
    public const string DownloadRule =
        "To save a file or a picture from the web into the working directory call " + NeonSidekick.Llm.Tools.DownloadFileTool.ToolName +
        " with its URL (and a " + NeonSidekick.Llm.Tools.DownloadFileTool.PathArgument + " when the user names one); it saves without reading — " +
        NeonSidekick.Llm.Tools.ViewImageTool.ToolName + " or " + NeonSidekick.Llm.Tools.ReadFileTool.ToolName + " look at the result.";

    /// <summary>
    /// The sentence the default rules gain while the git tools are offered (the setting <c>GitLib tools</c> on,
    /// 2026-09-20): appended after <see cref="DownloadRule"/>, with the sandbox's sentences, by <see cref="DefaultRules"/>.
    /// It names nine of the tools a fresh profile offers and neither of the two that lose work (<c>gitlib_discard</c>,
    /// on out of the box since 2026-09-23, and <c>gitlib_delete</c>, off by name in <c>ToolsDisabled</c>), so no variant is needed whichever is off; a custom
    /// <c>operata.md</c> stands verbatim. Pinned.
    /// </summary>
    public const string GitRule =
        "The working directory may be a git repository (or hold one): " + NeonSidekick.Llm.Tools.GitStatusTool.ToolName + " shows its state, " +
        NeonSidekick.Llm.Tools.GitLogTool.ToolName + " its history, " + NeonSidekick.Llm.Tools.GitShowTool.ToolName + " a commit or a file at a commit, " +
        NeonSidekick.Llm.Tools.GitDiffTool.ToolName + " the changes, " + NeonSidekick.Llm.Tools.GitBlameTool.ToolName + " who wrote a line; " +
        NeonSidekick.Llm.Tools.GitStageTool.ToolName + ", " + NeonSidekick.Llm.Tools.GitCommitTool.ToolName + ", " + NeonSidekick.Llm.Tools.GitBranchTool.ToolName + " and " + NeonSidekick.Llm.Tools.GitStashTool.ToolName +
        " change it — commit only what the user asked to commit, with the message they gave or a short imperative one, and never remove or overwrite work the user did not name.";

    /// <summary>
    /// The sentence the default rules gain while the vault tools are offered (the setting <c>Obsidian tools</c> on and
    /// <c>Obsidian vault</c> naming a vault, 2026-09-22): appended after the shell sentence by <see cref="DefaultRules"/>.
    /// It says the vault is the user's notes, that a note is named as Obsidian names it, that a rename goes through
    /// <c>vault_move</c> (the file tools would break every link to it), and that the app's own folder is not the
    /// model's; a custom <c>operata.md</c> stands verbatim. Pinned.
    /// </summary>
    public const string ObsidianRule =
        "The user's Obsidian vault holds their notes: name a note as Obsidian does (its name, a [[wikilink]] or its path in the vault); " +
        NeonSidekick.Llm.Tools.VaultSearchTool.ToolName + " and " + NeonSidekick.Llm.Tools.VaultListTool.ToolName + " find notes, " +
        NeonSidekick.Llm.Tools.VaultReadTool.ToolName + " reads one, " + NeonSidekick.Llm.Tools.VaultLinksTool.ToolName + " shows its links and backlinks, " +
        NeonSidekick.Llm.Tools.VaultDailyTool.ToolName + " opens a day's daily note; " +
        NeonSidekick.Llm.Tools.VaultWriteTool.ToolName + " and " + NeonSidekick.Llm.Tools.VaultPropertiesTool.ToolName + " change notes — write Obsidian Markdown ([[links]], #tags) — and " +
        NeonSidekick.Llm.Tools.VaultMoveTool.ToolName + " renames or moves one with its links kept; the vault's .obsidian folder belongs to Obsidian, so leave it alone.";

    /// <summary>
    /// The sentence <see cref="ObsidianRule"/> gains while <c>vault_delete</c> is offered (later on 2026-09-22: the setting
    /// <c>Obsidian allow delete (.trash)</c> on, on by default since 2026-09-23, and the tool not switched off): what the delete does and that it
    /// waits for the user's word. Appended right after the vault sentence by <see cref="DefaultRules"/>. Pinned.
    /// </summary>
    public const string ObsidianDeleteRule =
        NeonSidekick.Llm.Tools.VaultDeleteTool.ToolName + " moves a note or attachment into the vault's .trash, where Obsidian can restore it; use it only when the user asks for a deletion.";

    /// <summary>
    /// The sentence the default rules gain while the SQL tools are offered (the setting <c>SQL tools</c> on and a
    /// connection in <c>sql.json</c>, 2026-09-23): appended after the vault sentences by <see cref="DefaultRules"/>.
    /// It says the dialect is T-SQL, that the tools only read, the order a question is worked in (find the table or the
    /// column, describe it, follow the keys, then one bounded SELECT; the indexes for a performance question — both
    /// named later that day) and that values go in as parameters. Pinned.
    /// </summary>
    public const string SqlRule =
        "The SQL tools read SQL Server (T-SQL: TOP, not LIMIT) on the user's named connections and never change data: " +
        NeonSidekick.Llm.Tools.SqlConnectionsTool.ToolName + " lists the connections and " + NeonSidekick.Llm.Tools.SqlDatabasesTool.ToolName + " a server's databases; " +
        NeonSidekick.Llm.Tools.SqlTablesTool.ToolName + " finds a table and " + NeonSidekick.Llm.Tools.SqlColumnsTool.ToolName + " a column, " +
        NeonSidekick.Llm.Tools.SqlDescribeTool.ToolName + " shows a table's columns, keys and constraints, " + NeonSidekick.Llm.Tools.SqlRelationshipsTool.ToolName + " the joins and " +
        NeonSidekick.Llm.Tools.SqlIndexesTool.ToolName + " the indexes with their use — look before you query, never guess a column; " +
        NeonSidekick.Llm.Tools.SqlQueryTool.ToolName + " runs one SELECT per call, kept small with WHERE and TOP, values bound as @name through params. " +
        "Reach SQL Server only through these tools — never a client or a script in the shell, which are refused.";

    /// <summary>
    /// The sentence the default rules gain while the Oracle tools are offered (the setting <c>Oracle tools</c> on and a
    /// connection in <c>oracle.json</c>, 2026-09-30): appended after <see cref="SqlRule"/> by <see cref="DefaultRules"/>, its
    /// twin — the dialect (FETCH FIRST, not TOP or LIMIT; <c>:name</c> binds; no trailing semicolon), that the tools only read,
    /// the order a question is worked in, and that unquoted names are upper case. Pinned.
    /// </summary>
    public const string OracleRule =
        "The Oracle tools read Oracle databases (Oracle SQL: FETCH FIRST n ROWS ONLY, not TOP or LIMIT; no trailing semicolon; unquoted names are upper case) on the user's named connections and never change data: " +
        NeonSidekick.Llm.Tools.OracleConnectionsTool.ToolName + " lists the connections and " + NeonSidekick.Llm.Tools.OracleSchemasTool.ToolName + " a database's schemas; " +
        NeonSidekick.Llm.Tools.OracleTablesTool.ToolName + " finds a table and " + NeonSidekick.Llm.Tools.OracleColumnsTool.ToolName + " a column, " +
        NeonSidekick.Llm.Tools.OracleDescribeTool.ToolName + " shows a table's columns, keys and constraints, " + NeonSidekick.Llm.Tools.OracleRelationshipsTool.ToolName + " the joins and " +
        NeonSidekick.Llm.Tools.OracleIndexesTool.ToolName + " the indexes — look before you query, never guess a column; " +
        NeonSidekick.Llm.Tools.OracleQueryTool.ToolName + " runs one SELECT per call, kept small with WHERE and FETCH FIRST, values bound as :name through params. " +
        "Reach Oracle only through these tools — never a client or a script in the shell, which are refused.";

    /// <summary>
    /// The sentence the default rules gain while the MySQL tools are offered (the setting <c>MySQL tools</c> on and a connection in
    /// <c>mysql.json</c>, 2026-09-30): appended after <see cref="OracleRule"/> by <see cref="DefaultRules"/>, its twin — the dialect
    /// (LIMIT, backticks, <c>@name</c> binds), that the tools only read, and the order a question is worked in. Pinned.
    /// </summary>
    public const string MySqlRule =
        "The MySQL tools read MySQL and MariaDB databases (MySQL SQL: LIMIT n, not TOP; `backticks` for names) on the user's named connections and never change data: " +
        NeonSidekick.Llm.Tools.MySqlConnectionsTool.ToolName + " lists the connections and " + NeonSidekick.Llm.Tools.MySqlDatabasesTool.ToolName + " a server's databases; " +
        NeonSidekick.Llm.Tools.MySqlTablesTool.ToolName + " finds a table and " + NeonSidekick.Llm.Tools.MySqlColumnsTool.ToolName + " a column, " +
        NeonSidekick.Llm.Tools.MySqlDescribeTool.ToolName + " shows a table's columns, keys and constraints, " + NeonSidekick.Llm.Tools.MySqlRelationshipsTool.ToolName + " the joins and " +
        NeonSidekick.Llm.Tools.MySqlIndexesTool.ToolName + " the indexes — look before you query, never guess a column; " +
        NeonSidekick.Llm.Tools.MySqlQueryTool.ToolName + " runs one SELECT per call, kept small with WHERE and LIMIT, values bound as @name through params. " +
        "Reach MySQL only through these tools — never a client or a script in the shell, which are refused.";

    /// <summary>
    /// The sentence the default rules gain while the SQLite tools are offered (the setting <c>SQLite tools</c> on and a database named
    /// in <c>sqlite.json</c> or the sandbox's files allowed, 2026-10-04): appended after <see cref="MySqlRule"/> by
    /// <see cref="DefaultRules"/>, its twin — the dialect, that the tools only read, and the order a question is worked in. Pinned.
    /// </summary>
    public const string SqliteRule =
        "The SQLite tools read SQLite database files (SQLite SQL: LIMIT n; \"double quotes\" for names) — the user's named databases, or a file in the working directory by its path — and never change data: " +
        NeonSidekick.Llm.Tools.SqliteDatabasesTool.ToolName + " lists the databases and " + NeonSidekick.Llm.Tools.SqliteTablesTool.ToolName + " a file's tables; " +
        NeonSidekick.Llm.Tools.SqliteDescribeTool.ToolName + " shows a table's columns, keys and indexes — look before you query, never guess a column; " +
        NeonSidekick.Llm.Tools.SqliteQueryTool.ToolName + " runs one SELECT per call, kept small with WHERE and LIMIT, values bound as @name through params. " +
        "Reach SQLite only through these tools — never the shell, a script or the file tools, which are refused.";

    /// <summary>
    /// The sentence after <see cref="SqliteRule"/> while <c>sqlite_execute</c> is offered (2026-10-05, <c>SQLite mode</c>
    /// <c>read-write</c>), <see cref="DockerWriteRule"/>'s twin: the one way to change a file or make one, each change waiting for
    /// the user's allow, so change only what was asked, one statement at a time after a look, and never retry a declined one. Pinned.
    /// </summary>
    public const string SqliteWriteRule =
        "The user has allowed changes too: " + NeonSidekick.Llm.Tools.SqliteExecuteTool.ToolName + " runs one statement per call, only of the kinds its description lists " +
        "(with creating among them, create makes a new database file in the working directory); each change waits for the user's allow and is permanent once run. " +
        "Change only what the user asks for, describe a table before changing it, say what you changed, and do not retry one they decline.";

    /// <summary>
    /// The sentence after <see cref="MySqlRule"/> while <c>mysql_execute</c> is offered (2026-10-05, <c>MySQL mode</c> <c>read-write</c> and a
    /// <c>readwrite</c> connection), <see cref="PostgresWriteRule"/>'s twin. Pinned.
    /// </summary>
    public const string MySqlWriteRule =
        "The user has allowed changes too: " + NeonSidekick.Llm.Tools.MySqlExecuteTool.ToolName + " runs one statement per call on a connection the user has opened to changes, " +
        "only of the kinds its description lists; each change waits for the user's allow and is permanent once run. " +
        "Change only what the user asks for, describe a table before changing it, say what you changed, and do not retry one they decline.";

    /// <summary>
    /// The sentence after <see cref="SqlRule"/> while <c>sql_execute</c> is offered (2026-10-05, <c>SQL mode</c> <c>read-write</c> and a
    /// <c>readwrite</c> connection), <see cref="PostgresWriteRule"/>'s twin. Pinned.
    /// </summary>
    public const string SqlWriteRule =
        "The user has allowed changes too: " + NeonSidekick.Llm.Tools.SqlExecuteTool.ToolName + " runs one statement per call on a connection the user has opened to changes, " +
        "only of the kinds its description lists; each change waits for the user's allow and is permanent once run. " +
        "Change only what the user asks for, describe a table before changing it, say what you changed, and do not retry one they decline.";

    /// <summary>
    /// The sentence after <see cref="OracleRule"/> while <c>oracle_execute</c> is offered (2026-10-05, <c>Oracle mode</c> <c>read-write</c> and a
    /// <c>readwrite</c> connection), <see cref="PostgresWriteRule"/>'s twin. Pinned.
    /// </summary>
    public const string OracleWriteRule =
        "The user has allowed changes too: " + NeonSidekick.Llm.Tools.OracleExecuteTool.ToolName + " runs one statement per call on a connection the user has opened to changes, " +
        "only of the kinds its description lists; each change waits for the user's allow and is permanent once run. " +
        "Change only what the user asks for, describe a table before changing it, say what you changed, and do not retry one they decline.";

    /// <summary>
    /// The sentence after <see cref="PostgresRule"/> while <c>postgres_execute</c> is offered (2026-10-05, <c>PostgreSQL mode</c> <c>read-write</c> and a
    /// <c>readwrite</c> connection), <see cref="SqliteWriteRule"/>'s twin. Pinned.
    /// </summary>
    public const string PostgresWriteRule =
        "The user has allowed changes too: " + NeonSidekick.Llm.Tools.PostgresExecuteTool.ToolName + " runs one statement per call on a connection the user has opened to changes, " +
        "only of the kinds its description lists; each change waits for the user's allow and is permanent once run. " +
        "Change only what the user asks for, describe a table before changing it, say what you changed, and do not retry one they decline.";

    /// <summary>
    /// The sentence the default rules gain while the PostgreSQL tools are offered (the setting <c>PostgreSQL tools</c> on and a connection in
    /// <c>postgres.json</c>, 2026-10-04): appended after <see cref="SqliteRule"/> by <see cref="DefaultRules"/>, <see cref="MySqlRule"/>'s twin. Pinned.
    /// </summary>
    public const string PostgresRule =
        "The PostgreSQL tools read PostgreSQL databases (PostgreSQL SQL: LIMIT n; \"double quotes\" for mixed-case names; schema.table) on the user's named connections and never change data: " +
        NeonSidekick.Llm.Tools.PostgresConnectionsTool.ToolName + " lists the connections, " + NeonSidekick.Llm.Tools.PostgresDatabasesTool.ToolName + " a server's databases and " +
        NeonSidekick.Llm.Tools.PostgresSchemasTool.ToolName + " a database's schemas; " + NeonSidekick.Llm.Tools.PostgresTablesTool.ToolName + " finds a table and " +
        NeonSidekick.Llm.Tools.PostgresColumnsTool.ToolName + " a column, " + NeonSidekick.Llm.Tools.PostgresDescribeTool.ToolName + " shows a table's columns, keys and constraints, " +
        NeonSidekick.Llm.Tools.PostgresRelationshipsTool.ToolName + " the joins and " + NeonSidekick.Llm.Tools.PostgresIndexesTool.ToolName + " the indexes — look before you query, never guess a column; " +
        NeonSidekick.Llm.Tools.PostgresQueryTool.ToolName + " runs one SELECT per call, kept small with WHERE and LIMIT, values bound as @name through params. " +
        "Reach PostgreSQL only through these tools — never a client or a script in the shell, which are refused.";

    /// <summary>
    /// The sentence the default rules gain while the UNC tools are offered (the setting <c>UNC tools</c> on and a share in
    /// <c>unc.json</c>, 2026-09-30): appended after <see cref="MySqlRule"/> by <see cref="DefaultRules"/> — what the shares are, the
    /// read tools, how a path is given, and that a share is neither the working directory nor the shell's. <see cref="UncFetchRule"/>
    /// follows it while <c>unc_fetch</c> is offered, <see cref="UncWriteRule"/> while a change is. Since 2026-10-03 (the user's ask:
    /// "list my UNC shares" went to <c>net use</c>) it says these are the shares the user means and names the commands that are not. Pinned.
    /// </summary>
    public const string UncRule =
        "The unc_ tools reach the user's named network shares and outside folders, each signed in as the account the user set for it: " +
        NeonSidekick.Llm.Tools.UncSharesTool.ToolName + " lists them — where each points and whether it is read-only; when the user speaks of their shares or UNC shares, these are the ones, never net use, net share or net view; " +
        NeonSidekick.Llm.Tools.UncSearchTool.ToolName + " searches and lists a share as search_files does, " +
        NeonSidekick.Llm.Tools.UncInfoTool.ToolName + " and " + NeonSidekick.Llm.Tools.UncReadTool.ToolName + " look at a file; " +
        "a path is relative to the share named in share, or a full \\\\server\\share path under one. A share is not the working directory, and the shell cannot reach it.";

    /// <summary>The sentence after <see cref="UncRule"/> while <c>unc_fetch</c> is offered (the File tools on, 2026-09-30). Pinned.</summary>
    public const string UncFetchRule =
        NeonSidekick.Llm.Tools.UncFetchTool.ToolName + " copies a share's file into the working directory, where view_image, execute_code and the file tools can use it.";

    /// <summary>
    /// The sentence after <see cref="UncRule"/> while a change on a share is offered (<c>UNC writes</c> on and a <c>readwrite</c>
    /// share, 2026-09-30): that every change and delete there is permanent — a share has no trash, the user's call — so a share
    /// changes only at the user's word. Pinned.
    /// </summary>
    public const string UncWriteRule =
        "On a share marked read-write the unc_ tools also write, patch, create folders, move, copy, delete and put files there — permanently: nothing is kept and nothing can be undone, so change a share only when the user asks for that change.";

    /// <summary>
    /// The sentence the default rules gain while the shell tools are offered (the setting <c>Shell command
    /// policy</c> not <c>off</c>, 2026-09-21): appended after <see cref="GitRule"/> by <see cref="DefaultRules"/>. It
    /// says what the tool is for, that the sandbox is only where a command starts, that the user stands between
    /// the call and the shell, that a denial is final, and (phase B) how a background job and the process tool
    /// go together; a custom <c>operata.md</c> stands verbatim. Its last sentence, on <c>execute_code</c>, says the
    /// script can call the tools through <c>neon_tools</c> — only while the setting <c>Shell tool bridge</c> is on;
    /// off (later on 2026-09-21) the rules carry <see cref="ShellRuleWithoutBridge"/>, which does not, so the prompt
    /// never promises a module the run does not write. Since 2026-09-22 (the user's ask) the head follows the setting
    /// <c>Shell police</c> too: on (the default), it says the shell may only name paths under the working
    /// directory (<see cref="ShellRuleHeadPoliced"/>); off, it says only that a command starts there (<see cref="ShellRuleHeadUnpoliced"/>,
    /// the <c>…Unpoliced</c> variants) — until that day the head said a command "can reach the whole computer", and
    /// neither variant says so now, so the model does not try to leave unless asked. Pinned.
    /// </summary>
    public const string ShellRule = ShellRuleHeadPoliced + ShellRuleBridgeTail;

    /// <summary><see cref="ShellRule"/> with the bridge off: the same head, and <c>execute_code</c> runs a script that does everything itself. Pinned.</summary>
    public const string ShellRuleWithoutBridge = ShellRuleHeadPoliced + ShellRulePlainTail;

    /// <summary><see cref="ShellRule"/> with the setting <c>Shell police</c> off (2026-09-22): the head says a command starts in the working directory and no more. Pinned.</summary>
    public const string ShellRuleUnpoliced = ShellRuleHeadUnpoliced + ShellRuleBridgeTail;

    /// <summary><see cref="ShellRuleWithoutBridge"/> with the police off (2026-09-22): neither the bridge nor the confinement is named. Pinned.</summary>
    public const string ShellRuleWithoutBridgeUnpoliced = ShellRuleHeadUnpoliced + ShellRulePlainTail;

    /// <summary>What every shell rule opens with: <c>run_command</c> and its two picks.</summary>
    private const string ShellRuleOpening =
        "To run a program, a build, a test or a script the user asks for, call " + NeonSidekick.Llm.Tools.RunCommandTool.ToolName + " with the command line " +
        "(" + NeonSidekick.Llm.Tools.RunCommandTool.ShellArgument + " picks powershell, cmd or bash when the user's default will not do; " + NeonSidekick.Llm.Tools.RunCommandTool.WorkdirArgument + " a folder under the working directory); ";

    /// <summary>The head with the police on: the shell stays under the working directory, and a refused path is final like a denied command.</summary>
    private const string ShellRuleHeadPoliced = ShellRuleOpening +
        "it runs in the working directory and may only name paths under it (relative, or absolute under it), and the user approves each command before it runs and may deny it — " +
        "never retry or work around a denied or refused command, and say what you ran. " + ShellRuleProcess;

    /// <summary>The head with the police off: a command starts in the working directory; nothing said about where it may reach.</summary>
    private const string ShellRuleHeadUnpoliced = ShellRuleOpening +
        "it starts in the working directory, and the user approves each command before it runs and may deny it — " +
        "never retry or work around a denied command, and say what you ran. " + ShellRuleProcess;

    /// <summary>What every head closes with: the background job and <c>process</c>.</summary>
    private const string ShellRuleProcess =
        "For a server or a long job pass " + NeonSidekick.Llm.Tools.RunCommandTool.BackgroundArgument + " and use " + NeonSidekick.Llm.Tools.ProcessTool.ToolName + " to poll, read, wait for, write to or kill it; " +
        "with " + NeonSidekick.Llm.Tools.RunCommandTool.NotifyArgument + " you are told at your next turn when it exits. ";

    /// <summary>The <c>execute_code</c> sentence with the bridge on.</summary>
    private const string ShellRuleBridgeTail =
        "For a task with several steps or many tool calls, " + NeonSidekick.Llm.Tools.ExecuteCodeTool.ToolName + " runs a python, node or powershell script that can call these same tools through its neon_tools module and returns what it printed.";

    /// <summary>The <c>execute_code</c> sentence with the bridge off.</summary>
    private const string ShellRulePlainTail =
        "For a task with several steps, " + NeonSidekick.Llm.Tools.ExecuteCodeTool.ToolName + " runs a python, node or powershell script and returns what it printed.";

    /// <summary>The shell rule for a turn: the bridge picks the <c>execute_code</c> sentence, the police (2026-09-22) the head.</summary>
    public static string ShellRuleFor(bool bridge, bool police) => (bridge, police) switch
    {
        (true, true) => ShellRule,
        (false, true) => ShellRuleWithoutBridge,
        (true, false) => ShellRuleUnpoliced,
        (false, false) => ShellRuleWithoutBridgeUnpoliced,
    };

    /// <summary>
    /// The sentence the default rules gain after the shell rule while the setting <c>Shell prefer native tools</c> is on
    /// (2026-09-26, the user's ask: the model kept running <c>cat</c>, <c>dir</c>, <c>git status</c> or <c>curl</c> through
    /// <c>run_command</c> when a tool of its own did the job). It names only the groups offered that turn —
    /// <paramref name="files"/>, <paramref name="git"/>, <paramref name="web"/>, <paramref name="sql"/>, <paramref name="oracle"/> and <paramref name="mysql"/> (2026-09-30) — each with the shell
    /// words it replaces, since a small model follows a named word better than a principle; empty when none is, so a
    /// shell-only turn gains nothing. <c>run_command</c> backs it at the call (<see cref="Shell.NativeRedirect"/>). Pinned.
    /// </summary>
    public static string ShellNativeRule(bool files, bool git, bool web, bool sql, bool oracle = false, bool mysql = false, bool unc = false, bool sqlite = false, bool postgres = false)
    {
        var parts = new List<string>(6);
        if (files)
        {
            parts.Add(NeonSidekick.Llm.Tools.ReadFileTool.ToolName + " and " + NeonSidekick.Llm.Tools.SearchFilesTool.ToolName + " read, search and list files (not cat, type, Get-Content, dir, ls or grep) and the file tools write, copy, move and delete them");
        }

        if (git)
        {
            parts.Add("the gitlib_ tools look at and change the repository (not git status, log, diff, add or commit)");
        }

        if (web)
        {
            parts.Add(NeonSidekick.Llm.Tools.WebSearchTool.ToolName + " and " + NeonSidekick.Llm.Tools.WebFetchTool.ToolName + " reach the web (not curl or Invoke-WebRequest)");
        }

        if (sql)
        {
            parts.Add(NeonSidekick.Llm.Tools.SqlQueryTool.ToolName + " reads the databases (not sqlcmd)");
        }

        if (oracle)
        {
            parts.Add(NeonSidekick.Llm.Tools.OracleQueryTool.ToolName + " reads the Oracle databases (not sqlplus)");
        }

        if (mysql)
        {
            parts.Add(NeonSidekick.Llm.Tools.MySqlQueryTool.ToolName + " reads the MySQL databases (not mysql or mariadb)");
        }

        if (sqlite)
        {
            parts.Add(NeonSidekick.Llm.Tools.SqliteQueryTool.ToolName + " reads the SQLite files (not sqlite3)");
        }

        if (postgres)
        {
            parts.Add(NeonSidekick.Llm.Tools.PostgresQueryTool.ToolName + " reads the PostgreSQL databases (not psql)");
        }

        if (unc)
        {
            parts.Add("the unc_ tools list and reach the user's network shares (not net use, net share, net view, Get-SmbShare, dir \\\\server or copy \\\\server)");
        }

        return parts.Count == 0
            ? ""
            : "Call " + NeonSidekick.Llm.Tools.RunCommandTool.ToolName + " only for what no other tool does: " + string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// The sentence the default rules gain while <c>ask_user</c> is offered (the setting <c>Ask user</c>
    /// on, the bottom pane on): appended after <see cref="WebRule"/> by
    /// <see cref="SystemPrompt(bool, IReadOnlyList{string}?, string?, string?, string?, bool, bool, bool, AskLimits?)"/>;
    /// a custom <c>operata.md</c> stands verbatim. Quotes the caps the tool the model sees works under
    /// (<see cref="AskLimits"/>, the two Ask-tab rows; 2026-09-15). Pinned.
    /// </summary>
    public static string AskRule(AskLimits limits) =>
        "When you need the user to decide between a few possibilities — a choice, a preference, a clarification with a short list of answers — call " + AskUserTool.ToolName +
        " with the options and wait for the result instead of guessing or asking in prose; up to " + limits.MaxQuestions.ToString(CultureInfo.InvariantCulture) +
        " questions in one call, " + AskUserTool.MinOptions.ToString(CultureInfo.InvariantCulture) + " to " + limits.MaxChoices.ToString(CultureInfo.InvariantCulture) +
        " options each, and the user can type their own answer. " +
        "If the result says the questions were not answered, go on without them.";

    /// <summary>
    /// The sentence the default rules gain while <c>session_manager</c> is offered (the setting
    /// <c>Session tool</c> on, 2026-09-18): appended by <see cref="DefaultRules"/> after the ask
    /// sentence (last until <see cref="McpRule"/>, 2026-09-20); a custom <c>operata.md</c> stands verbatim. Pinned.
    /// </summary>
    public const string SessionRule =
        "Your earlier conversations with the user are stored: when they ask what was said, decided or done before, or refer to something you cannot see in this conversation, call " +
        NeonSidekick.Llm.Tools.SessionManagerTool.ToolName + " — " + NeonSidekick.Llm.Tools.SessionManagerTool.SearchAction + " with the words they remember, " +
        NeonSidekick.Llm.Tools.SessionManagerTool.ListAction + " for the newest, then " + NeonSidekick.Llm.Tools.SessionManagerTool.ReadAction + " a session by its id for the turns; never guess at them. " +
        "The user restores, renames or removes a session with /sessions, not you.";

    /// <summary>
    /// The sentence the default rules gain while the Home Assistant tools are offered (the setting <c>Home Assistant tools</c>
    /// on, a URL and a token set, 2026-09-28): appended after the SQL sentence by <see cref="DefaultRules"/>. It says look
    /// before acting and never guess an id, that a room goes to its group light, the order of the tools (the typed ones, then
    /// the generic call, Assist last), to say what changed, and that a declined call is not retried. Pinned.
    /// </summary>
    public const string HomeAssistantRule =
        "The ha_ tools reach the user's home through Home Assistant: look before you act — " + NeonSidekick.Llm.Tools.HaOverviewTool.ToolName + " for the house, " +
        NeonSidekick.Llm.Tools.HaStatesTool.ToolName + " for names, ids and a device's options, " + NeonSidekick.Llm.Tools.HaHistoryTool.ToolName + " for what happened — and never guess an entity id; when the user names a room, act on that room's group light. " +
        "Use " + NeonSidekick.Llm.Tools.HaLightsTool.ToolName + ", " + NeonSidekick.Llm.Tools.HaSceneTool.ToolName + ", " + NeonSidekick.Llm.Tools.HaMediaTool.ToolName + " and " + NeonSidekick.Llm.Tools.HaTodoTool.ToolName + " first, " +
        NeonSidekick.Llm.Tools.HaCallServiceTool.ToolName + " for any other service and " + NeonSidekick.Llm.Tools.HaAssistTool.ToolName + " as a last resort, and say in a few words what you changed. " +
        "Some calls wait for the user's approval; one they decline is not retried.";

    /// <summary>
    /// The sentence the default rules gain while the Docker tools are offered (the setting <c>Docker tools</c> on, 2026-10-02):
    /// appended after <see cref="HomeAssistantRule"/> by <see cref="DefaultRules"/> — what the tools reach, to look before saying
    /// and never guess a name, that the docker_ tools come before <c>docker</c> through the shell, and that a log may hold
    /// secrets, which are not to be repeated. <see cref="DockerWriteRule"/> follows it while a change is offered. Pinned.
    /// </summary>
    public const string DockerRule =
        "The docker_ tools reach the user's Docker Desktop: " + NeonSidekick.Llm.Tools.DockerContainersTool.ToolName + " lists the containers — use it to find a name, never guess one; " +
        NeonSidekick.Llm.Tools.DockerLogsTool.ToolName + ", " + NeonSidekick.Llm.Tools.DockerInspectTool.ToolName + " and " + NeonSidekick.Llm.Tools.DockerStatsTool.ToolName + " look at one, " +
        NeonSidekick.Llm.Tools.DockerResourcesTool.ToolName + " at the images, volumes, networks and disk, " + NeonSidekick.Llm.Tools.DockerComposeTool.ToolName + " at the compose projects. " +
        "Prefer them to running docker through the shell. Logs may hold passwords and other secrets: never repeat one.";

    /// <summary>
    /// The sentence after <see cref="DockerRule"/> while a change is offered (<c>Docker writes</c> on, 2026-10-02): that each change
    /// waits for the user's yes, so act only when asked, and that a declined one is not retried. Pinned.
    /// </summary>
    public const string DockerWriteRule =
        "With " + NeonSidekick.Llm.Tools.DockerLifecycleTool.ToolName + ", " + NeonSidekick.Llm.Tools.DockerPullTool.ToolName + " and the removing tools you may also change Docker, each call waiting for the user's yes: change it only when the user asks, say what you changed, and do not retry one they decline.";

    /// <summary>
    /// The sentence the default rules gain while <c>claude_advisor_cli</c> is offered (the setting <c>Claude CLI advisor tool</c> on,
    /// 2026-09-27): appended after the SQL sentence by <see cref="DefaultRules"/>. It says when (stuck, unsure of the best
    /// course — not for what a tool can look up), how (a self-contained question, the brief in context) and what to do with
    /// the answer (weigh it, decide, and never just relay it). Pinned.
    /// </summary>
    public const string ClaudeAdvisorRule =
        "When you are stuck or unsure of the best course, and your own tools cannot settle it, you may ask Claude, a stronger model, with " +
        NeonSidekick.Llm.Tools.ClaudeAdvisorTool.ToolName + ": one self-contained question, with what you tried and found in context. " +
        "It is slow and costs money, so use it sparingly. Weigh its advice and decide yourself; do not just pass it on.";

    /// <summary>
    /// The sentence the default rules gain while any MCP server's tools are offered (2026-09-20):
    /// appended after <see cref="SessionRule"/>, so the model knows the prefixed names are external and
    /// described by their servers, not by these rules. Pinned.
    /// </summary>
    public const string McpRule =
        "Tools named <server>" + NeonSidekick.Mcp.McpToolName.Separator + "<tool> belong to external MCP servers the user connected; each does what its own description says — " +
        "read it before calling, pass exactly the arguments its schema names, and answer from its result.";

    /// <summary>The system prompt with the default persona: the persona and the rules, each its own block (2026-10-03).</summary>
    public static readonly string DefaultSystemPrompt = DefaultPersona + "\n\n" + OperatingRules;

    /// <summary>The system prompt for a turn with every default: no voice directive either way, since there is no default one (2026-10-03).</summary>
    public static string SystemPrompt(bool speechOutput) => SystemPrompt(speechOutput, null);

    /// <summary>
    /// The default operating rules for a turn, the one composition behind <see cref="SystemPrompt(bool, IReadOnlyList{string}?, string?, string?, string?, bool, bool, bool, AskLimits?, ProjectNotes?, IReadOnlyList{Skills.Skill}?, bool)"/>
    /// and <c>/sys</c>: the reply-format sentence (<see cref="TextRule"/>), then with <paramref name="tools"/>
    /// the <see cref="ToolRules"/>, <see cref="FileRule"/> with <paramref name="files"/>, <see cref="WebRule"/>
    /// with <paramref name="web"/> (and <see cref="DownloadRule"/> with both, unless <paramref name="download"/> is false —
    /// <c>download_file</c> switched off by name on <c>/tools</c>, 2026-09-19), <see cref="AskRule"/> with <paramref name="ask"/>,
    /// <see cref="SessionRule"/> with <paramref name="sessions"/> and, last, <see cref="McpRule"/> with <paramref name="mcp"/> (2026-09-20). The file rule is
    /// <see cref="FileRuleWithoutDelete"/> with <paramref name="delete"/> false; the tool rules are
    /// <see cref="ToolRulesWithoutTimers"/> with <paramref name="timers"/> false (no timer tool offered — headless, or the
    /// Timers group emptied on <c>/tools</c>, 2026-09-20); <see cref="ShellRule"/> rides after the git sentence with
    /// <paramref name="shell"/> (the shell tools offered: <c>Shell command policy</c> not off, 2026-09-21), as
    /// <see cref="ShellRuleWithoutBridge"/> unless <paramref name="bridge"/> (the setting <c>Shell tool bridge</c>, off by
    /// default, later that day), and as the <c>…Unpoliced</c> variant with <paramref name="police"/> false (the setting <c>Shell police</c> off, 2026-09-22; <see cref="ShellRuleFor"/>), followed by <see cref="ShellNativeRule"/> with <paramref name="native"/>
    /// (the setting <c>Shell prefer native tools</c>, 2026-09-26) when it names a group. <see cref="ObsidianDeleteRule"/> follows <see cref="ObsidianRule"/>
    /// with <paramref name="obsidianDelete"/> (<c>vault_delete</c> offered, later on 2026-09-22); <see cref="SqlRule"/> after them with <paramref name="sql"/> (2026-09-23), <see cref="OracleRule"/> after it with <paramref name="oracle"/> (2026-09-30), <see cref="MySqlRule"/> after that with <paramref name="mysql"/> (the same day), <see cref="HomeAssistantRule"/> after it with <paramref name="homeAssistant"/> (2026-09-28), <see cref="DockerRule"/> after it with <paramref name="docker"/> and <see cref="DockerWriteRule"/> with <paramref name="dockerWrite"/> (2026-10-02), <see cref="ClaudeAdvisorRule"/> after that with <paramref name="advisor"/> (2026-09-27). With <paramref name="markdown"/> false it is <see cref="OperatingRules"/> and its variants byte for byte.
    /// </summary>
    public static string DefaultRules(bool markdown, bool tools, bool files = true, bool web = false, AskLimits? ask = null, bool sessions = false, bool download = true, bool delete = true, bool mcp = false, bool timers = true, bool git = false, bool shell = false, bool bridge = false, bool police = true, bool obsidian = false, bool obsidianDelete = false, bool sql = false, bool native = false, bool advisor = false, bool homeAssistant = false, bool oracle = false, bool mysql = false, bool unc = false, bool uncFetch = false, bool uncWrite = false, bool docker = false, bool dockerWrite = false, bool help = false, bool sqlite = false, bool postgres = false, bool sqliteWrite = false, ServerWrites serverWrites = ServerWrites.None) =>
        tools
            ? TextRule(markdown) + " " + (timers ? ToolRules : ToolRulesWithoutTimers) + (files ? " " + (delete ? FileRule : FileRuleWithoutDelete) : "") + (help ? " " + HelpRule : "") + (web ? " " + WebRule : "") + (web && files && download ? " " + DownloadRule : "") + (git ? " " + GitRule : "") + (shell ? " " + ShellRuleFor(bridge, police) + NativeTail(native, files, git, web, sql, oracle, mysql, unc, sqlite, postgres) : "") + (obsidian ? " " + ObsidianRule + (obsidianDelete ? " " + ObsidianDeleteRule : "") : "") + (sql ? " " + SqlRule + WriteTail(serverWrites, ServerWrites.Sql, SqlWriteRule) : "") + (oracle ? " " + OracleRule + WriteTail(serverWrites, ServerWrites.Oracle, OracleWriteRule) : "") + (mysql ? " " + MySqlRule + WriteTail(serverWrites, ServerWrites.MySql, MySqlWriteRule) : "") + (sqlite ? " " + SqliteRule + (sqliteWrite ? " " + SqliteWriteRule : "") : "") + (postgres ? " " + PostgresRule + WriteTail(serverWrites, ServerWrites.Postgres, PostgresWriteRule) : "") + (unc ? " " + UncRule + (uncFetch ? " " + UncFetchRule : "") + (uncWrite ? " " + UncWriteRule : "") : "") + (homeAssistant ? " " + HomeAssistantRule : "") + (docker ? " " + DockerRule + (dockerWrite ? " " + DockerWriteRule : "") : "") + (advisor ? " " + ClaudeAdvisorRule : "") + (ask is { } limits ? " " + AskRule(limits) : "") + (sessions ? " " + SessionRule : "") + (mcp ? " " + McpRule : "")
            : TextRule(markdown);

    /// <summary>A family's write sentence after a space while <paramref name="writes"/> holds <paramref name="family"/> (2026-10-05), else nothing.</summary>
    private static string WriteTail(ServerWrites writes, ServerWrites family, string rule) => (writes & family) != 0 ? " " + rule : "";

    /// <summary><see cref="ShellNativeRule"/> after a space, or nothing: off, or no group to name.</summary>
    private static string NativeTail(bool native, bool files, bool git, bool web, bool sql, bool oracle, bool mysql, bool unc, bool sqlite, bool postgres) =>
        native && ShellNativeRule(files, git, web, sql, oracle, mysql, unc, sqlite, postgres) is { Length: > 0 } rule ? " " + rule : "";

    /// <summary>
    /// The system prompt for a turn, in this order: the persona (<paramref name="persona"/> from
    /// <c>persona.md</c>, or <see cref="DefaultPersona"/> when it is null or blank) followed by the
    /// operating rules (<paramref name="operatingRules"/> from <c>operata.md</c>, or
    /// <see cref="OperatingRules"/> when null or blank), each as its own block (since 2026-10-03; both default
    /// had been one paragraph, which the sectioned default persona would have run into the rules); the memory section
    /// (<see cref="MemoryPrompt.Section"/>) when <paramref name="memories"/> is not null — null
    /// means memory is off, an empty list means on with nothing stored yet; the list itself is in the
    /// section only without tools, with them it rides the opening <c>recall_memory</c> pair
    /// (<see cref="OpeningMemoryCallId"/>); and the voice directive (<paramref name="voiceDirective"/>
    /// from <c>vocalia.md</c>) last when <paramref name="speechOutput"/> is on and it has text, so it
    /// still wins. There is no default directive since 2026-10-03 (the user's call): null or blank
    /// appends nothing, spoken or not (<see cref="VocaliaFile"/> keeps the why of what one should say).
    /// With <paramref name="tools"/> false (the setting
    /// <c>LLM offer tools</c> off) every <em>default</em> swaps for its tool-free form
    /// (<see cref="PlainTextRule"/>, <see cref="MemoryPrompt.DirectiveWithoutTool"/>); a custom file
    /// stands verbatim either way. With
    /// <paramref name="web"/> true (the web tools offered) the default rules end with <see cref="WebRule"/>;
    /// with <paramref name="files"/> false (the setting <c>File tools</c> off, the file tools not offered)
    /// they lose <see cref="FileRule"/> (<see cref="OperatingRulesWithoutFiles"/>); with <paramref name="ask"/>
    /// set (<c>ask_user</c> offered under those caps: the setting <c>Ask user</c> on, the bottom pane on) they end with
    /// <see cref="AskRule"/>, after the web sentence. With <paramref name="project"/> set (the working
    /// directory's <c>NEON.md</c> / <c>AGENTS.md</c>, <see cref="ProjectFile"/>) its notes follow the
    /// rules as their own block (<see cref="ProjectNotesSection"/>), tools or not; with
    /// <paramref name="skills"/> not null (the setting <c>Agent skills</c> on and tools offered) the skills
    /// block (<see cref="Skills.SkillsPrompt.Section"/>) follows the memory section — null means off,
    /// an empty list on with none installed; it is dropped with <paramref name="tools"/> false, since
    /// nothing could load one. With <paramref name="markdown"/> true (the transcript styles the reply:
    /// the setting <c>Transcript markdown</c> on, the pane on, the turn not spoken) the default rules
    /// open with <see cref="MarkdownRule"/> in place of <see cref="PlainTextRule"/>. With <paramref name="sessions"/>
    /// true (<c>session_manager</c> offered: the setting <c>Session tool</c> on) they close with <see cref="SessionRule"/>.
    /// Since 2026-09-19 a tool switched off by name on <c>/tools</c> leaves the rules that name it alone (a call
    /// answers <c>Error: unknown tool</c>), with two exceptions where the prompt would otherwise lie: <paramref name="download"/>
    /// false drops <see cref="DownloadRule"/>, and <paramref name="recall"/> false (<c>recall_memory</c> off while memory
    /// is on) puts the list into the memory section as under <paramref name="tools"/> false, since no opening call carries it;
    /// the third (2026-09-20) is a whole group: <paramref name="timers"/> false (no timer tool offered — headless, or the
    /// three switched off) drops <see cref="TimerRule"/>. With <paramref name="save"/> false (<c>Memory mode</c> read-only, 2026-10-04)
    /// the memory directive loses its save sentences (<see cref="MemoryPrompt.ReadOnlyDirective"/>).
    /// </summary>
    public static string SystemPrompt(bool speechOutput, IReadOnlyList<string>? memories, string? persona = null, string? operatingRules = null, string? voiceDirective = null, bool tools = true, bool web = false, bool files = true, AskLimits? ask = null, ProjectNotes? project = null, IReadOnlyList<Skills.Skill>? skills = null, bool markdown = false, bool sessions = false, bool download = true, bool recall = true, bool delete = true, bool mcp = false, bool timers = true, bool git = false, bool shell = false, bool bridge = false, bool police = true, bool obsidian = false, bool obsidianDelete = false, bool sql = false, bool native = false, string? plan = null, bool advisor = false, bool homeAssistant = false, bool oracle = false, bool mysql = false, bool unc = false, bool uncFetch = false, bool uncWrite = false, bool docker = false, bool dockerWrite = false, bool help = false, bool sqlite = false, bool postgres = false, bool save = true, bool sqliteWrite = false, ServerWrites serverWrites = ServerWrites.None)
    {
        bool customPersona = !string.IsNullOrWhiteSpace(persona);
        bool customRules = !string.IsNullOrWhiteSpace(operatingRules);
        string defaultRules = DefaultRules(markdown, tools, files, web, ask, sessions, download, delete, mcp, timers, git, shell, bridge, police, obsidian, obsidianDelete, sql, native, advisor, homeAssistant, oracle, mysql, unc, uncFetch, uncWrite, docker, dockerWrite, help, sqlite, postgres, sqliteWrite, serverWrites);
        var sb = new StringBuilder((customPersona ? persona!.Trim() : DefaultPersona) + "\n\n" + (customRules ? operatingRules!.Trim() : defaultRules));
        if (project is not null)
        {
            sb.Append("\n\n").Append(ProjectNotesSection(project));
        }

        if (memories is not null)
        {
            sb.Append("\n\n").Append(MemoryPrompt.Section(memories, tools && recall, save));
        }

        if (tools && skills is not null)
        {
            sb.Append("\n\n").Append(Skills.SkillsPrompt.Section(skills));
        }

        // Plan mode's directive (2026-09-26, PlanText.Directive): after the skills, apart from the rules so a custom
        // operata.md cannot drop it, and ahead of the voice directive, which stays last.
        if (!string.IsNullOrWhiteSpace(plan))
        {
            sb.Append("\n\n").Append(plan.Trim());
        }

        if (speechOutput && !string.IsNullOrWhiteSpace(voiceDirective))
        {
            sb.Append("\n\n").Append(voiceDirective.Trim());
        }

        return sb.ToString();
    }

    /// <summary>
    /// A <c>/botchat</c> bot's system prompt up to its own blocks (2026-10-04, the bots' tools and memory): the persona
    /// (<see cref="DefaultPersona"/> when null or blank), the default rules — <see cref="TextRule"/> alone without
    /// <paramref name="rules"/>, else the sentences of the tools the bot is offered (<see cref="TurnRules.DefaultRules"/>) — then,
    /// with <paramref name="memories"/>, the memory section with the list in it (<see cref="MemoryPrompt.ListedSection"/>: a bot's
    /// history is rebuilt every reply, so no opening <c>recall_memory</c> pair carries it), and the voice directive last when
    /// spoken. No <c>operata.md</c>, project notes, skills or plan directive: the bots never had them. With neither tools nor
    /// memories it is <c>SystemPrompt</c> with every tool off, byte for byte. Pure.
    /// </summary>
    public static string BotSystemPrompt(bool speechOutput, string? persona, string? voiceDirective, bool markdown, TurnRules? rules = null, IReadOnlyList<string>? memories = null, bool memorySave = true)
    {
        var sb = new StringBuilder((string.IsNullOrWhiteSpace(persona) ? DefaultPersona : persona.Trim()) + "\n\n" + (rules?.DefaultRules(markdown) ?? TextRule(markdown)));
        if (memories is not null)
        {
            sb.Append("\n\n").Append(MemoryPrompt.ListedSection(memories, memorySave));
        }

        if (speechOutput && !string.IsNullOrWhiteSpace(voiceDirective))
        {
            sb.Append("\n\n").Append(voiceDirective.Trim());
        }

        return sb.ToString();
    }

    /// <summary>The project notes block: <see cref="ProjectNotesHeading"/> naming the file, a newline, the text. Pinned.</summary>
    public static string ProjectNotesSection(ProjectNotes project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ProjectNotesHeading(project.FileName) + "\n" + project.Text;
    }

    /// <summary><c>Notes for the working directory (from NEON.md):</c>. Pinned.</summary>
    public static string ProjectNotesHeading(string fileName) => "Notes for the working directory (from " + fileName + "):";

    /// <summary>The default for <see cref="MaxToolIterations"/>: what the setting <c>LLM max tool iterations</c> starts at — the cap itself since 2026-09-15 (1000 before).</summary>
    public const int DefaultMaxToolIterations = 10000;

    private int _maxToolIterations = DefaultMaxToolIterations;

    /// <summary>
    /// Model round trips allowed per turn before the loop gives up — a tool call is one, and a
    /// picture (<see cref="Tools.ViewImageTool"/>) costs one, since it arrives in the message after
    /// the result. Set per turn by the shell from the setting, like <see cref="Tools"/>; the turn
    /// budget (<see cref="LlmTimeouts.Turn"/>) stays the guard against a runaway. Never below 1.
    /// </summary>
    public int MaxToolIterations
    {
        get => _maxToolIterations;
        set => _maxToolIterations = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "At least one tool iteration.");
    }

    /// <summary>
    /// Whether a call to an offered tool that the model wrote out as text is caught (<see cref="TextToolCallFilter"/>): kept out of
    /// the reply shown and spoken, and run as a real call (2026-09-25, the user's report: a <c>/botchat</c> bot wrote
    /// <c>generate_image(prompt=…)</c> into its line and no picture came). Off by default and on for botchat alone — the user's
    /// call: in the main chat a reply that shows a call as an example would run it.
    /// </summary>
    public bool TextToolCalls { get; set; }

    /// <summary>
    /// Tools whose calls written out as text are kept out of the reply and never run, whatever the turn offers (2026-09-30, code
    /// review: a <c>/botchat</c> bot offered no <c>load_skill</c> but given a preloaded skill whose content names it wrote
    /// <c>load_skill name: …</c> into its line, shown and spoken). Each is its name and parameter names (the line form's). Empty,
    /// the default, takes out none; a tool the turn offers and <see cref="TextToolCalls"/> catches still runs.
    /// </summary>
    public IReadOnlyList<(string Name, IReadOnlyList<string> Parameters)> WrittenCallsTakenOut { get; set; } = [];

    /// <summary>
    /// The offered tools whose calls written out as text <see cref="TextToolCalls"/> catches (2026-10-04, the <c>/botchat</c> bots'
    /// tools): null, the default, every one offered; else those names alone, so a bot offered the main chat's tools still runs a
    /// written <c>generate_image</c> or <c>save_memory</c> but never a written <c>delete</c> (the main chat runs none).
    /// </summary>
    public IReadOnlySet<string>? TextToolCallNames { get; set; }

    /// <summary>
    /// Whether the last round trip <see cref="MaxToolIterations"/> allows is asked without the tools, so the model must answer in
    /// words (2026-09-30, the user's report: a <c>/botchat</c> bot a few turns in spent all three of its round trips on
    /// <c>generate_image</c> and the chat showed "Stopped after 3 tool iterations without a final answer" in place of its line).
    /// A call the model still writes out as text in that round is kept out of the reply (<see cref="TextToolCalls"/>) but not run.
    /// Off by default and on for botchat alone: the main chat's cap keeps its meaning, the loop's guard against a runaway.
    /// </summary>
    public bool LastRoundAnswers { get; set; }

    /// <summary>
    /// How many pictures, and megabytes of them, a request may carry (2026-10-03, <see cref="Llm.PictureBudget"/>): applied to the
    /// history before every request of a turn, the oldest taken out in a batch with <see cref="Llm.PictureBudget.TakenOutNotice"/>
    /// shown. The compiled defaults until the shell sets the profile's (<c>LLM picture keep</c>, <c>LLM picture megabytes</c>).
    /// </summary>
    public PictureBudget PictureBudget { get; set; } = PictureBudget.Default;

    /// <summary>
    /// The context window the tool loop measures a request's usage against (<see cref="WindowTokens"/>,
    /// the figure behind <c>/usage</c>), the share of it that trips the guard (<see cref="Percent"/>,
    /// the setting <c>LLM auto compact (%)</c>; 0 = never) and what the loop then does (<see cref="Mode"/>,
    /// the setting <c>LLM tool compact type</c>).
    /// </summary>
    public sealed record TurnContextGuard(int WindowTokens, int Percent, ToolCompactMode Mode, bool ProtectSkills = true)
    {
        /// <summary>Whether the guard acts at all: a share above zero and a mode that does something.</summary>
        public bool Acts => Percent > 0 && Mode != ToolCompactMode.Nothing;

        /// <summary>The whole-number share of the window <paramref name="usage"/> takes.</summary>
        public int PercentOf(TokenUsage usage) => PercentOf(usage.Total);

        /// <summary>The whole-number share of the window <paramref name="tokens"/> take (2026-09-28: the loop's estimate of the next request).</summary>
        public int PercentOf(long tokens) => (int)(tokens * 100L / WindowTokens);

        /// <summary>Whether <paramref name="usage"/> is at or past the share.</summary>
        public bool Tripped(TokenUsage usage) => Tripped(usage.Total);

        /// <summary>Whether <paramref name="tokens"/> are at or past the share.</summary>
        public bool Tripped(long tokens) => Acts && tokens > 0 && tokens * 100L >= (long)WindowTokens * Percent;
    }

    /// <summary>
    /// The mid-turn context guard, set per turn by the shell like <see cref="MaxToolIterations"/>;
    /// null while the window is unknown. The automatic compact checks only at the top of a message,
    /// and one message can walk the context to the ceiling by itself (sixty tool round trips took
    /// a chef's five-recipe request from 7k to 129k tokens of a 151k window, 2026-09-15). After
    /// each iteration whose request reported usage, the next request is estimated — that usage plus
    /// the results just appended (<see cref="ConversationCompactor.EstimateTokens"/>; since 2026-09-28,
    /// the report alone was one request late: a big fetch took the next request past the share unseen) —
    /// and at or past the share: <see cref="ToolCompactMode.Prune"/> stubs this turn's older tool
    /// results (<see cref="ConversationCompactor.PruneRecent"/>) and carries on;
    /// <see cref="ToolCompactMode.Compact"/> prunes the same way and, when the estimate is still at or
    /// past the share, summarises (<see cref="CompactMidTurnAsync"/>); <see cref="ToolCompactMode.Stop"/>
    /// ends the turn with <see cref="TurnStoppedNotice"/> after the iteration's results are in, so no
    /// call is left unanswered. Kept non-null under <see cref="ToolCompactMode.Nothing"/> too, so a
    /// timeout can name the share.
    /// </summary>
    public TurnContextGuard? ContextGuard { get; set; }

    /// <summary>
    /// Whether every turn's thinking goes back to the server, not only the turn in flight's (2026-09-28, the setting
    /// <c>LLM preserve thinking</c>, off by default): the requests carry <see cref="OpenAICompatibleChatClient.PreserveThinkingKey"/>,
    /// which sends the history's <see cref="TextReasoningContent"/> back as <c>reasoning_content</c> on every assistant
    /// message and asks the chat template to keep it. The turn in flight's goes back either way. Read at each turn.
    /// </summary>
    public bool PreserveThinking { get; set; }

    /// <summary>
    /// The sampling every request of this assistant carries (2026-09-28, the setting <c>LLM sampling</c>): the turn's, the
    /// summariser's and a side request's (<see cref="RequestAsync"/>: the session's title, the skill learner), resolved for
    /// the connected model (<see cref="LlmSampling.Resolve"/>) at the connect and again before each turn, so an edit lands
    /// at the next turn. Null or <see cref="LlmSampling.None"/> sends nothing and leaves the server's defaults.
    /// </summary>
    public LlmSampling? Sampling { get; set; }

    /// <summary>
    /// Counts each request as it streams (2026-09-25): begun when the request goes out, a chunk per update with
    /// content other than the usage report, ended when the stream does — completed, cancelled or failed — so the
    /// busy row's <c>estimate</c> never outlives its request. Null (a botchat bot's assistant, a test) counts nothing.
    /// </summary>
    public StreamMeter? Meter { get; set; }

    /// <summary>
    /// What a line where only tool results were pruned opens with (2026-09-19, the user's pick):
    /// the guard's <see cref="TurnPrunedNotice"/> and a prune-only compact's notice (<c>App.CompactionText</c>
    /// reads it; a summary wears the clamp). The scissors U+2702 with the variation selector — bare
    /// it is a one-cell text glyph; with U+FE0F Windows Terminal draws the colour emoji two cells wide,
    /// and <c>UI.TextCells</c> counts the selector as one after a narrow character for that reason —
    /// and a space. Pinned.
    /// </summary>
    public const string PruneGlyph = "✂️ ";

    /// <summary>What the guard's stop line opens with (2026-09-19, the user's pick): the stop sign U+1F6D1, emoji-presentation by itself, and a space. Pinned.</summary>
    public const string StopGlyph = "🛑 ";

    /// <summary>
    /// What a line where something was summarised opens with (2026-09-19, the user's pick; here since 2026-09-28,
    /// when the mid-turn compact's failure line needed it): the clamp U+1F5DC with its variation selector — bare
    /// it is text-presentation — and a space. <c>App.CompactionText.CompactGlyph</c> is this one. Pinned.
    /// </summary>
    public const string CompactGlyph = "🗜️ ";

    /// <summary>The prefix of a failed compact's error line, <c>/compact</c>'s and the mid-turn guard's: <c>🗜️ Compact failed: </c> + <see cref="Explain"/>.</summary>
    public const string CompactFailedPrefix = CompactGlyph + "Compact failed: ";

    /// <summary>The transcript line after the guard pruned: <c>(✂️ context at 85%: pruned 23 tool results from this turn)</c>. Pinned.</summary>
    public static string TurnPrunedNotice(int percent, int pruned) =>
        "(" + PruneGlyph + "context at " + percent.ToString(CultureInfo.InvariantCulture) + "%: pruned " + pruned.ToString(CultureInfo.InvariantCulture) + (pruned == 1 ? " tool result" : " tool results") + " from this turn)";

    /// <summary>The error line when the guard stopped the turn: <c>🛑 Stopped at 85% …</c>. Pinned.</summary>
    public static string TurnStoppedNotice(int percent) =>
        StopGlyph + "Stopped at " + percent.ToString(CultureInfo.InvariantCulture) + "% of the context window (LLM tool compact type is stop); /compact or /clear before continuing.";

    /// <summary>
    /// One summary the mid-turn guard may make (2026-09-28): what the summariser reads, its prompt and closing line,
    /// how the new history is built from the summary, and the two protected counts <c>LLM compact show summary</c> names.
    /// </summary>
    private sealed record MidTurnCompact(IReadOnlyList<ChatMessage> Transcript, string Instruction, string RequestLine, Func<string, List<ChatMessage>> Rebuild, int OpeningKept, int RecentKept);

    /// <summary>What a mid-turn summary did: its result and the tokens it is estimated to have saved, or the failure's explanation.</summary>
    private sealed record MidTurnCompactDone(ConversationCompactor.Result? Result, long Saved, string? Failure);

    /// <summary>
    /// The first stage: the turns before this one become one summary (<see cref="ConversationCompactor.Summarised"/>),
    /// this turn kept whole — <c>LLM compact keep recent</c> is the between-turn compact's, and mid-turn the context is
    /// over the share already. Null when there is no older turn.
    /// </summary>
    private MidTurnCompact? PlanOlderCompact()
    {
        var plan = ConversationCompactor.Split(_history.Messages, 1);
        return plan.HasOlderTurns
            ? new MidTurnCompact(plan.Older, ConversationCompactor.SummaryInstruction, ConversationCompactor.SummaryRequest(null), summary => ConversationCompactor.Summarised(summary, plan), plan.Opening.Count, plan.Recent.Count)
            : null;
    }

    /// <summary>
    /// The second stage: this turn's earlier iterations become a progress note on its user message
    /// (<see cref="ConversationCompactor.SummarisedTurn"/>), the last iteration kept. Null when there is no earlier iteration.
    /// </summary>
    private MidTurnCompact? PlanTurnCompact()
    {
        var plan = ConversationCompactor.SplitTurn(_history.Messages);
        return plan is { HasEarlierIterations: true }
            ? new MidTurnCompact(plan.Transcript, ConversationCompactor.TurnProgressInstruction, ConversationCompactor.TurnProgressRequest, summary => ConversationCompactor.SummarisedTurn(summary, plan), plan.Before.Count + 1 + plan.Opening.Count, plan.Last.Count)
            : null;
    }

    /// <summary>
    /// One mid-turn summary (2026-09-28): the summariser's request, then the history swapped for the rebuilt one. Out of
    /// the iterator because C# allows no <c>yield</c> inside a try with a catch. Cancellation propagates with the history
    /// untouched, as <c>/compact</c>'s does; any other failure is its explanation, the history untouched too, and the turn carries on.
    /// </summary>
    private async Task<MidTurnCompactDone> CompactMidTurnAsync(MidTurnCompact step, CancellationToken cancellationToken)
    {
        int before = _history.Messages.Count;
        try
        {
            var (summary, usage) = await SummarizeAsync(step.Instruction, step.Transcript, step.RequestLine, cancellationToken).ConfigureAwait(false);
            var messages = step.Rebuild(summary);
            long saved = ConversationCompactor.EstimateTokens(_history.Messages) - ConversationCompactor.EstimateTokens(messages);
            _history.Replace(messages);
            return new MidTurnCompactDone(new ConversationCompactor.Result(before, messages.Count, 0, usage, Summarised: true)
            {
                Summary = summary.Trim(),
                OpeningKept = step.OpeningKept,
                RecentKept = step.RecentKept,
            }, saved, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new MidTurnCompactDone(null, 0, ExplainFailure(ex));
        }
    }

    /// <summary>The stable part of System.ClientModel's network-timeout message, the one the SDK's <c>NetworkTimeout</c> (the request timeout) throws with.</summary>
    public const string NetworkTimeoutMarker = "exceeded the configured timeout";

    /// <summary>
    /// Whether <paramref name="ex"/> is the request timeout: an <see cref="OperationCanceledException"/>
    /// anywhere in the chain carrying <see cref="NetworkTimeoutMarker"/>. The SDK's own sentence names
    /// <c>ClientPipelineOptions.NetworkTimeout</c>, a knob the operator does not have.
    /// </summary>
    public static bool LooksLikeRequestTimeout(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                current = aggregate.InnerExceptions[0];
            }

            if (current is OperationCanceledException && current.Message.Contains(NetworkTimeoutMarker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The request timeout in the operator's words: <c>The server sent nothing for 75s (LLM request
    /// timeout (s), LLM tab)</c>, and with the last request's share of the window known, <c> — the
    /// context was at 85% of the window; a model near its limit can run away (/compact, /clear)</c>.
    /// The idle-read timeout is the right guard for a runaway the server keeps generating into a
    /// buffered tool call; raising it only lets the runaway run longer. Pinned.
    /// </summary>
    public static string RequestTimeoutExplanation(TimeSpan request, int? contextPercent)
    {
        string text = "The server sent nothing for " + LlmTimeouts.Format(request) + " (LLM request timeout (s), LLM tab)";
        if (contextPercent is { } percent)
        {
            text += " — the context was at " + percent.ToString(CultureInfo.InvariantCulture) + "% of the window; a model near its limit can run away (/compact, /clear)";
        }

        return text;
    }

    /// <summary>
    /// <see cref="Explain"/>, except that the request timeout reads as <see cref="RequestTimeoutExplanation"/>
    /// over this assistant's ceiling, with <paramref name="lastUsage"/>'s share of the window when
    /// the guard knows it (the loop passes the last request that reported; a summariser passes none).
    /// </summary>
    public string ExplainFailure(Exception ex, TokenUsage? lastUsage = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (!LooksLikeRequestTimeout(ex))
        {
            return Explain(ex);
        }

        int? percent = ContextGuard is { } guard && lastUsage is { Total: > 0 } usage ? guard.PercentOf(usage) : null;
        return RequestTimeoutExplanation(_timeouts.Request, percent);
    }

    /// <summary>
    /// The <c>CallId</c>s of the opening calls (<see cref="OpeningCalls"/>): the clock's, the
    /// working directory's and the memory's (2026-09-17). Nine alphanumeric characters each on
    /// purpose: Mistral-family chat templates validate a tool call id as exactly that, and every
    /// other server takes any string. One conversation holds each once.
    /// </summary>
    public const string OpeningClockCallId = "neonclock";
    public const string OpeningCwdCallId = "neoncwdir";
    public const string OpeningMemoryCallId = "neonmemry";

    private static readonly IReadOnlySet<string> OpeningCallIds = new HashSet<string>(StringComparer.Ordinal) { OpeningClockCallId, OpeningCwdCallId, OpeningMemoryCallId };

    /// <summary>
    /// The prefix of a pending call's id (2026-09-21): <c>neonp</c> and the last four characters of the
    /// process id (<c>neonp2a1b</c> for <c>proc_3f2a1b</c>) — nine alphanumerics, the opening calls' rule.
    /// </summary>
    public const string PendingCallIdPrefix = "neonp";

    /// <summary>The call id a seeded poll for <paramref name="sessionId"/> carries: <see cref="PendingCallIdPrefix"/> and the id's last four characters.</summary>
    public static string PendingCallId(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        string tail = sessionId.Length >= 4 ? sessionId[^4..] : sessionId.PadLeft(4, '0');
        return PendingCallIdPrefix + tail;
    }

    /// <summary>Whether <paramref name="callId"/> is one of the opening calls' (<see cref="OpeningClockCallId"/>, <see cref="OpeningCwdCallId"/>, <see cref="OpeningMemoryCallId"/>) or a pending call's (<see cref="PendingCallIdPrefix"/>).</summary>
    public static bool IsOpeningCallId(string callId) =>
        OpeningCallIds.Contains(callId) || (callId.Length == 9 && callId.StartsWith(PendingCallIdPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Appended to the <c>Model error:</c> notice when the server refused a request that carried
    /// tools with the Mistral-family template's sentence (<see cref="LooksLikeToolRoleRejection"/>):
    /// the model has no tool role, and the setting that talks to it anyway is named. Pinned.
    /// </summary>
    public const string ToolRoleHint = " — this model's chat template accepts no tool messages; turn the setting LLM offer tools off (the LLM tab of /settings) to talk to it without tools";

    /// <summary>
    /// Whether a failed request reads as one the server would not take for its size (2026-10-03, the user's report: a
    /// llama-server closed the connection on a ~120 MB request of pictures, <c>SocketException: An existing connection was
    /// forcibly closed by the remote host</c>): an HTTP 413, or the connection reset or aborted under us. A reset has other
    /// causes too (a server that crashed), so the loop only acts on it for a request that carried pictures and got nothing back.
    /// </summary>
    public static bool LooksLikeOversizedRequest(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                current = aggregate.InnerExceptions[0];
            }

            switch (current)
            {
                case ClientResultException { Status: 413 }:
                case HttpRequestException { StatusCode: System.Net.HttpStatusCode.RequestEntityTooLarge }:
                case System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.ConnectionAborted }:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The notice before the one retry of a request the server dropped as it was sent: <c>The server dropped the request
    /// as it was sent (118 MB, 54 pictures); the 27 oldest pictures are now their file names. Trying again.</c> Pinned.
    /// </summary>
    public static string DroppedRequestNotice(long bytes, int pictures, int takenOut) =>
        "The server dropped the request as it was sent (" + PictureBudget.FormatMegabytes(bytes) + ", " + pictures.ToString(CultureInfo.InvariantCulture)
        + (pictures == 1 ? " picture" : " pictures") + "); the " + (takenOut == 1 ? "oldest picture is now its file name" : takenOut.ToString(CultureInfo.InvariantCulture) + " oldest pictures are now their file names") + ". Trying again.";

    /// <summary>
    /// Appended to the <c>Model error:</c> notice when a request with pictures was dropped and no retry could help:
    /// <c> — the request was 118 MB with 54 pictures; /compact prune takes older pictures out, and LLM picture keep (the
    /// LLM tab of /settings) caps them</c>. Pinned.
    /// </summary>
    public static string OversizedRequestHint(long bytes, int pictures) =>
        " — the request was " + PictureBudget.FormatMegabytes(bytes) + " with " + pictures.ToString(CultureInfo.InvariantCulture) + (pictures == 1 ? " picture" : " pictures")
        + "; /compact prune takes older pictures out, and LLM picture keep (the LLM tab of /settings) caps them";

    /// <summary>
    /// Whether a failure's explanation is a chat template rejecting the <c>tool</c> role:
    /// <c>Only user, system and assistant roles are supported!</c> (Mistral-family templates; SGLang
    /// and vLLM answer HTTP 400 with the template's own words). Case-insensitive on the stable part.
    /// </summary>
    public static bool LooksLikeToolRoleRejection(string explained)
    {
        ArgumentNullException.ThrowIfNull(explained);
        return explained.Contains("roles are supported", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What <see cref="SummarizeAsync"/> throws when the server answered with no text and no clue why. Pinned.</summary>
    public const string EmptySummaryError = "The server returned an empty summary.";

    /// <summary>The empty summary's message when the stream ended on the length limit (2026-09-28): the context or <c>max_tokens</c> ran out before any text. Pinned.</summary>
    public const string EmptySummaryLengthError = "The server stopped before writing a summary: the context or the output limit ran out.";

    /// <summary>The empty summary's message when the reply was thinking alone (2026-09-28): a template that ignores <c>enable_thinking=false</c> thinks anyway. Pinned.</summary>
    public static string EmptySummaryThinkingError(int chars) =>
        "The model only thought (" + chars.ToString("N0", CultureInfo.InvariantCulture) + " characters) and wrote no summary; its chat template may ignore enable_thinking=false.";

    /// <summary>The empty summary's message when the reply was a tool call (2026-09-28): a model primed by a tool loop calls on though no tool is offered. Pinned.</summary>
    public static string EmptySummaryToolError(string tool) =>
        "The model asked for a tool (" + tool + ") instead of writing a summary.";

    /// <summary>
    /// One opening call: the tool and the fixed id its call/result pair carries; the arguments are
    /// empty for the three openers (a <c>/skill</c> activation carried the skill's name here from
    /// 2026-09-16 until later on 2026-09-18) and the poll's for a pending call (2026-09-21).
    /// </summary>
    public sealed record OpeningCall(AIFunction Tool, string CallId, IReadOnlyDictionary<string, object?>? Arguments = null);

    /// <summary>Logged (Info, so <c>--log</c> only) when a reply carried a <c>&lt;/think&gt;</c> with no opener: the server's parser missed, and the text before it was thinking.</summary>
    public const string ThinkingLeakedNote = "The server streamed thinking as content (a </think> with no <think>); the tag was dropped.";

    /// <summary>Logged (Debug) when a whole think block arrived as content: the server has no reasoning parser.</summary>
    public const string ThinkingBlockNote = "The server streamed a <think> block as content; it was dropped.";

    private const string Category = "Llm";

    private readonly IChatClient _client;
    private readonly ConversationHistory _history;
    private readonly LlmTimeouts _timeouts;
    private IReadOnlyList<AIFunction> _tools;
    private readonly TimeProvider _time;
    private readonly ReasoningEffort? _reasoning;

    /// <param name="client">The seam; production passes <see cref="OpenAICompatibleChatClient"/>, tests a fake.</param>
    /// <param name="history">The transcript the model sees; shared with whoever renders it.</param>
    /// <param name="timeouts">Already interlocked by <see cref="LlmTimeouts.Resolve"/>.</param>
    /// <param name="tools">Hand-written <see cref="AIFunction"/> subclasses with explicit schemas; empty in sprint 1.</param>
    /// <param name="time">The clock behind the turn deadline; tests advance a fake.</param>
    /// <param name="reasoning">The effort to ask for on every request (<see cref="ReasoningLevel.Resolve"/>), or null to leave the server to its default.</param>
    public Assistant(
        IChatClient client,
        ConversationHistory history,
        LlmTimeouts timeouts,
        IReadOnlyList<AIFunction>? tools = null,
        TimeProvider? time = null,
        ReasoningEffort? reasoning = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _timeouts = timeouts;
        _tools = tools ?? Array.Empty<AIFunction>();
        _time = time ?? TimeProvider.System;
        _reasoning = reasoning;
    }

    public ConversationHistory History => _history;

    /// <summary>
    /// The tools the next turn offers. Settable because the shell decides per turn what a turn may
    /// do (memory on or off), the same way it sets <see cref="ConversationHistory.SystemPrompt"/>;
    /// a turn reads it once, at its start.
    /// </summary>
    public IReadOnlyList<AIFunction> Tools
    {
        get => _tools;
        set => _tools = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The tools the assistant calls itself at the start of a conversation — the clock, the
    /// working directory and the memory, in the app — so the first request already carries their answers. Seeded
    /// in list order by the first turn of a conversation (<see cref="ConversationHistory.TurnCount"/>
    /// was zero) right after the user's message and before the first model request, each as its
    /// own <c>assistant(call) → tool</c> pair: the shape every tool-capable chat template accepts,
    /// where a pair with no user message ahead of it is not. Empty (the default) seeds nothing.
    /// Set per turn by the shell, like <see cref="Tools"/>.
    /// </summary>
    public IReadOnlyList<OpeningCall> OpeningCalls { get; set; } = [];

    /// <summary>
    /// The calls seeded at the start of the <em>next</em> turn, whatever its number (2026-09-21): the
    /// <c>process poll</c> pairs for the background processes that exited with <c>notify</c> since the
    /// last turn, so the model learns of an end without asking. Set per turn by the shell after
    /// <see cref="OpeningCalls"/>, and cleared by <see cref="RunTurnAsync(string, IReadOnlyList{ImageAttachment}, CancellationToken)"/>
    /// once seeded, so a retry of the same message does not repeat them. Same shape as the opening
    /// calls: a real pair, no model request, no iteration spent.
    /// </summary>
    public IReadOnlyList<OpeningCall> PendingCalls { get; set; } = [];

    /// <summary>
    /// The conversation a stateful server keeps for this chat (2026-09-30, the Claude CLI server: its session id), sent as
    /// <see cref="ChatOptions.ConversationId"/> on every request of a turn; null for every other server, which is sent the
    /// whole history each time. Set per turn by the shell, like <see cref="Tools"/>. The side requests (a summary, a
    /// title) never carry it.
    /// </summary>
    public string? ConversationId { get; set; }

    /// <summary>
    /// True for the events the opening calls raise (a <see cref="TurnEvent.ToolCall"/> or
    /// <see cref="TurnEvent.ToolResult"/> carrying <see cref="OpeningClockCallId"/>,
    /// <see cref="OpeningCwdCallId"/> or <see cref="OpeningMemoryCallId"/>). They come first and at
    /// once, before any model request, so a host shows them ahead of the reply and keeps its
    /// thinking indicator over the wait that follows.
    /// </summary>
    public static bool IsOpeningEvent(TurnEvent evt) => evt switch
    {
        TurnEvent.ToolCall call => IsOpeningCallId(call.CallId),
        TurnEvent.ToolResult result => IsOpeningCallId(result.CallId),
        _ => false,
    };

    /// <summary>The reasoning effort every request asks for, or null when the server decides.</summary>
    public ReasoningEffort? Reasoning => _reasoning;

    /// <summary>
    /// Runs one turn for <paramref name="userText"/>. Text arrives as <see cref="TurnEvent.TextDelta"/>
    /// events; the reply is committed to <see cref="History"/> once the stream completes, or as far
    /// as it got if it was cut short. Server failures become a <see cref="TurnEvent.Notice"/>, never
    /// an exception — the shell must survive a dead server. Cancellation propagates.
    /// </summary>
    public IAsyncEnumerable<TurnEvent> RunTurnAsync(string userText, CancellationToken cancellationToken = default) =>
        RunTurnAsync(userText, [], cancellationToken);

    /// <summary>
    /// <see cref="RunTurnAsync(string, CancellationToken)"/> with pictures beside the text
    /// (<see cref="ConversationHistory.AddUser(string, IReadOnlyList{ImageAttachment})"/>); they
    /// stay in the history with the text, so a later turn can ask about them.
    /// </summary>
    public async IAsyncEnumerable<TurnEvent> RunTurnAsync(
        string userText,
        IReadOnlyList<ImageAttachment> images,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userText);
        ArgumentNullException.ThrowIfNull(images);

        // Decided before the user message lands: the first turn of a conversation opens with the
        // opening calls, a retry of it (the user message already committed) does not; the pending
        // calls (a process's exit) ride any turn, once.
        IReadOnlyList<OpeningCall> opening = _history.TurnCount == 0 ? OpeningCalls : [];
        if (PendingCalls.Count > 0)
        {
            opening = [.. opening, .. PendingCalls];
            PendingCalls = [];
        }

        // The turn's two lines in the log (2026-09-19): the opening one now, the closing one with
        // the tally when the scope ends — the reply's end, a notice, a cancel (the consumer's
        // dispose), a throw — through the using declaration's own finally.
        using var log = new TurnLog(_history.TurnCount + 1, userText, images.Count, opening.Count, _time, cancellationToken);

        // Committed up front: a turn that fails still leaves the question in the transcript.
        _history.AddUser(userText, images);

        foreach (var (tool, callId, seededArguments) in opening)
        {
            // A genuine call/result pair, not a prompt line: the same two events the model's own
            // calls raise, so the transcript shows it the same way, and not a model request, so it
            // costs no iteration and no time on the turn budget.
            var call = new FunctionCallContent(callId, tool.Name, seededArguments is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(seededArguments));
            _history.AddMessage(new ChatMessage(ChatRole.Assistant, [call]));
            string openingJson = SerializeArguments(call.Arguments);
            DiagnosticLog.Debug(Category, ToolCallLogLine(call.Name, openingJson) + OpeningSuffix);
            yield return new TurnEvent.ToolCall(call.Name, call.CallId, openingJson);

            // An opening tool answers with text; a picture from one would have no carrier.
            var (result, _, _, _) = await InvokeAsync(tool, call, cancellationToken).ConfigureAwait(false);
            _history.AddToolResults([ResultContent(call, result)]);
            yield return new TurnEvent.ToolResult(call.Name, call.CallId, result);
        }

        var options = new ChatOptions
        {
            // ModelId stays null; the OpenAI adapter ignores it and the ChatClient's model applies.
            Tools = _tools.Count > 0 ? new List<AITool>(_tools) : null,

            // Abstract here; OpenAICompatibleChatClient turns it into the wire fields.
            Reasoning = _reasoning is { } effort ? new ReasoningOptions { Effort = effort } : null,

            // Every turn's thinking back, and the template asked to keep it (2026-09-28); the client takes the key off.
            AdditionalProperties = PreserveThinking ? new AdditionalPropertiesDictionary { [OpenAICompatibleChatClient.PreserveThinkingKey] = true } : null,

            // The stateful server's conversation (2026-09-30), when the shell named one.
            ConversationId = ConversationId,
        };
        Sampling?.ApplyTo(options);

        long started = _time.GetTimestamp();

        // The last request that reported usage, for the guard and for a timeout's context share.
        TokenUsage? lastUsage = null;

        // Whether the guard may summarise (ToolCompactMode.Compact, 2026-09-28): once per climb. A summary disarms
        // it; a request that reports usage under the share arms it again, so a very long turn can compact more
        // than once but a turn hovering over the share does not pay for a summariser request every iteration.
        bool compactArmed = true;

        // Whether a request the server dropped as it was sent has been tried again with fewer pictures (2026-10-03): once a turn.
        bool picturesRetried = false;

        for (int iteration = 1; iteration <= MaxToolIterations; iteration++)
        {
            if (_time.GetElapsedTime(started) >= _timeouts.Turn)
            {
                DiagnosticLog.Warn(Category, $"Turn budget of {LlmTimeouts.Format(_timeouts.Turn)} exhausted after {iteration - 1} tool iteration(s).");
                log.Ending = TurnStopped;
                yield return new TurnEvent.Notice($"Turn budget of {LlmTimeouts.Format(_timeouts.Turn)} exhausted.", IsError: true);
                yield break;
            }

            var updates = new List<ChatResponseUpdate>();
            var partial = new StringBuilder();
            var filter = new ThinkTagFilter();
            // A <think> block streamed as content (2026-09-28): kept, so the history carries it as thinking like the server's own.
            var tagThinking = new StringBuilder();
            // After the think filter: a call written as text, caught when the turn asks for it and offers tools, and one to a tool
            // in WrittenCallsTakenOut (2026-09-30), taken out whatever the turn offers.
            var catching = TextToolCalls && _tools.Count > 0 ? WrittenCallNames(TextToolCallNames is { } caught ? _tools.Where(t => caught.Contains(t.Name)) : _tools) : [];
            var written = catching.Count > 0 || WrittenCallsTakenOut.Count > 0 ? WrittenCallFilter([.. catching, .. WrittenCallsTakenOut]) : null;
            Exception? failure = null;
            bool cancelled = false;

            // The request's two phases, for TurnEvent.Usage: the wait for the first chunk with
            // content (the prefill; a role-only leading chunk does not end it) and the streaming
            // after it. Stamped on this clock so a test can advance them.
            long sent = _time.GetTimestamp();
            long? first = null;

            // A turn cancelled before its request (2026-09-25: the /botchat ESC that ends the chat can land as the next bot's
            // turn opens) asks nothing: the server never sees a request its caller has already given up on.
            cancellationToken.ThrowIfCancellationRequested();

            // The picture budget (2026-10-03): the oldest pictures out of the history itself, in a batch, before the request is built.
            var trimmed = _history.ApplyPictureBudget(PictureBudget);
            if (trimmed.Pictures > 0)
            {
                string notice = PictureBudget.TakenOutNotice(trimmed.Pictures, trimmed.Bytes);
                DiagnosticLog.Info(Category, notice);
                yield return new TurnEvent.Notice(notice, IsError: false);
            }

            var request = _history.BuildRequest();
            var (sentPictures, sentBytes) = PictureBudget.Measure(request);
            log.Requests++;

            // The last round trip without the tools, when the turn asks for it (LastRoundAnswers, 2026-09-30).
            bool answerRound = LastRoundAnswers && iteration == MaxToolIterations && _tools.Count > 0;
            var roundOptions = options;
            if (answerRound)
            {
                roundOptions = options.Clone();
                roundOptions.Tools = null;
                DiagnosticLog.Debug(Category, LastRoundLogLine(iteration));
            }

            DiagnosticLog.Debug(Category, RequestLogLine(iteration, request.Count, answerRound ? 0 : _tools.Count, _reasoning));

            // Only MoveNextAsync sits inside the try: C# forbids `yield` inside a try with a catch.
            // The meter's try is a finally alone, so it may: the caller abandoning the turn mid-yield still ends the count.
            Meter?.Begin();
            try
            {
                await using (var stream = _client.GetStreamingResponseAsync(request, roundOptions, cancellationToken).GetAsyncEnumerator(cancellationToken))
                {
                    while (true)
                    {
                        try
                        {
                            if (!await stream.MoveNextAsync().ConfigureAwait(false))
                            {
                                break;
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            // Includes the transport's own TaskCanceledException when the request
                            // timeout fires with our token untouched: that is an error, not barge-in.
                            failure = ex;
                            break;
                        }

                        var update = stream.Current;
                        updates.Add(update);
                        if (first is null && update.Contents.Count > 0)
                        {
                            first = _time.GetTimestamp();
                        }

                        if (Meter is { } meter && update.Contents.Any(c => c is not UsageContent))
                        {
                            meter.Chunk();
                        }

                        // The thinking first (2026-09-26): the server's reasoning deltas, then a <think>
                        // block the filter kept aside. Shown by the host, never part of the reply.
                        string thinking = ReasoningText(update);
                        if (thinking.Length > 0)
                        {
                            yield return new TurnEvent.ThinkingDelta(thinking);
                        }

                        // The decoder can emit "" for the leading bytes of a multi-byte sequence, and
                        // the filter holds back a possible tag start; either way nothing is yielded.
                        string text = filter.Push(update.Text);
                        string tagged = filter.TakeThinking();
                        if (tagged.Length > 0)
                        {
                            tagThinking.Append(tagged);
                            yield return new TurnEvent.ThinkingDelta(tagged);
                        }

                        if (written is not null && text.Length > 0)
                        {
                            text = written.Push(text);
                        }

                        if (text.Length > 0)
                        {
                            partial.Append(text);
                            log.Chars += text.Length;
                            yield return new TurnEvent.TextDelta(text);
                        }
                    }
                }
            }
            finally
            {
                Meter?.End();
            }

            long ended = _time.GetTimestamp();

            // A reply that ends in "<" was held back as a possible tag; it is text after all.
            string held = filter.Flush();
            string unfinished = filter.TakeThinking();
            if (unfinished.Length > 0)
            {
                tagThinking.Append(unfinished);
                yield return new TurnEvent.ThinkingDelta(unfinished);
            }

            if (written is not null)
            {
                held = EndWritten(written, held);
            }

            if (held.Length > 0)
            {
                partial.Append(held);
                log.Chars += held.Length;
                yield return new TurnEvent.TextDelta(held);
            }

            if (filter.SawOrphanClose)
            {
                DiagnosticLog.Info(Category, ThinkingLeakedNote);
            }
            else if (filter.SawBlock)
            {
                DiagnosticLog.Debug(Category, ThinkingBlockNote);
            }

            if (cancelled || failure is not null)
            {
                if (partial.Length > 0)
                {
                    _history.AddAssistant(partial.ToString());
                }

                if (cancelled)
                {
                    DiagnosticLog.Debug(Category, RequestCancelledLogLine(iteration, _time.GetElapsedTime(sent), partial.Length));
                    cancellationToken.ThrowIfCancellationRequested();
                }

                string explained = ExplainFailure(failure!, lastUsage);
                bool hinted = false;
                DiagnosticLog.Info(Category, "Model call failed: " + explained);
                if (first is null && partial.Length == 0 && sentPictures > 0 && LooksLikeOversizedRequest(failure!))
                {
                    // A request with pictures dropped before a byte came back (2026-10-03): most likely its size. Once a
                    // turn, half the pictures it carried come out of the history and the same round goes again.
                    if (!picturesRetried)
                    {
                        picturesRetried = true;
                        var cut = _history.TakePicturesOut(sentPictures / 2, sentBytes / 2);
                        if (cut.Pictures > 0)
                        {
                            string retry = DroppedRequestNotice(sentBytes, sentPictures, cut.Pictures);
                            DiagnosticLog.Info(Category, retry);
                            yield return new TurnEvent.Notice(retry, IsError: false);
                            iteration--;
                            continue;
                        }
                    }

                    explained += OversizedRequestHint(sentBytes, sentPictures);
                    hinted = true;
                }

                if (options.Tools is not null && LooksLikeToolRoleRejection(explained))
                {
                    explained += ToolRoleHint;
                    hinted = true;
                }

                // One readable line with the way on when the failure is one a user can act on (2026-10-04, the UI review); the
                // chain stays in the log above, and a failure this loop has its own hint for keeps the chain and the hint.
                yield return new TurnEvent.Notice(
                    !hinted && !LooksLikeRequestTimeout(failure!) && ModelErrorText.Readable(failure!) is { } readable ? readable : "Model error: " + explained,
                    IsError: true);
                yield break;
            }

            var response = updates.ToChatResponse();
            if (filter.SawOrphanClose || filter.SawBlock)
            {
                // The model produced the tags, but they go back as content next time and prime it
                // to repeat the pattern (and a whole block would re-send the thinking as answer text).
                // The thinking itself stays, as thinking (2026-09-28): the client sends it back as the
                // server's own would be, in reasoning_content, where the template puts the tags itself.
                ReplaceText(response.Messages, partial.ToString());
                if (tagThinking.Length > 0 && response.Messages.Count > 0)
                {
                    response.Messages[0].Contents.Insert(0, new TextReasoningContent(tagThinking.ToString()));
                }
            }

            if (written is not null)
            {
                // The round that asked for words (LastRoundAnswers) keeps its written calls out of the reply and does not run them,
                // and no round runs one to a tool only taken out (WrittenCallsTakenOut).
                TakeWrittenCalls(response.Messages, partial.ToString(), written, iteration,
                    runs: answerRound || catching.Count == 0 ? null : name => catching.Any(t => t.Name == name) || !WrittenCallsTakenOut.Any(t => t.Name == name),
                    notRunNote: answerRound ? LastRoundTextCallsNote : TakenOutTextCallsNote);
            }

            foreach (var message in response.Messages)
            {
                _history.AddMessage(message);
            }

            // Summed from the updates, not response.Usage, so the count does not depend on how
            // ToChatResponse merges. The spans go on the first report only: a server that sent
            // two would otherwise count its time twice. No report (the server does not send
            // usage, or the stream was cut) means no event, never zeros.
            var usage = TokenUsage.Zero;
            foreach (var reported in updates.SelectMany(u => u.Contents).OfType<UsageContent>())
            {
                long firstAt = first ?? ended;
                usage += usage.IsEmpty
                    ? TokenUsage.From(reported.Details, _time.GetElapsedTime(sent, firstAt), _time.GetElapsedTime(firstAt, ended))
                    : TokenUsage.From(reported.Details, TimeSpan.Zero, TimeSpan.Zero);
            }

            if (!usage.IsEmpty)
            {
                DiagnosticLog.Debug(Category, UsageLogLine(usage));
                lastUsage = usage;
                if (ContextGuard is { } measured && !measured.Tripped(usage))
                {
                    compactArmed = true;
                }

                yield return new TurnEvent.Usage(usage);
            }
            else
            {
                DiagnosticLog.Debug(Category, "No usage reported by the server for this request.");
            }

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Where(c => !c.InformationalOnly)
                .ToList();

            if (calls.Count == 0)
            {
                log.Ending = TurnCompleted;
                yield break;
            }

            var results = new List<FunctionResultContent>(calls.Count);
            var fetched = new List<ImageAttachment>();
            var fetchers = new List<string>();
            log.ToolCalls += calls.Count;
            foreach (var call in calls)
            {
                string argumentsJson = SerializeArguments(call.Arguments);
                // The arguments as the model sent them, for the --log file: the transcript shows a
                // quiet tool's result alone, and a shape a tool refuses is otherwise never seen.
                DiagnosticLog.Debug(Category, ToolCallLogLine(call.Name, argumentsJson));
                yield return new TurnEvent.ToolCall(call.Name, call.CallId, argumentsJson);

                var (result, pictures, diff, shown) = await InvokeToolWithDiffAsync(_tools, call, cancellationToken).ConfigureAwait(false);
                results.Add(ResultContent(call, result));
                fetched.AddRange(pictures);
                if (pictures.Count > 0 && !fetchers.Contains(call.Name, StringComparer.Ordinal))
                {
                    fetchers.Add(call.Name);
                }

                yield return new TurnEvent.ToolResult(call.Name, call.CallId, result, pictures.Count > 0 ? pictures : null, diff, shown);
            }

            _history.AddToolResults(results);

            // The pictures the iteration fetched, in one carrier after the results (none for none), crediting the tools that fetched them (2026-09-24).
            _history.AddToolImages(fetched, string.Join(" and ", fetchers));

            // The mid-turn guard, after the iteration's results are in: they are the last
            // iteration a prune keeps, and a stop leaves no call unanswered. It judges the next request —
            // the reported usage (the reply's own tokens among it) plus the results and pictures just appended.
            long projected = ContextGuard is null || usage.IsEmpty ? 0 : usage.Total + ConversationCompactor.EstimateTokens([new ChatMessage(ChatRole.Tool, [.. results])]) + (long)fetched.Count * ConversationCompactor.PictureTokens;
            if (ContextGuard is { } guard && guard.Tripped(projected))
            {
                int percent = guard.PercentOf(projected);
                if (guard.Mode == ToolCompactMode.Stop)
                {
                    DiagnosticLog.Info(Category, $"Context at {percent}% of {guard.WindowTokens} tokens after {iteration} tool iteration(s); stopping the turn (LLM tool compact type is stop).");
                    log.Ending = TurnStopped;
                    yield return new TurnEvent.Notice(TurnStoppedNotice(percent), IsError: true);
                    yield break;
                }

                var (shrunk, pruned) = ConversationCompactor.PruneRecent(_history.Messages, guard.ProtectSkills);
                if (pruned > 0)
                {
                    projected -= ConversationCompactor.EstimateTokens(_history.Messages) - ConversationCompactor.EstimateTokens(shrunk);
                    _history.Replace(shrunk);
                    DiagnosticLog.Info(Category, $"Context at {percent}% of {guard.WindowTokens} tokens after {iteration} tool iteration(s); pruned {pruned} tool result(s) from this turn.");
                    yield return new TurnEvent.Notice(TurnPrunedNotice(percent, pruned), IsError: false);
                }
                else
                {
                    DiagnosticLog.Debug(Category, $"Context at {percent}% of {guard.WindowTokens} tokens after {iteration} tool iteration(s); nothing in this turn to prune.");
                }

                // Compact: the prune was not enough — the turns before this one first, then this turn's earlier iterations.
                if (guard.Mode == ToolCompactMode.Compact && guard.Tripped(projected))
                {
                    if (!compactArmed)
                    {
                        DiagnosticLog.Debug(Category, $"Context still near {guard.PercentOf(projected)}% after a mid-turn compact; the next summary waits for a request under {guard.Percent}%.");
                    }
                    else
                    {
                        compactArmed = false;
                        for (int stage = 1; stage <= 2 && guard.Tripped(projected); stage++)
                        {
                            bool thisTurn = stage == 2;
                            var step = thisTurn ? PlanTurnCompact() : PlanOlderCompact();
                            if (step is null)
                            {
                                continue;
                            }

                            int at = guard.PercentOf(projected);
                            yield return new TurnEvent.Compacting(at);
                            var done = await CompactMidTurnAsync(step, cancellationToken).ConfigureAwait(false);
                            if (done.Result is not { } compacted)
                            {
                                DiagnosticLog.Info(Category, CompactFailedPrefix + done.Failure);
                                yield return new TurnEvent.Notice(CompactFailedPrefix + done.Failure, IsError: true);
                                break;
                            }

                            projected -= done.Saved;
                            DiagnosticLog.Info(Category, string.Create(CultureInfo.InvariantCulture,
                                $"Context at {at}% of {guard.WindowTokens} tokens after {iteration} tool iteration(s); summarised {(thisTurn ? "this turn's earlier iterations" : "the turns before this one")}: {compacted.MessagesBefore} messages → {compacted.MessagesAfter}, about {guard.PercentOf(Math.Max(projected, 0))}% now."));
                            yield return new TurnEvent.Compacted(compacted, at, thisTurn);
                        }

                        if (guard.Tripped(projected))
                        {
                            DiagnosticLog.Info(Category, $"Context still near {guard.PercentOf(projected)}% after the mid-turn compact: nothing more to summarise; the server's limit answers.");
                        }
                    }
                }
            }
        }

        DiagnosticLog.Warn(Category, $"Stopped after {MaxToolIterations} tool iterations without a final answer.");
        log.Ending = TurnStopped;
        yield return new TurnEvent.Notice($"Stopped after {MaxToolIterations} tool iterations without a final answer.", IsError: true);
    }

    /// <summary>
    /// The turn's pair of log lines (2026-09-19): <see cref="TurnStartedLogLine"/> at construction,
    /// <see cref="TurnEndedLogLine"/> with the tally on dispose. A using declaration in the iterator,
    /// so the closing line runs whichever way the turn ends — including the consumer disposing a
    /// cancelled enumerator — without a try/finally around the loop. The loop counts into it.
    /// </summary>
    private sealed class TurnLog : IDisposable
    {
        private readonly int _number;
        private readonly TimeProvider _time;
        private readonly long _started;
        private readonly CancellationToken _token;

        public TurnLog(int number, string text, int images, int openingCalls, TimeProvider time, CancellationToken token)
        {
            _number = number;
            _time = time;
            _started = time.GetTimestamp();
            _token = token;
            DiagnosticLog.Info(TurnCategory, TurnStartedLogLine(number, text, images, openingCalls));
        }

        public int Requests { get; set; }

        public int ToolCalls { get; set; }

        public int Chars { get; set; }

        /// <summary>How the turn ended; <see cref="TurnFailed"/> until the loop says otherwise (a model error, a throw), <see cref="TurnCancelled"/> whenever the token was cancelled.</summary>
        public string Ending { get; set; } = TurnFailed;

        public void Dispose() =>
            DiagnosticLog.Info(TurnCategory, TurnEndedLogLine(_number, _time.GetElapsedTime(_started), Requests, ToolCalls, Chars, _token.IsCancellationRequested ? TurnCancelled : Ending));
    }

    /// <summary>The log category of the turn's opening and closing lines (the screen's reason line too), so a run reads off <c>Turn:</c>.</summary>
    public const string TurnCategory = "Turn";

    /// <summary>The endings <see cref="TurnEndedLogLine"/> names: the reply ran to its end; a guard, the budget or the iteration cap stopped it; the model's error or a throw failed it; the token cut it.</summary>
    public const string TurnCompleted = "completed";
    public const string TurnStopped = "stopped";
    public const string TurnFailed = "failed";
    public const string TurnCancelled = "cancelled";

    /// <summary>The suffix on an opening call's <c>Tool call</c> line: seeded, not the model's.</summary>
    public const string OpeningSuffix = " (opening)";

    /// <summary>The turn's opening line: <c>Turn 3 started: "why is the build red…" (2 images, 3 opening calls)</c> — the parenthesis only with either count. Pinned.</summary>
    public static string TurnStartedLogLine(int number, string text, int images, int openingCalls)
    {
        var extras = new List<string>(2);
        if (images > 0)
        {
            extras.Add(Plural(images, "image"));
        }

        if (openingCalls > 0)
        {
            extras.Add(Plural(openingCalls, "opening call"));
        }

        string tail = extras.Count == 0 ? "" : " (" + string.Join(", ", extras) + ")";
        return string.Create(CultureInfo.InvariantCulture, $"Turn {number} started: {LogText.Quoted(text)}{tail}");
    }

    /// <summary>The turn's closing line: <c>Turn 3 ended after 12.4 s: 2 requests, 3 tool calls, 512 chars, completed</c>. Pinned.</summary>
    public static string TurnEndedLogLine(int number, TimeSpan elapsed, int requests, int toolCalls, int chars, string ending) =>
        string.Create(CultureInfo.InvariantCulture, $"Turn {number} ended after {elapsed.TotalSeconds:F1} s: {Plural(requests, "request")}, {Plural(toolCalls, "tool call")}, {chars} chars, {ending}");

    /// <summary>The thinking one streamed update carried (<see cref="TextReasoningContent"/>, joined), or empty.</summary>
    internal static string ReasoningText(ChatResponseUpdate update) =>
        string.Concat(update.Contents.OfType<TextReasoningContent>().Select(r => r.Text));

    /// <summary>One request's line: <c>Request 2: 14 messages, 19 tools, reasoning medium</c> (<c>no tools</c>, <c>reasoning default</c> when unset). Pinned.</summary>
    public static string RequestLogLine(int iteration, int messages, int tools, ReasoningEffort? reasoning) =>
        string.Create(CultureInfo.InvariantCulture, $"Request {iteration}: {Plural(messages, "message")}, {(tools == 0 ? "no tools" : Plural(tools, "tool"))}, reasoning {(reasoning is { } effort ? effort.ToString().ToLowerInvariant() : "default")}");

    /// <summary>A request cut by the token: <c>Request 2 cancelled after 3.2 s: 140 chars received</c>. Pinned.</summary>
    public static string RequestCancelledLogLine(int iteration, TimeSpan elapsed, int chars) =>
        string.Create(CultureInfo.InvariantCulture, $"Request {iteration} cancelled after {elapsed.TotalSeconds:F1} s: {chars} chars received");

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

    /// <summary>
    /// The summariser behind <c>/compact</c> (<see cref="ConversationCompactor"/>): one request over
    /// <see cref="ConversationCompactor.SummaryInstruction"/>, <paramref name="transcript"/> as it
    /// was sent before, and <see cref="ConversationCompactor.SummaryRequest"/> last — no tools,
    /// thinking off (the adapter turns <see cref="ReasoningEffort.None"/> into the wire fields),
    /// the history untouched. Streamed like a turn so the usage carries the same two spans. A
    /// leaked <c>&lt;think&gt;</c> is filtered like a reply's. The transport's failures and
    /// cancellation propagate. An empty answer is asked for once more over
    /// <see cref="ConversationCompactor.LeanTranscript"/> (2026-09-28), the two requests' usage summed;
    /// a second empty one is an <see cref="EmptySummaryException"/> saying why.
    /// </summary>
    public Task<(string Text, TokenUsage? Usage)> SummarizeAsync(IReadOnlyList<ChatMessage> transcript, string? focus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return SummarizeAsync(ConversationCompactor.SummaryInstruction, transcript, ConversationCompactor.SummaryRequest(focus), cancellationToken);
    }

    /// <summary>
    /// <see cref="SummarizeAsync(IReadOnlyList{ChatMessage}, string?, CancellationToken)"/> with its own system prompt and
    /// closing line (2026-09-28): the mid-turn compact's second stage asks for a progress note
    /// (<see cref="ConversationCompactor.TurnProgressInstruction"/>) rather than a conversation's summary.
    /// </summary>
    private async Task<(string Text, TokenUsage? Usage)> SummarizeAsync(string instruction, IReadOnlyList<ChatMessage> transcript, string requestLine, CancellationToken cancellationToken)
    {
        // Within the picture budget (2026-10-03): a copy, the history untouched; a summary of a picture-heavy past sent every
        // picture once and could fail as the turn had.
        var request = new List<ChatMessage>(transcript.Count + 2) { new(ChatRole.System, instruction) };
        request.AddRange(PictureBudget.Apply(transcript).Messages);
        request.Add(new ChatMessage(ChatRole.User, requestLine));
        SummaryAttempt attempt;
        try
        {
            attempt = await RequestSummaryAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && PictureBudget.Measure(request).Pictures > 0)
        {
            // A first try with pictures that failed outright (2026-10-03) goes on to the lean transcript, which has none.
            DiagnosticLog.Debug(Category, "Summary request failed with pictures in it (" + Explain(ex) + ").");
            attempt = new SummaryAttempt("", null, null, 0, null);
        }

        var usage = attempt.Usage;
        if (attempt.Text.Length == 0)
        {
            // The second try (2026-09-28): the same ask over a plain dialogue — no call for a tool-primed model
            // to carry on from, the results cut, so a request that filled the context leaves room to write.
            DiagnosticLog.Debug(Category, EmptySummaryLogLine(attempt) + " Asking again over the lean transcript.");
            var lean = new List<ChatMessage> { new(ChatRole.System, instruction) };
            lean.AddRange(ConversationCompactor.LeanTranscript([.. transcript, new ChatMessage(ChatRole.User, requestLine)]));
            attempt = await RequestSummaryAsync(lean, cancellationToken).ConfigureAwait(false);
            usage = usage is { } before && attempt.Usage is { } after ? before + after : usage ?? attempt.Usage;
            if (attempt.Text.Length == 0)
            {
                DiagnosticLog.Debug(Category, EmptySummaryLogLine(attempt));
                throw new EmptySummaryException(EmptySummaryReason(attempt));
            }
        }

        // --log only: what the model kept is the first thing a field report needs.
        DiagnosticLog.Debug(Category, "Summary: " + attempt.Text);
        if (usage is not { } total)
        {
            DiagnosticLog.Debug(Category, "No usage reported by the server for the summary request.");
            return (attempt.Text, null);
        }

        DiagnosticLog.Debug(Category, string.Create(CultureInfo.InvariantCulture,
            $"Summary usage: {total.Input} in, {total.Output} out, {total.Total} total; first token after {total.ToFirstToken.TotalSeconds:F2}s, streamed {total.Generating.TotalSeconds:F2}s."));
        return (attempt.Text, total);
    }

    /// <summary>
    /// One summariser request's outcome (2026-09-28): its text (trimmed, a leaked <c>&lt;think&gt;</c> filtered; empty
    /// for none), its usage stamped with the two spans (null when the server reported none), and what an empty reply is
    /// explained by — the last finish reason, the thinking's characters (streamed as thinking or filtered from the text)
    /// and the first tool call's name.
    /// </summary>
    private sealed record SummaryAttempt(string Text, TokenUsage? Usage, ChatFinishReason? Finish, int ThinkingChars, string? ToolCall);

    private async Task<SummaryAttempt> RequestSummaryAsync(List<ChatMessage> request, CancellationToken cancellationToken)
    {
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } };
        Sampling?.ApplyTo(options);
        var filter = new ThinkTagFilter();
        var text = new StringBuilder();
        var usage = TokenUsage.Zero;
        ChatFinishReason? finish = null;
        int raw = 0;
        int thinking = 0;
        string? toolCall = null;
        long sent = _time.GetTimestamp();
        long? first = null;
        await foreach (var update in _client.GetStreamingResponseAsync(request, options, cancellationToken).ConfigureAwait(false))
        {
            if (first is null && update.Contents.Count > 0)
            {
                first = _time.GetTimestamp();
            }

            string delta = update.Text;
            raw += delta.Length;
            text.Append(filter.Push(delta));
            thinking += ReasoningText(update).Length;
            toolCall ??= update.Contents.OfType<FunctionCallContent>().FirstOrDefault()?.Name;
            finish = update.FinishReason ?? finish;
            foreach (var reported in update.Contents.OfType<UsageContent>())
            {
                usage += TokenUsage.From(reported.Details, TimeSpan.Zero, TimeSpan.Zero);
            }
        }

        long ended = _time.GetTimestamp();
        text.Append(filter.Flush());
        thinking += raw - text.Length;   // what the filter took out of the text: a <think> block streamed as content
        long firstAt = first ?? ended;
        TokenUsage? timed = usage.IsEmpty ? null : usage with { ToFirstToken = _time.GetElapsedTime(sent, firstAt), Generating = _time.GetElapsedTime(firstAt, ended) };
        return new SummaryAttempt(text.ToString().Trim(), timed, finish, thinking, toolCall);
    }

    /// <summary>Why an attempt came back empty, most telling first: a tool call, the length limit, thinking alone, else no clue.</summary>
    private static string EmptySummaryReason(SummaryAttempt attempt) =>
        attempt.ToolCall is { } tool ? EmptySummaryToolError(tool)
        : attempt.Finish == ChatFinishReason.Length ? EmptySummaryLengthError
        : attempt.ThinkingChars > 0 ? EmptySummaryThinkingError(attempt.ThinkingChars)
        : EmptySummaryError;

    /// <summary>The <c>--log</c> line for an empty attempt: <c>Empty summary: finish length, 1,234 characters of thinking, no tool call, 812 tokens out.</c></summary>
    private static string EmptySummaryLogLine(SummaryAttempt attempt) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Empty summary: finish {(attempt.Finish is { } finish ? finish.Value : "none")}, {attempt.ThinkingChars:N0} characters of thinking, {(attempt.ToolCall is { } tool ? "a call of " + tool : "no tool call")}, {(attempt.Usage is { } usage ? usage.Output.ToString(CultureInfo.InvariantCulture) : "no")} tokens out.");

    /// <summary>
    /// One model request over <paramref name="request"/> as given (its own system message first),
    /// with <paramref name="tools"/> offered and <paramref name="effort"/> as the reasoning level:
    /// the reply's messages (to append to the caller's own list), its text with a leaked
    /// <c>&lt;think&gt;</c> filtered, the tool calls it asked for (none = the model is done) and the
    /// usage the server reported (null for none), the two spans stamped as a turn's are.
    /// </summary>
    public sealed record SideResponse(IReadOnlyList<ChatMessage> Messages, string Text, IReadOnlyList<FunctionCallContent> Calls, TokenUsage? Usage)
    {
        /// <summary>
        /// The calls <see cref="RequestAsync"/>'s <c>textToolCalls</c> caught written out as text (2026-09-30, code review): each
        /// tool's name and arguments as written, run (then in <see cref="Calls"/> too) or only taken out. Empty with none.
        /// </summary>
        public IReadOnlyList<(string Name, string Arguments)> WrittenCalls { get; init; } = [];

        /// <summary>
        /// The words after the last call written out as text, caught or dropped as broken, trimmed (2026-09-30, code review: the
        /// <c>/botchat</c> picture writer's lead-in before a call — "Let me check the tag list first." — was its ComfyUI prompt);
        /// null when <see cref="Text"/> had nothing written taken out of it.
        /// </summary>
        public string? AfterWritten { get; init; }
    }

    /// <summary>
    /// The primitive behind a side loop that runs beside a turn (<see cref="Skills.SkillLearner"/>):
    /// one streamed request, the history untouched, nothing of this instance read but the client,
    /// the clock and the <see cref="Sampling"/> reference — so it is safe while <see cref="RunTurnAsync"/> streams. The transport's
    /// failures and cancellation propagate; the caller explains them. A null <paramref name="effort"/> sends none, as the turn
    /// does for a reasoning left unset, and <paramref name="format"/> is the request's <c>response_format</c> (2026-09-28, for
    /// <c>/test</c>'s structured-output tests: a JSON schema the adapter writes as <c>json_schema</c>); null asks for none.
    /// <para><paramref name="textToolCalls"/> (2026-09-30, code review: the <c>/botchat</c> picture writer had its own copy of the
    /// turn's handling, already short of it) is <see cref="TextToolCalls"/> for a side loop: the tools whose calls written out as
    /// text are caught, each its name and parameter names (<see cref="WrittenCallFilter"/>, <see cref="WrittenCallNames"/>; null,
    /// the default, catches none). They are kept out of <see cref="SideResponse.Text"/> and the messages, read in
    /// <see cref="SideResponse.WrittenCalls"/> and <see cref="SideResponse.AfterWritten"/>, and in a request that offers tools
    /// become real calls in <see cref="SideResponse.Calls"/> (<see cref="AddWrittenCalls"/>) — the turn's rule; in one that offers
    /// none, the answer round, they are only taken out. The main chat's side loops leave it off, as its turn does: a reply that
    /// shows a call as an example would otherwise run it.</para>
    /// </summary>
    public async Task<SideResponse> RequestAsync(IReadOnlyList<ChatMessage> request, IReadOnlyList<AIFunction> tools, ReasoningEffort? effort, CancellationToken cancellationToken, ChatResponseFormat? format = null, IReadOnlyList<(string Name, IReadOnlyList<string> Parameters)>? textToolCalls = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tools);
        var options = new ChatOptions
        {
            Tools = tools.Count > 0 ? new List<AITool>(tools) : null,
            Reasoning = effort is { } level ? new ReasoningOptions { Effort = level } : null,
            ResponseFormat = format,
        };
        Sampling?.ApplyTo(options);

        var updates = new List<ChatResponseUpdate>();
        var filter = new ThinkTagFilter();
        var text = new StringBuilder();
        long sent = _time.GetTimestamp();
        long? first = null;
        await foreach (var update in _client.GetStreamingResponseAsync(request, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            if (first is null && update.Contents.Count > 0)
            {
                first = _time.GetTimestamp();
            }

            text.Append(filter.Push(update.Text));
        }

        long ended = _time.GetTimestamp();
        text.Append(filter.Flush());

        var response = updates.ToChatResponse();
        if (filter.SawOrphanClose || filter.SawBlock)
        {
            ReplaceText(response.Messages, text.ToString());
        }

        IReadOnlyList<(string Name, string Arguments)> writtenCalls = [];
        string? afterWritten = null;
        if (textToolCalls is not null)
        {
            var written = WrittenCallFilter(textToolCalls);
            string kept = EndWritten(written, text.ToString());
            // The ids' round: one past the assistant messages already in the request, so a later round's never repeat an earlier's.
            int round = request.Count(m => m.Role == ChatRole.Assistant) + 1;
            TakeWrittenCalls(response.Messages, kept, written, round, runs: tools.Count > 0 ? _ => true : null);
            text.Clear().Append(kept);
            writtenCalls = written.Calls;
            afterWritten = written.Calls.Count > 0 || written.SawBroken ? kept[written.LastCallEnd..].Trim() : null;
        }

        var usage = TokenUsage.Zero;
        foreach (var reported in updates.SelectMany(u => u.Contents).OfType<UsageContent>())
        {
            long firstAt = first ?? ended;
            usage += usage.IsEmpty
                ? TokenUsage.From(reported.Details, _time.GetElapsedTime(sent, firstAt), _time.GetElapsedTime(firstAt, ended))
                : TokenUsage.From(reported.Details, TimeSpan.Zero, TimeSpan.Zero);
        }

        var calls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Where(c => !c.InformationalOnly)
            .ToList();

        return new SideResponse(response.Messages.ToList(), text.ToString().Trim(), calls, usage.IsEmpty ? null : usage) { WrittenCalls = writtenCalls, AfterWritten = afterWritten };
    }

    /// <summary>
    /// Replaces the text of a response with what the filter let through: every
    /// <see cref="TextContent"/> goes, and the cleaned text (if any) becomes one item at the front
    /// of the first message. Function calls and reasoning items stay where they are.
    /// </summary>
    /// <summary>The <c>--log</c> line when a call written as text never closed and was dropped (2026-09-25).</summary>
    internal const string TextCallBrokenNote = "The model wrote a tool call out as text and never closed it; it was dropped from the reply.";

    /// <summary>The <c>--log</c> line when the last round trip is asked without the tools (<see cref="LastRoundAnswers"/>, 2026-09-30).</summary>
    internal static string LastRoundLogLine(int iteration) =>
        "Round trip " + iteration.ToString(CultureInfo.InvariantCulture) + " is the turn's last: asked without the tools, for an answer.";

    /// <summary>The <c>--log</c> line when the model wrote a tool call as text in the round that asked for words (2026-09-30).</summary>
    internal const string LastRoundTextCallsNote = "The model wrote a tool call out as text in the round that asked for an answer; it was dropped from the reply and not run.";

    /// <summary>The <c>--log</c> line when the model wrote out a call to a tool only taken out (<see cref="WrittenCallsTakenOut"/>, 2026-09-30).</summary>
    internal const string TakenOutTextCallsNote = "The model wrote out, as text, a call to a tool it is not offered; it was dropped from the reply and not run.";

    /// <summary>The <c>--log</c> line when calls written as text came beside native ones (2026-09-30, code review): taken out, not run.</summary>
    internal const string WrittenBesideNativeNote = "The model wrote tool calls out as text beside native ones; they were dropped from the reply and not run.";

    /// <summary>
    /// The parse error a call written as text carries when its arguments do not parse (2026-09-30, code review: left out, the
    /// words beside it were the reply — the <c>/botchat</c> picture writer's lead-in its ComfyUI prompt); the model is answered
    /// <c>Error: the arguments for '…' could not be parsed: …</c>, as for a native call's.
    /// </summary>
    internal const string WrittenArgumentsUnparsed = "the call was written as text, and its arguments are neither name=value pairs nor a JSON object";

    /// <summary>A tool schema's parameter names (its <c>properties</c>), for the written call's line form (later on 2026-09-25); none when it has none.</summary>
    internal static IReadOnlyList<string> ParameterNames(System.Text.Json.JsonElement schema) =>
        schema.ValueKind == System.Text.Json.JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == System.Text.Json.JsonValueKind.Object
            ? properties.EnumerateObject().Select(property => property.Name).ToList()
            : [];

    /// <summary>The <c>--log</c> line for a call the model wrote out as text (2026-09-25): <c>Text tool call generate_image: prompt="…"</c>, cut like <see cref="ToolCallLogLine"/>.</summary>
    internal static string TextCallLogLine(string name, string arguments) =>
        "Text tool call " + name + ": " + (arguments.Length <= ToolCallLogChars ? arguments : arguments[..(ToolCallLogChars - 1)] + "…");

    /// <summary>
    /// <paramref name="tools"/>' names, each with its schema's parameter names for the line form (2026-09-30, code review): what
    /// <see cref="WrittenCallFilter"/> and <see cref="RequestAsync"/>'s <c>textToolCalls</c> take, so a caller with no tool instance
    /// at hand — the picture writer's last round — passes the names alone.
    /// </summary>
    internal static IReadOnlyList<(string Name, IReadOnlyList<string> Parameters)> WrittenCallNames(IEnumerable<AIFunction> tools) =>
        tools.Select(t => (t.Name, ParameterNames(t.JsonSchema))).ToList();

    /// <summary>
    /// The filter for calls to <paramref name="tools"/> written out as text, each with its parameter names for the line form
    /// (2026-09-30, code review: built here once, for the turn and <see cref="RequestAsync"/> alike).
    /// </summary>
    internal static TextToolCallFilter WrittenCallFilter(IEnumerable<(string Name, IReadOnlyList<string> Parameters)> tools) => new(tools);

    /// <summary>
    /// The end of a response's text through <paramref name="written"/>: <paramref name="rest"/> pushed, the filter flushed, and a
    /// call left open logged (<see cref="TextCallBrokenNote"/>). What was let through of the two. Shared by the turn and
    /// <see cref="RequestAsync"/> (2026-09-30, code review).
    /// </summary>
    private static string EndWritten(TextToolCallFilter written, string rest)
    {
        string kept = written.Push(rest) + written.Flush();
        if (written.SawBroken)
        {
            DiagnosticLog.Info(Category, TextCallBrokenNote);
        }

        return kept;
    }

    /// <summary>
    /// A response's written calls, once <paramref name="written"/> has seen all its text (2026-09-30, shared by the turn and
    /// <see cref="RequestAsync"/>): with a call caught or a broken one dropped, the messages' text becomes <paramref name="kept"/>
    /// — the history then carries no markup to prime the model with — and each call <paramref name="runs"/> picks is added as a
    /// real one (<see cref="AddWrittenCalls"/>); the others are logged as not run (<paramref name="notRunNote"/>). Null
    /// <paramref name="runs"/> runs none.
    /// </summary>
    internal static void TakeWrittenCalls(IList<ChatMessage> messages, string kept, TextToolCallFilter written, int iteration, Func<string, bool>? runs, string notRunNote = LastRoundTextCallsNote)
    {
        if (written.Calls.Count == 0 && !written.SawBroken)
        {
            return;
        }

        // The written calls go into the history as real ones, the reply's text without them, so the model sees what ran.
        ReplaceText(messages, kept);
        List<(string Name, string Arguments)> run = runs is null ? [] : written.Calls.Where(call => runs(call.Name)).ToList();
        if (run.Count < written.Calls.Count)
        {
            DiagnosticLog.Info(Category, notRunNote);
        }

        if (run.Count > 0)
        {
            AddWrittenCalls(messages, run, iteration);
        }
    }

    /// <summary>
    /// The calls <see cref="TextToolCallFilter"/> caught, added to the response's last message as real
    /// <see cref="FunctionCallContent"/>s (2026-09-25) with ids of their own (<see cref="WrittenCallId"/>), so the loop runs them as
    /// it runs native ones. A call whose arguments do not parse is added too, carrying <see cref="WrittenArgumentsUnparsed"/>, so the
    /// model is answered the error and asked again (2026-09-30, code review: left out, the words beside it were the reply).
    /// <para>None is added when the response carries a native call (2026-09-30, code review): a server that parses the calls but
    /// leaves the markup in the content would otherwise have each run twice — two pictures started, a skill's content sent twice —
    /// and the two copies' values are not spelled alike (<c>7.0</c> against <c>7</c>) for a comparison to catch it. Two calls written
    /// alike with no native one both run.</para>
    /// </summary>
    internal static void AddWrittenCalls(IList<ChatMessage> messages, IReadOnlyList<(string Name, string Arguments)> calls, int iteration)
    {
        foreach (var (name, text) in calls)
        {
            DiagnosticLog.Info(Category, TextCallLogLine(name, text));
        }

        if (messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any(native => !native.InformationalOnly))
        {
            DiagnosticLog.Info(Category, WrittenBesideNativeNote);
            return;
        }

        if (messages.Count == 0)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, (string?)null));
        }

        int n = 0;
        foreach (var (name, text) in calls)
        {
            string id = WrittenCallId(iteration, ++n);
            messages[^1].Contents.Add(TextToolCallFilter.ParseArguments(text) is { } arguments
                ? new FunctionCallContent(id, name, arguments)
                : new FunctionCallContent(id, name) { Exception = new FormatException(WrittenArgumentsUnparsed) });
        }
    }

    /// <summary>
    /// The prefix of a call written as text's id (2026-09-30, code review: <c>text-call-1-1</c> broke the opening calls' rule, and a
    /// Mistral-family template refused the round after it).
    /// </summary>
    internal const string WrittenCallIdPrefix = "neonw";

    /// <summary>
    /// The id of the <paramref name="n"/>th call written as text in round trip <paramref name="iteration"/>: <see cref="WrittenCallIdPrefix"/>
    /// and four base-36 digits of the two — nine alphanumerics, the opening calls' rule. Unique for up to 35 a round trip.
    /// </summary>
    internal static string WrittenCallId(int iteration, int n)
    {
        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        long value = ((long)iteration * 36 + n) % (36 * 36 * 36 * 36);
        Span<char> tail = stackalloc char[4];
        for (int i = tail.Length - 1; i >= 0; i--)
        {
            tail[i] = Digits[(int)(value % 36)];
            value /= 36;
        }

        return WrittenCallIdPrefix + new string(tail);
    }

    internal static void ReplaceText(IList<ChatMessage> messages, string text)
    {
        foreach (var message in messages)
        {
            for (int i = message.Contents.Count - 1; i >= 0; i--)
            {
                if (message.Contents[i] is TextContent)
                {
                    message.Contents.RemoveAt(i);
                }
            }
        }

        if (text.Length > 0 && messages.Count > 0)
        {
            messages[0].Contents.Insert(0, new TextContent(text));
        }
    }

    /// <summary>
    /// Runs one call against <paramref name="tools"/> and returns the text that goes back to the
    /// model, with the pictures a <see cref="ToolImageResult"/> carried (empty otherwise). Every
    /// branch returns — unknown tool, unparseable arguments, a throwing tool — so the caller can
    /// always attach the <c>CallId</c>. Only cancellation escapes. Static, so a side loop
    /// (<see cref="Skills.SkillLearner"/>) runs its own tools through the same rules.
    /// </summary>
    /// <summary>The result for a tool that threw (2026-09-18: <c>Error: boom failed: kaboom</c>, the <c>Error:</c> prefix every other tool sentence carries; <c>Error executing tool '…'</c> before). Pinned.</summary>
    public static string ToolFailed(string name, string message) => $"Error: {name} failed: {message}";

    public static async Task<(string Text, IReadOnlyList<ImageAttachment> Images)> InvokeToolAsync(IReadOnlyList<AIFunction> tools, FunctionCallContent call, CancellationToken cancellationToken)
    {
        var (text, images, _, _) = await InvokeToolWithDiffAsync(tools, call, cancellationToken).ConfigureAwait(false);
        return (text, images);
    }

    /// <summary>
    /// <see cref="InvokeToolAsync"/> with the <see cref="FileDiff"/> a <see cref="ToolDiffResult"/> carried (2026-10-03), for the
    /// turn's own calls, whose host draws it; a side loop has nowhere to and calls the two-part form. <c>Shown</c> is a
    /// <see cref="ToolShownResult"/>'s line for the user alone (later on 2026-10-03, the forbidden strings), null otherwise.
    /// </summary>
    public static async Task<(string Text, IReadOnlyList<ImageAttachment> Images, FileDiff? Diff, string? Shown)> InvokeToolWithDiffAsync(IReadOnlyList<AIFunction> tools, FunctionCallContent call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(call);

        // Exact, ordinal: a namespaced or re-cased name is an unknown tool, and the model is told so.
        // A model's mistake is the Error: result, logged at Info — never a Warning on the transcript
        // (the 🛠️ echo already shows it in a turn; a reflection's shows nowhere but the log).
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.Ordinal));
        if (tool is null)
        {
            DiagnosticLog.Info(Category, $"The model called unknown tool '{call.Name}'.");
            return ($"Error: unknown tool '{call.Name}'.", [], null, null);
        }

        if (call.Exception is not null)
        {
            // The adapter puts a JSON parse failure here instead of throwing.
            DiagnosticLog.Info(Category, $"Arguments for tool '{call.Name}' could not be parsed: {call.Exception.Message}");
            return ($"Error: the arguments for '{call.Name}' could not be parsed: {call.Exception.Message}", [], null, null);
        }

        return await InvokeAsync(tool, call, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The invocation itself, shared by the model's calls and the opening call: a throwing tool is a sentence, only cancellation escapes.</summary>
    private static async Task<(string Text, IReadOnlyList<ImageAttachment> Images, FileDiff? Diff, string? Shown)> InvokeAsync(AIFunction tool, FunctionCallContent call, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        (string Text, IReadOnlyList<ImageAttachment> Images, FileDiff? Diff, string? Shown) answer;
        try
        {
            var arguments = new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>());
            object? value = await tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            answer = value switch
            {
                null => ("(no result)", [], null, null),
                string s => (s, [], null, null),
                ToolImageResult pictures => (pictures.Text, pictures.Images, null, null),
                ToolDiffResult changed => (changed.Text, [], changed.Diff, null),
                ToolShownResult shown => (shown.Text, [], null, shown.Shown),
                _ => (value.ToString() ?? "(no result)", [], null, null),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn(Category, $"Tool '{call.Name}' threw.", ex);
            answer = (ToolFailed(call.Name, ex.Message), [], null, null);
        }

        // The result's shape for the log (2026-09-19): every call — the model's, the opening
        // ones, a reflection's — and a tool's own refusal at Info, since an Error: sentence never
        // throws and the transcript shows a quiet tool's line alone.
        if (IsToolError(answer.Text))
        {
            DiagnosticLog.Info(Category, ToolErrorLogLine(call.Name, answer.Text));
        }
        else
        {
            DiagnosticLog.Debug(Category, ToolResultLogLine(call.Name, answer.Text, answer.Images.Count, Stopwatch.GetElapsedTime(started)));
        }

        return answer;
    }

    /// <summary>Whether a tool's answer is one of its refusals: the <c>Error:</c> prefix every tool sentence carries.</summary>
    public static bool IsToolError(string result) => result.StartsWith("Error:", StringComparison.Ordinal);

    /// <summary>A tool's answer for the log: <c>Tool result read_file: 1,234 chars in 12 ms — "line one…"</c> (<c>, 2 pictures</c> after the chars when it carried any). Pinned.</summary>
    public static string ToolResultLogLine(string name, string result, int pictures, TimeSpan elapsed)
    {
        string carried = pictures == 0 ? "" : ", " + Plural(pictures, "picture");
        return string.Create(CultureInfo.InvariantCulture, $"Tool result {name}: {result.Length:N0} chars{carried} in {elapsed.TotalMilliseconds:F0} ms — {LogText.Quoted(result)}");
    }

    /// <summary>A tool's refusal for the log: <c>Tool read_file answered an error: Error: no such file…</c>. Pinned.</summary>
    public static string ToolErrorLogLine(string name, string result) =>
        "Tool " + name + " answered an error: " + LogText.Excerpt(result);

    /// <summary>
    /// The result content for a call: a loaded skill's instructions (a <c>load_skill</c> answer that
    /// is no <c>Error:</c>) are tagged <see cref="ConversationHistory.SkillResultKey"/>, the mark the
    /// compactor's protection reads (<see cref="ConversationCompactor.Prune"/>); never on the wire.
    /// </summary>
    public static FunctionResultContent ResultContent(FunctionCallContent call, string result)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(result);
        var content = new FunctionResultContent(call.CallId, result);
        if (string.Equals(call.Name, LoadSkillTool.ToolName, StringComparison.Ordinal) && !result.StartsWith("Error:", StringComparison.Ordinal))
        {
            content.AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationHistory.SkillResultKey] = true };
        }

        return content;
    }

    /// <summary>The most characters of a call's arguments the <c>--log</c> line repeats (a <c>write_file</c> body would swamp the file).</summary>
    public const int ToolCallLogChars = 2000;

    /// <summary>The <c>--log</c> line for a tool call as the model sent it: <c>Tool call ask_user: {"questions": …}</c>, the arguments cut to <see cref="ToolCallLogChars"/> with an ellipsis. Pinned.</summary>
    internal static string ToolCallLogLine(string name, string argumentsJson) =>
        "Tool call " + name + ": " + (argumentsJson.Length <= ToolCallLogChars ? argumentsJson : argumentsJson[..(ToolCallLogChars - 1)] + "…");

    /// <summary>
    /// The <c>--log</c> line for one request's report: <c>Usage: 22 in, 139 out (135 reasoning), 161 total; first token after 0.80s, streamed 2.10s.</c>
    /// — the parenthesis only when the thinking was counted, <c>(~135 reasoning)</c> when the app estimated it (2026-09-29). Pinned.
    /// </summary>
    internal static string UsageLogLine(TokenUsage usage)
    {
        string reasoning = usage.Reasoning is { } count ? string.Create(CultureInfo.InvariantCulture, $" ({(usage.ReasoningEstimated ? "~" : "")}{count} reasoning)") : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"Usage: {usage.Input} in, {usage.Output} out{reasoning}, {usage.Total} total; first token after {usage.ToFirstToken.TotalSeconds:F2}s, streamed {usage.Generating.TotalSeconds:F2}s.");
    }

    /// <summary>
    /// Unwraps an exception into something that identifies the failure on its own. Necessary
    /// because stack traces are stripped from the published build: a wrapped SDK failure's outer
    /// message names neither the type nor the cause. An <see cref="AggregateException"/> is
    /// transparent (its message repeats every child's), a link whose message merely repeats the
    /// previous one is dropped, and the first four distinct causes are kept.
    /// </summary>
    internal static string Explain(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var parts = new List<string>();
        string? previousMessage = null;

        for (var current = ex; current is not null;)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                current = aggregate.InnerExceptions[0];
                continue;
            }

            if (current is EmptySummaryException)
            {
                // Our own sentence, whole: the type's name adds nothing to it (2026-09-28).
                parts.Add(current.Message);
                previousMessage = current.Message;
            }
            else if (current is ClientResultException failed && ServerDetail(failed) is { } detail)
            {
                // The SDK's own message is "Service request failed." over two lines; the server's
                // reason (a rejected reasoning level, an unknown model) is in the body it dropped.
                parts.Add($"{current.GetType().Name}: {detail}");
                previousMessage = current.Message;
            }
            else if (!string.Equals(current.Message, previousMessage, StringComparison.Ordinal))
            {
                parts.Add(current is TypeInitializationException typeInit
                    ? $"{current.GetType().Name} for {typeInit.TypeName}: {current.Message}"
                    : $"{current.GetType().Name}: {current.Message}");
                previousMessage = current.Message;
            }

            if (parts.Count >= 4)
            {
                break;
            }

            current = current.InnerException;
        }

        return string.Join(" -> ", parts);
    }

    /// <summary>Longest server message kept in a notice; the rest is an ellipsis.</summary>
    internal const int MaxServerDetail = 300;

    /// <summary>
    /// A failed request as the server explained it: <c>HTTP 400 (Bad Request): Unexpected reasoning
    /// effort high…</c>. Null when the exception carries no response (a transport failure).
    /// </summary>
    internal static string? ServerDetail(ClientResultException ex)
    {
        var response = ex.GetRawResponse();
        if (response is null)
        {
            return null;
        }

        string status = string.IsNullOrEmpty(response.ReasonPhrase)
            ? $"HTTP {response.Status.ToString(CultureInfo.InvariantCulture)}"
            : $"HTTP {response.Status.ToString(CultureInfo.InvariantCulture)} ({response.ReasonPhrase})";
        string message = ServerMessage(response.Content?.ToString() ?? "");
        return message.Length == 0 ? status : status + ": " + message;
    }

    /// <summary>
    /// The human part of an error body: the <c>message</c> of the OpenAI error shape (top-level
    /// or under <c>error</c>), otherwise the body itself; one line, at most <see cref="MaxServerDetail"/> characters.
    /// </summary>
    internal static string ServerMessage(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string text = body.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    root = error;
                }

                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    text = message.GetString() ?? "";
                }
            }
            catch (JsonException)
            {
                // Not JSON after all; the body is the message.
            }
        }

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > MaxServerDetail ? text[..MaxServerDetail] + "…" : text;
    }

    /// <summary>
    /// Tool arguments as JSON for the transcript, written by hand with <see cref="Utf8JsonWriter"/>.
    ///
    /// <para>Never <c>JsonSerializer.Serialize</c> over an <c>IDictionary&lt;string, object?&gt;</c>
    /// behind an <c>IL2026/IL3050</c> suppression: with <c>PublishAot</c> set, reflection
    /// serialisation is off and that call throws at runtime. Values arrive as <see cref="JsonElement"/> from the server or
    /// plain CLR primitives from a direct caller; anything else falls back to its string form.
    /// This is a diagnostic record, not a round-trip format.</para>
    /// </summary>
    internal static string SerializeArguments(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        if (arguments is null)
        {
            return "{}";
        }

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                switch (value)
                {
                    case null: writer.WriteNullValue(); break;
                    case JsonElement element: element.WriteTo(writer); break;
                    case string text: writer.WriteStringValue(text); break;
                    case bool flag: writer.WriteBooleanValue(flag); break;
                    case int i: writer.WriteNumberValue(i); break;
                    case long l: writer.WriteNumberValue(l); break;
                    case double d: writer.WriteNumberValue(d); break;
                    case decimal m: writer.WriteNumberValue(m); break;
                    case IFormattable f: writer.WriteStringValue(f.ToString(null, CultureInfo.InvariantCulture)); break;
                    default: writer.WriteStringValue(value.ToString()); break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
