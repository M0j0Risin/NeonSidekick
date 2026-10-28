namespace NeonSidekick.Files;

/// <summary>
/// Which changing tools file checkpoints cover (2026-10-28, the checkpoints plan): <see cref="Tracked"/> write through the turn's working
/// directory or the images folder and are journaled; <see cref="Untracked"/> change files some other way (the shell, git, a share, the vault,
/// a database, a skill, the splash picture) and are named when they ran, since <c>/undo</c> and <c>/rewind … files</c> can't put their changes
/// back; <see cref="NoFiles"/> change something that isn't a file. A test holds every <see cref="Plans.PlanTools.Mutating"/> tool to exactly
/// one of the three, so the "not undone" text can't drift. An MCP tool (<c>server__tool</c>) is always untracked. Pure.
/// </summary>
public static class CheckpointTools
{
    /// <summary>The tools whose file changes a checkpoint keeps (some read-only by plan mode's rule, but saving a still or an export).</summary>
    public static readonly IReadOnlySet<string> Tracked = new HashSet<string>(StringComparer.Ordinal)
    {
        "write_file", "patch_file", "create_directory", "move", "copy", "delete", "zip", "unzip",
        "convert_to_pdf", "pdf_edit", "image_edit", "download_file", "generate_image", "unc_fetch",
        // stills and exports saved into the images folder or the working folder
        "camera_capture", "screen_capture", "ha_camera", "ad_user", "ad_group", "ad_computer", "ad_find", "ad_ou",
    };

    /// <summary>The changing tools whose file changes aren't kept, each with the kind the questions name.</summary>
    public static readonly IReadOnlyDictionary<string, UntrackedKind> Untracked = new Dictionary<string, UntrackedKind>(StringComparer.Ordinal)
    {
        ["run_command"] = UntrackedKind.Shell,
        ["process"] = UntrackedKind.Shell,
        ["execute_code"] = UntrackedKind.Shell,
        ["gitlib_stage"] = UntrackedKind.Git,
        ["gitlib_commit"] = UntrackedKind.Git,
        ["gitlib_stash"] = UntrackedKind.Git,
        ["gitlib_discard"] = UntrackedKind.Git,
        ["gitlib_delete"] = UntrackedKind.Git,
        ["gitlib_branch"] = UntrackedKind.Git,
        ["unc_write"] = UntrackedKind.Share,
        ["unc_patch"] = UntrackedKind.Share,
        ["unc_create_directory"] = UntrackedKind.Share,
        ["unc_move"] = UntrackedKind.Share,
        ["unc_copy"] = UntrackedKind.Share,
        ["unc_delete"] = UntrackedKind.Share,
        ["unc_put"] = UntrackedKind.Share,
        ["vault_write"] = UntrackedKind.Vault,
        ["vault_properties"] = UntrackedKind.Vault,
        ["vault_move"] = UntrackedKind.Vault,
        ["vault_delete"] = UntrackedKind.Vault,
        ["vault_daily"] = UntrackedKind.Vault,
        ["sqlite_execute"] = UntrackedKind.Database,
        ["duckdb_execute"] = UntrackedKind.Database,
        ["postgres_execute"] = UntrackedKind.Database,
        ["oracle_execute"] = UntrackedKind.Database,
        ["sql_execute"] = UntrackedKind.Database,
        ["mysql_execute"] = UntrackedKind.Database,
        ["skill_editor"] = UntrackedKind.Skill,
        ["set_splash_image"] = UntrackedKind.Splash,
    };

    /// <summary>The changing tools that change no file of yours (a timer, a light, a container, the app's own lists).</summary>
    public static readonly IReadOnlySet<string> NoFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "start_timer", "stop_timer", "open", "open_url", "web_save_link", "save_memory", "print_file",
        "ha_lights", "ha_scene", "ha_media", "ha_todo", "ha_call_service", "ha_assist",
        "docker_lifecycle", "docker_pull", "docker_remove", "docker_prune",
        "service_control", "service_start_type", "task_control",
        "youtube_play", "youtube_control", "youtube_save",
        "shopping_add", "shopping_check", "shopping_remove", "shopping_clear", "shopping_place", "shopping_layout",
        "monitor_input", "monitor_layout",
    };

    /// <summary>The untracked kinds among <paramref name="tools"/> (an MCP tool's <c>__</c> name counts as <see cref="UntrackedKind.Mcp"/>), each once, in enum order.</summary>
    public static IReadOnlyList<UntrackedKind> KindsAmong(IEnumerable<string> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var kinds = new SortedSet<UntrackedKind>();
        foreach (string tool in tools)
        {
            if (Untracked.TryGetValue(tool, out var kind))
            {
                kinds.Add(kind);
            }
            else if (tool.Contains("__", StringComparison.Ordinal))
            {
                kinds.Add(UntrackedKind.Mcp);
            }
        }

        return kinds.ToList();
    }
}

/// <summary>A kind of change file checkpoints can't put back, named in the <c>/undo</c> and <c>/rewind</c> questions.</summary>
public enum UntrackedKind
{
    Shell,
    Git,
    Share,
    Vault,
    Database,
    Skill,
    Splash,
    Mcp,
}
