using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /sys persona|operata|vocalia (2026-10-09) ─────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary><c>/sys persona</c>: the personality, <c>persona.md</c>. Pinned.</summary>
    public const string SysPersonaWord = "persona";

    /// <summary><c>/sys operata</c>: the operating rules, <c>operata.md</c>. Pinned.</summary>
    public const string SysOperataWord = "operata";

    /// <summary><c>/sys vocalia</c>: the spoken-reply directive, <c>vocalia.md</c>. Pinned.</summary>
    public const string SysVocaliaWord = "vocalia";

    /// <summary>
    /// The words after <c>/sys</c> and the file each edits (2026-10-09, the user's ask: <c>/persona</c>, <c>/operata</c> and
    /// <c>/vocalia</c> folded under the command that shows the prompt they make), in the prompt's order. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<(string Word, string File)> SysFileWords =
    [
        (SysPersonaWord, PersonaFile.FileName),
        (SysOperataWord, OperataFile.FileName),
        (SysVocaliaWord, VocaliaFile.FileName),
    ];

    /// <summary>The typed command a file's lines name: <c>/sys persona</c>. Pinned.</summary>
    public static string SysFileCommand(string word) => SlashCommands.SysWord + " " + word;

    /// <summary><c>/sys</c>'s completion note beside a file word: <c>edit persona.md</c>. Pinned.</summary>
    public static string SysFileNote(string fileName) => "edit " + fileName;

    /// <summary><c>/sys</c> given a first word that is none of the three files. Pinned.</summary>
    public static readonly string SysUsageError = "/sys takes " + string.Join(", ", SysFileWords.Select(f => f.Word)) + ", or nothing for the system prompt.";

    /// <summary>
    /// <c>/sys persona|operata|vocalia [reset]</c> (2026-10-09; <c>copy &lt;profile&gt; [force]</c> too until 2026-10-10): the file's word, then
    /// <see cref="HandlePromptFileAsync"/> with the rest, as <c>/persona</c> and its siblings ran it from 2026-09-16; a first word
    /// that is no file is <see cref="SysUsageError"/>. The lines go through the flow sink, so the same call serves a pane under a reply.
    /// </summary>
    private async Task HandleSysFileAsync(string args, CancellationToken cancellationToken)
    {
        string trimmed = args.Trim();
        int space = trimmed.IndexOfAny([' ', '\t']);
        string word = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        string rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        switch (word)
        {
            case SysPersonaWord:
                await HandlePromptFileAsync(_persona, SysFileCommand(SysPersonaWord), rest, PersonaCreatedNotice, PersonaOpenedNotice, PersonaOpenFailedError, spoken: false, cancellationToken).ConfigureAwait(false);
                break;
            case SysOperataWord:
                await HandlePromptFileAsync(_operata, SysFileCommand(SysOperataWord), rest, OperataCreatedNotice, OperataOpenedNotice, OperataOpenFailedError, spoken: false, cancellationToken, CurrentOperatingRules).ConfigureAwait(false);
                break;
            case SysVocaliaWord:
                await HandlePromptFileAsync(_vocalia, SysFileCommand(SysVocaliaWord), rest, VocaliaCreatedNotice, VocaliaOpenedNotice, VocaliaOpenFailedError, spoken: true, cancellationToken).ConfigureAwait(false);
                break;
            default:
                _flow.Error(SysUsageError);
                break;
        }
    }

    /// <summary>
    /// A prompt file's argument list after its word (2026-09-21, <c>/persona</c>'s; <c>/sys persona</c>'s since 2026-10-09): reset (copy, its
    /// profiles and the force word too until 2026-10-10, when the copy became <c>/profile push|pull persona</c>).
    /// </summary>
    private static IReadOnlyList<CompletionItem> PromptFileChoices(string fileName, string argText) =>
        MentionCompleter.Matches([new(ResetWord, PromptFileResetNote(fileName))], argText);
}
