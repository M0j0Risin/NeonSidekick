// SPDX-License-Identifier: AGPL-3.0-or-later

using SearXNG.Net.Results;

namespace SearXNG.Net;

/// <summary>Settings for a <see cref="MetaSearch"/>.</summary>
public sealed record MetaSearchOptions
{
    /// <summary>Caps every search's timeout, as SearXNG's <c>outgoing.max_request_timeout</c> does; null for no cap.</summary>
    public TimeSpan? MaxRequestTimeout { get; init; }

    /// <summary>The rest after an ordinary failure such as a timeout. SearXNG's default is 5 seconds.</summary>
    public TimeSpan DefaultSuspendTime { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>How long one engine took.</summary>
/// <param name="Engine">The engine's name.</param>
/// <param name="Elapsed">The time from the start of the search to this engine's answer.</param>
public sealed record EngineTiming(string Engine, TimeSpan Elapsed);

/// <summary>The outcome of a search.</summary>
/// <param name="Results">The merged results, best first.</param>
/// <param name="Failures">The engines that gave nothing, and why.</param>
/// <param name="Timings">How long each answering engine took.</param>
/// <param name="HasMorePages">Whether an asked engine can serve a later page.</param>
public sealed record MetaSearchResponse(
    IReadOnlyList<MergedResult> Results,
    IReadOnlyList<EngineFailure> Failures,
    IReadOnlyList<EngineTiming> Timings,
    bool HasMorePages);

/// <summary>
/// Asks several engines at once and merges what they find: SearXNG's <c>searx/search/__init__.py</c> and its online processor.
/// Keep one instance for the application's life, because it remembers which engines are resting after a failure.
/// </summary>
public sealed class MetaSearch
{
    private readonly Dictionary<string, IEngine> _engines = new(StringComparer.Ordinal);
    private readonly List<IEngine> _order = [];
    private readonly Dictionary<string, Suspension> _suspensions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _unfinished = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly HttpClient _http;
    private readonly MetaSearchOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the search.</summary>
    /// <param name="engines">The engines to ask.</param>
    /// <param name="http">The client for every engine's requests. The host owns it and its network rules.</param>
    /// <param name="options">The settings; the defaults when null.</param>
    /// <param name="time">The clock; the system clock when null.</param>
    public MetaSearch(IEnumerable<IEngine> engines, HttpClient http, MetaSearchOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _options = options ?? new MetaSearchOptions();
        _time = time ?? TimeProvider.System;
        foreach (var engine in engines)
        {
            if (!_engines.TryAdd(engine.Name, engine))
            {
                throw new ArgumentException($"Two engines are named '{engine.Name}'.", nameof(engines));
            }

            _order.Add(engine);
        }
    }

    /// <summary>Asks every available engine and returns the merged results.</summary>
    /// <param name="query">The search.</param>
    /// <param name="cancellationToken">Ends the search early; this throws rather than returning partial results.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<MetaSearchResponse> SearchAsync(EngineQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        long start = _time.GetTimestamp();
        var started = _time.GetUtcNow();
        var failures = new List<EngineFailure>();
        var asked = new List<IEngine>();
        Select(query, asked, failures);

        var container = new ResultContainer(_engines);
        var timings = new List<EngineTiming>();
        if (asked.Count > 0)
        {
            using var timeout = new CancellationTokenSource(ActualTimeout(asked, query), _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var outcomes = await Task.WhenAll(asked.Select(engine => WithinDeadlineAsync(engine, query, start, started, cancellationToken, linked.Token))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // SearXNG merges in whatever order engines finish, so ties can rank differently from one search to the next.
            // Merging in the configured order gives the same ranking for the same answers.
            foreach (var outcome in outcomes)
            {
                if (outcome.Failure is not null)
                {
                    failures.Add(outcome.Failure);
                    continue;
                }

                container.Extend(outcome.Engine.Name, outcome.Results);
                timings.Add(new EngineTiming(outcome.Engine.Name, outcome.Elapsed));
            }
        }

        return new MetaSearchResponse(container.GetOrderedResults(), failures, timings, asked.Any(engine => engine.Paging));
    }

    /// <summary>Whether a search would ask any engine, as one with every engine resting or left out asks none.</summary>
    /// <param name="query">The search.</param>
    public bool AnyAvailable(EngineQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var asked = new List<IEngine>();
        Select(query, asked, []);
        return asked.Count > 0;
    }

    private void Select(EngineQuery query, List<IEngine> asked, List<EngineFailure> failures)
    {
        foreach (var engine in _order)
        {
            // First, so an engine left out isn't reported as resting.
            if (query.Engines is { } wanted && !wanted.Contains(engine.Name))
            {
                continue;
            }

            // get_params: an engine without paging sits out later pages.
            if (query.PageNumber > 1 && !engine.Paging)
            {
                continue;
            }

            var (reason, cause) = SuspensionReason(engine.Name);
            if (reason is null && IsUnfinished(engine.Name))
            {
                (reason, cause) = ("still reading an earlier answer", EngineFailureKind.Timeout);
            }

            if (reason is not null)
            {
                failures.Add(new EngineFailure(engine.Name, EngineFailureKind.Suspended, reason, RestCause: cause));
                continue;
            }

            asked.Add(engine);
        }
    }

    /// <summary>SearXNG's <c>_get_requests</c>: the longest engine timeout, limited by the query's limit and the configured cap.</summary>
    private TimeSpan ActualTimeout(List<IEngine> asked, EngineQuery query)
    {
        var longest = asked.Max(engine => engine.Timeout);
        return (_options.MaxRequestTimeout, query.TimeoutLimit) switch
        {
            (null, null) => longest,
            (null, { } limit) => Min(longest, limit),
            ({ } cap, null) => Min(longest, cap),
            ({ } cap, { } limit) => Min(limit, cap),
        };
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>
    /// Parsing can't be canceled, so a search doesn't wait for it: as SearXNG's <c>th.join(remaining_time)</c>, it moves on at the
    /// deadline and reports a timeout, and the parse carries on until it ends, with its result dropped.
    /// </summary>
    private async Task<Outcome> WithinDeadlineAsync(IEngine engine, EngineQuery query, long start, DateTimeOffset started, CancellationToken caller, CancellationToken search)
    {
        var run = RunAsync(engine, query, start, started, caller, search);
        try
        {
            return await run.WaitAsync(search).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            Abandon(engine.Name, run);
            return Failed(engine, EngineFailureKind.Timeout, "canceled", null);
        }
        catch (OperationCanceledException)
        {
            Abandon(engine.Name, run);
            Suspend(engine.Name, _options.DefaultSuspendTime, "timeout", EngineFailureKind.Timeout);
            return Failed(engine, EngineFailureKind.Timeout, "timeout", null);
        }
    }

    /// <summary>An engine isn't asked again until what this search left behind has ended, or repeated searches would pile up parses.</summary>
    private void Abandon(string engine, Task run)
    {
        lock (_gate)
        {
            _unfinished[engine] = _unfinished.GetValueOrDefault(engine) + 1;
        }

        _ = run.ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    if (_unfinished[engine] == 1)
                    {
                        _unfinished.Remove(engine);
                    }
                    else
                    {
                        _unfinished[engine]--;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool IsUnfinished(string engine)
    {
        lock (_gate)
        {
            return _unfinished.ContainsKey(engine);
        }
    }

    private async Task<Outcome> RunAsync(IEngine engine, EngineQuery query, long start, DateTimeOffset started, CancellationToken caller, CancellationToken search)
    {
        try
        {
            var results = await engine.SearchAsync(query, _http, search).ConfigureAwait(false);
            Resume(engine.Name, started);
            return new Outcome(engine, results, null, _time.GetElapsedTime(start));
        }
        catch (EngineException e)
        {
            Suspend(engine.Name, e.SuspendFor ?? (e.Kind == EngineFailureKind.UnexpectedResponse ? TimeSpan.Zero : _options.DefaultSuspendTime), e.Message, e.Kind);
            return Failed(engine, e.Kind, e.Message, e.RefusedUrl);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            // Not the engine's fault: no rest. SearchAsync throws for the caller.
            return Failed(engine, EngineFailureKind.Timeout, "canceled", null);
        }
        catch (OperationCanceledException)
        {
            // This search's timeout, or the HttpClient's own.
            Suspend(engine.Name, _options.DefaultSuspendTime, "timeout", EngineFailureKind.Timeout);
            return Failed(engine, EngineFailureKind.Timeout, "timeout", null);
        }
        catch (HttpRequestException e)
        {
            Suspend(engine.Name, _options.DefaultSuspendTime, e.Message, EngineFailureKind.Network);
            return Failed(engine, EngineFailureKind.Network, e.Message, null);
        }
        catch (Exception e)
        {
            // As in SearXNG's processor: one engine's bug is reported, and must not end the whole search.
            return Failed(engine, EngineFailureKind.UnexpectedResponse, e.GetType().Name + ": " + e.Message, null);
        }
    }

    private static Outcome Failed(IEngine engine, EngineFailureKind kind, string message, Uri? url) =>
        new(engine, [], new EngineFailure(engine.Name, kind, message, url), TimeSpan.Zero);

    private (string? Reason, EngineFailureKind? Cause) SuspensionReason(string engine)
    {
        lock (_gate)
        {
            return _suspensions.TryGetValue(engine, out var suspension) && suspension.Until >= _time.GetUtcNow() ? (suspension.Reason, suspension.Cause) : (null, null);
        }
    }

    private void Suspend(string engine, TimeSpan rest, string reason, EngineFailureKind cause)
    {
        if (rest <= TimeSpan.Zero)
        {
            return;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var until = now + rest;
            if (!_suspensions.TryGetValue(engine, out var current) || until > current.Until)
            {
                _suspensions[engine] = new Suspension(until, reason, cause, now);
            }
        }
    }

    private void Resume(string engine, DateTimeOffset started)
    {
        lock (_gate)
        {
            if (_suspensions.TryGetValue(engine, out var current) && current.SetAt < started)
            {
                _suspensions.Remove(engine);
            }
        }
    }

    private sealed record Suspension(DateTimeOffset Until, string Reason, EngineFailureKind Cause, DateTimeOffset SetAt);

    private sealed record Outcome(IEngine Engine, IReadOnlyList<EngineResult> Results, EngineFailure? Failure, TimeSpan Elapsed);
}
