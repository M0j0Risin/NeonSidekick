using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the nine Home Assistant tools share (2026-09-28): the <see cref="HaSession"/> door, the settings in force at each
/// call, the snapshot lookup, and the gate every acting tool passes before a service call — <see cref="HaPolicy.Judge"/>,
/// then, for an asked call, the user's yes through <c>confirm</c> (the advisor's seam: true yes, false no, null nothing
/// could ask; the seam itself null where nothing ever can, headless). A refusal is an <c>Error:</c> sentence, never a throw.
/// </summary>
public abstract class HaTool : AIFunction
{
    private readonly HaSession _ha;
    private readonly Func<string, CancellationToken, Task<bool?>>? _confirm;

    protected HaTool(HaSession ha, Func<string, CancellationToken, Task<bool?>>? confirm)
    {
        _ha = ha ?? throw new ArgumentNullException(nameof(ha));
        _confirm = confirm;
    }

    protected HaSession Ha => _ha;

    /// <summary>
    /// The one <paramref name="domain"/> entity a call means (2026-10-27, the review: <c>ha_camera</c> and <c>ha_media</c> each had a copy):
    /// the one <paramref name="target"/> names, else the only one; with none or several, an <c>Error:</c> sentence naming at most
    /// <see cref="HaSnapshot.MaxCandidates"/> of them as <paramref name="noun"/>s. An entity id of another domain is
    /// <paramref name="otherDomain"/>'s sentence when given. Pure.
    /// </summary>
    public static (HaEntity? Entity, string? Error) OneOf(HaSnapshot snapshot, string domain, string noun, string? target, Func<HaEntity, string>? otherDomain = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (target is null)
        {
            var all = snapshot.Of(domain);
            return all.Count switch
            {
                0 => (null, HaText.NoneOf(noun)),
                1 => (all[0], null),
                _ => (null, HaText.Ambiguous(noun, all.Take(HaSnapshot.MaxCandidates).ToList(), all.Count)),
            };
        }

        if (otherDomain is not null && snapshot.Find(target) is { } exact && exact.Domain != domain)
        {
            return (null, otherDomain(exact));
        }

        var match = snapshot.Resolve(target, [domain]);
        if (match.Error is not null)
        {
            return (null, match.Error);
        }

        return match.Entities.Count == 1
            ? (match.Entities[0], null)
            : (null, HaText.Ambiguous(target, match.Entities.Take(HaSnapshot.MaxCandidates).ToList(), match.Entities.Count));
    }

    protected AppSettingsData Effective => _ha.Effective;

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// Whether <paramref name="domain"/>.<paramref name="service"/> on <paramref name="targets"/> may run: null when it may,
    /// else the refusal (policy off, the user's no, nobody to ask).
    /// </summary>
    /// <remarks>
    /// An asked call names every target and shows its data whole (2026-10-20, the code review: the data was cut at 120 characters and the
    /// names after six were a count): data the question can't show whole is refused before it is asked, so the yes is to exactly what runs.
    /// </remarks>
    protected Task<string?> GateAsync(string domain, string service, IReadOnlyList<HaEntity> targets, string? data, CancellationToken cancellationToken)
    {
        string? shown = string.IsNullOrWhiteSpace(data) ? null : HaJson.Shown(data);
        if (shown is not null && HaPolicy.Judge(Effective, domain, service) == HaVerdict.Ask && HaText.DataUnshowable(shown) is { } unshowable)
        {
            return Task.FromResult<string?>(unshowable);
        }

        return GateAsync(domain, service, HaText.ConfirmQuestion(domain, service, HaText.AllNames(targets), shown), cancellationToken);
    }

    /// <summary>
    /// The gate with the pane's question given: <see cref="HaPolicy.Judge"/> on <paramref name="domain"/>.<paramref name="service"/>,
    /// then, for an asked call, <paramref name="question"/> through <c>confirm</c>. Null when it may run, else the refusal.
    /// </summary>
    protected async Task<string?> GateAsync(string domain, string service, string question, CancellationToken cancellationToken)
    {
        switch (HaPolicy.Judge(Effective, domain, service))
        {
            case HaVerdict.Run:
                return null;
            case HaVerdict.Refuse:
                return HaText.PolicyOff;
        }

        bool? yes = _confirm is null ? null : await _confirm(question, cancellationToken).ConfigureAwait(false);
        return yes switch
        {
            true => null,
            false => HaText.Declined,
            null => HaText.NotAsked,
        };
    }

    /// <summary>The entities <paramref name="target"/> means among <paramref name="domains"/>, over a snapshot read if stale.</summary>
    protected async Task<(HaSnapshot? Snapshot, HaMatch Match)> ResolveAsync(string target, IReadOnlyList<string> domains, CancellationToken cancellationToken)
    {
        var (snapshot, error) = await _ha.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return (null, HaMatch.Refused(error ?? HaText.NotConfigured));
        }

        return (snapshot, snapshot.Resolve(target, domains));
    }

    /// <summary>Gates and runs one service call on <paramref name="targets"/>, answering <see cref="HaText.Done"/> or the failure.</summary>
    protected async Task<string> RunAsync(string domain, string service, IReadOnlyList<HaEntity> targets, string? data, string? detail, CancellationToken cancellationToken)
    {
        if (await GateAsync(domain, service, targets, data, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var reply = await _ha.CallAsync(domain, service, targets.Select(t => t.Id).ToList(), data, cancellationToken).ConfigureAwait(false);
        return reply.Ok ? HaText.Done(domain, service, targets, detail) : reply.Error!;
    }
}
