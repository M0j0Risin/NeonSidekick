using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Claude;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>claude_advisor(question, context?)</c> (2026-09-27, the user's ask): the local model asks Claude Code for advice on
/// its own when it is stuck — the <c>/claude</c> machinery (<see cref="IClaudeCli"/>, one <c>claude -p</c> child per call)
/// turned into a tool, always <see cref="ClaudePermissionLevel.ReadOnly"/> whatever <c>Claude command permissions</c> says:
/// an advisor reads, it never acts. The answer is the tool's result. The decisions, the user's:
/// <list type="bullet">
/// <item>its own Claude thread (<see cref="ClaudeAdvisorThread"/>), apart from <c>/claude</c>'s, resumed call to call and
/// dropped with the session; a lost resume is tried once anew, as <c>/claude</c> does;</item>
/// <item>what Claude sees is <c>Claude advisor context</c>: the model's brief alone, or with the last messages;</item>
/// <item>at most <c>Claude advisor calls per turn</c> calls a turn (<see cref="BeginTurn"/> resets the count), past it an
/// error the model reads as "carry on alone";</item>
/// <item><c>Claude advisor model</c> / <c>effort</c>, blank for <c>/claude</c>'s own;</item>
/// <item>with <c>Claude advisor confirm</c> on (off by default) each call waits for the user's yes; nothing to ask (headless)
/// is a no.</item>
/// </list>
/// The cost joins <c>/usage</c>'s Claude row through <paramref name="spent"/>. A failure is an <c>Error:</c> sentence, never a
/// throw; a cancelled turn's token kills the child (<see cref="ClaudeProcess"/>) and the cancellation goes on up.
/// </summary>
public sealed class ClaudeAdvisorTool : AIFunction
{
    public const string ToolName = "claude_advisor";
    public const string QuestionArgument = "question";
    public const string ContextArgument = "context";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "question": { "type": "string", "description": "{{ClaudeText.AdvisorQuestionDescription}}" },
            "context": { "type": "string", "description": "{{ClaudeText.AdvisorContextDescription}}" }
          },
          "required": ["question"]
        }
        """);

    private readonly IClaudeCli _claude;
    private readonly Func<AppSettingsData> _effective;
    private readonly Func<string> _workingDirectory;
    private readonly ClaudeAdvisorThread _thread;
    private readonly Action<TokenUsage, decimal> _spent;
    private readonly Func<IReadOnlyList<ChatMessage>> _history;
    private readonly Func<string, CancellationToken, Task<bool?>>? _confirm;
    private readonly IClaudeAdvisorView? _view;
    private int _calls;

    /// <param name="claude">The CLI: <see cref="ClaudeProcess"/>, a fake in the tests.</param>
    /// <param name="effective">The settings, read at every call.</param>
    /// <param name="workingDirectory">The folder Claude starts in: the sandbox's root as it is now.</param>
    /// <param name="thread">The advisor's Claude conversation, the screen's (or headless's) to store and drop.</param>
    /// <param name="spent">Where a run's tokens and dollars go (<see cref="TokenTally.AddClaude"/>).</param>
    /// <param name="history">The conversation, for <c>Claude advisor context: recent</c>.</param>
    /// <param name="confirm">Asks the user (the question, the turn's token): true for yes, false for no, null when nothing could ask (no pane); the seam itself null where nothing ever can (headless).</param>
    /// <param name="view">Shows the call as it runs; null shows nothing.</param>
    public ClaudeAdvisorTool(IClaudeCli claude, Func<AppSettingsData> effective, Func<string> workingDirectory, ClaudeAdvisorThread thread, Action<TokenUsage, decimal> spent, Func<IReadOnlyList<ChatMessage>> history, Func<string, CancellationToken, Task<bool?>>? confirm = null, IClaudeAdvisorView? view = null)
    {
        _claude = claude ?? throw new ArgumentNullException(nameof(claude));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        _thread = thread ?? throw new ArgumentNullException(nameof(thread));
        _spent = spent ?? throw new ArgumentNullException(nameof(spent));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _confirm = confirm;
        _view = view;
    }

    public override string Name => ToolName;

    public override string Description => ClaudeText.AdvisorDescription;

    public override JsonElement JsonSchema => Schema;

    /// <summary>A turn starts (<c>PrepareTurn</c>): the per-turn count starts over.</summary>
    public void BeginTurn() => Interlocked.Exchange(ref _calls, 0);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string question = ToolArguments.ReadString(arguments, QuestionArgument).Trim();
        if (question.Length == 0)
        {
            return ClaudeText.AdvisorNoQuestionError;
        }

        var effective = _effective();
        int cap = Math.Clamp(effective.ClaudeAdvisorCallsPerTurn, AppSettingsData.MinClaudeAdvisorCallsPerTurn, AppSettingsData.MaxClaudeAdvisorCallsPerTurn);
        if (Interlocked.Increment(ref _calls) > cap)
        {
            return ClaudeText.AdvisorCapError(cap);
        }

        if (effective.ClaudeAdvisorConfirm)
        {
            bool? yes = _confirm is null ? null : await _confirm(question, cancellationToken).ConfigureAwait(false);
            if (yes is null)
            {
                return ClaudeText.AdvisorNotAskedError;
            }

            if (yes == false)
            {
                return ClaudeText.AdvisorDeclinedError;
            }
        }

        string context = ToolArguments.ReadString(arguments, ContextArgument);
        var recent = ClaudeAdvisorContext.IsRecent(effective.ClaudeAdvisorContext) ? RecentLines(_history()) : null;
        _view?.Began(question);

        bool resume = _thread.SessionId is not null;
        var run = await RunAsync(ClaudeText.AdvisorPrompt(question, context, recent, first: !resume), _thread.SessionId ?? NewId(), resume, effective, cancellationToken).ConfigureAwait(false);
        if (run.ResumeLost)
        {
            // The thread is gone (deleted, another machine's): a new one, with the framing again.
            DiagnosticLog.Info(ClaudeText.Category, ClaudeText.ResumeLostNotice);
            _thread.SessionId = null;
            run = await RunAsync(ClaudeText.AdvisorPrompt(question, context, recent, first: true), NewId(), resume: false, effective, cancellationToken).ConfigureAwait(false);
        }

        return run.Text;
    }

    private static string NewId() => Guid.NewGuid().ToString("D");

    private readonly record struct Run(string Text, bool ResumeLost = false);

    private async Task<Run> RunAsync(string prompt, string sessionId, bool resume, AppSettingsData effective, CancellationToken cancellationToken)
    {
        string? model = !string.IsNullOrWhiteSpace(effective.ClaudeAdvisorModel) ? effective.ClaudeAdvisorModel.Trim()
            : !string.IsNullOrWhiteSpace(effective.ClaudeModel) ? effective.ClaudeModel.Trim() : null;
        string? effort = string.IsNullOrWhiteSpace(effective.ClaudeAdvisorEffort) ? ClaudeEffort.Resolve(effective.ClaudeEffort) : ClaudeEffort.Resolve(effective.ClaudeAdvisorEffort);
        var request = new ClaudeRequest(prompt, sessionId, resume, _workingDirectory(), ClaudePermissionLevel.ReadOnly, effective.ClaudeExecutable, model, effort);
        var answer = new StringBuilder();
        ClaudeEvent.Result? result = null;
        try
        {
            await foreach (var evt in _claude.RunAsync(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case ClaudeEvent.TextDelta delta:
                        answer.Append(delta.Text);
                        break;
                    case ClaudeEvent.ToolActivity tool:
                        _view?.Tool(tool.Name, tool.Detail);
                        break;
                    case ClaudeEvent.Result end:
                        result = end;
                        break;
                }
            }
        }
        catch (ClaudeStartException ex)
        {
            DiagnosticLog.Info(ClaudeText.Category, ex.Message);
            return new Run(ClaudeText.AdvisorFailedError(ex.Message));
        }

        if (result is not null)
        {
            DiagnosticLog.Info(ClaudeText.Category, ClaudeText.ResultLog(result));
            _spent(result.Usage, result.CostUsd);
        }

        if (result is null or { IsError: true })
        {
            string error = result?.Error ?? ClaudeText.UnknownFailure;
            if (resume && answer.Length == 0 && error.Contains("No conversation found", StringComparison.OrdinalIgnoreCase))
            {
                return new Run("", ResumeLost: true);
            }

            return new Run(ClaudeText.AdvisorFailedError(error));
        }

        // The thread is Claude's now: the next call resumes it, whatever id the CLI says it ran under.
        _thread.SessionId = result.SessionId ?? sessionId;
        string text = answer.Length > 0 ? answer.ToString().Trim() : result.Text.Trim();
        text = text.Length > 0 ? text : ClaudeText.AdvisorNoAnswer;
        _view?.Answered(text, result);
        return new Run(text);
    }

    /// <summary>
    /// The conversation's last <see cref="ClaudeText.AdvisorRecentMessages"/> messages as the advisor reads them: one line per
    /// text, call and result — the role and the words, a call as its tool's name, a result cut to
    /// <see cref="ClaudeText.AdvisorToolResultChars"/>. Pictures and empty parts are left out. Pure.
    /// </summary>
    public static IReadOnlyList<string> RecentLines(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var lines = new List<string>();
        foreach (var message in messages.Skip(Math.Max(0, messages.Count - ClaudeText.AdvisorRecentMessages)))
        {
            if (message.Role == ChatRole.System)
            {
                continue;
            }

            string role = message.Role == ChatRole.User ? "user" : message.Role == ChatRole.Assistant ? "assistant" : "tool";
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent { Text: { Length: > 0 } text } when !string.IsNullOrWhiteSpace(text):
                        lines.Add(ClaudeText.AdvisorRecentLine(role, text));
                        break;
                    case FunctionCallContent call:
                        lines.Add(ClaudeText.AdvisorRecentLine("assistant", "(called " + call.Name + ")"));
                        break;
                    case FunctionResultContent result:
                        string body = result.Result?.ToString() ?? "";
                        body = body.Length > ClaudeText.AdvisorToolResultChars ? body[..ClaudeText.AdvisorToolResultChars] + "…" : body;
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            lines.Add(ClaudeText.AdvisorRecentLine("tool", body));
                        }

                        break;
                }
            }
        }

        return lines;
    }
}
