using System.Globalization;

namespace NeonSidekick.Camera;

/// <summary>
/// The camera's user-visible wording (2026-10-02): the failure sentences, the device list, the shot lines the transcript and
/// the model read. The <c>*Text.cs</c> shape: one place to change a sentence and its test.
/// </summary>
public static class CameraText
{
    /// <summary>A device the system lists without a friendly name.</summary>
    public const string UnnamedCamera = "Camera";

    /// <summary>The HRESULT an unmapped failure prints, <c>0x80004005</c>.</summary>
    public static string Hresult(int hr) => "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The kind an HRESULT stands for: the privacy block, a device another app holds, one that went away, or plain failure.</summary>
    public static CameraFailure FailureOf(int hr) => hr switch
    {
        unchecked((int)0x80070005) => CameraFailure.Blocked,
        unchecked((int)0xC00D3704) or unchecked((int)0xC00DABE1) or unchecked((int)0x80070020) => CameraFailure.InUse,
        unchecked((int)0xC00DABE0) or unchecked((int)0x8007048F) or unchecked((int)0x80070002) or unchecked((int)0x8007001F) => CameraFailure.Unplugged,
        _ => CameraFailure.Failed,
    };

    /// <summary>The sentence for a failure, the detail (an HRESULT and the call, a device name) after it where it helps. Pinned.</summary>
    public static string Failure(CameraFailure failure, string? detail) => Failure(failure, detail, OperatingSystem.IsMacOS());

    /// <summary>
    /// <see cref="Failure(CameraFailure, string?)"/> for Windows' wording or, with <paramref name="mac"/>, a Mac's (2026-10-07): the
    /// permission is the terminal's there (<see cref="MacBlocked"/>), a MacBook's own camera is off with its lid closed, and no Media
    /// Foundation is involved. Both are pinned on every system.
    /// </summary>
    public static string Failure(CameraFailure failure, string? detail, bool mac) => mac ? MacFailure(failure, detail) : WindowsFailure(failure, detail);

    /// <summary>System Settings' page holding the camera permission on macOS 13 and later.</summary>
    public const string MacSettingsPath = "System Settings › Privacy & Security › Camera";

    /// <summary>The Mac's sentence when the terminal is refused the camera. Pinned.</summary>
    public static string MacBlocked(string terminal) =>
        $"Camera access is off for {terminal}, so the camera cannot be used. Turn {terminal} on in {MacSettingsPath}, then try again.";

    /// <summary>The Mac's sentence while its question is unanswered. Pinned.</summary>
    public static string MacAsking(string terminal) =>
        $"macOS is asking whether {terminal} may use the camera: answer its question, then try again.";

    private static string MacFailure(CameraFailure failure, string? detail) => failure switch
    {
        CameraFailure.Blocked => MacBlocked(Audio.MicrophoneText.Terminal),
        CameraFailure.Asking => MacAsking(Audio.MicrophoneText.Terminal),
        CameraFailure.Restricted => "Camera access is restricted on this Mac (a configuration profile or Screen Time), so the camera cannot be used.",
        CameraFailure.Suspended => $"{(string.IsNullOrEmpty(detail) ? "The camera" : detail)} is off while the MacBook's lid is closed: open the lid, or pick another camera (/camera use).",
        CameraFailure.InUse => "Another app is holding the camera; close it (a video call, Photo Booth) and try again.",
        CameraFailure.Unsupported => "There is no camera support on this Mac (it needs macOS 14 or later).",
        _ => WindowsFailure(failure, detail),
    };

    private static string WindowsFailure(CameraFailure failure, string? detail) => failure switch
    {
        CameraFailure.NoCamera => "No camera is connected.",
        CameraFailure.Blocked => "Windows is blocking the camera for desktop apps: turn on Settings › Privacy & security › Camera › \"Let desktop apps access your camera\".",
        CameraFailure.InUse => "Another app is using the camera; close it (a video call, the Camera app) and try again.",
        CameraFailure.Unplugged => "The camera was unplugged or stopped responding." + Tail(detail),
        CameraFailure.NoMediaFoundation => "Windows' Media Foundation is not installed (Windows N needs the Media Feature Pack), so the camera cannot be read.",
        CameraFailure.NoFrames => "The camera sent no picture.",
        CameraFailure.Unsupported => "There is no camera support on this system (Windows only).",
        CameraFailure.Suspended => "The camera is suspended." + Tail(detail),
        CameraFailure.Asking => "The system is asking whether the camera may be used: answer its question, then try again.",
        CameraFailure.Restricted => "Camera access is restricted on this system, so the camera cannot be used.",
        _ => "The camera failed." + Tail(detail),
    };

