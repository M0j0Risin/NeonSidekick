using System.Globalization;
using System.Text;

namespace NeonSidekick.EventLog;

/// <summary>
/// The event logs' visible strings (2026-10-15, the event-log plan): the log category, the glyph, the tools' refusals and headers, the
/// fence around text a program wrote, the redaction note, the advisors' stand-in, the sentences of <see cref="EventLogErrors"/>, and
/// <c>/eventlog</c>'s words. Pinned by the tests.
/// </summary>
public static class EventLogText
{
    /// <summary>The log category of everything event-log.</summary>
    public const string Category = "EventLog";

    /// <summary>The group's glyph: a log (the toolbar's, <c>/eventlog</c>'s title).</summary>
    public const string Glyph = "🪵";

    /// <summary>The name of this PC among the computers: built in, reserved in <c>eventlog.json</c>.</summary>
    public const string LocalName = "local";

    /// <summary>What <c>local</c> is called where a host would stand.</summary>
    public const string ThisPc = "this PC";

    /// <summary>Who a <c>windows</c> computer is read as, where an account would stand.</summary>
    public const string YouAccount = "your account";

    /// <summary>The working spinner's words.</summary>
    public const string Working = "Reading the event log…";

    // ── The fence ─────────────────────────────────────────────────────────────

    /// <summary>The fence's opening mark around text a program wrote (<see cref="Llm.Tools.UntrustedText"/>'s since later on 2026-10-15).</summary>
    public const string QuoteOpen = Llm.Tools.UntrustedText.QuoteOpen;

    /// <summary>The fence's closing mark.</summary>
    public const string QuoteClose = Llm.Tools.UntrustedText.QuoteClose;

    /// <summary>The sentence that heads an answer holding fenced text (<see cref="Llm.Tools.UntrustedText.QuoteNote"/>). Pinned.</summary>
    public const string QuoteNote = Llm.Tools.UntrustedText.QuoteNote;

    /// <summary><paramref name="text"/> fenced as quoted data (<see cref="Llm.Tools.UntrustedText.Quote"/>).</summary>
    public static string Quote(string text) => Llm.Tools.UntrustedText.Quote(text);

    /// <summary><paramref name="text"/> with its control characters replaced (<see cref="Llm.Tools.UntrustedText.Scrub"/>).</summary>
    public static string Scrub(string text) => Llm.Tools.UntrustedText.Scrub(text);

    /// <summary>
    /// The mark an event carries when its source is not registered here (2026-10-15): publisher metadata missing, or no message for
    /// it — any program could have written it under that name. Pinned.
    /// </summary>
    public const string UnregisteredSource = "source not registered: any program could have written this";

    /// <summary>The header's note under the <c>redacted</c> detail. Pinned.</summary>
    public const string RedactedNote = "secret-like values hidden as " + EventLogRedaction.Placeholder;

    /// <summary>
    /// What an advisor reads in place of an event-log tool's result in the recent lines (2026-10-15): the call, never its text, so a
    /// <c>full</c> computer's events never pass to another profile that way. Pinned.
    /// </summary>
    public static string WithheldFromAdvisor(string tool, string? log, string? record) =>
        "(" + tool + (string.IsNullOrWhiteSpace(log) ? "" : " " + log.Trim()) + (string.IsNullOrWhiteSpace(record) ? "" : " " + record.Trim()) + ": read it with your own eventlog tools)";

