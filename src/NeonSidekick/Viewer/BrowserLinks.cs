using System.Globalization;
using System.Text;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Viewer;

/// <summary>One saved link (2026-10-13, the user's ask): its address as it was saved, the user's name for it (null: none yet), and when.</summary>
public sealed record BrowserLink(string Url, string? Name, DateTimeOffset Added);

/// <summary>The profile's <c>browser-links.json</c>: a version and the links in the user's order.</summary>
public sealed class BrowserLinksFile
{
    public int Version { get; set; } = BrowserLinks.Version;

    public List<BrowserLink> Links { get; set; } = [];
}

/// <summary>What a change of the saved links did.</summary>
public enum BrowserLinkOutcome
{
    /// <summary>Done (for a move or a rename to what it was, nothing needed writing).</summary>
    Done,

    /// <summary>An add of an address saved already: only a missing name was filled.</summary>
    AlreadySaved,

    /// <summary>No saved link has that address.</summary>
    NotSaved,

    /// <summary>A readdress to an address another link has.</summary>
    Duplicate,

    /// <summary>Not an http or https address.</summary>
    NotWeb,

    /// <summary>An address with a user name or password in it, never written to the plain file.</summary>
    Credentials,

    /// <summary>A local PDF's copy the window shows (2026-10-14, <see cref="PdfView"/>): it is gone when the window closes.</summary>
    LocalCopy,

    /// <summary>A Web UI sign-in link (later on 2026-10-15): it holds the token, so it is never saved.</summary>
    SignInLink,

    /// <summary>The file is from a newer NeonSidekick: left alone, nothing written.</summary>
    Newer,

    /// <summary>The file could not be written (logged); nothing changed.</summary>
    Failed,
}

/// <summary>
/// The profile's saved links (2026-10-13, the user's ask: web links kept like <c>/youtube list</c>'s saved videos, chosen in the Browser
/// window's ☆ menu or on <c>/browser list</c>'s pane), in <see cref="FileName"/> beside <c>youtube.json</c>, in the user's order (a new
/// one last). <c>YouTubeLibrary</c>'s shape — read on each use, a save a temp file moved over the old one, a failure logged and reported,
/// never thrown — with a static lock, since the Browser window's thread and the screen each make their own over the same file. Unlike the
/// window's history, a list the user built is never written over unseen: a broken file is moved aside to <c>.bad</c> before the first write,
/// and one from a newer version is left alone and refuses every write. An address is matched by <see cref="Key"/>: exactly, the
/// <c>#fragment</c> too, the host in its punycode form. The pure parts (<see cref="Key"/>, <see cref="RowLabel"/>, <see cref="MenuItems"/>,
/// <see cref="UsableTitle"/>, <see cref="CleanName"/>) are pinned.
/// </summary>
public sealed class BrowserLinks
{
    public const string FileName = "browser-links.json";

    /// <summary>The file's version; a higher one is a newer NeonSidekick's and is left alone.</summary>
    public const int Version = 1;

    /// <summary>The longest name kept, whether typed or taken from a page's title.</summary>
    public const int NameLength = 200;

    /// <summary>
    /// How long a bare <c>/browser save</c> or <c>web_save_link</c>'s <c>current</c> waits for the window to say what it shows (later on
    /// 2026-10-13; on <c>ChatScreen</c> until the tools came, later still that day).
    /// </summary>
    public static readonly TimeSpan CurrentPageWait = TimeSpan.FromSeconds(2);

    /// <summary>The menu: the links on its root, the overflow submenus, the rows in each, how long a label's name may be.</summary>
    public const int RootRows = 15;
    public const int MaxSubmenus = 7;
    public const int SubmenuRows = 25;
    public const int LabelLength = 60;

    /// <summary>The menu's Save / Remove this page command, past the history menu's (1..1000 and 10 000), and a link's: its index plus <see cref="RowBase"/>.</summary>
    public const int ToggleCommand = 20_000;
    public const int RowBase = 20_001;

    private const string Category = "Browser";

    // One for every instance: the window's thread and the screen write the same file.
    private static readonly Lock s_gate = new();

    private readonly string _filePath;
    private readonly TimeProvider _time;

