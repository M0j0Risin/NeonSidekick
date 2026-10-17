namespace NeonSidekick.Shopping;

/// <summary>
/// The file and vault tools' tripwire (2026-10-17, the user's report: "add milk to my shopping list" now and then went to a file in the
/// working directory): while the shopping tools are offered, a write onto a file or note named as a shopping or grocery list is refused with
/// a pointer to <c>shopping_add</c> (<see cref="ShoppingText.NotAFile"/>), so a model that slips learns the right tool within the same turn.
/// The name alone decides, read in words — split at spaces, <c>-</c>, <c>_</c>, <c>.</c> and lower-to-upper case changes: the run
/// <c>shopping list</c> or <c>grocery list</c> anywhere, the word <c>groceries</c> anywhere, or a name that is only <c>shopping</c> or
/// <c>grocery</c>. So <c>my-shopping-list-oct.md</c> and <c>Groceries 2026-10-17.md</c> are caught while <c>ShoppingListener.ts</c> and
/// <c>shopping-cart.cs</c> pass. The app's own writes (<c>/shopping export</c>'s <c>shopping-list-&lt;time&gt;.md</c>) never come here. Pure.
/// </summary>
public static class ShoppingFileGuard
{
    /// <summary>Whether the last segment of <paramref name="path"/>, its extension off, names a shopping or grocery list.</summary>
    public static bool IsShoppingListName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string trimmed = path.TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        string name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;
        var words = Words(stem);
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i] == "groceries")
            {
                return true;
            }

            if (words[i] is "shopping" or "grocery" && i + 1 < words.Count && words[i + 1] == "list")
            {
                return true;
            }
        }

        return words is ["shopping" or "grocery"];
    }

    /// <summary>The name's words, lower-cased: runs of letters and digits, a lower-case letter followed by an upper-case one starting a new word.</summary>
    private static List<string> Words(string stem)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        char previous = '\0';
        foreach (char c in stem)
        {
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
            }
            else
            {
                if (char.IsUpper(c) && char.IsLower(previous))
                {
                    Flush();
                }

                word.Append(char.ToLowerInvariant(c));
            }

            previous = c;
        }

        Flush();
        return words;

        void Flush()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
    }
}
