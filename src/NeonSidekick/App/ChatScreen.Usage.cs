using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Claude;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.App;

// ── /usage's Context tab (2026-11-01, feature idea #1) ───────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The characters the wire wraps around one tool's name, description and schema in a request's <c>tools</c> list:
    /// <c>{"type":"function","function":{"name":"","description":"","parameters":}}</c>. A tool's weight on the Context tab is its
    /// three parts plus these, over <see cref="ConversationCompactor.CharsPerToken"/>.
    /// </summary>
    internal const int ToolWrapperChars = 73;

    /// <summary>A tool's estimated tokens as the wire sends it: name, description and schema plus <see cref="ToolWrapperChars"/>.</summary>
    internal static long ToolTokens(AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        long chars = tool.Name.Length + (tool.Description?.Length ?? 0) + SchemaChars(tool.JsonSchema) + ToolWrapperChars;
        return chars / ConversationCompactor.CharsPerToken;
    }

    /// <summary>What a tool with no schema object sends in its place, as the request writers do.</summary>
    internal const string EmptySchema = """{"type":"object"}""";

    /// <summary>
    /// A schema's characters as the wire sends it (2026-11-02, the review: the raw text kept a literal's indentation and read 15–25% high):
    /// written minified, as the request writers write it; <see cref="EmptySchema"/> for anything but an object.
    /// </summary>
    internal static int SchemaChars(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return EmptySchema.Length;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            schema.WriteTo(writer);
        }

        return Encoding.UTF8.GetCharCount(buffer.WrittenSpan);
    }

    /// <summary>
    /// What the <c>/usage</c> Context tab shows (2026-11-01), read as the next turn would build it and with none of a turn's side effects:
    /// the system prompt's sections (<see cref="SystemPromptSummary.PromptSections"/>, what <c>/sys</c> shows), the offered tool groups
    /// (<see cref="ToolGroups"/>, <c>/sys</c>' Tools tab), and the history's breakdown with the thinking the next request sends back
    /// (none of an earlier turn's unless <c>LLM preserve thinking</c> is on, since the next request starts a turn). While a reply runs the
    /// history is the turn's own list, appended to without a lock, so it is not read (2026-11-02, the review): the tab shows the main
    /// assistant's <see cref="Assistant.LastSentEstimate"/> (marked as last sent), else works the history out from the server's count. The
    /// settings and the <c>/tools</c> list are read once, for the prompt and the groups alike; a switchable group's prompt sentences are
    /// what the prompt loses <see cref="SystemPromptFacts.Without"/> its switch.
    /// </summary>
    private ContextFacts ContextFacts()
    {
        var effective = _effective();
        var url = _session.Endpoint?.BaseUrl;
        bool inFlightOnly = Assistant.InFlightOnlyWire(url);
        if (ClaudeCliEndpoint.IsClaudeCli(url))
        {
            return new ContextFacts(_session.ContextLength, 0, [], [], null, null, 0, effective.LlmPreserveThinking, ClaudeCode: true);
        }

        var disabled = TurnDisabled(effective);
        var promptFacts = SystemPromptFacts(effective, disabled);
        var prompt = PromptParts(promptFacts);
        long promptTokens = prompt.Sum(p => p.Tokens);
        var groups = ToolGroups(effective, disabled)
            .Select(g => new ContextToolGroup(g.Label, g.Tools.Count, g.Tools.Sum(ToolTokens), g.Switch is not null,
                g.Switch is { } field ? Math.Max(0, promptTokens - PromptParts(promptFacts.Without(field)).Sum(p => p.Tokens)) : 0))
            .ToList();
        string? noTools = groups.Count == 0 ? SystemPromptSummary.NoToolsLine(effective.LlmOfferTools) : null;
        ConversationCompactor.TokenEstimate? history = null;
        bool asSent = false;
        // By the reply, not the turn (2026-11-02, review P11-1): a turn waiting on a watch alone has no request in flight, so the whole history
        // is what the next one sends, not "as last sent".
        if (!_replyRunning)
        {
            var list = _session.History.Messages;
            history = ConversationCompactor.Estimate(list, effective.LlmPreserveThinking && !inFlightOnly ? 0 : list.Count);
        }
        else if (_session.Assistant?.LastSentEstimate is { } sent)
        {
            history = sent;
            asSent = true;
        }

        var usage = _session.Usage;
        long counted = usage.LastRequest.Total;
        return new ContextFacts(_session.ContextLength, counted, prompt, groups, noTools, history, _session.History.TurnCount, effective.LlmPreserveThinking)
        {
            InFlightOnly = inFlightOnly,
            AsLastSent = asSent,
            ReplyRunning = _replyRunning,
            Sent = !usage.Conversation.IsEmpty,
            PreviousModel = counted > 0 && usage.HintHidden,
        };
    }

    /// <summary>The system prompt's sections in it, as estimated tokens.</summary>
    private static List<(string Label, long Tokens)> PromptParts(SystemPromptFacts facts) =>
        SystemPromptSummary.PromptSections(facts)
            .Where(s => s.InPrompt)
            .Select(s => (s.Label, (long)(s.Body.Length / ConversationCompactor.CharsPerToken)))
            .ToList();

    /// <summary>The tab <c>/usage</c>'s argument names: 0 for none (Statistics), <see cref="UsageText.ContextTabIndex"/> for <c>context</c>, null for anything else.</summary>
    internal static int? UsageTab(string args)
    {
        string word = args.Trim();
        return word.Length == 0 ? 0
            : string.Equals(word, UsageText.ContextWord, StringComparison.OrdinalIgnoreCase) ? UsageText.ContextTabIndex
            : null;
    }
}

