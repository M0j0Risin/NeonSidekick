using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_call_service(domain, service, entity?, data?)</c> (2026-09-28): any Home Assistant service the typed tools do not
/// cover — a TV remote's key (<c>remote.send_command</c>), a button, a switch, a script, an automation. Judged by
/// <c>Home Assistant action policy</c>: under <c>ask</c> anything outside the safe list waits for the user's yes.
/// </summary>
public sealed class HaCallServiceTool : HaTool
{
    public const string ToolName = "ha_call_service";
    public const string DomainArgument = "domain";
    public const string ServiceArgument = "service";
    public const string EntityArgument = "entity";
    public const string DataArgument = "data";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "domain": { "type": "string", "description": "The service's domain, e.g. remote, button, switch, script, automation." },
            "service": { "type": "string", "description": "The service, e.g. send_command, press, turn_on." },
            "entity": { "type": "string", "description": "The entity it acts on: an id or a name; several joined with commas." },
            "data": { "type": "object", "description": "The service's other fields, e.g. {\"command\": \"Home\"} for remote.send_command." }
          },
          "required": ["domain", "service"]
        }
        """);

    public HaCallServiceTool(HaSession ha, Func<string, CancellationToken, Task<bool?>>? confirm) : base(ha, confirm)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Calls any Home Assistant service the other ha_ tools do not cover, such as remote.send_command (a TV remote key), button.press, switch.turn_off " +
        "or script.turn_on. Prefer ha_lights, ha_scene, ha_media and ha_todo when they fit. Some calls wait for the user's approval.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string domain = (Optional(arguments, DomainArgument) ?? "").ToLowerInvariant();
        string service = (Optional(arguments, ServiceArgument) ?? "").ToLowerInvariant();
        if (domain.Contains('.', StringComparison.Ordinal) && service.Length == 0)
        {
            // "light.turn_on" given whole as the domain.
            service = domain[(domain.IndexOf('.', StringComparison.Ordinal) + 1)..];
            domain = domain[..domain.IndexOf('.', StringComparison.Ordinal)];
        }

        if (domain.Length == 0)
        {
            return HaText.Missing(DomainArgument);
        }

        if (service.Length == 0)
        {
            return HaText.Missing(ServiceArgument);
        }

        string? data = Optional(arguments, DataArgument);
        if (HaJson.ServiceBody([], data, out string? bad) is null)
        {
            return bad;
        }

        IReadOnlyList<HaEntity> targets = [];
        if (Optional(arguments, EntityArgument) is { } entity)
        {
            var (_, match) = await ResolveAsync(entity, [], cancellationToken).ConfigureAwait(false);
            if (match.Error is not null)
            {
                return match.Error;
            }

            targets = match.Entities;
        }

        if (await GateAsync(domain, service, targets, data, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var reply = await Ha.CallAsync(domain, service, targets.Select(t => t.Id).ToList(), data, cancellationToken).ConfigureAwait(false);
        return reply.Ok ? HaText.Done(domain, service, targets) : reply.Error;
    }
}
