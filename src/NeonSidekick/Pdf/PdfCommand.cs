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
/// <para>
/// Since 2026-10-14 (the PDF plan's phase 4b, gap 37) it edits PDFs too, through <see cref="PdfEditing"/>, <c>pdf_edit</c>'s door:
/// <list type="bullet">
/// <item><c>/pdf merge a.pdf "b c.pdf" … [to=…] [overwrite]</c> — every word that is not an option is a PDF (quote one with spaces), or <c>paths=a.pdf,b.pdf</c>;</item>
/// <item><c>/pdf extract|delete &lt;file&gt; pages=2-5 [to=…] [overwrite]</c>;</item>
/// <item><c>/pdf rotate &lt;file&gt; angle=90|180|270 [pages=…] [to=…] [overwrite]</c>.</item>
/// </list>
/// The first word is a verb unless a file of that name is in the working directory (<c>/pdf merge</c> makes a PDF of a file called
/// <c>merge</c> when there is one). A form is the model's to fill (<c>fill_form</c>): its values need the field names read first.
/// </para>
/// </summary>
public static class PdfCommand
{
    public const string ReplyWord = PrintText.ReplyWord;

    /// <summary>The edit verbs, in the order the help and the completion list them.</summary>
    public static readonly IReadOnlyList<string> EditVerbs = [PdfEditPlan.MergeWord, PdfEditPlan.ExtractWord, PdfEditPlan.DeleteWord, PdfEditPlan.RotateWord];

    public const string PagesWord = "pages=";

    public const string AngleWord = "angle=";

    public const string PathsWord = "paths=";

