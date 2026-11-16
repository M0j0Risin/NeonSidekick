using NeonSidekick.Files;

namespace NeonSidekick.WebUi;

/// <summary>The kind a made file shows as on the 🗂️ sheet's Files tab, and how it is served.</summary>
public enum MadeFileKind
{
    Pdf,
    Word,
    Excel,
    Slides,
    Picture,
    Text,
    Other,
}

/// <summary>
/// How a file the model made is served to the page (2026-11-16, plan lucid-roaming-lantern #41): <paramref name="ContentType"/>, shown in the
/// page (<paramref name="Inline"/>) or downloaded, and whether its text's charset is read off the file (<paramref name="Text"/>).
/// </summary>
public sealed record MadeFileType(MadeFileKind Kind, string ContentType, bool Inline, bool Text = false)
{
    /// <summary>
    /// A served file's sandbox: every kind but a PDF gets <c>Content-Security-Policy: sandbox</c> on top of the page's policy, so nothing a model
    /// wrote runs as the page. A PDF can't (Chromium's viewer needs scripts, which the sandbox forbids); its scripts run in the viewer's own.
    /// </summary>
    public bool Sandboxed => Kind != MadeFileKind.Pdf;
}

/// <summary>
/// The made files' explicit allow-list (2026-11-16, #41), by extension ignoring case as <see cref="OfficeFile"/> matches: a PDF, the
/// pictures and plain text inline; Office files downloaded with their own type, so a phone hands them to Word or Excel; everything else
/// (HTML, SVG, scripts among them) downloaded as bytes. Pure.
/// </summary>
public static class MadeFileTypes
{
    /// <summary>What a download of anything not listed is sent as.</summary>
    public const string Bytes = "application/octet-stream";

    private static readonly Dictionary<string, MadeFileType> Listed = Build();

    private static Dictionary<string, MadeFileType> Build()
    {
        var map = new Dictionary<string, MadeFileType>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = new(MadeFileKind.Pdf, "application/pdf", true),
            [".png"] = new(MadeFileKind.Picture, "image/png", true),
            [".jpg"] = new(MadeFileKind.Picture, "image/jpeg", true),
            [".jpeg"] = new(MadeFileKind.Picture, "image/jpeg", true),
            [".webp"] = new(MadeFileKind.Picture, "image/webp", true),
            [".gif"] = new(MadeFileKind.Picture, "image/gif", true),
        };
        foreach (string text in (string[])[".txt", ".md", ".csv", ".json", ".log"])
        {
            map[text] = new(MadeFileKind.Text, "text/plain", true, Text: true);
        }

        // OfficeFile.MediaTypes is one per extension, in Extensions' order: the two zipped.
        for (int i = 0; i < OfficeFile.Extensions.Count; i++)
        {
            string extension = OfficeFile.Extensions[i];
            var kind = OfficeFile.WordExtensions.Contains(extension) ? MadeFileKind.Word
                : OfficeFile.ExcelExtensions.Contains(extension) ? MadeFileKind.Excel
                : MadeFileKind.Slides;
            map[extension] = new(kind, OfficeFile.MediaTypes[i], false);
        }

        return map;
    }

    /// <summary>How <paramref name="name"/> is served, by its extension.</summary>
    public static MadeFileType Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Listed.TryGetValue(Path.GetExtension(name), out var type) ? type : new MadeFileType(MadeFileKind.Other, Bytes, false);
    }

    /// <summary>
    /// A text file's charset, the app's own reading of every byte (<see cref="TextFile.Detect(Stream)"/>): <c>utf-8</c>, <c>utf-16le</c>/<c>be</c>,
    /// or the legacy code page's web name; null for UTF-32, which browsers don't decode, so it goes as a download. The stream is left where it was.
    /// </summary>
    public static string? Charset(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var codec = TextFile.Detect(stream);
        return codec.Form switch
        {
            TextForm.Utf8 or TextForm.Utf8Bom => "utf-8",
            TextForm.Utf16Le => "utf-16le",
            TextForm.Utf16Be => "utf-16be",
            TextForm.Legacy => codec.StreamEncoding.WebName,
            _ => null,
        };
    }

    /// <summary>The Files tab's glyph for <paramref name="kind"/>.</summary>
    public static string Glyph(MadeFileKind kind) => kind switch
    {
        MadeFileKind.Pdf => "📄",
        MadeFileKind.Word => "📝",
        MadeFileKind.Excel => "📊",
        MadeFileKind.Slides => "📽️",
        MadeFileKind.Picture => "🖼️",
        MadeFileKind.Text => "📃",
        _ => "⬇️",
    };
}
