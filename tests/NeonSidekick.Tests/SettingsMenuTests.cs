using System.Net;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public partial class SettingsMenuTests : IDisposable
{
    /// <summary>A second level's label inside <c>/settings</c> (<c>Settings › TTS voice</c>). Test-side since 2026-09-24: src builds the crumb its own way and never called the helper.</summary>
    internal static string Breadcrumb(string label) => SettingsMenu.Title + " › " + label;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly AppSettings _settings;
    private readonly StubHttpMessageHandler _http = new();
    private readonly Dictionary<SettingsField, string> _overrides = new();
    private readonly TranscriptRenderer _transcript;
    private readonly FakeSynthesizer _synth = new();
    private readonly SpeechSession _speech;
    private readonly SettingsMenu _menu;

    public SettingsMenuTests()
    {
        _console.Profile.Width = 100;
        _settings = new AppSettings(_dir);
        // Speech output is off by default; the toggle test walks it on -> off, so the fixture opts in — over the server
        // (the fake is HTTP-shaped; in-process is the default since 2026-09-16, and the picker tests set it when they want it).
        _settings.Update(d => { d.TtsOutput = true; d.TtsSource = "http"; });
        _transcript = new TranscriptRenderer(_console);
        _speech = new SpeechSession(_ => _synth, _ => new FakeAudioPlayback(), new ModelStore(Path.Combine(_dir, "models"), new HttpClient(new StubHttpMessageHandler())));
        var input = new InputLine(_console, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)));
        _menu = new SettingsMenu(_console, _settings, f => _overrides.GetValueOrDefault(f), input, _transcript, _speech, NoPane(_console), _ => FakeBrowserPath);
    }

    /// <summary>What the fixture's browser auto-detection "finds", so the empty <c>Browser path</c> row reads the same on every machine.</summary>
    private const string FakeBrowserPath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    /// <summary>A menu pane over a console without geometry: disabled, so every list is the Spectre prompt.</summary>
    private static MenuPane NoPane(IAnsiConsole console) =>
        new(new ScreenPane(console, null, new ManualTimeProvider()), new KeySource(console.Input, TimeSpan.FromMilliseconds(1)));

    public void Dispose()
    {
        _speech.Dispose();
        _settings.Dispose();
        _console.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Push(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys)
        {
            _console.Input.PushKey(key);
        }
    }

    private void Down(int times)
    {
        for (int i = 0; i < times; i++)
        {
            Push(Keys.Down);
        }
    }

    /// <summary>From the General tab the pane opens on, Right as many times as the tab's index — so a re-ordered strip (2026-09-18) moves no test.</summary>
    private void GoTo(SettingsTab tab)
    {
        for (int i = 0; i < (int)tab; i++)
        {
            Push(Keys.Right);
        }
    }

    private void Backspace(int times)
    {
        for (int i = 0; i < times; i++)
        {
            Push(Keys.Backspace);
        }
    }

    private LlmSession Session() => new(
        new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        (_, _) => new FakeChatClient());

    // ── /settings ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Escape_LeavesEverythingUnchanged_AndReturnsFalse()
    {
        var before = _settings.Current;
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal(before.LlmUrl, _settings.Current.LlmUrl);
        Assert.Contains(SettingsMenu.Title, _console.Output);
        Assert.Contains("LLM URL", _console.Output);
    }

    [Fact]
    public async Task Toggle_SpeechOutput_PersistsAndIsNotAnLlmChange()
    {
        Assert.True(_settings.Current.TtsOutput);
        Down(8);                     // SpeechOutput (TTS output) is the 9th row, first of the TTS block
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.TtsOutput);
        Assert.Contains("  · TTS output: off", _console.Output);
    }

    [Fact]
    public async Task Toggle_Memory_PersistsAndNeedsNoReconnect()
    {
        Assert.True(_settings.Current.Memory);
        Down(1);                     // Memory is the second row, under Profile
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.Memory);
        Assert.Contains("  · Memory: off", _console.Output);
    }

    [Fact]
    public async Task Toggle_CopyUserPrompt_IsRow25_PersistsAndNeedsNoReconnect()
    {
        Assert.True(_settings.Current.CopyUserPrompt);
        Down(24);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.CopyUserPrompt);
        Assert.Contains("  · Copy user prompt: off", _console.Output);   // "Copy user text" until 2026-09-18
    }

    [Fact]
    public async Task Toggle_ShowImageThumbnails_IsRow27_PersistsAndNeedsNoReconnect()
    {
        Assert.True(_settings.Current.ShowImageThumbnails);
        Down(26);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.ShowImageThumbnails);
        Assert.Contains("  · Show image thumbnails: off", _console.Output);
    }

    [Fact]
    public async Task EditModel_ByText_SavesAndReturnsTrue()
    {
        Down(3);
        Push(Keys.Enter);                       // LLM model
        _console.Input.PushText("qwen3");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("qwen3", _settings.Current.LlmModel);
        Assert.Contains("  · 🖥️ LLM model: qwen3", _console.Output);
    }

    [Fact]
    public async Task EditApiKey_ByText_SavesItEncrypted()
    {
        // 2026-09-28 (the user's call): the LLM API key is kept DPAPI-encrypted like the Claude and Home Assistant API keys.
        Down(4);
        Push(Keys.Enter);                       // LLM API key
        _console.Input.PushText("sk-typed");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await _menu.ShowAsync(CancellationToken.None));

        string stored = _settings.Current.LlmApiKey;
        Assert.Equal("sk-typed", SettingsSecrets.Reveal(stored));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(NeonSidekick.Sql.WindowsCredentials.IsProtected(stored));
            Assert.Contains("  · 🖥️ LLM API key: " + SettingsMenu.ClaudeApiKeyEncryptedLabel, _console.Output);
        }
    }

    [Fact]
    public async Task EditApiKey_ThePlaceholder_StaysPlain()
    {
        _settings.Update(d => d.LlmApiKey = "sk-old");
        Down(4);
        Push(Keys.Enter);                       // LLM API key: the line starts empty, the key never put back on it
        _console.Input.PushText(LlmEndpoint.DefaultApiKey);
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(LlmEndpoint.DefaultApiKey, _settings.Current.LlmApiKey);
    }

    [Fact]
    public void ApiKeyRows_ArePinned()
    {
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.LlmApiKey, new AppSettingsData { LlmApiKey = "sk-secret" }));
        Assert.Equal(SettingsMenu.ClaudeApiKeyEncryptedLabel, SettingsMenu.FieldValue(SettingsField.LlmApiKey, new AppSettingsData { LlmApiKey = "dpapi:AQAA" }, _settings.ProfileDirectory));
        Assert.Equal("em•••", SettingsMenu.FieldValue(SettingsField.LlmApiKey, new AppSettingsData(), _settings.ProfileDirectory));
        Assert.Equal("LLM API key saved unencrypted: no DPAPI.", SettingsMenu.LlmApiKeyPlainWarning("no DPAPI"));
        Assert.Equal("Home Assistant API key", SettingsMenu.FieldName(SettingsField.HomeAssistantToken));   // "token" until 2026-09-28 (the user's call)
        Assert.Equal("Home Assistant API key saved unencrypted: no DPAPI.", SettingsMenu.HomeAssistantTokenPlainWarning("no DPAPI"));
    }

    [Fact]
    public async Task EditUrl_EmptyIsAllowed_AndMeansProbe()
    {
        _settings.Update(d => d.LlmUrl = "http://old:1");
        Down(2);
        Push(Keys.Enter);                       // LLM URL, third row (Profile, then Memory)
        for (int i = 0; i < 12; i++)
        {
            Push(Keys.Backspace);
        }

        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("", _settings.Current.LlmUrl);
        Assert.Contains("(probe local ports)", _console.Output);
    }

    [Fact]
    public async Task EditText_Escape_KeepsTheOldValue()
    {
        Down(3);
        Push(Keys.Enter);                       // LLM model
        _console.Input.PushText("typo");
        Push(Keys.Escape);                      // one ESC keeps the saved value, typed text or not
        Push(Keys.Escape);                      // leave the menu

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("", _settings.Current.LlmModel);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task BadTimeout_IsRejected_AndTheOldValueKept()
    {
        Down(6);
        Push(Keys.Enter);                       // LLM request timeout (row 7), shows "3600"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("abc");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal(3600, _settings.Current.LlmRequestTimeoutSeconds);
        Assert.Contains(SettingsMenu.LlmRequestTimeoutRangeError, _console.Output);
        Assert.Contains("keeping 3600", _console.Output);
    }

    [Fact]
    public async Task RequestTimeout_OverAnHour_IsRejected()
    {
        _console.Profile.Width = 240;
        Down(6);
        Push(Keys.Enter);                       // LLM request timeout (row 7), shows "3600"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("3601");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal(3600, _settings.Current.LlmRequestTimeoutSeconds);
        Assert.Contains("LLM request timeout (s) must be a number of seconds in (0, 3600]; keeping 3600.", _console.Output);
    }

    [Fact]
    public async Task GoodTimeout_IsSaved_InvariantCulture()
    {
        Down(7);
        Push(Keys.Enter);                       // LLM turn timeout (row 8) "21600"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("120.5");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(120.5, _settings.Current.LlmTurnTimeoutSeconds);
    }

    [Theory]
    [InlineData("3601", 3601, SettingsChanges.Llm)]      // over the request ceiling, under the turn's
    [InlineData("21601", 21600, SettingsChanges.None)]
    public async Task TurnTimeout_HasItsOwnCeiling(string typed, double expected, SettingsChanges changes)
    {
        _console.Profile.Width = 240;
        Down(7);
        Push(Keys.Enter);                       // LLM turn timeout (row 8) "21600"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText(typed);
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(changes, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(expected, _settings.Current.LlmTurnTimeoutSeconds);
        if (changes == SettingsChanges.None)
        {
            Assert.Contains("LLM turn timeout (s) must be a number of seconds in (0, 21600]; keeping 21600.", _console.Output);
        }
    }

    [Fact]
    public async Task ContextLength_IsRow26_Typed_AnLlmChange_ZeroForTheServersFigure()
    {
        Down(25);
        Push(Keys.Enter);                       // LLM context length (the last row) shows "0"
        Push(Keys.Backspace);
        _console.Input.PushText("32768");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(32_768, _settings.Current.LlmContextLength);
        Assert.Contains("  · 🖥️ LLM context length: 32,768 tokens", _console.Output);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("32k")]
    [InlineData("4096.5")]
    public async Task BadContextLength_IsRejected_AndTheOldValueKept(string typed)
    {
        _console.Profile.Width = 240;           // the whole error on one row
        _settings.Update(d => d.LlmContextLength = 8_192);
        Down(25);
        Push(Keys.Enter);                       // shows "8192"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText(typed);
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal(8_192, _settings.Current.LlmContextLength);
        Assert.Contains(SettingsMenu.ContextLengthRangeError, _console.Output);
        Assert.Contains("keeping 8192", _console.Output);
    }

    [Fact]
    public async Task MaxToolIterations_IsRow34_Typed_NoReconnect_AndZeroIsRefused()
    {
        _console.Profile.Width = 240;
        Down(33);
        Push(Keys.Enter);                       // LLM max tool iterations shows "10000"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("0");
        Push(Keys.Enter);                       // refused, the row stays open with "10000"
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("20");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(20, _settings.Current.LlmMaxToolIterations);
        Assert.Contains(SettingsMenu.MaxToolIterationsRangeError, _console.Output);
        Assert.Contains("keeping 10000", _console.Output);
        Assert.Contains("  · 🖥️ LLM max tool iterations: 20 round trips", _console.Output);
    }

    [Fact]
    public async Task PushToTalkKey_IsRow23_APicker_AndAVoiceChange()
    {
        Down(21);
        Push(Keys.Enter);                                                   // Push-to-talk key "F4" (row 22): the picker opens on F4
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // F9
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("F9", _settings.Current.SttPushToTalkKey);
        Assert.Contains(Breadcrumb("STT push-to-talk key"), _console.Output);
        Assert.Contains("  · STT push-to-talk key: F9", _console.Output);
        Assert.Contains("F4  the default", _console.Output);
        Assert.DoesNotContain("F11", _console.Output);   // only the listed keys are offered
    }

    [Fact]
    public async Task PushToTalkKey_OpensOnTheSavedKey_AndPicksTheOneBelow()
    {
        _settings.Update(d => d.SttPushToTalkKey = "Insert");
        Down(21);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // Insert → Home

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("Home", _settings.Current.SttPushToTalkKey);
    }

    [Fact]
    public async Task PushToTalkKey_EscapeKeepsTheSavedKey()
    {
        _settings.Update(d => d.SttPushToTalkKey = "Insert");
        Down(21);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("Insert", _settings.Current.SttPushToTalkKey);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public void PushToTalkKeys_ArePinned()
    {
        Assert.Equal(
            new[]
            {
                ConsoleKey.F1, ConsoleKey.F2, ConsoleKey.F3, ConsoleKey.F4, ConsoleKey.F5, ConsoleKey.F6, ConsoleKey.F7, ConsoleKey.F8, ConsoleKey.F9, ConsoleKey.F10,
                ConsoleKey.Insert, ConsoleKey.Home, ConsoleKey.End, ConsoleKey.PageUp, ConsoleKey.PageDown,
            },
            SettingsMenu.PushToTalkKeys);
        Assert.Equal("Insert", SettingsMenu.PushToTalkLabel(ConsoleKey.Insert));
        Assert.StartsWith("F4", SettingsMenu.PushToTalkLabel(ConsoleKey.F4));
        Assert.Contains("the default", SettingsMenu.PushToTalkLabel(ConsoleKey.F4));
    }

    [Fact]
    public async Task WakePhrase_IsRow18_NormalisedValidated_AndAVoiceChange()
    {
        _console.Profile.Width = 240;
        Down(17);
        Push(Keys.Enter);                       // Wake phrase "hey neon"
        for (int i = 0; i < 8; i++)
        {
            Push(Keys.Backspace);
        }

        _console.Input.PushText("  Hey   JARVIS ");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // the cursor stays on the row: "hey jarvis"
        for (int i = 0; i < 10; i++)
        {
            Push(Keys.Backspace);
        }

        _console.Input.PushText("neon 2");      // a digit: rejected
        Push(Keys.Enter);
        Push(Keys.Enter);
        for (int i = 0; i < 10; i++)
        {
            Push(Keys.Backspace);
        }

        _console.Input.PushText("one two three four");   // too many words: rejected
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("hey jarvis", _settings.Current.SttWakePhrase);
        Assert.Contains("  · STT wake phrase: hey jarvis", _console.Output);
        Assert.Equal(2, _console.Output.Split(SettingsMenu.WakePhraseError).Length - 1);
        Assert.Contains("keeping hey jarvis", _console.Output);
    }

    [Fact]
    public async Task WakeWordToggle_IsRow17_AndAVoiceChange()
    {
        Down(16);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked          // Wake word: off -> on

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));
        Assert.True(_settings.Current.SttWake);
    }

    [Fact]
    public async Task InterruptToggle_IsRow19_AndAVoiceChange()
    {
        _settings.Update(d => d.SttWake = true);   // the interrupt needs the wake word
        Down(18);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked          // Interrupt: off -> on

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));
        Assert.True(_settings.Current.SttInterrupt);
        Assert.Contains("  · STT interrupt: on", _console.Output);
    }

    [Fact]
    public async Task InterruptToggle_WithTheWakeWordOff_IsRefused()
    {
        Down(18);
        Push(Keys.Enter, Keys.Escape);          // Interrupt: off -> refused

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.False(_settings.Current.SttInterrupt);
        Assert.Contains("  · " + ChatScreen.InterruptNeedsWakeNotice, _console.Output);
        Assert.DoesNotContain("STT interrupt: on", _console.Output);
    }

    [Fact]
    public async Task WakeWordToggle_Off_TakesTheInterruptDown_AndSaysSo()
    {
        _settings.Update(d => { d.SttWake = true; d.SttInterrupt = true; });
        Down(16);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked          // Wake word: on -> off

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));
        Assert.False(_settings.Current.SttWake);
        Assert.False(_settings.Current.SttInterrupt);
        Assert.Contains("  · STT wake: off", _console.Output);
        Assert.Contains("  · " + ChatScreen.InterruptOffWithWakeNotice, _console.Output);
    }

    [Theory]
    [InlineData("neon", true)]
    [InlineData("  Hey   Neon ", true)]
    [InlineData("hey there neon", true)]
    [InlineData("one two three four", false)]
    [InlineData("neon 2", false)]
    [InlineData("neon!", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsWakePhraseCandidate_AcceptsOneToThreeWordsOfLetters(string? phrase, bool accepted)
    {
        Assert.Equal(accepted, SettingsMenu.IsWakePhraseCandidate(phrase));
    }

    [Theory]
    [InlineData(ConsoleKey.F1, true)]
    [InlineData(ConsoleKey.F4, true)]
    [InlineData(ConsoleKey.F10, true)]
    [InlineData(ConsoleKey.Insert, true)]
    [InlineData(ConsoleKey.Home, true)]
    [InlineData(ConsoleKey.End, true)]
    [InlineData(ConsoleKey.PageUp, true)]
    [InlineData(ConsoleKey.PageDown, true)]
    [InlineData(ConsoleKey.F11, false)]      // the terminal's fullscreen key
    [InlineData(ConsoleKey.F12, false)]
    [InlineData(ConsoleKey.Pause, false)]
    [InlineData(ConsoleKey.Delete, false)]   // the line's own editing key
    [InlineData(ConsoleKey.A, false)]
    [InlineData(ConsoleKey.D7, false)]
    [InlineData(ConsoleKey.NumPad3, false)]
    [InlineData(ConsoleKey.OemPeriod, false)]
    [InlineData(ConsoleKey.Spacebar, false)]
    [InlineData(ConsoleKey.Enter, false)]
    [InlineData(ConsoleKey.Escape, false)]
    [InlineData(ConsoleKey.Backspace, false)]
    [InlineData(ConsoleKey.Tab, false)]
    public void IsPushToTalkCandidate_IsTheListedKeys(ConsoleKey key, bool accepted)
    {
        Assert.Equal(accepted, SettingsMenu.IsPushToTalkCandidate(key));
    }

    [Fact]
    public async Task WhisperModel_IsRow24_APicker_AndAVoiceChange()
    {
        Down(22);
        Push(Keys.Enter);                       // Whisper model "ggml-base.en.bin" (row 23): the picker opens on it
        Push(Keys.Down, Keys.Enter, Keys.Escape);   // ggml-small.en.bin

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("ggml-small.en.bin", _settings.Current.SttWhisperModel);
        Assert.Contains(Breadcrumb("STT whisper model"), _console.Output);
        Assert.Contains("  · STT whisper model: ggml-small.en.bin", _console.Output);
        Assert.Contains("ggml-base.en.bin   the default, 148 MB", _console.Output);
        Assert.Contains("ggml-small.en.bin  most accurate, 488 MB", _console.Output);
        Assert.DoesNotContain(ModelStore.WhisperModelError, _console.Output);
    }

    [Fact]
    public async Task WhisperModel_OpensOnTheSavedName_UpPicksTheOneAbove()
    {
        _settings.Update(d => d.SttWhisperModel = "GGML-SMALL.EN.BIN");   // any case
        Down(22);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // small → base

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("ggml-base.en.bin", _settings.Current.SttWhisperModel);
    }

    [Fact]
    public async Task WhisperModel_EscapeKeepsTheSavedName()
    {
        _settings.Update(d => d.SttWhisperModel = "ggml-small.en.bin");
        Down(22);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("ggml-small.en.bin", _settings.Current.SttWhisperModel);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task WhisperModel_SavedPath_OpensOnTheFirstRow()
    {
        _settings.Update(d => d.SttWhisperModel = @"C:\models\ggml-x.bin");   // only the variable or a hand-edited file can hold a path
        Down(22);
        Push(Keys.Enter, Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("ggml-tiny.en.bin", _settings.Current.SttWhisperModel);
    }

    [Fact]
    public async Task WhisperModel_RetiredShortName_OpensOnTheFirstRow()
    {
        _settings.Update(d => d.SttWhisperModel = "base.en");   // a profile saved before 2026-09-16: not mapped, the picker starts at the top
        Down(22);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("ggml-base.en.bin", _settings.Current.SttWhisperModel);
    }

    [Fact]
    public void WhisperModelLabel_IsPinned()
    {
        Assert.Equal("ggml-base.en.bin   " + Theme.DimMarkup("the default, 148 MB"), SettingsMenu.WhisperModelLabel("ggml-base.en.bin", 147_964_211));
        Assert.Equal("ggml-tiny.en.bin   " + Theme.DimMarkup("fastest, 78 MB"), SettingsMenu.WhisperModelLabel("ggml-tiny.en.bin", 77_691_713));
        Assert.Equal("ggml-small.en.bin  " + Theme.DimMarkup("most accurate, 488 MB"), SettingsMenu.WhisperModelLabel("ggml-small.en.bin", 487_601_967));
    }

    [Fact]
    public async Task VoskModel_IsRow54_APicker_AndAVoiceChange()
    {
        Down(53);
        Push(Keys.Enter);                       // Vosk model (row 53, the last): the picker opens on the default
        Push(Keys.Down, Keys.Enter, Keys.Escape);   // the lgraph model

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("vosk-model-en-us-0.22-lgraph", _settings.Current.SttVoskModel);
        Assert.Contains(Breadcrumb("STT vosk model"), _console.Output);
        Assert.Contains("  · STT vosk model: vosk-model-en-us-0.22-lgraph", _console.Output);
        Assert.Contains("vosk-model-small-en-us-0.15   the default, 41 MB", _console.Output);
        Assert.Contains("vosk-model-en-us-0.22-lgraph  most accurate, 131 MB", _console.Output);
        Assert.Contains("vosk-model-small-en-in-0.4    Indian English, 38 MB", _console.Output);
        Assert.DoesNotContain(ModelStore.VoskModelError, _console.Output);
    }

    [Fact]
    public async Task VoskModel_OpensOnTheSavedName_UpPicksTheOneAbove()
    {
        _settings.Update(d => d.SttVoskModel = "VOSK-MODEL-EN-US-0.22-LGRAPH");   // any case
        Down(53);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // lgraph → the default

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("vosk-model-small-en-us-0.15", _settings.Current.SttVoskModel);
    }

    [Fact]
    public async Task VoskModel_EscapeKeepsTheSavedName()
    {
        _settings.Update(d => d.SttVoskModel = "vosk-model-small-en-in-0.4");
        Down(52);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("vosk-model-small-en-in-0.4", _settings.Current.SttVoskModel);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task VoskModel_HandEditedName_OpensOnTheFirstRow()
    {
        _settings.Update(d => d.SttVoskModel = "vosk-model-en-us-0.22");   // a static-graph model, never offered: only a hand edit can hold it
        Down(53);
        Push(Keys.Enter, Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("vosk-model-small-en-us-0.15", _settings.Current.SttVoskModel);
    }

    [Fact]
    public void VoskModelLabel_IsPinned()
    {
        Assert.Equal("vosk-model-small-en-us-0.15   " + Theme.DimMarkup("the default, 41 MB"), SettingsMenu.VoskModelLabel("vosk-model-small-en-us-0.15", 41_205_931));
        Assert.Equal("vosk-model-en-us-0.22-lgraph  " + Theme.DimMarkup("most accurate, 131 MB"), SettingsMenu.VoskModelLabel("vosk-model-en-us-0.22-lgraph", 130_557_655));
        Assert.Equal("vosk-model-small-en-in-0.4    " + Theme.DimMarkup("Indian English, 38 MB"), SettingsMenu.VoskModelLabel("vosk-model-small-en-in-0.4", 37_573_330));
    }

    [Fact]
    public async Task TtsSpeed_IsSaved_AndRangeChecked()
    {
        Down(14);
        Push(Keys.Enter);                       // TTS speed "1.2" (row 14, after TTS voice mix)
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("1.5");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // the cursor stays on the row just edited: "1.5"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("9");
        Push(Keys.Enter, Keys.Escape);

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Equal(1.5, _settings.Current.TtsSpeed);
        Assert.Contains("  · TTS speed: 1.5", _console.Output);
        Assert.Contains(SettingsMenu.TtsSpeedRangeError, _console.Output);
        Assert.Contains("keeping 1.5", _console.Output);
    }

    [Fact]
    public async Task OverriddenField_IsLabelled_AndSavingWarns()
    {
        _overrides[SettingsField.LlmModel] = EnvironmentOverrides.LlmModelVariable;
        Down(3);
        Push(Keys.Enter);
        _console.Input.PushText("m");
        Push(Keys.Enter, Keys.Escape);

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Contains("(overridden by NEONSIDEKICK_LLM_MODEL)", _console.Output);
        Assert.Contains("  ! " + SettingsMenu.OverrideNotice(EnvironmentOverrides.LlmModelVariable), _console.Output);
    }

    [Fact]
    public async Task NonInteractiveConsole_PrintsTheGuard_NotAnException()
    {
        using var plain = new TestConsole();
        plain.Profile.Width = 200;
        var transcript = new TranscriptRenderer(plain);
        var menu = new SettingsMenu(plain, _settings, _ => null, new InputLine(plain, new KeySource(plain.Input)), transcript, _speech, NoPane(plain));

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));
        Assert.False(await menu.PickModelAsync(Session(), "", CancellationToken.None));
        Assert.Contains(SettingsMenu.MenusNeedTerminalError, plain.Output);
    }

    [Fact]
    public void Labels_ArePinned()
    {
        var data = new AppSettingsData { LlmApiKey = "sk-secret", LlmUrl = "" };
        // The label column is the longest name plus two spaces, computed ("STT interrupt echo guard" once touched "100 %" when it was a hand-kept number).
        int longest = Enum.GetValues<SettingsField>().Max(f => SettingsMenu.FieldName(f).Length);
        Assert.Equal(36, longest);   // "Use external skills (.agents\\skills)" (2026-09-16; "Ask max choices per question", 28, before)
        Assert.Equal(38, SettingsMenu.LabelWidth);
        Assert.Equal(longest + 2, SettingsMenu.LabelWidth);
        Assert.Equal("LLM request timeout (s)", SettingsMenu.FieldName(SettingsField.LlmRequestTimeoutSeconds));
        Assert.Equal("LLM turn timeout (s)", SettingsMenu.FieldName(SettingsField.LlmTurnTimeoutSeconds));
        Assert.Equal("LLM URL                               [#EFE6FF](probe local ports)[/]", SettingsMenu.FieldLabel(SettingsField.LlmUrl, data, _settings.ProfileDirectory, null));
        Assert.Equal("LLM API key                           [#EFE6FF]sk•••••••[/][#9A8BB8]  (overridden by X)[/]", SettingsMenu.FieldLabel(SettingsField.LlmApiKey, data, _settings.ProfileDirectory, "X"));
        Assert.Equal("LLM request timeout (s)               [#EFE6FF]3600[/]", SettingsMenu.FieldLabel(SettingsField.LlmRequestTimeoutSeconds, data, _settings.ProfileDirectory, null));
        Assert.Equal("(none)", SettingsMenu.Mask(""));
        Assert.Equal("••••", SettingsMenu.Mask("abcd"));
        Assert.Equal("3600", SettingsMenu.FieldValue(SettingsField.LlmRequestTimeoutSeconds, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.TtsOutput, data, _settings.ProfileDirectory));   // off by default
        Assert.Equal("1.2", SettingsMenu.FieldValue(SettingsField.TtsSpeed, data, _settings.ProfileDirectory));   // 1.0 until 2026-09-18
        Assert.Equal("1.25", SettingsMenu.Speed(1.25));
        Assert.True(SettingsMenu.IsLlmField(SettingsField.LlmApiKey));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.TtsVoice));
        Assert.True(SettingsMenu.IsTtsField(SettingsField.TtsSpeed));
        Assert.True(SettingsMenu.IsTtsField(SettingsField.TtsOutput));
        Assert.False(SettingsMenu.IsTtsField(SettingsField.SttInput));
        Assert.True(SettingsMenu.IsVoiceField(SettingsField.SttInput));
        Assert.True(SettingsMenu.IsVoiceField(SettingsField.SttPushToTalkKey));
        Assert.True(SettingsMenu.IsVoiceField(SettingsField.SttWhisperModel));
        Assert.True(SettingsMenu.IsVoiceField(SettingsField.SttVoskModel));
        Assert.False(SettingsMenu.IsToggle(SettingsField.SttVoskModel));
        Assert.Equal("STT vosk model", SettingsMenu.FieldName(SettingsField.SttVoskModel));
        Assert.Equal("vosk-model-small-en-us-0.15", SettingsMenu.FieldValue(SettingsField.SttVoskModel, data, _settings.ProfileDirectory));
        Assert.True(SettingsMenu.IsVoiceField(SettingsField.SttWakePhrase));
        Assert.True(SettingsMenu.IsVoiceField(SettingsField.SttWake));
        Assert.Equal("ggml-base.en.bin", SettingsMenu.FieldValue(SettingsField.SttWhisperModel, data, _settings.ProfileDirectory));
        // The menu order, top to bottom: the row order is the enum order and every Down(n) above counts on it.
        Assert.Equal(
            new[]
            {
                SettingsField.Profile, SettingsField.Memory, SettingsField.LlmUrl, SettingsField.LlmModel, SettingsField.LlmApiKey, SettingsField.LlmReasoning, SettingsField.LlmRequestTimeoutSeconds,
                SettingsField.LlmTurnTimeoutSeconds, SettingsField.TtsOutput, SettingsField.TtsHttpUrl, SettingsField.TtsVoicePreset, SettingsField.TtsVoice, SettingsField.TtsVoice2,
                SettingsField.TtsVoiceMix, SettingsField.TtsSpeed,
                SettingsField.SttInput, SettingsField.SttWake, SettingsField.SttWakePhrase,
                SettingsField.SttInterrupt, SettingsField.SttInterruptEchoGuard, SettingsField.SttInterruptConfirmMs, SettingsField.SttPushToTalkKey, SettingsField.SttWhisperModel,
                SettingsField.WorkingDirectory, SettingsField.CopyUserPrompt, SettingsField.LlmContextLength, SettingsField.ShowImageThumbnails,
                SettingsField.LlmCompactType, SettingsField.LlmCompactKeepRecent, SettingsField.LlmAutoCompactPercent, SettingsField.LlmMaxTurns, SettingsField.LlmToolCompactType, SettingsField.LlmOfferTools, SettingsField.LlmMaxToolIterations, SettingsField.ImageThumbnailSize,
                SettingsField.FileTreeMaxLength, SettingsField.FileTreeShowSizes, SettingsField.NewProfileMode, SettingsField.LlmUseFunVerbs, SettingsField.LlmScanMode,
                SettingsField.WebTools, SettingsField.WebBrowserMode, SettingsField.WebBrowserPath, SettingsField.WebBrowserNetworkMode, SettingsField.WebSearxngUrl, SettingsField.WebSearchMaxResults,
                SettingsField.TtsVoicePreview, SettingsField.FileTools, SettingsField.WebSearchMethod,
                SettingsField.AskUser, SettingsField.AskMaxQuestions, SettingsField.AskMaxChoices, SettingsField.TtsSource, SettingsField.SttVoskModel, SettingsField.FileMentionFolderMode,
                SettingsField.AgentSkills, SettingsField.ExternalSkills, SettingsField.SkillCompactMode, SettingsField.TranscriptMarkdown, SettingsField.PastePreviewLines,
                SettingsField.FileSafeEdits, SettingsField.SkillHashMention,
                SettingsField.ReflectionAutoLearn, SettingsField.ReflectionReasoning, SettingsField.ReflectionWindow, SettingsField.ReflectionMinToolCalls, SettingsField.ReflectionMaxRequests,
                SettingsField.HideExitAutocomplete, SettingsField.CommandTypoIntercept, SettingsField.WelcomeSplash, SettingsField.ShowWorkingDirectory,
                SettingsField.QueueMessages, SettingsField.QueueCancelMode,
                SettingsField.SessionLogging, SettingsField.SessionRetentionDays, SettingsField.SessionNamingMode, SettingsField.SessionShowName, SettingsField.SessionTool, SettingsField.SessionSearchMaxResults,
                SettingsField.ToolsDollarMention, SettingsField.ReflectionCooldownMinutes, SettingsField.ReflectionIncludesSessions, SettingsField.ReflectionCooldownMode,
                SettingsField.DraftEditor, SettingsField.FileViewImageMaxPerCall,
                SettingsField.McpServers, SettingsField.McpConnectTimeoutSeconds, SettingsField.GitNativeTools, SettingsField.GitNativeDiffMaxLines, SettingsField.GitNativeLogMaxCommits,
                SettingsField.ShellCommandPolicy, SettingsField.ShellCommandAllowed, SettingsField.ShellDefault, SettingsField.ShellTimeoutSeconds, SettingsField.ShellForegroundCapSeconds, SettingsField.ShellOutputMaxChars,
                SettingsField.ShellCodeLanguages, SettingsField.ShellCodeTimeoutSeconds, SettingsField.ShellCodeMaxToolCalls,
                SettingsField.LlmCompactShowSummary, SettingsField.GitNativeEmail, SettingsField.GitNativeName, SettingsField.ShellToolBridge, SettingsField.FileBrowserMode, SettingsField.ToolbarItems, SettingsField.ShellPoliceOutsidePaths,
                SettingsField.ToolCollapseCount, SettingsField.CodeCollapseCount,
                SettingsField.ObsidianTools, SettingsField.ObsidianVault, SettingsField.ObsidianAllowDelete,
                SettingsField.SqlTools, SettingsField.SqlDefaultConnection, SettingsField.SqlQueryMaxRows, SettingsField.SqlQueryTimeoutSeconds, SettingsField.SqlConnectionsProfile, SettingsField.SqlConnectionsGlobal, SettingsField.SqlSetPassword, SettingsField.SqlPercentMention, SettingsField.SqlConnectionsOffered, SettingsField.SqlAddConnection, SettingsField.Theme,
                SettingsField.ComfyTools, SettingsField.ComfyUrl, SettingsField.ComfyTimeoutSeconds, SettingsField.ComfyOutputFolder, SettingsField.ComfyWorkflowsOffered, SettingsField.ComfyAddWorkflow, SettingsField.ImageEditor, SettingsField.ComfyMaxPicturesPerCall, SettingsField.ComfyReinforceNegatives, SettingsField.ComfyShowPrompts, SettingsField.ComfyCaretMention, SettingsField.ComfyPictureStrip,
                SettingsField.ReflectionYieldsToTurns,
                SettingsField.BotChatImages, SettingsField.BotChatImageMode, SettingsField.BotChatTxt2ImgWorkflow, SettingsField.BotChatImageAsync,
                SettingsField.BotChatLlmMode,
                SettingsField.LlmMidTurnUsage,
                SettingsField.KeepCommandHistory,
                SettingsField.BotChatNonTtsDelaySeconds,
                SettingsField.ShellPreferNative,
                SettingsField.LlmShowThinking,
                SettingsField.ClaudeExecutable, SettingsField.ClaudePermissions, SettingsField.ClaudeModel, SettingsField.ClaudeEffort,
                SettingsField.ClaudeAdvisor, SettingsField.ClaudeAdvisorContext, SettingsField.ClaudeAdvisorCallsPerTurn, SettingsField.ClaudeAdvisorModel, SettingsField.ClaudeAdvisorEffort, SettingsField.ClaudeAdvisorConfirm,
                SettingsField.ClaudeApi, SettingsField.ClaudeApiKey, SettingsField.ClaudeApiMaxTokens, SettingsField.ClaudeApiPromptCaching,
                SettingsField.BotChatSkills,
                SettingsField.BotChatVision,
                SettingsField.ThemedViewer,
                SettingsField.ReflectionEditsSupportingFiles,
                SettingsField.BotChatImg2ImgWorkflow, SettingsField.BotChatImg2ImgMode,
                SettingsField.BotChatPreloadedSkills, SettingsField.BotChatSkillMode,
                SettingsField.LlmPreserveThinking, SettingsField.SessionSaveThinking, SettingsField.LlmSampling, SettingsField.LlmSamplingFromHuggingFace,
                SettingsField.HomeAssistantTools, SettingsField.HomeAssistantUrl, SettingsField.HomeAssistantToken, SettingsField.HomeAssistantTest, SettingsField.HomeAssistantActionPolicy, SettingsField.HomeAssistantAssistAgent, SettingsField.HomeAssistantTimeoutSeconds,
                SettingsField.PrintTools, SettingsField.PrintActionPolicy, SettingsField.PrintDefaultPrinter, SettingsField.PrintFontSize,
                SettingsField.EmbeddedModels, SettingsField.EmbeddedBackend, SettingsField.EmbeddedContextSize, SettingsField.EmbeddedGpuLayers, SettingsField.EmbeddedVision,
                SettingsField.LlmReasoningEstimate,
            },
            Enum.GetValues<SettingsField>());
        // The compact rows: on the LLM tab after the context length but no reconnect; the type a picker, the two others typed.
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmCompactType));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmCompactKeepRecent));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmAutoCompactPercent));
        // The mid-turn usage picker (2026-09-25): under the context length, no reconnect, last-known by default (estimate until later that day).
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmMidTurnUsage));
        Assert.False(SettingsMenu.IsToggle(SettingsField.LlmMidTurnUsage));
        Assert.Equal("LLM mid-turn usage", SettingsMenu.FieldName(SettingsField.LlmMidTurnUsage));
        Assert.Equal("last-known", SettingsMenu.FieldValue(SettingsField.LlmMidTurnUsage, data, _settings.ProfileDirectory));
        Assert.Equal("estimate", SettingsMenu.FieldValue(SettingsField.LlmMidTurnUsage, new AppSettingsData { LlmMidTurnUsage = "estimate" }, _settings.ProfileDirectory));
        // Keep command history (2026-09-25): a General toggle under the typo intercept, on by default, no reconnect.
        Assert.True(SettingsMenu.IsToggle(SettingsField.KeepCommandHistory));
        Assert.Equal("Keep command history", SettingsMenu.FieldName(SettingsField.KeepCommandHistory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.KeepCommandHistory, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.KeepCommandHistory, new AppSettingsData { KeepCommandHistory = false }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.KeepCommandHistory));
        Assert.Equal("command history enabled", SettingsMenu.ToggleDescribe(SettingsField.KeepCommandHistory, true));
        Assert.Equal("command history disabled", SettingsMenu.ToggleDescribe(SettingsField.KeepCommandHistory, false));
        Assert.Equal(SettingsField.KeepCommandHistory, SettingsMenu.TabFields[(int)SettingsTab.General][SettingsMenu.TabFields[(int)SettingsTab.General].ToList().IndexOf(SettingsField.CommandTypoIntercept) + 1]);
        // The show-summary toggle (2026-09-21): right under keep recent, no reconnect, off by default.
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmCompactShowSummary));
        Assert.True(SettingsMenu.IsToggle(SettingsField.LlmCompactShowSummary));
        Assert.Equal("LLM compact show summary", SettingsMenu.FieldName(SettingsField.LlmCompactShowSummary));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.LlmCompactShowSummary, data, _settings.ProfileDirectory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.LlmCompactShowSummary, new AppSettingsData { LlmCompactShowSummary = true }, _settings.ProfileDirectory));
        Assert.Equal("the summary's lines or the pruned results, then the protected counts", SettingsMenu.ToggleDescribe(SettingsField.LlmCompactShowSummary, true));
        Assert.Equal("the one compact notice alone", SettingsMenu.ToggleDescribe(SettingsField.LlmCompactShowSummary, false));
        // The LLM tab's tail (2026-09-15, the user's order): the tools toggle ABOVE the tool-compact picker, then the cap, then the fun verbs.
        Assert.Equal(new[] { SettingsField.LlmContextLength, SettingsField.LlmMidTurnUsage, SettingsField.LlmCompactType, SettingsField.LlmCompactKeepRecent, SettingsField.LlmCompactShowSummary, SettingsField.LlmAutoCompactPercent, SettingsField.LlmMaxTurns, SettingsField.LlmOfferTools, SettingsField.LlmToolCompactType, SettingsField.LlmMaxToolIterations, SettingsField.LlmUseFunVerbs, SettingsField.LlmShowThinking, SettingsField.LlmPreserveThinking, SettingsField.LlmReasoningEstimate, SettingsField.LlmSampling, SettingsField.LlmSamplingFromHuggingFace }, SettingsMenu.TabFields[(int)SettingsTab.Llm].TakeLast(16));   // LLM reasoning estimate under preserve thinking since 2026-09-29;   // LLM sampling last (2026-09-28)
        // The tool-compact row (2026-09-15): a picker under LLM offer tools, no reconnect, read at each turn.
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmToolCompactType));
        Assert.False(SettingsMenu.IsToggle(SettingsField.LlmToolCompactType));
        Assert.Equal("LLM tool compact type", SettingsMenu.FieldName(SettingsField.LlmToolCompactType));
        Assert.Equal("compact", SettingsMenu.FieldValue(SettingsField.LlmToolCompactType, data, _settings.ProfileDirectory));
        Assert.Equal("compact [#9A8BB8]prune, then summarise if the turn is still over the share[/]", SettingsMenu.ToolCompactTypeLabel("compact"));
        Assert.Equal("stop", SettingsMenu.FieldValue(SettingsField.LlmToolCompactType, new AppSettingsData { LlmToolCompactType = "stop" }, _settings.ProfileDirectory));
        Assert.Equal("prune   [#9A8BB8]stub this turn's older tool results and carry on[/]", SettingsMenu.ToolCompactTypeLabel("prune"));
        Assert.Equal("stop    [#9A8BB8]end the turn with a notice; /compact or /clear first[/]", SettingsMenu.ToolCompactTypeLabel("stop"));
        Assert.Equal("nothing [#9A8BB8]no check; the server's own limit answers[/]", SettingsMenu.ToolCompactTypeLabel("nothing"));
        // The turn-loop rows: on the LLM tab, no reconnect — the tools a toggle whose flip clears the conversation, the cap typed.
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmOfferTools));
        Assert.True(SettingsMenu.IsToggle(SettingsField.LlmOfferTools));
        Assert.Equal("LLM offer tools", SettingsMenu.FieldName(SettingsField.LlmOfferTools));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.LlmOfferTools, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.LlmOfferTools, new AppSettingsData { LlmOfferTools = false }, _settings.ProfileDirectory));
        Assert.Equal(16, (int)SettingsChanges.Conversation);
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmMaxToolIterations));
        Assert.False(SettingsMenu.IsToggle(SettingsField.LlmMaxToolIterations));
        Assert.Equal("LLM max tool iterations", SettingsMenu.FieldName(SettingsField.LlmMaxToolIterations));
        Assert.Equal("10000 round trips", SettingsMenu.FieldValue(SettingsField.LlmMaxToolIterations, data, _settings.ProfileDirectory));
        Assert.Equal("1 round trip", SettingsMenu.FieldValue(SettingsField.LlmMaxToolIterations, new AppSettingsData { LlmMaxToolIterations = 1 }, _settings.ProfileDirectory));
        Assert.Equal("10000", SettingsMenu.EditableValue(SettingsField.LlmMaxToolIterations, data));
        Assert.Equal("must be 1 to 10000 round trips", SettingsMenu.MaxToolIterationsRangeError);
        Assert.Equal("must be a number of seconds in (0, 3600]", SettingsMenu.LlmRequestTimeoutRangeError);
        Assert.Equal("must be a number of seconds in (0, 21600]", SettingsMenu.LlmTurnTimeoutRangeError);
        Assert.Equal("21600", SettingsMenu.FieldValue(SettingsField.LlmTurnTimeoutSeconds, data, _settings.ProfileDirectory));
        Assert.Equal("LLM compact type", SettingsMenu.FieldName(SettingsField.LlmCompactType));
        Assert.Equal("LLM compact keep recent", SettingsMenu.FieldName(SettingsField.LlmCompactKeepRecent));
        Assert.Equal("LLM auto compact (%)", SettingsMenu.FieldName(SettingsField.LlmAutoCompactPercent));
        Assert.Equal("summary", SettingsMenu.FieldValue(SettingsField.LlmCompactType, data, _settings.ProfileDirectory));
        Assert.Equal("2 turns", SettingsMenu.FieldValue(SettingsField.LlmCompactKeepRecent, data, _settings.ProfileDirectory));
        Assert.Equal("1 turn", SettingsMenu.FieldValue(SettingsField.LlmCompactKeepRecent, new AppSettingsData { LlmCompactKeepRecent = 1 }, _settings.ProfileDirectory));
        Assert.Equal("85 %", SettingsMenu.FieldValue(SettingsField.LlmAutoCompactPercent, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.LlmAutoCompactPercent, new AppSettingsData { LlmAutoCompactPercent = 0 }, _settings.ProfileDirectory));
        Assert.Equal("2", SettingsMenu.EditableValue(SettingsField.LlmCompactKeepRecent, data));
        Assert.Equal("85", SettingsMenu.EditableValue(SettingsField.LlmAutoCompactPercent, data));
        Assert.Equal("must be 0 to 24 turns", SettingsMenu.LlmCompactKeepRecentRangeError);
        Assert.Equal("LLM max turns", SettingsMenu.FieldName(SettingsField.LlmMaxTurns));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmMaxTurns));
        Assert.Equal("auto", SettingsMenu.FieldValue(SettingsField.LlmMaxTurns, data, _settings.ProfileDirectory));
        Assert.Equal("50 turns", SettingsMenu.FieldValue(SettingsField.LlmMaxTurns, new AppSettingsData { LlmMaxTurns = 50 }, _settings.ProfileDirectory));
        Assert.Equal("auto", SettingsMenu.EditableValue(SettingsField.LlmMaxTurns, data));
        Assert.Equal("50", SettingsMenu.EditableValue(SettingsField.LlmMaxTurns, new AppSettingsData { LlmMaxTurns = 50 }));
        Assert.Equal("must be auto (0) or 1 to 500 turns", SettingsMenu.LlmMaxTurnsRangeError);
        Assert.Equal("summary [#9A8BB8]summarise the older turns into one message, keep the recent ones[/]", SettingsMenu.CompactTypeLabel("summary"));
        Assert.Equal("prune   [#9A8BB8]stub the bulky tool results in the older turns, keep every turn[/]", SettingsMenu.CompactTypeLabel("prune"));
        // The context length: an LLM row (a reconnect), typed, 0 for the server's own figure.
        Assert.True(SettingsMenu.IsLlmField(SettingsField.LlmContextLength));
        Assert.False(SettingsMenu.IsToggle(SettingsField.LlmContextLength));
        Assert.Equal("LLM context length", SettingsMenu.FieldName(SettingsField.LlmContextLength));
        Assert.Equal("(from the server)", SettingsMenu.FieldValue(SettingsField.LlmContextLength, data, _settings.ProfileDirectory));
        Assert.Equal("0", SettingsMenu.EditableValue(SettingsField.LlmContextLength, data));
        Assert.Equal("32,768 tokens", SettingsMenu.FieldValue(SettingsField.LlmContextLength, new AppSettingsData { LlmContextLength = 32_768 }, _settings.ProfileDirectory));
        Assert.Equal("32768", SettingsMenu.EditableValue(SettingsField.LlmContextLength, new AppSettingsData { LlmContextLength = 32_768 }));
        Assert.Equal(SettingsField.LlmContextLength, SettingsMenu.TabFields[(int)SettingsTab.Llm][^16]);   // the reasoning estimate since 2026-09-29; the mid-turn usage picker, the four compact rows, the turn cap, the tools, the tool-compact picker, the cap, the fun verbs, show thinking, preserve thinking, sampling and sampling from Hugging Face follow it
        // The pane's tabs (five since 2026-09-19: Ask, Files and Web are /tools' tabs, Skills is /skills' Options tab): General, Sessions, LLM in their own order, TTS / STT the enum order of their session's fields; every field on exactly one tab of the three panes.
        Assert.Equal(["General", "Sessions", "LLM", "TTS", "STT", "Claude (API)", "Embedded", "Botchat"], SettingsMenu.TabTitles);   // Embedded LLM since 2026-09-29; Claude (API) before Botchat since later on 2026-09-27; Claude last since 2026-09-27; Botchat last from 2026-09-25;   // Sessions right after General (2026-09-18); Web last until 2026-09-19, Skills third until later that day
        // The Options tab of /skills (2026-09-19; the Skills tab of /settings from 2026-09-16 until then): the skills switch, the external-folder switch and the compact-mode picker, then (2026-09-17) the #-mention switch, the delete switch, then the auto-learn switch and the reflection rows; none a reconnect.
        Assert.Equal([SettingsField.AgentSkills, SettingsField.ExternalSkills, SettingsField.SkillCompactMode, SettingsField.SkillHashMention], SettingsMenu.SkillsTabFields[0]);   // the Options tab; the reflection rows on their own tab since later on 2026-09-19
        Assert.Equal([SettingsField.ReflectionAutoLearn, SettingsField.ReflectionReasoning, SettingsField.ReflectionWindow, SettingsField.ReflectionMinToolCalls, SettingsField.ReflectionMaxRequests, SettingsField.ReflectionCooldownMinutes, SettingsField.ReflectionCooldownMode, SettingsField.ReflectionIncludesSessions, SettingsField.ReflectionYieldsToTurns, SettingsField.ReflectionEditsSupportingFiles], SettingsMenu.SkillsTabFields[1]);   // the Reflection tab: the cooldown, its mode and the sessions switch (last, the user's place) since 2026-09-19
        // Reflection min tool calls (2026-09-17): the tab's last row and the enum's last member, typed 3–20; the error door stays one recovered error.
        Assert.False(SettingsMenu.IsToggle(SettingsField.ReflectionMinToolCalls));
        Assert.Equal("Reflection min tool calls", SettingsMenu.FieldName(SettingsField.ReflectionMinToolCalls));
        Assert.Equal("4 tool calls", SettingsMenu.FieldValue(SettingsField.ReflectionMinToolCalls, data, _settings.ProfileDirectory));
        Assert.Equal("1 tool call", SettingsMenu.ToolCalls(1));
        Assert.Equal("4", SettingsMenu.EditableValue(SettingsField.ReflectionMinToolCalls, data));
        Assert.Equal("must be 3 to 20 tool calls", SettingsMenu.ReflectionMinToolCallsRangeError);
        // Reflection max requests (2026-09-17): the enum's last member, the tab's tenth row above the verbose switch, typed 1–20; the reflection's own cap.
        Assert.False(SettingsMenu.IsToggle(SettingsField.ReflectionMaxRequests));
        Assert.Equal("Reflection max requests", SettingsMenu.FieldName(SettingsField.ReflectionMaxRequests));
        Assert.Equal("4 requests", SettingsMenu.FieldValue(SettingsField.ReflectionMaxRequests, data, _settings.ProfileDirectory));
        Assert.Equal("1 request", SettingsMenu.Requests(1));
        Assert.Equal("4", SettingsMenu.EditableValue(SettingsField.ReflectionMaxRequests, data));
        Assert.Equal("must be 1 to 20 requests", SettingsMenu.ReflectionMaxRequestsRangeError);
        // Reflection cooldown (minutes) and Reflection includes sessions (2026-09-19): the enum's last members, the tab's rows after the
        // request cap and last; the cooldown typed 0 (off) to 1440, the sessions switch a toggle, neither a reconnect.
        Assert.False(SettingsMenu.IsToggle(SettingsField.ReflectionCooldownMinutes));
        Assert.True(SettingsMenu.IsToggle(SettingsField.ReflectionIncludesSessions));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ReflectionCooldownMinutes));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ReflectionIncludesSessions));
        Assert.Equal("Reflection cooldown (minutes)", SettingsMenu.FieldName(SettingsField.ReflectionCooldownMinutes));
        Assert.Equal("Reflection includes sessions", SettingsMenu.FieldName(SettingsField.ReflectionIncludesSessions));
        Assert.Equal("5 minutes", SettingsMenu.FieldValue(SettingsField.ReflectionCooldownMinutes, data, _settings.ProfileDirectory));   // 30 for an hour on 2026-09-19
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ReflectionCooldownMinutes, new AppSettingsData { ReflectionCooldownMinutes = 0 }, _settings.ProfileDirectory));
        Assert.Equal("1 minute", SettingsMenu.Minutes(1));
        Assert.Equal("5", SettingsMenu.EditableValue(SettingsField.ReflectionCooldownMinutes, data));
        // Reflection cooldown mode (2026-09-19, the user's call): a picker, the row after the minutes, last-written-skill by default.
        Assert.False(SettingsMenu.IsToggle(SettingsField.ReflectionCooldownMode));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ReflectionCooldownMode));
        Assert.Equal("Reflection cooldown mode", SettingsMenu.FieldName(SettingsField.ReflectionCooldownMode));
        Assert.Equal("last-written-skill", SettingsMenu.FieldValue(SettingsField.ReflectionCooldownMode, data, _settings.ProfileDirectory));
        Assert.Equal("all-skills          [#9A8BB8]after any skill is written, every automatic reflection waits out the cooldown[/]", SettingsMenu.ReflectionCooldownModeLabel("all-skills"));
        Assert.Equal("last-written-skill  [#9A8BB8]only a turn that loaded the skill just written waits; another lesson reflects at once[/]", SettingsMenu.ReflectionCooldownModeLabel("last-written-skill"));
        Assert.Equal("must be 0 (off) or 1 to 1440 minutes", SettingsMenu.ReflectionCooldownMinutesRangeError);
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ReflectionIncludesSessions, data, _settings.ProfileDirectory));
        Assert.Equal("a reflection can read past sessions for insights", SettingsMenu.ToggleDescribe(SettingsField.ReflectionIncludesSessions, true));
        Assert.Equal("a reflection reads the conversation on screen alone", SettingsMenu.ToggleDescribe(SettingsField.ReflectionIncludesSessions, false));
        // Reflection yields to turns (2026-09-24): the tab's last row, a toggle, on by default, never refused mid-turn.
        Assert.True(SettingsMenu.IsToggle(SettingsField.ReflectionYieldsToTurns));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ReflectionYieldsToTurns));
        Assert.Equal("Reflection yields to turns", SettingsMenu.FieldName(SettingsField.ReflectionYieldsToTurns));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ReflectionYieldsToTurns, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ReflectionYieldsToTurns, new AppSettingsData { ReflectionYieldsToTurns = false }, _settings.ProfileDirectory));
        Assert.Equal("a turn pauses a running reflection which may resume after the turn", SettingsMenu.ToggleDescribe(SettingsField.ReflectionYieldsToTurns, true));
        Assert.Equal("reflections run asynchronously (when the model allows)", SettingsMenu.ToggleDescribe(SettingsField.ReflectionYieldsToTurns, false));
        // Reflection edit supporting files (2026-09-27): the tab's last row, a toggle, off by default, never refused mid-turn.
        Assert.True(SettingsMenu.IsToggle(SettingsField.ReflectionEditsSupportingFiles));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ReflectionEditsSupportingFiles));
        Assert.Equal("Reflection edit supporting files", SettingsMenu.FieldName(SettingsField.ReflectionEditsSupportingFiles));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ReflectionEditsSupportingFiles, data, _settings.ProfileDirectory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ReflectionEditsSupportingFiles, new AppSettingsData { ReflectionEditsSupportingFiles = true }, _settings.ProfileDirectory));
        Assert.Equal("a reflection may write a skill's supporting files", SettingsMenu.ToggleDescribe(SettingsField.ReflectionEditsSupportingFiles, true));
        Assert.Equal("a reflection writes a skill's SKILL.md alone", SettingsMenu.ToggleDescribe(SettingsField.ReflectionEditsSupportingFiles, false));
        // Reflection window (2026-09-17): the tab's last row and the enum's last member, typed 1–5 like the compact keep-recent count.
        Assert.False(SettingsMenu.IsToggle(SettingsField.ReflectionWindow));
        Assert.Equal("Reflection window", SettingsMenu.FieldName(SettingsField.ReflectionWindow));
        Assert.Equal("3 turns", SettingsMenu.FieldValue(SettingsField.ReflectionWindow, data, _settings.ProfileDirectory));
        Assert.Equal("1 turn", SettingsMenu.FieldValue(SettingsField.ReflectionWindow, new AppSettingsData { ReflectionWindow = 1 }, _settings.ProfileDirectory));
        Assert.Equal("3", SettingsMenu.EditableValue(SettingsField.ReflectionWindow, data));
        Assert.Equal("must be 1 to 5 turns", SettingsMenu.ReflectionWindowRangeError);
        // Reflection (auto-learn) + Reflection reasoning (2026-09-17): the tab's last two rows and the enum's last two members — a switch and a picker, no reconnect.
        Assert.True(SettingsMenu.IsToggle(SettingsField.ReflectionAutoLearn));
        Assert.False(SettingsMenu.IsToggle(SettingsField.ReflectionReasoning));
        Assert.Equal("Reflection (auto-learn)", SettingsMenu.FieldName(SettingsField.ReflectionAutoLearn));
        Assert.Equal("Reflection reasoning", SettingsMenu.FieldName(SettingsField.ReflectionReasoning));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ReflectionAutoLearn, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ReflectionAutoLearn, new AppSettingsData { ReflectionAutoLearn = false }, _settings.ProfileDirectory));
        Assert.Equal("none", SettingsMenu.FieldValue(SettingsField.ReflectionReasoning, data, _settings.ProfileDirectory));
        Assert.Equal("high", SettingsMenu.FieldValue(SettingsField.ReflectionReasoning, new AppSettingsData { ReflectionReasoning = "high" }, _settings.ProfileDirectory));
        Assert.Equal("profile [#9A8BB8]the profile's LLM reasoning level[/]", SettingsMenu.ReflectionReasoningLabel("profile"));
        Assert.Equal("xhigh   [#9A8BB8]maximum thinking, slowest[/]", SettingsMenu.ReflectionReasoningLabel("xhigh"));
        Assert.Equal("automatic reflection enabled", SettingsMenu.ToggleDescribe(SettingsField.ReflectionAutoLearn, true));
        Assert.Equal("automatic reflection disabled", SettingsMenu.ToggleDescribe(SettingsField.ReflectionAutoLearn, false));
        Assert.Equal(2, (int)SettingsTab.Llm);   // third since 2026-09-19 (Skills sat between from 2026-09-18 until then; the Options tab of /skills now)
        Assert.True(SettingsMenu.IsToggle(SettingsField.AgentSkills) && SettingsMenu.IsToggle(SettingsField.ExternalSkills));
        Assert.True(SettingsMenu.IsToggle(SettingsField.SkillHashMention));
        Assert.Equal("#-mention enabled", SettingsMenu.FieldName(SettingsField.SkillHashMention));
        Assert.False(SettingsMenu.IsToggle(SettingsField.SkillCompactMode));
        Assert.All(SettingsMenu.SkillsTabFields.SelectMany(t => t), f => Assert.False(SettingsMenu.IsLlmField(f) || SettingsMenu.IsTtsField(f) || SettingsMenu.IsVoiceField(f) || SettingsMenu.RefusedMidTurn(f)));
        Assert.Equal("Agent skills", SettingsMenu.FieldName(SettingsField.AgentSkills));
        Assert.Equal("Use external skills (.agents\\skills)", SettingsMenu.FieldName(SettingsField.ExternalSkills));
        Assert.Equal(SettingsMenu.ExternalSkillsName, SettingsMenu.FieldName(SettingsField.ExternalSkills));
        Assert.Equal("Skill compact mode", SettingsMenu.FieldName(SettingsField.SkillCompactMode));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.AgentSkills, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.AgentSkills, new AppSettingsData { AgentSkills = false }, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ExternalSkills, data, _settings.ProfileDirectory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ExternalSkills, new AppSettingsData { ExternalSkills = true }, _settings.ProfileDirectory));
        Assert.Equal("protected", SettingsMenu.FieldValue(SettingsField.SkillCompactMode, data, _settings.ProfileDirectory));
        Assert.Equal("unprotected", SettingsMenu.FieldValue(SettingsField.SkillCompactMode, new AppSettingsData { SkillCompactMode = "unprotected" }, _settings.ProfileDirectory));
        Assert.Equal("protected   [#9A8BB8]loaded skills survive a prune and the mid-turn guard[/]", SettingsMenu.SkillCompactModeLabel("protected"));
        Assert.Equal("unprotected [#9A8BB8]loaded skills prune like any tool result[/]", SettingsMenu.SkillCompactModeLabel("unprotected"));
        Assert.Equal(8, SettingsMenu.TabFields.Count);   // Embedded LLM since 2026-09-29; Claude (API) since later on 2026-09-27; Claude on 2026-09-27 until later that day (to /tools); Botchat since 2026-09-25; 9 until 2026-09-19, when Ask, Files and Web moved to /tools (ToolsTabFields) and, later that day, Skills to /skills (SkillsTabFields)
        Assert.Equal(12, SettingsMenu.ToolsTabFields.Count);   // Print since later on 2026-09-28; Home Assistant since 2026-09-28; Claude since 2026-09-27; Images since 2026-09-24; SQL since 2026-09-23; Obsidian since 2026-09-22 and Options last later that day (first since later on 2026-09-19); Git between Files and Web since 2026-09-20; Shell between Git and Web since 2026-09-21
        Assert.Equal(2, SettingsMenu.SkillsTabFields.Count);   // Options and Reflection, since later on 2026-09-19 (one list of 11, then 14, before)
        Assert.Equal(14, SettingsMenu.SkillsTabFields.Sum(t => t.Count));   // 13 until Reflection edit supporting files came on 2026-09-27; 12 until Reflection yields to turns came on 2026-09-24; 13 until Allow skill delete went on 2026-09-23; 14 until Reflection verbose went later still on 2026-09-19
        Assert.Equal(new[] { SettingsField.Profile, SettingsField.NewProfileMode, SettingsField.WorkingDirectory, SettingsField.QueueMessages, SettingsField.QueueCancelMode, SettingsField.Memory, SettingsField.CopyUserPrompt, SettingsField.ShowImageThumbnails, SettingsField.ImageThumbnailSize, SettingsField.TranscriptMarkdown, SettingsField.PastePreviewLines, SettingsField.HideExitAutocomplete, SettingsField.CommandTypoIntercept, SettingsField.KeepCommandHistory, SettingsField.WelcomeSplash, SettingsField.ShowWorkingDirectory, SettingsField.ToolbarItems, SettingsField.Theme, SettingsField.DraftEditor, SettingsField.ImageEditor, SettingsField.ThemedViewer }, SettingsMenu.TabFields[(int)SettingsTab.General]);   // Image editor under Draft editor, later on 2026-09-24; Keep command history under the typo intercept, 2026-09-25; Theme under Show toolbar and Themed image viewer last, later on 2026-09-27
        // Draft editor (2026-09-19): typed, the General tab's last row, blank = the shell's default for .txt, no reconnect (read at each /draft).
        Assert.False(SettingsMenu.IsToggle(SettingsField.DraftEditor));
        Assert.Equal("Draft editor", SettingsMenu.FieldName(SettingsField.DraftEditor));
        Assert.Equal(SettingsMenu.DefaultDraftEditorLabel, SettingsMenu.FieldValue(SettingsField.DraftEditor, data, _settings.ProfileDirectory));
        Assert.Equal("code --wait", SettingsMenu.FieldValue(SettingsField.DraftEditor, new AppSettingsData { DraftEditor = "code --wait" }, _settings.ProfileDirectory));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.DraftEditor, data));
        Assert.Equal("code --wait", SettingsMenu.EditableValue(SettingsField.DraftEditor, new AppSettingsData { DraftEditor = "code --wait" }));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.DraftEditor) || SettingsMenu.IsLlmField(SettingsField.DraftEditor) || SettingsMenu.IsTtsField(SettingsField.DraftEditor) || SettingsMenu.IsVoiceField(SettingsField.DraftEditor));
        // The message queue (2026-09-18): a toggle and a picker, the General tab's rows right under Working directory (the user's place), the enum's last members, no reconnect.
        Assert.True(SettingsMenu.IsToggle(SettingsField.QueueMessages));
        Assert.False(SettingsMenu.IsToggle(SettingsField.QueueCancelMode));
        Assert.Equal("Queue messages", SettingsMenu.FieldName(SettingsField.QueueMessages));
        Assert.Equal("Queue cancel mode", SettingsMenu.FieldName(SettingsField.QueueCancelMode));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.QueueMessages, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.QueueMessages, new AppSettingsData { QueueMessages = false }, _settings.ProfileDirectory));
        Assert.Equal("empty", SettingsMenu.FieldValue(SettingsField.QueueCancelMode, data, _settings.ProfileDirectory));
        Assert.Equal("drain", SettingsMenu.FieldValue(SettingsField.QueueCancelMode, new AppSettingsData { QueueCancelMode = "drain" }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.QueueMessages) || SettingsMenu.RefusedMidTurn(SettingsField.QueueCancelMode));
        Assert.Equal("hold  [#9A8BB8]a cancelled reply holds the queue; your next message runs first, then it resumes[/]", SettingsMenu.QueueCancelModeLabel("hold"));
        Assert.Equal("drain [#9A8BB8]a cancelled reply sends the next queued message at once[/]", SettingsMenu.QueueCancelModeLabel("drain"));
        Assert.Equal("empty [#9A8BB8]a cancelled reply drops every queued message[/]", SettingsMenu.QueueCancelModeLabel("empty"));
        // Show working directory (2026-09-18): a toggle, the General tab's last row, no reconnect (read at each banner draw).
        Assert.True(SettingsMenu.IsToggle(SettingsField.ShowWorkingDirectory));
        Assert.Equal("Working directory in header", SettingsMenu.FieldName(SettingsField.ShowWorkingDirectory));   // "Show working directory" until 2026-09-21
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ShowWorkingDirectory, data, _settings.ProfileDirectory));   // off by default since 2026-09-21
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ShowWorkingDirectory, new AppSettingsData { ShowWorkingDirectory = true }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ShowWorkingDirectory) || SettingsMenu.IsLlmField(SettingsField.ShowWorkingDirectory) || SettingsMenu.IsTtsField(SettingsField.ShowWorkingDirectory) || SettingsMenu.IsVoiceField(SettingsField.ShowWorkingDirectory));
        // Themed image viewer (later on 2026-09-27): a toggle, the General tab's last row, on by default, no reconnect.
        Assert.True(SettingsMenu.IsToggle(SettingsField.ThemedViewer));
        Assert.Equal("Themed image viewer", SettingsMenu.FieldName(SettingsField.ThemedViewer));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ThemedViewer, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ThemedViewer, new AppSettingsData { ThemedViewer = false }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ThemedViewer) || SettingsMenu.IsLlmField(SettingsField.ThemedViewer) || SettingsMenu.IsTtsField(SettingsField.ThemedViewer) || SettingsMenu.IsVoiceField(SettingsField.ThemedViewer));
        // Show toolbar (2026-09-21): the General row after it, a toggle, no reconnect (the pane reads it at each draw).
        Assert.False(SettingsMenu.IsToggle(SettingsField.ToolbarItems));   // a checklist since 2026-09-29, the user's ask
        Assert.Equal("Show toolbar", SettingsMenu.FieldName(SettingsField.ToolbarItems));
        Assert.Equal("all", SettingsMenu.FieldValue(SettingsField.ToolbarItems, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ToolbarItems, new AppSettingsData { ToolbarItems = [] }, _settings.ProfileDirectory));
        Assert.Equal("3 of 11", SettingsMenu.FieldValue(SettingsField.ToolbarItems, new AppSettingsData { ToolbarItems = ["usage", "PATH", " tools ", "nonsense"] }, _settings.ProfileDirectory));
        Assert.Equal("all", SettingsMenu.FieldValue(SettingsField.ToolbarItems, new AppSettingsData { ToolbarItems = [.. ToolbarItems.Names] }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ToolbarItems) || SettingsMenu.IsLlmField(SettingsField.ToolbarItems) || SettingsMenu.IsTtsField(SettingsField.ToolbarItems) || SettingsMenu.IsVoiceField(SettingsField.ToolbarItems));
        // Welcome splash (2026-09-18): the General tab's row before Show working directory (the user's order), no reconnect; a picker since 2026-09-24.
        Assert.False(SettingsMenu.IsToggle(SettingsField.WelcomeSplash));
        Assert.Equal("Welcome splash", SettingsMenu.FieldName(SettingsField.WelcomeSplash));
        Assert.Equal("fullsize", SettingsMenu.FieldValue(SettingsField.WelcomeSplash, data, _settings.ProfileDirectory));
        Assert.Equal("tiled", SettingsMenu.FieldValue(SettingsField.WelcomeSplash, new AppSettingsData { WelcomeSplashMode = "tiled" }, _settings.ProfileDirectory));
        Assert.Equal("fullsize [#9A8BB8]one picture fills the screen under the banner at startup; ← → walk them[/]", SettingsMenu.WelcomeSplashModeLabel("fullsize"));
        Assert.Equal("tiled    [#9A8BB8]the pictures as thumbnails at the image thumbnail size, a screenful at a time; ← → page[/]", SettingsMenu.WelcomeSplashModeLabel("tiled"));
        Assert.Equal("disabled [#9A8BB8]the banner alone at startup[/]", SettingsMenu.WelcomeSplashModeLabel("disabled"));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.WelcomeSplash) || SettingsMenu.IsLlmField(SettingsField.WelcomeSplash) || SettingsMenu.IsTtsField(SettingsField.WelcomeSplash) || SettingsMenu.IsVoiceField(SettingsField.WelcomeSplash));
        // The two line switches of 2026-09-18: the General tab's last rows and the last enum members, toggles, no reconnect.
        Assert.True(SettingsMenu.IsToggle(SettingsField.HideExitAutocomplete));
        Assert.True(SettingsMenu.IsToggle(SettingsField.CommandTypoIntercept));
        Assert.Equal("Hide /exit autocomplete", SettingsMenu.FieldName(SettingsField.HideExitAutocomplete));
        Assert.Equal("Command typo intercept", SettingsMenu.FieldName(SettingsField.CommandTypoIntercept));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.HideExitAutocomplete, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.HideExitAutocomplete, new AppSettingsData { HideExitAutocomplete = false }, _settings.ProfileDirectory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.CommandTypoIntercept, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.CommandTypoIntercept, new AppSettingsData { CommandTypoIntercept = false }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.HideExitAutocomplete) || SettingsMenu.RefusedMidTurn(SettingsField.CommandTypoIntercept));
        // Paste preview lines (2026-09-16): the General tab's last row until 2026-09-18, typed, 0 = off, no reconnect, allowed mid-turn.
        Assert.False(SettingsMenu.IsToggle(SettingsField.PastePreviewLines));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.PastePreviewLines));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.PastePreviewLines) || SettingsMenu.IsTtsField(SettingsField.PastePreviewLines) || SettingsMenu.IsVoiceField(SettingsField.PastePreviewLines));
        Assert.Equal("Paste preview lines", SettingsMenu.FieldName(SettingsField.PastePreviewLines));
        Assert.Equal("25 lines", SettingsMenu.FieldValue(SettingsField.PastePreviewLines, data, _settings.ProfileDirectory));
        Assert.Equal("1 line", SettingsMenu.FieldValue(SettingsField.PastePreviewLines, new AppSettingsData { PastePreviewLines = 1 }, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.PastePreviewLines, new AppSettingsData { PastePreviewLines = 0 }, _settings.ProfileDirectory));
        Assert.Equal("25", SettingsMenu.EditableValue(SettingsField.PastePreviewLines, data));
        Assert.Equal("must be 0 to 200 lines", SettingsMenu.PastePreviewLinesRangeError);
        // Transcript markdown (2026-09-16): the General tab's row above it, a toggle on by default, no reconnect, allowed mid-turn.
        Assert.True(SettingsMenu.IsToggle(SettingsField.TranscriptMarkdown));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.TranscriptMarkdown));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.TranscriptMarkdown) || SettingsMenu.IsTtsField(SettingsField.TranscriptMarkdown) || SettingsMenu.IsVoiceField(SettingsField.TranscriptMarkdown));
        Assert.Equal("Transcript markdown", SettingsMenu.FieldName(SettingsField.TranscriptMarkdown));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.TranscriptMarkdown, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.TranscriptMarkdown, new AppSettingsData { TranscriptMarkdown = false }, _settings.ProfileDirectory));
        // The @-mention folder mode (2026-09-16): the Files tab's last row since 2026-09-17 (General's row under the thumbnail size before), a picker, no reconnect, allowed mid-turn.
        Assert.False(SettingsMenu.IsToggle(SettingsField.FileMentionFolderMode));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.FileMentionFolderMode));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.FileMentionFolderMode) || SettingsMenu.IsTtsField(SettingsField.FileMentionFolderMode) || SettingsMenu.IsVoiceField(SettingsField.FileMentionFolderMode));
        Assert.Equal("File @-mention folder mode", SettingsMenu.FieldName(SettingsField.FileMentionFolderMode));
        Assert.Equal("folder-remain", SettingsMenu.FieldValue(SettingsField.FileMentionFolderMode, data, _settings.ProfileDirectory));   // the default since 2026-09-19
        Assert.Equal("folder-apply", SettingsMenu.FieldValue(SettingsField.FileMentionFolderMode, new AppSettingsData { FileMentionFolderMode = "folder-apply" }, _settings.ProfileDirectory));
        Assert.Equal("folder-apply  " + Theme.DimMarkup("insert @folder/ and close the list"), SettingsMenu.MentionFolderModeLabel("folder-apply"));
        Assert.Equal("folder-remain " + Theme.DimMarkup("insert @folder/ and keep listing inside it"), SettingsMenu.MentionFolderModeLabel("folder-remain"));
        // The Files tab (2026-09-15; /tools' second since 2026-09-19): the file-tools switch first, then the Safe edits switch (2026-09-17, on by default until 2026-09-19; the stale-number guard beside it until later that day, when edit_lines went),
        // then the two /tree rows that were General's last two, then the @-mention folder mode (General's until 2026-09-17); none a reconnect, none refused mid-turn (read at each tool call).
        Assert.Equal(new[] { SettingsField.FileTools, SettingsField.FileSafeEdits, SettingsField.FileTreeMaxLength, SettingsField.FileTreeShowSizes, SettingsField.FileMentionFolderMode, SettingsField.FileBrowserMode, SettingsField.FileViewImageMaxPerCall }, SettingsMenu.ToolsTabFields[1]);   // the view_image cap last, 2026-09-19; the browser mode under the folder mode, 2026-09-21
        Assert.True(SettingsMenu.IsToggle(SettingsField.FileTools));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.FileTools));
        Assert.Equal("File tools", SettingsMenu.FieldName(SettingsField.FileTools));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.FileTools, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.FileTools, new AppSettingsData { FileTools = false }, _settings.ProfileDirectory));
        Assert.Equal("File safe edits", SettingsMenu.FieldName(SettingsField.FileSafeEdits));
        foreach (var f in new[] { SettingsField.FileSafeEdits })
        {
            Assert.True(SettingsMenu.IsToggle(f));
            Assert.False(SettingsMenu.RefusedMidTurn(f));
            Assert.False(SettingsMenu.IsLlmField(f) || SettingsMenu.IsTtsField(f) || SettingsMenu.IsVoiceField(f));
            Assert.Equal("off", SettingsMenu.FieldValue(f, data, _settings.ProfileDirectory));   // off by default since 2026-09-19
        }

        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.FileSafeEdits, new AppSettingsData { FileSafeEdits = true }, _settings.ProfileDirectory));
        Assert.Equal(SettingsMenu.ToolsTabFields[1].Max(f => SettingsMenu.FieldName(f).Length) + 2, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[1]));
        // The web rows (2026-09-15): the Web tab (titled Browser until later that day; /tools' last from 2026-09-19, third since later on 2026-09-21), in this order, none a reconnect — one toggle, three pickers (the network mode in the LAN switch's slot since 2026-09-18; the search method above the URL it governs), two typed rows that may be empty, a typed count.
        Assert.Equal(new[] { SettingsField.WebTools, SettingsField.WebBrowserMode, SettingsField.WebBrowserPath, SettingsField.WebBrowserNetworkMode, SettingsField.WebSearchMethod, SettingsField.WebSearxngUrl, SettingsField.WebSearchMaxResults }, SettingsMenu.ToolsTabFields[0]);
        // The shell rows (2026-09-21): the Shell tab (between Git and Web that day, between Files and Ask since later on) — the policy (the group's switch, a picker), the allowed list, the default shell (a picker), then the three typed caps,
        // the languages, their timeout, the tool bridge (the tab's one toggle, later that day) above the tool-call cap it governs; none a reconnect. The outside-paths police (2026-09-22) sits third, under the list it guards beside.
        // Shell prefer native tools (2026-09-26) sits under the police, the other guard in front of the gate.
        Assert.Equal(new[] { SettingsField.ShellCommandPolicy, SettingsField.ShellCommandAllowed, SettingsField.ShellPoliceOutsidePaths, SettingsField.ShellPreferNative, SettingsField.ShellDefault, SettingsField.ShellTimeoutSeconds, SettingsField.ShellForegroundCapSeconds, SettingsField.ShellOutputMaxChars, SettingsField.ShellCodeLanguages, SettingsField.ShellCodeTimeoutSeconds, SettingsField.ShellToolBridge, SettingsField.ShellCodeMaxToolCalls }, SettingsMenu.ToolsTabFields[2]);
        Assert.Equal("Shell tool bridge", SettingsMenu.FieldName(SettingsField.ShellToolBridge));
        Assert.True(SettingsMenu.IsToggle(SettingsField.ShellToolBridge));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.ShellToolBridge));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ShellToolBridge, data, _settings.ProfileDirectory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ShellToolBridge, new AppSettingsData { ShellToolBridge = true }, _settings.ProfileDirectory));
        Assert.Equal("a script may call this app's other tools through its neon_tools module", SettingsMenu.ToggleDescribe(SettingsField.ShellToolBridge, true));
        Assert.Equal("a script does everything itself: no neon_tools module, no tool calls", SettingsMenu.ToggleDescribe(SettingsField.ShellToolBridge, false));
        Assert.Equal("Shell police outside paths", SettingsMenu.FieldName(SettingsField.ShellPoliceOutsidePaths));
        Assert.True(SettingsMenu.IsToggle(SettingsField.ShellPoliceOutsidePaths));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.ShellPoliceOutsidePaths));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ShellPoliceOutsidePaths, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ShellPoliceOutsidePaths, new AppSettingsData { ShellPoliceOutsidePaths = false }, _settings.ProfileDirectory));
        Assert.Equal("paths outside the working directory are denied",SettingsMenu.ToggleDescribe(SettingsField.ShellPoliceOutsidePaths, true));
        Assert.Equal("paths anywhere on the computer are allowed", SettingsMenu.ToggleDescribe(SettingsField.ShellPoliceOutsidePaths, false));
        Assert.Equal("Shell code languages", SettingsMenu.FieldName(SettingsField.ShellCodeLanguages));
        Assert.Equal("Shell code timeout (s)", SettingsMenu.FieldName(SettingsField.ShellCodeTimeoutSeconds));
        Assert.Equal("Shell tool bridge max calls", SettingsMenu.FieldName(SettingsField.ShellCodeMaxToolCalls));
        Assert.Equal("powershell, python, node", SettingsMenu.FieldValue(SettingsField.ShellCodeLanguages, data, _settings.ProfileDirectory));
        Assert.Equal("python", SettingsMenu.FieldValue(SettingsField.ShellCodeLanguages, new AppSettingsData { ShellCodeLanguages = ["python", "ruby"] }, _settings.ProfileDirectory));
        Assert.Equal("300", SettingsMenu.FieldValue(SettingsField.ShellCodeTimeoutSeconds, data, _settings.ProfileDirectory));
        Assert.Equal("50 tool calls", SettingsMenu.FieldValue(SettingsField.ShellCodeMaxToolCalls, data, _settings.ProfileDirectory));
        Assert.Equal("300", SettingsMenu.EditableValue(SettingsField.ShellCodeTimeoutSeconds, data));
        Assert.Equal("50", SettingsMenu.EditableValue(SettingsField.ShellCodeMaxToolCalls, data));
        Assert.Equal("must be 1 to 3600 seconds", SettingsMenu.ShellCodeTimeoutSecondsRangeError);
        Assert.Equal("must be 1 to 500 tool calls", SettingsMenu.ShellCodeMaxToolCallsRangeError);
        Assert.Equal("[[x]] python     [#9A8BB8]a .py through python.exe; from neon_tools import …[/]", SettingsMenu.CodeLanguageLabel("python", enabled: true, installed: true));   // the marks escaped for markup
        Assert.Equal("[[ ]] node       [#9A8BB8]a .js through node.exe; require('neon_tools') — not found[/]", SettingsMenu.CodeLanguageLabel("node", enabled: false, installed: false));
        Assert.Equal("Shell command policy", SettingsMenu.FieldName(SettingsField.ShellCommandPolicy));
        Assert.Equal("Shell allowed commands", SettingsMenu.FieldName(SettingsField.ShellCommandAllowed));
        Assert.Equal("Shell default", SettingsMenu.FieldName(SettingsField.ShellDefault));
        Assert.Equal("Shell timeout (s)", SettingsMenu.FieldName(SettingsField.ShellTimeoutSeconds));
        Assert.Equal("Shell foreground cap (s)", SettingsMenu.FieldName(SettingsField.ShellForegroundCapSeconds));
        Assert.Equal("Shell output max chars", SettingsMenu.FieldName(SettingsField.ShellOutputMaxChars));
        Assert.Equal("Shell prefer native tools", SettingsMenu.FieldName(SettingsField.ShellPreferNative));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ShellPreferNative, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ShellPreferNative, new AppSettingsData { ShellPreferNative = false }, _settings.ProfileDirectory));
        Assert.Equal([SettingsField.ShellPoliceOutsidePaths, SettingsField.ShellPreferNative, SettingsField.ShellToolBridge], SettingsMenu.ToolsTabFields[2].Where(SettingsMenu.IsToggle));
        Assert.All(SettingsMenu.ToolsTabFields[2], f => Assert.False(SettingsMenu.RefusedMidTurn(f)));
        Assert.Equal("ask", SettingsMenu.FieldValue(SettingsField.ShellCommandPolicy, data, _settings.ProfileDirectory));
        Assert.Equal("none", SettingsMenu.FieldValue(SettingsField.ShellCommandAllowed, data, _settings.ProfileDirectory));
        Assert.Equal("1 prefix", SettingsMenu.FieldValue(SettingsField.ShellCommandAllowed, new AppSettingsData { ShellCommandAllowed = ["git push"] }, _settings.ProfileDirectory));
        Assert.Equal("2 prefixes", SettingsMenu.FieldValue(SettingsField.ShellCommandAllowed, new AppSettingsData { ShellCommandAllowed = ["git push", "python"] }, _settings.ProfileDirectory));
        Assert.Equal("powershell", SettingsMenu.FieldValue(SettingsField.ShellDefault, data, _settings.ProfileDirectory));
        Assert.Equal("180", SettingsMenu.FieldValue(SettingsField.ShellTimeoutSeconds, data, _settings.ProfileDirectory));
        Assert.Equal("600", SettingsMenu.FieldValue(SettingsField.ShellForegroundCapSeconds, data, _settings.ProfileDirectory));
        Assert.Equal("30,000 chars", SettingsMenu.FieldValue(SettingsField.ShellOutputMaxChars, data, _settings.ProfileDirectory));
        Assert.Equal("180", SettingsMenu.EditableValue(SettingsField.ShellTimeoutSeconds, data));
        Assert.Equal("600", SettingsMenu.EditableValue(SettingsField.ShellForegroundCapSeconds, data));
        Assert.Equal("30000", SettingsMenu.EditableValue(SettingsField.ShellOutputMaxChars, data));
        Assert.Equal("must be 1 to 3600 seconds", SettingsMenu.ShellTimeoutSecondsRangeError);
        Assert.Equal("must be 10 to 3600 seconds", SettingsMenu.ShellForegroundCapSecondsRangeError);
        Assert.Equal("must be 2000 to 500000 chars", SettingsMenu.ShellOutputMaxCharsRangeError);
        Assert.Equal("ask  [#9A8BB8]you approve each command not on the allow list[/]", SettingsMenu.CommandPolicyLabel("ask"));
        Assert.Equal("yolo [#9A8BB8]every command runs, nothing is asked[/]", SettingsMenu.CommandPolicyLabel("yolo"));
        Assert.Equal("off  [#9A8BB8]no shell or script tool is offered[/]", SettingsMenu.CommandPolicyLabel("off"));
        Assert.Equal("bash       [#9A8BB8]Git Bash, when bash.exe is found — not found[/]", SettingsMenu.ShellLabel("bash", installed: false));
        Assert.Equal("cmd        [#9A8BB8]cmd.exe: batch syntax[/]", SettingsMenu.ShellLabel("cmd", installed: true));
        Assert.Equal("Shell allowed commands: git push removed", SettingsMenu.PrefixRemovedNotice("git push"));
        // The git rows (2026-09-20): the Git tab (Git (native), the last, since later on 2026-09-21) — the switch, then the two caps alphabetically; typed, none a reconnect.
        Assert.Equal(new[] { SettingsField.GitNativeTools, SettingsField.GitNativeDiffMaxLines, SettingsField.GitNativeLogMaxCommits, SettingsField.GitNativeEmail, SettingsField.GitNativeName }, SettingsMenu.ToolsTabFields[10]);
        // The identity pair (2026-09-21): typed, empty allowed and shown as (not set), no validation.
        Assert.Equal("Git native email", SettingsMenu.FieldName(SettingsField.GitNativeEmail));   // the Git native labels, later on 2026-09-21
        Assert.Equal("Git native name", SettingsMenu.FieldName(SettingsField.GitNativeName));
        Assert.Equal("(not set)", SettingsMenu.FieldValue(SettingsField.GitNativeEmail, data, _settings.ProfileDirectory));
        Assert.Equal("(not set)", SettingsMenu.FieldValue(SettingsField.GitNativeName, data, _settings.ProfileDirectory));
        Assert.Equal("me@example.invalid", SettingsMenu.FieldValue(SettingsField.GitNativeEmail, new AppSettingsData { GitNativeEmail = "me@example.invalid" }, _settings.ProfileDirectory));
        Assert.Equal("Some User", SettingsMenu.FieldValue(SettingsField.GitNativeName, new AppSettingsData { GitNativeName = "Some User" }, _settings.ProfileDirectory));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.GitNativeEmail, data));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.GitNativeName, data));
        Assert.False(SettingsMenu.IsToggle(SettingsField.GitNativeEmail));
        Assert.False(SettingsMenu.IsToggle(SettingsField.GitNativeName));
        Assert.Equal("Git native tools", SettingsMenu.FieldName(SettingsField.GitNativeTools));
        Assert.Equal("Git native diff max lines", SettingsMenu.FieldName(SettingsField.GitNativeDiffMaxLines));
        Assert.Equal("Git native log max commits", SettingsMenu.FieldName(SettingsField.GitNativeLogMaxCommits));
        Assert.True(SettingsMenu.IsToggle(SettingsField.GitNativeTools));
        Assert.False(SettingsMenu.IsToggle(SettingsField.GitNativeDiffMaxLines));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.GitNativeTools, data, _settings.ProfileDirectory));   // off by default since later on 2026-09-21
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.GitNativeTools, new AppSettingsData { GitNativeTools = true }, _settings.ProfileDirectory));
        Assert.Equal("500 lines", SettingsMenu.FieldValue(SettingsField.GitNativeDiffMaxLines, data, _settings.ProfileDirectory));
        Assert.Equal("20 commits", SettingsMenu.FieldValue(SettingsField.GitNativeLogMaxCommits, data, _settings.ProfileDirectory));
        Assert.Equal("1 commit", SettingsMenu.FieldValue(SettingsField.GitNativeLogMaxCommits, new AppSettingsData { GitNativeLogMaxCommits = 1 }, _settings.ProfileDirectory));
        Assert.Equal("500", SettingsMenu.EditableValue(SettingsField.GitNativeDiffMaxLines, data));
        Assert.Equal("20", SettingsMenu.EditableValue(SettingsField.GitNativeLogMaxCommits, data));
        Assert.Equal("must be 20 to 5000 lines", SettingsMenu.GitNativeDiffMaxLinesRangeError);
        Assert.Equal("must be 1 to 200 commits", SettingsMenu.GitNativeLogMaxCommitsRangeError);
        Assert.Equal("git (native) tools enabled", SettingsMenu.ToggleDescribe(SettingsField.GitNativeTools, true));
        Assert.Equal("git (native) tools disabled", SettingsMenu.ToggleDescribe(SettingsField.GitNativeTools, false));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.GitNativeTools));
        Assert.Equal("Web search method", SettingsMenu.FieldName(SettingsField.WebSearchMethod));
        Assert.Equal("duckduckgo", SettingsMenu.FieldValue(SettingsField.WebSearchMethod, data, _settings.ProfileDirectory));
        Assert.Equal("searxng", SettingsMenu.FieldValue(SettingsField.WebSearchMethod, new AppSettingsData { WebSearchMethod = "searxng" }, _settings.ProfileDirectory));
        Assert.Equal("duckduckgo [#9A8BB8]the built-in DuckDuckGo scrape, no setup[/]", SettingsMenu.SearchMethodLabel("duckduckgo"));
        Assert.Equal("searxng    [#9A8BB8]the instance named in Web SearXNG URL; DuckDuckGo until one is set[/]", SettingsMenu.SearchMethodLabel("searxng"));
        foreach (var f in SettingsMenu.ToolsTabFields[10])
        {
            Assert.False(SettingsMenu.IsLlmField(f) || SettingsMenu.IsTtsField(f) || SettingsMenu.IsVoiceField(f));
        }

        Assert.True(SettingsMenu.IsToggle(SettingsField.WebTools));
        Assert.False(SettingsMenu.IsToggle(SettingsField.WebBrowserNetworkMode));
        Assert.False(SettingsMenu.IsToggle(SettingsField.WebBrowserMode) || SettingsMenu.IsToggle(SettingsField.WebBrowserPath) || SettingsMenu.IsToggle(SettingsField.WebSearxngUrl) || SettingsMenu.IsToggle(SettingsField.WebSearchMaxResults) || SettingsMenu.IsToggle(SettingsField.WebSearchMethod));
        Assert.Equal("Web tools", SettingsMenu.FieldName(SettingsField.WebTools));
        Assert.Equal("Web browser mode", SettingsMenu.FieldName(SettingsField.WebBrowserMode));
        Assert.Equal("Web browser path", SettingsMenu.FieldName(SettingsField.WebBrowserPath));
        Assert.Equal("Web browser network mode", SettingsMenu.FieldName(SettingsField.WebBrowserNetworkMode));
        Assert.Equal("Web SearXNG URL", SettingsMenu.FieldName(SettingsField.WebSearxngUrl));
        Assert.Equal("Web search max results", SettingsMenu.FieldName(SettingsField.WebSearchMaxResults));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.WebTools, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.WebTools, new AppSettingsData { WebTools = false }, _settings.ProfileDirectory));
        Assert.Equal("default", SettingsMenu.FieldValue(SettingsField.WebBrowserMode, data, _settings.ProfileDirectory));
        Assert.Equal("chromium", SettingsMenu.FieldValue(SettingsField.WebBrowserMode, new AppSettingsData { WebBrowserMode = "chromium" }, _settings.ProfileDirectory));
        Assert.Equal("(auto: none found)", SettingsMenu.FieldValue(SettingsField.WebBrowserPath, data, _settings.ProfileDirectory));
        Assert.Equal("(auto: msedge.exe)", SettingsMenu.FieldValue(SettingsField.WebBrowserPath, data, _settings.ProfileDirectory, FakeBrowserPath));
        Assert.Equal(@"C:\tools\chrome.exe", SettingsMenu.FieldValue(SettingsField.WebBrowserPath, new AppSettingsData { WebBrowserPath = @"C:\tools\chrome.exe" }, _settings.ProfileDirectory, FakeBrowserPath));
        Assert.Equal("internet", SettingsMenu.FieldValue(SettingsField.WebBrowserNetworkMode, data, _settings.ProfileDirectory));
        Assert.Equal("both", SettingsMenu.FieldValue(SettingsField.WebBrowserNetworkMode, new AppSettingsData { WebBrowserNetworkMode = "both" }, _settings.ProfileDirectory));
        Assert.Equal("internet           [#9A8BB8]public addresses alone; this machine and the local network refused[/]", SettingsMenu.NetworkModeLabel("internet"));
        Assert.Equal("local_area_network [#9A8BB8]this machine and the local network alone; the internet refused[/]", SettingsMenu.NetworkModeLabel("local_area_network"));
        Assert.Equal("both               [#9A8BB8]every address[/]", SettingsMenu.NetworkModeLabel("both"));
        Assert.Equal("(not set)", SettingsMenu.FieldValue(SettingsField.WebSearxngUrl, data, _settings.ProfileDirectory));   // the engine is the method row's business (2026-09-15; "(built-in DuckDuckGo)" before)
        Assert.Equal("http://localhost:8080", SettingsMenu.FieldValue(SettingsField.WebSearxngUrl, new AppSettingsData { WebSearxngUrl = "http://localhost:8080" }, _settings.ProfileDirectory));
        Assert.Equal("20 results", SettingsMenu.FieldValue(SettingsField.WebSearchMaxResults, data, _settings.ProfileDirectory));
        Assert.Equal("1 result", SettingsMenu.FieldValue(SettingsField.WebSearchMaxResults, new AppSettingsData { WebSearchMaxResults = 1 }, _settings.ProfileDirectory));
        Assert.Equal("20", SettingsMenu.EditableValue(SettingsField.WebSearchMaxResults, data));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.WebBrowserPath, data));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.WebSearxngUrl, data));
        Assert.Equal("must be 1 to 20 results", SettingsMenu.WebSearchMaxResultsRangeError);
        Assert.Equal("default    [#9A8BB8]HttpClient, then a headless browser when a page is blocked or empty[/]", SettingsMenu.BrowserModeLabel("default"));
        Assert.Equal("httpclient [#9A8BB8]HttpClient alone, with browser-like headers[/]", SettingsMenu.BrowserModeLabel("httpclient"));
        Assert.Equal("chromium   [#9A8BB8]a headless Edge, Chrome or Brave for every page[/]", SettingsMenu.BrowserModeLabel("chromium"));
        Assert.Equal("Web browser path: (auto: msedge.exe)", SettingsMenu.SavedNotice(SettingsField.WebBrowserPath, data, _settings.ProfileDirectory, FakeBrowserPath));
        Assert.True(SettingsMenu.IsToggle(SettingsField.ShowImageThumbnails));
        Assert.Equal("Show image thumbnails", SettingsMenu.FieldName(SettingsField.ShowImageThumbnails));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ShowImageThumbnails, data, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.ShowImageThumbnails) || SettingsMenu.IsTtsField(SettingsField.ShowImageThumbnails) || SettingsMenu.IsVoiceField(SettingsField.ShowImageThumbnails));
        // The thumbnail size: a General-tab picker beside the toggle, no reconnect.
        Assert.False(SettingsMenu.IsToggle(SettingsField.ImageThumbnailSize));
        Assert.Equal("Image thumbnail size", SettingsMenu.FieldName(SettingsField.ImageThumbnailSize));
        Assert.Equal("small", SettingsMenu.FieldValue(SettingsField.ImageThumbnailSize, data, _settings.ProfileDirectory));
        Assert.Equal("large", SettingsMenu.FieldValue(SettingsField.ImageThumbnailSize, new AppSettingsData { ImageThumbnailSize = "large" }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.ImageThumbnailSize) || SettingsMenu.IsTtsField(SettingsField.ImageThumbnailSize) || SettingsMenu.IsVoiceField(SettingsField.ImageThumbnailSize));
        Assert.Equal("tiny     [#9A8BB8]32 columns × 8 rows[/]", SettingsMenu.ImageThumbnailSizeLabel("tiny"));
        Assert.Equal("small    [#9A8BB8]48 columns × 12 rows[/]", SettingsMenu.ImageThumbnailSizeLabel("small"));
        Assert.Equal("medium   [#9A8BB8]64 columns × 16 rows[/]", SettingsMenu.ImageThumbnailSizeLabel("medium"));
        Assert.Equal("large    [#9A8BB8]80 columns × 20 rows[/]", SettingsMenu.ImageThumbnailSizeLabel("large"));
        Assert.Equal("xlarge   [#9A8BB8]96 columns × 24 rows[/]", SettingsMenu.ImageThumbnailSizeLabel("xlarge"));
        Assert.Equal("fullsize [#9A8BB8]fits the window[/]", SettingsMenu.ImageThumbnailSizeLabel("fullsize"));
        // The new-profile mode: a General-tab picker under the profile, no reconnect.
        Assert.False(SettingsMenu.IsToggle(SettingsField.NewProfileMode));
        Assert.Equal("New profile mode", SettingsMenu.FieldName(SettingsField.NewProfileMode));
        Assert.Equal("basic", SettingsMenu.FieldValue(SettingsField.NewProfileMode, data, _settings.ProfileDirectory));
        Assert.Equal("advanced", SettingsMenu.FieldValue(SettingsField.NewProfileMode, new AppSettingsData { NewProfileMode = "advanced" }, _settings.ProfileDirectory));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.NewProfileMode, data));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.NewProfileMode) || SettingsMenu.IsTtsField(SettingsField.NewProfileMode) || SettingsMenu.IsVoiceField(SettingsField.NewProfileMode));
        Assert.Equal("basic    [#9A8BB8]copy the settings and memories[/]", SettingsMenu.NewProfileModeLabel("basic"));
        Assert.Equal("advanced [#9A8BB8]copy the settings and memories · copy persona, operata and vocalia if present[/]", SettingsMenu.NewProfileModeLabel("advanced"));
        Assert.False(SettingsMenu.IsToggle(SettingsField.FileTreeMaxLength));
        Assert.True(SettingsMenu.IsToggle(SettingsField.FileTreeShowSizes));
        Assert.Equal("File /tree max length", SettingsMenu.FieldName(SettingsField.FileTreeMaxLength));
        Assert.Equal("File /tree show sizes", SettingsMenu.FieldName(SettingsField.FileTreeShowSizes));
        Assert.Equal("500 entries", SettingsMenu.FieldValue(SettingsField.FileTreeMaxLength, data, _settings.ProfileDirectory));
        Assert.Equal("1 entry", SettingsMenu.FieldValue(SettingsField.FileTreeMaxLength, new AppSettingsData { FileTreeMaxLength = 1 }, _settings.ProfileDirectory));
        Assert.Equal("500", SettingsMenu.EditableValue(SettingsField.FileTreeMaxLength, data));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.FileTreeShowSizes, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.FileTreeShowSizes, new AppSettingsData { FileTreeShowSizes = false }, _settings.ProfileDirectory));
        Assert.Equal("must be 1 to 10000 entries", SettingsMenu.TreeMaxLengthRangeError);
        Assert.False(SettingsMenu.IsLlmField(SettingsField.FileTreeMaxLength) || SettingsMenu.IsTtsField(SettingsField.FileTreeMaxLength) || SettingsMenu.IsVoiceField(SettingsField.FileTreeMaxLength));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.FileTreeShowSizes) || SettingsMenu.IsTtsField(SettingsField.FileTreeShowSizes) || SettingsMenu.IsVoiceField(SettingsField.FileTreeShowSizes));
        // The view_image cap (2026-09-19, the user's ask): the Files tab's last row, typed, 1–100, 10 by default; no reconnect, allowed mid-turn (read at each call).
        Assert.False(SettingsMenu.IsToggle(SettingsField.FileViewImageMaxPerCall));
        Assert.Equal("File view image max (per call)", SettingsMenu.FieldName(SettingsField.FileViewImageMaxPerCall));
        Assert.Equal("10 pictures", SettingsMenu.FieldValue(SettingsField.FileViewImageMaxPerCall, data, _settings.ProfileDirectory));
        Assert.Equal("1 picture", SettingsMenu.FieldValue(SettingsField.FileViewImageMaxPerCall, new AppSettingsData { FileViewImageMaxPerCall = 1 }, _settings.ProfileDirectory));
        Assert.Equal("10", SettingsMenu.EditableValue(SettingsField.FileViewImageMaxPerCall, data));
        Assert.Equal("must be 1 to 100 pictures", SettingsMenu.ViewImageMaxPerCallRangeError);
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.FileViewImageMaxPerCall));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.FileViewImageMaxPerCall) || SettingsMenu.IsTtsField(SettingsField.FileViewImageMaxPerCall) || SettingsMenu.IsVoiceField(SettingsField.FileViewImageMaxPerCall));
        // The profile name on the hint row: the last enum member (the flat list's last row), on the General tab under the new-profile mode, a toggle on by default, no reconnect.
        // The fun verbs: the LLM tab's last row (General's until 2026-09-15, "Thinking use fun verbs" then), a toggle off by default, no reconnect; the member and the JSON key keep the old name.
        Assert.True(SettingsMenu.IsToggle(SettingsField.LlmUseFunVerbs));
        Assert.Equal("LLM use fun verbs", SettingsMenu.FieldName(SettingsField.LlmUseFunVerbs));
        Assert.Equal(SettingsField.LlmUseFunVerbs, SettingsMenu.TabFields[(int)SettingsTab.Llm][^6]);
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.LlmUseFunVerbs, data, _settings.ProfileDirectory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.LlmUseFunVerbs, new AppSettingsData { LlmUseFunVerbs = true }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmUseFunVerbs) || SettingsMenu.IsTtsField(SettingsField.LlmUseFunVerbs) || SettingsMenu.IsVoiceField(SettingsField.LlmUseFunVerbs));
        // Show thinking (2026-09-26): under the fun verbs, a toggle on by default, no reconnect, the user's descriptions.
        Assert.True(SettingsMenu.IsToggle(SettingsField.LlmShowThinking));
        Assert.Equal("LLM show thinking", SettingsMenu.FieldName(SettingsField.LlmShowThinking));
        Assert.Equal(SettingsField.LlmShowThinking, SettingsMenu.TabFields[(int)SettingsTab.Llm][^5]);
        // Preserve thinking (2026-09-28): the LLM tab's last row, a toggle off by default, no reconnect.
        Assert.True(SettingsMenu.IsToggle(SettingsField.LlmPreserveThinking));
        Assert.Equal(SettingsField.LlmPreserveThinking, SettingsMenu.TabFields[(int)SettingsTab.Llm][^4]);
        Assert.Equal(SettingsField.LlmReasoningEstimate, SettingsMenu.TabFields[(int)SettingsTab.Llm][^3]);   // under preserve thinking since 2026-09-29
        Assert.Equal(SettingsField.LlmSampling, SettingsMenu.TabFields[(int)SettingsTab.Llm][^2]);   // under preserve thinking since 2026-09-28
        Assert.Equal(SettingsField.LlmSamplingFromHuggingFace, SettingsMenu.TabFields[(int)SettingsTab.Llm][^1]);   // the LLM tab's last row, later that day
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.LlmPreserveThinking, data, _settings.ProfileDirectory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.LlmPreserveThinking, new AppSettingsData { LlmPreserveThinking = true }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmPreserveThinking));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.LlmShowThinking, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.LlmShowThinking, new AppSettingsData { LlmShowThinking = false }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmShowThinking) || SettingsMenu.IsTtsField(SettingsField.LlmShowThinking) || SettingsMenu.IsVoiceField(SettingsField.LlmShowThinking));
        Assert.Equal("thinking shown in chat", SettingsMenu.ToggleDescribe(SettingsField.LlmShowThinking, true));
        Assert.Equal("thinking not shown in chat", SettingsMenu.ToggleDescribe(SettingsField.LlmShowThinking, false));
        Assert.Equal("every turn's thinking goes back to the server", SettingsMenu.ToggleDescribe(SettingsField.LlmPreserveThinking, true));
        Assert.Equal("only the current turn's thinking goes back to the server", SettingsMenu.ToggleDescribe(SettingsField.LlmPreserveThinking, false));
        // The LLM tab: the scan mode first (where a blank URL looks, so above the URL; a picker, no reconnect), then the reconnecting LLM fields in enum order, the compact rows, the turn-loop rows and the fun verbs.
        Assert.Equal(new[] { SettingsField.LlmScanMode, SettingsField.LlmUrl, SettingsField.LlmModel, SettingsField.LlmApiKey, SettingsField.LlmReasoning, SettingsField.LlmRequestTimeoutSeconds, SettingsField.LlmTurnTimeoutSeconds, SettingsField.LlmContextLength, SettingsField.LlmMidTurnUsage, SettingsField.LlmCompactType, SettingsField.LlmCompactKeepRecent, SettingsField.LlmCompactShowSummary, SettingsField.LlmAutoCompactPercent, SettingsField.LlmMaxTurns, SettingsField.LlmOfferTools, SettingsField.LlmToolCompactType, SettingsField.LlmMaxToolIterations, SettingsField.LlmUseFunVerbs, SettingsField.LlmShowThinking, SettingsField.LlmPreserveThinking, SettingsField.LlmReasoningEstimate, SettingsField.LlmSampling, SettingsField.LlmSamplingFromHuggingFace }, SettingsMenu.TabFields[(int)SettingsTab.Llm]);
        // The reconnecting rows: the LLM tab's seven, then the Claude (API) tab's four (2026-09-27).
        Assert.Equal(Enum.GetValues<SettingsField>().Where(SettingsMenu.IsLlmField), SettingsMenu.TabFields[(int)SettingsTab.Llm].Skip(1).Take(7).Concat(SettingsMenu.TabFields[(int)SettingsTab.ClaudeApi]).Concat(SettingsMenu.TabFields[(int)SettingsTab.Embedded]));   // the Embedded LLM tab's rows all reconnect (2026-09-29)
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmScanMode) || SettingsMenu.IsTtsField(SettingsField.LlmScanMode) || SettingsMenu.IsVoiceField(SettingsField.LlmScanMode));
        Assert.False(SettingsMenu.IsToggle(SettingsField.LlmScanMode));
        Assert.Equal("LLM scan mode", SettingsMenu.FieldName(SettingsField.LlmScanMode));
        Assert.Equal("local", SettingsMenu.FieldValue(SettingsField.LlmScanMode, data, _settings.ProfileDirectory));
        Assert.Equal("remote", SettingsMenu.FieldValue(SettingsField.LlmScanMode, new AppSettingsData { LlmScanMode = "remote" }, _settings.ProfileDirectory));
        Assert.Equal("local    [#9A8BB8]the usual ports on this machine (127.0.0.1)[/]", SettingsMenu.LlmScanModeLabel("local"));   // padded to nine since disabled (2026-09-15)
        Assert.Equal("remote   [#9A8BB8]the usual ports on every other machine on the local network[/]", SettingsMenu.LlmScanModeLabel("remote"));
        Assert.Equal("both     [#9A8BB8]this machine first, then the local network[/]", SettingsMenu.LlmScanModeLabel("both"));
        Assert.Equal("disabled [#9A8BB8]no scan; set LLM URL by hand[/]", SettingsMenu.LlmScanModeLabel("disabled"));
        // The empty URL row says where the scan will look.
        Assert.Equal("(probe local ports)", SettingsMenu.BlankUrlLabel(ScanScope.Local));
        Assert.Equal("(scan the local network)", SettingsMenu.BlankUrlLabel(ScanScope.Remote));
        Assert.Equal("(probe local ports and the network)", SettingsMenu.BlankUrlLabel(ScanScope.Both));
        Assert.Equal("(not set; scan disabled)", SettingsMenu.BlankUrlLabel(ScanScope.Disabled));
        Assert.Equal("(scan the local network)", SettingsMenu.FieldValue(SettingsField.LlmUrl, new AppSettingsData { LlmScanMode = "remote" }, _settings.ProfileDirectory));
        Assert.Equal("(probe local ports)", SettingsMenu.FieldValue(SettingsField.LlmUrl, new AppSettingsData { LlmScanMode = "lan" }, _settings.ProfileDirectory));
        Assert.Equal("http://x/v1", SettingsMenu.FieldValue(SettingsField.LlmUrl, new AppSettingsData { LlmScanMode = "remote", LlmUrl = "http://x/v1" }, _settings.ProfileDirectory));
        // The TTS tab (the user's order, 2026-09-16): the switch, the source, the server's URL, the voice preview (a toggle on by default, no reconnect: read at the next pick), then the voices, the mix and the speed.
        Assert.Equal(new[] { SettingsField.TtsOutput, SettingsField.TtsSource, SettingsField.TtsHttpUrl, SettingsField.TtsVoicePreview, SettingsField.TtsVoicePreset, SettingsField.TtsVoice, SettingsField.TtsVoice2, SettingsField.TtsVoiceMix, SettingsField.TtsSpeed }, SettingsMenu.TabFields[(int)SettingsTab.Tts]);
        Assert.Equal(Enum.GetValues<SettingsField>().Where(SettingsMenu.IsTtsField).OrderBy(f => f).ToArray(), SettingsMenu.TabFields[(int)SettingsTab.Tts].Where(f => f != SettingsField.TtsVoicePreview).OrderBy(f => f).ToArray());
        // TTS source (2026-09-16): a picker over http / in-process, a reconnect like every other TTS row; the URL row reads "TTS HTTP URL" since.
        Assert.True(SettingsMenu.IsTtsField(SettingsField.TtsSource));
        Assert.True(SettingsMenu.RefusedMidTurn(SettingsField.TtsSource));
        Assert.False(SettingsMenu.IsToggle(SettingsField.TtsSource));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.TtsSource) || SettingsMenu.IsVoiceField(SettingsField.TtsSource));
        Assert.Equal("TTS source", SettingsMenu.FieldName(SettingsField.TtsSource));
        Assert.Equal("TTS HTTP URL", SettingsMenu.FieldName(SettingsField.TtsHttpUrl));
        Assert.Equal("in-process", SettingsMenu.FieldValue(SettingsField.TtsSource, data, _settings.ProfileDirectory));   // the default since 2026-09-16 (http for the first hours)
        Assert.Equal("http", SettingsMenu.FieldValue(SettingsField.TtsSource, new AppSettingsData { TtsSource = "http" }, _settings.ProfileDirectory));
        Assert.Equal("http       [#9A8BB8]a Kokoro-FastAPI server at TTS HTTP URL[/]", SettingsMenu.TtsSourceLabel("http"));
        Assert.Equal("in-process [#9A8BB8]KokoroSharp in this process; kokoro.onnx (326 MB) downloads on first use[/]", SettingsMenu.TtsSourceLabel("in-process"));
        Assert.True(SettingsMenu.IsToggle(SettingsField.TtsVoicePreview));
        Assert.Equal("TTS voice preview", SettingsMenu.FieldName(SettingsField.TtsVoicePreview));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.TtsVoicePreview, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.TtsVoicePreview, new AppSettingsData { TtsVoicePreview = false }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.TtsVoicePreview) || SettingsMenu.IsTtsField(SettingsField.TtsVoicePreview) || SettingsMenu.IsVoiceField(SettingsField.TtsVoicePreview));
        var chunker = new SentenceChunker();
        Assert.Equal(PreviewSentences, new List<string>([.. chunker.Append(SettingsMenu.VoicePreviewText), chunker.Flush()]));
        Assert.Equal(Enum.GetValues<SettingsField>().Where(SettingsMenu.IsVoiceField), SettingsMenu.TabFields[(int)SettingsTab.Stt]);
        // The Ask tab (2026-09-15; /tools' first settings tab since 2026-09-19): the question tool's switch and its two caps, none a reconnect.
        Assert.Equal(["General", "Sessions", "LLM", "TTS", "STT", "Claude (API)", "Embedded", "Botchat"], SettingsMenu.TabTitles);   // Embedded LLM since 2026-09-29; Claude (API) before Botchat since later on 2026-09-27; Claude last since 2026-09-27; Botchat last from 2026-09-25;   // five since 2026-09-19
        // The Options tab of /skills (2026-09-19; the Skills tab of /settings from 2026-09-16 until then): the skills switch, the external-folder switch and the compact-mode picker, then (2026-09-17) the #-mention switch, the delete switch, then the auto-learn switch and the reflection rows; none a reconnect.
        Assert.Equal([SettingsField.AgentSkills, SettingsField.ExternalSkills, SettingsField.SkillCompactMode, SettingsField.SkillHashMention], SettingsMenu.SkillsTabFields[0]);   // the Options tab; the reflection rows on their own tab since later on 2026-09-19
        Assert.Equal([SettingsField.ReflectionAutoLearn, SettingsField.ReflectionReasoning, SettingsField.ReflectionWindow, SettingsField.ReflectionMinToolCalls, SettingsField.ReflectionMaxRequests, SettingsField.ReflectionCooldownMinutes, SettingsField.ReflectionCooldownMode, SettingsField.ReflectionIncludesSessions, SettingsField.ReflectionYieldsToTurns, SettingsField.ReflectionEditsSupportingFiles], SettingsMenu.SkillsTabFields[1]);   // the Reflection tab: the cooldown, its mode and the sessions switch (last, the user's place) since 2026-09-19
        // Reflection min tool calls (2026-09-17): the tab's last row and the enum's last member, typed 3–20; the error door stays one recovered error.
        Assert.False(SettingsMenu.IsToggle(SettingsField.ReflectionMinToolCalls));
        Assert.Equal("Reflection min tool calls", SettingsMenu.FieldName(SettingsField.ReflectionMinToolCalls));
        Assert.Equal("4 tool calls", SettingsMenu.FieldValue(SettingsField.ReflectionMinToolCalls, data, _settings.ProfileDirectory));
        Assert.Equal("1 tool call", SettingsMenu.ToolCalls(1));
        Assert.Equal("4", SettingsMenu.EditableValue(SettingsField.ReflectionMinToolCalls, data));
        Assert.Equal("must be 3 to 20 tool calls", SettingsMenu.ReflectionMinToolCallsRangeError);
        // Reflection max requests (2026-09-17): the enum's last member, the tab's tenth row above the verbose switch, typed 1–20; the reflection's own cap.
        Assert.False(SettingsMenu.IsToggle(SettingsField.ReflectionMaxRequests));
        Assert.Equal("Reflection max requests", SettingsMenu.FieldName(SettingsField.ReflectionMaxRequests));
        Assert.Equal("4 requests", SettingsMenu.FieldValue(SettingsField.ReflectionMaxRequests, data, _settings.ProfileDirectory));
        Assert.Equal("1 request", SettingsMenu.Requests(1));
        Assert.Equal("4", SettingsMenu.EditableValue(SettingsField.ReflectionMaxRequests, data));
        Assert.Equal("must be 1 to 20 requests", SettingsMenu.ReflectionMaxRequestsRangeError);
        // Reflection window (2026-09-17): the tab's last row and the enum's last member, typed 1–5 like the compact keep-recent count.
        Assert.False(SettingsMenu.IsToggle(SettingsField.ReflectionWindow));
        Assert.Equal("Reflection window", SettingsMenu.FieldName(SettingsField.ReflectionWindow));
        Assert.Equal("3 turns", SettingsMenu.FieldValue(SettingsField.ReflectionWindow, data, _settings.ProfileDirectory));
        Assert.Equal("1 turn", SettingsMenu.FieldValue(SettingsField.ReflectionWindow, new AppSettingsData { ReflectionWindow = 1 }, _settings.ProfileDirectory));
        Assert.Equal("3", SettingsMenu.EditableValue(SettingsField.ReflectionWindow, data));
        Assert.Equal("must be 1 to 5 turns", SettingsMenu.ReflectionWindowRangeError);
        // Reflection (auto-learn) + Reflection reasoning (2026-09-17): the tab's last two rows and the enum's last two members — a switch and a picker, no reconnect.
        Assert.True(SettingsMenu.IsToggle(SettingsField.ReflectionAutoLearn));
        Assert.False(SettingsMenu.IsToggle(SettingsField.ReflectionReasoning));
        Assert.Equal("Reflection (auto-learn)", SettingsMenu.FieldName(SettingsField.ReflectionAutoLearn));
        Assert.Equal("Reflection reasoning", SettingsMenu.FieldName(SettingsField.ReflectionReasoning));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ReflectionAutoLearn, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ReflectionAutoLearn, new AppSettingsData { ReflectionAutoLearn = false }, _settings.ProfileDirectory));
        Assert.Equal("none", SettingsMenu.FieldValue(SettingsField.ReflectionReasoning, data, _settings.ProfileDirectory));
        Assert.Equal("high", SettingsMenu.FieldValue(SettingsField.ReflectionReasoning, new AppSettingsData { ReflectionReasoning = "high" }, _settings.ProfileDirectory));
        Assert.Equal("profile [#9A8BB8]the profile's LLM reasoning level[/]", SettingsMenu.ReflectionReasoningLabel("profile"));
        Assert.Equal("xhigh   [#9A8BB8]maximum thinking, slowest[/]", SettingsMenu.ReflectionReasoningLabel("xhigh"));
        Assert.Equal("automatic reflection enabled", SettingsMenu.ToggleDescribe(SettingsField.ReflectionAutoLearn, true));
        Assert.Equal("automatic reflection disabled", SettingsMenu.ToggleDescribe(SettingsField.ReflectionAutoLearn, false));
        Assert.Equal(2, (int)SettingsTab.Llm);   // third since 2026-09-19 (Skills sat between from 2026-09-18 until then; the Options tab of /skills now)
        Assert.True(SettingsMenu.IsToggle(SettingsField.AgentSkills) && SettingsMenu.IsToggle(SettingsField.ExternalSkills));
        Assert.True(SettingsMenu.IsToggle(SettingsField.SkillHashMention));
        Assert.Equal("#-mention enabled", SettingsMenu.FieldName(SettingsField.SkillHashMention));
        Assert.False(SettingsMenu.IsToggle(SettingsField.SkillCompactMode));
        Assert.All(SettingsMenu.SkillsTabFields.SelectMany(t => t), f => Assert.False(SettingsMenu.IsLlmField(f) || SettingsMenu.IsTtsField(f) || SettingsMenu.IsVoiceField(f) || SettingsMenu.RefusedMidTurn(f)));
        Assert.Equal("Agent skills", SettingsMenu.FieldName(SettingsField.AgentSkills));
        Assert.Equal("Use external skills (.agents\\skills)", SettingsMenu.FieldName(SettingsField.ExternalSkills));
        Assert.Equal(SettingsMenu.ExternalSkillsName, SettingsMenu.FieldName(SettingsField.ExternalSkills));
        Assert.Equal("Skill compact mode", SettingsMenu.FieldName(SettingsField.SkillCompactMode));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.AgentSkills, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.AgentSkills, new AppSettingsData { AgentSkills = false }, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ExternalSkills, data, _settings.ProfileDirectory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ExternalSkills, new AppSettingsData { ExternalSkills = true }, _settings.ProfileDirectory));
        Assert.Equal("protected", SettingsMenu.FieldValue(SettingsField.SkillCompactMode, data, _settings.ProfileDirectory));
        Assert.Equal("unprotected", SettingsMenu.FieldValue(SettingsField.SkillCompactMode, new AppSettingsData { SkillCompactMode = "unprotected" }, _settings.ProfileDirectory));
        Assert.Equal("protected   [#9A8BB8]loaded skills survive a prune and the mid-turn guard[/]", SettingsMenu.SkillCompactModeLabel("protected"));
        Assert.Equal("unprotected [#9A8BB8]loaded skills prune like any tool result[/]", SettingsMenu.SkillCompactModeLabel("unprotected"));
        Assert.Equal([SettingsField.ToolsDollarMention, SettingsField.ToolCollapseCount, SettingsField.CodeCollapseCount], SettingsMenu.ToolsTabFields[11]);   // the Options tab, later on 2026-09-19 (index 7 since the SQL tab, 2026-09-23, 9 since the Claude tab, 2026-09-27, 11 since the Print tab, 2026-09-28); the fold's count under the switch 2026-09-22, the code fold's under it later that day
        Assert.Equal([SettingsField.AskUser, SettingsField.AskMaxQuestions, SettingsField.AskMaxChoices], SettingsMenu.ToolsTabFields[3]);   // the Ask tab: second after Options until later on 2026-09-21, between Shell and Git (native) since
        Assert.True(SettingsMenu.IsToggle(SettingsField.AskUser));
        Assert.False(SettingsMenu.IsToggle(SettingsField.AskMaxQuestions) || SettingsMenu.IsToggle(SettingsField.AskMaxChoices));
        Assert.All(SettingsMenu.ToolsTabFields[3], f => Assert.False(SettingsMenu.IsLlmField(f) || SettingsMenu.IsTtsField(f) || SettingsMenu.IsVoiceField(f) || SettingsMenu.RefusedMidTurn(f)));
        Assert.Equal("Ask user", SettingsMenu.FieldName(SettingsField.AskUser));
        Assert.Equal("Ask max questions", SettingsMenu.FieldName(SettingsField.AskMaxQuestions));
        Assert.Equal("Ask max choices per question", SettingsMenu.FieldName(SettingsField.AskMaxChoices));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.AskUser, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.AskUser, new AppSettingsData { AskUser = false }, _settings.ProfileDirectory));
        Assert.Equal("10 questions", SettingsMenu.FieldValue(SettingsField.AskMaxQuestions, data, _settings.ProfileDirectory));
        Assert.Equal("1 question", SettingsMenu.FieldValue(SettingsField.AskMaxQuestions, new AppSettingsData { AskMaxQuestions = 1 }, _settings.ProfileDirectory));
        Assert.Equal("10 choices", SettingsMenu.FieldValue(SettingsField.AskMaxChoices, data, _settings.ProfileDirectory));
        Assert.Equal("2 choices", SettingsMenu.FieldValue(SettingsField.AskMaxChoices, new AppSettingsData { AskMaxChoices = 2 }, _settings.ProfileDirectory));
        Assert.Equal("1 choice", SettingsMenu.Choices(1));
        Assert.Equal("10", SettingsMenu.EditableValue(SettingsField.AskMaxQuestions, data));
        Assert.Equal("10", SettingsMenu.EditableValue(SettingsField.AskMaxChoices, data));
        Assert.Equal("must be 1 to 10 questions", SettingsMenu.AskMaxQuestionsRangeError);
        Assert.Equal("must be 2 to 15 choices", SettingsMenu.AskMaxChoicesRangeError);
        Assert.Equal(Enum.GetValues<SettingsField>().Order(), SettingsMenu.TabFields.Concat(SettingsMenu.SkillsTabFields).Concat(SettingsMenu.ToolsTabFields).Concat(SettingsMenu.McpTabFields).SelectMany(t => t).Order());   // the four panes together, every field once
        Assert.Equal(29, SettingsMenu.TabLabelWidth(SettingsTab.General));   // "Working directory in header", 27 (2026-09-21; "Hide /exit autocomplete", 23, from 2026-09-18; "Show image thumbnails", 21, before)
        Assert.Equal(32, SettingsMenu.TabLabelWidth(SettingsTab.Llm));       // "LLM sampling from Hugging Face" (2026-09-28; "LLM compact show summary", 24, from 2026-09-21; "LLM request timeout (s)", 23, before)
        Assert.Equal(19, SettingsMenu.TabLabelWidth(SettingsTab.Tts));       // "TTS voice preview"
        Assert.Equal(26, SettingsMenu.TabLabelWidth(SettingsTab.Stt));       // "STT interrupt echo guard"
        Assert.Equal(26, SettingsMenu.TabLabelWidth(SettingsTab.BotChat));   // "Botchat txt2img workflow" / "Botchat img2img workflow" (2026-09-27; "Botchat images enabled" before)
        Assert.Equal(36, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[4]));   // the Claude tab (2026-09-27): "Claude advisor tool calls per turn"
        Assert.Equal(21, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[11]));   // the Options tab (index 7 since SQL, 2026-09-23): "Tool collapse count" (2026-09-22; "$-mention enabled", 19, the Options tab's one row from later on 2026-09-19)
        Assert.Equal(26, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[0]));   // "Web browser network mode" (the Web-prefixed labels, later still on 2026-09-19; "Web search max results", 24, before)
        Assert.Equal(32, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[1]));   // "File view image max (per call)" (the File-prefixed labels, later still on 2026-09-19; "Stale line number guard", 25, that morning; "Always return line numbers", 28, from 2026-09-17 until it went; "Tree max length", 17, before)
        Assert.Equal(29, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[2]));   // "Shell tool bridge max calls" (the Shell tab, 2026-09-21; the row was "Shell code max tool calls", 27, until later that day)
        Assert.Equal(30, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[3]));   // "Ask max choices per question"
        Assert.Equal(28, SettingsMenu.LabelWidthOf(SettingsMenu.ToolsTabFields[10]));   // "Git native log max commits" (later on 2026-09-21; "Git log max commits", 21, from 2026-09-20)
        Assert.Equal(38, SettingsMenu.LabelWidthOf(SettingsMenu.SkillsTabFields[0]));   // "Use external skills (.agents\\skills)"
        Assert.Equal(34, SettingsMenu.LabelWidthOf(SettingsMenu.SkillsTabFields[1]));   // "Reflection edit supporting files" (2026-09-27; "Reflection cooldown (minutes)", 31, from later on 2026-09-19)
        Assert.Equal("TTS speed          [#EFE6FF]1.2[/]", SettingsMenu.FieldLabel(SettingsField.TtsSpeed, data, _settings.ProfileDirectory, null, SettingsMenu.TabLabelWidth(SettingsTab.Tts)));   // 1.0 until 2026-09-18
        Assert.Equal(@"Profile                      [#EFE6FF]work[/][#9A8BB8] (D:\home\profiles\work)[/]", SettingsMenu.ProfileLabel("work", @"D:\home\profiles\work", SettingsMenu.TabLabelWidth(SettingsTab.General)));
        Assert.False(SettingsMenu.IsToggle(SettingsField.WorkingDirectory));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.WorkingDirectory));
        Assert.False(SettingsMenu.IsTtsField(SettingsField.WorkingDirectory));
        Assert.False(SettingsMenu.IsVoiceField(SettingsField.WorkingDirectory));
        Assert.Equal("Working directory (cwd)", SettingsMenu.FieldName(SettingsField.WorkingDirectory));   // "(cwd)" since 2026-09-21, the user's words
        Assert.Equal("(" + Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName) + ")", SettingsMenu.FieldValue(SettingsField.WorkingDirectory, data, _settings.ProfileDirectory));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.WorkingDirectory, data));
        Assert.Equal(@"D:\x\[v]", SettingsMenu.FieldValue(SettingsField.WorkingDirectory, new AppSettingsData { WorkingDirectory = @"D:\x\[v]" }, _settings.ProfileDirectory));
        Assert.Equal("Working directory (cwd)               [#EFE6FF]D:\\x\\[[v]][/]", SettingsMenu.FieldLabel(SettingsField.WorkingDirectory, new AppSettingsData { WorkingDirectory = @"D:\x\[v]" }, _settings.ProfileDirectory, null));
        Assert.Equal(@"(D:\home\profiles\p\files)", SettingsMenu.DefaultWorkingDirectoryLabel(@"D:\home\profiles\p"));
        Assert.Equal("Could not create Q:\\nope (boom); keeping (profile folder).", SettingsMenu.WorkingDirectoryCreateError(@"Q:\nope", "boom", "(profile folder)"));
        Assert.True(SettingsMenu.IsToggle(SettingsField.Memory));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.Memory));
        Assert.False(SettingsMenu.IsTtsField(SettingsField.Memory));
        Assert.False(SettingsMenu.IsVoiceField(SettingsField.Memory));
        Assert.Equal("Memory", SettingsMenu.FieldName(SettingsField.Memory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.Memory, data, _settings.ProfileDirectory));
        Assert.True(SettingsMenu.IsToggle(SettingsField.CopyUserPrompt));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.CopyUserPrompt));
        Assert.False(SettingsMenu.IsTtsField(SettingsField.CopyUserPrompt));
        Assert.False(SettingsMenu.IsVoiceField(SettingsField.CopyUserPrompt));
        Assert.Equal("Copy user prompt", SettingsMenu.FieldName(SettingsField.CopyUserPrompt));   // "Copy user text" until 2026-09-18
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.CopyUserPrompt, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.CopyUserPrompt, new AppSettingsData { CopyUserPrompt = false }, _settings.ProfileDirectory));
        Assert.True(SettingsMenu.IsToggle(SettingsField.SttInterrupt));
        Assert.True(SettingsMenu.IsVoiceField(SettingsField.SttInterrupt));
        Assert.False(SettingsMenu.IsTtsField(SettingsField.SttInterrupt));
        Assert.Equal("STT interrupt", SettingsMenu.FieldName(SettingsField.SttInterrupt));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.SttInterrupt, data, _settings.ProfileDirectory));
        Assert.True(SettingsMenu.IsVoiceField(SettingsField.SttInterruptEchoGuard));
        Assert.False(SettingsMenu.IsToggle(SettingsField.SttInterruptEchoGuard));
        Assert.False(SettingsMenu.IsTtsField(SettingsField.SttInterruptEchoGuard));
        Assert.Equal("STT interrupt echo guard", SettingsMenu.FieldName(SettingsField.SttInterruptEchoGuard));
        Assert.Equal("100 %", SettingsMenu.FieldValue(SettingsField.SttInterruptEchoGuard, data, _settings.ProfileDirectory));
        Assert.Equal("100", SettingsMenu.EditableValue(SettingsField.SttInterruptEchoGuard, data));
        Assert.Equal("100 %", SettingsMenu.Percent(100));
        Assert.True(SettingsMenu.IsVoiceField(SettingsField.SttInterruptConfirmMs));
        Assert.False(SettingsMenu.IsToggle(SettingsField.SttInterruptConfirmMs));
        Assert.Equal("STT interrupt confirm", SettingsMenu.FieldName(SettingsField.SttInterruptConfirmMs));
        Assert.Equal("200 ms", SettingsMenu.FieldValue(SettingsField.SttInterruptConfirmMs, data, _settings.ProfileDirectory));
        Assert.Equal("200", SettingsMenu.EditableValue(SettingsField.SttInterruptConfirmMs, data));
        Assert.Equal("300 ms", SettingsMenu.Milliseconds(300));
        Assert.True(SettingsMenu.IsLlmField(SettingsField.LlmReasoning));
        Assert.False(SettingsMenu.IsToggle(SettingsField.LlmReasoning));
        Assert.Equal("LLM reasoning", SettingsMenu.FieldName(SettingsField.LlmReasoning));
        Assert.Equal("none", SettingsMenu.FieldValue(SettingsField.LlmReasoning, data, _settings.ProfileDirectory));
        Assert.Equal("xhigh   [#9A8BB8]maximum thinking, slowest[/]", SettingsMenu.ReasoningLabel("xhigh"));
        Assert.Equal("/reasoning takes none, low, medium, high or xhigh, or nothing to pick from a list.", SettingsMenu.ReasoningLevelError);
        Assert.True(SettingsMenu.IsTtsField(SettingsField.TtsVoice2));
        Assert.True(SettingsMenu.IsTtsField(SettingsField.TtsVoiceMix));
        Assert.False(SettingsMenu.IsToggle(SettingsField.TtsVoice2));
        Assert.False(SettingsMenu.IsToggle(SettingsField.TtsVoiceMix));
        Assert.Equal("TTS voice 2", SettingsMenu.FieldName(SettingsField.TtsVoice2));
        Assert.Equal("TTS voice mix", SettingsMenu.FieldName(SettingsField.TtsVoiceMix));
        Assert.Equal("am_eric", SettingsMenu.FieldValue(SettingsField.TtsVoice2, data, _settings.ProfileDirectory));   // the default since 2026-09-16
        Assert.Equal("am_eric", SettingsMenu.EditableValue(SettingsField.TtsVoice2, data));
        Assert.Equal("(none)", SettingsMenu.FieldValue(SettingsField.TtsVoice2, new AppSettingsData { TtsVoice2 = "" }, _settings.ProfileDirectory));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.TtsVoice2, new AppSettingsData { TtsVoice2 = "" }));
        Assert.Equal("af_sky", SettingsMenu.FieldValue(SettingsField.TtsVoice2, new AppSettingsData { TtsVoice2 = "af_sky" }, _settings.ProfileDirectory));
        Assert.Equal("80 % / 20 %", SettingsMenu.FieldValue(SettingsField.TtsVoiceMix, data, _settings.ProfileDirectory));   // the default since 2026-09-16
        Assert.Equal("50 % / 50 %", SettingsMenu.FieldValue(SettingsField.TtsVoiceMix, new AppSettingsData { TtsVoiceMix = 50 }, _settings.ProfileDirectory));
        Assert.Equal("80", SettingsMenu.EditableValue(SettingsField.TtsVoiceMix, data));
        Assert.Equal("70 % / 30 %", SettingsMenu.Mix(70));
        Assert.Null(Record.Exception(() => new Markup(SettingsMenu.FieldLabel(SettingsField.LlmUrl, new AppSettingsData { LlmUrl = "http://x/[v1]" }, _settings.ProfileDirectory, null))));
    }

    // ── Botchat non-TTS delay ───────────────────────────────────────────────

    /// <summary>The Botchat tab's last row (2026-09-26): typed seconds, 0 to 30, a value outside refused and the saved one kept; no reconnect.</summary>
    [Fact]
    public async Task OnThePane_BotChatNonTtsDelay_OnTheBotchatTab_SavedAndRangeChecked()
    {
        _console.Profile.Width = 240;   // the error line is longer than 100 cells
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.BotChat);
        Down(SettingsMenu.TabFields[(int)SettingsTab.BotChat].ToList().IndexOf(SettingsField.BotChatNonTtsDelaySeconds));   // the last row until Botchat skills enabled (2026-09-27)
        Push(Keys.Enter);                       // "5"
        Backspace(1);
        _console.Input.PushText("12");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // the cursor stays on the row just edited: "12"
        Backspace(2);
        _console.Input.PushText("31");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // still "12"
        Backspace(2);
        _console.Input.PushText("-1");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(12, _settings.Current.BotChatNonTtsDelaySeconds);
        Assert.Contains("Botchat non-TTS delay: 12 seconds", _console.Output);
        Assert.Contains(SettingsMenu.BotChatNonTtsDelayRangeError, _console.Output);
        Assert.Contains("keeping 12", _console.Output);
        pane.Dispose();
    }

    // ── Interrupt echo guard ────────────────────────────────────────────────

    [Fact]
    public async Task InterruptEchoMatch_IsRow20_SavedRangeCheckedAndAVoiceChange()
    {
        _console.Profile.Width = 240;   // the error line is longer than 100 cells
        Down(19);
        Push(Keys.Enter);                       // Interrupt echo guard "100"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("80");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // the cursor stays on the row just edited: "80"
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("49");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // still "80"
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("101");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal(80, _settings.Current.SttInterruptEchoGuard);
        Assert.Contains("  · STT interrupt echo guard: 80 %", _console.Output);
        Assert.Contains(SettingsMenu.SttInterruptEchoGuardRangeError, _console.Output);
        Assert.Contains("keeping 80", _console.Output);
    }

    [Fact]
    public async Task InterruptConfirm_IsRow21_SavedRangeCheckedAndAVoiceChange()
    {
        _console.Profile.Width = 240;
        Down(20);
        Push(Keys.Enter);                       // Interrupt confirm "200"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("400");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // the cursor stays on the row just edited: "400"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("2001");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Voice, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal(400, _settings.Current.SttInterruptConfirmMs);
        Assert.Contains("  · STT interrupt confirm: 400 ms", _console.Output);
        Assert.Contains(SettingsMenu.SttInterruptConfirmRangeError, _console.Output);
        Assert.Contains("keeping 400", _console.Output);
    }

    // ── Image thumbnail size ────────────────────────────────────────────────

    [Fact]
    public async Task ImageThumbnailSize_IsRow35_APicker_NoReconnect()
    {
        Down(34);
        Push(Keys.Enter);                           // Image thumbnail size: the picker opens on small
        Push(Keys.Down, Keys.Enter);                // medium
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("medium", _settings.Current.ImageThumbnailSize);
        Assert.Contains(Breadcrumb("Image thumbnail size"), _console.Output);
        Assert.Contains("  · Image thumbnail size: medium", _console.Output);
        Assert.Equal(0, _synth.ListCalls);          // no server is consulted
    }

    [Fact]
    public async Task ImageThumbnailSize_OpensOnTheSavedSize_AndEscapeKeepsIt()
    {
        _settings.Update(d => d.ImageThumbnailSize = "large");
        Down(33);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("large", _settings.Current.ImageThumbnailSize);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task ImageThumbnailSize_UpFromTheSavedSize_PicksTheOneAbove()
    {
        _settings.Update(d => d.ImageThumbnailSize = "large");
        Down(34);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // large → medium

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("medium", _settings.Current.ImageThumbnailSize);
    }

    // ── Theme (2026-09-23) ──────────────────────────────────────────────────

    [Fact]
    public void Theme_Labels_ArePinned()
    {
        var data = new AppSettingsData();
        Assert.False(SettingsMenu.IsToggle(SettingsField.Theme));
        Assert.Equal("Theme", SettingsMenu.FieldName(SettingsField.Theme));
        Assert.Equal("synthwave", SettingsMenu.FieldValue(SettingsField.Theme, data, _settings.ProfileDirectory));
        Assert.Equal("noir", SettingsMenu.FieldValue(SettingsField.Theme, new AppSettingsData { Theme = "noir" }, _settings.ProfileDirectory));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.Theme) || SettingsMenu.IsTtsField(SettingsField.Theme) || SettingsMenu.IsVoiceField(SettingsField.Theme) || SettingsMenu.IsMcpField(SettingsField.Theme));
        Assert.True(SettingsMenu.RefusedMidTurn(SettingsField.Theme));   // a change starts the screen over
        Assert.Equal("synthwave [#9A8BB8]default theme[/]", SettingsMenu.ThemeLabel("synthwave"));
        Assert.Equal("noir      [#9A8BB8]greyscale[/]", SettingsMenu.ThemeLabel("noir"));
        Assert.Equal("nostromo  [#9A8BB8]amber phosphor[/]", SettingsMenu.ThemeLabel("nostromo"));
        Assert.Equal("No theme named \"matrix\". /theme takes synthwave, netrunner, nostromo, noir, cyberpunk, vaporwave, mainframe, grid, replicant or abyssal, or nothing to pick from a list.", SettingsMenu.ThemeNameError("matrix"));
        Assert.Equal("Theme: noir (already in force)", SettingsMenu.ThemeAlreadyNotice("noir"));
        Assert.Equal("Theme: netrunner", SettingsMenu.SavedNotice(SettingsField.Theme, new AppSettingsData { Theme = "netrunner" }, _settings.ProfileDirectory));
    }

    [Fact]
    public async Task Theme_IsTheLastRow_APicker_PutInForceAtOnce_AndTheScreenHearsIt()
    {
        using var scope = new ThemeScope();
        Down(Array.IndexOf(Enum.GetValues<SettingsField>(), SettingsField.Theme));   // the last row until the ComfyUI rows came after it (2026-09-24)
        Push(Keys.Enter);                           // Theme: the picker opens on synthwave
        Push(Keys.Down, Keys.Enter);                // netrunner
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Theme, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("netrunner", _settings.Current.Theme);
        Assert.Same(ThemePalette.Netrunner, Theme.Current);
        Assert.Contains(Breadcrumb("Theme"), _console.Output);
        Assert.Contains("  · Theme: netrunner", _console.Output);
        Assert.Equal(0, _synth.ListCalls);          // no server is consulted
    }

    [Fact]
    public async Task Theme_PickedAndPickedBack_IsNoChangeForTheScreen()
    {
        using var scope = new ThemeScope();
        Down(Array.IndexOf(Enum.GetValues<SettingsField>(), SettingsField.Theme));   // the last row until the ComfyUI rows came after it (2026-09-24)
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);   // nostromo
        Push(Keys.Enter, Keys.Up, Keys.Up, Keys.Enter);       // synthwave again
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("synthwave", _settings.Current.Theme);
        Assert.Same(ThemePalette.Synthwave, Theme.Current);
    }

    [Fact]
    public async Task Theme_EscapeKeepsIt()
    {
        using var scope = new ThemeScope();
        Down(Array.IndexOf(Enum.GetValues<SettingsField>(), SettingsField.Theme));   // the last row until the ComfyUI rows came after it (2026-09-24)
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("synthwave", _settings.Current.Theme);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    // ── Tree max length / Tree show sizes ───────────────────────────────────

    [Fact]
    public async Task TreeMaxLength_IsRow36_Typed_NoReconnect_AndOutOfRangeIsRefused()
    {
        _console.Profile.Width = 240;
        Down(35);
        Push(Keys.Enter);                       // Tree max length shows "500"
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("0");
        Push(Keys.Enter);                       // refused, the row stays open with "500"
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("10001");
        Push(Keys.Enter);                       // refused again
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("250");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(250, _settings.Current.FileTreeMaxLength);
        Assert.Contains(SettingsMenu.TreeMaxLengthRangeError, _console.Output);
        Assert.Contains("keeping 500", _console.Output);
        Assert.Contains("  · File /tree max length: 250 entries", _console.Output);
        Assert.Equal(0, _synth.ListCalls);          // no server is consulted
    }

    [Fact]
    public async Task Toggle_TreeShowSizes_IsRow37_PersistsAndNeedsNoReconnect()
    {
        Assert.True(_settings.Current.FileTreeShowSizes);
        Down(36);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.FileTreeShowSizes);
        Assert.Contains("  · File /tree show sizes: off", _console.Output);
    }

    // ── LLM use fun verbs ───────────────────────────────────────────────────

    [Fact]
    public async Task Toggle_ThinkingFunVerbs_IsRow39_PersistsAndNeedsNoReconnect()
    {
        Assert.False(_settings.Current.LlmUseFunVerbs);
        Down(38);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked          // one row from the end

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.True(_settings.Current.LlmUseFunVerbs);
        Assert.Contains("  · 🖥️ LLM use fun verbs: on", _console.Output);
    }

    // ── LLM offer tools ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Toggle_LlmTools_IsRow33_PersistsAndNeedsNoReconnect_ButClearsTheConversation()
    {
        Assert.True(_settings.Current.LlmOfferTools);
        Down(32);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked          // the row above LLM max tool iterations

        Assert.Equal(SettingsChanges.Conversation, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.LlmOfferTools);
        Assert.Contains("  · 🖥️ LLM offer tools: off", _console.Output);
    }

    [Fact]
    public async Task Toggle_LlmTools_BackOn_ClearsTheConversationToo()
    {
        _settings.Update(d => d.LlmOfferTools = false);
        Down(32);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.Conversation, await _menu.ShowAsync(CancellationToken.None));

        Assert.True(_settings.Current.LlmOfferTools);
        Assert.Contains("  · 🖥️ LLM offer tools: on", _console.Output);
    }

    // ── New profile mode ────────────────────────────────────────────────────

    [Fact]
    public async Task NewProfileMode_IsRow38_APicker_NoReconnect()
    {
        Down(37);
        Push(Keys.Enter);                           // New profile mode: the picker opens on basic (the default, the first row)
        Push(Keys.Down, Keys.Enter);                // advanced
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("advanced", _settings.Current.NewProfileMode);
        Assert.Contains(Breadcrumb("New profile mode"), _console.Output);
        Assert.Contains("  · New profile mode: advanced", _console.Output);
        Assert.Equal(0, _synth.ListCalls);          // no server is consulted
    }

    [Fact]
    public async Task NewProfileMode_OpensOnTheSavedMode_AndEscapeKeepsIt()
    {
        _settings.Update(d => d.NewProfileMode = "advanced");
        Down(36);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("advanced", _settings.Current.NewProfileMode);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task NewProfileMode_UpFromAdvanced_PicksBasic()
    {
        _settings.Update(d => d.NewProfileMode = "advanced");
        Down(37);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // advanced → basic: the picker opened on the saved row

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("basic", _settings.Current.NewProfileMode);
        Assert.Contains("  · New profile mode: basic", _console.Output);
    }

    // ── LLM scan mode ───────────────────────────────────────────────────────

    [Fact]
    public async Task LlmScanMode_IsRow40_APicker_NoReconnect()
    {
        Down(39);
        Push(Keys.Enter);                           // LLM scan mode: the picker opens on local (the first row)
        Push(Keys.Down, Keys.Enter);                // remote
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("remote", _settings.Current.LlmScanMode);
        Assert.Contains(Breadcrumb("LLM scan mode"), _console.Output);
        Assert.Contains("  · 🖥️ LLM scan mode: remote", _console.Output);
        Assert.Contains("the usual ports on every other machine on the local network", _console.Output);
        Assert.Equal(0, _synth.ListCalls);          // no server is consulted
    }

    [Fact]
    public async Task LlmScanMode_OpensOnTheSavedMode_AndEscapeKeepsIt()
    {
        _settings.Update(d => d.LlmScanMode = "both");
        Down(38);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("both", _settings.Current.LlmScanMode);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task LlmScanMode_UpFromBoth_PicksRemote_AndTheUrlRowFollows()
    {
        _settings.Update(d => d.LlmScanMode = "both");
        Down(39);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // both → remote

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("remote", _settings.Current.LlmScanMode);
        Assert.Contains("(probe local ports and the network)", _console.Output);   // the list as it opened
    }

    // ── TTS source (flat row 52, under TTS output on the TTS tab) ───────────

    [Fact]
    public async Task TtsSource_IsRow53_APicker_AndReconnectsSpeech()
    {
        Down(52);
        Push(Keys.Enter);                           // TTS source: the picker opens on the fixture's http (the first row)
        Push(Keys.Down, Keys.Enter);                // in-process
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("in-process", _settings.Current.TtsSource);
        Assert.Contains(Breadcrumb("TTS source"), _console.Output);
        Assert.Contains("  · TTS source: in-process", _console.Output);
        Assert.Contains("kokoro.onnx (326 MB) downloads on first use", _console.Output);
        Assert.Equal(0, _synth.ListCalls);          // the menu itself connects nothing; the screen reconnects after it closes
        Assert.Equal(0, _synth.PrepareCalls);
    }

    [Fact]
    public async Task MentionFolderMode_IsAPicker_Row54()
    {
        Down(54);
        Push(Keys.Enter);                           // @-mention folder mode: the picker opens on the default (folder-remain, the second row, since 2026-09-19)
        Push(Keys.Up, Keys.Enter);                  // folder-apply
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("folder-apply", _settings.Current.FileMentionFolderMode);
        Assert.Contains(Breadcrumb("File @-mention folder mode"), _console.Output);
        Assert.Contains("  · File @-mention folder mode: folder-apply", _console.Output);
        Assert.Contains("insert @folder/ and close the list", _console.Output);
    }

    [Fact]
    public async Task MentionFolderMode_OpensOnTheSavedMode_AndEscapeKeepsIt()
    {
        _settings.Update(d => d.FileMentionFolderMode = "folder-apply");
        Down(53);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("folder-apply", _settings.Current.FileMentionFolderMode);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task MentionFolderMode_MidTurn_IsAllowed()
    {
        Down(54);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None, midTurn: true));
        Assert.Equal("folder-apply", _settings.Current.FileMentionFolderMode);
    }

    [Fact]
    public async Task TtsSource_OpensOnTheSavedSource_AndEscapeKeepsIt()
    {
        _settings.Update(d => d.TtsSource = "in-process");
        Down(51);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("in-process", _settings.Current.TtsSource);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task TtsSource_UpFromInProcess_PicksHttp()
    {
        _settings.Update(d => d.TtsSource = "in-process");   // the compiled default; the fixture saved http
        Down(52);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // in-process → http

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("http", _settings.Current.TtsSource);
        Assert.Contains("  · TTS source: http", _console.Output);
    }

    [Fact]
    public async Task TtsSource_MidTurn_IsRefused()
    {
        Down(52);
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None, midTurn: true));
        Assert.Equal("http", _settings.Current.TtsSource);
        Assert.Contains(SettingsMenu.NotWhileReplyRunsNotice, _console.Output);
    }

    [Fact]
    public async Task VoicePicker_UnderInProcess_ListsThroughTheSourcesSynthesizer_WithoutAConnect()
    {
        // Saved in-process while the session is connected over http: the picker asks a throwaway
        // synthesizer for that source (the fake, listing its three voices) and never prepares one.
        await ConnectSpeechAsync();
        _settings.Update(d => d.TtsSource = "in-process");
        Down(11);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // TTS voice → af_bella

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("af_bella", _settings.Current.TtsVoice);
        Assert.Equal(1, _synth.PrepareCalls);       // the fixture's connect alone
        Assert.True(_synth.ListCalls >= 1);
        Assert.Empty(_synth.Spoken);                // no preview: the connected source is not the saved one
    }

    // ── LLM tool compact type (flat row 31, under LLM auto compact (%) on the LLM tab) ──

    [Fact]
    public async Task ToolCompactType_IsRow32_APicker_NoReconnect()
    {
        Down(31);
        Push(Keys.Enter);                           // LLM tool compact type: the picker opens on compact (the first row, the default since 2026-09-28)
        Push(Keys.Down, Keys.Down, Keys.Enter);     // prune, then stop
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("stop", _settings.Current.LlmToolCompactType);
        Assert.Contains(Breadcrumb("LLM tool compact type"), _console.Output);
        Assert.Contains("  · 🖥️ LLM tool compact type: stop", _console.Output);
        Assert.Contains("end the turn with a notice; /compact or /clear first", _console.Output);
        Assert.Contains("no check; the server's own limit answers", _console.Output);
        Assert.Contains("prune, then summarise if the turn is still over the share", _console.Output);
    }

    [Fact]
    public async Task ToolCompactType_OpensOnTheSavedType_AndEscapeKeepsIt()
    {
        _settings.Update(d => d.LlmToolCompactType = "nothing");
        Down(30);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("nothing", _settings.Current.LlmToolCompactType);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task ToolCompactType_UpFromNothing_PicksStop()
    {
        _settings.Update(d => d.LlmToolCompactType = "nothing");
        Down(31);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // nothing → stop

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("stop", _settings.Current.LlmToolCompactType);
    }

    // ── The web rows (flat rows 40–45; on the General tab under Memory) ─────

    [Fact]
    public async Task Toggle_WebTools_IsRow41_PersistsAndNeedsNoReconnect()
    {
        Assert.True(_settings.Current.WebTools);
        Down(40);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.WebTools);
        Assert.Contains("  · Web tools: off", _console.Output);
    }

    [Fact]
    public async Task WebBrowserMode_IsRow42_APicker_NoReconnect()
    {
        Down(41);
        Push(Keys.Enter);                           // the picker opens on default (the first row)
        Push(Keys.Down, Keys.Down, Keys.Enter);     // chromium
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("chromium", _settings.Current.WebBrowserMode);
        Assert.Contains(Breadcrumb("Web browser mode"), _console.Output);
        Assert.Contains("  · Web browser mode: chromium", _console.Output);
        Assert.Contains("HttpClient, then a headless browser when a page is blocked or empty", _console.Output);
        Assert.Contains("a headless Edge, Chrome or Brave for every page", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task WebBrowserMode_OpensOnTheSavedMode_AndEscapeKeepsIt()
    {
        _settings.Update(d => d.WebBrowserMode = "httpclient");
        Down(40);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("httpclient", _settings.Current.WebBrowserMode);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task WebBrowserPath_IsRow43_Typed_AMissingFileIsRefused_AndEmptyMeansAuto()
    {
        _console.Profile.Width = 240;
        string exe = Path.Combine(_dir, "chrome.exe");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(exe, "");
        Down(42);
        Push(Keys.Enter);                           // Browser path: empty
        _console.Input.PushText(Path.Combine(_dir, "nope.exe"));
        Push(Keys.Enter);                           // refused, the row stays open
        Push(Keys.Enter);
        _console.Input.PushText(exe);
        Push(Keys.Enter);                           // saved
        Push(Keys.Enter);
        Backspace(exe.Length);
        Push(Keys.Enter, Keys.Escape);              // cleared: auto again

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("", _settings.Current.WebBrowserPath);
        Assert.Contains("Web browser path " + SettingsMenu.BrowserPathError + "; keeping (auto: msedge.exe).", _console.Output);
        Assert.Contains("  · Web browser path: " + exe, _console.Output);
        Assert.Contains("  · Web browser path: (auto: msedge.exe)", _console.Output);
    }

    [Fact]
    public async Task WebBrowserNetworkMode_IsRow44_APicker_InternetByDefault_PersistsAndNeedsNoReconnect()
    {
        // The on/off Browser allow LAN until 2026-09-18, this slot kept.
        Assert.Equal("internet", _settings.Current.WebBrowserNetworkMode);
        Down(43);
        Push(Keys.Enter);                           // the picker opens on internet (the first row)
        Push(Keys.Down, Keys.Enter);                // local_area_network
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("local_area_network", _settings.Current.WebBrowserNetworkMode);
        Assert.Contains(Breadcrumb("Web browser network mode"), _console.Output);
        Assert.Contains("  · Web browser network mode: local_area_network", _console.Output);
        Assert.Contains("public addresses alone; this machine and the local network refused", _console.Output);
        Assert.Contains("this machine and the local network alone; the internet refused", _console.Output);
        Assert.Contains("every address", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task WebBrowserNetworkMode_OpensOnTheSavedMode_AndEscapeKeepsIt()
    {
        _settings.Update(d => d.WebBrowserNetworkMode = "both");
        Down(42);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("both", _settings.Current.WebBrowserNetworkMode);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task SearxngUrl_IsRow45_Typed_NotAUrlIsRefused_AndEmptyMeansDuckDuckGo()
    {
        _console.Profile.Width = 240;
        Down(44);
        Push(Keys.Enter);                           // SearXNG URL: empty
        _console.Input.PushText("localhost:8080");
        Push(Keys.Enter);                           // refused (no scheme)
        Push(Keys.Enter);
        _console.Input.PushText("http://localhost:8080");
        Push(Keys.Enter);                           // saved
        Push(Keys.Enter);
        Backspace(21);
        Push(Keys.Enter, Keys.Escape);              // cleared

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("", _settings.Current.WebSearxngUrl);
        Assert.Contains("Web SearXNG URL " + SettingsMenu.SearxngUrlError + "; keeping (not set).", _console.Output);
        Assert.Contains("  · Web SearXNG URL: http://localhost:8080", _console.Output);
        Assert.Contains("  · Web SearXNG URL: (not set)", _console.Output);
    }

    [Fact]
    public async Task WebSearchResults_IsRow46_Typed_NoReconnect_AndOutOfRangeIsRefused()
    {
        Down(45);
        Push(Keys.Enter);                           // Web search max results shows "20" (two Backspaces clear it)
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("0");
        Push(Keys.Enter);                           // refused
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("21");
        Push(Keys.Enter);                           // refused again
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("5");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(5, _settings.Current.WebSearchMaxResults);
        Assert.Contains(SettingsMenu.WebSearchMaxResultsRangeError, _console.Output);
        Assert.Contains("keeping 20", _console.Output);
        Assert.Contains("  · Web search max results: 5 results", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task OnThePane_LlmScanMode_IsTheLlmTabsFirstRow_AboveTheUrl()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Llm);                       // the LLM tab, its first row
        Push(Keys.Enter, Keys.Down, Keys.Enter); // remote
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("remote", _settings.Current.LlmScanMode);
        Assert.Contains("\n" + Titled(Strip) + "\n  · 🖥️ LLM scan mode: remote\n▸ LLM scan mode                   remote\n  LLM URL                         (scan the local network)\n", _console.Output);
        Assert.Equal(0, pane.FlowRow);
        pane.Dispose();
    }

    // ── Reasoning ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Reasoning_IsRow5_APickerAndAnLlmChange()
    {
        _overrides[SettingsField.LlmReasoning] = EnvironmentOverrides.LlmReasoningVariable;
        Down(5);
        Push(Keys.Enter);                           // LLM reasoning (after LLM API key): the picker opens on none
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // high
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("high", _settings.Current.LlmReasoning);
        Assert.Contains(Breadcrumb("LLM reasoning"), _console.Output);
        Assert.Contains("  · 🖥️ LLM reasoning: high", _console.Output);
        Assert.Contains("  ! " + SettingsMenu.OverrideNotice(EnvironmentOverrides.LlmReasoningVariable), _console.Output);
        Assert.Equal(0, _synth.ListCalls);          // no server is consulted
    }

    [Fact]
    public async Task Reasoning_OpensOnTheSavedLevel_AndEscapeKeepsIt()
    {
        _settings.Update(d => d.LlmReasoning = "medium");
        Down(5);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("medium", _settings.Current.LlmReasoning);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task Reasoning_UpFromTheSavedLevel_PicksTheOneAbove()
    {
        _settings.Update(d => d.LlmReasoning = "medium");
        Down(5);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // medium → low

        Assert.Equal(SettingsChanges.Llm, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("low", _settings.Current.LlmReasoning);
    }

    // ── TTS voice ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Voice_PicksFromTheServerList()
    {
        Down(11);
        Push(Keys.Enter);               // TTS voice: the picker opens on af_heart
        Push(Keys.Down, Keys.Enter);    // af_bella
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("af_bella", _settings.Current.TtsVoice);
        Assert.Contains(Breadcrumb("TTS voice"), _console.Output);
        Assert.Contains("  · TTS voice: af_bella", _console.Output);
        Assert.Equal(1, _synth.ListCalls);
    }

    [Fact]
    public async Task Voice_Escape_KeepsTheVoice()
    {
        Down(11);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("af_heart", _settings.Current.TtsVoice);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task Voice_UnlistedSavedVoice_IsOfferedFirst()
    {
        _settings.Update(d => d.TtsVoice = "zz_custom");
        Down(11);
        Push(Keys.Enter, Keys.Enter, Keys.Escape);   // the first row is the saved voice, inserted because the server did not list it

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal("zz_custom", _settings.Current.TtsVoice);
    }

    [Fact]
    public async Task Voice_NoServer_FallsBackToTyping()
    {
        _synth.Exists = false;
        Down(11);
        Push(Keys.Enter);                       // TTS voice: no list, so the text edit shows "af_heart"
        for (int i = 0; i < 8; i++)
        {
            Push(Keys.Backspace);
        }

        _console.Input.PushText("bf_emma");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("bf_emma", _settings.Current.TtsVoice);
        Assert.Contains("did not list voices", _console.Output);
        Assert.DoesNotContain(Breadcrumb("TTS voice"), _console.Output);
    }

    // ── TTS voice 2 and the mix ─────────────────────────────────────────────

    [Fact]
    public async Task Voice2_IsRow11_PicksFromTheServerList_AfterANoneRow()
    {
        _settings.Update(d => d.TtsVoice2 = "");   // no second voice saved (the default is am_eric since 2026-09-16), so the picker opens on (none)
        Down(12);
        Push(Keys.Enter);                       // TTS voice 2: the picker opens on "(none)", the first row
        Push(Keys.Down, Keys.Down, Keys.Enter); // af_heart, af_bella
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("af_bella", _settings.Current.TtsVoice2);
        Assert.Contains(Breadcrumb("TTS voice 2"), _console.Output);
        Assert.Contains("  · TTS voice 2: af_bella", _console.Output);
        Assert.Equal(1, _synth.ListCalls);
    }

    [Fact]
    public async Task Voice2_PickingNone_ClearsIt()
    {
        _settings.Update(d => d.TtsVoice2 = "af_bella");
        Down(12);
        Push(Keys.Enter);                       // the picker opens on the saved af_bella
        Push(Keys.Up, Keys.Up, Keys.Enter);     // af_heart, (none)
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("", _settings.Current.TtsVoice2);
        Assert.Contains("  · TTS voice 2: (none)", _console.Output);
    }

    [Fact]
    public async Task Voice2_NoServer_TypedEmptyClears_AndATypedNameIsSaved()
    {
        _synth.Exists = false;
        _settings.Update(d => d.TtsVoice2 = "af_sky");
        Down(12);
        Push(Keys.Enter);                       // TTS voice 2: no list, so the text edit shows "af_sky"
        for (int i = 0; i < 6; i++)
        {
            Push(Keys.Backspace);
        }

        Push(Keys.Enter);                       // empty = none
        Push(Keys.Enter);                       // the cursor stays on the row: edit again, now empty
        _console.Input.PushText("bf_emma");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("bf_emma", _settings.Current.TtsVoice2);
        Assert.Contains("  · TTS voice 2: (none)", _console.Output);
        Assert.Contains("  · TTS voice 2: bf_emma", _console.Output);
        Assert.DoesNotContain(Breadcrumb("TTS voice 2"), _console.Output);
    }

    [Fact]
    public async Task VoiceMix_IsRow12_SavedRangeCheckedAndATtsChange()
    {
        Down(13);
        Push(Keys.Enter);                       // TTS voice mix "50"
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("70");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // the cursor stays on the row just edited: "70"
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("101");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // still "70"
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("abc");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal(70, _settings.Current.TtsVoiceMix);
        Assert.Contains("  · TTS voice mix: 70 % / 30 %", _console.Output);
        Assert.Contains(SettingsMenu.TtsVoiceMixRangeError, _console.Output);
        Assert.Contains("keeping 70", _console.Output);
        Assert.Equal(0, _synth.ListCalls);      // a typed number consults no server
    }

    // ── /model ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Model_PicksTheSecondListedId()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("first", "second"));
        using var session = Session();
        await session.ConnectAsync(new AppSettingsData(), CancellationToken.None);
        Push(Keys.Down, Keys.Enter);

        Assert.True(await _menu.PickModelAsync(session, "", CancellationToken.None));

        Assert.Equal("second", _settings.Current.LlmModel);
        Assert.Contains("  · 🖥️ LLM model: second", _console.Output);
        Assert.Contains(SettingsMenu.ModelTitle, _console.Output);
    }

    [Fact]
    public async Task Model_ListAlwaysContainsTheCurrentId()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("a", "b"));
        using var session = Session();
        await session.ConnectAsync(new AppSettingsData { LlmModel = "pinned" }, CancellationToken.None);
        Push(Keys.Enter);   // the first row is the current id, inserted because the server did not list it

        Assert.True(await _menu.PickModelAsync(session, "", CancellationToken.None));
        Assert.Equal("pinned", _settings.Current.LlmModel);
    }

    [Fact]
    public async Task Model_Escape_KeepsTheCurrentModel()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("a", "b"));
        using var session = Session();
        await session.ConnectAsync(new AppSettingsData(), CancellationToken.None);
        Push(Keys.Escape);

        Assert.False(await _menu.PickModelAsync(session, "", CancellationToken.None));
        Assert.Equal("", _settings.Current.LlmModel);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task Model_ServerWantsAKey_OffersOnlyTheCurrentId()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.Unauthorized, "{}");
        using var session = Session();
        await session.ConnectAsync(new AppSettingsData { LlmUrl = "http://127.0.0.1:1234" }, CancellationToken.None);
        Push(Keys.Enter);   // the list is never empty: the fallback id in use is its one row

        Assert.True(await _menu.PickModelAsync(session, "", CancellationToken.None));
        Assert.Equal(LlmEndpoint.FallbackModelId, _settings.Current.LlmModel);
    }

    [Fact]
    public async Task Model_WithArgument_SetsDirectly_NoMenu()
    {
        using var session = Session();
        Assert.True(await _menu.PickModelAsync(session, "  direct-id ", CancellationToken.None));
        Assert.Equal("direct-id", _settings.Current.LlmModel);
        Assert.DoesNotContain(SettingsMenu.ModelTitle, _console.Output);
    }

    [Fact]
    public async Task Model_NoUrlAtAll_IsAnError()
    {
        using var session = Session();
        await session.ConnectAsync(new AppSettingsData(), CancellationToken.None);   // nothing answers, nothing configured
        Assert.False(await _menu.PickModelAsync(session, "", CancellationToken.None));
        Assert.Contains(SettingsMenu.NoUrlError, _console.Output);
    }

    // ── /reasoning ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReasoningCommand_OpensOnTheCurrentLevel_AndPicks()
    {
        Push(Keys.Down, Keys.Enter);   // the cursor opens on the level in force, not the saved one

        Assert.True(await _menu.PickReasoningAsync("", "medium", CancellationToken.None));

        Assert.Equal("high", _settings.Current.LlmReasoning);
        Assert.Contains(SettingsMenu.ReasoningTitle, _console.Output);
        Assert.DoesNotContain(Breadcrumb("LLM reasoning"), _console.Output);
        Assert.Contains("  · 🖥️ LLM reasoning: high", _console.Output);
        Assert.Contains("maximum thinking, slowest", _console.Output);   // every level's hint is on its row
    }

    [Fact]
    public async Task ReasoningCommand_Escape_KeepsTheLevel()
    {
        _settings.Update(d => d.LlmReasoning = "low");
        Push(Keys.Escape);

        Assert.False(await _menu.PickReasoningAsync("", "low", CancellationToken.None));

        Assert.Equal("low", _settings.Current.LlmReasoning);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Theory]
    [InlineData(" XHIGH ", "xhigh")]
    [InlineData("high", "high")]
    [InlineData("None", "none")]
    public async Task ReasoningCommand_WithArgument_SetsDirectly_NoMenu(string argument, string expected)
    {
        _settings.Update(d => d.LlmReasoning = "medium");

        Assert.True(await _menu.PickReasoningAsync(argument, "medium", CancellationToken.None));

        Assert.Equal(expected, _settings.Current.LlmReasoning);
        Assert.Contains("  · 🖥️ LLM reasoning: " + expected, _console.Output);
        Assert.DoesNotContain(SettingsMenu.KeepKeys, _console.Output);   // no list was shown (the title is the notice's own words)
    }

    [Theory]
    [InlineData("lots")]
    [InlineData("extra high")]
    [InlineData("2")]
    public async Task ReasoningCommand_BadArgument_IsAnError_NothingSaved(string argument)
    {
        _settings.Update(d => d.LlmReasoning = "medium");

        Assert.False(await _menu.PickReasoningAsync(argument, "medium", CancellationToken.None));

        Assert.Equal("medium", _settings.Current.LlmReasoning);
        Assert.Contains("  ✗ " + SettingsMenu.ReasoningLevelError, _console.Output);
        Assert.DoesNotContain(SettingsMenu.KeepKeys, _console.Output);
    }

    [Fact]
    public async Task ReasoningCommand_Overridden_SavesWithTheWarning()
    {
        _overrides[SettingsField.LlmReasoning] = EnvironmentOverrides.LlmReasoningVariable;

        Assert.True(await _menu.PickReasoningAsync("high", "none", CancellationToken.None));

        Assert.Equal("high", _settings.Current.LlmReasoning);
        Assert.Contains("  · 🖥️ LLM reasoning: high", _console.Output);
        Assert.Contains("  ! " + SettingsMenu.OverrideNotice(EnvironmentOverrides.LlmReasoningVariable), _console.Output);
    }

    [Fact]
    public async Task ReasoningCommand_NonInteractiveConsole_PrintsTheGuard_ButTakesAnArgument()
    {
        using var plain = new TestConsole();
        plain.Profile.Width = 200;
        var transcript = new TranscriptRenderer(plain);
        var menu = new SettingsMenu(plain, _settings, _ => null, new InputLine(plain, new KeySource(plain.Input)), transcript, _speech, NoPane(plain));

        Assert.False(await menu.PickReasoningAsync("", "none", CancellationToken.None));
        Assert.Contains(SettingsMenu.MenusNeedTerminalError, plain.Output);

        Assert.True(await menu.PickReasoningAsync("low", "none", CancellationToken.None));
        Assert.Equal("low", _settings.Current.LlmReasoning);
    }

    // ── Profiles ────────────────────────────────────────────────────────────

    // ── /server ─────────────────────────────────────────────────────────────

    private static LlmServer Server(int port, string name, params string[] models) =>
        new(new Uri($"http://127.0.0.1:{port}/v1"), name, new ProbeResult(true, models, models.Length == 1 ? "1 chat model" : $"{models.Length} chat models"));

    [Fact]
    public async Task Server_OpensOnTheCurrentRow_UpPicksTheFirst()
    {
        var servers = new[] { Server(1234, "LM Studio", "lm"), Server(11434, "Ollama", "phi", "llama") };
        Push(Keys.Up, Keys.Enter);   // the cursor opens on the current (second) row

        var picked = await _menu.PickServerAsync(servers, servers[1].BaseUrl, SettingsMenu.ServerTitle, CancellationToken.None);

        Assert.Same(servers[0], picked);
        Assert.Contains(SettingsMenu.ServerTitle, _console.Output);
        Assert.Contains("LM Studio  http://127.0.0.1:1234/v1   1 chat model", _console.Output);   // the URLs padded to one column (2026-09-29)
        Assert.Contains("Ollama     http://127.0.0.1:11434/v1  2 chat models", _console.Output);
        Assert.Empty(_settings.Current.LlmUrl);   // picking saves nothing; the caller does
    }

    [Fact]
    public async Task Server_Escape_IsUnchanged_OnlyForTheCommand()
    {
        var servers = new[] { Server(1234, "LM Studio", "lm"), Server(8000, "vLLM", "v") };

        Push(Keys.Escape);
        Assert.Null(await _menu.PickServerAsync(servers, null, SettingsMenu.StartupServerTitle, CancellationToken.None));
        Assert.Contains(SettingsMenu.StartupServerTitle, _console.Output);
        Assert.DoesNotContain(SettingsMenu.UnchangedNotice, _console.Output);

        Push(Keys.Escape);
        Assert.Null(await _menu.PickServerAsync(servers, null, SettingsMenu.ServerTitle, CancellationToken.None));
        Assert.Contains("  · " + SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task Server_NonInteractiveConsole_ListsAndPicksNothing()
    {
        using var plain = new TestConsole();
        var transcript = new TranscriptRenderer(plain);
        var input = new InputLine(plain, new KeySource(plain.Input, TimeSpan.FromMilliseconds(1)));
        var menu = new SettingsMenu(plain, _settings, _ => null, input, transcript, _speech, NoPane(plain));
        var servers = new[] { Server(1234, "LM Studio", "lm"), Server(8000, "vLLM", "v") };

        Assert.Null(await menu.PickServerAsync(servers, null, SettingsMenu.StartupServerTitle, CancellationToken.None));

        // The plain console wraps at 80 cells: assert on the stable fragments.
        Assert.Contains("LLM servers: LM Studio http://127.0.0.1:1234/v1, vLLM", plain.Output);
        Assert.Contains("http://127.0.0.1:8000/v1", plain.Output);
        Assert.DoesNotContain(SettingsMenu.StartupServerTitle, plain.Output);
    }

    [Fact]
    public void SaveServer_WritesTheUrl_AndClearsTheModelOnlyWhenTheUrlChanged()
    {
        _settings.Update(d => d.LlmModel = "old-model");

        Assert.True(_menu.SaveServer(new Uri("http://127.0.0.1:8000/v1")));
        Assert.Equal("http://127.0.0.1:8000/v1", _settings.Current.LlmUrl);
        Assert.Equal("", _settings.Current.LlmModel);
        Assert.Contains("  · 🖥️ LLM URL: http://127.0.0.1:8000/v1", _console.Output);

        _settings.Update(d => d.LlmModel = "new-model");
        Assert.False(_menu.SaveServer(new Uri("http://127.0.0.1:8000/v1")));
        Assert.Equal("new-model", _settings.Current.LlmModel);

        _overrides[SettingsField.LlmUrl] = "--url";
        Assert.True(_menu.SaveServer(new Uri("http://127.0.0.1:1234/v1")));
        Assert.Contains("  ! " + SettingsMenu.OverrideNotice("--url"), _console.Output);
    }

    [Fact]
    public async Task ModelFromList_PicksFromTheListInHand_CurrentFirstWhenUnlisted()
    {
        var listed = new ProbeResult(true, new[] { "a", "b" }, "2 chat models");
        Push(Keys.Down, Keys.Enter);
        Assert.True(await _menu.PickModelFromListAsync(listed, "", CancellationToken.None));
        Assert.Equal("b", _settings.Current.LlmModel);

        Push(Keys.Enter);   // the unlisted current id is offered first
        Assert.True(await _menu.PickModelFromListAsync(listed, "pinned", CancellationToken.None));
        Assert.Equal("pinned", _settings.Current.LlmModel);

        Push(Keys.Escape);
        Assert.False(await _menu.PickModelFromListAsync(listed, "a", CancellationToken.None));
        Assert.Equal("pinned", _settings.Current.LlmModel);

        Assert.False(await _menu.PickModelFromListAsync(ProbeResult.Missing("refused"), "", CancellationToken.None));
        Assert.Contains("The server did not answer (refused). " + SettingsMenu.NoModelsListedError, _console.Output);
    }

    [Fact]
    public void ServerStrings_ArePinned()
    {
        Assert.Equal(SettingsMenu.ServerTitle + "   Enter = choose · ESC = keep", SettingsMenu.PromptTitle(SettingsMenu.ServerTitle, SettingsMenu.KeepKeys));
        Assert.Equal("LM Studio  [#EFE6FF]http://127.0.0.1:1234/v1[/][#9A8BB8]  1 chat model[/]", SettingsMenu.ServerLabel(Server(1234, "LM Studio", "lm")));
        Assert.Equal("Not a usable server URL: bad", SettingsMenu.ServerUrlError("bad"));
        Assert.Equal("http://127.0.0.1:9/v1 did not answer /v1/models (refused); using it anyway because you asked.",
            SettingsMenu.ServerNotAnsweringWarning(new Uri("http://127.0.0.1:9/v1"), "refused"));
    }

    [Fact]
    public void ProfileStrings_ArePinned()
    {
        Assert.Equal(@"Profile                               [#EFE6FF]work[/][#9A8BB8] (D:\home\profiles\work)[/]", SettingsMenu.ProfileLabel("work", @"D:\home\profiles\work"));
        Assert.Equal(@"Profile                               [#EFE6FF]p[/][#9A8BB8] (D:\h[[x]]\profiles\p)[/]", SettingsMenu.ProfileLabel("p", @"D:\h[x]\profiles\p"));   // the path escaped
        Assert.Equal("profiles: default (current), work", SettingsMenu.ProfileListLine(new[] { "default", "work" }, "default"));
        Assert.Equal("profiles: default, work (current)", SettingsMenu.ProfileListLine(new[] { "default", "work" }, "Work"));
        Assert.Equal("(🪪 already on profile \"default\")", SettingsMenu.AlreadyCurrentNotice("default"));
        Assert.Equal("(🪪 switched to profile \"work\"; conversation cleared)", SettingsMenu.SwitchedNotice("work"));
        Assert.Equal("Profile", SettingsMenu.FieldName(SettingsField.Profile));
        Assert.Equal("", SettingsMenu.FieldValue(SettingsField.Profile, new AppSettingsData(), _settings.ProfileDirectory));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.Profile, new AppSettingsData()));
        Assert.False(SettingsMenu.IsToggle(SettingsField.Profile));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.Profile));
        Assert.False(SettingsMenu.IsTtsField(SettingsField.Profile));
        Assert.False(SettingsMenu.IsVoiceField(SettingsField.Profile));
    }

    [Fact]
    public async Task Menu_ShowsTheLoadedProfile_OnTheFirstRow()
    {
        Push(Keys.Escape);

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Contains("Profile                               " + Profiles.DefaultName, _console.Output);
        Assert.StartsWith(Profiles.DefaultName, _settings.ProfileName);
    }

    [Fact]
    public async Task ProfileRow_PicksAnotherProfile_SwitchesTheStore_AndIsAProfileChange()
    {
        _settings.Update(d => d.LlmModel = "default-model");
        await _settings.FlushAsync();
        Profiles.Create(_dir, "work", new AppSettingsData { LlmModel = "work-model" });
        Push(Keys.Enter);                       // the Profile row
        Push(Keys.Down, Keys.Enter);            // default → work
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Profile, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("work", _settings.ProfileName);
        Assert.Equal("work-model", _settings.Current.LlmModel);
        Assert.DoesNotContain(SettingsMenu.SwitchedNotice("work"), _console.Output);   // the screen announces it, after its redraw
        Assert.Contains("Profile                               work", _console.Output);   // the menu again, on the new profile
    }

    [Fact]
    public async Task ProfileRow_PickingTheLoadedOne_SaysSo_AndChangesNothing()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());
        Push(Keys.Enter, Keys.Enter, Keys.Escape);    // the Profile row, then default (opened on it)

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
        Assert.Contains("  · " + SettingsMenu.AlreadyCurrentNotice(Profiles.DefaultName), _console.Output);
    }

    [Fact]
    public async Task ProfileRow_Escape_KeepsTheProfile()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());
        Push(Keys.Enter, Keys.Down, Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
        Assert.Contains("  · " + SettingsMenu.UnchangedNotice, _console.Output);
    }

    [Fact]
    public async Task PickProfile_WithoutMenus_ListsThem_AndSwitchesNothing()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());
        var plain = new TestConsole();   // not interactive
        var menu = new SettingsMenu(plain, _settings, _ => null, new InputLine(plain, new KeySource(plain.Input, TimeSpan.FromMilliseconds(1))), new TranscriptRenderer(plain), _speech, NoPane(plain));

        Assert.False(await menu.PickProfileAsync(CancellationToken.None));

        Assert.Contains("  · profiles: default (current), work", plain.Output);
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
    }
    // ── Working directory ───────────────────────────────────────────────────

    [Fact]
    public async Task WorkingDirectory_IsRow24_FullPathOrEmpty_CreatedOnSave_NoReconnect()
    {
        _console.Profile.Width = 240;
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Down(23);
        Push(Keys.Enter);                       // Working directory ""
        _console.Input.PushText(elsewhere);
        Push(Keys.Enter);
        Push(Keys.Enter);                       // the cursor stays on the row: the saved path
        Push(Keys.Escape);                      // one ESC keeps the saved value
        Push(Keys.Enter);
        Backspace(elsewhere.Length);            // the saved path deleted, another typed
        _console.Input.PushText("not\\rooted");
        Push(Keys.Enter);                       // refused, back to the menu
        Push(Keys.Enter);
        Backspace(elsewhere.Length);
        Push(Keys.Enter);                       // empty = back to the profile's folder
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.True(Directory.Exists(elsewhere));
        Assert.Contains("  · Working directory (cwd): " + elsewhere, _console.Output);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
        Assert.Contains("  ✗ Working directory (cwd) " + SettingsMenu.WorkingDirectoryError + "; keeping " + elsewhere + ".", _console.Output);
        Assert.Contains("  · Working directory (cwd): " + SettingsMenu.DefaultWorkingDirectoryLabel(_settings.ProfileDirectory), _console.Output);
        Assert.Equal("", _settings.Current.WorkingDirectory);
    }

    [Fact]
    public void TrySaveWorkingDirectory_IsTheOneSavePath()
    {
        _console.Profile.Width = 240;
        string elsewhere = Path.Combine(_dir, "made", "here") + "\\";
        Assert.True(_menu.TrySaveWorkingDirectory(" " + elsewhere + " "));
        Assert.Equal(Path.Combine(_dir, "made", "here"), _settings.Current.WorkingDirectory);   // full, no trailing separator
        Assert.True(Directory.Exists(elsewhere));

        Assert.False(_menu.TrySaveWorkingDirectory("relative"));
        Assert.Equal(Path.Combine(_dir, "made", "here"), _settings.Current.WorkingDirectory);

        // A path that cannot be created: a file is in the way.
        string blocked = Path.Combine(_dir, "file.txt");
        File.WriteAllText(blocked, "x");
        Assert.False(_menu.TrySaveWorkingDirectory(Path.Combine(blocked, "sub")));
        Assert.Contains("  ✗ Could not create " + Path.Combine(blocked, "sub") + " (", _console.Output);
        Assert.Contains("; keeping " + Path.Combine(_dir, "made", "here") + ".", _console.Output);

        Assert.True(_menu.TrySaveWorkingDirectory(""));
        Assert.Equal("", _settings.Current.WorkingDirectory);
        Assert.True(SettingsMenu.IsWorkingDirectoryCandidate(""));
        Assert.True(SettingsMenu.IsWorkingDirectoryCandidate(@"C:\x"));
        Assert.False(SettingsMenu.IsWorkingDirectoryCandidate("x"));
        Assert.False(SettingsMenu.IsWorkingDirectoryCandidate(null));
    }

    // ── On the pane ─────────────────────────────────────────────────────────

    /// <summary>The menu over a pane with geometry: every list is a level of the pane, the notices its status line.</summary>
    /// <param name="browseFolder">The folder picker the Working directory (cwd) row opens (2026-09-22); null leaves the row asking for a typed path, as it did before the picker.</param>
    private (SettingsMenu Menu, ScreenPane Pane) PaneMenu(Func<CancellationToken, Task<string?>>? browseFolder = null)
    {
        _console.Profile.Height = 40;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider()) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menu = new SettingsMenu(new ConsoleWithInput(pane, keys), _settings, f => _overrides.GetValueOrDefault(f), new InputLine(pane, keys), new TranscriptRenderer(pane), _speech, new MenuPane(pane, keys), _ => FakeBrowserPath, browseFolder: browseFolder);
        pane.Show();
        return (menu, pane);
    }

    /// <summary>
    /// The Working directory (cwd) row over a pane (2026-09-22, the user's ask): Enter opens the
    /// <c>/cwd browse</c> folder picker instead of asking for a typed path, and what it chose goes
    /// through the one save — which creates the folder. <c>/cwd &lt;path&gt;</c> still types one.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheWorkingDirectoryRow_OpensTheFolderPicker_AndSavesWhatItChose()
    {
        _console.Profile.Width = 240;
        string picked = Path.Combine(_dir, "picked");
        int opened = 0;
        var (menu, _) = PaneMenu(_ =>
        {
            opened++;
            return Task.FromResult<string?>(picked);
        });
        Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Escape);   // Profile, New profile mode, then the working directory row

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(1, opened);
        Assert.Equal(picked, _settings.Current.WorkingDirectory);
        Assert.True(Directory.Exists(picked));
        Assert.DoesNotContain(SettingsMenu.WorkingDirectoryError, _console.Output);   // nothing was typed, so nothing was rejected
    }

    /// <summary>The picker closed with nothing chosen: the setting stands and the row says so, as every cancelled edit does.</summary>
    [Fact]
    public async Task OnThePane_TheWorkingDirectoryRow_PickerCancelled_KeepsTheSetting()
    {
        _console.Profile.Width = 240;
        _settings.Update(d => d.WorkingDirectory = _dir);
        var (menu, _) = PaneMenu(_ => Task.FromResult<string?>(null));
        Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(_dir, _settings.Current.WorkingDirectory);
        Assert.Contains(SettingsMenu.UnchangedNotice, _console.Output);
    }

    /// <summary>The picker chose the profile's own folder: the empty value is saved, so the row reads as the default again.</summary>
    [Fact]
    public async Task OnThePane_TheWorkingDirectoryRow_PickerChoseTheProfileFolder_ClearsTheSetting()
    {
        _console.Profile.Width = 240;
        _settings.Update(d => d.WorkingDirectory = _dir);
        var (menu, _) = PaneMenu(_ => Task.FromResult<string?>(""));   // what the screen returns for the profile's files folder
        Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("", _settings.Current.WorkingDirectory);
    }

    /// <summary><see cref="PaneMenu"/> over a scripted source that carries clicks, the overlay's first row at buffer row <paramref name="cursorTop"/> (the strip; the spacer under it, the rows from +2).</summary>
    private (SettingsMenu Menu, ScreenPane Pane, ScriptedInput Input) ClickablePaneMenu(int cursorTop)
    {
        _console.Profile.Height = 40;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => cursorTop), new ManualTimeProvider()) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var keys = new KeySource(input, TimeSpan.FromMilliseconds(1));
        var menu = new SettingsMenu(new ConsoleWithInput(pane, keys), _settings, f => _overrides.GetValueOrDefault(f), new InputLine(pane, keys), new TranscriptRenderer(pane), _speech, new MenuPane(pane, keys), _ => FakeBrowserPath);
        pane.Show();
        return (menu, pane, input);
    }

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    /// <summary>A title or strip row as the pane prints it since 2026-09-18: the text, then the × close glyph in column width − 2 (the console's width as the test set it).</summary>
    private string Titled(string row) => row + new string(' ', _console.Profile.Width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    /// <summary>The strip as the pane prints it: the label, then every tab title with a space either side, two spaces between. Pinned.</summary>
    private const string Strip = SettingsMenu.Title + "   General    Sessions    LLM    TTS    STT    Claude (API)    Embedded    Botchat ";   // six since 2026-09-25 (Botchat); five tabs since 2026-09-19: Ask, Files and Web are /tools' (ToolsMenuTests), Skills is /skills' Options tab (SkillsMenuTests)

    [Fact]
    public async Task OnThePane_TheListOpensOnTheGeneralTab_AndEscClosesIt()
    {
        // Wide enough for the working directory row: the profile's files folder, spelt out (a temp path here).
        _console.Profile.Width = 240;
        var (menu, pane) = PaneMenu();
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        // General: its rows in their own order (Keep command history under Command typo intercept, 2026-09-25; Mouse in menus gone, 2026-09-21; Draft editor last, 2026-09-19; Show toolbar after Show working directory, 2026-09-21) (the six web rows moved to the Web tab and the two /tree rows to Files, 2026-09-15; Transcript markdown and Paste preview lines, 2026-09-16; the @-mention folder mode to Files, 2026-09-17; the two line switches, Welcome splash, then Show working directory last, and the queue's two rows under Working directory, 2026-09-18), padded to the tab's own column (25), nothing of the other tabs.
        string cwd = SettingsMenu.DefaultWorkingDirectoryLabel(_settings.ProfileDirectory);
        Assert.StartsWith("(", cwd);
        Assert.EndsWith(@"\profiles\default\files)", cwd);
        Assert.Contains(Rule(240) + "\n" + Titled(Strip) + "\n \n▸ Profile                      default (" + _settings.ProfileDirectory + ")\n  New profile mode             basic\n  Working directory (cwd)      " + cwd + "\n  Queue messages               on\n  Queue cancel mode            empty\n  Memory                       on\n  Copy user prompt             on\n  Show image thumbnails        on\n  Image thumbnail size         small\n  Transcript markdown          on\n  Paste preview lines          25 lines\n  Hide /exit autocomplete      on\n  Command typo intercept       on\n  Keep command history         on\n  Welcome splash               fullsize\n  Working directory in header  off\n  Show toolbar                 all\n  Theme                        synthwave\n  Draft editor                 (default .txt editor)\n  Image viewer                 (built-in viewer)\n  Themed image viewer          on\n" + Rule(240) + "\n" + SettingsMenu.TabKeys + "\n", _console.Output);
        Assert.DoesNotContain("File /tree max length", _console.Output);   // the Files tab's since 2026-09-15
        Assert.DoesNotContain("LLM URL", _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.EndsWith(Rule(240) + "\n› \n" + Rule(240) + "\nidle", _console.Output);
        Assert.Equal(0, pane.FlowRow);   // nothing reached the transcript
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_EveryTab_ShowsItsRowsInOrder()
    {
        var (menu, pane) = PaneMenu();
        Push(Keys.Right, Keys.Tab, Keys.Right, Keys.Right, Keys.Escape);   // Sessions, LLM (Tab), then TTS, STT (the three tool tabs left for /tools on 2026-09-19, the Skills tab for /skills later that day)

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        // Each tab under the strip, padded to its own column (28, 32, 19, 26), the whole tab in view, nothing of another tab on it.
        Assert.Contains("\n \n▸ Session logging             on\n  Session retention (days)    forever\n  Session naming mode         model-written\n  Session show name           all-names\n  Session tool                on\n  Session search max results  10 results\n  Session save thinking       off\n" + Rule(100), _console.Output);
        Assert.Contains("\n \n▸ LLM scan mode                   local\n  LLM URL                         (probe local ports)\n  LLM model                       (first listed)\n  LLM API key                     ", _console.Output);
        Assert.Contains("\n  LLM reasoning                   none\n  LLM request timeout (s)         3600\n  LLM turn timeout (s)            21600\n  LLM context length              (from the server)\n  LLM mid-turn usage              last-known\n  LLM compact type                summary\n  LLM compact keep recent         2 turns\n  LLM compact show summary        off\n  LLM auto compact (%)            85 %\n  LLM max turns                   auto\n  LLM offer tools                 on\n  LLM tool compact type           compact\n  LLM max tool iterations         10000 round trips\n  LLM use fun verbs               off\n  LLM show thinking               on\n  LLM preserve thinking           off\n  LLM reasoning estimate          chars\n  LLM sampling                    (server defaults)\n  LLM sampling from Hugging Face  off\n" + Rule(100), _console.Output);
        Assert.Contains("\n \n▸ TTS output         on\n  TTS source         http\n  TTS HTTP URL       http://localhost:8880/v1\n  TTS voice preview  on\n  TTS voice preset   neon\n  TTS voice          af_heart\n  TTS voice 2        am_eric\n  TTS voice mix      80 % / 20 %\n  TTS speed          1.2\n" + Rule(100), _console.Output);
        Assert.Contains("\n \n▸ STT input                 off\n  STT wake                  off\n  STT wake phrase           hey neon\n  STT interrupt             off\n  STT interrupt echo guard  100 %\n  STT interrupt confirm     200 ms\n  STT push-to-talk key      F4\n  STT whisper model         ggml-base.en.bin\n  STT vosk model            vosk-model-small-en-us-0.15\n" + Rule(100), _console.Output);
        Assert.DoesNotContain("Ask user", _console.Output);   // /tools' since 2026-09-19
        Assert.DoesNotContain("File tools", _console.Output);
        Assert.DoesNotContain("Web tools", _console.Output);
        Assert.DoesNotContain("Agent skills", _console.Output);   // /skills' Options tab since 2026-09-19
        Assert.DoesNotContain(MenuPane.MoreHint, _console.Output);
        Assert.Equal(0, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AToggle_SavesOntoTheStatusLine_NotTheTranscript()
    {
        var (menu, pane) = PaneMenu();
        _overrides[SettingsField.TtsOutput] = "X";
        GoTo(SettingsTab.Tts);                // the TTS tab, its first row
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off page (2026-09-17): on under the cursor, off picked

        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.TtsOutput);
        // The on/off page under the breadcrumb: the saved value on the cursor, each row with its sentence.
        Assert.Contains("\n" + Titled(Breadcrumb("TTS output")) + "\n \n▸ on  " + SettingsMenu.ToggleDescribe(SettingsField.TtsOutput, true) + "\n  off " + SettingsMenu.ToggleDescribe(SettingsField.TtsOutput, false) + "\n", _console.Output);
        // The status under the strip: the saved notice and the override warning, the tab's list under them with the cursor kept.
        Assert.Contains("\n" + Titled(Strip) + "\n  · TTS output: off\n  ! " + SettingsMenu.OverrideNotice("X") + "\n▸ TTS output         off  (overridden by X)\n  TTS source", _console.Output);
        Assert.Equal(0, pane.FlowRow);
        pane.Dispose();
    }

    /// <summary>The × at the strip's right edge (2026-09-18): on a nested page it is one level back — the list again, unchanged — and on the list it closes the pane.</summary>
    [Fact]
    public async Task OnThePane_TheCloseGlyph_BacksOutOfAPage_AndClosesTheList()
    {
        _console.Profile.Width = 240;
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        input.PushClick(4, 107);                 // Memory (a double-click; the queue's two rows above it since 2026-09-18)
        input.PushClick(4, 107);
        input.PushClick(238, 100);               // the × on the on/off page: back to the list
        input.PushClick(239, 100);               // the × on the list: closed
        input.Push(Keys.Escape);                 // never read

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.True(_settings.Current.Memory);
        Assert.Contains("\n" + Titled(Breadcrumb("Memory")) + "\n \n▸ on  ", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · " + SettingsMenu.UnchangedNotice + "\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.True(input.IsAvailable);
        pane.Dispose();
    }

    /// <summary>The user's example (2026-09-18): a double-click on General's Memory row opens its on/off page as Enter would, a double-click on off picks it.</summary>
    [Fact]
    public async Task OnThePane_ADoubleClickOnARow_OpensItsPage_AndOneOnAChoice_PicksIt()
    {
        _console.Profile.Width = 240;
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        Assert.True(_settings.Current.Memory);
        input.PushClick(4, 107);                 // Memory: strip 100, spacer 101, Profile 102, New profile mode 103, Working directory 104, Queue messages 105, Queue cancel mode 106
        input.PushClick(4, 107);
        input.PushClick(4, 103);                 // off: breadcrumb 100, spacer 101, on 102
        input.PushClick(6, 103);
        input.Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.Memory);
        Assert.Contains("\n" + Titled(Breadcrumb("Memory")) + "\n \n▸ on  " + SettingsMenu.ToggleDescribe(SettingsField.Memory, true) + "\n  off " + SettingsMenu.ToggleDescribe(SettingsField.Memory, false) + "\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · Memory: off\n  Profile                      default (", _console.Output);
        Assert.Contains("\n▸ Memory                       off\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>Two clicks on the transcript under a nested page (Memory's on/off under the list, later on 2026-09-18) close the whole settings menu at once: nothing saved, no list redrawn, the ESC left unread; a single one does nothing.</summary>
    [Fact]
    public async Task OnThePane_TwoClicksOffThePane_UnderANestedPage_CloseTheWholeMenu()
    {
        _console.Profile.Width = 240;
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        Assert.True(_settings.Current.Memory);
        input.PushClick(4, 107);                 // Memory (see the double-click test for the rows)
        input.PushClick(4, 107);
        input.PushClick(4, 50);                  // the transcript under the on/off page: a first
        input.Push(Keys.Down);                   // a key ends the pair: off under the cursor, nothing picked
        input.PushClick(4, 50);                  // a first again
        input.PushClick(60, 50);                 // the pair: every level closes
        input.Push(Keys.Escape);                 // never read

        int mark = _console.Output.Length;
        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.True(_settings.Current.Memory);
        Assert.False(pane.OverlayOpen);
        Assert.False(pane.Dismissed);
        Assert.True(input.IsAvailable);
        // Nothing after the page's draw: the unwinding drew no list.
        int page = _console.Output.LastIndexOf(Titled(Breadcrumb("Memory")), StringComparison.Ordinal);
        Assert.True(page > mark);
        Assert.DoesNotContain(Titled(Strip), _console.Output[page..]);
        Assert.Equal(0, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_MidTurn_TheReconnectingRows_AreRefused_AndTheOthersEdit()
    {
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter);                       // Profile: refused
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Working directory: refused
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // Memory (past the queue's two rows): a General toggle, its page opens, off picked, saved
        GoTo(SettingsTab.Llm); Push(Keys.Down, Keys.Enter);   // LLM URL: refused
        Push(Keys.Right, Keys.Enter);           // TTS output: refused
        Push(Keys.Right, Keys.Enter);           // STT input: refused
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None, midTurn: true));

        Assert.False(_settings.Current.Memory);
        Assert.True(_settings.Current.TtsOutput);
        Assert.False(_settings.Current.SttInput);
        // The refusal on the status line under the strip (a redraw repeats it, so no count), the toggle's own line too.
        Assert.Contains("\n" + Titled(Strip) + "\n  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n▸ Profile", _console.Output);
        Assert.Contains("  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n▸ TTS output", _console.Output);
        Assert.Contains("  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n▸ STT input", _console.Output);
        Assert.Contains("  · Memory: off", _console.Output);
        Assert.DoesNotContain(SettingsMenu.ProfileTitle + "\n", _console.Output);
        Assert.Equal(0, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public void RefusedMidTurn_IsTheProfile_TheSandbox_EveryReconnectingRow_AndTheToolsFlip()
    {
        foreach (var field in Enum.GetValues<SettingsField>())
        {
            bool expected = field is SettingsField.Profile or SettingsField.WorkingDirectory or SettingsField.LlmOfferTools or SettingsField.Theme
                || SettingsMenu.IsLlmField(field) || SettingsMenu.IsTtsField(field) || SettingsMenu.IsVoiceField(field) || SettingsMenu.IsMcpField(field);
            Assert.Equal(expected, SettingsMenu.RefusedMidTurn(field));
        }

        // The MCP master switch reconnects (2026-09-20), so it is refused; the timeout is read at the next connect.
        Assert.True(SettingsMenu.IsMcpField(SettingsField.McpServers));
        Assert.False(SettingsMenu.IsMcpField(SettingsField.McpConnectTimeoutSeconds));
        Assert.True(SettingsMenu.RefusedMidTurn(SettingsField.McpServers));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.McpConnectTimeoutSeconds));

        Assert.True(SettingsMenu.RefusedMidTurn(SettingsField.LlmUrl));
        Assert.True(SettingsMenu.RefusedMidTurn(SettingsField.TtsVoice));
        Assert.True(SettingsMenu.RefusedMidTurn(SettingsField.SttPushToTalkKey));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.LlmScanMode));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.LlmCompactType));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.LlmMaxToolIterations));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.TtsVoicePreview));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.Memory));
    }

    [Fact]
    public async Task OnThePane_Confirm_OpensOnNo_DownEnterIsYes_EnterAndEscAreNo()
    {
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Enter);
        Assert.True(await menu.ConfirmAsync("💾 Forget 2 memories?", CancellationToken.None));
        Assert.Contains("\n" + Titled("💾 Forget 2 memories?") + "\n \n▸ No\n  Yes\n" + Rule(100) + "\n" + SettingsMenu.ConfirmKeys + "\n", _console.Output);
        Assert.Contains("\n  No\n▸ Yes\n", _console.Output);
        Assert.False(pane.OverlayOpen);

        Push(Keys.Enter);
        Assert.False(await menu.ConfirmAsync("💾 Forget 2 memories?", CancellationToken.None));
        Push(Keys.Escape);
        Assert.False(await menu.ConfirmAsync("💾 Forget 2 memories?", CancellationToken.None));
        Assert.False(pane.OverlayOpen);
        Assert.Equal(0, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public async Task Confirm_WithoutThePane_IsAPrompt_OnNo()
    {
        Push(Keys.Enter);
        Assert.False(await _menu.ConfirmAsync("Empty the trash?", CancellationToken.None));
        Assert.Contains(SettingsMenu.PromptTitle("Empty the trash?", SettingsMenu.ConfirmKeys), _console.Output);
        Assert.Contains("No", _console.Output);
        Assert.Contains("Yes", _console.Output);

        Push(Keys.Down, Keys.Enter);
        Assert.True(await _menu.ConfirmAsync("Empty the trash?", CancellationToken.None));
        Assert.Equal(new[] { "No", "Yes" }, SettingsMenu.ConfirmRows);
        Assert.Equal(new Dictionary<char, int> { ['n'] = 0, ['y'] = 1 }, SettingsMenu.ConfirmHotkeys);
    }

    [Fact]
    public async Task OnThePane_Confirm_YAndNMoveTheCursor_EnterStillPicks_EscIsStillNo()
    {
        var (menu, pane) = PaneMenu();
        Push(Keys.Char('y'), Keys.Enter);
        Assert.True(await menu.ConfirmAsync("💾 Forget 2 memories?", CancellationToken.None));
        Assert.Contains("\n  No\n▸ Yes\n", _console.Output);

        _console.Clear();
        Push(Keys.Char('Y'), Keys.Char('n'), Keys.Enter);
        Assert.False(await menu.ConfirmAsync("💾 Forget 2 memories?", CancellationToken.None));
        Assert.Contains("\n  No\n▸ Yes\n", _console.Output);
        Assert.Contains("\n▸ No\n  Yes\n", _console.Output);

        _console.Clear();
        Push(Keys.Char('y'), Keys.Escape);
        Assert.False(await menu.ConfirmAsync("💾 Forget 2 memories?", CancellationToken.None));
        Assert.Contains("\n  No\n▸ Yes\n", _console.Output);

        Push(Keys.Char('x'), Keys.Enter);
        Assert.False(await menu.ConfirmAsync("💾 Forget 2 memories?", CancellationToken.None));
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ATypedEdit_RunsUnderTheList_EnterSaves_OneEscKeeps()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Llm); Push(Keys.Down, Keys.Down); // the LLM tab, its third row (the scan mode, then the URL, sit above)
        Push(Keys.Enter);                       // LLM model: the slot opens empty (first listed)
        _console.Input.PushText("qwen3");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // again, pre-filled with qwen3
        _console.Input.PushText("-typo");
        Push(Keys.Escape);                      // one ESC: back to the list, qwen3 kept
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("qwen3", _settings.Current.LlmModel);
        // The edit: the tab's list under its strip with the row marked, the slot under it, the edit keys in the hint row.
        Assert.Contains("\n" + Titled(Strip) + "\n \n  LLM scan mode                   local\n  LLM URL                         (probe local ports)\n▸ LLM model                       (first listed)\n", _console.Output);
        Assert.Contains("\n› \n" + Rule(100) + "\n" + SettingsMenu.EditKeys, _console.Output);
        Assert.Contains("qwen3-typo", _console.Output);
        // The results on the status line, never as a › line or a notice in the flow.
        Assert.Contains("\n" + Titled(Strip) + "\n  · 🖥️ LLM model: qwen3\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · " + SettingsMenu.UnchangedNotice + "\n", _console.Output);
        Assert.DoesNotContain("› qwen3", _console.Output);
        Assert.Equal(0, pane.FlowRow);
        pane.Dispose();
    }

    /// <summary>The field report of 2026-09-13: after a typed edit, ESC out of the pane left its top rule and title on the screen.</summary>
    [Fact]
    public async Task OnThePane_AfterATypedEdit_EscLeavesNothingOfThePaneBehind()
    {
        _console.EmitAnsiSequences();
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Stt); Push(Keys.Down, Keys.Down);   // the STT tab, its third row
        Push(Keys.Enter);                       // Wake phrase, pre-filled
        _console.Input.PushText(" there");
        Push(Keys.Enter);                       // saved: the list again
        Push(Keys.Escape);                      // closed

        Assert.Equal(SettingsChanges.Voice, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("hey neon there", _settings.Current.SttWakePhrase);
        Assert.False(pane.OverlayOpen);
        // Nothing is in the flow, so every lift (cursor up, erase to the end of the screen) must land on
        // row 0 — whatever shape the pane had before it. A lift from the wrong row leaves rows behind.
        AssertEveryEraseStartsAtRow(_console.Output, 0, height: 40);
        pane.Dispose();
    }

    /// <summary>
    /// Replays the cursor rows of an ANSI stream — line feeds, <c>ESC[nA</c>, <c>ESC[nB</c> — and asserts
    /// that every <c>ESC[J</c> (the pane's lift) is issued on <paramref name="row"/>.
    /// </summary>
    private static void AssertEveryEraseStartsAtRow(string output, int row, int height)
    {
        int cursor = 0;
        int erases = 0;
        for (int i = 0; i < output.Length; i++)
        {
            char c = output[i];
            if (c == '\n')
            {
                cursor = Math.Min(cursor + 1, height - 1);
                continue;
            }

            if (c != '\e' || i + 1 >= output.Length || output[i + 1] != '[')
            {
                continue;
            }

            int j = i + 2;
            while (j < output.Length && (char.IsDigit(output[j]) || output[j] == ';' || output[j] == '?'))
            {
                j++;
            }

            if (j >= output.Length)
            {
                break;
            }

            string arg = output[(i + 2)..j];
            int n = arg.Length == 0 ? 1 : int.TryParse(arg, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
            switch (output[j])
            {
                case 'A': cursor = Math.Max(0, cursor - n); break;
                case 'B': cursor = Math.Min(height - 1, cursor + n); break;
                case 'J':
                    erases++;
                    Assert.True(cursor == row, $"erase #{erases} at index {i} started on row {cursor}, expected {row}");
                    break;
            }

            i = j;
        }

        Assert.True(erases > 0, "no erase at all");
    }

    [Fact]
    public async Task OnThePane_ABadValue_IsAnErrorOnTheStatusLine_WithTheCursorOnTheRow()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.End);          // the TTS tab, its last row
        Push(Keys.Enter);                                // TTS speed, pre-filled "1.2"
        Backspace(3);
        _console.Input.PushText("9");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(1.2, _settings.Current.TtsSpeed);
        Assert.Contains("\n" + Titled(Strip) + "\n  ✗ TTS speed " + SettingsMenu.TtsSpeedRangeError + "; keeping 1.2.\n", _console.Output);
        string afterError = _console.Output[_console.Output.IndexOf("✗ TTS speed", StringComparison.Ordinal)..];
        Assert.Contains("\n  TTS voice mix      80 % / 20 %\n▸ TTS speed          1.2\n", afterError.Split(Rule(100))[0]);
        Assert.Equal(0, pane.FlowRow);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheProfileRow_IsASecondLevel_AndEscReturnsToTheList()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());
        _console.Profile.Width = 240;   // the profile row carries its directory (a temp path here)
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter);                       // Profile
        Push(Keys.Down, Keys.Escape);           // back to the settings list
        Push(Keys.Escape);                      // closed

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
        Assert.Contains(Rule(240) + "\n" + Titled(Breadcrumb("Profile")) + "\n \n▸ default\n  work\n" + Rule(240) + "\n" + SettingsMenu.SwitchKeys + "\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · " + SettingsMenu.UnchangedNotice + "\n▸ Profile                      default (", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ASingleLevelPicker_ClosesOnThePick_AndTheNoticeGoesToTheTranscript()
    {
        var (menu, pane) = PaneMenu();
        var listed = new ProbeResult(true, new[] { "a", "b" }, "2 chat models");
        Push(Keys.Down, Keys.Enter);

        Assert.True(await menu.PickModelFromListAsync(listed, "a", CancellationToken.None));

        Assert.Equal("b", _settings.Current.LlmModel);
        Assert.Contains(Rule(100) + "\n" + Titled(SettingsMenu.ModelTitle) + "\n \n▸ a\n  b\n" + Rule(100) + "\n" + SettingsMenu.KeepKeys + "\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        Assert.Contains("  · 🖥️ LLM model: b\n", _console.Output);
        Assert.Equal(1, pane.FlowRow);   // the notice is a transcript line, under no pane
        pane.Dispose();
    }

    // ── The voice preview ───────────────────────────────────────────────────

    /// <summary>The session as the screen has it after a connect with speech on: the fixture's saved URL, the fake server listing three voices.</summary>
    private async Task ConnectSpeechAsync()
    {
        await _speech.ConnectAsync(_settings.Current, null, CancellationToken.None);
        Assert.True(_speech.IsReady);
    }

    /// <summary>The preview phrase as <see cref="SentenceChunker"/> hands it to the synthesizer: two sentences, so two requests in the same voice at the same speed.</summary>
    private static (string Text, string Voice, double Speed)[] Preview(string voice, double speed) =>
        [.. PreviewSentences.Select(text => (text, voice, speed))];

    private static readonly string[] PreviewSentences = ["Hello.", "I am Neon, your friendly and concise terminal sidekick."];

    private async Task WaitForTheTailAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_speech.Playing is not null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Null(_speech.Playing);
    }

    [Fact]
    public async Task OnThePane_TheVoicePicker_SpeaksTheRowTheCursorLandsOn_AndEnterSavesIt()
    {
        await ConnectSpeechAsync();
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the TTS tab, TTS voice (its fifth row since 2026-09-16, sixth since 2026-09-27)
        Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Escape);             // af_heart → af_bella → bm_george

        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));
        await WaitForTheTailAsync();

        Assert.Equal("bm_george", _settings.Current.TtsVoice);
        // The row under the cursor spoke the phrase alone at the session's speed; af_bella may or may
        // not have reached the server before bm_george superseded it, so only the last is pinned.
        Assert.NotEmpty(_synth.Spoken);
        Assert.All(_synth.Spoken, s => { Assert.Contains(s.Text, PreviewSentences); Assert.Equal(1.2, s.Speed); });
        Assert.Equal("bm_george", _synth.Spoken[^1].Voice);
        Assert.DoesNotContain(_synth.Spoken, s => s.Voice == "af_heart");   // the opening row is not a move
        Assert.Contains("\n▸ TTS voice          bm_george\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public void VoicePreset_IsDerived_TheMatchingPresetOrCustom()
    {
        var data = _settings.Current;   // the fresh profile's af_heart + am_eric, 80, 1.2 is the neon preset

        Assert.Equal("TTS voice preset", SettingsMenu.FieldName(SettingsField.TtsVoicePreset));
        Assert.Equal("neon", SettingsMenu.FieldValue(SettingsField.TtsVoicePreset, data, _settings.ProfileDirectory));
        _settings.Update(d => d.TtsVoice = "af_sky");
        Assert.Equal(SettingsMenu.CustomPreset, SettingsMenu.FieldValue(SettingsField.TtsVoicePreset, _settings.Current, _settings.ProfileDirectory));
        Assert.True(SettingsMenu.IsTtsField(SettingsField.TtsVoicePreset));
        Assert.True(SettingsMenu.RefusedMidTurn(SettingsField.TtsVoicePreset));
    }

    [Fact]
    public void VoicePreset_ThePickerRows_LineUpInColumns()
    {
        var rows = SettingsMenu.PresetRows([
            new VoicePreset("amanda", "af_bella", "af_nicole", 60, 1.2),
            new VoicePreset("jo", "bm_george", "", 100, 1.25),
            new VoicePreset("hunter", "am_adam", "am_michael", 100, 1.3),
        ]);

        Assert.Equal(
        [
            "amanda  af_bella   + af_nicole   60 % / 40 %  1.2",
            "jo      bm_george                             1.25",
            "hunter  am_adam    + am_michael  100 % / 0 %  1.3",
        ], rows);
    }

    [Fact]
    public async Task VoicePreset_IsRow11_APicker_WritingAllFour_AndATtsChange()
    {
        Down(10);
        Push(Keys.Enter);                        // TTS voice preset: the picker opens on neon, the second row
        Push(Keys.Down, Keys.Down, Keys.Enter);  // richard, hunter
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        var saved = _settings.Current;
        Assert.Equal(("am_adam", "am_michael", 70, 1.3), (saved.TtsVoice, saved.TtsVoice2, saved.TtsVoiceMix, saved.TtsSpeed));
        Assert.Contains("TTS voice preset: hunter · am_adam + am_michael · 70 % / 30 % · 1.3", _console.Output);
        Assert.Equal("hunter", SettingsMenu.FieldValue(SettingsField.TtsVoicePreset, saved, _settings.ProfileDirectory));
    }

    [Fact]
    public async Task VoicePreset_EscapeKeepsTheFour_AndAnOverriddenOneIsWarnedAbout()
    {
        _settings.Update(d => d.TtsSpeed = 1.5);   // custom: the picker opens on the first row
        Down(10);
        Push(Keys.Enter, Keys.Escape, Keys.Escape);
        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(1.5, _settings.Current.TtsSpeed);
        Assert.Equal("af_heart", _settings.Current.TtsVoice);

        _overrides[SettingsField.TtsSpeed] = "NEONSIDEKICK_TTS_SPEED";
        Down(10);
        Push(Keys.Enter, Keys.Enter, Keys.Escape);   // amanda
        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(("af_bella", "af_nicole", 60, 1.2), (_settings.Current.TtsVoice, _settings.Current.TtsVoice2, _settings.Current.TtsVoiceMix, _settings.Current.TtsSpeed));
        Assert.Contains(SettingsMenu.OverrideNotice("NEONSIDEKICK_TTS_SPEED"), _console.Output);
    }

    [Fact]
    public async Task OnThePane_ThePresetPicker_SpeaksTheHighlightedBlend_AtItsSpeed()
    {
        await ConnectSpeechAsync();
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // TTS voice preset, the TTS tab's fifth row, opening on neon
        Push(Keys.Down, Keys.Enter, Keys.Escape);                                    // richard

        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));
        await WaitForTheTailAsync();

        Assert.Equal("bm_george", _settings.Current.TtsVoice);
        Assert.Equal(Preview(VoiceMix.Spec("bm_george", "am_fenrir", 50), 1.2), _synth.Spoken);
        Assert.Contains("\n▸ TTS voice preset   richard\n  TTS voice          bm_george\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheVoice2Picker_SpeaksAVoiceAlone_AndTheNoneRowIsSilent()
    {
        await ConnectSpeechAsync();
        _settings.Update(d => d.TtsVoice2 = "");   // no second voice saved, so the picker opens on (none)
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // TTS voice 2, opening on (none)
        Push(Keys.Down, Keys.Enter, Keys.Escape);                                    // af_heart

        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));
        await WaitForTheTailAsync();

        Assert.Equal("af_heart", _settings.Current.TtsVoice2);
        Assert.Equal(Preview("af_heart", 1.2), _synth.Spoken);

        // Back onto (none): nothing is spoken for it, and the earlier preview is silenced.
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // opening on af_heart now
        Push(Keys.Down, Keys.Up, Keys.Escape);                                       // af_bella, then (none)
        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));
        await WaitForTheTailAsync();
        Assert.Equal("af_heart", _settings.Current.TtsVoice2);
        Assert.DoesNotContain(_synth.Spoken, s => s.Voice == SettingsMenu.NoSecondaryVoice);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheVoice2Picker_BackspaceJumpsToNone_EnterClearsIt_ThePrimaryPickerIgnoresIt()
    {
        _settings.Update(d => { d.TtsVoice = "af_heart"; d.TtsVoice2 = "af_bella"; });
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // TTS voice 2, opening on af_bella
        Push(Keys.Backspace, Keys.Enter, Keys.Escape);                               // (none)

        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("", _settings.Current.TtsVoice2);
        Assert.Contains("\n▸ (none)\n  af_heart\n", _console.Output);
        Assert.Contains("\n" + SettingsMenu.NoneKeys + "\n", _console.Output);
        Assert.Contains("TTS voice 2: (none)", _console.Output);

        // The primary picker has no (none) row: Backspace is swallowed, Enter saves the row the cursor is on.
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);              // TTS voice, opening on af_heart
        Push(Keys.Backspace, Keys.Enter, Keys.Escape);
        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));
        Assert.Equal("af_heart", _settings.Current.TtsVoice);
        Assert.Contains("TTS voice: af_heart", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ThePreviewIsSilent_WithTheToggleOff_OrSpeechOutputOff_AndThePickerStillWorks()
    {
        await ConnectSpeechAsync();
        _settings.Update(d => d.TtsVoicePreview = false);
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // TTS voice

        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("af_bella", _settings.Current.TtsVoice);
        Assert.Empty(_synth.Spoken);

        // The switch back on but TTS output off (as saved; the session still thinks it is on until the reconnect).
        _settings.Update(d => { d.TtsVoicePreview = true; d.TtsOutput = false; });
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // TTS voice
        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));
        Assert.Equal("bm_george", _settings.Current.TtsVoice);
        Assert.Empty(_synth.Spoken);
        Assert.Equal(3, _synth.ListCalls);   // the connect and the two pickers
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheVoicePreviewRow_IsUnderTheHttpUrl_AToggleWithNoReconnect()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the TTS tab's fourth row (2026-09-16); its page, off picked

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.TtsVoicePreview);
        Assert.Contains("\n" + Titled(Strip) + "\n  · TTS voice preview: off\n", _console.Output);
        Assert.Contains("\n  TTS HTTP URL       http://localhost:8880/v1\n▸ TTS voice preview  off\n  TTS voice preset   neon\n  TTS voice          af_heart\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task Voice_TheFlatListPicker_HasNoPreview()
    {
        await ConnectSpeechAsync();
        Down(11);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("af_bella", _settings.Current.TtsVoice);
        Assert.Empty(_synth.Spoken);
    }

    [Fact]
    public async Task OnThePane_TheMixRow_SpeaksTheBlendItSaved()
    {
        await ConnectSpeechAsync();
        _settings.Update(d => { d.TtsVoice2 = "af_sky"; d.TtsSpeed = 1.5; });   // the speed as the row says it, not the session's 1.2
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // TTS voice mix, "50" in the slot
        Backspace(2);
        _console.Input.PushText("70");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));
        await WaitForTheTailAsync();

        Assert.Equal(70, _settings.Current.TtsVoiceMix);
        Assert.Equal(Preview("af_heart(70)+af_sky(30)", 1.5), _synth.Spoken);
        Assert.Contains("\n" + Titled(Strip) + "\n  · TTS voice mix: 70 % / 30 %\n", _console.Output);

        // No second voice: the blend is the primary alone, and that is what plays.
        _settings.Update(d => d.TtsVoice2 = "");
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // TTS voice mix
        Backspace(2);
        _console.Input.PushText("30");
        Push(Keys.Enter, Keys.Escape);
        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));
        await WaitForTheTailAsync();
        Assert.Equal(Preview("af_heart", 1.5), _synth.Spoken.Skip(2));
        Assert.Equal(4, _synth.Spoken.Count);   // two previews, two sentences each
        Assert.Equal(1, _synth.ListCalls);   // the connect; a typed number consults no server
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheMixRow_IsSilent_WhenRefused_OrTheToggleIsOff()
    {
        await ConnectSpeechAsync();
        _settings.Update(d => d.TtsVoice2 = "af_sky");
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // TTS voice mix
        Backspace(2);
        _console.Input.PushText("101");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(80, _settings.Current.TtsVoiceMix);
        Assert.Contains("\n" + Titled(Strip) + "\n  ✗ TTS voice mix " + SettingsMenu.TtsVoiceMixRangeError + "; keeping 80.\n", _console.Output);
        Assert.Empty(_synth.Spoken);
        Assert.Null(_speech.Playing);

        _settings.Update(d => d.TtsVoicePreview = false);
        GoTo(SettingsTab.Tts); Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // TTS voice mix
        Backspace(2);
        _console.Input.PushText("70");
        Push(Keys.Enter, Keys.Escape);
        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));
        Assert.Equal(70, _settings.Current.TtsVoiceMix);
        Assert.Empty(_synth.Spoken);
        Assert.Null(_speech.Playing);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheSpeedRow_SpeaksTheBlend_AtTheSpeedItSaved()
    {
        await ConnectSpeechAsync();
        _settings.Update(d => { d.TtsVoice2 = "af_sky"; d.TtsVoiceMix = 70; });
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.End, Keys.Enter);   // TTS speed (the tab's last row since 2026-09-16), "1.2" in the slot
        Backspace(3);
        _console.Input.PushText("1.4");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));
        await WaitForTheTailAsync();

        Assert.Equal(1.4, _settings.Current.TtsSpeed);
        Assert.Equal(Preview("af_heart(70)+af_sky(30)", 1.4), _synth.Spoken);
        Assert.Contains("\n" + Titled(Strip) + "\n  · TTS speed: 1.4\n", _console.Output);
        Assert.Equal(1, _synth.ListCalls);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheSpeedRow_IsSilent_WhenRefused_OrTheToggleIsOff()
    {
        await ConnectSpeechAsync();
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Tts); Push(Keys.End, Keys.Enter);   // TTS speed
        Backspace(3);
        _console.Input.PushText("2.5");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(1.2, _settings.Current.TtsSpeed);
        Assert.Contains("\n" + Titled(Strip) + "\n  ✗ TTS speed " + SettingsMenu.TtsSpeedRangeError + "; keeping 1.2.\n", _console.Output);
        Assert.Empty(_synth.Spoken);
        Assert.Null(_speech.Playing);

        _settings.Update(d => d.TtsVoicePreview = false);
        GoTo(SettingsTab.Tts); Push(Keys.End, Keys.Enter);   // TTS speed
        Backspace(3);
        _console.Input.PushText("1.4");
        Push(Keys.Enter, Keys.Escape);
        Assert.Equal(SettingsChanges.Tts, await menu.ShowAsync(CancellationToken.None));
        Assert.Equal(1.4, _settings.Current.TtsSpeed);
        Assert.Empty(_synth.Spoken);
        Assert.Null(_speech.Playing);
        pane.Dispose();
    }

    [Fact]
    public async Task TtsSpeed_TheFlatList_SpeaksTheBlendToo()
    {
        await ConnectSpeechAsync();
        Down(14);
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("0.5");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));
        await WaitForTheTailAsync();

        Assert.Equal(0.5, _settings.Current.TtsSpeed);
        Assert.Equal(Preview("af_heart(80)+am_eric(20)", 0.5), _synth.Spoken);   // the fresh profile's blend
    }

    [Fact]
    public async Task VoiceMix_TheFlatList_SpeaksTheBlendToo()
    {
        // A typed edit has no cursor hook to miss: the flat list previews the mix like the pane.
        await ConnectSpeechAsync();
        _settings.Update(d => d.TtsVoice2 = "af_sky");
        Down(13);
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("0");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.Tts, await _menu.ShowAsync(CancellationToken.None));
        await WaitForTheTailAsync();

        Assert.Equal(0, _settings.Current.TtsVoiceMix);
        Assert.Equal(Preview("af_sky", 1.2), _synth.Spoken);   // 0 = the second voice alone
    }

    [Fact]
    public async Task Toggle_TtsVoicePreview_IsRow47_NotATtsChange()
    {
        Down(46);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.TtsVoicePreview);
        Assert.Contains("  · TTS voice preview: off", _console.Output);
    }

    // ── File tools / Web search method (2026-09-15) ─────────────────────

    [Fact]
    public async Task Toggle_FileTools_IsRow48_NoReconnect()
    {
        Down(47);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.FileTools);
        Assert.Contains("  · File tools: off", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task WebSearchMethod_IsRow49_APicker_NoReconnect()
    {
        Down(48);
        Push(Keys.Enter);                           // the picker opens on duckduckgo (the first row)
        Push(Keys.Down, Keys.Enter);                // searxng
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("searxng", _settings.Current.WebSearchMethod);
        Assert.Contains(Breadcrumb("Web search method"), _console.Output);
        Assert.Contains("  · Web search method: searxng", _console.Output);
        Assert.Contains("the built-in DuckDuckGo scrape, no setup", _console.Output);
        Assert.Contains("the instance named in Web SearXNG URL; DuckDuckGo until one is set", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    // ── The Ask tab (2026-09-15) ───────────────────────────────────────────

    [Fact]
    public async Task Toggle_AskUser_IsRow50_NoReconnect()
    {
        Down(49);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.AskUser);
        Assert.Contains("  · Ask user: off", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task AskMaxQuestions_IsRow51_Typed_NoReconnect_AndOutOfRangeIsRefused()
    {
        Down(50);
        Push(Keys.Enter);                           // Ask max questions shows "10"
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("0");
        Push(Keys.Enter);                           // refused
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("11");
        Push(Keys.Enter);                           // refused again
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("3");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(3, _settings.Current.AskMaxQuestions);
        Assert.Contains("Ask max questions " + SettingsMenu.AskMaxQuestionsRangeError + "; keeping 10.", _console.Output);
        Assert.Contains("  · Ask max questions: 3 questions", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task AskMaxChoices_IsRow52_Typed_NoReconnect_AndOutOfRangeIsRefused()
    {
        Down(51);
        Push(Keys.Enter);                           // Ask max choices per question shows "10"
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("1");
        Push(Keys.Enter);                           // refused: a choice needs two
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("16");
        Push(Keys.Enter);                           // refused again
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("2");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(2, _settings.Current.AskMaxChoices);
        Assert.Contains("Ask max choices per question " + SettingsMenu.AskMaxChoicesRangeError + "; keeping 10.", _console.Output);
        Assert.Contains("  · Ask max choices per question: 2 choices", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    // ── The Skills tab (2026-09-16) ─────────────────────────────────────────

    [Fact]
    public async Task Toggle_AgentSkills_IsRow56_NoReconnect()
    {
        Down(55);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.AgentSkills);
        Assert.Contains("  · Agent skills: off", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task Toggle_ExternalSkills_IsRow57_OffByDefault_NoReconnect()
    {
        Down(56);
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.True(_settings.Current.ExternalSkills);
        Assert.Contains(@"  · Use external skills (.agents\skills): on", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task SkillCompactMode_IsAPicker_TheLastRow()
    {
        Down(57);
        Push(Keys.Enter);                           // Skill compact mode: the picker opens on the default (the first row)
        Push(Keys.Down, Keys.Enter);                // unprotected
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("unprotected", _settings.Current.SkillCompactMode);
        Assert.Contains(Breadcrumb("Skill compact mode"), _console.Output);
        Assert.Contains("  · Skill compact mode: unprotected", _console.Output);
        Assert.Contains("loaded skills prune like any tool result", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task Toggle_HashMention_IsRow62_TheLast_OnByDefault_NoReconnect()
    {
        Down(61);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.SkillHashMention);
        Assert.Contains("  · #-mention enabled: off", _console.Output);
        Assert.Contains("# is ordinary text", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task Toggle_DollarMention_IsRow80_TheLast_OnByDefault_NoReconnect()
    {
        // The $-mention switch (2026-09-19): the enum's last member, /tools' Options tab on the pane.
        Down(79);   // one row up since Allow skill delete went, 2026-09-23
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.ToolsDollarMention);
        Assert.Contains("  · $-mention enabled: off", _console.Output);
        Assert.Contains("$ is ordinary text", _console.Output);
        Assert.True(SettingsMenu.IsToggle(SettingsField.ToolsDollarMention));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ToolsDollarMention));
        Assert.Equal("$-mention enabled", SettingsMenu.FieldName(SettingsField.ToolsDollarMention));
        Assert.Equal("$ and part of a name lists the offered tools on the line", SettingsMenu.ToggleDescribe(SettingsField.ToolsDollarMention, true));
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task DraftEditor_IsRow84_TheLast_Typed_AnyTextIsKept_AndEmptyMeansTheShellsDefault()
    {
        // 2026-09-19: the enum's last member, the General tab's last row on the pane; a command line, not a path, so nothing is checked here.
        _console.Profile.Width = 240;
        Down(83);   // one row up since Allow skill delete went, 2026-09-23
        Push(Keys.Enter);                           // Draft editor: empty
        _console.Input.PushText("code --wait");
        Push(Keys.Enter);                           // saved
        Push(Keys.Enter);
        Backspace(11);
        Push(Keys.Enter, Keys.Escape);              // cleared

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("", _settings.Current.DraftEditor);
        Assert.Contains("  · Draft editor: code --wait", _console.Output);
        Assert.Contains("  · Draft editor: " + SettingsMenu.DefaultDraftEditorLabel, _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task ReflectionMaxRequests_IsRow67_SavedRangeChecked_NoReconnect()
    {
        // Flat row 65 since Mouse in menus went on 2026-09-21 (66 since the stale line number guard went with edit_lines later still on 2026-09-19 (67 since Reflection verbose went earlier that day, 68 before); the enum's last member from 2026-09-17 until the two General switches of 2026-09-18: the Reflection tab's fifth row.
        _console.Profile.Width = 240;
        Down(66);
        Push(Keys.Enter);                       // Reflection max requests "4"
        Push(Keys.Backspace);
        _console.Input.PushText("7");
        Push(Keys.Enter);
        Push(Keys.Enter);                       // the cursor stays on the row just edited: "7"
        Push(Keys.Backspace);
        _console.Input.PushText("21");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal(7, _settings.Current.ReflectionMaxRequests);
        Assert.Contains("  · Reflection max requests: 7 requests", _console.Output);
        Assert.Contains(SettingsMenu.ReflectionMaxRequestsRangeError, _console.Output);
        Assert.Contains("keeping 7", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ReflectionMaxRequests));
    }

    // ── Transcript markdown (2026-09-16) ────────────────────────────────────

    [Fact]
    public async Task Toggle_TranscriptMarkdown_IsRow59_OnByDefault_NoReconnect()
    {
        Down(58);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.TranscriptMarkdown);
        Assert.Contains("  · Transcript markdown: off", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task Toggle_TranscriptMarkdown_MidTurn_IsAllowed()
    {
        Down(58);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker (2026-09-17): the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None, midTurn: true));
        Assert.False(_settings.Current.TranscriptMarkdown);
    }

    // ── Paste preview lines (2026-09-16) ────────────────────────────────────

    [Fact]
    public async Task PastePreviewLines_IsRow60_Typed_ZeroIsOff_NoReconnect_AndOutOfRangeIsRefused()
    {
        _console.Profile.Width = 240;
        Down(59);
        Push(Keys.Enter);                       // Paste preview lines shows "25"
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("-1");
        Push(Keys.Enter);                       // refused, the row stays open with "25"
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("201");
        Push(Keys.Enter);                       // refused again
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("0");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.Equal(0, _settings.Current.PastePreviewLines);
        Assert.Contains(SettingsMenu.PastePreviewLinesRangeError, _console.Output);
        Assert.Contains("keeping 25", _console.Output);
        Assert.Contains("  · Paste preview lines: off", _console.Output);
        Assert.Equal(0, _synth.ListCalls);          // no server is consulted
    }

    [Fact]
    public async Task PastePreviewLines_MidTurn_IsAllowed()
    {
        Down(59);
        Push(Keys.Enter);
        Push(Keys.Backspace, Keys.Backspace);
        _console.Input.PushText("40");
        Push(Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None, midTurn: true));
        Assert.Equal(40, _settings.Current.PastePreviewLines);
        Assert.Contains("  · Paste preview lines: 40 lines", _console.Output);
    }

    [Fact]
    public async Task OnThePane_TheLlmTab_IsThird_AfterSessions()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Llm);                                  // General → Sessions → LLM (2026-09-19; the Skills tab sat between from 2026-09-18 until then — /skills' Options tab now)
        Push(Keys.Left);                                        // Sessions
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ LLM scan mode                   local\n", _console.Output);
        Assert.Contains("\n▸ Session logging             on\n", _console.Output);
        Assert.DoesNotContain("Agent skills", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheSessionsTab_IsSecond_AfterGeneral()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Sessions);                             // one Right (2026-09-18; the wrap from General that morning)
        Push(Keys.Down, Keys.Enter);                            // Session retention (days), the second row: typed
        _console.Input.PushText("30");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter, Keys.Up, Keys.Enter);       // Session naming mode, the third row: the page opens on model-written (the default), first-line picked
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);   // Session show name, the fourth row: the page opens on all-names (the default), none picked
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("first-line", _settings.Current.SessionNamingMode);
        Assert.Equal("none", _settings.Current.SessionShowName);
        Assert.Equal(30, _settings.Current.SessionRetentionDays);
        // The seven rows padded to the tab's own column (28) in the user's order (retention second, the show-name picker under the naming mode, the tool above the search cap, save thinking last since 2026-09-28), then the picker's page, then the notices on the status line.
        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ Session logging             on\n  Session retention (days)    forever\n  Session naming mode         model-written\n  Session show name           all-names\n  Session tool                on\n  Session search max results  10 results\n  Session save thinking       off\n" + Rule(100), _console.Output);
        Assert.Contains("\n" + Titled(Breadcrumb("Session naming mode")) + "\n \n  first-line     the session is named after its first sent line\n▸ model-written  the model writes a short title after the first turn\n" + Rule(100), _console.Output);
        Assert.Contains("\n  · Session retention (days): 30 days\n", _console.Output);
        Assert.Contains("\n▸ Session retention (days)    30 days\n", _console.Output);
        Assert.Contains("\n  · Session naming mode: first-line\n", _console.Output);
        Assert.Contains("\n▸ Session naming mode         first-line\n", _console.Output);
        Assert.Contains("\n" + Titled(Breadcrumb("Session show name")) + "\n \n▸ all-names      every session name shows on the rule above the input row\n  model-written  only a model-written or typed name shows; the first line never does\n  none           the rule stays bare\n" + Rule(100), _console.Output);
        Assert.Contains("\n  · Session show name: none\n", _console.Output);
        Assert.Contains("\n▸ Session show name           none\n", _console.Output);
        Assert.False(SettingsMenu.IsToggle(SettingsField.SessionShowName));
        Assert.Equal(SettingsTab.Sessions, (SettingsTab)1);
        Assert.Equal([SettingsField.SessionLogging, SettingsField.SessionRetentionDays, SettingsField.SessionNamingMode, SettingsField.SessionShowName, SettingsField.SessionTool, SettingsField.SessionSearchMaxResults, SettingsField.SessionSaveThinking], SettingsMenu.TabFields[(int)SettingsTab.Sessions]);
        // Save thinking (2026-09-28): the Sessions tab's last row, a toggle off by default.
        Assert.True(SettingsMenu.IsToggle(SettingsField.SessionSaveThinking));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.SessionSaveThinking, new AppSettingsData(), ""));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.SessionSaveThinking, new AppSettingsData { SessionSaveThinking = true }, ""));
        Assert.True(SettingsMenu.IsToggle(SettingsField.SessionLogging));
        Assert.True(SettingsMenu.IsToggle(SettingsField.SessionTool));
        Assert.False(SettingsMenu.IsToggle(SettingsField.SessionNamingMode));
        Assert.Equal("must be 0 to 3650 days", SettingsMenu.SessionRetentionDaysRangeError);
        Assert.Equal("must be 1 to 20 results", SettingsMenu.SessionSearchMaxResultsRangeError);
        Assert.Equal("forever", SettingsMenu.Days(0));
        Assert.Equal("1 day", SettingsMenu.Days(1));
        Assert.Equal("every completed turn is written to this profile's session store", SettingsMenu.ToggleDescribe(SettingsField.SessionLogging, true));
        Assert.Equal("nothing is written; what is stored still lists, restores and purges", SettingsMenu.ToggleDescribe(SettingsField.SessionLogging, false));
        Assert.Equal("the model can search, list and read this profile's earlier sessions", SettingsMenu.ToggleDescribe(SettingsField.SessionTool, true));
        Assert.Equal("the model never sees an earlier session", SettingsMenu.ToggleDescribe(SettingsField.SessionTool, false));
        pane.Dispose();
    }

    // ── The on/off pickers (2026-09-17) ─────────────────────────────────────

    [Fact]
    public void ToggleDescribe_HasASentenceForEveryToggle_BothWays_AndNoneForTheRest()
    {
        foreach (var field in Enum.GetValues<SettingsField>())
        {
            if (SettingsMenu.IsToggle(field))
            {
                Assert.False(string.IsNullOrWhiteSpace(SettingsMenu.ToggleDescribe(field, true)), field.ToString());
                Assert.False(string.IsNullOrWhiteSpace(SettingsMenu.ToggleDescribe(field, false)), field.ToString());
                Assert.NotEqual(SettingsMenu.ToggleDescribe(field, true), SettingsMenu.ToggleDescribe(field, false));
                Assert.True(SettingsMenu.ToggleDescribe(field, true).Length <= 72, field.ToString());   // one pane row after "on  " at 80 columns
                Assert.True(SettingsMenu.ToggleDescribe(field, false).Length <= 72, field.ToString());
            }
            else
            {
                Assert.Equal("", SettingsMenu.ToggleDescribe(field, true));
                Assert.Equal("", SettingsMenu.ToggleDescribe(field, false));
            }
        }

        Assert.Equal("on  " + Theme.DimMarkup("replies are read aloud"), SettingsMenu.ToggleLabel(SettingsField.TtsOutput, true));
        Assert.Equal("off " + Theme.DimMarkup("replies are text only"), SettingsMenu.ToggleLabel(SettingsField.TtsOutput, false));
        Assert.Equal("memory enabled", SettingsMenu.ToggleDescribe(SettingsField.Memory, true));   // the user's words, 2026-09-21
        Assert.Equal("memory disabled", SettingsMenu.ToggleDescribe(SettingsField.Memory, false));
        Assert.Equal("no tools at all; a change starts a new conversation", SettingsMenu.ToggleDescribe(SettingsField.LlmOfferTools, false));
        Assert.Equal("edit and delete operations move copies to .trash first", SettingsMenu.ToggleDescribe(SettingsField.FileSafeEdits, true));
        Assert.Equal("edit and delete operations function normally", SettingsMenu.ToggleDescribe(SettingsField.FileSafeEdits, false));
        Assert.Equal("%USERPROFILE%\\.agents\\skills is read too", SettingsMenu.ToggleDescribe(SettingsField.ExternalSkills, true));
        // 2026-09-18: the user's own sentences for /copy, and the two new line switches.
        Assert.Equal("/copy copies user prompts and model replies", SettingsMenu.ToggleDescribe(SettingsField.CopyUserPrompt, true));
        Assert.Equal("/copy copies model replies only", SettingsMenu.ToggleDescribe(SettingsField.CopyUserPrompt, false));
        Assert.Equal("hide '/exit' from the autocomplete list", SettingsMenu.ToggleDescribe(SettingsField.HideExitAutocomplete, true));   // the user's words, 2026-09-21
        Assert.Equal("show '/exit' in the autocomplete list", SettingsMenu.ToggleDescribe(SettingsField.HideExitAutocomplete, false));
        Assert.Equal("a command typed without its slash or with extra ones offers the command", SettingsMenu.ToggleDescribe(SettingsField.CommandTypoIntercept, true));   // extra slashes since later still on 2026-09-24
        Assert.Equal("a command typed without its slash or with extra ones is sent as typed", SettingsMenu.ToggleDescribe(SettingsField.CommandTypoIntercept, false));
        Assert.Equal("show the working directory in the header", SettingsMenu.ToggleDescribe(SettingsField.ShowWorkingDirectory, true));   // the user's words, 2026-09-21
        Assert.Equal("hide the working directory in the header", SettingsMenu.ToggleDescribe(SettingsField.ShowWorkingDirectory, false));
        Assert.Equal("theme the picture viewer", SettingsMenu.ToggleDescribe(SettingsField.ThemedViewer, true));   // later on 2026-09-27
        Assert.Equal("keep the picture viewer black", SettingsMenu.ToggleDescribe(SettingsField.ThemedViewer, false));
        Assert.Equal("vault_delete may move a note or attachment to the vault's .trash", SettingsMenu.ToggleDescribe(SettingsField.ObsidianAllowDelete, true));   // later on 2026-09-22
        Assert.Equal("vault_delete is disabled", SettingsMenu.ToggleDescribe(SettingsField.ObsidianAllowDelete, false));
        Assert.Equal("a message sent while a reply runs is queued and sent when the reply ends", SettingsMenu.ToggleDescribe(SettingsField.QueueMessages, true));
        Assert.Equal("a message sent during a reply goes when it ends, unlisted; no /queue", SettingsMenu.ToggleDescribe(SettingsField.QueueMessages, false));
    }

    [Fact]
    public async Task Toggle_HideExitAutocomplete_IsRow68_OnByDefault_PersistsAndNeedsNoReconnect()
    {
        // 2026-09-18: the first of the two General switches appended after Reflection max requests; the General tab's row after Paste preview lines.
        Assert.True(_settings.Current.HideExitAutocomplete);
        Down(67);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the on/off picker: the saved value under the cursor, the other row picked

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.HideExitAutocomplete);
        Assert.Contains("  · Hide /exit autocomplete: off", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task Toggle_CommandTypoIntercept_IsRow69_OnByDefault_PersistsAndNeedsNoReconnect()
    {
        // 2026-09-18: the General tab's row before Welcome splash (the last of both until it landed, later that day).
        Assert.True(_settings.Current.CommandTypoIntercept);
        Down(68);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.CommandTypoIntercept);
        Assert.Contains("  · Command typo intercept: off", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task WelcomeSplash_IsRow70_APicker_FullsizeByDefault_PersistsAndNeedsNoReconnect()
    {
        // 2026-09-18: the General tab's row before Show working directory (the user's order); a picker since 2026-09-24.
        Assert.Equal("fullsize", _settings.Current.WelcomeSplashMode);
        Down(69);
        Push(Keys.Enter);                           // the picker opens on fullsize (the first row)
        Push(Keys.Down, Keys.Enter);                // tiled
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("tiled", _settings.Current.WelcomeSplashMode);
        Assert.Contains(Breadcrumb("Welcome splash"), _console.Output);
        Assert.Contains("  · Welcome splash: tiled", _console.Output);
        Assert.Contains("the banner alone at startup", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task Toggle_ShowWorkingDirectory_IsRow71_OffByDefault_PersistsAndNeedsNoReconnect()
    {
        // 2026-09-18: the General tab's last row (the enum's last member until the queue's two, later that day); the banner reads it at its next draw. Off by default since 2026-09-21, so the flip lands on on.
        Assert.False(_settings.Current.ShowWorkingDirectory);
        Down(70);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.True(_settings.Current.ShowWorkingDirectory);
        Assert.Contains("  · Working directory in header: on", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    /// <summary>
    /// Show toolbar is a checklist (2026-09-29, the user's ask; a switch at row 105 until then): every item checked at
    /// first, Enter or Space flipping one and saving at once in strip order, ESC back; the row reads how many are checked.
    /// </summary>
    [Fact]
    public async Task OnThePane_ShowToolbar_IsAChecklist_SavedInStripOrder_NeedingNoReconnect()
    {
        Assert.Null(_settings.Current.ToolbarItems);
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.General].ToList().IndexOf(SettingsField.ToolbarItems));
        Push(Keys.Enter);                       // the checklist, on Settings
        Push(Keys.Char(' '));                   // Settings off
        Down(10);
        Push(Keys.Enter);                       // the path off
        Push(Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(["tools", "mcp", "skills", "sys", "sessions", "usage", "memory", "cmdlist", "police"], _settings.Current.ToolbarItems);
        Assert.Contains("[x] ⚙️  Settings", _console.Output);
        Assert.Contains("[ ] ⚙️  Settings", _console.Output);
        Assert.Contains("[ ]     Working directory path", _console.Output);
        Assert.Contains("  · Show toolbar: 10 of 11", _console.Output);
        Assert.Contains("  · Show toolbar: 9 of 11", _console.Output);
        pane.Dispose();
    }

    /// <summary>Every item flipped off (2026-09-29): an empty list — no toolbar — and nothing refused.</summary>
    [Fact]
    public async Task OnThePane_ShowToolbar_AllOff_IsAnEmptyList()
    {
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.General].ToList().IndexOf(SettingsField.ToolbarItems));
        Push(Keys.Enter);
        for (int i = 0; i < ToolbarItems.Names.Length; i++)
        {
            Push(Keys.Enter, Keys.Down);
        }

        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([], _settings.Current.ToolbarItems);
        Assert.Contains("  · Show toolbar: off", _console.Output);
        pane.Dispose();
    }

    /// <summary>The last unchecked item checked again (2026-09-29): every item saves as null, so one added later joins.</summary>
    [Fact]
    public async Task OnThePane_ShowToolbar_AllOn_SavesNull()
    {
        _settings.Update(d => d.ToolbarItems = [.. ToolbarItems.Names.Where(n => n != "police")]);
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.General].ToList().IndexOf(SettingsField.ToolbarItems));
        Push(Keys.Enter);
        Down(9);
        Push(Keys.Char(' '));                   // the officer back: every item
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.ToolbarItems);
        Assert.Contains("  · Show toolbar: all", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task Toggle_QueueMessages_IsRow72_OnByDefault_PersistsAndNeedsNoReconnect()
    {
        // 2026-09-18: the General tab's row under Working directory (the user's place), the enum's second-to-last member; the line hook reads it at each mid-turn Enter.
        Assert.True(_settings.Current.QueueMessages);
        Down(71);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.QueueMessages);
        Assert.Contains("  · Queue messages: off", _console.Output);
        Assert.Contains("a message sent while a reply runs is queued and sent when the reply ends", _console.Output);
        Assert.Contains("a message sent during a reply goes when it ends, unlisted; no /queue", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public async Task QueueCancelMode_IsRow73_TheLast_APicker_EmptyByDefault_PersistsAndNeedsNoReconnect()
    {
        // 2026-09-18: the enum's last member, the General tab's row under Queue messages; read when a turn ends. Empty by default since 2026-09-20 (hold before).
        Assert.Equal("empty", _settings.Current.QueueCancelMode);
        Down(72);
        Push(Keys.Enter);                           // the picker opens on empty (the last row)
        Push(Keys.Up, Keys.Up, Keys.Enter);         // hold
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.Equal("hold", _settings.Current.QueueCancelMode);
        Assert.Contains(Breadcrumb("Queue cancel mode"), _console.Output);
        Assert.Contains("  · Queue cancel mode: hold", _console.Output);
        Assert.Contains("a cancelled reply holds the queue; your next message runs first, then it resumes", _console.Output);
        Assert.Contains("a cancelled reply sends the next queued message at once", _console.Output);
        Assert.Contains("a cancelled reply drops every queued message", _console.Output);
        Assert.Equal(0, _synth.ListCalls);
    }

    [Fact]
    public void IsOn_ReadsEveryToggle_AndAgreesWithFieldValue()
    {
        var data = new AppSettingsData();
        foreach (var field in Enum.GetValues<SettingsField>())
        {
            if (SettingsMenu.IsToggle(field))
            {
                Assert.Equal(SettingsMenu.IsOn(field, data) ? "on" : "off", SettingsMenu.FieldValue(field, data, _settings.ProfileDirectory, null));
            }
            else
            {
                Assert.False(SettingsMenu.IsOn(field, data));
            }
        }

        Assert.True(SettingsMenu.IsOn(SettingsField.Memory, data));
        Assert.False(SettingsMenu.IsOn(SettingsField.LlmUseFunVerbs, data));
        Assert.True(SettingsMenu.IsOn(SettingsField.LlmShowThinking, data));
    }

    [Fact]
    public async Task Toggle_TheSavedValuePickedAgain_IsUnchanged_AndEscapeKeeps()
    {
        Assert.True(_settings.Current.Memory);
        Down(1);
        Push(Keys.Enter, Keys.Enter);           // Memory: the page opens on "on", on picked again
        Push(Keys.Enter, Keys.Escape);          // the page again, ESC
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));

        Assert.True(_settings.Current.Memory);
        Assert.Equal(2, _console.Output.Split(SettingsMenu.UnchangedNotice).Length - 1);
        Assert.DoesNotContain("Memory: on", _console.Output);
        Assert.Contains(SettingsMenu.PromptTitle(Breadcrumb("Memory"), SettingsMenu.PickKeys), _console.Output);
        Assert.Contains(SettingsMenu.ToggleDescribe(SettingsField.Memory, false), _console.Output);
    }

    [Fact]
    public async Task Toggle_LlmTools_TheSavedValuePickedAgain_ClearsNothing()
    {
        Down(31);
        Push(Keys.Enter, Keys.Enter, Keys.Escape);   // LLM offer tools: on picked again

        Assert.Equal(SettingsChanges.None, await _menu.ShowAsync(CancellationToken.None));
        Assert.True(_settings.Current.LlmOfferTools);
    }

    [Fact]
    public async Task OnThePane_TheSttInputRow_IsAPicker_AVoiceChange()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Stt);   // the STT tab, its first row
        Push(Keys.Enter, Keys.Up, Keys.Enter, Keys.Escape);   // off under the cursor, on picked

        Assert.Equal(SettingsChanges.Voice, await menu.ShowAsync(CancellationToken.None));

        Assert.True(_settings.Current.SttInput);
        Assert.Contains("\n" + Titled(Breadcrumb("STT input")) + "\n \n  on  " + SettingsMenu.ToggleDescribe(SettingsField.SttInput, true) + "\n▸ off " + SettingsMenu.ToggleDescribe(SettingsField.SttInput, false) + "\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · STT input: on\n▸ STT input                 on\n", _console.Output);
        pane.Dispose();
    }
}
