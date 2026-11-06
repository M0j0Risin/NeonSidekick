using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Files;

namespace NeonSidekick.Updates;

/// <summary>A version as a release tag spells it: three plain numbers (2026-11-04, self-update), compared by number, so <c>0.5.10</c> beats <c>0.5.9</c>.</summary>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    /// <summary>Reads <c>0.5.7</c>. Digits and two dots only: a suffix, a sign or a fourth part is no version here.</summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        string[] parts = text.Split('.');
        if (parts.Length != 3 || !parts.All(IsNumber))
        {
            return false;
        }

        version = new UpdateVersion(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>Reads a plain <c>v0.5.7</c> tag; anything else counts as no release (<c>build.ps1 -Tag</c>'s own rule).</summary>
    public static bool TryParseTag(string? tag, out UpdateVersion version)
    {
        version = default;
        return tag is ['v', ..] && TryParse(tag[1..], out version);
    }

    public int CompareTo(UpdateVersion other) => (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static bool IsNumber(string part) => part.Length is > 0 and <= 9 && part.All(char.IsAsciiDigit);
}

/// <summary>
/// What reading a manifest's file came to (2026-11-04, review P8-8): "can't open it now" isn't "it's bad", or a passing scanner counted as a damaged package, for every caller but the recheck, which had worked round it by hand.
/// </summary>
/// <param name="Manifest">The manifest, or null.</param>
/// <param name="Problem">Why not, in words for a line; null with a manifest.</param>
/// <param name="Unreadable">It's there and can't be read right now: nobody may read that as the package's fault.</param>
/// <param name="Missing">It isn't there.</param>
/// <param name="Bytes">The bytes it was read as, so a chain hashes those and not a second read (review P13-8). Null when it couldn't be read.</param>
public sealed record UpdateManifestLoad(UpdateManifest? Manifest, string? Problem, bool Unreadable = false, bool Missing = false, byte[]? Bytes = null);

/// <summary>One file a package lists.</summary>
public sealed class UpdateManifestFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// <c>update-manifest.json</c>, the package's list of itself (2026-11-04, self-update): written by <c>build.ps1 -Package</c> as the last step
/// before archiving, and a lasting contract, since an old exe reads a new release's. An old exe refuses a <see cref="Format"/> it doesn't know and
/// points to the Releases page. The file doesn't list itself: the signed <c>.sha256</c> holds its hash (<see cref="UpdateShaFile"/>), so the chain
/// from a file on disk to the embedded keys runs file, manifest, <c>.sha256</c>, <c>.sig</c>.
///
/// <para>Untrusted on read: the installed one sits in a folder anything could have changed, and <see cref="Read"/> takes only relative paths inside
/// the install folder and outside <c>.update</c>. The file being there is also what decides that a folder is a package's.</para>
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>The format this build reads and writes.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The file's name, at the top of a package.</summary>
    public const string FileName = "update-manifest.json";

    private const int LoadTries = 5;

    private const string ShortNameStem = "UPDATE~";

    private static readonly TimeSpan LoadPause = TimeSpan.FromMilliseconds(200);

    [JsonPropertyName("format")]
    public int Format { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("rid")]
    public string Rid { get; set; } = "";

    [JsonPropertyName("exe")]
    public string Exe { get; set; } = "";

    [JsonPropertyName("files")]
    public List<UpdateManifestFile> Files { get; set; } = [];

    /// <summary>The version as a value; only a manifest <see cref="Read"/> accepted has one.</summary>
    [JsonIgnore]
    public UpdateVersion ParsedVersion => UpdateVersion.TryParse(Version, out var version) ? version : default;

    /// <summary>Reads a manifest from its file's bytes. The problem says why not, in words for the failure line; null with a manifest.</summary>
    public static UpdateManifest? Read(ReadOnlySpan<byte> bytes, out string? problem)
    {
        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.UpdateManifest);
        }
        catch (JsonException)
        {
            problem = UpdateText.ManifestUnreadable;
            return null;
        }

        if (manifest is null)
        {
            problem = UpdateText.ManifestUnreadable;
            return null;
        }

        if (manifest.Format != CurrentFormat)
        {
            problem = UpdateText.ManifestUnknownFormat(manifest.Format);
            return null;
        }

        problem = manifest.Problem();
        return problem is null ? manifest : null;
    }

    /// <summary>Reads a manifest from a file, for a caller to whom every failure is the same; one that decides anything from it uses <see cref="Load"/>.</summary>
    public static UpdateManifest? ReadFile(string path, out string? problem)
    {
        var load = Load(path);
        problem = load.Problem;
        return load.Manifest;
    }

    /// <summary>Reads a manifest from a file, saying which way it failed. Over <see cref="UpdateFolder.ReadCap"/> it reads as bad, as bad JSON does.</summary>
    public static UpdateManifestLoad Load(string path)
    {
        byte[]? bytes;
        try
        {
            bytes = UpdateFolder.ReadCapped(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new UpdateManifestLoad(null, UpdateText.ManifestUnreadable, Missing: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpdateManifestLoad(null, UpdateText.ManifestHeld, Unreadable: true);
        }

        if (bytes is null)
        {
            return new UpdateManifestLoad(null, UpdateText.ManifestUnreadable);
        }

        var manifest = Read(bytes, out string? problem);
        return new UpdateManifestLoad(manifest, problem, Bytes: bytes);
    }

    /// <summary>
    /// <see cref="Load"/>, tried again for a moment while it can't be read (2026-11-04, reviews P8-2, P8-7): for the two callers with no later try, where a scanner on a fresh manifest would decide something for good.
    /// </summary>
    public static UpdateManifestLoad LoadRetrying(string path, Action<TimeSpan> pause)
    {
        ArgumentNullException.ThrowIfNull(pause);
        var load = Load(path);
        for (int tries = 1; load.Unreadable && tries < LoadTries; tries++)
        {
            pause(LoadPause);
            load = Load(path);
        }

        return load;
    }

    /// <summary>The file's listed hash by path, under <paramref name="comparer"/>.</summary>
    public Dictionary<string, string> Hashes(StringComparer comparer)
    {
        var hashes = new Dictionary<string, string>(comparer);
        foreach (var file in Files)
        {
            hashes[file.Path] = file.Sha256;
        }

        return hashes;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a package's own relative path: forward slashes, no empty, <c>.</c> or <c>..</c> part, no root, no colon
    /// (a drive or an NTFS stream), no part ending in a dot or space (Windows would quietly name another file), no control characters, and on Windows no device name (2026-11-04, review P5-16: a step at <c>NUL</c> targets a
    /// device, with or without an extension). None is in today's package. Shared with the journal's reading, which also accepts <c>.update</c>'s own paths.
    /// </summary>
    internal static bool IsRelativeInside(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            return false;
        }

        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part[^1] is '.' or ' ' || (OperatingSystem.IsWindows() && WorkingDirectory.HasDeviceName(part)))
            {
                return false;
            }
        }

        return !System.IO.Path.IsPathRooted(path);
    }

    private string? Problem()
    {
        // A JSON null reaches these past their initializers (2026-11-04, review P1-1): this reader's whole job is untrusted input, and a throw here went past every catch on the start's path.
        if (!UpdateVersion.TryParse(Version, out _) || string.IsNullOrEmpty(Rid) || Files is null || Exe is null)
        {
            return UpdateText.ManifestUnreadable;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null)
            {
                return UpdateText.ManifestUnreadable;
            }

            if (!IsRelativeInside(file.Path) || IsReserved(file.Path) || !IsHash(file.Sha256) || !seen.Add(file.Path))
            {
                return UpdateText.ManifestBadEntry(file.Path ?? "");
            }
        }

        // The exe sits at the top: the swap's last move, and what the renamed-copy check compares with.
        return Exe.Contains('/', StringComparison.Ordinal) || !seen.Contains(Exe) ? UpdateText.ManifestBadEntry(Exe) : null;
    }

    private static bool IsReserved(string path)
    {
        // The release folder too: previous/ holds a package's files beside its own release/ folder, so a top-level one would land in it.
        string top = path.Split('/')[0];
        return string.Equals(top, UpdateFolder.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(top, UpdateFolder.ReleaseName, StringComparison.OrdinalIgnoreCase) || IsUpdatesShortName(top) || string.Equals(path, FileName, StringComparison.OrdinalIgnoreCase);
    }

    // UPDATE~1, .update's own short name on a volume with 8.3 names (2026-11-04, review P13-10), and only that: a general ~digit rule would fail a swap over a user's stray named like one.
    private static bool IsUpdatesShortName(string top) =>
        top.StartsWith(ShortNameStem, StringComparison.OrdinalIgnoreCase) && top.Length > ShortNameStem.Length && top.AsSpan(ShortNameStem.Length).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool IsHash(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);
}

