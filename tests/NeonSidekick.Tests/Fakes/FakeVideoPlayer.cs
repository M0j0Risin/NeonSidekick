using NeonSidekick.Viewer;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A video window for the tests (2026-10-05, the YouTube plan): every play and command recorded, a play that fails on demand,
/// and a page made up — <see cref="Report"/> raises a snapshot as the real window's page would, <see cref="Behave"/> answers each
/// command with one (playing after a play, paused after a pause, the position after a seek…) unless switched off, and
/// <see cref="CloseByUser"/> is the user's Esc. Raised on the caller's thread, where the real one raises on the window's. A playlist
/// (2026-10-08) plays <see cref="PlaylistVideos"/>: from the entry asked for (or the video named in it), and next, previous and an
/// entry move through them as YouTube's player would.
/// </summary>
public sealed class FakeVideoPlayer : IVideoPlayer
{
    private readonly Lock _gate = new();
    private VideoSnapshot? _snapshot;

    public List<VideoRequest> Plays { get; } = [];

    public List<VideoCommand> Commands { get; } = [];

    public int Closes { get; private set; }

    /// <summary>Thrown by <see cref="Play"/> (each play until set back to null), as the real one's no-runtime refusal.</summary>
    public string? PlayFailure { get; set; }

    /// <summary>Whether a play and each command are answered with the snapshot a real page would report (on by default).</summary>
    public bool Behave { get; set; } = true;

    /// <summary>The title and channel a played video reports.</summary>
    public string Title { get; set; } = "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film";

    public string Author { get; set; } = "Blender";

    public double Duration { get; set; } = 635;

    /// <summary>The videos any playlist played holds, in order.</summary>
    public List<string> PlaylistVideos { get; } = ["aqz-KE-bpKQ", "jNQXAC9IVRw", "dQw4w9WgXcQ"];

    public VideoSnapshot? Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public event Action<VideoSnapshot?>? Changed;

    public void Play(VideoRequest request)
    {
        if (PlayFailure is { } failure)
        {
            throw new InvalidOperationException(failure);
        }

        Plays.Add(request);
        Report(VideoSnapshot.Opening(request) with { Version = (Snapshot?.Version ?? 0) + 1 });
        if (!Behave)
        {
            return;
        }

        var state = request.Autoplay ? VideoState.Playing : VideoState.Cued;
        if (request.PlaylistId is { } list)
        {
            int named = request.VideoId is { } id ? PlaylistVideos.IndexOf(id) : -1;
            int entry = named >= 0 ? named : Math.Min(request.Entry, PlaylistVideos.Count - 1);
            Report(Entry(new VideoSnapshot(state, null, Volume: Snapshot?.Volume ?? 100, Muted: Snapshot?.Muted ?? false, PlaylistId: list), entry) with { State = state, Position = named >= 0 ? request.Start : 0 });
            return;
        }

        Report(new VideoSnapshot(state, request.VideoId, Title, Author, request.Start, Duration, Snapshot?.Volume ?? 100, Snapshot?.Muted ?? false));
    }

    // The playlist's entry <paramref name="entry"/>, playing from its start.
    private VideoSnapshot Entry(VideoSnapshot current, int entry) => current with
    {
        State = VideoState.Playing,
        VideoId = PlaylistVideos[entry],
        Title = Title + " " + (entry + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Author = Author,
        Position = 0,
        Duration = Duration,
        Error = null,
        Skipping = false,
        PlaylistEntry = entry,
        PlaylistCount = PlaylistVideos.Count,
    };

    public bool Send(VideoCommand command)
    {
        if (Snapshot is not { } current)
        {
            return false;
        }

        Commands.Add(command);
        if (Behave)
        {
            Report(command.Kind switch
            {
                VideoCommandKind.Play => current with { State = VideoState.Playing },
                VideoCommandKind.Pause => current with { State = VideoState.Paused },
                VideoCommandKind.Stop => current with { State = VideoState.Cued, Position = 0 },
                VideoCommandKind.Seek => current with { Position = Math.Min(command.Value, Duration) },
                VideoCommandKind.Volume => current with { Volume = (int)command.Value, Muted = command.Value == 0 && current.Muted },
                VideoCommandKind.Mute => current with { Muted = true },
                VideoCommandKind.Unmute => current with { Muted = false },
                VideoCommandKind.Next when current is { PlaylistId: not null, PlaylistEntry: { } at } && at + 1 < PlaylistVideos.Count => Entry(current, at + 1),
                VideoCommandKind.Previous when current is { PlaylistId: not null, PlaylistEntry: { } at } => Entry(current, Math.Max(0, at - 1)),
                VideoCommandKind.PlayAt when current.PlaylistId is not null && (int)command.Value < PlaylistVideos.Count => Entry(current, (int)command.Value),
                _ => current,
            });
        }

        return true;
    }

    public bool Close()
    {
        bool open = Snapshot is not null;
        Closes++;
        if (open)
        {
            Report(null);
        }

        return open;
    }

    /// <summary>The user closed the window (Esc, the ×).</summary>
    public void CloseByUser() => Report(null);

    /// <summary>A report from the page: the new snapshot (its version counted on), or null as the window closes.</summary>
    public void Report(VideoSnapshot? snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot is null ? null : snapshot with { Version = Math.Max(snapshot.Version, (_snapshot?.Version ?? 0) + 1) };
            snapshot = _snapshot;
        }

        Changed?.Invoke(snapshot);
    }
}
