using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Speech;

namespace NeonSidekick.App;

/// <summary>
/// The microphone, the models and the recognizer for one interactive screen: the voice-input
/// counterpart of <see cref="SpeechSession"/>. <see cref="ConnectAsync"/> takes the effective
/// settings and, only when voice input is enabled, counts microphones, makes sure both ggml
/// models are on disk (downloading under the caller's spinner) and loads them;
/// <see cref="ListenAsync"/> runs one push-to-talk utterance.
///
/// <para>Availability is a session fact: probed at connect and after <c>/stt</c> or a voice
/// field in <c>/settings</c>; a failure while listening or transcribing marks the session
/// unavailable until the next probe, so a dead microphone costs one warning, not one per
/// key press. With the switch off nothing is touched: no device count, no disk, no HTTP.</para>
///
/// <para><b>The wake word (M5) is a second, subordinate fact.</b> When <c>SttWake</c> is
/// also on, connect stages the Vosk model and loads the detector after the voice models; a
/// failure there leaves push-to-talk ready and the wake word unavailable, with its own line.
/// <see cref="ArmWake"/> opens the microphone through a <see cref="WakeListener"/> while the input
/// line waits, cancelling the given source (from the thread pool, never the capture thread) when
/// the phrase is heard; <see cref="DisarmWake"/> closes it and hands back what was heard.</para>
/// </summary>
internal sealed class VoiceSession : IDisposable
{
    /// <summary>The log category of every voice line.</summary>
    public const string Category = "Voice";

    /// <summary>The status lines lead with the strip's glyphs (<see cref="ChatScreen.SttGlyph"/> and, on the ready line, the wake word's and the interrupt's after it, 2026-09-18) so the line and the hint row read as one. Pinned.</summary>
    public const string OffLine = ChatScreen.SttGlyph + " STT: off (/stt to enable)";

    public const string UnpackingLabel = "unpacking vosk model…";

    /// <summary>The detail after a connect cancelled by the caller (Ctrl+C under the spinner, 2026-09-17): unavailable with this reason until the next connect, never "off" while enabled.</summary>
    public const string ConnectCancelledDetail = "cancelled";

    private enum State
    {
        Off,
        Ready,
        NoMicrophone,
        ModelMissing,
        Unavailable,
    }

    private readonly Func<PcmFormat, IAudioCapture> _captureFactory;
    private readonly Func<string, ISpeechRecognizer> _recognizerFactory;
    private readonly Func<string, VadOptions, IVoiceActivityDetector> _vadFactory;
    private readonly ModelStore _models;
    private readonly Func<int> _inputDeviceCount;
    private readonly Func<string, string, IWakeWordDetector> _wakeDetectorFactory;

    private IAudioCapture? _capture;
    private ISpeechRecognizer? _recognizer;
    private IVoiceActivityDetector? _vad;
    private VoicePipeline? _pipeline;
    private IWakeWordDetector? _wakeDetector;
    private IWakeWordDetector? _echoDetector;
    private WakeListener? _wakeListener;
    private State _state = State.Off;
    private string _missingModel = "";

    /// <param name="captureFactory">Builds the microphone for a format; <see cref="WinMmAudioCapture"/> in the app, a fake in tests.</param>
    /// <param name="recognizerFactory">Builds the recognizer for a model path.</param>
    /// <param name="vadFactory">Builds the voice activity detector for a model path and options.</param>
    /// <param name="models">Where the ggml files live and come from.</param>
    /// <param name="inputDeviceCount">How many microphones Windows reports; <see cref="WinMmAudioCapture.InputDeviceCount"/> in the app.</param>
    /// <param name="wakeDetectorFactory">Builds the wake-word recogniser for a model directory and phrase; <see cref="VoskWakeWordDetector"/> in the app.</param>
    public VoiceSession(
        Func<PcmFormat, IAudioCapture> captureFactory,
        Func<string, ISpeechRecognizer> recognizerFactory,
        Func<string, VadOptions, IVoiceActivityDetector> vadFactory,
        ModelStore models,
        Func<int> inputDeviceCount,
        Func<string, string, IWakeWordDetector> wakeDetectorFactory)
    {
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _recognizerFactory = recognizerFactory ?? throw new ArgumentNullException(nameof(recognizerFactory));
        _vadFactory = vadFactory ?? throw new ArgumentNullException(nameof(vadFactory));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _inputDeviceCount = inputDeviceCount ?? throw new ArgumentNullException(nameof(inputDeviceCount));
        _wakeDetectorFactory = wakeDetectorFactory ?? throw new ArgumentNullException(nameof(wakeDetectorFactory));
    }

