using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Shell;

namespace NeonSidekick.Claude;

/// <summary>
/// What the Claude CLI server's client needs from the app (2026-09-30): where the CLI is, the folder it runs in, and how a
/// one-shot request runs (<see cref="ClaudeProcess.RunLaunchAsync"/> in the app, a script in the tests).
/// </summary>
/// <param name="Executable">The CLI's full path as <see cref="ClaudeExecutable.Locate"/> finds it now, or null.</param>
/// <param name="WorkingDirectory">The folder the CLI starts in: <c>&lt;home&gt;/claude-cli</c>. It has no file tools, so the folder is only where it keeps its per-folder state.</param>
/// <param name="OneShot">Runs one <c>claude -p</c> child: its launch and its stdin.</param>
public sealed record ClaudeCliContext(
    Func<string?> Executable,
    string WorkingDirectory,
    Func<ProcessLaunch, string, CancellationToken, IAsyncEnumerable<ClaudeEvent>>? OneShot = null);

/// <summary>
/// The chat client over the Claude CLI server (2026-09-30, the user's ask): one long-lived <c>claude</c> process the chat
/// writes its messages to (<see cref="IClaudeServerHost"/>), its own tools all off and the app's offered instead over MCP
/// (<see cref="ClaudeMcpServer"/>).
///
/// <para><b>The turn stays the app's.</b> <see cref="Assistant"/>'s loop runs every tool call, as it does for every other
/// server: when the model calls one of the app's tools, the CLI's stream shows the call and the CLI sends it to the MCP
/// server, where it waits. This client ends the response there with the calls as ordinary
/// <see cref="FunctionCallContent"/>s (the CLI's <c>tool_use</c> ids as the call ids); the loop runs them — the approval
/// pane, <c>ask_user</c>, the transcript — and asks again with the results, which this client hands to the waiting MCP
/// calls (matched by id: the CLI's call carries it, <see cref="ClaudeMcpServer.ToolUseIdMetaKey"/>) and goes on reading
/// the same Claude turn. So one Claude turn is several responses here, each a model request of the CLI's.</para>
///
/// <para><b>The CLI keeps the conversation.</b> A request of the main chat names its Claude session in
/// <see cref="ChatOptions.ConversationId"/> (the screen mints and stores it); of the history it carries, only the part
/// after the model's last message goes to the CLI (<see cref="ClaudeServerInput.Split"/>). A session the CLI has lost
/// ("No conversation found") starts over under the same id, opening with the conversation so far
/// (<see cref="ClaudeServerInput.Preamble"/>). A request with no conversation id — a session title, a skill reflection, a
/// summary, a <c>/botchat</c> bot — is asked beside the chat in a <c>claude -p</c> of its own (<see cref="ClaudeArguments.BuildOneShot"/>),
/// so nothing it says lands in the chat's session.</para>
///
/// <para><b>A cancel interrupts.</b> ESC mid-reply sends the CLI's interrupt and waits a moment for its <c>result</c>; a
/// CLI that does not answer in time is stopped, and the next turn resumes the session on a new one. The host is the app's,
/// not this client's: a reconnect that builds a new client keeps the process.</para>
/// </summary>
public sealed class ClaudeCliChatClient : IChatClient
{
    /// <summary>How long an interrupt waits for the turn's <c>result</c> before the CLI is stopped instead.</summary>
    public static readonly TimeSpan InterruptGrace = TimeSpan.FromSeconds(3);

    private readonly IClaudeServerHost _host;
    private readonly ClaudeCliContext _context;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _calls = new();
    private readonly Dictionary<string, TaskCompletionSource<ClaudeToolAnswer>> _answers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private readonly Func<ClaudeToolCall, CancellationToken, Task<ClaudeToolAnswer>> _onToolCall;
    private Turn? _turn;
    private int _interruptCount;

    // Whether the CLI said it takes an interrupt (its init's capabilities): known from the first turn on, for a cancel that
    // lands before the next turn's init.
    private bool _takesInterrupt;

