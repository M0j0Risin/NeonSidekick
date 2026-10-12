using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_reference(query?, file?, page?, picture?, reread?, store?)</c> (2026-10-11, the plan's phase 3): a store's reference
/// documents (<see cref="ShoppingReference"/>) — read again first whenever a file in the folder changed — searched for a product's words,
/// one page read whole (its picture too, with <c>picture: true</c>: a floor map is a picture), or, with nothing asked, listed. The
/// picture rides the next message the <c>view_image</c> way (<see cref="ToolImageResult"/>), so the Image tools need not be on.
/// <c>reread: true</c> reads every file again. Reads only.
/// </summary>
public sealed class ShoppingReferenceTool(ShoppingList list, ShoppingReference reference) : ShoppingTool(list)
{
    public const string ToolName = "shopping_reference";
    public const string QueryArgument = "query";
    public const string FileArgument = "file";
    public const string PageArgument = "page";
    public const string PictureArgument = "picture";
    public const string RereadArgument = "reread";

    /// <summary>The most of a page's text one answer carries.</summary>
    public const int MaxPageChars = 12_000;

    private readonly ShoppingReference _reference = reference ?? throw new ArgumentNullException(nameof(reference));

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "A product's words to look for (\"bread\", \"canned tomatoes\"): the pages that hold them and their lines." },
            "file": { "type": "string", "description": "A document's name, as listed, to read one page of." },
            "page": { "type": "integer", "description": "The page of file to read (1 first)." },
            "picture": { "type": "boolean", "description": "true sends the page's picture too (a floor map, a table) for you to look at." },
            "reread": { "type": "boolean", "description": "true reads every document again." },
            {{StoreProperty}}
          }
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Looks in a store's reference documents (a store guide, a floor map) the user put in its folder: search them for a product's words, read a page (its picture too), or list them. " +
        "Use it when the shopping list's facts do not place an item, then record what you find with " + ShoppingPlaceTool.ToolName + " (source reference).";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> RunWithPicturesAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (StoreOf(arguments, out string? error) is not { } shop)
        {
            return error!;
        }

        if (!ToolArguments.TryReadBoolean(arguments, RereadArgument, out bool? reread, out string raw)
            || !ToolArguments.TryReadBoolean(arguments, PictureArgument, out bool? picture, out raw))
        {
            return "Error: reread and picture must be true or false, not " + raw + ".";
        }

        if (!ToolArguments.TryReadInt32(arguments, PageArgument, out int? page, out string rawPage) || page is < 1)
        {
            return "Error: page must be a whole number from 1, not " + rawPage + ".";
        }

        string reading = "";
        if (reread is true || _reference.IsStale(shop))
        {
            var summary = await _reference.RefreshAsync(shop, reread is true, cancellationToken).ConfigureAwait(false);
            reading = reread is true || summary.Problems.Count > 0 ? ShoppingText.ReferenceRead(shop, summary) + "\n" : "";
        }

        string file = ToolArguments.ReadString(arguments, FileArgument).Trim();
        if (file.Length > 0)
        {
            int number = page ?? 1;
            if (List.Store.RefPage(shop.Id, file, number) is not { } found)
            {
                return reading + ShoppingText.NoSuchPage(shop, file, number);
            }

            if (picture is not true)
            {
                return reading + ShoppingText.ReferencePage(found, false, MaxPageChars);
            }

            if (found.Image is not { } path)
            {
                return reading + ShoppingText.ReferencePage(found, false, MaxPageChars) + "\n" + ShoppingText.NoPicture(found.File, found.Page);
            }

            var image = ImageFile.Load(path, out string? failure);
            return image is null
                ? reading + ShoppingText.ReferencePage(found, false, MaxPageChars) + "\n" + ShoppingText.PictureUnreadable(failure ?? "unknown")
                : new ToolImageResult(reading + ShoppingText.ReferencePage(found, true, MaxPageChars), [image]);
        }

        string query = ToolArguments.ReadString(arguments, QueryArgument).Trim();
        if (query.Length > 0)
        {
            var hits = _reference.Search(shop, query);
            return reading + (hits.Count > 0 ? ShoppingText.References(hits) : ShoppingText.NothingInReference(shop, query));
        }

        return reading + ShoppingText.ReferenceDocs(shop, List.Store.RefDocs(shop.Id), List.Store.ReferenceFolder(shop));
    }

    /// <summary>The answer's text alone (its picture dropped).</summary>
    protected override async ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        await RunWithPicturesAsync(arguments, cancellationToken).ConfigureAwait(false) switch
        {
            ToolImageResult pictures => pictures.Text,
            var text => text as string ?? "",
        };
}