    /// <summary>The saved switch, as of the last connect.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Whether a microphone was found, both models loaded, and nothing has failed since.</summary>
    public bool Available { get; private set; }

    public bool IsReady => Enabled && Available;

    /// <summary>Whether <see cref="StatusLine"/> is bad news (enabled but not available).</summary>
    public bool StatusIsWarning => Enabled && !Available;

    /// <summary>The <c>SttWhisperModel</c> setting in force.</summary>
    public string Model { get; private set; } = "";

    /// <summary>Where the Whisper model was found or downloaded; empty until ready.</summary>
    public string ModelPath { get; private set; } = "";

    /// <summary>Why voice input is unavailable, when it is.</summary>
    public string Detail { get; private set; } = "";

    public ConsoleKey PushToTalk { get; private set; } = ConsoleKey.F4;

    public string PushToTalkName => PushToTalk.ToString();

    /// <summary>Timeouts for the next listen; the voice check shortens them.</summary>
    public VoicePipelineOptions PipelineOptions { get; set; } = VoicePipelineOptions.Default;

    // ── Wake word ───────────────────────────────────────────────────────────

    /// <summary>The saved <c>SttWake</c> switch, as of the last connect.</summary>
    public bool WakeEnabled { get; private set; }

    /// <summary>Whether the Vosk model is staged, the detector loaded, and nothing has failed since.</summary>
    public bool WakeAvailable { get; private set; }

    /// <summary>Whether the input line should listen for the phrase: voice ready, wake on and available.</summary>
    public bool WakeReady => IsReady && WakeEnabled && WakeAvailable;

    /// <summary>Whether <see cref="WakeStatusLine"/> should be printed as a warning: voice ready, wake on, wake not available.</summary>
    public bool WakeStatusIsWarning => IsReady && WakeEnabled && !WakeAvailable;

    /// <summary>The normalised wake phrase in force.</summary>
    public string WakePhrase { get; private set; } = "";

    /// <summary>Why the wake word is unavailable, when it is.</summary>
    public string WakeDetail { get; private set; } = "";

    /// <summary>The <c>SttVoskModel</c> setting in force (the default for a blank one).</summary>
    public string WakeModel { get; private set; } = "";

    /// <summary>Where the Vosk model was found or unpacked; empty until ready.</summary>
    public string WakeModelPath { get; private set; } = "";

    // ── Interrupt (M6) ──────────────────────────────────────────────────────

    /// <summary>The saved <c>InterruptEnabled</c> switch, as of the last connect.</summary>
    public bool InterruptEnabled { get; private set; }

    /// <summary>Whether interrupting has not been switched off for the session (the backstop, or a microphone failure while armed).</summary>
    public bool InterruptAvailable { get; private set; }

    /// <summary>Whether a spoken turn should listen for the phrase: voice ready, the switch on, the Vosk model loaded, not switched off.</summary>
    public bool InterruptReady => IsReady && InterruptEnabled && WakeAvailable && InterruptAvailable;

    /// <summary>Whether <see cref="InterruptStatusLine"/> should be printed as a warning.</summary>
    public bool InterruptStatusIsWarning => IsReady && InterruptEnabled && !(WakeAvailable && InterruptAvailable);

