using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication.ExtendedProtection;

namespace NeonSidekick.ActiveDirectory.Ldap;

/// <summary>
/// One LDAP connection (2026-10-18, the Mac AD plan): TCP to the server, or LDAPS through <see cref="SslStream"/> with the system's trust
/// and no "skip validation" (wldap32's behaviour). Messages go one at a time (searches are sequential). Until <see cref="StartSecurity"/>
/// it speaks plain LDAP, reading exactly one message's bytes and nothing past them, so whatever follows the final bind reply is left for the
/// SASL reader: nothing is read in the wrong framing at the switch. After it, every message is a SASL buffer (RFC 4422 §3.7): a 4-byte
/// big-endian length and the wrapped bytes, a confidential connection refusing anything that came back unencrypted. A message is held to
/// <see cref="MaxMessage"/>, an incoming buffer to <see cref="SaslGssapiExchange.ClientMaximum"/>; anything larger closes the connection as a
/// protocol error. A read past <see cref="ReadTimeout"/> is <see cref="LdapResultCodes.Timeout"/>; a closed connection, or the server's
/// notice that it is leaving, <see cref="LdapResultCodes.ServerDown"/>.
/// <para>
/// 2026-10-20, the code review: it sends only what <see cref="LdapMessages"/> writes — <see cref="Bind"/>, <see cref="Search"/> and
/// <see cref="Unbind"/> — and no raw bytes (<c>LdapManagedTests</c> pins that no public or internal method takes a message), so the client stays
/// read-only by construction. A read waits at most <see cref="ReadTimeout"/>, but a server trickling a byte at a time beats that, so
/// <see cref="Within"/> sets a deadline for a whole operation, whose end closes the socket under the blocked read: what follows is
/// <see cref="LdapResultCodes.Timeout"/>, never a stray <see cref="IOException"/> or <see cref="ObjectDisposedException"/>. The SASL reader keeps
/// one buffer with a start and an end, so a reply in many wrapped buffers is read in linear time.
/// </para>
/// </summary>
public sealed class LdapConnection : IDisposable
{
    /// <summary>The largest message read, wrapped or plain.</summary>
    public const int MaxMessage = 16 * 1024 * 1024;

    /// <summary>How long a connect may take, as wldap32's.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient? _client;
    private readonly Socket? _socket;
    private readonly Stream _stream;
    private ISaslLayer? _layer;
    private SaslSecurity _security = SaslSecurity.None;
    private int _serverMaximum;
    private byte[] _plain = [];
    private int _plainStart;
    private int _plainEnd;
    private int _id;
    private volatile bool _expired;

