using System.Globalization;
using System.Text.RegularExpressions;
using NeonSidekick.Camera;
using NeonSidekick.Files;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// A camera's still kept (2026-10-27, <see cref="HaCamera.Save"/>): the attachment (its <see cref="ImageAttachment.Path"/> the saved
/// file's full path), the file as the images folder names it (in full: <see cref="WorkingDirectoryOptions.Images"/>), or, for a write
/// the folder refused, the attachment unchanged, no file and the sentence (<see cref="HaText.CameraNotSaved"/>).
/// </summary>
public sealed record HaCameraSave(ImageAttachment Image, string? Saved, string? Error);

/// <summary>
/// Home Assistant's camera stills made into pictures (2026-10-27, the user's ask: Ring cameras added to Home Assistant showed
/// nowhere in the app). <see cref="ToAttachment"/> puts the bytes <see cref="HaClient.CameraAsync"/> fetched through
/// <see cref="ImageFile.TryLoad(byte[], string, out ImageAttachment?, out ImageLoadFailure, bool)"/> as a photo — the metadata
/// strip and the fit every picture gets — and marks it <see cref="ImageAttachment.Camera"/>, so <c>Camera keep in sessions</c>
/// (off by default) decides whether a stored session keeps it. <see cref="Save"/> keeps every still as its own timestamped file in
/// <see cref="FolderName"/> under the profile's images folder (the user's call, as the webcam's photos are kept; nothing deletes
/// them), written as the webcam's are (<see cref="CameraCapture.SnapAsync"/>): the stripped bytes, so the file matches what was shown.
/// The images folder may write inside itself (<see cref="WriteGuard"/>'s own-root rule), so no <see cref="WriteGuard.AsApp"/>.
/// Static, no clock: the caller passes its local time.
/// </summary>
public static partial class HaCamera
{
    /// <summary>The folder under the profile's images folder the stills are kept in. Pinned.</summary>
    public const string FolderName = "home-cameras";

    /// <summary>
    /// The largest still read: <see cref="ImageFile.MaxFileBytes"/>, the most <see cref="ImageFile.TryLoad(byte[], string, out ImageAttachment?, out ImageLoadFailure, bool)"/>
    /// takes, so a picture the cap lets through is never refused for its size there. An <c>int</c>, as the capped read takes.
    /// </summary>
    public const int MaxBytes = (int)ImageFile.MaxFileBytes;

    /// <summary>
    /// The least a still is given: Home Assistant's own 10 s for the camera (<c>CAMERA_IMAGE_TIMEOUT</c>) and the trip. Step 0
    /// (2026-10-27) timed the user's Ring stills at 2–2.5 s; a camera past Home Assistant's 10 s fails inside it as a 500, so a
    /// longer wait here couldn't help one.
    /// </summary>
    public static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A still's ceiling: <c>Home Assistant timeout (s)</c>, but at least <see cref="MinTimeout"/>. Pure.</summary>
    public static TimeSpan TimeoutFor(TimeSpan setting) => setting > MinTimeout ? setting : MinTimeout;

