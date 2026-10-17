using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>vault_move(note, to)</c>: rename or move a note and rewrite every link to it, Obsidian's "update internal links" (2026-09-22).</summary>
public sealed class VaultMoveTool : VaultTool
{
    public const string ToolName = "vault_move";
    public const string ToArgument = "to";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{NoteProperty}},
            "to": { "type": "string", "description": "The new name (renames it in its folder), a folder ending in / (moves it there), or the new path in the vault." }
          },
          "required": ["note", "to"]
        }
        """);

    public VaultMoveTool(ObsidianVault vault, Func<AppSettingsData> effective) : base(vault, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Renames or moves a note in " + ObsidianText.VaultWords + " and rewrites every [[wikilink]], embed and Markdown link that pointed at it, " +
        "so nothing breaks; use it rather than the file tools to rename a note.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string note = ReadNote(arguments);
        string to = ToolArguments.ReadString(arguments, ToArgument);
        // A folder ending in / keeps the note's own name (2026-10-17, the shopping list's tripwire): that is the name judged.
        string landing = to.EndsWith('/') ? to + note.Split('/', '\\')[^1] : to;
        if (ShoppingListRefusal(landing) is { } refused)
        {
            return new ValueTask<object?>(refused);
        }

        return OffThread(() => Vault.Move(note, to), cancellationToken);
    }
}
