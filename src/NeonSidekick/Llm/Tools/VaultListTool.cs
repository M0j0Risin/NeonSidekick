using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>vault_list(what?, folder?, tag?, property?, value?, max_results?)</c>: the notes, the tags or the property keys, within filters (2026-09-22).</summary>
public sealed class VaultListTool : VaultTool
{
    public const string ToolName = "vault_list";
    public const string WhatArgument = "what";
    public const string PropertyArgument = "property";
    public const string ValueArgument = "value";

    public static readonly IReadOnlyList<string> WhatChoices = ["notes", "tags", "properties"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "what": { "type": "string", "enum": ["notes", "tags", "properties"], "description": "notes (the default): the notes' paths; tags: every tag with its count; properties: every property key with its count." },
            "folder": { "type": "string", "description": "Only notes under this folder of the vault." },
            "tag": { "type": "string", "description": "Only notes with this tag (or a tag nested under it)." },
            "property": { "type": "string", "description": "Only notes that have this property (its value is shown beside each note)." },
            "value": { "type": "string", "description": "With property: only notes whose property equals this (any item of a list), any case." },
            "max_results": { "type": "integer", "description": "The most rows to show (default 100, at most 500)." }
          }
        }
        """);

    public VaultListTool(ObsidianVault vault, Func<AppSettingsData> effective) : base(vault, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the notes of " + ObsidianText.VaultWords + " by folder, tag or property (e.g. property status, value draft), or every tag or property key with how many notes carry it.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryReadInt(arguments, VaultSearchTool.MaxResultsArgument, out var max, out string error))
        {
            return new ValueTask<object?>(error);
        }

        string whatText = ToolArguments.ReadString(arguments, WhatArgument).Trim().ToLowerInvariant();
        VaultListing what;
        switch (whatText)
        {
            case "" or "notes":
                what = VaultListing.Notes;
                break;
            case "tags":
                what = VaultListing.Tags;
                break;
            case "properties":
                what = VaultListing.Properties;
                break;
            default:
                return new ValueTask<object?>(ObsidianText.BadChoice(WhatArgument, whatText, WhatChoices));
        }

        string folder = ToolArguments.ReadString(arguments, VaultSearchTool.FolderArgument);
        string tag = ToolArguments.ReadString(arguments, VaultSearchTool.TagArgument);
        string property = ToolArguments.ReadString(arguments, PropertyArgument);
        string value = ToolArguments.ReadString(arguments, ValueArgument);
        return OffThread(() => Vault.List(what, folder, tag, property, value, max, cancellationToken), cancellationToken);
    }
}
