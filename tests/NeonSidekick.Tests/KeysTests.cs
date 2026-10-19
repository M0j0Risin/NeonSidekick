using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class KeysTests
{
    [Fact]
    public void IsCancel_IsEscape()
    {
        Assert.True(Keys.IsCancel(Keys.Escape));
        Assert.False(Keys.IsCancel(Keys.Ctrl(ConsoleKey.Q)));
        Assert.False(Keys.IsCancel(Keys.Char('e')));
    }

    [Fact]
    public void IsInterrupt_IsCtrlC_WithoutAlt()
    {
        // The console's own shape (ETX with the key) and the test factory's ('\0') both count;
        // Shift is ignored, Alt (AltGr+C) is not it, and no other key is.
        Assert.True(Keys.IsInterrupt(Keys.CtrlC));
        Assert.True(Keys.IsInterrupt(Keys.Ctrl(ConsoleKey.C)));
        Assert.True(Keys.IsInterrupt(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: true, alt: false, control: true)));
        Assert.False(Keys.IsInterrupt(new ConsoleKeyInfo('\0', ConsoleKey.C, shift: false, alt: true, control: true)));
        Assert.False(Keys.IsInterrupt(Keys.Escape));
        Assert.False(Keys.IsInterrupt(Keys.Ctrl(ConsoleKey.Q)));
        Assert.False(Keys.IsInterrupt(Keys.Char('c')));
        // Spectre's TestConsoleInput.PushText marks an upper-case letter with Control: a typed "C" is a character, never the chord.
        Assert.False(Keys.IsInterrupt(new ConsoleKeyInfo('C', ConsoleKey.C, shift: false, alt: false, control: true)));
        Assert.False(Keys.IsCancel(Keys.CtrlC));
    }

    [Fact]
    public void IsToolToggle_IsCtrlO_WithoutAlt_AndATypedOStaysAnO()
    {
        // 2026-09-22: the console's SI and the test factory's '\0' count; Alt, a typed "O" (upper case marked with Control by Spectre's test input) and "o" do not.
        Assert.True(Keys.IsToolToggle(Keys.CtrlO));
        Assert.True(Keys.IsToolToggle(Keys.Ctrl(ConsoleKey.O)));
        Assert.Equal('\x0f', Keys.CtrlO.KeyChar);
        Assert.False(Keys.IsToolToggle(new ConsoleKeyInfo('\0', ConsoleKey.O, shift: false, alt: true, control: true)));
        Assert.False(Keys.IsToolToggle(new ConsoleKeyInfo('O', ConsoleKey.O, shift: false, alt: false, control: true)));
        Assert.False(Keys.IsToolToggle(Keys.Char('o')));
        Assert.False(Keys.IsToolToggle(Keys.CtrlC));
    }

    [Fact]
    public void ShortcutLine_IsCtrlAltCNP_WithoutShift_AndAnAltGrCharacterStaysACharacter()
    {
        // 2026-09-30 (the user's ask): the test factory's '\0' and the console's control characters count. /splash was
        // Ctrl+Alt+S until later still on 2026-10-01 (the user's ask), Ctrl+Alt+P since, Ctrl+Alt+Z since 2026-10-18 (the user's pick).
        Assert.Equal("/clear", Keys.ShortcutLine(Keys.CtrlAltC));
        Assert.Equal("/new", Keys.ShortcutLine(Keys.CtrlAltN));
        Assert.Equal("/splash", Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.Z)));
        Assert.Equal("/clear", Keys.ShortcutLine(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: true, control: true)));
        Assert.Equal("/new", Keys.ShortcutLine(new ConsoleKeyInfo('\x0e', ConsoleKey.N, shift: false, alt: true, control: true)));   // Alt held tells it from Ctrl+N's /rename
        Assert.Equal("/splash", Keys.ShortcutLine(new ConsoleKeyInfo('\x1a', ConsoleKey.Z, shift: false, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('ż', ConsoleKey.Z, shift: false, alt: true, control: true)));      // AltGr+Z on a Polish layout

        Assert.Null(Keys.ShortcutLine(Keys.CtrlC));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: true, alt: false, control: true)));   // Ctrl+Shift+C: Windows Terminal's copy
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.N, shift: true, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('ć', ConsoleKey.C, shift: false, alt: true, control: true)));      // AltGr+C on a Polish layout
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('S', ConsoleKey.S, shift: false, alt: false, control: true)));      // a typed "S" in Spectre's test input
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.X, shift: false, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.C, shift: false, alt: true, control: false)));   // Alt+C alone
        Assert.False(Keys.IsInterrupt(Keys.CtrlAltC));
    }

    [Theory]
    [InlineData(ConsoleKey.T, '\x14', "/tools shell allowed")]   // 2026-10-18, the user's pick (Ctrl+Alt+L until then; /tools before)
    [InlineData(ConsoleKey.S, '\x13', "/skills")]   // from Ctrl+Alt+K later still on 2026-10-01 (the user's ask)
    [InlineData(ConsoleKey.M, '\r', "/memory")]
    [InlineData(ConsoleKey.D, '\x04', "/mcp")]
    [InlineData(ConsoleKey.O, '\x0f', "/tools shell police")]    // /police until 2026-10-09
    [InlineData(ConsoleKey.P, '\x10', "/prompts")]   // 2026-10-18, the user's pick (Ctrl+Alt+W until then)
    [InlineData(ConsoleKey.Y, '\x19', "/sessions")]   // 2026-10-18, the user's pick (Ctrl+Alt+E until then)
    [InlineData(ConsoleKey.Z, '\x1a', "/splash")]   // 2026-10-18, the user's pick (Ctrl+Alt+P until then)
    public void ShortcutLine_ThePaneChords_AreTheirBareCommands(ConsoleKey key, char control, string line)
    {
        // Later on 2026-09-30 (the user's ask): the test factory's '\0' and the console's control character count; Shift,
        // Ctrl alone, Alt alone and an AltGr key that types a character are no chord.
        Assert.Equal(line, Keys.ShortcutLine(Keys.CtrlAlt(key)));
        Assert.Equal(line, Keys.ShortcutLine(new ConsoleKeyInfo(control, key, shift: false, alt: true, control: true)));
        Assert.Contains(line.Split(' ')[0], NeonSidekick.App.SlashCommands.Words);   // the command's word: /tools shell allowed|police since 2026-10-09
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: true, alt: true, control: true)));
        Assert.NotEqual(line, Keys.ShortcutLine(new ConsoleKeyInfo(control, key, shift: false, alt: false, control: true)));   // Ctrl+E alone is /explore, Ctrl+M /model
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: false, alt: true, control: false)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('€', key, shift: false, alt: true, control: true)));   // AltGr+E on a German layout, and its like
    }

    [Fact]
    public void ShortcutLine_CtrlE_IsExplore_WithoutAltOrShift_AndATypedEStaysAnE()
    {
        // Later on 2026-10-01 (the user's ask): the console's ENQ and the test factory's '\0' count; Ctrl+Alt+E was /perfbar (gone since Ctrl+F; /sessions from 2026-10-03, free since 2026-10-18),
        // Shift, Alt alone and a typed "E" (Spectre's test input marks it with Control) are no chord.
        Assert.Equal("/explore", Keys.ShortcutLine(Keys.CtrlE));
        Assert.Equal("/explore", Keys.ShortcutLine(Keys.Ctrl(ConsoleKey.E)));
        Assert.Contains("/explore", NeonSidekick.App.SlashCommands.Words);
        Assert.Null(Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.E)));   // /sessions from later on 2026-10-03 until 2026-10-18 (Ctrl+Alt+Y since)
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\x05', ConsoleKey.E, shift: true, alt: false, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('E', ConsoleKey.E, shift: false, alt: false, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.E, shift: false, alt: true, control: false)));
        Assert.Null(Keys.ShortcutLine(Keys.Char('e')));
        Assert.Null(Keys.ShortcutLine(Keys.Ctrl(ConsoleKey.O)));   // the other plain-Ctrl keys stay theirs
        Assert.False(Keys.IsToolToggle(Keys.CtrlE));
        Assert.False(Keys.IsInterrupt(Keys.CtrlE));
    }

    [Theory]
    [InlineData(ConsoleKey.B, '\x02', "/browser")]     // 2026-10-18, the user's pick (free until then)
    [InlineData(ConsoleKey.F, '\x06', "/find")]        // 2026-10-18, the user's pick (Ctrl+Shift+F until then; /perfbar before)
    [InlineData(ConsoleKey.G, '\x07', "/log")]         // from Ctrl+Alt+G on 2026-10-12 (the user's ask)
    [InlineData(ConsoleKey.H, '\x08', "/help")]   // moved from Ctrl+Alt+H later still on 2026-10-01 (the user's ask)
    [InlineData(ConsoleKey.M, '\r', "/model")]
    [InlineData(ConsoleKey.N, '\x0e', "/rename")]    // 2026-10-18, the user's pick (Ctrl+Alt+R until then)
    [InlineData(ConsoleKey.P, '\x10', "/profile")]   // from Ctrl+Alt+P
    [InlineData(ConsoleKey.Q, '\x11', "/queue")]     // 2026-10-05 (the user's ask); the key quit once
    [InlineData(ConsoleKey.R, '\x12', "/reasoning")]
    [InlineData(ConsoleKey.S, '\x13', "/server")]
    [InlineData(ConsoleKey.T, '\x14', "/tools")]     // 2026-10-18, the user's pick (Ctrl+Alt+T until then; /toolbar before)
    [InlineData(ConsoleKey.U, '\x15', "/usage")]     // from Ctrl+Alt+G
    [InlineData(ConsoleKey.W, '\x17', "/webui browser")]   // 2026-10-18, the user's pick (/homebar until then)
    [InlineData(ConsoleKey.Y, '\x19', "/sys")]       // from Ctrl+Alt+Y on 2026-10-03 (the user's ask)
    [InlineData(ConsoleKey.Z, '\x1a', "/theme")]     // 2026-10-04 (the user's ask)
    public void ShortcutLine_ThePlainCtrlChords_AreTheirBareCommands_AndATypedLetterStaysALetter(ConsoleKey key, char control, string line)
    {
        // Later still on 2026-10-01 (the user's ask): Ctrl+M, R and S, then H, P and U, Ctrl+E's shape — the console's CR, DC2 and DC3 and the
        // test factory's '\0' count; Shift, Alt alone and a typed upper-case letter (Spectre marks it with Control) are no chord.
        Assert.Equal(line, Keys.ShortcutLine(new ConsoleKeyInfo(control, key, shift: false, alt: false, control: true)));
        Assert.Equal(line, Keys.ShortcutLine(Keys.Ctrl(key)));
        Assert.Contains(line.Split(' ')[0], NeonSidekick.App.SlashCommands.Words);
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo(control, key, shift: true, alt: false, control: true)));   // Ctrl+Shift+F was /find until 2026-10-18; the Ctrl+Shift arm went
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: false, alt: true, control: false)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo(key.ToString()[0], key, shift: false, alt: false, control: true)));
        Assert.Null(Keys.ShortcutLine(Keys.Char(char.ToLowerInvariant(key.ToString()[0]))));
        Assert.False(Keys.IsInterrupt(new ConsoleKeyInfo(control, key, shift: false, alt: false, control: true)));
        Assert.False(Keys.IsToolToggle(new ConsoleKeyInfo(control, key, shift: false, alt: false, control: true)));
    }

    /// <summary>
    /// The first bare F-key chords (2026-10-05, the user's pick): F9 <c>/camera snap</c>, F10 <c>/screen</c>, no modifier held;
    /// Shift, Ctrl, Alt and Ctrl+Alt with them are no chord, nor the other F-keys (F4 is push-to-talk's default).
    /// </summary>
    [Theory]
    [InlineData(ConsoleKey.F9, "/camera snap")]
    [InlineData(ConsoleKey.F10, "/screen")]
    public void ShortcutLine_F9AndF10_AreSnapAndScreen_WithNoModifier(ConsoleKey key, string line)
    {
        Assert.Equal(line, Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: false)));
        Assert.Contains(line.Split(' ')[0], NeonSidekick.App.SlashCommands.Words);
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: true, alt: false, control: false)));   // Shift+F10 is the viewers' menu key
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: false, alt: true, control: false)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: false, alt: true, control: true)));
        Assert.False(NeonSidekick.App.SettingsMenu.IsPushToTalkCandidate(key));   // off push-to-talk's list the same day
    }

    [Theory]
    [InlineData(ConsoleKey.F1)]
    [InlineData(ConsoleKey.F4)]
    [InlineData(ConsoleKey.F8)]
    [InlineData(ConsoleKey.F11)]
    [InlineData(ConsoleKey.F12)]
    public void ShortcutLine_TheOtherFKeys_AreNoChord(ConsoleKey key) =>
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: false)));

    /// <summary>Ctrl+Alt+Q (2026-10-05, the user's ask): <c>/queue clear</c>, the console's DC1 and the factory's '\0'; AltGr+Q (a German layout's @) types its character.</summary>
    [Fact]
    public void ShortcutLine_CtrlAltQ_ClearsTheQueue_AndAltGrQStaysAnAt()
    {
        Assert.Equal("/queue clear", Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.Q)));
        Assert.Equal("/queue clear", Keys.ShortcutLine(new ConsoleKeyInfo('\x11', ConsoleKey.Q, shift: false, alt: true, control: true)));
        Assert.Equal(NeonSidekick.App.QueueAction.Clear, NeonSidekick.App.ChatScreen.ParseQueueArgs("clear"));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('@', ConsoleKey.Q, shift: false, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.Q, shift: true, alt: true, control: true)));
    }

    [Fact]
    public void ShortcutLine_ATypedCharacter_IsNoChord() =>
        Assert.Null(Keys.ShortcutLine(Keys.Char('a')));

    [Fact]
    public void ThePickerChords_Factories_AreTheConsoleShapes_AndTheCtrlAltLettersKeepTheirCommands()
    {
        Assert.Equal("/model", Keys.ShortcutLine(Keys.CtrlM));
        Assert.Equal("/reasoning", Keys.ShortcutLine(Keys.CtrlR));
        Assert.Equal("/server", Keys.ShortcutLine(Keys.CtrlS));
        Assert.Equal(ConsoleKey.M, Keys.CtrlM.Key);   // the CR rides on the M key: never Enter, never a send
        Assert.False(Keys.IsSend(Keys.CtrlM));
        Assert.False(Keys.IsLineBreak(Keys.CtrlM));
        Assert.Equal("/memory", Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.M)));
        Assert.Equal("/skills", Keys.ShortcutLine(Keys.CtrlAltS));
        Assert.Equal("/splash", Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.Z)));   // Ctrl+Alt+P until 2026-10-18
        Assert.Equal("/rename", Keys.ShortcutLine(Keys.CtrlN));   // 2026-10-18 (Ctrl+Alt+R from 2026-10-05 until then)
        Assert.Null(Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.R)));

        Assert.Equal("/help", Keys.ShortcutLine(Keys.CtrlH));
        Assert.Equal("/profile", Keys.ShortcutLine(Keys.CtrlP));
        Assert.Equal("/usage", Keys.ShortcutLine(Keys.CtrlU));
        Assert.Equal("/find", Keys.ShortcutLine(Keys.CtrlF));   // /perfbar until 2026-10-18
        Assert.Equal("/tools", Keys.ShortcutLine(Keys.CtrlT));   // /toolbar until 2026-10-18
        Assert.Equal("/browser", Keys.ShortcutLine(Keys.CtrlB));
        Assert.Equal("/webui browser", Keys.ShortcutLine(Keys.CtrlW));
        Assert.Equal(ConsoleKey.H, Keys.CtrlH.Key);   // the BS rides on the H key: never Backspace
        Assert.Null(Keys.ShortcutLine(Keys.Backspace));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, true)));   // Ctrl+Backspace
        // The Ctrl+Alt chords they replaced are gone (Ctrl+Alt+P came back as /splash; Ctrl+Alt+G as /log on 2026-10-02, gone again
        // on 2026-10-12 when /log moved to plain Ctrl+G), and Ctrl+Alt+K with /skills' move.
        Assert.Null(Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.G)));
        Assert.Equal("/log", Keys.ShortcutLine(Keys.Ctrl(ConsoleKey.G)));
        Assert.Null(Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.H)));   // /header from later still on 2026-10-01 until 2026-10-18 (the bar chords went)
        // Ctrl+Alt+K and Ctrl+Alt+B came back on 2026-10-11 as the botchat chords (ShortcutLine_TheBotChatChords_…).
        Assert.Equal(NeonSidekick.App.BotChat.KillLine, Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.K)));
        Assert.Equal(NeonSidekick.App.BotChat.ResumeLine, Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.B)));   // /toolbar on Ctrl+T, /perfbar on Ctrl+F since later still on 2026-10-01
        Assert.Null(Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.E)));   // /sessions from later on 2026-10-03 until 2026-10-18
        Assert.Equal("/sessions", Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.Y)));   // /sys on Ctrl+Y since 2026-10-03; /sessions here since 2026-10-18
        Assert.Equal("/tools shell allowed", Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.T)));   // /tools until 2026-10-18
        Assert.Null(Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.L)));   // /tools shell allowed until 2026-10-18
    }

    [Theory]
    [InlineData(ConsoleKey.U, '\x15', "/comfy view")]
    [InlineData(ConsoleKey.V, '\x16', "/camera live")]
    [InlineData(ConsoleKey.W, '\x17', "/webui")]   // 2026-10-18 (/prompts from 2026-10-13 until then)
    public void ShortcutLine_TheWindowChords_AreTheirLines_AndAnAltGrCharacterStaysACharacter(ConsoleKey key, char control, string line)
    {
        // 2026-10-02 (the user's ask): the picture viewer and the camera's live view (and the log window, on plain Ctrl+G since
        // 2026-10-12). The console's NAK and SYN and the test factory's '\0' count; Shift, Alt alone, plain Ctrl (Ctrl+U is /usage,
        // Ctrl+V the paste) and an AltGr key that types a character do not.
        Assert.Equal(line, Keys.ShortcutLine(new ConsoleKeyInfo(control, key, shift: false, alt: true, control: true)));
        Assert.Equal(line, Keys.ShortcutLine(Keys.CtrlAlt(key)));
        Assert.Contains(line.Split(' ')[0], NeonSidekick.App.SlashCommands.Words);
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: true, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: false, alt: true, control: false)));
        Assert.NotEqual(line, Keys.ShortcutLine(Keys.Ctrl(key)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('@', key, shift: false, alt: true, control: true)));
        Assert.False(Keys.IsKillSwitch(Keys.CtrlAlt(key)));
    }

    [Theory]
    [InlineData(ConsoleKey.B, '\x02', "/botchat --resume")]
    [InlineData(ConsoleKey.K, '\x0b', "/botchat --kill")]
    public void ShortcutLine_TheBotChatChords_AreTheirLines_AndAnAltGrCharacterStaysACharacter(ConsoleKey key, char control, string line)
    {
        // 2026-10-11 (the user's ask: a key to carry a stopped /botchat on, or to stop the servers it left). The console's STX and VT
        // and the test factory's '\0' count; Shift, Alt alone, plain Ctrl and an AltGr key that types a character do not.
        Assert.Equal(line, Keys.ShortcutLine(new ConsoleKeyInfo(control, key, shift: false, alt: true, control: true)));
        Assert.Equal(line, Keys.ShortcutLine(Keys.CtrlAlt(key)));
        Assert.Equal(NeonSidekick.App.SlashCommand.BotChat, NeonSidekick.App.SlashCommands.Parse(line).Command);
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: true, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: false, alt: true, control: false)));
        Assert.NotEqual(line, Keys.ShortcutLine(Keys.Ctrl(key)));   // plain Ctrl+B is /browser since 2026-10-18
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('{', key, shift: false, alt: true, control: true)));
        Assert.False(Keys.IsKillSwitch(Keys.CtrlAlt(key)));
    }

    [Fact]
    public void ShortcutLine_CtrlPeriod_IsTerminal_AndATypedPeriodStaysAPeriod()
    {
        // 2026-10-03 (the user's ask, Ctrl+. over Ctrl+Shift+.): Ctrl+/'s shape on OemPeriod with no character; a typed "."
        // carries its character, and Shift, Alt and Ctrl+Alt are no chord.
        Assert.Equal("/terminal", Keys.ShortcutLine(Keys.CtrlPeriod));
        Assert.Equal("/terminal", Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.OemPeriod, shift: false, alt: false, control: true)));
        Assert.Contains("/terminal", NeonSidekick.App.SlashCommands.Words);
        Assert.Null(Keys.ShortcutLine(Keys.Char('.')));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('.', ConsoleKey.OemPeriod, shift: false, alt: false, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.OemPeriod, shift: true, alt: false, control: true)));   // Ctrl+> on a US layout
        Assert.Null(Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.OemPeriod)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.OemPeriod, shift: false, alt: true, control: false)));
        Assert.False(Keys.IsInterrupt(Keys.CtrlPeriod));
        Assert.False(Keys.IsToolToggle(Keys.CtrlPeriod));
    }

    [Fact]
    public void ShortcutLine_CtrlSlash_IsSettings_AndATypedSlashStaysASlash()
    {
        // Later still on 2026-10-01 (the user's ask): Ctrl+/ on the US "/" key (Oem2), no character or US's '\x1f'; a typed
        // "/" carries its character (the slash list's key), and Shift, Alt and Ctrl+Alt are no chord.
        Assert.Equal("/settings", Keys.ShortcutLine(Keys.CtrlSlash));
        Assert.Equal("/settings", Keys.ShortcutLine(new ConsoleKeyInfo('\x1f', ConsoleKey.Oem2, shift: false, alt: false, control: true)));
        Assert.Contains("/settings", NeonSidekick.App.SlashCommands.Words);
        Assert.Null(Keys.ShortcutLine(Keys.Char('/')));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('/', ConsoleKey.Oem2, shift: false, alt: false, control: false)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('/', ConsoleKey.Oem2, shift: false, alt: false, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.Oem2, shift: true, alt: false, control: true)));
        Assert.Null(Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.Oem2)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.Oem2, shift: false, alt: true, control: false)));
        Assert.False(Keys.IsInterrupt(Keys.CtrlSlash));
        Assert.False(Keys.IsToolToggle(Keys.CtrlSlash));
    }

    [Fact]
    public void IsKillSwitch_IsCtrlAltX_WithoutShift_AndNeverAShortcutLine()
    {
        // 2026-10-01 (the user's ask): the test factory's '\0' and the console's CAN count; Ctrl+X (cut), Shift, Alt alone and an
        // AltGr+X that types a character do not. A chord with no command: never a ShortcutLine.
        Assert.True(Keys.IsKillSwitch(Keys.CtrlAlt(ConsoleKey.X)));
        Assert.True(Keys.IsKillSwitch(new ConsoleKeyInfo('\x18', ConsoleKey.X, shift: false, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.X)));
        Assert.False(Keys.IsKillSwitch(Keys.Ctrl(ConsoleKey.X)));
        Assert.False(Keys.IsKillSwitch(new ConsoleKeyInfo('\x18', ConsoleKey.X, shift: false, alt: false, control: true)));
        Assert.False(Keys.IsKillSwitch(new ConsoleKeyInfo('\0', ConsoleKey.X, shift: true, alt: true, control: true)));
        Assert.False(Keys.IsKillSwitch(new ConsoleKeyInfo('\0', ConsoleKey.X, shift: false, alt: true, control: false)));
        Assert.False(Keys.IsKillSwitch(new ConsoleKeyInfo('ź', ConsoleKey.X, shift: false, alt: true, control: true)));   // AltGr+X on a Polish layout
        Assert.False(Keys.IsKillSwitch(Keys.CtrlAltC));
        Assert.False(Keys.IsKillSwitch(Keys.Char('x')));
    }

    [Fact]
    public void IsLearnCancel_IsPlainCtrlL_AndNeverAShortcutLine()
    {
        // 2026-10-04 (the user's ask): the test factory's '\0' and the console's FF count; Ctrl+Alt+L (the allowed-commands list until 2026-10-18, free since), Shift, and a
        // typed L (Spectre's test input marks a capital with Control) do not. A chord with no command: never a ShortcutLine.
        Assert.True(Keys.IsLearnCancel(Keys.CtrlL));
        Assert.True(Keys.IsLearnCancel(Keys.Ctrl(ConsoleKey.L)));
        Assert.Null(Keys.ShortcutLine(Keys.CtrlL));
        Assert.Null(Keys.ShortcutLine(Keys.CtrlAlt(ConsoleKey.L)));
        Assert.False(Keys.IsLearnCancel(Keys.CtrlAlt(ConsoleKey.L)));
        Assert.False(Keys.IsLearnCancel(new ConsoleKeyInfo('\x0c', ConsoleKey.L, shift: true, alt: false, control: true)));
        Assert.False(Keys.IsLearnCancel(new ConsoleKeyInfo('L', ConsoleKey.L, shift: true, alt: false, control: true)));
        Assert.False(Keys.IsLearnCancel(new ConsoleKeyInfo('l', ConsoleKey.L, shift: false, alt: false, control: false)));
        Assert.False(Keys.IsLearnCancel(Keys.Char('l')));
        Assert.False(Keys.IsLearnCancel(Keys.Ctrl(ConsoleKey.K)));
        Assert.False(Keys.IsKillSwitch(Keys.CtrlL));
    }

    [Fact]
    public void IsEmojiPicker_IsPlainCtrlJ_NeverCtrlEnter_AndNeverAShortcutLine()
    {
        // 2026-10-12 (the user's ask): the console's LF on J and the test factory's '\0' count; Ctrl+Enter's LF rides on Enter and stays
        // the line break; Alt, Shift and a typed j do not. A chord with no command: never a ShortcutLine.
        Assert.True(Keys.IsEmojiPicker(Keys.CtrlJ));
        Assert.True(Keys.IsEmojiPicker(Keys.Ctrl(ConsoleKey.J)));
        Assert.Null(Keys.ShortcutLine(Keys.CtrlJ));
        Assert.False(Keys.IsEmojiPicker(Keys.CtrlEnter));
        Assert.True(Keys.IsLineBreak(Keys.CtrlEnter));
        Assert.False(Keys.IsLineBreak(Keys.CtrlJ));
        Assert.False(Keys.IsEmojiPicker(Keys.CtrlAlt(ConsoleKey.J)));
        Assert.False(Keys.IsEmojiPicker(new ConsoleKeyInfo('\n', ConsoleKey.J, shift: true, alt: false, control: true)));
        Assert.False(Keys.IsEmojiPicker(new ConsoleKeyInfo('J', ConsoleKey.J, shift: true, alt: false, control: true)));
        Assert.False(Keys.IsEmojiPicker(new ConsoleKeyInfo('j', ConsoleKey.J, shift: false, alt: false, control: false)));
        Assert.False(Keys.IsEmojiPicker(Keys.Char('j')));
        Assert.False(Keys.IsLearnCancel(Keys.CtrlJ));
    }

    [Fact]
    public void ToolGroupText_IsPinned()
    {
        Assert.Equal(1, TextCells.Width(ToolGroupText.CollapsedGlyph));
        Assert.Equal(1, TextCells.Width(ToolGroupText.ExpandedGlyph));
        Assert.Equal("  ▸ 🛠️ 6 tool calls — read_file ×3, grep ×2, run_command", ToolGroupText.Summary([("grep", 2), ("read_file", 3), ("run_command", 1)], expanded: false));
        Assert.Equal("  ▾ 🛠️ 1 tool call — grep", ToolGroupText.Summary([("grep", 1)], expanded: true));
        Assert.Equal("  ▸ 🛠️ tool calls", ToolGroupText.Summary([], expanded: false));
        Assert.Equal("  ▸ 🛠️ 4 tool calls — b ×2, a, c", ToolGroupText.Summary([("a", 1), ("b", 2), ("c", 1)], expanded: false));   // a tie keeps the order first called
        Assert.True(TextCells.Width(ToolGroupText.Summary(Enumerable.Range(0, 50).Select(i => ("tool_number_" + i, 1)).ToList(), false)) <= TranscriptRenderer.ToolTextLimit);   // cells since 2026-10-04
        Assert.Equal("(tool calls, code blocks, diffs and thinking expanded; Ctrl+O or /collapse folds them)", ToolGroupText.ExpandedNotice(true));   // diffs since 2026-10-04
        Assert.Equal("(tool calls, code blocks, diffs and thinking collapsed; Ctrl+O, /expand or a click on a summary unfolds them)", ToolGroupText.ExpandedNotice(false));
    }

    [Fact]
    public void CtrlC_IsTheConsolesShape()
    {
        Assert.Equal('\x03', Keys.CtrlC.KeyChar);
        Assert.Equal(ConsoleKey.C, Keys.CtrlC.Key);
        Assert.Equal(ConsoleModifiers.Control, Keys.CtrlC.Modifiers);
    }

    [Fact]
    public void F4_IsABareKey()
    {
        Assert.Equal(ConsoleKey.F4, Keys.F4.Key);
        Assert.Equal('\0', Keys.F4.KeyChar);
        Assert.False(Keys.IsCancel(Keys.F4));
    }

    [Fact]
    public void Char_CarriesTheCharacterWithNoKey()
    {
        var key = Keys.Char('é');
        Assert.Equal('é', key.KeyChar);
        Assert.Equal(ConsoleKey.None, key.Key);
        Assert.Equal((ConsoleModifiers)0, key.Modifiers);
    }
}

