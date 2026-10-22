using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>vault_read(note, heading?, start_line?, max_lines?)</c>: a note found by name or path, whole, one heading's section, or a window of lines (2026-09-22).</summary>
public sealed class VaultReadTool : VaultTool
{
    public const string ToolName = "vault_read";
    public const string HeadingArgument = "heading";
    public const string StartLineArgument = "start_line";
    public const string MaxLinesArgument = "max_lines";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{NoteProperty}},
            "heading": { "type": "string", "description": "Read only this heading's section (its text, without the #s)." },
            "start_line": { "type": "integer", "description": "The first line to read (a line of the whole note)." },
            "max_lines": { "type": "integer", "description": "The most lines to read." }
          },
          "required": ["note"]
        }
        """);

    public VaultReadTool(ObsidianVault vault, Func<AppSettingsData> effective) : base(vault, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Reads a note from " + ObsidianText.VaultWords + ", found by name, [[wikilink]] or path the way Obsidian resolves links: " +
        "the whole note with its properties, one heading's section, or a window of lines (a partial read names the line to continue from).";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryReadInt(arguments, StartLineArgument, out var start, out string error) || !TryReadInt(arguments, MaxLinesArgument, out var max, out error))
        {
            return new ValueTask<object?>(error);
        }

        string note = ReadNote(arguments);
        string heading = ToolArguments.ReadString(arguments, HeadingArgument);
        return OffThread(() => Vault.Read(note, heading, start, max, cancellationToken), cancellationToken);
    }
}
