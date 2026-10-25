using System.Globalization;
using NeonSidekick.DuckDb;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The DuckDB tab of <c>/tools</c> (2026-10-25), the SQLite tab's twin over <c>duckdb.json</c>: the mode pick, the statements and offered
/// checklists, the two edit rows, and <c>DuckDB add database</c> — which file, the name, the database file, the description, then a summary
/// that opens the draft read-only (how many tables; a SQLite file said so) and saves it. The SQL tab's labels and range wording serve where
/// the words are the same.
/// </summary>
internal sealed partial class SettingsMenu
{

    /// <summary>The value of the <c>DuckDB add database</c> row. Pinned.</summary>
    public const string DuckDbAddDatabaseLabel = "Enter to start database wizard";

    /// <summary>The value of an edit row over one <c>duckdb.json</c>: how many databases it names and how many entries it skips, or <c>(none)</c>.</summary>
    public static string DuckDbDatabasesLabel(string path)
    {
        var loaded = DuckDbConfigFile.Load(path);
        string count = loaded.Databases.Count == 0 ? "(none)" : Sql.SqlText.Count(loaded.Databases.Count, "database");
        return (loaded.Problems.Count == 0 ? count : count + ", " + Sql.SqlText.Count(loaded.Problems.Count, "problem")) + " · Enter edits duckdb.json";
    }

