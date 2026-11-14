using System.Globalization;
using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.WebUi;

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
///
/// <para><b>A Web UI page speaks too (2026-11-13, the webui-voice plan).</b> Kokoro loads when <c>TtsOutput</c> is on or the Web UI wants
/// voice (<see cref="WebVoiceWanted"/>); <see cref="Enabled"/> stays the PC's switch, so <see cref="IsReady"/> is false at the PC while
/// <see cref="Available"/> serves the page. A page's reply begins with its own playback (<see cref="BeginTurn(CancellationToken, IAudioPlayback?)"/>),
/// which the session owns until that speaker's consumer is done. One speaker at a time, as ever; <see cref="Stop(SpeechOutput)"/> stops a
/// named one only, so a stop decided for an older speaker never reaches a newer one.</para>
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
    public const string ConnectCancelledDetail = "canceled";

    private readonly Func<SynthesizerRequest, ISpeechSynthesizer> _synthesizerFactory;
    private readonly Func<PcmFormat, IAudioPlayback> _playbackFactory;
    private readonly ModelStore _models;
    private ISpeechSynthesizer? _synth;
    private SynthesizerRequest? _request;
    private IAudioPlayback? _playback;
    private Speaker? _cur;
    private volatile bool _available;

    /// <summary>The current speaker with its own token source and, for a page's, its page (2026-11-13): swapped as one reference.</summary>
    private sealed record Speaker(SpeechOutput Output, CancellationTokenSource Cts, string? Page);

    public SpeechSession(Func<SynthesizerRequest, ISpeechSynthesizer> synthesizerFactory, Func<PcmFormat, IAudioPlayback> playbackFactory, ModelStore models)
    {
        _synthesizerFactory = synthesizerFactory ?? throw new ArgumentNullException(nameof(synthesizerFactory));
        _playbackFactory = playbackFactory ?? throw new ArgumentNullException(nameof(playbackFactory));
        _models = models ?? throw new ArgumentNullException(nameof(models));
    }

    /// <summary>The saved switch, as of the last connect.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Whether the server answered the last probe and has not failed since. Read from Kestrel threads (the Web UI), so volatile.</summary>
    public bool Available { get => _available; private set => _available = value; }

    public bool IsReady => Enabled && Available;

    /// <summary>Whether the Web UI wanted speech at the last connect (<see cref="WebVoiceMode.Wanted"/>), kept as <see cref="Enabled"/> is.</summary>
    public bool WebVoiceWanted { get; private set; }

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

    /// <summary>Whether <see cref="StatusLine"/> is bad news: wanted, by the PC or the Web UI, but not available.</summary>
    public bool StatusIsWarning => (Enabled || WebVoiceWanted) && !Available;

    public static string ReadyLine(string url, string voice, double speed) =>
        $"{ChatScreen.TtsGlyph} TTS: {url} voice={voice} speed={SettingsMenu.Speed(speed)}";

    public static string NoServerLine(string url, string detail) =>
        $"{ChatScreen.TtsGlyph} TTS: no server at {url} ({detail}); speech off until /tts";

    /// <summary>
    /// The <c>http</c> source with no <see cref="AppSettingsData.TtsHttpUrl"/> (2026-10-05: empty is its default since, as ComfyUI URL's),
    /// in <see cref="NoServerLine"/>'s place, which would read "no server at  (…)". Pinned.
    /// </summary>
    public static readonly string NoUrlLine =
        $"{ChatScreen.TtsGlyph} TTS: http has no HTTP URL (/settings › Voice › TTS › HTTP URL); speech off until /tts";

    /// <summary>The detail an <c>http</c> connect without a URL leaves (<see cref="NoUrlLine"/>).</summary>
    public const string NoUrlDetail = "no HTTP URL is set";

    /// <summary>The in-process engine's bad news: the model missing or not downloadable, the runtime or the voices missing beside the exe.</summary>
    public static string NotReadyLine(string detail) =>
        $"{ChatScreen.TtsGlyph} TTS: {TtsSource.InProcessSource} is not ready ({detail}); speech off until /tts";

    /// <summary>The one-line status the screen prints after every connect.</summary>
    public string StatusLine() =>
        !Enabled ? (WebVoiceWanted && !Available ? WebFailureLine(FailureDetail()) : OffLine)
        : Available ? ReadyLine(Source, VoiceSpec, Speed)
        : Engine == TtsEngine.InProcess ? NotReadyLine(Detail)
        : string.IsNullOrWhiteSpace(Source) ? NoUrlLine
        : NoServerLine(Source, Detail);

    /// <summary>
    /// Speech failed with only the Web UI wanting it (2026-11-13): the PC's lines end "speech off until /tts", the wrong fix, since
    /// <c>/tts on</c> turns the PC's speech on too. Pinned.
    /// </summary>
    public static string WebFailureLine(string detail) => $"{ChatScreen.TtsGlyph} TTS for the Web UI: not ready ({detail}); {WebUiText.VoiceRetryFix}";

    /// <summary>What failed, as the PC's lines would say it: the source with its detail.</summary>
    public string FailureDetail() =>
        Engine == TtsEngine.InProcess ? TtsSource.InProcessSource + ": " + Detail
        : string.IsNullOrWhiteSpace(Source) ? NoUrlDetail
        : "no server at " + Source + (Detail.Length > 0 ? ": " + Detail : "");

    /// <summary>
    /// Whether turning speech on under <paramref name="effective"/> would download Kokoro first (2026-10-19, the Web UI review: the page's
    /// <c>/tts on</c> may not start a first-use download): the in-process engine with its model missing. An HTTP server downloads nothing.
    /// </summary>
    public bool WouldDownload(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return TtsSource.Resolve(effective) == TtsEngine.InProcess && !ModelStore.IsPresent(_models.Kokoro());
    }

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
        bool webWanted = WebVoiceMode.Wanted(effective);
        if (_synth is not null && Available && webWanted && WebVoiceWanted && SameVoice(effective))
        {
            // A PC switch flip (2026-11-13, the webui-voice plan): the synthesizer stays wanted and the same, so it stays, with a page's
            // speaker on it; only the PC's switch changes.
            Enabled = effective.TtsOutput;
            return;
        }

        // Not ready before the first await (2026-09-29): the connect runs behind the input line now, and a reply or an alert
        // must not begin a speaker on the synthesizer disposed below while the stop is awaited.
        Available = false;

        // A tail still playing would be speaking through the client disposed next.
        await StopAsync().ConfigureAwait(false);
        _synth?.Dispose();
        _synth = null;
        _request = null;

        Enabled = effective.TtsOutput;
        WebVoiceWanted = webWanted;
        Engine = TtsSource.Resolve(effective);
        Voice = string.IsNullOrWhiteSpace(effective.TtsVoice) ? new AppSettingsData().TtsVoice : effective.TtsVoice.Trim();
        Voice2 = (effective.TtsVoice2 ?? "").Trim();
        Mix = effective.TtsVoiceMix;
        Speed = effective.TtsSpeed;
        Source = Engine == TtsEngine.InProcess ? TtsSource.InProcessSource : effective.TtsHttpUrl ?? "";
        Available = false;
        Voices = Array.Empty<string>();
        Detail = "";

        if (!Enabled && !WebVoiceWanted)
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
            if (string.IsNullOrWhiteSpace(Source))
            {
                Detail = NoUrlDetail;
                DiagnosticLog.Warn(Category, "The TTS source is http but no TTS HTTP URL is set.");
                return;
            }

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

    /// <summary>Whether <paramref name="effective"/> asks for the engine, source, voice and speed this session connected with (a switch flip changes none).</summary>
    private bool SameVoice(AppSettingsData effective)
    {
        var engine = TtsSource.Resolve(effective);
        string voice = string.IsNullOrWhiteSpace(effective.TtsVoice) ? new AppSettingsData().TtsVoice : effective.TtsVoice.Trim();
        string source = engine == TtsEngine.InProcess ? TtsSource.InProcessSource : effective.TtsHttpUrl ?? "";
        bool sameSource = engine == TtsEngine.InProcess || RequestFor(effective) == _request;
        return engine == Engine && sameSource && voice == Voice && (effective.TtsVoice2 ?? "").Trim() == Voice2 && effective.TtsVoiceMix == Mix
            && effective.TtsSpeed.Equals(Speed) && (engine == TtsEngine.InProcess || source.Length > 0);
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
    public SpeechOutput? BeginTurn(CancellationToken appToken) => BeginTurn(appToken, playback: null);

    /// <summary>
    /// <see cref="BeginTurn(CancellationToken)"/>, speaking through <paramref name="playback"/> when given (2026-11-13: a Web UI page's
    /// <c>WebAudioPlayback</c>, an <see cref="IPageAudio"/>), which the session then owns: it is disposed once its speaker's consumer is done, or at once when
    /// no speaker is made. With a playback the gate is <see cref="Available"/> (the page's own 🔊 was the switch); without, <see cref="IsReady"/>.
    /// </summary>
    public SpeechOutput? BeginTurn(CancellationToken appToken, IAudioPlayback? playback) =>
        BeginTurn(appToken, voice: "", Speed, playback);

    /// <summary>
    /// <see cref="BeginTurn(CancellationToken)"/> in another voice: <c>/botchat</c>'s (2026-09-24), each bot
    /// speaking in its own profile's <paramref name="voice"/> (a <see cref="VoiceMix.Spec"/>) at its
    /// <paramref name="speed"/> through the one synthesizer this session connected — a profile's own
    /// <c>TTS source</c> is never read. A blank voice is the session's own.
    /// </summary>
    public SpeechOutput? BeginTurn(CancellationToken appToken, string voice, double speed) => BeginTurn(appToken, voice, speed, playback: null);

    /// <summary><see cref="BeginTurn(CancellationToken, string, double)"/> through a page's <paramref name="playback"/> (a bot of a chat started from the page, 2026-11-13).</summary>
    public SpeechOutput? BeginTurn(CancellationToken appToken, string voice, double speed, IAudioPlayback? playback)
    {
        if (_synth is { } synth && (playback is null ? IsReady : Available)
            && Begin(synth, string.IsNullOrWhiteSpace(voice) ? VoiceSpec : voice, speed, appToken, playback) is { } speaker)
        {
            return speaker;
        }

        playback?.Dispose();
        return null;
    }

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

    private SpeechOutput? Begin(ISpeechSynthesizer synth, string voice, double speed, CancellationToken appToken, IAudioPlayback? owned = null)
    {
        if (Playing is not null)
        {
            DiagnosticLog.Warn(Category, TailSilencedWarning);
            Stop();
        }

        Forget();
        DiagnosticLog.Debug(Category, SpeakingLogLine(voice, speed));

        IAudioPlayback playback;
        if (owned is not null)
        {
            playback = owned;
        }
        else
        {
            try
            {
                playback = _playback ??= _playbackFactory(synth.Format);
            }
            catch (Exception ex)
            {
                MarkUnavailable("audio output unavailable: " + ex.Message);
                DiagnosticLog.Warn(Category, "Audio output unavailable: " + Assistant.Explain(ex));
                return null;
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        var output = new SpeechOutput(synth, playback, voice, speed, cts.Token, MarkUnavailable);
        _cur = new Speaker(output, cts, (owned as IPageAudio)?.Page);
        if (owned is not null)
        {
            // Disposed once this speaker's consumer is done with it, never in Forget: a newer Begin forgets an older tail at once, while
            // its consumer may still be about to clear the playback (and a page's clear sends its voice-stop).
            _ = output.Completion.ContinueWith(static (_, state) => ((IAudioPlayback)state!).Dispose(), owned, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        Began?.Invoke(output);
        return output;
    }

    /// <summary>
    /// Told each speaker as it is made, a reply's, a bot's or a preview's (2026-10-05: the video window pauses from its first
    /// audio, <c>VideoVoicePause</c>); on the caller's thread, before the speaker is fed. The screen sets it; null by default.
    /// </summary>
    public Action<SpeechOutput>? Began { get; set; }

    /// <summary>The speaker still owed audio — under its turn or as the tail under the input line — else null.</summary>
    public SpeechOutput? Playing => _cur is { Output.Completion.IsCompleted: false } cur ? cur.Output : null;

    /// <summary>The Web UI page the speaker <see cref="Playing"/> speaks on (2026-11-13), or null for the PC's own or none.</summary>
    public string? PlayingPage => _cur is { Output.Completion.IsCompleted: false } cur ? cur.Page : null;

    /// <summary>
    /// Silences the current speaker now: its token is cancelled, and the queue's cancellation
    /// path (or, once the queue had drained, the speaker's own wait) drops what is queued and
    /// stops the device. Idempotent, never throws; the turn's
    /// cancel registration (ESC, the wake phrase, the app token) and <see cref="Dispose"/> use
    /// this form, everything at the input line the awaited one.
    /// </summary>
    public void Stop()
    {
        if (_cur is { } cur)
        {
            VoiceSession.SafeCancel(cur.Cts);
        }
    }

    /// <summary>
    /// Silences <paramref name="which"/> only (2026-11-13, the webui-voice plan): a stop decided on another thread for a speaker a newer
    /// <see cref="Begin"/> has since replaced cancels that older speaker's own source and never the newer one's. Idempotent, never throws.
    /// </summary>
    public void Stop(SpeechOutput which)
    {
        ArgumentNullException.ThrowIfNull(which);
        if (_cur is { } cur && ReferenceEquals(cur.Output, which))
        {
            VoiceSession.SafeCancel(cur.Cts);
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
        if (_cur?.Output is not { } speaker)
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
        if (Interlocked.Exchange(ref _cur, null) is { } cur)
        {
            cur.Output.Probe?.Dispose();
            cur.Cts.Dispose();
        }
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
