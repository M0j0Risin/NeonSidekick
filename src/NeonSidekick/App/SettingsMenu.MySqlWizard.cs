using System.Globalization;
using NeonSidekick.MySql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>MySQL add/edit connection</c> (2026-09-30), the <c>Oracle add connection</c> wizard's shape over <c>mysql.json</c>: one page per
/// choice — which file, the name, the host, the port, the database, the user, where its password is kept, the password (masked),
/// the TLS mode, the connect timeout, the access (2026-10-05: read, or readwrite for <c>mysql_execute</c>), the description — then a summary that tests the unsaved draft (who it signs in as, the
/// server and its version, and a warning when <c>SHOW GRANTS</c> says the account could change data: the tools never will, but a
/// SELECT-only account is the real guard) and saves it (<see cref="MySqlConfigFile.AddConnection"/>, the password after it through
/// <see cref="MySqlSecrets.Save"/>). ESC steps back, and on the first page ends the visit with nothing written; Enter on a summary
/// row changes that choice and comes back. It edits an entry too since 2026-10-05 (the user's ask): a first page of the entries, a pick prefilled (the ConnectionEdit part).
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The wizard's rows, one per <see cref="MySqlWizardStep"/> before the summary, in its order. Pinned.</summary>
    public static readonly IReadOnlyList<string> MySqlWizardLabels =
        ["File", "Name", "Host", "Port", "Database", "User", "Password store", "Password", "TLS mode", "Connect timeout (s)", "Access", "Description"];

    public const string MySqlWizardFileQuestion = "Which mysql.json: this profile's or the global one?";
    public const string MySqlWizardHostQuestion = "The server's host name or address (localhost, db01.example.com).";
    public const string MySqlWizardPortQuestion = "The TCP port; empty for 3306.";
    public const string MySqlWizardDatabaseQuestion = "The database a call works in when it names none; empty to work across every database the user can see.";
    public const string MySqlWizardUserQuestion = "The database user (the real guard is an account with only SELECT grants).";
    public const string MySqlWizardTlsQuestion = "Encryption of the connection.";
    public const string MySqlWizardHostRequired = "A host is required.";
    public const string MySqlWizardSaveHiddenRow = "Save, hidden from the model until ticked in MySQL connections offered";

    public static string MySqlWizardPortError(string typed) => $"'{typed}' is not a port (1 to 65535).";

    /// <summary>The store picks, <c>file</c> then <c>credman</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> MySqlWizardStoreRows =
        Sql.SqlText.StoreRows("mysql.json");

    /// <summary>The TLS picks, <see cref="MySqlConnectionConfig.SslModeWords"/>' order. Pinned.</summary>
    public static readonly IReadOnlyList<string> MySqlWizardTlsRows =
    [
        "preferred    encrypted when the server offers it (the default)",
        "required     always encrypted, the certificate not checked",
        "verify-ca    encrypted, the certificate checked against a trusted authority",
        "verify-full  encrypted, the certificate and the host name checked",
        "none         never encrypted",
    ];

    public static readonly string MySqlWizardTimeoutQuestion =
        "Seconds a connect may take, 1 to " + Invariant(MySqlConnectionConfig.MaxConnectTimeoutSeconds) + "; empty for " + Invariant(MySqlConnectionConfig.DefaultConnectTimeoutSeconds) + ".";

    /// <summary>The step a wizard page asks; the summary last. The rows of <see cref="MySqlWizardLabels"/> by index.</summary>
    internal enum MySqlWizardStep
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
    private sealed class MySqlDraft
    {
        public bool Global { get; set; }

        public string Name { get; set; } = "";

        public string Password { get; set; } = "";

        /// <summary>An edit's entry name in its file (2026-10-05); null for a new connection.</summary>
        public string? Original { get; init; }

        /// <summary>An edit's Credential Manager target under its old name's default, removed when the save moves the password.</summary>
        public string? OriginalTarget { get; init; }

        /// <summary>Whether <see cref="Password"/> is the stored one an edit read, not one typed.</summary>
        public bool PasswordKept { get; set; }

        public MySqlConnectionConfig Config { get; init; } = new() { PasswordStore = MySqlConnectionConfig.FileStore };
    }

    private string MySqlWizardPath(bool global) =>
        global ? MySqlConfigFile.GlobalPath(_settings.StorageDirectory) : MySqlConfigFile.ProfilePath(_settings.ProfileDirectory);

    /// <summary>The first step the draft still lacks (a name, a host, a user, a password), or null.</summary>
    private static MySqlWizardStep? MySqlWizardMissing(MySqlDraft draft) =>
        draft.Name.Length == 0 ? MySqlWizardStep.Name
        : string.IsNullOrWhiteSpace(draft.Config.Host) ? MySqlWizardStep.Host
        : string.IsNullOrWhiteSpace(draft.Config.User) ? MySqlWizardStep.User
        : draft.Password.Length == 0 ? MySqlWizardStep.Password
        : null;

    /// <summary>The value column of the draft's row for <paramref name="step"/>.</summary>
    private string MySqlWizardValue(MySqlWizardStep step, MySqlDraft draft)
    {
        var c = draft.Config;
        static string OrUnset(string? text) => string.IsNullOrWhiteSpace(text) ? SqlWizardUnset : text.Trim();
        return step switch
        {
            MySqlWizardStep.File => SqlWizardFileRow(draft.Global, MySqlWizardPath(draft.Global)),
            MySqlWizardStep.Name => OrUnset(draft.Name),
            MySqlWizardStep.Host => OrUnset(c.Host),
            MySqlWizardStep.Port => Invariant(c.Port ?? MySqlConnectionConfig.DefaultPort),
            MySqlWizardStep.Database => string.IsNullOrWhiteSpace(c.Database) ? "(every database)" : c.Database.Trim(),
            MySqlWizardStep.User => OrUnset(c.User),
            MySqlWizardStep.Store => c.InCredentialManager ? MySqlConnectionConfig.CredmanStore + " (" + c.CredentialTarget(draft.Name.Length > 0 ? draft.Name : "<name>") + ")" : MySqlConnectionConfig.FileStore,
            MySqlWizardStep.Password => draft.Password.Length > 0 ? (draft.PasswordKept ? SqlWizardMaskedKept : SqlWizardMasked) : SqlWizardUnset,
            MySqlWizardStep.Tls => c.SslMode ?? MySqlConnectionConfig.SslModeWords[0],
            MySqlWizardStep.Timeout => Invariant(c.ConnectTimeoutSeconds ?? MySqlConnectionConfig.DefaultConnectTimeoutSeconds),
            MySqlWizardStep.Access => DatabaseWizardAccessValue(c.IsReadWrite),
            _ => OrUnset(c.Description),
        };
    }

    private List<string> MySqlWizardRows(MySqlDraft draft)
    {
        int width = MySqlWizardLabels.Max(l => l.Length) + 2;
        return MySqlWizardLabels.Select((label, i) => Markup.Escape(label.PadRight(width) + MySqlWizardValue((MySqlWizardStep)i, draft))).ToList();
    }

    private string MySqlWizardTitle => Crumb(FieldName(SettingsField.MySqlAddConnection));

    /// <summary>The wizard: its steps in order, ESC one back (before the first: nothing written), a change from the summary back to it. True when a setting changed.</summary>
    private async Task<bool> AddMySqlConnectionAsync(CancellationToken cancellationToken)
    {
        // The first page of the entries when there are any (2026-10-05, the user's ask), as the SQL wizard's.
        while (true)
        {
            var entries = ConnectionWizardEntries(MySqlWizardPath(false), MySqlWizardPath(true), path => MySqlConfigFile.Load(path).Connections);
            var draft = new MySqlDraft();
            if (entries.Count > 0)
            {
                if (await ConnectionWizardPickAsync(MySqlWizardTitle, "connection", entries.Select(e => (e.Named.Name, e.Global)).ToList(), MySqlWizardPath, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    Sink.Notice(SqlWizardCancelledNotice);
                    return false;
                }

                if (picked >= 0)
                {
                    draft = MySqlWizardDraftFrom(entries[picked].Named, entries[picked].Global);
                }
            }

            if (await RunMySqlWizardAsync(draft, cancellationToken).ConfigureAwait(false) is { } changed)
            {
                return changed;
            }

            if (entries.Count == 0)
            {
                Sink.Notice(SqlWizardCancelledNotice);
                return false;
            }
        }
    }

    /// <summary>An edit's draft (2026-10-05): the entry copied, its file and name kept as the original, its stored password read.</summary>
    private MySqlDraft MySqlWizardDraftFrom(MySqlNamedConnection named, bool global)
    {
        var c = named.Config;
        var draft = new MySqlDraft
        {
            Global = global,
            Name = named.Name,
            Original = named.Name,
            OriginalTarget = c.InCredentialManager && c.Credential is null ? c.CredentialTarget(named.Name) : null,
            Config = CloneEntry(c, MySqlJsonContext.Default.MySqlConnectionConfig, x => x.Password = null),
        };
        draft.Password = StoredPassword(MySqlSecrets.Resolve(named));
        draft.PasswordKept = draft.Password.Length > 0;
        return draft;
    }

    /// <summary>The wizard on <paramref name="draft"/>: null for ESC on its first page (the File page, or an edit's Name), else whether a setting changed.</summary>
    private async Task<bool?> RunMySqlWizardAsync(MySqlDraft draft, CancellationToken cancellationToken)
    {
        // An edit opens on its summary (2026-10-05): Enter on a row changes that one; a stored password that could not be
        // read asks first.
        var step = draft.Original is null ? MySqlWizardStep.File : MySqlWizardMissing(draft) ?? MySqlWizardStep.Summary;
        bool fromSummary = draft.Original is not null;
        while (true)
        {
            if (step == MySqlWizardStep.Summary)
            {
                var (done, changed, edit) = await MySqlWizardSummaryAsync(draft, cancellationToken).ConfigureAwait(false);
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
                    return null;   // an edit's ESC on its summary: back to the list
                }

                step = MySqlWizardStep.Description;
                continue;
            }

            if (await MySqlWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false))
            {
                step = fromSummary ? MySqlWizardMissing(draft) ?? MySqlWizardStep.Summary : step + 1;
                fromSummary = fromSummary && step != MySqlWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = MySqlWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            if (step == (draft.Original is null ? MySqlWizardStep.File : MySqlWizardStep.Name))
            {
                return null;
            }

            step--;
        }
    }

    /// <summary>One step's page: a pick or a typed value into the draft; true when it was answered, false for ESC.</summary>
    private async Task<bool> MySqlWizardStepAsync(MySqlWizardStep step, MySqlDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        switch (step)
        {
            case MySqlWizardStep.File:
            {
                string[] rows = [SqlWizardFileRow(false, MySqlWizardPath(false)), SqlWizardFileRow(true, MySqlWizardPath(true))];
                if (await MySqlWizardPickAsync(MySqlWizardFileQuestion, rows, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case MySqlWizardStep.Name:
                return await MySqlWizardTypeAsync(step, draft, SqlWizardNameQuestion, draft.Name, allowEmpty: false, mask: false, text =>
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

                    string path = MySqlWizardPath(draft.Global);
                    if (MySqlConfigFile.Load(path).Connections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Name, draft.Original, StringComparison.OrdinalIgnoreCase)))
                    {
                        return SqlWizardNameTaken(name, path);
                    }

                    draft.Name = name;
                    string other = MySqlWizardPath(!draft.Global);
                    if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                        && MySqlConfigFile.Load(other).Connections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Sink.Warning(draft.Global ? SqlWizardShadowedWarning(name, other) : SqlWizardShadowsWarning(name, other));
                    }

                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case MySqlWizardStep.Host:
                return await MySqlWizardTypeAsync(step, draft, MySqlWizardHostQuestion, c.Host ?? "", allowEmpty: false, mask: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return MySqlWizardHostRequired;
                    }

                    c.Host = text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case MySqlWizardStep.Port:
                return await MySqlWizardTypeAsync(step, draft, MySqlWizardPortQuestion, c.Port is { } p ? Invariant(p) : "", allowEmpty: true, mask: false, text =>
                {
                    string typed = text.Trim();
                    if (typed.Length == 0)
                    {
                        c.Port = null;
                        return null;
                    }

                    if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
                    {
                        return MySqlWizardPortError(typed);
                    }

                    c.Port = port == MySqlConnectionConfig.DefaultPort ? null : port;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case MySqlWizardStep.Database:
                return await MySqlWizardTypeAsync(step, draft, MySqlWizardDatabaseQuestion, c.Database ?? "", allowEmpty: true, mask: false, text =>
                {
                    c.Database = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case MySqlWizardStep.User:
                return await MySqlWizardTypeAsync(step, draft, MySqlWizardUserQuestion, c.User ?? "", allowEmpty: false, mask: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return SqlWizardUserRequired;
                    }

                    c.User = text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case MySqlWizardStep.Store:
            {
                if (await MySqlWizardPickAsync(SqlWizardStoreQuestion, MySqlWizardStoreRows, c.InCredentialManager ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.PasswordStore = picked == 1 ? MySqlConnectionConfig.CredmanStore : MySqlConnectionConfig.FileStore;
                return true;
            }

            case MySqlWizardStep.Password:
                return await MySqlWizardTypeAsync(step, draft, SqlWizardPasswordQuestion, draft.Password, allowEmpty: false, mask: true, text =>
                {
                    if (text.Length == 0)
                    {
                        return SqlWizardPasswordRequired;
                    }

                    draft.Password = text;
                    draft.PasswordKept = false;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case MySqlWizardStep.Tls:
            {
                int current = Math.Max(0, MySqlConnectionConfig.SslModeWords.ToList().IndexOf(c.SslMode ?? ""));
                if (await MySqlWizardPickAsync(MySqlWizardTlsQuestion, MySqlWizardTlsRows, current, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.SslMode = picked == 0 ? null : MySqlConnectionConfig.SslModeWords[picked];
                return true;
            }

            case MySqlWizardStep.Timeout:
                return await MySqlWizardTypeAsync(step, draft, MySqlWizardTimeoutQuestion, c.ConnectTimeoutSeconds is { } s ? Invariant(s) : "", allowEmpty: true, mask: false, text =>
                {
                    string typed = text.Trim();
                    if (typed.Length == 0)
                    {
                        c.ConnectTimeoutSeconds = null;
                        return null;
                    }

                    if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 1 || seconds > MySqlConnectionConfig.MaxConnectTimeoutSeconds)
                    {
                        return SqlWizardTimeoutError(typed);
                    }

                    c.ConnectTimeoutSeconds = seconds;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case MySqlWizardStep.Access:
            {
                if (await MySqlWizardPickAsync(DatabaseWizardAccessQuestion, DatabaseWizardAccessRows(MySql.MySqlStatementKinds.Family), c.IsReadWrite ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Access = Sql.ConnectionAccess.Stored(picked == 1);
                NoticeAccessModeOff(MySql.MySqlStatementKinds.Family, c.IsReadWrite, _settings.Current.MySqlMode);
                return true;
            }

            default:
                return await MySqlWizardTypeAsync(step, draft, SqlWizardDescriptionQuestion, c.Description ?? "", allowEmpty: true, mask: false, text =>
                {
                    c.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<int?> MySqlWizardPickAsync(string question, IReadOnlyList<string> choices, int cursor, CancellationToken cancellationToken)
    {
        var page = new MenuPage(MySqlWizardTitle, choices.Select(Markup.Escape).ToList(), PickKeys) { Caption = question };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        return PickAsync(page, cursor, cancellationToken);
    }

    /// <summary>A typed step: the draft's rows with the step's marked, the question as the caption; a wrong answer asks again with the error on the status line. True once accepted, false for ESC.</summary>
    private async Task<bool> MySqlWizardTypeAsync(MySqlWizardStep step, MySqlDraft draft, string question, string initial, bool allowEmpty, bool mask, Func<string, string?> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(MySqlWizardTitle, MySqlWizardRows(draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, (int)step, _input, initial, allowEmpty, cancellationToken, mask).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(MySqlWizardLabels[(int)step] + " · " + question, EditKeys));
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
    private async Task<(bool Done, bool Changed, MySqlWizardStep? Edit)> MySqlWizardSummaryAsync(MySqlDraft draft, CancellationToken cancellationToken)
    {
        int cursor = ConnectionWizardStartCursor(draft.Original, _settings.Current.MySqlConnectionsOffered);
        while (true)
        {
            // Always offer-or-hide (2026-10-01): nothing is offered until ticked, so the wizard is where a new one is.
            var actions = new List<string> { SqlWizardSaveOfferedRow, MySqlWizardSaveHiddenRow };
            int test = actions.Count;
            actions.Add(SqlWizardTestRow);
            actions.Add(SqlWizardCancelRow);
            var rows = actions.Select(Markup.Escape).Concat(MySqlWizardRows(draft)).ToList();
            var page = new MenuPage(MySqlWizardTitle, rows, PickKeys) { Caption = SqlWizardSummaryCaption };
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
                var edit = (MySqlWizardStep)(picked - actions.Count);
                if (edit == MySqlWizardStep.File && draft.Original is not null)
                {
                    continue;   // an edit's file is fixed
                }

                return (false, false, edit);
            }

            if (picked == test)
            {
                await MySqlWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == test + 1)
            {
                Sink.Notice(draft.Original is null ? SqlWizardCancelledNotice : ConnectionWizardUnchangedNotice);
                return (true, false, null);
            }

            if (MySqlWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>The draft tried as it stands, nothing written: who it signed in as and the server on the status line, then a warning when the account could write; a failure still lets it be saved.</summary>
    private async Task MySqlWizardTestAsync(MySqlDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        var copy = new MySqlConnectionConfig
        {
            Host = c.Host,
            Port = c.Port,
            Database = c.Database,
            User = c.User,
            Password = draft.Password,
            PasswordStore = MySqlConnectionConfig.FileStore,
            SslMode = c.SslMode,
            AllowPublicKeyRetrieval = c.AllowPublicKeyRetrieval,
            ConnectTimeoutSeconds = c.ConnectTimeoutSeconds,
            Access = c.Access,
        };
        if (copy.Problem is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        var run = await _testMySqlConnection(new MySqlNamedConnection(draft.Name, copy, MySqlWizardPath(draft.Global)), cancellationToken).ConfigureAwait(false);
        if (run.Outcome != Sql.SqlOutcome.Ok)
        {
            Sink.Error(MySqlText.Error(run));
            return;
        }

        // user, database, version; then the grants, one per row.
        var who = run.Grids.Count > 0 && run.Grids[0].Rows.Count > 0 ? run.Grids[0].Rows[0] : ["", "", ""];
        Sink.Notice(MySqlText.TestOk(draft.Name, who.ElementAtOrDefault(0) ?? "", who.ElementAtOrDefault(2) ?? ""));
        var powers = MySqlText.WritePowers(run.Grids.Count > 1 ? run.Grids[1].Rows.Select(r => r[0]) : []);
        if (powers.Count > 0)
        {
            Sink.Warning(MySqlText.CanWrite(powers, c.IsReadWrite));
        }
    }

    /// <summary>The app's test: <see cref="MySqlAccess.RunAsync"/> over the one connection, <see cref="MySqlCatalogQueries.WhoAmI"/> then <see cref="MySqlCatalogQueries.Grants"/>, the MySQL tab's timeout.</summary>
    private Task<Sql.SqlRun> TestMySqlConnectionAsync(MySqlNamedConnection connection, CancellationToken cancellationToken) =>
        new MySqlAccess(() => new MySqlCatalog([connection], []))
            .RunAsync(connection.Name, null, null, [MySqlCatalogQueries.WhoAmI, MySqlCatalogQueries.Grants], [], 50, _settings.Current.MySqlQueryTimeoutSeconds, cancellationToken);

    /// <summary>Writes the draft: the entry, then its password to its store, then — when <paramref name="offer"/> — its name there. Null when nothing was written.</summary>
    private bool? MySqlWizardSave(MySqlDraft draft, bool offer)
    {
        var c = draft.Config;
        if (MySqlWizardMissing(draft) is { } missing)
        {
            Sink.Error(Sql.SqlText.ConnectionAddFailed(draft.Name, MySqlWizardLabels[(int)missing] + " is not set"));
            return null;
        }

        if (c.Problem is { } problem)
        {
            Sink.Error(Sql.SqlText.ConnectionAddFailed(draft.Name, problem));
            return null;
        }

        string path = MySqlWizardPath(draft.Global);
        if ((draft.Original is { } original ? MySqlConfigFile.ReplaceConnection(path, original, draft.Name, c) : MySqlConfigFile.AddConnection(path, draft.Name, c)) is { } error)
        {
            Sink.Error(draft.Original is null ? Sql.SqlText.ConnectionAddFailed(draft.Name, error) : Sql.SqlText.ConnectionChangeFailed(draft.Name, error));
            return null;
        }

        Sink.Notice(draft.Original is null ? Sql.SqlText.ConnectionAdded(draft.Name, path) : Sql.SqlText.ConnectionChanged(draft.Name, path));
        var (saved, notice) = MySqlSecrets.Save(new MySqlNamedConnection(draft.Name, c, path), draft.Password);
        if (saved)
        {
            Sink.Notice(notice);
        }
        else
        {
            Sink.Error(notice);
        }

        if (saved)
        {
            ForgetOldCredential(draft.OriginalTarget, c.InCredentialManager ? c.CredentialTarget(draft.Name) : null);
        }

        return ApplyOfferedAfterSave(SettingsField.MySqlConnectionsOffered, _settings.Current.MySqlConnectionsOffered, draft.Original, draft.Name, offer, (d, next) => d.MySqlConnectionsOffered = next);
    }
}
