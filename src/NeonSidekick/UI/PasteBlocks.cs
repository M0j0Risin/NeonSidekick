using System.Text;
using System.Text.RegularExpressions;
using NeonSidekick.Files;

namespace NeonSidekick.UI;

/// <summary>
/// The long pastes and the images of a session, held off the input line. A pasted block that is
/// short enough (<see cref="Collapses"/>: at most <see cref="InlineMaxLines"/> lines and
/// <see cref="InlineMaxChars"/> characters) lands on the line as text; a longer one is kept here
/// and the draft holds one <em>token</em> for it — a private-use character, <c>U+E000</c> + the
/// block's index — that the line shows as its <see cref="Label"/> (<c>[Pasted text #1 +49 lines]</c>,
/// the Claude Code wording), moves over as one element, deletes as one, and expands into the block
/// when the line is sent. An image (a dropped file's path, <see cref="ImageFile.TryPastedPath"/>)
/// is a token of the same kind drawn as <see cref="ImageLabel"/> (<c>[Image #1]</c>, numbered on its
/// own); it expands into that label — the text the model reads names the picture — while the
/// picture itself goes beside the text (<see cref="ImagesIn"/>). Tokens survive in the session
/// history (Up recalls one; its block is still here), so the store lives as long as the
/// <see cref="InputLine"/>. A private-use character that is pasted or typed is never read as
/// a token: the line keeps it as a <em>literal</em> token of its own (<see cref="Literal"/>, 2026-10-04, the user's report:
/// every pasted Nerd Font or Powerline glyph lives in that range and was dropped until then), drawn and expanded as the
/// character itself, one per distinct character, never painted as a label.
///
/// <para>The draft is the string with tokens (what the line edits); the <em>display</em> string is
/// the draft with each token replaced by its label (what the pane draws). The two index mappings
/// carry the cursor and the selection one way and a click the other. Every rule pinned.</para>
/// </summary>
public sealed partial class PasteBlocks
{
    /// <summary>A paste of more lines than this is a token.</summary>
    public const int InlineMaxLines = 3;

    /// <summary>A paste of more characters than this is a token.</summary>
    public const int InlineMaxChars = 400;

    /// <summary>Lines of a block the transcript shows under the sent line by default (<see cref="Settings.AppSettingsData.PastePreviewLines"/>).</summary>
    public const int DefaultPreviewLines = 25;

    /// <summary>The most lines of a block the transcript shows under the sent line; 0 = the label alone.</summary>
    public const int MaxPreviewLines = 200;

    private const char FirstToken = '';
    private const char LastToken = '';

    /// <summary>
    /// One held thing: a text block or an image, with its number among its own kind — and an image's source, when known
    /// (<see cref="Original"/>) — or a private-use character the user pasted or typed (<see cref="Literal"/>), which stands for itself.
    /// </summary>
    private sealed record Block(int Number, string? Text, ImageAttachment? Image, byte[]? Original = null, string? SourcePath = null, char? Glyph = null);

    private readonly List<Block> _blocks = new();

    /// <summary>A clipboard picture's file once its message was sent (2026-10-09), by its number: <see cref="MarkSaved"/>. Under the blocks' lock.</summary>
    private readonly Dictionary<int, string> _saved = new();

    /// <summary>The literal token of each private-use character seen so far: one per character, however often it is pasted.</summary>
    private readonly Dictionary<char, char> _literals = new();
    private int _texts;
    private int _images;
    private int _documents;

    /// <summary>Blocks held so far this session, text and images together.</summary>
    public int Count => _blocks.Count;

    /// <summary>Images held so far this session (an attached PDF is not one: <see cref="DocumentCount"/>).</summary>
    public int ImageCount => _images;

    /// <summary>PDFs held so far this session (2026-10-14, the PDF plan's phase 3), numbered on their own.</summary>
    public int DocumentCount => _documents;

    /// <summary>True when a pasted (normalised) block is too long for the line and becomes a token.</summary>
    public static bool Collapses(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Lines(text) > InlineMaxLines || text.Length > InlineMaxChars;
    }

