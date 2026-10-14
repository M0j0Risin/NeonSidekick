using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>web_save_link(action, url, name?)</c> (later still on 2026-10-13, the user's ask): a link added to the profile's saved links
/// (<see cref="BrowserLinks"/>), taken off them, or renamed — the list <c>/browser list</c> and the Browser window's ☆ menu show, which
/// refreshes after a change. <c>url</c> is an address (a bare <c>example.com/page</c> made https, as <c>/browser save</c> does, so the two
/// save one link alike), for remove and rename also its number in <c>web_links</c>' list, and — with a Browser window — <c>current</c>, the
/// page it shows (its title the name when none is given; never a bot check's page). The <c>youtube_save</c> shape; headless and macOS too,
/// without <c>current</c>. Plan mode drops it.
/// </summary>
public sealed class WebSaveLinkTool : AIFunction
{
    public const string ToolName = "web_save_link";
    public const string ActionArgument = "action";
    public const string UrlArgument = "url";
    public const string NameArgument = "name";

    /// <summary>The actions.</summary>
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Rename = "rename";

    /// <summary>The word for the page the Browser window shows.</summary>
    public const string Current = "current";

    private static readonly JsonElement Schema = SchemaFor(current: false);
    private static readonly JsonElement CurrentSchema = SchemaFor(current: true);

    private static JsonElement SchemaFor(bool current)
    {
        string url = current
            ? "The link's address; for remove and rename also its number in web_links' list; \\\"current\\\" for the page the Browser window shows."
            : "The link's address; for remove and rename also its number in web_links' list.";
        return ToolSchema.Parse($$"""
            {
              "type": "object",
              "properties": {
                "action": { "type": "string", "enum": ["add", "remove", "rename"], "description": "add saves the link; remove takes it off the saved links; rename changes its name." },
                "url": { "type": "string", "description": "{{url}}" },
                "name": { "type": "string", "description": "The name to save it under (add) or its new name (rename; empty takes the name off)." }
              },
              "required": ["action", "url"]
            }
            """);
    }

    private readonly WebLinkHooks _hooks;

    public WebSaveLinkTool(WebLinkHooks hooks)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
    }

    public override string Name => ToolName;

    /// <summary>Pinned: the model reads it.</summary>
    public override string Description =>
        "Saves a web link to the user's saved links (their bookmarks), removes one, or renames one. Save only what the user asked to keep." +
        (_hooks.CurrentPage is null ? "" : " url \"current\" is the page the app's Browser window shows.");

    public override JsonElement JsonSchema => _hooks.CurrentPage is null ? Schema : CurrentSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string action = ToolArguments.ReadString(arguments, ActionArgument).Trim().ToLowerInvariant();
        string url = ToolArguments.ReadString(arguments, UrlArgument).Trim();
        string name = ToolArguments.ReadString(arguments, NameArgument);
        return action switch
        {
            Add => await AddAsync(url, name).ConfigureAwait(false),
            Remove or Rename => await ChangeAsync(action, url, name).ConfigureAwait(false),
            _ => BrowserLinkText.NotAnAction(action),
        };
    }

    private async Task<string> AddAsync(string url, string name)
    {
        string address;
        string? named = BrowserLinks.CleanName(name);
        if (url.Equals(Current, StringComparison.OrdinalIgnoreCase))
        {
            if (await CurrentPageAsync().ConfigureAwait(false) is not { } page)
            {
                return BrowserLinkText.NothingCurrent;
            }

            address = page.Url;
            named ??= BrowserLinks.UsableTitle(page.Title, page.Url);
        }
        else if (WebFetcher.ParseUrl(url) is { } parsed)
        {
            address = parsed.AbsoluteUri;
        }
        else
        {
            return BrowserLinkText.NotAWebAddress(url);
        }

        var outcome = _hooks.Links().Add(address, named, out var saved);
        if (BrowserLinkText.Refusal(outcome) is { } refusal)
        {
            return refusal;
        }

        _hooks.Changed?.Invoke();
        return outcome == BrowserLinkOutcome.AlreadySaved ? BrowserLinkText.AlreadySavedForModel(saved!) : BrowserLinkText.Saved(saved!);
    }

    private async Task<string> ChangeAsync(string action, string url, string name)
    {
        var links = _hooks.Links();
        BrowserLink? link;
        if (url.Equals(Current, StringComparison.OrdinalIgnoreCase))
        {
            if (await CurrentPageAsync().ConfigureAwait(false) is not { } page)
            {
                return BrowserLinkText.NothingCurrent;
            }

            url = page.Url;
            link = links.Find(page.Url);
        }
        else
        {
            link = links.Resolve(url);
        }

        if (link is null)
        {
            return BrowserLinkText.NotSavedForModel(url);
        }

        var outcome = action == Remove ? links.Remove(link.Url) : links.Rename(link.Url, name);
        if (outcome == BrowserLinkOutcome.NotSaved)
        {
            return BrowserLinkText.NotSavedForModel(url);
        }

        if (BrowserLinkText.Refusal(outcome) is { } refusal)
        {
            return refusal;
        }

        _hooks.Changed?.Invoke();
        return action == Remove ? BrowserLinkText.Removed(link) : BrowserLinkText.Renamed(link, BrowserLinks.CleanName(name));
    }

    /// <summary>The page the Browser window shows, waited on at most <see cref="BrowserLinks.CurrentPageWait"/>; null with no window hook or no page.</summary>
    private async Task<(string Url, string? Title)?> CurrentPageAsync()
    {
        if (_hooks.CurrentPage is not { } current)
        {
            return null;
        }

        try
        {
            return await current().WaitAsync(BrowserLinks.CurrentPageWait, _hooks.Time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
