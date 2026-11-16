using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// A tool that lists as well as changes, with a twin that only lists (2026-11-15, the tool-arguments review's #7 and #106, the user's call:
/// plan mode and <c>/undo</c> class <c>gitlib_tag</c>, <c>gitlib_branch</c> and <c>gitlib_stash</c> by the call). Plan mode offers the twin
/// (<c>ChatScreen.ComposeTurnTools</c> swaps it in wherever it narrows), and the turn loop asks it <see cref="Refusal"/> before the call runs,
/// so a write form is refused with nothing run and nothing journaled. <see cref="IRedactedTwin"/>'s pattern.
/// </summary>
public interface IListOnlyTwin
{
    /// <summary>The same tool refusing everything but its list form; itself when it already does.</summary>
    AIFunction AsListOnly();

    /// <summary>The refusal of a call that isn't the list form, when this is the twin; null for the list form, and always for the full tool.</summary>
    string? Refusal(IDictionary<string, object?>? arguments);
}

/// <summary>The swap plan mode makes. Pure.</summary>
public static class ListOnlyTwins
{
    /// <summary><paramref name="tool"/>'s list-only twin when it has one, else itself.</summary>
    public static AIFunction Of(AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool is IListOnlyTwin twin ? twin.AsListOnly() : tool;
    }

    /// <summary><see cref="Of"/> over a group; null stays null.</summary>
    public static IReadOnlyList<AIFunction>? Of(IReadOnlyList<AIFunction>? tools) => tools?.Select(Of).ToList();
}
