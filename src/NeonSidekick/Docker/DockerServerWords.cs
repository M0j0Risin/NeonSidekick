namespace NeonSidekick.Docker;

/// <summary>
/// Which containers <c>Docker server containers</c>' checklist shows (2026-10-10, the user's ask: every container the engine has,
/// mysql_dev and postgres_dev among them, buried the few LLM servers). A container counts as an LLM server when its image or its
/// command holds one of the words of <see cref="Settings.AppSettingsData.DockerServerMatchWords"/>, case ignored, or when it carries
/// <see cref="LlmLabel"/> set to <c>true</c> (for a server built on an image no word names). The words are the user's to edit
/// (the user's pick), starting as <see cref="Defaults"/>; an empty list shows every container, as the checklist did before. The
/// list keeps the user's case, sorted A to Z ignoring case, no duplicates ignoring case: <see cref="Shell.ForbiddenStrings"/>' shape.
/// Pure.
/// </summary>
public static class DockerServerWords
{
    /// <summary>The label that makes any container an LLM server, whatever its image: <c>neonsidekick.llm=true</c>. Pinned.</summary>
    public const string LlmLabel = "neonsidekick.llm";

    /// <summary>
    /// The engines a fresh profile knows (2026-10-10), as their images and commands spell them: vLLM, SGLang, llama.cpp's server,
    /// Ollama, Hugging Face's text-generation-inference, LocalAI, LMDeploy, TensorRT-LLM, NVIDIA's NIM images, Aphrodite and
    /// KoboldCpp. A fresh list each read.
    /// </summary>
    public static List<string> Defaults => Sorted(
    [
        "vllm",
        "sglang",
        "llama-server",
        "llama.cpp",
        "ollama",
        "text-generation",
        "localai",
        "lmdeploy",
        "trtllm",
        "nvcr.io/nim",
        "aphrodite",
        "koboldcpp",
    ]);

    /// <summary>
    /// Whether <paramref name="container"/> is an LLM server: <see cref="LlmLabel"/> set to <c>true</c> (case ignored), or its image
    /// or command holding one of <paramref name="words"/> (case ignored; a blank word never matches). An empty list is not asked
    /// here: the checklist shows every container then.
    /// </summary>
    public static bool Matches(DockerContainer container, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(words);
        if (container.Labels.TryGetValue(LlmLabel, out var label) && string.Equals(label.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string word in words)
        {
            string needle = Normalize(word ?? "");
            if (needle.Length > 0
                && (container.Image.Contains(needle, StringComparison.OrdinalIgnoreCase) || container.Command.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The LLM servers among <paramref name="all"/>, in its order (2026-10-11, the checklist's rule shared with <c>/docker</c>'s llm button):
    /// a container <see cref="Matches"/> by <paramref name="words"/> (<see cref="Sorted"/> first) or named in <paramref name="ticked"/>
    /// (the chosen <c>Docker server containers</c>, trimmed); an empty word list keeps every container, unless <paramref name="strict"/>
    /// (the llm button's, 2026-10-11 review: with no words it lit "LLM servers" over every container, databases and all, and the chosen
    /// ones were lost among them), which keeps only the chosen then.
    /// </summary>
    public static IReadOnlyList<DockerContainer> Servers(IReadOnlyList<DockerContainer> all, IReadOnlyList<string> words, IEnumerable<string>? ticked, bool strict = false)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(words);
        var sorted = Sorted(words);
        if (sorted.Count == 0 && !strict)
        {
            return all;
        }

        var chosen = (ticked ?? []).Select(n => (n ?? "").Trim()).ToHashSet(StringComparer.Ordinal);
        return all.Where(c => chosen.Contains(c.Name) || Matches(c, sorted)).ToList();
    }

    /// <summary><paramref name="word"/> trimmed: the form a word is saved and matched in.</summary>
    public static string Normalize(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return word.Trim();
    }

    /// <summary>The saved list as the editor shows it and saves it: each word <see cref="Normalize"/>d, blanks gone, no duplicates ignoring case (the first kept), sorted A to Z ignoring case.</summary>
    public static List<string> Sorted(IReadOnlyList<string> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (string word in saved)
        {
            string normalized = Normalize(word ?? "");
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                list.Add(normalized);
            }
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>Whether <paramref name="word"/> is already in <paramref name="saved"/>, both <see cref="Normalize"/>d, case ignored.</summary>
    public static bool Contains(IReadOnlyList<string> saved, string word)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(word);
        string normalized = Normalize(word);
        return saved.Any(existing => string.Equals(Normalize(existing ?? ""), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The saved list with <paramref name="word"/> added (<see cref="Sorted"/>); a blank word or one already there leaves it as it was.</summary>
    public static List<string> Add(IReadOnlyList<string> saved, string word)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(word);
        return Sorted([.. saved, word]);
    }

    /// <summary>The saved list without <paramref name="word"/> (both <see cref="Normalize"/>d, case ignored), <see cref="Sorted"/>: the editor's remove.</summary>
    public static List<string> Without(IReadOnlyList<string> saved, string word)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(word);
        string normalized = Normalize(word);
        var list = Sorted(saved);
        list.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
        return list;
    }
}
