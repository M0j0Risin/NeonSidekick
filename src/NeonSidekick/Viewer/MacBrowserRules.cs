using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Web;

namespace NeonSidekick.Viewer;

/// <summary>A download the Mac's Browser window holds (<see cref="MacBrowserRules"/>, the bar's download menu): where it goes, how far, how it ended.</summary>
public sealed record MacDownloadRow(int Id, string Path, double Fraction, MacDownloadState State);

/// <summary>The Mac Browser window's last ends of its page's web process (<see cref="MacBrowserRules.ContentEnded"/>): where, when, how many in a row.</summary>
public readonly record struct MacContentEnds(string? At, TimeSpan When, int Count);

/// <summary>Where a download of the Mac's Browser window is.</summary>
public enum MacDownloadState
{
    Running,
    Finished,
    Failed,
    Cancelled,
}

/// <summary>
/// The Mac Browser window's decisions with no AppKit in them (2026-10-18, the Mac Browser plan, pass 20): one covered class beside the thin
/// native one (<c>MacBrowserWindow</c>), so what the window decides is tested where the window itself cannot be (no test gets the main
/// thread). The SF Symbol per bar item, <c>NSHTTPCookie</c>'s values as a <see cref="BrowserCookie"/>, a download's free name, the profile's
/// WebKit store id file, Safari's zoom steps, a snapshot's width for the backing scale, a live view's place as a point, and which failed
/// loads are no failure. Pure but for the store id's file.
/// </summary>
public static class MacBrowserRules
{
    /// <summary>The file in the profile folder naming the window's WebKit data store (pass 6): a UUID, made at the first open, moved with the folder.</summary>
    public const string StoreIdFileName = "webkit-browser.id";

    /// <summary>The file the open window holds with <see cref="FileShare.None"/> (pass 22): a second copy of the app on the same profile is refused.</summary>
    public const string LockFileName = "webkit-browser.lock";

    /// <summary>The download button's symbol and the find field's.</summary>
    public const string DownloadSymbol = "arrow.down.circle";

    /// <summary>
    /// Safari's zoom steps (⌘+ / ⌘-): from half to three times. <see cref="Zoom"/> walks them.
    /// </summary>
    public static IReadOnlyList<double> ZoomSteps { get; } = [0.5, 0.75, 0.85, 1.0, 1.15, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    /// <summary>The SF Symbol an item of the bar shows, by what <paramref name="state"/> says: stop while loading, a filled star on a saved page. "" for the address box.</summary>
    public static string Symbol(BrowserToolItem item, BrowserBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return item switch
        {
            BrowserToolItem.Back => "chevron.left",
            BrowserToolItem.Forward => "chevron.right",
            BrowserToolItem.Reload => state.Loading ? "xmark" : "arrow.clockwise",
            BrowserToolItem.Home => "house",
            BrowserToolItem.History => "clock",
            BrowserToolItem.Links => state.Saved ? "star.fill" : "star",
            BrowserToolItem.Padlock => state.Lock switch
            {
                BrowserLock.Secure => "lock.fill",
                BrowserLock.NotSecure => "lock.open",
                _ => "",
            },
            BrowserToolItem.Go => "arrow.right.circle",
            BrowserToolItem.Open => "arrow.up.forward.app",
            _ => "",
        };
    }

    /// <summary>
    /// An <c>NSHTTPCookie</c>'s values as a <see cref="BrowserCookie"/>: a domain cookie keeps its leading dot (WebKit gives it so) and a host
    /// one its bare host; a session cookie expires at -1, the others in seconds since 1970. Null for a cookie with no name or domain.
    /// </summary>
    public static BrowserCookie? Cookie(string? name, string? value, string? domain, string? path, double? expires, bool session, bool secure, bool httpOnly)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(domain))
        {
            return null;
        }

