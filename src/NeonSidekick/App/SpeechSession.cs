using System.Globalization;
using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Speech;

namespace NeonSidekick.App;

/// <summary>
/// The speech source, the voice and the speaker for one interactive screen — the counterpart of
/// <see cref="LlmSession"/>. <see cref="ConnectAsync"/> rebuilds the synthesizer for the effective
/// settings and probes it (only when speech is enabled: with it off no HTTP happens and no model
/// loads) — a Kokoro-FastAPI server over HTTP, or Kokoro in this process (<c>TTS source</c>,
/// 2026-09-16), for which the session first ensures <c>kokoro.onnx</c> through the
/// <see cref="ModelStore"/> (a download on first use, reported through the spinner label the way
/// <see cref="VoiceSession"/> reports Whisper's) and the synthesizer then loads it in
/// <see cref="ISpeechSynthesizer.PrepareAsync"/>; <see cref="BeginTurn"/> hands out a
/// <see cref="SpeechOutput"/> per reply and keeps it.
///
/// <para>The speaker outlives its turn: the reply's text ends the turn, and the audio still
/// owed — the <em>tail</em> — plays on under the input line (<see cref="Playing"/>). There is no
/// queue: the next reply, a timer alert, a sent line, ESC, the push-to-talk key, the wake phrase
/// and the app's end all stop it (<see cref="Stop"/> / <see cref="StopAsync"/>), and only the
/// screen's loop reads <see cref="Playing"/>.</para>
///
/// <para>Availability is a session fact, not a per-sentence retry: a server that fails mid-turn
/// costs one warning and is marked unavailable until the next probe (<c>/tts</c>, or a TTS field
/// edited in <c>/settings</c>), so a dead server never produces a warning per sentence.</para>
/// </summary>
internal sealed class SpeechSession : IDisposable
{
    /// <summary>The log category of every speech line.</summary>
    public const string Category = "Speech";

    /// <summary>The status lines lead with the strip's glyph (<see cref="ChatScreen.TtsGlyph"/>, 2026-09-18) so the line and the hint row read as one. Pinned.</summary>
    public const string OffLine = ChatScreen.TtsGlyph + " TTS: off (/tts to enable)";

    /// <summary>Printed (as a Warning) when the device did not drain within <see cref="SpeechOutput.DrainTimeout"/> after a stop.</summary>
    public const string DrainTimeoutWarning = "Playback did not finish in time; stopped.";

    /// <summary>Printed (as a Warning) when <see cref="BeginTurn"/> finds a speaker still owing audio: the screen should have stopped it at the input line.</summary>
    public const string TailSilencedWarning = "A new reply started while the last one was still playing; the tail was silenced.";

    /// <summary>The detail after a connect cancelled by the caller (Ctrl+C under the spinner, 2026-09-17): what the status line and the TTS tab say until the next connect.</summary>
    public const string ConnectCancelledDetail = "cancelled";

    private readonly Func<SynthesizerRequest, ISpeechSynthesizer> _synthesizerFactory;
    private readonly Func<PcmFormat, IAudioPlayback> _playbackFactory;
    private readonly ModelStore _models;
    private ISpeechSynthesizer? _synth;
    private SynthesizerRequest? _request;
    private IAudioPlayback? _playback;
    private SpeechOutput? _current;
    private CancellationTokenSource? _currentCts;

    public SpeechSession(Func<SynthesizerRequest, ISpeechSynthesizer> synthesizerFactory, Func<PcmFormat, IAudioPlayback> playbackFactory, ModelStore models)
    {
        _synthesizerFactory = synthesizerFactory ?? throw new ArgumentNullException(nameof(synthesizerFactory));
        _playbackFactory = playbackFactory ?? throw new ArgumentNullException(nameof(playbackFactory));
        _models = models ?? throw new ArgumentNullException(nameof(models));
    }

