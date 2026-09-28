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

    protected AppSettingsData Effective => _ha.Effective;

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// Whether <paramref name="domain"/>.<paramref name="service"/> on <paramref name="targets"/> may run: null when it may,
    /// else the refusal (policy off, the user's no, nobody to ask).
    /// </summary>
    protected async Task<string?> GateAsync(string domain, string service, IReadOnlyList<HaEntity> targets, string? data, CancellationToken cancellationToken)
    {
        switch (HaPolicy.Judge(Effective, domain, service))
        {
            case HaVerdict.Run:
                return null;
            case HaVerdict.Refuse:
                return HaText.PolicyOff;
        }

        string question = HaText.ConfirmQuestion(domain, service, targets.Count == 0 ? "" : HaText.Names(targets), data);
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
