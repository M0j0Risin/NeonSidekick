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
    /// to sit under theirs (<see cref="TabFields"/>). Chat's Tool bars (2026-10-12, the user's ask) holds the four bar rows under
    /// Screen. Pinned.
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
                (SettingsField.HintBarItems, "Tool bars"),   // the Hint bar its first row since later on 2026-10-12
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
            [SettingsTab.Tts] =
            [
                (SettingsField.TtsOutput, "Read aloud"),
                (SettingsField.TtsVoicePreview, "Voices"),
            ],
            [SettingsTab.Stt] =
            [
                (SettingsField.SttInput, "Listening"),
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
                (SettingsField.BotChatTools, "Tools and skills"),
                (SettingsField.BotChatMemory, "Memory"),
                (SettingsField.BotChatVision, "Seeing"),
                (SettingsField.BotChatRecentContext, "Context"),
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
        [(SettingsField.YouTubeTools, "Connection"), (SettingsField.YouTubeSearchMaxResults, "Search"), (SettingsField.YouTubeAutoplay, "Playing")],
        [(SettingsField.GitLibTools, "Tools"), (SettingsField.GitLibEmail, "Identity")],
        [(SettingsField.ToolsDollarMention, "Mentions"), (SettingsField.ToolCollapseCount, "Folding"), (SettingsField.ShowFileDiffs, "Diffs")],
        [(SettingsField.ShoppingTools, "The list")],
    ];

    /// <summary>
    /// The section headings of <c>/skills</c>' settings tabs, indexed like <see cref="SkillsTabFields"/> (2026-10-10, the user's ask):
    /// Options, then Reflection, whose sessions switch moved up under What it reads (the user's call). Pinned.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<(SettingsField First, string Title)>> SkillsMenuSections =
    [
        [(SettingsField.AgentSkills, "Sources"), (SettingsField.SkillCompactMode, "Prompt and mentions")],
        [(SettingsField.ReflectionAutoLearn, "Learning"), (SettingsField.ReflectionWindow, "What it reads"), (SettingsField.ReflectionCooldownMinutes, "Pace"), (SettingsField.ReflectionConfirmMode, "What it may change")],   // its first row the confirm mode since 2026-10-12 (the user's place)
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
