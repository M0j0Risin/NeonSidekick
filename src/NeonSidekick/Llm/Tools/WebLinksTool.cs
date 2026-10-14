using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Viewer;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the saved-link tools reach (later still on 2026-10-13): the loaded profile's <see cref="BrowserLinks"/> (a func, so a profile
/// switch counts), the clock whose zone dates the list and whose timer caps a wait for the window, and — with a Browser window (the
/// screen on Windows) — the page it shows (null while it cannot say: none open, no web page, a bot check in it) and the ☆ menu's refresh.
/// Headless and macOS give neither window hook.
/// </summary>
public sealed record WebLinkHooks(Func<BrowserLinks> Links, TimeProvider Time, Func<Task<(string Url, string? Title)?>>? CurrentPage = null, Action? Changed = null);

/// <summary>
/// <c>web_links(filter?)</c> (later still on 2026-10-13, the user's ask): the profile's saved links (<see cref="BrowserLinks"/>, the ones
/// <c>/browser list</c> and the Browser window's ☆ menu show), numbered by their place in the whole list so <c>web_save_link</c> can name one
/// by it, each with its name, address and when it was saved. <c>filter</c> keeps the links holding every one of its words
/// (<see cref="BrowserLinkText.MatchesWords"/>); at most <see cref="MaxListed"/> rows. Reads the file only; headless and macOS too, and
/// plan mode keeps it. The <c>youtube_saved</c> shape.
/// </summary>
public sealed class WebLinksTool : AIFunction
{
    public const string ToolName = "web_links";
    public const string FilterArgument = "filter";

    /// <summary>The most rows one call lists; past it, a line says how many more and to filter.</summary>
    public const int MaxListed = 100;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "filter": { "type": "string", "description": "Words to look for in the links' names and addresses; a link must hold every word. Leave it out to list them all." }
          }
        }
        """);

    private readonly WebLinkHooks _hooks;

    public WebLinksTool(WebLinkHooks hooks)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
    }

    public override string Name => ToolName;

    /// <summary>Pinned: the model reads it.</summary>
    public override string Description =>
        "Lists the user's saved web links (their bookmarks), numbered, each with its name, address and when it was saved; filter narrows them by words. " +
        OpenUrlTool.ToolName + " opens one.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(List(_hooks.Links().List(), ToolArguments.ReadString(arguments, FilterArgument).Trim(), _hooks.Time.LocalTimeZone));
    }

    /// <summary>The answer over <paramref name="links"/>: the caption, a row per kept link (at most <see cref="MaxListed"/>), the line past the cap. Pure.</summary>
    public static string List(IReadOnlyList<BrowserLink> links, string filter, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(filter);
        if (links.Count == 0)
        {
            return BrowserLinkText.NoneSavedForModel;
        }

        var kept = Enumerable.Range(0, links.Count).Where(i => BrowserLinkText.MatchesWords(filter, links[i])).ToList();
        if (kept.Count == 0)
        {
            return BrowserLinkText.NoneMatch(filter, links.Count);
        }

        var sb = new StringBuilder(BrowserLinkText.ToolCaption(links.Count, kept.Count, filter));
        foreach (int i in kept.Take(MaxListed))
        {
            sb.Append('\n').Append(BrowserLinkText.ToolRow(i + 1, links[i], zone));
        }

        if (kept.Count > MaxListed)
        {
            sb.Append('\n').Append(BrowserLinkText.ToolMore(kept.Count - MaxListed));
        }

        return sb.ToString();
    }
}
