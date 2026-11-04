using System.Diagnostics;
using System.Globalization;
using System.Text;
using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using Whisper.net;

namespace NeonSidekick.Speech;

/// <summary>
/// Speech to text through Whisper.net over a ggml model, in process.
///
/// <para><b>Whisper.net parses its input stream as a WAV file.</b> Raw PCM straight off the
/// microphone throws <c>Invalid wave file RIFF header</c>, so <see cref="WrapPcmAsWav"/> puts the
/// 44-byte header in front of it. The capture path already records at the 16 kHz mono PCM16
/// Whisper wants, so the container is the only thing missing.</para>
///
/// <para>Transcription is timed and logged: it is most of the gap between saying something and
/// being answered, and on the wrong native runtime (noavx) it measured 5 s for 2 s of speech
/// with nothing anywhere saying so.</para>
/// </summary>
public sealed class WhisperNetTranscriber : ISpeechRecognizer
{
    private const string Category = "Voice";

    private readonly string _modelPath;
    private readonly string _language;
    private readonly Func<string, bool>? _vcLoads;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private bool _disposed;

    /// <param name="vcLoads">Stands in for the Visual C++ runtime's loader in a test (<see cref="App.VcRuntime"/>, 2026-11-04).</param>
    public WhisperNetTranscriber(string modelPath, string language = "en", Func<string, bool>? vcLoads = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        _modelPath = modelPath;
        _language = language;
        _vcLoads = vcLoads;
    }

    public PcmFormat Format => PcmFormat.Whisper;

    public string ModelPath => _modelPath;

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
            // FromPath is lazy; Build is where the model is actually read and the native runtime bound.
            _factory = WhisperFactory.FromPath(_modelPath);
            _processor = _factory.CreateBuilder().WithLanguage(_language).Build();
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

    public async Task<RecognitionResult> TranscribeAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        if (_processor is not { } processor)
        {
            return RecognitionResult.Failed("model not loaded");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var audio = TimeSpan.FromSeconds(pcm.Length / (double)Format.BytesPerSecond);
        var clock = Stopwatch.StartNew();

        try
        {
            string text = await Task.Run(async () =>
            {
                var wav = WrapPcmAsWav(pcm.Span, Format);
                var sb = new StringBuilder();
                using var stream = new MemoryStream(wav);
                await foreach (var segment in processor.ProcessAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    sb.Append(segment.Text);
                }

                return sb.ToString();
            }, cancellationToken).ConfigureAwait(false);

            clock.Stop();
            double seconds = audio.TotalSeconds;
            double realtime = seconds > 0 ? clock.Elapsed.TotalSeconds / seconds : 0;
            DiagnosticLog.Info(Category, string.Create(CultureInfo.InvariantCulture,
                $"Transcribed {seconds:F1}s in {clock.ElapsedMilliseconds}ms ({realtime:F1}x realtime): {LogText.Quoted(text)}"));
            return new RecognitionResult(true, text, audio, clock.Elapsed, "ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RecognitionResult.Failed("transcription failed: " + Assistant.Explain(ex));
        }
    }

    /// <summary>
    /// Wraps raw PCM in the canonical 44-byte RIFF/WAVE header for <paramref name="format"/>.
    /// The layout is pinned byte-by-byte by tests.
    /// </summary>
    public static byte[] WrapPcmAsWav(ReadOnlySpan<byte> pcm, PcmFormat format)
    {
        const int headerBytes = 44;
        int byteRate = format.BytesPerSecond;
        short blockAlign = (short)format.BlockAlign;

        var wav = new byte[headerBytes + pcm.Length];
        var span = wav.AsSpan();
        WriteAscii(span, 0, "RIFF");
        BitConverter.TryWriteBytes(span[4..], 36 + pcm.Length);
        WriteAscii(span, 8, "WAVE");
        WriteAscii(span, 12, "fmt ");
        BitConverter.TryWriteBytes(span[16..], 16);                          // PCM fmt chunk size
        BitConverter.TryWriteBytes(span[20..], (short)1);                    // uncompressed PCM
        BitConverter.TryWriteBytes(span[22..], (short)format.Channels);
        BitConverter.TryWriteBytes(span[24..], format.SampleRate);
        BitConverter.TryWriteBytes(span[28..], byteRate);
        BitConverter.TryWriteBytes(span[32..], blockAlign);
        BitConverter.TryWriteBytes(span[34..], (short)format.BitsPerSample);
        WriteAscii(span, 36, "data");
        BitConverter.TryWriteBytes(span[40..], pcm.Length);
        pcm.CopyTo(span[headerBytes..]);
        return wav;
    }

    private static void WriteAscii(Span<byte> target, int offset, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            target[offset + i] = (byte)text[i];
        }
    }

    private void Unload()
    {
        // Processor before factory: the processor holds the context the factory owns.
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
    }
}
