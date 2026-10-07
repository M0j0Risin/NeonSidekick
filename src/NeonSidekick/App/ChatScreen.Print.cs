using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Printing;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── Printing (2026-09-28) ─────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The print door <c>print_file</c>, <c>list_printers</c> and <c>/print</c> share: the spooler, the sandbox, the settings in force.</summary>
    private readonly PrintService _print;

    /// <summary>The two print tools, built once over <see cref="_print"/>; offered while <see cref="PrintOffered"/> says so.</summary>
    private readonly IReadOnlyList<AIFunction> _printTools;

    /// <summary>
    /// The print tools (2026-09-28): <c>list_printers</c>, then <c>print_file</c> behind <paramref name="confirm"/> for what the
    /// policy asks about (null where nothing can ask: headless). Shared with headless.
    /// </summary>
    public static IReadOnlyList<AIFunction> PrintTools(PrintService print, Func<string, CancellationToken, Task<bool?>>? confirm) =>
    [
        new ListPrintersTool(print),
        new PrintFileTool(print, confirm),
    ];

    /// <summary>The print tools' names: their result's first line is the transcript's note (<see cref="PrintText.Note"/>).</summary>
    public static readonly IReadOnlySet<string> PrintToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        ListPrintersTool.ToolName,
        PrintFileTool.ToolName,
    };

    /// <summary>Whether the print group is offered (2026-09-28): the setting <c>Print tools</c>.</summary>
    public static bool PrintOffered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.PrintTools && !OperatingSystem.IsMacOS();   // printing needs Windows for now (2026-10-06, the macOS build); /tools says so
    }

    /// <summary>
    /// A model's print's question, on the turn task: the yes/no pane through the watcher, as <see cref="ConfirmHomeAsync"/> —
    /// the cursor on No, ESC a no. Null (never asked) without the pane or a watcher to run it.
    /// </summary>
    private async Task<bool?> ConfirmPrintAsync(string question, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        var paneToken = _paneClose?.Token ?? CancellationToken.None;
        bool? yes = null;
        var pending = _keys.RequestPaneAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(paneToken, turnToken);
            try
            {
                yes = await _menu.ConfirmAsync(question, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !linked.IsCancellationRequested)
            {
                DiagnosticLog.Error(ScreenPane.Category, "The print confirm pane failed: " + Llm.Assistant.Explain(ex), ex);
            }
        });
        try
        {
            await pending.WaitAsync(turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
        {
            // No watcher to run the pane: never asked.
            return null;
        }

        return yes ?? false;
    }

    /// <summary>
    /// <c>/print …</c> (2026-09-28): <see cref="PrintCommand.RunAsync"/> under a spinner, its lines as notices (a failure's as an
    /// error). The user's own hand: no policy, no pane, nothing added to the conversation.
    /// </summary>
    private async Task HandlePrintAsync(string args, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacOS())
        {
            _transcript.Error(PrintText.NeedsWindows);   // 2026-10-06: no spooler on a Mac yet; Windows unchanged
            return;
        }

        PrintCommandResult result;
        try
        {
            result = await _transcript.WithSpinnerAsync(PrintText.Working, () => PrintCommand.RunAsync(_print, args, () => _log.LastReply, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // The bare /print and /print printers list the printers on the info pane (2026-10-04, the user's pick).
        if (result.Paned && !result.Failed)
        {
            await ShowLinesAsync(TreeText.PaneLabel("/print", args), result.Lines, _transcript, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (string line in result.Lines)
        {
            if (result.Failed)
            {
                _transcript.Error(line);
            }
            else
            {
                _transcript.Notice(line);
            }
        }
    }

    /// <summary><c>/print</c>'s word list: the verbs, then the options with the printers' names (<see cref="PrintCommand.Complete"/>).</summary>
    private IReadOnlyList<CompletionItem> PrintChoices(string argText) =>
        PrintCommand.Complete(argText, _printerNames.Get());

    /// <summary>The printers' names for the argument list, read at most every half minute: listing them can take a moment.</summary>
    private readonly PrinterNameCache _printerNames;

    private sealed class PrinterNameCache(PrintService print, TimeProvider time)
    {
        private IReadOnlyList<string> _names = [];
        private DateTimeOffset _read = DateTimeOffset.MinValue;

        public IReadOnlyList<string> Get()
        {
            var now = time.GetUtcNow();
            if (now - _read > TimeSpan.FromSeconds(30))
            {
                _names = print.Printers().Select(p => p.Name).ToList();
                _read = now;
            }

            return _names;
        }
    }
}
