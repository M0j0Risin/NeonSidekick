using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Comfy;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// Test (2026-11-25, the workflow manager): a draft, an Edit or an installed workflow run once on the server from a page that keeps its prompt,
/// its input pictures and its size for the visit, the picture saved under the temp folder's <c>comfy-test</c> and shown in the picture viewer;
/// and <c>ComfyUI test connection</c>. A server call runs as a task beside the pane (<see cref="ComfyBusyAsync{T}"/>), so Esc cancels it.
/// </summary>
internal sealed partial class SettingsMenu
{
    public const string ComfyTestRunRow = "Run the test";
    public const string ComfyTestPromptLabel = "Prompt";
    public const string ComfyTestSizeLabel = "Size";
    public static string ComfyTestPictureLabel(int slot) => "Picture " + Invariant(slot + 1);
    public static readonly string ComfyTestSmall = "small (" + Invariant(ComfyWizardTestSide) + ", " + Invariant(ComfyWizardTestSteps) + " steps)";
    public const string ComfyTestAsSaved = "as saved";
    public const string ComfyTestBlankPicture = "(a blank gray picture)";
    public const string ComfyTestPromptQuestion = "The prompt the test runs (empty for the default).";
    public const string ComfyTestPictureQuestion = "A picture's full path for this slot (empty for a blank gray one).";
    public const string ComfyTestCaption = "Runs once on the server with seed 1, so two tests compare; nothing is saved to the workflow.";
    public const string ComfyBusyKeys = "Esc = cancel";
    public const string ComfyTestCancelledNotice = "Test canceled.";

    /// <summary>
    /// The small test's size (2026-11-26, the review's #82): both sides scaled by the same factor so the longer is at most
    /// <see cref="ComfyWizardTestSide"/>, the shape kept, each rounded to 8 and at least <see cref="ComfyStudio.MinSide"/>. Pure.
    /// </summary>
    public static (int Width, int Height) ComfyTestSize(int width, int height)
    {
        double scale = Math.Min(1.0, (double)ComfyWizardTestSide / Math.Max(1, Math.Max(width, height)));
        int Side(int side) => Math.Max(ComfyStudio.MinSide, (int)Math.Round(side * scale / 8.0, MidpointRounding.AwayFromZero) * 8);
        return (Side(width), Side(height));
    }
    public const string ComfyTestConnectionLabel = "Enter to ask the server for its version";
    public const string ComfyAskingLabel = "Asking the server…";

    public static string ComfyTestingLabel(string name) => $"Testing '{name}' on the server…";

    public static string ComfyTestOkNotice(string name, double seconds, string path, string? where) =>
        $"Tested '{name}' in {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s: the picture is {path}" + (where is null ? "." : $", {where}.");

    public static string ComfyTestNoPictureNotice(string name, double seconds, IReadOnlyList<string> files) =>
        $"Tested '{name}' in {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s: ComfyUI made " + (files.Count == 0 ? "no file" : string.Join(", ", files)) + ", no picture to show.";

    public static string ComfyTestShowFailed(string path, string why) => $"The picture is {path}, but it could not be shown: {why}.";

    public static string ComfyTestPictureError(string path, string why) => $"'{path}' {why}.";

    /// <summary>What a test runs with, kept for the visit and shared by every workflow tested in it.</summary>
    private sealed class ComfyTestSetup
    {
        public string Prompt { get; set; } = ComfyWizardTestPrompt;

        public string[] Pictures { get; } = ["", "", ""];

        public bool Small { get; set; } = true;
    }

    private ComfyTestSetup _comfyTestSetup = new();

    /// <summary>Shows a test picture where a double-click would (ChatScreen sets it): the words saying where it went. Throws when the launch fails.</summary>
    public Func<string, string>? ShowPicture { get; set; }

    /// <summary>A test picture written (ChatScreen sets it so a private conversation's end asks about it too).</summary>
    public Action<string>? PictureMade { get; set; }

