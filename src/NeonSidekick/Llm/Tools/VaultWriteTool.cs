using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>vault_write(note, content, mode?, heading?)</c>: create a note, replace it, or add to its end, its top or one heading's section (2026-09-22).</summary>
public sealed class VaultWriteTool : VaultTool, IRefusesPrunedStubs
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> StubArguments { get; } = [ContentArgument];

    public const string ToolName = "vault_write";
    public const string ContentArgument = "content";
    public const string ModeArgument = "mode";

    public static readonly IReadOnlyList<string> ModeChoices = ["create", "overwrite", "append", "prepend"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{NoteProperty}},
            "content": { "type": "string", "description": "The Markdown to write." },
            "mode": { "type": "string", "enum": ["create", "overwrite", "append", "prepend"], "description": "create (the default): a new note, refused if it exists; overwrite: replace the whole note; append: add at the end (of heading's section when given); prepend: add after the properties (or right under heading)." },
            "heading": { "type": "string", "description": "With append or prepend: the heading whose section gets the content." }
          },
          "required": ["note", "content"]
        }
        """);

    public VaultWriteTool(ObsidianVault vault, Func<AppSettingsData> effective) : base(vault, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Writes a note in " + ObsidianText.VaultWords + ": creates one (a bare name goes where Obsidian puts new notes), replaces it, or appends or prepends " +
        "to it or to one heading's section; the note keeps its line endings. Use [[wikilinks]] and #tags as Obsidian does.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string modeText = ToolArguments.ReadString(arguments, ModeArgument).Trim().ToLowerInvariant();
        VaultWriteMode mode;
        switch (modeText)
        {
            case "" or "create":
                mode = VaultWriteMode.Create;
                break;
            case "overwrite":
                mode = VaultWriteMode.Overwrite;
                break;
            case "append":
                mode = VaultWriteMode.Append;
                break;
            case "prepend":
                mode = VaultWriteMode.Prepend;
                break;
            default:
                return new ValueTask<object?>(ObsidianText.BadChoice(ModeArgument, modeText, ModeChoices));
        }

        string note = ReadNote(arguments);
        if (ShoppingListRefusal(note) is { } refused)
        {
            return new ValueTask<object?>(refused);
        }

        // A missing content is not "" (2026-10-17, the toki-pona session's write_file: read as empty it replaced a whole file with nothing).
        if (!ToolArguments.Has(arguments, ContentArgument))
        {
            return new ValueTask<object?>(ObsidianText.Required(ContentArgument));
        }

        if (WriteFileTool.StubSent(arguments, ContentArgument) is { } stub)
        {
            return new ValueTask<object?>(stub);
        }

        string content = ToolArguments.ReadString(arguments, ContentArgument);
        string heading = ToolArguments.ReadString(arguments, VaultReadTool.HeadingArgument);
        // A heading names where an append or prepend goes; create and overwrite write the whole note, so one there is refused (2026-10-22,
        // review X1: "replace section Tasks" rewrote the whole note and answered "replaced Plan.md").
        if (heading.Trim().Length > 0 && mode is VaultWriteMode.Create or VaultWriteMode.Overwrite)
        {
            return new ValueTask<object?>(ObsidianText.HeadingNeedsInsert);
        }

        return OffThread(() => Vault.Write(note, content, mode, heading, cancellationToken), cancellationToken);
    }
}
