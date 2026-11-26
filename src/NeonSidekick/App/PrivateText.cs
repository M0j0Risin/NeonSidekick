using System.Globalization;

namespace NeonSidekick.App;

/// <summary>A private conversation's words (2026-11-25, the private-conversations plan), pinned by its tests.</summary>
public static class PrivateText
{
    /// <summary>The log category.</summary>
    public const string Category = "Private";

    /// <summary>The upper rule's and the page's mark.</summary>
    public const string Glyph = "🕶️";

    /// <summary>The command's word.</summary>
    public const string Command = "/private";

    /// <summary><c>/private new</c>'s word.</summary>
    public const string NewWord = "new";

    /// <summary><c>/private off</c>'s word.</summary>
    public const string OffWord = "off";

    /// <summary>The usage line for a word <c>/private</c> doesn't take.</summary>
    public const string Usage = "Usage: /private, /private new or /private off.";

    /// <summary>The conversation went private.</summary>
    public const string EnteredNotice = Glyph + " This conversation is private from here: nothing of it is saved.";

    /// <summary>A fresh private conversation (<c>/private new</c>, or a start or <c>/new</c> under the flag, variable or setting).</summary>
    public const string NewNotice = Glyph + " A private conversation: nothing of it is saved.";

    /// <summary><c>/private</c> while private.</summary>
    public const string AlreadyNotice = "This conversation is already private.";

    /// <summary><c>/private off</c> while not private.</summary>
    public const string NotPrivateNotice = "This conversation isn't private.";

    /// <summary><c>/private off</c>: the private conversation ended, a normal one begins.</summary>
    public const string OffNotice = Glyph + " The private conversation ended; nothing of it was saved. This one is saved as usual.";

    /// <summary><c>/private off</c> under <c>--private</c> or the variable. Pinned.</summary>
    public static string OffRefused(string source) => $"{source} keeps every conversation of this run private; /new starts a fresh private one.";

    /// <summary>A loop running. Pinned.</summary>
    public const string LoopRefused = "/private waits for the loop or review to end.";

    /// <summary>A bot chat running.</summary>
    public const string BotChatRefused = "/private waits for the bot chat to end.";

    /// <summary>The switch's yes/no on a saved conversation. Pinned.</summary>
    public static string SwitchQuestion(long id, int turns, DateTimeOffset started) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Make this conversation private?\nSession #{id} ({turns} turn{(turns == 1 ? "" : "s")}, from {started:yyyy-MM-dd}) is deleted from the store with its input lines, and its earlier file changes can no longer be undone.");

    /// <summary>The switch's yes/no when the conversation holds file checkpoints but no row (logging off, a failed turn).</summary>
    public const string SwitchCheckpointsQuestion = "Make this conversation private?\nIts earlier file changes can no longer be undone.";

    /// <summary>The switch deleted the row. Pinned.</summary>
    public static string DeletedNotice(long id) => string.Create(CultureInfo.InvariantCulture, $"{Glyph} Session #{id} deleted. This conversation is private from here: nothing of it is saved.");

    /// <summary>The switch's delete failed: nothing changed.</summary>
    public const string DeleteFailedError = "The session couldn't be deleted, so the conversation isn't private. See /log.";

    /// <summary>Going private under <c>ask</c>.</summary>
    public const string AskNote = "You'll be asked about this conversation's pictures when it ends.";

    /// <summary>Going private under <c>delete</c>.</summary>
    public const string DeleteNote = "Pictures from this conversation are deleted when it ends. Copy any you want to keep.";

    /// <summary>What deliberate saves still do while private: said once, at entry.</summary>
    public const string SavesNote = "What you save on purpose (a memory, a prompt, an export, a print) and what the tools change still stays.";

    /// <summary>With no row: <c>/rename</c>, <c>/sessions title</c>, a bare <c>/sessions export</c>.</summary>
    public const string NotSavedNotice = "This conversation is private and isn't saved.";

    /// <summary>The page's and the Sessions tab's word for it.</summary>
    public const string PageTitle = "Private conversation";


    /// <summary>A restore while private: asks first. Pinned.</summary>
    public static string RestoreQuestion(long id) => string.Create(CultureInfo.InvariantCulture, $"Open session #{id}? This private conversation ends first.");

    /// <summary>A restore under the flag or variable: a private copy. Pinned.</summary>
    public static string CopyNotice(long id) =>
        string.Create(CultureInfo.InvariantCulture, $"{Glyph} Session #{id} opened as a private copy: the stored session stays as it is, and nothing of this conversation is saved.");

    /// <summary><c>/botchat --resume</c> of a saved chat while private.</summary>
    public const string BotResumeRefused = "A saved bot chat can't be carried on in a private conversation; /private off first.";

    /// <summary>The headless error: <c>/private</c> isn't a headless command.</summary>
    public const string HeadlessError = "/private is for the screen; start a private run with --private.";

    /// <summary><c>/tools</c>' reason for the two tools left out (its rows' note, and Enter on one, which doesn't flip it).</summary>
    public const string ToolsNote = "not offered in a private conversation (Private learning is off)";

    /// <summary><c>/usage</c> › Models while private.</summary>
    public const string UsageNote = "Private turns aren't counted here.";

    /// <summary>A file checkpoint's message excerpt for a private turn.</summary>
    public const string CheckpointExcerpt = "private turn";

    /// <summary>The <c>/log</c> buffer's line after its drop. Pinned.</summary>
    public const string LogDroppedLine = "The private conversation's lines were dropped from this window.";

    /// <summary>The Info line when a conversation goes private: <c>The conversation went private (command).</c></summary>
    public static string WentPrivateLogLine(PrivateReason reason) => "The conversation went private (" + reason.ToString().ToLowerInvariant() + ").";

