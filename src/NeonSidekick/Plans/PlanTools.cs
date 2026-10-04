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
        "get_current_time", "days_between", "shift_date", "list_timers",
        // the app's own manual (2026-10-02): documentation only
        "neon_help",
        // files
        "get_working_directory", "search_files", "file_info", "read_file", "view_image",
        // git
        "gitlib_status", "gitlib_log", "gitlib_show", "gitlib_diff", "gitlib_blame",
        // web
        "web_search", "web_fetch",
        // SQL: read-only by three layers already
        "sql_connections", "sql_databases", "sql_tables", "sql_columns", "sql_describe", "sql_indexes", "sql_relationships", "sql_query",
        // Oracle: read-only by four layers already (2026-09-30)
        "oracle_connections", "oracle_schemas", "oracle_tables", "oracle_columns", "oracle_describe", "oracle_indexes", "oracle_relationships", "oracle_query",
        // MySQL and MariaDB: read-only by the same layers (2026-09-30)
        "mysql_connections", "mysql_databases", "mysql_tables", "mysql_columns", "mysql_describe", "mysql_indexes", "mysql_relationships", "mysql_query",
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
        // PostgreSQL (2026-10-04): every call reads, in a read-only transaction rolled back
        "postgres_connections", "postgres_databases", "postgres_schemas", "postgres_tables", "postgres_columns", "postgres_describe",
        "postgres_relationships", "postgres_indexes", "postgres_query",
        // the advisor: Claude reads and answers, read-only whatever Claude CLI slash command permissions says (2026-09-27)
        "claude_advisor_cli",
    };

    /// <summary>The tools plan mode drops because they change something or start something: named, so the classification test can hold every tool to one list.</summary>
    public static readonly IReadOnlySet<string> Mutating = new HashSet<string>(StringComparer.Ordinal)
    {
        "start_timer", "stop_timer",
        "write_file", "patch_file", "create_directory", "move", "copy", "delete", "zip", "unzip", "open", "convert_to_pdf",
        "gitlib_stage", "gitlib_commit", "gitlib_stash", "gitlib_discard", "gitlib_delete", "gitlib_branch",
        "run_command", "process", "execute_code",
        "download_file", "open_url",
        "vault_write", "vault_properties", "vault_move", "vault_delete", "vault_daily",
        "save_memory", "skill_editor",
        "generate_image", "set_splash_image",
        "ha_lights", "ha_scene", "ha_media", "ha_todo", "ha_call_service", "ha_assist",
        "print_file",
        "unc_fetch", "unc_write", "unc_patch", "unc_create_directory", "unc_move", "unc_copy", "unc_delete", "unc_put",
        "docker_lifecycle", "docker_pull", "docker_remove", "docker_prune",
    };

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