    /// <summary>The sentence for a shot the sandbox refused to save (the <c>Error:</c> line after it). Pinned.</summary>
    public static string NotSaved(string error) => "The photo could not be saved: " + error;

    // ── camera_capture ──────────────────────────────────────────────────────

    /// <summary>The prompt shown when the model gave none. Pinned.</summary>
    public const string DefaultPrompt = "The model would like to see something.";

    /// <summary>
    /// The tool's result for a photo: <c>photo taken with MX Brio (1280x720), saved as camera/20261002-140203.jpg: the picture is in
    /// the next message</c> (the <c>view_image</c> shape). Pinned.
    /// </summary>
    public static string Taken(CameraShot shot)
    {
        ArgumentNullException.ThrowIfNull(shot);
        return $"photo taken with {shot.Device} ({N(shot.Image.Width)}x{N(shot.Image.Height)}), saved as {shot.RelativePath}: the picture is in the next message";
    }

    public const string Declined = "The user declined to take a photo. Carry on without it, and do not ask for one again unless they ask.";

    public const string Denied = "The user did not allow a photo. Carry on without it, and do not ask for one again unless they ask.";

    public const string AlreadyDeclined = "The user already declined a photo in this turn; carry on without it.";

    public const string NoScreen = "Error: camera_capture needs the app's screen to ask the user; there is none here.";

    public static string ToolFailed(string message) => "Error: " + message;

    // ── The panes ───────────────────────────────────────────────────────────

    /// <summary>The shutter pane's title for <c>/camera</c>, and for the model's request.</summary>
    public const string PaneTitle = "Camera";
    public const string ModelPaneTitle = "The model asks for a photo";

    public const string TakeRow = "Take the photo";
    public const string SendRow = "Send this photo to the model";
    public const string AttachRow = "Put this photo on the input line";
    public const string RetakeRow = "Take it again";
    public const string CancelRow = "Cancel";

    /// <summary>The title row's buttons: Space takes the photo, R takes it again.</summary>
    public const string SnapButton = "␣ snap";
    public const string RetakeButton = "r retake";

    public const string PaneHint = "Space takes the photo · R takes it again · Enter picks · ESC cancels";

    /// <summary>The status line before a shot: the camera still opening, or on and ready.</summary>
    public static string Waiting(string? device) => device is null ? "The camera is opening…" : $"{device} is on: frame the shot, then press Space.";

    /// <summary>The status under the prompt after a shot: <c>MX Brio: 1280x720 at 14:02:03, camera/20261002-140203.jpg</c>.</summary>
    public static string ShotStatus(CameraShot shot, DateTimeOffset local)
    {
        ArgumentNullException.ThrowIfNull(shot);
        return $"{shot.Device}: {N(shot.Image.Width)}x{N(shot.Image.Height)} at {local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}, {shot.RelativePath}";
    }

    /// <summary>The spinner's word while <c>/camera snap</c> takes a photo.</summary>
    public const string Taking = "Taking the photo…";

    /// <summary>The shutter pane's prompt for <c>/camera</c> itself.</summary>
    public const string OwnPrompt = "A photo for your next message.";

    /// <summary>The allow pane (the <c>model</c> shutter).</summary>
    public const string AllowTitle = "The model asks to take a photo";
    public const string DenyRow = "Deny";
    public const string AllowOnceRow = "Allow once";
    public const string AllowSessionRow = "Allow for this session";
    public const string AllowHint = "d deny · o once · s session · Enter picks · ESC denies";

    /// <summary>The live window's title while framing, and over a held shot.</summary>
    public static string LiveTitle(string? device) => $"📷 {device ?? UnnamedCamera} — live";

    public static string HeldTitle(string? device) => $"📷 {device ?? UnnamedCamera} — the shot";

    // ── The transcript ──────────────────────────────────────────────────────

    /// <summary>The note under the model's call for a photo it got: <c>📷 camera/20261002-140203.jpg</c>.</summary>
    public static string Note(string relativePath) => "📷 " + relativePath;

