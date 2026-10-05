#:project ../src/NeonSidekick/NeonSidekick.csproj
#:property PublishAot=false

// ThemeAtlas: writes the data of assets/themes/Theme Atlas.html from the theme files beside it, so the atlas shows every built-in
// theme as the app resolves it (2026-10-05, the user's ask: the themes became assets/themes' files, embedded as the built-ins, so a
// theme is added, changed or removed by a file edit; the atlas was kept by hand until then and ThemeLibraryTests' drift test now
// fails when it falls behind).
//
//     assets/themes/Theme Atlas.html     the THEMES line (every theme: its colours, gradient, banner letters, rule, every resolved
//                                        style and its own style changes), the FILTERS list (one per category folder), the header's
//                                        theme, category and style counts and the app's version; the page around them is kept
//
// The themes are loaded by the app's own ThemeLibrary.Load over the files on disk (not the embedded copies, so an edit shows without
// a build of the app first), and every colour comes from the app's Theme code: the banner from Theme.GradientMarkup over
// SidekickApp.BannerTitle, the rule from Theme.Rule's five segments, the styles from Theme.StylesOf. A file the app would skip stops
// the run with its problem. A category keeps its place in FILTERS; a new folder is added after the rest, A to Z.
//
// A .NET 10 file-based app: no csproj, not in the solution, not run by build.ps1. The output is committed; run this after any change
// under assets/themes, from the repository root:
//
//     dotnet run tools/ThemeAtlas.cs
//     dotnet run tools/ThemeAtlas.cs -- --themes <folder> --out <page>

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.UI;
using Spectre.Console;

string themesDir = Path.GetFullPath(Arg("--themes") ?? Path.Combine("assets", "themes"));
string pagePath = Path.GetFullPath(Arg("--out") ?? Path.Combine(themesDir, "Theme Atlas.html"));

