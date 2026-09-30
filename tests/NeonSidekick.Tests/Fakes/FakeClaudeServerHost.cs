using System.Threading.Channels;
using Microsoft.Extensions.AI;
using NeonSidekick.Claude;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A scripted <see cref="IClaudeServerHost"/> (2026-09-30): no process, a channel of stdout lines the test feeds
/// (<see cref="Feed"/>, <see cref="End"/>), and a record of every launch, stdin line and stop. <see cref="OnWrite"/> plays
/// the CLI's side: it sees each line the client writes and feeds what the CLI would answer (a turn's lines, an interrupt's
/// <c>control_response</c> and <c>result</c>), and may call <see cref="IClaudeServerHost.OnToolCall"/> as the CLI's MCP
/// relay would.
/// </summary>
public sealed class FakeClaudeServerHost : IClaudeServerHost
{
    private Channel<string?> _lines = Channel.CreateUnbounded<string?>();
    private ClaudeServerLaunch? _launch;

    /// <summary>Every start: what it was launched with, and whether it resumed.</summary>
    public List<(ClaudeServerLaunch Launch, bool Resume, IReadOnlyList<AIFunction> Tools)> Starts { get; } = new();

    /// <summary>Every stdin line, in order.</summary>
    public List<string> Written { get; } = new();

    /// <summary>How many times <see cref="Stop"/> stopped a running CLI.</summary>
    public int Stops { get; private set; }

    /// <summary>Sees each written line (after it is recorded); the CLI's answer goes in through <see cref="Feed"/>.</summary>
    public Func<string, Task>? OnWrite { get; set; }

    /// <summary>Thrown by the next start, once.</summary>
    public ClaudeStartException? FailNextStart { get; set; }

    public Func<ClaudeToolCall, CancellationToken, Task<ClaudeToolAnswer>>? OnToolCall { get; set; }

    public string StderrTail { get; set; } = "";

    public int? ExitCode { get; set; }

    public bool Running { get; private set; }

    /// <summary>Stdout lines, in order.</summary>
    public void Feed(params string[] lines)
    {
        foreach (string line in lines)
        {
            _lines.Writer.TryWrite(line);
        }
    }

    /// <summary>The CLI's stdout closes: it ended.</summary>
    public void End()
    {
        Running = false;
        _lines.Writer.TryWrite(null);
    }

    public Task<bool> EnsureRunningAsync(ClaudeServerLaunch launch, bool resume, IReadOnlyList<AIFunction> tools, CancellationToken cancellationToken)
    {
        if (Running && _launch == launch)
        {
            return Task.FromResult(false);
        }

        if (FailNextStart is { } failure)
        {
            FailNextStart = null;
            throw failure;
        }

        Stop();
        _lines = Channel.CreateUnbounded<string?>();
        _launch = launch;
        Running = true;
        ExitCode = null;
        Starts.Add((launch, resume, tools));
        return Task.FromResult(true);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (!Running)
        {
            throw new InvalidOperationException(ClaudeCliText.NotRunningError);
        }

        Written.Add(line);
        if (OnWrite is { } hook)
        {
            await hook(line);
        }
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _lines.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public void Stop()
    {
        if (Running)
        {
            Stops++;
        }

        Running = false;
        _launch = null;
        _lines.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
