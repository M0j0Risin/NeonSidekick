namespace NeonSidekick.Llm;

/// <summary>
/// The session's running token counts, three scopes wide: the last reply, the conversation and
/// the process. Owned by the LLM session next to the history, so it outlives a reconnect
/// (<c>/server</c>, <c>/model</c>) the way the conversation does — the hint row alone stops
/// showing the figures until the new model reports (<see cref="HideFromHint"/>); nothing is persisted. Pure:
/// the screen calls <see cref="BeginTurn"/>, feeds every <see cref="TurnEvent.Usage"/> to
/// <see cref="Add"/> and calls <see cref="EndTurn"/> when the reply completed, and resets the
/// conversation scope exactly where it clears the history.
///
/// <para>One lock around every write and every read: the <c>/usage</c> pane can be opened while
/// a turn runs (its tabs are rebuilt on every show, on the key-reading task), and a
/// <see cref="TokenUsage"/> is several words wide.</para>
/// </summary>
public sealed class TokenTally
{
    private readonly object _gate = new();
    private TokenUsage _lastReply;
    private TokenUsage _previousReply;
    private TokenUsage _conversation;
    private TokenUsage _session;
    private TokenUsage _lastRequest;
    private TokenUsage _learning;
    private int _learningRequests;
    private TokenUsage _claude;
    private int _claudeRuns;
    private decimal _claudeCost;
    private TokenUsage _bots;
    private int _botRequests;
    private TokenUsage _advisors;
    private int _advisorRequests;
    private int _unreportedReplies;
    private bool _hintHidden;

    /// <summary>The turn in progress, or the last one that ran. Zero until the first reply.</summary>
    public TokenUsage LastReply { get { lock (_gate) { return _lastReply; } } }

    /// <summary>
    /// The speed the busy row shows as last known (2026-09-25): this turn's once a request of it reported, else the
    /// turn's before it — <see cref="BeginTurn"/> zeroes <see cref="LastReply"/>, and the row would lose its speed
    /// for the whole first request of every turn. Null until a reply streamed with a count. Since 2026-10-26 the idle row
    /// reads it too, and the turn before is the latest that had a speed: a reply of untimed requests alone (a tool call sent
    /// whole, <see cref="TokenUsage.Judged"/>) leaves the one before showing. Forgotten at <see cref="HideFromHint"/>.
    /// </summary>
    public double? LastKnownSpeed { get { lock (_gate) { return _lastReply.TokensPerSecond ?? _previousReply.TokensPerSecond; } } }

    /// <summary>Since launch or the last <see cref="ResetConversation"/>.</summary>
    public TokenUsage Conversation { get { lock (_gate) { return _conversation; } } }

    /// <summary>Since launch; nothing resets it.</summary>
    public TokenUsage Session { get { lock (_gate) { return _session; } } }

    /// <summary>
    /// The most recent model request alone: its prompt plus completion is what the history holds
    /// now, and so the context in use. Not a sum — every request re-sends the whole conversation,
    /// so <see cref="Conversation"/>'s total overstates the context several times over after a few
    /// turns. Kept across <see cref="BeginTurn"/> until the new turn's first report; zero after
    /// <see cref="ResetConversation"/>, the history having gone with it.
    /// </summary>
    public TokenUsage LastRequest { get { lock (_gate) { return _lastRequest; } } }

    /// <summary>Replies of this conversation that completed without a usage report from the server.</summary>
    public int UnreportedReplies { get { lock (_gate) { return _unreportedReplies; } } }

    /// <summary>A turn starts: the last reply's figures make way for it; the last known speed stays the latest reply that had one.</summary>
    public void BeginTurn()
    {
        lock (_gate)
        {
            if (_lastReply.TokensPerSecond is not null)
            {
                _previousReply = _lastReply;
            }

            _lastReply = TokenUsage.Zero;
        }
    }

    /// <summary>
    /// Whether the hint row leaves the figures out (2026-10-03, the user's ask): set by <see cref="HideFromHint"/> at an
    /// LLM connect, cleared by the next <see cref="Add"/> or <see cref="ResetConversation"/>.
    /// </summary>
    public bool HintHidden { get { lock (_gate) { return _hintHidden; } } }

    /// <summary>
    /// An LLM connect began (2026-10-03, the user's ask: after a <c>/server</c> switch the row kept reading
    /// <c>27.1k tokens · 82 tok/s</c> through the new model's load and after it — the old model's speed over the old
    /// window). Display only: the counts, <c>/usage</c> and the auto-compact keep <see cref="LastRequest"/>, since the
    /// history crosses the switch and a smaller window must still compact before the next message. The new model's first
    /// report brings the row back. The turn before's speed is forgotten (2026-10-26): <see cref="LastKnownSpeed"/> would bring the
    /// old model's back with a new reply that had none.
    /// </summary>
    public void HideFromHint()
    {
        lock (_gate)
        {
            _hintHidden = true;
            _previousReply = TokenUsage.Zero;
        }
    }

    /// <summary>One model request's usage, into every scope.</summary>
    public void Add(TokenUsage usage)
    {
        lock (_gate)
        {
            _hintHidden = false;
            _lastRequest = usage;
            _lastReply += usage;
            _conversation += usage;
            _session += usage;
        }
    }