        double when = session || expires is not { } at || !double.IsFinite(at) ? -1 : at;
        return new BrowserCookie(name, value ?? "", domain, string.IsNullOrEmpty(path) ? "/" : path, when, secure, httpOnly);
    }

    /// <summary>
    /// Where a download goes in <paramref name="folder"/>: the name the site suggested made safe (no folder part, no slash or colon, no
    /// leading dot), <c>download</c> when nothing is left, and <c>name-2.ext</c>, <c>name-3.ext</c>… when taken. Pure over <paramref name="exists"/>.
    /// </summary>
    public static string DownloadPath(string folder, string? suggested, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(exists);
        string name = (suggested ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Select(c => c is ':' || char.IsControl(c) ? '-' : c).ToArray()).Trim().TrimStart('.');
        if (name.Length == 0)
        {
            name = "download";
        }

        if (name.Length > 200)
        {
            string extension = Path.GetExtension(name);
            name = name[..(200 - Math.Min(extension.Length, 20))] + (extension.Length <= 20 ? extension : "");
        }

        string first = Path.Combine(folder, name);
        if (!exists(first))
        {
            return first;
        }

        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; n < 10_000; n++)
        {
            string candidate = Path.Combine(folder, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ext);
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, stem + "-" + Guid.NewGuid().ToString("N")[..8] + ext);
    }

    /// <summary>
    /// The profile's WebKit store id (pass 6): read from <see cref="StoreIdFileName"/> in <paramref name="profileDirectory"/>, else a new one
    /// written there (the folder made if need be). Null when the file cannot be read or written: the window then refuses to open rather than
    /// share a store. A file holding no UUID is replaced.
    /// </summary>
    public static string? StoreId(string profileDirectory, Func<Guid>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        string file = Path.Combine(profileDirectory, StoreIdFileName);
        try
        {
            if (ReadStoreId(file) is { } kept)
            {
                return kept;
            }

            Directory.CreateDirectory(profileDirectory);
            string made = (newId ?? Guid.NewGuid)().ToString("D").ToUpperInvariant();
            File.WriteAllText(file, made + "\n");
            return made;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Browser", $"The Browser window's store id could not be kept in {file}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The store id <paramref name="file"/> holds, or null when it is missing or holds no UUID.</summary>
    public static string? ReadStoreId(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!File.Exists(file))
        {
            return null;
        }

        string text = File.ReadAllText(file).Trim();
        return Guid.TryParseExact(text, "D", out var id) ? id.ToString("D").ToUpperInvariant() : null;
    }

    /// <summary>
    /// <see cref="ReadStoreId"/> for <paramref name="profileDirectory"/>'s file without a throw (2026-10-20, the code review's G6): null with
    /// no id, and then <paramref name="unreadable"/> the reason when the file is there but could not be read.
    /// </summary>
    public static string? TryReadStoreId(string profileDirectory, out string? unreadable)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        unreadable = null;
        try
        {
            return ReadStoreId(Path.Combine(profileDirectory, StoreIdFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Browser", $"The Browser window's store id in {profileDirectory} could not be read: {ex.Message}");
            unreadable = BrowserText.MacStoreIdUnreadable(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Where WebKit keeps the identified data stores of this process (<c>dataStoreForIdentifier:</c>, macOS 14): for an app with no bundle,
    /// <c>~/Library/WebKit/&lt;process name&gt;/WebsiteDataStore</c>, one folder per store named by its id in lower case (seen on this Mac,
    /// 2026-10-20). Null when the process has no name to go by.
    /// </summary>
    public static string? StoresRoot()
    {
        string? name = Environment.ProcessPath is { } path ? Path.GetFileNameWithoutExtension(path) : null;
        return string.IsNullOrEmpty(name)
            ? null
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "WebKit", name, "WebsiteDataStore");
    }

    /// <summary>
    /// Why a profile about to be deleted is kept (2026-10-20, the code review's C5: <c>/profile delete</c> removed the store id with the folder
    /// even when the store itself stayed, orphaning its cookies and cache): <see cref="BrowserText.MacStoreNotRemoved"/> with
    /// <paramref name="why"/>, unless the store's folder under <paramref name="storesRoot"/> (<see cref="StoresRoot"/> when null) is gone — an
    /// earlier try that answered late, or a store never written — when there is nothing to keep it for.
    /// </summary>
    public static string? StoreKept(string storeId, string why, string? storesRoot = null)
    {
        ArgumentNullException.ThrowIfNull(storeId);
        ArgumentNullException.ThrowIfNull(why);
        string? root = storesRoot ?? StoresRoot();
        if (root is not null && !Directory.Exists(Path.Combine(root, storeId.ToLowerInvariant())))
        {
            return null;
        }

        return BrowserText.MacStoreNotRemoved(why);
    }

    /// <summary>
    /// <c>/profile delete</c> with no Browser window here (before macOS 14, no desktop session: <c>Program</c> passes none): on a Mac, the
    /// profile is kept while its folder names a store that is still there (<see cref="StoreKept"/>), since nothing here can remove it; null
    /// elsewhere and with no store. <paramref name="storesRoot"/> for the tests.
    /// </summary>
    public static string? KeptWithoutWindow(string profileDirectory, string? storesRoot = null)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        if (!OperatingSystem.IsMacOS() && storesRoot is null)
        {
            return null;
        }

        if (TryReadStoreId(profileDirectory, out string? unreadable) is { } storeId)
        {
            return StoreKept(storeId, BrowserText.MacUnavailable, storesRoot);
        }

        return unreadable is null ? null : BrowserText.MacStoreNotRemoved(unreadable);
    }

    /// <summary>How long a page must have stayed up after its web process last ended for the next end to be reloaded again (<see cref="ContentEnded"/>).</summary>
    public static readonly TimeSpan ContentEndQuiet = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The page's web content process ended at <paramref name="at"/> (<paramref name="now"/> on a monotonic clock), after <paramref name="last"/>:
    /// the record kept, and whether the window reloads the page (true) or shows its error page (2026-10-20, the code review's G5: the count went
    /// back to 0 at every load's end, so a page whose process died after each load reloaded for ever). One reload per address, counted again
    /// only on another address or once the page stayed up <see cref="ContentEndQuiet"/>.
    /// </summary>
    public static (MacContentEnds Next, bool Reload) ContentEnded(MacContentEnds last, string? at, TimeSpan now)
    {
        bool fresh = last.Count == 0 || !string.Equals(last.At, at, StringComparison.Ordinal) || now - last.When >= ContentEndQuiet;
        var next = new MacContentEnds(at, now, fresh ? 1 : last.Count + 1);
        return (next, next.Count == 1);
    }

    /// <summary>The next zoom from <paramref name="current"/>: one of Safari's steps up (<paramref name="direction"/> 1) or down (-1); 0 is back to 1.</summary>
    public static double Zoom(double current, int direction)
    {
        if (direction == 0 || !double.IsFinite(current))
        {
            return 1.0;
        }

        if (direction > 0)
        {
            return ZoomSteps.FirstOrDefault(step => step > current + 0.001, ZoomSteps[^1]);
        }

        return ZoomSteps.LastOrDefault(step => step < current - 0.001, ZoomSteps[0]);
    }

    /// <summary>
    /// The width in points to ask WebKit's snapshot for (pass 10: it is drawn at the backing scale, so 225 points is 450 pixels on a Retina
    /// screen): at most <paramref name="maxWidth"/> pixels, never wider than the view.
    /// </summary>
    public static double SnapshotWidth(int maxWidth, double viewWidth, double scale)
    {
        double perPoint = double.IsFinite(scale) && scale >= 1 ? scale : 1;
        double asked = Math.Max(1, maxWidth) / perPoint;
        return Math.Max(1, Math.Floor(Math.Min(asked, Math.Max(1, viewWidth))));
    }

    /// <summary>A live view's place (fractions of the view across and down) as a point in the web view, top-left origin (it is flipped).</summary>
    public static (double X, double Y) PointIn(double fractionX, double fractionY, double width, double height) =>
        (Math.Round(Math.Clamp(fractionX, 0, 1) * width, 2), Math.Round(Math.Clamp(fractionY, 0, 1) * height, 2));

    /// <summary>A live view's scroll (CSS pixels, down positive) as a wheel's pixel delta (up positive), within <see cref="BrowserRemote.MaxScroll"/>.</summary>
    public static int WheelDelta(double deltaY) => (int)Math.Round(-Math.Clamp(double.IsFinite(deltaY) ? deltaY : 0, -BrowserRemote.MaxScroll, BrowserRemote.MaxScroll));

    /// <summary>A live view's key (<see cref="BrowserRemote.Keys"/>) as a Mac key code and its characters; null for one with no button.</summary>
    public static (ushort Code, string Characters)? KeyOf(string? key) => key switch
    {
        "Enter" => (36, "\r"),
        "Backspace" => (51, "\u007f"),
        "Tab" => (48, "\t"),
        _ => null,
    };

    /// <summary>
    /// Whether a failed load is no failure to show (pass 17): a load the window or a newer one cancelled (<c>NSURLErrorCancelled</c>), one
    /// that became a download or was refused by the filter (WebKit's 102, "Frame load interrupted"), or one a plug-in took (204).
    /// </summary>
    public static bool IsQuietFailure(string? domain, long code) => domain switch
    {
        "NSURLErrorDomain" => code == -999,
        "WebKitErrorDomain" => code is 102 or 204,
        _ => false,
    };

    /// <summary>Whether an HTTP sign-in the page meets is the window's to ask (Basic, Digest); the rest (Kerberos, NTLM, certificates, trust) are the system's.</summary>
    public static bool AsksForSignIn(string? method) =>
        method is "NSURLAuthenticationMethodHTTPBasic" or "NSURLAuthenticationMethodHTTPDigest" or "NSURLAuthenticationMethodDefault";

    /// <summary>The hovered link's address for the status bubble: nothing for none, else at most 160 characters, the middle cut.</summary>
    public static string Hover(string? href)
    {
        string text = (href ?? "").Trim();
        if (!BrowserPage.Allows(text) && !text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return text.Length <= 160 ? text : text[..100] + "…" + text[^59..];
    }

    /// <summary>A download's row in the bar's menu: its name and how far it is, or how it ended. Pinned.</summary>
    public static string DownloadLabel(MacDownloadRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string name = Path.GetFileName(row.Path);
        return row.State switch
        {
            MacDownloadState.Running => name + " — " + Math.Clamp((int)Math.Round(row.Fraction * 100), 0, 100).ToString(CultureInfo.InvariantCulture) + "%",
            MacDownloadState.Failed => name + " — " + BrowserText.DownloadFailedWord,
            MacDownloadState.Cancelled => name + " — " + BrowserText.DownloadCancelledWord,
            _ => name,
        };
    }

    /// <summary>The download menu's commands: per row, open, show in Finder, cancel; ids from <see cref="DownloadCommandBase"/>.</summary>
    public const int DownloadCommandBase = 30_000;

    /// <summary>The download menu (<see cref="DownloadLabel"/> rows, newest first, each with its actions).</summary>
    public static IReadOnlyList<ContextMenuItem> DownloadMenu(IReadOnlyList<MacDownloadRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return [new ContextMenuItem(BrowserText.NoDownloads, Enabled: false)];
        }

        var items = new List<ContextMenuItem>();
        foreach (var row in rows.Reverse())
        {
            int baseId = DownloadCommandBase + (row.Id * 4);
            var children = row.State == MacDownloadState.Running
                ? new List<ContextMenuItem> { new(BrowserText.DownloadCancel, baseId + 2) }
                : row.State == MacDownloadState.Finished
                    ? [new(BrowserText.DownloadOpen, baseId), new(BrowserText.DownloadShow, baseId + 1)]
                    : [new(BrowserText.DownloadShow, baseId + 1, Enabled: false)];
            items.Add(new ContextMenuItem(DownloadLabel(row), 0, children));
        }

        return items;
    }

    /// <summary>A download menu command read back: the row's id and the action (0 open, 1 show in Finder, 2 cancel); null for another command.</summary>
    public static (int Id, int Action)? DownloadCommand(int command) =>
        command >= DownloadCommandBase ? ((command - DownloadCommandBase) / 4, (command - DownloadCommandBase) % 4) : null;
}
