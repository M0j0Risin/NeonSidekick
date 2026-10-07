using NeonSidekick.Viewer;

namespace NeonSidekick.YouTube;

/// <summary>
/// Keeps each saved video's place (2026-10-07, the user's ask: a saved video resumes where it was left). It listens to the video
/// window's reports (<see cref="IVideoPlayer.Changed"/>, as <see cref="VideoVoicePause"/> does) and, for a video on the profile's
/// list (<see cref="YouTubeLibrary"/>; any other is left alone), writes where it is: on a pause, at the end, when the window
/// switches to another video or closes — the window's last report is kept for that, since a closed window reports nothing — and
/// at most every <see cref="SaveEvery"/> while it plays, so a crash loses no more. A video stopped within
/// <see cref="YouTubeLibrary.FinishedWithin"/> of its end, or ended, goes back to the start and is marked watched. The title,
/// channel and length the player reports fill the entry in (a video saved by its link alone has none until it plays). Raised on
/// the window's thread: a write is a small file, done there, so the last one is down before the app's exit closes the window.
/// </summary>
public sealed class YouTubeResume
{
    /// <summary>How often the place is written while a saved video plays (2026-10-07).</summary>
    public static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(15);

    private readonly Func<YouTubeLibrary> _library;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private VideoSnapshot? _last;
    private string? _writtenFor;
    private DateTimeOffset _writtenAt;

    /// <param name="player">The video window; the resume listens to it for the app's life.</param>
    /// <param name="library">The loaded profile's list, asked for at each write, so a profile switch moves the resume with it.</param>
    public YouTubeResume(IVideoPlayer player, Func<YouTubeLibrary> library, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _time = time ?? TimeProvider.System;
        player.Changed += OnChanged;
    }

    private void OnChanged(VideoSnapshot? snapshot)
    {
        lock (_gate)
        {
            var last = _last;
            _last = snapshot;
            if (snapshot is null)
            {
                // The window closed: its last report is where the video was left.
                if (last is not null)
                {
                    Write(last, stopped: true);
                }

                return;
            }

            if (last is { VideoId: { } before } && before != snapshot.VideoId)
            {
                Write(last, stopped: true);   // switched to another video: the one before stopped where it was
            }

            switch (snapshot.State)
            {
                case VideoState.Paused:
                case VideoState.Ended:
                    Write(snapshot, stopped: true);
                    break;
                case VideoState.Playing when snapshot.VideoId != _writtenFor || _time.GetUtcNow() - _writtenAt >= SaveEvery:
                    Write(snapshot, stopped: false);
                    break;
            }
        }
    }

    /// <summary>
    /// <paramref name="snapshot"/>'s place written when its video is saved and the report says where it is (not the window opening,
    /// failing or refusing it, nor a video cued or not started). A stop near the end, or the end itself, starts it over next time.
    /// </summary>
    private void Write(VideoSnapshot snapshot, bool stopped)
    {
        if (snapshot.VideoId is not { } id || snapshot.Error is not null
            || snapshot.State is VideoState.Opening or VideoState.Failed or VideoState.Cued or VideoState.Unstarted)
        {
            return;
        }

        var now = _time.GetUtcNow();
        bool finished = snapshot.State == VideoState.Ended || (stopped && YouTubeLibrary.Finished(snapshot.Position, snapshot.Duration));
        _library().Update(id, saved => saved with
        {
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? saved.Title : snapshot.Title,
            Author = string.IsNullOrWhiteSpace(snapshot.Author) ? saved.Author : snapshot.Author,
            Duration = snapshot.Duration > 0 ? snapshot.Duration : saved.Duration,
            Position = finished ? 0 : Math.Max(0, snapshot.Position),
            Watched = saved.Watched || finished,
            LastPlayed = now,
        });
        _writtenFor = id;
        _writtenAt = now;
    }
}
