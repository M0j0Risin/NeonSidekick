using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;

namespace NeonSidekick.Help;

/// <summary>
/// One tool <c>neon_help</c> knows (2026-10-25, the neon_help plan's Step 1): its name (the tool's own <c>ToolName</c> const), the group
/// <c>/tools</c> lists it under and that group's switch, what it does in one sentence, whether a headless run offers it, whether it needs
/// the bottom pane (it asks the user there), the settings beyond the switch it waits on, and a setup note (what must exist for it to be
/// offered, never an off reason). What a system lacks by OS is left out of the catalog; what it lacks at run time is the live line's.
/// </summary>
internal sealed record ToolHelp(string Name, string Group, SettingsField? Switch, string Does, bool Headless, bool NeedsPane, IReadOnlyList<SettingsField> Needs, string? Setup);

/// <summary>
/// Every tool the model can be offered, for <c>neon_help</c> (2026-10-25, the user's ask of 2026-10-14: a tools catalog): one
/// <see cref="ToolHelp"/> per tool class's <c>ToolName</c>, each group's label and switch as <see cref="SystemPromptSummary.ToolGroups"/> gives
/// them (pinned by a test), and the three pass-only tools under groups of their own. Off Windows the services, tasks and monitors entries are
/// left out (<see cref="ToolsText.MenuHere(string, bool)"/>), as <c>/tools</c>, the toolbar and the help leave their menus out. Hand-written
/// sentences only; the plan-mode, default and place facts are worked out (<see cref="PlanTools.Allowed"/>, a fresh <see cref="AppSettingsData"/>,
/// <see cref="HelpLocation.Of(SettingsField, bool)"/>). A new tool needs its entry here (<c>NeonHelpTests.EveryTool_HasExactlyOneHelpEntry</c>).
/// </summary>
internal static class HelpToolCatalog
{
    // The groups' labels as SystemPromptSummary.ToolGroups gives them, and the pass-only tools' own.
    public const string FilesGroup = "Files";
    public const string ShellGroup = "Shell";
    public const string WebGroup = "Web";
    public const string MemoryGroup = "Memory";
    public const string SkillsGroup = "Skills";
    public const string SessionsGroup = "Sessions";
    public const string QuestionsGroup = "Questions";
    public const string PlanGroup = "Plan";
    public const string AuditGroup = "Code audit";
    public const string RalphGroup = "Ralph loop";

    /// <summary>The groups only a pass offers, listed last.</summary>
    public static readonly IReadOnlyList<string> PassGroups = [PlanGroup, AuditGroup, RalphGroup];

    /// <summary>The bare tool names that match only as the whole query, in backticks, or followed by "tool" (common English words).</summary>
    public static readonly IReadOnlySet<string> BareWords = new HashSet<string>(StringComparer.Ordinal)
    {
        CopyTool.ToolName, DeleteTool.ToolName, MoveTool.ToolName, OpenTool.ToolName, ProcessTool.ToolName, ZipTool.ToolName, UnzipTool.ToolName,
    };

    private static readonly IReadOnlyList<SettingsField> None = [];

    private const string FileSetup = "works under the working directory (*Working directory (cwd)*); every path stays inside it";
    private const string BrowserSetup = "needs a browser: Edge, Chrome or Brave, or the one *Web browser path* names";
    private const string ConnectionSetup = "needs a connection in `{0}`, ticked on `/tools {1}`";
    private const string ExecuteSetup = "only on a connection whose entry says `\"access\": \"readwrite\"`; each change asks on the pane";