    /// <summary>Where test pictures go: the temp pictures folder's <c>comfy-test</c> (settable, so the suite never clears the user's).</summary>
    public string ComfyTestFolder { get; set; } = Path.Combine(ChatScreen.PictureTempFolder, "comfy-test");

    /// <summary>How long a server call runs before its busy page shows (a fast answer never flickers or eats typed-ahead keys).</summary>
    public TimeSpan ComfyBusyGrace { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The test page over <paramref name="workflows"/> (both of <c>both</c>, run in turn; a failure or Esc stops there).</summary>
    private async Task TestComfyAsync(IReadOnlyList<ComfyWorkflow> workflows, string label, CancellationToken cancellationToken)
    {
        if (workflows.Count == 0)
        {
            return;
        }

        int slots = workflows.Max(w => w.ImageCount);
        string title = Crumb(FieldName(SettingsField.ComfyWorkflowsOffered) + " › " + label + " › " + ComfyTestAction);
        int cursor = 0;
        while (true)
        {
            var setup = _comfyTestSetup;
            var labels = new List<string> { ComfyTestPromptLabel };
            var values = new List<string> { setup.Prompt };
            for (int i = 0; i < slots; i++)
            {
                labels.Add(ComfyTestPictureLabel(i));
                values.Add(setup.Pictures[i].Length == 0 ? ComfyTestBlankPicture : setup.Pictures[i]);
            }

            labels.Add(ComfyTestSizeLabel);
            values.Add(setup.Small ? ComfyTestSmall : ComfyTestAsSaved);
            int width = labels.Max(l => l.Length) + 2;
            var ruled = RuledRows.Of([[Markup.Escape(ComfyTestRunRow)], labels.Select((l, i) => Markup.Escape(l.PadRight(width) + values[i])).ToList()]);
            var page = new MenuPage(title, ruled.Rows, PickKeys) { Headings = ruled.Headings, Caption = ComfyTestCaption };
            if (await PickAsync(page, ruled.RowOf(cursor), cancellationToken).ConfigureAwait(false) is not { } row)
            {
                return;
            }

            int item = ruled.ItemOf(row);
            if (item < 0)
            {
                continue;
            }

            cursor = item;
            if (item == 0)
            {
                await ComfyTestRunAllAsync(workflows, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (item == 1)
            {
                if (await ComfyAskTextAsync(page, row, ComfyTestPromptQuestion, setup.Prompt, allowEmpty: true, cancellationToken).ConfigureAwait(false) is { } typed)
                {
                    setup.Prompt = typed.Trim().Length == 0 ? ComfyWizardTestPrompt : typed.Trim();
                }

                continue;
            }

            if (item == labels.Count)
            {
                setup.Small = !setup.Small;
                continue;
            }

            int slot = item - 2;
            string initial = setup.Pictures[slot];
            while (await ComfyAskTextAsync(page, row, ComfyTestPictureQuestion, initial, allowEmpty: true, cancellationToken).ConfigureAwait(false) is { } path)
            {
                string full = UnquotePath(path.Trim());
                if (full.Length == 0 || ComfyTestPicture(full).Error is not { } error)
                {
                    setup.Pictures[slot] = full;
                    break;
                }

                Sink.Error(error);
                initial = path;
            }
        }
    }

    /// <summary>Each workflow run in turn; a failure or a cancel stops there.</summary>
    private async Task ComfyTestRunAllAsync(IReadOnlyList<ComfyWorkflow> workflows, CancellationToken cancellationToken)
    {
        if (_comfyClient() is not { } client)
        {
            Sink.Error(ComfyText.NoServer);
            return;
        }

        foreach (var workflow in workflows)
        {
            if (!await ComfyTestOneAsync(client, workflow, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// A test picture's bytes and extension, or why not: <paramref name="path"/> (unquoted already) read under the input rules (no unknown
    /// computer, a picture file, at most <see cref="ImageFile.MaxFileBytes"/>); empty for a blank gray one.
    /// </summary>
    private static (byte[]? Bytes, string Extension, string? Error) ComfyTestPicture(string path)
    {
        if (path.Length == 0)
        {
            return (SmokeChecks.SolidBmp(ComfyWizardTestSide, ComfyWizardTestSide), ".bmp", null);
        }

        var (bytes, why) = ComfyReadTypedFile(path, ImageFile.MaxFileBytes, p => ImageFile.IsImagePath(p) ? null : ComfyText.PathNotAPicture);
        return bytes is null ? (null, "", ComfyTestPictureError(path, why!)) : (bytes, Path.GetExtension(path).ToLowerInvariant(), null);
    }

    /// <summary>One workflow run with the setup's values; its picture kept and shown. True when it ran.</summary>
    private async Task<bool> ComfyTestOneAsync(ComfyClient client, ComfyWorkflow workflow, CancellationToken cancellationToken)
    {
        var setup = _comfyTestSetup;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.Current.ComfyTimeoutSeconds, AppSettingsData.MinComfyTimeoutSeconds, AppSettingsData.MaxComfyTimeoutSeconds));
        var clock = Stopwatch.StartNew();
        var (done, run) = await ComfyBusyAsync(ComfyTestingLabel(workflow.Name), async token =>
        {
            List<string>? images = null;
            if (workflow.TakesImage)
            {
                images = [];
                for (int i = 0; i < workflow.ImageCount; i++)
                {
                    var (bytes, extension, error) = ComfyTestPicture(setup.Pictures[i]);
                    if (bytes is null)
                    {
                        return ComfyRunResult.Failed(error!);
                    }

                    // Its own name, never the picture's: the upload overwrites, and two slots may share a name.
                    var (name, refused) = await client.UploadAsync(bytes, "neon-test-" + Invariant(i + 1) + extension, timeout, token).ConfigureAwait(false);
                    if (name is null)
                    {
                        return ComfyRunResult.Failed(refused!);
                    }

                    images.Add(name);
                }
            }

            var d = workflow.Defaults;
            bool small = setup.Small;
            var (smallWidth, smallHeight) = ComfyTestSize(d.Width, d.Height);
            var values = new ComfyValues(ComfyStudio.JoinPositive(workflow.TriggerWords, setup.Prompt), d.Negative, 1,   // the trigger words, as every run (2026-11-26)
                small ? smallWidth : d.Width,
                small ? smallHeight : d.Height,
                small ? Math.Min(d.Steps, ComfyWizardTestSteps) : d.Steps,
                null, null, images);
            return await client.RunAsync(workflow.Fill(values), timeout, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (!done)
        {
            DiagnosticLog.Info(ComfyWorkflowFile.Category, ComfyText.LogTested(workflow.Name, "canceled"));
            if (!cancellationToken.IsCancellationRequested)
            {
                Sink.Notice(ComfyTestCancelledNotice);   // the pane's own Esc only (2026-11-26, the review's #81), never the app's cancel
            }

            return false;
        }

        if (!run.Ok)
        {
            DiagnosticLog.Info(ComfyWorkflowFile.Category, ComfyText.LogTested(workflow.Name, run.Error!));
            Sink.Error(run.Error!);
            return false;
        }

        DiagnosticLog.Info(ComfyWorkflowFile.Category, ComfyText.LogTested(workflow.Name, "ok"));
        ComfyShowTestResult(workflow.Name, clock.Elapsed.TotalSeconds, run.Images);
        return true;
    }

    /// <summary>The first output that is a picture written into <see cref="ComfyTestFolder"/> and shown; an output that is no picture is named only.</summary>
    private void ComfyShowTestResult(string name, double seconds, IReadOnlyList<ComfyImage> images)
    {
        foreach (var image in images)
        {
            string? extension = MetadataStripper.ContainerOf(image.Bytes) switch
            {
                StripContainer.Png => ".png",
                StripContainer.Jpeg => ".jpg",
                StripContainer.WebP => ".webp",
                StripContainer.Gif => ".gif",
                _ => null,
            };
            if (extension is null)
            {
                continue;
            }

            if (ComfyKeepTestPicture(name, image.Bytes, extension) is not { } path)
            {
                return;
            }

            string? where = null;
            if (ShowPicture is { } show)
            {
                try
                {
                    where = show(path);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
                {
                    Sink.Error(ComfyTestShowFailed(path, LogText.Excerpt(ex.Message)));
                    return;
                }
            }

            Sink.Notice(ComfyTestOkNotice(name, seconds, path, where));
            return;
        }

        Sink.Notice(ComfyTestNoPictureNotice(name, seconds, images.Select(i => i.FileName).ToList()));
    }

    /// <summary>A test picture written under a new name (the folder may hold the last one); test pictures over a day old cleared first. The path, or null (said).</summary>
    private string? ComfyKeepTestPicture(string name, byte[] bytes, string extension)
    {
        string folder = ComfyTestFolder;
        try
        {
            Directory.CreateDirectory(folder);
            var stale = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (string old in Directory.EnumerateFiles(folder))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(old) < stale)
                    {
                        File.Delete(old);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or locked: the next test tries again.
                }
            }

            string path = Path.Combine(folder, name + "-" + Guid.NewGuid().ToString("N")[..8] + extension);
            File.WriteAllBytes(path, bytes);
            PictureMade?.Invoke(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Sink.Error(ComfyTestPictureError(folder, ex.Message));
            return null;
        }
    }

    /// <summary>
    /// <paramref name="work"/> run as a task beside the pane: the spinner at once, and after <see cref="ComfyBusyGrace"/> a page saying what runs
    /// with <see cref="ComfyBusyKeys"/>. Esc (or a pane dismissed) cancels it and awaits it, unless it just finished: then its result stands.
    /// Without the pane, or with no keyboard to read, it only awaits. (Done, the result); not Done when cancelled.
    /// </summary>
    private async Task<(bool Done, T Value)> ComfyBusyAsync<T>(string label, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var busy = _transcript.BeginBusy(label);
        var task = work(run.Token);
        try
        {
            if (_pane.Enabled && await Task.WhenAny(task, Task.Delay(ComfyBusyGrace, CancellationToken.None)).ConfigureAwait(false) != task)
            {
                using var stale = new CancellationTokenSource();
                _ = task.ContinueWith(_ =>
                {
                    try
                    {
                        stale.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The page already closed.
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                var page = new MenuPage(Crumb(FieldName(SettingsField.ComfyWorkflowsOffered)), [Markup.Escape(label)], ComfyBusyKeys);
                while (!task.IsCompleted)
                {
                    var pick = await _pane.PickAsync(page, 0, cancellationToken, stale: stale.Token).ConfigureAwait(false);
                    if (task.IsCompleted || pick is not null || _pane.ReadDry)
                    {
                        if (_pane.ReadDry)
                        {
                            break;   // no keyboard: only await
                        }

                        continue;
                    }

                    await run.CancelAsync().ConfigureAwait(false);   // Esc
                    break;
                }
            }

            if (run.IsCancellationRequested && !task.IsCompleted)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled, as asked.
                }

                return (false, default!);
            }

            return (true, await task.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            return (false, default!);
        }
    }

    /// <summary><c>ComfyUI test connection</c>: the server asked for its version, the answer or the error said; <see cref="ComfyText.NoServer"/> with no URL.</summary>
    private async Task<bool> TestComfyConnectionAsync(CancellationToken cancellationToken)
    {
        if (_comfyClient() is not { } client)
        {
            Sink.Error(ComfyText.NoServer);
            return false;
        }

        var (ok, text) = await _transcript.WithSpinnerAsync(ComfyAskingLabel, () => client.StatusAsync(cancellationToken)).ConfigureAwait(false);
        DiagnosticLog.Info(ComfyWorkflowFile.Category, ComfyText.LogConnection(ok ? "ok" : text));
        if (ok)
        {
            Sink.Notice(text);
        }
        else
        {
            Sink.Error(text);
        }

        return false;
    }
}
