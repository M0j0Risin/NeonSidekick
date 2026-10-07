using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Shell;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The screen fixture's Unix helpers (2026-10-06, the macOS build).</summary>
public partial class ChatScreenTests
{
    /// <summary>
    /// A blank cell of the 240-wide toolbar for a twin's "the blanks open /settings" click: halfway between the widest strip of
    /// glyphs (every item, the policy's lock and the police) and the working directory, which the toolbar puts flush right. The
    /// tests click 120 on Windows; a macOS temp path (<c>/var/folders/…/T/NeonSidekick.Tests/…/profiles/default/files</c>) reaches
    /// past it, and a click there opens the folders. Fails, rather than clicking a glyph or the path, should no blank be left.
    /// </summary>
    private int ToolbarBlank()
    {
        int stripEnd = new[] { ChatScreen.ToolbarStrip, ChatScreen.ToolbarStripFor(CommandPolicyMode.Ask, true), ChatScreen.ToolbarStripFor(CommandPolicyMode.Yolo, true) }.Max(TextCells.Width);
        int pathStart = 239 - TextCells.Width(WorkingDirectory.Resolve("", _settings.ProfileDirectory));
        int blank = (stripEnd + pathStart) / 2;
        Assert.True(blank > stripEnd + 1 && blank < pathStart - 1, $"no blank between the glyphs (to {stripEnd}) and the working directory (from {pathStart})");
        return blank;
    }
}
