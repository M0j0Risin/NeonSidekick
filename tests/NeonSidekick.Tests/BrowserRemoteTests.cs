using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The Browser window driven from a Web UI page during a bot check (2026-10-17): the pure half, <see cref="BrowserRemote"/> — what may be
/// sent, the DevTools commands for it, the picture's clip on a scrolled and zoomed page, the JPEG's width — and the two answers it reads
/// (<see cref="BrowserAnswers.TryReadScreenshot"/>, <see cref="BrowserAnswers.TryReadViewport"/>). The window's side is the smoke's
/// (<c>browser:webview2</c>) and <c>WebViewTests</c>'.
/// </summary>
public sealed class BrowserRemoteTests
{
    private static readonly BrowserViewport Plain = new(0, 0, 1000, 800, 1);

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(0.5, 1.01)]
    [InlineData(double.NaN, 0.5)]
    public void Check_RefusesAPlaceOffTheView(double x, double y)
    {
        foreach (var kind in new[] { BrowserInputKind.Tap, BrowserInputKind.Down, BrowserInputKind.Up, BrowserInputKind.Scroll })
        {
            Assert.Equal(BrowserText.RemoteOffView, BrowserRemote.Check(new BrowserInput(kind, x, y)));
        }
    }

    [Fact]
    public void Check_TakesTheEdges_AndRefusesEmptyOrLongText_AndKeysWithNoButton()
    {
        Assert.Null(BrowserRemote.Check(new BrowserInput(BrowserInputKind.Tap, 0, 1)));
        Assert.Null(BrowserRemote.Check(new BrowserInput(BrowserInputKind.Scroll, 1, 0, -300)));
        Assert.Equal(BrowserText.RemoteOffView, BrowserRemote.Check(new BrowserInput(BrowserInputKind.Scroll, 0.5, 0.5, double.PositiveInfinity)));
        Assert.Null(BrowserRemote.Check(new BrowserInput(BrowserInputKind.Text, Text: "Main St")));
        Assert.Equal(BrowserText.RemoteNoText, BrowserRemote.Check(new BrowserInput(BrowserInputKind.Text, Text: "")));
        Assert.Equal(BrowserText.RemoteTooLong(BrowserRemote.MaxText), BrowserRemote.Check(new BrowserInput(BrowserInputKind.Text, Text: new string('a', BrowserRemote.MaxText + 1))));
        Assert.Null(BrowserRemote.Check(new BrowserInput(BrowserInputKind.Text, Text: new string('a', BrowserRemote.MaxText))));
        foreach (string key in BrowserRemote.Keys)
        {
            Assert.Null(BrowserRemote.Check(new BrowserInput(BrowserInputKind.Key, Key: key)));
        }

        Assert.Equal(BrowserText.RemoteNoSuchKey, BrowserRemote.Check(new BrowserInput(BrowserInputKind.Key, Key: "Escape")));
        Assert.Equal(BrowserText.RemoteNoSuchKey, BrowserRemote.Check(new BrowserInput(BrowserInputKind.Key)));
        Assert.Equal(["Enter", "Backspace", "Tab"], BrowserRemote.Keys);
    }

    [Fact]
    public void Commands_ATap_MovesPressesAndReleases_AtTheFractionOfTheView()
    {
        var commands = BrowserRemote.Commands(new BrowserInput(BrowserInputKind.Tap, 0.25, 0.5), Plain);
        Assert.All(commands, c => Assert.Equal("Input.dispatchMouseEvent", c.Method));
        Assert.Equal(["mouseMoved", "mousePressed", "mouseReleased"], commands.Select(c => Read(c.Json).GetProperty("type").GetString()));
        foreach (var (_, json) in commands)
        {
            var e = Read(json);
            Assert.Equal(250, e.GetProperty("x").GetDouble());
            Assert.Equal(400, e.GetProperty("y").GetDouble());
        }

        var pressed = Read(commands[1].Json);
        Assert.Equal("left", pressed.GetProperty("button").GetString());
        Assert.Equal(1, pressed.GetProperty("buttons").GetInt32());
        Assert.Equal(1, pressed.GetProperty("clickCount").GetInt32());
        Assert.Equal(0, Read(commands[2].Json).GetProperty("buttons").GetInt32());
    }

    [Fact]
    public void Commands_AHold_IsAPressThenItsRelease_AndAScrollIsClamped()
    {
        Assert.Equal(["mouseMoved", "mousePressed"], Types(BrowserRemote.Commands(new BrowserInput(BrowserInputKind.Down, 0.1, 0.1), Plain)));
        Assert.Equal(["mouseMoved", "mouseReleased"], Types(BrowserRemote.Commands(new BrowserInput(BrowserInputKind.Up, 0.1, 0.1), Plain)));
        var scroll = BrowserRemote.Commands(new BrowserInput(BrowserInputKind.Scroll, 0.5, 0.5, 5000), Plain);
        Assert.Equal(["mouseMoved", "mouseWheel"], Types(scroll));
        Assert.Equal(BrowserRemote.MaxScroll, Read(scroll[1].Json).GetProperty("deltaY").GetDouble());
        Assert.Equal(-BrowserRemote.MaxScroll, Read(BrowserRemote.Commands(new BrowserInput(BrowserInputKind.Scroll, 0.5, 0.5, -5000), Plain)[1].Json).GetProperty("deltaY").GetDouble());
        var (method, json) = BrowserRemote.Release(12.5, 40);
        Assert.Equal("Input.dispatchMouseEvent", method);
        Assert.Equal("mouseReleased", Read(json).GetProperty("type").GetString());
        Assert.Equal(12.5, Read(json).GetProperty("x").GetDouble());
    }

    [Fact]
    public void Commands_TextIsInserted_AndKeysAreDownThenUp_EnterWithItsText()
    {
        var text = Assert.Single(BrowserRemote.Commands(new BrowserInput(BrowserInputKind.Text, Text: "Ünïcode \"quoted\""), Plain));
        Assert.Equal("Input.insertText", text.Method);
        Assert.Equal("Ünïcode \"quoted\"", Read(text.Json).GetProperty("text").GetString());

        var enter = BrowserRemote.Commands(new BrowserInput(BrowserInputKind.Key, Key: "Enter"), Plain);
        Assert.Equal(["keyDown", "keyUp"], Types(enter));
        Assert.All(enter, c => Assert.Equal("Input.dispatchKeyEvent", c.Method));
        Assert.Equal("\r", Read(enter[0].Json).GetProperty("text").GetString());
        Assert.Equal(13, Read(enter[0].Json).GetProperty("windowsVirtualKeyCode").GetInt32());
        Assert.False(Read(enter[1].Json).TryGetProperty("text", out _));

        var back = BrowserRemote.Commands(new BrowserInput(BrowserInputKind.Key, Key: "Backspace"), Plain);
        Assert.Equal(["rawKeyDown", "keyUp"], Types(back));
        Assert.Equal(8, Read(back[0].Json).GetProperty("windowsVirtualKeyCode").GetInt32());
        Assert.False(Read(back[0].Json).TryGetProperty("text", out _));
        Assert.Equal(9, Read(BrowserRemote.Commands(new BrowserInput(BrowserInputKind.Key, Key: "Tab"), Plain)[0].Json).GetProperty("windowsVirtualKeyCode").GetInt32());
    }

    [Fact]
    public void Screenshot_ClipsWhatTheWindowShows_OnAScrolledZoomedPage()
    {
        // Scrolled to 300 down and zoomed to 125 %: the clip is in device-independent pixels from the document's top.
        var viewport = new BrowserViewport(10, 300, 800, 600, 1.25);
        var parameters = Read(BrowserRemote.ScreenshotParameters(viewport, 2000, 1));
        Assert.Equal("jpeg", parameters.GetProperty("format").GetString());
        Assert.Equal(BrowserRemote.JpegQuality, parameters.GetProperty("quality").GetInt32());
        var clip = parameters.GetProperty("clip");
        Assert.Equal(12.5, clip.GetProperty("x").GetDouble());
        Assert.Equal(375, clip.GetProperty("y").GetDouble());
        Assert.Equal(1000, clip.GetProperty("width").GetDouble());
        Assert.Equal(750, clip.GetProperty("height").GetDouble());
        Assert.Equal(1, clip.GetProperty("scale").GetDouble());   // 1000 px fits in 2000

        // Narrower than the view: scaled down, and a display at 150 % (1.5 pixels a dip, read off the last picture) scales further.
        Assert.Equal(0.5, BrowserRemote.ScaleOf(BrowserRemote.ScreenshotParameters(viewport, 500, 1)));
        Assert.Equal(0.3333, BrowserRemote.ScaleOf(BrowserRemote.ScreenshotParameters(viewport, 500, 1.5)));
        Assert.Equal(1, BrowserRemote.ScaleOf(BrowserRemote.ScreenshotParameters(viewport with { Zoom = 0 }, 800, 1)));   // no zoom read: 1
    }

    [Fact]
    public void PixelsPerDip_IsReadOffThePicture()
    {
        Assert.Equal(1.5, BrowserRemote.PixelsPerDip(750, Plain, 0.5));
        Assert.Equal(1, BrowserRemote.PixelsPerDip(1250, Plain with { Zoom = 1.25 }, 1));
        Assert.Null(BrowserRemote.PixelsPerDip(0, Plain, 1));
        Assert.Null(BrowserRemote.PixelsPerDip(100, Plain with { Width = 0 }, 1));
    }

    [Fact]
    public void JpegWidth_ReadsTheFrameHeader_AndRefusesWhatIsNoJpeg()
    {
        Assert.Equal(1, BrowserRemote.JpegWidth(FakeBrowserWindow.TinyJpeg));
        Assert.Null(BrowserRemote.JpegWidth([]));
        Assert.Null(BrowserRemote.JpegWidth("%PDF-1.7"u8));
        Assert.Null(BrowserRemote.JpegWidth([0xFF, 0xD8, 0xFF, 0xD9]));
        Assert.Null(BrowserRemote.JpegWidth(FakeBrowserWindow.TinyJpeg.AsSpan(0, 20)));
        Assert.Equal(16, BrowserRemote.TagOf(FakeBrowserWindow.TinyJpeg).Length);
        Assert.NotEqual(BrowserRemote.TagOf(FakeBrowserWindow.TinyJpeg), BrowserRemote.TagOf([1, 2, 3]));
    }

    [Fact]
    public void Answers_ReadTheScreenshotAndTheVisiblePart()
    {
        string shot = "{\"data\":\"" + Convert.ToBase64String(FakeBrowserWindow.TinyJpeg) + "\"}";
        Assert.True(BrowserAnswers.TryReadScreenshot(shot, out byte[] picture));
        Assert.Equal(FakeBrowserWindow.TinyJpeg, picture);
        Assert.False(BrowserAnswers.TryReadScreenshot("{\"data\":\"\"}", out _));
        Assert.False(BrowserAnswers.TryReadScreenshot("{\"data\":\"not base64!\"}", out _));
        Assert.False(BrowserAnswers.TryReadScreenshot("oops", out _));
        Assert.False(BrowserAnswers.TryReadScreenshot(null, out _));

        const string metrics = """
            {"layoutViewport":{"pageX":0,"pageY":0,"clientWidth":1069,"clientHeight":721},
             "cssVisualViewport":{"offsetX":0,"offsetY":0,"pageX":3,"pageY":1200,"clientWidth":1069,"clientHeight":721,"scale":1,"zoom":1.25}}
            """;
        Assert.True(BrowserAnswers.TryReadViewport(metrics, out var viewport));
        Assert.Equal(new BrowserViewport(3, 1200, 1069, 721, 1.25), viewport);
        Assert.True(BrowserAnswers.TryReadViewport("""{"cssVisualViewport":{"pageX":0,"pageY":0,"clientWidth":800,"clientHeight":600}}""", out var older));
        Assert.Equal(1, older.Zoom);
        Assert.False(BrowserAnswers.TryReadViewport("""{"cssVisualViewport":{"clientWidth":0,"clientHeight":600}}""", out _));
        Assert.False(BrowserAnswers.TryReadViewport("""{"layoutViewport":{}}""", out _));
        Assert.False(BrowserAnswers.TryReadViewport("[]", out _));
        Assert.False(BrowserAnswers.TryReadViewport("oops", out _));
    }

    private static JsonElement Read(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<string?> Types(IReadOnlyList<(string Method, string Json)> commands) => [.. commands.Select(c => Read(c.Json).GetProperty("type").GetString())];
}
