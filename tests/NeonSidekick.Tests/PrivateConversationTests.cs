using System.Text;
using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// Private conversations' pure parts (2026-11-25, the private-conversations plan): the store's delete leaves no trace in the file or its WAL, the
/// input lines' ids, the rule's 🕶️, Claude's command lines, the hidden log line and crash entry, the result lines' counts.
/// </summary>
public sealed class PrivateConversationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        PrivateMode.HideLog = false;
        PrivateMode.Active = false;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static bool FileHolds(string path, string marker)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        byte[] bytes = File.ReadAllBytes(path);
        return Holds(bytes, Encoding.UTF8.GetBytes(marker)) || Holds(bytes, Encoding.UTF8.GetBytes(marker.ToLowerInvariant()));
    }

    private static bool Holds(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    /// <summary>
    /// The switch's delete (the plan's proof of 2026-11-25): a session rewritten over several turns, then shrunk, then deleted with its input lines; no
    /// copy of the marker is left in the database file or its WAL, the FTS index's lower-cased terms included, and the other session stays.
    /// </summary>
    [Fact]
    public void DeleteForPrivate_LeavesTheMarkerNowhere_InTheFileOrItsWal_AndKeepsTheRest()
    {
        const string Marker = "ZebraQuartzMarker";
        long keep;
        CommandRow? line;
        using (var store = new SessionStore(_dir, _time))
        {
            keep = store.Begin("kept session", "llama")!.Value;
            store.AppendTurn(keep, "an ordinary line", "an ordinary reply", 0, [], [], 0, 1, 1, false);
            long id = store.Begin("private " + Marker, "llama")!.Value;
            var history = new StringBuilder();
            for (int turn = 0; turn < 6; turn++)
            {
                store.AppendTurn(id, "say " + Marker + " " + turn, "heard " + Marker, 0, [], [], 0, 1, 1, false);
                history.Append(Marker).Append(' ', 400);
                store.SaveHistory(id, "[{\"role\":\"user\",\"text\":\"" + history + "\"}]");
            }

            store.SaveHistory(id, "[]");   // shrunk: the earlier copies sit in free pages
            line = store.AppendCommand("typed " + Marker);
            Assert.NotNull(line);
            Assert.Null(store.AppendCommand("typed " + Marker));   // a repeat inserts nothing and names no row

            Assert.Equal(PrivateDeleteOutcome.Deleted, store.DeleteForPrivate(id, [line.Value]));
            Assert.Null(store.Summary(id));
            Assert.NotNull(store.Summary(keep));
            Assert.DoesNotContain(store.CommandHistory(), l => l.Contains(Marker, StringComparison.Ordinal));
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        string db = Path.Combine(_dir, SessionStore.FolderName, SessionStore.FileName);
        Assert.True(File.Exists(db));
        Assert.False(FileHolds(db, Marker), "the database file still holds the marker");
        Assert.False(FileHolds(db + "-wal", Marker), "the WAL still holds the marker");
        Assert.True(FileHolds(db, "kept session"));
    }

    /// <summary>A row another window already deleted is gone, not a failure: the switch goes on.</summary>
    [Fact]
    public void DeleteForPrivate_ARowAlreadyGone_IsGone()
    {
        using var store = new SessionStore(_dir, _time);
        Assert.Equal(PrivateDeleteOutcome.Gone, store.DeleteForPrivate(9_999, []));
    }

    /// <summary>The rule's 🕶️ (the user's ask): after ⤡ and 🎞️, each missing one closing up; a narrow rule drops 🎞️, then ⤡, then 🕶️.</summary>
    [Fact]
    public void UpperRule_ThePrivateMark_FollowsTheButtons_AndGoesLast()
    {
        string film = "🎞️";
        var all = ScreenPane.UpperRule("title", folds: true, 60, film, hidden: true);
        Assert.StartsWith("─ ⤡ 🎞️ 🕶️ ─", all.Text, StringComparison.Ordinal);
        Assert.Equal(2, all.FoldColumn);
        Assert.Equal(4, all.StripColumn);
        Assert.Equal(4 + TextCells.Width(film + " "), all.PrivateColumn);
        Assert.Equal(60, TextCells.Width(all.Text));
        Assert.EndsWith(" title ─", all.Text, StringComparison.Ordinal);

        Assert.StartsWith("─ ⤡ 🕶️ ─", ScreenPane.UpperRule("", folds: true, 40, null, hidden: true).Text, StringComparison.Ordinal);
        var alone = ScreenPane.UpperRule("", folds: false, 40, null, hidden: true);
        Assert.StartsWith("─ 🕶️ ─", alone.Text, StringComparison.Ordinal);
        Assert.Equal(2, alone.PrivateColumn);
        Assert.Equal(-1, alone.FoldColumn);

        // The drop order: 🎞️ first, then ⤡, then 🕶️.
        int markCells = TextCells.Width(ScreenPane.PrivateGlyph + " ");
        int filmCells = TextCells.Width(film + " ");
        int full = 2 + 2 + filmCells + markCells + ScreenPane.RuleTitleMinRule;
        Assert.Contains(film, ScreenPane.UpperRule("", true, full, film, true).Text, StringComparison.Ordinal);
        var noFilm = ScreenPane.UpperRule("", true, full - 1, film, true);
        Assert.DoesNotContain(film, noFilm.Text, StringComparison.Ordinal);
        Assert.Contains("⤡", noFilm.Text, StringComparison.Ordinal);
        Assert.Contains(ScreenPane.PrivateGlyph, noFilm.Text, StringComparison.Ordinal);
        var noFold = ScreenPane.UpperRule("", true, 2 + markCells + ScreenPane.RuleTitleMinRule + 1, film, true);
        Assert.DoesNotContain("⤡", noFold.Text, StringComparison.Ordinal);
        Assert.Contains(ScreenPane.PrivateGlyph, noFold.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ScreenPane.PrivateGlyph, ScreenPane.UpperRule("", true, 2 + markCells + ScreenPane.RuleTitleMinRule - 1, film, true).Text, StringComparison.Ordinal);

        // Not private: the rule as it was.
        Assert.Equal(ScreenPane.UpperRule("title", true, 60, film).Text, ScreenPane.UpperRule("title", true, 60, film, hidden: false).Text);
    }

    /// <summary>A private <c>/claude</c> and server keep no transcript and never resume (the CLI proof of 2026-11-25).</summary>
    [Fact]
    public void Claude_APrivateRequest_KeepsNoSession_AndNeverResumes()
    {
        var request = new ClaudeRequest("hi", "id-1", Resume: true, "C:\\work", ClaudePermissionLevel.ReadOnly, Private: true);
        var arguments = ClaudeArguments.Build(request);
        Assert.Contains("--no-session-persistence", arguments);
        Assert.DoesNotContain("--resume", arguments);
        Assert.Equal("id-1", arguments[arguments.ToList().IndexOf("--session-id") + 1]);
        Assert.DoesNotContain("--no-session-persistence", ClaudeArguments.Build(request with { Private = false }));
        Assert.Contains("--resume", ClaudeArguments.Build(request with { Private = false }));

        var launch = new ClaudeServerLaunch("claude", "s-1", "sonnet", null, "C:\\work", "prompt", "", Private: true);
        var server = ClaudeArguments.BuildServer(launch, resume: true, "p.txt", "{}");
        Assert.Contains("--no-session-persistence", server);
        Assert.DoesNotContain("--resume", server);
        Assert.NotEqual(launch, launch with { Private = false });   // a switch restarts the server's child
        Assert.Equal("CLAUDE_CODE_SKIP_PROMPT_HISTORY", ClaudeProcess.SkipPromptHistoryVariable);
        Assert.Equal("CLAUDE_CODE_FORCE_SESSION_PERSISTENCE", ClaudeProcess.ForceSessionPersistenceVariable);
    }

    /// <summary>Under <c>hide</c> the <c>--log</c> file keeps each line's time, level and category, never its words; an audit line stays whole.</summary>
    [Fact]
    public void TheLogFile_HidesWhatIsSaid_AndKeepsAuditLinesWhole()
    {
        string path = Path.Combine(_dir, "neon.log");
        string category = "PrivateTest-" + Guid.NewGuid().ToString("N");
        bool echo = DiagnosticLog.EchoToConsole;
        DiagnosticLog.EchoToConsole = false;
        try
        {
            using (DiagnosticFileSink.Open(path))
            {
                DiagnosticLog.Info(category, "before ZebraQuartz");
                PrivateMode.HideLog = true;
                DiagnosticLog.Info(category, "the secret ZebraQuartz");
                DiagnosticLog.Warn(category, "failed ZebraQuartz", new InvalidOperationException("ZebraQuartz inside"));
                DiagnosticLog.Audit(category, "audit: deleted table t");
                PrivateMode.HideLog = false;
                DiagnosticLog.Info(category, "after");
            }

            string[] lines = File.ReadAllLines(path).Where(l => l.Contains(category, StringComparison.Ordinal)).ToArray();
            Assert.EndsWith($"[Info] {category}: before ZebraQuartz", lines[0], StringComparison.Ordinal);
            Assert.EndsWith($"[Info] {category}: [private]", lines[1], StringComparison.Ordinal);
            Assert.EndsWith($"[Warning] {category}: [private] (InvalidOperationException)", lines[2], StringComparison.Ordinal);
            Assert.EndsWith($"[Info] {category}: audit: deleted table t", lines[3], StringComparison.Ordinal);
            Assert.EndsWith($"[Info] {category}: after", lines[4], StringComparison.Ordinal);
            Assert.Equal(1, lines.Count(l => l.Contains("ZebraQuartz", StringComparison.Ordinal)));
        }
        finally
        {
            PrivateMode.HideLog = false;
            DiagnosticLog.EchoToConsole = echo;
        }
    }

    /// <summary>Under <c>hide</c> a crash entry keeps the types and the stacks, never a message (inner ones too).</summary>
    [Fact]
    public void ACrashEntry_UnderHide_HasNoMessage()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("outer ZebraQuartz", new ArgumentException("inner ZebraQuartz"));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        string hidden = CrashReport.Entry(thrown, new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc), hide: true);
        Assert.DoesNotContain("ZebraQuartz", hidden, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), hidden, StringComparison.Ordinal);
        Assert.Contains(nameof(ArgumentException), hidden, StringComparison.Ordinal);
        Assert.Contains("ZebraQuartz", CrashReport.Entry(thrown, new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc)), StringComparison.Ordinal);
    }

    /// <summary>The pictures question's words, each mix counted apart (the user's pick).</summary>
    [Fact]
    public void ThePicturesWords_CountEachKindApart()
    {
        Assert.Equal("(🕶️ kept the picture)", PrivateText.KeptLine(1, 0));
        Assert.Equal("(🕶️ kept the picture and the PDF)", PrivateText.KeptLine(1, 1));
        Assert.Equal("(🕶️ kept 3 pictures and 2 PDFs)", PrivateText.KeptLine(3, 2));
        Assert.Equal("(🕶️ kept 2 PDFs)", PrivateText.KeptLine(0, 2));
        Assert.Equal("(🕶️ deleted the picture and the PDF)", PrivateText.DeletedLine(1, 1, []));
        Assert.StartsWith("(🕶️ deleted nothing; kept photo-2.png", PrivateText.DeletedLine(0, 0, [Path.Combine(_dir, "photo-2.png")]), StringComparison.Ordinal);
        Assert.Contains("Keep the 3 pictures and 2 PDFs", PrivateText.PicturesQuestion(3, 2), StringComparison.Ordinal);
        Assert.Contains("the Recycle Bin", PrivateText.PicturesCaption(2, mac: false), StringComparison.Ordinal);
        Assert.Contains("the Trash", PrivateText.PicturesCaption(1, mac: true), StringComparison.Ordinal);
        Assert.Contains("and 2 more", PrivateText.LeftAtExitLine(["a.png", "b.png", "c.png", "d.png", "e.png"]), StringComparison.Ordinal);
    }

    /// <summary>The conversation's lists: a picture tracked once, deleted for good under Delete, kept (and still listed) under Keep; spill logs always go.</summary>
    [Fact]
    public void DeleteFiles_TakesThePicturesOnlyWhenAsked_AndTheSpillLogsAlways()
    {
        Directory.CreateDirectory(_dir);
        string picture = Path.Combine(_dir, "photo.png");
        string pdf = Path.Combine(_dir, "phone-1.pdf");
        string spill = Path.Combine(_dir, "spill.log");
        File.WriteAllText(picture, "x");
        File.WriteAllText(pdf, "x");
        File.WriteAllText(spill, "x");
        var conversation = new PrivateConversation();
        conversation.Enter(PrivateReason.Command, new AppSettingsData());
        try
        {
            conversation.TrackPicture(picture);
            conversation.TrackPicture(picture);
            conversation.TrackPicture(pdf);
            conversation.TrackSpill(spill);
            Assert.Equal((1, 1), conversation.CountFiles());

            var kept = conversation.DeleteFiles(deletePictures: false);
            Assert.Equal((0, 0), (kept.Pictures, kept.Pdfs));
            Assert.True(File.Exists(picture));
            Assert.False(File.Exists(spill));

            var deleted = conversation.DeleteFiles(deletePictures: true, isOpen: path => path == pdf);
            Assert.Equal(1, deleted.Pictures);
            Assert.False(File.Exists(picture));
            Assert.True(File.Exists(pdf));
            Assert.Equal([pdf], deleted.Open);
        }
        finally
        {
            conversation.Leave();
            conversation.ReleaseLog();
        }
    }
}
