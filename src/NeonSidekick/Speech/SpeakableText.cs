using System.Text;
using System.Text.RegularExpressions;

namespace NeonSidekick.Speech;

/// <summary>
/// The text sanitiser between a model's prose and the synthesizer. Every pattern is a
/// <c>[GeneratedRegex]</c> so nothing is compiled or interpreted at run time under NativeAOT.
///
/// <para>Two layers. <see cref="StripMarkdown"/> removes what is speakable but should not be
/// spoken (measured on live runs: <em>"asterisk asterisk Container management asterisk
/// asterisk"</em>, a table read as <em>"pipe File pipe What it is pipe"</em>).
/// <see cref="MakeSpeakable"/> then removes what a phonemiser rejects outright — an emoji in
/// "Hey! 👋 What can I help you with today?" threw <c>"String contains invalid Unicode code
/// points"</c> and took the whole turn down — and folds the typographic quotes that make
/// <em>"don't"</em> come out as <em>"Don T"</em>.</para>
/// </summary>
public static partial class SpeakableText
{
    /// <summary>
    /// Removes markdown syntax, which models emit constantly and which is meaningless aloud.
    ///
    /// <para>Deliberately conservative. Over-stripping mangles ordinary prose, which is a subtler
    /// failure than reading punctuation aloud: emphasis markers are only removed when they wrap
    /// non-space content, so "2 * 3" and "snake_case" survive intact.</para>
    /// </summary>
    public static string StripMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var s = text;

        // A stray fence line: dropped. Whole blocks never get this far since 2026-09-25 — CodeBlockFilter
        // takes them out before the text is cut into sentences (the user's call: code is never read aloud);
        // this still catches a fence line in a sentence handed over directly.
        s = CodeFence().Replace(s, string.Empty);

        // Whole tables never get this far since 2026-09-26 — TableFilter takes them out upstream, as CodeBlockFilter does
        // code. These two still catch a stray row: a pipe-led line with no delimiter row, or a sentence handed over directly.
        // Table separator rows (|---|:---:|) carry no words at all.
        s = TableSeparator().Replace(s, string.Empty);

        // Remaining table rows: pipes become list separators so the cells are read as a list
        // rather than run together into one noun phrase.
        s = TableRow().Replace(s, static m => m.Groups[1].Value.Replace("|", ", "));

        // Headings: the text is the content, the hashes are formatting.
        s = Heading().Replace(s, string.Empty);

        // Blockquote markers.
        s = Blockquote().Replace(s, string.Empty);

        // Bullet markers at the start of a line. Numbered lists are left alone — "1." reads
        // naturally and carries meaning a bullet does not.
        s = Bullet().Replace(s, string.Empty);

        // Links and images: keep the label, drop the target. A URL read aloud is unusable.
        s = Link().Replace(s, "$1");

        // Inline code: the backticks are formatting, the identifier is the content.
        s = InlineCode().Replace(s, "$1");

        // Emphasis, longest marker first so ** is not left as a stray * by the single-char pass.
        // Each requires non-space at both edges, which is what keeps "2 * 3" and "a_b" intact.
        s = BoldStars().Replace(s, "$1");
        s = BoldUnderscores().Replace(s, "$1");
        s = ItalicStar().Replace(s, "$1");
        s = ItalicUnderscore().Replace(s, "$1");

        return s;
    }

    /// <summary>
    /// Folds typographic punctuation onto the ASCII a phonemiser understands.
    ///
    /// <para><b>This is a pronunciation fix, not tidiness.</b> Models write contractions with
    /// U+2019 RIGHT SINGLE QUOTATION MARK, not the ASCII apostrophe. A phonemiser that does not
    /// recognise it as a contraction marker treats the two halves as separate words and spells
    /// the second one out: <em>"don't"</em> is spoken <em>"Don T"</em>. Invisible on screen,
    /// because U+2019 is exactly what a reader expects to see.</para>
    ///
    /// <para>Dashes are deliberately NOT mapped: an em dash is a pause and so is a hyphen, nothing
    /// has been observed mispronouncing one, and a test pins a sentence containing an em dash
    /// surviving intact. This list covers measured failures; adding to it on a hunch is how the
    /// emphasis rule in <see cref="StripMarkdown"/> nearly started eating ordinary prose.</para>
    /// </summary>
    public static char TypographicToAscii(char c) => c switch
    {
        // Single quotes and primes — the apostrophe case, which is the one that matters.
        '‘' or '’' or '‚' or '‛' or '′' => '\'',

        // Double quotes.
        '“' or '”' or '„' or '‟' or '″' => '"',

        // Non-breaking, thin, narrow and figure spaces read as no gap at all otherwise.
        '\u00A0' or '\u2009' or '\u202F' or '\u2007' => ' ',

        _ => c,
    };

    /// <summary>
    /// Strips markdown, then the characters a phonemiser cannot handle: surrogate pairs (all emoji
    /// and anything else outside the BMP), lone surrogates, and control characters other than
    /// whitespace; then collapses the doubled spaces that leaves behind. Everything speakable —
    /// accents, punctuation, currency — is BMP and survives. Returns an empty string for a chunk
    /// with nothing left to say.
    /// </summary>
    public static string MakeSpeakable(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        text = StripMarkdown(text);

        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = TypographicToAscii(text[i]);

            // A surrogate pair is one non-BMP character — an emoji, a rarer CJK glyph, a musical
            // symbol. Skip both halves; leaving one behind is itself invalid.
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                }

                continue;
            }

            // A low surrogate reached on its own is already malformed.
            if (char.IsLowSurrogate(c))
            {
                continue;
            }

            // Tabs and newlines read as whitespace; other control characters have no sound.
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            {
                continue;
            }

            builder.Append(c);
        }

        // Removing a character mid-sentence leaves doubled spaces, which a phonemiser reads as a
        // longer pause than the sentence had.
        return WhitespaceRun().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"^\s*```.*$", RegexOptions.Multiline)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"^\s*\|[\s\-:|]+\|\s*$", RegexOptions.Multiline)]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^\s*\|(.+)\|\s*$", RegexOptions.Multiline)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"^\s*#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*>\s?", RegexOptions.Multiline)]
    private static partial Regex Blockquote();

    [GeneratedRegex(@"^\s*[-*+]\s+", RegexOptions.Multiline)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*")]
    private static partial Regex BoldStars();

    [GeneratedRegex(@"__(?=\S)(.+?)(?<=\S)__")]
    private static partial Regex BoldUnderscores();

    [GeneratedRegex(@"\*(?=\S)([^*]+?)(?<=\S)\*")]
    private static partial Regex ItalicStar();

    [GeneratedRegex(@"(?<![A-Za-z0-9])_(?=\S)([^_]+?)(?<=\S)_(?![A-Za-z0-9])")]
    private static partial Regex ItalicUnderscore();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRun();
}
