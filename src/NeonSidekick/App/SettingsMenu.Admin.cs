using System.Globalization;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Network;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Tasks;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The admin families' rows (2026-10-15, the admin-tools plan): the Network menu (its switch, reach picker, budget,
/// host lists and the certificate stores' switch; on <c>/tools</c> › Core until 2026-10-16, Admin's first since) and the Services, Tasks and Directory menus on <c>/tools</c> › Admin (switches, offered
/// checklists, passwords, wizards, edit rows). One value function and one edit dispatcher for every field from <see cref="SettingsField.NetworkTools"/>
/// to <see cref="SettingsField.DirectoryDomainsGlobal"/>.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The top row of a host list. Pinned.</summary>
    public const string AddHostRow = "+ Add a host…";

    public const string HostListKeys = "Enter = add or remove · Esc = back";

    /// <summary>A host list's value: <c>(empty)</c>, or how many entries. Pinned.</summary>
    public static string HostListValue(IReadOnlyList<string> hosts) =>
        hosts.Count == 0 ? "(empty)" : hosts.Count.ToString(CultureInfo.InvariantCulture) + (hosts.Count == 1 ? " entry" : " entries");

    public static string HostAddedNotice(SettingsField field, string entry) => FieldName(field) + ": " + entry + " added";

    public static string HostDuplicateNotice(SettingsField field, string entry) => FieldName(field) + ": " + entry + " is already in the list";

    public static string HostRemovedNotice(SettingsField field, string entry) => FieldName(field) + ": " + entry + " removed";

    public static string RemoveHostQuestion(SettingsField field, string entry) => $"Remove \"{entry}\" from {FieldName(field)}?";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.NetworkMaxHostsPerTurn"/>. Pinned.</summary>
    public static readonly string NetworkMaxHostsRangeError =
        "must be " + AppSettingsData.MinNetworkMaxHosts.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxNetworkMaxHosts.ToString(CultureInfo.InvariantCulture) + " hosts";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.NetworkMaxPortsPerTurn"/>. Pinned.</summary>
    public static readonly string NetworkMaxPortsRangeError =
        "must be " + AppSettingsData.MinNetworkMaxPorts.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxNetworkMaxPorts.ToString(CultureInfo.InvariantCulture) + " ports";

    public static string NetworkReachLabel(string name) => Markup.Escape(name.PadRight(13)) + Theme.DimMarkup(NetworkToolsReach.Describe(name));

    /// <summary>The value column of every admin field.</summary>
    public static string AdminFieldValue(SettingsField field, AppSettingsData data, string profileDirectory)
    {
        string home = Profiles.HomeOf(profileDirectory);
        return field switch
        {
            SettingsField.NetworkTools => OnOff(data.NetworkTools),
            SettingsField.NetworkToolsReach => string.IsNullOrWhiteSpace(data.NetworkToolsReach) ? NetworkToolsReach.Default : data.NetworkToolsReach,
            SettingsField.NetworkMaxHostsPerTurn => data.NetworkMaxHostsPerTurn.ToString(CultureInfo.InvariantCulture),
            SettingsField.NetworkMaxPortsPerTurn => data.NetworkMaxPortsPerTurn.ToString(CultureInfo.InvariantCulture),
            SettingsField.NetworkAllowedHosts => HostListValue(data.NetworkAllowedHosts) + (data.NetworkAllowedHosts.Count == 0 ? " (anything the reach admits)" : ""),
            SettingsField.NetworkDeniedHosts => HostListValue(data.NetworkDeniedHosts),
            SettingsField.NetworkCertificateStores => OnOff(data.NetworkCertificateStores),
            SettingsField.ServiceTools => OnOff(data.ServiceTools),
            SettingsField.ServiceWrites => OnOff(data.ServiceWrites),
            SettingsField.ServiceComputersOffered => ComputersOfferedValue(data.ServiceComputersOffered, ServiceConfigFile.Family.LoadCatalog(profileDirectory, home)),
            SettingsField.ServiceSetPassword or SettingsField.TaskSetPassword => ComputerSetPasswordLabel,
            SettingsField.ServiceAddComputer or SettingsField.TaskAddComputer => ComputerAddLabel,
            SettingsField.ServiceComputersProfile => ComputersFileLabel(ServiceConfigFile.Family, ServiceConfigFile.Family.ProfilePath(profileDirectory)),
            SettingsField.ServiceComputersGlobal => ComputersFileLabel(ServiceConfigFile.Family, ServiceConfigFile.Family.GlobalPath(home)),
            SettingsField.TaskTools => OnOff(data.TaskTools),
            SettingsField.TaskWrites => OnOff(data.TaskWrites),
            SettingsField.TaskComputersOffered => ComputersOfferedValue(data.TaskComputersOffered, TaskConfigFile.Family.LoadCatalog(profileDirectory, home)),
            SettingsField.TaskComputersProfile => ComputersFileLabel(TaskConfigFile.Family, TaskConfigFile.Family.ProfilePath(profileDirectory)),
            SettingsField.TaskComputersGlobal => ComputersFileLabel(TaskConfigFile.Family, TaskConfigFile.Family.GlobalPath(home)),
            SettingsField.DirectoryTools => OnOff(data.DirectoryTools),
            SettingsField.DirectoryDomainsOffered => DirectoryOfferedValue(data.DirectoryDomainsOffered, DirectoryConfigFile.LoadCatalog(profileDirectory, home, DomainJoin.Current), DomainJoin.Current is not null),
            SettingsField.DirectorySetPassword => ComputerSetPasswordLabel.Replace("computer", "domain", StringComparison.Ordinal),
            SettingsField.DirectoryAddDomain => DirectoryAddLabel,
            SettingsField.DirectoryDomainsProfile => DirectoryFileLabel(DirectoryConfigFile.ProfilePath(profileDirectory)),
            SettingsField.DirectoryDomainsGlobal => DirectoryFileLabel(DirectoryConfigFile.GlobalPath(home)),
            _ => "",
        };
    }

    /// <summary>Enter on an admin field that is not a toggle: the reach picker, a typed number, a host list, a checklist, a password, a wizard, a file.</summary>
    private async Task<bool> AdminEditAsync(SettingsField field, AppSettingsData saved, MenuPage page, int row, CancellationToken cancellationToken)
    {
        switch (field)
        {
            case SettingsField.NetworkToolsReach:
                return await PickNetworkReachAsync(saved, cancellationToken).ConfigureAwait(false);
            case SettingsField.NetworkMaxHostsPerTurn:
            case SettingsField.NetworkMaxPortsPerTurn:
                return await EditNetworkBudgetAsync(field, saved, page, row, cancellationToken).ConfigureAwait(false);
            case SettingsField.NetworkAllowedHosts:
                return await EditHostListAsync(field, d => d.NetworkAllowedHosts, (d, next) => d.NetworkAllowedHosts = next, cancellationToken).ConfigureAwait(false);
            case SettingsField.NetworkDeniedHosts:
                return await EditHostListAsync(field, d => d.NetworkDeniedHosts, (d, next) => d.NetworkDeniedHosts = next, cancellationToken).ConfigureAwait(false);
            case SettingsField.ServiceComputersOffered:
                return await EditComputersOfferedAsync(ServiceConfigFile.Family, field, d => d.ServiceComputersOffered, (d, next) => d.ServiceComputersOffered = next, cancellationToken).ConfigureAwait(false);
            case SettingsField.ServiceSetPassword:
                return await SetComputerPasswordAsync(ServiceConfigFile.Family, field, cancellationToken).ConfigureAwait(false);
            case SettingsField.ServiceAddComputer:
                return await AddComputerAsync(ServiceWizard, cancellationToken).ConfigureAwait(false);
            case SettingsField.ServiceComputersProfile:
            case SettingsField.ServiceComputersGlobal:
                OpenConfigFile(field == SettingsField.ServiceComputersProfile ? ServiceConfigFile.Family.ProfilePath(_settings.ProfileDirectory) : ServiceConfigFile.Family.GlobalPath(_settings.StorageDirectory),
                    ServiceConfigFile.Family.EnsureExists, ServiceConfigFile.FileName);
                return false;
            case SettingsField.TaskComputersOffered:
                return await EditComputersOfferedAsync(TaskConfigFile.Family, field, d => d.TaskComputersOffered, (d, next) => d.TaskComputersOffered = next, cancellationToken).ConfigureAwait(false);
            case SettingsField.TaskSetPassword:
                return await SetComputerPasswordAsync(TaskConfigFile.Family, field, cancellationToken).ConfigureAwait(false);
            case SettingsField.TaskAddComputer:
                return await AddComputerAsync(TaskWizard, cancellationToken).ConfigureAwait(false);
            case SettingsField.TaskComputersProfile:
            case SettingsField.TaskComputersGlobal:
                OpenConfigFile(field == SettingsField.TaskComputersProfile ? TaskConfigFile.Family.ProfilePath(_settings.ProfileDirectory) : TaskConfigFile.Family.GlobalPath(_settings.StorageDirectory),
                    TaskConfigFile.Family.EnsureExists, TaskConfigFile.FileName);
                return false;
            case SettingsField.DirectoryDomainsOffered:
                return await EditDirectoryOfferedAsync(cancellationToken).ConfigureAwait(false);
            case SettingsField.DirectorySetPassword:
                return await SetDirectoryPasswordAsync(cancellationToken).ConfigureAwait(false);
            case SettingsField.DirectoryAddDomain:
                return await AddDirectoryDomainAsync(cancellationToken).ConfigureAwait(false);
            case SettingsField.DirectoryDomainsProfile:
            case SettingsField.DirectoryDomainsGlobal:
                OpenConfigFile(field == SettingsField.DirectoryDomainsProfile ? DirectoryConfigFile.ProfilePath(_settings.ProfileDirectory) : DirectoryConfigFile.GlobalPath(_settings.StorageDirectory),
                    DirectoryConfigFile.EnsureExists, DirectoryConfigFile.FileName);
                return false;
            default:
                return Unchanged();
        }
    }

    /// <summary>The reach picker: same-as-web, internet, lan, both, each with its note, the saved one under the cursor.</summary>
    private async Task<bool> PickNetworkReachAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = NetworkToolsReach.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.NetworkToolsReach)), names.Select(NetworkReachLabel).ToList(), PickKeys);
        int current = Math.Max(0, names.ToList().IndexOf(saved.NetworkToolsReach?.Trim().ToLowerInvariant() ?? NetworkToolsReach.Default));
        if (await PickAsync(page, current, cancellationToken).ConfigureAwait(false) is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.NetworkToolsReach, d => d.NetworkToolsReach = name);
        return true;
    }

    /// <summary>A typed budget number, its range checked.</summary>
    private async Task<bool> EditNetworkBudgetAsync(SettingsField field, AppSettingsData saved, MenuPage page, int row, CancellationToken cancellationToken)
    {
        var result = await EditTextAsync(field, page, row, EditableValue(field, saved), allowEmpty: false, cancellationToken).ConfigureAwait(false);
        if (result is not InputResult.Submitted submitted)
        {
            return Unchanged();
        }

        bool hosts = field == SettingsField.NetworkMaxHostsPerTurn;
        int min = hosts ? AppSettingsData.MinNetworkMaxHosts : AppSettingsData.MinNetworkMaxPorts;
        int max = hosts ? AppSettingsData.MaxNetworkMaxHosts : AppSettingsData.MaxNetworkMaxPorts;
        if (!int.TryParse(submitted.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
        {
            Sink.Error($"{FieldName(field)} {(hosts ? NetworkMaxHostsRangeError : NetworkMaxPortsRangeError)}; keeping {EditableValue(field, saved)}.");
            return false;
        }

        Apply(field, d =>
        {
            if (hosts)
            {
                d.NetworkMaxHostsPerTurn = value;
            }
            else
            {
                d.NetworkMaxPortsPerTurn = value;
            }
        });
        return true;
    }

    /// <summary>
    /// A host list (2026-10-15, the plan's item 36: the forbidden strings' typed-list page): <see cref="AddHostRow"/> on top, each entry under it;
    /// a typed entry is read by <see cref="HostRules.Parse"/> and a bad CIDR or glob refused on entry; Enter on an entry removes it after a yes,
    /// and Delete too (2026-10-16, the user's ask; on the add row nothing). True when anything changed.
    /// </summary>
    private async Task<bool> EditHostListAsync(SettingsField field, Func<AppSettingsData, List<string>> get, Action<AppSettingsData, List<string>> set, CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        string title = FieldName(field);
        while (true)
        {
            var hosts = get(_settings.Current);
            IReadOnlyList<string> rows = [Markup.Escape(AddHostRow), .. hosts.Select(Markup.Escape)];
            var page = new MenuPage(Crumb(title), rows, HostListKeys);
            if (await PickListAsync(page, Math.Min(cursor, rows.Count - 1), cancellationToken).ConfigureAwait(false) is not { Row: var index } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = index;
            if (pick.Remove && index == 0)
            {
                continue;   // Delete on the add row: nothing to remove
            }

            if (index > 0)
            {
                string entry = hosts[index - 1];
                var removing = new MenuPage(RemoveHostQuestion(field, entry), ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
                if (await PickAsync(removing, 0, cancellationToken).ConfigureAwait(false) != 1)
                {
                    Sink.Notice(ChatScreen.KeptNotice);
                    continue;
                }

                Apply(field, d => set(d, get(d).Where(h => !string.Equals(h, entry, StringComparison.OrdinalIgnoreCase)).ToList()));
                Sink.Notice(HostRemovedNotice(field, entry));
                changed = true;
                continue;
            }

            InputResult result;
            if (_pane.Enabled)
            {
                result = await _pane.EditAsync(page with { Hint = EditKeys }, 0, _input, "", allowEmpty: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(title, EditKeys));
                result = await _input.ReadAsync("", remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true).ConfigureAwait(false);
            }

            string typed = result is InputResult.Submitted submitted ? submitted.Text.Trim() : "";
            if (typed.Length == 0)
            {
                continue;
            }

            if (HostRules.Parse(typed).Error is { } error)
            {
                Sink.Error(title + ": " + error + ".");
                continue;
            }

            if (hosts.Contains(typed, StringComparer.OrdinalIgnoreCase))
            {
                Sink.Notice(HostDuplicateNotice(field, typed));
                continue;
            }

            Apply(field, d => set(d, [.. get(d), typed]));
            Sink.Notice(HostAddedNotice(field, typed));
            changed = true;
        }
    }
}
