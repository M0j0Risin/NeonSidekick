using System.Text.Json;
using NeonSidekick.Web;

namespace NeonSidekick.Viewer;

/// <summary>What a key does in the Browser window (<see cref="BrowserKeys.ActionFor"/>).</summary>
public enum BrowserKeyAction
{
    /// <summary>The page's: typing, Ctrl+C / Ctrl+V into a form, Esc for a page's own dialog.</summary>
    None,
    Close,
    Reload,
    Back,
    Forward,

    /// <summary>The address box focused, all of it selected (Ctrl+L, Alt+D, F6).</summary>
    Address,

    /// <summary>The page's load stopped (Esc while one loads; otherwise Esc is the page's).</summary>
    Stop,
}

/// <summary>
/// The Browser window's keys (2026-10-13): Ctrl+W closes it, F5 or Ctrl+R reloads, Alt+← goes back and Alt+→ forward, Ctrl+L, Alt+D
/// or F6 goes to the address box, Esc stops a page that is loading (later the same day, with the navigation bar); nothing else is
/// taken — unlike the video window, no chord goes to the chat (<c>TerminalHandoff.Take</c>), since a page's form wants Ctrl+C, Ctrl+V
/// and the rest, and a page's own dialog Esc. Pure; pinned.
/// </summary>
public static class BrowserKeys
{
    public const int VkW = 0x57;
    public const int VkR = 0x52;
    public const int VkL = 0x4C;
    public const int VkD = 0x44;
    public const int VkF5 = 0x74;
    public const int VkF6 = 0x75;
    public const int VkLeft = 0x25;
    public const int VkRight = 0x27;
    public const int VkEscape = 0x1B;

    public static BrowserKeyAction ActionFor(int virtualKey, bool control, bool alt, bool loading = false) => virtualKey switch
    {
        VkW when control && !alt => BrowserKeyAction.Close,
        VkF5 when !control && !alt => BrowserKeyAction.Reload,
        VkR when control && !alt => BrowserKeyAction.Reload,
        VkLeft when alt && !control => BrowserKeyAction.Back,
        VkRight when alt && !control => BrowserKeyAction.Forward,
        VkL when control && !alt => BrowserKeyAction.Address,
        VkD when alt && !control => BrowserKeyAction.Address,
        VkF6 when !control && !alt => BrowserKeyAction.Address,
        VkEscape when loading && !control && !alt => BrowserKeyAction.Stop,
        _ => BrowserKeyAction.None,
    };
}

/// <summary>
/// The Browser window's answers from its page (2026-10-13): a look's fingerprint (<see cref="HeadlessBrowser.PrintExpression"/>), the
/// DOM (<see cref="HeadlessBrowser.PageExpression"/>), both as WebView2's ExecuteScript hands them (the value as JSON), and the cookies
/// of DevTools' <c>Network.getAllCookies</c>. Pure; pinned.
/// </summary>
public static class BrowserAnswers
{
    /// <summary>A fingerprint answer: the ready state and the fingerprint; false while the page has no document to ask.</summary>
    public static bool TryReadPrint(string? json, out string state, out (int, uint) print)
    {
        state = "";
        print = default;
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("n", out var n) || !n.TryGetInt32(out int length) || length == 0
                || !value.TryGetProperty("x", out var x) || !x.TryGetUInt32(out uint hash))
            {
                return false;
            }

            state = value.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            print = (length, hash);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A DOM answer: the DOM; false while the page has none to give.</summary>
    public static bool TryReadPage(string? json, out string html)
    {
        html = "";
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.String)
            {
                html = h.GetString() ?? "";
            }

            return html.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The cookies of a <c>Network.getAllCookies</c> answer that would be sent to one of <paramref name="hosts"/>; none for an answer that is not one.</summary>
    public static IReadOnlyList<BrowserCookie> CookiesFor(string? json, IReadOnlyCollection<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return BrowserCookie.Parse(document.RootElement).Where(c => hosts.Any(c.AppliesTo)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
