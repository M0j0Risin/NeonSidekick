// EmojiGen: writes assets/emoji/emoji.tsv, the emoji catalog the avatar picker reads (2026-10-10, the user's ask: a
// profile's avatar picked from the whole emoji set, by name, keyword and category). Embedded as emoji/emoji.tsv and read
// by src/NeonSidekick/Emoji/EmojiCatalog.cs.
//
// Sources, both under the Unicode License v3:
//   - Unicode's emoji-test.txt: every fully-qualified emoji in CLDR order, with its group, subgroup, name and version.
//   - CLDR's annotations (common/annotations/en.xml and common/annotationsDerived/en.xml): the keywords. CLDR keys them
//     without the U+FE0F presentation selector, so the lookup drops it.
//
// What is left out (the user's call: show only what Windows Terminal draws): the Component group (skin-tone and hair
// swatches), the country-flag and subdivision-flag subgroups (Windows' Segoe UI Emoji has no flags; it draws the two
// letters), anything newer than --max-version (Windows 11 draws Emoji 15.1), and every toned variant, which is folded
// into its base's tones column: the five uniform tones (light to dark) exactly as emoji-test spells them, so the app
// never works out where a modifier goes in a ZWJ sequence. Mixed-tone sequences (two people, two tones) are dropped.
//
// One row per base emoji, tab-separated, "# " lines are comments (the keycap #️⃣ starts a row with a bare #):
//     emoji  group  subgroup  name  keywords (| separated)  version  tones (space separated, or empty)
//
// A .NET 10 file-based app: no csproj, not in the solution, not run by build.ps1. The output is committed; run this
// only to pick up a new emoji or CLDR release, from the repository root:
//
//     dotnet run tools/EmojiGen.cs
//     dotnet run tools/EmojiGen.cs -- --test <emoji-test.txt> --ann <en.xml> --derived <en.xml> [--max-version 15.1] [--out <path>]

using System.Globalization;
using System.Text;
using System.Xml.Linq;

const string EmojiVersion = "15.1";
const string CldrRelease = "release-46";
string testUrl = $"https://unicode.org/Public/emoji/{EmojiVersion}/emoji-test.txt";
string annUrl = $"https://raw.githubusercontent.com/unicode-org/cldr/{CldrRelease}/common/annotations/en.xml";
string derivedUrl = $"https://raw.githubusercontent.com/unicode-org/cldr/{CldrRelease}/common/annotationsDerived/en.xml";

var outPath = Arg("--out") ?? "assets/emoji/emoji.tsv";
decimal maxVersion = decimal.Parse(Arg("--max-version") ?? EmojiVersion, CultureInfo.InvariantCulture);

using var http = new HttpClient();
string test = Arg("--test") is { } t ? File.ReadAllText(t) : await http.GetStringAsync(testUrl);
string ann = Arg("--ann") is { } a ? File.ReadAllText(a) : await http.GetStringAsync(annUrl);
string derived = Arg("--derived") is { } d ? File.ReadAllText(d) : await http.GetStringAsync(derivedUrl);

// Keywords by the emoji without U+FE0F; the derived file covers the sequences (toned, ZWJ) the plain one does not.
var keywords = new Dictionary<string, string[]>(StringComparer.Ordinal);
foreach (var xml in new[] { ann, derived })
{
    foreach (var node in XDocument.Parse(xml).Descendants("annotation"))
    {
        if ((string?)node.Attribute("type") == "tts")
        {
            continue;
        }

        string cp = Bare((string)node.Attribute("cp")!);
        var words = node.Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant()).ToArray();
        keywords.TryAdd(cp, words);
    }
}

string testVersion = test.Split('\n').FirstOrDefault(l => l.StartsWith("# Version:", StringComparison.Ordinal))?[10..].Trim() ?? "unknown";

