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
        var (camera, refused) = await ResolveAsync(Ha, target, HaText.NotACamera, cancellationToken).ConfigureAwait(false);
        if (camera is null)
        {
            return refused;
        }

        // The recording's time is asked beside the still, not after it (2026-10-27, the review): a slow still no longer waits twice.
        // It never throws but the caller's cancel, so a still that fails leaves it to finish unread.
        var recording = Ha.CameraTimeAsync(camera.Id, cancellationToken);
        var (save, failed) = await HaCamera.FetchAndSaveAsync(Ha, _images, camera.Id, _zone, cancellationToken).ConfigureAwait(false);
        if (save is null)
        {
            return failed;
        }

        var recorded = await recording.ConfigureAwait(false);
        return new ToolImageResult(HaText.CameraTaken(camera, save, recorded is { } when ? HaText.CameraRecorded(when, _zone) : null), [save.Image]);
    }

    /// <summary>
    /// The camera a call or a command means, fit to fetch (2026-11-13, moved out of the tool for <c>/ha camera</c>, which the tool, the
    /// screen and headless now share): read the kept snapshot and pick (<see cref="Pick"/>); a camera reported unavailable gets one fresh read
    /// (the kept one may be up to 30 s old, and a camera that has just come back is fetched), then is refused (<see cref="HaText.CameraUnavailable"/>).
    /// <paramref name="aliasedId"/> (a chip's alias, already looked up) is the camera on each read that has it; one the kept read lacks gets
    /// the fresh read too (the review of 2026-11-13: a camera just added and put on the bar), and one gone from Home Assistant is ignored and
    /// <paramref name="target"/> picked, so an error names what was typed. <paramref name="otherDomain"/> words an entity id of another domain
    /// (the tool's sentence names <c>ha_camera</c>, the command's <c>/ha camera</c>). The failure as an <c>Error:</c> sentence.
    /// </summary>
    public static async Task<(HaEntity? Camera, string? Error)> ResolveAsync(HaSession ha, string? target, Func<HaEntity, string> otherDomain, CancellationToken cancellationToken, string? aliasedId = null)
    {
        ArgumentNullException.ThrowIfNull(ha);
        ArgumentNullException.ThrowIfNull(otherDomain);
        var (snapshot, error) = await ha.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return (null, error);
        }

        var (camera, refused) = Pick(snapshot, target, otherDomain, aliasedId);
        if (camera is { Unavailable: true } || (aliasedId is not null && snapshot.Find(aliasedId) is null))
        {
            (snapshot, error) = await ha.SnapshotAsync(cancellationToken, fresh: true).ConfigureAwait(false);
            if (snapshot is null)
            {
                return (null, error);
            }

            (camera, refused) = Pick(snapshot, target, otherDomain, aliasedId);
        }

        if (camera is null)
        {
            return (null, refused);
        }

        return camera.Unavailable ? (null, HaText.CameraUnavailable(camera.Name, camera.State)) : (camera, null);
    }

    /// <summary>
    /// The camera <paramref name="read"/> gives for a call or a command: <paramref name="aliasedId"/>'s where the read has it; else the one
    /// <paramref name="target"/> names (a name, a room, an id), or the only one when it is null (<see cref="HaTool.OneOf"/>). Where several
    /// match and exactly one of them is available, that one (2026-11-13, the review: a bare <c>/ha camera</c>, or a room, was ambiguous
    /// whenever an offline camera stood beside the one that could give a picture). The failure as an <c>Error:</c> sentence. Pure.
    /// </summary>
    public static (HaEntity? Camera, string? Error) Pick(HaSnapshot read, string? target, Func<HaEntity, string> otherDomain, string? aliasedId = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (aliasedId is not null && read.Find(aliasedId) is { Domain: Domain } aliased)
        {
            return (aliased, null);
        }

        var (camera, error) = OneOf(read, Domain, Domain, target, otherDomain);
        if (camera is not null)
        {
            return (camera, null);
        }

        IReadOnlyList<HaEntity> candidates = target is null ? read.Of(Domain) : read.Resolve(target, [Domain]).Entities;
        var available = candidates.Where(c => !c.Unavailable).Take(2).ToList();
        return candidates.Count > 1 && available.Count == 1 ? (available[0], null) : (null, error);
    }
}
