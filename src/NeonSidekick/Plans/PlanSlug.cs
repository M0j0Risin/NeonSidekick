using System.Globalization;
using System.Text;

namespace NeonSidekick.Plans;

/// <summary>
/// A plan's file name (2026-09-26): <c>plans/&lt;kebab-name&gt;.md</c> under the working directory,
/// the name a short description of what the plan does. <see cref="From"/> makes the kebab case out
/// of whatever the model sent — accents folded to their letters, everything else but ASCII letters
/// and digits a single hyphen, at most <see cref="MaxLength"/> characters cut back to a word, never a
/// reserved Windows device name, <see cref="Fallback"/> when nothing is left. <see cref="Choose"/>
/// picks the first path not taken (<c>-2</c>, <c>-3</c> …); the plan keeps that path for every
/// revision (<see cref="PlanSession.Path"/>), so a revision never lands beside its own earlier copy. Pure.
/// </summary>
public static class PlanSlug
{
    /// <summary>The folder under the working directory the plans go in.</summary>
    public const string Folder = "plans";

    public const string Extension = ".md";

    public const int MaxLength = 60;

    /// <summary>The name when the text holds no letter or digit.</summary>
    public const string Fallback = "plan";

    /// <summary>The most suffixes tried before the name gains the time instead.</summary>
    public const int MaxSuffix = 999;

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>The kebab-case name for <paramref name="text"/>.</summary>
    public static string From(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Fallback;
        }

        var sb = new StringBuilder(text.Length);
        bool hyphen = false;
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            char lower = char.ToLowerInvariant(c);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (hyphen && sb.Length > 0)
                {
                    sb.Append('-');
                }

                sb.Append(lower);
                hyphen = false;
            }
            else
            {
                hyphen = true;
            }
        }

        string slug = sb.ToString();
        if (slug.Length > MaxLength)
        {
            // Cut back to the last whole word when that keeps at least half the room.
            int cut = slug.LastIndexOf('-', MaxLength);
            slug = cut >= MaxLength / 2 ? slug[..cut] : slug[..MaxLength].TrimEnd('-');
        }

        if (slug.Length == 0)
        {
            return Fallback;
        }

        return Reserved.Contains(slug) ? slug + "-" + Fallback : slug;
    }

    /// <summary>The relative path of <paramref name="slug"/>'s file, with a numbered suffix when <paramref name="suffix"/> is over 1.</summary>
    public static string PathFor(string slug, int suffix = 1)
    {
        ArgumentNullException.ThrowIfNull(slug);
        return suffix <= 1 ? $"{Folder}/{slug}{Extension}" : $"{Folder}/{slug}-{suffix.ToString(CultureInfo.InvariantCulture)}{Extension}";
    }

    /// <summary>
    /// The first of <c>plans/&lt;slug&gt;.md</c>, <c>plans/&lt;slug&gt;-2.md</c> … that <paramref name="taken"/>
    /// says is free; past <see cref="MaxSuffix"/>, the name gains <paramref name="stamp"/>.
    /// </summary>
    public static string Choose(string slug, Func<string, bool> taken, string stamp)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(taken);
        for (int suffix = 1; suffix <= MaxSuffix; suffix++)
        {
            string path = PathFor(slug, suffix);
            if (!taken(path))
            {
                return path;
            }
        }

        return PathFor(slug + "-" + stamp);
    }
}
