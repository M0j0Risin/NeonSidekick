using System.Diagnostics;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

// ── The bot check driven from a Web UI page (2026-10-18, the Mac Browser plan's Phase 5) ──

/// <summary>
/// The Mac window's side of the Web UI's live view, only while a check runs in it, input not once it is finishing. A picture is WebKit's
/// snapshot of what the view shows, <see cref="MacBrowserRules.SnapshotWidth"/> points wide (pixels ÷ the backing scale, Phase 0: 225 points
/// drew 450 pixels), as a JPEG at <see cref="BrowserRemote.JpegQuality"/>. Input goes straight to the web view's own responder methods
/// (<c>mouseDown:</c>, <c>mouseUp:</c>, <c>mouseMoved:</c>, <c>keyDown:</c>, <c>insertText:</c>), never <c>[window sendEvent:]</c> (Phase 0:
/// a window that is not key in an app that is not active drops those) and never a CGEvent posted to the system (it would move the real
/// pointer and need the Accessibility permission, pass 30). Phase 0: such input arrives with <c>isTrusted</c> true, and Cloudflare's
/// forced-interactive Turnstile passed by it, the window covered or not. A scroll is the app's world scrolling the element under the place.
/// </summary>
internal sealed partial class MacBrowserWindow
{
    private const ulong InMouseDown = 1;
    private const ulong InMouseUp = 2;
    private const ulong InMouseMoved = 5;
    private const ulong InMouseDragged = 6;
    private const ulong KeyDownType = 10;
    private const ulong KeyUpType = 11;

    /// <summary>How old a picture still on its way may be before a new ask stops waiting for it (Windows' rule).</summary>
    private static readonly TimeSpan FrameStale = TimeSpan.FromSeconds(5);

    private readonly MainTimer _release;
    private readonly List<TaskCompletionSource<bool>> _pendingInputs = [];
    private readonly Lock _frameGate = new();
    private TaskCompletionSource<BrowserFrame?>? _frame;
    private long _frameAsked;

    /// <summary>See <see cref="IBrowserWindow.FrameAsync"/>: one picture at a time, a stale one given up as none.</summary>
    public Task<BrowserFrame?> FrameAsync(int maxWidth)
    {
        TaskCompletionSource<BrowserFrame?> done;
        lock (_frameGate)
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
        lock (_pendingInputs)
        {
            _pendingInputs.Add(done);
        }

        if (!Post(() => SettleInput(done, SendInput(input))))
        {
            SettleInput(done, false);
        }

        return done.Task;
    }

    private void BeginFrame(TaskCompletionSource<BrowserFrame?> done, int maxWidth)
    {
        if (_jobs.Check is null || _web == 0)
        {
            done.TrySetResult(null);
            return;
        }

        var bounds = SendRect(_web, Sel("bounds"));
        double width = MacBrowserRules.SnapshotWidth(maxWidth, bounds.Width, Scale);
        MacBrowserKit.Snapshot(_web, width, BrowserRemote.JpegQuality, (jpeg, _) =>
            done.TrySetResult(jpeg is { Length: > 0 } picture ? new BrowserFrame(picture, BrowserRemote.TagOf(picture)) : null));
    }

    private bool SendInput(BrowserInput input)
    {
        if (_jobs.Check is not { Finishing: false } check || _web == 0 || BrowserRemote.Check(input) is not null)
        {
            return false;
        }

        var bounds = SendRect(_web, Sel("bounds"));
        var (x, y) = MacBrowserRules.PointIn(input.X, input.Y, bounds.Width, bounds.Height);
        switch (input.Kind)
        {
            case BrowserInputKind.Tap:
                Mouse(InMouseMoved, x, y);
                Mouse(InMouseDown, x, y);
                Mouse(InMouseUp, x, y);
                return true;
            case BrowserInputKind.Down:
                Mouse(InMouseMoved, x, y);
                Mouse(InMouseDown, x, y);
                check.Held = (x, y);
                _release.Start(MacBrowserWindows.HoldLimit);
                return true;
            case BrowserInputKind.Up:
                if (check.Held is { } held && (held.X != x || held.Y != y))
                {
                    Mouse(InMouseDragged, x, y);
                }

                Mouse(InMouseUp, x, y);
                check.Held = null;
                _release.Stop();
                return true;
            case BrowserInputKind.Scroll:
                MacBrowserKit.Run(_web, ScrollScript(input));
                return true;
            case BrowserInputKind.Text:
                SendVoid(_web, Sel("insertText:"), NSString(input.Text ?? ""));
                return true;
            case BrowserInputKind.Key when MacBrowserRules.KeyOf(input.Key) is { } key:
                Key(KeyDownType, key.Code, key.Characters);
                Key(KeyUpType, key.Code, key.Characters);
                return true;
            default:
                return false;
        }
    }

