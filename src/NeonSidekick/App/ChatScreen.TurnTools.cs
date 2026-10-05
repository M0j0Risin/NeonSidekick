using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;

namespace NeonSidekick.App;

/// <summary>
/// What a turn's tool list is made from (2026-10-04, the <c>/botchat</c> bots' tools): every group <c>ChatScreen.PrepareTurn</c> takes, with its switch, so the main chat's turn and a bot's are put together by one composition
/// (<see cref="ChatScreen.ComposeTurnTools"/>). A null group is not offered. <see cref="Skills"/> is the turn's skill tools after the
/// catalog's scan (<c>PrepareTurn</c> sets it). <see cref="PlanReadOnly"/> narrows to plan mode's read-only tools without
/// <c>present_plan</c> (a bot's turn while the main chat plans); <see cref="OnlyTools"/> keeps those names alone
/// (<c>Botchat limited tools</c>), each still offered only as the main chat would offer it.
/// </summary>
internal sealed record TurnToolInputs
{
    public required IReadOnlyList<AIFunction> Standing { get; init; }
    public bool ToolsEnabled { get; init; } = true;
    public IReadOnlyList<AIFunction> Memory { get; init; } = [];
    public bool MemoryEnabled { get; init; }

    /// <summary><c>Memory mode</c> read-write (2026-10-04): false (read-only) leaves <c>save_memory</c> out of the memory group.</summary>
    public bool MemorySave { get; init; } = true;
    public IReadOnlyList<AIFunction> Skills { get; init; } = [];
    public IReadOnlyList<AIFunction>? Web { get; init; }
    public bool WebEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Files { get; init; }
    public bool FilesEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Ask { get; init; }
    public IReadOnlyList<AIFunction>? Sessions { get; init; }
    public bool SessionsEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Mcp { get; init; }
    public bool McpEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Git { get; init; }
    public bool GitEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Shell { get; init; }
    public bool ShellEnabled { get; init; }
    public bool ShellBridge { get; init; }
    public bool ShellPolice { get; init; } = true;
    public bool ShellNative { get; init; }
    public IReadOnlyList<AIFunction>? Obsidian { get; init; }
    public bool ObsidianEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Sql { get; init; }
    public bool SqlEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Comfy { get; init; }
    public bool ComfyEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Advisor { get; init; }
    public bool AdvisorEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Home { get; init; }
    public bool HomeEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Print { get; init; }
    public bool PrintEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Oracle { get; init; }
    public bool OracleEnabled { get; init; }
    public IReadOnlyList<AIFunction>? MySql { get; init; }
    public bool MySqlEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Sqlite { get; init; }
    public bool SqliteEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Postgres { get; init; }
    public bool PostgresEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Unc { get; init; }
    public bool UncEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Docker { get; init; }
    public bool DockerEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Camera { get; init; }
    public bool CameraEnabled { get; init; }
    public IReadOnlyList<AIFunction>? Screen { get; init; }
    public bool ScreenEnabled { get; init; }
    public IReadOnlySet<string>? Disabled { get; init; }
    public PlanTurn? Plan { get; init; }
    public bool PlanReadOnly { get; init; }
    public IReadOnlySet<string>? OnlyTools { get; init; }
}

