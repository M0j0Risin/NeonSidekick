using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Advisors;
using NeonSidekick.Claude;
using NeonSidekick.Diagnostics;
using NeonSidekick.Emojis;
using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>neon_advisor(advisor, question, context?)</c> (2026-10-10, the user's ask): the model consults another profile of this home, one
/// of <c>Advisor list</c>, in the role it was given. The decisions, the user's:
/// <list type="bullet">
/// <item>the advisor answers on its own profile's server, model, key, reasoning and sampling (<see cref="IAdvisorHost.LinkAsync"/>,
/// botchat's links), under its own persona (the default one without) and <see cref="AdvisorText.Framing"/>;</item>
/// <item>it may call this chat's read-only tools — the turn's tools that plan mode keeps (<see cref="PlanTools.Allowed"/>), less
/// <see cref="Excluded"/> — up to <c>Advisor tool iterations</c> round trips, its last round asked for words;</item>
/// <item>a thread per advisor (<see cref="AdvisorThreads"/>): its questions and answers replayed, the newest <c>Advisor thread
/// length</c> kept, dropped with the session;</item>
/// <item><c>Advisor context</c>, <c>Advisor calls per turn</c> and <c>Advisor confirm</c> as the Claude Code advisor tool's rows (retired later on 2026-10-10).</item>
/// </list>
/// A failure is an <c>Error:</c> sentence, never a throw; a cancelled turn's token goes on up.
/// </summary>
public sealed class NeonAdvisorTool : AIFunction
{
    public const string ToolName = "neon_advisor";
    public const string AdvisorArgument = "advisor";
    public const string QuestionArgument = "question";
    public const string ContextArgument = "context";

    /// <summary>
    /// The read-only tools an advisor is never offered (2026-10-10): itself (no advice chains; the Claude Code advisor tool too until it was retired), and the
    /// ones that put something to the user — the question pane, the camera's shutter, the screen's (the advisor answers the model,
    /// not the user). Pinned.
    /// </summary>
    public static readonly IReadOnlySet<string> Excluded = new HashSet<string>(StringComparer.Ordinal)
    {
        ToolName, AskUserTool.ToolName, CameraCaptureTool.ToolName, ScreenCaptureTool.ToolName, ScreenListTool.ToolName,
    };

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "advisor": { "type": "string", "description": "{{AdvisorText.AdvisorArgumentDescription}}" },
            "question": { "type": "string", "description": "{{AdvisorText.QuestionDescription}}" },
            "context": { "type": "string", "description": "{{AdvisorText.ContextDescription}}" }
          },
          "required": ["advisor", "question"]
        }
        """);

    private readonly IAdvisorHost _host;
    private readonly Func<AppSettingsData> _effective;
    private readonly Func<string> _master;
    private readonly AdvisorThreads _threads;
    private readonly Func<(IReadOnlyList<AIFunction> Tools, string Rules)> _offered;
    private readonly Func<IReadOnlyList<ChatMessage>> _history;
    private readonly Func<AdvisorEntry, string, CancellationToken, Task<bool?>>? _confirm;
    private readonly IAdvisorView? _view;
    private readonly Action<TokenUsage, int>? _spent;
    private int _calls;

    /// <param name="host">The advisors' profiles and links.</param>
    /// <param name="effective">The settings, read at every call.</param>
    /// <param name="master">The loaded profile's name, for the framing.</param>
    /// <param name="threads">The advisors' threads, the screen's (or headless's) to store and drop.</param>
    /// <param name="offered">The tools this chat would offer a turn now and their rules' text; filtered here to the read-only ones.</param>
    /// <param name="history">The conversation, for <c>Advisor context: recent</c>.</param>
    /// <param name="confirm">Asks the user: true for yes, false for no, null when nothing could ask; the seam itself null where nothing ever can (headless).</param>
    /// <param name="view">Shows the call as it runs; null shows nothing.</param>
    /// <param name="spent">
    /// Told what each answer's requests used, the requests that reported and their tokens summed (2026-10-10, found comparing botchat with
    /// the advisors: neither was counted on <c>/usage</c>); a failed or cancelled answer's too, those requests having been made. Null counts nothing.
    /// </param>
    public NeonAdvisorTool(IAdvisorHost host, Func<AppSettingsData> effective, Func<string> master, AdvisorThreads threads, Func<(IReadOnlyList<AIFunction> Tools, string Rules)> offered, Func<IReadOnlyList<ChatMessage>> history, Func<AdvisorEntry, string, CancellationToken, Task<bool?>>? confirm = null, IAdvisorView? view = null, Action<TokenUsage, int>? spent = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _master = master ?? throw new ArgumentNullException(nameof(master));
        _threads = threads ?? throw new ArgumentNullException(nameof(threads));
        _offered = offered ?? throw new ArgumentNullException(nameof(offered));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _confirm = confirm;
        _view = view;
        _spent = spent;
    }

    public override string Name => ToolName;

    public override string Description => AdvisorText.Description(AdvisorRoster.Entries(_effective().AdvisorList));

    public override JsonElement JsonSchema => Schema;

    /// <summary>The advisors as they are now (2026-10-10): plan mode's directive names them.</summary>
    public IReadOnlyList<AdvisorEntry> Advisors => AdvisorRoster.Entries(_effective().AdvisorList);

    /// <summary>A turn starts (<c>PrepareTurn</c>): the per-turn count starts over.</summary>
    public void BeginTurn() => Interlocked.Exchange(ref _calls, 0);

    /// <summary>
    /// The tools an advisor may call out of <paramref name="tools"/>: plan mode's read-only ones, less <see cref="Excluded"/>; an event-log
    /// tool swapped for its twin that reads every computer redacted (2026-10-15, the event-log plan: a computer's <c>full</c> detail is the
    /// main chat's alone); every tool with a redacted twin since later that day (the admin-tools plan, <see cref="IRedactedTwin"/>). Pure.
    /// </summary>
    public static IReadOnlyList<AIFunction> ReadOnlyOf(IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Where(t => PlanTools.Allowed(t.Name) && !Excluded.Contains(t.Name)).Select(RedactedTwins.Of).ToList();
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string question = ToolArguments.ReadString(arguments, QuestionArgument).Trim();
        if (question.Length == 0)
        {
            return AdvisorText.NoQuestionError;
        }

        var effective = _effective();
        var advisors = AdvisorRoster.Entries(effective.AdvisorList);
        if (advisors.Count == 0)
        {
            return AdvisorText.NoAdvisorsError;
        }

        string words = ToolArguments.ReadString(arguments, AdvisorArgument);
        var (advisor, candidates) = AdvisorRoster.Find(advisors, words);
        if (advisor is null)
        {
            return AdvisorText.UnknownAdvisorError(words, candidates);
        }

        int cap = Math.Clamp(effective.AdvisorCallsPerTurn, AppSettingsData.MinAdvisorCallsPerTurn, AppSettingsData.MaxAdvisorCallsPerTurn);
        if (Interlocked.Increment(ref _calls) > cap)
        {
            return AdvisorText.CapError(cap);
        }

        if (effective.AdvisorConfirm)
        {
            bool? yes = _confirm is null ? null : await _confirm(advisor, question, cancellationToken).ConfigureAwait(false);
            if (yes is null)
            {
                return AdvisorText.NotAskedError;
            }

            if (yes == false)
            {
                return AdvisorText.DeclinedError(advisor.Profile);
            }
        }

        string context = ToolArguments.ReadString(arguments, ContextArgument);
        var recent = AdvisorContextMode.IsRecent(effective.AdvisorContext) ? AdvisorRecent.Lines(_history()) : null;
        return await AskAsync(advisor, question, context, recent, effective, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One question to <paramref name="advisor"/> (the tool's call and <c>/advisor</c>'s): its profile read, its link, its thread replayed
    /// under its persona and the framing, the turn run over the read-only tools, the pair added to its thread. The answer, or an
    /// <c>Error:</c> sentence. <paramref name="thread"/> false (2026-10-18, the Ralph loop's review: the reviewer must not see the advice it gave
    /// during the pass and judge its own work) neither replays the thread nor adds to it.
    /// </summary>
    public async Task<string> AskAsync(AdvisorEntry advisor, string question, string context, IReadOnlyList<string>? recent, AppSettingsData effective, CancellationToken cancellationToken, bool thread = true)
    {
        ArgumentNullException.ThrowIfNull(advisor);
        ArgumentNullException.ThrowIfNull(effective);
        AdvisorProfile? profile;
        try
        {
            profile = _host.Read(advisor.Profile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return AdvisorText.UnreadableProfileError(advisor.Profile, ex.Message);
        }

        if (profile is null)
        {
            return AdvisorText.MissingProfileError(advisor.Profile);
        }

        string avatar = AvatarRules.Normalize(profile.Settings.ModelAvatar);
        _view?.Began(advisor, question, avatar);
        var (link, problem) = await _host.LinkAsync(profile, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            DiagnosticLog.Info(AdvisorText.Category, advisor.Profile + ": " + problem);
            return AdvisorText.FailedError(advisor.Profile, problem ?? "no link");
        }

        int rounds = Math.Clamp(effective.AdvisorMaxToolIterations, 0, AppSettingsData.MaxAdvisorToolIterations);
        var (offered, rules) = rounds > 0 ? _offered() : ([], "");
        var tools = ReadOnlyOf(offered);
        string system = (string.IsNullOrWhiteSpace(profile.Persona) ? Assistant.DefaultPersona : profile.Persona.Trim())
            + "\n\n" + AdvisorText.Framing(_master(), advisor.Role, tools.Count > 0)
            + (tools.Count > 0 && rules.Length > 0 ? "\n\n" + rules : "");
        var history = new ConversationHistory(system);
        if (thread)
        {
            history.Replace(_threads.Messages(advisor.Profile));
        }

        var assistant = link.Create(history);
        assistant.Tools = tools;
        assistant.MaxToolIterations = tools.Count > 0 ? rounds + 1 : 1;
        if (link.Session)
        {
            // The Claude Code server keeps the conversation (2026-10-10): the advisor's thread names it, so its turns go to its
            // link's own process (beside the chat's since 2026-10-13) with the tools offered over MCP. Its last round is not asked
            // without tools: the server's turn carries on as one, so the cap stops the loop instead.
            assistant.ConversationId = thread ? _threads.SessionOf(advisor.Profile) : null;
        }
        else
        {
            assistant.LastRoundAnswers = tools.Count > 0;
        }

        // The advisor's reads are not the main model's (2026-10-11, the review): over the chat's own file tools, its read_file would mark
        // a file seen, and the main model's write_file without a mode then replace one it never read.
        using var seenOff = WorkingDirectory.SeenOff();
        var answer = new StringBuilder();
        var usage = TokenUsage.Zero;
        int requests = 0;
        string? failure = null;
        int calls = 0;
        try
        {
            await foreach (var evt in assistant.RunTurnAsync(AdvisorText.Prompt(question, context, recent), cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case TurnEvent.TextDelta delta:
                        answer.Append(delta.Text);
                        break;
                    case TurnEvent.ToolCall call:
                        calls++;
                        _view?.Tool(advisor, call.Name, call.ArgumentsJson);
                        break;
                    case TurnEvent.Usage used:
                        usage += used.Tokens;   // one per request: summed
                        requests++;
                        break;
                    case TurnEvent.Notice { IsError: true } notice:
                        failure = notice.Text;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = Assistant.Explain(ex);
        }
        finally
        {
            if (requests > 0)
            {
                _spent?.Invoke(usage, requests);
            }
        }

        string text = answer.ToString().Trim();
        if (failure is not null && text.Length == 0)
        {
            // A dead server (a stopped extra, a closed container): the next call links anew.
            _host.Forget(advisor.Profile);
            DiagnosticLog.Info(AdvisorText.Category, advisor.Profile + ": " + failure);
            return AdvisorText.FailedError(advisor.Profile, failure);
        }

        DiagnosticLog.Info(AdvisorText.Category, AdvisorText.ResultLog(advisor.Profile, link.Model, usage, calls));
        text = text.Length > 0 ? text : AdvisorText.NoAnswer(advisor.Profile);
        if (thread)
        {
            _threads.Add(advisor.Profile, new AdvisorPair(question, text), effective.AdvisorThreadPairs);
        }

        _view?.Answered(advisor, text, link.Model, usage, link.Warning, avatar);
        return text;
    }
}
