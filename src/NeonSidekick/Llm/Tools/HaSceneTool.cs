using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_scene(scene, transition?)</c> (2026-09-28): activates a Home Assistant scene by name — "Den Relax", "den relax",
/// "indoor night" — the Hue scenes among them.
/// </summary>
public sealed class HaSceneTool : HaTool
{
    public const string ToolName = "ha_scene";
    public const string SceneArgument = "scene";
    public const string TransitionArgument = "transition";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "scene": { "type": "string", "description": "The scene's name (Den Relax) or entity id (scene.den_den_relax)." },
            "transition": { "type": "number", "description": "Seconds to fade over, 0 to 300." }
          },
          "required": ["scene"]
        }
        """);

    public HaSceneTool(HaSession ha, Func<string, CancellationToken, Task<bool?>>? confirm) : base(ha, confirm)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Activates a Home Assistant scene by name, such as Den Relax or Indoor Night. ha_states with domain scene lists them.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string scene = ToolArguments.ReadString(arguments, SceneArgument).Trim();
        if (scene.Length == 0)
        {
            return HaText.Missing(SceneArgument);
        }

        if (!ToolArguments.TryReadDouble(arguments, TransitionArgument, out double? transition, out string raw) || transition is < 0 or > HaLightsTool.MaxTransitionSeconds)
        {
            return HaText.OutOfRange(TransitionArgument, raw, 0, HaLightsTool.MaxTransitionSeconds);
        }

        var (_, match) = await ResolveAsync(scene, ["scene"], cancellationToken).ConfigureAwait(false);
        if (match.Error is not null)
        {
            return match.Error;
        }

        if (match.Entities.Count != 1)
        {
            return HaText.Ambiguous(scene, match.Entities.Take(HaSnapshot.MaxCandidates).ToList(), match.Entities.Count);
        }

        string? data = transition is { } t ? HaJson.Object(w => w.WriteNumber("transition", t)) : null;
        return await RunAsync("scene", "turn_on", match.Entities, data, null, cancellationToken).ConfigureAwait(false);
    }
}
