using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Speech;

/// <summary>
/// Speech for one reply: streamed text in, audio out. Composes <see cref="CodeBlockFilter"/> (fenced code is never
/// spoken, 2026-09-25), <see cref="TableFilter"/> (nor are tables, 2026-09-26), <see cref="SentenceChunker"/>, <see cref="SpeechQueue"/>, an <see cref="ISpeechSynthesizer"/> and
/// an <see cref="IAudioPlayback"/>.
///
/// <para>The loop feeds every text delta through <see cref="Feed"/>; whole sentences are queued
/// and a consumer synthesises them in order, streaming each one's PCM straight into playback.
/// Synthesis runs <em>ahead</em> of playback — the consumer does not wait for a sentence to finish
/// playing before rendering the next — so the only wait is at the end: <see cref="Completion"/>
/// completes when the queue is drained and the device reports every byte played (or when the
/// token is cancelled, after <see cref="StopAll"/> has silenced the device). The token is the
/// speaker's own (<c>SpeechSession</c> holds it): the turn that feeds the text cancels it while
/// it runs, and the tail left when the text ends plays on under the input line until the screen
/// stops it there.</para>
///
/// <para>Runs on the thread pool and never touches the console. Failures go through
/// <see cref="DiagnosticLog"/> (which the chat screen queues and drains at safe points) and
/// <c>onFailure</c>; the first failure in a turn is reported once and the rest of the turn is
/// silent, so a dead server costs one warning, not one per sentence.</para>
///
/// <para><b>What is being heard right now</b> (M6, the interrupt's echo guard): every byte handed
/// to the device is counted and each sentence remembers the byte range it occupies, so
/// <see cref="SpokenNear"/> can name the text at the play head (bytes written minus bytes the
/// device still owes). Synthesis time would be the wrong clock: it runs ahead of playback by
/// up to the whole reply.</para>
/// </summary>
internal sealed class SpeechOutput
{
    public const string Category = "Speech";

    /// <summary>
    /// Upper bound on the wait for the consumer after a stop. A ceiling against a device that
    /// stops draining, not a budget anyone should reach: a stopped speaker completes at once.
    /// </summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(3);

    /// <summary>How far behind the play head <see cref="SpokenNear"/> looks by default: Vosk's finalisation lag, the phrase itself and the device's latency.</summary>
    public static readonly TimeSpan DefaultEchoLookBack = TimeSpan.FromSeconds(3);

    private readonly ISpeechSynthesizer _synth;
    private readonly IAudioPlayback _playback;
    private readonly string _voice;
    private readonly double _speed;
    private readonly CancellationToken _turnToken;
    private readonly Action<string>? _onFailure;
    private readonly CodeBlockFilter _code = new();
    private readonly TableFilter _tables = new();
    private readonly SentenceChunker _chunker = new();
    private readonly SpeechQueue _queue;
    private readonly object _chunkGate = new();
    private readonly List<SpokenChunk> _chunks = new();

    private int _chunksQueued;
    private int _chunksStarted;
    private long _written;
    private int _stoppedAt;
    private volatile bool _failed;

    /// <summary>A sentence and the byte range its audio occupies in the written stream; <c>End</c> is -1 while it is still being synthesised.</summary>
    private sealed record SpokenChunk(string Text, long Start)
    {
        public long End { get; set; } = -1;
    }

    /// <param name="synth">The server client.</param>
    /// <param name="playback">The device; started lazily on the first chunk, silenced by <see cref="StopAll"/>.</param>
    /// <param name="voice">Kokoro voice id.</param>
    /// <param name="speed">Kokoro speed multiplier.</param>
    /// <param name="turnToken">The turn's token: cancelling it abandons queued speech and silences the device.</param>
    /// <param name="onFailure">Invoked once, on the consumer thread, with the first failure's detail.</param>
    public SpeechOutput(ISpeechSynthesizer synth, IAudioPlayback playback, string voice, double speed, CancellationToken turnToken, Action<string>? onFailure = null)
    {
        _synth = synth ?? throw new ArgumentNullException(nameof(synth));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _voice = string.IsNullOrWhiteSpace(voice) ? throw new ArgumentException("Voice must not be blank.", nameof(voice)) : voice.Trim();
        _speed = speed;
        _turnToken = turnToken;
        _onFailure = onFailure;

        _queue = new SpeechQueue(SpeakChunkAsync, _ => OnChunkStarted(), StopAll, turnToken);
        Completion = WaitAsync();
    }

    /// <summary>
    /// Completes when every queued sentence has been synthesised and the device has played it,
    /// or — normally, not faulted — once the turn token is cancelled. Faults only on an unexpected
    /// exception in the consumer.
    /// </summary>
    public Task Completion { get; }

