using System.Text;

namespace NeonSidekick.WebUi;

/// <summary>A QR code's error correction level: the share of the code that may be damaged and still read.</summary>
public enum QrEcc
{
    /// <summary>About 7%.</summary>
    Low,

    /// <summary>About 15%: the Web UI's, a phone pointed at a screen.</summary>
    Medium,

    /// <summary>About 25%.</summary>
    Quartile,

    /// <summary>About 30%.</summary>
    High,
}

/// <summary>
/// A QR code (ISO/IEC 18004, model 2) of a text in byte mode (2026-10-14, the Web UI's sign-in link on <c>/webui</c>'s pane, so a phone scans
/// it instead of typing it): the smallest version 1–40 that holds the text at <see cref="Ecc"/>, Reed–Solomon over GF(2⁸) (0x11D), the
/// blocks interleaved, the function patterns, the data in the zigzag, the mask of the eight with the lowest penalty, the format and version
/// bits. Written here rather than taken from a package, as the app's other formats are; it follows the standard's procedure step by step
/// (the same steps Project Nayuki's reference encoder takes). <see cref="Modules"/> is row-major, true for dark; no quiet zone. Pure.
/// </summary>
public sealed class QrCode
{
    /// <summary>ECC codewords per block, by level then version (index 0 unused).</summary>
    private static readonly sbyte[][] EccPerBlock =
    [
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    /// <summary>Error correction blocks, by level then version (index 0 unused).</summary>
    private static readonly sbyte[][] Blocks =
    [
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];

    private readonly bool[,] _modules;
    private readonly bool[,] _function;

    private QrCode(int version, QrEcc ecc, byte[] dataCodewords, int? forcedMask)
    {
        Version = version;
        Ecc = ecc;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _function = new bool[Size, Size];
        DrawFunctionPatterns();
        DrawCodewords(AddEccAndInterleave(dataCodewords));
        Mask = forcedMask ?? BestMask();
        ApplyMask(Mask);
        DrawFormatBits(Mask);
    }

    /// <summary>The version, 1 to 40.</summary>
    public int Version { get; }

    /// <summary>The error correction level.</summary>
    public QrEcc Ecc { get; }

    /// <summary>The modules per side: 21 to 177.</summary>
    public int Size { get; }

    /// <summary>The mask pattern applied, 0 to 7.</summary>
    public int Mask { get; }

    /// <summary>Whether the module at column <paramref name="x"/>, row <paramref name="y"/> belongs to a function pattern, not the data (tests read the code back).</summary>
    internal bool IsFunction(int x, int y) => _function[y, x];

    /// <summary>Whether the module at column <paramref name="x"/>, row <paramref name="y"/> is dark; false outside the code (the quiet zone).</summary>
    public bool this[int x, int y] => x >= 0 && y >= 0 && x < Size && y < Size && _modules[y, x];

    /// <summary>
    /// <paramref name="text"/> (UTF-8) as the smallest code that holds it at <paramref name="ecc"/>; <paramref name="mask"/> forces a mask (tests).
    /// Throws <see cref="ArgumentException"/> when it is longer than version 40 holds.
    /// </summary>
    public static QrCode Encode(string text, QrEcc ecc = QrEcc.Medium, int? mask = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (mask is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(mask));
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        for (int version = 1; version <= 40; version++)
        {
            int capacityBits = DataCodewords(version, ecc) * 8;
            int countBits = version < 10 ? 8 : 16;
            int usedBits = 4 + countBits + bytes.Length * 8;
            if (bytes.Length >= 1 << countBits || usedBits > capacityBits)
            {
                continue;
            }

            var bits = new List<bool>(capacityBits);
            Append(bits, 0b0100, 4);   // byte mode
            Append(bits, bytes.Length, countBits);
            foreach (byte b in bytes)
            {
                Append(bits, b, 8);
            }

            Append(bits, 0, Math.Min(4, capacityBits - bits.Count));   // the terminator
            Append(bits, 0, (8 - (bits.Count % 8)) % 8);
            for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11)
            {
                Append(bits, pad, 8);
            }

            var codewords = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++)
            {
                if (bits[i])
                {
                    codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
                }
            }

            return new QrCode(version, ecc, codewords, mask);
        }

