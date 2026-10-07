using System.Globalization;
using NeonSidekick.Camera;
using NeonSidekick.Files;

namespace NeonSidekick.Screen;

/// <summary>A screenshot taken: the attachment the model gets, where it was saved (relative to the working directory), and what it shows.</summary>
public sealed record ScreenShot(ImageAttachment Image, string RelativePath, string FullPath, string Described);

/// <summary>
/// Taking a screenshot (2026-10-04, <see cref="CameraCapture"/>'s twin): the target resolved against the screen as it is now, the
/// pixels captured off the calling thread (a window that stopped answering <c>PrintWindow</c> cannot hang the turn: the wait
/// gives up after <see cref="Timeout"/>), encoded as JPEG at <see cref="ImageFile.MaxSide"/> at most, saved into the working
/// directory's <c>Screen capture output folder</c> (<c>screen_images</c> by default) as <c>yyyyMMdd-HHmmss.jpg</c> (a <c>-2</c>,
/// <c>-3</c>… on a clash), the sandbox's own write. The attachment is marked <see cref="ImageAttachment.Screen"/>: a stored session
/// keeps a line naming it unless <c>Screen capture keep in sessions</c> is on, since a screenshot can hold anything that was
/// on the screen.
/// </summary>
public sealed class ScreenCapture
{
    /// <summary>How long a capture may take before it is given up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IScreenSystem _screen;
    private readonly Func<WorkingDirectory> _files;
    private readonly Func<string?> _folder;
    private readonly TimeProvider _time;

    /// <param name="folder">The <c>Screen capture output folder</c> setting, read at every shot.</param>
    public ScreenCapture(IScreenSystem screen, Func<WorkingDirectory> files, Func<string?> folder, TimeProvider time)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public IScreenSystem Screen => _screen;

    /// <summary>
    /// The target resolved now (<see cref="ScreenAiming.Resolve"/>), off the calling thread. Throws <see cref="ScreenException"/>,
    /// with the system's refusal first (2026-10-07, the Mac's permission): before the allow pane, so the user is never asked for a
    /// picture that would come back as the wallpaper.
    /// </summary>
    public Task<ScreenAim> AimAsync(ScreenTarget target, CancellationToken cancellationToken) =>
        Bounded(() => _screen.Refusal(ask: true) is { } refusal ? throw new ScreenException(refusal) : ScreenAiming.Resolve(target, _screen), cancellationToken);

    /// <summary>
    /// <c>screen_list</c>'s and <c>/screen list</c>'s lines (2026-10-07): <see cref="ScreenText.List(IReadOnlyList{ScreenMonitor}, int?, IReadOnlyList{ScreenWindow}, long?)"/>,
    /// or, while the system refuses captures, the monitors and the refusal where the windows would be (their titles are hidden
    /// then). Never asks the system. Blocking; throws <see cref="ScreenException"/>.
    /// </summary>
    public static IReadOnlyList<string> Listing(IScreenSystem screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return screen.Refusal(ask: false) is { } refusal
            ? ScreenText.List(screen.Monitors(), screen.OwnMonitor(), refusal)
            : ScreenText.List(screen.Monitors(), screen.OwnMonitor(), screen.Windows(), screen.OwnWindow());
    }

    /// <summary>The aim captured, encoded and saved. Throws <see cref="ScreenException"/> for the capture and for a save the sandbox refuses.</summary>
    public async Task<ScreenShot> TakeAsync(ScreenAim aim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aim);
        var image = await Bounded(() =>
        {
            var frame = aim.Window is { } id ? _screen.CaptureWindow(id) : _screen.CaptureArea(aim.Area ?? default);
            return Attachment(frame);
        }, cancellationToken).ConfigureAwait(false);
        var files = _files();
        string stem = CameraCapture.Under(CameraCapture.OutputFolder(_folder()), CameraCapture.Stem(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone)));
        for (int n = 1; ; n++)
        {
            string relative = n == 1 ? stem + ".jpg" : stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".jpg";
            var written = files.WriteBytes(relative, image.Bytes, overwrite: false);
            if (written.Outcome == FileOutcome.Exists && n < 100)
            {
                continue;
            }

            if (written.Outcome != FileOutcome.Ok)
            {
                throw new ScreenException(ScreenText.NotSaved(FileText.Error(written.Outcome, relative, "write", written.Detail)));
            }

            _ = files.Resolve(written.Relative, forWrite: false, out string full);
            return new ScreenShot(image with { Path = full }, written.Relative, full, aim.Described);
        }
    }

    /// <summary>A frame as the attachment the model gets: JPEG, its longer side at most <see cref="ImageFile.MaxSide"/>, marked as the screen's. Pure.</summary>
    public static ImageAttachment Attachment(ScreenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            throw new ScreenException(ScreenText.Failed("an empty picture"));
        }

        byte[] bytes = CameraJpeg.Encode(frame.Width, frame.Height, frame.Bgrx);
        var (width, height) = CameraPixels.Fit(frame.Width, frame.Height, ImageFile.MaxSide);
        return new ImageAttachment("screen.jpg", bytes, ImageFile.Jpeg, width, height) { Screen = true };
    }

    private static async Task<T> Bounded<T>(Func<T> body, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(body, cancellationToken).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ScreenException(ScreenText.TimedOut);
        }
    }
}
