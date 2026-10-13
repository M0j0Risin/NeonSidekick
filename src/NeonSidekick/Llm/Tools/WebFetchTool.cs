using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>web_fetch(url, offset?)</c>: a web page as light Markdown (<see cref="HtmlToMarkdown"/>),
/// <see cref="MaxChars"/> characters at a time — <c>offset</c> continues a long one, the page
/// served from <see cref="PageCache"/> so paging never downloads twice. Plain text, JSON, XML and
/// CSV come back as they are; a PDF or a picture is an error. The result starts with a header line
/// naming the title, the final URL, the window and the engine, so the transcript's one-line note
/// reads <c>Example Domain — https://example.com/ (chars 1–2,100 of 2,100, http)</c>.
/// <para>
/// Since 2026-10-13 a page refused by a bot check that wants a person (<see cref="BotCheck.Offers(FetchResult)"/>) can be shown to the
/// user in the Browser window (<see cref="BotCheckSolver"/>, per <c>Web browser botcheck window</c>); the page they let through is the
/// answer, its engine <see cref="WebText.WindowEngine"/>.
/// </para>
/// </summary>
public sealed class WebFetchTool : AIFunction
{
    public const string ToolName = "web_fetch";
    public const string UrlArgument = "url";
    public const string OffsetArgument = "offset";

    /// <summary>One call's window, the <c>read_file</c> cap.</summary>
    public const int MaxChars = Files.WorkingDirectory.MaxReadChars;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The page's address (http or https)." },
            "offset": { "type": "integer", "description": "Where to continue a long page, in characters: the number the previous result's header named. Leave it out to start at the top." }
          },
          "required": ["url"]
        }
        """);

    private readonly WebAccess _web;
    private readonly Func<AppSettingsData> _effective;
    private readonly BotCheckSolver? _botCheck;

    /// <param name="botCheck">Who shows a bot check to the user (2026-10-13); null where nobody can be asked (headless).</param>
    public WebFetchTool(WebAccess web, Func<AppSettingsData> effective, BotCheckSolver? botCheck = null)
    {
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _botCheck = botCheck;
    }

    public override string Name => ToolName;

    public override string Description =>
        "Fetches a web page and returns its readable content as Markdown, " + MaxChars.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters at a time; " +
        "the header names the offset to continue a long page with. Also reads plain text, JSON, XML and CSV. Use it on a " + WebSearchTool.ToolName + " result or a URL the user gives you.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The window of a fetched page from <paramref name="offset"/>, under its header. Pure; pinned.</summary>
    public static string Format(FetchResult page, int offset)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!page.Ok)
        {
            return page.Error;
        }

        string title = page.Page?.Title ?? "";
        string text = page.Page?.Markdown ?? page.Text;
        int total = text.Length;
        if (offset < 0)
        {
            return WebText.NegativeOffset;
        }

        if (total == 0)
        {
            return WebText.EmptyPage(title, page.FinalUrl, page.Engine);
        }

        if (offset >= total)
        {
            return WebText.OffsetPastEnd(offset, total);
        }

        int end = Math.Min(total, offset + MaxChars);
        string header = WebText.FetchHeader(title, page.FinalUrl, offset + 1, end, total, page.Engine, end < total ? end : null);
        return header + "\n\n" + text[offset..end];
    }

    /// <summary>The fetch itself, apart from the argument reading: the result text the model reads.</summary>
    public async Task<string> FetchAsync(string url, int? offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (string.IsNullOrWhiteSpace(url))
        {
            return WebText.NoUrl;
        }

        var parsed = WebFetcher.ParseUrl(url);
        if (parsed is null)
        {
            return WebText.NotHttp(url.Trim());
        }

        int from = offset ?? 0;
        if (from < 0)
        {
            return WebText.NegativeOffset;
        }

        string key = parsed.AbsoluteUri;
        var page = _web.Pages.TryGet(key);
        if (page is null)
        {
            // A hit web_search listed goes as a click from its results page would (2026-10-11).
            var options = WebAccess.Options(_effective()) with { Referer = _web.Referers.For(parsed) };
            page = await _web.Fetcher.FetchAsync(parsed, options, cancellationToken).ConfigureAwait(false);
            if (_botCheck is not null && BotCheck.Offers(page))
            {
                page = await _web.Fetcher.PastBotCheckAsync(parsed, page, options, _botCheck, BotCheckUse.Fetch, cancellationToken).ConfigureAwait(false);
            }

            _web.Pages.Put(key, page);
        }

        return Format(page, from);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, OffsetArgument, out var offset, out var raw))
        {
            return ClockText.BadInteger(OffsetArgument, raw);
        }

        return await FetchAsync(ToolArguments.ReadString(arguments, UrlArgument), offset, cancellationToken).ConfigureAwait(false);
    }
}
