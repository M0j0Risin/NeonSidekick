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
    /// <c>/vocalia</c> folded under the command that showed the prompt they make, until <c>/usage</c> took that over on 2026-11-07), in the
    /// prompt's order. Pinned.
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

    /// <summary>
    /// <c>/sys</c> given nothing, or a first word that is none of the three files. Pinned. Since 2026-11-07 (the user's ask) a bare <c>/sys</c>
    /// opens no pane, so the error points at <c>/usage prompt</c>, where the system prompt went.
    /// </summary>
    public static readonly string SysUsageError = "/sys takes " + SysPersonaWord + ", " + SysOperataWord + " or " + SysVocaliaWord + "; "
        + SlashCommands.UsageWord + " " + UsageText.PromptWord + " shows the system prompt.";

    /// <summary>
    /// <c>/sys persona|operata|vocalia [reset]</c> (2026-10-09; <c>copy &lt;profile&gt; [force]</c> too until 2026-10-10): the file's word, then
    /// <see cref="HandlePromptFileAsync"/> with the rest, as <c>/persona</c> and its siblings ran it from 2026-09-16; no word (since
    /// 2026-11-07) or a first word that is no file is <see cref="SysUsageError"/>. The lines go through the flow sink, so the same call serves a pane under a reply.
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
                // template [<id>] (2026-11-22), the persona's alone; any other words past reset are its own usage line, naming template too.
                string[] words = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length is 1 or 2 && words[0].Equals(PersonaTemplateWord, StringComparison.OrdinalIgnoreCase))
                {
                    await HandlePersonaTemplateAsync(words.Length == 2 ? words[1] : "", cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (words.Length > 0 && !(words.Length == 1 && words[0].Equals(ResetWord, StringComparison.OrdinalIgnoreCase)))
                {
                    _flow.Error(PersonaUsageError);
                    break;
                }

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
    private static IReadOnlyList<CompletionItem> PromptFileChoices(string fileName, string argText, bool persona = false) =>
        MentionCompleter.Matches(persona
            ? [new(ResetWord, PromptFileResetNote(fileName)), new(PersonaTemplateWord, PersonaTemplateNote)]
            : [new(ResetWord, PromptFileResetNote(fileName))], argText);

    // ── /sys persona template [<id>] (2026-11-22, the advisor templates) ──────

    /// <summary><c>/sys persona template</c>'s word. Pinned.</summary>
    public const string PersonaTemplateWord = "template";

    /// <summary>Its completion note. Pinned.</summary>
    public const string PersonaTemplateNote = "write persona.md from an advisor template";

    /// <summary><c>/sys persona</c>'s usage line, the template form beside reset (the other two files keep <see cref="PromptFileUsageError"/>). Pinned.</summary>
    public static readonly string PersonaUsageError =
        SysFileCommand(SysPersonaWord) + " takes nothing (open " + PersonaFile.FileName + " in your editor), " + ResetWord + ", or " + PersonaTemplateWord + " [<id>].";

    /// <summary>The list's heading. Pinned.</summary>
    public const string PersonaTemplatesHeading = "Advisor templates (/sys persona template <id> writes one to persona.md):";

    /// <summary>An id that is no template. Pinned.</summary>
    public static string UnknownTemplateError(string id, IReadOnlyList<string> ids) =>
        "No advisor template \"" + id + "\"; the templates are " + string.Join(", ", ids) + ".";

    /// <summary>The yes/no before the write: a write, or a replace when the profile has its own persona. Pinned.</summary>
    public static string PersonaTemplatePrompt(string id, bool replacing) => replacing
        ? $"{NoticeGlyphs.PromptFile(PersonaFile.FileName)}Replace {PersonaFile.FileName} with the {id} template?"
        : $"{NoticeGlyphs.PromptFile(PersonaFile.FileName)}Write {PersonaFile.FileName} from the {id} template?";

    /// <summary>The notice after the write. Pinned.</summary>
    public static string PersonaTemplateNotice(string id) =>
        $"({NoticeGlyphs.PromptFile(PersonaFile.FileName)}wrote {PersonaFile.FileName} from the {id} template; the next reply uses it; /sys persona edits it)";

    /// <summary>The write failed. Pinned.</summary>
    public static string PersonaTemplateFailedError(string detail) => $"Could not write {PersonaFile.FileName}: {detail}";

    /// <summary>
    /// <c>/sys persona template [&lt;id&gt;]</c> (2026-11-22, the user's ask): no id lists the templates (<see cref="Advisors.AdvisorText.TemplateLine"/>,
    /// yours marked, the home's problems said); an unknown id is an error naming them; else, after a yes every time (a write, or a replace
    /// when the loaded profile has its own persona), the template's persona becomes the loaded profile's <c>persona.md</c>, its own from then on.
    /// </summary>
    private async Task HandlePersonaTemplateAsync(string id, CancellationToken cancellationToken)
    {
        var scan = Advisors.AdvisorTemplates.All(_settings.StorageDirectory);
        if (id.Length == 0)
        {
            Advisors.AdvisorTemplates.Report(scan);
            DrainDiagnostics();
            _flow.Notice(PersonaTemplatesHeading);
            foreach (var each in scan.Templates)
            {
                _flow.Notice("  " + Advisors.AdvisorText.TemplateLine(each));
            }

            return;
        }

        if (scan.Find(id) is not { } template)
        {
            _flow.Error(UnknownTemplateError(id, scan.Ids));
            return;
        }

        if (!await ConfirmAsync(PersonaTemplatePrompt(template.Id, _persona.Read() is not null), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        try
        {
            _persona.WriteText(template.Persona);
            _flow.Notice(PersonaTemplateNotice(template.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _flow.Error(PersonaTemplateFailedError(ex.Message));
        }
    }

    /// <summary>The templates for completion: each id noted with its role (2026-11-22). Read without reporting a broken file.</summary>
    private IReadOnlyList<CompletionItem> AdvisorTemplateChoices() =>
        Advisors.AdvisorTemplates.All(_settings.StorageDirectory).Templates.Select(t => new CompletionItem(t.Id, t.Role)).ToList();
}
