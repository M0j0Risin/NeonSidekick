using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

// ── The bar (2026-10-18, the Mac Browser plan: native AppKit controls) ────

/// <summary>
/// The Mac Browser window's bar: borderless <c>NSButton</c>s with SF Symbols for back, forward, reload or stop, home, history and the saved
/// links, the padlock, an <c>NSTextField</c> address box, Go and Open, laid out by <see cref="BrowserToolbar.Layout"/> in points (its sizes at
/// 96 DPI are points here), their names from <see cref="BrowserText"/> as tooltips and as the symbols' accessibility descriptions (VoiceOver
/// reads the bar, pass 24); a progress line, the download button while there are downloads, the find field in the address box's place, and
/// the status bubble at the foot. A runtime target class (<c>NeonSidekickBrowserBar</c>) maps the clicks, the box's Enter and its Esc back to
/// the window. The smallest size keeps the bar whole (pass 26).
/// </summary>
internal sealed partial class MacBrowserWindow
{
    private const int ItemDownloads = 100;
    private const double StatusHeight = 20;

    private static readonly Dictionary<nint, MacBrowserWindow> s_bars = [];
    private static nint s_barClass;

    private readonly Dictionary<BrowserToolItem, nint> _buttons = [];
    private readonly Dictionary<BrowserToolItem, string> _symbols = [];
    private BrowserBarState _barState = BrowserBarState.Initial;
    private BrowserBarLayout? _layout;
    private nint _barTarget;
    private nint _address;
    private nint _padlock;
    private nint _findField;
    private nint _downloadButton;
    private nint _progress;
    private nint _status;
    private ViewerStyle _barStyle;

    private void MakeBar()
    {
        _barTarget = Send(Send(BarClass, Sel("alloc")), Sel("init"));
        s_bars[_barTarget] = this;
        foreach (var item in BrowserBarLayout.Buttons)
        {
            _buttons[item] = NewButton((long)item);
        }

        // Made by initWithFrame: and given their image after (the first run: buttonWithImage:… with no image throws, and an
        // Objective-C exception ends the process, pass 27).
        _downloadButton = NewButton(ItemDownloads);
        SendVoid(_downloadButton, Sel("setImage:"), Symbol(MacBrowserRules.DownloadSymbol, BrowserText.DownloadsName));
        SendVoid(_downloadButton, Sel("setToolTip:"), NSString(BrowserText.DownloadsName));
        SendVoidBool(_downloadButton, Sel("setHidden:"), 1);
        SendVoid(View, Sel("addSubview:"), _downloadButton);

        _padlock = SendInitRect(Send(Class("NSImageView"), Sel("alloc")), Sel("initWithFrame:"), default);
        SendVoid(View, Sel("addSubview:"), _padlock);

        _address = SendInitRect(Send(Class("NSTextField"), Sel("alloc")), Sel("initWithFrame:"), default);
        SendVoidLong(_address, Sel("setBezelStyle:"), 1);   // NSTextFieldRoundedBezel
        SendVoidBool(_address, Sel("setBezeled:"), 1);
        SendVoidBool(_address, Sel("setUsesSingleLineMode:"), 1);
        SendVoidBool(Send(_address, Sel("cell")), Sel("setScrollable:"), 1);
        SendVoid(_address, Sel("setFont:"), SendFont(Class("NSFont"), Sel("systemFontOfSize:"), 13));
        SendVoid(_address, Sel("setTarget:"), _barTarget);
        SendVoid(_address, Sel("setAction:"), Sel("addressEntered:"));
        SendVoid(_address, Sel("setDelegate:"), _barTarget);
        SendVoid(View, Sel("addSubview:"), _address);

        _findField = SendInitRect(Send(Class("NSSearchField"), Sel("alloc")), Sel("initWithFrame:"), default);
        SendVoid(_findField, Sel("setPlaceholderString:"), NSString(BrowserText.FindPlaceholder));
        SendVoidBool(_findField, Sel("setSendsWholeSearchString:"), 1);
        SendVoid(_findField, Sel("setTarget:"), _barTarget);
        SendVoid(_findField, Sel("setAction:"), Sel("findEntered:"));
        SendVoid(_findField, Sel("setDelegate:"), _barTarget);
        SendVoidBool(_findField, Sel("setHidden:"), 1);
        SendVoid(View, Sel("addSubview:"), _findField);

        _progress = SendInitRect(Send(Class("NSView"), Sel("alloc")), Sel("initWithFrame:"), default);
        SendVoidBool(_progress, Sel("setWantsLayer:"), 1);
        SendVoidBool(_progress, Sel("setHidden:"), 1);
        SendVoid(View, Sel("addSubview:"), _progress);

        _status = Send(Send(Class("NSTextField"), Sel("labelWithString:"), NSString("")), Sel("retain"));
        SendVoidBool(_status, Sel("setDrawsBackground:"), 1);
        SendVoid(_status, Sel("setFont:"), SendFont(Class("NSFont"), Sel("systemFontOfSize:"), 11));
        SendVoidLong(_status, Sel("setLineBreakMode:"), 5);   // NSLineBreakByTruncatingMiddle
        SendVoidBool(_status, Sel("setHidden:"), 1);
        SendVoid(View, Sel("addSubview:"), _status);   // after the view: drawn over the page's foot

        // The smallest window keeps the bar whole: the field at its least width (pass 26).
        var least = BrowserToolbar.Layout(0, 96);
        SendVoidSize(Window, Sel("setContentMinSize:"), least.Open.Right + BrowserToolbar.Margin + BrowserToolbar.ButtonSize + BrowserToolbar.ButtonGap, 240);
        RefreshBar();
    }

