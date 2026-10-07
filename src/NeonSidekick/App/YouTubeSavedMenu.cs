using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;
using NeonSidekick.YouTube;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/youtube saved</c> (2026-10-07, the user's ask): the profile's saved videos on a <see cref="MenuPane"/> page in the order they
/// were saved, each row <see cref="YouTubeText.SavedRow"/>'s (a watched one dim) under the count (<see cref="YouTubeText.SavedCaption"/>).
/// Enter or a double-click plays the highlighted one in the video window — where it was left (<see cref="YouTubeLibrary.ResumeAt"/>,
/// through the play the screen hands in) — the pane kept open with the player's answer on its status line; the
/// <see cref="YouTubeText.RemoveButton"/> (a click, or <see cref="YouTubeText.RemoveKey"/>) takes it off the list after a yes/no (the
/// cursor on No). The <see cref="ProcessMenu"/> shape, the list read again after every act. Without the pane the screen prints the
/// rows instead (<c>ChatScreen.ListSavedVideos</c>).
/// </summary>
internal sealed class YouTubeSavedMenu
{
    /// <summary>The page's buttons: the one.</summary>
    public static readonly IReadOnlyList<MenuButton> Buttons = [new(YouTubeText.RemoveButton, YouTubeText.RemoveKey)];

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

    /// <summary>A row: <see cref="YouTubeText.SavedRow"/> escaped, dim once watched.</summary>
    public static string RowMarkup(YouTubeSaved video)
    {
        ArgumentNullException.ThrowIfNull(video);
        string row = YouTubeText.SavedRow(video);
        return video.Watched ? Theme.DimMarkup(row) : Markup.Escape(row);
    }

    /// <summary>Shows the list until ESC; none saved is <see cref="YouTubeText.NoneSaved"/> on the transcript.</summary>
    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        var videos = _library().List();
        if (videos.Count == 0)
        {
            _transcript.Notice(YouTubeText.NoneSaved);
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
                List<string> rows = shown.Count > 0 ? shown.Select(i => RowMarkup(listed[i])).ToList() : [MenuFilter.NoMatchRow(filter)];
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

                if (pick.Row < 0 || pick.Row >= shown.Count)
                {
                    continue;   // the no-match row
                }

                cursor = shown[pick.Row];
                var video = videos[cursor];
                if (pick.Button < 0)
                {
                    Say(_play is null ? "Error: " + YouTubeText.NoWindow : await _play(video, cancellationToken).ConfigureAwait(false));
                }
                else if (await ConfirmAsync(YouTubeText.RemovePrompt(video), cancellationToken).ConfigureAwait(false))
                {
                    string removed = YouTubeSaveTool.Remove(_library(), null, video.Id);
                    if (_library().List().Count == 0)
                    {
                        // The last one gone: the pane closes, so the line goes to the transcript.
                        _transcript.Notice(removed);
                        return;
                    }

                    Say(removed);
                }
                else
                {
                    _pane.Notice(ChatScreen.KeptNotice);
                }

                // The list again (a play fills in a title and moves a place; the model may save one meanwhile), the cursor on the same
                // video wherever it moved to, or on the row it stood on once it is gone.
                videos = _library().List();
                if (videos.Count == 0)
                {
                    _transcript.Notice(YouTubeText.NoneSaved);
                    return;
                }

                int same = videos.ToList().FindIndex(v => v.Id == video.Id);
                cursor = same >= 0 ? same : Math.Clamp(cursor, 0, videos.Count - 1);
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>An answer on the pane's status line: an error as one, anything else a notice.</summary>
    private void Say(string result)
    {
        const string Prefix = "Error: ";
        if (result.StartsWith(Prefix, StringComparison.Ordinal))
        {
            _pane.Error(result[Prefix.Length..]);
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
