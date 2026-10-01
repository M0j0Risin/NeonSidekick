using System.Globalization;
using NeonSidekick.Oracle;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>Oracle add connection</c> (2026-09-30, the user's ask: an Oracle tab "and a wizard to set up new connections"), the
/// <c>SQL add connection</c> wizard's shape over <c>oracle.json</c>: one page per choice — which file, the name, the data
/// source, the default schema, the user, where its password is kept, the password (masked), the connect timeout, the
/// description — every row of the draft on each page, the current one marked, the question in the caption; then a summary
/// that tests the unsaved draft (who it signs in as, where, the server's version — and, since read-only is the user's first
/// ask of these tools, a warning when the account could change data: the tools never will, but a read-only account is the
/// real guard) and saves it (<see cref="OracleConfigFile.AddConnection"/>, the password after it through
/// <see cref="OracleSecrets.Save"/>). ESC steps back, and on the first page ends the visit with nothing written; Enter on a
/// summary row changes that choice and comes back. Adds only: an existing entry is edited in the file.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The wizard's rows, one per <see cref="OracleWizardStep"/> before the summary, in its order. Pinned.</summary>
    public static readonly IReadOnlyList<string> OracleWizardLabels =
        ["File", "Name", "Data source", "Default schema", "User", "Password store", "Password", "Connect timeout (s)", "Description"];

    public const string OracleWizardFileQuestion = "Scope for oracle.json?";
    public const string OracleWizardDataSourceQuestion = "The database: host:port/service (EZConnect, e.g. localhost:1521/FREEPDB1) or a whole (DESCRIPTION=…).";
    public const string OracleWizardSchemaQuestion = "The schema a call works in when it names none; empty for the user's own.";
    public const string OracleWizardUserQuestion = "The database user (a read-only account is the real guard; SYS is refused).";
    public const string OracleWizardDataSourceRequired = "A data source is required.";

    /// <summary>The store picks, <c>file</c> then <c>credman</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> OracleWizardStoreRows =
        ["file     encrypted (DPAPI) in oracle.json", "credman  Windows Credential Manager"];

    public static readonly string OracleWizardTimeoutQuestion =
        "Seconds a connect may take, 1 to " + Invariant(OracleConnectionConfig.MaxConnectTimeoutSeconds) + "; empty for " + Invariant(OracleConnectionConfig.DefaultConnectTimeoutSeconds) + ".";

    /// <summary>The step a wizard page asks; the summary last. The rows of <see cref="OracleWizardLabels"/> by index.</summary>
    internal enum OracleWizardStep
    {
        File,
        Name,
        DataSource,
        Schema,
        User,
        Store,
        Password,
        Timeout,
        Description,
        Summary,
    }

    /// <summary>What the wizard has so far: the file, the name, the entry as it will be written and the password it will store.</summary>
    private sealed class OracleDraft
    {
        public bool Global { get; set; }

        public string Name { get; set; } = "";

        public string Password { get; set; } = "";

        public OracleConnectionConfig Config { get; } = new() { PasswordStore = OracleConnectionConfig.FileStore };
    }

    private string OracleWizardPath(bool global) =>
        global ? OracleConfigFile.GlobalPath(_settings.StorageDirectory) : OracleConfigFile.ProfilePath(_settings.ProfileDirectory);

    /// <summary>The first step the draft still lacks (a name, a data source, a user, a password), or null.</summary>
    private static OracleWizardStep? OracleWizardMissing(OracleDraft draft)
    {
        if (draft.Name.Length == 0)
        {
            return OracleWizardStep.Name;
        }

        if (string.IsNullOrWhiteSpace(draft.Config.DataSource))
        {
            return OracleWizardStep.DataSource;
        }

        if (string.IsNullOrWhiteSpace(draft.Config.User))
        {
            return OracleWizardStep.User;
        }

        return draft.Password.Length == 0 ? OracleWizardStep.Password : null;
    }

    /// <summary>The value column of the draft's row for <paramref name="step"/>.</summary>
    private string OracleWizardValue(OracleWizardStep step, OracleDraft draft)
    {
        var c = draft.Config;
        static string OrUnset(string? text) => string.IsNullOrWhiteSpace(text) ? SqlWizardUnset : text.Trim();
        return step switch
        {
            OracleWizardStep.File => SqlWizardFileRow(draft.Global, OracleWizardPath(draft.Global)),
            OracleWizardStep.Name => OrUnset(draft.Name),
            OracleWizardStep.DataSource => OrUnset(c.DataSource),
            OracleWizardStep.Schema => string.IsNullOrWhiteSpace(c.Schema) ? "(the user's own)" : c.Schema.Trim(),
            OracleWizardStep.User => OrUnset(c.User),
            OracleWizardStep.Store => c.InCredentialManager ? OracleConnectionConfig.CredmanStore + " (" + c.CredentialTarget(draft.Name.Length > 0 ? draft.Name : "<name>") + ")" : OracleConnectionConfig.FileStore,
            OracleWizardStep.Password => draft.Password.Length > 0 ? SqlWizardMasked : SqlWizardUnset,
            OracleWizardStep.Timeout => Invariant(c.ConnectTimeoutSeconds ?? OracleConnectionConfig.DefaultConnectTimeoutSeconds),
            _ => OrUnset(c.Description),
        };
    }

    /// <summary>Every row of the draft, the label padded, escaped.</summary>
    private List<string> OracleWizardRows(OracleDraft draft)
    {
        int width = OracleWizardLabels.Max(l => l.Length) + 2;
        return OracleWizardLabels.Select((label, i) => Markup.Escape(label.PadRight(width) + OracleWizardValue((OracleWizardStep)i, draft))).ToList();
    }

    private string OracleWizardTitle => Crumb(FieldName(SettingsField.OracleAddConnection));

    /// <summary>
    /// The wizard: its steps in order, ESC one back (before the first: nothing written), a change from the summary back to
    /// it. True when a setting changed: the offered list, for a connection saved and offered.
    /// </summary>
    private async Task<bool> AddOracleConnectionAsync(CancellationToken cancellationToken)
    {
        var draft = new OracleDraft();
        var step = OracleWizardStep.File;
        bool fromSummary = false;
        while (true)
        {
            if (step == OracleWizardStep.Summary)
            {
                var (done, changed, edit) = await OracleWizardSummaryAsync(draft, cancellationToken).ConfigureAwait(false);
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

                step = OracleWizardStep.Description;
                continue;
            }

            if (await OracleWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false))
            {
                step = fromSummary ? OracleWizardMissing(draft) ?? OracleWizardStep.Summary : step + 1;
                fromSummary = fromSummary && step != OracleWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = OracleWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            if (step == OracleWizardStep.File)
            {
                Sink.Notice(SqlWizardCancelledNotice);
                return false;
            }

            step--;
        }
    }

    /// <summary>One step's page: a pick or a typed value into the draft; true when it was answered, false for ESC.</summary>
    private async Task<bool> OracleWizardStepAsync(OracleWizardStep step, OracleDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        switch (step)
        {
            case OracleWizardStep.File:
            {
                string[] rows = [SqlWizardFileRow(false, OracleWizardPath(false)), SqlWizardFileRow(true, OracleWizardPath(true))];
                if (await OracleWizardPickAsync(OracleWizardFileQuestion, rows, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case OracleWizardStep.Name:
                return await OracleWizardTypeAsync(step, draft, SqlWizardNameQuestion, draft.Name, allowEmpty: false, mask: false, text =>
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

                    string path = OracleWizardPath(draft.Global);
                    if (OracleConfigFile.Load(path).Connections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return SqlWizardNameTaken(name, path);
                    }

                    draft.Name = name;
                    string other = OracleWizardPath(!draft.Global);
                    if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                        && OracleConfigFile.Load(other).Connections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Sink.Warning(draft.Global ? SqlWizardShadowedWarning(name, other) : SqlWizardShadowsWarning(name, other));
                    }

                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case OracleWizardStep.DataSource:
                return await OracleWizardTypeAsync(step, draft, OracleWizardDataSourceQuestion, c.DataSource ?? "", allowEmpty: false, mask: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return OracleWizardDataSourceRequired;
                    }

                    c.DataSource = text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case OracleWizardStep.Schema:
                return await OracleWizardTypeAsync(step, draft, OracleWizardSchemaQuestion, c.Schema ?? "", allowEmpty: true, mask: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        c.Schema = null;
                        return null;
                    }

                    if (OracleIdentifier.Normalize(text) is null)
                    {
                        return OracleText.BadSchemaKey(text.Trim());
                    }

                    c.Schema = text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case OracleWizardStep.User:
                return await OracleWizardTypeAsync(step, draft, OracleWizardUserQuestion, c.User ?? "", allowEmpty: false, mask: false, text =>
                {
                    string user = text.Trim();
                    if (user.Length == 0)
                    {
                        return SqlWizardUserRequired;
                    }

                    var probe = new OracleConnectionConfig { DataSource = "probe", User = user };
                    if (probe.Problem is { } problem)
                    {
                        return problem;
                    }

                    c.User = user;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case OracleWizardStep.Store:
            {
                if (await OracleWizardPickAsync(SqlWizardStoreQuestion, OracleWizardStoreRows, c.InCredentialManager ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.PasswordStore = picked == 1 ? OracleConnectionConfig.CredmanStore : OracleConnectionConfig.FileStore;
                return true;
            }

            case OracleWizardStep.Password:
                return await OracleWizardTypeAsync(step, draft, SqlWizardPasswordQuestion, draft.Password, allowEmpty: false, mask: true, text =>
                {
                    if (text.Length == 0)
                    {
                        return SqlWizardPasswordRequired;
                    }

                    draft.Password = text;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case OracleWizardStep.Timeout:
                return await OracleWizardTypeAsync(step, draft, OracleWizardTimeoutQuestion, c.ConnectTimeoutSeconds is { } s ? Invariant(s) : "", allowEmpty: true, mask: false, text =>
                {
                    string typed = text.Trim();
                    if (typed.Length == 0)
                    {
                        c.ConnectTimeoutSeconds = null;
                        return null;
                    }

                    if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 1 || seconds > OracleConnectionConfig.MaxConnectTimeoutSeconds)
                    {
                        return SqlWizardTimeoutError(typed);
                    }

                    c.ConnectTimeoutSeconds = seconds;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            default:
                return await OracleWizardTypeAsync(step, draft, SqlWizardDescriptionQuestion, c.Description ?? "", allowEmpty: true, mask: false, text =>
                {
                    c.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A pick page: the question as its caption (a notice first on the prompt host, which has none), the choices as rows. The row, or null for ESC.</summary>
    private Task<int?> OracleWizardPickAsync(string question, IReadOnlyList<string> choices, int cursor, CancellationToken cancellationToken)
    {
        var page = new MenuPage(OracleWizardTitle, choices.Select(Markup.Escape).ToList(), PickKeys) { Caption = question };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        return PickAsync(page, cursor, cancellationToken);
    }

    /// <summary>
    /// A typed step: the draft's rows with the step's marked, the question as the caption, the slot pre-filled with
    /// <paramref name="initial"/>; <paramref name="accept"/> stores the text or names what is wrong with it, and a wrong one
    /// asks again with the error on the status line. True once accepted, false for ESC.
    /// </summary>
    private async Task<bool> OracleWizardTypeAsync(OracleWizardStep step, OracleDraft draft, string question, string initial, bool allowEmpty, bool mask, Func<string, string?> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(OracleWizardTitle, OracleWizardRows(draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, (int)step, _input, initial, allowEmpty, cancellationToken, mask).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(OracleWizardLabels[(int)step] + " · " + question, EditKeys));
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
    /// The summary: the save rows (with and without offering it — always both since 2026-10-01, when nothing is offered until ticked), the test, the
    /// cancel, then every row of the draft. Done with whether a setting changed; or the step a row picked; or neither for ESC (back).
    /// </summary>
    private async Task<(bool Done, bool Changed, OracleWizardStep? Edit)> OracleWizardSummaryAsync(OracleDraft draft, CancellationToken cancellationToken)
    {
        int cursor = 0;
        while (true)
        {
            // Always offer-or-hide (2026-10-01): nothing is offered until ticked, so the wizard is where a new one is.
            var actions = new List<string> { SqlWizardSaveOfferedRow, OracleWizardSaveHiddenRow };
            int test = actions.Count;
            actions.Add(SqlWizardTestRow);
            actions.Add(SqlWizardCancelRow);
            var rows = actions.Select(Markup.Escape).Concat(OracleWizardRows(draft)).ToList();
            var page = new MenuPage(OracleWizardTitle, rows, PickKeys) { Caption = SqlWizardSummaryCaption };
            if (!_pane.Enabled)
            {
                Flow.Notice(SqlWizardSummaryCaption);
            }

            if (await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                return (false, false, null);
            }

            cursor = picked;
            if (picked >= actions.Count)
            {
                return (false, false, (OracleWizardStep)(picked - actions.Count));
            }

            if (picked == test)
            {
                await OracleWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == test + 1)
            {
                Sink.Notice(SqlWizardCancelledNotice);
                return (true, false, null);
            }

            if (OracleWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>The summary's hidden-save row. Pinned.</summary>
    public const string OracleWizardSaveHiddenRow = "Save, hidden from the model until ticked in Oracle connections offered";

    /// <summary>
    /// The draft tried as it stands, nothing written: a one-entry catalog of it, its password handed over as a plain
    /// <c>file</c> value (which <see cref="OracleSecrets.Resolve"/> passes through) so a <c>credman</c> draft is tested before
    /// its entry exists. Who it signed in as and the server's version on the status line, then a warning when the account
    /// could write; a failure still lets it be saved.
    /// </summary>
    private async Task OracleWizardTestAsync(OracleDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        var copy = new OracleConnectionConfig
        {
            DataSource = c.DataSource,
            Schema = c.Schema,
            User = c.User,
            Password = draft.Password,
            PasswordStore = OracleConnectionConfig.FileStore,
            ConnectTimeoutSeconds = c.ConnectTimeoutSeconds,
        };
        if (copy.Problem is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        var run = await _testOracleConnection(new OracleNamedConnection(draft.Name, copy, OracleWizardPath(draft.Global)), cancellationToken).ConfigureAwait(false);
        if (run.Outcome != Sql.SqlOutcome.Ok)
        {
            Sink.Error(OracleText.Error(run));
            return;
        }

        // user, container, version; then the write powers, one per row.
        var who = run.Grids.Count > 0 && run.Grids[0].Rows.Count > 0 ? run.Grids[0].Rows[0] : ["", "", ""];
        Sink.Notice(OracleText.TestOk(draft.Name, who.ElementAtOrDefault(0) ?? "", who.ElementAtOrDefault(1) ?? "", who.ElementAtOrDefault(2) ?? ""));
        var powers = run.Grids.Count > 1 ? run.Grids[1].Rows.Select(r => r[0]).ToList() : [];
        if (powers.Count > 0)
        {
            Sink.Warning(OracleText.CanWrite(powers));
        }
    }

    /// <summary>The app's test: <see cref="OracleAccess.RunAsync"/> over the one connection, <see cref="OracleCatalogQueries.WhoAmI"/> then <see cref="OracleCatalogQueries.WritePowers"/>, the Oracle tab's timeout.</summary>
    private Task<Sql.SqlRun> TestOracleConnectionAsync(OracleNamedConnection connection, CancellationToken cancellationToken) =>
        new OracleAccess(() => new OracleCatalog([connection], []))
            .RunAsync(connection.Name, null, null, [OracleCatalogQueries.WhoAmI, OracleCatalogQueries.WritePowers], [], 50, _settings.Current.OracleQueryTimeoutSeconds, cancellationToken);

    /// <summary>
    /// Writes the draft: the entry (<see cref="OracleConfigFile.AddConnection"/>), then its password to its store, then — when
    /// <paramref name="offer"/> — its name added there. Whether a setting changed;
    /// null when nothing was written (the status line says why), the summary shown again.
    /// </summary>
    private bool? OracleWizardSave(OracleDraft draft, bool offer)
    {
        var c = draft.Config;
        if (OracleWizardMissing(draft) is { } missing)
        {
            Sink.Error(Sql.SqlText.ConnectionAddFailed(draft.Name, OracleWizardLabels[(int)missing] + " is not set"));
            return null;
        }

        if (c.Problem is { } problem)
        {
            Sink.Error(Sql.SqlText.ConnectionAddFailed(draft.Name, problem));
            return null;
        }

        string path = OracleWizardPath(draft.Global);
        if (OracleConfigFile.AddConnection(path, draft.Name, c) is { } error)
        {
            Sink.Error(Sql.SqlText.ConnectionAddFailed(draft.Name, error));
            return null;
        }

        Sink.Notice(Sql.SqlText.ConnectionAdded(draft.Name, path));
        var (saved, notice) = OracleSecrets.Save(new OracleNamedConnection(draft.Name, c, path), draft.Password);
        if (saved)
        {
            Sink.Notice(notice);
        }
        else
        {
            Sink.Error(notice);
        }

        if (!offer)
        {
            return false;
        }

        var offered = _settings.Current.OracleConnectionsOffered ?? [];   // null offers none (2026-10-01)
        Apply(SettingsField.OracleConnectionsOffered, d => d.OracleConnectionsOffered = [.. offered, draft.Name]);
        return true;
    }
}
