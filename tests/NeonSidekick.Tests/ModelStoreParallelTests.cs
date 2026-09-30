using System.Net;
using System.Net.Http.Headers;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The parallel downloads <see cref="ModelStore"/> gained for Embedded HF download type (2026-09-30): a pinned file fetched as
/// <see cref="ModelStore.ParallelConnections"/> ranged parts and joined, resumed part by part, a join cut short picked up, a
/// server that ignores ranges sent back to one stream, retries, failures and cancels that keep the parts, and the setting.
/// </summary>
public class ModelStoreParallelTests : IDisposable
{
    private const string Url = "https://huggingface.co/owner/repo/resolve/abc/model.gguf";
    private const int Parts = ModelStore.ParallelConnections;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"), "models");
    private readonly StubHttpMessageHandler _http = new();
    private readonly ModelStore _store;
    private readonly List<(long? From, long? To)> _ranges = new();

    public ModelStoreParallelTests()
    {
        _store = new ModelStore(_dir, new HttpClient(_http), "EmbeddedLlm") { AvailableBytes = _ => null, ParallelMinimumBytes = 1 };
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { /* best effort */ }
    }

    private string ModelPath => Path.Combine(_dir, "llm", "m", "model.gguf");

    /// <summary>A GGUF body whose bytes past the magic are random, so a part joined out of place shows.</summary>
    private static byte[] Body(int length)
    {
        var body = FakeModelFiles.GgufBytes(length);
        var rest = new byte[length - 4];
        new Random(42).NextBytes(rest);
        rest.CopyTo(body, 4);
        return body;
    }

    private ModelSpec Spec(byte[] body, int connections = Parts, string? sha = null) =>
        new("test model", ModelPath, new Uri(Url), body.Length, ModelFormat.Gguf, sha ?? FakeModelFiles.Sha256(body), Resumable: true, connections);

    private ParallelDownload Split(byte[] body, int count = Parts) => new(new HttpClient(_http), "EmbeddedLlm", "test model", new Uri(Url), ModelPath, body.Length, count);

    /// <summary>
    /// Serves <paramref name="body"/>, honouring a <c>From-To</c> range unless <paramref name="ignoreRanges"/>; <paramref name="intercept"/>
    /// may answer a request (by its range) instead.
    /// </summary>
    private void Serve(byte[] body, bool ignoreRanges = false, Func<long, HttpResponseMessage?>? intercept = null) =>
        _http.Map(Url, (request, _) =>
        {
            var range = request.Headers.Range?.Ranges.Single();
            lock (_ranges)
            {
                _ranges.Add((range?.From, range?.To));
            }

            if (range?.From is { } from && intercept?.Invoke(from) is { } answer)
            {
                return Task.FromResult(answer);
            }

            if (ignoreRanges || range?.From is not { } start)
            {
                return Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, "application/octet-stream"));
            }

