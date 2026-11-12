using System.Collections.Concurrent;
using System.Globalization;
using NeonSidekick.Midi;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--midi-check</c> (2026-11-12, the MIDI plan, <see cref="MonitorCheck"/>'s shape): the MIDI layer's proof on the published binary and
/// the hands-on "what does my pedal send?" — the system there (<see cref="MidiText.NoneHere"/> fails where there is none), the inputs
/// listed, each opened (one another app holds is a passing "in use" line: an absence, not a fault), then every message heard printed raw
/// and decoded (<see cref="MidiText.Heard"/>, "· press" on one that would run a binding) until Ctrl+C or <see cref="ListenFor"/>, then each
/// closed. No input at all is a passing line. Nothing is saved. Not part of the build gate: it needs a device and a hand.
/// </summary>
internal static class MidiCheck
{
    public const string IntroLine = "MIDI check: the inputs listed and opened; press the buttons to see what they send. Nothing is saved.";

    /// <summary>How long it listens.</summary>
    public static readonly TimeSpan ListenFor = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(IAnsiConsole console, IMidiSystem? system, TimeProvider time, CancellationToken cancellationToken, TimeSpan? listenFor = null)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(time);
        console.MarkupLine(Markup.Escape(IntroLine));
        var checks = new List<SmokeCheck>();
        if (system is null)
        {
            checks.Add(new SmokeCheck("midi:system", false, MidiText.NoneHere));
            return Report(console, checks);
        }

        checks.Add(new SmokeCheck("midi:system", true, system.GetType().Name));
        IReadOnlyList<MidiInputInfo> inputs;
        try
        {
            inputs = system.Inputs();
        }
        catch (MidiException e)
        {
            checks.Add(new SmokeCheck("midi:list", false, e.Message));
            return Report(console, checks);
        }

        if (inputs.Count == 0)
        {
            checks.Add(new SmokeCheck("midi:list", true, MidiText.CheckNone));
            return Report(console, checks);
        }

        console.MarkupLine(Markup.Escape(MidiText.CheckInputs(inputs.Count)));
        var occurrences = MidiNames.Occurrences(inputs);
        var heard = new ConcurrentQueue<string>();
        using var wake = new SemaphoreSlim(0);
        var open = new List<(string Name, IMidiInput Input)>();
        try
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                var info = inputs[i];
                string name = MidiText.Device(MidiNames.Normalize(info.Name), occurrences[i]);
                string check = "midi:open " + (i + 1).ToString(CultureInfo.InvariantCulture);
                var parser = new MidiParser();
                var edge = new MidiEdge();
                int occurrence = occurrences[i];
                try
                {
                    var input = system.Open(info, bytes =>
                    {
                        var messages = new List<MidiMessage>(2);
                        parser.Feed(bytes, messages);
                        var decoded = messages.Select(m => MidiText.Heard(new MidiSeen(new MidiTrigger(MidiNames.Normalize(info.Name), occurrence, m.Kind, m.Channel, m.Number), m.Value, edge.Fires(name, m)))).ToList();
                        heard.Enqueue(MidiText.CheckHeard(name, bytes, decoded));
                        wake.Release();
                    });
                    open.Add((name, input));
                    checks.Add(new SmokeCheck(check, true, name));
                    console.MarkupLine(Markup.Escape(MidiText.CheckInput(i + 1, name, null)));
                }
                catch (MidiException e)
                {
                    checks.Add(new SmokeCheck(check, e.InUse, name + ": " + e.Message));
                    console.MarkupLine(Markup.Escape(MidiText.CheckInput(i + 1, name, e.Message)));
                }
            }

            if (open.Count > 0)
            {
                console.MarkupLine(Markup.Escape(MidiText.CheckListening));
                using var timeout = new CancellationTokenSource(listenFor ?? ListenFor, time);
                using var both = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
                while (true)
                {
                    try
                    {
                        await wake.WaitAsync(both.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    while (heard.TryDequeue(out string? line))
                    {
                        console.MarkupLine(Markup.Escape(line));
                    }
                }

                while (heard.TryDequeue(out string? line))
                {
                    console.MarkupLine(Markup.Escape(line));
                }
            }
        }
        finally
        {
            foreach (var (name, input) in open)
            {
                try
                {
                    input.Dispose();
                    checks.Add(new SmokeCheck("midi:close", true, name));
                }
                catch (MidiException e)
                {
                    checks.Add(new SmokeCheck("midi:close", false, name + ": " + e.Message));
                }
            }
        }

        console.MarkupLine(Markup.Escape(MidiText.CheckDone));
        return Report(console, checks);
    }

    private static int Report(IAnsiConsole console, IReadOnlyList<SmokeCheck> checks)
    {
        int failed = 0;
        foreach (var check in checks)
        {
            failed += check.Passed ? 0 : 1;
            string verdict = check.Passed ? Theme.ColorMarkup(Theme.Good, "PASS") : Theme.ColorMarkup(Theme.Bad, "FAIL");
            console.MarkupLine($"  {verdict}  {Markup.Escape(check.Name)}  {Theme.DimMarkup(check.Detail)}");
        }

        console.WriteLine();
        console.MarkupLine(failed == 0
            ? Theme.ColorMarkup(Theme.Good, $"MIDI CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"MIDI CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
