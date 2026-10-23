using NeonSidekick.Help;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── The settings tabs' rows: sections, short names and the footer (2026-10-04, the UI review) ──

internal sealed partial class SettingsMenu
{
    /// <summary>
    /// The section headings of the long tabs (2026-10-04, the UI review: General, LLM and Botchat were long flat lists whose runs the
    /// <see cref="TabFields"/> comment named but the pane never showed): each heading over the field it names, drawn as a
    /// <see cref="SectionRule"/> the cursor never rests on. The LLM tab's split (2026-10-10) kept its runs on Common, Connection
    /// over the URL now, and gave Http three of its own. Every other tab got its own later that day (the user's ask: the Anthropic
    /// tab ran the Claude Code server, <c>/claude</c> and the Anthropic API together with nothing saying whose row was whose, and
    /// the rest wanted the same look): Anthropic's Claude Code (the executable both read), Claude Code server, <c>/claude</c>
    /// command and Anthropic API; Embedded, Docker, OpenAI, TTS, STT, Sessions and Advisors in runs of their own, a few rows moved
    /// to sit under theirs (<see cref="TabFields"/>). Chat's Tool bars (2026-10-12, the user's ask) holds the bar rows under
    /// Screen. Web UI's three runs (2026-10-16, the user's ask): Server, then Guardrails and Tabs, "The page" split into what the
    /// page may do and which tabs it shows. Common's Plan mode (2026-10-17, the user's ask): the gap review's two rows,
    /// <c>LLM plan gap passes</c> and <c>LLM plan gap clean passes</c>, between Tools and limits and Context (four since 2026-10-21, the
    /// first renamed <c>LLM plan gap max passes</c> and <c>LLM plan gap stop tone</c> last). Plan mode, Ralph loop and Code audit left
    /// Common for the Automation tab on 2026-10-21 (the user's ask), as they were, and their rows lost "LLM " the same day (<c>Plan gap max passes</c>…). TTS's first run is Text-to-speech and STT's
    /// Speech-to-text since the same day (the user's ask; Read aloud and Listening until then). Pinned.
    /// </summary>
    public static readonly IReadOnlyDictionary<SettingsTab, IReadOnlyList<(SettingsField First, string Title)>> TabSections =
        new Dictionary<SettingsTab, IReadOnlyList<(SettingsField First, string Title)>>
        {
            [SettingsTab.Chat] =
            [
                (SettingsField.Profile, "Who and where"),
                (SettingsField.QueueMode, "Input line"),
                (SettingsField.TranscriptMarkdown, "Transcript"),
                (SettingsField.Theme, "Screen"),
                (SettingsField.HintsBar, "Tool bars"),   // the Hints bar its first row since 2026-10-14 (the Hint bar since later on 2026-10-12)
                (SettingsField.DraftEditor, "Outside apps"),
            ],
            [SettingsTab.Common] =
            [
                (SettingsField.LlmUrl, "Connection"),
                (SettingsField.LlmReasoning, "How it answers"),
                (SettingsField.LlmOfferTools, "Tools and limits"),
                (SettingsField.LlmContextLength, "Context"),
                (SettingsField.LlmPictureKeep, "Pictures and verbs"),
            ],
            [SettingsTab.Http] =
            [
                (SettingsField.LlmScanMode, "Connection"),
                (SettingsField.LlmPreserveThinking, "Thinking"),
                (SettingsField.LlmSampling, "Sampling"),
            ],
            [SettingsTab.Embedded] =
            [
                (SettingsField.EmbeddedLlmServer, "Server and models"),
                (SettingsField.EmbeddedBackend, "GPU and memory"),
                (SettingsField.EmbeddedVision, "Vision and drafting"),
            ],
            [SettingsTab.Docker] =
            [
                (SettingsField.DockerServers, "Containers"),
                (SettingsField.DockerServerStopTimeoutSeconds, "Starting and stopping"),
            ],
            [SettingsTab.Anthropic] =
            [
                (SettingsField.ClaudeCodeExecutable, "Claude Code"),
                (SettingsField.ClaudeCodeServer, "Claude Code server"),
                (SettingsField.ClaudeCodePermissions, "/claude command"),
                (SettingsField.AnthropicApi, "Anthropic API"),
            ],
            [SettingsTab.OpenAI] =
            [
                (SettingsField.OpenAIApi, "Connection"),
                (SettingsField.OpenAIApiMaxTokens, "Requests"),
            ],
            [SettingsTab.Automation] =
            [
                (SettingsField.PlanGapPasses, "Plan mode"),   // 2026-10-17, the user's ask: the gap review's two rows under a heading of their own (three since 2026-10-18, Plan gap ask user; four since 2026-10-21, Plan gap stop tone; "LLM plan gap …" until 2026-10-21); on Common until 2026-10-21
                (SettingsField.RalphLoopReviewer, "Ralph loop"),   // later on 2026-10-18: the loop's seven rows, a section of their own (they would swamp Plan mode's three, four since 2026-10-21)
                (SettingsField.CodeAuditPasses, "Code audit"),   // 2026-10-18, /code-audit's six rows after Plan mode (seven since 2026-10-21, Code audit stop tone; "LLM code audit …" until 2026-10-21)
            ],
            [SettingsTab.Tts] =
            [
                (SettingsField.TtsOutput, "Text-to-speech"),   // 2026-10-21, the user's ask; "Read aloud" until then
                (SettingsField.TtsVoicePreview, "Voices"),
            ],
            [SettingsTab.Stt] =
            [
                (SettingsField.SttInput, "Speech-to-text"),   // 2026-10-21, the user's ask; "Listening" until then
                (SettingsField.SttWake, "Wake word"),
                (SettingsField.SttInterrupt, "Interruptions"),
            ],
            [SettingsTab.Sessions] =
            [
                (SettingsField.SessionLogging, "Saving"),
                (SettingsField.SessionNamingMode, "Titles"),
                (SettingsField.SessionTool, "Search tool"),
            ],
            [SettingsTab.BotChat] =
            [
                (SettingsField.BotChatLlmMode, "Models"),
                (SettingsField.BotChatComfy, "Pictures"),
                (SettingsField.BotChatNonTtsDelaySeconds, "Pace"),
                (SettingsField.BotChatLimitedTools, "Tools and skills"),
                (SettingsField.BotChatMemory, "Memory"),
                (SettingsField.BotChatVision, "Seeing"),
                (SettingsField.BotChatRecentContext, "Context"),
            ],
            [SettingsTab.WebUi] =
            [
                (SettingsField.WebUi, "Server"),
                (SettingsField.WebUiCommands, "Guardrails"),
                (SettingsField.WebUiProfileTheme, "Tabs"),
            ],
            [SettingsTab.Advisors] =
            [
                (SettingsField.Advisors, "The panel"),
                (SettingsField.AdvisorContext, "Each call"),
                (SettingsField.AdvisorThreadPairs, "Threads and servers"),
            ],
        };

