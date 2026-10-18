using System.Runtime.Versioning;
using NeonSidekick.Images;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture menu on a Mac (2026-10-07, Stage 2 phase 2: the user's pick, a native NSMenu rather than <see cref="ContextMenuWindow"/>'s
/// themed popup redrawn): the rows <see cref="PictureMenu.Build"/> makes, as NSMenuItems with their submenus, the greyed rows disabled,
/// every row's tag its <see cref="PictureCommand"/>. It wears the window's appearance (dark or light), and the keyboard, the type-to-select
/// and the submenus are AppKit's own. AppKit's menu is modal: <see cref="Show"/> returns the row chosen, and the window runs it then, as
/// Windows' menu posts its choice after it closes. Main thread only; excluded from coverage with the AppKit layer.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacPictureMenu
{
    /// <summary>
    /// The menu for the picture at <paramref name="path"/> over <paramref name="view"/>: at the mouse event <paramref name="mouse"/> (a
    /// right-click), or, with none, at <paramref name="at"/> in the view (the keyboard's Shift+F10). The command chosen, or null.
    /// </summary>
    public static PictureCommand? Show(nint view, nint mouse, CGPoint at, bool thumbs, string path)
    {
        nint target = Send(Send(AppKitClasses.MenuTargetClass, Sel("alloc")), Sel("init"));
        nint menu = Build(PictureMenu.BuildNow(thumbs, path), target);
        try
        {
            IsOpen = true;
            AppKitClasses.MenuChosen = null;
            if (mouse != 0)
            {
                SendVoid(Class("NSMenu"), Sel("popUpContextMenu:withEvent:forView:"), menu, mouse, view);
            }
            else
            {
                SendPopUp(menu, Sel("popUpMenuPositioningItem:atLocation:inView:"), 0, at, view);
            }

            long? chosen = AppKitClasses.MenuChosen;
            AppKitClasses.MenuChosen = null;
            return chosen is long tag && tag > 0 ? (PictureCommand)(tag - 1) : null;
        }
        finally
        {
            IsOpen = false;
            SendVoid(menu, Sel("release"));
            SendVoid(target, Sel("release"));
        }
    }

    /// <summary>
    /// Any rows as a menu popped at <paramref name="at"/> in <paramref name="view"/> (2026-10-18, the Browser window's history, saved links and
    /// downloads: already <see cref="ContextMenuItem"/>s, pass 1): the command of the row chosen, or null. The same build as the picture
    /// menu's, in AppKit's own tracking loop, which the main queue still runs through.
    /// </summary>
    public static int? Show(nint view, IReadOnlyList<ContextMenuItem> rows, CGPoint at)
    {
        ArgumentNullException.ThrowIfNull(rows);
        nint target = Send(Send(AppKitClasses.MenuTargetClass, Sel("alloc")), Sel("init"));
        nint menu = Build(rows, target);
        try
        {
            IsOpen = true;
            AppKitClasses.MenuChosen = null;
            SendPopUp(menu, Sel("popUpMenuPositioningItem:atLocation:inView:"), 0, at, view);
            long? chosen = AppKitClasses.MenuChosen;
            AppKitClasses.MenuChosen = null;
            return chosen is long tag && tag > 0 ? (int)(tag - 1) : null;
        }
        finally
        {
            IsOpen = false;
            SendVoid(menu, Sel("release"));
            SendVoid(target, Sel("release"));
        }
    }

    /// <summary>
    /// <see cref="Show(nint, IReadOnlyList{ContextMenuItem}, CGPoint)"/>'s rows built and a row's choice read back through the target, nothing
    /// shown (the smoke's <c>browser:webkit</c>): the count of top rows and whether the choice came back as <paramref name="pick"/>'s command.
    /// </summary>
    public static (long Rows, bool Chose) ProbeRows(IReadOnlyList<ContextMenuItem> rows, int pick)
    {
        ArgumentNullException.ThrowIfNull(rows);
        nint target = Send(Send(AppKitClasses.MenuTargetClass, Sel("alloc")), Sel("init"));
        nint menu = Build(rows, target);
        try
        {
            long count = SendLong(menu, Sel("numberOfItems"));
            int index = rows.ToList().FindIndex(r => r.Command == pick);
            bool chose = false;
            if (index >= 0)
            {
                AppKitClasses.MenuChosen = null;
                SendVoid(target, Sel("chosen:"), SendIndex(menu, Sel("itemAtIndex:"), (nuint)index));
                chose = AppKitClasses.MenuChosen == pick + 1L;
                AppKitClasses.MenuChosen = null;
            }

            return (count, chose);
        }
        finally
        {
            SendVoid(menu, Sel("release"));
            SendVoid(target, Sel("release"));
        }
    }

    /// <summary>Whether a picture menu is up (its loop runs the main queue meanwhile): the slide show holds while it is, as on Windows.</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>Finder opened on the picture's folder with it selected (the Mac's Show in Explorer): NSWorkspace's own call, no process of ours.</summary>
    public static void ShowInFinder(string path)
    {
        nint url = Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path));
        nint urls = Send(Class("NSArray"), Sel("arrayWithObject:"), url);
        SendVoid(Send(Class("NSWorkspace"), Sel("sharedWorkspace")), Sel("activateFileViewerSelectingURLs:"), urls);
    }

    /// <summary>
    /// <c>viewer:menu</c> on a Mac: a picture's menu built as NSMenu items on the main thread (submenus, greyed rows, the target), its
    /// rows counted against <see cref="PictureMenu.Build"/>'s, the target's <c>chosen:</c> run through the runtime as a click would,
    /// and let go. Nothing is shown.
    /// </summary>
    public static (bool Ok, string Detail) Probe()
    {
        if (!AppKitHost.IsEnabled)
        {
            return (true, HasWindowServer() ? "skipped: no AppKit host here (the app's main thread runs it)" : "skipped: no window server");
        }

        if (!AppKitHost.Invoke(() =>
        {
            var rows = PictureMenu.Build(thumbs: true, "/tmp/probe.png", "beside-original");
            nint target = Send(Send(AppKitClasses.MenuTargetClass, Sel("alloc")), Sel("init"));
            nint menu = Build(rows, target);
            try
            {
                long count = SendLong(menu, Sel("numberOfItems"));
                nint rotate = SendIndex(menu, Sel("itemAtIndex:"), (nuint)rows.ToList().FindIndex(r => r.HasChildren));
                long subCount = SendLong(Send(rotate, Sel("submenu")), Sel("numberOfItems"));
                nint first = SendIndex(menu, Sel("itemAtIndex:"), 0);
                AppKitClasses.MenuChosen = null;
                SendVoid(target, Sel("chosen:"), first);
                bool chose = AppKitClasses.MenuChosen == (long)PictureCommand.OpenInViewer + 1;
                AppKitClasses.MenuChosen = null;
                bool ok = count == rows.Count && subCount > 0 && chose;
                return (ok, $"an NSMenu of {count} rows ({rows.Count} built) with submenus; a row's choice reached the app's target{(chose ? "" : " (it did not)")}");
            }
            finally
            {
                SendVoid(menu, Sel("release"));
                SendVoid(target, Sel("release"));
            }
        }, out (bool Ok, string Detail) result))
        {
            return (false, "the main thread did not answer");
        }

        return result;
    }

    // The rows as an NSMenu (retained: the caller releases it), submenus within; nothing enabled by AppKit's rules, only by the rows'.
    private static nint Build(IReadOnlyList<ContextMenuItem> items, nint target)
    {
        nint menu = Send(Send(Class("NSMenu"), Sel("alloc")), Sel("initWithTitle:"), NSString(""));
        SendVoidBool(menu, Sel("setAutoenablesItems:"), 0);
        foreach (var row in items)
        {
            if (row.IsSeparator)
            {
                SendVoid(menu, Sel("addItem:"), Send(Class("NSMenuItem"), Sel("separatorItem")));
                continue;
            }

            nint action = row.HasChildren || row.Command == 0 ? 0 : Sel("chosen:");
            nint item = Send(Send(Class("NSMenuItem"), Sel("alloc")), Sel("initWithTitle:action:keyEquivalent:"), NSString(row.Label), action, NSString(""));
            SendVoidLong(item, Sel("setTag:"), row.Command + 1L);   // 0 is no command: the tag is one past it
            SendVoidBool(item, Sel("setEnabled:"), (byte)(row.Enabled ? 1 : 0));
            if (action != 0)
            {
                SendVoid(item, Sel("setTarget:"), target);
            }

            if (row.HasChildren)
            {
                nint sub = Build(row.Children!, target);
                SendVoid(item, Sel("setSubmenu:"), sub);
                SendVoid(sub, Sel("release"));
            }

            SendVoid(menu, Sel("addItem:"), item);
            SendVoid(item, Sel("release"));
        }

        return menu;
    }
}
