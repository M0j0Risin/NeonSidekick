using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Render;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>render_picture(mermaid | svg | html, …)</c> (2026-11-16, plan sharded-hugging-puppy #9, the ideas file's #9: the model draws diagrams,
/// posters, cards and logos): Mermaid text drawn by the embedded Mermaid in its strict mode, or the model's SVG or HTML rebuilt with no script
/// and nothing fetched (<see cref="PicturePage"/>), each turned to a PNG in the headless browser (<see cref="BrowserPictureRenderer"/>). Saved as
/// <c>diagram.png</c> (Mermaid) or <c>picture.png</c>, or at <c>to</c>; the picture comes back for the model to check (<see cref="RenderTool"/>).
/// </summary>
public sealed class RenderPictureTool(WorkingDirectory files, PictureRendering rendering, WorkingDirectory? images = null) : RenderTool(files, images, rendering)
{
    public const string ToolName = "render_picture";

    public const string MermaidArgument = "mermaid";
    public const string SvgArgument = "svg";
    public const string HtmlArgument = "html";

    public const int DefaultWidth = 1200;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "mermaid": { "type": "string", "description": "A Mermaid diagram's text (flowchart, sequenceDiagram, classDiagram, stateDiagram, erDiagram, gantt, pie, mindmap, timeline…). Web icons (fa:) don't draw." },
            "svg": { "type": "string", "description": "Your own SVG document, inline: no script, no web addresses." },
            "html": { "type": "string", "description": "Your own HTML page with inline CSS: no script, no web fonts. Pictures from the working directory by name (<img src=\"photo.jpg\">)." },
            "to": { "type": "string", "description": "The .png to write, relative to the working directory. Default: diagram.png (Mermaid) or picture.png at the top, numbered when taken." },
            "overwrite": { "type": "boolean", "description": "Only with to: replace a file already there. Default false." },
            "width": { "type": "integer", "minimum": 200, "maximum": 4096, "description": "The page's width in CSS pixels. Default 1200; the height follows the content." },
            "scale": { "type": "number", "minimum": 1, "maximum": 3, "description": "Pixels per CSS pixel. Default 2, for crisp text." },
            "background": { "type": "string", "description": "white (default), transparent, black or a #RRGGBB color. A Mermaid diagram on a dark color takes its dark theme." }
          }
        }
        """);

    public override string Name => ToolName;

    public override string Description => RenderText.PictureDescription;

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string mermaid = ToolArguments.ReadString(arguments, MermaidArgument);
        string svg = ToolArguments.ReadString(arguments, SvgArgument);
        string html = ToolArguments.ReadString(arguments, HtmlArgument);
        int given = new[] { mermaid, svg, html }.Count(t => t.Trim().Length > 0);
        if (given != 1)
        {
            return RenderText.OneSource;
        }

        if (ReadOverwrite(arguments, out string raw) is not { } overwrite)
        {
            return FileText.BadBoolean(OverwriteArgument, raw);
        }

        if (ReadWidth(arguments, DefaultWidth, out string? error) is not { } width || ReadScale(arguments, out error) is not { } scale)
        {
            return error;
        }

        if (!ReadBackground(arguments, out string? background, out error))
        {
            return error;
        }

        string to = ToolArguments.ReadString(arguments, ToArgument).Trim();
        var job = mermaid.Trim().Length > 0
            ? new RenderJob(mermaid.Trim(), null, PictureFormat.Png, width, scale, background)
            : new RenderJob(null, svg.Trim().Length > 0 ? PicturePage.ForModelSvg(svg, Inline) : PicturePage.ForModelHtml(html, Inline), PictureFormat.Png, width, scale, background);
        return await DrawAndSaveAsync(job, to, overwrite, mermaid.Trim().Length > 0 ? "diagram" : "picture", [".png"], "", cancellationToken).ConfigureAwait(false);
    }
}
