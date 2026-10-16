using System.Globalization;
using System.Net;
using System.Text;
using NeonSidekick.EventLog;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Network;

/// <summary>
/// The network tools' answers (2026-10-15, the admin-tools plan): a header line, then a line per record, echo, hop, port or certificate.
/// What a stranger wrote — a certificate's subject, issuer and names, a TXT, PTR or other record's data, a hop's name — is scrubbed and
/// fenced (<see cref="UntrustedText.Fenced"/>), with <see cref="UntrustedText.QuoteNote"/> under any answer holding some. An answer is held
/// to <see cref="MaxChars"/>, its last line saying what was left out and how to narrow the call; a single value to
/// <see cref="UntrustedText.MaxValueChars"/>. Times in the user's zone with the offset (the event logs' formatter). <c>/net</c> shows the
/// same lines. Pure.
/// </summary>
public static class NetworkRender
{
    /// <summary>The most characters an answer carries back to the model.</summary>
    public const int MaxChars = 16_000;

    /// <summary>An answer under construction: the lines, whether anything was fenced, the cap.</summary>
    private sealed class Answer(string header)
    {
        private readonly StringBuilder _text = new(header);
        private bool _fenced;
        private int _left;
        private string _leftNoun = "lines";
        private string _narrow = "";

        public string Fence(string value)
        {
            _fenced = true;
            return UntrustedText.Fenced(UntrustedText.Cut(value));
        }

        /// <summary>Adds a line, or counts it as left out once the cap is near.</summary>
        public void Line(string line)
        {
            if (_left > 0 || _text.Length + line.Length + 1 > MaxChars - 400)
            {
                _left++;
                return;
            }

            _text.Append('\n').Append(line);
        }

        public void LeftOut(string noun, string narrow)
        {
            _leftNoun = noun;
            _narrow = narrow;
        }

        public void Extra(int more)
        {
            _left += more;
        }

        public override string ToString()
        {
            var answer = new StringBuilder(_text.ToString());
            if (_left > 0)
            {
                answer.Append('\n').Append(_left.ToString("N0", CultureInfo.InvariantCulture)).Append(" more ").Append(_leftNoun)
                    .Append(_narrow.Length > 0 ? "; " + _narrow : "").Append('.');
            }

            if (_fenced)
            {
                answer.Append('\n').Append(UntrustedText.QuoteNote);
            }

            return answer.ToString();
        }
    }

    // ── DNS ─────────────────────────────────────────────────────────────────

    /// <summary>A system lookup of A/AAAA: the addresses the resolver gave.</summary>
    public static string SystemLookup(string name, ushort type, IReadOnlyList<IPAddress> addresses)
    {
        var answer = new Answer($"DNS {name} {DnsWire.TypeName(type)} via the system resolver: {Count(addresses.Count, "address", "addresses")}");
        foreach (var address in addresses)
        {
            answer.Line("  " + address);
        }

        answer.LeftOut("addresses", "");
        return answer.ToString();
    }

    /// <summary>A lookup through the app's own client: the server, the RCODE, then each record (data fenced, an address as itself).</summary>
    public static string Lookup(string name, ushort type, DnsOutcome outcome, bool systemServer)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        string via = outcome.Server is { } server ? server.Address + (systemServer ? " (system resolver)" : "") + (outcome.OverTcp ? " over TCP" : "") : "?";
        if (outcome.Reply is not { } reply)
        {
            return outcome.Error ?? NetworkText.DnsNoAnswer(via);
        }

        if (reply.Rcode != 0)
        {
            return $"DNS {name} {DnsWire.TypeName(type)} via {via}: {NetworkText.Rcode(reply.Rcode)}";
        }

        var answer = new Answer($"DNS {name} {DnsWire.TypeName(type)} via {via}: {Count(reply.Answers.Count, "record")}" + (reply.Truncated && !outcome.OverTcp ? " (cut short)" : ""));
        foreach (var record in reply.Answers)
        {
            bool plain = record.Type is DnsWire.A or DnsWire.Aaaa;
            string owner = string.Equals(record.Name.TrimEnd('.'), name.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ? record.Name : answer.Fence(record.Name);
            answer.Line($"  {owner}  {record.TypeName}  {record.Ttl.ToString(CultureInfo.InvariantCulture)}  {(plain ? record.Data : answer.Fence(record.Data))}");
        }

        answer.LeftOut("records", "ask for one type");
        return answer.ToString();
    }

