using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Pdf;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>convert_to_pdf(path | url | markdown, title?, to?, overwrite?, landscape?, paper?)</c> (2026-10-03, the user's ask): a PDF
/// in the working directory from a Markdown, text/code, HTML or picture file, a web page, or Markdown the model writes — through
/// <see cref="PdfConverter"/>, so the browser or Microsoft Print to PDF makes it, never the model's own bytes. One of the file
/// tools (it writes the sandbox, nothing else), so <c>File tools</c> rules it; a <c>url</c> also needs <c>Web tools</c>, read at
/// the call (the <c>download_file</c> rule). No confirm: a sandbox write like <c>write_file</c>'s. Not in plan mode.
/// </summary>
public sealed class ConvertToPdfTool : FileTool
{
    public const string ToolName = "convert_to_pdf";

    public const string UrlArgument = "url";
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
            "path": { "type": "string", "description": "The file to make a PDF of, relative to the working directory: Markdown (.md), any text or code file, HTML (.html), or a picture." },
            "url": { "type": "string", "description": "A web page (http or https) to make a PDF of, instead of path." },
            "markdown": { "type": "string", "description": "Markdown text to make a PDF of, instead of path: a report or notes written for the PDF." },
            "title": { "type": "string", "description": "With markdown: the document's title, shown at the top of each page and used to name the file." },
            "to": { "type": "string", "description": "The PDF's path or folder, relative to the working directory. Default: beside the source file with .pdf, or at the top for a url or markdown." },
            "overwrite": { "type": "boolean", "description": "Replace a PDF that is already there. Default false." },
            "landscape": { "type": "boolean", "description": "Lay the pages out landscape. Default false. Not for a url." },
            "paper": { "type": "string", "enum": ["letter", "a4", "legal"], "description": "The paper size. Default letter. Not for a url." }
          }
        }
        """);

    private readonly PdfConverter _pdf;
    private readonly Func<AppSettingsData> _effective;

    public ConvertToPdfTool(WorkingDirectory files, PdfConverter pdf, Func<AppSettingsData> effective) : base(files)
    {
        _pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    public override string Name => ToolName;

    public override string Description => DescriptionText;

    /// <summary>The description. Pinned.</summary>
    public const string DescriptionText =
        "Makes a PDF in the working directory from a file (Markdown, text or code, HTML, or a picture), a web page (url), or Markdown text you write (markdown). " +
        "Give exactly one of path, url or markdown. Markdown keeps its headings, tables, lists, links and coloured code; pictures in it come from the working directory. " +
        "It uses Edge, Chrome or Brave; without one, Microsoft Print to PDF, which takes Markdown, text and pictures only. " +
        "This is the way to make a PDF: never write PDF bytes yourself or run a script to make one.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (ReadOverwrite(arguments, out string raw) is not { } overwrite)
        {
            return FileText.BadBoolean(OverwriteArgument, raw);
        }

        if (ReadFlag(arguments, LandscapeArgument, out raw) is not { } landscape)
        {
            return FileText.BadBoolean(LandscapeArgument, raw);
        }

        PdfPaperSize? paper = null;
        string paperText = ToolArguments.ReadString(arguments, PaperArgument).Trim();
        if (paperText.Length > 0)
        {
            if (!PdfPaper.TryParse(paperText, out var parsed))
            {
                return FileText.BadChoice(PaperArgument, paperText, PdfText.PaperChoices);
            }

            paper = parsed;
        }

        string path = ReadPath(arguments).Trim();
        string urlText = ToolArguments.ReadString(arguments, UrlArgument).Trim();
        string markdown = ToolArguments.ReadString(arguments, MarkdownArgument);
        Uri? url = null;
        if (urlText.Length > 0)
        {
            if (!_effective().WebTools)
            {
                return PdfText.UrlNeedsWebTools;
            }

            url = WebFetcher.ParseUrl(urlText);
            if (url is null)
            {
                return WebText.NotHttp(urlText);
            }
        }

        string title = ToolArguments.ReadString(arguments, TitleArgument).Trim();
        var request = new PdfRequest(
            Path: path.Length > 0 ? path : null,
            Url: url,
            Markdown: string.IsNullOrWhiteSpace(markdown) ? null : markdown,
            Title: title.Length > 0 ? title : null,
            To: ToolArguments.ReadString(arguments, ToArgument).Trim() is { Length: > 0 } to ? to : null,
            Overwrite: overwrite,
            Landscape: landscape,
            Paper: paper);
        return await _pdf.ConvertAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
