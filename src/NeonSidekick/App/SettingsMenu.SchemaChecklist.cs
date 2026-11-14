using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The Oracle and PostgreSQL wizards' schema step (2026-11-14, feature idea #18, the user's pick: a checkbox list): the schemas the draft's
/// account can see, the saved ones ticked, a Continue row last; with two or more ticked, a one-choice page for which comes first (Oracle's
/// current schema, PostgreSQL's search order). Enter and Space toggle, as on every checklist; Enter on Continue goes on, Esc one back.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The schema checklist's hint: <see cref="ToggleKeys"/> with Continue. Pinned.</summary>
    public const string WizardSchemaKeys = "Enter / Space = on or off · a = all · n = none · Continue = next · Esc = back";

    /// <summary>The checklist's last row, which goes on to the next step.</summary>
    public const string WizardSchemaContinueRow = "Continue";

    /// <summary>A saved schema the server no longer lists, shown ticked so unticking it is the fix.</summary>
    public const string WizardSchemaMissing = "(missing)";

    /// <summary>Why the step asks for typed names instead of the list: the draft's account could not list them.</summary>
    public static string WizardSchemasUnlisted(string detail) => $"Could not list the schemas ({detail}); type them instead.";

    /// <summary>The typed answer's names: comma-separated, trimmed, blanks dropped.</summary>
    public static List<string> WizardSchemaNames(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>
    /// The checklist over <paramref name="available"/> (the server's order) with <paramref name="saved"/> ticked, a saved name the server
    /// lacks listed after them as <see cref="WizardSchemaMissing"/>. The ticked names when Continue is chosen — the saved ones in their
    /// saved order (a hand-edited file's order kept), then the newly ticked in the list's — or null for Esc.
    /// </summary>
    private async Task<List<string>?> WizardSchemaChecklistAsync(string title, string caption, IReadOnlyList<string> available, IReadOnlyList<string> saved, IEqualityComparer<string> compare, CancellationToken cancellationToken)
    {
        var missing = saved.Where(s => !available.Contains(s, compare)).ToList();
        var names = available.Concat(missing).ToList();
        var on = new HashSet<string>(saved, compare);
        int cursor = 0;
        while (true)
        {
            int width = names.Count == 0 ? 0 : names.Max(n => n.Length) + 2;
            var rows = names.Select(n => Markup.Escape((on.Contains(n) ? "[x] " : "[ ] ") + n.PadRight(width)) + (missing.Contains(n, compare) ? Theme.DimMarkup(WizardSchemaMissing) : "")).ToList();
            rows.Add(Markup.Escape(WizardSchemaContinueRow));
            var page = new MenuPage(title, rows, WizardSchemaKeys) { SpaceToggles = true, Caption = caption };
            if (!_pane.Enabled)
            {
                Flow.Notice(caption);
            }

            if (await PickChecklistAsync(page, Math.Min(cursor, rows.Count - 1), cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return null;
            }

            cursor = pick.Row;
            if (pick.Button == SelectAllIndex)
            {
                on.UnionWith(names);
            }
            else if (pick.Button == SelectNoneIndex)
            {
                on.Clear();
            }
            else if (pick.Row >= names.Count)
            {
                return saved.Where(on.Contains).Concat(names.Where(n => on.Contains(n) && !saved.Contains(n, compare))).ToList();
            }
            else if (!on.Remove(names[pick.Row]))
            {
                on.Add(names[pick.Row]);
            }
        }
    }

    /// <summary>
    /// The ticked schemas in their final order: one or none as they are; two or more through a one-choice page asking which comes first
    /// (<paramref name="question"/>), <paramref name="preferred"/> preselected when ticked, the rest after it in their order. Null for Esc.
    /// </summary>
    private async Task<List<string>?> WizardSchemaOrderAsync(string title, string question, List<string> ticked, string? preferred, IEqualityComparer<string> compare, CancellationToken cancellationToken)
    {
        if (ticked.Count < 2)
        {
            return ticked;
        }

        int start = preferred is null ? 0 : Math.Max(0, ticked.FindIndex(t => compare.Equals(t, preferred)));
        var page = new MenuPage(title, ticked.Select(Markup.Escape).ToList(), PickKeys) { Caption = question };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        if (await PickAsync(page, start, cancellationToken).ConfigureAwait(false) is not { } first)
        {
            return null;
        }

        return [ticked[first], .. ticked.Where((_, i) => i != first)];
    }

    /// <summary>
    /// The whole schema step over a listing: the checklist, then the order page (Esc there goes back to the checklist, its ticks kept). The
    /// final list, or null for Esc on the checklist.
    /// </summary>
    private async Task<List<string>?> WizardSchemasAsync(string title, string caption, string orderQuestion, IReadOnlyList<string> available, IReadOnlyList<string> saved, string? preferred, IEqualityComparer<string> compare, CancellationToken cancellationToken)
    {
        var current = saved;
        while (true)
        {
            if (await WizardSchemaChecklistAsync(title, caption, available, current, compare, cancellationToken).ConfigureAwait(false) is not { } ticked)
            {
                return null;
            }

            if (await WizardSchemaOrderAsync(title, orderQuestion, ticked, preferred ?? saved.FirstOrDefault(), compare, cancellationToken).ConfigureAwait(false) is { } ordered)
            {
                return ordered;
            }

            current = ticked;
        }
    }
}