    // A borderless image button on the bar, its tag the item, its clicks to the bar's target (retained).
    private nint NewButton(long tag)
    {
        nint button = SendInitRect(Send(Class("NSButton"), Sel("alloc")), Sel("initWithFrame:"), new CGRect(0, 0, BrowserToolbar.ButtonSize, BrowserToolbar.ButtonSize));
        SendVoid(button, Sel("setTitle:"), NSString(""));
        SendVoidBool(button, Sel("setBordered:"), 0);
        SendVoidLong(button, Sel("setImagePosition:"), 1);   // NSImageOnly
        SendVoidLong(button, Sel("setTag:"), tag);
        SendVoid(button, Sel("setTarget:"), _barTarget);
        SendVoid(button, Sel("setAction:"), Sel("clicked:"));
        SendVoid(View, Sel("addSubview:"), button);
        return button;
    }

    private void FreeBar()
    {
        foreach (var button in _buttons.Values)
        {
            SendVoid(button, Sel("setTarget:"), 0);
            SendVoid(button, Sel("release"));
        }

        _buttons.Clear();
        foreach (nint control in (nint[])[_downloadButton, _address, _findField])
        {
            if (control != 0)
            {
                SendVoid(control, Sel("setTarget:"), 0);
            }
        }

        foreach (nint field in (nint[])[_address, _findField])
        {
            if (field != 0)
            {
                SendVoid(field, Sel("setDelegate:"), 0);
            }
        }

        foreach (nint owned in (nint[])[_downloadButton, _padlock, _address, _findField, _progress, _status])
        {
            if (owned != 0)
            {
                SendVoid(owned, Sel("release"));
            }
        }

        _downloadButton = _padlock = _address = _findField = _progress = _status = 0;
        if (_barTarget != 0)
        {
            s_bars.Remove(_barTarget);
            SendVoid(_barTarget, Sel("release"));
            _barTarget = 0;
        }
    }

