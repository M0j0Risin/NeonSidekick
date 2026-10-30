using PhotoSauce.MagicScaler;

namespace NeonSidekick.Files;

/// <summary>
/// An image the user put on the line, ready for the model: the bytes as they are sent (the
/// original file when it is small enough and already PNG or JPEG, else a downscaled re-encode),
/// their media type, and the size they stand at.
/// </summary>
public sealed record ImageAttachment(string Path, byte[] Bytes, string MediaType, int Width, int Height)
{
    /// <summary>
    /// A picture off the camera (2026-10-02): the history marks its part so a stored session keeps a placeholder instead
    /// unless <c>Camera keep in sessions</c> is on (<c>SessionHistory</c>).
    /// </summary>
    public bool Camera { get; init; }

    /// <summary>
    /// A screenshot (2026-10-04, <c>screen_capture</c> and <c>/screen</c>): marked as <see cref="Camera"/> is, so a stored session keeps a
    /// line naming it unless <c>Screen capture keep in sessions</c> is on (<c>SessionHistory</c>).
    /// </summary>
    public bool Screen { get; init; }

    /// <summary>
    /// A PDF page's picture drawn by <c>read_pdf</c> (2026-10-14, the PDF plan's gaps 21 and 32): the PDF and the page, <c>report.pdf#3</c>.
    /// Such a picture has no file (<see cref="Path"/> empty); its history part is marked so a stored session keeps a line naming the
    /// page instead of its bytes, and a picture taken out of a request names the page (the PDF is still on disk to draw again).
    /// </summary>
    public string? PdfPage { get; init; }

    /// <summary>
    /// A plan's reference picture the app attached itself (2026-10-18, <c>Plans.PlanReferences.ForRun</c>: a fresh-context approve, a Ralph pass), its
    /// <see cref="Path"/> the file beside the plan: the history marks its part (<c>ConversationHistory.ReferenceKey</c>) so a stored session keeps a
    /// line naming the file rather than the bytes again, pass after pass.
    /// </summary>
    public bool Reference { get; init; }

    /// <summary>
    /// An attached PDF's text (2026-10-14, the PDF plan's phase 3 and gap 39): the read started when it was pasted, carried as the task so
    /// a message sent or queued before it ends hands the read over rather than cancelling it; the send awaits it. Null for a picture.
    /// One attachment list for both (the plan's choice): a PDF is an attachment with <see cref="PdfFile.MediaType"/>, its whole file in
    /// <see cref="Bytes"/> and no size, so the input line, the queue and the turn carry it as they carry a picture.
    /// </summary>
    public Task<PdfDocumentText>? DocumentText { get; init; }

    /// <summary>Whether this attachment is a PDF rather than a picture.</summary>
    public bool IsDocument => MediaType == PdfFile.MediaType;
}

/// <summary>
/// A pasted picture as it was before any downscale (later still on 2026-09-24): the bytes <c>generate_image</c> uploads when the
/// model names the paste by its <c>[Image #N]</c> label, and a dropped file's own name — null for a clipboard picture, which has
/// none. <paramref name="Path"/> (2026-10-09) is where it lives: a dropped file's own path, or the file a clipboard picture was
/// saved as when its message was sent (<c>Pasted images folder</c>); null for one not saved (yet, or the write refused).
/// </summary>
public sealed record PastedPicture(byte[] Bytes, string? FileName, string? Path = null);

/// <summary>
/// Why <see cref="ImageFile.TryLoad"/> gave no attachment. <see cref="ImageFile.Notice"/> turns one into
/// the sentence the input line shows; <see cref="WorkingDirectory.ReadImage"/> maps one to a <see cref="FileOutcome"/>.
/// </summary>
public enum ImageLoadFailure
{
    /// <summary>The image loaded.</summary>
    None,

    /// <summary>No file at the path.</summary>
    NotFound,

    /// <summary>Over <see cref="ImageFile.MaxFileBytes"/> or <see cref="ImageFile.MaxPixels"/>.</summary>
    TooLarge,

    /// <summary>The codecs refused it — or the file could not be opened at all, which reads the same from the line.</summary>
    CouldNotRead,

    /// <summary>No codecs here at all (2026-10-06, the macOS build: <see cref="ImageCodecs.Available"/>).</summary>
    Unsupported,

    /// <summary>
    /// The file could not be opened or read — in use, denied, gone offline (2026-10-22, review K11: that read as "not an image", and the
    /// model took a locked picture for a broken one).
    /// </summary>
    Unreadable,
}

