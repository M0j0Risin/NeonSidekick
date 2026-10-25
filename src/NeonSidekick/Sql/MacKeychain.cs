using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using static NeonSidekick.Sql.KeychainNative;

namespace NeonSidekick.Sql;

/// <summary>
/// The secrets on macOS (2026-10-06, the macOS build, the user's call: the Keychain in the first release, nothing secret in plain
/// text): what DPAPI and Credential Manager are to <see cref="WindowsCredentials"/>, which hands its calls here on a Mac so every
/// caller (the API keys, the SQL family's passwords, the UNC shares) is unchanged.
///
/// <para>A value stored in a file keeps DPAPI's shape — the ciphertext inline, so a profile or a <c>sql.json</c> is still whole
/// and encrypt-in-place still works: <c>keychain:</c> and base64 of IV, AES-256-CBC ciphertext and an HMAC-SHA256 tag over the
/// caller's entropy, the IV and the ciphertext (encrypt-then-MAC). The 64-byte key (32 to encrypt, 32 to sign) is one generic
/// password in the login Keychain, service <see cref="Service"/>, account <see cref="MasterAccount"/>, made on first use. AES-CBC
/// and HMAC rather than AES-GCM: both are CommonCrypto underneath on macOS, where AES-GCM goes through a CryptoKit shim the AOT
/// build would be the first to prove.</para>
///
/// <para>Credential Manager's generic credentials (<c>passwordStore: credman</c>) are Keychain generic passwords with the target
/// as the service and the user as the account: <c>security add-generic-password -s &lt;target&gt; -a &lt;user&gt; -w</c> makes one.</para>
///
/// <para>A build with a new signature reads the key the first time after it with the Keychain's "allow" prompt (an ad-hoc signed
/// exe's identity is its hash); Always Allow keeps it quiet until the next build.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacKeychain
{
    /// <summary>What a Keychain-encrypted value starts with.</summary>
    public const string ProtectedPrefix = WindowsCredentials.KeychainPrefix;

    /// <summary>The service of the app's own key in the Keychain.</summary>
    public const string Service = "NeonSidekick";

    /// <summary>
    /// The service the app's key is read and made under: <see cref="Service"/>, or the test suite's own (later on 2026-10-06: the
    /// tests encrypt and decrypt through here, and on a developer's Mac they made the app's key in the login Keychain before the
    /// app ever ran, trusting <c>dotnet</c>). Set once, before the key is first read.
    /// </summary>
    internal static string KeyService { get; set; } = Service;

    /// <summary>The account of the app's own key in the Keychain.</summary>
    public const string MasterAccount = "master-key";

    private const int KeyBytes = 64;
    private const int IvBytes = 16;
    private const int TagBytes = 32;

    private static readonly object s_gate = new();
    private static byte[]? s_key;

    public static CredentialResult Protect(string secret, byte[] associated)
    {
        if (Key(out var key) is { } failure)
        {
            return failure;
        }

        byte[] plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            using var aes = Aes.Create();
            aes.Key = key[..32];
            byte[] iv = RandomNumberGenerator.GetBytes(IvBytes);
            byte[] cipher = aes.EncryptCbc(plain, iv);
            byte[] tag = Tag(key, associated, iv, cipher);
            return CredentialResult.Ok(ProtectedPrefix + Convert.ToBase64String([.. iv, .. cipher, .. tag]));
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    public static CredentialResult Unprotect(string value, byte[] associated)
    {
        if (!value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            return CredentialResult.Failed(WindowsCredentials.IsProtected(value) ? SqlText.CannotDecrypt("it was encrypted on Windows") : SqlText.NotProtected);
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(value[ProtectedPrefix.Length..]);
        }
        catch (FormatException)
        {
            return CredentialResult.Failed(SqlText.NotProtected);
        }

        if (blob.Length < IvBytes + 16 + TagBytes)
        {
            return CredentialResult.Failed(SqlText.NotProtected);
        }

        if (Key(out var key) is { } failure)
        {
            return failure;
        }

        byte[] iv = blob[..IvBytes];
        byte[] cipher = blob[IvBytes..^TagBytes];
        byte[] tag = blob[^TagBytes..];
        if (!CryptographicOperations.FixedTimeEquals(tag, Tag(key, associated, iv, cipher)))
        {
            return CredentialResult.Failed(SqlText.CannotDecrypt("the Keychain's key does not match it: it was saved by another account or machine"));
        }

        using var aes = Aes.Create();
        aes.Key = key[..32];
        byte[] plain = aes.DecryptCbc(cipher, iv);
        try
        {
            return CredentialResult.Ok(Encoding.UTF8.GetString(plain));
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>
    /// Whether <paramref name="target"/> is the app's own key's service (2026-10-24, the database review's D7: a generic credential named
    /// <c>NeonSidekick</c> read the key as its password, and saving through it deleted the key — every <c>keychain:</c> secret lost).
    /// </summary>
    public static bool IsReserved(string target) =>
        string.Equals(target.Trim(), Service, StringComparison.OrdinalIgnoreCase) || string.Equals(target.Trim(), KeyService, StringComparison.OrdinalIgnoreCase);

    public static CredentialResult ReadGeneric(string target)
    {
        if (IsReserved(target))
        {
            return CredentialResult.Failed(SqlText.ReservedCredential(target));
        }

        int status = Find(target, null, out byte[]? secret, out IntPtr item);
        try
        {
            if (status != ErrSecSuccess)
            {
                return status == ErrSecItemNotFound ? CredentialResult.Failed(SqlText.NoCredential(target), notFound: true) : CredentialResult.Failed(Describe(status));
            }

            // A found item with no data, or none, is no credential (2026-10-24, review Q10 and K4: secret! threw on it).
            if (secret is not { Length: > 0 })
            {
                return CredentialResult.Failed(SqlText.EmptyCredential(target));
            }

            var mark = CredentialMark.Parse(AttributeOf(item, KeychainNative.CommentAttribute));
            return CredentialResult.Ok(Encoding.UTF8.GetString(secret), AccountOf(item), mark.Host);
        }
        finally
        {
            // Zeroed once read (2026-10-24, review K5).
            if (secret is not null)
            {
                Array.Clear(secret);
            }

            Release(item);
        }
    }

    /// <summary>
    /// Writes <paramref name="secret"/> for <paramref name="target"/> as <paramref name="user"/>, its comment the app's mark (<see cref="CredentialMark"/>:
    /// UTF-8, and <paramref name="host"/> when given, DB9). An item already there is changed in place (2026-10-24, the database review's I11: the
    /// old one was deleted before the add, so a refused add — a canceled prompt, a locked Keychain — lost the old password).
    /// </summary>
    public static CredentialResult WriteGeneric(string target, string user, string secret, string? host = null)
    {
        if (IsReserved(target))
        {
            return CredentialResult.Failed(SqlText.ReservedCredential(target));
        }

        if (secret.Length == 0)
        {
            return CredentialResult.Failed(SqlText.EmptyCredential(target));
        }

        byte[] data = Encoding.UTF8.GetBytes(secret);
        string comment = CredentialMark.Format(wide: false, host);
        try
        {
            int found = Find(target, null, out byte[]? old, out IntPtr item);
            if (old is not null)
            {
                Array.Clear(old);
            }

            try
            {
                if (found == ErrSecSuccess)
                {
                    // The first item for the target is the one a read finds, so changing it is the replace; another account's item behind it
                    // (Q9) is never read and goes with the next delete.
                    int changed = Modify(item, user, comment, data);
                    return changed == ErrSecSuccess ? CredentialResult.Ok("") : CredentialResult.Failed(Describe(changed));
                }

                if (found != ErrSecItemNotFound)
                {
                    return CredentialResult.Failed(Describe(found));
                }
            }
            finally
            {
                Release(item);
            }

            int status = Add(target, user, data, out IntPtr added);
            try
            {
                if (status != ErrSecSuccess)
                {
                    return CredentialResult.Failed(Describe(status));
                }

                // The comment rides a second call (the add takes no attributes); a refusal leaves a working credential with no host recorded,
                // which reads as before DB9.
                _ = Modify(added, null, comment, null);
                return CredentialResult.Ok("");
            }
            finally
            {
                Release(added);
            }
        }
        finally
        {
            Array.Clear(data);
        }
    }

    /// <summary>
    /// Removes every item for <paramref name="target"/> (2026-10-24, the database review's Q9: only the first was removed, so another account's
    /// item stayed and a read could find its old password), up to 32. One that is not there counts as removed.
    /// </summary>
    public static bool DeleteGeneric(string target)
    {
        if (IsReserved(target))
        {
            return false;
        }

        for (int round = 0; round < 32; round++)
        {
            int status = Find(target, null, out byte[]? secret, out IntPtr item);
            if (secret is not null)
            {
                Array.Clear(secret);
            }

            if (status == ErrSecItemNotFound)
            {
                return true;
            }

            if (status != ErrSecSuccess)
            {
                Release(item);
                return false;
            }

            int deleted = SecKeychainItemDelete(item);
            Release(item);
            if (deleted != ErrSecSuccess)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>The sentence for a Keychain status. Pinned in shape (the words in <see cref="SqlText"/> since 2026-10-24, review K10).</summary>
    public static string Describe(int status) => status switch
    {
        ErrSecUserCanceled => SqlText.KeychainCanceled,
        ErrSecAuthFailed => SqlText.KeychainAuthFailed,
        ErrSecInteractionNotAllowed => SqlText.KeychainLocked,
        _ => SqlText.KeychainRefused(status.ToString(CultureInfo.InvariantCulture)),
    };

    private static byte[] Tag(byte[] key, byte[] associated, byte[] iv, byte[] cipher)
    {
        byte[] message = [.. associated, .. iv, .. cipher];
        return HMACSHA256.HashData(key[32..], message);
    }

    // The app's key, read or made once per process. Null on success, else the failure to hand back.
    private static CredentialResult? Key(out byte[] key)
    {
        lock (s_gate)
        {
            if (s_key is { } cached)
            {
                key = cached;
                return null;
            }

            key = [];
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int status = Find(KeyService, MasterAccount, out byte[]? stored, out IntPtr item);
                Release(item);
                if (status == ErrSecSuccess)
                {
                    try
                    {
                        byte[] decoded = Convert.FromBase64String(Encoding.ASCII.GetString(stored!));
                        if (decoded.Length != KeyBytes)
                        {
                            return CredentialResult.Failed(SqlText.KeychainKeyDamaged);
                        }

                        s_key = key = decoded;
                        return null;
                    }
                    catch (FormatException)
                    {
                        return CredentialResult.Failed(SqlText.KeychainKeyDamaged);
                    }
                    finally
                    {
                        Array.Clear(stored!);
                    }
                }

                if (status != ErrSecItemNotFound)
                {
                    return CredentialResult.Failed(Describe(status));
                }

                byte[] fresh = RandomNumberGenerator.GetBytes(KeyBytes);
                byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(fresh));
                int added = Add(KeyService, MasterAccount, encoded, out IntPtr made);
                Release(made);
                Array.Clear(encoded);
                if (added == ErrSecSuccess)
                {
                    s_key = key = fresh;
                    return null;
                }

                if (added != ErrSecDuplicateItem)
                {
                    return CredentialResult.Failed(Describe(added));
                }

                // Another process made it between the find and the add: read theirs.
            }

            return CredentialResult.Failed(SqlText.KeychainKeyUnmade);
        }
    }

    /// <summary>
    /// The item's account attribute (2026-10-23, decision DA2: <c>SecKeychainFindGenericPassword</c> gives no account, so the admin families'
    /// user check reads it with <c>SecKeychainItemCopyAttributesAndData</c>, the same Security framework). Null when it can't be read.
    /// Unproven off a Mac: the smoke's <c>keys:keychain</c> reads it back.
    /// </summary>
    internal static string? AccountOf(IntPtr item)
    {
        if (item == IntPtr.Zero)
        {
            return null;
        }

        uint tag = KeychainNative.AccountAttribute;
        uint format = KeychainNative.FormatString;
        var info = new KeychainNative.SecKeychainAttributeInfo { Count = 1, Tag = &tag, Format = &format };
        KeychainNative.SecKeychainAttributeList* list = null;
        if (KeychainNative.SecKeychainItemCopyAttributesAndData(item, &info, null, &list, null, null) != ErrSecSuccess || list is null)
        {
            return null;
        }

        try
        {
            return list->Count > 0 && list->Attr[0].Data is not null
                ? Encoding.UTF8.GetString(new ReadOnlySpan<byte>(list->Attr[0].Data, (int)list->Attr[0].Length))
                : null;
        }
        finally
        {
            KeychainNative.SecKeychainItemFreeAttributesAndData(list, null);
        }
    }

    private static int Find(string service, string? account, out byte[]? secret, out IntPtr item)
    {
        secret = null;
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = account is null ? [] : Encoding.UTF8.GetBytes(account);
        uint length = 0;
        void* data = null;
        IntPtr found = IntPtr.Zero;
        int status;
        fixed (byte* s = serviceBytes)
        fixed (byte* a = accountBytes)
        {
            status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, s, (uint)accountBytes.Length, account is null ? null : a, &length, &data, &found);
        }

        item = found;
        if (status == ErrSecSuccess && data is not null && length > 0)
        {
            secret = new ReadOnlySpan<byte>(data, (int)length).ToArray();
            new Span<byte>(data, (int)length).Clear();
        }

        if (data is not null)
        {
            SecKeychainItemFreeContent(null, data);
        }

        return status;
    }

    private static int Add(string service, string account, byte[] data, out IntPtr item)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        IntPtr made = IntPtr.Zero;
        int status;
        fixed (byte* s = serviceBytes)
        fixed (byte* a = accountBytes)
        fixed (byte* d = data)
        {
            status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, s, (uint)accountBytes.Length, a, (uint)data.Length, d, &made);
        }

        item = made;
        return status;
    }

    /// <summary>
    /// Changes <paramref name="item"/> in place (2026-10-24, review I11 and DB9): its account (when given), its comment, and its password
    /// (when <paramref name="data"/> is given), through <c>SecKeychainItemModifyAttributesAndData</c>, the Security framework already linked.
    /// </summary>
    private static int Modify(IntPtr item, string? account, string comment, byte[]? data)
    {
        if (item == IntPtr.Zero)
        {
            return ErrSecItemNotFound;
        }

        byte[] accountBytes = account is null ? [] : Encoding.UTF8.GetBytes(account);
        byte[] commentBytes = Encoding.UTF8.GetBytes(comment);
        fixed (byte* a = accountBytes)
        fixed (byte* c = commentBytes)
        fixed (byte* d = data)
        {
            var attributes = stackalloc KeychainNative.SecKeychainAttribute[2];
            uint count = 0;
            attributes[count++] = new KeychainNative.SecKeychainAttribute { Tag = KeychainNative.CommentAttribute, Length = (uint)commentBytes.Length, Data = c };
            if (account is not null)
            {
                attributes[count++] = new KeychainNative.SecKeychainAttribute { Tag = KeychainNative.AccountAttribute, Length = (uint)accountBytes.Length, Data = a };
            }

            var list = new KeychainNative.SecKeychainAttributeList { Count = count, Attr = attributes };
            return KeychainNative.SecKeychainItemModifyAttributesAndData(item, &list, data is null ? 0u : (uint)data.Length, data is null ? null : d);
        }
    }

    /// <summary>One string attribute of <paramref name="item"/> (<see cref="AccountOf"/>'s read for any tag); null when it can't be read or is absent.</summary>
    internal static string? AttributeOf(IntPtr item, uint attribute)
    {
        if (item == IntPtr.Zero)
        {
            return null;
        }

        uint tag = attribute;
        uint format = KeychainNative.FormatString;
        var info = new KeychainNative.SecKeychainAttributeInfo { Count = 1, Tag = &tag, Format = &format };
        KeychainNative.SecKeychainAttributeList* list = null;
        if (KeychainNative.SecKeychainItemCopyAttributesAndData(item, &info, null, &list, null, null) != ErrSecSuccess || list is null)
        {
            return null;
        }

        try
        {
            return list->Count > 0 && list->Attr[0].Data is not null
                ? Encoding.UTF8.GetString(new ReadOnlySpan<byte>(list->Attr[0].Data, (int)list->Attr[0].Length))
                : null;
        }
        finally
        {
            KeychainNative.SecKeychainItemFreeAttributesAndData(list, null);
        }
    }

    private static void Release(IntPtr item)
    {
        if (item != IntPtr.Zero)
        {
            CFRelease(item);
        }
    }
}
