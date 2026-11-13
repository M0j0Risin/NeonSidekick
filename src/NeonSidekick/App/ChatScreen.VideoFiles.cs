using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.App;

// ── Video files (2026-11-12, the video-files plan) ──────────────────────────

internal sealed partial class ChatScreen
{
    private const string VideoCategory = "Video";

    /// <summary>
    /// The video file last sent to its default app with no router to record it (a screen with no video window): its name, so
    /// <c>video_control</c> can say where it went. With a router the hand-off is the router's (<see cref="RoutedVideoPlayer.LastHandoff"/>).
    /// </summary>
    private string? _fileHandoffName;

    /// <summary>
    /// Where a video file plays now (<c>Video files play in</c>), worked out once for the play, the spinner's words and the tools'
    /// descriptions, so they never disagree. <c>webui-window</c> copies <see cref="PlayTarget"/>'s web test (a router with a web sink,
    /// <i>Web UI</i> on and serving, <i>Web UI video controls</i> on) and falls back to the video window; the video window falls back to the
    /// default app where there is none or it plays no files (a Mac's). So every fallback ends at the default app, and the note leading
    /// the answer names where the play went.
    /// </summary>
    private (VideoFilesPlayInKind Kind, string? Note) FilePlayTarget()
    {
        var effective = _effective();
        var setting = VideoFilesPlayIn.Resolve(effective);
        string? note = null;
        if (setting == VideoFilesPlayInKind.WebUiWindow)
        {
            if (_routedVideo is not { HasWeb: true } || !effective.WebUi || !_webUiController.Serving)
            {
                (setting, note) = (VideoFilesPlayInKind.VideoWindow, VideoText.WebUiOffFellBack);
            }
            else if (!effective.WebUiYouTubeControls)
            {
                (setting, note) = (VideoFilesPlayInKind.VideoWindow, VideoText.WebUiControlsOffFellBack);
            }
            else
            {
                return (setting, null);
            }
        }

        if (setting == VideoFilesPlayInKind.VideoWindow)
        {
            // The default app's note alone: "plays in the video window instead" would be wrong once the window can't either.
            if (_routedVideo is null)
            {
                return (VideoFilesPlayInKind.DefaultApp, VideoText.DefaultAppFellBack(VideoText.NoWindowHere));
            }

            if (!_routedVideo.PlaysFilesOn(VideoSink.Window))
            {
                return (VideoFilesPlayInKind.DefaultApp, VideoText.DefaultAppFellBack(VideoText.WindowPlaysNoFiles));
            }
        }

        return (setting, note);
    }

    /// <summary>
    /// A video file played where <see cref="FilePlayTarget"/> says: on the router's window or web sink (waited for by the tool), or in its
    /// default app (answered at once). Unlike <see cref="RouteVideo"/>, nothing throws: a sink that refuses the file or a window that can't
    /// open sends it to the default app, the reason leading the answer.
    /// </summary>
    private VideoRouted RouteFile(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (target, note) = FilePlayTarget();
        DiagnosticLog.Info(VideoCategory, $"Video files play in {VideoFilesPlayIn.Name(VideoFilesPlayIn.Resolve(_effective()))}: {request.FileName} to {VideoFilesPlayIn.Name(target)}" + (note is null ? "." : " (" + note.Trim() + ")"));
        if (target != VideoFilesPlayInKind.DefaultApp && _routedVideo is { } router)
        {
            try
            {
                router.Play(request, target == VideoFilesPlayInKind.WebUiWindow ? VideoSink.Web : VideoSink.Window);
                _fileHandoffName = null;
                return new VideoRouted(true, null, note);
            }
            catch (InvalidOperationException e)
            {
                DiagnosticLog.Info(VideoCategory, $"{request.FileName} could not play in {VideoFilesPlayIn.Name(target)} ({e.Message}); its default app has it.");
                note = VideoText.DefaultAppFellBack(e.Message);
            }
        }

        return OpenFileInDefaultApp(request, note);
    }

