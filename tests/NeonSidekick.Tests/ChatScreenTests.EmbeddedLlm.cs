using System.Net;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The embedded model on the screen (2026-09-29): its <c>/server</c> rows, the install before the switch, <c>/server embedded</c>, the startup picker.</summary>
public partial class ChatScreenTests
{
    /// <summary>The fixture's session swapped for one over <paramref name="embedded"/>, its server's <c>/props</c> answered on the stub.</summary>
    private FakeEmbeddedLlm UseEmbedded(FakeEmbeddedLlm embedded)
    {
        _session.Dispose();
        _session = new LlmSession(new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)), new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)), (endpoint, _) => { _endpoints.Add(endpoint); return _chat; }, _time, embedded: embedded);
        _http.Map("http://127.0.0.1:59999/props", HttpStatusCode.OK, "{\"default_generation_settings\":{\"n_ctx\":32768}}");
        return embedded;
    }

    private const string EmbeddedConnectedE2b = "LLM: http://embedded-llm.invalid/v1 model=gemma-4-e2b (embedded llama.cpp b11258 cuda on 127.0.0.1:59999)";

    [Fact]
    public async Task Server_ListsTheEmbeddedModels_AndPickingAnInstalledOne_SavesItAndStartsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        PushLine("/server");
        for (int i = 0; i < 19; i++)
        {
            _console.Input.PushKey(Keys.Down);  // past LM Studio, the 12B's six builds and the 26B A4B's and 31B's twelve to Gemma 4 E2B
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);    // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.ServerTitle, output);
        // Every row's detail in one column: the names padded as the catalog pads them, the quantisations after, and the
        // detail's · under one another (2026-09-29).
        Assert.Contains("Embedded   " + "Gemma 4 E4B QAT".PadRight(32) + "UD-Q4_K_XL  download  · 5.3 GB", output);   // each with its MTP drafter (2026-09-29)
        Assert.Contains("Embedded   " + "Gemma 4 E2B".PadRight(32) + "UD-Q4_K_XL  installed · 4.3 GB", output);
        Assert.Contains("Embedded   " + "Gemma 4 12B QAT Uncensored".PadRight(32) + "Q4_K_M      download  · 7.8 GB", output);
        Assert.Contains("Embedded   " + "Gemma 4 12B".PadRight(32) + "BF16        download  · 24.5 GB", output);
        Assert.Contains("Embedded   " + "Gemma 4 26B A4B QAT Uncensored".PadRight(32) + "Q4_K_M      download  · 18.2 GB", output);
        Assert.Contains("LM Studio  " + "http://127.0.0.1:1234/v1".PadRight(42) + "  1 chat model", output);   // the URL column as wide as a name and a quantisation (38 until the 26B A4B names, 2026-09-29)
        Assert.Contains("  · 🖥️ LLM URL: " + EmbeddedLlmText.UrlDisplay, output);
        Assert.Contains(SettingsMenu.ReasoningTitle, output);
        Assert.DoesNotContain(SettingsMenu.ModelTitle, output);   // an embedded row is one model: no model step
        Assert.Equal("http://embedded-llm.invalid/v1", _settings.Current.LlmUrl);
        Assert.Equal("gemma-4-e2b", _settings.Current.LlmModel);
        Assert.Equal(["gemma-4-e2b"], embedded.Starts);
        Assert.Empty(embedded.Installs);
        Assert.Contains(EmbeddedConnectedE2b, output);
        Assert.Equal(FakeEmbeddedLlm.LiveUrl, _endpoints[^1].LiveUrl);
    }

    [Fact]
    public async Task Server_PickingAnEmbeddedModelNotInstalled_DownloadsItFirst_ThenStartsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        var embedded = UseEmbedded(new FakeEmbeddedLlm());
        PushLine("/server");
        for (int i = 0; i < 22; i++)
        {
            _console.Input.PushKey(Keys.Down);  // the twenty-second embedded row: Gemma 4 E4B QAT
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);    // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["gemma-4-e4b-qat"], embedded.Installs);
        Assert.Equal(["gemma-4-e4b-qat"], embedded.Starts);
        Assert.Contains(NoticeGlyphs.Llm + EmbeddedLlmText.Installed(EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!), output);
        Assert.Equal("gemma-4-e4b-qat", _settings.Current.LlmModel);
    }

    /// <summary>
    /// Moves the manual clock on from the pool until <paramref name="done"/>: a job held open past <see cref="BackgroundJobs.Grace"/>
    /// (whose wait runs on the screen's clock) leaves the line to the user. Started from inside the job, before its grace wait
    /// is armed, so it keeps stepping until the screen reads again.
    /// </summary>
    private void StepPastTheGrace(Func<bool> done) => _ = Task.Run(async () =>
    {
        for (int i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10).ConfigureAwait(false);
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }
    });

    /// <summary>
    /// A download behind the line (2026-09-29, the user's ask): picked in <c>/server</c>, the reasoning asked first, then the
    /// line is the user's while it runs — a command typed meanwhile runs — and its end installs, saves and connects.
    /// </summary>
    [Fact]
    public async Task Server_AnEmbeddedDownload_RunsBehindTheLine_ThenSwitchesToIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        var input = Scripted();
        var embedded = UseEmbedded(new FakeEmbeddedLlm());
        var release = new TaskCompletionSource();
        bool idle = false;
        embedded.InstallGate = async ct =>
        {
            StepPastTheGrace(() => Volatile.Read(ref idle));
            await release.Task.WaitAsync(ct);
        };
        int step = 0;
        input.OnWait = () =>
        {
            switch (step)
            {
                case 0:
                    step++;
                    PushLine(input, "/server");
                    input.Push(Keys.Down, Keys.Enter, Keys.Escape);   // the first embedded row, not installed; keep the reasoning
                    break;
                case 1:
                    step++;
                    Volatile.Write(ref idle, true);
                    Assert.Empty(embedded.Starts);
                    PushLine(input, "/cwd");                          // the line is the user's while it downloads
                    break;
                case 2:
                    step++;
                    release.SetResult();
                    break;
                case 3:
                    if (embedded.Starts.Count > 0)
                    {
                        step++;
                        PushLine(input, "/exit");
                    }

                    break;
            }
        };

        string output = await RunAsync();

        var model = EmbeddedModelCatalog.Models[0];
        Assert.Contains(BackgroundJobText.DownloadStarted(model), output);
        Assert.True(output.IndexOf("› /cwd", StringComparison.Ordinal) < output.IndexOf(EmbeddedLlmText.Installed(model), StringComparison.Ordinal), output);
        Assert.Equal([model.Id], embedded.Installs);
        Assert.Equal([model.Id], embedded.Starts);
        Assert.Equal("http://embedded-llm.invalid/v1", _settings.Current.LlmUrl);
        Assert.Equal(model.Id, _settings.Current.LlmModel);
    }

    /// <summary>
    /// The download behind the line paused by a double-click on its 📥 (2026-09-29; Ctrl+C under a spinner until then): the
    /// paused notice, nothing saved, nothing started.
    /// </summary>
    [Fact]
    public async Task Server_AnEmbeddedDownloadPaused_SavesNothing_AndSaysSo()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);
        var input = Scripted();
        var embedded = UseEmbedded(new FakeEmbeddedLlm());
        bool idle = false;
        embedded.InstallGate = async ct =>
        {
            StepPastTheGrace(() => Volatile.Read(ref idle));
            await Task.Delay(Timeout.Infinite, ct);
        };
        int step = 0;
        input.OnWait = () =>
        {
            switch (step)
            {
                case 0:
                    step++;
                    PushLine(input, "/server");
                    input.Push(Keys.Down, Keys.Enter, Keys.Escape);
                    break;
                case 1:
                    step++;
                    Volatile.Write(ref idle, true);
                    _time.Advance(ScreenPane.Tick);
                    input.PushClick(0, 102);                          // 📥 at 0–1, the strip's first part
                    input.PushClick(1, 102);
                    break;
                case 2:
                    if (Output.Contains(EmbeddedLlmText.PausedNotice, StringComparison.Ordinal))
                    {
                        step++;
                        PushLine(input, "/exit");
                    }

                    break;
            }
        };

        string output = await RunAsync();

        Assert.Contains(BackgroundJobText.DownloadGlyph, output);
        Assert.Contains("· " + EmbeddedLlmText.PausedNotice, output);
        Assert.Equal("http://127.0.0.1:1234/v1", _settings.Current.LlmUrl);   // nothing saved before the model is there
        Assert.Empty(embedded.Starts);
        Assert.DoesNotContain(SettingsMenu.Title + "   General", output);   // the click was the job's, never the row's /settings
    }

    /// <summary>
    /// The catalog's Remove on the model downloading behind the line (2026-09-29, the user's ask): the download is stopped
    /// and awaited before the folder goes — no paused notice, nothing saved, nothing started — and the 📥 slot is free.
    /// </summary>
    [Fact]
    public async Task Settings_RemovingTheModelThatDownloads_StopsTheDownloadFirst()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the pane: /settings with its tabs
        var input = Scripted();
        var embedded = UseEmbedded(new FakeEmbeddedLlm());
        var model = EmbeddedModelCatalog.Models[0];
        bool idle = false;
        bool cancelled = false;
        embedded.InstallGate = async ct =>
        {
            embedded.Partial(model.Id, 10);                   // bytes on disk: the catalog offers Remove
            StepPastTheGrace(() => Volatile.Read(ref idle));
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                Volatile.Write(ref cancelled, true);
                Assert.Empty(embedded.Removes);               // stopped before the folder went
            }
        };
        int step = 0;
        input.OnWait = () =>
        {
            switch (step)
            {
                case 0:
                    step++;
                    PushLine(input, "/server");
                    input.Push(Keys.Down, Keys.Enter, Keys.Escape);   // the first embedded row, not installed; keep the reasoning
                    break;
                case 1:
                    step++;
                    Volatile.Write(ref idle, true);
                    PushLine(input, "/settings");
                    input.Push(Enumerable.Repeat(Keys.Right, (int)SettingsTab.Embedded).ToArray());
                    input.Push(Keys.Down, Keys.Enter);                // the catalog, on the first model
                    input.Push(Keys.Enter);                           // its page: Install, Remove, Back
                    input.Push(Keys.Down, Keys.Enter);                // Remove (the partial download)
                    input.Push(Keys.Down, Keys.Enter);                // Yes
                    input.Push(Keys.Escape, Keys.Escape);
                    break;
                case 2:
                    if (embedded.Removes.Count > 0)
                    {
                        step++;
                        PushLine(input, "/exit");
                    }

                    break;
            }
        };

        string output = await RunAsync();

        Assert.True(Volatile.Read(ref cancelled));
        Assert.Equal([model.Id], embedded.Removes);
        Assert.Contains(EmbeddedLlmText.Removed(model), output);
        Assert.DoesNotContain(EmbeddedLlmText.PausedNotice, output);   // its end dropped: the files are gone, not paused
        Assert.Empty(embedded.Starts);
        Assert.Equal("http://127.0.0.1:1234/v1", _settings.Current.LlmUrl);
    }

    [Fact]
    public async Task Server_Embedded_ListsTheEmbeddedModelsAlone()
    {
        _settings.Update(d => d.TtsOutput = false);
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b", "gemma-4-e4b-qat"));
        PushLine("/server embedded");
        for (int i = 0; i < 18; i++)
        {
            _console.Input.PushKey(Keys.Down);
        }

        _console.Input.PushKey(Keys.Enter);     // the nineteenth embedded row: Gemma 4 E2B
        _console.Input.PushKey(Keys.Escape);    // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain("LM Studio  http://127.0.0.1:1234/v1", output);
        Assert.Equal(["gemma-4-e2b"], embedded.Starts);
        Assert.Equal(1, ModelProbes);   // the startup connect alone: /server embedded asked no server
    }

    [Fact]
    public async Task Startup_OnAnEmbeddedUrl_StartsTheSavedModel()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "embedded"; d.LlmModel = "gemma-4-e2b"; });
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["gemma-4-e2b"], embedded.Starts);
        Assert.Equal(0, ModelProbes);   // no scan, no /v1/models
        Assert.DoesNotContain("LLM: ", output);   // quiet under the banner: the settings name the endpoint
        Assert.NotNull(_session.Assistant);
    }

    [Fact]
    public async Task Startup_OnAnEmbeddedModelNotInstalled_OffersTheInstall_AndNoKeepsItOff()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "embedded"; d.LlmModel = "gemma-4-e4b-qat"; });
        var embedded = UseEmbedded(new FakeEmbeddedLlm { RuntimeBytes = 577_081_932 });
        _console.Input.PushKey(Keys.Enter);     // No (the cursor opens on No)
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(EmbeddedLlmText.InstallQuestion(EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!, 577_081_932), output);
        Assert.Empty(embedded.Installs);
        Assert.Empty(embedded.Starts);
        Assert.Contains(EmbeddedLlmText.NotInstalled(EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!), output);
        Assert.Null(_session.Assistant);
    }

    [Fact]
    public async Task Startup_Escape_OnTheServerPicker_NeverStartsAnEmbeddedModel()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = ""; });
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        _console.Input.PushKey(Keys.Escape);    // the startup picker: the first server that answered
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.StartupServerTitle, output);
        Assert.Empty(embedded.Starts);
        Assert.Contains("LLM: http://127.0.0.1:1234/v1 model=llama (probed http://127.0.0.1:1234/v1)", output);
    }

    [Fact]
    public async Task SwitchedOff_TheSavedModelNeverStarts_TheRowsAreGone_AndServerEmbeddedRefuses()
    {
        // Embedded LLM enabled off (2026-09-29, the user's ask): the saved embedded URL reads as none, so the startup picker
        // opens on the scan's servers alone, and /server embedded says why it lists nothing.
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "embedded"; d.LlmModel = "gemma-4-e2b"; d.EmbeddedLlmEnabled = false; });
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        _console.Input.PushKey(Keys.Escape);    // the startup picker: the first server that answered
        PushLine("/server embedded");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.StartupServerTitle, output);
        Assert.DoesNotContain("Embedded   Gemma", output);
        Assert.Contains(EmbeddedLlmText.SwitchedOffError, output);
        Assert.Empty(embedded.Starts);
        Assert.Equal("http://127.0.0.1:1234/v1", _session.Endpoint?.BaseUrl.AbsoluteUri);   // the first server that answered
    }

    [Fact]
    public async Task ASwitchAway_StopsTheEmbeddedServer()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "embedded"; d.LlmModel = "gemma-4-e2b"; });
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        PushLine("/server http://127.0.0.1:1234");
        _console.Input.PushKey(Keys.Enter);     // llama
        _console.Input.PushKey(Keys.Escape);    // keep the reasoning
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(1, embedded.Stops);
        Assert.Null(embedded.Running);
        Assert.Equal("http://127.0.0.1:1234/v1", _settings.Current.LlmUrl);
    }

    /// <summary>A message with a pasted picture (Alt+V over the clipboard's image), then /exit.</summary>
    private ScriptedInput PictureMessage()
    {
        _clipboardImage = () => SmokeChecks.SolidBmp(4, 4);
        var input = new ScriptedInput();
        foreach (char c in "what is ")
        {
            input.Push(Keys.Char(c));
        }

        input.Push(new ConsoleKeyInfo('v', ConsoleKey.V, false, true, false)).Push(Keys.Char('?')).Push(Keys.Enter);
        PushLine(input, "/exit");
        return input;
    }

    [Fact]
    public async Task APicture_ToAModelWhoseVisionIsOff_PointsAtTheSwitch()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "embedded"; d.LlmModel = "gemma-4-e2b"; d.EmbeddedVision = false; });
        UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));

        string output = await RunAsync(PictureMessage());

        Assert.Contains(EmbeddedLlmText.NoVisionError, output);
        Assert.Empty(_chat.Requests);
    }
}
