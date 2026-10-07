using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.UI;

/// <summary>
/// The clipboard on macOS (2026-10-06, the macOS build, the user's call: pbcopy/pbpaste over AppKit through objc): text both
/// ways, <see cref="WindowsClipboard"/>'s two text doors with the same contract — nothing throws, a failure reads as null and
/// writes as false. A process-start site of its own, counted and deliberate: <c>/usr/bin/pbpaste</c> and <c>/usr/bin/pbcopy</c>,
/// by absolute path (never the PATH), the text over their redirected stdin and stdout (so .NET never hands them the terminal the
/// app's reader keeps raw), each waited for at most <see cref="Timeout"/>. No picture: images are left out of the macOS build
/// until it has codecs (<c>Files/ImageCodecs</c>). Cmd+V needs none of this — the terminal pastes it as a bracketed paste; this
/// is <c>/copy</c>, the input line's Ctrl+C copy, and the right click's and Alt+V's paste.
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacClipboard
{
    public const string Category = "Screen";

    public const string PasteProgram = "/usr/bin/pbpaste";
    public const string CopyProgram = "/usr/bin/pbcopy";

    /// <summary>How long a pbcopy or pbpaste may take before the clipboard counts as busy.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>The clipboard's text, or null for none (empty, not text, or pbpaste failed).</summary>
    public static string? TryReadText()
    {
        try
        {
            using var process = Process.Start(Start(PasteProgram));
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Timeout) || !read.Wait(Timeout))
            {
                Kill(process);
                return null;
            }

            string text = read.Result;
            return process.ExitCode == 0 && text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            DiagnosticLog.Debug(Category, "pbpaste failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard; false when pbcopy failed.</summary>
    public static bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using var process = Process.Start(Start(CopyProgram));
            if (process is null)
            {
                return false;
            }

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            if (!process.WaitForExit(Timeout))
            {
                Kill(process);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            DiagnosticLog.Debug(Category, "pbcopy failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>No pictures on macOS yet: always null.</summary>
    public static byte[]? TryReadImage() => null;

    private static ProcessStartInfo Start(string program)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };

        // pbcopy and pbpaste read and write the locale's encoding; UTF-8 whatever the user's shell set.
        start.Environment["LANG"] = "en_US.UTF-8";
        start.Environment["LC_CTYPE"] = "UTF-8";
        return start;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
