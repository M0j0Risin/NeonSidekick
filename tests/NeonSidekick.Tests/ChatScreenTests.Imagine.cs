using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Comfy;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/imagine</c> behind the input line (2026-10-04, the user's report: while it ran, most keys and clicks waited for it): a
/// generation past the grace leaves the line to the user and is drawn at the loop top when it ends, in the order sent; the
/// strip's double-click cancels it. A looped one stays in the foreground, under the reply's own watch.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>
    /// A stub ComfyUI whose jobs finish when their prompt's <c>seed</c> is in <paramref name="released"/> (each prompt
    /// answered with an id of its seed, <c>p-5</c>), serving a 4×4 picture; until then a history read is empty. <c>/queue</c>
    /// and <c>/interrupt</c> answer, recorded (the drain).
    /// </summary>
    private StubHttpMessageHandler GatedComfyServer(Func<string, bool> released)
    {
        _settings.Update(d => { d.TtsOutput = false; d.ComfyUrl = "http://comfy.lan:8188"; d.ComfyWorkflowsOffered = ["pony"]; });
        File.WriteAllText(Path.Combine(_settings.ProfileComfyDirectory, "pony.json"),
            "{\"3\":{\"class_type\":\"KSampler\",\"inputs\":{\"seed\":\"{{seed}}\"}},\"6\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"{{prompt}}\"}},\"7\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"{{negative}}\"}}}");
        var stub = new StubHttpMessageHandler()
            .Map("http://comfy.lan:8188/prompt", async (request, ct) =>
            {
                string body = await request.Content!.ReadAsStringAsync(ct);
                string seed = System.Text.RegularExpressions.Regex.Match(body, "\"seed\":\"?(\\d+)").Groups[1].Value;
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{\"prompt_id\":\"p-" + seed + "\"}");
            })
            .Map("http://comfy.lan:8188/history/", (request, _) =>
            {
                string id = request.RequestUri!.Segments[^1];
                return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, released(id[2..])
                    ? "{\"" + id + "\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"x.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}"
                    : "{}"));
            })
            .Map("http://comfy.lan:8188/view", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, SmokeChecks.SolidBmp(4, 4), "image/png")))
            .Map("http://comfy.lan:8188/queue", HttpStatusCode.OK, "{}")
            .Map("http://comfy.lan:8188/interrupt", HttpStatusCode.OK, "{}")
            .Map("http://comfy.lan:8188/system_stats", HttpStatusCode.OK, "{\"system\":{\"comfyui_version\":\"0.3.40\"},\"devices\":[]}");
        _comfyClient = url => new ComfyClient(url, new HttpClient(stub), TimeSpan.FromMilliseconds(1));
        return stub;
    }

    private static string Generated(int seed) => ComfyText.TextToImageGlyph + $@"generated 1 picture with pony (seed {seed}, 1024×1024): comfy_images\pony-{seed}.png";

    /// <summary>
    /// A generation past the grace goes behind the line with the notice; a message sent meanwhile is answered at once and goes
    /// without the picture; the picture is drawn at the idle line when ComfyUI is done, and rides the next message.
    /// </summary>
    [WindowsFact]
    public async Task Imagine_PastTheGrace_GoesBehindTheLine_AMessageRunsMeanwhile_AndThePictureIsDrawnWhenItEnds()
    {
        bool released = false;
        GatedComfyServer(_ => Volatile.Read(ref released));
        _chat.EnqueueText("Hello.");
        _chat.EnqueueText("A black square.");
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            switch (step)
            {
                case 0:
                    step++;
                    PushLine(input, "/imagine a cat --seed 5");
                    StepPastTheGrace(() => Output.Contains(ComfyText.ImagineInBackground, StringComparison.Ordinal));
                    break;
                case 1 when Output.Contains(ComfyText.ImagineInBackground, StringComparison.Ordinal):
                    step++;
                    PushLine(input, "hi");
                    break;
                case 2 when Output.Contains("Hello.", StringComparison.Ordinal):
                    step++;
                    Volatile.Write(ref released, true);
                    break;
                case 3 when Output.Contains(Generated(5), StringComparison.Ordinal):
                    step++;
                    PushLine(input, "what did you make?");
                    break;
                case 4 when Output.Contains("A black square.", StringComparison.Ordinal):
                    step++;
                    PushLine(input, "/exit");
                    break;
            }
        };

        string output = await RunAsync();

        Assert.Contains("  · " + ComfyText.ImagineInBackground, output);
        Assert.True(output.IndexOf("Hello.", StringComparison.Ordinal) < output.IndexOf(Generated(5), StringComparison.Ordinal), output);
        Assert.Equal(2, _chat.Requests.Count);
        var first = _chat.Requests[0].Last(m => m.Role == ChatRole.User);
        Assert.Equal("hi", first.Text);                                  // sent while the picture was still being made
        Assert.Empty(first.Contents.OfType<DataContent>());
        var second = _chat.Requests[1].Last(m => m.Role == ChatRole.User);
        Assert.StartsWith("(the user made a picture with /imagine", second.Text);
        Assert.EndsWith("what did you make?", second.Text);
        Assert.Single(second.Contents.OfType<DataContent>());
        Assert.True(File.Exists(Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, "comfy_images", "pony-5.png")));
    }

    /// <summary>Two behind the line, the second done first: drawn in the order sent, the second held back until the first ends.</summary>
    [WindowsFact]
    public async Task Imagine_TwoBehindTheLine_AreDrawnInTheOrderSent()
    {
        var done = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        GatedComfyServer(done.ContainsKey);
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            switch (step)
            {
                case 0:
                    step++;
                    PushLine(input, "/imagine a cat --seed 5");
                    StepPastTheGrace(() => CountOf(Output, ComfyText.ImagineInBackground) >= 1);
                    break;
                case 1 when CountOf(Output, ComfyText.ImagineInBackground) == 1:
                    step++;
                    PushLine(input, "/imagine a dog --seed 6");
                    StepPastTheGrace(() => CountOf(Output, ComfyText.ImagineInBackground) >= 2);
                    break;
                case 2 when CountOf(Output, ComfyText.ImagineInBackground) == 2:
                    step++;
                    done["6"] = true;
                    break;
                case 3 when PictureSaved(6):
                    step++;
                    done["5"] = true;
                    break;
                case 4 when Output.Contains(Generated(6), StringComparison.Ordinal):
                    step++;
                    PushLine(input, "/exit");
                    break;
            }
        };

        string output = await RunAsync();

        Assert.True(output.IndexOf(Generated(5), StringComparison.Ordinal) < output.IndexOf(Generated(6), StringComparison.Ordinal), output);
        Assert.Equal(1, CountOf(output, Generated(5)));
        Assert.Equal(1, CountOf(output, Generated(6)));

        // The second's picture is on disk before the first is: its generation has ended, its drawing waits.
        bool PictureSaved(int seed) =>
            File.Exists(Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, "comfy_images", $"pony-{seed}.png"));
    }

    /// <summary>The strip's 🖼️ double-clicked at the idle line: the generation behind the line is cancelled with one notice, and no picture comes.</summary>
    [Fact]
    public async Task Imagine_BehindTheLine_ADoubleClickOnTheStrip_CancelsIt_WithOneNotice()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the hint row at 102
        var stub = GatedComfyServer(_ => false);
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            switch (step)
            {
                case 0:
                    step++;
                    PushLine(input, "/imagine a cat --seed 5");
                    StepPastTheGrace(() => Output.Contains(ComfyText.ImagineInBackground, StringComparison.Ordinal));
                    break;
                case 1 when Output.Contains(ComfyText.ImagineInBackground, StringComparison.Ordinal):
                    step++;
                    _time.Advance(ScreenPane.Tick);
                    input.PushClick(0, 102);                  // 🖼️ at 0–1: speech is off
                    input.PushClick(1, 102);
                    break;
                case 2 when Output.Contains("· " + ComfyText.Drained(1), StringComparison.Ordinal):
                    step++;
                    PushLine(input, "/exit");
                    break;
            }
        };

        string output = await RunAsync();

        Assert.Equal(1, CountOf(output, "· " + ComfyText.Cancelled));   // the drain's notice, not a second from the job's end
        Assert.DoesNotContain("generated 1 picture", output);
        Assert.Contains(stub.Requests, r => r.Uri.AbsolutePath == "/queue" && r.Body == "{\"delete\":[\"p-5\"]}");
        Assert.Contains("› /exit", output);
    }

    /// <summary>A generation behind the line that fails: its error at the idle line, when it ends.</summary>
    [Fact]
    public async Task Imagine_BehindTheLine_AFailure_IsAnErrorAtTheIdleLine()
    {
        bool fail = false;
        GatedComfyServer(_ => false);
        // Done once fail is set, and its picture's download refused.
        var failing = new StubHttpMessageHandler()
            .Map("http://comfy.lan:8188/prompt", HttpStatusCode.OK, "{\"prompt_id\":\"p-5\"}")
            .Map("http://comfy.lan:8188/history/", (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, Volatile.Read(ref fail)
                ? "{\"p-5\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"x.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}"
                : "{}")))
            .Map("http://comfy.lan:8188/view", HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
        _comfyClient = url => new ComfyClient(url, new HttpClient(failing), TimeSpan.FromMilliseconds(1));
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            switch (step)
            {
                case 0:
                    step++;
                    PushLine(input, "/imagine a cat --seed 5");
                    StepPastTheGrace(() => Output.Contains(ComfyText.ImagineInBackground, StringComparison.Ordinal));
                    break;
                case 1 when Output.Contains(ComfyText.ImagineInBackground, StringComparison.Ordinal):
                    step++;
                    Volatile.Write(ref fail, true);
                    break;
                case 2 when Output.Contains("  ✗ ", StringComparison.Ordinal):
                    step++;
                    PushLine(input, "/exit");
                    break;
            }
        };

        string output = await RunAsync();

        Assert.True(output.IndexOf(ComfyText.ImagineInBackground, StringComparison.Ordinal) < output.IndexOf("  ✗ ", StringComparison.Ordinal), output);
        Assert.DoesNotContain("generated 1 picture", output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// <c>/help</c> typed in a looped <c>/imagine</c>'s wait opens there (the reply's own watch, 2026-10-04; it waited for the
    /// loop's end until then), the next pass waits for the pane to close, and the ESC that closes it does not stop the loop.
    /// </summary>
    [WindowsFact]
    public async Task Loop_Imagine_APaneTypedInTheWait_OpensThere_AndTheLoopGoesOnAfterIt()
    {
        UsePane();
        ComfyServer();
        var input = LoopScript("/loop 2 30s /imagine a cat --seed 5", "/exit");
        using var done = new CancellationTokenSource();
        var help = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested && !Output.Contains(ChatScreen.LoopWaitNotice(TimeSpan.FromSeconds(30)), StringComparison.Ordinal))
            {
                await Task.Delay(5, CancellationToken.None);
            }

            PushLine(input, "/help");
            while (!done.IsCancellationRequested && !Output.Contains(InfoPane.Title, StringComparison.Ordinal))
            {
                await Task.Delay(5, CancellationToken.None);
            }

            input.Push(Keys.Escape);
        });

        string output = await RunLoopAsync();
        await done.CancelAsync();
        await help;

        int pane = output.IndexOf(InfoPane.Title, StringComparison.Ordinal);
        Assert.True(pane >= 0, output);
        Assert.True(pane < output.IndexOf(ChatScreen.LoopTurnNotice(2, 2), StringComparison.Ordinal), output);   // in the wait, not after the loop
        Assert.Contains("  · " + ChatScreen.LoopCommandDoneNotice(2), output);
        Assert.DoesNotContain(ChatScreen.LoopCommandStoppedNotice(1), output);
    }
}
