using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NeonSidekick.Viewer;

/// <summary>What a Web UI page sends the Browser window during a bot check (<see cref="BrowserInput"/>).</summary>
public enum BrowserInputKind
{
    /// <summary>A click: pressed and released at once.</summary>
    Tap,

    /// <summary>The button pressed and held (a press-and-hold check), until its <see cref="Up"/>.</summary>
    Down,

    /// <summary>The held button released.</summary>
    Up,

    /// <summary>A wheel's turn at the place, <see cref="BrowserInput.DeltaY"/> CSS pixels (down is positive).</summary>
    Scroll,

    /// <summary>Text put into the focused field as typed.</summary>
    Text,

    /// <summary>One of <see cref="BrowserRemote.Keys"/>, pressed and released.</summary>
    Key,
}

/// <summary>
/// One input from the page's live view (2026-10-17): its kind, the place as fractions of the view (0 to 1 across and down, so the page's
/// zoom and the PC's display scale never matter), a scroll's distance, a text, a key's name.
/// </summary>
public sealed record BrowserInput(BrowserInputKind Kind, double X = 0, double Y = 0, double DeltaY = 0, string? Text = null, string? Key = null);

/// <summary>
/// The page's visible part as DevTools' <c>Page.getLayoutMetrics</c> gives it (<c>cssVisualViewport</c>): where it sits in the document and
/// its size, in CSS pixels, and the page's zoom (CSS to device-independent pixels; Ctrl+wheel changes it).
/// </summary>
public sealed record BrowserViewport(double PageX, double PageY, double Width, double Height, double Zoom);

/// <summary>A picture of the window's page (2026-10-17): the JPEG and its tag (a hash of its bytes, what the page's next read sends back).</summary>
public sealed record BrowserFrame(byte[] Jpeg, string Tag);

/// <summary>
/// The Browser window driven from a Web UI page during a bot check (2026-10-17, the user's ask: a check met while away from home waited at the
/// PC until they were back). A check's pass is a cookie bound to the browser that earned it, so the challenge stays in the window; the page
/// sees it as pictures (<c>Page.captureScreenshot</c>) and its taps, holds, scrolls and typing come back as DevTools input. This is the pure
/// half: what may be sent (<see cref="Check"/>), the protocol's commands for it (<see cref="Commands"/>), the picture's clip
/// (<see cref="ScreenshotParameters"/>) and the JPEG's width (<see cref="JpegWidth"/>). JSON through <see cref="Utf8JsonWriter"/>, whose
/// numbers are invariant. Pinned by tests.
/// </summary>
public static class BrowserRemote
{
    /// <summary>The most text one input puts in a field.</summary>
    public const int MaxText = 500;

    /// <summary>The longest scroll one input asks for, either way, in CSS pixels.</summary>
    public const double MaxScroll = 1200;

    /// <summary>The picture's JPEG quality: enough to read a check's words, small enough for a phone on a mobile network.</summary>
    public const int JpegQuality = 70;

    /// <summary>The keys the page has buttons for (⏎ ⌫ ⇥). Esc has none: on a check it would only close a dialog.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["Enter", "Backspace", "Tab"];

    /// <summary>Why <paramref name="input"/> may not be sent, or null: a place off the view, an empty or overlong text, a key with no button.</summary>
    public static string? Check(BrowserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        switch (input.Kind)
        {
            case BrowserInputKind.Tap or BrowserInputKind.Down or BrowserInputKind.Up or BrowserInputKind.Scroll:
                if (!InView(input.X) || !InView(input.Y))
                {
                    return BrowserText.RemoteOffView;
                }

                return input.Kind == BrowserInputKind.Scroll && !double.IsFinite(input.DeltaY) ? BrowserText.RemoteOffView : null;
            case BrowserInputKind.Text:
                return string.IsNullOrEmpty(input.Text) ? BrowserText.RemoteNoText
                    : input.Text.Length > MaxText ? BrowserText.RemoteTooLong(MaxText)
                    : null;
            case BrowserInputKind.Key:
                return input.Key is { } key && Keys.Contains(key, StringComparer.Ordinal) ? null : BrowserText.RemoteNoSuchKey;
            default:
                return BrowserText.RemoteNoSuchKey;
        }
    }

