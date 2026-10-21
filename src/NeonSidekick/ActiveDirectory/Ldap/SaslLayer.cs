using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Security.Authentication.ExtendedProtection;

namespace NeonSidekick.ActiveDirectory.Ldap;

/// <summary>What one sign-in step gave: the token to send (empty for none), and whether the context is now complete.</summary>
public readonly record struct SaslStep(byte[] Outgoing, bool Complete);

/// <summary>
/// The security mechanism under SASL GSSAPI (2026-10-18, the Mac AD plan): the sign-in's steps, and wrapping once it is complete.
/// <see cref="NegotiateSaslLayer"/> is Kerberos through .NET; tests and the smoke use layers that need no KDC. A failed step throws
/// <see cref="DirectoryException"/>.
/// </summary>
public interface ISaslLayer : IDisposable
{
    SaslStep Step(ReadOnlySpan<byte> incoming);

    byte[] Wrap(ReadOnlySpan<byte> data, bool encrypt, out bool encrypted);

    byte[] Unwrap(ReadOnlySpan<byte> data, out bool wasEncrypted);

    /// <summary>The negotiated flags once complete.</summary>
    bool Signed { get; }

    bool Encrypted { get; }
}

/// <summary>
/// Kerberos through <see cref="NegotiateAuthentication"/> (2026-10-18, the Mac AD plan; <c>Sql/KerberosSignIn</c>'s settings): package
/// <c>Kerberos</c> (the krb5 mechanism itself, never NTLM), mutual authentication, the target <c>ldap/&lt;host&gt;</c> host-based — Phase 0
/// found the explicit <c>ldap/host@REALM</c> form fails on macOS, while the host-based one takes its realm from the host's domain — and
/// encryption and signing requested on 389, nothing under TLS, where AD refuses a context that asks for them. A <c>runas</c> login is an
/// explicit credential; <c>windows</c> the default one (a <c>kinit</c> ticket). Under TLS the channel binding goes with it. A failed step's
/// status is kept in <see cref="Failure"/>, for <see cref="ManagedDirectorySystem"/> to word. Excluded from coverage: it needs a KDC, and
/// is proven by the live tests and the published exe.
/// <para>
/// 2026-10-20, the code review: with an explicit credential .NET runs the AS exchange as it is made, and on a Mac any failure there comes
/// back from the first step as <c>Unsupported</c> (Phase 0, and the smoke's made-up realm): that alone is
/// <see cref="LdapResultCodes.TicketRefused"/>, which the system asks the KDC about. Any other status, or a failure at a later step, came after
/// the password was taken (a service ticket, the DC's mutual reply) and is <see cref="LdapResultCodes.KerberosFailed"/>, never a refused password.
/// </para>
/// </summary>
public sealed class NegotiateSaslLayer : ISaslLayer
{
    public const string Package = "Kerberos";

    private readonly NegotiateAuthentication _auth;
    private readonly bool _explicit;
    private int _steps;

