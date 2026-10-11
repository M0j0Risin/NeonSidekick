using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Docker;

/// <summary>How the client reaches the engine: Docker Desktop's named pipe on Windows, a Unix socket on a Mac, nothing elsewhere.</summary>
public enum DockerTransport
{
    /// <summary>Windows: <see cref="DockerPipe"/>.</summary>
    Pipe,

    /// <summary>macOS: <see cref="DockerSocket"/> (2026-10-11).</summary>
    Socket,

    /// <summary>Any other system: every call fails with <see cref="DockerText.NotHere"/>.</summary>
    None,
}

/// <summary>
/// A transport failure that already knows its sentence (2026-10-11): no socket found, a path too long for a Unix socket, no
/// transport on this system. Thrown from the connect callback (so it arrives inside an <see cref="HttpRequestException"/>) and
/// said as it is by <see cref="DockerClient"/>'s explanation.
/// </summary>
public sealed class DockerTransportException : IOException
{
    public DockerTransportException(string sentence)
        : base(sentence)
    {
    }

    public DockerTransportException()
    {
    }

    public DockerTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A socket found: its full path, and what named it (<c>the setting</c>, <c>DOCKER_HOST</c>, <c>the Docker context …</c>, <c>a usual place</c>).</summary>
public sealed record DockerSocketFound(string Path, string Source);

/// <summary>
/// One look for the engine's socket: the socket (null when none was found), every place looked at (as written for a sentence,
/// <c>~</c> for the home), and a <c>DOCKER_HOST</c> that was set but is not a Unix socket (for the one warning).
/// </summary>
public sealed record DockerSocketLookup(DockerSocketFound? Socket, IReadOnlyList<string> Looked, string? IgnoredHost);

/// <summary>
/// The Docker engine's Unix socket on a Mac (2026-10-11, the user's ask: the Docker tools on the Mac too). The Engine API above
/// the transport was always plain HTTP; only <see cref="DockerPipe"/> was Windows'. Here the client's connect opens a
/// <see cref="Socket"/> to a <see cref="UnixDomainSocketEndPoint"/>, and still nothing is started — no <c>docker</c>, no
/// <c>docker-compose</c>, no new process-start site.
///
/// <para>Where the socket is (<see cref="Resolve"/>), in the Docker CLI's own order, so the app reaches whatever engine
/// <c>docker</c> would: the setting <c>Docker engine socket</c> (the same field as Windows' <c>Docker engine pipe</c>,
/// <c>DockerEnginePipe</c>; a path or <c>unix://</c> URL, used alone); then <c>DOCKER_HOST</c> when it is <c>unix://</c> (a
/// <c>tcp://</c> or <c>ssh://</c> one is not ours: one warning, then the look goes on); then the CLI's current context
/// (<c>DOCKER_CONTEXT</c>, else <c>currentContext</c> in <c>~/.docker/config.json</c>, then that context's <c>meta.json</c> —
/// how OrbStack and Colima are found when they are the active engine, without <c>DOCKER_HOST</c>); then the usual places,
/// <see cref="UsualPlaces"/>. Measured on this Mac (Docker Desktop 4.42): the context <c>desktop-linux</c> names
/// <c>~/.docker/run/docker.sock</c>, and <c>/var/run/docker.sock</c> is a link to it (Desktop's "Allow the default Docker
/// socket"). Windows never calls any of this: it never reads <c>DOCKER_HOST</c> or the CLI's files.</para>
///
/// <para>Only <c>currentContext</c> is read from <c>config.json</c>, which also holds the registry logins' store; nothing of it is
/// logged. A link counts only when its target is there (<see cref="Exists"/>): <c>File.Exists</c> passes a dangling one, and
/// <c>/var/run/docker.sock</c> may dangle once Desktop quits.</para>
/// </summary>
public static class DockerSocket
{
    /// <summary>The scheme the CLI writes a socket with, <c>unix:///path</c>.</summary>
    public const string UnixScheme = "unix://";

    /// <summary>The CLI's own variable for the engine's address. Read through <see cref="Settings.EnvironmentOverrides.System"/>.</summary>
    public const string HostVariable = "DOCKER_HOST";

    /// <summary>The CLI's variable naming the context in force, ahead of <c>config.json</c>'s <c>currentContext</c>.</summary>
    public const string ContextVariable = "DOCKER_CONTEXT";

    /// <summary>The CLI's variable for its configuration folder (default <c>~/.docker</c>).</summary>
    public const string ConfigVariable = "DOCKER_CONFIG";

    /// <summary>
    /// The places an engine's socket usually is, in the order looked at: Docker Desktop's own, the default socket (Desktop's
    /// opt-in link, or another engine's), OrbStack's, Colima's default profile's, Rancher Desktop's.
    /// </summary>
    public static readonly IReadOnlyList<string> UsualPlaces =
    [
        "~/.docker/run/docker.sock",
        "/var/run/docker.sock",
        "~/.orbstack/run/docker.sock",
        "~/.colima/default/docker.sock",
        "~/.rd/docker.sock",
    ];

