using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.BrowserBarNative;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

// ── The navigation bar (2026-10-13) ───────────────────────────────────────

/// <summary>
/// The Browser window's navigation bar (later on 2026-10-13, the user's ask): <see cref="BrowserToolbar"/>'s strip drawn with GDI
/// across the top of the window, the view below it (<see cref="WebViewHost.TopInset"/>), and a stock EDIT child for the address. The
/// keys reach the bar from the message pump before <c>TranslateMessage</c> (<see cref="PreKey"/>), so the box's Enter, Esc and Ctrl+A
/// and the window's chords never become characters that ding — no subclassed procedure needed.
/// </summary>
internal sealed unsafe partial class BrowserWindowThread
{
    private const int VkReturn = 0x0D;
    private const int VkA = 0x41;

    /// <summary>The glyphs' size at 96 DPI, and the address's font's points.</summary>
    private const int GlyphPixels = 16;
    private const int AddressPoints = 10;

    /// <summary>How far a lit button's fill goes from the bar's background toward its edge colour, hovered and pressed.</summary>
    private const int HoverPercent = 25;
    private const int PressPercent = 45;

    /// <summary>The white below the bar before the view paints.</summary>
    private const uint White = 0x00FFFFFF;

    private readonly BrowserToolbar _bar = new();
    private BrowserBarState _barState = BrowserBarState.Initial;
    private BrowserBarLayout? _layout;
    private ViewerStyle? _barStyle;
    private IntPtr _edit;
    private IntPtr _tooltip;
    private IntPtr _glyphFont;
    private IntPtr _textFont;
    private IntPtr _editBrush;
    private uint _dpi = 96;
    private int _lineHeight;
    private bool _tracking;

    // The box, its font and the tooltips, made once the window is (before it shows).
    private void MakeBar(IntPtr instance)
    {
        _dpi = Math.Max(96u, GetDpiForWindow(_hwnd));
        MakeFonts();
        _edit = BrowserBarNative.CreateWindowExW(0, "EDIT", null, WsChild | WsVisible | EsAutoHScroll, 0, 0, 0, 0, _hwnd, (IntPtr)AddressId, instance, IntPtr.Zero);
        if (_edit == IntPtr.Zero)
        {
            DiagnosticLog.Warn("Browser", "The Browser window's address box was not made; the bar's buttons still work.");
        }
        else
        {
            SendMessageW(_edit, WmSetFont, _textFont, 1);
        }

        _tooltip = MakeTooltip(_hwnd, instance);
        LayOutBar();
        if (_tooltip != IntPtr.Zero && _layout is { } layout)
        {
            foreach (var item in BrowserBarLayout.Buttons)
            {
                AddTool(_tooltip, _hwnd, ToolId(item), ToRect(layout[item]), _barState.Name(item));
            }
        }
    }

    private void FreeBar()
    {
        foreach (var handle in new[] { _glyphFont, _textFont, _editBrush })
        {
            if (handle != IntPtr.Zero)
            {
                DeleteObject(handle);
            }
        }

        _glyphFont = _textFont = _editBrush = IntPtr.Zero;
    }

    private static int ToolId(BrowserToolItem item) => (int)item + 1;

    private static Rect ToRect(BarRect r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };

    // The glyphs' font and the address's, for the window's DPI; the box's line height read from the latter.
    private void MakeFonts()
    {
        if (_glyphFont != IntPtr.Zero)
        {
            DeleteObject(_glyphFont);
        }

        if (_textFont != IntPtr.Zero)
        {
            DeleteObject(_textFont);
        }

        fixed (char* face = BrowserToolbar.GlyphFace)
        {
            _glyphFont = CreateFontW(-BrowserToolbar.Scale(GlyphPixels, _dpi), 0, 0, 0, FwNormal, 0, 0, 0, DefaultCharset, 0, 0, ClearTypeQuality, 0, face);
        }

        _textFont = ContextMenuWindow.MakeFont(_dpi, AddressPoints);
        IntPtr dc = GetDC(_hwnd);
        IntPtr old = SelectObject(dc, _textFont);
        try
        {
            TextMetric metric;
            _lineHeight = GetTextMetricsW(dc, &metric) ? metric.tmHeight : BrowserToolbar.Scale(18, _dpi);
        }
        finally
        {
            SelectObject(dc, old);
            ReleaseDC(_hwnd, dc);
        }
    }

