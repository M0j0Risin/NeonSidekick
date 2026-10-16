using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Network;
using NeonSidekick.Services;
using NeonSidekick.Tasks;

namespace NeonSidekick.App;

/// <summary>
/// The admin families' smoke probes (2026-10-15, the admin-tools plan): each native layer's entry points bound on the published binary and a
/// refusal worded from its code, nothing changed anywhere. System libraries all: nothing joins <see cref="RequiredNativeLibraries"/>.
/// </summary>
public static partial class SmokeChecks
{
    /// <summary><c>net:icmp</c>: one echo to 127.0.0.1 through this system's pinger (IcmpSendEcho2 on Windows, the datagram socket elsewhere).</summary>
    public static SmokeCheck ProbeIcmp()
    {
        const string name = "net:icmp";
        IPinger pinger = OperatingSystem.IsWindows() ? new WindowsPinger() : new IcmpSocketPinger();
        var hop = Task.Run(() => pinger.PingAsync(IPAddress.Loopback, 64, TimeSpan.FromSeconds(2), CancellationToken.None)).GetAwaiter().GetResult();
        return new SmokeCheck(name, hop.Status == PingStatus.Reply, hop.Status + (hop.Rtt is { } rtt ? " in " + rtt.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "") + (hop.Detail is { } detail ? ": " + detail : ""));
    }

    /// <summary><c>net:dns</c>: the app's own DNS client against an in-process UDP server on loopback answering one A record.</summary>
    public static SmokeCheck ProbeDns()
    {
        const string name = "net:dns";
        try
        {
            using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
            var serve = Task.Run(async () =>
            {
                var request = await server.ReceiveAsync().ConfigureAwait(false);
                await server.SendAsync(AnswerA(request.Buffer, [192, 0, 2, 7]), request.RemoteEndPoint).ConfigureAwait(false);
            });
            var outcome = Task.Run(() => DnsClient.AskAsync(endpoint, "smoke.neonsidekick.invalid", DnsWire.A, CancellationToken.None)).GetAwaiter().GetResult();
            serve.Wait(TimeSpan.FromSeconds(5));
            string? answer = outcome.Reply?.Answers.FirstOrDefault()?.Data;
            return new SmokeCheck(name, answer == "192.0.2.7", answer is null ? outcome.Error ?? "no answer" : "A " + answer + " from the in-process server");
        }
        catch (SocketException ex)
        {
            return new SmokeCheck(name, false, ex.Message);
        }
    }

