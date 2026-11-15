using System.Globalization;
using NeonSidekick.Oracle;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>Oracle add/edit connection</c> (2026-09-30, the user's ask: an Oracle tab "and a wizard to set up new connections"), the
/// <c>SQL add connection</c> wizard's shape over <c>oracle.json</c>: one page per choice — which file, the name, the data
/// source, the default schema, the user, where its password is kept, the password (masked), the connect timeout, the access
/// (2026-10-05: read, or readwrite for <c>oracle_execute</c>), the description — every row of the draft on each page, the current one marked, the question in the caption; then a summary
/// that tests the unsaved draft (who it signs in as, where, the server's version — and, since read-only is the user's first
/// ask of these tools, a warning when the account could change data: the tools never will, but a read-only account is the
/// real guard) and saves it (<see cref="OracleConfigFile.AddConnection"/>, the password after it through
/// <see cref="OracleSecrets.Save"/>). ESC steps back, and on the first page ends the visit with nothing written; Enter on a
/// summary row changes that choice and comes back. It edits an entry too since 2026-10-05 (the user's ask): a first page of the entries, a pick prefilled (the ConnectionEdit part).
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The wizard's rows, one per <see cref="OracleWizardStep"/> before the summary, in its order. Pinned.</summary>
    public static readonly IReadOnlyList<string> OracleWizardLabels =
        ["File", "Name", "Data source", "User", "Password store", "Password", "Connect timeout (s)", "Schemas", "Access", "Description"];

    public const string OracleWizardFileQuestion = "Which oracle.json: this profile's or the global one?";
    public const string OracleWizardDataSourceQuestion = "The database: host:port/service (EZConnect, e.g. localhost:1521/FREEPDB1) or a whole (DESCRIPTION=…).";
    public const string OracleWizardSchemaQuestion = "The schemas the connection is held to (the tools never leave them); none ticked is the user's own.";

    /// <summary>The checklist's caption on the pane (#31/#33): the order is the rows'.</summary>
    public const string OracleWizardSchemaOrderNote = "The first ticked is current (unqualified names resolve there).";

    /// <summary>The typed fallback's question, when the schemas could not be listed (#18).</summary>
    public const string OracleWizardSchemaTypedQuestion = "The schemas the connection is held to, comma-separated, the current one first; empty for the user's own.";

    /// <summary>The first order page's question, with two or more ticked and no pane (#18; on the pane the rows are moved instead, #31).</summary>
    public const string OracleWizardCurrentQuestion = "Which is current (unqualified names resolve there)?";

    /// <summary>The Schemas row of a draft with none: the user's own.</summary>
    public const string OracleWizardOwnSchema = "(the user's own)";
    public const string OracleWizardUserQuestion = "The database user (a read-only account is the real guard; SYS is refused).";
    public const string OracleWizardDataSourceRequired = "A data source is required.";

    /// <summary>The store picks, <c>file</c> then <c>credman</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> OracleWizardStoreRows =
        Sql.SqlText.StoreRows("oracle.json");

    public static readonly string OracleWizardTimeoutQuestion =
        "Seconds a connect may take, 1 to " + Invariant(OracleConnectionConfig.MaxConnectTimeoutSeconds) + "; empty for " + Invariant(OracleConnectionConfig.DefaultConnectTimeoutSeconds) + ".";

    /// <summary>The step a wizard page asks; the summary last. The rows of <see cref="OracleWizardLabels"/> by index.</summary>
    internal enum OracleWizardStep
    {
        File,
        Name,
        DataSource,
        User,
        Store,
        Password,
        Timeout,
        Schema,
        Access,
        Description,
        Summary,
    }

    /// <summary>What the wizard has so far: the file, the name, the entry as it will be written and the password it will store.</summary>
    private sealed class OracleDraft
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

        public OracleConnectionConfig Config { get; init; } = new() { PasswordStore = OracleConnectionConfig.FileStore };

        /// <summary>The Schemas step's last listing (#99).</summary>
        public SchemaListCache Schemas { get; } = new();
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
            OracleWizardStep.Schema => c.NamesLock ? OracleAccess.PlaceText(c) : OracleWizardOwnSchema,
            OracleWizardStep.User => OrUnset(c.User),
            OracleWizardStep.Store => c.InCredentialManager ? OracleConnectionConfig.CredmanStore + " (" + c.CredentialTarget(draft.Name.Length > 0 ? draft.Name : "<name>") + ")" : OracleConnectionConfig.FileStore,
            OracleWizardStep.Password => draft.Password.Length > 0 ? (draft.PasswordKept ? SqlWizardMaskedKept : SqlWizardMasked) : SqlWizardUnset,
            OracleWizardStep.Timeout => Invariant(c.ConnectTimeoutSeconds ?? OracleConnectionConfig.DefaultConnectTimeoutSeconds),
            OracleWizardStep.Access => DatabaseWizardAccessValue(c.IsReadWrite),
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
        // The first page of the entries when there are any (2026-10-05, the user's ask), as the SQL wizard's.
        while (true)
        {
            var entries = ConnectionWizardEntries(OracleWizardPath(false), OracleWizardPath(true), path => OracleConfigFile.Load(path).Connections, c => c.Name);
            var draft = new OracleDraft();
            if (entries.Count > 0)
            {
                if (await ConnectionWizardPickAsync(OracleWizardTitle, "connection", entries.Select(e => (e.Named.Name, e.Global)).ToList(), OracleWizardPath, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    Sink.Notice(SqlWizardCancelledNotice);
                    return false;
                }

                if (picked >= 0)
                {
                    draft = OracleWizardDraftFrom(entries[picked].Named, entries[picked].Global);
                }
            }

            if (await RunOracleWizardAsync(draft, cancellationToken).ConfigureAwait(false) is { } changed)
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
    private OracleDraft OracleWizardDraftFrom(OracleNamedConnection named, bool global)
    {
        var c = named.Config;
        var draft = new OracleDraft
        {
            Global = global,
            Name = named.Name,
            Original = named.Name,
            OriginalHost = c.SignInHost,
            OriginalTarget = c.InCredentialManager && string.IsNullOrWhiteSpace(c.Credential) ? c.CredentialTarget(named.Name) : null,   // a blank credential is the default too (2026-10-24, review N13)
            Config = CloneEntry(c, OracleJsonContext.Default.OracleConnectionConfig, x => x.Password = null),
        };
        draft.Password = StoredPassword(OracleSecrets.Resolve(named));
        draft.PasswordKept = draft.Password.Length > 0;
        return draft;
    }

    /// <summary>The wizard on <paramref name="draft"/>: null for ESC on its first page (the File page, or an edit's Name), else whether a setting changed.</summary>
    private async Task<bool?> RunOracleWizardAsync(OracleDraft draft, CancellationToken cancellationToken)
    {
        // An edit opens on its summary (2026-10-05): Enter on a row changes that one; a stored password that could not be
        // read asks first.
        var step = draft.Original is null ? OracleWizardStep.File : OracleWizardMissing(draft) ?? OracleWizardStep.Summary;
        bool fromSummary = draft.Original is not null;
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

                if (draft.Original is not null)
                {
                    return null;   // an edit's ESC on its summary: back to the list
                }

                step = OracleWizardStep.Description;
                continue;
            }

            string reach = OracleWizardReachKey(draft);
            if (await OracleWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false))
            {
                ForgetKeptPasswordOnMove(draft.OriginalHost, draft.Config.SignInHost, draft.PasswordKept, () => { draft.Password = ""; draft.PasswordKept = false; });
                var next = fromSummary ? OracleWizardMissing(draft) ?? OracleWizardStep.Summary : step + 1;

                // #163: a summary edit of where it connects or as whom, with a lock held, checks the lock against the new account.
                if (fromSummary && next == OracleWizardStep.Summary && step != OracleWizardStep.Schema && draft.Config.NamesLock && OracleWizardReachKey(draft) != reach)
                {
                    next = OracleWizardStep.Schema;
                }

                step = next;
                fromSummary = fromSummary && step != OracleWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = OracleWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            if (step == (draft.Original is null ? OracleWizardStep.File : OracleWizardStep.Name))
            {
                return null;
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
                    if (OracleConfigFile.Load(path).Connections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Name, draft.Original, StringComparison.OrdinalIgnoreCase)))
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
                return await OracleWizardSchemasAsync(step, draft, cancellationToken).ConfigureAwait(false);

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
                    draft.PasswordKept = false;
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

            case OracleWizardStep.Access:
            {
                if (await OracleWizardPickAsync(DatabaseWizardAccessQuestion, DatabaseWizardAccessRows(Oracle.OracleStatementKinds.Family), c.IsReadWrite ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Access = Sql.ConnectionAccess.Stored(picked == 1);
                NoticeAccessModeOff(Oracle.OracleStatementKinds.Family, c.IsReadWrite, _settings.Current.OracleMode);
                return true;
            }

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
        int cursor = ConnectionWizardStartCursor(draft.Original, _settings.Current.OracleConnectionsOffered);
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
                var edit = (OracleWizardStep)(picked - actions.Count);
                if (edit == OracleWizardStep.File && draft.Original is not null)
                {
                    continue;   // an edit's file is fixed
                }

                return (false, false, edit);
            }

            if (picked == test)
            {
                await OracleWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == test + 1)
            {
                Sink.Notice(draft.Original is null ? SqlWizardCancelledNotice : ConnectionWizardUnchangedNotice);
                return (true, false, null);
            }

            if (OracleWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>The summary's hidden-save row. Pinned.</summary>
    public const string OracleWizardSaveHiddenRow = "Save, hidden from the model until you tick it in Oracle connections offered";

    /// <summary>
    /// The draft tried as it stands, nothing written: a one-entry catalog of it, its password handed over as a plain
    /// <c>file</c> value (which <see cref="OracleSecrets.Resolve"/> passes through) so a <c>credman</c> draft is tested before
    /// its entry exists. Who it signed in as and the server's version on the status line, then a warning when the account
    /// could write; a failure still lets it be saved.
    /// </summary>
    private async Task OracleWizardTestAsync(OracleDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        var copy = OracleWizardCopy(draft, keepLock: true);
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
            Sink.Warning(OracleText.CanWrite(powers, c.IsReadWrite));
        }

        // What else it could read (2026-10-09, OracleCatalogQueries.Reach: the third and fourth results).
        if (run.Grids.Count > 3)
        {
            var (owners, any) = (run.Grids[2].Rows.Select(r => r[0]).ToList(), run.Grids[3].Rows.Select(r => r[0]).ToList());
            string reach = OracleText.Reach(OracleAccess.Places(copy), owners, any);
            if (owners.Count > 0 || any.Count > 0)
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
    /// The Schemas step (2026-11-14, feature idea #18; one step with PostgreSQL's since 2026-11-15, #11): the user's own schema first, then
    /// the others <c>ALL_USERS</c> lists that Oracle doesn't maintain (#118: an empty schema was "(missing)" while the list read only schemas
    /// holding a table), listed with the draft's lock cleared (a listed schema since dropped would otherwise fail the session and hide the fix).
    /// Saved names past the listing's cap are looked up by name (#146). True once answered, false for ESC.
    /// </summary>
    private Task<bool> OracleWizardSchemasAsync(OracleWizardStep step, OracleDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        var copy = OracleWizardCopy(draft, keepLock: false);
        string own = OracleAccess.Place(copy);
        IReadOnlyList<string> saved = c.NamesLock ? OracleAccess.Places(c) : [];
        var connection = new OracleNamedConnection(draft.Name, copy, OracleWizardPath(draft.Global));
        async Task<SchemaListing> ListAsync(CancellationToken token)
        {
            var run = await (ListOracleSchemas ?? ListOracleSchemasAsync)(connection, saved, token).ConfigureAwait(false);
            if (run.Outcome != Sql.SqlOutcome.Ok || run.Grids.Count == 0)
            {
                return new SchemaListing([], false, [], OracleText.Error(run));
            }

            // As the entry writes them: a dictionary name that a bare word would fold differently ("hr") in quotes. A name that read back
            // empty (a failed read) is left out; one the lock refuses is left out unless it is the user's own (#53: shown, refused).
            var others = run.Grids[0].Rows.Select(r => r[0]).Where(n => n.Length > 0 && n != own).Select(OracleWizardSpelling).Where(n => OracleConnectionConfig.PickProblem([n]) is null);
            var found = run.Grids.Count > 1 ? run.Grids[1].Rows.Select(r => r[0]).Where(n => n.Length > 0).Select(OracleWizardSpelling).ToList() : [];
            return new SchemaListing([OracleWizardSpelling(own), .. others], run.Grids[0].More, found, null);
        }

        var family = new SchemaFamily(OracleWizardTitle, OracleWizardSchemaQuestion, OracleWizardSchemaOrderNote, OracleWizardSchemaTypedQuestion, OracleWizardCurrentQuestion,
            c.DataSource?.Trim() ?? "", OracleWizardListingKey(draft), draft.Schemas, [.. saved.Select(OracleWizardSpelling)], copy.Problem, ListAsync,
            OracleConnectionConfig.PickProblem, OracleText.TooManySchemas,
            // The user's own alone is the default: no key written, as before the list (a file stays as it was).
            chosen => chosen is [var only] && only == OracleWizardSpelling(own) && !c.NamesLock ? [] : chosen,
            c.HoldTo,
            (question, start, accept, token) => OracleWizardTypeAsync(step, draft, question, start, allowEmpty: true, mask: false, accept, token));
        return WizardSchemaStepAsync(family, cancellationToken);
    }

    /// <summary>What the draft connects with, as the Schemas listing's cache keys it (#99): the data source, user, password and timeout.</summary>
    private static string OracleWizardListingKey(OracleDraft draft) =>
        OracleWizardReachKey(draft) + "\u0001" + draft.Config.ConnectTimeoutSeconds?.ToString(CultureInfo.InvariantCulture);

    /// <summary>The fields whose edit from the summary goes on to Schemas while a lock is held (#163): where it connects and as whom.</summary>
    private static string OracleWizardReachKey(OracleDraft draft) =>
        string.Join("\u0001", draft.Config.DataSource?.Trim(), draft.Config.User?.Trim(), draft.Password);

    /// <summary>A dictionary name as an entry writes it: bare when a bare word reads back the same, else in double quotes.</summary>
    public static string OracleWizardSpelling(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return OracleIdentifier.Normalize(name) == name ? name : "\"" + name + "\"";
    }

    /// <summary>
    /// The draft as a connection to try, nothing written (the Test's and the Schemas listing's): its typed or kept password as a plain
    /// <c>file</c> value; <paramref name="keepLock"/> false clears the schemas, so the session works in the user's own.
    /// </summary>
    private static OracleConnectionConfig OracleWizardCopy(OracleDraft draft, bool keepLock)
    {
        var c = draft.Config;
        return new OracleConnectionConfig
        {
            DataSource = c.DataSource,
            Schema = keepLock ? c.Schema : null,
            Schemas = keepLock ? c.Schemas : null,
            User = c.User,
            Password = draft.Password,
            PasswordStore = OracleConnectionConfig.FileStore,
            ConnectTimeoutSeconds = c.ConnectTimeoutSeconds,
            Access = c.Access,
        };
    }

    /// <summary>
    /// What the Schemas step lists over the unlocked draft (#18), given the saved schemas as the dictionary spells them: a real run of
    /// <see cref="OracleCatalogQueries.UserSchemas"/>, then, with any saved, <see cref="OracleCatalogQueries.SchemaCheck"/> over them (#146);
    /// the tests set their own.
    /// </summary>
    public Func<OracleNamedConnection, IReadOnlyList<string>, CancellationToken, Task<Sql.SqlRun>>? ListOracleSchemas { get; set; }

    private Task<Sql.SqlRun> ListOracleSchemasAsync(OracleNamedConnection connection, IReadOnlyList<string> saved, CancellationToken cancellationToken) =>
        new OracleAccess(() => new OracleCatalog([connection], []))
            .RunAsync(connection.Name, saved.Count == 0 ? [OracleCatalogQueries.UserSchemas] : [OracleCatalogQueries.UserSchemas, OracleCatalogQueries.ForPlaces(OracleCatalogQueries.SchemaCheck, saved.Count)],
                OracleCatalogQueries.OwnerBinds(saved), OracleAccess.MaxCatalogRows, _settings.Current.OracleQueryTimeoutSeconds, ownerMarker: false, cancellationToken, readCell: Sql.TableProfile.CatalogCell);

    /// <summary>The app's test: <see cref="OracleAccess.RunAsync"/> over the one connection, <see cref="OracleCatalogQueries.WhoAmI"/>, <see cref="OracleCatalogQueries.WritePowers"/> and <see cref="OracleCatalogQueries.Reach"/>, the Oracle tab's timeout.</summary>
    private Task<Sql.SqlRun> TestOracleConnectionAsync(OracleNamedConnection connection, CancellationToken cancellationToken) =>
        new OracleAccess(() => new OracleCatalog([connection], []))
            .RunAsync(connection.Name, [OracleCatalogQueries.WhoAmI, OracleCatalogQueries.WritePowers, .. OracleCatalogQueries.Reach], [], 100, _settings.Current.OracleQueryTimeoutSeconds, ownerMarker: true, cancellationToken);

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
            Sink.Error(WizardSaveFailed(draft.Original, draft.Name, OracleWizardLabels[(int)missing] + " is not set"));
            return null;
        }

        if (c.Problem is { } problem)
        {
            Sink.Error(WizardSaveFailed(draft.Original, draft.Name, problem));
            return null;
        }

        string path = OracleWizardPath(draft.Global);
        if (!SaveWizardEntry(OracleConfigFile.Family, path, OracleWizardPath(!draft.Global), draft.Original, draft.Name, c, draft.Password, draft.OriginalTarget))
        {
            return null;
        }

        return ApplyOfferedAfterSave(SettingsField.OracleConnectionsOffered, _settings.Current.OracleConnectionsOffered, draft.Original, draft.Name, offer, (d, next) => d.OracleConnectionsOffered = next);
    }
}
