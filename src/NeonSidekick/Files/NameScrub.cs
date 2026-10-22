using System.Globalization;
using System.Text;

namespace NeonSidekick.Files;

/// <summary>
/// A name as the app shows it (2026-10-22, review P5, the fix plan's mechanism 6): file, folder, share and note names went raw into results,
/// panes, the transcript and the log, so a right-to-left override or a zero-width character disguised a name (<c>invoice\u202Efdp.exe</c>), and
/// on a Mac a line break or ESC in a name forged a log line or sent the terminal a sequence (OSC 52 writes the clipboard). Bidi controls,
/// zero-width characters, control characters and ESC are written as <c>\u202E</c>-style escapes; every other character as it is. Pure.
/// </summary>
public static class NameScrub
{
    /// <summary>
    /// <paramref name="name"/> with every character <see cref="Hides"/> escaped; the same string when there is none. With
    /// <paramref name="keepLineBreaks"/> a line break and a tab stay (a log message's own).
    /// </summary>
    public static string Display(string? name, bool keepLineBreaks = false)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name ?? "";
        }

        int first = -1;
        for (int i = 0; i < name.Length; i++)
        {
            if (Hides(name[i]) && !(keepLineBreaks && name[i] is '\n' or '\r' or '\t'))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return name;
        }

        var sb = new StringBuilder(name.Length + 16).Append(name, 0, first);
        for (int i = first; i < name.Length; i++)
        {
            char c = name[i];
            if (Hides(c) && !(keepLineBreaks && c is '\n' or '\r' or '\t'))
            {
                sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Whether <paramref name="c"/> changes how the text around it shows or what a terminal does: a control character (ESC and line breaks
    /// among them), a bidi control (U+200E, U+200F, U+202A–U+202E, U+2066–U+2069, U+061C), a zero-width one (U+200B–U+200D, U+2060, U+FEFF)
    /// or a line or paragraph separator.
    /// </summary>
    public static bool Hides(char c) =>
        char.IsControl(c)
        || c is '\u200E' or '\u200F' or '\u061C' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')
        || c is (>= '\u200B' and <= '\u200D') or '\u2060' or '\uFEFF' or '\u2028' or '\u2029';
}