    /// <summary>The source word of a socket the setting names.</summary>
    public const string FromSetting = "the setting";

    /// <summary>The source word of a socket found in one of <see cref="UsualPlaces"/>.</summary>
    public const string FromUsualPlace = "a usual place";

    /// <summary>The source word of a socket a CLI context names.</summary>
    public static string FromContext(string name) => "the Docker context " + name;

    /// <summary>The transport this system's client uses: the pipe on Windows, the socket on a Mac, none elsewhere.</summary>
    public static DockerTransport Here =>
        OperatingSystem.IsWindows() ? DockerTransport.Pipe : OperatingSystem.IsMacOS() ? DockerTransport.Socket : DockerTransport.None;

    /// <summary>
    /// The setting's value as a socket: a path (<c>unix://</c> taken off, a leading <c>~</c> kept so the profile follows a moved
    /// home), or <c>""</c> for "find it". Blank, and every Windows pipe form — the compiled default <c>docker_engine</c>,
    /// <c>\\.\pipe\name</c>, <c>npipe:…</c>, <c>//./pipe/name</c>, or any word that is not an absolute or <c>~</c> path — mean
    /// find it, so a profile still holding the Windows default works on a Mac. Pure.
    /// </summary>
    public static string Normalize(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith(UnixScheme, StringComparison.OrdinalIgnoreCase))
        {
            text = text[UnixScheme.Length..].Trim();
        }

        bool path = text.StartsWith('/') && !text.StartsWith("//", StringComparison.Ordinal) || text == "~" || text.StartsWith("~/", StringComparison.Ordinal);
        return path ? text : "";
    }

