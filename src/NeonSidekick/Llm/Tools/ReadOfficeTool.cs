using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Office;
using NeonSidekick.Pdf;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>read_office(path, sheet?, range?, slides?, notes?, formulas?, from_char?, share?)</c> (2026-10-28, the Office plan, the user's pick:
/// <c>read_file</c> refused a <c>.docx</c> as not text and nothing in the app read one): a Word document as Markdown, an Excel workbook's
/// sheets and ranges as tables, a presentation's slides, through <see cref="OfficeReader"/>. Modeled on <c>read_pdf</c>: the answer's first
/// line names what was shown and what to ask for next (<c>from_char</c> for a document or one slide, the next <c>range</c> for a sheet, the
/// next <c>slides</c>), within <see cref="WorkingDirectory.MaxReadChars"/>; <c>share</c> (or a full <c>\\server\share</c> path) reads one on an
/// offered UNC share where it is, under the share's sign-in. An argument that doesn't fit the file's kind is refused with what that kind
/// takes. Like <c>read_pdf</c>, it marks the file neither seen nor viewed: no file tool edits an Office file's content. Macros are never run.
/// </summary>
public sealed class ReadOfficeTool : FileTool
{
    public const string ToolName = "read_office";
    public const string SheetArgument = "sheet";
    public const string RangeArgument = "range";
    public const string SlidesArgument = "slides";
    public const string NotesArgument = "notes";
    public const string FormulasArgument = "formulas";
    public const string FromCharArgument = "from_char";
    public const string ShareArgument = UncTool.ShareArgument;

    private static readonly JsonElement Schema = SchemaFor(shares: false);
    private static readonly JsonElement ShareSchema = SchemaFor(shares: true);

    private static JsonElement SchemaFor(bool shares) => ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The Office file to read, relative to the working directory{{(shares ? " (or to the share when share is given)" : "")}}." },
            "sheet": { "type": "string", "description": "Excel: a sheet's name or number from 1." },
            "range": { "type": "string", "description": "Excel: the cells to read: \"A1:F200\", \"B7\", \"A:C\", \"2:10\" or \"'Q3 Sales'!A1:F200\"." },
            "slides": { "type": "string", "description": "PowerPoint: which slides: \"3\", \"2-5\", \"5-\" or \"1,3,7-9\"." },
            "notes": { "type": "boolean", "description": "PowerPoint: true adds the speaker notes." },
            "formulas": { "type": "boolean", "description": "Excel: true shows each cell's formula beside its value." },
            "from_char": { "type": "integer", "description": "Word, or one slide: the character to start at, as the header's next names it." }{{(shares ? ShareProperty : "")}}
          },
          "required": ["path"]
        }
        """);

    private const string ShareProperty =
        """
        ,
            "share": { "type": "string", "description": "A UNC share's name (unc_shares lists them) to read the file there instead of the working directory; not needed when path is a full \\\\server\\share path under one." }
        """;

    /// <summary>The description. Pinned.</summary>
    public const string BaseDescription =
        "Reads a Word (.docx), Excel (.xlsx) or PowerPoint (.pptx) file under the working directory (the user's cwd / current directory) as text. " +
        "Word: the document as Markdown. Excel: without sheet, its sheets with their used ranges and the first rows; sheet and range read cells as a table. " +
        "PowerPoint: the slides with their titles; notes: true adds the speaker notes. " +
        "A long answer comes back in part, its header naming what to ask for next. read_file cannot read an Office file.";

    private const string ShareDescription = " With share, or a full \\\\server\\share path, it reads one on that UNC share where it is.";

    private readonly Func<AppSettingsData> _effective;
    private readonly UncAccess? _unc;

    /// <param name="files">The working directory.</param>
    /// <param name="effective">The settings in force: the offered shares, read at every call.</param>
    /// <param name="unc">The UNC shares' door; null = the working directory alone.</param>
    /// <param name="images">The profile's images folder, read by its full paths; null = the working directory alone.</param>
    public ReadOfficeTool(WorkingDirectory files, Func<AppSettingsData> effective, UncAccess? unc = null, WorkingDirectory? images = null) : base(files, images)
    {
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _unc = unc;
    }

    public override string Name => ToolName;

    public override string Description => SharesOffered ? BaseDescription + ShareDescription : BaseDescription;

    public override JsonElement JsonSchema => SharesOffered ? ShareSchema : Schema;

    private bool SharesOffered => _unc is not null && _unc.IsOffered(_effective());

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!RequirePath(arguments, PathArgument, out string path, out string error))
        {
            return error;
        }

        if (OfficeReader.RefuseByName(path) is { } refused)
        {
            return refused;
        }

        if (ReadFlag(arguments, NotesArgument, out string raw) is not { } notes)
        {
            return FileText.BadBoolean(NotesArgument, raw);
        }

        if (ReadFlag(arguments, FormulasArgument, out raw) is not { } formulas)
        {
            return FileText.BadBoolean(FormulasArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, FromCharArgument, out var fromChar, out string rawFrom) || fromChar < 0)
        {
            return ClockText.BadInteger(FromCharArgument, rawFrom);
        }

        string sheet = ToolArguments.ReadString(arguments, SheetArgument).Trim();
        string range = ToolArguments.ReadString(arguments, RangeArgument).Trim();
        string spec = ToolArguments.ReadString(arguments, SlidesArgument).Trim().Trim('"');
        var kind = OfficeFile.Kind(path)!.Value;
        if (Misfit(kind, sheet, range, spec, notes, formulas, fromChar ?? 0) is { } misfit)
        {
            return OfficeText.WrongArgument(misfit, kind);
        }

        PdfPageRange? slides = null;
        if (spec.Length > 0 && !PdfPageRange.TryParse(spec, out slides, out _))
        {
            return OfficeText.BadSlides(spec);
        }

        if (kind == OfficeKind.Slides && fromChar > 0 && (slides is null || slides.Resolve(int.MaxValue).Count != 1))
        {
            return OfficeText.FromCharNeedsOneSlide;
        }

        var request = new OfficeRequest(sheet.Length > 0 ? sheet : null, range.Length > 0 ? range : null, slides, spec, notes, formulas, fromChar ?? 0);
        string? share = ToolArguments.ReadString(arguments, ShareArgument).Trim() is { Length: > 0 } named ? named : null;
        if (share is null && !OnAShare(path))
        {
            var files = FilesFor(path);
            return await Task.Run(() => OfficeReader.Read(files, path, request, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        return await ReadOnShareAsync(share, path, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The first argument given that <paramref name="kind"/> doesn't take; null when they all fit.</summary>
    private static string? Misfit(OfficeKind kind, string sheet, string range, string slides, bool notes, bool formulas, int fromChar)
    {
        var given = new List<string>();
        if (sheet.Length > 0)
        {
            given.Add(SheetArgument);
        }

        if (range.Length > 0)
        {
            given.Add(RangeArgument);
        }

        if (formulas)
        {
            given.Add(FormulasArgument);
        }

        if (slides.Length > 0)
        {
            given.Add(SlidesArgument);
        }

        if (notes)
        {
            given.Add(NotesArgument);
        }

        if (fromChar > 0)
        {
            given.Add(FromCharArgument);
        }

        string[] takes = kind switch
        {
            OfficeKind.Word => [FromCharArgument],
            OfficeKind.Excel => [SheetArgument, RangeArgument, FormulasArgument],
            _ => [SlidesArgument, NotesArgument, FromCharArgument],
        };
        return given.FirstOrDefault(g => !takes.Contains(g));
    }

    private async Task<object?> ReadOnShareAsync(string? share, string path, OfficeRequest request, CancellationToken cancellationToken)
    {
        if (!SharesOffered)
        {
            return UncText.NoSharesOffered;
        }

        var target = _unc!.Resolve(share, path, out string relative, out string? error);
        if (target is null)
        {
            return error!;
        }

        // The root named for the share while the answer is built (read_pdf's rule, review H2).
        using var naming = FileText.NamingRoot(UncText.ShareName(target));
        var read = await _unc.RunAsync(target, write: false, files => OfficeReader.Read(files, relative, request, cancellationToken), cancellationToken).ConfigureAwait(false);
        return read.Error ?? read.Value;
    }

    /// <summary>Whether a call without a share means one: a full path outside the working directory (and the images folder) while a share is offered (<c>open</c>'s rule).</summary>
    private bool OnAShare(string path)
    {
        string spelled = UncPath.Spell(path, OperatingSystem.IsWindows());
        return UncCatalog.IsAbsolute(spelled) && SharesOffered && (UncPath.IsNetwork(spelled, OperatingSystem.IsWindows()) || FilesFor(spelled).Resolve(spelled, forWrite: false, out _) != FileOutcome.Ok);
    }
}
