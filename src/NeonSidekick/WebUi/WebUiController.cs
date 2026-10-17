using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.WebUi;

/// <summary>
/// One network adapter as <see cref="WebUiController.ChooseAddresses"/> weighs it: its kind, its IPv4 addresses, whether it has an IPv4 gateway,
/// its speed, and <paramref name="Names"/>, its name and description together (later on 2026-10-14: Windows' "Tailscale" / "Tailscale Tunnel",
/// so a renamed adapter still reads as Tailscale's; <see cref="WebUiController.IsTailscaleAdapter(WebUiAdapter)"/>).
/// </summary>
public sealed record WebUiAdapter(NetworkInterfaceType Type, IReadOnlyList<IPAddress> Addresses, bool HasGateway, long Speed, string Names = "");

/// <summary>A sign-in address <see cref="WebUiController.ChooseAddresses"/> chose, and whether it is Tailscale's (later on 2026-10-14: its tab says so).</summary>
public sealed record WebUiAddress(string Host, bool Tailscale);

/// <summary>A sign-in link (the token in it, never logged) and whether it is Tailscale's address's (later on 2026-10-14).</summary>
public sealed record WebUiLink(string Url, bool Tailscale);

/// <summary>What the Web UI should be doing: serving or not, on which port, to whom, with which token (plain, never logged).</summary>
public sealed record WebUiWanted(bool On, int Port, WebUiAudience Audience, string Token)
{
    /// <summary>Not serving.</summary>
    public static WebUiWanted Off { get; } = new(false, 0, WebUiAudience.LocalAreaNetwork, "");

    /// <summary>The token left out (a record's own <c>ToString</c> would print it).</summary>
    public override string ToString() => On ? "on " + WebUiReach.Name(Audience) + ":" + Port : "off";
}

/// <summary>
/// Starts, stops and restarts the Web UI's server as the settings say (2026-10-14): <see cref="ReconcileAsync"/> moves to the wanted
/// state — one call at a time, behind a gate, so two restarts never fight over the port — and does nothing when the server already
/// runs as wanted, or a start failed for exactly this wanted state (the idle line's backstop asks again at every loop). A port that
/// cannot be bound (in use, or in a range Windows reserves: "access denied") is one error notice and the server stays off. The notices go
/// through the session's queue (<see cref="WebUiSession.Notice"/>), so the screen prints them on its own task.
/// </summary>
public sealed class WebUiController : IAsyncDisposable
{
    private const string Category = "WebUi";

    private readonly Func<IWebUiBackend, IWebUiHost>? _factory;
    private readonly WebUiSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IWebUiHost? _host;
    private WebUiWanted _settled = WebUiWanted.Off;
    private bool _disposed;

