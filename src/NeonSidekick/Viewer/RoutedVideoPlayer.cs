using NeonSidekick.Diagnostics;
using NeonSidekick.YouTube;

namespace NeonSidekick.Viewer;

/// <summary>The app's two players a <see cref="RoutedVideoPlayer"/> chooses between (2026-10-16).</summary>
public enum VideoSink
{
    /// <summary>The video window on the PC (or the Mac).</summary>
    Window,

    /// <summary>The Web UI's YouTube tab (<c>WebUi.WebVideoPlayer</c>).</summary>
    Web,
}

/// <summary>
/// A video sent to a browser instead of a player of the app's own (2026-10-16, <c>YouTube open links in</c> <c>browser-window</c> or
/// <c>default-browser</c>): its page, where it went after any fallback (<see cref="YouTubeOpenLinksInKind.BrowserWindow"/> or
/// <see cref="YouTubeOpenLinksInKind.DefaultBrowser"/>), what it was, and the router's version when it went. Status and control name it,
/// since neither can see or drive a browser.
/// </summary>
public sealed record VideoHandoff(string Url, YouTubeOpenLinksInKind To, string? VideoId, string? PlaylistId, long Version = 0);

/// <summary>
/// The one <see cref="IVideoPlayer"/> the chat holds (2026-10-16, <c>YouTube open links in</c>): the video window and, when there is one,
/// the Web UI's player, one of them active. Status, commands, closes and every listener (<see cref="YouTubeResume"/>,
/// <see cref="VideoVoicePause"/>, the waits) reach the active one through it. Portable.
///
/// <para><b>Switching.</b> Only two things change the active player: a <see cref="Play(VideoRequest, VideoSink)"/> naming the other one, and
/// the web player's own <see cref="IVideoTakeover.TookOver"/> (its Resume tap). A play on <c>S</c>: first it waits for <c>S</c>'s own close
/// still running, if any (bounded by <see cref="CloseWait"/>; a window's close still running would close the new window, and its null,
/// landing while <c>S</c> is inactive, is never forwarded); then, under the lock, <c>S</c> becomes active and its pending drop clears (the
/// real windows discard a replaced window's null themselves, so a drop left over would swallow the new window's own close); outside it,
/// the router's own null once when the old player had a video open, so the resume saves its place and the voice pause lets go; the old
/// player closed off the caller's thread (a window still starting can hold its close for 12 s, and the caller may be a Kestrel request
/// or the tool's play to the phone); then the play. A switch counts no drop: only the active player's events are forwarded.</para>
///
/// <para><b>A hand-off</b> (<see cref="Handoff"/>) closes the active player's video, which stays active, with the router's own null, and
/// counts one null of that player's to drop (taken back when its close finds nothing open); the next play clears it. While a player's close
/// runs the router reads it as closed: its snapshot null and its commands refused.</para>
///
/// <para><b>Versions</b> climb across a switch: each player counts on its own, and the fake and the web player may start over at 1, so the
/// router keeps the highest version it forwarded and lifts a player's offset whenever its next report would not be newer.</para>
/// </summary>
public sealed class RoutedVideoPlayer : IVideoPlayer
{
    /// <summary>How long a play waits for its player's own close still running: a window's start (10 s) and its thread's join (2 s).</summary>
    public static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(12);

    private const string Category = "YouTube";

    private readonly IVideoPlayer[] _players;
    private readonly Lock _gate = new();
    private readonly long[] _offset = new long[2];
    private readonly int[] _drops = new int[2];
    private readonly Task?[] _closing = new Task?[2];
    private VideoSink _active = VideoSink.Window;
    private long _highest;
    private VideoHandoff? _handoff;

    /// <param name="window">The video window.</param>
    /// <param name="web">The Web UI's player, or null with none (headless never builds a router).</param>
    public RoutedVideoPlayer(IVideoPlayer window, IVideoPlayer? web)
    {
        ArgumentNullException.ThrowIfNull(window);
        _players = [window, web ?? window];
        HasWeb = web is not null;
        window.Changed += s => OnReport(VideoSink.Window, s);
        if (web is not null)
        {
            web.Changed += s => OnReport(VideoSink.Web, s);
            if (web is IVideoTakeover takeover)
            {
                takeover.TookOver += () => Switch(VideoSink.Web);
            }
        }
    }

    /// <summary>Whether there is a web player to route to.</summary>
    public bool HasWeb { get; }

    /// <summary>The player in use now.</summary>
    public VideoSink Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public VideoPlace Place => Active == VideoSink.Web ? VideoPlace.WebTab : VideoPlace.Window;

    /// <summary>The video last sent to a browser, until the next play; null otherwise.</summary>
    public VideoHandoff? LastHandoff
    {
        get
        {
            lock (_gate)
            {
                return _handoff;
            }
        }
    }

    public event Action<VideoSnapshot?>? Changed;

    public VideoSnapshot? Snapshot
    {
        get
        {
            lock (_gate)
            {
                int i = (int)_active;
                if (_closing[i] is not null)
                {
                    return null;   // closing: the router already called it closed
                }

                return _players[i].Snapshot is { } s ? s with { Version = s.Version + _offset[i] } : null;
            }
        }
    }

    /// <summary>A play on the active player (the tests, and any caller with no route).</summary>
    public void Play(VideoRequest request) => Play(request, Active);

