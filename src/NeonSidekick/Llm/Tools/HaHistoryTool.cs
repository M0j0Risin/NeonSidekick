using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_history(entity, hours?)</c> (2026-09-28): one entity's states over the last hours, oldest first, in local time —
/// "when was there motion in the pantry?", "how warm was the bathroom overnight?", "when did the TV go on?".
/// </summary>
public sealed class HaHistoryTool : HaTool
{
    public const string ToolName = "ha_history";
    public const string EntityArgument = "entity";
    public const string HoursArgument = "hours";

    public const int DefaultHours = 24;
    public const int MaxHours = 24 * 14;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "entity": { "type": "string", "description": "The entity: its id or its name." },
            "hours": { "type": "integer", "description": "How many hours back, 1 to 336; 24 when left out." }
          },
          "required": ["entity"]
        }
        """);

    private readonly TimeZoneInfo _zone;

    /// <param name="zone">The zone the times are shown in; null = the machine's.</param>
    public HaHistoryTool(HaSession ha, TimeZoneInfo? zone = null) : base(ha, confirm: null)
    {
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public override string Name => ToolName;

    public override string Description =>
        "A Home Assistant entity's state changes over the last hours, oldest first, in local time: when motion was seen, how a temperature moved, when a light or the TV went on.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string target = ToolArguments.ReadString(arguments, EntityArgument).Trim();
        if (target.Length == 0)
        {
            return HaText.Missing(EntityArgument);
        }

        if (!ToolArguments.TryReadInt32(arguments, HoursArgument, out int? given, out string raw) || given is < 1 or > MaxHours)
        {
            return HaText.OutOfRange(HoursArgument, raw.Length > 0 ? raw : given?.ToString(CultureInfo.InvariantCulture) ?? "", 1, MaxHours);
        }

        var (_, match) = await ResolveAsync(target, [], cancellationToken).ConfigureAwait(false);
        if (match.Error is not null)
        {
            return match.Error;
        }

        if (match.Entities.Count != 1)
        {
            return HaText.Ambiguous(target, match.Entities.Take(HaSnapshot.MaxCandidates).ToList(), match.Entities.Count);
        }

        if (Ha.Client() is not { } client)
        {
            return HaText.NotConfigured;
        }

        var entity = match.Entities[0];
        int hours = given ?? DefaultHours;
        var now = Ha.Time.GetUtcNow();
        var reply = await client.HistoryAsync(entity.Id, now.AddHours(-hours), now, Ha.Timeout, cancellationToken).ConfigureAwait(false);
        return reply.Ok ? HaText.History(entity, reply.Body, hours, _zone) : reply.Error;
    }
}
