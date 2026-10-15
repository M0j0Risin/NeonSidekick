using NeonSidekick.EventLog;
using NeonSidekick.Sql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>Event log add/edit computer</c> (2026-10-15, the event-log plan's phase 3, the UNC wizard's loop for <c>eventlog.json</c>): one page per
/// choice — which file, the name, the host, the sign-in, the account, where its password is kept, the password (masked), the auth method,
/// the logs allowed, the detail, the description — then a summary that tests the unsaved draft (the newest System event read under its
/// account, nothing written there or here) and saves it (<see cref="EventLogConfigFile.AddComputer"/>, the password after it through
/// <see cref="EventLogSecrets.Save"/>). <c>local</c> (this PC) is listed whether a file holds it or not, and its walk is File, Logs,
/// Detail and Description alone. ESC steps back; Enter on a summary row changes that choice and comes back.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The value column of the <c>Event log add/edit computer</c> action row. Pinned.</summary>
    public const string EventLogAddComputerLabel = "Enter to start computer wizard";

    /// <summary>The wizard's rows, one per <see cref="EventLogWizardStep"/> before the summary, in its order. Pinned.</summary>
    public static readonly IReadOnlyList<string> EventLogWizardLabels =
        ["File", "Name", "Host", "Sign-in", "User", "Password store", "Password", "Auth method", "Logs", "Detail", "Description"];

    public const string EventLogWizardFileQuestion = "Which eventlog.json: this profile's or the global one?";
    public const string EventLogWizardNameQuestion = "Its name: what the model passes as \"computer\" and /eventlog computer:<name> reads.";
    public const string EventLogWizardHostQuestion = "The computer's name or address (fs01.corp.example, 192.168.1.40; localhost for a loopback session).";
    public const string EventLogWizardAuthQuestion = "Who its logs are read as.";
    public const string EventLogWizardUserQuestion = "The account to read them as: DOMAIN\\name or name@domain (lab-box\\admin for the machine's own account).";
    public const string EventLogWizardMethodQuestion = "How the sign-in is made (default lets Windows choose).";
    public const string EventLogWizardLogsQuestion = "The logs allowed, joined by commas (* any run but /, e.g. Microsoft-Windows-*/Operational); empty for all.";
    public const string EventLogWizardDetailQuestion = "What the main chat's model reads of these events (the bots and advisors always read redacted).";
    public const string EventLogWizardDescriptionQuestion = "What the computer is, in your words (optional): the model reads it to pick one.";
    public const string EventLogWizardSummaryCaption = "Check the computer: Test reads its newest System event without saving; Enter on a row below changes it.";

    /// <summary>The sign-in picks, <c>windows</c> then <c>runas</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> EventLogWizardAuthRows =
        ["windows  as you (no password)", "runas    as another Windows account (it needs Event Log Readers there)"];

    /// <summary>The detail picks, <c>redacted</c> then <c>full</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> EventLogWizardDetailRows =
        ["redacted  secret-like values hidden from the model (the default)", "full      the events as they are, for the main chat"];

    /// <summary>The store picks, <c>file</c> then <c>credman</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> EventLogWizardStoreRows = SqlText.StoreRows("eventlog.json");

    public const string EventLogWizardSaveHiddenRow = "Save, hidden from the model until ticked in Event log computers offered";
    public const string EventLogWizardTestRow = "Test the computer";
    public const string EventLogWizardHostRequired = "A host is required.";
    public const string EventLogWizardCancelledNotice = "No computer added.";
    public const string EventLogWizardNoTest = "There is no event log to test against here (it needs Windows).";

    /// <summary>The first page's row for <c>local</c> when no file holds it yet. Pinned.</summary>
    public const string EventLogWizardLocalRow = "local  (this PC, built in)";

    /// <summary>The step a wizard page asks; the summary last. The rows of <see cref="EventLogWizardLabels"/> by index.</summary>
    internal enum EventLogWizardStep
    {
        File,
        Name,
        Host,
        Auth,
        User,
        Store,
        Password,
        Method,
        Logs,
        Detail,
        Description,
        Summary,
    }

    /// <summary>What the wizard has so far: the file, the name, the entry as it will be written and the password it will store.</summary>
    private sealed class EventLogDraft
    {
        public bool Global { get; set; }

        public string Name { get; set; } = "";

        public string Password { get; set; } = "";

        /// <summary>An edit's entry name in its file; null for a new computer (or <c>local</c> no file holds yet).</summary>
        public string? Original { get; init; }

        public string? OriginalTarget { get; init; }

        public bool PasswordKept { get; set; }

        public bool IsLocal => string.Equals(Name, EventLogText.LocalName, StringComparison.OrdinalIgnoreCase);

        public EventLogComputerConfig Config { get; init; } = new();
    }

    private string EventLogWizardPath(bool global) =>
        global ? EventLogConfigFile.GlobalPath(_settings.StorageDirectory) : EventLogConfigFile.ProfilePath(_settings.ProfileDirectory);

    private string EventLogWizardTitle => Crumb(FieldName(SettingsField.EventLogAddComputer));

    /// <summary>Whether <paramref name="step"/> is asked: <c>local</c> only its file (when new), logs, detail and description; the account and password only under runas; the file never for an edit.</summary>
    private static bool EventLogWizardAsks(EventLogWizardStep step, EventLogDraft draft)
    {
        if (step == EventLogWizardStep.File)
        {
            return draft.Original is null;
        }

        if (draft.IsLocal)
        {
            return step is EventLogWizardStep.Logs or EventLogWizardStep.Detail or EventLogWizardStep.Description;
        }

        return step is not (EventLogWizardStep.User or EventLogWizardStep.Store or EventLogWizardStep.Password) || draft.Config.NeedsPassword;
    }

    /// <summary>The first step the draft still lacks, or null.</summary>
    private static EventLogWizardStep? EventLogWizardMissing(EventLogDraft draft)
    {
        if (draft.Name.Length == 0)
        {
            return EventLogWizardStep.Name;
        }

        if (draft.IsLocal)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(draft.Config.Host))
        {
            return EventLogWizardStep.Host;
        }

        if (draft.Config.NeedsPassword && string.IsNullOrWhiteSpace(draft.Config.User))
        {
            return EventLogWizardStep.User;
        }

        return draft.Config.NeedsPassword && draft.Password.Length == 0 ? EventLogWizardStep.Password : null;
    }

    private string EventLogWizardValue(EventLogWizardStep step, EventLogDraft draft)
    {
        var c = draft.Config;
        if (step != EventLogWizardStep.File && step != EventLogWizardStep.Name && !EventLogWizardAsks(step, draft))
        {
            return SqlWizardNotNeeded;
        }

        static string OrUnset(string? text) => string.IsNullOrWhiteSpace(text) ? SqlWizardUnset : text.Trim();
        return step switch
        {
            EventLogWizardStep.File => SqlWizardFileRow(draft.Global, EventLogWizardPath(draft.Global)),
            EventLogWizardStep.Name => OrUnset(draft.Name),
            EventLogWizardStep.Host => OrUnset(c.Host),
            EventLogWizardStep.Auth => c.IsRunAs ? EventLogComputerConfig.RunAsAuth : EventLogComputerConfig.WindowsAuth,
            EventLogWizardStep.User => OrUnset(c.User),
            EventLogWizardStep.Store => c.InCredentialManager ? EventLogComputerConfig.CredmanStore + " (" + c.CredentialTarget(draft.Name.Length > 0 ? draft.Name : "<name>") + ")" : EventLogComputerConfig.FileStore,
            EventLogWizardStep.Password => draft.Password.Length > 0 ? (draft.PasswordKept ? SqlWizardMaskedKept : SqlWizardMasked) : SqlWizardUnset,
            EventLogWizardStep.Method => c.AuthMethodWord,
            EventLogWizardStep.Logs => c.Logs is null ? "(all)" : string.Join(", ", c.Logs),
            EventLogWizardStep.Detail => c.DetailWord,
            _ => OrUnset(c.Description),
        };
    }

    private List<string> EventLogWizardRows(EventLogDraft draft)
    {
        int width = EventLogWizardLabels.Max(l => l.Length) + 2;
        return EventLogWizardLabels.Select((label, i) => Markup.Escape(label.PadRight(width) + EventLogWizardValue((EventLogWizardStep)i, draft))).ToList();
    }

    /// <summary>The wizard: the first page of the entries (<c>local</c> among them), then its steps. True when a setting changed.</summary>
    private async Task<bool> AddEventLogComputerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var entries = ConnectionWizardEntries(EventLogWizardPath(false), EventLogWizardPath(true), path => EventLogConfigFile.Load(path).Computers, c => c.Name);
            bool localListed = entries.Any(e => e.Named.IsLocal);
            var rows = new List<(string Name, bool Global)>();
            if (!localListed)
            {
                rows.Add((EventLogText.LocalName, false));
            }

            rows.AddRange(entries.Select(e => (e.Named.Name, e.Global)));
            var draft = new EventLogDraft();
            if (await ConnectionWizardPickAsync(EventLogWizardTitle, "computer", rows, EventLogWizardPath, cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                Sink.Notice(EventLogWizardCancelledNotice);
                return false;
            }

            if (picked >= 0)
            {
                int at = localListed ? picked : picked - 1;
                draft = at < 0 ? new EventLogDraft { Name = EventLogText.LocalName } : EventLogWizardDraftFrom(entries[at].Named, entries[at].Global);
            }

            if (await RunEventLogWizardAsync(draft, cancellationToken).ConfigureAwait(false) is { } changed)
            {
                return changed;
            }
        }
    }

    private EventLogDraft EventLogWizardDraftFrom(EventLogComputer named, bool global)
    {
        var c = named.Config;
        var draft = new EventLogDraft
        {
            Global = global,
            Name = named.Name,
            Original = named.Name,
            OriginalTarget = c.NeedsPassword && c.InCredentialManager && c.Credential is null ? c.CredentialTarget(named.Name) : null,
            Config = CloneEntry(c, EventLogJsonContext.Default.EventLogComputerConfig, x => x.Password = null),
        };
        if (c.NeedsPassword)
        {
            draft.Password = StoredPassword(EventLogSecrets.Resolve(named));
            draft.PasswordKept = draft.Password.Length > 0;
        }

        return draft;
    }

    /// <summary>The wizard on <paramref name="draft"/>: null for ESC on its first page, else whether a setting changed.</summary>
    private async Task<bool?> RunEventLogWizardAsync(EventLogDraft draft, CancellationToken cancellationToken)
    {
        var step = draft.Original is null ? EventLogWizardStep.File : EventLogWizardMissing(draft) ?? EventLogWizardStep.Summary;
        bool fromSummary = draft.Original is not null;
        while (true)
        {
            if (step == EventLogWizardStep.Summary)
            {
                var (done, changed, edit) = await EventLogWizardSummaryAsync(draft, cancellationToken).ConfigureAwait(false);
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

                step = EventLogWizardStep.Description;
                continue;
            }

            if (!EventLogWizardAsks(step, draft) || (step == EventLogWizardStep.Name && draft.IsLocal))
            {
                step++;
                continue;
            }

            bool advanced = await EventLogWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false);
            if (advanced)
            {
                step = fromSummary ? EventLogWizardMissing(draft) ?? EventLogWizardStep.Summary : step + 1;
                fromSummary = fromSummary && step != EventLogWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = EventLogWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            do
            {
                step--;
            }
            while (step >= EventLogWizardStep.File && (!EventLogWizardAsks(step, draft) || (step == EventLogWizardStep.Name && draft.IsLocal)));

            if (step < EventLogWizardStep.File)
            {
                return null;
            }
        }
    }

    private async Task<bool> EventLogWizardStepAsync(EventLogWizardStep step, EventLogDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        switch (step)
        {
            case EventLogWizardStep.File:
            {
                string[] rows = [SqlWizardFileRow(false, EventLogWizardPath(false)), SqlWizardFileRow(true, EventLogWizardPath(true))];
                if (await EventLogWizardPickAsync(EventLogWizardFileQuestion, rows, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case EventLogWizardStep.Name:
                return await EventLogWizardTypeAsync(step, draft, EventLogWizardNameQuestion, draft.Name, allowEmpty: false, mask: false, text =>
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

                    if (string.Equals(name, EventLogText.LocalName, StringComparison.OrdinalIgnoreCase) && !draft.IsLocal)
                    {
                        return EventLogText.LocalTakesNoHost;
                    }

                    string path = EventLogWizardPath(draft.Global);
                    if (EventLogConfigFile.Load(path).Computers.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Name, draft.Original, StringComparison.OrdinalIgnoreCase)))
                    {
                        return SqlWizardNameTaken(name, path);
                    }

                    draft.Name = name;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case EventLogWizardStep.Host:
                return await EventLogWizardTypeAsync(step, draft, EventLogWizardHostQuestion, c.Host ?? "", allowEmpty: false, mask: false, text =>
                {
                    string host = text.Trim();
                    if (host.Length == 0)
                    {
                        return EventLogWizardHostRequired;
                    }

                    c.Host = host.TrimStart('\\');
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case EventLogWizardStep.Auth:
            {
                int current = c.IsRunAs ? 1 : 0;
                if (await EventLogWizardPickAsync(EventLogWizardAuthQuestion, EventLogWizardAuthRows, current, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                if (picked != current)
                {
                    c.User = null;
                    draft.Password = "";
                }

                c.Auth = picked == 1 ? EventLogComputerConfig.RunAsAuth : EventLogComputerConfig.WindowsAuth;
                return true;
            }

            case EventLogWizardStep.User:
                return await EventLogWizardTypeAsync(step, draft, EventLogWizardUserQuestion, c.User ?? "", allowEmpty: false, mask: false, text =>
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

            case EventLogWizardStep.Store:
            {
                if (await EventLogWizardPickAsync(SqlWizardStoreQuestion, EventLogWizardStoreRows, c.InCredentialManager ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.PasswordStore = picked == 1 ? EventLogComputerConfig.CredmanStore : EventLogComputerConfig.FileStore;
                return true;
            }

            case EventLogWizardStep.Password:
                return await EventLogWizardTypeAsync(step, draft, SqlWizardPasswordQuestion, draft.Password, allowEmpty: false, mask: true, text =>
                {
                    if (text.Length == 0)
                    {
                        return SqlWizardPasswordRequired;
                    }

                    draft.Password = text;
                    draft.PasswordKept = false;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case EventLogWizardStep.Method:
            {
                var methods = EventLogComputerConfig.AuthMethods;
                int current = Math.Max(0, methods.ToList().IndexOf(c.AuthMethodWord));
                if (await EventLogWizardPickAsync(EventLogWizardMethodQuestion, methods, current, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.AuthMethod = picked == 0 ? null : methods[picked];
                return true;
            }

            case EventLogWizardStep.Logs:
                return await EventLogWizardTypeAsync(step, draft, EventLogWizardLogsQuestion, c.Logs is null ? "" : string.Join(", ", c.Logs), allowEmpty: true, mask: false, text =>
                {
                    var logs = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (logs.Any(l => l.Any(char.IsControl)))
                    {
                        return EventLogText.BadLiteral(text);
                    }

                    c.Logs = logs.Count == 0 ? null : logs;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case EventLogWizardStep.Detail:
            {
                if (await EventLogWizardPickAsync(EventLogWizardDetailQuestion, EventLogWizardDetailRows, c.Full ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Detail = picked == 1 ? EventLogComputerConfig.FullDetail : null;   // redacted is the default; the file stays short
                return true;
            }

            default:
                return await EventLogWizardTypeAsync(step, draft, EventLogWizardDescriptionQuestion, c.Description ?? "", allowEmpty: true, mask: false, text =>
                {
                    c.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<int?> EventLogWizardPickAsync(string question, IReadOnlyList<string> choices, int cursor, CancellationToken cancellationToken)
    {
        var page = new MenuPage(EventLogWizardTitle, choices.Select(Markup.Escape).ToList(), PickKeys) { Caption = question };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        return PickAsync(page, cursor, cancellationToken);
    }

    private async Task<bool> EventLogWizardTypeAsync(EventLogWizardStep step, EventLogDraft draft, string question, string initial, bool allowEmpty, bool mask, Func<string, string?> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(EventLogWizardTitle, EventLogWizardRows(draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, (int)step, _input, initial, allowEmpty, cancellationToken, mask).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(EventLogWizardLabels[(int)step] + " · " + question, EditKeys));
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

    private async Task<(bool Done, bool Changed, EventLogWizardStep? Edit)> EventLogWizardSummaryAsync(EventLogDraft draft, CancellationToken cancellationToken)
    {
        int cursor = ConnectionWizardStartCursor(draft.Original, EventLogCatalog.OfferedNames(_settings.Current.EventLogComputersOffered));
        while (true)
        {
            var actions = new List<string> { SqlWizardSaveOfferedRow, EventLogWizardSaveHiddenRow };
            int test = actions.Count;
            actions.Add(EventLogWizardTestRow);
            actions.Add(SqlWizardCancelRow);
            var rows = actions.Select(Markup.Escape).Concat(EventLogWizardRows(draft)).ToList();
            var page = new MenuPage(EventLogWizardTitle, rows, PickKeys) { Caption = EventLogWizardSummaryCaption };
            if (!_pane.Enabled)
            {
                Flow.Notice(EventLogWizardSummaryCaption);
            }

            if (await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                return (false, false, null);
            }

            cursor = picked;
            if (picked >= actions.Count)
            {
                var edit = (EventLogWizardStep)(picked - actions.Count);
                if (EventLogWizardAsks(edit, draft) && !(edit == EventLogWizardStep.Name && draft.IsLocal))
                {
                    return (false, false, edit);
                }

                continue;
            }

            if (picked == test)
            {
                await EventLogWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == test + 1)
            {
                Sink.Notice(draft.Original is null ? EventLogWizardCancelledNotice : ConnectionWizardUnchangedNotice);
                return (true, false, null);
            }

            if (EventLogWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>The draft tried as it stands, nothing written: the newest System event under its account (its password handed over as a plain file value).</summary>
    private async Task EventLogWizardTestAsync(EventLogDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        var copy = new EventLogComputerConfig
        {
            Host = draft.IsLocal ? null : c.Host,
            Auth = draft.IsLocal ? null : c.Auth,
            User = c.NeedsPassword ? c.User : null,
            Password = c.NeedsPassword ? draft.Password : null,
            PasswordStore = c.NeedsPassword ? EventLogComputerConfig.FileStore : null,
            AuthMethod = draft.IsLocal ? null : c.AuthMethod,
            Logs = c.Logs,
            Detail = c.Detail,
        };
        if (copy.Problem(draft.Name.Length > 0 ? draft.Name : "draft") is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        if (TestEventLogComputer is not { } test)
        {
            Sink.Error(EventLogWizardNoTest);
            return;
        }

        var (ok, text) = await test(new EventLogComputer(draft.Name, copy, EventLogWizardPath(draft.Global)), cancellationToken).ConfigureAwait(false);
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
    private bool? EventLogWizardSave(EventLogDraft draft, bool offer)
    {
        var c = draft.Config;
        if (draft.IsLocal)
        {
            c.Host = c.Auth = c.User = c.PasswordStore = c.Credential = c.AuthMethod = null;
        }
        else if (!c.NeedsPassword)
        {
            c.User = null;
            c.PasswordStore = null;
            c.Auth = null;   // windows is the default; the file stays short
        }

        if (EventLogWizardMissing(draft) is { } missing)
        {
            string unset = EventLogWizardLabels[(int)missing] + " is not set";
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, unset) : SqlText.ConnectionChangeFailed(draft.Name, unset));
            return null;
        }

        if (c.Problem(draft.Name) is { } problem)
        {
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, problem) : SqlText.ConnectionChangeFailed(draft.Name, problem));
            return null;
        }

        string path = EventLogWizardPath(draft.Global);
        if ((draft.Original is { } original ? EventLogConfigFile.ReplaceComputer(path, original, draft.Name, c) : EventLogConfigFile.AddComputer(path, draft.Name, c)) is { } error)
        {
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, error) : SqlText.ConnectionChangeFailed(draft.Name, error));
            return null;
        }

        Sink.Notice(draft.Original is null ? SqlText.ConnectionAdded(draft.Name, path) : SqlText.ConnectionChanged(draft.Name, path));
        bool saved = true;
        if (c.NeedsPassword)
        {
            (saved, string notice) = EventLogSecrets.Save(new EventLogComputer(draft.Name, c, path), draft.Password);
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
            ForgetOldCredential(draft.OriginalTarget, c.NeedsPassword && c.InCredentialManager ? c.CredentialTarget(draft.Name) : null);
        }

        return ApplyOfferedAfterSave(SettingsField.EventLogComputersOffered, EventLogCatalog.OfferedNames(_settings.Current.EventLogComputersOffered), draft.Original, draft.Name, offer, (d, next) => d.EventLogComputersOffered = next);
    }
}
