using NeonSidekick.Images;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// The picture folders' three words (2026-10-18, the user's ask: every folder under the profile's <c>images</c> folder gets <c>/comfy</c>'s
/// <c>view</c>, <c>thumbs</c> and <c>purge</c>): <c>/splash view|thumbs|purge</c> on the profile's <c>splash</c> and <c>/pasted
/// view|thumbs|purge</c> on the <c>Pasted images folder</c>. <c>view</c> and <c>thumbs</c> are windows of their own (quick under a reply);
/// <c>purge</c> waits for the idle line, as <c>/comfy purge</c> does (<see cref="PurgePictureFolderAsync"/>).
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>
    /// <c>/splash</c> with words: the folder's window or its purge; a word it does not know is <see cref="SplashCommand.Usage"/>, nothing
    /// wiped. After a view or thumbs on a folder with no pictures, <see cref="SplashCommand.BuiltInNotice"/>: the built-in ones live in the app.
    /// </summary>
    private async Task HandleSplashWordsAsync(string args, CancellationToken cancellationToken)
    {
        string folder = UI.SplashImages.ProfileFolderName;
        switch (SplashCommand.Parse(args))
        {
            // The note only after a window opened (2026-10-20, the code review: it followed a refusal too).
            case PictureFolderVerb.View:
                if (OpenViewerOn(folder, notice: true))
                {
                    SayIfNoSplashPictures();
                }

                return;
            case PictureFolderVerb.Thumbs:
                if (OpenThumbsOn(folder))
                {
                    SayIfNoSplashPictures();
                }

                return;
            case PictureFolderVerb.Purge:
                await PurgePictureFolderAsync(folder, SplashFolderLabel, cancellationToken).ConfigureAwait(false);
                return;
            default:
                _flow.Error(SplashCommand.Usage);
                return;
        }
    }

    private void SayIfNoSplashPictures()
    {
        if (Directory.Exists(_settings.ProfileSplashDirectory) && UI.SplashImages.FromDirectory(_settings.ProfileSplashDirectory) is null)
        {
            _flow.Notice(SplashCommand.BuiltInNotice);
        }
    }

    /// <summary><c>/pasted view|thumbs|purge</c>; bare or another word is <see cref="PastedCommand.Usage"/>.</summary>
    private async Task HandlePastedAsync(string args, CancellationToken cancellationToken)
    {
        string? folder = _effective().PastedImagesFolder;
        switch (PastedCommand.Parse(args))
        {
            case PictureFolderVerb.View:
                OpenViewerOn(folder, notice: true);
                return;
            case PictureFolderVerb.Thumbs:
                OpenThumbsOn(folder);
                return;
            case PictureFolderVerb.Purge:
                await PurgePictureFolderAsync(folder, PastedFolderLabel, cancellationToken).ConfigureAwait(false);
                return;
            default:
                _flow.Error(PastedCommand.Usage);
                return;
        }
    }

    /// <summary>
    /// <c>/ha stills view|thumbs|purge</c> (2026-10-27, the user's ask) on <see cref="HomeAssistant.HaCamera.FolderName"/>; bare or another word
    /// is <see cref="HomeAssistant.HaStillsCommand.Usage"/>. Never asks Home Assistant, so it works with no URL or key. A still deleted here
    /// is never reopened by the chip's 2 s reuse: <see cref="HomeCameraFetches"/> forgets a saved path whose file is gone.
    /// </summary>
    private async Task HandleHomeStillsAsync(PictureFolderVerb verb, CancellationToken cancellationToken)
    {
        string folder = HomeAssistant.HaCamera.FolderName;
        switch (verb)
        {
            case PictureFolderVerb.View:
                OpenViewerOn(folder, notice: true);
                return;
            case PictureFolderVerb.Thumbs:
                OpenThumbsOn(folder);
                return;
            case PictureFolderVerb.Purge:
                await PurgePictureFolderAsync(folder, HomeStillsFolderLabel, cancellationToken).ConfigureAwait(false);
                return;
            default:
                _flow.Error(HomeAssistant.HaStillsCommand.Usage);
                return;
        }
    }
}
