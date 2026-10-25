using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NeonSidekick.Sql;

/// <summary>
/// What a credential call gave: the value, or why there is none (<see cref="Error"/> set, <see cref="Value"/> null). <see cref="User"/> is a
/// stored credential's own account name when the store keeps one (2026-10-23, the admin review's N2/V1, decision DA2: the admin families
/// check it against the entry's user before a password is sent); the SQL and UNC callers ignore it. <see cref="Host"/> (2026-10-24, the
/// database review's H5, decision DB9) is the host a credential the app saved was saved for, read back from its comment; null for one written
/// by <c>cmdkey</c>, <c>security</c> or before then.
/// </summary>
public readonly record struct CredentialResult(string? Value, string? Error, bool NotFound = false, string? User = null, string? Host = null)
{
    public static CredentialResult Ok(string value, string? user = null, string? host = null) => new(value, null, User: user, Host: host);

    public static CredentialResult Failed(string error, bool notFound = false) => new(null, error, notFound);
}

/// <summary>
/// The SQL tools' three Win32 doors (later on 2026-09-23, the user's ask: sign in to SQL Server "as another user",
/// the way their <c>runas /savecred</c> launcher does, with the password kept safe): DPAPI for a password stored in
/// <c>sql.json</c> (<see cref="Protect"/>), Windows Credential Manager for one stored outside it (<see cref="ReadGeneric"/>),
/// and a <c>LOGON32_LOGON_NEW_CREDENTIALS</c> logon — what <c>runas /netonly</c> makes — for a connection that signs in
/// as another account (<see cref="LogonNetOnly"/>). A Windows-only layer beside <c>Audio/WinMm*</c> and
/// <c>UI/WindowsClipboard</c>: every entry checks <see cref="OperatingSystem.IsWindows"/> and answers an outcome
/// elsewhere, nothing throws on a refusal, and the interop is <c>LibraryImport</c> over typed pointers (nothing
/// marshalled by value — the NAudio-under-AOT scar). The smoke check <c>sql:credentials</c> runs all three on the
/// published binary.
///
/// <para>On macOS (2026-10-06, the macOS build) the first two hand over to <see cref="MacKeychain"/>: <see cref="Protect"/> writes a
/// <c>keychain:</c> value, <see cref="Unprotect"/> reads one, and the generic credentials are Keychain generic passwords — so every
/// caller stays as it is. The run-as logon has no twin there and still answers <see cref="NotWindows"/>.</para>
/// </summary>
public static unsafe partial class WindowsCredentials
{
    /// <summary>What a DPAPI value in <c>sql.json</c> starts with; anything else in a <c>password</c> is plain text.</summary>
    public const string ProtectedPrefix = "dpapi:";

    /// <summary>What a Keychain-encrypted value starts with (macOS, 2026-10-06: <see cref="MacKeychain"/>).</summary>
    public const string KeychainPrefix = "keychain:";

    /// <summary>The entropy mixed into every DPAPI blob, so a value lifted from another app's store does not decrypt here.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NeonSidekick.sql");

    public const string NotWindows = "this needs Windows";

    private const uint CryptProtectUiForbidden = 0x1;
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int Logon32LogonNewCredentials = 9;
    private const int Logon32ProviderWinnt50 = 3;

    /// <summary>Whether <see cref="Protect"/> can encrypt here: Windows (DPAPI) and macOS (the Keychain, 2026-10-06).</summary>
    public static bool CanProtect => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Whether <paramref name="value"/> starts as a value this layer writes: DPAPI's, or the Keychain's on macOS.</summary>
    public static bool IsProtected(string? value) =>
        value is not null && (value.StartsWith(ProtectedPrefix, StringComparison.Ordinal) || value.StartsWith(KeychainPrefix, StringComparison.Ordinal));

    /// <summary>The fixed start of every DPAPI blob: its version and the default provider's GUID.</summary>
    private static ReadOnlySpan<byte> DpapiHeader => [0x01, 0x00, 0x00, 0x00, 0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11, 0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB];