    /// <summary>Everything placed for the window's size: the bar across the top, the page under it, the bubble at its foot.</summary>
    private void LayOut()
    {
        if (!Alive || _address == 0)
        {
            return;
        }

        var (width, height) = ClientSize;
        bool downloads = _downloads.Count > 0;
        double extra = downloads ? BrowserToolbar.ButtonSize + BrowserToolbar.ButtonGap : 0;
        var layout = BrowserToolbar.Layout((int)Math.Max(0, width - extra), 96);
        _layout = layout;
        WithoutAnimation(() =>
        {
            foreach (var (item, button) in _buttons)
            {
                SendVoidRect(button, Sel("setFrame:"), Rect(layout[item]));
            }

            SendVoidBool(_downloadButton, Sel("setHidden:"), (byte)(downloads ? 0 : 1));
            if (downloads)
            {
                SendVoidRect(_downloadButton, Sel("setFrame:"), new CGRect(layout.Open.Right + BrowserToolbar.ButtonGap, layout.Open.Top, BrowserToolbar.ButtonSize, BrowserToolbar.ButtonSize));
            }

            var padlock = layout.Padlock;
            SendVoidRect(_padlock, Sel("setFrame:"), new CGRect(padlock.Left + 4, padlock.Top + 3, padlock.Width - 8, padlock.Height - 6));
            const double FieldHeight = 24;
            var field = new CGRect(layout.Padlock.Right, layout.Field.Top + ((layout.Field.Height - FieldHeight) / 2), layout.Field.Right - layout.Padlock.Right, FieldHeight);
            SendVoidRect(_address, Sel("setFrame:"), field);
            SendVoidRect(_findField, Sel("setFrame:"), field);
            double top = layout.Height;
            SendVoidRect(_web, Sel("setFrame:"), new CGRect(0, top, width, Math.Max(0, height - top)));
            PlaceProgress();
            PlaceStatus();
        });
    }

    private static CGRect Rect(BarRect r) => new(r.Left, r.Top, r.Width, r.Height);

    private void PlaceProgress()
    {
        if (_layout is null || _progress == 0)
        {
            return;
        }

        var (width, _) = ClientSize;
        double fraction = _barState.Loading ? Math.Clamp(MacBrowserKit.Progress(_web), 0.05, 1) : 0;
        SendVoidBool(_progress, Sel("setHidden:"), (byte)(fraction > 0 ? 0 : 1));
        SendVoidRect(_progress, Sel("setFrame:"), new CGRect(0, _layout.Height - 2, width * fraction, 2));
    }

    private void PlaceStatus()
    {
        if (_status == 0)
        {
            return;
        }

        var (width, height) = ClientSize;
        SendVoid(_status, Sel("sizeToFit"));
        var fitted = SendRect(_status, Sel("frame"));
        double w = Math.Min(fitted.Width + 8, Math.Max(80, width * 0.6));
        SendVoidRect(_status, Sel("setFrame:"), new CGRect(0, height - StatusHeight, w, StatusHeight));
    }

    /// <summary>The bar drawn for where the page is: the history, the load, the address, the padlock, the saved star.</summary>
    private void RefreshBar()
    {
        if (_web == 0 || _address == 0)
        {
            return;
        }

        string? source = Source;
        bool sourceChanged = !string.Equals(source, _barState.Source, StringComparison.Ordinal);
        _barState = _barState with
        {
            CanBack = MacBrowserKit.CanGoBack(_web),
            CanForward = MacBrowserKit.CanGoForward(_web),
            Loading = MacBrowserKit.Loading(_web),
            Source = source,
        };
        foreach (var (item, button) in _buttons)
        {
            string symbol = MacBrowserRules.Symbol(item, _barState);
            string name = _barState.Name(item);
            if (!_symbols.TryGetValue(item, out var shown) || shown != symbol)
            {
                _symbols[item] = symbol;
                SendVoid(button, Sel("setImage:"), Symbol(symbol, name));
                SendVoid(button, Sel("setToolTip:"), NSString(name));
            }

            SendVoidBool(button, Sel("setEnabled:"), (byte)(_barState.Enabled(item) ? 1 : 0));
        }

        string padlock = MacBrowserRules.Symbol(BrowserToolItem.Padlock, _barState);
        string padlockName = _barState.Lock == BrowserLock.Secure ? BrowserText.SecureTip : _barState.Lock == BrowserLock.NotSecure ? BrowserText.NotSecureTip : "";
        SendVoid(_padlock, Sel("setImage:"), padlock.Length == 0 ? 0 : Symbol(padlock, padlockName));
        SendVoid(_padlock, Sel("setToolTip:"), padlockName.Length == 0 ? 0 : NSString(padlockName));
        if (sourceChanged)
        {
            ShowAddress(source);
        }

        PlaceProgress();
    }

