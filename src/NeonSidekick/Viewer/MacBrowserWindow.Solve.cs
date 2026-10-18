using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using NeonSidekick.Web;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

// ── A bot check (2026-10-18, the Mac Browser plan's Phase 3) ──────────────

/// <summary>
/// A web tool's bot check in the Mac's window, as Windows' step for step: the window titled, shown and activated on the address; a look every
/// <see cref="DomSettle.PollInterval"/> at the page's fingerprint, and its DOM when <see cref="BrowserWatch.WantsRead"/> says, both read in the
/// app's own content world; once <see cref="BrowserWatch.Decide"/> says through, the address it ended on, the DOM (cut at
/// <see cref="WebFetcher.MaxDownloadBytes"/>), the status, the PDF for <c>convert_to_pdf</c> (paged by WebKit's print operation, Phase 0) and the
/// store's cookies for the two hosts go back; the window hides, gives the keyboard back, and closes after <see cref="BrowserJobs.Linger"/>
/// unless a visit holds it or another check comes. Downloads and answers that are no page end it as a file. A user-initiated activity is
/// held while it runs (pass 28).
/// </summary>
internal sealed partial class MacBrowserWindow
{
    private long _printToken;
    private readonly Dictionary<long, Action<bool>> _printsWaiting = [];

    private void BeginSolve(BotCheckRequest request, TaskCompletionSource<BotCheckResult> done)
    {
        var (check, older) = _jobs.BeginCheck(request, done);
        if (older is not null)
        {
            Finish(older, BotCheckResult.Ended(BotCheckEnd.GaveUp, older.Request.Url.AbsoluteUri), close: false);
        }

        _linger.Stop();
        DiagnosticLog.Info("Browser", $"A bot check on {request.Url.Host} for the user to solve ({request.Why}).");
        ShowTitle();
        BeginActivity();
        Go(request.Url.AbsoluteUri);
        Show();
        _poll.Start(DomSettle.PollInterval);
        _ = check;
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

    private void Finish(BrowserCheck check, BotCheckResult result, bool close)
    {
        if (_jobs.Check == check)
        {
            _poll.Stop();
        }

        ReleaseHeld(check);
        bool hide = _jobs.EndCheck(check, close);
        Settle(check.Done, result);
        DiagnosticLog.Info("Browser", $"The bot check on {check.Request.Url.Host} ended: {result.End}{(result.Detail.Length > 0 ? " (" + result.Detail + ")" : "")}.");
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
            _linger.Start(BrowserJobs.Linger);
        }
    }

    private void LingerOut()
    {
        if (_jobs.LingerCloses())
        {
            CloseQuietly();
        }
    }

    // One look at the page while a check waits: its fingerprint, the DOM when that is due, then the watch's say.
    private void Tick()
    {
        if (_jobs.Check is not { } check)
        {
            return;
        }

        _poll.Start(DomSettle.PollInterval);   // MainTimer fires once: the next look is set first
        if (check.Busy || check.Finishing || !check.Watch.Loaded || _web == 0)
        {
            return;
        }

        check.Busy = true;
        MacBrowserKit.Evaluate(_web, HeadlessBrowser.PrintExpression, json => OnPrint(check, json));
    }

    private void OnPrint(BrowserCheck check, string? json)
    {
        if (_jobs.Check != check || !Alive)
        {
            return;
        }

        if (!BrowserAnswers.TryReadPrint(json, out string state, out var print))
        {
            check.Busy = false;   // mid-navigation: the next look tries again
            return;
        }

        if (check.Watch.WantsRead(check.Clock.Elapsed, print))
        {
            ReadDom(check, () => AfterRead(check, state, print));
        }
        else
        {
            AfterRead(check, state, print);
        }
    }

    private void AfterRead(BrowserCheck check, string state, (int, uint) print)
    {
        bool ready = check.Watch.Observe(check.Clock.Elapsed, state, print);
        if (ready && check.Watch.Stale(print))
        {
            ReadDom(check, () => Conclude(check));
        }
        else if (ready)
        {
            Conclude(check);
        }
        else
        {
            check.Busy = false;
        }
    }

    private void ReadDom(BrowserCheck check, Action then)
    {
        MacBrowserKit.Evaluate(_web, HeadlessBrowser.PageExpression, json =>
        {
            if (_jobs.Check != check || !Alive)
            {
                return;
            }

            if (BrowserAnswers.TryReadPage(json, out string html))
            {
                if (html.Length > WebFetcher.MaxDownloadBytes)
                {
                    html = html[..WebFetcher.MaxDownloadBytes];
                    check.Truncated = true;
                }

                var url = Uri.TryCreate(Source, UriKind.Absolute, out var at) && WebFetcher.IsHttp(at) ? at : check.Request.Url;
                check.Watch.Read(check.Clock.Elapsed, html, url);
            }

            then();
        });
    }

    private void Conclude(BrowserCheck check)
    {
        switch (BrowserJobs.Outcome(check.Watch.Decide()))
        {
            case BotCheckEnd.Cleared:
                Complete(check);
                break;
            case BotCheckEnd.Refused:
                Finish(check, BrowserJobs.Refused(check, Source ?? check.Request.Url.AbsoluteUri), close: BrowserJobs.EndCloses(check));
                break;
            default:
                check.Busy = false;
                break;
        }
    }

    // Through: the PDF when one was asked for, then the cookies, then the answer.
    private void Complete(BrowserCheck check)
    {
        check.Finishing = true;
        string final = BrowserJobs.FinalUrl(Source, check.Request.Url);
        if (check.Request.PdfPath is { } path && !check.WasFile && _web != 0)
        {
            long token = ++_printToken;
            _printsWaiting[token] = ok =>
            {
                check.PdfWritten = ok && HasPdfHeader(path);
                Harvest(check, final);
            };
            if (MacBrowserKit.Print(_web, Window, _delegate, path, token))
            {
                return;
            }

            _printsWaiting.Remove(token);
        }

        Harvest(check, final);
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

    private void Harvest(BrowserCheck check, string final)
    {
        var hosts = BrowserJobs.CheckHosts(check.Request.Url, final);
        void Done(IReadOnlyList<BrowserCookie> cookies)
        {
            if (!check.Done.Task.IsCompleted)
            {
                Finish(check, BrowserJobs.Cleared(check, final, cookies, _identity), close: BrowserJobs.EndCloses(check));
            }
        }

        if (_store == 0)
        {
            Done([]);
            return;
        }

        MacBrowserKit.StoreCookies(_store, all => Done(all.Where(c => hosts.Any(c.AppliesTo)).ToList()));
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
