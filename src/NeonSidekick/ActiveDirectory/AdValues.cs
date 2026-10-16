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

    /// <summary>A FILETIME as a moment; null for 0 or the largest value (never).</summary>
    public static DateTimeOffset? FileTime(long value) =>
        value <= 0 || value == Never ? null : value > DateTime.MaxValue.ToFileTimeUtc() ? null : DateTimeOffset.FromFileTime(value).ToUniversalTime();

    /// <summary>A FILETIME attribute's text as a moment.</summary>
    public static DateTimeOffset? FileTime(string? text) =>
        text is not null && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) ? FileTime(value) : null;

    /// <summary>A moment as a FILETIME, for a filter's cutoff.</summary>
    public static long ToFileTime(DateTimeOffset moment) => moment.UtcDateTime.ToFileTimeUtc();

    /// <summary>GeneralizedTime (<c>20261015123456.0Z</c>) as a moment; null when it is not one.</summary>
    public static DateTimeOffset? GeneralizedTime(string? text)
    {
        if (text is null || text.Length < 14)
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(text[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment)
            ? moment
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

        var text = new StringBuilder("S-1-").Append(authority.ToString(CultureInfo.InvariantCulture));
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

    /// <summary>The DN's first RDN's value: <c>CN=Jane Doe,OU=…</c> → <c>Jane Doe</c>.</summary>
    public static string FirstName(string dn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        int eq = dn.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            return dn;
        }

        var name = new StringBuilder();
        for (int i = eq + 1; i < dn.Length; i++)
        {
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

        return name.ToString();
    }

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