    /// <summary>Lines in a block: one more than its <c>'\n'</c>s; zero for an empty one.</summary>
    public static int Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? 0 : text.AsSpan().Count('\n') + 1;
    }

    /// <summary>A private-use character: what a token is.</summary>
    public static bool IsToken(char c) => c is >= FirstToken and <= LastToken;

    /// <summary>The label a text token is drawn as. Pinned.</summary>
    public static string Label(int number, string block)
    {
        int lines = Lines(block);
        // One long line (2026-10-04, the UI review): "+1 line" said nothing of its size; its characters do.
        return lines == 1
            ? $"[Pasted text #{number} · {block.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} chars]"
            : $"[Pasted text #{number} +{lines} lines]";
    }

    /// <summary>The label an image token is drawn as, and what it expands to. Pinned.</summary>
    public static string ImageLabel(int number) => $"[Image #{number}]";

    /// <summary>The line that closes a cut preview: how many lines of the block it left out. Pinned.</summary>
    public static string MoreLabel(int hidden) => $"[… +{hidden} more {(hidden == 1 ? "line" : "lines")}]";

    /// <summary>
    /// The first <paramref name="lines"/> lines of a block, the transcript's preview under the sent
    /// line, closed by <see cref="MoreLabel"/> when the block is longer; empty at 0.
    /// </summary>
    public static string Preview(string block, int lines)
    {
        ArgumentNullException.ThrowIfNull(block);
        int total = Lines(block);
        if (lines <= 0 || total == 0)
        {
            return "";
        }

        if (total <= lines)
        {
            return block;
        }

        int end = 0;
        for (int i = 0; i < lines; i++)
        {
            end = block.IndexOf('\n', end) + 1;
        }

        return string.Concat(block.AsSpan(0, end), MoreLabel(total - lines));
    }

    /// <summary>
    /// The preview of every text token in the draft, in order, each with the label it is drawn as
    /// (an image token has no preview: its picture goes under the line on its own); empty at 0.
    /// </summary>
    public IReadOnlyList<(string Label, string Preview)> Previews(string draft, int lines)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (lines <= 0)
        {
            return [];
        }

        List<(string, string)>? previews = null;
        foreach (char c in draft)
        {
            if (TryIndex(c, out int i) && _blocks[i].Text is { } text)
            {
                (previews ??= new List<(string, string)>()).Add((Label(_blocks[i].Number, text), Preview(text, lines)));
            }
        }

        return previews ?? (IReadOnlyList<(string, string)>)[];
    }

    /// <summary>
    /// The draft's form of <paramref name="c"/> (2026-10-04): itself, or for a private-use character (a Nerd Font glyph) the literal
    /// token that stands for it, so the draft never reads a pasted or typed glyph as one of its pastes.
    /// </summary>
    public char Literal(char c)
    {
        if (!IsToken(c))
        {
            return c;
        }

        lock (_blocks)
        {
            if (!_literals.TryGetValue(c, out char token))
            {
                token = Keep(new Block(0, null, null, Glyph: c));
                _literals[c] = token;
            }

            return token;
        }
    }

    /// <summary><paramref name="text"/> with every private-use character swapped for its literal token (<see cref="Literal"/>): pasted text as the draft holds it.</summary>
    public string Literals(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Any(IsToken))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            result.Append(Literal(c));
        }

        return result.ToString();
    }

    /// <summary>Whether <paramref name="c"/> is one of this store's literal tokens: drawn as its character, never as a label.</summary>
    public bool IsLiteral(char c) => TryIndex(c, out int i) && _blocks[i].Glyph is not null;

    /// <summary>Keeps <paramref name="block"/> and returns its token for the draft.</summary>
    public char Add(string block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return Keep(new Block(++_texts, block, null));
    }

    /// <summary>
    /// Keeps <paramref name="image"/> and returns its token for the draft. <paramref name="original"/> is a clipboard
    /// picture's own bytes and <paramref name="sourcePath"/> a dropped file's path (later still on 2026-09-24): what
    /// <see cref="Original"/> hands <c>generate_image</c> at full size, where the attachment may be the 2048 downscale.
    /// A clipboard picture that fitted is the same array as the attachment's, so keeping it costs nothing; a dropped
    /// file is read again at use, so it costs nothing either.
    /// </summary>
    public char AddImage(ImageAttachment image, byte[]? original = null, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_blocks)
        {
            return Keep(new Block(++_images, null, image, original, sourcePath));
        }
    }

    /// <summary>
    /// Keeps an attached PDF (2026-10-14, the PDF plan's phase 3: <paramref name="document"/>, an attachment of <see cref="PdfFile.MediaType"/>
    /// with its read under way) and returns its token: drawn as <see cref="Pdf.PdfText.DocumentLabel"/> (<c>[PDF #1 report.pdf]</c>), numbered
    /// on its own so the pictures' <c>[Image #N]</c> keep theirs.
    /// </summary>
    public char AddDocument(ImageAttachment document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_blocks)
        {
            return Keep(new Block(++_documents, null, document, SourcePath: document.Path));
        }
    }

    /// <summary>A held picture (not a PDF) by its number.</summary>
    private static bool IsPicture(Block b) => b.Image is { IsDocument: false };

    /// <summary>The number of the attached PDF <paramref name="document"/> is; null for any other attachment.</summary>
    public int? DocumentNumberOf(ImageAttachment document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_blocks)
        {
            // The very attachment, else the one a copy was made from (the send may carry a copy whose text it cut to fit, gap 43): the
            // copy shares its bytes, and each attachment read its own. By path until 2026-10-20 (review: the same PDF attached twice and
            // cut gave both copies the second's number).
            return (_blocks.Find(b => ReferenceEquals(b.Image, document) && b.Image.IsDocument)
                ?? _blocks.Find(b => b.Image is { IsDocument: true } held && ReferenceEquals(held.Bytes, document.Bytes)))?.Number;
        }
    }

    /// <summary>
    /// The <paramref name="number"/>th pasted picture as it was before any downscale (later still on 2026-09-24, the
    /// user's ask: a paste as <c>generate_image</c>'s input): a clipboard picture's own bytes with no name (the saver
    /// stamps one), or a dropped file read again under its own name. Null for no such picture, one kept without a source, or a dropped file that is gone
    /// or unreadable now. Called from a turn's task, so the store's list is read under its lock.
    /// </summary>
    public PastedPicture? Original(int number)
    {
        Block? block;
        lock (_blocks)
        {
            block = _blocks.Find(b => IsPicture(b) && b.Number == number);
        }

        if (block is null)
        {
            return null;
        }

        if (block.Original is { } bytes && block.SourcePath is null)
        {
            return new PastedPicture(bytes, null, SavedPath(number));
        }

        if (block.SourcePath is { } path)
        {
            try
            {
                return File.Exists(path) ? new PastedPicture(File.ReadAllBytes(path), Path.GetFileName(path), path) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The number of the pasted picture <paramref name="image"/> is (the very attachment the draft's token stands for); null for any other picture.</summary>
    public int? NumberOf(ImageAttachment image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_blocks)
        {
            return _blocks.Find(b => ReferenceEquals(b.Image, image) && IsPicture(b))?.Number;
        }
    }

    /// <summary>
    /// Whether picture <paramref name="number"/> came off the clipboard (2026-10-09): its own bytes kept and no file of its own, so it is
    /// saved when its message is sent. A dropped file has its file, and a picture kept without its source is not saved.
    /// </summary>
    public bool IsClipboard(int number)
    {
        lock (_blocks)
        {
            return _blocks.Find(b => IsPicture(b) && b.Number == number) is { Original: not null, SourcePath: null };
        }
    }

    /// <summary>Where clipboard picture <paramref name="number"/> was saved (<see cref="MarkSaved"/>); null until then.</summary>
    public string? SavedPath(int number)
    {
        lock (_blocks)
        {
            return _saved.GetValueOrDefault(number);
        }
    }

    /// <summary>Records that clipboard picture <paramref name="number"/> was saved as <paramref name="path"/> (a full path), so a resend reuses the file.</summary>
    public void MarkSaved(int number, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_blocks)
        {
            _saved[number] = path;
        }
    }

    /// <summary>
    /// The label a saved clipboard picture carries in the sent text (2026-10-09, the user's pick): <c>[Image #1, saved as C:\…\pasted-…png]</c>,
    /// so the model can hand the file to its picture tools. The transcript's line keeps <see cref="ImageLabel"/>. Pinned.
    /// </summary>
    public static string SavedLabel(int number, string path) => $"[Image #{number}, saved as {path}]";

    /// <summary>
    /// <paramref name="text"/> with every <see cref="SavedLabel"/> put back as its <see cref="ImageLabel"/>, and since 2026-10-14 every sent PDF's
    /// <see cref="Pdf.PdfText.DocumentSentLabel"/> as its draft label (the PDF plan's gap 49): a sent message read back as it was typed
    /// (<c>/rewind</c>'s draft, a withdrawn line). Pure.
    /// </summary>
    public static string WithoutSavedPaths(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string pictures = SavedLabelPattern().Replace(text, "[Image #$1]");
        return SentDocumentPattern().Replace(pictures, m => Pdf.PdfText.DocumentLabel(
            int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), Files.FileNames.LastPart(m.Groups[2].Value.Trim())));
    }

    [GeneratedRegex(@"\[Image #(\d+), saved as [^\]\r\n]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex SavedLabelPattern();

    // Up to the first ".pdf]", not the first ']' (2026-10-20, review): an attached PDF's path always ends in .pdf, and a name or folder may
    // hold a ']' of its own (report [v2].pdf, C:\docs\[old]\a.pdf).
    [GeneratedRegex(@"\[PDF #(\d+), from ([^\r\n]*?\.pdf)\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SentDocumentPattern();

    private char Keep(Block block)
    {
        lock (_blocks)
        {
            if (_blocks.Count > LastToken - FirstToken)
            {
                throw new InvalidOperationException("No room for another pasted block this session.");
            }

            _blocks.Add(block);
            return (char)(FirstToken + _blocks.Count - 1);
        }
    }

    /// <summary>The text a token stands for (an image token's is its label, a PDF's its label too); null for a character that is not one of this store's tokens.</summary>
    public string? BlockOf(char c) => TryIndex(c, out int i) ? _blocks[i].Glyph is { } glyph ? glyph.ToString() : _blocks[i].Text ?? AttachmentLabel(_blocks[i]) : null;

    /// <summary>An attachment's label: a picture's <see cref="ImageLabel"/>, a PDF's <see cref="Pdf.PdfText.DocumentLabel"/>.</summary>
    private static string AttachmentLabel(Block block) =>
        block.Image is { IsDocument: true } document ? Pdf.PdfText.DocumentLabel(block.Number, Path.GetFileName(document.Path)) : ImageLabel(block.Number);

    /// <summary>The image a token stands for; null for a text token or a character that is not one of this store's tokens.</summary>
    public ImageAttachment? ImageOf(char c) => TryIndex(c, out int i) ? _blocks[i].Image : null;

    /// <summary>The label a token is drawn as; null for a character that is not one of this store's tokens.</summary>
    public string? LabelOf(char c) =>
        TryIndex(c, out int i) ? _blocks[i].Glyph is { } glyph ? glyph.ToString() : _blocks[i].Text is { } text ? Label(_blocks[i].Number, text) : AttachmentLabel(_blocks[i]) : null;

    /// <summary>
    /// The draft with every token replaced by its label: the transcript's line. <paramref name="unbreakable"/>
    /// is the pane's form, the labels' spaces non-breaking (<see cref="Unbreakable"/>) so the word
    /// wrap never splits one over two rows.
    /// </summary>
    public string Display(string draft, bool unbreakable = false) =>
        Replace(draft, unbreakable ? c => LabelOf(c) is { } label ? Unbreakable(label) : null : LabelOf);

    /// <summary>A label with its spaces non-breaking (U+00A0, one cell each): one word to the wrap.</summary>
    public static string Unbreakable(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return label.Replace(' ', ' ');
    }

    /// <summary>The draft with every token replaced by its block (an image token by its label): the text that is sent.</summary>
    public string Expand(string draft) => Replace(draft, BlockOf);

    /// <summary>The images the draft's tokens stand for, in the order they appear: what goes beside the text.</summary>
    public IReadOnlyList<ImageAttachment> ImagesIn(string draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        List<ImageAttachment>? images = null;
        foreach (char c in draft)
        {
            if (ImageOf(c) is { } image)
            {
                (images ??= new List<ImageAttachment>()).Add(image);
            }
        }

        return images ?? (IReadOnlyList<ImageAttachment>)[];
    }

    /// <summary>Where a draft index is in the display string (a token's index maps to its label's start; the index after it to the label's end).</summary>
    public int ToDisplayIndex(string draft, int index)
    {
        ArgumentNullException.ThrowIfNull(draft);
        index = Math.Clamp(index, 0, draft.Length);
        int display = 0;
        for (int i = 0; i < index; i++)
        {
            display += LabelOf(draft[i])?.Length ?? 1;
        }

        return display;
    }

    /// <summary>
    /// Where a display index is in the draft: a click inside a label lands before the token in the
    /// label's first half and after it in the second, so a token is never split.
    /// </summary>
    public int ToDraftIndex(string draft, int displayIndex)
    {
        ArgumentNullException.ThrowIfNull(draft);
        int display = 0;
        for (int i = 0; i < draft.Length; i++)
        {
            int length = LabelOf(draft[i])?.Length ?? 1;
            if (displayIndex < display + length)
            {
                return displayIndex - display < (length + 1) / 2 ? i : i + 1;
            }

            display += length;
        }

        return draft.Length;
    }

    /// <summary>
    /// The draft index of the element whose display stretch holds <paramref name="displayIndex"/> (2026-09-30, the
    /// double-click's word): a token anywhere in its label, never the index after it as <see cref="ToDraftIndex"/> snaps
    /// a caret; the draft's length past its end.
    /// </summary>
    public int ToDraftElement(string draft, int displayIndex)
    {
        ArgumentNullException.ThrowIfNull(draft);
        int display = 0;
        for (int i = 0; i < draft.Length; i++)
        {
            display += LabelOf(draft[i])?.Length ?? 1;
            if (displayIndex < display)
            {
                return i;
            }
        }

        return draft.Length;
    }

    /// <summary>Each label's stretch in the display string, in order: what the pane paints in <see cref="Theme.PasteLabel"/>.</summary>
    public IReadOnlyList<(int Start, int Length)> LabelRanges(string draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var ranges = new List<(int, int)>();
        int display = 0;
        foreach (char c in draft)
        {
            if (LabelOf(c) is { } label)
            {
                if (!IsLiteral(c))
                {
                    ranges.Add((display, label.Length));   // a literal glyph is text, not a label (2026-10-04)
                }

                display += label.Length;
            }
            else
            {
                display++;
            }
        }

        return ranges;
    }

    private bool TryIndex(char c, out int index)
    {
        index = c - FirstToken;
        return IsToken(c) && index < _blocks.Count;
    }

    private string Replace(string draft, Func<char, string?> replacement)
    {
        ArgumentNullException.ThrowIfNull(draft);
        StringBuilder? result = null;
        for (int i = 0; i < draft.Length; i++)
        {
            if (replacement(draft[i]) is { } text)
            {
                result ??= new StringBuilder(draft.Length + text.Length).Append(draft, 0, i);
                result.Append(text);
            }
            else
            {
                result?.Append(draft[i]);
            }
        }

        return result?.ToString() ?? draft;
    }
}
