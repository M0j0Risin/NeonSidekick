using NeonSidekick.Diagnostics;

namespace NeonSidekick.Monitors;

/// <summary>What a change did: switched, left the desktop, a new layout, nothing to do, refused (its sentence), or the user said no.</summary>
public enum MonitorOutcomeKind
{
    Switched,
    LeftDesktop,
    Ignored,
    Arranged,
    Unchanged,
    Refused,
    Declined,
}

/// <summary>A change's outcome: its kind, the sentence the user or the model reads, and the topology read after it (null when none was).</summary>
public sealed record MonitorOutcome(MonitorOutcomeKind Kind, string Text, MonitorSnapshot? After = null)
{
    /// <summary>Whether something changed (a switch, a layout, a monitor gone off the desktop).</summary>
    public bool Changed => Kind is MonitorOutcomeKind.Switched or MonitorOutcomeKind.LeftDesktop or MonitorOutcomeKind.Arranged;

    /// <summary>Whether it failed (a refusal).</summary>
    public bool Failed => Kind == MonitorOutcomeKind.Refused;

    public static MonitorOutcome Refused(string text) => new(MonitorOutcomeKind.Refused, text);
}

/// <summary>
/// One change: an input switch (<see cref="Input"/> on <see cref="Monitor"/>) or a layout (<see cref="Layout"/>, with the monitor for Only and
/// Primary). <see cref="Unlisted"/> marks a code the monitor neither lists nor is common (its outcome says so). <see cref="Return"/> (2026-10-27,
/// the NVIDIA plan) marks a switch of a monitor off the desktop to this PC's input: the switch, then Extend all brings it back.
/// <see cref="ExtendsAll"/> (2026-10-27, the review; the user's call: keep Extend all, say so) marks one whose Extend all does more than add
/// it: another monitor off the desktop comes back too, or a Duplicate ends. Every switch is built by <see cref="MonitorState.SwitchTo"/>.
/// </summary>
public sealed record MonitorChange(MonitorInfo? Monitor, byte? Input, MonitorLayout? Layout, bool Unlisted = false, bool Return = false, bool ExtendsAll = false)
{
    public static MonitorChange SwitchInput(MonitorInfo monitor, byte code, bool unlisted = false) => new(monitor, code, null, unlisted);

    public static MonitorChange Arrange(MonitorLayout kind, MonitorInfo? monitor = null) => new(monitor, null, kind);

    /// <summary>The change in words, for a question and the audit line: <c>switch 2 XG27UQDMS to HDMI-1</c>, <c>Extend all</c>, <c>Only 1 LG ULTRAWIDE</c>.</summary>
    public string Describe(MonitorWords words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (Input is { } code && Monitor is { } m)
        {
            return "switch " + m.Label + " to " + words.Describe(m, code) + (!Return ? "" : ExtendsAll ? MonitorText.AndExtendAllWords : MonitorText.AndAddWords);
        }

        string label = Monitor?.Label ?? "";
        return Layout switch
        {
            MonitorLayout.Extend => MonitorText.ExtendRow,
            MonitorLayout.Duplicate => MonitorText.DuplicateRow,
            MonitorLayout.Only => MonitorText.OnlyRow(label),
            _ => MonitorText.PrimaryRow(label),
        };
    }
}

/// <summary>
/// The one path every monitor change takes (2026-10-23, the monitors plan) — the pane, <c>/monitor</c> (the screen's and headless's) and the
/// tools: the topology read afresh, the change checked against it, applied through <see cref="MonitorCalls"/> (cap and retry), a settle of
/// <see cref="Settle"/>, the topology and the input read again, and a <see cref="MonitorOutcome"/>. <see cref="WarningAsync"/> is the question a
/// change asks first — the last screen showing this PC switched away, or a layout around a monitor on another input — after every active
/// monitor's input is read again and judged (<see cref="MonitorShowing"/>: the user's pick, the app's own marks in <see cref="MonitorAway"/>,
/// the cable's kind; later on 2026-10-23), so one switched back by its own buttons asks nothing; the caller asks it (headless never does). Every read is heard by <see cref="TopologyRead"/> and <see cref="InputRead"/>, the board's feed.
/// Failures come back as <see cref="MonitorOutcomeKind.Refused"/>, never a throw (a cancel is still one).
/// </summary>
public sealed class MonitorActions
{
    /// <summary>The wait after a change before reading again: a monitor needs a moment to change inputs and Windows to notice a hotplug.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time;
    private readonly TimeSpan _settle;

