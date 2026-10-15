namespace NeonSidekick.Viewer;

/// <summary>A place on the Browser window's bar (<see cref="BrowserToolbar"/>): the six buttons, the padlock and the address box.</summary>
public enum BrowserToolItem
{
    Back,
    Forward,

    /// <summary>Reload, or stop while a page loads (<see cref="BrowserBarState.Loading"/>).</summary>
    Reload,
    Home,

    /// <summary>The history menu (later on 2026-10-13): the window's own list of visits.</summary>
    History,

    /// <summary>The saved links' menu (later still on 2026-10-13): ☆, or ★ while the page is saved (<see cref="BrowserBarState.Saved"/>).</summary>
    Links,

    /// <summary>The https / not-secure mark before the address; drawn, never clicked.</summary>
    Padlock,

    /// <summary>The address box: the window's EDIT child sits here, so a click there is the box's, not the bar's.</summary>
    Address,
    Go,
    Open,
}

/// <summary>What the padlock shows (<see cref="BrowserBarState.Lock"/>).</summary>
public enum BrowserLock
{
    /// <summary>Nothing: the blank page, no page yet.</summary>
    None,

    /// <summary>An https page.</summary>
    Secure,

    /// <summary>An http page.</summary>
    NotSecure,
}

/// <summary>A rectangle on the bar, in client pixels, right and bottom exclusive (a Win32 RECT's sense).</summary>
public readonly record struct BarRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// Where everything on the bar is for one client width and DPI (<see cref="BrowserToolbar.Layout"/>): the strip's height, each
/// button's square, the framed field (the padlock's slot and the address box inside its 1 px frame). Pure.
/// </summary>
public sealed record BrowserBarLayout(int Height, BarRect Back, BarRect Forward, BarRect Reload, BarRect Home, BarRect History, BarRect Links, BarRect Field, BarRect Padlock, BarRect Address, BarRect Go, BarRect Open)
{
    /// <summary>The buttons left to right: what a click can land on.</summary>
    public static readonly IReadOnlyList<BrowserToolItem> Buttons = [BrowserToolItem.Back, BrowserToolItem.Forward, BrowserToolItem.Reload, BrowserToolItem.Home, BrowserToolItem.History, BrowserToolItem.Links, BrowserToolItem.Go, BrowserToolItem.Open];

    public BarRect this[BrowserToolItem item] => item switch
    {
        BrowserToolItem.Back => Back,
        BrowserToolItem.Forward => Forward,
        BrowserToolItem.Reload => Reload,
        BrowserToolItem.Home => Home,
        BrowserToolItem.History => History,
        BrowserToolItem.Links => Links,
        BrowserToolItem.Padlock => Padlock,
        BrowserToolItem.Address => Address,
        BrowserToolItem.Go => Go,
        _ => Open,
    };

