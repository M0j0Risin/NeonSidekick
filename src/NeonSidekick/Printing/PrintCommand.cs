using System.Globalization;
using System.Text;
using NeonSidekick.UI;

namespace NeonSidekick.Printing;

/// <summary>What one <c>/print</c> line came to: the lines to print, and whether it failed (an error's line).</summary>
public sealed record PrintCommandResult(IReadOnlyList<string> Lines, bool Failed)
{
    public static PrintCommandResult Error(string line) => new([line], true);

    public static PrintCommandResult Of(params string[] lines) => new(lines, false);
}

/// <summary>A <c>/print</c> line taken apart: the target (a path, <c>reply</c> or <c>printers</c>) and the options.</summary>
public sealed record PrintArguments(string Target, string? Printer, int Copies, string? Pages, bool Landscape, string? Error);

/// <summary>
/// <c>/print</c> (2026-09-28, the user's ask): the user's own hand, so <see cref="PrintPolicy"/> never judges it. One engine for
/// the screen and headless.
/// <list type="bullet">
/// <item><c>/print</c> — the usage and the printers;</item>
/// <item><c>/print printers</c> — the printers, the Windows default and the setting's marked;</item>
/// <item><c>/print &lt;file&gt; [printer=&lt;name&gt;] [copies=N] [pages=1-3] [landscape]</c> — a file of the working directory;</item>
/// <item><c>/print reply [options]</c> — the last reply, as markdown.</item>
/// </list>
/// A name with spaces is quoted (<c>printer="HP LaserJet"</c>); the path is every word that is not an option, so it needs none.
/// </summary>
public static class PrintCommand
{
    public const string PrinterOption = "printer=";
    public const string CopiesOption = "copies=";
    public const string PagesOption = "pages=";

    /// <summary><paramref name="args"/> taken apart. Pure.</summary>
    public static PrintArguments Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? printer = null;
        string? pages = null;
        int copies = 1;
        bool landscape = false;
        var target = new List<string>();
        foreach (string word in Words(args))
        {
            if (word.StartsWith(PrinterOption, StringComparison.OrdinalIgnoreCase))
            {
                printer = word[PrinterOption.Length..];
            }
            else if (word.StartsWith(CopiesOption, StringComparison.OrdinalIgnoreCase))
            {
                string given = word[CopiesOption.Length..];
                if (!int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out copies) || copies is < 1 or > PrintText.MaxCopies)
                {
                    return new PrintArguments("", null, 1, null, false, PrintText.BadCopies(given));
                }
            }
            else if (word.StartsWith(PagesOption, StringComparison.OrdinalIgnoreCase))
            {
                pages = word[PagesOption.Length..];
            }
            else if (string.Equals(word, PrintText.LandscapeWord, StringComparison.OrdinalIgnoreCase))
            {
                landscape = true;
            }
            else
            {
                target.Add(word);
            }
        }

        return new PrintArguments(string.Join(' ', target), printer, copies, pages, landscape, null);
    }

    /// <summary>The words of <paramref name="text"/>: split on spaces, a double-quoted stretch kept whole and unquoted.</summary>
    public static IEnumerable<string> Words(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var word = new StringBuilder();
        bool quoted = false;
        bool any = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any || word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                    any = false;
                }
            }
            else
            {
                word.Append(c);
            }
        }

        if (any || word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    /// <summary>Runs one <c>/print</c> line; <paramref name="lastReply"/> is the last reply's text for <c>/print reply</c>.</summary>
    public static async Task<PrintCommandResult> RunAsync(PrintService service, string args, Func<string?> lastReply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(lastReply);
        var parsed = Parse(args);
        if (parsed.Error is not null)
        {
            return PrintCommandResult.Error(parsed.Error);
        }

        string setting = service.Effective.PrintDefaultPrinter;
        if (parsed.Target.Length == 0 && parsed.Printer is null && !parsed.Landscape && parsed.Pages is null && parsed.Copies == 1)
        {
            var printers = await Task.Run(service.Printers, cancellationToken).ConfigureAwait(false);
            return new PrintCommandResult([PrintText.Usage, .. Lines(PrintText.PrinterList(printers, setting))], false);
        }

        if (string.Equals(parsed.Target, PrintText.PrintersWord, StringComparison.OrdinalIgnoreCase))
        {
            var printers = await Task.Run(service.Printers, cancellationToken).ConfigureAwait(false);
            return new PrintCommandResult(Lines(PrintText.PrinterList(printers, setting)), false);
        }

        PrintRequest request;
        if (string.Equals(parsed.Target, PrintText.ReplyWord, StringComparison.OrdinalIgnoreCase))
        {
            string? reply = lastReply();
            if (string.IsNullOrWhiteSpace(reply))
            {
                return PrintCommandResult.Error(PrintText.NoReply);
            }

            request = new PrintRequest(null, parsed.Printer, parsed.Copies, parsed.Pages, parsed.Landscape, reply, PrintText.ReplyTitle);
        }
        else if (parsed.Target.Length == 0)
        {
            return PrintCommandResult.Error(PrintText.Usage);
        }
        else
        {
            request = new PrintRequest(parsed.Target, parsed.Printer, parsed.Copies, parsed.Pages, parsed.Landscape);
        }

        string result = await service.RunAsync(request, cancellationToken).ConfigureAwait(false);
        return result.StartsWith("Error:", StringComparison.Ordinal) ? PrintCommandResult.Error(result) : PrintCommandResult.Of(result);
    }

    private static string[] Lines(string text) => text.Split('\n');

    /// <summary>
    /// <c>/print</c>'s word list: <c>reply</c> and <c>printers</c> for the first word, then — once a target and a space are typed —
    /// the options not given yet, a <c>printer="…"</c> per printer. The path itself is the path list's
    /// (<c>ChatScreen.ArgumentPaths</c>). Pure.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText, IReadOnlyList<string> printers)
    {
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(printers);
        int cut = argText.LastIndexOf(' ');
        if (cut < 0)
        {
            return MentionCompleter.Matches([new(PrintText.ReplyWord, PrintText.ReplyNote), new(PrintText.PrintersWord, PrintText.PrintersNote)], argText);
        }

        string head = argText[..(cut + 1)];
        if (string.Equals(head.Trim(), PrintText.PrintersWord, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var items = new List<CompletionItem>();
        if (!head.Contains(PrinterOption, StringComparison.OrdinalIgnoreCase))
        {
            items.AddRange(printers.Select(p => new CompletionItem(head + PrinterOption + (p.Contains(' ', StringComparison.Ordinal) ? "\"" + p + "\"" : p), "print on " + p)));
        }

        if (!head.Contains(" " + PrintText.LandscapeWord, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PrintText.LandscapeWord, "turn the page sideways"));
        }

        if (!head.Contains(CopiesOption, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + CopiesOption, "copies=2 for two, up to " + PrintText.MaxCopies.ToString(CultureInfo.InvariantCulture)));
        }

        if (!head.Contains(PagesOption, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem(head + PagesOption, "pages=1-3, 4- or 1,3"));
        }

        return MentionCompleter.Matches(items, argText);
    }
}
