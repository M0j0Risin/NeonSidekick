using System.Net.Sockets;
using System.Text;

namespace NeonSidekick.Claude;

/// <summary>
/// This app's executable as the Claude CLI server's MCP server (2026-09-30): <c>NeonSidekick --mcp-relay &lt;address&gt;
/// &lt;key&gt;</c>, the command the CLI's <c>--mcp-config</c> names (<see cref="ClaudeArguments.McpConfig"/>). The CLI
/// speaks MCP over this process's stdio; the relay connects to the app's loopback listener
/// (<see cref="ClaudeMcpServer"/>), sends the key as its first line, and from then on copies bytes both ways — nothing is
/// parsed, so the SDK's server in the app is the only MCP code in play. It ends when either side closes: the CLI's exit
/// closes stdin, the app's stop closes the socket. It reads no settings and draws nothing: stdout is the MCP pipe.
///
/// <para>A child of the CLI, not a process-start site of the app's: the CLI starts it, and it dies with the CLI's job
/// (<see cref="ClaudeServerHost"/> puts the CLI in the app's kill-on-close job, which takes its children with it).</para>
/// </summary>
public static class McpRelay
{
    /// <summary>The flag that starts the relay: the first of exactly three arguments. Pinned.</summary>
    public const string Flag = "--mcp-relay";

    /// <summary>Whether <paramref name="args"/> ask for the relay: the flag, an address and a key, nothing else.</summary>
    public static bool Asked(IReadOnlyList<string> args) =>
        args is { Count: 3 } && string.Equals(args[0], Flag, StringComparison.Ordinal);

    /// <summary>The relay's arguments for <paramref name="address"/> and <paramref name="token"/>, the flag first.</summary>
    public static IReadOnlyList<string> Arguments(string address, string token) => [Flag, address, token];

    /// <summary>
    /// Runs the relay over this process's standard streams until either side closes. 0 when it ran, 1 when the listener
    /// could not be reached (said on stderr, which the CLI logs).
    /// </summary>
    public static Task<int> RunAsync(string address, string token) =>
        RunAsync(address, token, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError());

    /// <summary><see cref="RunAsync(string, string)"/> over given streams (the tests').</summary>
    public static async Task<int> RunAsync(string address, string token, Stream input, Stream output, Stream error)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(token);
        int colon = address.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(address.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port))
        {
            await WriteAsync(error, ClaudeCliText.RelayBadAddress(address)).ConfigureAwait(false);
            return 1;
        }

        using var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(address[..colon], port).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            await WriteAsync(error, ClaudeCliText.RelayCouldNotConnect(address, ex.Message)).ConfigureAwait(false);
            return 1;
        }

        var socket = client.GetStream();
        await socket.WriteAsync(Encoding.ASCII.GetBytes(token + "\n")).ConfigureAwait(false);

        using var done = new CancellationTokenSource();
        var up = PumpAsync(input, socket, done.Token);
        var down = PumpAsync(socket, output, done.Token);
        await Task.WhenAny(up, down).ConfigureAwait(false);
        done.Cancel();
        try
        {
            client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }

        return 0;
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await to.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // Either side went away: the relay's end.
        }
    }

    private static async Task WriteAsync(Stream error, string line)
    {
        try
        {
            await error.WriteAsync(Encoding.UTF8.GetBytes(line + Environment.NewLine)).ConfigureAwait(false);
            await error.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }
}
