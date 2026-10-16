using System.Globalization;
using System.Text;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// LDAP filters built, never written by the model (2026-10-15, the admin-tools plan): every value a user or the model gives is escaped by
/// RFC 4515 (<c>*</c> <c>(</c> <c>)</c> <c>\</c> and NUL as <c>\2a</c>, <c>\28</c>, <c>\29</c>, <c>\5c</c>, <c>\00</c>; every byte past
/// ASCII as its hex too), so there is no filter injection; the builders below are the only filters the directory tools send. Pure.
/// </summary>
public static class LdapFilter
{
    /// <summary>A value escaped for an equality or substring term.</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = new StringBuilder(value.Length + 8);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            if (b is (byte)'*' or (byte)'(' or (byte)')' or (byte)'\\' or 0 || b >= 0x80)
            {
                text.Append('\\').Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append((char)b);
            }
        }

        return text.ToString();
    }

    /// <summary>Raw bytes (a SID, a GUID) as a filter value: every byte as <c>\xx</c>.</summary>
    public static string EscapeBytes(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length * 3);
        foreach (byte b in bytes)
        {
            text.Append('\\').Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    public const string Users = "(objectCategory=person)(objectClass=user)";
    public const string Computers = "(objectCategory=computer)";
    public const string Groups = "(objectCategory=group)";

    /// <summary>The user a name means: sAMAccountName, UPN, displayName, cn or mail equal to it.</summary>
    public static string UserByName(string name) => "(&" + Users + AnyName(name) + ")";

    public static string GroupByName(string name) => "(&" + Groups + "(|(sAMAccountName=" + Escape(name) + ")(cn=" + Escape(name) + ")(displayName=" + Escape(name) + ")(mail=" + Escape(name) + ")))";

    /// <summary>A computer by its name: the account (with or without its <c>$</c>), the cn, or the DNS name.</summary>
    public static string ComputerByName(string name)
    {
        string bare = name.TrimEnd('$');
        return "(&" + Computers + "(|(sAMAccountName=" + Escape(bare + "$") + ")(cn=" + Escape(bare) + ")(dNSHostName=" + Escape(name) + ")))";
    }

    private static string AnyName(string name)
    {
        string v = Escape(name);
        return "(|(sAMAccountName=" + v + ")(userPrincipalName=" + v + ")(displayName=" + v + ")(cn=" + v + ")(mail=" + v + "))";
    }

    /// <summary>Anything whose names hold the words: <c>*words*</c>, the words escaped.</summary>
    public static string NameContains(string words)
    {
        string v = "*" + Escape(words.Trim()) + "*";
        return "(&(|" + "(&" + Users + ")" + Groups + Computers + ")(|(sAMAccountName=" + v + ")(displayName=" + v + ")(cn=" + v + ")(mail=" + v + ")(userPrincipalName=" + v + ")))";
    }

    /// <summary>The nested members of a group: the in-chain rule (<c>1.2.840.113556.1.4.1941</c>, Samba 4.2+).</summary>
    public static string NestedMembersOf(string groupDn) => "(memberOf:1.2.840.113556.1.4.1941:=" + Escape(groupDn) + ")";

    /// <summary>Objects with any of <paramref name="sids"/>: one <c>objectSid</c> term each.</summary>
    public static string BySids(IEnumerable<byte[]> sids) => "(|" + string.Concat(sids.Select(s => "(objectSid=" + EscapeBytes(s) + ")")) + ")";

    /// <summary>Locked users, by the stored attribute (the constructed ones cannot be filtered on): <c>lockoutTime&gt;=1</c>.</summary>
    public const string LockedCandidates = "(&" + Users + "(lockoutTime>=1))";

    /// <summary>Enabled users whose password can expire and has been set: the candidates for <c>password_expiring</c>.</summary>
    public const string ExpiringCandidates =
        "(&" + Users + "(!(userAccountControl:1.2.840.113556.1.4.803:=65536))(!(userAccountControl:1.2.840.113556.1.4.803:=2))(pwdLastSet>=1))";

    public const string DisabledUsers = "(&" + Users + "(userAccountControl:1.2.840.113556.1.4.803:=2))";

    /// <summary>Users or computers not signed in since the cutoff (a FILETIME), by the replicated <c>lastLogonTimestamp</c>.</summary>
    public static string StaleSince(bool computers, long cutoffFileTime) =>
        "(&" + (computers ? Computers : Users) + "(lastLogonTimestamp<=" + cutoffFileTime.ToString(CultureInfo.InvariantCulture) + "))";

    /// <summary>Users or computers never signed in (no <c>lastLogonTimestamp</c>).</summary>
    public static string NeverSignedIn(bool computers) => "(&" + (computers ? Computers : Users) + "(!(lastLogonTimestamp=*)))";

    /// <summary>The domain controllers: computers with SERVER_TRUST_ACCOUNT (8192).</summary>
    public const string DomainControllers = "(&" + Computers + "(userAccountControl:1.2.840.113556.1.4.803:=8192))";
}
