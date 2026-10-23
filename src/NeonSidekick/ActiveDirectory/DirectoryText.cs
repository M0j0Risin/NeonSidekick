using System.Globalization;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// The directory's visible strings (2026-10-15, the admin-tools plan): the log category, the glyph, the menu path, the refusals, the cards'
/// words, the error sentences (<see cref="DirectoryErrors"/>) and <c>/ad</c>'s words. Pinned by the tests.
/// </summary>
public static class DirectoryText
{
    /// <summary>A bind whose session read back neither signed nor sealed (2026-10-23, J3; here since the branch review of DP7).</summary>
    public const string NotSealedAfterBind = "the session was not signed and sealed after the bind";

    public const string Category = "Directory";

    /// <summary>The group's glyph: an office building (👥 is botchat's resume, 🪪 the profile's).</summary>
    public const string Glyph = "🏢";

    public const string MenuPath = "/tools › Admin › Directory";

    /// <summary>
    /// What a redacted free-text match read (2026-10-23, the admin review's E2, DA5): the values matched on their redacted text, how many
    /// objects that was, and the call that goes on. Pinned.
    /// </summary>
    public static string RedactedScan(string what, int scanned, int from, string? next) =>
        $"{what} matched on their redacted text, over {scanned.ToString("N0", CultureInfo.InvariantCulture)} objects" + (from > 0 ? $" from the {from.ToString("N0", CultureInfo.InvariantCulture)}th" : "")
        + (next is null ? "" : "; more are left, next: " + next);

    /// <summary>A redacted scan the call's time ended before it reached its first object (2026-10-23, the branch review of DP7).</summary>
    public static string ScanUnreached(string what, int from) =>
        $"{what} not matched: the scan did not reach the {from.ToString("N0", CultureInfo.InvariantCulture)}th object in the call's time; narrow it with ou= or type=";

    /// <summary>The same find from the next object on, as the model calls it.</summary>
    public static string NextScan(FindOptions o, int from)
    {
        ArgumentNullException.ThrowIfNull(o);
        var parts = new List<string> { "kind=" + o.Kind };
        if (o.Words is { } words)
        {
            parts.Add("words=\"" + Llm.Tools.UntrustedText.OneLine(words).Replace("\"", "'", StringComparison.Ordinal) + "\"");
        }

        if (o.Type is { } type)
        {
            parts.Add("type=" + type);
        }

        if (o.Field is { } field)
        {
            parts.Add("field=" + field);
        }

        if (o.Ou is { } ou)
        {
            parts.Add("ou=\"" + Llm.Tools.UntrustedText.OneLine(ou).Replace("\"", "'", StringComparison.Ordinal) + "\"");
        }

        parts.Add("scan_from=" + from.ToString(CultureInfo.InvariantCulture));
        return "ad_find(" + string.Join(", ", parts) + ")";
    }

    /// <summary>The row a domain's password is set with (2026-10-23, the wizard's unticked save, H9).</summary>
    public const string SetPasswordRow = "Directory set password";

    public const string Working = "Reading the directory…";

    /// <summary>This PC's own domain when joined: built in, reserved in <c>ad.json</c>.</summary>
    public const string CurrentName = "current";

    /// <summary>The refusal off Windows. Pinned.</summary>
    public const string NoneHere = "Error: there is no directory to read here.";

    public const string NoneHereReason = "there is no directory to read here";

    // ── A Mac's directory (2026-10-18, the Mac AD plan): Kerberos only, no joined PC ──

    /// <summary>Who a <c>windows</c> sign-in is on a Mac: the default Kerberos ticket. Pinned.</summary>
    public const string MacTicketAccount = "your Kerberos ticket (kinit)";

    /// <summary>The wizard's sign-in rows on a Mac. Pinned.</summary>
    public static readonly IReadOnlyList<string> MacAuthRows = ["windows  your Kerberos ticket (kinit)", "runas    as another account, name@REALM (Kerberos)"];

    /// <summary>How a managed session is connected: <c>389, Kerberos as ldap/dc1.corp.example, sealed</c>. Pinned.</summary>
    public static string Transport(int port, bool tls, string serviceName, string layer) =>
        port.ToString(System.Globalization.CultureInfo.InvariantCulture) + (tls ? " TLS" : "") + ", Kerberos as " + serviceName + ", " + layer;

