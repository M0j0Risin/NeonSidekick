using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>play_video(path, start?, autoplay?)</c> (2026-11-12, the video-files plan): a video file on this computer (MP4, M4V, MOV, WebM, MKV;
/// <see cref="VideoFile"/>) under the working directory, played for the user where <c>Video files play in</c> says: the video window, the
/// Web UI's YouTube tab, or its default app. A Files-family tool beside <c>view_image</c>, under its own switch (<i>Video tools</i>, off by
/// default). Shaped as <see cref="YouTubePlayTool"/>: the play goes through the route hook (<c>ChatScreen.RouteFile</c>), which answers at
/// once for the default app; a play on one of the app's own players waits (<see cref="YouTubeWait.Play"/>) for the file's own report —
/// its token, so a late report for the video before is never the answer — and answers with <see cref="VideoText.FileStatus"/>. The path
/// resolves inside the sandbox, and a folder named <c>x.mp4</c> is refused as a missing file is. <c>autoplay</c> is its own (true unless
/// given): <i>YouTube autoplay</i> names YouTube. With no player at all (a screen with no video window) the route hands every play to the
/// default app. Never headless.
/// </summary>
public sealed class PlayVideoTool : FileTool
{
    public const string ToolName = "play_video";
    public const string StartArgument = "start";
    public const string AutoplayArgument = "autoplay";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The video file (mp4, m4v, mov, webm or mkv), relative to the working directory or a full path inside it." },
            "start": { "type": "string", "description": "Where to start: seconds (90) or a time (1:30). Leave it out to start at the beginning." },
            "autoplay": { "type": "boolean", "description": "false loads it paused at start. Leave it out to play at once." }
          },
          "required": ["path"]
        }
        """);

    private readonly IVideoPlayer? _player;
    private readonly TimeProvider _time;
    private readonly Func<VideoRequest, VideoRouted>? _route;
    private readonly Func<VideoFilesPlayInKind>? _target;

    /// <param name="player">The player whose reports the wait reads (the router); null with no video window, every play then the route's hand-off.</param>
    /// <param name="files">The sandbox the path resolves in.</param>
    /// <param name="route">Where a play goes (<c>ChatScreen.RouteFile</c>); null plays on <paramref name="player"/>.</param>
    /// <param name="target">Where a play would go now, for the description; null is the video window.</param>
    public PlayVideoTool(IVideoPlayer? player, WorkingDirectory files, TimeProvider? time = null, Func<VideoRequest, VideoRouted>? route = null, Func<VideoFilesPlayInKind>? target = null)
        : base(files)
    {
        _player = player;
        _time = time ?? TimeProvider.System;
        _route = route;
        _target = target;
    }

    public override string Name => ToolName;

    /// <summary>Follows <c>Video files play in</c>, read at each turn. Pinned.</summary>
    public override string Description => DescriptionFor(_target?.Invoke() ?? VideoFilesPlayInKind.VideoWindow);

    /// <summary>The description where a play goes to <paramref name="target"/>.</summary>
    public static string DescriptionFor(VideoFilesPlayInKind target) => target switch
    {
        VideoFilesPlayInKind.WebUiWindow =>
            What + "for the user, with sound, in the Web UI's YouTube tab on their phone or browser (one player: a new video replaces the one playing; with no tab in view it waits for one). " +
            Unseen + Then + Only,
        VideoFilesPlayInKind.DefaultApp =>
            "Opens a video file on this computer (not YouTube) in the user's own video app. Once it opens it is out of your control: " + VideoControlTool.ToolName + " can't see or drive it. " + Only,
        _ =>
            What + "for the user, with sound, in the app's video window on their screen (one window: a new video replaces the one playing). " +
            Unseen + Then + Only,
    };

    private const string What = "Plays a video file on this computer (not YouTube: an mp4, m4v, mov, webm or mkv file in the working directory) ";

    private const string Unseen = "You see none of it. ";

    private const string Then = "Then " + VideoControlTool.ToolName + " pauses, seeks, sets the volume or says where it is. ";

    private const string Only = "Play only what the user asked for.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!RequirePath(arguments, PathArgument, out string path, out string error))
        {
            return error;
        }

        string startText = ToolArguments.ReadString(arguments, StartArgument).Trim();
        double start = 0;
        if (startText.Length > 0)
        {
            if (YouTubeIds.ParseTime(startText) is not { } seconds)
            {
                return YouTubeText.NotATime(StartArgument, startText);
            }

            start = seconds;
        }

        if (!ToolArguments.TryReadBoolean(arguments, AutoplayArgument, out bool? autoplayRead, out string raw))
        {
            return FileText.BadBoolean(AutoplayArgument, raw);
        }

        bool autoplay = autoplayRead ?? true;
        return await PlayAsync(path, start, autoplay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="path"/> resolved, checked and played: the one way <c>/view</c>, <c>/video</c> and the model start a file, so the
    /// refusals, the wait and the words are the same for all three.
    /// </summary>
    public async Task<string> PlayAsync(string path, double start, bool autoplay, CancellationToken cancellationToken)
    {
        var outcome = Files.Resolve(path, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return FileText.Error(outcome, FileText.Shown(path), "play");
        }

        if (!VideoFile.IsVideoPath(full))
        {
            return VideoText.NotAVideoFile(FileText.Shown(path));
        }

        if (!File.Exists(full))
        {
            return Directory.Exists(full) ? VideoText.NotAVideoFile(FileText.Shown(path)) : FileText.Error(FileOutcome.Missing, FileText.Shown(path), "play");
        }

        var request = VideoRequest.File(full, start, autoplay);
        string token = request.FileToken!;
        long before = _player?.Snapshot?.Version ?? -1;
        VideoRouted routed;
        try
        {
            routed = Route(request);
        }
        catch (InvalidOperationException e)
        {
            return "Error: " + e.Message;
        }

        if (!routed.Wait)
        {
            return (routed.Note ?? "") + routed.Answer;
        }

        if (_player is null)
        {
            return (routed.Note ?? "") + VideoText.NoFile;
        }

        // Started, ready (autoplay off), ended at once or refused, by this play's own token; a file held for a YouTube tab with no page to
        // see the ask is an answer at once, as youtube_play's is.
        var snapshot = await YouTubeWait.UntilAsync(_player, before, s => s.FileToken == token && (s.Held is { Nudged: false }
            || s.State is VideoState.Playing or VideoState.Paused or VideoState.Ended || (s.State == VideoState.Cued && !autoplay)), YouTubeWait.Play, _time, cancellationToken, video: token).ConfigureAwait(false);
        return (routed.Note ?? "") + (snapshot is null ? YouTubeText.ClosedMeanwhileIn(_player.Place) : YouTubeText.Status(snapshot));
    }

    // The play through the route, or on the player as a test builds it.
    private VideoRouted Route(VideoRequest request)
    {
        if (_route is not null)
        {
            return _route(request);
        }

        if (_player is null)
        {
            throw new InvalidOperationException(VideoText.NoFilePlayer);
        }

        _player.Play(request);
        return VideoRouted.Waited;
    }
}