        throw new ArgumentException("The text is too long for a QR code.", nameof(text));
    }

    /// <summary>How many data codewords <paramref name="version"/> holds at <paramref name="ecc"/> (the rest are error correction).</summary>
    public static int DataCodewords(int version, QrEcc ecc) =>
        (RawDataModules(version) / 8) - (EccPerBlock[(int)ecc][version] * Blocks[(int)ecc][version]);

    /// <summary>The error correction blocks of <paramref name="version"/> at <paramref name="ecc"/> and each one's error correction codewords.</summary>
    internal static (int Blocks, int EccPerBlock) Layout(int version, QrEcc ecc) => (Blocks[(int)ecc][version], EccPerBlock[(int)ecc][version]);

    /// <summary>The modules left for data and error correction once the function patterns are drawn.</summary>
    public static int RawDataModules(int version)
    {
        int result = (((16 * version) + 128) * version) + 64;
        if (version >= 2)
        {
            int align = (version / 7) + 2;
            result -= (((25 * align) - 10) * align) - 55;
            if (version >= 7)
            {
                result -= 36;
            }
        }

        return result;
    }

    private static void Append(List<bool> bits, int value, int length)
    {
        for (int i = length - 1; i >= 0; i--)
        {
            bits.Add(((value >> i) & 1) != 0);
        }
    }

    // ── Reed–Solomon ──────────────────────────────────────────────────────────

    /// <summary>The generator polynomial's coefficients of <paramref name="degree"/>, highest first, the leading 1 left out.</summary>
    public static byte[] Divisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = Multiply(root, 2);
        }

        return result;
    }

    /// <summary>The error correction codewords of <paramref name="data"/> for <paramref name="divisor"/>.</summary>
    public static byte[] Remainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }

        return result;
    }

    /// <summary>The product in GF(2⁸) modulo x⁸ + x⁴ + x³ + x² + 1.</summary>
    private static byte Multiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    private byte[] AddEccAndInterleave(byte[] data)
    {
        int blocks = Blocks[(int)Ecc][Version];
        int eccLength = EccPerBlock[(int)Ecc][Version];
        int raw = RawDataModules(Version) / 8;
        int shortBlocks = blocks - (raw % blocks);
        int shortLength = raw / blocks;
        var divisor = Divisor(eccLength);
        var all = new byte[blocks][];
        for (int i = 0, k = 0; i < blocks; i++)
        {
            int length = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            var block = new byte[shortLength + 1];
            Array.Copy(data, k, block, 0, length);
            k += length;
            var ecc = Remainder(data.AsSpan(k - length, length), divisor);
            // A short block gets a gap where the long ones have their last data codeword, skipped in the interleave.
            Array.Copy(ecc, 0, block, shortLength + 1 - eccLength, eccLength);
            all[i] = block;
        }

        var result = new List<byte>(raw);
        for (int i = 0; i < shortLength + 1; i++)
        {
            for (int j = 0; j < blocks; j++)
            {
                if (i != shortLength - eccLength || j >= shortBlocks)
                {
                    result.Add(all[j][i]);
                }
            }
        }

        return [.. result];
    }

    // ── The patterns ──────────────────────────────────────────────────────────

    private void Set(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _function[y, x] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (int i = 0; i < Size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);
        var positions = AlignmentPositions(Version);
        int last = positions.Length - 1;
        for (int i = 0; i <= last; i++)
        {
            for (int j = 0; j <= last; j++)
            {
                if (!((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0)))
                {
                    DrawAlignment(positions[i], positions[j]);
                }
            }
        }

        DrawFormatBits(0);   // reserves the format areas; drawn again with the real mask
        DrawVersion();
    }

    /// <summary>The alignment patterns' centers on each axis for <paramref name="version"/>.</summary>
    public static int[] AlignmentPositions(int version)
    {
        if (version == 1)
        {
            return [];
        }

        int count = (version / 7) + 2;
        int step = ((version * 8) + (count * 3) + 5) / ((count * 4) - 4) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, position = (version * 4) + 17 - 7; i >= 1; i--, position -= step)
        {
            result[i] = position;
        }

        return result;
    }

    private void DrawFinder(int x, int y)
    {
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx;
                int yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                {
                    Set(xx, yy, distance != 2 && distance != 4);
                }
            }
        }
    }

    private void DrawAlignment(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                Set(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    /// <summary>The 15 format bits for <paramref name="ecc"/> and <paramref name="mask"/>: the level and mask, their BCH code, the XOR mask.</summary>
    public static int FormatBits(QrEcc ecc, int mask)
    {
        int level = ecc switch
        {
            QrEcc.Low => 1,
            QrEcc.Medium => 0,
            QrEcc.Quartile => 3,
            _ => 2,
        };
        int data = (level << 3) | mask;
        int remainder = data;
        for (int i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        return ((data << 10) | remainder) ^ 0x5412;
    }

    private void DrawFormatBits(int mask)
    {
        int bits = FormatBits(Ecc, mask);
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        for (int i = 0; i <= 5; i++)
        {
            Set(8, i, Bit(i));
        }

        Set(8, 7, Bit(6));
        Set(8, 8, Bit(7));
        Set(7, 8, Bit(8));
        for (int i = 9; i < 15; i++)
        {
            Set(14 - i, 8, Bit(i));
        }

        for (int i = 0; i < 8; i++)
        {
            Set(Size - 1 - i, 8, Bit(i));
        }

        for (int i = 8; i < 15; i++)
        {
            Set(8, Size - 15 + i, Bit(i));
        }

        Set(8, Size - 8, true);   // the dark module
    }

    private void DrawVersion()
    {
        if (Version < 7)
        {
            return;
        }

        int remainder = Version;
        for (int i = 0; i < 12; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        }

        int bits = (Version << 12) | remainder;
        for (int i = 0; i < 18; i++)
        {
            bool bit = ((bits >> i) & 1) != 0;
            int a = Size - 11 + (i % 3);
            int b = i / 3;
            Set(a, b, bit);
            Set(b, a, bit);
        }
    }

    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;   // the vertical timing column is skipped
            }

            for (int vertical = 0; vertical < Size; vertical++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vertical : vertical;
                    if (!_function[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
            }
        }
    }

    /// <summary>Whether mask <paramref name="mask"/> inverts the module at column <paramref name="x"/>, row <paramref name="y"/>.</summary>
    public static bool Masked(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => ((x / 3) + (y / 2)) % 2 == 0,
        5 => ((x * y) % 2) + ((x * y) % 3) == 0,
        6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
        _ => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
    };

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (!_function[y, x] && Masked(mask, x, y))
                {
                    _modules[y, x] = !_modules[y, x];
                }
            }
        }
    }

    private int BestMask()
    {
        int best = 0;
        int lowest = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            int penalty = Penalty();
            if (penalty < lowest)
            {
                best = mask;
                lowest = penalty;
            }

            ApplyMask(mask);   // undone: the mask is its own inverse
        }

        return best;
    }

    /// <summary>The standard's four penalty rules: long runs, 2×2 blocks, finder-like runs, and the balance of dark and light.</summary>
    private int Penalty()
    {
        int result = 0;
        for (int line = 0; line < Size; line++)
        {
            result += RunPenalty(i => _modules[line, i]);
            result += RunPenalty(i => _modules[i, line]);
        }

        for (int y = 0; y < Size - 1; y++)
        {
            for (int x = 0; x < Size - 1; x++)
            {
                bool c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1])
                {
                    result += 3;
                }
            }
        }

        int dark = 0;
        foreach (bool module in _modules)
        {
            dark += module ? 1 : 0;
        }

        int total = Size * Size;
        int k = ((Math.Abs((dark * 20) - (total * 10)) + total - 1) / total) - 1;
        return result + (k * 10);
    }

    private static readonly bool[] FinderLike = [true, false, true, true, true, false, true];

    private int RunPenalty(Func<int, bool> at)
    {
        int result = 0;
        int run = 1;
        for (int i = 1; i <= Size; i++)
        {
            if (i < Size && at(i) == at(i - 1))
            {
                run++;
                continue;
            }

            if (run >= 5)
            {
                result += 3 + (run - 5);
            }

            run = 1;
        }

        // 1:1:3:1:1 with four light modules on a side (the quiet zone counting as light).
        for (int i = 0; i + 7 <= Size; i++)
        {
            bool match = true;
            for (int j = 0; j < 7 && match; j++)
            {
                match = at(i + j) == FinderLike[j];
            }

            if (match && (LightRun(at, i - 4, i) || LightRun(at, i + 7, i + 11)))
            {
                result += 40;
            }
        }

        return result;
    }

    private bool LightRun(Func<int, bool> at, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (i >= 0 && i < Size && at(i))
            {
                return false;
            }
        }

        return true;
    }
}
