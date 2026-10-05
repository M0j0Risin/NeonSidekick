using System.Globalization;
using NeonSidekick.Postgres;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>PostgreSQL add connection</c> (2026-10-04), the <c>Oracle add connection</c> wizard's shape over <c>postgres.json</c>: one page per
/// choice — which file, the name, the host, the port, the database, the user, where its password is kept, the password (masked),
/// the TLS mode, the connect timeout, the access (2026-10-05: read, or readwrite for <c>postgres_execute</c>), the description — then a summary that tests the unsaved draft (who it signs in as, the
/// server and its version, and a warning when the role's attributes or grants say it could change data (a superuser is warned of, not refused: the user's call, 2026-10-04): the tools never will, but a
/// SELECT-only account is the real guard) and saves it (<see cref="PostgresConfigFile.AddConnection"/>, the password after it through
/// <see cref="PostgresSecrets.Save"/>). ESC steps back, and on the first page ends the visit with nothing written; Enter on a summary
/// row changes that choice and comes back. Adds only: an existing entry is edited in the file.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The wizard's rows, one per <see cref="PostgresWizardStep"/> before the summary, in its order. Pinned.</summary>
    public static readonly IReadOnlyList<string> PostgresWizardLabels =
        ["File", "Name", "Host", "Port", "Database", "User", "Password store", "Password", "TLS mode", "Connect timeout (s)", "Access", "Description"];

    public const string PostgresWizardFileQuestion = "Scope for postgres.json?";
    public const string PostgresWizardHostQuestion = "The server's host name or address (localhost, db01.example.com).";
    public const string PostgresWizardPortQuestion = "The TCP port; empty for 5432.";
    public const string PostgresWizardDatabaseQuestion = "The database a call works in when it names none; empty for postgres.";
    public const string PostgresWizardUserQuestion = "The database user (an account with SELECT grants alone is the real guard).";
    public const string PostgresWizardTlsQuestion = "Encryption of the connection.";
    public const string PostgresWizardHostRequired = "A host is required.";
    public const string PostgresWizardSaveHiddenRow = "Save, hidden from the model until ticked in PostgreSQL connections offered";

    public static string PostgresWizardPortError(string typed) => $"'{typed}' is not a port (1 to 65535).";

    /// <summary>The store picks, <c>file</c> then <c>credman</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> PostgresWizardStoreRows =
        ["file     encrypted (DPAPI) in postgres.json", "credman  Windows Credential Manager"];

    /// <summary>The TLS picks, <see cref="PostgresConnectionConfig.SslModeWords"/>' order. Pinned.</summary>
    public static readonly IReadOnlyList<string> PostgresWizardTlsRows =
    [
        "preferred    encrypted when the server offers it (the default)",
        "required     always encrypted, the certificate not checked",
        "verify-ca    encrypted, the certificate checked against a trusted authority",
        "verify-full  encrypted, the certificate and the host name checked",
        "none         never encrypted",
    ];

    public static readonly string PostgresWizardTimeoutQuestion =
        "Seconds a connect may take, 1 to " + Invariant(PostgresConnectionConfig.MaxConnectTimeoutSeconds) + "; empty for " + Invariant(PostgresConnectionConfig.DefaultConnectTimeoutSeconds) + ".";

    /// <summary>The step a wizard page asks; the summary last. The rows of <see cref="PostgresWizardLabels"/> by index.</summary>
    internal enum PostgresWizardStep
    {
        File,
        Name,
        Host,
        Port,
        Database,
        User,
        Store,
        Password,
        Tls,
        Timeout,
        Access,
        Description,
        Summary,
    }

    /// <summary>What the wizard has so far: the file, the name, the entry as it will be written and the password it will store.</summary>
    private sealed class PostgresDraft
    {
        public bool Global { get; set; }

        public string Name { get; set; } = "";

        public string Password { get; set; } = "";

        public PostgresConnectionConfig Config { get; } = new() { PasswordStore = PostgresConnectionConfig.FileStore };
    }

    private string PostgresWizardPath(bool global) =>
        global ? PostgresConfigFile.GlobalPath(_settings.StorageDirectory) : PostgresConfigFile.ProfilePath(_settings.ProfileDirectory);

    /// <summary>The first step the draft still lacks (a name, a host, a user, a password), or null.</summary>
    private static PostgresWizardStep? PostgresWizardMissing(PostgresDraft draft) =>
        draft.Name.Length == 0 ? PostgresWizardStep.Name
        : string.IsNullOrWhiteSpace(draft.Config.Host) ? PostgresWizardStep.Host
        : string.IsNullOrWhiteSpace(draft.Config.User) ? PostgresWizardStep.User
        : draft.Password.Length == 0 ? PostgresWizardStep.Password
        : null;

    /// <summary>The value column of the draft's row for <paramref name="step"/>.</summary>
    private string PostgresWizardValue(PostgresWizardStep step, PostgresDraft draft)
    {
        var c = draft.Config;
        static string OrUnset(string? text) => string.IsNullOrWhiteSpace(text) ? SqlWizardUnset : text.Trim();
        return step switch
        {
            PostgresWizardStep.File => SqlWizardFileRow(draft.Global, PostgresWizardPath(draft.Global)),
            PostgresWizardStep.Name => OrUnset(draft.Name),
            PostgresWizardStep.Host => OrUnset(c.Host),
            PostgresWizardStep.Port => Invariant(c.Port ?? PostgresConnectionConfig.DefaultPort),
            PostgresWizardStep.Database => string.IsNullOrWhiteSpace(c.Database) ? PostgresConnectionConfig.DefaultDatabase : c.Database.Trim(),
            PostgresWizardStep.User => OrUnset(c.User),
            PostgresWizardStep.Store => c.InCredentialManager ? PostgresConnectionConfig.CredmanStore + " (" + c.CredentialTarget(draft.Name.Length > 0 ? draft.Name : "<name>") + ")" : PostgresConnectionConfig.FileStore,
            PostgresWizardStep.Password => draft.Password.Length > 0 ? SqlWizardMasked : SqlWizardUnset,
            PostgresWizardStep.Tls => c.SslMode ?? PostgresConnectionConfig.SslModeWords[0],
            PostgresWizardStep.Timeout => Invariant(c.ConnectTimeoutSeconds ?? PostgresConnectionConfig.DefaultConnectTimeoutSeconds),
            PostgresWizardStep.Access => DatabaseWizardAccessValue(c.IsReadWrite),
            _ => OrUnset(c.Description),
        };
    }

    private List<string> PostgresWizardRows(PostgresDraft draft)
    {
        int width = PostgresWizardLabels.Max(l => l.Length) + 2;
        return PostgresWizardLabels.Select((label, i) => Markup.Escape(label.PadRight(width) + PostgresWizardValue((PostgresWizardStep)i, draft))).ToList();
    }

    private string PostgresWizardTitle => Crumb(FieldName(SettingsField.PostgresAddConnection));

    /// <summary>The wizard: its steps in order, ESC one back (before the first: nothing written), a change from the summary back to it. True when a setting changed.</summary>
    private async Task<bool> AddPostgresConnectionAsync(CancellationToken cancellationToken)
    {
        var draft = new PostgresDraft();
        var step = PostgresWizardStep.File;
        bool fromSummary = false;
        while (true)
        {
            if (step == PostgresWizardStep.Summary)
            {
                var (done, changed, edit) = await PostgresWizardSummaryAsync(draft, cancellationToken).ConfigureAwait(false);
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

                step = PostgresWizardStep.Description;
                continue;
            }

            if (await PostgresWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false))
            {
                step = fromSummary ? PostgresWizardMissing(draft) ?? PostgresWizardStep.Summary : step + 1;
                fromSummary = fromSummary && step != PostgresWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = PostgresWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            if (step == PostgresWizardStep.File)
            {
                Sink.Notice(SqlWizardCancelledNotice);
                return false;
            }

            step--;
        }
    }

    /// <summary>One step's page: a pick or a typed value into the draft; true when it was answered, false for ESC.</summary>
    private async Task<bool> PostgresWizardStepAsync(PostgresWizardStep step, PostgresDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        switch (step)
        {
            case PostgresWizardStep.File:
            {
                string[] rows = [SqlWizardFileRow(false, PostgresWizardPath(false)), SqlWizardFileRow(true, PostgresWizardPath(true))];
                if (await PostgresWizardPickAsync(PostgresWizardFileQuestion, rows, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case PostgresWizardStep.Name:
                return await PostgresWizardTypeAsync(step, draft, SqlWizardNameQuestion, draft.Name, allowEmpty: false, mask: false, text =>
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

                    string path = PostgresWizardPath(draft.Global);
                    if (PostgresConfigFile.Load(path).Connections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return SqlWizardNameTaken(name, path);
                    }

                    draft.Name = name;
                    string other = PostgresWizardPath(!draft.Global);
                    if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                        && PostgresConfigFile.Load(other).Connections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Sink.Warning(draft.Global ? SqlWizardShadowedWarning(name, other) : SqlWizardShadowsWarning(name, other));
                    }

                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case PostgresWizardStep.Host:
                return await PostgresWizardTypeAsync(step, draft, PostgresWizardHostQuestion, c.Host ?? "", allowEmpty: false, mask: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return PostgresWizardHostRequired;
                    }

                    c.Host = text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case PostgresWizardStep.Port:
                return await PostgresWizardTypeAsync(step, draft, PostgresWizardPortQuestion, c.Port is { } p ? Invariant(p) : "", allowEmpty: true, mask: false, text =>
                {
                    string typed = text.Trim();
                    if (typed.Length == 0)
                    {
                        c.Port = null;
                        return null;
                    }

                    if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
                    {
                        return PostgresWizardPortError(typed);
                    }

                    c.Port = port == PostgresConnectionConfig.DefaultPort ? null : port;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case PostgresWizardStep.Database:
                return await PostgresWizardTypeAsync(step, draft, PostgresWizardDatabaseQuestion, c.Database ?? "", allowEmpty: true, mask: false, text =>
                {
                    c.Database = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case PostgresWizardStep.User:
                return await PostgresWizardTypeAsync(step, draft, PostgresWizardUserQuestion, c.User ?? "", allowEmpty: false, mask: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return SqlWizardUserRequired;
                    }

                    c.User = text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case PostgresWizardStep.Store:
            {
                if (await PostgresWizardPickAsync(SqlWizardStoreQuestion, PostgresWizardStoreRows, c.InCredentialManager ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.PasswordStore = picked == 1 ? PostgresConnectionConfig.CredmanStore : PostgresConnectionConfig.FileStore;
                return true;
            }

            case PostgresWizardStep.Password:
                return await PostgresWizardTypeAsync(step, draft, SqlWizardPasswordQuestion, draft.Password, allowEmpty: false, mask: true, text =>
                {
                    if (text.Length == 0)
                    {
                        return SqlWizardPasswordRequired;
                    }

                    draft.Password = text;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case PostgresWizardStep.Tls:
            {
                int current = Math.Max(0, PostgresConnectionConfig.SslModeWords.ToList().IndexOf(c.SslMode ?? ""));
                if (await PostgresWizardPickAsync(PostgresWizardTlsQuestion, PostgresWizardTlsRows, current, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.SslMode = picked == 0 ? null : PostgresConnectionConfig.SslModeWords[picked];
                return true;
            }

            case PostgresWizardStep.Timeout:
                return await PostgresWizardTypeAsync(step, draft, PostgresWizardTimeoutQuestion, c.ConnectTimeoutSeconds is { } s ? Invariant(s) : "", allowEmpty: true, mask: false, text =>
                {
                    string typed = text.Trim();
                    if (typed.Length == 0)
                    {
                        c.ConnectTimeoutSeconds = null;
                        return null;
                    }

                    if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 1 || seconds > PostgresConnectionConfig.MaxConnectTimeoutSeconds)
                    {
                        return SqlWizardTimeoutError(typed);
                    }

                    c.ConnectTimeoutSeconds = seconds;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case PostgresWizardStep.Access:
            {
                if (await PostgresWizardPickAsync(DatabaseWizardAccessQuestion, DatabaseWizardAccessRows(PostgresStatementKinds.Family), c.IsReadWrite ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Access = Sql.ConnectionAccess.Stored(picked == 1);
                NoticeAccessModeOff(PostgresStatementKinds.Family, c.IsReadWrite, _settings.Current.PostgresMode);
                return true;
            }

            default:
                return await PostgresWizardTypeAsync(step, draft, SqlWizardDescriptionQuestion, c.Description ?? "", allowEmpty: true, mask: false, text =>
                {
                    c.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<int?> PostgresWizardPickAsync(string question, IReadOnlyList<string> choices, int cursor, CancellationToken cancellationToken)
    {
        var page = new MenuPage(PostgresWizardTitle, choices.Select(Markup.Escape).ToList(), PickKeys) { Caption = question };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        return PickAsync(page, cursor, cancellationToken);
    }

    /// <summary>A typed step: the draft's rows with the step's marked, the question as the caption; a wrong answer asks again with the error on the status line. True once accepted, false for ESC.</summary>
    private async Task<bool> PostgresWizardTypeAsync(PostgresWizardStep step, PostgresDraft draft, string question, string initial, bool allowEmpty, bool mask, Func<string, string?> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(PostgresWizardTitle, PostgresWizardRows(draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, (int)step, _input, initial, allowEmpty, cancellationToken, mask).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(PostgresWizardLabels[(int)step] + " · " + question, EditKeys));
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

    /// <summary>The summary: the save rows, the test, the cancel, then every row of the draft. Done with whether a setting changed; or the step a row picked; or neither for ESC (back).</summary>
    private async Task<(bool Done, bool Changed, PostgresWizardStep? Edit)> PostgresWizardSummaryAsync(PostgresDraft draft, CancellationToken cancellationToken)
    {
        int cursor = 0;
        while (true)
        {
            // Always offer-or-hide (2026-10-01): nothing is offered until ticked, so the wizard is where a new one is.
            var actions = new List<string> { SqlWizardSaveOfferedRow, PostgresWizardSaveHiddenRow };
            int test = actions.Count;
            actions.Add(SqlWizardTestRow);
            actions.Add(SqlWizardCancelRow);
            var rows = actions.Select(Markup.Escape).Concat(PostgresWizardRows(draft)).ToList();
            var page = new MenuPage(PostgresWizardTitle, rows, PickKeys) { Caption = SqlWizardSummaryCaption };
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
                return (false, false, (PostgresWizardStep)(picked - actions.Count));
            }

            if (picked == test)
            {
                await PostgresWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == test + 1)
            {
                Sink.Notice(SqlWizardCancelledNotice);
                return (true, false, null);
            }

            if (PostgresWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>The draft tried as it stands, nothing written: who it signed in as, the database and the server on the status line, then a warning when the role could change data; a failure still lets it be saved.</summary>
    private async Task PostgresWizardTestAsync(PostgresDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        var copy = new PostgresConnectionConfig
        {
            Host = c.Host,
            Port = c.Port,
            Database = c.Database,
            User = c.User,
            Password = draft.Password,
            PasswordStore = PostgresConnectionConfig.FileStore,
            SslMode = c.SslMode,
            ConnectTimeoutSeconds = c.ConnectTimeoutSeconds,
            Access = c.Access,
        };
        if (copy.Problem is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        var run = await (TestPostgresConnection ?? TestPostgresConnectionAsync)(new PostgresNamedConnection(draft.Name, copy, PostgresWizardPath(draft.Global)), cancellationToken).ConfigureAwait(false);
        if (run.Outcome != Sql.SqlOutcome.Ok)
        {
            Sink.Error(PostgresText.Error(run));
            return;
        }

        // user, database, version; then the powers, one per row.
        var who = run.Grids.Count > 0 && run.Grids[0].Rows.Count > 0 ? run.Grids[0].Rows[0] : ["", "", ""];
        Sink.Notice(PostgresText.TestOk(draft.Name, who.ElementAtOrDefault(0) ?? "", who.ElementAtOrDefault(1) ?? "", who.ElementAtOrDefault(2) ?? ""));
        var powers = run.Grids.Count > 1 ? run.Grids[1].Rows.Select(r => r[0]).ToList() : [];
        if (powers.Count > 0)
        {
            Sink.Warning(PostgresText.CanWrite(powers, c.IsReadWrite));
        }
    }

    /// <summary>
    /// What the wizard's Test runs over the unsaved draft (2026-10-04): a real <see cref="PostgresAccess"/> run of
    /// <see cref="PostgresCatalogQueries.WhoAmI"/> at the tab's timeout; the tests set their own.
    /// </summary>
    public Func<PostgresNamedConnection, CancellationToken, Task<Sql.SqlRun>>? TestPostgresConnection { get; set; }

    private Task<Sql.SqlRun> TestPostgresConnectionAsync(PostgresNamedConnection connection, CancellationToken cancellationToken) =>
        new PostgresAccess(() => new PostgresCatalog([connection], []))
            .RunAsync(connection.Name, null, null, PostgresCatalogQueries.WhoAmI, [], 50, _settings.Current.PostgresQueryTimeoutSeconds, cancellationToken);

    /// <summary>Writes the draft: the entry, then its password to its store, then — when <paramref name="offer"/> — its name there. Null when nothing was written.</summary>
    private bool? PostgresWizardSave(PostgresDraft draft, bool offer)
    {
        var c = draft.Config;
        if (PostgresWizardMissing(draft) is { } missing)
        {
            Sink.Error(Sql.SqlText.ConnectionAddFailed(draft.Name, PostgresWizardLabels[(int)missing] + " is not set"));
            return null;
        }

        if (c.Problem is { } problem)
        {
            Sink.Error(Sql.SqlText.ConnectionAddFailed(draft.Name, problem));
            return null;
        }

        string path = PostgresWizardPath(draft.Global);
        if (PostgresConfigFile.AddConnection(path, draft.Name, c) is { } error)
        {
            Sink.Error(Sql.SqlText.ConnectionAddFailed(draft.Name, error));
            return null;
        }

        Sink.Notice(Sql.SqlText.ConnectionAdded(draft.Name, path));
        var (saved, notice) = PostgresSecrets.Save(new PostgresNamedConnection(draft.Name, c, path), draft.Password);
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

        var offered = _settings.Current.PostgresConnectionsOffered ?? [];   // null offers none (2026-10-01)
        Apply(SettingsField.PostgresConnectionsOffered, d => d.PostgresConnectionsOffered = [.. offered, draft.Name]);
        return true;
    }
}
