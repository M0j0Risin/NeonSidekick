using System.Globalization;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Web;

namespace NeonSidekick.Render;

/// <summary>
/// The user-visible wording of the rendered pictures (2026-11-16, plan sharded-hugging-puppy #9): <c>render_picture</c>,
/// <c>render_chart</c> and <c>render_collage</c>, their answers and refusals. Every refusal, a string that ends a call, starts <c>Error: </c>
/// and repeats the model's values through <see cref="ArgumentText.Echo"/> (2026-11-17, the admin-themes-render review's #20, #134, #135);
/// the answers' tails (<see cref="Written"/>, <see cref="PointsCut"/>, <see cref="Skipped"/>…) don't.
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

    /// <summary>The most names a list in an answer shows before "and N more".</summary>
    public const int ListShown = 10;

    public const string OneSource = "Error: give exactly one of mermaid, svg or html.";
    public const string OneDataSource = "Error: give exactly one of data or from.";
    public const string NoX = "Error: name the label column: x.";
    public const string NoY = "Error: name one or more number columns: y.";
    public const string OnePictureSource = "Error: give paths, or a folder (and a pattern), not both.";
    public const string NoPictures = "Error: no pictures to lay out: give paths, or a folder holding some.";

    /// <summary><c>pattern</c> sent with <c>paths</c> (2026-11-17, #108): it filters a folder only.</summary>
    public const string PatternNeedsFolder = "Error: pattern goes with folder; with paths, name each picture.";

    /// <summary><c>overwrite</c> sent without <c>to</c> (2026-11-17, #193).</summary>
    public const string OverwriteNeedsTo = "Error: overwrite goes with to; without to the picture gets a new numbered name, never over a file.";

    /// <summary>No browser to draw with, per OS as <c>PdfText</c> words it (2026-11-17, #92): a configured path not found is named.</summary>
    public static string NoBrowser(string browser) =>
        (OperatingSystem.IsMacOS()
            ? "Error: drawing needs Edge, Chrome, Brave or Chromium in /Applications, and none was found"
            : "Error: drawing needs Edge, Chrome or Brave, and none was found")
        + (browser.Length > 0 ? " at " + browser : "") + "; set Web browser path on the Web tab of /tools.";

    public static string BadNumber(string name, string raw, string range) => $"Error: {name} '{ArgumentText.Echo(raw)}' is not a number in {range}.";

    public static string BadChoice(string name, string raw, IEnumerable<string> choices) =>
        $"Error: {name} '{ArgumentText.Echo(raw)}' is not one of {string.Join(", ", choices)}.";

    public static string BadList(string name, string raw) => $"Error: {name} '{ArgumentText.Echo(raw)}' is not a list of strings.";

    /// <summary>A ground that is no color (2026-11-17, #210: what the one parser takes).</summary>
    public static string BadBackground(string raw) =>
        $"Error: background '{ArgumentText.Echo(raw)}' is not a color; give white, black, transparent, #rgb, #rrggbb, #rrggbbaa or a color name.";

    public static string BadExtension(string to, string allowed) => $"Error: '{ArgumentText.Echo(to)}' must end in {allowed}.";

    public static string MermaidError(string message) => $"Error: Mermaid couldn't read the diagram: {message}. Fix the text and try again.";

    public static string Failed(string detail) => $"Error: the picture wasn't drawn: {detail}.";

    public const string TooLarge = "Error: the picture came out too large to hand back; try a smaller width or scale.";

    /// <summary>A column the table doesn't have, with the columns it does (the first <see cref="ListShown"/>).</summary>
    public static string NoColumn(string name, IEnumerable<string> columns) =>
        $"Error: no column '{ArgumentText.Echo(name)}'; the columns are {Shown(columns.Select(c => "'" + ArgumentText.Echo(c) + "'").ToList())}.";

    /// <summary>A name the header gives more than one column (2026-11-17, #220).</summary>
    public static string AmbiguousColumn(string name, int count) =>
        $"Error: {count.ToString(CultureInfo.InvariantCulture)} columns are named '{ArgumentText.Echo(name)}'; rename one, or chart another.";

    /// <summary>A column <c>y</c> names twice (2026-11-17, #220).</summary>
    public static string RepeatedY(string name) => $"Error: '{ArgumentText.Echo(name)}' is in y twice.";

    /// <summary>A cell that is no number, by its data row: the first row under the header is 1 (2026-11-17, #136).</summary>
    public static string NotANumber(string column, int row, string text) =>
        $"Error: column '{ArgumentText.Echo(column)}' holds '{ArgumentText.Echo(text)}' on data row {row.ToString(CultureInfo.InvariantCulture)}, not a number.";

    public static string TooManySeries(int max) => $"Error: at most {max.ToString(CultureInfo.InvariantCulture)} y columns.";

    public const string PieOneSeries = "Error: a pie takes one y column.";

    public static string PieNegative(string label) => $"Error: a pie can't show '{ArgumentText.Echo(label)}': its value is below zero.";

    public const string NoRows = "Error: the table has no rows to chart.";

    /// <summary><c>horizontal</c> or <c>stacked</c> on a line or pie chart (2026-11-17, #200).</summary>
    public static string BarOnly(string option, string kind) => $"Error: {option} is for bar charts; a {kind} chart can't use it.";

    /// <summary><c>y</c> sent as something other than a list of strings (2026-11-17, #175).</summary>
    public const string YNotStrings = "Error: y is a list of column names, such as [\"sales\"].";

    /// <summary>Every y cell blank (2026-11-17, #67).</summary>
    public const string NothingToDraw = "Error: every y cell is blank, so there's nothing to draw.";

    /// <summary>A pie with no slice above zero (2026-11-17, #24).</summary>
    public const string PieNothingAbove0 = "Error: a pie needs a value above 0; every slice is zero, negative or blank.";

    /// <summary>A range or a total past a double (2026-11-17, #48, #185).</summary>
    public const string NumbersTooLarge = "Error: the numbers are too large to draw; scale them down (thousands, millions).";

    /// <summary>A plot area under <see cref="ChartSvg.MinPlotWidth"/> by <see cref="ChartSvg.MinPlotHeight"/> (2026-11-17, #83).</summary>
    public const string PlotTooSmall = "Error: too little room for the plot; widen it, or a shorter title and fewer labels.";

    /// <summary>Bars under one CSS pixel (2026-11-17, #218).</summary>
    public static string BarsTooThin(int rows, int series) =>
        $"Error: {rows.ToString(CultureInfo.InvariantCulture)} rows of {series.ToString(CultureInfo.InvariantCulture)} series leave bars under a pixel; widen it, or fewer rows.";

    public static string TooManyPictures(int max) => $"Error: at most {max.ToString(CultureInfo.InvariantCulture)} pictures in one collage.";

    public static string CaptionsCount(int captions, int pictures) =>
        $"Error: {captions.ToString(CultureInfo.InvariantCulture)} captions for {pictures.ToString(CultureInfo.InvariantCulture)} pictures: give one each, or \"names\".";

    /// <summary>The draw's own failures, worded where the browser meets them (2026-11-17, #8).</summary>
    public const string PictureTooLarge = "the picture is too large to hand back";

    public static string MermaidNotLoaded(string why) => "Mermaid did not load: " + why;

    public static string MermaidFailed(string why) => "Mermaid failed: " + why;

    public static string NotMeasured(string why) => "the page couldn't be measured: " + why;

    /// <summary>The reasons a collage skips a file (2026-11-17, #13, #77, #125).</summary>
    public const string SkipNotPicture = "not a picture";

    public const string SkipEarlierCollage = "an earlier collage";

    public static string SkipTooLarge => "over " + (Files.ImageFile.MaxFileBytes / 1_000_000).ToString(CultureInfo.InvariantCulture) + " MB";

    public static string SkipUnreadable(string detail) => "unreadable: " + detail;

    /// <summary>
    /// The files a collage left out, grouped by reason, each group's first <see cref="ListShown"/> names then "and N more" (2026-11-17, #13,
    /// #77, #134): " Skipped, not a picture: a.txt, b.doc; an earlier collage: collage.jpg."
    /// </summary>
    public static string Skipped(IReadOnlyList<(string Name, string Reason)> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        return skipped.Count == 0 ? ""
            : " Skipped, " + string.Join("; ", skipped.GroupBy(s => s.Reason, StringComparer.Ordinal).Select(g => g.Key + ": " + Shown([.. g.Select(s => s.Name)]))) + ".";
    }

    /// <summary>The files a page named and didn't get: not found, outside both roots, too large, or past the budget (2026-11-17, #42, #165).</summary>
    public static string PicturesLeftOut(IReadOnlyList<string> addresses) =>
        " Left out, not found in the working directory or the images folder, or too large: " + Shown([.. addresses.Select(a => "'" + ArgumentText.Echo(a) + "'")]) + ".";

    /// <summary>A folder holding more pictures than a collage takes (2026-11-17, #102).</summary>
    public static string FirstPictures(int shown, int found) => string.Create(CultureInfo.InvariantCulture,
        $" The folder has {found} pictures; the first {shown} in name order are laid out.");

    /// <summary>A folder whose listing stopped at its cap (2026-11-17, #133).</summary>
    public static string FolderListCut(int limit) => string.Create(CultureInfo.InvariantCulture, $" Only the folder's first {limit:N0} entries were looked at.");

    /// <summary>
    /// The answer: where it went and its size, and the notes after. A page past <see cref="BrowserPictureRenderer.MaxPixels"/> tall is cut at
    /// the bottom (<paramref name="cut"/>), one wider than it was laid out at the right (<paramref name="wide"/>, 2026-11-17, #2, #105).
    /// </summary>
    public static string Written(string relative, int width, int height, long bytes, bool cut, bool wide = false)
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"Drew {relative}: {width}×{height} pixels, {Files.FileText.Size(bytes)}.");
        if (!cut && !wide)
        {
            return text;
        }

        string most = BrowserPictureRenderer.MaxPixels.ToString(CultureInfo.InvariantCulture);
        string how = cut && wide ? $"taller than {most} pixels and wider than its width, and is cut at the bottom and right"
            : cut ? $"taller than {most} pixels and is cut at the bottom"
            : $"wider than its width (at most {most} pixels) and is cut at the right";
        return text + " The page was " + how + "; draw less, or a smaller scale.";
    }

    public static string PointsCut(int kept) => string.Create(CultureInfo.InvariantCulture, $" Only the first {kept} rows are charted.");

    public static string SlicesFolded(int folded) => string.Create(CultureInfo.InvariantCulture, $" The {folded} smallest slices are shown together as Other.");

    /// <summary>A pie's slices left out at zero or blank (2026-11-17, #219).</summary>
    public static string SlicesDropped(int dropped) => string.Create(CultureInfo.InvariantCulture, $" {dropped} {(dropped == 1 ? "slice at zero or blank is" : "slices at zero or blank are")} left out.");

    /// <summary><c>stacked</c> with one series: drawn, and said (2026-11-17, #200).</summary>
    public const string StackedOneSeries = " One series: nothing to stack.";

    /// <summary>The pie's slice that holds the small ones.</summary>
    public const string Other = "Other";

    // The first ListShown of a list, then "and N more".
    private static string Shown(IReadOnlyList<string> items) =>
        items.Count <= ListShown ? string.Join(", ", items)
            : string.Join(", ", items.Take(ListShown)) + " and " + (items.Count - ListShown).ToString(CultureInfo.InvariantCulture) + " more";
}