    /// <summary>The saved switch, as of the last connect.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Whether the server answered the last probe and has not failed since.</summary>
    public bool Available { get; private set; }

    public bool IsReady => Enabled && Available;

    /// <summary>The engine, as of the last connect.</summary>
    public TtsEngine Engine { get; private set; }

    /// <summary>What the status line names: the URL in use (normalised when valid), or <see cref="TtsSource.InProcessSource"/>.</summary>
    public string Source { get; private set; } = "";

    /// <summary>The primary voice (the compiled default when the saved one is blank).</summary>
    public string Voice { get; private set; } = "";

    /// <summary>The secondary voice, or empty when there is no mix.</summary>
    public string Voice2 { get; private set; } = "";

    /// <summary>The primary voice's share of the mix in percent (<see cref="AppSettingsData.TtsVoiceMix"/>).</summary>
    public int Mix { get; private set; } = 50;

    /// <summary>What is actually sent as <c>voice</c>: <see cref="VoiceMix.Spec"/> over the three above.</summary>
    public string VoiceSpec => VoiceMix.Spec(Voice, Voice2, Mix);

    public double Speed { get; private set; } = 1.0;

    /// <summary>What the server listed at the last probe; empty when it did not answer.</summary>
    public IReadOnlyList<string> Voices { get; private set; } = Array.Empty<string>();

    /// <summary>Why speech is unavailable, when it is.</summary>
    public string Detail { get; private set; } = "";

    /// <summary>Whether <see cref="StatusLine"/> is bad news (enabled but no server).</summary>
    public bool StatusIsWarning => Enabled && !Available;

    public static string ReadyLine(string url, string voice, double speed) =>
        $"{ChatScreen.TtsGlyph} TTS: {url} voice={voice} speed={SettingsMenu.Speed(speed)}";

    public static string NoServerLine(string url, string detail) =>
        $"{ChatScreen.TtsGlyph} TTS: no server at {url} ({detail}); speech off until /tts";

    /// <summary>The in-process engine's bad news: the model missing or not downloadable, the runtime or the voices missing beside the exe.</summary>
    public static string NotReadyLine(string detail) =>
        $"{ChatScreen.TtsGlyph} TTS: {TtsSource.InProcessSource} is not ready ({detail}); speech off until /tts";

    /// <summary>The one-line status the screen prints after every connect.</summary>
    public string StatusLine() =>
        !Enabled ? OffLine
        : Available ? ReadyLine(Source, VoiceSpec, Speed)
        : Engine == TtsEngine.InProcess ? NotReadyLine(Detail)
        : NoServerLine(Source, Detail);

