using System.Net;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests;

/// <summary>The screen's door over a real <c>.update/</c> in a temp folder (2026-11-04, self-update). Its stage starts a real probe, so <see cref="UpdateStage"/>'s own tests hold that part.</summary>
public sealed class AppUpdaterTests : IDisposable
{
    private const string Latest = "https://api.github.com/repos/M0j0Risin/NeonSidekick/releases/latest";

    private readonly UpdateFixture _f = new();

    public void Dispose() => _f.Dispose();

    private AppUpdater Updater(string? startLine = null, string running = "0.5.6") => new(_f.Context(running), startLine, _f.Client);

    private async Task Ready()
    {
        var result = await _f.Stage().StageAsync(_f.Usual(), null, "the notes", CancellationToken.None);
        Assert.True(result.Ok, result.Reason);
    }

    private void Swapped() => new UpdateExit(_f.Context(), null, null).Run(_ => { });

    [Fact]
    public void AFolderWithNoManifest_IsNoPackage_AndItsCheckAsksNothing()
    {
        var updater = Updater("a line");
        Assert.False(updater.IsPackage);
        Assert.Equal("a line", updater.StartLine);
        Assert.Equal("0.5.6", updater.Running);
        Assert.Equal(new UpdateDiskState(null, null), updater.ReadDisk());
        Assert.Null(updater.RunningNotes());
    }

    [Fact]
    public async Task ACheck_WithNoManifest_FailsWithoutARequest()
    {
        var answer = await Updater().CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCheckKind.Failed, answer.Kind);
        Assert.NotNull(answer.Reason);
    }

    [Fact]
    public async Task ACheck_AsksForTheInstalledPackagesPlatform_AndAFailureIsAnAnswer()
    {
        _f.Usual();
        Uri? asked = null;
        _f.Http.Map(Latest, (request, _) =>
        {
            asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        });

        var answer = await Updater().CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckKind.Failed, answer.Kind);
        Assert.Equal(Latest, asked?.AbsoluteUri);
    }

    [Fact]
    public async Task AReadyStage_IsReadFromDisk_AndDroppedKeepingNothingOfIt()
    {
        await Ready();
        var updater = Updater();
        Assert.Equal("0.5.7", updater.ReadDisk().ReadyVersion);

        Assert.Null(updater.DropStage(alsoParts: false));

        Assert.Null(updater.ReadDisk().ReadyVersion);
        Assert.False(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public async Task ADrop_WhileAnotherCopyHoldsTheStagingLock_IsRefused_AndTheStageStays()
    {
        await Ready();
        using (UpdateLock.TryExclusive(_f.Folder.StagingLock))
        {
            Assert.Equal(UpdateText.AnotherCopyStaging, Updater().DropStage(alsoParts: false));
        }

        Assert.True(File.Exists(_f.Folder.Ready));
    }

    [Fact]
    public void Rollback_WithNoPrevious_IsNothingToGoBackTo()
    {
        _f.Usual();
        Assert.Equal(UpdateText.NothingToGoBackTo, Updater().AskRollback(CancellationToken.None, out string? target));
        Assert.Null(target);
    }

    [Fact]
    public async Task Rollback_AfterAnUpdate_IsAsked_ReadFromDisk_AndTakenBack()
    {
        await Ready();
        Swapped();
        var updater = Updater(running: "0.5.7");

        Assert.Null(updater.AskRollback(CancellationToken.None, out string? target));

        Assert.Equal("0.5.6", target);
        Assert.Equal("0.5.6", updater.ReadDisk().RollbackTarget);
        updater.CancelRollback();
        Assert.Null(updater.ReadDisk().RollbackTarget);
    }

    [Fact]
    public async Task ARefusedRollback_LeavesAStageInPlace()
    {
        await Ready();
        Assert.NotNull(Updater().AskRollback(CancellationToken.None, out _));
        Assert.Equal("0.5.7", Updater().ReadDisk().ReadyVersion);
    }

    [Fact]
    public async Task RunningNotes_AreTheInstalledVersions_OnceSwapped()
    {
        await Ready();
        Assert.Null(Updater().RunningNotes());
        Swapped();
        Assert.Equal("the notes", Updater(running: "0.5.7").RunningNotes());
    }

    [Fact]
    public void Preflight_InAFolderWithNoManifest_Refuses()
    {
        Assert.NotNull(Updater().Preflight(1000));
    }
}
