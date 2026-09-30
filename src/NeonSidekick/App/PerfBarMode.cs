using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>How the performance bar draws its meters (<see cref="PerfBarMode"/>); <see cref="Off"/> draws no bar.</summary>
public enum PerfBarStyle
{
    /// <summary>No bar: the sampler stops and the row goes.</summary>
    Off,

    /// <summary>Labels and values: <c>CPU  34% · RAM  62% · GPU  18% · VRAM  81%</c>, each value four cells wide.</summary>
    Text,

    /// <summary>A bar per meter in eighth-block steps, coloured by load.</summary>
    Gauge,

    /// <summary>The last samples per meter as a sparkline, coloured by load.</summary>
    Spark,

    /// <summary>Ten segments per meter, lit ones along the theme's gradient.</summary>
    Led,
}

/// <summary>
/// The setting <c>Show performance bar</c> (2026-09-29, the user's ask: a third bar, under the toolbar, with CPU, RAM, GPU
/// and VRAM — "let's do all of them" for the look): the five words the user picks from (<c>off</c>, <c>text</c>,
/// <c>gauge</c>, <c>spark</c>, <c>led</c>) and their mapping to <see cref="PerfBarStyle"/>, the <see cref="QueueCancelMode"/>
/// shape. <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that is none of them
/// falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class PerfBarMode
{
    /// <summary>Off, the user's call. Pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "off";

    /// <summary>
    /// The look a bare <c>/perf</c> turns the bar on in before any look was ever picked (later on 2026-09-29,
    /// <see cref="AppSettingsData.PerformanceBarLook"/>). Pinned.
    /// </summary>
    public const string DefaultLook = "text";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "off", "text", "gauge", "spark", "led" };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="PerfBarStyle.Off"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out PerfBarStyle style)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "off": style = PerfBarStyle.Off; return true;
            case "text": style = PerfBarStyle.Text; return true;
            case "gauge": style = PerfBarStyle.Gauge; return true;
            case "spark": style = PerfBarStyle.Spark; return true;
            case "led": style = PerfBarStyle.Led; return true;
            default: style = PerfBarStyle.Off; return false;
        }
    }

    /// <summary>The saved word for <paramref name="style"/>.</summary>
    public static string Name(PerfBarStyle style) => style switch
    {
        PerfBarStyle.Text => "text",
        PerfBarStyle.Gauge => "gauge",
        PerfBarStyle.Spark => "spark",
        PerfBarStyle.Led => "led",
        _ => "off",
    };

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "off" => "no performance bar",
        "text" => "CPU, RAM, GPU and VRAM as numbers",
        "gauge" => "a bar per meter, coloured by load",
        "spark" => "the last seconds per meter as a sparkline",
        "led" => "ten segments per meter along the theme's gradient",
        _ => "",
    };

    /// <summary>The look <paramref name="saved"/> names when it is one (not <c>off</c>), else <see cref="DefaultLook"/>.</summary>
    public static string LastLook(string? saved) => TryParse(saved, out var style) && style != PerfBarStyle.Off ? Name(style) : DefaultLook;

    /// <summary>
    /// What <c>/perf</c> saves, pure (later on 2026-09-29, the user's ask): bare, <c>off</c> while a look shows, else the last
    /// look (<see cref="LastLook"/> of <paramref name="last"/>) — a hand-edited <paramref name="shown"/> reads as off; one of
    /// <see cref="Names"/> (trimmed, any case) sets it; null for anything else, the usage error.
    /// </summary>
    public static string? Toggle(string args, string? shown, string? last)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Trim().Length == 0)
        {
            return TryParse(shown, out var style) && style != PerfBarStyle.Off ? Default : LastLook(last);
        }

        return TryParse(args, out var picked) ? Name(picked) : null;
    }

    // The last unknown value warned about: the bar asks at every tick, the log hears once per value.
    private static string? _warned;

    /// <summary>The style in force for <paramref name="effective"/>; an unknown saved value warns once and uses <see cref="Default"/>.</summary>
    public static PerfBarStyle Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.ShowPerformanceBar, out var style))
        {
            return style;
        }

        if (!string.Equals(Interlocked.Exchange(ref _warned, effective.ShowPerformanceBar), effective.ShowPerformanceBar, StringComparison.Ordinal))
        {
            DiagnosticLog.Warn(Category,
                $"{nameof(AppSettingsData.ShowPerformanceBar)}='{effective.ShowPerformanceBar}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return PerfBarStyle.Off;
    }
}
