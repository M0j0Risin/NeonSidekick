using NeonSidekick.Diagnostics;
using NeonSidekick.Midi;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── MIDI control (2026-11-12, the MIDI plan) ──────────────────────────────

internal sealed partial class ChatScreen
{
    private const string MidiCategory = "MIDI";

    /// <summary>The MIDI listening (WinMM on Windows, CoreMIDI on a Mac, a fake in tests); null without a backend: <c>/midi</c> says so.</summary>
    private MidiBoard? _midi;

    /// <summary>The board, for the tests.</summary>
    internal MidiBoard? MidiBoardNow => _midi;

    /// <summary>
    /// The board over <paramref name="system"/>, made in the constructor (nothing opens until <see cref="StartMidi"/>). A button's line is
    /// checked as a command and queued for the screen (<see cref="KeySource.PostCommand"/>); a bound device that can't be opened is said
    /// once on the screen's task. <i>MIDI input</i> and the profile follow <see cref="AppSettings.Changed"/> from any thread: a flip, a
    /// profile switch, a reset, the Web UI's change.
    /// </summary>
    private void InitMidi(IMidiSystem? system, TimeProvider time)
    {
        if (system is null)
        {
            return;
        }

        _midi = new MidiBoard(system, OnMidiLine, notice => _webUi.Notice(notice, error: true), time);
        // The effective settings, as StartMidi reads them (2026-11-14, the review's #45: no override of MIDI input exists today).
        _settings.Changed += _ => _midi?.Apply(_effective().MidiInput, _settings.ProfileDirectory);
        _keys.MidiTaken = line => _pane.Flash(MidiText.Ran(line));
    }

    /// <summary>The board started as the screen starts: off the screen's thread, the bound devices opened when <i>MIDI input</i> is on.</summary>
    private void StartMidi() => _midi?.Apply(_effective().MidiInput, _settings.ProfileDirectory);

    /// <summary>A button's command line, on the board's pump thread: queued when it is a command, else one error line on the screen's task.</summary>
    private void OnMidiLine(string line)
    {
        if (!IsBindableCommand(line))
        {
            DiagnosticLog.Warn(MidiCategory, $"A MIDI binding's {line} isn't a command; not run.");
            _webUi.Notice(MidiText.NotACommand(line), error: true);
            return;
        }

        _keys.PostCommand(line);
    }

    /// <summary>Whether <paramref name="line"/> parses as a command a button may run: a <c>/</c> word, known (built-in or custom), taking what it is given.</summary>
    private bool IsBindableCommand(string line)
    {
        string text = line.Trim();
        if (!text.StartsWith('/') || text.Length < 2)
        {
            return false;
        }

        var (command, _) = ParseWithCustom(text);
        return command is not (SlashCommand.Unknown or SlashCommand.Overloaded);
    }

    /// <summary>
    /// <c>/midi …</c> (2026-11-12): bare the pane, <c>learn [&lt;command&gt;]</c> the learn, <c>list</c> the lines, <c>on</c>/<c>off</c> the
    /// switch (the board opens and closes on its own thread), <c>forget &lt;n&gt;</c> a binding.
    /// </summary>
    private async Task HandleMidiAsync(string args, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (_midi is not { } board)
        {
            sink.Error(MidiText.NoneHere);
            return;
        }

        if (MidiCommand.Parse(args) is not { } parsed)
        {
            sink.Error(MidiText.Usage);
            return;
        }

        switch (parsed.Verb)
        {
            case MidiVerb.Pane or MidiVerb.Learn:
                if (!_pane.Enabled)
                {
                    PrintMidiList(sink);
                    return;
                }

                var menu = new MidiMenu(board, _menuPane, _input, _settings, _effective, CommandChoices, IsBindableCommand, ConfirmAsync);
                try
                {
                    await menu.ShowAsync(parsed.Verb == MidiVerb.Learn, parsed.Command, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _menuPane.Close();
                }

                return;
            case MidiVerb.List:
                PrintMidiList(sink);
                return;
            case MidiVerb.On or MidiVerb.Off:
                bool on = parsed.Verb == MidiVerb.On;
                if (_effective().MidiInput == on)
                {
                    sink.Notice(on ? MidiText.AlreadyOn : MidiText.AlreadyOff);
                    return;
                }

                _settings.Update(d => d.MidiInput = on);
                sink.Notice(on ? MidiText.TurnedOn : MidiText.TurnedOff);
                return;
            case MidiVerb.Forget:
                var set = new MidiBindings(_settings.ProfileDirectory).Load();
                if (MidiText.FileProblem(set.State) is { } problem)
                {
                    sink.Error(problem);   // a broken or newer file named, never "no bindings" (2026-11-14, the review's #59)
                    return;
                }

                if (parsed.Number < 1 || parsed.Number > set.Bindings.Count)
                {
                    sink.Error(MidiText.ForgetUsage(set.Bindings.Count));
                    return;
                }

                var binding = set.Bindings[parsed.Number - 1];
                switch (new MidiBindings(_settings.ProfileDirectory).Forget(parsed.Number - 1))
                {
                    case MidiSaveOutcome.Saved:
                        board.Reload();
                        sink.Notice(MidiText.Forgot(parsed.Number, binding));
                        break;
                    case MidiSaveOutcome.Newer:
                        sink.Error(MidiText.NewerFile);
                        break;
                    case MidiSaveOutcome.Gone:
                        sink.Error(MidiText.ChangedOnDisk);
                        break;
                    default:
                        sink.Error(MidiText.SaveFailed);
                        break;
                }

                return;
        }
    }

    /// <summary><c>/midi list</c>: the bindings read afresh, numbered, the skipped entries after, and a line when the input is off.</summary>
    private void PrintMidiList(INoticeSink sink)
    {
        var set = new MidiBindings(_settings.ProfileDirectory).Load();
        if (MidiText.FileProblem(set.State) is { } problem)
        {
            sink.Warning(problem);
        }

        if (set.Bindings.Count == 0 && set.Skipped.Count == 0)
        {
            sink.Notice(MidiText.NoBindings);
            return;
        }

        foreach (string line in MidiMenu.ListLines(set))
        {
            sink.Notice(line);
        }

        if (!_effective().MidiInput && set.Bindings.Count > 0)
        {
            sink.Notice(MidiText.OffWithBindings);
        }
    }

    /// <summary><c>/midi</c>'s completion: the verbs, the commands after <c>learn</c>, the bindings' numbers after <c>forget</c>.</summary>
    private IReadOnlyList<CompletionItem> MidiChoices(string argText) =>
        _midi is { } board ? MidiCommand.Complete(argText, CommandChoices(), board.Bindings.Bindings) : [];
}
