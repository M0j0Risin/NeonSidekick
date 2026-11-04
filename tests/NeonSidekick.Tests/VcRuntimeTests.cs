using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The Visual C++ runtime's message (2026-11-04): over a fake loader, so none of these needs a PC without it.</summary>
public sealed class VcRuntimeTests : IDisposable
{
    private static readonly Func<string, bool> AllLoad = _ => true;
    private static readonly Func<string, bool> NoneLoad = _ => false;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public VcRuntimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A native library may still hold a file; the temp folder takes it.
        }
    }

    [Fact]
    public void Missing_NothingMissing_IsNull()
    {
        Assert.Null(VcRuntime.Missing(AllLoad));
        Assert.False(VcRuntime.MainMissing(AllLoad));
    }

    [Fact]
    public void Missing_NamesTheOneThatWontLoad()
    {
        Assert.Equal("msvcp140.dll", VcRuntime.Missing(library => library != "msvcp140.dll"));
        Assert.Equal(VcRuntime.MainLibrary, VcRuntime.Missing(NoneLoad));
        Assert.True(VcRuntime.MainMissing(NoneLoad));
        Assert.False(VcRuntime.MainMissing(library => library != "vcomp140.dll"));   // the embedded model's one question
    }

    [UnixFact]
    public void OffWindows_NothingIsMissing()
    {
        Assert.Null(VcRuntime.Missing());
        Assert.False(VcRuntime.MainMissing());
    }

    [Fact]
    public void TheText_HasNoPeriodOrParenthesesOfItsOwn_AndEndsInTheLink()
    {
        string text = VcRuntimeText.Missing("vcruntime140.dll");

        Assert.Contains("vcruntime140.dll", text, StringComparison.Ordinal);
        Assert.EndsWith(VcRuntimeText.InstallerUrl, text, StringComparison.Ordinal);
        Assert.DoesNotContain("(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_KeepsTodaysDetail_UnlessADllIsMissing()
    {
        Assert.Equal("today's", VcRuntime.Explain("today's", AllLoad));
        Assert.Equal(VcRuntimeText.Missing(VcRuntime.MainLibrary), VcRuntime.Explain("today's", NoneLoad));
    }

    private string NotAModel()
    {
        string path = Path.Combine(_dir, "not-a-model.bin");
        File.WriteAllBytes(path, new byte[256]);
        return path;
    }

    [Fact]
    public void Whisper_AFailedLoad_NamesTheRuntime_OnlyWhenADllIsMissing()
    {
        using (var missing = new WhisperNetTranscriber(NotAModel(), vcLoads: NoneLoad))
        {
            Assert.False(missing.Load(out var detail));
            Assert.Equal(VcRuntimeText.Missing(VcRuntime.MainLibrary), detail);
        }

        using var present = new WhisperNetTranscriber(NotAModel(), vcLoads: AllLoad);
        Assert.False(present.Load(out var today));
        Assert.DoesNotContain("Visual C++", today, StringComparison.Ordinal);
    }

    [Fact]
    public void Silero_AFailedLoad_NamesTheRuntime_OnlyWhenADllIsMissing()
    {
        using (var missing = new SileroVad(NotAModel(), vcLoads: NoneLoad))
        {
            Assert.False(missing.Load(out var detail));
            Assert.Equal(VcRuntimeText.Missing(VcRuntime.MainLibrary), detail);
        }

        using var present = new SileroVad(NotAModel(), vcLoads: AllLoad);
        Assert.False(present.Load(out var today));
        Assert.DoesNotContain("Visual C++", today, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kokoro_AFailedLoad_NamesTheRuntime_OnlyWhenADllIsMissing()
    {
        string voices = KokoroInProcessSynthesizer.VoicesDirectory(_dir);
        Directory.CreateDirectory(voices);
        File.WriteAllBytes(Path.Combine(voices, "af_heart.npy"), [1, 2, 3]);
        string model = FakeModelFiles.WriteOnnx(Path.Combine(_dir, "kokoro.onnx"));

        using (var missing = new KokoroInProcessSynthesizer(model, _dir, NoneLoad))
        {
            var result = await missing.PrepareAsync(CancellationToken.None);
            Assert.False(result.Exists);
            Assert.Equal(VcRuntimeText.Missing(VcRuntime.MainLibrary), result.Detail);
        }

        using var present = new KokoroInProcessSynthesizer(model, _dir, AllLoad);
        var today = await present.PrepareAsync(CancellationToken.None);
        Assert.False(today.Exists);
        Assert.True(today.Detail.StartsWith("model load failed: ", StringComparison.Ordinal) || today.Detail == KokoroInProcessSynthesizer.MissingRuntimeDetail, today.Detail);
    }

    [Fact]
    public void AnEarlyExit_ADllNotFound_GetsTheText_OnlyWhenADllIsMissing()
    {
        var missing = LlamaServerHost.EarlyExit(VcRuntime.DllNotFoundExitCode, "", NoneLoad);
        Assert.Equal(EmbeddedLlmText.StartFailed(VcRuntimeText.Missing(VcRuntime.MainLibrary)), missing.Message);
        Assert.True(missing.RuntimeMissing);

        var other = LlamaServerHost.EarlyExit(VcRuntime.DllNotFoundExitCode, "", AllLoad);
        Assert.Equal(EmbeddedLlmText.StartFailed(EmbeddedLlmText.ExitedEarly(VcRuntime.DllNotFoundExitCode, "")), other.Message);
        Assert.False(other.RuntimeMissing);

        var otherCode = LlamaServerHost.EarlyExit(1, "bind failed", NoneLoad);
        Assert.Contains("code 1", otherCode.Message, StringComparison.Ordinal);
        Assert.False(otherCode.RuntimeMissing);
        Assert.True(otherCode.PortInUse);
    }
}