    /// <summary>The button under a client point, else null (a gap, the field, below the bar).</summary>
    public BrowserToolItem? HitTest(int x, int y)
    {
        foreach (var item in Buttons)
        {
            if (this[item].Contains(x, y))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>The address box's EDIT child for a line <paramref name="lineHeight"/> high: the box's width, centred in it top to bottom.</summary>
    public BarRect EditFor(int lineHeight)
    {
        int height = Math.Clamp(lineHeight, 1, Address.Height);
        int top = Address.Top + (Address.Height - height) / 2;
        return Address with { Top = top, Bottom = top + height };
    }
}

/// <summary>
/// What the bar shows for where the page is: back and forward by its history, reload or stop by whether it loads, the padlock by its
/// address, Go while the box has text, Open on a web page. Pure.
/// </summary>
public sealed record BrowserBarState(bool CanBack, bool CanForward, bool Loading, string? Source, bool Typed)
{
    /// <summary>Whether the page is one of the saved links (later on 2026-10-13): the links button draws a filled star.</summary>
    public bool Saved { get; init; }

    public static BrowserBarState Initial { get; } = new(false, false, false, null, false);

    public BrowserLock Lock =>
        Uri.TryCreate(Source, UriKind.Absolute, out var url) && url.Host.Length > 0
            ? url.Scheme == Uri.UriSchemeHttps ? BrowserLock.Secure : url.Scheme == Uri.UriSchemeHttp ? BrowserLock.NotSecure : BrowserLock.None
            : BrowserLock.None;

    public bool Enabled(BrowserToolItem item) => item switch
    {
        BrowserToolItem.Back => CanBack,
        BrowserToolItem.Forward => CanForward,
        BrowserToolItem.Go => Typed,
        BrowserToolItem.Open => VideoPage.IsWebLink(Source),
        BrowserToolItem.Padlock => Lock != BrowserLock.None,
        _ => true,
    };

    /// <summary>The Segoe MDL2 Assets glyph an item draws ("" for the address box, and for the padlock with nothing to say).</summary>
    public string Glyph(BrowserToolItem item) => item switch
    {
        BrowserToolItem.Back => BrowserToolbar.BackGlyph,
        BrowserToolItem.Forward => BrowserToolbar.ForwardGlyph,
        BrowserToolItem.Reload => Loading ? BrowserToolbar.StopGlyph : BrowserToolbar.ReloadGlyph,
        BrowserToolItem.Home => BrowserToolbar.HomeGlyph,
        BrowserToolItem.History => BrowserToolbar.HistoryGlyph,
        BrowserToolItem.Links => Saved ? BrowserToolbar.SavedGlyph : BrowserToolbar.LinksGlyph,
        BrowserToolItem.Padlock => Lock switch
        {
            BrowserLock.Secure => BrowserToolbar.LockGlyph,
            BrowserLock.NotSecure => BrowserToolbar.NotSecureGlyph,
            _ => "",
        },
        BrowserToolItem.Go => BrowserToolbar.GoGlyph,
        BrowserToolItem.Open => BrowserToolbar.OpenGlyph,
        _ => "",
    };

    /// <summary>An item's name, its tooltip's words (<see cref="BrowserText"/>).</summary>
    public string Name(BrowserToolItem item) => item switch
    {
        BrowserToolItem.Back => BrowserText.BackName,
        BrowserToolItem.Forward => BrowserText.ForwardName,
        BrowserToolItem.Reload => Loading ? BrowserText.StopName : BrowserText.ReloadName,
        BrowserToolItem.Home => BrowserText.HomeName,
        BrowserToolItem.History => BrowserText.HistoryName,
        BrowserToolItem.Links => BrowserText.LinksName,
        BrowserToolItem.Padlock => Lock == BrowserLock.Secure ? BrowserText.SecureName : Lock == BrowserLock.NotSecure ? BrowserText.NotSecureName : "",
        BrowserToolItem.Go => BrowserText.GoName,
        // A staged PDF (2026-10-14, the PDF plan's gap 76): the button opens the file in its own program.
        BrowserToolItem.Open => PdfView.IsOurs(Source) ? BrowserText.OpenFileName : BrowserText.OpenName,
        _ => "",
    };
}

/// <summary>
/// The Browser window's navigation bar (2026-10-13, the user's ask, "the full monty"): back, forward, reload (stop while a page loads)
/// and home, the address box with its padlock and Go, and open in the default browser — <c>[←][→][⟳][⌂]  [🔒 address…][Go]  [↗]</c>;
/// the history menu's button after home since later that day (<c>[⌂][🕘]</c>), and the saved links' after it later still (<c>[🕘][☆]</c>).
/// The window draws it with GDI across its top (the log window's and the picture menu's way: a memory DC, Segoe UI at the window's DPI,
/// <see cref="ViewerStyle"/>'s colours) and puts a native EDIT child in the address box; WebView2 sits below it. The glyphs are Segoe
/// MDL2 Assets', Edge's own, on Windows 10 and 11. This class is the layout, the state's glyphs and the pointer's hover and press, with
/// no drawing: pure, portable, pinned.
/// </summary>
public sealed class BrowserToolbar
{
    public const string BackGlyph = "\uE72B";
    public const string ForwardGlyph = "\uE72A";
    public const string ReloadGlyph = "\uE72C";
    public const string StopGlyph = "\uE711";
    public const string HomeGlyph = "\uE80F";
    public const string HistoryGlyph = "\uE81C";
    public const string LinksGlyph = "\uE734";   // FavoriteStar
    public const string SavedGlyph = "\uE735";   // FavoriteStarFill
    public const string LockGlyph = "\uE72E";
    public const string NotSecureGlyph = "\uE7BA";
    public const string GoGlyph = "\uE751";
    public const string OpenGlyph = "\uE8A7";

    /// <summary>The glyphs' font.</summary>
    public const string GlyphFace = "Segoe MDL2 Assets";

    /// <summary>The sizes at 96 DPI: the strip, a button's square, the gaps, the field's height, the padlock's slot, the box's least width.</summary>
    public const int BarHeight = 40;
    public const int ButtonSize = 32;
    public const int Margin = 4;
    public const int ButtonGap = 2;
    public const int GroupGap = 10;
    public const int FieldHeight = 28;
    public const int PadlockWidth = 28;
    public const int MinAddressWidth = 120;

    /// <summary>The item under the pointer, drawn lit while it is enabled.</summary>
    public BrowserToolItem? Hot { get; private set; }

    /// <summary>The item the left button went down on, drawn pressed while the pointer stays on it.</summary>
    public BrowserToolItem? Pressed { get; private set; }

    /// <summary><paramref name="pixels"/> at 96 DPI scaled to <paramref name="dpi"/>, rounded.</summary>
    public static int Scale(int pixels, uint dpi) => (int)Math.Round(pixels * (double)Math.Max(dpi, 96u) / 96, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The bar for a client <paramref name="clientWidth"/> wide at <paramref name="dpi"/>: the four buttons from the left, Open at the
    /// right and Go beside the field, which takes what is left, at least <see cref="MinAddressWidth"/> (past that, Go and Open run off).
    /// </summary>
    public static BrowserBarLayout Layout(int clientWidth, uint dpi)
    {
        int height = Scale(BarHeight, dpi), size = Scale(ButtonSize, dpi), margin = Scale(Margin, dpi), gap = Scale(ButtonGap, dpi), group = Scale(GroupGap, dpi);
        int top = (height - size) / 2;
        BarRect Square(int left) => new(left, top, left + size, top + size);

        var back = Square(margin);
        var forward = Square(back.Right + gap);
        var reload = Square(forward.Right + gap);
        var home = Square(reload.Right + gap);
        var history = Square(home.Right + gap);
        var links = Square(history.Right + gap);
        int fieldLeft = links.Right + group;
        int padlockWidth = Scale(PadlockWidth, dpi), inset = Scale(4, dpi);
        int fieldRight = Math.Max(fieldLeft + 1 + padlockWidth + Scale(MinAddressWidth, dpi) + inset + 1, clientWidth - margin - size - group - size - gap);
        int fieldHeight = Scale(FieldHeight, dpi);
        int fieldTop = (height - fieldHeight) / 2;
        var field = new BarRect(fieldLeft, fieldTop, fieldRight, fieldTop + fieldHeight);
        var padlock = new BarRect(field.Left + 1, field.Top + 1, field.Left + 1 + padlockWidth, field.Bottom - 1);
        var address = new BarRect(padlock.Right, field.Top + 1, field.Right - 1 - inset, field.Bottom - 1);
        var go = Square(field.Right + gap);
        var open = Square(go.Right + group);
        return new BrowserBarLayout(height, back, forward, reload, home, history, links, field, padlock, address, go, open);
    }

    /// <summary>The pointer moved onto <paramref name="item"/> (null: off every button); true when the bar must be drawn again.</summary>
    public bool Move(BrowserToolItem? item)
    {
        if (item == Hot)
        {
            return false;
        }

        Hot = item;
        return true;
    }

    /// <summary>The pointer left the window; true when the bar must be drawn again.</summary>
    public bool Leave()
    {
        bool changed = Hot is not null;
        Hot = null;
        return changed;
    }

    /// <summary>The left button went down on <paramref name="item"/>, which counts only when it is enabled; true when the bar must be drawn again.</summary>
    public bool Down(BrowserToolItem? item, BrowserBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var pressed = item is { } on && state.Enabled(on) && on is not (BrowserToolItem.Padlock or BrowserToolItem.Address) ? item : null;
        bool changed = pressed != Pressed;
        Pressed = pressed;
        return changed;
    }

    /// <summary>
    /// The left button came up over <paramref name="item"/>: the click, when it is the button it went down on and that is still
    /// enabled (a page that loaded in between may have turned it off); else null. The press ends either way.
    /// </summary>
    public BrowserToolItem? Up(BrowserToolItem? item, BrowserBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var pressed = Pressed;
        Pressed = null;
        return pressed is { } down && item == down && state.Enabled(down) ? down : null;
    }

    /// <summary>The press dropped without a click (the capture lost).</summary>
    public bool Cancel()
    {
        bool changed = Pressed is not null;
        Pressed = null;
        return changed;
    }

    /// <summary>
    /// A COLORREF <paramref name="percent"/> of the way from <paramref name="from"/> to <paramref name="to"/>, channel by channel: a
    /// button's hover (the edge colour over the background) and its press, stronger. Pure.
    /// </summary>
    public static uint Blend(uint from, uint to, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        uint Channel(int shift)
        {
            int a = (int)((from >> shift) & 0xFF), b = (int)((to >> shift) & 0xFF);
            return (uint)(a + (int)Math.Round((b - a) * percent / 100.0, MidpointRounding.AwayFromZero)) << shift;
        }

        return Channel(0) | Channel(8) | Channel(16);
    }
}
