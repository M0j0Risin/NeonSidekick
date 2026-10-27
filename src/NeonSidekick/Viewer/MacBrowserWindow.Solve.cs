using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using NeonSidekick.Web;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

// ── A bot check (2026-10-18, the Mac Browser plan's Phase 3) ──────────────

/// <summary>
/// A web tool's bot check in the Mac's window, as Windows' step for step: the window titled, shown and activated on the address; a look every
/// <see cref="DomSettle.PollInterval"/> by the shared watch (<see cref="BrowserCheckWatch"/>, since 2026-10-20: the code review's G8, Windows'
/// too), its reads in the app's own content world; once through, the address it ended on, the DOM (cut at
/// <see cref="WebFetcher.MaxDownloadBytes"/>), the status, the PDF for <c>convert_to_pdf</c> (paged by WebKit's print operation, Phase 0) and the
/// store's cookies for the two hosts go back; the window hides, gives the keyboard back, and closes after <see cref="BrowserJobs.Linger"/>
/// unless a visit holds it or another check comes. Downloads and answers that are no page end it as a file. A user-initiated activity is
/// held while it runs (pass 28).
/// </summary>
internal sealed partial class MacBrowserWindow : IBrowserCheckPage
{
    private long _printToken;
    private readonly Dictionary<long, Action<bool>> _printsWaiting = [];

    // Whether the linger runs (a check's end hid the window): Open hands such a window out only with its linger started afresh (C3).
    private bool _lingering;

    private void BeginSolve(BotCheckRequest request, TaskCompletionSource<BotCheckResult> done)
    {
        var (_, older) = _jobs.BeginCheck(request, done);
        if (older is not null)
        {
            Finish(older, BotCheckResult.Ended(BotCheckEnd.GaveUp, older.Request.Url.AbsoluteUri), close: false);
        }

        StopLinger();
        DiagnosticLog.Info("Browser", $"A bot check on {request.Url.Host} for the user to solve ({request.Why}).");
        ShowTitle();
        BeginActivity();
        if (_jobs.Check is { } check)
        {
            BrowserCheckWatch.Start(this, check, () => Go(request.Url.AbsoluteUri));   // a look at the page from before first (2026-10-26)
        }

        Show();
        _poll.Start(DomSettle.PollInterval);
    }

    private void CancelSolve(TaskCompletionSource<BotCheckResult> done)
    {
        if (_jobs.Check is { } check && check.Done == done)
        {
            Finish(check, BotCheckResult.Ended(BotCheckEnd.GaveUp, check.Request.Url.AbsoluteUri), close: BrowserJobs.CancelCloses(check));
        }
        else
        {
            Settle(done, BotCheckResult.Ended(BotCheckEnd.GaveUp, ""));
        }
    }

    // Once per check (2026-10-20, the code review: a late cookie answer or a stale look could end one twice, hiding the window under the
    // next check and ending its activity early).
    private void Finish(BrowserCheck check, BotCheckResult result, bool close)
    {
        if (!check.TryEnd())
        {
            return;
        }

        if (_jobs.Check == check)
        {
            _poll.Stop();
        }

        ReleaseHeld(check);
        bool hide = _jobs.EndCheck(check, close);
        Settle(check.Done, result);
        DiagnosticLog.Info("Browser", BrowserText.CheckEndedLogLine(check.Request.Url.Host, result));
        if (!Alive)
        {
            return;
        }

        ShowTitle();
        EndActivity();
        if (hide)
        {
            // Hidden at once, the keyboard given back; closed for good after Linger unless another check comes.
            Hide();
            _lingering = true;
            _linger.Start(BrowserJobs.Linger);
        }
    }

    private void StopLinger()
    {
        _lingering = false;
        _linger.Stop();
    }

    /// <summary>
    /// The window handed out by <see cref="MacBrowserWindows.Open"/> for a check or a visit about to be posted to it (2026-10-20, the code
    /// review's C3): a window hidden in a check's linger gets its linger afresh, so the close it was counting to cannot come before the posted
    /// begin, which stops it. Afresh rather than stopped: a caller cancelled before it posted leaves no hidden window behind. Main thread.
    /// </summary>
    internal void HandOut()
    {
        if (_lingering)
        {
            _linger.Start(BrowserJobs.Linger);
        }
    }

    private void LingerOut()
    {
        _lingering = false;
        if (_jobs.LingerCloses())
        {
            CloseQuietly();
        }
    }

