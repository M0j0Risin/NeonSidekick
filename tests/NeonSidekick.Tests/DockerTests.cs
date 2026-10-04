using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Docker;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The Docker integration (2026-10-02): the pipe's name, the API version agreed, the JSON read over fixtures made in the
/// shapes Docker Desktop 4.90 gave, the log demultiplexer, the stats arithmetic, the redaction, the name lookup, the compose
/// order, the words; the client and the session over a stub engine; the ten tools with the write gate; <c>/docker</c>'s
/// engine and argument list; the turn's offer and rules; and one real round trip over a named pipe.
/// </summary>
public sealed class DockerTests : IDisposable
{
    private const string Host = DockerClient.Host;
    private const string MysqlId = "2dc6d221ea8a008bfa4558be3d820e5e015273f0c3158005caefeaaca7f11177";
    private const string CoreId = "8676a171815c4e2f9e0c1d2b3a4f5e6d7c8b9a0f1e2d3c4b5a6f7e8d9c0b1a2f";
    private const string ValkeyId = "2d93e57491dc0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f6a7b8c9d";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "docker", name));

    private static readonly string ContainersJson = Fixture("containers.json");
    private static readonly string InspectJson = Fixture("inspect-mysql.json");
    private static readonly string StatsJson = Fixture("stats.json");
    private static readonly string VersionJson = Fixture("version.json");

    private readonly AppSettingsData _settings = new() { DockerTools = true };
    private readonly StubHttpMessageHandler _stub = new();
    private readonly ManualTimeProvider _time = new() { UtcNow = new DateTimeOffset(2026, 10, 2, 17, 0, 0, TimeSpan.Zero) };
    private readonly DockerSession _docker;
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private readonly List<string> _asked = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public DockerTests()
    {
        _docker = new DockerSession(() => _settings, pipe => new DockerClient(pipe, new HttpClient(_stub)), _time);
        // One route per exact path (the stub's prefixes would let /volumes answer /volumes/prune); an unknown path is a refused connection.
        _stub.Map(Host, (request, _) => Task.FromResult(Route(request)));
        On("/version", HttpStatusCode.OK, VersionJson);
        On("/v1.47/containers/json", HttpStatusCode.OK, ContainersJson);
        On("/v1.47/containers/" + MysqlId + "/json", HttpStatusCode.OK, InspectJson);
        On("/v1.47/containers/" + MysqlId + "/stats", HttpStatusCode.OK, StatsJson);
        On("/v1.47/containers/" + MysqlId + "/logs", _ => StubHttpMessageHandler.Bytes(HttpStatusCode.OK, [.. Frame(1, "ready\n"), .. Frame(2, "\u001b[33mwarning: low disk\u001b[0m\n"), .. Frame(1, "query ok\r\n")], DockerLogStream.MultiplexedType));
        On("/v1.47/info", HttpStatusCode.OK, Fixture("info.json"));
        On("/v1.47/images/json", HttpStatusCode.OK, Fixture("images.json"));
        On("/v1.47/volumes", HttpStatusCode.OK, Fixture("volumes.json"));
        On("/v1.47/networks", HttpStatusCode.OK, Fixture("networks.json"));
        On("/v1.47/system/df", HttpStatusCode.OK, Fixture("system-df.json"));
    }

    private void On(string path, HttpStatusCode status, string body, string mediaType = "application/json") =>
        _routes[path] = _ => StubHttpMessageHandler.Json(status, body, mediaType);

    private void On(string path, Func<HttpRequestMessage, HttpResponseMessage> answer) => _routes[path] = answer;

    private HttpResponseMessage Route(HttpRequestMessage request) =>
        _routes.TryGetValue(request.RequestUri!.AbsolutePath, out var answer)
            ? answer(request)
            : throw new HttpRequestException($"No connection could be made because the target machine actively refused it (docker{request.RequestUri.AbsolutePath}).");

    public void Dispose()
    {
        _docker.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static byte[] Frame(byte stream, string text) => SmokeChecks.DockerFrame(stream, text);

    private static IReadOnlyList<DockerContainer> Containers() => DockerJson.Containers(ContainersJson)!;

    private static DockerContainer Named(string name) => Containers().Single(c => c.Name == name);

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in pairs)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private IReadOnlyList<AIFunction> Tools(bool? yes = true) => ChatScreen.DockerTools(_docker, Answer(yes));

    private Func<string, CancellationToken, Task<bool?>> Answer(bool? yes) => (question, _) =>
    {
        _asked.Add(question);
        return Task.FromResult(yes);
    };

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction => await Invoke<T>(true, pairs);

    private async Task<string> Invoke<T>(bool? yes, params (string Name, object? Value)[] pairs) where T : AIFunction =>
        (string)(await Tools(yes).OfType<T>().Single().InvokeAsync(Args(pairs)))!;

    private IReadOnlyList<RecordedRequest> Posts() => _stub.Requests.Where(r => r.Method == HttpMethod.Post || r.Method == HttpMethod.Delete).ToList();

    private static async Task<(T Result, List<string> Audit)> CaptureAuditAsync<T>(Func<Task<T>> run)
    {
        var lines = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == DockerText.Category && e.Level == DiagnosticLevel.Info) lock (lines) { lines.Add(e.Message); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            return (await run(), lines);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }
    }

    // ── the pipe and the version ────────────────────────────────────────────

    [Theory]
    [InlineData("docker_engine", "docker_engine")]
    [InlineData(@"\\.\pipe\docker_engine", "docker_engine")]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine", "dockerDesktopLinuxEngine")]
    [InlineData("//./pipe/custom", "custom")]
    [InlineData("  ", DockerPipe.DefaultName)]
    [InlineData(null, DockerPipe.DefaultName)]
    public void ThePipe_IsReadInEveryForm(string? setting, string bare)
    {
        Assert.Equal(bare, DockerPipe.Normalize(setting));
        Assert.Equal(@"\\.\pipe\" + bare, DockerPipe.Display(setting ?? ""));
    }

    [Theory]
    [InlineData("1.55", "1.40", "/v1.47")]   // Docker Desktop 4.90's engine: the pinned version
    [InlineData("1.44", "1.24", "/v1.44")]   // an older engine: its newest
    [InlineData("1.62", "1.50", "/v1.50")]   // a newer engine that dropped 1.47: its oldest
    [InlineData("1.41", "1.12", "/v1.41")]   // the floor
    [InlineData("1.40", "1.12", null)]       // below it
    [InlineData("junk", "1.12", null)]
    public void TheApiVersion_IsThePinnedOne_WhileTheEngineTakesIt(string max, string min, string? prefix) =>
        Assert.Equal(prefix, DockerClient.PrefixFor(max, min));

    [Fact]
    public async Task TheClient_AgreesTheVersionOnce_ThenPrefixesEveryPath()
    {
        var (containers, error) = await _docker.ContainersAsync(CancellationToken.None);
        Assert.Null(error);
        Assert.Equal(6, containers!.Count);
        _ = await _docker.ContainersAsync(CancellationToken.None);
        Assert.Equal(["/version", "/v1.47/containers/json", "/v1.47/containers/json"], _stub.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.Equal("?all=1", _stub.Requests[1].Uri.Query);
        Assert.Equal("/v1.47", _docker.Client().Prefix);
    }

    [Fact]
    public async Task AnEngineTooOld_IsRefused_InWords()
    {
        var old = new StubHttpMessageHandler().Map(Host + "/version", HttpStatusCode.OK, """{"Version":"19.03","ApiVersion":"1.40","MinAPIVersion":"1.12","Os":"linux","Arch":"amd64"}""");
        using var client = new DockerClient("docker_engine", new HttpClient(old));
        var reply = await client.GetAsync("/containers/json", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(reply.Ok);
        Assert.Equal("Error: the Docker engine speaks API 1.40; the Docker tools need 1.41 or later (Docker 20.10+)", reply.Error);
    }

    [Fact]
    public async Task DesktopNotRunning_ADeniedPipe_AndAnyOtherFailure_AreSentences_ACancelThrows()
    {
        var down = new StubHttpMessageHandler().Map(Host, (_, _) => throw new HttpRequestException("connect failed", new TimeoutException("The operation has timed out.")));
        using (var client = new DockerClient("docker_engine", new HttpClient(down)))
        {
            var reply = await client.PingAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(@"Error: Docker Desktop is not running (nothing answers on \\.\pipe\docker_engine); start Docker Desktop, or check Docker engine pipe on the Docker tab of /tools", reply.Error);
        }

        var denied = new StubHttpMessageHandler().Map(Host, (_, _) => throw new HttpRequestException("connect failed", new UnauthorizedAccessException("Access to the path is denied.")));
        using (var client = new DockerClient("docker_engine", new HttpClient(denied)))
        {
            var reply = await client.GetAsync("/info", TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(@"Error: Windows refused access to \\.\pipe\docker_engine; the account may need to be in the docker-users group", reply.Error);
        }

        using (var client = new DockerClient("docker_engine", new HttpClient(new StubHttpMessageHandler())))
        {
            var reply = await client.PingAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.StartsWith(@"Error: cannot reach the Docker engine on \\.\pipe\docker_engine: No connection could be made", reply.Error);
        }

        var hang = new StubHttpMessageHandler().Map(Host, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        using (var client = new DockerClient("docker_engine", new HttpClient(hang)))
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PingAsync(TimeSpan.FromSeconds(5), cancel.Token));
        }
    }

    [Fact]
    public async Task AnEngineRefusal_IsItsOwnMessage_A304IsAlreadySo()
    {
        On("/v1.47/containers/nosuch/json", HttpStatusCode.NotFound, """{"message":"No such container: nosuch"}""");
        On("/v1.47/containers/" + MysqlId + "/start", HttpStatusCode.NotModified, "");
        var missing = await _docker.Client().GetAsync("/containers/nosuch/json", DockerSession.ReadTimeout, CancellationToken.None);
        Assert.Equal("Error: the Docker engine answered 404 to /containers/nosuch/json: No such container: nosuch", missing.Error);
        Assert.Equal(404, missing.Status);
        string done = await _docker.ActAsync("start", [Named("mysql_dev")], null, DockerText.ByUser, CancellationToken.None);
        Assert.Equal("mysql_dev was already started", done);
    }

    // ── the JSON ────────────────────────────────────────────────────────────

    [Fact]
    public void TheContainerList_IsRead_NameHealthPortsProjectNetworksMounts()
    {
        var all = Containers();
        var mysql = all[0];
        Assert.Equal("mysql_dev", mysql.Name);
        Assert.Equal("2dc6d221ea8a", mysql.ShortId);
        Assert.True(mysql.Running);
        Assert.Null(mysql.Health);
        Assert.Equal(2, mysql.Ports.Count);
        Assert.Equal(["bridge"], mysql.Networks);
        Assert.Equal("02c61c74061fc17b3e28d1113765abac5e8e549828536bfe5716f5dabba53148", Assert.Single(mysql.Mounts).Name);
        Assert.Equal("healthy", Named("mariadb_dev").Health);
        Assert.Equal("unhealthy", Named("searxng-core").Health);
        Assert.Equal(("searxng", "core"), (Named("searxng-core").Project, Named("searxng-core").Service));
        Assert.True(Named("homeassistant").Paused);
        Assert.Null(Named("open-webui").Project);
        Assert.Null(DockerJson.Containers("{}"));
        Assert.Null(DockerJson.Containers("not json"));
        Assert.Equal("starting", DockerJson.HealthOf("Up 5 seconds (health: starting)"));
    }

    [Fact]
    public void TheLines_ArePinned()
    {
        Assert.Equal("mysql_dev · running · Up 40 hours · mysql:8.4 · 3306→3306/tcp · 2dc6d221ea8a", DockerText.ContainerLine(Named("mysql_dev")));
        Assert.Equal("searxng-core · running (unhealthy) · Up 3 days (unhealthy) · docker.io/searxng/searxng:latest · 127.0.0.1:8888→8080/tcp · compose searxng/core · 8676a171815c", DockerText.ContainerLine(Named("searxng-core")));
        Assert.Equal("searxng-valkey · running · Up 3 days · docker.io/valkey/valkey:9-alpine · 6379/tcp · compose searxng/valkey · 2d93e57491dc", DockerText.ContainerLine(Named("searxng-valkey")));
        Assert.Equal("Docker: 6 containers (4 running, 1 exited, 1 paused)", DockerText.ContainersHeader(Containers(), ""));
        Assert.Equal("Docker: no containers matching 'x'", DockerText.ContainersHeader([], "'x'"));
        Assert.Equal(["mariadb_dev", "mysql_dev", "searxng-core", "searxng-valkey", "homeassistant", "open-webui"], DockerText.Ordered(Containers()).Select(c => c.Name));
        Assert.Equal("[::1]:9000→9000/tcp", DockerText.Ports([new DockerPort("::1", 9000, 9000, "tcp")]));
        Assert.Equal("Let the model stop container mysql_dev (mysql:8.4, Up 40 hours)?", DockerText.ConfirmQuestion(DockerText.LifecycleAct("stop", [Named("mysql_dev")], null)));
        Assert.Equal("stopped mysql_dev", DockerText.Done("stop", Named("mysql_dev"), already: false));
        Assert.Equal("3 days ago", DockerText.Age(1790700000, DateTimeOffset.FromUnixTimeSeconds(1790700000 + 3 * 86400 + 60)));
        Assert.Equal("Error: Docker writes is off, so the model may only look; the user can act with /docker, or switch Docker writes on in /tools", DockerText.WritesOff);
        Assert.Equal("Error: the user declined this Docker action; do not retry it unless they ask", DockerText.Declined);
        Assert.Equal("Error: this Docker action needs the user's yes and nobody could be asked; the user can run it with /docker", DockerText.NotAsked);
        Assert.Equal("<redacted>", DockerText.Redacted);
    }

    // ── the log ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheLog_IsDemultiplexed_ByStream_TheColoursAndCarriageReturnsGone()
    {
        byte[] data = [.. Frame(1, "out one\n"), .. Frame(2, "\u001b[31merr\u001b[0m one\n"), .. Frame(1, "out two\r\n")];
        Assert.Equal(["out one", "err one", "out two"], DockerLogStream.Lines(data, DockerLogStream.MultiplexedType, DockerLogStreams.Both));
        Assert.Equal(["out one", "out two"], DockerLogStream.Lines(data, DockerLogStream.MultiplexedType, DockerLogStreams.Stdout));
        Assert.Equal(["err one"], DockerLogStream.Lines(data, DockerLogStream.MultiplexedType, DockerLogStreams.Stderr));
        // An older engine labels both raw: the frame header gives it away.
        Assert.Equal(["out one", "err one", "out two"], DockerLogStream.Lines(data, "application/vnd.docker.raw-stream", DockerLogStreams.Both));
        // A TTY container's log is the bytes themselves.
        Assert.Equal(["plain", "text"], DockerLogStream.Lines(Encoding.UTF8.GetBytes("plain\ntext\n"), "application/vnd.docker.raw-stream", DockerLogStreams.Both));
        // A frame the cap cut keeps what arrived; a multi-byte character split over two frames is whole.
        byte[] euro = Encoding.UTF8.GetBytes("€");
        byte[] split = [.. FrameBytes(1, euro[..1]), .. FrameBytes(1, [.. euro[1..], (byte)'\n']), .. Frame(1, "cut short")[..12]];
        Assert.Equal(["€", "cut "], DockerLogStream.Lines(split, DockerLogStream.MultiplexedType, DockerLogStreams.Both));
        Assert.Equal(["a", "b"], DockerLogStream.SplitLines("a\r\nb"));
    }

    private static byte[] FrameBytes(byte stream, byte[] payload)
    {
        byte[] frame = new byte[8 + payload.Length];
        frame[0] = stream;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    [Fact]
    public async Task TheLogRead_StopsAtItsCap_OverATrickle()
    {
        byte[] body = Enumerable.Repeat((byte)'x', 300).ToArray();
        var (data, capped) = await DockerLogStream.ReadCappedAsync(new TrickleStream(body), 256, CancellationToken.None);
        Assert.Equal(256, data.Length);
        Assert.True(capped);
        var (all, more) = await DockerLogStream.ReadCappedAsync(new TrickleStream(body), 1000, CancellationToken.None);
        Assert.Equal(300, all.Length);
        Assert.False(more);
    }

    [Fact]
    public void TheLogAnswer_KeepsTheLastLines_TheGrepsMatches_AndFitsTheBudget()
    {
        var lines = Enumerable.Range(1, 10).Select(i => "line " + i).ToList();
        Assert.Equal("Docker logs: web · last 3 lines\nline 8\nline 9\nline 10", DockerLogsTool.Render("web", lines, 3, null, capped: false));
        Assert.Equal("Docker logs: web · 2 lines matching 'LINE 1'\nline 1\nline 10", DockerLogsTool.Render("web", lines, 5, "LINE 1", capped: false));
        Assert.Equal("Docker logs: web · no lines", DockerLogsTool.Render("web", [], 5, null, capped: false));
        Assert.Equal("Docker logs: web · 0 lines matching 'zzz'", DockerLogsTool.Render("web", lines, 5, "zzz", capped: false));
        var long_ = Enumerable.Range(1, 100).Select(i => new string('y', 400) + i).ToList();
        string fitted = DockerLogsTool.Render("web", long_, 100, null, capped: true);
        Assert.True(fitted.Length < DockerLogsTool.MaxChars + 400);
        Assert.Contains("earlier lines cut to fit", fitted);
        Assert.EndsWith(new string('y', 400) + "100", fitted);
        Assert.Contains("(the read stopped at 1 MB)", fitted);
    }

    [Theory]
    [InlineData("10m", 600)]
    [InlineData("2h", 7200)]
    [InlineData("1h30m", 5400)]
    [InlineData("1d", 86400)]
    [InlineData("1w", 604800)]
    [InlineData("90 s", 90)]
    public void Since_AnAge_IsSecondsBeforeNow(string text, long seconds)
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.ToUnixTimeSeconds() - seconds, DockerLogsTool.ParseSince(text, now));
    }

    [Fact]
    public void Since_AMoment_IsItsTime_UtcWithoutAnOffset_AndJunkIsNull()
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), DockerLogsTool.ParseSince("2026-10-02T14:00:00Z", now));
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), DockerLogsTool.ParseSince("2026-10-02 14:00", now));
        Assert.Null(DockerLogsTool.ParseSince("yesterday-ish", now));
    }

    // ── the stats ───────────────────────────────────────────────────────────

    [Fact]
    public void TheStats_AreDockerStatsArithmetic_UnderCgroupV2AndV1()
    {
        var v2 = DockerStatsMath.Parse(StatsJson)!;
        Assert.Equal(3_422_000.0 / 24_040_000_000.0 * 24 * 100, v2.CpuPercent, 6);
        Assert.Equal(483_823_616 - 5_931_008, v2.MemoryUsed);
        Assert.Equal(52_625_555_456, v2.MemoryLimit);
        Assert.Equal((215_242, 192_376, 140_169_216, 304_111_616, 50L), (v2.NetRx, v2.NetTx, v2.BlockRead, v2.BlockWrite, v2.Pids!.Value));
        Assert.Equal("mysql_dev · CPU 0.3% · memory 477.9 MB / 52.6 GB (0.9%) · net 215.2 KB in / 192.4 KB out · disk 140.2 MB read / 304.1 MB written · 50 pids", DockerText.StatsLine("mysql_dev", v2));

        var v1 = DockerStatsMath.Parse(Fixture("stats-v1.json"))!;
        Assert.Equal(20, v1.CpuPercent, 6);   // the CPUs counted from percpu_usage
        Assert.Equal(150_000_000, v1.MemoryUsed);   // total_inactive_file
        Assert.Equal((15L, 20L), (v1.BlockRead, v1.BlockWrite));
        Assert.Null(v1.Pids);
        Assert.Equal(0, DockerStatsMath.CpuPercent(5, 0, 4));
        Assert.Null(DockerStatsMath.Parse("{}"));
    }

    // ── the redaction ───────────────────────────────────────────────────────

    [Fact]
    public void TheRedaction_HidesEveryEnvValue_SecretFlags_SecretLabels_AndUrlPasswords()
    {
        Assert.Equal("MYSQL_ROOT_PASSWORD=<redacted>", DockerRedaction.Env("MYSQL_ROOT_PASSWORD=hunter2"));
        Assert.Equal("PATH=<redacted>", DockerRedaction.Env("PATH=/usr/bin"));
        Assert.Equal("NOVALUE", DockerRedaction.Env("NOVALUE"));
        Assert.Equal(["serve", "--token=<redacted>", "--api-key", "<redacted>", "--port", "8080", "-p", "--secret", "--verbose"], DockerRedaction.Args(["serve", "--token=abc", "--api-key", "abc", "--port", "8080", "-p", "--secret", "--verbose"]));
        Assert.Equal(["DB_PASSWORD=<redacted>", "NAME=x"], DockerRedaction.Args(["DB_PASSWORD=pw", "NAME=x"]));
        Assert.Equal("postgres://app:<redacted>@db:5432/x and http://plain.example.com", DockerRedaction.Url("postgres://app:pw@db:5432/x and http://plain.example.com"));
        Assert.Equal("<redacted>", DockerRedaction.Label("api.token", "abc"));
        Assert.Equal("neon", DockerRedaction.Label("maintainer", "neon"));
        Assert.Equal("mysqladmin ping --password=<redacted>", DockerRedaction.Line("mysqladmin ping --password=pw"));
        Assert.All(["password", "PWD", "client_secret", "auth_token", "privateKey", "cert_file", "credentials"], n => Assert.True(DockerRedaction.IsSecretName(n), n));
        Assert.All(["port", "name", "image"], n => Assert.False(DockerRedaction.IsSecretName(n), n));
    }

    [Fact]
    public void TheInspect_IsASummary_WithNoSecretLeft()
    {
        string summary = DockerInspect.Summary(InspectJson)!;
        Assert.DoesNotContain("Fixture", summary);   // every fake secret in the fixture carries the word
        Assert.DoesNotContain("splunk", summary, StringComparison.OrdinalIgnoreCase);   // the log driver's options
        Assert.DoesNotContain("overlay", summary);   // the storage driver's paths
        Assert.DoesNotContain("resolv.conf", summary);
        Assert.DoesNotContain("desktop.docker.io", summary);
        string[] lines = summary.Split('\n');
        Assert.Equal("mysql_dev · 2dc6d221ea8a · running (healthy)", lines[0]);
        Assert.Equal("Image: mysql:8.4 (6ea90827b110)", lines[1]);
        Assert.Equal("State: started 2026-09-30 22:52:06 UTC · restarts 2 · exit code 0", lines[2]);
        Assert.Equal("Health: healthy · failing streak 0 · last checks: 2026-10-02 16:40:30 UTC exit 0 \"mysqld is alive\"; 2026-10-02 16:41:00 UTC exit 0 \"mysqld is alive\"; 2026-10-02 16:41:30 UTC exit 0 \"mysqladmin --password=<redacted> ping ok\"", lines[3]);
        Assert.Contains("Entrypoint: docker-entrypoint.sh", lines);
        Assert.Contains("Command: mysqld --default-authentication-plugin=<redacted> --admin-password <redacted> --log-url=https://ops:<redacted>@logs.example.com/in", lines);   // "auth" sounds secret: a harmless value hidden too
        Assert.Contains("Health check: CMD-SHELL mysqladmin ping --password=<redacted>", lines);
        Assert.Contains("Working dir / · user mysql", lines);
        Assert.Contains("Env (values redacted): MYSQL_ROOT_PASSWORD=<redacted>, MYSQL_DATABASE=<redacted>, DATABASE_URL=<redacted>, PATH=<redacted>, GOSU_VERSION=<redacted>", lines);
        Assert.Contains("Ports: 3306/tcp → 0.0.0.0:3306, [::]:3306; 33060/tcp (not published)", lines);
        Assert.Contains("Mounts: volume 02c61c74061f → /var/lib/mysql (rw); bind /home/neon/initdb → /docker-entrypoint-initdb.d (ro)", lines);
        Assert.Contains("Networks: bridge 172.17.0.5", lines);
        Assert.Contains("Policy: restart unless-stopped · memory 2.1 GB · cpus 1.5", lines);
        Assert.Contains("Compose: project shop, service db, in /home/neon/shop", lines);
        Assert.Contains("Labels: api.token=<redacted>, maintainer=neon", lines);
        Assert.Null(DockerInspect.Summary("[]"));
    }

    // ── names and projects ──────────────────────────────────────────────────

    [Theory]
    [InlineData("mysql_dev", "mysql_dev")]
    [InlineData("/mysql_dev", "mysql_dev")]
    [InlineData("MYSQL_DEV", "mysql_dev")]
    [InlineData("2dc6", "mysql_dev")]
    [InlineData(MysqlId, "mysql_dev")]
    [InlineData("mysql", "mysql_dev")]
    [InlineData("valkey", "searxng-valkey")]
    [InlineData("c9e0", "mariadb_dev")]
    public void AName_ResolvesToOneContainer(string target, string name) =>
        Assert.Equal(name, Assert.Single(DockerTargets.Resolve(Containers(), target).Containers).Name);

    [Fact]
    public void AnAmbiguousOrMissingName_IsRefused_ListingWhatIsThere()
    {
        Assert.Equal("Error: 'searxng' could be 2 containers; name one: searxng-core, searxng-valkey", DockerTargets.Resolve(Containers(), "searxng").Error);
        Assert.Equal("Error: no container named 'nosuch'; there are: mysql_dev, mariadb_dev, searxng-core, searxng-valkey, homeassistant, open-webui", DockerTargets.Resolve(Containers(), "nosuch").Error);
        Assert.Equal("Error: no container named '2dc'; there are: mysql_dev, mariadb_dev, searxng-core, searxng-valkey, homeassistant, open-webui", DockerTargets.Resolve(Containers(), "2dc").Error);   // three hex characters is no id
        Assert.Equal("Error: no container named 'x'; there is no container at all", DockerTargets.Resolve([], "x").Error);
        Assert.Equal(DockerText.Missing("container"), DockerTargets.Resolve(Containers(), " / ").Error);
        Assert.Equal(["searxng-core", "searxng-valkey"], DockerTargets.Project(Containers(), "SEARXNG").Containers.Select(c => c.Name));
        Assert.Equal("Error: no compose project named 'web'; there are: home-assistant, searxng", DockerTargets.Project(Containers(), "web").Error);
    }

    [Fact]
    public void TheComposeProjects_AreTheLabels_InComposesOrder()
    {
        var projects = DockerCompose.Group(Containers());
        Assert.Equal(["home-assistant", "searxng"], projects.Select(p => p.Name));
        var searxng = projects[1];
        Assert.Equal(["valkey"], DockerCompose.DependsOn(searxng.Containers[0]));
        Assert.Equal(["searxng-valkey", "searxng-core"], DockerCompose.StartOrder(searxng.Containers).Select(c => c.Name));
        Assert.Equal(["searxng-core", "searxng-valkey"], DockerCompose.StopOrder(searxng.Containers).Select(c => c.Name));
        Assert.Equal(
            [
                "searxng · 2 of 2 running · in /home/neon/searxng · /home/neon/searxng/docker-compose.yml",
                "  core · searxng-core · running (unhealthy) · docker.io/searxng/searxng:latest · 127.0.0.1:8888→8080/tcp",
                "  valkey · searxng-valkey · running · docker.io/valkey/valkey:9-alpine · 6379/tcp",
            ],
            DockerText.ProjectLines(searxng));
        // A cycle never stalls the order.
        var a = Named("searxng-core") with { Labels = new Dictionary<string, string> { [DockerContainer.ServiceLabel] = "a", [DockerContainer.DependsOnLabel] = "b:service_started:false" } };
        var b = Named("searxng-valkey") with { Labels = new Dictionary<string, string> { [DockerContainer.ServiceLabel] = "b", [DockerContainer.DependsOnLabel] = "a:service_started:false" } };
        Assert.Equal(2, DockerCompose.StartOrder([a, b]).Count);
    }

    [Fact]
    public void ThePulls_TheDiskUse_AndThePrunes_AreRead()
    {
        Assert.Equal(("Status: Downloaded newer image for nginx:latest", (string?)null), DockerJson.PullResult(Fixture("pull.jsonl")));
        var (_, denied) = DockerJson.PullResult(Fixture("pull-denied.jsonl"));
        Assert.StartsWith("pull access denied for nosuchimage", denied);
        Assert.Equal((3, 1_200_000L), DockerJson.PruneResult("""{"VolumesDeleted":["a","b"],"ContainersDeleted":["c"],"SpaceReclaimed":1200000}"""));
        var usage = DockerJson.DiskUsage(Fixture("system-df.json"))!;
        Assert.Equal(
            [
                "Docker disk use: 6.2 GB in all, 1.5 GB reclaimable",
                "images · 3 (1 in use) · 5 GB · 400 MB reclaimable",
                "containers · 2 (1 in use) · 5 MB · 5 MB reclaimable",
                "volumes · 3 (1 in use) · 702 MB · 700 MB reclaimable",
                "build cache · 2 (1 in use) · 500 MB · 400 MB reclaimable",
            ],
            DockerText.DiskLines(usage));
        Assert.Equal(("nginx", "latest"), DockerPullTool.Split("nginx", null));
        Assert.Equal(("postgres", "16"), DockerPullTool.Split("postgres:16", null));
        Assert.Equal(("postgres", "17"), DockerPullTool.Split("postgres:16", "17"));
        Assert.Equal(("localhost:5000/app", "latest"), DockerPullTool.Split("localhost:5000/app", ""));
        Assert.Equal(("nginx@sha256:abc", ""), DockerPullTool.Split("nginx@sha256:abc", null));
        Assert.Equal("nginx@sha256:abc", DockerPullTool.Reference("nginx@sha256:abc", ""));
        Assert.Equal("/volumes/prune?filters=%7B%22all%22%3A%5B%22true%22%5D%7D", DockerPruneTool.PrunePath("volumes", all: true));
        Assert.Equal("/images/prune?filters=%7B%22dangling%22%3A%5B%22false%22%5D%7D", DockerPruneTool.PrunePath("images", all: true));
        Assert.Equal("/build/prune", DockerPruneTool.PrunePath("build_cache", all: false));
    }

    // ── the read tools ──────────────────────────────────────────────────────

    [Fact]
    public void TheTools_AreTen_InOrder_TheirSchemasParse_AndPlanModeKeepsTheReads()
    {
        var tools = Tools();
        Assert.Equal(DockerToolNames.All, tools.Select(t => t.Name));
        Assert.Equal(DockerToolNames.Writes.Order(StringComparer.Ordinal), ChatScreen.DockerWriteToolNames.Order(StringComparer.Ordinal));
        Assert.Equal(DockerToolNames.All.Order(StringComparer.Ordinal), ChatScreen.DockerToolNames.Order(StringComparer.Ordinal));
        Assert.All(tools, t => Assert.Equal(System.Text.Json.JsonValueKind.Object, t.JsonSchema.ValueKind));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.All(DockerToolNames.Reads, name => Assert.True(PlanTools.Allowed(name), name));
        Assert.All(DockerToolNames.Writes, name => Assert.Contains(name, PlanTools.Mutating));
        Assert.Equal(DockerToolNames.Reads, ChatScreen.DockerToolsFor(tools, new AppSettingsData()).Select(t => t.Name));
        Assert.Equal(DockerToolNames.All, ChatScreen.DockerToolsFor(tools, new AppSettingsData { DockerWrites = true }).Select(t => t.Name));
        Assert.Contains(DockerRemoveTool.ToolName, new AppSettingsData().ToolsDisabled);
        Assert.Contains(DockerPruneTool.ToolName, new AppSettingsData().ToolsDisabled);
        Assert.DoesNotContain(DockerLifecycleTool.ToolName, new AppSettingsData().ToolsDisabled);
        Assert.True(ChatScreen.DockerOffered(new AppSettingsData { DockerTools = true }) == OperatingSystem.IsWindows());
        Assert.False(ChatScreen.DockerOffered(new AppSettingsData()));
    }

    [Fact]
    public async Task Containers_ListsThemAll_RunningFirst_NarrowedByFilterAndProject()
    {
        string all = await Invoke<DockerContainersTool>();
        Assert.StartsWith("Docker: 6 containers (4 running, 1 exited, 1 paused)\nmariadb_dev · running (healthy)", all);
        Assert.EndsWith("open-webui · exited · Exited (255) 3 weeks ago · ghcr.io/open-webui/open-webui:latest · 19b25012a85e", all);
        Assert.Equal("Docker: 2 containers matching running, project searxng (2 running)\n" + DockerText.ContainerLine(Named("searxng-core")) + "\n" + DockerText.ContainerLine(Named("searxng-valkey")), await Invoke<DockerContainersTool>(("all", false), ("project", "searxng")));
        Assert.StartsWith("Docker: 1 container matching 'MARIA' (1 running)", await Invoke<DockerContainersTool>(("filter", "MARIA")));
        Assert.Equal("Error: 'maybe' is not true or false for 'all'", await Invoke<DockerContainersTool>(("all", "maybe")));
    }

    [Fact]
    public async Task Logs_ResolvesTheName_AsksTheEngine_AndReadsTheFrames()
    {
        string result = await Invoke<DockerLogsTool>(("container", "mysql"), ("tail", 20), ("since", "10m"), ("timestamps", true));
        Assert.Equal("Docker logs: mysql_dev · last 3 lines\nready\nwarning: low disk\nquery ok", result);
        var asked = _stub.Requests.Last();
        Assert.Equal("/v1.47/containers/" + MysqlId + "/logs", asked.Uri.AbsolutePath);
        Assert.Equal("?stdout=1&stderr=1&tail=20&since=" + (_time.UtcNow.ToUnixTimeSeconds() - 600).ToString(System.Globalization.CultureInfo.InvariantCulture) + "&timestamps=1", asked.Uri.Query);

        Assert.Equal("Docker logs: mysql_dev · 1 line matching 'disk'\nwarning: low disk", await Invoke<DockerLogsTool>(("container", "mysql_dev"), ("grep", "disk")));
        Assert.Contains("tail=" + DockerLogsTool.GrepWindow.ToString(System.Globalization.CultureInfo.InvariantCulture), _stub.Requests.Last().Uri.Query);
        _ = await Invoke<DockerLogsTool>(("container", "mysql_dev"), ("stream", "stderr"));
        Assert.StartsWith("?stdout=0&stderr=1&tail=100", _stub.Requests.Last().Uri.Query);

        Assert.Equal("Error: give \"container\"", await Invoke<DockerLogsTool>());
        Assert.Equal("Error: '0' is not a whole number from 1 to 2000 for 'tail'", await Invoke<DockerLogsTool>(("container", "mysql"), ("tail", 0)));
        Assert.Equal(DockerText.BadSince("soon"), await Invoke<DockerLogsTool>(("container", "mysql"), ("since", "soon")));
        Assert.Equal("Error: 'both ways' is not a valid stream; use one of: both, stdout, stderr", await Invoke<DockerLogsTool>(("container", "mysql"), ("stream", "both ways")));
        Assert.StartsWith("Error: 'searxng' could be 2 containers", await Invoke<DockerLogsTool>(("container", "searxng")));
    }

    [Fact]
    public async Task Inspect_IsTheRedactedSummary()
    {
        string result = await Invoke<DockerInspectTool>(("container", "2dc6"));
        Assert.StartsWith("Docker inspect: mysql_dev · 2dc6d221ea8a · running (healthy)\n", result);
        Assert.DoesNotContain("Fixture", result);
        Assert.Equal("Docker inspect: mysql_dev · 2dc6d221ea8a · running (healthy)", DockerText.Note(result));
    }

    [Fact]
    public async Task Stats_SamplesOne_OrEveryRunningOne_AndSaysWhenOneIsNotRunning()
    {
        Assert.Equal("Docker stats: 1 running container\n" + DockerText.StatsLine("mysql_dev", DockerStatsMath.Parse(StatsJson)!), await Invoke<DockerStatsTool>(("container", "mysql_dev")));
        Assert.Equal("Docker stats: open-webui is exited, so it uses nothing", await Invoke<DockerStatsTool>(("container", "open-webui")));
        string all = await Invoke<DockerStatsTool>();
        string[] lines = all.Split('\n');
        Assert.Equal("Docker stats: 4 running containers", lines[0]);
        Assert.Equal(5, lines.Length);   // the paused one is not sampled
        Assert.StartsWith("mariadb_dev · Error: cannot reach the Docker engine", lines[1]);   // no stats route for it here: its own line says so, the others stand
        Assert.StartsWith("mysql_dev · CPU 0.3%", lines[2]);
    }

    [Fact]
    public async Task Resources_ListImagesVolumesNetworks_WithTheirUsers_AndTheDiskUse()
    {
        string images = await Invoke<DockerResourcesTool>(("kind", "images"));
        Assert.StartsWith("Docker images: 4 (2 unused)\nmysql:8.4 · 6ea90827b110 · 1.1 GB · 2 days ago · used by mysql_dev\nghcr.io/home-assistant/home-assistant:stable · 3e6710a7ab2a · 3.4 GB · ", images);
        Assert.Contains("\nnginx:1.27, nginx:latest · 111122223333 · 200 MB · ", images);
        Assert.EndsWith(" · unused", images);
        Assert.Contains("<none> (dangling) · deadbeefcafe · 52.4 MB · ", images);
        Assert.Equal("Docker images: 2 matching unused (2 unused)", (await Invoke<DockerResourcesTool>(("kind", "images"), ("unused", true))).Split('\n')[0]);

        Assert.Equal(
            "Docker volumes: 4 (1 unused)\nkokoro-models · local · unused\nopen-webui · local · used by open-webui\nsearxng_valkey-data · local · used by searxng-valkey\n02c61c74061f (anonymous) · local · used by mysql_dev",
            await Invoke<DockerResourcesTool>(("kind", "volumes")));
        Assert.Equal(
            "Docker networks: 5 (1 unused)\nbridge · bridge · local · 172.17.0.0/16 · mysql_dev, mariadb_dev, open-webui\nhost · host · local · homeassistant\nnone · null · local · no containers\nold_project_default · bridge · local · 172.20.0.0/16 · internal · no containers\nsearxng_default · bridge · local · 172.18.0.0/16 · searxng-core, searxng-valkey",
            await Invoke<DockerResourcesTool>(("kind", "networks")));
        Assert.StartsWith("Docker disk use: 6.2 GB in all, 1.5 GB reclaimable\n", await Invoke<DockerResourcesTool>(("kind", "disk")));
        Assert.Equal("Error: 'pods' is not a valid kind; use one of: images, volumes, networks, disk", await Invoke<DockerResourcesTool>(("kind", "pods")));
        Assert.Equal("Error: give \"kind\"", await Invoke<DockerResourcesTool>());
    }

    [Fact]
    public async Task Compose_ListsTheProjects_OrOne()
    {
        string all = await Invoke<DockerComposeTool>();
        Assert.StartsWith("Docker compose: 2 projects\nhome-assistant · 0 of 1 running\n  homeassistant · homeassistant · paused", all);
        Assert.StartsWith("Docker compose: 1 project\nsearxng · 2 of 2 running", await Invoke<DockerComposeTool>(("project", "searxng")));
        Assert.Equal("Error: no compose project named 'web'; there are: home-assistant, searxng", await Invoke<DockerComposeTool>(("project", "web")));
    }

    // ── the write gate ──────────────────────────────────────────────────────

    [Fact]
    public async Task Lifecycle_WithWritesOff_IsRefused_BeforeAnythingIsRead_OrAsked()
    {
        Assert.Equal(DockerText.WritesOff, await Invoke<DockerLifecycleTool>(("target", "mysql_dev"), ("action", "stop")));
        Assert.Empty(_asked);
        Assert.Empty(_stub.Requests);
    }

    [Fact]
    public async Task Lifecycle_TheUsersNo_OrNobodyToAsk_ActsOnNothing()
    {
        _settings.DockerWrites = true;
        Assert.Equal(DockerText.Declined, await Invoke<DockerLifecycleTool>(false, ("target", "mysql_dev"), ("action", "stop")));
        Assert.Equal(["Let the model stop container mysql_dev (mysql:8.4, Up 40 hours)?"], _asked);
        Assert.Equal(DockerText.NotAsked, await Invoke<DockerLifecycleTool>(null, ("target", "mysql_dev"), ("action", "stop")));
        var headless = ChatScreen.DockerTools(_docker, confirm: null).OfType<DockerLifecycleTool>().Single();
        Assert.Equal(DockerText.NotAsked, (string)(await headless.InvokeAsync(Args(("target", "mysql_dev"), ("action", "restart"))))!);
        Assert.Empty(Posts());
    }

    [Fact]
    public async Task Lifecycle_TheUsersYes_Acts_AndAudits()
    {
        _settings.DockerWrites = true;
        On("/v1.47/containers/" + MysqlId + "/stop", HttpStatusCode.NoContent, "");
        var (result, audit) = await CaptureAuditAsync(() => Invoke<DockerLifecycleTool>(("target", "mysql"), ("action", "STOP"), ("timeout_seconds", 30)));
        Assert.Equal("stopped mysql_dev", result);
        var post = Assert.Single(Posts());
        Assert.Equal("/v1.47/containers/" + MysqlId + "/stop?t=30", post.Uri.PathAndQuery);
        Assert.Equal(["model: stop mysql_dev — done"], audit);
        Assert.Null(_docker.Last);   // the list is read afresh after a change
    }

    [Fact]
    public async Task Lifecycle_OnAProject_ActsInComposesOrder_OneQuestionForAll()
    {
        _settings.DockerWrites = true;
        On("/v1.47/containers/" + CoreId + "/restart", HttpStatusCode.NoContent, "");
        On("/v1.47/containers/" + ValkeyId + "/restart", HttpStatusCode.InternalServerError, """{"message":"cannot restart: device busy"}""");
        string result = await Invoke<DockerLifecycleTool>(("target", "searxng"), ("action", "restart"), ("scope", "project"));
        Assert.Equal(["Let the model restart compose project searxng: searxng-valkey, searxng-core?"], _asked);
        Assert.Equal(["/v1.47/containers/" + ValkeyId + "/restart?t=10", "/v1.47/containers/" + CoreId + "/restart?t=10"], Posts().Select(p => p.Uri.PathAndQuery));
        Assert.Equal("Docker: restart · 1 of 2 containers done\n  Error: the Docker engine answered 500 to /containers/" + ValkeyId + "/restart: cannot restart: device busy\n  restarted searxng-core", result);
        Assert.Equal(["searxng-core", "searxng-valkey"], DockerLifecycleTool.Order("stop", DockerTargets.Project(Containers(), "searxng").Containers).Select(c => c.Name));
    }

    [Fact]
    public async Task Lifecycle_RefusesABadActionScopeOrTimeout_AndAnUnknownTarget()
    {
        _settings.DockerWrites = true;
        Assert.Equal("Error: 'kill' is not a valid action; use one of: start, stop, restart, pause, unpause", await Invoke<DockerLifecycleTool>(("target", "mysql"), ("action", "kill")));
        Assert.Equal("Error: 'swarm' is not a valid scope; use one of: container, project", await Invoke<DockerLifecycleTool>(("target", "mysql"), ("action", "stop"), ("scope", "swarm")));
        Assert.Equal("Error: '500' is not a whole number from 0 to 120 for 'timeout_seconds'", await Invoke<DockerLifecycleTool>(("target", "mysql"), ("action", "stop"), ("timeout_seconds", 500)));
        Assert.StartsWith("Error: no container named 'ghost'", await Invoke<DockerLifecycleTool>(("target", "ghost"), ("action", "start")));
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Pull_AsksWithTheReference_ThenReadsTheStream()
    {
        _settings.DockerWrites = true;
        On("/v1.47/images/create", request => StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixture(request.RequestUri!.Query.Contains("nosuchimage", StringComparison.Ordinal) ? "pull-denied.jsonl" : "pull.jsonl")));
        Assert.Equal("Docker: pulled nginx:1.27 · Status: Downloaded newer image for nginx:latest", await Invoke<DockerPullTool>(("image", "nginx:1.27")));
        Assert.Equal("Let the model pull image nginx:1.27?", _asked[0]);
        Assert.StartsWith("Error: the pull of nosuchimage:latest failed: pull access denied for nosuchimage", await Invoke<DockerPullTool>(("image", "nosuchimage")));
        Assert.Equal(DockerText.Declined, await Invoke<DockerPullTool>(false, ("image", "redis")));
        Assert.Equal(2, Posts().Count);
    }

    [Fact]
    public async Task Remove_AContainer_ByItsId_AVolume_ByName_EachAsked()
    {
        _settings.DockerWrites = true;
        On("/v1.47/containers/19b25012a85e7f6e5d4c3b2a1908f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5", HttpStatusCode.NoContent, "");
        On("/v1.47/volumes/kokoro-models", HttpStatusCode.NoContent, "");
        On("/v1.47/images/busy", HttpStatusCode.Conflict, """{"message":"conflict: unable to remove repository reference \"busy\" (must force)"}""");
        Assert.Equal("Docker: removed container open-webui", await Invoke<DockerRemoveTool>(("kind", "container"), ("name", "open-webui")));
        Assert.Equal("Let the model remove container open-webui (ghcr.io/open-webui/open-webui:latest, Exited (255) 3 weeks ago)?", _asked[0]);
        Assert.Equal("Docker: removed volume kokoro-models", await Invoke<DockerRemoveTool>(("kind", "volume"), ("name", "kokoro-models"), ("force", true)));
        Assert.Equal("Let the model remove volume kokoro-models, forced?", _asked[1]);
        Assert.Equal(["/v1.47/containers/19b25012a85e7f6e5d4c3b2a1908f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5?v=0&force=0", "/v1.47/volumes/kokoro-models?force=1"], Posts().Select(p => p.Uri.PathAndQuery));
        Assert.Equal("Error: the Docker engine answered 409 to /images/busy: conflict: unable to remove repository reference \"busy\" (must force)", await Invoke<DockerRemoveTool>(("kind", "image"), ("name", "busy")));
        Assert.Equal("Error: 'network' is not a valid kind; use one of: container, image, volume", await Invoke<DockerRemoveTool>(("kind", "network"), ("name", "x")));
    }

    [Fact]
    public async Task Prune_SaysWhatItWouldTake_Asks_ThenSaysWhatItFreed()
    {
        _settings.DockerWrites = true;
        On("/v1.47/volumes/prune", HttpStatusCode.OK, """{"VolumesDeleted":["kokoro-models","open-webui"],"SpaceReclaimed":700000000}""");
        On("/v1.47/containers/prune", HttpStatusCode.OK, """{"ContainersDeleted":["19b2"],"SpaceReclaimed":5000000}""");
        Assert.Equal("Docker: pruned volumes · 2 removed · 700 MB reclaimed", await Invoke<DockerPruneTool>(("kind", "volumes"), ("all", true)));
        Assert.Equal("Let the model prune volumes (named ones too): 2 unused volumes, up to 700 MB?", _asked[0]);
        Assert.Equal("Docker: pruned containers · 1 removed · 5 MB reclaimed", await Invoke<DockerPruneTool>(("kind", "containers")));
        Assert.Equal("Let the model prune containers: 1 stopped container?", _asked[1]);
        Assert.Equal(DockerText.Declined, await Invoke<DockerPruneTool>(false, ("kind", "build cache")));
        Assert.Equal("Let the model prune build cache: about 400 MB of build cache?", _asked[2]);
        Assert.Equal(2, Posts().Count);
    }

    // ── /docker ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCommand_ListsShowsStatusLogsStats_AndActsAsTheUser()
    {
        var ps = await DockerCommand.RunAsync(_docker, "", CancellationToken.None);
        Assert.False(ps.Failed);
        Assert.Equal("Docker: 6 containers (4 running, 1 exited, 1 paused)", ps.Lines[0]);
        Assert.Equal(7, ps.Lines.Count);

        var status = await DockerCommand.RunAsync(_docker, "status", CancellationToken.None);
        Assert.Equal(
            [
                @"Docker Desktop 4.90.0 (238679) · engine 29.7.2 · API 1.55 (asking v1.47) · linux/amd64 · \\.\pipe\docker_engine",
                "Containers: 4 running, 1 paused, 1 stopped · 4 images",
                "Engine machine: 24 CPUs · 52.6 GB memory · Docker Desktop",
            ],
            status.Lines);

        var logs = await DockerCommand.RunAsync(_docker, "logs mysql 2", CancellationToken.None);
        Assert.Equal(["Docker logs: mysql_dev · last 2 lines", "warning: low disk", "query ok"], logs.Lines);
        Assert.Contains("tail=2", _stub.Requests.Last().Uri.Query);

        var stats = await DockerCommand.RunAsync(_docker, "stats mysql_dev", CancellationToken.None);
        Assert.Equal("Docker stats: 1 running container", stats.Lines[0]);

        // The user's own hand: Docker writes off, nobody asked, the act audited as the user's.
        On("/v1.47/containers/" + MysqlId + "/restart", HttpStatusCode.NoContent, "");
        var (restart, audit) = await CaptureAuditAsync(() => DockerCommand.RunAsync(_docker, "restart mysql_dev", CancellationToken.None));
        Assert.Equal(["restarted mysql_dev"], restart.Lines);
        Assert.Equal(["user: restart mysql_dev — done"], audit);
        Assert.Empty(_asked);

        Assert.Equal([DockerText.Usage], (await DockerCommand.RunAsync(_docker, "frob", CancellationToken.None)).Lines);
        Assert.True((await DockerCommand.RunAsync(_docker, "stop", CancellationToken.None)).Failed);
        Assert.True((await DockerCommand.RunAsync(_docker, "logs mysql 0", CancellationToken.None)).Failed);
        Assert.StartsWith("Error: no container named 'ghost'", (await DockerCommand.RunAsync(_docker, "start ghost", CancellationToken.None)).Lines[0]);
    }

    [Fact]
    public void TheCommandsList_IsTheVerbs_ThenTheNamesThatFit()
    {
        Assert.Equal(DockerText.Verbs, DockerCommand.Complete(null, "").Select(i => i.Text));
        Assert.Equal(["status", "stats", "start", "stop"], DockerCommand.Complete(null, "st").Select(i => i.Text));
        Assert.Empty(DockerCommand.Complete(null, "stop "));
        var last = Containers();
        Assert.Equal(["stop mariadb_dev", "stop mysql_dev", "stop searxng-core", "stop searxng-valkey"], DockerCommand.Complete(last, "stop ").Select(i => i.Text));
        Assert.Equal(["start open-webui"], DockerCommand.Complete(last, "start ").Select(i => i.Text));
        Assert.Equal(["unpause homeassistant"], DockerCommand.Complete(last, "unpause ").Select(i => i.Text));
        Assert.Equal(["logs mysql_dev"], DockerCommand.Complete(last, "logs my").Select(i => i.Text));
        Assert.Equal("running · mysql:8.4", DockerCommand.Complete(last, "logs my")[0].Note);
        Assert.Empty(DockerCommand.Complete(last, "logs mysql_dev 5"));
        Assert.Equal(("mysql_dev", (int?)200), DockerCommand.SplitCount("mysql_dev 200"));
        Assert.Equal(("my sql", (int?)null), DockerCommand.SplitCount("my sql"));
    }

    [Fact]
    public void TheSlashCommand_IsRegistered_TakesAnArgument_AndRunsUnderAReply()
    {
        Assert.Equal((SlashCommand.Docker, "ps"), SlashCommands.Parse("/docker ps"));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.Docker));
        Assert.Contains("/docker", SlashCommands.Words);
        Assert.Contains(SlashCommands.HelpEntries, e => e.Command == "/docker");
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Docker, hasArgs: false));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Docker, hasArgs: true));
    }

    [Fact]
    public void ThePane_OffersWhatFitsTheState()
    {
        Assert.Equal(["stop", "restart", "pause", "logs", "open http://localhost:3306", "copy id"], DockerMenu.RowWords(Named("mysql_dev")));
        Assert.Equal(["unpause", "logs", "copy id"], DockerMenu.RowWords(Named("homeassistant")));
        Assert.Equal(["start", "logs", "copy id"], DockerMenu.RowWords(Named("open-webui")));
        Assert.Equal(["http://127.0.0.1:8888"], DockerMenu.Urls(Named("searxng-core")));
        Assert.Empty(DockerMenu.Urls(Named("searxng-valkey")));
        Assert.Equal(["https://localhost:8443"], DockerMenu.Urls(Named("mysql_dev") with { Ports = [new DockerPort("0.0.0.0", 443, 8443, "tcp")] }));
        Assert.Equal("🐳 Docker › mysql_dev", DockerMenu.RowTitle(Named("mysql_dev")));
        Assert.Equal("🐳 Stop mysql_dev (mysql:8.4, Up 40 hours)?", DockerMenu.ConfirmPrompt("stop", Named("mysql_dev")));
        Assert.Equal(["●", "◐", "○"], new[] { "mysql_dev", "homeassistant", "open-webui" }.Select(n => DockerMenu.Glyph(Named(n))));
        Assert.True(DockerMenu.AskFirst.SetEquals(["stop", "restart", "pause"]));
    }

    // ── the turn ────────────────────────────────────────────────────────────

    [Fact]
    public void TheRules_NameTheTools_AndRideWithTheGroup_TheWriteSentenceOnlyWithAChange()
    {
        Assert.All(DockerToolNames.Reads, name => Assert.Contains(name, Assistant.DockerRule));
        Assert.Contains("never repeat one", Assistant.DockerRule);
        Assert.Contains(DockerLifecycleTool.ToolName, Assistant.DockerWriteRule);
        Assert.DoesNotContain(Assistant.DockerRule, Assistant.DefaultRules(markdown: false, tools: true, homeAssistant: true));
        string reads = Assistant.DefaultRules(markdown: false, tools: true, homeAssistant: true, docker: true, advisor: true);
        Assert.True(reads.IndexOf(Assistant.HomeAssistantRule, StringComparison.Ordinal) < reads.IndexOf(Assistant.DockerRule, StringComparison.Ordinal));
        Assert.True(reads.IndexOf(Assistant.DockerRule, StringComparison.Ordinal) < reads.IndexOf(Assistant.ClaudeAdvisorRule, StringComparison.Ordinal));
        Assert.DoesNotContain(Assistant.DockerWriteRule, reads);
        Assert.Contains(Assistant.DockerRule + " " + Assistant.DockerWriteRule, Assistant.DefaultRules(markdown: false, tools: true, docker: true, dockerWrite: true));
        Assert.DoesNotContain(Assistant.DockerWriteRule, Assistant.DefaultRules(markdown: false, tools: true, dockerWrite: true));   // no group, no sentence

        var facts = new SystemPromptFacts(null, null, null, false, [], false, false, DockerEnabled: true, DockerTools: 6);
        Assert.True(facts.Docker);
        Assert.Contains(Assistant.DockerRule, SystemPromptSummary.SystemPrompt(facts));
        Assert.DoesNotContain(Assistant.DockerWriteRule, SystemPromptSummary.SystemPrompt(facts));
        Assert.Contains(Assistant.DockerWriteRule, SystemPromptSummary.SystemPrompt(facts with { DockerWrite = true }));
        Assert.False((facts with { ToolsEnabled = false }).Docker);
        var group = Assert.Single(SystemPromptSummary.ToolGroups([], [], [], [], false, docker: Tools(), dockerEnabled: false), g => g.Label == ToolsText.DockerTabTitle);
        Assert.Contains(SystemPromptSummary.DockerOffSuffix, group.Note);
        Assert.Equal(SettingsField.DockerTools, group.Switch);
    }

    [Fact]
    public void PrepareTurn_OffersTheGroup_TheChangesOnlyUnderWrites_AndPlanModeDropsThem()
    {
        var assistant = new Assistant(new FakeChatClient(), new ConversationHistory(""), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        var memory = new MemoryStore(_dir);
        string home = Path.Combine(_dir, "profile");
        var tools = Tools();
        void Prepare(AppSettingsData e, PlanTurn? plan = null) => ChatScreen.PrepareTurn(assistant, memory, [], [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: false, speechOutput: false, plan: plan, dockerTools: ChatScreen.DockerToolsFor(tools, e), dockerEnabled: ChatScreen.DockerOffered(e));
        IEnumerable<string> Offered() => assistant.Tools.Select(t => t.Name).Where(n => n.StartsWith("docker_", StringComparison.Ordinal));

        Prepare(new AppSettingsData { DockerTools = true });
        Assert.Equal(DockerToolNames.Reads, Offered());
        Assert.Contains(Assistant.DockerRule, assistant.History.SystemPrompt);
        Assert.DoesNotContain(Assistant.DockerWriteRule, assistant.History.SystemPrompt);

        Prepare(new AppSettingsData { DockerTools = true, DockerWrites = true });
        Assert.Equal(DockerToolNames.All, Offered());
        Assert.Contains(Assistant.DockerRule + " " + Assistant.DockerWriteRule, assistant.History.SystemPrompt);

        Prepare(new AppSettingsData { DockerTools = false, DockerWrites = true });
        Assert.Empty(Offered());
        Assert.DoesNotContain(Assistant.DockerRule, assistant.History.SystemPrompt);
    }

    [Fact]
    public void TheSettings_AreOffByDefault_TheirRowsSit_OnTheDockerTab()
    {
        var fresh = new AppSettingsData();
        Assert.False(fresh.DockerTools);
        Assert.False(fresh.DockerWrites);
        Assert.Equal("docker_engine", fresh.DockerEnginePipe);
        int tab = ToolsText.TabTitles.ToList().IndexOf(ToolsText.DockerTabTitle) - 1;
        Assert.Equal([SettingsField.DockerTools, SettingsField.DockerWrites, SettingsField.DockerEnginePipe], SettingsMenu.ToolsTabFields[tab]);
        Assert.Equal(ToolsText.TabTitles.ToList().IndexOf(ToolsText.ClaudeCliTabTitle) + 1, tab + 1);   // after Claude since 2026-10-03, the user's order (after UNC before)
        Assert.Equal(["Docker tools", "Docker writes", "Docker engine pipe"], SettingsMenu.ToolsTabFields[tab].Select(SettingsMenu.FieldName));
        Assert.Equal(@"\\.\pipe\docker_engine", SettingsMenu.FieldValue(SettingsField.DockerEnginePipe, fresh, _dir));
        Assert.True(SettingsMenu.IsToggle(SettingsField.DockerWrites));
        Assert.False(SettingsMenu.IsToggle(SettingsField.DockerEnginePipe));
        var copy = AppSettings.Copy(new AppSettingsData { DockerTools = true, DockerWrites = true, DockerEnginePipe = "x" });   // the snapshot copy carries all three
        Assert.Equal((true, true, "x"), (copy.DockerTools, copy.DockerWrites, copy.DockerEnginePipe));
    }

    // ── the transport ───────────────────────────────────────────────────────

    [Fact]
    public void TheSmokeProbe_RoundTripsOverARealNamedPipe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var check = SmokeChecks.ProbeDocker();
        Assert.True(check.Passed, check.Detail);
        Assert.Equal("docker:pipe-transport", check.Name);
    }

    [Fact]
    public async Task TheSession_OverARealNamedPipe_IsTheClientsOwnTransport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string pipe = "neonsidekick-test-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = SmokeChecks.ServeDockerAsync(pipe, path => path.StartsWith("/version", StringComparison.Ordinal)
            ? ("application/json", Encoding.UTF8.GetBytes(VersionJson))
            : ("application/json", Encoding.UTF8.GetBytes(ContainersJson)), connections: 2, stop.Token);
        using var session = new DockerSession(() => new AppSettingsData { DockerEnginePipe = @"\\.\pipe\" + pipe });
        var (containers, error) = await session.ContainersAsync(stop.Token);
        await server;
        Assert.Null(error);
        Assert.Equal(6, containers!.Count);
        Assert.Equal(pipe, session.Client().Pipe);
    }
}
