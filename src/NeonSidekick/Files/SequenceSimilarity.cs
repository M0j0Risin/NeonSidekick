namespace NeonSidekick.Files;

/// <summary>
/// A faithful port of Python's <c>difflib.SequenceMatcher</c> over characters (Ratcliff/Obershelp
/// with the <c>autojunk</c> heuristic and no junk predicate), for the two similarity strategies of
/// <see cref="FuzzyMatch"/> (2026-09-19). Faithful because the thresholds those strategies use
/// (0.50 / 0.70 / 0.80) were tuned on difflib's numbers: the longest match is found by the same
/// <c>b2j</c> index walk, "popular" characters of <paramref name="b"/> (more than <c>len / 100 + 1</c>
/// occurrences in a sequence of at least <see cref="AutoJunkMinLength"/>) are left out of the index
/// and picked up only while extending a match, the blocks are found by the same divide-and-conquer
/// queue and merged when adjacent. Pure; nothing here touches a file.
/// </summary>
public static class SequenceSimilarity
{
    /// <summary>A run of <paramref name="Length"/> equal characters at <paramref name="A"/> in the first sequence and <paramref name="B"/> in the second.</summary>
    public readonly record struct Block(int A, int B, int Length);

    public enum Op
    {
        Equal,
        Replace,
        Delete,
        Insert,
    }

    /// <summary>difflib's opcode: <c>a[I1..I2)</c> against <c>b[J1..J2)</c> under <paramref name="Tag"/>.</summary>
    public readonly record struct Opcode(Op Tag, int I1, int I2, int J1, int J2);

    /// <summary>The second sequence's length from which its popular characters are dropped from the index (difflib's <c>autojunk</c> floor).</summary>
    public const int AutoJunkMinLength = 200;

    /// <summary><c>2 · M / (|a| + |b|)</c>, M the characters in the matching blocks; 1.0 when both are empty (difflib's rule).</summary>
    public static double Ratio(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        int total = a.Length + b.Length;
        if (total == 0)
        {
            return 1.0;
        }

        int matches = 0;
        foreach (var block in MatchingBlocks(a, b))
        {
            matches += block.Length;
        }

        return 2.0 * matches / total;
    }

    /// <summary>The matching blocks, ascending in both sequences, adjacent ones merged, ending with difflib's <c>(|a|, |b|, 0)</c> sentinel.</summary>
    public static IReadOnlyList<Block> MatchingBlocks(string a, string b) => MatchingBlocks(a, b, long.MaxValue)!;

    /// <summary>
    /// <see cref="MatchingBlocks(string, string)"/> within <paramref name="budget"/> steps of the index walk, else null (2026-10-22, review
    /// H13: the walk is quadratic on reordered text — a sorted 40,000-row CSV took minutes of CPU after its write, for a transcript diff).
    /// </summary>
    public static IReadOnlyList<Block>? MatchingBlocks(string a, string b, long budget)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var index = IndexOf(b);
        var queue = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        queue.Push((0, a.Length, 0, b.Length));
        var found = new List<Block>();
        var walk = new Walk(budget);
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var match = LongestMatch(a, b, index, alo, ahi, blo, bhi, walk);
            if (walk.Spent)
            {
                return null;
            }

            if (match.Length == 0)
            {
                continue;
            }

            found.Add(match);
            if (alo < match.A && blo < match.B)
            {
                queue.Push((alo, match.A, blo, match.B));
            }