    // ── Ping and trace ──────────────────────────────────────────────────────

    /// <summary>A ping's summary line and one line per echo.</summary>
    public static string Ping(string host, IPAddress address, IReadOnlyList<PingHop> echoes)
    {
        ArgumentNullException.ThrowIfNull(echoes);
        int received = echoes.Count(e => e.Status == PingStatus.Reply);
        int loss = echoes.Count == 0 ? 0 : (int)Math.Round(100.0 * (echoes.Count - received) / echoes.Count);
        var times = echoes.Where(e => e.Status == PingStatus.Reply && e.Rtt is not null).Select(e => e.Rtt!.Value.TotalMilliseconds).ToList();
        string trip = times.Count == 0 ? "" : $"; round trip min {Ms(times.Min())}, avg {Ms(times.Average())}, max {Ms(times.Max())}";
        var answer = new Answer($"Ping {host} ({address}): {echoes.Count.ToString(CultureInfo.InvariantCulture)} sent, {received.ToString(CultureInfo.InvariantCulture)} received, {loss.ToString(CultureInfo.InvariantCulture)}% loss{trip}");
        for (int i = 0; i < echoes.Count; i++)
        {
            var e = echoes[i];
            string n = "#" + (i + 1).ToString(CultureInfo.InvariantCulture);
            answer.Line(e.Status switch
            {
                PingStatus.Reply => $"  {n} reply from {e.From} in {Ms(e.Rtt?.TotalMilliseconds ?? 0)}",
                PingStatus.TtlExpired => $"  {n} TTL ran out at {e.From}",
                PingStatus.Unreachable => $"  {n} unreachable" + (e.From is null ? "" : " (said " + e.From + ")"),
                PingStatus.TimedOut => $"  {n} no answer",
                _ => $"  {n} failed: {e.Detail}",
            });
        }

        return answer.ToString();
    }

    /// <summary>A trace's header and a line per hop (a hop's name fenced).</summary>
    public static string Trace(string host, IPAddress address, int maxHops, IReadOnlyList<TraceHop> hops)
    {
        ArgumentNullException.ThrowIfNull(hops);
        string ending = hops.Count > 0 && hops[^1].Reached ? $"reached in {Count(hops.Count, "hop")}"
            : hops.Count > 0 && hops[^1].Unreachable ? $"unreachable after {Count(hops.Count, "hop")}"
            : hops.Count < maxHops ? $"stopped after {Count(hops.Count, "hop")} (the 45 s budget)"
            : $"not reached in {Count(maxHops, "hop")}";
        var answer = new Answer($"Trace to {host} ({address}), {Count(maxHops, "hop")} at most: {ending}");
        foreach (var hop in hops)
        {
            string ttl = hop.Ttl.ToString(CultureInfo.InvariantCulture).PadLeft(3);
            answer.Line(hop.From is null
                ? $"  {ttl}  *"
                : $"  {ttl}  {hop.From}  {(hop.Rtt is { } rtt ? Ms(rtt.TotalMilliseconds) : "")}{(hop.Name is { } name ? "  " + answer.Fence(name) : "")}".TrimEnd());
        }

        return answer.ToString();
    }

    // ── Ports ───────────────────────────────────────────────────────────────

    /// <summary>One line per port: its state on each address.</summary>
    public static string Ports(string host, IReadOnlyList<IPAddress> addresses, IReadOnlyList<PortResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var ports = results.Select(r => r.Port).Distinct().ToList();
        var answer = new Answer($"Ports on {host}: {Count(ports.Count, "port")} on {Count(addresses.Count, "address", "addresses")}");
        foreach (int port in ports)
        {
            var states = results.Where(r => r.Port == port).Select(r => State(r.State) + (addresses.Count > 1 ? " on " + r.Address : ""));
            answer.Line($"  {port.ToString(CultureInfo.InvariantCulture),-5} {string.Join(", ", states)}");
        }

        return answer.ToString();
    }

    public static string State(PortState state) => state switch
    {
        PortState.Open => "open",
        PortState.Refused => "refused",
        PortState.NoAnswer => "no answer",
        _ => "unreachable",
    };

    // ── TLS ─────────────────────────────────────────────────────────────────

