using System.Threading.Channels;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.WebUi;

/// <summary>
/// One server-sent event: its name and its JSON data, made when a page's stream first writes it — on that stream's thread, never on the
/// task that published it (the turn task publishes a reply's deltas; a reply's HTML is rendered here, once, for every page), and not
/// at all when no page is connected. <see cref="Page"/> (2026-11-13, the webui-voice plan) targets one page: only that page's streams get it.
/// </summary>
public sealed record WebEvent(string Name, Lazy<string> Json)
{
    public WebEvent(string name, Func<string> json)
        : this(name, new Lazy<string>(json, LazyThreadSafetyMode.ExecutionAndPublication))
    {
    }

    /// <summary>The page the event is for, or null for every page (everything but voice).</summary>
    public string? Page { get; init; }
}

/// <summary>
/// The Web UI's fan-out (2026-10-14): what the feed, the asks and the state publish reaches every connected page's event stream. Each
/// subscriber has a bounded queue of its own; one that falls behind (a phone asleep with its socket half open) is dropped — its stream
/// ends, the page reconnects and starts again from a snapshot — so a slow reader never holds a writer up. Publishing never blocks and
/// never throws; it runs on whatever task produced the event.
///
/// <para><b>Pages (2026-11-13, the webui-voice plan).</b> A Chat stream names its page and its run (<c>?page=&amp;load=</c>). A subscribe with
/// the same page and run replaces the older stream (a half-open socket); the same page with a new run while its stream is open is a
/// duplicated tab, given a fresh id (<see cref="Subscription.Page"/>), as the Video tab's player does. <see cref="PageJoined"/> and
/// <see cref="PageLeft"/> tell the voice side, after the list is updated and never under the lock; a bare subscriber gets broadcasts only.</para>
/// </summary>
public sealed class WebEventHub
{
    /// <summary>The most events one page may have waiting before it is dropped.</summary>
    public const int Capacity = 2048;

    private const string Category = "WebUi";

    private readonly object _gate = new();
    private readonly List<Sub> _subscribers = [];

    /// <summary>A page's stream subscribed: its page id (perhaps fresh, a duplicated tab's) and its run. Called on the stream's thread; must not block.</summary>
    public Action<string, string>? PageJoined { get; set; }

    /// <summary>A page's last stream went (closed, or dropped for falling behind). Called on whatever thread removed it; must not block.</summary>
    public Action<string>? PageLeft { get; set; }

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

    /// <summary>Whether <paramref name="page"/> has a stream open now.</summary>
    public bool HasPage(string? page)
    {
        if (page is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _subscribers.Exists(s => s.Page == page);
        }
    }

    /// <summary>Whether <paramref name="page"/> has a stream open now from run <paramref name="load"/>.</summary>
    public bool HasPage(string? page, string? load)
    {
        if (page is null || load is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _subscribers.Exists(s => s.Page == page && s.Load == load);
        }
    }

    /// <summary>
    /// The run <paramref name="page"/>'s open stream came from, or null with none (2026-11-14, the review's #52: a voice entry made after
    /// the page joined learns its run here). One stream per page id, a duplicated tab getting a fresh id, so the answer is the one run.
    /// </summary>
    public string? LoadOf(string? page)
    {
        if (page is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _subscribers.Find(s => s.Page == page)?.Load;
        }
    }

    /// <summary>
    /// A new subscription; dispose it when the page's stream ends. <paramref name="page"/> and <paramref name="load"/> must have the id shape
    /// (<see cref="WebVideoPlayer.IsId"/>) to count; otherwise the subscriber gets broadcasts only.
    /// </summary>
    public Subscription Subscribe(string? page = null, string? load = null)
    {
        if (!WebVideoPlayer.IsId(page) || !WebVideoPlayer.IsId(load))
        {
            page = null;
            load = null;
        }

        var channel = Channel.CreateBounded<WebEvent>(new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var sub = new Sub(channel, page, load);
        lock (_gate)
        {
            if (page is not null)
            {
                var same = _subscribers.FindAll(s => s.Page == page);
                var reconnect = same.FindAll(s => s.Load == load);
                foreach (var old in reconnect)
                {
                    _subscribers.Remove(old);   // a reconnect: the half-open stream goes quietly
                    old.Channel.Writer.TryComplete();
                }

                if (reconnect.Count == 0 && same.Count > 0)
                {
                    page = WebVideoPlayer.NewId();   // a duplicated tab: sessionStorage copied, the other page still open
                    sub = new Sub(channel, page, load);
                }
            }

            _subscribers.Add(sub);
        }

        if (page is not null)
        {
            Raise(() => PageJoined?.Invoke(page, load!));
        }

        return new Subscription(this, sub);
    }

    /// <summary>
    /// Hands <paramref name="item"/> to every page (or to its <see cref="WebEvent.Page"/> alone); one whose queue is full is dropped. Nothing at all
    /// with no page connected.
    /// </summary>
    public void Publish(WebEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        List<string>? left = null;
        lock (_gate)
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                var sub = _subscribers[i];
                if (item.Page is { } target && sub.Page != target)
                {
                    continue;
                }

                if (!sub.Channel.Writer.TryWrite(item))
                {
                    sub.Channel.Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                    if (sub.Page is { } page && !_subscribers.Exists(s => s.Page == page))
                    {
                        (left ??= []).Add(page);
                    }
                }
            }
        }

        if (left is not null)
        {
            foreach (string page in left)
            {
                Raise(() => PageLeft?.Invoke(page));
            }
        }
    }

    private void Remove(Sub sub)
    {
        bool left;
        lock (_gate)
        {
            // A stream dropped by Publish, or replaced by a reconnect, is already gone: its Dispose raises nothing.
            left = _subscribers.Remove(sub) && sub.Page is { } page && !_subscribers.Exists(s => s.Page == page);
        }

        sub.Channel.Writer.TryComplete();
        if (left)
        {
            Raise(() => PageLeft?.Invoke(sub.Page!));
        }
    }

    /// <summary>A handler run where it can't take the publisher down: a throw is logged.</summary>
    private static void Raise(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Error(Category, "A Web UI page handler failed: " + e.Message, e);
        }
    }

    internal sealed record Sub(Channel<WebEvent> Channel, string? Page, string? Load);

    /// <summary>One page's queue of events; disposing it unsubscribes.</summary>
    public sealed class Subscription : IDisposable
    {
        private readonly WebEventHub _hub;
        private readonly Sub _sub;

        internal Subscription(WebEventHub hub, Sub sub)
        {
            _hub = hub;
            _sub = sub;
        }

        public ChannelReader<WebEvent> Reader => _sub.Channel.Reader;

        /// <summary>The page this stream is (a duplicated tab's fresh id), or null for a bare subscriber.</summary>
        public string? Page => _sub.Page;

        /// <summary>The run it came from, or null.</summary>
        public string? Load => _sub.Load;

        public void Dispose() => _hub.Remove(_sub);
    }
}
