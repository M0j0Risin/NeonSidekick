using System.Globalization;

namespace NeonSidekick.Screen;

/// <summary>What a <c>target</c> asks for (<see cref="ScreenTarget.Parse"/>).</summary>
public enum ScreenTargetKind
{
    /// <summary>The monitor the app's window is on (the default).</summary>
    Screen,

    /// <summary>Every monitor: the whole virtual desktop.</summary>
    All,

    /// <summary>One monitor by its number.</summary>
    Monitor,

    /// <summary>One window, by its id or words of its title.</summary>
    Window,

    /// <summary>The window right behind the app's own: the one the user was just looking at.</summary>
    Behind,
}

/// <summary>A target as parsed: its kind and its argument (the monitor's number, the window's id or title words).</summary>
public sealed record ScreenTarget(ScreenTargetKind Kind, string Argument = "", int Monitor = 0)
{
    /// <summary>
    /// <c>screen</c> (or nothing), <c>all</c>, <c>monitor:N</c>, <c>window:&lt;id or title words&gt;</c>, <c>behind</c>, case and the
    /// spaces around the colon ignored; <c>monitor N</c> and <c>window …</c> with a space too, the lenient way models write. Null
    /// with <paramref name="error"/> for anything else. Pure.
    /// </summary>
    public static ScreenTarget? Parse(string? text, out string? error)
    {
        error = null;
        string raw = (text ?? "").Trim();
        if (raw.Length == 0)
        {
            return new ScreenTarget(ScreenTargetKind.Screen);
        }

        // The keyword ends at whichever comes first, a colon or a space (the 2026-10-04 review: the first colon anywhere split
        // "window Untitled: Notepad" inside the title); a colon after the space ("monitor : 2") still belongs to the keyword.
        int colon = raw.IndexOf(':'), space = raw.IndexOf(' ');
        int split = colon < 0 ? space : space < 0 ? colon : Math.Min(colon, space);
        string word = (split < 0 ? raw : raw[..split]).Trim().ToLowerInvariant();
        string rest = split < 0 ? "" : raw[(split + 1)..].Trim();
        if (split == space && rest.StartsWith(':'))
        {
            rest = rest[1..].Trim();
        }
        switch (word)
        {
            case "screen" or "monitor" when rest.Length == 0:
                return new ScreenTarget(ScreenTargetKind.Screen);
            case "all" or "desktop" when rest.Length == 0:
                return new ScreenTarget(ScreenTargetKind.All);
            case "behind" when rest.Length == 0:
                return new ScreenTarget(ScreenTargetKind.Behind);
            case "monitor" or "screen":
                if (int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0)
                {
                    return new ScreenTarget(ScreenTargetKind.Monitor, rest, number);
                }

                error = ScreenText.BadMonitor(rest);
                return null;
            case "window" when rest.Length > 0:
                return new ScreenTarget(ScreenTargetKind.Window, rest);
            default:
                error = ScreenText.BadTarget(raw);
                return null;
        }
    }
}

/// <summary>What a target came to on this screen: an area or a window, and the words that say which (the pane, the result line).</summary>
public sealed record ScreenAim(ScreenRect? Area, long? Window, string Described);

/// <summary>A target against the screen as it is now (<see cref="Resolve"/>). Pure over <see cref="IScreenSystem"/>'s answers.</summary>
public static class ScreenAiming
{
    /// <summary>The most windows an ambiguous title lists back.</summary>
    public const int MaxCandidates = 8;

    /// <summary>
    /// The area or window <paramref name="target"/> means. A monitor number past the last, a title no window has, or words more
    /// than one window's title holds (and none holds exactly) throw <see cref="ScreenException"/> with the sentence, the
    /// candidates listed for an ambiguous one.
    /// </summary>
    public static ScreenAim Resolve(ScreenTarget target, IScreenSystem screen)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(screen);
        switch (target.Kind)
        {
            case ScreenTargetKind.All:
            {
                var monitors = screen.Monitors();
                var area = ScreenRect.Union(monitors.Select(m => m.Bounds));
                return new ScreenAim(area, null, ScreenText.AllDescribed(monitors.Count, area));
            }

            case ScreenTargetKind.Monitor:
            {
                var monitors = screen.Monitors();
                var monitor = monitors.FirstOrDefault(m => m.Number == target.Monitor)
                    ?? throw new ScreenException(ScreenText.NoSuchMonitor(target.Monitor, monitors.Count));
                return new ScreenAim(monitor.Bounds, null, ScreenText.MonitorDescribed(monitor, own: false));
            }

            case ScreenTargetKind.Window:
            {
                var window = FindWindow(target.Argument, screen.Windows());
                return new ScreenAim(null, window.Id, ScreenText.WindowDescribed(window));
            }

            case ScreenTargetKind.Behind:
            {
                var windows = screen.Windows();
                long? own = screen.OwnWindow();
                int at = own is { } id ? windows.ToList().FindIndex(w => w.Id == id) : -1;
                if (at < 0 || at + 1 >= windows.Count)
                {
                    throw new ScreenException(ScreenText.NothingBehind);
                }

                var behind = windows[at + 1];
                return new ScreenAim(null, behind.Id, ScreenText.WindowDescribed(behind));
            }

            default:
            {
                var monitors = screen.Monitors();
                int? ownNumber = screen.OwnMonitor();
                var monitor = monitors.FirstOrDefault(m => m.Number == ownNumber) ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault()
                    ?? throw new ScreenException(ScreenText.NoMonitors);
                return new ScreenAim(monitor.Bounds, null, ScreenText.MonitorDescribed(monitor, own: monitor.Number == ownNumber));
            }
        }
    }

    /// <summary>
    /// The window <paramref name="what"/> names: its id; else the one window whose title (then process name) holds the words,
    /// case ignored, an exact title winning over partial ones. Throws <see cref="ScreenException"/> for none or too many. Pure.
    /// </summary>
    public static ScreenWindow FindWindow(string what, IReadOnlyList<ScreenWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        string words = (what ?? "").Trim().Trim('"');
        if (long.TryParse(words, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && windows.FirstOrDefault(w => w.Id == id) is { } byId)
        {
            return byId;
        }

        var exact = windows.Where(w => string.Equals(w.Title, words, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var titled = windows.Where(w => w.Title.Contains(words, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = titled.Count > 0 ? titled : windows.Where(w => w.Process.Contains(words, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ScreenException(ScreenText.NoSuchWindow(words)),
            _ => throw new ScreenException(ScreenText.AmbiguousWindow(words, matches.Take(MaxCandidates).ToList(), matches.Count)),
        };
    }
}