// ── The reply footer (2026-11-01, feature idea #4) ───────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// A tool's own time, for its result line (2026-11-01, the reply footer; from the Assistant's stamps since 2026-11-02, the review): its
    /// <see cref="TurnEvent.ToolResult.Took"/> less its <see cref="TurnEvent.ToolResult.Asked"/> (an approval, <c>ask_user</c>, answered
    /// here or on the page), which also goes to the turn's <see cref="_turnToolAsks"/>. With the footer on, outside a <c>/botchat</c> turn, a
    /// tool that took <see cref="UsageText.SlowTool"/> or more gets its suffix; never the shell family (its note has its time),
    /// <c>ask_user</c> (the time is the user's), the advisor (its own view) or a call never run. Null for none.
    /// </summary>
    private string? ToolTimeFor(TurnEvent.ToolResult result)
    {
        _turnToolAsks += result.Asked;
        if (result.Took is not { } took || !_effective().ReplyFooter || _botTurnRunning
            || ShellToolNames.Contains(result.Name)
            || string.Equals(result.Name, AskUserTool.ToolName, StringComparison.Ordinal)
            || string.Equals(result.Name, NeonAdvisorTool.ToolName, StringComparison.Ordinal))
        {
            return null;
        }

        var shown = took - result.Asked;
        return UsageText.ToolTime(shown < TimeSpan.Zero ? TimeSpan.Zero : shown);
    }

    /// <summary>
    /// The reply footer's line for a turn's end (2026-11-01), or null: only with <see cref="Settings.AppSettingsData.ReplyFooter"/> on, for a
    /// main-chat turn that stands (<see cref="TurnOutcome.Continue"/>, cancelled ones included, or <see cref="TurnOutcome.Interrupted"/>) and
    /// isn't an error alone. The time is the turn's wall time less the asks inside its tools.
    /// </summary>
    private string? ReplyFooter(bool mainChat, TurnOutcome outcome, bool errorOnly, long started, TurnTrace trace)
    {
        if (!mainChat || errorOnly || outcome is not (TurnOutcome.Continue or TurnOutcome.Interrupted) || !_effective().ReplyFooter)
        {
            return null;
        }

        var usage = _session.Usage.LastReply;
        var elapsed = _time.GetElapsedTime(started) - _turnToolAsks;
        return UsageText.Footer(new FooterFacts(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed, usage.Output, usage.TokensPerSecond, trace.ToolCalls, _session.Endpoint?.ModelId));
    }
}
