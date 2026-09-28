using System.Globalization;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm;

/// <summary>
/// <c>/compact</c>: shrinks what the model re-reads on every request without forgetting it the way
/// <c>/clear</c> does. Two modes (<see cref="CompactMode"/>): the older turns become one summary
/// message written by the model itself, or their bulky tool results become one-line stubs. Either
/// way the most recent turns (the setting <c>LLM compact keep recent</c>) stay word for word.
///
/// <para>Pure statics over the message list plus one orchestration (<see cref="RunAsync"/>) that
/// calls the summariser and swaps the history; nothing here touches the console, and the history
/// is untouched until the new list is complete, so a cancelled or failed summary leaves the
/// conversation as it was.</para>
///
/// <para>The shape after a summary: <c>user(summary) → the opening call pairs → the recent turns</c>.
/// A user message, because every chat template accepts what follows one and because it keeps
/// <see cref="ConversationHistory.TurnCount"/> above zero, so the next turn does not seed the
/// opening calls again; the clock and working-directory pairs are carried over as they were, so
/// <c>/cwd</c>'s in-place edit (<see cref="ConversationHistory.TryReplaceToolResult"/>) still finds
/// its result. The summary turn then ages out at <see cref="ConversationHistory.MaxTurns"/> like any
/// other.</para>
/// </summary>
public static class ConversationCompactor
{
    /// <summary>A tool result longer than this (characters) is stubbed by <see cref="Prune"/>; shorter ones are left, a stub would not be smaller.</summary>
    public const int PruneThreshold = 200;

    /// <summary>The summariser's system prompt. Pinned.</summary>
    public const string SummaryInstruction =
        "You are compacting a conversation between a user and Neon, a terminal sidekick with tools. " +
        "Write a summary Neon can continue the conversation from as if it remembered everything: the user's goals and requests, " +
        "what was decided or answered, every concrete fact learned from tool results (file names, paths, values, names, dates — even ones the user has not asked about yet), " +
        "anything still open or promised, and the user's preferences and tone. Be specific and concise, in plain text without markdown. " +
        "Do not answer the user, do not add commentary, and do not mention that this is a summary.";

    /// <summary>The line that asks for the summary, closing the summariser's request. Pinned.</summary>
    public const string SummaryRequestLine = "Summarise the conversation above.";

    /// <summary>What goes before the summary in the message the history keeps. Pinned.</summary>
    public const string SummaryPreamble = "The conversation so far was compacted into this summary; continue as if you remembered it:\n\n";

    /// <summary>One <see cref="ChatMessage"/> list split for a compact: the older turns (the opening call pairs among them, in place), those pairs alone, and the recent turns kept verbatim.</summary>
    public sealed record Plan(IReadOnlyList<ChatMessage> Older, IReadOnlyList<ChatMessage> Opening, IReadOnlyList<ChatMessage> Recent)
    {
        /// <summary>Whether there is anything to compact: an older turn that is not just the opening pairs.</summary>
        public bool HasOlderTurns => Older.Count > Opening.Count;
    }

    /// <summary>
    /// What a compact did: the message counts either side, the results stubbed (a prune, or the
    /// recent turns' under the automatic compact), the summariser's usage when the server reported
    /// one, and whether the older turns became a summary at all (false for a prune, and for the
    /// automatic compact that found nothing older and stubbed the recent turns alone). Since
    /// 2026-09-21 (<c>LLM compact show summary</c>) it also carries what the transcript may show:
    /// the summary's text, one <see cref="PrunedEntry"/> per stubbed result and (later that day,
    /// the detail's closing lines) how many messages were protected at either end.
    /// </summary>
    public sealed record Result(int MessagesBefore, int MessagesAfter, int Pruned, TokenUsage? Usage, bool Summarised = false)
    {
        /// <summary>The summariser's text, trimmed, when <see cref="Summarised"/>; null otherwise.</summary>
        public string? Summary { get; init; }