// The files, as the csproj embeds them: themes/<category>/<file>.json.
var resources = new List<KeyValuePair<string, string>>();
var rawStyles = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
foreach (string file in Directory.EnumerateFiles(themesDir, "*.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
{
    string relative = Path.GetRelativePath(themesDir, file).Replace('\\', '/');
    string text = File.ReadAllText(file);
    resources.Add(new(ThemeLibrary.ResourcePrefix + relative, text));
    using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    if (doc.RootElement.TryGetProperty("styles", out var styles) && styles.ValueKind == JsonValueKind.Object && styles.EnumerateObject().Any())
    {
        rawStyles[Path.GetFileNameWithoutExtension(file)] = styles.Clone();
    }
}

var loaded = ThemeLibrary.Load(resources);
if (loaded.Problems.Count > 0)
{
    foreach (var problem in loaded.Problems)
    {
        Console.Error.WriteLine(ThemeText.Problem(problem.Shown ?? problem.FilePath, problem.Problem));
    }

    Console.Error.WriteLine("The atlas was not written: fix the files above first.");
    return 1;
}

string page = File.ReadAllText(pagePath);

// THEMES: one compact line, A to Z (the page sorts by category itself).
var buffer = new MemoryStream();
using (var json = new Utf8JsonWriter(buffer))
{
    json.WriteStartArray();
    foreach (var theme in loaded.Themes)
    {
        WriteTheme(json, theme, loaded.Categories[theme.Name]);
    }

    json.WriteEndArray();
}

string themesLine = "const THEMES = " + Encoding.UTF8.GetString(buffer.ToArray()) + ";";
page = Replace(page, new Regex(@"^const THEMES = \[.*\];$", RegexOptions.Multiline), themesLine, "the THEMES line");

// FILTERS: the categories in their old order, new ones after, gone ones out.
var block = new Regex(@"const FILTERS = \[\n(?<body>.*?)\n\];", RegexOptions.Singleline);
var oldPairs = Regex.Matches(block.Match(page.Replace("\r\n", "\n")).Groups["body"].Value, "\\[\"(?<key>[^\"]+)\", \"(?<label>[^\"]+)\"\\]")
    .Select(m => (Key: m.Groups["key"].Value, Label: m.Groups["label"].Value))
    .ToList();
var categories = loaded.Categories.Values.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
var pairs = new List<(string Key, string Label)> { ("all", "All") };
pairs.AddRange(oldPairs.Where(p => categories.Contains(p.Key)));
pairs.AddRange(categories.Where(c => pairs.All(p => p.Key != c)).Order(StringComparer.Ordinal)
    .Select(c => (c, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c))));
string filters = "const FILTERS = [\n  " + string.Join(",\n  ", pairs.Chunk(5).Select(chunk => string.Join(", ", chunk.Select(p => $"[\"{p.Key}\", \"{p.Label}\"]")))) + ",\n];";
string newline = page.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
page = Replace(page, new Regex(@"const FILTERS = \[\r?\n.*?\r?\n\];", RegexOptions.Singleline), filters.Replace("\n", newline), "the FILTERS list");

// The header's counts and the version (the banner's too).
string version = SidekickApp.Version;
page = Replace(page, new Regex(@"<span id=""count"">\d+</span> themes · \w+ categories"),
    $"<span id=\"count\">{loaded.Themes.Count}</span> themes · {Words(categories.Count)} categories", "the header's counts");
page = Replace(page, new Regex(@"NeonSidekick \d+\.\d+\.\d+ ·"), $"NeonSidekick {version} ·", "the header's version");
page = Replace(page, new Regex(@"all \d+ styles"), $"all {Enum.GetValues<ThemeStyleSlot>().Length} styles", "the lede's style count");
page = Replace(page, new Regex(@"""  v\d+\.\d+\.\d+"""), $"\"  v{version}\"", "the banner's version");

File.WriteAllText(pagePath, page);
Console.WriteLine($"{Path.GetRelativePath(Environment.CurrentDirectory, pagePath)}: {loaded.Themes.Count} themes in {categories.Count} categories.");
return 0;

void WriteTheme(Utf8JsonWriter json, ThemePalette theme, string category)
{
    json.WriteStartObject();
    json.WriteString("n", theme.Name);
    json.WriteString("d", theme.Description);
    json.WriteString("cat", category);

    json.WriteStartObject("c");
    for (int i = 0; i < ThemeKeys.Colors.Count; i++)
    {
        json.WriteString(ThemeKeys.Colors[i], Theme.ToHex(theme.ColorOf((ThemeColorSlot)i)));
    }

    json.WriteEndObject();

    json.WriteStartArray("gr");
    foreach (var stop in theme.GradientStops)
    {
        json.WriteStringValue(Theme.ToHex(stop));
    }

    json.WriteEndArray();

    // The banner letters and the rule's five segments, read back from the markup the app draws them with.
    json.WriteStartArray("t");
    foreach (var (hex, text) in Segments(Theme.GradientMarkup(SidekickApp.BannerTitle, theme.GradientStops)))
    {
        json.WriteStartArray();
        json.WriteStringValue(hex);
        json.WriteStringValue(text);
        json.WriteEndArray();
    }

    json.WriteEndArray();

    json.WriteStartArray("r");
    foreach (var (hex, _) in Segments(Theme.Rule(5, theme.GradientStops)))
    {
        json.WriteStringValue(hex);
    }

    json.WriteEndArray();

    // Every style as the app hands it out: [fg, bg or null, flags] — b bold, i italic, u underline, s strikethrough, d dim.
    var styles = Theme.StylesOf(theme);
    json.WriteStartObject("s");
    foreach (var slot in Enum.GetValues<ThemeStyleSlot>())
    {
        var style = styles(slot);
        json.WriteStartArray(ThemeKeys.Of(slot));
        json.WriteStringValue(Theme.ToHex(style.Foreground));
        if (style.Background == Color.Default)
        {
            json.WriteNullValue();
        }
        else
        {
            json.WriteStringValue(Theme.ToHex(style.Background));
        }

        string flags = Flags(style.Decoration);
        if (flags.Length > 0)
        {
            json.WriteStringValue(flags);
        }

        json.WriteEndArray();
    }

    json.WriteEndObject();

    if (rawStyles.TryGetValue(theme.Name, out var own))
    {
        json.WritePropertyName("o");
        own.WriteTo(json);
    }

    json.WriteEndObject();
}

static IEnumerable<(string Hex, string Text)> Segments(string markup) =>
    Regex.Matches(markup, @"\[(?<hex>#[0-9A-F]{6})\](?<text>.*?)\[/\]")
        .Select(m => (m.Groups["hex"].Value, m.Groups["text"].Value.Replace("[[", "[").Replace("]]", "]")));

static string Flags(Decoration decoration)
{
    var flags = new StringBuilder();
    if (decoration.HasFlag(Decoration.Bold)) flags.Append('b');
    if (decoration.HasFlag(Decoration.Italic)) flags.Append('i');
    if (decoration.HasFlag(Decoration.Underline)) flags.Append('u');
    if (decoration.HasFlag(Decoration.Strikethrough)) flags.Append('s');
    if (decoration.HasFlag(Decoration.Dim)) flags.Append('d');
    return flags.ToString();
}

static string Words(int n) => n switch
{
    1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine",
    10 => "ten", 11 => "eleven", 12 => "twelve",
    _ => n.ToString(CultureInfo.InvariantCulture),
};

static string Replace(string text, Regex pattern, string replacement, string what)
{
    if (!pattern.IsMatch(text))
    {
        throw new InvalidOperationException($"The page has no {what} to replace.");
    }

    return pattern.Replace(text, replacement.Replace("$", "$$"), 1);
}

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
