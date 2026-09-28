using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The <c>/sampling</c> pane (2026-09-28, the user's ask: temperature, top_p, top_k, min_p, presence_penalty and
/// repetition_penalty over the model's defaults, per model, with a free-form extra body — the user's calls): a tabbed
/// <see cref="MenuPane"/> in the <see cref="ToolsMenu"/> shape over <see cref="AppSettingsData.LlmSampling"/>. The first tab
/// is the connected model's (there even before it has an entry), then <see cref="SamplingText.AnyModelTabTitle"/> — the
/// <c>*</c> entry every model without a value of its own falls back to — then every other model with an entry, A to Z. Each
/// tab's rows are the seven fields (<see cref="SamplingField.All"/>), the extra body and <see cref="SamplingText.ClearRowName"/>;
/// a field shows its own value, the one it inherits from <c>*</c>, or <see cref="SamplingText.ServerDefault"/>. Enter edits
/// a row as typed text (blank clears it; a value out of range is refused, never clamped); the clear row asks first. An
/// entry left empty is removed. Nothing reconnects: every save is read at the next turn, so the pane opens mid-turn too.
/// <c>/sampling &lt;field&gt; &lt;value&gt;</c> sets the connected model's without the pane (<see cref="Quick"/>). Without the
/// pane the tabs print as plain lines.
/// </summary>
internal sealed class SamplingMenu
{
    private readonly AppSettings _settings;
    private readonly SettingsMenu _menu;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly InputLine _input;
    private readonly Func<LlmEndpoint?> _endpoint;
    private readonly Func<string?> _overriddenBy;
    private readonly Action _changed;
    private readonly Func<CancellationToken, Task<ServerSampling?>> _serverDefaults;

    /// <param name="settings">The store the edits write to.</param>
    /// <param name="menu">The settings menu, for its yes/no pane.</param>
    /// <param name="transcript">Where the lines outside the pane go: the screen's deferring sink, since the pane may open while a reply runs.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="input">The input line the typed edits run on.</param>
    /// <param name="endpoint">The connected endpoint, whose model the first tab and <see cref="Quick"/> are for; null while nothing is.</param>
    /// <param name="overriddenBy">The variable overriding the sampling for this launch, or null (<c>NEONSIDEKICK_LLM_SAMPLING</c>).</param>
    /// <param name="changed">Called after every save: the screen resolves the connected assistant's sampling again.</param>
    /// <param name="serverDefaults">What the connected server says it applies (<see cref="LlmSession.ServerSamplingAsync"/>), asked once each time the pane opens; null asks nothing.</param>
    public SamplingMenu(AppSettings settings, SettingsMenu menu, INoticeSink transcript, MenuPane pane, InputLine input, Func<LlmEndpoint?> endpoint, Func<string?> overriddenBy, Action changed, Func<CancellationToken, Task<ServerSampling?>>? serverDefaults = null)
    {
        _serverDefaults = serverDefaults ?? (_ => Task.FromResult<ServerSampling?>(null));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _menu = menu ?? throw new ArgumentNullException(nameof(menu));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _overriddenBy = overriddenBy ?? throw new ArgumentNullException(nameof(overriddenBy));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    /// <summary>Where a notice goes: the pane's status line while it is open, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    /// <summary>The row of the extra body on every tab, after the fields.</summary>
    public static int ExtraRow => SamplingField.All.Count;

    /// <summary>The clear row, last.</summary>
    public static int ClearRow => ExtraRow + 1;

    /// <summary>
    /// The tabs' keys in order: <paramref name="connectedModel"/> (as the map stores it, when it does), <c>*</c>, then every
    /// other key with an entry, ignoring case A to Z.
    /// </summary>
    public static IReadOnlyList<string> TabKeys(Dictionary<string, LlmSamplingEntry>? map, string? connectedModel)
    {
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(connectedModel) && connectedModel != LlmSampling.AnyModel)
        {
            keys.Add(LlmSampling.KeyFor(map, connectedModel) ?? connectedModel);
        }

        keys.Add(LlmSampling.KeyFor(map, LlmSampling.AnyModel) ?? LlmSampling.AnyModel);
        if (map is not null)
        {
            keys.AddRange(map.Keys.Where(k => !keys.Contains(k, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase));
        }

        return keys;
    }

    /// <summary>
    /// One tab's rows as markup: each field padded to <see cref="SamplingText.LabelWidth"/>, a value it sets in ink, an
    /// inherited one, the server's (<paramref name="server"/>: the connected model's tab alone) or a default dim.
    /// </summary>
    public static IReadOnlyList<string> Rows(Dictionary<string, LlmSamplingEntry>? map, string key, ServerSampling? server = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        bool anyTab = key == LlmSampling.AnyModel;
        var entry = Entry(map, key);
        var any = anyTab ? null : Entry(map, LlmSampling.AnyModel);
        var rows = new List<string>(ClearRow + 1);
        foreach (var field in SamplingField.All)
        {
            rows.Add(Row(field.Wire, SamplingText.FieldValue(field, entry, any, anyTab, server), own: field.Get(entry) is not null));
        }

        rows.Add(Row(SamplingText.ExtraRowName, SamplingText.ExtraValue(entry, any, anyTab), own: entry?.Extra is { Count: > 0 }));
        rows.Add(Theme.DimMarkup(SamplingText.ClearRowName));
        return rows;
    }

    private static string Row(string name, string value, bool own) =>
        Markup.Escape(name.PadRight(SamplingText.LabelWidth)) + (own ? Theme.ColorMarkup(Theme.Ink, value) : Theme.DimMarkup(value));

    /// <summary>
    /// The tabbed page over <paramref name="keys"/>, on <paramref name="tab"/>; every tab captioned
    /// <see cref="SamplingText.ClaudeApiCaption"/> while <paramref name="claudeApi"/>, else the connected model's tab (the
    /// first, when <paramref name="server"/> is known) captioned with where the server's values came from.
    /// </summary>
    public static MenuPage Page(Dictionary<string, LlmSamplingEntry>? map, IReadOnlyList<string> keys, int tab, bool claudeApi, ServerSampling? server = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var tabs = keys.Select((k, i) =>
        {
            var reported = i == 0 && k != LlmSampling.AnyModel ? server : null;
            string? caption = claudeApi ? SamplingText.ClaudeApiCaption : reported is null ? null : SamplingText.SourceCaption(reported);
            return new MenuTab(SamplingText.TabTitle(k), Rows(map, k, reported)) { Caption = caption };
        }).ToList();
        return MenuPage.Tabbed(SamplingText.Label, tabs, tab, SamplingText.Keys);
    }

    /// <summary>The tabs as plain lines, for a console without the pane: each tab's title as a heading, its rows indented; the connected model's with the server's values and their source.</summary>
    public static IEnumerable<string> Lines(Dictionary<string, LlmSamplingEntry>? map, string? connectedModel, ServerSampling? server = null)
    {
        var keys = TabKeys(map, connectedModel);
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            bool anyTab = key == LlmSampling.AnyModel;
            var reported = i == 0 && !anyTab ? server : null;
            var entry = Entry(map, key);
            var any = anyTab ? null : Entry(map, LlmSampling.AnyModel);
            yield return SamplingText.TabTitle(key);
            if (reported is not null)
            {
                yield return "  " + SamplingText.SourceCaption(reported);
            }

            foreach (var field in SamplingField.All)
            {
                yield return "  " + field.Wire.PadRight(SamplingText.LabelWidth) + SamplingText.FieldValue(field, entry, any, anyTab, reported);
            }

            yield return "  " + SamplingText.ExtraRowName.PadRight(SamplingText.LabelWidth) + SamplingText.ExtraValue(entry, any, anyTab);
        }
    }

    private static LlmSamplingEntry? Entry(Dictionary<string, LlmSamplingEntry>? map, string key) =>
        LlmSampling.KeyFor(map, key) is { } stored ? map![stored] : null;

    /// <summary>
    /// Changes <paramref name="key"/>'s entry of <paramref name="data"/> (made when absent, under the stored spelling when
    /// one differs only by case), then drops it when it says nothing, and the map when it is empty. The one mutation.
    /// </summary>
    public static void Mutate(AppSettingsData data, string key, Action<LlmSamplingEntry> change)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(change);
        var map = data.LlmSampling ??= new Dictionary<string, LlmSamplingEntry>(StringComparer.Ordinal);
        string stored = LlmSampling.KeyFor(map, key) ?? key;
        var entry = map.TryGetValue(stored, out var found) && found is not null ? found : new LlmSamplingEntry();
        change(entry);
        if (entry.IsEmpty)
        {
            map.Remove(stored);
        }
        else
        {
            map[stored] = entry;
        }

        if (map.Count == 0)
        {
            data.LlmSampling = null;
        }
    }

    /// <summary>One save, the screen told, the notice, and the override reminder while the variable wins.</summary>
    private void Save(string key, Action<LlmSamplingEntry> change, string notice)
    {
        _settings.Update(d => Mutate(d, key, change));
        _changed();
        Sink.Notice(notice);
        if (_overriddenBy() is { } overriddenBy)
        {
            Sink.Warning(SettingsMenu.OverrideNotice(overriddenBy));
        }
    }

    /// <summary>
    /// Sets or clears one field of <paramref name="key"/>'s entry from typed <paramref name="text"/>: blank clears it,
    /// else it must parse in range. False with the refusal on the sink otherwise; the saved value kept.
    /// </summary>
    public bool SetField(string key, SamplingField field, string text)
    {
        ArgumentNullException.ThrowIfNull(field);
        text = text.Trim();
        if (text.Length == 0 || string.Equals(text, SamplingText.ClearWord, StringComparison.OrdinalIgnoreCase))
        {
            Save(key, e => field.Set(e, null), SamplingText.ClearedNotice(key, field.Wire));
            return true;
        }

        if (!field.TryParse(text, out double value))
        {
            string keeping = field.Get(Entry(_settings.Current.LlmSampling, key)) is { } old ? SamplingField.Format(old) : SamplingText.ServerDefault;
            Sink.Error(SamplingText.RangeError(field, keeping));
            return false;
        }

        Save(key, e => field.Set(e, value), SamplingText.SavedNotice(key, field.Wire, SamplingField.Format(value)));
        return true;
    }

    /// <summary>Sets or clears <paramref name="key"/>'s extra body from typed JSON; blank (or <c>{}</c>) clears it.</summary>
    public bool SetExtra(string key, string text)
    {
        text = text.Trim();
        if (text.Length == 0 || string.Equals(text, SamplingText.ClearWord, StringComparison.OrdinalIgnoreCase))
        {
            Save(key, e => e.Extra = null, SamplingText.ClearedNotice(key, SamplingText.ExtraRowName));
            return true;
        }

        if (!LlmSampling.TryParseExtra(text, out var extra, out string problem))
        {
            string keeping = SamplingText.ExtraJson(Entry(_settings.Current.LlmSampling, key)?.Extra);
            Sink.Error(SamplingText.ExtraError(problem, keeping.Length > 0 ? keeping : "(none)"));
            return false;
        }

        Save(key, e => e.Extra = extra, extra is null
            ? SamplingText.ClearedNotice(key, SamplingText.ExtraRowName)
            : SamplingText.SavedNotice(key, SamplingText.ExtraRowName, SamplingText.ExtraJson(extra)));
        return true;
    }

    /// <summary>Removes <paramref name="key"/>'s whole entry.</summary>
    public void ClearAll(string key) => Save(key, e =>
    {
        foreach (var field in SamplingField.All)
        {
            field.Set(e, null);
        }

        e.Extra = null;
    }, SamplingText.ClearedAllNotice(key));

    /// <summary>
    /// <c>/sampling</c> with arguments, for the connected model: <c>&lt;field&gt; &lt;value&gt;</c>, <c>&lt;field&gt; clear</c>,
    /// <c>extra {…}</c>, <c>extra clear</c>, or <c>clear</c> alone for the whole entry. The field by its wire name
    /// (<see cref="SamplingField.ByWire"/>). Anything else is <see cref="SamplingText.UsageError"/>; no model connected,
    /// <see cref="SamplingText.NoModelError"/>. True when something was saved.
    /// </summary>
    public bool Quick(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (_endpoint()?.ModelId is not { Length: > 0 } model)
        {
            _transcript.Error(SamplingText.NoModelError);
            return false;
        }

        string trimmed = args.Trim();
        int split = trimmed.IndexOfAny([' ', '\t']);
        string word = split < 0 ? trimmed : trimmed[..split];
        string rest = split < 0 ? "" : trimmed[(split + 1)..].Trim();
        if (string.Equals(word, SamplingText.ClearWord, StringComparison.OrdinalIgnoreCase) && rest.Length == 0)
        {
            ClearAll(model);
            return true;
        }

        if (rest.Length == 0)
        {
            _transcript.Error(SamplingText.UsageError);
            return false;
        }

        if (string.Equals(word, SamplingText.ExtraWord, StringComparison.OrdinalIgnoreCase))
        {
            return SetExtra(model, rest);
        }

        if (SamplingField.ByWire(word) is not { } field)
        {
            _transcript.Error(SamplingText.UsageError);
            return false;
        }

        return SetField(model, field, rest);
    }

    /// <summary>The pane: the tabs until ESC, each Enter an edit of the row under the cursor.</summary>
    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        var endpoint = _endpoint();
        string? model = endpoint?.ModelId;
        var server = await _serverDefaults(cancellationToken).ConfigureAwait(false);
        if (!_pane.Enabled)
        {
            foreach (var line in Lines(_settings.Current.LlmSampling, model, server))
            {
                _transcript.Notice(line);
            }

            return;
        }

        bool claudeApi = endpoint is not null && ClaudeApi.IsClaudeApi(endpoint.BaseUrl);
        int tab = 0;
        int cursor = 0;
        try
        {
            while (true)
            {
                var map = _settings.Current.LlmSampling;
                var keys = TabKeys(map, model);
                tab = Math.Clamp(tab, 0, keys.Count - 1);
                var page = Page(map, keys, tab, claudeApi, server);
                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return;
                }

                tab = pick.Tab;
                cursor = pick.Row;
                string key = keys[tab];
                var shown = pick.Tab == page.Tab ? page : Page(map, keys, tab, claudeApi, server);
                if (cursor == ClearRow)
                {
                    if (Entry(map, key) is { IsEmpty: false } && await _menu.ConfirmAsync(SamplingText.ClearQuestion(key), cancellationToken).ConfigureAwait(false))
                    {
                        ClearAll(key);
                    }

                    continue;
                }

                string initial = cursor == ExtraRow
                    ? SamplingText.ExtraJson(Entry(map, key)?.Extra)
                    : SamplingField.All[cursor].Get(Entry(map, key)) is { } own ? SamplingField.Format(own) : "";
                var result = await _pane.EditAsync(shown with { Hint = SettingsMenu.EditKeys }, cursor, _input, initial, allowEmpty: true, cancellationToken).ConfigureAwait(false);
                if (result is not InputResult.Submitted submitted)
                {
                    continue;
                }

                if (cursor == ExtraRow)
                {
                    SetExtra(key, submitted.Text);
                }
                else
                {
                    SetField(key, SamplingField.All[cursor], submitted.Text);
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}
