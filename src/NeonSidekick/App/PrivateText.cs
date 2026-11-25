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

    /// <summary>The Sessions tab's buttons.</summary>
    public const string NewPrivateButton = Glyph + " New private";

    /// <summary>The Sessions tab's toggle.</summary>
    public const string PrivateButton = Glyph + " Private";

    /// <summary>A page's line or tap naming a private conversation that has ended. Pinned.</summary>
    public const string EndedConflict = "That private conversation has ended. Nothing was sent.";

    /// <summary>A restore while private: asks first. Pinned.</summary>
    public static string RestoreQuestion(long id) => string.Create(CultureInfo.InvariantCulture, $"Open session #{id}? This private conversation ends first.");

    /// <summary>A restore under the flag or variable: a private copy. Pinned.</summary>
    public static string CopyNotice(long id) =>
        string.Create(CultureInfo.InvariantCulture, $"{Glyph} Session #{id} opened as a private copy: the stored session stays as it is, and nothing of this conversation is saved.");

    /// <summary><c>/botchat --resume</c> of a saved chat while private.</summary>
    public const string BotResumeRefused = "A saved bot chat can't be carried on in a private conversation; /private off first.";

    /// <summary>The headless error: <c>/private</c> isn't a headless command.</summary>
    public const string HeadlessError = "/private is for the screen; start a private run with --private.";

    /// <summary><c>/tools</c>' and <c>/sysprompt</c>'s reason for the two tools left out.</summary>
    public const string ToolsNote = "not offered in a private conversation (Private learning is off)";

    /// <summary><c>/usage</c> › Models while private.</summary>
    public const string UsageNote = "Private turns aren't counted here.";

    /// <summary>A file checkpoint's message excerpt for a private turn.</summary>
    public const string CheckpointExcerpt = "private turn";

    /// <summary>The <c>/log</c> buffer's line after its drop. Pinned.</summary>
    public const string LogDroppedLine = "The private conversation's lines were dropped from this window.";

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

    /// <summary>"the picture", "the 3 pictures", "the PDF", "the picture and the PDF", "the 3 pictures and 1 PDF". Pinned.</summary>
    public static string Count(int pictures, int pdfs, bool the = true)
    {
        string p = pictures == 1 ? (the ? "the picture" : "1 picture") : (the ? "the " : "") + pictures.ToString(CultureInfo.InvariantCulture) + " pictures";
        string d = pdfs == 1 ? (the ? "the PDF" : "1 PDF") : (the ? "the " : "") + pdfs.ToString(CultureInfo.InvariantCulture) + " PDFs";
        if (pictures == 0)
        {
            return d;
        }

        if (pdfs == 0)
        {
            return p;
        }

        if (the && pictures == 1 && pdfs == 1)
        {
            return "the picture and the PDF";
        }

        // Mixed counts: the first "the" covers both ("the 3 pictures and 1 PDF").
        string second = pdfs.ToString(CultureInfo.InvariantCulture) + (pdfs == 1 ? " PDF" : " PDFs");
        string first = (the ? "the " : "") + pictures.ToString(CultureInfo.InvariantCulture) + (pictures == 1 ? " picture" : " pictures");
        return first + " and " + second;
    }

    /// <summary>The question. Pinned.</summary>
    public static string PicturesQuestion(int pictures, int pdfs) => $"{Glyph} Keep {Count(pictures, pdfs)} from this private conversation?";

    /// <summary>The dim line under it: Delete is for good. Pinned.</summary>
    public static string PicturesCaption(int total, bool mac) =>
        $"Delete is permanent; {(total == 1 ? "it doesn't" : "they don't")} go to the {(mac ? "Trash" : "Recycle Bin")}.";

    /// <summary>The line afterwards. Pinned.</summary>
    public static string KeptLine(int pictures, int pdfs) => $"({Glyph} kept {ResultCount(pictures, pdfs)})";

    /// <summary>A result's count: "the picture and the PDF" for one of each kind at most, else "2 pictures and 1 PDF".</summary>
    public static string ResultCount(int pictures, int pdfs) => Count(pictures, pdfs, the: pictures <= 1 && pdfs <= 1);

    /// <summary>The line after a delete: what went, and the open files left. Pinned.</summary>
    public static string DeletedLine(int pictures, int pdfs, IReadOnlyList<string> open)
    {
        string what = pictures + pdfs == 0 ? "nothing" : ResultCount(pictures, pdfs);
        if (open.Count == 0)
        {
            return $"({Glyph} deleted {what})";
        }

        var names = open.Take(3).Select(Path.GetFileName).ToList();
        string list = names.Count switch
        {
            1 => names[0]!,
            2 => names[0] + " and " + names[1],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
        if (open.Count > 3)
        {
            list = string.Join(", ", names) + " and " + (open.Count - 3).ToString(CultureInfo.InvariantCulture) + " more";
        }

        return $"({Glyph} deleted {what}; kept {list}, {(open.Count == 1 ? "which is" : "which are")} open)";
    }

    /// <summary>At the app's exit, after the alternate buffer: a file left. Pinned.</summary>
    public static string LeftAtExitLine(IReadOnlyList<string> left)
    {
        ArgumentNullException.ThrowIfNull(left);
        var names = left.Take(3).Select(Path.GetFileName).ToList();
        string more = left.Count > 3 ? string.Create(CultureInfo.InvariantCulture, $" and {left.Count - 3} more") : "";
        return $"{Glyph} Left from the private conversation (open elsewhere): {string.Join(", ", names)}{more}";
    }
}
