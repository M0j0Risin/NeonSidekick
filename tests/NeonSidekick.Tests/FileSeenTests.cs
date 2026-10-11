using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>write_file</c> without a mode on a file the model has seen (2026-10-10, the user's call: a run retried twice, each a whole request,
/// for files it had just read or written): read, written or patched this session and unchanged since, it is replaced; a file never
/// seen, one changed since, an explicit <c>create</c>, or a forgotten session still refuse.
/// </summary>
public sealed class FileSeenTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("neon-seen-").FullName;
    private readonly WorkingDirectory _files;
    private readonly IReadOnlyList<AIFunction> _tools;

    public FileSeenTests()
    {
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
        _tools = ChatScreen.FileTools(_files, () => true, _ => { }, () => new AppSettingsData());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private T Tool<T>() where T : AIFunction => _tools.OfType<T>().Single();

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] values) where T : AIFunction =>
        ToolAnswers.Text(await Tool<T>().InvokeAsync(new AIFunctionArguments(values.ToDictionary(v => v.Name, v => v.Value)), CancellationToken.None));

    private string Text(string name) => File.ReadAllText(Path.Combine(_root, name));

    [Fact]
    public async Task AFileTheModelRead_IsReplacedWithoutAMode()
    {
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "Console.WriteLine(1);\n");
        Assert.StartsWith("Error: 'Program.cs' already exists", await Invoke<WriteFileTool>(("path", "Program.cs"), ("content", "x")));   // never seen

        await Invoke<ReadFileTool>(("path", "Program.cs"));
        Assert.StartsWith("replaced Program.cs", await Invoke<WriteFileTool>(("path", "Program.cs"), ("content", "ApiProbe.Run();\n")));
        Assert.Equal("ApiProbe.Run();\n", Text("Program.cs"));
    }

    [Fact]
    public async Task AFileTheModelWrote_OrPatched_IsReplacedWithoutAMode()
    {
        Assert.StartsWith("wrote ApiProbe.cs", await Invoke<WriteFileTool>(("path", "ApiProbe.cs"), ("content", "one\n")));
        Assert.StartsWith("replaced ApiProbe.cs", await Invoke<WriteFileTool>(("path", "ApiProbe.cs"), ("content", "two\n")));

        await Invoke<PatchFileTool>(("path", "ApiProbe.cs"), ("old_text", "two"), ("new_text", "three"));
        Assert.StartsWith("replaced ApiProbe.cs", await Invoke<WriteFileTool>(("path", "ApiProbe.cs"), ("content", "four\n")));
        Assert.Equal("four\n", Text("ApiProbe.cs"));
    }

    [Fact]
    public async Task AFileChangedSince_AnExplicitCreate_AndAForgottenSession_StillRefuse()
    {
        string full = Path.Combine(_root, "notes.txt");
        File.WriteAllText(full, "mine\n");
        await Invoke<ReadFileTool>(("path", "notes.txt"));

        Assert.StartsWith("Error: 'notes.txt' already exists", await Invoke<WriteFileTool>(("path", "notes.txt"), ("content", "x"), ("mode", "create")));   // asked for create: never replaces

        File.WriteAllText(full, "the user's edit, longer\n");   // changed since it was read
        Assert.StartsWith("Error: 'notes.txt' already exists", await Invoke<WriteFileTool>(("path", "notes.txt"), ("content", "x")));
        Assert.Equal("the user's edit, longer\n", Text("notes.txt"));

        await Invoke<ReadFileTool>(("path", "notes.txt"));
        Assert.True(_files.WasSeen("NOTES.txt") || !OperatingSystem.IsWindows());   // case-blind on Windows
        _files.ForgetSeen();
        Assert.False(_files.WasSeen("notes.txt"));
        Assert.StartsWith("Error: 'notes.txt' already exists", await Invoke<WriteFileTool>(("path", "notes.txt"), ("content", "x")));
        Assert.False(_files.WasSeen("missing.txt"));
    }

    /// <summary>The review of 2026-10-11: a window of a file, a read cut at the cap, or an append says nothing of the rest, so none marks it seen.</summary>
    [Fact]
    public async Task APartialRead_ACutRead_AndAnAppend_MarkNothing()
    {
        File.WriteAllText(Path.Combine(_root, "big.cs"), string.Join('\n', Enumerable.Range(1, 40).Select(i => "line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "\n");
        await Invoke<ReadFileTool>(("path", "big.cs"), ("max_lines", 20));
        Assert.False(_files.WasSeen("big.cs"));
        await Invoke<ReadFileTool>(("path", "big.cs"), ("start_line", 21));
        Assert.False(_files.WasSeen("big.cs"));   // two halves are not one whole read
        Assert.StartsWith("Error: 'big.cs' already exists", await Invoke<WriteFileTool>(("path", "big.cs"), ("content", "line 1\n")));

        File.WriteAllText(Path.Combine(_root, "long.txt"), new string('x', WorkingDirectory.MaxReadChars + 10));   // one line past the cap
        await Invoke<ReadFileTool>(("path", "long.txt"));
        Assert.False(_files.WasSeen("long.txt"));

        File.WriteAllText(Path.Combine(_root, "journal.md"), "the user's entries\n");
        await Invoke<WriteFileTool>(("path", "journal.md"), ("content", "a new line"), ("mode", "append"));
        Assert.False(_files.WasSeen("journal.md"));
        Assert.StartsWith("Error: 'journal.md' already exists", await Invoke<WriteFileTool>(("path", "journal.md"), ("content", "x")));
        Assert.StartsWith("the user's entries", Text("journal.md"));

        await Invoke<ReadFileTool>(("path", "big.cs"));
        Assert.True(_files.WasSeen("big.cs"));   // the whole file at once
    }

    /// <summary>The review of 2026-10-11: under <see cref="WorkingDirectory.SeenOff"/> (an advisor's turn, a bot's reply) a read marks nothing and a mark already there is not found.</summary>
    [Fact]
    public async Task UnderSeenOff_ReadsMarkNothing_AndMarksAreNotFound()
    {
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "Console.WriteLine(1);\n");
        File.WriteAllText(Path.Combine(_root, "seen.cs"), "x\n");
        await Invoke<ReadFileTool>(("path", "seen.cs"));
        await Task.Run(async () =>
        {
            using var off = WorkingDirectory.SeenOff();
            await Invoke<ReadFileTool>(("path", "Program.cs"));
            Assert.False(_files.WasSeen("seen.cs"));
            Assert.StartsWith("Error: 'seen.cs' already exists", await Invoke<WriteFileTool>(("path", "seen.cs"), ("content", "y")));
        });

        Assert.False(_files.WasSeen("Program.cs"));   // the advisor's read is not the main model's
        Assert.StartsWith("Error: 'Program.cs' already exists", await Invoke<WriteFileTool>(("path", "Program.cs"), ("content", "x")));
        Assert.True(_files.WasSeen("seen.cs"));   // the flow outside never had it off
    }
}
