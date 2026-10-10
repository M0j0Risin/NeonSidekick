using Microsoft.Extensions.AI;

namespace NeonSidekick.Advisors;

/// <summary>One question to an advisor and its answer, as a thread keeps and a session stores them (2026-10-10).</summary>
public sealed record AdvisorPair(string Question, string Answer);

/// <summary>
/// Each advisor's thread in this session (2026-10-10, the user's call: a thread per advisor): its questions and answers, no tool
/// round trips, the newest <see cref="Settings.AppSettingsData.AdvisorThreadPairs"/> kept. Keyed by profile, case ignored. The
/// screen (or headless) stores it with the session, reads it back on a restore and clears it with the session. Thread-safe.
/// </summary>
public sealed class AdvisorThreads
{
    private readonly object _gate = new();
    private Dictionary<string, List<AdvisorPair>> _threads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The thread of <paramref name="profile"/>, oldest first; empty when it has none.</summary>
    public IReadOnlyList<AdvisorPair> Of(string profile)
    {
        lock (_gate)
        {
            return _threads.TryGetValue(profile, out var pairs) ? [.. pairs] : [];
        }
    }

    /// <summary>The thread as the advisor's history replays it: the question as the user's line, the answer as the assistant's.</summary>
    public IReadOnlyList<ChatMessage> Messages(string profile) =>
        Of(profile).SelectMany(p => new[] { new ChatMessage(ChatRole.User, p.Question), new ChatMessage(ChatRole.Assistant, p.Answer) }).ToList();

    /// <summary><paramref name="pair"/> last on <paramref name="profile"/>'s thread, the oldest dropped past <paramref name="keep"/>.</summary>
    public void Add(string profile, AdvisorPair pair, int keep)
    {
        ArgumentNullException.ThrowIfNull(pair);
        lock (_gate)
        {
            if (!_threads.TryGetValue(profile, out var pairs))
            {
                _threads[profile] = pairs = [];
            }

            pairs.Add(pair);
            if (pairs.Count > Math.Max(1, keep))
            {
                pairs.RemoveRange(0, pairs.Count - Math.Max(1, keep));
            }
        }
    }

    /// <summary>Whether any advisor has a thread.</summary>
    public bool Any
    {
        get
        {
            lock (_gate)
            {
                return _threads.Values.Any(t => t.Count > 0);
            }
        }
    }

    /// <summary>Every thread dropped: <c>/new</c>, <c>/clear</c>, a profile switch.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _threads = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>A copy for the session store; null when there is no thread (the stored row keeps no empty key).</summary>
    public Dictionary<string, List<AdvisorPair>>? Snapshot()
    {
        lock (_gate)
        {
            var copy = _threads.Where(t => t.Value.Count > 0).ToDictionary(t => t.Key, t => t.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            return copy.Count == 0 ? null : copy;
        }
    }

    /// <summary>The threads a restored session stored, in place of these; null clears.</summary>
    public void Restore(IReadOnlyDictionary<string, List<AdvisorPair>>? stored)
    {
        lock (_gate)
        {
            _threads = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (profile, pairs) in stored ?? new Dictionary<string, List<AdvisorPair>>())
            {
                if (pairs is { Count: > 0 })
                {
                    _threads[profile] = pairs.Where(p => p is not null).ToList();
                }
            }
        }
    }
}
