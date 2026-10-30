using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Office;
using NeonSidekick.Pdf;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>convert_to_docx(path | markdown, title?, to?, overwrite?, paper?, landscape?)</c> (2026-10-29, the Office writing plan,
/// <see cref="ConvertToPdfTool"/>'s twin): a Word document in the working directory from a Markdown or text file, or Markdown the model
/// writes, through <see cref="DocxConverter"/> (shared with <c>/docx</c>), never the model's own bytes. One of the file tools, so *File
/// tools* rules it; quiet, so the Markdown a call carries never reaches the transcript. Not in plan mode.
/// </summary>
public sealed class ConvertToDocxTool : FileTool, IRefusesPrunedStubs
{
    /// <inheritdoc/>
    /// <remarks>
    /// The Markdown is the tool's own to refuse (2026-10-29, review P7-10): its <see cref="FileText.PrunedStubSent"/> names the argument, where
    /// the generic refusal didn't reach the model.
    /// </remarks>
    public IReadOnlyCollection<string> StubArguments { get; } = [MarkdownArgument];

    public const string ToolName = "convert_to_docx";

    public const string MarkdownArgument = "markdown";
    public const string TitleArgument = "title";
    public const string ToArgument = "to";
    public const string LandscapeArgument = "landscape";
    public const string PaperArgument = "paper";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The Markdown (.md) or text (.txt) file to make a Word document of, relative to the working directory." },
            "markdown": { "type": "string", "description": "Markdown text to make a Word document of, instead of path: a report or notes written for it." },
            "title": { "type": "string", "description": "The document's title, shown at the top of each page; with markdown it names the file too. Default: the first # heading." },
            "to": { "type": "string", "description": "The .docx path or folder, relative to the working directory. Default: beside the source file with .docx, or at the top for markdown." },
            "overwrite": { "type": "boolean", "description": "Only with to: replace a document that is already there (a name the tool makes up never replaces one). Default false." },
            "paper": { "type": "string", "enum": ["letter", "a4", "legal"], "description": "The paper size. Default letter." },
            "landscape": { "type": "boolean", "description": "Lay the pages out landscape. Default false." }
          }
        }
        """);

    private readonly DocxConverter _docx;

    public ConvertToDocxTool(WorkingDirectory files, DocxConverter docx) : base(files)
    {
        _docx = docx ?? throw new ArgumentNullException(nameof(docx));
    }

    public override string Name => ToolName;

    /// <summary>The description. Pinned.</summary>
    public const string DescriptionText =
        "Makes a Word document (.docx) in the working directory from a Markdown or text file (path) or Markdown text you write (markdown). " +
        "Give exactly one of path or markdown. Headings, lists, tables, links, quotes and code blocks become Word's own; pictures in it come from the working directory. " +
        "This is the way to make a Word document: never write .docx bytes yourself or run a script to make one.";

    public override string Description => DescriptionText;

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (ReadOverwrite(arguments, out string raw) is not { } overwrite)
        {
            return new(FileText.BadBoolean(OverwriteArgument, raw));
        }

        if (ReadFlag(arguments, LandscapeArgument, out raw) is not { } landscape)
        {
            return new(FileText.BadBoolean(LandscapeArgument, raw));
        }

        PdfPaperSize? paper = null;
        string paperText = ToolArguments.ReadString(arguments, PaperArgument).Trim();
        if (paperText.Length > 0)
        {
            if (!PdfPaper.TryParse(paperText, out var parsed))
            {
                return new(FileText.BadChoice(PaperArgument, paperText, PdfText.PaperChoices));
            }

            paper = parsed;
        }

        string markdown = ToolArguments.ReadString(arguments, MarkdownArgument);
        if (ConversationCompactor.IsPrunedStub(markdown))
        {
            return new(FileText.PrunedStubSent(MarkdownArgument));
        }

        string path = ReadPath(arguments).Trim();
        string title = ToolArguments.ReadString(arguments, TitleArgument).Trim();
        var request = new DocxRequest(
            Path: path.Length > 0 ? path : null,
            Markdown: string.IsNullOrWhiteSpace(markdown) ? null : markdown,
            Title: title.Length > 0 ? title : null,
            To: ToolArguments.ReadString(arguments, ToArgument).Trim() is { Length: > 0 } to ? to : null,
            Overwrite: overwrite,
            Landscape: landscape,
            Paper: paper);
        return new(ConvertAsync(request, cancellationToken));
    }

    /// <summary>
    /// The conversion off the turn's thread, so Esc returns the turn at once (2026-10-30, review F52, as <c>image_edit</c> does): the pool's
    /// run stops at its next block or picture and drops its unfinished file.
    /// </summary>
    private async Task<object?> ConvertAsync(DocxRequest request, CancellationToken cancellationToken) =>
        await Task.Run(() => _docx.Convert(request, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
}
