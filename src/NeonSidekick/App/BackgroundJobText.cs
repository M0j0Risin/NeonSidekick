using NeonSidekick.EmbeddedLlm;

namespace NeonSidekick.App;

/// <summary>
/// The words of the jobs that run behind the input line (2026-09-29, <see cref="BackgroundJobs"/>): their strip glyphs and the
/// notices around them. Pure statics, every string pinned.
/// </summary>
public static class BackgroundJobText
{
    /// <summary>An embedded model downloading.</summary>
    public const string DownloadGlyph = "📥";

    /// <summary>The MCP servers connecting (the glyph <c>/mcp</c> and the toolbar already wear).</summary>
    public const string McpGlyph = Mcp.McpText.Glyph;

    /// <summary>
    /// Voice input setting up: the studio microphone, not the <c>/stt</c> switch's hand-held one (whose click turns it off),
    /// as <see cref="SpeechGlyph"/> is to the <c>/tts</c> switch's speaker. A headset until 2026-10-03 (the user's call:
    /// headphones read as output). U+1F399 with the variation selector, the <c>🏷️</c> shape: the strip's walk keeps the
    /// selector with its glyph, so the double-click's hit is this whole string.
    /// </summary>
    public const string VoiceGlyph = "🎙️";

    /// <summary>Speech output setting up: the quiet speaker, not the <c>/tts</c> switch's loud one.</summary>
    public const string SpeechGlyph = "🔈";

    /// <summary>A job's strip glyph. Pinned.</summary>
    public static string Glyph(BackgroundJobKind kind) => kind switch
    {
        BackgroundJobKind.EmbeddedDownload => DownloadGlyph,
        BackgroundJobKind.Mcp => McpGlyph,
        BackgroundJobKind.Voice => VoiceGlyph,
        _ => SpeechGlyph,
    };

    /// <summary>The notice as an embedded download starts behind the line.</summary>
    public static string DownloadStarted(EmbeddedModel model) =>
        $"{DownloadGlyph} downloading {model.Display} in the background; double-click {DownloadGlyph} on the hint row to pause";

    /// <summary>The same model picked again while it downloads.</summary>
    public static string AlreadyDownloading(EmbeddedModel model) => $"{model.Display} is already downloading ({DownloadGlyph} on the hint row)";

    /// <summary>A download that finished after another server was picked: installed, not switched to.</summary>
    public static string InstalledNotSwitched(EmbeddedModel model) => $"{model.Display} installed; pick it in /server to use it";

    /// <summary>Push-to-talk while voice input still sets up, with the strip's progress when there is one.</summary>
    public static string VoiceSettingUp(string? progress) =>
        "voice input is still setting up (" + VoiceGlyph + (string.IsNullOrEmpty(progress) ? "" : " " + progress) + ")";

    /// <summary>A <c>/mcp</c> action that would wait for the wave to finish.</summary>
    public const string McpStillConnecting = "the MCP servers are still connecting; try again once " + McpGlyph + " leaves the hint row";
}