    /// <summary>A turn completed (not cancelled, not failed): one with nothing counted is an unreported reply.</summary>
    public void EndTurn()
    {
        lock (_gate)
        {
            if (_lastReply.IsEmpty)
            {
                _unreportedReplies++;
            }
        }
    }

    /// <summary>
    /// The history was compacted (<see cref="ConversationCompactor"/>): the summariser's request,
    /// when there was one and the server reported it, is billed to the conversation and the
    /// session like any other; the last reply's figures stay; the context in use is zeroed — what
    /// the new history costs is only known at the next reply, and a stale figure would fire the
    /// auto-compact again on the very next message.
    /// </summary>
    public void AddCompaction(TokenUsage? usage)
    {
        lock (_gate)
        {
            if (usage is { } reported)
            {
                _conversation += reported;
                _session += reported;
            }

            _lastRequest = TokenUsage.Zero;
        }
    }

    /// <summary>
    /// The history was cut back (2026-09-30, <c>/rewind</c>): only the context in use is zeroed, as after a compact. What the
    /// shorter history costs is known at the next reply, and the stale figure would fire the auto-compact on the next message.
    /// The billing scopes stay: those requests were really made.
    /// </summary>
    public void ForgetContext()
    {
        lock (_gate)
        {
            _lastRequest = TokenUsage.Zero;
        }
    }

    /// <summary>
    /// A skill-learning reflection ran (<c>Skills.SkillLearner</c>): its requests are billed to the
    /// conversation and the session like any other and counted under <see cref="Learning"/> for
    /// <c>/usage</c>; the context in use is <b>not</b> touched — the reflection ran beside the
    /// conversation, and the turn's own figure is what the hint row and the auto-compact read.
    /// Safe from the pool while a turn runs: nothing of the turn's scopes is changed.
    /// </summary>
    public void AddLearning(TokenUsage usage, int requests)
    {
        lock (_gate)
        {
            _conversation += usage;
            _session += usage;
            _learning += usage;
            _learningRequests += requests;
        }
    }

    /// <summary>What the skill-learning reflections cost since launch; zero until one ran.</summary>
    public TokenUsage Learning { get { lock (_gate) { return _learning; } } }

    /// <summary>The model requests the reflections made since launch.</summary>
    public int LearningRequests { get { lock (_gate) { return _learningRequests; } } }

    /// <summary>
    /// A <c>/claude</c> run ended (2026-09-27): its tokens and its dollars into their own bucket, since launch, and nowhere
    /// else — they are another model's, on another bill: summed into the conversation they would mislead the context
    /// figures and the auto-compact, which are the local model's.
    /// </summary>
    public void AddClaude(TokenUsage usage, decimal costUsd)
    {
        lock (_gate)
        {
            _claude += usage;
            _claudeRuns++;
            _claudeCost += costUsd;
        }
    }

    /// <summary>What the <c>/claude</c> runs used since launch; zero until one ran.</summary>
    public TokenUsage Claude { get { lock (_gate) { return _claude; } } }

    /// <summary>The <c>/claude</c> runs since launch that reported a result.</summary>
    public int ClaudeRuns { get { lock (_gate) { return _claudeRuns; } } }

    /// <summary>What the <c>/claude</c> runs cost since launch, in US dollars, as the CLI reported it.</summary>
    public decimal ClaudeCostUsd { get { lock (_gate) { return _claudeCost; } } }

    /// <summary>
    /// A <c>/botchat</c> reply ran (2026-10-10, found comparing botchat with the advisors: neither was counted anywhere): its requests
    /// into their own bucket, since launch, and nowhere else, as <see cref="AddClaude"/>'s — a bot may talk through another
    /// profile's server and model, and its view is not this conversation, so summed in they would mislead the context figures and
    /// the auto-compact. Safe from any thread.
    /// </summary>
    public void AddBots(TokenUsage usage, int requests)
    {
        lock (_gate)
        {
            _bots += usage;
            _botRequests += requests;
        }
    }

    /// <summary>What the <c>/botchat</c> bots used since launch; zero until a bot's request reported.</summary>
    public TokenUsage Bots { get { lock (_gate) { return _bots; } } }

    /// <summary>The <c>/botchat</c> requests since launch that reported usage.</summary>
    public int BotRequests { get { lock (_gate) { return _botRequests; } } }

    /// <summary>
    /// An advisor answered (2026-10-10, <c>neon_advisor</c> and <c>/advisor</c>): <see cref="AddBots"/>'s shape, for the same reasons —
    /// each advisor talks through its own profile's server and model. Safe from any thread: an advisor runs under a turn's tool call.
    /// </summary>
    public void AddAdvisors(TokenUsage usage, int requests)
    {
        lock (_gate)
        {
            _advisors += usage;
            _advisorRequests += requests;
        }
    }

    /// <summary>What the advisors used since launch; zero until an advisor's request reported.</summary>
    public TokenUsage Advisors { get { lock (_gate) { return _advisors; } } }

    /// <summary>The advisors' requests since launch that reported usage.</summary>
    public int AdvisorRequests { get { lock (_gate) { return _advisorRequests; } } }

    /// <summary>The conversation was cleared: its scope and the context in use start over; the session's and the last reply's stay.</summary>
    public void ResetConversation()
    {
        lock (_gate)
        {
            _conversation = TokenUsage.Zero;
            _lastRequest = TokenUsage.Zero;
            _unreportedReplies = 0;
            _hintHidden = false;
        }
    }
}