public class PromptResultTests
{
    [Fact]
    public void Canceled_IsOneSharedInstance()
    {
        Assert.Same(PromptResult<string>.Canceled, PromptResult<string>.Canceled);
        Assert.True(PromptResult<string>.Canceled.IsCanceled);
        Assert.Null(PromptResult<string>.Canceled.Value);
    }

    [Fact]
    public void From_WrapsTheValue_WithRecordEquality()
    {
        Assert.Equal(PromptResult<string>.From("a"), PromptResult<string>.From("a"));
        Assert.NotEqual(PromptResult<string>.From("a"), PromptResult<string>.Canceled);
        Assert.Throws<ArgumentNullException>(() => PromptResult<string>.From(null!));
    }
}

public class TextCellsTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("日本語", 6)]
    [InlineData("a日b", 4)]
    [InlineData("😀", 2)]
    [InlineData("x😀y", 4)]
    [InlineData("⏰", 2)]
    [InlineData("⏳", 2)]
    [InlineData("✋", 2)]                    // ✋ (2026-09-18): the interrupt glyph, a BMP character Unicode lists as Wide
    [InlineData("✊", 2)]
    [InlineData("🎤 👂 ✋", 8)]              // the speech strip with the interrupt
    [InlineData("⏰ tea 09:00", 12)]
    [InlineData("\U0001F5D1", 2)]                // 🗑 bare: a surrogate pair
    [InlineData("\U0001F5D1️", 2)]          // 🗑️ (2026-09-18): the variation selector is zero, drawn inside the pair's two cells
    [InlineData("\U0001F5D1️ ", 3)]
    [InlineData("⚙️", 2)]              // ⚙️ (2026-09-19): the gear is Neutral, one cell bare; U+FE0F makes the two-cell emoji, so the selector counts one
    [InlineData("✂️", 2)]              // ✂️: the prune lines' scissors, the same shape
    [InlineData("✂️ x", 4)]
    [InlineData("✋️", 2)]              // a selector after a wide BMP character stays zero
    [InlineData("\U0001F5DC️", 2)]          // 🗜️: the compact lines' clamp, a pair + the selector
    [InlineData("\U0001F6D1", 2)]                // 🛑: the stop line's sign, a pair alone
    [InlineData("️", 0)]                    // a selector with nothing before it
    [InlineData("️️", 0)]              // two selectors: the second follows a selector, not a character
    [InlineData("⛓️‍\U0001F4A5", 2)]   // ⛓️‍💥 (later on 2026-09-29, the uncensored column): a ZWJ sequence, one glyph as wide as its first part
    [InlineData("⛓️‍\U0001F4A5 x", 4)]
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467", 2)]   // 👨‍👩‍👧: the family, three pairs joined, two cells
    [InlineData("\U0001F4A2", 2)]                // 💢: a pair alone (the aggressive build's mark until 2026-10-02)
    [InlineData("⚔️", 2)]              // ⚔️: the crossed swords, Neutral bare, two cells with the selector
    [InlineData("⛓️‍💥⚔️", 4)]   // ⛓️‍💥⚔️: the chain and the swords side by side (the aggressive build's mark from 2026-10-02 until 2026-10-14)
    public void Width_CountsCells(string text, int cells)
    {
        Assert.Equal(cells, TextCells.Width(text));
    }

    [Fact]
    public void Width_AVariationSelectorIsZero()
    {
        Assert.Equal(0, TextCells.Width('️'));
        Assert.Equal(0, TextCells.Width('︎'));
    }

    [Fact]
    public void ElementLengthBefore_StepsOverASurrogatePair()
    {
        Assert.Equal(2, TextCells.ElementLengthBefore("a😀", 3));
        Assert.Equal(1, TextCells.ElementLengthBefore("ab", 2));
        Assert.Equal(0, TextCells.ElementLengthBefore("ab", 0));
    }
}
