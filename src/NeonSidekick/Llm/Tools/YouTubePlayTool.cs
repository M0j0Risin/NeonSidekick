using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_play(video, start?)</c> (2026-10-05, the YouTube plan): a YouTube video in the app's video window — by its id or any
/// YouTube link (<see cref="YouTubeIds"/>), from <c>start</c> or the link's own time — waiting for the player to start it, cue it
/// (<c>YouTube autoplay</c> off) or refuse it (<see cref="YouTubeWait.Play"/>), and answering with what it reports
/// (<see cref="YouTubeText.Status"/>). One window: a second play switches it. Needs no key. Never headless: there is no window.
/// </summary>
public sealed class YouTubePlayTool : AIFunction
{
    public const string ToolName = "youtube_play";
    public const string VideoArgument = "video";
    public const string StartArgument = "start";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "video": { "type": "string", "description": "The video's 11-character id (from youtube_search) or a YouTube link." },
            "start": { "type": "string", "description": "Where to start: seconds (90) or a time (1:30). Leave it out to start at the beginning, or at a link's own t=." }
          },
          "required": ["video"]
        }
        """);

    private readonly IVideoPlayer _player;
    private readonly Func<AppSettingsData> _effective;
    private readonly TimeProvider _time;

    public YouTubePlayTool(IVideoPlayer player, Func<AppSettingsData> effective, TimeProvider? time = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _time = time ?? TimeProvider.System;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Plays a YouTube video, with sound, in the app's video window on the user's screen (one window: a new video replaces the one playing). " +
        "Give the id from " + YouTubeSearchTool.ToolName + " or a link the user pasted. Play only what the user asked for. " +
        "Then " + YouTubeControlTool.ToolName + " pauses, seeks or sets the volume, and " + YouTubeStatusTool.ToolName + " says where it is.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string video = ToolArguments.ReadString(arguments, VideoArgument);
        if (!YouTubeIds.TryParse(video, out string id, out double linkStart))
        {
            return YouTubeText.NotAVideo(video);
        }

        string startText = ToolArguments.ReadString(arguments, StartArgument).Trim();
        double start = linkStart;
        if (startText.Length > 0)
        {
            if (YouTubeIds.ParseTime(startText) is not { } seconds)
            {
                return YouTubeText.NotATime(StartArgument, startText);
            }

            start = seconds;
        }

        long before = _player.Snapshot?.Version ?? -1;
        bool autoplay = _effective().YouTubeAutoplay;
        try
        {
            _player.Play(new VideoRequest(id, start, autoplay));
        }
        catch (InvalidOperationException e)
        {
            return "Error: " + e.Message;
        }

        // Started, cued, ended at once or refused: anything but the window's own opening, and on the video asked for (its error
        // too, not a late one for the video before). The page holds its every-second report from a load until the player's next
        // change (player.html), so a replay of the same video at another time is not answered with the old position. Cued is an
        // answer only when the play asked for none (2026-10-07, found in the Mac live run and confirmed on Windows): a new window's
        // first report comes from the player's ready event, just before its playVideo shows, and read "Cued (press play to start)"
        // to the model and the saved session while the video started a second later. A play the browser keeps from starting stays
        // cued and is answered so at the wait's cap.
        var snapshot = await YouTubeWait.UntilAsync(_player, before, s => s.VideoId == id && (s.State is VideoState.Playing or VideoState.Paused or VideoState.Ended || (s.State == VideoState.Cued && !autoplay)), YouTubeWait.Play, _time, cancellationToken, video: id).ConfigureAwait(false);
        return snapshot is null ? YouTubeText.ClosedMeanwhile : YouTubeText.Status(snapshot);
    }
}
