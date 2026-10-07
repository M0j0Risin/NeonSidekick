using System.Globalization;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;
using Microsoft.ML.OnnxRuntime;
using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Speech;

/// <summary>
/// <see cref="ISpeechSynthesizer"/> over KokoroSharp in this process (<c>TTS source</c> =
/// <c>in-process</c>, 2026-09-16): one ONNX Runtime session over <c>kokoro.onnx</c>, the voice
/// embeddings read from the <c>voices\</c> folder beside the exe, 24 kHz 16-bit mono PCM out —
/// the same bytes the HTTP server streams, so <see cref="SpeechOutput"/> cannot tell the two apart.
///
/// <para><b>The session loads in <see cref="PrepareAsync"/> and nowhere else.</b> It costs
/// ~0.8 s and ~750 MB resident (measured 2026-09-16), so the speech session runs it once at
/// connect and disposes it at the next; <see cref="ListVoicesAsync"/> reads file names only,
/// which is what the settings picker calls on a throwaway instance. Rules kept:
/// the model is opened from an absolute path (never <c>KokoroTTS.LoadModel(KModel…)</c>, which
/// resolves against the current directory and downloads 325 MB into it), there is one session
/// (a second one was +675 MB), and KokoroSharp's own playback is never created.</para>
///
/// <para>Voices are resolved by file (<see cref="KokoroVoice.FromPath"/>) rather than through
/// <c>KokoroVoiceManager</c>'s process-wide static list, so the content directory is an honest
/// seam and a blend (<see cref="VoiceMix"/>'s <c>a(70)+b(30)</c>, which the server parses for the
/// HTTP path) is mixed here from the two embeddings. English is phonemised by MisakiSharp in
/// this process; every other language spawns the bundled <c>espeak-ng</c> from
/// <c>espeak\</c> — the one process start outside the app's own two, inside the package.</para>
///
/// <para>One synthesis at a time is the caller's guarantee (<see cref="SpeechQueue"/>'s single
/// consumer, the menu's serialised preview chain); nothing here locks or touches the console.
/// A call runs to completion whatever the token says — an ORT run must never see its session
/// disposed under it — and the token is honoured between the pieces handed to the sink; a
/// sentence is at most 240 characters, half a second on a CPU, so a stop lands within that.</para>
/// </summary>
public sealed class KokoroInProcessSynthesizer : ISpeechSynthesizer
{
    private const string Category = "Speech";

    /// <summary>The folders KokoroSharp expects beside the exe (the csproj republishes the package's content there).</summary>
    public const string VoicesFolder = "voices";
    public const string EspeakFolder = "espeak";
    public const string VoiceExtension = ".npy";

    /// <summary>
    /// The espeak-ng executable KokoroSharp runs on this OS, inside <see cref="EspeakFolder"/>. The package ships one per OS
    /// and architecture, every one named <c>.dll</c> whatever it is (a Mach-O on macOS); KokoroSharp builds the same name
    /// (2026-10-06, the macOS build: the smoke names the running OS's, and <see cref="EnsureEspeakRunnable"/> marks it executable).
    /// </summary>
    public static string EspeakExecutable =>
        "espeak-ng-"
        + (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "macos" : "linux")
        + "-"
        + (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64")
        + ".dll";

    /// <summary>100 ms of audio per piece handed to the sink, like the HTTP path's reads.</summary>
    private const int PieceBytes = 4800;

    public const string MissingRuntimeDetail = "onnxruntime.dll is missing beside the exe";
    public const string MissingVoicesDetail = "the voices folder is missing beside the exe";
    public const string MissingModelDetail = "kokoro.onnx is missing";
    public const string NotLoadedDetail = "in-process Kokoro is not loaded";

    private readonly string _modelPath;
    private readonly string _contentDirectory;
    private readonly Dictionary<string, KokoroVoice> _voices = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _voiceFiles = new Dictionary<string, string>();
    private SessionOptions? _options;
    private KokoroWavSynthesizer? _engine;
    private bool _disposed;

    /// <param name="modelPath">The absolute path of <c>kokoro.onnx</c> (the session has ensured it).</param>
    /// <param name="contentDirectory">Where <c>voices\</c> and <c>espeak\</c> live; the exe's directory by default, a temp dir in tests.</param>
    public KokoroInProcessSynthesizer(string modelPath, string? contentDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        _modelPath = modelPath;
        _contentDirectory = string.IsNullOrWhiteSpace(contentDirectory) ? AppContext.BaseDirectory : contentDirectory;
    }

    public PcmFormat Format => PcmFormat.Kokoro;

    /// <summary>Whether <see cref="PrepareAsync"/> has loaded the session.</summary>
    public bool IsLoaded => _engine is not null;

    /// <summary>The voices folder under <paramref name="contentDirectory"/>.</summary>
    public static string VoicesDirectory(string contentDirectory) => Path.Combine(contentDirectory, VoicesFolder);

    /// <summary>
    /// Every voice file under <c>voices\</c> (subfolders too — the package ships
    /// <c>voices-zh\</c> inside it), keyed by the name the setting uses (the file's stem), sorted
    /// ordinally like the server's list so English (<c>a*</c>, <c>b*</c>) comes first. Empty when the
    /// folder is missing. Pure.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ListVoiceFiles(string contentDirectory)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string directory = VoicesDirectory(contentDirectory);
        if (!Directory.Exists(directory))
        {
            return files;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*" + VoiceExtension, SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length > 0)
            {
                files.TryAdd(name, path);
            }
        }

        return files;
    }

