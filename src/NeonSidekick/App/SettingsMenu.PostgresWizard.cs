using System.Globalization;
using NeonSidekick.Postgres;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>PostgreSQL add/edit connection</c> (2026-10-04), the <c>Oracle add connection</c> wizard's shape over <c>postgres.json</c>: one page per
/// choice — which file, the name, the host, the port, the database, the user, where its password is kept, the password (masked),
/// the TLS mode, the connect timeout, the access (2026-10-05: read, or readwrite for <c>postgres_execute</c>), the description — then a summary that tests the unsaved draft (who it signs in as, the
/// server and its version, and a warning when the role's attributes or grants say it could change data (a superuser is warned of, not refused: the user's call, 2026-10-04): the tools never will, but a
/// SELECT-only account is the real guard) and saves it (<see cref="PostgresConfigFile.AddConnection"/>, the password after it through
/// <see cref="PostgresSecrets.Save"/>). ESC steps back, and on the first page ends the visit with nothing written; Enter on a summary
/// row changes that choice and comes back. It edits an entry too since 2026-10-05 (the user's ask): a first page of the entries, a pick prefilled (the ConnectionEdit part).
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The wizard's rows, one per <see cref="PostgresWizardStep"/> before the summary, in its order. Pinned.</summary>
    public static readonly IReadOnlyList<string> PostgresWizardLabels =
        ["File", "Name", "Host", "Port", "Database", "User", "Password store", "Password", "TLS mode", "Connect timeout (s)", "Schemas", "Access", "Description"];

    public const string PostgresWizardFileQuestion = "Which postgres.json: this profile's or the global one?";
    public const string PostgresWizardHostQuestion = "The server's host name or address (localhost, db01.example.com).";
    public const string PostgresWizardPortQuestion = "The TCP port; empty for 5432.";
    public const string PostgresWizardDatabaseQuestion = "The database: the one this connection reaches (the tools never leave it; another database is another connection).";
    public const string PostgresWizardSchemaQuestion = "The schemas to hold the connection to; none ticked leaves every schema of the database open.";

    /// <summary>The checklist's caption on the pane (#31/#33): the order is the rows'.</summary>
    public const string PostgresWizardSchemaOrderNote = "The first ticked comes first on the search path.";

    /// <summary>The typed fallback's question, when the schemas could not be listed (#18).</summary>
    public const string PostgresWizardSchemaTypedQuestion = "The schemas to hold the connection to, comma-separated in search-path order, or empty for every schema.";

    /// <summary>The first order page's question, with two or more ticked and no pane (#18; on the pane the rows are moved instead, #31).</summary>
    public const string PostgresWizardFirstQuestion = "Which comes first on the search path (a name in two schemas resolves there)?";
    public const string PostgresWizardDatabaseRequired = "A database is required: the connection reaches that one alone.";
    public const string PostgresWizardEverySchema = "(every schema)";
    public const string PostgresWizardUserQuestion = "The database user (the real guard is an account with only SELECT grants).";
    public const string PostgresWizardTlsQuestion = "Encryption of the connection.";
    public const string PostgresWizardHostRequired = "A host is required.";
    public const string PostgresWizardSaveHiddenRow = "Save, hidden from the model until you tick it in PostgreSQL connections offered";

    public static string PostgresWizardPortError(string typed) => $"'{typed}' is not a port (1 to 65535).";

    /// <summary>The store picks, <c>file</c> then <c>credman</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> PostgresWizardStoreRows =
        Sql.SqlText.StoreRows("postgres.json");

    /// <summary>The TLS picks, <see cref="PostgresConnectionConfig.SslModeWords"/>' order. Pinned.</summary>
    public static readonly IReadOnlyList<string> PostgresWizardTlsRows =
    [
        "preferred    encrypted when the server offers it, the certificate not checked",
        "required     always encrypted, the certificate not checked",
        "verify-ca    encrypted, the certificate checked against a trusted authority",
        "verify-full  encrypted, the certificate and the host name checked (a new connection's)",
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
        Schema,
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

        /// <summary>An edit's entry name in its file (2026-10-05); null for a new connection.</summary>
        public string? Original { get; init; }

        /// <summary>An edit's Credential Manager target under its old name's default, removed when the save moves the password.</summary>
        public string? OriginalTarget { get; init; }

        /// <summary>Whether <see cref="Password"/> is the stored one an edit read, not one typed.</summary>
        public bool PasswordKept { get; set; }

        /// <summary>An edit's host as read (2026-10-24, review E9): a move away from it lets go of the kept password.</summary>
        public string? OriginalHost { get; init; }

        public PostgresConnectionConfig Config { get; init; } = new() { PasswordStore = PostgresConnectionConfig.FileStore, SslMode = PostgresConnectionConfig.NewConnectionSslMode };

        /// <summary>The Schemas step's last listing (#99).</summary>
        public SchemaListCache Schemas { get; } = new();
    }

    private string PostgresWizardPath(bool global) =>
        global ? PostgresConfigFile.GlobalPath(_settings.StorageDirectory) : PostgresConfigFile.ProfilePath(_settings.ProfileDirectory);

    /// <summary>The first step the draft still lacks (a name, a host, a database, a user, a password), or null.</summary>
    private static PostgresWizardStep? PostgresWizardMissing(PostgresDraft draft) =>
        draft.Name.Length == 0 ? PostgresWizardStep.Name
        : string.IsNullOrWhiteSpace(draft.Config.Host) ? PostgresWizardStep.Host
        : string.IsNullOrWhiteSpace(draft.Config.Database) ? PostgresWizardStep.Database
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
            PostgresWizardStep.Database => OrUnset(c.Database),
            PostgresWizardStep.Schema => c.LockText ?? PostgresWizardEverySchema,
            PostgresWizardStep.User => OrUnset(c.User),
            PostgresWizardStep.Store => c.InCredentialManager ? PostgresConnectionConfig.CredmanStore + " (" + c.CredentialTarget(draft.Name.Length > 0 ? draft.Name : "<name>") + ")" : PostgresConnectionConfig.FileStore,
            PostgresWizardStep.Password => draft.Password.Length > 0 ? (draft.PasswordKept ? SqlWizardMaskedKept : SqlWizardMasked) : SqlWizardUnset,
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
        // The first page of the entries when there are any (2026-10-05, the user's ask), as the SQL wizard's.
        while (true)
        {
            var entries = ConnectionWizardEntries(PostgresWizardPath(false), PostgresWizardPath(true), path => PostgresConfigFile.Load(path).Connections, c => c.Name);
            var draft = new PostgresDraft();
            if (entries.Count > 0)
            {
                if (await ConnectionWizardPickAsync(PostgresWizardTitle, "connection", entries.Select(e => (e.Named.Name, e.Global)).ToList(), PostgresWizardPath, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    Sink.Notice(SqlWizardCancelledNotice);
                    return false;
                }

                if (picked >= 0)
                {
                    draft = PostgresWizardDraftFrom(entries[picked].Named, entries[picked].Global);
                }
            }

            if (await RunPostgresWizardAsync(draft, cancellationToken).ConfigureAwait(false) is { } changed)
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
    private PostgresDraft PostgresWizardDraftFrom(PostgresNamedConnection named, bool global)
    {
        var c = named.Config;
        var draft = new PostgresDraft
        {
            Global = global,
            Name = named.Name,
            Original = named.Name,
            OriginalHost = c.SignInHost,
            OriginalTarget = c.InCredentialManager && string.IsNullOrWhiteSpace(c.Credential) ? c.CredentialTarget(named.Name) : null,   // a blank credential is the default too (2026-10-24, review N13)
            Config = CloneEntry(c, PostgresJsonContext.Default.PostgresConnectionConfig, x => x.Password = null),
        };
        draft.Password = StoredPassword(PostgresSecrets.Resolve(named));
        draft.PasswordKept = draft.Password.Length > 0;
        return draft;
    }

    /// <summary>The wizard on <paramref name="draft"/>: null for ESC on its first page (the File page, or an edit's Name), else whether a setting changed.</summary>
    private async Task<bool?> RunPostgresWizardAsync(PostgresDraft draft, CancellationToken cancellationToken)
    {
        // An edit opens on its summary (2026-10-05): Enter on a row changes that one; a stored password that could not be
        // read asks first.
        var step = draft.Original is null ? PostgresWizardStep.File : PostgresWizardMissing(draft) ?? PostgresWizardStep.Summary;
        bool fromSummary = draft.Original is not null;
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

                if (draft.Original is not null)
                {
                    return null;   // an edit's ESC on its summary: back to the list
                }

                step = PostgresWizardStep.Description;
                continue;
            }

            string reach = PostgresWizardReachKey(draft);
            if (await PostgresWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false))
            {
                ForgetKeptPasswordOnMove(draft.OriginalHost, draft.Config.SignInHost, draft.PasswordKept, () => { draft.Password = ""; draft.PasswordKept = false; });
                var next = fromSummary ? PostgresWizardMissing(draft) ?? PostgresWizardStep.Summary : step + 1;

                // #163: a summary edit of where it connects or as whom, with a lock held, checks the lock against the new database and role.
                if (fromSummary && next == PostgresWizardStep.Summary && step != PostgresWizardStep.Schema && draft.Config.Locks.Count > 0 && PostgresWizardReachKey(draft) != reach)
                {
                    next = PostgresWizardStep.Schema;
                }

                step = next;
                fromSummary = fromSummary && step != PostgresWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = PostgresWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            if (step == (draft.Original is null ? PostgresWizardStep.File : PostgresWizardStep.Name))
            {
                return null;
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
                    if (PostgresConfigFile.Load(path).Connections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Name, draft.Original, StringComparison.OrdinalIgnoreCase)))
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
                return await PostgresWizardTypeAsync(step, draft, PostgresWizardDatabaseQuestion, c.Database ?? "", allowEmpty: false, mask: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return PostgresWizardDatabaseRequired;
                    }

                    c.Database = text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case PostgresWizardStep.Schema:
                return await PostgresWizardSchemasAsync(step, draft, cancellationToken).ConfigureAwait(false);

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
                    draft.PasswordKept = false;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case PostgresWizardStep.Tls:
            {
                int current = WordIndex(PostgresConnectionConfig.SslModeWords, c.SslMode);
                if (await PostgresWizardPickAsync(PostgresWizardTlsQuestion, PostgresWizardTlsRows, current, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.SslMode = PostgresConnectionConfig.SslModeWords[picked];   // the key always written (2026-10-24, DB1: an absent one is prefer)
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

    /// <summary>
    /// The Schemas step (2026-11-14, feature idea #18; one step with Oracle's since 2026-11-15, #11): the schemas the draft's role may use,
    /// listed with the draft's lock cleared (a saved schema since dropped would otherwise fail the session and hide the fix); saved names
    /// past the listing's cap looked up by name (#146). True once answered, false for ESC.
    /// </summary>
    private Task<bool> PostgresWizardSchemasAsync(PostgresWizardStep step, PostgresDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        var saved = c.Locks;
        var copy = PostgresWizardCopy(draft, keepLock: false);
        var connection = new PostgresNamedConnection(draft.Name, copy, PostgresWizardPath(draft.Global));
        async Task<SchemaListing> ListAsync(CancellationToken token)
        {
            var run = await (ListPostgresSchemas ?? ListPostgresSchemasAsync)(connection, saved, token).ConfigureAwait(false);
            if (run.Outcome != Sql.SqlOutcome.Ok || run.Grids.Count == 0)
            {
                return new SchemaListing([], false, [], PostgresText.Error(run));
            }

            // A name the session's options cannot carry is left off: the connection could not be held to it. One that read back empty too.
            var names = run.Grids[0].Rows.Select(r => r[0]).Where(n => n.Length > 0 && PostgresConnectionConfig.PickProblem([n]) is null).ToList();
            var found = run.Grids.Count > 1 ? run.Grids[1].Rows.Select(r => r[0]).Where(n => n.Length > 0).ToList() : [];
            return new SchemaListing(names, run.Grids[0].More, found, null);
        }

        var family = new SchemaFamily(PostgresWizardTitle, PostgresWizardSchemaQuestion, PostgresWizardSchemaOrderNote, PostgresWizardSchemaTypedQuestion, PostgresWizardFirstQuestion,
            c.Host?.Trim() + "/" + c.Database?.Trim(), PostgresWizardListingKey(draft), draft.Schemas, saved, copy.Problem, ListAsync,
            PostgresConnectionConfig.PickProblem, PostgresText.TooManySchemas, chosen => chosen, c.HoldTo,
            (question, start, accept, token) => PostgresWizardTypeAsync(step, draft, question, start, allowEmpty: true, mask: false, accept, token));
        return WizardSchemaStepAsync(family, cancellationToken);
    }

    /// <summary>What the draft connects with, as the Schemas listing's cache keys it (#99): <see cref="PostgresWizardReachKey"/>, the TLS mode and the timeout.</summary>
    private static string PostgresWizardListingKey(PostgresDraft draft) =>
        string.Join("\u0001", PostgresWizardReachKey(draft), draft.Config.SslMode, draft.Config.ConnectTimeoutSeconds?.ToString(CultureInfo.InvariantCulture));

    /// <summary>The fields whose edit from the summary goes on to Schemas while a lock is held (#163): the host, port, database, user and password.</summary>
    private static string PostgresWizardReachKey(PostgresDraft draft) =>
        string.Join("\u0001", draft.Config.Host?.Trim(), draft.Config.Port?.ToString(CultureInfo.InvariantCulture), draft.Config.Database?.Trim(), draft.Config.User?.Trim(), draft.Password);

    /// <summary>
    /// The draft as a connection to try, nothing written (the Test's and the Schemas listing's): its typed or kept password under the file
    /// store; <paramref name="keepLock"/> false clears the schema lock, so the listing sees every schema the role may use.
    /// </summary>
    private static PostgresConnectionConfig PostgresWizardCopy(PostgresDraft draft, bool keepLock)
    {
        var c = draft.Config;
        return new PostgresConnectionConfig
        {
            Host = c.Host,
            Port = c.Port,
            Database = c.Database,
            Schema = keepLock ? c.Schema : null,
            Schemas = keepLock ? c.Schemas : null,
            User = c.User,
            Password = draft.Password,
            PasswordStore = PostgresConnectionConfig.FileStore,
            SslMode = c.SslMode,
            ConnectTimeoutSeconds = c.ConnectTimeoutSeconds,
            Access = c.Access,
        };
    }

    /// <summary>
    /// What the Schemas step lists over the unlocked draft (#18), given the saved schemas: a real run of
    /// <see cref="PostgresCatalogQueries.UsableSchemas"/>, then, with any saved, <see cref="PostgresCatalogQueries.SavedSchemas"/> (#146); the
    /// tests set their own.
    /// </summary>
    public Func<PostgresNamedConnection, IReadOnlyList<string>, CancellationToken, Task<Sql.SqlRun>>? ListPostgresSchemas { get; set; }

    private Task<Sql.SqlRun> ListPostgresSchemasAsync(PostgresNamedConnection connection, IReadOnlyList<string> saved, CancellationToken cancellationToken) =>
        new PostgresAccess(() => new PostgresCatalog([connection], []))
            .RunAsync(connection.Name, saved.Count == 0 ? [PostgresCatalogQueries.UsableSchemas] : [PostgresCatalogQueries.UsableSchemas, PostgresCatalogQueries.SavedSchemas],
                [new Sql.SqlParameterValue("saved", saved.ToArray())], PostgresAccess.MaxCatalogRows, _settings.Current.PostgresQueryTimeoutSeconds, cancellationToken, readCell: Sql.TableProfile.CatalogCell);

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
        int cursor = ConnectionWizardStartCursor(draft.Original, _settings.Current.PostgresConnectionsOffered);
        while (true)
        {
            // Always offer-or-hide (2026-10-01): nothing is offered until ticked, so the wizard is where a new one is.
            var actions = new List<string> { SqlWizardSaveOfferedRow, PostgresWizardSaveHiddenRow };
            int test = actions.Count;
            actions.Add(SqlWizardTestRow);
            actions.Add(SqlWizardCancelRow);
            var ruled = ConnectionWizardSummaryRows(actions, PostgresWizardRows(draft));
            var rows = ruled.Rows;
            var page = new MenuPage(PostgresWizardTitle, rows, PickKeys) { Headings = ruled.Headings, Caption = SqlWizardSummaryCaption };
            if (!_pane.Enabled)
            {
                Flow.Notice(SqlWizardSummaryCaption);
            }

            if (await PickAsync(page, ruled.RowOf(cursor), cancellationToken).ConfigureAwait(false) is not { } pickedRow)
            {
                return (false, false, null);
            }

            int picked = ruled.ItemOf(pickedRow);
            if (picked < 0)
            {
                continue;
            }

            cursor = picked;
            if (picked >= actions.Count)
            {
                var edit = (PostgresWizardStep)(picked - actions.Count);
                if (edit == PostgresWizardStep.File && draft.Original is not null)
                {
                    continue;   // an edit's file is fixed
                }

                return (false, false, edit);
            }

            if (picked == test)
            {
                await PostgresWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == test + 1)
            {
                Sink.Notice(draft.Original is null ? SqlWizardCancelledNotice : ConnectionWizardUnchangedNotice);
                return (true, false, null);
            }

            if (PostgresWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>
    /// The draft tried as it stands, nothing written: who it signed in as, the database and the server on the status line, then a warning
    /// when the role could change data; then what else it could reach (2026-10-09, <see cref="PostgresCatalogQueries.Reach"/>, the third
    /// to fifth results): a warning naming them, or that the server holds it to its place too. A failure still lets it be saved.
    /// </summary>
    private async Task PostgresWizardTestAsync(PostgresDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        var copy = PostgresWizardCopy(draft, keepLock: true);
        if (copy.Problem is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        var run = await (TestPostgresConnection ?? TestPostgresConnectionAsync)(new PostgresNamedConnection(draft.Name, copy, PostgresWizardPath(draft.Global)), cancellationToken).ConfigureAwait(false);
        if (run.Outcome != Sql.SqlOutcome.Ok)
        {
            Sink.Error(PostgresText.Error(run));

            // A certificate the computer does not trust (2026-10-24, decision DB1: a new connection checks it): what to do, never a dead end.
            if (run.Detail.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            {
                Sink.Notice(Sql.SqlText.CertificateNotTrusted);
            }

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

        if (run.Grids.Count > 4)
        {
            List<string> Column(int set) => run.Grids[set].Rows.Select(r => r[0]).ToList();
            var locks = c.Locks;
            var (databases, servers, schemas) = (Column(2), Column(3), Column(4));
            string reach = PostgresText.Reach(c.Database!.Trim(), locks, databases, servers, schemas);
            if (databases.Count > 0 || servers.Count > 0 || (locks.Count > 0 && schemas.Count > 0))
            {
                Sink.Warning(reach);
            }
            else
            {
                Sink.Notice(reach);
            }
        }
    }

    /// <summary>
    /// What the wizard's Test runs over the unsaved draft (2026-10-04): a real <see cref="PostgresAccess"/> run of
    /// <see cref="PostgresCatalogQueries.WhoAmI"/> at the tab's timeout; the tests set their own.
    /// </summary>
    public Func<PostgresNamedConnection, CancellationToken, Task<Sql.SqlRun>>? TestPostgresConnection { get; set; }

    private Task<Sql.SqlRun> TestPostgresConnectionAsync(PostgresNamedConnection connection, CancellationToken cancellationToken) =>
        new PostgresAccess(() => new PostgresCatalog([connection], []))
            .RunAsync(connection.Name, [.. PostgresCatalogQueries.WhoAmI, .. PostgresCatalogQueries.Reach], [], 100, _settings.Current.PostgresQueryTimeoutSeconds, cancellationToken);

    /// <summary>Writes the draft: the entry, then its password to its store, then — when <paramref name="offer"/> — its name there. Null when nothing was written.</summary>
    private bool? PostgresWizardSave(PostgresDraft draft, bool offer)
    {
        var c = draft.Config;
        if (PostgresWizardMissing(draft) is { } missing)
        {
            Sink.Error(WizardSaveFailed(draft.Original, draft.Name, PostgresWizardLabels[(int)missing] + " is not set"));
            return null;
        }

        if (c.Problem is { } problem)
        {
            Sink.Error(WizardSaveFailed(draft.Original, draft.Name, problem));
            return null;
        }

        string path = PostgresWizardPath(draft.Global);
        if (!SaveWizardEntry(PostgresConfigFile.Family, path, PostgresWizardPath(!draft.Global), draft.Original, draft.Name, c, draft.Password, draft.OriginalTarget))
        {
            return null;
        }

        return ApplyOfferedAfterSave(SettingsField.PostgresConnectionsOffered, _settings.Current.PostgresConnectionsOffered, draft.Original, draft.Name, offer, (d, next) => d.PostgresConnectionsOffered = next);
    }
}