    /// <summary>The Info line when a private conversation ends.</summary>
    public const string EndedLogLine = "The private conversation ended.";

    /// <summary>The Info line when a picture the end cancelled is still being made past its wait (a file it saves later is deleted).</summary>
    public const string PictureStillRunningLogLine = "A picture the private conversation's end cancelled was still being made; a file it saves is deleted.";

    /// <summary>The Warn line when one of the end's steps throws (the rest go on).</summary>
    public static string EndStepFailedLogLine(string kind) => "A step of the private conversation's end failed: " + kind;

    /// <summary>A file the end couldn't delete. Pinned.</summary>
    public static string DeleteFailedLogLine(string kind) => $"A private conversation's file couldn't be deleted ({kind}); it is tried again at exit.";

    /// <summary>The kept count when nothing could answer the ask. Pinned.</summary>
    public static string KeptUnaskedLogLine(int pictures, int pdfs) => $"Private conversation ended with nobody to ask: kept {Count(pictures, pdfs, the: false)}.";

    // ─── The pictures question ─────────────────────────────────────────────

    /// <summary>The Keep row.</summary>
    public const string KeepRow = "Keep";

    /// <summary>The Delete row.</summary>
    public const string DeleteRow = "Delete";

    /// <summary>The question's hint. Pinned by the hint tests.</summary>
    public const string PicturesHint = "k / d = pick · Enter = choose · Esc = keep";

    /// <summary>
    /// With <paramref name="the"/>, each kind in its own form: "the picture", "the 3 pictures", "the picture and the PDF", "the picture and the 2 PDFs",
    /// "the 3 pictures and the PDF" (2026-11-26, the review's #79: one "the" over a mixed pair read wrong). Without it: "1 picture and 2 PDFs". Pinned.
    /// </summary>
    public static string Count(int pictures, int pdfs, bool the = true)
    {
        string p = pictures == 1 ? (the ? "the picture" : "1 picture") : (the ? "the " : "") + pictures.ToString(CultureInfo.InvariantCulture) + " pictures";
        string d = pdfs == 1 ? (the ? "the PDF" : "1 PDF") : (the ? "the " : "") + pdfs.ToString(CultureInfo.InvariantCulture) + " PDFs";
        if (pictures == 0)
        {
            return d;
        }

        return pdfs == 0 ? p : p + " and " + d;
    }

    /// <summary>The question: the conversation's pictures, those from before it went private too (2026-11-26, the review's #75). Pinned.</summary>
    public static string PicturesQuestion(int pictures, int pdfs) => $"{Glyph} Keep {Count(pictures, pdfs)} this conversation made (before it went private too)?";

    /// <summary>The dim line under it: Delete is for good, never to the Recycle bin (the Trash on a Mac, <see cref="Files.FileText.RecycleBinName"/>). Pinned.</summary>
    public static string PicturesCaption(int total) =>
        $"Delete is permanent; {(total == 1 ? "it doesn't" : "they don't")} go to the {Files.FileText.RecycleBinName}.";

    /// <summary>The line afterwards. Pinned.</summary>
    public static string KeptLine(int pictures, int pdfs) => $"({Glyph} kept {ResultCount(pictures, pdfs)})";

    /// <summary>A result's count: "the picture and the PDF" for one of each kind at most, else "2 pictures and 1 PDF".</summary>
    public static string ResultCount(int pictures, int pdfs) => Count(pictures, pdfs, the: pictures <= 1 && pdfs <= 1);

    /// <summary>
    /// The line after the end's deletes: what went, the open files kept, and the files a delete failed on (a spill log or a temp copy, 2026-11-26, the
    /// review's #98: said apart from the open ones, and tried again at the exit). With no picture deleted or kept open, the failed part alone. Pinned.
    /// </summary>
    public static string DeletedLine(int pictures, int pdfs, IReadOnlyList<string> open, IReadOnlyList<string> failed)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(failed);
        string failedPart = failed.Count == 0 ? "" : $"couldn't delete {Names(failed)}, tried again at the exit";
        if (pictures + pdfs == 0 && open.Count == 0 && failed.Count > 0)
        {
            return $"({Glyph} {failedPart})";
        }

        string what = pictures + pdfs == 0 ? "nothing" : ResultCount(pictures, pdfs);
        string openPart = open.Count == 0 ? "" : $"; kept {Names(open)}, {(open.Count == 1 ? "which is" : "which are")} open";
        return $"({Glyph} deleted {what}{openPart}{(failedPart.Length == 0 ? "" : "; " + failedPart)})";
    }

    /// <summary>"a.png", "a.png and b.png", "a.png, b.png and c.png", "a.png, b.png, c.png and 2 more": file names only.</summary>
    private static string Names(IReadOnlyList<string> paths)
    {
        var names = paths.Take(3).Select(Path.GetFileName).ToList();
        if (paths.Count > 3)
        {
            return string.Join(", ", names) + " and " + (paths.Count - 3).ToString(CultureInfo.InvariantCulture) + " more";
        }

        return names.Count switch
        {
            1 => names[0]!,
            2 => names[0] + " and " + names[1],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    /// <summary>
    /// At the app's exit, after the alternate buffer: the files left, the open ones apart from the ones a delete failed on (2026-11-26). Null with
    /// neither. Pinned.
    /// </summary>
    public static string? LeftAtExitLine(IReadOnlyList<string> open, IReadOnlyList<string> failed)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(failed);
        var parts = new List<string>();
        if (open.Count > 0)
        {
            parts.Add(Names(open) + " (open elsewhere)");
        }

        if (failed.Count > 0)
        {
            parts.Add(Names(failed) + " (couldn't be deleted)");
        }

        return parts.Count == 0 ? null : $"{Glyph} Left from the private conversation: {string.Join("; ", parts)}";
    }
}
