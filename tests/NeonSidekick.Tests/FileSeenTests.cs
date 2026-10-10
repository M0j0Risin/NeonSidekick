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
}
