using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using NeonSidekick.Shell;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The log window and the process window on a Mac (2026-10-07, Stage 2 phase 3) — <see cref="LogWindow"/>'s and
/// <see cref="ProcessWindow"/>'s calls land here when <see cref="OperatingSystem.IsMacOS"/>. One of each per process, as on Windows:
/// a second <c>/log</c> brings the log forward, a <c>/process</c> on another id swaps the process window onto it in place.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacLineWindows
{
    private static volatile MacLineWindow? s_log;       // set on the main thread; read elsewhere only to choose a post over a wait
    private static volatile MacLineWindow? s_process;
    private static ProcessSession? s_session;           // main thread
    private static int s_registered;

    /// <summary>The log window on <paramref name="buffer"/>, or the open one brought forward. Throws when it could not be made.</summary>
    public static void ShowLog(DiagnosticBuffer buffer)
    {
        RegisterEnd();
        if (s_log is { Alive: true } && AppKitHost.Post(() => ShowLogOnMain(buffer)))
        {
            return;
        }

        if (!AppKitHost.Invoke(() => ShowLogOnMain(buffer), out _))
        {
            throw new InvalidOperationException("the window did not start");
        }
    }

    private static bool ShowLogOnMain(DiagnosticBuffer buffer)
    {
        if (s_log is { Alive: true } open)
        {
            open.Raise();
            return true;
        }

        var window = new MacLineWindow(new DiagnosticFeed(buffer), "Log window", LogWindow.Position, LogWindow.Placed);
        window.Start();
        s_log = window;
        return true;
    }

    /// <summary>The process window on <paramref name="session"/>: brought forward, swapped onto it, or opened.</summary>
    public static void ShowProcess(ProcessSession session, Action<ProcessSession> stop)
    {
        RegisterEnd();
        if (s_process is { Alive: true } && AppKitHost.Post(() => ShowProcessOnMain(session, stop)))
        {
            return;
        }

        if (!AppKitHost.Invoke(() => ShowProcessOnMain(session, stop), out _))
        {
            throw new InvalidOperationException("the window did not start");
        }
    }

    private static bool ShowProcessOnMain(ProcessSession session, Action<ProcessSession> stop)
    {
        if (s_process is { Alive: true } open)
        {
            if (!ReferenceEquals(s_session, session))
            {
                open.Swap(new ProcessFeed(session, stop, TimeProvider.System));
                s_session = session;
            }

            open.Raise();
            return true;
        }

        var window = new MacLineWindow(new ProcessFeed(session, stop, TimeProvider.System), "Process window", ProcessWindow.Position, ProcessWindow.Placed);
        window.Start();
        s_process = window;
        s_session = session;
        return true;
    }

    /// <summary>The log window closed; true when one was open.</summary>
    public static bool CloseLog() => AppKitHost.IsRunning && AppKitHost.Invoke(CloseLogOnMain, out bool closed) && closed;

    /// <summary>The process window closed; true when one was open.</summary>
    public static bool CloseProcess() => AppKitHost.IsRunning && AppKitHost.Invoke(CloseProcessOnMain, out bool closed) && closed;

    private static bool CloseLogOnMain()
    {
        var open = s_log;
        s_log = null;
        bool alive = open is { Alive: true };
        open?.Close();
        return alive;
    }

    private static bool CloseProcessOnMain()
    {
        var open = s_process;
        s_process = null;
        s_session = null;
        bool alive = open is { Alive: true };
        open?.Close();
        return alive;
    }

    /// <summary>
    /// <c>viewer:log-window</c> on a Mac: a hidden window with a scrolling text view, two coloured lines appended through a feed and
    /// drawn into a bitmap, then let go — on the main thread through the host. Nothing is shown.
    /// </summary>
    public static (bool Ok, string Detail) Probe()
    {
        if (!AppKitHost.IsEnabled)
        {
            return (true, HasWindowServer() ? "skipped: no AppKit host here (the app's main thread runs it)" : "skipped: no window server");
        }

        if (!AppKitHost.Invoke(MacLineWindow.Probe, out (bool Ok, string Detail) result))
        {
            return (false, "the main thread did not answer");
        }

        return result;
    }

    private static void RegisterEnd()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
        {
            AppKitHost.AtEnd(() =>
            {
                CloseLogOnMain();
                CloseProcessOnMain();
            });
        }
    }
}

