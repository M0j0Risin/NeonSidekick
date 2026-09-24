using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>view_image(path | paths)</c>: one picture under the working directory, or several in one
/// call, for the model to look at. The result is one line per path the transcript shows verbatim
/// — <c>ladybug.png (1024×768 image/png, 213.4 KB): the picture is in the next message</c> — and
/// the pictures themselves follow as a user-role message the turn loop appends (the carrier), so
/// the model sees them on its next request. Several in one call matter: every call is a model
/// round trip, and the round trips per message are capped (<see cref="Assistant.MaxToolIterations"/>).
/// A path that fails is a line of its own; the others still load.
/// </summary>
public sealed class ViewImageTool : FileTool
{
    public const string ToolName = "view_image";

    public const string PathsArgument = "paths";

    /// <summary>The sentence for a call with neither <c>path</c> nor <c>paths</c>. Pinned.</summary>
    public const string NoPathError = "Error: give path (one picture) or paths (several)";

    private readonly Func<AppSettingsData> _effective;

    // The schema for the cap last asked for: a request reads JsonSchema once per tool, and the cap
    // changes only when the user edits the row, so one parse per edit (the ask_user shape).
    private int _schemaLimit;
    private JsonElement _schema;

    /// <param name="files">The sandbox the paths are read from.</param>
    /// <param name="effective">The settings the cap (<c>File view image max (per call)</c>) is read from at every use.</param>
    public ViewImageTool(WorkingDirectory files, Func<AppSettingsData> effective) : base(files)
    {
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    public override string Name => ToolName;

    /// <summary>
    /// Pictures one call may fetch, as the settings stand now: <c>File view image max (per call)</c> on
    /// <c>/tools</c>' Files tab (2026-09-19, the user's ask; a constant four until then — a picture is
    /// hundreds of tokens at least, the carrier holds them all, and a local vision server has a
    /// ceiling of its own: LM Studio + Gemma 4 took five 1024×1024 pictures in one request and fell
    /// over at six, 2026-09-14). Over it the first that many load and the result names the rest
    /// (2026-09-18). A hand-edited value is clamped (<see cref="LimitOf"/>).
    /// </summary>
    public int Limit => LimitOf(_effective());

    /// <summary>The cap <paramref name="effective"/> holds, clamped into <see cref="AppSettingsData.MinViewImageMaxPerCall"/>–<see cref="AppSettingsData.MaxViewImageMaxPerCall"/>. Pure.</summary>
    public static int LimitOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.FileViewImageMaxPerCall, AppSettingsData.MinViewImageMaxPerCall, AppSettingsData.MaxViewImageMaxPerCall);
    }

    public override string Description => Describe(Limit);

    /// <summary>The description under the given cap. Pinned.</summary>
    public static string Describe(int limit) =>
        "Shows you image files (png, jpg, gif, webp, bmp) under the working directory (the user's cwd / current directory): one as path, or up to " + N(limit) + " per call as paths " +
        "(more than " + N(limit) + " shows the first " + N(limit) + " and names the rest for the next call; a batch per call, not a call per picture). They are attached to the message after the result, so you can describe or analyse them; read_file cannot read images.";

    public override JsonElement JsonSchema
    {
        get
        {
            int limit = Limit;
            if (_schema.ValueKind == JsonValueKind.Undefined || limit != _schemaLimit)
            {
                _schema = SchemaFor(limit);
                _schemaLimit = limit;
            }

            return _schema;
        }
    }

    /// <summary>The schema under the given cap: the <c>paths</c> array's description quotes it. Pinned.</summary>
    public static JsonElement SchemaFor(int limit) => ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " +
        "\"path\": { \"type\": \"string\", \"description\": \"One image file to look at (png, jpg, gif, webp or bmp), relative to the working directory.\" }, " +
        "\"paths\": { \"type\": \"array\", \"items\": { \"type\": \"string\" }, \"description\": \"Several image files to look at in one call (at most " + N(limit) + "), each relative to the working directory. Prefer this over one call per picture; call again for the rest.\" } " +
        "} }");

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The result text for <paramref name="paths"/>: one line each, success or error, in order. The pictures come from <see cref="Load"/>.</summary>
    public string Describe(IReadOnlyList<string> paths) => Load(paths).Text;

    /// <summary>
    /// The first <see cref="Limit"/> paths loaded in order: the text (one line each) and the pictures
    /// that loaded. Over the cap the rest are not touched and the text ends with
    /// <see cref="FileText.MorePictures"/> naming them (2026-09-18; a call with 17 was refused whole
    /// before, and the model began again in batches — now the first batch shows and the rest are spelled out).
    /// </summary>
    public (string Text, IReadOnlyList<ImageAttachment> Images) Load(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        int limit = Limit;
        var lines = new List<string>(paths.Count);
        var images = new List<ImageAttachment>(paths.Count);
        foreach (var path in paths.Take(limit))
        {
            var result = Files.ReadImage(path);
            lines.Add(FileText.Image(result));
            if (result.Outcome == FileOutcome.Ok && result.Image is { } image)
            {
                images.Add(image);
            }
        }

        if (paths.Count > limit)
        {
            lines.Add(FileText.MorePictures(paths.Skip(limit).ToList()));
        }

        return (string.Join("\n", lines), images);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadStringList(arguments, PathsArgument, out var several, out string raw))
        {
            return new ValueTask<object?>(FileText.BadStringList(PathsArgument, raw));
        }

        var paths = new List<string>(several.Count + 1);
        string one = ReadPath(arguments);
        if (!string.IsNullOrWhiteSpace(one))
        {
            paths.Add(one);
        }

        paths.AddRange(several);
        if (paths.Count == 0)
        {
            return new ValueTask<object?>(NoPathError);
        }

        var (text, images) = Load(paths);
        return new ValueTask<object?>(images.Count > 0 ? new ToolImageResult(text, images) : text);
    }
}
