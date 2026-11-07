using SearXNG.Net;
using SearXNG.Net.Engines;

namespace NeonSidekick.Tests.Fakes;

/// <summary>An engine scripted by a test.</summary>
internal sealed class FakeEngine(string name, Func<EngineQuery, CancellationToken, Task<IReadOnlyList<EngineResult>>> search) : IEngine
{
    public FakeEngine(string name, params EngineResult[] results)
        : this(name, (_, _) => Task.FromResult<IReadOnlyList<EngineResult>>(results))
    {
    }

    public FakeEngine(string name, Func<EngineQuery, Task<IReadOnlyList<EngineResult>>> search)
        : this(name, (query, _) => search(query))
    {
    }

    public string Name => name;

    public string Category { get; init; } = "general";

    public double Weight { get; init; } = 1.0;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    public bool Paging { get; init; }

    public int Calls { get; private set; }

    public List<EngineQuery> Queries { get; } = [];

    public Task<IReadOnlyList<EngineResult>> SearchAsync(EngineQuery query, HttpClient http, CancellationToken cancellationToken)
    {
        Calls++;
        Queries.Add(query);
        return search(query, cancellationToken);
    }
}
