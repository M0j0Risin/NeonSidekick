namespace NeonSidekick.App;

/// <summary>
/// The glyph a transcript notice wears ahead of its words (2026-09-22, the user's picks, made on
/// the catalogue of system messages): inside the parentheses when the line has them —
/// <c>(💾 remembered: …)</c> — else straight after the prefix — <c>  · 💾 Memory is off; …</c>. Each
/// carries its one trailing space. The areas that already had a glyph keep theirs (the toolbar's,
/// <see cref="ChatScreen.TrashGlyph"/>, <see cref="ChatScreen.LearnGlyph"/>, compaction's); these
/// reuse the toolbar and strip glyphs where the area has one, so a pane's notice matches its door.
/// A surrogate pair is two cells to <see cref="UI.TextCells"/>, as is a BMP glyph it lists as wide
/// (⏳ ✋); a pictograph that defaults to text presentation carries U+FE0F (🖥️ 🗣️), which the
/// terminal draws two cells wide too.
/// </summary>
public static class NoticeGlyphs
{
    /// <summary>The LLM server: the <c>LLM:</c> connected lines, the no-server hints, a cancelled connect.</summary>
    public const string Llm = "🖥️ ";

    /// <summary>Speech output (the strip's speaker): the mid-turn switch, a stopped reading, the TTS voice list.</summary>
    public const string Tts = ChatScreen.TtsGlyph + " ";

    /// <summary>Voice input (the strip's microphone): the mid-turn switch, heard nothing, discarded, voice off.</summary>
    public const string Stt = ChatScreen.SttGlyph + " ";

    /// <summary>The wake word (the strip's ear).</summary>
    public const string Wake = ChatScreen.WakeGlyph + " ";

    /// <summary>The interrupt (the strip's hand).</summary>
    public const string Interrupt = ChatScreen.InterruptGlyph + " ";

    /// <summary>The MCP pane and an MCP connect cancelled (the toolbar's plug).</summary>
    public const string Mcp = NeonSidekick.Mcp.McpText.Glyph + " ";

    /// <summary>A shell command allowed on the approval pane (the toolbar's open lock).</summary>
    public const string Allowed = ChatScreen.CmdYoloToolGlyph + " ";

    /// <summary>The message queue.</summary>
    public const string Queue = "⏳ ";

    /// <summary>Profiles, and <c>persona.md</c>.</summary>
    public const string Profile = ChatScreen.ProfileToolGlyph + " ";   // the toolbar's ID card since later on 2026-09-29, one source

    /// <summary>The performance bar (<c>/perf</c>, later on 2026-09-29).</summary>
    public const string Perf = ChatScreen.PerfToolGlyph + " ";

    /// <summary><c>operata.md</c>, the operating rules.</summary>
    public const string Operata = "📋 ";

    /// <summary><c>vocalia.md</c>, the spoken-reply directive.</summary>
    public const string Vocalia = "🗣️ ";

    /// <summary>Memory (the toolbar's disk).</summary>
    public const string Memory = ChatScreen.MemoryToolGlyph + " ";

    /// <summary>Timers (the hint row's clock).</summary>
    public const string Timer = NeonSidekick.Timers.TimerText.StatusGlyph + " ";

    /// <summary>The working directory: <c>/cwd</c>, <c>/explore</c>.</summary>
    public const string Folder = "📂 ";

    /// <summary><c>/gituser</c>'s written identity (the toolbar's tools).</summary>
    public const string Git = ChatScreen.ToolsToolGlyph + " ";

    /// <summary>The terminal window's size.</summary>
    public const string Window = "🖥️ ";

    /// <summary>Sessions (the toolbar's balloon).</summary>
    public const string Session = ChatScreen.SessionsToolGlyph + " ";

    /// <summary>Skills (the toolbar's mortarboard).</summary>
    public const string Skill = ChatScreen.SkillsToolGlyph + " ";

    /// <summary>The <c>--log</c> file: <c>/log</c> (2026-09-22).</summary>
    public const string Log = "📄 ";

    /// <summary><c>/rewind</c> (2026-09-30): the picker's title, the rewound line, nothing to rewind. A BMP glyph <see cref="UI.TextCells"/> lists as wide.</summary>
    public const string Rewind = "⏪ ";

    /// <summary>The glyph of a prompt file's notices: 🪪 persona.md, 📋 operata.md, 🗣️ vocalia.md; nothing for another name.</summary>
    public static string PromptFile(string fileName) => fileName switch
    {
        NeonSidekick.Llm.PersonaFile.FileName => Profile,
        NeonSidekick.Llm.OperataFile.FileName => Operata,
        NeonSidekick.Llm.VocaliaFile.FileName => Vocalia,
        _ => "",
    };
}
