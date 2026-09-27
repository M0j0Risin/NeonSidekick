namespace NeonSidekick.Claude;

/// <summary>
/// The command line of one <c>/claude</c> child (2026-09-27), pure and tested as data. Always
/// <c>-p</c> (headless, the prompt read from stdin), <c>stream-json</c> out with the partial messages (the reply
/// streams as the local model's does; <c>--verbose</c> is what the CLI requires for that format under <c>-p</c>),
/// and <c>--permission-prompts none</c>: nothing can answer a prompt here, so whatever the level does not allow is
/// denied on the spot rather than hanging the run. <c>--session-id</c> opens the thread with the id this app minted,
/// <c>--resume</c> carries it on — so the thread survives this app's restarts with the session that stores the id.
/// The user's own Claude Code configuration (CLAUDE.md, skills, MCP servers, hooks) is loaded as if they ran it
/// themselves: nothing here narrows it (the brainstorm's default, 2026-09-27).
/// </summary>
public static class ClaudeArguments
{
    /// <summary>The tools of <see cref="ClaudePermissionLevel.ReadOnly"/>: read, search, fetch. Pinned.</summary>
    public const string ReadOnlyTools = "Read,Grep,Glob,WebSearch,WebFetch";

    public static IReadOnlyList<string> Build(ClaudeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--verbose",
            "--permission-prompts", "none",
            request.Resume ? "--resume" : "--session-id", request.SessionId,
        };

        switch (request.Permission)
        {
            case ClaudePermissionLevel.ReadOnly:
                arguments.Add("--tools");
                arguments.Add(ReadOnlyTools);
                break;
            case ClaudePermissionLevel.Edit:
                arguments.Add("--permission-mode");
                arguments.Add("acceptEdits");
                break;
            case ClaudePermissionLevel.Full:
                arguments.Add("--permission-mode");
                arguments.Add("bypassPermissions");
                break;
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.Effort))
        {
            arguments.Add("--effort");
            arguments.Add(request.Effort.Trim());
        }

        return arguments;
    }
}
