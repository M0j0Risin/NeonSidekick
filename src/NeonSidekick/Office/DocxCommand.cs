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
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(lastReply);
        var parsed = PdfCommand.Parse(args);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        if (parsed.Target.Length == 0)
        {
            return OfficeText.DocxUsage;
        }

        if (PdfCommand.IsWebAddress(parsed.Target))
        {
            return OfficeText.NoWebSource;
        }

        DocxRequest request;
        if (string.Equals(parsed.Target, PrintText.ReplyWord, StringComparison.OrdinalIgnoreCase))
        {
            string? reply = lastReply();
            if (string.IsNullOrWhiteSpace(reply))
            {
                return OfficeText.NoReplyForDocx;
            }

            request = new DocxRequest(Markdown: reply, Title: PrintText.ReplyTitle, ByUser: true);
        }
        else
        {
            request = new DocxRequest(Path: parsed.Target, ByUser: true);
        }

        return converter.Convert(request with { To = parsed.To, Overwrite = parsed.Overwrite, Landscape = parsed.Landscape, Paper = parsed.Paper }, cancellationToken);
    }

    /// <summary><see cref="Run"/> off the caller's thread: the screen's spinner keeps turning while a long plan and its pictures are written.</summary>
    public static Task<string> RunAsync(DocxConverter converter, string args, Func<string?> lastReply, CancellationToken cancellationToken) =>
        Task.Run(() => Run(converter, args, lastReply, cancellationToken), cancellationToken);

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
        var items = new List<CompletionItem>();
        if (!head.Contains(PdfText.ToWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PdfText.ToWord, OfficeText.DocxToNote));
        }

        if (!head.Contains(PdfText.PaperWord, StringComparison.OrdinalIgnoreCase))
        {
            items.AddRange(PdfPaper.Names.Select(n => new CompletionItem(head + PdfText.PaperWord + n, n + " paper")));
        }

        if (!head.Contains(" " + PrintText.LandscapeWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PrintText.LandscapeWord, OfficeText.DocxLandscapeNote));
        }

        if (!head.Contains(" " + PdfText.OverwriteWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PdfText.OverwriteWord, OfficeText.DocxOverwriteNote));
        }

        return MentionCompleter.Matches(items, argText);
    }
}
