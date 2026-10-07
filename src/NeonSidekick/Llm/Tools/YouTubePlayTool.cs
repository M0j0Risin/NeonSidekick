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
/// A saved video played with no time named resumes where it was left (2026-10-07, <see cref="YouTubeLibrary.ResumeAt"/>), and the
/// answer says so (<see cref="YouTubeText.Resumed"/>); a <c>start</c> or a link's own time wins.
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
    private readonly Func<YouTubeLibrary>? _library;

    /// <param name="library">The profile's saved videos, whose places a play with no time resumes; null for none.</param>
    public YouTubePlayTool(IVideoPlayer player, Func<AppSettingsData> effective, TimeProvider? time = null, Func<YouTubeLibrary>? library = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _time = time ?? TimeProvider.System;
        _library = library;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Plays a YouTube video, with sound, in the app's video window on the user's screen (one window: a new video replaces the one playing). " +
        "Give the id from " + YouTubeSearchTool.ToolName + " or a link the user pasted. Play only what the user asked for. " +
        "Then " + YouTubeControlTool.ToolName + " pauses, seeks or sets the volume, and " + YouTubeStatusTool.ToolName + " says where it is. " +
        "A saved video (" + YouTubeSavedTool.ToolName + ") resumes where it was left unless start is given.";

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

        // A saved video with no time named picks up where it was left (2026-10-07).
        double resumed = startText.Length == 0 && linkStart <= 0 && _library?.Invoke() is { } library ? library.ResumeAt(id) : 0;
        if (resumed > 0)
        {
            start = resumed;
        }

        long before = _player.Snapshot?.Version ?? -1;
        try
        {
            _player.Play(new VideoRequest(id, start, _effective().YouTubeAutoplay));
        }
        catch (InvalidOperationException e)
        {
            return "Error: " + e.Message;
        }

        // Started, cued, ended at once or refused: anything but the window's own opening, and on the video asked for (its error
        // too, not a late one for the video before). The page holds its every-second report from a load until the player's next
        // change (player.html), so a replay of the same video at another time is not answered with the old position.
        var snapshot = await YouTubeWait.UntilAsync(_player, before, s => s.VideoId == id && s.State is VideoState.Playing or VideoState.Paused or VideoState.Cued or VideoState.Ended, YouTubeWait.Play, _time, cancellationToken, video: id).ConfigureAwait(false);
        if (snapshot is null)
        {
            return YouTubeText.ClosedMeanwhile;
        }

        string status = YouTubeText.Status(snapshot);
        return resumed > 0 && !status.StartsWith("Error: ", StringComparison.Ordinal) ? YouTubeText.Resumed(resumed) + status : status;
    }
}
