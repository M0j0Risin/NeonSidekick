using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Office;
using NeonSidekick.Render;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>render_chart(kind, data | from, x, y, …)</c> (2026-11-16, plan sharded-hugging-puppy #9, the user's call: the app draws the chart as SVG
/// itself, <see cref="ChartSvg"/>, and the browser only turns it into pixels): a bar, line or pie chart of a table given as text or read from a
/// <c>.csv</c>, <c>.tsv</c> or <c>.json</c> file (<see cref="TableSource"/>, its columns typed as <c>convert_to_xlsx</c> types them). At most
/// <see cref="ChartSvg.MaxPoints"/> rows and <see cref="ChartSvg.MaxSeries"/> series. Saved as <c>chart.png</c>, or at <c>to</c>.
/// </summary>
public sealed class RenderChartTool(WorkingDirectory files, PictureRendering rendering, WorkingDirectory? images = null) : RenderTool(files, images, rendering)
{
    public const string ToolName = "render_chart";

    public const string KindArgument = "kind";
    public const string DataArgument = "data";
    public const string FromArgument = "from";
    public const string XArgument = "x";
    public const string YArgument = "y";
    public const string HorizontalArgument = "horizontal";
    public const string StackedArgument = "stacked";
    public const string TitleArgument = "title";
    public const string XLabelArgument = "x_label";
    public const string YLabelArgument = "y_label";
    public const string StyleArgument = "style";

    public const int DefaultWidth = 1200;

    private static readonly string[] Kinds = ["bar", "line", "pie"];
    private static readonly string[] Styles = ["light", "dark"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["bar", "line", "pie"], "description": "The chart: bar, line or pie." },
            "data": { "type": "string", "description": "The table as CSV text with a header row, or a JSON array of objects (or of arrays, the header first)." },
            "from": { "type": "string", "description": "Instead of data: a .csv, .tsv or .json file in the working directory." },
            "x": { "type": "string", "description": "The column of labels: categories, names or dates along the axis (a pie's slice names)." },
            "y": { "type": "array", "items": { "type": "string" }, "description": "The number columns to draw, one series each (a pie takes one)." },
            "horizontal": { "type": "boolean", "description": "bar only: bars run left to right. Default false." },
            "stacked": { "type": "boolean", "description": "bar only, with two or more y: the series stacked in one bar. Default false." },
            "title": { "type": "string", "description": "A title over the chart." },
            "x_label": { "type": "string", "description": "The label axis's title." },
            "y_label": { "type": "string", "description": "The value axis's title." },
            "style": { "type": "string", "enum": ["light", "dark"], "description": "light (default) or dark ground." },
            "to": { "type": "string", "description": "The .png to write, relative to the working directory. Default: chart.png at the top, numbered when taken." },
            "overwrite": { "type": "boolean", "description": "Only with to: replace a file already there. Default false." },
            "width": { "type": "integer", "minimum": 200, "maximum": 4096, "description": "The chart's width in CSS pixels. Default 1200; the height is three fifths of it." },
            "scale": { "type": "number", "minimum": 1, "maximum": 3, "description": "Pixels per CSS pixel. Default 2." }
          },
          "required": ["kind", "x", "y"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => RenderText.ChartDescription;

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var (job, notes, error) = Prepare(arguments, cancellationToken);
        if (job is null)
        {
            return error;
        }

        if (ReadOverwrite(arguments, out string raw) is not { } overwrite)
        {
            return FileText.BadBoolean(OverwriteArgument, raw);
        }

        string to = ToolArguments.ReadString(arguments, ToArgument).Trim();
        return await DrawAndSaveAsync(job, to, overwrite, "chart", [".png"], notes, cancellationToken).ConfigureAwait(false);
    }

    // The table read, the data taken from it, and the chart's SVG as a page: the job, the notes for the answer, or the refusal.
    private (RenderJob? Job, string Notes, string? Error) Prepare(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string kindText = ToolArguments.ReadString(arguments, KindArgument).Trim().ToLowerInvariant();
        int kindIndex = Array.IndexOf(Kinds, kindText);
        if (kindIndex < 0)
        {
            return (null, "", RenderText.BadChoice(KindArgument, kindText, Kinds));
        }

        string styleText = ToolArguments.ReadString(arguments, StyleArgument).Trim().ToLowerInvariant();
        if (styleText.Length > 0 && Array.IndexOf(Styles, styleText) < 0)
        {
            return (null, "", RenderText.BadChoice(StyleArgument, styleText, Styles));
        }

        string x = ToolArguments.ReadString(arguments, XArgument).Trim();
        if (x.Length == 0)
        {
            return (null, "", RenderText.NoX);
        }

        if (!ToolArguments.TryReadStringList(arguments, YArgument, out var y, out string rawY) || y.Count == 0)
        {
            return (null, "", RenderText.NoY + (rawY.Length > 0 ? $" ({rawY})" : ""));
        }

        if (y.Count > ChartSvg.MaxSeries)
        {
            return (null, "", RenderText.TooManySeries(ChartSvg.MaxSeries));
        }

        var kind = (ChartKind)kindIndex;
        if (kind == ChartKind.Pie && y.Count != 1)
        {
            return (null, "", RenderText.PieOneSeries);
        }

        bool? horizontal = ReadFlag(arguments, HorizontalArgument, out string raw);
        if (horizontal is null)
        {
            return (null, "", FileText.BadBoolean(HorizontalArgument, raw));
        }

        bool? stacked = ReadFlag(arguments, StackedArgument, out raw);
        if (stacked is null)
        {
            return (null, "", FileText.BadBoolean(StackedArgument, raw));
        }

        if (ReadWidth(arguments, DefaultWidth, out string? error) is not { } width || ReadScale(arguments, out error) is not { } scale)
        {
            return (null, "", error);
        }

        var table = new ChartTable();
        if (Read(arguments, table, cancellationToken) is { } refused)
        {
            return (null, "", refused);
        }

        if (table.Data(x, y, out error) is not { } data)
        {
            return (null, "", error);
        }

        if (kind == ChartKind.Pie && data.Labels.Select((label, i) => (label, data.Series[0].Values[i])).FirstOrDefault(s => s.Item2 < 0) is { label: { } negative })
        {
            return (null, "", RenderText.PieNegative(negative));
        }

        bool dark = styleText == "dark";
        var spec = new ChartSpec(kind, Text(arguments, TitleArgument), Text(arguments, XLabelArgument), Text(arguments, YLabelArgument), horizontal.Value, stacked.Value, dark, width);
        string svg = ChartSvg.Svg(spec, data, out int folded);
        string notes = (table.Cut ? RenderText.PointsCut(table.Rows.Count) : "") + (folded > 0 ? RenderText.SlicesFolded(folded) : "");
        var job = new RenderJob(null, PicturePage.ForTrustedSvg(svg), PictureFormat.Png, width + (2 * PicturePage.Pad), scale, dark ? ChartSvg.DarkGround : ChartSvg.LightGround);
        return (job, notes, null);
    }

    // The table into the sink: the text given, or the file named; the refusal, else null.
    private string? Read(AIFunctionArguments arguments, ChartTable table, CancellationToken cancellationToken)
    {
        string data = ToolArguments.ReadString(arguments, DataArgument);
        string from = ToolArguments.ReadString(arguments, FromArgument).Trim();
        if ((data.Trim().Length > 0) == (from.Length > 0))
        {
            return RenderText.OneDataSource;
        }

        if (data.Trim().Length > 0)
        {
            return TableSource.TextTable(data, DataArgument, table, cancellationToken);
        }

        var outcome = FilesFor(from).Resolve(from, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return FileText.Error(outcome, from, "read");
        }

        if (Directory.Exists(full))
        {
            return FileText.Error(FileOutcome.IsDirectory, from, "read");
        }

        if (!File.Exists(full))
        {
            return FileText.Error(FileOutcome.Missing, from, "read");
        }

        return TableSource.IsTablePath(full) ? TableSource.Write(full, from, table, cancellationToken) : OfficeText.NotATableFile(from);
    }

    private static string? Text(AIFunctionArguments arguments, string name) => ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;
}
