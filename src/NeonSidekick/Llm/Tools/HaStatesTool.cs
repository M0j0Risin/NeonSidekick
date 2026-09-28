using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_states(query?, domain?, area?)</c> (2026-09-28): Home Assistant's entities, a line each — id, name, state and what
/// matters for the domain — narrowed by words of the name, a domain and a room; an exact entity id gives every attribute
/// (a TV's source list, a light's effects).
/// </summary>
public sealed class HaStatesTool : HaTool
{
    public const string ToolName = "ha_states";
    public const string QueryArgument = "query";
    public const string DomainArgument = "domain";
    public const string AreaArgument = "area";

    /// <summary>The most lines one call lists.</summary>
    public const int MaxLines = 80;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Words of the name, or an exact entity id for all its attributes (e.g. media_player.bravia_xr_55a80l)." },
            "domain": { "type": "string", "description": "Only this domain: light, scene, media_player, sensor, binary_sensor, switch, todo, remote, button…" },
            "area": { "type": "string", "description": "Only this room (an area name, e.g. Den)." }
          }
        }
        """);

    public HaStatesTool(HaSession ha) : base(ha, confirm: null)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists Home Assistant entities with their ids and states, narrowed by words of the name, a domain and a room. " +
        "Pass an exact entity id as query to see all its attributes, such as a TV's sources. Use it to find the id or name before acting.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var (snapshot, error) = await Ha.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return error;
        }

        return List(snapshot, Optional(arguments, QueryArgument), Optional(arguments, DomainArgument), Optional(arguments, AreaArgument));
    }

    /// <summary>The listing for a snapshot and the three filters. Pure.</summary>
    public static string List(HaSnapshot snapshot, string? query, string? domain, string? area)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (query is not null && snapshot.Find(query) is { } exact)
        {
            return HaText.Full(exact);
        }

        IEnumerable<HaEntity> pool = snapshot.Entities;
        var filters = new List<string>(3);
        if (domain is not null)
        {
            string d = domain.Trim().TrimEnd('.').ToLowerInvariant();
            pool = pool.Where(e => string.Equals(e.Domain, d, StringComparison.Ordinal));
            filters.Add("domain " + d);
        }

        if (area is not null)
        {
            string wanted = HaSnapshot.Normalize(area);
            var room = snapshot.Areas.FirstOrDefault(a => HaSnapshot.Normalize(a.Name) == wanted || HaSnapshot.Normalize(a.Id.Replace('_', ' ')) == wanted);
            pool = room is null ? pool.Where(e => e.Area is { } name && HaSnapshot.Normalize(name).Contains(wanted, StringComparison.Ordinal))
                : pool.Where(e => room.EntityIds.Contains(e.Id, StringComparer.Ordinal));
            filters.Add("area " + (room?.Name ?? area.Trim()));
        }

        if (query is not null)
        {
            var words = HaSnapshot.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            pool = pool.Where(e =>
            {
                string hay = HaSnapshot.Normalize(e.Name + " " + e.Id.Replace('_', ' '));
                return words.All(w => hay.Contains(w, StringComparison.Ordinal));
            });
            filters.Add("'" + query.Trim() + "'");
        }

        var all = pool.ToList();
        return HaText.States(all.Take(MaxLines).ToList(), all.Count, string.Join(", ", filters));
    }
}
