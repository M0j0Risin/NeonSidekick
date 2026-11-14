using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// The Web UI voice rules in one place (2026-11-13, the webui-voice plan). <see cref="Wanted"/> decides what the two voice sessions load
/// (Kokoro, Whisper and Silero for the page, apart from the PC's <see cref="AppSettingsData.TtsOutput"/> and
/// <see cref="AppSettingsData.SttInput"/>); <see cref="Offered"/> decides what the page may use, read per request. Pure.
/// </summary>
internal static class WebVoiceMode
{
    /// <summary>Whether the sessions load the voice models for the page: <see cref="AppSettingsData.WebUi"/> and <see cref="AppSettingsData.WebUiVoice"/> both on.</summary>
    public static bool Wanted(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.WebUi && effective.WebUiVoice;
    }

    /// <summary>Whether the page shows 🎤 and 🔊 and the voice endpoints answer: <see cref="Wanted"/> and the Chat tab on.</summary>
    public static bool Offered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Wanted(effective) && effective.WebUiChatControls;
    }
}

/// <summary>A page a line or a dispatch came from, with its 🔊 as the line carried it (2026-11-13, the webui-voice plan).</summary>
internal sealed record WebPageRef(string Page, bool Speak);
