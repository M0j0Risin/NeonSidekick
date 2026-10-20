using System.Globalization;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Web;

namespace NeonSidekick.Network;

/// <summary>
/// The network tools' visible strings (2026-10-15, the admin-tools plan): the log category, the glyph, the refusals of a call, the reach and
/// list refusals, the budget's, the DNS client's and the preambles' sentences, and <c>/net</c>'s words. Pinned by the tests.
/// </summary>
public static class NetworkText
{
    /// <summary>The log category of everything network.</summary>
    public const string Category = "Network";

    /// <summary>The group's glyph: a dish (the toolbar's, <c>/net</c>'s).</summary>
    public const string Glyph = "📡";

    /// <summary>The working spinner's words.</summary>
    public const string Working = "Checking the network…";

    /// <summary>The transcript's note for a network tool's result: its first line (<c>EventLogText.Note</c>'s shape).</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n', StringComparison.Ordinal);
        string first = newline < 0 ? result : result[..newline];
        return "(" + Glyph + " " + (first.Length > 160 ? first[..159] + "…" : first) + ")";
    }

    // ── Refusals of a call ──────────────────────────────────────────────────

    public static string Missing(string argument) => $"Error: {argument} is required.";

    public static string NotBoolean(string argument, string raw) => $"Error: {argument} '{raw}' is not true or false.";

    public static string OutOfRange(string argument, string given, int min, int max) =>
        $"Error: {argument} '{given}' is out of range; it must be {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.";

    /// <summary>A target that is not one host: a range, a block, a wildcard, a list. Pinned.</summary>
    public static string NotOneHost(string host) =>
        $"Error: '{Short(host)}' is not one host; the network tools take a single name or address — no ranges, blocks, wildcards or lists.";

    public static string PortInHost(string host) => $"Error: '{Short(host)}' carries a port or a scheme; give the host alone and the port separately.";

    public static string BadName(string name) => $"Error: '{Short(name)}' is not a host name or an address.";

    public static string NotResolved(string host, string detail) => $"Error: {Short(host)} could not be resolved" + (detail.Length > 0 ? ": " + detail : ".");

    public static string NoAddresses(string host) => $"Error: {Short(host)} resolved to no address.";

    public static string BadType(string type) => $"Error: type '{Short(type)}' is not one of {string.Join(", ", DnsWire.Types.Keys)}.";

    public static string BadPorts(string raw) => $"Error: ports '{Short(raw)}' is not a list of port numbers (1 to 65535), 16 at most.";

    public static string BadStartTls(string word) => $"Error: starttls '{Short(word)}' is not one of {string.Join(", ", TlsPreamble.Words.Keys)}.";

    public static string BadStore(string word) => $"Error: store '{Short(word)}' is not one of {string.Join(", ", CertificateStores.StoreWords)}.";

    public static string BadLocation(string word) => $"Error: location '{Short(word)}' is not user or machine.";

    public const string NoPinger = "Error: there is no way to send a ping here.";

    public const string NoTrace = "Error: route tracing is not available here.";

    public const string Canceled = "Error: the check was canceled.";

    // ── Reach, lists and budget ─────────────────────────────────────────────

    /// <summary>The setting's name as the refusals say it.</summary>
    public const string ReachSetting = "Network tools reach";

    /// <summary>A host the reach keeps off limits, in the web tools' words with this setting's name. Pinned.</summary>
    public static string ReachRefused(string host, NetworkRefusal refusal) => refusal == NetworkRefusal.Internet
        ? $"Error: {Short(host)} is on the internet, and {ReachSetting} allows only this machine and the local network."
        : $"Error: {Short(host)} is this machine or the local network, and {ReachSetting} allows only the internet.";

    public const string AllowedSetting = "Network tools allowed hosts";
    public const string DeniedSetting = "Network tools denied hosts";

    /// <summary>A host one of the lists refuses, naming the list and the entry. Pinned.</summary>
    public static string Denied(string host, string entry) => $"Error: {Short(host)} matches '{entry}' in {DeniedSetting}.";

    public static string NotAllowed(string host) => $"Error: {Short(host)} is not in {AllowedSetting}.";

    /// <summary>How both budget refusals open: what <see cref="IsBudgetSpent"/> knows them by.</summary>
    private const string BudgetSpentLead = "Error: this turn has checked ";

    /// <summary>The host budget spent. Pinned.</summary>
    public static string HostBudgetSpent(int hosts) =>
        $"{BudgetSpentLead}{hosts.ToString(CultureInfo.InvariantCulture)} hosts, the most Network tools max hosts per turn allows.";

    /// <summary>The port budget spent. Pinned.</summary>
    public static string PortBudgetSpent(int used, int wanted) =>
        $"{BudgetSpentLead}{used.ToString(CultureInfo.InvariantCulture)} host:port pairs and this call would add {wanted.ToString(CultureInfo.InvariantCulture)}, past what Network tools max ports per turn allows.";

    /// <summary>Whether a tool's result is one of the budget's refusals (<see cref="HostBudgetSpent"/>, <see cref="PortBudgetSpent"/>).</summary>
    public static bool IsBudgetSpent(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.StartsWith(BudgetSpentLead, StringComparison.Ordinal);
    }

    /// <summary>The transcript's once-a-turn note when the budget refused a call. Pinned.</summary>
    public const string BudgetNote = "(" + Glyph + " the network tools' budget for this turn is spent)";

    /// <summary>The dim note under <c>/net</c>'s answer when the model's call would have been refused (the user's hand is not judged). Pinned.</summary>
    public static string ModelWouldBeRefused(string reason) => "(the model's net_ tools would be refused here: " + reason.Replace("Error: ", "", StringComparison.Ordinal) + ")";

    // ── Host lists (the settings' typed-list rows) ──────────────────────────

    public const string BlankHostEntry = "an entry cannot be blank";

    public static string BadCidr(string entry) => $"'{Short(entry)}' is not a CIDR block such as 10.20.0.0/16 or fd00::/8";

    public static string BadGlob(string entry) => $"'{Short(entry)}' is not a glob of whole labels such as *.corp.example";

    public static string BadHostEntry(string entry) => $"'{Short(entry)}' is not a host name, an address, a glob such as *.corp.example or a CIDR block";

    public static string HostListProblemLogLine(string setting, string problem) => $"{setting}: {problem}; that entry is left out.";

    // ── Ping ────────────────────────────────────────────────────────────────

    public const string NoIpv6Ping = "IPv6 ping is not available here";

    // ── DNS ─────────────────────────────────────────────────────────────────

    public const string DnsTruncatedReply = "the reply was cut short";

    public const string DnsPointerLoop = "the reply's names loop";

    public const string DnsNameTooLong = "a name in the reply is longer than 255 bytes";

    public const string NoDnsServers = "Error: this machine has no DNS server configured.";

    public static string DnsNoAnswer(string server) => $"Error: {server} gave no usable answer in time.";

    /// <summary>An RCODE as a sentence: NXDOMAIN, SERVFAIL, REFUSED and the rest.</summary>
    public static string Rcode(int code) => code switch
    {
        0 => "no error",
        1 => "the server could not read the query (FORMERR)",
        2 => "the server failed (SERVFAIL)",
        3 => "no such name (NXDOMAIN)",
        4 => "the server does not do that (NOTIMP)",
        5 => "the server refused (REFUSED)",
        _ => "error code " + code.ToString(CultureInfo.InvariantCulture),
    };

    // ── TLS preambles ───────────────────────────────────────────────────────

    /// <summary>
    /// A server's refusal before TLS, its own words quoted: scrubbed and fenced with <see cref="UntrustedText.QuoteNote"/> under them (2026-10-19,
    /// the network review: a hostile banner reached the model and the transcript note bare, control characters and all).
    /// </summary>
    public static string PreambleRefused(string protocol, int port, string said) =>
        $"Error: the {protocol} server on port {TlsProbe.Port(port)} refused: {UntrustedText.Fenced(Short(said))}\n{UntrustedText.QuoteNote}";

    /// <summary>A server that never offers STARTTLS. Pinned.</summary>
    public static string NoStartTls(int port) => $"Error: this server does not offer STARTTLS on port {TlsProbe.Port(port)}.";

    public const string PreambleGarbled = "Error: the server's answer before TLS was not what its protocol says.";

    public const string PreambleTooLong = "Error: the server said too much before TLS (over 16 KB, 64 lines or a 1,024-byte line).";

    public const string PreambleClosed = "Error: the server closed the connection before TLS.";

    public static string LdapStartTlsRefused(int port, int code) => $"Error: the LDAP server on port {TlsProbe.Port(port)} refused StartTLS (result code {code.ToString(CultureInfo.InvariantCulture)}).";

    public static string RdpNoTls(int port) => $"Error: the RDP server on port {TlsProbe.Port(port)} offers no TLS, only standard RDP security.";

    public static string RdpRefused(int port, int code) => $"Error: the RDP server on port {TlsProbe.Port(port)} refused TLS (failure code {code.ToString(CultureInfo.InvariantCulture)}).";

    public static string HandshakeFailed(string host, int port, string detail) => $"Error: the TLS handshake with {Short(host)}:{TlsProbe.Port(port)} failed: {Short(detail)}";

    public static string ConnectFailed(string host, int port, string detail) => $"Error: could not connect to {Short(host)}:{TlsProbe.Port(port)}: {Short(detail)}";

    public static string TimedOut(string what) => $"Error: {what} took too long and was stopped.";

    // ── Certificate stores ──────────────────────────────────────────────────

    /// <summary>A store a Mac does not keep. Pinned.</summary>
    public const string StoreNotKeptHere = "Error: that certificate store is not kept here.";

    public static string StoreUnreadable(string store, string location, string detail) => $"Error: the {location} {store} store could not be read: {Short(detail)}";

    /// <summary>Why <c>net_certs</c> is not offered with the network tools on. Pinned.</summary>
    public const string CertStoresOffReason = "Network certificate stores is off";

    // ── /net ────────────────────────────────────────────────────────────────

    /// <summary><c>/net</c>'s verbs, in the order completion offers them.</summary>
    public static readonly IReadOnlyList<string> Verbs = ["ping", "trace", "port", "dns", "tls", "certs"];

    /// <summary>The completion note beside a verb. Pinned.</summary>
    public static string VerbNote(string verb) => verb switch
    {
        "ping" => "echoes to a host",
        "trace" => "the route to a host",
        "port" => "whether TCP ports are open",
        "dns" => "look a name up",
        "tls" => "a TLS handshake and its certificate",
        "certs" => "a certificate store",
        _ => "",
    };

    /// <summary><c>/net</c> with no verb or one it does not know. Pinned.</summary>
    public const string Usage = "Usage: /net ping <host> [count] · trace <host> [names] · port <host> <ports> · dns <name> [type] [server] · tls <host> [port] [starttls] · certs [store] [user|machine] [filter]";

    // ── --network-check ─────────────────────────────────────────────────────

    public static string CheckLine(string what, string outcome) => $"  {what,-6} {outcome}";

    /// <summary>A string cut for a sentence: 80 characters.</summary>
    public static string Short(string text) => text.Length > 80 ? text[..79] + "…" : text;
}
