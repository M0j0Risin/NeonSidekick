using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_place(term, kind, location?, section?, source?, note?, store?, forget?)</c> (2026-10-11): a layout fact of a store recorded —
/// a product, a brand or a category at an aisle or zone (made when the store has no such label) or in a section alone — or forgotten
/// (<c>forget: true</c>). Every line of the list the term covers moves with it (<see cref="ShoppingList.Place"/>): what the old skill's
/// "apply it in two places" rule did by hand. A guess (<c>source: guess</c>) is marked wherever it places a line.
/// </summary>
public sealed class ShoppingPlaceTool(ShoppingList list) : ShoppingTool(list)
{
    public const string ToolName = "shopping_place";
    public const string TermArgument = "term";
    public const string KindArgument = "kind";
    public const string LocationArgument = "location";
    public const string SectionArgument = "section";
    public const string SourceArgument = "source";
    public const string NoteArgument = "note";
    public const string ForgetArgument = "forget";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "term": { "type": "string", "description": "The product, brand or category: \"Babybel\", \"Goya\", \"Cheese\"." },
            "kind": { "type": "string", "enum": ["item", "brand", "category"], "description": "What the term names; a brand places every product of it, a category every product of that kind." },
            "location": { "type": "string", "description": "The aisle or zone: \"Aisle 7\", \"Dairy\", \"Deli\". Made if the store has none by that name." },
            "section": { "type": "string", "description": "The department: \"Dairy & Eggs\", \"International Foods\"; alone when the product has no aisle." },
            "source": { "type": "string", "enum": ["user", "reference", "guess"], "description": "user: the user said so; reference: read in the store's documents; guess: your best guess (tell the user)." },
            "note": { "type": "string", "description": "Anything more: \"end of the aisle\", \"top shelf\"." },
            "forget": { "type": "boolean", "description": "true forgets where the term is instead." },
            {{StoreProperty}}
          },
          "required": ["term"]
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Records where a product, brand or category is at a store (or forgets it); every line of the shopping list it covers moves there. " +
        "Use it when the user says where something is, when a reference document says, or for a best guess marked as one.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string term = ToolArguments.ReadString(arguments, TermArgument).Trim();
        if (term.Length == 0)
        {
            return ValueTask.FromResult("Error: " + ShoppingText.NoTerm + ".");
        }

        if (StoreOf(arguments, out string? error) is not { } shop)
        {
            return ValueTask.FromResult(error!);
        }

        if (!ToolArguments.TryReadBoolean(arguments, ForgetArgument, out bool? forget, out string raw))
        {
            return ValueTask.FromResult("Error: forget must be true or false, not " + raw + ".");
        }

        if (forget is true)
        {
            return ValueTask.FromResult(List.Unplace(shop, term));
        }

        string kindWord = ToolArguments.ReadString(arguments, KindArgument);
        string? kind = kindWord.Trim().Length == 0 ? ShoppingKinds.Item : ShoppingKinds.Parse(kindWord);
        if (kind is null)
        {
            return ValueTask.FromResult("Error: kind must be item, brand or category, not " + kindWord + ".");
        }

        string sourceWord = ToolArguments.ReadString(arguments, SourceArgument).Trim().ToLowerInvariant();
        string source = sourceWord.Length == 0 ? ShoppingSources.User : sourceWord;
        if (!ShoppingSources.All.Contains(source))
        {
            return ValueTask.FromResult("Error: source must be user, reference or guess, not " + sourceWord + ".");
        }

        return ValueTask.FromResult(List.Place(shop, term, kind, ToolArguments.ReadString(arguments, LocationArgument), ToolArguments.ReadString(arguments, SectionArgument), source, ToolArguments.ReadString(arguments, NoteArgument).Trim()));
    }
}
