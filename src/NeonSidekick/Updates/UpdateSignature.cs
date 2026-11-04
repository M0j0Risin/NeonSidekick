using System.Security.Cryptography;

namespace NeonSidekick.Updates;

/// <summary>
/// The release signature (2026-11-04, self-update; the user's pick: the private key stays on their PC and the app refuses anything
/// unsigned): ECDSA P-256 with SHA-256 over a release's <c>.sha256</c> file's exact bytes. The <c>.sig</c> file holds one base64 IEEE P1363
/// signature per line (fixed width, unlike DER, and spelled out because tools differ), and more than one line is allowed, so a new key can
/// sign beside the old one until old copies have moved on. Any line passing under any given key is enough. A lasting contract: an old exe
/// checks a new release, so this format is pinned by a test vector in <c>UpdateSignatureTests</c>.
///
/// <para>Public, since <c>tools/ReleaseSign.cs</c> and <c>tools/UpdateSim.cs</c> reach it through <c>#:project</c> and share this routine, so
/// the simulation proves the app accepts what the real tool writes (<c>InternalsVisibleTo</c> names only the tests). It holds no key.</para>
/// </summary>
public static class UpdateSignature
{
    /// <summary>A P1363 signature on P-256 is two 32-byte numbers side by side, so anything else on a line isn't one.</summary>
    public const int SignatureBytes = 64;

    private const int KeyBits = 256;
    private const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    // The key file's own protection. 600,000 rounds is OWASP's figure for PBKDF2 with SHA-256: slow to guess at, a moment to open.
    private static readonly PbeParameters KeyFileProtection = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    /// <summary>Whether any line of <paramref name="signatureText"/> is a valid signature of <paramref name="signed"/> under any of <paramref name="publicKeys"/>. Bad lines and bad keys count as no match, so a <c>.sig</c> a future version added a line to still passes on the line this copy can read.</summary>
    public static bool Verify(ReadOnlySpan<byte> signed, string signatureText, IEnumerable<string> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(signatureText);
        ArgumentNullException.ThrowIfNull(publicKeys);
        var signatures = Lines(signatureText);
        if (signatures.Count == 0)
        {
            return false;
        }

        foreach (string publicKey in publicKeys)
        {
            using ECDsa? key = TryLoadPublicKey(publicKey);
            if (key is null)
            {
                continue;
            }

            foreach (byte[] signature in signatures)
            {
                if (key.VerifyData(signed, signature, HashAlgorithmName.SHA256, Format))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>One line of a <c>.sig</c> file: the base64 signature of <paramref name="signed"/>, with no line ending.</summary>
    public static string SignatureLine(ReadOnlySpan<byte> signed, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        return Convert.ToBase64String(privateKey.SignData(signed, HashAlgorithmName.SHA256, Format));
    }

    /// <summary>A new P-256 key pair. The caller disposes it.</summary>
    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The text form of a public key, as <see cref="UpdateKeys"/> lists it: base64 of its SubjectPublicKeyInfo.</summary>
    public static string PublicKeyText(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Whether <paramref name="publicKey"/> is a P-256 public key in <see cref="PublicKeyText"/>'s form.</summary>
    public static bool IsPublicKey(string publicKey)
    {
        using ECDsa? key = TryLoadPublicKey(publicKey);
        return key is not null;
    }

    /// <summary>A private key as a password-encrypted PKCS#8 PEM, the contents of a key file.</summary>
    public static string EncryptedKeyFile(ECDsa key, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportEncryptedPkcs8PrivateKeyPem(password, KeyFileProtection);
    }

    /// <summary>Opens a key file's text. A wrong password or a damaged file throws <see cref="CryptographicException"/>, or <see cref="ArgumentException"/> for text with no key in it. The caller disposes the key.</summary>
    public static ECDsa LoadKeyFile(string keyFileText, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(keyFileText);
        var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(keyFileText, password);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static ECDsa? TryLoadPublicKey(string publicKey)
    {
        byte[] bytes = new byte[publicKey.Length];
        if (!Convert.TryFromBase64String(publicKey, bytes, out int written))
        {
            return null;
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes.AsSpan(0, written), out _);
            if (key.KeySize == KeyBits)
            {
                return key;
            }
        }
        catch (CryptographicException)
        {
            // Not a key: the same answer as the wrong size below.
        }

        key.Dispose();
        return null;
    }

    private static List<byte[]> Lines(string signatureText)
    {
        var lines = new List<byte[]>();
        foreach (string line in signatureText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] signature = new byte[SignatureBytes];
            if (Convert.TryFromBase64String(line, signature, out int written) && written == SignatureBytes)
            {
                lines.Add(signature);
            }
        }

        return lines;
    }
}
