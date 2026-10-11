namespace NeonSidekick.Web;

/// <summary>
/// The pages a search listed and the page they were listed on (2026-10-11, the user's ask: get <c>web_fetch</c> past more bot
/// checks): <c>web_search</c> remembers its hits here, and a <c>web_fetch</c> of one goes as a click from that results page would —
/// with the engine's <c>Referer</c> and <c>Sec-Fetch-Site: cross-site</c> — rather than as an address typed into the bar. Only an
/// engine that names a referer (<see cref="ISearchEngine.ResultReferer"/>) is remembered. The <see cref="Capacity"/> hits listed most
/// recently are kept; thread-safe.
/// </summary>
public sealed class ResultReferers
{
    /// <summary>How many hits are remembered; the oldest go first.</summary>
    public const int Capacity = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, (Uri Referer, LinkedListNode<string> Node)> _byUrl = new(StringComparer.Ordinal);

    // Oldest first; a hit listed again moves to the back (code review, 2026-10-11: it kept its first place and went first).
    private readonly LinkedList<string> _order = new();

    /// <summary>Remembers each of <paramref name="results"/> as listed on <paramref name="referer"/>; a hit that is no http(s) URL is skipped.</summary>
    public void Remember(IEnumerable<SearchResult> results, Uri referer)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(referer);
        lock (_gate)
        {
            foreach (var result in results)
            {
                if (WebFetcher.ParseUrl(result.Url) is not { } url)
                {
                    continue;
                }

                string key = url.AbsoluteUri;
                if (_byUrl.TryGetValue(key, out var known))
                {
                    _order.Remove(known.Node);
                }

                _byUrl[key] = (referer, _order.AddLast(key));
                while (_order.Count > Capacity)
                {
                    _byUrl.Remove(_order.First!.Value);
                    _order.RemoveFirst();
                }
            }
        }
    }

    /// <summary>The results page <paramref name="url"/> was listed on, null when no search listed it.</summary>
    public Uri? For(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        lock (_gate)
        {
            return _byUrl.TryGetValue(url.AbsoluteUri, out var known) ? known.Referer : null;
        }
    }
}
