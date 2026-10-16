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
        "msDS-ResultantPSO", "memberOf",
        "telephoneNumber", "mobile", "homePhone", "ipPhone", "facsimileTelephoneNumber", "pager", "streetAddress", "l", "st", "postalCode",
        "co", "physicalDeliveryOfficeName", "employeeID", "employeeNumber", "homeDirectory", "homeDrive", "scriptPath",
    ];

    public static readonly IReadOnlyList<string> Group =
    [
        "sAMAccountName", "cn", "displayName", "description", "mail", "groupType", "managedBy", "whenCreated", "objectSid", "memberOf",
    ];

    public static readonly IReadOnlyList<string> Computer =
    [
        "sAMAccountName", "cn", "dNSHostName", "operatingSystem", "operatingSystemVersion", "operatingSystemServicePack", "userAccountControl",
        "lastLogonTimestamp", "description", "managedBy", "whenCreated", "location", "pwdLastSet",
    ];

    public static readonly IReadOnlyList<string> Find =
    [
        "sAMAccountName", "userPrincipalName", "displayName", "cn", "objectClass", "userAccountControl", "lockoutTime", "pwdLastSet",
        "msDS-UserPasswordExpiryTimeComputed", "lastLogonTimestamp", "dNSHostName", "operatingSystem", "msDS-ResultantPSO",
    ];

    public static readonly IReadOnlyList<string> DomainHead =
    [
        "name", "maxPwdAge", "minPwdAge", "minPwdLength", "pwdHistoryLength", "pwdProperties", "lockoutThreshold", "lockoutDuration",
        "lockOutObservationWindow", "msDS-Behavior-Version", "fSMORoleOwner", "objectSid",
    ];

    public static readonly IReadOnlyList<string> PasswordSettings =
    [
        "name", "msDS-MaximumPasswordAge", "msDS-MinimumPasswordLength", "msDS-LockoutThreshold", "msDS-LockoutDuration",
        "msDS-LockoutObservationWindow", "msDS-PasswordSettingsPrecedence",
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
    };

    /// <summary>Whether a returned attribute's name (its options stripped) is never read.</summary>
    public static bool NeverReadName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int semi = name.IndexOf(';', StringComparison.Ordinal);
        return NeverRead.Contains(semi < 0 ? name : name[..semi]);
    }
}