    /// <summary>The value of <c>DuckDB databases offered</c>: how many of the named databases the profile offers, <c>none of N</c> before any is ticked. Pinned.</summary>
    public static string DuckDbOfferedValue(IReadOnlyList<string>? offered, DuckDbCatalog loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        int kept = loaded.Offered(offered).Databases.Count;
        return (kept == 0 ? "none" : kept.ToString(CultureInfo.InvariantCulture)) + " of " + loaded.Databases.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One row of the <c>DuckDB databases offered</c> checklist: the mark, the name, the file. Pinned.</summary>
    public static string DuckDbOfferedRow(DuckDbNamedDatabase database, bool offered, int width)
    {
        ArgumentNullException.ThrowIfNull(database);
        return Markup.Escape((offered ? "[x] " : "[ ] ") + database.Name.PadRight(width)) + Theme.DimMarkup(DuckDbText.MentionNote(database));
    }

    /// <summary>The status line after an edit row opened a <c>duckdb.json</c>. Pinned.</summary>
    public static string DuckDbEditingNotice(string path) => $"Opened {path} in the editor; the DuckDB tools read it at their next call.";

    /// <summary>One row of the <c>DuckDB mode</c> pick: the name padded past <c>read-write</c>, then its hint, dim. Pinned.</summary>
    public static string DuckDbModeLabel(string name) => Markup.Escape(name.PadRight(12)) + Theme.DimMarkup(Markup.Escape(DuckDbModes.Describe(name)));

    /// <summary>The <c>DuckDB mode</c> pick: each mode with its hint, the saved one under the cursor; true when a pick changed it.</summary>
    private async Task<bool> PickDuckDbModeAsync(Settings.AppSettingsData saved, CancellationToken cancellationToken)
    {
        var values = DuckDbModes.Names;
        var rows = values.Select(DuckDbModeLabel).ToList();
        var page = new MenuPage(Crumb(FieldName(SettingsField.DuckDbMode)), rows, PickKeys);
        int at = Math.Max(0, Array.FindIndex(values, v => string.Equals(v, saved.DuckDbMode?.Trim(), StringComparison.OrdinalIgnoreCase)));
        int? picked = await PickAsync(page, at, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index || index >= values.Length)
        {
            return Unchanged();
        }

        string value = values[index];
        Apply(SettingsField.DuckDbMode, d => d.DuckDbMode = value);
        return true;
    }

    /// <summary>
    /// The value of <c>DuckDB statements allowed</c>: the ticked kinds by name while three or fewer (the default's three), else how many,
    /// <c>none</c> with none; while the mode is not read-write it says the list is unused till then. Pinned.
    /// </summary>
    public static string DuckDbStatementsValue(IReadOnlyList<string>? saved, string? mode)
    {
        var kinds = DuckDbStatementKinds.Resolve(saved);
        string list = kinds.Count == 0 ? "none"
            : kinds.Count <= 3 ? string.Join(", ", kinds.Select(DuckDbStatementKinds.Title))
            : kinds.Count.ToString(CultureInfo.InvariantCulture) + " of " + DuckDbStatementKinds.Names.Length.ToString(CultureInfo.InvariantCulture);
        return DuckDbModes.TryParse(mode, out var parsed) && parsed == DuckDbMode.ReadWrite ? list : list + " (used under read-write)";
    }

    /// <summary>One row of the <c>DuckDB statements allowed</c> checklist: the mark, the kind, its statements dim. Pinned.</summary>
    public static string DuckDbStatementRow(DuckDbStatementKind kind, bool on) =>
        Markup.Escape((on ? "[x] " : "[ ] ") + DuckDbStatementKinds.Title(kind).PadRight(20)) + Theme.DimMarkup(Markup.Escape(DuckDbStatementKinds.Statements(kind)));

    /// <summary>
    /// <c>DuckDB statements allowed</c>, SQLite's checklist over the eight kinds: Enter or Space flips one, A all, N none, D the default, until
    /// Esc. Saved as the kinds' words in menu order. True when anything changed.
    /// </summary>
    private async Task<bool> EditDuckDbStatementsAsync(CancellationToken cancellationToken)
    {
        var kinds = Enum.GetValues<DuckDbStatementKind>();
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var on = DuckDbStatementKinds.Resolve(_settings.Current.DuckDbStatementsAllowed).ToHashSet();
            var page = new MenuPage(Crumb(FieldName(SettingsField.DuckDbStatementsAllowed)), kinds.Select(k => DuckDbStatementRow(k, on.Contains(k))).ToList(), DefaultToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, kinds.Length - 1), cancellationToken, DefaultChecklistButtons).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            var next = pick.Button == SelectAllIndex ? kinds.ToList()
                : pick.Button == SelectNoneIndex ? []
                : pick.Button == DefaultsIndex ? DuckDbStatementKinds.Resolve(DuckDbStatementKinds.Default()).ToList()
                : kinds.Where(k => on.Contains(k) != (k == kinds[pick.Row])).ToList();
            if (next.Count == on.Count && next.All(on.Contains))
            {
                continue;
            }

            var words = next.Select(DuckDbStatementKinds.NameOf).ToList();
            Apply(SettingsField.DuckDbStatementsAllowed, d => d.DuckDbStatementsAllowed = words);
            changed = true;
        }
    }

    /// <summary><c>DuckDB databases offered</c>, SQLite's twin: every named database, ticked or not, Enter or Space flipping one until Esc. True when anything changed.</summary>
    private async Task<bool> EditDuckDbOfferedAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var loaded = DuckDbConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            if (loaded.Databases.Count == 0)
            {
                Sink.Error(DuckDbText.NoDatabases);
                return changed;
            }

            // A saved name no longer in the files is dropped as the list opens (SQLite's rule), unless a file could not be read or a problem
            // still names it (OfferedNames.StaleConnections).
            changed |= PruneStale(SettingsField.DuckDbDatabasesOffered, _settings.Current.DuckDbDatabasesOffered, OfferedNames.StaleConnections(_settings.Current.DuckDbDatabasesOffered, loaded.Databases.Select(d => d.Name), loaded.Problems), StringComparer.OrdinalIgnoreCase, (d, kept) => d.DuckDbDatabasesOffered = kept);

            var offered = _settings.Current.DuckDbDatabasesOffered;
            var on = loaded.Offered(offered).Databases.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var databases = ByName(loaded.Databases, d => d.Name);   // A to Z
            int width = databases.Max(d => d.Name.Length) + 2;
            var page = new MenuPage(Crumb(FieldName(SettingsField.DuckDbDatabasesOffered)), databases.Select(d => DuckDbOfferedRow(d, on.Contains(d.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, databases.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = databases[pick.Row].Name;
            var next = pick.Button == SelectAllIndex ? databases.Select(d => d.Name).ToList()
                : pick.Button == SelectNoneIndex ? []
                : databases.Select(d => d.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count == on.Count && next.All(on.Contains))
            {
                continue;
            }

            // What is saved but not listed now survived the prune above: a name a problem still names, or every name while a
            // file cannot be read. It stays until the file is mended, and the next opening decides.
            if (offered is not null)
            {
                next.AddRange(offered.Where(n => !loaded.Databases.Any(d => string.Equals(d.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
            }

            Apply(SettingsField.DuckDbDatabasesOffered, d => d.DuckDbDatabasesOffered = next);
            changed = true;
        }
    }

    /// <summary>Opens one <c>duckdb.json</c> in the editor, made first when missing; without an opener or on an IO failure, the status line says so.</summary>
    private void OpenDuckDbFile(string path)
    {
        try
        {
            DuckDbConfigFile.EnsureExists(path);
            if (_openFile is null)
            {
                Sink.Error(SqlEditFailedError(path, "no editor to open it in"));
                return;
            }

            _openFile(path);
            Sink.Notice(DuckDbEditingNotice(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(SqlEditFailedError(path, Diagnostics.LogText.Excerpt(ex.Message)));
        }
    }

    // ── DuckDB add/edit database ───────────────────────────────────────────

    /// <summary>The wizard's rows, one per <see cref="DuckDbWizardStep"/> before the summary. Pinned.</summary>
    public static readonly IReadOnlyList<string> DuckDbWizardLabels = ["File", "Name", "Database file", "Description"];

    public const string DuckDbWizardFileQuestion = "Which duckdb.json: this profile's or the global one?";
    public const string DuckDbWizardNameQuestion = "Its name: what the model passes as \"database\" and %name picks on the input line.";
    public static string DuckDbWizardPathQuestion => OperatingSystem.IsMacOS()   // a Mac's own example path
        ? "The database file: a full path (/Users/you/data/sales.duckdb), or one relative to duckdb.json's folder."
        : "The database file: a full path (D:\\data\\sales.duckdb), or one relative to duckdb.json's folder.";
    public const string DuckDbWizardDescriptionQuestion = "What the database holds, in your words (optional): the model reads it to pick one.";
    public const string DuckDbWizardSummaryCaption = "Check the database: Test opens it read-only without saving; Enter on a row below changes it.";
    public const string DuckDbWizardSaveHiddenRow = "Save, hidden from the model until you tick it in DuckDB databases offered";
    public const string DuckDbWizardTestRow = "Test the database";
    public const string DuckDbWizardPathRequired = "A file is required.";
    public const string DuckDbWizardCancelledNotice = "No database added.";

    public static string DuckDbWizardTestOk(string name, int tables) =>
        $"Opened '{name}' read-only: {Sql.SqlText.Count(tables, "table or view", "tables and views")}.";

    public static string DuckDbWizardAdded(string name, string path) => $"Added '{name}' to {path}.";

    public static string DuckDbWizardAddFailed(string name, string reason) => $"Could not add '{name}': {reason}.";

    public static string DuckDbWizardChanged(string name, string path) => $"Saved '{name}' in {path}.";   // the wizard's edit

    public static string DuckDbWizardChangeFailed(string name, string reason) => $"Could not save '{name}': {reason}.";

    internal enum DuckDbWizardStep
    {
        File,
        Name,
        Path,
        Description,
        Summary,
    }

    private sealed class DuckDbDraft
    {
        public bool Global { get; set; }

        public string Name { get; set; } = "";

        /// <summary>An edit's entry name in its file; null for a new database.</summary>
        public string? Original { get; init; }

        public DuckDbDatabaseConfig Config { get; init; } = new();
    }

    private string DuckDbWizardPath(bool global) =>
        global ? DuckDbConfigFile.GlobalPath(_settings.StorageDirectory) : DuckDbConfigFile.ProfilePath(_settings.ProfileDirectory);

    private string DuckDbWizardTitle => Crumb(FieldName(SettingsField.DuckDbAddDatabase));

    private string DuckDbWizardValue(DuckDbWizardStep step, DuckDbDraft draft) => step switch
    {
        DuckDbWizardStep.File => SqlWizardFileRow(draft.Global, DuckDbWizardPath(draft.Global)),
        DuckDbWizardStep.Name => draft.Name.Length == 0 ? SqlWizardUnset : draft.Name,
        DuckDbWizardStep.Path => string.IsNullOrWhiteSpace(draft.Config.Path) ? SqlWizardUnset : draft.Config.Path.Trim(),
        _ => string.IsNullOrWhiteSpace(draft.Config.Description) ? SqlWizardUnset : draft.Config.Description.Trim(),
    };

    private List<string> DuckDbWizardRows(DuckDbDraft draft)
    {
        int width = DuckDbWizardLabels.Max(l => l.Length) + 2;
        return DuckDbWizardLabels.Select((label, i) => Markup.Escape(label.PadRight(width) + DuckDbWizardValue((DuckDbWizardStep)i, draft))).ToList();
    }

    /// <summary>
    /// The visit (<c>DuckDB add/edit database</c>): the first page of the entries when there are any, then the wizard on its draft; Esc on its
    /// first page back to that list. True when a setting changed.
    /// </summary>
    private async Task<bool> AddDuckDbDatabaseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var entries = ConnectionWizardEntries(DuckDbWizardPath(false), DuckDbWizardPath(true), path => DuckDbConfigFile.Load(path).Databases, c => c.Name);
            var draft = new DuckDbDraft();
            if (entries.Count > 0)
            {
                if (await ConnectionWizardPickAsync(DuckDbWizardTitle, "database", entries.Select(e => (e.Named.Name, e.Global)).ToList(), DuckDbWizardPath, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    Sink.Notice(DuckDbWizardCancelledNotice);
                    return false;
                }

                if (picked >= 0)
                {
                    var (named, global) = entries[picked];
                    draft = new DuckDbDraft { Global = global, Name = named.Name, Original = named.Name, Config = CloneEntry(named.Config, DuckDbJsonContext.Default.DuckDbDatabaseConfig, _ => { }) };
                }
            }

            if (await RunDuckDbWizardAsync(draft, cancellationToken).ConfigureAwait(false) is { } changed)
            {
                return changed;
            }

            if (entries.Count == 0)
            {
                Sink.Notice(DuckDbWizardCancelledNotice);
                return false;
            }
        }
    }

    /// <summary>The wizard: its steps in order, Esc one back (before the first — the File page, or an edit's Name: null), a change from the summary back to it. True when a setting changed.</summary>
    private async Task<bool?> RunDuckDbWizardAsync(DuckDbDraft draft, CancellationToken cancellationToken)
    {
        var step = draft.Original is null ? DuckDbWizardStep.File : DuckDbWizardStep.Summary;   // an edit opens on its summary
        bool fromSummary = draft.Original is not null;
        while (true)
        {
            if (step == DuckDbWizardStep.Summary)
            {
                var (done, changed, edit) = await DuckDbWizardSummaryAsync(draft, cancellationToken).ConfigureAwait(false);
                if (done)
                {
                    return changed;
                }

                if (edit is null && draft.Original is not null)
                {
                    return null;   // an edit's Esc on its summary: back to the list
                }

                step = edit ?? DuckDbWizardStep.Description;
                fromSummary = edit is not null;
                continue;
            }

            if (await DuckDbWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false))
            {
                step = fromSummary ? (draft.Name.Length == 0 ? DuckDbWizardStep.Name : string.IsNullOrWhiteSpace(draft.Config.Path) ? DuckDbWizardStep.Path : DuckDbWizardStep.Summary) : step + 1;
                fromSummary = fromSummary && step != DuckDbWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = DuckDbWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            if (step == (draft.Original is null ? DuckDbWizardStep.File : DuckDbWizardStep.Name))
            {
                return null;
            }

            step--;
        }
    }

    private async Task<bool> DuckDbWizardStepAsync(DuckDbWizardStep step, DuckDbDraft draft, CancellationToken cancellationToken)
    {
        switch (step)
        {
            case DuckDbWizardStep.File:
            {
                var page = new MenuPage(DuckDbWizardTitle, new[] { SqlWizardFileRow(false, DuckDbWizardPath(false)), SqlWizardFileRow(true, DuckDbWizardPath(true)) }.Select(Markup.Escape).ToList(), PickKeys) { Caption = DuckDbWizardFileQuestion };
                if (!_pane.Enabled)
                {
                    Flow.Notice(DuckDbWizardFileQuestion);
                }

                if (await PickAsync(page, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case DuckDbWizardStep.Name:
                return await DuckDbWizardTypeAsync(step, draft, DuckDbWizardNameQuestion, draft.Name, allowEmpty: false, text =>
                {
                    string name = text.Trim();
                    if (name.Length == 0)
                    {
                        return SqlWizardNameRequired;
                    }

                    if (name.Any(char.IsWhiteSpace))
                    {
                        return SqlWizardNameSpaces;
                    }

                    string path = DuckDbWizardPath(draft.Global);
                    if (DuckDbConfigFile.Load(path).Named(name) is { } taken && !string.Equals(taken.Name, draft.Original, StringComparison.OrdinalIgnoreCase))
                    {
                        return SqlWizardNameTaken(name, path);
                    }

                    draft.Name = name;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case DuckDbWizardStep.Path:
                return await DuckDbWizardTypeAsync(step, draft, DuckDbWizardPathQuestion, draft.Config.Path ?? "", allowEmpty: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return DuckDbWizardPathRequired;
                    }

                    draft.Config.Path = text.Trim().Trim('"');
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            default:
                return await DuckDbWizardTypeAsync(step, draft, DuckDbWizardDescriptionQuestion, draft.Config.Description ?? "", allowEmpty: true, text =>
                {
                    draft.Config.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> DuckDbWizardTypeAsync(DuckDbWizardStep step, DuckDbDraft draft, string question, string initial, bool allowEmpty, Func<string, string?> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(DuckDbWizardTitle, DuckDbWizardRows(draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, (int)step, _input, initial, allowEmpty, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(DuckDbWizardLabels[(int)step] + " · " + question, EditKeys));
                result = await _input.ReadAsync(initial, remember: false, allowEmpty: allowEmpty, cancellationToken: cancellationToken, escapeCancels: true).ConfigureAwait(false);
            }

            if (result is not InputResult.Submitted submitted)
            {
                return false;
            }

            if (accept(submitted.Text) is not { } error)
            {
                return true;
            }

            Sink.Error(error);
            initial = submitted.Text;
        }
    }

    private async Task<(bool Done, bool Changed, DuckDbWizardStep? Edit)> DuckDbWizardSummaryAsync(DuckDbDraft draft, CancellationToken cancellationToken)
    {
        int cursor = ConnectionWizardStartCursor(draft.Original, _settings.Current.DuckDbDatabasesOffered);
        while (true)
        {
            string[] actions = [SqlWizardSaveOfferedRow, DuckDbWizardSaveHiddenRow, DuckDbWizardTestRow, SqlWizardCancelRow];
            var rows = actions.Select(Markup.Escape).Concat(DuckDbWizardRows(draft)).ToList();
            var page = new MenuPage(DuckDbWizardTitle, rows, PickKeys) { Caption = DuckDbWizardSummaryCaption };
            if (!_pane.Enabled)
            {
                Flow.Notice(DuckDbWizardSummaryCaption);
            }

            if (await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                return (false, false, null);
            }

            cursor = picked;
            if (picked >= actions.Length)
            {
                var edit = (DuckDbWizardStep)(picked - actions.Length);
                if (edit == DuckDbWizardStep.File && draft.Original is not null)
                {
                    continue;   // an edit's file is fixed
                }

                return (false, false, edit);
            }

            if (picked == 2)
            {
                await DuckDbWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == 3)
            {
                Sink.Notice(draft.Original is null ? DuckDbWizardCancelledNotice : ConnectionWizardUnchangedNotice);
                return (true, false, null);
            }

            if (DuckDbWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>The draft opened read-only, nothing written: how many tables and views, or why it did not open; a failure still lets it be saved.</summary>
    private async Task DuckDbWizardTestAsync(DuckDbDraft draft, CancellationToken cancellationToken)
    {
        if (draft.Config.Problem is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        var named = new DuckDbNamedDatabase(draft.Name.Length == 0 ? "draft" : draft.Name, draft.Config, DuckDbWizardPath(draft.Global));
        if (File.Exists(named.FullPath) && DuckDbAccess.Sniff(named.FullPath) is var kind && kind is DuckDbFileKind.Sqlite or DuckDbFileKind.Empty or DuckDbFileKind.Other)
        {
            Sink.Error(DuckDbText.ConnectFailed(named.Name, kind switch
            {
                DuckDbFileKind.Sqlite => DuckDbText.IsSqliteFile(named.FullPath),
                DuckDbFileKind.Empty => DuckDbText.EmptyFile(named.FullPath),
                _ => DuckDbText.NotADuckDbFile(named.FullPath),
            }));
            return;
        }

        var run = await Task.Run(() => DuckDbAccess.Run(new DuckDbTarget(named.Name, named.FullPath), [DuckDbPrepared.AsWritten(DuckDbCatalogQueries.Count)], [], 1, _settings.Current.DuckDbQueryTimeoutSeconds, null, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (run.Outcome != Sql.SqlOutcome.Ok)
        {
            Sink.Error(DuckDbText.Error(run));
            return;
        }

        int count = run.Grids is [{ Rows: [var row, ..] }, ..] && int.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        Sink.Notice(DuckDbWizardTestOk(named.Name, count));
    }

    /// <summary>Writes the draft (an add, or an edit's replace), then the offered list (<see cref="ConnectionOfferedAfterSave"/>). Null when nothing was written.</summary>
    private bool? DuckDbWizardSave(DuckDbDraft draft, bool offer)
    {
        Func<string, string, string> failed = draft.Original is null ? DuckDbWizardAddFailed : DuckDbWizardChangeFailed;
        if (draft.Name.Length == 0 || draft.Config.Problem is not null)
        {
            Sink.Error(failed(draft.Name, (draft.Name.Length == 0 ? DuckDbWizardLabels[1] : DuckDbWizardLabels[2]) + " is not set"));
            return null;
        }

        string path = DuckDbWizardPath(draft.Global);
        if ((draft.Original is { } original ? DuckDbConfigFile.ReplaceDatabase(path, original, draft.Name, draft.Config) : DuckDbConfigFile.AddDatabase(path, draft.Name, draft.Config)) is { } error)
        {
            Sink.Error(failed(draft.Name, error));
            return null;
        }

        Sink.Notice(draft.Original is null ? DuckDbWizardAdded(draft.Name, path) : DuckDbWizardChanged(draft.Name, path));
        return ApplyOfferedAfterSave(SettingsField.DuckDbDatabasesOffered, _settings.Current.DuckDbDatabasesOffered, draft.Original, draft.Name, offer, (d, next) => d.DuckDbDatabasesOffered = next);
    }
}
