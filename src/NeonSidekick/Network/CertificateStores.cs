using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NeonSidekick.Network;

/// <summary>What <see cref="CertificateStores.Read"/> found: the certificates, or the sentence saying why not.</summary>
public sealed record CertificateListing(string Store, string Location, IReadOnlyList<CertificateView> Certificates, string? Error, int Unreadable = 0);

/// <summary>
/// <c>net_certs</c>' reader (2026-10-15, the admin-tools plan): an <see cref="X509Store"/> opened read-only and only if it exists, every
/// certificate read into its view — whether a private key is there is a yes or no; nothing is exported. The words a call takes: <c>my</c>,
/// <c>root</c>, <c>ca</c>, <c>trustedpeople</c>, <c>trustedpublisher</c>, <c>authroot</c>, <c>disallowed</c>, <c>webhosting</c>; locations
/// <c>user</c> and <c>machine</c>. A Mac keeps fewer stores (<c>my</c> is a keychain; <c>root</c> the system's trust): any other says
/// "that certificate store is not kept here", never that something needs Windows.
/// </summary>
public static class CertificateStores
{
    /// <summary>The store words, in the order the description lists them.</summary>
    public static readonly IReadOnlyList<string> StoreWords = ["my", "root", "ca", "trustedpeople", "trustedpublisher", "authroot", "disallowed", "webhosting"];

    /// <summary>The location words.</summary>
    public static readonly IReadOnlyList<string> LocationWords = ["user", "machine"];

    /// <summary>The store's name as Windows calls it; null for a word that is none.</summary>
    public static string? StoreName(string word) => word.Trim().ToLowerInvariant() switch
    {
        "my" or "personal" => "My",
        "root" => "Root",
        "ca" or "intermediate" => "CA",
        "trustedpeople" => "TrustedPeople",
        "trustedpublisher" => "TrustedPublisher",
        "authroot" => "AuthRoot",
        "disallowed" => "Disallowed",
        "webhosting" => "WebHosting",
        _ => null,
    };

    /// <summary>
    /// The location a store is read at when none is named: the user's, but on a Mac the machine's for <c>Root</c>, the system's trust
    /// (2026-10-18, the Mac pass: .NET's user Root there is the account's own trust settings, empty as a rule, so <c>/net certs root</c>
    /// listed none where MAC.md promised the system's trust; <c>root machine</c> listed its 154).
    /// </summary>
    public static StoreLocation DefaultLocation(string store) =>
        OperatingSystem.IsMacOS() && string.Equals(store, "Root", StringComparison.Ordinal) ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;

    /// <summary>The location; null for a word that is none.</summary>
    public static StoreLocation? Location(string word) => word.Trim().ToLowerInvariant() switch
    {
        "user" or "currentuser" => StoreLocation.CurrentUser,
        "machine" or "localmachine" => StoreLocation.LocalMachine,
        _ => null,
    };

    /// <summary>Reads <paramref name="store"/> at <paramref name="location"/>; a store that cannot be opened is its sentence.</summary>
    public static CertificateListing Read(string store, StoreLocation location)
    {
        ArgumentNullException.ThrowIfNull(store);
        string where = location == StoreLocation.CurrentUser ? "user" : "machine";
        try
        {
            using var opened = new X509Store(store, location);
            opened.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            // Read once (2026-10-23, the admin review's D9: each Certificates read is a full copy, and the one taken for the count was never
            // disposed); one certificate that will not read is counted, not the whole store's failure (E5).
            var all = opened.Certificates;
            var views = new List<CertificateView>(all.Count);
            int unreadable = 0;
            foreach (var certificate in all)
            {
                using (certificate)
                {
                    try
                    {
                        views.Add(CertificateView.Of(certificate));
                    }
                    catch (CryptographicException)
                    {
                        unreadable++;
                    }
                }
            }

            return new CertificateListing(store, where, views, null, unreadable);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return new CertificateListing(store, where, [], OperatingSystem.IsWindows() ? NetworkText.StoreUnreadable(store, where, ex.Message) : NetworkText.StoreNotKeptHere);
        }
    }

    /// <summary>
    /// The listing narrowed: <paramref name="filter"/> in the subject, the issuer, a name or the thumbprint (case ignored), and
    /// <paramref name="expiringWithinDays"/> to those ending within that many days (expired ones included). Soonest to end first.
    /// </summary>
    public static IReadOnlyList<CertificateView> Narrow(IReadOnlyList<CertificateView> certificates, string? filter, int? expiringWithinDays, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        IEnumerable<CertificateView> kept = certificates;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            string f = filter.Trim();
            kept = kept.Where(c => c.Subject.Contains(f, StringComparison.OrdinalIgnoreCase) || c.Issuer.Contains(f, StringComparison.OrdinalIgnoreCase)
                || c.Sha256.Contains(f, StringComparison.OrdinalIgnoreCase) || (c.Sha1?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || c.Names.Any(n => n.Contains(f, StringComparison.OrdinalIgnoreCase)));
        }

        if (expiringWithinDays is { } days)
        {
            kept = kept.Where(c => c.NotAfter <= now.AddDays(days));
        }

        return kept.OrderBy(c => c.NotAfter).ToList();
    }
}