var rows = new List<Row>();
var byBare = new Dictionary<string, Row>(StringComparer.Ordinal);
string group = "", subgroup = "";
int dropped = 0, toned = 0;
foreach (var raw in test.Split('\n'))
{
    string line = raw.TrimEnd('\r');
    if (line.StartsWith("# group:", StringComparison.Ordinal))
    {
        group = line[8..].Trim();
        continue;
    }

    if (line.StartsWith("# subgroup:", StringComparison.Ordinal))
    {
        subgroup = line[11..].Trim();
        continue;
    }

    if (line.Length == 0 || line[0] == '#')
    {
        continue;
    }

    // 1F600 ; fully-qualified # 😀 E1.0 grinning face
    int semi = line.IndexOf(';');
    int hash = line.IndexOf('#');
    if (semi < 0 || hash < semi || line[semi..hash].Trim(';', ' ') != "fully-qualified")
    {
        continue;
    }

    var cps = line[..semi].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
    string emoji = string.Concat(cps.Select(char.ConvertFromUtf32));
    string comment = line[(hash + 1)..].Trim();
    // "😀 E1.0 grinning face": the emoji, the version, the name.
    var parts = comment.Split(' ', 3);
    decimal version = decimal.Parse(parts[1].TrimStart('E'), CultureInfo.InvariantCulture);
    string name = parts[2];

    if (group == "Component" || subgroup is "country-flag" or "subdivision-flag" || version > maxVersion)
    {
        dropped++;
        continue;
    }

    var tones = cps.Where(c => c is >= 0x1F3FB and <= 0x1F3FF).Distinct().ToArray();
    if (tones.Length > 0)
    {
        // A toned variant: folded into its base when the tone is uniform, dropped when mixed or baseless.
        string bare = Bare(string.Concat(cps.Where(c => c is < 0x1F3FB or > 0x1F3FF).Select(char.ConvertFromUtf32)));
        if (tones.Length == 1 && byBare.TryGetValue(bare, out var baseRow))
        {
            baseRow.Tones[tones[0] - 0x1F3FB] = emoji;
            toned++;
        }
        else
        {
            dropped++;
        }

        continue;
    }

    var row = new Row(emoji, group, subgroup, name, version, new string?[5]);
    rows.Add(row);
    byBare.TryAdd(Bare(emoji), row);
}

var sb = new StringBuilder();
sb.Append("# Generated by tools/EmojiGen.cs from Unicode emoji-test.txt ").Append(testVersion)
  .Append(" and CLDR ").Append(CldrRelease).Append(" annotations (Unicode License v3). Do not edit; rerun the tool.\n");
sb.Append("# emoji\tgroup\tsubgroup\tname\tkeywords\tversion\ttones\n");
int withKeywords = 0;
foreach (var row in rows)
{
    var words = keywords.TryGetValue(Bare(row.Emoji), out var found) ? found : [];
    if (words.Length > 0)
    {
        withKeywords++;
    }

    // A partial tone set (none expected for uniform tones) is left out whole: the picker offers all five or none.
    string tones = row.Tones.All(x => x is not null) ? string.Join(' ', row.Tones) : "";
    sb.Append(row.Emoji).Append('\t')
      .Append(row.Group).Append('\t')
      .Append(row.Subgroup).Append('\t')
      .Append(Clean(row.Name)).Append('\t')
      .Append(string.Join('|', words.Select(Clean).Where(w => w.Length > 0 && w != row.Name))).Append('\t')
      .Append(row.Version.ToString("0.0", CultureInfo.InvariantCulture)).Append('\t')
      .Append(tones).Append('\n');
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"Wrote {outPath}: {rows.Count} emoji ({rows.Count(r => r.Tones[0] is not null)} with skin tones, {toned} toned forms folded, " +
    $"{withKeywords} with keywords, {dropped} left out), emoji-test {testVersion}, CLDR {CldrRelease}, max version {maxVersion.ToString(CultureInfo.InvariantCulture)}.");
return 0;

static string Bare(string s) => s.Replace("\uFE0F", "", StringComparison.Ordinal);

static string Clean(string s) => s.Replace('\t', ' ').Replace('|', '/').Trim();

string? Arg(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return null;
}

sealed record Row(string Emoji, string Group, string Subgroup, string Name, decimal Version, string?[] Tones);