    /// <param name="settle">The wait after a change; null for <see cref="Settle"/> (the tests pass zero).</param>
    public MonitorActions(MonitorCalls calls, MonitorAway? away = null, TimeProvider? time = null, TimeSpan? settle = null)
    {
        Calls = calls ?? throw new ArgumentNullException(nameof(calls));
        Away = away ?? new MonitorAway();
        _time = time ?? TimeProvider.System;
        _settle = settle ?? Settle;
    }

    public MonitorCalls Calls { get; }

    public MonitorAway Away { get; }

    /// <summary>Heard with every topology read.</summary>
    public event Action<MonitorSnapshot>? TopologyRead;

    /// <summary>Heard with every input read: the target and its input.</summary>
    public event Action<string, byte>? InputRead;

    /// <summary>The topology now (the marks of monitors gone off the desktop dropped).</summary>
    public async Task<MonitorSnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        var snapshot = await Calls.RunAsync("query", s => s.Query(), retry: false, cancellationToken).ConfigureAwait(false);
        Away.Prune(snapshot);
        TopologyRead?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// <paramref name="monitor"/>'s input now (a mark cleared when it shows the input it left again); <paramref name="retry"/> false for one
    /// try only (the questions' reads, later on 2026-10-23: a monitor without DDC/CI costs one fast failure, not the retry's gap).
    /// </summary>
    public async Task<byte> ReadInputAsync(MonitorInfo monitor, CancellationToken cancellationToken, bool retry = true)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        byte current = await Calls.RunAsync("read input of " + monitor.Label, s => s.CurrentInput(monitor.Target), retry, cancellationToken).ConfigureAwait(false);
        Away.Seen(monitor.Key, current);
        InputRead?.Invoke(monitor.Target, current);
        return current;
    }

    /// <summary>
    /// The topology and every monitor's input and capabilities read once, with no board (headless's <c>/monitor list</c>): a duplicated monitor
    /// gets its sentence, one whose read fails its problem (one off the desktop that NVIDIA's driver doesn't reach, <see cref="MonitorText.OffDesktop"/>),
    /// a capabilities read that fails the common four.
    /// </summary>
    public async Task<MonitorState> ReadAllAsync(CancellationToken cancellationToken) =>
        await ReadAllAsync(await QueryAsync(cancellationToken).ConfigureAwait(false), _ => true, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// <see cref="ReadAllAsync(CancellationToken)"/> over a topology already read, for the monitors <paramref name="which"/> takes; the rest stay
    /// <see cref="MonitorInputs.Unread"/> (2026-10-27, the review: headless's <c>/monitor 1 input …</c> read every monitor's list, seconds each).
    /// </summary>
    public async Task<MonitorState> ReadAllAsync(MonitorSnapshot snapshot, Func<MonitorInfo, bool> which, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(which);
        var inputs = new Dictionary<string, MonitorInputs>(StringComparer.Ordinal);
        foreach (var m in snapshot.Monitors.Where(which))
        {
            if (m.Shared)
            {
                inputs[m.Target] = MonitorInputs.Unread with { Problem = MonitorText.SharedSource };
                continue;
            }

            try
            {
                byte current = await ReadInputAsync(m, cancellationToken).ConfigureAwait(false);
                MonitorCaps caps;
                try
                {
                    caps = await Calls.RunAsync("capabilities of " + m.Label, s => s.Capabilities(m.Target), retry: true, cancellationToken).ConfigureAwait(false);
                }
                catch (MonitorException)
                {
                    caps = new MonitorCaps(Mccs.CommonInputs, false, "");
                }

                inputs[m.Target] = new MonitorInputs(current, caps, null, false, false);
            }
            catch (MonitorException e)
            {
                inputs[m.Target] = MonitorInputs.Unread with { Problem = e.Message };
            }
        }

        return new MonitorState(snapshot, inputs, Away.Marks, 0);
    }

    /// <summary>
    /// Whether <paramref name="change"/> would change anything in <paramref name="snapshot"/> (a row that wouldn't is left out): Extend when
    /// every connected monitor is already extended, Duplicate with fewer than two connected or all already showing one source, Only the
    /// one monitor already alone, Primary the primary, one off the desktop, or while duplicated. An input switch always may. Pure.
    /// </summary>
    public static bool WouldChange(MonitorChange change, MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (change.Input is not null)
        {
            return true;
        }

        var all = snapshot.Monitors;
        var m = change.Monitor is { } chosen ? snapshot.ByTarget(chosen.Target) : null;
        return change.Layout switch
        {
            MonitorLayout.Extend => !(all.All(x => x.Active) && snapshot.Layout != MonitorArrangement.Duplicated),
            MonitorLayout.Duplicate => all.Count >= 2 && !(all.All(x => x.Active && x.Shared) && snapshot.Layout == MonitorArrangement.Duplicated),
            MonitorLayout.Only => m is not null && !(m.Active && snapshot.Active.Count == 1),
            MonitorLayout.Primary => m is { Active: true, Primary: false } && snapshot.Layout != MonitorArrangement.Duplicated,
            _ => false,
        };
    }

    /// <summary>
    /// The question <paramref name="change"/> asks before it runs, or null: switching away the last monitor still showing this PC
    /// (<see cref="MonitorText.LastScreenQuestion"/>), Only or Make primary on a monitor on another input, Duplicate while one is
    /// (<see cref="MonitorText.AwayLayoutQuestion"/>). Since later on 2026-10-23 every active monitor's input is read first, once and with no
    /// retry, and each is judged by <see cref="MonitorShowing"/> (the pick in <paramref name="words"/>, the read, the app's mark): a monitor
    /// whose read fails can't be told, which counts as showing this PC unless the app's mark says otherwise (the count can only run high).
    /// A failure to read the topology asks nothing (the change itself then says why).
    /// </summary>
    public async Task<string?> WarningAsync(MonitorChange change, MonitorWords words, CancellationToken cancellationToken)
    {
        using var foreground = Calls.Foreground();
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(words);
        MonitorSnapshot snapshot;
        var reads = new Dictionary<string, byte>(StringComparer.Ordinal);
        try
        {
            snapshot = await QueryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var active in snapshot.Active.Where(m => !m.Shared))
            {
                try
                {
                    reads[active.Target] = await ReadInputAsync(active, cancellationToken, retry: false).ConfigureAwait(false);
                }
                catch (MonitorException)
                {
                    // Not known: Unknown, or Elsewhere by its mark.
                }
            }
        }
        catch (MonitorException)
        {
            return null;
        }

        MonitorShowing Showing(MonitorInfo monitor) =>
            MonitorShowing.Judge(monitor, reads.TryGetValue(monitor.Target, out byte read) ? read : null, Away.LeftFrom(monitor.Key), words.PcInput(monitor));

        var m = change.Monitor is { } chosen ? snapshot.ByTarget(chosen.Target) : null;
        string? question = null;
        if (change.Input is { } code)
        {
            if (m is { Active: true } && Showing(m).Counts && !snapshot.Active.Any(o => o.Target != m.Target && Showing(o).Counts))
            {
                question = MonitorText.LastScreenQuestion(m.Label, words.Describe(m, code));
            }
        }
        else if (change.Layout is MonitorLayout.Only or MonitorLayout.Primary && m is not null && Showing(m) is { IsElsewhere: true } away)
        {
            question = MonitorText.AwayLayoutQuestion(change.Describe(words), m.Label, MonitorText.Why(away, words, m));
        }
        else if (change.Layout == MonitorLayout.Duplicate && snapshot.Active.FirstOrDefault(o => Showing(o).IsElsewhere) is { } elsewhere)
        {
            question = MonitorText.DuplicateAwayQuestion(elsewhere.Label, MonitorText.Why(Showing(elsewhere), words, elsewhere));
        }

        DiagnosticLog.Debug("Monitors", change.Describe(words) + ": " + (question is null ? "no question" : "asks first") + " ("
            + string.Join(", ", snapshot.Active.Select(o => o.Label + " " + ShowingLog(Showing(o)))) + ")");
        return question;
    }

    // A judgment as the log line names it: "this PC (picked)", "elsewhere (cable)", "unknown".
    private static string ShowingLog(MonitorShowing showing) => showing.Kind switch
    {
        MonitorShowingKind.ThisPc => "this PC",
        MonitorShowingKind.Elsewhere => "elsewhere",
        _ => "unknown",
    } + (showing.Basis is { } basis ? " (" + basis.ToString().ToLowerInvariant() + ")" : "");

    /// <summary>Applies <paramref name="change"/> (no question: the caller asked first) and says what came of it.</summary>
    public async Task<MonitorOutcome> ApplyAsync(MonitorChange change, MonitorWords words, CancellationToken cancellationToken)
    {
        using var foreground = Calls.Foreground();
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(words);
        try
        {
            var outcome = change.Input is { } code ? await SwitchAsync(change, code, words, cancellationToken).ConfigureAwait(false)
                : await ArrangeAsync(change, words, cancellationToken).ConfigureAwait(false);
            DiagnosticLog.Debug("Monitors", change.Describe(words) + ": " + outcome.Kind.ToString());
            return outcome;
        }
        catch (MonitorException e)
        {
            DiagnosticLog.Debug("Monitors", change.Describe(words) + " refused: " + e.Message);
            return MonitorOutcome.Refused(e.Message);
        }
    }

    private async Task<MonitorOutcome> SwitchAsync(MonitorChange change, byte code, MonitorWords words, CancellationToken cancellationToken)
    {
        var before = await QueryAsync(cancellationToken).ConfigureAwait(false);
        var m = before.ByTarget(change.Monitor!.Target);
        if (m is null)
        {
            return MonitorOutcome.Refused(MonitorText.NoSuchTarget);
        }

        if (m.Shared)
        {
            return MonitorOutcome.Refused(MonitorText.SharedSource);
        }

        if (!m.Active)
        {
            return await OffDesktopSwitchAsync(change, m, code, words, cancellationToken).ConfigureAwait(false);
        }

        string input = words.Describe(m, code);
        byte? from = null;
        try
        {
            // A 0x00 read is none (no input has it).
            from = MonitorShowing.Usable(await ReadInputAsync(m, cancellationToken).ConfigureAwait(false));
        }
        catch (MonitorException e) when (e.Message is not (MonitorText.NoDdc or MonitorText.NoInputSwitch or MonitorText.NoMonitorControl))
        {
            // Not known: the set says the rest.
        }

        if (from == code)
        {
            return new MonitorOutcome(MonitorOutcomeKind.Unchanged, MonitorText.AlreadyOn(m.Label, input), before);
        }

        // Both ends judged (later on 2026-10-23): a mark only when it was showing this PC, cleared by a switch to this PC's input.
        byte? picked = words.PcInput(m);
        bool fromCounts = MonitorShowing.Judge(m, from, Away.LeftFrom(m.Key), picked).Counts;
        bool toIsPc = MonitorShowing.Judge(m, code, null, picked).Kind == MonitorShowingKind.ThisPc;
        await Calls.RunAsync("switch " + m.Label, s => s.SetInput(m.Target, code), retry: true, cancellationToken).ConfigureAwait(false);
        Away.Switched(m.Key, from, code, fromCounts, toIsPc);
        await Task.Delay(_settle, _time, cancellationToken).ConfigureAwait(false);
        var after = await QueryAsync(cancellationToken).ConfigureAwait(false);
        var now = after.ByTarget(m.Target);
        if (now is not { Active: true })
        {
            // NVIDIA's driver may still reach it (2026-10-27): one read, no retry, and the sentence says the tab still switches it.
            string left = MonitorText.LeftDesktop(m.Label, input);
            if (now is not null)
            {
                try
                {
                    _ = await ReadInputAsync(now, cancellationToken, retry: false).ConfigureAwait(false);
                    left = MonitorText.LeftDesktopReached(m.Label, input);
                }
                catch (MonitorException)
                {
                    // Not reached: only its own buttons or the other machine.
                }
            }

            return new MonitorOutcome(MonitorOutcomeKind.LeftDesktop, left, after);
        }

        byte current;
        try
        {
            current = await ReadInputAsync(now, cancellationToken).ConfigureAwait(false);
        }
        catch (MonitorException)
        {
            return new MonitorOutcome(MonitorOutcomeKind.Switched, MonitorText.SwitchedNoRead(now.Label, input), after);
        }

        // A 0x00 read says nothing (a monitor that answers zeros would report every switch ignored).
        if (current == 0)
        {
            return new MonitorOutcome(MonitorOutcomeKind.Switched, MonitorText.SwitchedNoRead(now.Label, input), after);
        }

        if (current != code)
        {
            return new MonitorOutcome(MonitorOutcomeKind.Ignored, MonitorText.Ignored(now.Label, input, words.Describe(now, current)), after);
        }

        return new MonitorOutcome(MonitorOutcomeKind.Switched, change.Unlisted ? MonitorText.SwitchedUnlisted(now.Label, input) : MonitorText.Switched(now.Label, input), after);
    }

    /// <summary>
    /// A switch of a monitor off the desktop (2026-10-27, the NVIDIA plan), through NVIDIA's driver: no away mark (it shows nothing of this PC,
    /// and its marks are pruned off the desktop anyway), still off the desktop afterwards is expected, and the input read back says Switched,
    /// Ignored or not read as for any switch. A failure is today's <see cref="MonitorText.OffDesktop"/> when the system didn't reach it (no
    /// NVIDIA card, no match), else <see cref="MonitorText.OffDesktopNoAnswer"/>. A <see cref="MonitorChange.Return"/> then runs Extend all
    /// (after a switch, a send whose read didn't come back, or an input it already showed; never after Ignored): Windows may have added it back
    /// on its own, which the Extend's no-change answer covers with the same outcome.
    /// </summary>
    private async Task<MonitorOutcome> OffDesktopSwitchAsync(MonitorChange change, MonitorInfo m, byte code, MonitorWords words, CancellationToken cancellationToken)
    {
        string input = words.Describe(m, code);
        byte? from;
        try
        {
            from = MonitorShowing.Usable(await ReadInputAsync(m, cancellationToken).ConfigureAwait(false));
            if (from != code)
            {
                await Calls.RunAsync("switch " + m.Label, s => s.SetInput(m.Target, code), retry: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (MonitorException e)
        {
            return MonitorOutcome.Refused(e.Message is MonitorText.OffDesktop or MonitorText.NoSuchTarget ? e.Message : MonitorText.OffDesktopNoAnswer(m.Label));
        }

        MonitorOutcome switched;
        if (from == code)
        {
            switched = new MonitorOutcome(MonitorOutcomeKind.Unchanged, MonitorText.AlreadyOn(m.Label, input));
            if (!change.Return)
            {
                return switched;
            }
        }
        else
        {
            await Task.Delay(_settle, _time, cancellationToken).ConfigureAwait(false);
            var after = await QueryAsync(cancellationToken).ConfigureAwait(false);
            var now = after.ByTarget(m.Target) ?? m;
            byte current;
            try
            {
                current = await ReadInputAsync(now, cancellationToken).ConfigureAwait(false);
            }
            catch (MonitorException)
            {
                current = 0;
            }

            switched = current == 0 ? new MonitorOutcome(MonitorOutcomeKind.Switched, MonitorText.SwitchedNoRead(now.Label, input), after)
                : current != code ? new MonitorOutcome(MonitorOutcomeKind.Ignored, MonitorText.Ignored(now.Label, input, words.Describe(now, current)), after)
                : new MonitorOutcome(MonitorOutcomeKind.Switched, change.Unlisted ? MonitorText.SwitchedUnlisted(now.Label, input) : MonitorText.Switched(now.Label, input), after);
            if (!change.Return || switched.Kind == MonitorOutcomeKind.Ignored)
            {
                return switched;
            }
        }

        MonitorOutcome added;
        try
        {
            added = await ArrangeAsync(MonitorChange.Arrange(MonitorLayout.Extend), words, cancellationToken).ConfigureAwait(false);
        }
        catch (MonitorException e)
        {
            added = MonitorOutcome.Refused(e.Message);
        }

        string label = added.After?.ByTarget(m.Target)?.Label ?? switched.After?.ByTarget(m.Target)?.Label ?? m.Label;
        // Added only when the read after the Extend has it on the desktop (2026-10-27, the review: an Extend Windows ran without it read as added).
        bool on = added.After?.ByTarget(m.Target) is { Active: true };
        if (switched.Kind == MonitorOutcomeKind.Unchanged)
        {
            // It showed this PC's input already: the Extend alone, its own kind (an Extend that changed nothing is no arrangement), its sentence
            // after the input's.
            return new MonitorOutcome(added.Kind, switched.Text + " " + (added.Failed || on ? added.Text : MonitorText.KeptOffDesktop), added.After);
        }

        return added.Failed ? new MonitorOutcome(MonitorOutcomeKind.Switched, MonitorText.SwitchedNotAdded(label, input, added.Text), switched.After)
            : on ? new MonitorOutcome(MonitorOutcomeKind.Switched, MonitorText.SwitchedAndAdded(label, input), added.After)
            : new MonitorOutcome(MonitorOutcomeKind.Switched, MonitorText.SwitchedNotAdded(label, input, MonitorText.KeptOffDesktop), added.After ?? switched.After);
    }

    private async Task<MonitorOutcome> ArrangeAsync(MonitorChange change, MonitorWords words, CancellationToken cancellationToken)
    {
        var kind = change.Layout ?? MonitorLayout.Extend;
        var before = await QueryAsync(cancellationToken).ConfigureAwait(false);
        MonitorInfo? m = null;
        if (kind is MonitorLayout.Only or MonitorLayout.Primary)
        {
            m = change.Monitor is { } chosen ? before.ByTarget(chosen.Target) : null;
            if (m is null)
            {
                return MonitorOutcome.Refused(MonitorText.NoSuchTarget);
            }

            if (kind == MonitorLayout.Primary && !m.Active)
            {
                return MonitorOutcome.Refused(MonitorText.OffDesktop);
            }

            if (kind == MonitorLayout.Primary && before.Layout == MonitorArrangement.Duplicated)
            {
                return MonitorOutcome.Refused(MonitorText.PrimaryWhileDuplicated);
            }
        }

        if (!WouldChange(change with { Monitor = m ?? change.Monitor }, before))
        {
            string already = kind switch
            {
                MonitorLayout.Only => MonitorText.AlreadyOnly(m!.Label),
                MonitorLayout.Primary => MonitorText.AlreadyPrimary(m!.Label),
                MonitorLayout.Duplicate when before.Monitors.Count < 2 => MonitorText.OneMonitor,
                _ => MonitorText.AlreadyArranged(MonitorText.LayoutWord(before.Layout)),
            };
            return new MonitorOutcome(MonitorOutcomeKind.Unchanged, already, before);
        }

        await Calls.RunAsync(change.Describe(words), s => s.Layout(kind, m?.Target, validateOnly: false), retry: false, cancellationToken).ConfigureAwait(false);
        await Task.Delay(_settle, _time, cancellationToken).ConfigureAwait(false);
        var after = await QueryAsync(cancellationToken).ConfigureAwait(false);
        string text = kind switch
        {
            MonitorLayout.Only => MonitorText.OnlyNow(after.ByTarget(m!.Target)?.Label ?? m.Label),
            MonitorLayout.Primary => MonitorText.MadePrimary(after.ByTarget(m!.Target)?.Label ?? m.Label),
            _ => MonitorText.Arranged(MonitorText.LayoutWord(after.Layout)),
        };
        return new MonitorOutcome(MonitorOutcomeKind.Arranged, text, after);
    }
}
