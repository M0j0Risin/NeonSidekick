using System.Text;

namespace NeonSidekick.App;

/// <summary>
/// The app's embedded text assets read one way (2026-10-13, the code review: the fun verbs, the shell police's starter list and the
/// default persona each had a copy of the stream, the reader and the line split): <see cref="Read"/> a resource whole, as UTF-8 (a BOM
/// skipped), and <see cref="Lines"/> a text's lines trimmed with the blanks left out. A missing resource throws, naming it; the build
/// embeds every one, and the smoke run on the published exe would catch one lost.
/// </summary>
internal static class EmbeddedText
{
    /// <summary>The resource <paramref name="name"/> whole; <paramref name="what"/> names it in the error when it is not embedded.</summary>
    public static string Read(string name, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var stream = typeof(EmbeddedText).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(what + " " + name + " is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary><paramref name="text"/>'s lines (CRLF or LF), each trimmed, the blank ones left out, in order. Pure.</summary>
    public static IEnumerable<string> Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }
}
