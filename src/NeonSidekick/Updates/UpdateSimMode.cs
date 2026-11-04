#if UPDATE_SIM
using System.Globalization;

namespace NeonSidekick.Updates;

/// <summary>
/// A simulation build's flags (2026-11-04, self-update; <c>build.ps1 -UpdateSim</c>, driven by <c>tools/UpdateSim.cs</c>). Compiled into no shipped build, and taken off the
/// arguments before <see cref="App.SidekickOptions"/> reads them, so they stay out of its usage and <c>Help/HelpStartup</c>; the smoke's <c>update:no-sim-flags</c> holds that a shipped build doesn't know them.
/// </summary>
public sealed class UpdateSimFlags
{
    /// <summary><c>--update-source &lt;url&gt;</c>: the fake GitHub, with its one loopback origin in place of <c>github.com</c> and HTTPS.</summary>
    public Uri? Source { get; private set; }

    /// <summary><c>--update-as-version &lt;x.y.z&gt;</c>: makes the app believe it's this version, so one publish is enough.</summary>
    public string? AsVersion { get; private set; }

    /// <summary><c>--update-check &lt;mode&gt;</c>: the steps with no UI. See <see cref="UpdateSimMode"/>.</summary>
    public string? Check { get; private set; }

    /// <summary><c>--update-move-all</c>: every file counts as changed. One binary means the exe's hash doesn't change between "versions", so without it the simulation never moves the exe.</summary>
    public bool MoveAll { get; private set; }

    /// <summary><c>--update-swap-delay &lt;ms&gt;</c>: a pause between moves, since the real swap takes milliseconds and "killed halfway" has nothing to hit without it.</summary>
    public int SwapDelayMs { get; private set; }

    /// <summary><c>--update-test-key &lt;base64&gt;</c>: a public key to trust. UpdateSim makes a new pair each run, so no private key is ever committed to this public repo.</summary>
    public string? TestKey { get; private set; }

    /// <summary>Takes this class's flags off <paramref name="args"/>, leaving the rest for the app's own parser.</summary>
    public static UpdateSimFlags Take(ref string[] args)
    {
        var flags = new UpdateSimFlags();
        var rest = new List<string>();
        string[] given = args;
        for (int i = 0; i < given.Length; i++)
        {
            string Value() => ++i < given.Length ? given[i] : throw new ArgumentException($"{given[i - 1]} needs a value");
            switch (given[i])
            {
                case "--update-source":
                    flags.Source = new Uri(Value());
                    break;
                case "--update-as-version":
                    flags.AsVersion = Value();
                    break;
                case "--update-check":
                    flags.Check = Value();
                    break;
                case "--update-move-all":
                    flags.MoveAll = true;
                    break;
                case "--update-swap-delay":
                    flags.SwapDelayMs = int.Parse(Value(), CultureInfo.InvariantCulture);
                    break;
                case "--update-test-key":
                    flags.TestKey = Value();
                    break;
                default:
                    rest.Add(given[i]);
                    break;
            }
        }

        args = [.. rest];
        return flags;
    }

    /// <summary>The context with this run's version, key, move-all and pause.</summary>
    public UpdateContext Apply(UpdateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        int delay = SwapDelayMs;
        return context with
        {
            Running = AsVersion is { } version && UpdateVersion.TryParse(version, out var running) ? running : context.Running,
            Keys = TestKey is { } key ? [.. context.Keys, key] : context.Keys,
            MoveAll = MoveAll,
            BetweenSteps = delay > 0 ? () => Thread.Sleep(delay) : null,
        };
    }

    /// <summary>The fake GitHub, or null for the real one.</summary>
    public UpdateSource? SourceOrNull() => Source is { } origin ? UpdateSource.Simulated(origin) : null;
}

/// <summary>
/// <c>--update-check &lt;mode&gt;</c> (2026-11-04, self-update): check, stage, probe and swap with no UI, each step's outcome on a line of its own for <c>tools/UpdateSim.cs</c> to read. The one check mode that swaps.
/// <c>all</c> is every step; <c>check</c>, <c>stage</c>, <c>exit</c>, <c>start</c>, <c>disk</c>, <c>notes</c>, <c>cancel</c> and <c>rollback</c> stop at or run just that one, so a scenario can change the folder between them.
/// </summary>
public static class UpdateSimMode
{
    public static async Task<int> RunAsync(UpdateSimFlags flags, UpdateContext context, string? startLine, UpdateExit exit, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(say);
        string mode = flags.Check ?? "all";
        say("start: " + (startLine ?? "nothing to say"));
        var updater = new AppUpdater(context, startLine, source: flags.SourceOrNull());
        switch (mode)
        {
            case "start":
                return 0;
            case "disk":
                var disk = updater.ReadDisk();
                say($"disk: ready {disk.ReadyVersion ?? "none"}, rollback {disk.RollbackTarget ?? "none"}");
                return 0;
            case "notes":
                say("notes: " + (updater.RunningNotes() ?? "none"));
                return 0;
            case "cancel":
                updater.CancelRollback();
                string? busy = updater.DropStage(alsoParts: true);
                say("cancel: " + (busy ?? "done"));
                return busy is null ? 0 : 1;
            case "exit":
                exit.Run(say);
                say("exit: done");
                return 0;
            case "rollback":
                string? refused = updater.AskRollback(CancellationToken.None, out string? target);
                if (refused is not null)
                {
                    say("rollback: refused: " + refused);
                    return 1;
                }

                say("rollback: to " + target);
                exit.Run(say);
                say("exit: done");
                return 0;
        }

        var answer = await updater.CheckAsync(CancellationToken.None).ConfigureAwait(false);
        say($"check: {answer.Kind} {answer.Found} {answer.Reason}".TrimEnd());
        if (mode == "check" || answer is not { Kind: UpdateCheckKind.Newer, Release: { } release })
        {
            return 0;
        }

        if (updater.Preflight(release.ArchiveBytes) is { } preflight)
        {
            say("preflight: refused: " + preflight);
            return 1;
        }

        var progress = new SayingProgress(say);
        var result = await updater.StageAsync(release, progress, release.Notes, CancellationToken.None).ConfigureAwait(false);
        if (!result.Ok)
        {
            say("stage: failed: " + result.Reason);
            return 1;
        }

        say("stage: ready " + release.Version);
        if (mode == "stage")
        {
            return 0;
        }

        exit.Run(say);
        say("exit: done");
        return 0;
    }

    // On the reporter's thread, so the lines keep their order; a line for each quarter, since a scenario only needs to see that it moved.
    private sealed class SayingProgress(Action<string> say) : IProgress<UpdateStagePhase>
    {
        private int _quarter = -1;
        private bool _checking;

        public void Report(UpdateStagePhase value)
        {
            switch (value)
            {
                case UpdateStagePhase.Downloading downloading when downloading.Percent / 25 != _quarter:
                    _quarter = downloading.Percent / 25;
                    say($"stage: downloading {downloading.Percent}%");
                    break;
                case UpdateStagePhase.Checking when !_checking:
                    _checking = true;
                    say("stage: checking");
                    break;
            }
        }
    }
}
#endif
