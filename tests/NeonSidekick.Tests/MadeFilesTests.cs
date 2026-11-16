using System.IO.Compression;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

/// <summary>
/// The made files' list (2026-11-16, plan lucid-roaming-lantern #41): fed from the journal's hooks per tool call, with or without a journal; a
/// write, a move's target and an unzip's files listed; a failed, refused or uncommitted write and an app write never; a write after its call
/// closed never; past 200 the newest kept; one file written again in a turn one row, under a later turn a second entry a rewind of that turn
/// leaves; only today's roots, a pinned copy of the root counting; saved with its turns' ordinals and restored against them.
/// </summary>
public sealed class MadeFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "neon-made-" + Guid.NewGuid().ToString("N"));
    private readonly WorkingDirectory _files;
    private readonly MadeFiles _made;

    public MadeFilesTests()
    {
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, TimeProvider.System);
        _made = new MadeFiles(() => [_root]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ChatMessage Turn(string text) => new(ChatRole.User, text);

    private void Call(ChatMessage turn, Action<WorkingDirectory> act)
    {
        using (MadeFiles.Enter(_made, turn))
        {
            act(_files);
        }
    }

    private List<string> Names() => [.. _made.Listed(File.Exists).Select(f => Path.GetRelativePath(_root, f.Full).Replace('\\', '/'))];

    [Fact]
    public void AWrite_AMove_AndAnUnzip_AreListed_NewestFirst_AndARemoveHidesTheRow()
    {
        var one = Turn("one");
        long v0 = _made.Version;
        Call(one, f => Assert.Equal(FileOutcome.Ok, f.WriteText("a.txt", "a", overwrite: false).Outcome));
        Assert.True(_made.Version > v0);
        Call(one, f => Assert.Equal(FileOutcome.Ok, f.Move("a.txt", "out/b.txt", overwrite: false).Outcome));
        Assert.Equal(["out/b.txt"], Names());   // a.txt is gone: its row hides

        using (var archive = ZipFile.Open(Path.Combine(_root, "pack.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("in/z.txt").Open());
            writer.Write("z");
        }

        Call(one, f => Assert.Equal(FileOutcome.Ok, f.Unzip("pack.zip", "x", overwrite: false).Outcome));
        Assert.Equal(["x/in/z.txt", "out/b.txt"], Names());

        long v1 = _made.Version;
        Call(one, f => Assert.Equal(FileOutcome.Ok, f.Delete("x/in/z.txt").Outcome));
        Assert.True(_made.Version > v1);   // no entry changed, but a row went
        Assert.Equal(["out/b.txt"], Names());

        Call(one, f => Assert.Equal(FileOutcome.Ok, f.CreateDirectory("folder").Outcome));
        Assert.Equal(["out/b.txt"], Names());   // a folder is no file
    }

    [Fact]
    public async Task ARefusedWrite_AnUncommittedOne_AnAppWrite_AndOneAfterItsCallClosed_ListNothing()
    {
        File.WriteAllText(Path.Combine(_root, "mine.txt"), "the user's");
        var one = Turn("one");
        Call(one, f => Assert.Equal(FileOutcome.Exists, f.WriteText("mine.txt", "x", overwrite: false).Outcome));
        Call(one, f =>
        {
            // A download whose hash didn't match: its Writing hook ran, the write was never committed.
            f.BeginWrite("fresh.bin", overwrite: false, out var fresh);
            fresh!.Stream.Write("x"u8);
            fresh.Dispose();
            f.BeginWrite("mine.txt", overwrite: true, out var over);
            over!.Stream.Write("y"u8);
            over.Dispose();
        });
        Call(one, f => WriteGuard.AsApp(() => f.WriteText("app.txt", "app", overwrite: false)));

        var gate = new TaskCompletionSource();
        Task? later = null;
        Call(one, f => later = Task.Run(async () =>
        {
            await gate.Task;
            f.WriteText("late.txt", "late", overwrite: false);
        }));
        gate.SetResult();
        await later!;

        Assert.True(File.Exists(Path.Combine(_root, "app.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "late.txt")));
        Assert.Empty(Names());
    }

    [WindowsFact]
    public void AnOverwriteOfALockedFile_ListsNothing_AndAFailedPatch_AfterAGoodWrite_KeepsTheRow()
    {
        File.WriteAllText(Path.Combine(_root, "locked.txt"), "the user's");
        var one = Turn("one");
        using (new FileStream(Path.Combine(_root, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Call(one, f => Assert.NotEqual(FileOutcome.Ok, f.WriteText("locked.txt", "x", overwrite: true).Outcome));
        }

        Assert.Empty(Names());

        Call(one, f => Assert.Equal(FileOutcome.Ok, f.WriteText("made.txt", "a", overwrite: false).Outcome));
        using (new FileStream(Path.Combine(_root, "made.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Call(one, f => Assert.NotEqual(FileOutcome.Ok, f.EditText("made.txt", "a", "b").Outcome));
        }

        Assert.Equal(["made.txt"], Names());
    }

    [Fact]
    public void AFileWrittenAgain_InItsTurn_IsOneEntry_AndUnderALaterTurn_ARewindOfThatTurnKeepsIt()
    {
        var one = Turn("one");
        var two = Turn("two");
        Call(one, f => f.WriteText("a.txt", "1", overwrite: false));
        Call(one, f => f.WriteText("b.txt", "1", overwrite: false));
        Call(one, f => f.WriteText("a.txt", "22", overwrite: true));
        Assert.Equal(["a.txt", "b.txt"], Names());   // moved to the newest place
        Assert.Equal(2, _made.Saved([one]).Count);

        string token = _made.Listed(File.Exists)[0].Token;
        for (int i = 0; i < 3; i++)
        {
            Call(two, f => f.WriteText("a.txt", new string('x', i + 3), overwrite: true));
        }

        Assert.Equal(["a.txt", "b.txt"], Names());   // one row
        Assert.Equal(token, _made.Listed(File.Exists)[0].Token);   // the path keeps its token
        Assert.Equal(3, _made.Saved([one, two]).Count);

        _made.Rewound(new HashSet<ChatMessage>([one, two], ReferenceEqualityComparer.Instance), [one]);
        Assert.Equal(["a.txt", "b.txt"], Names());   // turn one's entry stays
        Assert.Equal(2, _made.Saved([one]).Count);
    }

    [Fact]
    public void PastTwoHundred_TheNewestAreKept()
    {
        var one = Turn("one");
        for (int i = 0; i < MadeFiles.Keep + 5; i++)
        {
            int n = i;
            Call(one, f => f.WriteText("f" + n.ToString("000", System.Globalization.CultureInfo.InvariantCulture) + ".txt", "x", overwrite: false));
        }

        var names = Names();
        Assert.Equal(MadeFiles.Keep, names.Count);
        Assert.Equal("f204.txt", names[0]);
        Assert.DoesNotContain("f004.txt", names);
        Assert.True(_made.Trimmed);
    }

    [Fact]
    public void APinnedCopyOfTheRoot_Counts_AndAnotherRoot_DoesNot()
    {
        string vault = Path.Combine(Path.GetTempPath(), "neon-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vault);
        try
        {
            var one = Turn("one");
            using (MadeFiles.Enter(_made, one))
            {
                _files.PinnedAt(_root).WriteText("pinned.txt", "p", overwrite: false);
                new WorkingDirectory(() => vault, TimeProvider.System).WriteText("note.md", "v", overwrite: false);
            }

            Assert.Equal(["pinned.txt"], Names());
        }
        finally
        {
            Directory.Delete(vault, recursive: true);
        }
    }

    [Fact]
    public void ARestore_TiesEachEntryToItsOrdinal_DropsOnesPastTheTurns_AndKeepsOnesWithNone()
    {
        var restored = new List<ChatMessage> { Turn("first"), new(ChatRole.Assistant, "ok"), Turn("second") };
        ConversationHistory.SetTurnOrdinal(restored[0], 1);
        ConversationHistory.SetTurnOrdinal(restored[2], 2);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(_root, "c.txt"), "c");
        File.WriteAllText(Path.Combine(_root, "d.txt"), "d");

        _made.Restore(
            [new StoredMadeFile(Path.Combine(_root, "a.txt"), 1), new StoredMadeFile(Path.Combine(_root, "b.txt"), 2),
                new StoredMadeFile(Path.Combine(_root, "c.txt"), 3), new StoredMadeFile(Path.Combine(_root, "d.txt"))],
            restored,
            turns: 2);

        Assert.Equal(["d.txt", "b.txt", "a.txt"], Names());   // c.txt's turn was cut
        var saved = _made.Saved(restored);
        Assert.Equal([1, 2, null], saved.Select(s => s.Ordinal));

        _made.Rewound(new HashSet<ChatMessage>(restored, ReferenceEqualityComparer.Instance), restored.Take(2));
        Assert.Equal(["d.txt", "a.txt"], Names());   // the second turn's went; the one with no turn stays
    }

    [Fact]
    public void ANestedFile_IsUnderItsRoot_AndAPrefixIsNot()
    {
        Assert.True(MadeFiles.IsUnder(Path.Combine(_root, "out", "sub", "a.txt"), _root));
        Assert.False(MadeFiles.IsUnder(_root + "x" + Path.DirectorySeparatorChar + "a.txt", _root));
        Assert.False(MadeFiles.IsUnder(_root, _root));
    }

    [Fact]
    public void WithNoListEntered_NothingIsRecorded()
    {
        _files.WriteText("a.txt", "a", overwrite: false);
        using (MadeFiles.Enter(null, null))
        {
            _files.WriteText("b.txt", "b", overwrite: false);
        }

        Assert.Empty(Names());
    }
}
