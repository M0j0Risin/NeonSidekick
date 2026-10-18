using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Network;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the six network tools share (2026-10-15, the admin-tools plan, <see cref="EventLogTool"/>'s shape): the <see cref="NetworkAccess"/>
/// door, the turn's <see cref="NetworkBudget"/> (the main chat's, reset when a turn's tools are composed; a bot's own), and the arguments'
/// readers. Every tool reads; none changes anything; all are offered headless too. A refusal is an <c>Error:</c> sentence, never a throw.
/// </summary>
public abstract class NetworkTool : AIFunction
{
    public const string HostArgument = "host";

    protected NetworkTool(NetworkAccess access, NetworkBudget budget)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        Budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    protected NetworkAccess Access { get; }

    /// <summary>The budget this tool's calls count against.</summary>
    public NetworkBudget Budget { get; }

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A boolean argument: <paramref name="fallback"/> when missing, the refusal when it is not one.</summary>
    protected static (bool Value, string? Error) Flag(AIFunctionArguments arguments, string name, bool fallback)
    {
        if (!ToolArguments.TryReadBoolean(arguments, name, out bool? value, out string raw))
        {
            return (fallback, NetworkText.NotBoolean(name, raw));
        }

        return (value ?? fallback, null);
    }

    /// <summary>A whole-number argument in <paramref name="min"/>..<paramref name="max"/>: <paramref name="fallback"/> when missing, the refusal otherwise.</summary>
    protected static (int Value, string? Error) Number(AIFunctionArguments arguments, string name, int min, int max, int fallback)
    {
        if (!ToolArguments.TryReadInt64(arguments, name, out long? value, out string raw) || value is { } n && (n < min || n > max))
        {
            string given = raw.Length > 0 ? raw : value?.ToString(CultureInfo.InvariantCulture) ?? "";
            return (fallback, NetworkText.OutOfRange(name, given, min, max));
        }

        return ((int)(value ?? fallback), null);
    }

    /// <summary>
    /// A list of ports: a JSON array of numbers or of numeric strings, one number, or a string of them split by commas or spaces; each 1–65535,
    /// <see cref="NetworkChecks.MaxPortsPerCall"/> at most, each once. The refusal for anything else or none.
    /// </summary>
    public static (IReadOnlyList<int> Ports, string? Error) Ports(object? sent)
    {
        var ports = new List<int>();
        string raw = sent switch
        {
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText(),
            null => "",
            _ => sent.ToString() ?? "",
        };
        IEnumerable<string> parts = sent switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()),
            IEnumerable<object?> list when sent is not string => list.Select(o => o?.ToString() ?? ""),
            _ => raw.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries),
        };
        foreach (string part in parts)
        {
            if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
            {
                return ([], NetworkText.BadPorts(raw));
            }

            if (!ports.Contains(port))
            {
                ports.Add(port);
            }
        }

        return ports.Count == 0 || ports.Count > NetworkChecks.MaxPortsPerCall ? ([], NetworkText.BadPorts(raw)) : (ports, null);
    }

    /// <summary>The answer the model reads: the text, never <c>/net</c>'s note.</summary>
    protected static string Text(NetworkAnswer answer) => answer.Text;
}

/// <summary><c>net_dns(name, type?, server?)</c> (2026-10-15): a DNS lookup — A/AAAA through the system resolver, the other types through the app's own client.</summary>
public sealed class NetDnsTool(NetworkAccess access, NetworkBudget budget) : NetworkTool(access, budget)
{
    public const string ToolName = "net_dns";
    public const string NameArgument = "name";
    public const string TypeArgument = "type";
    public const string ServerArgument = "server";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The name to look up, e.g. \"example.com\"; for PTR, an address such as \"8.8.8.8\" works too." },
            "type": { "type": "string", "description": "A (default), AAAA, CNAME, MX, TXT, NS, SOA, PTR, SRV or CAA." },
            "server": { "type": "string", "description": "A DNS server to ask instead of this machine's (a name or an address); leave out to ask the system's." }
          },
          "required": ["name"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Looks up a DNS name: its addresses, mail servers, text records, name servers, reverse name and other records.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, NameArgument) is not { } name)
        {
            return NetworkText.Missing(NameArgument);
        }

        string typeWord = Optional(arguments, TypeArgument) ?? "A";
        if (!DnsWire.Types.TryGetValue(typeWord, out ushort type))
        {
            return NetworkText.BadType(typeWord);
        }

        return Text(await NetworkChecks.DnsAsync(Access, Budget, name, type, Optional(arguments, ServerArgument), cancellationToken).ConfigureAwait(false));
    }
}

