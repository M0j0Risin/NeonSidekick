using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// A tool with a twin that reads everything redacted (2026-10-15, the admin-tools plan: the event logs' <c>AsRedacted</c> lifted out so
/// the services, tasks and directory tools have one too). <c>execute_code</c>'s bridge (<see cref="ExecuteCodeTool"/>) and the advisors
/// (<see cref="NeonAdvisorTool.ReadOnlyOf"/>) swap every such tool for its twin, so a computer's or a domain's <c>full</c> detail stays the
/// main chat's alone; the bots are built with the twins from the start.
/// </summary>
public interface IRedactedTwin
{
    /// <summary>The same tool reading everything redacted; itself when it already does.</summary>
    AIFunction AsRedacted();
}

/// <summary>The swap both callers make. Pure.</summary>
public static class RedactedTwins
{
    /// <summary><paramref name="tool"/>'s redacted twin when it has one, else itself.</summary>
    public static AIFunction Of(AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool is IRedactedTwin twin ? twin.AsRedacted() : tool;
    }
}
