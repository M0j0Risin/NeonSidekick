using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Monitors;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the monitor tools reach (2026-10-23, the monitors plan): the screen's board (its one worker, its away marks), the settings in force for
/// the input names, and the yes/no every change asks first (<c>ChatScreen.ConfirmMonitorAsync</c>: the pane and the Web UI's page at once;
/// null with nobody to ask, which refuses).
/// </summary>
public sealed record MonitorToolHooks(MonitorBoard Board, Func<AppSettingsData> Effective, Func<string, CancellationToken, Task<bool?>>? Confirm);

/// <summary>
/// The monitor tools' shared steps (2026-10-23): <c>monitor_list</c> reads, <c>monitor_input</c> and <c>monitor_layout</c> change — each
/// change asked first through <see cref="MonitorToolHooks.Confirm"/> (its question naming the monitor and the input or layout, and the last
/// screen's warning when it applies), run through <see cref="MonitorActions"/>, and written to the log (what, which monitor, yes, declined
/// or failed). Offered only on Windows, with a pane, under <c>Monitor tools</c>; never to bots or advisors. Hardware text (names, the
/// capabilities string) never reaches the model unscrubbed, and the raw capabilities string never at all.
/// </summary>
public abstract class MonitorTool : AIFunction
{
    private protected MonitorTool(MonitorToolHooks hooks)
    {
        Hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
    }

    private protected MonitorToolHooks Hooks { get; }

    private protected MonitorWords Words(MonitorSnapshot snapshot) => MonitorWords.For(Hooks.Effective(), snapshot.Monitors);

    /// <summary>The monitor <c>monitor</c> names (a number from <c>monitor_list</c> or a name, a JSON number or a string), or the refusal.</summary>
    private protected async Task<(MonitorInfo? Monitor, string? Error)> MonitorArgAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!ToolArguments.Has(arguments, "monitor") || ToolArguments.ReadString(arguments, "monitor").Trim().Length == 0)
        {
            return (null, MonitorText.MissingArgument("monitor"));
        }

        var snapshot = await Hooks.Board.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var monitor = MonitorCommand.Find(snapshot, ToolArguments.ReadString(arguments, "monitor").Trim('"', ' '), out string? error);
        return (monitor, error is null ? null : "Error: " + error);
    }

    /// <summary>Asks, applies and logs a change; the result the model reads.</summary>
    private protected async Task<string> ChangeAsync(MonitorChange change, CancellationToken cancellationToken)
    {
        var words = Words(Hooks.Board.State.Topology);
        string what = change.Describe(words);
        if (Hooks.Confirm is not { } confirm)
        {
            DiagnosticLog.Info("Monitors", Name + ": " + what + " — refused, nobody to ask");
            return MonitorText.NoPaneToAsk;
        }

        string? warning = await Hooks.Board.Actions.WarningAsync(change, words, cancellationToken).ConfigureAwait(false);
        bool? yes = await confirm(MonitorText.ToolQuestion(what, warning), cancellationToken).ConfigureAwait(false);
        if (yes is null)
        {
            DiagnosticLog.Info("Monitors", Name + ": " + what + " — refused, nobody to ask");
            return MonitorText.NoPaneToAsk;
        }

        if (yes == false)
        {
            DiagnosticLog.Info("Monitors", Name + ": " + what + " — declined");
            return MonitorText.ToolDeclined;
        }

        var outcome = await Hooks.Board.Actions.ApplyAsync(change, words, cancellationToken).ConfigureAwait(false);
        Hooks.Board.NoteAway();
        DiagnosticLog.Info("Monitors", Name + ": " + what + " — yes, " + (outcome.Failed ? "failed: " : "") + outcome.Text);
        return outcome.Failed ? "Error: " + outcome.Text : outcome.Text;
    }
}

/// <summary><c>monitor_list()</c> (2026-10-23): the layout, then a line per monitor — the same lines <c>/monitor list</c> prints. Plan-mode read-only.</summary>
public sealed class MonitorListTool(MonitorToolHooks hooks) : MonitorTool(hooks)
{
    public const string ToolName = "monitor_list";

    private static readonly System.Text.Json.JsonElement Schema = ToolSchema.Parse("{ \"type\": \"object\", \"properties\": {} }");

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Lists the user's monitors: number, name, connector, on the desktop or not, primary, mode, current input and its inputs, with the layout.";

    public override System.Text.Json.JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        await Hooks.Board.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await Hooks.Board.ReadInputsAsync(cancellationToken).ConfigureAwait(false);
        var state = Hooks.Board.State;
        return string.Join('\n', MonitorText.ListLines(state, Words(state.Topology)));
    }
}

