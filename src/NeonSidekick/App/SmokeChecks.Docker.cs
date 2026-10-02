using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using NeonSidekick.Docker;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>docker:pipe-transport</c> (2026-10-02, the Docker tools): their transport on the published binary, with no Docker. An
    /// in-process named-pipe server (<see cref="ServeDockerAsync"/>) answers <c>/version</c>, the container list and a two-frame
    /// multiplexed log; the real <see cref="DockerClient"/> — a <see cref="SocketsHttpHandler"/> whose connect opens the pipe —
    /// must agree the API version, read the container's name and health and demultiplex both log lines (colour codes gone),
    /// and <see cref="DockerRedaction"/>'s generated patterns must hide an environment value and a password flag's word. No
    /// native library is involved (no <c>RequiredNativeLibraries</c> line): the probe proves the trimmed pipes, the connect
    /// callback and the hand-read JSON.
    /// </summary>
    public static SmokeCheck ProbeDocker()
    {
        const string name = "docker:pipe-transport";
        try
        {
            return ProbeDockerAsync(name).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task<SmokeCheck> ProbeDockerAsync(string name)
    {
        string pipe = "neonsidekick-smoke-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // Four connections: the session's client agrees the version and lists, then a second client agrees it again and reads the log.
        var server = ServeDockerAsync(pipe, SmokeDockerAnswer, connections: 4, stop.Token);
        using var client = new DockerClient(pipe);
        using var session = new DockerSession(() => new Settings.AppSettingsData { DockerEnginePipe = pipe });
        var (containers, error) = await session.ContainersAsync(stop.Token).ConfigureAwait(false);
        if (containers is null)
        {
            return new SmokeCheck(name, false, "the container list failed: " + error);
        }

        // A second client of its own for the log, so the session's is not the only one tried.
        var logs = await client.LogsAsync("smoke_web", 10, null, timestamps: false, DockerLogStreams.Both, TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false);
        await server.ConfigureAwait(false);
        if (logs.Error is not null)
        {
            return new SmokeCheck(name, false, "the log failed: " + logs.Error);
        }

        bool listed = containers is [{ Name: "smoke_web", Health: "healthy" }];
        bool demuxed = logs.Lines is ["hello", "warn"];
        bool redacted = DockerRedaction.Env("SMOKE_PASSWORD=hunter2") == "SMOKE_PASSWORD=" + DockerText.Redacted
            && DockerRedaction.Args(["--password", "hunter2"]) is [_, DockerText.Redacted];
        bool agreed = client.Prefix == "/v1.47";
        return listed && demuxed && redacted && agreed
            ? new SmokeCheck(name, true, "pipe round trip: API agreed at 1.47, 1 container read, 2 log frames demultiplexed, an env value and a password flag redacted")
            : new SmokeCheck(name, false, $"listed {listed}, demultiplexed {demuxed} ({string.Join(" | ", logs.Lines)}), redacted {redacted}, agreed {agreed} ({client.Prefix})");
    }

    /// <summary>The canned engine the smoke probe talks to: <c>/version</c>, one container, a two-frame log.</summary>
    private static (string ContentType, byte[] Body) SmokeDockerAnswer(string path)
    {
        if (path.StartsWith("/version", StringComparison.Ordinal))
        {
            return ("application/json", Encoding.UTF8.GetBytes("""{"Platform":{"Name":"Smoke Desktop"},"Version":"29.0.0","ApiVersion":"1.55","MinAPIVersion":"1.40","Os":"linux","Arch":"amd64"}"""));
        }

        if (path.Contains("/logs", StringComparison.Ordinal))
        {
            return (DockerLogStream.MultiplexedType, [.. DockerFrame(1, "hello\n"), .. DockerFrame(2, "\u001b[31mwarn\u001b[0m\r\n")]);
        }

        return ("application/json", Encoding.UTF8.GetBytes("""[{"Id":"5a0e1c2b3d4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b","Names":["/smoke_web"],"Image":"nginx:smoke","ImageID":"sha256:00","State":"running","Status":"Up 1 minute (healthy)","Created":1790000000,"Ports":[{"IP":"0.0.0.0","PrivatePort":80,"PublicPort":8080,"Type":"tcp"}],"Labels":{}}]"""));
    }

    /// <summary>One multiplexed log frame: the stream byte, three zeros, the length big-endian, the text.</summary>
    internal static byte[] DockerFrame(byte stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] frame = new byte[8 + payload.Length];
        frame[0] = stream;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    /// <summary>
    /// A tiny HTTP/1.1 server on named pipe <paramref name="pipe"/>, the Docker engine's stand-in for the smoke probe and the
    /// transport test: <paramref name="connections"/> connections, one request each (the answer says <c>Connection: close</c>, so
    /// the client opens a fresh pipe instance for the next), the request line's path handed to <paramref name="answer"/>.
    /// </summary>
    internal static async Task ServeDockerAsync(string pipe, Func<string, (string ContentType, byte[] Body)> answer, int connections, CancellationToken cancellationToken)
    {
        for (int i = 0; i < connections; i++)
        {
            await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            string head = await ReadRequestHeadAsync(server, cancellationToken).ConfigureAwait(false);
            string path = head.Split('\n')[0].Split(' ') is [_, var target, ..] ? target : "/";
            var (contentType, body) = answer(path);
            string header = "HTTP/1.1 200 OK\r\nContent-Type: " + contentType + "\r\nContent-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\nApi-Version: 1.55\r\nConnection: close\r\n\r\n";
            await server.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
            await server.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await server.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsWindows())
            {
                // The answer read before the instance goes: a closed pipe can drop what the client has not taken.
                server.WaitForPipeDrain();
            }
        }
    }

    private static async Task<string> ReadRequestHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        byte[] one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            head.Append((char)one[0]);
        }

        return head.ToString();
    }
}
