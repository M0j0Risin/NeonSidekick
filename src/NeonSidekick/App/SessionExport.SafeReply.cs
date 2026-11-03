using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using NeonSidekick.Files;

namespace NeonSidekick.App;

public static partial class SessionExport
{
    /// <summary>
    /// The pipeline <see cref="SafeReply"/> reads a reply with: raw HTML left on (the app's other two pipelines turn it off, so they never
    /// see an <see cref="HtmlBlock"/> or <see cref="HtmlInline"/>), pipe tables (a link in a cell is found), bare addresses as links, and each
    /// node's span mapped back to the source.
    /// </summary>
    private static readonly MarkdownPipeline SafeReplyPipeline = new MarkdownPipelineBuilder().UsePipeTables().UseAutoLinks().UsePreciseSourceLocation().Build();

    /// <summary>
    /// A reply made safe for the Markdown note (2026-11-02, review P5-1/P12-1): what an injected or misbehaving model wrote can't go live
    /// when the note opens in Obsidian, VS Code or GitHub, as the screen, the Web UI and the HTML export already refuse it. The reply's own
    /// Markdown is kept and edited in place, from the last span back so earlier offsets stay right:
    /// a last fenced block left open is closed (it swallowed every later turn); raw HTML is escaped (<c>&lt;img&gt;</c>, <c>&lt;iframe&gt;</c>); an
    /// absolute picture that isn't <c>data:</c> becomes its alt text (a remote picture is a tracking pixel); a link or bare address whose
    /// absolute target <see cref="SafeText.WebLink"/> refuses (<c>obsidian:</c>, <c>file:</c>) becomes its text; and <c>%%</c>, Obsidian's
    /// comment, is escaped. Code (fenced, indented or inline) keeps what it says, and a relative link stays. Line endings are <c>\n</c>, the
    /// end trimmed. Pure.
    /// </summary>
    public static string SafeReply(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        string text = reply.ReplaceLineEndings("\n").TrimEnd();
        var document = Markdig.Markdown.Parse(text, SafeReplyPipeline);
        var spans = new List<(int Start, int End, string? With)>();   // End exclusive; With null keeps the span verbatim (no %% escape)
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case CodeBlock code:   // a FencedCodeBlock too
                    spans.Add((code.Span.Start, CodeEnd(code) + 1, null));
                    break;
                case CodeInline inline:
                    spans.Add((inline.Span.Start, inline.Span.End + 1, null));
                    break;
                case HtmlBlock html:
                    spans.Add((html.Span.Start, html.Span.End + 1, EscapedHtml(Slice(text, html.Span))));
                    break;
                case HtmlInline html:
                    spans.Add((html.Span.Start, html.Span.End + 1, EscapedHtml(Slice(text, html.Span))));
                    break;
                case AutolinkInline auto:
                    spans.Add((auto.Span.Start, auto.Span.End + 1, Kept(auto.Url, auto.IsEmail) ? null : SafeCode(auto.Url)));
                    break;
                case LinkInline link:
                    // A kept link is copied whole (its target's %% untouched) unless something inside it must change (a remote picture).
                    string? instead = LinkInstead(link);
                    if (instead is not null || !NeedsChange(link))
                    {
                        spans.Add((link.Span.Start, link.Span.End + 1, instead));
                    }

                    break;
            }
        }

        var builder = new StringBuilder(text.Length + 16);
        int at = 0;
        foreach (var (start, end, with) in spans.Where(s => s.Start >= 0 && s.End > s.Start).OrderBy(s => s.Start).ThenByDescending(s => s.End))
        {
            if (start < at)
            {
                continue;   // inside a span already taken (a picture in a link that's replaced whole)
            }

            builder.Append(Percents(text[at..start]));
            builder.Append(with ?? text[start..Math.Min(end, text.Length)]);
            at = Math.Min(end, text.Length);
        }

        builder.Append(Percents(text[at..]));
        if (OpenFence(document, text) is { } fence)
        {
            builder.Append('\n').Append(fence);
        }

        return builder.ToString();
    }

    /// <summary>A link's text in its place, or null to keep it: a picture's alt text unless it's <c>data:</c> or relative; a link's text unless its target passes.</summary>
    private static string? LinkInstead(LinkInline link)
    {
        string url = link.Url ?? "";
        if (link.IsImage)
        {
            return !IsAbsolute(url) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? null : MarkdownText(PlainText(link));
        }

        if (Kept(url, email: false))
        {
            return null;
        }

        return link.IsAutoLink ? SafeCode(url) : MarkdownText(PlainText(link));
    }

    /// <summary>Whether a target stays: a relative one, or an absolute one <see cref="SafeText.WebLink"/> passes (a bare email address is mailto).</summary>
    private static bool Kept(string url, bool email) =>
        email ? SafeText.WebLink("mailto:" + url) is not null : !IsAbsolute(url) || SafeText.WebLink(url) is not null;

    /// <summary>An absolute target, a protocol-relative <c>//host/…</c> one too; a drive path or a rooted one is relative to the note.</summary>
    private static bool IsAbsolute(string url)
    {
        string trimmed = url.Trim();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || (Uri.TryCreate(trimmed, UriKind.Absolute, out _) && !(trimmed.Length > 1 && trimmed[1] == ':') && !trimmed.StartsWith('/'));
    }

    /// <summary>Whether anything inside a link must change: raw HTML, or a link or picture that's replaced.</summary>
    private static bool NeedsChange(LinkInline link) =>
        link.Descendants<HtmlInline>().Any() || link.Descendants<LinkInline>().Any(inner => LinkInstead(inner) is not null)
        || link.Descendants<AutolinkInline>().Any(auto => !Kept(auto.Url, auto.IsEmail));

    /// <summary>A link's label as plain text, in order: its literal runs and code, a picture inside it its alt text.</summary>
    private static string PlainText(ContainerInline container)
    {
        var sb = new StringBuilder();
        Append(container);
        return sb.ToString();

        void Append(ContainerInline parent)
        {
            for (var inline = parent.FirstChild; inline is not null; inline = inline.NextSibling)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        sb.Append(literal.Content.ToString());
                        break;
                    case CodeInline code:
                        sb.Append(code.Content);
                        break;
                    case LineBreakInline:
                        sb.Append(' ');
                        break;
                    case ContainerInline inner:
                        Append(inner);
                        break;
                }
            }
        }
    }

    /// <summary>An address shown as code, so the note's reader never links it.</summary>
    private static string SafeCode(string url) => CodeSpan(url);

    /// <summary>Raw HTML as text: each <c>&lt;</c> escaped, and its <c>%%</c>.</summary>
    private static string EscapedHtml(string html) => Percents(html).Replace("<", "\\<", StringComparison.Ordinal);

    /// <summary>Obsidian's comment mark escaped in text outside code.</summary>
    private static string Percents(string text) => text.Replace("%%", "\\%\\%", StringComparison.Ordinal);

    /// <summary>
    /// Where a code block ends in the source: its span's end, or its last line's when that's later — Markdig leaves an unclosed fence's
    /// span on its opening line, its lines past it.
    /// </summary>
    private static int CodeEnd(CodeBlock code) =>
        code.Lines.Count == 0 ? code.Span.End : Math.Max(code.Span.End, code.Lines.Lines[code.Lines.Count - 1].Slice.End);

    private static string Slice(string text, Markdig.Syntax.SourceSpan span) => text[span.Start..Math.Min(span.End + 1, text.Length)];

    /// <summary>
    /// The closing fence a last fenced block left open needs, or null: one whose span's last line isn't a fence of its character at least
    /// as long as the opening one (or whose span is its opening line alone).
    /// </summary>
    private static string? OpenFence(MarkdownDocument document, string text)
    {
        var fenced = document.Descendants<FencedCodeBlock>().LastOrDefault();
        if (fenced is null || CodeEnd(fenced) + 1 < text.Length)
        {
            return null;
        }

        string body = text[fenced.Span.Start..Math.Min(CodeEnd(fenced) + 1, text.Length)];
        int lastBreak = body.LastIndexOf('\n');
        string closing = new(fenced.FencedChar, fenced.OpeningFencedCharCount);
        if (lastBreak >= 0)
        {
            string last = body[(lastBreak + 1)..].Trim();
            if (last.Length >= closing.Length && last.All(c => c == fenced.FencedChar))
            {
                return null;
            }
        }

        return closing;
    }
}
