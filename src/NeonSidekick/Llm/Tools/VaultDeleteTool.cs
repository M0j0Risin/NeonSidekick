using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>vault_delete(note)</c>: one note or attachment moved into the vault's <c>.trash</c>, Obsidian's "Move to Obsidian
/// trash" (2026-09-22, the user's ask). Offered only while the setting <c>Obsidian allow delete (.trash)</c> is on — off by
/// default, a bool rather than a <c>ToolsDisabled</c> default so a profile saved before it keeps it off too
/// (<see cref="App.ChatScreen.ObsidianToolsFor"/>) — and refused here as well when the setting is off at the call.
/// </summary>
public sealed class VaultDeleteTool : VaultTool
{
    public const string ToolName = "vault_delete";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "note": { "type": "string", "description": "The note: its name (Plan), a [[wikilink]], or its path in the vault (Projects/Plan.md); or an attachment by its path in the vault (assets/diagram.png)." }
          },
          "required": ["note"]
        }
        """);

    public VaultDeleteTool(ObsidianVault vault, Func<AppSettingsData> effective) : base(vault, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Deletes one note or attachment from " + ObsidianText.VaultWords + " by moving it where the vault's own setting sends deleted files (the system's bin, or the vault's .trash folder), where it can be restored; " +
        "links that pointed at it are left as they are and listed in the result. Use it only when the user asks for a deletion; never for a folder.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Effective.ObsidianAllowDelete)
        {
            return ValueTask.FromResult<object?>(ObsidianText.DeleteOff);
        }

        string note = ReadNote(arguments);
        return OffThread(() => Vault.Delete(note, cancellationToken), cancellationToken);
    }
}
