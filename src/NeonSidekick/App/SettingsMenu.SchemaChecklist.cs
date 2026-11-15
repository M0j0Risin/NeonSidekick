using System.Globalization;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The Oracle and PostgreSQL wizards' schema step (2026-11-14, feature idea #18, the user's pick: a checkbox list): the schemas the draft's
/// account can see, the saved ones ticked and first, a Continue row last. Since 2026-11-15 (the database-tools review's #11) one step for
/// both families, each bringing its listing, spelling and rules as a <see cref="SchemaFamily"/>. The checklist itself orders (#31/#33, the
/// user's pick of a full reorder): Shift+↑↓ and Shift+Home/End move a row as on <i>Toolbar order</i>, and the ticked rows top to bottom are
/// the saved order, the first Oracle's current schema or PostgreSQL's first on the search path. Without the pane there are no moves, so a
/// one-choice page asks which comes first, then which next. Enter and Space toggle; Enter on Continue goes on, Esc one back.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The schema checklist's hint: <see cref="ToggleKeys"/> with the move keys and Continue. Pinned.</summary>
    public const string WizardSchemaKeys = "Enter / Space = on or off · Shift+↑↓, Shift+Home/End = move · a = all · n = none · Continue = next · Esc = back";

    /// <summary>The checklist's last row, which goes on to the next step.</summary>
    public const string WizardSchemaContinueRow = "Continue";

    /// <summary>A saved schema the server no longer has, shown ticked so unticking it is the fix.</summary>
    public const string WizardSchemaMissing = "(missing)";

    /// <summary>A schema the connection can't be held to (#53: an Oracle account's own when Oracle maintains it), refused when ticked.</summary>
    public const string WizardSchemaNotAllowed = "(not allowed)";

    /// <summary>The order pages after the first, without the pane (#31/#33).</summary>
    public const string WizardSchemaNextQuestion = "Which comes next?";

    /// <summary>Why the step asks for typed names instead of the list: the draft's account could not list them.</summary>
    public static string WizardSchemasUnlisted(string detail) => $"Could not list the schemas ({detail}); type them instead.";

    /// <summary>The notice while the step lists the schemas (#99: a slow server showed nothing).</summary>
    public static string WizardSchemaListing(string where) => $"Listing the schemas on {where}…";

    /// <summary>The note when the listing stopped at its cap (#105/#146's twin): a saved name past it is looked up by name.</summary>
    public static string WizardSchemaListCut(int shown) =>
        $"Only the first {shown.ToString("N0", CultureInfo.InvariantCulture)} schemas are listed; a saved one past them is still found.";

    /// <summary>
    /// The typed answer's names: comma-separated, trimmed, blanks dropped. A comma inside double quotes is part of the name (#121: an Oracle
    /// name written <c>"a,b"</c> was split in two).
    /// </summary>
    public static List<string> WizardSchemaNames(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var names = new List<string>();
        var part = new StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }

            if (c == ',' && !quoted)
            {
                names.Add(part.ToString().Trim());
                part.Clear();
            }
            else
            {
                part.Append(c);
            }
        }

        names.Add(part.ToString().Trim());
        return names.Where(n => n.Length > 0).ToList();
    }

    /// <summary>
    /// What a family's listing found: the names as an entry writes them (Oracle's own first), whether the server had more than the cap, the
    /// saved names a lookup by name found past the cap (#146), or the <c>Error:</c> sentence of a listing that failed.
    /// </summary>
    internal sealed record SchemaListing(IReadOnlyList<string> Names, bool More, IReadOnlyList<string> Found, string? Error);

    /// <summary>
    /// A draft's last listing that succeeded, with the connection it was made over (#99): a step re-entered on the same connection reuses it,
    /// and a changed data source, host, database, user, password, TLS or timeout lists again, so it always matches what Save connects with.
    /// </summary>
    internal sealed class SchemaListCache
    {
        public string? Key { get; set; }

        public SchemaListing? Listing { get; set; }
    }

    /// <summary>
    /// A family's side of the schema step (#11). <paramref name="Saved"/> is the draft's lock as the entry writes it; <paramref name="Unlistable"/>
    /// the reason the draft can't be listed before trying (a field it lacks); <paramref name="Final"/> turns the chosen names into the lock
    /// (Oracle's own alone is no key); <paramref name="TypeAsync"/> asks the typed fallback (question, start text, check).
    /// </summary>
    private sealed record SchemaFamily(
        string Title,
        string Question,
        string OrderNote,
        string TypedQuestion,
        string FirstQuestion,
        string Where,
        string ListingKey,
        SchemaListCache Cache,
        IReadOnlyList<string> Saved,
        string? Unlistable,
        Func<CancellationToken, Task<SchemaListing>> ListAsync,
        Func<IReadOnlyList<string?>, string?> PickProblem,
        Func<int, string> TooMany,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> Final,
        Action<IReadOnlyList<string>> Hold,
        Func<string, string, Func<string, string?>, CancellationToken, Task<bool>> TypeAsync);

    /// <summary>
    /// The checklist's state, built once a step (#4/#20/#173): the rows in their order (the saved names first, de-duplicated, then the rest of
    /// the listing), the ticks, the saved names the server lacks, the names the lock refuses and why, and the cursor, kept across an Esc from
    /// the order pages.
    /// </summary>
    private sealed class WizardSchemaState
    {
        public List<string> Order { get; set; } = [];

        public HashSet<string> On { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Refused { get; } = new(StringComparer.Ordinal);

        public int Cursor { get; set; }

        public static WizardSchemaState From(SchemaFamily family, SchemaListing listing)
        {
            var state = new WizardSchemaState();
            var saved = family.Saved.Distinct(StringComparer.Ordinal).ToList();
            var known = new HashSet<string>(listing.Names.Concat(listing.Found), StringComparer.Ordinal);
            state.Order = [.. saved, .. listing.Names.Where(n => !saved.Contains(n, StringComparer.Ordinal))];
            state.On.UnionWith(saved);
            state.Missing.UnionWith(saved.Where(s => !known.Contains(s)));
            foreach (string name in state.Order.Where(n => !state.Missing.Contains(n)))
            {
                if (family.PickProblem([name]) is { } problem)
                {
                    state.Refused[name] = problem;
                }
            }

            return state;
        }
    }

    /// <summary>
    /// The whole schema step: the listing (or the cached one), then the checklist, then — without the pane, two or more ticked — the order
    /// pages (Esc there goes back to the checklist, its ticks kept); typed, comma-separated, when the schemas can't be listed. The lock is
    /// held through <see cref="SchemaFamily.Hold"/>. True once answered, false for Esc.
    /// </summary>
    private async Task<bool> WizardSchemaStepAsync(SchemaFamily family, CancellationToken cancellationToken)
    {
        string? unlisted = family.Unlistable;
        var listing = family.Cache.Key == family.ListingKey ? family.Cache.Listing : null;
        if (unlisted is null && listing is null)
        {
            Sink.Notice(WizardSchemaListing(family.Where));
            listing = await family.ListAsync(cancellationToken).ConfigureAwait(false);
            (family.Cache.Key, family.Cache.Listing) = listing.Error is null ? (family.ListingKey, listing) : (null, null);
            unlisted = listing.Error;
        }

        if (unlisted is not null || listing is null)
        {
            Sink.Notice(WizardSchemasUnlisted(unlisted ?? ""));
            return await family.TypeAsync(family.TypedQuestion, string.Join(", ", family.Saved), text =>
            {
                var names = WizardSchemaNames(text);
                if (family.PickProblem(names) is { } problem)
                {
                    return problem;
                }

                family.Hold(family.Final(names));
                return null;
            }, cancellationToken).ConfigureAwait(false);
        }

        if (listing.More)
        {
            Sink.Notice(WizardSchemaListCut(listing.Names.Count));
        }

        var state = WizardSchemaState.From(family, listing);
        string caption = _pane.Enabled ? family.Question + " " + family.OrderNote : family.Question;
        if (!_pane.Enabled)
        {
            Flow.Notice(caption);   // once a step (#174), not once a toggle
        }

        while (true)
        {
            if (await WizardSchemaChecklistAsync(family.Title, caption, state, cancellationToken).ConfigureAwait(false) is not { } ticked)
            {
                return false;
            }

            if (ticked.Count > Sql.SchemaLock.MaxSchemas)
            {
                Sink.Error(family.TooMany(Sql.SchemaLock.MaxSchemas));
                continue;
            }

            if (family.PickProblem(ticked) is { } problem)
            {
                Sink.Error(problem);   // a saved name the lock refuses, still ticked
                continue;
            }

            var ordered = _pane.Enabled ? ticked : await WizardSchemaOrderAsync(family, ticked, cancellationToken).ConfigureAwait(false);
            if (ordered is null)
            {
                continue;
            }

            family.Hold(family.Final(ordered));
            return true;
        }
    }

    /// <summary>
    /// The checklist over <paramref name="state"/>'s rows: a saved name the server lacks marked <see cref="WizardSchemaMissing"/>, one the lock
    /// refuses <see cref="WizardSchemaNotAllowed"/> (and refused when ticked, #53). <c>a</c> ticks every listed name the lock allows (#36). A
    /// move reorders the name rows, the cursor following. The ticked names top to bottom when Continue is chosen, or null for Esc.
    /// </summary>
    private async Task<List<string>?> WizardSchemaChecklistAsync(string title, string caption, WizardSchemaState state, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Names are read whole (#147) and scrubbed only here, for the screen; padded by cells (#58), so a wide character lines up.
            var shown = state.Order.Select(n => NameScrub.Display(n)).ToList();
            int width = shown.Count == 0 ? 0 : shown.Max(TextCells.Width) + 2;
            var rows = state.Order.Select((n, i) => Markup.Escape((state.On.Contains(n) ? "[x] " : "[ ] ") + shown[i] + new string(' ', width - TextCells.Width(shown[i])))
                + (state.Missing.Contains(n) ? Theme.DimMarkup(WizardSchemaMissing) : state.Refused.ContainsKey(n) ? Theme.DimMarkup(WizardSchemaNotAllowed) : "")).ToList();
            rows.Add(Markup.Escape(WizardSchemaContinueRow));
            var page = new MenuPage(title, rows, WizardSchemaKeys) { SpaceToggles = true, Caption = caption };
            if (await PickOrderedChecklistAsync(page, Math.Min(state.Cursor, rows.Count - 1), cancellationToken, ChecklistButtons).ConfigureAwait(false) is not { } pick)
            {
                return null;
            }

            state.Cursor = pick.Row;
            bool onName = pick.Row < state.Order.Count;
            if (pick.Move != 0)
            {
                if (onName)
                {
                    (state.Order, state.Cursor) = ItemOrder.Move(state.Order, pick.Row, pick.Move);
                }

                continue;
            }

            if (pick.Remove)
            {
                continue;   // nothing to remove: unticking is the way off
            }

            if (pick.Button == SelectAllIndex)
            {
                state.On.UnionWith(state.Order.Where(n => !state.Missing.Contains(n) && !state.Refused.ContainsKey(n)));
            }
            else if (pick.Button == SelectNoneIndex)
            {
                state.On.Clear();
            }
            else if (!onName)
            {
                return state.Order.Where(state.On.Contains).ToList();
            }
            else if (state.On.Remove(state.Order[pick.Row]))
            {
                // unticked
            }
            else if (state.Refused.TryGetValue(state.Order[pick.Row], out string? why))
            {
                Sink.Notice(why);
            }
            else
            {
                state.On.Add(state.Order[pick.Row]);
            }
        }
    }

    /// <summary>
    /// The ticked schemas in order without the pane (#31/#33): one-choice pages, the first asking <see cref="SchemaFamily.FirstQuestion"/> and
    /// the rest <see cref="WizardSchemaNextQuestion"/>, each starting on the next in the checklist's order (the saved one first), until one is
    /// left. One or none ticked is the order as it is. Null for Esc.
    /// </summary>
    private async Task<List<string>?> WizardSchemaOrderAsync(SchemaFamily family, List<string> ticked, CancellationToken cancellationToken)
    {
        var left = new List<string>(ticked);
        var ordered = new List<string>();
        while (left.Count > 1)
        {
            string question = ordered.Count == 0 ? family.FirstQuestion : WizardSchemaNextQuestion;
            Flow.Notice(question);
            var page = new MenuPage(family.Title, left.Select(n => Markup.Escape(NameScrub.Display(n))).ToList(), PickKeys) { Caption = question };
            if (await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                return null;
            }

            ordered.Add(left[picked]);
            left.RemoveAt(picked);
        }

        return [.. ordered, .. left];
    }
}
