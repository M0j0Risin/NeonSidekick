namespace NeonSidekick.Viewer;

/// <summary>
/// The report a new video's load leaves behind, dropped (2026-10-07, found in the Mac live run). When the page loads a video into a
/// player that already has one (<c>loadVideoById</c>), YouTube's player first reports state 2, paused — the old video stopping —
/// already carrying the new video's id, with no title and no duration, and only then -1 and 3; measured in WebKit on macOS 15.7.9
/// within 40 ms of the load. <c>youtube_play</c>'s wait takes a paused report for the asked video as its answer, so a second play
/// answered "Paused" while the video started, and a video YouTube then refused (error 150) answered "Paused" instead of the refusal.
/// So from a load until the player's next other report, a paused report with no duration is not the new video's state and is not
/// passed on. The Mac window applies it; Windows' window does not (its Chromium was not measured here, and its behaviour stays as it
/// was). Pure; the window's thread only.
/// </summary>
public sealed class VideoLoadSettle
{
    private bool _loading;

    /// <summary>A load was sent to the page.</summary>
    public void Loaded() => _loading = true;

    /// <summary>
    /// Whether the page's <paramref name="kind"/> of message, folded into <paramref name="next"/>, is passed on: false only for the
    /// stopping report a load leaves behind. Any other state report or an error ends the load's watch.
    /// </summary>
    public bool Accept(VideoPageEvent kind, VideoSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (kind is not (VideoPageEvent.State or VideoPageEvent.Error))
        {
            return true;
        }

        if (_loading && kind == VideoPageEvent.State && next.State == VideoState.Paused && next.Duration <= 0)
        {
            return false;
        }

        _loading = false;
        return true;
    }
}