        /// <summary>One entry per stubbed result — the older turns' first, then the recent turns' under the automatic compact — in message order; <see cref="Pruned"/> is their count (a carrier's pictures counted each). Empty when none.</summary>
        public IReadOnlyList<PrunedEntry> Entries { get; init; } = [];

        /// <summary>How many messages at the start were protected: the opening call pairs (<see cref="Plan.Opening"/>), carried across a summary in place and never stubbed by a prune.</summary>
        public int OpeningKept { get; init; }

        /// <summary>How many messages at the end were protected: the recent turns (<see cref="Plan.Recent"/>) kept in place — verbatim by hand, their older results stubbed under the automatic compact.</summary>
        public int RecentKept { get; init; }
    }

    /// <summary>
    /// One stubbed result (2026-09-21): the tool that produced it (<see cref="UnknownTool"/> when no
    /// call in the history carries its id) and its length in characters, or — for a carrier's
    /// pictures — <c>view_image</c> with <paramref name="Pictures"/> above zero and no length.
    /// </summary>
    public sealed record PrunedEntry(string Tool, int Characters, int Pictures = 0);

    /// <summary>The tool name an entry carries when the result's call is not in the history (a hand-built list). Pinned.</summary>
    public const string UnknownTool = "tool";

    /// <summary>
    /// The summariser's system prompt for this turn's earlier iterations (2026-09-28, the mid-turn compact's second
    /// stage, <see cref="SummarisedTurn"/>): the model is in the middle of a request and must carry on from the note. Pinned.
    /// </summary>
    public const string TurnProgressInstruction =
        "You are compacting the work in progress of Neon, a terminal sidekick with tools, in the middle of answering the user's latest request. " +
        "Write a progress note Neon can continue the request from as if it remembered every step: what the user asked for, " +
        "each tool call made so far and what it found (file names, paths, values, names, dates, errors — every concrete fact), " +
        "what has been done or changed, and what is still left to do. Be specific and concise, in plain text without markdown. " +
        "Do not answer the user, do not add commentary, and do not mention that this is a summary.";

    /// <summary>The line that asks for the progress note, closing the second stage's request. Pinned.</summary>
    public const string TurnProgressRequest = "Write the progress note for the latest request above.";

    /// <summary>What goes before the progress note, appended to the turn's user message. Pinned.</summary>
    public const string TurnProgressPreamble = "\n\n(The work on this request so far was compacted into this progress note; carry on from it:)\n\n";

    /// <summary>
    /// The <see cref="AIContent.AdditionalProperties"/> key that marks the progress note's text part on a user message
    /// (value <c>true</c>), so a second compact of the same turn replaces the note rather than adding another. Never on the wire.
    /// </summary>
    public const string TurnProgressKey = "neon.turnProgress";

    /// <summary>
    /// The characters one token is estimated at by <see cref="EstimateTokens"/>: the usual rule of thumb for English
    /// text and code under the common tokenisers; JSON runs more tokens per character, which errs toward compacting.
    /// </summary>
    public const int CharsPerToken = 4;

    /// <summary>
    /// The tokens one picture is estimated at by <see cref="EstimateTokens"/>: what a vision model charges varies by
    /// model and size (a few hundred to a few thousand), and a picture is stubbed or summarised long before the figure matters.
    /// </summary>
    public const int PictureTokens = 1000;

