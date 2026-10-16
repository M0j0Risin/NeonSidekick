using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Sql;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// The directory in-process (2026-10-15, the admin-tools plan) over <see cref="LdapNative"/>: <c>ldap_initW</c> (or <c>ldap_sslinitW</c> under
/// <c>tls</c>), protocol 3, referrals off, the server's own name used as given (<c>LDAP_OPT_AREC_EXCLUSIVE</c>), client-side time and size
/// limits; on 389 signing and sealing always on — never under TLS, where AD refuses a sign/seal bind ("Cannot bind using sign/seal on a
/// connection on which TLS or SSL is in effect") and TLS already protects. A Negotiate bind always (never a simple bind): an explicit
/// <c>SEC_WINNT_AUTH_IDENTITY_W</c> for <c>runas</c>, none for the Windows sign-in. From an unjoined PC that is NTLMv2 inside SPNEGO (Windows 11
/// removed only NTLMv1); a policy that blocks NTLM fails the bind, worded by <see cref="DirectoryErrors"/>. LDAPS checks the server's
/// certificate against Windows' trust store, as wldap32 does by default; there is no "skip validation". Excluded from coverage: proven by the
/// smoke's <c>ad:wldap32</c> and the live tests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsDirectorySystem : IDirectorySystem
{
    public T Run<T>(DirectoryTarget target, Func<IDirectorySession, T> work)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(work);
        nint ld = target.Tls ? LdapNative.SslInit(target.Server, (uint)target.Port, 1) : LdapNative.Init(target.Server, (uint)target.Port);
        if (ld == 0)
        {
            int code = LdapNative.GetLastError();
            throw new DirectoryException(code, "ldap_init failed (0x" + code.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ")");
        }

        try
        {
            Prepare(ld, target);
            Bind(ld, target.Login);
            return work(new Session(ld));
        }
        finally
        {
            LdapNative.Unbind(ld);
        }
    }

    /// <summary>The options before the connect: version 3, no referrals, the name as given, the limits, sign and seal off TLS.</summary>
    internal static void Prepare(nint ld, DirectoryTarget target)
    {
        Set(ld, LdapNative.OptProtocolVersion, 3, "LDAP_OPT_PROTOCOL_VERSION");
        Set(ld, LdapNative.OptReferrals, 0, "LDAP_OPT_REFERRALS");
        Set(ld, LdapNative.OptArecExclusive, 1, "LDAP_OPT_AREC_EXCLUSIVE");
        Set(ld, LdapNative.OptSizeLimit, DirectoryLimits.MaxEntries, "LDAP_OPT_SIZELIMIT");
        Set(ld, LdapNative.OptTimeLimit, (int)DirectoryLimits.SearchTime.TotalSeconds, "LDAP_OPT_TIMELIMIT");
        if (!target.Tls)
        {
            Set(ld, LdapNative.OptSign, 1, "LDAP_OPT_SIGN");
            Set(ld, LdapNative.OptEncrypt, 1, "LDAP_OPT_ENCRYPT");
        }

        var timeout = new LdapNative.LTimeval { Seconds = 10 };
        int connected = LdapNative.Connect(ld, &timeout);
        if (connected != LdapNative.Success)
        {
            throw new DirectoryException(connected, "ldap_connect failed (0x" + connected.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ")");
        }
    }

    private static void Set(nint ld, int option, int value, string name)
    {
        int result = LdapNative.SetOption(ld, option, &value);
        if (result != LdapNative.Success)
        {
            throw new DirectoryException(result, "ldap_set_option(" + name + ") failed");
        }
    }

    private static void Bind(nint ld, DirectoryLogin? login)
    {
        int result;
        if (login is null)
        {
            result = LdapNative.BindS(ld, null, null, LdapNative.AuthNegotiate);
        }
        else
        {
            var (domain, user) = WindowsCredentials.SplitAccount(login.User) ?? (null, login.User);
            string userText = user;
            string domainText = domain ?? "";
            string password = login.Password;
            fixed (char* u = userText)
            fixed (char* d = domainText)
            fixed (char* p = password)
            {
                var identity = new LdapNative.AuthIdentity
                {
                    User = u,
                    UserLength = (uint)userText.Length,
                    Domain = domain is null ? null : d,
                    DomainLength = (uint)domainText.Length,
                    Password = p,
                    PasswordLength = (uint)password.Length,
                    Flags = LdapNative.SecWinntAuthIdentityUnicode,
                };
                result = LdapNative.BindS(ld, null, &identity, LdapNative.AuthNegotiate);
            }
        }

        if (result != LdapNative.Success)
        {
            throw new DirectoryException(result, "ldap_bind_s failed (0x" + result.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ")");
        }
    }

    private sealed class Session(nint ld) : IDirectorySession
    {
        public DirectoryEntry RootDse()
        {
            var results = Search("", DirectoryScope.Base, "(objectClass=*)", AdAttributes.RootDse, 1, paged: false);
            return results.Entries.Count > 0 ? results.Entries[0] : throw new DirectoryException(LdapNative.NoSuchObject, "RootDSE answered nothing");
        }

        public DirectoryResults Search(string baseDn, DirectoryScope scope, string filter, IReadOnlyList<string> attributes, int sizeLimit, bool paged, TimeSpan? timeLimit = null)
        {
            ArgumentNullException.ThrowIfNull(baseDn);
            ArgumentNullException.ThrowIfNull(filter);
            ArgumentNullException.ThrowIfNull(attributes);
            var names = attributes.Select(a => Marshal.StringToHGlobalUni(a)).ToArray();
            var list = (char**)NativeMemory.Alloc((nuint)(names.Length + 1), (nuint)sizeof(nint));
            try
            {
                for (int i = 0; i < names.Length; i++)
                {
                    list[i] = (char*)names[i];
                }

                list[names.Length] = null;
                fixed (char* b = baseDn)
                fixed (char* f = filter)
                {
                    // A walk's search gets what its deadline has left (2026-10-15, the AD gaps plan), whole seconds, at least one.
                    int seconds = Math.Max(1, (int)Math.Ceiling((timeLimit ?? DirectoryLimits.SearchTime).TotalSeconds));
                    return paged ? Paged(b, scope, f, list, sizeLimit, seconds) : Single(b, scope, f, list, sizeLimit, seconds);
                }
            }
            finally
            {
                NativeMemory.Free(list);
                foreach (nint name in names)
                {
                    Marshal.FreeHGlobal(name);
                }
            }
        }

        private DirectoryResults Single(char* baseDn, DirectoryScope scope, char* filter, char** attributes, int sizeLimit, int seconds)
        {
            var timeout = new LdapNative.LTimeval { Seconds = seconds };
            nint result = 0;
            int code = LdapNative.SearchExtS(ld, baseDn, (uint)scope, filter, attributes, 0, null, null, &timeout, (uint)sizeLimit, &result);
            try
            {
                if (code is not (LdapNative.Success or LdapNative.SizeLimitExceeded or LdapNative.TimeLimitExceeded))
                {
                    throw new DirectoryException(code, "ldap_search_ext_s failed (0x" + code.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ")");
                }

                var entries = new List<DirectoryEntry>();
                Read(result, entries, sizeLimit);
                return new DirectoryResults(entries, code != LdapNative.Success, code == LdapNative.TimeLimitExceeded);
            }
            finally
            {
                if (result != 0)
                {
                    LdapNative.MsgFree(result);
                }
            }
        }

        private DirectoryResults Paged(char* baseDn, DirectoryScope scope, char* filter, char** attributes, int sizeLimit, int seconds)
        {
            nint search = LdapNative.SearchInitPage(ld, baseDn, (uint)scope, filter, attributes, 0, null, null, (uint)seconds, (uint)sizeLimit, null);
            if (search == 0)
            {
                int failed = LdapNative.GetLastError();
                throw new DirectoryException(failed, "ldap_search_init_page failed (0x" + failed.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ")");
            }

            var entries = new List<DirectoryEntry>();
            bool truncated = false;
            bool timedOut = false;
            try
            {
                while (entries.Count < sizeLimit)
                {
                    var timeout = new LdapNative.LTimeval { Seconds = seconds };
                    uint total = 0;
                    nint result = 0;
                    int code = LdapNative.GetNextPageS(ld, search, &timeout, (uint)DirectoryLimits.PageSize, &total, &result);
                    try
                    {
                        if (code == LdapNative.NoResultsReturned)
                        {
                            break;
                        }

                        if (code is LdapNative.SizeLimitExceeded or LdapNative.TimeLimitExceeded)
                        {
                            Read(result, entries, sizeLimit);
                            truncated = true;
                            timedOut = code == LdapNative.TimeLimitExceeded;
                            break;
                        }

                        if (code != LdapNative.Success)
                        {
                            throw new DirectoryException(code, "ldap_get_next_page_s failed (0x" + code.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ")");
                        }

                        Read(result, entries, sizeLimit);
                    }
                    finally
                    {
                        if (result != 0)
                        {
                            LdapNative.MsgFree(result);
                        }
                    }
                }

                truncated |= entries.Count >= sizeLimit;
                return new DirectoryResults(entries, truncated, timedOut);
            }
            finally
            {
                LdapNative.SearchAbandonPage(ld, search);
            }
        }

        private void Read(nint result, List<DirectoryEntry> entries, int sizeLimit)
        {
            if (result == 0)
            {
                return;
            }

            for (nint entry = LdapNative.FirstEntry(ld, result); entry != 0 && entries.Count < sizeLimit; entry = LdapNative.NextEntry(ld, entry))
            {
                char* dn = LdapNative.GetDn(ld, entry);
                string name = dn is null ? "" : new string(dn);
                if (dn is not null)
                {
                    LdapNative.MemFree(dn);
                }

                var attributes = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
                nint ber = 0;
                for (char* attribute = LdapNative.FirstAttribute(ld, entry, &ber); attribute is not null; attribute = LdapNative.NextAttribute(ld, entry, ber))
                {
                    string key = new(attribute);
                    var values = LdapNative.GetValuesLen(ld, entry, attribute);
                    LdapNative.MemFree(attribute);
                    if (values is null || AdAttributes.NeverReadName(key))
                    {
                        if (values is not null)
                        {
                            LdapNative.ValueFreeLen(values);
                        }

                        continue;
                    }

                    var list = new List<byte[]>();
                    for (int i = 0; values[i] is not null; i++)
                    {
                        list.Add(new ReadOnlySpan<byte>(values[i]->Value, (int)values[i]->Length).ToArray());
                    }

                    LdapNative.ValueFreeLen(values);
                    attributes[key] = list;
                }

                if (ber != 0)
                {
                    LdapNative.BerFree(ber, 0);
                }

                entries.Add(new DirectoryEntry(name, attributes));
            }
        }
    }
}
