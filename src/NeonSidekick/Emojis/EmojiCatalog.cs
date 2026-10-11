using NeonSidekick.Diagnostics;

namespace NeonSidekick.Emojis;

/// <summary>
/// One emoji of the catalog (2026-10-10): the fully-qualified spelling, its Unicode group and subgroup, its name, CLDR's keywords,
/// the emoji version it came in, and its five uniform skin-tone forms (light to dark) when it takes them, else none.
/// <see cref="Order"/> is its place in the catalog, CLDR's order, the tie-break of every ranking.
/// </summary>
public sealed record EmojiEntry(string Emoji, string Group, string Subgroup, string Name, IReadOnlyList<string> Keywords, string Version, IReadOnlyList<string> Tones, int Order)
{
    /// <summary>Whether the emoji takes a skin tone: its five forms are known.</summary>
    public bool HasTones => Tones.Count == EmojiCatalog.ToneCount;

    /// <summary>The form in <paramref name="tone"/> (1 light … 5 dark); 0, or a tone it does not take, is the emoji itself.</summary>
    public string InTone(int tone) => HasTones && tone is >= 1 and <= EmojiCatalog.ToneCount ? Tones[tone - 1] : Emoji;
}

/// <summary>A Unicode emoji group (<c>Animals &amp; Nature</c>) and its subgroups in catalog order.</summary>
public sealed record EmojiGroup(string Name, IReadOnlyList<string> Subgroups);

/// <summary>An emoji found by its spelling: the entry and the tone it was spelled in (0 for none).</summary>
public readonly record struct EmojiFound(EmojiEntry Entry, int Tone);

/// <summary>
/// The emoji catalog the avatar picker browses (2026-10-10, the user's ask: a profile's avatar picked from the whole set, by name,
/// keyword and category): <c>assets/emoji/emoji.tsv</c>, written by <c>tools/EmojiGen.cs</c> from Unicode's emoji-test.txt and CLDR's
/// annotations, embedded as <see cref="ResourceName"/> and read once, the first time it is asked for (<see cref="Shipped"/>; the
/// <c>ThemeLibrary</c> shape). Only what Windows Terminal draws is in it: no country or subdivision flags, nothing past Emoji 15.1, the
/// toned forms folded into their bases. A row that does not parse is left out with a warning, never fatal;
/// <c>EmojiCatalogTests</c> holds the shipped file to none. Tab-separated so no JSON context is needed.
/// </summary>
public sealed class EmojiCatalog
{
    /// <summary>The csproj's <c>LogicalName</c> for the catalog.</summary>
    public const string ResourceName = "emoji/emoji.tsv";

    /// <summary>The skin tones an emoji that takes one has: light, medium-light, medium, medium-dark, dark.</summary>
    public const int ToneCount = 5;

    private const string Category = "Emoji";

    private static readonly Lazy<EmojiCatalog> s_shipped = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, EmojiFound> _bySpelling;

    private EmojiCatalog(IReadOnlyList<EmojiEntry> entries, IReadOnlyList<EmojiGroup> groups, IReadOnlyList<string> problems)
    {
        Entries = entries;
        Groups = groups;
        Problems = problems;
        _bySpelling = new Dictionary<string, EmojiFound>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            _bySpelling.TryAdd(Bare(entry.Emoji), new EmojiFound(entry, 0));
            for (int t = 0; t < entry.Tones.Count; t++)
            {
                _bySpelling.TryAdd(Bare(entry.Tones[t]), new EmojiFound(entry, t + 1));
            }
        }
    }

    /// <summary>The catalog the app ships.</summary>
    public static EmojiCatalog Shipped => s_shipped.Value;

    /// <summary>Every emoji, in catalog order.</summary>
    public IReadOnlyList<EmojiEntry> Entries { get; }

    /// <summary>The groups, in catalog order, each with its subgroups.</summary>
    public IReadOnlyList<EmojiGroup> Groups { get; }

    /// <summary>The rows that did not parse, as "line N: why".</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// The emoji spelled <paramref name="emoji"/>, in any of its tones, with or without its U+FE0F presentation selectors (a typed
    /// <c>☺</c> finds <c>☺️</c>); null for one the catalog does not hold.
    /// </summary>
    public EmojiFound? Find(string? emoji) =>
        string.IsNullOrEmpty(emoji) ? null : _bySpelling.TryGetValue(Bare(emoji.Trim()), out var found) ? found : null;

    /// <summary>
    /// The catalog in <paramref name="tsv"/>: one emoji a line as <c>tools/EmojiGen.cs</c> writes it (emoji, group, subgroup, name,
    /// keywords split by <c>|</c>, version, the five tones split by spaces or nothing); <c># </c> lines and blank ones skipped. The seam
    /// the tests feed; <see cref="Shipped"/> is this over the manifest. Pure.
    /// </summary>
    public static EmojiCatalog Parse(string tsv)
    {
        ArgumentNullException.ThrowIfNull(tsv);
        var entries = new List<EmojiEntry>();
        var groups = new List<(string Name, List<string> Subgroups)>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string[] lines = tsv.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].TrimEnd('\r');
            // A comment is "# …": the keycap #️⃣ starts its row with a # too (found in the first look, the catalog one short).
            if (line.Length == 0 || (line[0] == '#' && (line.Length == 1 || line[1] == ' ')))
            {
                continue;
            }

            string[] f = line.Split('\t');
            string? problem = f.Length != 7 ? EmojiText.BadColumns(f.Length)
                : f[0].Length == 0 || f[1].Length == 0 || f[2].Length == 0 || f[3].Length == 0 ? EmojiText.EmptyField
                : !seen.Add(f[0]) ? EmojiText.DuplicateRow(f[0])
                : null;
            string[] tones = f.Length == 7 ? f[6].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            if (problem is null && tones.Length is not (0 or ToneCount))
            {
                problem = EmojiText.BadTones(tones.Length);
            }

            if (problem is not null)
            {
                problems.Add(EmojiText.Problem(n + 1, problem));
                continue;
            }

            var keywords = f[4].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            entries.Add(new EmojiEntry(f[0], f[1], f[2], f[3], keywords, f[5], tones, entries.Count));
            if (groups.Count == 0 || groups[^1].Name != f[1])
            {
                groups.Add((f[1], []));
            }

            if (groups[^1].Subgroups.Count == 0 || groups[^1].Subgroups[^1] != f[2])
            {
                groups[^1].Subgroups.Add(f[2]);
            }
        }

        return new EmojiCatalog(entries, [.. groups.Select(g => new EmojiGroup(g.Name, g.Subgroups))], problems);
    }

    /// <summary><paramref name="emoji"/> without its U+FE0F selectors: the key a typed or pasted spelling is matched by.</summary>
    public static string Bare(string emoji) => emoji.Replace("\uFE0F", "", StringComparison.Ordinal);

    private static EmojiCatalog LoadEmbedded()
    {
        var assembly = typeof(EmojiCatalog).Assembly;
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == ResourceName);
        using var stream = resource is null ? null : assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            DiagnosticLog.Warn(Category, EmojiText.Missing);
            return Parse("");
        }

        using var reader = new StreamReader(stream);
        var catalog = Parse(reader.ReadToEnd());
        foreach (string problem in catalog.Problems)
        {
            DiagnosticLog.Warn(Category, problem);
        }

        return catalog;
    }
}
