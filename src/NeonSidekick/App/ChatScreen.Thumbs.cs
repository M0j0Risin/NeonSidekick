using System.Collections.Concurrent;
using NeonSidekick.Comfy;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.App;

/// <summary>
/// The thumbnail browser's side of the chat (2026-10-04, the user's ask): <c>/thumbs &lt;folder&gt;</c> and <c>/comfy thumbs</c> open
/// it, and the chat is the hub that keeps it, the picture viewer and the console's picture strip on the same picture — a click in the
/// browser moves the viewer (<see cref="ThumbsPicked"/>), the viewer's keys and the strip's arrows move the browser
/// (<see cref="FollowThumbs"/>), and neither window's follow answers back, so nothing chases itself. The picture menu's attach and
/// print come here from the windows' threads (<see cref="AttachPicture"/>, <see cref="PrintPicture"/>), queued for the idle line,
/// which they wake; what a menu row did is a line in the chat (<see cref="PictureReported"/>).
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>The thumbnail browser's opener (<see cref="ThumbsWindow.Open"/>), a folder's full path and the picture to select; null where there is none.</summary>
    private readonly Action<string, string?>? _openThumbs;

    /// <summary>An open thumbnail browser moved to a picture quietly (<see cref="ThumbsWindow.Follow"/>); null where there is none.</summary>
    private readonly Action<string>? _followThumbs;

    /// <summary>The viewer moved to a picture without the keyboard, opened when there is none (<see cref="PictureWindow.ShowQuietly"/>); null where there is none.</summary>
    private readonly Action<string>? _showInViewer;

    // The pictures the windows' menus asked to attach or print, for the idle line (full paths).
    private readonly ConcurrentQueue<string> _windowAttaches = new();
    private readonly ConcurrentQueue<string> _windowPrints = new();

    /// <summary>Whether a window's attach or print waits for the idle line: the read is woken for it.</summary>
    private bool WindowWorkReady => !_windowAttaches.IsEmpty || !_windowPrints.IsEmpty;

    /// <summary>
    /// <c>/thumbs &lt;folder&gt;</c>: the folder resolved in the sandbox (as <c>/view</c>'s path), the browser opened on it (or the open one
    /// pointed at it and brought forward); a picture's path opens its folder with it selected. Errors and the notices go through the
    /// flow sink, so it is safe under a reply. Any thread.
    /// </summary>
    private void HandleThumbs(string args)
    {
        string path = args.Trim().Trim('"');
        if (path.Length == 0)
        {
            _flow.Error(ThumbsText.UsageError);
            return;
        }

        var outcome = _files.Resolve(path, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            _flow.Error(FileText.Error(outcome, path, "view"));
            return;
        }

        bool folder = Directory.Exists(full);
        if (!folder && !File.Exists(full))
        {
            _flow.Error(FileText.Error(FileOutcome.Missing, _files.Relative(full), "view"));
            return;
        }

        if (!folder && !ImageFile.IsImagePath(full))
        {
            _flow.Error(FileText.Error(FileOutcome.NotAnImage, _files.Relative(full), "view"));
            return;
        }

        OpenThumbs(folder ? full : Path.GetDirectoryName(full) ?? full, folder ? null : full);
    }

    /// <summary><c>/comfy thumbs</c>: the browser on the ComfyUI output folder, made when it is not there yet (<see cref="OpenViewer"/>'s twin). Any thread.</summary>
    private void OpenComfyThumbs()
    {
        if (_openThumbs is null)
        {
            _flow.Error(ThumbsText.Unavailable);
            return;
        }

        string folder = ComfyStudio.OutputFolder(_effective().ComfyOutputFolder);
        var outcome = _files.Resolve(folder, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            _flow.Error(FileText.Error(outcome, folder, "watch"));
            return;
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _flow.Error(ThumbsText.Failed(full, ex.Message));
            return;
        }

        OpenThumbs(full, null);
    }

    private void OpenThumbs(string folder, string? select)
    {
        if (_openThumbs is null)
        {
            _flow.Error(ThumbsText.Unavailable);
            return;
        }

        try
        {
            _openThumbs(folder, select);
            _flow.Notice(ThumbsText.Opened(folder));
            _flow.Notice(ThumbsText.Keys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            _flow.Error(ThumbsText.Failed(folder, ex.Message));
        }
    }

    /// <summary>
    /// The picture the browser's user picked (a click, the keys, an edit's result; <see cref="ThumbsWindow.Picked"/>): the viewer moved
    /// to it without the keyboard (opened quietly when there is none), and the strip's tile for it highlighted. Any thread (the browser's).
    /// </summary>
    public void ThumbsPicked(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_showInViewer is { } show)
        {
            try
            {
                show(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or ArgumentException)
            {
                DiagnosticLog.Warn("Viewer", $"Could not move the viewer to {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (_effective().ComfyPictureStrip)
        {
            HighlightStrip(path);
        }
    }

    /// <summary>An open browser moved to <paramref name="path"/> quietly (the viewer's keys, the strip's arrows); nothing without one.</summary>
    private void FollowThumbs(string path)
    {
        if (_followThumbs is null)
        {
            return;
        }

        try
        {
            _followThumbs(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not move the thumbnails to {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>
    /// The picture menu's Attach to the chat (<see cref="PictureMenu.Attach"/>): queued for the idle line, which pastes its path onto the
    /// line as <c>/camera</c>'s shot is (an <c>[Image #N]</c> and a space, the draft kept); the idle read is woken for it. Any thread.
    /// </summary>
    public void AttachPicture(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _windowAttaches.Enqueue(path);
        SignalAlert();
    }

    /// <summary>The picture menu's Print (<see cref="PictureMenu.Print"/>): queued for the idle line, which prints it as <c>/print</c> would. Any thread.</summary>
    public void PrintPicture(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _windowPrints.Enqueue(path);
        SignalAlert();
    }

    /// <summary>What a picture menu row did (<see cref="PictureMenu.Reported"/>): a notice, or an error line. Any thread.</summary>
    public void PictureReported(string line, bool error)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (error)
        {
            _flow.Error(line);
        }
        else
        {
            _flow.Notice(line);
        }
    }

    // The idle loop's turn for the windows: a queued attach becomes the line's paste replay (one at a time, the next on the next
    // pass); the queued prints run now. Only at the idle line.
    private async Task TakeWindowWorkAsync(CancellationToken cancellationToken)
    {
        while (_windowPrints.TryDequeue(out string? print))
        {
            await HandlePrintAsync("\"" + _files.Relative(print) + "\"", cancellationToken).ConfigureAwait(false);
        }

        if (_attachReplay is null && _draftReplay is null && _windowAttaches.TryDequeue(out string? attach))
        {
            // The path as a paste (the drop rule makes it the token), then a space, so what the user types next is a word apart.
            _attachReplay = [new InputEvent.Paste("\"" + attach + "\""), new InputEvent.Key(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false))];
        }
    }

    // The strip's newest tile for the file highlighted and the pane redrawn (ViewerBrowsed's body, shared with ThumbsPicked).
    private void HighlightStrip(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        var ids = new HashSet<int>();
        lock (_pictures)
        {
            for (int id = 0; id < _pictures.Count; id++)
            {
                if (_pictures[id].FullPath is { } own && string.Equals(Path.GetFullPath(own), full, StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(id);
                }
            }
        }

        if (ids.Count > 0 && _pictureStrip.Highlight(ids.Contains))
        {
            _pane.RedrawStrip();
        }
    }
}