            if (match.A + match.Length < ahi && match.B + match.Length < bhi)
            {
                queue.Push((match.A + match.Length, ahi, match.B + match.Length, bhi));
            }
        }

        found.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B != y.B ? x.B.CompareTo(y.B) : x.Length.CompareTo(y.Length));

        // Adjacent blocks collapse into one, as difflib's do.
        var merged = new List<Block>();
        int i1 = 0, j1 = 0, k1 = 0;
        foreach (var (i2, j2, k2) in found)
        {
            if (i1 + k1 == i2 && j1 + k1 == j2)
            {
                k1 += k2;
            }
            else
            {
                if (k1 > 0)
                {
                    merged.Add(new Block(i1, j1, k1));
                }

                (i1, j1, k1) = (i2, j2, k2);
            }
        }

        if (k1 > 0)
        {
            merged.Add(new Block(i1, j1, k1));
        }

        merged.Add(new Block(a.Length, b.Length, 0));
        return merged;
    }

    /// <summary>difflib's <c>get_opcodes</c>: how to turn <paramref name="a"/> into <paramref name="b"/>, block by block.</summary>
    public static IReadOnlyList<Opcode> Opcodes(string a, string b) => Opcodes(a, b, long.MaxValue)!;

    /// <summary><see cref="Opcodes(string, string)"/> within <paramref name="budget"/> steps, else null (review H13).</summary>
    public static IReadOnlyList<Opcode>? Opcodes(string a, string b, long budget)
    {
        if (MatchingBlocks(a, b, budget) is not { } blocks)
        {
            return null;
        }

        var codes = new List<Opcode>();
        int i = 0, j = 0;
        foreach (var (ai, bj, size) in blocks)
        {
            Op? tag = null;
            if (i < ai && j < bj)
            {
                tag = Op.Replace;
            }
            else if (i < ai)
            {
                tag = Op.Delete;
            }
            else if (j < bj)
            {
                tag = Op.Insert;
            }

            if (tag is { } t)
            {
                codes.Add(new Opcode(t, i, ai, j, bj));
            }

            i = ai + size;
            j = bj + size;
            if (size > 0)
            {
                codes.Add(new Opcode(Op.Equal, ai, i, bj, j));
            }
        }

        return codes;
    }

    /// <summary>difflib's <c>b2j</c>: every position of each character of <paramref name="b"/>, the popular ones dropped under <c>autojunk</c>.</summary>
    private static Dictionary<char, List<int>> IndexOf(string b)
    {
        var index = new Dictionary<char, List<int>>();
        for (int i = 0; i < b.Length; i++)
        {
            if (!index.TryGetValue(b[i], out var positions))
            {
                positions = [];
                index[b[i]] = positions;
            }

            positions.Add(i);
        }

        if (b.Length >= AutoJunkMinLength)
        {
            int limit = b.Length / 100 + 1;
            foreach (var key in index.Where(pair => pair.Value.Count > limit).Select(pair => pair.Key).ToList())
            {
                index.Remove(key);
            }
        }

        return index;
    }

    /// <summary>
    /// difflib's <c>find_longest_match</c> over <c>a[alo..ahi)</c> and <c>b[blo..bhi)</c>: the longest
    /// block through the index, then extended on both sides over any equal characters — the popular
    /// ones the index left out included (with no junk predicate, difflib's "non-junk" extension takes
    /// them all). Ties go to the earliest in <paramref name="a"/>, then in <paramref name="b"/>.
    /// </summary>
    private static Block LongestMatch(string a, string b, Dictionary<char, List<int>> index, int alo, int ahi, int blo, int bhi, Walk walk)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        // Two maps swapped and cleared rather than one made per position (review H13: a Dictionary per line per recursion).
        var j2len = walk.Current;
        var newj2len = walk.Next;
        j2len.Clear();
        for (int i = alo; i < ahi; i++)
        {
            newj2len.Clear();
            if (!walk.Step())
            {
                return default;
            }

            if (index.TryGetValue(a[i], out var positions))
            {
                foreach (int j in positions)
                {
                    if (!walk.Step())
                    {
                        return default;
                    }

                    if (j < blo)
                    {
                        continue;
                    }

                    if (j >= bhi)
                    {
                        break;
                    }

                    int k = (j2len.TryGetValue(j - 1, out int previous) ? previous : 0) + 1;
                    newj2len[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }

            (j2len, newj2len) = (newj2len, j2len);
        }

        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1])
        {
            besti--;
            bestj--;
            bestsize++;
        }

        while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize])
        {
            bestsize++;
        }

        return new Block(besti, bestj, bestsize);
    }

    /// <summary>One run's step budget and the two maps <see cref="LongestMatch"/> reuses.</summary>
    private sealed class Walk(long budget)
    {
        private long _left = budget;

        public Dictionary<int, int> Current { get; } = [];

        public Dictionary<int, int> Next { get; } = [];

        public bool Spent => _left < 0;

        public bool Step() => --_left >= 0;
    }
}
