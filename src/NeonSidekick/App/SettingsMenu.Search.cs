using NeonSidekick.Help;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── Settings search and the changed settings (2026-10-04, the UI review) ──────

internal sealed partial class SettingsMenu
{
    /// <summary>The search page's breadcrumb: <c>⚙️ Settings › Search: voice</c>. Pinned.</summary>
    public static string SearchCrumbLabel(string query) => "Search: " + query;

    /// <summary>The search page's hint row. Pinned.</summary>
    public const string SearchKeys = "Enter = edit · type to search · Esc = clear, then close";

    /// <summary>The search page's one row while nothing is typed. Pinned.</summary>
    public const string SearchEmptyLine = "Type words from a setting's name, its tab or what it does.";

    /// <summary>The changed settings page's breadcrumb label. Pinned.</summary>
    public const string ChangedLabel = "Changed";

    /// <summary>The changed settings page's hint row. Pinned.</summary>
    public const string ChangedKeys = "Enter = edit · r = back to the default · Esc = close";

    /// <summary>The changed settings page's reset button. Pinned.</summary>
    public const string ResetButton = "↺ default";
    public const char ResetKey = 'r';

    /// <summary>The changed settings page's one row while every setting is its default. Pinned.</summary>
    public const string NothingChangedLine = "Every setting here is at its default.";

    /// <summary>The word <c>/settings changed</c> takes. Pinned.</summary>
    public const string ChangedWord = "changed";

    /// <summary>
    /// A tab's badge in the strip (2026-10-07, the user's ask): how many of its rows differ from the default, in superscript
    /// digits (<c>Client³</c>), so it hugs the title without a cell of its own between; null for none changed. Pinned.
    /// </summary>
    public static string? ChangedBadge(int changed)
    {
        if (changed <= 0)
        {
            return null;
        }

        const string digits = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        var text = changed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Concat(text.Select(c => digits[c - '0']));
    }

    /// <summary>The footer's last line for a row whose value is not its default: the default and a note. Pinned.</summary>
    public const string ChangedNote = " · this profile differs";

    /// <summary>
    /// Whether <paramref name="field"/>'s value in <paramref name="data"/> differs from a fresh profile's, as the menu shows both
    /// (<see cref="FieldValue(SettingsField, AppSettingsData, string)"/> over the same profile folder, so the default working
    /// directory or a path under the profile compares equal). Never for the rows that are no value of the profile's: the
    /// profile, the embedded catalog and backend doors, the sampling door. Pure.
    /// </summary>
    public static bool IsChanged(SettingsField field, AppSettingsData data, string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (field is SettingsField.Profile or SettingsField.EmbeddedModels or SettingsField.EmbeddedBackend or SettingsField.LlmSampling)
        {
            return false;
        }

        return !string.Equals(FieldValue(field, data, profileDirectory), FieldValue(field, new AppSettingsData(), profileDirectory), StringComparison.Ordinal);
    }

    /// <summary>The footer for <paramref name="field"/> as this profile has it: <see cref="FieldFooter(SettingsField)"/>, its last line noting a value that is not the default.</summary>
    internal MenuFooter FieldFooter(SettingsField field, AppSettingsData saved)
    {
        var footer = (field == SettingsField.AdvisorList ? AdvisorListFooter(saved) : null) ?? FieldFooter(field);
        return IsChanged(field, saved, _settings.ProfileDirectory) ? footer with { Last = footer.Last + ChangedNote } : footer;
    }

    /// <summary>The flags a saved <paramref name="field"/> asks the screen for, as <see cref="ShowAsync"/> gathers them.</summary>
    private static SettingsChanges ChangesOf(SettingsField field)
    {
        var changes = SettingsChanges.None;
        if (IsLlmField(field))
        {
            changes |= SettingsChanges.Llm;
        }

        if (IsTtsField(field))
        {
            changes |= SettingsChanges.Tts;
        }

        if (IsVoiceField(field))
        {
            changes |= SettingsChanges.Voice;
        }

        if (IsWebVoiceField(field))
        {
            changes |= SettingsChanges.Tts | SettingsChanges.Voice;   // what the sessions load for the Web UI (2026-11-13)
        }

        if (field == SettingsField.LlmOfferTools)
        {
            changes |= SettingsChanges.Conversation;
        }

        if (IsMcpField(field))
        {
            changes |= SettingsChanges.Mcp;
        }

        if (IsWebUiField(field))
        {
            changes |= SettingsChanges.WebUi;
        }

        return changes;
    }

