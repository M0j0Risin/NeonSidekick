using System.Globalization;
using NeonSidekick.Files;

namespace NeonSidekick.Camera;

/// <summary>A photo taken: the attachment the model gets, where it was saved (relative to the working directory), the frame it came from, and the camera's name.</summary>
public sealed record CameraShot(ImageAttachment Image, string RelativePath, string FullPath, CameraFrame Frame, string Device);

/// <summary>
/// Taking a photo (2026-10-02): a settled frame through the lease, encoded as JPEG at the size the settings ask for (its
/// longer side), saved into the working directory's <c>Camera output folder</c> (<see cref="OutputFolder"/>; <c>camera_images</c> by
/// default) as <c>yyyyMMdd-HHmmss.jpg</c> (a <c>-2</c>, <c>-3</c>…
/// on a clash), the sandbox's own write (atomic, refused outside the root). A retaken or declined shot is deleted again
/// (<see cref="Discard"/>), so the folder keeps only what was sent or attached.
/// </summary>
public sealed class CameraCapture
{
    private readonly CameraSession _session;
    private readonly Func<WorkingDirectory> _files;
    private readonly Func<string?> _folder;
    private readonly Func<CameraOptions> _options;
    private readonly TimeProvider _time;

    /// <param name="folder">The <c>Camera output folder</c> setting, read at every shot.</param>
    public CameraCapture(CameraSession session, Func<WorkingDirectory> files, Func<string?> folder, Func<CameraOptions> options, TimeProvider time)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The setting's folder relative to the working directory: trimmed, <c>.</c> (the working directory) when empty. Pure, the ComfyUI shape.</summary>
    public static string OutputFolder(string? setting) => string.IsNullOrWhiteSpace(setting) ? "." : setting.Trim();

    /// <summary><paramref name="name"/> under <paramref name="folder"/> (an <see cref="OutputFolder"/>), relative to the working directory. Pure.</summary>
    public static string Under(string folder, string name) => folder == "." ? name : folder.TrimEnd('/', '\\') + "/" + name;

    public CameraSession Session => _session;

    /// <summary>A shot's file name for the local time it was taken: <c>20261002-140203</c>. Pinned.</summary>
    public static string Stem(DateTimeOffset local) => local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// A settled frame, saved. Throws <see cref="CameraException"/> for the camera, and for a save the sandbox refuses (the
    /// sentence names why).
    /// </summary>
    public async Task<CameraShot> SnapAsync(CameraLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var frame = await lease.NextFrameAsync(settled: true, cancellationToken).ConfigureAwait(false);
        var target = _options().Target;
        int maxSide = Math.Max(target.Width, target.Height);
        var image = await Task.Run(() => CameraJpeg.Attachment(frame, "camera.jpg", maxSide), cancellationToken).ConfigureAwait(false);
        var files = _files();
        string stem = Under(OutputFolder(_folder()), Stem(TimeZoneInfo.ConvertTime(frame.At, _time.LocalTimeZone)));
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
                throw new CameraException(CameraFailure.Failed, CameraText.NotSaved(FileText.Error(written.Outcome, relative, "write", written.Detail)));
            }

            _ = files.Resolve(written.Relative, forWrite: false, out string full);
            return new CameraShot(image with { Path = full }, written.Relative, full, frame, _session.Device?.Name ?? CameraText.UnnamedCamera);
        }
    }

    /// <summary>Deletes a shot that was not used; a failure is only logged (the file is the user's to remove then).</summary>
    public void Discard(CameraShot? shot)
    {
        if (shot is null)
        {
            return;
        }

        var deleted = _files().Delete(shot.RelativePath);
        if (deleted.Outcome != FileOutcome.Ok)
        {
            Diagnostics.DiagnosticLog.Warn("Camera", $"Could not delete the unused shot {shot.RelativePath}: {deleted.Outcome} {deleted.Detail}");
        }
    }
}