    /// <summary>
    /// A video file sent to its default app through the <c>open</c> tool's path (<see cref="WorkingDirectory.Open"/> with
    /// <see cref="OpenRules.Refusal"/>, the counted shell-open site behind <c>_openFile</c>), recorded as a hand-off only once it opened, as
    /// <see cref="HandOff"/> does: a failed open leaves the playing video alone and answers with the open's error.
    /// </summary>
    private VideoRouted OpenFileInDefaultApp(VideoRequest request, string? note)
    {
        string path = request.FilePath ?? throw new ArgumentException(VideoText.NotAFullPath(null), nameof(request));
        OpenRefusal? refused = null;
        var result = _files.Open(path, full =>
        {
            if (OpenRules.Refusal(full, Directory.Exists(full)) is { } kind)
            {
                refused = kind;
                return;
            }

            _openFile(full);
        }, filesOnly: true);
        if (refused is { } why)
        {
            return new VideoRouted(false, FileText.OpenRefused(result.Relative, why), note);
        }

        string opened = FileText.Opened(result, VideoText.InVideoApp);
        if (result.Outcome != FileOutcome.Ok)
        {
            return new VideoRouted(false, opened, note);   // its Error: leads the answer
        }

        string name = request.FileName ?? Path.GetFileName(path);
        if (_routedVideo is { } router)
        {
            router.Handoff(new VideoHandoff(VideoPage.FileSrc(path), YouTubeOpenLinksInKind.DefaultBrowser, null, null) { FileName = name });
        }
        else
        {
            _fileHandoffName = name;
        }

        return new VideoRouted(false, VideoText.HandedOff(opened), note);
    }

    /// <summary>The video file last sent to its default app, until the next play; null for none (or a YouTube hand-off).</summary>
    private string? FileHandoffName() =>
        _routedVideo is { } router ? router.LastHandoff is { IsFile: true } handoff ? handoff.FileName : null : _fileHandoffName;

    /// <summary>YouTube's hand-off for its own readers: a file's is never theirs.</summary>
    private VideoHandoff? YouTubeHandoff() => _routedVideo?.LastHandoff is { IsFile: false } handoff ? handoff : null;

    /// <summary>The player a file plays on: the router, or none (every play then goes to the default app).</summary>
    private IVideoPlayer? FilePlayer => _routedVideo;

    /// <summary>A play tool for <c>/view</c>'s and <c>/video</c>'s own use, with the routing the model's has.</summary>
    private PlayVideoTool PlayVideoToolFor() =>
        new(FilePlayer, _files, _time, RouteFile, () => FilePlayTarget().Kind);

    /// <summary>A control tool for <c>/video</c>'s own use, with the hand-off and the pointer the model's read.</summary>
    private VideoControlTool VideoControlToolFor() =>
        new(FilePlayer, _time, FileHandoffName, () => FilePlayTarget().Kind, () => "/youtube");

    /// <summary>
    /// The video file tools (2026-11-12): <c>play_video</c> and <c>video_control</c>, offered on the interactive screen while <i>Video
    /// tools</i> is on (a play always has a place: the window, the phone or the default app); headless builds none.
    /// <paramref name="youTubePointer"/> names <c>youtube_control</c> to a file tool when that one is offered this turn.
    /// </summary>
    public static IReadOnlyList<AIFunction> VideoTools(IVideoPlayer? player, WorkingDirectory files, TimeProvider? time, Func<VideoRequest, VideoRouted> route,
        Func<VideoFilesPlayInKind>? target, Func<string?>? handoffName, Func<string?>? youTubePointer) =>
        [new PlayVideoTool(player, files, time, route, target), new VideoControlTool(player, time, handoffName, target, youTubePointer)];

    /// <summary>Whether the video file group is offered: <i>Video tools</i> on and a tool in the list.</summary>
    public static bool VideoOffered(Settings.AppSettingsData effective, IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(tools);
        return effective.VideoTools && tools.Count > 0;
    }

