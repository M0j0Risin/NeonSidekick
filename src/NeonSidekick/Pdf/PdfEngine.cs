namespace NeonSidekick.Pdf;

/// <summary>
/// What makes a PDF (2026-10-03, the setting <c>PDF engine</c>, the user's ask): <c>auto</c> (the default: Edge, Chrome or Brave
/// when one is found, Microsoft Print to PDF when none is or the browser fails — Markdown, text and pictures only), <c>browser</c>
/// (the browser or nothing) or <c>printer</c> (Microsoft Print to PDF always, so never HTML or a web page). Anything else reads
/// as <c>auto</c>.
/// </summary>
public static class PdfEngine
{
    public const string Auto = "auto";
    public const string Browser = "browser";
    public const string Printer = "printer";

    /// <summary>The engines in the picker's order.</summary>
    public static readonly string[] Names = [Auto, Browser, Printer];

    public const string Default = Auto;

    /// <summary>The engine in force: the setting in lower case, anything unknown read as <see cref="Default"/>.</summary>
    public static string Resolve(string? engine)
    {
        string name = engine?.Trim().ToLowerInvariant() ?? "";
        return Names.Contains(name, StringComparer.Ordinal) ? name : Default;
    }

    /// <summary>A one-line description for the picker.</summary>
    public static string Describe(string engine) => OperatingSystem.IsMacOS() ? DescribeMac(engine) : Resolve(engine) switch
    {
        Browser => "Edge, Chrome or Brave only; no browser, no PDF",
        Printer => "Microsoft Print to PDF only: Markdown, text and pictures, in black and white",
        _ => "the browser when one is found, else Microsoft Print to PDF",
    };

    /// <summary><see cref="Describe"/> on macOS (2026-10-06): no Microsoft Print to PDF there, so the browser is all <c>auto</c> has. Pinned.</summary>
    private static string DescribeMac(string engine) => Resolve(engine) switch
    {
        Browser => "Edge, Chrome, Brave or Chromium only; no browser, no PDF",
        Printer => "Microsoft Print to PDF: needs Windows",
        _ => "the browser when one is found",
    };
}

/// <summary>The paper a generated page is laid out for (2026-10-03): US Letter by default, the user's call.</summary>
public enum PdfPaperSize
{
    Letter,
    A4,
    Legal,
}

/// <summary>The paper names and their CSS <c>@page size</c> values.</summary>
public static class PdfPaper
{
    public const PdfPaperSize Default = PdfPaperSize.Letter;

    /// <summary>The names in the schema's order.</summary>
    public static readonly string[] Names = ["letter", "a4", "legal"];

    public static bool TryParse(string? text, out PdfPaperSize paper)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "letter":
                paper = PdfPaperSize.Letter;
                return true;
            case "a4":
                paper = PdfPaperSize.A4;
                return true;
            case "legal":
                paper = PdfPaperSize.Legal;
                return true;
            default:
                paper = Default;
                return false;
        }
    }

    /// <summary>The CSS <c>@page size</c> value: <c>letter</c>, <c>A4 landscape</c>. Pinned.</summary>
    public static string Css(PdfPaperSize paper, bool landscape) =>
        (paper switch { PdfPaperSize.A4 => "A4", PdfPaperSize.Legal => "legal", _ => "letter" }) + (landscape ? " landscape" : "");
}

/// <summary>What a PDF is made from (2026-10-03).</summary>
public enum PdfSourceKind
{
    /// <summary>A <c>.md</c> file, or Markdown text (the reply, the tool's <c>markdown</c>).</summary>
    Markdown,

    /// <summary>Any other text file: a listing, coloured by its extension's language.</summary>
    Listing,

    /// <summary>A picture fitted to a page.</summary>
    Picture,

    /// <summary>An HTML file of the user's, cleaned first (<see cref="PdfHtmlSanitizer"/>). The browser only.</summary>
    Html,

    /// <summary>A web page. The browser only.</summary>
    Url,
}

/// <summary>Which engine makes a given PDF.</summary>
public enum PdfRoute
{
    Browser,
    Printer,
}

/// <summary>The pure decisions: a file's kind, the engine, the output's name.</summary>
public static class PdfPlan
{
    private static readonly string[] HtmlExtensions = [".html", ".htm", ".xhtml"];

    /// <summary>
    /// What <paramref name="path"/> makes, by its name and (for text) <paramref name="isText"/>: a picture, HTML, Markdown, a
    /// listing; null for a PDF (<paramref name="isPdf"/>) or anything else, which the caller refuses.
    /// </summary>
    public static PdfSourceKind? Classify(string path, Func<string, bool> isText, out bool isPdf)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(isText);
        string extension = Path.GetExtension(path);
        isPdf = string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
        if (isPdf)
        {
            return null;
        }

        if (Files.ImageFile.IsImagePath(path))
        {
            return PdfSourceKind.Picture;
        }