    /// <summary>
    /// One row of a search or changed page: the setting's full name padded to <paramref name="width"/>, its value, and dim after it
    /// <paramref name="tail"/> (where it lives, or its default).
    /// </summary>
    private static string FoundRow(HelpRow row, string value, int width, int valueWidth, string tail) =>
        Markup.Escape(PadCells(row.Label, width))
        + Theme.ColorMarkup(Theme.Ink, value)
        + Theme.DimMarkup(new string(' ', Math.Max(0, valueWidth - TextCells.Width(value))) + "  " + tail);

    /// <summary><paramref name="row"/>'s value as its tab shows it, plain.</summary>
    private string FoundValue(HelpRow row, AppSettingsData saved, string? located) =>
        FieldValue(row.Field, saved, _settings.ProfileDirectory, row.Field == SettingsField.WebBrowserPath ? located : null);

    /// <summary>The widest a search or changed page pads its value and default columns to (2026-11-24, the last-7 review's #37). Pinned.</summary>
    internal const int FoundValueCap = 40;

    /// <summary><paramref name="text"/> padded with blanks to <paramref name="cells"/> cells; as it is when it is that wide already.</summary>
    private static string PadCells(string text, int cells) => text + new string(' ', Math.Max(0, cells - TextCells.Width(text)));

    /// <summary>
    /// The rows of a search or changed page in aligned columns (2026-11-24, the user's note): the name, the value and each tail's first
    /// part (<paramref name="lead"/>, the changed page's <c>Default: …</c>; none on the search) padded to the widest across the rows, by
    /// cell width, so the place column after them lines up.
    /// </summary>
    private List<string> FoundRows(IReadOnlyList<HelpRow> rows, AppSettingsData saved, string? located, Func<HelpRow, string?> lead, Func<HelpRow, string> place)
    {
        var values = rows.Select(r => FoundValue(r, saved, located)).ToList();
        int width = rows.Count == 0 ? 0 : rows.Max(r => TextCells.Width(r.Label)) + LabelGap;
        // The padding capped, never the value (2026-11-24, the last-7 review's #37): a long URL or path is drawn whole, only its own row's tail
        // out of line, rather than pushing every row's place column off the pane.
        int valueWidth = Math.Min(values.Select(TextCells.Width).DefaultIfEmpty(0).Max(), FoundValueCap);
        var leads = rows.Select(lead).ToList();
        int leadWidth = Math.Min(leads.Select(l => l is null ? 0 : TextCells.Width(l)).DefaultIfEmpty(0).Max(), FoundValueCap);
        return rows.Select((r, i) => FoundRow(r, values[i], width, valueWidth, (leads[i] is { } l ? PadCells(l, leadWidth) + "  " : "") + place(r))).ToList();
    }

    /// <summary>The pane label a <see cref="HelpRow.Pane"/> stands for, the glyph and its space as the crumbs have them (2026-11-24).</summary>
    internal static string PaneLabel(string pane) => pane switch
    {
        "/tools" => ToolsText.Label,
        "/skills" => SkillsText.Label,
        "/mcp" => Mcp.McpText.Label,
        _ => Title,
    };

