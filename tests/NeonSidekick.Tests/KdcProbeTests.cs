using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.ActiveDirectory.Ldap;

namespace NeonSidekick.Tests;

/// <summary>
/// The KDC asked about an account with no password (2026-10-20, the code review; <see cref="KdcProbe"/>): the AS-REQ's shape, a KRB-ERROR's code
/// and time read as a verdict, the round trip over a loopback KDC, and the clock's wording. Portable: no KDC, no network beyond loopback.
/// </summary>
public sealed class KdcProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);

    private static Asn1Tag Field(int n) => new(TagClass.ContextSpecific, n, isConstructed: true);

    [Fact]
    public void TheRequest_IsAnAsReq_ForTheAccountsTgt_WithNoPreauthentication()
    {
        byte[] request = KdcProbe.AsRequest("reader", "LAB.NEON.TEST", Now, 1234);

        var req = new AsnReader(request, AsnEncodingRules.DER).ReadSequence(new Asn1Tag(TagClass.Application, 10, true)).ReadSequence();
        Assert.Equal(5, (int)req.ReadSequence(Field(1)).ReadInteger());
        Assert.Equal(10, (int)req.ReadSequence(Field(2)).ReadInteger());
        Assert.False(req.PeekTag().HasSameClassAndValue(Field(3)));   // no padata: nothing of a password goes out
        var body = req.ReadSequence(Field(4)).ReadSequence();
        Assert.False(req.HasData);
        body.ReadSequence(Field(0)).ReadBitString(out _);
        Assert.Equal(["reader"], Names(body.ReadSequence(Field(1)), 1));
        Assert.Equal("LAB.NEON.TEST", GeneralString(body.ReadSequence(Field(2))));
        Assert.Equal(["krbtgt", "LAB.NEON.TEST"], Names(body.ReadSequence(Field(3)), 2));
        Assert.Equal(Now.AddDays(1), body.ReadSequence(Field(5)).ReadGeneralizedTime());
        Assert.Equal(1234, (int)body.ReadSequence(Field(7)).ReadInteger());
        var etypes = body.ReadSequence(Field(8)).ReadSequence();
        Assert.Equal([18, 17, 23], new[] { (int)etypes.ReadInteger(), (int)etypes.ReadInteger(), (int)etypes.ReadInteger() });
    }

    [Theory]
    [InlineData(KdcProbe.PreauthRequired, 0, KdcVerdict.KnowsTheAccount)]
    [InlineData(KdcProbe.PreauthRequired, 4, KdcVerdict.KnowsTheAccount)]
    [InlineData(KdcProbe.PreauthRequired, 12, KdcVerdict.ClockSkew)]
    [InlineData(KdcProbe.PreauthRequired, -12, KdcVerdict.ClockSkew)]
    [InlineData(KdcProbe.ApSkew, 0, KdcVerdict.ClockSkew)]
    [InlineData(KdcProbe.ClientRevoked, 0, KdcVerdict.Revoked)]
    [InlineData(KdcProbe.PrincipalUnknown, 0, KdcVerdict.UnknownAccount)]
    [InlineData(68, 0, KdcVerdict.Other)]   // KDC_ERR_WRONG_REALM
    [InlineData(14, 0, KdcVerdict.Other)]   // KDC_ERR_ETYPE_NOSUPP
    public void AKrbError_IsReadAsAVerdict(int code, int kdcMinutesAhead, KdcVerdict expected)
    {
        var answer = KdcProbe.Read(KrbError(code, Now.AddMinutes(kdcMinutesAhead)), Now);

        Assert.Equal(expected, answer.Verdict);
        Assert.Equal(code, answer.ErrorCode);
        Assert.Equal(TimeSpan.FromMinutes(kdcMinutesAhead), answer.Skew);
    }

    [Fact]
    public void AnAsRep_OrBytesThatAreNoKerberos_AreOther()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.Application, 11, true)))
        using (writer.PushSequence())
        {
            writer.WriteInteger(5);
        }

        Assert.Equal(KdcVerdict.Other, KdcProbe.Read(writer.Encode(), Now).Verdict);
        Assert.Equal(KdcVerdict.Other, KdcProbe.Read(new byte[] { 0x7E, 0x05, 0x30, 0x03, 0x02 }, Now).Verdict);
        Assert.Equal(KdcVerdict.Other, KdcProbe.Read(Array.Empty<byte>(), Now).Verdict);
    }

    /// <summary>The round trip: the request framed for TCP, the KDC's framed answer read.</summary>
    [Fact]
    public async Task Ask_OverTcp_ReadsTheKdcsAnswer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var kdc = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var head = new byte[4];
            await stream.ReadExactlyAsync(head);
            var request = new byte[BinaryPrimitives.ReadInt32BigEndian(head)];
            await stream.ReadExactlyAsync(request);
            var body = new AsnReader(request, AsnEncodingRules.DER).ReadSequence(new Asn1Tag(TagClass.Application, 10, true)).ReadSequence();
            body.ReadSequence(Field(1));
            body.ReadSequence(Field(2));
            var reqBody = body.ReadSequence(Field(4)).ReadSequence();
            reqBody.ReadSequence(Field(0));
            string name = Names(reqBody.ReadSequence(Field(1)), 1)[0];
            byte[] reply = KrbError(name == "reader" ? KdcProbe.PreauthRequired : KdcProbe.PrincipalUnknown, DateTimeOffset.UtcNow);
            var framed = new byte[4 + reply.Length];
            BinaryPrimitives.WriteInt32BigEndian(framed, reply.Length);
            reply.CopyTo(framed, 4);
            await stream.WriteAsync(framed);
        });

        var answer = KdcProbe.Ask("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "reader@LAB.NEON.TEST");
        await kdc.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(KdcVerdict.KnowsTheAccount, answer.Verdict);
    }

    [Fact]
    public void Ask_WithNothingListening_IsUnreachable_AndANameWithNoRealm_IsOther()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        Assert.Equal(KdcVerdict.Unreachable, KdcProbe.Ask("127.0.0.1", port, "reader@LAB.NEON.TEST").Verdict);
        Assert.Equal(KdcVerdict.Other, KdcProbe.Ask("127.0.0.1", port, "LAB\\reader").Verdict);
    }

    [Fact]
    public void TheClock_IsWorded_InWholeMinutes()
    {
        Assert.Equal("12 minutes behind", DirectoryText.ClockOff(TimeSpan.FromMinutes(11.2)));
        Assert.Equal("1 minute ahead of", DirectoryText.ClockOff(TimeSpan.FromSeconds(-20)));
        Assert.Equal("Error: this computer's clock is 12 minutes behind dc1.lab.example's, more than the 5 minutes Kerberos allows, so the sign-in to lab.example cannot work. Set the clock to the network's time and try again.",
            DirectoryErrors.Sentence(LdapResultCodes.ClockSkew, "lab.example", "dc1.lab.example", 389, "reader@LAB.EXAMPLE", DirectoryText.ClockOff(TimeSpan.FromMinutes(12))));
    }

    [Fact]
    public void AnUnknownAccount_IsWorded_NamingTheKdc()
    {
        var refusal = ManagedDirectorySystem.Refusal(new KdcAnswer(KdcVerdict.UnknownAccount, 6, TimeSpan.Zero), "reader@LAB.NEON.TEST", "localhost");
        Assert.Equal(LdapResultCodes.UnknownAccount, refusal.Code);
        Assert.Equal("Error: the KDC on localhost does not know reader@LAB.NEON.TEST. Check the account and its realm (name@REALM), and that localhost is a DC of lab.neon.test whose own KDC answers on port 88.",
            DirectoryErrors.Sentence(refusal.Code, "lab.neon.test", "localhost", 389, "reader@LAB.NEON.TEST", refusal.Message));
    }

    [Fact]
    public void Refusal_HoldsOnlyWhatTheKdcSaysOfTheAccount()
    {
        foreach (KdcVerdict verdict in Enum.GetValues<KdcVerdict>())
        {
            var refusal = ManagedDirectorySystem.Refusal(new KdcAnswer(verdict, 25, TimeSpan.FromMinutes(9)), "reader@LAB.NEON.TEST", "dc1.lab.neon.test");
            bool held = verdict is KdcVerdict.KnowsTheAccount or KdcVerdict.Revoked;
            Assert.Equal(held, DirectoryAccess.RefusedPassword(refusal.Code, refusal.Message));
        }

        Assert.Equal("9 minutes behind", ManagedDirectorySystem.Refusal(new KdcAnswer(KdcVerdict.ClockSkew, 37, TimeSpan.FromMinutes(9)), "r@X", "dc").Message);
        Assert.Contains("answered error 68", ManagedDirectorySystem.Refusal(new KdcAnswer(KdcVerdict.Other, 68), "r@X", "dc").Message, StringComparison.Ordinal);
    }

    private static List<string> Names(AsnReader field, int nameType)
    {
        var principal = field.ReadSequence();
        Assert.Equal(nameType, (int)principal.ReadSequence(Field(0)).ReadInteger());
        var strings = principal.ReadSequence(Field(1)).ReadSequence();
        var names = new List<string>();
        while (strings.HasData)
        {
            names.Add(GeneralString(strings));
        }

        return names;
    }

    /// <summary>A short GeneralString (tag 27), read by hand: the reader takes no octet string under another universal tag.</summary>
    private static string GeneralString(AsnReader reader)
    {
        var encoded = reader.ReadEncodedValue().Span;
        Assert.Equal((byte)UniversalTagNumber.GeneralString, encoded[0]);
        Assert.Equal(encoded.Length - 2, encoded[1]);
        return Encoding.UTF8.GetString(encoded[2..]);
    }

    /// <summary>A KRB-ERROR (RFC 4120 §5.9.1) with <paramref name="code"/> and the KDC's time.</summary>
    private static byte[] KrbError(int code, DateTimeOffset stime)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.Application, 30, true)))
        using (writer.PushSequence())
        {
            using (writer.PushSequence(Field(0)))
            {
                writer.WriteInteger(5);
            }

            using (writer.PushSequence(Field(1)))
            {
                writer.WriteInteger(30);
            }

            using (writer.PushSequence(Field(4)))
            {
                writer.WriteGeneralizedTime(stime, omitFractionalSeconds: true);
            }

            using (writer.PushSequence(Field(5)))
            {
                writer.WriteInteger(0);
            }

            using (writer.PushSequence(Field(6)))
            {
                writer.WriteInteger(code);
            }

            using (writer.PushSequence(Field(9)))
            {
                writer.WriteCharacterString(UniversalTagNumber.IA5String, "LAB.NEON.TEST");   // a GeneralString on the wire; skipped either way
            }
        }

        return writer.Encode();
    }
}