    /// <summary>A connection over a stream the caller made (the tests' and the smoke's loopback).</summary>
    public LdapConnection(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    private LdapConnection(TcpClient client, Stream stream, ChannelBinding? binding, string remote)
    {
        _client = client;
        _socket = client.Client;
        _stream = stream;
        Binding = binding;
        Remote = remote;
    }

    /// <summary>The TLS channel binding (tls-server-end-point), when the connection is LDAPS and the platform gives one.</summary>
    public ChannelBinding? Binding { get; }

    /// <summary>The address this connection reached, for the KDC check.</summary>
    public string? Remote { get; }

    /// <summary>The layer in force.</summary>
    public SaslSecurity Security => _security;

    /// <summary>The most one read waits (the search's time limit plus a margin).</summary>
    public TimeSpan ReadTimeout
    {
        set
        {
            if (_stream.CanTimeout)
            {
                _stream.ReadTimeout = (int)Math.Clamp(value.TotalMilliseconds, 1000, int.MaxValue);
            }
        }
    }

    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/>, LDAPS under <paramref name="tls"/>, in <see cref="ConnectTimeout"/>.</summary>
    public static LdapConnection Open(string host, int port, bool tls)
    {
        ArgumentNullException.ThrowIfNull(host);
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            client.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            client.Dispose();
            throw new DirectoryException(LdapResultCodes.ConnectError, "the connection to " + host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + " failed: " + ex.Message);
        }

        string remote = client.Client.RemoteEndPoint is System.Net.IPEndPoint end ? end.Address.ToString() : host;
        Stream stream = client.GetStream();
        ChannelBinding? binding = null;
        if (tls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            SslPolicyErrors refused = SslPolicyErrors.None;
            var chainFlags = System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NoError;
            try
            {
                ssl.ReadTimeout = (int)ConnectTimeout.TotalMilliseconds;

                // The system's own judgement, only noted on the way past so a refusal can say why; nothing it refuses is let through.
                var options = new SslClientAuthenticationOptions
                {
                    TargetHost = host,

                    // Revocation checked online, as Windows does (2026-10-23, the admin review's V4: .NET's default is no check, so a revoked DC
                    // certificate was taken).
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
                    RemoteCertificateValidationCallback = (_, _, chain, errors) =>
                    {
                        foreach (var status in chain?.ChainStatus ?? [])
                        {
                            chainFlags |= status.Status;
                        }

                        // A revocation that could not be checked (an ldap:// CRL point the Mac cannot fetch, a CRL host off the VPN) is
                        // passed, as Windows' default does (2026-10-23, the branch review of DP7: every such bind was refused); a revoked
                        // certificate, and every other chain error, is still refused.
                        if (errors == SslPolicyErrors.RemoteCertificateChainErrors && LdapTls.RevocationUnknownOnly(chainFlags))
                        {
                            errors = SslPolicyErrors.None;
                        }

                        refused = errors;
                        return errors == SslPolicyErrors.None;
                    },
                };
                using var timeout = new CancellationTokenSource(ConnectTimeout);
                ssl.AuthenticateAsClientAsync(options, timeout.Token).GetAwaiter().GetResult();
            }
            catch (System.Security.Authentication.AuthenticationException) when (refused != SslPolicyErrors.None)
            {
                ssl.Dispose();
                client.Dispose();
                throw new DirectoryException(LdapResultCodes.TlsRefused, DirectoryText.CertificateRefused(host, refused, chainFlags));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
                ssl.Dispose();
                client.Dispose();
                throw new DirectoryException(LdapResultCodes.ConnectError, "the TLS connection to " + host + " failed: " + ex.Message);
            }

            binding = ssl.TransportContext?.GetChannelBinding(ChannelBindingKind.Endpoint);
            stream = ssl;
        }

        return new LdapConnection(client, stream, binding, remote);
    }

    /// <summary>The next message id.</summary>
    private int NextId() => ++_id;

    /// <summary>
    /// A deadline for what runs until the scope is disposed (2026-10-20, the code review): past <paramref name="limit"/> the socket is closed, so a
    /// blocked read or write ends and throws <see cref="LdapResultCodes.Timeout"/>; the connection is then gone for good.
    /// </summary>
    public IDisposable Within(TimeSpan limit) => new Deadline(this, limit);

    /// <summary>Whether a deadline ended this connection.</summary>
    public bool Expired => _expired;

