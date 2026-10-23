namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// The attributes the directory tools ever ask for (2026-10-15, the admin-tools plan): an allow-list per kind, never <c>*</c>; the personal
/// ones a <c>redacted</c> domain leaves out; and <see cref="NeverRead"/> — passwords, LAPS and BitLocker secrets, managed and supplemental
/// credentials, key material, photos — never requested and dropped if a server sends one anyway. Pure.
/// </summary>
public static class AdAttributes
{
    public static readonly IReadOnlyList<string> RootDse =
    [
        "defaultNamingContext", "configurationNamingContext", "schemaNamingContext", "rootDomainNamingContext", "dnsHostName", "serverName",
        "domainFunctionality", "forestFunctionality", "domainControllerFunctionality", "supportedControl", "currentTime",
    ];

    public static readonly IReadOnlyList<string> User =
    [
        "sAMAccountName", "userPrincipalName", "displayName", "cn", "mail", "userAccountControl", "msDS-User-Account-Control-Computed",
        "lockoutTime", "badPwdCount", "badPasswordTime", "pwdLastSet", "msDS-UserPasswordExpiryTimeComputed", "lastLogonTimestamp",
        "accountExpires", "manager", "title", "department", "company", "description", "whenCreated", "whenChanged", "objectSid",
        "msDS-ResultantPSO", "memberOf", "directReports", "primaryGroupID", "objectClass",
        "telephoneNumber", "mobile", "homePhone", "ipPhone", "facsimileTelephoneNumber", "pager", "streetAddress", "l", "st", "postalCode",
        "co", "physicalDeliveryOfficeName", "employeeID", "employeeNumber", "homeDirectory", "homeDrive", "scriptPath",
    ];

    public static readonly IReadOnlyList<string> Group =
    [
        "sAMAccountName", "cn", "displayName", "description", "mail", "groupType", "managedBy", "whenCreated", "objectSid", "memberOf", "objectClass",
    ];

    /// <summary>
    /// A group's member as a walk resolves it (2026-10-15, the AD gaps plan): its names and kind, and a group's SID (its RID finds the primary-group
    /// members) and type.
    /// </summary>
    public static readonly IReadOnlyList<string> Member =
    [
        "sAMAccountName", "displayName", "cn", "objectClass", "objectSid", "groupType",
    ];

    public static readonly IReadOnlyList<string> Computer =
    [
        "sAMAccountName", "cn", "dNSHostName", "operatingSystem", "operatingSystemVersion", "operatingSystemServicePack", "userAccountControl",
        "lastLogonTimestamp", "description", "managedBy", "whenCreated", "location", "pwdLastSet", "objectClass", "objectSid", "memberOf", "primaryGroupID",
    ];

    public static readonly IReadOnlyList<string> Find =
    [
        "sAMAccountName", "userPrincipalName", "displayName", "cn", "objectClass", "userAccountControl", "lockoutTime", "pwdLastSet",
        "msDS-UserPasswordExpiryTimeComputed", "lastLogonTimestamp", "dNSHostName", "operatingSystem", "msDS-ResultantPSO",
        "description", "groupType", "whenCreated", "accountExpires", "adminCount", "memberOf", "operatingSystemVersion", "objectSid", "mail",
    ];

    /// <summary>An OU's own (2026-10-15, the AD gaps plan): its names, path, description, owner, linked GPOs and whether the admin tools hide it.</summary>
    public static readonly IReadOnlyList<string> Ou =
    [
        "ou", "name", "cn", "description", "canonicalName", "showInAdvancedViewOnly", "managedBy", "gPLink", "objectClass", "whenCreated",
    ];

    /// <summary>What an OU holds, one level down: the names and the kind.</summary>
    public static readonly IReadOnlyList<string> OuContent =
    [
        "sAMAccountName", "displayName", "cn", "ou", "name", "objectClass", "description",
    ];

    /// <summary>What a clicked row's lookup reads: the class picks the card.</summary>
    public static readonly IReadOnlyList<string> Open =
    [
        "objectClass", "sAMAccountName", "cn",
    ];

    /// <summary>The fields <c>ad_find kind:field</c> searches, by its word.</summary>
    public static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["department"] = "department",
        ["title"] = "title",
        ["company"] = "company",
        ["office"] = "physicalDeliveryOfficeName",
        ["description"] = "description",
    };

    public static readonly IReadOnlyList<string> DomainHead =
    [
        "name", "maxPwdAge", "minPwdAge", "minPwdLength", "pwdHistoryLength", "pwdProperties", "lockoutThreshold", "lockoutDuration",
        "lockOutObservationWindow", "msDS-Behavior-Version", "fSMORoleOwner", "objectSid",
    ];

    public static readonly IReadOnlyList<string> PasswordSettings =
    [
        "name", "msDS-MaximumPasswordAge", "msDS-MinimumPasswordLength", "msDS-LockoutThreshold", "msDS-LockoutDuration",
        "msDS-LockoutObservationWindow", "msDS-PasswordSettingsPrecedence", "msDS-PSOAppliesTo",
    ];

    /// <summary>What a <c>redacted</c> domain leaves out of an answer: phones, addresses, employee numbers, home folders.</summary>
    public static readonly IReadOnlySet<string> Personal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "telephoneNumber", "mobile", "homePhone", "ipPhone", "facsimileTelephoneNumber", "pager", "streetAddress", "l", "st", "postalCode",
        "co", "physicalDeliveryOfficeName", "employeeID", "employeeNumber", "homeDirectory", "homeDrive", "scriptPath", "homePostalAddress",
    };

    /// <summary>Never requested, and dropped if returned. Pinned.</summary>
    public static readonly IReadOnlySet<string> NeverRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "unicodePwd", "userPassword", "unixUserPassword", "dBCSPwd", "lmPwdHistory", "ntPwdHistory", "supplementalCredentials",
        "ms-Mcs-AdmPwd", "ms-Mcs-AdmPwdExpirationTime", "msLAPS-Password", "msLAPS-EncryptedPassword", "msLAPS-EncryptedPasswordHistory",
        "msLAPS-EncryptedDSRMPassword", "msLAPS-EncryptedDSRMPasswordHistory", "msLAPS-PasswordExpirationTime",
        "msFVE-RecoveryPassword", "msFVE-KeyPackage", "msFVE-VolumeGuid", "msDS-ManagedPassword", "msDS-ManagedPasswordId",
        "msDS-ManagedPasswordPreviousId", "msDS-KeyCredentialLink", "msPKIAccountCredentials", "msPKIDPAPIMasterKeys", "msTPM-OwnerInformation",
        "msTPM-TpmInformationForComputer", "thumbnailPhoto", "jpegPhoto", "photo", "userCertificate", "userSMIMECertificate",

        // The trusts' and LSA secrets' keys and roaming credentials (2026-10-23, the admin review's V10).
        "trustAuthIncoming", "trustAuthOutgoing", "initialAuthIncoming", "initialAuthOutgoing", "currentValue", "priorValue",
        "msPKI-CredentialRoamingTokens",
    };

    /// <summary>Whether a returned attribute's name (its options stripped) is never read.</summary>
    public static bool NeverReadName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int semi = name.IndexOf(';', StringComparison.Ordinal);
        return NeverRead.Contains(semi < 0 ? name : name[..semi]);
    }
}
