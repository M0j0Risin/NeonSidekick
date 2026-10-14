using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>How the performance bar draws its meters (<see cref="PerfBarMode"/>). Whether it draws at all is <see cref="PerfBarItems"/>' (2026-09-30).</summary>
public enum PerfBarStyle
{
    /// <summary>Labels and values: <c>CPU  34% · RAM  62% · NET↓ 12.4M</c>, each percent four cells wide, each rate five.</summary>
    Text,

    /// <summary>A bar per meter in half-cell steps, coloured by load.</summary>
    Gauge,

    /// <summary>Ten segments per meter, lit ones along the theme's gradient.</summary>
    Led,
}

/// <summary>What <c>/perfbar</c> saves (<see cref="PerfBarMode.Toggle"/>): the meters shown (empty: the bar hidden; null until 2026-10-02, when null became the defaults), the ones a later show brings back, and the look.</summary>
public sealed record PerfToggle(List<string>? Items, List<string>? LastItems, string Look);

/// <summary>
/// The performance bar's look (2026-09-29, the user's ask: a third bar, under the toolbar — "let's do all of them" for the
/// look): the three words (<c>text</c>, <c>gauge</c>, <c>led</c>) and their mapping to
/// <see cref="PerfBarStyle"/>. A fourth, <c>spark</c> (the last ten readings as a sparkline), went on 2026-10-12 (the user's
/// call), with the sampler's history only it read; a profile that saved it reads as <see cref="Default"/>. Until 2026-09-30 the setting <c>Show performance bar</c> was one of these or <c>off</c>, the
/// switch and the look in one word; since, the meters shown are a checklist (<see cref="PerfBarItems"/>, the user's ask)
/// and the look is <see cref="AppSettingsData.PerformanceBarLook"/>, picked by <c>/perfbar &lt;look&gt;</c> (on the same page's title row too until 2026-10-12, the user's call: those buttons and their
/// glyphs went) and, since 2026-10-14 (the user's ask, the Queue pane's mode button's shape), the page's one <see cref="LookButton"/>, stepping
/// through <see cref="Names"/>. <c>off</c> is a
/// <c>/perfbar</c> word alone (<see cref="Words"/>). <see cref="Parse"/> is the one place the saved look becomes the enum: a
/// hand-edited value that is none of them reads as <see cref="Default"/> without a warning, a display setting as the meters'
/// list is (a <c>Resolve</c> that warned once went later on 2026-09-30, the review's catch: nothing but a test called it).
/// </summary>
public static class PerfBarMode
{
    /// <summary>The look before any was picked: <c>led</c> since 2026-10-02 (the user's ask), <c>text</c> until then. Pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "led";

    /// <summary>The <c>/perfbar</c> word that hides the bar.</summary>
    public const string OffWord = "off";

    /// <summary>The looks in <c>/perfbar</c>'s completion order. Pinned.</summary>
    public static readonly string[] Names = { "text", "gauge", "led" };

    /// <summary><c>/perfbar edit</c> (2026-10-12, the user's ask, as <c>/homebar edit</c>): the <c>Performance bar</c> row's checklist. Pinned.</summary>
    public const string EditWord = "edit";

    /// <summary><c>/perfbar</c>'s words, as its completion lists them: <see cref="OffWord"/>, the looks, then <see cref="EditWord"/>.</summary>
    public static readonly string[] Words = [OffWord, .. Names, EditWord];

    /// <summary>Whether <c>/perfbar</c>'s argument is <see cref="EditWord"/> (any case, trimmed): the checklist, a pane, not a toggle. Pure.</summary>
    public static bool IsEdit(string args) => string.Equals(args.Trim(), EditWord, StringComparison.OrdinalIgnoreCase);

    /// <summary>Trims and ignores case; false (and <see cref="Default"/>'s <see cref="PerfBarStyle.Led"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out PerfBarStyle style)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "text": style = PerfBarStyle.Text; return true;
            case "gauge": style = PerfBarStyle.Gauge; return true;
            case "led": style = PerfBarStyle.Led; return true;
            default: style = PerfBarStyle.Led; return false;   // Default's, so a hand-edited word reads as a fresh profile does
        }
    }

    /// <summary>The look <paramref name="text"/> names, <see cref="Default"/>'s for anything else, with no warning (the pane's tick asks).</summary>
    public static PerfBarStyle Parse(string? text)
    {
        TryParse(text, out var style);
        return style;
    }

    /// <summary>The Performance bar page's look button (2026-10-14, the user's ask, as the Queue pane's mode button): the look in force, <c>look: led</c>; a press steps to the next. Pinned.</summary>
    public static string LookButton(PerfBarStyle style) => "look: " + Name(style);

    /// <summary>The key that is <see cref="LookButton"/>.</summary>
    public const char LookKey = 'l';

    /// <summary>The look after <paramref name="style"/> in <see cref="Names"/>' order (text, gauge, led), the last wrapping to the first. Pure.</summary>
    public static PerfBarStyle Next(PerfBarStyle style) => Parse(Names[(Array.IndexOf(Names, Name(style)) + 1) % Names.Length]);

    /// <summary>The saved word for <paramref name="style"/>.</summary>
    public static string Name(PerfBarStyle style) => style switch
    {
        PerfBarStyle.Gauge => "gauge",
        PerfBarStyle.Led => "led",
        _ => "text",
    };

    /// <summary><c>/perfbar</c>'s completion hint beside a word. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        OffWord => "hide the performance bar",
        "text" => "the meters as numbers",
        "gauge" => "a bar per meter, colored by load",
        "led" => "ten segments per meter along the theme's gradient",
        EditWord => "choose the bar's meters and their order",
        _ => "",
    };

    /// <summary>
    /// What <c>/perfbar</c> saves, pure (later on 2026-09-29, the user's ask; the checklist's since 2026-09-30): bare, the bar
    /// hidden while it shows — its meters kept in <paramref name="last"/> — else shown again with the last ones (or
    /// <see cref="PerfBarItems.Defaults"/> the first time); <c>off</c> hides it; a look sets it and shows the bar the same way;
    /// null for anything else, the usage error.
    /// </summary>
    public static PerfToggle? Toggle(string args, IReadOnlyList<string>? items, IReadOnlyList<string>? last, string? look)
    {
        ArgumentNullException.ThrowIfNull(args);
        var shown = PerfBarItems.Resolve(items);
        string word = args.Trim();
        if (word.Length == 0)
        {
            return shown.Count > 0 ? Hide() : Show(Name(Parse(look)));
        }

        if (string.Equals(word, OffWord, StringComparison.OrdinalIgnoreCase))
        {
            return Hide();
        }

        return TryParse(word, out var picked) ? Show(Name(picked)) : null;

        PerfToggle Hide() => new([], shown.Count > 0 ? PerfBarItems.Save(shown) : last?.ToList(), Name(Parse(look)));

        PerfToggle Show(string name)
        {
            if (shown.Count > 0)
            {
                return new(PerfBarItems.Save(shown), last?.ToList(), name);
            }

            var back = PerfBarItems.Resolve(last);
            return new(PerfBarItems.Save(back.Count > 0 ? back : PerfBarItems.Defaults.ToHashSet(StringComparer.Ordinal)), last?.ToList(), name);
        }
    }
}
