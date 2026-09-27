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
        // files
        "get_working_directory", "search_files", "file_info", "read_file", "view_image",
        // git
        "git_status", "git_log", "git_show", "git_diff", "git_blame",
        // web
        "web_search", "web_fetch",
        // SQL: read-only by three layers already
        "sql_connections", "sql_databases", "sql_tables", "sql_columns", "sql_describe", "sql_indexes", "sql_relationships", "sql_query",
        // Obsidian
        "vault_search", "vault_list", "vault_read", "vault_links",
        // memory, skills, sessions, questions
        "recall_memory", "load_skill", "session_manager", "ask_user",
        // the advisor: Claude reads and answers, read-only whatever Claude slash command permissions says (2026-09-27)
        "claude_advisor",
    };

    /// <summary>The tools plan mode drops because they change something or start something: named, so the classification test can hold every tool to one list.</summary>
    public static readonly IReadOnlySet<string> Mutating = new HashSet<string>(StringComparer.Ordinal)
    {
        "start_timer", "stop_timer",
        "write_file", "patch_file", "create_directory", "move", "copy", "delete", "restore", "zip", "unzip", "open",
        "git_stage", "git_commit", "git_stash", "git_discard", "git_delete", "git_branch",
        "run_command", "process", "execute_code",
        "download_file", "open_url",
        "vault_write", "vault_properties", "vault_move", "vault_delete", "vault_daily",
        "save_memory", "skill_editor",
        "generate_image", "set_splash_image",
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
