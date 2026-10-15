namespace NeonSidekick.WebUi;

/// <summary>
/// One send per <c>interval</c> with a trailing one (later on 2026-10-15, out of the strip's hand-written copy, the picture point's too): a nudge
/// sends at once when the last send is older than the interval; inside it, one send is kept for the window's end, so the last change always
/// lands (a held arrow key's last picture). Never blocks; the send runs on the nudging thread or the timer's. A trailing send's failure is
/// handed to <c>failed</c>, a nudge's goes to its caller.
/// </summary>
public sealed class WebThrottle : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Action _send;
    private readonly Action<Exception>? _failed;
    private readonly ITimer _trail;
    private readonly object _gate = new();
    private long _at;
    private bool _sent;
    private bool _trailing;

    public WebThrottle(TimeProvider time, TimeSpan interval, Action send, Action<Exception>? failed = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _interval = interval;
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _failed = failed;
        _trail = _time.CreateTimer(_ => Trailing(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Nudge()
    {
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_trailing)
            {
                return;
            }

            var since = _time.GetElapsedTime(_at, now);
            if (_sent && since < _interval)
            {
                _trailing = true;
                _trail.Change(_interval - since, Timeout.InfiniteTimeSpan);
                return;
            }

            _at = now;
            _sent = true;
        }

        _send();
    }

    private void Trailing()
    {
        lock (_gate)
        {
            _trailing = false;
            _at = _time.GetTimestamp();
        }

        try
        {
            _send();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _failed?.Invoke(e);
        }
    }

    public void Dispose() => _trail.Dispose();
}
