using System.Globalization;
using NeonSidekick.Sqlite;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The SQLite tab of <c>/tools</c> (2026-10-04), the MySQL tab's rows over <c>sqlite.json</c>: the default pick, the offered
/// checklist, the two edit rows, and <c>SQLite add database</c> — the MySQL wizard's shape cut to what a file needs (no host, no
/// password): which file, the name, the database file, the description, then a summary that opens the draft read-only (how many
/// tables) and saves it. The SQL tab's labels and range wording serve where the words are the same.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>How the menu shows an empty <c>SQLite default database</c>. Pinned.</summary>
    public const string FirstSqliteDatabaseLabel = "(the first database)";

    /// <summary>The value of the <c>SQLite add database</c> row. Pinned.</summary>
    public const string SqliteAddDatabaseLabel = "Enter to start database wizard";

    /// <summary>The value of an edit row over one <c>sqlite.json</c>: how many databases it names and how many entries it skips, or <c>(none)</c>.</summary>
    public static string SqliteDatabasesLabel(string path)
    {
        var loaded = SqliteConfigFile.Load(path);
        string count = loaded.Databases.Count == 0 ? "(none)" : Sql.SqlText.Count(loaded.Databases.Count, "database");
        return (loaded.Problems.Count == 0 ? count : count + ", " + Sql.SqlText.Count(loaded.Problems.Count, "problem")) + " · Enter edits sqlite.json";
    }

    /// <summary>The value of <c>SQLite databases offered</c>: how many of the named databases the profile offers, <c>none of N</c> before any is ticked. Pinned.</summary>
    public static string SqliteOfferedValue(IReadOnlyList<string>? offered, SqliteCatalog loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        int kept = loaded.Offered(offered).Databases.Count;
        return (kept == 0 ? "none" : kept.ToString(CultureInfo.InvariantCulture)) + " of " + loaded.Databases.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One row of the <c>SQLite databases offered</c> checklist: the mark, the name, the file. Pinned.</summary>
    public static string SqliteOfferedRow(SqliteNamedDatabase database, bool offered, int width)
    {
        ArgumentNullException.ThrowIfNull(database);
        return Markup.Escape((offered ? "[x] " : "[ ] ") + database.Name.PadRight(width)) + Theme.DimMarkup(SqliteText.MentionNote(database));
    }

    /// <summary>The status line after an edit row opened a <c>sqlite.json</c>. Pinned.</summary>
    public static string SqliteEditingNotice(string path) => $"Opened {path} in the editor; the SQLite tools read it at their next call.";

    /// <summary>The <c>SQLite default database</c> pick: <see cref="FirstSqliteDatabaseLabel"/>, then every offered database, the cursor on the one saved.</summary>
    private async Task<bool> PickSqliteDatabaseAsync(Settings.AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = SqliteConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(saved.SqliteDatabasesOffered).Databases.Select(d => d.Name).ToList();
        var rows = new List<string> { Markup.Escape(FirstSqliteDatabaseLabel) };
        rows.AddRange(names.Select(Markup.Escape));
        int current = names.FindIndex(n => string.Equals(n, saved.SqliteDefaultDatabase, StringComparison.OrdinalIgnoreCase));
        var page = new MenuPage(Crumb(FieldName(SettingsField.SqliteDefaultDatabase)), rows, PickKeys);
        int? picked = await PickAsync(page, current + 1, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = index == 0 ? "" : names[index - 1];
        Apply(SettingsField.SqliteDefaultDatabase, d => d.SqliteDefaultDatabase = name);
        return true;
    }

    /// <summary><c>SQLite databases offered</c>, <see cref="EditMySqlOfferedAsync"/>'s twin: every named database, ticked or not, Enter or Space flipping one until ESC. True when anything changed.</summary>
    private async Task<bool> EditSqliteOfferedAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var loaded = SqliteConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            if (loaded.Databases.Count == 0)
            {
                Sink.Error(SqliteText.NoDatabases);
                return changed;
            }

            // A saved name no longer in the files is dropped as the list opens (2026-10-04, the user's call; it was carried along
            // unseen until then), unless a file could not be read or a problem still names it (OfferedNames.StaleConnections).
            changed |= PruneStale(SettingsField.SqliteDatabasesOffered, _settings.Current.SqliteDatabasesOffered, OfferedNames.StaleConnections(_settings.Current.SqliteDatabasesOffered, loaded.Databases.Select(d => d.Name), loaded.Problems), StringComparer.OrdinalIgnoreCase, (d, kept) => d.SqliteDatabasesOffered = kept);

            var offered = _settings.Current.SqliteDatabasesOffered;
            var on = loaded.Offered(offered).Databases.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int width = loaded.Databases.Max(d => d.Name.Length) + 2;
            var page = new MenuPage(Crumb(FieldName(SettingsField.SqliteDatabasesOffered)), loaded.Databases.Select(d => SqliteOfferedRow(d, on.Contains(d.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, loaded.Databases.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = loaded.Databases[pick.Row].Name;
            var next = pick.Button == SelectAllIndex ? loaded.Databases.Select(d => d.Name).ToList()
                : pick.Button == SelectNoneIndex ? []
                : loaded.Databases.Select(d => d.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count == on.Count && next.All(on.Contains))
            {
                continue;
            }

            // What is saved but not listed now survived the prune above: a name a problem still names, or every name while a
            // file cannot be read. It stays until the file is mended, and the next opening decides (2026-10-04).
            if (offered is not null)
            {
                next.AddRange(offered.Where(n => !loaded.Databases.Any(d => string.Equals(d.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
            }

            Apply(SettingsField.SqliteDatabasesOffered, d => d.SqliteDatabasesOffered = next);
            changed = true;
        }
    }

    /// <summary>Opens one <c>sqlite.json</c> in the editor, made first when missing; without an opener or on an IO failure, the status line says so.</summary>
    private void OpenSqliteFile(string path)
    {
        try
        {
            SqliteConfigFile.EnsureExists(path);
            if (_openFile is null)
            {
                Sink.Error(SqlEditFailedError(path, "no editor to open it in"));
                return;
            }

            _openFile(path);
            Sink.Notice(SqliteEditingNotice(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(SqlEditFailedError(path, Diagnostics.LogText.Excerpt(ex.Message)));
        }
    }

    // ── SQLite add database ────────────────────────────────────────────────

    /// <summary>The wizard's rows, one per <see cref="SqliteWizardStep"/> before the summary. Pinned.</summary>
    public static readonly IReadOnlyList<string> SqliteWizardLabels = ["File", "Name", "Database file", "Description"];

    public const string SqliteWizardFileQuestion = "Scope for sqlite.json?";
    public const string SqliteWizardNameQuestion = "Its name: what the model passes as \"database\" and %name picks on the input line.";
    public const string SqliteWizardPathQuestion = "The database file: a full path (D:\\data\\app.db), or one relative to sqlite.json's folder.";
    public const string SqliteWizardDescriptionQuestion = "What the database holds, in your words (optional): the model reads it to pick one.";
    public const string SqliteWizardSummaryCaption = "Check the database: Test opens it read-only without saving; Enter on a row below changes it.";
    public const string SqliteWizardSaveHiddenRow = "Save, hidden from the model until ticked in SQLite databases offered";
    public const string SqliteWizardTestRow = "Test the database";
    public const string SqliteWizardPathRequired = "A file is required.";
    public const string SqliteWizardCancelledNotice = "No database added.";

    public static string SqliteWizardTestOk(string name, int tables) =>
        $"Opened '{name}' read-only: {Sql.SqlText.Count(tables, "table or view", "tables and views")}.";

    public static string SqliteWizardAdded(string name, string path) => $"Added '{name}' to {path}.";

    public static string SqliteWizardAddFailed(string name, string reason) => $"Could not add '{name}': {reason}.";

    internal enum SqliteWizardStep
    {
        File,
        Name,
        Path,
        Description,
        Summary,
    }

    private sealed class SqliteDraft
    {
        public bool Global { get; set; }

        public string Name { get; set; } = "";

        public SqliteDatabaseConfig Config { get; } = new();
    }

    private string SqliteWizardPath(bool global) =>
        global ? SqliteConfigFile.GlobalPath(_settings.StorageDirectory) : SqliteConfigFile.ProfilePath(_settings.ProfileDirectory);

    private string SqliteWizardTitle => Crumb(FieldName(SettingsField.SqliteAddDatabase));

    private string SqliteWizardValue(SqliteWizardStep step, SqliteDraft draft) => step switch
    {
        SqliteWizardStep.File => SqlWizardFileRow(draft.Global, SqliteWizardPath(draft.Global)),
        SqliteWizardStep.Name => draft.Name.Length == 0 ? SqlWizardUnset : draft.Name,
        SqliteWizardStep.Path => string.IsNullOrWhiteSpace(draft.Config.Path) ? SqlWizardUnset : draft.Config.Path.Trim(),
        _ => string.IsNullOrWhiteSpace(draft.Config.Description) ? SqlWizardUnset : draft.Config.Description.Trim(),
    };

    private List<string> SqliteWizardRows(SqliteDraft draft)
    {
        int width = SqliteWizardLabels.Max(l => l.Length) + 2;
        return SqliteWizardLabels.Select((label, i) => Markup.Escape(label.PadRight(width) + SqliteWizardValue((SqliteWizardStep)i, draft))).ToList();
    }

    /// <summary>The wizard: its steps in order, ESC one back (before the first: nothing written), a change from the summary back to it. True when a setting changed.</summary>
    private async Task<bool> AddSqliteDatabaseAsync(CancellationToken cancellationToken)
    {
        var draft = new SqliteDraft();
        var step = SqliteWizardStep.File;
        bool fromSummary = false;
        while (true)
        {
            if (step == SqliteWizardStep.Summary)
            {
                var (done, changed, edit) = await SqliteWizardSummaryAsync(draft, cancellationToken).ConfigureAwait(false);
                if (done)
                {
                    return changed;
                }

                step = edit ?? SqliteWizardStep.Description;
                fromSummary = edit is not null;
                continue;
            }

            if (await SqliteWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false))
            {
                step = fromSummary ? (draft.Name.Length == 0 ? SqliteWizardStep.Name : string.IsNullOrWhiteSpace(draft.Config.Path) ? SqliteWizardStep.Path : SqliteWizardStep.Summary) : step + 1;
                fromSummary = fromSummary && step != SqliteWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = SqliteWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            if (step == SqliteWizardStep.File)
            {
                Sink.Notice(SqliteWizardCancelledNotice);
                return false;
            }

            step--;
        }
    }

    private async Task<bool> SqliteWizardStepAsync(SqliteWizardStep step, SqliteDraft draft, CancellationToken cancellationToken)
    {
        switch (step)
        {
            case SqliteWizardStep.File:
            {
                var page = new MenuPage(SqliteWizardTitle, new[] { SqlWizardFileRow(false, SqliteWizardPath(false)), SqlWizardFileRow(true, SqliteWizardPath(true)) }.Select(Markup.Escape).ToList(), PickKeys) { Caption = SqliteWizardFileQuestion };
                if (!_pane.Enabled)
                {
                    Flow.Notice(SqliteWizardFileQuestion);
                }

                if (await PickAsync(page, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case SqliteWizardStep.Name:
                return await SqliteWizardTypeAsync(step, draft, SqliteWizardNameQuestion, draft.Name, allowEmpty: false, text =>
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

                    string path = SqliteWizardPath(draft.Global);
                    if (SqliteConfigFile.Load(path).Named(name) is not null)
                    {
                        return SqlWizardNameTaken(name, path);
                    }

                    draft.Name = name;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case SqliteWizardStep.Path:
                return await SqliteWizardTypeAsync(step, draft, SqliteWizardPathQuestion, draft.Config.Path ?? "", allowEmpty: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return SqliteWizardPathRequired;
                    }

                    draft.Config.Path = text.Trim().Trim('"');
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            default:
                return await SqliteWizardTypeAsync(step, draft, SqliteWizardDescriptionQuestion, draft.Config.Description ?? "", allowEmpty: true, text =>
                {
                    draft.Config.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> SqliteWizardTypeAsync(SqliteWizardStep step, SqliteDraft draft, string question, string initial, bool allowEmpty, Func<string, string?> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(SqliteWizardTitle, SqliteWizardRows(draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, (int)step, _input, initial, allowEmpty, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(SqliteWizardLabels[(int)step] + " · " + question, EditKeys));
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

    private async Task<(bool Done, bool Changed, SqliteWizardStep? Edit)> SqliteWizardSummaryAsync(SqliteDraft draft, CancellationToken cancellationToken)
    {
        int cursor = 0;
        while (true)
        {
            string[] actions = [SqlWizardSaveOfferedRow, SqliteWizardSaveHiddenRow, SqliteWizardTestRow, SqlWizardCancelRow];
            var rows = actions.Select(Markup.Escape).Concat(SqliteWizardRows(draft)).ToList();
            var page = new MenuPage(SqliteWizardTitle, rows, PickKeys) { Caption = SqliteWizardSummaryCaption };
            if (!_pane.Enabled)
            {
                Flow.Notice(SqliteWizardSummaryCaption);
            }

            if (await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                return (false, false, null);
            }

            cursor = picked;
            if (picked >= actions.Length)
            {
                return (false, false, (SqliteWizardStep)(picked - actions.Length));
            }

            if (picked == 2)
            {
                await SqliteWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == 3)
            {
                Sink.Notice(SqliteWizardCancelledNotice);
                return (true, false, null);
            }

            if (SqliteWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>The draft opened read-only, nothing written: how many tables and views, or why it did not open; a failure still lets it be saved.</summary>
    private async Task SqliteWizardTestAsync(SqliteDraft draft, CancellationToken cancellationToken)
    {
        if (draft.Config.Problem is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        var named = new SqliteNamedDatabase(draft.Name.Length == 0 ? "draft" : draft.Name, draft.Config, SqliteWizardPath(draft.Global));
        var run = await Task.Run(() => SqliteAccess.Run(new SqliteTarget(named.Name, named.FullPath), [SqliteCatalogQueries.Count], [], 1, _settings.Current.SqliteQueryTimeoutSeconds, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (run.Outcome != Sql.SqlOutcome.Ok)
        {
            Sink.Error(SqliteText.Error(run));
            return;
        }

        int count = run.Grids is [{ Rows: [var row, ..] }, ..] && int.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        Sink.Notice(SqliteWizardTestOk(named.Name, count));
    }

    /// <summary>Writes the draft, then — when <paramref name="offer"/> — its name into the offered list. Null when nothing was written.</summary>
    private bool? SqliteWizardSave(SqliteDraft draft, bool offer)
    {
        if (draft.Name.Length == 0 || draft.Config.Problem is not null)
        {
            Sink.Error(SqliteWizardAddFailed(draft.Name, (draft.Name.Length == 0 ? SqliteWizardLabels[1] : SqliteWizardLabels[2]) + " is not set"));
            return null;
        }

        string path = SqliteWizardPath(draft.Global);
        if (SqliteConfigFile.AddDatabase(path, draft.Name, draft.Config) is { } error)
        {
            Sink.Error(SqliteWizardAddFailed(draft.Name, error));
            return null;
        }

        Sink.Notice(SqliteWizardAdded(draft.Name, path));
        if (!offer)
        {
            return false;
        }

        var offered = _settings.Current.SqliteDatabasesOffered ?? [];
        Apply(SettingsField.SqliteDatabasesOffered, d => d.SqliteDatabasesOffered = [.. offered, draft.Name]);
        return true;
    }
}
