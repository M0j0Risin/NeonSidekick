using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;
using NeonSidekick.YouTube;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/youtube list</c> (2026-10-07, the user's ask): the profile's saved videos on a <see cref="MenuPane"/> page in the order they
/// were saved, each row <see cref="YouTubeText.SavedPaneRow"/>'s in uniform columns (2026-10-08, the user's ask; a watched one dim) under
/// the count (<see cref="YouTubeText.SavedCaption"/>).
/// Enter or a double-click plays the highlighted one in the video window — where it was left (<see cref="YouTubeLibrary.ResumeAt"/>,
/// through the play the screen hands in) — the pane kept open with the player's answer on its status line.
/// <see cref="YouTubeText.RemoveSelectedButton"/> (a click, or <see cref="YouTubeText.RemoveKey"/>; back later on 2026-10-08, the user's
/// ask) takes the highlighted one off after a yes/no, the last one gone closing the pane; the <see cref="QueueMenu.ClearAllButton"/> (a click, or <see cref="QueueMenu.ClearAllKey"/>; 2026-10-08, the user's call: it was the
/// highlighted video's remove, and one at a time is <c>/youtube unsave</c>'s) takes every saved video off the list after a yes/no (the
/// cursor on No), the whole list whatever the filter shows, and closes the pane with <see cref="YouTubeText.Cleared"/> on the
/// transcript — <c>/queue</c>'s clear all. The <see cref="ProcessMenu"/> shape, the list read again after every act. Without the pane
/// the screen prints the rows instead (<c>ChatScreen.ShowSavedVideosAsync</c>).
/// </summary>
internal sealed class YouTubeSavedMenu
{
    /// <summary>The page's buttons: remove selected, then clear all, the queue pane's (2026-10-08; the skills' version list's pair).</summary>
    public static readonly IReadOnlyList<MenuButton> Buttons =
        [new(YouTubeText.RemoveSelectedButton, YouTubeText.RemoveKey), new(QueueMenu.ClearAllButton, QueueMenu.ClearAllKey)];

    private const int RemoveSelected = 0;
    private const int ClearAll = 1;

    private readonly Func<YouTubeLibrary> _library;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly Func<YouTubeSaved, CancellationToken, Task<string>>? _play;
    private readonly TimeZoneInfo _zone;

    /// <param name="library">The loaded profile's saved videos, read when the pane opens and after every act.</param>
    /// <param name="transcript">Where the lines outside the pane go.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="play">What Enter hands a video to: the screen's play, answering as youtube_play does; null with no video window (Enter then says so).</param>
    /// <param name="zone">The zone the footer's dates are shown in.</param>
    public YouTubeSavedMenu(Func<YouTubeLibrary> library, INoticeSink transcript, MenuPane pane, Func<YouTubeSaved, CancellationToken, Task<string>>? play, TimeZoneInfo? zone = null)
    {
        _zone = zone ?? TimeZoneInfo.Local;
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _play = play;
    }

    /// <summary>A row: <see cref="YouTubeText.SavedPaneRow"/> in the columns given, escaped, dim once watched.</summary>
    public static string RowMarkup(YouTubeSaved video, (int Title, int Channel) columns)
    {
        ArgumentNullException.ThrowIfNull(video);
        string row = YouTubeText.SavedPaneRow(video, columns.Title, columns.Channel);
        return video.Watched ? Theme.DimMarkup(row) : Markup.Escape(row);
    }

    /// <summary>Shows the list until ESC; none saved is <see cref="YouTubeText.NoneSaved"/> on the transcript.</summary>
    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        var videos = _library().List();
        if (videos.Count == 0)
        {
            // The pane opens on the empty list too (2026-10-07, the consistency pass).
            await _pane.ShowEmptyAsync(YouTubeText.SavedLabel, YouTubeText.NoneSaved, cancellationToken).ConfigureAwait(false);
            return;
        }

        int cursor = 0;
        string filter = "";
        try
        {
            while (true)
            {
                // The rows under the filter and the cursor's footer (2026-10-07): indices into the list.
                var listed = videos;
                var shown = Enumerable.Range(0, listed.Count).Where(i => YouTubeText.SavedMatches(filter, listed[i])).ToList();
                var columns = YouTubeText.SavedColumns(listed);
                List<string> rows = shown.Count > 0 ? shown.Select(i => RowMarkup(listed[i], columns)).ToList() : [MenuFilter.NoMatchRow(filter)];
                var page = new MenuPage(YouTubeText.SavedLabel, rows, MenuFilter.Hint(YouTubeText.SavedKeys, filter))
                {
                    Buttons = Buttons,
                    Filter = filter,
                    Caption = MenuFilter.CaptionOrNull(filter, shown.Count, listed.Count) ?? YouTubeText.SavedCaption(listed.Count),
                    Footer = (_, row) => row < shown.Count ? YouTubeText.SavedFooter(listed[shown[row]], _zone) : null,
                };
                if (await _pane.PickAsync(page, Math.Max(0, shown.IndexOf(cursor)), cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return;
                }

                if (pick.Filter is { } typed)
                {
                    filter = typed;
                    cursor = -1;   // the first row left
                    continue;
                }

                if (pick.Row >= 0 && pick.Row < shown.Count)
                {
                    cursor = shown[pick.Row];
                }

                if (pick.Button == ClearAll)
                {
                    // Clear all (2026-10-08): the whole list, asked about by its count; a yes closes the pane, so the line goes to the transcript.
                    if (await ConfirmAsync(YouTubeText.ClearPrompt(_library().List().Count), cancellationToken).ConfigureAwait(false))
                    {
                        Tell(YouTubeText.ClearAnswer(_library().Clear()));
                        return;
                    }

                    _pane.Notice(ChatScreen.KeptNotice);
                }
                else if (pick.Row < 0 || pick.Row >= shown.Count)
                {
                    continue;   // the no-match row
                }
                else if (pick.Button == RemoveSelected)
                {
                    if (await ConfirmAsync(YouTubeText.RemovePrompt(videos[cursor]), cancellationToken).ConfigureAwait(false))
                    {
                        string removed = YouTubeSaveTool.Remove(_library(), null, videos[cursor].Id);
                        if (_library().List().Count == 0)
                        {
                            // The last one gone: the pane closes, so the line goes to the transcript.
                            Tell(removed);
                            return;
                        }

                        Say(removed);
                    }
                    else
                    {
                        _pane.Notice(ChatScreen.KeptNotice);
                    }
                }
                else
                {
                    Say(_play is null ? "Error: " + YouTubeText.NoWindow : await _play(videos[cursor], cancellationToken).ConfigureAwait(false));
                }

                var video = cursor >= 0 && cursor < videos.Count ? videos[cursor] : null;

                // The list again (a play fills in a title and moves a place; the model may save one meanwhile), the cursor on the same
                // video wherever it moved to, or on the row it stood on once it is gone.
                videos = _library().List();
                if (videos.Count == 0)
                {
                    _transcript.Notice(YouTubeText.NoneSaved);
                    return;
                }

                int same = video is null ? -1 : videos.ToList().FindIndex(v => v.Id == video.Id);
                cursor = same >= 0 ? same : Math.Clamp(cursor, 0, videos.Count - 1);
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    private const string ErrorPrefix = "Error: ";

    /// <summary>An answer on the transcript, for one the pane closes after: an error as one, anything else a notice.</summary>
    private void Tell(string result)
    {
        if (result.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            _transcript.Error(result[ErrorPrefix.Length..]);
        }
        else
        {
            _transcript.Notice(result);
        }
    }

    /// <summary>An answer on the pane's status line: an error as one, anything else a notice.</summary>
    private void Say(string result)
    {
        if (result.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            _pane.Error(result[ErrorPrefix.Length..]);
        }
        else
        {
            _pane.Notice(result);
        }
    }

    /// <summary>The yes/no page kept under the list (<see cref="SettingsMenu.ConfirmAsync"/>'s rows, keys and hotkeys, the cursor on No): true for Enter on Yes alone.</summary>
    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        var page = new MenuPage(question, SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys) { Hotkeys = SettingsMenu.ConfirmHotkeys };
        var picked = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
        return picked is { Row: 1 };
    }
}
