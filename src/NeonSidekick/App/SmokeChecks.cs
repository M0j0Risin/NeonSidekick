using System.Buffers.Binary;
using Microsoft.Extensions.AI;
using NeonSidekick.Audio;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Speech;
using NeonSidekick.UI;
using Spectre.Console;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace NeonSidekick.App;

/// <summary>One result line from <see cref="SmokeChecks.Run"/>.</summary>
public sealed record SmokeCheck(string Name, bool Passed, string Detail);

/// <summary>
/// The <c>--smoke</c> checks: can the published binary actually reach its native dependencies
/// and did every third-party assembly survive NativeAOT? Runs against the <em>published</em> exe
/// from <c>build.ps1</c>, because the failures it catches (a native library the publish step did
/// not copy, a P/Invoke that NativeAOT marshals differently, an SDK that needs reflection) are
/// invisible to <c>dotnet build</c> and to the JIT test run. Every native dependency degrades
/// silently in the app proper — voice features just switch themselves off — so the gate has to
/// ask head-on.
///
/// <para>Touching Whisper.net here is also what pulls it through the AOT compiler at all: an
/// assembly no code references is trimmed to nothing and its warnings never appear. The OpenAI
/// SDK is reached through <see cref="OpenAICompatibleChatClient"/> since M1, so that check now
/// proves our own wrapper binds in the published binary.</para>
/// </summary>
public static partial class SmokeChecks
{
    /// <summary>
    /// Native libraries, relative to the binary's directory. Whisper.net's own loader probes
    /// <c>runtimes/win-x64/</c> (AVX build) and <c>runtimes/noavx/win-x64/</c>; the publish step
    /// keeps that layout. Vosk's <c>libvosk.dll</c> sits at the root with the three MinGW runtime
    /// libraries it links against; without any one of them it fails to load. ONNX Runtime's two
    /// (Kokoro in-process, 2026-09-16) land at the root from the Microsoft.ML.OnnxRuntime package, and
    /// libgit2 (the git tools, 2026-09-20) from LibGit2Sharp.NativeBinaries — the file is named after the
    /// libgit2 commit it was built from, so the const follows a package bump; SqlClient's SNI (the SQL tools,
    /// 2026-09-23) from Microsoft.Data.SqlClient.SNI.runtime.
    /// </summary>
    public static readonly string[] RequiredNativeLibraries =
    {
        "onnxruntime.dll",
        "onnxruntime_providers_shared.dll",
        Path.Combine("runtimes", "win-x64", "whisper.dll"),
        Path.Combine("runtimes", "win-x64", "ggml-whisper.dll"),
        Path.Combine("runtimes", "win-x64", "ggml-base-whisper.dll"),
        Path.Combine("runtimes", "win-x64", "ggml-cpu-whisper.dll"),
        Path.Combine("runtimes", "noavx", "win-x64", "whisper.dll"),
        "libvosk.dll",
        "libgcc_s_seh-1.dll",
        "libstdc++-6.dll",
        "libwinpthread-1.dll",
        Git.GitAccess.NativeLibraryFileName,
        Sql.SqlAccess.NativeLibraryFileName,
    };

    /// <summary>
    /// KokoroSharp's content, relative to the binary's directory: one voice, the espeak-ng
    /// executable and one of its data files. The csproj republishes the package's <c>content\</c>
    /// with an explicit <c>Link</c>; without it MSBuild flattens the files into the root and the
    /// engine throws a parameterless <c>DirectoryNotFoundException</c> — so their presence at the
    /// right depth is checked, not just their existence somewhere.
    /// </summary>
    public static readonly string[] RequiredContentFiles =
    {
        Path.Combine(KokoroInProcessSynthesizer.VoicesFolder, "af_heart" + KokoroInProcessSynthesizer.VoiceExtension),
        Path.Combine(KokoroInProcessSynthesizer.EspeakFolder, "espeak-ng-win-amd64.dll"),
        Path.Combine(KokoroInProcessSynthesizer.EspeakFolder, "espeak-ng-data", "phondata"),
    };

