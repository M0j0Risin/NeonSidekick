using NeonSidekick.Perf;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The setting <c>Show performance bar</c> as a checklist (2026-09-30, the user's ask: the toolbar's shape — pick the
/// meters, none picked is no bar — in place of the one word that was both the switch and the look): one id per meter, in
/// the bar's order, saved in <see cref="Settings.AppSettingsData.PerformanceBarItems"/>. Null there is <see cref="Defaults"/>
/// (2026-10-02, the user's ask: the bar shown by default, the toolbar's shape; none until then) and an empty list is none:
/// no row, nothing sampled. The network's three (NET, NET↓, NET↑) came with it, each a choice of its own (the user's
/// pick). PROC (2026-10-05, the user's ask) is the last: the model's background processes still running, the app's own count
/// rather than the machine's, so no reader samples it and every look writes it as a number alone (the user's pick); off by default.
/// The look is <see cref="Settings.AppSettingsData.PerformanceBarLook"/>, picked on the same page's title row.
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
    public const string Proc = "proc";   // 2026-10-05, the user's ask

    /// <summary>Every meter in the bar's order. Pinned.</summary>
    public static readonly string[] Names = [Cpu, Ram, Gpu, Vram, Net, NetDown, NetUp, Proc];

    /// <summary>
    /// The default meters: what a bare <c>/perfbar</c> (or the toolbar's 📈) shows when nothing was ever picked — the bar as it
    /// was before the checklist, CPU, RAM, GPU and VRAM — and, since 2026-10-02 (the user's ask, Show toolbar's shape), what
    /// the checklist's <c>⊡ default</c> button checks. <c>Restored</c> until then. What a profile that never chose shows since
    /// later that day (null). Pinned.
    /// </summary>
    public static readonly string[] Defaults = [Cpu, Ram, Gpu, Vram];

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
        Proc => PerfText.ProcLabel,
        _ => id,
    };

    /// <summary>The checklist's dim note beside a meter (the wording <see cref="PerfText"/>'s). Pinned.</summary>
    public static string Describe(string id) => id switch
    {
        Cpu => PerfText.CpuNote,
        Ram => PerfText.RamNote,
        Gpu => PerfText.GpuNote,
        Vram => PerfText.VramNote,
        Net => PerfText.NetNote,
        NetDown => PerfText.NetDownNote,
        NetUp => PerfText.NetUpNote,
        Proc => PerfText.ProcNote,
        _ => "",
    };

    /// <summary>
    /// The readers <paramref name="on"/>'s meters need (later on 2026-09-30): the sampler runs those alone, and none at all
    /// with nothing checked. GPU and VRAM are one reader, the network's three another; PROC none (the screen counts it).
    /// </summary>
    public static PerfReads Reads(IReadOnlySet<string> on)
    {
        ArgumentNullException.ThrowIfNull(on);
        var reads = PerfReads.None;
        foreach (string id in on)
        {
            reads |= id switch
            {
                Cpu => PerfReads.Cpu,
                Ram => PerfReads.Ram,
                Gpu or Vram => PerfReads.Gpu,
                Net or NetDown or NetUp => PerfReads.Net,
                _ => PerfReads.None,
            };
        }

        return reads;
    }

    /// <summary>The checklist's name column: "VRAM" (4) plus four.</summary>
    public const int TitleWidth = 8;

    /// <summary>One row of the checklist: the mark, the label and <see cref="Describe"/> dimmed. Pinned.</summary>
    public static string Label(string id, bool on) =>
        Markup.Escape((on ? "[x] " : "[ ] ") + Title(id).PadRight(TitleWidth)) + Theme.DimMarkup(Describe(id));

    /// <summary>The meters <paramref name="saved"/> names: <see cref="Defaults"/> when null (2026-10-02), the known ids (trimmed, any case) otherwise.</summary>
    public static IReadOnlySet<string> Resolve(IReadOnlyList<string>? saved)
    {
        if (saved is null)
        {
            return Defaults.ToHashSet(StringComparer.Ordinal);
        }

        // A null in a hand-edited list is skipped (later on 2026-09-30): this runs on the pane's tick, where a throw repeats.
        var wanted = saved.OfType<string>().Select(w => w.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return Names.Where(wanted.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// What saves for <paramref name="on"/>: the ids in <see cref="Names"/> order, an empty list for none — never null, which
    /// is <see cref="Defaults"/> since 2026-10-02 (null was none until then).
    /// </summary>
    public static List<string> Save(IReadOnlySet<string> on)
    {
        ArgumentNullException.ThrowIfNull(on);
        return Names.Where(on.Contains).ToList();
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
            return PerfText.NoMeters;
        }

        string meters = on.Count == Names.Length ? PerfText.AllMeters : string.Join(", ", Names.Where(on.Contains).Select(Title));
        return meters + " · " + PerfBarMode.Name(PerfBarMode.Parse(look));
    }
}
