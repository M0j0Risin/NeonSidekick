using NeonSidekick.Perf;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The setting <c>Show performance bar</c> as a checklist (2026-09-30, the user's ask: the toolbar's shape — pick the
/// meters, none picked is no bar — in place of the one word that was both the switch and the look): one id per meter, in
/// the bar's order, saved in <see cref="Settings.AppSettingsData.PerformanceBarItems"/>. Null there is none (the default:
/// no row, nothing sampled). The network's three (NET%, NET↓, NET↑) came with it, each a choice of its own (the user's
/// pick). The look is <see cref="Settings.AppSettingsData.PerformanceBarLook"/>, picked on the same page's title row.
/// <see cref="Resolve"/> is the one place the saved list becomes the set: a display setting, so an unknown word is dropped
/// without a warning.
/// </summary>
public static class PerfBarItems
{
    public const string Cpu = "cpu";
    public const string Ram = "ram";
    public const string Gpu = "gpu";
    public const string Vram = "vram";
    public const string Net = "net";
    public const string NetDown = "netdown";
    public const string NetUp = "netup";

    /// <summary>Every meter in the bar's order. Pinned.</summary>
    public static readonly string[] Names = [Cpu, Ram, Gpu, Vram, Net, NetDown, NetUp];

    /// <summary>
    /// What a bare <c>/perf</c> (or the toolbar's 📈) shows when nothing was ever picked: the bar as it was before the
    /// checklist, CPU, RAM, GPU and VRAM. Pinned.
    /// </summary>
    public static readonly string[] Restored = [Cpu, Ram, Gpu, Vram];

    /// <summary>The meter's label, the bar's own. Pinned.</summary>
    public static string Title(string id) => id switch
    {
        Cpu => PerfText.CpuLabel,
        Ram => PerfText.RamLabel,
        Gpu => PerfText.GpuLabel,
        Vram => PerfText.VramLabel,
        Net => PerfText.NetLabel,
        NetDown => PerfText.NetDownLabel,
        NetUp => PerfText.NetUpLabel,
        _ => id,
    };

    /// <summary>The checklist's dim note beside a meter. Pinned.</summary>
    public static string Describe(string id) => id switch
    {
        Cpu => "processor load",
        Ram => "memory in use",
        Gpu => "GPU load (NVIDIA, else Windows counters)",
        Vram => "GPU memory in use",
        Net => "network use, % of the link",
        NetDown => "download rate (bits/s)",
        NetUp => "upload rate (bits/s)",
        _ => "",
    };

    /// <summary>The checklist's name column: "VRAM" (4) plus four.</summary>
    public const int TitleWidth = 8;

    /// <summary>One row of the checklist: the mark, the label and <see cref="Describe"/> dimmed. Pinned.</summary>
    public static string Label(string id, bool on) =>
        Markup.Escape((on ? "[x] " : "[ ] ") + Title(id).PadRight(TitleWidth)) + Theme.DimMarkup(Describe(id));

    /// <summary>The meters <paramref name="saved"/> names: none when null, the known ids (trimmed, any case) otherwise.</summary>
    public static IReadOnlySet<string> Resolve(IReadOnlyList<string>? saved)
    {
        if (saved is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var wanted = saved.Select(w => w.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return Names.Where(wanted.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>What saves for <paramref name="on"/>: null for none, else the ids in <see cref="Names"/> order.</summary>
    public static List<string>? Save(IReadOnlySet<string> on)
    {
        ArgumentNullException.ThrowIfNull(on);
        var chosen = Names.Where(on.Contains).ToList();
        return chosen.Count == 0 ? null : chosen;
    }

    /// <summary>
    /// The <c>Show performance bar</c> row's value: <c>off</c> with nothing checked, else the checked meters and the look —
    /// <c>CPU, RAM, NET↓ · gauge</c>, or <c>all · gauge</c> with every one. Pinned.
    /// </summary>
    public static string Value(IReadOnlyList<string>? saved, string? look)
    {
        var on = Resolve(saved);
        if (on.Count == 0)
        {
            return "off";
        }

        string meters = on.Count == Names.Length ? "all" : string.Join(", ", Names.Where(on.Contains).Select(Title));
        return meters + " · " + PerfBarMode.Name(PerfBarMode.Parse(look));
    }
}
