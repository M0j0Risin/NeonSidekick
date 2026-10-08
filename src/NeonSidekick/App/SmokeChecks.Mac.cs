using System.Runtime.Versioning;
using NeonSidekick.UI;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>console:termios</c> (2026-10-06, the macOS build): the terminal reader's libc doors on the published binary. The
    /// struct is macOS's 72 bytes; <c>isatty</c> binds and answers (0 or 1 both pass: the smoke's stdin may be a terminal or
    /// not); where stdin is a terminal its attributes read back, and the app's raw mode of them reads as raw. Nothing is set:
    /// the smoke must leave the terminal as it found it.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static unsafe SmokeCheck ProbeTermios()
    {
        const string name = "console:termios";
        try
        {
            int size = sizeof(TermiosNative.Termios);
            if (size != 72)
            {
                return new SmokeCheck(name, false, $"struct termios is {size} bytes, macOS's is 72");
            }

            int tty = TermiosNative.IsATty(TermiosNative.StdinFileNo);
            if (tty != 1)
            {
                return new SmokeCheck(name, true, "libc bound; stdin is not a terminal, attributes not read");
            }

            TermiosNative.Termios current;
            if (TermiosNative.TcGetAttr(TermiosNative.StdinFileNo, &current) != 0)
            {
                return new SmokeCheck(name, false, "tcgetattr failed on a terminal stdin, errno " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return TermiosNative.IsRaw(TermiosNative.Raw(current))
                ? new SmokeCheck(name, true, "libc bound; the terminal's attributes read, raw mode computed")
                : new SmokeCheck(name, false, "the raw mode of the terminal's attributes does not read as raw");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
    }

    /// <summary>
    /// <c>audio:audioqueue</c> (2026-10-07, sound on a Mac): AudioToolbox and Core Audio bind on the published binary, and
    /// where Core Audio names a default output an output queue at the Kokoro format is created and disposed (never started:
    /// nothing plays). A Mac with no output device (GitHub's runner may have none) passes as "no device"; whether sound comes
    /// out is <c>--audio-check</c>'s job, as on Windows.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static SmokeCheck ProbeAudioQueue()
    {
        const string name = "audio:audioqueue";
        try
        {
            if (Audio.AudioQueuePlayback.OutputDeviceCount() == 0)
            {
                return new SmokeCheck(name, true, "Core Audio bound; no default output device");
            }

            int status = Audio.AudioQueuePlayback.ProbeDefaultDevice(Audio.PcmFormat.Kokoro);
            return status == 0
                ? new SmokeCheck(name, true, $"output queue created and disposed at {Audio.PcmFormat.Kokoro}")
                : new SmokeCheck(name, false, Audio.AudioQueueText.Failed("AudioQueueNewOutput", status));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
    }

    /// <summary>
    /// <c>audio:audioqueue-in</c>: the capture twin of <see cref="ProbeAudioQueue"/> at the Whisper format, plus the
    /// microphone permission read through AVFoundation (any answer passes; it is reported). The input queue is never started,
    /// so the smoke never brings up the permission question.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static SmokeCheck ProbeAudioQueueIn()
    {
        const string name = "audio:audioqueue-in";
        try
        {
            long? permission = Audio.AudioQueueNative.MicrophoneAuthorization();
            if (permission is null)
            {
                return new SmokeCheck(name, false, "AVFoundation's AVCaptureDevice could not be reached for the microphone permission");
            }

            string access = permission.Value switch
            {
                Audio.MicrophoneAccess.Authorized => "allowed",
                Audio.MicrophoneAccess.Denied => "denied",
                Audio.MicrophoneAccess.Restricted => "restricted",
                _ => "not asked yet",
            };

            if (Audio.AudioQueueCapture.InputDeviceCount() == 0)
            {
                return new SmokeCheck(name, true, $"Core Audio bound; no default input device; microphone permission {access}");
            }

            int status = Audio.AudioQueueCapture.ProbeDefaultDevice(Audio.PcmFormat.Whisper);
            return status == 0
                ? new SmokeCheck(name, true, $"input queue created and disposed at {Audio.PcmFormat.Whisper}; microphone permission {access}")
                : new SmokeCheck(name, false, Audio.AudioQueueText.Failed("AudioQueueNewInput", status));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
    }

    /// <summary>
    /// <c>keys:keychain</c> (2026-10-06, the macOS build): the Keychain doors on the published binary — a throwaway generic
    /// password written, read back, replaced and deleted under a target nobody uses. Never the app's own key: a new build's
    /// signature makes the Keychain ask before reading an item an older build made, and the smoke would wait on that dialog.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static SmokeCheck ProbeKeychain()
    {
        const string name = "keys:keychain";
        const string secret = "smoke-päss-🔑";
        string target = "NeonSidekick.smoke/" + Guid.NewGuid().ToString("N");
        try
        {
            if (Sql.WindowsCredentials.WriteGeneric(target, "smoke", secret).Error is { } writeError)
            {
                return new SmokeCheck(name, false, "Keychain write failed: " + writeError);
            }

            if (Sql.WindowsCredentials.ReadGeneric(target) is not { Value: secret })
            {
                return new SmokeCheck(name, false, "the Keychain item did not read back");
            }

            if (Sql.WindowsCredentials.WriteGeneric(target, "smoke", secret + "2").Error is { } replaceError
                || Sql.WindowsCredentials.ReadGeneric(target).Value != secret + "2")
            {
                return new SmokeCheck(name, false, "the Keychain item was not replaced");
            }

            return Sql.WindowsCredentials.DeleteGeneric(target) && Sql.WindowsCredentials.ReadGeneric(target).NotFound
                ? new SmokeCheck(name, true, "generic password written, read, replaced and deleted")
                : new SmokeCheck(name, false, "the Keychain item was not deleted");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
        finally
        {
            Sql.WindowsCredentials.DeleteGeneric(target);
        }
    }
}
