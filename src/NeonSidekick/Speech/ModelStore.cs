using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.Speech;

/// <summary>How a model file is recognised on disk (<see cref="ModelStore.LooksLike"/>).</summary>
public enum ModelFormat
{
    /// <summary>A ggml file (Whisper, Silero): the four-byte magic.</summary>
    Ggml,

    /// <summary>An ONNX file (Kokoro): a protobuf whose first fields are <c>ir_version</c> and <c>producer_name</c>.</summary>
    Onnx,

    /// <summary>A GGUF file (the embedded LLM's weights and vision projector, 2026-09-29): the four-byte magic <c>GGUF</c>.</summary>
    Gguf,

    /// <summary>A zip archive (one part of an <see cref="ArchiveSetSpec"/>, 2026-09-29): the embedded-header magic.</summary>
    Zip,
}

/// <summary>
/// One model file: what to call it, where it lives, where to fetch it from (null = must already exist), roughly how
/// big it is, and how to recognise it. <paramref name="Sha256"/> and <paramref name="Resumable"/> (2026-09-29, the
/// embedded LLM's multi-gigabyte files) pin the file's digest — <paramref name="ApproxBytes"/> is then its exact size —
/// and download it through a stable <c>.partial</c> file that a cancelled or broken download keeps, so the next
/// ensure asks for the rest with a <c>Range</c> header instead of starting over. The voice models leave both off
/// and keep the throwaway <c>.tmp</c> of before.
/// </summary>
public sealed record ModelSpec(string Display, string Path, Uri? DownloadUrl, long ApproxBytes, ModelFormat Format = ModelFormat.Ggml, string? Sha256 = null, bool Resumable = false);

/// <summary>One archive of an <see cref="ArchiveSetSpec"/>: its file name, where to fetch it, its exact size and SHA-256.</summary>
public sealed record ArchivePart(string Name, Uri Url, long Bytes, string Sha256);

/// <summary>
/// A directory unpacked from several zip archives into one folder (2026-09-29: llama.cpp's CUDA build ships its
/// runtime DLLs in a second zip): what to call it, the directory it must end up in, the archives, and the relative
/// files whose presence means the directory is complete.
/// </summary>
public sealed record ArchiveSetSpec(string Display, string Path, IReadOnlyList<ArchivePart> Parts, IReadOnlyList<string> RequiredFiles);

/// <summary>
/// One model shipped as a zipped directory (Vosk): what to call it, the directory it must end up
/// in, the archive to fetch, roughly how big the archive is, and the relative files whose presence
/// means the directory is complete.
/// </summary>
public sealed record ModelDirectorySpec(string Display, string Path, Uri ArchiveUrl, long ApproxBytes, IReadOnlyList<string> RequiredFiles);

/// <summary>What <see cref="ModelStore.EnsureAsync"/> found or fetched.</summary>
public readonly record struct ModelResult(bool Ok, string Path, string Detail)
{
    public static ModelResult Failed(string path, string detail) => new(false, path, detail);
}

/// <summary>
/// Resolves the ggml model files voice input needs (a Whisper model and the Silero VAD model)
/// under the settings directory, and downloads what is missing. The resolution half is static
/// because the settings menu validates the <c>SttWhisperModel</c> field with it.
///
/// <para>Whisper.net's own mirror on Hugging Face serves both files under one tree (ggerganov's
/// repository has the Whisper models but no Silero file), so one host, one constant per path.
/// If the mirror moves, only these constants change; an operator can also drop a file in place
/// and no HTTP happens.</para>
///
/// <para><b>Downloads land on a temporary file and are moved into place only when complete and
/// verified.</b> <c>.tmp</c> + length check + ggml magic + <c>File.Move(overwrite: true)</c>, and a present
/// file that fails the magic check is treated as absent.</para>
///
/// <para>The Vosk wake-word model (M5) is a zipped directory, not a file: the archive lands on the
/// same kind of temporary file, is checked for the zip magic, unpacked next to its destination,
/// verified against <see cref="VoskRequiredFiles"/> and only then moved into place; a present
/// directory missing any of those files is treated as absent. Vosk itself never sees an
/// incomplete directory, which matters because a failed model load returns a null handle that
/// the next native call dereferences, aborting the process with nothing to catch.</para>
/// </summary>
public sealed class ModelStore
{
    private const int CopyBufferBytes = 64 * 1024;

