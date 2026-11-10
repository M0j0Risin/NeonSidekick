using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Llm;

/// <summary>
/// <c>/compact</c>: shrinks what the model re-reads on every request without forgetting it the way
/// <c>/clear</c> does. Two modes (<see cref="CompactMode"/>): the older turns become one summary
/// message written by the model itself, or their bulky tool results and long call arguments (2026-10-17) become one-line stubs. Either
/// way the most recent turns (the setting <c>LLM compact keep recent</c>) stay word for word.
///
/// <para>Pure statics over the message list plus one orchestration (<see cref="RunAsync"/>) that
/// calls the summariser and swaps the history; nothing here touches the console, and the history
/// is untouched until the new list is complete, so a cancelled or failed summary leaves the
/// conversation as it was.</para>
///
/// <para>The shape after a summary: <c>user(summary) → the opening call pairs → the recent turns</c>; the opener the first turn's
/// pairs follow (<see cref="ConversationHistory.IsOpener"/>, 2026-10-30) is summarised with the older turns, the summary taking its place.
/// A user message, because every chat template accepts what follows one and because it keeps
/// <see cref="ConversationHistory.TurnCount"/> above zero, so the next turn does not seed the
/// opening calls again; the clock and working-directory pairs are carried over as they were, so
/// <c>/cwd</c>'s in-place edit (<see cref="ConversationHistory.TryReplaceToolResult"/>) still finds
/// its result. The summary turn then ages out at <see cref="ConversationHistory.MaxTurns"/> like any
/// other.</para>
/// </summary>
public static partial class ConversationCompactor
{
    /// <summary>A tool result longer than this (characters) is stubbed by <see cref="PruneOlderTurns"/>; shorter ones are left, a stub would not be smaller.</summary>
    public const int PruneThreshold = 200;

    /// <summary>
    /// A call's string argument longer than this (characters) is stubbed wherever a result would be (2026-10-17, the toki-pona
    /// session: two 27 KB <c>write_file</c> bodies filled the context while their results were pruned); a name, a path, a query stays.
    /// </summary>
    public const int ArgumentPruneThreshold = 2_000;

    /// <summary>
    /// The tools whose arguments are never stubbed by a prune (2026-10-17): <c>present_plan</c>'s <c>markdown</c> is the plan an approved
    /// plan is carried out from in this conversation. Since 2026-10-18 only the latest presentation is kept whole: the earlier ones are
    /// stubbed by <see cref="StubSupersededPlans"/>, the first step of every compaction. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> KeepsArguments = [Tools.PresentPlanTool.ToolName];

    /// <summary>
    /// The tools whose list arguments are recorded elsewhere (2026-10-18, the code audit): <c>present_audit</c>'s findings, retractions,
    /// revisions, nits, questions and coverage are in the report on disk, so a long array of them is stubbed too, as a long string is;
    /// no other tool's arrays are touched. Only a <see cref="System.Text.Json.JsonElement"/> array is measured (by its JSON's length): any other
    /// shape is left alone rather than serialized, since serializing an unknown type is the reflection path NativeAOT forbids. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> RecordedArguments = [Tools.PresentAuditTool.ToolName];

    /// <summary>The stub a pruned argument becomes: <c>(a 27,610-character argument, pruned by /compact)</c> (2026-10-17). Pinned.</summary>
    public static string PrunedArgumentStub(int length) =>
        "(a " + length.ToString("N0", CultureInfo.InvariantCulture) + "-character argument, pruned by /compact)";

    /// <summary>
    /// Whether <paramref name="text"/> is a pruned stub, a result's or an argument's (2026-10-17): a small model that copied one back
    /// as a write's content would replace a file with it. The write tools refuse it.
    /// </summary>
    public static bool IsPrunedStub(string? text) => text is not null && StubPattern().IsMatch(text);

    /// <summary>
    /// The first of a call's arguments that is a pruned stub, a string or a JSON string (2026-10-21, the user's log: a gap pass sent
    /// <c>neon_advisor</c> the stub of an earlier question as its <c>question</c>, and the advisor was asked "(a 8,402-character argument,
    /// pruned by /compact)"); null when none is. Top-level arguments only, and none of <paramref name="except"/>: those a tool refuses in its own
    /// words (<see cref="Tools.IRefusesPrunedStubs.StubArguments"/>, 2026-10-22). A list's items are the tools' own to check.
    /// </summary>
    public static string? StubbedArgument(IDictionary<string, object?>? arguments, IReadOnlyCollection<string>? except = null)
    {
        if (arguments is null)
        {
            return null;
        }

        foreach (var (name, value) in arguments)
        {
            // The one reading of a string argument (2026-10-22, a review: a copy of ArgumentText's switch).
            if (except?.Contains(name) != true && IsPrunedStub(ArgumentText(value)))
            {
                return name;
            }
        }

        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\(a [\d,]+-character (?:result|argument), pruned by /compact\)\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex StubPattern();

    /// <summary>The summariser's system prompt. Pinned.</summary>
    public const string SummaryInstruction =
        "You are compacting a conversation between a user and Neon, a terminal sidekick with tools. " +
        "Write a summary Neon can continue the conversation from as if it remembered everything: the user's goals and requests, " +
        "what was decided or answered, every concrete fact learned from tool results (file names, paths, values, names, dates — even ones the user has not asked about yet), " +
        "anything still open or promised, and the user's preferences and tone. Be specific and concise, in plain text without Markdown. " +
        "Do not answer the user, do not add commentary, and do not mention that this is a summary.";

    /// <summary>The line that asks for the summary, closing the summariser's request. Pinned.</summary>
    public const string SummaryRequestLine = "Summarize the conversation above.";

    /// <summary>What goes before the summary in the message the history keeps. Pinned.</summary>
    public const string SummaryPreamble = "The conversation so far was compacted into this summary; continue as if you remembered it:\n\n";

    /// <summary>One <see cref="ChatMessage"/> list split for a compact: the older turns (the opening call pairs among them, in place), those pairs alone, and the recent turns kept verbatim.</summary>
    public sealed record Plan(IReadOnlyList<ChatMessage> Older, IReadOnlyList<ChatMessage> Opening, IReadOnlyList<ChatMessage> Recent)
    {
        /// <summary>
        /// Whether there is anything to compact: an older turn that is not just the opening pairs and the opener ahead of them
        /// (<see cref="ConversationHistory.IsOpener"/>, 2026-10-30: since the first turn's pairs moved ahead of its message, a history
        /// of exactly <c>keepRecent</c> turns has them older than the cut).
        /// </summary>
        public bool HasOlderTurns => Older.Count > Opening.Count + Older.Count(ConversationHistory.IsOpener);

        /// <summary>
        /// <see cref="HasOlderTurns"/>, an earlier summary head (<see cref="IsSummary"/>) not counted (2026-11-10, review P9-1): the mid-turn
        /// guard's older-turn summary (<c>Assistant.PlanOlderCompact</c>) doesn't summarize a summary. <c>/compact</c> keeps <see cref="HasOlderTurns"/>.
        /// </summary>
        public bool HasOlderBeyondSummary => Older.Count > Opening.Count + Older.Count(m => ConversationHistory.IsOpener(m) || IsSummary(m));
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

        /// <summary>How many long call arguments were stubbed besides the results (2026-10-17, <see cref="ArgumentPruneThreshold"/>); <see cref="Entries"/> lists them with their argument named.</summary>
        public int ArgumentsPruned { get; init; }
    }

    /// <summary>
    /// One stubbed result (2026-09-21): the tool that produced it (<see cref="UnknownTool"/> when no
    /// call in the history carries its id) and its length in characters, or — for a carrier's
    /// pictures — <c>view_image</c> with <paramref name="Pictures"/> above zero and no length. <paramref name="Argument"/> (2026-10-17)
    /// names a stubbed call argument (<c>content</c>) instead of a result, its tool the call's.
    /// </summary>
    public sealed record PrunedEntry(string Tool, int Characters, int Pictures = 0, string? Argument = null);

    /// <summary>The tool name an entry carries when the result's call is not in the history (a hand-built list). Pinned.</summary>
    public const string UnknownTool = "tool";

    /// <summary>
    /// Whether <paramref name="message"/> is a compact's summary head (2026-11-10, review P9-1; <c>/rewind</c>'s test until then): the user
    /// message <see cref="Summarised"/> builds, whose text starts with <see cref="SummaryPreamble"/>. Holds after a session reload.
    /// </summary>
    public static bool IsSummary(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Role == ChatRole.User && message.Text.StartsWith(SummaryPreamble, StringComparison.Ordinal);
    }

    /// <summary>
    /// The summariser's system prompt for this turn's earlier iterations (2026-09-28, the mid-turn compact's second
    /// stage, <see cref="SummarisedTurn"/>): the model is in the middle of a request and must carry on from the note. Pinned.
    /// </summary>
    public const string TurnProgressInstruction =
        "You are compacting the work in progress of Neon, a terminal sidekick with tools, in the middle of answering the user's latest request. " +
        "Write a progress note Neon can continue the request from as if it remembered every step: what the user asked for, " +
        "each tool call made so far and what it found (file names, paths, values, names, dates, errors — every concrete fact), " +
        "what has been done or changed, and what is still left to do. Be specific and concise, in plain text without Markdown. " +
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
    /// The characters one token is estimated at by <see cref="Estimate"/>: the usual rule of thumb for English
    /// text and code under the common tokenisers. JSON, code and logs run more tokens per character, so this figure errs toward
    /// overflow, not toward compacting (2026-11-09: corrected); the mid-turn guard counts what a round just added at
    /// <see cref="GuardCharsPerToken"/> instead.
    /// </summary>
    public const int CharsPerToken = 4;

    /// <summary>
    /// The characters one token is estimated at when the mid-turn guard projects the round just appended (2026-11-09, a turn that ran
    /// the window out before the guard fired): tool results are mostly JSON, code and logs, which run nearer three characters a token than
    /// four, and a low estimate lets a request past the window. Used only there and in <see cref="CutLatestResults"/>' budget;
    /// <see cref="CharsPerToken"/> stays the figure for <c>/usage</c> and the compactor's other estimates.
    /// </summary>
    public const int GuardCharsPerToken = 3;

    /// <summary>The most tokens <see cref="PictureTokensFor"/> counts for one picture (2026-11-09): a cap, so a huge picture's guess stays sane.</summary>
    public const int MaxPictureTokens = 16_384;

    /// <summary>
    /// The tokens a new picture of <paramref name="width"/> × <paramref name="height"/> is estimated at by the mid-turn guard (2026-11-09):
    /// about one token per 750 pixels (the common vision tokenizers' rate), at least <see cref="PictureTokens"/> and at most
    /// <see cref="MaxPictureTokens"/>. A size of 0 or less (unknown) gives the floor. The product is taken in <c>long</c>, since a tool's
    /// picture isn't always fitted to <see cref="Files.ImageFile.MaxSide"/>.
    /// </summary>
    public static long PictureTokensFor(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return PictureTokens;
        }

        return Math.Clamp((long)width * height / 750, PictureTokens, MaxPictureTokens);
    }

