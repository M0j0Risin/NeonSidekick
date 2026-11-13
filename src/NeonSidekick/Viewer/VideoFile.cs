using System.Security.Cryptography;

namespace NeonSidekick.Viewer;

/// <summary>
/// A video file on this computer (2026-11-12, the video-files plan): which names the app plays itself, the type a browser is told,
/// the play's token, and the page's own error codes beside <c>MediaError</c>'s. Portable.
///
/// <para>The types are the ones both engines play in an HTML5 <c>&lt;video&gt;</c>: MP4 and M4V (H.264/AAC), MOV, WebM, and MKV.
/// Chromium has no Matroska of its own, only WebM, which is a Matroska subset, so an <c>.mkv</c> holding VP8/VP9/AV1 (often
/// H.264 too) plays and one holding HEVC may not: it is served as <c>video/webm</c>, and the docs say an MKV "may play", the
/// default app the way out. No <c>.ogv</c>: Chromium dropped Theora (2024), and <c>OpenRules</c>' allow-list, which the
/// default-app fallback passes through, has no such type; every type here is on that list (a test holds it). <c>.avi</c>,
/// <c>.wmv</c> and the rest keep the default app.</para>
/// </summary>
public static class VideoFile
{
    /// <summary>The extensions the app plays itself, lower case with the dot.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".mp4", ".m4v", ".mov", ".webm", ".mkv"];

    /// <summary>
    /// The page's own code beside <c>MediaError</c>'s 1-4 (and outside YouTube's 2-150): the file's metadata loaded with no
    /// picture (<c>videoWidth</c> 0), an audio-only file or an HEVC file on a PC without the decoder playing sound over black. The page
    /// still sends it as an <c>error</c>, but <see cref="VideoMessages.Apply"/> reads it as <see cref="VideoSnapshot.NoPicture"/>
    /// (2026-11-13): the file plays, so no wait, status or Resume treats it as failed.
    /// </summary>
    public const int NoPictureCode = 1000;

    /// <summary>
    /// The app's own code for a file that was not there when its error came (moved or deleted): a missing <c>file:</c> video
    /// reports <c>MediaError</c> 4, the not-supported code (the spike, 2026-11-12), so the player that knows the path asks the disk.
    /// </summary>
    public const int GoneCode = 1001;

    /// <summary>
    /// The Web UI tab's code for a file refused over Tailscale Funnel (2026-11-12): <i>Web UI video files over Funnel</i> is off. The page reads
    /// the refusal's status when its <c>&lt;video&gt;</c> fails, which a <c>MediaError</c> can't say.
    /// </summary>
    public const int FunnelCode = 1002;

    /// <summary>
    /// A file error's code asked of the disk (2026-11-13, the review's fix): a missing <c>file:</c> video and a dead share both report
    /// <c>MediaError</c> 2 or 4, so for those two the player that knows the path stats it. <see cref="GoneCode"/> only when the file or its
    /// folder is not found; 2, "couldn't be read", when the stat fails otherwise (a share that doesn't answer, access denied); the code
    /// as it was when the file is there, for another code, or with no path. A stat on a dead share can block, so callers run it off any
    /// UI thread. On a Mac an unmounted <c>/Volumes</c> share reads as not found, so gone: the two can't be told apart there.
    /// </summary>
    public static int GoneIfMissing(int code, string? path)
    {
        if (code is not (2 or 4) || string.IsNullOrEmpty(path))
        {
            return code;
        }

        try
        {
            File.GetAttributes(path);
            return code;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return GoneCode;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 2;
        }
    }

    /// <summary>Whether <paramref name="path"/>'s name ends in one of <see cref="Extensions"/> (any case). The name only: a caller also asks <see cref="File.Exists(string)"/>.</summary>
    public static bool IsVideoPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string extension = Path.GetExtension(path.Trim());
        return Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The type a browser is told for <paramref name="extension"/> (with or without its dot); <c>application/octet-stream</c> for another.</summary>
    public static string ContentType(string? extension)
    {
        string e = (extension ?? "").Trim().TrimStart('.').ToLowerInvariant();
        return e switch
        {
            "mp4" or "m4v" => "video/mp4",
            "mov" => "video/quicktime",
            "webm" or "mkv" => "video/webm",
            _ => "application/octet-stream",
        };
    }

    /// <summary>A new play's token: 128 random bits as 32 lower-case hex digits.</summary>
    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Whether <paramref name="text"/> has a token's shape: exactly 32 hex digits.</summary>
    public static bool IsToken(string? text) => text is { Length: 32 } && text.All(char.IsAsciiHexDigit);
}