        if (Array.Exists(HtmlExtensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
        {
            return PdfSourceKind.Html;
        }

        if (Printing.PrintService.IsMarkdown(path))
        {
            return PdfSourceKind.Markdown;
        }

        return isText(path) ? PdfSourceKind.Listing : null;
    }

    /// <summary>Whether only the browser can make <paramref name="kind"/>.</summary>
    public static bool NeedsBrowser(PdfSourceKind kind) => kind is PdfSourceKind.Html or PdfSourceKind.Url;

    /// <summary>
    /// The engine for <paramref name="kind"/> under <paramref name="engine"/> (<see cref="PdfEngine"/>), with what is installed:
    /// the route, or null with the refusal. <c>auto</c> takes the browser when there is one, else the printer; <c>browser</c> and
    /// <c>printer</c> take theirs or refuse. Pure.
    /// </summary>
    public static (PdfRoute? Route, string? Error) Choose(PdfSourceKind kind, string engine, bool browserFound, bool printerFound, string what)
    {
        ArgumentNullException.ThrowIfNull(what);
        string printer = Printing.PrintText.PrintToPdfPrinter;
        switch (PdfEngine.Resolve(engine))
        {
            case PdfEngine.Browser:
                return browserFound ? (PdfRoute.Browser, null) : (null, PdfText.NoBrowser);
            case PdfEngine.Printer:
                if (NeedsBrowser(kind))
                {
                    return (null, PdfText.NeedsBrowserByEngine(what));
                }

                return printerFound ? (PdfRoute.Printer, null) : (null, PdfText.NoPrinter(printer));
        }

        if (browserFound)
        {
            return (PdfRoute.Browser, null);
        }

        if (NeedsBrowser(kind))
        {
            return (null, PdfText.NeedsBrowser(what));
        }

        return printerFound ? (PdfRoute.Printer, null) : (null, PdfText.NoRoute(printer));
    }

    /// <summary>
    /// The output's path, relative to the working directory: <paramref name="to"/> when it names a file (<c>.pdf</c> added when it
    /// has another extension or none), else a name made from the source — <c>notes.md</c> → <c>notes.pdf</c> beside it, a URL →
    /// its host and last segment, Markdown text → <c>reply-2026-10-03-1405.pdf</c> — inside the folder <paramref name="to"/> names,
    /// or beside the source file, or at the top. Pure; <paramref name="isFolder"/> asks the sandbox.
    /// </summary>
    public static string OutputFor(string? to, string? sourceRelative, Uri? url, string? title, DateTimeOffset now, Func<string, bool> isFolder)
    {
        ArgumentNullException.ThrowIfNull(isFolder);
        string target = (to ?? "").Trim();
        bool intoFolder = target.Length == 0 || target is "." || target.EndsWith('/') || target.EndsWith('\\') || isFolder(target);
        if (!intoFolder)
        {
            return WithPdf(target);
        }

        string name;
        string folder;
        if (sourceRelative is not null)
        {
            name = Path.GetFileNameWithoutExtension(sourceRelative);
            string? parent = Path.GetDirectoryName(sourceRelative);
            folder = target.Length > 0 ? target : (string.IsNullOrEmpty(parent) ? "" : parent.Replace('\\', '/'));
        }
        else
        {
            name = url is not null ? UrlName(url) : Stamped(title, now);
            folder = target;
        }

        folder = folder is "." ? "" : folder.TrimEnd('/', '\\');
        string file = Llm.Tools.DownloadFileTool.Sanitise(name);
        if (file.Length == 0)
        {
            file = Stamped(null, now);
        }

        return (folder.Length == 0 ? "" : folder + "/") + file + ".pdf";
    }

    /// <summary><paramref name="target"/> ending in <c>.pdf</c>: kept when it does, the extension added when it does not.</summary>
    public static string WithPdf(string target) =>
        target.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? target : target + ".pdf";

    /// <summary>A web page's file name: <c>example.com-docs-intro</c> from <c>https://example.com/docs/intro.html</c>.</summary>
    public static string UrlName(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        string host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host;
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return host;
        }

        string last = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(segments[^1]));
        return last.Length == 0 ? host : host + "-" + last;
    }

    /// <summary>A name for Markdown text: the title (when it is not the reply's) or <c>reply</c>, and the time to the minute.</summary>
    public static string Stamped(string? title, DateTimeOffset now)
    {
        string stem = string.IsNullOrWhiteSpace(title) || string.Equals(title.Trim(), Printing.PrintText.ReplyTitle, StringComparison.Ordinal)
            ? "reply"
            : Llm.Tools.DownloadFileTool.Sanitise(title.Trim().Replace(' ', '-'));
        return (stem.Length == 0 ? "reply" : stem) + "-" + now.ToString("yyyy-MM-dd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
    }
}
