using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>vault_properties(note, set?, remove?)</c>: a note's properties (its YAML frontmatter), read or changed key by key (2026-09-22).</summary>
public sealed class VaultPropertiesTool : VaultTool
{
    public const string ToolName = "vault_properties";
    public const string SetArgument = "set";
    public const string RemoveArgument = "remove";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{NoteProperty}},
            "set": { "type": "object", "description": "Properties to add or change: names and values (a string, number, true/false, or a list), e.g. {\"status\": \"done\", \"tags\": [\"project\"]}." },
            "remove": { "type": "array", "items": { "type": "string" }, "description": "Property names to remove." }
          },
          "required": ["note"]
        }
        """);

    public VaultPropertiesTool(ObsidianVault vault, Func<AppSettingsData> effective) : base(vault, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Reads or changes a note's properties (its YAML frontmatter: tags, aliases, status, dates…) in " + ObsidianText.VaultWords + "; " +
        "without set or remove it lists them. Only the named keys' lines change, the rest stays as written.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadObjectList(arguments, SetArgument, out var objects, out _) || objects.Count > 1)
        {
            return new ValueTask<object?>(ObsidianText.BadSet);
        }

        if (!ToolArguments.TryReadStringList(arguments, RemoveArgument, out var remove, out _))
        {
            return new ValueTask<object?>(ObsidianText.BadRemove);
        }

        JsonElement? set = objects.Count == 1 ? objects[0] : null;
        string note = ReadNote(arguments);
        return OffThread(() => Vault.Properties(note, set, remove, cancellationToken), cancellationToken);
    }
}
