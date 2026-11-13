using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Win32.SafeHandles;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.WebUi;

/// <summary>
/// What the Web UI's two file servers share (2026-11-12, the video-files plan): <see cref="PictureServer"/>'s original-file answer and
/// <see cref="MediaFileServer"/>'s video file. A file's stamp (its length and write time), its ETag, the 304 for a matching
/// <c>If-None-Match</c>, the inline <c>filename*</c>, and the 404 with a Debug line for a file that is gone. Pure over the response.
/// </summary>
internal static class WebFileResponse
{
    private const string Category = "WebUi";

    /// <summary>
    /// A file's stamp: its length and last write time, in hex. A file that grows or is replaced gets a new one. Read from the open
    /// <paramref name="handle"/> (2026-11-13, the review's fix), the one the bytes come from, never the path again: a file replaced between
    /// the open and a second look at its name can't lend the new one's time to the old one's bytes.
    /// </summary>
    public static string Stamp(long length, SafeFileHandle handle) =>
        "f" + length.ToString("x", CultureInfo.InvariantCulture) + "-" + File.GetLastWriteTimeUtc(handle).Ticks.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>The ETag for <paramref name="stamp"/> served as <paramref name="what"/>, quoted.</summary>
    public static string Tag(string stamp, string what) => "\"" + stamp + "-" + what + "\"";

    /// <summary>The ETag set; a 304 when the request's <c>If-None-Match</c> is it (true: nothing more to send).</summary>
    public static bool NotModified(HttpContext context, string tag)
    {
        context.Response.Headers.ETag = tag;
        if (context.Request.Headers.IfNoneMatch == tag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return true;
        }

        return false;
    }

    /// <summary>Shown in the page, under <paramref name="name"/> should the browser save it.</summary>
    public static void Inline(HttpResponse response, string name) =>
        response.Headers.ContentDisposition = "inline; filename*=UTF-8''" + Uri.EscapeDataString(name);

    /// <summary>
    /// A 404 (by default) for a file that is gone or unreadable, its reason in a Debug line, never in the transcript. The video file server
    /// (2026-11-13) says which: <paramref name="status"/> 410 for a file not found, 503 for one it couldn't read (locked, denied, a share
    /// that doesn't answer), so the page can word it.
    /// </summary>
    public static void Gone(HttpResponse response, string line, int status = StatusCodes.Status404NotFound)
    {
        DiagnosticLog.Debug(Category, line);
        response.Headers.Remove("ETag");
        response.Headers.Remove("Content-Disposition");
        response.StatusCode = status;
    }

    /// <summary>
    /// The status for a file that couldn't be opened or read (2026-11-13): 410 when it or its folder isn't there, 503 for any other failure
    /// (locked, access denied, a share that doesn't answer), which may pass.
    /// </summary>
    public static int StatusFor(Exception failure) =>
        failure is FileNotFoundException or DirectoryNotFoundException ? StatusCodes.Status410Gone : StatusCodes.Status503ServiceUnavailable;
}
