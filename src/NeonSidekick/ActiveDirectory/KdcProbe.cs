using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

using NeonSidekick.ActiveDirectory.Ldap;

namespace NeonSidekick.ActiveDirectory;

/// <summary>What the DC's KDC said of an account asked about with no password (<see cref="KdcProbe"/>).</summary>
public enum KdcVerdict
{
    /// <summary>Nothing answered on port 88 in time.</summary>
    Unreachable,

    /// <summary><c>KDC_ERR_PREAUTH_REQUIRED</c>: the KDC knows the account and wants its password; the clock is within <see cref="KdcProbe.MaxSkew"/>.</summary>
    KnowsTheAccount,

    /// <summary><c>KDC_ERR_CLIENT_REVOKED</c>: the account is locked, disabled or expired.</summary>
    Revoked,

    /// <summary><c>KDC_ERR_C_PRINCIPAL_UNKNOWN</c>: no such account in the realm.</summary>
    UnknownAccount,

    /// <summary>The KDC's time is more than <see cref="KdcProbe.MaxSkew"/> from this machine's (or it said <c>KRB_AP_ERR_SKEW</c>).</summary>
    ClockSkew,

    /// <summary>Anything else: another error, a ticket handed over with no password asked (an account without preauthentication), bytes that are no Kerberos.</summary>
    Other,
}

/// <summary>The probe's answer: the verdict, the KDC's error code when it sent one, and how far its clock is from this one's (positive: ahead).</summary>
public readonly record struct KdcAnswer(KdcVerdict Verdict, int? ErrorCode = null, TimeSpan? Skew = null);

/// <summary>
/// Asks a DC's KDC about an account without a password (2026-10-20, the code review; the user's call that day): one AS-REQ over TCP to port 88
/// (RFC 4120 §7.2.2: a 4-byte length, then the message) for <c>krbtgt/REALM</c> with no preauthentication. A KDC that wants the password answers
/// <c>KDC_ERR_PREAUTH_REQUIRED</c> with its own time, and nothing of the password goes out, so the account's bad-password count is untouched.
/// <see cref="ManagedDirectorySystem"/> asks it when a <c>runas</c> sign-in could not get its first ticket
/// (<see cref="LdapResultCodes.TicketRefused"/>), which on a Mac hides why: until then any answer on port 88 counted as a refused password, which
/// held a right password after a skewed clock or an unknown service name, and on a Mac whose own KDC holds port 88 on <c>localhost</c> always did.
/// It cannot tell a wrong password itself; what is left once the KDC knows the account and the clock agrees is the password (or a <c>krb5.conf</c>
/// naming another KDC that failed, which this DC's cannot show). The message is built with <see cref="AsnWriter"/> (DER) and holds no secret.
/// It lives outside <c>Ldap/</c>, whose files <c>LdapManagedTests</c> holds to writing no LDAP operation but <c>LdapMessages</c>' three.
/// </summary>
public static class KdcProbe
{
    /// <summary>Kerberos' usual tolerance between the client's and the KDC's clocks.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    /// <summary>How long the probe may take, connect and answer together.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    /// <summary>The largest answer read.</summary>
    public const int MaxReply = 64 * 1024;

    public const int PreauthRequired = 25;
    public const int ClientRevoked = 18;
    public const int PrincipalUnknown = 6;
    public const int ApSkew = 37;

    /// <summary>Splits <c>name@REALM</c>; null for anything else.</summary>
    public static (string Name, string Realm)? Split(string principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        int at = principal.LastIndexOf('@');
        return at > 0 && at < principal.Length - 1 ? (principal[..at], principal[(at + 1)..]) : null;
    }

    /// <summary>Asks <paramref name="address"/>'s port 88 about <paramref name="principal"/> (<c>name@REALM</c>), within <see cref="Timeout"/>.</summary>
    public static KdcAnswer Ask(string address, string principal) => Ask(address, 88, principal);

    /// <summary>The same on another port (the tests' loopback KDC).</summary>
    internal static KdcAnswer Ask(string address, int port, string principal)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (Split(principal) is not { } parts)
        {
            return new KdcAnswer(KdcVerdict.Other);
        }

        byte[] request = AsRequest(parts.Name, parts.Realm, DateTimeOffset.UtcNow, (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue));
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            using var client = new TcpClient { NoDelay = true };
            client.ConnectAsync(address, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            var stream = client.GetStream();
            byte[] framed = new byte[4 + request.Length];
            BinaryPrimitives.WriteInt32BigEndian(framed, request.Length);
            request.CopyTo(framed, 4);
            stream.WriteAsync(framed, timeout.Token).AsTask().GetAwaiter().GetResult();
            byte[] head = new byte[4];
            stream.ReadExactlyAsync(head, timeout.Token).AsTask().GetAwaiter().GetResult();
            int length = BinaryPrimitives.ReadInt32BigEndian(head);
            if (length <= 0 || length > MaxReply)
            {
                return new KdcAnswer(KdcVerdict.Other);
            }

            byte[] reply = new byte[length];
            stream.ReadExactlyAsync(reply, timeout.Token).AsTask().GetAwaiter().GetResult();
            return Read(reply, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return new KdcAnswer(KdcVerdict.Unreachable);
        }
    }

