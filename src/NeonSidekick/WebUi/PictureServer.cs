using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using NeonSidekick.Diagnostics;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.WebUi;

/// <summary>
/// A picture the page may ask for, as the screen holds it (2026-10-14): its name, its file when it has one (found when it was drawn, never
/// named by the page), and the attachment's bytes (at most <see cref="Files.ImageFile.MaxSide"/> a side, PNG or JPEG) while they are kept.
/// </summary>
public sealed record WebPictureSource(string Name, string? FullPath, byte[]? Bytes, string MediaType);

/// <summary>The three sizes a picture is served at: the transcript's grid, the overlay's, and the original.</summary>
public enum PictureSize
{
    Thumb,
    Mid,
    Full,
}

/// <summary>
/// The Web UI's pictures (2026-10-14): <c>GET /api/picture/{run}/{id}/{thumb|mid|full}</c> answered from a <see cref="WebPictureSource"/>.
/// <c>thumb</c> is <see cref="ThumbSide"/> px, from the kept bytes when there are any, else one decode of the file (hybrid scaling, under
/// <see cref="MaxDecodeBytes"/> and <see cref="MaxDecodePixels"/>); <c>mid</c>, the overlay's, the kept bytes as they are, else the file
/// decoded to <see cref="MidSide"/>; <c>full</c> the original file when its signature is one every browser draws (<see cref="Sniff"/>; never
/// SVG), else re-encoded (HEIC, TIFF, JXL: JPEG at <see cref="FullQuality"/>, PNG with alpha). Every answer has an ETag (the file's length and
/// write time, or a hash of the bytes; <c>thumb</c>'s and the re-encodes' carry <see cref="EncodeVersion"/>) and <c>private, no-cache</c>, so a
/// browser re-checks and gets a 304 before anything is decoded; a HEAD decodes nothing. Decodes run two at a time, one per picture and size
/// however many ask (<see cref="_inflight"/>), and their output is kept in an LRU of about <see cref="CacheBytes"/>. An expected failure (no
/// file, I/O, access, a decode) is a 404 or a 415 and a Debug line, never an error in the transcript. Any thread.
/// </summary>
internal sealed class PictureServer
{
    /// <summary>The grid's longest side.</summary>
    public const int ThumbSide = 640;

    /// <summary>The overlay's longest side for a picture decoded from its file.</summary>
    public const int MidSide = 2048;

    /// <summary>The largest file decoded for a size.</summary>
    public const long MaxDecodeBytes = 20L * 1024 * 1024;

    /// <summary>The most pixels decoded for a size.</summary>
    public const long MaxDecodePixels = 40_000_000;

    /// <summary>About the most bytes of decoded output kept.</summary>
    public const long CacheBytes = 32L * 1024 * 1024;

    /// <summary>The JPEG quality of a thumbnail and the overlay's size.</summary>
    public const int ThumbQuality = 82;

    /// <summary>The JPEG quality of an original re-encoded.</summary>
    public const int FullQuality = 90;

    /// <summary>Changes when the sizes or qualities do, so a browser's cached thumbnail is asked again.</summary>
    public const string EncodeVersion = "e1";

    private const string Category = "WebUi";