    /// <summary>
    /// The tokens one picture is estimated at by <see cref="Estimate"/> (and the mid-turn guard's floor, <see cref="PictureTokensFor"/>):
    /// what a vision model charges varies by model and size (a few hundred to a few thousand), and a picture is stubbed or summarised long
    /// before the figure matters.
    /// </summary>
    public const int PictureTokens = 1000;

    /// <summary>
    /// The estimate by kind (2026-11-01, the <c>/usage</c> Context tab): characters of text, call names and arguments, results,
    /// a PDF's text and thinking, and the pictures counted, in one walk. Thinking counts only for messages at index
    /// <paramref name="thinkingFrom"/> or later (0 = all, <c>messages.Count</c> = none), since the wire sends back only the turn
    /// in flight's unless every turn's is preserved (<see cref="ThinkingFrom"/>). A gate's heuristic, never a count: the mid-turn guard
    /// (<see cref="Assistant.ContextGuard"/>) adds what a round appended to the last reported usage, judges the next request against the share,
    /// the ceiling summary and the cut before it goes out, and subtracts what each stage took away; an overflow's retry starts from the server's
    /// own figures (2026-11-10).
    /// </summary>
    public static TokenEstimate Estimate(IReadOnlyList<ChatMessage> messages, int thinkingFrom)
    {
        ArgumentNullException.ThrowIfNull(messages);
        long text = 0, calls = 0, results = 0, documents = 0, thinking = 0, pictures = 0;
        for (int i = 0; i < messages.Count; i++)
        {
            foreach (var content in messages[i].Contents)
            {
                switch (content)
                {
                    case TextContent t:
                        text += t.Text?.Length ?? 0;
                        break;
                    case TextReasoningContent reasoning:
                        if (i >= thinkingFrom)
                        {
                            thinking += reasoning.Text?.Length ?? 0;
                        }

                        break;
                    case FunctionCallContent call:
                        calls += call.Name.Length + Assistant.SerializeArguments(call.Arguments).Length;
                        break;
                    case FunctionResultContent { Result: string result }:
                        results += result.Length;
                        break;
                    case FunctionResultContent { Result: { } other }:
                        results += other.ToString()?.Length ?? 0;
                        break;
                    case DataContent document when ConversationHistory.DocumentText(document) is { } pdf:
                        // An attached PDF (2026-10-14, the PDF plan's gap 4) weighs its text, not one picture's tokens.
                        documents += pdf.Length;
                        break;
                    case DataContent:
                        pictures++;
                        break;
                }
            }
        }

        return new TokenEstimate(text, calls, results, documents, thinking, pictures);
    }

