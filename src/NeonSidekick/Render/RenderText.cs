using System.Globalization;

namespace NeonSidekick.Render;

/// <summary>
/// The user-visible wording of the rendered pictures (2026-11-16, plan sharded-hugging-puppy #9): <c>render_picture</c>,
/// <c>render_chart</c> and <c>render_collage</c>, their answers and refusals.
/// </summary>
public static class RenderText
{
    public const string PictureDescription =
        "Draws a picture and saves it as a PNG in the working directory: a diagram from Mermaid text (flowchart, sequence, class, state, " +
        "timeline, mind map, gantt, pie…), or your own SVG or HTML (a poster, card, logo, icon, or anything with text laid out). Give exactly one " +
        "of mermaid, svg or html. Scripts don't run and nothing is fetched from the web: inline everything, and use pictures only from the " +
        "working directory by name. The picture comes back so you can look at it and fix it. This is the way to draw: never write a script for it.";

    public const string ChartDescription =
        "Draws a bar, line or pie chart as a PNG in the working directory from a table: data (CSV text, or a JSON array of objects or of arrays) " +
        "or from (a .csv, .tsv or .json file there; save a database query to one first with the query tool's to). x names the label column, y " +
        "the number columns, one series each. The chart comes back so you can look at it. This is the way to chart: never write a script for it.";

    public const string CollageDescription =
        "Lays pictures out in a grid on one picture, with optional captions and a title, and saves it as a JPEG (or PNG) in the working directory. " +
        "Give paths (pictures in the working directory, or full paths of saved pictures), or a folder and a pattern. The collage comes back so " +
        "you can look at it.";

    public const string OneSource = "Give exactly one of mermaid, svg or html.";
    public const string OneDataSource = "Give exactly one of data or from.";
    public const string NoX = "Name the label column: x.";
    public const string NoY = "Name one or more number columns: y.";
    public const string OnePictureSource = "Give paths, or a folder (and a pattern), not both.";
    public const string NoPictures = "No pictures to lay out: give paths, or a folder holding some.";

    public static string NoBrowser(string browser) =>
        $"Drawing needs Edge, Chrome, Brave or Chromium, and none was found{(browser.Length > 0 ? $" at {browser}" : "")}. Set Web browser path in /settings › Web.";

    public static string BadNumber(string name, string raw, string range) => $"{name} \"{raw}\" is not a number in {range}.";

    public static string BadChoice(string name, string raw, IEnumerable<string> choices) => $"{name} \"{raw}\" is not one of {string.Join(", ", choices)}.";

    public static string BadList(string name, string raw) => $"{name} \"{raw}\" is not a list of strings.";

    public static string BadBackground(string raw) => $"background \"{raw}\" is not white, transparent or a #RRGGBB color.";

    public static string BadExtension(string to, string allowed) => $"\"{to}\" must end in {allowed}.";

    public static string MermaidError(string message) => $"Mermaid couldn't read the diagram: {message}. Fix the text and try again.";

    public static string Failed(string detail) => $"The picture wasn't drawn: {detail}.";

    public const string TooLarge = "The picture came out too large to hand back; try a smaller width or scale.";

    public static string NoColumn(string name, IEnumerable<string> columns) => $"No column \"{name}\"; the columns are {string.Join(", ", columns)}.";

    public static string NotANumber(string column, int row, string text) =>
        $"Column \"{column}\" holds \"{text}\" on row {row.ToString(CultureInfo.InvariantCulture)}, not a number.";

    public static string TooManySeries(int max) => $"At most {max.ToString(CultureInfo.InvariantCulture)} y columns.";

    public const string PieOneSeries = "A pie takes one y column.";

    public static string PieNegative(string label) => $"A pie can't show \"{label}\": its value is below zero.";

    public const string NoRows = "The table has no rows to chart.";

    public static string TooManyPictures(int max) => $"At most {max.ToString(CultureInfo.InvariantCulture)} pictures in one collage.";

    public static string CaptionsCount(int captions, int pictures) =>
        $"{captions.ToString(CultureInfo.InvariantCulture)} captions for {pictures.ToString(CultureInfo.InvariantCulture)} pictures: give one each, or \"names\".";

    public static string Skipped(IReadOnlyList<string> names) => names.Count == 0 ? "" : $" Skipped, not pictures: {string.Join(", ", names)}.";

    /// <summary>The answer: where it went and its size, and the notes after.</summary>
    public static string Written(string relative, int width, int height, long bytes, bool cut) =>
        string.Create(CultureInfo.InvariantCulture, $"Drew {relative}: {width}×{height} pixels, {Files.FileText.Size(bytes)}.")
        + (cut ? " The page was taller than 4096 pixels and is cut at the bottom; draw less, or a smaller scale." : "");

    public static string PointsCut(int kept) => string.Create(CultureInfo.InvariantCulture, $" Only the first {kept} rows are charted.");

    public static string SlicesFolded(int folded) => string.Create(CultureInfo.InvariantCulture, $" The {folded} smallest slices are shown together as Other.");

    /// <summary>The pie's slice that holds the small ones.</summary>
    public const string Other = "Other";
}