    /// <param name="directory">The profile's directory; the file is <see cref="FileName"/> under it.</param>
    public BrowserLinks(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _filePath = NeonSidekick.Settings.Profiles.ConfigPath(Directory, FileName);   // <profile>\configs since 2026-10-16
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The profile directory the file lives in: the screen builds a new one when the profile moves.</summary>
    public string Directory { get; }

    public string FilePath => _filePath;

    /// <summary>The saved links in the user's order.</summary>
    public IReadOnlyList<BrowserLink> List()
    {
        lock (s_gate)
        {
            return Load().Links;
        }
    }

    /// <summary>Whether the list can be changed: false while the file is a newer NeonSidekick's.</summary>
    public bool Writable()
    {
        lock (s_gate)
        {
            return Blocked(Load()) is null;
        }
    }

    /// <summary>The saved link with <paramref name="url"/>'s address (<see cref="Key"/>), or null.</summary>
    public BrowserLink? Find(string? url)
    {
        if (Key(url) is not { } key)
        {
            return null;
        }

        lock (s_gate)
        {
            return Load().Links.Find(l => Key(l.Url) == key);
        }
    }

    /// <summary>The saved link <paramref name="text"/> names: its number in <see cref="List"/> (from 1), or its address; null for none.</summary>
    public BrowserLink? Resolve(string? text)
    {
        string trimmed = (text ?? "").Trim();
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            var links = List();
            return number >= 1 && number <= links.Count ? links[number - 1] : null;
        }

        return trimmed.Length > 0 && Web.WebFetcher.ParseUrl(trimmed) is { } url ? Find(url.AbsoluteUri) : null;
    }

    /// <summary>
    /// <paramref name="url"/> saved last with <paramref name="name"/> (<see cref="CleanName"/>); an address saved already keeps its place
    /// and only takes a name it lacked (<see cref="BrowserLinkOutcome.AlreadySaved"/>). <paramref name="saved"/> is the link as the list
    /// has it after, null when nothing was saved.
    /// </summary>
    public BrowserLinkOutcome Add(string? url, string? name, out BrowserLink? saved)
    {
        saved = null;
        if (Refused(url) is { } refused)
        {
            return refused;
        }

        lock (s_gate)
        {
            var file = Load();
            if (Blocked(file) is { } blocked)
            {
                return blocked;
            }

            string key = Key(url)!;
            int at = file.Links.FindIndex(l => Key(l.Url) == key);
            if (at >= 0)
            {
                var known = file.Links[at];
                saved = known.Name is null && CleanName(name) is { } filled ? known with { Name = filled } : known;
                if (saved != known)
                {
                    file.Links[at] = saved;
                    if (!Save(file))
                    {
                        saved = known;
                        return BrowserLinkOutcome.Failed;
                    }
                }

                return BrowserLinkOutcome.AlreadySaved;
            }

            var link = new BrowserLink(url!, CleanName(name), _time.GetUtcNow());
            file.Links.Add(link);
            if (!Save(file))
            {
                return BrowserLinkOutcome.Failed;
            }

            saved = link;
            return BrowserLinkOutcome.Done;
        }
    }

    /// <summary>The saved link with <paramref name="url"/>'s address taken off the list.</summary>
    public BrowserLinkOutcome Remove(string? url) => Change(url, (links, at) =>
    {
        links.RemoveAt(at);
        return BrowserLinkOutcome.Done;
    });

    /// <summary>The saved link's name changed to <paramref name="name"/> (<see cref="CleanName"/>; blank takes the name off).</summary>
    public BrowserLinkOutcome Rename(string? url, string? name) => Change(url, (links, at) =>
    {
        links[at] = links[at] with { Name = CleanName(name) };
        return BrowserLinkOutcome.Done;
    });

    /// <summary>The saved link's address changed to <paramref name="newUrl"/>; refused when another link has it already.</summary>
    public BrowserLinkOutcome Readdress(string? url, string? newUrl)
    {
        if (Refused(newUrl) is { } refused)
        {
            return refused;
        }

        string newKey = Key(newUrl)!;
        return Change(url, (links, at) =>
        {
            if (links.Where((_, i) => i != at).Any(l => Key(l.Url) == newKey))
            {
                return BrowserLinkOutcome.Duplicate;
            }

            links[at] = links[at] with { Url = newUrl! };
            return BrowserLinkOutcome.Done;
        });
    }