    /// <summary>
    /// The first message whose thinking the wire sends back (2026-11-01): every one with <paramref name="preserveAll"/>
    /// (<c>LLM preserve thinking</c>), else the turn in flight's, from <see cref="ConversationHistory.InFlightStart"/> — the
    /// rule <see cref="OpenAICompatibleChatClient.WithReasoningBack"/> applies.
    /// </summary>
    public static int ThinkingFrom(IReadOnlyList<ChatMessage> messages, bool preserveAll)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return preserveAll ? 0 : ConversationHistory.InFlightStart(messages);
    }

    /// <summary>
    /// <see cref="Estimate"/>'s result (2026-11-01): characters per kind and the pictures counted. The token figures divide
    /// the characters by <see cref="CharsPerToken"/>; <see cref="Total"/> divides their sum (<see cref="Chars"/>) once, and
    /// <see cref="HistoryTokens"/> is what the total holds beyond the pictures and the thinking, so the three add up to it.
    /// </summary>
    public readonly record struct TokenEstimate(long TextChars, long ToolCallChars, long ToolResultChars, long DocumentChars, long ThinkingChars, long Pictures)
    {
        /// <summary>Every character counted, the five kinds summed (2026-11-10, review P15-3: one sum for <see cref="Total"/> and <see cref="CharsTokens"/>).</summary>
        public long Chars => TextChars + ToolCallChars + ToolResultChars + DocumentChars + ThinkingChars;

        /// <summary>The estimate in tokens: every character over <see cref="CharsPerToken"/>, plus <see cref="PictureTokens"/> a picture.</summary>
        public long Total => Chars / CharsPerToken + PictureTokens * Pictures;

        /// <summary>The pictures' tokens.</summary>
        public long PictureTokenCount => PictureTokens * Pictures;

        /// <summary>
        /// The characters alone in tokens at <paramref name="charsPerToken"/> characters a token (2026-11-09, the mid-turn guard's
        /// <see cref="GuardCharsPerToken"/>): the five counts summed, then divided once; no pictures (the guard counts them by size).
        /// </summary>
        public long CharsTokens(int charsPerToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(charsPerToken);
            return Chars / charsPerToken;
        }

        /// <summary>The thinking's tokens (the part counted).</summary>
        public long ThinkingTokens => ThinkingChars / CharsPerToken;

        /// <summary>The tool results' tokens, part of <see cref="HistoryTokens"/>.</summary>
        public long ToolResultTokens => ToolResultChars / CharsPerToken;

        /// <summary>Everything else: text, calls, results and documents, so <c>HistoryTokens + PictureTokenCount + ThinkingTokens == Total</c>.</summary>
        public long HistoryTokens => Total - PictureTokenCount - ThinkingTokens;
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
            // A user message holding tool results is no turn's start (2026-11-10, review P19-2: InFlightStart's rule, so the summary and
            // the cut agree on the newest round).
            if (ConversationHistory.IsTurnStart(messages[i]) && !messages[i].Contents.Any(c => c is FunctionResultContent))
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

    /// <summary>
    /// What a carrier's pictures become: <c>(a picture from view_image, pruned by /compact)</c>, <c>(2 pictures …)</c>.
    /// Since 2026-10-03 <paramref name="source"/> is the tool the carrier credits (<see cref="ConversationHistory.CarrierSource"/>:
    /// <c>generate_image</c>'s pictures once read as <c>view_image</c>'s too), <c>view_image</c> when none is known. Pinned.
    /// </summary>
    public static string PrunedImageStub(int pictures, string? source = null) =>
        (pictures == 1 ? "(a picture" : "(" + pictures.ToString(CultureInfo.InvariantCulture) + " pictures") + " from " + (string.IsNullOrWhiteSpace(source) ? Tools.ViewImageTool.ToolName : source) + ", pruned by /compact)";

    /// <summary>The <see cref="PrunedEntry"/> tool a user's own picture is listed under in the prune report (2026-10-03, <c>2 pictures from you</c>): it came from no tool. Pinned.</summary>
    public const string UserPictureEntry = "you";

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
    /// result — since a picture is the bulkiest result there is, and an older turn's own pictures too (2026-10-03). The older turns'
    /// replies lose their thinking (2026-09-28, with thinking sent back): the cheapest thing to drop, and what <c>LLM preserve thinking</c>
    /// would otherwise carry to the window's end; not counted. Long call arguments are stubbed too (2026-10-17). Returns the results' and the
    /// arguments' counts and one <see cref="PrunedEntry"/> per stub.
    /// <c>/compact</c> prune (<see cref="RunAsync"/>) and the mid-turn guard (<c>Assistant.ShrinkAsync</c>, over <c>Split(messages, 1)</c>:
    /// after a too-long answer, and past the ceiling, 2026-11-10) both use it.
    /// </summary>
    public static (List<ChatMessage> Messages, int Pruned, int Arguments, List<PrunedEntry> Entries) PruneOlderTurns(Plan plan, bool protectSkills)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var entries = new List<PrunedEntry>();
        var messages = new List<ChatMessage>(plan.Older.Count + plan.Recent.Count);
        var (pruned, arguments) = Stub(plan.Older, 0, plan.Older.Count, messages, protectSkills, entries, CallNames(plan.Older), dropThinking: true, userPictures: true);
        messages.AddRange(plan.Recent);
        return (messages, pruned, arguments, entries);
    }

    /// <summary>
    /// The list with the last turn's older tool results stubbed the way <see cref="PruneOlderTurns"/> stubs
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

        var (pruned, _) = StubBeforeLastIteration(messages, start, result, protectSkills, entries, entries is null ? null : CallNames(messages));
        return (result, pruned);
    }

    /// <summary>
    /// Appends <paramref name="messages"/>[<paramref name="from"/>..] to <paramref name="into"/> with
    /// the tool results and carriers before the last iteration stubbed: the last <see cref="ChatRole.Tool"/>
    /// message at or after <paramref name="from"/> and what follows it are copied. Returns the count stubbed.
    /// </summary>
    private static (int Results, int Arguments) StubBeforeLastIteration(IReadOnlyList<ChatMessage> messages, int from, List<ChatMessage> into, bool protectSkills, List<PrunedEntry>? entries = null, IReadOnlyDictionary<string, string>? names = null)
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

        // The last iteration's calls are the ones being answered (2026-10-17): their arguments are never stubbed — nor, with them, the
        // Anthropic API's signed thinking, which rides that message and must go back unedited.
        int lastCalls = int.MaxValue;
        for (int i = messages.Count - 1; i >= from; i--)
        {
            if (messages[i].Role == ChatRole.Assistant && messages[i].Contents.OfType<FunctionCallContent>().Any(c => !Assistant.IsOpeningCallId(c.CallId)))
            {
                lastCalls = i;
                break;
            }
        }

        var pruned = Stub(messages, from, keep, into, protectSkills, entries, names, keepCallsFrom: lastCalls);
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
    /// With <paramref name="userPictures"/> (the older turns' prune, 2026-10-03) a user message's own pictures go too, each
    /// <see cref="PictureBudget.LeftOut"/>, counted; never in the turn in flight, whose pictures the user just gave.
    /// </summary>
    private static (int Results, int Arguments) Stub(IReadOnlyList<ChatMessage> messages, int from, int to, List<ChatMessage> into, bool protectSkills, List<PrunedEntry>? entries, IReadOnlyDictionary<string, string>? names, bool dropThinking = false, bool userPictures = false, int keepCallsFrom = int.MaxValue)
    {
        int pruned = 0;
        int arguments = 0;
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

                string? source = ConversationHistory.CarrierSource(message);
                into.Add(StubbedCarrier(pictures, source));
                pruned += pictures;
                entries?.Add(new PrunedEntry(source ?? Tools.ViewImageTool.ToolName, 0, pictures));
                continue;
            }

            if (userPictures && message.Role == ChatRole.User && message.Contents.Any(c => c is DataContent))
            {
                // An older turn's own pictures (2026-10-03: pasted, /imagine's, the camera's), each its line naming the file, the text kept.
                var kept = message.Clone();
                kept.Contents = message.Contents.Select(c => c is DataContent picture ? PictureBudget.LeftOutPart(picture) : c).ToList();
                int pictures = message.Contents.Count(c => c is DataContent);
                into.Add(kept);
                pruned += pictures;
                entries?.Add(new PrunedEntry(UserPictureEntry, 0, pictures));
                continue;
            }

            if (message.Role == ChatRole.Assistant)
            {
                // One clone for both (2026-10-17): the older turns' thinking dropped, and before the last iteration the calls' long
                // string arguments stubbed as their results are.
                IList<AIContent> contents = message.Contents;
                bool thinking = dropThinking && contents.Any(c => c is TextReasoningContent);
                if (thinking)
                {
                    contents = contents.Where(c => c is not TextReasoningContent).ToList();
                }

                var stubbed = index < keepCallsFrom ? StubArguments(contents, entries, ref arguments) : null;
                if (thinking || stubbed is not null)
                {
                    var kept = message.Clone();
                    kept.Contents = stubbed ?? contents;
                    if (stubbed is not null)
                    {
                        // A raw SDK message would go out as it was, its arguments whole.
                        kept.RawRepresentation = null;
                    }

                    into.Add(kept);
                    continue;
                }
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

        return (pruned, arguments);
    }

    /// <summary>
    /// A carrier with its <paramref name="pictures"/> pictures stubbed by a prune (<see cref="Stub"/>; the cut keeps the file names since
    /// 2026-11-10, <see cref="CutLatestResults"/>): a new user message whose text is <see cref="PrunedImageStub"/>, tagged
    /// <see cref="ConversationHistory.CarrierKey"/> and, with a <paramref name="source"/> (the carrier's, which the caller read once),
    /// <see cref="ConversationHistory.SourceKey"/>, so it stays a carrier for <see cref="ConversationHistory.IsImageCarrier"/>, the picture
    /// budget and later prunes.
    /// </summary>
    private static ChatMessage StubbedCarrier(int pictures, string? source)
    {
        var properties = new AdditionalPropertiesDictionary { [ConversationHistory.CarrierKey] = true };
        if (source is not null)
        {
            properties[ConversationHistory.SourceKey] = source;
        }

        return new ChatMessage(ChatRole.User, PrunedImageStub(pictures, source)) { AdditionalProperties = properties };
    }

    /// <summary>
    /// <paramref name="contents"/> with every call's long string arguments stubbed (2026-10-17, the toki-pona session: two 27 KB
    /// <c>write_file</c> bodies and several 10 KB skill bodies stayed in the history while their results were pruned, and the context
    /// reached 94%, then 100%): a value over <see cref="ArgumentPruneThreshold"/> becomes <see cref="PrunedArgumentStub"/>, the keys kept
    /// so the call still names its path, its action, its skill. The opening calls, a call that failed to parse (no arguments) and
    /// <see cref="KeepsArguments"/>' are left; with <paramref name="plansOnly"/> (<see cref="StubSupersededPlans"/>, 2026-10-18) it is the other
    /// way round, only <see cref="KeepsArguments"/>' stubbed. Null when nothing changed. Each stub is counted and, with <paramref name="entries"/>, logged.
    /// </summary>
    private static List<AIContent>? StubArguments(IList<AIContent> contents, List<PrunedEntry>? entries, ref int count, bool plansOnly = false)
    {
        List<AIContent>? rebuilt = null;
        for (int i = 0; i < contents.Count; i++)
        {
            if (contents[i] is not FunctionCallContent { Arguments: { } arguments } call || Assistant.IsOpeningCallId(call.CallId)
                || KeepsArguments.Contains(call.Name, StringComparer.Ordinal) != plansOnly)
            {
                continue;
            }

            Dictionary<string, object?>? stubbed = null;
            bool recorded = !plansOnly && RecordedArguments.Contains(call.Name, StringComparer.Ordinal);
            foreach (var (key, value) in arguments)
            {
                if ((ArgumentText(value) ?? (recorded ? ArrayText(value) : null)) is { Length: > ArgumentPruneThreshold } text)
                {
                    stubbed ??= new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
                    stubbed[key] = PrunedArgumentStub(text.Length);
                    count++;
                    entries?.Add(new PrunedEntry(call.Name, text.Length, Argument: key));
                }
            }

            if (stubbed is not null)
            {
                rebuilt ??= new List<AIContent>(contents);
                rebuilt[i] = new FunctionCallContent(call.CallId, call.Name, stubbed) { InformationalOnly = call.InformationalOnly };
            }
        }

        return rebuilt;
    }

    /// <summary>
    /// The list with every copy of a plan the latest presentation superseded stubbed (2026-10-18, a gap chain's log: each pass read the
    /// whole plan file and sent it back whole through <c>present_plan</c>, whose arguments a prune never stubs, adding 25–30k tokens a
    /// pass until a request ran out of context). It finds the message holding the latest <c>present_plan</c> call whose <c>markdown</c>
    /// is real text (not a stub) and copies that message and everything after it untouched: the latest presentation, the last
    /// iteration's calls and the Anthropic API's signed thinking riding that message. Before it, every <c>present_plan</c> call's long
    /// string arguments become <see cref="PrunedArgumentStub"/> and every <c>read_file</c> result of a plan file
    /// (<see cref="IsPlanPath"/>) over <see cref="PruneThreshold"/> becomes <see cref="PrunedStub"/>, result by result in a shared tool
    /// message. Opening calls and a call that failed to parse are never touched. A refused presentation with real text still counts as
    /// the latest (its text is the model's newest; the revision it would have replaced is the file on disk): the pass reads calls, never
    /// results. Keyed on any plan, since one plan session runs at a time and an earlier plan's file is on disk. Not run every turn
    /// (rewriting old messages costs the server its prompt cache from the first stub on), only as the first step of a compaction: the
    /// mid-turn guard's and <see cref="RunAsync"/>'s. A copy, with zeros when nothing changed; each stub goes into <paramref name="entries"/>.
    /// </summary>
    public static (List<ChatMessage> Messages, int Pruned, int Arguments) StubSupersededPlans(IReadOnlyList<ChatMessage> messages, List<PrunedEntry>? entries = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int latest = -1;
        for (int i = messages.Count - 1; i >= 0 && latest < 0; i--)
        {
            if (messages[i].Role == ChatRole.Assistant && messages[i].Contents.Any(IsRealPresentation))
            {
                latest = i;
            }
        }

        var result = new List<ChatMessage>(messages.Count);
        if (latest < 0)
        {
            result.AddRange(messages);
            return (result, 0, 0);
        }

        // The read_file calls before the latest presentation that name a plan file, by call id.
        var planReads = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < latest; i++)
        {
            if (messages[i].Role != ChatRole.Assistant)
            {
                continue;
            }

            foreach (var call in messages[i].Contents.OfType<FunctionCallContent>())
            {
                if (IsPlanRead(call))
                {
                    planReads.Add(call.CallId);
                }
            }
        }

        int pruned = 0;
        int arguments = 0;
        for (int index = 0; index < latest; index++)
        {
            var message = messages[index];
            if (message.Role == ChatRole.Assistant && StubArguments(message.Contents, entries, ref arguments, plansOnly: true) is { } stubbed)
            {
                var kept = message.Clone();
                kept.Contents = stubbed;

                // A raw SDK message would go out as it was, its arguments whole.
                kept.RawRepresentation = null;
                result.Add(kept);
                continue;
            }

            if (message.Role == ChatRole.Tool && planReads.Count > 0)
            {
                List<AIContent>? rebuilt = null;
                for (int i = 0; i < message.Contents.Count; i++)
                {
                    if (message.Contents[i] is FunctionResultContent { Result: string text } read && text.Length > PruneThreshold && planReads.Contains(read.CallId))
                    {
                        rebuilt ??= new List<AIContent>(message.Contents);
                        rebuilt[i] = new FunctionResultContent(read.CallId, PrunedStub(text.Length));
                        pruned++;
                        entries?.Add(new PrunedEntry(Tools.ReadFileTool.ToolName, text.Length));
                    }
                }

                if (rebuilt is not null)
                {
                    result.Add(new ChatMessage(ChatRole.Tool, rebuilt));
                    continue;
                }
            }

            result.Add(message);
        }

        for (int index = latest; index < messages.Count; index++)
        {
            result.Add(messages[index]);
        }

        return (result, pruned, arguments);
    }

    /// <summary>
    /// Whether <paramref name="call"/> is a <c>read_file</c> of a plan file (2026-11-09, one test for <see cref="StubSupersededPlans"/> and
    /// <see cref="CutLatestResults"/>): not an opening call, its <c>path</c> passing <see cref="IsPlanPath"/>.
    /// </summary>
    private static bool IsPlanRead(FunctionCallContent call) =>
        call.Name == Tools.ReadFileTool.ToolName && call.Arguments is { } arguments && !Assistant.IsOpeningCallId(call.CallId)
        && arguments.TryGetValue(Tools.FileTool.PathArgument, out object? path) && ArgumentText(path) is { } text && IsPlanPath(text);

    /// <summary>A <c>present_plan</c> call (not an opening one) whose <c>markdown</c> is real text, not missing and not a stub (2026-10-18).</summary>
    private static bool IsRealPresentation(AIContent content) =>
        content is FunctionCallContent { Name: Tools.PresentPlanTool.ToolName, Arguments: { } arguments } call && !Assistant.IsOpeningCallId(call.CallId)
        && arguments.TryGetValue(Tools.PresentPlanTool.MarkdownArgument, out object? markdown) && ArgumentText(markdown) is { } text && !IsPrunedStub(text);

    /// <summary>
    /// Whether <paramref name="path"/> names a plan file (2026-10-18): with <c>\</c> read as <c>/</c> and a leading <c>./</c> dropped, it
    /// starts with <see cref="Plans.PlanSlug.Folder"/> and a slash, or holds it between slashes (an absolute path); any case.
    /// </summary>
    public static bool IsPlanPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string normal = path.Trim().Replace('\\', '/');
        while (normal.StartsWith("./", StringComparison.Ordinal))
        {
            normal = normal[2..];
        }

        return normal.StartsWith(Plans.PlanSlug.Folder + "/", StringComparison.OrdinalIgnoreCase)
            || normal.Contains("/" + Plans.PlanSlug.Folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A <see cref="System.Text.Json.JsonElement"/> array's JSON (2026-10-18, <see cref="RecordedArguments"/>); null for any other shape, never serialized.</summary>
    private static string? ArrayText(object? value) =>
        value is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } element ? element.GetRawText() : null;

    /// <summary>A call argument's text: a string, or a JSON string element; null for any other shape.</summary>
    internal static string? ArgumentText(object? value) => value switch
    {
        string s => s,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
        _ => null,
    };

    /// <summary>
    /// One result <see cref="CutLatestResults"/> cut (2026-11-09): the tool that produced it (<see cref="UnknownTool"/> when no call carries its
    /// id), its length in characters before and after, and the call its id matched in the newest round (2026-11-10, review P4-1/P1-9: the
    /// guard keys "cut before" on it, and the host forgets a cut <c>read_file</c>'s seen stamp), null for <see cref="UnknownTool"/>. Apart from
    /// <see cref="PrunedEntry"/>, which feeds the prune counts and has no after-size.
    /// </summary>
    public sealed record CutEntry(string Tool, int Before, int After, FunctionCallContent? Call = null);

    /// <summary>
    /// What <see cref="CutLatestResults"/> did (2026-11-10, review P4-9): the list, the results cut, the carrier's pictures stubbed, and the
    /// newest round's estimate before and after, as the guard counts it, so the round is found once.
    /// </summary>
    public sealed record CutRound(List<ChatMessage> Messages, IReadOnlyList<CutEntry> Cuts, int PicturesStubbed, long Before, long After);

    /// <summary>
    /// The least a lone result is cut to by <see cref="CutLatestResults"/> (2026-11-09), in characters. Shared by the round since 2026-11-10
    /// (review P21-2): with several results each keeps <c>CutFloorChars / count</c>, never under <see cref="CutLeastChars"/>, so a fan-out of
    /// mid-size results can be cut too.
    /// </summary>
    public const int CutFloorChars = 2_000;

    /// <summary>The least any result is cut to (2026-11-10), in characters; one this long or shorter is never cut.</summary>
    public const int CutLeastChars = 500;

    /// <summary>How far from a cut point <see cref="CutLatestResults"/> looks for a line break to cut on instead (2026-11-09), in characters.</summary>
    private const int CutReach = 200;

    /// <summary>
    /// The newest round's results cut to fit (2026-11-09, a turn whose one round of reads was bigger than the room left: the prune and the
    /// summaries keep the newest round whole, so the request overflowed). The round is the last <see cref="ChatRole.Tool"/> message after
    /// the turn's start (<see cref="ConversationHistory.InFlightStart"/>) and the carrier after it; its calls are those after the previous
    /// tool message (2026-11-10, review P8-5: a reused id can't pick an older call). When its estimate (results at
    /// <see cref="GuardCharsPerToken"/>, plus each carrier picture at <paramref name="pictureTokens"/>' figure, in the carrier's order, else
    /// <see cref="PictureTokens"/>) is over <paramref name="budgetTokens"/>, results are cut largest first, each to its head and tail around
    /// <see cref="TurnText.CutMarker"/> (<see cref="CutText"/>), never below the round's shared floor, until the round fits. A call
    /// <paramref name="cutBefore"/> says was cut earlier in the turn gets <see cref="TurnText.CutAgainMarker"/>. Never cut: an opening call's
    /// result, a loaded skill's while <paramref name="protectSkills"/>, a <c>read_file</c> of a plan file (the model rewrites the plan from it),
    /// an <c>ask_user</c> answer (the user's words, which can't be read back). When every result is at its floor and the round still doesn't
    /// fit, the carrier's pictures are stubbed oldest first, only until the excess is covered (2026-11-10, review P5-2), each becoming
    /// <see cref="PictureBudget.LeftOutPart"/>'s line in a clone of the carrier, its text and tags kept, with
    /// <see cref="TurnText.PicturesCutAgainNote"/> when <paramref name="picturesCutBefore"/>. Only the tool message and the carrier change,
    /// rebuilt (the held ones untouched, no raw representation); the calls' message, with the Anthropic API's signed thinking, goes back as
    /// it was. Pure: the list is copied only once something is cut.
    /// </summary>
    public static CutRound CutLatestResults(IReadOnlyList<ChatMessage> messages, long budgetTokens, bool protectSkills, IReadOnlyList<long>? pictureTokens = null,
        Func<FunctionCallContent, bool>? cutBefore = null, bool picturesCutBefore = false)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var (start, tool, carrier) = LatestRound(messages);
        if (tool < 0)
        {
            return new CutRound([.. messages], [], 0, 0, 0);
        }

        var message = messages[tool];
        long[] sizes = PictureSizes(carrier < 0 ? null : messages[carrier], pictureTokens);
        long before = RoundTokens(message, sizes);
        long over = before - Math.Max(budgetTokens, 0);
        if (over <= 0)
        {
            return new CutRound([.. messages], [], 0, before, before);
        }

        // The calls this round answers, by id: those after the previous tool message, so an id reused earlier in the turn can't match.
        int from = start;
        for (int i = tool - 1; i > start; i--)
        {
            if (messages[i].Role == ChatRole.Tool)
            {
                from = i + 1;
                break;
            }
        }

        var calls = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        for (int i = from; i < tool; i++)
        {
            if (messages[i].Role == ChatRole.Assistant)
            {
                foreach (var call in messages[i].Contents.OfType<FunctionCallContent>())
                {
                    calls.TryAdd(call.CallId, call);
                }
            }
        }

        // The candidates and their lengths, measured once; the floor shared by them (P21-2).
        var candidates = new List<(int Index, string Text)>();
        for (int i = 0; i < message.Contents.Count; i++)
        {
            if (message.Contents[i] is FunctionResultContent { Result: string text } found && text.Length > CutLeastChars
                && !Assistant.IsOpeningCallId(found.CallId) && !(protectSkills && ConversationHistory.IsSkillResult(found))
                && !(calls.TryGetValue(found.CallId, out var call) && (IsPlanRead(call) || call.Name == Tools.AskUserTool.ToolName)))
            {
                candidates.Add((i, text));
            }
        }

        int floor = candidates.Count == 0 ? CutFloorChars : Math.Max(CutLeastChars, CutFloorChars / candidates.Count);
        candidates.RemoveAll(c => c.Text.Length <= floor);
        candidates.Sort((a, b) => b.Text.Length.CompareTo(a.Text.Length));
        long excess = over * GuardCharsPerToken;
        var cuts = new List<CutEntry>();
        List<AIContent>? rebuilt = null;
        foreach (var (i, text) in candidates)
        {
            if (excess <= 0)
            {
                break;
            }

            var original = (FunctionResultContent)message.Contents[i];
            var named = calls.GetValueOrDefault(original.CallId);
            bool again = named is not null && cutBefore?.Invoke(named) == true;
            string cut = CutText(text, (int)Math.Max(floor, text.Length - Math.Min(excess, int.MaxValue)), again);
            if (cut.Length >= text.Length)
            {
                continue;
            }

            excess -= text.Length - cut.Length;
            rebuilt ??= new List<AIContent>(message.Contents);
            rebuilt[i] = new FunctionResultContent(original.CallId, cut) { AdditionalProperties = original.AdditionalProperties };
            cuts.Add(new CutEntry(named?.Name ?? UnknownTool, text.Length, cut.Length, named));
        }

        List<ChatMessage>? result = null;
        long after = before;
        if (rebuilt is not null)
        {
            var kept = message.Clone();
            kept.Contents = rebuilt;

            // A raw SDK message would go out as it was, its results whole.
            kept.RawRepresentation = null;
            result = [.. messages];
            result[tool] = kept;
            after = RoundTokens(kept, sizes);
        }

        // The pictures, oldest first, only until the excess left is covered (P5-2); each keeps its file name (P6-1).
        int stubbed = 0;
        if (excess > 0 && sizes.Length > 0)
        {
            long need = (excess + GuardCharsPerToken - 1) / GuardCharsPerToken;
            var source = messages[carrier];
            var contents = new List<AIContent>(source.Contents.Count + 1);
            int picture = 0;
            foreach (var content in source.Contents)
            {
                if (content is DataContent data && need > 0)
                {
                    contents.Add(PictureBudget.LeftOutPart(data));
                    need -= sizes[picture];
                    after -= sizes[picture];
                    stubbed++;
                }
                else
                {
                    contents.Add(content);
                }

                if (content is DataContent)
                {
                    picture++;
                }
            }

            if (picturesCutBefore)
            {
                contents.Insert(contents.FindIndex(c => c is TextContent) + 1, new TextContent(TurnText.PicturesCutAgainNote));
            }

            var kept = source.Clone();
            kept.Contents = contents;
            kept.RawRepresentation = null;
            result ??= [.. messages];
            result[carrier] = kept;
        }

        return new CutRound(result ?? [.. messages], cuts, stubbed, before, after);
    }

    /// <summary>The newest round's carrier pictures' tokens, in its order: <paramref name="pictureTokens"/> when it names each, else <see cref="PictureTokens"/> each.</summary>
    private static long[] PictureSizes(ChatMessage? carrier, IReadOnlyList<long>? pictureTokens)
    {
        int count = carrier?.Contents.Count(c => c is DataContent) ?? 0;
        var sizes = new long[count];
        for (int i = 0; i < count; i++)
        {
            sizes[i] = pictureTokens is { } known && known.Count == count ? known[i] : PictureTokens;
        }

        return sizes;
    }

    /// <summary>A tool message's results at <see cref="GuardCharsPerToken"/> plus its carrier's <paramref name="pictures"/>: the round as the guard counts it.</summary>
    private static long RoundTokens(ChatMessage tool, long[] pictures)
    {
        long chars = 0;
        foreach (var content in tool.Contents)
        {
            if (content is FunctionResultContent { Result: string text })
            {
                chars += text.Length;
            }
        }

        return chars / GuardCharsPerToken + pictures.Sum();
    }

    /// <summary>
    /// The newest round as <see cref="CutLatestResults"/> reads it (2026-11-09): the turn's start (<see cref="ConversationHistory.InFlightStart"/>),
    /// the last <see cref="ChatRole.Tool"/> message after it (-1 for none: a first request) and the carrier right after that (-1 for none).
    /// </summary>
    private static (int Start, int Tool, int Carrier) LatestRound(IReadOnlyList<ChatMessage> messages)
    {
        int start = ConversationHistory.InFlightStart(messages);
        for (int i = messages.Count - 1; i > start; i--)
        {
            if (messages[i].Role == ChatRole.Tool)
            {
                return (start, i, i + 1 < messages.Count && ConversationHistory.IsImageCarrier(messages[i + 1]) ? i + 1 : -1);
            }
        }

        return (start, -1, -1);
    }

    /// <summary>
    /// The turn in flight lightened after a too-long answer, on a wire whose thinking isn't signed (2026-11-10, review P11-1/P16-1/P22-1):
    /// the thinking of this turn's earlier iterations dropped (on those wires the turn in flight's goes back whatever <c>LLM preserve
    /// thinking</c> says, so it costs real tokens), and the newest round's calls' long arguments stubbed as a prune stubs them
    /// (<see cref="StubArguments"/>: the same stub, which <see cref="IsPrunedStub"/> refuses as a copy and the counts name). A written file
    /// is on disk, so <c>read_file</c> shows it again. The changed messages are clones with no raw representation; the rest is copied as it
    /// is. Returns the list and the arguments stubbed, each in <paramref name="entries"/>.
    /// </summary>
    public static (List<ChatMessage> Messages, int Arguments) LightenTurn(IReadOnlyList<ChatMessage> messages, List<PrunedEntry>? entries = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int start = ConversationHistory.InFlightStart(messages);
        int last = -1;
        for (int i = messages.Count - 1; i > start; i--)
        {
            if (messages[i].Role == ChatRole.Assistant && messages[i].Contents.OfType<FunctionCallContent>().Any(c => !Assistant.IsOpeningCallId(c.CallId)))
            {
                last = i;
                break;
            }
        }

        var result = new List<ChatMessage>(messages);
        int arguments = 0;
        for (int i = start + 1; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role != ChatRole.Assistant)
            {
                continue;
            }

            if (i == last)
            {
                if (StubArguments(message.Contents, entries, ref arguments) is { } stubbed)
                {
                    var kept = message.Clone();
                    kept.Contents = stubbed;
                    kept.RawRepresentation = null;
                    result[i] = kept;
                }
            }
            else if ((last < 0 || i < last) && message.Contents.Any(c => c is TextReasoningContent))
            {
                var kept = message.Clone();
                kept.Contents = message.Contents.Where(c => c is not TextReasoningContent).ToList();
                kept.RawRepresentation = null;
                result[i] = kept;
            }
        }

        return (result, arguments);
    }

    /// <summary>
    /// <paramref name="text"/> cut to about <paramref name="target"/> characters (2026-11-09): its head (two thirds) and tail around
    /// <see cref="TurnText.CutMarker"/> (<see cref="TurnText.CutAgainMarker"/> when <paramref name="again"/>), each end moved to a line break
    /// within <see cref="CutReach"/> when there is one, never inside a surrogate pair (<see cref="Files.TextCut"/>). Since 2026-11-10 (review
    /// P4-7/P4-3/P2-4/P21-1/P23-2): a text cut before has its old marker taken out and its count carried into the new one; a fence the cut
    /// leaves open (<c>⟪ ⟫</c>, <see cref="Tools.UntrustedText"/>, or an odd count of <c>```</c>) is closed before the marker and opened again
    /// after it; a shell spill line in the removed middle (<see cref="Shell.ShellText.CutLine"/>) is kept, right after the marker, so the model
    /// reads the file rather than running the command again. The text itself when cutting would save less than twice the marker's length.
    /// </summary>
    internal static string CutText(string text, int target, bool again = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        string original = text;
        long carried = 0;
        if (OwnCutMark().Match(text) is { Success: true } old
            && long.TryParse(old.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out long count))
        {
            carried = count;
            text = text.Remove(old.Index, old.Length);
        }

        string Marker(long cut) => again ? TurnText.CutAgainMarker(cut) : TurnText.CutMarker(cut);
        int markerLength = Marker(text.Length + carried).Length;
        int room = target - markerLength - 2;
        if (room <= 0 || target >= text.Length || original.Length - target < 2 * markerLength)
        {
            return original;
        }

        int head = room * 2 / 3;
        int tail = text.Length - (room - head);
        int line = head > 0 ? text.LastIndexOf('\n', head - 1, Math.Min(CutReach, head)) : -1;
        if (line >= 0)
        {
            head = line + 1;
        }

        if (tail < text.Length)
        {
            int next = text.IndexOf('\n', tail, Math.Min(CutReach, text.Length - tail));
            if (next >= 0)
            {
                tail = next + 1;
            }
        }

        head = Files.TextCut.SafeLength(text, head);
        tail = Math.Max(head, Files.TextCut.SafeStart(text, tail));
        string kept = text[..head];
        string removed = text[head..tail];
        bool quoteOpen = Opens(kept, Tools.UntrustedText.QuoteOpen, Tools.UntrustedText.QuoteClose);
        bool codeOpen = Occurrences(kept, "```") % 2 == 1;
        bool quoteReopen = Opens(text[..tail], Tools.UntrustedText.QuoteOpen, Tools.UntrustedText.QuoteClose);
        bool codeReopen = Occurrences(text[..tail], "```") % 2 == 1;

        var built = new System.Text.StringBuilder(text.Length - removed.Length + markerLength + 64);
        built.Append(kept);
        if (codeOpen)
        {
            built.Append(kept.Length > 0 && kept[^1] != '\n' ? "\n```" : "```");
        }

        if (quoteOpen)
        {
            built.Append(Tools.UntrustedText.QuoteClose);
        }

        if (built.Length > 0 && built[^1] != '\n')
        {
            built.Append('\n');
        }

        built.Append(Marker(removed.Length + carried)).Append('\n');
        if (Shell.ShellText.SpillLine(removed) is { } spill)
        {
            built.Append(spill).Append('\n');
        }

        if (quoteReopen)
        {
            built.Append(Tools.UntrustedText.QuoteOpen);
        }

        if (codeReopen)
        {
            built.Append("```\n");
        }

        built.Append(text, tail, text.Length - tail);
        return built.ToString();
    }

    /// <summary>Whether <paramref name="text"/> leaves a fence open: more <paramref name="open"/>s than <paramref name="close"/>s.</summary>
    private static bool Opens(string text, string open, string close) => Occurrences(text, open) > Occurrences(text, close);

    private static int Occurrences(string text, string what)
    {
        int count = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The app's own cut marker as <see cref="TurnText.CutMarker"/> and <see cref="TurnText.CutAgainMarker"/> write it, with its line break; the count is group 1.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\[… ([\d,]{1,20}) characters cut here to fit the context window;[^\]\n]{0,300}\]\n?", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex OwnCutMark();

    /// <summary>
    /// Whether <paramref name="text"/> holds a cut result's marker (2026-11-09, <see cref="TurnText.CutMarker"/>): a model that read a cut
    /// file and wrote it back whole would delete the file's middle. Unanchored, since the marker sits mid-text in a long copy, and loose
    /// about what a model may reproduce: <c>…</c> or <c>...</c>, any run of spaces, digits with or without commas, any case. Linear, so a
    /// 200,000-character write argument can't make it run away.
    /// </summary>
    public static bool HoldsCutMark(string? text) => text is not null && CutMarkPattern().IsMatch(text);

    [System.Text.RegularExpressions.GeneratedRegex(@"\[\s*(?:…|\.{3})\s*[\d,]+\s+characters\s+cut\s+here\s+to\s+fit\s+the\s+context\s+window", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex CutMarkPattern();

    /// <summary>
    /// The first top-level argument whose value holds a cut result's marker (<see cref="HoldsCutMark"/>, 2026-11-09), or null. Unlike
    /// <see cref="StubbedArgument"/> it reaches inside lists (a <c>patch_file</c> edit): a string, a JSON string, an encoded JSON array or
    /// object in either (read with <see cref="Tools.LooseJson.TryParse"/>, the parse a tool's list argument accepts), a JSON array or object,
    /// or a sequence of <see cref="System.Text.Json.JsonElement"/>, every string inside walked. Any other type is skipped.
    /// </summary>
    public static string? CutMarkedArgument(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        foreach (var (name, value) in arguments)
        {
            bool marked = value switch
            {
                string text => TextHoldsCutMark(text),
                System.Text.Json.JsonElement element => ElementHoldsCutMark(element),
                IEnumerable<System.Text.Json.JsonElement> list => list.Any(ElementHoldsCutMark),
                _ => false,
            };

            if (marked)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>A string argument holds the marker: as it is, or, when it reads as encoded JSON, in any string inside it.</summary>
    private static bool TextHoldsCutMark(string text)
    {
        if (HoldsCutMark(text))
        {
            return true;
        }

        string trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '[' && trimmed[0] != '{'))
        {
            return false;
        }

        using var document = Tools.LooseJson.TryParse(text);
        return document is not null && ElementHoldsCutMark(document.RootElement);
    }

    /// <summary>Any string in <paramref name="element"/> holds the marker.</summary>
    private static bool ElementHoldsCutMark(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                return TextHoldsCutMark(element.GetString() ?? "");
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ElementHoldsCutMark(item))
                    {
                        return true;
                    }
                }

                return false;
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ElementHoldsCutMark(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>The longest one tool result runs in <see cref="LeanTranscript"/> (2026-09-28); the rest is cut, and <see cref="LeanCut"/> says how much.</summary>
    public const int LeanResultChars = 1500;

    /// <summary>What a picture becomes in <see cref="LeanTranscript"/>. Pinned.</summary>
    public const string LeanPicture = "(a picture, left out)";

    /// <summary>The tail of a result <see cref="LeanTranscript"/> cut: <c>… (4,312 more characters cut)</c>. Pinned.</summary>
    public static string LeanCut(int cut) =>
        "… (" + cut.ToString("N0", CultureInfo.InvariantCulture) + " more characters cut)";

    /// <summary>
    /// <paramref name="messages"/> as a plain dialogue, for the summariser's second try after an empty reply
    /// (2026-09-28, <see cref="Assistant.SummarizeAsync(IReadOnlyList{ChatMessage}, string?, CancellationToken)"/>):
    /// a call becomes the assistant's line <c>[called read_file {"path":"a.txt"}]</c>, a result the user's
    /// <c>[read_file returned] …</c> cut at <paramref name="maxResultChars"/> (<see cref="LeanCut"/>), a picture
    /// <see cref="LeanPicture"/>, thinking dropped, a message left with nothing dropped too; messages of one role in a
    /// row are joined, so the roles alternate as every chat template accepts. No call is left for a tool-primed model
    /// to carry on from, and the request is smaller, so there is room to write. Pure.
    /// </summary>
    public static List<ChatMessage> LeanTranscript(IReadOnlyList<ChatMessage> messages, int maxResultChars = LeanResultChars)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfNegative(maxResultChars);
        var names = CallNames(messages);
        var lean = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            var parts = new List<string>();
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent { Text: { } text } when !string.IsNullOrWhiteSpace(text):
                        parts.Add(text);
                        break;
                    case FunctionCallContent call:
                        parts.Add("[called " + call.Name + " " + Assistant.SerializeArguments(call.Arguments) + "]");
                        break;
                    case FunctionResultContent result:
                        string name = result.CallId is { } id && names.TryGetValue(id, out var found) ? found : UnknownTool;
                        string output = result.Result?.ToString() ?? "";
                        parts.Add("[" + name + " returned] " + (output.Length <= maxResultChars ? output : output[..maxResultChars] + LeanCut(output.Length - maxResultChars)));
                        break;
                    case DataContent document when ConversationHistory.DocumentText(document) is { } pdf:
                        // An attached PDF (2026-10-14, the PDF plan's gap 5): its text, cut, so the summary keeps what it said.
                        parts.Add(pdf.Length <= Pdf.PdfText.LeftOutChars ? pdf : pdf[..Pdf.PdfText.LeftOutChars] + LeanCut(pdf.Length - Pdf.PdfText.LeftOutChars));
                        break;
                    case DataContent:
                        parts.Add(LeanPicture);
                        break;
                }
            }

            if (parts.Count == 0)
            {
                continue;
            }

            var role = message.Role == ChatRole.Tool ? ChatRole.User : message.Role;
            string joined = string.Join("\n", parts);
            if (lean.Count > 0 && lean[^1].Role == role)
            {
                lean[^1] = new ChatMessage(role, lean[^1].Text + "\n\n" + joined);
            }
            else
            {
                lean.Add(new ChatMessage(role, joined));
            }
        }

        return lean;
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

    /// <summary>
    /// The list after a summary: <see cref="SummaryPreamble"/> + <paramref name="summary"/> as one user message, then the opening pairs, then the
    /// recent turns; the opener is not kept (it is in <see cref="Plan.Older"/>, not <see cref="Plan.Opening"/>). The summary keeps the file checkpoints of the turns it stands for (2026-10-28, review P2-06, the user's pick: keep them), an
    /// earlier summary's too, newest first (<see cref="ConversationHistory.CheckpointListKey"/>), so <c>/undo</c> still reaches them.
    /// </summary>
    public static List<ChatMessage> Summarised(string summary, Plan plan)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(plan);
        var head = new ChatMessage(ChatRole.User, SummaryPreamble + summary.Trim());
        var kept = new List<string>();
        for (int i = plan.Older.Count - 1; i >= 0; i--)
        {
            if (ConversationHistory.IsTurnStart(plan.Older[i]) && ConversationHistory.TurnCheckpoint(plan.Older[i]) is { } id)
            {
                kept.Add(id);
            }

            kept.AddRange(ConversationHistory.CheckpointList(plan.Older[i]));
        }

        ConversationHistory.SetCheckpointList(head, kept);
        var messages = new List<ChatMessage>(1 + plan.Opening.Count + plan.Recent.Count) { head };
        messages.AddRange(plan.Opening);
        messages.AddRange(plan.Recent);
        return messages;
    }

    /// <summary>
    /// Whether the next turn should compact first: <paramref name="percent"/> above zero, a known
    /// window, and the last request's context (<see cref="TokenTally.LastRequest"/>) at or past
    /// that share of it. A zeroed last request — nothing sent yet, or just compacted — never fires.
    /// </summary>
    /// <remarks>Since 2026-10-18 the check leaves room for the next reply (<paramref name="largestReply"/>: the largest reply this
    /// conversation has produced, or the plan file's size while planning, <see cref="ReplyReserve"/> capping it): a gap chain's pass
    /// started at 75% of the window, under the 90% share, and its reply, the plan sent back whole, ran out of context. The notice keeps
    /// the plain share.</remarks>
    public static bool ShouldAutoCompact(TokenUsage lastRequest, ContextLength? window, int percent, long largestReply = 0)
    {
        if (percent <= 0 || window is not { Tokens: > 0 } w || lastRequest.Total <= 0)
        {
            return false;
        }

        long share = (long)w.Tokens * percent;
        long reserve = ReplyReserve(largestReply, w.Tokens);
        bool fires = (lastRequest.Total + reserve) * 100L >= share;
        if (fires && reserve > 0 && lastRequest.Total * 100L < share)
        {
            DiagnosticLog.Debug(LogCategory, string.Create(CultureInfo.InvariantCulture,
                $"Auto compact at {lastRequest.Total * 100L / w.Tokens}% of {w.Tokens} tokens, leaving room for a {reserve}-token reply."));
        }

        return fires;
    }

    /// <summary>The <c>--log</c> category of the compactor's own lines (2026-10-18).</summary>
    private const string LogCategory = "Compact";

    /// <summary>
    /// The most of the window the room for the next reply may take (2026-10-18, <see cref="ReplyReserve"/>): a quarter, so one huge reply
    /// (a plan) does not make a small window compact constantly.
    /// </summary>
    public const int MaxReplyReservePercent = 25;

    /// <summary>
    /// The room both compact checks leave for the next reply (2026-10-18): <paramref name="largestReply"/> (the larger of
    /// <see cref="ConversationHistory.LargestReplyTokens"/> and the plan's floor) up to <see cref="MaxReplyReservePercent"/> of the window.
    /// The one place the cap lives. The trade-off: one huge reply lowers the effective share for the rest of the conversation, by at most
    /// that quarter, until <c>/clear</c> or a session load.
    /// </summary>
    public static long ReplyReserve(long largestReply, int windowTokens) =>
        Math.Max(0, Math.Min(largestReply, (long)windowTokens * MaxReplyReservePercent / 100));

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
        int before = history.Messages.Count;

        // The earlier copies of a plan first (2026-10-18): no model call, so the cheap first step; a summary then reads the stubbed older part.
        var supersededEntries = new List<PrunedEntry>();
        var (current, supersededPruned, supersededArguments) = StubSupersededPlans(history.Messages, supersededEntries);
        var plan = Split(current, keepRecent);
        int recentPruned = 0;
        int recentArguments = 0;
        var recentEntries = new List<PrunedEntry>();
        if (pruneRecent && plan.Recent.Count > 0)
        {
            var kept = new List<ChatMessage>(plan.Recent.Count);
            (recentPruned, recentArguments) = StubBeforeLastIteration(plan.Recent, 0, kept, protectSkills, recentEntries, CallNames(plan.Recent));
            if (recentPruned + recentArguments > 0)
            {
                plan = plan with { Recent = kept };
            }
        }

        // What the superseded plans' stubs add to every result: they come first in the message order.
        recentPruned += supersededPruned;
        recentArguments += supersededArguments;
        if (!plan.HasOlderTurns)
        {
            if (recentPruned + recentArguments == 0)
            {
                return null;
            }

            var shrunk = new List<ChatMessage>(plan.Older.Count + plan.Recent.Count);
            shrunk.AddRange(plan.Older);
            shrunk.AddRange(plan.Recent);
            history.Replace(shrunk);
            tally.AddCompaction(null);
            return new Result(before, shrunk.Count, recentPruned, null) { Entries = [.. supersededEntries, .. recentEntries], OpeningKept = plan.Opening.Count, RecentKept = plan.Recent.Count, ArgumentsPruned = recentArguments };
        }

        if (mode == CompactMode.Prune)
        {
            var (older, count, arguments, olderEntries) = PruneOlderTurns(plan, protectSkills);
            count += recentPruned;
            arguments += recentArguments;
            if (count + arguments == 0)
            {
                return null;
            }

            history.Replace(older);
            tally.AddCompaction(null);
            return new Result(before, older.Count, count, null) { Entries = [.. supersededEntries, .. olderEntries, .. recentEntries], OpeningKept = plan.Opening.Count, RecentKept = plan.Recent.Count, ArgumentsPruned = arguments };
        }

        var (summary, usage) = await assistant.SummarizeAsync(plan.Older, focus, cancellationToken).ConfigureAwait(false);
        var messages = Summarised(summary, plan);
        history.Replace(messages);
        tally.AddCompaction(usage);
        return new Result(before, messages.Count, recentPruned, usage, Summarised: true) { Summary = summary.Trim(), Entries = [.. supersededEntries, .. recentEntries], OpeningKept = plan.Opening.Count, RecentKept = plan.Recent.Count, ArgumentsPruned = recentArguments };
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
