using System.Globalization;
using NeonSidekick.Computers;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Tasks;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The computer families' rows on <c>/tools</c> › Admin (2026-10-15, the admin-tools plan: the EventLog menu's rows made generic over
/// <see cref="ComputerFamily{TConfig}"/>): the offered checklist (<c>local</c> first), the masked password prompt for a runas computer, the
/// wizard (<c>SettingsMenu.ComputerWizard.cs</c>) and the two edit rows. The services' and the tasks' menus are these over their families.
/// </summary>
internal sealed partial class SettingsMenu
{

    /// <summary>The wizards' tests (the screen sets them; null where there is no system, and the wizard says so).</summary>
    public Func<Computer<ServiceComputerConfig>, CancellationToken, Task<(bool Ok, string Text)>>? TestServiceComputer { get; set; }

    public Func<Computer<TaskComputerConfig>, CancellationToken, Task<(bool Ok, string Text)>>? TestTaskComputer { get; set; }

    /// <summary>The value of an edit row over one family file: how many computers it holds and how many entries it skips, or <c>(none)</c>.</summary>
    public static string ComputersFileLabel<TConfig>(ComputerFamily<TConfig> family, string path) where TConfig : ComputerConfig, new()
    {
        var (computers, problems) = family.Load(path);
        string count = computers.Count == 0 ? "(none)" : Sql.SqlText.Count(computers.Count, "computer");
        return (problems.Count == 0 ? count : count + ", " + Sql.SqlText.Count(problems.Count, "problem")) + " · Enter edits " + family.Info.FileName;
    }

    /// <summary>The value of an <c>… computers offered</c> row: ticked of loaded, <c>none of N</c>. Pinned.</summary>
    public static string ComputersOfferedValue<TConfig>(IReadOnlyList<string>? offered, ComputerCatalog<TConfig> loaded) where TConfig : ComputerConfig
    {
        ArgumentNullException.ThrowIfNull(loaded);
        int kept = loaded.Offered(offered).Computers.Count;
        return (kept == 0 ? "none" : kept.ToString(CultureInfo.InvariantCulture)) + " of " + loaded.Computers.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A family's ticked computers on a pick page, the current one under the cursor; the picked name, or null for Esc (2026-10-23, the admin
    /// review's T19: the services and tasks panes each had a copy).
    /// </summary>
    public static async Task<string?> PickComputerAsync<TConfig>(MenuPane pane, string title, IReadOnlyList<Computer<TConfig>> computers, string current, CancellationToken cancellationToken)
        where TConfig : ComputerConfig
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(computers);
        int width = computers.Max(c => c.Name.Length) + 2;
        var rows = computers.Select(c => Spectre.Console.Markup.Escape(c.Name.PadRight(width)) + Theme.DimMarkup(ComputerNote(c))).ToList();
        int cursor = Math.Max(0, computers.ToList().FindIndex(c => string.Equals(c.Name, current, StringComparison.OrdinalIgnoreCase)));
        var page = new MenuPage(title + " · computer", rows, PickKeys);
        return await pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is { Row: var row } && row >= 0 && row < computers.Count ? computers[row].Name : null;
    }

    /// <summary>A computer's note on the checklist: where, as whom, read or readwrite, the detail. Pinned.</summary>
    public static string ComputerNote<TConfig>(Computer<TConfig> computer) where TConfig : ComputerConfig
    {
        ArgumentNullException.ThrowIfNull(computer);
        var c = computer.Config;
        var parts = new List<string> { computer.Where, computer.IsLocal ? "as " + computer.Account : c.IsRunAs ? "runas " + c.User?.Trim() : "as you" };
        parts.Add(c.ReadWrite ? "readwrite" + (c.ControlActions is { } actions ? " (" + (actions.Count == 0 ? "no actions" : string.Join(", ", actions)) + ")" : "") : "read");
        parts.Add(c.DetailWord);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// An <c>… computers offered</c> checklist (2026-10-15, <c>EditEventLogOfferedAsync</c> made generic): every computer the two files hold,
    /// <c>local</c> first and the rest A to Z; Enter or Space flips one until ESC. True when anything changed.
    /// </summary>
    private async Task<bool> EditComputersOfferedAsync<TConfig>(ComputerFamily<TConfig> family, SettingsField field, Func<AppSettingsData, List<string>?> get, Action<AppSettingsData, List<string>> set, CancellationToken cancellationToken)
        where TConfig : ComputerConfig, new()
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var loaded = family.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory);
            var saved = get(_settings.Current);
            if (saved is not null)
            {
                changed |= PruneStale(field, saved, OfferedNames.StaleConnections(saved, loaded.Computers.Select(c => c.Name), loaded.Problems), StringComparer.OrdinalIgnoreCase, set);
                saved = get(_settings.Current);
            }

            var on = loaded.Offered(saved).Computers.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var computers = new List<Computer<TConfig>> { loaded.Computers[0] };
            computers.AddRange(ByName(loaded.Computers.Skip(1), c => c.Name));
            int width = computers.Max(c => c.Name.Length) + 2;
            var rows = computers.Select(c => Markup.Escape((on.Contains(c.Name) ? "[x] " : "[ ] ") + c.Name.PadRight(width)) + Theme.DimMarkup(ComputerNote(c))).ToList();
            var page = new MenuPage(Crumb(FieldName(field)), rows, ToggleKeys) { SpaceToggles = true };
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

            Apply(field, d => set(d, next));
            changed = true;
        }
    }

    /// <summary>Opens a family's file in the editor, made with its commented shape first; the status line says so, or why not.</summary>
    private void OpenConfigFile(string path, Func<string, bool> ensure, string file)
    {
        try
        {
            ensure(path);
            if (_openFile is null)
            {
                Sink.Error(SqlEditFailedError(path, "no editor to open it in"));
                return;
            }

            _openFile(path);
            Sink.Notice(AdminEditingNotice(path, file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(SqlEditFailedError(path, Diagnostics.LogText.Excerpt(ex.Message)));
        }
    }

    /// <summary>The status line after an edit row opened an admin family's file. Pinned.</summary>
    public static string AdminEditingNotice(string path, string file) => $"Opened {path} in the editor; the tools read {file} at their next call.";
}
