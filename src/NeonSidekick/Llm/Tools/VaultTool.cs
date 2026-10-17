using Microsoft.Extensions.AI;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the eight Obsidian vault tools share (2026-09-22): the <see cref="ObsidianVault"/> door, the settings in
/// force at each call, the <c>note</c> property most of them take, and the act run off the turn's thread (a
/// walk of a large vault is disk work; the <see cref="GitTool"/> shape). Every refusal is an
/// <see cref="ObsidianText"/> sentence starting <c>Error:</c>.
/// </summary>
public abstract class VaultTool : AIFunction
{
    public const string NoteArgument = "note";

    /// <summary>The <c>note</c> property of every schema that names one.</summary>
    public const string NoteProperty = "\"note\": { \"type\": \"string\", \"description\": \"" + ObsidianText.NoteArgument + "\" }";

    private readonly Func<AppSettingsData> _effective;

    protected VaultTool(ObsidianVault vault, Func<AppSettingsData> effective)
    {
        Vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    protected ObsidianVault Vault { get; }

    protected AppSettingsData Effective => _effective();

    protected static string ReadNote(AIFunctionArguments arguments) => ToolArguments.ReadString(arguments, NoteArgument);

    /// <summary>
    /// The shopping list's tripwire for a note written or moved to <paramref name="note"/> (2026-10-17, the user's report; the file tools'
    /// <c>FileTool.ShoppingListRefusal</c>): <see cref="Shopping.ShoppingText.NotAFile"/> while the vault's
    /// <see cref="ObsidianVault.ShoppingListGuard"/> holds and the name is a list's, else null.
    /// </summary>
    protected string? ShoppingListRefusal(string note) =>
        Vault.ShoppingListGuard?.Invoke() == true && Shopping.ShoppingFileGuard.IsShoppingListName(note) ? Shopping.ShoppingText.NotAFile(note) : null;

    /// <summary>The act off the caller's thread: the vault is read and written synchronously, and the turn loop is the UI's.</summary>
    protected static async ValueTask<object?> OffThread(Func<string> act, CancellationToken cancellationToken) =>
        await Task.Run(act, cancellationToken).ConfigureAwait(false);

    /// <summary>An optional whole-number argument, or the refusal (<see cref="ClockText.BadInteger"/>).</summary>
    protected static bool TryReadInt(AIFunctionArguments arguments, string name, out int? value, out string error)
    {
        if (ToolArguments.TryReadInt32(arguments, name, out value, out string raw))
        {
            error = "";
            return true;
        }

        error = ClockText.BadInteger(name, raw);
        return false;
    }
}
