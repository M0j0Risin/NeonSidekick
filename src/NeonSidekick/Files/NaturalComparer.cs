namespace NeonSidekick.Files;

/// <summary>
/// Compares labels with their digit runs as numbers (Aisle 2 before Aisle 10), the rest without case. Pure. Moved here from the shopping
/// list 2026-11-17 (the admin-themes-render review's #137), so a collage orders a folder's pictures the same way (shot-2 before shot-10).
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    j++;
                }

                string a = x[si..i].TrimStart('0'), b = y[sj..j].TrimStart('0');
                int byLength = a.Length.CompareTo(b.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                int byDigits = string.CompareOrdinal(a, b);
                if (byDigits != 0)
                {
                    return byDigits;
                }

                continue;
            }

            int byChar = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
            if (byChar != 0)
            {
                return byChar;
            }

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
