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

    public static CredentialResult ReadGeneric(string target)
    {
        int status = Find(target, null, out byte[]? secret, out IntPtr item);
        Release(item);
        return status switch
        {
            ErrSecSuccess => CredentialResult.Ok(Encoding.UTF8.GetString(secret!)),
            ErrSecItemNotFound => CredentialResult.Failed(SqlText.NoCredential(target), notFound: true),
            _ => CredentialResult.Failed(Describe(status)),
        };
    }

    public static CredentialResult WriteGeneric(string target, string user, string secret)
    {
        // Replace: the old item (whatever its account) goes, the new one is added with this account.
        if (!DeleteGeneric(target))
        {
            return CredentialResult.Failed("the Keychain would not replace the old item");
        }

        byte[] data = Encoding.UTF8.GetBytes(secret);
        try
        {
            int status = Add(target, user, data);
            return status == ErrSecSuccess ? CredentialResult.Ok("") : CredentialResult.Failed(Describe(status));
        }
        finally
        {
            Array.Clear(data);
        }
    }

    public static bool DeleteGeneric(string target)
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
        return deleted == ErrSecSuccess;
    }

    /// <summary>The sentence for a Keychain status. Pinned in shape.</summary>
    public static string Describe(int status) => status switch
    {
        ErrSecUserCanceled => "the Keychain prompt was cancelled",
        ErrSecAuthFailed => "the Keychain refused the sign-in",
        ErrSecInteractionNotAllowed => "the Keychain is locked and cannot ask here",
        _ => "the Keychain refused it (OSStatus " + status.ToString(CultureInfo.InvariantCulture) + ")",
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
                            return CredentialResult.Failed("the app's key in the Keychain is damaged; delete the NeonSidekick item in Keychain Access and save the secrets again");
                        }

                        s_key = key = decoded;
                        return null;
                    }
                    catch (FormatException)
                    {
                        return CredentialResult.Failed("the app's key in the Keychain is damaged; delete the NeonSidekick item in Keychain Access and save the secrets again");
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
                int added = Add(KeyService, MasterAccount, encoded);
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

            return CredentialResult.Failed("the app's key could not be read or made in the Keychain");
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
        if (status == ErrSecSuccess && data is not null)
        {
            secret = new ReadOnlySpan<byte>(data, (int)length).ToArray();
            new Span<byte>(data, (int)length).Clear();
            SecKeychainItemFreeContent(null, data);
        }

        return status;
    }

    private static int Add(string service, string account, byte[] data)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        fixed (byte* s = serviceBytes)
        fixed (byte* a = accountBytes)
        fixed (byte* d = data)
        {
            return SecKeychainAddGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, s, (uint)accountBytes.Length, a, (uint)data.Length, d, null);
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