/// <summary>
/// The one file that knows how an image gets from a dropped path to an <see cref="ImageAttachment"/>.
/// A drag into Windows Terminal is a paste of the file's path — quoted when it has a space, no
/// line break after it; several files at once are several such paths — so <see cref="TryPastedPaths"/>
/// is the rule that tells such a paste from text: nothing but paths, one per line or a run on a
/// line, each an image extension and a file that exists. A typed path is never one (typing is
/// not a paste), and a path among other words stays text.
///
/// <para><see cref="Load"/> keeps the model's request sane: a file over <see cref="MaxFileBytes"/>
/// is refused unread, one over <see cref="MaxPixels"/> undecoded; anything wider or taller than <see cref="MaxSide"/> is downscaled to fit
/// (never enlarged), PNG for a PNG/GIF/BMP source (a screenshot keeps its sharp text) and JPEG at
/// <see cref="JpegQuality"/> for a JPEG/WebP one; a PNG or JPEG that already fits goes byte for
/// byte. Decoding goes through Windows' own codecs (WIC, driven by MagicScaler), so WebP needs the
/// system's WebP codec (Windows 11 ships it) and a file the codecs refuse reads as
/// <see cref="CouldNotRead"/>. On a Mac (2026-10-07) MagicScaler drives Apple's ImageIO instead
/// (<see cref="Images.ImageIOCodecs"/>), which also reads HEIC, TIFF and AVIF (<see cref="MacExtensions"/>). Every failure is a sentence back to the line, never an exception.
/// A picture off the clipboard (<see cref="UI.WindowsClipboard.TryReadImage"/>) takes the same
/// road from its bytes under the name <see cref="ClipboardName"/>.
/// Pure apart from the file system; the sentences are pinned.</para>
/// </summary>
public static class ImageFile
{
    /// <summary>The longest side an image is sent at; a bigger one is downscaled to fit.</summary>
    public const int MaxSide = 2048;

    /// <summary>A file over this many bytes is refused without being read.</summary>
    public const long MaxFileBytes = 20_000_000;

    /// <summary>An image of more pixels than this is refused before it is decoded (a 20 MB PNG can be 400 MB of pixels).</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>The JPEG quality a downscaled JPEG or WebP is re-encoded at.</summary>
    public const int JpegQuality = 85;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    // The extensions every system's codecs read (WIC; ImageIO on a Mac).
    private static readonly string[] CommonExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    /// <summary>
    /// What a Mac reads besides (2026-10-07, ImageIO): an iPhone's HEIC/HEIF, TIFF, and AVIF. Re-encoded for the model as WebP is
    /// (<see cref="SentAs"/>). Windows' list stays as it was: its HEIF codec is a Store extension, not a given.
    /// </summary>
    public static readonly IReadOnlyList<string> MacExtensions = [".heic", ".heif", ".tif", ".tiff", ".avif"];

    /// <summary>The extensions that make a pasted path an image, lower case with the dot (<see cref="MacExtensions"/> too on a Mac).</summary>
    public static readonly string[] Extensions = OperatingSystem.IsMacOS() ? [.. CommonExtensions, .. MacExtensions] : CommonExtensions;

    /// <summary>True when the path ends in one of <see cref="Extensions"/> (any case). No file system.</summary>
    public static bool IsImagePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = System.IO.Path.GetExtension(path);
        return extension.Length > 0 && Array.Exists(Extensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The path a pasted block is, when the whole block is one: a single line, trimmed, one pair
    /// of surrounding double quotes stripped (the terminal's form for a path with a space), an
    /// image extension, fully qualified, and a file that exists. Anything else is text — two
    /// paths included; <see cref="TryPastedPaths"/> is the rule for a list.
    /// </summary>
    public static bool TryPastedPath(string paste, out string path, bool documents = false)
    {
        ArgumentNullException.ThrowIfNull(paste);
        path = "";
        string candidate = paste.Trim();
        if (candidate.Contains('\n') || candidate.Contains('\r') || !TryPath(candidate, out string found, documents))
        {
            return false;
        }

        path = found;
        return true;
    }

    /// <summary>
    /// The paths a pasted block is, when the whole block is nothing but image paths: one per
    /// line, or several on a line each in quotes or without a space (the terminal's forms for a
    /// drop of several files), every one by the rule of <see cref="TryPastedPath"/>. A line is
    /// tried whole first, so an unquoted path with a space still reads as one. One line that is
    /// neither a path nor a run of paths makes the whole block text; blank lines are skipped. With <paramref name="documents"/>
    /// (2026-10-14, the PDF plan's phase 3: the input line attaches PDFs) a <c>.pdf</c> path counts as one too.
    /// </summary>
    public static bool TryPastedPaths(string paste, out IReadOnlyList<string> paths, bool documents = false)
    {
        ArgumentNullException.ThrowIfNull(paste);
        var found = new List<string>();
        paths = found;
        foreach (var raw in paste.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryPath(line, out string one, documents))
            {
                found.Add(one);
                continue;
            }

            foreach (var token in Tokens(line))
            {
                if (!TryPath(token, out string path, documents))
                {
                    found.Clear();
                    return false;
                }

                found.Add(path);
            }
        }

        return found.Count > 0;
    }

