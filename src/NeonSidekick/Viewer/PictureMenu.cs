using NeonSidekick.Diagnostics;
using NeonSidekick.Images;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture menu both picture windows open on a right-click (2026-10-04, the user's ask: the image tools on a thumbnail, or on
/// the viewer's picture, one picture at a time — the user's pick, no multi-select): its rows (<see cref="Build"/>), the edits run off
/// the window's thread through <see cref="PictureActions"/>, and the file's own actions — the path to the clipboard, Explorer
/// opened on the picture (shell32's <c>SHOpenFolderAndSelectItems</c>: the shell's own window, no process of ours started), the
/// picture attached to the chat or printed (both handed to the chat, <see cref="Attach"/> and <see cref="Print"/>, which runs them
/// on its own thread), and Delete, which the window does itself (its list changes). What a row did is told to the chat
/// (<see cref="Reported"/>), the windows having no line of their own. The app supplies the hooks (<c>Program</c>); with none, the
/// row does nothing and says so in the log.
/// </summary>
public static class PictureMenu
{
    /// <summary>The settings an edit reads, asked on the window's thread at each edit (<c>Program</c>: the effective settings). The defaults until then.</summary>
    public static Func<PictureEditSettings> Settings { get; set; } = static () => PictureEditSettings.Default;

    /// <summary>The picture (a full path) attached to the chat's next message. Called on a window's thread; must not block.</summary>
    public static Action<string>? Attach { get; set; }

    /// <summary>The picture (a full path) printed on the chat's thread. Called on a window's thread; must not block.</summary>
    public static Action<string>? Print { get; set; }

    /// <summary>A line for the chat: what a row did (false) or why it failed (true). Any thread; must not block.</summary>
    public static Action<string, bool>? Reported { get; set; }

    /// <summary>
    /// A new file an edit wrote (2026-11-26, the review's #24): the source's full path and the new file's, never an overwrite. The chat lists it with
    /// the conversation's pictures when its source is one, so a private end deletes it with them. A pool thread; must not block.
    /// </summary>
    public static Action<string, string>? Created { get; set; }

    /// <summary>
    /// The loaded profile's splash folder, read at each press (2026-11-23, the user's ask: Add to the splash images), so a profile switch is
    /// followed; null greys the row.
    /// </summary>
    public static Func<string?>? SplashFolder { get; set; }

    /// <summary>Whether <c>Welcome splash</c> is <c>robot</c> now (2026-11-23): the added line says when the pictures show.</summary>
    public static Func<bool>? RobotSplash { get; set; }

    /// <summary>
    /// The rows for the picture at <paramref name="path"/>: Open in the viewer first in the thumbnail browser; Rotate and flip, Colour,
    /// Resize, Convert to (the picture's own format greyed) and Shrink the file as submenus; Strip metadata (lossless; greyed but for
    /// a JPEG, PNG, WebP or GIF, 2026-10-05); the file's actions; Delete apart; and the
    /// edit mode as a last row that cannot be chosen. Add to the splash images (2026-11-23) is greyed with no <paramref name="splashFolder"/>
    /// or for a picture already in it. Pure.
    /// </summary>
    public static IReadOnlyList<ContextMenuItem> Build(bool thumbs, string path, string mode, string? splashFolder = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(mode);
        var own = ImageFormats.ByExtension(path);
        var rows = new List<ContextMenuItem>();
        if (thumbs)
        {
            rows.Add(Row(PictureMenuText.OpenInViewer, PictureCommand.OpenInViewer));
            rows.Add(ContextMenuItem.Separator);
        }

        rows.Add(new ContextMenuItem(PictureMenuText.Rotate, Children:
        [
            Row(PictureMenuText.RotateRight, PictureCommand.RotateRight),
            Row(PictureMenuText.RotateLeft, PictureCommand.RotateLeft),
            Row(PictureMenuText.Rotate180, PictureCommand.Rotate180),
            ContextMenuItem.Separator,
            Row(PictureMenuText.FlipHorizontal, PictureCommand.FlipHorizontal),
            Row(PictureMenuText.FlipVertical, PictureCommand.FlipVertical),
        ]));
        rows.Add(new ContextMenuItem(PictureMenuText.Colour, Children:
        [
            Row(PictureMenuText.Grey, PictureCommand.Grey),
            Row(PictureMenuText.Sepia, PictureCommand.Sepia),
            Row(PictureMenuText.Negative, PictureCommand.Negative),
            Row(PictureMenuText.Polaroid, PictureCommand.Polaroid),
        ]));
        rows.Add(new ContextMenuItem(PictureMenuText.Resize, Children:
        [
            Row(PictureMenuText.Half, PictureCommand.Half),
            Row(PictureMenuText.Quarter, PictureCommand.Quarter),
            ContextMenuItem.Separator,
            .. new[] { PictureCommand.Fit3840, PictureCommand.Fit1920, PictureCommand.Fit1280, PictureCommand.Fit1024, PictureCommand.Fit512 }
                .Select(c => Row(PictureMenuText.Fit(PictureActions.FitSide(c)!.Value), c)),
        ]));
        rows.Add(new ContextMenuItem(PictureMenuText.Convert, Children:
        [
            .. new[] { PictureCommand.ToPng, PictureCommand.ToJpeg, PictureCommand.ToGif, PictureCommand.ToBmp }
                .Select(c => PictureActions.FormatOf(c)!)
                .Select((f, i) => new ContextMenuItem(f.Label, (int)(PictureCommand.ToPng + i), Enabled: f != own)),
        ]));
        rows.Add(new ContextMenuItem(PictureMenuText.Shrink, Children:
        [
            .. new[] { PictureCommand.Under2Mb, PictureCommand.Under1Mb, PictureCommand.Under500Kb, PictureCommand.Under200Kb }
                .Select(c => Row(PictureMenuText.Under(PictureActions.MaxKb(c)!.Value), c)),
        ]));
        rows.Add(new ContextMenuItem(PictureMenuText.StripMetadata, (int)PictureCommand.StripMetadata, Enabled: MetadataStripper.ContainerOfPath(path) is not null));
        rows.Add(ContextMenuItem.Separator);
        rows.Add(Row(PictureMenuText.CopyPath, PictureCommand.CopyPath));
        rows.Add(Row(OperatingSystem.IsMacOS() ? PictureMenuText.ShowInFinder : PictureMenuText.ShowInExplorer, PictureCommand.ShowInExplorer));
        rows.Add(Row(PictureMenuText.Attach, PictureCommand.Attach));
        rows.Add(Row(PictureMenuText.Print, PictureCommand.Print));   // a Mac's too since 2026-10-08 (CUPS; left out from 2026-10-07 until then)
        rows.Add(new ContextMenuItem(PictureMenuText.AddToSplash, (int)PictureCommand.AddToSplash, Enabled: splashFolder is not null && !InFolder(path, splashFolder)));

        rows.Add(ContextMenuItem.Separator);
        rows.Add(Row(PictureMenuText.Delete, PictureCommand.Delete));
        rows.Add(ContextMenuItem.Separator);
        rows.Add(new ContextMenuItem(PictureMenuText.ModeNote(mode), Enabled: false));
        return rows;
    }

    /// <summary>The rows for the picture at <paramref name="path"/> with the edit mode in force now.</summary>
    internal static IReadOnlyList<ContextMenuItem> BuildNow(bool thumbs, string path) =>
        Build(thumbs, path, ImageWords.EditModeNames[(int)SettingsNow().Mode], SplashFolderNow());

    /// <summary>The hook's splash folder, null when none is wired or it throws.</summary>
    private static string? SplashFolderNow()
    {
        try
        {
            return SplashFolder?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn("Viewer", $"The splash folder could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Whether <paramref name="path"/> sits directly in <paramref name="folder"/>.</summary>
    private static bool InFolder(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), Files.CaseRule.Comparison(folder));   // the folder's own case rule (review #25)

    /// <summary>
    /// An edit row run on the picture at <paramref name="path"/> on a pool thread; <paramref name="done"/> gets the outcome there (the
    /// window keeps it and posts itself a message). The outcome's line is told to the chat as it ends.
    /// </summary>
    internal static void Edit(string path, PictureCommand command, Action<PictureEditOutcome> done)
    {
        var settings = SettingsNow();
        _ = Task.Run(() =>
        {
            PictureEditOutcome outcome;
            try
            {
                outcome = PictureActions.Edit(path, command, settings);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                outcome = PictureEditOutcome.Failure(ImageText.Failed(ex.Message));
            }

            if (!outcome.Failed && !outcome.Replaced && outcome.Written is { Length: > 0 } written)
            {
                try
                {
                    Created?.Invoke(path, written);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DiagnosticLog.Warn("Viewer", Files.FileText.CreatedListenerFailedLogLine(ex.GetType().Name), ex);
                }
            }

            Report(outcome.Failed ? PictureMenuText.Failed(Path.GetFileName(path), outcome.Line) : PictureMenuText.Edited(outcome.Line), outcome.Failed);
            DiagnosticLog.Info("Viewer", $"Picture menu {command} on {path}: {outcome.Line}");
            done(outcome);
        });
    }

    /// <summary>
    /// A file action the window has nothing to change for — the path copied, Explorer, attach, print — run now, on the window's
    /// thread (an STA with OLE, which Explorer's call wants). False for any other command.
    /// </summary>
    internal static bool RunFileAction(string path, PictureCommand command)
    {
        switch (command)
        {
            case PictureCommand.CopyPath:
                if (OperatingSystem.IsMacOS() ? NeonSidekick.UI.MacClipboard.TrySetText(path) : NeonSidekick.UI.WindowsClipboard.TrySetText(path))
                {
                    Report(PictureMenuText.Copied(path), false);
                }
                else
                {
                    Report(PictureMenuText.CopyFailed, true);
                }

                return true;
            case PictureCommand.ShowInExplorer:
                if (OperatingSystem.IsMacOS())
                {
                    MacPictureMenu.ShowInFinder(path);   // Finder on the picture (2026-10-07), on the main thread the menu ran on
                }
                else
                {
                    ShowInExplorer(path);
                }

                return true;
            case PictureCommand.Attach:
                Hand(Attach, path, "attach");
                return true;
            case PictureCommand.Print:
                Hand(Print, path, "print");
                return true;
            case PictureCommand.AddToSplash:
                AddToSplash(path);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The picture deleted (the viewer's double-Del's way, no confirmation — the user's call): to the Recycle bin while <c>File deletes to
    /// Recycle bin</c> is on (later on 2026-10-15; for good until then), else for good. True when it went.
    /// </summary>
    internal static bool Delete(string path)
    {
        try
        {
            var way = PictureDeletes.Delete(path);
            DiagnosticLog.Info("Viewer", $"Deleted {path} from the picture menu{PictureDeletes.LogTail(way)}.");
            Report(PictureMenuText.Deleted(Path.GetFileName(path), way), false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(PictureMenuText.Failed(Path.GetFileName(path), ViewerText.DeleteFailed(Path.GetFileName(path), ex.Message)), true);
            return false;
        }
    }

    /// <summary>The style the menu wears now: the theme in force, or the black look with <c>Themed external windows</c> off.</summary>
    internal static MenuStyle StyleNow(WindowChrome chrome) => MenuStyle.For(NeonSidekick.UI.Theme.Current, chrome.Themed());

    private static ContextMenuItem Row(string label, PictureCommand command) => new(label, (int)command);

    private static PictureEditSettings SettingsNow()
    {
        try
        {
            return Settings();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not read the image edit settings: {ex.Message}");
            return PictureEditSettings.Default;
        }
    }

    private static void Report(string line, bool error)
    {
        try
        {
            Reported?.Invoke(line, error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not tell the chat: {ex.Message}");
        }
    }

    /// <summary>
    /// Add to the splash images (2026-11-23, the user's ask): the picture copied into the profile's splash folder by <see cref="NeonSidekick.UI.SplashImages.Add"/>,
    /// as <c>set_splash_image</c> copies one; told to the chat either way. What the window owns (the folder, whether the robot is the splash) is
    /// read first on its thread, then the copy runs on a pool thread as <see cref="Edit"/>'s work does (2026-11-24, the last-7 review's #39: a
    /// large file held the window), so two adds may overlap: <see cref="NeonSidekick.UI.SplashImages.Add"/> takes the next free name then.
    /// </summary>
    private static void AddToSplash(string path)
    {
        string name = Path.GetFileName(path);
        if (SplashFolderNow() is not { } folder)
        {
            DiagnosticLog.Warn("Viewer", $"Nothing to add {name} to the splash images with: no splash folder.");
            return;
        }

        bool robot;
        try
        {
            robot = RobotSplash?.Invoke() == true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            robot = false;
        }

        _ = Task.Run(() => AddToSplash(path, name, folder, robot));
    }

    private static void AddToSplash(string path, string name, string folder, bool robot)
    {
        NeonSidekick.UI.SplashAdd added;
        try
        {
            added = NeonSidekick.UI.SplashImages.Add(folder, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            added = new NeonSidekick.UI.SplashAdd(NeonSidekick.UI.SplashAddResult.Failed, "", false, ex.Message);
        }

        switch (added.Result)
        {
            case NeonSidekick.UI.SplashAddResult.Added:
                Report(PictureMenuText.AddedToSplash(Path.GetFileName(added.Target), added.Only, robot), false);
                DiagnosticLog.Info("Viewer", $"Picture menu AddToSplash: {path} to {added.Target}");
                break;
            case NeonSidekick.UI.SplashAddResult.Already:
                Report(PictureMenuText.SplashAlready(name), true);
                break;
            case NeonSidekick.UI.SplashAddResult.NotAnImage:
                Report(PictureMenuText.SplashNotAnImage(name), true);
                break;
            default:
                Report(PictureMenuText.SplashFailed(name, added.Detail), true);
                break;
        }
    }

    private static void Hand(Action<string>? hook, string path, string what)
    {
        if (hook is null)
        {
            DiagnosticLog.Warn("Viewer", $"Nothing to {what} {Path.GetFileName(path)} with: the chat is not listening.");
            return;
        }

        try
        {
            hook(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(PictureMenuText.Failed(Path.GetFileName(path), ex.Message), true);
        }
    }

    // Explorer on the picture's folder with it selected: the shell's own call (an open folder window is reused), no process of ours.
    private static unsafe void ShowInExplorer(string path)
    {
        IntPtr pidl = IntPtr.Zero;
        uint attributes = 0;
        int hr = SHParseDisplayName(path, IntPtr.Zero, &pidl, 0, &attributes);
        if (hr >= 0 && pidl != IntPtr.Zero)
        {
            try
            {
                hr = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
            }
            finally
            {
                ILFree(pidl);
            }
        }

        if (hr < 0)
        {
            Report(PictureMenuText.Failed(Path.GetFileName(path), $"Explorer could not show it (0x{hr:X8})"), true);
        }
    }
}
