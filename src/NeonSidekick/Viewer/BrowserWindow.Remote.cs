using System.Diagnostics;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

// ── The bot check driven from a Web UI page (2026-10-17) ──────────────────

/// <summary>
/// The window's side of the Web UI's live view of a bot check (2026-10-17, the user's ask): a picture of what it shows and the page's input
/// sent into it, both over the DevTools protocol (<see cref="WebViewHost.CallDevTools"/>), both only while a check runs in it — never a
/// <c>/browser</c> visit — and input not once the check is finishing. The pure half is <see cref="BrowserRemote"/>.
/// </summary>
/// <remarks>
/// Step 0's spike (2026-10-17, WebView2 on this PC at 100% scale) on local pages:
/// <list type="bullet">
/// <item>The picture held Cloudflare's cross-origin Turnstile widget. A DevTools click on its box (the dummy sitekey that forces the
/// interactive challenge) passed it, and the check ended Cleared with the page's cookie handed over.</item>
/// <item>A click dispatched into a cross-origin frame reported <c>screenX</c> 488 against <c>clientX</c> 148. So the 2025 tell, a frame's
/// click measured from the frame, is fixed in this runtime, and DevTools input was kept; posted mouse messages were never needed.</item>
/// <item>A 2.2 s press and release, inserted text, Backspace and Enter all arrived trusted.</item>
/// <item>A clip at the visual viewport's <c>pageX</c>/<c>pageY</c> showed the scrolled view, and a tap at the picture's middle hit what
/// the picture showed there.</item>
/// <item>A covered window drew as ever, and so did one on a locked PC (the phone run under <c>auto</c>, later that day). A minimized one did not (<see cref="Unminimize"/>).</item>
/// </list>
/// </remarks>
internal sealed unsafe partial class BrowserWindowThread
{
    private static readonly IntPtr ReleaseTimer = 7;

    /// <summary>How long a button the page pressed stays down without its release before the window lets it go: press-and-hold checks want up to about 10 s.</summary>
    public static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(30);

    /// <summary>How old a picture still on its way may be before a new ask stops waiting for it (a view that stopped drawing never answers).</summary>
    private static readonly TimeSpan FrameStale = TimeSpan.FromSeconds(5);

    private readonly List<TaskCompletionSource<bool>> _pendingInputs = [];
    private TaskCompletionSource<BrowserFrame?>? _frame;
    private long _frameAsked;

    /// <summary>
    /// See <see cref="IBrowserWindow.FrameAsync"/>: one picture at a time — an ask while one is on its way waits for that one, unless it is
    /// older than <see cref="FrameStale"/>, which is then given up as none.
    /// </summary>
    public Task<BrowserFrame?> FrameAsync(int maxWidth)
    {
        TaskCompletionSource<BrowserFrame?> done;
        lock (_gate)
        {
            if (_frame is { } going && !going.Task.IsCompleted)
            {
                if (Stopwatch.GetElapsedTime(_frameAsked) < FrameStale)
                {
                    return going.Task;
                }

                going.TrySetResult(null);
            }

            done = new TaskCompletionSource<BrowserFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _frame = done;
            _frameAsked = Stopwatch.GetTimestamp();
        }

        if (!Post(() => BeginFrame(done, maxWidth)))
        {
            done.TrySetResult(null);
        }

        return done.Task;
    }

