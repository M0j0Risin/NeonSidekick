using NeonSidekick.Audits;
using NeonSidekick.Git;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/code-audit commit </c>'s list (2026-11-25): the last commits on HEAD, read once and again only when HEAD's tip or the folder changes;
/// empty without a repository or above the sandbox; a subject made one clean line.
/// </summary>
public sealed class AuditCommitListTests : IDisposable
{
    private readonly AuditSandbox _box = new();
    private int _logs;

    public void Dispose() => _box.Dispose();

    private AuditCommitList List(GitAccess? git = null)
    {
        var door = git ?? _box.Git;
        return new AuditCommitList(() => door.ResolveCommit("", GitAccess.Head).Sha, count =>
        {
            _logs++;
            return door.Log("", GitAccess.Head, count);
        });
    }

    [Fact]
    public void TheList_IsReadOnce_AndAgainAfterANewCommitOrAnotherFolder()
    {
        GitAccessTests.Init(_box.Root);
        string first = GitAccessTests.CommitFile(_box.Root, "a.cs", "a", "first");
        var list = List();
        Assert.Equal([first[..GitAccess.ShortShaLength]], list.For(_box.Root).Select(i => i.Text));
        Assert.Equal("first", list.For(_box.Root)[0].Note);
        Assert.Equal(1, _logs);
        string second = GitAccessTests.CommitFile(_box.Root, "b.cs", "b", "second");
        Assert.Equal([second[..7], first[..7]], list.For(_box.Root).Select(i => i.Text));
        Assert.Equal(2, _logs);
        list.For(_box.Root + "-elsewhere");
        Assert.Equal(3, _logs);
    }

    [Fact]
    public void NoRepository_OrOneAboveTheSandbox_IsAnEmptyList()
    {
        Assert.Empty(List().For(_box.Root));
        GitAccessTests.Init(_box.Dir);
        GitAccessTests.CommitFile(_box.Dir, "top.cs", "t", "above");
        Assert.Empty(List().For(_box.Root));
        Assert.Equal(0, _logs);
    }

    [Fact]
    public void ASubject_IsOneCleanLine_CutToFit()
    {
        GitAccessTests.Init(_box.Root);
        GitAccessTests.CommitFile(_box.Root, "a.cs", "a", "line one\tand\u001b[31m " + new string('x', 200) + "\n\nthe body");
        string note = List().For(_box.Root)[0].Note;
        Assert.DoesNotContain('\n', note);
        Assert.DoesNotContain('\u001b', note);
        Assert.StartsWith("line one", note);
        Assert.True(UI.TextCells.Width(note) <= AuditCommitList.NoteCells);
    }
}