    /// <summary>
    /// Whether <paramref name="value"/> has the shape of a value this layer wrote, not just its prefix (2026-10-24, the database review's D10:
    /// a typed plain password that happened to start <c>dpapi:</c> or <c>keychain:</c> counted as protected, so it stayed plain on disk and
    /// every sign-in failed to decrypt it for good): base64 of a DPAPI blob (its fixed header), or of the Keychain's IV, ciphertext and tag.
    /// A value with the prefix but not the shape is plain text and is encrypted like one. Pure; nothing is decrypted.
    /// </summary>
    public static bool IsSealed(string? value)
    {
        if (!IsProtected(value))
        {
            return false;
        }

        bool dpapi = value!.StartsWith(ProtectedPrefix, StringComparison.Ordinal);
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(value[(dpapi ? ProtectedPrefix.Length : KeychainPrefix.Length)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        return dpapi ? blob.AsSpan().StartsWith(DpapiHeader) : blob.Length >= 16 + 16 + 32 && (blob.Length - 16 - 32) % 16 == 0;
    }

    /// <summary><paramref name="secret"/> encrypted for this Windows user on this machine: <c>dpapi:</c> and the blob in base64.</summary>
    public static CredentialResult Protect(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!OperatingSystem.IsWindows())
        {
            return OperatingSystem.IsMacOS() ? MacKeychain.Protect(secret, Entropy) : CredentialResult.Failed(NotWindows);
        }

        byte[] plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Crypt(plain, protect: true, out byte[] blob, out string? error)
                ? CredentialResult.Ok(ProtectedPrefix + Convert.ToBase64String(blob))
                : CredentialResult.Failed(error!);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>The secret inside a <c>dpapi:</c> value, or why it cannot be read: another user's or machine's, or not DPAPI at all.</summary>
    public static CredentialResult Unprotect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!OperatingSystem.IsWindows())
        {
            return OperatingSystem.IsMacOS() ? MacKeychain.Unprotect(value, Entropy) : CredentialResult.Failed(NotWindows);
        }

        if (!value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            return CredentialResult.Failed(SqlText.NotProtected);
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

        if (!Crypt(blob, protect: false, out byte[] plain, out string? error))
        {
            return CredentialResult.Failed(SqlText.CannotDecrypt(error!));
        }

        try
        {
            return CredentialResult.Ok(Encoding.UTF8.GetString(plain));
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    private static bool Crypt(byte[] input, bool protect, out byte[] output, out string? error)
    {
        output = [];
        error = null;
        fixed (byte* inputPointer = input)
        fixed (byte* entropyPointer = Entropy)
        {
            var inBlob = new DataBlob { Size = (uint)input.Length, Data = inputPointer };
            var entropyBlob = new DataBlob { Size = (uint)Entropy.Length, Data = entropyPointer };
            var outBlob = default(DataBlob);
            int ok = protect
                ? CryptProtectData(&inBlob, null, &entropyBlob, null, null, CryptProtectUiForbidden, &outBlob)
                : CryptUnprotectData(&inBlob, null, &entropyBlob, null, null, CryptProtectUiForbidden, &outBlob);
            if (ok == 0)
            {
                error = Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());
                return false;
            }

            try
            {
                output = new ReadOnlySpan<byte>(outBlob.Data, (int)outBlob.Size).ToArray();
                return true;
            }
            finally
            {
                new Span<byte>(outBlob.Data, (int)outBlob.Size).Clear();
                LocalFree(outBlob.Data);
            }
        }
    }

    /// <summary>The password of the Generic credential <paramref name="target"/> (what <c>cmdkey /generic:</c> makes), or not found.</summary>
    public static CredentialResult ReadGeneric(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!OperatingSystem.IsWindows())
        {
            return OperatingSystem.IsMacOS() ? MacKeychain.ReadGeneric(target) : CredentialResult.Failed(NotWindows);
        }

        Credential* credential;
        int ok;
        fixed (char* targetPointer = target)
        {
            ok = CredReadW(targetPointer, CredTypeGeneric, 0, &credential);
        }

        if (ok == 0)
        {
            int code = Marshal.GetLastPInvokeError();
            return CredentialResult.Failed(code == ErrorNotFound ? SqlText.NoCredential(target) : Marshal.GetPInvokeErrorMessage(code), notFound: code == ErrorNotFound);
        }

        var blob = new Span<byte>(credential->CredentialBlob, (int)credential->CredentialBlobSize);
        try
        {
            // An empty secret is no credential (2026-10-24, the database review's K4: cmdkey /generic:… with no /pass stored one, and the app
            // signed in with an empty password rather than say the password was never set).
            if (blob.IsEmpty)
            {
                return CredentialResult.Failed(SqlText.EmptyCredential(target));
            }

            var mark = CredentialMark.Parse(credential->Comment is null ? null : new string(credential->Comment));
            string secret = mark.Wide is { } wide ? (wide ? Encoding.Unicode.GetString(blob) : Encoding.UTF8.GetString(blob)) : CredentialMark.Decode(blob);
            return CredentialResult.Ok(secret, credential->UserName is null ? null : new string(credential->UserName), mark.Host);
        }
        finally
        {
            // Zeroed before it goes back (2026-10-24, review K5: CredFree left the password in the freed heap).
            blob.Clear();
            CredFree(credential);
        }
    }

    /// <summary>
    /// Writes (or replaces) the Generic credential <paramref name="target"/>: <paramref name="user"/> and <paramref name="secret"/>, kept on this
    /// machine for this Windows user. Its comment marks the secret as the app's UTF-16 (K3) and, given <paramref name="host"/>, records the host
    /// it is for (DB9): a read for another host is refused by the caller.
    /// </summary>
    public static CredentialResult WriteGeneric(string target, string user, string secret, string? host = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(secret);
        if (!OperatingSystem.IsWindows())
        {
            return OperatingSystem.IsMacOS() ? MacKeychain.WriteGeneric(target, user, secret, host) : CredentialResult.Failed(NotWindows);
        }

        if (secret.Length == 0)
        {
            return CredentialResult.Failed(SqlText.EmptyCredential(target));
        }

        byte[] blob = Encoding.Unicode.GetBytes(secret);
        string comment = CredentialMark.Format(wide: true, host);
        try
        {
            fixed (char* targetPointer = target)
            fixed (char* userPointer = user)
            fixed (char* commentPointer = comment)
            fixed (byte* blobPointer = blob)
            {
                var credential = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = targetPointer,
                    Comment = commentPointer,
                    UserName = userPointer,
                    CredentialBlob = blobPointer,
                    CredentialBlobSize = (uint)blob.Length,
                    Persist = CredPersistLocalMachine,
                };
                return CredWriteW(&credential, 0) != 0
                    ? CredentialResult.Ok("")
                    : CredentialResult.Failed(Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            Array.Clear(blob);
        }
    }

    /// <summary>Removes the Generic credential <paramref name="target"/>; one that is not there counts as removed.</summary>
    public static bool DeleteGeneric(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!OperatingSystem.IsWindows())
        {
            return OperatingSystem.IsMacOS() && MacKeychain.DeleteGeneric(target);
        }

        fixed (char* targetPointer = target)
        {
            return CredDeleteW(targetPointer, CredTypeGeneric, 0) != 0 || Marshal.GetLastPInvokeError() == ErrorNotFound;
        }
    }

    /// <summary>
    /// <paramref name="account"/> as the domain and user name <c>LogonUserW</c> takes: <c>DOMAIN\name</c> split at the
    /// backslash, a UPN (<c>name@domain</c>) whole with no domain; null for anything else (a bare name).
    /// </summary>
    public static (string? Domain, string User)? SplitAccount(string? account)
    {
        string text = account?.Trim() ?? "";
        int slash = text.IndexOf('\\');
        if (slash > 0 && slash < text.Length - 1 && text.IndexOf('\\', slash + 1) < 0)
        {
            return (text[..slash], text[(slash + 1)..]);
        }

        int at = text.IndexOf('@');
        return at > 0 && at < text.Length - 1 && !text.Contains('\\') ? (null, text) : null;
    }

    /// <summary>
    /// A <c>LOGON32_LOGON_NEW_CREDENTIALS</c> token for <paramref name="account"/> — <c>runas /netonly</c>'s: this process's
    /// own identity locally, <paramref name="account"/>'s credentials for every network sign-in made while it is
    /// impersonated. Windows does not check the password here (the server does, at the sign-in), so a token comes back
    /// for any well-formed account. Null with <paramref name="error"/> on a refusal.
    /// </summary>
    public static SafeAccessTokenHandle? LogonNetOnly(string account, string password, out string? error)
    {
        ArgumentNullException.ThrowIfNull(password);
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = NotWindows;
            return null;
        }

        if (SplitAccount(account) is not { } split)
        {
            error = SqlText.RunAsNeedsDomain(account);
            return null;
        }

        var (domain, user) = split;

        nint token;
        int ok;
        fixed (char* userPointer = user)
        fixed (char* domainPointer = domain)   // a UPN has no domain: fixed over null is a null pointer, which LogonUserW wants
        fixed (char* passwordPointer = password)
        {
            ok = LogonUserW(userPointer, domainPointer, passwordPointer, Logon32LogonNewCredentials, Logon32ProviderWinnt50, &token);
        }

        if (ok == 0)
        {
            error = Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());
            return null;
        }

        return new SafeAccessTokenHandle(token);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public byte* Data;
    }

    /// <summary><c>CREDENTIALW</c>, pointers kept as pointers (x64 layout from the field order).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public ulong LastWritten;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public void* Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    private static partial int CryptProtectData(DataBlob* dataIn, char* description, DataBlob* entropy, void* reserved, void* prompt, uint flags, DataBlob* dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    private static partial int CryptUnprotectData(DataBlob* dataIn, char** description, DataBlob* entropy, void* reserved, void* prompt, uint flags, DataBlob* dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial void* LocalFree(void* memory);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CredReadW(char* target, uint type, uint flags, Credential** credential);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CredWriteW(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CredDeleteW(char* target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(void* buffer);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int LogonUserW(char* user, char* domain, char* password, int logonType, int logonProvider, nint* token);
}