    // One look at the page while a check waits (BrowserCheckWatch); MainTimer fires once, so the next look is set first.
    private void Tick()
    {
        if (_jobs.Check is not { } check)
        {
            return;
        }

        _poll.Start(DomSettle.PollInterval);
        BrowserCheckWatch.Tick(this, check);
    }

    // A file instead of a page during a check: it ends as a file, its cookies what the caller wants.
    private void EndAsFile()
    {
        if (_jobs.Check is { } check)
        {
            BrowserCheckWatch.EndAsFile(this, check);
        }
    }

    bool IBrowserCheckPage.Holds(BrowserCheck check) => _jobs.Check == check && Alive;

    string? IBrowserCheckPage.Source => Source;

    BrowserIdentity IBrowserCheckPage.Identity => _identity;

    void IBrowserCheckPage.Finish(BrowserCheck check, BotCheckResult result, bool close) => Finish(check, result, close);

    // WebKit's documents carry no status: the main frame's latest answer for the look's address (else the latest at all), and the window's own
    // error page, loaded with the failing address as its base (2026-10-26, the bot check's redesign).
    (int Status, bool ErrorPage) IBrowserCheckPage.Describe(BrowserLook look) =>
        (MacBrowserRules.StatusFor(_responses, look.Url), _errorShownFor is { } failed && MacBrowserRules.SameAddress(failed, look.Url));

    bool IBrowserCheckPage.Evaluate(string expression, Action<string?> answer)
    {
        if (_web == 0)
        {
            return false;
        }

        MacBrowserKit.Evaluate(_web, expression, answer);
        return true;
    }

    // The check's PDF: WebKit's print operation run modal for the window, so it counts as the window's sheet (2026-10-20, the code review's G2:
    // without _sheetUp a page's window.print(), ⌘P or a dialog started a second modal on the window — an Objective-C exception, which ends a
    // NativeAOT process — or the PDF was never written). A sheet already up means no print: the answer goes without a PDF.
    bool IBrowserCheckPage.PrintToPdf(string path, Action<bool> written)
    {
        if (_web == 0 || _sheetUp)
        {
            return false;
        }

        long token = ++_printToken;
        _sheetUp = true;
        _printsWaiting[token] = ok =>
        {
            _sheetUp = false;
            written(ok && HasPdfHeader(path));
        };
        if (MacBrowserKit.Print(_web, Window, _delegate, path, token))
        {
            return true;
        }

        _printsWaiting.Remove(token);
        _sheetUp = false;
        return false;
    }

    // The check's cookies within BrowserJobs.HarvestTimeout (2026-10-20, the code review's G4: a stalled network process held the pane for the
    // whole solve, the visit's close already being capped so).
    bool IBrowserCheckPage.ReadCookies(IReadOnlyList<string> hosts, Action<IReadOnlyList<BrowserCookie>> answer)
    {
        if (_store == 0)
        {
            return false;
        }

        CookiesWithin(_store, hosts, BrowserJobs.HarvestTimeout, answer);
        return true;
    }

    /// <summary>
    /// <paramref name="store"/>'s cookies for <paramref name="hosts"/> to <paramref name="answer"/> once: what the store says, or none when it
    /// says nothing within <paramref name="timeout"/>. The block WebKit holds reaches the answer only through a box the timeout empties, so a
    /// store that never answers keeps a box alive, not the window (G4). Main thread.
    /// </summary>
    private static void CookiesWithin(nint store, IReadOnlyList<string> hosts, TimeSpan timeout, Action<IReadOnlyList<BrowserCookie>> answer)
    {
        var box = new StrongBox<Action<IReadOnlyList<BrowserCookie>>?>(answer);
        var timer = new MainTimer(() => Take(box)?.Invoke([]));
        timer.Start(timeout);
        MacBrowserKit.StoreCookies(store, all =>
        {
            timer.Stop();
            Take(box)?.Invoke(all.Where(c => hosts.Any(c.AppliesTo)).ToList());
        });

        static Action<IReadOnlyList<BrowserCookie>>? Take(StrongBox<Action<IReadOnlyList<BrowserCookie>>?> box)
        {
            var taken = box.Value;
            box.Value = null;
            return taken;
        }
    }

    private static bool HasPdfHeader(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[5];
            return file.Read(head) == 5 && head.SequenceEqual("%PDF-"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A print run by the window ended (a check's PDF, ⌘P's sheet).</summary>
    public void PrintEnded(long token, bool ok)
    {
        if (_printsWaiting.Remove(token, out var then))
        {
            then(ok);
        }
    }
}