    // Everything placed for the window's width: the layout, the box, the tooltips' rectangles, and the view below the strip.
    private void LayOutBar()
    {
        if (_hwnd == IntPtr.Zero || _glyphFont == IntPtr.Zero)
        {
            return;
        }

        Rect client;
        GetClientRect(_hwnd, &client);
        var layout = BrowserToolbar.Layout(client.Right, _dpi);
        _layout = layout;
        if (_edit != IntPtr.Zero)
        {
            var box = layout.EditFor(_lineHeight);
            SetWindowPos(_edit, IntPtr.Zero, box.Left, box.Top, box.Width, box.Height, SwpNoZOrder | SwpNoActivate);
        }

        if (_tooltip != IntPtr.Zero)
        {
            foreach (var item in BrowserBarLayout.Buttons)
            {
                MoveTool(_tooltip, _hwnd, ToolId(item), ToRect(layout[item]));
            }
        }

        if (_host is { } host)
        {
            host.TopInset = layout.Height;
            host.Resize();
        }

        InvalidateRect(_hwnd, null, false);
    }

    private void InvalidateBar()
    {
        if (_layout is { } layout)
        {
            Rect client;
            GetClientRect(_hwnd, &client);
            var strip = new Rect { Right = client.Right, Bottom = layout.Height };
            InvalidateRect(_hwnd, &strip, false);
        }
    }

    // A new state: the strip drawn again, and reload's tooltip renamed when it became stop or back.
    private void SetBarState(BrowserBarState state)
    {
        if (state == _barState)
        {
            return;
        }

        bool renamed = state.Name(BrowserToolItem.Reload) != _barState.Name(BrowserToolItem.Reload);
        _barState = state;
        if (renamed && _tooltip != IntPtr.Zero)
        {
            RenameTool(_tooltip, _hwnd, ToolId(BrowserToolItem.Reload), state.Name(BrowserToolItem.Reload));
        }

        InvalidateBar();
    }

    private void ReadHistory()
    {
        if (_host is { IsCreated: true } host)
        {
            SetBarState(_barState with { CanBack = host.CanGoBack, CanForward = host.CanGoForward });
        }
    }

    // The theme's colours for the bar and the box; drawn again when they changed (a /theme change, met at the next focus).
    private void ApplyBarStyle(ViewerStyle style)
    {
        if (_barStyle == style)
        {
            return;
        }

        _barStyle = style;
        if (_editBrush != IntPtr.Zero)
        {
            DeleteObject(_editBrush);
        }

        _editBrush = CreateSolidBrush(style.Background);
        InvalidateBar();
        if (_edit != IntPtr.Zero)
        {
            InvalidateRect(_edit, null, true);
        }
    }