/// <summary>The hashes of a file or some bytes, in the lower-case hex the manifest and the <c>.sha256</c> use.</summary>
public static class UpdateHash
{
    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The file's hash, read in chunks with <paramref name="cancellationToken"/> checked between them (2026-11-04, review P7-6): one big exe could otherwise run past a window close's whole budget.</summary>
    public static string OfFile(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        return OfStream(stream, cancellationToken);
    }

    /// <summary>What <see cref="OfFile"/> reads with, a seam so a test can cancel during a read (2026-11-04, review P10-14).</summary>
    internal static string OfStream(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The release's <c>.sha256</c> file (2026-11-04, self-update), the bytes the signature covers: the archive's hash and name, then a <c>#</c> comment
/// holding the manifest's hash and name. A plain second entry would fail a user's <c>sha256sum -c</c>, since the manifest isn't beside the
/// download; the comment passes it silently on both GNU <c>sha256sum</c> and Perl <c>shasum</c> (tested). A lasting contract, pinned by a test.
/// </summary>
public sealed record UpdateShaFile(string ArchiveHash, string ArchiveName, string ManifestHash)
{
    /// <summary>The file's text, as <c>build.ps1 -Package</c> writes it; the tests' and the simulation's.</summary>
    public static string Text(string archiveHash, string archiveName, string manifestHash) =>
        $"{archiveHash} *{archiveName}\n# {manifestHash} *{UpdateManifest.FileName}\n";

    /// <summary>Reads the signed text, or null for one without both lines: a release from before the updater has no manifest line, and can't be installed by it.</summary>
    public static UpdateShaFile? TryParse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 2 || !lines[1].StartsWith("# ", StringComparison.Ordinal))
        {
            return null;
        }

        return Entry(lines[0]) is { } archive && Entry(lines[1][2..]) is { } manifest && manifest.Name == UpdateManifest.FileName
            ? new UpdateShaFile(archive.Hash, archive.Name, manifest.Hash)
            : null;
    }

