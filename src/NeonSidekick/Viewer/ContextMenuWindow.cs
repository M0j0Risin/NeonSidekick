using System.Runtime.InteropServices;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture menu on the screen (2026-10-04, the user's pick: drawn in the theme, not Windows' own menu): a root popup and at
/// most one submenu beside it, each a <c>WS_POPUP</c> owned by the picture window, on that window's thread, never activated
/// (<c>WS_EX_NOACTIVATE</c>, <c>MA_NOACTIVATE</c>), so the keyboard stays with the window, which hands its keys here
/// (<see cref="Key"/>). Modeless — no nested message loop as <c>TrackPopupMenu</c> runs, which would swallow the
/// <c>WM_QUIT</c> of the app closing its windows: the root holds the mouse capture, so every move and click comes here wherever the
/// mouse is; a press outside both popups, the capture lost, or the window deactivated, moved, resized or full-screened
/// (<see cref="Close"/> from the window) closes it. A chosen row's command is posted to the window
/// (<c>chosenMessage</c>, the command in wParam), so it runs after the popups are gone. Everything it decides is
/// <see cref="ContextMenuState"/>'s; this draws (Segoe UI at the window's DPI, a memory DC each paint) and routes the mouse.
/// The smoke's <c>viewer:menu</c> proves the class and the font (<see cref="Probe"/>).
/// </summary>
internal sealed unsafe class ContextMenuWindow(IntPtr owner, uint chosenMessage)
{
    private const uint ProbeMessage = WmApp + 9;
    private static readonly IntPtr ProbeAnswer = new(0x3E0);

    /// <summary>The menu's text size in points: Windows' own menus'.</summary>
    public const int FontPoints = 9;

    // The layout at 96 DPI: a row's least height, a separator's, the padding above and below the rows, the text's indent, the
    // room kept on the right for the submenu mark, a popup's least width.
    private const int MinItemHeight = 26;
    private const int SeparatorHeight = 9;
    private const int Padding = 4;
    private const int TextIndent = 14;
    private const int MarkRoom = 28;
    private const int MinWidth = 180;

    private static readonly Lock s_classGate = new();
    private static IntPtr s_className;
    private static ushort s_atom;

    private ContextMenuState? _state;
    private MenuStyle _style = MenuStyle.Black;
    private GCHandle _self;
    private IntPtr _root;
    private IntPtr _sub;
    private int _subFor = -1;
    private IntPtr _font;
    private uint _dpi = 96;
    private int _itemHeight;
    private Rect _rootRect;
    private Rect _subRect;
    private Rect _work;
    private bool _closing;

    /// <summary>Whether the menu shows.</summary>
    public bool IsOpen => _root != IntPtr.Zero;

    /// <summary>
    /// The menu of <paramref name="items"/> opened at the screen point (<paramref name="x"/>, <paramref name="y"/>) in
    /// <paramref name="style"/>; the first row highlighted when it was opened by a key (<paramref name="keyboard"/>). One already
    /// open is closed first. False (and nothing shown) when the popup could not be made.
    /// </summary>
    public bool Open(IReadOnlyList<ContextMenuItem> items, int x, int y, MenuStyle style, bool keyboard)
    {
        ArgumentNullException.ThrowIfNull(items);
        Close();
        IntPtr instance = GetModuleHandle(IntPtr.Zero);
        if (!EnsureClass(instance, out string? failure))
        {
            DiagnosticLog.Warn("Viewer", $"The picture menu could not open: {failure}");
            return false;
        }

        _state = new ContextMenuState(items, keyboard);
        _style = style;
        _dpi = Math.Max(96u, GetDpiForWindow(owner));
        _font = MakeFont(_dpi);
        _itemHeight = Math.Max(Scale(MinItemHeight), TextHeight() + Scale(10));
        _work = WorkArea(x, y);
        int width = Width(items), height = ContextMenuState.Height(items, _itemHeight, Scale(SeparatorHeight), Scale(Padding));
        var (left, top) = ContextMenuState.Place(x, y, width, height, _work.Left, _work.Top, _work.Right, _work.Bottom);
        _self = GCHandle.Alloc(this);
        _root = MakePopup(instance, left, top, width, height);
        if (_root == IntPtr.Zero)
        {
            Close();
            return false;
        }

        _rootRect = new Rect { Left = left, Top = top, Right = left + width, Bottom = top + height };
        ShowWindow(_root, SwShowNoActivate);
        SetCapture(_root);
        return true;
    }

