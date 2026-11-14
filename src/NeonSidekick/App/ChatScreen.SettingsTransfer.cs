using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// <c>/settings export</c> and <c>/settings import</c> (2026-11-13, plan parsed-yawning-papert, the user's ask): the settings carried to
/// another computer in a zip in Downloads. Routed by the args' first word before the settings pane (so both work where no pane can open);
/// every other first word is still a search. The flow is <see cref="SettingsTransferCommand"/>'s, under a spinner on the pool, as
/// <c>/sessions export</c> is; mid-reply an export or import waits for the idle line, and a bare import's list prints at once.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>
    /// The user's Downloads folder, where the export lands and the import looks (2026-11-13); set by <see cref="SidekickApp"/> before the screen
    /// runs, and by tests to a temp folder (the review's #12: resolving it here too was a wasted look-up the app overwrote).
    /// </summary>
    public string DownloadsFolder { get; set; } = "";

    /// <summary>
    /// What the start's <c>--import-settings</c> left to say (2026-11-13): shown as notices once the first screen is drawn, since one printed
    /// before the screen would be wiped by its clear. Set by <see cref="SidekickApp"/>.
    /// </summary>
    public IReadOnlyList<SettingsReportLine> StartNotices { get; set; } = [];

    /// <summary>Runs <c>/settings export|import</c>; false when the line isn't one (then it opens the pane as before).</summary>
    private async Task<bool> TrySettingsTransferAsync(string args, CancellationToken cancellationToken)
    {
        if (SettingsTransferCommand.Parse(args) is not { } verb)
        {
            return false;
        }

        var outcome = await SettingsTransferCommand.RunAsync(_settings, verb.Verb, verb.Path, DownloadsFolder, _time, ImportEnvironmentForTests ?? ImportEnvironment.Live,
            (label, work) => _transcript.WithSpinnerAsync(label, work), cancellationToken).ConfigureAwait(false);
        if (outcome is not null)
        {
            ShowSettingsOutcome(outcome);
            if (verb.Verb == SettingsTransferText.ImportWord && verb.Path.Length > 0 && outcome.Error is null)
            {
                CommandsChanged();   // the review's #47: an imported home command runs at once, and the Web UI's names follow
            }
        }

        DrainDiagnostics();
        return true;
    }

    /// <summary>
    /// A bare <c>/settings import</c> under a reply (the review's #94: a list, so no need to wait for the idle line): the zips read on the pool,
    /// the lines through <paramref name="sink"/>, the flow's under a reply.
    /// </summary>
    private async Task ListSettingsZipsAsync(INoticeSink sink)
    {
        string downloads = DownloadsFolder;
        foreach (var line in await Task.Run(() => SettingsArchive.ListZips(downloads)).ConfigureAwait(false))
        {
            sink.Notice(LineText(line));
        }
    }

    /// <summary>The import's view of this computer; tests set a fake (no DPAPI or Keychain).</summary>
    internal ImportEnvironment? ImportEnvironmentForTests { get; set; }

    private void ShowSettingsOutcome(SettingsTransferOutcome outcome)
    {
        if (outcome.Error is { } error)
        {
            _transcript.Error(error);
            return;
        }

        ShowSettingsLines(outcome.Lines);
    }

    private void ShowSettingsLines(IReadOnlyList<SettingsReportLine> lines)
    {
        foreach (var line in lines)
        {
            if (line.Warn)
            {
                _transcript.Notice(LineText(line), NoticeTone.Warn);
            }
            else
            {
                _transcript.Notice(LineText(line));
            }
        }
    }

    /// <summary>A report line as the screen writes it: the profile glyph before it, but a command to copy bare (the review's #84).</summary>
    private static string LineText(SettingsReportLine line) => line.Command ? line.Text : NoticeGlyphs.Profile + line.Text;

    /// <summary>The start's <c>--import-settings</c> report, once, after the first screen (<see cref="StartNotices"/>).</summary>
    private void ShowStartNotices()
    {
        if (StartNotices.Count == 0)
        {
            return;
        }

        ShowSettingsLines(StartNotices);
        StartNotices = [];
        DiagnosticLog.Debug(SettingsTransferText.Category, "The start's settings import was reported.");
    }

    /// <summary>The settings zips in Downloads for completion after <c>/settings import </c>, newest first.</summary>
    private IReadOnlyList<string> SettingsZipChoices() => SettingsArchive.Zips(DownloadsFolder);
}
