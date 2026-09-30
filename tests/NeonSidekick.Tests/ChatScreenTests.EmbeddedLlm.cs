using System.Net;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The embedded model on the screen (2026-09-29): its <c>/server</c> rows (the installed models alone since later that day), the catalog's install before the switch, <c>/server embedded</c>, the startup picker.</summary>
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

    private const string EmbeddedConnectedE2b = "LLM: http://embedded.localhost/v1 model=gemma-4-e2b (embedded llama.cpp b11258 cuda on 127.0.0.1:59999)";

    [Fact]
    public async Task Server_ListsTheInstalledEmbeddedModelsAlone_AndPickingOne_SavesItAndStartsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b", "gemma-4-12b-qat-uncensored"));
        PushLine("/server");
        for (int i = 0; i < 2; i++)
        {
            _console.Input.PushKey(Keys.Down);  // past LM Studio and the 12B QAT Uncensored to Gemma 4 E2B, the only rows
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);    // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.ServerTitle, output);
        // Every row's detail in one column: the names padded as the catalog pads them, the quantisations after, and the
        // detail's · under one another (2026-09-29). The installed models alone (later that day, the user's ask): a
        // download starts from /settings › Embedded, never from /server.
        Assert.Contains("Embedded   " + "Gemma 4 E2B".PadRight(32) + "UD-Q4_K_XL  installed · 4.3 GB", output);
        Assert.Contains("Embedded   " + "Gemma 4 12B QAT Uncensored".PadRight(32) + "Q4_K_M      installed · 7.8 GB", output);
        Assert.DoesNotContain("download  · ", output);
        Assert.DoesNotContain("Gemma 4 E4B QAT", output);
        Assert.Contains("LM Studio  " + "http://127.0.0.1:1234/v1".PadRight(42) + "  1 chat model", output);   // the URL column as wide as the widest name and quantisation shown
        Assert.Matches(@"installed · 4\.3 GB +⚡", output);   // the drafter column (2026-09-29), one column down the list
        Assert.Contains("  · 🖥️ LLM URL: " + EmbeddedLlmText.UrlDisplay, output);
        Assert.Contains(SettingsMenu.ReasoningTitle, output);
        Assert.DoesNotContain(SettingsMenu.ModelTitle, output);   // an embedded row is one model: no model step
        Assert.Equal("http://embedded.localhost/v1", _settings.Current.LlmUrl);
        Assert.Equal("gemma-4-e2b", _settings.Current.LlmModel);
        Assert.Equal(["gemma-4-e2b"], embedded.Starts);
        Assert.Empty(embedded.Installs);
        Assert.Contains(EmbeddedConnectedE2b, output);
        Assert.Equal(FakeEmbeddedLlm.LiveUrl, _endpoints[^1].LiveUrl);
    }

    /// <summary>The fixture sized for the pane, so <c>/settings</c> opens with its tabs (the Embedded tab's catalog door).</summary>
    private void UsePane()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);
    }

    /// <summary>The keys from the input line to the catalog's Install of its first model: <c>/settings</c>, the Embedded tab, the <c>Embedded models</c> door, the model, Install.</summary>
    private static void InstallTheFirstModelFromTheCatalog(ScriptedInput input)
    {
        PushLine(input, "/settings");
        input.Push(Enumerable.Repeat(Keys.Right, (int)SettingsTab.Embedded).ToArray());
        input.Push(Keys.Down, Keys.Enter);                // the catalog, on the first model
        input.Push(Keys.Enter);                           // its page: Install, Back
        input.Push(Keys.Enter);                           // Install: the pane closes, the screen downloads
    }

    [Fact]
    public async Task Server_ListsNoEmbeddedModelNotInstalled_TheCatalogInstallsIt_ThenStartsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        UsePane();
        var embedded = UseEmbedded(new FakeEmbeddedLlm());
        var model = EmbeddedModelCatalog.Models[0];
        var input = new ScriptedInput();
        PushLine(input, "/server");
        input.Push(Keys.Escape);                          // LM Studio alone: nothing embedded is installed
        InstallTheFirstModelFromTheCatalog(input);
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.DoesNotContain("Embedded   " + model.Display, output);
        Assert.Equal([model.Id], embedded.Installs);
        Assert.Equal([model.Id], embedded.Starts);
        Assert.Contains(NoticeGlyphs.Llm + EmbeddedLlmText.Installed(model), output);
        Assert.Equal(model.Id, _settings.Current.LlmModel);
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
    /// A download behind the line (2026-09-29, the user's ask): picked in the catalog on <c>/settings</c> › Embedded (in
    /// <c>/server</c> until later that day, which lists the installed models alone since), then the line is the user's while
    /// it runs — a command typed meanwhile runs — and its end installs, saves and connects.
    /// </summary>
    [Fact]
    public async Task Settings_AnEmbeddedDownload_RunsBehindTheLine_ThenSwitchesToIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        UsePane();
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
                    InstallTheFirstModelFromTheCatalog(input);
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
        Assert.Equal("http://embedded.localhost/v1", _settings.Current.LlmUrl);
        Assert.Equal(model.Id, _settings.Current.LlmModel);
    }

    /// <summary>
    /// The download behind the line paused by a double-click on its 📥 (2026-09-29; Ctrl+C under a spinner until then): the
    /// paused notice, nothing saved, nothing started.
    /// </summary>
    [Fact]
    public async Task Settings_AnEmbeddedDownloadPaused_SavesNothing_AndSaysSo()
    {
        _settings.Update(d => d.TtsOutput = false);
        UsePane();
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
                    InstallTheFirstModelFromTheCatalog(input);
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
        int started = output.IndexOf(BackgroundJobText.DownloadStarted(EmbeddedModelCatalog.Models[0]), StringComparison.Ordinal);
        Assert.True(started >= 0, output);
        Assert.DoesNotContain(SettingsMenu.Title + "   General", output[started..]);   // the click was the job's, never the row's /settings (the catalog's own /settings closed before)
    }

    /// <summary>
    /// The catalog's Remove on the model downloading behind the line (2026-09-29, the user's ask): the download is stopped
    /// and awaited before the folder goes — no paused notice, nothing saved, nothing started — and the 📥 slot is free.
    /// </summary>
    [Fact]
    public async Task Settings_RemovingTheModelThatDownloads_StopsTheDownloadFirst()
    {
        _settings.Update(d => d.TtsOutput = false);
        UsePane();
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
                    InstallTheFirstModelFromTheCatalog(input);
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
    public async Task Server_Embedded_ListsTheInstalledEmbeddedModelsAlone()
    {
        _settings.Update(d => d.TtsOutput = false);
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b", "gemma-4-e4b-qat"));
        PushLine("/server embedded");
        _console.Input.PushKey(Keys.Enter);     // the first of the two rows: Gemma 4 E2B (E4B QAT after it in the catalog)
        _console.Input.PushKey(Keys.Escape);    // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain("LM Studio  http://127.0.0.1:1234/v1", output);
        Assert.Contains("Gemma 4 E4B QAT", output);
        Assert.DoesNotContain("Gemma 4 12B", output);   // not installed: the catalog's, not /server's (2026-09-29, the user's ask)
        Assert.Equal(["gemma-4-e2b"], embedded.Starts);
        Assert.Equal(1, ModelProbes);   // the startup connect alone: /server embedded asked no server
    }

    [Fact]
    public async Task Server_Embedded_WithNoneInstalled_PointsAtTheCatalog()
    {
        _settings.Update(d => d.TtsOutput = false);
        var embedded = UseEmbedded(new FakeEmbeddedLlm());
        PushLine("/server embedded");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(EmbeddedLlmText.NoneInstalled, output);
        Assert.DoesNotContain(SettingsMenu.ServerTitle, output);   // no empty picker
        Assert.Empty(embedded.Starts);
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
        // Embedded LLM server enabled off (2026-09-29, the user's ask): the saved embedded URL reads as none, so the startup picker
        // opens on the scan's servers alone, and /server embedded says why it lists nothing.
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "embedded"; d.LlmModel = "gemma-4-e2b"; d.EmbeddedLlmServer = false; });
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

    // ── The start with nothing to connect to (2026-09-30, the user's ask) ─────────────────────────────

    [Fact]
    public async Task Startup_ScanDisabled_NoModelDownloaded_OpensTheCatalog_WhoseInstallStartsIt()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmScanMode = "disabled"; d.LlmUrl = ""; });
        UsePane();
        var embedded = UseEmbedded(new FakeEmbeddedLlm());
        var model = EmbeddedModelCatalog.Models[0];
        var input = new ScriptedInput();
        input.Push(Keys.Enter);                           // the catalog, open by itself: the first model's page
        input.Push(Keys.Enter);                           // Install: the pane closes, the screen downloads
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.Contains("✗ " + LlmSession.NoEmbeddedLine, output);
        Assert.DoesNotContain("🖥️ Set the URL", output);   // one line, no hint under it (2026-09-30)
        Assert.DoesNotContain(LlmSession.NoServerLine(ScanScope.Disabled), output);
        Assert.Equal(0, ModelProbes);                     // nothing was looked for
        Assert.Equal([model.Id], embedded.Installs);
        Assert.Equal([model.Id], embedded.Starts);
        Assert.Equal(model.Id, _settings.Current.LlmModel);
    }

    [Fact]
    public async Task Startup_ScanDisabled_NoModelDownloaded_EscapeTwice_LeavesTheLine_AndServerSaysTheSame()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmScanMode = "disabled"; d.LlmUrl = ""; });
        UsePane();
        var embedded = UseEmbedded(new FakeEmbeddedLlm());
        var input = new ScriptedInput();
        input.Push(Keys.Escape);                          // out of the catalog: the settings list
        input.Push(Keys.Escape);                          // the pane closes
        PushLine(input, "/server");                       // never opens the catalog itself
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.Contains(EmbeddedModelCatalog.Models[0].Display, output);   // the catalog's rows were shown
        Assert.Equal(2, Count(output, "✗ " + LlmSession.NoEmbeddedLine));   // the launch, then /server
        Assert.Empty(embedded.Installs);
        Assert.Empty(embedded.Starts);
        Assert.Null(_session.Endpoint);
    }

    [Fact]
    public async Task Startup_ScanDisabled_AModelDownloaded_OffersThePicker_NotTheCatalog()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmScanMode = "disabled"; d.LlmUrl = ""; });
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        _console.Input.PushKey(Keys.Escape);              // the startup picker, its one embedded row: ESC never starts it
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.StartupServerTitle, output);
        // The picker declined (2026-09-30, the user's report): one line naming it, not the no-URL pair.
        Assert.Contains("✗ LLM: no server picked; /server lists the installed embedded models again", output);
        Assert.DoesNotContain(LlmSession.NoServerLine(ScanScope.Disabled), output);
        Assert.DoesNotContain("🖥️ Set the URL", output);
        Assert.DoesNotContain(LlmSession.NoEmbeddedLine, output);
        Assert.Empty(embedded.Starts);
    }

    [Fact]
    public async Task Startup_EmbeddedSwitchedOff_KeepsTheOldLines()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmScanMode = "disabled"; d.LlmUrl = ""; d.EmbeddedLlmServer = false; });
        UsePane();
        UseEmbedded(new FakeEmbeddedLlm());
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("✗ " + LlmSession.NoServerLine(ScanScope.Disabled), output);
        Assert.DoesNotContain(LlmSession.NoEmbeddedLine, output);
        Assert.DoesNotContain(EmbeddedModelCatalog.Models[0].Display, output);
    }
}
