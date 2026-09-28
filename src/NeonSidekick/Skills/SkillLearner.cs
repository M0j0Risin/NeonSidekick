using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;

namespace NeonSidekick.Skills;

/// <summary>What a reflection ended in.</summary>
public enum SkillLearnOutcome
{
    /// <summary>A skill was created or updated (<see cref="SkillLearnResult.Edit"/>).</summary>
    Learned,

    /// <summary>The model found nothing reusable and said so.</summary>
    Nothing,

    /// <summary>The model was still calling tools at the request cap (<c>Reflection max requests</c>, <see cref="SkillLearner.DefaultMaxRequests"/> out of the box).</summary>
    Exhausted,

    /// <summary>The server or the transport failed; <see cref="SkillLearnResult.Detail"/> explains.</summary>
    Failed,

    /// <summary>Cancelled: a reconnect, a profile switch, the exit.</summary>
    Cancelled,
}

/// <summary>The outcome, the edit when there was one, a detail for the log or the notice, and what the requests cost.</summary>
public sealed record SkillLearnResult(SkillLearnOutcome Outcome, SkillEditResult? Edit, string Detail, TokenUsage Usage, int Requests);

/// <summary>
/// What a reflection reads (2026-09-19): the last turns of the conversation on screen
/// (<see cref="Turn"/>, the shape since 2026-09-17) or several stored sessions
/// (<see cref="Sessions"/>, a <c>/learn sessions</c> pass). Built on the turn task, pure data.
/// </summary>
public abstract record ReflectionMaterial
{
    private ReflectionMaterial()
    {
    }

    /// <summary>
    /// The <c>Reflection window</c>'s messages (a snapshot; <see cref="SkillLearner.Transcript"/> cleans
    /// them), the note a <c>/learn</c> gave, and — under <c>Reflection includes sessions</c> — the
    /// earlier sessions found for the turn: the query words joined, and the search's result text as
    /// <c>session_manager</c> would answer it, seeded as a call and its result after the transcript.
    /// </summary>
    public sealed record Turn(IReadOnlyList<ChatMessage> Messages, string? Focus, string? SeededQuery = null, string? SeededResult = null) : ReflectionMaterial;

    /// <summary>The stored sessions a pass reads, each as one user message (<see cref="SkillLearner.MaxSessionChars"/>), and the text that found them (null for the newest N).</summary>
    public sealed record Sessions(IReadOnlyList<SessionRecord> Records, string? Query) : ReflectionMaterial;
}

/// <summary>
/// The session store's side of a reflection (2026-09-19): the store to read each skill's usage
/// from and to hand <c>session_manager</c>, the settings the tool reads, the session on screen
/// (left out of the tool's answers), and the clock whose zone the moments show in. Null = the
/// reflection reads the conversation alone (the switch off, <c>Session logging</c> off, headless).
/// </summary>
public sealed record SessionEvidence(SessionStore Store, Func<AppSettingsData> Effective, long? Current, TimeProvider Time);

/// <summary>
/// Self-learning skills (<c>Reflection (auto-learn)</c>, 2026-09-17; <c>Skills auto learn</c> until later that day): after a turn that was work — an
/// orchestration of <see cref="DefaultMinToolCalls"/> (the setting <c>Reflection min tool calls</c>) or more tool calls, or one that hit an error and
/// recovered (<see cref="ShouldLearn"/>) — or on <c>/learn</c>, the model is asked in a side
/// request to distil what is reusable into a skill: an improvement to the one that covers the
/// topic, or a new one under the profile. The reflection is its own short tool loop over a copy
/// of the turn (<see cref="Transcript"/>: pictures dropped, tool results cut) with the catalog in
/// its system prompt and two tools — <c>load_skill</c>, so an update reads the body before
/// rewriting it, and <c>skill_editor</c>, whose rules (<see cref="SkillEditor.Find"/>) stop a copy
/// under another scope. One write per reflection: the loop ends at the first successful
/// <c>skill_editor</c> result. The turn's history is never touched (a snapshot goes in), and the
/// roots are fixed at the start, so a profile switch mid-run still writes the old profile. Runs
/// in the background under <c>LlmSession</c>; never writes to the console — the host prints the
/// notice for the result.
/// </summary>
public static class SkillLearner
{
    /// <summary>The model's own tool calls a task needs to be worth a reflection, out of the box (<c>Reflection min tool calls</c>, <see cref="ReflectionMinToolCalls"/>).</summary>
    public const int DefaultMinToolCalls = ReflectionMinToolCalls.Default;

