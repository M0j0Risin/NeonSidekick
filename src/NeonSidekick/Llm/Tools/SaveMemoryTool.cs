using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Memory;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The model's way into <see cref="MemoryStore"/>: <c>save_memory(text)</c>. The pattern every
/// tool follows: a hand-written schema parsed once (<see cref="ToolSchema"/>), <c>Name</c>,
/// <c>Description</c> and <c>JsonSchema</c> overridden, the argument read by hand
/// (<see cref="ToolArguments"/>) out of whatever the adapter delivers. Never
/// <c>AIFunctionFactory</c> (reflection). The property name in the schema and the key read below
/// must match; nothing checks that at compile time.
///
/// <para>The result is a sentence for the model and, verbatim, the one dim line the transcript
/// shows (<c>🛠️ remembered: …</c>), so a wrong save is visible and <c>/memory</c> can undo it — the row, or <c>/memory forget</c> for the lot.</para>
/// </summary>
public sealed class SaveMemoryTool : AIFunction
{
    public const string ToolName = "save_memory";

    private const string ArgumentName = "text";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "text": {
              "type": "string",
              "description": "One short sentence about the user, in the third person: \"Their name is Chris.\", \"They prefer replies in metric units.\""
            }
          },
          "required": ["text"]
        }
        """);

    private readonly MemoryStore _store;

    public SaveMemoryTool(MemoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Saves one lasting fact about the user to long-term memory, so it is known in every later session. Not for passing details.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The tool's answer, pinned: what the model reads and what the transcript shows.</summary>
    public static string Describe(MemoryAddResult result) => result.Outcome switch
    {
        MemoryAddOutcome.Added => "remembered: " + result.Text,
        MemoryAddOutcome.Duplicate => "already remembered: " + result.Text,
        MemoryAddOutcome.Disabled => "not saved: the user has disabled this memory (it is kept but not offered to you); do not save it again: " + result.Text,   // 2026-10-13, the code review
        MemoryAddOutcome.Full => $"memory is full ({MemoryStore.MaxEntries.ToString(CultureInfo.InvariantCulture)} entries); the user can clear it with /memory forget",
        MemoryAddOutcome.Empty => "nothing to remember: the text was empty",
        _ => "could not save the memory (the file could not be written); tell the user",
    };

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Describe(_store.Add(ReadText(arguments))));
    }

    /// <summary>The <c>text</c> argument however it arrived: a <see cref="JsonElement"/> from the wire, or a string from a test.</summary>
    internal static string ReadText(AIFunctionArguments arguments) => ToolArguments.ReadString(arguments, ArgumentName);
}