    /// <summary>An SF Symbol's image with <paramref name="name"/> as its accessibility description (autoreleased); 0 for none.</summary>
    private static nint Symbol(string symbol, string name) =>
        symbol.Length == 0 ? 0 : Send(Class("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"), NSString(symbol), NSString(name));

    private void ApplyBarStyle(ViewerStyle style)
    {
        _barStyle = style;
        WithoutAnimation(() =>
        {
            nint caption = CGColor(style.Caption);
            SendVoid(RootLayer, Sel("setBackgroundColor:"), caption);
            CGColorRelease(caption);
            if (_progress != 0)
            {
                nint accent = CGColor(style.Border);
                SendVoid(Send(_progress, Sel("layer")), Sel("setBackgroundColor:"), accent);
                CGColorRelease(accent);
            }
        });

        foreach (nint button in _buttons.Values.Append(_downloadButton))
        {
            if (button != 0)
            {
                SendVoid(button, Sel("setContentTintColor:"), NSColor(style.CaptionText));
            }
        }

        if (_padlock != 0)
        {
            SendVoid(_padlock, Sel("setContentTintColor:"), NSColor(style.CaptionText));
        }

        if (_status != 0)
        {
            SendVoid(_status, Sel("setBackgroundColor:"), NSColor(style.Background));
            SendVoid(_status, Sel("setTextColor:"), NSColor(style.Text));
        }
    }

    // ── The address box ───────────────────────────────────────────────────

    /// <summary>Whether the address box or the find field has the keyboard (the window's field editor is theirs).</summary>
    private bool EditingInBar() => EditingIn(_address) || (_finding && EditingIn(_findField));

    private bool EditingIn(nint field)
    {
        if (field == 0 || !Alive)
        {
            return false;
        }

        nint responder = Send(Window, Sel("firstResponder"));
        return responder != 0 && (responder == field || (MacWebKit.SendBoolWith(responder, Sel("isKindOfClass:"), Class("NSTextView")) != 0 && Send(responder, Sel("delegate")) == field));
    }

    /// <summary>The box shows the page's address, unless the user is typing in it.</summary>
    private void ShowAddress(string? source, bool force = false)
    {
        if (_address != 0 && (force || !EditingIn(_address)))
        {
            SendVoid(_address, Sel("setStringValue:"), NSString(BrowserAddress.Shown(source)));
            _barState = _barState with { Typed = BrowserAddress.Shown(source).Length > 0 };
        }
    }

    private void FocusAddress()
    {
        if (_finding)
        {
            CloseFind();
        }

        SendVoid(Window, Sel("makeFirstResponder:"), _address);
        SendVoid(_address, Sel("selectText:"), 0);
    }

    // Enter in the box: an address, or words searched; a refused one beeps and stays for the user to change.
    private void AddressEntered()
    {
        string text = FromNSString(Send(_address, Sel("stringValue"))) ?? "";
        var go = BrowserAddress.Resolve(text, MacBrowserWindows.SearchUrl);
        if (go.Refusal is { } refusal)
        {
            Beep();
            DiagnosticLog.Info("Browser", refusal + ".");
            return;
        }

        if (go.Url is { } url)
        {
            Go(url.AbsoluteUri);
            SendVoid(Window, Sel("makeFirstResponder:"), _web);
        }
    }