    /// <summary>Requests one reflection may make, out of the box: a load or two, then the write (<c>Reflection max requests</c>, <see cref="ReflectionMaxRequests"/>).</summary>
    public const int DefaultMaxRequests = ReflectionMaxRequests.Default;

    /// <summary>The most characters of one tool result of the last turn the reflection's copy keeps.</summary>
    public const int MaxResultChars = 3_000;

    /// <summary>The most characters of one tool result of a lead-up turn (the <c>Reflection window</c>'s earlier turns) the copy keeps: context, not evidence.</summary>
    public const int MaxLeadUpResultChars = 300;

    /// <summary>The most characters one stored session's turns take in a <c>/learn sessions</c> pass (<see cref="SessionText.Read(SessionRecord, int, int, TimeZoneInfo, int)"/>); the tool's <c>read</c> fetches the rest.</summary>
    public const int MaxSessionChars = 6_000;

    /// <summary>The id of the seeded <c>session_manager</c> search pair — nine characters, the opening calls' shape; local to the reflection's request, never in the history. Pinned.</summary>
    public const string SessionsCallId = "neonsessn";

    /// <summary>What closes a cut result in the copy. Pinned.</summary>
    public const string CutMark = "[… cut for the reflection]";

    /// <summary>The one-word answer that means no skill. Pinned.</summary>
    public const string NothingWord = "nothing";

    /// <summary>The reflection's system prompt (the catalog follows it): <see cref="TurnOpening"/> + <see cref="InstructionBody"/>. Pinned.</summary>
    public const string Instruction = TurnOpening + InstructionBody;

    /// <summary>The first sentence of a turn reflection's instruction. Pinned.</summary>
    public const string TurnOpening =
        "You are reviewing the last turns of a conversation between a user and Neon, a terminal sidekick with tools, to decide whether they taught a procedure worth keeping for later sessions as a skill. ";

    /// <summary>The first sentence of a <c>/learn sessions</c> pass's instruction (2026-09-19). Pinned.</summary>
    public const string SessionsOpening =
        "You are reviewing several earlier conversations between a user and Neon, a terminal sidekick with tools, to find a procedure the user has needed more than once, or a pitfall met more than once, worth keeping for later sessions as a skill. ";

    /// <summary>The pass's system prompt (the catalog follows it): <see cref="SessionsOpening"/> + <see cref="InstructionBody"/>. Pinned.</summary>
    public const string SessionsPassInstruction = SessionsOpening + InstructionBody;

    /// <summary>
    /// Appended to <see cref="Instruction"/> when the earlier sessions found for the turn are seeded
    /// (<c>Reflection includes sessions</c>, 2026-09-19): what the evidence is for, and the caution
    /// against a one-off. Pinned.
    /// </summary>
    public const string SessionsInstruction =
        "The earlier sessions found for this turn close the transcript, as a session_manager search: a procedure this user has needed in more than one session is worth a skill even when this turn was short, " +
        "and a pitfall met in an earlier session belongs in the skill's body; call session_manager with action read when a hit needs its detail. " +
        "A task seen once here that no earlier session shares is not worth a skill unless this turn taught it fully. " +
        "A skill's usage line says how the stored sessions used it: a skill loaded often and still followed by errors needs its steps fixed, and one a reflection wrote a moment ago needs a real reason to be rewritten.";

