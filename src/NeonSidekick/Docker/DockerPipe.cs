using System.IO.Pipes;

namespace NeonSidekick.Docker;

/// <summary>
/// The Docker engine's named pipe (2026-10-02, the user's ask: control Docker Desktop from the app; the user's call: the
/// Engine API over the pipe and nothing else — no <c>docker.exe</c>, no new process-start site). Docker Desktop answers
/// on <c>\\.\pipe\docker_engine</c> (and on <c>dockerDesktopLinuxEngine</c>, the CLI context's own); the name is the setting
/// <c>Docker engine pipe</c>, written bare, as <c>\\.\pipe\name</c> or as the CLI's <c>npipe:////./pipe/name</c>.
/// <see cref="ConnectAsync"/> is what the client's <see cref="System.Net.Http.SocketsHttpHandler.ConnectCallback"/> calls: every HTTP
/// connection is a fresh pipe instance, the <see cref="Web.LanPolicy.ConnectAsync"/> shape over a pipe instead of a socket.
/// Never probed with <c>File.Exists</c>: opening a pipe that way can take one of its instances.
/// </summary>
public static class DockerPipe
{
    /// <summary>Docker Desktop's engine pipe, the setting's default.</summary>
    public const string DefaultName = "docker_engine";

    /// <summary>How long a connect waits for a pipe instance: a missing pipe (Desktop not running) is a timeout.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private const string WindowsPrefix = @"\\.\pipe\";
    private const string CliPrefix = "npipe:";

    /// <summary>
    /// The bare pipe name of a setting's value: <c>docker_engine</c>, <c>\\.\pipe\docker_engine</c>,
    /// <c>npipe:////./pipe/docker_engine</c> and <c>//./pipe/docker_engine</c> are all <c>docker_engine</c>; blank is
    /// <see cref="DefaultName"/>. Pure.
    /// </summary>
    public static string Normalize(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith(CliPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[CliPrefix.Length..];
        }

        text = text.Replace('/', '\\').TrimStart('\\');
        const string local = @".\pipe\";
        if (text.StartsWith(local, StringComparison.OrdinalIgnoreCase))
        {
            text = text[local.Length..];
        }

        text = text.Trim('\\').Trim();
        return text.Length == 0 ? DefaultName : text;
    }

    /// <summary>The pipe as Windows writes it, <c>\\.\pipe\docker_engine</c>: the error sentences' word for it. Pure.</summary>
    public static string Display(string name) => WindowsPrefix + Normalize(name);

    /// <summary>
    /// One connected instance of pipe <paramref name="name"/> (bare), within <see cref="ConnectTimeout"/>: a
    /// <see cref="TimeoutException"/> when nothing serves it, an <see cref="UnauthorizedAccessException"/> when Windows
    /// refuses this account — the client turns either into its sentence.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(string name, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", Normalize(name), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