    private static VoiceListResult List(IReadOnlyDictionary<string, string> files) =>
        new(true, files.Keys.ToArray(), files.Count == 1 ? "1 voice" : $"{files.Count.ToString(CultureInfo.InvariantCulture)} voices");

    /// <summary>The file names alone: no session, no native call.</summary>
    public Task<VoiceListResult> ListVoicesAsync(CancellationToken cancellationToken)
    {
        var files = ListVoiceFiles(_contentDirectory);
        return Task.FromResult(files.Count == 0 ? VoiceListResult.Missing(MissingVoicesDetail) : List(files));
    }

    /// <summary>
    /// Loads the session (once; a second call answers from the first) and lists the voices.
    /// Every failure is a result with a pinned detail: the voices folder or the model file
    /// missing (checked before anything native), <c>onnxruntime.dll</c> missing beside the exe,
    /// or the model refusing to load.
    /// </summary>
    public async Task<VoiceListResult> PrepareAsync(CancellationToken cancellationToken)
    {
        if (_engine is not null)
        {
            return List(_voiceFiles);
        }

        var files = ListVoiceFiles(_contentDirectory);
        if (files.Count == 0)
        {
            return VoiceListResult.Missing(MissingVoicesDetail);
        }

        if (!File.Exists(_modelPath))
        {
            return VoiceListResult.Missing(MissingModelDetail);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = await Task.Run(() =>
            {
                // The tokenizer's espeak-ng lives beside the voices; the default is the exe's directory.
                Tokenizer.eSpeakNGPath = Path.Combine(_contentDirectory, EspeakFolder);
                EnsureEspeakRunnable(Tokenizer.eSpeakNGPath);
                var options = new SessionOptions();
                try
                {
                    return (Options: options, Engine: new KokoroWavSynthesizer(_modelPath, options));
                }
                catch
                {
                    options.Dispose();
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);

            if (_disposed)
            {
                loaded.Engine.Dispose();
                loaded.Options.Dispose();
                return VoiceListResult.Missing("disposed");
            }

            _options = loaded.Options;
            _engine = loaded.Engine;
            _voiceFiles = files;
            DiagnosticLog.Debug(Category, $"Kokoro loaded in-process from {_modelPath} ({files.Count} voices).");
            return List(files);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return VoiceListResult.Missing("cancelled");
        }
        catch (DllNotFoundException ex)
        {
            DiagnosticLog.Error(Category, "Kokoro in-process: " + ex.Message, ex);
            return VoiceListResult.Missing(MissingRuntimeDetail);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(Category, "Kokoro in-process: the model did not load: " + ex.Message, ex);
            return VoiceListResult.Missing("model load failed: " + ex.Message);
        }
    }

    public async Task<SynthesisResult> SynthesizeAsync(string text, string voice, double speed, Action<byte[], int> pcmSink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcmSink);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SynthesisResult(true, 0, "nothing to say");
        }

        if (_engine is not { } engine)
        {
            return SynthesisResult.Failed(NotLoadedDetail);
        }

        KokoroVoice embedding;
        try
        {
            if (!TryResolveVoice(voice, out embedding, out string? unknown))
            {
                return SynthesisResult.Failed(UnknownVoiceDetail(unknown ?? voice));
            }
        }
        catch (Exception ex)
        {
            return SynthesisResult.Failed($"voice '{voice}' could not be read: {ex.Message}");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Awaited whatever the token says: a run in flight must finish before anything is disposed.
            byte[] bytes = await engine.SynthesizeAsync(text, embedding, new KokoroTTSPipelineConfig { Speed = (float)speed }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            long total = Deliver(WavBytes.Payload(bytes), pcmSink, cancellationToken);
            return new SynthesisResult(true, total, $"{total.ToString(CultureInfo.InvariantCulture)} bytes");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SynthesisResult.Failed(ex.Message);
        }
    }

    public static string UnknownVoiceDetail(string name) => $"unknown voice '{name}'";

    /// <summary>
    /// Marks this OS's espeak-ng executable runnable by its owner when it is not (2026-10-06, the macOS build). NuGet unpacks
    /// a package without Unix modes, so the copy beside the exe lands as 0644 and KokoroSharp's start of it fails with a
    /// permission error; <c>build.ps1</c> sets the bit in the published folder, and this covers a <c>dotnet run</c> or a copy
    /// that lost it. Nothing to do on Windows; a failure is logged and left for the start itself to report.
    /// </summary>
    public static void EnsureEspeakRunnable(string espeakDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(espeakDirectory, EspeakExecutable);
        try
        {
            if (File.Exists(path) && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) == 0)
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                DiagnosticLog.Debug(Category, $"Marked {path} executable.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not mark {path} executable: {ex.Message}");
        }
    }