    /// <summary>The rest of the instruction, shared by the turn and the pass. Pinned.</summary>
    public const string InstructionBody =
        "A skill is a folder of instructions a future session loads when a task matches its description: a lowercase hyphenated name (at most 64 characters), " +
        "a description that says what it does and when to use it (at most 1,024 characters, with the words such a task would contain), " +
        "and a short Markdown body — when to use it, the steps in order, the pitfalls met and how they were avoided. " +
        "Write for a future session with no memory of this one: general steps, not this turn's story; no one-off facts, no paths or names specific to this machine unless the procedure needs them. " +
        "The last turn is the one to learn from; the turns before it are its lead-up with their tool results shortened — a mistake made there and put right later, or a correction the user gave, is a pitfall worth keeping. " +
        "Prefer improving a skill that already covers the topic over adding another: read it with load_skill first, then call skill_editor with action update and the whole improved body, keeping what still holds. " +
        "Create a new skill (action create, scope profile) only when no listed skill covers the topic. " +
        "Call skill_editor at most once, with a summary: one or two sentences saying what you changed and why, for the user to read. A turn that was a simple question, a chat, or a task any session could do without notes teaches nothing: then answer with the single word " + NothingWord + " and no tool call.";

    /// <summary>
    /// Appended to the instruction when the reflection may write a skill's supporting files
    /// (<c>Reflection edit supporting files</c>, 2026-09-27): what the file actions are for. Pinned.
    /// </summary>
    public const string SupportingFilesInstruction =
        "A skill may keep supporting files beside its instructions (data, examples, a script; load_skill lists them): when the turns corrected or extended one, keep it current with skill_editor action edit_file (old_text to new_text) or write_file (the whole file), a path relative to the skill folder. " +
        "That call counts as your one skill_editor call; never write the SKILL.md that way.";

    /// <summary>The catalog's stand-in when no skill is installed. Pinned.</summary>
    public const string NoSkillsLine = "No skills are installed yet.";

    /// <summary>The closing user message: the ask, and the user's own focus when <c>/learn</c> gave one. Pinned.</summary>
    public static string Request(string? focus)
    {
        const string line = "Reflect on the turns above. Update or create a skill if they taught a reusable procedure; otherwise answer " + NothingWord + ".";
        return string.IsNullOrWhiteSpace(focus) ? line : line + " The user asked to keep: " + focus.Trim();
    }

    /// <summary>The closing user message of a <c>/learn sessions</c> pass: the ask, and the text that found the sessions when there was one. Pinned.</summary>
    public static string SessionsRequest(string? query)
    {
        const string line = "Reflect on the sessions above. Find the procedure that recurs or the pitfall met more than once; update or create ONE skill for it, or answer " + NothingWord + ".";
        return string.IsNullOrWhiteSpace(query) ? line : line + " The sessions were found by searching for: " + query.Trim();
    }

