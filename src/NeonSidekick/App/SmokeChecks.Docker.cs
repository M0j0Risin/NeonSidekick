using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
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
    /// and <see cref="DockerRedaction"/>'s generated patterns must hide an environment value and a password flag's word. Since
    /// 2026-10-23 (the Docker log window) a follow too: the server holds one answer open after a timestamped frame, and
    /// <see cref="DockerClient.FollowLogsAsync"/> must hand that line over, stamp stripped, then let go within a second of a cancel,
    /// the server seeing the client go: the streamed read and its cancel over the real pipe in the AOT binary. No
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
        // Five connections: the session's client agrees the version and lists, then a second client agrees it again, reads the log
        // and follows it (the held one, 2026-10-23).
        var followGone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeDockerAsync(pipe, SmokeDockerAnswer, connections: 5, stop.Token, followGone);
        using var client = new DockerClient(pipe);
        using var session = new DockerSession(() => new Settings.AppSettingsData { DockerEnginePipe = pipe });
        return await DockerRoundTripAsync(name, "pipe", client, session, server, stop, transportHeld: true, followGone).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>docker:unix-socket</c> (2026-10-11, the Docker tools on a Mac): <see cref="ProbeDocker"/>'s twin over a Unix socket, with no
    /// Docker installed. An in-process listener on a socket in a temp folder (<see cref="ServeDockerSocketAsync"/>, the same canned
    /// engine) and the real <see cref="DockerClient"/> — its connect opening <see cref="DockerSocket.ConnectAsync"/> to the path the
    /// setting names, the look's first step — must agree the version, read the container, demultiplex the log and redact. Proves the
    /// trimmed socket classes and the connect callback on the published binary. "skipped: not macOS" elsewhere.
    /// </summary>
    public static SmokeCheck ProbeDockerSocket()
    {
        const string name = "docker:unix-socket";
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck(name, true, "skipped: not macOS");
        }

        // A short folder: a Unix socket's path is 104 characters at most on macOS, and the temp folder is already about 50.
        var folder = Directory.CreateTempSubdirectory("nsd-");
        try
        {
            return ProbeDockerSocketAsync(name, Path.Join(folder.FullName, "engine.sock")).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Files.WorkingDirectory.DeleteDirectoryQuietly(folder.FullName);
        }
    }

    private static async Task<SmokeCheck> ProbeDockerSocketAsync(string name, string path)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        var followGone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeDockerSocketAsync(listener, SmokeDockerAnswer, connections: 5, stop.Token, followGone);
        using var client = new DockerClient(path);
        using var session = new DockerSession(() => new Settings.AppSettingsData { DockerEnginePipe = DockerSocket.UnixScheme + path });
        bool socket = client.Transport == DockerTransport.Socket && session.Client().Pipe == path;
        return await DockerRoundTripAsync(name, "socket", client, session, server, stop, socket, followGone).ConfigureAwait(false);
    }

    /// <summary>
    /// The two probes' shared body (code review of 9c145d1, 2026-10-11, one copy where two would drift): the session's client
    /// agrees the version and lists, a second client of its own reads the log (so the session's is not the only one tried), then
    /// the reads, the redaction and the agreed version are checked, with <paramref name="transportHeld"/> (the probe's own
    /// transport check) beside them. Whatever the outcome <paramref name="server"/> is stopped and awaited before the caller's
    /// listener goes: a failed list or log left it waiting on an accept, its fault never observed.
    /// </summary>
    private static async Task<SmokeCheck> DockerRoundTripAsync(string name, string transport, DockerClient client, DockerSession session, Task server, CancellationTokenSource stop, bool transportHeld, TaskCompletionSource followGone)
    {
        try
        {
            var (containers, error) = await session.ContainersAsync(stop.Token).ConfigureAwait(false);
            if (containers is null)
            {
                return new SmokeCheck(name, false, "the container list failed: " + error);
            }

            var logs = await client.LogsAsync("smoke_web", 10, null, timestamps: false, DockerLogStreams.Both, TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false);
            if (logs.Error is not null)
            {
                return new SmokeCheck(name, false, "the log failed: " + logs.Error);
            }

            var (followed, letGo) = await DockerFollowLegAsync(client, followGone, stop.Token).ConfigureAwait(false);
            await server.ConfigureAwait(false);
            bool listed = containers is [{ Name: "smoke_web", Health: "healthy" }];
            bool demuxed = logs.Lines is ["hello", "warn"];
            bool redacted = DockerRedaction.Env("SMOKE_PASSWORD=hunter2") == "SMOKE_PASSWORD=" + DockerText.Redacted
                && DockerRedaction.Args(["--password", "hunter2"]) is [_, DockerText.Redacted];
            bool agreed = client.Prefix == "/v1.47";
            return listed && demuxed && redacted && agreed && transportHeld && followed && letGo
                ? new SmokeCheck(name, true, transport + " round trip: API agreed at 1.47, 1 container read, 2 log frames demultiplexed, a follow line read and canceled, an env value and a password flag redacted")
                : new SmokeCheck(name, false, $"listed {listed}, demultiplexed {demuxed} ({string.Join(" | ", logs.Lines)}), followed {followed}, let go {letGo}, redacted {redacted}, agreed {agreed} ({client.Prefix}), {transport} {transportHeld}");
        }
        finally
        {
            if (!server.IsCompletedSuccessfully)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                try
                {
                    await server.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Observed only: the verdict is already made (or the exception on its way out of the try).
                }
            }
        }
    }

    /// <summary>
    /// The follow leg (2026-10-23): the held answer's one line handed over with its stamp stripped, then a cancel that returns
    /// within a second and a server that saw the client go (within five).
    /// </summary>
    private static async Task<(bool Followed, bool LetGo)> DockerFollowLegAsync(DockerClient client, TaskCompletionSource followGone, CancellationToken cancellationToken)
    {
        var line = new TaskCompletionSource<DockerLogLine>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var follow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = client.FollowLogsAsync("smoke_web", 1, null, l => line.TrySetResult(l), TimeSpan.FromSeconds(10), follow.Token);
        bool followed = await Task.WhenAny(line.Task, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)).ConfigureAwait(false) == line.Task
            && line.Task.Result is { Text: SmokeFollowLine, IsError: false, Stamp: not null };
        var canceled = System.Diagnostics.Stopwatch.StartNew();
        await follow.CancelAsync().ConfigureAwait(false);
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The cancel's own way out.
        }

        bool quick = canceled.Elapsed < TimeSpan.FromSeconds(1);
        bool gone = await Task.WhenAny(followGone.Task, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)).ConfigureAwait(false) == followGone.Task;
        return (followed, quick && gone);
    }

    /// <summary>The followed log's one line (after its stamp) in the smoke's held answer.</summary>
    private const string SmokeFollowLine = "following";

    /// <summary>The canned engine the smoke probe talks to: <c>/version</c>, one container, a two-frame log, a held follow.</summary>
    private static (string ContentType, byte[] Body) SmokeDockerAnswer(string path)
    {
        if (path.StartsWith("/version", StringComparison.Ordinal))
        {
            return ("application/json", Encoding.UTF8.GetBytes("""{"Platform":{"Name":"Smoke Desktop"},"Version":"29.0.0","ApiVersion":"1.55","MinAPIVersion":"1.40","Os":"linux","Arch":"amd64"}"""));
        }

        if (path.Contains("/logs?follow=1", StringComparison.Ordinal))
        {
            return (DockerLogStream.MultiplexedType, DockerFrame(1, "2026-10-23T14:02:11.123456789Z " + SmokeFollowLine + "\n"));
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
    internal static async Task ServeDockerAsync(string pipe, Func<string, (string ContentType, byte[] Body)> answer, int connections, CancellationToken cancellationToken, TaskCompletionSource? followGone = null)
    {
        for (int i = 0; i < connections; i++)
        {
            await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (await AnswerOneAsync(server, answer, cancellationToken, followGone).ConfigureAwait(false))
            {
                continue;   // a held follow: the client is gone, nothing left to drain
            }

            if (OperatingSystem.IsWindows())
            {
                // The answer read before the instance goes: a closed pipe can drop what the client has not taken.
                server.WaitForPipeDrain();
            }
        }
    }

    /// <summary>
    /// <see cref="ServeDockerAsync"/> over a Unix socket (2026-10-11, the Mac's twin): <paramref name="connections"/> connections
    /// accepted on <paramref name="listener"/> (bound and listening, the caller's), one request each.
    /// </summary>
    internal static async Task ServeDockerSocketAsync(Socket listener, Func<string, (string ContentType, byte[] Body)> answer, int connections, CancellationToken cancellationToken, TaskCompletionSource? followGone = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        for (int i = 0; i < connections; i++)
        {
            var accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = new NetworkStream(accepted, ownsSocket: true);
            await AnswerOneAsync(stream, answer, cancellationToken, followGone).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One request read off <paramref name="stream"/> and its canned answer written, <c>Connection: close</c>. A follow
    /// (<c>follow=1</c>, 2026-10-23) is held: no length, so its body runs until the close, then a wait (five seconds at most) for
    /// the client to go, told to <paramref name="followGone"/>. True for a held answer.
    /// </summary>
    private static async Task<bool> AnswerOneAsync(Stream stream, Func<string, (string ContentType, byte[] Body)> answer, CancellationToken cancellationToken, TaskCompletionSource? followGone = null)
    {
        string head = await ReadRequestHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        string path = head.Split('\n')[0].Split(' ') is [_, var target, ..] ? target : "/";
        var (contentType, body) = answer(path);
        bool held = path.Contains("follow=1", StringComparison.Ordinal);
        string length = held ? "" : "\r\nContent-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string header = "HTTP/1.1 200 OK\r\nContent-Type: " + contentType + length + "\r\nApi-Version: 1.55\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!held)
        {
            return false;
        }

        using var cap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cap.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            byte[] one = new byte[1];
            while (await stream.ReadAsync(one, cap.Token).ConfigureAwait(false) > 0)
            {
                // Nothing the client sends matters: only its going.
            }

            followGone?.TrySetResult();
        }
        catch (IOException)
        {
            followGone?.TrySetResult();   // a broken pipe is the client gone too
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The five seconds ran out: the client never let go, and the follow leg says so.
        }

        return true;
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