    /// <summary>Hands <paramref name="pcm"/> to the sink in 100 ms even-length pieces; a trailing odd byte is dropped with a Debug line, as on the HTTP path.</summary>
    private static long Deliver(ReadOnlyMemory<byte> pcm, Action<byte[], int> pcmSink, CancellationToken cancellationToken)
    {
        int even = pcm.Length & ~1;
        if (even != pcm.Length)
        {
            DiagnosticLog.Debug(Category, "The synthesised audio ended on a half sample; dropped one byte.");
        }

        var buffer = new byte[PieceBytes];
        long total = 0;
        for (int offset = 0; offset < even; offset += PieceBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(PieceBytes, even - offset);
            pcm.Slice(offset, count).CopyTo(buffer);
            pcmSink(buffer, count);
            total += count;
        }

        return total;
    }

    /// <summary>
    /// The embedding for a wire voice: one file, or <see cref="VoiceMix"/>'s parts mixed by
    /// KokoroSharp (weights normalised there). Cached per spec. False, with the offending name,
    /// when a part is not among the files.
    /// </summary>
    private bool TryResolveVoice(string spec, out KokoroVoice embedding, out string? unknown)
    {
        unknown = null;
        if (_voices.TryGetValue(spec, out embedding!))
        {
            return true;
        }

        if (!VoiceMix.TryParse(spec, out var parts))
        {
            unknown = spec;
            return false;
        }

        var weighted = new (KokoroVoice, float)[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            if (!_voiceFiles.TryGetValue(parts[i].Name, out var path))
            {
                unknown = parts[i].Name;
                return false;
            }

            if (!_voices.TryGetValue(parts[i].Name, out var single))
            {
                single = KokoroVoice.FromPath(path);
                _voices[parts[i].Name] = single;
            }

            weighted[i] = (single, parts[i].Percent);
        }

        embedding = weighted.Length == 1 ? weighted[0].Item1 : KokoroSharp.KokoroVoiceManager.Mix(weighted);
        _voices[spec] = embedding;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine?.Dispose();
        _engine = null;
        _options?.Dispose();
        _options = null;
        _voices.Clear();
    }
}