    /// <summary>
    /// The host of the first picture (or, with <paramref name="documents"/>, PDF) path in <paramref name="paste"/> that names a UNC host
    /// nobody configured (2026-10-22, review K7): the line says it wasn't opened. Null when there is none.
    /// </summary>
    public static string? UnknownUncHost(string paste, bool documents = false)
    {
        ArgumentNullException.ThrowIfNull(paste);
        foreach (string raw in paste.Split('\n'))
        {
            foreach (string token in (string[])[raw.Trim(), .. Tokens(raw.Trim())])
            {
                string candidate = token.Trim().Trim('"').Trim();
                if (candidate.Length > 0 && (IsImagePath(candidate) || (documents && PdfFile.IsPdfPath(candidate))) && Unc.KnownUncHosts.IsUnknown(candidate))
                {
                    return Unc.KnownUncHosts.HostOf(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>A line's whitespace-separated tokens, a double-quoted stretch (quotes kept) being one token whatever it holds. Pinned.</summary>
    public static IReadOnlyList<string> Tokens(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }

            int start = i;
            if (line[i] == '"')
            {
                int close = line.IndexOf('"', i + 1);
                i = close < 0 ? line.Length : close + 1;
            }
            else
            {
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    i++;
                }
            }

            tokens.Add(line[start..i]);
        }

        return tokens;
    }

    // One trimmed line or token as a path: the quotes off, an image extension (or a PDF's, with documents), fully qualified, a file that exists.
    private static bool TryPath(string candidate, out string path, bool documents = false)
    {
        path = "";
        candidate = candidate.Trim();
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1].Trim();
        }

        if (candidate.Length == 0 || candidate.Contains('"') || !(IsImagePath(candidate) || (documents && PdfFile.IsPdfPath(candidate))) || !System.IO.Path.IsPathFullyQualified(candidate))
        {
            // A bare "photo.png" inside a sentence must never resolve against the process's own directory.
            return false;
        }

        // A UNC path is probed only on a known host (2026-10-22, review K7, the fix plan's D13): File.Exists on \\attacker\s\cat.png
        // opens SMB there, NTLM sign-in and all, and holds the paste until the host answers. Any other host's path stays text.
        if (Unc.KnownUncHosts.IsUnknown(candidate))
        {
            return false;
        }

        bool exists;
        try
        {
            exists = File.Exists(candidate);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (!exists)
        {
            return false;
        }

        path = candidate;
        return true;
    }

    // ---- the sentences ----

    // The words live in FileText since 2026-10-22 (review N11); these keep their callers.
    public static string NotFound(string path) => FileText.ImageNotFound(path);
    public static string TooLarge(string path) => FileText.ImageTooLarge(path);
    public static string CouldNotRead(string path) => FileText.ImageCouldNotRead(path);
    public static string Unsupported(string path) => FileText.ImageUnsupported(path);

    /// <summary>A picture file that could not be opened (2026-10-22, review K11). Pinned.</summary>
    public static string Unreadable(string path) => FileText.ImageUnreadable(path);

    /// <summary>The sentence for a <paramref name="failure"/>; <see cref="ImageLoadFailure.None"/> has none (throws).</summary>
    public static string Notice(ImageLoadFailure failure, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return failure switch
        {
            ImageLoadFailure.NotFound => NotFound(path),
            ImageLoadFailure.TooLarge => TooLarge(path),
            ImageLoadFailure.CouldNotRead => CouldNotRead(path),
            ImageLoadFailure.Unsupported => Unsupported(path),
            ImageLoadFailure.Unreadable => Unreadable(path),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Not a failure."),
        };
    }

    /// <summary>
    /// Reads <paramref name="path"/> into what the model gets, or null with <paramref name="error"/>
    /// one of the sentences above. Never throws for a bad file.
    /// </summary>
    public static ImageAttachment? Load(string path, out string? error)
    {
        if (TryLoad(path, out var image, out var failure))
        {
            error = null;
            return image;
        }

        error = Notice(failure, path);
        return null;
    }

    /// <summary>
    /// <see cref="Load"/> with the reason as an <see cref="ImageLoadFailure"/> instead of a sentence,
    /// for a caller with sentences of its own (<see cref="WorkingDirectory.ReadImage"/>). A file the
    /// process may not open reads as <see cref="ImageLoadFailure.CouldNotRead"/> like one the codecs
    /// refuse: the line cannot tell them apart either. Never throws for a bad file.
    /// </summary>
    public static bool TryLoad(string path, out ImageAttachment? image, out ImageLoadFailure failure) => TryLoad(path, out image, out failure, out _);

    /// <summary><see cref="TryLoad(string, out ImageAttachment?, out ImageLoadFailure)"/> with the failed read's message (review K11).</summary>
    public static bool TryLoad(string path, out ImageAttachment? image, out ImageLoadFailure failure, out string detail)
    {
        ArgumentNullException.ThrowIfNull(path);
        image = null;
        failure = ImageLoadFailure.None;
        detail = "";
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                failure = ImageLoadFailure.NotFound;
                return false;
            }

            if (info.Length > MaxFileBytes)
            {
                failure = ImageLoadFailure.TooLarge;
                return false;
            }

            bytes = RegularFiles.ReadAllBytes(path);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            failure = ImageLoadFailure.Unreadable;
            detail = e.Message;
            return false;
        }

