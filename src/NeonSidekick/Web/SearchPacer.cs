namespace NeonSidekick.Web;

/// <summary>Spaces requests a fixed gap apart. Each search method keeps its own, as each site's limit is its own.</summary>
internal sealed class SearchPacer(TimeProvider time, TimeSpan gap)
{
    private readonly object _gate = new();
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            wait = gap - (now - _last);
            _last = now + (wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
        }
    }
}
