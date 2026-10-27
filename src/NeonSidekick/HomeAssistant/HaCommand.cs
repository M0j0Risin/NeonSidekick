using System.Globalization;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// What one <c>/ha</c> line came to: the lines to print, whether it failed (an error's line), and whether the screen shows them on
/// the info pane (<paramref name="Paned"/>: <c>states</c>, 2026-10-04); headless prints them either way.
/// </summary>
public sealed record HaCommandResult(IReadOnlyList<string> Lines, bool Failed, bool Paned = false)
{
    public static HaCommandResult Error(string line) => new([line], true);

    public static HaCommandResult Of(params string[] lines) => new(lines, false);
}

/// <summary>
/// <c>/ha</c> (2026-09-28, the user's call: a direct command beside the model's tools): the house driven without the model —
/// instant, and working with a weak model or none. The user's own hand, so <see cref="HaPolicy"/> never judges it. One engine
/// for the screen and headless; its argument list (<see cref="Complete"/>) reads the last snapshot and never the network.
/// <list type="bullet">
/// <item><c>/ha</c> — the server and the overview;</item>
/// <item><c>/ha on|off|toggle &lt;name&gt; [brightness%]</c> — lights by room or name, or any switchable entity;</item>
/// <item><c>/ha scene &lt;name&gt;</c>;</item>
/// <item><c>/ha tv on|off|mute|unmute|up|down|vol &lt;0-100&gt;|source &lt;name&gt;</c> — the only media player;</item>
/// <item><c>/ha states [filter]</c> — a domain (<c>scene</c>) or words of the name, or an id for its attributes;</item>
/// <item><c>/ha say &lt;sentence&gt;</c> — Assist.</item>
/// </list>
/// </summary>
/// <summary>
/// How <c>/ha</c> asks before a change from the Web UI's page (later on 2026-10-14): <paramref name="Confirm"/> asks the question (true a yes;
/// false or null keeps it), and <paramref name="Safe"/> is <i>Home Assistant safe services</i> (null its defaults).
/// </summary>
public sealed record HaAsk(Func<string, CancellationToken, Task<bool?>> Confirm, IReadOnlyList<string>? Safe)
{
    /// <summary>Whether <paramref name="domain"/>.<paramref name="service"/> on <paramref name="what"/> may run: a read or a safe service at once, else the yes.</summary>
    public async Task<bool> AllowsAsync(string domain, string service, string what, CancellationToken cancellationToken)
    {
        if (HaPolicy.ReadServices.Contains(domain + "." + service) || HaPolicy.IsSafe(domain, service, Safe))
        {
            return true;
        }

        return await Confirm(HaText.ConfirmCall(domain, service, what), cancellationToken).ConfigureAwait(false) == true;
    }
}

public static class HaCommand
{
    /// <summary>The domains <c>/ha on|off|toggle</c> reaches; a light gets the light service, any other <c>homeassistant.*</c>.</summary>
    public static readonly IReadOnlyList<string> SwitchableDomains = ["light", "switch", "fan", "input_boolean", "media_player"];

