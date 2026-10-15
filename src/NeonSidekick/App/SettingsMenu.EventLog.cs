using System.Globalization;
using NeonSidekick.EventLog;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The EventLog menu of <c>/tools</c> › Apps (2026-10-15, the event-log plan), the UNC menu's rows over <c>eventlog.json</c>: the switch, the
/// computers offered (a checklist, <c>local</c> first), the masked password prompt for a runas computer, the wizard
/// (<c>SettingsMenu.EventLogWizard.cs</c>), the two numbers and the two edit rows. Windows only, as the tools are.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The value column of <c>Event log set password</c>. Pinned.</summary>
    public const string EventLogSetPasswordLabel = "Enter to set password for a runas computer";

    /// <summary>What <c>Event log set password</c> says with no runas computer. Pinned.</summary>
    public const string EventLogNoPasswordComputers = "No computer in eventlog.json signs in as another account (runas); this PC and a windows computer need no password.";

    /// <summary>
    /// The app's test of a wizard's draft (2026-10-15): the newest System event read under the draft's account, nothing written. Set by the
    /// screen (its reader); null where there is none, and the wizard's test then says so.
    /// </summary>
    public Func<EventLogComputer, CancellationToken, Task<(bool Ok, string Text)>>? TestEventLogComputer { get; set; }

    /// <summary>The value of an edit row over one <c>eventlog.json</c>: how many computers it holds and how many entries it skips, or <c>(none)</c>.</summary>
    public static string EventLogComputersLabel(string path)
    {
        var (computers, problems) = EventLogConfigFile.Load(path);
        string count = computers.Count == 0 ? "(none)" : Sql.SqlText.Count(computers.Count, "computer");
        return (problems.Count == 0 ? count : count + ", " + Sql.SqlText.Count(problems.Count, "problem")) + " · Enter edits eventlog.json";
    }

    /// <summary>The value of <c>Event log computers offered</c>: how many of the loaded computers are ticked, <c>local</c> counted, <c>none of N</c> for none. Pinned.</summary>
    public static string EventLogOfferedValue(IReadOnlyList<string>? offered, EventLogCatalog loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        int kept = loaded.Offered(offered).Computers.Count;
        return (kept == 0 ? "none" : kept.ToString(CultureInfo.InvariantCulture)) + " of " + loaded.Computers.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One row of the checklist: the mark, the name, then (dim) <c>host · sign-in · detail · logs</c>; <c>local</c>'s
    /// <c>this PC · as &lt;account&gt; · &lt;detail&gt;</c>. Pinned.
    /// </summary>
    public static string EventLogOfferedRow(EventLogComputer computer, bool offered, int width)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return Markup.Escape((offered ? "[x] " : "[ ] ") + computer.Name.PadRight(width)) + Theme.DimMarkup(EventLogComputerNote(computer));
    }

    /// <summary>A computer's note: where, as whom, the detail, and the logs it allows when it narrows them. Pinned.</summary>
    public static string EventLogComputerNote(EventLogComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var c = computer.Config;
        var parts = new List<string> { computer.Where };
        parts.Add(computer.IsLocal ? "as " + computer.Account : c.IsRunAs ? "runas " + c.User?.Trim() : "as you");
        parts.Add(c.DetailWord);
        if (c.Logs is { } logs)
        {
            parts.Add(logs.Count == 0 ? "no logs" : string.Join(", ", logs));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>A runas computer on the <c>Event log set password</c> pick: its name, host, account and store. Pinned.</summary>
    public static string EventLogPasswordRow(EventLogComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        string store = computer.Config.InCredentialManager ? Sql.SqlText.CredentialStore : "encrypted in eventlog.json";
        return $"{computer.Name}  ({computer.Config.Host?.Trim()}, runas {computer.Config.User?.Trim()} · {store})";
    }

    /// <summary>The status line after an edit row opened an <c>eventlog.json</c>. Pinned.</summary>
    public static string EventLogEditingNotice(string path) => $"Opened {path} in the editor; the event-log tools read it at their next call.";

    /// <summary>
    /// <c>Event log computers offered</c>: every computer the two files hold, <c>local</c> first and the rest A to Z, ticked or not; Enter or
    /// Space flips one until ESC. Null (never set) reads as <c>local</c> alone; a flip saves the list as it stands, so an untick of
    /// <c>local</c> stays. True when anything changed.
    /// </summary>
    private async Task<bool> EditEventLogOfferedAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var loaded = EventLogConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            var saved = _settings.Current.EventLogComputersOffered;
            if (saved is not null)
            {
                // A name no longer in the files is dropped as the list opens (the UNC list's rule), unless a problem still names it.
                changed |= PruneStale(SettingsField.EventLogComputersOffered, saved, OfferedNames.StaleConnections(saved, loaded.Computers.Select(c => c.Name), loaded.Problems), StringComparer.OrdinalIgnoreCase, (d, kept) => d.EventLogComputersOffered = kept);
                saved = _settings.Current.EventLogComputersOffered;
            }

            var on = loaded.Offered(saved).Computers.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var computers = new List<EventLogComputer> { loaded.Computers[0] };
            computers.AddRange(ByName(loaded.Computers.Skip(1), c => c.Name));
            int width = computers.Max(c => c.Name.Length) + 2;
            var page = new MenuPage(Crumb(FieldName(SettingsField.EventLogComputersOffered)), computers.Select(c => EventLogOfferedRow(c, on.Contains(c.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, computers.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = computers[pick.Row].Name;
            var next = pick.Button == SelectAllIndex ? computers.Select(c => c.Name).ToList()
                : pick.Button == SelectNoneIndex ? []
                : computers.Select(c => c.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count == on.Count && next.All(on.Contains))
            {
                continue;
            }

            if (saved is not null)
            {
                next.AddRange(saved.Where(n => !loaded.Computers.Any(c => string.Equals(c.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
            }

            Apply(SettingsField.EventLogComputersOffered, d => d.EventLogComputersOffered = next);
            changed = true;
        }
    }

    /// <summary><c>Event log set password</c>: every runas computer with its store, then a masked slot under the picked one, then <see cref="EventLogSecrets.Save"/>.</summary>
    private async Task<bool> SetEventLogPasswordAsync(CancellationToken cancellationToken)
    {
        var computers = ByName(EventLogConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory).Computers.Where(c => c.Config.NeedsPassword), c => c.Name);
        if (computers.Count == 0)
        {
            Sink.Error(EventLogNoPasswordComputers);
            return false;
        }

        var page = new MenuPage(Crumb(FieldName(SettingsField.EventLogSetPassword)), computers.Select(c => Markup.Escape(EventLogPasswordRow(c))).ToList(), PickKeys);
        if (await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { } index)
        {
            return Unchanged();
        }

        var computer = computers[index];
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Hint = EditKeys }, index, _input, "", allowEmpty: false, cancellationToken, mask: true).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(FieldName(SettingsField.EventLogSetPassword) + " · " + computer.Name, EditKeys));
            result = await _input.ReadAsync("", remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true, mask: true).ConfigureAwait(false);
        }

        if (result is not InputResult.Submitted { Text.Length: > 0 } submitted)
        {
            return Unchanged();
        }

        var (saved, notice) = EventLogSecrets.Save(computer, submitted.Text);
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

    /// <summary>Opens one <c>eventlog.json</c> in the editor, made first when missing; without an opener or on an IO failure, the status line says so.</summary>
    private void OpenEventLogFile(string path)
    {
        try
        {
            EventLogConfigFile.EnsureExists(path);
            if (_openFile is null)
            {
                Sink.Error(SqlEditFailedError(path, "no editor to open it in"));
                return;
            }

            _openFile(path);
            Sink.Notice(EventLogEditingNotice(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(SqlEditFailedError(path, Diagnostics.LogText.Excerpt(ex.Message)));
        }
    }
}
