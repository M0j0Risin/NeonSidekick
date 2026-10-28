using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_camera(target?)</c> (2026-10-27, the user's ask: Ring cameras added to Home Assistant, and the model could list them but see
/// nothing): a camera's still through <c>camera_proxy</c>, stripped and fitted (<see cref="HaCamera.ToAttachment"/>), kept in
/// <c>home-cameras</c> under the profile's images folder (<see cref="HaCamera.Save"/>), and sent the <c>camera_capture</c> way
/// (<see cref="ToolImageResult"/>), marked a camera photo so <c>Camera keep in sessions</c> governs it. A read like <c>ha_states</c>
/// (the user's call): no confirm, no <see cref="HaPolicy"/> gate, so it runs under every <c>Home Assistant action policy</c>, <c>off</c>
/// included. With one camera the target may be left out (<c>ha_media</c>'s rule). A camera Home Assistant reports unavailable is refused
/// before any fetch, after one fresh read, since the kept snapshot may be 30 s old. The text says when the recording behind the still
/// was, where Home Assistant knows (<see cref="HaSession.CameraTimeAsync"/>, the user's call after step 0: a Ring still is a frame of the
/// latest recording, never live).
/// </summary>
public sealed class HaCameraTool : HaTool
{
    public const string ToolName = "ha_camera";
    public const string TargetArgument = "target";

    /// <summary>The domain the tool reads.</summary>
    public const string Domain = "camera";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "target": { "type": "string", "description": "The camera's name or entity id; leave it out when there is only one." }
          }
        }
        """);

    private readonly WorkingDirectory _images;
    private readonly TimeZoneInfo _zone;

    /// <param name="images">The profile's images folder the stills are kept in.</param>
    /// <param name="zone">The zone the recording's time is shown in; null = the session's clock's (2026-10-27, the review: not the machine's, which a test's clock never set).</param>
    public HaCameraTool(HaSession ha, WorkingDirectory images, TimeZoneInfo? zone = null) : base(ha, confirm: null)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _zone = zone ?? Ha.Time.LocalTimeZone;
    }

    public override string Name => ToolName;

    public override string Description =>
        "Looks at a Home Assistant camera: fetches its still picture, which comes in the next message. " +
        "Some cameras, Ring's among them, send their latest recording's picture rather than a live frame.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string? target = Optional(arguments, TargetArgument);
        var (snapshot, error) = await Ha.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return error;
        }

        var (camera, refused) = Camera(snapshot, target);
        if (camera is { Unavailable: true })
        {
            // The kept snapshot may be up to 30 s old: a camera that has just come back is fetched.
            (snapshot, error) = await Ha.SnapshotAsync(cancellationToken, fresh: true).ConfigureAwait(false);
            if (snapshot is null)
            {
                return error;
            }

            (camera, refused) = Camera(snapshot, target);
        }

        if (camera is null)
        {
            return refused;
        }

        if (camera.Unavailable)
        {
            return HaText.CameraUnavailable(camera.Name, camera.State);
        }

        // The recording's time is asked beside the still, not after it (2026-10-27, the review): a slow still no longer waits twice.
        // It never throws but the caller's cancel, so a still that fails leaves it to finish unread.
        var recording = Ha.CameraTimeAsync(camera.Id, cancellationToken);
        var reply = await Ha.CameraAsync(camera.Id, cancellationToken).ConfigureAwait(false);
        var (image, failed) = HaCamera.ToAttachment(reply, camera.Id);
        if (image is null)
        {
            return failed;
        }

        var save = HaCamera.Save(_images, image, camera.Id, TimeZoneInfo.ConvertTime(Ha.Time.GetUtcNow(), _zone));
        var recorded = await recording.ConfigureAwait(false);
        return new ToolImageResult(HaText.CameraTaken(camera, save, recorded is { } when ? HaText.CameraRecorded(when, _zone) : null), [save.Image]);
    }

    /// <summary>
    /// The camera a call means (<see cref="HaTool.OneOf"/>): the one named, else the only one; an entity id of another domain is
    /// <see cref="HaText.NotACamera"/>. The failure as an <c>Error:</c> sentence. Pure.
    /// </summary>
    public static (HaEntity? Camera, string? Error) Camera(HaSnapshot snapshot, string? target) => OneOf(snapshot, Domain, Domain, target, HaText.NotACamera);
}
