using System.Globalization;
using NeonSidekick.UI;

namespace NeonSidekick.Camera;

/// <summary>A <c>/camera</c> word.</summary>
public enum CameraVerb
{
    /// <summary>The bare word: the shutter pane.</summary>
    Shutter,
    Snap,
    List,
    Use,
    Live,
    Watch,
    Off,

    /// <summary>A word <c>/camera</c> does not know (the error says so).</summary>
    Unknown,
}

/// <summary>A <c>/camera</c> line read: the verb, what follows it, and the error for a line that is no command.</summary>
public sealed record CameraCommandLine(CameraVerb Verb, string Argument, string? Error = null);

/// <summary>
/// <c>/camera</c>'s words (2026-10-02), pure where it can be: <see cref="Parse"/> the line, <see cref="Find"/> a camera by number
/// or name, <see cref="ListAsync"/> the list's lines, <see cref="Complete"/> the input line's argument list.
/// </summary>
public static class CameraCommand
{
    /// <summary>The words in the order the completion offers them, with their notes.</summary>
    public static readonly IReadOnlyList<(string Word, string Note)> Words =
    [
        ("snap", "take a photo at once and put it on the input line"),
        ("list", OperatingSystem.IsMacOS() ? "the cameras macOS lists" : "the cameras Windows lists"),   // the Mac's since 2026-10-06; AVFoundation's since 2026-10-07
        ("use", "choose a camera: /camera use <n|name>"),
        ("live", "the camera live in a window of its own"),
        ("watch", "watch the camera: a change rides your next message; /camera watch [seconds|off]"),
        ("off", "let the camera go (live and watch)"),
    ];

    public static CameraCommandLine Parse(string? args)
    {
        string text = (args ?? "").Trim();
        if (text.Length == 0)
        {
            return new CameraCommandLine(CameraVerb.Shutter, "");
        }

        int space = text.IndexOf(' ', StringComparison.Ordinal);
        string word = (space < 0 ? text : text[..space]).ToLowerInvariant();
        string rest = space < 0 ? "" : text[(space + 1)..].Trim();
        var verb = word switch
        {
            "snap" => CameraVerb.Snap,
            "list" => CameraVerb.List,
            "use" => CameraVerb.Use,
            "live" => CameraVerb.Live,
            "watch" => CameraVerb.Watch,
            "off" => CameraVerb.Off,
            _ => CameraVerb.Unknown,
        };
        return verb switch
        {
            CameraVerb.Unknown => new CameraCommandLine(verb, rest, CameraText.Unknown(word)),
            CameraVerb.Use when rest.Length == 0 => new CameraCommandLine(CameraVerb.Unknown, "", CameraText.Usage),
            CameraVerb.Snap or CameraVerb.List or CameraVerb.Live or CameraVerb.Off when rest.Length > 0 => new CameraCommandLine(CameraVerb.Unknown, rest, CameraText.Usage),
            _ => new CameraCommandLine(verb, rest),
        };
    }

    /// <summary>The camera <paramref name="what"/> names: its number in the list (from 1), else its name (any case), else the one name it begins (alone). Null for none.</summary>
    public static CameraDevice? Find(IReadOnlyList<CameraDevice> devices, string what)
    {
        ArgumentNullException.ThrowIfNull(devices);
        string wanted = (what ?? "").Trim().Trim('"');
        if (int.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return number >= 1 && number <= devices.Count ? devices[number - 1] : null;
        }

        var exact = devices.FirstOrDefault(d => string.Equals(d.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null || wanted.Length == 0)
        {
            return exact;
        }

        var prefixed = devices.Where(d => d.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefixed.Count == 1 ? prefixed[0] : null;
    }

    /// <summary>The list's lines (text, whether an error): the header, one numbered line per camera, the chosen one marked, or the failure.</summary>
    public static async Task<IReadOnlyList<(string Text, bool Error)>> ListAsync(CameraSession camera, string? chosen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(camera);
        IReadOnlyList<CameraDevice> devices;
        try
        {
            devices = await Task.Run(camera.List, cancellationToken).ConfigureAwait(false);
        }
        catch (CameraException e)
        {
            return [(e.Message, true)];
        }

        if (devices.Count == 0)
        {
            return [(CameraText.Failure(CameraFailure.NoCamera, null), true)];
        }

        var lines = new List<(string, bool)> { (CameraText.ListHeader, false) };
        var current = CameraSession.Choose(devices, chosen, out bool fellBack);
        for (int i = 0; i < devices.Count; i++)
        {
            lines.Add((CameraText.ListLine(i + 1, devices[i], ReferenceEquals(devices[i], current)), false));
        }

        if (string.IsNullOrWhiteSpace(chosen))
        {
            lines.Add((CameraText.ListFirstNote, false));
        }
        else if (fellBack)
        {
            lines.Add((CameraText.FellBack(chosen, current!.Name), false));
        }

        return lines;
    }

    /// <summary>The input line's list after <c>/camera </c>: the words, then <c>off</c> after <c>watch</c>. Pure.</summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        if (argText.StartsWith("watch ", StringComparison.OrdinalIgnoreCase))
        {
            return argText.Count(c => c == ' ') > 1 ? [] : MentionCompleter.Matches([new("watch off", "stop watching")], argText);
        }

        return argText.Contains(' ', StringComparison.Ordinal) ? [] : MentionCompleter.Matches(Words.Select(w => new CompletionItem(w.Word, w.Note)).ToList(), argText);
    }
}
