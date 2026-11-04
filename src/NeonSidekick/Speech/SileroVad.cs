using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using Whisper.net;

namespace NeonSidekick.Speech;

/// <summary>
/// Silero VAD tuning. The first six map onto Whisper.net's builder; the rest are this class's
/// own accumulate-and-reanalyse policy.
/// </summary>
/// <param name="Threshold">Probability above which a frame counts as speech.</param>
/// <param name="MinSilenceMs">How Silero splits segments internally. 100 ms is an ordinary gap between words, which is why it is <em>not</em> the end-of-utterance rule.</param>
/// <param name="MinSpeechMs">Shorter runs are not reported as segments at all.</param>
/// <param name="MaxSpeechSeconds">Silero forces a segment boundary after this long.</param>
/// <param name="SpeechPaddingMs">Padding Silero adds around a segment.</param>
/// <param name="Threads">Silero's thread count.</param>
/// <param name="EndOfSpeechSilenceMs">Trailing silence after the last segment that ends the utterance. Long enough to survive a pause for breath.</param>
/// <param name="AnalysisIntervalMs">How much new audio to collect between analyses. Silero is cheap; this is latency.</param>
/// <param name="IdleTailMs">Audio kept after a pass that found nothing, so a word starting mid-buffer is not clipped before the next pass.</param>
/// <param name="MaxBufferSeconds">Hard cap on the accumulation buffer.</param>
public sealed record VadOptions(
    float Threshold = 0.5f,
    int MinSilenceMs = 100,
    int MinSpeechMs = 250,
    int MaxSpeechSeconds = 60,
    int SpeechPaddingMs = 50,
    int Threads = 2,
    int EndOfSpeechSilenceMs = 700,
    int AnalysisIntervalMs = 300,
    int IdleTailMs = 1500,
    int MaxBufferSeconds = 30)
{
    public static VadOptions Default { get; } = new();
}

/// <summary>
/// End-of-utterance detection with Whisper.net's built-in Silero VAD (no ONNX runtime).
///
/// <para><b>The audio is accumulated and the whole utterance re-analysed every
/// <see cref="VadOptions.AnalysisIntervalMs"/>.</b> Silero reports segments found <em>within</em>
/// the samples it is handed, and a segment must be at least <see cref="VadOptions.MinSpeechMs"/>
/// long to be reported at all, so a 50 ms capture buffer can never contain one: fed per buffer it
/// found precisely nothing, on every call, forever. Measured in the reference: the same 8.3 s
/// clip yields 3 segments analysed whole and 0 analysed in 50 ms chunks.</para>
///
/// <para>"Speech ended" means the last segment finished <see cref="VadOptions.EndOfSpeechSilenceMs"/>
/// before the end of what we have. Firing on any segment at all would end the user's turn at the
/// first pause for breath. <see cref="Judge"/> is that rule as a pure function, pinned without a
/// model.</para>
/// </summary>
public sealed class SileroVad : IVoiceActivityDetector
{
    private const string Category = "Voice";

    private readonly string _modelPath;
    private readonly VadOptions _options;
    private readonly object _gate = new();
    private readonly MemoryStream _buffer = new();
    private int _bytesSinceAnalysis;
    private bool _feedFailureLogged;

    private readonly Func<string, bool>? _vcLoads;
    private WhisperVadFactory? _factory;
    private WhisperVadProcessor? _processor;
    private bool _disposed;

    /// <param name="vcLoads">Stands in for the Visual C++ runtime's loader in a test (<see cref="App.VcRuntime"/>, 2026-11-04).</param>
    public SileroVad(string modelPath, VadOptions? options = null, Func<string, bool>? vcLoads = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        _modelPath = modelPath;
        _options = options ?? VadOptions.Default;
        _vcLoads = vcLoads;
    }

    public string ModelPath => _modelPath;

    public VadOptions Options => _options;

    public bool IsLoaded => _processor is not null;

