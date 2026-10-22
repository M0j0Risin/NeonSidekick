using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>vault_daily(date?, append?)</c>: the daily note for a day, created from the vault's template when missing, with text appended when given (2026-09-22).</summary>
public sealed class VaultDailyTool : VaultTool
{
    public const string ToolName = "vault_daily";
    public const string DateArgument = "date";
    public const string AppendArgument = "append";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "date": { "type": "string", "description": "The day: YYYY-MM-DD, today (the default), yesterday, tomorrow, or +N / -N days." },
            "append": { "type": "string", "description": "Markdown to add at the end of that day's note." }
          }
        }
        """);

    public VaultDailyTool(ObsidianVault vault, Func<AppSettingsData> effective) : base(vault, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Opens the daily note for a day in " + ObsidianText.VaultWords + " — in the folder and date format the vault's Daily notes settings name, " +
        "created from its template when missing, as Obsidian does — optionally appending to it; returns its text.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string date = ToolArguments.ReadString(arguments, DateArgument);
        string append = ToolArguments.ReadString(arguments, AppendArgument);
        return OffThread(() => Vault.Daily(date, append, cancellationToken), cancellationToken);
    }
}
