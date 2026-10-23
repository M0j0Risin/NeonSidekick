using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NeonSidekick.Network;

namespace NeonSidekick.Tests;

/// <summary>
/// The network family's own clients against servers on loopback (2026-10-15, the admin-tools plan): <see cref="DnsClient"/> over UDP — a
/// stranger's reply and a wrong id skipped, a truncated answer asked again over TCP — and <see cref="TlsProbe"/> against a self-signed
/// server, plain and behind SMTP's STARTTLS. Nothing leaves the machine.
/// </summary>
public sealed class NetworkLoopbackTests
{
    // ── DNS ────────────────────────────────────────────────────────────────

    private static byte[] Answer(byte[] query, bool truncated, int records, ushort? idOverride = null)
    {
        int questionEnd = 12;
        while (query[questionEnd] != 0)
        {
            questionEnd += query[questionEnd] + 1;
        }

        questionEnd += 5;
        var reply = new List<byte>(query[..questionEnd]);
        if (idOverride is { } id)
        {
            reply[0] = (byte)(id >> 8);
            reply[1] = (byte)id;
        }

        reply[2] = (byte)(0x80 | (truncated ? 0x02 : 0));
        reply[3] = 0x80;
        reply[6] = 0;
        reply[7] = (byte)records;
        reply[10] = 0;
        reply[11] = 0;
        for (int i = 0; i < records; i++)
        {
            byte[] exchange = DnsWire.EncodeName("mx" + i + ".example.com");
            reply.AddRange([0xC0, 0x0C, 0, 15, 0, 1, 0, 0, 0x0E, 0x10, 0, (byte)(exchange.Length + 2), 0, (byte)(10 * (i + 1)), .. exchange]);
        }

        return reply.ToArray();
    }

    [Fact]
    public async Task Dns_OverUdp_SkipsAWrongIdAndTakesTheAnswer()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var serve = Task.Run(async () =>
        {
            var query = await server.ReceiveAsync();
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(query.Buffer);
            await server.SendAsync(Answer(query.Buffer, false, 1, (ushort)(id ^ 0xFFFF)), query.RemoteEndPoint);   // someone else's
            await server.SendAsync(Answer(query.Buffer, false, 2), query.RemoteEndPoint);
        });

        var outcome = await DnsClient.AskAsync(endpoint, "example.com", DnsWire.Mx, CancellationToken.None);
        await serve;

