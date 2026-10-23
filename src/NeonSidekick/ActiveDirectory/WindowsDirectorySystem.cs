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
            if (!target.Tls)
            {
                Sealed(ld);
            }

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
        if (NamesHost(target))
        {
            // The name as given only when it names a host (2026-10-23, the admin review's J1): for the domain's own name (the default, and
            // `current`) AREC_EXCLUSIVE asked for ldap/<domain>, an SPN no DC registers, and Negotiate fell back to NTLM without a word.
            Set(ld, LdapNative.OptArecExclusive, 1, "LDAP_OPT_AREC_EXCLUSIVE");
        }

        Set(ld, LdapNative.OptSizeLimit, DirectoryLimits.MaxEntries, "LDAP_OPT_SIZELIMIT");

        // Set before the connect and the bind (2026-10-23, the admin review's O2, settled against ldap_bind's page): ldap_bind_s takes no timeout
        // of its own and follows LDAP_OPT_TIMELIMIT (120 s by default), so the bind is held to the search time, inside the call's 40 s deadline
        // with the connect's 10 s; the sign-in guard's cap bounds what is left.
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
            // Under TLS wldap32 says 0x51 for a certificate it refused as for a server that is down (2026-10-23, the admin review's J4): both said.
            int code = target.Tls && connected == LdapNative.ServerDown ? Ldap.LdapResultCodes.TlsUnanswered : connected;
            throw new DirectoryException(code, "ldap_connect failed (0x" + connected.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ")");
        }
    }

    /// <summary>Whether the server is a host's name rather than the domain's own (J1).</summary>
    internal static bool NamesHost(DirectoryTarget target) =>
        target.Domain is not { } domain || !string.Equals(target.Server.Trim().TrimEnd('.'), domain.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Signing and sealing read back after the bind (2026-10-23, the admin review's J3, the Mac's check of its layer): a session the options say is
    /// neither is closed, never taken as sealed.
    /// </summary>
    private static void Sealed(nint ld)
    {
        nint sign = 0, encrypt = 0;
        if (LdapNative.GetOption(ld, LdapNative.OptSign, &sign) != LdapNative.Success || LdapNative.GetOption(ld, LdapNative.OptEncrypt, &encrypt) != LdapNative.Success
            || (int)sign == 0 || (int)encrypt == 0)
        {
            throw new DirectoryException(Ldap.LdapResultCodes.NotSealed, DirectoryText.NotSealedAfterBind);
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
            // The server's own text joins the code (2026-10-23, the admin review's J5): AD's "data 52e" (a wrong password) and the locked,
            // disabled, expired and must-change sub-codes read apart only from it (B8), and an NTLM block names itself there.
            string server = ServerError(ld);
            throw new DirectoryException(result, "ldap_bind_s failed (0x" + result.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ")" + (server.Length > 0 ? ": " + server : ""));
        }
    }

    /// <summary><c>LDAP_OPT_SERVER_ERROR</c>'s text, cut to 300 characters; empty when there is none.</summary>
    private static string ServerError(nint ld)
    {
        char* text = null;
        if (LdapNative.GetOption(ld, LdapNative.OptServerError, &text) != LdapNative.Success || text is null)
        {
            return "";
        }

        try
        {
            string value = new string(text).Trim();
            return value.Length <= 300 ? value : value[..300];
        }
        finally
        {
            LdapNative.MemFree(text);
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

            // The whole walk is bounded (2026-10-23, the admin review's N1, A6, D8): one clock for every page (each page used to get the whole
            // limit again, so a slow server ran the call past its deadline into an abandoned thread), a page cap and a stop after a few empty
            // pages in a row (a server answering empty pages with a cookie spun this for ever; RFC 2696 allows an empty page, so not the
            // first), and "truncated" only when something was left unread (exactly sizeLimit entries with nothing after read as "500+").
            var entries = new List<DirectoryEntry>();
            bool truncated = false;
            bool timedOut = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int maxPages = (sizeLimit / DirectoryLimits.PageSize) + DirectoryLimits.PageSlack;
            int emptyInARow = 0;
            try
            {
                for (int page = 0; ; page++)
                {
                    int left = seconds - (int)clock.Elapsed.TotalSeconds;
                    if (left <= 0 || page >= maxPages || emptyInARow >= DirectoryLimits.MaxEmptyPages)
                    {
                        truncated = true;
                        timedOut = left <= 0;
                        break;
                    }

                    bool full = entries.Count >= sizeLimit;
                    var timeout = new LdapNative.LTimeval { Seconds = left };
                    uint total = 0;
                    nint result = 0;

                    // Once full, one more page of one entry says whether anything was left.
                    int code = LdapNative.GetNextPageS(ld, search, &timeout, full ? 1u : (uint)DirectoryLimits.PageSize, &total, &result);
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

                        int before = entries.Count;
                        bool leftOver = Read(result, entries, sizeLimit);
                        if (full)
                        {
                            truncated = result != 0 && LdapNative.FirstEntry(ld, result) != 0;
                            break;
                        }

                        if (leftOver)
                        {
                            truncated = true;
                            break;
                        }

                        emptyInARow = entries.Count == before ? emptyInARow + 1 : 0;
                    }
                    finally
                    {
                        if (result != 0)
                        {
                            LdapNative.MsgFree(result);
                        }
                    }
                }

                return new DirectoryResults(entries, truncated, timedOut);
            }
            finally
            {
                LdapNative.SearchAbandonPage(ld, search);
            }
        }

        /// <summary>A result's entries added up to <paramref name="sizeLimit"/>; whether some were left unread past it.</summary>
        private bool Read(nint result, List<DirectoryEntry> entries, int sizeLimit)
        {
            if (result == 0)
            {
                return false;
            }

            nint entry = LdapNative.FirstEntry(ld, result);
            for (; entry != 0 && entries.Count < sizeLimit; entry = LdapNative.NextEntry(ld, entry))
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

            return entry != 0;
        }
    }
}
