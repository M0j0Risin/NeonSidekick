using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The one place a link is judged before it reaches the terminal (2026-11-01, clickable links and paths, plan review 5 and D22): an
/// <see cref="IAnsiConsole"/> over the pane's own console that forwards everything and wraps only <see cref="Write"/>. Each written
/// renderable is handed on inside a small wrapper whose segments keep their link only when it is a web one (<c>http</c>, <c>https</c>,
/// <c>mailto</c>) that <see cref="LinkText.IsLinkable"/> passes; any other (a path's <c>neon-path:</c>, a model's <c>file:</c> or
/// <c>javascript:</c>) is dropped, so OSC 8 is only ever written for a web link and a path never reaches the terminal. The renderable is
/// still rendered once, by Spectre's own write. The store keeps the unfiltered segments, so the hit test still reads a path.
/// <see cref="WriteAnsi"/> passes through: nothing in the app writes transcript text that way. Follows <see cref="ConsoleWithInput"/>.
/// </summary>
public sealed class LinkFilter : IAnsiConsole
{
    private readonly IAnsiConsole _inner;

    public LinkFilter(IAnsiConsole inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The console this one writes to.</summary>
    public IAnsiConsole Inner => _inner;

    public Profile Profile => _inner.Profile;

    public IAnsiConsoleCursor Cursor => _inner.Cursor;

    public IAnsiConsoleInput Input => _inner.Input;

    public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;

    public RenderPipeline Pipeline => _inner.Pipeline;

    public void Clear(bool home) => _inner.Clear(home);

    public void Write(IRenderable renderable)
    {
        ArgumentNullException.ThrowIfNull(renderable);
        _inner.Write(new Filtered(renderable));
    }

    public void WriteAnsi(Action<AnsiWriter> write) => _inner.WriteAnsi(write);

    /// <summary>Whether <paramref name="link"/> may reach the terminal: a web link the one rule passes.</summary>
    public static bool Passes(Link? link) => link is not null && LinkText.IsWeb(link.Url) && LinkText.IsLinkable(link.Url, web: true);

    /// <summary><paramref name="segment"/> as the terminal may get it: as it is without a link or with a web one, else rebuilt without its link.</summary>
    public static Segment Filter(Segment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.Link is null || Passes(segment.Link) ? segment : new Segment(segment.Text, segment.Style);
    }

    /// <summary>The written renderable with its segments filtered on the way out.</summary>
    private sealed class Filtered(IRenderable inner) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth) => inner.Measure(options, maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            foreach (var segment in inner.Render(options, maxWidth))
            {
                yield return Filter(segment);
            }
        }
    }
}
