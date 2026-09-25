using System.Net;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class ModelStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"), "models");
    private readonly StubHttpMessageHandler _http = new();
    private readonly ModelStore _store;

    public ModelStoreTests()
    {
        _store = new ModelStore(_dir, new HttpClient(_http));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { /* best effort */ }
    }

    private static string SileroUrl => ModelStore.SileroUrl;

    private void ServeSilero(byte[] body) =>
        _http.Map(SileroUrl, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, "application/octet-stream")));

    private IEnumerable<string> TempFiles() => Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir, "*.tmp") : Array.Empty<string>();

    private void ServeVosk(byte[] body) =>
        _http.Map(ModelStore.VoskModelUrl, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, "application/zip")));

    /// <summary>Anything an interrupted directory ensure could leave: temporary archives and unpack directories.</summary>
    private IEnumerable<string> Leftovers() => Directory.Exists(_dir)
        ? Directory.EnumerateFiles(_dir, "*.tmp").Concat(Directory.EnumerateDirectories(_dir, "*.extracting"))
        : Array.Empty<string>();

    private string VoskDir => Path.Combine(_dir, ModelStore.VoskModelDirectoryName);

    // ── Resolution ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ggml-tiny.en.bin", "ggml-tiny.en.bin")]
    [InlineData("GGML-BASE.EN.BIN", "ggml-base.en.bin")]
    [InlineData(" ggml-small.en.bin ", "ggml-small.en.bin")]
    public void ResolveWhisper_KnownName_PointsUnderTheDirectoryWithAUrl(string setting, string file)
    {
        var spec = ModelStore.ResolveWhisper(setting, _dir);

        Assert.NotNull(spec);
        Assert.Equal(Path.Combine(_dir, file), spec.Path);
        Assert.Equal(ModelStore.WhisperRepository + file, spec.DownloadUrl!.AbsoluteUri);
        Assert.True(spec.ApproxBytes > 1_000_000);
        Assert.StartsWith("whisper ", spec.Display);
        Assert.Equal(spec, _store.Whisper(setting));
    }

    [Fact]
    public void ResolveWhisper_RootedPath_IsUsedAsIs_WithNoUrl()
    {
        string path = Path.Combine(_dir, "custom", "ggml-medium.bin");
        var spec = ModelStore.ResolveWhisper(path, _dir);

        Assert.NotNull(spec);
        Assert.Equal(path, spec.Path);
        Assert.Null(spec.DownloadUrl);
        Assert.Equal("whisper ggml-medium.bin", spec.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("huge.en")]
    [InlineData("base.en")]                     // the short form of before 2026-09-16: refused, never mapped
    [InlineData("relative/ggml-base.en.bin")]
    [InlineData("C:\\models\\notes.txt")]
    public void ResolveWhisper_Garbage_IsNull(string setting)
    {
        Assert.Null(ModelStore.ResolveWhisper(setting, _dir));
    }

    [Fact]
    public void Silero_IsUnderTheDirectory()
    {
        var spec = _store.Silero();
        Assert.Equal(Path.Combine(_dir, ModelStore.SileroFileName), spec.Path);
        Assert.Equal(SileroUrl, spec.DownloadUrl!.AbsoluteUri);
        Assert.Equal("silero vad", spec.Display);
    }

    [Fact]
    public void Kokoro_IsUnderTheDirectory_AndIsOnnx()
    {
        var spec = _store.Kokoro();
        Assert.Equal(Path.Combine(_dir, "kokoro.onnx"), spec.Path);
        Assert.Equal(ModelStore.KokoroModelUrl, spec.DownloadUrl!.AbsoluteUri);
        Assert.Equal("kokoro.onnx", spec.Display);
        Assert.Equal(ModelFormat.Onnx, spec.Format);
        Assert.Equal(325_508_342, spec.ApproxBytes);
        Assert.Equal("326 MB", ModelStore.SizeLabel(spec.ApproxBytes));
        // The ggml specs are untouched by the new component's default.
        Assert.Equal(ModelFormat.Ggml, _store.Silero().Format);
        Assert.Equal(ModelFormat.Ggml, _store.Whisper("ggml-base.en.bin")!.Format);
    }

    [Fact]
    public void LooksLikeOnnx_ReadsTheFirstProtobufTags()
    {
        string path = FakeModelFiles.WriteOnnx(Path.Combine(_dir, "a.onnx"));
        Assert.True(ModelStore.LooksLikeOnnx(path));
        Assert.True(ModelStore.LooksLike(path, ModelFormat.Onnx));
        Assert.False(ModelStore.LooksLike(path, ModelFormat.Ggml));

        File.WriteAllText(Path.Combine(_dir, "b.onnx"), "<html>login please</html>");
        Assert.False(ModelStore.LooksLikeOnnx(Path.Combine(_dir, "b.onnx")));
        File.WriteAllBytes(Path.Combine(_dir, "c.onnx"), new byte[] { 0x08, 0x09 });
        Assert.False(ModelStore.LooksLikeOnnx(Path.Combine(_dir, "c.onnx")));
        Assert.False(ModelStore.LooksLikeOnnx(Path.Combine(_dir, "missing.onnx")));

        Assert.True(ModelStore.LooksLike(FakeModelFiles.Write(Path.Combine(_dir, "g.bin")), ModelFormat.Ggml));
        Assert.Equal("an ONNX", ModelStore.FormatName(ModelFormat.Onnx));
        Assert.Equal("a ggml", ModelStore.FormatName(ModelFormat.Ggml));
    }

    [Fact]
    public async Task Ensure_Kokoro_RefusesABodyThatIsNotOnnx()
    {
        _http.Map(ModelStore.KokoroModelUrl, HttpStatusCode.OK, "<html>login please</html>", "text/html");
        var refused = await _store.EnsureAsync(_store.Kokoro(), null, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("not an ONNX model", refused.Detail);
        Assert.False(File.Exists(refused.Path));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Ensure_Kokoro_DownloadsAnOnnxBody_AndFindsItPresentNextTime()
    {
        var body = FakeModelFiles.OnnxBytes(512);
        _http.Map(ModelStore.KokoroModelUrl, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, "application/octet-stream")));
        var result = await _store.EnsureAsync(_store.Kokoro(), null, CancellationToken.None);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(result.Path));
        Assert.Empty(TempFiles());

        // Present and valid next time: no request (a fresh store over an unmapped stub would refuse the connection).
        var fresh = new ModelStore(_dir, new HttpClient(new StubHttpMessageHandler()));
        var again = await fresh.EnsureAsync(fresh.Kokoro(), null, CancellationToken.None);
        Assert.True(again.Ok);
        Assert.Equal("present", again.Detail);
    }

    [Theory]
    [InlineData(147_964_211, "148 MB")]
    [InlineData(885_098, "1 MB")]
    [InlineData(400_000, "400 KB")]
    [InlineData(900, "900 B")]
    public void SizeLabel_IsInvariantAndRounded(long bytes, string expected)
    {
        Assert.Equal(expected, ModelStore.SizeLabel(bytes));
    }

    [Fact]
    public void Urls_ArePinned()
    {
        Assert.Equal("https://huggingface.co/sandrohanea/whisper.net/resolve/v4/classic/", ModelStore.WhisperRepository);
        Assert.Equal("https://huggingface.co/sandrohanea/whisper.net/resolve/v4/vad/ggml-silero-v6.2.0.bin", ModelStore.SileroUrl);
        Assert.Equal(new[] { "ggml-tiny.en.bin", "ggml-base.en.bin", "ggml-small.en.bin" }, ModelStore.WhisperModelNames);
        Assert.Equal("https://alphacephei.com/vosk/models/", ModelStore.VoskRepository);
        Assert.Equal("https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip", ModelStore.VoskModelUrl);
        Assert.Equal("vosk-model-small-en-us-0.15", ModelStore.VoskModelDirectoryName);
        Assert.Equal(new[] { "vosk-model-small-en-us-0.15", "vosk-model-en-us-0.22-lgraph", "vosk-model-small-en-in-0.4" }, ModelStore.VoskModelNames);
        Assert.Equal(ModelStore.VoskModelDirectoryName, ModelStore.VoskModelNames[0]);
        Assert.Equal("https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx", ModelStore.KokoroModelUrl);
        Assert.Equal("kokoro.onnx", ModelStore.KokoroFileName);
        Assert.Equal(new[] { "am/final.mdl", "conf/model.conf", "graph/HCLr.fst", "graph/Gr.fst", "ivector/final.ie" }, ModelStore.VoskRequiredFiles);
    }

    [Fact]
    public void Vosk_IsUnderTheDirectory()
    {
        var spec = _store.Vosk();
        Assert.Equal(VoskDir, spec.Path);
        Assert.Equal(ModelStore.VoskModelUrl, spec.ArchiveUrl.AbsoluteUri);
        Assert.Equal("vosk model", spec.Display);
        Assert.Equal("41 MB", ModelStore.SizeLabel(spec.ApproxBytes));
        Assert.Same(ModelStore.VoskRequiredFiles, spec.RequiredFiles);
    }

    [Fact]
    public void ResolveVosk_KnowsTheThreeNames_AnyCase_AndNothingElse()
    {
        var lgraph = ModelStore.ResolveVosk("  VOSK-MODEL-EN-US-0.22-LGRAPH ", _dir);
        Assert.NotNull(lgraph);
        Assert.Equal(Path.Combine(_dir, "vosk-model-en-us-0.22-lgraph"), lgraph.Path);
        Assert.Equal("https://alphacephei.com/vosk/models/vosk-model-en-us-0.22-lgraph.zip", lgraph.ArchiveUrl.AbsoluteUri);
        Assert.Equal("vosk model", lgraph.Display);
        Assert.Equal("131 MB", ModelStore.SizeLabel(lgraph.ApproxBytes));
        Assert.Same(ModelStore.VoskRequiredFiles, lgraph.RequiredFiles);

        var indian = ModelStore.ResolveVosk("vosk-model-small-en-in-0.4", _dir);
        Assert.NotNull(indian);
        Assert.Equal(Path.Combine(_dir, "vosk-model-small-en-in-0.4"), indian.Path);
        Assert.Equal("https://alphacephei.com/vosk/models/vosk-model-small-en-in-0.4.zip", indian.ArchiveUrl.AbsoluteUri);
        Assert.Equal("38 MB", ModelStore.SizeLabel(indian.ApproxBytes));

        Assert.Equal(_store.Vosk(), _store.Vosk("vosk-model-small-en-us-0.15"));
        Assert.Equal(_store.Vosk(), _store.Vosk(ModelStore.VoskModelDirectoryName));

        // No path form and none of the static-graph models: the picker is the one way.
        Assert.Null(ModelStore.ResolveVosk("", _dir));
        Assert.Null(ModelStore.ResolveVosk(null!, _dir));
        Assert.Null(ModelStore.ResolveVosk("vosk-model-en-us-0.22", _dir));
        Assert.Null(ModelStore.ResolveVosk(@"C:\models\vosk-model-small-en-us-0.15", _dir));
        Assert.Null(_store.Vosk("small"));
    }

    [Fact]
    public void LooksLikeZip_ChecksTheMagic()
    {
        Directory.CreateDirectory(_dir);
        string good = Path.Combine(_dir, "good.zip");
        File.WriteAllBytes(good, FakeModelFiles.VoskZip());
        string bad = Path.Combine(_dir, "bad.zip");
        File.WriteAllText(bad, "<html>login please</html>");

        Assert.True(ModelStore.LooksLikeZip(good));
        Assert.False(ModelStore.LooksLikeZip(bad));
        Assert.False(ModelStore.LooksLikeZip(FakeModelFiles.Write(Path.Combine(_dir, "model.bin"))));
        Assert.False(ModelStore.LooksLikeZip(Path.Combine(_dir, "missing.zip")));
    }

    [Fact]
    public void IsCompleteModelDirectory_NeedsEveryRequiredFile()
    {
        Assert.False(ModelStore.IsCompleteModelDirectory(VoskDir, ModelStore.VoskRequiredFiles));

        FakeModelFiles.WriteVoskModel(VoskDir, "graph/Gr.fst");
        Assert.False(ModelStore.IsCompleteModelDirectory(VoskDir, ModelStore.VoskRequiredFiles));

        FakeModelFiles.WriteVoskModel(VoskDir);
        Assert.True(ModelStore.IsCompleteModelDirectory(VoskDir, ModelStore.VoskRequiredFiles));
    }

    [Fact]
    public void LooksLikeGgml_ChecksTheMagic()
    {
        string good = FakeModelFiles.Write(Path.Combine(_dir, "good.bin"));
        string bad = Path.Combine(_dir, "bad.bin");
        File.WriteAllText(bad, "not a model at all");
        string empty = Path.Combine(_dir, "empty.bin");
        File.WriteAllBytes(empty, Array.Empty<byte>());

        Assert.True(ModelStore.LooksLikeGgml(good));
        Assert.False(ModelStore.LooksLikeGgml(bad));
        Assert.False(ModelStore.LooksLikeGgml(empty));
        Assert.False(ModelStore.LooksLikeGgml(Path.Combine(_dir, "missing.bin")));
    }

    // ── EnsureAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Ensure_Downloads_ThroughATempFile_AndLeavesNoTemp()
    {
        var body = FakeModelFiles.GgmlBytes(200_000);
        ServeSilero(body);
        var progress = new List<(long Received, long? Total)>();

        var result = await _store.EnsureAsync(_store.Silero(), new SyncProgress(progress), CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(Path.Combine(_dir, ModelStore.SileroFileName), result.Path);
        Assert.Equal(body, File.ReadAllBytes(result.Path));
        Assert.Empty(TempFiles());
        Assert.Contains("downloaded", result.Detail);
        Assert.Single(_http.Requests);
        Assert.Equal(body.Length, progress[^1].Total);
        Assert.Equal(body.Length, progress[^1].Received);
        Assert.Equal(0, progress[0].Received);
    }

    [Fact]
    public async Task Ensure_PresentValidFile_CostsNoHttp()
    {
        FakeModelFiles.Write(_store.Silero().Path);

        var result = await _store.EnsureAsync(_store.Silero(), null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("present", result.Detail);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Ensure_ZeroLengthFile_IsReplaced()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(_store.Silero().Path, Array.Empty<byte>());
        var body = FakeModelFiles.GgmlBytes();
        ServeSilero(body);

        var result = await _store.EnsureAsync(_store.Silero(), null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(body, File.ReadAllBytes(result.Path));
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Ensure_NotFound_Fails_AndWritesNothing()
    {
        _http.Map(SileroUrl, HttpStatusCode.NotFound, "gone");

        var result = await _store.EnsureAsync(_store.Silero(), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("HTTP 404", result.Detail);
        Assert.False(File.Exists(result.Path));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Ensure_NonGgmlBody_Fails_AndWritesNothing()
    {
        _http.Map(SileroUrl, HttpStatusCode.OK, "<html>login please</html>", "text/html");

        var result = await _store.EnsureAsync(_store.Silero(), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not a ggml model", result.Detail);
        Assert.False(File.Exists(result.Path));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Ensure_RefusedConnection_IsAFailedResult()
    {
        var result = await _store.EnsureAsync(_store.Silero(), null, CancellationToken.None);   // nothing mapped: refused

        Assert.False(result.Ok);
        Assert.Contains("HttpRequestException", result.Detail);
        Assert.False(File.Exists(result.Path));
    }

    [Fact]
    public async Task Ensure_NoUrlAndMissing_IsNotFound_WithNoHttp()
    {
        var spec = ModelStore.ResolveWhisper(Path.Combine(_dir, "ggml-custom.bin"), _dir)!;

        var result = await _store.EnsureAsync(spec, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("not found", result.Detail);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Ensure_Cancelled_ThrowsAndLeavesNoTemp()
    {
        using var cts = new CancellationTokenSource();
        _http.Map(SileroUrl, async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.EnsureAsync(_store.Silero(), null, cts.Token));

        Assert.False(File.Exists(_store.Silero().Path));
        Assert.Empty(TempFiles());
    }

    // ── EnsureDirectoryAsync ────────────────────────────────────────────────

    [Fact]
    public async Task EnsureDirectory_Downloads_Unpacks_AndLeavesNothingBehind()
    {
        var body = FakeModelFiles.VoskZip();
        ServeVosk(body);
        var progress = new List<(long Received, long? Total)>();
        int unpacking = 0;

        var result = await _store.EnsureDirectoryAsync(_store.Vosk(), new SyncProgress(progress), () => unpacking++, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(VoskDir, result.Path);
        Assert.True(ModelStore.IsCompleteModelDirectory(VoskDir, ModelStore.VoskRequiredFiles));
        Assert.Equal("fake am/final.mdl", File.ReadAllText(Path.Combine(VoskDir, "am", "final.mdl")));
        Assert.Empty(Leftovers());
        Assert.Contains("downloaded", result.Detail);
        Assert.Single(_http.Requests);
        Assert.Equal(1, unpacking);
        Assert.Equal(body.Length, progress[^1].Total);
        Assert.Equal(body.Length, progress[^1].Received);
        Assert.Equal(0, progress[0].Received);
    }

    [Fact]
    public async Task EnsureDirectory_FlatArchive_IsAccepted()
    {
        ServeVosk(FakeModelFiles.VoskZip(topFolder: ""));

        var result = await _store.EnsureDirectoryAsync(_store.Vosk(), null, null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.True(ModelStore.IsCompleteModelDirectory(VoskDir, ModelStore.VoskRequiredFiles));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task EnsureDirectory_PresentCompleteDirectory_CostsNoHttp()
    {
        FakeModelFiles.WriteVoskModel(VoskDir);

        var result = await _store.EnsureDirectoryAsync(_store.Vosk(), null, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("present", result.Detail);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task EnsureDirectory_IncompleteDirectory_IsReplaced()
    {
        FakeModelFiles.WriteVoskModel(VoskDir, "graph/Gr.fst");
        File.WriteAllText(Path.Combine(VoskDir, "stale.txt"), "left over");
        ServeVosk(FakeModelFiles.VoskZip());

        var result = await _store.EnsureDirectoryAsync(_store.Vosk(), null, null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.True(ModelStore.IsCompleteModelDirectory(VoskDir, ModelStore.VoskRequiredFiles));
        Assert.False(File.Exists(Path.Combine(VoskDir, "stale.txt")));
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task EnsureDirectory_NonZipBody_Fails_AndWritesNothing()
    {
        _http.Map(ModelStore.VoskModelUrl, HttpStatusCode.OK, "<html>login please</html>", "text/html");

        var result = await _store.EnsureDirectoryAsync(_store.Vosk(), null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not a zip archive", result.Detail);
        Assert.False(Directory.Exists(VoskDir));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task EnsureDirectory_ArchiveWithoutTheModel_Fails_AndLeavesNothing()
    {
        ServeVosk(FakeModelFiles.VoskZip(omit: "ivector/final.ie"));
        int unpacking = 0;

        var result = await _store.EnsureDirectoryAsync(_store.Vosk(), null, () => unpacking++, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("does not contain a vosk model", result.Detail);
        Assert.Contains("ivector/final.ie", result.Detail);
        Assert.False(Directory.Exists(VoskDir));
        Assert.Empty(Leftovers());
        Assert.Equal(1, unpacking);
    }

    [Fact]
    public async Task EnsureDirectory_NotFound_Fails_AndWritesNothing()
    {
        _http.Map(ModelStore.VoskModelUrl, HttpStatusCode.NotFound, "gone");

        var result = await _store.EnsureDirectoryAsync(_store.Vosk(), null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("HTTP 404", result.Detail);
        Assert.False(Directory.Exists(VoskDir));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task EnsureDirectory_RefusedConnection_IsAFailedResult()
    {
        var result = await _store.EnsureDirectoryAsync(_store.Vosk(), null, null, CancellationToken.None);   // nothing mapped: refused

        Assert.False(result.Ok);
        Assert.Contains("HttpRequestException", result.Detail);
        Assert.False(Directory.Exists(VoskDir));
    }

    [Fact]
    public async Task EnsureDirectory_Cancelled_ThrowsAndLeavesNothing()
    {
        using var cts = new CancellationTokenSource();
        _http.Map(ModelStore.VoskModelUrl, async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.EnsureDirectoryAsync(_store.Vosk(), null, null, cts.Token));

        Assert.False(Directory.Exists(VoskDir));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Ctor_Guards()
    {
        Assert.Throws<ArgumentException>(() => new ModelStore(" ", new HttpClient(_http)));
        Assert.Throws<ArgumentNullException>(() => new ModelStore(_dir, null!));
    }

    private sealed class SyncProgress : IProgress<(long Received, long? Total)>
    {
        private readonly List<(long Received, long? Total)> _seen;

        public SyncProgress(List<(long Received, long? Total)> seen) => _seen = seen;

        public void Report((long Received, long? Total) value) => _seen.Add(value);
    }
}
