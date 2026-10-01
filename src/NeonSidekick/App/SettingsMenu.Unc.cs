using System.Globalization;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.Unc;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The UNC tab of <c>/tools</c> (2026-09-30, the user's ask: SQL's integrated auth and run-as, for file shares), the database
/// tabs' rows over <c>unc.json</c>: the two switches (the tools, and the writes they may make), the offered checklist, the default
/// pick, the masked password prompt for a runas share and the two edit rows; the wizard is <c>SettingsMenu.UncWizard.cs</c>. The
/// SQL tab's labels serve where the words are the same.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The value column of the <c>UNC set password</c> action row: only a runas share takes one. Pinned.</summary>
    public const string UncSetPasswordLabel = "Enter to set password for a runas share";

    /// <summary>The value of <c>UNC default share</c> while none is saved. Pinned.</summary>
    public const string FirstUncShareLabel = "(the first share)";

    /// <summary>The value of an edit row over one <c>unc.json</c>: how many shares it holds and how many entries it skips, or <c>(none)</c>.</summary>
    public static string UncSharesLabel(string path)
    {
        var loaded = UncConfigFile.Load(path);
        string count = loaded.Shares.Count == 0 ? "(none)" : Sql.SqlText.Count(loaded.Shares.Count, "share");
        return (loaded.Problems.Count == 0 ? count : count + ", " + Sql.SqlText.Count(loaded.Problems.Count, "problem")) + " · Enter edits unc.json";
    }

    /// <summary>The value of <c>UNC shares offered</c>: how many of the loaded shares the profile offers, <c>none of N</c> before any is ticked (2026-10-01: null offers none). Pinned.</summary>
    public static string UncOfferedValue(IReadOnlyList<string>? offered, UncCatalog loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        int kept = loaded.Offered(offered).Shares.Count;
        return (kept == 0 ? "none" : kept.ToString(CultureInfo.InvariantCulture)) + " of " + loaded.Shares.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One row of the <c>UNC shares offered</c> checklist: the mark, the name, where it points. Pinned.</summary>
    public static string UncOfferedRow(UncNamedShare share, bool offered, int width)
    {
        ArgumentNullException.ThrowIfNull(share);
        return Markup.Escape((offered ? "[x] " : "[ ] ") + share.Name.PadRight(width)) + Theme.DimMarkup(UncText.MentionNote(share));
    }

    /// <summary>A runas share on the <c>UNC set password</c> pick: its name, its account and where its password goes. Pinned.</summary>
    public static string UncPasswordRow(UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        string store = share.Config.InCredentialManager ? "Windows Credential Manager" : "encrypted in unc.json";
        return $"{share.Name}  ({share.Config.Root}, runas {share.Config.User?.Trim()} · {store})";
    }

    /// <summary>The status line after an edit row opened an <c>unc.json</c>. Pinned.</summary>
    public static string UncEditingNotice(string path) => $"Opened {path} in the editor; the UNC tools read it at their next call.";

    /// <summary>What <c>UNC set password</c> says with no runas share to set one for. Pinned.</summary>
    public const string UncNoPasswordShares = "No share in unc.json signs in as another account (runas); a windows share needs no password.";

    /// <summary>The <c>UNC default share</c> pick: <see cref="FirstUncShareLabel"/>, then every offered share, the cursor on the one saved.</summary>
    private async Task<bool> PickUncShareAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = UncConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Offered(saved.UncSharesOffered).Shares.Select(s => s.Name).ToList();
        var rows = new List<string> { Markup.Escape(FirstUncShareLabel) };
        rows.AddRange(names.Select(Markup.Escape));
        int current = names.FindIndex(n => string.Equals(n, saved.UncDefaultShare, StringComparison.OrdinalIgnoreCase));
        var page = new MenuPage(Crumb(FieldName(SettingsField.UncDefaultShare)), rows, PickKeys);
        int? picked = await PickAsync(page, current + 1, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = index == 0 ? "" : names[index - 1];
        Apply(SettingsField.UncDefaultShare, d => d.UncDefaultShare = name);
        return true;
    }

    /// <summary>
    /// <c>UNC shares offered</c>, <see cref="EditSqlOfferedAsync"/>'s twin: every share the two files hold, ticked or not, Enter or
    /// Space flipping one until ESC; nothing is ticked until the user ticks it (2026-10-01; a never-narrowed profile started all ticked
    /// until then), so a share added later stays hidden until ticked. True when anything changed.
    /// </summary>
    private async Task<bool> EditUncOfferedAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var loaded = UncConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            if (loaded.Shares.Count == 0)
            {
                Sink.Error(UncText.NoShares);
                return changed;
            }

            var offered = _settings.Current.UncSharesOffered;
            var on = loaded.Offered(offered).Shares.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int width = loaded.Shares.Max(s => s.Name.Length) + 2;
            var page = new MenuPage(Crumb(FieldName(SettingsField.UncSharesOffered)), loaded.Shares.Select(s => UncOfferedRow(s, on.Contains(s.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, loaded.Shares.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = loaded.Shares[pick.Row].Name;
            var next = pick.Button == SelectAllIndex ? loaded.Shares.Select(s => s.Name).ToList()
                : pick.Button == SelectNoneIndex ? []
                : loaded.Shares.Select(s => s.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count == on.Count && next.All(on.Contains))
            {
                continue;   // null and empty both offer none (2026-10-01)
            }

            if (offered is not null)
            {
                next.AddRange(offered.Where(n => !loaded.Shares.Any(s => string.Equals(s.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
            }

            Apply(SettingsField.UncSharesOffered, d => d.UncSharesOffered = next);
            changed = true;
        }
    }

    /// <summary>
    /// <c>UNC set password</c>, <see cref="SetSqlPasswordAsync"/>'s twin: every runas share with its store, then a masked slot under
    /// the picked one, then <see cref="UncSecrets.Save"/>. Nothing in the settings changes; the status line says what happened. ESC
    /// or an empty Enter saves nothing.
    /// </summary>
    private async Task<bool> SetUncPasswordAsync(CancellationToken cancellationToken)
    {
        var shares = UncConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Shares.Where(s => s.Config.NeedsPassword).ToList();
        if (shares.Count == 0)
        {
            Sink.Error(UncNoPasswordShares);
            return false;
        }

        var page = new MenuPage(Crumb(FieldName(SettingsField.UncSetPassword)), shares.Select(s => Markup.Escape(UncPasswordRow(s))).ToList(), PickKeys);
        if (await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { } index)
        {
            return Unchanged();
        }

        var share = shares[index];
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Hint = EditKeys }, index, _input, "", allowEmpty: false, cancellationToken, mask: true).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(FieldName(SettingsField.UncSetPassword) + " · " + share.Name, EditKeys));
            result = await _input.ReadAsync("", remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true, mask: true).ConfigureAwait(false);
        }

        if (result is not InputResult.Submitted { Text.Length: > 0 } submitted)
        {
            return Unchanged();
        }

        var (saved, notice) = UncSecrets.Save(share, submitted.Text);
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

    /// <summary>Opens one <c>unc.json</c> in the editor, made first when missing; without an opener or on an IO failure, the status line says so.</summary>
    private void OpenUncFile(string path)
    {
        try
        {
            UncConfigFile.EnsureExists(path);
            if (_openFile is null)
            {
                Sink.Error(SqlEditFailedError(path, "no editor to open it in"));
                return;
            }

            _openFile(path);
            Sink.Notice(UncEditingNotice(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(SqlEditFailedError(path, Diagnostics.LogText.Excerpt(ex.Message)));
        }
    }
}
