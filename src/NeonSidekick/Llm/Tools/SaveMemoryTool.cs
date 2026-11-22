using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Memory;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The model's way into <see cref="MemoryStore"/>: <c>save_memory(text)</c>. The pattern every
/// tool follows: a hand-written schema parsed once (<see cref="ToolSchema"/>), <c>Name</c>,
/// <c>Description</c> and <c>JsonSchema</c> overridden, the argument read by hand
/// (<see cref="ToolArguments"/>) out of whatever the adapter delivers. Never
/// <c>AIFunctionFactory</c> (reflection). The property name in the schema and the key read below
/// must match; nothing checks that at compile time.
///
/// <para>The result is a sentence for the model and, verbatim, the one dim line the transcript
/// shows (<c>🛠️ remembered: …</c>), so a wrong save is visible and <c>/memory</c> can undo it — the row, or <c>/memory forget</c> for the lot.</para>
/// </summary>
public sealed class SaveMemoryTool : AIFunction
{
    public const string ToolName = "save_memory";

    private const string ArgumentName = "text";

    private const string HereName = "here";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "text": {
              "type": "string",
              "description": "One short sentence about the user, in the third person: \"Their name is Chris.\", \"They prefer replies in metric units.\""
            }
          },
          "required": ["text"]
        }
        """);

    /// <summary>The schema with <c>here</c> (2026-11-21, feature idea #6): offered only to a tool built with a folder.</summary>
    private static readonly JsonElement HereSchema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "text": {
              "type": "string",
              "description": "One short sentence about the user, in the third person: \"Their name is Chris.\", \"They prefer replies in metric units.\""
            },
            "here": {
              "type": "boolean",
              "description": "True when the fact is about this project or folder only, not about the user (\"This repo's tests run with build.ps1.\"); leave it off for facts about the user."
            }
          },
          "required": ["text"]
        }
        """);

    private readonly MemoryStore _store;

    private readonly Func<string?>? _folder;

    /// <param name="folder">The working folder a <c>here</c> save is for (2026-11-21, feature idea #6); null leaves <c>here</c> out of the schema
    /// (a bot, whose chat isn't about a folder).</param>
    public SaveMemoryTool(MemoryStore store, Func<string?>? folder = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _folder = folder;
    }

    public override string Name => ToolName;

    public override string Description => _folder is null
        ? "Saves one lasting fact about the user to long-term memory, so it is known in every later session. Not for passing details."
        : "Saves one lasting fact to long-term memory, so it is known in every later session: about the user, or with here about this project " +
          "or folder (2026-11-22, the review's #33). Not for passing details.";

    public override JsonElement JsonSchema => _folder is null ? Schema : HereSchema;

    /// <summary>Whether this tool offers <c>here</c> (a folder to save for); the prompt's sentence about it follows (#33).</summary>
    public bool OffersHere => _folder?.Invoke() is not null;

    /// <summary>The tool's answer, pinned: what the model reads and what the transcript shows.</summary>
    /// <param name="here">The folder the save was made in (2026-11-21): a duplicate saved for another folder names that one.</param>
    public static string Describe(MemoryAddResult result, string? here = null) => result.Outcome switch
    {
        MemoryAddOutcome.Added when result.Folder is not null => MemoryText.RememberedHere(result.Text),
        MemoryAddOutcome.Added => "remembered: " + result.Text,
        MemoryAddOutcome.Duplicate when result.WidenedFrom is { } from => MemoryText.Widened(result.Text, from),
        MemoryAddOutcome.Duplicate when result.CoversHere => MemoryText.CoversHere(result.Text),
        MemoryAddOutcome.Duplicate when result.Folder is not null && !Files.FolderMatch.Same(result.Folder, here) => MemoryText.DuplicateElsewhereForModel(result.Text, result.Folder),
        MemoryAddOutcome.Duplicate => "already remembered: " + result.Text,
        MemoryAddOutcome.Disabled => "not saved: the user has disabled this memory (it is kept but not offered to you); do not save it again: " + result.Text,   // 2026-10-13, the code review
        MemoryAddOutcome.Full => result.FileFull ? MemoryText.FullFileForModel : MemoryText.FullFolderForModel,
        MemoryAddOutcome.FolderRefused => MemoryText.FolderRefused(here ?? ""),
        MemoryAddOutcome.Empty => "nothing to remember: the text was empty",
        _ => "could not save the memory (the file could not be written); tell the user",
    };

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? folder = _folder?.Invoke();
        bool scoped = folder is not null && ToolArguments.TryReadBoolean(arguments, HereName, out bool? here, out _) && here == true;   // a malformed here reads as off: the safer scope is everywhere's twin, never a wrong folder
        // A line copied from the list keeps its folder mark (2026-11-22, the review's #35): the mark is the list's, never the fact's.
        string text = ReadText(arguments).TrimEnd();
        if (text.EndsWith(MemoryPrompt.FolderOnlyMark.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^MemoryPrompt.FolderOnlyMark.Trim().Length];
        }

        var result = _store.Remember(text, folder, scoped);
        // The answer's folder is the saved copy's: a fresh save reports where it went, a duplicate where the copy already applies.
        return new ValueTask<object?>(Describe(result.Outcome == MemoryAddOutcome.Added && !scoped ? result with { Folder = null } : result, folder));
    }

    /// <summary>The <c>text</c> argument however it arrived: a <see cref="JsonElement"/> from the wire, or a string from a test.</summary>
    internal static string ReadText(AIFunctionArguments arguments) => ToolArguments.ReadString(arguments, ArgumentName);
}