/// <summary>
/// One Mac line window over an <see cref="ILineFeed"/> (2026-10-07; <see cref="LogWindowThread"/>'s twin). The user's pick: an
/// NSTextView in a scroll view, so the scrolling, the selection and ⌘C and ⌘A are AppKit's own, with every line coloured by level
/// from the theme (<see cref="LogViewStyle"/>) and drawn in the system's monospaced font. It follows the newest line while the view
/// is at the bottom and holds still once scrolled away (the title saying so, as on Windows); ⌘↓, Ctrl+End or Ctrl+E go back to the
/// bottom and ⌘↑ or Ctrl+Home to the top. Lines the feed no longer holds are cut from the top, so the text is the feed's. A key the
/// window has no use for goes to the feed first (the process window's Ctrl+K twice). Main thread only; excluded from coverage with
/// the AppKit layer.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacLineWindow : AppKitWindow
{
    /// <summary>The window's size in points when it opens (the Windows one's at 96 DPI).</summary>
    public const double DefaultWidth = 960;
    public const double DefaultHeight = 600;

    /// <summary>The text's size in points (Windows' 10-point Consolas is about this on a Mac's screen).</summary>
    public const double FontPoints = 12;

    /// <summary>How near the bottom (points) still counts as at it: following.</summary>
    private const double BottomSlack = 4;

    private readonly string _name;
    private readonly Func<(int X, int Y)?>? _position;
    private readonly Action<int, int>? _placed;
    private readonly List<LogLine> _scratch = [];
    private readonly Queue<(long Seq, int Length)> _shown = new();
    private ILineFeed _feed;
    private nint _scroll;
    private nint _text;
    private long _nextSeq;
    private int _pullPosted;
    private bool _following = true;
    private string? _title;
    private LogViewStyle _style = LogViewStyle.Black;
    private bool _empty = true;
    private bool _styled;

    public MacLineWindow(ILineFeed feed, string name, Func<(int X, int Y)?>? position, Action<int, int>? placed)
    {
        _feed = feed;
        _name = name;
        _position = position;
        _placed = placed;
    }

    protected override Action<int, int>? Placed => _placed;

    /// <summary>The window made, filled with the feed's lines, at the bottom and following, and brought forward.</summary>
    public void Start(bool show = true)
    {
        Create(DefaultWidth, DefaultHeight, _position);
        MakeText();
        ApplyStyle();
        _feed.Appended += OnAppended;
        Pull();
        if (show)
        {
            ShowWindow(activate: true);
            DiagnosticLog.Info("Viewer", $"{_name} opened.");
        }
    }

    /// <summary>Brought forward with the keyboard.</summary>
    public void Raise()
    {
        ApplyStyle();
        ShowWindow(activate: true);
    }

    /// <summary>Shown over <paramref name="feed"/> in place of its own (the process window switched to another process), at the bottom and following.</summary>
    public void Swap(ILineFeed feed)
    {
        _feed.Appended -= OnAppended;
        _feed.Dispose();
        _feed = feed;
        _shown.Clear();
        _nextSeq = 0;
        SendVoidRange(Storage, Sel("deleteCharactersInRange:"), new NSRange(0, (nuint)SendLong(Storage, Sel("length"))));
        _empty = true;
        _following = true;
        _feed.Appended += OnAppended;
        Pull();
    }

    protected override void OnClosed()
    {
        _feed.Appended -= OnAppended;
        _feed.Dispose();
        DiagnosticLog.Info("Viewer", $"{_name} closed.");
    }

    // ---- keys ----

    internal override bool KeyDown(MacKeyEvent key)
    {
        var action = MacKeys.LogAction(key.KeyCode, key.Flags, FullScreen);
        switch (action)
        {
            case LogViewAction.Close:
                Close();
                return true;
            case LogViewAction.ToggleFullScreen:
                SetFullScreen(!FullScreen);
                return true;
            case LogViewAction.LeaveFullScreen:
                SetFullScreen(false);
                return true;
            case LogViewAction.Copy:
                SendVoid(_text, Sel("copy:"), 0);
                return true;
            case LogViewAction.SelectAll:
                SendVoid(_text, Sel("selectAll:"), 0);
                return true;
            case LogViewAction.Top:
                SendVoid(_text, Sel("scrollToBeginningOfDocument:"), 0);
                return true;
            case LogViewAction.Bottom:
                SendVoid(_text, Sel("scrollToEndOfDocument:"), 0);
                return true;
            case LogViewAction.LineUp:
                SendVoid(_text, Sel("scrollLineUp:"), 0);
                return true;
            case LogViewAction.LineDown:
                SendVoid(_text, Sel("scrollLineDown:"), 0);
                return true;
            case LogViewAction.PageUp:
                SendVoid(_text, Sel("scrollPageUp:"), 0);
                return true;
            case LogViewAction.PageDown:
                SendVoid(_text, Sel("scrollPageDown:"), 0);
                return true;
        }

        // The feed's own key (the process window's Ctrl+K twice), then nothing: a key typed into read-only text would only beep.
        // ⌘ chords go on to AppKit (⌘` between windows, ⌘H).
        int vk = MacKeys.ToVirtualKey(key.KeyCode);
        bool control = (key.Flags & MacKeys.ControlFlag) != 0;
        if (_feed.Key(vk, control, key.Repeat))
        {
            UpdateTitle();
            return true;
        }

        return (key.Flags & MacKeys.CommandFlag) == 0;
    }

    internal override void BecameKey() => ApplyStyle();

    internal override void Scrolled()
    {
        bool following = AtBottom();
        if (following != _following)
        {
            _following = following;
            UpdateTitle();
        }
    }

    // ---- the lines ----

    // From the feed's thread: one pull posted at a time (a burst of lines is one read).
    private void OnAppended()
    {
        if (Interlocked.Exchange(ref _pullPosted, 1) == 0 && !AppKitHost.Post(Pull))
        {
            Volatile.Write(ref _pullPosted, 0);
        }
    }

    // The feed's new lines appended, coloured by level, and the lines it dropped cut from the top; scrolled to the end while following.
    private void Pull()
    {
        Volatile.Write(ref _pullPosted, 0);
        if (!Alive)
        {
            return;
        }

        _scratch.Clear();
        long firstHeld = _feed.CopySince(_nextSeq, _scratch);
        bool following = _following;
        nint storage = Storage;
        SendVoid(storage, Sel("beginEditing"));
        try
        {
            if (_empty && _scratch.Count > 0)
            {
                SendVoidRange(storage, Sel("deleteCharactersInRange:"), new NSRange(0, (nuint)SendLong(storage, Sel("length"))));
                _empty = false;
            }

            int cut = 0;
            while (_shown.Count > 0 && _shown.Peek().Seq < firstHeld)
            {
                cut += _shown.Dequeue().Length;
            }

            if (cut > 0)
            {
                SendVoidRange(storage, Sel("deleteCharactersInRange:"), new NSRange(0, (nuint)cut));
            }

            // One attributed run per level in a row: a burst of a thousand lines is a handful of appends.
            int i = 0;
            while (i < _scratch.Count)
            {
                var level = _scratch[i].Level;
                var run = new System.Text.StringBuilder();
                for (; i < _scratch.Count && _scratch[i].Level == level; i++)
                {
                    string line = LogViewState.Display(_scratch[i].Text) + "\n";
                    run.Append(line);
                    _shown.Enqueue((_scratch[i].Seq, Utf16Length(line)));
                    _nextSeq = _scratch[i].Seq + 1;
                }

                Append(run.ToString(), _style.ColorOf(level));
            }

            if (_shown.Count == 0 && _empty && SendLong(storage, Sel("length")) == 0)
            {
                Append(_feed.Empty, _style.Dim);   // the empty line, replaced by the first real one
            }
        }
        finally
        {
            SendVoid(storage, Sel("endEditing"));
        }

        if (following)
        {
            SendVoid(_text, Sel("scrollToEndOfDocument:"), 0);
        }

        UpdateTitle();
    }

    // NSString lengths are UTF-16 units, which a .NET string's are too.
    private static int Utf16Length(string text) => text.Length;

    private nint Storage => Send(_text, Sel("textStorage"));

    private void Append(string text, uint color)
    {
        nint attributed = Send(Send(Class("NSAttributedString"), Sel("alloc")), Sel("initWithString:attributes:"), NSString(text), Attributes(color));
        SendVoid(Storage, Sel("appendAttributedString:"), attributed);
        SendVoid(attributed, Sel("release"));
    }

    private static unsafe nint Attributes(uint color)
    {
        nint font = SendFontWeight(Class("NSFont"), Sel("monospacedSystemFontOfSize:weight:"), FontPoints, 0);
        Span<nint> keys = [NSString("NSFont"), NSString("NSColor")];
        Span<nint> values = [font, NSColor(color)];
        fixed (nint* k = keys, v = values)
        {
            return SendCount(Class("NSDictionary"), Sel("dictionaryWithObjects:forKeys:count:"), (nint)v, (nint)k, 2);
        }
    }

    private bool AtBottom()
    {
        nint clip = Send(_scroll, Sel("contentView"));
        var visible = SendRect(clip, Sel("bounds"));
        var document = SendRect(_text, Sel("frame"));
        return visible.Y + visible.Height >= document.Height - BottomSlack;
    }

    private void UpdateTitle()
    {
        if (!Alive)
        {
            return;
        }

        string title = _feed.Title(_following);
        if (title != _title)
        {
            _title = title;
            SetTitle(title);
        }
    }

    // ---- the view ----

    private void MakeText()
    {
        var (width, height) = ClientSize;
        _scroll = Send(Class("NSTextView"), Sel("scrollableTextView"));
        SendVoidRect(_scroll, Sel("setFrame:"), new CGRect(0, 0, width, height));
        SendVoidULong(_scroll, Sel("setAutoresizingMask:"), 2 | 16);   // NSViewWidthSizable | NSViewHeightSizable
        SendVoidBool(_scroll, Sel("setHasHorizontalScroller:"), 0);
        _text = Send(_scroll, Sel("documentView"));
        SendVoidBool(_text, Sel("setEditable:"), 0);
        SendVoidBool(_text, Sel("setSelectable:"), 1);
        SendVoidBool(_text, Sel("setRichText:"), 0);
        SendVoidSize(_text, Sel("setTextContainerInset:"), 6, 6);   // Windows' 6-point margin
        SendVoid(ContentView, Sel("addSubview:"), _scroll);
        SendVoid(Window, Sel("makeFirstResponder:"), _text);
        ObserveScrolling(Send(_scroll, Sel("contentView")));
    }

    // The theme on the frame, the text's background, the selection and the scroller; lines already shown keep their colours.
    private void ApplyStyle()
    {
        bool themed = PictureWindow.Themed();
        var palette = NeonSidekick.UI.Theme.Current;
        ApplyChrome(ViewerStyle.For(palette, themed));
        var style = LogViewStyle.For(palette, themed);
        if (style == _style && _styled)
        {
            return;
        }

        _styled = true;
        _style = style;
        SendVoid(_text, Sel("setBackgroundColor:"), NSColor(style.Background));
        SendVoid(_scroll, Sel("setBackgroundColor:"), NSColor(style.Background));
        SendVoid(_text, Sel("setInsertionPointColor:"), NSColor(style.Text));
        uint bg = style.Background;
        bool dark = (0.2126 * (bg & 0xFF)) + (0.7152 * ((bg >> 8) & 0xFF)) + (0.0722 * ((bg >> 16) & 0xFF)) < 128;
        SendVoidLong(_scroll, Sel("setScrollerKnobStyle:"), dark ? 2 : 1);   // NSScrollerKnobStyleLight on a dark background
        unsafe
        {
            Span<nint> keys = [NSString("NSBackgroundColor"), NSString("NSColor")];
            Span<nint> values = [NSColor(style.SelectionBack), NSColor(style.SelectionText)];
            fixed (nint* k = keys, v = values)
            {
                SendVoid(_text, Sel("setSelectedTextAttributes:"), SendCount(Class("NSDictionary"), Sel("dictionaryWithObjects:forKeys:count:"), (nint)v, (nint)k, 2));
            }
        }
    }

    /// <summary>The smoke's proof (<see cref="MacLineWindows.Probe"/>), on the main thread: a hidden window, two lines, a draw.</summary>
    internal static (bool Ok, string Detail) Probe()
    {
        var feed = new ProbeFeed();
        var window = new MacLineWindow(feed, "Probe window", null, null);
        try
        {
            window.Start(show: false);
            long length = SendLong(window.Storage, Sel("length"));
            nint view = window.ContentView;
            var bounds = SendRect(view, Sel("bounds"));
            nint rep = SendInitRect(view, Sel("bitmapImageRepForCachingDisplayInRect:"), bounds);
            SendVoidRectNint(view, Sel("cacheDisplayInRect:toBitmapImageRep:"), bounds, rep);
            bool hidden = SendBool(window.Window, Sel("isVisible")) == 0;
            bool ok = length == "a warning\nan error\n".Length && hidden && window._title == ProbeFeed.TitleText;
            return (ok, $"a hidden window's text view took {length} characters in two levels' colours and drew them; the title followed the feed");
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class ProbeFeed : ILineFeed
    {
        public const string TitleText = "probe";

        public event Action? Appended
        {
            add { }
            remove { }
        }

        public string Empty => "nothing";

        public long CopySince(long from, List<LogLine> into)
        {
            if (from == 0)
            {
                into.Add(new LogLine(0, DiagnosticLevel.Warning, "a warning"));
                into.Add(new LogLine(1, DiagnosticLevel.Error, "an error"));
            }

            return 0;
        }

        public string Title(bool following) => TitleText;

        public bool Key(int virtualKey, bool control, bool repeat) => false;

        public void Dispose()
        {
        }
    }
}
