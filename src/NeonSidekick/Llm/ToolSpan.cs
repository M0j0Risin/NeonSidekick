namespace NeonSidekick.Llm;

/// <summary>
/// The time a tool call spent waiting on the user (2026-11-02, the review: the reply footer took off every pane the menu showed while a tool
/// ran, a <c>/settings</c> page the user opened over the reply too, and missed an approval answered on the Web UI's page). The Assistant
/// runs each call under one (<see cref="RunAsync{T}"/>), an <see cref="AsyncLocal{T}"/> that flows into the tool and nowhere else; the
/// turn side's asks (<c>ChatScreen.AskBothAsync</c>, <c>RaceAsync</c>, the bot check's wait, the camera's pane) count their whole wait
/// on it through <see cref="Asking"/>, the PC's pane, the page's answer and the queue before either alike.
/// </summary>
/// <remarks>
/// The clock is a union of intervals: asks that overlap count once (a nested ask's depth), and <see cref="Asked"/> includes an ask still
/// open. Spans nest: a tool that runs an Assistant of its own (the advisor) sets an inner span for its tools, and an ask counts on every
/// span up the chain, so an approval inside the advisor's tool comes off the advisor's time and the footer's too. The one place a span
/// does not reach is code run with the execution context's flow suppressed (<c>UncAccess</c>'s impersonated file IO), where nothing asks.
/// Thread-safe: an ask may end on another thread than it began.
/// </remarks>
public sealed class ToolSpan
{
    private static readonly AsyncLocal<ToolSpan?> Ambient = new();

    private readonly TimeProvider _time;
    private readonly ToolSpan? _parent;
    private readonly object _gate = new();
    private int _depth;
    private long _openedAt;
    private TimeSpan _asked;

    private ToolSpan(TimeProvider time, ToolSpan? parent)
    {
        _time = time;
        _parent = parent;
    }

    /// <summary>The span of the tool call this code runs under, null outside one.</summary>
    public static ToolSpan? Current => Ambient.Value;

    /// <summary>The time asks inside the span have waited, an ask still open counted to now.</summary>
    public TimeSpan Asked
    {
        get
        {
            lock (_gate)
            {
                return _depth > 0 ? _asked + _time.GetElapsedTime(_openedAt) : _asked;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> under a new span, the current one its parent, and returns its result with the time its asks waited. Set
    /// inside this ordinary async method, the span flows into the body and is gone when it returns (never left in force across an
    /// iterator's <c>yield return</c>).
    /// </summary>
    public static async Task<(T Result, TimeSpan Asked)> RunAsync<T>(TimeProvider time, Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(body);
        var span = new ToolSpan(time, Ambient.Value);
        Ambient.Value = span;
        try
        {
            var result = await body().ConfigureAwait(false);
            return (result, span.Asked);
        }
        finally
        {
            Ambient.Value = span._parent;
        }
    }

    /// <summary>An ask begins on this span and every one up the chain; disposing it ends it there.</summary>
    public IDisposable Asking()
    {
        for (var span = this; span is not null; span = span._parent)
        {
            span.Open();
        }

        return new Ask(this);
    }

    private void Open()
    {
        lock (_gate)
        {
            if (_depth++ == 0)
            {
                _openedAt = _time.GetTimestamp();
            }
        }
    }

    private void Close()
    {
        lock (_gate)
        {
            if (_depth > 0 && --_depth == 0)
            {
                _asked += _time.GetElapsedTime(_openedAt);
            }
        }
    }

    private sealed class Ask(ToolSpan span) : IDisposable
    {
        private ToolSpan? _span = span;

        public void Dispose()
        {
            for (var at = Interlocked.Exchange(ref _span, null); at is not null; at = at._parent)
            {
                at.Close();
            }
        }
    }
}
