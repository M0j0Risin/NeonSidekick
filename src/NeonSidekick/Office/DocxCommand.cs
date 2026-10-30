using NeonSidekick.Pdf;
using NeonSidekick.Printing;
using NeonSidekick.UI;

namespace NeonSidekick.Office;

/// <summary>
/// <c>/docx &lt;file.md|file.txt&gt; | reply [to=&lt;out.docx&gt;] [paper=letter|a4|legal] [landscape] [overwrite]</c> (2026-10-29, the Office
/// writing plan): <c>convert_to_docx</c> by the user's own hand, through the same <see cref="DocxConverter"/>, for the screen and headless.
/// <c>/docx .neon/plans/x.md</c> makes a Word copy of a plan. The line is taken apart by <see cref="PdfCommand.Parse"/> (a bare
/// <c>overwrite</c> or <c>landscape</c> counts only at the line's end, review S10), never by its edit parser, so <c>/docx merge</c> is a
/// file called <c>merge</c>. A web address is refused with what <c>/docx</c> takes.
/// </summary>
public static class DocxCommand
{
    /// <summary>Runs one <c>/docx</c> line; <paramref name="lastReply"/> is the last reply's text for <c>/docx reply</c>.</summary>
    public static string Run(DocxConverter converter, string args, Func<string?> lastReply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(converter);
        var request = Prepare(args, lastReply, out string? answer);
        return request is null ? answer! : converter.Convert(request, cancellationToken);
    }

    /// <summary>
    /// <see cref="Run"/> with only the conversion off the caller's thread, so the screen's spinner keeps turning while a long plan and its
    /// pictures are written. The line is parsed and <paramref name="lastReply"/> read here, on the caller's thread (2026-10-30, review F17: both
    /// ran on the pool, reading the screen's transcript and headless's history from a thread that doesn't own them).
    /// </summary>
    public static Task<string> RunAsync(DocxConverter converter, string args, Func<string?> lastReply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(converter);
        var request = Prepare(args, lastReply, out string? answer);
        return request is null ? Task.FromResult(answer!) : Task.Run(() => converter.Convert(request, cancellationToken), cancellationToken);
    }

    /// <summary>The request one <c>/docx</c> line asks for, or null with the answer that ends it here (usage, a refusal, no reply).</summary>
    private static DocxRequest? Prepare(string args, Func<string?> lastReply, out string? answer)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(lastReply);
        var parsed = PdfCommand.Parse(args);
        answer = parsed.Error
            ?? (parsed.Target.Length == 0 ? OfficeText.DocxUsage : null)
            ?? (PdfCommand.IsWebAddress(parsed.Target) ? OfficeText.NoWebSource : null);
        if (answer is not null)
        {
            return null;
        }

        DocxRequest request;
        if (string.Equals(parsed.Target, PrintText.ReplyWord, StringComparison.OrdinalIgnoreCase))
        {
            string? reply = lastReply();
            if (string.IsNullOrWhiteSpace(reply))
            {
                answer = OfficeText.NoReplyForDocx;
                return null;
            }

            request = new DocxRequest(Markdown: reply, ByUser: true, FromReply: true);
        }
        else
        {
            request = new DocxRequest(Path: parsed.Target, ByUser: true);
        }

        return request with { To = parsed.To, Overwrite = parsed.Overwrite, Landscape = parsed.Landscape, Paper = parsed.Paper };
    }

    /// <summary>
    /// <c>/docx</c>'s word list: <c>reply</c> for the first word, then — once a target and a space are typed — <c>to=</c>, <c>paper=</c>,
    /// <c>landscape</c> and <c>overwrite</c> not given yet; never <c>/pdf</c>'s edit verbs. The path itself is the path list's. Pure.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int cut = argText.LastIndexOf(' ');
        if (cut < 0)
        {
            return MentionCompleter.Matches([new(PrintText.ReplyWord, OfficeText.DocxReplyNote)], argText);
        }

        string head = argText[..(cut + 1)];
        return MentionCompleter.Matches(PdfCommand.OptionItems(head, layout: true, OfficeText.DocxToNote, OfficeText.DocxLandscapeNote, OfficeText.DocxOverwriteNote), argText);
    }
}