    /// <summary>Wraps every message from now on in <paramref name="layer"/>'s <paramref name="security"/> (nothing for <see cref="SaslSecurity.None"/>).</summary>
    public void StartSecurity(ISaslLayer layer, SaslSecurity security, int serverMaximum)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _security = security;
        _serverMaximum = serverMaximum;
    }

    private bool Wrapped => _layer is not null && _security != SaslSecurity.None;

    /// <summary>
    /// One SASL GSSAPI bind round trip with <paramref name="credentials"/> (2026-10-20, the code review: the reply must be a bind response to
    /// this request, by its message id, or it is a decoding error).
    /// </summary>
    public LdapReply Bind(ReadOnlySpan<byte> credentials)
    {
        int id = NextId();
        Send(LdapMessages.Bind(id, credentials));
        var answer = Receive();
        if (answer.Op != LdapOp.BindResponse)
        {
            throw new DirectoryException(LdapResultCodes.DecodingError, "the bind was answered with something else");
        }

        return answer.MessageId == id ? answer : throw new DirectoryException(LdapResultCodes.DecodingError, "a bind reply to another request");
    }

    /// <summary>Sends a search (<see cref="LdapMessages.Search"/>'s); its message id, which every reply to it carries.</summary>
    public int Search(string baseDn, DirectoryScope scope, string filter, IReadOnlyList<string> attributes, int sizeLimit, int timeLimitSeconds, int? pageSize = null, byte[]? cookie = null)
    {
        int id = NextId();
        Send(LdapMessages.Search(id, baseDn, scope, filter, attributes, sizeLimit, timeLimitSeconds, pageSize, cookie));
        return id;
    }

    /// <summary>Sends one message.</summary>
    private void Send(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            if (!Wrapped)
            {
                _stream.Write(message);
                _stream.Flush();
                return;
            }

            bool confidential = _security == SaslSecurity.Confidentiality;
            byte[] token = _layer!.Wrap(message, confidential, out bool encrypted);
            if (confidential && !encrypted)
            {
                throw new DirectoryException(LdapResultCodes.AuthUnknown, "a message on a sealed connection went out unencrypted");
            }

            if (_serverMaximum > 0 && token.Length > _serverMaximum)
            {
                throw new DirectoryException(LdapResultCodes.DecodingError, "a request larger than the server takes");
            }

            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, token.Length);
            _stream.Write(length);
            _stream.Write(token);
            _stream.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            throw Lost(ex);
        }
    }

    /// <summary>Reads one message; the server's notice of disconnection is <see cref="LdapResultCodes.ServerDown"/>.</summary>
    public LdapReply Receive()
    {
        var reply = LdapMessages.Read(Wrapped ? ReadWrapped() : ReadPlain());
        if (reply.MessageId == 0 && reply.Op == LdapOp.ExtendedResponse)
        {
            throw new DirectoryException(LdapResultCodes.ServerDown, "the server ended the connection" + (reply.Diagnostic.Length > 0 ? ": " + Llm.Tools.UntrustedText.Cut(Llm.Tools.UntrustedText.OneLine(reply.Diagnostic), 300) : ""));
        }

        return reply;
    }

    /// <summary>How long the unbind may take to go out.</summary>
    private static readonly TimeSpan UnbindTime = TimeSpan.FromSeconds(2);

    /// <summary>Sends the unbind, quietly: the connection is going anyway.</summary>
    public void Unbind()
    {
        try
        {
            using var limit = Within(UnbindTime);
            Send(LdapMessages.Unbind(NextId()));
        }
        catch (Exception ex) when (ex is DirectoryException or IOException or ObjectDisposedException)
        {
            // already gone
        }
    }

    private byte[] ReadPlain()
    {
        try
        {
            var head = new List<byte>(6);
            int? length = null;
            while (length is null)
            {
                int b = _stream.ReadByte();
                if (b < 0)
                {
                    throw Closed();
                }

                head.Add((byte)b);
                length = LdapMessages.MessageLength(head.ToArray(), MaxMessage);
            }

            var message = new byte[length.Value];
            head.CopyTo(message);
            _stream.ReadExactly(message, head.Count, message.Length - head.Count);
            return message;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            throw Lost(ex);
        }
    }

    /// <summary>
    /// The next message under the SASL layer: from what earlier buffers left in <c>_plain[_plainStart.._plainEnd]</c>, reading and unwrapping
    /// buffers until one whole message is there (2026-10-20, the code review: the held bytes were copied into a new array per buffer, quadratic in
    /// a reply split across many).
    /// </summary>
    private byte[] ReadWrapped()
    {
        Span<byte> head = stackalloc byte[4];
        try
        {
            while (true)
            {
                var held = _plain.AsSpan(_plainStart, _plainEnd - _plainStart);
                if (LdapMessages.MessageLength(held, MaxMessage) is { } length && held.Length >= length)
                {
                    byte[] message = held[..length].ToArray();
                    _plainStart += length;
                    if (_plainStart == _plainEnd)
                    {
                        _plainStart = _plainEnd = 0;
                    }

                    return message;
                }

                _stream.ReadExactly(head);
                int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head);
                if (size <= 0 || size > SaslGssapiExchange.ClientMaximum)
                {
                    throw new DirectoryException(LdapResultCodes.DecodingError, "a wrapped reply of a size this client does not take");
                }

                var token = new byte[size];
                _stream.ReadExactly(token);
                byte[] clear = _layer!.Unwrap(token, out bool wasEncrypted);
                if (_security == SaslSecurity.Confidentiality && !wasEncrypted)
                {
                    throw new DirectoryException(LdapResultCodes.AuthUnknown, "a reply on a sealed connection came back unencrypted");
                }

                if (held.Length + clear.Length > MaxMessage)
                {
                    throw new DirectoryException(LdapResultCodes.DecodingError, "a reply larger than this client takes");
                }

                Hold(clear);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            throw Lost(ex);
        }
    }

    /// <summary>Adds <paramref name="clear"/> after the bytes held: moved to the front, or into a buffer twice as large, only when it doesn't fit.</summary>
    private void Hold(byte[] clear)
    {
        int held = _plainEnd - _plainStart;
        if (_plain.Length - _plainEnd < clear.Length)
        {
            byte[] into = _plain.Length - held >= clear.Length ? _plain : new byte[Math.Max(held + clear.Length, _plain.Length * 2)];
            Buffer.BlockCopy(_plain, _plainStart, into, 0, held);
            _plain = into;
            _plainStart = 0;
            _plainEnd = held;
        }

        clear.CopyTo(_plain, _plainEnd);
        _plainEnd += clear.Length;
    }

    /// <summary>The server's end of the stream: a timeout when a deadline closed it.</summary>
    private DirectoryException Closed() => _expired ? TimedOut() : new DirectoryException(LdapResultCodes.ServerDown, "the server closed the connection");

    private static DirectoryException TimedOut() => new(LdapResultCodes.Timeout, "the server took too long to answer");

    private DirectoryException Lost(Exception ex) =>
        _expired || ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut } || ex is SocketException { SocketErrorCode: SocketError.TimedOut }
            ? TimedOut()
            : ex is EndOfStreamException or ObjectDisposedException
                ? Closed()
                : new DirectoryException(LdapResultCodes.ServerDown, "the connection was lost: " + ex.Message);

    /// <summary>
    /// A deadline's end: the socket closed under whatever reads or writes it (the stream itself left alone where there is a socket, so an
    /// <see cref="SslStream"/> is never disposed in the middle of a read).
    /// </summary>
    private void Expire()
    {
        _expired = true;
        try
        {
            if (_socket is not null)
            {
                _socket.Dispose();   // idempotent, and held apart from the TcpClient, whose Client a Dispose already run may have cleared
            }
            else
            {
                _stream.Dispose();
            }
        }
        catch (Exception)
        {
            // A timer's callback on a pool thread: whatever it throws would end the process, not the call. The connection is gone either way.
        }
    }

    /// <summary>One deadline: a timer whose end calls <see cref="Expire"/>; disposing it waits out a callback already running.</summary>
    private sealed class Deadline : IDisposable
    {
        private readonly CancellationTokenSource _timer;
        private readonly CancellationTokenRegistration _onEnd;

        public Deadline(LdapConnection connection, TimeSpan limit)
        {
            _timer = new CancellationTokenSource(limit);
            _onEnd = _timer.Token.Register(connection.Expire);
        }

        public void Dispose()
        {
            _onEnd.Dispose();
            _timer.Dispose();
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client?.Dispose();
    }
}

/// <summary>The Mac's LDAPS chain rules, kept off <see cref="LdapConnection"/>, whose members are pinned as its only operations (DP7).</summary>
internal static class LdapTls
{
    /// <summary>Whether a chain's only faults are a revocation status that could not be learned (DP7): never <c>Revoked</c>.</summary>
    internal static bool RevocationUnknownOnly(System.Security.Cryptography.X509Certificates.X509ChainStatusFlags flags)
    {
        const System.Security.Cryptography.X509Certificates.X509ChainStatusFlags unknown =
            System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown
            | System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation;
        return flags != System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NoError && (flags & ~unknown) == 0;
    }
}
