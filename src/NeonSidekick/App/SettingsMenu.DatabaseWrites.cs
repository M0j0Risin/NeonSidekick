using System.Globalization;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The server families' write rows (2026-10-05, the user's ask: the SQLite tab's <c>SQLite mode</c> and <c>SQLite statements
/// allowed</c> mirrored on the SQL, Oracle, MySQL and PostgreSQL tabs): the mode pick and the statements checklist, one shape for the
/// four through <see cref="ServerWriteFamily"/>, each tab's two rows right after its tools switch.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>One row of a family's mode pick: the name padded past <c>read-write</c>, then its hint, dim. Pinned.</summary>
    public static string WriteModeLabel(string name, ServerWriteFamily family) =>
        Markup.Escape(name.PadRight(12)) + Theme.DimMarkup(Markup.Escape(DatabaseWriteModes.Describe(name, family)));

    /// <summary>A family's mode pick (<see cref="PickSqliteModeAsync"/>'s shape): each mode with its hint, the saved one under the cursor; true when a pick changed it.</summary>
    private async Task<bool> PickWriteModeAsync(SettingsField field, ServerWriteFamily family, string? saved, Action<AppSettingsData, string> set, CancellationToken cancellationToken)
    {
        var values = DatabaseWriteModes.Names;
        var rows = values.Select(v => WriteModeLabel(v, family)).ToList();
        var page = new MenuPage(Crumb(FieldName(field)), rows, PickKeys);
        int at = Math.Max(0, Array.FindIndex(values, v => string.Equals(v, saved?.Trim(), StringComparison.OrdinalIgnoreCase)));
        int? picked = await PickAsync(page, at, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index || index >= values.Length)
        {
            return Unchanged();
        }

        string value = values[index];
        Apply(field, d => set(d, value));
        return true;
    }

    /// <summary>
    /// The value of a family's <c>statements allowed</c> (<see cref="SqliteStatementsValue"/>'s shape): the ticked kinds by name while
    /// three or fewer, else how many, <c>none</c> with none; while the mode is not read-write it says the list is unused till then. Pinned.
    /// </summary>
    public static string WriteStatementsValue(IReadOnlyList<string>? saved, string? mode)
    {
        var kinds = ServerStatementKinds.Resolve(saved);
        string list = kinds.Count == 0 ? "none"
            : kinds.Count <= 3 ? string.Join(", ", kinds.Select(ServerStatementKinds.Title))
            : kinds.Count.ToString(CultureInfo.InvariantCulture) + " of " + ServerStatementKinds.Names.Length.ToString(CultureInfo.InvariantCulture);
        return DatabaseWriteModes.IsReadWrite(mode) ? list : list + " (used under read-write)";
    }

    /// <summary>One row of a family's statements checklist: the mark, the kind, its statements in the family's dialect, dim. Pinned.</summary>
    public static string WriteStatementRow(ServerStatementKind kind, ServerWriteFamily family, bool on) =>
        Markup.Escape((on ? "[x] " : "[ ] ") + ServerStatementKinds.Title(kind).PadRight(25)) + Theme.DimMarkup(Markup.Escape(family.Statements(kind)));

    /// <summary>
    /// A family's statements checklist (<see cref="EditSqliteStatementsAsync"/>'s shape) over the eight kinds: Enter or Space flips one,
    /// A all, N none, D the default, until ESC. Saved as the kinds' words in menu order. True when anything changed.
    /// </summary>
    private async Task<bool> EditWriteStatementsAsync(SettingsField field, ServerWriteFamily family, Func<AppSettingsData, IReadOnlyList<string>?> get, Action<AppSettingsData, List<string>> set, CancellationToken cancellationToken)
    {
        var kinds = Enum.GetValues<ServerStatementKind>();
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var on = ServerStatementKinds.Resolve(get(_settings.Current)).ToHashSet();
            var page = new MenuPage(Crumb(FieldName(field)), kinds.Select(k => WriteStatementRow(k, family, on.Contains(k))).ToList(), DefaultToggleKeys) { SpaceToggles = true };
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
                : pick.Button == DefaultsIndex ? ServerStatementKinds.Resolve(ServerStatementKinds.Default()).ToList()
                : kinds.Where(k => on.Contains(k) != (k == kinds[pick.Row])).ToList();
            if (next.Count == on.Count && next.All(on.Contains))
            {
                continue;
            }

            var words = next.Select(ServerStatementKinds.NameOf).ToList();
            Apply(field, d => set(d, words));
            changed = true;
        }
    }

    /// <summary>The wizards' access question (2026-10-05). Pinned.</summary>
    public const string DatabaseWizardAccessQuestion = "What the model may do through this connection.";

    /// <summary>The wizards' access picks, <c>read</c> then <c>readwrite</c> (<c>UncWizardAccessRows</c>' shape). Pinned.</summary>
    public static IReadOnlyList<string> DatabaseWizardAccessRows(ServerWriteFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return
        [
            "read       read only (the default)",
            $"readwrite  also change data through {family.ToolName}, each change allowed by you, while {family.ModeSetting} is read-write",
        ];
    }

    /// <summary>The wizards' access row value: the word.</summary>
    public static string DatabaseWizardAccessValue(bool readWrite) => readWrite ? ConnectionAccess.ReadWriteAccess : ConnectionAccess.ReadAccess;

    /// <summary>The notice when a connection is made readwrite while its family's mode is read-only: it stays read-only till then. Pinned.</summary>
    public static string DatabaseWizardModeOffNotice(ServerWriteFamily family) =>
        $"readwrite takes effect once {family.ModeSetting} is read-write ({family.Tab}); until then the connection only reads.";

    /// <summary>Says <see cref="DatabaseWizardModeOffNotice"/> when <paramref name="readWrite"/> and the family's <paramref name="mode"/> is not read-write.</summary>
    private void NoticeAccessModeOff(ServerWriteFamily family, bool readWrite, string? mode)
    {
        if (readWrite && !DatabaseWriteModes.IsReadWrite(mode))
        {
            Sink.Notice(DatabaseWizardModeOffNotice(family));
        }
    }

    /// <summary>The write rows' edits, by field; false when <paramref name="field"/> is none of them.</summary>
    private async Task<bool?> EditWriteFieldAsync(SettingsField field, AppSettingsData saved, CancellationToken cancellationToken) => field switch
    {
        SettingsField.PostgresMode => await PickWriteModeAsync(field, Postgres.PostgresStatementKinds.Family, saved.PostgresMode, (d, v) => d.PostgresMode = v, cancellationToken).ConfigureAwait(false),
        SettingsField.PostgresStatementsAllowed => await EditWriteStatementsAsync(field, Postgres.PostgresStatementKinds.Family, d => d.PostgresStatementsAllowed, (d, v) => d.PostgresStatementsAllowed = v, cancellationToken).ConfigureAwait(false),
        SettingsField.MySqlMode => await PickWriteModeAsync(field, MySql.MySqlStatementKinds.Family, saved.MySqlMode, (d, v) => d.MySqlMode = v, cancellationToken).ConfigureAwait(false),
        SettingsField.MySqlStatementsAllowed => await EditWriteStatementsAsync(field, MySql.MySqlStatementKinds.Family, d => d.MySqlStatementsAllowed, (d, v) => d.MySqlStatementsAllowed = v, cancellationToken).ConfigureAwait(false),
        SettingsField.SqlMode => await PickWriteModeAsync(field, Sql.SqlStatementKinds.Family, saved.SqlMode, (d, v) => d.SqlMode = v, cancellationToken).ConfigureAwait(false),
        SettingsField.SqlStatementsAllowed => await EditWriteStatementsAsync(field, Sql.SqlStatementKinds.Family, d => d.SqlStatementsAllowed, (d, v) => d.SqlStatementsAllowed = v, cancellationToken).ConfigureAwait(false),
        SettingsField.OracleMode => await PickWriteModeAsync(field, Oracle.OracleStatementKinds.Family, saved.OracleMode, (d, v) => d.OracleMode = v, cancellationToken).ConfigureAwait(false),
        SettingsField.OracleStatementsAllowed => await EditWriteStatementsAsync(field, Oracle.OracleStatementKinds.Family, d => d.OracleStatementsAllowed, (d, v) => d.OracleStatementsAllowed = v, cancellationToken).ConfigureAwait(false),
        _ => null,
    };
}
