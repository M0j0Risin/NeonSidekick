using Microsoft.Extensions.AI;
using NeonSidekick.CustomCommands;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>What a <c>/commands</c> line asks for (<see cref="CustomCommandsCommand.Parse"/>).</summary>
public enum CustomCommandsVerb
{
    /// <summary>The line is wrong: <see cref="CustomCommandsLine.Error"/> says why.</summary>
    Unknown,

    /// <summary>The pane (its lines without one).</summary>
    Pane,

    /// <summary>A command started from the template, opened in the editor.</summary>
    New,

    /// <summary>The last message sent saved as a command.</summary>
    Save,
}

/// <summary>A parsed <c>/commands</c> line: the verb, the name and the scope, or the error to show.</summary>
public sealed record CustomCommandsLine(CustomCommandsVerb Verb, string Name = "", CustomCommandScope Scope = CustomCommandScope.Profile, string? Error = null);

/// <summary>
/// <c>/commands</c>' words (2026-10-30, the custom commands plan; the <see cref="PromptsCommand"/> shape), pure: bare, the pane;
/// <c>new &lt;name&gt; [--global]</c>; <c>save &lt;name&gt; [--global]</c>. The scope switch is <c>--global</c>, as <c>/skills add</c>'s, so a command
/// may be called <c>global</c>. A name must already be a valid one: a bad one is refused with the name it could be, never fixed silently.
/// </summary>
public static class CustomCommandsCommand
{
    /// <summary>The words in the order the completion offers them, with their notes.</summary>
    public static readonly IReadOnlyList<(string Word, string Note)> Words =
    [
        (CustomCommandText.NewWord, "start a command from the template in your editor: /commands new <name> [--global]"),
        (CustomCommandText.SaveWord, "save your last message as a command: /commands save <name> [--global]"),
    ];

