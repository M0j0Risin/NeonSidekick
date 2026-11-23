using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Skills;

/// <summary>
/// The hashes a project skill's trust is checked by (2026-11-23, feature idea #16): one per skill, over what could reach the model from it — its
/// <c>SKILL.md</c> and the bundled files <c>load_skill</c> lists (<see cref="SkillCatalog.Resources"/>, the first 50, never through a link) — and one
/// per trusted folder (<see cref="FolderFingerprint"/>), over the sorted skill folder names and each skill's hash, so an add, a remove or an edit
/// changes it.
/// <para>A budget per skill (<see cref="ProjectSkillTrust.SkillHashBudget"/>, 128 KB of content): <c>SKILL.md</c> first, then the listed files in
/// sorted order; a file past the budget, or over the read cap, counts by relative path, size and write time alone, so a change still shows. A cold
/// folder hashes at most 50 × 128 KB. Each skill's hash is cached by its files' (path, size, write time) stamps and recomputed only when one moved:
/// the catalog rescans every turn. Never throws: an unreadable file counts by its path and an error mark.</para>
/// </summary>
public sealed class ProjectSkillHasher
{
    private sealed record Stamp(string Relative, long Length, long Ticks);

    private sealed record Cached(IReadOnlyList<Stamp> Stamps, string Hash);

    private readonly Dictionary<string, Cached> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>How many hashes were computed rather than reused (a test proves an untouched folder isn't re-hashed).</summary>
    public int Computed { get; private set; }

    /// <summary>The hash of the skill folder <paramref name="directory"/> (its <c>SKILL.md</c> and listed files), from the cache while no stamp moved.</summary>
    public string SkillHash(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var stamps = Stamps(directory);
        lock (_gate)
        {
            if (_cache.TryGetValue(directory, out var held) && held.Stamps.SequenceEqual(stamps))
            {
                return held.Hash;
            }
        }

        string hash = Compute(directory, stamps);
        lock (_gate)
        {
            _cache[directory] = new Cached(stamps, hash);
            Computed++;
        }

        return hash;
    }

    /// <summary>
    /// A trusted folder's fingerprint: SHA-256 over each skill's key (<c>.agents/skills/x</c>, sorted ordinally) and its hash, null for a skill past the
    /// folder's 50 (named only). Pure.
    /// </summary>
    public static string FolderFingerprint(IEnumerable<KeyValuePair<string, string?>> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var sb = new StringBuilder();
        foreach (var (key, hash) in skills.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(key).Append('\0').Append(hash ?? "-").Append('\n');
        }

        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Lower-case hex of <paramref name="bytes"/>.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // SKILL.md, then the listed files (never through a link: SkillCatalog.ProjectResources), each with its stamp.
    private static List<Stamp> Stamps(string directory)
    {
        var stamps = new List<Stamp>();
        foreach (string relative in Inputs(directory))
        {
            try
            {
                var info = new FileInfo(Path.Combine(directory, relative));
                stamps.Add(info.Exists ? new Stamp(relative, info.Length, info.LastWriteTimeUtc.Ticks) : new Stamp(relative, -1, 0));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                stamps.Add(new Stamp(relative, -2, 0));
            }
        }

        return stamps;
    }

    private static IEnumerable<string> Inputs(string directory)
    {
        yield return SkillCatalog.FileName;
        foreach (string relative in SkillCatalog.ProjectResources(directory, out _))
        {
            yield return relative;
        }
    }

    private static string Compute(string directory, IReadOnlyList<Stamp> stamps)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long budget = ProjectSkillTrust.SkillHashBudget;
        foreach (var stamp in stamps)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(stamp.Relative + "\0"));
            bool whole = stamp.Length >= 0 && stamp.Length <= budget && stamp.Length <= ProjectSkillTrust.MaxSkillFileBytes;
            if (whole)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(Path.Combine(directory, stamp.Relative));
                    hash.AppendData(bytes);
                    budget -= bytes.LongLength;
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    DiagnosticLog.Debug(SkillCatalog.Category, $"Could not hash {stamp.Relative} of {directory}: {LogText.Excerpt(ex.Message)}");
                }
            }

            // Past the budget or the cap, or unreadable: its size and write time stand for it.
            hash.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"#{stamp.Length}:{stamp.Ticks}")));
        }

        return Hex(hash.GetHashAndReset());
    }
}