    /// <summary>See <see cref="IBrowserWindow.InputAsync"/>.</summary>
    public Task<bool> InputAsync(BrowserInput input)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingInputs.Add(done);
        }

        if (!Post(() => BeginInput(input, done)))
        {
            SettleInput(done, false);
        }

        return done.Task;
    }

    private void BeginFrame(TaskCompletionSource<BrowserFrame?> done, int maxWidth)
    {
        if (_job is not { } job || _host is not { IsCreated: true } host)
        {
            done.TrySetResult(null);
            return;
        }

        Unminimize();

        ReadViewport(job, host, viewport =>
        {
            if (viewport is null)
            {
                done.TrySetResult(null);
                return;
            }

            string parameters = BrowserRemote.ScreenshotParameters(viewport, maxWidth, job.PixelsPerDip);
            double scale = BrowserRemote.ScaleFor(viewport, maxWidth, job.PixelsPerDip);
            bool sent = host.CallDevTools("Page.captureScreenshot", parameters, (hr, json) =>
            {
                if (hr < 0 || !BrowserAnswers.TryReadScreenshot(json, out byte[] jpeg))
                {
                    done.TrySetResult(null);
                    return;
                }

                if (BrowserRemote.JpegWidth(jpeg) is { } width && BrowserRemote.PixelsPerDip(width, viewport, scale) is { } perDip)
                {
                    job.PixelsPerDip = perDip;
                }

                done.TrySetResult(new BrowserFrame(jpeg, BrowserRemote.TagOf(jpeg)));
            });
            if (!sent)
            {
                done.TrySetResult(null);
            }
        });
    }

    private void BeginInput(BrowserInput input, TaskCompletionSource<bool> done)
    {
        if (_job is not { Finishing: false } job || _host is not { IsCreated: true } host || BrowserRemote.Check(input) is not null)
        {
            SettleInput(done, false);
            return;
        }

        Unminimize();

        ReadViewport(job, host, viewport =>
        {
            if (viewport is null || _job != job || job.Finishing)
            {
                SettleInput(done, false);
                return;
            }

            var commands = BrowserRemote.Commands(input, viewport);
            Send(host, commands, 0, ok =>
            {
                if (ok && (_job != job || job.Finishing))
                {
                    // The check ended while the commands were on their way (2026-10-19, the code review): Finish has let go of what it
                    // knew was held, so a press answered after it is let go here, or nobody would (the release timer reads the live job).
                    if (input.Kind == BrowserInputKind.Down)
                    {
                        SendRelease(host, Math.Round(input.X * viewport.Width, 2), Math.Round(input.Y * viewport.Height, 2));
                    }

                    ok = false;
                }
                else if (ok)
                {
                    Hold(job, input, viewport);
                }

                SettleInput(done, ok);
            });
        });
    }

    // A minimized window draws nothing: step 0's spike (2026-10-17) found its DevTools calls hanging about 5 s or answering late, and the
    // picture stale, where one restored without activation (no keyboard taken from whoever is at the PC) answered at once. Covered by
    // other windows it drew as ever.
    private void Unminimize()
    {
        if (IsIconic(_hwnd))
        {
            ShowWindow(_hwnd, SwShowNoActivate);
        }
    }

    // The visible part read now (the page scrolls between looks), kept on the job; the last one read when this one can't be had.
    private static void ReadViewport(BrowserCheck job, WebViewHost host, Action<BrowserViewport?> then)
    {
        bool sent = host.CallDevTools("Page.getLayoutMetrics", "{}", (hr, json) =>
        {
            if (hr >= 0 && BrowserAnswers.TryReadViewport(json, out var viewport))
            {
                job.Viewport = viewport;
            }

            then(job.Viewport);
        });
        if (!sent)
        {
            then(job.Viewport);
        }
    }

    // The commands one after another, each sent once the last is answered: a press never overtakes its move.
    private static void Send(WebViewHost host, IReadOnlyList<(string Method, string Json)> commands, int index, Action<bool> then)
    {
        if (index == commands.Count)
        {
            then(true);
            return;
        }

        var (method, json) = commands[index];
        if (!host.CallDevTools(method, json, (hr, _) =>
        {
            if (hr < 0)
            {
                then(false);
                return;
            }

            Send(host, commands, index + 1, then);
        }))
        {
            then(false);
        }
    }

    // A press kept until its release, or HoldLimit, or the check's end, whichever comes first.
    private void Hold(BrowserCheck job, BrowserInput input, BrowserViewport viewport)
    {
        switch (input.Kind)
        {
            case BrowserInputKind.Down:
                job.Held = (Math.Round(input.X * viewport.Width, 2), Math.Round(input.Y * viewport.Height, 2));
                SetTimer(_hwnd, ReleaseTimer, (uint)HoldLimit.TotalMilliseconds, IntPtr.Zero);
                break;
            case BrowserInputKind.Up or BrowserInputKind.Tap:
                job.Held = null;
                KillTimer(_hwnd, ReleaseTimer);
                break;
        }
    }

    // A held button let go: the page went away holding it, the hold ran past HoldLimit, or the check is ending.
    private void ReleaseHeld(BrowserCheck? job)
    {
        KillTimer(_hwnd, ReleaseTimer);
        if (job?.Held is not { } held)
        {
            return;
        }

        job.Held = null;
        if (_host is { IsCreated: true } host)
        {
            SendRelease(host, held.X, held.Y);
        }
    }

    private static void SendRelease(WebViewHost host, double x, double y)
    {
        var (method, json) = BrowserRemote.Release(x, y);
        if (!host.CallDevTools(method, json, (_, _) => { }))
        {
            DiagnosticLog.Debug("Browser", "The held button could not be released.");
        }
    }

    private void SettleInput(TaskCompletionSource<bool> done, bool ok)
    {
        lock (_gate)
        {
            _pendingInputs.Remove(done);
        }

        done.TrySetResult(ok);
    }

    // The window gone: the picture on its way is none, every input waiting is refused.
    private void EndRemote()
    {
        TaskCompletionSource<bool>[] inputs;
        TaskCompletionSource<BrowserFrame?>? frame;
        lock (_gate)
        {
            inputs = [.. _pendingInputs];
            _pendingInputs.Clear();
            frame = _frame;
            _frame = null;
        }

        frame?.TrySetResult(null);
        foreach (var done in inputs)
        {
            done.TrySetResult(false);
        }
    }
}
