using NeonSidekick.Screen;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A screen for the tests (2026-10-04): two monitors (the primary 1920x1080 at 0,0, the second 2560x1440 to its right), three
/// windows front to back — the app's terminal first, then Notepad, then a browser — and pictures of the asked size filled with
/// one colour. Records every capture; <see cref="Fail"/> makes the next capture throw its sentence.
/// </summary>
public sealed class FakeScreenSystem : IScreenSystem
{
    public List<ScreenMonitor> MonitorList { get; } =
    [
        new(1, @"\\.\DISPLAY1", new ScreenRect(0, 0, 1920, 1080), true),
        new(2, @"\\.\DISPLAY2", new ScreenRect(1920, 0, 2560, 1440), false),
    ];

    public List<ScreenWindow> WindowList { get; } =
    [
        new(100, "Neon", "WindowsTerminal", new ScreenRect(10, 10, 800, 600)),
        new(200, "notes.txt - Notepad", "Notepad", new ScreenRect(100, 100, 640, 480)),
        new(300, "Error - Contoso Browser", "browser", new ScreenRect(2000, 50, 1200, 900)),
    ];

    public long? Own { get; set; } = 100;

    public int? OwnNumber { get; set; } = 2;

    public string? Fail { get; set; }

    /// <summary>The system's refusal (2026-10-07, a Mac without Screen Recording): null lets captures go ahead.</summary>
    public string? Refused { get; set; }

    /// <summary>Every <see cref="Refusal"/> call's <c>ask</c>, in order.</summary>
    public List<bool> Asked { get; } = [];

    public List<string> Captures { get; } = [];

    public IReadOnlyList<ScreenMonitor> Monitors() => MonitorList;

    public IReadOnlyList<ScreenWindow> Windows() => WindowList;

    public long? OwnWindow() => Own;

    public int? OwnMonitor() => OwnNumber;

    public string? Refusal(bool ask)
    {
        Asked.Add(ask);
        return Refused;
    }

    public ScreenFrame CaptureArea(ScreenRect area)
    {
        Captures.Add($"area {area.Left},{area.Top} {area.Width}x{area.Height}");
        return Picture(area.Width, area.Height);
    }

    public ScreenFrame CaptureWindow(long id)
    {
        Captures.Add("window " + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var window = WindowList.FirstOrDefault(w => w.Id == id) ?? throw new ScreenException(ScreenText.WindowGone);
        return Picture(window.Bounds.Width, window.Bounds.Height);
    }

    private ScreenFrame Picture(int width, int height)
    {
        if (Fail is { } fail)
        {
            Fail = null;
            throw new ScreenException(fail);
        }

        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;
            pixels[i + 1] = 0x80;
            pixels[i + 2] = 0xC0;
        }

        return new ScreenFrame(width, height, pixels);
    }
}
