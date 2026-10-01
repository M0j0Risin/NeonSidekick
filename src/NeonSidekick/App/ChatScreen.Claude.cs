using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Claude;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Sessions;

namespace NeonSidekick.App;

// ── /claude (2026-09-27) ──────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The colour of Claude's name over its replies: Claude's own clay, whatever the theme.</summary>
    internal static readonly Spectre.Console.Color ClaudeColor = new(0xD9, 0x77, 0x57);

    /// <summary>Claude Code headless: one <c>claude -p</c> child per <c>/claude</c> (<see cref="ClaudeProcess"/>), a fake in the tests.</summary>
    private readonly IClaudeCli _claude;

    /// <summary>
    /// The Claude conversation this session's <c>/claude</c> resumes: the id minted for its first message, kept once that
    /// message got a result, stored with the session's history and read back by a restore; dropped by <c>/claude new</c>
    /// and with the session itself (<see cref="ForgetSession"/>: <c>/clear</c>, <c>/new</c>, a profile switch).
    /// </summary>
    private string? _claudeSessionId;

    /// <summary>
    /// <c>claude_advisor</c>'s own Claude conversation (2026-09-27, the user's call: apart from <c>/claude</c>'s): stored with the
    /// session's history beside <see cref="_claudeSessionId"/>, read back by a restore, dropped by <see cref="ForgetSession"/>.
    /// </summary>
    private readonly ClaudeAdvisorThread _advisorThread = new();

    /// <summary>
    /// The Claude session the Claude CLI server runs this chat in (2026-09-30): minted at the first turn over the Claude CLI
    /// (<see cref="ClaudeServerConversation"/>), stored with the session's history and read back by a restore, dropped with the
    /// session (<see cref="ForgetSession"/>) — so <c>/clear</c> starts the CLI on a new conversation.
    /// </summary>
    private string? _claudeServerSessionId;

    /// <summary>
    /// The conversation id a turn over the Claude CLI names (<see cref="Assistant.ConversationId"/>), minted at the first;
    /// null for every other server, which is sent the whole history.
    /// </summary>
    private string? ClaudeServerConversation() =>
        ClaudeCliEndpoint.IsClaudeCli(_session.Endpoint?.BaseUrl) ? _claudeServerSessionId ??= NewClaudeSessionId() : null;

    /// <summary>The advisor's group (one tool), built once over <see cref="_claude"/>; offered while <c>Claude advisor tool</c> is on.</summary>
    private readonly IReadOnlyList<AIFunction> _advisorTools;

    /// <summary>
    /// The advisor's group (2026-09-27): <see cref="ClaudeAdvisorTool"/> over the CLI, the settings, the sandbox's root, its thread,
    /// where the cost goes, the conversation (for <c>Claude advisor tool context: recent</c>), the confirm seam and the view. Shared with headless.
    /// </summary>
    public static IReadOnlyList<AIFunction> ClaudeAdvisorTools(IClaudeCli claude, Func<AppSettingsData> effective, Func<string> workingDirectory, ClaudeAdvisorThread thread, Action<Llm.TokenUsage, decimal> spent, Func<IReadOnlyList<ChatMessage>> history, Func<string, CancellationToken, Task<bool?>>? confirm, IClaudeAdvisorView? view) =>
    [
        new ClaudeAdvisorTool(claude, effective, workingDirectory, thread, spent, history, confirm, view),
    ];

    /// <summary>
    /// <c>Claude advisor tool confirm</c>'s question, on the turn task: the yes/no pane through the watcher — the
    /// <see cref="ApproveCommandAsync"/> shape, the cursor on No, ESC a no. Null (never asked) without the pane or a watcher to run it.
    /// </summary>
    private async Task<bool?> ConfirmAdvisorAsync(string question, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        var paneToken = _paneClose?.Token ?? CancellationToken.None;
        bool? yes = null;
        var pending = _keys.RequestPaneAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(paneToken, turnToken);
            try
            {
                yes = await _menu.ConfirmAsync(ClaudeText.AdvisorConfirmQuestion(question), linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !linked.IsCancellationRequested)
            {
                DiagnosticLog.Error(UI.ScreenPane.Category, "The advisor's confirm pane failed: " + Llm.Assistant.Explain(ex), ex);
            }
        });
        try
        {
            await pending.WaitAsync(turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
        {
            // No watcher to run the pane: never asked.
            return null;
        }

        return yes ?? false;
    }

    /// <summary>
    /// The advisor on the transcript as it runs (2026-09-27), on the turn task between the call and its result: the question
    /// as a tool note, each tool Claude uses as a dim <c>Claude ›</c> line, then the answer in Claude's colour under them and the
    /// footer with the cost. The result's own line is left to <see cref="Render"/>, which prints only a refusal or a failure.
    /// </summary>
    private sealed class AdvisorView(ChatScreen screen) : IClaudeAdvisorView
    {
        public void Began(string question) => screen._transcript.ToolNote(ClaudeText.AdvisorQuestionNote(question));

        public void Tool(string name, string detail) => screen._transcript.ToolNote(ClaudeText.ToolNote(name, detail));

        public void Answered(string answer, ClaudeEvent.Result result)
        {
            screen._transcript.ToolAnswer(answer, ClaudeColor);
            screen._transcript.ToolNote(ClaudeText.Footer(result.CostUsd, result.Usage, ClaudeText.AdvisorName));
        }
    }

    /// <summary>
    /// <c>/claude &lt;message&gt;</c>: the message to Claude Code, the reply streamed under Claude's name, spoken when speech
    /// is on, ESC or Ctrl+C killing the child; then the pair into the local model's history, tagged (<see cref="ClaudeText.HistoryUser"/>,
    /// <see cref="ClaudeText.HistoryReply"/>), and into the session store as a turn, so a restore shows it and the next local turn
    /// can build on it. A failure adds nothing. A <c>--resume</c> the CLI refuses (the thread is gone) drops the id and tries once
    /// with a new one. No local model is needed: the history is the session's, connected or not. Refused mid-turn.
    /// </summary>
    private async Task HandleClaudeAsync(string args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _transcript.Error(ClaudeText.UsageError);
            return;
        }

        if (string.Equals(args, ClaudeText.NewWord, StringComparison.OrdinalIgnoreCase))
        {
            _claudeSessionId = null;
            SaveClaudeHistory();
            _transcript.Notice(ClaudeText.NewThreadNotice);
            return;
        }

        var effective = _effective();
        bool resume = _claudeSessionId is not null;
        var run = await RunClaudeAsync(args, _claudeSessionId ?? NewClaudeSessionId(), resume, effective, cancellationToken).ConfigureAwait(false);
        if (run.ResumeLost)
        {
            _claudeSessionId = null;
            _transcript.Notice(ClaudeText.ResumeLostNotice);
            await RunClaudeAsync(args, NewClaudeSessionId(), resume: false, effective, cancellationToken).ConfigureAwait(false);
        }

        DrainDiagnostics();
    }

    private static string NewClaudeSessionId() => Guid.NewGuid().ToString("D");

    /// <summary>How a <c>/claude</c> run ended, for <see cref="HandleClaudeAsync"/>: whether to try again without the resume.</summary>
    private readonly record struct ClaudeRun(bool ResumeLost);

    private async Task<ClaudeRun> RunClaudeAsync(string prompt, string sessionId, bool resume, AppSettingsData effective, CancellationToken cancellationToken)
    {
        using var claudeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        var speaker = effective.TtsOutput && _speech.IsReady ? _speech.BeginTurn(cancellationToken) : null;
        using var stopSpeech = speaker is null ? default : claudeCts.Token.Register(_speech.Stop);
        _queuedClicks.Reset();
        var watcher = _keys.WatchAsync(claudeCts, stop.Token, null, null, LiveLineHook, spend: e => { _queuedClicks.Reset(); return ScrollInput(e); }, onClick: _pane.Enabled ? HintClickLine : null, editor: LiveEditor);
        bool styled = StyledReply(effective.TranscriptMarkdown, _pane.Enabled);
        var level = ClaudePermission.Resolve(effective);
        var request = new ClaudeRequest(prompt, sessionId, resume, _files.Root, level, effective.ClaudeExecutable,
            string.IsNullOrWhiteSpace(effective.ClaudeModel) ? null : effective.ClaudeModel.Trim(), ClaudeEffort.Resolve(effective.ClaudeEffort));
        var reply = new StringBuilder();
        ClaudeEvent.Result? result = null;
        string? startError = null;
        bool cancelled = false;
        bool opened = false;

        // The name and the glyph at the first thing Claude shows: a run that fails first (no CLI, a lost resume) is one error line.
        void Open()
        {
            if (!opened)
            {
                _transcript.Speaker(ClaudeText.SpeakerName, ClaudeColor);
                _transcript.BeginAssistant(styled);
                opened = true;
            }
        }

        using (var busy = _transcript.BeginBusy(ClaudeText.AskingLabel))
        {
            try
            {
                await foreach (var evt in _claude.RunAsync(request, claudeCts.Token).WithCancellation(claudeCts.Token).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case ClaudeEvent.TextDelta delta:
                            Open();
                            _transcript.AppendDelta(delta.Text);
                            speaker?.Feed(delta.Text);
                            reply.Append(delta.Text);
                            busy?.SetLabel(ClaudeText.AskingLabel);
                            break;
                        case ClaudeEvent.ToolActivity tool:
                            Open();
                            _transcript.ToolNote(ClaudeText.ToolNote(tool.Name, tool.Detail));
                            busy?.SetLabel(ClaudeText.ToolLabel(tool.Name));
                            break;
                        case ClaudeEvent.Result end:
                            result = end;
                            break;
                    }

                    DrainDiagnostics();
                }
            }
            catch (OperationCanceledException) when (claudeCts.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (ClaudeStartException ex)
            {
                startError = ex.Message;
            }
            finally
            {
                speaker?.CompleteAdding();
                stop.Cancel();
                await watcher.ConfigureAwait(false);
                if (speaker is not null && claudeCts.IsCancellationRequested)
                {
                    await _speech.StopAsync().ConfigureAwait(false);
                }
            }
        }

        if (startError is not null)
        {
            DiagnosticLog.Info(ClaudeText.Category, startError);
            _transcript.Error(startError);
            return default;
        }

        if (result is { } done)
        {
            DiagnosticLog.Info(ClaudeText.Category, ClaudeText.ResultLog(done));
            _session.Usage.AddClaude(done.Usage, done.CostUsd);
            // A run whose words never streamed (an older CLI, a reply in one piece) still shows them.
            if (!done.IsError && reply.Length == 0 && done.Text.Length > 0)
            {
                Open();
                _transcript.AppendDelta(done.Text);
                reply.Append(done.Text);
            }
        }

        if (opened)
        {
            _transcript.EndAssistant();
        }

        if (cancelled)
        {
            _transcript.Notice(CancelledNotice);
        }
        else if (result is null or { IsError: true })
        {
            string error = result?.Error ?? ClaudeText.UnknownFailure;
            bool lost = resume && reply.Length == 0 && error.Contains("No conversation found", StringComparison.OrdinalIgnoreCase);
            if (lost)
            {
                return new ClaudeRun(ResumeLost: true);
            }

            _transcript.Error(ClaudeText.Failed(error));
            return default;
        }
        else
        {
            if (result.Denied.Count > 0)
            {
                _transcript.Notice(ClaudeText.DeniedNotice(result.Denied, ClaudePermission.Name(level)));
            }

            _transcript.Notice(ClaudeText.Footer(result.CostUsd, result.Usage));
            // The thread is Claude's now: the next /claude resumes it, whatever id the CLI says it ran under.
            _claudeSessionId = result.SessionId ?? sessionId;
        }

        if (reply.Length > 0)
        {
            // The shared history (the user's pick): the local model reads the exchange as one put to someone else.
            _session.History.AddUser(ClaudeText.HistoryUser(prompt));
            _session.History.AddAssistant(ClaudeText.HistoryReply(reply.ToString()));
            string line = SlashLine(prompt);
            _log.Add(line, reply.ToString());
            _lastReply = reply.ToString();
            LogClaudeTurn(line, reply.ToString(), result?.Usage ?? default, cancelled, effective);
        }

        return default;
    }

    /// <summary>The line as the store and <c>/copy</c> keep it: the command and the message, as typed.</summary>
    private static string SlashLine(string prompt) => "/claude " + prompt;

    /// <summary>
    /// A <c>/claude</c> exchange into the store as a turn (<see cref="LogTurn"/>'s shape without a local-model trace): the row
    /// begun at the first one, the tokens Claude's, the history saved with the Claude id. Nothing while <c>Session logging</c> is off.
    /// </summary>
    private void LogClaudeTurn(string line, string reply, Llm.TokenUsage usage, bool cancelled, AppSettingsData effective)
    {
        if (!effective.SessionLogging)
        {
            return;
        }

        bool first = _sessionId is null;
        _sessionId ??= _sessions.Begin(SessionText.FirstLineTitle(line), _session.Endpoint?.ModelId ?? "");
        if (_sessionId is not { } id)
        {
            return;
        }

        if (first)
        {
            RefreshSessionTitle();
        }

        StampTurn(_session.History, _sessions.AppendTurn(id, line, reply, 0, [], [], 0, usage.Input, usage.Output, cancelled));
        SaveClaudeHistory();
    }

    /// <summary>The history as it stands, with the plan and the Claude id, into the current session's row; nothing without a row.</summary>
    private void SaveClaudeHistory()
    {
        if (_sessionId is { } id)
        {
            _sessions.SaveHistory(id, SessionHistory.ToJson(_session.History.Messages, _plan.ToStored(), _executingPlan, _claudeSessionId, _advisorThread.SessionId, _effective().SessionSaveThinking, _claudeServerSessionId));
        }
    }
}