/// <summary>
/// <c>monitor_input(monitor, input)</c> (2026-10-23): switches a monitor's input over DDC/CI after the user's yes. <c>input</c> is a given name,
/// a standard name (<c>HDMI-1</c>) or a code in hex; a code the monitor neither lists nor is common is still sent, and its result says so.
/// </summary>
public sealed class MonitorInputTool(MonitorToolHooks hooks) : MonitorTool(hooks)
{
    public const string ToolName = "monitor_input";

    private static readonly System.Text.Json.JsonElement Schema = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "monitor": { "type": "string", "description": "The monitor's number from monitor_list, or its name." },
            "input": { "type": "string", "description": "An input's name (\"HDMI-1\", a name the user gave it) or a code in hex (\"0x1B\")." }
          },
          "required": ["monitor", "input"]
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Switches a monitor's input (asks the user first). Switching away from this PC's input may drop the monitor from this PC; say so.";

    public override System.Text.Json.JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!ToolArguments.Has(arguments, "input") || ToolArguments.ReadString(arguments, "input").Trim().Length == 0)
        {
            return MonitorText.MissingArgument("input");
        }

        var (monitor, error) = await MonitorArgAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (monitor is null)
        {
            return error;
        }

        // Off the desktop it switches through NVIDIA's driver, or the change says why not (2026-10-27, the NVIDIA plan).
        if (monitor.Shared)
        {
            return "Error: " + MonitorText.SharedSource;
        }

        var state = Hooks.Board.State;
        if (!monitor.Active)
        {
            // Read before any question (2026-10-27, the review): one NVIDIA's driver can't reach was asked about, then refused after the yes.
            // The board's own read keeps what it found, or the failure as the monitor's problem.
            await Hooks.Board.ReadInputsAsync(cancellationToken, onlyUnread: true).ConfigureAwait(false);
            state = Hooks.Board.State;
            if (state.InputsOf(monitor).Problem is { } problem)
            {
                return "Error: " + problem;
            }
        }

        var inputs = state.InputsOf(monitor);
        var words = Words(state.Topology);
        var rows = words.Rows(monitor, inputs.Offered, inputs.Current, state.AwayOf(monitor), all: true);
        string typed = ToolArguments.ReadString(arguments, "input").Trim();
        if (MonitorWords.Resolve(rows, typed) is not { } code)
        {
            return "Error: " + MonitorText.UnknownInput(monitor.Label, typed, rows.Where(r => !r.Hidden).Select(r => r.Words).ToList());
        }

        return await ChangeAsync(state.SwitchTo(monitor, code, words), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>monitor_layout(action, monitor?)</c> (2026-10-23): extend every monitor, duplicate them, only one monitor, or make one primary, after the
/// user's yes; <c>monitor</c> is required for <c>only</c> and <c>primary</c>.
/// </summary>
public sealed class MonitorLayoutTool(MonitorToolHooks hooks) : MonitorTool(hooks)
{
    public const string ToolName = "monitor_layout";

    private static readonly System.Text.Json.JsonElement Schema = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["extend", "duplicate", "only", "primary"] },
            "monitor": { "type": "string", "description": "For only and primary: the monitor's number from monitor_list, or its name." }
          },
          "required": ["action"]
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Changes the monitors' layout like Win+P (asks the user first): extend, duplicate, only one monitor, or make one primary.";

    public override System.Text.Json.JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!ToolArguments.Has(arguments, "action") || ToolArguments.ReadString(arguments, "action").Trim().Length == 0)
        {
            return MonitorText.MissingArgument("action");
        }

        string action = ToolArguments.ReadString(arguments, "action").Trim().ToLowerInvariant();
        MonitorLayout? kind = action switch
        {
            "extend" => MonitorLayout.Extend,
            "duplicate" => MonitorLayout.Duplicate,
            "only" => MonitorLayout.Only,
            "primary" => MonitorLayout.Primary,
            _ => null,
        };
        if (kind is not { } layout)
        {
            return MonitorText.BadAction;
        }

        MonitorInfo? monitor = null;
        if (layout is MonitorLayout.Only or MonitorLayout.Primary)
        {
            if (!ToolArguments.Has(arguments, "monitor") || ToolArguments.ReadString(arguments, "monitor").Trim().Length == 0)
            {
                return MonitorText.NeedsMonitor(action);
            }

            (monitor, string? error) = await MonitorArgAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (monitor is null)
            {
                return error;
            }
        }
        else
        {
            await Hooks.Board.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ChangeAsync(MonitorChange.Arrange(layout, monitor), cancellationToken).ConfigureAwait(false);
    }
}