    /// <summary>Why interrupting is unavailable, when it is (the Vosk detail when the model failed).</summary>
    public string InterruptDetail { get; private set; } = "";

    /// <summary>Timeouts for the listen that follows an interruption: the user has just cut the assistant off and is about to speak.</summary>
    public static VoicePipelineOptions InterruptionOptions { get; } = VoicePipelineOptions.Default with { NoSpeechTimeout = TimeSpan.FromSeconds(4) };

    /// <summary>The options the screen passes to that listen; <see cref="InterruptionOptions"/> unless a test shortens them.</summary>
    public VoicePipelineOptions InterruptOptions { get; set; } = InterruptionOptions;

    // ── Pinned wording ──────────────────────────────────────────────────────

    /// <summary>
    /// The ready line; with <paramref name="wakePhrase"/> (the wake word listening) it also says
    /// the phrase to say, and whether it cuts a spoken reply short too (<paramref name="interrupt"/>).
    /// Its lead is the strip's own rule (<see cref="ChatScreen.SpeechGlyphs"/>: 🎤, then 👂 with the
    /// phrase, then ✋ with the interrupt — the same glyphs in the same order as the hint row).
    /// </summary>
    public static string ReadyLine(string key, string? wakePhrase = null, bool interrupt = false) =>
        ChatScreen.SpeechGlyphs(ttsOn: false, sttOn: true, wakeReady: wakePhrase is not null, interruptReady: interrupt) + " " + (wakePhrase is null
            ? $"STT: {key} to talk"
            : $"STT: {key} to talk or say \"{wakePhrase}\"" + (interrupt ? " (interrupt enabled)" : ""));

    /// <summary>The interrupt's own line when voice is ready but interrupting is not.</summary>
    public static string InterruptUnavailableLine(string detail) => $"{ChatScreen.InterruptGlyph} Interrupt: unavailable ({detail}); push-to-talk still works, /interrupt on retries";

    public static string NoMicrophoneLine(string detail) => $"{ChatScreen.SttGlyph} STT: no microphone ({detail}); voice off until /stt";

    public static string ModelMissingLine(string model, string detail) => $"{ChatScreen.SttGlyph} STT: model {model} missing ({detail}); voice off until /stt";

    public static string UnavailableLine(string detail) => $"{ChatScreen.SttGlyph} STT: unavailable ({detail}); voice off until /stt";

    /// <summary>The wake word's own line when voice is ready but the wake word is not.</summary>
    public static string WakeUnavailableLine(string detail) => $"{ChatScreen.WakeGlyph} Wake word: unavailable ({detail}); push-to-talk still works, /stt retries";

    /// <summary>The spinner label while a model downloads; <paramref name="percent"/> is appended when the server said how big it is.</summary>
    public static string DownloadLabel(string display, long bytes, int? percent = null) =>
        $"downloading {display} ({ModelStore.SizeLabel(bytes)})…" + (percent is { } p ? $" {p}%" : "");

    public static string LoadingLabel(string display) => $"loading {display}…";

    /// <summary>The saved key name as a <see cref="ConsoleKey"/>; an unusable name warns once and falls back to F4.</summary>
    public static ConsoleKey ParsePushToTalk(string? name)
    {
        if (Enum.TryParse<ConsoleKey>((name ?? "").Trim(), ignoreCase: true, out var key) && Enum.IsDefined(key) && SettingsMenu.IsPushToTalkCandidate(key))
        {
            return key;
        }

        DiagnosticLog.Warn(Category, $"Push-to-talk key '{name}' {SettingsMenu.PushToTalkKeyError}; using F4.");
        return ConsoleKey.F4;
    }