    /// <summary>A path with a leading <c>~</c> made <paramref name="home"/>'s. Pure.</summary>
    public static string Expand(string path, string home)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) ? Join(home, path[2..]) : path;
    }

    /// <summary>A path as the sentences write it: under <paramref name="home"/> it starts <c>~/</c>. Pure.</summary>
    public static string Display(string path, string home)
    {
        ArgumentNullException.ThrowIfNull(path);
        string root = home.TrimEnd('/');
        return root.Length > 0 && path.StartsWith(root + "/", StringComparison.Ordinal) ? "~" + path[root.Length..] : path;
    }

    /// <summary>
    /// The engine's socket for <paramref name="setting"/>, in the order the class names. The setting and <c>DOCKER_HOST</c> are
    /// used whether their socket is there or not (the user named it: the connect's error says it); a context's and the usual
    /// places count only when there. Pure but for the three readers.
    /// </summary>
    /// <param name="setting">The <c>DockerEnginePipe</c> value, any form <see cref="Normalize"/> reads.</param>
    /// <param name="environment">The variables (<see cref="Settings.EnvironmentOverrides.System"/>); null or blank for unset.</param>
    /// <param name="home">The user's home folder.</param>
    /// <param name="exists">Whether a socket is at a full path (<see cref="Exists"/>).</param>
    /// <param name="readFile">A file's text, or null when it cannot be read.</param>
    public static DockerSocketLookup Resolve(string? setting, Func<string, string?> environment, string home, Func<string, bool> exists, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(readFile);
        string chosen = Normalize(setting);
        if (chosen.Length > 0)
        {
            string path = Expand(chosen, home);
            return new DockerSocketLookup(new DockerSocketFound(path, FromSetting), [Display(path, home)], null);
        }

        var looked = new List<string>();
        string? ignored = null;
        if (Blank(environment(HostVariable)) is { } host)
        {
            if (host.StartsWith(UnixScheme, StringComparison.OrdinalIgnoreCase) && Normalize(host) is { Length: > 0 } hostPath)
            {
                string path = Expand(hostPath, home);
                return new DockerSocketLookup(new DockerSocketFound(path, HostVariable), [Display(path, home)], null);
            }

            ignored = host;
        }

        if (ContextSocket(environment, home, readFile) is { } context)
        {
            string path = Expand(context.Path, home);
            if (exists(path))
            {
                return new DockerSocketLookup(new DockerSocketFound(path, FromContext(context.Name)), [Display(path, home)], ignored);
            }

            looked.Add(Display(path, home));
        }

        foreach (string place in UsualPlaces)
        {
            string path = Expand(place, home);
            string shown = Display(path, home);
            if (!looked.Contains(shown, StringComparer.Ordinal))
            {
                looked.Add(shown);
            }

            if (exists(path))
            {
                return new DockerSocketLookup(new DockerSocketFound(path, FromUsualPlace), [shown], ignored);
            }
        }

        return new DockerSocketLookup(null, looked, ignored);
    }

    /// <summary>
    /// The CLI's current context's socket: <c>DOCKER_CONTEXT</c>, else <c>currentContext</c> in <c>$DOCKER_CONFIG/config.json</c>
    /// (<c>~/.docker</c> by default); then <c>contexts/meta/&lt;sha256 of the name&gt;/meta.json</c>'s <c>Endpoints.docker.Host</c>
    /// when it is <c>unix://</c>. Null for <c>default</c> (the CLI's built-in context names no file), a file missing or unreadable,
    /// or another scheme.
    /// </summary>
    private static (string Name, string Path)? ContextSocket(Func<string, string?> environment, string home, Func<string, string?> readFile)
    {
        string folder = Blank(environment(ConfigVariable)) is { } config ? Expand(config, home) : Join(home, ".docker");
        string? name = Blank(environment(ContextVariable)) ?? JsonString(readFile(Join(folder, "config.json")), "currentContext");
        if (name is null || string.Equals(name, "default", StringComparison.Ordinal))
        {
            return null;
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        string? meta = readFile(Join(folder, "contexts/meta/" + hash + "/meta.json"));
        if (meta is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(meta);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Object
                && endpoints.TryGetProperty("docker", out var docker) && docker.ValueKind == JsonValueKind.Object
                && docker.TryGetProperty("Host", out var host) && host.ValueKind == JsonValueKind.String
                && host.GetString() is { } url && url.StartsWith(UnixScheme, StringComparison.OrdinalIgnoreCase)
                && Normalize(url) is { Length: > 0 } path)
            {
                return (name, path);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>One string member of a JSON object's text, or null (no text, not JSON, no such string).</summary>
    private static string? JsonString(string? text, string member)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
                ? Blank(value.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Two parts of a Unix path joined with <c>/</c>, whatever the system (the pure tests run on Windows too, where <c>Path.Join</c> writes a backslash).</summary>
    private static string Join(string folder, string rest) => folder.TrimEnd('/') + "/" + rest;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Whether something is at <paramref name="path"/>, a link counting only when its final target is there.</summary>
    public static bool Exists(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null)
            {
                return file.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true };
            }

            return file.Exists;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A file's text, or null when it is missing or unreadable.</summary>
    public static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int s_warned;

    /// <summary>
    /// <see cref="Resolve"/> against this machine (the home, the real files), warning once per run of a <c>DOCKER_HOST</c> that is
    /// not a Unix socket.
    /// </summary>
    public static DockerSocketLookup Locate(string? setting, Func<string, string?> environment)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var lookup = Resolve(setting, environment, home, Exists, ReadFile);
        if (lookup.IgnoredHost is { } host && Interlocked.Exchange(ref s_warned, 1) == 0)
        {
            DiagnosticLog.Warn(DockerText.Category, DockerText.HostIgnored(host));
        }

        return lookup;
    }

    /// <summary>
    /// The process's variables for the places with no reader handed to them (the settings row's <c>auto: …</c>): set once by
    /// <c>Program</c> to <see cref="Settings.EnvironmentOverrides.System"/>, as <c>MicrophoneText.Terminal</c> is; none until then.
    /// The clients get theirs through <see cref="DockerSession"/>.
    /// </summary>
    public static Func<string, string?> MachineEnvironment { get; set; } = _ => null;

    /// <summary>The settings row's value on a Mac: the socket set (as written), or <c>auto: </c> and what the look finds now.</summary>
    public static string RowValue(string? setting)
    {
        string chosen = Normalize(setting);
        if (chosen.Length > 0)
        {
            return chosen;
        }

        return Locate(setting, MachineEnvironment).Socket is { } found ? AutoWord + ": " + Display(found.Path, Home) : AutoWord + ": " + NoneFoundWord;
    }

    /// <summary>The row's word for "find it".</summary>
    public const string AutoWord = "auto";

    /// <summary>The row's word when the look finds nothing.</summary>
    public const string NoneFoundWord = "none found";

    /// <summary>The home as <see cref="Display(string, string)"/> takes it, for the client's sentences.</summary>
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// One connected stream to the socket at <paramref name="path"/>, within <see cref="DockerPipe.ConnectTimeout"/>. A
    /// <see cref="SocketException"/> when nothing is there (<c>AddressNotAvailable</c>, measured on macOS 15: a missing file;
    /// <c>ConnectionRefused</c>: a file left behind) or macOS refuses (<c>AccessDenied</c>); a <see cref="TimeoutException"/> when
    /// it never answers; a <see cref="DockerTransportException"/> for a path longer than a Unix socket takes. The client turns
    /// each into its sentence.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(string path, CancellationToken cancellationToken)
    {
        UnixDomainSocketEndPoint endpoint;
        try
        {
            endpoint = new UnixDomainSocketEndPoint(path);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new DockerTransportException(DockerText.SocketPathTooLong(Display(path, Home)));
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(DockerPipe.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(endpoint, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("No answer on " + path + ".");
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