    public int ChunksQueued => Volatile.Read(ref _chunksQueued);

    public int ChunksStarted => Volatile.Read(ref _chunksStarted);

    /// <summary>
    /// Told once, as the first chunk starts playing (2026-10-05, the video window's pause while the app speaks): a reply that only
    /// calls tools, or is cut off before a sentence, never says it. Called on the queue's thread; it must not block, and what it
    /// throws is logged, never the queue's.
    /// </summary>
    public Action? FirstAudio { get; set; }

    private void OnChunkStarted()
    {
        if (Interlocked.Increment(ref _chunksStarted) == 1 && FirstAudio is { } first)
        {
            try
            {
                first();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Diagnostics.DiagnosticLog.Warn("TTS", "A first-audio listener failed: " + ex.Message);
            }
        }
    }

    /// <summary>Whether this turn's speech failed; the rest of the turn is skipped.</summary>
    public bool Failed => _failed;

    /// <summary>The device's format: what <see cref="WrittenBytes"/> and <see cref="PlayedBytes"/> count in.</summary>
    public PcmFormat Format => _playback.Format;

    /// <summary>
    /// The interrupt's echo probe, when the turn is armed for it: every byte written to the device
    /// is also handed here (a copy, on the consumer thread, after the write). Set before the first
    /// sentence is fed; the owner completes and disposes it.
    /// </summary>
    public EchoProbe? Probe { get; set; }

    /// <summary>Bytes handed to the device so far this turn.</summary>
    public long WrittenBytes => Interlocked.Read(ref _written);

    /// <summary>Bytes the device has actually played: written minus what it still owes.</summary>
    public long PlayedBytes => Math.Max(0, WrittenBytes - Math.Max(0, _playback.BufferedBytes));

    /// <summary>
    /// The text of every sentence whose audio touches the window <c>[play head − lookBack, play head]</c>,
    /// space-joined; empty when nothing has reached the speaker. A sentence still being synthesised
    /// counts from its first byte. Safe from any thread; used by the interrupt's echo guard on the
    /// capture thread, so it does nothing but arithmetic under one short lock.
    /// </summary>
    public string SpokenNear(TimeSpan lookBack)
    {
        long played = PlayedBytes;
        long from = played - Math.Max(0, _playback.Format.BytesFor((int)Math.Min(int.MaxValue, lookBack.TotalMilliseconds)));
        var texts = new List<string>();
        lock (_chunkGate)
        {
            foreach (var chunk in _chunks)
            {
                if (chunk.End == chunk.Start)
                {
                    continue;   // no audio: nothing speakable in it
                }

                long end = chunk.End < 0 ? long.MaxValue : chunk.End;
                if (chunk.Start <= played && end >= from)
                {
                    texts.Add(chunk.Text);
                }
            }
        }

        return string.Join(' ', texts);
    }

    /// <summary>
    /// The 1-based index of the sentence at the play head — the last one started whose audio
    /// holds <see cref="PlayedBytes"/> (an unfinished one counts from its first byte); the last
    /// sentence with bytes once the play head has passed every one; 0 before any sound. A
    /// sentence with nothing speakable keeps its number and never counts. Safe from any thread
    /// (the <c>/speak</c> position on the hint row is read on the pane's tick).
    /// </summary>
    public int PlayingChunk
    {
        get
        {
            long played = PlayedBytes;
            int at = 0;
            int lastWithBytes = 0;
            lock (_chunkGate)
            {
                for (int i = 0; i < _chunks.Count; i++)
                {
                    var chunk = _chunks[i];
                    if (chunk.End == chunk.Start)
                    {
                        continue;   // nothing speakable, or not a byte yet
                    }

                    lastWithBytes = i + 1;
                    if (chunk.Start <= played && (chunk.End < 0 || played < chunk.End))
                    {
                        at = i + 1;
                    }
                }
            }

            return at > 0 ? at : lastWithBytes;
        }
    }

    /// <summary>
    /// <see cref="PlayingChunk"/> as it was when the device was first silenced (<see cref="StopAll"/>,
    /// taken before the buffer is cleared — after that the play head reads as the end of what was
    /// written); 0 while never stopped. What a stopped <c>/speak</c> resumes at.
    /// </summary>
    public int StoppedAtChunk => Volatile.Read(ref _stoppedAt);

