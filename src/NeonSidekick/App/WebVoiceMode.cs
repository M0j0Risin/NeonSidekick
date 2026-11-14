using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// The Web UI voice rules in one place (2026-11-13, the webui-voice plan). <see cref="Wanted"/> decides what the two voice sessions load
/// (Kokoro, Whisper and Silero for the page, apart from the PC's <see cref="AppSettingsData.TtsOutput"/> and
/// <see cref="AppSettingsData.SttInput"/>); <see cref="Offered"/> decides what the page may use, read per request. Pure.
/// </summary>
internal static class WebVoiceMode
{
    /// <summary>
    /// Whether the sessions load the voice models for the page: <see cref="AppSettingsData.WebUi"/>, <see cref="AppSettingsData.WebUiVoice"/>
    /// and the Chat tab (<see cref="AppSettingsData.WebUiChatControls"/>) all on. The Chat tab since 2026-11-14 (the user's call on the review's
    /// #43): with it off the page has no 🎤 or 🔊, so nothing is loaded for it.
    /// </summary>
    public static bool Wanted(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.WebUi && effective.WebUiVoice && effective.WebUiChatControls;
    }

    /// <summary>Whether the page shows 🎤 and 🔊 and the voice endpoints answer: the same rule as <see cref="Wanted"/> (2026-11-14), kept by its name.</summary>
    public static bool Offered(AppSettingsData effective) => Wanted(effective);
}

/// <summary>A page a line or a dispatch came from, with its 🔊 as the line carried it (2026-11-13, the webui-voice plan).</summary>
internal sealed record WebPageRef(string Page, bool Speak);
