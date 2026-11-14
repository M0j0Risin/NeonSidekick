using System.Net;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

/// <summary>
/// The Chat tab's voice (2026-11-13, the webui-voice plan): each page's latest 🔊, its listen through the shared Whisper and Silero, and the
/// heard text held for its box. Called on Kestrel threads (<see cref="IWebVoice"/>), never waiting on the screen and never writing the
/// transcript: a heard line joins the page's queue as a typed one does (<see cref="WebUiSession.Send"/>), and the page sees each phase
/// through events targeted at it.
///
/// <para><b>A page's entry</b> is made at its first voice post or send, under one lock, and goes once the page has had no stream for 10
/// minutes with nothing running (<see cref="WebVoiceTick"/>, on the session's 1 s tick), at most <see cref="MaxVoicePages"/> kept. All go on a
/// profile switch, the Web UI stopping and <i>Web UI voice</i> or the Chat tab going off (<see cref="StopWebVoice"/>). A page whose stream
/// goes is given <see cref="VoiceGrace"/> to come back with the same run before its listen is cancelled; a new run (a reload) cancels it at once.</para>
/// </summary>
internal sealed partial class ChatScreen : IWebVoice
{
    /// <summary>The most page entries kept.</summary>
    public const int MaxVoicePages = 32;

    /// <summary>How long a page may be without its stream (a blip, a reconnect) before its listen is cancelled.</summary>
    public static readonly TimeSpan VoiceGrace = TimeSpan.FromSeconds(10);

    /// <summary>How long an entry with no stream and nothing running is kept.</summary>
    public static readonly TimeSpan VoicePageIdle = TimeSpan.FromMinutes(10);

    /// <summary>How long heard text held for a page's box is kept.</summary>
    public static readonly TimeSpan HeardKeep = TimeSpan.FromMinutes(1);

    private readonly object _voiceGate = new();
    private readonly Dictionary<string, VoicePage> _voicePages = new(StringComparer.Ordinal);

    /// <summary>A test's hook between a start's checks and the listen's lock (the race the lock decides). Null in the app.</summary>
    internal Action? VoiceStartRaceForTests { get; set; }

    /// <summary>The stall a page's listen waits for; tests shorten it.</summary>
    internal TimeSpan VoiceStallForTests { get; set; } = WebAudioCapture.StallAfter;

    /// <summary>One page's voice state. Fields under <see cref="_voiceGate"/>.</summary>
    private sealed class VoicePage(string id, DateTimeOffset now)
    {
        public string Id { get; } = id;

        /// <summary>The page's latest 🔊, or null when it never said.</summary>
        public bool? Speak { get; set; }

        /// <summary>The run its stream came from last.</summary>
        public string? Load { get; set; }

        /// <summary>Heard text held for its box and when it was heard.</summary>
        public WebHeard? Heard { get; set; }

        public DateTimeOffset HeardAt { get; set; }

        public PageListen? Listen { get; set; }

        /// <summary>The page's speaker while it speaks (<c>ChatScreen.WebSpeech.cs</c>), and its playback.</summary>
        public SpeechOutput? Speaker { get; set; }

        public WebAudioPlayback? Playback { get; set; }

        /// <summary>The reconnect grace's timer while the stream is gone.</summary>
        public ITimer? Grace { get; set; }

        /// <summary>When the page last had a stream (or was made).</summary>
        public DateTimeOffset LastSeen { get; set; } = now;
    }

    /// <summary>One listen of a page: its capture, its two ends (finish transcribes, cancel discards) and the phase it ended in.</summary>
    private sealed class PageListen(string id, WebAudioCapture capture, string? load, IPAddress? remote)
    {
        public string Id { get; } = id;

        public WebAudioCapture Capture { get; } = capture;

        public string? Load { get; } = load;

        public IPAddress? Remote { get; } = remote;

        public CancellationTokenSource Finish { get; } = new();

        public CancellationTokenSource Cancel { get; } = new();

        /// <summary>The running botchat's hold this listen took (Design 2b), lifted at its end; null with no chat.</summary>
        public TaskCompletionSource? BotHold { get; set; }

