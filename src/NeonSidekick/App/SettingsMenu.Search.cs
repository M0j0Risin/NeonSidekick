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
    public const string SearchKeys = "Enter = edit · type to search · ESC = clear, then close";

    /// <summary>The search page's one row while nothing is typed. Pinned.</summary>
    public const string SearchEmptyLine = "Type words from a setting's name, its tab or what it does.";

    /// <summary>The changed settings page's breadcrumb label. Pinned.</summary>
    public const string ChangedLabel = "Changed";

    /// <summary>The changed settings page's hint row. Pinned.</summary>
    public const string ChangedKeys = "Enter = edit · R = back to the default · ESC = close";

    /// <summary>The changed settings page's reset button. Pinned.</summary>
    public const string ResetButton = "↺ default";
    public const char ResetKey = 'r';

    /// <summary>The changed settings page's one row while every setting is its default. Pinned.</summary>
    public const string NothingChangedLine = "Every setting here is at its default.";

    /// <summary>The word <c>/settings changed</c> takes. Pinned.</summary>
    public const string ChangedWord = "changed";

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
        var footer = FieldFooter(field);
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

        if (field == SettingsField.LlmOfferTools)
        {
            changes |= SettingsChanges.Conversation;
        }

        if (IsMcpField(field))
        {
            changes |= SettingsChanges.Mcp;
        }

        return changes;
    }

    /// <summary>
    /// One row of a search or changed page: the setting's full name padded to <paramref name="width"/>, its value, and dim after it
    /// <paramref name="tail"/> (where it lives, or its default).
    /// </summary>
    private string FoundRow(HelpRow row, AppSettingsData saved, int width, string? located, string tail) =>
        Markup.Escape(row.Label.PadRight(width))
        + Theme.ColorMarkup(Theme.Ink, FieldValue(row.Field, saved, _settings.ProfileDirectory, row.Field == SettingsField.WebBrowserPath ? located : null))
        + Theme.DimMarkup("  " + tail);

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
                int width = found.Count == 0 ? 0 : found.Max(r => r.Label.Length) + LabelGap;
                IReadOnlyList<string> rows = found.Count > 0
                    ? found.Select(r => FoundRow(r, saved, width, located, r.Pane + HelpText.PathSeparator + r.Tab)).ToList()
                    : [query.Length == 0 ? Theme.DimMarkup(SearchEmptyLine) : MenuFilter.NoMatchRow(query)];
                var page = new MenuPage(Crumb(SearchCrumbLabel(query)), rows, SearchKeys)
                {
                    Filter = query,
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
                if (_midTurn && RefusedMidTurn(field))
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
                int width = changed.Count == 0 ? 0 : changed.Max(r => r.Label.Length) + LabelGap;
                IReadOnlyList<string> rows = changed.Count > 0
                    ? changed.Select(r => FoundRow(r, saved, width, located, DefaultLabel + HelpLocation.Default(r.Field) + "  " + r.Pane + HelpText.PathSeparator + r.Tab)).ToList()
                    : [Theme.DimMarkup(NothingChangedLine)];
                var page = new MenuPage(Crumb(ChangedLabel), rows, ChangedKeys)
                {
                    Buttons = changed.Count > 0 ? [new MenuButton(ResetButton, ResetKey)] : null,
                    Footer = (_, row) => row < changed.Count ? FieldFooter(changed[row].Field) : null,
                };
                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return changes;
                }

                cursor = pick.Row;
                if (pick.Row >= changed.Count)
                {
                    continue;
                }

                var field = changed[pick.Row].Field;
                if (_midTurn && RefusedMidTurn(field))
                {
                    Sink.Notice(NotWhileReplyRunsNotice);
                    continue;
                }

                if (pick.Button >= 0 ? ResetToDefault(field) : await EditAsync(field, saved, page, pick.Row, cancellationToken).ConfigureAwait(false))
                {
                    changes |= ChangesOf(field);
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