    /// <summary>
    /// The section headings of <c>/tools</c>' menus, indexed like <see cref="ToolsTabFields"/> (2026-10-10, the user's ask: the
    /// <c>/settings</c> look on the other panes' menus): each heading over the field it names, every menu headed, even the short
    /// ones. The YouTube menu moved its autoplay and voice rows under the search rows to sit under Playing (the user's call, as
    /// <see cref="TabSections"/>' four tabs did). Pinned.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<(SettingsField First, string Title)>> ToolsMenuSections =
    [
        [(SettingsField.AskUser, "Questions"), (SettingsField.AskMaxQuestions, "Limits")],
        [(SettingsField.WebTools, "Browser"), (SettingsField.WebSearchMethod, "Search"), (SettingsField.WebDownloadMaxMegabytes, "Downloads")],
        [(SettingsField.ShellCommandPolicy, "Policy"), (SettingsField.ShellPolice, "Guards"), (SettingsField.ShellPreferNative, "Commands"), (SettingsField.ShellCodeLanguages, "Scripts")],
        [(SettingsField.FileTools, "Files"), (SettingsField.FileTreeMaxLength, "/tree"), (SettingsField.FileMentionFolderMode, "Browsing"), (SettingsField.FileViewImageMaxPerCall, "Limits")],
        [(SettingsField.ImageTools, "Editing"), (SettingsField.ImageEditOutputFolder, "Folders")],
        [(SettingsField.UncTools, "Access"), (SettingsField.UncSharesOffered, "Shares"), (SettingsField.UncSharesProfile, "Files")],
        [(SettingsField.PrintTools, "Printing"), (SettingsField.PrintDefaultPrinter, "Output")],
        [(SettingsField.CameraTools, "Shutter"), (SettingsField.CameraDevice, "Device"), (SettingsField.CameraOutputFolder, "Photos"), (SettingsField.CameraWatchSeconds, "Watch")],
        [(SettingsField.ScreenTools, "Capture"), (SettingsField.ScreenOutputFolder, "Pictures")],
        [(SettingsField.ObsidianTools, "Vault")],
        [(SettingsField.SqlTools, "Access"), (SettingsField.SqlConnectionsOffered, "Connections"), (SettingsField.SqlQueryMaxRows, "Queries"), (SettingsField.SqlConnectionsProfile, "Files")],
        [(SettingsField.MySqlTools, "Access"), (SettingsField.MySqlConnectionsOffered, "Connections"), (SettingsField.MySqlQueryMaxRows, "Queries"), (SettingsField.MySqlConnectionsProfile, "Files")],
        [(SettingsField.SqliteTools, "Access"), (SettingsField.SqliteDatabasesOffered, "Databases"), (SettingsField.SqliteQueryMaxRows, "Queries"), (SettingsField.SqliteDatabasesProfile, "Files")],
        [(SettingsField.PostgresTools, "Access"), (SettingsField.PostgresConnectionsOffered, "Connections"), (SettingsField.PostgresQueryMaxRows, "Queries"), (SettingsField.PostgresConnectionsProfile, "Files")],
        [(SettingsField.OracleTools, "Access"), (SettingsField.OracleConnectionsOffered, "Connections"), (SettingsField.OracleQueryMaxRows, "Queries"), (SettingsField.OracleConnectionsProfile, "Files")],
        [(SettingsField.DockerTools, "Access"), (SettingsField.DockerEnginePipe, "Engine")],
        [(SettingsField.HomeAssistantTools, "Connection"), (SettingsField.HomeAssistantActionPolicy, "Requests")],
        [(SettingsField.ComfyTools, "Server"), (SettingsField.ComfyWorkflowsOffered, "Workflows"), (SettingsField.ComfyTimeoutSeconds, "Runs"), (SettingsField.ComfyPictureStrip, "Pictures")],
        [(SettingsField.YouTubeTools, "Connection"), (SettingsField.YouTubeSearchMaxResults, "Search"), (SettingsField.YouTubeOpenLinksIn, "Playing")],
        [(SettingsField.GitLibTools, "Tools"), (SettingsField.GitLibEmail, "Identity")],
        [(SettingsField.ToolsDollarMention, "Mentions"), (SettingsField.ToolCollapseCount, "Folding"), (SettingsField.ShowFileDiffs, "Diffs")],
        [(SettingsField.ShoppingTools, "The list")],
        [(SettingsField.EventLogTools, "Access"), (SettingsField.EventLogComputersOffered, "Computers"), (SettingsField.EventLogMaxEvents, "Searches"), (SettingsField.EventLogComputersProfile, "Files")],
        [(SettingsField.TimerTools, "Timers")],
        [(SettingsField.NetworkTools, "Access"), (SettingsField.NetworkMaxHostsPerTurn, "Budget"), (SettingsField.NetworkAllowedHosts, "Hosts"), (SettingsField.NetworkCertificateStores, "Certificates")],
        [(SettingsField.ServiceTools, "Access"), (SettingsField.ServiceComputersOffered, "Computers"), (SettingsField.ServiceComputersProfile, "Files")],
        [(SettingsField.TaskTools, "Access"), (SettingsField.TaskComputersOffered, "Computers"), (SettingsField.TaskComputersProfile, "Files")],
        [(SettingsField.DirectoryTools, "Access"), (SettingsField.DirectoryDomainsOffered, "Domains"), (SettingsField.DirectoryDomainsProfile, "Files")],
        [(SettingsField.ClockTools, "Clock")],
        [(SettingsField.HelpTools, "Help")],
        [(SettingsField.MonitorTools, "Monitors")],
    ];

