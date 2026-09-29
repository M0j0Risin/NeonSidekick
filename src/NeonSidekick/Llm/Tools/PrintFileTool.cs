using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Printing;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>print_file(path, printer?, copies?, pages?, landscape?)</c> (2026-09-28, the user's ask): a file of the working directory
/// on paper. Text and code print as a listing, markdown styled, a picture fitted to a page — all drawn by the app on the chosen
/// printer; anything else (a PDF, an Office file) goes to the program Windows has for it, on the Windows default printer. Behind
/// <see cref="PrintPolicy"/>: under <c>ask</c> (the default) every call waits for the user's yes on the pane, which names the file,
/// the printer and the sheets (<see cref="PrintText.ConfirmQuestion"/>); <c>confirm</c> is the advisor's seam — true yes, false no,
/// null nothing could ask — and itself null where nothing ever can (headless).
/// </summary>
public sealed class PrintFileTool : AIFunction
{
    public const string ToolName = "print_file";
    public const string PrinterArgument = "printer";
    public const string CopiesArgument = "copies";
    public const string PagesArgument = "pages";
    public const string LandscapeArgument = "landscape";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file to print, relative to the working directory." },
            "printer": { "type": "string", "description": "A printer's name from list_printers. Leave it out for the user's default printer." },
            "copies": { "type": "integer", "description": "How many copies, 1 to 10. Default 1." },
            "pages": { "type": "string", "description": "Which pages, such as 1-3, 4- or 1,3,5. Default all." },
            "landscape": { "type": "boolean", "description": "Turn the page sideways. Default false." }
          },
          "required": ["path"]
        }
        """);

    private readonly PrintService _print;
    private readonly Func<string, CancellationToken, Task<bool?>>? _confirm;

    public PrintFileTool(PrintService print, Func<string, CancellationToken, Task<bool?>>? confirm)
    {
        _print = print ?? throw new ArgumentNullException(nameof(print));
        _confirm = confirm;
    }

    public override string Name => ToolName;

    public override string Description =>
        "Prints a file from the working directory on the user's printer: text and code as a listing, markdown formatted, a picture on one page; " +
        "a PDF or an Office file goes to its own program on the default printer. Only print when the user asks you to.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (PrintPolicy.Judge(_print.Effective) == PrintVerdict.Refuse)
        {
            return PrintText.PolicyOff;
        }

        string path = ToolArguments.ReadString(arguments, FileTool.PathArgument).Trim();
        if (path.Length == 0)
        {
            return Files.FileText.PathRequired(FileTool.PathArgument);
        }

        if (!ToolArguments.TryReadInt32(arguments, CopiesArgument, out int? copies, out string rawCopies) || copies is < 1 or > PrintText.MaxCopies)
        {
            return PrintText.BadCopies(rawCopies.Length > 0 ? rawCopies : copies?.ToString(CultureInfo.InvariantCulture) ?? "");
        }

        if (!ToolArguments.TryReadBoolean(arguments, LandscapeArgument, out bool? landscape, out string rawLandscape))
        {
            return Files.FileText.BadBoolean(LandscapeArgument, rawLandscape);
        }

        string? printer = ToolArguments.ReadString(arguments, PrinterArgument).Trim() is { Length: > 0 } named ? named : null;
        string? pages = ToolArguments.ReadString(arguments, PagesArgument).Trim() is { Length: > 0 } range ? range : null;
        var request = new PrintRequest(path, printer, copies ?? 1, pages, landscape ?? false);
        var (plan, error) = await Task.Run(() => _print.Prepare(request), cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return error;
        }

        if (PrintPolicy.Judge(_print.Effective) == PrintVerdict.Ask)
        {
            bool? yes = _confirm is null ? null : await _confirm(PrintText.ConfirmQuestion(plan), cancellationToken).ConfigureAwait(false);
            switch (yes)
            {
                case false:
                    return PrintText.Declined;
                case null:
                    return PrintText.NotAsked;
            }
        }

        return await _print.PrintAsync(plan, cancellationToken).ConfigureAwait(false);
    }
}
