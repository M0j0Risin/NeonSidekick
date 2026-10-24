using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeonSidekick.App;
using NeonSidekick.Docker;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The Docker log window's pure parts and its client calls (2026-10-23): the engine's timestamps, the streaming decoder (frames,
/// characters and lines cut across chunks, stamps stripped per frame or per line), the event and inspect reads, the follow and
/// events calls over the stub engine, <c>/docker logs</c>' words, the session's <see cref="DockerSession.TryClient"/> and the
/// pane's logs row.
/// </summary>
public sealed partial class DockerTests
{
    private static List<DockerLogLine> Decode(string? contentType, params byte[][] chunks)
    {
        var decoder = new DockerLogDecoder(contentType);
        var lines = new List<DockerLogLine>();
        foreach (byte[] chunk in chunks)
        {
            decoder.Feed(chunk, lines);
        }

        decoder.Flush(lines);
        return lines;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // ── timestamps ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-10-23T10:00:01Z", 1792749601L, 0)]
    [InlineData("2026-10-23T10:00:01.5Z", 1792749601L, 500000000)]
    [InlineData("2026-10-23T10:00:01.123456789Z", 1792749601L, 123456789)]
    [InlineData("2026-10-23T10:00:01.1234567891Z", 1792749601L, 123456789)]   // more than nine digits are cut
    [InlineData("2026-10-23T12:00:01.25+02:00", 1792749601L, 250000000)]   // an offset folded to UTC
    public void Timestamps_AreRead(string text, long seconds, int nanos)
    {
        Assert.True(DockerTimestamp.TryParse(text, out var stamp));
        Assert.Equal(new DockerStamp(seconds, nanos), stamp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ready")]
    [InlineData("2026-10-23T10:00:01")]   // no zone
    [InlineData("2026-10-23T10:00:01.Z")]   // a point and no digits
    [InlineData("2026-13-23T10:00:01Z")]
    public void Timestamps_ThatAreNot_AreRefused(string text) => Assert.False(DockerTimestamp.TryParse(text, out _));

    [Fact]
    public void Stamps_AreOrdered_AndSaidAsTheEnginesSince()
    {
        Assert.True(new DockerStamp(5, 2) > new DockerStamp(5, 1));
        Assert.True(new DockerStamp(4, 999) < new DockerStamp(5, 0));
        Assert.Equal("1792749601.000000042", new DockerStamp(1792749601, 42).Since);
    }

    // ── the decoder ─────────────────────────────────────────────────────────

    [Fact]
    public void Decoder_TakesAFrameCutAtEveryByte()
    {
        byte[] stream = [.. Frame(1, "2026-10-23T10:00:01Z hello\n"), .. Frame(2, "2026-10-23T10:00:02Z world\n")];
        var lines = Decode(DockerLogStream.MultiplexedType, stream.Select(b => new[] { b }).ToArray());

        Assert.Equal([("hello", false), ("world", true)], lines.Select(l => (l.Text, l.IsError)));
        Assert.Equal(new DockerStamp(1792749602, 0), lines[1].Stamp);
    }

    [Fact]
    public void Decoder_WaitsForEightBytes_BeforeItCallsAStreamFramed()
    {
        byte[] stream = Frame(1, "2026-10-23T10:00:01Z hello\n");
        var decoder = new DockerLogDecoder(contentType: null);
        var lines = new List<DockerLogLine>();
        decoder.Feed(stream.AsSpan(0, 3), lines);
        Assert.Null(decoder.Framed);

        decoder.Feed(stream.AsSpan(3), lines);
        Assert.True(decoder.Framed);   // API 1.41 labels a framed stream raw: the bytes tell
        Assert.Equal("hello", Assert.Single(lines).Text);
    }

    [Fact]
    public void Decoder_JoinsACharacterCutAcrossChunks()
    {
        byte[] stream = Frame(1, "2026-10-23T10:00:01Z café ☕\n");
        var lines = Decode(DockerLogStream.MultiplexedType, stream[..^4], stream[^4..^2], stream[^2..]);
        Assert.Equal("café ☕", Assert.Single(lines).Text);
    }

    [Fact]
    public void Decoder_KeepsEachStreamsLineApart_AndJoinsALongMessagesParts()
    {
        var lines = Decode(DockerLogStream.MultiplexedType,
            Frame(1, "2026-10-23T10:00:01Z out "),
            Frame(2, "2026-10-23T10:00:01Z err\n"),
            Frame(1, "2026-10-23T10:00:02Z goes on\n"));

        Assert.Equal([("err", true), ("out goes on", false)], lines.Select(l => (l.Text, l.IsError)));   // the second part's stamp stripped
        Assert.Equal(new DockerStamp(1792749602, 0), lines[1].Stamp);   // the last part's stamp
    }

    [Fact]
    public void Decoder_RawStream_TakesAStampAtEachLinesStart()
    {
        var lines = Decode("application/vnd.docker.raw-stream", Bytes("2026-10-23T10:00:01Z one\r\n2026-10-23T10:00:02Z \u001b[32mtwo\u001b[0m\r\n"));
        Assert.Equal(["one", "two"], lines.Select(l => l.Text));
        Assert.False(lines[0].IsError);
    }

    [Fact]
    public void Decoder_ALoneCarriageReturn_EndsALine_AndTextThatIsNoStamp_StaysText()
    {
        var lines = Decode("application/vnd.docker.raw-stream", Bytes("progress 50%\rprogress 100%\n"));
        Assert.Equal(["progress 50%", "progress 100%"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Null(l.Stamp));
    }

    [Fact]
    public void Decoder_CutsALongLine_NeverInsideAPair_AndFlushesTheLast()
    {
        var decoder = new DockerLogDecoder("application/vnd.docker.raw-stream", stamped: false, maxLineChars: 4);
        var lines = new List<DockerLogLine>();
        decoder.Feed(Bytes("abc\U0001F600de"), lines);
        decoder.Flush(lines);

        Assert.Equal(["abc", "\U0001F600de"], lines.Select(l => l.Text));   // the cut stepped back from the pair
    }

    // ── the engine's JSON ───────────────────────────────────────────────────

    [Fact]
    public void Events_AreRead_TheNewFieldsOrTheLegacyOnes()
    {
        Assert.Equal(new DockerEvent("start", "abc", "mysql_dev"), DockerJson.Event(FakeDockerEngine.Event("start", "abc", "mysql_dev")));
        Assert.Equal(new DockerEvent("die", "abc", ""), DockerJson.Event("""{"status":"die","id":"abc"}"""));
        Assert.Null(DockerJson.Event("not json"));
        Assert.Null(DockerJson.Event("""{"Type":"container"}"""));
    }

    [Fact]
    public void Inspect_ReadsTheStartTime_AndRunningIsRunningOrPaused()
    {
        var inspected = DockerJson.Inspected("""{"Id":"abc","Name":"/x","State":{"Status":"paused","StartedAt":"2026-10-23T10:00:00.000000001Z"}}""")!;
        Assert.Equal("2026-10-23T10:00:00.000000001Z", inspected.StartedAt);
        Assert.True(inspected.Running);
        Assert.False(DockerJson.Inspected("""{"Id":"abc","State":{"Status":"exited"}}""")!.Running);
        Assert.NotNull(DockerJson.Inspected(InspectJson)!.StartedAt);
    }

    // ── the follow and the events ───────────────────────────────────────────

    [Fact]
    public async Task FollowLogs_AsksToFollow_WithStamps_AndHandsEachLineOver_ThenEnds()
    {
        On("/v1.47/containers/" + MysqlId + "/logs", _ => StubHttpMessageHandler.Bytes(HttpStatusCode.OK, [.. Frame(1, "2026-10-23T10:00:01Z ready\n"), .. Frame(2, "2026-10-23T10:00:02Z oops\n")], DockerLogStream.MultiplexedType));
        var lines = new List<DockerLogLine>();

        var end = await _docker.Client().FollowLogsAsync(MysqlId, null, new DockerStamp(1792749600, 5), lines.Add, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(DockerFollowEnd.Ended, end);
        Assert.Equal([("ready", false), ("oops", true)], lines.Select(l => (l.Text, l.IsError)));
        string query = _stub.Requests.Last().Uri.Query;
        Assert.Equal("?follow=1&stdout=1&stderr=1&tail=all&since=1792749600.000000005&timestamps=1", query);
    }

    [Fact]
    public async Task FollowLogs_A404IsNotFound_AnotherRefusalSaysTheEnginesWords_AndADisposedClientFails()
    {
        On("/v1.47/containers/gone/logs", HttpStatusCode.NotFound, """{"message":"No such container: gone"}""");
        On("/v1.47/containers/" + MysqlId + "/logs", HttpStatusCode.NotImplemented, """{"message":"configured logging driver does not support reading"}""");
        var client = _docker.Client();

        Assert.Equal(DockerFollowOutcome.NotFound, (await client.FollowLogsAsync("gone", 5, null, _ => { }, TimeSpan.FromSeconds(5), CancellationToken.None)).Outcome);
        var refused = await client.FollowLogsAsync(MysqlId, 5, null, _ => { }, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(DockerFollowOutcome.Refused, refused.Outcome);
        Assert.Contains("logging driver", refused.Error);

        var http = new HttpClient(_stub);
        http.Dispose();
        using var disposed = new DockerClient("docker_engine", http, transport: DockerTransport.Pipe);
        Assert.Equal(DockerFollowOutcome.Failed, (await disposed.FollowLogsAsync(MysqlId, 5, null, _ => { }, TimeSpan.FromSeconds(5), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task FollowLogs_ACancel_IsThrown_AndTheStreamIsLetGo()
    {
        var body = new LiveStream();
        On("/v1.47/containers/" + MysqlId + "/logs", _ => Streamed(body, DockerLogStream.MultiplexedType));
        using var cancel = new CancellationTokenSource();
        var first = new TaskCompletionSource();

        var follow = _docker.Client().FollowLogsAsync(MysqlId, 5, null, _ => first.TrySetResult(), TimeSpan.FromSeconds(5), cancel.Token);
        body.Write(Frame(1, "2026-10-23T10:00:01Z one\n"));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follow);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task WatchEvents_SendsTheFilterEscaped_AndReadsUntilTheCallbackSaysStop()
    {
        var body = new LiveStream();
        On("/v1.47/events", _ => Streamed(body, "application/json"));
        var seen = new List<string>();
        body.Write(FakeDockerEngine.Event("start", "a", "x") + FakeDockerEngine.Event("destroy", "a", "x"));

        var end = await _docker.Client().WatchEventsAsync(DockerLogFeed.EventsFilter("mysql_dev"), line => { seen.Add(line); return seen.Count < 2; }, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(DockerFollowOutcome.Ended, end.Outcome);
        Assert.Equal(2, seen.Count);
        var uri = _stub.Requests.Last().Uri;
        Assert.DoesNotContain("{", uri.Query, StringComparison.Ordinal);
        Assert.Equal("?filters=" + DockerLogFeed.EventsFilter("mysql_dev"), Uri.UnescapeDataString(uri.Query));
    }

    private static HttpResponseMessage Streamed(LiveStream body, string mediaType)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // ── /docker logs' words, the session, the pane ──────────────────────────

    [Theory]
    [InlineData("mysql_dev", "mysql_dev", null, false)]
    [InlineData("mysql_dev 30", "mysql_dev", 30, false)]
    [InlineData("mysql_dev --pane", "mysql_dev", null, true)]
    [InlineData("--pane mysql_dev 30", "mysql_dev", 30, true)]
    [InlineData("mysql_dev 30 --PANE", "mysql_dev", 30, true)]
    public void ParseLogs_DropsThePaneWordWhereverItStands(string rest, string target, int? count, bool pane) =>
        Assert.Equal((target, count, pane), DockerCommand.ParseLogs(rest));

    [Fact]
    public async Task LogsTarget_ResolvesTheContainer_DefaultsTo200_AndRefusesAsThePaneDoes()
    {
        var (container, tail, error) = await DockerCommand.LogsTargetAsync(_docker, "mysql_dev", CancellationToken.None);
        Assert.Equal((MysqlId, DockerLogWindowText.DefaultWindowLines, null), (container?.Id, tail, error));
        Assert.Equal(30, (await DockerCommand.LogsTargetAsync(_docker, "mysql_dev 30", CancellationToken.None)).Tail);
        Assert.Equal(DockerText.Usage, (await DockerCommand.LogsTargetAsync(_docker, "", CancellationToken.None)).Error);
        Assert.Equal(DockerText.Usage, (await DockerCommand.LogsTargetAsync(_docker, "mysql_dev 0", CancellationToken.None)).Error);
        Assert.Equal(DockerText.Usage, (await DockerCommand.LogsTargetAsync(_docker, "mysql_dev " + (DockerLogsTool.MaxTail + 1), CancellationToken.None)).Error);
        Assert.NotNull((await DockerCommand.LogsTargetAsync(_docker, "nope", CancellationToken.None)).Error);
    }

    [Fact]
    public void Complete_OffersThePaneWord_AfterTheContainer()
    {
        var last = Containers();
        Assert.Equal(["logs mysql_dev --pane"], DockerCommand.Complete(last, "logs mysql_dev ").Select(i => i.Text));
        Assert.Equal(["logs mysql_dev 30 --pane"], DockerCommand.Complete(last, "logs mysql_dev 30 --p").Select(i => i.Text));
        Assert.Empty(DockerCommand.Complete(last, "logs mysql_dev --pane "));
        Assert.Equal(DockerText.PaneNote, DockerCommand.Complete(null, "logs x ")[0].Note);
    }

    [Fact]
    public void TryClient_GivesTheClient_UntilTheSessionIsDisposed()
    {
        var engine = new FakeDockerEngine();
        Assert.True(engine.Session.TryClient(out var client));
        Assert.Same(engine.Session.Client(), client);

        engine.Dispose();

        Assert.False(engine.Session.TryClient(out var none));
        Assert.Null(none);
        Assert.True(engine.Session.IsDisposed);
    }

    [Fact]
    public void ThePanesLogsRow_NamesTheWindow_WhenThereIsOne()
    {
        Assert.Contains(DockerLogWindowText.MenuNote, DockerMenu.RowPageRow(DockerMenu.LogsWord, window: true));
        Assert.Contains("into the transcript", DockerMenu.RowPageRow(DockerMenu.LogsWord, window: false));
        Assert.Equal(DockerMenu.RowPageRow(DockerMenu.LogsWord, window: false), DockerMenu.RowPageRow(DockerMenu.LogsWord));
    }
}
