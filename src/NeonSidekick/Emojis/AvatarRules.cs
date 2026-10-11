using NeonSidekick.UI;

namespace NeonSidekick.Emojis;

/// <summary>
/// What a profile's avatar may be (2026-10-10): one emoji, a single cluster the terminal draws two cells wide — the reply's glyph
/// and the window title's mark, so anything wider would push the reply's indent, and a word would read as text. The catalog's
/// emoji all pass; so does one it does not hold (a newer emoji pasted in), as long as it is one two-cell picture. Pure.
/// </summary>
public static class AvatarRules
{
    /// <summary>
    /// The avatar a profile wears until one is picked (later on 2026-10-10, the user's call: none until then), and the one a profile
    /// <c>/profile add</c> makes starts with. 🚫 on the picker, <c>/avatar clear</c>, still clears it: an empty value saved is none.
    /// </summary>
    public const string DefaultAvatar = "🤖";

    /// <summary>Whether <paramref name="text"/> (already trimmed) is one emoji as above.</summary>
    public static bool IsEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text) || TextCells.ClusterWidth(text, 0, out int length) != 2 || length != text.Length)
        {
            return false;
        }

        // A picture, not a wide letter: a surrogate pair (every astral emoji, a skin tone on ✌), a presentation selector or keycap (☺️ #️⃣), or
        // one of the BMP characters drawn as emoji with no selector (⭐ ⚡).
        return text.Any(char.IsSurrogate) || text.Contains('\uFE0F') || text.Contains('\u20E3') || TextCells.IsWideEmoji(text[0]);
    }

    /// <summary>
    /// The avatar as the app uses it: the setting trimmed when it is an emoji, else empty (a hand-edited profile.json holding a
    /// word, or two emoji, shows no avatar rather than a wide or broken glyph). Pure.
    /// </summary>
    public static string Normalize(string? avatar)
    {
        string text = (avatar ?? "").Trim();
        return IsEmoji(text) ? text : "";
    }
}
