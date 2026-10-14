using System.Threading.Channels;

namespace NeonSidekick.WebUi;

/// <summary>
/// One server-sent event: its name and its JSON data, made when a page's stream first writes it — on that stream's thread, never on the
/// task that published it (the turn task publishes a reply's deltas; a reply's HTML is rendered here, once, for every page), and not
/// at all when no page is connected.
/// </summary>
public sealed record WebEvent(string Name, Lazy<string> Json)
{
    public WebEvent(string name, Func<string> json)
        : this(name, new Lazy<string>(json, LazyThreadSafetyMode.ExecutionAndPublication))
    {
    }
}

/// <summary>
/// The Web UI's fan-out (2026-10-14): what the feed, the asks and the state publish reaches every connected page's event stream. Each
/// subscriber has a bounded queue of its own; one that falls behind (a phone asleep with its socket half open) is dropped — its stream
/// ends, the page reconnects and starts again from a snapshot — so a slow reader never holds a writer up. Publishing never blocks and
/// never throws; it runs on whatever task produced the event.
/// </summary>
public sealed class WebEventHub
{
    /// <summary>The most events one page may have waiting before it is dropped.</summary>
    public const int Capacity = 2048;

    private readonly object _gate = new();
    private readonly List<Channel<WebEvent>> _subscribers = [];

    /// <summary>The pages subscribed now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>A new subscription; dispose it when the page's stream ends.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<WebEvent>(new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        return new Subscription(this, channel);
    }

    /// <summary>Hands <paramref name="item"/> to every page; one whose queue is full is dropped. Nothing at all with no page connected.</summary>
    public void Publish(WebEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                var channel = _subscribers[i];
                if (!channel.Writer.TryWrite(item))
                {
                    channel.Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    private void Remove(Channel<WebEvent> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    /// <summary>One page's queue of events; disposing it unsubscribes.</summary>
    public sealed class Subscription : IDisposable
    {
        private readonly WebEventHub _hub;
        private readonly Channel<WebEvent> _channel;

        internal Subscription(WebEventHub hub, Channel<WebEvent> channel)
        {
            _hub = hub;
            _channel = channel;
        }

        public ChannelReader<WebEvent> Reader => _channel.Reader;

        public void Dispose() => _hub.Remove(_channel);
    }
}
