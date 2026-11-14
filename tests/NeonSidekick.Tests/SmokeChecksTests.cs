using NeonSidekick.App;
using NeonSidekick.Speech;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class SmokeChecksTests
{
    /// <summary><c>settings:roundtrip</c> (2026-11-13, settings transfer): a scratch home exported and imported, the resets made; touches neither the real home nor Downloads.</summary>
    [Fact]
    public void ProbeSettingsRoundtrip_Passes()
    {
        var check = SmokeChecks.ProbeSettingsRoundtrip();

        Assert.Equal("settings:roundtrip", check.Name);
        Assert.True(check.Passed, check.Detail);
    }

    [WindowsFact]
    public void ProbeWinMm_Passes_WithOrWithoutADevice()
    {
        var check = SmokeChecks.ProbeWinMm();

        Assert.Equal("audio:winmm", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.Contains(AudioDevice.OutputCount == 0 ? "no output device" : "device", check.Detail);
    }

    /// <summary><c>pdf:cmap</c> (2026-10-14, the PDF plan's gap 87): page 2 of the probe's PDF read alone, its Japanese through the packed CMaps; skipped, passing, with no browser.</summary>
    [WindowsFact]
    public void ProbePdfCMap_ReadsPageTwo_ThroughThePackedCMaps()
    {
        var check = SmokeChecks.ProbePdfCMap();

        Assert.Equal("pdf:cmap", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.True(check.Detail.StartsWith("skipped", StringComparison.Ordinal) || check.Detail.Contains("read page 2 of 2, Japanese through", StringComparison.Ordinal), check.Detail);
    }

    /// <summary><c>pdf:edit</c> (2026-10-14, the PDF plan's gap 87): page 1 taken out and turned with pdf-lib, read back wider than tall; skipped, passing, with no browser.</summary>
    [WindowsFact]
    public void ProbePdfEdit_TakesAPageOut_AndTurnsIt()
    {
        var check = SmokeChecks.ProbePdfEdit();

        Assert.Equal("pdf:edit", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.True(check.Detail.StartsWith("skipped", StringComparison.Ordinal) || check.Detail.Contains("took page 1 of 2 out with pdf-lib, turned it 90° and read it back", StringComparison.Ordinal), check.Detail);
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

    /// <summary><c>image:edit</c> (2026-10-04): crop, a quarter turn, grey, hue and a border into five encodes, each the right size and type.</summary>
    [WindowsFact]
    public void ProbeImageEdit_EditsIntoEveryEncoder()
    {
        var check = SmokeChecks.ProbeImageEdit();

        Assert.Equal("image:edit", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("png, png/16, jpeg/444, gif, tiff at 8x10; middle #", check.Detail);
        Assert.Contains("; writes png, jpeg, gif, bmp, tiff", check.Detail);
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
        Assert.EndsWith("1 FTS5 hit, 1 OR hit, a skill's usage and a reflection recorded, 2 messages restored, 1 purged, skills.db written and read with its history", check.Detail);   // the schema-3 legs since 2026-09-19; the migration legs went 2026-09-24
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

    /// <summary>The Claude Code server's relay (2026-09-30): the app's MCP listener, the relay over a pipe pair as the CLI's stdio, a call carrying its tool-use id.</summary>
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
        Assert.EndsWith("); init, found from a nested path, 1 untracked, staged, branch created and switched, committed ×2, diff +1 −0 unstaged / staged / by commit, 2 commits logged, blame 2 lines by 2 commits, stashed and popped, 1 path discarded, ahead 1 of origin/main, 1 tracked file, merge base found, an untracked folder walked, branch deleted", check.Detail);   // the code audit's two reads since 2026-10-18; the Ralph loop's changed paths later that day
        Assert.Contains("git2-5853918.dll", SmokeChecks.WindowsNativeLibraries);
        Assert.Contains("libgit2-5853918.dylib", SmokeChecks.MacNativeLibraries);
    }

    /// <summary>The file checkpoints' round trip (2026-10-28): a journal, a commit into the profile's store, a restore, the ref spent (the JIT half).</summary>
    [Fact]
    public void ProbeCheckpoints_RoundTripsATurnsFiles()
    {
        var check = SmokeChecks.ProbeCheckpoints();

        Assert.Equal("checkpoints:roundtrip", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.EndsWith("committed, found and replayed: 3 restored, 0 skipped, 0 not saved, 0 failed, ref spent", check.Detail);
    }

    [Fact]
    public void ProbeSql_ParsesWithTheGate_AndReachesTheNativeSni()
    {
        var check = SmokeChecks.ProbeSql();

        Assert.Equal("sql:parse-and-sni", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("ScriptDom gate ok; SqlClient ", check.Detail);
        Assert.EndsWith(OperatingSystem.IsWindows() ? " through the native SNI" : " through the managed SNI", check.Detail);
        Assert.Contains("Microsoft.Data.SqlClient.SNI.dll", SmokeChecks.WindowsNativeLibraries);
    }

    /// <summary><c>oracle:driver</c> (2026-09-30): the gate, the fetch-size setter the trimmed driver broke, and ODP.NET's connect path answering a port nobody serves.</summary>
    [Fact]
    public void ProbeOracle_ChecksTheGate_AndReachesTheManagedDriver()
    {
        var check = SmokeChecks.ProbeOracle();

        Assert.Equal("oracle:driver", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("gate ok; ODP.NET ", check.Detail);
        Assert.Contains(" answered ORA-", check.Detail);
    }

    /// <summary><c>unc:impersonation</c> (later on 2026-09-30): a netonly token on every parallel worker, and the share sandbox reading a temp folder under it.</summary>
    [Fact]
    public void ProbeUnc_SeesTheTokenOnEveryWorker_AndTheShareSandboxReads()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // a netonly token is Windows'
        }

        var check = SmokeChecks.ProbeUnc();

        Assert.Equal("unc:impersonation", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("netonly token on ", check.Detail);
        Assert.EndsWith(" worker threads; the share sandbox listed 8 and searched 8", check.Detail);
    }

    /// <summary><c>eventlog:wevtapi</c>, <c>eventlog:rpc-login</c> and <c>eventlog:evtx</c> (2026-10-15): this PC's own logs, read-only, the session path and an export read back.</summary>
    [Fact]
    public void ProbeEventLog_ReadsThisPcsLogs_ALoopbackSession_AndAnExport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // the event logs are Windows'
        }

        var wevtapi = SmokeChecks.ProbeEventLog();
        var rpc = SmokeChecks.ProbeEventLogRpcLogin();
        var evtx = SmokeChecks.ProbeEventLogEvtx();

        Assert.Equal("eventlog:wevtapi", wevtapi.Name);
        Assert.True(wevtapi.Passed, wevtapi.Detail);
        Assert.EndsWith("15001 and 15007 worded", wevtapi.Detail);
        Assert.Equal("eventlog:rpc-login", rpc.Name);
        Assert.True(rpc.Passed, rpc.Detail);
        Assert.Equal("eventlog:evtx", evtx.Name);
        Assert.True(evtx.Passed, evtx.Detail);
    }

    /// <summary><c>mysql:driver</c> (later on 2026-09-30): the gate, a bound parameter, and MySqlConnector's connect path answering a port nobody serves.</summary>
    [Fact]
    public void ProbeMySql_ChecksTheGate_AndReachesTheManagedDriver()
    {
        var check = SmokeChecks.ProbeMySql();

        Assert.Equal("mysql:driver", check.Name);
        Assert.True(check.Passed, check.Detail);
        Assert.StartsWith("gate ok; MySqlConnector ", check.Detail);
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
        Assert.Contains("onnxruntime.dll", SmokeChecks.WindowsNativeLibraries);
        Assert.Contains("onnxruntime_providers_shared.dll", SmokeChecks.WindowsNativeLibraries);
        Assert.Contains("libvosk.dll", SmokeChecks.WindowsNativeLibraries);
        Assert.Contains("libstdc++-6.dll", SmokeChecks.WindowsNativeLibraries);
        Assert.Contains("libgcc_s_seh-1.dll", SmokeChecks.WindowsNativeLibraries);
        Assert.Contains("libwinpthread-1.dll", SmokeChecks.WindowsNativeLibraries);
        Assert.Contains("duckdb.dll", SmokeChecks.WindowsNativeLibraries);   // the DuckDB tools, 2026-10-25
        Assert.Equal(OperatingSystem.IsWindows() ? SmokeChecks.WindowsNativeLibraries : SmokeChecks.MacNativeLibraries, SmokeChecks.RequiredNativeLibraries);
    }

    /// <summary>The macOS list (2026-10-06): the dylibs the packages ship for osx-arm64, at the depths the publish puts them, and none of Windows' own.</summary>
    [Fact]
    public void MacNativeLibraries_AreTheArm64Dylibs()
    {
        Assert.Equal(
            new[]
            {
                "libonnxruntime.dylib",
                Path.Combine("runtimes", "macos-arm64", "libggml-base-whisper.dylib"),
                Path.Combine("runtimes", "macos-arm64", "libggml-cpu-whisper.dylib"),
                Path.Combine("runtimes", "macos-arm64", "libggml-blas-whisper.dylib"),
                Path.Combine("runtimes", "macos-arm64", "libggml-metal-whisper.dylib"),
                Path.Combine("runtimes", "macos-arm64", "libggml-whisper.dylib"),
                Path.Combine("runtimes", "macos-arm64", "libwhisper.dylib"),
                "libvosk.dylib",
                "libgit2-5853918.dylib",
                "libe_sqlite3.dylib",
                "libduckdb.dylib",
            },
            SmokeChecks.MacNativeLibraries);
    }

    /// <summary>
    /// The update probe loads <see cref="SmokeChecks.MacNativeLibraries"/> one by one by path (2026-11-06, the Mac pass of self-update), and Whisper.net's
    /// dylibs find each other only as <c>@rpath/…</c> names already loaded: their rpaths are Whisper.net's CI folders. So every <c>@rpath</c> dependency
    /// of a listed dylib must be the install name of one listed before it. Read from the dylibs this build copied, by their own load commands, so a
    /// Whisper.net update that adds a backend fails here rather than in <c>build.ps1 -Package</c>'s probe. Whisper first failed every Mac package.
    /// </summary>
    [MacFact]
    public void MacNativeLibraries_LoadInOrder_EachAfterWhatItNeeds()
    {
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        int read = 0;
        foreach (string library in SmokeChecks.MacNativeLibraries)
        {
            string path = Path.Combine(AppContext.BaseDirectory, library);
            if (!File.Exists(path))
            {
                continue;   // the JIT's output keeps some under runtimes/osx-arm64/native, as the smoke's notes say; Whisper.net's are here
            }

            var (id, needs) = MachODylib.Read(path);
            foreach (string need in needs.Where(name => name.StartsWith("@rpath/", StringComparison.Ordinal) && name != id))
            {
                Assert.True(loaded.Contains(need), $"{library} needs {need}, which no library listed before it is");
            }

            if (id is not null)
            {
                loaded.Add(id);
            }

            read++;
        }

        Assert.True(read >= 6, $"only {read} of the listed dylibs are beside the tests");
    }

    /// <summary>
    /// A Mach-O 64-bit dylib's install name (LC_ID_DYLIB) and the dylibs it loads (LC_LOAD_DYLIB, weak, re-export): the test's own reader, what <c>otool -D</c>/<c>-L</c> print.
    /// A universal file (libvosk's) is read at its arm64 slice.
    /// </summary>
    private static class MachODylib
    {
        private const uint Magic64 = 0xFEEDFACF;
        private const uint FatMagic = 0xCAFEBABE;
        private const uint CpuArm64 = 0x0100000C;
        private const uint IdDylib = 0xD;
        private const uint LoadDylib = 0xC;
        private const uint LoadWeakDylib = 0x80000018;
        private const uint ReexportDylib = 0x8000001F;

        public static (string? Id, List<string> Needs) Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int start = 0;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes) == FatMagic)
            {
                uint slices = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4));
                start = -1;
                for (int slice = 0; slice < slices; slice++)
                {
                    var entry = bytes.AsSpan(8 + slice * 20);
                    if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(entry) == CpuArm64)
                    {
                        start = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                    }
                }

                Assert.True(start > 0, path + " has no arm64 slice");
            }

            Assert.Equal(Magic64, BitConverter.ToUInt32(bytes, start));
            uint commands = BitConverter.ToUInt32(bytes, start + 16);
            int at = start + 32;
            string? id = null;
            var needs = new List<string>();
            for (uint i = 0; i < commands; i++)
            {
                uint command = BitConverter.ToUInt32(bytes, at);
                int size = (int)BitConverter.ToUInt32(bytes, at + 4);
                if (command is IdDylib or LoadDylib or LoadWeakDylib or ReexportDylib)
                {
                    int nameAt = at + (int)BitConverter.ToUInt32(bytes, at + 8);
                    int end = Array.IndexOf(bytes, (byte)0, nameAt, at + size - nameAt);
                    string name = System.Text.Encoding.UTF8.GetString(bytes, nameAt, (end < 0 ? at + size : end) - nameAt);
                    if (command == IdDylib)
                    {
                        id = name;
                    }
                    else
                    {
                        needs.Add(name);
                    }
                }

                at += size;
            }

            return (id, needs);
        }
    }

    [Fact]
    public void RequiredContentFiles_NameThisOsEspeak()
    {
        Assert.Equal(new[] { Path.Combine("voices", "af_heart.npy"), Path.Combine("espeak", KokoroInProcessSynthesizer.EspeakExecutable), Path.Combine("espeak", "espeak-ng-data", "phondata") }, SmokeChecks.RequiredContentFiles);
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows() && arch == System.Runtime.InteropServices.Architecture.X64)
        {
            Assert.Equal("espeak-ng-win-amd64.dll", KokoroInProcessSynthesizer.EspeakExecutable);
        }
        else if (OperatingSystem.IsMacOS() && arch == System.Runtime.InteropServices.Architecture.Arm64)
        {
            Assert.Equal("espeak-ng-macos-arm64.dll", KokoroInProcessSynthesizer.EspeakExecutable);
        }
    }

    [WindowsFact]
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
