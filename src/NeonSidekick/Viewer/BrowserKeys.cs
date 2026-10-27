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

    /// <summary>The history menu opened at its button (Ctrl+H, later on 2026-10-13).</summary>
    History,

    /// <summary>The page saved to the saved links, or taken off them when it is one (Ctrl+D, later still on 2026-10-13).</summary>
    SaveLink,

    /// <summary>The saved links' menu opened at its button, the keyboard on it (Ctrl+Shift+O, Edge's favorites key; later still).</summary>
    Links,

    /// <summary>The window full screen, or out of every full screen when it is (F11; 2026-10-14).</summary>
    ToggleFullScreen,

    /// <summary>One full screen left, the page's first (Esc while full screen and no page loading).</summary>
    LeaveFullScreen,
}

/// <summary>
/// The Browser window's keys (2026-10-13): Ctrl+W closes it, F5 or Ctrl+R reloads, Alt+← goes back and Alt+→ forward, Ctrl+L, Alt+D
/// or F6 goes to the address box, Esc stops a page that is loading (later the same day, with the navigation bar), Ctrl+H opens the
/// history menu (later still), Ctrl+D saves or unsaves the page and Ctrl+Shift+O opens the saved links (later still), and F11 goes
/// full screen and back with Esc leaving it after a load is stopped (2026-10-14, the user's ask; <see cref="BrowserFullScreen"/>,
/// never closing the window, which Esc does in the picture windows); nothing else is taken — unlike the video window, no chord
/// goes to the chat (<c>TerminalHandoff.Take</c>), since a page's form wants Ctrl+C, Ctrl+V and the rest, and a page's own dialog
/// Esc (while full screen, the second Esc). Pure; pinned.
/// </summary>
public static class BrowserKeys
{
    public const int VkW = 0x57;
    public const int VkR = 0x52;
    public const int VkL = 0x4C;
    public const int VkD = 0x44;
    public const int VkH = 0x48;
    public const int VkO = 0x4F;
    public const int VkF5 = 0x74;
    public const int VkF6 = 0x75;
    public const int VkF11 = 0x7A;
    public const int VkLeft = 0x25;
    public const int VkRight = 0x27;
    public const int VkEscape = 0x1B;

    /// <summary>
    /// What <paramref name="virtualKey"/> does with the modifiers held; <paramref name="shift"/> matters to Ctrl+Shift+O alone (Ctrl+D
    /// ignores it), and <paramref name="fullScreen"/> (either full screen) to Esc.
    /// </summary>
    public static BrowserKeyAction ActionFor(int virtualKey, bool control, bool alt, bool loading = false, bool shift = false, bool fullScreen = false) => virtualKey switch
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
        VkEscape when fullScreen && !control && !alt => BrowserKeyAction.LeaveFullScreen,
        VkF11 when !control && !alt => BrowserKeyAction.ToggleFullScreen,
        VkH when control && !alt => BrowserKeyAction.History,
        VkD when control && !alt => BrowserKeyAction.SaveLink,
        VkO when control && shift && !alt => BrowserKeyAction.Links,
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
    /// <summary>
    /// A look's answer (<see cref="HeadlessBrowser.PrintExpression"/>): the ready state, the fingerprint, and since 2026-10-26 the document's
    /// status (0 when it gives none), address and time origin (<see cref="BrowserLook"/>); false while the page has no document to ask.
    /// </summary>
    public static bool TryReadLook(string? json, out BrowserLook look)
    {
        look = default;
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
                || !value.TryGetProperty("x", out var x) || !HeadlessBrowser.TryReadHash(x, out uint hash))
            {
                return false;
            }

            string state = value.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            int status = value.TryGetProperty("st", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetDouble(out double code)
                && code is >= 100 and <= 999 && code == Math.Floor(code) ? (int)code : 0;
            string url = value.TryGetProperty("u", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            look = new BrowserLook(state, (length, hash), status, url, Origin(value));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A DOM answer (<see cref="HeadlessBrowser.PageExpression"/>): the DOM and its document's time origin; false while the page has none to give.</summary>
    public static bool TryReadPage(string? json, out string html, out double document)
    {
        html = "";
        document = 0;
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            using var answer = JsonDocument.Parse(json);
            if (answer.RootElement.ValueKind == JsonValueKind.Object && answer.RootElement.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.String)
            {
                html = h.GetString() ?? "";
                document = Origin(answer.RootElement);
            }

            return html.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A document's time origin (o): a double, as the page writes it; 0 when it gives none.
    private static double Origin(JsonElement value) =>
        value.TryGetProperty("o", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetDouble(out double origin) ? origin : 0;

    /// <summary>A <c>Page.captureScreenshot</c> answer's picture (2026-10-17, the Web UI's live view): its bytes; false for an answer with none.</summary>
    public static bool TryReadScreenshot(string? json, out byte[] picture)
    {
        picture = [];
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.String && data.TryGetBytesFromBase64(out byte[]? bytes) && bytes is { Length: > 0 })
            {
                picture = bytes;
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A <c>Page.getLayoutMetrics</c> answer's visible part (2026-10-17): its <c>cssVisualViewport</c>'s place in the document, size and zoom
    /// (1 when it gives none, as an older runtime's may); false for an answer without one, or one with no size.
    /// </summary>
    public static bool TryReadViewport(string? json, out BrowserViewport viewport)
    {
        viewport = new BrowserViewport(0, 0, 0, 0, 1);
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("cssVisualViewport", out var visual)
                || visual.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            double width = Number(visual, "clientWidth");
            double height = Number(visual, "clientHeight");
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            double zoom = Number(visual, "zoom");
            viewport = new BrowserViewport(Number(visual, "pageX"), Number(visual, "pageY"), width, height, zoom > 0 ? zoom : 1);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) && double.IsFinite(d) ? d : 0;

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