/// <summary><c>net_ping(host, count?)</c> (2026-10-15): echoes to one host, 4 by default, 10 at most.</summary>
public sealed class NetPingTool(NetworkAccess access, NetworkBudget budget) : NetworkTool(access, budget)
{
    public const string ToolName = "net_ping";
    public const string CountArgument = "count";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "host": { "type": "string", "description": "One host name or address." },
            "count": { "type": "integer", "description": "How many echoes, 1 to 10 (default 4)." }
          },
          "required": ["host"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Pings one host: whether it answers, the loss, and the round-trip times.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, HostArgument) is not { } host)
        {
            return NetworkText.Missing(HostArgument);
        }

        var (count, error) = Number(arguments, CountArgument, 1, NetworkChecks.MaxCount, NetworkChecks.DefaultCount);
        return error ?? Text(await NetworkChecks.PingAsync(Access, Budget, host, count, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary><c>net_trace(host, max_hops?, names?)</c> (2026-10-15): the route to one host, hop by hop.</summary>
public sealed class NetTraceTool(NetworkAccess access, NetworkBudget budget) : NetworkTool(access, budget)
{
    public const string ToolName = "net_trace";
    public const string MaxHopsArgument = "max_hops";
    public const string NamesArgument = "names";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "host": { "type": "string", "description": "One host name or address." },
            "max_hops": { "type": "integer", "description": "The most hops to try, 1 to 30 (default 30)." },
            "names": { "type": "boolean", "description": "Also look up each hop's name (slower; default false)." }
          },
          "required": ["host"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Traces the route to one host: each router on the way and its round-trip time, up to 45 seconds.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, HostArgument) is not { } host)
        {
            return NetworkText.Missing(HostArgument);
        }

        var (hops, hopsError) = Number(arguments, MaxHopsArgument, 1, NetworkChecks.MaxHops, NetworkChecks.DefaultHops);
        var (names, namesError) = Flag(arguments, NamesArgument, fallback: false);
        return hopsError ?? namesError ?? Text(await NetworkChecks.TraceAsync(Access, Budget, host, hops, names, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary><c>net_port(host, ports)</c> (2026-10-15): whether each port takes a TCP connection, on every address the host has.</summary>
public sealed class NetPortTool(NetworkAccess access, NetworkBudget budget) : NetworkTool(access, budget)
{
    public const string ToolName = "net_port";
    public const string PortsArgument = "ports";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "host": { "type": "string", "description": "One host name or address." },
            "ports": { "type": "array", "items": { "type": "integer" }, "description": "The TCP ports to try, 16 at most, e.g. [443, 3389]." }
          },
          "required": ["host", "ports"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Checks whether TCP ports on one host are open, refused or silent, on each of its addresses.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, HostArgument) is not { } host)
        {
            return NetworkText.Missing(HostArgument);
        }

        arguments.TryGetValue(PortsArgument, out object? sent);
        var (ports, error) = Ports(sent);
        return error ?? Text(await NetworkChecks.PortsAsync(Access, Budget, host, ports, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary><c>net_tls(host, port?, server_name?, starttls?)</c> (2026-10-15): a TLS handshake and what the certificate says.</summary>
public sealed class NetTlsTool(NetworkAccess access, NetworkBudget budget) : NetworkTool(access, budget)
{
    public const string ToolName = "net_tls";
    public const string PortArgument = "port";
    public const string ServerNameArgument = "server_name";
    public const string StartTlsArgument = "starttls";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "host": { "type": "string", "description": "One host name or address." },
            "port": { "type": "integer", "description": "The port (default 443)." },
            "server_name": { "type": "string", "description": "The name to ask the certificate for (SNI); leave out for the host." },
            "starttls": { "type": "string", "description": "smtp, imap, pop3, ldap, rdp or none; leave out to guess from the port (25/587 smtp, 143 imap, 110 pop3, 389 ldap, 3389 rdp)." }
          },
          "required": ["host"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Connects to a TLS service and reports the protocol, the cipher and the certificate: its names, issuer, days left and chain problems.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, HostArgument) is not { } host)
        {
            return NetworkText.Missing(HostArgument);
        }

        var (port, portError) = Number(arguments, PortArgument, 1, 65535, 443);
        if (portError is not null)
        {
            return portError;
        }

        StartTls? preamble = null;
        if (Optional(arguments, StartTlsArgument) is { } word)
        {
            if (!TlsPreamble.Words.TryGetValue(word, out var kind))
            {
                return NetworkText.BadStartTls(word);
            }

            preamble = kind;
        }

        return Text(await NetworkChecks.TlsAsync(Access, Budget, host, port, Optional(arguments, ServerNameArgument), preamble, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary><c>net_certs(store?, location?, filter?, expiring_within_days?, limit?)</c> (2026-10-15): a certificate store's certificates, nothing exported.</summary>
public sealed class NetCertsTool(NetworkAccess access, NetworkBudget budget) : NetworkTool(access, budget)
{
    public const string ToolName = "net_certs";
    public const string StoreArgument = "store";
    public const string LocationArgument = "location";
    public const string FilterArgument = "filter";
    public const string ExpiringArgument = "expiring_within_days";
    public const string LimitArgument = "limit";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "store": { "type": "string", "description": "my (default), root, ca, trustedpeople, trustedpublisher, authroot, disallowed or webhosting." },
            "location": { "type": "string", "description": "user or machine. The default is user, but on a Mac root defaults to machine, the system's trust." },
            "filter": { "type": "string", "description": "Only certificates whose subject, issuer, a name or the thumbprint contains this text." },
            "expiring_within_days": { "type": "integer", "description": "Only certificates that end within this many days (expired ones too)." },
            "limit": { "type": "integer", "description": "The most to list, 1 to 500 (default 50)." }
          }
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Lists the certificates in one of this machine's certificate stores: subject, issuer, end date and whether a private key is there; nothing is exported.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string storeWord = Optional(arguments, StoreArgument) ?? "my";
        if (CertificateStores.StoreName(storeWord) is not { } store)
        {
            return ValueTask.FromResult<object?>(NetworkText.BadStore(storeWord));
        }

        var location = CertificateStores.DefaultLocation(store);   // none named: the user's, a Mac's Root the machine's (2026-10-18)
        if (Optional(arguments, LocationArgument) is { } locationWord)
        {
            if (CertificateStores.Location(locationWord) is not { } named)
            {
                return ValueTask.FromResult<object?>(NetworkText.BadLocation(locationWord));
            }

            location = named;
        }

        int? expiring = null;
        if (arguments.TryGetValue(ExpiringArgument, out object? sent) && sent is not null && !(sent is JsonElement { ValueKind: JsonValueKind.Null }))
        {
            var (days, daysError) = Number(arguments, ExpiringArgument, 0, 36_500, 0);
            if (daysError is not null)
            {
                return ValueTask.FromResult<object?>(daysError);
            }

            expiring = days;
        }

        var (limit, limitError) = Number(arguments, LimitArgument, 1, NetworkChecks.MaxCertLimit, NetworkChecks.DefaultCertLimit);
        if (limitError is not null)
        {
            return ValueTask.FromResult<object?>(limitError);
        }

        return ValueTask.FromResult<object?>(Text(NetworkChecks.Certificates(Access, store, location, Optional(arguments, FilterArgument), expiring, limit)));
    }
}
