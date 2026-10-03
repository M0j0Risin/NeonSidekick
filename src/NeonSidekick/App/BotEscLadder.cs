namespace NeonSidekick.App;

/// <summary>What one ESC (or Ctrl+C) press does to a running <c>/botchat</c> (<see cref="BotEscLadder"/>).</summary>
internal enum BotPress
{
    /// <summary>The speaking bot's voice stops; its reply runs on, silent.</summary>
    StopVoice,

    /// <summary>The bot now replying is cut short; the chat goes on with the next one.</summary>
    SkipBot,

    /// <summary>The whole chat ends.</summary>
    EndChat,
}

/// <summary>
/// The <c>/botchat</c> ESC ladder (2026-09-25, the user's ask): the first press stops the speaking bot's voice, the next
/// cuts that bot's reply short — the chat going on with the next bot —, the next ends the chat, and one more, at the idle
/// line, clears the draft. One ladder per chat; the bots' turns are numbered (<see cref="BeginBot"/>).
///
/// <para>A press (<see cref="Press"/>): the voice first, when one is heard; else, when an earlier bot's press armed the
/// ladder and this bot has not yet shown a word nor made a sound, the chat ends — the press that follows a skip; else a bot
/// still replying is cut short; else (its text done, its voice stopped or heard out) the chat ends. Every stop and skip arms
/// the ladder at its bot. The arm carries to the next bot only until that bot shows or speaks: pressed after that, the press
/// is that bot's own first again. A picture's spinner never asks the ladder (ESC there skips the picture alone).</para>
///
/// <para>Pressed on the key watcher's task, read on the chat's; one lock. Pure: the screen does what the answer says.</para>
/// </summary>
internal sealed class BotEscLadder
{
    private readonly object _gate = new();
    private int _turn;
    private int _armedAt = -1;
    private int _skipped = -1;
    private bool _endRequested;

    /// <summary>A press ended the chat (<see cref="BotPress.EndChat"/>).</summary>
    public bool EndRequested
    {
        get
        {
            lock (_gate)
            {
                return _endRequested;
            }
        }
    }

    /// <summary>The next bot's turn begins: its number, for the presses under it.</summary>
    public int BeginBot()
    {
        lock (_gate)
        {
            return ++_turn;
        }
    }

    /// <summary>
    /// One press under bot turn <paramref name="turn"/>: <paramref name="shown"/> is whether that bot has put a word on the
    /// screen or a sound on the device, <paramref name="voiceAudible"/> whether its voice is being heard (and not stopped
    /// already), <paramref name="responding"/> whether its reply is still being written.
    /// </summary>
    public BotPress Press(int turn, bool shown, bool voiceAudible, bool responding)
    {
        lock (_gate)
        {
            if (voiceAudible)
            {
                _armedAt = turn;
                return BotPress.StopVoice;
            }

            if (_armedAt >= 0 && _armedAt < turn && !shown)
            {
                _endRequested = true;
                return BotPress.EndChat;
            }

            if (responding)
            {
                _armedAt = turn;
                _skipped = turn;
                return BotPress.SkipBot;
            }

            _endRequested = true;
            return BotPress.EndChat;
        }
    }

    /// <summary>
    /// Ends the chat from outside the ladder (2026-10-01, the embedded model's kill switch, Ctrl+Alt+X): the bots' servers are
    /// gone, and the next bot's link would only start one again.
    /// </summary>
    public void End()
    {
        lock (_gate)
        {
            _endRequested = true;
        }
    }

    /// <summary>Whether a press cut bot turn <paramref name="turn"/> short (<see cref="BotPress.SkipBot"/>), without taking it.</summary>
    /// <summary>
    /// Bot turn <paramref name="turn"/> cut short by the user's push-to-talk or wake phrase (2026-10-02): a skip as an ESC's, the chat
    /// going on, but nothing armed — an ESC after the user spoke is a first press again, never the chat's end.
    /// </summary>
    public void Skip(int turn)
    {
        lock (_gate)
        {
            _skipped = turn;
        }
    }

    public bool Skipped(int turn)
    {
        lock (_gate)
        {
            return _skipped == turn;
        }
    }
}
