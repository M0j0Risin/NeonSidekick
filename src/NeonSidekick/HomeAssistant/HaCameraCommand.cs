using NeonSidekick.Files;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// <c>/ha camera [&lt;name&gt;]</c> (2026-11-13, the user's ask: a camera's still opened the way a 📹 chip's double-click opens it, for any
/// camera, not only the bar's): the name is a chip's alias, an entity id, a friendly name or a room, resolved as <c>ha_camera</c> resolves
/// it (<see cref="Llm.Tools.HaCameraTool.ResolveAsync"/>); bare, the only camera. The screen takes it before <see cref="HaCommand.RunAsync"/>
/// and fetches through the chips' shared fetch; headless reaches <see cref="HaCommand.RunAsync"/>'s <c>camera</c> case, which saves the
/// still and prints its path (<see cref="HaCameraOptions"/>).
/// </summary>
public static class HaCameraCommand
{
    /// <summary>The <c>/ha</c> word. Pinned: a contract identifier.</summary>
    public const string Word = "camera";

    /// <summary>
    /// True when <paramref name="haArgs"/> (the text after <c>/ha</c>) starts with <see cref="Word"/>, in any case; <paramref name="target"/>
    /// is then the trimmed rest, or null when nothing follows the word — <see cref="Llm.Tools.HaTool.OneOf"/> reads a null target as "the
    /// only camera", where an empty one would be resolved as a name. Pure.
    /// </summary>
    public static bool TryParse(string haArgs, out string? target)
    {
        var (first, rest) = HaCommand.SplitVerb(haArgs);
        if (first != Word)
        {
            target = null;
            return false;
        }

        target = rest.Length == 0 ? null : rest;
        return true;
    }
}

/// <summary>
/// What headless's <c>/ha camera</c> needs (2026-11-13): the images folder the still is kept in, and the chips' alias lookup
/// (<c>HomeBarItems.AliasedCamera</c> over the profile's <i>Home bar aliases</i>), passed in so nothing under <c>HomeAssistant/</c>
/// reads the bar's reference format. The screen never passes one: it takes <c>/ha camera</c> before <see cref="HaCommand.RunAsync"/>.
/// </summary>
public sealed record HaCameraOptions(WorkingDirectory Images, Func<string?, string?>? AliasedId = null);
