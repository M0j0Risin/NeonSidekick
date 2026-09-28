using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_overview()</c> (2026-09-28): the house at a glance — the lights on by room with each room's group light, the media
/// players, temperatures, motion, low batteries, to-do lists and how many scenes. The first look before acting on a name.
/// </summary>
public sealed class HaOverviewTool : HaTool
{
    public const string ToolName = "ha_overview";

    private static readonly JsonElement Schema = ToolSchema.Parse("""{ "type": "object", "properties": {} }""");

    public HaOverviewTool(HaSession ha) : base(ha, confirm: null)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Home Assistant at a glance: which lights are on in each room (with the room's group light), the TV and other media players, " +
        "temperatures, motion, low batteries, to-do lists and how many scenes there are. Call it first when the user asks about or wants to change the house.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var (snapshot, error) = await Ha.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot is null ? error : string.Join('\n', HaText.Overview(snapshot));
    }
}
