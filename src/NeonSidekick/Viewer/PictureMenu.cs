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
    /// The rows for the picture at <paramref name="path"/>: Open in the viewer first in the thumbnail browser; Rotate and flip, Colour,
    /// Resize, Convert to (the picture's own format greyed) and Shrink the file as submenus; Strip metadata (lossless; greyed but for
    /// a JPEG, PNG, WebP or GIF, 2026-10-05); the file's actions; Delete apart; and the
    /// edit mode as a last row that cannot be chosen. Pure.
    /// </summary>
    public static IReadOnlyList<ContextMenuItem> Build(bool thumbs, string path, string mode)
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

        rows.Add(ContextMenuItem.Separator);
        rows.Add(Row(PictureMenuText.Delete, PictureCommand.Delete));
        rows.Add(ContextMenuItem.Separator);
        rows.Add(new ContextMenuItem(PictureMenuText.ModeNote(mode), Enabled: false));
        return rows;
    }

    /// <summary>The rows for the picture at <paramref name="path"/> with the edit mode in force now.</summary>
    internal static IReadOnlyList<ContextMenuItem> BuildNow(bool thumbs, string path) =>
        Build(thumbs, path, ImageWords.EditModeNames[(int)SettingsNow().Mode]);

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
            default:
                return false;
        }
    }

    /// <summary>The picture deleted for good (the viewer's double-Del's way, no Recycle Bin, no confirmation — the user's call). True when it went.</summary>
    internal static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            DiagnosticLog.Info("Viewer", $"Deleted {path} from the picture menu.");
            Report(PictureMenuText.Deleted(Path.GetFileName(path)), false);
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