    [LibraryImport(AppKitPath)]
    private static partial void NSBeep();

    private static void Beep() => NSBeep();

    // Esc in the box: the page's address back, the keyboard to the page.
    private void AddressCancelled()
    {
        ShowAddress(Source, force: true);
        SendVoid(Window, Sel("makeFirstResponder:"), _web);
    }

    private void Clicked(long tag)
    {
        if (tag == ItemDownloads)
        {
            OpenDownloadMenu();
            return;
        }

        var item = (BrowserToolItem)tag;
        if (!_barState.Enabled(item))
        {
            return;
        }

        if (_full.Page && item is BrowserToolItem.History or BrowserToolItem.Links)
        {
            LeavePageFullScreen();
        }

        switch (item)
        {
            case BrowserToolItem.Back:
                SendVoid(_web, Sel("goBack"));
                break;
            case BrowserToolItem.Forward:
                SendVoid(_web, Sel("goForward"));
                break;
            case BrowserToolItem.Reload when _barState.Loading:
                SendVoid(_web, Sel("stopLoading"));
                break;
            case BrowserToolItem.Reload:
                Reload();
                break;
            case BrowserToolItem.Home:
                Go(MacBrowserWindows.HomeAddress());
                break;
            case BrowserToolItem.History:
                OpenHistoryMenu();
                break;
            case BrowserToolItem.Links:
                OpenLinksMenu();
                break;
            case BrowserToolItem.Go:
                AddressEntered();
                break;
            case BrowserToolItem.Open:
                OpenOutside();
                break;
        }
    }

    // The page in the user's own browser: web addresses only. A staged PDF: its original file in its own program.
    private void OpenOutside()
    {
        string? source = Source;
        if (PdfView.OriginalOf(source) is { } original)
        {
            MacBrowserWindows.OpenFile(original);
            return;
        }

        if (VideoPage.IsWebLink(source) && !PdfView.IsOurs(source))
        {
            MacBrowserWindows.OpenLink(source!);
        }
    }

    // ── The status bubble ─────────────────────────────────────────────────

    public void Hovered(string href)
    {
        if (_status == 0)
        {
            return;
        }

        string text = MacBrowserRules.Hover(href);
        SendVoid(_status, Sel("setStringValue:"), NSString(text));
        SendVoidBool(_status, Sel("setHidden:"), (byte)(text.Length == 0 ? 1 : 0));
        if (text.Length > 0)
        {
            PlaceStatus();
        }
    }

    // ── The menus ─────────────────────────────────────────────────────────

    // A menu under a bar item, its command run once AppKit's menu loop is over (Windows' posted choice).
    private int? PopUnder(BarRect under, IReadOnlyList<ContextMenuItem> rows) =>
        MacPictureMenu.Show(View, rows, new CGPoint(under.Left, under.Bottom + 2));

    private void OpenHistoryMenu()
    {
        if (_layout is null)
        {
            return;
        }

        var shown = _history;
        var rows = BrowserHistory.MenuItems(shown, TimeProvider.System.GetUtcNow(), TimeZoneInfo.Local);
        if (PopUnder(_layout.History, rows) is not { } command || !Alive)
        {
            return;
        }

        if (command == BrowserHistory.ClearCommand)
        {
            ForgetHistory();
            DiagnosticLog.Info("Browser", "The Browser window's history cleared (its menu).");
            return;
        }

        if (command >= 1 && command <= shown.Count)
        {
            Go(shown[command - 1].Url);
            FocusPage();
        }
    }

    private void OpenLinksMenu()
    {
        if (_layout is null)
        {
            return;
        }

        var links = _links.List();
        string? page = Source;
        var rows = BrowserLinks.MenuItems(links, page, writable: _links.Writable());
        if (PopUnder(_layout.Links, rows) is not { } command || !Alive)
        {
            return;
        }

        if (command == BrowserLinks.ToggleCommand)
        {
            string? title = string.Equals(page, Source, StringComparison.Ordinal) ? _pageTitle : null;
            ToggleSaved(page, title);
            return;
        }

        int index = command - BrowserLinks.RowBase;
        if (index >= 0 && index < links.Count)
        {
            Go(links[index].Url);
            FocusPage();
        }
    }

