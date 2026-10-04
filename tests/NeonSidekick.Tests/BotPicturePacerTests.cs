using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The pace of <c>/botchat</c>'s app pictures with no voice (2026-09-25): one at a time, a second's rest after each.</summary>
public class BotPicturePacerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, true, false, true)]    // TTS on but the speech not ready: no voice either
    [InlineData(true, true, true, true, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    public void Applies_PicturesAndAsyncOn_WithNoVoice(bool images, bool async, bool tts, bool speechReady, bool expected)
    {
        // Pictures are a ComfyUI workflow chosen since later on 2026-10-04 (BotChat.ComfyChosen): the switch, or the limited list.
        var data = new AppSettingsData { BotChatComfy = images, BotChatImageAsync = async, TtsOutput = tts };
        Assert.Equal(expected, BotPicturePacer.Applies(data, speechReady));
        var limited = new AppSettingsData { BotChatLimitedComfyWorkflows = images ? ["pony"] : null, BotChatImageAsync = async, TtsOutput = tts };
        Assert.Equal(expected, BotPicturePacer.Applies(limited, speechReady));
    }

    [Fact]
    public async Task TheFirstPicture_IsSentAtOnce()
    {
        var pacer = new BotPicturePacer(new ManualTimeProvider());
        Assert.Equal(1, await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None).WaitAsync(Wait));
    }

    [Fact]
    public async Task TheNextPicture_WaitsASecondAfterTheLastWasMade()
    {
        var time = new ManualTimeProvider();
        var pacer = new BotPicturePacer(time);
        await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None).WaitAsync(Wait);

        bool sent = false;
        var second = pacer.RunAsync(_ => { sent = true; return Task.FromResult(2); }, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50);
        Assert.False(sent);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, await second.WaitAsync(Wait));
        Assert.True(sent);
    }

    [Fact]
    public async Task APictureAskedForLongAfterTheLast_IsSentAtOnce()
    {
        var time = new ManualTimeProvider();
        var pacer = new BotPicturePacer(time);
        await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None).WaitAsync(Wait);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(2, await pacer.RunAsync(_ => Task.FromResult(2), CancellationToken.None).WaitAsync(Wait));
    }

    [Fact]
    public async Task APictureAskedForWhileOneRenders_WaitsForIt_ThenTheRest()
    {
        var time = new ManualTimeProvider();
        var pacer = new BotPicturePacer(time);
        var rendering = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = pacer.RunAsync(_ => rendering.Task, CancellationToken.None);

        bool sent = false;
        var second = pacer.RunAsync(_ => { sent = true; return Task.FromResult(2); }, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(3));
        await Task.Delay(50);
        Assert.False(sent);

        rendering.SetResult(1);
        Assert.Equal(1, await first.WaitAsync(Wait));
        await Task.Delay(50);
        Assert.False(sent);

        time.Advance(BotPicturePacer.Gap);
        Assert.Equal(2, await second.WaitAsync(Wait));
    }

    [Fact]
    public async Task ACancelledWait_SendsNothing()
    {
        var time = new ManualTimeProvider();
        var pacer = new BotPicturePacer(time);
        await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None).WaitAsync(Wait);

        using var cts = new CancellationTokenSource();
        bool sent = false;
        var second = pacer.RunAsync(_ => { sent = true; return Task.FromResult(2); }, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Wait));
        Assert.False(sent);
    }
}