    /// <summary>The trigger: at least <paramref name="minCalls"/> of the model's own calls, or an error the turns got past — and never when a turn already wrote a skill itself.</summary>
    public static bool ShouldLearn(TurnTrace trace, int minCalls)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentOutOfRangeException.ThrowIfLessThan(minCalls, 1);
        return !trace.WroteSkill && (trace.ToolCalls >= minCalls || (trace.Errors > 0 && trace.Recovered));
    }

    /// <summary>
    /// The window as the reflection reads it: image carriers dropped, every <see cref="DataContent"/>
    /// dropped from the messages that carry one; the last turn (from the last
    /// <see cref="ConversationHistory.IsTurnStart"/> message) with each tool result cut at
    /// <see cref="MaxResultChars"/>, the turns before it — the lead-up — with theirs cut at
    /// <see cref="MaxLeadUpResultChars"/>, both with <see cref="CutMark"/>; the user's words, the
    /// replies and the calls whole throughout (a <c>write_file</c> body is the procedure itself).
    /// New message objects throughout — the history's are never edited.
    /// </summary>
    public static List<ChatMessage> Transcript(IReadOnlyList<ChatMessage> turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        int lastStart = 0;
        for (int i = turn.Count - 1; i >= 0; i--)
        {
            if (ConversationHistory.IsTurnStart(turn[i]))
            {
                lastStart = i;
                break;
            }
        }

        var copy = new List<ChatMessage>(turn.Count);
        for (int index = 0; index < turn.Count; index++)
        {
            var message = turn[index];
            if (ConversationHistory.IsImageCarrier(message))
            {
                continue;
            }

            int cap = index < lastStart ? MaxLeadUpResultChars : MaxResultChars;
            var contents = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case DataContent:
                        break;
                    case FunctionResultContent result when result.Result is string text && text.Length > cap:
                        contents.Add(new FunctionResultContent(result.CallId, text[..cap] + "\n" + CutMark));
                        break;
                    default:
                        contents.Add(content);
                        break;
                }
            }

            if (contents.Count > 0)
            {
                copy.Add(new ChatMessage(message.Role, contents));
            }
        }

        return copy;
    }

    /// <summary>The whole request of a turn reflection with no session evidence: the instruction and the catalog as the system message, the cleaned turn, the closing ask.</summary>
    public static List<ChatMessage> Build(IReadOnlyList<ChatMessage> turn, IReadOnlyList<Skill> catalog, string? focus) =>
        Build(new ReflectionMaterial.Turn(turn, focus), catalog, null, TimeZoneInfo.Utc);

    /// <summary>
    /// The whole request. A <see cref="ReflectionMaterial.Turn"/>: the instruction (+ <see cref="SessionsInstruction"/>
    /// with a seeded search) and the catalog (with <paramref name="usage"/>'s lines) as the system
    /// message, the cleaned turn, then — with a seeded search — an assistant <c>session_manager</c>
    /// call (<see cref="SessionsCallId"/>, <c>action</c> search, <c>query</c> the words) and its
    /// result as a tool message, then the closing ask. A <see cref="ReflectionMaterial.Sessions"/>:
    /// <see cref="SessionsPassInstruction"/> and the catalog, one user message per session
    /// (<see cref="SessionText.Read(SessionRecord, int, int, TimeZoneInfo, int)"/> at <see cref="MaxSessionChars"/>),
    /// then <see cref="SessionsRequest"/>.
    /// </summary>
    /// <param name="files">Whether the reflection may write supporting files: <see cref="SupportingFilesInstruction"/> joins the instruction (2026-09-27).</param>
    public static List<ChatMessage> Build(ReflectionMaterial material, IReadOnlyList<Skill> catalog, IReadOnlyDictionary<string, string>? usage, TimeZoneInfo zone, bool files = false)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(zone);
        string catalogBlock = (files ? SupportingFilesInstruction + "\n\n" : "") + (catalog.Count == 0 ? NoSkillsLine : SkillsPrompt.Catalog(catalog, usage));
        switch (material)
        {
            case ReflectionMaterial.Turn turn:
            {
                bool seeded = turn.SeededQuery is not null && turn.SeededResult is not null;
                var transcript = Transcript(turn.Messages);
                var request = new List<ChatMessage>(transcript.Count + 4)
                {
                    new(ChatRole.System, Instruction + (seeded ? "\n\n" + SessionsInstruction : "") + "\n\n" + catalogBlock),
                };
                request.AddRange(transcript);
                if (seeded)
                {
                    request.Add(new ChatMessage(ChatRole.Assistant, [SeededCall(turn.SeededQuery!)]));
                    request.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(SessionsCallId, turn.SeededResult!)]));
                }

                request.Add(new ChatMessage(ChatRole.User, Request(turn.Focus)));
                return request;
            }

            case ReflectionMaterial.Sessions sessions:
            {
                var request = new List<ChatMessage>(sessions.Records.Count + 2)
                {
                    new(ChatRole.System, SessionsPassInstruction + "\n\n" + catalogBlock),
                };
                foreach (var record in sessions.Records)
                {
                    request.Add(new ChatMessage(ChatRole.User, SessionText.Read(record, 1, 0, zone, MaxSessionChars)));
                }

                request.Add(new ChatMessage(ChatRole.User, SessionsRequest(sessions.Query)));
                return request;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(material));
        }
    }

    /// <summary>The seeded <c>session_manager</c> search call: the arguments as detached <see cref="JsonElement"/>s, the one wire shape the AOT round trip knows.</summary>
    public static FunctionCallContent SeededCall(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var action = JsonDocument.Parse("\"" + SessionManagerTool.SearchAction + "\"");
        using var text = JsonDocument.Parse("\"" + JsonEncodedText.Encode(query).ToString() + "\"");
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SessionManagerTool.ActionArgument] = action.RootElement.Clone(),
            [SessionManagerTool.QueryArgument] = text.RootElement.Clone(),
        };
        return new FunctionCallContent(SessionsCallId, SessionManagerTool.ToolName, arguments);
    }

    /// <summary>
    /// The earlier sessions found for a turn (the turn task, before the job starts): the user line's
    /// <see cref="SessionText.SearchWords"/> as an OR search over the store with the session on screen
    /// left out, the result as <c>session_manager</c> would answer it. Null query and result when the
    /// line yields no word; <see cref="SessionText.NoHits"/> when nothing matched.
    /// </summary>
    public static (string? Query, string? Result) Evidence(SessionEvidence sessions, string userLine)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(userLine);
        var words = SessionText.SearchWords(userLine);
        if (words.Count == 0)
        {
            return (null, null);
        }

        string query = string.Join(' ', words);
        var hits = sessions.Store.SearchAny(words, SessionManagerTool.DefaultCount(sessions.Effective()), sessions.Current);
        return (query, SessionText.SearchResults(query, hits, sessions.Time.LocalTimeZone));
    }

    /// <summary>Each catalog skill's <see cref="SkillText.UsageLine"/> from the store, keyed by name; a skill with no facts is left out.</summary>
    public static Dictionary<string, string> UsageLines(SessionEvidence sessions, IReadOnlyList<Skill> catalog)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(catalog);
        var zone = sessions.Time.LocalTimeZone;
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var skill in catalog)
        {
            var usage = sessions.Store.SkillUsageOf(skill.Name);
            var mark = sessions.Store.LastReflectionOf(skill.Name);
            int writes = mark is null ? 0 : sessions.Store.ReflectionWrites(skill.Name);
            if (usage is not null || mark is not null)
            {
                lines[skill.Name] = SkillText.UsageLine(usage, mark, writes, zone);
            }
        }

        return lines;
    }

    /// <summary>The reflection over the last turns with no session evidence — the shape since 2026-09-17; see <see cref="RunAsync(Assistant, ReflectionMaterial, SkillRoots, bool, ReasoningEffort, CancellationToken, int, SessionEvidence?)"/>.</summary>
    public static Task<SkillLearnResult> RunAsync(Assistant assistant, IReadOnlyList<ChatMessage> turn, SkillRoots roots, bool external, string? focus, ReasoningEffort effort, CancellationToken cancellationToken, int maxRequests = DefaultMaxRequests) =>
        RunAsync(assistant, new ReflectionMaterial.Turn(turn, focus), roots, external, effort, cancellationToken, maxRequests, null);

    /// <summary>
    /// The reflection over <paramref name="material"/>: its own catalog over <paramref name="roots"/>
    /// (scanned once), <c>load_skill</c> when that holds a skill, <c>skill_editor</c> always,
    /// <c>session_manager</c> with <paramref name="sessions"/> (the catalog then carries each
    /// skill's usage), then at most <paramref name="maxRequests"/> requests through
    /// <see cref="Assistant.RequestAsync"/> (<see cref="DefaultMaxRequests"/> unless the host passes the setting).
    /// Never throws: cancellation is <see cref="SkillLearnOutcome.Cancelled"/>, anything else
    /// <see cref="SkillLearnOutcome.Failed"/> with <see cref="Assistant.Explain"/>'s sentence.
    /// <paramref name="files"/> (2026-09-27, <c>Reflection edit supporting files</c> on): the editor offers
    /// <c>write_file</c> / <c>edit_file</c> too, the instruction says so, and a file written ends the pass
    /// as a SKILL.md write does; null keeps the reflection to the SKILL.md.
    /// </summary>
    public static async Task<SkillLearnResult> RunAsync(Assistant assistant, ReflectionMaterial material, SkillRoots roots, bool external, ReasoningEffort effort, CancellationToken cancellationToken, int maxRequests, SessionEvidence? sessions, SkillFileAccess? files = null)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRequests, 1);

        var catalog = new SkillCatalog(() => roots);
        catalog.Scan(external);
        var editor = new SkillEditorTool(() => roots, () => external, files);
        var tools = new List<AIFunction>(3);
        if (catalog.Skills.Count > 0)
        {
            tools.Add(new LoadSkillTool(catalog));
        }

        tools.Add(editor);
        if (sessions is not null)
        {
            tools.Add(new SessionManagerTool(sessions.Store, sessions.Effective, () => sessions.Current, sessions.Time));
        }

        var usage0 = sessions is null ? null : UsageLines(sessions, catalog.Skills);
        var messages = Build(material, catalog.Skills, usage0, sessions?.Time.LocalTimeZone ?? TimeZoneInfo.Utc, files is not null);
        var usage = TokenUsage.Zero;
        int requests = 0;
        try
        {
            for (int iteration = 1; iteration <= maxRequests; iteration++)
            {
                var response = await assistant.RequestAsync(messages, tools, effort, cancellationToken).ConfigureAwait(false);
                requests++;
                if (response.Usage is { } reported)
                {
                    usage += reported;
                }

                messages.AddRange(response.Messages);
                if (response.Calls.Count == 0)
                {
                    string said = FirstLine(response.Text);
                    DiagnosticLog.Info(SkillCatalog.Category, "Reflection: nothing to keep" + (said.Length > 0 ? " (" + said + ")" : "") + ".");
                    return new SkillLearnResult(SkillLearnOutcome.Nothing, null, said, usage, requests);
                }

                var results = new List<FunctionResultContent>(response.Calls.Count);
                SkillEditResult? edit = null;
                foreach (var call in response.Calls)
                {
                    DiagnosticLog.Debug(SkillCatalog.Category, "Reflection " + Assistant.ToolCallLogLine(call.Name, Assistant.SerializeArguments(call.Arguments)));
                    // The result's line is the invocation's own (Assistant.ToolResultLogLine, 2026-09-19).
                    var (text, _) = await Assistant.InvokeToolAsync(tools, call, cancellationToken).ConfigureAwait(false);
                    results.Add(Assistant.ResultContent(call, text));
                    if (string.Equals(call.Name, SkillEditorTool.ToolName, StringComparison.Ordinal)
                        && editor.LastResult is { Outcome: SkillEditOutcome.Created or SkillEditOutcome.Updated or SkillEditOutcome.FileWritten or SkillEditOutcome.FileEdited } written)
                    {
                        edit ??= written;
                    }
                }

                messages.Add(new ChatMessage(ChatRole.Tool, results.Cast<AIContent>().ToList()));
                if (edit is not null)
                {
                    DiagnosticLog.Info(SkillCatalog.Category, "Reflection: " + SkillText.Edited(edit) + (edit.Summary.Length > 0 ? " — " + edit.Summary : ""));
                    return new SkillLearnResult(SkillLearnOutcome.Learned, edit, SkillText.Edited(edit), usage, requests);
                }
            }

            DiagnosticLog.Info(SkillCatalog.Category, $"Reflection: still calling tools after {maxRequests.ToString(CultureInfo.InvariantCulture)} requests; nothing written.");
            return new SkillLearnResult(SkillLearnOutcome.Exhausted, null, "", usage, requests);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Info(SkillCatalog.Category, "Reflection cancelled.");
            return new SkillLearnResult(SkillLearnOutcome.Cancelled, null, "", usage, requests);
        }
        catch (Exception ex)
        {
            string explained = assistant.ExplainFailure(ex);
            DiagnosticLog.Info(SkillCatalog.Category, "Reflection failed: " + explained);
            return new SkillLearnResult(SkillLearnOutcome.Failed, null, explained, usage, requests);
        }
    }

    /// <summary>The first non-blank line of a reply, trimmed, for a log line or a detail.</summary>
    internal static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return "";
    }
}
