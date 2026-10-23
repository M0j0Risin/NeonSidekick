using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// wldap32's read side, and nothing else (2026-10-15, the admin-tools plan): the directory tools are read-only by construction — no modify,
/// add, delete, rename or extended-operation entry point is ever declared here, and <c>ActiveDirectoryTests.LdapNative_ImportsNoWriteEntryPoint</c>
/// reads this file and pins that. Setup (<c>ldap_initW</c>, <c>ldap_sslinitW</c>, <c>ldap_set_optionW</c>, <c>ldap_connect</c>, a Negotiate
/// <c>ldap_bind_sW</c>), search (<c>ldap_search_ext_sW</c>, the paged search's init/next/abandon — abandon frees the client-side search, it writes
/// nothing — entries, DNs, attribute names and values), and the frees. The app's own <c>LibraryImport</c> layer, not
/// <c>System.DirectoryServices.Protocols</c>, whose marshalling is not AOT-annotated. A system library: no DLL ships. Excluded from coverage:
/// proven by the smoke's <c>ad:wldap32</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class LdapNative
{
    public const int Success = 0x00;
    public const int TimeLimitExceeded = 0x03;
    public const int SizeLimitExceeded = 0x04;
    public const int StrongAuthRequired = 0x08;
    public const int PartialResults = 0x09;
    public const int Referral = 0x0a;
    public const int NoSuchObject = 0x20;
    public const int InvalidCredentials = 0x31;
    public const int InsufficientRights = 0x32;
    public const int Unavailable = 0x34;
    public const int ServerDown = 0x51;
    public const int LocalError = 0x52;
    public const int Timeout = 0x55;
    public const int AuthUnknown = 0x56;
    public const int ConnectError = 0x5b;
    public const int NoResultsReturned = 0x5e;

    public const int OptSizeLimit = 0x03;
    public const int OptTimeLimit = 0x04;
    public const int OptReferrals = 0x08;
    public const int OptProtocolVersion = 0x11;
    public const int OptSign = 0x95;
    public const int OptEncrypt = 0x96;
    public const int OptArecExclusive = 0x98;

    /// <summary><c>LDAP_OPT_SERVER_ERROR</c>: the server's own text for the last failure (2026-10-23, the admin review's J5: AD's <c>data 52e</c> and friends).</summary>
    public const int OptServerError = 0x33;

    public const uint AuthNegotiate = 0x0486;
    public const uint ScopeBase = 0;
    public const uint ScopeOneLevel = 1;
    public const uint ScopeSubtree = 2;

    public const uint SecWinntAuthIdentityUnicode = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Berval
    {
        public uint Length;
        public byte* Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LTimeval
    {
        public int Seconds;
        public int Microseconds;
    }

    /// <summary>SEC_WINNT_AUTH_IDENTITY_W: the account a Negotiate bind uses (lengths in characters, no terminator).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AuthIdentity
    {
        public char* User;
        public uint UserLength;
        public char* Domain;
        public uint DomainLength;
        public char* Password;
        public uint PasswordLength;
        public uint Flags;
    }

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_initW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint Init(string host, uint port);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_sslinitW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SslInit(string host, uint port, int secure);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_set_optionW")]
    public static partial int SetOption(nint ld, int option, void* value);

    /// <summary>An option read back (2026-10-23, the admin review's J5 and J3): the server's error text, the signing and sealing state. A read, never a write.</summary>
    [LibraryImport("wldap32.dll", EntryPoint = "ldap_get_optionW")]
    public static partial int GetOption(nint ld, int option, void* value);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_connect")]
    public static partial int Connect(nint ld, LTimeval* timeout);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_bind_sW")]
    public static partial int BindS(nint ld, char* dn, void* credentials, uint method);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_search_ext_sW")]
    public static partial int SearchExtS(nint ld, char* baseDn, uint scope, char* filter, char** attributes, uint attributesOnly, nint* serverControls, nint* clientControls, LTimeval* timeout, uint sizeLimit, nint* result);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_search_init_pageW")]
    public static partial nint SearchInitPage(nint ld, char* baseDn, uint scope, char* filter, char** attributes, uint attributesOnly, nint* serverControls, nint* clientControls, uint pageTimeLimit, uint totalSizeLimit, nint* sortKeys);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_get_next_page_s")]
    public static partial int GetNextPageS(nint ld, nint search, LTimeval* timeout, uint pageSize, uint* totalCount, nint* result);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_search_abandon_page")]
    public static partial int SearchAbandonPage(nint ld, nint search);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_first_entry")]
    public static partial nint FirstEntry(nint ld, nint result);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_next_entry")]
    public static partial nint NextEntry(nint ld, nint entry);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_get_dnW")]
    public static partial char* GetDn(nint ld, nint entry);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_first_attributeW")]
    public static partial char* FirstAttribute(nint ld, nint entry, nint* ber);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_next_attributeW")]
    public static partial char* NextAttribute(nint ld, nint entry, nint ber);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_get_values_lenW")]
    public static partial Berval** GetValuesLen(nint ld, nint entry, char* attribute);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_value_free_len")]
    public static partial int ValueFreeLen(Berval** values);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_memfreeW")]
    public static partial void MemFree(char* block);

    [LibraryImport("wldap32.dll", EntryPoint = "ber_free")]
    public static partial void BerFree(nint ber, int freeBuffer);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_msgfree")]
    public static partial int MsgFree(nint message);

    [LibraryImport("wldap32.dll", EntryPoint = "ldap_unbind")]
    public static partial int Unbind(nint ld);

    [LibraryImport("wldap32.dll", EntryPoint = "LdapGetLastError")]
    public static partial int GetLastError();
}