    /// <summary>
    /// <paramref name="request"/> played on <paramref name="sink"/>, switching to it first when it is not the active one. Throws as that
    /// player's <see cref="IVideoPlayer.Play"/> does (no window to have).
    /// </summary>
    public void Play(VideoRequest request, VideoSink sink)
    {
        ArgumentNullException.ThrowIfNull(request);
        Switch(sink, clearHandoff: true);
        _players[(int)sink].Play(request);
    }

    public bool Send(VideoCommand command)
    {
        IVideoPlayer player;
        lock (_gate)
        {
            int i = (int)_active;
            if (_closing[i] is not null)
            {
                return false;
            }

            player = _players[i];
        }

        return player.Send(command);
    }

    /// <summary>
    /// The active player's video closed, synchronously as before the router (<c>youtube_control close</c>, <c>/youtube close</c>): no switch,
    /// no drop counted, and that player's null forwarded as ever. False when nothing was open, or its close already runs.
    /// </summary>
    public bool Close()
    {
        IVideoPlayer player;
        lock (_gate)
        {
            int i = (int)_active;
            if (_closing[i] is not null)
            {
                return false;
            }

            player = _players[i];
        }

        return player.Close();
    }

    /// <summary>
    /// <paramref name="handoff"/> recorded: a video sent to a browser. The active player's video, if any, closes with the router's own null
    /// (two videos never play at once), its own null dropped when it comes; the next play clears the record.
    /// </summary>
    public void Handoff(VideoHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        bool open;
        VideoSink sink;
        lock (_gate)
        {
            sink = _active;
            int i = (int)sink;
            _handoff = handoff with { Version = ++_highest };
            open = _closing[i] is null && _players[i].Snapshot is not null;
            if (open)
            {
                _drops[i]++;
            }
        }

        if (open)
        {
            Raise(null);
            StartClose(sink, countBack: true);
        }
    }

    /// <summary>Every close the router started still running, as one task (the tests await it rather than sleep).</summary>
    internal Task PendingClose
    {
        get
        {
            lock (_gate)
            {
                var running = _closing.Where(t => t is not null).Select(t => t!).ToArray();
                return running.Length == 0 ? Task.CompletedTask : Task.WhenAll(running);
            }
        }
    }

    // Steps 0 to 3 of a play on sink (the class's remarks): its own close waited for, the switch under the lock, the router's null
    // outside it when the old one had a video open, the old one's close off the caller's thread.
    private void Switch(VideoSink sink, bool clearHandoff = false)
    {
        Task? pending;
        lock (_gate)
        {
            pending = _closing[(int)sink];
        }

        if (pending is not null)
        {
            try
            {
                pending.Wait(CloseWait);
            }
            catch (AggregateException)
            {
                // logged by the close itself
            }
        }

        bool raise = false;
        VideoSink old;
        bool close = false;
        lock (_gate)
        {
            old = _active;
            if (clearHandoff)
            {
                _handoff = null;
            }

            if (old != sink)
            {
                int o = (int)old;
                raise = _closing[o] is null && _players[o].Snapshot is not null;
                close = _closing[o] is null;
                _active = sink;
            }

            _drops[(int)sink] = 0;
        }

        if (raise)
        {
            Raise(null);
        }

        if (close)
        {
            StartClose(old, countBack: false);
        }
    }

    // A player's close on the pool, kept as its task until it ends. A hand-off's close that finds nothing open takes its drop back.
    private void StartClose(VideoSink sink, bool countBack)
    {
        int i = (int)sink;
        var player = _players[i];
        Task? self = null;
        self = new Task(() =>
        {
            try
            {
                if (!player.Close() && countBack)
                {
                    lock (_gate)
                    {
                        _drops[i] = Math.Max(0, _drops[i] - 1);
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DiagnosticLog.Error(Category, YouTubeText.CloseFailedLogLine(sink == VideoSink.Web ? VideoPlace.WebTab : VideoPlace.Window), e);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_closing[i], self))
                    {
                        _closing[i] = null;
                    }
                }
            }
        });
        lock (_gate)
        {
            _closing[i] = self;
        }

        self.Start(TaskScheduler.Default);
    }

    // A player's report: forwarded only from the active one, never while its close runs; its null dropped while one is counted; its version
    // lifted past the highest forwarded.
    private void OnReport(VideoSink from, VideoSnapshot? snapshot)
    {
        VideoSnapshot? forward;
        lock (_gate)
        {
            int i = (int)from;
            if (from != _active)
            {
                return;
            }

            if (snapshot is null)
            {
                if (_drops[i] > 0)
                {
                    _drops[i]--;
                    return;
                }

                if (_closing[i] is not null)
                {
                    return;   // the router raised its own null already
                }

                forward = null;
            }
            else
            {
                if (_closing[i] is not null)
                {
                    return;   // a closing player's last tick
                }

                long version = snapshot.Version + _offset[i];
                if (version <= _highest)
                {
                    _offset[i] += _highest + 1 - version;
                    version = _highest + 1;
                }

                _highest = version;
                forward = snapshot with { Version = version };
            }
        }

        Raise(forward);
    }

    private void Raise(VideoSnapshot? snapshot)
    {
        try
        {
            Changed?.Invoke(snapshot);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Error(Category, "A video listener failed.", e);
        }
    }
}
