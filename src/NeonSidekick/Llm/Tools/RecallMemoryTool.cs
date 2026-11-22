using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Memory;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>recall_memory()</c>: everything in <see cref="MemoryStore"/>, oldest first, as
/// <see cref="MemoryPrompt.Recalled"/> lays it out. The read side of memory (2026-09-17): the
/// list rides this tool's result rather than the system prompt while tools are on — seeded as the
/// last opening pair of a conversation (<see cref="Assistant.OpeningMemoryCallId"/>) and kept
/// current there at every turn, so the facts are the freshest context before the first reply
/// (a list far back in the system prompt went unread on the first turn) — and offered, so the
/// model can read it again after a save or once the pair has aged out of a long conversation.
/// </summary>
public sealed class RecallMemoryTool : AIFunction
{
    public const string ToolName = "recall_memory";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {}
        }
        """);

    private readonly MemoryStore _store;

    private readonly Func<string?>? _folder;

    /// <param name="folder">The working folder whose memories join the everywhere ones (2026-11-21, feature idea #6), read at each call, so a
    /// <c>/cwd</c> changes the list at the next turn; null offers the everywhere ones alone.</param>
    public RecallMemoryTool(MemoryStore store, Func<string?>? folder = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _folder = folder;
    }

    /// <summary>The folder the list is for now; null for a tool built without one.</summary>
    public string? Folder => _folder?.Invoke();

    public override string Name => ToolName;

    public override string Description =>
        "Everything you remember, oldest first: the facts saved with " + SaveMemoryTool.ToolName + " in any session for everywhere, and those saved for this folder only " +
        "(another folder's are left out). " +
        "Its result opens every conversation; call it again after a save or when the list is no longer in view.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The list as the model reads it (<see cref="MemoryPrompt.Recalled"/>), from the store as it stands now.</summary>
    public string Describe() => MemoryPrompt.Recalled(_store.Snapshot(Folder));

    /// <summary>
    /// The transcript's one dim line for a result — <c>nothing remembered yet</c>, <c>1 memory recalled</c>,
    /// <c>12 memories recalled</c> — counted off the result's own bullets, so the screen never reads the
    /// store. Pinned. The list itself is <c>/memory</c>'s to show.
    /// </summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int count = 0;
        int at = 0;
        while ((at = result.IndexOf("\n- ", at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += 3;
        }

        return count switch
        {
            0 => "nothing remembered yet",
            1 => "1 memory recalled",
            _ => count.ToString(CultureInfo.InvariantCulture) + " memories recalled",
        };
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Describe());
    }
}
