using System.Globalization;
using NeonSidekick.Docker;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The bare <c>/docker</c> screen (2026-10-02, the <see cref="SessionsMenu"/> shape): every container on a
/// <see cref="MenuPane"/> page, running first — a state glyph in the theme's colours (● running, ◐ paused, ○ stopped), the
/// name padded to the widest, the health, the ports, the image and compose project dim. Enter or a double-click opens the
/// container's page (<see cref="RowTitle"/>) with what fits its state: <c>stop</c>, <c>restart</c> and <c>pause</c> for a
/// running one, each after a yes/no kept under the list (the cursor on No), <c>start</c> or <c>unpause</c> at once, <c>logs</c>
/// (the last <see cref="DockerCommand.DefaultLogLines"/> lines into the transcript, the pane closed), <c>open :port</c> per
/// published port (the browser, through the screen's existing opener — no process of Docker's own is started) and
/// <c>copy id</c>. The user's own hand: <c>Docker writes</c> never judges it; every change is audited. The button on the
/// title row reads the list again. Mid-turn the list shows and a pick is refused. Without the pane the list prints as lines.
/// </summary>
internal sealed class DockerMenu
{
    // The key hints. Pinned.
    public const string Title = DockerText.Glyph + " Docker";
    public const string Keys = "Enter = open · r = refresh · " + MenuFilter.TypeAndCloseKeys;   // type to filter since 2026-10-07
    public const string RowKeys = SettingsMenu.PickKeys;

    public const string EmptyNotice = "(" + DockerText.Glyph + " no containers)";

    /// <summary>The title row's button: the list read again.</summary>
    public const string RefreshButton = "⟳ refresh";

    /// <summary>The key that is the button.</summary>
    public const char RefreshKey = 'r';

    /// <summary>The page's buttons.</summary>
    public static readonly IReadOnlyList<MenuButton> Buttons = [new(RefreshButton, RefreshKey)];

    public const string LogsWord = "logs";
    public const string CopyIdWord = "copy id";
    public const string OpenWord = "open";

    /// <summary>The acts that ask first.</summary>
    public static readonly IReadOnlySet<string> AskFirst = new HashSet<string>(StringComparer.Ordinal) { "stop", "restart", "pause" };

    /// <summary>What a declined confirmation says on the status line: the transcript's word.</summary>
    public const string KeptNotice = ChatScreen.KeptNotice;

    private readonly DockerSession _docker;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly Action<string> _openUrl;
    private readonly Func<string, bool> _copy;

