using NeonSidekick.Audio;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The in-process engine's pure half — the voice files, the pinned details, the pieces handed to
/// the sink — and one gated end-to-end synthesis when kokoro.onnx and the voices are at hand.
/// </summary>
public class KokoroInProcessSynthesizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Voices(params string[] relative)
    {
        foreach (var name in relative)
        {
            string path = Path.Combine(KokoroInProcessSynthesizer.VoicesDirectory(_dir), name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' });
        }

        return _dir;
    }

    [Fact]
    public void ListVoiceFiles_IsTheNpyStems_Recursive_SortedOrdinally()
    {
        Voices("bm_george.npy", "af_heart.npy", "voices-zh/zf_001.npy", "README.md", "af_heart.txt");

        var files = KokoroInProcessSynthesizer.ListVoiceFiles(_dir);

        Assert.Equal(["af_heart", "bm_george", "zf_001"], files.Keys);
        Assert.Equal(Path.Combine(_dir, "voices", "af_heart.npy"), files["af_heart"]);
        Assert.Empty(KokoroInProcessSynthesizer.ListVoiceFiles(Path.Combine(_dir, "nowhere")));
    }

    [Fact]
    public void Constants_ArePinned()
    {
        Assert.Equal("voices", KokoroInProcessSynthesizer.VoicesFolder);
        Assert.Equal("espeak", KokoroInProcessSynthesizer.EspeakFolder);
        Assert.Equal(".npy", KokoroInProcessSynthesizer.VoiceExtension);
        Assert.Equal("unknown voice 'zz_nobody'", KokoroInProcessSynthesizer.UnknownVoiceDetail("zz_nobody"));
        Assert.Equal(PcmFormat.Kokoro, new KokoroInProcessSynthesizer(Path.Combine(_dir, "kokoro.onnx")).Format);
    }

    [Fact]
    public async Task ListVoices_ReadsTheFiles_AndNeverLoads()
    {
        Voices("af_heart.npy", "af_sky.npy");
        using var synth = new KokoroInProcessSynthesizer(Path.Combine(_dir, "missing.onnx"), _dir);

        var listed = await synth.ListVoicesAsync(CancellationToken.None);

        Assert.True(listed.Exists);
        Assert.Equal(["af_heart", "af_sky"], listed.Voices);
        Assert.Equal("2 voices", listed.Detail);
        Assert.False(synth.IsLoaded);
    }

    [Fact]
    public async Task Prepare_WithoutTheVoicesFolder_OrTheModel_IsAPinnedDetail_WithNothingNative()
    {
        using var noVoices = new KokoroInProcessSynthesizer(Path.Combine(_dir, "kokoro.onnx"), _dir);
        var result = await noVoices.PrepareAsync(CancellationToken.None);
        Assert.False(result.Exists);
        Assert.Equal(KokoroInProcessSynthesizer.MissingVoicesDetail, result.Detail);
        Assert.Equal(KokoroInProcessSynthesizer.MissingVoicesDetail, (await noVoices.ListVoicesAsync(CancellationToken.None)).Detail);

        Voices("af_heart.npy");
        using var noModel = new KokoroInProcessSynthesizer(Path.Combine(_dir, "kokoro.onnx"), _dir);
        result = await noModel.PrepareAsync(CancellationToken.None);
        Assert.False(result.Exists);
        Assert.Equal(KokoroInProcessSynthesizer.MissingModelDetail, result.Detail);
        Assert.False(noModel.IsLoaded);
    }

    [Fact]
    public async Task Synthesize_BeforePrepare_IsAFailedResult_AndBlankTextIsNothingToSay()
    {
        Voices("af_heart.npy");
        using var synth = new KokoroInProcessSynthesizer(Path.Combine(_dir, "kokoro.onnx"), _dir);
        int calls = 0;

        var blank = await synth.SynthesizeAsync("  ", "af_heart", 1.0, (_, _) => calls++, CancellationToken.None);
        Assert.True(blank.Ok);
        Assert.Equal(0, blank.PcmBytes);
        Assert.Equal("nothing to say", blank.Detail);

        var result = await synth.SynthesizeAsync("Hello.", "af_heart", 1.0, (_, _) => calls++, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(KokoroInProcessSynthesizer.NotLoadedDetail, result.Detail);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Prepare_WithAFileThatIsNotAModel_IsALoadFailure_NotAThrow()
    {
        // Real voices beside a real runtime, a fake model: ORT refuses the file and the detail says so.
        Voices("af_heart.npy");
        string model = FakeModelFiles.WriteOnnx(Path.Combine(_dir, "kokoro.onnx"));
        using var synth = new KokoroInProcessSynthesizer(model, _dir);

        var result = await synth.PrepareAsync(CancellationToken.None);

        Assert.False(result.Exists);
        Assert.True(result.Detail.StartsWith("model load failed: ") || result.Detail == KokoroInProcessSynthesizer.MissingRuntimeDetail, result.Detail);
        Assert.False(synth.IsLoaded);
        synth.Dispose();   // twice is fine
    }

    [KokoroModelFact]
    public async Task Gated_SpeaksASentence_AloneAndBlended_AsEvenRawPcm()
    {
        using var synth = new KokoroInProcessSynthesizer(VoiceTestModels.KokoroPath!);

        var prepared = await synth.PrepareAsync(CancellationToken.None);
        Assert.True(prepared.Exists, prepared.Detail);
        Assert.True(prepared.Voices.Count >= 50, prepared.Detail);
        Assert.Contains("af_heart", prepared.Voices);
        Assert.True(synth.IsLoaded);
        Assert.Equal(prepared.Voices, (await synth.ListVoicesAsync(CancellationToken.None)).Voices);
        Assert.Equal(prepared.Voices, (await synth.PrepareAsync(CancellationToken.None)).Voices);   // cached, no second load

        var pieces = new List<int>();
        byte[]? first = null;
        var result = await synth.SynthesizeAsync("Hello. I am Neon.", "af_heart", 1.0, (buffer, count) =>
        {
            first ??= buffer[..count];
            pieces.Add(count);
        }, CancellationToken.None);
        Assert.True(result.Ok, result.Detail);
        Assert.True(result.PcmBytes > 24000, result.Detail);   // more than half a second of 24 kHz mono
        Assert.Equal(0, result.PcmBytes % 2);
        Assert.Equal(result.PcmBytes, pieces.Sum());
        Assert.All(pieces, p => Assert.True(p > 0 && p <= 4800 && p % 2 == 0));
        Assert.False(first!.Length >= 4 && first[0] == (byte)'R' && first[1] == (byte)'I');   // raw PCM, no header

        var blended = await synth.SynthesizeAsync("A blended voice.", "af_heart(70)+af_bella(30)", 1.3, (_, _) => { }, CancellationToken.None);
        Assert.True(blended.Ok, blended.Detail);
        Assert.True(blended.PcmBytes > 0);

        var unknown = await synth.SynthesizeAsync("Nobody.", "af_heart(70)+zz_nobody(30)", 1.0, (_, _) => { }, CancellationToken.None);
        Assert.False(unknown.Ok);
        Assert.Equal(KokoroInProcessSynthesizer.UnknownVoiceDetail("zz_nobody"), unknown.Detail);

        synth.Dispose();
        synth.Dispose();
    }
}