    /// <summary>
    /// The saved link moved in the list: <paramref name="delta"/> places down (negative up), <see cref="int.MinValue"/> to the top,
    /// <see cref="int.MaxValue"/> to the bottom, held at the ends (<see cref="YouTube.YouTubeLibrary.MovedTo"/>).
    /// </summary>
    public BrowserLinkOutcome Move(string? url, int delta) => Change(url, (links, at) =>
    {
        int to = YouTube.YouTubeLibrary.MovedTo(at, delta, links.Count);
        var link = links[at];
        links.RemoveAt(at);
        links.Insert(to, link);
        return BrowserLinkOutcome.Done;
    });

    /// <summary>
    /// The saved link with <paramref name="url"/>'s address named <paramref name="title"/> when it has no name yet and the title is one
    /// (<see cref="UsableTitle"/>): the Browser window's visit of a link saved by its address alone. True when it was named.
    /// </summary>
    public bool FillName(string? url, string? title)
    {
        if (UsableTitle(title, url) is not { } name)
        {
            return false;
        }

        bool named = false;
        Change(url, (links, at) =>
        {
            if (links[at].Name is not null)
            {
                return BrowserLinkOutcome.Done;
            }

            links[at] = links[at] with { Name = name };
            named = true;
            return BrowserLinkOutcome.Done;
        });
        return named;
    }

    /// <summary>Every saved link taken off: how many went (0 with none, nothing written), or the refusal.</summary>
    public BrowserLinkOutcome Clear(out int cleared)
    {
        cleared = 0;
        lock (s_gate)
        {
            var file = Load();
            if (Blocked(file) is { } blocked)
            {
                return blocked;
            }

            if (file.Links.Count == 0)
            {
                return BrowserLinkOutcome.Done;
            }

            int count = file.Links.Count;
            file.Links.Clear();
            if (!Save(file))
            {
                return BrowserLinkOutcome.Failed;
            }

            cleared = count;
            return BrowserLinkOutcome.Done;
        }
    }

    // Load, find the link with url's address, change the list, save when it changed.
    private BrowserLinkOutcome Change(string? url, Func<List<BrowserLink>, int, BrowserLinkOutcome> change)
    {
        if (Key(url) is not { } key)
        {
            return BrowserLinkOutcome.NotSaved;
        }

        lock (s_gate)
        {
            var file = Load();
            if (Blocked(file) is { } blocked)
            {
                return blocked;
            }

            int at = file.Links.FindIndex(l => Key(l.Url) == key);
            if (at < 0)
            {
                return BrowserLinkOutcome.NotSaved;
            }

            var before = file.Links.ToList();
            var outcome = change(file.Links, at);
            if (outcome != BrowserLinkOutcome.Done || file.Links.SequenceEqual(before))
            {
                return outcome;
            }

            return Save(file) ? BrowserLinkOutcome.Done : BrowserLinkOutcome.Failed;
        }
    }

    /// <summary>Why <paramref name="url"/> cannot be saved, or null when it can: a web address with no user name or password in it.</summary>
    public static BrowserLinkOutcome? Refused(string? url) =>
        PdfView.IsOurs(url) ? BrowserLinkOutcome.LocalCopy   // a staged PDF's copy (2026-10-14, the PDF plan's gap 65): gone when the window closes
        : WebUi.WebUiAuth.IsSignInUrl(url) ? BrowserLinkOutcome.SignInLink   // a Web UI sign-in link (later on 2026-10-15): it holds the token
        : !VideoPage.IsWebLink(url) ? BrowserLinkOutcome.NotWeb
        : HasCredentials(url) ? BrowserLinkOutcome.Credentials
        : null;

