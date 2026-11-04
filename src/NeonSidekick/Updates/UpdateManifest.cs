using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public int CompareTo(UpdateVersion other)
    {
        int major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        int minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static bool IsNumber(string part) => part.Length is > 0 and <= 9 && part.All(char.IsAsciiDigit);
}

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

    /// <summary>Reads a manifest from a file; a file that can't be read is the same problem as one that can't be understood.</summary>
    public static UpdateManifest? ReadFile(string path, out string? problem)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = UpdateText.ManifestUnreadable;
            return null;
        }

        return Read(bytes, out problem);
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
    /// (a drive or an NTFS stream), no part ending in a dot or space (Windows would quietly name another file), no control characters. Shared with
    /// the journal's reading, which also accepts <c>.update</c>'s own paths.
    /// </summary>
    internal static bool IsRelativeInside(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            return false;
        }

        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part[^1] is '.' or ' ')
            {
                return false;
            }
        }

        return !System.IO.Path.IsPathRooted(path);
    }

    private string? Problem()
    {
        if (!UpdateVersion.TryParse(Version, out _) || string.IsNullOrEmpty(Rid))
        {
            return UpdateText.ManifestUnreadable;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (!IsRelativeInside(file.Path) || IsReserved(file.Path) || !IsHash(file.Sha256) || !seen.Add(file.Path))
            {
                return UpdateText.ManifestBadEntry(file.Path);
            }
        }

        // The exe sits at the top: the swap's last move, and what the renamed-copy check compares with.
        return Exe.Contains('/', StringComparison.Ordinal) || !seen.Contains(Exe) ? UpdateText.ManifestBadEntry(Exe) : null;
    }

    private static bool IsReserved(string path)
    {
        // "release" too: previous/ holds a package's files beside its own release/ folder, so a top-level one would land in it.
        string top = path.Split('/')[0];
        return string.Equals(top, UpdateFolder.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(top, "release", StringComparison.OrdinalIgnoreCase) || string.Equals(path, FileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHash(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigit);
}

/// <summary>The hashes of a file or some bytes, in the lower-case hex the manifest and the <c>.sha256</c> use.</summary>
public static class UpdateHash
{
    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
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

/// <summary>
/// The hashes of the install's listed files, kept in <c>staged/</c> (2026-11-04, self-update): hashing the whole install happens at staging, in the
/// background, so a swap in a window close's 4 s only hashes what moves, and a file altered by hand counts as changed. <see cref="ManifestHash"/> is
/// the installed manifest they were taken against: a package unpacked by hand over the folder changes it, and then the list no longer holds.
/// </summary>
public sealed class UpdateInstallHashes
{
    [JsonPropertyName("manifest")]
    public string ManifestHash { get; set; } = "";

    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = [];

    /// <summary>Takes the hashes of every file <paramref name="installed"/> lists that's there; a missing one has no entry, so it counts as changed.</summary>
    public static UpdateInstallHashes Take(string install, UpdateManifest installed, string manifestHash)
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
                    hashes.Files[file.Path] = UpdateHash.OfFile(full);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable counts as changed, as a missing file does.
                }
            }
        }

        return hashes;
    }
}

/// <summary>The update's JSON types (2026-11-04, self-update), source-generated like every other area's.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(UpdateInstallHashes))]
[JsonSerializable(typeof(UpdateJournal))]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