    public NegotiateSaslLayer(DirectoryLogin? login, string host, bool tls, ChannelBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(host);
        _explicit = login is not null;
        _auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = Package,
            Credential = login is null ? CredentialCache.DefaultNetworkCredentials : new NetworkCredential(login.User, login.Password),
            TargetName = ServiceName(host),
            RequiredProtectionLevel = tls ? ProtectionLevel.None : ProtectionLevel.EncryptAndSign,
            RequireMutualAuthentication = true,
            Binding = binding,
        });
    }

    /// <summary>The service name a DC is asked for: <c>ldap/</c> and its host name in lower case (DNS ignores case; AD and Samba match either).</summary>
    public static string ServiceName(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return "ldap/" + host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>The status of the step that failed, or null.</summary>
    public NegotiateAuthenticationStatusCode? Failure { get; private set; }

    public bool Signed => _auth.IsSigned;

    public bool Encrypted => _auth.IsEncrypted;

    public SaslStep Step(ReadOnlySpan<byte> incoming)
    {
        byte[]? blob = _auth.GetOutgoingBlob(incoming, out var status);
        _steps++;
        if (status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
        {
            Failure = status;
            int code = FailureCode(_explicit, _steps, status);
            throw new DirectoryException(code, "the Kerberos sign-in failed (" + status + ")");
        }

        return new SaslStep(blob ?? [], status == NegotiateAuthenticationStatusCode.Completed);
    }

    /// <summary>
    /// A failed step's code: <see cref="LdapResultCodes.TicketRefused"/> only for an explicit credential's first step answered
    /// <c>Unsupported</c> (its AS exchange failed), else <see cref="LdapResultCodes.KerberosFailed"/>.
    /// </summary>
    public static int FailureCode(bool explicitCredential, int step, NegotiateAuthenticationStatusCode status) =>
        explicitCredential && step == 1 && status == NegotiateAuthenticationStatusCode.Unsupported ? LdapResultCodes.TicketRefused : LdapResultCodes.KerberosFailed;

    public byte[] Wrap(ReadOnlySpan<byte> data, bool encrypt, out bool encrypted)
    {
        var output = new ArrayBufferWriter<byte>();
        var status = _auth.Wrap(data, output, encrypt, out encrypted);
        return status == NegotiateAuthenticationStatusCode.Completed
            ? output.WrittenSpan.ToArray()
            : throw new DirectoryException(LdapResultCodes.DecodingError, "a message could not be wrapped (" + status + ")");
    }

    public byte[] Unwrap(ReadOnlySpan<byte> data, out bool wasEncrypted)
    {
        var output = new ArrayBufferWriter<byte>();
        var status = _auth.Unwrap(data, output, out wasEncrypted);
        return status == NegotiateAuthenticationStatusCode.Completed
            ? output.WrittenSpan.ToArray()
            : throw new DirectoryException(LdapResultCodes.DecodingError, "a reply could not be unwrapped (" + status + ")");
    }

    public void Dispose() => _auth.Dispose();
}

/// <summary>Which SASL security layer a bind ended with (RFC 4752's bit values).</summary>
public enum SaslSecurity : byte
{
    None = 1,
    Integrity = 2,
    Confidentiality = 4,
}

/// <summary>
/// SASL GSSAPI's exchange (2026-10-18, the Mac AD plan; RFC 4752 §3.1 and §3.3) over any <see cref="ISaslLayer"/>:
/// <list type="number">
/// <item>Send each token in a SASL bind while the server answers <c>saslBindInProgress</c>; once the context is complete, its last
/// token, or an empty response.</item>
/// <item>The server's challenge must unwrap to exactly 4 bytes: a layer bit-mask (unknown bits dropped) and the most it receives,
/// which must be 0 when it offers no layer.</item>
/// <item>Choose: on 389 the strongest offered, confidentiality, else integrity, never none — a connection on 389 is never unsigned, as
/// Windows' always signs and seals; under TLS, none.</item>
/// <item>Reply, wrapped without encryption (conf_flag FALSE): the chosen bit, this client's maximum (0 with no layer), no
/// authorization id.</item>
/// </list>
/// A chosen confidentiality must be what the context negotiated (<see cref="ISaslLayer.Encrypted"/>), since Heimdal reports
/// <c>conf_state</c> as requested, whatever was negotiated. Every refusal is a <see cref="DirectoryException"/>.
/// </summary>
public static class SaslGssapiExchange
{
    /// <summary>The most a wrapped buffer from the server may be: the 3-byte maximum this client offers.</summary>
    public const int ClientMaximum = 0xFFFFFF;

    /// <summary>How many bind round trips are allowed before the exchange is taken as broken.</summary>
    public const int MaxRounds = 8;

    /// <summary>Binds <paramref name="connection"/> through <paramref name="layer"/>; the layer chosen and the server's maximum.</summary>
    public static (SaslSecurity Security, int ServerMaximum) Bind(LdapConnection connection, ISaslLayer layer, bool tls)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(layer);
        byte[] incoming = [];
        bool complete = false;
        for (int round = 0; round < MaxRounds; round++)
        {
            byte[] outgoing;
            SaslSecurity? chosen = null;
            int serverMaximum = 0;
            if (!complete)
            {
                var step = layer.Step(incoming);
                complete = step.Complete;
                outgoing = step.Outgoing;
            }
            else
            {
                (chosen, serverMaximum) = Choose(layer.Unwrap(incoming, out _), tls, layer);
                byte[] reply = Reply(chosen.Value);
                outgoing = layer.Wrap(reply, encrypt: false, out _);
            }

            var answer = connection.Bind(outgoing);
            if (answer.ResultCode == LdapResultCodes.Success)
            {
                if (chosen is { } done)
                {
                    return (done, serverMaximum);
                }

                throw new DirectoryException(LdapResultCodes.AuthUnknown, "the server ended the bind without a security layer offer");
            }

            if (answer.ResultCode != LdapResultCodes.SaslBindInProgress)
            {
                throw new DirectoryException(answer.ResultCode, "the bind was refused" + (answer.Diagnostic.Length > 0 ? ": " + answer.Diagnostic : ""));
            }

            if (chosen is not null)
            {
                throw new DirectoryException(LdapResultCodes.DecodingError, "the server asked for more after the security layer was chosen");
            }

            incoming = answer.SaslCredentials ?? [];
        }

        throw new DirectoryException(LdapResultCodes.DecodingError, "the bind took too many rounds");
    }

    /// <summary>The layer chosen from the server's unwrapped offer, and its maximum; a malformed or unacceptable offer refused.</summary>
    public static (SaslSecurity Security, int ServerMaximum) Choose(ReadOnlySpan<byte> offer, bool tls, ISaslLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (offer.Length != 4)
        {
            throw new DirectoryException(LdapResultCodes.DecodingError, "the security layer offer was not 4 bytes");
        }

        int mask = offer[0] & 0x07;
        int maximum = (offer[1] << 16) | (offer[2] << 8) | offer[3];
        if (mask == (int)SaslSecurity.None && maximum != 0)
        {
            throw new DirectoryException(LdapResultCodes.DecodingError, "the server offered no security layer but a buffer size");
        }

        if (tls)
        {
            return (mask & (int)SaslSecurity.None) != 0
                ? (SaslSecurity.None, 0)
                : throw new DirectoryException(LdapResultCodes.AuthUnknown, "the server offered no plain layer under TLS");
        }

        if ((mask & (int)SaslSecurity.Confidentiality) != 0 && layer.Encrypted)
        {
            return (SaslSecurity.Confidentiality, maximum);
        }

        if ((mask & (int)SaslSecurity.Integrity) != 0 && layer.Signed)
        {
            return (SaslSecurity.Integrity, maximum);
        }

        throw new DirectoryException(LdapResultCodes.AuthUnknown, "the server offered no signed or sealed layer on 389");
    }

    /// <summary>The reply: the chosen bit, this client's 3-byte maximum (0 with no layer), no authorization id.</summary>
    public static byte[] Reply(SaslSecurity security) =>
        security == SaslSecurity.None ? [(byte)SaslSecurity.None, 0, 0, 0] : [(byte)security, 0xFF, 0xFF, 0xFF];
}