    public bool Load(out string detail)
    {
        if (_disposed)
        {
            detail = "disposed";
            return false;
        }

        if (_processor is not null)
        {
            detail = "already loaded";
            return true;
        }

        if (!File.Exists(_modelPath))
        {
            detail = $"model file not found: {_modelPath}";
            return false;
        }

        try
        {
            _factory = WhisperVadFactory.FromPath(_modelPath);
            _processor = _factory.CreateBuilder()
                .WithThreshold(_options.Threshold)
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(_options.MinSilenceMs))
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(_options.MinSpeechMs))
                .WithMaxSpeechDuration(TimeSpan.FromSeconds(_options.MaxSpeechSeconds))
                .WithSpeechPadding(TimeSpan.FromMilliseconds(_options.SpeechPaddingMs))
                .WithThreads(_options.Threads)
                .Build();
            detail = $"loaded {Path.GetFileName(_modelPath)}";
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Unload();
            detail = App.VcRuntime.Explain("native runtime missing: " + ex.Message, _vcLoads);
            return false;
        }
        catch (Exception ex)
        {
            Unload();
            detail = App.VcRuntime.Explain(Assistant.Explain(ex), _vcLoads);
            return false;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _buffer.SetLength(0);
            _bytesSinceAnalysis = 0;
        }

        try
        {
            _processor?.ResetState();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Debug(Category, "VAD reset failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Accumulates and, every <see cref="VadOptions.AnalysisIntervalMs"/> of new audio, analyses
    /// the whole buffer. Runs on the capture thread; a failure is logged once and reads as
    /// <see cref="VadVerdict.None"/> so the pipeline's timers still end the utterance.
    /// </summary>
    public VadVerdict Feed(byte[] pcm, int count)
    {
        if (_disposed || _processor is not { } processor || pcm is null || count <= 0)
        {
            return VadVerdict.None;
        }

        try
        {
            byte[] toAnalyse;
            lock (_gate)
            {
                _buffer.Write(pcm, 0, Math.Min(count, pcm.Length));
                _bytesSinceAnalysis += count;
                if (_bytesSinceAnalysis < BytesFor(_options.AnalysisIntervalMs))
                {
                    return VadVerdict.None;
                }

                _bytesSinceAnalysis = 0;
                TrimLocked(_options.MaxBufferSeconds * 1000);
                toAnalyse = _buffer.ToArray();
            }

            var segments = processor.DetectSpeech(Pcm16ToFloat(toAnalyse));
            long bufferedMs = toAnalyse.Length * 1000L / PcmFormat.Whisper.BytesPerSecond;
            var verdict = Judge(segments, bufferedMs, _options);

            lock (_gate)
            {
                if (verdict == VadVerdict.None)
                {
                    // Nothing to build a segment from: keep only a short tail, or the buffer grows
                    // to its cap and tens of seconds of silence are re-analysed several times a second.
                    TrimLocked(_options.IdleTailMs);
                }
                else if (verdict == VadVerdict.EndOfSpeech)
                {
                    _buffer.SetLength(0);
                    _bytesSinceAnalysis = 0;
                }
            }

            return verdict;
        }
        catch (Exception ex)
        {
            if (!_feedFailureLogged)
            {
                _feedFailureLogged = true;
                DiagnosticLog.Warn(Category, "Voice activity detection failed: " + Assistant.Explain(ex), ex);
            }

            return VadVerdict.None;
        }
    }

    /// <summary>
    /// The end-of-utterance rule. No segments: nothing heard. The last segment still reaching
    /// (near) the end of the buffer, or followed by less than <see cref="VadOptions.EndOfSpeechSilenceMs"/>
    /// of silence: still speaking. Otherwise the utterance is over.
    /// </summary>
    public static VadVerdict Judge(IReadOnlyList<VadSegmentData> segments, long bufferedMs, VadOptions options)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);
        if (segments.Count == 0)
        {
            return VadVerdict.None;
        }

        long trailingSilenceMs = bufferedMs - (long)segments[^1].End.TotalMilliseconds;
        if (trailingSilenceMs < options.AnalysisIntervalMs || trailingSilenceMs < options.EndOfSpeechSilenceMs)
        {
            return VadVerdict.Speaking;
        }

        return VadVerdict.EndOfSpeech;
    }

    /// <summary>PCM16 little-endian to normalised floats, what Silero consumes.</summary>
    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            samples[i] = sample / 32768f;
        }

        return samples;
    }

    private static int BytesFor(int milliseconds) => PcmFormat.Whisper.BytesFor(milliseconds);

    /// <summary>Keeps only the most recent <paramref name="ms"/> of audio. Caller holds the gate.</summary>
    private void TrimLocked(int ms)
    {
        int maxBytes = BytesFor(ms);
        if (_buffer.Length <= maxBytes)
        {
            return;
        }

        var all = _buffer.ToArray();
        _buffer.SetLength(0);
        _buffer.Write(all, all.Length - maxBytes, maxBytes);
    }

    private void Unload()
    {
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unload();
        lock (_gate)
        {
            _buffer.Dispose();
        }
    }
}