    private static (string Hash, string Name)? Entry(string line)
    {
        int space = line.IndexOf(" *", StringComparison.Ordinal);
        if (space != 64)
        {
            return null;
        }

        string hash = line[..space];
        string name = line[(space + 2)..];
        return hash.All(char.IsAsciiHexDigit) && name.Length > 0 ? (hash, name) : null;
    }
}

/// <summary>One installed file as staging found it: its hash, and the length and time it had just before that hash was taken.</summary>
public sealed class UpdateInstallFile
{
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("length")]
    public long Length { get; set; }

    [JsonPropertyName("time")]
    public long TimeTicks { get; set; }
}

/// <summary>
/// The hashes of the install's listed files, kept in <c>staged/</c> (2026-11-04, self-update): hashing the whole install happens at staging, in the
/// background, so a swap in a window close's 4 s only hashes what moves, and a file altered by hand counts as changed. <see cref="ManifestHash"/> is
/// the installed manifest they were taken against: a package unpacked by hand over the folder changes it, and then the list no longer holds.
///
/// <para>Each entry keeps the file's length and time (2026-11-04, review P4-8: a shared library antivirus quarantined while a stage waited read as "same", and the swap reported done with it gone), read before its hash, so a write between the two shows
/// as a change. <see cref="Current"/> hashes again only what's different now, so a window close's cost stays near the old one.</para>
/// </summary>
public sealed class UpdateInstallHashes
{
    [JsonPropertyName("manifest")]
    public string ManifestHash { get; set; } = "";

