using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Speech;

namespace NeonSidekick.Updates;

/// <summary>Runs a probe: the seam a test fakes, so no test starts a process.</summary>
public interface IUpdateProbeRunner
{
    /// <summary>Starts <paramref name="exe"/>'s probe and waits. Its exit code, or null when it hadn't ended by <paramref name="limit"/> and was killed and waited for, since its files are locked until then. Throws <see cref="OperationCanceledException"/> the same way, once it's gone.</summary>
    Task<int?> RunAsync(string exe, string resultFile, string stderrFile, TimeSpan limit, CancellationToken cancellationToken);
}

/// <summary>
/// The real probe's process (2026-11-04, self-update), a process-start site by design. Through <see cref="ChildJob.Start(string, IReadOnlyList{string}, string, out bool, out string)"/> on Windows, so it can't outlive the app, with no window and nothing on the app's console. On a Mac a plain
/// process with all three streams redirected, as <c>ShellRunner</c> does, so the child never touches the terminal the app owns.
/// </summary>
public sealed class UpdateProbeProcess : IUpdateProbeRunner
{
    public async Task<int?> RunAsync(string exe, string resultFile, string stderrFile, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var process = Start(exe, resultFile, stderrFile);
        if (process is null)
        {
            return UpdateProbe.Failed;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static Process? Start(string exe, string resultFile, string stderrFile)
    {
        if (OperatingSystem.IsWindows())
        {
            var started = ChildJob.Start(exe, [UpdateProbe.Flag, resultFile], stderrFile, out _, out string error);
            if (started is null)
            {
                DiagnosticLog.Info(UpdateJournal.Category, error);
            }

            return started;
        }

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(UpdateProbe.Flag);
        start.ArgumentList.Add(resultFile);
        try
        {
            var process = Process.Start(start);
            if (process is not null)
            {
                process.StandardInput.Close();
                process.OutputDataReceived += static (_, _) => { };
                process.BeginOutputReadLine();
                process.ErrorDataReceived += (_, line) =>
                {
                    if (line.Data is { } text)
                    {
                        File.AppendAllText(stderrFile, text + "\n");
                    }
                };
                process.BeginErrorReadLine();
            }

            return process;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
            return null;
        }
    }
}

/// <summary>Where a stage has got to, for the marker.</summary>
public abstract record UpdateStagePhase
{
    /// <summary>Coming down: its share of the archive so far.</summary>
    public sealed record Downloading(int Percent) : UpdateStagePhase;

    /// <summary>Verifying and test-running.</summary>
    public sealed record Checking : UpdateStagePhase;
}

/// <summary>How a stage ended: <see cref="Reason"/> null when it's ready and installs at quit.</summary>
public sealed record UpdateStageResult(string? Reason)
{
    public bool Ok => Reason is null;
}

/// <summary>
/// Preflight, download, verification and the probe (2026-11-04, self-update). Staged in <c>.update/staging</c>, on the install's volume because the swap renames files, and over <see cref="ModelStore.EnsureArchiveSetAsync"/>: resume, size and SHA-256 checks, unpacking that refuses paths outside the folder, the
/// package's top folder unwrapped, and tar's exec bits and symlinks kept. Only after the probe passes is <c>ready</c> written, and only a stage with one is ever swapped. <c>ready</c> is deleted first whenever a stage is started, replaced or dropped, because the recheck vouches for the files and not for the
/// probe, so a leftover one would let a signed stage swap unprobed.
/// </summary>
public sealed class UpdateStage
{
    /// <summary>A download with no new bytes for this long fails, since <see cref="ModelStore"/> has no read timeout and a connection that stalls silently would leave the marker frozen.</summary>
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    /// <summary>The probe's limit; a fresh exe's first start took 0.12 s here, so this is roomy.</summary>
    public static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(30);

    /// <summary>Free space wanted, as multiples of the archive: the archive and the unpacked copy while <c>previous</c> still exists.</summary>
    public const int SpaceFactor = 4;

    private readonly UpdateContext _context;
    private readonly ModelStore _store;
    private readonly UpdateCheck _check;
    private readonly IUpdateProbeRunner _probe;
    private readonly TimeProvider _time;
    private readonly Func<string, long?> _freeBytes;

    public UpdateStage(UpdateContext context, ModelStore store, UpdateCheck check, IUpdateProbeRunner probe, TimeProvider? time = null, Func<string, long?>? freeBytes = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _time = time ?? TimeProvider.System;
        _freeBytes = freeBytes ?? ModelStore.FreeBytesOn;
    }

    private UpdateFolder Folder => _context.Folder;

    /// <summary>Drops a stage: <c>ready</c> first, then the files. Partial downloads stay unless <paramref name="alsoParts"/>, so a failed stage's next try resumes and <c>/update cancel</c> doesn't.</summary>
    public static void Drop(UpdateFolder folder, bool alsoParts = false)
    {
        ArgumentNullException.ThrowIfNull(folder);
        TryDo(() => File.Delete(folder.Ready));
        WorkingDirectory.DeleteDirectoryQuietly(folder.Staging);
        WorkingDirectory.DeleteDirectoryQuietly(folder.Staged);
        if (alsoParts)
        {
            WorkingDirectory.DeleteDirectoryQuietly(folder.StagingParts);
        }
    }

    /// <summary>The temporary folders a killed run left: <see cref="ModelStore"/> only cleans its own within a run. Only while holding the staging lock, so it never takes a folder another copy is unpacking into.</summary>
    public static void RemoveExtracting(UpdateFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder.Root))
        {
            return;
        }

        foreach (string leftover in Directory.EnumerateDirectories(folder.Root, "*.extracting"))
        {
            WorkingDirectory.DeleteDirectoryQuietly(leftover);
        }
    }

