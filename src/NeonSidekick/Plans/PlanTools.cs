using Microsoft.Extensions.AI;

namespace NeonSidekick.Plans;

/// <summary>
/// What plan mode lets the model call (2026-09-26): an allow-list, not a block-list. <see cref="ReadOnly"/>
/// names every tool that only reads — the files, the repository, the web, the databases, the vault,
/// the clock, memory's recall, the skills' load, the sessions' search and the question pane; anything
/// else is dropped while planning, <see cref="Mutating"/> and whatever is not named at all — every MCP
/// tool (nothing tells a server's reads from its writes) and any tool added later, until someone
/// decides it may stay. A test holds every tool the app builds to exactly one of the two lists, so a
/// new tool is a decision, not an accident. Pure.
/// </summary>
public static class PlanTools
{
    /// <summary>The tools plan mode keeps.</summary>
    public static readonly IReadOnlySet<string> ReadOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        // clock and timers
        "get_current_time", "date_difference", "shift_date", "list_timers",
        // the app's own manual (2026-10-02): documentation only
        "neon_help",
        // files
        "get_working_directory", "search_files", "file_info", "read_file", "view_image",
        // PDFs (2026-10-14, the PDF plan): read_pdf reads a PDF's text and draws its pages; nothing changes
        "read_pdf",
        // pictures' headers (2026-10-04): image_info reads, image_edit writes a new file so it is not here
        "image_info",
        // git
        "gitlib_status", "gitlib_log", "gitlib_show", "gitlib_diff", "gitlib_blame",
        // web
        "web_search", "web_fetch",
        // the saved links (later still on 2026-10-13): the list reads the profile's browser-links.json
        "web_links",
        // SQL: read-only by three layers already
        "sql_connections", "sql_tables", "sql_columns", "sql_describe", "sql_indexes", "sql_relationships", "sql_query",
        // Oracle: read-only by four layers already (2026-09-30)
        "oracle_connections", "oracle_tables", "oracle_columns", "oracle_describe", "oracle_indexes", "oracle_relationships", "oracle_query",
        // MySQL and MariaDB: read-only by the same layers (2026-09-30)
        "mysql_connections", "mysql_tables", "mysql_columns", "mysql_describe", "mysql_indexes", "mysql_relationships", "mysql_query",
        // UNC shares: the reads (2026-09-30); unc_fetch writes the working directory, so it is not here
        "unc_shares", "unc_search", "unc_info", "unc_read",
        // Docker: the reads (2026-10-02)
        "docker_containers", "docker_logs", "docker_inspect", "docker_stats", "docker_resources", "docker_compose",
        // Home Assistant: the reads (2026-09-28)
        "ha_overview", "ha_states", "ha_history",
        // printing: the list only (2026-09-28)
        "list_printers",
        // Obsidian
        "vault_search", "vault_list", "vault_read", "vault_links",
        // memory, skills, sessions, questions
        "recall_memory", "load_skill", "session_manager", "ask_user",
        // the camera: a photo asked of the user, as a question is (2026-10-02); the shot it saves is the user's own taking
        "camera_capture",
        // the screen (2026-10-04): a look at what is on it, asked like the camera's photo; screen_list names windows, no pixels
        "screen_capture", "screen_list",
        // SQLite (2026-10-04): every call reads, on a file opened read-only
        "sqlite_databases", "sqlite_tables", "sqlite_describe", "sqlite_query",
        // DuckDB (2026-10-25): every call reads, on a file opened read-only (or data files in memory), rolled back
        "duckdb_databases", "duckdb_tables", "duckdb_describe", "duckdb_query",
        // PostgreSQL (2026-10-04): every call reads, in a read-only transaction rolled back
        "postgres_connections", "postgres_schemas", "postgres_tables", "postgres_columns", "postgres_describe",
        "postgres_relationships", "postgres_indexes", "postgres_query",
        // the profile advisors (2026-10-10): another profile reads and answers over this chat's read-only tools alone
        "neon_advisor",
        // YouTube (2026-10-05): a search and a playlist's listing (2026-10-08) read the Data API; the status reads the window's last report; the saved list (2026-10-07) reads the profile's file
        "youtube_search", "youtube_playlist", "youtube_status", "youtube_saved",
        // the shopping list (2026-10-11): the list, a product's place and the stores' layouts read the profile's shopping.db
        "shopping_list", "shopping_locate", "shopping_stores",
        // the stores' reference documents (2026-10-11, phase 3): read, and read again when a file changed (the index is derived, the files untouched)
        "shopping_reference",
        // what was bought before (2026-10-11): read from the profile's shopping.db
        "shopping_recent",
        // the event logs (2026-10-15): every call reads Windows' logs or an .evtx file; nothing changes
        "eventlog_logs", "eventlog_search", "eventlog_summary", "eventlog_event",
        // the admin families' reads (2026-10-15, the admin-tools plan): the network checks, services, tasks, the directory
        "net_dns", "net_ping", "net_trace", "net_port", "net_tls", "net_certs",
        "service_list", "service_info", "task_list", "task_info",
        "ad_user", "ad_group", "ad_computer", "ad_find", "ad_ou", "ad_domain",
        // the monitors (2026-10-23): the topology and each monitor's input read, nothing changed
        "monitor_list",
    };

    /// <summary>The tools plan mode drops because they change something or start something: named, so the classification test can hold every tool to one list.</summary>
    public static readonly IReadOnlySet<string> Mutating = new HashSet<string>(StringComparer.Ordinal)
    {
        "start_timer", "stop_timer",
        "write_file", "patch_file", "create_directory", "move", "copy", "delete", "zip", "unzip", "open", "convert_to_pdf", "pdf_edit", "image_edit", "set_splash_image",
        "gitlib_stage", "gitlib_commit", "gitlib_stash", "gitlib_discard", "gitlib_delete", "gitlib_branch",
        "run_command", "process", "execute_code",
        "download_file", "open_url",
        // the saved links (later still on 2026-10-13): a link saved, removed or renamed
        "web_save_link",
        "vault_write", "vault_properties", "vault_move", "vault_delete", "vault_daily",
        "save_memory", "skill_editor",
        "generate_image",
        "ha_lights", "ha_scene", "ha_media", "ha_todo", "ha_call_service", "ha_assist",
        "print_file",
        "unc_fetch", "unc_write", "unc_patch", "unc_create_directory", "unc_move", "unc_copy", "unc_delete", "unc_put",
        "docker_lifecycle", "docker_pull", "docker_remove", "docker_prune",
        // the admin families' changes (2026-10-15): a service started, stopped or retyped; a task run, ended, enabled or disabled
        "service_control", "service_start_type", "task_control",
        // SQLite's changes (2026-10-05, SQLite mode read-write): a database changed or made
        "sqlite_execute",
        // DuckDB's changes (2026-10-25, DuckDB mode read-write)
        "duckdb_execute",
        // The server families' changes (2026-10-05, each family's mode read-write).
        "postgres_execute",
        "oracle_execute",
        "sql_execute",
        "mysql_execute",
        // YouTube (2026-10-05): a video opened and played on the user's screen, or its playback changed; a video saved or taken off the list (2026-10-07)
        "youtube_play", "youtube_control", "youtube_save",
        // the shopping list (2026-10-11): a line or a store's layout changed
        "shopping_add", "shopping_check", "shopping_remove", "shopping_clear", "shopping_place", "shopping_layout",
        // the monitors (2026-10-23): an input switched, the layout changed
        "monitor_input", "monitor_layout",
    };

    /// <summary>
    /// The read-only tools that stop a turn on a pane until the user answers: a question, a photo, a look at the screen
    /// (2026-10-18, the user's ask). A gap pass under <see cref="Settings.AppSettingsData.PlanGapAskUser"/> off is offered none
    /// of them, so a review can run unattended; <c>screen_list</c> opens no pane and stays.
    /// </summary>
    public static readonly IReadOnlySet<string> AskingTools = new HashSet<string>(StringComparer.Ordinal)
    {
        Llm.Tools.AskUserTool.ToolName, Llm.Tools.CameraCaptureTool.ToolName, Llm.Tools.ScreenCaptureTool.ToolName,
    };

    /// <summary>
    /// The changing tools that stop a call on a confirm pane until the user answers (2026-10-23, the admin review's L3): a Ralph pass at
    /// checkpoint 0 runs unattended, and one of these waited on its pane with no timeout — the loop hung, or a person coming back approved a
    /// change nobody had reviewed. Withheld from such a pass beside <see cref="AskingTools"/>, never from a gap pass (whose wording follows
    /// <see cref="AskingTools"/> alone). The same wait in other confirm-gated tools (Docker's changes; the <c>*_execute</c> tools joined on 2026-10-24,
    /// <c>monitor_input</c>/<c>monitor_layout</c>) is noted for a later review; each joins with one line here.
    /// </summary>
    public static readonly IReadOnlySet<string> ConfirmingTools = new HashSet<string>(StringComparer.Ordinal)
    {
        Llm.Tools.ServiceControlTool.ToolName, Llm.Tools.ServiceStartTypeTool.ToolName, Llm.Tools.TaskControlTool.ToolName,
        Llm.Tools.MonitorInputTool.ToolName, Llm.Tools.MonitorLayoutTool.ToolName,   // the monitors' changes ask too (2026-10-23)
        // The database changes ask on their pane too (2026-10-24, the database review's H2: an unattended pass kept them, so a session allow
        // committed with nobody watching, or the pane stalled the loop).
        Llm.Tools.SqlExecuteTool.ToolName, Llm.Tools.OracleExecuteTool.ToolName, Llm.Tools.MySqlExecuteTool.ToolName,
        Llm.Tools.PostgresExecuteTool.ToolName, Llm.Tools.SqliteExecuteTool.ToolName, Llm.Tools.DuckDbExecuteTool.ToolName,
    };

    /// <summary><paramref name="disabled"/> with <see cref="AskingTools"/> and <see cref="ConfirmingTools"/> added: what an unattended Ralph pass leaves out. Pure.</summary>
    public static IReadOnlySet<string> WithoutAskingOrConfirming(IReadOnlySet<string> disabled)
    {
        ArgumentNullException.ThrowIfNull(disabled);
        var union = new HashSet<string>(disabled, StringComparer.Ordinal);
        union.UnionWith(AskingTools);
        union.UnionWith(ConfirmingTools);
        return union;
    }

    /// <summary><paramref name="disabled"/> with every one of <see cref="AskingTools"/> added (2026-10-18): what a gap pass that may not wait on the user leaves out. Pure.</summary>
    public static IReadOnlySet<string> WithoutAsking(IReadOnlySet<string> disabled)
    {
        ArgumentNullException.ThrowIfNull(disabled);
        var union = new HashSet<string>(disabled, StringComparer.Ordinal);
        union.UnionWith(AskingTools);
        return union;
    }

    /// <summary>Whether plan mode keeps <paramref name="tool"/>.</summary>
    public static bool Allowed(string tool) => ReadOnly.Contains(tool);

    /// <summary>
    /// <paramref name="disabled"/> (the <c>/tools</c> list) widened by every tool of <paramref name="groups"/>
    /// plan mode drops, so each group loses them exactly as it loses a tool switched off — an emptied group
    /// is the group's switch off, its rule gone with it.
    /// </summary>
    public static IReadOnlySet<string> Widen(IReadOnlySet<string>? disabled, params IReadOnlyList<AIFunction>?[] groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var widened = disabled is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(disabled, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group is null)
            {
                continue;
            }

            foreach (var tool in group)
            {
                if (!Allowed(tool.Name))
                {
                    widened.Add(tool.Name);
                }
            }
        }

        return widened;
    }
}