        return TryLoad(bytes, path, out image, out failure);
    }

    /// <summary>
    /// <see cref="Load(string, out string?)"/> for an image that is already bytes — a picture off
    /// the clipboard, named <see cref="ClipboardName"/>; the file overload reads its file into
    /// this. Null with the sentence when the bytes are over the cap or not an image.
    /// </summary>
    public static ImageAttachment? Load(byte[] bytes, string name, out string? error)
    {
        if (TryLoad(bytes, name, out var image, out var failure))
        {
            error = null;
            return image;
        }

        error = Notice(failure, name);
        return null;
    }

    /// <summary>
    /// The decode behind every overload: <paramref name="bytes"/> are an image file's, whatever
    /// they came from, and <paramref name="name"/> is what the attachment is called. The same
    /// caps and the same re-encode as a file; <see cref="ImageLoadFailure.NotFound"/> never.
    /// Never throws for bad bytes.
    /// </summary>
    public static bool TryLoad(byte[] bytes, string name, out ImageAttachment? image, out ImageLoadFailure failure) =>
        TryLoad(bytes, name, out image, out failure, photo: false);

    /// <summary>
    /// <see cref="TryLoad(byte[], string, out ImageAttachment?, out ImageLoadFailure)"/> for a picture that is a photo or a
    /// painting rather than a screenshot (2026-10-03, the user's ask after a session of ComfyUI pictures outgrew what the
    /// embedded llama-server would take in one request): with <paramref name="photo"/> a picture with no alpha goes to the
    /// model as a JPEG at <see cref="JpegQuality"/> even when it fits — a 1152×896 render is ~1.6 MB as PNG and ~250 KB
    /// as JPEG, and every request carries it again. One with transparency stays as it would have been (a JPEG would
    /// flatten it); a JPEG source is as before. Only the bytes for the model: the file on disk is the caller's and is never touched.
    /// </summary>
    public static bool TryLoad(byte[] bytes, string name, out ImageAttachment? image, out ImageLoadFailure failure, bool photo)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        image = null;
        failure = ImageLoadFailure.None;
        if (bytes.Length > MaxFileBytes)
        {
            failure = ImageLoadFailure.TooLarge;
            return false;
        }

        if (!ImageCodecs.Available)
        {
            failure = ImageLoadFailure.Unsupported;
            return false;
        }

        try
        {
            var loaded = ImageFileInfo.Load(bytes);
            if (loaded.Frames.Count == 0)
            {
                failure = ImageLoadFailure.CouldNotRead;
                return false;
            }

            var frame = loaded.Frames[0];
            if ((long)frame.Width * frame.Height > MaxPixels)
            {
                failure = ImageLoadFailure.TooLarge;
                return false;
            }

            bool fits = frame.Width <= MaxSide && frame.Height <= MaxSide;
            string source = loaded.MimeType ?? "";
            bool keepsPng = string.Equals(source, Png, StringComparison.OrdinalIgnoreCase);
            bool keepsJpeg = string.Equals(source, Jpeg, StringComparison.OrdinalIgnoreCase);
            bool toJpeg = photo && !keepsJpeg && !frame.HasAlpha;
            // Sent without its metadata, losslessly, whatever model takes it (2026-10-22, review I10, the fix plan's D5: a picture that fit
            // went byte for byte, EXIF and GPS and all); its own orientation written back when it has one MagicScaler doesn't see (U3).
            if (fits && !toJpeg && (keepsPng || keepsJpeg) && frame.ExifOrientation == Orientation.Normal
                && Images.MetadataStripper.Strip(bytes, Images.MetadataStripper.ExifOrientation(bytes) ?? 1).Result is { } stripped)
            {
                // A clean picture still goes as it was, byte for byte.
                image = new ImageAttachment(name, stripped.Removed == Images.MetadataKinds.None ? bytes : stripped.Bytes, keepsPng ? Png : Jpeg, frame.Width, frame.Height);
                return true;
            }

            string mediaType = toJpeg ? Jpeg : SentAs(source);
            var settings = new ProcessImageSettings
            {
                Width = MaxSide,
                Height = MaxSide,
                ResizeMode = CropScaleMode.Max,
                EncoderOptions = mediaType == Jpeg ? new JpegEncoderOptions(JpegQuality, ChromaSubsampleMode.Default, false) : PngEncoderOptions.Default,
            };
            if (!settings.TrySetEncoderFormat(mediaType))
            {
                failure = ImageLoadFailure.CouldNotRead;
                return false;
            }

            using var output = new MemoryStream();
            var result = MagicImageProcessor.ProcessImage(bytes, output, settings);
            image = new ImageAttachment(name, output.ToArray(), mediaType, result.Settings.Width, result.Settings.Height);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            failure = ImageLoadFailure.CouldNotRead;
            return false;
        }
    }

    /// <summary>What a picture pasted off the clipboard is called: the nth image on the line, and always a PNG in truth (a PNG block stays one, a DIB re-encodes as one). Pinned.</summary>
    public static string ClipboardName(int number) => $"clipboard-{number}.png";

    /// <summary>
    /// The extension an image file's bytes call for (later still on 2026-09-24, a pasted picture saved for
    /// <c>generate_image</c>): <c>.bmp</c> for the <c>BM</c> header the clipboard's DIB becomes, <c>.jpg</c> for a JPEG's
    /// <c>FF D8 FF</c>, <c>.png</c> for the rest (the clipboard's own PNG block among them). Pinned.
    /// </summary>
    public static string ExtensionOf(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("BM"u8) ? ".bmp"
        : bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? ".jpg"
        : ".png";

    /// <summary>
    /// The extension a picture is saved with: its media type's (png, jpeg/jpg, gif, webp, bmp), else what its bytes say (<see cref="ExtensionOf"/>).
    /// Lifted out of the plan's references on 2026-10-29 so a session export names its pictures the same way. Pinned.
    /// </summary>
    public static string ExtensionFor(string mediaType, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ExtensionOf(bytes),
        };
    }

    /// <summary>
    /// What a re-encoded image is sent as: JPEG for a JPEG or WebP source (a photo), PNG for the rest (a drawing, a screenshot, a
    /// TIFF). On a Mac a HEIC/HEIF or AVIF source is a photo too (2026-10-07). Pinned.
    /// </summary>
    public static string SentAs(string sourceMediaType)
    {
        ArgumentNullException.ThrowIfNull(sourceMediaType);
        // A photo format goes as a photo on every system (2026-10-22, review V6: on Windows HEIC, HEIF, AVIF and JXL went as PNG, 5–7 MB
        // against 400 KB, riding every request and eating the picture budget about fifteen times faster).
        if (PhotoTypes.Contains(sourceMediaType, StringComparer.OrdinalIgnoreCase))
        {
            return Jpeg;
        }

        return sourceMediaType.Equals(Jpeg, StringComparison.OrdinalIgnoreCase) || sourceMediaType.Equals("image/webp", StringComparison.OrdinalIgnoreCase) ? Jpeg : Png;
    }

    /// <summary>The source types a Mac sends as JPEG besides JPEG and WebP (2026-10-07): an iPhone's HEIC/HEIF, and AVIF.</summary>
    public static readonly IReadOnlyList<string> MacPhotoTypes = ["image/heic", "image/heif", "image/avif"];

    /// <summary>The photo formats sent as JPEG on every system (review V6): <see cref="MacPhotoTypes"/> and JPEG XL.</summary>
    public static readonly IReadOnlyList<string> PhotoTypes = [.. MacPhotoTypes, "image/jxl"];
}
