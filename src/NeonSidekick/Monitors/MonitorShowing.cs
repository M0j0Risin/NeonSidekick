namespace NeonSidekick.Monitors;

/// <summary>Whether a monitor shows this PC: yes, another input, or can't tell (off the desktop, duplicated, its input unread).</summary>
public enum MonitorShowingKind
{
    ThisPc,
    Elsewhere,
    Unknown,
}

/// <summary>What a judgment rests on: the user's pick of this PC's input, the app's own switch away, or the cable's kind against the input's.</summary>
public enum MonitorShowingBasis
{
    Picked,
    Switched,
    Cable,
}

/// <summary>
/// Whether a monitor shows this PC (later on 2026-10-23, the user's report: the XG27UQDMS switched to HDMI-1 still read "extended" with
/// nothing to say it showed another machine — Windows keeps a DisplayPort monitor on another input on the desktop, rightly, since its hot-plug
/// stays up). <see cref="Judge"/> is the one rule every front end reads (<c>/monitor list</c>, <c>monitor_list</c>, the pane's tabs and
/// captions, <see cref="MonitorActions.WarningAsync"/>'s questions, <c>--monitor-check</c>): the user's pick first (<c>Monitor PC inputs</c>),
/// then the app's own switch away (<see cref="MonitorAway"/>), then the cable's kind against the input's — a DisplayPort (or USB-C) cable
/// showing HDMI-1/2, or an HDMI cable showing DisplayPort-1/2, is another input; anything else is no guess. Unknown counts as showing this
/// PC (<see cref="Counts"/>), today's assumption, so a guess only ever adds a question and never takes one away. An input read of 0x00 is
/// unread: some LG monitors answer a VCP read they don't support with no error and all-zero bytes (ddcutil issue #100), and no input has 0x00.
/// <see cref="Current"/> is the input as read (null when unread or 0x00), <see cref="PcInput"/> the pick, <see cref="Left"/> the app's mark. Pure.
/// </summary>
public sealed record MonitorShowing(MonitorShowingKind Kind, MonitorShowingBasis? Basis, byte? Current, byte? PcInput, byte? Left)
{
    /// <summary>Nothing known.</summary>
    public static readonly MonitorShowing Unknown = new(MonitorShowingKind.Unknown, null, null, null, null);

    /// <summary>Whether it counts as showing this PC: yes, or can't tell (today's assumption for a monitor on the desktop).</summary>
    public bool Counts => Kind != MonitorShowingKind.Elsewhere;

    /// <summary>Whether it shows another input.</summary>
    public bool IsElsewhere => Kind == MonitorShowingKind.Elsewhere;

    /// <summary>An input read as the judgment takes it: null for unread and for 0x00 (no input has it).</summary>
    public static byte? Usable(byte? current) => current is { } code && code != 0 ? code : null;

    /// <summary>
    /// <paramref name="monitor"/>'s judgment from its input as read (<paramref name="current"/>), the input the app switched it away from
    /// (<paramref name="awayMark"/>) and the user's pick (<paramref name="picked"/>); first match wins: off the desktop or duplicated → Unknown;
    /// a pick and a read → ThisPc or Elsewhere by it; a mark → Elsewhere, read or not (a failed read keeps it, as it always has); no read →
    /// Unknown; else the cable.
    /// </summary>
    public static MonitorShowing Judge(MonitorInfo monitor, byte? current, byte? awayMark, byte? picked)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (!monitor.Active || monitor.Shared)
        {
            return Unknown with { PcInput = picked };
        }

        byte? now = Usable(current);
        if (picked is { } pc && now is { } read)
        {
            return new(read == pc ? MonitorShowingKind.ThisPc : MonitorShowingKind.Elsewhere, MonitorShowingBasis.Picked, read, pc, awayMark);
        }

        if (awayMark is { } left)
        {
            return new(MonitorShowingKind.Elsewhere, MonitorShowingBasis.Switched, now, picked, left);
        }

        if (now is not { } code)
        {
            return Unknown with { PcInput = picked };
        }

        return CableMatches(monitor.Connector, code) switch
        {
            true => new(MonitorShowingKind.ThisPc, MonitorShowingBasis.Cable, code, picked, null),
            false => new(MonitorShowingKind.Elsewhere, MonitorShowingBasis.Cable, code, picked, null),
            _ => Unknown with { Current = code, PcInput = picked },
        };
    }

    /// <summary>
    /// Whether an input of <paramref name="code"/> can be what a <paramref name="connector"/> cable carries: true for the same kind, false
    /// for the other, null when either is no kind (another connector, a vendor code, VGA, DVI). Pure.
    /// </summary>
    public static bool? CableMatches(string connector, byte code) =>
        CableKind(connector) is { } cable && InputKind(code) is { } input ? cable == input : null;

    /// <summary>Whether the cable's kind is one the guess knows (DisplayPort, USB-C, HDMI).</summary>
    public static bool CableKnown(string connector) => CableKind(connector) is not null;

    /// <summary>
    /// The one input the pick list and <c>/monitor &lt;n&gt; pc</c> call "guessed": the current input when it is of the cable's kind and the
    /// monitor is on the desktop, else the only offered input of that kind; null when there are two (the LG's DisplayPort-1 and -2), none, or
    /// the cable is no kind. Off the desktop the current input is another machine's (2026-10-27, the review), so it never counts. Pure.
    /// </summary>
    public static byte? CableGuess(MonitorInfo monitor, byte? current, IReadOnlyList<byte> offered)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(offered);
        if (CableKind(monitor.Connector) is not { } cable)
        {
            return null;
        }

        if (monitor.Active && Usable(current) is { } now && InputKind(now) == cable)
        {
            return now;
        }

        var same = offered.Distinct().Where(c => InputKind(c) == cable).ToList();
        return same.Count == 1 ? same[0] : null;
    }

    // The two kinds the guess knows: DisplayPort (a USB-C connection carries DisplayPort) and HDMI.
    private static char? CableKind(string connector) => connector switch
    {
        MonitorText.DisplayPortConnector or MonitorText.UsbCConnector => 'D',
        MonitorText.HdmiConnector => 'H',
        _ => null,
    };

    private static char? InputKind(byte code) => code switch
    {
        0x0F or 0x10 => 'D',
        0x11 or 0x12 => 'H',
        _ => null,
    };
}