    /// <summary>Runs one <c>/ha</c> line (<paramref name="args"/>, the text after the word).</summary>
    /// <param name="ask">
    /// A change from the Web UI's page asks first (later on 2026-10-14, the user's call: from the PC <c>/ha</c> acts unasked, the user's own
    /// hand at the keyboard): a service call that only reads (<see cref="HaPolicy.ReadServices"/>) or that <see cref="HaAsk.Safe"/> names runs,
    /// anything else waits for a yes. Null, nothing is asked.
    /// </param>
    public static async Task<HaCommandResult> RunAsync(HaSession ha, string args, CancellationToken cancellationToken, HaAsk? ask = null)
    {
        ArgumentNullException.ThrowIfNull(ha);
        ArgumentNullException.ThrowIfNull(args);
        if (ha.Client() is not { } client)
        {
            return HaCommandResult.Error(HaText.NotConfigured);
        }

        string text = args.Trim();
        string verb = text.Split(' ', 2)[0].ToLowerInvariant();
        string rest = text.Length > verb.Length ? text[verb.Length..].Trim() : "";
        switch (verb)
        {
            case "":
            {
                var config = await client.ConfigAsync(ha.Timeout, cancellationToken).ConfigureAwait(false);
                if (!config.Ok)
                {
                    return HaCommandResult.Error(config.Error!);
                }

                var (snapshot, error) = await ha.SnapshotAsync(cancellationToken, fresh: true).ConfigureAwait(false);
                if (snapshot is null)
                {
                    return HaCommandResult.Error(error!);
                }

                return new HaCommandResult([HaText.Server(client.BaseUrl, config.Body), .. HaText.Overview(snapshot)], false);
            }

            case "on":
            case "off":
            case "toggle":
                return await SwitchAsync(ha, ask, verb, rest, cancellationToken).ConfigureAwait(false);

            case "scene":
            {
                if (rest.Length == 0)
                {
                    return HaCommandResult.Error(HaText.Usage);
                }

                var (snapshot, error) = await ha.SnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (snapshot is null)
                {
                    return HaCommandResult.Error(error!);
                }

                var match = snapshot.Resolve(rest, ["scene"]);
                if (match.Error is not null)
                {
                    return HaCommandResult.Error(match.Error);
                }

                if (match.Entities.Count != 1)
                {
                    return HaCommandResult.Error(HaText.Ambiguous(rest, match.Entities.Take(HaSnapshot.MaxCandidates).ToList(), match.Entities.Count));
                }

                return await CallAsync(ha, ask, "scene", "turn_on", match.Entities, null, null, cancellationToken).ConfigureAwait(false);
            }

            case "tv":
                return await TvAsync(ha, ask, rest, cancellationToken).ConfigureAwait(false);

            case "states":
            {
                var (snapshot, error) = await ha.SnapshotAsync(cancellationToken, fresh: true).ConfigureAwait(false);
                if (snapshot is null)
                {
                    return HaCommandResult.Error(error!);
                }

                bool isDomain = rest.Length > 0 && !rest.Contains(' ', StringComparison.Ordinal) && snapshot.Of(rest.ToLowerInvariant()).Count > 0;
                string listing = HaStatesTool.List(snapshot, rest.Length == 0 || isDomain ? null : rest, isDomain ? rest : null, null);
                return new HaCommandResult(listing.Split('\n'), false, Paned: true);
            }

            case "say":
                if (rest.Length == 0)
                {
                    return HaCommandResult.Error(HaText.Usage);
                }

                if (ask is not null && !await ask.AllowsAsync("conversation", "process", rest, cancellationToken).ConfigureAwait(false))
                {
                    return HaCommandResult.Of(HaText.Kept);
                }

                string answer = await HaAssistTool.AskAsync(ha, rest, cancellationToken).ConfigureAwait(false);
                return answer.StartsWith("Error:", StringComparison.Ordinal) ? HaCommandResult.Error(answer) : HaCommandResult.Of(answer);

            default:
                return HaCommandResult.Error(HaText.Usage);
        }
    }

    /// <summary>
    /// <c>on|off|toggle &lt;name&gt; [n%]</c>: a trailing number (with or without %) is the brightness, on only — unless the whole
    /// text names something (2026-10-02, the user's report: <c>/ha toggle Den Piano 1</c> read the 1 as a brightness and asked
    /// which of Den Piano 1 and 2; the quoted name worked only because <c>1"</c> is no number). The whole text is tried first
    /// and the split only when it names nothing, so <c>on den 40</c> is still the den at 40% and a miss reports the split's target.
    /// </summary>
    private static async Task<HaCommandResult> SwitchAsync(HaSession ha, HaAsk? ask, string verb, string rest, CancellationToken cancellationToken)
    {
        var (target, brightness) = SplitBrightness(rest);
        if (target.Length == 0)
        {
            return HaCommandResult.Error(HaText.Usage);
        }

        var (snapshot, error) = await ha.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return HaCommandResult.Error(error!);
        }

        HaMatch? whole = brightness is null ? null : ResolveSwitchable(snapshot, rest.Trim());
        if (whole is { Error: null })
        {
            brightness = null;
        }

        var match = whole is { Error: null } ? whole : ResolveSwitchable(snapshot, target);
        if (match.Error is not null)
        {
            return HaCommandResult.Error(match.Error);
        }