    /// <summary>A key the window got while the menu shows: true when the menu took it (then the window does nothing more with it).</summary>
    public bool Key(int virtualKey)
    {
        if (_state is not { } state)
        {
            return false;
        }

        var outcome = state.Key(virtualKey);
        Act(outcome);
        return outcome != MenuOutcome.None;
    }

    /// <summary>The menu closed, nothing chosen; nothing when it is not open.</summary>
    public void Close()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (_root != IntPtr.Zero && GetCapture() == _root)
            {
                ReleaseCapture();
            }

            DestroySub();
            if (_root != IntPtr.Zero)
            {
                DestroyWindow(_root);
                _root = IntPtr.Zero;
            }

            if (_font != IntPtr.Zero)
            {
                DeleteObject(_font);
                _font = IntPtr.Zero;
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            _state = null;
        }
        finally
        {
            _closing = false;
        }
    }

    /// <summary>
    /// <c>viewer:menu</c> for the smoke: the popup class registered, a hidden popup answering through its <c>[UnmanagedCallersOnly]</c>
    /// procedure, Segoe UI made and a row measured with <c>DrawTextW</c>'s <c>DT_CALCRECT</c>, and the exe's own path parsed into a shell
    /// item list (Show in Explorer's first step) and freed. Nothing is shown.
    /// </summary>
    public static (bool Ok, string Detail) Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (true, "skipped: not Windows");
        }

        try
        {
            IntPtr instance = GetModuleHandle(IntPtr.Zero);
            if (!EnsureClass(instance, out string? failure))
            {
                return (false, failure ?? "the menu class was not registered");
            }

            IntPtr hwnd = CreateWindowExW(WsExToolWindow | WsExNoActivate, s_className, IntPtr.Zero, WsPopup, 0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                return (false, $"CreateWindowExW failed ({Marshal.GetLastPInvokeError()})");
            }

            IntPtr dc = IntPtr.Zero, font = IntPtr.Zero;
            try
            {
                IntPtr answer = SendMessageW(hwnd, ProbeMessage, IntPtr.Zero, IntPtr.Zero);
                if (answer != ProbeAnswer)
                {
                    return (false, $"the popup's procedure answered 0x{answer:X}");
                }

                dc = GetDC(hwnd);
                font = MakeFont(96);
                if (dc == IntPtr.Zero || font == IntPtr.Zero)
                {
                    return (false, "a DC or Segoe UI was not made");
                }

                SelectObject(dc, font);
                var rect = new Rect();
                DrawTextW(dc, PictureMenuText.RotateRight, -1, &rect, DtCalcRect | DtSingleLine | DtNoPrefix);
                if (rect.Right <= 0 || rect.Bottom <= 0)
                {
                    return (false, "DrawTextW measured nothing");
                }

                string? exe = Environment.ProcessPath;
                IntPtr pidl = IntPtr.Zero;
                uint attributes = 0;
                int hr = exe is null ? EFail : SHParseDisplayName(exe, IntPtr.Zero, &pidl, 0, &attributes);
                if (pidl != IntPtr.Zero)
                {
                    ILFree(pidl);
                }

                return hr < 0
                    ? (false, $"SHParseDisplayName on the exe failed (0x{hr:X8})")
                    : (true, $"a hidden popup answered through its procedure; Segoe UI measured a row at {rect.Right}x{rect.Bottom}; the exe parsed into a shell item list");
            }
            finally
            {
                if (font != IntPtr.Zero)
                {
                    DeleteObject(font);
                }

                if (dc != IntPtr.Zero)
                {
                    ReleaseDC(hwnd, dc);
                }

                DestroyWindow(hwnd);
            }
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The popup class, registered once per process: the procedure below, the system's shadow, no background brush.
    private static bool EnsureClass(IntPtr instance, out string? failure)
    {
        lock (s_classGate)
        {
            failure = null;
            if (s_atom != 0)
            {
                return true;
            }

            if (s_className == IntPtr.Zero)
            {
                s_className = Marshal.StringToHGlobalUni("NeonSidekick.PictureMenu");
            }

            var wc = new WndClassEx
            {
                cbSize = (uint)sizeof(WndClassEx),
                style = CsDropShadow,
                lpfnWndProc = &WindowProcedure,
                hInstance = instance,
                hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IdcArrow),
                lpszClassName = s_className,
            };
            s_atom = RegisterClassExW(&wc);
            if (s_atom == 0)
            {
                failure = $"RegisterClassExW failed ({Marshal.GetLastPInvokeError()})";
                return false;
            }

            return true;
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == ProbeMessage)
            {
                return ProbeAnswer;
            }

            if (message == WmNcCreate && ((CreateStruct*)lParam)->lpCreateParams is var created && created != IntPtr.Zero)
            {
                SetWindowLongPtr(hwnd, GwlpUserData, created);
            }

            IntPtr user = GetWindowLongPtr(hwnd, GwlpUserData);
            if (user != IntPtr.Zero && GCHandle.FromIntPtr(user).Target is ContextMenuWindow menu)
            {
                return menu.Handle(hwnd, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Nothing may cross back into user32: an exception here would take the process down.
            DiagnosticLog.Error("Viewer", $"The picture menu failed on message 0x{message:X}.", ex);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmPaint:
                Paint(hwnd);
                return IntPtr.Zero;
            case WmEraseBackground:
                return 1;
            case WmMouseActivate:
                return MaNoActivate;
            case WmMouseMove when hwnd == _root:
                Hover(CursorNow());
                return IntPtr.Zero;
            case WmLeftButtonDown or WmRightButtonDown when hwnd == _root:
                if (Where(CursorNow()).Level < 0)
                {
                    Close();   // a press outside both popups: closed, the press itself not passed on (Windows' menus do the same)
                }

                return IntPtr.Zero;
            case WmLeftButtonUp or WmRightButtonUp when hwnd == _root:
                Click(CursorNow());
                return IntPtr.Zero;
            case WmCaptureChanged when hwnd == _root && !_closing && lParam != _root:
                Close();
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Hover((int X, int Y) point)
    {
        if (_state is not { } state)
        {
            return;
        }

        var (level, index) = Where(point);
        var outcome = level switch
        {
            1 => state.HoverSub(index),
            0 => state.HoverRoot(index),
            _ => MenuOutcome.None,
        };
        Act(outcome);
    }

    private void Click((int X, int Y) point)
    {
        if (_state is not { } state)
        {
            return;
        }

        var (level, index) = Where(point);
        var outcome = level switch
        {
            1 => state.ClickSub(index),
            0 => state.ClickRoot(index),
            _ => MenuOutcome.None,
        };
        Act(outcome);
    }

    // What a key or the mouse did: drawn again, closed, or a row chosen — closed first, then the command posted to the window.
    private void Act(MenuOutcome outcome)
    {
        switch (outcome)
        {
            case MenuOutcome.Changed:
                SyncSub();
                Invalidate();
                break;
            case MenuOutcome.Close:
                Close();
                break;
            case MenuOutcome.Chosen when _state is { } state:
                int command = state.Chosen;
                Close();
                PostMessageW(owner, chosenMessage, command, IntPtr.Zero);
                break;
        }
    }

    // The popup and row under a screen point: level 1 the submenu, 0 the root, -1 neither (index -1 on a separator or padding).
    private (int Level, int Index) Where((int X, int Y) point)
    {
        if (_state is not { } state)
        {
            return (-1, -1);
        }

        int separator = Scale(SeparatorHeight), padding = Scale(Padding);
        if (_sub != IntPtr.Zero && state.SubItems is { } sub && Inside(_subRect, point))
        {
            return (1, ContextMenuState.ItemAt(sub, point.Y - _subRect.Top, _itemHeight, separator, padding));
        }

        if (Inside(_rootRect, point))
        {
            return (0, ContextMenuState.ItemAt(state.Items, point.Y - _rootRect.Top, _itemHeight, separator, padding));
        }

        return (-1, -1);
    }

    private static bool Inside(Rect rect, (int X, int Y) point) => point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    private static (int X, int Y) CursorNow()
    {
        Point point;
        GetCursorPos(&point);
        return (point.X, point.Y);
    }

    // The submenu window matched to the state: made beside its row when one opened, gone when it closed or another opened.
    private void SyncSub()
    {
        if (_state is not { } state)
        {
            return;
        }

        int wanted = state.Open ?? -1;
        if (wanted == _subFor)
        {
            return;
        }

        DestroySub();
        if (state.SubItems is not { } items)
        {
            return;
        }

        int width = Width(items), height = ContextMenuState.Height(items, _itemHeight, Scale(SeparatorHeight), Scale(Padding));
        int rowTop = _rootRect.Top + ContextMenuState.ItemTop(state.Items, wanted, _itemHeight, Scale(SeparatorHeight), Scale(Padding)) - Scale(Padding);
        var (left, top) = ContextMenuState.PlaceSub(_rootRect.Left, _rootRect.Right, rowTop, width, height, _work.Left, _work.Top, _work.Right, _work.Bottom);
        _sub = MakePopup(GetModuleHandle(IntPtr.Zero), left, top, width, height);
        if (_sub == IntPtr.Zero)
        {
            return;
        }

        _subFor = wanted;
        _subRect = new Rect { Left = left, Top = top, Right = left + width, Bottom = top + height };
        ShowWindow(_sub, SwShowNoActivate);
    }

    private void DestroySub()
    {
        if (_sub != IntPtr.Zero)
        {
            DestroyWindow(_sub);
            _sub = IntPtr.Zero;
        }

        _subFor = -1;
    }

    private void Invalidate()
    {
        if (_root != IntPtr.Zero)
        {
            InvalidateRect(_root, null, false);
        }

        if (_sub != IntPtr.Zero)
        {
            InvalidateRect(_sub, null, false);
        }
    }

    private IntPtr MakePopup(IntPtr instance, int x, int y, int width, int height) =>
        CreateWindowExW(WsExTopmost | WsExToolWindow | WsExNoActivate, s_className, IntPtr.Zero, WsPopup, x, y, width, height, owner, IntPtr.Zero, instance, GCHandle.ToIntPtr(_self));

    // A popup's rows drawn into a memory DC and copied out: the fill, the edge, each row (the highlighted one on the accent, a
    // disabled one dim, a submenu's › at the right), a separator a line.
    private void Paint(IntPtr hwnd)
    {
        PaintStruct ps;
        IntPtr hdc = BeginPaint(hwnd, &ps);
        IntPtr mem = IntPtr.Zero, bitmap = IntPtr.Zero;
        try
        {
            if (_state is not { } state)
            {
                return;
            }

            bool root = hwnd == _root;
            var items = root ? state.Items : state.SubItems;
            if (items is null)
            {
                return;
            }

            int hot = root ? (state.Open is int open && (state.InSub || state.Hot == open) ? open : state.Hot) : state.SubHot;
            Rect client;
            GetClientRect(hwnd, &client);
            int width = Math.Max(1, client.Right), height = Math.Max(1, client.Bottom);
            mem = CreateCompatibleDC(hdc);
            bitmap = CreateCompatibleBitmap(hdc, width, height);
            if (mem == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return;
            }

            SelectObject(mem, bitmap);
            SelectObject(mem, _font);
            SetBkMode(mem, Transparent);
            Fill(mem, new Rect { Right = width, Bottom = height }, _style.Border);
            Fill(mem, new Rect { Left = 1, Top = 1, Right = width - 1, Bottom = height - 1 }, _style.Background);
            int separator = Scale(SeparatorHeight), padding = Scale(Padding);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                int top = ContextMenuState.ItemTop(items, i, _itemHeight, separator, padding);
                if (item.IsSeparator)
                {
                    int middle = top + separator / 2;
                    Fill(mem, new Rect { Left = Scale(8), Top = middle, Right = width - Scale(8), Bottom = middle + Math.Max(1, Scale(1)) }, _style.Separator);
                    continue;
                }

                var row = new Rect { Left = 1, Top = top, Right = width - 1, Bottom = top + _itemHeight };
                bool lit = i == hot && item.Selectable;
                if (lit)
                {
                    Fill(mem, row, _style.HotBack);
                }

                SetTextColor(mem, !item.Enabled ? _style.Dim : lit ? _style.HotText : _style.Text);
                var text = row with { Left = Scale(TextIndent), Right = width - Scale(MarkRoom) };
                DrawTextW(mem, item.Label, -1, &text, DtLeft | DtVCenter | DtSingleLine | DtNoPrefix | DtEndEllipsis);
                if (item.HasChildren)
                {
                    var mark = row with { Right = width - Scale(10) };
                    DrawTextW(mem, "›", -1, &mark, DtRight | DtVCenter | DtSingleLine | DtNoPrefix);
                }
            }

            BitBlt(hdc, 0, 0, width, height, mem, 0, 0, SrcCopy);
        }
        finally
        {
            if (mem != IntPtr.Zero)
            {
                DeleteDC(mem);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            EndPaint(hwnd, &ps);
        }
    }

    // A popup's width: its widest row measured in the menu's font, with the indent and the mark's room, at least MinWidth.
    private int Width(IReadOnlyList<ContextMenuItem> items)
    {
        int widest = 0;
        IntPtr dc = GetDC(owner);
        IntPtr old = SelectObject(dc, _font);
        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    continue;
                }

                var rect = new Rect();
                DrawTextW(dc, item.Label, -1, &rect, DtCalcRect | DtSingleLine | DtNoPrefix);
                widest = Math.Max(widest, rect.Right);
            }
        }
        finally
        {
            SelectObject(dc, old);
            ReleaseDC(owner, dc);
        }

        return Math.Max(Scale(MinWidth), widest + Scale(TextIndent) + Scale(MarkRoom));
    }

    private int TextHeight()
    {
        IntPtr dc = GetDC(owner);
        IntPtr old = SelectObject(dc, _font);
        try
        {
            TextMetric metric;
            return GetTextMetricsW(dc, &metric) ? metric.tmHeight : Scale(16);
        }
        finally
        {
            SelectObject(dc, old);
            ReleaseDC(owner, dc);
        }
    }

    // The work area of the monitor under the point: where the popups must stay.
    private static Rect WorkArea(int x, int y)
    {
        var monitor = new MonitorInfo { cbSize = (uint)sizeof(MonitorInfo) };
        IntPtr handle = MonitorFromPoint(new Point { X = x, Y = y }, MonitorDefaultToNearest);
        return handle != IntPtr.Zero && GetMonitorInfoW(handle, &monitor) ? monitor.rcWork : new Rect { Left = int.MinValue / 2, Top = int.MinValue / 2, Right = int.MaxValue / 2, Bottom = int.MaxValue / 2 };
    }

    private int Scale(int pixels) => (int)(pixels * _dpi / 96);

    /// <summary>
    /// Whether a window's <c>WM_CONTEXTMENU</c> is the client area's (its lParam -1, the keyboard's, or a screen point inside the client
    /// rectangle) rather than the title bar's, whose system menu the default must still open.
    /// </summary>
    internal static bool IsClientContextMenu(IntPtr hwnd, IntPtr lParam)
    {
        if ((long)lParam == -1 || (uint)(long)lParam == uint.MaxValue)
        {
            return true;
        }

        var point = new Point { X = (short)((long)lParam & 0xFFFF), Y = (short)(((long)lParam >> 16) & 0xFFFF) };
        ScreenToClient(hwnd, &point);
        Rect client;
        GetClientRect(hwnd, &client);
        return point.X >= 0 && point.Y >= 0 && point.X < client.Right && point.Y < client.Bottom;
    }

    /// <summary>Segoe UI at <see cref="FontPoints"/> for <paramref name="dpi"/>: the menu's font, and the thumbnails' captions'.</summary>
    internal static IntPtr MakeFont(uint dpi, int points = FontPoints)
    {
        int height = -(int)Math.Round(points * dpi / 72.0, MidpointRounding.AwayFromZero);
        fixed (char* face = "Segoe UI")
        {
            return CreateFontW(height, 0, 0, 0, FwNormal, 0, 0, 0, DefaultCharset, 0, 0, ClearTypeQuality, VariablePitchSwiss, face);
        }
    }

    // A rectangle filled with one colour: ExtTextOutW's opaque fill, no brush to make and free.
    private static void Fill(IntPtr dc, Rect rect, uint color)
    {
        SetBkColor(dc, color);
        ExtTextOutW(dc, 0, 0, EtoOpaque, &rect, null, 0, null);
    }
}
