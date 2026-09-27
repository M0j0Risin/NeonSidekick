using NeonSidekick.Llm;

namespace NeonSidekick.Claude;

/// <summary>
/// One <c>/claude</c> message for the Claude Code CLI (2026-09-27).
/// </summary>
/// <param name="Prompt">What the user typed after <c>/claude</c>; sent on the child's stdin, never on its command line.</param>
/// <param name="SessionId">The conversation's id (a GUID this app mints), so a follow-up resumes it.</param>
/// <param name="Resume">False for the first message of the thread (<c>--session-id</c>), true after (<c>--resume</c>).</param>
/// <param name="WorkingDirectory">The folder the child starts in: the sandbox root, as the file tools see it.</param>
/// <param name="Permission">What the child may do past reading (<see cref="ClaudePermission"/>).</param>
/// <param name="Executable">The <c>Claude executable</c> setting: a full path, or blank to look the CLI up.</param>
/// <param name="Model">The <c>--model</c> word, or null for the CLI's own.</param>
/// <param name="Effort">The <c>--effort</c> word, or null for the CLI's own.</param>
public sealed record ClaudeRequest(
    string Prompt,
    string SessionId,
    bool Resume,
    string WorkingDirectory,
    ClaudePermissionLevel Permission,
    string Executable = "",
    string? Model = null,
    string? Effort = null);

/// <summary>What a <c>/claude</c> run reports as it goes (<see cref="ClaudeStreamParser"/> reads them off the CLI's <c>stream-json</c>).</summary>
public abstract record ClaudeEvent
{
    private ClaudeEvent()
    {
    }

    /// <summary>A piece of the reply's text, in order; a separator between two text blocks is already in it.</summary>
    public sealed record TextDelta(string Text) : ClaudeEvent;

    /// <summary>Claude used one of its tools: its name and a short what (<c>Read</c>, <c>src/Foo.cs</c>); <paramref name="Detail"/> may be empty.</summary>
    public sealed record ToolActivity(string Name, string Detail) : ClaudeEvent;

    /// <summary>
    /// The run's end, the CLI's <c>result</c> line: the session it ran in, what it cost, the tokens, and whether it failed.
    /// <paramref name="Text"/> is the whole reply as the CLI saw it (for a run whose text never streamed);
    /// <paramref name="Error"/> says why a failed one failed; <paramref name="Denied"/> the tools the permission level turned away.
    /// </summary>
    public sealed record Result(string? SessionId, decimal CostUsd, TokenUsage Usage, bool IsError, string? Error, string Text, IReadOnlyList<string> Denied) : ClaudeEvent;
}

/// <summary>The CLI could not be found or started: the message is the user's, ready to print.</summary>
public sealed class ClaudeStartException(string message) : Exception(message);

/// <summary>
/// Claude Code driven headless (2026-09-27): one message in, the reply's events out. The app's is
/// <see cref="ClaudeProcess"/> (one <c>claude -p</c> child per message); the tests' a fake that yields a script.
/// </summary>
public interface IClaudeCli
{
    /// <summary>
    /// Runs <paramref name="request"/>; the last event is always a <see cref="ClaudeEvent.Result"/> unless the token
    /// cancels first (the child is killed with its tree). Throws <see cref="ClaudeStartException"/> before any event when
    /// the CLI cannot be started.
    /// </summary>
    IAsyncEnumerable<ClaudeEvent> RunAsync(ClaudeRequest request, CancellationToken cancellationToken);
}