    /// <summary>A reply to <paramref name="query"/>: its header with QR and RA set, its question, one A record pointing at the question's name.</summary>
    internal static byte[] AnswerA(byte[] query, byte[] address)
    {
        int questionEnd = 12;
        while (query[questionEnd] != 0)
        {
            questionEnd += query[questionEnd] + 1;
        }

        questionEnd += 5;
        var reply = new List<byte>(query[..questionEnd]);
        reply[2] = 0x81;
        reply[3] = 0x80;
        reply[7] = 1;   // ANCOUNT
        reply[10] = 0;  // no OPT echoed
        reply[11] = 0;
        reply.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4]);
        reply.AddRange(address);
        return reply.ToArray();
    }

    /// <summary>
    /// <c>net:tls</c>: <see cref="TlsProbe"/> against an in-process <see cref="SslStream"/> server on loopback, its certificate made by
    /// <see cref="CertificateRequest"/> and re-imported through <see cref="X509CertificateLoader.LoadPkcs12"/> (SChannel will not serve an
    /// ephemeral key): the protocol, the leaf's subject and its untrusted chain read back.
    /// </summary>
    public static SmokeCheck ProbeTls()
    {
        const string name = "net:tls";
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=smoke.neonsidekick.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            using var certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx, "smoke"), "smoke");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var serve = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    await using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsServerAsync(certificate).ConfigureAwait(false);
                });
                var report = Task.Run(() => TlsProbe.RunAsync("localhost", [IPAddress.Loopback], port, "localhost", StartTls.None, CancellationToken.None)).GetAwaiter().GetResult();
                serve.Wait(TimeSpan.FromSeconds(5));
                bool ok = report.Leaf?.Subject.Contains("smoke.neonsidekick.invalid", StringComparison.Ordinal) == true && report.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors);
                return new SmokeCheck(name, ok, $"{report.Protocol}, {report.Cipher}; leaf {report.Leaf?.Subject}; policy errors {TlsProbe.PolicyWords(report.PolicyErrors)}");
            }
            finally
            {
                listener.Stop();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or CryptographicException or System.Security.Authentication.AuthenticationException or TlsPreambleException)
        {
            return new SmokeCheck(name, false, ex.Message);
        }
    }

    /// <summary><c>net:certstore</c>: the user's My store opened read-only and listed.</summary>
    public static SmokeCheck ProbeCertStore()
    {
        var listing = CertificateStores.Read("My", StoreLocation.CurrentUser);
        return new SmokeCheck("net:certstore", listing.Error is null, listing.Error ?? listing.Certificates.Count.ToString(CultureInfo.InvariantCulture) + " certificates in the user's My store");
    }

    /// <summary><c>services:scm</c>: the services enumerated, EventLog's configuration and status read, and a missing service's 1060 worded.</summary>
    [SupportedOSPlatform("windows")]
    public static SmokeCheck ProbeServices()
    {
        const string name = "services:scm";
        try
        {
            var system = new WindowsServiceSystem();
            return system.Run(ServiceTarget.Local, session =>
            {
                int count = session.List(drivers: false).Count;
                var detail = session.Detail("EventLog");
                string missing;
                try
                {
                    session.Detail("NeonSidekickNoSuchService");
                    missing = "no refusal";
                }
                catch (ServiceException ex)
                {
                    missing = ServiceErrors.Sentence(ex.Code, new ServiceErrorContext(Computers.ComputerText.ThisPc, "NeonSidekickNoSuchService", false, "", false), ex.Message);
                }

                bool worded = missing.Contains("there is no service named", StringComparison.Ordinal);
                return new SmokeCheck(name, count > 0 && worded,
                    $"{count.ToString(CultureInfo.InvariantCulture)} services; EventLog {ServiceText.StateWord(detail.Status.State)}, {ServiceText.StartWord(detail.Config.StartType, detail.Config.DelayedAutoStart)}, {detail.Dependents.Count.ToString(CultureInfo.InvariantCulture)} dependents; 1060 {(worded ? "worded" : "NOT worded: " + missing)}");
            });
        }
        catch (ServiceException ex)
        {
            return new SmokeCheck(name, false, ex.Message);
        }
    }

    /// <summary><c>services:remote</c>: the SCM opened over RPC on <c>\\localhost</c> and listed; access denied passes (the call reached it).</summary>
    [SupportedOSPlatform("windows")]
    public static SmokeCheck ProbeServicesRemote()
    {
        const string name = "services:remote";
        try
        {
            int count = new WindowsServiceSystem().Run(new ServiceTarget("localhost", null), session => session.List(drivers: false).Count);
            return new SmokeCheck(name, count > 0, count.ToString(CultureInfo.InvariantCulture) + " services over \\\\localhost");
        }
        catch (ServiceException ex)
        {
            return new SmokeCheck(name, ex.Code == 5, ex.Code == 5 ? "access denied: the call reached the remote SCM" : ex.Message);
        }
    }

    /// <summary>
    /// <c>tasks:com</c>: the Task Scheduler connected (TargetServer, HighestVersion), the tasks listed, one read back by its path, its XML
    /// parsed, and a missing task's 0x80070002 worded — every vtable slot the app uses.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static SmokeCheck ProbeTasks()
    {
        const string name = "tasks:com";
        try
        {
            return new WindowsTaskSystem().Run(TaskTarget.Local, session =>
            {
                string server = session.TargetServer;
                int version = session.HighestVersion;
                var all = session.List("\\", recursive: true);
                if (all.Count == 0)
                {
                    return new SmokeCheck(name, false, "no task listed");
                }

                var detail = session.Detail(all[0].Path);
                var definition = TaskXml.Parse(detail.Xml);
                string missing;
                try
                {
                    session.Detail("\\NeonSidekickNoSuchTask");
                    missing = "no refusal";
                }
                catch (TaskException ex)
                {
                    missing = TaskErrors.Sentence(ex.Code, new TaskErrorContext(Computers.ComputerText.ThisPc, "\\NeonSidekickNoSuchTask", false, "", false), ex.Message);
                }

                bool roundTrip = string.Equals(detail.Entry.Path, all[0].Path, StringComparison.OrdinalIgnoreCase);
                bool worded = missing.Contains("there is no task", StringComparison.Ordinal);
                return new SmokeCheck(name, roundTrip && worded && server.Length > 0,
                    $"{server}, version 0x{version.ToString("X", CultureInfo.InvariantCulture)}; {all.Count.ToString(CultureInfo.InvariantCulture)} tasks; {all[0].Path} read back ({definition.Actions.Count.ToString(CultureInfo.InvariantCulture)} actions); 0x80070002 {(worded ? "worded" : "NOT worded: " + missing)}");
            });
        }
        catch (Exception ex) when (ex is TaskException or TaskXmlException)
        {
            return new SmokeCheck(name, false, ex.Message);
        }
    }

    /// <summary><c>tasks:remote</c>: <c>Connect</c> to <c>localhost</c> by name and the root listed; access denied passes.</summary>
    [SupportedOSPlatform("windows")]
    public static SmokeCheck ProbeTasksRemote()
    {
        const string name = "tasks:remote";
        try
        {
            string server = new WindowsTaskSystem().Run(new TaskTarget("localhost", null), session => session.TargetServer);
            return new SmokeCheck(name, server.Length > 0, "connected to " + server + " by name");
        }
        catch (TaskException ex)
        {
            bool denied = unchecked((uint)ex.Code) == 0x80070005;
            return new SmokeCheck(name, denied, denied ? "access denied: the call reached the Task Scheduler" : ex.Message);
        }
    }

    /// <summary><c>ad:wldap32</c>: init, the options set, then a connect to a closed loopback port — LDAP_SERVER_DOWN worded. Proves the entry points.</summary>
    [SupportedOSPlatform("windows")]
    public static SmokeCheck ProbeLdap()
    {
        const string name = "ad:wldap32";
        int port = ClosedLoopbackPort();
        try
        {
            new WindowsDirectorySystem().Run(new DirectoryTarget("127.0.0.1", port, false, null), session => 0);
            return new SmokeCheck(name, false, "a closed port answered");
        }
        catch (DirectoryException ex)
        {
            string sentence = DirectoryErrors.Sentence(ex.Code, "smoke", "127.0.0.1", port, "", ex.Message);
            bool worded = sentence.Contains("did not answer LDAP", StringComparison.Ordinal);
            return new SmokeCheck(name, worded, $"code 0x{ex.Code.ToString("X2", CultureInfo.InvariantCulture)} {(worded ? "worded" : "NOT worded: " + sentence)}");
        }
    }

    /// <summary>A loopback port nothing listens on: one bound and released.</summary>
    private static int ClosedLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