    /// <summary>Every entry, both systems' (the services, tasks and monitors among them).</summary>
    public static readonly IReadOnlyList<ToolHelp> All =
    [
        // Clock
        T(GetCurrentTimeTool.ToolName, ToolsText.ClockMenuTitle, SettingsField.ClockTools, "Tells the current date, time, weekday and time zone."),
        T(ShiftDateTool.ToolName, ToolsText.ClockMenuTitle, SettingsField.ClockTools, "Adds or subtracts days, weeks, months or years to a date and gives the date with its weekday."),
        T(DateDifferenceTool.ToolName, ToolsText.ClockMenuTitle, SettingsField.ClockTools, "Counts the days between two dates, and the years, months and days once the gap is a month or more."),
        // Timers
        T(StartTimerTool.ToolName, ToolsText.TimersMenuTitle, SettingsField.TimerTools, "Starts a named countdown timer that alerts the user when it ends.", headless: false),
        T(StopTimerTool.ToolName, ToolsText.TimersMenuTitle, SettingsField.TimerTools, "Stops a running timer or stopwatch by name, or silences one that has gone off.", headless: false),
        T(ListTimersTool.ToolName, ToolsText.TimersMenuTitle, SettingsField.TimerTools, "Lists every running timer and stopwatch with the time left or the time run.", headless: false),
        // Help
        T(NeonHelpTool.ToolName, HelpText.GroupTitle, SettingsField.HelpTools, "Looks things up in NeonSidekick's own manual: commands, settings, panes, keys, tools, bars, flags, variables and files.",
            setup: "with *Help tools* off, `/help <words>` still answers from the manual itself"),
        // Files
        T(GetWorkingDirectoryTool.ToolName, FilesGroup, SettingsField.FileTools, "Names the working directory, where the user's files live and where the file tools read and write.", setup: FileSetup),
        T(SearchFilesTool.ToolName, FilesGroup, SettingsField.FileTools, "Searches the text files under the working directory for a word or phrase, each hit as file, line and text.", setup: FileSetup),
        T(FileInfoTool.ToolName, FilesGroup, SettingsField.FileTools, "Tells a file's size, modified time, lines, words, line endings and BOM, or a folder's counts and size.", setup: FileSetup),
        T(ReadFileTool.ToolName, FilesGroup, SettingsField.FileTools, "Reads a text file under the working directory, whole or a window of lines.", setup: FileSetup),
        T(ReadPdfTool.ToolName, FilesGroup, SettingsField.FileTools, "Reads a PDF under the working directory page by page, its outline, its pictures or its form fields.", setup: BrowserSetup),
        T(WriteFileTool.ToolName, FilesGroup, SettingsField.FileTools, "Writes a text file under the working directory, making any missing folders.", setup: FileSetup),
        T(PatchFileTool.ToolName, FilesGroup, SettingsField.FileTools, "Changes part of a text file under the working directory by replacing an exact piece of its text.", setup: FileSetup),
        T(CreateDirectoryTool.ToolName, FilesGroup, SettingsField.FileTools, "Makes a folder, and any missing parents, under the working directory.", setup: FileSetup),
        T(MoveTool.ToolName, FilesGroup, SettingsField.FileTools, "Renames or moves a file or folder under the working directory.", setup: FileSetup),
        T(CopyTool.ToolName, FilesGroup, SettingsField.FileTools, "Copies a file or folder, with everything in it, to a new path under the working directory.", setup: FileSetup),
        T(DeleteTool.ToolName, FilesGroup, SettingsField.FileTools, "Deletes a file or folder under the working directory, to the bin or for good, as the user's delete setting says.", setup: FileSetup),
        T(ZipTool.ToolName, FilesGroup, SettingsField.FileTools, "Packs a file or folder under the working directory into a .zip archive.", setup: FileSetup),
        T(UnzipTool.ToolName, FilesGroup, SettingsField.FileTools, "Extracts a .zip archive under the working directory into a folder.", setup: FileSetup),
        T(OpenTool.ToolName, FilesGroup, SettingsField.FileTools, "Opens a file in the user's own editor or viewer, or a folder in the file manager, on their screen.", setup: FileSetup),
        T(ConvertToPdfTool.ToolName, FilesGroup, SettingsField.FileTools, "Makes a PDF in the working directory from a file, a web page or Markdown text.", setup: BrowserSetup + ", or a printer engine as *PDF engine* says"),
        T(PdfEditTool.ToolName, FilesGroup, SettingsField.FileTools, "Edits PDFs in the working directory: merges, extracts, deletes or rotates pages, or fills a form, writing a new PDF.", setup: BrowserSetup),
        // GitLib
        T(GitStatusTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Shows the git repository's branch, how far it is from its upstream, and every changed path."),
        T(GitLogTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Lists the git history, newest first, one commit a line."),
        T(GitShowTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Shows one commit: who made it, when, its message and the files it changed."),
        T(GitDiffTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Shows changes as a unified diff: unstaged, staged, one commit, or between two commits."),
        T(GitBlameTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Shows who last changed each line of a file and in which commit."),
        T(GitBranchTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Lists, creates, switches to or renames git branches."),
        T(GitStageTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Stages or unstages changes for the next commit."),
        T(GitCommitTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Commits what is staged with the message given, as the user's git identity."),
        T(GitStashTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Puts the working tree's changes aside as a stash and brings them back."),
        T(GitDiscardTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Throws uncommitted changes away for good, for the paths named or the whole tree."),
        T(GitDeleteTool.ToolName, ToolsText.GitTabTitle, SettingsField.GitLibTools, "Removes a local branch, a tag or a stash."),
        // Image
        T(ViewImageTool.ToolName, ToolsText.ImageMenuTitle, SettingsField.ImageTools, "Shows the model picture files under the working directory or in the user's images folder."),
        T(ImageInfoTool.ToolName, ToolsText.ImageMenuTitle, SettingsField.ImageTools, "Reads facts about pictures without loading them: format, size, frames, orientation and the metadata they carry."),
        T(ImageEditTool.ToolName, ToolsText.ImageMenuTitle, SettingsField.ImageTools, "Edits a picture and writes a new file: resize, crop, rotate, flip, color, blur, sharpen, a border, another format."),
        T(SetSplashImageTool.ToolName, ToolsText.ImageMenuTitle, SettingsField.ImageTools, "Adds a picture to the profile's splash pictures, shown at start and by `/splash`."),
        // Shell
        T(RunCommandTool.ToolName, ShellGroup, SettingsField.ShellCommandPolicy, "Runs a command line in a shell on the user's computer and gives back its exit code and output.",
            setup: "each line passes *Shell command policy* (ask, allowed list or yolo) and the shell police (*Shell police*)"),
        T(ProcessTool.ToolName, ShellGroup, SettingsField.ShellCommandPolicy, "Lists, polls, reads, feeds or ends the background processes `run_command` started."),
        T(ExecuteCodeTool.ToolName, ShellGroup, SettingsField.ShellCommandPolicy, "Runs a script in a fresh process in the working directory and gives back what it printed.",
            setup: "needs an interpreter for one of the languages *Shell code languages* names"),
        // Obsidian
        T(VaultSearchTool.ToolName, ToolsText.ObsidianTabTitle, SettingsField.ObsidianTools, "Searches the user's Obsidian vault for a text, by line and by note name.", setup: "needs a vault in *Obsidian vault*"),
        T(VaultListTool.ToolName, ToolsText.ObsidianTabTitle, SettingsField.ObsidianTools, "Lists the vault's notes by folder, tag or property.", setup: "needs a vault in *Obsidian vault*"),
        T(VaultReadTool.ToolName, ToolsText.ObsidianTabTitle, SettingsField.ObsidianTools, "Reads a note from the vault, found the way Obsidian resolves a link.", setup: "needs a vault in *Obsidian vault*"),
        T(VaultLinksTool.ToolName, ToolsText.ObsidianTabTitle, SettingsField.ObsidianTools, "Shows a note's links and embeds, and the notes that link back to it.", setup: "needs a vault in *Obsidian vault*"),
        T(VaultDailyTool.ToolName, ToolsText.ObsidianTabTitle, SettingsField.ObsidianTools, "Opens a day's daily note, made from the vault's template when missing, and can append to it.", setup: "needs a vault in *Obsidian vault*"),
        T(VaultWriteTool.ToolName, ToolsText.ObsidianTabTitle, SettingsField.ObsidianTools, "Writes a note in the vault: creates, replaces, appends or prepends.", setup: "needs a vault in *Obsidian vault*"),
        T(VaultPropertiesTool.ToolName, ToolsText.ObsidianTabTitle, SettingsField.ObsidianTools, "Reads or changes a note's properties (its frontmatter).", setup: "needs a vault in *Obsidian vault*"),
        T(VaultMoveTool.ToolName, ToolsText.ObsidianTabTitle, SettingsField.ObsidianTools, "Renames or moves a note and rewrites every link that pointed at it.", setup: "needs a vault in *Obsidian vault*"),
        T(VaultDeleteTool.ToolName, ToolsText.ObsidianTabTitle, SettingsField.ObsidianTools, "Deletes a note or attachment where the vault sends deleted files, so it can be restored.", needs: [SettingsField.ObsidianAllowDelete], setup: "needs a vault in *Obsidian vault*"),
        // SQL
        T(SqlConnectionsTool.ToolName, ToolsText.SqlTabTitle, SettingsField.SqlTools, "Lists the SQL Server connections the user has set up.", setup: Conn("sql.json", "sql")),
        T(SqlTablesTool.ToolName, ToolsText.SqlTabTitle, SettingsField.SqlTools, "Lists a SQL Server database's tables and views with their row counts.", setup: Conn("sql.json", "sql")),
        T(SqlColumnsTool.ToolName, ToolsText.SqlTabTitle, SettingsField.SqlTools, "Finds the columns of a SQL Server database whose name matches.", setup: Conn("sql.json", "sql")),
        T(SqlDescribeTool.ToolName, ToolsText.SqlTabTitle, SettingsField.SqlTools, "Describes one SQL Server table or view: columns, keys, joins and indexes.", setup: Conn("sql.json", "sql")),
        T(SqlRelationshipsTool.ToolName, ToolsText.SqlTabTitle, SettingsField.SqlTools, "Lists a SQL Server database's foreign keys as join paths.", setup: Conn("sql.json", "sql")),
        T(SqlIndexesTool.ToolName, ToolsText.SqlTabTitle, SettingsField.SqlTools, "Lists a SQL Server table's or database's indexes and how much each is used.", setup: Conn("sql.json", "sql")),
        T(SqlQueryTool.ToolName, ToolsText.SqlTabTitle, SettingsField.SqlTools, "Runs one read-only T-SQL SELECT and gives back the rows.", setup: Conn("sql.json", "sql")),
        T(SqlExecuteTool.ToolName, ToolsText.SqlTabTitle, SettingsField.SqlTools, "Runs one statement that changes a SQL Server database.", headless: false, pane: true, needs: [SettingsField.SqlMode], setup: ExecuteSetup),
        // Oracle
        T(OracleConnectionsTool.ToolName, ToolsText.OracleTabTitle, SettingsField.OracleTools, "Lists the Oracle connections the user has set up.", setup: Conn("oracle.json", "oracle")),
        T(OracleTablesTool.ToolName, ToolsText.OracleTabTitle, SettingsField.OracleTools, "Lists an Oracle schema's tables and views with their row counts and comments.", setup: Conn("oracle.json", "oracle")),
        T(OracleColumnsTool.ToolName, ToolsText.OracleTabTitle, SettingsField.OracleTools, "Finds the columns of an Oracle schema whose name matches.", setup: Conn("oracle.json", "oracle")),
        T(OracleDescribeTool.ToolName, ToolsText.OracleTabTitle, SettingsField.OracleTools, "Describes one Oracle table or view: columns, keys, joins, indexes, checks and triggers.", setup: Conn("oracle.json", "oracle")),
        T(OracleRelationshipsTool.ToolName, ToolsText.OracleTabTitle, SettingsField.OracleTools, "Lists an Oracle schema's foreign keys as join paths.", setup: Conn("oracle.json", "oracle")),
        T(OracleIndexesTool.ToolName, ToolsText.OracleTabTitle, SettingsField.OracleTools, "Lists an Oracle table's or schema's indexes and their recorded use.", setup: Conn("oracle.json", "oracle")),
        T(OracleQueryTool.ToolName, ToolsText.OracleTabTitle, SettingsField.OracleTools, "Runs one read-only Oracle SELECT and gives back the rows.", setup: Conn("oracle.json", "oracle")),
        T(OracleExecuteTool.ToolName, ToolsText.OracleTabTitle, SettingsField.OracleTools, "Runs one statement that changes an Oracle database.", headless: false, pane: true, needs: [SettingsField.OracleMode], setup: ExecuteSetup),
        // MySQL
        T(MySqlConnectionsTool.ToolName, ToolsText.MySqlTabTitle, SettingsField.MySqlTools, "Lists the MySQL and MariaDB connections the user has set up.", setup: Conn("mysql.json", "mysql")),
        T(MySqlTablesTool.ToolName, ToolsText.MySqlTabTitle, SettingsField.MySqlTools, "Lists a MySQL database's tables and views with their row counts and comments.", setup: Conn("mysql.json", "mysql")),
        T(MySqlColumnsTool.ToolName, ToolsText.MySqlTabTitle, SettingsField.MySqlTools, "Finds the columns of a MySQL database whose name matches.", setup: Conn("mysql.json", "mysql")),
        T(MySqlDescribeTool.ToolName, ToolsText.MySqlTabTitle, SettingsField.MySqlTools, "Describes one MySQL table or view: columns, keys, joins, indexes, checks and triggers.", setup: Conn("mysql.json", "mysql")),
        T(MySqlRelationshipsTool.ToolName, ToolsText.MySqlTabTitle, SettingsField.MySqlTools, "Lists a MySQL database's foreign keys as join paths.", setup: Conn("mysql.json", "mysql")),
        T(MySqlIndexesTool.ToolName, ToolsText.MySqlTabTitle, SettingsField.MySqlTools, "Lists a MySQL table's or database's indexes and how much each is used.", setup: Conn("mysql.json", "mysql")),
        T(MySqlQueryTool.ToolName, ToolsText.MySqlTabTitle, SettingsField.MySqlTools, "Runs one read-only SELECT on a MySQL or MariaDB connection and gives back the rows.", setup: Conn("mysql.json", "mysql")),
        T(MySqlExecuteTool.ToolName, ToolsText.MySqlTabTitle, SettingsField.MySqlTools, "Runs one statement that changes a MySQL database.", headless: false, pane: true, needs: [SettingsField.MySqlMode], setup: ExecuteSetup),
        // SQLite
        T(SqliteDatabasesTool.ToolName, ToolsText.SqliteTabTitle, SettingsField.SqliteTools, "Lists the SQLite databases the user has named, and whether a database file in the working directory may be named by its path.", setup: SqliteSetup),
        T(SqliteTablesTool.ToolName, ToolsText.SqliteTabTitle, SettingsField.SqliteTools, "Lists a SQLite database's tables and views.", setup: SqliteSetup),
        T(SqliteDescribeTool.ToolName, ToolsText.SqliteTabTitle, SettingsField.SqliteTools, "Describes a SQLite table or view: columns, foreign keys, indexes and its CREATE statement.", setup: SqliteSetup),
        T(SqliteQueryTool.ToolName, ToolsText.SqliteTabTitle, SettingsField.SqliteTools, "Runs one read-only SELECT on a SQLite database and gives back the rows.", setup: SqliteSetup),
        T(SqliteExecuteTool.ToolName, ToolsText.SqliteTabTitle, SettingsField.SqliteTools, "Runs one statement that changes a SQLite database.", headless: false, pane: true, needs: [SettingsField.SqliteMode],
            setup: "only the statement kinds *SQLite statements allowed* ticks; each new database file asks on the pane"),
        // PostgreSQL
        T(PostgresConnectionsTool.ToolName, ToolsText.PostgresTabTitle, SettingsField.PostgresTools, "Lists the PostgreSQL connections the user has set up.", setup: Conn("postgres.json", "postgres")),
        T(PostgresSchemasTool.ToolName, ToolsText.PostgresTabTitle, SettingsField.PostgresTools, "Lists a PostgreSQL database's schemas with their table counts and owners.", setup: Conn("postgres.json", "postgres")),
        T(PostgresTablesTool.ToolName, ToolsText.PostgresTabTitle, SettingsField.PostgresTools, "Lists a PostgreSQL database's tables and views with their row counts and comments.", setup: Conn("postgres.json", "postgres")),
        T(PostgresColumnsTool.ToolName, ToolsText.PostgresTabTitle, SettingsField.PostgresTools, "Finds the columns of a PostgreSQL database whose name matches.", setup: Conn("postgres.json", "postgres")),
        T(PostgresDescribeTool.ToolName, ToolsText.PostgresTabTitle, SettingsField.PostgresTools, "Describes a PostgreSQL table or view: columns, foreign keys, indexes and checks.", setup: Conn("postgres.json", "postgres")),
        T(PostgresRelationshipsTool.ToolName, ToolsText.PostgresTabTitle, SettingsField.PostgresTools, "Lists a PostgreSQL schema's foreign keys as join paths.", setup: Conn("postgres.json", "postgres")),
        T(PostgresIndexesTool.ToolName, ToolsText.PostgresTabTitle, SettingsField.PostgresTools, "Lists a PostgreSQL schema's or table's indexes and how often each was scanned.", setup: Conn("postgres.json", "postgres")),
        T(PostgresQueryTool.ToolName, ToolsText.PostgresTabTitle, SettingsField.PostgresTools, "Runs one read-only SELECT on a PostgreSQL connection and gives back the rows.", setup: Conn("postgres.json", "postgres")),
        T(PostgresExecuteTool.ToolName, ToolsText.PostgresTabTitle, SettingsField.PostgresTools, "Runs one statement that changes a PostgreSQL database.", headless: false, pane: true, needs: [SettingsField.PostgresMode], setup: ExecuteSetup),
        // UNC
        T(UncSharesTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Lists the network shares and outside folders the user has set up for the UNC tools.", setup: UncSetup),
        T(UncSearchTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Searches the text files on one of the user's shares for a word or phrase.", setup: UncSetup),
        T(UncInfoTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Tells about a file or folder on one of the user's shares: size, times, lines, counts.", setup: UncSetup),
        T(UncReadTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Reads a text file on one of the user's shares, whole or a range of lines.", setup: UncSetup),
        T(UncFetchTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Copies a file or folder from a share into the working directory.", needs: [SettingsField.FileTools], setup: UncSetup),
        T(UncWriteTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Writes a text file on one of the user's read-write shares.", needs: [SettingsField.UncWrites], setup: UncWriteSetup),
        T(UncPatchTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Changes part of a text file on a read-write share by replacing an exact piece of its text.", needs: [SettingsField.UncWrites], setup: UncWriteSetup),
        T(UncCreateDirectoryTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Makes a folder, and any missing parents, on a read-write share.", needs: [SettingsField.UncWrites], setup: UncWriteSetup),
        T(UncMoveTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Moves or renames a file or folder within a read-write share.", needs: [SettingsField.UncWrites], setup: UncWriteSetup),
        T(UncCopyTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Copies a file or folder within a read-write share.", needs: [SettingsField.UncWrites], setup: UncWriteSetup),
        T(UncDeleteTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Deletes a file or folder, with everything in it, on a read-write share.", needs: [SettingsField.UncWrites], setup: UncWriteSetup),
        T(UncPutTool.ToolName, ToolsText.UncTabTitle, SettingsField.UncTools, "Copies a file or folder from the working directory onto a read-write share.", needs: [SettingsField.UncWrites, SettingsField.FileTools], setup: UncWriteSetup),
        // Docker
        T(DockerContainersTool.ToolName, ToolsText.DockerTabTitle, SettingsField.DockerTools, "Lists the Docker containers on this machine with their state, health, image and ports."),
        T(DockerLogsTool.ToolName, ToolsText.DockerTabTitle, SettingsField.DockerTools, "Reads a Docker container's recent log lines."),
        T(DockerInspectTool.ToolName, ToolsText.DockerTabTitle, SettingsField.DockerTools, "Shows one Docker container in detail, with every environment value hidden."),
        T(DockerStatsTool.ToolName, ToolsText.DockerTabTitle, SettingsField.DockerTools, "Shows the CPU, memory, network and disk use of one running container, or of all of them."),
        T(DockerResourcesTool.ToolName, ToolsText.DockerTabTitle, SettingsField.DockerTools, "Lists Docker images, volumes or networks, or the disk space Docker takes."),
        T(DockerComposeTool.ToolName, ToolsText.DockerTabTitle, SettingsField.DockerTools, "Lists the Docker Compose projects on this machine with their folders and services."),
        T(DockerLifecycleTool.ToolName, ToolsText.DockerTabTitle, SettingsField.DockerTools, "Starts, stops, restarts, pauses or unpauses a container or a compose project.", needs: [SettingsField.DockerWrites], setup: DockerWriteSetup),
        T(DockerPullTool.ToolName, ToolsText.DockerTabTitle, SettingsField.DockerTools, "Pulls a public Docker image from its registry.", needs: [SettingsField.DockerWrites], setup: DockerWriteSetup),
        T(DockerRemoveTool.ToolName, ToolsText.DockerTabTitle, SettingsField.DockerTools, "Removes one Docker container, image or volume for good.", needs: [SettingsField.DockerWrites], setup: DockerWriteSetup),
        T(DockerPruneTool.ToolName, ToolsText.DockerTabTitle, SettingsField.DockerTools, "Removes every unused Docker thing of one kind, such as stopped containers or the build cache.", needs: [SettingsField.DockerWrites], setup: DockerWriteSetup),
        // EventLog
        T(EventLogLogsTool.ToolName, ToolsText.EventLogTabTitle, SettingsField.EventLogTools, "Lists the computers whose event logs can be read, a computer's logs, and the log files in the working directory.", setup: EventLogSetup),
        T(EventLogSearchTool.ToolName, ToolsText.EventLogTabTitle, SettingsField.EventLogTools, "Searches an event log, newest first, by level, event id, provider, time, a field or text.", setup: EventLogSetup),
        T(EventLogSummaryTool.ToolName, ToolsText.EventLogTabTitle, SettingsField.EventLogTools, "Counts an event log's events since a time by provider and event id, with a sample message of each.", setup: EventLogSetup),
        T(EventLogEventTool.ToolName, ToolsText.EventLogTabTitle, SettingsField.EventLogTools, "Reads one event in full by its record number.", setup: EventLogSetup),
        // Network
        T(NetDnsTool.ToolName, ToolsText.NetworkMenuTitle, SettingsField.NetworkTools, "Looks up a DNS name: its addresses, mail servers, text records, name servers and other records.", setup: NetworkSetup),
        T(NetPingTool.ToolName, ToolsText.NetworkMenuTitle, SettingsField.NetworkTools, "Pings one host: whether it answers, the loss and the round-trip times.", setup: NetworkSetup + "; needs a way to send a ping here"),
        T(NetTraceTool.ToolName, ToolsText.NetworkMenuTitle, SettingsField.NetworkTools, "Traces the route to one host: each router on the way and its round-trip time.", setup: NetworkSetup + "; needs a way to trace a route here"),
        T(NetPortTool.ToolName, ToolsText.NetworkMenuTitle, SettingsField.NetworkTools, "Checks whether TCP ports on one host are open, refused or silent.", setup: NetworkSetup),
        T(NetTlsTool.ToolName, ToolsText.NetworkMenuTitle, SettingsField.NetworkTools, "Connects to a TLS service and reports its protocol, cipher and certificate.", setup: NetworkSetup),
        T(NetCertsTool.ToolName, ToolsText.NetworkMenuTitle, SettingsField.NetworkTools, "Lists the certificates in one of this machine's certificate stores; nothing is exported.", needs: [SettingsField.NetworkCertificateStores]),
        // Services (Windows only)
        T(ServiceListTool.ToolName, ToolsText.ServicesMenuTitle, SettingsField.ServiceTools, "Lists the Windows services on a computer with their states.", setup: ServiceSetup),
        T(ServiceInfoTool.ToolName, ToolsText.ServicesMenuTitle, SettingsField.ServiceTools, "Reads one Windows service in full: state, start type, account, program, recovery and dependencies.", setup: ServiceSetup),
        T(ServiceControlTool.ToolName, ToolsText.ServicesMenuTitle, SettingsField.ServiceTools, "Starts, stops or restarts a Windows service, as the computer's agreed actions allow.", headless: false, pane: true, needs: [SettingsField.ServiceWrites], setup: ServiceWriteSetup),
        T(ServiceStartTypeTool.ToolName, ToolsText.ServicesMenuTitle, SettingsField.ServiceTools, "Changes how a Windows service starts: automatic, delayed, manual or disabled.", headless: false, pane: true, needs: [SettingsField.ServiceWrites], setup: ServiceWriteSetup),
        // Tasks (Windows only)
        T(TaskListTool.ToolName, ToolsText.TasksMenuTitle, SettingsField.TaskTools, "Lists the scheduled tasks on a computer with their states and runs.", setup: TaskSetup),
        T(TaskInfoTool.ToolName, ToolsText.TasksMenuTitle, SettingsField.TaskTools, "Reads one scheduled task in full: its triggers in words, what it runs, who it runs as, its last result.", setup: TaskSetup),
        T(TaskControlTool.ToolName, ToolsText.TasksMenuTitle, SettingsField.TaskTools, "Runs, ends, enables or disables a scheduled task, as the computer's agreed actions allow.", headless: false, pane: true, needs: [SettingsField.TaskWrites], setup: TaskWriteSetup),
        // Directory
        T(AdUserTool.ToolName, ToolsText.DirectoryMenuTitle, SettingsField.DirectoryTools, "Reads an Active Directory user: enabled, locked, password expiry, last sign-in, groups, manager and OU.", setup: DirectorySetup),
        T(AdGroupTool.ToolName, ToolsText.DirectoryMenuTitle, SettingsField.DirectoryTools, "Lists an Active Directory group's members, direct or nested, and the groups it is in.", setup: DirectorySetup + "; a list's `to` writes a CSV only with *File tools* on and not in plan mode"),
        T(AdComputerTool.ToolName, ToolsText.DirectoryMenuTitle, SettingsField.DirectoryTools, "Reads an Active Directory computer or service account: its system, state, last sign-in, OU and groups.", setup: DirectorySetup),
        T(AdFindTool.ToolName, ToolsText.DirectoryMenuTitle, SettingsField.DirectoryTools, "Finds Active Directory objects by name, by a field, or by a state such as locked, disabled or stale.", setup: DirectorySetup + "; a list's `to` writes a CSV only with *File tools* on and not in plan mode"),
        T(AdOuTool.ToolName, ToolsText.DirectoryMenuTitle, SettingsField.DirectoryTools, "Lists Active Directory's OUs as a tree, or one OU's owner, linked GPOs and the objects in it.", setup: DirectorySetup),
        T(AdDomainTool.ToolName, ToolsText.DirectoryMenuTitle, SettingsField.DirectoryTools, "Reads an Active Directory domain: its controllers, levels, password and lockout policy, and its role holders.", setup: DirectorySetup),
        // ComfyUI
        T(GenerateImageTool.ToolName, ToolsText.ComfyTabTitle, SettingsField.ComfyTools, "Makes pictures on the user's ComfyUI server from one of their workflows.", setup: "needs *ComfyUI URL* and a workflow installed (`/comfy`)"),
        // Home Assistant
        T(HaOverviewTool.ToolName, ToolsText.HomeAssistantGroupTitle, SettingsField.HomeAssistantTools, "Gives Home Assistant at a glance: lights on by room, media players, temperatures, motion, batteries and lists.", setup: HaSetup),
        T(HaStatesTool.ToolName, ToolsText.HomeAssistantGroupTitle, SettingsField.HomeAssistantTools, "Lists Home Assistant entities with their ids and states, narrowed by name, domain or room.", setup: HaSetup),
        T(HaHistoryTool.ToolName, ToolsText.HomeAssistantGroupTitle, SettingsField.HomeAssistantTools, "Shows a Home Assistant entity's state changes over the last hours.", setup: HaSetup),
        T(HaLightsTool.ToolName, ToolsText.HomeAssistantGroupTitle, SettingsField.HomeAssistantTools, "Turns Home Assistant lights on or off by room, name or id, with brightness or a color.", setup: HaSetup),
        T(HaSceneTool.ToolName, ToolsText.HomeAssistantGroupTitle, SettingsField.HomeAssistantTools, "Activates a Home Assistant scene by name.", setup: HaSetup),
        T(HaMediaTool.ToolName, ToolsText.HomeAssistantGroupTitle, SettingsField.HomeAssistantTools, "Controls a Home Assistant media player: power, volume, mute, source, play, pause, skip.", setup: HaSetup),
        T(HaTodoTool.ToolName, ToolsText.HomeAssistantGroupTitle, SettingsField.HomeAssistantTools, "Reads and edits a Home Assistant to-do list.", setup: HaSetup),
        T(HaCallServiceTool.ToolName, ToolsText.HomeAssistantGroupTitle, SettingsField.HomeAssistantTools, "Calls any Home Assistant service the other ha_ tools do not cover.", setup: HaSetup),
        T(HaAssistTool.ToolName, ToolsText.HomeAssistantGroupTitle, SettingsField.HomeAssistantTools, "Hands one plain sentence to Home Assistant's own Assist agent.", setup: HaSetup),
        // Print
        T(ListPrintersTool.ToolName, ToolsText.PrintTabTitle, SettingsField.PrintTools, "Lists the printers installed on the user's computer, marking the default."),
        T(PrintFileTool.ToolName, ToolsText.PrintTabTitle, SettingsField.PrintTools, "Prints a file from the working directory on the user's printer."),
        // Camera
        T(CameraCaptureTool.ToolName, ToolsText.CameraTabTitle, SettingsField.CameraTools, "Asks the user for a photo from their camera and shows it to the model.", headless: false, pane: true, setup: PictureSetup("a camera")),
        // Screen
        T(ScreenCaptureTool.ToolName, ToolsText.ScreenTabTitle, SettingsField.ScreenTools, "Takes a screenshot of a monitor, every monitor or one window and shows it to the model.", headless: false, pane: true, setup: PictureSetup("screen capture support")),
        T(ScreenListTool.ToolName, ToolsText.ScreenTabTitle, SettingsField.ScreenTools, "Lists the monitors and the windows on the screen, with the target that names each.", headless: false, pane: true, setup: PictureSetup("screen capture support")),
        // YouTube
        T(YouTubeSearchTool.ToolName, ToolsText.YouTubeTabTitle, SettingsField.YouTubeTools, "Searches YouTube for videos, playlists or channels.", setup: "needs a *YouTube API key*"),
        T(YouTubePlaylistTool.ToolName, ToolsText.YouTubeTabTitle, SettingsField.YouTubeTools, "Lists a public YouTube playlist's videos.", setup: "needs a *YouTube API key*"),
        T(YouTubePlayTool.ToolName, ToolsText.YouTubeTabTitle, SettingsField.YouTubeTools, "Plays a YouTube video where *YouTube open links in* says.", headless: false, setup: VideoSetup),
        T(YouTubeControlTool.ToolName, ToolsText.YouTubeTabTitle, SettingsField.YouTubeTools, "Controls the video playing: play, pause, seek, volume, mute, the playlist, or close the player.", headless: false, setup: VideoSetup),
        T(YouTubeStatusTool.ToolName, ToolsText.YouTubeTabTitle, SettingsField.YouTubeTools, "Tells what the video player is playing, where it is and its volume.", headless: false, setup: VideoSetup),
        T(YouTubeSavedTool.ToolName, ToolsText.YouTubeTabTitle, SettingsField.YouTubeTools, "Lists the user's saved YouTube videos and where each was left."),
        T(YouTubeSaveTool.ToolName, ToolsText.YouTubeTabTitle, SettingsField.YouTubeTools, "Adds a YouTube video to the user's saved videos, or removes one."),
        // Shopping
        T(ShoppingListTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Shows the user's shopping list, grouped by aisle in the order the store is walked."),
        T(ShoppingAddTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Adds items to the user's shopping list."),
        T(ShoppingCheckTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Ticks lines of the shopping list as bought, or unticks them."),
        T(ShoppingRemoveTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Removes lines from the shopping list."),
        T(ShoppingClearTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Removes the ticked lines from the shopping list, or the whole list once the user agrees."),
        T(ShoppingLocateTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Finds where a product is at a store, and why."),
        T(ShoppingPlaceTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Records where a product, brand or category is at a store."),
        T(ShoppingStoresTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Lists the stores the shopping list knows and shows one store's layout."),
        T(ShoppingLayoutTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Changes the stores and their layouts: stores, sections, aisles and the walking order."),
        T(ShoppingRecentTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Lists what the user bought before and is not on the list now, the most often first."),
        T(ShoppingReferenceTool.ToolName, ToolsText.ShoppingMenuTitle, SettingsField.ShoppingTools, "Looks in a store's reference documents, such as a store guide or a floor map."),
        // Monitors (Windows only)
        T(MonitorListTool.ToolName, ToolsText.MonitorsMenuTitle, SettingsField.MonitorTools, "Lists the user's monitors: name, connector, mode, input and the layout.", headless: false, pane: true),
        T(MonitorInputTool.ToolName, ToolsText.MonitorsMenuTitle, SettingsField.MonitorTools, "Switches a monitor's input, after asking the user.", headless: false, pane: true),
        T(MonitorLayoutTool.ToolName, ToolsText.MonitorsMenuTitle, SettingsField.MonitorTools, "Changes the monitors' layout (extend, duplicate, one monitor, primary), after asking the user.", headless: false, pane: true),
        // Advisors
        T(NeonAdvisorTool.ToolName, ToolsText.AdvisorsGroupTitle, SettingsField.Advisors, "Asks one of the user's advisors, another profile with its own model, for its view.", setup: "needs a profile in *Advisor list*"),
        // Web
        T(WebSearchTool.ToolName, WebGroup, SettingsField.WebTools, "Searches the web and gives back the top results."),
        T(WebFetchTool.ToolName, WebGroup, SettingsField.WebTools, "Fetches a web page and gives back its readable text as Markdown, a part at a time."),
        T(OpenUrlTool.ToolName, WebGroup, SettingsField.WebTools, "Opens a web page in the user's browser on their screen."),
        T(WebLinksTool.ToolName, WebGroup, SettingsField.WebTools, "Lists the user's saved web links."),
        T(WebSaveLinkTool.ToolName, WebGroup, SettingsField.WebTools, "Saves, removes or renames one of the user's saved web links."),
        T(DownloadFileTool.ToolName, WebGroup, SettingsField.WebTools, "Downloads a file from the web into the working directory, unread and unopened.", needs: [SettingsField.FileTools]),
        // Memory
        T(SaveMemoryTool.ToolName, MemoryGroup, SettingsField.MemoryMode, "Saves one lasting fact about the user, known in every later session.", needs: [SettingsField.MemoryMode], setup: "only while *Memory mode* is read-write"),
        T(RecallMemoryTool.ToolName, MemoryGroup, SettingsField.MemoryMode, "Gives back every fact saved about the user, oldest first."),
        // Skills
        T(LoadSkillTool.ToolName, SkillsGroup, SettingsField.AgentSkills, "Loads a skill's full instructions by name.", setup: "needs a skill installed (`/skills`)"),
        T(SkillEditorTool.ToolName, SkillsGroup, SettingsField.AgentSkills, "Creates or updates a skill: a named folder of instructions kept for later sessions."),
        // Sessions
        T(SessionManagerTool.ToolName, SessionsGroup, SettingsField.SessionTool, "Finds and reads the user's earlier conversations in this profile."),
        // Questions
        T(AskUserTool.ToolName, QuestionsGroup, SettingsField.AskUser, "Asks the user one to ten multiple-choice questions on screen and waits for the answers.", headless: false, pane: true),
        // The passes' own
        T(PresentPlanTool.ToolName, PlanGroup, null, "Presents a plan for the user's approval and saves it in the working folder's `.neon` folder.", setup: "only while planning, under `/plan`"),
        T(PresentAuditTool.ToolName, AuditGroup, null, "Records a code audit pass's findings in the audit's report.", setup: "only during a `/code-audit` pass"),
        T(FinishStepTool.ToolName, RalphGroup, null, "Reports a Ralph loop pass's step as done, blocked or split.", setup: "only during a Ralph loop pass, under `/plan ralph`"),
    ];

    private const string SqliteSetup = "needs a database in `sqlite.json` ticked on `/tools sqlite`, or *SQLite sandbox files* on";
    private const string UncSetup = "needs a share in `unc.json`, ticked on `/tools unc`";
    private const string UncWriteSetup = "only on a share whose entry says `\"access\": \"readwrite\"`";
    private const string DockerWriteSetup = "each change asks on the pane; a headless run refuses every one";
    private const string EventLogSetup = "reads the computers ticked in *Event log computers offered*; with none ticked, only log files in the working directory";
    private const string NetworkSetup = "each host is judged by *Network tools reach* and the host lists, within a scan budget each turn";
    private const string ServiceSetup = "reads the computers ticked in *Service computers offered*";
    private const string ServiceWriteSetup = "needs a ticked computer with `\"access\": \"readwrite\"` whose control list allows the action; each change asks on the pane";
    private const string TaskSetup = "reads the computers ticked in *Task computers offered*";
    private const string TaskWriteSetup = "needs a ticked computer with `\"access\": \"readwrite\"` whose control list allows the action; each change asks on the pane";
    private const string DirectorySetup = "needs a domain ticked in *Directory domains offered*: one in `ad.json`, or `current` where this computer is joined to one";
    private const string HaSetup = "needs *Home Assistant URL* and *Home Assistant API key*; an asked call waits on the pane";
    private const string VideoSetup = "needs a video player here";

    private static string Conn(string file, string word) => string.Format(System.Globalization.CultureInfo.InvariantCulture, ConnectionSetup, file, word);

    private static string PictureSetup(string device) => $"needs {device} and a model that reads pictures; it asks on the pane";

    private static ToolHelp T(string name, string group, SettingsField? @switch, string does, bool headless = true, bool pane = false, IReadOnlyList<SettingsField>? needs = null, string? setup = null) =>
        new(name, group, @switch, does, headless, pane, needs ?? None, setup);

    private static readonly Lazy<IReadOnlyList<ToolHelp>> s_windows = new(() => All.Where(e => ToolsText.MenuHere(e.Group, windows: true)).ToList());
    private static readonly Lazy<IReadOnlyList<ToolHelp>> s_elsewhere = new(() => All.Where(e => ToolsText.MenuHere(e.Group, windows: false)).ToList());

    /// <summary>The entries a system knows: every one on Windows, all but the Windows-only groups' elsewhere (2026-10-25, G102). Built once each.</summary>
    public static IReadOnlyList<ToolHelp> Entries(bool windows) => windows ? s_windows.Value : s_elsewhere.Value;

    /// <summary><see cref="Entries"/>, under the name the plan gives it.</summary>
    public static IReadOnlyList<ToolHelp> EntriesFor(bool windows) => Entries(windows);

    /// <summary>The entry named <paramref name="name"/> on a system, any case; null for none.</summary>
    public static ToolHelp? Find(string name, bool windows) =>
        Entries(windows).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The groups in the order <c>/tools</c> › Offered shows them (A to Z, any case), the pass-only groups last, each with its tools.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<ToolHelp> Tools)> Groups(bool windows)
    {
        var groups = Entries(windows).GroupBy(e => e.Group, StringComparer.Ordinal).Select(g => (Group: g.Key, Tools: (IReadOnlyList<ToolHelp>)g.ToList())).ToList();
        return groups.Where(g => !PassGroups.Contains(g.Group)).OrderBy(g => g.Group, StringComparer.OrdinalIgnoreCase)
            .Concat(PassGroups.SelectMany(p => groups.Where(g => g.Group == p))).ToList();
    }

    /// <summary>The group a switch offers, on a system; null for a field that switches no group of the catalog.</summary>
    public static (string Group, IReadOnlyList<ToolHelp> Tools)? GroupOfSwitch(SettingsField field, bool windows)
    {
        foreach (var group in Groups(windows))
        {
            if (group.Tools[0].Switch == field)
            {
                return group;
            }
        }

        return null;
    }

    /// <summary>
    /// The group <paramref name="words"/> names (lower case, a question's words): "camera tools", "shell tool" — the group's label or its
    /// switch's name less "tools", followed by "tool(s)"; with <paramref name="bare"/> (<c>kind: tool</c>) the label or stem alone too.
    /// </summary>
    public static (string Group, IReadOnlyList<ToolHelp> Tools)? GroupNamed(IReadOnlyList<string> words, bool bare, bool windows)
    {
        ArgumentNullException.ThrowIfNull(words);
        string joined = " " + string.Join(' ', words) + " ";
        foreach (var group in Groups(windows))
        {
            foreach (string stem in Stems(group))
            {
                if (joined.Contains(" " + stem + " tools ", StringComparison.Ordinal) || joined.Contains(" " + stem + " tool ", StringComparison.Ordinal)
                    || (bare && string.Equals(joined.Trim(), stem, StringComparison.Ordinal)))
                {
                    return group;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> Stems((string Group, IReadOnlyList<ToolHelp> Tools) group)
    {
        yield return group.Group.ToLowerInvariant();
        if (group.Tools[0].Switch is { } field)
        {
            string name = SettingsMenu.FieldName(field).ToLowerInvariant();
            foreach (string tail in (string[])[" tools", " tool"])
            {
                if (name.EndsWith(tail, StringComparison.Ordinal))
                {
                    yield return name[..^tail.Length];
                }
            }
        }
    }

    /// <summary>One tool in full (<see cref="HelpText.Tool"/>), the live line last when <paramref name="live"/> is given.</summary>
    public static string Answer(ToolHelp entry, HelpLive? live, bool windows, bool mac)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string? needs = entry.Needs.Count == 0 ? null : string.Join("; ", entry.Needs.Select(n => NeedText(n, windows)));
        string? macLine = mac && HelpMac.Tools.TryGetValue(entry.Name, out var line) ? line : null;
        return HelpText.Tool(entry.Name, entry.Does, entry.Group, SwitchText(entry, windows), PlanTools.Allowed(entry.Name), entry.Headless, entry.NeedsPane, needs, entry.Setup, macLine, live?.Line(entry));
    }

    /// <summary>A group: its switch and live line, then each tool with what it does.</summary>
    public static string GroupAnswer((string Group, IReadOnlyList<ToolHelp> Tools) group, HelpLive? live, bool windows)
    {
        var first = group.Tools[0];
        var sb = new System.Text.StringBuilder(HelpText.ToolGroupHeading(group.Group));
        if (SwitchText(first, windows) is { } switchLine)
        {
            sb.Append("\n  Switch: ").Append(switchLine);
        }
        else if (first.Setup is not null)
        {
            sb.Append("\n  Offered: ").Append(first.Setup);
        }

        if (live is not null)
        {
            sb.Append("\n  ").Append(live.GroupLine(first.Switch, group.Tools));
        }

        foreach (var tool in group.Tools)
        {
            sb.Append("\n- `").Append(tool.Name).Append("`: ").Append(tool.Does);
        }

        return sb.ToString();
    }

    /// <summary>Every group on a line of its own, its switch named, the live line after it when given, then the MCP line (<c>kind: tool</c>, no query).</summary>
    public static string List(HelpLive? live, bool windows)
    {
        var sb = new System.Text.StringBuilder(HelpText.ToolListHeading);
        foreach (var group in Groups(windows))
        {
            var first = group.Tools[0];
            sb.Append("\n- ").Append(group.Group);
            if (first.Switch is { } field)
            {
                sb.Append(" (").Append(SettingsMenu.FieldName(field)).Append(')');
            }

            sb.Append(": ").Append(string.Join(", ", group.Tools.Select(t => $"`{t.Name}`")));
            if (live is not null)
            {
                sb.Append(" — ").Append(live.GroupLine(first.Switch, group.Tools));
            }
        }

        return sb.Append('\n').Append(HelpText.ToolListMcpLine).ToString();
    }

    /// <summary>What a group switch's setting answer adds: its tools, and the group's live line when given; null for a field that switches no group.</summary>
    public static string? SwitchTools(SettingsField field, HelpLive? live, bool windows)
    {
        if (GroupOfSwitch(field, windows) is not { } group)
        {
            return null;
        }

        string tools = HelpText.ToolsLine(group.Tools.Select(t => t.Name));
        return live is null ? tools : tools + "\n  " + live.GroupLine(field, group.Tools);
    }

    /// <summary>
    /// An MCP tool by its whole name (2026-10-25): with a session, the server it belongs to and that server's state; without one (the
    /// advisors' copy), the generic facts, naming no server. Never the server's own description or detail.
    /// </summary>
    public static string McpAnswer(string name, HelpLive? live)
    {
        var found = live?.Mcp?.Invoke(name);
        var row = HelpLocation.Of(SettingsField.McpServers);
        string switchLine = HelpText.SwitchLine(row.Label, row.Path, Offers(SettingsField.McpServers, new AppSettingsData()) == true, toolStartsOff: false);
        return HelpText.McpTool(name, found?.Server, switchLine, live?.McpLine(name, found));
    }

    /// <summary>Whether <paramref name="name"/> reads as an MCP tool's name: letters, digits, <c>_</c> and <c>-</c>, two underscores inside.</summary>
    public static bool LooksLikeMcpName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int at = name.IndexOf("__", StringComparison.Ordinal);
        return at > 0 && at < name.Length - 2 && name.Length <= Mcp.McpToolName.MaxLength && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }

    private static string? SwitchText(ToolHelp entry, bool windows)
    {
        if (entry.Switch is not { } field || HelpLocation.Of(field, windows) is not { } row)
        {
            return null;
        }

        return HelpText.SwitchLine(row.Label, row.Path, Offers(field, new AppSettingsData()) == true, StartsOff(entry));
    }

    private static string NeedText(SettingsField field, bool windows)
    {
        string label = SettingsMenu.FieldName(field);
        string state = field switch
        {
            SettingsField.MemoryMode or SettingsField.SqlMode or SettingsField.OracleMode or SettingsField.MySqlMode or SettingsField.PostgresMode or SettingsField.SqliteMode => "read-write",
            _ => "on",
        };
        return HelpLocation.Of(field, windows) is { } row ? $"{label} {state} ({row.Path})" : $"{label} {state}";
    }

    /// <summary>
    /// Whether <paramref name="field"/> lets its tools be offered in <paramref name="data"/>: a switch through <see cref="ToolsText.SwitchOn"/>,
    /// <c>Memory mode</c> not disabled, a write mode read-write (the four server families' and SQLite's). Null for a field it does not know.
    /// </summary>
    public static bool? Offers(SettingsField field, AppSettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return field switch
        {
            SettingsField.MemoryMode => MemoryMode.Enabled(data),
            SettingsField.SqlMode => DatabaseWriteModes.IsReadWrite(data.SqlMode),
            SettingsField.OracleMode => DatabaseWriteModes.IsReadWrite(data.OracleMode),
            SettingsField.MySqlMode => DatabaseWriteModes.IsReadWrite(data.MySqlMode),
            SettingsField.PostgresMode => DatabaseWriteModes.IsReadWrite(data.PostgresMode),
            SettingsField.SqliteMode => SqliteModes.TryParse(data.SqliteMode, out var mode) && mode == SqliteMode.ReadWrite,
            _ => ToolsText.SwitchOn(field, data),
        };
    }

    /// <summary>Whether a need is met: <see cref="Offers"/>, except that <c>Memory mode</c> as a need (<c>save_memory</c>'s) means read-write.</summary>
    public static bool? NeedMet(SettingsField field, AppSettingsData data) =>
        field == SettingsField.MemoryMode ? MemoryMode.Saves(data) : Offers(field, data);

    /// <summary>Why an unmet need keeps a tool off: <c>Docker writes is off</c>, <c>SQL mode is read-only</c>, <c>Memory mode is not read-write</c>.</summary>
    public static string NeedReason(SettingsField field) => field switch
    {
        SettingsField.MemoryMode => SettingsMenu.FieldName(field) + " is not read-write",
        SettingsField.SqlMode or SettingsField.OracleMode or SettingsField.MySqlMode or SettingsField.PostgresMode or SettingsField.SqliteMode => SettingsMenu.FieldName(field) + " is read-only",
        _ => ToolsText.SwitchOffReason(field),
    };

    /// <summary>Whether a tool starts off by default: its switch off in a fresh profile, or the tool in a fresh profile's <c>/tools</c> list.</summary>
    public static bool StartsOff(ToolHelp entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new AppSettingsData().ToolsDisabled.Contains(entry.Name, StringComparer.Ordinal);
    }
}