    /// <summary>
    /// The AS-REQ (RFC 4120 §5.4.1): pvno 5, msg-type 10, no padata, no options, <paramref name="name"/> as an NT-PRINCIPAL in
    /// <paramref name="realm"/>, <c>krbtgt/REALM</c>, a day's lifetime, <paramref name="nonce"/>, and AES-256, AES-128 and RC4 asked for.
    /// </summary>
    public static byte[] AsRequest(string name, string realm, DateTimeOffset now, uint nonce)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(realm);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.Application, 10, isConstructed: true)))
        using (writer.PushSequence())
        {
            using (writer.PushSequence(Field(1)))
            {
                writer.WriteInteger(5);
            }

            using (writer.PushSequence(Field(2)))
            {
                writer.WriteInteger(10);
            }

            using (writer.PushSequence(Field(4)))
            using (writer.PushSequence())
            {
                using (writer.PushSequence(Field(0)))
                {
                    writer.WriteBitString(new byte[4]);
                }

                using (writer.PushSequence(Field(1)))
                {
                    WritePrincipal(writer, 1, [name]);
                }

                using (writer.PushSequence(Field(2)))
                {
                    WriteGeneralString(writer, realm);
                }

                using (writer.PushSequence(Field(3)))
                {
                    WritePrincipal(writer, 2, ["krbtgt", realm]);
                }

                using (writer.PushSequence(Field(5)))
                {
                    writer.WriteGeneralizedTime(now.AddDays(1), omitFractionalSeconds: true);
                }

                using (writer.PushSequence(Field(7)))
                {
                    writer.WriteInteger(nonce);
                }

                using (writer.PushSequence(Field(8)))
                using (writer.PushSequence())
                {
                    writer.WriteInteger(18);
                    writer.WriteInteger(17);
                    writer.WriteInteger(23);
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// The KDC's answer read against <paramref name="now"/>: a KRB-ERROR's code and time (RFC 4120 §5.9.1) as a verdict; an AS-REP, or anything
    /// unreadable, <see cref="KdcVerdict.Other"/>.
    /// </summary>
    public static KdcAnswer Read(ReadOnlyMemory<byte> reply, DateTimeOffset now)
    {
        try
        {
            var reader = new AsnReader(reply, AsnEncodingRules.BER);
            if (!reader.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.Application, 30, isConstructed: true)))
            {
                return new KdcAnswer(KdcVerdict.Other);
            }

            var error = reader.ReadSequence(new Asn1Tag(TagClass.Application, 30, isConstructed: true)).ReadSequence();
            DateTimeOffset? stime = null;
            int? code = null;
            while (error.HasData)
            {
                var tag = error.PeekTag();
                var field = error.ReadSequence(tag);
                if (tag.TagClass != TagClass.ContextSpecific)
                {
                    continue;
                }

                if (tag.TagValue == 4)
                {
                    stime = field.ReadGeneralizedTime();
                }
                else if (tag.TagValue == 6 && field.TryReadInt32(out int value))
                {
                    code = value;
                }
            }

            TimeSpan? skew = stime is { } kdc ? kdc - now : null;
            return code switch
            {
                null => new KdcAnswer(KdcVerdict.Other),
                ApSkew => new KdcAnswer(KdcVerdict.ClockSkew, code, skew),
                _ when skew is { } off && off.Duration() > MaxSkew => new KdcAnswer(KdcVerdict.ClockSkew, code, skew),
                PreauthRequired when skew is not null => new KdcAnswer(KdcVerdict.KnowsTheAccount, code, skew),
                ClientRevoked => new KdcAnswer(KdcVerdict.Revoked, code, skew),
                PrincipalUnknown => new KdcAnswer(KdcVerdict.UnknownAccount, code, skew),
                _ => new KdcAnswer(KdcVerdict.Other, code, skew),
            };
        }
        catch (Exception ex) when (ex is AsnContentException or OverflowException or ArgumentException or InvalidOperationException or FormatException)
        {
            return new KdcAnswer(KdcVerdict.Other);
        }
    }

    private static Asn1Tag Field(int number) => new(TagClass.ContextSpecific, number, isConstructed: true);

    private static void WritePrincipal(AsnWriter writer, int nameType, IReadOnlyList<string> parts)
    {
        using (writer.PushSequence())
        {
            using (writer.PushSequence(Field(0)))
            {
                writer.WriteInteger(nameType);
            }

            using (writer.PushSequence(Field(1)))
            using (writer.PushSequence())
            {
                foreach (string part in parts)
                {
                    WriteGeneralString(writer, part);
                }
            }
        }
    }

    /// <summary>A KerberosString (GeneralString, tag 27): the octet string's encoding under that tag, since the writer refuses the universal tag.</summary>
    private static void WriteGeneralString(AsnWriter writer, string text)
    {
        var inner = new AsnWriter(AsnEncodingRules.DER);
        inner.WriteOctetString(Encoding.UTF8.GetBytes(text));
        byte[] encoded = inner.Encode();
        encoded[0] = (byte)UniversalTagNumber.GeneralString;
        writer.WriteEncodedValue(encoded);
    }
}