        /// <summary>The phase it ended in (<c>transcribing</c> while Whisper runs), or null while listening.</summary>
        public volatile string? Phase;
    }

    // ── IWebVoice (Kestrel threads) ────────────────────────────────────────────

    void IWebVoice.Saw(string? page, bool? speak) => SawSpeak(page, speak);

    WebReply IWebVoice.Start(WebVoiceStart request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!WebVideoPlayer.IsId(request.Page) || !WebVideoPlayer.IsId(request.Listen))
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest);
        }

        string page = request.Page!;
        SawSpeak(page, request.Speak);
        if (VoiceRefusal() is { } refused)
        {
            return refused;
        }

        if (!_webUi.Events.HasPage(page))
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.VoiceNoStream);
        }

        PageListen listen;
        SpeechOutput? tapped = null;
        lock (_voiceGate)
        {
            if (_voice.Listening || _voicePages.Values.Any(p => p.Listen is not null))
            {
                return new WebReply(WebOutcome.Conflict, WebUiText.VoiceBusy);
            }

            var entry = EntryOf(page);
            if (entry.Speaker is { Completion.IsCompleted: false } speaking)
            {
                tapped = speaking;   // 🎤 over the page's own voice: it stops, then the listen starts (the PC key's way)
            }

            var capture = new WebAudioCapture(_time, VoiceStallForTests);
            listen = new PageListen(request.Listen!, capture, entry.Load, remote);
            entry.Listen = listen;
            capture.Stalled += () => OnListenStalled(page, listen);
        }

        if (tapped is not null)
        {
            _speech.Stop(tapped);
        }

        listen.BotHold = RequestBotPhoneTalk();   // under a botchat, the person is talking: the bots wait (Design 2b)

        DiagnosticLog.Info(WebUiCategory, WebUiText.ListenStartLogLine(page, remote));
        _webUi.FlashPc(WebUiText.ListeningFlash);
        PublishVoice(page, new WebVoicePhase(listen.Id, VoicePhases.Listening));
        _ = Task.Run(() => RunPageListenAsync(page, listen));
        return new WebReply(WebOutcome.Ok, Listen: VoicePhases.Listening);
    }

    WebReply IWebVoice.Chunk(WebVoiceChunk request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!WebVideoPlayer.IsId(request.Page) || !WebVideoPlayer.IsId(request.Listen))
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest);
        }

        if (ListenOf(request.Page!, request.Listen!) is not { } listen)
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.VoiceListenGone, Listen: VoicePhases.Ended);
        }

        if (request.Seq < 0 || DecodeChunk(request.Pcm) is not { } pcm)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest, Listen: ListenState(listen));
        }

        return listen.Capture.Deliver(request.Seq, pcm) switch
        {
            WebChunkResult.Delivered or WebChunkResult.Repeat => new WebReply(WebOutcome.Ok, Listen: ListenState(listen)),
            WebChunkResult.OutOfOrder => new WebReply(WebOutcome.Refused, WebUiText.BadRequest, Listen: ListenState(listen)),
            _ => new WebReply(WebOutcome.Ok, Listen: listen.Phase ?? VoicePhases.Ended),
        };
    }

    WebReply IWebVoice.End(WebVoiceEnd request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!WebVideoPlayer.IsId(request.Page) || !WebVideoPlayer.IsId(request.Listen))
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest);
        }

        SawSpeak(request.Page, request.Speak);
        if (ListenOf(request.Page!, request.Listen!) is not { } listen)
        {
            return new WebReply(WebOutcome.Ok, Listen: VoicePhases.Ended);   // ended already: nothing to do
        }

        if (string.Equals(request.How, "cancel", StringComparison.Ordinal))
        {
            VoiceSession.SafeCancel(listen.Cancel);
        }
        else
        {
            VoiceSession.SafeCancel(listen.Finish);
        }

        return new WebReply(WebOutcome.Ok, Listen: ListenState(listen));
    }

    WebReply IWebVoice.Speak(WebVoiceSpeak request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!WebVideoPlayer.IsId(request.Page))
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest);
        }

        SawSpeak(request.Page, request.On);
        if (!request.On)
        {
            StopPageSpeech(request.Page!, WebUiText.VoiceStoppedFlash);
        }

        return WebReply.Ok;
    }

    WebReply IWebVoice.Played(WebVoicePlayed request, IPAddress? remote) => WebPlayed(request);

    byte[]? IWebVoice.Clip(string page, string turn, long n) => WebClip(page, turn, n);

    // ── The listen ─────────────────────────────────────────────────────────────

    /// <summary>A page's listen, on the pool: the shared pipeline over its capture, then the heard text where <i>STT destination</i> sends it. Never throws.</summary>
    private async Task RunPageListenAsync(string page, PageListen listen)
    {
        string how = "canceled";
        var audio = TimeSpan.Zero;
        try
        {
            VoiceStartRaceForTests?.Invoke();
            var result = await _voice.ListenOnAsync(listen.Capture, listen.Finish.Token,
                phase =>
                {
                    if (phase == VoicePipeline.TranscribingLabel)
                    {
                        listen.Phase = VoicePhases.Transcribing;
                        PublishVoice(page, new WebVoicePhase(listen.Id, VoicePhases.Transcribing));
                    }
                },
                listen.Cancel.Token, VoiceSession.PageOptions).ConfigureAwait(false);
            audio = result.Audio;
            how = result.EndedBy.ToString();
            if (!result.Ok)
            {
                string detail = result.Detail == VoiceSession.BusyDetail ? WebUiText.VoiceBusy
                    : result.Detail == VoiceSession.NotReadyDetail ? WebUiText.VoiceGettingReady
                    : WebUiText.ListenFailed(result.Detail);
                how = "failed: " + result.Detail;
                EndListen(page, listen, VoicePhases.Failed, detail: detail);
                return;
            }

            if (result.HeardNothing)
            {
                EndListen(page, listen, VoicePhases.Nothing);
                return;
            }

            DeliverHeard(page, listen, result.Text);
        }
        catch (OperationCanceledException)
        {
            EndListen(page, listen, VoicePhases.Canceled);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            how = "failed: " + e.Message;
            DiagnosticLog.Error(WebUiCategory, WebUiText.ListenFailed(e.Message), e);
            EndListen(page, listen, VoicePhases.Failed, detail: WebUiText.ListenFailed(e.Message));
        }
        finally
        {
            listen.Capture.Dispose();
            lock (_voiceGate)
            {
                if (_voicePages.TryGetValue(page, out var entry) && ReferenceEquals(entry.Listen, listen))
                {
                    entry.Listen = null;
                }
            }

            HoldPageSpeech(page, false);   // a reply's voice that began mid-listen plays now (the page schedules it from its first clip)
            if (listen.BotHold is { } botHold)
            {
                LiftBotPhoneHold(botHold);   // after the heard line was queued, so the next bot's TakeInterjections sees it
            }

            listen.Finish.Dispose();
            listen.Cancel.Dispose();
            DiagnosticLog.Info(WebUiCategory, WebUiText.ListenLogLine(page, how, audio));
        }
    }

    /// <summary>
    /// The heard text: under <i>STT destination</i> <c>chat</c> it joins the page's queue as a message (never a command, whatever it reads), and a
    /// refused one (the queue full, too long) goes to the page's box with the refusal's word; under <c>draft</c> it goes to that page's box.
    /// </summary>
    private void DeliverHeard(string page, PageListen listen, string text)
    {
        if (SttDestinationMode.Resolve(_effective()) == SttTarget.Chat)
        {
            bool? speak;
            lock (_voiceGate)
            {
                speak = _voicePages.TryGetValue(page, out var entry) ? entry.Speak : null;
            }

            var sent = _webUi.Send(new WebSendRequest { Id = listen.Id, Text = text, AsText = true, Page = page, Speak = speak }, listen.Remote);
            if (sent.Outcome == WebOutcome.Ok)
            {
                EndListen(page, listen, VoicePhases.Sent);
                return;
            }

            EndListen(page, listen, VoicePhases.Heard, text, sent.Message);
            return;
        }

        EndListen(page, listen, VoicePhases.Heard, text);
    }

    /// <summary>
    /// A listen's end told to its page: its phase (with the heard text and a word for <c>heard</c>, why for <c>failed</c>). A <c>heard</c> that finds
    /// the page's stream down is held for its next snapshot.
    /// </summary>
    private void EndListen(string page, PageListen listen, string phase, string? text = null, string? detail = null)
    {
        listen.Phase = phase;
        if (phase == VoicePhases.Heard && !_webUi.Events.HasPage(page))
        {
            lock (_voiceGate)
            {
                var entry = EntryOf(page);
                entry.Heard = new WebHeard(text ?? "", detail);
                entry.HeardAt = _time.GetUtcNow();
            }

            return;
        }

        PublishVoice(page, new WebVoicePhase(listen.Id, phase, text, detail));
    }

    /// <summary>3 s with no chunk: what came is transcribed, but only while the page's stream is up from the run that started it (a reloading page's half-heard line is never sent).</summary>
    private void OnListenStalled(string page, PageListen listen)
    {
        if (_webUi.Events.HasPage(page, listen.Load))
        {
            DiagnosticLog.Info(WebUiCategory, WebUiText.ListenStalledLogLine(page));
            VoiceSession.SafeCancel(listen.Finish);
        }
    }

    // ── Pages coming and going ─────────────────────────────────────────────────

    /// <summary>A page's stream subscribed (the hub's <see cref="WebEventHub.PageJoined"/>): the grace ends; a new run is a reload, whose listen is cancelled.</summary>
    private void OnVoicePageJoined(string page, string load)
    {
        PageListen? reloaded = null;
        bool reload = false;
        lock (_voiceGate)
        {
            if (!_voicePages.TryGetValue(page, out var entry))
            {
                if (!WebVoiceMode.Offered(_effective()))
                {
                    return;
                }

                entry = EntryOf(page);   // made now, so a listen knows the run it came from
            }

            entry.Grace?.Dispose();
            entry.Grace = null;
            entry.LastSeen = _time.GetUtcNow();
            if (entry.Load is not null && entry.Load != load)
            {
                reload = true;
                reloaded = entry.Listen;
            }

            entry.Load = load;
        }

        if (reloaded is not null)
        {
            VoiceSession.SafeCancel(reloaded.Cancel);
        }

        if (reload)
        {
            ReloadedPageSpeech(page);
        }
    }

    /// <summary>A page's last stream went (the hub's <see cref="WebEventHub.PageLeft"/>): its grace begins. Only arms a timer.</summary>
    private void OnVoicePageLeft(string page)
    {
        lock (_voiceGate)
        {
            if (!_voicePages.TryGetValue(page, out var entry))
            {
                return;
            }

            entry.LastSeen = _time.GetUtcNow();
            entry.Grace?.Dispose();
            entry.Grace = _time.CreateTimer(_ => GraceOver(page), null, VoiceGrace, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The grace ran out: with the page still gone, its listen is cancelled and its speech counted heard.</summary>
    private void GraceOver(string page)
    {
        try
        {
            if (_webUi.Events.HasPage(page))
            {
                return;
            }

            PageListen? listen;
            lock (_voiceGate)
            {
                if (!_voicePages.TryGetValue(page, out var entry))
                {
                    return;
                }

                entry.Grace?.Dispose();
                entry.Grace = null;
                listen = entry.Listen;
            }

            if (listen is not null)
            {
                VoiceSession.SafeCancel(listen.Cancel);
            }

            PageGoneSpeech(page);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Error(WebUiCategory, "A Web UI page's grace check failed: " + e.Message, e);
        }
    }

    /// <summary>
    /// The voice's chores on the session's 1 s tick: everything goes once voice is no longer offered; held heard text over a minute old goes; an
    /// entry with no stream for 10 minutes and nothing running goes, and past <see cref="MaxVoicePages"/> the oldest with no stream.
    /// </summary>
    private void WebVoiceTick()
    {
        if (!WebVoiceMode.Offered(_effective()))
        {
            bool any;
            lock (_voiceGate)
            {
                any = _voicePages.Count > 0;
            }

            if (any)
            {
                StopWebVoice();
            }

            return;
        }

        var now = _time.GetUtcNow();
        lock (_voiceGate)
        {
            foreach (var entry in _voicePages.Values)
            {
                if (entry.Heard is not null && now - entry.HeardAt >= HeardKeep)
                {
                    entry.Heard = null;
                }
            }

            var idle = _voicePages.Values.Where(e => e.Listen is null && !PageSpeaking(e.Id) && !_webUi.Events.HasPage(e.Id) && now - e.LastSeen >= VoicePageIdle).ToList();
            foreach (var entry in idle)
            {
                Drop(entry);
            }
        }
    }

    /// <summary>
    /// Every page's voice ends (2026-11-13): listens cancelled, speech stopped, held text and entries dropped. On a profile switch, the Web UI
    /// stopping or restarting, and <i>Web UI voice</i> or the Chat tab going off.
    /// </summary>
    private void StopWebVoice()
    {
        List<PageListen> listens;
        List<(SpeechOutput Speaker, WebAudioPlayback? Playback)> speakers;
        lock (_voiceGate)
        {
            listens = [.. _voicePages.Values.Select(e => e.Listen).OfType<PageListen>()];
            speakers = [.. _voicePages.Values.Where(e => e.Speaker is { Completion.IsCompleted: false }).Select(e => (e.Speaker!, e.Playback))];
            foreach (var entry in _voicePages.Values.ToList())
            {
                Drop(entry);
            }
        }

        foreach (var listen in listens)
        {
            VoiceSession.SafeCancel(listen.Cancel);
        }

        StopSpeakers(speakers);
    }

    /// <summary>Cancels every page's listen (the models about to go), keeping the pages' entries and speech.</summary>
    private void CancelPageListens()
    {
        List<PageListen> listens;
        lock (_voiceGate)
        {
            listens = [.. _voicePages.Values.Select(e => e.Listen).OfType<PageListen>()];
        }

        foreach (var listen in listens)
        {
            VoiceSession.SafeCancel(listen.Cancel);
        }
    }

    /// <summary>
    /// <c>/webui on|off</c> or the pane's button flipped the server (2026-11-13, the webui-voice plan): with <i>Web UI voice</i> on, the
    /// voice models follow it, reconnected now when idle, owed to the turn's end mid-reply. Off, the pages' voice stops at once.
    /// </summary>
    private async Task WebVoiceWantedChangedAsync(bool before, CancellationToken cancellationToken)
    {
        bool after = WebVoiceMode.Wanted(_effective());
        if (before == after)
        {
            return;
        }

        if (!after)
        {
            StopWebVoice();
        }

        if (_replyRunning)
        {
            _deferred |= SettingsChanges.Tts | SettingsChanges.Voice;
            return;
        }

        await ApplySettingsChangesAsync(SettingsChanges.Tts | SettingsChanges.Voice, cancellationToken).ConfigureAwait(false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Why a voice post is refused now: voice not offered, or the models not ready (getting ready while a connect runs). Null when it may go on.</summary>
    private WebReply? VoiceRefusal()
    {
        if (!WebVoiceMode.Offered(_effective()))
        {
            return new WebReply(WebOutcome.Refused, WebUiText.VoiceOff);
        }

        if (WebVoiceListenState(_effective()) is { Length: > 0 } why)
        {
            return new WebReply(WebOutcome.Refused, why);
        }

        return null;
    }

    /// <summary>The page's 🎤 as its state carries it: null when voice isn't offered, "" when the models are ready, else why not.</summary>
    private string? WebVoiceListenState(AppSettingsData effective) =>
        !WebVoiceMode.Offered(effective) ? null
        : _voice.ModelsReady ? ""
        : _jobs.Running(BackgroundJobKind.Voice) ? WebUiText.VoiceGettingReady
        : WebUiText.VoiceNotReady(_voice.Detail.Length > 0 ? _voice.Detail : VoiceSession.NotReadyDetail);

    /// <summary>The page's 🔊 as its state carries it: null when voice isn't offered, "" when Kokoro is ready, else why not.</summary>
    private string? WebVoiceSpeakState(AppSettingsData effective) =>
        !WebVoiceMode.Offered(effective) ? null
        : _speech.Available ? ""
        : _jobs.Running(BackgroundJobKind.Speech) ? WebUiText.VoiceGettingReady
        : WebUiText.VoiceNotReady(_speech.FailureDetail());

    /// <summary>Records a page's latest 🔊 (a post that carries it). A page id of the wrong shape, or none said, records nothing.</summary>
    private void SawSpeak(string? page, bool? speak)
    {
        if (!WebVideoPlayer.IsId(page) || speak is not { } on)
        {
            return;
        }

        lock (_voiceGate)
        {
            EntryOf(page!).Speak = on;
        }
    }

    /// <summary>The page's entry, made if new; past <see cref="MaxVoicePages"/> the oldest with no stream goes first. Under the lock.</summary>
    private VoicePage EntryOf(string page)
    {
        if (_voicePages.TryGetValue(page, out var entry))
        {
            return entry;
        }

        if (_voicePages.Count >= MaxVoicePages
            && _voicePages.Values.Where(e => e.Listen is null && e.Speaker is null && !_webUi.Events.HasPage(e.Id)).OrderBy(e => e.LastSeen).FirstOrDefault() is { } oldest)
        {
            Drop(oldest);
        }

        entry = new VoicePage(page, _time.GetUtcNow());
        _voicePages[page] = entry;
        return entry;
    }

    /// <summary>An entry gone. Under the lock.</summary>
    private void Drop(VoicePage entry)
    {
        entry.Grace?.Dispose();
        entry.Grace = null;
        _voicePages.Remove(entry.Id);
    }

    /// <summary>The page's running listen when it is <paramref name="listen"/>; null for another page's or one that ended.</summary>
    private PageListen? ListenOf(string page, string listen)
    {
        lock (_voiceGate)
        {
            return _voicePages.TryGetValue(page, out var entry) && entry.Listen is { } running && running.Id == listen ? running : null;
        }
    }

    private static string ListenState(PageListen listen) => listen.Phase ?? VoicePhases.Listening;

    /// <summary>A chunk's audio: base64 that decodes to an even length of at most half a second; null otherwise.</summary>
    internal static byte[]? DecodeChunk(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > (WebAudioCapture.MaxChunkBytes + 2) / 3 * 4)
        {
            return null;
        }

        var buffer = new byte[base64.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(base64, buffer, out int written) || written == 0 || written % 2 != 0 || written > WebAudioCapture.MaxChunkBytes)
        {
            return null;
        }

        return buffer[..written];
    }

    /// <summary>A <c>voice</c> event to <paramref name="page"/> alone.</summary>
    private void PublishVoice(string page, WebVoicePhase phase) =>
        _webUi.Events.Publish(new WebEvent("voice", () => JsonSerializer.Serialize(phase, WebUiJsonContext.Default.WebVoicePhase)) { Page = page });

    /// <summary>A page's voice part of its snapshot: its held heard text (taken) and its speaker.</summary>
    private (WebHeard? Heard, WebSpeaking? Speaking) WebVoiceSnapshot(string page)
    {
        WebHeard? heard = null;
        lock (_voiceGate)
        {
            if (_voicePages.TryGetValue(page, out var entry) && entry.Heard is { } held)
            {
                heard = _time.GetUtcNow() - entry.HeardAt < HeardKeep ? held : null;
                entry.Heard = null;
            }
        }

        return (heard, PageSpeakingNow(page));
    }

    /// <summary>The page's latest 🔊 as the voice knows it: null for a page never heard from.</summary>
    private bool? PageSpeak(string page)
    {
        lock (_voiceGate)
        {
            return _voicePages.TryGetValue(page, out var entry) ? entry.Speak : null;
        }
    }

    /// <summary>What a listen's <c>voice</c> events and chunk answers say.</summary>
    internal static class VoicePhases
    {
        public const string Listening = "listening";
        public const string Transcribing = "transcribing";
        public const string Nothing = "nothing";
        public const string Failed = "failed";
        public const string Heard = "heard";
        public const string Sent = "sent";
        public const string Canceled = "canceled";
        public const string Ended = "ended";
    }
}
