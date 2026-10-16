using System.Globalization;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Sql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The Directory menu of <c>/tools</c> › Admin (2026-10-15, the admin-tools plan): the switch, the domains offered (<c>current</c> first on a
/// joined PC), the masked password prompt for a runas domain, the domain wizard (a separate small one: domains are not computers) and the two
/// edit rows over <c>ad.json</c>.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The wizard's test (the screen sets it).</summary>
    public Func<DirectoryDomain, CancellationToken, Task<(bool Ok, string Text)>>? TestDirectoryDomain { get; set; }

    /// <summary>This PC's joined domain (the screen sets it; null when not joined or off Windows).</summary>
    public Func<string?> JoinedDomain { get; set; } = () => null;

    public const string DirectoryAddLabel = "Enter to start domain wizard";

    public static readonly IReadOnlyList<string> DirectoryWizardLabels = ["File", "Name", "Domain", "Server", "Base DN", "TLS", "Sign-in", "User", "Password store", "Password", "Detail", "Description"];

    public const string DirectoryWizardNameQuestion = "Its name: what the model passes as \"domain\".";
    public const string DirectoryWizardDomainQuestion = "The domain's DNS name (corp.example).";
    public const string DirectoryWizardServerQuestion = "A domain controller to ask (dc01.corp.example, localhost for a test container); empty for the domain's name.";
    public const string DirectoryWizardBaseQuestion = "Where to search (DC=corp,DC=example, or an OU); empty for the whole domain.";
    public const string DirectoryWizardTlsQuestion = "How to connect.";
    public const string DirectoryWizardAuthQuestion = "Who it is read as.";
    public const string DirectoryWizardDetailQuestion = "What the main chat's model reads (the bots and advisors always read redacted).";
    public const string DirectoryWizardCancelledNotice = "No domain added.";
    public const string DirectoryWizardDomainRequired = "A domain is required.";

    public static readonly IReadOnlyList<string> DirectoryWizardTlsRows = ["389   signed and sealed (the default)", "636   LDAPS (the server's certificate must be trusted here)"];

    public static readonly IReadOnlyList<string> DirectoryWizardDetailRows =
        ["redacted  phones, addresses and employee numbers left out (the default)", "full      every attribute the tools read, for the main chat"];

    public static string DirectoryFileLabel(string path)
    {
        var (domains, problems) = DirectoryConfigFile.Load(path);
        string count = domains.Count == 0 ? "(none)" : SqlText.Count(domains.Count, "domain");
        return (problems.Count == 0 ? count : count + ", " + SqlText.Count(problems.Count, "problem")) + " · Enter edits ad.json";
    }

    public static string DirectoryOfferedValue(IReadOnlyList<string>? offered, DirectoryCatalog loaded, bool joined)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        int kept = loaded.Offered(offered, joined).Domains.Count;
        return (kept == 0 ? "none" : kept.ToString(CultureInfo.InvariantCulture)) + " of " + loaded.Domains.Count.ToString(CultureInfo.InvariantCulture);
    }

    public static string DirectoryNote(DirectoryDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var c = domain.Config;
        return string.Join(" · ", domain.DomainName, domain.Server + ":" + domain.Port.ToString(CultureInfo.InvariantCulture), c.IsRunAs ? "runas " + c.User?.Trim() : "as you", c.Full ? "full" : "redacted");
    }

    private DirectoryCatalog DirectoryLoaded() => DirectoryConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory, JoinedDomain());

    /// <summary><c>Directory domains offered</c>: every domain, <c>current</c> first when joined; Enter or Space flips one until ESC.</summary>
    private async Task<bool> EditDirectoryOfferedAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var loaded = DirectoryLoaded();
            bool joined = JoinedDomain() is not null;
            if (loaded.Domains.Count == 0)
            {
                Sink.Error(DirectoryText.NoDomainReason[..1].ToUpperInvariant() + DirectoryText.NoDomainReason[1..] + "; add one with " + FieldName(SettingsField.DirectoryAddDomain) + ".");
                return changed;
            }

            var saved = _settings.Current.DirectoryDomainsOffered;
            var on = loaded.Offered(saved, joined).Domains.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var domains = loaded.Domains.Where(d => d.IsCurrent).Concat(ByName(loaded.Domains.Where(d => !d.IsCurrent), d => d.Name)).ToList();
            int width = domains.Max(d => d.Name.Length) + 2;
            var rows = domains.Select(d => Markup.Escape((on.Contains(d.Name) ? "[x] " : "[ ] ") + d.Name.PadRight(width)) + Theme.DimMarkup(DirectoryNote(d))).ToList();
            var page = new MenuPage(Crumb(FieldName(SettingsField.DirectoryDomainsOffered)), rows, ToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, domains.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = domains[pick.Row].Name;
            var next = pick.Button == SelectAllIndex ? domains.Select(d => d.Name).ToList()
                : pick.Button == SelectNoneIndex ? []
                : domains.Select(d => d.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count == on.Count && next.All(on.Contains))
            {
                continue;
            }

            Apply(SettingsField.DirectoryDomainsOffered, d => d.DirectoryDomainsOffered = next);
            changed = true;
        }
    }

    private async Task<bool> SetDirectoryPasswordAsync(CancellationToken cancellationToken)
    {
        var domains = ByName(DirectoryLoaded().Domains.Where(d => d.Config.IsRunAs), d => d.Name);
        if (domains.Count == 0)
        {
            Sink.Error(ComputerNoPasswordComputers(DirectoryConfigFile.FileName).Replace("computer", "domain", StringComparison.Ordinal));
            return false;
        }

        var rows = domains.Select(d => Markup.Escape($"{d.Name}  ({d.DomainName}, runas {d.Config.User?.Trim()} · {(d.Config.InCredentialManager ? SqlText.CredentialStore : "encrypted in ad.json")})")).ToList();
        var page = new MenuPage(Crumb(FieldName(SettingsField.DirectorySetPassword)), rows, PickKeys);
        if (await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { } index)
        {
            return Unchanged();
        }

        InputResult result = _pane.Enabled
            ? await _pane.EditAsync(page with { Hint = EditKeys }, index, _input, "", allowEmpty: false, cancellationToken, mask: true).ConfigureAwait(false)
            : await _input.ReadAsync("", remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true, mask: true).ConfigureAwait(false);
        if (result is not InputResult.Submitted { Text.Length: > 0 } submitted)
        {
            return Unchanged();
        }

        var (saved, notice) = DirectoryConfigFile.SavePassword(domains[index], submitted.Text);
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

    // ── The domain wizard ───────────────────────────────────────────────────

    private sealed class DirectoryDraft
    {
        public bool Global { get; set; }

        public string Name { get; set; } = "";

        public string Password { get; set; } = "";

        public string? Original { get; init; }

        public string? OriginalTarget { get; init; }

        public bool PasswordKept { get; set; }

        public DirectoryDomainConfig Config { get; init; } = new();
    }

    private string DirectoryWizardPath(bool global) => global ? DirectoryConfigFile.GlobalPath(_settings.StorageDirectory) : DirectoryConfigFile.ProfilePath(_settings.ProfileDirectory);

    private static bool DirectoryWizardAsks(int step, DirectoryDraft draft) =>
        step == 0 ? draft.Original is null : step is 7 or 8 or 9 ? draft.Config.IsRunAs : true;

    private string DirectoryWizardValue(int step, DirectoryDraft draft)
    {
        var c = draft.Config;
        if (step > 1 && !DirectoryWizardAsks(step, draft))
        {
            return SqlWizardNotNeeded;
        }

        static string OrUnset(string? text) => string.IsNullOrWhiteSpace(text) ? SqlWizardUnset : text.Trim();
        return step switch
        {
            0 => SqlWizardFileRow(draft.Global, DirectoryWizardPath(draft.Global)),
            1 => OrUnset(draft.Name),
            2 => OrUnset(c.Domain),
            3 => string.IsNullOrWhiteSpace(c.Server) ? "(the domain's name)" : c.Server.Trim(),
            4 => string.IsNullOrWhiteSpace(c.BaseDn) ? "(the whole domain)" : c.BaseDn.Trim(),
            5 => c.UsesTls ? "636 LDAPS" : "389 signed and sealed",
            6 => c.IsRunAs ? DirectoryDomainConfig.RunAsAuth : DirectoryDomainConfig.WindowsAuth,
            7 => OrUnset(c.User),
            8 => c.InCredentialManager ? SqlConnectionConfig.CredmanStore + " (" + c.CredentialTarget(draft.Name.Length > 0 ? draft.Name : "<name>") + ")" : SqlConnectionConfig.FileStore,
            9 => draft.Password.Length > 0 ? (draft.PasswordKept ? SqlWizardMaskedKept : SqlWizardMasked) : SqlWizardUnset,
            10 => c.Full ? "full" : "redacted",
            _ => OrUnset(c.Description),
        };
    }

    private List<string> DirectoryWizardRows(DirectoryDraft draft)
    {
        int width = DirectoryWizardLabels.Max(l => l.Length) + 2;
        return DirectoryWizardLabels.Select((label, i) => Markup.Escape(label.PadRight(width) + DirectoryWizardValue(i, draft))).ToList();
    }

    /// <summary><c>Directory add/edit domain</c>: the entries' first page, then the steps and the summary. True when a setting changed.</summary>
    private async Task<bool> AddDirectoryDomainAsync(CancellationToken cancellationToken)
    {
        string title = Crumb(FieldName(SettingsField.DirectoryAddDomain));
        while (true)
        {
            var entries = ConnectionWizardEntries(DirectoryWizardPath(false), DirectoryWizardPath(true), path => DirectoryConfigFile.Load(path).Domains, d => d.Name);
            // With no domain saved there is nothing to pick: straight to a new one, as the SQL wizard does (2026-10-15, the user's report:
            // the pick page measured an empty list and the app crashed).
            int picked = -1;
            if (entries.Count > 0)
            {
                var rows = entries.Select(e => (e.Named.Name, e.Global)).ToList();
                if (await ConnectionWizardPickAsync(title, "domain", rows, DirectoryWizardPath, cancellationToken).ConfigureAwait(false) is not { } chosen)
                {
                    Sink.Notice(DirectoryWizardCancelledNotice);
                    return false;
                }

                picked = chosen;
            }

            var draft = new DirectoryDraft();
            if (picked >= 0)
            {
                var named = entries[picked].Named;
                draft = new DirectoryDraft
                {
                    Global = entries[picked].Global,
                    Name = named.Name,
                    Original = named.Name,
                    OriginalTarget = named.Config.IsRunAs && named.Config.InCredentialManager && named.Config.Credential is null ? named.Config.CredentialTarget(named.Name) : null,
                    Config = CloneEntry(named.Config, DirectoryJsonContext.Default.DirectoryDomainConfig, x => x.Password = null),
                };
                if (named.Config.IsRunAs)
                {
                    draft.Password = StoredPassword(DirectoryConfigFile.ResolvePassword(named));
                    draft.PasswordKept = draft.Password.Length > 0;
                }
            }

            if (await RunDirectoryWizardAsync(draft, title, cancellationToken).ConfigureAwait(false) is { } changed)
            {
                return changed;
            }

            if (entries.Count == 0)
            {
                Sink.Notice(DirectoryWizardCancelledNotice);
                return false;
            }
        }
    }

    private async Task<bool?> RunDirectoryWizardAsync(DirectoryDraft draft, string title, CancellationToken cancellationToken)
    {
        const int summary = 12;
        int step = draft.Original is null ? 0 : summary;
        bool fromSummary = draft.Original is not null;
        while (true)
        {
            if (step == summary)
            {
                var (done, changed, edit) = await DirectoryWizardSummaryAsync(draft, title, cancellationToken).ConfigureAwait(false);
                if (done)
                {
                    return changed;
                }

                if (edit is { } target)
                {
                    step = target;
                    fromSummary = true;
                    continue;
                }

                if (draft.Original is not null)
                {
                    return null;
                }

                step = summary - 1;
                continue;
            }

            if (!DirectoryWizardAsks(step, draft))
            {
                step++;
                continue;
            }

            if (await DirectoryWizardStepAsync(step, draft, title, cancellationToken).ConfigureAwait(false))
            {
                step = fromSummary ? summary : step + 1;
                fromSummary = fromSummary && step != summary;
                continue;
            }

            if (fromSummary)
            {
                step = summary;
                fromSummary = false;
                continue;
            }

            do
            {
                step--;
            }
            while (step >= 0 && !DirectoryWizardAsks(step, draft));

            if (step < 0)
            {
                return null;
            }
        }
    }

    private async Task<bool> DirectoryWizardStepAsync(int step, DirectoryDraft draft, string title, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        switch (step)
        {
            case 0:
            {
                string[] rows = [SqlWizardFileRow(false, DirectoryWizardPath(false)), SqlWizardFileRow(true, DirectoryWizardPath(true))];
                if (await DirectoryWizardPickAsync(title, ComputerWizardFileQuestion, rows, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case 1:
                return await DirectoryWizardTypeAsync(step, draft, title, DirectoryWizardNameQuestion, draft.Name, false, false, text =>
                {
                    string name = text.Trim();
                    if (name.Length == 0)
                    {
                        return SqlWizardNameRequired;
                    }

                    if (name.Any(char.IsWhiteSpace) || name.Contains(':', StringComparison.Ordinal))
                    {
                        return SqlWizardNameSpaces;
                    }

                    if (string.Equals(name, DirectoryText.CurrentName, StringComparison.OrdinalIgnoreCase))
                    {
                        return DirectoryText.CurrentReserved;
                    }

                    string path = DirectoryWizardPath(draft.Global);
                    if (DirectoryConfigFile.Load(path).Domains.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Name, draft.Original, StringComparison.OrdinalIgnoreCase)))
                    {
                        return SqlWizardNameTaken(name, path);
                    }

                    draft.Name = name;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case 2:
                return await DirectoryWizardTypeAsync(step, draft, title, DirectoryWizardDomainQuestion, c.Domain ?? "", false, false, text =>
                {
                    string domain = text.Trim().TrimEnd('.');
                    if (domain.Length == 0)
                    {
                        return DirectoryWizardDomainRequired;
                    }

                    c.Domain = domain;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case 3:
                return await DirectoryWizardTypeAsync(step, draft, title, DirectoryWizardServerQuestion, c.Server ?? "", true, false, text =>
                {
                    c.Server = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case 4:
                return await DirectoryWizardTypeAsync(step, draft, title, DirectoryWizardBaseQuestion, c.BaseDn ?? "", true, false, text =>
                {
                    string baseDn = text.Trim();
                    if (baseDn.Length > 0 && !baseDn.Contains('=', StringComparison.Ordinal))
                    {
                        return DirectoryText.BadBaseDn(baseDn);
                    }

                    c.BaseDn = baseDn.Length == 0 ? null : baseDn;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case 5:
            {
                if (await DirectoryWizardPickAsync(title, DirectoryWizardTlsQuestion, DirectoryWizardTlsRows, c.UsesTls ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Tls = picked == 1 ? true : null;
                return true;
            }

            case 6:
            {
                int current = c.IsRunAs ? 1 : 0;
                if (await DirectoryWizardPickAsync(title, DirectoryWizardAuthQuestion, ComputerWizardAuthRows, current, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                if (picked != current)
                {
                    c.User = null;
                    draft.Password = "";
                }

                c.Auth = picked == 1 ? DirectoryDomainConfig.RunAsAuth : null;
                return true;
            }

            case 7:
                return await DirectoryWizardTypeAsync(step, draft, title, ComputerWizardUserQuestion, c.User ?? "", false, false, text =>
                {
                    string user = text.Trim();
                    if (WindowsCredentials.SplitAccount(user) is null)
                    {
                        return user.Length == 0 ? SqlWizardUserRequired : SqlText.RunAsNeedsDomain(user);
                    }

                    c.User = user;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case 8:
            {
                if (await DirectoryWizardPickAsync(title, SqlWizardStoreQuestion, SqlText.StoreRows(DirectoryConfigFile.FileName), c.InCredentialManager ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.PasswordStore = picked == 1 ? SqlConnectionConfig.CredmanStore : SqlConnectionConfig.FileStore;
                return true;
            }

            case 9:
                return await DirectoryWizardTypeAsync(step, draft, title, SqlWizardPasswordQuestion, draft.Password, false, true, text =>
                {
                    if (text.Length == 0)
                    {
                        return SqlWizardPasswordRequired;
                    }

                    draft.Password = text;
                    draft.PasswordKept = false;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case 10:
            {
                if (await DirectoryWizardPickAsync(title, DirectoryWizardDetailQuestion, DirectoryWizardDetailRows, c.Full ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Detail = picked == 1 ? DirectoryDomainConfig.FullDetail : null;
                return true;
            }

            default:
                return await DirectoryWizardTypeAsync(step, draft, title, ComputerWizardDescriptionQuestion, c.Description ?? "", true, false, text =>
                {
                    c.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<int?> DirectoryWizardPickAsync(string title, string question, IReadOnlyList<string> choices, int cursor, CancellationToken cancellationToken)
    {
        var page = new MenuPage(title, choices.Select(Markup.Escape).ToList(), PickKeys) { Caption = question };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        return PickAsync(page, cursor, cancellationToken);
    }

    private async Task<bool> DirectoryWizardTypeAsync(int step, DirectoryDraft draft, string title, string question, string initial, bool allowEmpty, bool mask, Func<string, string?> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(title, DirectoryWizardRows(draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, step, _input, initial, allowEmpty, cancellationToken, mask).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(DirectoryWizardLabels[step] + " · " + question, EditKeys));
                result = await _input.ReadAsync(initial, remember: false, allowEmpty: allowEmpty, cancellationToken: cancellationToken, escapeCancels: true, mask: mask).ConfigureAwait(false);
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
            initial = mask ? "" : submitted.Text;
        }
    }

    private async Task<(bool Done, bool Changed, int? Edit)> DirectoryWizardSummaryAsync(DirectoryDraft draft, string title, CancellationToken cancellationToken)
    {
        int cursor = 0;
        while (true)
        {
            var actions = new List<string> { SqlWizardSaveOfferedRow, ComputerWizardSaveHiddenRow, "Test the domain", SqlWizardCancelRow };
            var rows = actions.Select(Markup.Escape).Concat(DirectoryWizardRows(draft)).ToList();
            var page = new MenuPage(title, rows, PickKeys) { Caption = ComputerWizardSummaryCaption.Replace("computer", "domain", StringComparison.Ordinal) };
            if (await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                return (false, false, null);
            }

            cursor = picked;
            if (picked >= actions.Count)
            {
                int edit = picked - actions.Count;
                if (edit > 0 && DirectoryWizardAsks(edit, draft))
                {
                    return (false, false, edit);
                }

                continue;
            }

            if (picked == 2)
            {
                await DirectoryWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == 3)
            {
                Sink.Notice(draft.Original is null ? DirectoryWizardCancelledNotice : ConnectionWizardUnchangedNotice);
                return (true, false, null);
            }

            if (DirectoryWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    private async Task DirectoryWizardTestAsync(DirectoryDraft draft, CancellationToken cancellationToken)
    {
        var copy = CloneEntry(draft.Config, DirectoryJsonContext.Default.DirectoryDomainConfig, _ => { });
        if (copy.IsRunAs)
        {
            copy.Password = draft.Password;
            copy.PasswordStore = SqlConnectionConfig.FileStore;
            copy.Credential = null;
        }

        if (copy.Problem(draft.Name.Length > 0 ? draft.Name : "draft") is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        if (TestDirectoryDomain is not { } test)
        {
            Sink.Error(DirectoryText.NoneHere.Replace("Error: ", "", StringComparison.Ordinal));
            return;
        }

        var (ok, text) = await test(new DirectoryDomain(draft.Name, copy, DirectoryWizardPath(draft.Global)), cancellationToken).ConfigureAwait(false);
        if (ok)
        {
            Sink.Notice(text);
        }
        else
        {
            Sink.Error(text);
        }
    }

    private bool? DirectoryWizardSave(DirectoryDraft draft, bool offer)
    {
        var c = draft.Config;
        if (!c.IsRunAs)
        {
            c.User = null;
            c.PasswordStore = null;
            c.Auth = null;
        }

        string? missing = draft.Name.Length == 0 ? DirectoryWizardLabels[1] : string.IsNullOrWhiteSpace(c.Domain) ? DirectoryWizardLabels[2]
            : c.IsRunAs && string.IsNullOrWhiteSpace(c.User) ? DirectoryWizardLabels[7] : c.IsRunAs && draft.Password.Length == 0 ? DirectoryWizardLabels[9] : null;
        string? problem = missing is not null ? missing + " is not set" : c.Problem(draft.Name);
        if (problem is not null)
        {
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, problem) : SqlText.ConnectionChangeFailed(draft.Name, problem));
            return null;
        }

        string path = DirectoryWizardPath(draft.Global);
        if ((draft.Original is { } original ? DirectoryConfigFile.ReplaceDomain(path, original, draft.Name, c) : DirectoryConfigFile.AddDomain(path, draft.Name, c)) is { } error)
        {
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, error) : SqlText.ConnectionChangeFailed(draft.Name, error));
            return null;
        }

        Sink.Notice(draft.Original is null ? SqlText.ConnectionAdded(draft.Name, path) : SqlText.ConnectionChanged(draft.Name, path));
        bool saved = true;
        if (c.IsRunAs)
        {
            (saved, string notice) = DirectoryConfigFile.SavePassword(new DirectoryDomain(draft.Name, c, path), draft.Password);
            if (saved)
            {
                Sink.Notice(notice);
            }
            else
            {
                Sink.Error(notice);
            }
        }

        if (saved)
        {
            ForgetOldCredential(draft.OriginalTarget, c.IsRunAs && c.InCredentialManager ? c.CredentialTarget(draft.Name) : null);
        }

        return ApplyOfferedAfterSave(SettingsField.DirectoryDomainsOffered, DirectoryCatalog.OfferedNames(_settings.Current.DirectoryDomainsOffered, JoinedDomain() is not null), draft.Original, draft.Name, offer, (d, next) => d.DirectoryDomainsOffered = next);
    }
}