    /// <summary>Runs every check against <paramref name="nativeDirectory"/> (normally <c>AppContext.BaseDirectory</c>); <paramref name="modelsDirectory"/> is where a <c>kokoro.onnx</c> already downloaded lets <see cref="ProbeKokoroSynthesis"/> run for real.</summary>
    public static IReadOnlyList<SmokeCheck> Run(string nativeDirectory, string? modelsDirectory = null)
    {
        var results = new List<SmokeCheck>();

        foreach (var library in RequiredNativeLibraries)
        {
            var path = Path.Combine(nativeDirectory, library);
            bool exists = File.Exists(path);
            results.Add(new SmokeCheck(
                $"native:{library.Replace('\\', '/')}",
                exists,
                exists ? "present" : $"missing from {nativeDirectory}"));
        }

        foreach (var file in RequiredContentFiles)
        {
            var path = Path.Combine(nativeDirectory, file);
            bool exists = File.Exists(path);
            results.Add(new SmokeCheck(
                $"content:{file.Replace('\\', '/')}",
                exists,
                exists ? "present" : $"missing from {nativeDirectory}"));
        }

        results.Add(ProbeVosk());
        results.Add(ProbeWhisper());
        results.Add(ProbeWhisperVad());
        results.Add(ProbeOpenAiSdk());
        results.Add(ProbeWinMm());
        results.Add(ProbeWinMmIn());
        results.Add(ProbeConsoleInput());
        results.Add(ProbeImageResize());
        results.Add(ProbeViewerWindow());
        results.Add(ProbeViewerDrag());
        results.Add(ProbePrintSpooler());
        results.Add(ProbeSplash());
        results.Add(ProbeWebMarkdown());
        results.Add(ProbeTranscriptMarkdown());
        results.Add(ProbeOnnxRuntime());
        results.Add(ProbeSessions());
        results.Add(ProbeMcp());
        results.Add(ProbeGit());
        results.Add(ProbeSql());
        results.Add(OperatingSystem.IsWindows() ? ProbeCredentials() : new SmokeCheck("sql:credentials", true, "skipped: not Windows"));
        results.Add(ProbeCulture());
        results.Add(ProbeKokoroVoices(nativeDirectory));
        results.Add(ProbeKokoroPhonemizer());
        results.Add(ProbeKokoroSynthesis(nativeDirectory, modelsDirectory));
        results.Add(ProbeJobObject());
        results.Add(ProbePerfKernel32());
        results.Add(ProbePerfNvml());
        results.Add(ProbePerfPdhDxgi());
        results.Add(ProbeLlamaServer(modelsDirectory));
        return results;
    }