    /// <summary>
    /// A rough token count for <paramref name="messages"/> (2026-09-28): the characters of their text, thinking,
    /// call names and arguments (as the wire's JSON) and results over <see cref="CharsPerToken"/>, plus
    /// <see cref="PictureTokens"/> per picture. A gate's heuristic, never a count: the mid-turn guard adds it to the
    /// last reported usage for what was appended since (<see cref="Assistant.ContextGuard"/>), and subtracts what a
    /// prune or a summary took away, so a request is judged before it goes out rather than after.
    /// </summary>
    public static long EstimateTokens(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        long chars = 0;
        long pictures = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        chars += text.Text?.Length ?? 0;
                        break;
                    case TextReasoningContent reasoning:
                        chars += reasoning.Text?.Length ?? 0;
                        break;
                    case FunctionCallContent call:
                        chars += call.Name.Length + Assistant.SerializeArguments(call.Arguments).Length;
                        break;
                    case FunctionResultContent { Result: string result }:
                        chars += result.Length;
                        break;
                    case FunctionResultContent { Result: { } other }:
                        chars += other.ToString()?.Length ?? 0;
                        break;
                    case DataContent:
                        pictures++;
                        break;
                }
            }
        }

        return chars / CharsPerToken + pictures * PictureTokens;
    }

    /// <summary>
    /// The last turn split for the mid-turn compact's second stage (2026-09-28): the messages before it, its user
    /// message, the opening and pending call pairs seeded inside it (kept in place, as <see cref="Split"/> keeps them),
    /// the earlier iterations the progress note replaces, and the last iteration — the last assistant message with a
    /// call and everything after it: its results and carrier, which the model has not read yet.
    /// <see cref="Transcript"/> is everything ahead of the last iteration, in order: what the summariser reads.
    /// </summary>
    public sealed record TurnPlan(IReadOnlyList<ChatMessage> Before, ChatMessage Start, IReadOnlyList<ChatMessage> Opening, IReadOnlyList<ChatMessage> Earlier, IReadOnlyList<ChatMessage> Last, IReadOnlyList<ChatMessage> Transcript)
    {
        /// <summary>Whether there is an earlier iteration to summarise: a call of the model's before the last one.</summary>
        public bool HasEarlierIterations => Earlier.Any(m => m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any());
    }

    /// <summary>
    /// <see cref="TurnPlan"/> over <paramref name="messages"/>; null with no turn or no call of the model's in the last one.
    /// </summary>
    public static TurnPlan? SplitTurn(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int start = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (ConversationHistory.IsTurnStart(messages[i]))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        int last = -1;
        for (int i = messages.Count - 1; i > start; i--)
        {
            if (messages[i].Role == ChatRole.Assistant && messages[i].Contents.OfType<FunctionCallContent>().Any(c => !Assistant.IsOpeningCallId(c.CallId)))
            {
                last = i;
                break;
            }
        }

        if (last < 0)
        {
            return null;
        }

        var opening = new List<ChatMessage>(4);
        var earlier = new List<ChatMessage>(last - start);
        for (int i = start + 1; i < last; i++)
        {
            if (OpeningCallId(messages[i]) is { } callId && i + 1 < last && HasResult(messages[i + 1], callId))
            {
                opening.Add(messages[i]);
                opening.Add(messages[++i]);
                continue;
            }

            earlier.Add(messages[i]);
        }

        return new TurnPlan([.. messages.Take(start)], messages[start], opening, earlier, [.. messages.Skip(last)], [.. messages.Take(last)]);
    }

    /// <summary>
    /// The list after the second stage (2026-09-28): the messages before the turn, the turn's user message with
    /// <see cref="TurnProgressPreamble"/> + <paramref name="summary"/> appended as a text part of its own (tagged
    /// <see cref="TurnProgressKey"/>, replacing an earlier note), the opening pairs, then the last iteration untouched.
    /// The note rides the user message rather than a message of its own because the templates that demand strict
    /// user/assistant alternation (the Mistral family) refuse a second user message, and the last assistant message
    /// may carry the Anthropic API's signed thinking, which has to go back first and unedited.
    /// </summary>
    public static List<ChatMessage> SummarisedTurn(string summary, TurnPlan plan)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(plan);
        var contents = new List<AIContent>(plan.Start.Contents.Count + 1);
        foreach (var content in plan.Start.Contents)
        {
            if (!IsTurnProgress(content))
            {
                contents.Add(content);
            }
        }

        contents.Add(new TextContent(TurnProgressPreamble + summary.Trim()) { AdditionalProperties = new AdditionalPropertiesDictionary { [TurnProgressKey] = true } });
        var messages = new List<ChatMessage>(plan.Before.Count + 1 + plan.Opening.Count + plan.Last.Count);
        messages.AddRange(plan.Before);
        messages.Add(new ChatMessage(ChatRole.User, contents) { AdditionalProperties = plan.Start.AdditionalProperties });
        messages.AddRange(plan.Opening);
        messages.AddRange(plan.Last);
        return messages;
    }

    /// <summary>Whether <paramref name="content"/> is a progress note's text part (<see cref="TurnProgressKey"/>).</summary>
    public static bool IsTurnProgress(AIContent content) =>
        content is TextContent { AdditionalProperties: { } properties } && properties.TryGetValue(TurnProgressKey, out object? tag) && tag is true;

    /// <summary>The summariser's closing user message: <see cref="SummaryRequestLine"/>, the focus appended when given. Pinned.</summary>
    public static string SummaryRequest(string? focus) =>
        string.IsNullOrWhiteSpace(focus) ? SummaryRequestLine : SummaryRequestLine + " Pay particular attention to: " + focus.Trim();

    /// <summary>The stub a pruned result becomes: <c>(a 4,312-character result, pruned by /compact)</c>. Pinned.</summary>
    public static string PrunedStub(int length) =>
        "(a " + length.ToString("N0", CultureInfo.InvariantCulture) + "-character result, pruned by /compact)";

    /// <summary>What a carrier's pictures become: <c>(a picture from view_image, pruned by /compact)</c>, <c>(2 pictures …)</c>. Pinned.</summary>
    public static string PrunedImageStub(int pictures) =>
        (pictures == 1 ? "(a picture" : "(" + pictures.ToString(CultureInfo.InvariantCulture) + " pictures") + " from " + Tools.ViewImageTool.ToolName + ", pruned by /compact)";

    /// <summary>
    /// Splits <paramref name="messages"/> at a user-message boundary: the last <paramref name="keepRecent"/>
    /// user turns are <see cref="Plan.Recent"/> (every message from the first of them to the end),
    /// the rest <see cref="Plan.Older"/>; zero keeps nothing verbatim, and a history with fewer
    /// turns than that has nothing older. The opening call pairs (<see cref="Assistant.IsOpeningCallId"/>)
    /// found among the older messages are listed again as <see cref="Plan.Opening"/>. Copies, never views.
    /// </summary>
    public static Plan Split(IReadOnlyList<ChatMessage> messages, int keepRecent)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfNegative(keepRecent);
        var users = new List<int>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (ConversationHistory.IsTurnStart(messages[i]))
            {
                users.Add(i);
            }
        }

        int cut = keepRecent == 0 ? messages.Count : users.Count < keepRecent ? 0 : users[^keepRecent];
        var older = new List<ChatMessage>(cut);
        var opening = new List<ChatMessage>(4);
        for (int i = 0; i < cut; i++)
        {
            older.Add(messages[i]);
            if (OpeningCallId(messages[i]) is { } callId && i + 1 < cut && HasResult(messages[i + 1], callId))
            {
                opening.Add(messages[i]);
                opening.Add(messages[i + 1]);
                older.Add(messages[++i]);
            }
        }

        var recent = new List<ChatMessage>(messages.Count - cut);
        for (int i = cut; i < messages.Count; i++)
        {
            recent.Add(messages[i]);
        }

        return new Plan(older, opening, recent);
    }

    /// <summary>
    /// The list with every tool result in the older turns longer than <see cref="PruneThreshold"/>
    /// replaced by <see cref="PrunedStub"/> — new messages and contents, the held ones untouched
    /// (<see cref="ConversationHistory.TryReplaceToolResult"/> stays the one in-place edit) — and the
    /// recent turns as they are. The opening pairs' results are never stubbed. A carrier's pictures
    /// (<see cref="ConversationHistory.IsImageCarrier"/>) go the same way — a new tagged message with
    /// <see cref="PrunedImageStub"/> for its text and no image parts, each picture counted as one
    /// result — since a picture is the bulkiest result there is. The count is how many were.
    /// <paramref name="entries"/>, when given, receives one <see cref="PrunedEntry"/> per stub (2026-09-21).
    /// The older turns' replies lose their thinking too (2026-09-28, with thinking sent back): the cheapest thing to drop,
    /// and what <c>LLM preserve thinking</c> would otherwise carry to the window's end. Not counted: the count is the
    /// results', and a prune with none to stub is none, the thinking kept.
    /// </summary>
    public static (List<ChatMessage> Messages, int Pruned) Prune(Plan plan, bool protectSkills = true, List<PrunedEntry>? entries = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var messages = new List<ChatMessage>(plan.Older.Count + plan.Recent.Count);
        int pruned = Stub(plan.Older, 0, plan.Older.Count, messages, protectSkills, entries, entries is null ? null : CallNames(plan.Older), dropThinking: true);
        messages.AddRange(plan.Recent);
        return (messages, pruned);
    }

    /// <summary>
    /// The list with the last turn's older tool results stubbed the way <see cref="Prune"/> stubs
    /// the older turns': every tool result and carrier from the last user message up to — not
    /// including — the last <see cref="ChatRole.Tool"/> message and what follows it, which are the
    /// last iteration's, the ones the model has not read yet. Everything before the last turn is
    /// copied as it is. The tool loop's mid-turn guard (<see cref="Assistant.ContextGuard"/>) and,
    /// over the recent turns it keeps, the automatic compact (<see cref="RunAsync"/>) both use this
    /// rule. Nothing to stub (no turn, no earlier iteration, no result long enough) is a copy and zero.
    /// <paramref name="entries"/>, when given, receives one <see cref="PrunedEntry"/> per stub (2026-09-21).
    /// </summary>
    public static (List<ChatMessage> Messages, int Pruned) PruneRecent(IReadOnlyList<ChatMessage> messages, bool protectSkills = true, List<PrunedEntry>? entries = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int start = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (ConversationHistory.IsTurnStart(messages[i]))
            {
                start = i;
                break;
            }
        }

        var result = new List<ChatMessage>(messages.Count);
        if (start < 0)
        {
            result.AddRange(messages);
            return (result, 0);
        }

        for (int i = 0; i < start; i++)
        {
            result.Add(messages[i]);
        }

        int pruned = StubBeforeLastIteration(messages, start, result, protectSkills, entries, entries is null ? null : CallNames(messages));
        return (result, pruned);
    }

    /// <summary>
    /// Appends <paramref name="messages"/>[<paramref name="from"/>..] to <paramref name="into"/> with
    /// the tool results and carriers before the last iteration stubbed: the last <see cref="ChatRole.Tool"/>
    /// message at or after <paramref name="from"/> and what follows it are copied. Returns the count stubbed.
    /// </summary>
    private static int StubBeforeLastIteration(IReadOnlyList<ChatMessage> messages, int from, List<ChatMessage> into, bool protectSkills, List<PrunedEntry>? entries = null, IReadOnlyDictionary<string, string>? names = null)
    {
        int keep = from;
        for (int i = messages.Count - 1; i >= from; i--)
        {
            if (messages[i].Role == ChatRole.Tool)
            {
                keep = i;
                break;
            }
        }

        int pruned = Stub(messages, from, keep, into, protectSkills, entries, names);
        for (int i = keep; i < messages.Count; i++)
        {
            into.Add(messages[i]);
        }

        return pruned;
    }

    /// <summary>
    /// Appends <paramref name="messages"/>[<paramref name="from"/>..<paramref name="to"/>) to <paramref name="into"/>,
    /// the tool results over the threshold and the carriers' pictures stubbed; returns the count stubbed.
    /// The opening pairs' results never are; a loaded skill's (<see cref="ConversationHistory.IsSkillResult"/>)
    /// is not while <paramref name="protectSkills"/> — the <c>Skill compact mode</c> setting.
    /// With <paramref name="entries"/> each stub is logged there, its tool looked up in <paramref name="names"/> (<see cref="CallNames"/>).
    /// With <paramref name="dropThinking"/> an assistant message goes without its <see cref="TextReasoningContent"/>, not counted.
    /// </summary>
    private static int Stub(IReadOnlyList<ChatMessage> messages, int from, int to, List<ChatMessage> into, bool protectSkills, List<PrunedEntry>? entries, IReadOnlyDictionary<string, string>? names, bool dropThinking = false)
    {
        int pruned = 0;
        for (int index = from; index < to; index++)
        {
            var message = messages[index];
            if (ConversationHistory.IsImageCarrier(message))
            {
                int pictures = message.Contents.Count(c => c is DataContent);
                if (pictures == 0)
                {
                    into.Add(message);
                    continue;
                }

                into.Add(new ChatMessage(ChatRole.User, PrunedImageStub(pictures))
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationHistory.CarrierKey] = true },
                });
                pruned += pictures;
                entries?.Add(new PrunedEntry(Tools.ViewImageTool.ToolName, 0, pictures));
                continue;
            }

            if (dropThinking && message.Role == ChatRole.Assistant && message.Contents.Any(c => c is TextReasoningContent))
            {
                var thoughtless = message.Clone();
                thoughtless.Contents = message.Contents.Where(c => c is not TextReasoningContent).ToList();
                into.Add(thoughtless);
                continue;
            }

            if (message.Role != ChatRole.Tool)
            {
                into.Add(message);
                continue;
            }

            List<AIContent>? rebuilt = null;
            for (int i = 0; i < message.Contents.Count; i++)
            {
                if (message.Contents[i] is FunctionResultContent { Result: string text } result
                    && text.Length > PruneThreshold && !Assistant.IsOpeningCallId(result.CallId)
                    && !(protectSkills && ConversationHistory.IsSkillResult(result)))
                {
                    rebuilt ??= new List<AIContent>(message.Contents);
                    rebuilt[i] = new FunctionResultContent(result.CallId, PrunedStub(text.Length));
                    pruned++;
                    entries?.Add(new PrunedEntry(names is not null && names.TryGetValue(result.CallId, out string? tool) ? tool : UnknownTool, text.Length));
                }
            }

            into.Add(rebuilt is null ? message : new ChatMessage(ChatRole.Tool, rebuilt));
        }

        return pruned;
    }

    /// <summary>
    /// Every tool call's id → the tool's name, over the assistant messages of <paramref name="messages"/>
    /// (2026-09-21): what a stubbed result is named by in its <see cref="PrunedEntry"/>, since a result
    /// carries only the call's id. A repeated id keeps the first.
    /// </summary>
    public static Dictionary<string, string> CallNames(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                continue;
            }

            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call && !string.IsNullOrEmpty(call.CallId))
                {
                    names.TryAdd(call.CallId, call.Name);
                }
            }
        }

        return names;
    }

    /// <summary>The list after a summary: <see cref="SummaryPreamble"/> + <paramref name="summary"/> as one user message, then the opening pairs, then the recent turns.</summary>
    public static List<ChatMessage> Summarised(string summary, Plan plan)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(plan);
        var messages = new List<ChatMessage>(1 + plan.Opening.Count + plan.Recent.Count)
        {
            new(ChatRole.User, SummaryPreamble + summary.Trim()),
        };
        messages.AddRange(plan.Opening);
        messages.AddRange(plan.Recent);
        return messages;
    }

    /// <summary>
    /// Whether the next turn should compact first: <paramref name="percent"/> above zero, a known
    /// window, and the last request's context (<see cref="TokenTally.LastRequest"/>) at or past
    /// that share of it. A zeroed last request — nothing sent yet, or just compacted — never fires.
    /// </summary>
    public static bool ShouldAutoCompact(TokenUsage lastRequest, ContextLength? window, int percent) =>
        percent > 0 && window is { Tokens: > 0 } w && lastRequest.Total > 0 && lastRequest.Total * 100L >= (long)w.Tokens * percent;

    /// <summary>
    /// One compact of <paramref name="assistant"/>'s history: null when there is nothing to do (no
    /// older turn; for <see cref="CompactMode.Prune"/>, no result long enough), otherwise the new
    /// list is swapped in and <see cref="TokenTally.AddCompaction"/> told. The summariser's
    /// exceptions (a failed or cancelled request, an empty summary) propagate with the history untouched.
    ///
    /// <para>With <paramref name="pruneRecent"/> (the automatic compact, never <c>/compact</c> by
    /// hand) the recent turns it keeps lose their older tool results too, by <see cref="PruneRecent"/>'s
    /// rule — the last turn's last iteration stays — so a turn that walked the context to the share
    /// by itself shrinks even though it is kept; with nothing older that alone is the compact, no
    /// summariser request (one over that history is what just failed), and the count is the result's.</para>
    /// </summary>
    public static async Task<Result?> RunAsync(Assistant assistant, TokenTally tally, CompactMode mode, int keepRecent, string? focus, CancellationToken cancellationToken, bool pruneRecent = false, bool protectSkills = true)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(tally);
        var history = assistant.History;
        var plan = Split(history.Messages, keepRecent);
        int before = history.Messages.Count;
        int recentPruned = 0;
        var recentEntries = new List<PrunedEntry>();
        if (pruneRecent && plan.Recent.Count > 0)
        {
            var kept = new List<ChatMessage>(plan.Recent.Count);
            recentPruned = StubBeforeLastIteration(plan.Recent, 0, kept, protectSkills, recentEntries, CallNames(plan.Recent));
            if (recentPruned > 0)
            {
                plan = plan with { Recent = kept };
            }
        }

        if (!plan.HasOlderTurns)
        {
            if (recentPruned == 0)
            {
                return null;
            }

            var shrunk = new List<ChatMessage>(plan.Older.Count + plan.Recent.Count);
            shrunk.AddRange(plan.Older);
            shrunk.AddRange(plan.Recent);
            history.Replace(shrunk);
            tally.AddCompaction(null);
            return new Result(before, shrunk.Count, recentPruned, null) { Entries = recentEntries, OpeningKept = plan.Opening.Count, RecentKept = plan.Recent.Count };
        }

        if (mode == CompactMode.Prune)
        {
            var olderEntries = new List<PrunedEntry>();
            var (pruned, count) = Prune(plan, protectSkills, olderEntries);
            count += recentPruned;
            if (count == 0)
            {
                return null;
            }

            history.Replace(pruned);
            tally.AddCompaction(null);
            return new Result(before, pruned.Count, count, null) { Entries = [.. olderEntries, .. recentEntries], OpeningKept = plan.Opening.Count, RecentKept = plan.Recent.Count };
        }

        var (summary, usage) = await assistant.SummarizeAsync(plan.Older, focus, cancellationToken).ConfigureAwait(false);
        var messages = Summarised(summary, plan);
        history.Replace(messages);
        tally.AddCompaction(usage);
        return new Result(before, messages.Count, recentPruned, usage, Summarised: true) { Summary = summary.Trim(), Entries = recentEntries, OpeningKept = plan.Opening.Count, RecentKept = plan.Recent.Count };
    }

    /// <summary>The opening call id an assistant message carries, or null.</summary>
    private static string? OpeningCallId(ChatMessage message)
    {
        if (message.Role != ChatRole.Assistant)
        {
            return null;
        }

        foreach (var content in message.Contents)
        {
            if (content is FunctionCallContent call && Assistant.IsOpeningCallId(call.CallId))
            {
                return call.CallId;
            }
        }

        return null;
    }

    private static bool HasResult(ChatMessage message, string callId) =>
        message.Role == ChatRole.Tool && message.Contents.Any(c => c is FunctionResultContent r && string.Equals(r.CallId, callId, StringComparison.Ordinal));
}
