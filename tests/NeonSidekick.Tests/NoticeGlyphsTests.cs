using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class NoticeGlyphsTests
{
    /// <summary>The user's picks (2026-09-22): every glyph two cells and one space after it.</summary>
    [Fact]
    public void Glyphs_ArePinned_TwoCellsAndASpace()
    {
        var glyphs = new Dictionary<string, string>
        {
            ["🖥️ "] = NoticeGlyphs.Llm, ["🔊 "] = NoticeGlyphs.Tts, ["🎤 "] = NoticeGlyphs.Stt, ["👂 "] = NoticeGlyphs.Wake,
            ["✋ "] = NoticeGlyphs.Interrupt, ["🔌 "] = NoticeGlyphs.Mcp, ["🔓 "] = NoticeGlyphs.Allowed, ["⏳ "] = NoticeGlyphs.Queue,
            ["🪪 "] = NoticeGlyphs.Profile, ["📋 "] = NoticeGlyphs.Operata, ["🗣️ "] = NoticeGlyphs.Vocalia, ["💾 "] = NoticeGlyphs.Memory,
            ["⏰ "] = NoticeGlyphs.Timer, ["📂 "] = NoticeGlyphs.Folder, ["🛠️ "] = NoticeGlyphs.Git, ["💬 "] = NoticeGlyphs.Session,
            ["🎓 "] = NoticeGlyphs.Skill, ["📈 "] = NoticeGlyphs.Perf,
        };
        foreach (var (expected, actual) in glyphs)
        {
            Assert.Equal(expected, actual);
            Assert.Equal(3, TextCells.Width(actual));
        }

        Assert.Equal(NoticeGlyphs.Llm, NoticeGlyphs.Window);
    }

    [Fact]
    public void PromptFile_PicksTheFilesGlyph()
    {
        Assert.Equal("🪪 ", NoticeGlyphs.PromptFile("persona.md"));
        Assert.Equal("📋 ", NoticeGlyphs.PromptFile("operata.md"));
        Assert.Equal("🗣️ ", NoticeGlyphs.PromptFile("vocalia.md"));
        Assert.Equal("", NoticeGlyphs.PromptFile("other.md"));
    }

    [Fact]
    public void ConnectCancelled_WearsTheConnectsGlyph()
    {
        Assert.Equal("(🖥️ cancelled)", ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm));
        Assert.Equal("(🔌 cancelled)", ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Mcp));
    }
}
