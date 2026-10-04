using NeonSidekick.Printing;
using NeonSidekick.UI;

namespace NeonSidekick.Pdf;

/// <summary>A <c>/pdf</c> line taken apart: the target (a path, a web address or <c>reply</c>) and the options.</summary>
public sealed record PdfArguments(string Target, string? To, PdfPaperSize? Paper, bool Landscape, bool Overwrite, string? Error);

/// <summary>
/// <c>/pdf</c> (2026-10-03, the user's ask): <c>convert_to_pdf</c> by the user's own hand, through the same
/// <see cref="PdfConverter"/>, for the screen and headless.
/// <list type="bullet">
/// <item><c>/pdf</c> — the usage;</item>
/// <item><c>/pdf &lt;file&gt; [to=&lt;out.pdf&gt;] [paper=letter|a4|legal] [landscape] [overwrite]</c> — a file of the working directory;</item>
/// <item><c>/pdf https://… [to=…] [overwrite]</c> — a web page (only an <c>http://</c> or <c>https://</c> start makes one: <c>notes.md</c> is a file);</item>
/// <item><c>/pdf reply [options]</c> — the last reply, as Markdown.</item>
/// </list>
/// Web tools need not be on — the user asked — but <c>Web browser network mode</c> still judges a web page. A path with spaces
/// needs no quotes (every word that is not an option is the path); a <c>to=</c> with spaces does.
/// </summary>
public static class PdfCommand
{
    public const string ReplyWord = PrintText.ReplyWord;

    /// <summary><paramref name="args"/> taken apart. Pure.</summary>
    public static PdfArguments Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? to = null;
        PdfPaperSize? paper = null;
        bool landscape = false;
        bool overwrite = false;
        var target = new List<string>();
        foreach (string word in PrintCommand.Words(args))
        {
            if (word.StartsWith(PdfText.ToWord, StringComparison.OrdinalIgnoreCase))
            {
                to = word[PdfText.ToWord.Length..];
            }
            else if (word.StartsWith(PdfText.PaperWord, StringComparison.OrdinalIgnoreCase))
            {
                string given = word[PdfText.PaperWord.Length..];
                if (!PdfPaper.TryParse(given, out var parsed))
                {
                    return new PdfArguments("", null, null, false, false, Files.FileText.BadChoice("paper", given, PdfText.PaperChoices));
                }

                paper = parsed;
            }
            else if (string.Equals(word, PrintText.LandscapeWord, StringComparison.OrdinalIgnoreCase))
            {
                landscape = true;
            }
            else if (string.Equals(word, PdfText.OverwriteWord, StringComparison.OrdinalIgnoreCase))
            {
                overwrite = true;
            }
            else
            {
                target.Add(word);
            }
        }

        return new PdfArguments(string.Join(' ', target), to, paper, landscape, overwrite, null);
    }

    /// <summary>Whether a target is a web page: it starts <c>http://</c> or <c>https://</c>.</summary>
    public static bool IsWebAddress(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs one <c>/pdf</c> line; <paramref name="lastReply"/> is the last reply's text for <c>/pdf reply</c>. The result is the
    /// converter's sentence (an <c>Error:</c> one on failure), or the usage.
    /// </summary>
    public static async Task<string> RunAsync(PdfConverter converter, string args, Func<string?> lastReply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(lastReply);
        var parsed = Parse(args);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        if (parsed.Target.Length == 0)
        {
            return PdfText.Usage;
        }

        PdfRequest request;
        if (string.Equals(parsed.Target, ReplyWord, StringComparison.OrdinalIgnoreCase))
        {
            string? reply = lastReply();
            if (string.IsNullOrWhiteSpace(reply))
            {
                return PdfText.NoReply;
            }

            request = new PdfRequest(Markdown: reply, Title: PrintText.ReplyTitle);
        }
        else if (IsWebAddress(parsed.Target))
        {
            if (!Uri.TryCreate(parsed.Target, UriKind.Absolute, out var url) || !Web.WebFetcher.IsHttp(url))
            {
                return Web.WebText.NotHttp(parsed.Target);
            }

            request = new PdfRequest(Url: url);
        }
        else
        {
            request = new PdfRequest(Path: parsed.Target);
        }

        request = request with { To = parsed.To, Overwrite = parsed.Overwrite, Landscape = parsed.Landscape, Paper = parsed.Paper };
        return await converter.ConvertAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/pdf</c>'s word list: <c>reply</c> for the first word, then — once a target and a space are typed — the options not
    /// given yet. The path itself is the path list's (<c>ChatScreen.ArgumentPaths</c>). Pure.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int cut = argText.LastIndexOf(' ');
        if (cut < 0)
        {
            return MentionCompleter.Matches([new(ReplyWord, PdfText.ReplyNote)], argText);
        }

        string head = argText[..(cut + 1)];
        var items = new List<CompletionItem>();
        if (!head.Contains(PdfText.ToWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PdfText.ToWord, "to=out.pdf, or a folder"));
        }

        if (!head.Contains(PdfText.PaperWord, StringComparison.OrdinalIgnoreCase))
        {
            items.AddRange(PdfPaper.Names.Select(n => new CompletionItem(head + PdfText.PaperWord + n, n + " paper")));
        }

        if (!head.Contains(" " + PrintText.LandscapeWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PrintText.LandscapeWord, "turn the page sideways"));
        }

        if (!head.Contains(" " + PdfText.OverwriteWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PdfText.OverwriteWord, "replace a PDF already there"));
        }

        return MentionCompleter.Matches(items, argText);
    }
}
