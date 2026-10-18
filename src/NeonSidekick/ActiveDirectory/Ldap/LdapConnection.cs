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
/// </summary>
public sealed class LdapConnection : IDisposable
{
    /// <summary>The largest message read, wrapped or plain.</summary>
    public const int MaxMessage = 16 * 1024 * 1024;

    /// <summary>How long a connect may take, as wldap32's.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient? _client;
    private readonly Stream _stream;
    private ISaslLayer? _layer;
    private SaslSecurity _security = SaslSecurity.None;
    private int _serverMaximum;
    private byte[] _plain = [];
    private int _id;

    /// <summary>A connection over a stream the caller made (the tests' and the smoke's loopback).</summary>
    public LdapConnection(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    private LdapConnection(TcpClient client, Stream stream, ChannelBinding? binding, string remote)
    {
        _client = client;
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
            try
            {
                ssl.ReadTimeout = (int)ConnectTimeout.TotalMilliseconds;
                ssl.AuthenticateAsClient(host);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
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
    public int NextId() => ++_id;

    /// <summary>Wraps every message from now on in <paramref name="layer"/>'s <paramref name="security"/> (nothing for <see cref="SaslSecurity.None"/>).</summary>
    public void StartSecurity(ISaslLayer layer, SaslSecurity security, int serverMaximum)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _security = security;
        _serverMaximum = serverMaximum;
    }

    private bool Wrapped => _layer is not null && _security != SaslSecurity.None;

    /// <summary>Sends one message.</summary>
    public void Send(byte[] message)
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
        catch (IOException ex)
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
            throw new DirectoryException(LdapResultCodes.ServerDown, "the server ended the connection" + (reply.Diagnostic.Length > 0 ? ": " + reply.Diagnostic : ""));
        }

        return reply;
    }

    /// <summary>Sends one message and reads its one reply (the bind's way).</summary>
    public LdapReply Exchange(byte[] message)
    {
        Send(message);
        return Receive();
    }

    /// <summary>Sends the unbind, quietly: the connection is going anyway.</summary>
    public void Unbind()
    {
        try
        {
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
                    throw new DirectoryException(LdapResultCodes.ServerDown, "the server closed the connection");
                }

                head.Add((byte)b);
                length = LdapMessages.MessageLength(head.ToArray(), MaxMessage);
            }

            var message = new byte[length.Value];
            head.CopyTo(message);
            _stream.ReadExactly(message, head.Count, message.Length - head.Count);
            return message;
        }
        catch (EndOfStreamException)
        {
            throw new DirectoryException(LdapResultCodes.ServerDown, "the server closed the connection");
        }
        catch (IOException ex)
        {
            throw Lost(ex);
        }
    }

    private byte[] ReadWrapped()
    {
        Span<byte> head = stackalloc byte[4];
        try
        {
            while (true)
            {
                if (LdapMessages.MessageLength(_plain, MaxMessage) is { } length && _plain.Length >= length)
                {
                    byte[] message = _plain[..length];
                    _plain = _plain[length..];
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

                if (_plain.Length + clear.Length > MaxMessage)
                {
                    throw new DirectoryException(LdapResultCodes.DecodingError, "a reply larger than this client takes");
                }

                _plain = [.. _plain, .. clear];
            }
        }
        catch (EndOfStreamException)
        {
            throw new DirectoryException(LdapResultCodes.ServerDown, "the server closed the connection");
        }
        catch (IOException ex)
        {
            throw Lost(ex);
        }
    }

    private static DirectoryException Lost(IOException ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }
            ? new DirectoryException(LdapResultCodes.Timeout, "the server took too long to answer")
            : new DirectoryException(LdapResultCodes.ServerDown, "the connection was lost: " + ex.Message);

    public void Dispose()
    {
        _stream.Dispose();
        _client?.Dispose();
    }
}
