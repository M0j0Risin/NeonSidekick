using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── Voice input during /botchat (2026-10-02) ──────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The running bot turn a push-to-talk (or the wake phrase) cuts short: its ladder, its number and its token.</summary>
    private sealed record BotTurnHandle(BotEscLadder Ladder, int Id, CancellationTokenSource Cts);

    /// <summary>The bot turn running now, or null between turns.</summary>
    private BotTurnHandle? _botTurn;

    /// <summary>The user asked to talk (the push-to-talk key, or the wake phrase with TTS off): the chat listens before the next bot.</summary>
    private volatile bool _botTalk;

    /// <summary>The wake phrase's arm during a botchat with TTS off; null when not armed.</summary>
    private CancellationTokenSource? _botWake;

    /// <summary>
    /// The push-to-talk key under a <c>/botchat</c> (2026-10-02, the user's report: the key did nothing there — voice input lived on
    /// the idle line alone, which a chat never returns to): the watchers' spend hook sees every key, and this one is the chat's.
    /// With voice input on it is <see cref="RequestBotTalk"/>; off, a notice says how to turn it on. False for every other key — and for
    /// a key that types a character: the push-to-talk keys type none, and a typed 's' shares F4's number (115) where a key reader
    /// fills the key from the character.
    /// </summary>
    private bool BotTalkKey(InputEvent input)
    {
        if (!_botChatRunning || input is not InputEvent.Key { Info: var key } || key.Key != _voice.PushToTalk || key.Modifiers != 0 || !char.IsControl(key.KeyChar) && key.KeyChar != default)
        {
            return false;
        }

        if (!_voice.Enabled)
        {
            Post(() => _transcript.Notice(VoiceOffHint));
            return true;
        }

        RequestBotTalk();
        return true;
    }

    /// <summary>
    /// The user wants to talk (2026-10-02, the user's call: stop the bot, then listen): the speaking voice stops at once, the bot still
    /// replying is cut short — what it wrote stays its line, as an ESC's skip leaves it, without arming the ladder — and a voice or
    /// pause being waited for ends; the chat's loop listens before it picks the next bot (<see cref="BotListenAsync"/>). Any thread.
    /// </summary>
    private void RequestBotTalk()
    {
        if (_botTalk)
        {
            return;
        }

        _botTalk = true;
        _speech.Stop();
        if (Volatile.Read(ref _botTurn) is { } turn)
        {
            turn.Ladder.Skip(turn.Id);
            VoiceSession.SafeCancel(turn.Cts);
        }

        // A wait for the voice or the pause is woken by an act; this one does nothing.
        Post(static () => { });
        DiagnosticLog.Debug(VoiceSession.Category, BotTalkLogLine);
    }

    /// <summary>The log's line for a talk asked for under a botchat.</summary>
    public const string BotTalkLogLine = "Botchat: the user asked to talk.";

    /// <summary>Marks <paramref name="turnCts"/> as the bot turn running now until the result is disposed.</summary>
    private IDisposable EnterBotTurn(BotEscLadder ladder, int id, CancellationTokenSource turnCts)
    {
        var handle = new BotTurnHandle(ladder, id, turnCts);
        Volatile.Write(ref _botTurn, handle);
        return new BotTurnScope(this, handle);
    }

    private sealed class BotTurnScope(ChatScreen screen, BotTurnHandle handle) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref screen._botTurn, null, handle);
    }

    /// <summary>
    /// Whether the wake phrase may start a talk in a botchat (2026-10-02, the user's call): only with no voice playing — TTS off (or
    /// not ready) — and the wake word ready; with the bots speaking out loud their own voices would set it off, so it is push-to-talk
    /// alone then.
    /// </summary>
    public static bool BotWakeAllowed(bool speaking, bool wakeReady) => !speaking && wakeReady;

    /// <summary>Arms the wake phrase for the chat when <see cref="BotWakeAllowed"/> says so and it is not armed yet; a hit is a <see cref="RequestBotTalk"/>.</summary>
    private void ArmBotWake(AppSettingsData effective)
    {
        if (_botWake is not null || !BotWakeAllowed(effective.TtsOutput && _speech.IsReady, _voice.WakeReady))
        {
            return;
        }

        var wake = new CancellationTokenSource();
        if (!_voice.ArmWake(wake))
        {
            wake.Dispose();
            return;
        }

        _botWake = wake;
        wake.Token.Register(RequestBotTalk);
    }

    /// <summary>Disarms the chat's wake phrase; what it heard (the seed of the listen), or null.</summary>
    private WakeHit? DisarmBotWake()
    {
        if (_botWake is not { } wake)
        {
            return null;
        }

        _botWake = null;
        var hit = _voice.DisarmWake();
        wake.Dispose();
        return hit;
    }

    /// <summary>
    /// The chat's listen, at its loop's top once a talk was asked for: the speaker quiet first, then the push-to-talk listen (or the
    /// wake phrase's, seeded with what it heard) under the spinner. Under <c>STT destination</c> <c>chat</c> the transcript is the user's
    /// line in the chat at once — the <c>›</c> row, then the next bot answers it; under <c>draft</c> it goes to the end of the input
    /// row's draft, sent with Enter as a typed interjection. True when the app token ended it.
    /// </summary>
    private async Task<bool> BotListenAsync(List<BotChatLine> lines, CancellationToken cancellationToken)
    {
        _botTalk = false;
        var hit = DisarmBotWake();
        await _speech.StopAsync().ConfigureAwait(false);
        if (!_voice.Enabled)
        {
            _transcript.Notice(VoiceOffHint);
            return false;
        }

        if (!_voice.IsReady)
        {
            _transcript.Warning(_voice.StatusLine());
            return false;
        }

        var target = SttDestinationMode.Resolve(_effective());
        string label = hit is null ? ListeningLabel(_voice.PushToTalkName) : WakeListeningLabel(_voice.WakePhrase, _voice.PushToTalkName);
        var listen = await ListenForRequestAsync(label, hit?.Seed, hit?.HasRequest ?? false, stripWakeWord: hit is not null, options: null, target, cancellationToken).ConfigureAwait(false);
        if (listen.Exit)
        {
            return true;
        }

        if (listen.Text is null)
        {
            return false;
        }

        if (listen.Text.Length == 0)
        {
            _transcript.Notice(HeardNothingNotice);
            return false;
        }

        if (target == SttTarget.Draft)
        {
            AppendSpokenToDraft(listen.Text);
            _input.Chat.Redraw();
            return false;
        }

        lines.Add(new BotChatLine(BotChat.UserName, listen.Text, IsUser: true));
        return false;
    }
}
