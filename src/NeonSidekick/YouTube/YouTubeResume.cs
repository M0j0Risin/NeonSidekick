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
/// A playlist's entries leave the videos' own rows alone (2026-10-08): the player starts each at its beginning, so a saved video
/// reached in a list and paused early would lose the place it was saved at. A saved playlist keeps its own place instead (2026-11-08,
/// the user's ask): its row takes the entry playing, that entry's video and the place in it, never the title or channel (the player
/// reports the video's, not the list's). An entry seen to the end moves the row on to the next entry, and the last one back to the
/// first, the list then marked watched. A report that cannot say which entry plays yet writes nothing.
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
                case VideoState.Playing when Key(snapshot) != _writtenFor || _time.GetUtcNow() - _writtenAt >= SaveEvery:
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
        if (snapshot.PlaylistId is { } list)
        {
            if (snapshot.PlaylistEntry is { } entry)
            {
                _library().Update(list, saved => saved.Playlist ? Entry(saved, snapshot, id, entry, finished, now) : saved);
                _writtenFor = Key(snapshot);
                _writtenAt = now;
            }

            return;
        }

        _library().Update(id, saved => saved.Playlist ? saved : saved with
        {
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? saved.Title : snapshot.Title,
            Author = string.IsNullOrWhiteSpace(snapshot.Author) ? saved.Author : snapshot.Author,
            Duration = snapshot.Duration > 0 ? snapshot.Duration : saved.Duration,
            Position = finished ? 0 : Math.Max(0, snapshot.Position),
            Watched = saved.Watched || finished,
            LastPlayed = now,
        });
        _writtenFor = Key(snapshot);
        _writtenAt = now;
    }

    /// <summary>What the throttle tells apart (2026-11-08): the video, and the list it plays in, so two entries are never one.</summary>
    private static string? Key(VideoSnapshot snapshot) => snapshot.PlaylistId is { } list ? list + "/" + snapshot.VideoId : snapshot.VideoId;

    /// <summary>
    /// A saved playlist's row after a report of <paramref name="entry"/> playing <paramref name="video"/>: the entry and place, or — that
    /// entry seen to the end — the next entry from its start (its video unknown yet, so a resume never finds the old one and plays it
    /// again), the last entry going back to the first and marking the list watched. A count the player cannot give keeps the saved one.
    /// </summary>
    private static YouTubeSaved Entry(YouTubeSaved saved, VideoSnapshot snapshot, string video, int entry, bool finished, DateTimeOffset now)
    {
        int count = snapshot.PlaylistCount is { } reported && reported > 0 ? reported : saved.Count;
        if (!finished)
        {
            return saved with
            {
                Entry = entry,
                EntryVideo = video,
                Count = count,
                Position = Math.Max(0, snapshot.Position),
                Duration = snapshot.Duration > 0 ? snapshot.Duration : saved.EntryVideo == video ? saved.Duration : 0,
                LastPlayed = now,
            };
        }

        bool last = count > 0 && entry >= count - 1;
        return saved with
        {
            Entry = last ? 0 : entry + 1,
            EntryVideo = null,
            Count = count,
            Position = 0,
            Duration = 0,
            Watched = saved.Watched || last,
            LastPlayed = now,
        };
    }
}