    /// <summary>A test's line with how it connected inside its full stop: <c>…, 1 user read; 389, Kerberos as …, sealed.</c> Pinned.</summary>
    public static string WithTransport(string line, string? transport) =>
        transport is null ? line : line.TrimEnd('.') + "; " + transport + ".";

    /// <summary>The SASL layer's word in <see cref="Transport"/>.</summary>
    public static string LayerWord(string layer) => layer switch
    {
        "Confidentiality" => "sealed",
        "Integrity" => "signed",
        _ => "no SASL layer",
    };

    /// <summary>
    /// A runas domain held after its password was refused (2026-10-18, the user's call, both systems): never tried again with that password,
    /// so a model's retries cannot lock the account. Pinned.
    /// </summary>
    public static string Held(string domain, string account) =>
        $"Error: {domain} refused the password for {account}, so it is not tried again (repeated tries can lock the account). Set it again with Directory set password on {MenuPath}.";

    /// <summary>
    /// How far this machine's clock is from a KDC's (2026-10-20, the code review), whole minutes rounded up, for <see cref="DirectoryErrors"/>'
    /// clock sentence: "12 minutes behind" when the KDC's time is later, "ahead of" when earlier. Pinned.
    /// </summary>
    public static string ClockOff(TimeSpan kdcAhead)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling(kdcAhead.Duration().TotalMinutes));
        return minutes.ToString(CultureInfo.InvariantCulture) + (minutes == 1 ? " minute " : " minutes ") + (kdcAhead > TimeSpan.Zero ? "behind" : "ahead of");
    }

    /// <summary>What was wrong with a DC's LDAPS certificate (2026-10-18), for <see cref="DirectoryErrors"/>' refusal. Pinned.</summary>
    /// <remarks>
    /// The chain's own reasons named (2026-10-23, the admin review's S3: every chain error was "no authority this machine trusts signed it", so an
    /// expired, revoked or unchecked certificate sent the user to trust a CA to no effect).
    /// </remarks>
    public static string CertificateRefused(string host, System.Net.Security.SslPolicyErrors errors,
        System.Security.Cryptography.X509Certificates.X509ChainStatusFlags chain = System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NoError)
    {
        var why = new List<string>();
        if (errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            why.Add("it sent none");
        }

        if (errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            why.Add("it does not name " + host);
        }

        if (errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors))
        {
            why.AddRange(ChainWords(chain));
        }

        return string.Join(", and ", why);
    }

    /// <summary>A chain's failures in words; "no authority this machine trusts signed it" when none of the named ones is set.</summary>
    public static IReadOnlyList<string> ChainWords(System.Security.Cryptography.X509Certificates.X509ChainStatusFlags chain)
    {
        var words = new List<string>();
        if ((chain & (System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NotTimeValid | System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NotTimeNested)) != 0)
        {
            words.Add("it has expired or is not valid yet");
        }

        if ((chain & System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.Revoked) != 0)
        {
            words.Add("it has been revoked");
        }

        if ((chain & (System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown | System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation)) != 0)
        {
            words.Add("whether it was revoked could not be checked (its revocation list or OCSP server was not reached)");
        }

        if (words.Count == 0 || (chain & (System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.UntrustedRoot | System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.PartialChain)) != 0)
        {
            words.Add("no authority this machine trusts signed it");
        }

        return words;
    }

    /// <summary>The wizard's account question on a Mac. Pinned.</summary>
    public const string MacUserQuestion = "The account as name@REALM (reader@CORP.EXAMPLE): Kerberos, so the realm in capitals.";

    /// <summary>Why the group is not offered on an unjoined PC with no domain ticked. Pinned.</summary>
    public const string NoDomainReason = "no domain is configured";

    /// <summary>The longest transcript note (the renderer cuts every tool note at 200 from the end, so <see cref="Note"/> fits itself first).</summary>
    public const int NoteLimit = 200;

    /// <summary>
    /// A tool result's transcript note: its first line, cut at 160; and a <c>to</c> save's file named after it whole (2026-10-15, the AD gaps plan:
    /// a write by the model is what the user must see), the head line cut first so the whole note stays within <see cref="NoteLimit"/> — a long
    /// name the model chose cut in its middle, keeping <c>.csv</c>. The save line is the result's last and starts <see cref="SavedPrefix"/>.
    /// </summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n', StringComparison.Ordinal);
        string first = newline < 0 ? result : result[..newline];
        int last = result.LastIndexOf('\n');
        string tailLine = last < 0 ? "" : result[(last + 1)..];
        int to = tailLine.IndexOf(" to ", StringComparison.Ordinal);
        if (!tailLine.StartsWith(SavedPrefix, StringComparison.Ordinal) || to < 0)
        {
            return "(" + Glyph + " " + (first.Length > 160 ? first[..159] + "…" : first) + ")";
        }

        string file = tailLine[(to + 4)..].Split(';')[0];
        if (file.Length > 80)
        {
            file = file[..38] + "…" + file[^41..];
        }

        string tail = " · saved to " + file + ")";
        int room = NoteLimit - 3 - Glyph.Length - tail.Length;
        return "(" + Glyph + " " + (first.Length > room ? first[..Math.Max(0, room - 1)] + "…" : first) + tail;
    }

    public static string WithheldFromAdvisor(string tool, string? name) =>
        "(" + tool + (string.IsNullOrWhiteSpace(name) ? "" : " " + name.Trim()) + ": read it with your own directory tools)";

    // ── Refusals of a call ──────────────────────────────────────────────────

    public static string Missing(string argument) => $"Error: {argument} is required.";

    public static string NotBoolean(string argument, string raw) => Llm.Tools.ArgumentText.NotBoolean(argument, raw);

    public static string OutOfRange(string argument, string given, int min, int max) => Llm.Tools.ArgumentText.OutOfRange(argument, given, min, max);

    /// <summary>A choice the argument does not take (2026-10-15, the AD gaps plan: one refusal for <c>kind</c>, <c>type</c>, <c>field</c> and <c>members</c>). Pinned.</summary>
    public static string BadChoice(string argument, string word, IReadOnlyList<string> choices) => $"Error: {argument} '{Llm.Tools.ArgumentText.Echo(word)}' is not {Join(choices)}.";

    /// <summary>A kind <c>ad_find</c> has not: its list from <see cref="DirectoryQueries.FindKinds"/>. Pinned.</summary>
    public static string BadKind(string word) => BadChoice("kind", word, DirectoryQueries.FindKinds);

    /// <summary>A kind that needs words given none (<c>name</c>, <c>field</c>, <c>os</c>). Pinned.</summary>
    public static string WordsNeeded(string kind) => $"Error: kind {kind} needs words to look for.";

    /// <summary>An argument the kind does not take, naming the kinds that do (2026-10-15, the AD gaps plan). Pinned.</summary>
    public static string NotTaken(string argument, string kind, IReadOnlyList<string> takers) => $"Error: kind {kind} takes no {argument}; {argument} is for {Join(takers)}.";

    /// <summary>A field the type does not have (a group, a computer, an OU and a service account have a description alone). Pinned.</summary>
    public static string FieldNotForType(string field, string type, IReadOnlyList<string> fields) => $"Error: field '{field}' is not one a {type} has; a {type} takes {Join(fields)}.";

    /// <summary><c>office</c> on a redacted domain: the office is among the personal fields left out. Pinned.</summary>
    public const string OfficeRedacted = "Error: field office is left out on a redacted domain (phones, addresses and offices are personal data there).";

    /// <summary>"a, b or c".</summary>
    public static string Join(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        return words.Count switch
        {
            0 => "",
            1 => words[0],
            _ => string.Join(", ", words.Take(words.Count - 1)) + " or " + words[^1],
        };
    }

    public static string NotFound(string what, string name, string domain) => $"Error: no {what} named '{name}' in {domain}.";

    /// <summary>A clicked object that has gone since (2026-10-15, the AD gaps plan): by the row's name, never the DN. Pinned.</summary>
    public static string Gone(string name, string domain) => $"'{name}' is no longer in {domain}.";

    /// <summary>The card's title over the accounts a name matches. Pinned.</summary>
    public static string AmbiguousTitle(string name, int count) => $"'{name}' matches {count.ToString(CultureInfo.InvariantCulture)} accounts";

    /// <summary>An OU name several OUs share (2026-10-15, the AD gaps plan): their paths. Pinned.</summary>
    public static string AmbiguousOu(string name, IReadOnlyList<string> paths) =>
        $"Error: '{name}' matches {paths.Count.ToString(CultureInfo.InvariantCulture)} OUs; name one by its path:\n  " + string.Join("\n  ", paths);

    /// <summary>The card's title over the OUs a name matches: a click opens the OU, never the search inside it. Pinned.</summary>
    public static string AmbiguousOuTitle(string name, int count) => $"'{name}' matches {count.ToString(CultureInfo.InvariantCulture)} OUs; type ou: with the path shown";

    /// <summary>A name that matches several objects: the candidates listed; <paramref name="more"/> when more matched than are listed (Q13). Pinned.</summary>
    public static string Ambiguous(string name, IReadOnlyList<string> candidates, bool more = false) =>
        $"Error: '{name}' matches {(more ? "more than " : "")}{candidates.Count.ToString(CultureInfo.InvariantCulture)} accounts; name one by its account name{(more ? " (the first " + candidates.Count.ToString(CultureInfo.InvariantCulture) + " listed)" : "")}:\n  " + string.Join("\n  ", candidates);

    public static string TimedOut(string domain) => $"Error: the search in {domain} took too long and was stopped.";

    /// <summary>A call refused while earlier ones to the same server are still waiting on it (a bind it never answered).</summary>
    public static string StillWaiting(string domain, string server) =>
        $"Error: earlier calls to {server} for {domain} are still waiting on the server, which is not answering; try again later.";

    // ── Domains (ad.json) ────────────────────────────────────────────────────

    public const string NoDomain = "\"domain\" is required";

    public const string CurrentReserved = "\"current\" is this PC's own domain, built in; name the entry otherwise";

    public static string BadBaseDn(string text) => $"\"baseDn\" is '{text}'; it must be a distinguished name such as DC=corp,DC=example";

    public static string UnknownDomain(string name, string offered) =>
        $"Error: no domain named '{Llm.Tools.ArgumentText.Echo(name)}' is ticked in Directory domains offered" + (offered.Length > 0 ? $"; the ticked ones are: {offered}." : "; none is ticked.");

    public static string DomainNeeded(string offered) => $"Error: several domains are ticked ({offered}); name one with domain.";

    public const string NoDomainTicked = "Error: no domain is ticked in Directory domains offered (" + MenuPath + ").";

    public static string NoPassword(string name) => $"no password for '{name}'; set it with Directory set password on {MenuPath}";

    public static string NoCredential(string target) =>
        $"no password in {Sql.SqlText.CredentialStore} for {target}; the user can set it with {SetPasswordRow} on {MenuPath}";   // DP1 (2026-10-23, L4): no cmdkey line

    /// <remarks>The count's noun agrees (2026-10-23, the admin review's S18: "0 user read").</remarks>
    public static string TestOk(string name, string server, string domain, int users) =>
        $"Reached '{name}' at {server}: {domain}, {users.ToString(CultureInfo.InvariantCulture)} {(users == 1 ? "user" : "users")} read.";

    /// <summary>The detail of a Mac's held refusal: the KDC knew the account and gave no ticket (D3), read by <see cref="DirectoryErrors"/>.</summary>
    public const string NoTicketPrefix = "the KDC knew the account and gave no ticket";

    /// <summary>A <c>to</c> save found closed by the time the answer came (2026-10-23, F7). Pinned.</summary>
    public static string SaveClosedLate(string reason) => "Not saved: " + reason.Replace("Error: ", "", StringComparison.Ordinal);

    /// <summary>A <c>/ad</c> key word with no value (2026-10-23, V6). Pinned.</summary>
    public static string KeyNeedsValue(string key) => "/ad " + key + ": needs a value";

    // ── Cards ────────────────────────────────────────────────────────────────

    /// <summary>The note beside a last sign-in. Pinned.</summary>
    public const string StaleNote = "lastLogonTimestamp; up to about 14 days stale";

    /// <summary>A locked user's pointer to where the lockout's source machine is recorded. Pinned.</summary>
    public static string LockoutSource(string pdc) => $"the source machine is in Security event 4740 on the PDC emulator, {pdc}";

    /// <summary>The policy when no fine-grained one is named (Samba may not compute it). Pinned.</summary>
    public const string DomainPolicyMaybe = "the domain policy (a fine-grained policy may apply)";

    public const string MustChange = "must change at next sign-in";

    public const string NeverWord = "never";

    public const string ForeverWord = "until an administrator unlocks it";

    /// <summary>
    /// A scan a cap cut short (2026-10-15, the AD gaps plan: the cap's own number, 1,000 or 10,000), and the save offered only where it would be
    /// allowed (<paramref name="canSave"/>: <c>to</c> not used, more to read, and the caller may save now). Pinned.
    /// </summary>
    public static string CapNote(int cap, bool canSave) =>
        "the " + cap.ToString("N0", CultureInfo.InvariantCulture) + "-entry cap cut the search short; narrow it" + (canSave ? ", or save the whole list with to" : "");

    /// <summary>A walk or a save the call's time budget stopped (2026-10-15): what was read is the answer. Pinned.</summary>
    public const string StoppedAtTimeLimit = "stopped at the time limit; what was read is shown";

    /// <summary>A nested walk past its group cap.</summary>
    public const string GroupsNotWalked = "more than 200 groups below; the rest not walked";

    /// <summary>A group's members cut to the limit, and how to get the rest.</summary>
    public static string FirstListed(int limit, bool canSave) =>
        "the first " + limit.ToString("N0", CultureInfo.InvariantCulture) + " listed (" + (canSave ? "narrow with members, or save them all with to" : "narrow with members") + ")";

    /// <summary>A member a walk could not read, or one in another domain.</summary>
    public const string NotReadable = "not readable";

    public const string OtherDomain = "in another domain";

    /// <summary>A member whose batch's search was cut or ran out of time (2026-10-23, D7): neither unreadable nor foreign, just not read.</summary>
    public const string NotRead = "not read (the search stopped)";

    /// <summary>An account past its <c>accountExpires</c>, in a user's head (2026-10-23, H11).</summary>
    public const string AccountExpiredWord = "account expired";

    /// <summary>An account's groups whose SIDs were not all resolved (2026-10-23, Q5).</summary>
    public const string GroupsNotAllRead = "not every group was resolved (the read stopped at its limit or the time limit); the list is short";

    /// <summary>The days under which a stale find may list people who signed in lately (2026-10-23, V5, DA6).</summary>
    public const int StaleLagDays = 14;

    /// <summary>A stale find under <see cref="StaleLagDays"/>: run, and said (DA6).</summary>
    public const string StaleLagNote = "lastLogonTimestamp is copied between DCs only every 9 to 14 days, so a sign-in of the last two weeks may not show yet; some listed may have signed in lately";

    /// <summary>The domain head gave none of its policy (2026-10-23, Q6).</summary>
    public const string PolicyNotRead = "not read (the domain head gave none of it)";

    /// <summary>Groups left out of empty_groups because their primary-group read was not whole (2026-10-23, G12).</summary>
    public static string EmptyGroupsUnsure(int count) =>
        count.ToString(CultureInfo.InvariantCulture) + " more may be empty, left out: who has them as a primary group was not read whole";

    /// <summary>A member brought in by its primary group, beside the group.</summary>
    public const string PrimaryGroupWord = "primary group";

    /// <summary>A member in the group's own list, in a nested answer.</summary>
    public const string DirectWord = "direct";

    /// <summary>The server sent only the first range of a many-valued attribute (MaxValRange).</summary>
    public const string FirstRangeNote = "(the server sent only the first range of these values)";

    /// <summary>The user card's lockout row's label: <c>/ad</c>'s "Search 4740" reads it.</summary>
    public const string LockedLabel = "locked";

    // ── privileged ───────────────────────────────────────────────────────────

    public const string LeftoversTitle = "marked protected (adminCount=1) but in no protected group";

    public const string LeftoversSection = "adminCount leftovers";

    public const string LeftoverNote = "marked protected once; no longer in a protected group";

    /// <summary>A leftover when the walk did not end whole (2026-10-23, Q2): it may still be in a protected group.</summary>
    public const string NotReachedNote = "marked protected; not reached by the walk, which stopped early (it may still be in a protected group)";

    public const string ExpectedNote = "expected (built-in)";

    public static string MissingProtected(IReadOnlyList<string> names) => "not in this domain, skipped: " + string.Join(", ", names);

    // ── the domain's policies ────────────────────────────────────────────────

    public static string PoliciesTitle(int count) => "fine-grained password policies (" + count.ToString(CultureInfo.InvariantCulture) + ")";

    public const string PoliciesLabel = "fine-grained password policies";

    public const string PoliciesNone = "none found (an account without rights to see them sees none)";

    public const string PolicyUnreadable = "its settings are not readable by this account";

    /// <summary>A user card's policy row for a PSO it cannot read (the name already fenced).</summary>
    public static string PolicyUnreadOver(string fencedName) => "fine-grained: " + fencedName + " (" + PolicyUnreadable + "; worked out from the domain policy)";

    // ── to ───────────────────────────────────────────────────────────────────

    /// <summary>What starts a <c>to</c> save's line, the answer's last: <see cref="Note"/> finds it by this. Pinned.</summary>
    public const string SavedPrefix = "💾 saved ";

    public static string Saved(int rows, string file, bool stopped) =>
        SavedPrefix + rows.ToString("N0", CultureInfo.InvariantCulture) + (rows == 1 ? " row" : " rows") + " to " + file + (stopped ? "; stopped at the cap or the time limit, so it is partial" : "");

    public const string NothingSaved = "no rows; nothing saved";

    public const string NameAnother = "; name another file";

    public static string ToNeedsCsv(string path) => $"Error: to '{path}' is neither a .csv file nor an existing folder in the working directory.";

    public const string SaveNeedsFileTools = "Error: to saves a file, which needs File tools on (/tools › Files › File).";

    public const string SaveInPlanMode = "Error: to saves a file, and plan mode writes no files.";

    public const string SaveNotHere = "Error: to saves a file, which this caller may not do.";

    // ── the card's columns (also the CSV's header; pinned) ──────────────────

    public const string ColAccount = "account";
    public const string ColName = "name";
    public const string ColKind = "kind";
    public const string ColVia = "via";
    public const string ColLastSignIn = "last sign-in";
    public const string ColLockedSince = "locked since";
    public const string ColUnlocks = "unlocks";
    public const string ColExpires = "expires";
    public const string ColHow = "how";
    public const string ColOs = "os";
    public const string ColVersion = "version";
    public const string ColDescription = "description";
    public const string ColType = "type";
    public const string ColCreated = "created";
    public const string ColGroups = "groups";
    public const string ColPath = "path";
    public const string ColState = "state";
    public const string ColNote = "note";
    public const string ColOu = "ou";
    public const string ColDnsName = "DNS name";
    public const string ColPrecedence = "precedence";
    public const string ColMaxAge = "max age";
    public const string ColMinLength = "minimum length";
    public const string ColLockout = "lockout";
    public const string ColAppliesTo = "applies to";
    public const string ColSection = "section";
    public const string ColDn = "dn";

    // ── a group card's buttons (2026-10-15, the user's pick): each names the state it switches to ────────

    public const string NestedButton = "nested";
    public const string DirectButton = "direct";
    public const string UsersOnlyButton = "users only";
    public const string AllMembersButton = "all members";

    /// <summary>The card's buttons' words on the Hints bar (2026-10-16, the user's ask: every hint fits a 120-column window).</summary>
    public const string LockoutWord = "search 4740";
    public const string NestingWord = "nesting";
    public const string MembersWord = "users/all";

    /// <summary>How a value was worked out, beside it.</summary>
    public const string Computed = "computed by the domain";

    public const string WorkedOut = "worked out from the policy";
}
