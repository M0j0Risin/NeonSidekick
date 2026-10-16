using System.Text.Json.Serialization.Metadata;
using NeonSidekick.Computers;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Tasks;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// What one family's computer wizard asks beyond the shared steps (2026-10-15, the admin-tools plan): its file and settings, its visible list
/// (<c>services</c>, <c>folders</c>), its guard (the services' <c>protected</c> list, the tasks' <c>privileged</c> flag), and its test.
/// </summary>
internal sealed record ComputerWizardSpec<TConfig>(
    ComputerFamily<TConfig> Family,
    SettingsField AddField,
    SettingsField OfferedField,
    Func<AppSettingsData, List<string>?> Offered,
    Action<AppSettingsData, List<string>> SetOffered,
    JsonTypeInfo<TConfig> Info,
    string VisibleLabel,
    string VisibleQuestion,
    Func<TConfig, List<string>?> GetVisible,
    Action<TConfig, List<string>?> SetVisible,
    string ControlQuestion,
    string GuardLabel,
    string GuardQuestion,
    Func<TConfig, List<string>?>? GetGuardList,
    Action<TConfig, List<string>?>? SetGuardList,
    Func<TConfig, bool>? GetGuardFlag,
    Action<TConfig, bool>? SetGuardFlag,
    Func<Computer<TConfig>, CancellationToken, Task<(bool Ok, string Text)>>? Test,
    string NoTest)
    where TConfig : ComputerConfig, new();

