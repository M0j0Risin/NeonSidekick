using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;
using NeonSidekick.Shopping;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The shopping list's tripwire (2026-10-17, the user's report: "add milk to my shopping list" now and then went to a file): the name rule, the
/// write tools refusing a list's name while the guard holds (a copy or move into a folder judged by the name the file will have), a delete
/// and <c>/shopping export</c>'s own file never refused, and the vault's <c>vault_write</c> and <c>vault_move</c> the same.
/// </summary>
public sealed class ShoppingFileGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly AppSettingsData _settings = new() { ObsidianTools = true };
    private readonly IReadOnlyList<AIFunction> _tools;
    private readonly IReadOnlyList<AIFunction> _vaultTools;
    private bool _guarded = true;

    public ShoppingFileGuardTests()
    {
        _files = new WorkingDirectory(() => Path.Combine(_dir, "files"), _time) { ShoppingListGuard = () => _guarded };
        _tools = ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings);
        string vault = Path.Combine(_dir, "vault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
        _settings.ObsidianVault = vault;
        _vaultTools = ChatScreen.ObsidianTools(new ObsidianVault(() => _settings.ObsidianVault, _time) { ShoppingListGuard = () => _guarded }, () => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("shopping-list.md")]
    [InlineData("Shopping List.txt")]
    [InlineData("grocery_list.json")]
    [InlineData("groceries.md")]
    [InlineData("Groceries 2026-10-17.md")]
    [InlineData("my-shopping-list-oct.md")]
    [InlineData("ShoppingList.md")]
    [InlineData("notes/shopping.md")]
    [InlineData("Lists\\Grocery List")]
    public void AListsName_IsCaught(string path) => Assert.True(ShoppingFileGuard.IsShoppingListName(path), path);

    [Theory]
    [InlineData("shopping-cart.cs")]
    [InlineData("ShoppingListener.ts")]
    [InlineData("notes.md")]
    [InlineData("list.md")]
    [InlineData("shopping-list/readme.md")]   // a folder of that name holds other files
    [InlineData("todo-list.md")]
    public void AnotherName_Passes(string path) => Assert.False(ShoppingFileGuard.IsShoppingListName(path), path);

    [Fact]
    public async Task WriteFile_OntoAListsName_IsRefused_WhileGuarded_AndWritten_Otherwise()
    {
        string refused = await Call(_tools, WriteFileTool.ToolName, ("path", "shopping-list.md"), ("content", "- milk\n"));
        Assert.Equal(ShoppingText.NotAFile("shopping-list.md"), refused);
        Assert.False(File.Exists(Path.Combine(_files.Root, "shopping-list.md")));

        Assert.DoesNotContain("Error", await Call(_tools, WriteFileTool.ToolName, ("path", "notes.md"), ("content", "hello\n")), StringComparison.Ordinal);

        _guarded = false;
        await Call(_tools, WriteFileTool.ToolName, ("path", "shopping-list.md"), ("content", "- milk\n"));
        Assert.True(File.Exists(Path.Combine(_files.Root, "shopping-list.md")));
    }

    [Fact]
    public async Task ACopyOrMove_IsJudgedByTheNameItLandsAs_AndADeleteIsNever()
    {
        _guarded = false;
        await Call(_tools, WriteFileTool.ToolName, ("path", "groceries.md"), ("content", "- eggs\n"));
        await Call(_tools, WriteFileTool.ToolName, ("path", "notes.md"), ("content", "hello\n"));
        Directory.CreateDirectory(Path.Combine(_files.Root, "old"));
        _guarded = true;

        Assert.Equal(ShoppingText.NotAFile("shopping-list.md"), await Call(_tools, CopyTool.ToolName, ("from", "notes.md"), ("to", "shopping-list.md")));
        Assert.StartsWith("Error: the user's shopping list", await Call(_tools, MoveTool.ToolName, ("from", "groceries.md"), ("to", "old")), StringComparison.Ordinal);   // into a folder: it keeps its name
        Assert.DoesNotContain("shopping list", await Call(_tools, MoveTool.ToolName, ("from", "groceries.md"), ("to", "old-list.md")), StringComparison.Ordinal);   // a move away from such a name
        Assert.False(File.Exists(Path.Combine(_files.Root, "groceries.md")));

        _guarded = false;
        await Call(_tools, WriteFileTool.ToolName, ("path", "shopping-list.md"), ("content", "- milk\n"));
        _guarded = true;
        await Call(_tools, DeleteTool.ToolName, ("path", "shopping-list.md"));
        Assert.False(File.Exists(Path.Combine(_files.Root, "shopping-list.md")));   // tidying an old one is fine
    }

    [Fact]
    public async Task TheVaultsWriteAndMove_RefuseAListsName()
    {
        Assert.Equal(ShoppingText.NotAFile("Shopping List"), await Call(_vaultTools, VaultWriteTool.ToolName, ("note", "Shopping List"), ("content", "- milk")));
        Assert.DoesNotContain("shopping list", await Call(_vaultTools, VaultWriteTool.ToolName, ("note", "Recipes"), ("content", "soup")), StringComparison.Ordinal);
        Assert.StartsWith("Error: the user's shopping list", await Call(_vaultTools, VaultMoveTool.ToolName, ("note", "Recipes"), ("to", "Groceries")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShoppingExport_StillWritesItsOwnFile_WhileGuarded()
    {
        using var holder = new ShoppingStoreHolder(() => Path.Combine(_dir, "profile"), _time);
        var list = new ShoppingList(holder.Current);
        await ShoppingCommandRunner.RunAsync(list, ShoppingCommand.Parse("add milk"), _files, null, null, CancellationToken.None, time: _time);
        var said = await ShoppingCommandRunner.RunAsync(list, ShoppingCommand.Parse("export"), _files, null, null, CancellationToken.None, time: _time);

        Assert.DoesNotContain(said, line => line.StartsWith("Error", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(_files.Root, "shopping-list-*.md"));
    }

    private static async Task<string> Call(IReadOnlyList<AIFunction> tools, string name, params (string Name, object? Value)[] values)
    {
        var arguments = new AIFunctionArguments(values.ToDictionary(v => v.Name, v => v.Value));
        return ToolAnswers.Text(await tools.Single(t => t.Name == name).InvokeAsync(arguments, CancellationToken.None));
    }
}
