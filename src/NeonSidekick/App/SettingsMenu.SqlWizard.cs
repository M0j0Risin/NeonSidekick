using System.Globalization;
using NeonSidekick.Sql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>SQL add/edit connection</c> (2026-09-23, the user's ask: "a kind of wizard to step the user through all the choices
/// before updating whichever sql.json"): one page per choice — which file, the name, the server, the database, the
/// sign-in, the account, where its password is kept, the password (masked), the TLS pair, the connect timeout, the access
/// (2026-10-05: read, or readwrite for <c>sql_execute</c>), the description — every row of the draft on each page, the current one marked, the question in the caption; then a
/// summary that tests the unsaved draft (<c>SELECT @@VERSION</c>, nothing written) and saves it
/// (<see cref="SqlConfigFile.AddConnection"/>, the password after it through <see cref="SqlSecrets.Save"/>). ESC steps
/// back, and on the first page ends the visit with nothing written; Enter on a summary row changes that choice and comes
/// back. It added only until 2026-10-05, when it took to editing an entry too (the user's ask; <c>SQL add connection</c> until
/// then): a first page of the entries, a pick prefilled (<see cref="SettingsMenu"/>'s ConnectionEdit part).
/// </summary>
internal sealed partial class SettingsMenu
{
    // ── Pinned statics ──────────────────────────────────────────────────────

    /// <summary>The value column of the <c>SQL add/edit connection</c> action row. Pinned.</summary>
    public const string SqlAddConnectionLabel = "Enter to start connection wizard";

    /// <summary>The query the summary's test runs: one row, any login may read it.</summary>
    public const string SqlTestQuery = "SELECT @@VERSION";

    /// <summary>The wizard's rows, one per <see cref="SqlWizardStep"/> before the summary, in its order. Pinned.</summary>
    public static readonly IReadOnlyList<string> SqlWizardLabels =
        ["File", "Name", "Server", "Database", "Sign-in", "User", "Password store", "Password", "Encryption", "Trust server certificate", "Connect timeout (s)", "Access", "Description"];

    public const string SqlWizardFileQuestion = "Scope for sql.json?";
    public const string SqlWizardNameQuestion = "Its name: what the model passes as \"connection\" and %name picks on the input line.";
    public const string SqlWizardServerQuestion = "The server: host, host,port or host\\instance.";
    public const string SqlWizardDatabaseQuestion = "The database a call opens when it names none; empty for the login's default database.";
    public const string SqlWizardAuthQuestion = "How it signs in.";
    public const string SqlWizardSqlUserQuestion = "The SQL login's name.";
    public const string SqlWizardRunAsUserQuestion = "The Windows account to sign in as: DOMAIN\\name or name@domain.";
    public const string SqlWizardStoreQuestion = "Where the password is kept.";
    public const string SqlWizardPasswordQuestion = "The password (masked; never shown, logged or sent to the model).";
    public const string SqlWizardEncryptQuestion = "Encryption of the connection.";
    public const string SqlWizardTrustQuestion = "Accept a certificate no authority vouches for? Only for a self-signed one (a dev container's).";
    public static readonly string SqlWizardTimeoutQuestion =
        "Seconds a connect may take, 1 to " + Invariant(SqlConnectionConfig.MaxConnectTimeoutSeconds) + "; empty for " + Invariant(SqlConnectionConfig.DefaultConnectTimeoutSeconds) + ".";
    public const string SqlWizardDescriptionQuestion = "What the database holds, in your words (optional): the model reads it to pick a connection.";
    public const string SqlWizardSummaryCaption = "Check the connection: Test tries it without saving; Enter on a row below changes it.";

    /// <summary>The sign-in picks, <see cref="SqlConnectionConfig.SqlAuth"/> / <c>windows</c> / <c>runas</c> in that order. Pinned.</summary>
    public static readonly IReadOnlyList<string> SqlWizardAuthRows =
        ["sql      a SQL login (user and password)", "windows  Windows sign-in as you (no password)", "runas    Windows sign-in as another account (runas /netonly)"];

    /// <summary>The store picks, <c>file</c> then <c>credman</c>. Pinned.</summary>
    public static readonly IReadOnlyList<string> SqlWizardStoreRows =
        ["file     encrypted (DPAPI) in sql.json", "credman  Windows Credential Manager"];

    /// <summary>The encryption picks, <see cref="SqlConnectionConfig.EncryptWords"/>' order put the default first. Pinned.</summary>
    public static readonly IReadOnlyList<string> SqlWizardEncryptWords = ["mandatory", "strict", "optional"];

    public static readonly IReadOnlyList<string> SqlWizardEncryptRows =
        ["mandatory  encrypted, the certificate checked unless trusted below (the default)", "strict     TDS 8: TLS first, the certificate always checked", "optional   encrypted only if the server asks"];

    public const string SqlWizardSaveOfferedRow = "Save, and offer it to the model";
    public const string SqlWizardSaveHiddenRow = "Save, hidden from the model until ticked in SQL connections offered";
    public const string SqlWizardTestRow = "Test the connection";
    public const string SqlWizardCancelRow = "Cancel";

    public const string SqlWizardNameRequired = "A name is required.";
    public const string SqlWizardNameSpaces = "A name has no spaces: the model and %name use it as one word.";
    public const string SqlWizardServerRequired = "A server is required.";
    public const string SqlWizardUserRequired = "A user is required.";
    public const string SqlWizardPasswordRequired = "A password is required.";
    public const string SqlWizardCancelledNotice = "No connection added.";
    public const string SqlWizardNotNeeded = "(not needed)";
    public const string SqlWizardUnset = "—";
    public const string SqlWizardMasked = "••••••";

    public static string SqlWizardTimeoutError(string typed) =>
        $"'{typed}' is not 1 to {Invariant(SqlConnectionConfig.MaxConnectTimeoutSeconds)} seconds.";

    public static string SqlWizardNameTaken(string name, string path) => $"'{name}' is already in {path}; pick another name.";

    public static string SqlWizardShadowsWarning(string name, string path) => $"'{name}' is also in {path}; this profile's entry wins it.";

    public static string SqlWizardShadowedWarning(string name, string path) => $"'{name}' is also in {path}, which wins it for this profile.";

    public static string SqlWizardFileRow(bool global, string path) => (global ? "global  " : "profile ") + path;

    public static string SqlWizardTestOkNotice(string name, string version) => $"Connected to '{name}': {version}";

    /// <summary>The step a wizard page asks; the summary last. The rows of <see cref="SqlWizardLabels"/> by index.</summary>
    internal enum SqlWizardStep
    {
        File,
        Name,
        Server,
        Database,
        Auth,
        User,
        Store,
        Password,
        Encrypt,
        Trust,
        Timeout,
        Access,
        Description,
        Summary,
    }

    /// <summary>What the wizard has so far: the file, the name, the entry as it will be written and the password it will store.</summary>
    private sealed class SqlDraft
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

        public SqlConnectionConfig Config { get; init; } = new()
        {
            Auth = SqlConnectionConfig.SqlAuth,
            PasswordStore = SqlConnectionConfig.FileStore,
            Encrypt = SqlWizardEncryptWords[0],
        };
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private string SqlWizardPath(bool global) =>
        global ? SqlConfigFile.GlobalPath(_settings.StorageDirectory) : SqlConfigFile.ProfilePath(_settings.ProfileDirectory);

    /// <summary>Whether <paramref name="step"/> is asked for the draft: the account, store and password only when it signs in with a password; the file never for an edit.</summary>
    private static bool SqlWizardAsks(SqlWizardStep step, SqlDraft draft) =>
        step == SqlWizardStep.File ? draft.Original is null
            : step is not (SqlWizardStep.User or SqlWizardStep.Store or SqlWizardStep.Password) || draft.Config.NeedsPassword;

    /// <summary>The first step the draft still lacks (a name, a server, and the account and password when it needs them), or null.</summary>
    private static SqlWizardStep? SqlWizardMissing(SqlDraft draft)
    {
        if (draft.Name.Length == 0)
        {
            return SqlWizardStep.Name;
        }

        if (string.IsNullOrWhiteSpace(draft.Config.Server))
        {
            return SqlWizardStep.Server;
        }

        if (draft.Config.NeedsPassword && string.IsNullOrWhiteSpace(draft.Config.User))
        {
            return SqlWizardStep.User;
        }

        return draft.Config.NeedsPassword && draft.Password.Length == 0 ? SqlWizardStep.Password : null;
    }

    /// <summary>The value column of the draft's row for <paramref name="step"/>.</summary>
    private string SqlWizardValue(SqlWizardStep step, SqlDraft draft)
    {
        var c = draft.Config;
        if (step != SqlWizardStep.File && !SqlWizardAsks(step, draft))
        {
            return SqlWizardNotNeeded;
        }

        static string OrUnset(string? text) => string.IsNullOrWhiteSpace(text) ? SqlWizardUnset : text.Trim();
        return step switch
        {
            SqlWizardStep.File => SqlWizardFileRow(draft.Global, SqlWizardPath(draft.Global)),
            SqlWizardStep.Name => OrUnset(draft.Name),
            SqlWizardStep.Server => OrUnset(c.Server),
            SqlWizardStep.Database => string.IsNullOrWhiteSpace(c.Database) ? "(the login's default)" : c.Database.Trim(),
            SqlWizardStep.Auth => c.Auth ?? SqlConnectionConfig.SqlAuth,
            SqlWizardStep.User => OrUnset(c.User),
            SqlWizardStep.Store => c.InCredentialManager ? SqlConnectionConfig.CredmanStore + " (" + c.CredentialTarget(draft.Name.Length > 0 ? draft.Name : "<name>") + ")" : SqlConnectionConfig.FileStore,
            SqlWizardStep.Password => draft.Password.Length > 0 ? (draft.PasswordKept ? SqlWizardMaskedKept : SqlWizardMasked) : SqlWizardUnset,
            SqlWizardStep.Encrypt => c.Encrypt ?? SqlWizardEncryptWords[0],
            SqlWizardStep.Trust => c.TrustServerCertificate ? "yes" : "no",
            SqlWizardStep.Timeout => Invariant(c.ConnectTimeoutSeconds ?? SqlConnectionConfig.DefaultConnectTimeoutSeconds),
            SqlWizardStep.Access => DatabaseWizardAccessValue(c.IsReadWrite),
            _ => OrUnset(c.Description),
        };
    }

    /// <summary>Every row of the draft, the label padded, escaped.</summary>
    private List<string> SqlWizardRows(SqlDraft draft)
    {
        int width = SqlWizardLabels.Max(l => l.Length) + 2;
        return SqlWizardLabels.Select((label, i) => Markup.Escape(label.PadRight(width) + SqlWizardValue((SqlWizardStep)i, draft))).ToList();
    }

    private string SqlWizardTitle => Crumb(FieldName(SettingsField.SqlAddConnection));

    /// <summary>
    /// The visit: the first page of the entries when there are any (new, or one to edit), then the wizard on its draft; ESC on
    /// the wizard's first page back to that list, ESC there (or on the File page with no entries) the end with nothing written.
    /// True when a setting changed: the offered list.
    /// </summary>
    private async Task<bool> AddSqlConnectionAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var entries = ConnectionWizardEntries(SqlWizardPath(false), SqlWizardPath(true), path => SqlConfigFile.Load(path).Connections);
            var draft = new SqlDraft();
            if (entries.Count > 0)
            {
                if (await ConnectionWizardPickAsync(SqlWizardTitle, "connection", entries.Select(e => (e.Named.Name, e.Global)).ToList(), SqlWizardPath, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    Sink.Notice(SqlWizardCancelledNotice);
                    return false;
                }

                if (picked >= 0)
                {
                    draft = SqlWizardDraftFrom(entries[picked].Named, entries[picked].Global);
                }
            }

            if (await RunSqlWizardAsync(draft, cancellationToken).ConfigureAwait(false) is { } changed)
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
    private SqlDraft SqlWizardDraftFrom(SqlNamedConnection named, bool global)
    {
        var c = named.Config;
        var draft = new SqlDraft
        {
            Global = global,
            Name = named.Name,
            Original = named.Name,
            OriginalTarget = c.NeedsPassword && c.InCredentialManager && c.Credential is null ? c.CredentialTarget(named.Name) : null,
            Config = CloneEntry(c, SqlJsonContext.Default.SqlConnectionConfig, x => x.Password = null),
        };
        if (c.NeedsPassword)
        {
            draft.Password = StoredPassword(SqlSecrets.Resolve(named));
            draft.PasswordKept = draft.Password.Length > 0;
        }

        return draft;
    }

    /// <summary>
    /// The wizard: its steps in order, ESC one back (before the first: null, nothing written), a change from the summary back
    /// to it (by the step the draft still lacks, when the change asks for one — a sign-in that now takes a password).
    /// True when a setting changed: the offered list.
    /// </summary>
    private async Task<bool?> RunSqlWizardAsync(SqlDraft draft, CancellationToken cancellationToken)
    {
        // An edit opens on its summary (2026-10-05): Enter on a row changes that one; a stored password that could not be
        // read asks first.
        var step = draft.Original is null ? SqlWizardStep.File : SqlWizardMissing(draft) ?? SqlWizardStep.Summary;
        bool fromSummary = draft.Original is not null;
        while (true)
        {
            if (step == SqlWizardStep.Summary)
            {
                var (done, changed, edit) = await SqlWizardSummaryAsync(draft, cancellationToken).ConfigureAwait(false);
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

                step = SqlWizardStep.Description;
                continue;
            }

            if (!SqlWizardAsks(step, draft))
            {
                step++;
                continue;
            }

            bool advanced = await SqlWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false);
            if (advanced)
            {
                step = fromSummary ? SqlWizardMissing(draft) ?? SqlWizardStep.Summary : step + 1;
                fromSummary = fromSummary && step != SqlWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = SqlWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            do
            {
                step--;
            }
            while (step >= SqlWizardStep.File && !SqlWizardAsks(step, draft));

            if (step < SqlWizardStep.File)
            {
                return null;
            }
        }
    }

    /// <summary>One step's page: a pick or a typed value into the draft; true when it was answered, false for ESC.</summary>
    private async Task<bool> SqlWizardStepAsync(SqlWizardStep step, SqlDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        switch (step)
        {
            case SqlWizardStep.File:
            {
                string[] rows = [SqlWizardFileRow(false, SqlWizardPath(false)), SqlWizardFileRow(true, SqlWizardPath(true))];
                if (await SqlWizardPickAsync(SqlWizardFileQuestion, rows, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case SqlWizardStep.Name:
                return await SqlWizardTypeAsync(step, draft, SqlWizardNameQuestion, draft.Name, allowEmpty: false, mask: false, text =>
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

                    string path = SqlWizardPath(draft.Global);
                    if (SqlConfigFile.Load(path).Connections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Name, draft.Original, StringComparison.OrdinalIgnoreCase)))
                    {
                        return SqlWizardNameTaken(name, path);
                    }

                    draft.Name = name;
                    string other = SqlWizardPath(!draft.Global);
                    if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                        && SqlConfigFile.Load(other).Connections.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Sink.Warning(draft.Global ? SqlWizardShadowedWarning(name, other) : SqlWizardShadowsWarning(name, other));
                    }

                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case SqlWizardStep.Server:
                return await SqlWizardTypeAsync(step, draft, SqlWizardServerQuestion, c.Server ?? "", allowEmpty: false, mask: false, text =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return SqlWizardServerRequired;
                    }

                    c.Server = text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case SqlWizardStep.Database:
                return await SqlWizardTypeAsync(step, draft, SqlWizardDatabaseQuestion, c.Database ?? "", allowEmpty: true, mask: false, text =>
                {
                    c.Database = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case SqlWizardStep.Auth:
            {
                string[] words = [SqlConnectionConfig.SqlAuth, SqlConnectionConfig.WindowsAuth, SqlConnectionConfig.RunAsAuth];
                int current = Math.Max(0, Array.IndexOf(words, c.Auth));
                if (await SqlWizardPickAsync(SqlWizardAuthQuestion, SqlWizardAuthRows, current, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                if (picked != current)
                {
                    // Another kind of account: the one typed for the last does not carry over.
                    c.User = null;
                    draft.Password = "";
                }

                c.Auth = words[picked];
                return true;
            }

            case SqlWizardStep.User:
                return await SqlWizardTypeAsync(step, draft, c.IsRunAs ? SqlWizardRunAsUserQuestion : SqlWizardSqlUserQuestion, c.User ?? "", allowEmpty: false, mask: false, text =>
                {
                    string user = text.Trim();
                    if (user.Length == 0)
                    {
                        return SqlWizardUserRequired;
                    }

                    if (c.IsRunAs && WindowsCredentials.SplitAccount(user) is null)
                    {
                        return SqlText.RunAsNeedsDomain(user);
                    }

                    c.User = user;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case SqlWizardStep.Store:
            {
                if (await SqlWizardPickAsync(SqlWizardStoreQuestion, SqlWizardStoreRows, c.InCredentialManager ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.PasswordStore = picked == 1 ? SqlConnectionConfig.CredmanStore : SqlConnectionConfig.FileStore;
                return true;
            }

            case SqlWizardStep.Password:
                return await SqlWizardTypeAsync(step, draft, SqlWizardPasswordQuestion, draft.Password, allowEmpty: false, mask: true, text =>
                {
                    if (text.Length == 0)
                    {
                        return SqlWizardPasswordRequired;
                    }

                    draft.Password = text;
                    draft.PasswordKept = false;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case SqlWizardStep.Encrypt:
            {
                int current = Math.Max(0, SqlWizardEncryptWords.ToList().IndexOf(c.Encrypt ?? ""));
                if (await SqlWizardPickAsync(SqlWizardEncryptQuestion, SqlWizardEncryptRows, current, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Encrypt = SqlWizardEncryptWords[picked];
                return true;
            }

            case SqlWizardStep.Trust:
            {
                if (await SqlWizardPickAsync(SqlWizardTrustQuestion, ConfirmRows, c.TrustServerCertificate ? 1 : 0, cancellationToken, ConfirmHotkeys).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.TrustServerCertificate = picked == 1;
                return true;
            }

            case SqlWizardStep.Timeout:
                return await SqlWizardTypeAsync(step, draft, SqlWizardTimeoutQuestion, c.ConnectTimeoutSeconds is { } s ? Invariant(s) : "", allowEmpty: true, mask: false, text =>
                {
                    string typed = text.Trim();
                    if (typed.Length == 0)
                    {
                        c.ConnectTimeoutSeconds = null;
                        return null;
                    }

                    if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 1 || seconds > SqlConnectionConfig.MaxConnectTimeoutSeconds)
                    {
                        return SqlWizardTimeoutError(typed);
                    }

                    c.ConnectTimeoutSeconds = seconds;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case SqlWizardStep.Access:
            {
                if (await SqlWizardPickAsync(DatabaseWizardAccessQuestion, DatabaseWizardAccessRows(Sql.SqlStatementKinds.Family), c.IsReadWrite ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                c.Access = Sql.ConnectionAccess.Stored(picked == 1);
                NoticeAccessModeOff(Sql.SqlStatementKinds.Family, c.IsReadWrite, _settings.Current.SqlMode);
                return true;
            }

            default:
                return await SqlWizardTypeAsync(step, draft, SqlWizardDescriptionQuestion, c.Description ?? "", allowEmpty: true, mask: false, text =>
                {
                    c.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A pick page: the question as its caption (a notice first on the prompt host, which has none), the choices as rows. The row, or null for ESC.</summary>
    private Task<int?> SqlWizardPickAsync(string question, IReadOnlyList<string> choices, int cursor, CancellationToken cancellationToken, IReadOnlyDictionary<char, int>? hotkeys = null)
    {
        var page = new MenuPage(SqlWizardTitle, choices.Select(Markup.Escape).ToList(), PickKeys) { Caption = question, Hotkeys = hotkeys };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        return PickAsync(page, cursor, cancellationToken);
    }

    /// <summary>
    /// A typed step: the draft's rows with the step's marked, the question as the caption, the slot under them pre-filled
    /// with <paramref name="initial"/>; <paramref name="accept"/> stores the text or names what is wrong with it, and a
    /// wrong one asks again with the error on the status line. True once accepted, false for ESC.
    /// </summary>
    private async Task<bool> SqlWizardTypeAsync(SqlWizardStep step, SqlDraft draft, string question, string initial, bool allowEmpty, bool mask, Func<string, string?> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(SqlWizardTitle, SqlWizardRows(draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, (int)step, _input, initial, allowEmpty, cancellationToken, mask).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(SqlWizardLabels[(int)step] + " · " + question, EditKeys));
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
    private async Task<(bool Done, bool Changed, SqlWizardStep? Edit)> SqlWizardSummaryAsync(SqlDraft draft, CancellationToken cancellationToken)
    {
        int cursor = ConnectionWizardStartCursor(draft.Original, _settings.Current.SqlConnectionsOffered);
        while (true)
        {
            // Always offer-or-hide (2026-10-01): nothing is offered until ticked, so the wizard is where a new one is.
            var actions = new List<string> { SqlWizardSaveOfferedRow, SqlWizardSaveHiddenRow };
            int test = actions.Count;
            actions.Add(SqlWizardTestRow);
            actions.Add(SqlWizardCancelRow);
            var rows = actions.Select(Markup.Escape).Concat(SqlWizardRows(draft)).ToList();
            var page = new MenuPage(SqlWizardTitle, rows, PickKeys) { Caption = SqlWizardSummaryCaption };
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
                var edit = (SqlWizardStep)(picked - actions.Count);
                if (SqlWizardAsks(edit, draft))
                {
                    return (false, false, edit);
                }

                continue;
            }

            if (picked == test)
            {
                await SqlWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == test + 1)
            {
                Sink.Notice(draft.Original is null ? SqlWizardCancelledNotice : ConnectionWizardUnchangedNotice);
                return (true, false, null);
            }

            if (SqlWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>
    /// The draft tried as it stands, nothing written: a one-entry catalog of it, its password handed over as a plain
    /// <c>file</c> value (which <see cref="SqlSecrets.Resolve"/> passes through) so a <c>credman</c> draft is tested before its
    /// entry exists. The server's answer on the status line; a failure still lets it be saved.
    /// </summary>
    private async Task SqlWizardTestAsync(SqlDraft draft, CancellationToken cancellationToken)
    {
        var c = draft.Config;
        var copy = new SqlConnectionConfig
        {
            Server = c.Server,
            Database = c.Database,
            Auth = c.Auth,
            User = c.User,
            Password = c.NeedsPassword ? draft.Password : null,
            PasswordStore = SqlConnectionConfig.FileStore,
            Encrypt = c.Encrypt,
            TrustServerCertificate = c.TrustServerCertificate,
            ConnectTimeoutSeconds = c.ConnectTimeoutSeconds,
            Access = c.Access,
        };
        if (copy.Problem is { } problem)
        {
            Sink.Error(problem);
            return;
        }

        var run = await _testSqlConnection(new SqlNamedConnection(draft.Name, copy, SqlWizardPath(draft.Global)), cancellationToken).ConfigureAwait(false);
        if (run.Outcome == SqlOutcome.Ok)
        {
            string version = run.Grids.Count > 0 && run.Grids[0].Rows.Count > 0 ? run.Grids[0].Rows[0][0] : "";
            Sink.Notice(SqlWizardTestOkNotice(draft.Name, version.Split('\n')[0].Trim()));
        }
        else
        {
            Sink.Error(SqlText.Error(run));
        }
    }

    /// <summary>The app's test: <see cref="SqlAccess.RunAsync"/> over the one connection, <see cref="SqlTestQuery"/>, one row, the SQL tab's query timeout.</summary>
    private Task<SqlRun> TestSqlConnectionAsync(SqlNamedConnection connection, CancellationToken cancellationToken) =>
        new SqlAccess(() => new SqlCatalog([connection], []))
            .RunAsync(connection.Name, null, null, SqlTestQuery, [], 1, _settings.Current.SqlQueryTimeoutSeconds, cancellationToken);

    /// <summary>
    /// Writes the draft: the entry (<see cref="SqlConfigFile.AddConnection"/>, or for an edit <see cref="SqlConfigFile.ReplaceConnection"/>),
    /// then its password to its store, then the offered list (<see cref="ConnectionOfferedAfterSave"/>). Whether a setting changed;
    /// null when nothing was written (the status line says why), the summary shown again.
    /// </summary>
    private bool? SqlWizardSave(SqlDraft draft, bool offer)
    {
        var c = draft.Config;
        if (!c.NeedsPassword)
        {
            c.User = null;
            c.PasswordStore = null;
        }

        if (SqlWizardMissing(draft) is { } missing)
        {
            // An edit opens on its summary, so a page it still lacks (a stored password that could not be read) is caught here.
            string unset = SqlWizardLabels[(int)missing] + " is not set";
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, unset) : SqlText.ConnectionChangeFailed(draft.Name, unset));
            return null;
        }

        if (c.Problem is { } problem)
        {
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, problem) : SqlText.ConnectionChangeFailed(draft.Name, problem));
            return null;
        }

        string path = SqlWizardPath(draft.Global);
        if ((draft.Original is { } original ? SqlConfigFile.ReplaceConnection(path, original, draft.Name, c) : SqlConfigFile.AddConnection(path, draft.Name, c)) is { } error)
        {
            Sink.Error(draft.Original is null ? SqlText.ConnectionAddFailed(draft.Name, error) : SqlText.ConnectionChangeFailed(draft.Name, error));
            return null;
        }

        Sink.Notice(draft.Original is null ? SqlText.ConnectionAdded(draft.Name, path) : SqlText.ConnectionChanged(draft.Name, path));
        if (c.NeedsPassword)
        {
            var (saved, notice) = SqlSecrets.Save(new SqlNamedConnection(draft.Name, c, path), draft.Password);
            if (saved)
            {
                Sink.Notice(notice);
            }
            else
            {
                Sink.Error(notice);
            }
        }

        ForgetOldCredential(draft.OriginalTarget, c.NeedsPassword && c.InCredentialManager ? c.CredentialTarget(draft.Name) : null);
        return ApplyOfferedAfterSave(SettingsField.SqlConnectionsOffered, _settings.Current.SqlConnectionsOffered, draft.Original, draft.Name, offer, (d, next) => d.SqlConnectionsOffered = next);
    }
}