        string service = verb switch { "on" => "turn_on", "off" => "turn_off", _ => "toggle" };
        bool lights = match.Entities.All(e => e.Domain == "light");
        int? level = verb == "on" && lights ? brightness : null;
        string? data = level is { } b ? HaLightsTool.Data(b, null, null, null) : null;
        return await CallAsync(ha, ask, lights ? "light" : "homeassistant", service, match.Entities, data, level is { } l ? l.ToString(CultureInfo.InvariantCulture) + "%" : null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A light by <paramref name="target"/>, else any switchable entity (a switch, a fan, the TV); a miss is the light's error.</summary>
    private static HaMatch ResolveSwitchable(HaSnapshot snapshot, string target)
    {
        var match = snapshot.Resolve(target, ["light"]);
        if (match.Error is null)
        {
            return match;
        }

        var other = snapshot.Resolve(target, SwitchableDomains);
        return other.Error is null ? other : match;
    }

    /// <summary>
    /// A target and its trailing brightness — <c>den 40%</c>, <c>den 40</c> — the number 0 to 100. Pure: a name that ends in a
    /// number (<c>Den Piano 1</c>) splits too, and <see cref="SwitchAsync"/> tries the whole text first (2026-10-02).
    /// </summary>
    public static (string Target, int? Brightness) SplitBrightness(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        int space = trimmed.LastIndexOf(' ');
        if (space > 0 && int.TryParse(trimmed[(space + 1)..].TrimEnd('%'), NumberStyles.None, CultureInfo.InvariantCulture, out int level) && level is >= 0 and <= 100)
        {
            return (trimmed[..space].Trim(), level);
        }

        return (trimmed, null);
    }

    /// <summary><c>tv …</c>: the only media player (or the ambiguity), one action.</summary>
    private static async Task<HaCommandResult> TvAsync(HaSession ha, HaAsk? ask, string rest, CancellationToken cancellationToken)
    {
        string action = rest.Split(' ', 2)[0].ToLowerInvariant();
        string value = rest.Length > action.Length ? rest[action.Length..].Trim() : "";
        var (snapshot, error) = await ha.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return HaCommandResult.Error(error!);
        }

        var (player, refused) = HaMediaTool.Player(snapshot, null);
        if (player is null)
        {
            return HaCommandResult.Error(refused!);
        }

        switch (action)
        {
            case "on":
            case "off":
                return await CallAsync(ha, ask, "media_player", action == "on" ? "turn_on" : "turn_off", [player], null, null, cancellationToken).ConfigureAwait(false);
            case "mute":
            case "unmute":
                bool mute = action == "mute";
                return await CallAsync(ha, ask, "media_player", "volume_mute", [player], HaJson.Object(w => w.WriteBoolean("is_volume_muted", mute)), mute ? "muted" : "unmuted", cancellationToken).ConfigureAwait(false);
            case "up":
            case "down":
                return await CallAsync(ha, ask, "media_player", action == "up" ? "volume_up" : "volume_down", [player], null, null, cancellationToken).ConfigureAwait(false);
            case "vol":
            case "volume":
                if (!int.TryParse(value.TrimEnd('%'), NumberStyles.None, CultureInfo.InvariantCulture, out int level) || level > 100)
                {
                    return HaCommandResult.Error(HaText.Usage);
                }

                return await CallAsync(ha, ask, "media_player", "volume_set", [player], HaJson.Object(w => w.WriteNumber("volume_level", Math.Round(level / 100.0, 2))), "volume " + level.ToString(CultureInfo.InvariantCulture) + "%", cancellationToken).ConfigureAwait(false);
            case "source":
                var sources = HaJson.Strings(player.Attributes, "source_list");
                if (value.Length == 0 || HaMediaTool.MatchSource(sources, value) is not { } source)
                {
                    return HaCommandResult.Error(HaText.BadChoice("source", value, sources.Count == 0 ? ["(the player lists none)"] : sources));
                }

                return await CallAsync(ha, ask, "media_player", "select_source", [player], HaJson.Object(w => w.WriteString("source", source)), "source " + source, cancellationToken).ConfigureAwait(false);
            default:
                return HaCommandResult.Error(HaText.Usage);
        }
    }

