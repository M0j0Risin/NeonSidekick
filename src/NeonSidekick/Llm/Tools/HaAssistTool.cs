using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_assist(text)</c> (2026-09-28, the user's call: Assist as a fallback): a sentence handed to Home Assistant's own
/// conversation agent (<c>Home Assistant Assist agent</c>, empty = the default) — "turn on the kitchen lights", "what is the
/// temperature in the bathroom?" — for when the typed tools do not fit. Assist reaches only the entities exposed to it, so
/// under <c>ask</c> it runs without the pane; under <c>off</c> it is refused, since it may act.
/// </summary>
public sealed class HaAssistTool : HaTool
{
    public const string ToolName = "ha_assist";
    public const string TextArgument = "text";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "One plain sentence for Home Assistant's Assist, e.g. turn off the kitchen lights." }
          },
          "required": ["text"]
        }
        """);

    public HaAssistTool(HaSession ha) : base(ha, confirm: null)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Hands one plain sentence to Home Assistant's own Assist agent, which acts or answers. A fallback for what the other ha_ tools do not cover.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string text = ToolArguments.ReadString(arguments, TextArgument).Trim();
        if (text.Length == 0)
        {
            return HaText.Missing(TextArgument);
        }

        return await AskAsync(Ha, text, cancellationToken, judged: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Assist's answer to <paramref name="text"/>. <paramref name="judged"/> (the model's call) refuses under policy <c>off</c>;
    /// <c>/ha say</c> is the user's own and passes false. The snapshot is marked stale after, since Assist may have acted.
    /// </summary>
    public static async Task<string> AskAsync(HaSession ha, string text, CancellationToken cancellationToken, bool judged)
    {
        ArgumentNullException.ThrowIfNull(ha);
        ArgumentNullException.ThrowIfNull(text);
        if (judged && HaPolicy.Resolve(ha.Effective.HomeAssistantActionPolicy) == HaPolicy.Off)
        {
            return HaText.PolicyOff;
        }

        if (ha.Client() is not { } client)
        {
            return HaText.NotConfigured;
        }

        var reply = await client.ConversationAsync(text, ha.Effective.HomeAssistantAssistAgent, ha.Timeout, cancellationToken).ConfigureAwait(false);
        ha.Invalidate();
        return reply.Ok ? HaText.AssistAnswer(reply.Body) : reply.Error!;
    }
}