    private readonly SemaphoreSlim _decodes = new(2);
    private readonly ConcurrentDictionary<string, Lazy<Task<Encoded?>>> _inflight = new(StringComparer.Ordinal);
    private readonly object _cacheGate = new();
    private readonly LinkedList<(string Key, Encoded Value)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Encoded Value)>> _cache = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<byte[], string> _hashes = new();
    private long _cached;

    /// <summary>A decoded answer: its bytes and media type.</summary>
    internal sealed record Encoded(byte[] Bytes, string ContentType);

    /// <summary>The size a request's word names, or null.</summary>
    public static PictureSize? SizeOf(string word) => word switch
    {
        "thumb" => PictureSize.Thumb,
        "mid" => PictureSize.Mid,
        "full" => PictureSize.Full,
        _ => null,
    };

    /// <summary>
    /// The media type of a picture every browser draws, by its first bytes: PNG, JPEG, GIF, WebP, AVIF or BMP; null for anything else (HEIC,
    /// TIFF, JXL, SVG, not a picture). Pure.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8) && (head[8..12].SequenceEqual("avif"u8) || head[8..12].SequenceEqual("avis"u8)))
        {
            return "image/avif";
        }

        return head.Length >= 14 && head.StartsWith("BM"u8) ? "image/bmp" : null;
    }

    /// <summary>Answers one picture request: the headers, a 304, a HEAD, or the picture.</summary>
    public async Task ServeAsync(HttpContext context, WebPictureSource source, PictureSize size)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        var response = context.Response;
        response.Headers.CacheControl = "private, no-cache";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        bool head = HttpMethods.IsHead(context.Request.Method);
        try
        {
            string? file = source.FullPath is { } path && File.Exists(path) ? path : null;
            bool fromFile = size == PictureSize.Full ? file is not null : source.Bytes is null && file is not null;
            if (!fromFile && source.Bytes is null)
            {
                Gone(response, source, "its file is gone");
                return;
            }

            if (fromFile)
            {
                await FromFileAsync(context, source, file!, size, head).ConfigureAwait(false);
            }
            else
            {
                await FromBytesAsync(context, source, source.Bytes!, size, head).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!response.HasStarted)
            {
                Gone(response, source, e.Message);
            }
        }
    }

    private async Task FromFileAsync(HttpContext context, WebPictureSource source, string file, PictureSize size, bool head)
    {
        var response = context.Response;
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        long length = stream.Length;
        string stamp = "f" + length.ToString("x", CultureInfo.InvariantCulture) + "-" + File.GetLastWriteTimeUtc(file).Ticks.ToString("x", CultureInfo.InvariantCulture);
        var headBytes = new byte[32];
        int read = await stream.ReadAtLeastAsync(headBytes, headBytes.Length, throwOnEndOfStream: false, context.RequestAborted).ConfigureAwait(false);
        string? sniffed = Sniff(headBytes.AsSpan(0, read));
        if (size == PictureSize.Full && sniffed is not null)
        {
            // The original as it is: the same handle sniffed and sent.
            if (NotModified(context, Tag(stamp, "o")))
            {
                return;
            }

            response.ContentType = sniffed;
            response.ContentLength = length;
            Inline(response, source.Name);
            if (!head)
            {
                stream.Position = 0;
                await stream.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
            }

            return;
        }

        string tag = Tag(stamp, Word(size) + EncodeVersion);
        if (NotModified(context, tag) || head)
        {
            response.ContentType = "image/jpeg";
            return;
        }

        if (length > MaxDecodeBytes)
        {
            Unsupported(response, source, "over " + (MaxDecodeBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB");
            return;
        }

        // Read by the decode itself, not through this request's handle: the decode is shared and may outlive this request. The cache's key names
        // the file as well as its stamp (2026-10-19, the review: two pictures of one length and write time, a coarse clock's or an archive's,
        // shared one cached thumbnail); the ETag needs no path, the browser keeping it per address.
        var encoded = await EncodeOnceAsync(tag + "|" + file, () => File.ReadAllBytesAsync(file), size, context.RequestAborted).ConfigureAwait(false);
        await SendAsync(context, source, encoded).ConfigureAwait(false);
    }

    private async Task FromBytesAsync(HttpContext context, WebPictureSource source, byte[] bytes, PictureSize size, bool head)
    {
        var response = context.Response;
        string stamp = "b" + _hashes.GetValue(bytes, b => Convert.ToHexStringLower(SHA256.HashData(b))[..20]);
        if (size != PictureSize.Thumb)
        {
            // The attachment's bytes are the overlay's size already, and the original when there is no file.
            if (NotModified(context, Tag(stamp, "o")))
            {
                return;
            }

            response.ContentType = Sniff(bytes) ?? source.MediaType;
            response.ContentLength = bytes.Length;
            Inline(response, source.Name);
            if (!head)
            {
                await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            }

            return;
        }

        string tag = Tag(stamp, Word(size) + EncodeVersion);
        if (NotModified(context, tag) || head)
        {
            response.ContentType = "image/jpeg";
            return;
        }

        var encoded = await EncodeOnceAsync(tag, () => Task.FromResult(bytes), size, context.RequestAborted).ConfigureAwait(false);
        await SendAsync(context, source, encoded).ConfigureAwait(false);
    }

    private static async Task SendAsync(HttpContext context, WebPictureSource source, Encoded? encoded)
    {
        var response = context.Response;
        if (encoded is null)
        {
            Unsupported(response, source, "the codecs could not read it");
            return;
        }

        response.ContentType = encoded.ContentType;
        response.ContentLength = encoded.Bytes.Length;
        await response.Body.WriteAsync(encoded.Bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static string Word(PictureSize size) => size switch
    {
        PictureSize.Thumb => "t",
        PictureSize.Mid => "m",
        _ => "r",
    };

    private static string Tag(string stamp, string what) => "\"" + stamp + "-" + what + "\"";

    private static bool NotModified(HttpContext context, string tag)
    {
        context.Response.Headers.ETag = tag;
        if (context.Request.Headers.IfNoneMatch == tag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return true;
        }

        return false;
    }

    private static void Inline(HttpResponse response, string name) =>
        response.Headers.ContentDisposition = "inline; filename*=UTF-8''" + Uri.EscapeDataString(name);

    private static void Gone(HttpResponse response, WebPictureSource source, string why)
    {
        DiagnosticLog.Debug(Category, "Web UI: no picture for " + source.Name + ": " + why + ".");
        response.Headers.Remove("ETag");
        response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static void Unsupported(HttpResponse response, WebPictureSource source, string why)
    {
        DiagnosticLog.Debug(Category, "Web UI: " + source.Name + " is not served at this size: " + why + ".");
        response.Headers.Remove("ETag");
        response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
    }

    /// <summary>The output for <paramref name="key"/>: the cache's, the decode another request runs now, or a decode of its own.</summary>
    private async Task<Encoded?> EncodeOnceAsync(string key, Func<Task<byte[]>> bytes, PictureSize size, CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Value;
            }
        }

        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<Encoded?>>(() => DecodeAsync(k, bytes, size)));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lazy.Value.IsCompleted)
            {
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<Encoded?>>>(key, lazy));
            }
        }
    }

    private async Task<Encoded?> DecodeAsync(string key, Func<Task<byte[]>> read, PictureSize size)
    {
        // Not the request's token: the decode is shared, and its output cached for the next asker.
        await _decodes.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[] bytes = await read().ConfigureAwait(false);
            var encoded = await Task.Run(() => Encode(bytes, size)).ConfigureAwait(false);
            if (encoded is not null)
            {
                Keep(key, encoded);
            }

            return encoded;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug(Category, "Web UI: a picture could not be read: " + e.Message);
            return null;
        }
        finally
        {
            _decodes.Release();
            _inflight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// <paramref name="bytes"/> at <paramref name="size"/>: no larger than its side (never enlarged), JPEG, or PNG when it has alpha; null when
    /// the codecs refuse it or it is over <see cref="MaxDecodePixels"/>.
    /// </summary>
    internal static Encoded? Encode(byte[] bytes, PictureSize size)
    {
        try
        {
            var info = ImageFileInfo.Load(bytes);
            if (info.Frames.Count == 0)
            {
                return null;
            }

            var frame = info.Frames[0];
            if ((long)frame.Width * frame.Height > MaxDecodePixels)
            {
                return null;
            }

            int side = size switch
            {
                PictureSize.Thumb => ThumbSide,
                PictureSize.Mid => MidSide,
                _ => Math.Max(frame.Width, frame.Height),
            };
            string type = frame.HasAlpha ? "image/png" : "image/jpeg";
            bool fits = frame.Width <= side && frame.Height <= side;
            var settings = new ProcessImageSettings
            {
                // Never enlarged: a picture inside the side keeps its own.
                Width = fits ? frame.Width : side,
                Height = fits ? frame.Height : side,
                ResizeMode = CropScaleMode.Max,
                HybridMode = HybridScaleMode.FavorQuality,
                EncoderOptions = frame.HasAlpha ? PngEncoderOptions.Default : new JpegEncoderOptions(size == PictureSize.Full ? FullQuality : ThumbQuality, ChromaSubsampleMode.Default, false),
            };
            if (!settings.TrySetEncoderFormat(type))
            {
                return null;
            }

            using var output = new MemoryStream();
            _ = MagicImageProcessor.ProcessImage(bytes, output, settings);
            return new Encoded(output.ToArray(), type);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Debug(Category, "Web UI: a picture could not be decoded: " + e.GetType().Name + ": " + e.Message);
            return null;
        }
    }

    private void Keep(string key, Encoded encoded)
    {
        long size = encoded.Bytes.LongLength;
        if (size > CacheBytes / 4)
        {
            return;
        }

        lock (_cacheGate)
        {
            if (_cache.ContainsKey(key))
            {
                return;
            }

            _cache[key] = _lru.AddFirst((key, encoded));
            _cached += size;
            while (_cached > CacheBytes && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
                _cached -= last.Value.Value.Bytes.LongLength;
            }
        }
    }

    /// <summary>The decoded outputs kept now (tests).</summary>
    internal int CachedCount
    {
        get
        {
            lock (_cacheGate)
            {
                return _cache.Count;
            }
        }
    }
}