    /// <summary>
    /// The section headings of <c>/skills</c>' settings tabs, indexed like <see cref="SkillsTabFields"/> (2026-10-10, the user's ask):
    /// Options, then Reflection, whose sessions switch moved up under What it reads (the user's call). Pinned.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<(SettingsField First, string Title)>> SkillsMenuSections =
    [
        [(SettingsField.AgentSkills, "Sources"), (SettingsField.SkillCompactMode, "Prompt and mentions")],
        [(SettingsField.ReflectionAutoLearn, "Learning"), (SettingsField.ReflectionWindow, "What it reads"), (SettingsField.ReflectionCooldownMinutes, "Pace"), (SettingsField.ReflectionMode, "What it may change")],   // its first row the confirm mode since 2026-10-12 (the user's place), Reflection mode above it since 2026-10-16
    ];

    /// <summary>The section headings of <c>/mcp</c>' Options tab, indexed like <see cref="McpTabFields"/> (2026-10-10, the user's ask). Pinned.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<(SettingsField First, string Title)>> McpMenuSections =
    [
        [(SettingsField.McpServers, "Servers")],
    ];

    /// <summary>Each settings tab's rows as the pane shows them: its field, or null for a heading. Built once from <see cref="TabFields"/> and <see cref="TabSections"/>.</summary>
    private static readonly IReadOnlyList<SettingsField?>[] s_tabRows = BuildTabRows();