    /// <summary>
    /// The settings search (2026-10-04, the UI review: <c>/settings &lt;words&gt;</c>, or a letter typed on a settings tab): every
    /// pane's settings scored by <see cref="NeonHelp.FindSettings"/>, each row its name, value and <c>pane › tab</c>, the footer
    /// describing the highlighted one; typing refines the words (the page's filter), Enter edits the row in place as its own
    /// tab would. The first ESC clears the words, the next closes. Returns what the saves ask the screen for.
    /// </summary>
    internal async Task<SettingsChanges> SearchAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var changes = SettingsChanges.None;
        int cursor = 0;
        string root = Root;
        Root = Title;
        try
        {
            while (true)
            {
                var saved = _settings.Current;
                var found = NeonHelp.FindSettings(query);
                string? located = _locateBrowser("");
                IReadOnlyList<string> rows = found.Count > 0
                    ? FoundRows(found, saved, located, _ => null, r => r.Pane + HelpText.PathSeparator + r.Place)
                    : [query.Length == 0 ? Theme.DimMarkup(SearchEmptyLine) : MenuFilter.NoMatchRow(query)];
                if (!_pane.Enabled)
                {
                    // No pane on an interactive console (2026-11-24, the plan's check): the rows as lines, as other lists print without it.
                    foreach (string line in rows)
                    {
                        Flow.Notice(Markup.Remove(line));
                    }

                    return changes;
                }

                var page = new MenuPage(Crumb(SearchCrumbLabel(query)), rows, SearchKeys)
                {
                    Filter = query,
                    Placeholder = found.Count == 0,
                    Footer = (_, row) => row < found.Count ? FieldFooter(found[row].Field, _settings.Current) : null,
                };
                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return changes;
                }

                if (pick.Filter is { } typed)
                {
                    query = typed;
                    cursor = 0;
                    continue;
                }

                cursor = pick.Row;
                if (pick.Row >= found.Count)
                {
                    continue;
                }

                var field = found[pick.Row].Field;
                if (_reply.Running && RefusedMidTurn(field))
                {
                    Sink.Notice(NotWhileReplyRunsNotice);
                    continue;
                }

                if (field == SettingsField.Profile)
                {
                    if (await PickProfileAsync(Crumb(FieldName(SettingsField.Profile)), SwitchKeys, close: false, cancellationToken).ConfigureAwait(false))
                    {
                        return changes | SettingsChanges.Profile;   // another profile: its settings, so the search ends
                    }

                    continue;
                }

                if (await EditAsync(field, saved, page, pick.Row, cancellationToken).ConfigureAwait(false))
                {
                    changes |= ChangesOf(field);
                    if (field == SettingsField.EmbeddedModels)
                    {
                        return changes;   // a use or an install: the screen takes the model from here, as from the tab
                    }
                }

                if (TakeProfileSwitched())
                {
                    return changes | SettingsChanges.Profile;   // an advisor's Switch to profile (2026-11-22): its settings now, so the search ends
                }

                if (_embeddedLlmCleared)
                {
                    _embeddedLlmCleared = false;
                    changes |= SettingsChanges.Llm;
                }
            }
        }
        finally
        {
            Root = root;
        }
    }

    /// <summary>
    /// <c>/settings changed</c> (2026-10-04, the UI review): every setting of every pane whose value is not a fresh profile's
    /// (<see cref="IsChanged"/>), its value and its default; Enter edits the row, R (the title row's button) puts it back to the
    /// default (<see cref="ResetToDefault"/>). Returns what the saves ask the screen for.
    /// </summary>
    internal async Task<SettingsChanges> ChangedAsync(CancellationToken cancellationToken)
    {
        var changes = SettingsChanges.None;
        int cursor = 0;
        string root = Root;
        Root = Title;
        try
        {
            while (true)
            {
                var saved = _settings.Current;
                var changed = HelpLocation.Rows.Where(r => IsChanged(r.Field, saved, _settings.ProfileDirectory)).ToList();
                string? located = _locateBrowser("");
                // A labeled heading per pane, in HelpLocation.Rows' order (2026-11-24, the user's pick), so each tail drops the pane; the
                // columns aligned across the whole list. The cursor stays an item (a changed row); headings map to -1.
                var found = FoundRows(changed, saved, located, r => DefaultLabel + HelpLocation.Default(r.Field), r => r.Place);
                var rows = new List<string>();
                var headings = new HashSet<int>();
                var items = new List<int>();   // the changed row each page row is, -1 for a heading
                foreach (var pane in changed.Select((r, i) => (r.Pane, Index: i)).GroupBy(p => p.Pane))
                {
                    headings.Add(rows.Count);
                    rows.Add(SectionRule.Markup(PaneLabel(pane.Key), pane.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    items.Add(-1);
                    foreach (var one in pane)
                    {
                        rows.Add(found[one.Index]);
                        items.Add(one.Index);
                    }
                }

                if (changed.Count == 0)
                {
                    rows.Add(Theme.DimMarkup(NothingChangedLine));
                    items.Add(-1);
                }

                if (!_pane.Enabled)
                {
                    // No pane on an interactive console (2026-11-24, the plan's check): the rows under their pane headings as lines.
                    for (int i = 0; i < rows.Count; i++)
                    {
                        Flow.Notice(headings.Contains(i) ? Markup.Remove(rows[i]) : "  " + Markup.Remove(rows[i]));
                    }

                    return changes;
                }

                int ItemOf(int row) => row >= 0 && row < items.Count ? items[row] : -1;
                var page = new MenuPage(Crumb(ChangedLabel), rows, ChangedKeys)
                {
                    Buttons = changed.Count > 0 ? [new MenuButton(ResetButton, ResetKey)] : null,
                    Placeholder = changed.Count == 0,
                    Headings = headings.Count > 0 ? headings : null,
                    Footer = (_, row) => ItemOf(row) is >= 0 and var at ? FieldFooter(changed[at].Field) : null,
                };
                int start = changed.Count == 0 ? 0 : items.IndexOf(Math.Clamp(cursor, 0, changed.Count - 1));
                if (await _pane.PickAsync(page, start, cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return changes;
                }

                int item = ItemOf(pick.Row);
                if (item < 0 || item >= changed.Count)
                {
                    continue;
                }

                cursor = item;
                var field = changed[item].Field;
                if (_reply.Running && RefusedMidTurn(field))
                {
                    Sink.Notice(NotWhileReplyRunsNotice);
                    continue;
                }

                if (pick.Button >= 0 ? ResetToDefault(field) : await EditAsync(field, saved, page, pick.Row, cancellationToken).ConfigureAwait(false))
                {
                    changes |= ChangesOf(field);
                }

                if (TakeProfileSwitched())
                {
                    return changes | SettingsChanges.Profile;   // an advisor's Switch to profile (2026-11-22): another profile's changes now
                }
            }
        }
        finally
        {
            Root = root;
        }
    }

    /// <summary>
    /// <paramref name="field"/> put back to its default, saved with its row's notice: every property of the profile whose fresh
    /// value moves the row as the menu shows it, copied from a fresh <see cref="AppSettingsData"/> through the source-generated
    /// property accessors (no table to keep, no reflection). False when the row already reads its default.
    /// </summary>
    internal bool ResetToDefault(SettingsField field)
    {
        var current = _settings.Current;
        var properties = DefaultingProperties(field, current, _settings.ProfileDirectory);
        if (properties.Count == 0)
        {
            return Unchanged();
        }

        var fresh = new AppSettingsData();
        Apply(field, data =>
        {
            foreach (var property in properties)
            {
                property.Set!(data, property.Get!(fresh));
            }
        });
        return true;
    }

    /// <summary>The profile's properties that, put back to a fresh profile's value one at a time, change how <paramref name="field"/>'s row reads. Pure.</summary>
    internal static IReadOnlyList<System.Text.Json.Serialization.Metadata.JsonPropertyInfo> DefaultingProperties(SettingsField field, AppSettingsData current, string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!IsChanged(field, current, profileDirectory))
        {
            return [];
        }

        var fresh = new AppSettingsData();
        string have = FieldValue(field, current, profileDirectory);
        var found = new List<System.Text.Json.Serialization.Metadata.JsonPropertyInfo>();
        foreach (var property in SettingsJsonContext.Default.AppSettingsData.Properties)
        {
            if (property.Get is null || property.Set is null)
            {
                continue;
            }

            var trial = AppSettings.Copy(current);
            property.Set(trial, property.Get(fresh));
            if (!string.Equals(FieldValue(field, trial, profileDirectory), have, StringComparison.Ordinal))
            {
                found.Add(property);
            }
        }

        return found;
    }
}
