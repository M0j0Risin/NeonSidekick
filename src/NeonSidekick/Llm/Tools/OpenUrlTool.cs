using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>open_url(url?, urls?)</c>: hands a link — or up to <see cref="MaxUrls"/> of them — to the
/// user's default browser through <see cref="WebAccess.OpenUrl"/> (<see cref="PersonaFile.OpenInBrowser"/>
/// in the app: a shell execute, never waited for). Nothing is fetched and the LAN rule does not
/// apply: it is the user's own browser opening the page, not the app reading it. One link answers
/// <c>Opened … in your browser</c>; several a header and a line each, a bad one its <c>Error:</c>
/// line in place. Offered under the setting <c>Web tools</c> with the other two.
///
/// <para><b>The Browser window</b> (later still on 2026-10-13, the user's ask: <c>Web open links in</c>, <see cref="OpenLinksIn"/>): under
/// <c>browser-window</c>, with a window hook given (the screen on Windows), a link opens in the app's Browser window instead, through the
/// one open every caller uses (<c>ChatScreen.OpenInBrowser</c>: one close notice per visit, the cookies handed over on close). The window
/// shows one page at a time, so only the first link opens and the rest are named as not opened (the user's pick), and the schema offers
/// <c>url</c> alone. When the window cannot take it now — no runtime, a bot check waiting in it — the link goes to the default browser
/// with the reason. Headless and macOS give no hook: the default browser, as before.</para>
///
/// <para><b>YouTube links</b> (2026-10-16, <c>YouTube open links in</c>): with a YouTube hook given (the screen with a video player), each
/// link goes to it first, before either path above. A link it plays has its answer as its line (a player of the app's own, or the Browser
/// window: one at a time, so the next playable links are named as not opened; the default browser: each one); a link it passes on goes on
/// through <c>Web open links in</c>, its note (if any) before that line. The hook passes on whatever is not a video or <c>list=</c> link,
/// whatever YouTube refused in the player, and everything while YouTube tools are off. Headless gives no hook.</para>
/// </summary>
public sealed class OpenUrlTool : AIFunction
{
    public const string ToolName = "open_url";
    public const string UrlArgument = "url";
    public const string UrlsArgument = "urls";