    // The element under the place scrolled, or the page when none of them scrolls: in the app's world, by fractions of the page's view.
    private static string ScrollScript(BrowserInput input)
    {
        string Number(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        double delta = -MacBrowserRules.WheelDelta(input.DeltaY);
        return "(() => { const x = " + Number(Math.Clamp(input.X, 0, 1)) + " * innerWidth, y = " + Number(Math.Clamp(input.Y, 0, 1)) + " * innerHeight, dy = " + Number(delta) + ";"
            + " let e = document.elementFromPoint(x, y); while (e && e !== document.documentElement && e !== document.body) {"
            + " const s = getComputedStyle(e); if (/(auto|scroll)/.test(s.overflowY) && e.scrollHeight > e.clientHeight) { e.scrollBy(0, dy); return; } e = e.parentElement; }"
            + " window.scrollBy(0, dy); })();";
    }

    // A mouse event at a point of the view (top-left origin), sent to the view itself.
    private void Mouse(ulong type, double x, double y)
    {
        var inWindow = SendPointConvert(_web, Sel("convertPoint:toView:"), new CGPoint(x, y), 0);
        long number = SendLong(Window, Sel("windowNumber"));
        double time = SendDouble(Send(Class("NSProcessInfo"), Sel("processInfo")), Sel("systemUptime"));
        nint e = MacBrowserKit.SendMouse(Class("NSEvent"), Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
            type, inWindow, 0, time, number, 0, 0, type is InMouseDown or InMouseUp ? 1 : 0, type is InMouseDown or InMouseDragged ? 1f : 0f);
        if (e == 0)
        {
            return;
        }

        string selector = type switch
        {
            InMouseDown => "mouseDown:",
            InMouseUp => "mouseUp:",
            InMouseDragged => "mouseDragged:",
            _ => "mouseMoved:",
        };
        SendVoid(_web, Sel(selector), e);
    }

    private void Key(ulong type, ushort code, string characters)
    {
        long number = SendLong(Window, Sel("windowNumber"));
        double time = SendDouble(Send(Class("NSProcessInfo"), Sel("processInfo")), Sel("systemUptime"));
        nint text = NSString(characters);
        nint e = MacBrowserKit.SendKey(Class("NSEvent"), Sel("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
            type, default, 0, time, number, 0, text, text, 0, code);
        if (e != 0)
        {
            SendVoid(_web, Sel(type == KeyDownType ? "keyDown:" : "keyUp:"), e);
        }
    }

    // A held button let go: the page went away holding it, the hold ran past the limit, or the check is ending.
    private void ReleaseHeld(BrowserCheck? check)
    {
        _release.Stop();
        if (check?.Held is not { } held)
        {
            return;
        }

        check.Held = null;
        if (_web != 0)
        {
            Mouse(InMouseUp, held.X, held.Y);
            DiagnosticLog.Debug("Browser", "The held button was released.");
        }
    }

    private void SettleInput(TaskCompletionSource<bool> done, bool ok)
    {
        lock (_pendingInputs)
        {
            _pendingInputs.Remove(done);
        }

        done.TrySetResult(ok);
    }

    private void EndRemote()
    {
        TaskCompletionSource<bool>[] inputs;
        TaskCompletionSource<BrowserFrame?>? frame;
        lock (_pendingInputs)
        {
            inputs = [.. _pendingInputs];
            _pendingInputs.Clear();
        }

        lock (_frameGate)
        {
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
