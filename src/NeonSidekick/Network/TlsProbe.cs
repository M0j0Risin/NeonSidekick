using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NeonSidekick.Web;

namespace NeonSidekick.Network;

/// <summary>One certificate as an answer shows it.</summary>
/// <remarks><c>Sha1</c> (2026-10-23, the admin review's H10): the thumbprint Windows' own tools show, which a filter matches too.</remarks>
public sealed record CertificateView(string Subject, string Issuer, IReadOnlyList<string> Names, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Sha256, string SerialNumber, bool HasPrivateKey = false, string? KeyAlgorithm = null, bool NamesUnreadable = false, string? Sha1 = null)
{
    /// <summary>A certificate read into its view (nothing exported: the key's presence is a yes or no).</summary>
    public static CertificateView Of(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var names = new List<string>();
        bool unreadable = false;
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                // A malformed name list is said, not thrown (2026-10-23, the admin review's E5: it failed net_tls whole, and one such
                // certificate made a whole store unreadable).
                try
                {
                    names.AddRange(san.EnumerateDnsNames());
                    names.AddRange(san.EnumerateIPAddresses().Select(a => a.ToString()));
                }
                catch (CryptographicException)
                {
                    unreadable = true;
                }
            }
        }

        bool hasKey;
        try
        {
            hasKey = certificate.HasPrivateKey;
        }
        catch (CryptographicException)
        {
            hasKey = false;
        }

        return new CertificateView(
            certificate.Subject,
            certificate.Issuer,
            names,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            certificate.GetCertHashString(HashAlgorithmName.SHA256),
            certificate.SerialNumber,
            hasKey,
            KeyAlgorithmOf(certificate),
            unreadable,
            certificate.Thumbprint);
    }

    private static string? KeyAlgorithmOf(X509Certificate2 certificate)
    {
        try
        {
            return certificate.PublicKey.Oid.FriendlyName;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Whole days from <paramref name="now"/> to the end of its validity (negative once expired).</summary>
    public int DaysLeft(DateTimeOffset now) => (int)Math.Floor((NotAfter - now).TotalDays);
}

/// <summary>What a TLS handshake showed.</summary>
public sealed record TlsReport(
    string Host,
    IPAddress Address,
    int Port,
    string ServerName,
    StartTls Preamble,
    string Protocol,
    string Cipher,
    CertificateView? Leaf,
    IReadOnlyList<string> Chain,
    IReadOnlyList<string> ChainStatus,
    SslPolicyErrors PolicyErrors);

/// <summary>
/// <c>net_tls</c>'s handshake (2026-10-15, the admin-tools plan): a TCP connection to the first admitted address to answer (raced, <see cref="Web.HappyEyeballs"/>), the port's
/// preamble (<see cref="TlsPreamble"/>), then <see cref="SslStream"/> as a client for <c>server_name</c> — every certificate accepted so the
/// report can say what is wrong with it, the chain built with <see cref="X509ChainPolicy.DisableCertificateDownloads"/> and revocation
/// <see cref="X509RevocationMode.NoCheck"/> (no fetch of anyone's AIA or CRL from here). The protocol, the cipher, the leaf, the chain and
/// its status, the policy errors. 10 s for the whole of it; nothing past the handshake is read and no credential is ever sent.
/// </summary>
public static class TlsProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<TlsReport> RunAsync(string host, IReadOnlyList<IPAddress> addresses, int port, string serverName, StartTls preamble, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(addresses);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Timeout);
        var token = budget.Token;

        // Raced, not one after another (2026-10-19, the network review: a dead IPv6 route held the whole 10 s on the first address while
        // IPv4 would have answered, and an attempt cancelled by the budget or Esc was never disposed). Each attempt disposes its own socket
        // on a failure or a cancel; a loser that connects anyway is closed (HappyEyeballs).
        var socket = await HappyEyeballs.ConnectAsync(addresses, port, HappyEyeballs.AttemptDelay, HappyEyeballs.ConnectOneAsync, token).ConfigureAwait(false);

        // Owned at once (2026-10-23, the admin review's U12: a throw from RemoteEndPoint before the stream took it leaked the socket).
        await using var network = new NetworkStream(socket, ownsSocket: true);
        var reached = socket.RemoteEndPoint is IPEndPoint remote ? remote.Address : addresses[0];
        await TlsPreamble.RunAsync(network, preamble, port, token).ConfigureAwait(false);
        var chain = new List<string>();
        var status = new List<string>();
        var errors = SslPolicyErrors.None;
        CertificateView? leaf = null;
        await using var ssl = new SslStream(network, leaveInnerStreamOpen: true, (_, certificate, built, policyErrors) =>
        {
            errors = policyErrors;
            if (certificate is X509Certificate2 full)
            {
                leaf = CertificateView.Of(full);
            }
            else if (certificate is not null)
            {
                using var copy = new X509Certificate2(certificate);
                leaf = CertificateView.Of(copy);
            }

            if (built is not null)
            {
                foreach (var element in built.ChainElements)
                {
                    chain.Add(element.Certificate.Subject);
                }

                foreach (var flag in built.ChainStatus)
                {
                    if (flag.Status != X509ChainStatusFlags.NoError)
                    {
                        status.Add(flag.Status.ToString() + (string.IsNullOrWhiteSpace(flag.StatusInformation) ? "" : ": " + flag.StatusInformation.Trim()));
                    }
                }
            }

            return true;
        });
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = serverName,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            CertificateChainPolicy = new X509ChainPolicy
            {
                DisableCertificateDownloads = true,
                RevocationMode = X509RevocationMode.NoCheck,
            },
        };
        await ssl.AuthenticateAsClientAsync(options, token).ConfigureAwait(false);
        string cipher;
        try
        {
            cipher = ssl.NegotiatedCipherSuite.ToString();
        }
        catch (NotSupportedException)
        {
            cipher = "(not reported here)";
        }

        return new TlsReport(host, reached, port, serverName, preamble, ProtocolName(ssl.SslProtocol), cipher, leaf, chain, status, errors);
    }

    /// <summary>A protocol as people say it: <c>TLS 1.3</c>.</summary>
    public static string ProtocolName(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
        Tls11 => "TLS 1.1",
        Tls10 => "TLS 1.0",
        _ => protocol.ToString(),
    };

    // The obsolete members' values (SYSLIB0039): naming what an old server negotiated, never asking for it, without a suppression.
    private const SslProtocols Tls11 = (SslProtocols)0x300;
    private const SslProtocols Tls10 = (SslProtocols)0xC0;

    /// <summary>The policy errors in words: <c>name mismatch, chain errors</c>; <c>none</c> for none.</summary>
    public static string PolicyWords(SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return "none";
        }

        var words = new List<string>(3);
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            words.Add("no certificate");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            words.Add("name mismatch");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            words.Add("chain errors");
        }

        return string.Join(", ", words);
    }

    /// <summary>A port as text, for the lines.</summary>
    public static string Port(int port) => port.ToString(CultureInfo.InvariantCulture);
}
