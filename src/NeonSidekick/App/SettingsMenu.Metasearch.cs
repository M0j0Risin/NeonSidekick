using System.Globalization;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.Web;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>The built-in metasearch's two rows on the Web tab (2026-11-06): which engines it asks and the filter it asks them for.</summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The value of <c>Web metasearch engines</c>: <c>all</c>, <c>none</c>, the names while three or fewer are on, else how many. Pinned.</summary>
    public static string MetasearchEnginesValue(IReadOnlyList<string>? saved)
    {
        var on = BuiltInMetaSearch.EnginesOn(saved);
        int all = BuiltInMetaSearch.EngineNames.Count;
        return on.Count == all ? "all"
            : on.Count == 0 ? "none"
            : on.Count <= 3 ? string.Join(", ", on)
            : on.Count.ToString(CultureInfo.InvariantCulture) + " of " + all.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One row of the engines' checklist: the mark and the name. Pinned.</summary>
    public static string MetasearchEngineRow(string name, bool on) => Markup.Escape((on ? "[x] " : "[ ] ") + name);

    /// <summary>
    /// The engines' checklist (<see cref="EditWriteStatementsAsync"/>'s shape): Enter or Space flips one, A all, N none, D the default, until
    /// ESC. Unlike that list's, all of them saves null rather than five names, so an engine added later starts on. True when anything changed.
    /// </summary>
    private async Task<bool> EditMetasearchEnginesAsync(CancellationToken cancellationToken)
    {
        var names = BuiltInMetaSearch.EngineNames;
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var on = BuiltInMetaSearch.EnginesOn(_settings.Current.WebMetasearchEngines).ToHashSet(StringComparer.Ordinal);
            var page = new MenuPage(Crumb(FieldName(SettingsField.WebMetasearchEngines)), names.Select(n => MetasearchEngineRow(n, on.Contains(n))).ToList(), DefaultToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, names.Count - 1), cancellationToken, DefaultChecklistButtons).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            List<string> next = pick.Button == SelectAllIndex || pick.Button == DefaultsIndex ? [.. names]
                : pick.Button == SelectNoneIndex ? []
                : names.Where(n => on.Contains(n) != (n == names[pick.Row])).ToList();
            if (on.SetEquals(next))
            {
                continue;
            }

            List<string>? saved = next.Count == names.Count ? null : next;
            Apply(SettingsField.WebMetasearchEngines, d => d.WebMetasearchEngines = saved);
            changed = true;
        }
    }

    /// <summary>One row of the safe-search pick: the word padded past <c>moderate</c>, then its hint, dim. Pinned.</summary>
    public static string MetasearchSafeSearchLabel(string name) =>
        Markup.Escape(name.PadRight(10)) + Theme.DimMarkup(Markup.Escape(BuiltInMetaSearch.DescribeSafeSearch(name)));

    /// <summary>The safe-search pick (<see cref="PickSearchMethodAsync"/>'s shape): each word with its hint, the saved one under the cursor; true when a pick changed it.</summary>
    private async Task<bool> PickMetasearchSafeSearchAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = BuiltInMetaSearch.SafeSearchNames;
        var page = new MenuPage(Crumb(FieldName(SettingsField.WebMetasearchSafeSearch)), names.Select(MetasearchSafeSearchLabel).ToList(), PickKeys);
        int at = Math.Max(0, Array.FindIndex(names, n => string.Equals(n, saved.WebMetasearchSafeSearch?.Trim(), StringComparison.OrdinalIgnoreCase)));
        int? picked = await PickAsync(page, at, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.WebMetasearchSafeSearch, d => d.WebMetasearchSafeSearch = name);
        return true;
    }
}
