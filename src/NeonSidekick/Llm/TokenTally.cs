namespace NeonSidekick.Llm;

/// <summary>
/// The session's running token counts, three scopes wide: the last reply, the conversation and
/// the process. Owned by the LLM session next to the history, so it outlives a reconnect
/// (<c>/server</c>, <c>/model</c>) the way the conversation does; nothing is persisted. Pure:
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
    private int _unreportedReplies;

    /// <summary>The turn in progress, or the last one that ran. Zero until the first reply.</summary>
    public TokenUsage LastReply { get { lock (_gate) { return _lastReply; } } }

    /// <summary>
    /// The speed the busy row shows as last known (2026-09-25): this turn's once a request of it reported, else the
    /// turn's before it — <see cref="BeginTurn"/> zeroes <see cref="LastReply"/>, and the row would lose its speed
    /// for the whole first request of every turn. Null until a reply streamed with a count.
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

    /// <summary>A turn starts: the last reply's figures make way for it.</summary>
    public void BeginTurn()
    {
        lock (_gate)
        {
            if (!_lastReply.IsEmpty)
            {
                _previousReply = _lastReply;
            }

            _lastReply = TokenUsage.Zero;
        }
    }

    /// <summary>One model request's usage, into every scope.</summary>
    public void Add(TokenUsage usage)
    {
        lock (_gate)
        {
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

    /// <summary>The conversation was cleared: its scope and the context in use start over; the session's and the last reply's stay.</summary>
    public void ResetConversation()
    {
        lock (_gate)
        {
            _conversation = TokenUsage.Zero;
            _lastRequest = TokenUsage.Zero;
            _unreportedReplies = 0;
        }
    }
}
