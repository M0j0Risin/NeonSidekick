using System.Net;
using System.Net.Http.Headers;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The resumable, pinned downloads and archive sets <see cref="ModelStore"/> gained for the embedded LLM (2026-09-29):
/// Range resume (206 appends, 200 restarts, 416 is whole), the SHA-256 check, the disk-space refusal, cancellation
/// keeping the partial file, and several zips unpacked into one folder.
/// </summary>
public class ModelStoreResumeTests : IDisposable
{
    private const string Url = "https://huggingface.co/owner/repo/resolve/abc/model.gguf";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"), "models");
    private readonly StubHttpMessageHandler _http = new();
    private readonly ModelStore _store;
    private readonly List<RangeHeaderValue?> _ranges = new();

    public ModelStoreResumeTests()
    {
        _store = new ModelStore(_dir, new HttpClient(_http), "EmbeddedLlm") { AvailableBytes = _ => null };
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { /* best effort */ }
    }

    private string ModelPath => Path.Combine(_dir, "llm", "m", "model.gguf");

    private ModelSpec Pinned(byte[] body, string? sha = null) =>
        new("test model", ModelPath, new Uri(Url), body.Length, ModelFormat.Gguf, sha ?? FakeModelFiles.Sha256(body), Resumable: true);

    /// <summary>Serves <paramref name="body"/> at <paramref name="url"/>, honouring a Range header unless <paramref name="ignoreRanges"/>.</summary>
    private void Serve(string url, byte[] body, bool ignoreRanges = false) =>
        _http.Map(url, (request, _) =>
        {
            _ranges.Add(request.Headers.Range);
            if (!ignoreRanges && request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } from)
            {
                if (from >= body.Length)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
                }

                var partial = new ByteArrayContent(body[(int)from..]);
                partial.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = partial });
            }

            return Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, "application/octet-stream"));
        });

    private void WritePartial(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        File.WriteAllBytes(ModelStore.PartialPath(ModelPath), bytes);
    }

    // ── Labels and magic ────────────────────────────────────────────────────

    [Theory]
    [InlineData(4_215_695_776, "4.2 GB")]
    [InlineData(5_369_246_688, "5.4 GB")]
    [InlineData(1_000_000_000, "1 GB")]
    [InlineData(990_372_672, "990 MB")]
    public void SizeLabel_HasAGigabyteStep(long bytes, string expected)
    {
        Assert.Equal(expected, ModelStore.SizeLabel(bytes));
    }

    [Fact]
    public void LooksLikeGguf_ChecksTheMagic()
    {
        Directory.CreateDirectory(_dir);
        string good = Path.Combine(_dir, "a.gguf");
        File.WriteAllBytes(good, FakeModelFiles.GgufBytes());
        string ggml = FakeModelFiles.Write(Path.Combine(_dir, "b.bin"));

        Assert.True(ModelStore.LooksLikeGguf(good));
        Assert.True(ModelStore.LooksLike(good, ModelFormat.Gguf));
        Assert.False(ModelStore.LooksLike(ggml, ModelFormat.Gguf));
        Assert.False(ModelStore.LooksLikeGguf(Path.Combine(_dir, "missing.gguf")));
        Assert.Equal("a GGUF", ModelStore.FormatName(ModelFormat.Gguf));
        Assert.Equal("a zip", ModelStore.FormatName(ModelFormat.Zip));
    }

    // ── Resumable EnsureAsync ───────────────────────────────────────────────

    [Fact]
    public async Task Resumable_Downloads_Verifies_AndLeavesNoPartial()
    {
        var body = FakeModelFiles.GgufBytes(200_000);
        Serve(Url, body);
        int verifying = 0;

        var result = await _store.EnsureAsync(Pinned(body), null, () => verifying++, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.False(File.Exists(ModelStore.PartialPath(ModelPath)));
        Assert.Equal(1, verifying);
        Assert.Null(Assert.Single(_ranges));

        // Present next time: no request, no hashing.
        var again = await _store.EnsureAsync(Pinned(body), null, () => verifying++, CancellationToken.None);
        Assert.True(again.Ok);
        Assert.Equal("present", again.Detail);
        Assert.Single(_ranges);
        Assert.Equal(1, verifying);
    }

    [Fact]
    public async Task Resumable_PartialFile_AsksForTheRest_AndAppends()
    {
        var body = FakeModelFiles.GgufBytes(200_000);
        WritePartial(body[..70_000]);
        Serve(Url, body);
        var progress = new List<(long Received, long? Total)>();

        var result = await _store.EnsureAsync(Pinned(body), new SyncProgress(progress), CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Equal(70_000, Assert.Single(_ranges)!.Ranges.Single().From);
        Assert.Equal((70_000L, (long?)200_000), progress[0]);
        Assert.Equal((200_000L, (long?)200_000), progress[^1]);
    }

    [Fact]
    public async Task Resumable_ServerIgnoringRanges_StartsOver()
    {
        var body = FakeModelFiles.GgufBytes(100_000);
        WritePartial(new byte[40_000]);   // wrong bytes: a restart must overwrite them, not append after them
        Serve(Url, body, ignoreRanges: true);

        var result = await _store.EnsureAsync(Pinned(body), null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.NotNull(Assert.Single(_ranges));
    }

    [Fact]
    public async Task Resumable_WholePartialOfAnUnknownSize_Answered416_IsKept()
    {
        var body = FakeModelFiles.GgufBytes(50_000);
        WritePartial(body);
        Serve(Url, body);
        var spec = new ModelSpec("test model", ModelPath, new Uri(Url), 0, ModelFormat.Gguf, Resumable: true);

        var result = await _store.EnsureAsync(spec, null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Equal(50_000, Assert.Single(_ranges)!.Ranges.Single().From);
    }

    [Fact]
    public async Task Resumable_WholePartialOfAPinnedSize_NeedsNoRequest()
    {
        var body = FakeModelFiles.GgufBytes(50_000);
        WritePartial(body);

        var result = await _store.EnsureAsync(Pinned(body), null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Resumable_ChecksumMismatch_DeletesTheDownload_AndFails()
    {
        var body = FakeModelFiles.GgufBytes(10_000);
        Serve(Url, body);

        var result = await _store.EnsureAsync(Pinned(body, sha: new string('0', 64)), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ModelStore.ChecksumError("test model"), result.Detail);
        Assert.False(File.Exists(ModelPath));
        Assert.False(File.Exists(ModelStore.PartialPath(ModelPath)));
    }

    [Fact]
    public async Task Resumable_NotAGguf_IsDeleted_AndFails()
    {
        var body = new byte[10_000];
        Serve(Url, body);

        var result = await _store.EnsureAsync(Pinned(body), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not a GGUF model", result.Detail);
        Assert.False(File.Exists(ModelStore.PartialPath(ModelPath)));
    }

    [Fact]
    public async Task Resumable_ABodyOfAnotherSize_IsRefusedBeforeAByteIsWritten()
    {
        var body = FakeModelFiles.GgufBytes(10_000);
        Serve(Url, body);
        var spec = Pinned(body) with { ApproxBytes = 12_000 };

        var result = await _store.EnsureAsync(spec, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("offers 10000 bytes, not the 12000 expected", result.Detail);
        Assert.False(File.Exists(ModelStore.PartialPath(ModelPath)));
    }

    [Fact]
    public async Task Resumable_Cancelled_KeepsThePartialFile_AndTheNextEnsureResumes()
    {
        var body = FakeModelFiles.GgufBytes(300_000);
        Serve(Url, body);
        using var cts = new CancellationTokenSource();
        var cancelling = new CallbackProgress(p => { if (p.Received > 0) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.EnsureAsync(Pinned(body), cancelling, cts.Token));

        long kept = new FileInfo(ModelStore.PartialPath(ModelPath)).Length;
        Assert.InRange(kept, 1, body.Length - 1);
        Assert.False(File.Exists(ModelPath));

        var result = await _store.EnsureAsync(Pinned(body), null, CancellationToken.None);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
        Assert.Equal(kept, _ranges[^1]!.Ranges.Single().From);
    }

    [Fact]
    public async Task Resumable_AFullDisk_IsRefusedWithoutARequest()
    {
        var body = FakeModelFiles.GgufBytes(10_000);
        var store = new ModelStore(_dir, new HttpClient(_http), "EmbeddedLlm") { AvailableBytes = _ => 5_000 };

        var result = await store.EnsureAsync(Pinned(body), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("not enough disk space for test model: it needs 10 KB more and 1 GB to spare,", result.Detail);
        Assert.EndsWith("has 5 KB free", result.Detail);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Resumable_PresentFileOfTheWrongLength_IsDiscarded_AndFetchedAgain()
    {
        var body = FakeModelFiles.GgufBytes(10_000);
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        File.WriteAllBytes(ModelPath, body[..5_000]);
        Serve(Url, body);

        var result = await _store.EnsureAsync(Pinned(body), null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(ModelPath));
    }

    // ── Archive sets ────────────────────────────────────────────────────────

    private const string BinUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b1/llama-bin.zip";
    private const string RtUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b1/cudart.zip";

    private string RuntimeDir => Path.Combine(_dir, "llama", "b1-cuda");

    private ArchiveSetSpec RuntimeSpec(byte[] bin, byte[] rt, params string[] required) => new(
        "llama.cpp runtime",
        RuntimeDir,
        new[]
        {
            new ArchivePart("llama-bin.zip", new Uri(BinUrl), bin.Length, FakeModelFiles.Sha256(bin)),
            new ArchivePart("cudart.zip", new Uri(RtUrl), rt.Length, FakeModelFiles.Sha256(rt)),
        },
        required.Length == 0 ? new[] { "llama-server.exe", "cudart64_13.dll" } : required);

    [Fact]
    public async Task ArchiveSet_UnpacksEveryPartIntoOneFolder_AndCleansUp()
    {
        var bin = FakeModelFiles.Zip(("llama-server.exe", "server"), ("ggml-cuda.dll", "cuda"));
        var rt = FakeModelFiles.Zip(("cudart64_13.dll", "runtime"));
        Serve(BinUrl, bin);
        Serve(RtUrl, rt);
        var progress = new List<(long Received, long? Total)>();
        bool unpacked = false;

        var result = await _store.EnsureArchiveSetAsync(RuntimeSpec(bin, rt), new SyncProgress(progress), null, () => unpacked = true, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.True(unpacked);
        Assert.Equal("server", File.ReadAllText(Path.Combine(RuntimeDir, "llama-server.exe")));
        Assert.Equal("runtime", File.ReadAllText(Path.Combine(RuntimeDir, "cudart64_13.dll")));
        Assert.True(File.Exists(Path.Combine(RuntimeDir, "ggml-cuda.dll")));
        Assert.False(Directory.Exists(RuntimeDir + ".parts"));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(RuntimeDir)!, "*.extracting"));
        Assert.All(progress, p => Assert.Equal(bin.Length + rt.Length, p.Total));
        Assert.Equal(bin.Length + rt.Length, progress[^1].Received);

        var fresh = new ModelStore(_dir, new HttpClient(new StubHttpMessageHandler()), "EmbeddedLlm");
        var again = await fresh.EnsureArchiveSetAsync(RuntimeSpec(bin, rt), null, null, null, CancellationToken.None);
        Assert.True(again.Ok);
        Assert.Equal("present", again.Detail);
    }

    // ── A tar.gz (2026-10-07, llama.cpp's macOS build) ───────────────────────

    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode Plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private ArchiveSetSpec TarSpec(byte[] tar, params string[] required) => new(
        "llama.cpp runtime",
        RuntimeDir,
        new[] { new ArchivePart("llama-bin-macos-arm64.tar.gz", new Uri(BinUrl), tar.Length, FakeModelFiles.Sha256(tar)) },
        required);

    [UnixFact]
    public async Task ArchiveSet_UnpacksATarGz_FromItsTopFolder_KeepingExecBitsAndLinks()
    {
        var tar = FakeModelFiles.TarGz(
            ("llama-b1/llama-server", "server", Executable, null),
            ("llama-b1/libllama.0.5.0.dylib", "lib", Plain, null),
            ("llama-b1/libllama.0.dylib", "", Plain, "libllama.0.5.0.dylib"));
        Serve(BinUrl, tar);

        var result = await _store.EnsureArchiveSetAsync(TarSpec(tar, "llama-server", "libllama.0.dylib", "libllama.0.5.0.dylib"), null, null, null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        string server = Path.Combine(RuntimeDir, "llama-server");
        Assert.Equal("server", File.ReadAllText(server));
        Assert.True(OperatingSystem.IsWindows() || (File.GetUnixFileMode(server) & UnixFileMode.UserExecute) != 0);
        var link = new FileInfo(Path.Combine(RuntimeDir, "libllama.0.dylib"));
        Assert.Equal("libllama.0.5.0.dylib", link.LinkTarget);
        Assert.Equal("lib", File.ReadAllText(link.FullName));
        Assert.False(Directory.Exists(RuntimeDir + ".parts"));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(RuntimeDir)!, "*.extracting"));
    }

    [Fact]
    public async Task ArchiveSet_ATarGzThatIsNoGzip_IsRefused()
    {
        var zip = FakeModelFiles.Zip(("llama-server", "server"));
        Serve(BinUrl, zip);

        var result = await _store.EnsureArchiveSetAsync(TarSpec(zip, "llama-server"), null, null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("is not a gzip archive", result.Detail);
        Assert.False(Directory.Exists(RuntimeDir));
    }

    [UnixFact]
    public async Task ArchiveSet_ATarGzLinkingOutOfTheFolder_Fails_AndLeavesNoFolder()
    {
        var tar = FakeModelFiles.TarGz(
            ("llama-b1/llama-server", "server", Executable, null),
            ("llama-b1/escape", "", Plain, "../../../../etc/passwd"));
        Serve(BinUrl, tar);

        var result = await _store.EnsureArchiveSetAsync(TarSpec(tar, "llama-server"), null, null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("outside the specified destination", result.Detail);
        Assert.False(Directory.Exists(RuntimeDir));
    }

    [Fact]
    public void ArchiveFormat_IsReadFromTheName()
    {
        Assert.Equal(ModelFormat.TarGz, ModelStore.ArchiveFormat("llama-b11258-bin-macos-arm64.tar.gz"));
        Assert.Equal(ModelFormat.TarGz, ModelStore.ArchiveFormat("x.TGZ"));
        Assert.Equal(ModelFormat.Zip, ModelStore.ArchiveFormat("cudart-llama-bin-win-cuda-13.4-x64.zip"));
        Assert.Equal("a gzip", ModelStore.FormatName(ModelFormat.TarGz));
    }

    [Fact]
    public async Task ArchiveSet_MissingARequiredFile_Fails_AndLeavesNoFolder()
    {
        var bin = FakeModelFiles.Zip(("llama-server.exe", "server"));
        var rt = FakeModelFiles.Zip(("readme.txt", "no dll here"));
        Serve(BinUrl, bin);
        Serve(RtUrl, rt);

        var result = await _store.EnsureArchiveSetAsync(RuntimeSpec(bin, rt), null, null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no cudart64_13.dll", result.Detail);
        Assert.False(Directory.Exists(RuntimeDir));
    }

    [Fact]
    public async Task ArchiveSet_APartThatFails_FailsTheSet()
    {
        var bin = FakeModelFiles.Zip(("llama-server.exe", "server"));
        var rt = FakeModelFiles.Zip(("cudart64_13.dll", "runtime"));
        Serve(BinUrl, bin);
        _http.Map(RtUrl, HttpStatusCode.NotFound, "gone");

        var result = await _store.EnsureArchiveSetAsync(RuntimeSpec(bin, rt), null, null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("HTTP 404", result.Detail);
        Assert.False(Directory.Exists(RuntimeDir));
        // The part that arrived stays for the next try.
        Assert.True(File.Exists(Path.Combine(RuntimeDir + ".parts", "llama-bin.zip")));
    }

    [Fact]
    public async Task ArchiveSet_AnIncompleteFolder_IsDiscarded_AndFetchedAgain()
    {
        Directory.CreateDirectory(RuntimeDir);
        File.WriteAllText(Path.Combine(RuntimeDir, "llama-server.exe"), "old");
        var bin = FakeModelFiles.Zip(("llama-server.exe", "server"));
        var rt = FakeModelFiles.Zip(("cudart64_13.dll", "runtime"));
        Serve(BinUrl, bin);
        Serve(RtUrl, rt);

        var result = await _store.EnsureArchiveSetAsync(RuntimeSpec(bin, rt), null, null, null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal("server", File.ReadAllText(Path.Combine(RuntimeDir, "llama-server.exe")));
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