    /// <summary>The notice when a shot goes on the input line. Pinned.</summary>
    public static string Attached(string relativePath) => $"📷 {relativePath} is on the input line.";

    /// <summary>The toolbar's camera-on glyph (always drawn while the camera is open: privacy before preference).</summary>
    public const string Glyph = "📷";

    /// <summary>The hint row's word for the camera when the toolbar is hidden.</summary>
    public const string OnHint = "📷 camera on";

    /// <summary>The stored session's stand-in for a camera picture (<c>Camera keep in sessions</c> off). Pinned.</summary>
    public static string NotKept(string path) => $"[camera photo not kept in the session: {path}]";

    /// <summary>The notice when a turn was refused a picture by an embedded model without vision.</summary>
    public const string NoVision = "The model in use reads no pictures: the camera is not offered.";

    // ── /camera ─────────────────────────────────────────────────────────────

    public const string Word = "/camera";

    public const string HelpSummary = "take a photo with the camera";

    public const string Usage = "Usage: /camera [snap | list | use <n|name> | live | watch [seconds|off] | off]";

    public static string Unknown(string word) => $"'{word}' is not a /camera word. " + Usage;

    public const string ListHeader = "Cameras:";

    /// <summary>One device line: <c>  1. MX Brio (in use)</c>, the saved one marked.</summary>
    public static string ListLine(int number, CameraDevice device, bool chosen) =>
        $"  {N(number)}. {device.Name}{(chosen ? "  ← chosen" : "")}";

    public const string ListFirstNote = "No camera is chosen: the first one is used (/camera use <n|name>).";

    public static string Using(string name) => $"Camera: {name}.";

    public static string NoSuchCamera(string what) => $"No camera '{what}' is connected (/camera list).";

    public static string Off(int released) => released == 0 ? "The camera was not held by /camera live or watch." : "The camera is released (it closes in a few seconds).";

    public const string NeedsScreen = "/camera needs the app's screen for that; in headless mode only /camera list works.";

    public const string LiveOn = "The camera is live in its own window (close it, or /camera off, to stop).";

    /// <summary>Ctrl+Alt+V's notice when it closes <c>/camera live</c>'s window (later on 2026-10-02, the user's ask: the chord toggles).</summary>
    public const string LiveOff = "The camera's live window is closed (the camera closes in a few seconds).";

    public const string NoViewer = "There is no picture viewer here.";

    // ── Botchat ─────────────────────────────────────────────────────────────

    /// <summary>The notice at a botchat's start with <c>Botchat camera</c> on. Pinned.</summary>
    public const string BotChatOn = "📷 The bots can see you: each turn takes a picture from the camera (kept in memory only).";

    /// <summary>The notice when the camera failed during a botchat (once): the chat goes on without it.</summary>
    public static string BotChatFailed(string message) => message + " The bots go on without the camera.";

    // ── Watch mode ──────────────────────────────────────────────────────────

    public static string WatchOn(int seconds, int threshold, bool unprompted) =>
        $"Watching the camera every {N(seconds)} s; a change of {N(threshold)}% or more goes with your next message" + (unprompted ? ", and the model may speak up." : ".") + " /camera watch off stops it.";

    public const string WatchOff = "Stopped watching the camera.";

    public const string WatchNotOn = "The camera is not being watched.";

    public static string BadSeconds(string raw) => $"'{raw}' is not a number of seconds ({N(Settings.AppSettingsData.MinCameraWatchSeconds)}–{N(Settings.AppSettingsData.MaxCameraWatchSeconds)}).";

    /// <summary>The caption added to a message that carries watch mode's frame. Pinned (prompt text).</summary>
    public static string WatchCaption(DateTimeOffset local) =>
        $"(Attached: the user's camera at {local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}; the picture changed since the last one you saw.)";

    /// <summary>The message of an unprompted watch turn. Pinned (prompt text).</summary>
    public const string WatchNudgeMessage = "(The user's camera just changed; the picture is attached. Say something about it only if it is worth saying, briefly — or nothing at all.)";

    public const string WatchNudgeNotice = "📷 The camera changed: showing the model.";

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The notice when the saved camera is not connected and the first one is used instead. Pinned.</summary>
    public static string FellBack(string saved, string used) => $"The camera \"{saved}\" is not connected; using \"{used}\".";

    private static string Tail(string? detail) => string.IsNullOrEmpty(detail) ? "" : $" ({detail})";
}
