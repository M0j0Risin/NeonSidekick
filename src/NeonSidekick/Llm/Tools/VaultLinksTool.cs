using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>vault_links(note)</c>: a note's outgoing links and embeds, resolved or not, and its backlinks (2026-09-22).</summary>
public sealed class VaultLinksTool : VaultTool
{
    public const string ToolName = "vault_links";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{NoteProperty}}
          },
          "required": ["note"]
        }
        """);

    public VaultLinksTool(ObsidianVault vault, Func<AppSettingsData> effective) : base(vault, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows a note's links in " + ObsidianText.VaultWords + ": every link and embed it makes and the note each resolves to (or unresolved), " +
        "and every backlink — the notes that link to it, with the line.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string note = ReadNote(arguments);
        return OffThread(() => Vault.Links(note, cancellationToken), cancellationToken);
    }
}