    /// <summary>The most links one call opens: enough for "open these", too few for a tab storm.</summary>
    public const int MaxUrls = 5;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The link to open (http or https)." },
            "urls": { "type": "array", "items": { "type": "string" }, "description": "Several links to open at once, at most 5. Either url or urls." }
          }
        }
        """);

    /// <summary>The schema under <c>browser-window</c>: one link, the window showing one page at a time.</summary>
    private static readonly JsonElement WindowSchema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The link to open (http or https)." }
          }
        }
        """);

    /// <summary>The description under <c>browser-window</c>. Pinned: the model reads it.</summary>
    public const string WindowDescription =
        "Opens a web page for the user in the app's Browser window, on their screen; the window shows one page at a time. " +
        "Use it when they ask to open, show or see a link rather than to have it read out.";

    private readonly WebAccess _web;
    private readonly Func<AppSettingsData>? _effective;
    private readonly Func<Uri, string?>? _openInWindow;
    private readonly Func<Uri, CancellationToken, Task<YouTubeLinkOutcome>>? _openYouTube;

    /// <param name="web">The default browser's opener (<see cref="WebAccess.OpenUrl"/>).</param>
    /// <param name="effective">The settings in force, read at each call for <c>Web open links in</c>; null keeps the default browser.</param>
    /// <param name="openInWindow">Opens a link in the Browser window and returns null, or returns why it cannot now (<c>IBrowserWindow.Unavailable</c>)
    /// without opening; null (headless, macOS) keeps the default browser.</param>
    /// <param name="openYouTube">A YouTube link played where <c>YouTube open links in</c> says (2026-10-16; <c>ChatScreen.OpenYouTubeForToolAsync</c>),
    /// or passed on; null (headless) passes every link on.</param>
    public OpenUrlTool(WebAccess web, Func<AppSettingsData>? effective = null, Func<Uri, string?>? openInWindow = null, Func<Uri, CancellationToken, Task<YouTubeLinkOutcome>>? openYouTube = null)
    {
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _effective = effective;
        _openInWindow = openInWindow;
        _openYouTube = openYouTube;
    }

    public override string Name => ToolName;

    public override string Description => InWindow
        ? WindowDescription
        : "Opens a web page in the user's own browser, on their screen. Use it when they ask to open, show or see a link rather than to have it read out; " +
          "one link in url, or up to " + MaxUrls.ToString(System.Globalization.CultureInfo.InvariantCulture) + " in urls.";

    public override JsonElement JsonSchema => InWindow ? WindowSchema : Schema;

    /// <summary>Whether links go to the Browser window now: <c>Web open links in</c> is <c>browser-window</c> and there is a window to ask.</summary>
    private bool InWindow => _openInWindow is not null && _effective is not null && OpenLinksIn.Resolve(_effective()) == OpenLinksInKind.BrowserWindow;

    /// <summary>Opens each link and describes what happened. The argument reading is <see cref="InvokeCoreAsync"/>'s.</summary>
    public string Open(IReadOnlyList<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (urls.Count == 0)
        {
            return WebText.NoUrl;
        }

        if (urls.Count > MaxUrls)
        {
            return WebText.TooManyLinks;
        }

        if (InWindow)
        {
            return OpenInWindow(urls);
        }

        var lines = new List<string>(urls.Count);
        int opened = 0;
        string last = "";
        foreach (var raw in urls)
        {
            var parsed = WebFetcher.ParseUrl(raw);
            if (parsed is null)
            {
                lines.Add(WebText.NotHttp(raw.Trim()));
                continue;
            }

            try
            {
                _web.OpenUrl(parsed.AbsoluteUri);
                NeonSidekick.Diagnostics.DiagnosticLog.Info(WebFetcher.Category, "Opened in the browser: " + parsed.AbsoluteUri);
                opened++;
                last = parsed.AbsoluteUri;
                lines.Add(WebText.OpenedLine(parsed.AbsoluteUri));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
            {
                lines.Add(WebText.OpenFailed(parsed.AbsoluteUri, ex.Message));
            }
        }

        if (urls.Count == 1)
        {
            return opened == 1 ? WebText.OpenedOne(last) : lines[0];
        }

        var sb = new StringBuilder(opened == 0 ? WebText.NoneOpened(urls.Count) : WebText.OpenedHeader(opened));
        foreach (var line in lines)
        {
            sb.Append('\n').Append(line);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The first web link in the Browser window (or the default browser with the reason when it cannot take it), any before it that are
    /// not web links said so, the rest named as not opened (2026-10-14, the review: only the first was tried, so a bad first link left
    /// a good second one unopened).
    /// </summary>
    private string OpenInWindow(IReadOnlyList<string> urls)
    {
        var lines = new List<string>(3);
        int at = 0;
        Uri? parsed = null;
        for (; at < urls.Count && (parsed = WebFetcher.ParseUrl(urls[at])) is null; at++)
        {
            lines.Add(WebText.NotHttp(urls[at].Trim()));
        }

        if (parsed is not null)
        {
            lines.Add(OpenOneInWindow(parsed));
            at++;
        }

        if (at < urls.Count)
        {
            lines.Add(WebText.OneAtATime(urls.Skip(at).Select(u => u.Trim()).ToList()));
        }

        return string.Join('\n', lines);
    }

    private string OpenOneInWindow(Uri parsed)
    {
        if (_openInWindow!(parsed) is not { } reason)
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Info(WebFetcher.Category, "Opened in the Browser window: " + parsed.AbsoluteUri);
            return WebText.OpenedInWindow(parsed.AbsoluteUri);
        }

        try
        {
            _web.OpenUrl(parsed.AbsoluteUri);
            NeonSidekick.Diagnostics.DiagnosticLog.Info(WebFetcher.Category, "Opened in the browser (the Browser window could not take it: " + reason + "): " + parsed.AbsoluteUri);
            return WebText.WindowFellBack(parsed.AbsoluteUri, reason);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            return WebText.OpenFailed(parsed.AbsoluteUri, ex.Message);
        }
    }

    /// <summary>
    /// <see cref="Open"/> with YouTube's links first (2026-10-16): each web link the hook takes answers its own line; the rest go on through
    /// <see cref="Open"/>, each hook's note before that line. With no hook, <see cref="Open"/> alone.
    /// </summary>
    public async Task<string> OpenAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (_openYouTube is null || urls.Count == 0 || urls.Count > MaxUrls)
        {
            return Open(urls);
        }

        var lines = new List<string>();
        var notes = new List<string>();
        var passOn = new List<string>();
        var notOpened = new List<string>();
        bool onePlayerTaken = false;
        foreach (string raw in urls)
        {
            var parsed = WebFetcher.ParseUrl(raw);
            if (parsed is null || !YouTube.YouTubeIds.IsYouTubeHost(parsed.Host))
            {
                passOn.Add(raw);
                continue;
            }

            if (onePlayerTaken && YouTube.YouTubeIds.IsPlayable(parsed.AbsoluteUri))
            {
                notOpened.Add(raw.Trim());   // one player, one page at a time: the first playable link has it
                continue;
            }

            var outcome = await _openYouTube(parsed, cancellationToken).ConfigureAwait(false);
            if (outcome.Answer is { } answer)
            {
                lines.Add(answer);
                onePlayerTaken |= outcome.OnePlayer;
                continue;
            }

            if (outcome.Note is { Length: > 0 } note)
            {
                notes.Add(note);
            }

            passOn.Add(raw);
        }

        if (notOpened.Count > 0)
        {
            lines.Add(WebText.OneAtATime(notOpened));
        }

        if (passOn.Count > 0)
        {
            lines.Add(string.Concat(notes) + Open(passOn));
        }

        return string.Join('\n', lines);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadStringList(arguments, UrlsArgument, out var several, out string raw))
        {
            return WebText.BadUrlList(UrlsArgument, raw);
        }

        var urls = new List<string>(several.Count + 1);
        string one = ToolArguments.ReadString(arguments, UrlArgument);
        if (!string.IsNullOrWhiteSpace(one))
        {
            urls.Add(one);
        }

        urls.AddRange(several);
        return await OpenAsync(urls, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// What open_url's YouTube hook did with a link (2026-10-16): <see cref="Answer"/> its whole line when it played or handed it off (and
/// <see cref="OnePlayer"/> when the place shows one at a time: the video window, the Web UI's tab, the Browser window), or null to pass the
/// link on through <c>Web open links in</c>, <see cref="Note"/> (if any) before that line.
/// </summary>
public sealed record YouTubeLinkOutcome(string? Answer, string? Note = null, bool OnePlayer = false)
{
    /// <summary>Passed on, with nothing to say.</summary>
    public static YouTubeLinkOutcome PassOn { get; } = new(Answer: null);
}
