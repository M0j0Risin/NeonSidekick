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

    /// <summary>
    /// The service accounts (2026-10-15, the AD gaps plan): group managed, standalone managed and Windows Server 2025's delegated one, by class,
    /// since <c>(objectCategory=computer)</c> does not match them (each has a category of its own).
    /// </summary>
    public const string ServiceAccounts = "(|(objectClass=msDS-GroupManagedServiceAccount)(objectClass=msDS-ManagedServiceAccount)(objectClass=msDS-DelegatedManagedServiceAccount))";

    public const string Contacts = "(objectClass=contact)";

    public const string Ous = "(objectClass=organizationalUnit)";

    /// <summary>The containers an OU path may name: an OU, a container (<c>CN=Users</c>), the builtin domain.</summary>
    public const string OuLike = "(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain))";

    /// <summary>
    /// A computer by its name: the account (with or without its <c>$</c>), the cn, or the DNS name. A service account matches too (2026-10-15, the
    /// AD gaps plan: <c>ad_computer svc_web$</c> finds a gMSA), by its own term here only: <see cref="Computers"/> stays machines for the rest.
    /// </summary>
    public static string ComputerByName(string name)
    {
        string bare = name.TrimEnd('$');
        return "(&(|" + Computers + ServiceAccounts + ")(|(sAMAccountName=" + Escape(bare + "$") + ")(cn=" + Escape(bare) + ")(dNSHostName=" + Escape(name) + ")))";
    }

    private static string AnyName(string name)
    {
        string v = Escape(name);
        return "(|(sAMAccountName=" + v + ")(userPrincipalName=" + v + ")(displayName=" + v + ")(cn=" + v + ")(mail=" + v + "))";
    }

    /// <summary>One object by its DN, inside a kind's term: what a clicked row opens (2026-10-15, the AD gaps plan).</summary>
    public static string ByDn(string kindTerm, string dn) => "(&" + kindTerm + "(distinguishedName=" + Escape(dn) + "))";

    /// <summary>Any of <paramref name="dns"/> (the members a walk resolves, 50 at a time).</summary>
    public static string ByDns(IEnumerable<string> dns) => "(|" + string.Concat(dns.Select(d => "(distinguishedName=" + Escape(d) + ")")) + ")";

    /// <summary>
    /// Anything whose names hold the words: <c>*words*</c>, the words escaped. The description too since 2026-10-15 (the AD gaps plan); users, groups
    /// and computers without a type, the one kind asked with one (<see cref="TypeTerm"/>).
    /// </summary>
    public static string NameContains(string words, string? type = null)
    {
        string v = "*" + Escape(words.Trim()) + "*";
        return type switch
        {
            "ou" => "(&" + Ous + "(|(ou=" + v + ")(name=" + v + ")(description=" + v + ")))",
            "contact" => "(&" + Contacts + "(|(cn=" + v + ")(displayName=" + v + ")(mail=" + v + ")(description=" + v + ")))",
            _ => "(&" + (type is null ? "(|(&" + Users + ")" + Groups + Computers + ")" : TypeTerm(type)) + "(|(sAMAccountName=" + v + ")(displayName=" + v + ")(cn=" + v + ")(mail=" + v + ")(userPrincipalName=" + v + ")(description=" + v + ")))",
        };
    }

    /// <summary>The term for one of <c>ad_find</c>'s types: user, group, computer, ou, contact, service.</summary>
    public static string TypeTerm(string type) => type switch
    {
        "user" => "(&" + Users + ")",
        "group" => Groups,
        "computer" => Computers,
        "ou" => Ous,
        "contact" => Contacts,
        "service" => ServiceAccounts,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not a type"),
    };

    /// <summary>Objects of <paramref name="type"/> whose <paramref name="attribute"/> holds the words.</summary>
    public static string FieldContains(string type, string attribute, string words) =>
        "(&" + TypeTerm(type) + "(" + attribute + "=*" + Escape(words.Trim()) + "*))";

    /// <summary>Computers whose operating system holds the words.</summary>
    public static string OsContains(string words) => "(&" + Computers + "(operatingSystem=*" + Escape(words.Trim()) + "*))";

    /// <summary>Users whose password never expires (UAC 65536).</summary>
    public const string PasswordNeverExpires = "(&" + Users + "(userAccountControl:1.2.840.113556.1.4.803:=65536))";

    /// <summary>Users whose account expires between two FILETIMEs (0 and the largest value never fall in a range).</summary>
    public static string AccountExpiresBetween(long from, long to) =>
        "(&" + Users + "(accountExpires>=" + from.ToString(CultureInfo.InvariantCulture) + ")(accountExpires<=" + to.ToString(CultureInfo.InvariantCulture) + "))";

    /// <summary>Objects of the types (users, groups and computers by default) made since a moment, as GeneralizedTime.</summary>
    public static string CreatedSince(string? type, DateTimeOffset since) =>
        "(&" + (type is null ? "(|(&" + Users + ")" + Groups + Computers + ")" : TypeTerm(type)) + "(whenCreated>=" + AdValues.ToGeneralizedTime(since) + "))";

    /// <summary>Groups with no member that are no system object: the candidates for <c>empty_groups</c> (someone's primary group is taken out after).</summary>
    public const string EmptyGroupCandidates = "(&" + Groups + "(!(member=*))(!(isCriticalSystemObject=TRUE)))";

    /// <summary>Objects (of the kind's term, none for any) whose primary group is one of <paramref name="rids"/>.</summary>
    public static string PrimaryGroupIn(string? kindTerm, IEnumerable<uint> rids)
    {
        string any = "(|" + string.Concat(rids.Select(r => "(primaryGroupID=" + r.ToString(CultureInfo.InvariantCulture) + ")")) + ")";
        return kindTerm is null ? any : "(&" + kindTerm + any + ")";
    }

    /// <summary>The objects once marked protected (<c>adminCount=1</c>).</summary>
    public const string AdminCount = "(adminCount=1)";

    /// <summary>An OU or container named <paramref name="name"/> (its <c>ou</c> or <c>cn</c>).</summary>
    public static string OuNamed(string name) => "(&" + OuLike + "(|(ou=" + Escape(name) + ")(cn=" + Escape(name) + ")))";

    /// <summary>The OU tree's OUs (every depth) and the top's containers: the advanced-view ones are hidden after, from the attribute read.</summary>
    public const string TreeOus = Ous;

    public const string TreeContainers = "(|(objectClass=container)(objectClass=builtinDomain))";

    /// <summary>
    /// A domain's fine-grained password policies: everything one level under the Password Settings Container, by presence — an ordinary account
    /// may not read a PSO's <c>objectClass</c>, and a term on an attribute it cannot read never matches (checked on Samba as <c>reader</c>,
    /// 2026-10-15: <c>(objectClass=msDS-PasswordSettings)</c> found nothing where <c>(objectClass=*)</c> listed ShortPasswords).
    /// </summary>
    public const string PasswordSettingsObjects = "(objectClass=*)";

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