    /// <summary>
    /// One sentence, already split, straight into the queue (2026-09-17): the <c>/speak</c>
    /// reading feeds its own list this way so sentence <em>k</em> of the list is chunk <em>k</em>
    /// here, whatever the chunker would have made of the joined text. <see cref="Feed"/> is the
    /// streamed reply's way.
    /// </summary>
    public void Speak(string sentence)
    {
        if (!string.IsNullOrWhiteSpace(sentence))
        {
            Enqueue(sentence);
        }
    }

    /// <summary>A text delta from the model. Whole sentences are queued as they complete.</summary>
    public void Feed(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        // The code blocks and tables go before the text is cut into sentences: the chunker would split one across several.
        // Code first, so a table inside a fence is already gone.
        foreach (var sentence in _chunker.Append(_tables.Push(_code.Push(delta))))
        {
            Enqueue(sentence);
        }
    }

    /// <summary>The text is finished: queue the trailing partial sentence and close the queue. Call on every path.</summary>
    public void CompleteAdding()
    {
        foreach (var sentence in _chunker.Append(_tables.Push(_code.Flush()) + _tables.Flush()))
        {
            Enqueue(sentence);
        }

        var tail = _chunker.Flush();
        if (tail.Length > 0)
        {
            Enqueue(tail);
        }

        _queue.CompleteAdding();
    }

    /// <summary>
    /// Silences the device now: drop what is queued, then stop it. Idempotent. The queue calls
    /// this on cancellation; the screen calls it when the drain times out.
    /// </summary>
    public void StopAll()
    {
        // The play head as it was: the clear below makes every written byte count as played.
        Interlocked.CompareExchange(ref _stoppedAt, PlayingChunk, 0);
        try
        {
            _playback.ClearBuffer();
            _playback.Stop();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(Category, $"Stopping playback failed: {ex.Message}", ex);
        }
    }

    private void Enqueue(string sentence)
    {
        if (_queue.TryEnqueue(sentence))
        {
            Interlocked.Increment(ref _chunksQueued);
        }
    }

    private async Task SpeakChunkAsync(string sentence, CancellationToken cancellationToken)
    {
        if (_failed)
        {
            return;
        }

        var text = SpeakableText.MakeSpeakable(sentence);
        // Every started sentence has its chunk, so chunk k is sentence k (PlayingChunk); one with
        // nothing speakable, or that never starts the device, closes at once with no bytes.
        var chunk = new SpokenChunk(text, WrittenBytes);
        lock (_chunkGate)
        {
            _chunks.Add(chunk);
        }

        if (text.Length == 0)
        {
            Close(chunk);
            return;
        }

        try
        {
            if (!_playback.IsPlaying)
            {
                _playback.Start();
            }
        }
        catch (Exception ex)
        {
            Close(chunk);
            Fail("Audio output failed to start: " + ex.Message);
            return;
        }

        try
        {
            var result = await _synth.SynthesizeAsync(text, _voice, _speed, WriteCounted, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                Fail("Speech synthesis failed: " + result.Detail);
            }
        }
        finally
        {
            Close(chunk);
        }
    }

    private void Close(SpokenChunk chunk)
    {
        lock (_chunkGate)
        {
            chunk.End = WrittenBytes;
        }
    }

    private void WriteCounted(byte[] pcm, int count)
    {
        _playback.Write(pcm, count);
        if (count > 0)
        {
            long end = Interlocked.Add(ref _written, count);
            Probe?.Feed(pcm, count, end);
        }
    }

    private void Fail(string detail)
    {
        if (_failed)
        {
            return;
        }

        _failed = true;
        DiagnosticLog.Warn(Category, detail);
        try
        {
            _onFailure?.Invoke(detail);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(Category, $"Failure callback threw: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// True once <see cref="Completion"/> completed because the turn token was cancelled (the
    /// queue abandoned mid-sentence, or the played tail cut short), i.e. the device was silenced
    /// with audio still owed. <see cref="Completion"/> completes <em>successfully</em> on that
    /// path too, so a caller that awaited it cannot tell from the task alone; the screen reads
    /// this to decide between "the reply was heard" and "(interrupted)" / "(speech stopped)".
    /// Racing the token itself is not enough: the queue can observe the cancellation and finish
    /// before the caller's own wait registers it.
    /// </summary>
    public bool StoppedEarly { get; private set; }

    private async Task WaitAsync()
    {
        await _queue.Completion.ConfigureAwait(false);

        if (_turnToken.IsCancellationRequested)
        {
            // The queue's onCancelled already silenced the device.
            StoppedEarly = true;
            return;
        }

        try
        {
            while (_playback.BufferedBytes > 0)
            {
                await Task.Delay(20, _turnToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // ESC during the spoken tail, after the last sentence was synthesised.
            StoppedEarly = true;
            StopAll();
        }
    }
}