    /// <summary>The one-line status the screen prints after every connect.</summary>
    public string StatusLine() => _state switch
    {
        State.Off => OffLine,
        State.Ready => ReadyLine(PushToTalkName, WakeReady ? WakePhrase : null, WakeReady && InterruptReady),
        State.NoMicrophone => NoMicrophoneLine(Detail),
        State.ModelMissing => ModelMissingLine(_missingModel, Detail),
        _ => UnavailableLine(Detail),
    };

    /// <summary>The wake word's line, when it has one to print (<see cref="WakeStatusIsWarning"/>); otherwise null.</summary>
    public string? WakeStatusLine() => WakeStatusIsWarning ? WakeUnavailableLine(WakeDetail) : null;

    /// <summary>The interrupt's line, when it has one to print (<see cref="InterruptStatusIsWarning"/>); otherwise null.</summary>
    public string? InterruptStatusLine() =>
        InterruptStatusIsWarning ? InterruptUnavailableLine(InterruptDetail.Length > 0 ? InterruptDetail : WakeDetail) : null;

    /// <summary>
    /// Drops the loaded models, takes the switch, key and model from <paramref name="effective"/>
    /// and, only when enabled, counts microphones, ensures both model files (reporting progress
    /// through <paramref name="phase"/>) and loads them; then, only when the wake word is also
    /// enabled, stages the Vosk model and loads the detector. Never throws except for cancellation.
    /// </summary>
    public async Task ConnectAsync(AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        Unload();

        Enabled = effective.SttInput;
        Model = string.IsNullOrWhiteSpace(effective.SttWhisperModel) ? new AppSettingsData().SttWhisperModel : effective.SttWhisperModel.Trim();
        PushToTalk = ParsePushToTalk(effective.SttPushToTalkKey);
        Available = false;
        Detail = "";
        ModelPath = "";
        _missingModel = "";
        _state = State.Off;

        WakeEnabled = effective.SttWake;
        WakePhrase = WakeWordMatch.NormalizePhrase(effective.SttWakePhrase);
        if (WakePhrase.Length == 0)
        {
            WakePhrase = WakeWordMatch.NormalizePhrase(new AppSettingsData().SttWakePhrase);
        }

        WakeModel = string.IsNullOrWhiteSpace(effective.SttVoskModel) ? new AppSettingsData().SttVoskModel : effective.SttVoskModel.Trim();
        WakeAvailable = false;
        WakeDetail = "";
        WakeModelPath = "";
        InterruptEnabled = effective.SttInterrupt;
        InterruptAvailable = false;
        InterruptDetail = "";

        if (!Enabled)
        {
            return;
        }

        try
        {
            int microphones = _inputDeviceCount();
            if (microphones <= 0)
            {
                _state = State.NoMicrophone;
                Detail = AudioSupport.Available ? "no wave-in device" : AudioSupport.Unavailable;
                return;
            }

            var whisperSpec = _models.Whisper(Model);
            if (whisperSpec is null)
            {
                _state = State.ModelMissing;
                _missingModel = Model;
                Detail = ModelStore.WhisperModelError;
                return;
            }

            var whisper = await _models.EnsureAsync(whisperSpec, Progress(phase, whisperSpec.Display, whisperSpec.ApproxBytes), cancellationToken).ConfigureAwait(false);
            if (!whisper.Ok)
            {
                _state = State.ModelMissing;
                _missingModel = Model;
                Detail = whisper.Detail;
                return;
            }

            var sileroSpec = _models.Silero();
            var silero = await _models.EnsureAsync(sileroSpec, Progress(phase, sileroSpec.Display, sileroSpec.ApproxBytes), cancellationToken).ConfigureAwait(false);
            if (!silero.Ok)
            {
                _state = State.ModelMissing;
                _missingModel = "silero";
                Detail = silero.Detail;
                return;
            }

            phase?.Invoke(LoadingLabel(whisperSpec.Display));
            _recognizer = _recognizerFactory(whisper.Path);
            var loaded = await Task.Run(() => Load(_recognizer), cancellationToken).ConfigureAwait(false);
            if (!loaded.Ok)
            {
                _state = State.Unavailable;
                Detail = loaded.Detail;
                return;
            }

            phase?.Invoke(LoadingLabel(sileroSpec.Display));
            _vad = _vadFactory(silero.Path, VadOptions.Default);
            loaded = await Task.Run(() => Load(_vad), cancellationToken).ConfigureAwait(false);
            if (!loaded.Ok)
            {
                _state = State.Unavailable;
                Detail = loaded.Detail;
                return;
            }

            ModelPath = whisper.Path;
            Available = true;
            _state = State.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A download or load cut short (Ctrl+C under the spinner): unavailable with the reason, never "off" while enabled.
            _state = State.Unavailable;
            Detail = ConnectCancelledDetail;
            throw;
        }
        catch (Exception ex)
        {
            _state = State.Unavailable;
            Detail = Assistant.Explain(ex);
            DiagnosticLog.Error(Category, "Voice input could not be prepared: " + Detail, ex);
            return;
        }

        if (WakeEnabled || InterruptEnabled)
        {
            await PrepareWakeAsync(phase, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stages the Vosk model and loads the detector (the wake word's and the interrupt's shared recogniser); a failure is theirs alone.</summary>
    private async Task PrepareWakeAsync(Action<string>? phase, CancellationToken cancellationToken)
    {
        try
        {
            var spec = _models.Vosk(WakeModel);
            if (spec is null)
            {
                // A hand-edited name outside the picker's list: the wake word and the interrupt
                // stay unavailable with the same sentence the picker would give; push-to-talk is untouched.
                WakeDetail = ModelStore.VoskModelError;
                return;
            }

            var vosk = await _models.EnsureDirectoryAsync(spec, Progress(phase, spec.Display, spec.ApproxBytes), () => phase?.Invoke(UnpackingLabel), cancellationToken).ConfigureAwait(false);
            if (!vosk.Ok)
            {
                WakeDetail = vosk.Detail;
                return;
            }

            phase?.Invoke(LoadingLabel(spec.Display));
            _wakeDetector = _wakeDetectorFactory(vosk.Path, WakePhrase);
            var detector = _wakeDetector;
            var loaded = await Task.Run(() => Load(detector), cancellationToken).ConfigureAwait(false);
            if (!loaded.Ok)
            {
                WakeDetail = loaded.Detail;
                return;
            }

            WakeModelPath = vosk.Path;
            WakeAvailable = true;
            InterruptAvailable = true;
            try
            {
                // The interrupt's echo probe listens to the assistant's own audio with a second
                // recogniser over the same model; without one the text guard stands alone.
                _echoDetector = detector.Fork();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn(Category, "The echo probe could not be prepared; the interrupt will rely on the text guard: " + Assistant.Explain(ex), ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WakeDetail = ConnectCancelledDetail;
            throw;
        }
        catch (Exception ex)
        {
            WakeDetail = Assistant.Explain(ex);
            DiagnosticLog.Error(Category, "The wake word could not be prepared: " + WakeDetail, ex);
        }
    }

    /// <summary>
    /// One push-to-talk utterance, or a wake-word utterance when <paramref name="seed"/> is the
    /// listener's pre-roll. <paramref name="options"/> overrides <see cref="PipelineOptions"/> for
    /// this listen only (the interrupt's shorter no-speech window). Opens the microphone on first
    /// use; a failed result marks the session unavailable. Cancellation propagates. Not ready: a
    /// failed result, never a throw.
    /// </summary>
    public async Task<ListenResult> ListenAsync(CancellationToken finishEarly, Action<string>? phase, CancellationToken cancellationToken, byte[]? seed = null, VoicePipelineOptions? options = null)
    {
        if (!IsReady || _recognizer is null || _vad is null)
        {
            return ListenResult.Failed(ListenEnd.NoAudio, TimeSpan.Zero, TimeSpan.Zero, "voice input is not ready");
        }

        var wanted = options ?? PipelineOptions;
        if (_pipeline is null || _pipeline.Options != wanted)
        {
            IAudioCapture capture;
            try
            {
                capture = EnsureCapture();
            }
            catch (Exception ex)
            {
                string detail = "microphone unavailable: " + Assistant.Explain(ex);
                MarkUnavailable(detail);
                return ListenResult.Failed(ListenEnd.NoAudio, TimeSpan.Zero, TimeSpan.Zero, detail);
            }

            _pipeline = new VoicePipeline(capture, _vad, _recognizer, wanted);
        }

        var result = await _pipeline.ListenAsync(finishEarly, phase, cancellationToken, seed).ConfigureAwait(false);
        if (!result.Ok)
        {
            MarkUnavailable(result.Detail);
        }

        return result;
    }

    /// <summary>
    /// Opens the microphone and listens for the phrase; when it is heard, <paramref name="wake"/>
    /// is cancelled from the thread pool. <paramref name="ignore"/> drops a matching result
    /// (the interrupt's echo guard). False, with the wake word and the interrupt marked
    /// unavailable, when the microphone cannot be opened or neither is ready; then the caller
    /// goes on without it. The caller decides which readiness applies (<see cref="WakeReady"/>
    /// at the idle line, <see cref="InterruptReady"/> for a spoken turn) and how the recogniser
    /// listens (<paramref name="mode"/>: <see cref="WakeDetectorMode.Utterance"/> at the idle
    /// line, <see cref="WakeDetectorMode.Keyword"/> for the interrupt, where the microphone
    /// never hears the silence a final result needs).
    /// </summary>
    public bool ArmWake(CancellationTokenSource wake, Func<WakeUtterance, bool>? ignore = null, WakeDetectorMode mode = WakeDetectorMode.Utterance, TimeSpan? keywordConfirm = null)
    {
        ArgumentNullException.ThrowIfNull(wake);
        if (!(WakeReady || InterruptReady) || _wakeDetector is null)
        {
            return false;
        }

        try
        {
            _wakeListener ??= new WakeListener(EnsureCapture(), _wakeDetector, WakePhrase);
            _wakeListener.Arm(ignore, mode, keywordConfirm);
            DiagnosticLog.Debug(Category, ArmedLogLine(mode, keywordConfirm));
        }
        catch (Exception ex)
        {
            MarkWakeUnavailable("microphone unavailable: " + Assistant.Explain(ex));
            return false;
        }

        // Never inline: the source's callbacks would run the input line's continuation on the
        // capture thread. The listener's task already continues on the pool; this keeps it there.
        _ = _wakeListener.Detected.ContinueWith(
            static (task, state) =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    SafeCancel((CancellationTokenSource)state!);
                }
            },
            wake,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return true;
    }

    /// <summary>Stops listening for the phrase and returns what was heard, if anything. Safe when not armed.</summary>
    public WakeHit? DisarmWake()
    {
        if (_wakeListener is not { } listener)
        {
            return null;
        }

        // A hit has already cleared the armed flag on the capture thread, so the flag alone
        // cannot say whether there was an arm to log: a hit says so too.
        bool wasArmed = listener.IsArmed;
        var hit = listener.Disarm();
        if (wasArmed || hit is not null)
        {
            DiagnosticLog.Debug(Category, DisarmedLogLine(hit is not null));
        }

        return hit;
    }

    /// <summary>The listener armed: <c>Wake listener armed (utterance: the idle wake word)</c> / <c>… (keyword: the interrupt, confirm 200 ms)</c>. Pinned.</summary>
    public static string ArmedLogLine(WakeDetectorMode mode, TimeSpan? keywordConfirm) => mode == WakeDetectorMode.Keyword
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Wake listener armed (keyword: the interrupt, confirm {(keywordConfirm ?? TimeSpan.Zero).TotalMilliseconds:F0} ms)")
        : "Wake listener armed (utterance: the idle wake word)";

    /// <summary><c>Wake listener disarmed (hit)</c> / <c>… (no hit)</c>. Pinned.</summary>
    public static string DisarmedLogLine(bool hit) => hit ? "Wake listener disarmed (hit)" : "Wake listener disarmed (no hit)";

    /// <summary>Whether the phrase listener is armed right now (the microphone is its, not a listen's). A fact for tests; the screen tracks its own arms.</summary>
    public bool WakeArmed => _wakeListener?.IsArmed == true;

    /// <summary>Voice input is off for the rest of the session (until the next probe).</summary>
    public void MarkUnavailable(string reason)
    {
        Available = false;
        Detail = reason;
        _state = State.Unavailable;
    }

    /// <summary>The wake word (and with it the interrupt) is off for the rest of the session (until the next probe); push-to-talk is untouched.</summary>
    public void MarkWakeUnavailable(string reason)
    {
        WakeAvailable = false;
        WakeDetail = reason;
    }

    /// <summary>Interrupting is off for the rest of the session (until the next probe); the idle wake word and push-to-talk are untouched.</summary>
    public void MarkInterruptUnavailable(string reason)
    {
        InterruptAvailable = false;
        InterruptDetail = reason;
    }

    public void Dispose() => Unload();

    private IAudioCapture EnsureCapture() => _capture ??= _captureFactory(PcmFormat.Whisper);

    private static (bool Ok, string Detail) Load(ISpeechRecognizer recognizer) => (recognizer.Load(out var detail), detail);

    private static (bool Ok, string Detail) Load(IVoiceActivityDetector vad) => (vad.Load(out var detail), detail);

    private static (bool Ok, string Detail) Load(IWakeWordDetector detector) => (detector.Load(out var detail), detail);

    /// <summary>
    /// Cancels a source that may already be disposed: the input line disposes its per-read
    /// sources the moment the read returns, and a late signal from the pool must not throw. The
    /// wake hit and the timer alert both go through here; what fired waits where it was queued.
    /// </summary>
    internal static void SafeCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The input line already returned and disposed it; the hit waits in the listener.
        }
    }

    /// <summary>Progress that updates the spinner label whenever the percentage moves (or per megabyte when the size is unknown); the speech session's in-process download uses it too.</summary>
    internal static IProgress<(long Received, long? Total)>? Progress(Action<string>? phase, string display, long approxBytes) =>
        phase is null ? null : new LabelProgress(phase, display, approxBytes);

    /// <summary>
    /// A fresh <see cref="EchoProbe"/> for one spoken turn (the caller sets it on the turn's
    /// <see cref="SpeechOutput"/> and disposes it), or null when the interrupt is not ready or
    /// the second recogniser is missing — then the text guard stands alone.
    /// </summary>
    public EchoProbe? CreateEchoProbe(PcmFormat source, TimeSpan? keywordConfirm = null)
    {
        if (!InterruptReady || _echoDetector is null)
        {
            return null;
        }

        try
        {
            return new EchoProbe(_echoDetector, WakePhrase, source, keywordConfirm);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn(Category, "The echo probe could not be started; the interrupt will rely on the text guard: " + Assistant.Explain(ex), ex);
            return null;
        }
    }

    private void Unload()
    {
        _wakeListener?.Dispose();
        _wakeListener = null;
        // The fork before its parent: it holds a recogniser over the parent's model.
        _echoDetector?.Dispose();
        _echoDetector = null;
        _wakeDetector?.Dispose();
        _wakeDetector = null;
        _pipeline = null;
        _capture?.Dispose();
        _capture = null;
        _recognizer?.Dispose();
        _recognizer = null;
        _vad?.Dispose();
        _vad = null;
    }
}
