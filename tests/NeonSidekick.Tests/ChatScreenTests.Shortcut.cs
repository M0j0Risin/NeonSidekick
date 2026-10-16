using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Shortcuts;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/shortcut</c> on the screen (2026-10-07): the four forms, a replaced one, the refusals, the error off Windows; and the
/// offer of it (2026-10-09), each profile's own since 2026-10-16: at start and after a switch to a profile not yet asked.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The writer the chat screen gets; null (the default) is a platform without one.</summary>
    private FakeShortcutWriter? _shortcutWriter;

    private string ShortcutExe => Path.Combine(_dir, "app", "NeonSidekick.exe");

    /// <summary>
    /// A writer for the screen, the loaded profile's offer already made (unless <paramref name="offered"/> is false) so a script's keys
    /// reach the line. Only the loaded profile is marked: a test that switches must mark its target too, or answer its offer.
    /// </summary>
    private FakeShortcutWriter UseShortcutWriter(bool offered = true, ShortcutKind kind = ShortcutKind.Lnk)
    {
        if (offered)
        {
            _settings.MarkShortcutOfferedAsync().GetAwaiter().GetResult();
        }

        return _shortcutWriter = new FakeShortcutWriter(Path.Combine(_dir, "Desktop"), ShortcutExe, kind);
    }

    private static string OfferWords => ShortcutText.OfferQuestion(Profiles.DefaultName)[NoticeGlyphs.Shortcut.Length..];

    [Fact]
    public async Task Shortcut_TheFourForms_TheLoadedProfileOrTheNamedOne_WithOrWithoutTheLog()
    {
        var writer = UseShortcutWriter();
        Profiles.Create(_dir, "Jason", new AppSettingsData());
        PushLine("/shortcut");
        PushLine("/shortcut --log");
        PushLine("/shortcut jason");
        PushLine("/shortcut jason --log");
        PushLine("/exit");

        string output = await RunAsync();

        string exeFolder = Path.GetDirectoryName(ShortcutExe)!;
        string Log(string profile) => " --log " + DesktopShortcut.Quote(Path.Combine(exeFolder, "logs", "neon-" + profile + "-{ts}.log"));
        string log = Log("neon");
        Assert.Equal(
            ["--profile neon", "--profile neon" + log, "--profile Jason", "--profile Jason" + Log("Jason")],
            writer.Written.Select(s => s.Arguments));
        Assert.All(writer.Written, s => Assert.Equal(ShortcutExe, s.Target));
        Assert.All(writer.Written, s => Assert.Equal(exeFolder, s.WorkingDirectory));
        Assert.Equal(Path.Combine(writer.DesktopFolder, "NeonSidekick (Jason).lnk"), writer.Written[2].LinkPath);   // as the profile list spells it
        Assert.Contains("  · " + ShortcutText.Made(writer.Written[0].LinkPath, "--profile neon", replaced: false), output);
        Assert.Contains("  · " + ShortcutText.Made(writer.Written[1].LinkPath, "--profile neon" + log, replaced: true), output);   // the same name: replaced
        Assert.Contains("  · " + ShortcutText.Made(writer.Written[2].LinkPath, "--profile Jason", replaced: false), output);
        Assert.Equal(2, Directory.GetFiles(writer.DesktopFolder).Length);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>A Mac's writer (2026-10-08): the same forms as a .command file, its log quoted for zsh.</summary>
    [Fact]
    public async Task Shortcut_AMacsWriter_MakesACommandFile_ItsLogQuotedForZsh()
    {
        var writer = UseShortcutWriter(kind: ShortcutKind.Command);
        PushLine("/shortcut --log");
        PushLine("/exit");

        string output = await RunAsync();

        string exeFolder = Path.GetDirectoryName(ShortcutExe)!;
        var spec = Assert.Single(writer.Written);
        Assert.Equal(Path.Combine(writer.DesktopFolder, "NeonSidekick (neon).command"), spec.LinkPath);
        Assert.Equal("--profile neon --log " + DesktopShortcut.ShellQuote(Path.Combine(exeFolder, "logs", "neon-neon-{ts}.log")), spec.Arguments);
        Assert.Contains("  · " + ShortcutText.Made(spec.LinkPath, spec.Arguments, replaced: false), output.Replace("\n", "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shortcut_Refusals_AreOneLineEach_AndWriteNothing()
    {
        var writer = UseShortcutWriter();
        PushLine("/shortcut ghost");
        PushLine("/shortcut a b");
        PushLine("/shortcut --verbose");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ChatScreen.ProfileMissingError("ghost"), output);
        Assert.Equal(2, output.Split("  ✗ " + ShortcutText.Usage).Length - 1);
        Assert.Empty(writer.Written);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Shortcut_AWriterThatThrows_OrNoExe_PrintsTheError_AndTheScreenGoesOn()
    {
        var writer = UseShortcutWriter();
        writer.Failure = new IOException("IPersistFile.Save failed (0x80070005)");
        _chat.EnqueueText("Hello.");
        PushLine("/shortcut");
        PushLine("hi");
        PushLine("/exit");

        string output = await RunAsync();

        string link = Path.Combine(writer.DesktopFolder, DesktopShortcut.LinkName(Profiles.DefaultName));
        Assert.Contains(ShortcutText.WriteFailed(link, "IPersistFile.Save failed (0x80070005)"), output.Replace("\n", "", StringComparison.Ordinal));
        Assert.Contains("Hello.", output);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task Shortcut_NoOwnExecutable_IsAnError()
    {
        var writer = UseShortcutWriter();
        writer.Executable = null;
        PushLine("/shortcut");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ShortcutText.NoOwnExecutable, output);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task Shortcut_WithoutAWriter_IsNotHere()
    {
        PushLine("/shortcut");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ShortcutText.NotHere, output);
    }

    /// <summary>The first start's offer (2026-10-09): Yes is <c>/shortcut</c>, and the offer is marked made.</summary>
    [Fact]
    public async Task Offer_AFreshHome_Yes_WritesTheShortcut_AndMarksItOffered()
    {
        var writer = UseShortcutWriter(offered: false);
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(OfferWords, output);
        var spec = Assert.Single(writer.Written);
        Assert.Equal("--profile neon", spec.Arguments);
        Assert.Contains(ShortcutText.Made(spec.LinkPath, spec.Arguments, replaced: false), output.Replace("\n", "", StringComparison.Ordinal));
        Assert.True(_settings.ShortcutOffered);
        Assert.True(Profiles.ReadProfileFile(Profiles.ProfileFile(_dir, Profiles.DefaultName)).ShortcutOffered);   // written at once, in the profile
        using var relaunched = new AppSettings(_dir);
        Assert.True(relaunched.ShortcutOffered);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>A switch to a profile not yet asked (2026-10-16): its own offer, a yes writing its shortcut and marking it alone.</summary>
    [Fact]
    public async Task Offer_ASwitchToAnUnaskedProfile_OffersItsShortcut_AndMarksItAlone()
    {
        var writer = UseShortcutWriter();
        Profiles.Create(_dir, "work", new AppSettingsData());
        Profiles.Create(_dir, "home", new AppSettingsData());
        PushLine("/profile work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ShortcutText.OfferQuestion("work")[NoticeGlyphs.Shortcut.Length..], output);
        var spec = Assert.Single(writer.Written);
        Assert.Equal("--profile work", spec.Arguments);
        Assert.Equal(Path.Combine(writer.DesktopFolder, "NeonSidekick (work).lnk"), spec.LinkPath);
        Assert.Equal("work", _settings.ProfileName);
        Assert.True(_settings.ShortcutOffered);
        Assert.True(Profiles.ReadProfileFile(Profiles.ProfileFile(_dir, "work")).ShortcutOffered);
        Assert.Null(Profiles.ReadProfileFile(Profiles.ProfileFile(_dir, "home")).ShortcutOffered);
    }

    /// <summary>A switch to a profile already asked: no pane, the script's next line reaches the input row.</summary>
    [Fact]
    public async Task Offer_ASwitchToAnAskedProfile_IsNotAsked()
    {
        var writer = UseShortcutWriter();
        Profiles.Create(_dir, "work", new AppSettingsData { ShortcutOffered = true });
        PushLine("/profile work");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal("work", _settings.ProfileName);
        Assert.DoesNotContain(ShortcutText.OfferQuestion("work")[NoticeGlyphs.Shortcut.Length..], output);
        Assert.Empty(writer.Written);
    }

    /// <summary><c>/profile add</c> with a writer: the new profile, its flag blank, is offered its shortcut as the switch lands.</summary>
    [Fact]
    public async Task Offer_ProfileAdd_OffersTheNewProfilesShortcut()
    {
        var writer = UseShortcutWriter();
        PushLine("/profile add work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ShortcutText.OfferQuestion("work")[NoticeGlyphs.Shortcut.Length..], output);
        var spec = Assert.Single(writer.Written);
        Assert.Equal("--profile work", spec.Arguments);
        Assert.True(_settings.ShortcutOffered);
    }

    /// <summary>The log row: <c>/shortcut --log</c>. (The fixture's menus are the prompt, whose rows are reached by the arrows.)</summary>
    [Fact]
    public async Task Offer_TheLogRow_WritesTheShortcutWithTheLog()
    {
        var writer = UseShortcutWriter(offered: false);
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Enter);
        PushLine("/exit");

        await RunAsync();

        var spec = Assert.Single(writer.Written);
        string log = Path.Combine(Path.GetDirectoryName(ShortcutExe)!, "logs", "neon-neon-{ts}.log");
        Assert.Equal("--profile neon --log " + DesktopShortcut.Quote(log), spec.Arguments);
        Assert.True(_settings.ShortcutOffered);
    }

    /// <summary>ESC is No (the user's pick): nothing written, the line that says how to make one later, and never asked again.</summary>
    [Fact]
    public async Task Offer_Escape_WritesNothing_SaysSo_AndMarksItOffered()
    {
        var writer = UseShortcutWriter(offered: false);
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(OfferWords, output);
        Assert.Empty(writer.Written);
        Assert.Contains(ShortcutText.OfferDeclined, output.Replace("\n", "", StringComparison.Ordinal));
        Assert.True(_settings.ShortcutOffered);
    }

    /// <summary>Made already: no pane, and the script's first line reaches the input row.</summary>
    [Fact]
    public async Task Offer_AlreadyMade_IsNotAsked()
    {
        var writer = UseShortcutWriter();
        PushLine("/shortcut");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(OfferWords, output);
        Assert.Single(writer.Written);
    }

    /// <summary>The loaded profile's shortcut already on the desktop: not asked, and marked made.</summary>
    [Fact]
    public async Task Offer_TheShortcutAlreadyThere_IsNotAsked_ButMarked()
    {
        var writer = UseShortcutWriter(offered: false);
        Directory.CreateDirectory(writer.DesktopFolder);
        File.WriteAllText(Path.Combine(writer.DesktopFolder, DesktopShortcut.LinkName(Profiles.DefaultName)), "");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(OfferWords, output);
        Assert.Empty(writer.Written);
        Assert.True(_settings.ShortcutOffered);
    }

    /// <summary>Nothing to make a shortcut to (no writer, or no exe path): not asked, and left unmarked for a later start.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Offer_NoWriterOrNoExe_IsNotAsked_NorMarked(bool writerWithoutExe)
    {
        if (writerWithoutExe)
        {
            UseShortcutWriter(offered: false).Executable = null;
        }

        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(OfferWords, output);
        Assert.False(_settings.ShortcutOffered);
    }
}