    /// <summary>
    /// The DevTools commands that carry <paramref name="input"/> (one that passed <see cref="Check"/>) into the page, in order: the mouse's
    /// at the place in CSS pixels from the visible part's top-left (<c>Input.dispatchMouseEvent</c>'s x and y), a text as
    /// <c>Input.insertText</c>, a key as its keyDown and keyUp.
    /// </summary>
    public static IReadOnlyList<(string Method, string Json)> Commands(BrowserInput input, BrowserViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(viewport);
        double x = Math.Round(Math.Clamp(input.X, 0, 1) * viewport.Width, 2);
        double y = Math.Round(Math.Clamp(input.Y, 0, 1) * viewport.Height, 2);
        const string mouse = "Input.dispatchMouseEvent";
        return input.Kind switch
        {
            BrowserInputKind.Tap => [(mouse, Move(x, y)), (mouse, Button("mousePressed", x, y, 1)), (mouse, Button("mouseReleased", x, y, 0))],
            BrowserInputKind.Down => [(mouse, Move(x, y)), (mouse, Button("mousePressed", x, y, 1))],
            BrowserInputKind.Up => [(mouse, Move(x, y)), (mouse, Button("mouseReleased", x, y, 0))],
            BrowserInputKind.Scroll => [(mouse, Move(x, y)), (mouse, Wheel(x, y, Math.Clamp(input.DeltaY, -MaxScroll, MaxScroll)))],
            BrowserInputKind.Text => [("Input.insertText", Write(w => w.WriteString("text", input.Text ?? "")))],
            BrowserInputKind.Key => KeyCommands(input.Key ?? ""),
            _ => [],
        };
    }

    /// <summary>The release of a button held at (<paramref name="x"/>, <paramref name="y"/>) CSS pixels: a hold left behind (the page gone, the check over).</summary>
    public static (string Method, string Json) Release(double x, double y) => ("Input.dispatchMouseEvent", Button("mouseReleased", x, y, 0));

    /// <summary>
    /// <c>Page.captureScreenshot</c>'s parameters for what the window shows: a JPEG of the visible part. The clip is in device-independent pixels
    /// from the document's top-left (the protocol's <c>Viewport</c>), so the scroll and the zoom are both in it: a clip at 0, 0 would be the
    /// document's top, not the window's view. The scale keeps the picture at most <paramref name="maxWidth"/> pixels wide, given how many
    /// pixels a device-independent one came to in the last picture (<paramref name="pixelsPerDip"/>, the PC's display scale; 1 before any).
    /// </summary>
    public static string ScreenshotParameters(BrowserViewport viewport, int maxWidth, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        double zoom = viewport.Zoom > 0 && double.IsFinite(viewport.Zoom) ? viewport.Zoom : 1;
        double width = viewport.Width * zoom;
        double scale = ScaleFor(viewport, maxWidth, pixelsPerDip);
        return Write(w =>
        {
            w.WriteString("format", "jpeg");
            w.WriteNumber("quality", JpegQuality);
            w.WriteStartObject("clip");
            w.WriteNumber("x", Math.Round(viewport.PageX * zoom, 2));
            w.WriteNumber("y", Math.Round(viewport.PageY * zoom, 2));
            w.WriteNumber("width", Math.Round(width, 2));
            w.WriteNumber("height", Math.Round(viewport.Height * zoom, 2));
            w.WriteNumber("scale", scale);
            w.WriteEndObject();
        });
    }

