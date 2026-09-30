using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class SmokeChecksTests
{
    [Fact]
    public void ProbeWinMm_Passes_WithOrWithoutADevice()
    {
        var check = SmokeChecks.ProbeWinMm();

        Assert.Equal("audio:winmm", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.Contains(AudioDevice.OutputCount == 0 ? "no output device" : "device", check.Detail);
    }

    [Fact]
    public void ProbeWhisperVad_Passes_WithoutAModel()
    {
        var check = SmokeChecks.ProbeWhisperVad();

        Assert.Equal("whisper:vad-load", check.Name);
        Assert.True(check.Passed, check.Detail);
    }

    [Fact]
    public void ProbeImageResize_DownscalesABitmapToAPng()
    {
        var check = SmokeChecks.ProbeImageResize();

        Assert.Equal("image:resize", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("2048x2 image/png", check.Detail);
        Assert.EndsWith("thumbnail 48x1 #FF40C8", check.Detail);
    }

    [Fact]
    public void ProbeSplash_DecodesEveryEmbeddedPicture()
    {
        var check = SmokeChecks.ProbeSplash();

        Assert.Equal("splash:decode", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.Equal(SplashImages.Names.Count, check.Detail.Split("; ").Length);
        Assert.StartsWith("splash/", check.Detail);
    }

    [Fact]
    public void ProbeSessions_OpensAStoreAndSearchesIt()
    {
        var check = SmokeChecks.ProbeSessions();

        Assert.Equal("sessions:open", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("SQLite 3.", check.Detail);
        Assert.EndsWith("1 FTS5 hit, 1 OR hit, a skill's usage and a reflection recorded, 2 messages restored, 1 purged", check.Detail);   // the schema-3 legs since 2026-09-19; the migration legs went 2026-09-24
    }

    /// <summary>The MCP round trip (2026-09-20): an in-process server over a pipe, the real client, the app's adapter answering an echo.</summary>
    [Fact]
    public void ProbeMcp_RoundTripsAnEchoTool_ThroughTheAdapter()
    {
        var check = SmokeChecks.ProbeMcp();

        Assert.Equal("mcp:roundtrip", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.Equal("1 tool listed as smoke__echo (smoke 1.0); echo answered through McpTool", check.Detail);
    }

    /// <summary>The Claude CLI server's relay (2026-09-30): the app's MCP listener, the relay over a pipe pair as the CLI's stdio, a call carrying its tool-use id.</summary>
    [Fact]
    public void ProbeClaudeCliRelay_ListsAndAnswersThroughTheRelay()
    {
        var check = SmokeChecks.ProbeClaudeCliRelay();

        Assert.Equal("claude-cli:relay", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.Equal("1 tool listed through the relay; the call answered with its tool-use id", check.Detail);
    }

    /// <summary>The performance bar's network meters (2026-09-30): .NET's adapter counters read twice, the rates sane (the JIT half; the published exe is the AOT proof).</summary>
    [Fact]
    public void ProbePerfNetwork_ReadsTheAdapters()
    {
        var check = SmokeChecks.ProbePerfNetwork();

        Assert.Equal("perf:network", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.Matches("^(no adapter with a gateway|\\d+ adapters? with a gateway, fastest link )", check.Detail);
    }

    /// <summary>The git round trip (2026-09-20): every GitAccess operation the tools call over a temp repository (the JIT half; the published exe is the AOT proof).</summary>
    [Fact]
    public void ProbeGit_RoundTripsATempRepository()
    {
        var check = SmokeChecks.ProbeGit();

        Assert.Equal("git:roundtrip", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("libgit2 5853918 (", check.Detail);
        Assert.EndsWith("); init, found from a nested path, 1 untracked, staged, branch created and switched, committed ×2, diff +1 −0 unstaged / staged / by commit, 2 commits logged, blame 2 lines by 2 commits, stashed and popped, 1 path discarded, ahead 1 of origin/main, branch deleted", check.Detail);
        Assert.Contains("git2-5853918.dll", SmokeChecks.RequiredNativeLibraries);
    }

    [Fact]
    public void ProbeSql_ParsesWithTheGate_AndReachesTheNativeSni()
    {
        var check = SmokeChecks.ProbeSql();

        Assert.Equal("sql:parse-and-sni", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("ScriptDom gate ok; SqlClient ", check.Detail);
        Assert.EndsWith(" through the native SNI", check.Detail);
        Assert.Contains("Microsoft.Data.SqlClient.SNI.dll", SmokeChecks.RequiredNativeLibraries);
    }

    [Fact]
    public void ProbeCulture_SeesTheInvariantPin()
    {
        // The test assembly pins its cultures as Program.cs does (ModuleInit, 2026-09-23).
        var check = SmokeChecks.ProbeCulture();

        Assert.Equal("culture:invariant", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.Equal("current culture invariant; 1234.5 01/07/2009", check.Detail);
        Assert.True(CulturePin.Holds);
    }

    [Fact]
    public void ProbeTranscriptMarkdown_ParsesAndLaysOutAReply()
    {
        var check = SmokeChecks.ProbeTranscriptMarkdown();

        Assert.Equal("transcript:markdown", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("Markdig 1.3.2", check.Detail);
        Assert.EndsWith("7 rows at 40 cells", check.Detail);
    }

    [Fact]
    public void SolidBmp_IsAValidUncompressedBitmap()
    {
        byte[] bmp = SmokeChecks.SolidBmp(3, 2);

        Assert.Equal(54 + 12 * 2, bmp.Length);   // rows padded to four bytes
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
        Assert.Equal(3, BitConverter.ToInt32(bmp, 18));
        Assert.Equal(2, BitConverter.ToInt32(bmp, 22));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmokeChecks.SolidBmp(0, 1));
    }

    [Fact]
    public void ProbeVosk_BindsTheNativeLibrary()
    {
        var check = SmokeChecks.ProbeVosk();

        Assert.Equal("vosk:load", check.Name);
        Assert.True(check.Passed, check.Detail);
    }

    [Fact]
    public void RequiredNativeLibraries_IncludeVoskAndItsRuntime()
    {
        Assert.Contains("onnxruntime.dll", SmokeChecks.RequiredNativeLibraries);
        Assert.Contains("onnxruntime_providers_shared.dll", SmokeChecks.RequiredNativeLibraries);
        Assert.Equal(new[] { Path.Combine("voices", "af_heart.npy"), Path.Combine("espeak", "espeak-ng-win-amd64.dll"), Path.Combine("espeak", "espeak-ng-data", "phondata") }, SmokeChecks.RequiredContentFiles);
        Assert.Contains("libvosk.dll", SmokeChecks.RequiredNativeLibraries);
        Assert.Contains("libstdc++-6.dll", SmokeChecks.RequiredNativeLibraries);
        Assert.Contains("libgcc_s_seh-1.dll", SmokeChecks.RequiredNativeLibraries);
        Assert.Contains("libwinpthread-1.dll", SmokeChecks.RequiredNativeLibraries);
    }

    [Fact]
    public void ProbeWinMmIn_Passes_WithOrWithoutADevice()
    {
        var check = SmokeChecks.ProbeWinMmIn();

        Assert.Equal("audio:winmm-in", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.Contains(AudioInputDevice.InputCount == 0 ? "no input device" : "device", check.Detail);
    }

    [Fact]
    public void KokoroProbes_PassUnderTheJit_OverTheBuildOutputsContent()
    {
        // The Content items flow into the test output through the project reference, so the
        // voices and the phonemizer are exercised here too; the published exe is the AOT proof.
        var ort = SmokeChecks.ProbeOnnxRuntime();
        Assert.True(ort.Passed, ort.Detail);
        Assert.Equal("kokoro:ort", ort.Name);

        var voices = SmokeChecks.ProbeKokoroVoices(AppContext.BaseDirectory);
        Assert.True(voices.Passed, voices.Detail);
        Assert.Contains("mixed as af_mix", voices.Detail);

        var phonemes = SmokeChecks.ProbeKokoroPhonemizer();
        Assert.True(phonemes.Passed, phonemes.Detail);
        Assert.Contains("tokens", phonemes.Detail);

        var missing = SmokeChecks.ProbeKokoroVoices(Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "nowhere"));
        Assert.False(missing.Passed);
        Assert.Contains("no af_heart voice", missing.Detail);

        // Without the model the synthesis probe passes as not exercised; it never downloads.
        var noModel = SmokeChecks.ProbeKokoroSynthesis(AppContext.BaseDirectory, Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "nowhere"));
        Assert.True(noModel.Passed);
        Assert.Contains("inference not exercised", noModel.Detail);
        Assert.Contains("inference not exercised", SmokeChecks.ProbeKokoroSynthesis(AppContext.BaseDirectory, null).Detail);
    }
}