/// <summary>
/// A turn's tools as <see cref="ChatScreen.ComposeTurnTools"/> put them together (2026-10-04): the list in its order, the standing
/// and file tools the opening calls are found among (<see cref="Offered"/>), the rules they call for, and the three tools whose
/// per-turn state the caller starts over or seeds (<see cref="Recall"/> only while memory is on). <see cref="Empty"/> with tools off.
/// </summary>
internal sealed record TurnToolSet(IReadOnlyList<AIFunction> Tools, IReadOnlyList<AIFunction> Offered, TurnRules Rules, RecallMemoryTool? Recall, RunCommandTool? Shell, ClaudeAdvisorTool? Advisor)
{
    public static readonly TurnToolSet Empty = new([], [], new TurnRules(), null, null, null);
}

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The tool list for a turn and the rules it calls for (2026-10-04, out of <c>PrepareTurn</c> so a <c>/botchat</c> bot's turn is
    /// put together the same way): plan mode's narrowing, then <see cref="TurnToolInputs.OnlyTools"/> (every other name joins the
    /// disabled set, so an emptied group drops its rule as <c>/tools</c> does), then <see cref="Without"/> on every group, then the
    /// per-group decisions and the concatenation, unchanged — see <c>PrepareTurn</c>'s comment for each. Pure: no
    /// <c>BeginTurn</c>, no opening call; the caller does those. <see cref="TurnToolSet.Empty"/> with tools off.
    /// </summary>
    public static TurnToolSet ComposeTurnTools(TurnToolInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!inputs.ToolsEnabled)
        {
            return TurnToolSet.Empty;
        }

        var standingTools = inputs.Standing;
        var fileTools = inputs.Files;
        var webTools = inputs.Web;
        var gitTools = inputs.Git;
        var shellTools = inputs.Shell;
        var obsidianTools = inputs.Obsidian;
        var sqlTools = inputs.Sql;
        var oracleTools = inputs.Oracle;
        var mysqlTools = inputs.MySql;
        var sqliteTools = inputs.Sqlite;
        var postgresTools = inputs.Postgres;
        var uncTools = inputs.Unc;
        var dockerTools = inputs.Docker;
        var comfyTools = inputs.Comfy;
        var homeTools = inputs.Home;
        var printTools = inputs.Print;
        // Memory mode read-only (2026-10-04, the user's ask): save_memory is never offered, whatever /tools says.
        var memoryTools = inputs.MemorySave ? inputs.Memory : inputs.Memory.Where(t => t is not SaveMemoryTool).ToList();
        var skillTools = inputs.Skills;
        var sessionTools = inputs.Sessions;
        var askTools = inputs.Ask;
        var cameraTools = inputs.Camera;
        var screenTools = inputs.Screen;
        var mcpTools = inputs.Mcp;
        var advisorTools = inputs.Advisor;
        var plan = inputs.Plan;
        var disabledTools = inputs.Disabled;

        if (plan is not null || inputs.PlanReadOnly)
        {
            // Plan mode (2026-09-26): every tool it does not allow joins the /tools list for this turn, so a group
            // loses them as it loses a tool switched off, and a group left empty takes its rule with it.
            disabledTools = PlanTools.Widen(disabledTools, standingTools, fileTools, webTools, gitTools, shellTools, obsidianTools, sqlTools, oracleTools, mysqlTools, sqliteTools, postgresTools, uncTools, dockerTools, comfyTools, memoryTools, skillTools, sessionTools, askTools, mcpTools, advisorTools, homeTools, printTools);
        }

        if (inputs.OnlyTools is { } only)
        {
            // Botchat limited tools (2026-10-04): every name not on the list joins the disabled set, the same way.
            disabledTools = DisableAllBut(disabledTools, only.Contains, standingTools, fileTools, webTools, gitTools, shellTools, obsidianTools, sqlTools, oracleTools, mysqlTools, sqliteTools, postgresTools, uncTools, dockerTools, comfyTools, memoryTools, skillTools, sessionTools, askTools, cameraTools, screenTools, mcpTools, advisorTools, homeTools, printTools);
        }

        if (disabledTools is { Count: > 0 })
        {
            // The /tools list (2026-09-19): every group loses its switched-off names first, so what follows reads an emptied group as its switch off.
            standingTools = Without(standingTools, disabledTools);
            fileTools = fileTools is null ? null : Without(fileTools, disabledTools);
            webTools = webTools is null ? null : Without(webTools, disabledTools);
            gitTools = gitTools is null ? null : Without(gitTools, disabledTools);
            shellTools = shellTools is null ? null : Without(shellTools, disabledTools);
            obsidianTools = obsidianTools is null ? null : Without(obsidianTools, disabledTools);
            sqlTools = sqlTools is null ? null : Without(sqlTools, disabledTools);
            oracleTools = oracleTools is null ? null : Without(oracleTools, disabledTools);
            mysqlTools = mysqlTools is null ? null : Without(mysqlTools, disabledTools);
            sqliteTools = sqliteTools is null ? null : Without(sqliteTools, disabledTools);
            postgresTools = postgresTools is null ? null : Without(postgresTools, disabledTools);
            uncTools = uncTools is null ? null : Without(uncTools, disabledTools);
            dockerTools = dockerTools is null ? null : Without(dockerTools, disabledTools);
            comfyTools = comfyTools is null ? null : Without(comfyTools, disabledTools);
            homeTools = homeTools is null ? null : Without(homeTools, disabledTools);
            printTools = printTools is null ? null : Without(printTools, disabledTools);
            memoryTools = Without(memoryTools, disabledTools);
            skillTools = Without(skillTools, disabledTools);
            sessionTools = sessionTools is null ? null : Without(sessionTools, disabledTools);
            askTools = askTools is null ? null : Without(askTools, disabledTools);
            cameraTools = cameraTools is null ? null : Without(cameraTools, disabledTools);
            screenTools = screenTools is null ? null : Without(screenTools, disabledTools);
            mcpTools = mcpTools is null ? null : Without(mcpTools, disabledTools);
            advisorTools = advisorTools is null ? null : Without(advisorTools, disabledTools);
        }

        bool memoryEnabled = inputs.MemoryEnabled;
        bool files = inputs.FilesEnabled && fileTools is { Count: > 0 };
        // The timer sentence rides only with a timer tool (2026-09-20): headless has none, the pane loses all three on /tools.
        bool timers = standingTools.Any(t => t is StartTimerTool or StopTimerTool or ListTimersTool);
        // The manual's sentence rides with neon_help (2026-10-02): a standing tool, gone with it when /tools switches it off.
        bool help = standingTools.Any(t => t is NeonHelpTool);
        // The download tool rides the web list only while the file tools are offered (2026-09-18).
        webTools = webTools is null ? null : WebToolsFor(webTools, files);
        bool web = inputs.WebEnabled && webTools is { Count: > 0 };
        bool download = web && webTools!.Any(t => t is DownloadFileTool);
        // The delete clause of the file rule rides only while delete is offered (2026-09-20; off in a fresh profile until later on 2026-09-21).
        bool delete = files && fileTools!.Any(t => t is DeleteTool);
        // The rule quotes the caps the offered tool itself reads, so the two never disagree.
        AskLimits? ask = askTools is { Count: > 0 } ? askTools.OfType<AskUserTool>().FirstOrDefault()?.Limits ?? AskLimits.Default : null;
        IReadOnlyList<AIFunction> offered = files ? [.. standingTools, .. fileTools!] : standingTools;
        // The git tools right after the file tools (2026-09-20): the sandbox's tools together, the setting GitLib tools a per-group offer.
        bool git = inputs.GitEnabled && gitTools is { Count: > 0 };
        IReadOnlyList<AIFunction> tools = git ? [.. offered, .. gitTools!] : offered;
        // The shell tools right after the git tools (2026-09-21): the setting Shell command policy is the group's switch; execute_code rides only with an interpreter to run.
        shellTools = shellTools is null ? null : ShellToolsFor(shellTools);
        bool shell = inputs.ShellEnabled && shellTools is { Count: > 0 };
        tools = shell ? [.. tools, .. shellTools!] : tools;
        // The vault tools after the shell tools (2026-09-22): the setting Obsidian tools and a vault set are the group's switch.
        bool obsidian = inputs.ObsidianEnabled && obsidianTools is { Count: > 0 };
        tools = obsidian ? [.. tools, .. obsidianTools!] : tools;
        // The vault rule's delete sentence rides only while vault_delete is offered (later on 2026-09-22): Obsidian allow delete on, the tool not switched off.
        bool obsidianDelete = obsidian && obsidianTools!.Any(t => t is VaultDeleteTool);
        // The SQL tools after the vault tools (2026-09-23): the setting SQL tools and a connection in sql.json are the group's switch.
        bool sql = inputs.SqlEnabled && sqlTools is { Count: > 0 };
        tools = sql ? [.. tools, .. sqlTools!] : tools;
        // The Oracle tools right after the SQL tools (2026-09-30): the setting Oracle tools and a connection in oracle.json are the group's switch.
        bool oracle = inputs.OracleEnabled && oracleTools is { Count: > 0 };
        tools = oracle ? [.. tools, .. oracleTools!] : tools;
        // The MySQL tools after the Oracle tools (2026-09-30): the setting MySQL tools and a connection in mysql.json are the group's switch.
        bool mysql = inputs.MySqlEnabled && mysqlTools is { Count: > 0 };
        tools = mysql ? [.. tools, .. mysqlTools!] : tools;
        // The SQLite tools after the MySQL tools (2026-10-04): the setting SQLite tools and a database to open (named, or the sandbox's files) are the group's switch.
        bool sqlite = inputs.SqliteEnabled && sqliteTools is { Count: > 0 };
        tools = sqlite ? [.. tools, .. sqliteTools!] : tools;
        // The callers pass the list SqliteToolsFor cut (sqlite_execute only under read-write with a pane, 2026-10-05); the write sentence rides while it is left.
        bool sqliteWrite = sqlite && sqliteTools!.Any(t => SqliteWriteToolNames.Contains(t.Name));
        // The PostgreSQL tools after the SQLite tools (2026-10-04): the setting PostgreSQL tools and a connection in postgres.json are the group's switch.
        bool postgres = inputs.PostgresEnabled && postgresTools is { Count: > 0 };
        tools = postgres ? [.. tools, .. postgresTools!] : tools;
        // The UNC tools after the MySQL tools (2026-09-30): the setting UNC tools and a share in unc.json are the group's switch;
        // unc_fetch and unc_put have the working directory at their other end, so they ride only with the file tools offered.
        uncTools = uncTools is null || files ? uncTools : uncTools.Where(t => t is not (UncFetchTool or UncPutTool)).ToList();
        bool unc = inputs.UncEnabled && uncTools is { Count: > 0 };
        tools = unc ? [.. tools, .. uncTools!] : tools;
        bool uncFetch = unc && uncTools!.Any(t => t is UncFetchTool);
        bool uncWrite = unc && uncTools!.Any(t => UncWriteToolNames.Contains(t.Name));
        // The Docker tools after the UNC tools (2026-10-02): the setting Docker tools is the group's switch; the callers pass the list
        // DockerToolsFor cut (the changes only under Docker writes), and the write sentence rides while a change is left.
        bool docker = inputs.DockerEnabled && dockerTools is { Count: > 0 };
        tools = docker ? [.. tools, .. dockerTools!] : tools;
        bool dockerWrite = docker && dockerTools!.Any(t => DockerWriteToolNames.Contains(t.Name));
        // The image tools after the SQL tools (2026-09-24): ComfyUI tools, a URL and a workflow are the group's switch; no rule — the description carries the workflows and the prompt styles.
        bool comfy = inputs.ComfyEnabled && comfyTools is { Count: > 0 };
        tools = comfy ? [.. tools, .. comfyTools!] : tools;
        // The Home Assistant tools after the image tools (2026-09-28): Home Assistant tools, a URL and a token are the group's switch; its sentence after the SQL one.
        bool home = inputs.HomeEnabled && homeTools is { Count: > 0 };
        tools = home ? [.. tools, .. homeTools!] : tools;
        // The print tools after the Home Assistant tools (2026-09-28): Print tools is the group's switch; no rule — the descriptions say to print only when asked.
        bool print = inputs.PrintEnabled && printTools is { Count: > 0 };
        tools = print ? [.. tools, .. printTools!] : tools;
        // The advisor after the image tools (2026-09-27): the setting Claude CLI advisor tool is the group's switch; its sentence after the SQL one.
        bool advisor = inputs.AdvisorEnabled && advisorTools is { Count: > 0 };
        tools = advisor ? [.. tools, .. advisorTools!] : tools;
        // The shell rule's execute_code sentence promises neon_tools only while the setting Shell tool bridge is on (later on 2026-09-21).
        bool bridge = shell && inputs.ShellBridge;
        // … and its head says the shell stays under the working directory only while the setting Shell police is on (2026-09-22); off, it says a command starts there and no more.
        bool police = !shell || inputs.ShellPolice;
        tools = (web, memoryEnabled) switch
        {
            (true, true) => [.. tools, .. webTools!, .. memoryTools],
            (true, false) => [.. tools, .. webTools!],
            (false, true) => [.. tools, .. memoryTools],
            _ => tools,
        };
        tools = skillTools.Count > 0 ? [.. tools, .. skillTools] : tools;
        // The session tool after the skills (2026-09-18): the setting Session tool, a per-group offer.
        bool sessions = inputs.SessionsEnabled && sessionTools is { Count: > 0 };
        tools = sessions ? [.. tools, .. sessionTools!] : tools;
        // The MCP servers' tools after the session tool (2026-09-20): the setting MCP servers, a per-group offer over what is connected.
        bool mcp = inputs.McpEnabled && mcpTools is { Count: > 0 };
        tools = mcp ? [.. tools, .. mcpTools!] : tools;
        // camera_capture (2026-10-02): Camera tool on, the pane and a model that reads pictures; ahead of present_plan and ask_user.
        bool camera = inputs.CameraEnabled && cameraTools is { Count: > 0 };
        tools = camera ? [.. tools, .. cameraTools!] : tools;
        // screen_capture and screen_list (2026-10-04): Screen capture tool on, the pane and a model that reads pictures; after the camera.
        bool screen = inputs.ScreenEnabled && screenTools is { Count: > 0 };
        tools = screen ? [.. tools, .. screenTools!] : tools;
        // present_plan while planning (2026-09-26): after everything else, ahead of the question tool, which stays last.
        tools = plan is not null ? [.. tools, plan.Tool] : tools;
        tools = ask is not null ? [.. tools, .. askTools!] : tools;
        // … and the rules say so after the shell sentence, naming the groups offered.
        bool native = shell && inputs.ShellNative;
        var rules = new TurnRules(web, files, ask, sessions, download, delete, mcp, timers, git, shell, bridge, police, obsidian, obsidianDelete, sql, native, advisor, home, oracle, mysql, unc, uncFetch, uncWrite, docker, dockerWrite, help, sqlite, postgres, sqliteWrite, ServerWritesOf(tools));
        return new TurnToolSet(
            tools,
            offered,
            rules,
            memoryEnabled ? memoryTools.OfType<RecallMemoryTool>().FirstOrDefault() : null,
            shell ? shellTools!.OfType<RunCommandTool>().FirstOrDefault() : null,
            advisor ? advisorTools!.OfType<ClaudeAdvisorTool>().FirstOrDefault() : null);
    }

    /// <summary>
    /// <paramref name="disabled"/> widened by every tool name in <paramref name="groups"/> that <paramref name="keep"/> refuses
    /// (2026-10-04, <c>Botchat limited tools</c>; <see cref="PlanTools.Widen"/>'s shape). A new set; null groups are skipped. Pure.
    /// </summary>
    public static IReadOnlySet<string> DisableAllBut(IReadOnlySet<string>? disabled, Func<string, bool> keep, params IReadOnlyList<AIFunction>?[] groups)
    {
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(groups);
        var widened = disabled is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(disabled, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            foreach (var tool in group ?? [])
            {
                if (!keep(tool.Name))
                {
                    widened.Add(tool.Name);
                }
            }
        }

        return widened;
    }
}