    /// <summary>
    /// How many pixels a device-independent one came to in a picture <paramref name="jpegWidth"/> wide taken of <paramref name="viewport"/> at
    /// <paramref name="scale"/>: what the next picture's scale is worked out from. Null when it can't be told.
    /// </summary>
    public static double? PixelsPerDip(int jpegWidth, BrowserViewport viewport, double scale)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        double zoom = viewport.Zoom > 0 && double.IsFinite(viewport.Zoom) ? viewport.Zoom : 1;
        double dips = viewport.Width * zoom * scale;
        return jpegWidth > 0 && dips > 0 ? jpegWidth / dips : null;
    }

    /// <summary>
    /// The scale <see cref="ScreenshotParameters"/> asks for, as it writes it (rounded to 4 places). Worked out here since 2026-10-19 (the code
    /// review): each picture parsed its own parameters back to learn it.
    /// </summary>
    public static double ScaleFor(BrowserViewport viewport, int maxWidth, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        double zoom = viewport.Zoom > 0 && double.IsFinite(viewport.Zoom) ? viewport.Zoom : 1;
        double width = viewport.Width * zoom;
        double perDip = pixelsPerDip > 0 && double.IsFinite(pixelsPerDip) ? pixelsPerDip : 1;
        return width <= 0 ? 1 : Math.Round(Math.Min(1, maxWidth / (width * perDip)), 4);
    }

    /// <summary>A JPEG's width from its frame header (any SOF marker), or null for bytes that are no JPEG.</summary>
    public static int? JpegWidth(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return null;
        }

        int at = 2;
        while (at + 4 <= jpeg.Length)
        {
            if (jpeg[at] != 0xFF)
            {
                return null;
            }

            byte marker = jpeg[at + 1];
            if (marker == 0xFF)
            {
                at++;   // fill
                continue;
            }

            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            bool frame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (frame)
            {
                return at + 9 <= jpeg.Length ? (jpeg[at + 7] << 8) | jpeg[at + 8] : null;
            }

            if (marker is 0xD9 or 0xDA || length < 2)
            {
                return null;   // the scan or the end before any frame header
            }

            at += 2 + length;
        }

        return null;
    }

    /// <summary>A picture's tag: the first 16 hex digits of its bytes' SHA-256, as the Web UI's other tags are.</summary>
    public static string TagOf(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];

    private static bool InView(double fraction) => double.IsFinite(fraction) && fraction is >= 0 and <= 1;

    private static string Move(double x, double y) => Write(w =>
    {
        w.WriteString("type", "mouseMoved");
        w.WriteNumber("x", x);
        w.WriteNumber("y", y);
        w.WriteString("button", "none");
    });

    private static string Button(string type, double x, double y, int buttons) => Write(w =>
    {
        w.WriteString("type", type);
        w.WriteNumber("x", x);
        w.WriteNumber("y", y);
        w.WriteString("button", "left");
        w.WriteNumber("buttons", buttons);
        w.WriteNumber("clickCount", 1);
    });

    private static string Wheel(double x, double y, double deltaY) => Write(w =>
    {
        w.WriteString("type", "mouseWheel");
        w.WriteNumber("x", x);
        w.WriteNumber("y", y);
        w.WriteNumber("deltaX", 0);
        w.WriteNumber("deltaY", deltaY);
    });

    // Enter carries its text ("\r", the keypress a form submits on); Backspace and Tab are raw key-downs, as Chromium's own are.
    private static IReadOnlyList<(string Method, string Json)> KeyCommands(string key)
    {
        (string code, int vk, string? text) = key switch
        {
            "Enter" => ("Enter", 13, "\r"),
            "Backspace" => ("Backspace", 8, (string?)null),
            _ => ("Tab", 9, null),
        };
        const string method = "Input.dispatchKeyEvent";
        string down = Write(w =>
        {
            w.WriteString("type", text is null ? "rawKeyDown" : "keyDown");
            w.WriteString("key", key);
            w.WriteString("code", code);
            w.WriteNumber("windowsVirtualKeyCode", vk);
            w.WriteNumber("nativeVirtualKeyCode", vk);
            if (text is not null)
            {
                w.WriteString("text", text);
                w.WriteString("unmodifiedText", text);
            }
        });
        string up = Write(w =>
        {
            w.WriteString("type", "keyUp");
            w.WriteString("key", key);
            w.WriteString("code", code);
            w.WriteNumber("windowsVirtualKeyCode", vk);
            w.WriteNumber("nativeVirtualKeyCode", vk);
        });
        return [(method, down), (method, up)];
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
