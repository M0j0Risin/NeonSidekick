using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_lights(target, action?, brightness_pct?, color_name?, color_temp_kelvin?, transition?)</c> (2026-09-28): turns lights
/// on, off or over, by name, room or id — "the den", "kitchen and hallway", "Den Corner Lamp", "all" — with a brightness, a
/// colour or a white temperature. A room goes to its group light (one Hue call for the room), else to every light in it.
/// </summary>
public sealed class HaLightsTool : HaTool
{
    public const string ToolName = "ha_lights";
    public const string TargetArgument = "target";
    public const string ActionArgument = "action";
    public const string BrightnessArgument = "brightness_pct";
    public const string ColorArgument = "color_name";
    public const string KelvinArgument = "color_temp_kelvin";
    public const string TransitionArgument = "transition";

    public const int MinKelvin = 1500;
    public const int MaxKelvin = 9000;
    public const int MaxTransitionSeconds = 300;

    /// <summary>The actions and the service each calls.</summary>
    public static readonly IReadOnlyDictionary<string, string> Actions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["on"] = "turn_on",
        ["off"] = "turn_off",
        ["toggle"] = "toggle",
    };

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "target": { "type": "string", "description": "Which lights: a room (Den), a light's name (Den Corner Lamp), an entity id, several joined with commas, or all." },
            "action": { "type": "string", "enum": ["on", "off", "toggle"], "description": "on (the default; also to change brightness or colour), off or toggle." },
            "brightness_pct": { "type": "integer", "description": "Brightness 0 to 100 (on only)." },
            "color_name": { "type": "string", "description": "A CSS colour name such as red, warmwhite, blue, orange (on only)." },
            "color_temp_kelvin": { "type": "integer", "description": "White temperature 1500 (warm) to 9000 (cool) (on only)." },
            "transition": { "type": "number", "description": "Seconds to fade over, 0 to 300." }
          },
          "required": ["target"]
        }
        """);

    public HaLightsTool(HaSession ha, Func<string, CancellationToken, Task<bool?>>? confirm) : base(ha, confirm)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Turns Home Assistant lights on, off or toggles them by room, name or id, optionally setting brightness, a colour or a white temperature. " +
        "A room name uses the room's group light.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string target = ToolArguments.ReadString(arguments, TargetArgument).Trim();
        if (target.Length == 0)
        {
            return HaText.Missing(TargetArgument);
        }

        string action = Optional(arguments, ActionArgument)?.ToLowerInvariant() ?? "on";
        if (!Actions.TryGetValue(action, out string? service))
        {
            return HaText.BadChoice(ActionArgument, action, Actions.Keys);
        }

        if (!ToolArguments.TryReadInt32(arguments, BrightnessArgument, out int? brightness, out string rawBrightness) || brightness is < 0 or > 100)
        {
            return HaText.OutOfRange(BrightnessArgument, Raw(rawBrightness, brightness), 0, 100);
        }

        if (!ToolArguments.TryReadInt32(arguments, KelvinArgument, out int? kelvin, out string rawKelvin) || kelvin is < MinKelvin or > MaxKelvin)
        {
            return HaText.OutOfRange(KelvinArgument, Raw(rawKelvin, kelvin), MinKelvin, MaxKelvin);
        }

        if (!ToolArguments.TryReadDouble(arguments, TransitionArgument, out double? transition, out string rawTransition) || transition is < 0 or > MaxTransitionSeconds)
        {
            return HaText.OutOfRange(TransitionArgument, rawTransition.Length > 0 ? rawTransition : transition?.ToString(CultureInfo.InvariantCulture) ?? "", 0, MaxTransitionSeconds);
        }

        string? color = Optional(arguments, ColorArgument);
        bool on = service == "turn_on";
        var (_, match) = await ResolveAsync(target, ["light"], cancellationToken).ConfigureAwait(false);
        if (match.Error is not null)
        {
            return match.Error;
        }

        string data = Data(on ? brightness : null, on ? color : null, on ? kelvin : null, transition);
        string detail = HaText.LightDetail(on ? brightness : null, on ? color : null, on ? kelvin : null, transition);
        return await RunAsync("light", service, match.Entities, data, detail, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The call's data: only what was given; a colour name and a temperature both given, the temperature wins (HA takes one). Pure.</summary>
    public static string Data(int? brightness, string? color, int? kelvin, double? transition) => HaJson.Object(w =>
    {
        if (brightness is { } b)
        {
            w.WriteNumber("brightness_pct", b);
        }

        if (kelvin is { } k)
        {
            w.WriteNumber("color_temp_kelvin", k);
        }
        else if (!string.IsNullOrWhiteSpace(color))
        {
            w.WriteString("color_name", color.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal));
        }

        if (transition is { } t)
        {
            w.WriteNumber("transition", t);
        }
    });

    private static string Raw(string raw, int? value) => raw.Length > 0 ? raw : value?.ToString(CultureInfo.InvariantCulture) ?? "";
}
