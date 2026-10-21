using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>ha_assist(text)</c> (2026-09-28, the user's call: Assist as a fallback): a sentence handed to Home Assistant's own
/// conversation agent (<c>Home Assistant Assist agent</c>, empty = the default) — "turn on the kitchen lights", "what is the
/// temperature in the bathroom?" — for when the typed tools do not fit. Judged as the service it is, <c>conversation.process</c>:
/// refused under <c>off</c>, run under <c>allow</c>, and under <c>ask</c> the sentence waits for the user's yes on the pane unless
/// <c>HomeAssistantSafeServices</c> names it (2026-10-14, the user's ask, found by the docs pass: it ran unasked under <c>ask</c>,
/// while Assist may unlock a door or open a garage as readily as it answers a question; headless, with nobody to ask, refuses).
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

    /// <summary>Assist's service, the name the policy and the safe list judge it by.</summary>
    public const string Domain = "conversation";
    public const string Service = "process";

    public HaAssistTool(HaSession ha, Func<string, CancellationToken, Task<bool?>>? confirm) : base(ha, confirm)
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

        if (!HaSession.Configured(Effective))
        {
            return HaText.NotConfigured;
        }

        // A sentence the question can't show whole is refused before it is asked (2026-10-20, the code review: the question cut and joined it,
        // and the yes sent the whole): the user approves exactly what acts. Only when it would be asked; allowed or safe, nothing is shown.
        if (HaPolicy.Judge(Effective, Domain, Service) == HaVerdict.Ask && HaText.AssistUnshowable(text) is { } unshowable)
        {
            return unshowable;
        }

        if (await GateAsync(Domain, Service, HaText.AssistQuestion(text), cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        return await AskAsync(Ha, text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Assist's answer to <paramref name="text"/>, unjudged: the tool gates first, and <c>/ha say</c> is the user's own. The
    /// snapshot is marked stale after, since Assist may have acted.
    /// </summary>
    public static async Task<string> AskAsync(HaSession ha, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ha);
        ArgumentNullException.ThrowIfNull(text);
        if (ha.Client() is not { } client)
        {
            return HaText.NotConfigured;
        }

        var reply = await client.ConversationAsync(text, ha.Effective.HomeAssistantAssistAgent, ha.Timeout, cancellationToken).ConfigureAwait(false);
        ha.Invalidate();
        return reply.Ok ? HaText.AssistAnswer(reply.Body) : reply.Error!;
    }
}
