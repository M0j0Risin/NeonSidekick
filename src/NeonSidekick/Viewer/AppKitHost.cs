using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The main thread AppKit needs, on a Mac (2026-10-07, Stage 2: the app's own windows over AppKit; the user's pick of the
/// design, (a) over a <c>--window-host</c> child process). AppKit runs only on the process's main thread, and the terminal UI
/// held it; but an async <c>Main</c> only parks that thread on the run's task, every <c>await</c> after it running on the pool.
/// So <see cref="Run"/> starts the run on the pool and keeps the main thread for the windows: a plain queue until the first
/// window is wanted, then <c>[NSApp run]</c> — AppKit, and the window server with it, never touched by a run that opens no
/// window (over SSH, say, where there is none). Windows of the app's own post their work here (<see cref="Post"/>, <see
/// cref="Invoke{T}"/>): <c>dispatch_async_f</c> on the main queue, never a block. The run's end closes the windows still open
/// (<see cref="AtEnd"/>) and stops the loop, and <c>Main</c> goes on as it would have: the settings flushed, the exit code
/// returned.
///
/// <para>The new risk is reentrancy: on Windows each window has a thread of its own, so a window that opens another, or waits
/// for one to start, is safe; here they share one thread. So a wait from the main thread runs its work inline, and nothing
/// waits on the main thread from the main thread. AppKit's own quit (a logout's, an Apple event's) would call <c>exit()</c> past
/// the flush and the terminal's restore: the app's delegate cancels it and asks the app to end as Ctrl+C would
/// (<see cref="QuitRequested"/>). The activation policy is Accessory (the user's call, measured that day on macOS 15.7: no Dock
/// icon and no menu bar, and a window activated with <c>activateIgnoringOtherApps:</c> still takes the keyboard, where the
/// cooperative <c>activate</c> of macOS 14 is refused under either policy).</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class AppKitHost
{
    private const int Off = 0;
    private const int Enabled = 1;
    private const int Running = 2;
    private const int Ended = 3;

    /// <summary>How long a caller off the main thread waits for its work (a window's start) before it gives up.</summary>
    public static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(10);

    private static readonly BlockingCollection<Action?> s_early = [];
    private static readonly List<Action> s_atEnd = [];
    private static readonly Lock s_gate = new();
    private static volatile int s_state;
    private static volatile bool s_appKit;
    private static nint s_app;
    private static nint s_delegate;

    /// <summary>
    /// Asked by the app to end as Ctrl+C would (<c>Program</c>: the shutdown token cancelled), when AppKit's own quit came — a
    /// logout, an Apple event. On the main thread; it must not block.
    /// </summary>
    public static Action? QuitRequested { get; set; }

    /// <summary>Whether <see cref="Enable"/> found the main thread and a window server: the app's windows are offered.</summary>
    public static bool IsEnabled => s_state is Enabled or Running;

    /// <summary>Whether the loop runs: work posted now is done.</summary>
    public static bool IsRunning => s_state == Running;

    /// <summary>Whether the calling thread is the process's main thread.</summary>
    public static bool OnMainThread => pthread_main_np() == 1;

    /// <summary>Whether AppKit has started (the first window was wanted).</summary>
    public static bool AppKitStarted => s_appKit;

    /// <summary>
    /// Called by <c>Program</c> on the main thread before the app is made: true (and the windows offered) when this is the
    /// process's main thread and a window server is there to draw on. Nothing of AppKit is loaded yet.
    /// </summary>
    public static bool Enable()
    {
        if (s_state != Off)
        {
            return IsEnabled;
        }

        if (!OnMainThread)
        {
            DiagnosticLog.Warn("Viewer", "Not on the process's main thread: the app's own windows are not offered.");
            return false;
        }

        if (!HasWindowServer())
        {
            DiagnosticLog.Info("Viewer", "No window server (no desktop session): the app's own windows are not offered.");
            return false;
        }

        s_state = Enabled;
        return true;
    }

    /// <summary>
    /// The run on the pool and the windows on this, the main, thread until it ends; its exit code, or its exception thrown again
    /// here as an awaited one would be.
    /// </summary>
    public static int Run(Func<Task<int>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (s_state != Enabled || !OnMainThread)
        {
            throw new InvalidOperationException("the AppKit host runs once, on the main thread, after Enable");
        }

        s_state = Running;
        var task = Task.Run(body);
        _ = task.ContinueWith(_ => Finish(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        // Before AppKit: the queue alone. The first window's work starts AppKit and goes on in NSApp's loop; null is the end
        // with no window ever opened.
        if (s_early.Take() is { } first)
        {
            StartAppKit(first);
            SendVoid(s_app, Sel("run"));   // until the end's stop
        }

        s_state = Ended;
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Something to do as the run ends, on the main thread while the loop still runs (an open window closed, its place kept).</summary>
    public static void AtEnd(Action work)
    {
        lock (s_gate)
        {
            s_atEnd.Add(work);
        }
    }

    /// <summary>
    /// <paramref name="work"/> on the main thread later, from any thread — from the main thread too, after what it is doing (a
    /// window's own message, as <c>PostMessage</c> is on Windows). False, and nothing done, when the loop is not running.
    /// </summary>
    public static bool Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (s_gate)
        {
            if (s_state != Running)
            {
                return false;
            }

            if (!s_appKit)
            {
                s_early.Add(work);
                return true;
            }
        }

        Dispatch(work);
        return true;
    }

    /// <summary>
    /// <paramref name="work"/>'s answer from the main thread: run inline when called there (a window opening another), else posted
    /// and waited for up to <see cref="InvokeTimeout"/>. False when the loop is not running or the wait ran out.
    /// </summary>
    public static bool Invoke<T>(Func<T> work, out T result)
    {
        ArgumentNullException.ThrowIfNull(work);
        result = default!;
        if (s_state != Running)
        {
            return false;
        }

        if (OnMainThread && s_appKit)
        {
            result = work();
            return true;
        }

        T answer = default!;
        Exception? failure = null;
        // Not disposed: after a timeout the work may still finish and set it (left to the collector, as a late answer is dropped).
        var done = new ManualResetEventSlim();
        bool posted = Post(() =>
        {
            try
            {
                answer = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        if (!posted || !done.Wait(InvokeTimeout))
        {
            return false;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfoThrow(failure);
        }

        result = answer;
        return true;
    }

    private static void ExceptionDispatchInfoThrow(Exception failure) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

    // The run's end, on the pool: the windows closed and the loop stopped on the main thread.
    private static void Finish()
    {
        lock (s_gate)
        {
            if (!s_appKit)
            {
                s_state = Ended;
                s_early.Add(null);
                return;
            }
        }

        DispatchEnd();
    }

    // The end's work on the main thread, after everything posted before it: the windows closed, the loop stopped.
    private static void DispatchEnd()
    {
        Dispatch(() =>
        {
            Action[] closers;
            lock (s_gate)
            {
                closers = [.. s_atEnd];
            }

            foreach (var close in closers)
            {
                try
                {
                    close();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn("Viewer", "A window did not close cleanly at the end: " + ex.Message);
                }
            }

            lock (s_gate)
            {
                s_state = Ended;   // nothing posted from here on
            }

            // stop: ends run after the event in hand; this is a dispatch, not an event, so one is posted to be that event.
            SendVoid(s_app, Sel("stop:"), 0);
            nint wake = SendOtherEvent(Class("NSEvent"), Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
                EventApplicationDefined, default, 0, 0, 0, 0, 0, 0, 0);
            SendVoidNintBool(s_app, Sel("postEvent:atStart:"), wake, 1);
        });
    }

    // AppKit loaded and NSApp made (Accessory, the delegate set), the first window's work handed to its loop, and the early
    // queue's rest after it — under the gate, so nothing posted meanwhile is lost or run out of order.
    private static void StartAppKit(Action first)
    {
        nint pool = objc_autoreleasePoolPush();
        try
        {
            NativeLibrary.Load(AppKitPath);
            NativeLibrary.Load(QuartzCorePath);
            s_app = Send(Class("NSApplication"), Sel("sharedApplication"));
            SendBoolLong(s_app, Sel("setActivationPolicy:"), ActivationAccessory);
            s_delegate = AppKitClasses.NewAppDelegate();
            SendVoid(s_app, Sel("setDelegate:"), s_delegate);
            DiagnosticLog.Info("Viewer", "AppKit started (accessory: no Dock icon, no menu bar).");
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }

        lock (s_gate)
        {
            s_appKit = true;
            Dispatch(first);
            bool ended = false;
            while (s_early.TryTake(out var work))
            {
                if (work is null)
                {
                    ended = true;   // the run ended while AppKit was starting: its end still comes, after the window's work
                    continue;
                }

                Dispatch(work);
            }

            if (ended)
            {
                DispatchEnd();
            }
        }
    }

    /// <summary>AppKit's own quit asked for (the app delegate): the app asked to end its own way, AppKit's cancelled.</summary>
    internal static void OnTerminateAsked()
    {
        DiagnosticLog.Info("Viewer", "macOS asked the app to quit: ending as Ctrl+C would.");
        QuitRequested?.Invoke();
    }

    private static void Dispatch(Action work)
    {
        var handle = GCHandle.Alloc(work);
        dispatch_async_f(MainQueue, GCHandle.ToIntPtr(handle), &Trampoline);
    }

    [UnmanagedCallersOnly]
    private static void Trampoline(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var work = (Action)handle.Target!;
        handle.Free();
        nint pool = objc_autoreleasePoolPush();
        try
        {
            work();
        }
        catch (Exception ex)
        {
            // Nothing may cross back into libdispatch: an exception here would take the process down.
            DiagnosticLog.Error("Viewer", "A window's work failed on the main thread.", ex);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }
}

/// <summary>
/// A one-shot timer on the main thread (2026-10-07, the Mac windows' <c>SetTimer</c>): <c>dispatch_after_f</c> on the main queue,
/// a start replacing the one before (its firing dropped by version) and a stop dropping it. Main thread only.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MainTimer(Action fired)
{
    private readonly Action _fired = fired;
    private int _version;

    /// <summary>Fires once after <paramref name="delay"/>; an earlier start's firing is dropped.</summary>
    public void Start(TimeSpan delay)
    {
        int version = ++_version;
        var handle = GCHandle.Alloc((this, version));
        dispatch_after_f(dispatch_time(0, (long)delay.TotalMilliseconds * 1_000_000), MainQueue, GCHandle.ToIntPtr(handle), &Fire);
    }

    /// <summary>The pending firing dropped.</summary>
    public void Stop() => _version++;

    [UnmanagedCallersOnly]
    private static void Fire(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var (timer, version) = ((MainTimer, int))handle.Target!;
        handle.Free();
        if (version != timer._version || !AppKitHost.IsRunning)
        {
            return;
        }

        timer._version++;
        nint pool = objc_autoreleasePoolPush();
        try
        {
            timer._fired();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Viewer", "A window's timer failed on the main thread.", ex);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }
}
