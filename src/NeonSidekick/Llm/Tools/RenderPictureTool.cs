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
/// A Markdown fence around the text is taken off (2026-11-17, the admin-themes-render review's #187); pictures the page names that weren't
/// found are named in the answer (#165).
/// </summary>
public sealed class RenderPictureTool(WorkingDirectory files, PictureRendering rendering, WorkingDirectory? images = null) : RenderTool(files, images, rendering)
{
    public const string ToolName = "render_picture";

    public const string MermaidArgument = "mermaid";
    public const string SvgArgument = "svg";
    public const string HtmlArgument = "html";

    public const int DefaultWidth = 1200;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "mermaid": { "type": "string", "description": "A Mermaid diagram's text (flowchart, sequenceDiagram, classDiagram, stateDiagram, erDiagram, gantt, pie, mindmap, timeline…). Web icons (fa:) don't draw." },
            "svg": { "type": "string", "description": "Your own SVG document, inline: no script, no web addresses." },
            "html": { "type": "string", "description": "Your own HTML page with inline CSS: no script, no web fonts. Pictures from the working directory by name (<img src=\"photo.jpg\">)." },
            "to": { "type": "string", "description": "The .png to write, relative to the working directory. Default: diagram.png (Mermaid) or picture.png at the top, numbered when taken." },
            "overwrite": { "type": "boolean", "description": "Only with to: replace a file already there. Default false." },
            "width": { "type": "integer", "minimum": {{MinWidth}}, "maximum": {{MaxWidth}}, "description": "The page's width in CSS pixels. Default 1200; the height follows the content." },
            "scale": { "type": "number", "minimum": 1, "maximum": 3, "description": "Pixels per CSS pixel. Default 2, for crisp text." },
            "background": { "type": "string", "description": "white (default), transparent, black or a color (#RRGGBB, #RRGGBBAA or a name). A Mermaid diagram on a dark color takes its dark theme." }
          }
        }
        """);

    public override string Name => ToolName;

    public override string Description => RenderText.PictureDescription;

    public override JsonElement JsonSchema => Schema;

    /// <summary>
    /// <paramref name="text"/> without a Markdown fence around it (#187): when the trimmed text's first line is three or more backticks or
    /// tildes (and an optional language word) and its last line a run of the same mark at least as long, the lines between; otherwise the
    /// text unchanged, a fence that never closes included, so Mermaid's own error names it. Pure.
    /// </summary>
    public static string Unfence(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.Trim().ReplaceLineEndings("\n").Split('\n');
        if (lines.Length < 2 || lines[0].Length < 3 || lines[0][0] is not ('`' or '~'))
        {
            return text;
        }

        char mark = lines[0][0];
        int run = lines[0].TakeWhile(c => c == mark).Count();
        string word = lines[0][run..].Trim();
        string last = lines[^1].Trim();
        if (run < 3 || word.Contains(' ', StringComparison.Ordinal) || word.Contains(mark, StringComparison.Ordinal)
            || last.Length < run || last.Any(c => c != mark))
        {
            return text;
        }

        return string.Join('\n', lines[1..^1]);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string mermaid = Unfence(ToolArguments.ReadString(arguments, MermaidArgument));
        string svg = Unfence(ToolArguments.ReadString(arguments, SvgArgument));
        string html = Unfence(ToolArguments.ReadString(arguments, HtmlArgument));
        int given = new[] { mermaid, svg, html }.Count(t => t.Trim().Length > 0);
        if (given != 1)
        {
            return RenderText.OneSource;
        }

        if (!ReadTarget(arguments, out string to, out bool overwrite, out string? error))
        {
            return error;
        }

        if (ReadWidth(arguments, DefaultWidth, out error) is not { } width || ReadScale(arguments, out error) is not { } scale)
        {
            return error;
        }

        if (!ReadBackground(arguments, out var background, out error))
        {
            return error;
        }

        var pictures = Pictures();
        var job = mermaid.Trim().Length > 0
            ? new RenderJob(mermaid.Trim(), null, PictureFormat.Png, width, scale, background, Svg: true)
            : svg.Trim().Length > 0
                ? new RenderJob(null, PicturePage.ForModelSvg(svg, pictures.Bytes), PictureFormat.Png, width, scale, background, Svg: true)
                : new RenderJob(null, PicturePage.ForModelHtml(html, pictures.Bytes), PictureFormat.Png, width, scale, background, Svg: false);
        string notes = pictures.Missed.Count > 0 ? RenderText.PicturesLeftOut(pictures.Missed) : "";
        return await DrawAndSaveAsync(job, to, overwrite, mermaid.Trim().Length > 0 ? "diagram" : "picture", [".png"], notes, cancellationToken).ConfigureAwait(false);
    }
}
