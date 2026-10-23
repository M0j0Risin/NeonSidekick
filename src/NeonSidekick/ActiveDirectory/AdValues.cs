using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NeonSidekick.ActiveDirectory;

/// <summary>A length of time read from the directory: a span, forever (lockout until an admin unlocks), or never (a password that does not age).</summary>
public readonly record struct AdInterval(TimeSpan? Span)
{
    /// <summary>Forever or never: no end.</summary>
    public bool Endless => Span is null;
}

/// <summary>
/// The directory's values in words (2026-10-15, the admin-tools plan): FILETIMEs (0 and the largest value mean never), GeneralizedTime,
/// SIDs, GUIDs, <c>userAccountControl</c>'s flags, and the policy intervals — stored as negative 100-ns counts (Microsoft's Lockout-Duration page
/// says "the negative of the number of 100-nanosecond intervals"), though no page names a "never" value, so <see cref="Interval"/> is tolerant:
/// a negative value is its negation, 0 and <c>0x8000000000000000</c> are endless, a positive value is taken as written. Pure.
/// </summary>
public static class AdValues
{
    public const long Never = 0x7FFFFFFFFFFFFFFF;
    public const long NegativeNever = unchecked((long)0x8000000000000000);

    public const int AccountDisable = 0x2;
    public const int Lockout = 0x10;
    public const int PasswordNotRequired = 0x20;
    public const int NormalAccount = 0x200;
    public const int ServerTrustAccount = 0x2000;
    public const int DontExpirePassword = 0x10000;
    public const int SmartcardRequired = 0x40000;
    public const int TrustedForDelegation = 0x80000;
    public const int PasswordExpired = 0x800000;

    /// <summary>A read-only DC's account (PARTIAL_SECRETS_ACCOUNT, 2026-10-23, G9).</summary>
    public const int PartialSecretsAccount = 0x4000000;

    /// <summary>The trust accounts' bits: interdomain, workstation, server (2026-10-23, H3: no password change at sign-in for them).</summary>
    public const int TrustAccounts = 0x800 | 0x1000 | 0x2000;

    /// <summary>
    /// A FILETIME as a moment; null for 0 or the largest value (never). Read as UTC (2026-10-23, the admin review's P7):
    /// <c>DateTimeOffset.FromFileTime</c> goes through the local zone and threw near the top of the range east of UTC, though the guard passed.
    /// </summary>
    public static DateTimeOffset? FileTime(long value) =>
        value <= 0 || value == Never ? null : value > DateTime.MaxValue.ToFileTimeUtc() ? null : new DateTimeOffset(DateTime.FromFileTimeUtc(value), TimeSpan.Zero);

    /// <summary>
    /// <paramref name="at"/> plus <paramref name="span"/>, or null past the last representable moment (2026-10-23, the admin review's A8: a
    /// policy interval near "never" overflowed the addition, and the ArgumentOutOfRangeException failed the whole answer).
    /// </summary>
    public static DateTimeOffset? Plus(DateTimeOffset at, TimeSpan span) =>
        span.Ticks > DateTimeOffset.MaxValue.UtcTicks - at.UtcTicks || span.Ticks < DateTimeOffset.MinValue.UtcTicks - at.UtcTicks ? null : at + span;