    /// <param name="endpoint">The Claude CLI endpoint: its model id is the <c>--model</c> word.</param>
    /// <param name="host">The app's CLI process, shared by every client built over it.</param>
    /// <param name="context">Where the CLI is and runs, and how a one-shot request runs.</param>
    /// <param name="requestTimeout">How long one response may take; the CLI is interrupted past it.</param>
    public ClaudeCliChatClient(LlmEndpoint endpoint, IClaudeServerHost host, ClaudeCliContext context, TimeSpan requestTimeout)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _requestTimeout = requestTimeout > TimeSpan.Zero ? requestTimeout : Timeout.InfiniteTimeSpan;
        _onToolCall = OnToolCallAsync;
    }

    public LlmEndpoint Endpoint { get; }

    /// <summary>What the CLI running now has spent, as its last <c>result</c> said (the figure runs for the process, not the turn).</summary>
    private decimal _spent;

    /// <summary>The <c>--effort</c> word for the request's reasoning: none leaves it to the CLI.</summary>
    public static string? EffortOf(ChatOptions? options) => options?.Reasoning?.Effort switch
    {
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.ExtraHigh => "xhigh",
        _ => null,
    };

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (options?.ConversationId is not { Length: > 0 } sessionId)
        {
            await foreach (var update in OneShotAsync(list, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var input = ClaudeServerInput.Split(list);
            var turn = await BeginAsync(sessionId, input, options, cancellationToken).ConfigureAwait(false);
            await foreach (var update in ReadAsync(turn, input, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The request's first step: the results of the calls the turn waits on handed over, or — anything else — a new turn
    /// started (a turn left mid-way, by ESC during a tool or a failure, is interrupted first).
    /// </summary>
    private async Task<Turn> BeginAsync(string sessionId, ClaudeTurnInput input, ChatOptions? options, CancellationToken cancellationToken)
    {
        if (_turn is { } open && open.SessionId == sessionId && open.Pending.Count > 0 && input.Results.Any(r => open.Pending.Contains(r.CallId)))
        {
            Deliver(open, input);
            return open;
        }

        if (_turn is { } stale)
        {
            await AbandonAsync(stale).ConfigureAwait(false);
        }

        string executable = _context.Executable() ?? throw new ClaudeStartException(ClaudeText.NotFound);
        var tools = options?.Tools?.OfType<AIFunction>().ToList() ?? [];
        string prompt = tools.Count > 0 ? input.SystemPrompt + "\n\n" + ClaudeCliText.ToolNamesNote : input.SystemPrompt;
        var launch = new ClaudeServerLaunch(executable, sessionId, ClaudeCliEndpoint.ModelOf(Endpoint.ModelId), EffortOf(options), _context.WorkingDirectory,
            prompt, string.Join(",", tools.Select(t => t.Name)));
        bool resume = _started.Contains(sessionId) || input.HasEarlierTurns;
        var turn = new Turn(sessionId, launch, tools, input, resume);
        await StartAsync(turn, input.NewContent, cancellationToken).ConfigureAwait(false);
        return turn;
    }

    private async Task StartAsync(Turn turn, IReadOnlyList<AIContent> content, CancellationToken cancellationToken)
    {
        // The chat's client takes the MCP calls: claimed per turn, so a /botchat bot's client over the same host (whose
        // requests are all one-shots) never takes them from it.
        _host.OnToolCall = _onToolCall;
        if (await _host.EnsureRunningAsync(turn.Launch, turn.Resume, turn.Tools, cancellationToken).ConfigureAwait(false))
        {
            _spent = 0m;
        }

        _started.Add(turn.SessionId);
        _turn = turn;
        await _host.WriteLineAsync(ClaudeServerInput.UserLine(turn.SessionId, content), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The CLI lost the session (a resume answered "No conversation found": deleted, or another machine's) or already has
    /// the id a fresh start asked for: started again the other way, the new part sent again — a fresh session opening
    /// with the conversation so far.
    /// </summary>
    private async Task<Turn> StartOverAsync(Turn turn, bool resume, CancellationToken cancellationToken)
    {
        DiagnosticLog.Info(ClaudeText.Category, resume ? ClaudeCliText.SessionInUseLog(turn.SessionId) : ClaudeCliText.SessionLostLog(turn.SessionId));
        _host.Stop();
        var again = new Turn(turn.SessionId, turn.Launch, turn.Tools, turn.Input, resume) { StartedOver = true };
        var content = new List<AIContent>();
        if (!resume && ClaudeServerInput.Preamble(turn.Input.Earlier) is { Length: > 0 } preamble)
        {
            content.Add(new TextContent(preamble));
        }

        content.AddRange(turn.Input.NewContent);
        await StartAsync(again, content, cancellationToken).ConfigureAwait(false);
        return again;
    }

    private async IAsyncEnumerable<ChatResponseUpdate> ReadAsync(Turn turn, ClaudeTurnInput input, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_requestTimeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(_requestTimeout);
        }

        string model = turn.Launch.Model;
        var calls = new List<ClaudeServerEvent.ToolUse>();
        long inputTokens = 0, outputTokens = 0;
        bool wrote = false;
        while (true)
        {
            string? line;
            try
            {
                line = await _host.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await AbandonAsync(turn).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(ClaudeCliText.RequestTimedOut(_requestTimeout));
            }

            if (line is null)
            {
                End(turn, ClaudeCliText.CallCancelled);
                throw new InvalidOperationException(ClaudeCliText.EndedError(_host.ExitCode, _host.StderrTail));
            }

            bool startOver = false;
            foreach (var evt in turn.Parser.Read(line))
            {
                switch (evt)
                {
                    case ClaudeServerEvent.Init init:
                        _takesInterrupt = init.Interrupts;
                        if (init.Tools.Any(t => !t.StartsWith(ClaudeArguments.McpToolPrefix, StringComparison.Ordinal)))
                        {
                            DiagnosticLog.Warn(ClaudeText.Category, ClaudeCliText.ForeignToolsLog(init.Tools));
                        }

                        break;
                    case ClaudeServerEvent.MessageStart start:
                        inputTokens = start.InputTokens;
                        outputTokens = 0;
                        calls.Clear();
                        break;
                    case ClaudeServerEvent.TextDelta text:
                        wrote = true;
                        yield return Update(turn, model, new TextContent(text.Text));
                        break;
                    case ClaudeServerEvent.ThinkingDelta thinking:
                        yield return Update(turn, model, new TextReasoningContent(thinking.Text));
                        break;
                    case ClaudeServerEvent.ToolUse use when use.Name.StartsWith(ClaudeArguments.McpToolPrefix, StringComparison.Ordinal):
                        calls.Add(use);
                        break;
                    case ClaudeServerEvent.ToolUse use:
                        DiagnosticLog.Warn(ClaudeText.Category, ClaudeCliText.ForeignToolsLog([use.Name]));
                        break;
                    case ClaudeServerEvent.MessageOutput output:
                        outputTokens = output.OutputTokens;
                        break;
                    case ClaudeServerEvent.MessageStop when calls.Count > 0:
                        var contents = new List<AIContent>(calls.Count + 1);
                        lock (_calls)
                        {
                            foreach (var call in calls)
                            {
                                turn.Pending.Add(call.Id);
                                AnswerFor(call.Id);
                                contents.Add(AnthropicStream.Call(call.Id, call.Name[ClaudeArguments.McpToolPrefix.Length..], call.InputJson));
                            }
                        }

                        contents.Add(Usage(inputTokens, outputTokens));
                        yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
                        {
                            ConversationId = turn.SessionId,
                            ModelId = model,
                            FinishReason = ChatFinishReason.ToolCalls,
                        };
                        yield break;
                    case ClaudeServerEvent.MessageStop:
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [Usage(inputTokens, outputTokens)]) { ConversationId = turn.SessionId, ModelId = model };
                        break;
                    case ClaudeServerEvent.Result result:
                        End(turn, ClaudeCliText.CallCancelled);
                        Account(result);
                        if (!result.IsError)
                        {
                            yield break;
                        }

                        string error = result.Error ?? ClaudeText.UnknownFailure;
                        if (!wrote && !turn.StartedOver && turn.Resume && error.Contains(ClaudeCliText.SessionLostMarker, StringComparison.OrdinalIgnoreCase))
                        {
                            startOver = true;
                            break;
                        }

                        if (!wrote && !turn.StartedOver && !turn.Resume && error.Contains(ClaudeCliText.SessionInUseMarker, StringComparison.OrdinalIgnoreCase))
                        {
                            startOver = true;
                            break;
                        }

                        if (result.TerminalReason is { } reason && reason.StartsWith("aborted", StringComparison.Ordinal))
                        {
                            // An interrupt this client sent (a timeout's, a cancel's): the reply ends where it got to.
                            yield break;
                        }

                        throw new InvalidOperationException(ClaudeText.Failed(error));
                }

                if (startOver)
                {
                    break;
                }
            }

            if (startOver)
            {
                turn = await StartOverAsync(turn, resume: !turn.Resume, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static ChatResponseUpdate Update(Turn turn, string model, AIContent content) =>
        new(ChatRole.Assistant, [content]) { ConversationId = turn.SessionId, ModelId = model };

    private static UsageContent Usage(long input, long output) =>
        new(new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output });

    /// <summary>A <c>result</c>'s spend, logged: the process's so far, so the turn's is the difference.</summary>
    private void Account(ClaudeServerEvent.Result result)
    {
        decimal turnCost = Math.Max(0m, result.TotalCostUsd - _spent);
        _spent = Math.Max(_spent, result.TotalCostUsd);
        DiagnosticLog.Info(ClaudeText.Category, ClaudeCliText.TurnLog(result, turnCost));
    }

    /// <summary>Hands the loop's results to the MCP calls waiting on them; a call the loop gave no result gets an error.</summary>
    private void Deliver(Turn turn, ClaudeTurnInput input)
    {
        var ids = turn.Pending.ToList();
        lock (_calls)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                var result = input.Results.FirstOrDefault(r => string.Equals(r.CallId, ids[i], StringComparison.Ordinal));
                string text = result is null ? ClaudeCliText.NoResultError : ClaudeServerInput.ResultText(result);

                // The pictures the round fetched ride its last answer: the history's carrier holds them together.
                var images = i == ids.Count - 1 ? input.ResultImages : [];
                AnswerFor(ids[i]).TrySetResult(new ClaudeToolAnswer(text, images, ClaudeServerInput.IsError(text)));
            }
        }

        turn.Pending.Clear();
    }

    /// <summary>
    /// A turn left mid-way (ESC while a tool ran, a timeout, a failure): the CLI interrupted when it takes an interrupt, its
    /// waiting calls answered, and its stream read to the turn's <c>result</c>; a CLI that does not get there within
    /// <see cref="InterruptGrace"/> is stopped, and the next turn resumes the session on a new one.
    /// </summary>
    private async Task AbandonAsync(Turn turn)
    {
        _turn = null;
        if (_host.Running && _takesInterrupt)
        {
            string id = "neon-interrupt-" + Interlocked.Increment(ref _interruptCount).ToString(CultureInfo.InvariantCulture);
            using var grace = new CancellationTokenSource(InterruptGrace);
            try
            {
                await _host.WriteLineAsync(ClaudeServerInput.InterruptLine(id), grace.Token).ConfigureAwait(false);
                End(turn, ClaudeCliText.CallCancelled);
                while (await _host.ReadLineAsync(grace.Token).ConfigureAwait(false) is { } line)
                {
                    if (turn.Parser.Read(line).OfType<ClaudeServerEvent.Result>().FirstOrDefault() is { } result)
                    {
                        Account(result);
                        DiagnosticLog.Info(ClaudeText.Category, ClaudeCliText.InterruptedLog);
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
            {
            }
        }

        End(turn, ClaudeCliText.CallCancelled);
        if (_host.Running)
        {
            DiagnosticLog.Info(ClaudeText.Category, ClaudeCliText.StoppedMidTurnLog);
            _host.Stop();
        }
    }

    /// <summary>The turn over: every call still waiting answered <paramref name="why"/>, the answers dropped.</summary>
    private void End(Turn turn, string why)
    {
        if (ReferenceEquals(_turn, turn))
        {
            _turn = null;
        }

        lock (_calls)
        {
            foreach (var answer in _answers.Values)
            {
                answer.TrySetResult(new ClaudeToolAnswer(why, [], IsError: true));
            }

            _answers.Clear();
        }

        turn.Pending.Clear();
    }

    /// <summary>The answer an MCP call with <paramref name="toolUseId"/> waits on, made by whichever side comes first. Under <see cref="_calls"/>.</summary>
    private TaskCompletionSource<ClaudeToolAnswer> AnswerFor(string toolUseId)
    {
        if (!_answers.TryGetValue(toolUseId, out var answer))
        {
            answer = new TaskCompletionSource<ClaudeToolAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _answers[toolUseId] = answer;
        }

        return answer;
    }

    /// <summary>
    /// An MCP call from the CLI: it waits for the loop's result of the same call. A call with no id (a CLI that stopped
    /// sending it) is paired with the first waiting call of its name, else refused.
    /// </summary>
    private Task<ClaudeToolAnswer> OnToolCallAsync(ClaudeToolCall call, CancellationToken cancellationToken)
    {
        TaskCompletionSource<ClaudeToolAnswer>? answer = null;
        lock (_calls)
        {
            if (call.ToolUseId is { Length: > 0 } id)
            {
                answer = AnswerFor(id);
            }
            else if (_turn is { } turn)
            {
                answer = turn.Pending.Select(p => _answers.GetValueOrDefault(p)).FirstOrDefault(a => a is { Task.IsCompleted: false });
            }
        }

        if (answer is null)
        {
            DiagnosticLog.Warn(ClaudeText.Category, ClaudeCliText.UnmatchedCallLog(call.Name));
            return Task.FromResult(new ClaudeToolAnswer(ClaudeCliText.UnmatchedCallError, [], IsError: true));
        }

        return answer.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// A request asked beside the chat: one <c>claude -p</c> with no tools and no session kept, the system messages as its
    /// system prompt and the rest flattened into its stdin (<see cref="ClaudeCliText.Flatten"/>).
    /// </summary>
    private async IAsyncEnumerable<ChatResponseUpdate> OneShotAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string executable = _context.Executable() ?? throw new ClaudeStartException(ClaudeText.NotFound);
        string model = ClaudeCliEndpoint.ModelOf(Endpoint.ModelId);
        string system = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        string prompt = ClaudeCliText.Flatten(messages.Where(m => m.Role != ChatRole.System).ToList());
        string promptFile = Path.Combine(Path.GetTempPath(), "neonsidekick-claude-" + Guid.NewGuid().ToString("N") + ".md");
        await File.WriteAllTextAsync(promptFile, system, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_context.WorkingDirectory);
        var launch = new ProcessLaunch(executable, ClaudeArguments.BuildOneShot(model, EffortOf(options), promptFile), null, _context.WorkingDirectory, "claude -p (one-shot)", "claude");
        var run = _context.OneShot ?? ((l, p, ct) => ClaudeProcess.RunLaunchAsync(l, p, pid => ClaudeCliText.OneShotStartedLog(pid, model), ct));
        try
        {
            await foreach (var evt in run(launch, prompt, cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case ClaudeEvent.TextDelta text:
                        yield return new ChatResponseUpdate(ChatRole.Assistant, text.Text) { ModelId = model };
                        break;
                    case ClaudeEvent.Result { IsError: true } failed:
                        throw new InvalidOperationException(ClaudeText.Failed(failed.Error ?? ClaudeText.UnknownFailure));
                    case ClaudeEvent.Result result:
                        DiagnosticLog.Debug(ClaudeText.Category, ClaudeText.ResultLog(result));
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [Usage(result.Usage.Input, result.Usage.Output)]) { ModelId = model };
                        break;
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(promptFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata("claude-cli", Endpoint.BaseUrl, Endpoint.ModelId);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>
    /// The client goes (a reconnect, the exit): a turn it left mid-way stops the CLI, whose next start resumes the
    /// session; an idle CLI is the host's and stays.
    /// </summary>
    public void Dispose()
    {
        if (_turn is { } turn)
        {
            End(turn, ClaudeCliText.CallCancelled);
            _host.Stop();
        }

        if (ReferenceEquals(_host.OnToolCall, _onToolCall))
        {
            _host.OnToolCall = null;
        }

        _gate.Dispose();
    }

    /// <summary>One Claude turn as this client follows it across the loop's rounds.</summary>
    private sealed class Turn(string sessionId, ClaudeServerLaunch launch, IReadOnlyList<AIFunction> tools, ClaudeTurnInput input, bool resume)
    {
        public string SessionId { get; } = sessionId;

        public ClaudeServerLaunch Launch { get; } = launch;

        public IReadOnlyList<AIFunction> Tools { get; } = tools;

        /// <summary>The request that opened the turn: what a start-over sends again.</summary>
        public ClaudeTurnInput Input { get; } = input;

        /// <summary>Whether the CLI was started (or kept) resuming the session.</summary>
        public bool Resume { get; } = resume;

        /// <summary>Whether this is a start-over already: one per turn.</summary>
        public bool StartedOver { get; init; }

        /// <summary>The calls ended on, waiting for the loop's results.</summary>
        public HashSet<string> Pending { get; } = new(StringComparer.Ordinal);

        public ClaudeServerStream Parser { get; } = new();
    }
}