    public static CustomCommandsLine Parse(string? args)
    {
        var words = (args ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return new CustomCommandsLine(CustomCommandsVerb.Pane);
        }

        var verb = words[0].ToLowerInvariant() switch
        {
            CustomCommandText.NewWord => CustomCommandsVerb.New,
            CustomCommandText.SaveWord => CustomCommandsVerb.Save,
            _ => CustomCommandsVerb.Unknown,
        };
        if (verb == CustomCommandsVerb.Unknown)
        {
            return Error(CustomCommandText.Usage);
        }

        var rest = words.Skip(1).ToList();
        var scope = CustomCommandScope.Profile;
        if (rest.RemoveAll(w => w.Equals(CustomCommandText.GlobalSwitch, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            scope = CustomCommandScope.Global;
        }

        if (rest.Count != 1)
        {
            return Error(CustomCommandText.Usage);
        }

        string typed = rest[0].TrimStart('/');
        if (!CustomCommandName.IsValid(typed))
        {
            string suggestion = CustomCommandName.Normalize(typed);
            return Error(CustomCommandText.BadNameError(rest[0], CustomCommandName.IsValid(suggestion) ? suggestion : "", words[0].ToLowerInvariant()));
        }

        return new CustomCommandsLine(verb, typed, scope);
    }

    /// <summary>Whether the line is the pane on the watcher under a reply (the bare word).</summary>
    public static bool IsPane(string? args) => Parse(args).Verb == CustomCommandsVerb.Pane;

    /// <summary>The input line's argument list after <c>/commands </c>: <c>new</c> and <c>save</c> as the first word, then <c>--global</c> after a name.</summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        if (!argText.Contains(' ', StringComparison.Ordinal))
        {
            return MentionCompleter.Matches(Words.Select(w => new CompletionItem(w.Word, w.Note)).ToList(), argText);
        }

        var words = argText.Split(' ');
        if (words.Length == 3 && words[0].ToLowerInvariant() is CustomCommandText.NewWord or CustomCommandText.SaveWord && words[1].Length > 0)
        {
            string head = words[0] + " " + words[1] + " ";
            return MentionCompleter.Matches([new CompletionItem(head + CustomCommandText.GlobalSwitch, "in the home's commands folder, every profile's")], argText);
        }

        return [];
    }

    /// <summary>
    /// The text <c>/commands save</c> takes (2026-10-30, the custom commands plan; the screen and headless share it): the last turn
    /// <c>/rewind</c> could pick (<see cref="ConversationRewind.Turns"/>: no opener, no compact summary), its words as
    /// <see cref="ConversationRewind.TypedText"/> reads them (no progress note, <c>/undo</c> note or restored PDF), the picture and PDF labels
    /// taken out (<see cref="PasteBlocks.WithoutAttachmentLabels"/>; <c>PicturesDropped</c> also when the turn carried one with no label).
    /// A <c>/claude</c> or <c>/cmd</c> turn is no message: its error. None at all: <see cref="CustomCommandText.NothingToSaveError"/>. Pure.
    /// </summary>
    public static (string? Text, bool PicturesDropped, string? Error) LastSentMessage(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var turns = ConversationRewind.Turns(messages);
        if (turns.Count == 0)
        {
            return (null, false, CustomCommandText.NothingToSaveError);
        }

        var message = messages[turns[^1].Start];
        string typed = ConversationRewind.TypedText(message);
        if (Claude.ClaudeText.HistoryUserPrompt(typed) is not null)
        {
            return (null, false, CustomCommandText.NotAMessageError("/claude"));
        }

        if (Shell.ShellText.CmdHistoryCommand(typed) is not null)
        {
            return (null, false, CustomCommandText.NotAMessageError("/cmd"));
        }

        string text = PasteBlocks.WithoutAttachmentLabels(typed, out bool dropped);
        return (text, dropped || message.Contents.OfType<DataContent>().Any(), null);
    }

    /// <summary>
    /// What a custom command sends for <paramref name="args"/> (the screen, <c>/loop</c> and headless share it): the body expanded
    /// (<see cref="CustomCommandTemplate.Expand"/>), then each picture or PDF label typed in the arguments that the expansion dropped (a body
    /// using only <c>$0</c>) on a line of its own after it, so the picture's file is still named in the sent text. A blank expansion is
    /// <see cref="CustomCommandText.EmptyPromptError"/>. The result is a message, never parsed again. Pure.
    /// </summary>
    public static (string? Prompt, string? Error) Prompt(CustomCommand command, string args)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(args);
        string text = CustomCommandTemplate.Expand(command.Body, args, command.Arguments).Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, CustomCommandText.EmptyPromptError(command.Word, command.ArgumentHint));
        }

        var dropped = PasteBlocks.AttachmentLabels(args).Where(label => !text.Contains(label, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (dropped.Count > 0)
        {
            text = text.TrimEnd() + "\n\n" + string.Join('\n', dropped);
        }

        return (text, null);
    }

    /// <summary>
    /// The custom command <c>/help &lt;words&gt;</c> names (2026-10-30): one word, with or without its <c>/</c>, that no built-in has and the
    /// scan runs; null otherwise (several words, a built-in, none such), which goes to the manual's lookup as before. Pure.
    /// </summary>
    public static CustomCommand? HelpCard(CustomCommandScan scan, string args)
    {
        ArgumentNullException.ThrowIfNull(scan);
        string word = (args ?? "").Trim();
        if (word.Length == 0 || word.Any(char.IsWhiteSpace))
        {
            return null;
        }

        string slashed = word.StartsWith('/') ? word : "/" + word;
        return SlashCommands.Parse(slashed).Command == SlashCommand.Unknown ? scan.Find(slashed) : null;
    }

    /// <summary>
    /// <c>/commands new &lt;name&gt; [--global]</c>'s write (the screen and headless share it): the template written, and the notice naming the
    /// file, with the profile-over-global note; or the refusal. <c>Path</c> is the file written, null on a refusal.
    /// </summary>
    public static (string Answer, string? Path) Create(CustomCommandRoots roots, CustomCommandsLine line)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(line);
        var result = CustomCommandEditor.Create(roots, line.Scope, line.Name);
        return result.Outcome == CustomCommandEditOutcome.Created
            ? (CustomCommandText.CreatedNotice(line.Name, result.Path) + CustomCommandsMenu.OverrideNote(roots, line.Scope, line.Name), result.Path)
            : (CustomCommandsMenu.Refusal(result), null);
    }

    /// <summary>
    /// <c>/commands save &lt;name&gt; [--global]</c>'s write: <see cref="LastSentMessage"/> of <paramref name="messages"/> saved as the
    /// command's body (<see cref="CustomCommandEditor.Save"/>), the notice quoting its first line and naming the file, with the override,
    /// <c>\$</c> and pictures notes that apply; or the refusal (<c>Failed</c>).
    /// </summary>
    public static (string Answer, bool Failed) Save(CustomCommandRoots roots, CustomCommandsLine line, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(line);
        var (text, dropped, error) = LastSentMessage(messages);
        if (error is not null || text is null)
        {
            return (error ?? CustomCommandText.NothingToSaveError, true);
        }

        var result = CustomCommandEditor.Save(roots, line.Scope, line.Name, text);
        if (result.Outcome != CustomCommandEditOutcome.Saved)
        {
            return (CustomCommandsMenu.Refusal(result), true);
        }

        string answer = CustomCommandText.SavedNotice(line.Name, result.Path, CustomCommandText.FirstLine(text))
            + CustomCommandsMenu.OverrideNote(roots, line.Scope, line.Name)
            + (result.MayReadDifferently ? " " + CustomCommandText.SavedMayReadDifferently : "")
            + (dropped ? " " + CustomCommandText.PicturesNotKept : "");
        return (answer, false);
    }

    private static CustomCommandsLine Error(string error) => new(CustomCommandsVerb.Unknown, Error: error);
}