            long end = range.To ?? body.Length - 1;
            var partial = new ByteArrayContent(body[(int)start..(int)(end + 1)]);
            partial.Headers.ContentRange = new ContentRangeHeaderValue(start, end, body.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = partial });
        });

    private void WritePart(byte[] body, int index, int held, int count = Parts)
    {
        var split = Split(body, count);
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        long start = split.Start(index);
        File.WriteAllBytes(ParallelDownload.PartPath(ModelPath, index, count), body[(int)start..(int)(start + held)]);
    }

    private string[] LeftParts() =>
        Directory.Exists(Path.GetDirectoryName(ModelPath)) ? Directory.GetFiles(Path.GetDirectoryName(ModelPath)!, "model.gguf.partial.*-of-*") : [];

    // ── The split ──────────────────────────────────────────────────────────

    [Fact]
    public void Split_IsEqualShares_TheLastTakingTheRemainder()
    {
        var split = new ParallelDownload(new HttpClient(_http), "EmbeddedLlm", "m", new Uri(Url), ModelPath, 100_003, 8);

        Assert.Equal(8, split.Count);
        Assert.Equal(0, split.Start(0));
        Assert.Equal(12_499, split.End(0));
        Assert.Equal(12_500, split.Start(1));
        Assert.Equal(100_002, split.End(7));
        Assert.Equal(12_503, split.LargestPartBytes);
        Assert.Equal(100_003, Enumerable.Range(0, 8).Sum(i => split.Length(i)));
        Assert.Equal(3, new ParallelDownload(new HttpClient(_http), "EmbeddedLlm", "m", new Uri(Url), ModelPath, 3, 8).Count);   // never more parts than bytes
        Assert.Equal(ModelPath + ".partial.2-of-8", ParallelDownload.PartPath(ModelPath, 2, 8));
    }

    // ── Downloads ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Parallel_FetchesEveryPartAsARange_JoinsThem_AndVerifies()
    {
        var body = Body(100_003);
        Serve(body);
        int verifying = 0;
        var progress = new List<(long Received, long? Total)>();

        var result = await _store.EnsureAsync(Spec(body), new SyncProgress(progress), () => verifying++, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Equal(1, verifying);
        var split = Split(body);
        Assert.Equal(Enumerable.Range(0, Parts).Select(i => ((long?)split.Start(i), (long?)split.End(i))).OrderBy(r => r.Item1), _ranges.OrderBy(r => r.From));
        Assert.Empty(LeftParts());
        Assert.False(File.Exists(ModelStore.PartialPath(ModelPath)));
        Assert.Equal((0L, (long?)body.Length), progress[0]);
        Assert.Equal((body.Length, (long?)body.Length), progress[^1]);
    }

    [Fact]
    public async Task Parallel_BelowTheMinimum_OrUnpinned_OrOneConnection_IsOneStream()
    {
        var body = Body(50_000);
        Serve(body);
        var store = new ModelStore(_dir, new HttpClient(_http), "EmbeddedLlm") { AvailableBytes = _ => null };

        Assert.True((await store.EnsureAsync(Spec(body), null, CancellationToken.None)).Ok);   // 64 MB minimum
        File.Delete(ModelPath);
        Assert.True((await _store.EnsureAsync(Spec(body, connections: 1), null, CancellationToken.None)).Ok);
        File.Delete(ModelPath);
        Assert.True((await _store.EnsureAsync(Spec(body) with { Sha256 = null }, null, CancellationToken.None)).Ok);

        Assert.Equal(3, _ranges.Count);
        Assert.All(_ranges, r => Assert.Null(r.From));
    }

    [Fact]
    public async Task Parallel_PartlyFilledParts_AskOnlyForTheirRest()
    {
        var body = Body(100_003);
        var split = Split(body);
        WritePart(body, 0, (int)split.Length(0));   // whole: no request
        WritePart(body, 3, 5_000);
        WritePart(body, 7, 1);
        Serve(body);
        var progress = new List<(long Received, long? Total)>();

        var result = await _store.EnsureAsync(Spec(body), new SyncProgress(progress), CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Equal(Parts - 1, _ranges.Count);
        Assert.Contains(_ranges, r => r.From == split.Start(3) + 5_000 && r.To == split.End(3));
        Assert.Contains(_ranges, r => r.From == split.Start(7) + 1);
        Assert.DoesNotContain(_ranges, r => r.From == 0);
        Assert.Equal(split.Length(0) + 5_000 + 1, progress[0].Received);
    }

    [Fact]
    public async Task Parallel_AJoinCutShort_IsPickedUp_WithoutARequest()
    {
        var body = Body(100_003);
        var split = Split(body);
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        // Parts 0–2 joined, part 3 half appended when the app stopped; parts 3–7 still whole beside it.
        File.WriteAllBytes(ModelStore.PartialPath(ModelPath), body[..(int)(split.Start(3) + 4_000)]);
        for (int i = 3; i < Parts; i++)
        {
            WritePart(body, i, (int)split.Length(i));
        }

        var result = await _store.EnsureAsync(Spec(body), null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Empty(_http.Requests);
        Assert.Empty(LeftParts());
    }

    [Fact]
    public async Task Parallel_ServerIgnoringRanges_GoesBackToOneStream()
    {
        var body = Body(100_003);
        Serve(body, ignoreRanges: true);

        var result = await _store.EnsureAsync(Spec(body), null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Empty(LeftParts());
        Assert.Null(_ranges[^1].From);   // the one stream asks from the start
    }

    [Fact]
    public async Task Parallel_AServerError_IsTriedAgain()
    {
        var body = Body(100_003);
        long third = Split(body).Start(2);
        int failed = 0;
        Serve(body, intercept: from => from == third && Interlocked.Exchange(ref failed, 1) == 0 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null);

        var result = await _store.EnsureAsync(Spec(body), null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Equal(2, _ranges.Count(r => r.From == third));
    }

    [Fact]
    public async Task Parallel_AServerErrorEveryTime_GivesUp_AndKeepsWhatArrived()
    {
        var body = Body(100_003);
        long third = Split(body).Start(2);
        Serve(body, intercept: from => from == third ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null);

        var result = await _store.EnsureAsync(Spec(body), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("HTTP 502", result.Detail);
        Assert.Equal(ParallelDownload.MaxAttempts, _ranges.Count(r => r.From == third));
        Assert.False(File.Exists(ModelPath));
    }

    [Fact]
    public async Task Parallel_ANotFound_FailsAtOnce()
    {
        var body = Body(100_003);
        long sixth = Split(body).Start(5);
        Serve(body, intercept: from => from == sixth ? new HttpResponseMessage(HttpStatusCode.NotFound) : null);

        var result = await _store.EnsureAsync(Spec(body), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("HTTP 404", result.Detail);
        Assert.Equal(1, _ranges.Count(r => r.From == sixth));
        Assert.False(File.Exists(ModelPath));
    }

    [Fact]
    public async Task Parallel_OtherBytesThanAsked_DeleteThatPart_AndFail()
    {
        var body = Body(100_003);
        var split = Split(body);
        long fourth = split.Start(3);
        WritePart(body, 3, 100);
        Serve(body, intercept: from =>
        {
            if (from != fourth + 100)
            {
                return null;
            }

            var wrong = new ByteArrayContent(body[(int)(from + 1)..(int)(split.End(3) + 1)]);
            wrong.Headers.ContentRange = new ContentRangeHeaderValue(from + 1, split.End(3), body.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = wrong };
        });

        var result = await _store.EnsureAsync(Spec(body), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("sent other bytes than part 4 of test model asked for", result.Detail);
        Assert.False(File.Exists(ParallelDownload.PartPath(ModelPath, 3, Parts)));
        Assert.False(File.Exists(ModelPath));
    }

    [Fact]
    public async Task Parallel_Cancelled_KeepsTheParts_AndTheNextEnsureResumes()
    {
        var body = Body(8 * 200_000);
        Serve(body);
        using var cts = new CancellationTokenSource();
        var cancelling = new CallbackProgress(p => { if (p.Received > 0) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.EnsureAsync(Spec(body), cancelling, cts.Token));

        long kept = ModelStore.PartialBytes(ModelPath);
        Assert.InRange(kept, 1, body.Length - 1);
        Assert.NotEmpty(LeftParts());
        Assert.False(File.Exists(ModelPath));

        _ranges.Clear();
        var result = await _store.EnsureAsync(Spec(body), null, CancellationToken.None);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Empty(LeftParts());
        Assert.True(_ranges.Sum(r => r.To!.Value - r.From!.Value + 1) <= body.Length - kept);
    }

    [Fact]
    public async Task Parallel_AOneStreamPartial_GoesOnAsOneStream()
    {
        var body = Body(100_003);
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        File.WriteAllBytes(ModelStore.PartialPath(ModelPath), body[..70_000]);
        Serve(body);

        var result = await _store.EnsureAsync(Spec(body), null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Equal(70_000, Assert.Single(_ranges).From);
    }

    [Fact]
    public async Task Parallel_PartsBegun_GoOnAsParts_WhateverTheSpecAsks()
    {
        var body = Body(100_003);
        WritePart(body, 1, 10, count: 4);
        Serve(body);

        var result = await _store.EnsureAsync(Spec(body, connections: 1), null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Equal(4, _ranges.Count);
    }

    [Fact]
    public async Task Parallel_ChecksumMismatch_DeletesTheDownload_AndFails()
    {
        var body = Body(100_003);
        Serve(body);

        var result = await _store.EnsureAsync(Spec(body, sha: new string('0', 64)), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ModelStore.ChecksumError("test model"), result.Detail);
        Assert.False(File.Exists(ModelPath));
        Assert.False(File.Exists(ModelStore.PartialPath(ModelPath)));
        Assert.Empty(LeftParts());
    }

    [Fact]
    public async Task Parallel_AFullDisk_CountsTheJoinsPeak_AndAsksNothing()
    {
        var body = Body(100_003);
        var store = new ModelStore(_dir, new HttpClient(_http), "EmbeddedLlm") { AvailableBytes = _ => ModelStore.DiskHeadroomBytes + body.Length, ParallelMinimumBytes = 1 };

        var result = await store.EnsureAsync(Spec(body), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("not enough disk space for test model:", result.Detail);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public void PartialBytes_CountsTheOneStreamFile_AndTheParts()
    {
        var body = Body(100_003);
        Assert.Equal(0, ModelStore.PartialBytes(ModelPath));
        WritePart(body, 0, 300);
        WritePart(body, 5, 20);
        File.WriteAllBytes(ModelStore.PartialPath(ModelPath), new byte[7]);

        Assert.Equal(327, ModelStore.PartialBytes(ModelPath));
        Assert.Equal(8, ParallelDownload.BegunConnections(ModelPath));
        ParallelDownload.DeleteParts(ModelPath);
        Assert.Equal(7, ModelStore.PartialBytes(ModelPath));
        Assert.Null(ParallelDownload.BegunConnections(ModelPath));
    }

    [Fact]
    public void BegunConnections_OfTwoSplits_DeletesBoth()
    {
        var body = Body(100_003);
        WritePart(body, 0, 10, count: 4);
        WritePart(body, 0, 10, count: 8);

        Assert.Null(ParallelDownload.BegunConnections(ModelPath));
        Assert.Empty(LeftParts());
    }

    // ── The setting ────────────────────────────────────────────────────────

    [Fact]
    public void Setting_IsParallelByDefault_AndSaysHowManyConnections()
    {
        Assert.Equal("parallel", EmbeddedHfDownloadTypes.Default);
        Assert.Equal(["single", "parallel"], EmbeddedHfDownloadTypes.Names);
        Assert.Equal("parallel", new AppSettingsData().EmbeddedHfDownloadType);
        Assert.Equal(ModelStore.ParallelConnections, EmbeddedHfDownloadTypes.Connections(new AppSettingsData()));
        Assert.Equal(1, EmbeddedHfDownloadTypes.Connections(new AppSettingsData { EmbeddedHfDownloadType = " Single " }));
        Assert.Equal(EmbeddedHfDownloadType.Parallel, EmbeddedHfDownloadTypes.Resolve(new AppSettingsData { EmbeddedHfDownloadType = "turbo" }));   // a hand-edited word: the default
        Assert.Equal("single", AppSettings.Copy(new AppSettingsData { EmbeddedHfDownloadType = "single" }).EmbeddedHfDownloadType);
        Assert.All(EmbeddedHfDownloadTypes.Names, n => Assert.NotEmpty(EmbeddedHfDownloadTypes.Describe(n)));
    }

    private sealed class SyncProgress(List<(long Received, long? Total)> sink) : IProgress<(long Received, long? Total)>
    {
        public void Report((long Received, long? Total) value) => sink.Add(value);
    }

    private sealed class CallbackProgress(Action<(long Received, long? Total)> callback) : IProgress<(long Received, long? Total)>
    {
        public void Report((long Received, long? Total) value) => callback(value);
    }
}
