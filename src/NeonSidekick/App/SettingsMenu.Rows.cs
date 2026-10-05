using NeonSidekick.Help;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── The settings tabs' rows: sections, short names and the footer (2026-10-04, the UI review) ──

internal sealed partial class SettingsMenu
{
    /// <summary>
    /// The section headings of the long tabs (2026-10-04, the UI review: General, LLM and Botchat were long flat lists whose runs the
    /// <see cref="TabFields"/> comment named but the pane never showed): each heading over the field it names, drawn as a
    /// <see cref="SectionRule"/> the cursor never rests on. The other tabs have none. Pinned.
    /// </summary>
    public static readonly IReadOnlyDictionary<SettingsTab, IReadOnlyList<(SettingsField First, string Title)>> TabSections =
        new Dictionary<SettingsTab, IReadOnlyList<(SettingsField First, string Title)>>
        {
            [SettingsTab.General] =
            [
                (SettingsField.Profile, "Who and where"),
                (SettingsField.QueueMessages, "Input line"),
                (SettingsField.TranscriptMarkdown, "Transcript"),
                (SettingsField.Theme, "Screen"),
                (SettingsField.DraftEditor, "Outside apps"),
            ],
            [SettingsTab.Llm] =
            [
                (SettingsField.LlmScanMode, "Connection"),
                (SettingsField.LlmReasoning, "How it answers"),
                (SettingsField.LlmOfferTools, "Tools and limits"),
                (SettingsField.LlmContextLength, "Context"),
                (SettingsField.LlmPictureKeep, "Pictures and verbs"),
            ],
            [SettingsTab.BotChat] =
            [
                (SettingsField.BotChatLlmMode, "Models"),
                (SettingsField.BotChatComfy, "Pictures"),
                (SettingsField.BotChatNonTtsDelaySeconds, "Pace"),
                (SettingsField.BotChatTools, "Tools and skills"),
                (SettingsField.BotChatMemory, "Memory"),
                (SettingsField.BotChatVision, "Seeing"),
            ],
        };

    /// <summary>Each settings tab's rows as the pane shows them: its field, or null for a heading. Built once from <see cref="TabFields"/> and <see cref="TabSections"/>.</summary>
    private static readonly IReadOnlyList<SettingsField?>[] s_tabRows = BuildTabRows();

    private static IReadOnlyList<SettingsField?>[] BuildTabRows()
    {
        var rows = new IReadOnlyList<SettingsField?>[TabFields.Count];
        for (int tab = 0; tab < rows.Length; tab++)
        {
            var sections = TabSections.GetValueOrDefault((SettingsTab)tab);
            var list = new List<SettingsField?>(TabFields[tab].Count + (sections?.Count ?? 0));
            foreach (var field in TabFields[tab])
            {
                if (sections is not null && sections.Any(s => s.First == field))
                {
                    list.Add(null);
                }

                list.Add(field);
            }

            rows[tab] = list;
        }

        return rows;
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