    /// <param name="docker">The door: the list is read when the pane opens and again after every change.</param>
    /// <param name="transcript">Where the lines outside the pane go.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="openUrl">What <c>open :port</c> hands its URL to: the screen's browser opener.</param>
    /// <param name="copy">What <c>copy id</c> writes the id with, true on success.</param>
    public DockerMenu(DockerSession docker, INoticeSink transcript, MenuPane pane, Action<string> openUrl, Func<string, bool> copy)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
    }

    /// <summary>Where a notice goes: the pane's status line while the list is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    // ── Pinned statics ──────────────────────────────────────────────────────

    /// <summary>The container page's title: <c>🐳 Docker › mysql_dev</c>.</summary>
    public static string RowTitle(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return Title + " › " + container.Name;
    }

    /// <summary>The state glyph: ● running, ◐ paused, ○ anything else.</summary>
    public static string Glyph(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container.Running ? "●" : container.Paused ? "◐" : "○";
    }

    /// <summary>
    /// A list row: the glyph (green running, red unhealthy, amber paused, dim stopped), the name padded to
    /// <paramref name="nameWidth"/>, the state and health, the ports, then the image and project dim.
    /// </summary>
    public static string RowMarkup(DockerContainer container, int nameWidth)
    {
        ArgumentNullException.ThrowIfNull(container);
        var color = container.Health == "unhealthy" ? Theme.Bad : container.Running ? Theme.Good : container.Paused ? Theme.Warn : Theme.Dim;
        string state = container.State + (container.Health is { } health ? " (" + health + ")" : "");
        string ports = DockerText.Ports(container.Ports);
        string tail = container.Image + (container.Project is { } project ? " · " + project : "");
        return Theme.ColorMarkup(color, Glyph(container)) + " " + Markup.Escape(container.Name.PadRight(nameWidth)) + "  " + Markup.Escape(state)
            + (ports.Length > 0 ? "  " + Markup.Escape(ports) : "") + "  " + Theme.DimMarkup(tail);
    }

    /// <summary>
    /// The footer under the list for <paramref name="container"/> (2026-10-07, the user's ask: the image and project are the row's dim
    /// end, cut first): <c>mysql_dev · mysql:8.4 · project shop · 0.0.0.0:3306→3306/tcp · networks shop_default</c>, then Docker's own
    /// status (<c>Up 3 hours (healthy)</c>). Pinned.
    /// </summary>
    public static MenuFooter Footer(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var parts = new List<string> { container.Name, container.Image };
        if (container.Project is { } project)
        {
            parts.Add("project " + project);
        }

        if (DockerText.Ports(container.Ports) is { Length: > 0 } ports)
        {
            parts.Add(ports);
        }

        if (container.Networks.Count > 0)
        {
            parts.Add((container.Networks.Count == 1 ? "network " : "networks ") + string.Join(", ", container.Networks));
        }

        return new MenuFooter(string.Join(" · ", parts), container.Status.Length > 0 ? container.Status : container.State);
    }

    /// <summary>Whether <paramref name="container"/> stays under <paramref name="filter"/>: its name, image or project holds it (2026-10-07).</summary>
    public static bool Matches(string filter, DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return MenuFilter.Matches(filter, container.Name, container.Image + " " + container.Project);
    }

    /// <summary>The container page's words for <paramref name="container"/>'s state, in order.</summary>
    public static IReadOnlyList<string> RowWords(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var words = new List<string>(8);
        if (container.Running)
        {
            words.AddRange(["stop", "restart", "pause"]);
        }
        else if (container.Paused)
        {
            words.Add("unpause");
        }
        else
        {
            words.Add("start");
        }

        words.Add(LogsWord);
        if (container.Running)
        {
            words.AddRange(Urls(container).Select(u => OpenWord + " " + u));
        }

        words.Add(CopyIdWord);
        return words;
    }

    /// <summary>The URLs of a container's published TCP ports, one per host port: <c>http://localhost:8123</c>, https for 443 and 8443, a bound address kept.</summary>
    public static IReadOnlyList<string> Urls(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container.Ports
            .Where(p => p.Public is not null && string.Equals(p.Type, "tcp", StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                int port = p.Public!.Value;
                string host = p.Ip is null or "" or "0.0.0.0" or "::" ? "localhost" : p.Ip.Contains(':', StringComparison.Ordinal) ? "[" + p.Ip + "]" : p.Ip;
                string scheme = p.Private is 443 or 8443 ? "https" : "http";
                return scheme + "://" + host + ":" + port.ToString(CultureInfo.InvariantCulture);
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A container page's row: the word padded, what it does dim after it.</summary>
    public static string RowPageRow(string word) => Markup.Escape(word.PadRight(12)) + Theme.DimMarkup(word switch
    {
        "stop" => "stop it (asks first)",
        "restart" => "stop and start it again (asks first)",
        "pause" => "freeze its processes (asks first)",
        "unpause" => "let its processes run again",
        "start" => "start it",
        LogsWord => "its last " + DockerCommand.DefaultLogLines.ToString(CultureInfo.InvariantCulture) + " log lines, into the transcript",
        CopyIdWord => "its full id, to the clipboard",
        _ => word.StartsWith(OpenWord + " ", StringComparison.Ordinal) ? "in the browser" : "",
    });

    /// <summary>The question before a change from the pane.</summary>
    public static string ConfirmPrompt(string action, DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return DockerText.Glyph + " " + char.ToUpperInvariant(action[0]) + action[1..] + " " + container.Name + " (" + container.Image + ", " + container.Status + ")?";
    }

    public static string OpenedNotice(string url) => "(" + DockerText.Glyph + " opened " + url + ")";

    public static string CopiedNotice(string name) => "(" + DockerText.Glyph + " copied " + name + "'s id)";

    public const string CopyFailedError = "Could not copy the id to the clipboard";

    // ── Screen ──────────────────────────────────────────────────────────────

    /// <summary>Shows the list until ESC.</summary>
    /// <param name="midTurn">The pane opened while a reply runs: the list shows, a pick is refused.</param>
    public async Task ShowAsync(CancellationToken cancellationToken, bool midTurn = false)
    {
        var (read, error) = await _docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
        if (read is null)
        {
            _transcript.Error(error!);
            return;
        }

        var containers = DockerText.Ordered(read);
        if (containers.Count == 0)
        {
            _transcript.Notice(EmptyNotice);
            return;
        }

        if (!_pane.Enabled)
        {
            foreach (string line in DockerCommandResult.Of(Llm.Tools.DockerContainersTool.List(containers, all: true, null, null)).Lines)
            {
                _transcript.Notice(line);
            }

            return;
        }

        int cursor = 0;
        string filter = "";
        try
        {
            while (true)
            {
                int width = containers.Max(c => c.Name.Length);
                // The rows under the filter (2026-10-07): indices into the list.
                var listed = containers;
                var shown = Enumerable.Range(0, listed.Count).Where(i => Matches(filter, listed[i])).ToList();
                List<string> rows = shown.Count > 0 ? shown.Select(i => RowMarkup(listed[i], width)).ToList() : [MenuFilter.NoMatchRow(filter)];
                var page = new MenuPage(Title, rows, MenuFilter.Hint(Keys, filter))
                {
                    Buttons = Buttons,
                    Filter = filter,
                    Caption = MenuFilter.CaptionOrNull(filter, shown.Count, listed.Count),
                    Footer = (_, row) => row < shown.Count ? Footer(listed[shown[row]]) : null,
                };
                var picked = await _pane.PickAsync(page, Math.Max(0, shown.IndexOf(cursor)), cancellationToken).ConfigureAwait(false);
                if (picked is not { } pick)
                {
                    return;
                }

                if (pick.Filter is { } typed)
                {
                    filter = typed;
                    cursor = -1;   // the first row left
                    continue;
                }

                if (pick.Button < 0 && (pick.Row < 0 || pick.Row >= shown.Count))
                {
                    continue;   // the no-match row
                }

                cursor = pick.Row >= 0 && pick.Row < shown.Count ? shown[pick.Row] : Math.Max(0, cursor);
                if (pick.Button < 0)
                {
                    if (midTurn)
                    {
                        Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
                        continue;
                    }

                    var outcome = await PickRowAsync(containers[cursor], cancellationToken).ConfigureAwait(false);
                    if (outcome == RowOutcome.Close)
                    {
                        return;
                    }
                }

                var (again, failed) = await _docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
                if (again is null)
                {
                    Sink.Error(failed!);
                    continue;
                }

                containers = DockerText.Ordered(again);
                if (containers.Count == 0)
                {
                    _pane.Close();
                    _transcript.Notice(EmptyNotice);
                    return;
                }

                cursor = Math.Min(cursor, containers.Count - 1);
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    private enum RowOutcome
    {
        Stay,
        Close,
    }

    /// <summary>The container's page under the list, then the act.</summary>
    private async Task<RowOutcome> PickRowAsync(DockerContainer container, CancellationToken cancellationToken)
    {
        var words = RowWords(container);
        var page = new MenuPage(RowTitle(container), words.Select(RowPageRow).ToList(), RowKeys);
        var picked = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
        if (picked is not { Row: var row })
        {
            return RowOutcome.Stay;
        }

        string word = words[Math.Clamp(row, 0, words.Count - 1)];
        switch (word)
        {
            case LogsWord:
            {
                _pane.Close();
                var result = await DockerCommand.RunAsync(_docker, "logs " + container.Id + " " + DockerCommand.DefaultLogLines.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                foreach (string line in result.Lines)
                {
                    if (result.Failed)
                    {
                        _transcript.Error(line);
                    }
                    else
                    {
                        _transcript.Notice(line);
                    }
                }

                return RowOutcome.Close;
            }

            case CopyIdWord:
                if (_copy(container.Id))
                {
                    Sink.Notice(CopiedNotice(container.Name));
                }
                else
                {
                    Sink.Error(CopyFailedError);
                }

                return RowOutcome.Stay;

            default:
                if (word.StartsWith(OpenWord + " ", StringComparison.Ordinal))
                {
                    string url = word[(OpenWord.Length + 1)..];
                    _openUrl(url);
                    Sink.Notice(OpenedNotice(url));
                    return RowOutcome.Stay;
                }

                if (AskFirst.Contains(word) && !await ConfirmAsync(ConfirmPrompt(word, container), cancellationToken).ConfigureAwait(false))
                {
                    Sink.Notice(KeptNotice);
                    return RowOutcome.Stay;
                }

                string done = await _docker.ActAsync(word, [container], null, DockerText.ByUser, cancellationToken).ConfigureAwait(false);
                if (done.StartsWith("Error:", StringComparison.Ordinal))
                {
                    Sink.Error(done);
                }
                else
                {
                    Sink.Notice("(" + DockerText.Glyph + " " + done + ")");
                }

                return RowOutcome.Stay;
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
