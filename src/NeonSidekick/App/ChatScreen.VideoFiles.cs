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

    /// <summary>Whether the Web UI's Video tab can take a play now (<see cref="WebSinkReady"/>), and if not, which reason holds.</summary>
    private enum WebSinkState
    {
        Ready,

        /// <summary>No web player, <i>Web UI</i> off, or the Web UI not serving.</summary>
        WebUiOff,

        /// <summary><i>Web UI video controls</i> off: there is no Video tab.</summary>
        ControlsOff,
    }

    /// <summary>
    /// The one test both families' <c>webui-window</c> make (2026-11-13, the review's fix: <see cref="PlayTarget"/> and
    /// <see cref="FilePlayTarget"/> had a copy each): a router with a web sink, <i>Web UI</i> on and serving, <i>Web UI video controls</i>
    /// on. A reason, not a sentence: each caller words it with its own family's fallback.
    /// </summary>
    private WebSinkState WebSinkReady(Settings.AppSettingsData effective) =>
        _routedVideo is not { HasWeb: true } || !effective.WebUi || !_webUiController.Serving ? WebSinkState.WebUiOff
        : effective.WebUiYouTubeControls ? WebSinkState.Ready
        : WebSinkState.ControlsOff;

    /// <summary>
    /// Where a video file plays now (<see cref="FilePlayTarget"/>): the place, the note leading the answer when a fallback was taken, the
    /// raw setting (for the log line, from the same read) and the Web UI's bare reason when <c>webui-window</c> fell back (for a chain).
    /// </summary>
    private readonly record struct FileTarget(VideoFilesPlayInKind Kind, string? Note, VideoFilesPlayInKind Setting, string? WebReason);

    /// <summary>
    /// Where a video file plays now (<c>Video files play in</c>), worked out once for the play, the spinner's words and the tools'
    /// descriptions, so they never disagree. <c>webui-window</c> falls back to the video window when <see cref="WebSinkReady"/> says no;
    /// the video window falls back to the default app where there is none or it plays no files (a Mac's). So every fallback ends at the
    /// default app, and the note leading the answer names where the play went: a <c>webui-window</c> → window → default app chain keeps
    /// both reasons in one sentence (2026-11-13), never the window note, whose "plays in the video window instead" is false there.
    /// </summary>
    private FileTarget FilePlayTarget()
    {
        var effective = _effective();
        var setting = VideoFilesPlayIn.Resolve(effective);
        var kind = setting;
        string? note = null, webReason = null;
        if (setting == VideoFilesPlayInKind.WebUiWindow)
        {
            switch (WebSinkReady(effective))
            {
                case WebSinkState.Ready:
                    return new FileTarget(setting, null, setting, null);
                case WebSinkState.WebUiOff:
                    (kind, note, webReason) = (VideoFilesPlayInKind.VideoWindow, VideoText.WebUiOffFellBack, YouTubeText.WebUiNotServing);
                    break;
                default:
                    (kind, note, webReason) = (VideoFilesPlayInKind.VideoWindow, VideoText.WebUiControlsOffFellBack, YouTubeText.WebUiControlsOff);
                    break;
            }
        }

        if (kind == VideoFilesPlayInKind.VideoWindow)
        {
            string? windowReason = _routedVideo is null ? VideoText.NoWindowHere
                : !_routedVideo.PlaysFilesOn(VideoSink.Window) ? VideoText.WindowPlaysNoFiles
                : null;
            if (windowReason is not null)
            {
                return new FileTarget(VideoFilesPlayInKind.DefaultApp, DefaultAppNote(webReason, windowReason), setting, webReason);
            }
        }

        return new FileTarget(kind, note, setting, webReason);
    }

    // The default app's note: the Web UI's reason first when webui-window fell back before the window's own.
    private static string DefaultAppNote(string? webReason, string reason) =>
        webReason is null ? VideoText.DefaultAppFellBack(reason) : VideoText.DefaultAppFellBack(webReason, reason);

    /// <summary>
    /// A video file played where <see cref="FilePlayTarget"/> says: on the router's window or web sink (waited for by the tool), or in its
    /// default app (answered at once). Unlike <see cref="RouteVideo"/>, nothing throws: a sink that refuses the file or a window that can't
    /// open sends it to the default app, a reason naming the place leading the answer (the exception's text only in the log, 2026-11-13).
    /// <paramref name="forUser"/> words the hand-off for <c>/video</c> and <c>/view</c>.
    /// </summary>
    private VideoRouted RouteFile(VideoRequest request, bool forUser)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = FilePlayTarget();
        string? note = target.Note;
        DiagnosticLog.Info(VideoCategory, VideoText.FileRouteLogLine(VideoFilesPlayIn.Name(target.Setting), request.FileName, VideoFilesPlayIn.Name(target.Kind), note));
        if (target.Kind != VideoFilesPlayInKind.DefaultApp && _routedVideo is { } router)
        {
            try
            {
                router.Play(request, target.Kind == VideoFilesPlayInKind.WebUiWindow ? VideoSink.Web : VideoSink.Window);
                _fileHandoffName = null;
                return new VideoRouted(true, null, note);
            }
            catch (InvalidOperationException e)
            {
                DiagnosticLog.Info(VideoCategory, VideoText.FileRouteFailedLogLine(request.FileName, VideoFilesPlayIn.Name(target.Kind), e.Message));
                note = DefaultAppNote(target.Kind == VideoFilesPlayInKind.VideoWindow ? target.WebReason : null, VideoText.CouldNotPlayIn(target.Kind));
            }
        }

        return OpenFileInDefaultApp(request, note, forUser);
    }

    /// <summary>
    /// A video file sent to its default app through the <c>open</c> tool's path (<see cref="WorkingDirectory.Open"/> with
    /// <see cref="OpenRules.Refusal"/>, the counted shell-open site behind <c>_openFile</c>), recorded as a hand-off only once it opened, as
    /// <see cref="HandOff"/> does: a failed open leaves the playing video alone and answers with the open's error. The hand-off's
    /// <c>Url</c> is the plain path (2026-11-13), read only for its name, so nothing can throw once the app has opened.
    /// </summary>
    private VideoRouted OpenFileInDefaultApp(VideoRequest request, string? note, bool forUser)
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
            router.Handoff(new VideoHandoff(path, YouTubeOpenLinksInKind.DefaultBrowser, null, null) { FileName = name });
        }
        else
        {
            _fileHandoffName = name;
        }

        return new VideoRouted(false, VideoText.HandedOff(opened, forUser), note);
    }

    /// <summary>The video file last sent to its default app, until the next play; null for none (or a YouTube hand-off).</summary>
    private string? FileHandoffName() =>
        _routedVideo is { } router ? router.LastHandoff is { IsFile: true } handoff ? handoff.FileName : null : _fileHandoffName;

    /// <summary>YouTube's hand-off for its own readers: a file's is never theirs.</summary>
    private VideoHandoff? YouTubeHandoff() => _routedVideo?.LastHandoff is { IsFile: false } handoff ? handoff : null;

    /// <summary>
    /// A play tool for <c>/view</c>'s and <c>/video</c>'s own use, with the routing and the late-failure fallback the model's has, its
    /// answers worded for the user (2026-11-13).
    /// </summary>
    private PlayVideoTool PlayVideoToolFor() =>
        new(_routedVideo, _files, _time, r => RouteFile(r, forUser: true), () => FilePlayTarget().Kind, (r, n) => OpenFileInDefaultApp(r, n, forUser: true), forUser: true);

    /// <summary>A control tool for <c>/video</c>'s own use, with the hand-off and the pointer the model's read, its answers worded for the user.</summary>
    private VideoControlTool VideoControlToolFor() =>
        new(_routedVideo, _time, FileHandoffName, () => FilePlayTarget().Kind, () => YouTubeCommand.Word, forUser: true);

    /// <summary>
    /// The video file tools (2026-11-12): <c>play_video</c> and <c>video_control</c>, offered on the interactive screen while <i>Video
    /// tools</i> is on (a play always has a place: the window, the phone or the default app); headless builds none.
    /// <paramref name="youTubePointer"/> names <c>youtube_control</c> to a file tool when that one is offered this turn; <paramref name="fallback"/>
    /// takes a file whose video window failed after it opened to its default app.
    /// </summary>
    public static IReadOnlyList<AIFunction> VideoTools(IVideoPlayer? player, WorkingDirectory files, TimeProvider? time, Func<VideoRequest, VideoRouted> route,
        Func<VideoFilesPlayInKind>? target, Func<string?>? handoffName, Func<string?>? youTubePointer, Func<VideoRequest, string, VideoRouted>? fallback = null) =>
        [new PlayVideoTool(player, files, time, route, target, fallback), new VideoControlTool(player, time, handoffName, target, youTubePointer)];

    /// <summary>Whether the video file group is offered: <i>Video tools</i> on (the list is always its two tools).</summary>
    public static bool VideoOffered(Settings.AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.VideoTools;
    }

    /// <summary>
    /// <c>/video</c>: a file played, or the one playing driven, through the tools' own instances (worded for the user), so the checks, the
    /// wait and the refusals are the model's (<c>/youtube</c>'s way; a seek's or a volume's text goes to the tool as typed, 2026-11-13); the
    /// user's own hand, so neither <i>Video tools</i> nor plan mode judges it. A line starting <c>Error: </c> is an error, anything else a
    /// notice, through <paramref name="sink"/>.
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
            await PlayFileAsync(line.Path!, sink, spinner, cancellationToken).ConfigureAwait(false);
            return;
        }

        string action = VideoFileCommand.ActionOf(line.Verb)!;
        string value = line.Value ?? "";
        var control = VideoControlToolFor();
        if (line.Verb is VideoFileVerb.Status or VideoFileVerb.Close)
        {
            Show(await control.ControlAsync(action, value, cancellationToken).ConfigureAwait(false), sink);
            return;
        }

        // The spinner's words are WatchPlayerAsync's own for a command: the player in use.
        ShowWatched(await WatchPlayerAsync(token => control.ControlAsync(action, value, token), spinner, cancellationToken).ConfigureAwait(false),
            sink, cancellationToken, file: true);
    }

    /// <summary>
    /// A file played for <c>/video</c> or <c>/view</c>. The play's synchronous part (the sandbox's checks, which can block on a share that
    /// doesn't answer) runs off the screen's thread (2026-11-13), so Esc ends the wait even then; the tool never starts a play given up.
    /// </summary>
    private async Task PlayFileAsync(string path, INoticeSink sink, bool spinner, CancellationToken cancellationToken)
    {
        var tool = PlayVideoToolFor();
        ShowWatched(await WatchPlayerAsync(token => Task.Run(() => tool.PlayAsync(path, 0, autoplay: true, token), token).WaitAsync(token), spinner, cancellationToken, play: true,
            label: VideoText.WaitingFor(FilePlayTarget().Kind)).ConfigureAwait(false), sink, cancellationToken, file: true);
    }

    /// <summary>Whether <c>/view</c>'s argument names a video file (2026-11-12): <c>/video</c>'s play then takes it, never the picture viewer. By the name alone.</summary>
    internal static bool ViewsVideo(string args) => ParseViewArgs(args) is { Path.Length: > 0 } view && VideoFile.IsVideoPath(view.Path);

    /// <summary>
    /// <c>/view &lt;video&gt;</c>: played as <c>/video &lt;path&gt;</c> is; <c>--chat</c> or <c>--thumbs</c> with one is the usage error. A
    /// folder named like a video (2026-11-13) is the picture viewer's (<paramref name="viewFolder"/>, flags and all), checked off the screen's
    /// thread, where a share that doesn't answer would stall it, and before the flags error, which is for a file.
    /// </summary>
    private async Task HandleViewVideoAsync(string args, INoticeSink sink, bool spinner, Action<string> viewFolder, CancellationToken cancellationToken)
    {
        var (path, chat, thumbs) = ParseViewArgs(args);
        if (await Task.Run(() => _files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && Directory.Exists(full), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            viewFolder(args);
            return;
        }

        if (chat || thumbs)
        {
            sink.Error(VideoText.ViewVideoFlagsError(FilePlayTarget().Kind));
            return;
        }

        await PlayFileAsync(path, sink, spinner, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>video_control</c>'s name for a YouTube tool's file-open answer, when <i>Video tools</i> offers it this turn: not switched off in
    /// <c>/tools</c>, nor by the turn's mode (plan, audit, a gap pass: <see cref="TurnDisabled"/>, 2026-11-13), and never in a bot's turn,
    /// which gets the YouTube tools but not this one.
    /// </summary>
    private string? VideoPointer()
    {
        if (Volatile.Read(ref _botTurn) is not null)
        {
            return null;
        }

        var effective = _effective();
        return effective.VideoTools && !TurnDisabled(effective).Contains(VideoControlTool.ToolName) ? VideoControlTool.ToolName : null;
    }

    /// <summary><c>youtube_control</c>'s name for <c>video_control</c>'s YouTube-open answer, when the YouTube group offers it this turn (<see cref="VideoPointer"/>'s rules).</summary>
    private string? YouTubePointer()
    {
        if (Volatile.Read(ref _botTurn) is not null)
        {
            return null;
        }

        var effective = _effective();
        return YouTubeOffered(effective, _youTubeTools) && _youTubeTools.Any(t => t is YouTubeControlTool)
            && !TurnDisabled(effective).Contains(YouTubeControlTool.ToolName) ? YouTubeControlTool.ToolName : null;
    }
}