    /// <summary>
    /// The refusals before anything starts: no package manifest, a folder that can't take files, not enough room, a renamed copy. Null when it may start. The lock is taken by <see cref="StageAsync"/>, since a refusal here is a reason and that one's a state.
    /// </summary>
    public string? Preflight(long archiveBytes)
    {
        if (!Folder.IsPackage)
        {
            return UpdateText.NotAPackage;
        }

        var installed = UpdateManifest.ReadFile(Folder.Manifest, out string? problem);
        if (installed is null)
        {
            return UpdateText.DamagedInstall(problem!);
        }

        if (!_context.IsListedExe(installed))
        {
            return UpdateText.RenamedCopy(installed.Exe);
        }

        // Really creating and renaming a file, which also catches synced or network folders that refuse it.
        try
        {
            Directory.CreateDirectory(Folder.Root);
            string first = Path.Combine(Folder.Root, "preflight.tmp");
            string second = Path.Combine(Folder.Root, "preflight2.tmp");
            File.WriteAllText(first, "");
            File.Move(first, second, overwrite: true);
            File.Delete(second);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateText.ReadOnlyFolder(ex.Message);
        }

        // Plus the download's own margin, so there's one refusal, before anything starts. Space it can't read means going on: the rename test ran, and a full disk fails the download with its reason.
        long needed = (archiveBytes * SpaceFactor) + ModelStore.DiskHeadroomBytes;
        return _freeBytes(Folder.Install) is { } free && free < needed ? ModelStore.DiskSpaceError(UpdateText.StageDisplay, needed - ModelStore.DiskHeadroomBytes, free, Folder.Install) : null;
    }

