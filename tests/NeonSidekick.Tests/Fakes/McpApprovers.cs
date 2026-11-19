using NeonSidekick.Mcp;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// Approvers for an <c>McpSession</c>'s calls that ask (2026-11-19, feature idea #28): <see cref="AllowAll"/> for the tests about connects,
/// lists and reconnects, so their unmarked tools still run; <see cref="Recording"/> for the ask's own tests.
/// </summary>
public static class McpApprovers
{
    /// <summary>Every ask allowed once.</summary>
    public static Task<McpVerdict> AllowAll(McpAsk ask, CancellationToken cancellationToken) =>
        Task.FromResult<McpVerdict>(new McpVerdict.Allowed(McpText.AllowedOnce));

    /// <summary>
    /// Records each <see cref="McpAsk"/> and answers from a queue of verdicts (allowed once when it runs dry). <see cref="Before"/> runs
    /// first on each ask: a test drops the server while the pane would be open.
    /// </summary>
    public sealed class Recording(params McpVerdict[] verdicts)
    {
        private readonly Queue<McpVerdict> _verdicts = new(verdicts);
        private readonly List<McpAsk> _asks = [];

        /// <summary>What runs on each ask before it's answered.</summary>
        public Func<McpAsk, Task>? Before { get; set; }

        /// <summary>Every ask, in order.</summary>
        public IReadOnlyList<McpAsk> Asks
        {
            get
            {
                lock (_asks)
                {
                    return _asks.ToArray();
                }
            }
        }

        public async Task<McpVerdict> Approve(McpAsk ask, CancellationToken cancellationToken)
        {
            lock (_asks)
            {
                _asks.Add(ask);
            }

            if (Before is { } before)
            {
                await before(ask);
            }

            lock (_asks)
            {
                return _verdicts.Count > 0 ? _verdicts.Dequeue() : new McpVerdict.Allowed(McpText.AllowedOnce);
            }
        }
    }
}
