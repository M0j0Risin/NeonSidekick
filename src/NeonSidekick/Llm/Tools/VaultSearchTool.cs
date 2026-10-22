using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>vault_search(query, tag?, folder?, max_results?)</c>: the lines and note names holding a text, any case (2026-09-22).</summary>
public sealed class VaultSearchTool : VaultTool
{
    public const string ToolName = "vault_search";
    public const string QueryArgument = "query";
    public const string TagArgument = "tag";
    public const string FolderArgument = "folder";
    public const string MaxResultsArgument = "max_results";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "The text to find, any case." },
            "tag": { "type": "string", "description": "Only notes with this tag (or a tag nested under it), with or without the #." },
            "folder": { "type": "string", "description": "Only notes under this folder of the vault." },
            "max_results": { "type": "integer", "description": "The most matching lines to show (default 50, at most 200)." }
          },
          "required": ["query"]
        }
        """);

    public VaultSearchTool(ObsidianVault vault, Func<AppSettingsData> effective) : base(vault, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Searches " + ObsidianText.VaultWords + " for a text: every matching line as path:line, and every note whose name or alias holds it; " +
        "narrowed to a tag or a folder when given.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryReadInt(arguments, MaxResultsArgument, out var max, out string error))
        {
            return new ValueTask<object?>(error);
        }

        string query = ToolArguments.ReadString(arguments, QueryArgument);
        string tag = ToolArguments.ReadString(arguments, TagArgument);
        string folder = ToolArguments.ReadString(arguments, FolderArgument);
        return OffThread(() => Vault.Search(query, tag, folder, max, cancellationToken), cancellationToken);
    }
}
