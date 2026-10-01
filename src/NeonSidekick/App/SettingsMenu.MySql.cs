using System.Globalization;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The MySQL tab of <c>/tools</c> (2026-09-30, the user's ask: MySQL "very similar to Oracle"), the Oracle tab's rows over
/// <c>mysql.json</c>: the default pick, the offered checklist, the masked password prompt and the
/// two edit rows; the wizard is <c>SettingsMenu.MySqlWizard.cs</c>. The SQL tab's labels and range wording serve where the
/// words are the same.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The value of an edit row over one <c>mysql.json</c>: how many connections it holds and how many entries it skips, or <c>(none)</c>.</summary>
    public static string MySqlConnectionsLabel(string path)
    {
        var loaded = MySqlConfigFile.Load(path);
        string count = loaded.Connections.Count == 0 ? "(none)" : Sql.SqlText.Count(loaded.Connections.Count, "connection");
        return (loaded.Problems.Count == 0 ? count : count + ", " + Sql.SqlText.Count(loaded.Problems.Count, "problem")) + " · Enter edits mysql.json";
    }

    /// <summary>The value of <c>MySQL connections offered</c>: how many of the loaded connections the profile offers, <c>none of N</c> before any is ticked (2026-10-01: null offers none). Pinned.</summary>
    public static string MySqlOfferedValue(IReadOnlyList<string>? offered, MySqlCatalog loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        int kept = loaded.Offered(offered).Connections.Count;
        return (kept == 0 ? "none" : kept.ToString(CultureInfo.InvariantCulture)) + " of " + loaded.Connections.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One row of the <c>MySQL connections offered</c> checklist: the mark, the name, where it points. Pinned.</summary>
    public static string MySqlOfferedRow(MySqlNamedConnection connection, bool offered, int width)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Markup.Escape((offered ? "[x] " : "[ ] ") + connection.Name.PadRight(width)) + Theme.DimMarkup(MySqlText.MentionNote(connection));
    }

    /// <summary>A connection on the <c>MySQL set password</c> pick: its name, its user and where its password goes. Pinned.</summary>
    public static string MySqlPasswordRow(MySqlNamedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string store = connection.Config.InCredentialManager ? "Windows Credential Manager" : "encrypted in mysql.json";
        return $"{connection.Name}  ({connection.Config.Endpoint}, user {connection.Config.User?.Trim()} · {store})";
    }

    /// <summary>The status line after an edit row opened an <c>mysql.json</c>. Pinned.</summary>
    public static string MySqlEditingNotice(string path) => $"Opened {path} in the editor; the MySQL tools read it at their next call.";

    /// <summary>The <c>MySQL default connection</c> pick: <see cref="FirstSqlConnectionLabel"/>, then every offered connection, the cursor on the one saved.</summary>
    private async Task<bool> PickMySqlConnectionAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = MySqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(saved.MySqlConnectionsOffered).Connections.Select(c => c.Name).ToList();
        var rows = new List<string> { Markup.Escape(FirstSqlConnectionLabel) };
        rows.AddRange(names.Select(Markup.Escape));
        int current = names.FindIndex(n => string.Equals(n, saved.MySqlDefaultConnection, StringComparison.OrdinalIgnoreCase));
        var page = new MenuPage(Crumb(FieldName(SettingsField.MySqlDefaultConnection)), rows, PickKeys);
        int? picked = await PickAsync(page, current + 1, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = index == 0 ? "" : names[index - 1];
        Apply(SettingsField.MySqlDefaultConnection, d => d.MySqlDefaultConnection = name);
        return true;
    }

    /// <summary>
    /// <c>MySQL connections offered</c>, <see cref="EditSqlOfferedAsync"/>'s twin: every connection the two files hold, ticked
    /// or not, Enter or Space flipping one until ESC; nothing is ticked until the user ticks it (2026-10-01; a
    /// never-narrowed profile started all ticked until then), so a connection added later stays hidden until ticked. True when anything changed.
    /// </summary>
    private async Task<bool> EditMySqlOfferedAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var loaded = MySqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            if (loaded.Connections.Count == 0)
            {
                Sink.Error(MySqlText.NoConnections);
                return changed;
            }

            var offered = _settings.Current.MySqlConnectionsOffered;
            var on = loaded.Offered(offered).Connections.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int width = loaded.Connections.Max(c => c.Name.Length) + 2;
            var page = new MenuPage(Crumb(FieldName(SettingsField.MySqlConnectionsOffered)), loaded.Connections.Select(c => MySqlOfferedRow(c, on.Contains(c.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
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
            if (next.Count == on.Count && next.All(on.Contains))
            {
                continue;   // null and empty both offer none (2026-10-01)
            }

            if (offered is not null)
            {
                next.AddRange(offered.Where(n => !loaded.Connections.Any(c => string.Equals(c.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
            }

            Apply(SettingsField.MySqlConnectionsOffered, d => d.MySqlConnectionsOffered = next);
            changed = true;
        }
    }

    /// <summary>
    /// <c>MySQL set password</c>, <see cref="SetSqlPasswordAsync"/>'s twin (the user's ask: "the function to set the password
    /// is also nice"): every connection with its store, then a masked slot under the picked one, then
    /// <see cref="MySqlSecrets.Save"/>. Nothing in the settings changes; the status line says what happened. ESC or an empty
    /// Enter saves nothing.
    /// </summary>
    private async Task<bool> SetMySqlPasswordAsync(CancellationToken cancellationToken)
    {
        var connections = MySqlConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Connections;
        if (connections.Count == 0)
        {
            Sink.Error(MySqlText.NoPasswordConnections);
            return false;
        }

        var page = new MenuPage(Crumb(FieldName(SettingsField.MySqlSetPassword)), connections.Select(c => Markup.Escape(MySqlPasswordRow(c))).ToList(), PickKeys);
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
            Flow.Notice(PromptTitle(FieldName(SettingsField.MySqlSetPassword) + " · " + connection.Name, EditKeys));
            result = await _input.ReadAsync("", remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true, mask: true).ConfigureAwait(false);
        }

        if (result is not InputResult.Submitted { Text.Length: > 0 } submitted)
        {
            return Unchanged();
        }

        var (saved, notice) = MySqlSecrets.Save(connection, submitted.Text);
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

    /// <summary>Opens one <c>mysql.json</c> in the editor, made first when missing; without an opener or on an IO failure, the status line says so.</summary>
    private void OpenMySqlFile(string path)
    {
        try
        {
            MySqlConfigFile.EnsureExists(path);
            if (_openFile is null)
            {
                Sink.Error(SqlEditFailedError(path, "no editor to open it in"));
                return;
            }

            _openFile(path);
            Sink.Notice(MySqlEditingNotice(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(SqlEditFailedError(path, Diagnostics.LogText.Excerpt(ex.Message)));
        }
    }
}
