using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.ActiveDirectory.Ldap;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>ad:ldap</c> (2026-10-18, the Mac AD plan): the managed LDAP client on the published binary, on every system — an in-process
    /// loopback server answers a SASL bind (two tokens, the 4-byte offer), a RootDSE and a two-page search, through the real
    /// <see cref="LdapConnection"/>, BER code and <see cref="SaslGssapiExchange"/> over <see cref="LoopbackSaslLayer"/>, which needs no KDC.
    /// On a Mac it also starts a Kerberos sign-in for a made-up realm through <see cref="NegotiateSaslLayer"/>, which must fail with a status,
    /// never a crash, proving .NET's GSS shim in the trimmed exe.
    /// </summary>
    public static SmokeCheck ProbeLdapClient()
    {
        const string name = "ad:ldap";
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = Task.Run(() => ServeLdap(listener));
            int entries;
            SaslSecurity security;
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var connection = new LdapConnection(client.GetStream()) { ReadTimeout = TimeSpan.FromSeconds(10) };
                using var layer = new LoopbackSaslLayer();
                (security, int maximum) = SaslGssapiExchange.Bind(connection, layer, tls: false);
                connection.StartSecurity(layer, security, maximum);
                var session = ManagedDirectorySystem.SessionOver(connection);
                string? host = session.RootDse().Text("dnsHostName");
                var results = session.Search("DC=smoke,DC=invalid", DirectoryScope.Subtree, "(&(objectClass=user)(userAccountControl:1.2.840.113556.1.4.803:=2))", ["sAMAccountName"], 100, paged: true);
                entries = results.Entries.Count;
                if (host != "dc.smoke.invalid" || entries != 2)
                {
                    return new SmokeCheck(name, false, $"the loopback read gave {host} and {entries.ToString(System.Globalization.CultureInfo.InvariantCulture)} entries");
                }
            }

            server.Wait(TimeSpan.FromSeconds(10));
            string kerberos = "";
            if (OperatingSystem.IsMacOS())
            {
                using var gss = new NegotiateSaslLayer(new DirectoryLogin("nobody@NEONSIDEKICK.INVALID", "x"), "dc.neonsidekick.invalid", tls: false, binding: null);
                try
                {
                    gss.Step([]);
                    return new SmokeCheck(name, false, "a Kerberos sign-in for a made-up realm did not fail");
                }
                catch (DirectoryException)
                {
                    kerberos = $"; a made-up realm's Kerberos sign-in refused ({gss.Failure})";
                }
            }

            return new SmokeCheck(name, true, $"loopback: bound {security}, RootDSE read, {entries.ToString(System.Globalization.CultureInfo.InvariantCulture)} entries over 2 pages{kerberos}");
        }
        catch (Exception ex) when (ex is DirectoryException or SocketException or IOException or AggregateException)
        {
            return new SmokeCheck(name, false, ex.Message);
        }
    }

    /// <summary>The smoke's server: the bind, a RootDSE, two pages; its replies wrapped as <see cref="LoopbackSaslLayer"/> wraps.</summary>
    private static void ServeLdap(TcpListener listener)
    {
        using var client = listener.AcceptTcpClient();
        using var stream = client.GetStream();
        int first = ReadPlainId(stream);
        stream.Write(LdapReply(first, 1, 14, creds: "s1"u8.ToArray()));
        int second = ReadPlainId(stream);
        stream.Write(LdapReply(second, 1, 14, creds: [LoopbackSaslLayer.Mark, 0, 7, 0, 0x10, 0]));
        int third = ReadPlainId(stream);
        stream.Write(LdapReply(third, 1, 0));
        int root = ReadWrappedId(stream);
        WriteWrapped(stream, LdapEntry(root, "", ("dnsHostName", "dc.smoke.invalid")));
        WriteWrapped(stream, LdapReply(root, 5, 0));
        for (int page = 0; page < 2; page++)
        {
            int id = ReadWrappedId(stream);
            WriteWrapped(stream, LdapEntry(id, "CN=u" + page.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",DC=smoke,DC=invalid", ("sAMAccountName", "u")));
            WriteWrapped(stream, LdapReply(id, 5, 0, cookie: page == 0 ? [1] : []));
        }

        ReadWrappedId(stream);   // the unbind or nothing: the client may close
    }

    private static int ReadPlainId(Stream stream)
    {
        var head = new List<byte>();
        int? length = null;
        while (length is null)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                throw new IOException("the smoke's client closed");
            }

            head.Add((byte)b);
            length = LdapMessages.MessageLength(head.ToArray(), 1 << 20);
        }

        var message = new byte[length.Value];
        head.CopyTo(message);
        stream.ReadExactly(message, head.Count, message.Length - head.Count);
        return (int)new AsnReader(message, AsnEncodingRules.BER).ReadSequence().ReadInteger();
    }

    private static int ReadWrappedId(Stream stream)
    {
        var head = new byte[4];
        if (stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4)
        {
            return 0;
        }

        var token = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head)];
        stream.ReadExactly(token);
        return (int)new AsnReader(token.AsMemory(2), AsnEncodingRules.BER).ReadSequence().ReadInteger();
    }

    private static void WriteWrapped(Stream stream, byte[] message)
    {
        byte[] token = [LoopbackSaslLayer.Mark, 1, .. message];
        var head = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head, token.Length);
        stream.Write(head);
        stream.Write(token);
    }

    private static byte[] LdapReply(int id, int op, int code, byte[]? creds = null, byte[]? cookie = null)
    {
        var writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(id);
            using (writer.PushSequence(new Asn1Tag(TagClass.Application, op, isConstructed: true)))
            {
                writer.WriteEncodedValue(new byte[] { 0x0A, 1, (byte)code });
                writer.WriteOctetString([]);
                writer.WriteOctetString([]);
                if (creds is not null)
                {
                    writer.WriteOctetString(creds, new Asn1Tag(TagClass.ContextSpecific, 7));
                }
            }

            if (cookie is not null)
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                using (writer.PushSequence())
                {
                    writer.WriteOctetString(Encoding.UTF8.GetBytes(LdapMessages.PagedResultsOid));
                    var value = new AsnWriter(AsnEncodingRules.BER);
                    using (value.PushSequence())
                    {
                        value.WriteInteger(0);
                        value.WriteOctetString(cookie);
                    }

                    writer.WriteOctetString(value.Encode());
                }
            }
        }

        return writer.Encode();
    }

    private static byte[] LdapEntry(int id, string dn, (string Name, string Value) attribute)
    {
        var writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(id);
            using (writer.PushSequence(new Asn1Tag(TagClass.Application, 4, isConstructed: true)))
            {
                writer.WriteOctetString(Encoding.UTF8.GetBytes(dn));
                using (writer.PushSequence())
                using (writer.PushSequence())
                {
                    writer.WriteOctetString(Encoding.UTF8.GetBytes(attribute.Name));
                    using (writer.PushSetOf())
                    {
                        writer.WriteOctetString(Encoding.UTF8.GetBytes(attribute.Value));
                    }
                }
            }
        }

        return writer.Encode();
    }
}