    /// <summary>Whether <paramref name="url"/> carries a user name or password (<c>https://user:pass@host/</c>). Pure.</summary>
    public static bool HasCredentials(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.UserInfo.Length > 0;

    /// <summary>
    /// What makes two addresses one saved link: the scheme, the host in its punycode form (WebView2 reports an international host that way,
    /// a typed address keeps its Unicode), the port when not the scheme's, the path, query and fragment. Null for anything but a web address. Pure.
    /// </summary>
    public static string? Key(string? url)
    {
        if (!VideoPage.IsWebLink(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        string port = parsed.IsDefaultPort ? "" : ":" + parsed.Port.ToString(CultureInfo.InvariantCulture);
        return parsed.Scheme + "://" + parsed.IdnHost.ToLowerInvariant() + port + parsed.PathAndQuery + parsed.Fragment;
    }

    /// <summary>
    /// A name as kept: trimmed, control characters taken out, at most <see cref="NameLength"/> characters (never splitting a surrogate
    /// pair); null when nothing is left. Pure.
    /// </summary>
    public static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var kept = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            kept.Append(char.IsControl(c) ? ' ' : c);
        }

        string clean = kept.ToString().Trim();
        if (clean.Length > NameLength)
        {
            int end = char.IsHighSurrogate(clean[NameLength - 1]) ? NameLength - 1 : NameLength;
            clean = clean[..end].TrimEnd();
        }

        return clean.Length == 0 ? null : clean;
    }

    /// <summary>
    /// <paramref name="title"/> as a name for <paramref name="url"/>'s link (<see cref="CleanName"/>), or null when it is none: blank, or
    /// only the address — WebView2 reports the address (whole, or without its scheme) as the title until the page's own arrives, and for a
    /// page with none, and a name is filled only once. Pure.
    /// </summary>
    public static string? UsableTitle(string? title, string? url)
    {
        if (CleanName(title) is not { } name)
        {
            return null;
        }

        if (url is null)
        {
            return name;
        }

        string bare = WithoutScheme(url);
        string[] addresses = [url, url.TrimEnd('/'), bare, bare.TrimEnd('/'), BrowserPage.HostOf(url) ?? url];
        return addresses.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) ? null : name;
    }

    /// <summary><paramref name="url"/> without <c>https://</c> or <c>http://</c>. Pure.</summary>
    public static string WithoutScheme(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        foreach (string scheme in (ReadOnlySpan<string>)["https://", "http://"])
        {
            if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return url[scheme.Length..];
            }
        }

