namespace NeonSidekick.UI;

/// <summary>
/// The type-ahead of a list (2026-10-03, out of <c>FolderTree.JumpFrom</c> when the theme pickers took it too, the user's ask):
/// one rule for every list that jumps on a typed character. Pure.
/// </summary>
public static class TypeAhead
{
    /// <summary>
    /// The next row after <paramref name="row"/> of <paramref name="count"/>, wrapping at the end, whose name
    /// (<paramref name="nameAt"/>) starts with <paramref name="c"/>, case folded; −1 when no other row does. A row before the
    /// first (<paramref name="row"/> &lt; 0) starts the search at row 0.
    /// </summary>
    public static int Next(int count, Func<int, string> nameAt, int row, char c)
    {
        ArgumentNullException.ThrowIfNull(nameAt);
        if (count <= 0)
        {
            return -1;
        }

        string prefix = c.ToString();
        int from = row < 0 ? -1 : row;
        int steps = row < 0 ? count : count - 1;
        for (int step = 1; step <= steps; step++)
        {
            int i = (from + step) % count;
            if (nameAt(i).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