    /// <summary>What a resumable download leaves free on its drive beyond the file itself (2026-09-29): a model that fills the disk to the last byte leaves Windows and the app no room to breathe.</summary>
    public const long DiskHeadroomBytes = 1_000_000_000;

    /// <summary>Every ggml file starts with these four bytes ("lmgg", i.e. "ggml" little-endian).</summary>
    private static readonly byte[] GgmlMagic = { 0x6C, 0x6D, 0x67, 0x67 };

    /// <summary>Every GGUF file starts with these four bytes: "GGUF".</summary>
    private static readonly byte[] GgufMagic = { 0x47, 0x47, 0x55, 0x46 };

    /// <summary>Every zip archive starts with a local file header: "PK\x03\x04".</summary>
    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };

    private readonly HttpClient _http;
    private readonly string _category;

    /// <param name="modelsDirectory">Where named models are stored; created on first download.</param>
    /// <param name="http">The client downloads go through; the app's default has no timeout, a stub in tests.</param>
    /// <param name="category">The <see cref="DiagnosticLog"/> category its lines go under: <c>Voice</c> for the speech models, <c>EmbeddedLlm</c> for the embedded LLM's (2026-09-29).</param>
    public ModelStore(string modelsDirectory, HttpClient http, string category = "Voice")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        Directory = modelsDirectory;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _category = category;
    }

    public string Directory { get; }

    /// <summary>
    /// The free bytes on the drive holding a directory, or null when that cannot be told (a UNC path, a drive gone
    /// away). A seam so the tests can fake a full disk; the default asks <see cref="DriveInfo"/>.
    /// </summary>
    internal Func<string, long?> AvailableBytes { get; init; } = FreeBytesOn;

    /// <summary><see cref="ResolveWhisper(string, string)"/> over this store's directory.</summary>
    public ModelSpec? Whisper(string setting) => ResolveWhisper(setting, Directory);

    /// <summary>The Silero VAD model under this store's directory.</summary>
    public ModelSpec Silero() => new("silero vad", System.IO.Path.Combine(Directory, SileroFileName), new Uri(SileroUrl), SileroBytes);

    /// <summary>The Kokoro TTS model (in-process speech output) under this store's directory.</summary>
    public ModelSpec Kokoro() => new(KokoroFileName, System.IO.Path.Combine(Directory, KokoroFileName), new Uri(KokoroModelUrl), KokoroBytes, ModelFormat.Onnx);

    /// <summary>
    /// Makes sure <paramref name="spec"/> is on disk: a present, valid file costs nothing; a
    /// missing (or invalid) one is downloaded when the spec has a URL. Returns a result, never
    /// throws, except the caller's own cancellation (the temporary file is removed first).
    /// </summary>
    public Task<ModelResult> EnsureAsync(ModelSpec spec, IProgress<(long Received, long? Total)>? progress, CancellationToken cancellationToken) =>
        EnsureAsync(spec, progress, verifying: null, cancellationToken);

    /// <summary>
    /// <see cref="EnsureAsync(ModelSpec, IProgress{ValueTuple{long, Nullable{long}}}?, CancellationToken)"/> with a
    /// word for the pause after a pinned download (<see cref="ModelSpec.Sha256"/>): <paramref name="verifying"/> is
    /// told before the file is hashed, which takes seconds for a few gigabytes.
    /// </summary>
    public async Task<ModelResult> EnsureAsync(ModelSpec spec, IProgress<(long Received, long? Total)>? progress, Action? verifying, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        string path = spec.Path;

        if (File.Exists(path))
        {
            // A pinned file is only ever moved into place after its hash matched, so its length and magic are proof
            // enough; re-hashing gigabytes on every launch is not worth it (2026-09-29).
            if (LooksLike(path, spec.Format) && (spec.Sha256 is null || new FileInfo(path).Length == spec.ApproxBytes))
            {
                return new ModelResult(true, path, "present");
            }

            // A zero-length or foreign file is the fingerprint of an interrupted download or a
            // wrong drop-in; clearing it beats honouring it.
            DiagnosticLog.Warn(_category, $"Discarding {path}: not {FormatName(spec.Format)} model.");
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                return ModelResult.Failed(path, $"cannot replace {path}: {ex.Message}");
            }
        }

        if (spec.DownloadUrl is not { } url)
        {
            return ModelResult.Failed(path, $"not found: {path}");
        }

        if (spec.Resumable)
        {
            return await EnsureResumableAsync(spec, url, progress, verifying, cancellationToken).ConfigureAwait(false);
        }

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var download = await DownloadAsync(spec.Display, url, path, tempPath, progress, cancellationToken).ConfigureAwait(false);
            if (!download.Ok)
            {
                return ModelResult.Failed(path, download.Detail);
            }

            if (!LooksLike(tempPath, spec.Format))
            {
                return ModelResult.Failed(path, $"the download from {url} is not {FormatName(spec.Format)} model");
            }

            File.Move(tempPath, path, overwrite: true);
            DiagnosticLog.Info(_category, $"Downloaded {spec.Display} ({SizeLabel(download.Received)}) to {path}.");
            return new ModelResult(true, path, $"downloaded {SizeLabel(download.Received)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ModelResult.Failed(path, Assistant.Explain(ex));
        }
        finally
        {
            DeleteFileQuietly(tempPath);
        }
    }

    /// <summary>
    /// Makes sure the zipped model directory <paramref name="spec"/> is on disk and complete: a
    /// present directory holding every required file costs nothing; otherwise the archive is
    /// downloaded (progress through <paramref name="progress"/>), unpacked next to the destination
    /// (<paramref name="unpacking"/> is told when that starts), verified and moved into place.
    /// Returns a result, never throws, except the caller's own cancellation (leftovers removed first).
    /// </summary>
    public async Task<ModelResult> EnsureDirectoryAsync(ModelDirectorySpec spec, IProgress<(long Received, long? Total)>? progress, Action? unpacking, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        string path = spec.Path;

        if (System.IO.Directory.Exists(path))
        {
            if (IsCompleteModelDirectory(path, spec.RequiredFiles))
            {
                return new ModelResult(true, path, "present");
            }

            // Half a model is the fingerprint of an interrupted unpack or a wrong drop-in.
            DiagnosticLog.Warn(_category, $"Discarding {path}: not a complete {spec.Display}.");
            try
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                return ModelResult.Failed(path, $"cannot replace {path}: {ex.Message}");
            }
        }

        var url = spec.ArchiveUrl;
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        string extractPath = $"{path}.{Guid.NewGuid():N}.extracting";
        try
        {
            var download = await DownloadAsync(spec.Display, url, path, tempPath, progress, cancellationToken).ConfigureAwait(false);
            if (!download.Ok)
            {
                return ModelResult.Failed(path, download.Detail);
            }

            if (!LooksLikeZip(tempPath))
            {
                return ModelResult.Failed(path, $"the download from {url} is not a zip archive");
            }

            unpacking?.Invoke();
            DiagnosticLog.Info(_category, $"Unpacking {spec.Display} into {extractPath}.");
            await Task.Run(() => ZipFile.ExtractToDirectory(tempPath, extractPath), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string? root = FindModelRoot(extractPath, spec.RequiredFiles);
            if (root is null)
            {
                return ModelResult.Failed(path, $"the archive from {url} does not contain a {spec.Display} (no {MissingFileHint(extractPath, spec.RequiredFiles)})");
            }

            System.IO.Directory.Move(root, path);
            DiagnosticLog.Info(_category, $"Downloaded {spec.Display} ({SizeLabel(download.Received)}) to {path}.");
            return new ModelResult(true, path, $"downloaded {SizeLabel(download.Received)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ModelResult.Failed(path, Assistant.Explain(ex));
        }
        finally
        {
            DeleteFileQuietly(tempPath);
            DeleteDirectoryQuietly(extractPath);
        }
    }

    /// <summary>
    /// The resumable half of <see cref="EnsureAsync(ModelSpec, IProgress{ValueTuple{long, Nullable{long}}}?, Action?, CancellationToken)"/>
    /// (2026-09-29, the embedded LLM's 3–5 GB files): the bytes land on <see cref="PartialPath"/>, which a cancel or a
    /// broken connection keeps, and the next ensure asks for the rest with <c>Range: bytes=&lt;held&gt;-</c> — a 206
    /// appends, a 200 (a server that ignores ranges) starts over, a 416 means the partial file is already whole.
    /// Before a byte is fetched the drive must hold what is left plus <see cref="DiskHeadroomBytes"/>. The finished
    /// file is checked for its exact length, its magic and — when pinned — its SHA-256 (<paramref name="verifying"/>
    /// is told first), and only then moved into place; a file that fails a check is deleted, since resuming it could
    /// only fail again.
    /// </summary>
    private async Task<ModelResult> EnsureResumableAsync(ModelSpec spec, Uri url, IProgress<(long Received, long? Total)>? progress, Action? verifying, CancellationToken cancellationToken)
    {
        string path = spec.Path;
        string partial = PartialPath(path);
        try
        {
            string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
            System.IO.Directory.CreateDirectory(directory);

            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (spec.ApproxBytes > 0 && have > spec.ApproxBytes)
            {
                DiagnosticLog.Warn(_category, $"Discarding {partial}: longer than {spec.Display}.");
                File.Delete(partial);
                have = 0;
            }

            if (spec.ApproxBytes <= 0 || have < spec.ApproxBytes)
            {
                long needed = Math.Max(0, spec.ApproxBytes - have);
                if (AvailableBytes(directory) is { } free && free < needed + DiskHeadroomBytes)
                {
                    return ModelResult.Failed(path, DiskSpaceError(spec.Display, needed, free, directory));
                }

                var download = await DownloadRangeAsync(spec.Display, url, partial, have, spec.ApproxBytes, progress, cancellationToken).ConfigureAwait(false);
                if (!download.Ok)
                {
                    return ModelResult.Failed(path, download.Detail);
                }
            }

            long length = new FileInfo(partial).Length;
            if (spec.Sha256 is not null && spec.ApproxBytes > 0 && length != spec.ApproxBytes)
            {
                DeleteFileQuietly(partial);
                return ModelResult.Failed(path, string.Create(CultureInfo.InvariantCulture, $"the download of {spec.Display} is {length} bytes, not {spec.ApproxBytes}; deleted, try again"));
            }

            if (!LooksLike(partial, spec.Format))
            {
                DeleteFileQuietly(partial);
                return ModelResult.Failed(path, $"the download from {url} is not {FormatName(spec.Format)} {(spec.Format == ModelFormat.Zip ? "archive" : "model")}");
            }

            if (spec.Sha256 is { } pinned)
            {
                verifying?.Invoke();
                string actual = await Sha256Async(partial, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteFileQuietly(partial);
                    DiagnosticLog.Warn(_category, $"{spec.Display}: SHA-256 {actual}, expected {pinned}.");
                    return ModelResult.Failed(path, ChecksumError(spec.Display));
                }
            }

            File.Move(partial, path, overwrite: true);
            DiagnosticLog.Info(_category, $"Downloaded {spec.Display} ({SizeLabel(length)}) to {path}.");
            return new ModelResult(true, path, $"downloaded {SizeLabel(length)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The partial file stays: the next ensure resumes it.
            DiagnosticLog.Info(_category, $"Download of {spec.Display} paused at {(File.Exists(partial) ? SizeLabel(new FileInfo(partial).Length) : "0 B")}.");
            throw;
        }
        catch (Exception ex)
        {
            return ModelResult.Failed(path, Assistant.Explain(ex));
        }
    }

    /// <summary>
    /// Fetches <paramref name="url"/> onto <paramref name="partial"/>, from byte <paramref name="have"/> when it holds
    /// some. A body whose size is not <paramref name="expected"/> (when known) is refused before a byte is written: the
    /// file behind the URL changed, and downloading gigabytes to fail the checksum helps no one.
    /// </summary>
    private async Task<(bool Ok, long Received, string Detail)> DownloadRangeAsync(string display, Uri url, string partial, long have, long expected, IProgress<(long Received, long? Total)>? progress, CancellationToken cancellationToken)
    {
        DiagnosticLog.Info(_category, have > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Resuming {display} from {url} at byte {have}.")
            : $"Downloading {display} from {url} to {partial}.");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (have > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Nothing past what is held: the partial file is the whole file (the checks after decide).
            return (true, have, "");
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, have, $"HTTP {(int)response.StatusCode} from {url}");
        }

        long start = 0;
        long? total = response.Content.Headers.ContentLength;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (have == 0 || response.Content.Headers.ContentRange is not { From: { } from } range || from != have)
            {
                DeleteFileQuietly(partial);
                return (false, 0, $"{url} resumed at the wrong byte; the partial download was deleted, try again");
            }

            start = have;
            total = range.Length ?? (total is { } rest ? have + rest : null);
        }
        else if (have > 0)
        {
            DiagnosticLog.Info(_category, $"{url} did not resume; downloading {display} from the start.");
        }

        if (expected > 0 && total is { } offered && offered != expected)
        {
            return (false, start, string.Create(CultureInfo.InvariantCulture, $"{url} offers {offered} bytes, not the {expected} expected; the file behind it changed"));
        }

        long received = start;
        progress?.Report((received, total));
        using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        using (var target = new FileStream(partial, start > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true))
        {
            var buffer = new byte[CopyBufferBytes];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report((received, total));
            }
        }

        if (total is { } whole && received != whole)
        {
            return (false, received, string.Create(CultureInfo.InvariantCulture, $"download incomplete: {received} of {whole} bytes; try again to resume"));
        }

        return (true, received, "");
    }

    /// <summary>
    /// Makes sure the directory <paramref name="spec"/> unpacks to is on disk and complete (2026-09-29, the llama.cpp
    /// runtime): a present directory holding every required file costs nothing; otherwise each archive is ensured as a
    /// resumable, pinned download under <c>&lt;path&gt;.parts</c> (progress runs across all of them), every archive is
    /// unpacked into one folder (<paramref name="unpacking"/> is told first), the folder is checked and moved into
    /// place, and the archives are deleted. Returns a result, never throws, except the caller's own cancellation — the
    /// downloaded parts stay for the next try.
    /// </summary>
    public async Task<ModelResult> EnsureArchiveSetAsync(ArchiveSetSpec spec, IProgress<(long Received, long? Total)>? progress, Action? verifying, Action? unpacking, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        string path = spec.Path;

        if (System.IO.Directory.Exists(path))
        {
            if (IsCompleteModelDirectory(path, spec.RequiredFiles))
            {
                return new ModelResult(true, path, "present");
            }

            DiagnosticLog.Warn(_category, $"Discarding {path}: not a complete {spec.Display}.");
            try
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                return ModelResult.Failed(path, $"cannot replace {path}: {ex.Message}");
            }
        }

        string partsPath = path + ".parts";
        string extractPath = $"{path}.{Guid.NewGuid():N}.extracting";
        long total = spec.Parts.Sum(p => p.Bytes);
        try
        {
            var archives = new List<string>(spec.Parts.Count);
            long done = 0;
            foreach (var part in spec.Parts)
            {
                var partSpec = new ModelSpec(part.Name, System.IO.Path.Combine(partsPath, part.Name), part.Url, part.Bytes, ModelFormat.Zip, part.Sha256, Resumable: true);
                var partProgress = progress is null ? null : new OffsetProgress(progress, done, total);
                var result = await EnsureAsync(partSpec, partProgress, verifying, cancellationToken).ConfigureAwait(false);
                if (!result.Ok)
                {
                    return ModelResult.Failed(path, result.Detail);
                }

                archives.Add(result.Path);
                done += part.Bytes;
            }

            unpacking?.Invoke();
            DiagnosticLog.Info(_category, $"Unpacking {spec.Display} into {extractPath}.");
            await Task.Run(() =>
            {
                foreach (var archive in archives)
                {
                    ZipFile.ExtractToDirectory(archive, extractPath, overwriteFiles: true);
                }
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string? root = FindModelRoot(extractPath, spec.RequiredFiles);
            if (root is null)
            {
                return ModelResult.Failed(path, $"the archives of {spec.Display} do not contain it (no {MissingFileHint(extractPath, spec.RequiredFiles)})");
            }

            System.IO.Directory.Move(root, path);
            DeleteDirectoryQuietly(partsPath);
            DiagnosticLog.Info(_category, $"Downloaded {spec.Display} ({SizeLabel(total)}) to {path}.");
            return new ModelResult(true, path, $"downloaded {SizeLabel(total)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ModelResult.Failed(path, Assistant.Explain(ex));
        }
        finally
        {
            DeleteDirectoryQuietly(extractPath);
        }
    }

    /// <summary>Where a resumable download of <paramref name="path"/> collects its bytes until verified.</summary>
    public static string PartialPath(string path) => path + ".partial";

    /// <summary>The refusal when a pinned download's SHA-256 does not match (the file is deleted). Pinned.</summary>
    public static string ChecksumError(string display) => $"the download of {display} does not match its published checksum; deleted, try again";

    /// <summary>The refusal when the drive cannot hold what a resumable download still needs plus <see cref="DiskHeadroomBytes"/>. Pinned.</summary>
    public static string DiskSpaceError(string display, long needed, long free, string directory) =>
        $"not enough disk space for {display}: it needs {SizeLabel(needed)} more and {SizeLabel(DiskHeadroomBytes)} to spare, {System.IO.Path.GetPathRoot(directory)} has {SizeLabel(free)} free";

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static long? FreeBytesOn(string directory)
    {
        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One archive's progress reported as a share of the whole set's bytes.</summary>
    private sealed class OffsetProgress(IProgress<(long Received, long? Total)> inner, long offset, long total) : IProgress<(long Received, long? Total)>
    {
        public void Report((long Received, long? Total) value) => inner.Report((offset + value.Received, total));
    }

    /// <summary>Whether <paramref name="directory"/> holds every file in <paramref name="requiredFiles"/> (relative, forward or back slashes).</summary>
    public static bool IsCompleteModelDirectory(string directory, IReadOnlyList<string> requiredFiles) =>
        System.IO.Directory.Exists(directory) && FirstMissingFile(directory, requiredFiles) is null;

    /// <summary>Whether the file at <paramref name="path"/> starts with the zip local-header magic (<c>PK\x03\x04</c>). False for a missing, short or unreadable file.</summary>
    public static bool LooksLikeZip(string path) => StartsWith(path, ZipMagic);

    /// <summary>The unpacked directory that holds the model: the extraction root itself, or its one top-level folder (Vosk archives wrap the model in one).</summary>
    private static string? FindModelRoot(string extractPath, IReadOnlyList<string> requiredFiles)
    {
        if (IsCompleteModelDirectory(extractPath, requiredFiles))
        {
            return extractPath;
        }

        foreach (var sub in System.IO.Directory.EnumerateDirectories(extractPath))
        {
            if (IsCompleteModelDirectory(sub, requiredFiles))
            {
                return sub;
            }
        }

        return null;
    }

    /// <summary>The first required file missing from whichever candidate directory (the root or a top-level folder) holds any of them; the first required file when none does.</summary>
    private static string MissingFileHint(string extractPath, IReadOnlyList<string> requiredFiles)
    {
        var candidates = new List<string> { extractPath };
        candidates.AddRange(System.IO.Directory.EnumerateDirectories(extractPath));
        foreach (var candidate in candidates)
        {
            bool holdsAny = false;
            foreach (var relative in requiredFiles)
            {
                if (File.Exists(System.IO.Path.Combine(candidate, relative.Replace('/', System.IO.Path.DirectorySeparatorChar))))
                {
                    holdsAny = true;
                    break;
                }
            }

            if (holdsAny)
            {
                return FirstMissingFile(candidate, requiredFiles) ?? requiredFiles[0];
            }
        }

        return requiredFiles[0];
    }

    private static string? FirstMissingFile(string directory, IReadOnlyList<string> requiredFiles)
    {
        foreach (var relative in requiredFiles)
        {
            if (!File.Exists(System.IO.Path.Combine(directory, relative.Replace('/', System.IO.Path.DirectorySeparatorChar))))
            {
                return relative;
            }
        }

        return null;
    }

    /// <summary>Streams <paramref name="url"/> to <paramref name="tempPath"/>, creating the destination's parent; the length is checked against Content-Length when the server sent one.</summary>
    private async Task<(bool Ok, long Received, string Detail)> DownloadAsync(string display, Uri url, string path, string tempPath, IProgress<(long Received, long? Total)>? progress, CancellationToken cancellationToken)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        DiagnosticLog.Info(_category, $"Downloading {display} from {url} to {path}.");
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (false, 0, $"HTTP {(int)response.StatusCode} from {url}");
        }

        long? total = response.Content.Headers.ContentLength;
        long received = 0;
        progress?.Report((0, total));

        using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true))
        {
            var buffer = new byte[CopyBufferBytes];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report((received, total));
            }
        }

        if (total is { } expected && received != expected)
        {
            return (false, received, string.Create(CultureInfo.InvariantCulture, $"download incomplete: {received} of {expected} bytes"));
        }

        return (true, received, "");
    }

    // Leave nothing behind that a later run could mistake for a complete model. Best effort: the
    // result (or the exception) is the interesting part.
    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    /// <summary>Whether the file at <paramref name="path"/> starts with the ggml magic. False for a missing, short or unreadable file.</summary>
    public static bool LooksLikeGgml(string path) => StartsWith(path, GgmlMagic);

    /// <summary>
    /// Whether the file at <paramref name="path"/> looks like an ONNX model. A protobuf has no
    /// magic; an exported <c>ModelProto</c> starts with field 1 (<c>ir_version</c>, tag
    /// <c>0x08</c>, a one-byte varint) and then field 2 (<c>producer_name</c>, tag <c>0x12</c>):
    /// <c>08 09 12 07 "pytorch"</c> for kokoro.onnx. That rejects what the magics reject — an
    /// HTML error page saved as a model, a zero-length file — which a length check alone would not.
    /// False for a missing, short or unreadable file.
    /// </summary>
    public static bool LooksLikeOnnx(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[3];
            return stream.Read(head) == 3 && head[0] == 0x08 && head[2] == 0x12;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The check for <paramref name="format"/>.</summary>
    public static bool LooksLike(string path, ModelFormat format) => format switch
    {
        ModelFormat.Onnx => LooksLikeOnnx(path),
        ModelFormat.Gguf => LooksLikeGguf(path),
        ModelFormat.Zip => LooksLikeZip(path),
        _ => LooksLikeGgml(path),
    };

    /// <summary>The words after "not" in the discard / refusal lines: <c>a ggml</c>, <c>an ONNX</c>, <c>a GGUF</c> or <c>a zip</c>.</summary>
    public static string FormatName(ModelFormat format) => format switch
    {
        ModelFormat.Onnx => "an ONNX",
        ModelFormat.Gguf => "a GGUF",
        ModelFormat.Zip => "a zip",
        _ => "a ggml",
    };

    /// <summary>Whether the file at <paramref name="path"/> starts with the GGUF magic. False for a missing, short or unreadable file.</summary>
    public static bool LooksLikeGguf(string path) => StartsWith(path, GgufMagic);

    private static bool StartsWith(string path, byte[] magic)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[4];
            int read = stream.Read(head);
            return read == magic.Length && head[..magic.Length].SequenceEqual(magic);
        }
        catch
        {
            return false;
        }
    }

    public const string WhisperRepository = "https://huggingface.co/sandrohanea/whisper.net/resolve/v4/classic/";
    public const string SileroUrl = "https://huggingface.co/sandrohanea/whisper.net/resolve/v4/vad/ggml-silero-v6.2.0.bin";
    public const string SileroFileName = "ggml-silero-v6.2.0.bin";

    /// <summary>
    /// The Kokoro-82M model as KokoroSharp's author publishes it (float32, 24 kHz; the fp16 and
    /// Chinese variants sit beside it under the same release). Downloaded on first use when
    /// <c>TTS source</c> is <c>in-process</c>; never shipped beside the exe (2026-09-16).
    /// </summary>
    public const string KokoroModelUrl = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx";
    public const string KokoroFileName = "kokoro.onnx";

    /// <summary>Where alphacephei publishes the Vosk models: <c>&lt;repository&gt;&lt;name&gt;.zip</c>, each archive wrapping the model in one folder of the same name.</summary>
    public const string VoskRepository = "https://alphacephei.com/vosk/models/";

    /// <summary>
    /// The names <see cref="ResolveVosk"/> accepts (the <c>STT vosk model</c> picker's rows): the
    /// English models with a dynamic graph, which is what the interrupt's phrase-only grammar
    /// needs — the 1–2 GB static-graph models (<c>vosk-model-en-us-0.22</c>, <c>-0.42-gigaspeech</c>,
    /// <c>vosk-model-en-in-0.5</c>) log "Runtime graphs are not supported by this model" and
    /// decode the full vocabulary instead, so they are not offered. The first is the default.
    /// </summary>
    public static readonly string[] VoskModelNames = { "vosk-model-small-en-us-0.15", "vosk-model-en-us-0.22-lgraph", "vosk-model-small-en-in-0.4" };

    /// <summary>The default Vosk model (the small US English one); the tests and the model-gated facts look for this folder.</summary>
    public const string VoskModelDirectoryName = "vosk-model-small-en-us-0.15";
    public const string VoskModelUrl = VoskRepository + VoskModelDirectoryName + ".zip";

    /// <summary>The settings-menu and status wording for a bad <c>SttVoskModel</c> value. Pinned.</summary>
    public const string VoskModelError = "must be vosk-model-small-en-us-0.15, vosk-model-en-us-0.22-lgraph or vosk-model-small-en-in-0.4";

    /// <summary>The files that make a Vosk model directory usable (the dynamic-graph layout every offered model shares); a directory missing any is treated as absent.</summary>
    public static readonly string[] VoskRequiredFiles = { "am/final.mdl", "conf/model.conf", "graph/HCLr.fst", "graph/Gr.fst", "ivector/final.ie" };

    /// <summary>The default Vosk wake-word model under this store's directory.</summary>
    public ModelDirectorySpec Vosk() => Vosk(VoskModelDirectoryName)!;

    /// <summary><see cref="ResolveVosk(string, string)"/> over this store's directory.</summary>
    public ModelDirectorySpec? Vosk(string setting) => ResolveVosk(setting, Directory);

    /// <summary>
    /// The model directory for a <c>SttVoskModel</c> setting: a known name (any case) resolves to
    /// <c>&lt;dir&gt;\&lt;name&gt;</c> plus its archive URL; anything else is null. No path form:
    /// the picker is the one way to choose (there is no wake env var either).
    /// </summary>
    public static ModelDirectorySpec? ResolveVosk(string setting, string modelsDirectory)
    {
        ArgumentNullException.ThrowIfNull(modelsDirectory);
        string value = (setting ?? "").Trim();
        foreach (var name in VoskModelNames)
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelDirectorySpec(
                    "vosk model",
                    System.IO.Path.Combine(modelsDirectory, name),
                    new Uri(VoskRepository + name + ".zip"),
                    name switch { "vosk-model-en-us-0.22-lgraph" => VoskLgraphBytes, "vosk-model-small-en-in-0.4" => VoskSmallInBytes, _ => VoskBytes },
                    VoskRequiredFiles);
            }
        }

        return null;
    }

    /// <summary>The names <see cref="ResolveWhisper"/> accepts — the ggml file names as they land on disk (the short <c>base.en</c> form was retired 2026-09-16); anything else must be an absolute path.</summary>
    public static readonly string[] WhisperModelNames = { "ggml-tiny.en.bin", "ggml-base.en.bin", "ggml-small.en.bin" };

    /// <summary>The settings-menu and status wording for a bad <c>SttWhisperModel</c> value. Pinned.</summary>
    public const string WhisperModelError = "must be ggml-tiny.en.bin, ggml-base.en.bin, ggml-small.en.bin or an absolute path to a ggml .bin";

    private const long TinyBytes = 77_691_713;
    private const long BaseBytes = 147_964_211;
    private const long SmallBytes = 487_601_967;
    private const long SileroBytes = 885_098;
    /// <summary>The exact archive sizes (Content-Length, 2026-09-16) of the three Vosk models, in <see cref="VoskModelNames"/> order.</summary>
    private const long VoskBytes = 41_205_931;
    private const long VoskLgraphBytes = 130_557_655;
    private const long VoskSmallInBytes = 37_573_330;

    /// <summary>The exact size of the published kokoro.onnx (<see cref="SizeLabel"/> says 326 MB).</summary>
    public const long KokoroBytes = 325_508_342;

    /// <summary>
    /// The model file for a <c>SttWhisperModel</c> setting: a known file name resolves to
    /// <c>&lt;dir&gt;\&lt;name&gt;</c> plus its download URL; an absolute path resolves
    /// to itself with no URL (it must exist); anything else is null.
    /// </summary>
    public static ModelSpec? ResolveWhisper(string setting, string modelsDirectory)
    {
        ArgumentNullException.ThrowIfNull(modelsDirectory);
        string value = (setting ?? "").Trim();
        if (value.Length == 0)
        {
            return null;
        }

        foreach (var name in WhisperModelNames)
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelSpec(
                    "whisper " + name,
                    System.IO.Path.Combine(modelsDirectory, name),
                    new Uri(WhisperRepository + name),
                    name switch { "ggml-tiny.en.bin" => TinyBytes, "ggml-small.en.bin" => SmallBytes, _ => BaseBytes });
            }
        }

        if (System.IO.Path.IsPathRooted(value) && value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && value.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0)
        {
            return new ModelSpec("whisper " + System.IO.Path.GetFileName(value), value, null, 0);
        }

        return null;
    }

    /// <summary>
    /// A size as people read it: <c>4.2 GB</c>, <c>148 MB</c>, <c>1 MB</c>, <c>512 KB</c>. Invariant. The gigabyte step
    /// (one decimal, dropped when it is zero) arrived with the embedded LLM's files (2026-09-29); "4216 MB" read badly.
    /// </summary>
    public static string SizeLabel(long bytes)
    {
        if (bytes >= 1_000_000_000)
        {
            return (bytes / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        }

        if (Math.Round(bytes / 1_000_000.0) >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / 1_000_000.0)} MB");
        }

        if (bytes >= 1_000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / 1_000.0)} KB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
    }
}