    [JsonPropertyName("files")]
    public Dictionary<string, UpdateInstallFile> Files { get; set; } = [];

    /// <summary>Takes the hashes of every file <paramref name="installed"/> lists that's there; a missing one has no entry, so it counts as changed. <paramref name="cancellationToken"/> ends it (review P2-12: it was a whole install's hashing that nothing could stop).</summary>
    public static UpdateInstallHashes Take(string install, UpdateManifest installed, string manifestHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var hashes = new UpdateInstallHashes { ManifestHash = manifestHash };
        foreach (var file in installed.Files)
        {
            string full = System.IO.Path.Combine(install, file.Path);
            if (File.Exists(full))
            {
                try
                {
                    var info = new FileInfo(full);
                    long length = info.Length;
                    long time = info.LastWriteTimeUtc.Ticks;
                    hashes.Files[file.Path] = new UpdateInstallFile { Sha256 = UpdateHash.OfFile(full, cancellationToken), Length = length, TimeTicks = time };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable counts as changed, as a missing file does.
                }
            }
        }

        return hashes;
    }

    /// <summary>
    /// The stage's list, one reader for the recheck and the start (2026-11-04, reviews P2-9, P3-19). Null when it's missing or not a valid list: <c>"files": null</c> or a null entry went past every catch on the exit's path. A failure to read it throws, since that isn't the list's fault.
    /// </summary>
    public static UpdateInstallHashes? TryRead(UpdateFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            if (UpdateFolder.ReadCapped(folder.StagedInstallHashes) is not { } bytes)
            {
                return null;
            }

            var hashes = JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.UpdateInstallHashes);
            return hashes is { ManifestHash: not null, Files: not null } && !hashes.Files.Values.Any(file => file is null or { Sha256: null }) ? hashes : null;
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// What each listed file hashes to now, by path: the kept answer when its length and time are as staging found them, else hashed again under <paramref name="cancellationToken"/>, so a file edited or quarantined since gets repaired, and one only touched stays verified.
    /// A file that's gone has no entry. One that can't be read throws, for the recheck's Keep (2026-11-04, review P11-4): read as changed, a real old-version file a read-denying holder had moved to <c>kept/</c>, and the way back was lost.
    /// </summary>
    public Dictionary<string, string> Current(string install, UpdateManifest installed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in installed.Files)
        {
            var info = new FileInfo(System.IO.Path.Combine(install, file.Path));
            if (!info.Exists)
            {
                continue;
            }

            if (Files.TryGetValue(file.Path, out var kept) && kept.Length == info.Length && kept.TimeTicks == info.LastWriteTimeUtc.Ticks)
            {
                current[file.Path] = kept.Sha256;
                continue;
            }

            try
            {
                current[file.Path] = UpdateHash.OfFile(info.FullName, cancellationToken);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Gone since the look above: changed, as a missing file is.
            }
        }

        return current;
    }
}

/// <summary>The update's JSON types (2026-11-04, self-update), source-generated like every other area's.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(UpdateInstallHashes))]
[JsonSerializable(typeof(UpdateInstallFile))]
[JsonSerializable(typeof(UpdateJournal))]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
