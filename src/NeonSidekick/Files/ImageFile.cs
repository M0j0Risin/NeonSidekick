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
}

/// <summary>
/// A pasted picture as it was before any downscale (later still on 2026-09-24): the bytes <c>generate_image</c> saves and
/// uploads when the model names the paste by its <c>[Image #N]</c> label, and a dropped file's own name — null for a
/// clipboard picture, which has none: the saver stamps it (<c>ComfyStudio.PastedStem</c>).
/// </summary>
public sealed record PastedPicture(byte[] Bytes, string? FileName);

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
/// <see cref="CouldNotRead"/>. Every failure is a sentence back to the line, never an exception.
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

    /// <summary>The extensions that make a pasted path an image, lower case with the dot.</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

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
    public static bool TryPastedPath(string paste, out string path)
    {
        ArgumentNullException.ThrowIfNull(paste);
        path = "";
        string candidate = paste.Trim();
        if (candidate.Contains('\n') || candidate.Contains('\r') || !TryPath(candidate, out string found))
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
    /// neither a path nor a run of paths makes the whole block text; blank lines are skipped.
    /// </summary>
    public static bool TryPastedPaths(string paste, out IReadOnlyList<string> paths)
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

            if (TryPath(line, out string one))
            {
                found.Add(one);
                continue;
            }

            foreach (var token in Tokens(line))
            {
                if (!TryPath(token, out string path))
                {
                    found.Clear();
                    return false;
                }

                found.Add(path);
            }
        }

        return found.Count > 0;
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

    // One trimmed line or token as a path: the quotes off, an image extension, fully qualified, a file that exists.
    private static bool TryPath(string candidate, out string path)
    {
        path = "";
        candidate = candidate.Trim();
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1].Trim();
        }

        if (candidate.Length == 0 || candidate.Contains('"') || !IsImagePath(candidate) || !System.IO.Path.IsPathFullyQualified(candidate))
        {
            // A bare "photo.png" inside a sentence must never resolve against the process's own directory.
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

    public static string NotFound(string path) => $"(image not attached: {path} was not found)";
    public static string TooLarge(string path) => $"(image not attached: {path} is over {MaxFileBytes / 1_000_000} MB or {MaxPixels / 1_000_000} megapixels)";
    public static string CouldNotRead(string path) => $"(image not attached: {path} could not be read as an image)";
    public static string Unsupported(string path) => $"(image not attached: {path}; {ImageCodecs.Unavailable})";

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
    public static bool TryLoad(string path, out ImageAttachment? image, out ImageLoadFailure failure)
    {
        ArgumentNullException.ThrowIfNull(path);
        image = null;
        failure = ImageLoadFailure.None;
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

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            failure = ImageLoadFailure.CouldNotRead;
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
            if (fits && !toJpeg && (keepsPng || keepsJpeg) && frame.ExifOrientation == Orientation.Normal)
            {
                image = new ImageAttachment(name, bytes, keepsPng ? Png : Jpeg, frame.Width, frame.Height);
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

    /// <summary>What a re-encoded image is sent as: JPEG for a JPEG or WebP source (a photo), PNG for the rest (a drawing, a screenshot). Pinned.</summary>
    public static string SentAs(string sourceMediaType)
    {
        ArgumentNullException.ThrowIfNull(sourceMediaType);
        return sourceMediaType.Equals(Jpeg, StringComparison.OrdinalIgnoreCase) || sourceMediaType.Equals("image/webp", StringComparison.OrdinalIgnoreCase) ? Jpeg : Png;
    }
}
