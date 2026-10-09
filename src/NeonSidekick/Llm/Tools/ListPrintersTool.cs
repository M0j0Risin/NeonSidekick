using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Printing;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>list_printers()</c> (2026-09-28): the installed printers, the Windows default and the <c>Print default printer</c> setting
/// marked, so the model can name one to <c>print_file</c>. Read-only: plan mode keeps it, and no policy judges it.
/// </summary>
public sealed class ListPrintersTool : AIFunction
{
    public const string ToolName = "list_printers";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {}
        }
        """);

    private readonly PrintService _print;

    public ListPrintersTool(PrintService print)
    {
        _print = print ?? throw new ArgumentNullException(nameof(print));
    }

    public override string Name => ToolName;

    public override string Description => OperatingSystem.IsMacOS() ? MacDescriptionText : DescriptionText;

    /// <summary>The description on Windows. Pinned.</summary>
    public const string DescriptionText = "Lists the printers installed on the user's PC, marking their default, for print_file.";

    /// <summary>The description on a Mac (2026-10-08): CUPS' queue names with the names System Settings shows. Pinned.</summary>
    public const string MacDescriptionText = "Lists the printers set up on the user's Mac (each queue name with the name System Settings shows), marking their default, for print_file.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var printers = await Task.Run(_print.Printers, cancellationToken).ConfigureAwait(false);
        return PrintText.PrinterList(printers, _print.Effective.PrintDefaultPrinter);
    }
}