    /// <summary>A FILETIME attribute's text as a moment.</summary>
    public static DateTimeOffset? FileTime(string? text) =>
        text is not null && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) ? FileTime(value) : null;

    /// <summary>A moment as a FILETIME, for a filter's cutoff.</summary>
    public static long ToFileTime(DateTimeOffset moment) => moment.UtcDateTime.ToFileTimeUtc();

    /// <summary>
    /// GeneralizedTime (<c>20261015123456.0Z</c>) as a moment; null when it is not one. RFC 4517's forms (2026-10-23, the admin review's P9: the
    /// first 14 characters were read as UTC, an offset ignored and the shorter forms refused): the hour, then optional minutes and seconds, an
    /// optional fraction, then <c>Z</c> or an offset (<c>+0200</c>, <c>-05</c>); no zone is read as UTC, as AD always writes one.
    /// </summary>
    public static DateTimeOffset? GeneralizedTime(string? text)
    {
        if (text is null)
        {
            return null;
        }

        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        if (digits is not (10 or 12 or 14))
        {
            return null;
        }

        int at = digits;
        if (at < text.Length && text[at] is '.' or ',')
        {
            at++;
            while (at < text.Length && char.IsAsciiDigit(text[at]))
            {
                at++;
            }
        }

        var offset = TimeSpan.Zero;
        string zone = text[at..];
        if (zone.Length > 0 && zone != "Z")
        {
            if (zone.Length is not (3 or 5) || zone[0] is not ('+' or '-') || !zone[1..].All(char.IsAsciiDigit))
            {
                return null;
            }

            int hours = int.Parse(zone[1..3], CultureInfo.InvariantCulture);
            int minutes = zone.Length == 5 ? int.Parse(zone[3..5], CultureInfo.InvariantCulture) : 0;
            if (hours > 14 || minutes > 59)
            {
                return null;
            }

            offset = new TimeSpan(hours, minutes, 0) * (zone[0] == '-' ? -1 : 1);
        }

        string stamp = text[..digits].PadRight(14, '0');
        return DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, offset).ToUniversalTime()
            : null;
    }

    /// <summary>A policy interval read tolerantly (see the class); null for text that is no number.</summary>
    public static AdInterval? Interval(string? text)
    {
        if (text is null || !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
        {
            return null;
        }

        return Interval(value);
    }

    public static AdInterval Interval(long value) => value switch
    {
        0 or NegativeNever => new AdInterval(null),
        < 0 => new AdInterval(TimeSpan.FromTicks(-value)),
        _ => new AdInterval(TimeSpan.FromTicks(value)),
    };

    /// <summary>A binary SID as <c>S-1-5-21-…</c>; null when it is not one.</summary>
    public static string? Sid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 1)
        {
            return null;
        }

        int count = bytes[1];
        if (bytes.Length < 8 + (4 * count))
        {
            return null;
        }

        long authority = 0;
        for (int i = 2; i < 8; i++)
        {
            authority = (authority << 8) | bytes[i];
        }

        // An authority of 2^32 or more is written in hex, as Windows writes it (2026-10-23, the admin review's V9), so the text round-trips.
        var text = new StringBuilder("S-1-").Append(authority >= 1L << 32 ? "0x" + authority.ToString("X12", CultureInfo.InvariantCulture) : authority.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < count; i++)
        {
            text.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(8 + (4 * i))..]).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>A binary GUID in its usual spelling.</summary>
    public static string? Guid(ReadOnlySpan<byte> bytes) => bytes.Length == 16 ? new Guid(bytes).ToString() : null;

    /// <summary>An integer attribute's value; null when absent or not a number.</summary>
    public static long? Integer(string? text) =>
        text is not null && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) ? value : null;

    /// <summary><c>userAccountControl</c>'s flags that matter, in words.</summary>
    public static IReadOnlyList<string> Flags(int uac)
    {
        var words = new List<string>(6);
        if ((uac & AccountDisable) != 0)
        {
            words.Add("disabled");
        }

        if ((uac & DontExpirePassword) != 0)
        {
            words.Add("password never expires");
        }

        if ((uac & PasswordNotRequired) != 0)
        {
            words.Add("password not required");
        }

        if ((uac & SmartcardRequired) != 0)
        {
            words.Add("smart card required");
        }

        if ((uac & TrustedForDelegation) != 0)
        {
            words.Add("trusted for delegation");
        }

        if ((uac & ServerTrustAccount) != 0)
        {
            words.Add("domain controller");
        }

        return words;
    }

    /// <summary>A domain's or forest's functional level as Windows names it.</summary>
    public static string FunctionalLevel(long? level) => level switch
    {
        0 => "Windows 2000",
        1 => "Windows Server 2003 interim",
        2 => "Windows Server 2003",
        3 => "Windows Server 2008",
        4 => "Windows Server 2008 R2",
        5 => "Windows Server 2012",
        6 => "Windows Server 2012 R2",
        7 => "Windows Server 2016",
        10 => "Windows Server 2025",
        { } other => "level " + other.ToString(CultureInfo.InvariantCulture),
        null => "unknown",
    };

    /// <summary>
    /// The DN's first RDN's value: <c>CN=Jane Doe,OU=…</c> → <c>Jane Doe</c>. RFC 4514's two escapes are read: a <c>\</c> before a character
    /// (<c>\,</c>), and a hex pair (<c>\2C</c> for a comma, <c>\C3\A9</c> for é as UTF-8 bytes; 2026-10-15, the AD gaps plan: copied through as
    /// <c>2C</c> until then).
    /// </summary>
    public static string FirstName(string dn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        int eq = dn.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            return dn;
        }

        var name = new StringBuilder();
        var bytes = new List<byte>();
        for (int i = eq + 1; i < dn.Length; i++)
        {
            if (dn[i] == '\\' && IsHexPair(dn, i + 1))
            {
                bytes.Add(Convert.ToByte(dn.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }

            Flush(name, bytes);
            if (dn[i] == '\\' && i + 1 < dn.Length)
            {
                name.Append(dn[++i]);
                continue;
            }

            if (dn[i] == ',')
            {
                break;
            }

            name.Append(dn[i]);
        }

        Flush(name, bytes);
        return name.ToString();
    }

    private static bool IsHexPair(string text, int at) => at + 1 < text.Length && Uri.IsHexDigit(text[at]) && Uri.IsHexDigit(text[at + 1]);

    private static void Flush(StringBuilder name, List<byte> bytes)
    {
        if (bytes.Count > 0)
        {
            name.Append(Encoding.UTF8.GetString([.. bytes]));
            bytes.Clear();
        }
    }

    /// <summary>
    /// A DN under <paramref name="baseDn"/> as its path, as <c>canonicalName</c> spells it without the domain: <c>OU=Engineering,OU=Neon,DC=…</c> →
    /// <c>Neon/Engineering</c>, each name's <c>/</c> and <c>\</c> escaped with a <c>\</c> (canonicalName's own escaping, Microsoft's TechNet wiki
    /// "Active Directory: Characters to Escape"; 2026-10-15, the AD gaps plan), so a path worked out here and a computed one compare equal; ""
    /// for the base itself. Pure.
    /// </summary>
    public static string CanonicalPath(string dn, string baseDn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        ArgumentNullException.ThrowIfNull(baseDn);
        var names = new List<string>();
        string rest = NormalDn(dn);
        string top = NormalDn(baseDn);   // V8: compared as the server writes a DN
        for (int guard = 0; rest.Length > 0 && !string.Equals(rest, top, StringComparison.OrdinalIgnoreCase) && guard < 64; guard++)
        {
            names.Add(FirstName(rest).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("/", "\\/", StringComparison.Ordinal));
            rest = Parent(rest);
        }

        names.Reverse();
        return string.Join('/', names);
    }

    /// <summary>A computed <c>canonicalName</c> (<c>lab.neon.test/Neon/Engineering</c>) as its path in the domain (<c>Neon/Engineering</c>). Pure.</summary>
    public static string PathOfCanonical(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        int slash = canonical.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? "" : canonical[(slash + 1)..].TrimEnd('/');
    }

    /// <summary>
    /// A path's parts split on its unescaped <c>/</c>, each unescaped (<c>\/</c> → <c>/</c>, <c>\\</c> → <c>\</c>): <c>Neon/Sales\/EMEA</c> →
    /// <c>Neon</c>, <c>Sales/EMEA</c>. Pure.
    /// </summary>
    public static IReadOnlyList<string> PathParts(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = new List<string>();
        var part = new StringBuilder();
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\' && i + 1 < path.Length)
            {
                part.Append(path[++i]);
            }
            else if (path[i] == '/')
            {
                parts.Add(part.ToString());
                part.Clear();
            }
            else
            {
                part.Append(path[i]);
            }
        }

        parts.Add(part.ToString());
        return parts.Where(p => p.Length > 0).ToList();
    }

    /// <summary>
    /// <c>groupType</c> in words (2026-10-15, the AD gaps plan): security or distribution (0x80000000), its scope — global (0x2), domain local
    /// (0x4), universal (0x8) — and builtin (0x1, a system group). Pure.
    /// </summary>
    public static string GroupKind(long groupType)
    {
        string kind = (groupType & 0x80000000L) != 0 ? "security" : "distribution";
        string scope = (groupType & 0x8) != 0 ? "universal" : (groupType & 0x4) != 0 ? "domain local" : (groupType & 0x2) != 0 ? "global" : "";
        return kind + (scope.Length > 0 ? " " + scope : "") + ((groupType & 0x1) != 0 ? ", builtin" : "");
    }

    /// <summary>A binary SID's RID, its last sub-authority (a group's 513 for Domain Users); null when it is not one. Pure.</summary>
    public static uint? Rid(ReadOnlySpan<byte> sid)
    {
        if (sid.Length < 8 || sid[0] != 1 || sid[1] == 0 || sid.Length < 8 + (4 * sid[1]))
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(sid[(8 + (4 * (sid[1] - 1)))..]);
    }

    /// <summary>A SID's text (<c>S-1-5-32-544</c>) as its binary form, for an <c>objectSid</c> term; null when it is not one. Pure.</summary>
    public static byte[]? SidBytes(string sid)
    {
        ArgumentNullException.ThrowIfNull(sid);
        var parts = sid.Split('-');
        // The authority in decimal or as 0x hex, and within its 48 bits (V9: its high bits were dropped silently, a search for the wrong SID).
        bool hex = parts.Length >= 3 && parts[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (parts.Length < 3 || parts[0] != "S" || parts[1] != "1" || parts.Length - 3 > 15
            || !long.TryParse(hex ? parts[2][2..] : parts[2], hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out long authority)
            || authority < 0 || authority >= 1L << 48)
        {
            return null;
        }

        var bytes = new byte[8 + (4 * (parts.Length - 3))];
        bytes[0] = 1;
        bytes[1] = (byte)(parts.Length - 3);
        for (int i = 0; i < 6; i++)
        {
            bytes[2 + i] = (byte)(authority >> (8 * (5 - i)));
        }

        for (int i = 3; i < parts.Length; i++)
        {
            if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out uint sub))
            {
                return null;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (4 * (i - 3))), sub);
        }

        return bytes;
    }

    /// <summary>A moment as GeneralizedTime for a filter (<c>20261015123456.0Z</c>). Pure.</summary>
    public static string ToGeneralizedTime(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".0Z";

    /// <summary>
    /// <c>gPLink</c>'s links (2026-10-15, the AD gaps plan): one string of <c>[LDAP://cn={GUID},cn=policies,…;flags]</c>, its flags 1 disabled and
    /// 2 enforced; the count of links, of disabled and of enforced. Zeros for none. Pure.
    /// </summary>
    public static (int Links, int Disabled, int Enforced) GpLinks(string? gpLink)
    {
        if (string.IsNullOrWhiteSpace(gpLink))
        {
            return (0, 0, 0);
        }

        int links = 0, disabled = 0, enforced = 0;
        int at = 0;
        while ((at = gpLink.IndexOf('[', at)) >= 0)
        {
            int close = gpLink.IndexOf(']', at);
            if (close < 0)
            {
                break;
            }

            string link = gpLink[(at + 1)..close];
            if (link.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase))
            {
                links++;
                int semi = link.LastIndexOf(';');
                if (semi >= 0 && int.TryParse(link[(semi + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int flags))
                {
                    disabled += (flags & 1) != 0 ? 1 : 0;
                    enforced += (flags & 2) != 0 ? 1 : 0;
                }
            }

            at = close + 1;
        }

        return (links, disabled, enforced);
    }

    public const string KindServiceAccount = "service account";

    /// <summary>The service accounts' classes: group managed, standalone managed, and Windows Server 2025's delegated one (Akamai's dMSA research).</summary>
    public static readonly IReadOnlyList<string> ServiceAccountClasses = ["msDS-GroupManagedServiceAccount", "msDS-ManagedServiceAccount", "msDS-DelegatedManagedServiceAccount"];

    /// <summary>
    /// An object's kind by its <c>objectClass</c> chain (2026-10-15, the AD gaps plan). The order matters, since the chain holds every class
    /// above: a gMSA's is top, person, organizationalPerson, user, computer, msDS-GroupManagedServiceAccount, so the service-account classes come
    /// first, then computer (whose chain holds user), group, contact (a person but no user), a foreign security principal, an OU or container;
    /// user last. Pure.
    /// </summary>
    public static string KindOf(IReadOnlyList<string> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);
        bool Has(string c) => classes.Contains(c, StringComparer.OrdinalIgnoreCase);
        return ServiceAccountClasses.Any(Has) ? KindServiceAccount
            : Has("computer") ? "computer"
            : Has("group") ? "group"
            : Has("contact") ? "contact"
            : Has("foreignSecurityPrincipal") ? "foreign"
            : Has("organizationalUnit") ? "ou"
            : Has("container") || Has("builtinDomain") ? "container"
            : Has("user") || Has("person") ? "user"
            : ObjectKind;
    }

    /// <summary>
    /// The kind of an object that is none of the others (2026-10-23, the admin review's V2: lostAndFound, quotas, TPM devices and print queues
    /// were "user", openable, and opened as "no such user"); never opened.
    /// </summary>
    public const string ObjectKind = "object";

    /// <summary>
    /// A DN with the spaces around its <c>,</c> and <c>=</c> taken out (an escaped one kept), for comparing DNs as the server writes them with one
    /// a person typed (2026-10-23, the admin review's V8: <c>OU=Neon, DC=lab</c> never matched the server's <c>OU=Neon,DC=lab</c>). Pure.
    /// </summary>
    public static string NormalDn(string dn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        var text = new StringBuilder(dn.Length);
        for (int i = 0; i < dn.Length; i++)
        {
            char c = dn[i];
            if (c == '\\' && i + 1 < dn.Length)
            {
                text.Append(c).Append(dn[++i]);
                continue;
            }

            if (c is ',' or '=')
            {
                while (text.Length > 0 && text[^1] == ' ' && (text.Length < 2 || text[^2] != '\\'))
                {
                    text.Length--;
                }

                text.Append(c);
                while (i + 1 < dn.Length && dn[i + 1] == ' ')
                {
                    i++;
                }

                continue;
            }

            text.Append(c);
        }

        return text.ToString().Trim();
    }

    /// <summary>A DN's domain part: its trailing run of <c>DC=</c> names, normalized (Q9). Pure.</summary>
    public static string DomainPart(string dn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        string rest = NormalDn(dn);
        for (int guard = 0; guard < 64 && rest.Length > 0 && !rest.StartsWith("DC=", StringComparison.OrdinalIgnoreCase); guard++)
        {
            rest = Parent(rest);
        }

        return rest;
    }

    /// <summary>
    /// Whether <paramref name="dn"/> is in <paramref name="domainDn"/>'s domain: their domain parts the same (2026-10-23, the admin review's Q9: a
    /// child domain's <c>DC=child,DC=corp,DC=com</c> ends in its parent's and was judged the same). Pure.
    /// </summary>
    public static bool SameDomain(string dn, string domainDn) => string.Equals(DomainPart(dn), DomainPart(domainDn), StringComparison.OrdinalIgnoreCase);

    /// <summary>The DN of the container holding <paramref name="dn"/> (its parent); "" for a root.</summary>
    public static string Parent(string dn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        for (int i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\')
            {
                i++;
                continue;
            }

            if (dn[i] == ',')
            {
                return dn[(i + 1)..];
            }
        }

        return "";
    }

    /// <summary>A domain's DNS name as its naming context: <c>corp.example</c> → <c>DC=corp,DC=example</c>.</summary>
    public static string DomainDn(string dnsName) => string.Join(',', dnsName.Trim().TrimEnd('.').Split('.').Select(part => "DC=" + part));

    /// <summary>A naming context as its DNS name: <c>DC=corp,DC=example</c> → <c>corp.example</c>.</summary>
    public static string DnsName(string domainDn) =>
        string.Join('.', domainDn.Split(',').Where(p => p.Trim().StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p.Trim()[3..]));
}
