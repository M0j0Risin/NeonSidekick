namespace NeonSidekick.Emojis;

/// <summary>
/// The picker's "For you" tab (2026-10-10, the user's pick): emoji whose name or keywords match the words of the profile's name
/// (split at <c>-</c>, <c>_</c>, spaces, digits and camelCase humps) and of its persona's first <see cref="PersonaChars"/> characters,
/// less the words every persona has. Each word's matches (a name hit before a keyword hit, then catalog order) are taken in turn,
/// the name's words before the persona's, up to <see cref="Limit"/>, so every word gets its best few. A profile <c>chef</c> gets the
/// cook, the cooking pan, the knife… Pure.
/// </summary>
public static class EmojiSuggest
{
    /// <summary>How many suggestions at most.</summary>
    public const int Limit = 40;

    /// <summary>How much of the persona is read: its opening says who the profile is; the rest is rules.</summary>
    public const int PersonaChars = 4096;

    // The words a persona says whatever it is about, and the ones too short or common to say anything (CLDR's "face" is on a
    // hundred smileys).
    private static readonly HashSet<string> s_stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "you", "your", "yours", "are", "was", "were", "will", "would", "can", "could",
        "should", "must", "may", "might", "not", "but", "from", "into", "about", "over", "under", "when", "what", "who", "whom",
        "which", "where", "why", "how", "have", "has", "had", "its", "it's", "they", "them", "their", "there", "here", "then",
        "than", "each", "every", "all", "any", "some", "more", "most", "less", "only", "just", "very", "also", "too", "always",
        "never", "keep", "use", "using", "used", "make", "like", "one", "two", "our", "out", "off", "own", "being", "been",
        "does", "doing", "done", "get", "got", "let", "via", "per", "assistant", "helpful", "help", "helps", "answer", "answers",
        "user", "users", "model", "reply", "replies", "respond", "response", "responses", "question", "questions", "ask", "asks",
        "tool", "tools", "call", "calls", "please", "short", "brief", "clear", "concise", "plain", "text", "say", "says", "tell",
        "write", "writes", "role", "persona", "profile", "default", "face", "sign", "symbol", "mark", "button", "neon", "sidekick",
    };

    /// <summary>The emoji suggested for a profile named <paramref name="profileName"/> with <paramref name="persona"/> (null for none).</summary>
    public static IReadOnlyList<EmojiEntry> For(string profileName, string? persona, IReadOnlyList<EmojiEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        ArgumentNullException.ThrowIfNull(entries);
        var nameWords = Useful(SplitName(profileName));
        string head = persona is null ? "" : persona.Length > PersonaChars ? persona[..PersonaChars] : persona;
        var personaWords = Useful(EmojiSearch.Words(head)).Where(w => !nameWords.Contains(w)).ToList();
        if (nameWords.Count == 0 && personaWords.Count == 0)
        {
            return [];
        }

        // Each emoji's words once: its name's apart from its keywords'. Within a word's list the emoji named just that comes first
        // ("cat" is the cat, before ten cat faces), then any other name holding it, then a keyword.
        var names = entries.Select(e => new HashSet<string>(EmojiSearch.Words(e.Name), StringComparer.Ordinal)).ToList();
        var keywords = entries.Select(e => new HashSet<string>(e.Keywords.SelectMany(EmojiSearch.Words), StringComparer.Ordinal)).ToList();
        List<EmojiEntry> Matches(string word) =>
            [.. Enumerable.Range(0, entries.Count)
                .Where(i => Hit(word, names[i]) || Hit(word, keywords[i]))
                .OrderBy(i => names[i].Count == 1 && Hit(word, names[i]) ? 0 : Hit(word, names[i]) ? 1 : 2).ThenBy(i => entries[i].Order)
                .Select(i => entries[i])];

        // Taken a word at a time, round and round (the first look in Windows Terminal, later the same day: a persona that "loves"
        // filled the tab with hearts, one sort by score leaving the cat, the ocean and the coffee past the cut); the name's words
        // all go round before the persona's.
        var picked = new List<EmojiEntry>();
        var seen = new HashSet<EmojiEntry>();
        foreach (var words in (List<string>[])[nameWords, personaWords])
        {
            var lists = words.Select(Matches).Where(l => l.Count > 0).ToList();
            for (int round = 0; picked.Count < Limit && lists.Any(l => round < l.Count); round++)
            {
                foreach (var list in lists)
                {
                    if (round < list.Count && picked.Count < Limit && seen.Add(list[round]))
                    {
                        picked.Add(list[round]);
                    }
                }
            }
        }

        return picked;
    }

    /// <summary>A profile name's words: <c>ChefBot-2_home</c> is chef, bot, home. Lowercase.</summary>
    public static List<string> SplitName(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool hump = char.IsUpper(c) && i > 0 && char.IsLower(name[i - 1]);
            if (!char.IsLetter(c) || hump)
            {
                Flush();
            }

            if (char.IsLetter(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
        }

        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }

    // The words worth matching, in the order said, each once.
    private static List<string> Useful(IEnumerable<string> words) =>
        [.. words.Where(w => w.Length >= 3 && !s_stop.Contains(w) && !w.All(char.IsDigit)).Distinct(StringComparer.Ordinal)];

    // A word or its singular (a persona's "cats" is the cat's word).
    private static bool Hit(string word, HashSet<string> words) =>
        words.Contains(word) || (word.Length > 3 && word[^1] == 's' && words.Contains(word[..^1]));
}