/// <summary>
/// <c>Service add/edit computer</c> and <c>Task add/edit computer</c> (2026-10-15, the admin-tools plan: the event-log wizard made one wizard
/// over a <see cref="ComputerWizardSpec{TConfig}"/>): one page per choice — which file, the name, the host, the sign-in, the account, its
/// store, the password, the access, the visible list, and under <c>readwrite</c> the control list, the allowed actions and the family's guard —
/// then the detail and the description; a summary that tests the unsaved draft and saves it. <c>local</c> (this PC) is listed whether a file
/// holds it or not; its walk skips the host and the sign-in. ESC steps back; Enter on a summary row changes that choice and comes back.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The value column of an <c>… add/edit computer</c> row. Pinned.</summary>
    public const string ComputerAddLabel = "Enter to start computer wizard";

    internal enum ComputerWizardStep
    {
        File,
        Name,
        Host,
        Auth,
        User,
        Store,
        Password,
        Access,
        Visible,
        Control,
        Actions,
        Guard,
        Detail,
        Description,
        Summary,
    }

    /// <summary>The wizard's row labels, the visible list's and the guard's from the spec. Pinned (the shared ones).</summary>
    public static IReadOnlyList<string> ComputerWizardLabels(string visible, string guard) =>
        ["File", "Name", "Host", "Sign-in", "User", "Password store", "Password", "Access", visible, "Control", "Allowed actions", guard, "Detail", "Description"];

    public const string ComputerWizardFileQuestion = "Which file: this profile's or the global one?";
    public const string ComputerWizardNameQuestion = "Its name: what the model passes as \"computer\".";
    public const string ComputerWizardHostQuestion = "The computer's name or address (app01.corp.example, 192.168.1.40).";
    public const string ComputerWizardAuthQuestion = "Who it is reached as.";
    public const string ComputerWizardUserQuestion = "The account: DOMAIN\\name or name@domain (lab-box\\admin for the machine's own account).";
    public const string ComputerWizardAccessQuestion = "Whether anything may be changed here (each change still needs the writes switch, the lists below and your yes).";
    public const string ComputerWizardControlQuestion = "What may be changed, joined by commas (* any run); empty for nothing.";
    public const string ComputerWizardActionsQuestion = "The actions allowed, joined by commas; all (or empty) for every one, none for none.";
    public const string ComputerWizardDetailQuestion = "What the main chat's model reads here (the bots and advisors always read redacted).";
    public const string ComputerWizardDescriptionQuestion = "What the computer is, in your words (optional): the model reads it to pick one.";
    public const string ComputerWizardSummaryCaption = "Check the computer: Test reads it under its account without saving; Enter on a row below changes it.";
    public const string ComputerWizardSaveHiddenRow = "Save, hidden from the model until ticked";
    public const string ComputerWizardTestRow = "Test the computer";
    public const string ComputerWizardHostRequired = "A host is required.";
    public const string ComputerWizardCancelledNotice = "No computer added.";
    public const string ComputerWizardLocalRow = "local  (this PC, built in)";

    public static readonly IReadOnlyList<string> ComputerWizardAuthRows = ["windows  as you (no password)", "runas    as another Windows account"];

    public static readonly IReadOnlyList<string> ComputerWizardAccessRows = ["read       nothing is changed here (the default)", "readwrite  changes allowed by the lists below"];

    public static readonly IReadOnlyList<string> ComputerWizardDetailRows =
        ["redacted  secret-like values in paths and commands hidden from the model (the default)", "full      as they are, for the main chat"];

    public static readonly IReadOnlyList<string> ComputerWizardPrivilegedRows =
        ["no   run and end never reach a SYSTEM, service-account, group or highest-privilege task (the default)", "yes  run and end may reach them"];

    /// <summary>The services' wizard spec.</summary>
    private ComputerWizardSpec<ServiceComputerConfig> ServiceWizard => new(
        ServiceConfigFile.Family, SettingsField.ServiceAddComputer, SettingsField.ServiceComputersOffered,
        d => d.ServiceComputersOffered, (d, next) => d.ServiceComputersOffered = next, ServiceJsonContext.Default.ServiceComputerConfig,
        "Services", "The services visible here, joined by commas (* any run, e.g. MSSQL*); empty for all.", c => c.Services, (c, v) => c.Services = v,
        ComputerWizardControlQuestion,
        "Protected", "Services never stopped, restarted or retyped here, over the app's own list, joined by commas; empty for the app's alone.",
        c => c.Protected, (c, v) => c.Protected = v, null, null,
        TestServiceComputer, ServiceText.NoneHere.Replace("Error: ", "", StringComparison.Ordinal));

    /// <summary>The tasks' wizard spec.</summary>
    private ComputerWizardSpec<TaskComputerConfig> TaskWizard => new(
        TaskConfigFile.Family, SettingsField.TaskAddComputer, SettingsField.TaskComputersOffered,
        d => d.TaskComputersOffered, (d, next) => d.TaskComputersOffered = next, TaskJsonContext.Default.TaskComputerConfig,
        "Folders", "The folders visible here, joined by commas (\\ the root, \\Backup\\* a folder's tasks); empty for all.", c => c.Folders, (c, v) => c.Folders = v,
        "The task paths that may be changed, joined by commas (\\Backup\\*; Windows' own only by a pattern starting \\Microsoft\\); empty for nothing.",
        "Privileged", "Whether run and end may reach a task that runs as SYSTEM, a service account, a group or with highest privileges.",
        null, null, c => c.AllowsPrivileged, (c, v) => c.Privileged = v ? true : null,
        TestTaskComputer, TaskText.NoneHere.Replace("Error: ", "", StringComparison.Ordinal));

    /// <summary>What the wizard has so far.</summary>
    private sealed class ComputerDraft<TConfig> where TConfig : ComputerConfig, new()
    {
        public bool Global { get; set; }

        public string Name { get; set; } = "";

        public string Password { get; set; } = "";

        public string? Original { get; init; }

        public string? OriginalTarget { get; init; }

        public bool PasswordKept { get; set; }

        public bool IsLocal => string.Equals(Name, ComputerConfig.LocalName, StringComparison.OrdinalIgnoreCase);

        public TConfig Config { get; init; } = new();
    }

    private string ComputerWizardPath<TConfig>(ComputerWizardSpec<TConfig> spec, bool global) where TConfig : ComputerConfig, new() =>
        global ? spec.Family.GlobalPath(_settings.StorageDirectory) : spec.Family.ProfilePath(_settings.ProfileDirectory);

    /// <summary>Whether <paramref name="step"/> is asked: <c>local</c> never its host or sign-in; the account and password only under runas; control, actions and guard only under readwrite; the file only when new.</summary>
    private static bool ComputerWizardAsks<TConfig>(ComputerWizardStep step, ComputerDraft<TConfig> draft) where TConfig : ComputerConfig, new()
    {
        if (step == ComputerWizardStep.File)
        {
            return draft.Original is null;
        }

        if (draft.IsLocal && step is ComputerWizardStep.Name or ComputerWizardStep.Host or ComputerWizardStep.Auth or ComputerWizardStep.User or ComputerWizardStep.Store or ComputerWizardStep.Password)
        {
            return false;
        }

        if (step is ComputerWizardStep.User or ComputerWizardStep.Store or ComputerWizardStep.Password)
        {
            return draft.Config.NeedsPassword;
        }

        return step is not (ComputerWizardStep.Control or ComputerWizardStep.Actions or ComputerWizardStep.Guard) || draft.Config.ReadWrite;
    }

    private static ComputerWizardStep? ComputerWizardMissing<TConfig>(ComputerDraft<TConfig> draft) where TConfig : ComputerConfig, new()
    {
        if (draft.Name.Length == 0)
        {
            return ComputerWizardStep.Name;
        }

        if (draft.IsLocal)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(draft.Config.Host))
        {
            return ComputerWizardStep.Host;
        }

        if (draft.Config.NeedsPassword && string.IsNullOrWhiteSpace(draft.Config.User))
        {
            return ComputerWizardStep.User;
        }

        return draft.Config.NeedsPassword && draft.Password.Length == 0 ? ComputerWizardStep.Password : null;
    }

    private string ComputerWizardValue<TConfig>(ComputerWizardSpec<TConfig> spec, ComputerWizardStep step, ComputerDraft<TConfig> draft) where TConfig : ComputerConfig, new()
    {
        var c = draft.Config;
        if (step != ComputerWizardStep.File && step != ComputerWizardStep.Name && !ComputerWizardAsks(step, draft))
        {
            return SqlWizardNotNeeded;
        }

        static string OrUnset(string? text) => string.IsNullOrWhiteSpace(text) ? SqlWizardUnset : text.Trim();
        static string List(List<string>? items, string absent) => items is null ? absent : items.Count == 0 ? "(none)" : string.Join(", ", items);
        return step switch
        {
            ComputerWizardStep.File => SqlWizardFileRow(draft.Global, ComputerWizardPath(spec, draft.Global)),
            ComputerWizardStep.Name => OrUnset(draft.Name),
            ComputerWizardStep.Host => OrUnset(c.Host),
            ComputerWizardStep.Auth => c.IsRunAs ? ComputerConfig.RunAsAuth : ComputerConfig.WindowsAuth,
            ComputerWizardStep.User => OrUnset(c.User),
            ComputerWizardStep.Store => c.InCredentialManager ? ComputerConfig.CredmanStore + " (" + c.CredentialTarget(spec.Family.Info, draft.Name.Length > 0 ? draft.Name : "<name>") + ")" : ComputerConfig.FileStore,
            ComputerWizardStep.Password => draft.Password.Length > 0 ? (draft.PasswordKept ? SqlWizardMaskedKept : SqlWizardMasked) : SqlWizardUnset,
            ComputerWizardStep.Access => c.ReadWrite ? ConnectionAccess.ReadWriteAccess : ConnectionAccess.ReadAccess,
            ComputerWizardStep.Visible => List(spec.GetVisible(c), "(all)"),
            ComputerWizardStep.Control => List(c.Control, "(nothing)"),
            ComputerWizardStep.Actions => List(c.ControlActions, "(all)"),
            ComputerWizardStep.Guard => spec.GetGuardList is { } guard ? List(guard(c), "(the app's alone)") : spec.GetGuardFlag!(c) ? "yes" : "no",
            ComputerWizardStep.Detail => c.DetailWord,
            _ => OrUnset(c.Description),
        };
    }

    private List<string> ComputerWizardRows<TConfig>(ComputerWizardSpec<TConfig> spec, ComputerDraft<TConfig> draft) where TConfig : ComputerConfig, new()
    {
        var labels = ComputerWizardLabels(spec.VisibleLabel, spec.GuardLabel);
        int width = labels.Max(l => l.Length) + 2;
        return labels.Select((label, i) => Markup.Escape(label.PadRight(width) + ComputerWizardValue(spec, (ComputerWizardStep)i, draft))).ToList();
    }

    /// <summary>The wizard: the first page of the entries (<c>local</c> among them), then its steps. True when a setting changed.</summary>
    private async Task<bool> AddComputerAsync<TConfig>(ComputerWizardSpec<TConfig> spec, CancellationToken cancellationToken) where TConfig : ComputerConfig, new()
    {
        string title = Crumb(FieldName(spec.AddField));
        while (true)
        {
            var entries = ConnectionWizardEntries(ComputerWizardPath(spec, false), ComputerWizardPath(spec, true), path => spec.Family.Load(path).Computers, c => c.Name);
            bool localListed = entries.Any(e => e.Named.IsLocal);
            var rows = new List<(string Name, bool Global)>();
            if (!localListed)
            {
                rows.Add((ComputerConfig.LocalName, false));
            }

            rows.AddRange(entries.Select(e => (e.Named.Name, e.Global)));
            var draft = new ComputerDraft<TConfig>();
            if (await ConnectionWizardPickAsync(title, "computer", rows, global => ComputerWizardPath(spec, global), cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                Sink.Notice(ComputerWizardCancelledNotice);
                return false;
            }

            if (picked >= 0)
            {
                int at = localListed ? picked : picked - 1;
                draft = at < 0 ? new ComputerDraft<TConfig> { Name = ComputerConfig.LocalName } : ComputerWizardDraftFrom(spec, entries[at].Named, entries[at].Global);
            }

            if (await RunComputerWizardAsync(spec, draft, cancellationToken).ConfigureAwait(false) is { } changed)
            {
                return changed;
            }
        }
    }

    private ComputerDraft<TConfig> ComputerWizardDraftFrom<TConfig>(ComputerWizardSpec<TConfig> spec, Computer<TConfig> named, bool global) where TConfig : ComputerConfig, new()
    {
        var c = named.Config;
        var draft = new ComputerDraft<TConfig>
        {
            Global = global,
            Name = named.Name,
            Original = named.Name,
            OriginalTarget = c.NeedsPassword && c.InCredentialManager && c.Credential is null ? c.CredentialTarget(spec.Family.Info, named.Name) : null,
            Config = CloneEntry(c, spec.Info, x => x.Password = null),
        };
        if (c.NeedsPassword)
        {
            draft.Password = StoredPassword(spec.Family.ResolvePassword(named));
            draft.PasswordKept = draft.Password.Length > 0;
        }

        return draft;
    }

    private async Task<bool?> RunComputerWizardAsync<TConfig>(ComputerWizardSpec<TConfig> spec, ComputerDraft<TConfig> draft, CancellationToken cancellationToken) where TConfig : ComputerConfig, new()
    {
        var step = draft.Original is null ? ComputerWizardStep.File : ComputerWizardMissing(draft) ?? ComputerWizardStep.Summary;
        bool fromSummary = draft.Original is not null;
        while (true)
        {
            if (step == ComputerWizardStep.Summary)
            {
                var (done, changed, edit) = await ComputerWizardSummaryAsync(spec, draft, cancellationToken).ConfigureAwait(false);
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

                step = ComputerWizardStep.Description;
                continue;
            }

            if (!ComputerWizardAsks(step, draft))
            {
                step++;
                continue;
            }

            if (await ComputerWizardStepAsync(spec, step, draft, cancellationToken).ConfigureAwait(false))
            {
                step = fromSummary ? ComputerWizardMissing(draft) ?? ComputerWizardStep.Summary : step + 1;
                fromSummary = fromSummary && step != ComputerWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = ComputerWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            do
            {
                step--;
            }
            while (step >= ComputerWizardStep.File && !ComputerWizardAsks(step, draft));

            if (step < ComputerWizardStep.File)
            {
                return null;
            }
        }
    }

    private async Task<bool> ComputerWizardStepAsync<TConfig>(ComputerWizardSpec<TConfig> spec, ComputerWizardStep step, ComputerDraft<TConfig> draft, CancellationToken cancellationToken) where TConfig : ComputerConfig, new()
    {
        var c = draft.Config;
        switch (step)
        {
            case ComputerWizardStep.File:
            {
                string[] rows = [SqlWizardFileRow(false, ComputerWizardPath(spec, false)), SqlWizardFileRow(true, ComputerWizardPath(spec, true))];
                if (await ComputerWizardPickAsync(spec, ComputerWizardFileQuestion, rows, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case ComputerWizardStep.Name:
                return await ComputerWizardTypeAsync(spec, step, draft, ComputerWizardNameQuestion, draft.Name, allowEmpty: false, mask: false, text =>
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

                    if (string.Equals(name, ComputerConfig.LocalName, StringComparison.OrdinalIgnoreCase))
                    {
                        return ComputerText.LocalTakesNoHost;
                    }

                    string path = ComputerWizardPath(spec, draft.Global);
                    if (spec.Family.Load(path).Computers.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Name, draft.Original, StringComparison.OrdinalIgnoreCase)))
                    {
                        return SqlWizardNameTaken(name, path);
                    }

                    draft.Name = name;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case ComputerWizardStep.Host:
                return await ComputerWizardTypeAsync(spec, step, draft, ComputerWizardHostQuestion, c.Host ?? "", allowEmpty: false, mask: false, text =>
                {
                    string host = text.Trim().TrimStart('\\');
                    if (host.Length == 0)
                    {
                        return ComputerWizardHostRequired;
                    }

                    c.Host = host;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case ComputerWizardStep.Auth:
            {
                int current = c.IsRunAs ? 1 : 0;
                if (await ComputerWizardPickAsync(spec, ComputerWizardAuthQuestion, ComputerWizardAuthRows, current, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                if (picked != current)
                {
                    c.User = null;
                    draft.Password = "";
                }

                c.Auth = picked == 1 ? ComputerConfig.RunAsAuth : ComputerConfig.WindowsAuth;
                return true;
            }

            case ComputerWizardStep.User:
                return await ComputerWizardTypeAsync(spec, step, draft, ComputerWizardUserQuestion, c.User ?? "", allowEmpty: false, mask: false, text =>
                {
                    string user = text.Trim();
                    if (user.Length == 0)
                    {
                        return SqlWizardUserRequired;
                    }

                    if (WindowsCredentials.SplitAccount(user) is null)
                    {
                        return SqlText.RunAsNeedsDomain(user);
                    }

                    c.User = user;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case ComputerWizardStep.Store:
            {
                if (await ComputerWizardPickAsync(spec, SqlWizardStoreQuestion, SqlText.StoreRows(spec.Family.Info.FileName), c.InCredentialManager ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.PasswordStore = picked == 1 ? ComputerConfig.CredmanStore : ComputerConfig.FileStore;
                return true;
            }

            case ComputerWizardStep.Password:
                return await ComputerWizardTypeAsync(spec, step, draft, SqlWizardPasswordQuestion, draft.Password, allowEmpty: false, mask: true, text =>
                {
                    if (text.Length == 0)
                    {
                        return SqlWizardPasswordRequired;
                    }

                    draft.Password = text;
                    draft.PasswordKept = false;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case ComputerWizardStep.Access:
            {
                if (await ComputerWizardPickAsync(spec, ComputerWizardAccessQuestion, ComputerWizardAccessRows, c.ReadWrite ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Access = ConnectionAccess.Stored(picked == 1);
                return true;
            }

            case ComputerWizardStep.Visible:
                return await ComputerWizardTypeAsync(spec, step, draft, spec.VisibleQuestion, Joined(spec.GetVisible(c)), allowEmpty: true, mask: false, text =>
                {
                    spec.SetVisible(c, Patterns(text));
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case ComputerWizardStep.Control:
                return await ComputerWizardTypeAsync(spec, step, draft, spec.ControlQuestion, Joined(c.Control), allowEmpty: true, mask: false, text =>
                {
                    c.Control = Patterns(text);
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case ComputerWizardStep.Actions:
                return await ComputerWizardTypeAsync(spec, step, draft, ComputerWizardActionsQuestion + " (" + string.Join(", ", spec.Family.Info.ActionWords) + ")", c.ControlActions is null ? "all" : c.ControlActions.Count == 0 ? "none" : string.Join(", ", c.ControlActions), allowEmpty: true, mask: false, text =>
                {
                    string trimmed = text.Trim();
                    if (trimmed.Length == 0 || string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        c.ControlActions = null;
                        return null;
                    }

                    if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        c.ControlActions = [];
                        return null;
                    }

                    var words = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).Distinct().ToList();
                    if (words.FirstOrDefault(w => !spec.Family.Info.ActionWords.Contains(w)) is { } unknown)
                    {
                        return ComputerText.BadAction(unknown, spec.Family.Info);
                    }

                    c.ControlActions = spec.Family.Info.ActionWords.Where(words.Contains).ToList();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case ComputerWizardStep.Guard:
                if (spec.SetGuardList is { } setList)
                {
                    return await ComputerWizardTypeAsync(spec, step, draft, spec.GuardQuestion, Joined(spec.GetGuardList!(c)), allowEmpty: true, mask: false, text =>
                    {
                        setList(c, Patterns(text));
                        return null;
                    }, cancellationToken).ConfigureAwait(false);
                }

                {
                    if (await ComputerWizardPickAsync(spec, spec.GuardQuestion, ComputerWizardPrivilegedRows, spec.GetGuardFlag!(c) ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                    {
                        return false;
                    }

                    spec.SetGuardFlag!(c, picked == 1);
                    return true;
                }

            case ComputerWizardStep.Detail:
            {
                if (await ComputerWizardPickAsync(spec, ComputerWizardDetailQuestion, ComputerWizardDetailRows, c.Full ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Detail = picked == 1 ? ComputerConfig.FullDetail : null;
                return true;
            }

            default:
                return await ComputerWizardTypeAsync(spec, step, draft, ComputerWizardDescriptionQuestion, c.Description ?? "", allowEmpty: true, mask: false, text =>
                {
                    c.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A typed list of patterns: empty is absent.</summary>
    private static List<string>? Patterns(string text)
    {
        var items = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(i => !i.Any(char.IsControl)).ToList();
        return items.Count == 0 ? null : items;
    }

    private static string Joined(List<string>? items) => items is null ? "" : string.Join(", ", items);

    private Task<int?> ComputerWizardPickAsync<TConfig>(ComputerWizardSpec<TConfig> spec, string question, IReadOnlyList<string> choices, int cursor, CancellationToken cancellationToken) where TConfig : ComputerConfig, new()
    {
        var page = new MenuPage(Crumb(FieldName(spec.AddField)), choices.Select(Markup.Escape).ToList(), PickKeys) { Caption = question };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        return PickAsync(page, cursor, cancellationToken);
    }

    private async Task<bool> ComputerWizardTypeAsync<TConfig>(ComputerWizardSpec<TConfig> spec, ComputerWizardStep step, ComputerDraft<TConfig> draft, string question, string initial, bool allowEmpty, bool mask, Func<string, string?> accept, CancellationToken cancellationToken)
        where TConfig : ComputerConfig, new()
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(Crumb(FieldName(spec.AddField)), ComputerWizardRows(spec, draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, (int)step, _input, initial, allowEmpty, cancellationToken, mask).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(ComputerWizardLabels(spec.VisibleLabel, spec.GuardLabel)[(int)step] + " · " + question, EditKeys));
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

    private async Task<(bool Done, bool Changed, ComputerWizardStep? Edit)> ComputerWizardSummaryAsync<TConfig>(ComputerWizardSpec<TConfig> spec, ComputerDraft<TConfig> draft, CancellationToken cancellationToken)
        where TConfig : ComputerConfig, new()
    {
        int cursor = ConnectionWizardStartCursor(draft.Original, ComputerCatalog<TConfig>.OfferedNames(spec.Offered(_settings.Current)));
        while (true)
        {
            var actions = new List<string> { SqlWizardSaveOfferedRow, ComputerWizardSaveHiddenRow, ComputerWizardTestRow, SqlWizardCancelRow };
            int test = 2;
            var rows = actions.Select(Markup.Escape).Concat(ComputerWizardRows(spec, draft)).ToList();
            var page = new MenuPage(Crumb(FieldName(spec.AddField)), rows, PickKeys) { Caption = ComputerWizardSummaryCaption };
            if (!_pane.Enabled)
            {
                Flow.Notice(ComputerWizardSummaryCaption);
            }

            if (await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                return (false, false, null);
            }

            cursor = picked;
            if (picked >= actions.Count)
            {
                var edit = (ComputerWizardStep)(picked - actions.Count);
                if (ComputerWizardAsks(edit, draft))
                {
                    return (false, false, edit);
                }

                continue;
            }

            if (picked == test)
            {
                await ComputerWizardTestAsync(spec, draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == test + 1)
            {
                Sink.Notice(draft.Original is null ? ComputerWizardCancelledNotice : ConnectionWizardUnchangedNotice);
                return (true, false, null);
            }

            if (ComputerWizardSave(spec, draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>The draft tried as it stands, nothing written: read under its account (its password handed over as a plain file value).</summary>
    private async Task ComputerWizardTestAsync<TConfig>(ComputerWizardSpec<TConfig> spec, ComputerDraft<TConfig> draft, CancellationToken cancellationToken) where TConfig : ComputerConfig, new()
    {
        var copy = CloneEntry(draft.Config, spec.Info, _ => { });
        if (draft.IsLocal)
        {
            copy.Host = copy.Auth = copy.User = copy.Password = copy.PasswordStore = copy.Credential = null;
        }
        else if (copy.NeedsPassword)
        {
            copy.Password = draft.Password;
            copy.PasswordStore = ComputerConfig.FileStore;
            copy.Credential = null;
        }

        if (copy.Problem(draft.Name.Length > 0 ? draft.Name : "draft", spec.Family.Info) is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        if (spec.Test is not { } test)
        {
            Sink.Error(spec.NoTest);
            return;
        }

        var (ok, text) = await test(new Computer<TConfig>(draft.Name, copy, ComputerWizardPath(spec, draft.Global)), cancellationToken).ConfigureAwait(false);
        if (ok)
        {
            Sink.Notice(text);
        }
        else
        {
            Sink.Error(text);
        }
    }

    /// <summary>Writes the draft: the entry, then under runas its password, then the offered list. Whether a setting changed; null when nothing was written.</summary>
    private bool? ComputerWizardSave<TConfig>(ComputerWizardSpec<TConfig> spec, ComputerDraft<TConfig> draft, bool offer) where TConfig : ComputerConfig, new()
    {
        var c = draft.Config;
        if (draft.IsLocal)
        {
            c.Host = c.Auth = c.User = c.PasswordStore = c.Credential = null;
        }
        else if (!c.NeedsPassword)
        {
            c.User = null;
            c.PasswordStore = null;
            c.Auth = null;
        }

        if (!c.ReadWrite)
        {
            // Read-only drops what only a readwrite entry reads; the file stays short.
            c.Control = null;
            c.ControlActions = null;
        }

        if (ComputerWizardMissing(draft) is { } missing)
        {
            string unset = ComputerWizardLabels(spec.VisibleLabel, spec.GuardLabel)[(int)missing] + " is not set";
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, unset) : SqlText.ConnectionChangeFailed(draft.Name, unset));
            return null;
        }

        if (c.Problem(draft.Name, spec.Family.Info) is { } problem)
        {
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, problem) : SqlText.ConnectionChangeFailed(draft.Name, problem));
            return null;
        }

        string path = ComputerWizardPath(spec, draft.Global);
        if ((draft.Original is { } original ? spec.Family.ReplaceComputer(path, original, draft.Name, c) : spec.Family.AddComputer(path, draft.Name, c)) is { } error)
        {
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, error) : SqlText.ConnectionChangeFailed(draft.Name, error));
            return null;
        }

        Sink.Notice(draft.Original is null ? SqlText.ConnectionAdded(draft.Name, path) : SqlText.ConnectionChanged(draft.Name, path));
        bool saved = true;
        if (c.NeedsPassword)
        {
            (saved, string notice) = spec.Family.SavePassword(new Computer<TConfig>(draft.Name, c, path), draft.Password);
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
            ForgetOldCredential(draft.OriginalTarget, c.NeedsPassword && c.InCredentialManager ? c.CredentialTarget(spec.Family.Info, draft.Name) : null);
        }

        return ApplyOfferedAfterSave(spec.OfferedField, ComputerCatalog<TConfig>.OfferedNames(spec.Offered(_settings.Current)), draft.Original, draft.Name, offer, spec.SetOffered);
    }
}
