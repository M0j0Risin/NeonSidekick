using NeonSidekick.Sql;
using NeonSidekick.UI;
using NeonSidekick.Unc;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>UNC add share</c> (2026-09-30, the SQL wizard's walk for <c>unc.json</c>): one page per choice — which file, the name, the
/// path, the sign-in, the account, where its password is kept, the password (masked), the access, the description — every row
/// of the draft on each page, the current one marked, the question in the caption; then a summary that tests the unsaved draft
/// (its root listed under its account, nothing written there or here) and saves it (<see cref="UncConfigFile.AddShare"/>, the
/// password after it through <see cref="UncSecrets.Save"/>). ESC steps back, and on the first page ends the visit with nothing
/// written; Enter on a summary row changes that choice and comes back. Adds only: an existing entry is edited in the file.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The value column of the <c>UNC add share</c> action row. Pinned.</summary>
    public const string UncAddShareLabel = "Enter to start share wizard";

    /// <summary>The wizard's rows, one per <see cref="UncWizardStep"/> before the summary, in its order. Pinned.</summary>
    public static readonly IReadOnlyList<string> UncWizardLabels =
        ["File", "Name", "Path", "Sign-in", "User", "Password store", "Password", "Access", "Description"];

    public const string UncWizardFileQuestion = "Scope for unc.json?";
    public const string UncWizardNameQuestion = "Its name: what the model passes as \"share\" and *name picks on the input line.";
    public const string UncWizardPathQuestion = "The path: \\\\server\\share, a folder under it, or a local folder such as D:\\Data.";
    public const string UncWizardAuthQuestion = "Who the share is reached as.";
    public const string UncWizardUserQuestion = "The Windows account to reach it as: DOMAIN\\name or name@domain.";
    public const string UncWizardAccessQuestion = "What the model may do there.";
    public const string UncWizardDescriptionQuestion = "What the share holds, in your words (optional): the model reads it to pick a share.";
    public const string UncWizardSummaryCaption = "Check the share: Test lists its root without saving; Enter on a row below changes it.";

    /// <summary>The sign-in picks, <c>windows</c> then <c>runas</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> UncWizardAuthRows =
        ["windows  as you (no password)", "runas    as another Windows account (runas /netonly)"];

    /// <summary>The store picks, <c>file</c> then <c>credman</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> UncWizardStoreRows =
        ["file     encrypted (DPAPI) in unc.json", "credman  Windows Credential Manager"];

    /// <summary>The access picks, <c>read</c> then <c>readwrite</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> UncWizardAccessRows =
        ["read       search and read only (the default)", "readwrite  also write, move and delete — permanently, while UNC writes is on"];

    public const string UncWizardSaveHiddenRow = "Save, hidden from the model until ticked in UNC shares offered";
    public const string UncWizardTestRow = "Test the share";
    public const string UncWizardPathRequired = "A path is required.";
    public const string UncWizardCancelledNotice = "No share added.";

    /// <summary>The notice when a share is made readwrite while UNC writes is off: it stays read-only until the switch is on. Pinned.</summary>
    public const string UncWizardWritesOffNotice = "UNC writes is off: the share stays read-only until it is turned on (the UNC tab).";

    public static string UncWizardTestOkNotice(string name, int entries) => $"Reached '{name}': {SqlText.Count(entries, "entry", "entries")} at its root.";

    /// <summary>The step a wizard page asks; the summary last. The rows of <see cref="UncWizardLabels"/> by index.</summary>
    internal enum UncWizardStep
    {
        File,
        Name,
        Path,
        Auth,
        User,
        Store,
        Password,
        Access,
        Description,
        Summary,
    }

    /// <summary>What the wizard has so far: the file, the name, the entry as it will be written and the password it will store.</summary>
    private sealed class UncDraft
    {
        public bool Global { get; set; }

        public string Name { get; set; } = "";

        public string Password { get; set; } = "";

        public UncShareConfig Config { get; } = new()
        {
            Auth = UncShareConfig.WindowsAuth,
            PasswordStore = UncShareConfig.FileStore,
            Access = UncShareConfig.ReadAccess,
        };
    }

    private string UncWizardPath(bool global) =>
        global ? UncConfigFile.GlobalPath(_settings.StorageDirectory) : UncConfigFile.ProfilePath(_settings.ProfileDirectory);

    /// <summary>Whether <paramref name="step"/> is asked for the draft: the account, store and password only under runas.</summary>
    private static bool UncWizardAsks(UncWizardStep step, UncDraft draft) =>
        step is not (UncWizardStep.User or UncWizardStep.Store or UncWizardStep.Password) || draft.Config.NeedsPassword;

    /// <summary>The first step the draft still lacks (a name, a path, and under runas the account and password), or null.</summary>
    private static UncWizardStep? UncWizardMissing(UncDraft draft)
    {
        if (draft.Name.Length == 0)
        {
            return UncWizardStep.Name;
        }

        if (string.IsNullOrWhiteSpace(draft.Config.Path))
        {
            return UncWizardStep.Path;
        }

        if (draft.Config.NeedsPassword && string.IsNullOrWhiteSpace(draft.Config.User))
        {
            return UncWizardStep.User;
        }

        return draft.Config.NeedsPassword && draft.Password.Length == 0 ? UncWizardStep.Password : null;
    }

    /// <summary>The value column of the draft's row for <paramref name="step"/>.</summary>
    private string UncWizardValue(UncWizardStep step, UncDraft draft)
    {
        var c = draft.Config;
        if (!UncWizardAsks(step, draft))
        {
            return SqlWizardNotNeeded;
        }

        static string OrUnset(string? text) => string.IsNullOrWhiteSpace(text) ? SqlWizardUnset : text.Trim();
        return step switch
        {
            UncWizardStep.File => SqlWizardFileRow(draft.Global, UncWizardPath(draft.Global)),
            UncWizardStep.Name => OrUnset(draft.Name),
            UncWizardStep.Path => OrUnset(c.Path),
            UncWizardStep.Auth => c.Auth ?? UncShareConfig.WindowsAuth,
            UncWizardStep.User => OrUnset(c.User),
            UncWizardStep.Store => c.InCredentialManager ? UncShareConfig.CredmanStore + " (" + c.CredentialTarget(draft.Name.Length > 0 ? draft.Name : "<name>") + ")" : UncShareConfig.FileStore,
            UncWizardStep.Password => draft.Password.Length > 0 ? SqlWizardMasked : SqlWizardUnset,
            UncWizardStep.Access => c.Access ?? UncShareConfig.ReadAccess,
            _ => OrUnset(c.Description),
        };
    }

    /// <summary>Every row of the draft, the label padded, escaped.</summary>
    private List<string> UncWizardRows(UncDraft draft)
    {
        int width = UncWizardLabels.Max(l => l.Length) + 2;
        return UncWizardLabels.Select((label, i) => Markup.Escape(label.PadRight(width) + UncWizardValue((UncWizardStep)i, draft))).ToList();
    }

    private string UncWizardTitle => Crumb(FieldName(SettingsField.UncAddShare));

    /// <summary>
    /// The wizard: its steps in order, ESC one back (before the first: nothing written), a change from the summary back to it (by
    /// the step the draft still lacks, when the change asks for one — a sign-in that now takes a password). True when a setting
    /// changed: the offered list, for a share saved and offered.
    /// </summary>
    private async Task<bool> AddUncShareAsync(CancellationToken cancellationToken)
    {
        var draft = new UncDraft();
        var step = UncWizardStep.File;
        bool fromSummary = false;
        while (true)
        {
            if (step == UncWizardStep.Summary)
            {
                var (done, changed, edit) = await UncWizardSummaryAsync(draft, cancellationToken).ConfigureAwait(false);
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

                step = UncWizardStep.Description;
                continue;
            }

            if (!UncWizardAsks(step, draft))
            {
                step++;
                continue;
            }

            bool advanced = await UncWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false);
            if (advanced)
            {
                step = fromSummary ? UncWizardMissing(draft) ?? UncWizardStep.Summary : step + 1;
                fromSummary = fromSummary && step != UncWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = UncWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            do
            {
                step--;
            }
            while (step >= UncWizardStep.File && !UncWizardAsks(step, draft));

            if (step < UncWizardStep.File)
            {
                Sink.Notice(UncWizardCancelledNotice);
                return false;
            }
        }
    }

    /// <summary>One step's page: a pick or a typed value into the draft; true when it was answered, false for ESC.</summary>
    private async Task<bool> UncWizardStepAsync(UncWizardStep step, UncDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        switch (step)
        {
            case UncWizardStep.File:
            {
                string[] rows = [SqlWizardFileRow(false, UncWizardPath(false)), SqlWizardFileRow(true, UncWizardPath(true))];
                if (await UncWizardPickAsync(UncWizardFileQuestion, rows, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case UncWizardStep.Name:
                return await UncWizardTypeAsync(step, draft, UncWizardNameQuestion, draft.Name, allowEmpty: false, mask: false, text =>
                {
                    string name = text.Trim();
                    if (name.Length == 0)
                    {
                        return SqlWizardNameRequired;
                    }

                    if (name.Any(char.IsWhiteSpace))
                    {
                        return SqlWizardNameSpaces;
                    }

                    string path = UncWizardPath(draft.Global);
                    if (UncConfigFile.Load(path).Shares.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return SqlWizardNameTaken(name, path);
                    }

                    draft.Name = name;
                    string other = UncWizardPath(!draft.Global);
                    if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                        && UncConfigFile.Load(other).Shares.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Sink.Warning(draft.Global ? SqlWizardShadowedWarning(name, other) : SqlWizardShadowsWarning(name, other));
                    }

                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case UncWizardStep.Path:
                return await UncWizardTypeAsync(step, draft, UncWizardPathQuestion, c.Path ?? "", allowEmpty: false, mask: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return UncWizardPathRequired;
                    }

                    if (UncShareConfig.PathProblem(text) is { } problem)
                    {
                        return problem;
                    }

                    c.Path = UncShareConfig.NormalizeRoot(text);
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case UncWizardStep.Auth:
            {
                string[] words = [UncShareConfig.WindowsAuth, UncShareConfig.RunAsAuth];
                int current = Math.Max(0, Array.IndexOf(words, c.Auth));
                if (await UncWizardPickAsync(UncWizardAuthQuestion, UncWizardAuthRows, current, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                if (picked != current)
                {
                    // Another kind of sign-in: the account typed for the last does not carry over.
                    c.User = null;
                    draft.Password = "";
                }

                c.Auth = words[picked];
                return true;
            }

            case UncWizardStep.User:
                return await UncWizardTypeAsync(step, draft, UncWizardUserQuestion, c.User ?? "", allowEmpty: false, mask: false, text =>
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

            case UncWizardStep.Store:
            {
                if (await UncWizardPickAsync(SqlWizardStoreQuestion, UncWizardStoreRows, c.InCredentialManager ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.PasswordStore = picked == 1 ? UncShareConfig.CredmanStore : UncShareConfig.FileStore;
                return true;
            }

            case UncWizardStep.Password:
                return await UncWizardTypeAsync(step, draft, SqlWizardPasswordQuestion, draft.Password, allowEmpty: false, mask: true, text =>
                {
                    if (text.Length == 0)
                    {
                        return SqlWizardPasswordRequired;
                    }

                    draft.Password = text;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case UncWizardStep.Access:
            {
                if (await UncWizardPickAsync(UncWizardAccessQuestion, UncWizardAccessRows, c.IsReadWrite ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Access = picked == 1 ? UncShareConfig.ReadWriteAccess : UncShareConfig.ReadAccess;
                if (picked == 1 && !_settings.Current.UncWrites)
                {
                    Sink.Notice(UncWizardWritesOffNotice);
                }

                return true;
            }

            default:
                return await UncWizardTypeAsync(step, draft, UncWizardDescriptionQuestion, c.Description ?? "", allowEmpty: true, mask: false, text =>
                {
                    c.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A pick page: the question as its caption (a notice first on the prompt host, which has none), the choices as rows. The row, or null for ESC.</summary>
    private Task<int?> UncWizardPickAsync(string question, IReadOnlyList<string> choices, int cursor, CancellationToken cancellationToken)
    {
        var page = new MenuPage(UncWizardTitle, choices.Select(Markup.Escape).ToList(), PickKeys) { Caption = question };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        return PickAsync(page, cursor, cancellationToken);
    }

    /// <summary>A typed step, <see cref="SqlWizardTypeAsync"/>'s shape over the UNC draft. True once accepted, false for ESC.</summary>
    private async Task<bool> UncWizardTypeAsync(UncWizardStep step, UncDraft draft, string question, string initial, bool allowEmpty, bool mask, Func<string, string?> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(UncWizardTitle, UncWizardRows(draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, (int)step, _input, initial, allowEmpty, cancellationToken, mask).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(UncWizardLabels[(int)step] + " · " + question, EditKeys));
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

    /// <summary>
    /// The summary: the save rows (with and without offering it — always both since 2026-10-01, when nothing is offered until ticked), the test, the cancel,
    /// then every row of the draft. Done with whether a setting changed; or the step a row picked; or neither for ESC (back).
    /// </summary>
    private async Task<(bool Done, bool Changed, UncWizardStep? Edit)> UncWizardSummaryAsync(UncDraft draft, CancellationToken cancellationToken)
    {
        int cursor = 0;
        while (true)
        {
            // Always offer-or-hide (2026-10-01): nothing is offered until ticked, so the wizard is where a new one is.
            var actions = new List<string> { SqlWizardSaveOfferedRow, UncWizardSaveHiddenRow };
            int test = actions.Count;
            actions.Add(UncWizardTestRow);
            actions.Add(SqlWizardCancelRow);
            var rows = actions.Select(Markup.Escape).Concat(UncWizardRows(draft)).ToList();
            var page = new MenuPage(UncWizardTitle, rows, PickKeys) { Caption = UncWizardSummaryCaption };
            if (!_pane.Enabled)
            {
                Flow.Notice(UncWizardSummaryCaption);
            }

            if (await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                return (false, false, null);
            }

            cursor = picked;
            if (picked >= actions.Count)
            {
                var edit = (UncWizardStep)(picked - actions.Count);
                if (UncWizardAsks(edit, draft))
                {
                    return (false, false, edit);
                }

                continue;
            }

            if (picked == test)
            {
                await UncWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == test + 1)
            {
                Sink.Notice(UncWizardCancelledNotice);
                return (true, false, null);
            }

            if (UncWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>
    /// The draft tried as it stands, nothing written anywhere: its root listed under its account, its password handed over as a
    /// plain <c>file</c> value (which <see cref="UncSecrets.Resolve"/> passes through) so a <c>credman</c> draft is tested before its
    /// entry exists. The answer on the status line, with the share's warning if it has one; a failure still lets it be saved.
    /// </summary>
    private async Task UncWizardTestAsync(UncDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        var copy = new UncShareConfig
        {
            Path = c.Path,
            Auth = c.Auth,
            User = c.NeedsPassword ? c.User : null,
            Password = c.NeedsPassword ? draft.Password : null,
            PasswordStore = c.NeedsPassword ? UncShareConfig.FileStore : null,
            Access = c.Access,
        };
        if (copy.Problem is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        var result = await _testUncShare(new UncNamedShare(draft.Name, copy, UncWizardPath(draft.Global)), cancellationToken).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            Sink.Error(error);
            return;
        }

        Sink.Notice(UncWizardTestOkNotice(draft.Name, result.Value));
        if (copy.Warning is { } warning)
        {
            Sink.Warning(warning);
        }
    }

    /// <summary>The app's test: the draft's root listed under its account through <see cref="UncAccess.RunAsync{T}"/>, the entries counted. Never writes.</summary>
    private Task<UncResult<int>> TestUncShareAsync(UncNamedShare share, CancellationToken cancellationToken) =>
        new UncAccess(() => new UncCatalog([share], []), TimeProvider.System)
            .RunAsync(share, write: false, files => files.List("", Files.WorkingDirectory.ProbeListLimit).Entries.Count, cancellationToken);

    /// <summary>
    /// Writes the draft: the entry (<see cref="UncConfigFile.AddShare"/>), then under runas its password to its store, then — when
    /// <paramref name="offer"/> — its name added there. Whether a setting changed; null
    /// when nothing was written (the status line says why), the summary shown again.
    /// </summary>
    private bool? UncWizardSave(UncDraft draft, bool offer)
    {
        var c = draft.Config;
        if (!c.NeedsPassword)
        {
            c.User = null;
            c.PasswordStore = null;
            c.Auth = null;   // windows is the default; the file stays short
        }

        if (!c.IsReadWrite)
        {
            c.Access = null;   // read is the default
        }

        if (c.Problem is { } problem)
        {
            Sink.Error(SqlText.ConnectionAddFailed(draft.Name, problem));
            return null;
        }

        string path = UncWizardPath(draft.Global);
        if (UncConfigFile.AddShare(path, draft.Name, c) is { } error)
        {
            Sink.Error(SqlText.ConnectionAddFailed(draft.Name, error));
            return null;
        }

        Sink.Notice(SqlText.ConnectionAdded(draft.Name, path));
        if (c.NeedsPassword)
        {
            var (saved, notice) = UncSecrets.Save(new UncNamedShare(draft.Name, c, path), draft.Password);
            if (saved)
            {
                Sink.Notice(notice);
            }
            else
            {
                Sink.Error(notice);
            }
        }

        if (!offer)
        {
            return false;
        }

        var offered = _settings.Current.UncSharesOffered ?? [];   // null offers none (2026-10-01)
        Apply(SettingsField.UncSharesOffered, d => d.UncSharesOffered = [.. offered, draft.Name]);
        return true;
    }
}