    /// <summary>The whole flow, for one release. Holds the staging lock throughout. Throws <see cref="OperationCanceledException"/> on the caller's cancel, with the partial download kept and a running probe gone.</summary>
    public async Task<UpdateStageResult> StageAsync(UpdateRelease release, IProgress<UpdateStagePhase>? progress, string? rawNotes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        string? refused = Preflight(release.ArchiveBytes);
        if (refused is not null)
        {
            return new UpdateStageResult(refused);
        }

        using var held = UpdateLock.TryExclusive(Folder.StagingLock);
        if (held is null)
        {
            return new UpdateStageResult(UpdateText.AnotherCopyStaging);
        }

        Prepare(release);
        byte[] shaBytes;
        byte[] sigBytes;
        try
        {
            shaBytes = await _check.FetchAsync(release.ShaUrl, UpdateCheck.SignedFileCap, cancellationToken).ConfigureAwait(false);
            sigBytes = await _check.FetchAsync(release.SigUrl, UpdateCheck.SignedFileCap, cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateFetchException ex)
        {
            return new UpdateStageResult(ex.Message);
        }

        if (!UpdateSignature.Verify(shaBytes, Encoding.ASCII.GetString(sigBytes), _context.Keys))
        {
            return new UpdateStageResult(UpdateText.NotSigned);
        }

        var sha = UpdateShaFile.TryParse(Encoding.UTF8.GetString(shaBytes));
        if (sha is null || sha.ArchiveName != release.ArchiveName)
        {
            // The signed text must name the exact expected asset, so an older signed release can't be replayed as new.
            return new UpdateStageResult(UpdateText.NotThisRelease(release.ArchiveName));
        }

        string? failed = await DownloadAsync(release, sha, progress, cancellationToken).ConfigureAwait(false);
        if (failed is not null)
        {
            return new UpdateStageResult(failed);
        }

        progress?.Report(new UpdateStagePhase.Checking());
        try
        {
            Directory.CreateDirectory(Folder.Staged);
            await File.WriteAllBytesAsync(Folder.StagedSha, shaBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Folder.StagedSig, sigBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Folder.StagedNotes, UntrustedText.Scrub(rawNotes ?? ""), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpdateStageResult(ex.Message);
        }

        string? bad = await Task.Run(() => Verify(release, sha, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (bad is null)
        {
            bad = await ProbeAsync(release, cancellationToken).ConfigureAwait(false);
        }

        if (bad is not null)
        {
            Drop(Folder);
            return new UpdateStageResult(bad);
        }

        File.WriteAllText(Folder.Ready, "");
        return new UpdateStageResult(null);
    }

    // A stage is started: ready goes first. A complete folder of this version stays, to be verified and probed again rather than downloaded again, since EnsureArchiveSetAsync answers "present" for any complete folder
    // without looking at its version; another version's goes, as does a partial download of another version, since staging.parts is named per archive and only a success would clear it.
    private void Prepare(UpdateRelease release)
    {
        TryDo(() => File.Delete(Folder.Ready));
        RemoveExtracting(Folder);
        WorkingDirectory.DeleteDirectoryQuietly(Folder.Staged);
        if (Directory.Exists(Folder.Staging)
            && (UpdateManifest.ReadFile(Folder.StagingManifest, out _) is not { } staged || staged.ParsedVersion != release.Version))
        {
            WorkingDirectory.DeleteDirectoryQuietly(Folder.Staging);
        }

        if (Directory.Exists(Folder.StagingParts))
        {
            foreach (string part in Directory.EnumerateFileSystemEntries(Folder.StagingParts))
            {
                if (!Path.GetFileName(part).StartsWith(release.ArchiveName, StringComparison.Ordinal))
                {
                    TryDo(() =>
                    {
                        if (Directory.Exists(part))
                        {
                            Directory.Delete(part, recursive: true);
                        }
                        else
                        {
                            File.Delete(part);
                        }
                    });
                }
            }
        }
    }

    private async Task<string?> DownloadAsync(UpdateRelease release, UpdateShaFile sha, IProgress<UpdateStagePhase>? progress, CancellationToken cancellationToken)
    {
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watch = new StallWatch(_time);
        var spec = new ArchiveSetSpec(
            UpdateText.StageDisplay,
            Folder.Staging,
            [new ArchivePart(release.ArchiveName, release.ArchiveUrl, release.ArchiveBytes, sha.ArchiveHash)],
            [UpdateManifest.FileName]);
        var report = new InlineProgress(value =>
        {
            watch.Saw(value.Received);
            if (value.Total is > 0 and { } total)
            {
                progress?.Report(new UpdateStagePhase.Downloading((int)Math.Clamp(value.Received * 100 / total, 0, 100)));
            }
        });

        // The watch ends at the verifying callback, since hashing and unpacking report no bytes and a slow disk must not read as a stall.
        void Verifying()
        {
            watch.Stop();
            progress?.Report(new UpdateStagePhase.Checking());
        }

        var watching = watch.RunAsync(stalled, StallLimit);
        try
        {
            var result = await _store.EnsureArchiveSetAsync(spec, report, Verifying, Verifying, stalled.Token).ConfigureAwait(false);
            return result.Ok ? null : result.Detail;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && stalled.IsCancellationRequested)
        {
            return UpdateText.Stalled;
        }
        finally
        {
            watch.Stop();
            await watching.ConfigureAwait(false);
        }
    }

    private string? Verify(UpdateRelease release, UpdateShaFile sha, CancellationToken cancellationToken)
    {
        try
        {
            if (!UpdateHash.Same(UpdateHash.OfFile(Folder.StagingManifest), sha.ManifestHash))
            {
                return UpdateText.PackageBad(UpdateManifest.FileName);
            }

            var staged = UpdateManifest.ReadFile(Folder.StagingManifest, out string? problem);
            if (staged is null)
            {
                return UpdateText.PackageRefused(problem!);
            }

            var installed = UpdateManifest.ReadFile(Folder.Manifest, out problem);
            if (installed is null)
            {
                return UpdateText.DamagedInstall(problem!);
            }

            if (staged.ParsedVersion != release.Version || !string.Equals(staged.Rid, installed.Rid, StringComparison.Ordinal))
            {
                return UpdateText.PackageRefused(UpdateText.ManifestNotThisRelease(staged.Version, staged.Rid));
            }

            foreach (var file in staged.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string full = Path.Combine(Folder.Staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full) || !UpdateHash.Same(UpdateHash.OfFile(full), file.Sha256))
                {
                    return UpdateText.PackageBad(file.Path);
                }
            }

            // Hashing the whole install happens here, in the background, and the result is kept, so a window close only hashes what moves.
            var hashes = UpdateInstallHashes.Take(Folder.Install, installed, UpdateHash.OfFile(Folder.Manifest));
            File.WriteAllBytes(Folder.StagedInstallHashes, JsonSerializer.SerializeToUtf8Bytes(hashes, UpdateJsonContext.Default.UpdateInstallHashes));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    // The machine's fault isn't the release's: the app chats without the Visual C++ runtime, so refusing on a library that won't load here either would refuse every update and blame antivirus. So a failed load or a crash asks
    // the installed exe's own probe, and a library failing there too is logged and ignored. A crash ends that run, so the libraries after it go unloaded; their files have still passed the hash check. A missing file, a library new in this release,
    // or any failure at the package step still refuses.
    private async Task<string?> ProbeAsync(UpdateRelease release, CancellationToken cancellationToken)
    {
        var staged = UpdateManifest.ReadFile(Folder.StagingManifest, out _)!;
        string stagedExe = Path.Combine(Folder.Staging, staged.Exe);
        int? code = await RunProbeAsync(stagedExe, Folder.ProbeResult, cancellationToken).ConfigureAwait(false);
        if (code is null)
        {
            return UpdateText.ProbeTimedOut;
        }

        var result = UpdateProbeResult.ParseFile(Folder.ProbeResult);
        if (result.Version != release.Version.ToString())
        {
            return UpdateText.ProbeFailed;
        }

        if (result.Missing.Count > 0)
        {
            return UpdateText.PackageBad(result.Missing[0]);
        }

        var suspects = result.Suspects.ToList();
        if (suspects.Count == 0)
        {
            return code == UpdateProbe.Passed && result.Finished ? null : UpdateText.ProbeFailed;
        }

        var installed = UpdateManifest.ReadFile(Folder.Manifest, out _);
        if (installed is null)
        {
            return UpdateText.ProbeFailedOn(suspects[0]);
        }

        int? installedCode = await RunProbeAsync(Path.Combine(Folder.Install, installed.Exe), Folder.ProbeResultInstalled, cancellationToken).ConfigureAwait(false);
        var alsoFails = installedCode is null ? [] : new HashSet<string>(UpdateProbeResult.ParseFile(Folder.ProbeResultInstalled).Suspects, _context.Comparer);
        foreach (string library in suspects)
        {
            if (!alsoFails.Contains(library))
            {
                return UpdateText.ProbeFailedOn(library);
            }

            DiagnosticLog.Info(UpdateJournal.Category, UpdateText.LibraryFailsInBoth(library));
        }

        return null;
    }

    private async Task<int?> RunProbeAsync(string exe, string resultFile, CancellationToken cancellationToken)
    {
        // Each run deletes its own result first, so a crash can't be read as an older run's answer.
        TryDo(() => File.Delete(resultFile));
        int? code = await _probe.RunAsync(exe, resultFile, Folder.ProbeStderr, ProbeLimit, cancellationToken).ConfigureAwait(false);
        if (code != UpdateProbe.Passed)
        {
            TryDo(() =>
            {
                if (File.Exists(Folder.ProbeStderr))
                {
                    DiagnosticLog.Info(UpdateJournal.Category, UntrustedText.Scrub(File.ReadAllText(Folder.ProbeStderr)));
                }
            });
        }

        return code;
    }

    private static void TryDo(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
        }
    }

    // ModelStore's progress is reported inline: Progress<T> would post to a context, and the stall's clock must see each report.
    private sealed class InlineProgress(Action<(long Received, long? Total)> report) : IProgress<(long Received, long? Total)>
    {
        public void Report((long Received, long? Total) value) => report(value);
    }

    private sealed class StallWatch(TimeProvider time)
    {
        private readonly CancellationTokenSource _stop = new();
        private long _received = -1;
        private long _lastChange = time.GetTimestamp();

        public void Saw(long received)
        {
            if (Interlocked.Exchange(ref _received, received) != received)
            {
                Interlocked.Exchange(ref _lastChange, time.GetTimestamp());
            }
        }

        public void Stop() => _stop.Cancel();

        public async Task RunAsync(CancellationTokenSource stall, TimeSpan limit)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), time, _stop.Token).ConfigureAwait(false);
                    if (time.GetElapsedTime(Interlocked.Read(ref _lastChange)) >= limit)
                    {
                        await stall.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped: the download finished or moved on.
            }
            catch (ObjectDisposedException)
            {
                // The download's source went first.
            }
        }
    }
}