    // The strip into a memory DC and out: the fill and its bottom edge, each button (lit under the pointer, darker pressed, its
    // glyph dim while it does nothing), the framed field and its padlock. Below the strip, white until the view covers it.
    private void PaintBar(IntPtr hwnd)
    {
        PaintStruct ps;
        IntPtr hdc = BeginPaint(hwnd, &ps);
        IntPtr mem = IntPtr.Zero, bitmap = IntPtr.Zero;
        try
        {
            Rect client;
            GetClientRect(hwnd, &client);
            int width = Math.Max(1, client.Right);
            int height = _layout?.Height ?? 0;
            if (client.Bottom > height)
            {
                Fill(hdc, new Rect { Top = height, Right = width, Bottom = client.Bottom }, White);
            }

            if (_layout is not { } layout || _barStyle is not { } style || ps.rcPaint.Top >= height)
            {
                return;
            }

            mem = CreateCompatibleDC(hdc);
            bitmap = CreateCompatibleBitmap(hdc, width, height);
            if (mem == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return;
            }

            SelectObject(mem, bitmap);
            SelectObject(mem, _glyphFont);
            SetBkMode(mem, Transparent);
            Fill(mem, new Rect { Right = width, Bottom = height }, style.Background);
            Fill(mem, new Rect { Top = height - 1, Right = width, Bottom = height }, BrowserToolbar.Blend(style.Background, style.Border, 50));
            foreach (var item in BrowserBarLayout.Buttons)
            {
                var r = ToRect(layout[item]);
                bool enabled = _barState.Enabled(item);
                if (enabled && _bar.Hot == item)
                {
                    Fill(mem, r, BrowserToolbar.Blend(style.Background, style.Border, _bar.Pressed == item ? PressPercent : HoverPercent));
                }

                SetTextColor(mem, enabled ? style.CaptionText : style.Text);
                DrawTextW(mem, _barState.Glyph(item), -1, &r, DtCenter | DtVCenter | DtSingleLine | DtNoPrefix);
            }

            var field = ToRect(layout.Field);
            Fill(mem, field, style.Border);
            Fill(mem, field with { Left = field.Left + 1, Top = field.Top + 1, Right = field.Right - 1, Bottom = field.Bottom - 1 }, style.Background);
            if (_barState.Glyph(BrowserToolItem.Padlock) is { Length: > 0 } padlock)
            {
                var slot = ToRect(layout.Padlock);
                SetTextColor(mem, _barState.Lock == BrowserLock.Secure ? style.CaptionText : style.Text);
                DrawTextW(mem, padlock, -1, &slot, DtCenter | DtVCenter | DtSingleLine | DtNoPrefix);
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

    // A rectangle filled with one colour: ExtTextOutW's opaque fill, no brush to make and free.
    private static void Fill(IntPtr dc, Rect rect, uint color)
    {
        SetBkColor(dc, color);
        ExtTextOutW(dc, 0, 0, EtoOpaque, &rect, null, 0, null);
    }

    // The bar's share of the window's messages; true when it answered (the answer in result).
    private bool HandleBar(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (message)
        {
            case WmPaint:
                PaintBar(hwnd);
                return true;
            case WmEraseBackground:
                result = 1;   // the paint fills everything: no white flash under the strip
                return true;
            case WmMouseMove:
            {
                if (!_tracking)
                {
                    var track = new TrackMouseEventInfo { cbSize = (uint)sizeof(TrackMouseEventInfo), dwFlags = TmeLeave, hwndTrack = hwnd };
                    _tracking = TrackMouseEvent(&track);
                }

                if (_bar.Move(HitTest(lParam)))
                {
                    InvalidateBar();
                }

                return true;
            }

            case WmMouseLeave:
                _tracking = false;
                if (_bar.Leave())
                {
                    InvalidateBar();
                }

                return true;
            case WmLeftButtonDown:
                _bar.Down(HitTest(lParam), _barState);
                if (_bar.Pressed is not null)
                {
                    SetCapture(hwnd);
                    InvalidateBar();
                }

                return true;
            case WmLeftButtonUp:
                if (_bar.Pressed is not null)
                {
                    var clicked = _bar.Up(HitTest(lParam), _barState);
                    ReleaseCapture();
                    InvalidateBar();
                    if (clicked is { } item)
                    {
                        Click(item);
                    }
                }

                return true;
            case WmCaptureChanged:
                if (_bar.Cancel())
                {
                    InvalidateBar();
                }

                return true;
            case WmCommand when (int)((long)wParam & 0xFFFF) == AddressId:
                switch ((uint)(((long)wParam >> 16) & 0xFFFF))
                {
                    case EnSetFocus:
                        PostMessageW(_edit, EmSetSel, IntPtr.Zero, -1);   // after the click that focused it has placed its caret
                        break;
                    case EnChange:
                        SetBarState(_barState with { Typed = TextOf(_edit).Trim().Length > 0 });
                        break;
                }

                return true;
            case WmCtlColorEdit when lParam == _edit && _barStyle is { } style:
                SetTextColor(wParam, style.CaptionText);
                SetBkColor(wParam, style.Background);
                result = _editBrush;
                return true;
            case WmDpiChanged:
            {
                // Dragged onto a monitor of another scale: Windows' suggested frame, the fonts made again, everything placed again.
                var r = (Rect*)lParam;
                SetWindowPos(hwnd, IntPtr.Zero, r->Left, r->Top, r->Right - r->Left, r->Bottom - r->Top, SwpNoZOrder | SwpNoActivate);
                _dpi = Math.Max(96u, (uint)((long)wParam & 0xFFFF));
                MakeFonts();
                if (_edit != IntPtr.Zero)
                {
                    SendMessageW(_edit, WmSetFont, _textFont, 1);
                }

                LayOutBar();
                return true;
            }
        }

        return false;
    }

    private BrowserToolItem? HitTest(IntPtr lParam) =>
        _layout?.HitTest((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));

    private void Click(BrowserToolItem item)
    {
        switch (item)
        {
            case BrowserToolItem.Back:
                _host?.GoBack();
                break;
            case BrowserToolItem.Forward:
                _host?.GoForward();
                break;
            case BrowserToolItem.Reload when _barState.Loading:
                _host?.Stop();
                break;
            case BrowserToolItem.Reload:
                _host?.Reload();
                break;
            case BrowserToolItem.Home:
                Go(BrowserWindow.HomeAddress());
                break;
            case BrowserToolItem.History:
                OpenHistoryMenu(keyboard: false);
                break;
            case BrowserToolItem.Links:
                OpenLinksMenu(keyboard: false);
                break;
            case BrowserToolItem.Go:
                GoTyped();
                break;
            case BrowserToolItem.Open:
                OpenOutside();
                break;
        }
    }

    // The box's text, gone to: an address, or words searched; a refused one beeps and stays for the user to change.
    private void GoTyped()
    {
        if (_edit == IntPtr.Zero)
        {
            return;
        }

        string text = TextOf(_edit);
        var go = BrowserAddress.Resolve(text, BrowserWindow.SearchUrl);
        if (go.Refusal is { } refusal)
        {
            MessageBeep(MbOk);
            DiagnosticLog.Info("Browser", refusal + ".");
            return;
        }

        if (go.Url is { } url)
        {
            Go(url.AbsoluteUri);
            _host?.Focus();
        }
    }

    // The page in the user's own browser: web addresses only (the shell runs whatever handler a scheme has).
    private void OpenOutside()
    {
        string? source = _host?.Source;
        if (!VideoPage.IsWebLink(source))
        {
            return;
        }

        try
        {
            BrowserWindow.OpenLink(source!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn("Browser", $"The page did not open in the default browser: {ex.Message}");
        }
    }

    // The box shows the page's address, unless the user is typing in it.
    private void ShowAddress(string? source, bool force = false)
    {
        if (_edit != IntPtr.Zero && (force || GetFocus() != _edit))
        {
            SetWindowTextW(_edit, BrowserAddress.Shown(source));
        }
    }

    private void FocusAddress()
    {
        if (_edit != IntPtr.Zero)
        {
            SetFocus(_edit);
            SendMessageW(_edit, EmSetSel, IntPtr.Zero, -1);
        }
    }

    /// <summary>
    /// A key going down, before <c>TranslateMessage</c> makes a character of it: the box's Enter (go), Esc (the address back, the
    /// keyboard to the page) and Ctrl+A (select all, which a version 5 EDIT lacks), then the window's chords, in the box or the window.
    /// True when taken: the message is neither translated nor dispatched.
    /// </summary>
    private bool PreKey(Msg* msg)
    {
        if (msg->message is not (WmKeyDown or WmSysKeyDown) || msg->hwnd == IntPtr.Zero)
        {
            return false;
        }

        int key = (int)msg->wParam;
        if ((msg->hwnd == _edit || msg->hwnd == _hwnd) && MenuKey(key))
        {
            return true;   // the history menu has the keys while it shows
        }

        if (msg->hwnd == _edit)
        {
            bool control = GetKeyState(VkControl) < 0, alt = GetKeyState(VkMenu) < 0;
            if (key == VkReturn && !alt)
            {
                GoTyped();
                return true;
            }

            if (key == BrowserKeys.VkEscape && !control && !alt)
            {
                ShowAddress(_host?.Source, force: true);
                _host?.Focus();
                return true;
            }

            if (key == VkA && control && !alt)
            {
                SendMessageW(_edit, EmSetSel, IntPtr.Zero, -1);
                return true;
            }

            return OnKey(key);
        }

        return msg->hwnd == _hwnd && OnKey(key);
    }
}
