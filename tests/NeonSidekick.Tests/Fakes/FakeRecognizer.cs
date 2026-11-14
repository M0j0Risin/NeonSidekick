using NeonSidekick.Audio;
using NeonSidekick.Speech;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// An <see cref="ISpeechRecognizer"/> that answers <see cref="Text"/> for every call and records
/// what it was given. <see cref="OnTranscribe"/> runs before the answer, so a test can hold the
/// call open or push keys; <see cref="Log"/> (shared with <see cref="FakeAudioCapture"/>) gets a
/// "transcribe" entry so ordering against capture stop can be asserted.
/// </summary>
public sealed class FakeRecognizer : ISpeechRecognizer
{
    public PcmFormat Format => PcmFormat.Whisper;

    public string Text { get; set; } = "hello";

    public bool Fail { get; set; }

    /// <summary>Answers even once the token is cancelled, as Whisper may when its run was already done (a cancel that lost the race).</summary>
    public bool IgnoreCancellation { get; set; }

    public string FailureDetail { get; set; } = "transcription failed: scripted";

    public bool LoadFails { get; set; }

    public bool Loaded { get; private set; }

    public bool Disposed { get; private set; }

    public List<byte[]> Received { get; } = new();

    public List<string> Log { get; init; } = new();

    public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? OnTranscribe { get; set; }

    public bool Load(out string detail)
    {
        if (LoadFails)
        {
            detail = "model file not found: ggml-base.en.bin";
            return false;
        }

        Loaded = true;
        detail = "loaded";
        return true;
    }

    public async Task<RecognitionResult> TranscribeAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        lock (Log)
        {
            Log.Add("transcribe");
        }

        Received.Add(pcm.ToArray());
        if (OnTranscribe is not null)
        {
            await OnTranscribe(pcm, cancellationToken);
        }

        if (!IgnoreCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (Fail)
        {
            return RecognitionResult.Failed(FailureDetail);
        }

        var audio = TimeSpan.FromSeconds(pcm.Length / (double)Format.BytesPerSecond);
        return new RecognitionResult(true, Text, audio, TimeSpan.FromMilliseconds(12), "ok");
    }

    public void Dispose() => Disposed = true;
}