    /// <param name="factory">Makes a server over the session (<see cref="WebUiHost"/> in the app); null = no Web UI in this build.</param>
    public WebUiController(Func<IWebUiBackend, IWebUiHost>? factory, WebUiSession session)
    {
        _factory = factory;
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>Whether this build has a server at all.</summary>
    public bool Available => _factory is not null;

    /// <summary>Whether it serves now.</summary>
    public bool Serving => Volatile.Read(ref _host) is not null;

    /// <summary>The port it serves on, or 0.</summary>
    public int Port => Volatile.Read(ref _host)?.Port ?? 0;

    /// <summary>To whom it serves now.</summary>
    public WebUiAudience Audience => _settled.Audience;

    /// <summary>Moves to <paramref name="wanted"/>: a stop, a start, a restart, or nothing.</summary>
    public async Task ReconcileAsync(WebUiWanted wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        if (_factory is null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || wanted == _settled)
            {
                return;
            }

            if (_host is { } running)
            {
                Volatile.Write(ref _host, null);
                await StopQuietlyAsync(running).ConfigureAwait(false);
                if (!wanted.On)
                {
                    _session.Notice(WebUiText.StoppedNotice);
                }
            }

            _settled = wanted;
            if (!wanted.On)
            {
                return;
            }

            var host = _factory(_session);
            try
            {
                await host.StartAsync(new WebUiBinding(wanted.Audience, wanted.Port, wanted.Token), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or InvalidOperationException)
            {
                DiagnosticLog.Warn(Category, "Web UI: port " + wanted.Port + " could not be opened: " + e.Message);
                _session.Notice(WebUiText.PortFailedError(wanted.Port, Reason(e)), error: true);
                await StopQuietlyAsync(host).ConfigureAwait(false);
                return;
            }

            Volatile.Write(ref _host, host);
            // A start writes nothing in the transcript (2026-10-17, the user's ask: the address and the firewall's warning were noise at
            // every start); the log keeps the address, and /webui shows it with the sign-in link.
            string address = wanted.Audience == WebUiAudience.Local ? "localhost" : PrivateAddresses().FirstOrDefault()?.Host ?? "localhost";
            DiagnosticLog.Info(Category, "Web UI serving " + wanted + " at " + WebUiAuth.Address(address, host.Port) + ".");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A one-time sign-in link on <paramref name="host"/> from the running server (later on 2026-10-15), or null while it serves none.</summary>
    public string? OneTimeLink(string host) => Volatile.Read(ref _host)?.OneTimeLink(host);

    /// <summary>
    /// The sign-in links for <paramref name="token"/>: <c>localhost</c> under <c>local</c> (never Tailscale's), each chosen private IPv4 address under
    /// <c>lan</c>, Tailscale's marked (later on 2026-10-14).
    /// </summary>
    public IReadOnlyList<WebUiLink> Links(string token)
    {
        if (!Serving)
        {
            return [];
        }

        IReadOnlyList<WebUiAddress> hosts = Audience == WebUiAudience.Local ? [new WebUiAddress("localhost", false)] : PrivateAddresses();
        return [.. hosts.Select(h => new WebUiLink(WebUiAuth.SignInLink(h.Host, Port, token), h.Tailscale))];
    }

    /// <summary>
    /// The addresses a phone on the home network reaches this machine at, best first (2026-10-14, the user's ask: <c>/webui</c> listed a VPN's
    /// tunnel and WSL's virtual switch beside the home network's): read from the interfaces that are up and chosen by <see cref="ChooseAddresses"/>.
    /// </summary>
    public static IReadOnlyList<WebUiAddress> PrivateAddresses()
    {
        var adapters = new List<WebUiAdapter>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                adapters.Add(new WebUiAdapter(
                    nic.NetworkInterfaceType,
                    [.. properties.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork)],
                    properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)),
                    nic.Speed,
                    nic.Name + " " + nic.Description));
            }
        }
        catch (NetworkInformationException e)
        {
            DiagnosticLog.Debug(Category, "Web UI: the network interfaces could not be read: " + e.Message);
        }

        return ChooseAddresses(adapters);
    }

    /// <summary>
    /// The sign-in addresses among <paramref name="adapters"/>: the private IPv4 addresses (no loopback, no link-local) of the physical ones —
    /// Ethernet or Wi-Fi — that have an IPv4 gateway, the fastest first. A VPN's tunnel (NordLynx, Tailscale: a virtual type) and Hyper-V's
    /// switch for WSL (Ethernet, but no gateway) are left out; a browser through them still connects when its address is typed. When no
    /// adapter passes, every private address is offered, as before, so the pane is never without a link. Tailscale's address, for a phone away
    /// from home, comes first (2026-10-17, the user's ask: its link works at home and away, so its tab is the one to open on; it followed the
    /// home network's until then): in the tailnet range (100.64/10) and on Tailscale's own adapter (later on 2026-10-14, the user's
    /// pick: a provider's carrier-grade NAT address or another VPN can hold the range too, <see cref="IsTailscaleAdapter(WebUiAdapter)"/>). An
    /// address is marked Tailscale's by the same test, in the fallback too (first there as well), where a 100.x on another tunnel is listed
    /// unmarked. Pure.
    /// </summary>
    public static IReadOnlyList<WebUiAddress> ChooseAddresses(IReadOnlyList<WebUiAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        static bool Usable(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return !IPAddress.IsLoopback(address) && !(bytes[0] == 169 && bytes[1] == 254) && Web.LanPolicy.IsPrivate(address);
        }

        static IEnumerable<WebUiAddress> Addresses(IEnumerable<WebUiAdapter> from) =>
            from.SelectMany(a => a.Addresses.Where(Usable).Select(address => new WebUiAddress(address.ToString(), IsTailscaleAdapter(a) && IsTailnet(address))))
                .DistinctBy(a => a.Host, StringComparer.Ordinal);

        var home = Addresses(adapters.Where(a => a.HasGateway && IsPhysical(a.Type)).OrderByDescending(a => a.Speed)).ToList();
        if (home.Count == 0)
        {
            // Tailscale's first here too (2026-10-17); a stable sort keeps the rest in the adapters' order.
            return [.. Addresses(adapters.Where(a => a.Type != NetworkInterfaceType.Loopback)).OrderByDescending(a => a.Tailscale)];
        }

        // Tailscale's own address (later on 2026-10-14, the user's ask): its tailnet range, 100.64.0.0/10, on Tailscale's adapter (later that
        // day), so a phone away from home has a code to scan too — while NordLynx's 10.x, WSL's 172.x and any other 100.x stay off the pane.
        // First since 2026-10-17 (the user's ask), the home network's after it.
        var tailnet = adapters.Where(IsTailscaleAdapter).SelectMany(a => a.Addresses).Where(IsTailnet).Select(a => new WebUiAddress(a.ToString(), true));
        return [.. tailnet.Concat(home).DistinctBy(a => a.Host, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether <paramref name="adapter"/> is Tailscale's own (later on 2026-10-14, the user's pick): its name or description says Tailscale
    /// (Windows: "Tailscale" / "Tailscale Tunnel"). Off Windows a <c>utun</c> interface counts too, as macOS names Tailscale's tunnel
    /// <c>utunN</c> with nothing naming Tailscale; its type is not read there (.NET's type for a utun on macOS is unproven), so another
    /// VPN's utun holding a tailnet-range address would pass. Pure.
    /// </summary>
    public static bool IsTailscaleAdapter(WebUiAdapter adapter, bool windows)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.Names.Contains("tailscale", StringComparison.OrdinalIgnoreCase)
            || (!windows && adapter.Names.StartsWith("utun", StringComparison.Ordinal));
    }

    /// <summary><see cref="IsTailscaleAdapter(WebUiAdapter, bool)"/> on this machine.</summary>
    public static bool IsTailscaleAdapter(WebUiAdapter adapter) => IsTailscaleAdapter(adapter, OperatingSystem.IsWindows());

    /// <summary>Whether <paramref name="address"/> is in Tailscale's tailnet range, the carrier-grade NAT block 100.64.0.0/10.</summary>
    public static bool IsTailnet(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        byte[] bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    /// <summary>Whether <paramref name="type"/> is a wired or wireless card rather than a tunnel, a dial-up link or the loopback.</summary>
    public static bool IsPhysical(NetworkInterfaceType type) => type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
        or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit;

    private static string Reason(Exception e) =>
        e is SocketException { SocketErrorCode: SocketError.AccessDenied } || e.InnerException is SocketException { SocketErrorCode: SocketError.AccessDenied }
            ? "access denied"
            : e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } || e.InnerException is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                ? "address in use"
                : e.Message;

    private static async Task StopQuietlyAsync(IWebUiHost host)
    {
        try
        {
            await host.StopAsync().ConfigureAwait(false);
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Debug(Category, "Web UI: the server's stop failed: " + e.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_host is { } host)
            {
                Volatile.Write(ref _host, null);
                await StopQuietlyAsync(host).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
