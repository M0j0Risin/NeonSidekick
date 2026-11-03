using NeonSidekick.Files;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// Clickable links and paths (2026-11-01, feature idea #10, plan memoized-dancing-horizon): a web address or a path in the transcript
/// carries a link (<see cref="LinkText"/>); the link chord (Ctrl+click on Windows, Option or Shift+click on a Mac) opens a web address in
/// the default browser, or a path in <c>/browse</c> with the file at the cursor, and hovering shows where it goes on the hints bar.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>
    /// A web link's chord-click (both click paths; any thread): the address in the default browser through the web seam, as
    /// <c>open_url</c> and <c>/browser</c>'s saved links open one. An open prints nothing (the browser coming up is the answer, as a
    /// picture's open); a failure is an error line through the flow sink, which waits for a running reply. Where the terminal opens
    /// links itself (Windows Terminal or VS Code on Windows: the pane's console writes links only there) the click is spent and nothing
    /// is opened, so a link never opens twice.
    /// </summary>
    /// <remarks>
    /// The address opened is <see cref="SafeText.WebLink"/>'s target, its host in the ASCII form the hover hint shows (2026-11-02, review
    /// P12-3/P12-8), so what the hint names is what opens.
    /// </remarks>
    private void OpenLink(string target)
    {
        if (!LinkText.IsWeb(target) || SafeText.WebLink(target) is not { } link || !Uri.TryCreate(link.Target, UriKind.Absolute, out var url))
        {
            return;
        }

        if (OperatingSystem.IsWindows() && _pane.Profile.Capabilities.Links)
        {
            return;   // the terminal has opened it already
        }

        string answer = OpenInDefaultBrowser(url);
        if (answer.StartsWith(ErrorLead, StringComparison.Ordinal))
        {
            _flow.Error(answer[ErrorLead.Length..]);
        }
    }

    /// <summary>
    /// The transcript's path resolver (<see cref="TranscriptRenderer.ResolvePath"/>): <paramref name="text"/> through the working folder's
    /// read-side resolve, the full path of an existing file or folder inside it, else null. The working folder as it is when the line is
    /// laid out: a later <c>/cwd</c> changes no link already made (the click judges it again).
    /// </summary>
    private string? ResolveLinkPath(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return _files.Resolve(text.Trim(), forWrite: false, out string full) == FileOutcome.Ok && (File.Exists(full) || Directory.Exists(full))
                ? full
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The hover hint for a link's target (<see cref="ScreenPane.LinkHint"/>, on the pane's tick under its lock): a web address as it is,
    /// a path relative to the working folder, or its full path said to be outside after a <c>/cwd</c>. Path math alone, never the disk.
    /// </summary>
    private string? LinkHintFor(string target)
    {
        if (LinkText.PathOf(target) is not { } full)
        {
            return LinkText.IsWeb(target) ? LinkText.HoverHint(target, null, outside: false, LinkText.HintWidth) : null;
        }

        try
        {
            bool outside = !_files.Contains(full);
            string shown = outside ? full : FileText.Name(_files.Relative(full));
            return LinkText.HoverHint(target, shown, outside, LinkText.HintWidth);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The line a path's chord-click runs: <c>/browse</c> on its full path (a path with a space needs no quoting: the argument is the whole text).</summary>
    internal static string BrowsePathLine(string full) => "/browse " + full;

    /// <summary>The screen's pane (tests: where a link's cell is found, a pointer moved).</summary>
    internal ScreenPane PaneForTests => _pane;
}