    /// <summary>The transcript's note for an event-log tool's result: its first line, as the Docker tools' (<c>DockerText.Note</c>).</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n', StringComparison.Ordinal);
        string first = newline < 0 ? result : result[..newline];
        return "(" + Glyph + " " + (first.Length > 160 ? first[..159] + "…" : first) + ")";
    }

    // ── Times ────────────────────────────────────────────────────────────────

    /// <summary>A moment in <paramref name="zone"/> with its offset: <c>2026-10-14 21:03:11 -05:00</c>.</summary>
    public static string Time(DateTimeOffset? moment, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (moment is not { } m)
        {
            return "(no time)";
        }

        var local = TimeZoneInfo.ConvertTime(m, zone);
        return local.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    /// <summary>A moment as a call's argument restates it: ISO 8601 with its offset in <paramref name="zone"/>.</summary>
    public static string Argument(DateTimeOffset moment, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(moment, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    /// <summary>A level as Event Viewer names it.</summary>
    public static string Level(int level, ulong keywords = 0)
    {
        if ((keywords & EventSystemValues.AuditFailureKeyword) != 0)
        {
            return "audit failure";
        }

        if ((keywords & EventSystemValues.AuditSuccessKeyword) != 0)
        {
            return "audit success";
        }

        return level switch
        {
            1 => "critical",
            2 => "error",
            3 => "warning",
            0 or 4 => "information",
            5 => "verbose",
            _ => "level " + level.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>A byte count as the logs' list shows it: <c>20.0 MB</c>.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
    };

    /// <summary>A count with its noun: <c>1 event</c>, <c>12 events</c>.</summary>
    public static string Count(long n, string singular, string? plural = null) =>
        n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");

    // ── Refusals of a call ──────────────────────────────────────────────────

    public static string Missing(string argument) => $"Error: {argument} is required.";

    public static string BadLevel(string word) => $"Error: level '{word}' is not one of critical, error, warning, information, verbose or all.";

    public static string BadIds(string part) => $"Error: id '{part}' is not an event id (0 to 65535) or a range such as 4700-4799.";

    public static string BadField(string text) => $"Error: field '{text}' is not Name=value with a field's name (letters, digits, _ . -).";

    public static string BadAudit(string text) => $"Error: audit '{text}' is not success, failure or any.";

    public static string BadTime(string argument, string text) => $"Error: {argument} '{text}' is not an age (30m, 2h, 1d, 1w) or a moment (2026-10-14T21:00).";

    public static string BadRecord(string text) => $"Error: '{text}' is not a record number.";

    public static string OutOfRange(string argument, string given, int min, int max) =>
        $"Error: {argument} '{given}' is out of range; it must be {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.";

    public static string NotBoolean(string argument, string raw) => $"Error: {argument} '{raw}' is not true or false.";

    public static string BadLiteral(string text) =>
        $"Error: '{(text.Length > 40 ? text[..40] + "…" : text)}' cannot be searched for: it holds both quote marks, a control character, or more than {EventLogXPath.MaxLiteral.ToString(CultureInfo.InvariantCulture)} characters.";

    public static string TooManyTerms(int terms, int max) =>
        $"Error: the search has {terms.ToString(CultureInfo.InvariantCulture)} terms and the event log takes {max.ToString(CultureInfo.InvariantCulture)} at most (each level, id, range end, provider, field and time bound counts one); narrow it, or split it into two searches.";

    public const string QueryRefused = "Error: the event log refused the search as malformed or too complex; use fewer ids, levels, providers or fields.";

    public static string NotSearchable(string log) => $"Error: '{log}' is an Analytic or Debug log: it reads oldest-first only and is not searchable here.";

    public const string EventTooLarge = "Error: an event was too large to read even alone.";

    public const string Canceled = "Error: the read was canceled.";

    public static string TimedOut(string computer) => $"Error: the read on {computer} took too long and was stopped; narrow the search (a level, an id or a shorter time).";

    public static string Failed(string computer, string code, string detail) =>
        $"Error: the event log on {computer} failed (code {code})" + (detail.Length > 0 ? ": " + detail : ".");

    public static string AccessDenied(string? log, string computer) =>
        $"Error: Windows refused access to {(log is null ? "that log" : "the " + log + " log")} on {computer}. To read it, join the Event Log Readers group (or run the app as administrator).";

    public static string RemoteAccessDenied(string? log, string host, string account) =>
        $"Error: {host} refused {account} access to {(log is null ? "its logs" : "the " + log + " log")}. The account needs to be in the Event Log Readers group on {host}; a local administrator's sign-in is filtered by UAC over the network, so membership is the dependable way.";

    public static string FileMissing(string path) => $"Error: {path} was not found.";

    public static string NoSuchLog(string log, string computer) => $"Error: there is no log named '{log}' on {computer}; eventlog_logs lists them.";

    public static string Unreachable(string host) =>
        $"Error: {host} did not answer the event log's RPC call. Check the name, and on {host} enable the \"Remote Event Log Management\" firewall rule.";

    public static string BadSignIn(string host, string account) => $"Error: {host} refused the sign-in as {account}: the user name or password is wrong.";

    public static string NoLogonServer(string host) => $"Error: no logon server could check the sign-in to {host}.";

    public static string AccountRefused(string host, string account, string detail) =>
        $"Error: {host} refused the account {account}" + (detail.Length > 0 ? ": " + detail : ".");

    public static string RefusedQueryLogLine(int code, string log) => $"The event log refused a query on {log} (code {code.ToString(CultureInfo.InvariantCulture)}).";

    // ── Computers ───────────────────────────────────────────────────────────

    /// <summary>
    /// The refusal off Windows (2026-10-15). "the event logs are Windows' own" until later that day: no message tells a Mac user that
    /// something needs Windows (2026-10-11, the user's ask). Pinned.
    /// </summary>
    public const string NoneHere = "Error: there are no event logs to read here.";

    public static string UnknownComputer(string name, string offered) =>
        $"Error: no computer named '{name}' is ticked in Event log computers offered" + (offered.Length > 0 ? $"; the ticked ones are: {offered}." : "; none is ticked.");

    public static string ComputerNeeded(string offered) =>
        $"Error: several computers are ticked ({offered}); name one with computer.";

    public const string NoComputerTicked = "Error: no computer is ticked in Event log computers offered (/tools › Admin › EventLog); only .evtx files in the working directory can be read.";

    public static string LogNotAllowed(string log, string computer) =>
        $"Error: the {log} log on {computer} is not among its \"logs\" in eventlog.json; eventlog_logs lists the ones it allows.";

    public const string FileWithComputer = "Error: a .evtx file is read on this PC; leave computer out for a file.";

    public static string NotAnEvtx(string path) => $"Error: '{path}' is not an .evtx file.";

    public static string NoEvent(long record, string log) => $"Error: there is no record {record.ToString(CultureInfo.InvariantCulture)} in {log}.";

    public static string SecretField(string name) => $"Error: '{name}' holds a secret, so it cannot be searched by its value here.";

    // ── Config (eventlog.json) ──────────────────────────────────────────────

    public const string BlankName = "an entry has a blank name";

    public const string NoHost = "\"host\" is required";

    public const string LocalTakesNoHost = "\"local\" is this PC: it takes only \"logs\", \"detail\" and \"description\"";

    public static string BadAuth(string word) => $"\"auth\" is '{word}'; it must be windows or runas";

    public static string BadDetail(string word) => $"\"detail\" is '{word}'; it must be full or redacted";

    public static string BadAuthMethod(string word) => $"\"authMethod\" is '{word}'; it must be default, negotiate, kerberos or ntlm";

    public const string RunAsNoUser = "runas needs the account in \"user\"";

    public const string CredmanWithWindows = "\"passwordStore\": \"credman\" is only for runas";

    public static string UnreadableFile(string detail) => $"the file could not be read: {detail}";

    public static string NoPassword(string name) => $"no password for '{name}'; set it with Event log set password on /tools › Admin › EventLog";

    public static string NoCredential(string target) => $"no password in {Sql.SqlText.CredentialStore} for {target}; set it with Event log set password on /tools › Admin › EventLog, or: {Sql.SqlText.CredentialCommand(target, "<user>")}";

    public static string CannotSignIn(string name, string reason) => $"Error: cannot sign in to '{name}': {reason}.";

    public static string ProfilesUnlistedLogLine(string folder, string detail) => $"The profiles under {folder} could not be listed, so only the home's eventlog.json was read: {detail}";

    /// <summary>A test that reached the computer. Pinned.</summary>
    public static string TestOk(string name, long record, string time) =>
        $"Reached '{name}': its newest System event is #{record.ToString(CultureInfo.InvariantCulture)} at {time}.";

    /// <summary>A test that reached the computer and found its System log empty. Pinned.</summary>
    public static string TestEmpty(string name) => $"Reached '{name}': its System log is empty.";

    // ── Lines ────────────────────────────────────────────────────────────────

    /// <summary>What an empty view says: <c>no errors or warnings in System since yesterday</c>. Pinned.</summary>
    public static string NoEvents(string log, EventLevels levels, EventAudit audit, DateTimeOffset? since, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        string what = audit switch
        {
            EventAudit.Failure => "audit failures",
            EventAudit.Success => "audit successes",
            _ => levels switch
            {
                EventLevels.None => "events",
                EventLevels.Critical | EventLevels.Error | EventLevels.Warning or EventLevels.Error | EventLevels.Warning => "errors or warnings",
                EventLevels.Error | EventLevels.Critical or EventLevels.Error => "errors",
                EventLevels.Critical => "critical events",
                EventLevels.Warning => "warnings",
                _ => EventLogQuery.LevelWords(levels).Replace(",", " or ", StringComparison.Ordinal) + " events",
            },
        };
        return "no " + what + " in " + log + (since is { } s ? " since " + Since(s, now, zone) : "");
    }

    /// <summary>A since as a person says it: <c>yesterday</c> for about a day back, else the local time.</summary>
    public static string Since(DateTimeOffset since, DateTimeOffset now, TimeZoneInfo zone)
    {
        var back = now - since;
        if (back > TimeSpan.FromHours(23) && back < TimeSpan.FromHours(25))
        {
            return "yesterday";
        }

        if (back > TimeSpan.Zero && back <= TimeSpan.FromHours(1))
        {
            return "an hour ago";
        }

        return Time(since, zone);
    }
}
