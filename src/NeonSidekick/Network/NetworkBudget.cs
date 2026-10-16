using System.Net;

namespace NeonSidekick.Network;

/// <summary>
/// The per-turn scan budget (2026-10-15, the admin-tools plan, the user's call): at most <c>Network tools max hosts per turn</c> distinct
/// targets and <c>Network tools max ports per turn</c> distinct host:port pairs across every <c>net_*</c> call of a turn — a script's and an
/// advisor's calls included, since they call the turn's own instances. A target is known by the name it was asked as and by each address it
/// was admitted at, so two names for one address count once and a sweep of addresses counts each. Reset when the turn's tools are composed;
/// a bot has its own, reset at each bot turn. <c>/net</c> (the user's hand) never counts. Thread-safe: parallel calls share it.
/// </summary>
public sealed class NetworkBudget
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _targets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pairs = new(StringComparer.Ordinal);
    private int _nextTarget;
    private bool _noted;

    /// <summary>The distinct targets counted so far this turn.</summary>
    public int Hosts
    {
        get
        {
            lock (_gate)
            {
                return _nextTarget;
            }
        }
    }

    /// <summary>The distinct host:port pairs counted so far this turn.</summary>
    public int Ports
    {
        get
        {
            lock (_gate)
            {
                return _pairs.Count;
            }
        }
    }

    /// <summary>A new turn: nothing counted, the refusal not yet noted.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _targets.Clear();
            _pairs.Clear();
            _nextTarget = 0;
            _noted = false;
        }
    }

    /// <summary>
    /// Counts the target <paramref name="name"/> at <paramref name="addresses"/> and its <paramref name="ports"/>, all or nothing: null when
    /// it fits under <paramref name="maxHosts"/> and <paramref name="maxPorts"/>, else the refusal (nothing counted).
    /// </summary>
    public string? Take(string name, IReadOnlyList<IPAddress> addresses, IReadOnlyCollection<int> ports, int maxHosts, int maxPorts)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(ports);
        var keys = Keys(name, addresses);
        lock (_gate)
        {
            int? known = null;
            foreach (string key in keys)
            {
                if (_targets.TryGetValue(key, out int id))
                {
                    known = id;
                    break;
                }
            }

            if (known is null && _nextTarget >= maxHosts)
            {
                return NetworkText.HostBudgetSpent(_nextTarget);
            }

            int target = known ?? _nextTarget;
            var newPairs = ports.Select(p => target.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + p.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Where(p => !_pairs.Contains(p)).Distinct(StringComparer.Ordinal).ToList();
            if (_pairs.Count + newPairs.Count > maxPorts)
            {
                return NetworkText.PortBudgetSpent(_pairs.Count, newPairs.Count);
            }

            if (known is null)
            {
                _nextTarget++;
            }

            foreach (string key in keys)
            {
                _targets.TryAdd(key, target);
            }

            foreach (string pair in newPairs)
            {
                _pairs.Add(pair);
            }

            return null;
        }
    }

    /// <summary>
    /// Whether the budget's refusal is yet to be noted in the transcript this turn (2026-10-15, the plan: once per turn, not per call); true
    /// once, then false until <see cref="Reset"/>.
    /// </summary>
    public bool NoteOnce()
    {
        lock (_gate)
        {
            if (_noted)
            {
                return false;
            }

            _noted = true;
            return true;
        }
    }

    private static List<string> Keys(string name, IReadOnlyList<IPAddress> addresses)
    {
        var keys = new List<string>(addresses.Count + 1);
        string normalized = HostRules.NormalizeName(name);
        if (normalized.Length > 0)
        {
            keys.Add(normalized);
        }

        foreach (var address in addresses)
        {
            string text = HostRules.Canonical(address).ToString();
            if (!keys.Contains(text, StringComparer.Ordinal))
            {
                keys.Add(text);
            }
        }

        return keys;
    }
}