/// <summary>
/// The smoke's SASL layer (2026-10-18, the Mac AD plan): two tokens, then wrapping as <c>[Mark, encrypted, …]</c>, with no cryptography at
/// all — so it must never reach a real connection. <see cref="ManagedDirectorySystem"/> builds its own <see cref="NegotiateSaslLayer"/> and
/// takes no layer from outside the tests; <c>LdapManagedTests</c> pins that this name appears in the smoke's file alone.
/// </summary>
internal sealed class LoopbackSaslLayer : ISaslLayer
{
    public const byte Mark = 0x5A;

    private int _steps;

    public bool Signed => true;

    public bool Encrypted => true;

    public SaslStep Step(ReadOnlySpan<byte> incoming) => ++_steps == 1 ? new SaslStep("c1"u8.ToArray(), false) : new SaslStep([], true);

    public byte[] Wrap(ReadOnlySpan<byte> data, bool encrypt, out bool encrypted)
    {
        encrypted = encrypt;
        return [Mark, (byte)(encrypt ? 1 : 0), .. data];
    }

    public byte[] Unwrap(ReadOnlySpan<byte> data, out bool wasEncrypted)
    {
        if (data.Length < 2 || data[0] != Mark)
        {
            throw new DirectoryException(LdapResultCodes.DecodingError, "not a loopback buffer");
        }

        wasEncrypted = data[1] == 1;
        return data[2..].ToArray();
    }

    public void Dispose()
    {
    }
}