    /// <summary>
    /// <c>mcp:roundtrip</c> (2026-09-20): the MCP SDK's JSON path on the published binary — an in-process
    /// server over a pipe (<see cref="Mcp.McpPipeServer"/>, hand-written handlers), the real
    /// <c>McpClient</c> initialising and listing its one tool, the app's <see cref="Mcp.McpTool"/> wrapping it
    /// and answering an echo through <see cref="Assistant.InvokeToolAsync"/> — the whole path the screen
    /// takes with a real server, without a child process.
    /// </summary>
    public static SmokeCheck ProbeMcp()
    {
        const string name = "mcp:roundtrip";
        try
        {
            return ProbeMcpAsync(name).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<SmokeCheck> ProbeMcpAsync(string name)
    {
        await using var server = Mcp.McpPipeServer.Start([("echo", "Echoes the text back.")], "smoke");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(server.ClientTransport, new ModelContextProtocol.Client.McpClientOptions
        {
            ClientInfo = new ModelContextProtocol.Protocol.Implementation { Name = McpSession.ClientName, Version = "smoke" },
        }, null, budget.Token).ConfigureAwait(false);
        var listed = await client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, budget.Token).ConfigureAwait(false);
        if (listed.Count != 1)
        {
            return new SmokeCheck(name, false, $"{listed.Count} tools listed, expected 1");
        }

        var tool = new Mcp.McpTool("smoke", client, listed[0], Mcp.McpToolName.Prefixed("smoke", listed[0].Name));
        var call = new FunctionCallContent("smoke1", tool.Name, new Dictionary<string, object?> { ["text"] = "ping" });
        var (text, _) = await Assistant.InvokeToolAsync([tool], call, budget.Token).ConfigureAwait(false);
        string expected = Mcp.McpPipeServer.EchoPrefix + "ping";
        return text == expected
            ? new SmokeCheck(name, true, $"{listed.Count} tool listed as {tool.Name} ({client.ServerInfo.Name} {client.ServerInfo.Version}); echo answered through McpTool")
            : new SmokeCheck(name, false, $"echo answered \"{text}\", expected \"{expected}\"");
    }

    /// <summary>
    /// The one check that runs ONNX inference through the compiled binary: a sentence synthesised
    /// by the real engine when <c>kokoro.onnx</c> is already in <paramref name="modelsDirectory"/>
    /// (a run of the app with <c>TTS source</c> = <c>in-process</c> put it there). Never a
    /// download, so on a machine without the file — CI — it passes as not exercised.
    /// </summary>
    public static SmokeCheck ProbeKokoroSynthesis(string nativeDirectory, string? modelsDirectory)
    {
        const string name = "kokoro:synthesize";
        string? model = modelsDirectory is null ? null : Path.Combine(modelsDirectory, ModelStore.KokoroFileName);
        if (model is null || !ModelStore.LooksLikeOnnx(model))
        {
            return new SmokeCheck(name, true, $"no {ModelStore.KokoroFileName} in {modelsDirectory ?? "(no models directory)"}; inference not exercised");
        }

        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            using var synth = new KokoroInProcessSynthesizer(model, nativeDirectory);
            var prepared = synth.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (!prepared.Exists)
            {
                return new SmokeCheck(name, false, prepared.Detail);
            }

            long loadMs = started.ElapsedMilliseconds;
            started.Restart();
            long bytes = 0;
            var result = synth.SynthesizeAsync("Hello from the smoke test.", "af_heart", 1.0, (_, count) => bytes += count, CancellationToken.None).GetAwaiter().GetResult();
            bool ok = result.Ok && bytes > 0 && bytes % 2 == 0 && bytes == result.PcmBytes;
            return new SmokeCheck(name, ok, ok
                ? $"loaded in {loadMs} ms; {bytes} bytes of PCM in {started.ElapsedMilliseconds} ms"
                : result.Detail);
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Binds <c>onnxruntime.dll</c> by constructing (and disposing) a <see cref="SessionOptions"/>
    /// — the first native call the in-process engine makes — with no model and no session. Also
    /// what pulls the managed ORT layer through the AOT compiler.
    /// </summary>
    public static SmokeCheck ProbeOnnxRuntime()
    {
        const string name = "kokoro:ort";
        try
        {
            using var options = new SessionOptions();
            return new SmokeCheck(name, true, $"onnxruntime bound; SessionOptions created (ORT {OrtEnv.Instance().GetVersionString()})");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads one voice embedding from <c>voices\</c> the way the engine does (NumSharp's <c>.npy</c>
    /// reader, through ILC) and mixes it with itself — the Content items proven at the right depth,
    /// the blend path proven native. No model, no session.
    /// </summary>
    public static SmokeCheck ProbeKokoroVoices(string nativeDirectory)
    {
        const string name = "kokoro:voices";
        try
        {
            var files = KokoroInProcessSynthesizer.ListVoiceFiles(nativeDirectory);
            if (!files.TryGetValue("af_heart", out var path))
            {
                return new SmokeCheck(name, false, $"no af_heart voice under {KokoroInProcessSynthesizer.VoicesDirectory(nativeDirectory)} ({files.Count} voices listed)");
            }

            var voice = KokoroVoice.FromPath(path);
            var mixed = KokoroSharp.KokoroVoiceManager.Mix(new[] { (voice, 70f), (voice, 30f) });
            int frames = voice.Features.GetLength(0);
            return new SmokeCheck(name, frames > 0 && mixed.Features.GetLength(0) == frames, $"{files.Count} voices; af_heart {frames}×{voice.Features.GetLength(1)}×{voice.Features.GetLength(2)}, mixed as {mixed.Name}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Phonemises and tokenises one English sentence through MisakiSharp — the managed G2P the
    /// engine uses for English — under the published binary's invariant globalization. No espeak-ng
    /// (that is the non-English path, a child process).
    /// </summary>
    public static SmokeCheck ProbeKokoroPhonemizer()
    {
        const string name = "kokoro:phonemize";
        try
        {
            string phonemes = Tokenizer.Phonemize("Hello world.");
            int[] tokens = Tokenizer.Tokenize("Hello world.");
            bool ok = phonemes.Length > 0 && tokens.Length > 0;
            return new SmokeCheck(name, ok, ok ? $"'{phonemes}' → {tokens.Length} tokens" : "nothing came back");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The page <see cref="ProbeWebMarkdown"/> converts: a title, a heading, a link, a list, a code fence, a table, a windows-1252 charset and the chrome that is dropped.</summary>
    public const string SmokePage =
        "<!DOCTYPE html><html><head><meta charset=\"windows-1252\"><title>Smoke &amp; Mirrors</title><style>p{color:red}</style></head>" +
        "<body><nav><a href=\"/x\">skip me</a></nav><h1>Hello</h1><p>See <a href=\"/docs\">the docs</a> &mdash; <b>bold</b>.</p>" +
        "<ul><li>one</li><li>two</li></ul><pre><code class=\"language-cs\">var x = 1;</code></pre>" +
        "<table><tr><th>a</th><th>b</th></tr><tr><td>1</td><td>2</td></tr></table><script>alert(1)</script><footer>bye</footer></body></html>";

    /// <summary>
    /// The web tools' HTML reader in the published binary: the tokenizer, the Markdown converter, the
    /// charset lookup (the code-page provider, reached from nowhere else) and the challenge / shell
    /// classifiers over <see cref="SmokePage"/>. No network.
    /// </summary>
    public static SmokeCheck ProbeWebMarkdown()
    {
        const string name = "web:markdown";
        try
        {
            string decoded = Web.PageCharset.Decode(System.Text.Encoding.ASCII.GetBytes(SmokePage), null);
            var page = Web.HtmlToMarkdown.Convert(decoded, new Uri("https://example.com/a/"));
            string expected = "# Hello\n\nSee [the docs](https://example.com/docs) — **bold**.\n\n- one\n- two\n\n```cs\nvar x = 1;\n```\n\n| a | b |\n| --- | --- |\n| 1 | 2 |";
            bool ok = page.Title == "Smoke & Mirrors" && page.Markdown == expected
                && Web.PageCharset.Lookup("windows-1252") is not null
                && !Web.WebFetcher.LooksLikeChallenge(decoded, page.Markdown) && !Web.WebFetcher.LooksLikeShell(decoded, page.Markdown);
            return new SmokeCheck(name, ok, ok ? $"title '{page.Title}', {page.Markdown.Length} chars" : $"title '{page.Title}': {page.Markdown.Replace("\n", "\\n")}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The styled transcript in the published binary (2026-09-16): Markdig's parse (a package that
    /// declares no AOT metadata — this is what proves it survives ILC in this exe) through
    /// <see cref="UI.Markdown.MarkdigParser"/>, and a <see cref="UI.Markdown.ReplyBlock"/> laid out
    /// at 40 cells on a throwaway console — the glyph, the consumed markers, the list, the fence.
    /// </summary>
    public static SmokeCheck ProbeTranscriptMarkdown()
    {
        const string name = "transcript:markdown";
        try
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Yes,
                ColorSystem = ColorSystemSupport.TrueColor,
                Interactive = InteractionSupport.No,
                Out = new AnsiConsoleOutput(TextWriter.Null),
            });
            var block = new UI.Markdown.ReplyBlock("**Hello** there\n\n- one\n- two\n\n```cs\nvar x = 1;\n```", glyph: true);
            var lines = UI.ScreenPane.RenderLines(block, console, 40).Select(l => string.Concat(l.Select(s => s.Text))).ToList();
            string[] expected = ["● Hello there", "   ", "  • one", "  • two", "   ", "  📜 cs", "    var x = 1;"];   // the scroll ahead of the label since 2026-09-22
            bool ok = lines.SequenceEqual(expected);
            return new SmokeCheck(name, ok, ok ? $"Markdig {Markdig.Markdown.Version}; {lines.Count} rows at 40 cells" : string.Join(" | ", lines));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Every embedded welcome splash picture (<see cref="SplashImages.Names"/>) loads from the
    /// manifest and reads to a thumbnail: the real files through the published binary's WIC PNG
    /// decoder (<see cref="ProbeImageResize"/> proves the codecs on a bitmap it made; this proves the
    /// resources are in the exe and the pictures decode). An empty set is a failure — the csproj glob
    /// found nothing under <c>assets\splash</c>.
    /// </summary>
    public static SmokeCheck ProbeSplash()
    {
        const string name = "splash:decode";
        try
        {
            var names = SplashImages.Names;
            if (names.Count == 0)
            {
                return new SmokeCheck(name, false, "no embedded splash picture");
            }

            var sizes = new List<string>();
            foreach (var resource in names)
            {
                var image = SplashImages.Load(resource);
                if (image is null)
                {
                    return new SmokeCheck(name, false, $"{resource} did not load");
                }

                var thumbnail = ImageThumbnail.Read(image);
                if (thumbnail is null || thumbnail.Width == 0 || thumbnail.Height == 0)
                {
                    return new SmokeCheck(name, false, $"{resource} {image.Width}x{image.Height}, no thumbnail");
                }

                sizes.Add($"{resource} {image.Width}x{image.Height} → {thumbnail.Width}x{thumbnail.Height}");
            }

            return new SmokeCheck(name, true, string.Join("; ", sizes));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A bitmap wider than <see cref="ImageFile.MaxSide"/> goes through <see cref="ImageFile.Load"/>
    /// and comes back a PNG that fits, and that PNG through <see cref="ImageThumbnail.Read"/> comes
    /// back as its pixels. This is the WIC path (MagicScaler's function-pointer COM
    /// over Windows' own codecs) in the published binary: a decoder, a scaler and an encoder, none
    /// of which anything else in the exe reaches, so this is what pulls them through ILC and what
    /// proves the COM calls survive it.
    /// </summary>
    public static SmokeCheck ProbeImageResize()
    {
        const string name = "image:resize";
        var path = Path.Combine(Path.GetTempPath(), "neonsidekick-smoke-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            File.WriteAllBytes(path, SolidBmp(ImageFile.MaxSide * 2, 4));
            var image = ImageFile.Load(path, out string? error);
            if (image is null)
            {
                return new SmokeCheck(name, false, error ?? "no image and no sentence");
            }

            bool png = image.Bytes.Length > 8 && image.Bytes[0] == 0x89 && image.Bytes[1] == (byte)'P' && image.Bytes[2] == (byte)'N' && image.Bytes[3] == (byte)'G';
            bool fits = image.Width == ImageFile.MaxSide && image.Height == 2 && image.MediaType == ImageFile.Png;
            if (!png || !fits)
            {
                return new SmokeCheck(name, false, $"{image.Width}x{image.Height} {image.MediaType}, {image.Bytes.Length} bytes{(png ? "" : ", not a PNG")}");
            }

            // The transcript's thumbnail: the PNG back through the decoder into raw BGRA pixels
            // (the format-conversion transform and CopyPixels, reached from nowhere else).
            var thumbnail = ImageThumbnail.Read(image);
            if (thumbnail is null)
            {
                return new SmokeCheck(name, false, $"{image.Width}x{image.Height} {image.MediaType}, {image.Bytes.Length} bytes, no thumbnail");
            }

            var pixel = thumbnail.At(0, 0);
            bool pink = pixel.R == 0xFF && pixel.G == 0x40 && pixel.B == 0xC8;
            bool small = thumbnail.Width == ImageThumbnail.Columns && thumbnail.Height == 1;
            return new SmokeCheck(name, pink && small, $"{image.Width}x{image.Height} {image.MediaType}, {image.Bytes.Length} bytes; thumbnail {thumbnail.Width}x{thumbnail.Height} #{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// <c>viewer:window</c> (2026-09-27): the picture viewer's Win32 layer in the published binary — the class registered, a
    /// hidden window made, a private message answered through the <c>[UnmanagedCallersOnly]</c> window procedure, the window
    /// destroyed (<see cref="Viewer.PictureWindow.Probe"/>) — and the decode it draws from: the fixture BMP through
    /// <see cref="Viewer.ViewerImage.Decode"/> into GDI's 32-bit order. Nothing is shown.
    /// </summary>
    public static SmokeCheck ProbeViewerWindow()
    {
        const string name = "viewer:window";
        try
        {
            var (ok, detail) = Viewer.PictureWindow.Probe();
            if (!ok)
            {
                return new SmokeCheck(name, false, detail);
            }

            var bitmap = Viewer.ViewerImage.Decode(SolidBmp(8, 2), "smoke.bmp");
            bool pink = bitmap is { Width: 8, Height: 2 } && bitmap.Bgrx[0] == 0xC8 && bitmap.Bgrx[1] == 0x40 && bitmap.Bgrx[2] == 0xFF;
            return new SmokeCheck(name, pink, pink ? detail + "; an 8x2 BMP decoded to BGRX" : detail + $"; the BMP decoded to {(bitmap is null ? "nothing" : $"{bitmap.Width}x{bitmap.Height}, first pixel {bitmap.Bgrx[0]:X2}{bitmap.Bgrx[1]:X2}{bitmap.Bgrx[2]:X2}")}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>viewer:drag</c> (2026-09-28): the viewer's drag out in the published binary, short of the drag — OLE started on an
    /// STA thread, the shell's data object made for a fixture BMP written to the temp folder and asked for CF_HDROP, released
    /// through its vtable (<see cref="Viewer.PictureWindowDrag.Probe"/>). Nothing is dragged; the file is deleted.
    /// </summary>
    public static SmokeCheck ProbeViewerDrag()
    {
        const string name = "viewer:drag";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        string path = Path.Combine(Path.GetTempPath(), "neonsidekick-drag-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            File.WriteAllBytes(path, SolidBmp(2, 2));
            var (ok, detail) = Viewer.PictureWindowDrag.Probe(path);
            return new SmokeCheck(name, ok, detail);
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temp file left behind is harmless.
            }
        }
    }

    /// <summary>A 24-bit uncompressed BMP of one colour, built by hand: the fixture the image check and its tests use.</summary>
    public static byte[] SolidBmp(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        int stride = (width * 3 + 3) / 4 * 4;
        int pixels = stride * height;
        var bytes = new byte[54 + pixels];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), pixels);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(38), 2835);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(42), 2835);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = 54 + y * stride + x * 3;
                bytes[at] = 0xC8;       // blue
                bytes[at + 1] = 0x40;   // green
                bytes[at + 2] = 0xFF;   // red: the theme's pink, more or less
            }
        }

        return bytes;
    }

    /// <summary>
    /// The console-input imports bind: <c>GetStdHandle</c> and <c>GetConsoleMode</c> on whatever
    /// stdin is. A redirected stdin (CI, a pipe) has no console mode and passes as "not a console";
    /// what matters is that the imports <see cref="WindowsConsoleInput"/> is built on survive the
    /// AOT compile and the mode can be read on a real console. Nothing is changed: the mode is
    /// read, never set, and no record is read.
    /// </summary>
    public static SmokeCheck ProbeConsoleInput()
    {
        const string name = "console:input";
        try
        {
            IntPtr handle = ConsoleInputNative.GetStdHandle(ConsoleInputNative.StdInputHandle);
            if (handle == IntPtr.Zero || handle == ConsoleInputNative.InvalidHandleValue)
            {
                return new SmokeCheck(name, true, "kernel32 bound; no stdin handle");
            }

            if (!ConsoleInputNative.GetConsoleMode(handle, out uint mode))
            {
                return new SmokeCheck(name, true, "kernel32 bound; stdin is not a console (redirected)");
            }

            bool mouse = (mode & ConsoleInputNative.EnableMouseInput) != 0;
            bool quickEdit = (mode & ConsoleInputNative.EnableQuickEditMode) != 0;
            return new SmokeCheck(name, true, $"kernel32 bound; console mode 0x{mode:X} (mouse {(mouse ? "on" : "off")}, quick-edit {(quickEdit ? "on" : "off")})");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Binds <c>winmm.dll</c> and, when a device exists, opens and closes <c>WAVE_MAPPER</c> at
    /// the Kokoro format without playing anything. What it proves is that the published binary's
    /// source-generated imports and the pointer-shaped header path work; whether sound comes out
    /// is <c>--audio-check</c>'s job. A CI runner with no audio endpoint reports zero devices (or
    /// <c>MMSYSERR_NODRIVER</c> / <c>MMSYSERR_BADDEVICEID</c>), which says nothing about our
    /// binary and passes; a marshalling or binding failure surfaces as an exception or another
    /// MMSYSERR, which still fails.
    /// </summary>
    public static SmokeCheck ProbeWinMm()
    {
        const string name = "audio:winmm";
        try
        {
            int devices = WinMmAudioPlayback.OutputDeviceCount();
            if (devices == 0)
            {
                return new SmokeCheck(name, true, "winmm bound; no output device (0 devices)");
            }

            int code = WinMmAudioPlayback.ProbeDefaultDevice(PcmFormat.Kokoro);
            return code switch
            {
                WinMmNative.MmsyserrNoError => new SmokeCheck(name, true, $"{devices} output device(s); WAVE_MAPPER opened and closed at {PcmFormat.Kokoro}"),
                WinMmNative.MmsyserrNoDriver or WinMmNative.MmsyserrBadDeviceId => new SmokeCheck(name, true, $"{devices} device(s) listed but none usable (MMSYSERR {code})"),
                _ => new SmokeCheck(name, false, $"waveOutOpen failed with MMSYSERR {code}"),
            };
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The capture-side twin of <see cref="ProbeWinMm"/>: binds the wave-in imports and, when a
    /// microphone exists, opens and closes <c>WAVE_MAPPER</c> at the Whisper format without
    /// recording. Zero devices (CI) passes; whether audio actually arrives is <c>--voice-check</c>'s
    /// job.
    /// </summary>
    public static SmokeCheck ProbeWinMmIn()
    {
        const string name = "audio:winmm-in";
        try
        {
            int devices = WinMmAudioCapture.InputDeviceCount();
            if (devices == 0)
            {
                return new SmokeCheck(name, true, "winmm bound; no input device (0 devices)");
            }

            int code = WinMmAudioCapture.ProbeDefaultDevice(PcmFormat.Whisper);
            return code switch
            {
                WinMmNative.MmsyserrNoError => new SmokeCheck(name, true, $"{devices} input device(s); WAVE_MAPPER opened and closed at {PcmFormat.Whisper}"),
                WinMmNative.MmsyserrNoDriver or WinMmNative.MmsyserrBadDeviceId => new SmokeCheck(name, true, $"{devices} device(s) listed but none usable (MMSYSERR {code})"),
                _ => new SmokeCheck(name, false, $"waveInOpen failed with MMSYSERR {code}"),
            };
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Actually loads libvosk by calling into it. A file being present proves the publish copied
    /// it; only a call proves the AOT binary can bind to it. Deliberately no recogniser: a
    /// <c>Model</c> over a missing directory returns a null handle that the next call dereferences,
    /// aborting the process, and CI has no model.
    /// </summary>
    public static SmokeCheck ProbeVosk()
    {
        try
        {
            Vosk.Vosk.SetLogLevel(-1);
            return new SmokeCheck("vosk:load", true, "libvosk bound; SetLogLevel(-1) returned");
        }
        catch (Exception ex)
        {
            return new SmokeCheck("vosk:load", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes Whisper.net load its native runtime without a model. <c>WhisperFactory.FromPath</c>
    /// is lazy — nothing native happens until a processor is built — so the probe builds one
    /// against a path that does not exist. The model open then fails, which is expected; what
    /// the check reads is <see cref="RuntimeOptions.LoadedLibrary"/>, which the loader sets only
    /// after a native runtime bound. It also reports <em>which</em> runtime, so a regression to
    /// the slow NoAvx build (Avx.IsSupported is a compile-time constant under NativeAOT) is
    /// visible rather than merely slow.
    /// </summary>
    private static SmokeCheck ProbeWhisper()
    {
        var bogusModel = Path.Combine(Path.GetTempPath(), "neonsidekick-no-such-model-" + Guid.NewGuid().ToString("N") + ".bin");
        string outcome;
        try
        {
            using var factory = WhisperFactory.FromPath(bogusModel);
            using var processor = factory.CreateBuilder().Build();
            outcome = "a non-existent model path produced a processor";
        }
        catch (Exception ex)
        {
            outcome = $"{ex.GetType().Name} on the bogus model, as expected";
        }

        var loaded = RuntimeOptions.LoadedLibrary;
        return loaded is null
            ? new SmokeCheck("whisper:load", false, $"no native runtime bound; {outcome}")
            : new SmokeCheck("whisper:load", true, $"runtime={loaded}; {outcome}");
    }

    /// <summary>
    /// The VAD twin of <see cref="ProbeWhisper"/>: builds a Silero processor against a path that
    /// does not exist. The model open fails, which is expected; what matters is that the
    /// <c>whisper_vad_*</c> exports bound at all, because nothing else in this binary references
    /// them and a P/Invoke ILC never saw is a P/Invoke the published exe cannot make. A missing
    /// export or library shows as one of the three exception types below and fails the check.
    /// </summary>
    public static SmokeCheck ProbeWhisperVad()
    {
        const string name = "whisper:vad-load";
        var bogusModel = Path.Combine(Path.GetTempPath(), "neonsidekick-no-such-vad-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            using var factory = WhisperVadFactory.FromPath(bogusModel);
            using var processor = factory.CreateBuilder().Build();
            return new SmokeCheck(name, true, "a non-existent model path produced a VAD processor");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, true, $"VAD exports bound; {ex.GetType().Name} on the bogus model, as expected");
        }
    }

    /// <summary>
    /// Constructs <see cref="OpenAICompatibleChatClient"/> — the OpenAI SDK plus its
    /// Microsoft.Extensions.AI adapter — without sending anything. Under NativeAOT the failure
    /// this catches is a static constructor or an adapter path that needs reflection, which
    /// surfaces as a <see cref="TypeInitializationException"/> or
    /// <see cref="NotSupportedException"/> only in the published binary.
    /// </summary>
    /// <summary>
    /// The session store's native half (<see cref="Sessions.SessionStore"/>): a store in a temp
    /// folder through Microsoft.Data.Sqlite (the e_sqlite3 bundle must load beside the exe) — a
    /// session begun, a turn appended, the FTS5 search finding it (the virtual table and its
    /// triggers, compiled in or not), the OR search (<see cref="Sessions.SessionStore.SearchAny"/>),
    /// a skill's usage and a reflection row (schema 3), the history round-tripped, the purge — then the
    /// folder removed. (The schema-1 and schema-2 migration legs went with the migrations, 2026-09-24.)
    /// </summary>
    public static SmokeCheck ProbeSessions()
    {
        const string name = "sessions:open";
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.smoke", Guid.NewGuid().ToString("N"));
        try
        {
            string version;
            using (var store = new Sessions.SessionStore(dir))
            {
                if (store.Begin("smoke", "smoke-model") is not { } id)
                {
                    return new SmokeCheck(name, false, "the store did not open (see the log)");
                }

                store.AppendTurn(id, "the smoke test wrote this", "and the reply answered", 2, ["read_file", "patch_file"], ["smoke-skill"], 1, 1, 2, false);
                var history = new List<ChatMessage> { new(ChatRole.User, "the smoke test wrote this"), new(ChatRole.Assistant, "and the reply answered") };
                store.SaveHistory(id, Sessions.SessionHistory.ToJson(history));
                var hits = store.Search("smoke", 5);
                var any = store.SearchAny(["nothing", "answered"], 5);
                var usage = store.SkillUsageOf("smoke-skill");
                store.RecordReflection(new Sessions.ReflectionRow(id, 1, false, Sessions.ReflectionRow.Learned, "smoke-skill", Sessions.ReflectionRow.Updated, 2, 30, 20));
                var mark = store.LastReflectionWrite();
                var loaded = store.Load(id);
                int restored = loaded is null ? 0 : Sessions.SessionHistory.FromJson(loaded.HistoryJson).Count;
                int purged = store.PurgeAll();
                bool telemetry = loaded is { Turns: [{ ToolNames: ["read_file", "patch_file"], SkillsLoaded: ["smoke-skill"], Errors: 1 }] }
                    && usage is { Turns: 1, Sessions: 1, WithErrors: 1 } && mark is { Skill: "smoke-skill" } && store.LastReflectionWrite() is null;
                bool ok = hits.Count == 1 && hits[0].Turn == 1 && any.Count == 1 && restored == 2 && purged == 1 && telemetry;
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
                connection.Open();
                using var query = connection.CreateCommand();
                query.CommandText = "SELECT sqlite_version()";
                version = (string)(query.ExecuteScalar() ?? "?");
                if (!ok)
                {
                    return new SmokeCheck(name, false, $"SQLite {version}; {hits.Count} hit(s), {any.Count} OR hit(s), {restored} message(s) restored, {purged} purged, telemetry {(telemetry ? "ok" : "wrong")}");
                }
            }

            return new SmokeCheck(name, true, $"SQLite {version}; 1 FTS5 hit, 1 OR hit, a skill's usage and a reflection recorded, 2 messages restored, 1 purged");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static SmokeCheck ProbeOpenAiSdk()
    {
        try
        {
            var endpoint = new LlmEndpoint(new Uri("http://127.0.0.1:1234"), "smoke-model", LlmEndpoint.DefaultApiKey, "smoke");
            using var client = new OpenAICompatibleChatClient(endpoint, TimeSpan.FromSeconds(5));
            var message = new ChatMessage(ChatRole.User, "smoke");
            var options = new ChatOptions { Tools = null };
            return new SmokeCheck("openai-sdk:construct", true,
                $"{client.Endpoint.BaseUrl} timeout {LlmTimeouts.Format(client.AppliedRequestTimeout)}; message role {message.Role}; tools {(options.Tools is null ? "none" : "set")}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck("openai-sdk:construct", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