    /// <summary>
    /// The part of <paramref name="entityId"/> a file name is made of: after <c>camera.</c>, kept to <c>[a-z0-9_]</c>, cut to 40,
    /// <c>camera</c> when nothing is left. The id comes from Home Assistant's own list, but a file name never trusts it. Pure.
    /// </summary>
    public static string ObjectId(string entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        int dot = entityId.IndexOf('.', StringComparison.Ordinal);
        string tail = (dot < 0 ? entityId : entityId[(dot + 1)..]).ToLowerInvariant();
        string kept = new([.. tail.Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')]);
        kept = kept.Length > 40 ? kept[..40] : kept;
        return kept.Length == 0 ? "camera" : kept;
    }

    /// <summary>The entity ids a template may carry: Home Assistant's own form, nothing that could close the quote.</summary>
    [GeneratedRegex(@"^camera\.[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateSafeId();

    /// <summary>
    /// The template that finds when <paramref name="entityId"/>'s latest recording was (2026-10-27, the user's call after step 0): the camera's
    /// device's <c>sensor.</c> entities with <c>device_class: timestamp</c> and a <c>recording_status</c> attribute, as <c>id=state;</c> pairs.
    /// <c>recording_status</c> is what marks Ring's <c>last_activity</c> sensor whatever the user renamed it to; a battery's or a boot's
    /// timestamp has none. Proven on the user's Home Assistant (each camera's sensor in under 200 ms; an id with no device gives nothing).
    /// Null for an id outside <c>camera.[a-z0-9_]+</c>: the id comes from Home Assistant's list, but a template never trusts it. Pure.
    /// </summary>
    public static string? TimeTemplate(string entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        if (!TemplateSafeId().IsMatch(entityId))
        {
            return null;
        }

        return "{% set d = device_id('" + entityId + "') %}{% if d %}{% for e in device_entities(d) | select('match', 'sensor[.]') | select('is_state_attr', 'device_class', 'timestamp') %}"
            + "{% if state_attr(e, 'recording_status') is not none %}{{ e }}={{ states(e) }};{% endif %}{% endfor %}{% endif %}";
    }

    /// <summary>
    /// The time <see cref="TimeTemplate"/>'s answer gives: the newest of its <c>id=time;</c> pairs (a device with several recording sensors),
    /// null for none or nothing readable. Pure.
    /// </summary>
    public static DateTimeOffset? RecordingTime(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        DateTimeOffset? newest = null;
        foreach (string pair in answer.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && DateTimeOffset.TryParse(pair[(equals + 1)..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                && (newest is null || when > newest))
            {
                newest = when;
            }
        }

        return newest;
    }

    /// <summary>
    /// The fetched still as a picture for the model, the window and the page: stripped, fitted (2048 px a side), a picture with no
    /// transparency as JPEG, marked <see cref="ImageAttachment.Camera"/>. A failed fetch gives its own sentence; a still that won't
    /// decode, <see cref="HaText.PictureUnreadable"/>. Pure but for the codecs.
    /// </summary>
    public static (ImageAttachment? Image, string? Error) ToAttachment(HaPictureReply reply, string entityId)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.Ok)
        {
            return (null, reply.Error ?? HaText.BadAnswer(HaClient.CameraPath(entityId)));
        }

        return ImageFile.TryLoad(reply.Bytes, ObjectId(entityId), out var image, out var failure, photo: true) && image is not null
            ? (image with { Camera = true }, null)
            : (null, HaText.PictureUnreadable(failure));
    }

    /// <summary>
    /// <paramref name="image"/> written into <see cref="FolderName"/> of <paramref name="images"/> as
    /// <c>&lt;object id&gt;-yyyyMMdd-HHmmss.jpg</c> (<c>.png</c> for one that stayed PNG) at <paramref name="local"/>, the caller's
    /// local time, with <c>-2</c>… on a clash (<see cref="Images.ImageOutput.WriteNumbered"/>). A refused write is a sentence, never a
    /// throw: the still is used anyway, with no path (2026-10-27, the review: its object id read as a file name, and a picture with no
    /// file but a name opened from the camera's watch folder).
    /// </summary>
    public static HaCameraSave Save(WorkingDirectory images, ImageAttachment image, string entityId, DateTimeOffset local)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(image);
        string stem = CameraCapture.Under(FolderName, ObjectId(entityId) + "-" + CameraCapture.Stem(local));
        var (written, error) = Images.ImageOutput.WriteNumbered(images, stem, ImageFile.ExtensionOf(image.Bytes), image.Bytes);
        if (error is not null)
        {
            return new HaCameraSave(image with { Path = "" }, null, HaText.CameraNotSaved(error));
        }

        _ = images.Resolve(written.Relative, forWrite: false, out string full);
        return new HaCameraSave(image with { Path = full }, written.Relative, null);
    }
}
