namespace NeonSidekick.Network;

/// <summary>
/// One <c>DNSServiceQueryRecord</c> lookup's callbacks read into a <see cref="DnsOutcome"/> (2026-11-03, a VPN domain's report: on a Mac the
/// app's own client asked <c>/etc/resolv.conf</c>'s servers, which a VPN's per-domain resolvers are not among, so a domain's DCs were never found).
/// Pure, so every OS tests it; <see cref="MacDnsService"/> feeds it. What a ctypes probe of mDNSResponder showed (macOS 15, a VPN up), with
/// <see cref="ReturnIntermediates"/> | <see cref="TimeoutFlag"/>:
/// <list type="bullet">
/// <item>an answer's records come as one batch, <see cref="MoreComing"/> on each but the last, <see cref="AddFlag"/> on all, the RDATA written out whole;</item>
/// <item>a CNAME comes in the same batch as its target's records;</item>
/// <item>a name that does not exist and a name without the asked type both come as one <see cref="NoSuchRecord"/>: macOS does not tell
/// NXDOMAIN from no data there, so both read as no records (<see cref="NoSuchName"/>, when it does come, is NXDOMAIN).</item>
/// </list>
/// A lookup is done at the end of a batch holding a record of the asked type (a CNAME's for a CNAME question), a negative answer, or an
/// error; a batch of CNAMEs alone waits for the target's. A removal (no error, no Add) changes nothing: the lookup is a snapshot.
/// </summary>
public sealed class DnsServiceReplies
{
    /// <summary><c>kDNSServiceFlagsMoreComing</c>: more callbacks are queued behind this one.</summary>
    public const uint MoreComing = 0x1;

    /// <summary><c>kDNSServiceFlagsAdd</c>: the record is there (clear: it went away).</summary>
    public const uint AddFlag = 0x2;

    /// <summary><c>kDNSServiceFlagsReturnIntermediates</c>: negative answers and CNAMEs are reported too.</summary>
    public const uint ReturnIntermediates = 0x1000;

    /// <summary><c>kDNSServiceFlagsTimeout</c>: the daemon gives up with <see cref="Timeout"/> rather than waiting for ever.</summary>
    public const uint TimeoutFlag = 0x10000;

    public const int NoError = 0;
    public const int NoSuchName = -65538;
    public const int NoSuchRecord = -65554;
    public const int ServiceNotRunning = -65563;
    public const int Timeout = -65568;
    public const int DefunctConnection = -65569;

    private readonly string _name;
    private readonly ushort _type;
    private readonly List<DnsRecord> _records = [];
    private bool _batchAnswers;
    private int? _negative;
    private int? _error;

    /// <param name="name">The name asked.</param>
    /// <param name="type">The type asked.</param>
    public DnsServiceReplies(string name, ushort type)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _type = type;
    }

    /// <summary>Whether the lookup has its answer (or its error).</summary>
    public bool Done { get; private set; }

    /// <summary>One callback: its flags, error code, record name, type, RDATA and TTL.</summary>
    public void Add(uint flags, int error, string fullname, ushort rrtype, ReadOnlySpan<byte> rdata, uint ttl)
    {
        ArgumentNullException.ThrowIfNull(fullname);
        if (Done)
        {
            return;
        }

        switch (error)
        {
            case NoError when (flags & AddFlag) != 0:
                try
                {
                    // The owner without its final dot, as the app's own client reads names (the root stays ".").
                    string owner = fullname.Length > 1 ? fullname.TrimEnd('.') : fullname;
                    _records.Add(new DnsRecord(owner, rrtype, ttl, DnsWire.RecordData(rdata, rrtype)));
                    _batchAnswers |= rrtype == _type;
                }
                catch (DnsFormatException)
                {
                    // A record whose data does not read is left out, as a malformed reply's is.
                }

                break;
            case NoError:
                // A removal: the lookup is a snapshot.
                break;
            case NoSuchRecord or NoSuchName:
                _negative ??= error;
                break;
            default:
                _error = error;
                Done = true;
                return;
        }

        if ((flags & MoreComing) == 0 && (_batchAnswers || _negative is not null))
        {
            Done = true;
        }
    }

    /// <summary>
    /// The lookup as the app's client would give it: the records as a reply's answers; <see cref="NoSuchName"/> as NXDOMAIN; no records at all
    /// as an empty answer; <see cref="Timeout"/>, or no answer by the time the caller stopped waiting, as <see cref="NetworkText.DnsTookTooLong"/>;
    /// any other error by its code.
    /// </summary>
    public DnsOutcome Outcome()
    {
        if (_error is { } error)
        {
            return new DnsOutcome(null, null, error == Timeout ? NetworkText.DnsTookTooLong : NetworkText.DnsServiceFailed(error), Resolver: NetworkText.SystemResolver);
        }

        if (!Done)
        {
            return new DnsOutcome(null, null, NetworkText.DnsTookTooLong, Resolver: NetworkText.SystemResolver);
        }

        int rcode = _records.Count == 0 && _negative == NoSuchName ? 3 : 0;
        var reply = new DnsMessage(0, IsResponse: true, Truncated: false, rcode, _name, _type, DnsWire.ClassIn, [.. _records]);
        return new DnsOutcome(reply, null, null, Resolver: NetworkText.SystemResolver);
    }
}
