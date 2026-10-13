using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.Skills;
using NeonSidekick.UI;
using NeonSidekick.Web;

namespace NeonSidekick.Settings;

/// <summary>
/// The setting words retired on 2026-10-13 (the settings verbiage pass, the user's call: <c>off</c> for a feature that is off, no
/// snake_case, no word repeating its row's name), still read. Each picker class keeps its own <c>Retired</c> map (old word → new),
/// which its <c>TryParse</c> reads through <see cref="Current"/>, so a typed <c>/queue mode disabled</c> still works; and
/// <see cref="Upgrade"/> swaps a loaded profile's old words for the new ones, so the menus show (and the next save writes) today's.
/// No migration step: a profile file keeps its old word until something is saved.
/// </summary>
public static class RetiredWords
{
    /// <summary><paramref name="text"/> trimmed and lower-cased, a retired word swapped for its successor; null stays null.</summary>
    public static string? Current(string? text, IReadOnlyDictionary<string, string> retired)
    {
        ArgumentNullException.ThrowIfNull(retired);
        string? word = text?.Trim().ToLowerInvariant();
        return word is not null && retired.TryGetValue(word, out string? now) ? now : word;
    }

    /// <summary>Swaps every retired word in <paramref name="data"/> for its successor, in place (a profile read from disk).</summary>
    public static void Upgrade(AppSettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.WebBrowserNetworkMode = Swap(data.WebBrowserNetworkMode, NetworkMode.Retired);
        data.WebBrowserMode = Swap(data.WebBrowserMode, BrowserMode.Retired);
        data.WebBrowserBotcheckWindow = Swap(data.WebBrowserBotcheckWindow, BotcheckWindow.Retired);
        data.LlmScanMode = Swap(data.LlmScanMode, LlmScanMode.Retired);
        data.LlmToolCompactType = Swap(data.LlmToolCompactType, ToolCompactType.Retired);
        data.FileBrowserMode = Swap(data.FileBrowserMode, FileBrowserMode.Retired);
        data.FileMentionFolderMode = Swap(data.FileMentionFolderMode, MentionFolderMode.Retired);
        data.ReflectionConfirmMode = Swap(data.ReflectionConfirmMode, ReflectionConfirmMode.Retired);
        data.SessionShowName = Swap(data.SessionShowName, SessionShowName.Retired);
        data.MenuMaxHeight = Swap(data.MenuMaxHeight, MenuHeight.Retired);
        data.WelcomeSplashMode = Swap(data.WelcomeSplashMode, SplashMode.Retired);
        data.MemoryMode = Swap(data.MemoryMode, MemoryMode.Retired);
        data.QueueMode = Swap(data.QueueMode, QueueMode.Retired);
        data.BotChatImageMentionMode = Swap(data.BotChatImageMentionMode, BotChatImageMentionMode.Retired);
        data.CameraPreview = Swap(data.CameraPreview, CameraPreviewMode.Retired);
    }

    /// <summary><paramref name="value"/>, or its successor when it is a retired word (case and blanks aside).</summary>
    private static string Swap(string value, IReadOnlyDictionary<string, string> retired) =>
        value is not null && retired.TryGetValue(value.Trim().ToLowerInvariant(), out string? now) ? now : value!;
}
