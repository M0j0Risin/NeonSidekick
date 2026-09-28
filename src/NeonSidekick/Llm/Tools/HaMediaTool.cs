using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_media(action, target?, volume_pct?, source?)</c> (2026-09-28): the TV and other media players — power, volume, mute,
/// the input source and playback. With one media player in the house the target may be left out.
/// </summary>
public sealed class HaMediaTool : HaTool
{
    public const string ToolName = "ha_media";
    public const string ActionArgument = "action";
    public const string TargetArgument = "target";
    public const string VolumeArgument = "volume_pct";
    public const string SourceArgument = "source";

    /// <summary>The actions and the <c>media_player</c> service each calls.</summary>
    public static readonly IReadOnlyDictionary<string, string> Actions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["on"] = "turn_on",
        ["off"] = "turn_off",
        ["volume"] = "volume_set",
        ["volume_up"] = "volume_up",
        ["volume_down"] = "volume_down",
        ["mute"] = "volume_mute",
        ["unmute"] = "volume_mute",
        ["source"] = "select_source",
        ["play"] = "media_play",
        ["pause"] = "media_pause",
        ["play_pause"] = "media_play_pause",
        ["stop"] = "media_stop",
        ["next"] = "media_next_track",
        ["previous"] = "media_previous_track",
    };

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["on", "off", "volume", "volume_up", "volume_down", "mute", "unmute", "source", "play", "pause", "play_pause", "stop", "next", "previous"], "description": "What to do." },
            "target": { "type": "string", "description": "The media player's name or id; leave it out when there is only one." },
            "volume_pct": { "type": "integer", "description": "The volume, 0 to 100, for action volume." },
            "source": { "type": "string", "description": "The input for action source, one of the player's sources (e.g. HDMI 2); ha_states with the player's id lists them." }
          },
          "required": ["action"]
        }
        """);

    public HaMediaTool(HaSession ha, Func<string, CancellationToken, Task<bool?>>? confirm) : base(ha, confirm)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Controls a Home Assistant media player such as the TV: power on or off, set the volume or step it, mute, pick the input source, play, pause, skip.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string action = Optional(arguments, ActionArgument)?.ToLowerInvariant() ?? "";
        if (!Actions.TryGetValue(action, out string? service))
        {
            return action.Length == 0 ? HaText.Missing(ActionArgument) : HaText.BadChoice(ActionArgument, action, Actions.Keys);
        }

        if (!ToolArguments.TryReadInt32(arguments, VolumeArgument, out int? volume, out string rawVolume) || volume is < 0 or > 100)
        {
            return HaText.OutOfRange(VolumeArgument, rawVolume.Length > 0 ? rawVolume : volume?.ToString(CultureInfo.InvariantCulture) ?? "", 0, 100);
        }

        var (snapshot, error) = await Ha.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return error;
        }

        var (player, refused) = Player(snapshot, Optional(arguments, TargetArgument));
        if (player is null)
        {
            return refused;
        }

        string? data;
        string? detail;
        switch (action)
        {
            case "volume":
                if (volume is not { } level)
                {
                    return HaText.Missing(VolumeArgument);
                }

                data = HaJson.Object(w => w.WriteNumber("volume_level", Math.Round(level / 100.0, 2)));
                detail = "volume " + level.ToString(CultureInfo.InvariantCulture) + "%";
                break;
            case "mute":
            case "unmute":
                bool mute = action == "mute";
                data = HaJson.Object(w => w.WriteBoolean("is_volume_muted", mute));
                detail = mute ? "muted" : "unmuted";
                break;
            case "source":
                string wanted = Optional(arguments, SourceArgument) ?? "";
                if (wanted.Length == 0)
                {
                    return HaText.Missing(SourceArgument);
                }

                var sources = HaJson.Strings(player.Attributes, "source_list");
                if (MatchSource(sources, wanted) is not { } source)
                {
                    return sources.Count == 0 ? HaText.BadChoice(SourceArgument, wanted, ["(the player lists none)"]) : HaText.BadChoice(SourceArgument, wanted, sources);
                }

                data = HaJson.Object(w => w.WriteString("source", source));
                detail = "source " + source;
                break;
            default:
                data = null;
                detail = null;
                break;
        }

        return await RunAsync("media_player", service, [player], data, detail, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The player a call means: the one named, else the only one; the failure as an <c>Error:</c> sentence. Pure.</summary>
    public static (HaEntity? Player, string? Error) Player(HaSnapshot snapshot, string? target)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (target is null)
        {
            var players = snapshot.Of("media_player");
            return players.Count switch
            {
                0 => (null, HaText.NoneOf("media player")),
                1 => (players[0], null),
                _ => (null, HaText.Ambiguous("media player", players, players.Count)),
            };
        }

        var match = snapshot.Resolve(target, ["media_player"]);
        if (match.Error is not null)
        {
            return (null, match.Error);
        }

        return match.Entities.Count == 1 ? (match.Entities[0], null) : (null, HaText.Ambiguous(target, match.Entities, match.Entities.Count));
    }

    /// <summary>
    /// The source <paramref name="wanted"/> means among <paramref name="sources"/>: the same name in any case, else the only
    /// one starting with it ("HDMI 3" → "HDMI 3 (eARC/ARC)"), else the only one holding it; null when none or several. Pure.
    /// </summary>
    public static string? MatchSource(IReadOnlyList<string> sources, string wanted)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(wanted);
        string w = HaSnapshot.Normalize(wanted);
        if (sources.FirstOrDefault(s => HaSnapshot.Normalize(s) == w) is { } same)
        {
            return same;
        }

        var starting = sources.Where(s => HaSnapshot.Normalize(s).StartsWith(w, StringComparison.Ordinal)).ToList();
        if (starting.Count == 1)
        {
            return starting[0];
        }

        var holding = sources.Where(s => HaSnapshot.Normalize(s).Contains(w, StringComparison.Ordinal)).ToList();
        return holding.Count == 1 ? holding[0] : null;
    }
}