    /// <summary>Whether <paramref name="word"/> is <c>overwrite</c> or <c>landscape</c>, an option with no <c>=</c>.</summary>
    private static bool IsBareFlag(string word) =>
        string.Equals(word, PdfText.OverwriteWord, StringComparison.OrdinalIgnoreCase) || string.Equals(word, PrintText.LandscapeWord, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where the options at the line's end begin (2026-10-22, review S10: <c>/pdf Q3 overwrite plan.md</c> took "overwrite" from the middle
    /// of the name, turned it on and made "Q3 plan.md"): a bare option word counts only when every word after it is an option too.
    /// </summary>
    private static int FlagsFrom(IReadOnlyList<string> words)
    {
        int at = words.Count;
        while (at > 0 && (IsBareFlag(words[at - 1]) || words[at - 1].Contains('=', StringComparison.Ordinal)))
        {
            at--;
        }

        return at;
    }

    /// <summary>Whether <paramref name="word"/> is an edit verb.</summary>
    public static bool IsEditVerb(string word) => EditVerbs.Any(v => string.Equals(v, word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The edit a line asks for (2026-10-14, gap 37): null with no error when its first word is no verb, or names a file there
    /// (<paramref name="isFile"/>); null with the refusal for a line that is an edit but a wrong one. Pure over <paramref name="isFile"/>.
    /// </summary>
    public static (PdfEditRequest? Request, string? Error) ParseEdit(string args, Func<string, bool> isFile)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(isFile);
        var words = PrintCommand.Words(args).ToList();
        if (words.Count == 0 || !IsEditVerb(words[0]) || isFile(words[0]) || !PdfEditPlan.TryAction(words[0], out var op))
        {
            return (null, null);
        }

        string? to = null;
        string? pages = null;
        int angle = 0;
        bool overwrite = false;
        var paths = new List<string>();
        var target = new List<string>();
        var rest = words.Skip(1).ToList();
        int flagsFrom = FlagsFrom(rest);
        for (int i = 0; i < rest.Count; i++)
        {
            string word = rest[i];
            if (i < flagsFrom && IsBareFlag(word))
            {
                target.Add(word);   // a word of the path, not an option (review S10)
                continue;
            }

            if (word.StartsWith(PdfText.ToWord, StringComparison.OrdinalIgnoreCase))
            {
                to = word[PdfText.ToWord.Length..];
            }
            else if (word.StartsWith(PagesWord, StringComparison.OrdinalIgnoreCase))
            {
                pages = word[PagesWord.Length..];
            }
            else if (word.StartsWith(AngleWord, StringComparison.OrdinalIgnoreCase))
            {
                string given = word[AngleWord.Length..];
                if (!int.TryParse(given, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out angle))
                {
                    return (null, PdfText.BadAngleText(given));
                }
            }
            else if (word.StartsWith(PathsWord, StringComparison.OrdinalIgnoreCase))
            {
                paths.AddRange(word[PathsWord.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if (string.Equals(word, PdfText.OverwriteWord, StringComparison.OrdinalIgnoreCase))
            {
                overwrite = true;
            }
            else if (word.Contains('=', StringComparison.Ordinal) || string.Equals(word, PrintText.LandscapeWord, StringComparison.OrdinalIgnoreCase))
            {
                return (null, PdfText.NotAnEditOption(word, words[0].ToLowerInvariant()));
            }
            else
            {
                target.Add(word);
            }
        }

        // A merge's words are each a PDF; any other edit's are one path, spaces and all, as /pdf's own target is.
        if (op == Web.PdfEditOp.Merge)
        {
            paths.InsertRange(0, target);
        }
        else if (target.Count > 0)
        {
            paths.Insert(0, string.Join(' ', target));
        }

        return (new PdfEditRequest(op, paths, pages, angle, null, false, to, overwrite, ByUser: true), null);
    }

    /// <summary><paramref name="args"/> taken apart. Pure.</summary>
    public static PdfArguments Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? to = null;
        PdfPaperSize? paper = null;
        bool landscape = false;
        bool overwrite = false;
        var target = new List<string>();
        var all = PrintCommand.Words(args).ToList();
        int flagsFrom = FlagsFrom(all);
        for (int i = 0; i < all.Count; i++)
        {
            string word = all[i];
            if (i < flagsFrom && IsBareFlag(word))
            {
                target.Add(word);   // review S10
                continue;
            }

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
    /// converter's sentence (an <c>Error:</c> one on failure), or the usage. With <paramref name="editing"/> (2026-10-14) a line whose first
    /// word is an edit verb is <see cref="PdfEditing"/>'s; without it, as before, the verb is a file's name.
    /// </summary>
    public static async Task<string> RunAsync(PdfConverter converter, string args, Func<string?> lastReply, CancellationToken cancellationToken, PdfEditing? editing = null)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(lastReply);
        if (editing is not null)
        {
            var (edit, editError) = ParseEdit(args, editing.IsFile);
            if (editError is not null)
            {
                return editError;
            }

            if (edit is not null)
            {
                return await editing.EditAsync(edit, cancellationToken).ConfigureAwait(false);
            }
        }

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

            request = new PdfRequest(Markdown: reply, Title: PrintText.ReplyTitle, ByUser: true);
        }
        else if (IsWebAddress(parsed.Target))
        {
            if (!Uri.TryCreate(parsed.Target, UriKind.Absolute, out var url) || !Web.WebFetcher.IsHttp(url))
            {
                return Web.WebText.NotHttp(parsed.Target);
            }

            request = new PdfRequest(Url: url, ByUser: true);
        }
        else
        {
            request = new PdfRequest(Path: parsed.Target, ByUser: true);
        }

        request = request with { To = parsed.To, Overwrite = parsed.Overwrite, Landscape = parsed.Landscape, Paper = parsed.Paper };
        return await converter.ConvertAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/pdf</c>'s word list: <c>reply</c> for the first word, then — once a target and a space are typed — the options not
    /// given yet. The path itself is the path list's (<c>ChatScreen.ArgumentPaths</c>). A web page's address (2026-10-27, the completion
    /// survey) is offered neither <c>paper=</c> nor <c>landscape</c>, which that form refuses: a web page keeps its own page layout. Pure.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int cut = argText.LastIndexOf(' ');
        if (cut < 0)
        {
            return MentionCompleter.Matches([new(ReplyWord, PdfText.ReplyNote), .. EditVerbs.Select(v => new CompletionItem(v, PdfText.VerbNote(v)))], argText);
        }

        string head = argText[..(cut + 1)];
        string first = head.Split(' ', 2)[0];
        if (IsEditVerb(first))
        {
            return MentionCompleter.Matches(EditOptions(head, first.ToLowerInvariant()), argText);
        }

        bool web = IsWebAddress(first);   // the parse's own test (2026-10-27, the review)
        return MentionCompleter.Matches(OptionItems(head, layout: !web, PdfText.ToNote, PdfText.LandscapeNote, PdfText.OverwriteNote), argText);
    }

    /// <summary>
    /// A converter's options not given yet after <paramref name="head"/>: <c>to=</c>, then (with <paramref name="layout"/>) the papers and
    /// <c>landscape</c>, then <c>overwrite</c>, each with its note. <c>/pdf</c>'s and <c>/docx</c>'s one list (2026-10-29, review P1-19: two
    /// copies drifted).
    /// </summary>
    public static List<CompletionItem> OptionItems(string head, bool layout, string toNote, string landscapeNote, string overwriteNote)
    {
        ArgumentNullException.ThrowIfNull(head);
        var items = new List<CompletionItem>();
        if (!head.Contains(PdfText.ToWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PdfText.ToWord, toNote));
        }

        if (layout && !head.Contains(PdfText.PaperWord, StringComparison.OrdinalIgnoreCase))
        {
            items.AddRange(PdfPaper.Names.Select(n => new CompletionItem(head + PdfText.PaperWord + n, PdfText.PaperNote(n))));
        }

        if (layout && !head.Contains(" " + PrintText.LandscapeWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PrintText.LandscapeWord, landscapeNote));
        }

        if (!head.Contains(" " + PdfText.OverwriteWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PdfText.OverwriteWord, overwriteNote));
        }

        return items;
    }

    // An edit's options not given yet (2026-10-14): pages= for all but a merge, the turns for rotate, then to= and overwrite.
    private static List<CompletionItem> EditOptions(string head, string verb)
    {
        var items = new List<CompletionItem>();
        if (verb != PdfEditPlan.MergeWord && !head.Contains(PagesWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PagesWord, verb == PdfEditPlan.RotateWord ? "pages=2-5, every page when left out" : "pages=2-5, \"5-\" to the end, or 1,3,7-9"));
        }

        if (verb == PdfEditPlan.RotateWord && !head.Contains(AngleWord, StringComparison.OrdinalIgnoreCase))
        {
            items.AddRange(PdfEditPlan.Angles.Select(a => new CompletionItem(head + AngleWord + a.ToString(System.Globalization.CultureInfo.InvariantCulture), "turn " + a.ToString(System.Globalization.CultureInfo.InvariantCulture) + "° clockwise")));
        }

        items.AddRange(OptionItems(head, layout: false, PdfText.ToNote, PdfText.LandscapeNote, PdfText.OverwriteNote));
        return items;
    }
}
