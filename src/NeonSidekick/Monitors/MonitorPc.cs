namespace NeonSidekick.Monitors;

/// <summary>
/// What <c>/monitor &lt;n&gt; pc …</c> said and, for a change, how to apply it to the saved <c>Monitor PC inputs</c> list (the caller puts it
/// through its settings' <c>Update</c>). <see cref="Failed"/> marks a refusal.
/// </summary>
public sealed record MonitorPcResult(IReadOnlyList<string> Lines, bool Failed, Func<IReadOnlyList<string>?, List<string>?>? Save = null);

/// <summary>
/// <c>/monitor &lt;n&gt; pc [&lt;input&gt;|now|auto]</c> (later on 2026-10-23, the user's ask: this PC's input a choice, not a name to type), one
/// path for the screen and headless: bare, what is set (the pick, else the cable's guess) and the usage; an input by its given name,
/// standard name or hex code; <c>now</c> the input it shows now, read afresh (a failed read or a 0x00 saves nothing); <c>auto</c> the pick
/// cleared. The pick is saved under <see cref="MonitorWords.SavedKey"/>, as the input names are; a monitor with no key is refused. Never
/// the model's: no tool reaches it (a wrong pick could silence the last screen's question).
/// </summary>
public static class MonitorPc
{
    /// <summary>Runs <paramref name="input"/> (null: bare) for <paramref name="monitor"/> over <paramref name="state"/> and <paramref name="words"/>.</summary>
    public static async Task<MonitorPcResult> RunAsync(MonitorActions actions, MonitorState state, MonitorInfo monitor, MonitorWords words, string? input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(words);
        if (words.SavedKey(monitor) is not { } key)
        {
            return new([MonitorText.PcNoKey(monitor.Label)], true);
        }

        var inputs = state.InputsOf(monitor);
        var rows = words.Rows(monitor, inputs.Offered, inputs.Current, null, all: true);
        var shown = rows.Where(r => !r.Hidden).Select(r => r.Words).ToList();
        string? guessed = MonitorShowing.CableGuess(monitor, inputs.Current, inputs.Offered) is { } guess ? words.Describe(monitor, guess) : null;
        if (input is null)
        {
            string? picked = words.PcInput(monitor) is { } pc ? words.Describe(monitor, pc) : null;
            return new([MonitorText.PcNow(monitor.Label, picked, guessed, monitor.Connector), MonitorText.PcUsage(MonitorCommand.Spelled(monitor), shown)], false);
        }

        string typed = input.Trim();
        if (string.Equals(typed, MonitorCommand.AutoWord, StringComparison.OrdinalIgnoreCase))
        {
            return new([MonitorText.PcCleared(monitor.Label, guessed, monitor.Connector)], false, list => MonitorWords.SetPc(list, key, null));
        }

        if (string.Equals(typed, MonitorCommand.NowWord, StringComparison.OrdinalIgnoreCase))
        {
            byte read;
            try
            {
                read = await actions.ReadInputAsync(monitor, cancellationToken).ConfigureAwait(false);
            }
            catch (MonitorException)
            {
                return new([MonitorText.PcNoRead(monitor.Label)], true);
            }

            return Saved(monitor, words, key, read);
        }

        if (MonitorWords.Resolve(rows, typed) is not { } code)
        {
            return new([MonitorText.UnknownInput(monitor.Label, typed, shown)], true);
        }

        return Saved(monitor, words, key, code, withCode: false);
    }

    /// <summary>The result of picking <paramref name="code"/>: refused for 0x00 (no input has it), else saved and said (with its hex for a read, <paramref name="withCode"/>).</summary>
    public static MonitorPcResult Saved(MonitorInfo monitor, MonitorWords words, string key, byte code, bool withCode = true)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(words);
        if (MonitorShowing.Usable(code) is null)
        {
            return new([MonitorText.PcNoRead(monitor.Label)], true);
        }

        string said = words.Describe(monitor, code) + (withCode && Mccs.StandardName(code) is not null ? " (" + Mccs.Hex(code) + ")" : "");
        return new([MonitorText.PcSet(monitor.Label, said)], false, list => MonitorWords.SetPc(list, key, code));
    }
}
