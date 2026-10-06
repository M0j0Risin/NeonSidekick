using NeonSidekick.Viewer;

namespace NeonSidekick.YouTube;

/// <summary>
/// Waiting on the video window's reports (2026-10-05): a command takes effect as the player gets to it, and the report the page
/// sends at once still shows the state before it (the harness: a pause's first report said playing, the next, 4 ms on, paused).
/// So a tool waits for a report that shows the change — newer than the one before it, and meeting the tool's test — or the
/// window closing, with a short cap; at the cap it answers with the newest report there is. Never throws on the window.
/// </summary>
public static class YouTubeWait
{
    /// <summary>How long a play waits for the player to start or refuse: the page, the player and the first frame.</summary>
    public static readonly TimeSpan Play = TimeSpan.FromSeconds(12);

    /// <summary>How long a command waits for the player to show it.</summary>
    public static readonly TimeSpan Command = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The first snapshot after <paramref name="after"/> (a <see cref="VideoSnapshot.Version"/>; -1 for any) that <paramref name="done"/>
    /// accepts, or null as the window closes; at <paramref name="timeout"/>, the newest there is.
    /// </summary>
    public static async Task<VideoSnapshot?> UntilAsync(IVideoPlayer player, long after, Func<VideoSnapshot, bool> done, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(time);
        var arrived = new TaskCompletionSource<VideoSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Accepts(VideoSnapshot? s) => s is null || (s.Version > after && (done(s) || s.State == VideoState.Failed || s.Error is not null));
        void OnChanged(VideoSnapshot? s)
        {
            if (Accepts(s))
            {
                arrived.TrySetResult(s);
            }
        }

        player.Changed += OnChanged;
        try
        {
            var now = player.Snapshot;
            if (Accepts(now))
            {
                return now;
            }

            try
            {
                return await arrived.Task.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return player.Snapshot;
            }
        }
        finally
        {
            player.Changed -= OnChanged;
        }
    }
}