    private static IReadOnlyList<SettingsField?>[] BuildTabRows()
    {
        var rows = new IReadOnlyList<SettingsField?>[TabFields.Count];
        for (int tab = 0; tab < rows.Length; tab++)
        {
            rows[tab] = SectionedRows(TabFields[tab], TabSections.GetValueOrDefault((SettingsTab)tab));
        }

        return rows;
    }

    /// <summary>
    /// <paramref name="fields"/> as a pane shows them under <paramref name="sections"/> (<see cref="FieldsTab"/>'s rows): each field,
    /// a null before one a section starts at — the heading's row. The <c>/settings</c> tabs' map since 2026-10-10 the other panes'
    /// menus' too (<see cref="ToolsMenuSections"/>, <see cref="SkillsMenuSections"/>, <see cref="McpMenuSections"/>). Pure.
    /// </summary>
    public static IReadOnlyList<SettingsField?> SectionedRows(IReadOnlyList<SettingsField> fields, IReadOnlyList<(SettingsField First, string Title)>? sections)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var list = new List<SettingsField?>(fields.Count + (sections?.Count ?? 0));
        foreach (var field in fields)
        {
            if (sections is not null && sections.Any(s => s.First == field))
            {
                list.Add(null);
            }

            list.Add(field);
        }

        return list;
    }

    /// <summary>The field on pane row <paramref name="row"/> of <paramref name="fields"/> under <paramref name="sections"/>; null for a heading or past the rows. Pure.</summary>
    public static SettingsField? SectionedField(IReadOnlyList<SettingsField> fields, IReadOnlyList<(SettingsField First, string Title)>? sections, int row)
    {
        var rows = SectionedRows(fields, sections);
        return row >= 0 && row < rows.Count ? rows[row] : null;
    }

    /// <summary>The rows of settings tab <paramref name="tab"/> on the pane: a field, or null for a heading (<see cref="TabSections"/>). Pure.</summary>
    public static IReadOnlyList<SettingsField?> TabRows(int tab) => s_tabRows[tab];

    /// <summary>The pane row of <paramref name="field"/> on settings tab <paramref name="tab"/>, headings counted; −1 when the tab does not show it. Pure.</summary>
    public static int RowOf(int tab, SettingsField field)
    {
        var rows = s_tabRows[tab];
        for (int row = 0; row < rows.Count; row++)
        {
            if (rows[row] == field)
            {
                return row;
            }
        }

        return -1;
    }

    /// <summary>
    /// A row's name on a tab titled <paramref name="tabTitle"/> (2026-10-04, the UI review: the Botchat tab said "Botchat" seventeen
    /// times): <see cref="FieldName"/> without a leading <c>&lt;tab title&gt; </c>, its first letter raised (<c>Botchat LLM mode</c> is
    /// <c>LLM mode</c> there, <c>TTS output</c> is <c>Output</c>); the full name everywhere else — notices, help, the README. A
    /// <c>/settings</c> tab uses it only when every row carries the title (<see cref="FieldsTab"/>). Pure.
    /// </summary>
    public static string RowName(SettingsField field, string tabTitle)
    {
        ArgumentNullException.ThrowIfNull(tabTitle);
        string name = FieldName(field);
        if (tabTitle.Length == 0 || name.Length <= tabTitle.Length + 1
            || !name.StartsWith(tabTitle + " ", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        string rest = name[(tabTitle.Length + 1)..];
        return char.ToUpperInvariant(rest[0]) + rest[1..];
    }

    /// <summary>
    /// A row's name on the pane where its section says the rest (2026-10-12, the user's names: <c>Toolbar order</c> is
    /// <c>Order</c> under Tool bars), else <paramref name="name"/>, what the row would be called there otherwise. The full
    /// <see cref="FieldName"/> stays everywhere else — notices, <c>neon_help</c>, SETTINGS.md — where a bare "Order" would say
    /// nothing, as <see cref="RowName"/>'s short names do. Pure.
    /// </summary>
    public static string PaneName(SettingsField field, string name) => field switch
    {
        SettingsField.BarOrder => BarOrderPaneName,
        _ => name,
    };

    /// <summary><c>Toolbar order</c>'s name on the pane (2026-10-12). Pinned.</summary>
    public const string BarOrderPaneName = "Order";

    /// <summary>
    /// The <c>/settings</c> tabs whose rows keep their whole names though every one carries the tab's title (2026-10-10, the user's
    /// call: the OpenAI tab read "API", "API key", "API max tokens"…, a bare "API" saying nothing of whose). Pure.
    /// </summary>
    public static bool KeepsWholeNames(string tabTitle) => string.Equals(tabTitle, OpenAITabTitle, StringComparison.Ordinal);

    /// <summary>
    /// What the pane says under the list about <paramref name="field"/> (2026-10-04, the UI review: no row said what it does, while
    /// <c>neon_help</c> had every one's description): <see cref="HelpSettings.Describe"/> as plain text (its Markdown's backticks
    /// and asterisks dropped), then its compiled default on a last line of its own (<see cref="HelpLocation.Default"/>). Pinned.
    /// </summary>
    public static MenuFooter FieldFooter(SettingsField field) =>
        new(PlainHelp(HelpSettings.Describe(field)), DefaultLabel + HelpLocation.Default(field));

    /// <summary>The start of the footer's last line. Pinned.</summary>
    public const string DefaultLabel = "Default: ";

    /// <summary><paramref name="markdown"/> without the inline Markdown <c>neon_help</c>'s text carries: backticks and asterisks. Pure.</summary>
    public static string PlainHelp(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return markdown.Replace("`", "", StringComparison.Ordinal).Replace("*", "", StringComparison.Ordinal);
    }
}