    /// <summary>
    /// <c>/video</c> (and <c>/view &lt;video&gt;</c>): a file played, or the one playing driven, through the tools' own instances, so the
    /// checks, the wait and the words are the model's (<c>/youtube</c>'s way); the user's own hand, so neither <i>Video tools</i> nor plan
    /// mode judges it. A line starting <c>Error: </c> is an error, anything else a notice, through <paramref name="sink"/>.
    /// </summary>
    private async Task HandleVideoAsync(string args, INoticeSink sink, bool spinner, CancellationToken cancellationToken)
    {
        var line = VideoFileCommand.Parse(args);
        if (line.Error is { } error)
        {
            sink.Error(error);
            return;
        }

        if (line.Verb == VideoFileVerb.Play)
        {
            string path = line.Path!;
            ShowWatched(await WatchPlayerAsync(token => PlayVideoToolFor().PlayAsync(path, 0, autoplay: true, token), spinner, cancellationToken, play: true,
                label: VideoText.WaitingFor(FilePlayTarget().Kind)).ConfigureAwait(false), sink, cancellationToken, file: true);
            return;
        }

        string action = VideoFileCommand.ActionOf(line.Verb)!;
        string value = line.Number is { } number ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        var control = VideoControlToolFor();
        if (line.Verb is VideoFileVerb.Status or VideoFileVerb.Close)
        {
            Show(Command(await control.ControlAsync(action, value, cancellationToken).ConfigureAwait(false)), sink);
            return;
        }

        string? label = _videoPlayer?.Place == VideoPlace.WebTab ? VideoText.WaitingFor(VideoFilesPlayInKind.WebUiWindow) : null;
        ShowWatched(await WatchPlayerAsync(async token => Command(await control.ControlAsync(action, value, token).ConfigureAwait(false)), spinner, cancellationToken, label: label).ConfigureAwait(false),
            sink, cancellationToken, file: true);

        // The tool's "play_video plays one" is the model's way; the user's is /video <path>.
        static string Command(string answer) => ReferenceEquals(answer, VideoText.NoFile) ? VideoText.NoFileCommand : answer;
    }

    /// <summary>Whether <c>/view</c>'s argument names a video file (2026-11-12): <c>/video</c>'s play then takes it, never the picture viewer.</summary>
    internal static bool ViewsVideo(string args) => ParseViewArgs(args) is { Path.Length: > 0 } view && VideoFile.IsVideoPath(view.Path);

    /// <summary><c>/view &lt;video&gt;</c>: played as <c>/video &lt;path&gt;</c> is; <c>--chat</c> or <c>--thumbs</c> with one is the usage error.</summary>
    private Task HandleViewVideoAsync(string args, INoticeSink sink, bool spinner, CancellationToken cancellationToken)
    {
        var (path, chat, thumbs) = ParseViewArgs(args);
        if (chat || thumbs)
        {
            sink.Error(VideoText.ViewVideoFlagsError);
            return Task.CompletedTask;
        }

        return HandleVideoAsync(path, sink, spinner, cancellationToken);
    }

    /// <summary><c>video_control</c>'s name for a YouTube tool's file-open answer, when <i>Video tools</i> offers it and <c>/tools</c> hasn't switched it off.</summary>
    private string? VideoPointer()
    {
        var effective = _effective();
        return effective.VideoTools && !ToolsText.DisabledSet(effective.ToolsDisabled).Contains(VideoControlTool.ToolName) ? VideoControlTool.ToolName : null;
    }

    /// <summary><c>youtube_control</c>'s name for <c>video_control</c>'s YouTube-open answer, when the YouTube group offers it and <c>/tools</c> hasn't switched it off.</summary>
    private string? YouTubePointer()
    {
        var effective = _effective();
        return YouTubeOffered(effective, _youTubeTools) && _youTubeTools.Any(t => t is YouTubeControlTool)
            && !ToolsText.DisabledSet(effective.ToolsDisabled).Contains(YouTubeControlTool.ToolName) ? YouTubeControlTool.ToolName : null;
    }

    /// <summary>The video file tools' names, for plan mode's and <c>/tools</c>' checks.</summary>
    public static readonly IReadOnlySet<string> VideoToolNames = new HashSet<string>(StringComparer.Ordinal) { PlayVideoTool.ToolName, VideoControlTool.ToolName };
}
