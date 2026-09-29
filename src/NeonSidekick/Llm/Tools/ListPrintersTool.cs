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

    public override string Description => "Lists the printers installed on the user's PC, marking their default, for print_file.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var printers = await Task.Run(_print.Printers, cancellationToken).ConfigureAwait(false);
        return PrintText.PrinterList(printers, _print.Effective.PrintDefaultPrinter);
    }
}