    /// <summary>The handshake: protocol and cipher, the leaf (subject, issuer, names, validity, days left, thumbprint), the chain, its status, the policy errors.</summary>
    public static string Tls(TlsReport report, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        string preamble = report.Preamble == StartTls.None ? "" : " after " + TlsPreamble.Word(report.Preamble).ToUpperInvariant() + (report.Preamble == StartTls.Rdp ? " negotiation" : " STARTTLS");
        var answer = new Answer($"TLS {report.Host}:{TlsProbe.Port(report.Port)} ({report.Address}){preamble}, server name {report.ServerName}: {report.Protocol}, {report.Cipher}");
        if (report.Leaf is { } leaf)
        {
            answer.Line("  subject: " + answer.Fence(leaf.Subject));
            answer.Line("  issuer: " + answer.Fence(leaf.Issuer));
            if (leaf.Names.Count > 0)
            {
                answer.Line("  names: " + string.Join(", ", leaf.Names.Take(50).Select(answer.Fence)) + (leaf.Names.Count > 50 ? $" and {(leaf.Names.Count - 50).ToString(CultureInfo.InvariantCulture)} more" : ""));
            }

            answer.Line($"  valid: {EventLogText.Time(leaf.NotBefore, zone)} to {EventLogText.Time(leaf.NotAfter, zone)} ({DaysLeft(leaf, now)})");
            answer.Line("  sha256: " + leaf.Sha256);
        }
        else
        {
            answer.Line("  no certificate was sent");
        }

        if (report.Chain.Count > 0)
        {
            answer.Line("  chain: " + string.Join(" › ", report.Chain.Select(answer.Fence)));
        }

        answer.Line("  chain status: " + (report.ChainStatus.Count == 0 ? "ok" : string.Join("; ", report.ChainStatus.Select(s => UntrustedText.Scrub(s)))));
        answer.Line("  policy errors: " + TlsProbe.PolicyWords(report.PolicyErrors));
        return answer.ToString();
    }

    // ── Certificate stores ──────────────────────────────────────────────────

    /// <summary>A store's certificates after the filter, soonest to end first, up to <paramref name="limit"/>.</summary>
    public static string Certificates(CertificateListing listing, IReadOnlyList<CertificateView> shown, int limit, string? filter, int? expiring, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(shown);
        if (listing.Error is { } error)
        {
            return error;
        }

        var narrowed = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            narrowed.Add("filter \"" + filter.Trim() + "\"");
        }

        if (expiring is { } days)
        {
            narrowed.Add("ending within " + Count(days, "day"));
        }

        int listed = Math.Min(limit, shown.Count);
        var answer = new Answer($"Certificates in the {listing.Location} {listing.Store} store: {listed.ToString(CultureInfo.InvariantCulture)} of {listing.Certificates.Count.ToString(CultureInfo.InvariantCulture)} shown"
            + (narrowed.Count > 0 ? " (" + string.Join(", ", narrowed) + ")" : ""));
        foreach (var certificate in shown.Take(limit))
        {
            answer.Line($"  {answer.Fence(certificate.Subject)} issued by {answer.Fence(certificate.Issuer)}; ends {EventLogText.Time(certificate.NotAfter, zone)} ({DaysLeft(certificate, now)}); private key: {(certificate.HasPrivateKey ? "yes" : "no")}; sha256 {certificate.Sha256}");
        }

        answer.Extra(Math.Max(0, shown.Count - limit));
        answer.LeftOut("certificates", "pass filter or expiring_within_days, or raise limit");
        return answer.ToString();
    }

    /// <summary><c>17 days left</c>, <c>ends today</c>, <c>expired 3 days ago</c>.</summary>
    public static string DaysLeft(CertificateView certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (certificate.NotAfter < now)
        {
            int ago = Math.Max(1, (int)Math.Ceiling((now - certificate.NotAfter).TotalDays));
            return "expired " + Count(ago, "day") + " ago";
        }

        int days = certificate.DaysLeft(now);
        return days == 0 ? "ends today" : Count(days, "day") + " left";
    }

    private static string Ms(double milliseconds) => milliseconds < 1 ? "<1 ms" : Math.Round(milliseconds).ToString("0", CultureInfo.InvariantCulture) + " ms";

    private static string Count(int n, string singular, string? plural = null) => EventLogText.Count(n, singular, plural);
}