    /// <summary>
    /// The request the factory would get for <paramref name="saved"/>: the normalised URL over
    /// HTTP (null when it is not usable), the model's path in-process — the same value the
    /// session keeps from its connect, so the picker and the preview can compare.
    /// </summary>
    public SynthesizerRequest? RequestFor(AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (TtsSource.Resolve(saved) == TtsEngine.InProcess)
        {
            return SynthesizerRequest.InProcess(_models.Kokoro().Path);
        }

        try
        {
            return SynthesizerRequest.Http(LlmEndpoint.NormalizeBaseUrl(saved.TtsHttpUrl ?? ""));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Drops the current synthesizer, takes the switch, source, voice and speed from
    /// <paramref name="effective"/>, and — only when enabled — readies the source: over HTTP a
    /// probe of the server; in-process the model file ensured (a download reported through
    /// <paramref name="phase"/>, which may be null) and loaded. Never throws except for the
    /// caller's own cancellation of a download or load, which leaves the session not ready with
    /// <see cref="ConnectCancelledDetail"/> as its detail.
    /// </summary>
    public async Task ConnectAsync(AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        try
        {
            await ConnectCoreAsync(effective, phase, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A download or load cut short (Ctrl+C under the spinner): the status says so, not an empty detail.
            Available = false;
            Detail = ConnectCancelledDetail;
            throw;
        }
    }

    private async Task ConnectCoreAsync(AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        // Not ready before the first await (2026-09-29): the connect runs behind the input line now, and a reply or an alert
        // must not begin a speaker on the synthesizer disposed below while the stop is awaited.
        Available = false;

        // A tail still playing would be speaking through the client disposed next.
        await StopAsync().ConfigureAwait(false);
        _synth?.Dispose();
        _synth = null;
        _request = null;

        Enabled = effective.TtsOutput;
        Engine = TtsSource.Resolve(effective);
        Voice = string.IsNullOrWhiteSpace(effective.TtsVoice) ? new AppSettingsData().TtsVoice : effective.TtsVoice.Trim();
        Voice2 = (effective.TtsVoice2 ?? "").Trim();
        Mix = effective.TtsVoiceMix;
        Speed = effective.TtsSpeed;
        Source = Engine == TtsEngine.InProcess ? TtsSource.InProcessSource : effective.TtsHttpUrl ?? "";
        Available = false;
        Voices = Array.Empty<string>();
        Detail = "";

        if (!Enabled)
        {
            return;
        }

        SynthesizerRequest request;
        if (Engine == TtsEngine.InProcess)
        {
            var spec = _models.Kokoro();
            var model = await _models.EnsureAsync(spec, VoiceSession.Progress(phase, spec.Display, spec.ApproxBytes), cancellationToken).ConfigureAwait(false);
            if (!model.Ok)
            {
                Detail = spec.Display + ": " + model.Detail;
                DiagnosticLog.Error(Category, $"Kokoro in-process: {Detail}");
                return;
            }

            phase?.Invoke(VoiceSession.LoadingLabel(spec.Display));
            request = SynthesizerRequest.InProcess(model.Path);
        }
        else
        {
            Uri v1;
            try
            {
                v1 = LlmEndpoint.NormalizeBaseUrl(Source);
            }
            catch (ArgumentException ex)
            {
                Detail = "not a valid URL";
                DiagnosticLog.Error(Category, $"The TTS HTTP URL is not usable: {ex.Message}");
                return;
            }

            Source = v1.AbsoluteUri;
            request = SynthesizerRequest.Http(v1);
        }

        try
        {
            _synth = _synthesizerFactory(request);
        }
        catch (Exception ex)
        {
            Detail = ex.Message;
            DiagnosticLog.Error(Category, $"Could not create the speech client: {Assistant.Explain(ex)}");
            return;
        }

        _request = request;
        var result = await _synth.PrepareAsync(cancellationToken).ConfigureAwait(false);
        Available = result.Exists;
        Voices = result.Voices;
        Detail = result.Detail;

        if (Available)
        {
            DiagnosticLog.Info(Category, ReadyLogLine(request.Source, Voice, Voice2, Mix, Speed, Voices.Count));
        }

        if (Available && Voices.Count > 0)
        {
            foreach (var name in VoiceMix.Voices(Voice, Voice2, Mix))
            {
                if (!Voices.Contains(name, StringComparer.Ordinal))
                {
                    DiagnosticLog.Warn(Category, $"The TTS source does not list the voice '{name}'; pick one in /settings if synthesis fails.");
                }
            }
        }
    }

    /// <summary>
    /// The voices for <paramref name="request"/> (<see cref="RequestFor"/>), for the picker.
    /// Reuses the connected synthesizer when the request is the one it connected with, else a
    /// throwaway one — cheap either way, <see cref="ISpeechSynthesizer.ListVoicesAsync"/> never
    /// loads a model; null for a null request (an unusable URL).
    /// </summary>
    public async Task<VoiceListResult?> ListVoicesAsync(SynthesizerRequest? request, CancellationToken cancellationToken)
    {
        if (request is not { } wanted)
        {
            return null;
        }

        if (_synth is not null && _request == wanted)
        {
            return await _synth.ListVoicesAsync(cancellationToken).ConfigureAwait(false);
        }

        using var synth = _synthesizerFactory(wanted);
        return await synth.ListVoicesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Speech is off for the rest of the session (until the next probe). Called from the consumer thread.</summary>
    public void MarkUnavailable(string reason)
    {
        Available = false;
        Detail = reason;
    }

    /// <summary>
    /// A <see cref="SpeechOutput"/> for one reply, or null when speech is not ready (or the audio
    /// device cannot be opened, which also marks the session unavailable). Its token is the
    /// session's own, linked to <paramref name="appToken"/>: the turn that feeds it cancels it
    /// through <see cref="Stop"/> while it runs, and the screen stops the tail at the input line
    /// (the screen awaits <see cref="StopAsync"/> first; a speaker still owing audio here is
    /// silenced as a safety net, the device being one, and a drained one is only forgotten).
    /// </summary>
    public SpeechOutput? BeginTurn(CancellationToken appToken) =>
        _synth is { } synth && IsReady ? Begin(synth, VoiceSpec, Speed, appToken) : null;

    /// <summary>
    /// <see cref="BeginTurn(CancellationToken)"/> in another voice: <c>/botchat</c>'s (2026-09-24), each bot
    /// speaking in its own profile's <paramref name="voice"/> (a <see cref="VoiceMix.Spec"/>) at its
    /// <paramref name="speed"/> through the one synthesizer this session connected — a profile's own
    /// <c>TTS source</c> is never read. A blank voice is the session's own.
    /// </summary>
    public SpeechOutput? BeginTurn(CancellationToken appToken, string voice, double speed) =>
        _synth is { } synth && IsReady ? Begin(synth, string.IsNullOrWhiteSpace(voice) ? VoiceSpec : voice, speed, appToken) : null;

    /// <summary>
    /// A speaker for the settings menu's preview: <paramref name="voice"/> as given (a name alone
    /// for the pickers, the blend for the mix and speed rows) at <paramref name="speed"/>, through
    /// the connected synthesizer — so null when speech is not ready or <paramref name="request"/>
    /// (<see cref="RequestFor"/>) is not the one the session connected with (a <c>TTS HTTP URL</c>
    /// or <c>TTS source</c> edited in the same menu session lists from the new source; it speaks
    /// after the reconnect).
    /// The caller has awaited <see cref="StopAsync"/> for the previous preview; the tail rules
    /// then apply to this speaker as to any other.
    /// </summary>
    public SpeechOutput? BeginPreview(SynthesizerRequest? request, string voice, double speed, CancellationToken appToken)
    {
        if (_synth is null || !IsReady || string.IsNullOrWhiteSpace(voice) || request is not { } wanted || _request != wanted)
        {
            return null;
        }

        return Begin(_synth, voice, speed, appToken);
    }

    private SpeechOutput? Begin(ISpeechSynthesizer synth, string voice, double speed, CancellationToken appToken)
    {
        if (Playing is not null)
        {
            DiagnosticLog.Warn(Category, TailSilencedWarning);
            Stop();
        }

        Forget();
        DiagnosticLog.Debug(Category, SpeakingLogLine(voice, speed));

        try
        {
            _playback ??= _playbackFactory(synth.Format);
        }
        catch (Exception ex)
        {
            MarkUnavailable("audio output unavailable: " + ex.Message);
            DiagnosticLog.Warn(Category, "Audio output unavailable: " + Assistant.Explain(ex));
            return null;
        }

        _currentCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        _current = new SpeechOutput(synth, _playback, voice, speed, _currentCts.Token, MarkUnavailable);
        return _current;
    }

    /// <summary>The speaker still owed audio — under its turn or as the tail under the input line — else null.</summary>
    public SpeechOutput? Playing => _current is { Completion.IsCompleted: false } ? _current : null;

    /// <summary>
    /// Silences the current speaker now: its token is cancelled, and the queue's cancellation
    /// path drops what is queued and stops the device. Idempotent, never throws; the turn's
    /// cancel registration (ESC, the wake phrase, the app token) and <see cref="Dispose"/> use
    /// this form, everything at the input line the awaited one.
    /// </summary>
    public void Stop()
    {
        if (_currentCts is { } cts)
        {
            VoiceSession.SafeCancel(cts);
        }
    }

    /// <summary>
    /// <see cref="Stop"/>, then waits for the consumer to be done (bounded by
    /// <see cref="SpeechOutput.DrainTimeout"/>, so a device that never drains cannot hold the
    /// screen) before the speaker is forgotten and its echo probe disposed — the caller has
    /// disarmed the listener that reads the probe by then. True when the speaker was silenced
    /// with audio still owed (<see cref="SpeechOutput.StoppedEarly"/>); false when it had
    /// finished or nothing was playing. Never throws: a timeout or a failure is a log line the
    /// screen prints as a warning.
    /// </summary>
    public async Task<bool> StopAsync()
    {
        if (_current is not { } speaker)
        {
            return false;
        }

        bool stoppedEarly = false;
        Stop();
        try
        {
            // The queue's cancellation path silences the device when it sees the token (and a
            // speaker that had drained has nothing to silence); after a timeout or a fault nothing did.
            await speaker.Completion.WaitAsync(SpeechOutput.DrainTimeout).ConfigureAwait(false);
            stoppedEarly = speaker.StoppedEarly;
            DiagnosticLog.Debug(Category, stoppedEarly ? StoppedLogLine(speaker.PlayedBytes, speaker.WrittenBytes) : DoneLogLine(speaker.WrittenBytes));
        }
        catch (TimeoutException)
        {
            DiagnosticLog.Warn(Category, DrainTimeoutWarning);
            speaker.StopAll();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(Category, "Speech failed: " + Assistant.Explain(ex), ex);
            speaker.StopAll();
        }

        Forget();
        return stoppedEarly;
    }

    /// <summary>The TTS connect's line: <c>TTS ready: in-process, voice af_heart + am_eric 80%, speed 1.2, 54 voices listed</c>. Pinned.</summary>
    public static string ReadyLogLine(string source, string voice, string voice2, int mix, double speed, int voices)
    {
        string blend = string.IsNullOrWhiteSpace(voice2) ? voice : string.Create(CultureInfo.InvariantCulture, $"{voice} + {voice2} {mix}%");
        return string.Create(CultureInfo.InvariantCulture, $"TTS ready: {source}, voice {blend}, speed {speed}, {voices} voices listed");
    }

    /// <summary>A speaker begun: <c>Speaking: voice af_heart, speed 1.2</c>. Pinned.</summary>
    public static string SpeakingLogLine(string voice, double speed) => string.Create(CultureInfo.InvariantCulture, $"Speaking: voice {voice}, speed {speed}");

    /// <summary>A speaker silenced with audio owed: <c>Speech stopped: 48,000 of 96,000 bytes played</c>. Pinned.</summary>
    public static string StoppedLogLine(long played, long written) => string.Create(CultureInfo.InvariantCulture, $"Speech stopped: {played:N0} of {written:N0} bytes played");

    /// <summary>A speaker that drained on its own: <c>Speech done: 96,000 bytes</c>. Pinned.</summary>
    public static string DoneLogLine(long written) => string.Create(CultureInfo.InvariantCulture, $"Speech done: {written:N0} bytes");

    private void Forget()
    {
        _current?.Probe?.Dispose();
        _current = null;
        _currentCts?.Dispose();
        _currentCts = null;
    }

    public void Dispose()
    {
        Stop();
        Forget();
        _synth?.Dispose();
        _synth = null;
        _request = null;
        _playback?.Dispose();
        _playback = null;
    }
}