    private void OpenDownloadMenu()
    {
        if (_layout is null)
        {
            return;
        }

        var rows = DownloadRows();
        var at = new BarRect(_layout.Open.Right + BrowserToolbar.ButtonGap, _layout.Open.Top, _layout.Open.Right + BrowserToolbar.ButtonGap + BrowserToolbar.ButtonSize, _layout.Open.Bottom);
        if (PopUnder(at, MacBrowserRules.DownloadMenu(rows)) is { } command && Alive && MacBrowserRules.DownloadCommand(command) is { } chosen)
        {
            RunDownloadCommand(chosen.Id, chosen.Action);
        }
    }

    // ── The bar's runtime class ───────────────────────────────────────────

    private static nint BarClass
    {
        get
        {
            if (s_barClass != 0)
            {
                return s_barClass;
            }

            nint cls = objc_allocateClassPair(Class("NSObject"), "NeonSidekickBrowserBar", 0);
            unsafe
            {
                class_addMethod(cls, Sel("clicked:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnClicked, "v@:@");
                class_addMethod(cls, Sel("addressEntered:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnAddressEntered, "v@:@");
                class_addMethod(cls, Sel("findEntered:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnFindEntered, "v@:@");
                class_addMethod(cls, Sel("controlTextDidChange:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnTextChanged, "v@:@");
                class_addMethod(cls, Sel("control:textView:doCommandBySelector:"), (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, byte>)&OnCommand, "B@:@@:");
            }

            nint protocol = objc_getProtocol("NSTextFieldDelegate");
            if (protocol != 0)
            {
                class_addProtocol(cls, protocol);
            }

            objc_registerClassPair(cls);
            s_barClass = cls;
            return cls;
        }
    }

    private static void Bar(nint self, Action<MacBrowserWindow> act)
    {
        try
        {
            if (s_bars.TryGetValue(self, out var window) && window.Alive)
            {
                act(window);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", "The Browser window's bar failed.", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnClicked(nint self, nint selector, nint sender) => Bar(self, w => w.Clicked(SendLong(sender, Sel("tag"))));

    [UnmanagedCallersOnly]
    private static void OnAddressEntered(nint self, nint selector, nint sender) => Bar(self, static w => w.AddressEntered());

    [UnmanagedCallersOnly]
    private static void OnFindEntered(nint self, nint selector, nint sender) => Bar(self, static w => w.FindEntered());

    [UnmanagedCallersOnly]
    private static void OnTextChanged(nint self, nint selector, nint notification) => Bar(self, w =>
    {
        if (Send(notification, Sel("object")) == w._address)
        {
            w._barState = w._barState with { Typed = (FromNSString(Send(w._address, Sel("stringValue"))) ?? "").Length > 0 };
            w.RefreshBar();
        }
    });

    // Esc in either field, Shift+Enter in the find field: the field's editor asks here before doing its own.
    [UnmanagedCallersOnly]
    private static byte OnCommand(nint self, nint selector, nint control, nint textView, nint command)
    {
        byte taken = 0;
        Bar(self, w =>
        {
            if (command == Sel("cancelOperation:"))
            {
                if (control == w._findField)
                {
                    w.CloseFind();
                }
                else
                {
                    w.AddressCancelled();
                }

                taken = 1;
            }
            else if (command == Sel("insertNewline:") && control == w._findField)
            {
                bool shift = (SendULong(Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("currentEvent")), Sel("modifierFlags")) & MacKeys.ShiftFlag) != 0;
                w.FindAgain(backwards: shift);
                taken = 1;
            }
        });
        return taken;
    }
}
