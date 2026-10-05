using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.UI;

/// <summary>
/// The source-generated context for a theme file, <c>&lt;home&gt;/themes/*.json</c> (2026-10-01) and the embedded built-ins (2026-10-05); a sibling of
/// <c>McpJsonContext</c> with its options: camelCase keys, comments and a trailing comma tolerated on the way in (a
/// hand-edited file), indented on the way out (<c>/theme export</c>). Reflection serialisation is off.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ThemeFileData))]
[JsonSerializable(typeof(ThemeStyleData))]
public sealed partial class ThemeJsonContext : JsonSerializerContext;

/// <summary>One theme file as written (2026-10-01): every colour role required since 2026-10-05 (<see cref="ThemeFile.Build"/>), the rest optional.</summary>
public sealed class ThemeFileData
{
    /// <summary>The name <c>/theme</c> takes; the file's stem when absent.</summary>
    public string? Name { get; set; }

    /// <summary>The note beside the name in the picker; <see cref="ThemeText.CustomDescription"/> when absent.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The theme the left-out parts came from until 2026-10-05, when every theme came to stand alone (the user's call). Read only so
    /// a file that still names one hears that it is ignored (<see cref="ThemeText.BaseIgnored"/>); never written.
    /// </summary>
    public string? Base { get; set; }

    /// <summary>Colour role (<see cref="ThemeKeys.Colors"/>) to <c>#RRGGBB</c> or <c>#RGB</c>; all fifteen required.</summary>
    public Dictionary<string, string>? Colors { get; set; }

    /// <summary>The banner's stops, left to right, 2 to 16.</summary>
    public List<string>? Gradient { get; set; }

    /// <summary>Style (<see cref="ThemeKeys.Styles"/>) to its change.</summary>
    public Dictionary<string, ThemeStyleData>? Styles { get; set; }
}

/// <summary>One style's change as written: a colour is a hex or a colour role's word; a flag true turns its decoration on, false off.</summary>
public sealed class ThemeStyleData
{
    public string? Fg { get; set; }
    public string? Bg { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }
    public bool? Dim { get; set; }
    public bool? Strikethrough { get; set; }
}
