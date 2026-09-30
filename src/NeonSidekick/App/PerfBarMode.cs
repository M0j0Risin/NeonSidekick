using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>How the performance bar draws its meters (<see cref="PerfBarMode"/>). Whether it draws at all is <see cref="PerfBarItems"/>' (2026-09-30).</summary>
public enum PerfBarStyle
{
    /// <summary>Labels and values: <c>CPU  34% · RAM  62% · NET↓ 12.4M</c>, each percent four cells wide, each rate five.</summary>
    Text,

    /// <summary>A bar per meter in half-cell steps, coloured by load.</summary>
    Gauge,

    /// <summary>The last samples per meter as a sparkline, coloured by load.</summary>
    Spark,

    /// <summary>Ten segments per meter, lit ones along the theme's gradient.</summary>
    Led,
}

/// <summary>What <c>/perf</c> saves (<see cref="PerfBarMode.Toggle"/>): the meters shown (null: the bar hidden), the ones a later show brings back, and the look.</summary>
public sealed record PerfToggle(List<string>? Items, List<string>? LastItems, string Look);

/// <summary>
/// The performance bar's look (2026-09-29, the user's ask: a third bar, under the toolbar — "let's do all of them" for the
/// look): the four words (<c>text</c>, <c>gauge</c>, <c>spark</c>, <c>led</c>) and their mapping to
/// <see cref="PerfBarStyle"/>. Until 2026-09-30 the setting <c>Show performance bar</c> was one of these or <c>off</c>, the
/// switch and the look in one word; since, the meters shown are a checklist (<see cref="PerfBarItems"/>, the user's ask)
/// and the look is <see cref="AppSettingsData.PerformanceBarLook"/>, picked on the same page's title row. <c>off</c> is a
/// <c>/perf</c> word alone (<see cref="Words"/>). <see cref="Parse"/> is the one place the saved look becomes the enum: a
/// hand-edited value that is none of them reads as <see cref="Default"/> without a warning, a display setting as the meters'
/// list is (a <c>Resolve</c> that warned once went later on 2026-09-30, the review's catch: nothing but a test called it).
/// </summary>
public static class PerfBarMode
{
    /// <summary>The look before any was picked. Pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "text";

    /// <summary>The <c>/perf</c> word that hides the bar.</summary>
    public const string OffWord = "off";

    /// <summary>The looks in the page's button order. Pinned.</summary>
    public static readonly string[] Names = { "text", "gauge", "spark", "led" };

    /// <summary><c>/perf</c>'s words, as its completion lists them: <see cref="OffWord"/>, then the looks.</summary>
    public static readonly string[] Words = [OffWord, .. Names];

    /// <summary>Trims and ignores case; false (and <see cref="PerfBarStyle.Text"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out PerfBarStyle style)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "text": style = PerfBarStyle.Text; return true;
            case "gauge": style = PerfBarStyle.Gauge; return true;
            case "spark": style = PerfBarStyle.Spark; return true;
            case "led": style = PerfBarStyle.Led; return true;
            default: style = PerfBarStyle.Text; return false;
        }
    }

    /// <summary>The look <paramref name="text"/> names, <see cref="PerfBarStyle.Text"/> for anything else, with no warning (the pane's tick asks).</summary>
    public static PerfBarStyle Parse(string? text)
    {
        TryParse(text, out var style);
        return style;
    }

    /// <summary>The saved word for <paramref name="style"/>.</summary>
    public static string Name(PerfBarStyle style) => style switch
    {
        PerfBarStyle.Gauge => "gauge",
        PerfBarStyle.Spark => "spark",
        PerfBarStyle.Led => "led",
        _ => "text",
    };

    /// <summary><c>/perf</c>'s completion hint beside a word. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        OffWord => "hide the performance bar",
        "text" => "the meters as numbers",
        "gauge" => "a bar per meter, coloured by load",
        "spark" => "the last seconds per meter as a sparkline",
        "led" => "ten segments per meter along the theme's gradient",
        _ => "",
    };

    /// <summary>
    /// What <c>/perf</c> saves, pure (later on 2026-09-29, the user's ask; the checklist's since 2026-09-30): bare, the bar
    /// hidden while it shows — its meters kept in <paramref name="last"/> — else shown again with the last ones (or
    /// <see cref="PerfBarItems.Restored"/> the first time); <c>off</c> hides it; a look sets it and shows the bar the same way;
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

        PerfToggle Hide() => new(null, shown.Count > 0 ? PerfBarItems.Save(shown) : last?.ToList(), Name(Parse(look)));

        PerfToggle Show(string name)
        {
            if (shown.Count > 0)
            {
                return new(PerfBarItems.Save(shown), last?.ToList(), name);
            }

            var back = PerfBarItems.Resolve(last);
            return new(PerfBarItems.Save(back.Count > 0 ? back : PerfBarItems.Restored.ToHashSet(StringComparer.Ordinal)), last?.ToList(), name);
        }
    }
}