        Assert.Null(outcome.Error);
        Assert.False(outcome.OverTcp);
        Assert.Equal(["10 mx0.example.com", "20 mx1.example.com"], outcome.Reply!.Answers.Select(a => a.Data));
    }

    [Fact]
    public async Task Dns_ATruncatedAnswer_IsAskedAgainOverTcp()
    {
        var (udp, tcp) = PortPair();
        using var _ = udp;
        var endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
        try
        {
            var serve = Task.Run(async () =>
            {
                var query = await udp.ReceiveAsync();
                await udp.SendAsync(Answer(query.Buffer, true, 0), query.RemoteEndPoint);
                using var client = await tcp.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var length = new byte[2];
                await stream.ReadExactlyAsync(length);
                var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
                await stream.ReadExactlyAsync(body);
                var answer = Answer(body, false, 3);
                await stream.WriteAsync((byte[])[(byte)(answer.Length >> 8), (byte)answer.Length, .. answer]);
            });

            var outcome = await DnsClient.AskAsync(endpoint, "example.com", DnsWire.Mx, CancellationToken.None);
            await serve;

            Assert.True(outcome.OverTcp);
            Assert.Equal(3, outcome.Reply!.Answers.Count);
        }
        finally
        {
            tcp.Stop();
        }
    }

    /// <summary>
    /// DP7 (the branch review, 2026-10-23): a server that refuses EDNS (FORMERR) and truncates the plain answer is asked over TCP plainly too;
    /// an OPT record over TCP would draw the FORMERR back as the lookup's answer.
    /// </summary>
    [Fact]
    public async Task Dns_AfterAFormErr_TheTcpRetryIsPlainToo()
    {
        static byte[] FormErr(byte[] query)
        {
            var reply = Answer(query, false, 0);
            reply[3] = 0x81;
            return reply;
        }

        static bool HasOpt(byte[] query) => BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(10)) != 0;
        var (udp, tcp) = PortPair();
        using var _ = udp;
        var endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
        try
        {
            var serve = Task.Run(async () =>
            {
                var first = await udp.ReceiveAsync();
                await udp.SendAsync(HasOpt(first.Buffer) ? FormErr(first.Buffer) : Answer(first.Buffer, true, 0), first.RemoteEndPoint);
                var plain = await udp.ReceiveAsync();
                await udp.SendAsync(Answer(plain.Buffer, true, 0), plain.RemoteEndPoint);
                using var client = await tcp.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var length = new byte[2];
                await stream.ReadExactlyAsync(length);
                var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
                await stream.ReadExactlyAsync(body);
                var answer = HasOpt(body) ? FormErr(body) : Answer(body, false, 3);
                await stream.WriteAsync((byte[])[(byte)(answer.Length >> 8), (byte)answer.Length, .. answer]);
            });

            var outcome = await DnsClient.AskAsync(endpoint, "example.com", DnsWire.Mx, CancellationToken.None);
            await serve;

            Assert.True(outcome.OverTcp);
            Assert.Equal(0, outcome.Reply!.Rcode);
            Assert.Equal(3, outcome.Reply.Answers.Count);
        }
        finally
        {
            tcp.Stop();
        }
    }

    /// <summary>
    /// A UDP socket and a TCP listener on one loopback port, tried again on a clash. Windows reserves blocks of 100 ports for Hyper-V, of TCP
    /// and of UDP apart, so one protocol's free port may be the other's reserved one; and free ports come in sequence, so twenty TCP-first tries
    /// in a row can all land in one reserved UDP block (2026-10-16: the suite failed so, with 53308–54307 reserved for UDP). The tries
    /// alternate: TCP's free port then UDP on it, UDP's free port then TCP on it.
    /// </summary>
    private static (UdpClient Udp, TcpListener Tcp) PortPair()
    {
        for (int attempt = 0; ; attempt++)
        {
            bool tcpFirst = attempt % 2 == 0;
            TcpListener? tcp = null;
            UdpClient? udp = null;
            try
            {
                if (tcpFirst)
                {
                    tcp = new TcpListener(IPAddress.Loopback, 0);
                    tcp.Start();
                    udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)tcp.LocalEndpoint).Port));
                }
                else
                {
                    udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    tcp = new TcpListener(IPAddress.Loopback, ((IPEndPoint)udp.Client.LocalEndPoint!).Port);
                    tcp.Start();
                }

                return (udp, tcp);
            }
            catch (SocketException) when (attempt < 40)
            {
                tcp?.Stop();
                udp?.Dispose();
            }
        }
    }

    [Fact]
    public async Task Dns_NobodyAnswering_IsItsSentence()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)silent.Client.LocalEndPoint!;
        var outcome = await DnsClient.QueryAsync([endpoint], "example.com", DnsWire.A, CancellationToken.None);
        Assert.Equal(NetworkText.DnsNoAnswer("127.0.0.1"), outcome.Error);
        Assert.Equal(NetworkText.NoDnsServers, (await DnsClient.QueryAsync([], "example.com", DnsWire.A, CancellationToken.None)).Error);
    }

    // ── TLS ────────────────────────────────────────────────────────────────

    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=neon-loopback", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // SChannel signs only with a key it can find: a PFX round trip gives it one.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    private static async Task ServeTlsAsync(TcpListener listener, X509Certificate2 certificate, bool smtp)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        if (smtp)
        {
            var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            async Task Say(string line) => await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
            await Say("220 loopback ESMTP");
            Assert.StartsWith("EHLO ", await reader.ReadLineAsync());
            await Say("250-loopback");
            await Say("250 STARTTLS");
            Assert.Equal("STARTTLS", await reader.ReadLineAsync());
            await Say("220 go ahead");
        }

        await using var ssl = new SslStream(stream, leaveInnerStreamOpen: true);
        await ssl.AuthenticateAsServerAsync(certificate);
        try
        {
            await ssl.ShutdownAsync();
        }
        catch (IOException)
        {
            // The probe hangs up once it has the certificate; on one core it always did so before this close_notify went out
            // (2026-10-19, the first CI-like run: "an established connection was aborted", both cases, every time). The handshake
            // above is the test; a refused courtesy is not.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tls_ReportsTheSelfSignedLeaf_AndItsChainError(bool smtp)
    {
        using var certificate = SelfSigned();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = ServeTlsAsync(listener, certificate, smtp);
            var report = await TlsProbe.RunAsync("localhost", [IPAddress.Loopback], port, "localhost", smtp ? StartTls.Smtp : StartTls.None, CancellationToken.None);
            await serve;

            Assert.StartsWith("TLS 1.", report.Protocol);
            Assert.Equal("CN=neon-loopback", report.Leaf!.Subject);
            Assert.Contains("localhost", report.Leaf.Names);
            Assert.True(report.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.Contains("chain", TlsProbe.PolicyWords(report.PolicyErrors));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Tls_NobodyListening_Throws()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await Assert.ThrowsAsync<SocketException>(() => TlsProbe.RunAsync("localhost", [IPAddress.Loopback], port, "localhost", StartTls.None, CancellationToken.None));
    }
}
