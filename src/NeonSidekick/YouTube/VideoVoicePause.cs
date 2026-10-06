using NeonSidekick.Viewer;

namespace NeonSidekick.YouTube;

/// <summary>The setting <c>YouTube voice</c>'s words (2026-10-05).</summary>
public static class YouTubeVoiceMode
{
    public const string Pause = "pause";
    public const string Duck = "duck";
    public const string None = "none";

    /// <summary>The default (the user's call, 2026-10-05: easier to manage than ducking, and speech input still hears a ducked video).</summary>
    public const string Default = Pause;

    public static readonly string[] Names = [Pause, Duck, None];

    /// <summary>The saved word, or the default for anything else.</summary>
    public static string Resolve(string? saved) =>
        Names.FirstOrDefault(n => string.Equals(n, saved?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>What a word does, for the picker's rows.</summary>
    public static string Describe(string name) => Resolve(name) switch
    {
        Pause => "the video pauses while the app speaks or listens to you, and plays on after",
        Duck => "the video turns down to " + VideoVoicePause.DuckVolume + "% while the app speaks or listens, and back up after",
        _ => "the video plays on whatever the app says or hears",
    };
}

/// <summary>
/// The video window and the app's voice (2026-10-05, the YouTube plan's Phase 6): while the app speaks a reply (from its first
/// audio to its last) or listens to a request (push-to-talk, or after the wake phrase), a playing video is paused — or turned down
/// under <c>duck</c> — and set back after, so the microphone does not hear it and the reply is not talked over. Not on the voice
/// detector: the video's own speech would trip it, and a pause on it would set itself off.
///
/// <para>Holds overlap (a reply's tail under the next listen): the first acts, the last sets back. Only what this did is set back:
/// a video someone else paused (the user, the model's <c>youtube_control</c>) stays paused, and one resumed or turned up while
/// held is left as it is. "Someone else" is read off the window's reports — once a report shows this pause (or this volume), a
/// later one that does not is another hand; the report right after a command still shows the old state, so it never counts.</para>
/// </summary>
public sealed class VideoVoicePause
{
    /// <summary>The volume <c>duck</c> turns a video down to.</summary>
    public const int DuckVolume = 15;

    private readonly IVideoPlayer _player;
    private readonly Func<string> _mode;
    private readonly Lock _gate = new();
    private int _holds;
    private bool _paused;
    private bool _sawPaused;
    private int? _duckedFrom;
    private bool _sawDucked;

    public VideoVoicePause(IVideoPlayer player, Func<string> mode)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _player.Changed += OnChanged;
    }

    /// <summary>A hold until the returned token is disposed (a listen).</summary>
    public IDisposable Hold()
    {
        Acquire();
        return new Releaser(this);
    }

    /// <summary>A hold until <paramref name="done"/> ends (a reply's audio, <c>SpeechOutput.Completion</c>).</summary>
    public void HoldUntil(Task done)
    {
        ArgumentNullException.ThrowIfNull(done);
        var hold = Hold();
        done.ContinueWith(_ => hold.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // The decisions under the gate, the commands outside it: a player may report from inside Send (the tests' fake does).
    private void Acquire()
    {
        VideoSnapshot? playing;
        string mode;
        lock (_gate)
        {
            if (_holds++ > 0)
            {
                return;
            }

            playing = _player.Snapshot is { State: VideoState.Playing or VideoState.Buffering } s ? s : null;
            mode = YouTubeVoiceMode.Resolve(_mode());
            if (playing is not null && mode == YouTubeVoiceMode.Pause)
            {
                _paused = true;
                _sawPaused = false;
            }
            else if (playing is { Muted: false, Volume: > DuckVolume } && mode == YouTubeVoiceMode.Duck)
            {
                _duckedFrom = playing.Volume;
                _sawDucked = false;
            }
            else
            {
                return;
            }
        }

        bool sent = mode == YouTubeVoiceMode.Pause ? _player.Send(VideoCommand.Pause) : _player.Send(VideoCommand.Volume(DuckVolume));
        if (!sent)
        {
            lock (_gate)
            {
                _paused = false;
                _duckedFrom = null;
            }
        }
    }

    private void ReleaseOne()
    {
        bool resume;
        int? restore;
        lock (_gate)
        {
            if (--_holds > 0)
            {
                return;
            }

            _holds = 0;
            resume = _paused && _player.Snapshot is { State: VideoState.Paused };
            restore = _duckedFrom is { } volume && _player.Snapshot is { Volume: DuckVolume } ? volume : null;
            _paused = false;
            _duckedFrom = null;
        }

        if (resume)
        {
            _player.Send(VideoCommand.Play);
        }

        if (restore is { } back)
        {
            _player.Send(VideoCommand.Volume(back));
        }
    }

    // Another hand while held: a report after this pause showed paused, and now one shows playing; or the duck's volume changed.
    private void OnChanged(VideoSnapshot? snapshot)
    {
        lock (_gate)
        {
            if (snapshot is null)
            {
                _paused = false;
                _duckedFrom = null;
                return;
            }

            if (_paused)
            {
                if (snapshot.State == VideoState.Paused)
                {
                    _sawPaused = true;
                }
                else if (_sawPaused && snapshot.State is VideoState.Playing or VideoState.Ended)
                {
                    _paused = false;
                }
            }

            if (_duckedFrom is not null)
            {
                if (snapshot.Volume == DuckVolume)
                {
                    _sawDucked = true;
                }
                else if (_sawDucked)
                {
                    _duckedFrom = null;
                }
            }
        }
    }

    private sealed class Releaser(VideoVoicePause owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                owner.ReleaseOne();
            }
        }
    }
}
