using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

/// <summary>Where a turn's voice goes (2026-11-13, the webui-voice plan): the PC's device, a page, or nowhere.</summary>
internal enum VoiceWhere
{
    Silent,
    Pc,
    Page,
}

/// <summary>A turn's voice target: <see cref="VoiceWhere.Page"/> names its page.</summary>
internal readonly record struct VoiceTarget(VoiceWhere Where, string? Page = null)
{
    public static VoiceTarget Silent => new(VoiceWhere.Silent);

    public static VoiceTarget Pc => new(VoiceWhere.Pc);
}

/// <summary>
/// The Chat tab's spoken replies (2026-11-13, the webui-voice plan): a page's speaker (a <see cref="WebAudioPlayback"/> under the one
/// synthesizer), its clips and its progress. A reply speaks where its message came from: a page's line on that page while its 🔊 is on
/// (silent while it's off, never at the PC), a PC line at the PC under <i>TTS output</i>. The page's entry (<c>ChatScreen.WebVoice.cs</c>)
/// holds its speaker; every stop names the speaker it decided on (<see cref="SpeechSession.Stop(SpeechOutput)"/>), so a stop handed to
/// another thread never reaches a newer turn's voice.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>
    /// The page of the line this dispatch sent (2026-11-13), beside <see cref="_sentFromWeb"/> and set and cleared with it, so a stale page
    /// never takes a later PC turn's voice. The automation runners clear it before their first pass: their turns are the PC's.
    /// </summary>
    private WebPageRef? _sentFromWebPage;

    /// <summary>
    /// Where a turn speaks: a page's line with <i>Web UI voice</i> on goes to that page while its latest 🔊 is on (the line's own only when
    /// the page has no entry) and Kokoro is loaded, else nowhere; anything else by today's rule (<i>TTS output</i> and speech ready).
    /// <paramref name="needsStream"/> (a bot's turn) also asks for the page's stream to be up: a chat never speaks into a page gone.
    /// </summary>
    private VoiceTarget TurnVoiceTarget(AppSettingsData effective, WebPageRef? page, bool needsStream)
    {
        if (page is not null && WebVoiceMode.Wanted(effective))
        {
            bool speak = PageSpeak(page.Page) ?? page.Speak;
            return speak && WebVoiceMode.Offered(effective) && _speech.Available && (!needsStream || _webUi.Events.HasPage(page.Page))
                ? new VoiceTarget(VoiceWhere.Page, page.Page)
                : VoiceTarget.Silent;
        }

        return effective.TtsOutput && _speech.IsReady ? VoiceTarget.Pc : VoiceTarget.Silent;
    }

    /// <summary>The speaker for <paramref name="target"/>: the PC's (in <paramref name="bot"/>'s voice for a bot), a page's, or none.</summary>
    private SpeechOutput? BeginTurnSpeaker(VoiceTarget target, BotParticipant? bot, CancellationToken cancellationToken) => target.Where switch
    {
        VoiceWhere.Pc => bot is null ? _speech.BeginTurn(cancellationToken) : _speech.BeginTurn(cancellationToken, bot.Voice, bot.Speed),
        VoiceWhere.Page => BeginPageSpeaker(target.Page!, bot, cancellationToken),
        _ => null,
    };

    /// <summary>
    /// A speaker on <paramref name="page"/>: a fresh <see cref="WebAudioPlayback"/> (its own turn id) the session owns, kept in the page's
    /// entry until it completes. Held from the start while the page listens. Null when Kokoro isn't loaded.
    /// </summary>
    private SpeechOutput? BeginPageSpeaker(string page, BotParticipant? bot, CancellationToken cancellationToken)
    {
        var playback = new WebAudioPlayback(page, WebVideoPlayer.NewId(), _time, _webUi.Events.Publish);
        var speaker = bot is null ? _speech.BeginTurn(cancellationToken, playback) : _speech.BeginTurn(cancellationToken, bot.Voice, bot.Speed, playback);
        if (speaker is null)
        {
            return null;   // the session disposed the playback
        }

        playback.GaveUp = _ => _speech.Stop(speaker);
        lock (_voiceGate)
        {
            var entry = EntryOf(page);
            entry.Speaker = speaker;
            entry.Playback = playback;
            if (entry.Listen is not null)
            {
                playback.Hold(true);
            }
        }

        _ = speaker.Completion.ContinueWith(_ => ForgetPageSpeaker(page, speaker), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return speaker;
    }

    private void ForgetPageSpeaker(string page, SpeechOutput speaker)
    {
        lock (_voiceGate)
        {
            if (_voicePages.TryGetValue(page, out var entry) && ReferenceEquals(entry.Speaker, speaker))
            {
                entry.Speaker = null;
                entry.Playback = null;
            }
        }
    }

    /// <summary>The page's speaker and playback while it speaks, else nulls.</summary>
    private (SpeechOutput? Speaker, WebAudioPlayback? Playback) PageSpeakerOf(string page)
    {
        lock (_voiceGate)
        {
            return _voicePages.TryGetValue(page, out var entry) && entry.Speaker is { Completion.IsCompleted: false } speaker
                ? (speaker, entry.Playback)
                : (null, null);
        }
    }

    /// <summary>
    /// A page's progress, its ■ on a tail (<c>stop</c>), or its going (<c>gone</c>): answered only when that page's speaker owns
    /// <c>turn</c>, so one page can't touch another's voice. A report for an ended speaker is taken quietly.
    /// </summary>
    private WebReply WebPlayed(WebVoicePlayed request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!WebVideoPlayer.IsId(request.Page) || !WebVideoPlayer.IsId(request.Turn))
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest);
        }

        var (speaker, playback) = PageSpeakerOf(request.Page!);
        if (speaker is null || playback is null || playback.Turn != request.Turn)
        {
            return WebReply.Ok;   // that voice ended (or was another's): nothing to move
        }

        if (request.Stop == true)
        {
            StopPageSpeech(request.Page!, WebUiText.VoiceStoppedFlash);
            return WebReply.Ok;
        }

        if (request.Gone == true)
        {
            playback.Gone();
            _speech.Stop(speaker);
            return WebReply.Ok;
        }

        return playback.Played(request.Clip, request.Offset) == WebPlayedResult.Refused
            ? new WebReply(WebOutcome.Refused, WebUiText.BadRequest)
            : WebReply.Ok;
    }

    /// <summary>A clip of the page's speaker, when its turn is <paramref name="turn"/> and it holds clip <paramref name="n"/>.</summary>
    private byte[]? WebClip(string page, string turn, long n)
    {
        var (_, playback) = PageSpeakerOf(page);
        return playback is not null && playback.Turn == turn ? playback.Clip(n) : null;
    }

    /// <summary>Stops <paramref name="page"/>'s voice (that speaker alone), flashing <paramref name="flash"/> at the PC when it did.</summary>
    private void StopPageSpeech(string page, string? flash)
    {
        var (speaker, _) = PageSpeakerOf(page);
        if (speaker is null)
        {
            return;
        }

        _speech.Stop(speaker);
        DiagnosticLog.Info(WebUiCategory, WebUiText.VoiceStoppedLogLine(page, "the page asked"));
        if (flash is not null)
        {
            _webUi.FlashPc(flash);
        }
    }

    /// <summary>A page reloaded: its schedule is lost, so its speaker stops, counted heard.</summary>
    private void ReloadedPageSpeech(string page) => PageGoneSpeech(page);

    /// <summary>A page gone (past its grace, or reloaded): its speaker counts everything heard and stops, so Kokoro synthesizes into nothing no longer.</summary>
    private void PageGoneSpeech(string page)
    {
        var (speaker, playback) = PageSpeakerOf(page);
        if (speaker is null)
        {
            return;
        }

        playback?.Gone();
        _speech.Stop(speaker);
    }

    /// <summary>Every page's speech stopped (the pages' voice ending: a profile switch, the server stopping, voice off).</summary>
    private void StopAllPageSpeech()
    {
        List<(SpeechOutput Speaker, WebAudioPlayback? Playback)> speakers;
        lock (_voiceGate)
        {
            speakers = [.. _voicePages.Values.Where(e => e.Speaker is { Completion.IsCompleted: false }).Select(e => (e.Speaker!, e.Playback))];
        }

        StopSpeakers(speakers);
    }

    private void StopSpeakers(List<(SpeechOutput Speaker, WebAudioPlayback? Playback)> speakers)
    {
        foreach (var (speaker, playback) in speakers)
        {
            playback?.Gone();
            _speech.Stop(speaker);
        }
    }

    /// <summary>Whether <paramref name="page"/> has a speaker owing audio.</summary>
    private bool PageSpeaking(string page) => PageSpeakerOf(page).Playback?.Owed == true;

    /// <summary>The page's speaker as its snapshot carries it, or null.</summary>
    private WebSpeaking? PageSpeakingNow(string page) => PageSpeakerOf(page).Playback is { Owed: true } playback ? playback.Snapshot() : null;

    /// <summary>The page's listen began or ended: its speaker's silent rule pauses meanwhile (the page holds the clips).</summary>
    private void HoldPageSpeech(string page, bool on)
    {
        PageSpeakerOf(page).Playback?.Hold(on);
    }

    /// <summary>
    /// The idle line's tail stops follow the origin (2026-11-13): a PC gesture or line (<paramref name="page"/> null) stops only a PC tail,
    /// a page's line only that page's. Esc, the tail hook and the wake phrase over a tail stay "any tail".
    /// </summary>
    private async Task StopTailFromAsync(string? page)
    {
        if (_speech.Playing is not null && _speech.PlayingPage != page)
        {
            return;
        }

        await _speech.StopAsync().ConfigureAwait(false);
    }
}