    private static async Task<HaCommandResult> CallAsync(HaSession ha, HaAsk? ask, string domain, string service, IReadOnlyList<HaEntity> targets, string? data, string? detail, CancellationToken cancellationToken)
    {
        if (ask is not null && !await ask.AllowsAsync(domain, service, string.Join(", ", targets.Select(t => t.Name)), cancellationToken).ConfigureAwait(false))
        {
            return HaCommandResult.Of(HaText.Kept);
        }

        var reply = await ha.CallAsync(domain, service, targets.Select(t => t.Id).ToList(), data, cancellationToken).ConfigureAwait(false);
        return reply.Ok ? HaCommandResult.Of(HaText.Done(domain, service, targets, detail)) : HaCommandResult.Error(reply.Error!);
    }

    /// <summary>
    /// <c>/ha</c>'s argument list over <paramref name="snapshot"/> (the last one read; null = the verbs alone): the verbs; after
    /// <c>on</c>/<c>off</c>/<c>toggle</c> the rooms and the switchable names; after <c>scene</c> the scenes; after <c>tv</c> its
    /// actions and after <c>tv source</c> the player's sources. Each item is the whole argument, as <c>/comfy edit</c>'s are. Pure.
    /// Since 2026-10-27: <c>stills</c> and, after it, its three words with or without a snapshot (<paramref name="stills"/> false for the
    /// Web UI's page, which refuses <c>/ha stills</c>); after <c>states</c> the snapshot's domains, then its entity ids.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(HaSnapshot? snapshot, string argText, bool stills = true)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int space = argText.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            var verbs = stills ? HaText.Verbs : HaText.Verbs.Where(v => v != HaStillsCommand.Word);
            return MentionCompleter.Matches(verbs.Select(v => new CompletionItem(v, VerbNote(v))).ToList(), argText);
        }

        string verb = argText[..space].ToLowerInvariant();
        string head = argText[..(space + 1)];
        if (verb == HaStillsCommand.Word)
        {
            return stills && argText.IndexOf(' ', space + 1) < 0
                ? MentionCompleter.Matches(HaStillsCommand.Words.Select(w => new CompletionItem(head + w.Text, w.Note)).ToList(), argText)
                : [];
        }

        if (snapshot is null)
        {
            return verb == "tv" ? MentionCompleter.Matches(HaText.TvActions.Select(a => new CompletionItem(head + a, "")).ToList(), argText) : [];
        }

        IEnumerable<CompletionItem> items = verb switch
        {
            "on" or "off" or "toggle" => snapshot.Areas.Select(a => new CompletionItem(head + a.Name, "room"))
                .Concat(snapshot.Entities.Where(e => SwitchableDomains.Contains(e.Domain, StringComparer.Ordinal) && !e.Unavailable).Select(e => new CompletionItem(head + e.Name, e.Domain + " · " + e.State))),
            "scene" => snapshot.Of("scene").Select(e => new CompletionItem(head + e.Name, e.Area ?? "scene")),
            "tv" => TvItems(snapshot, argText, head),
            "states" => snapshot.Entities.Select(e => e.Domain).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(d => new CompletionItem(head + d, "domain"))
                .Concat(snapshot.Entities.Select(e => new CompletionItem(head + e.Id, e.Name))),
            _ => [],
        };
        return MentionCompleter.Matches(items.DistinctBy(i => i.Text, StringComparer.OrdinalIgnoreCase).ToList(), argText);
    }

    private static IEnumerable<CompletionItem> TvItems(HaSnapshot snapshot, string argText, string head)
    {
        string sourceHead = head + "source ";
        if (argText.StartsWith(sourceHead, StringComparison.OrdinalIgnoreCase) && snapshot.Of("media_player") is [var player])
        {
            return HaJson.Strings(player.Attributes, "source_list").Select(s => new CompletionItem(sourceHead + s, "source"));
        }

        return HaText.TvActions.Select(a => new CompletionItem(head + a, ""));
    }

    private static string VerbNote(string verb) => verb switch
    {
        "on" => "turn a room or a light on, optionally at a brightness",
        "off" => "turn a room or a light off",
        "toggle" => "toggle a room or a light",
        "scene" => "activate a scene",
        "tv" => "the TV: on, off, mute, volume, source",
        "states" => "list entities, by domain or name",
        "say" => "hand a sentence to Assist",
        "stills" => "open or empty the camera stills' folder",
        _ => "",
    };
}
