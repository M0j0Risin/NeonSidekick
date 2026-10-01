using System.Globalization;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The Oracle tab of <c>/tools</c> (2026-09-30, the user's ask: "an Oracle tab in tools with all the settings we'd need"),
/// the SQL tab's rows over <c>oracle.json</c>: the default pick, the offered checklist, the masked password prompt and the
/// two edit rows; the wizard is <c>SettingsMenu.OracleWizard.cs</c>. The SQL tab's labels and range wording serve where the
/// words are the same.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The value of an edit row over one <c>oracle.json</c>: how many connections it holds and how many entries it skips, or <c>(none)</c>.</summary>
    public static string OracleConnectionsLabel(string path)
    {
        var loaded = OracleConfigFile.Load(path);
        string count = loaded.Connections.Count == 0 ? "(none)" : Sql.SqlText.Count(loaded.Connections.Count, "connection");
        return (loaded.Problems.Count == 0 ? count : count + ", " + Sql.SqlText.Count(loaded.Problems.Count, "problem")) + " · Enter edits oracle.json";
    }

    /// <summary>The value of <c>Oracle connections offered</c>: <see cref="SqlNotNarrowedLabel"/> while the profile never narrowed it, else how many of the loaded connections it offers. Pinned.</summary>
    public static string OracleOfferedValue(IReadOnlyList<string>? offered, OracleCatalog loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (offered is null)
        {
            return SqlNotNarrowedLabel;
        }

        int kept = loaded.Offered(offered).Connections.Count;
        return (kept == 0 ? "none" : kept.ToString(CultureInfo.InvariantCulture)) + " of " + loaded.Connections.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One row of the <c>Oracle connections offered</c> checklist: the mark, the name, where it points. Pinned.</summary>
    public static string OracleOfferedRow(OracleNamedConnection connection, bool offered, int width)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Markup.Escape((offered ? "[x] " : "[ ] ") + connection.Name.PadRight(width)) + Theme.DimMarkup(OracleText.MentionNote(connection));
    }

    /// <summary>A connection on the <c>Oracle set password</c> pick: its name, its user and where its password goes. Pinned.</summary>
    public static string OraclePasswordRow(OracleNamedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string store = connection.Config.InCredentialManager ? "Windows Credential Manager" : "encrypted in oracle.json";
        return $"{connection.Name}  (user {connection.Config.User?.Trim()} · {store})";
    }

    /// <summary>The status line after an edit row opened an <c>oracle.json</c>. Pinned.</summary>
    public static string OracleEditingNotice(string path) => $"Opened {path} in the editor; the Oracle tools read it at their next call.";

    /// <summary>The <c>Oracle default connection</c> pick: <see cref="FirstSqlConnectionLabel"/>, then every offered connection, the cursor on the one saved.</summary>
    private async Task<bool> PickOracleConnectionAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = OracleConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(saved.OracleConnectionsOffered).Connections.Select(c => c.Name).ToList();
        var rows = new List<string> { Markup.Escape(FirstSqlConnectionLabel) };
        rows.AddRange(names.Select(Markup.Escape));
        int current = names.FindIndex(n => string.Equals(n, saved.OracleDefaultConnection, StringComparison.OrdinalIgnoreCase));
        var page = new MenuPage(Crumb(FieldName(SettingsField.OracleDefaultConnection)), rows, PickKeys);
        int? picked = await PickAsync(page, current + 1, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = index == 0 ? "" : names[index - 1];
        Apply(SettingsField.OracleDefaultConnection, d => d.OracleDefaultConnection = name);
        return true;
    }

    /// <summary>
    /// <c>Oracle connections offered</c>, <see cref="EditSqlOfferedAsync"/>'s twin: every connection the two files hold, ticked
    /// or not, Enter or Space flipping one until ESC; the first flip of a profile that never narrowed it saves every name but
    /// the flipped one, so a connection added later stays hidden until ticked. True when anything changed.
    /// </summary>
    private async Task<bool> EditOracleOfferedAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var loaded = OracleConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            if (loaded.Connections.Count == 0)
            {
                Sink.Error(OracleText.NoConnections);
                return changed;
            }

            var offered = _settings.Current.OracleConnectionsOffered;
            var on = loaded.Offered(offered).Connections.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int width = loaded.Connections.Max(c => c.Name.Length) + 2;
            var page = new MenuPage(Crumb(FieldName(SettingsField.OracleConnectionsOffered)), loaded.Connections.Select(c => OracleOfferedRow(c, on.Contains(c.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, loaded.Connections.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = loaded.Connections[pick.Row].Name;
            var next = pick.Button == SelectAllIndex ? loaded.Connections.Select(c => c.Name).ToList()
                : pick.Button == SelectNoneIndex ? []
                : loaded.Connections.Select(c => c.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (offered is not null && next.Count == on.Count && next.All(on.Contains))
            {
                continue;
            }

            if (offered is not null)
            {
                next.AddRange(offered.Where(n => !loaded.Connections.Any(c => string.Equals(c.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
            }

            Apply(SettingsField.OracleConnectionsOffered, d => d.OracleConnectionsOffered = next);
            changed = true;
        }
    }

    /// <summary>
    /// <c>Oracle set password</c>, <see cref="SetSqlPasswordAsync"/>'s twin (the user's ask: "the function to set the password
    /// is also nice"): every connection with its store, then a masked slot under the picked one, then
    /// <see cref="OracleSecrets.Save"/>. Nothing in the settings changes; the status line says what happened. ESC or an empty
    /// Enter saves nothing.
    /// </summary>
    private async Task<bool> SetOraclePasswordAsync(CancellationToken cancellationToken)
    {
        var connections = OracleConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Connections;
        if (connections.Count == 0)
        {
            Sink.Error(OracleText.NoPasswordConnections);
            return false;
        }

        var page = new MenuPage(Crumb(FieldName(SettingsField.OracleSetPassword)), connections.Select(c => Markup.Escape(OraclePasswordRow(c))).ToList(), PickKeys);
        if (await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { } index)
        {
            return Unchanged();
        }

        var connection = connections[index];
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Hint = EditKeys }, index, _input, "", allowEmpty: false, cancellationToken, mask: true).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(FieldName(SettingsField.OracleSetPassword) + " · " + connection.Name, EditKeys));
            result = await _input.ReadAsync("", remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true, mask: true).ConfigureAwait(false);
        }

        if (result is not InputResult.Submitted { Text.Length: > 0 } submitted)
        {
            return Unchanged();
        }

        var (saved, notice) = OracleSecrets.Save(connection, submitted.Text);
        if (saved)
        {
            Sink.Notice(notice);
        }
        else
        {
            Sink.Error(notice);
        }

        return false;
    }

    /// <summary>Opens one <c>oracle.json</c> in the editor, made first when missing; without an opener or on an IO failure, the status line says so.</summary>
    private void OpenOracleFile(string path)
    {
        try
        {
            OracleConfigFile.EnsureExists(path);
            if (_openFile is null)
            {
                Sink.Error(SqlEditFailedError(path, "no editor to open it in"));
                return;
            }

            _openFile(path);
            Sink.Notice(OracleEditingNotice(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(SqlEditFailedError(path, Diagnostics.LogText.Excerpt(ex.Message)));
        }
    }
}