        return url;
    }

    /// <summary>
    /// A menu row's words, the history menu's shape (<see cref="BrowserHistory.RowLabel"/>): the name (cut at <see cref="LabelLength"/>)
    /// and the host, <c>News · example.com</c>, so two links of one name are told apart; the address without its scheme with no name. Pure.
    /// </summary>
    public static string RowLabel(BrowserLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.Name is { Length: > 0 } name
            ? Cut(name) + " · " + (BrowserPage.HostOf(link.Url) ?? link.Url)
            : Cut(WithoutScheme(link.Url));
    }

    /// <summary>
    /// The ☆ menu over <paramref name="list"/> for the page at <paramref name="current"/>: <see cref="BrowserLinkText.SaveThisPage"/> (or
    /// <see cref="BrowserLinkText.RemoveThisPage"/> when it is saved; off for a page that cannot be saved or a list that cannot be changed),
    /// a separator, the first <see cref="RootRows"/> links, the next ones in up to <see cref="MaxSubmenus"/> submenus of
    /// <see cref="SubmenuRows"/> (<c>16–40</c>…), then a dim note of how many more <c>/browser list</c> has; one dim
    /// <see cref="BrowserLinkText.NoneInMenu"/> row with none. A link's command is <see cref="RowBase"/> plus its index. Pure.
    /// </summary>
    public static IReadOnlyList<ContextMenuItem> MenuItems(IReadOnlyList<BrowserLink> list, string? current, bool writable = true)
    {
        ArgumentNullException.ThrowIfNull(list);
        string? key = Key(current);
        bool saved = key is not null && list.Any(l => Key(l.Url) == key);
        bool canToggle = writable && Refused(current) is null;
        var items = new List<ContextMenuItem>
        {
            new(saved ? BrowserLinkText.RemoveThisPage : BrowserLinkText.SaveThisPage, ToggleCommand, Enabled: canToggle),
            ContextMenuItem.Separator,
        };
        if (list.Count == 0)
        {
            items.Add(new ContextMenuItem(BrowserLinkText.NoneInMenu, Enabled: false));
            return items;
        }

        for (int i = 0; i < Math.Min(RootRows, list.Count); i++)
        {
            items.Add(new ContextMenuItem(RowLabel(list[i]), RowBase + i));
        }

        int index = RootRows;
        for (int menu = 0; menu < MaxSubmenus && index < list.Count; menu++)
        {
            int end = Math.Min(index + SubmenuRows, list.Count);
            var rows = Enumerable.Range(index, end - index).Select(i => new ContextMenuItem(RowLabel(list[i]), RowBase + i)).ToList();
            items.Add(new ContextMenuItem(BrowserLinkText.SubmenuLabel(index + 1, end), Children: rows));
            index = end;
        }

        if (index < list.Count)
        {
            items.Add(new ContextMenuItem(BrowserLinkText.MoreInList(list.Count - index), Enabled: false));
        }

        return items;
    }

    // At most LabelLength characters, never splitting a surrogate pair, with an ellipsis when cut.
    private static string Cut(string text)
    {
        if (text.Length <= LabelLength)
        {
            return text;
        }

        int end = LabelLength - 1;
        if (char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end].TrimEnd() + "…";
    }

    private enum FileState
    {
        Ok,
        Broken,
        Newer,

        /// <summary>Could not be read for now (locked, denied): not broken, so never moved aside, and not written over.</summary>
        Unreadable,
    }

    // Why a loaded file takes no change: a newer version's, or one that could not be read; null when it can be written.
    private static BrowserLinkOutcome? Blocked(Loaded file) => file.State switch
    {
        FileState.Newer => BrowserLinkOutcome.Newer,
        FileState.Unreadable => BrowserLinkOutcome.Failed,
        _ => null,
    };

    private sealed record Loaded(List<BrowserLink> Links, FileState State);

    /// <summary>The list as the file has it: none for a missing file; none, logged, for a broken one or a newer version's. Caller holds the lock.</summary>
    private Loaded Load()
    {
        if (!File.Exists(_filePath))
        {
            return new Loaded([], FileState.Ok);
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), BrowserJsonContext.Default.BrowserLinksFile);
            if (file is null)
            {
                DiagnosticLog.Warn(Category, $"{FileName} is empty or null; starting with no saved links (it is moved aside at the next save).");
                return new Loaded([], FileState.Broken);
            }

            if (file.Version > Version)
            {
                DiagnosticLog.Warn(Category, $"{FileName} is version {file.Version.ToString(CultureInfo.InvariantCulture)}, from a newer NeonSidekick; it is left alone and the saved links cannot be changed here.");
                return new Loaded([], FileState.Newer);
            }

            var links = (file.Links ?? []).Where(l => l is not null && Refused(l.Url) is null).Select(l => l with { Name = CleanName(l.Name) }).ToList();
            return new Loaded(links, FileState.Ok);
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}; starting with no saved links (it is moved aside at the next save): {ex.Message}", ex);
            return new Loaded([], FileState.Broken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}: {ex.Message}", ex);
            return new Loaded([], FileState.Unreadable);
        }
    }

    /// <summary>
    /// The list written (a temp file moved over the old one), a broken file moved aside to <c>.bad</c> first; false, logged, when it could
    /// not be. Caller holds the lock and has refused a newer version's file.
    /// </summary>
    private bool Save(Loaded file)
    {
        string tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            if (file.State == FileState.Broken && File.Exists(_filePath))
            {
                File.Move(_filePath, _filePath + ".bad", overwrite: true);
                DiagnosticLog.Warn(Category, $"The unreadable {FileName} was moved aside to {FileName}.bad.");
            }

            File.WriteAllText(tempPath, JsonSerializer.Serialize(new BrowserLinksFile { Links = file.Links }, BrowserJsonContext.Default.BrowserLinksFile));
            File.Move(tempPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* best effort */ }
            DiagnosticLog.Error(Category, $"Could not write {FileName}: {ex.Message}", ex);
            return false;
        }
    }
}
