using NeonSidekick.App;
using NeonSidekick.Files;

namespace NeonSidekick.Tests;

public sealed class TreeTextTests
{
    private static FileTreeResult Ok(params FileTreeEntry[] entries) =>
        new(FileOutcome.Ok, "", @"D:\home\profiles\default\files\", entries, Truncated: false);

    private static readonly FileTreeEntry[] Sample =
    [
        new("docs", 1, true, 0, false),
        new("a.md", 2, false, 1_200, false),
        new("b.md", 2, false, 340, true),
        new("src", 1, true, 0, false),
        new("deep", 2, true, 0, true),
        new("x [1].cs", 3, false, 0, true),
        new("notes.txt", 1, false, 12, true),
    ];

    [Fact]
    public void Lines_DrawTheBranches_WithSizes()
    {
        Assert.Equal(
            [
                @"D:\home\profiles\default\files\",
                "├── docs\\",
                "│   ├── a.md  1.2 KB",
                "│   └── b.md  340 B",
                "├── src\\",
                "│   └── deep\\",
                "│       └── x [1].cs  0 B",
                "└── notes.txt  12 B",
            ],
            TreeText.Lines(Ok(Sample), showSizes: true, cap: 500));
    }

    [Fact]
    public void Lines_WithoutSizes_AreTheNamesAlone()
    {
        Assert.Equal(
            [
                @"D:\home\profiles\default\files\",
                "├── docs\\",
                "│   ├── a.md",
                "│   └── b.md",
                "├── src\\",
                "│   └── deep\\",
                "│       └── x [1].cs",
                "└── notes.txt",
            ],
            TreeText.Lines(Ok(Sample), showSizes: false, cap: 500));
    }

    [Fact]
    public void Lines_EmptyFolder_AndTheCutTail()
    {
        Assert.Equal([@"D:\home\profiles\default\files\", "(empty)"], TreeText.Lines(Ok(), showSizes: true, cap: 500));

        var cut = new FileTreeResult(FileOutcome.Ok, "", @"D:\x\", [new FileTreeEntry("a.txt", 1, false, 1, false)], Truncated: true);
        Assert.Equal([@"D:\x\", "├── a.txt  1 B", "… only the first 5 entries are shown (File /tree max length)"], TreeText.Lines(cut, showSizes: true, cap: 5));
        Assert.Equal("… only the first 500 entries are shown (File /tree max length)", TreeText.CutLine(500));
    }

    [Fact]
    public void Error_IsTheFileSentence()
    {
        Assert.Equal(FileText.Missing(@"nope\"), TreeText.Error(new FileTreeResult(FileOutcome.Missing, @"nope\", "", [], false)));
        Assert.Equal(FileText.OutsideRoot(".."), TreeText.Error(new FileTreeResult(FileOutcome.OutsideRoot, "..", "", [], false)));
        Assert.Equal(FileText.IsAFile("a.txt"), TreeText.Error(new FileTreeResult(FileOutcome.IsAFile, "a.txt", "", [], false)));
        Assert.Equal(FileText.CouldNot("list", @"docs\", "boom"), TreeText.Error(new FileTreeResult(FileOutcome.Failed, @"docs\", "", [], false, "boom")));
    }
}
