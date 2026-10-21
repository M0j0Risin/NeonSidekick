using System.Text;
using NeonSidekick.WebUi;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The QR encoder behind <c>/webui</c>'s codes (2026-10-14): the standard's tables and numbers, a published Reed–Solomon example, the
/// function patterns, and every code read back — the zigzag, the mask, the blocks and the byte-mode text — to the link it was made from.
/// </summary>
public sealed class QrCodeTests
{
    [Fact]
    public void ReedSolomon_GivesThePublishedHelloWorldCodewords()
    {
        // "HELLO WORLD" at 1-M, the worked example of the standard's procedure: its 16 data codewords and their 10 error correction codewords.
        byte[] data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];

        Assert.Equal([196, 35, 39, 119, 235, 215, 231, 226, 93, 23], QrCode.Remainder(data, QrCode.Divisor(10)));
    }

    [Theory]
    [InlineData(1, QrEcc.Low, 19)]
    [InlineData(1, QrEcc.Medium, 16)]
    [InlineData(1, QrEcc.Quartile, 13)]
    [InlineData(1, QrEcc.High, 9)]
    [InlineData(5, QrEcc.Medium, 86)]
    [InlineData(10, QrEcc.Medium, 216)]
    [InlineData(40, QrEcc.Low, 2956)]
    [InlineData(40, QrEcc.High, 1276)]
    public void DataCodewords_AreTheStandardsCapacities(int version, QrEcc ecc, int codewords)
    {
        Assert.Equal(codewords, QrCode.DataCodewords(version, ecc));
    }

    [Theory]
    [InlineData(14, 1)]
    [InlineData(15, 2)]
    [InlineData(84, 5)]
    [InlineData(85, 6)]
    public void TheSmallestVersionThatHoldsTheText_IsChosen(int length, int version)
    {
        var code = QrCode.Encode(new string('a', length));

        Assert.Equal(version, code.Version);
        Assert.Equal((version * 4) + 17, code.Size);
    }

    [Fact]
    public void ATextPastVersion40_IsRefused()
    {
        Assert.Equal(40, QrCode.Encode(new string('a', 2953), QrEcc.Low).Version);
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('a', 2954), QrEcc.Low));
    }

    [Theory]
    [InlineData(1, new int[0])]
    [InlineData(2, new[] { 6, 18 })]
    [InlineData(7, new[] { 6, 22, 38 })]
    [InlineData(32, new[] { 6, 34, 60, 86, 112, 138 })]
    [InlineData(40, new[] { 6, 30, 58, 86, 114, 142, 170 })]
    public void AlignmentPositions_AreTheStandardsTable(int version, int[] positions)
    {
        Assert.Equal(positions, QrCode.AlignmentPositions(version));
    }

    [Fact]
    public void FormatBits_AreTheStandardsTable()
    {
        Assert.Equal(0b101010000010010, QrCode.FormatBits(QrEcc.Medium, 0));
        Assert.Equal(0b111011111000100, QrCode.FormatBits(QrEcc.Low, 0));
        Assert.Equal(0b011010101011111, QrCode.FormatBits(QrEcc.Quartile, 0));
        Assert.Equal(0b001011010001001, QrCode.FormatBits(QrEcc.High, 0));
    }

    [Fact]
    public void TheFunctionPatterns_StandWhereTheyShould()
    {
        var code = QrCode.Encode(WebUiAuth.SignInLink("192.168.100.200", 65535, WebUiAuth.NewToken()));

        foreach (var (cx, cy) in new[] { (3, 3), (code.Size - 4, 3), (3, code.Size - 4) })
        {
            Assert.True(code[cx, cy]);                // the finder's center
            Assert.False(code[cx + 2, cy]);           // its light ring
            Assert.True(code[cx + 3, cy]);            // its dark border
        }

        for (int i = 8; i < code.Size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, code[i, 6]);     // the timing patterns
            Assert.Equal(i % 2 == 0, code[6, i]);
        }

        Assert.True(code[8, code.Size - 8]);          // the dark module
        Assert.False(code[-1, 0]);                    // outside: the quiet zone
    }

    [Theory]
    [InlineData("http://192.168.1.23:8765/signin?t=", QrEcc.Medium)]
    [InlineData("http://192.168.100.200:65535/signin?t=", QrEcc.Medium)]
    [InlineData("http://localhost:8765/signin?t=", QrEcc.Low)]
    [InlineData("http://10.0.0.5:8765/signin?t=", QrEcc.High)]
    public void EveryMask_ReadsBackToTheLink(string prefix, QrEcc ecc)
    {
        string link = prefix + WebUiAuth.NewToken();
        for (int mask = 0; mask < 8; mask++)
        {
            Assert.Equal(link, ReadBack(QrCode.Encode(link, ecc, mask)));
        }

        Assert.Equal(link, ReadBack(QrCode.Encode(link, ecc)));
    }

    [Fact]
    public void ALongText_InSeveralBlocksOfTwoSizes_ReadsBack()
    {
        // Version 13-M (9 blocks: 8 of 41 data codewords, 1 of 42) and 25-Q exercise the interleave's short and long blocks.
        string text = string.Concat(Enumerable.Range(0, 30).Select(i => "line " + i + "; "));
        var code = QrCode.Encode(text);
        Assert.True(code.Version > 9);   // a 16-bit count
        Assert.Equal(text, ReadBack(code));
        string longer = new string('é', 300);
        Assert.Equal(longer, ReadBack(QrCode.Encode(longer, QrEcc.Quartile)));
    }

    [Fact]
    public void TheView_IsBlackOnWhite_TwoModulesACell_InsideItsQuietZone()
    {
        var code = QrCode.Encode("http://localhost:8765/signin?t=" + WebUiAuth.NewToken());
        var view = new QrView(code);
        var console = new TestConsole { Profile = { Width = 200 } }.EmitAnsiSequences();

        console.Write(view);

        Assert.Equal(code.Size + 8, view.Columns);
        Assert.Equal((code.Size + 9) / 2, view.Rows);
        string[] lines = console.Output.TrimEnd('\n').Split('\n');
        Assert.Equal(view.Rows, lines.Length);
        Assert.All(lines, l => Assert.Equal(view.Columns, l.Count(c => c == '▀')));
        Assert.Contains("\u001b[38;2;0;0;0;48;2;255;255;255m", console.Output);   // a dark module over a light one
    }

    [Fact]
    public void EveryDrawnRow_IsAQrRow_AndNoOtherLineIs()
    {
        // 2026-10-20: /webui's double-click opens the link from any row of the code.
        var code = QrCode.Encode("http://localhost:8765/signin?t=" + WebUiAuth.NewToken());
        var console = new TestConsole { Profile = { Width = 200 } };

        console.Write(new QrView(code));

        Assert.All(console.Output.TrimEnd('\n').Split('\n'), line => Assert.True(QrView.IsQrRow(line)));
        Assert.True(QrView.IsQrRow("  " + QrView.Cell + "\r"));
        Assert.False(QrView.IsQrRow(""));
        Assert.False(QrView.IsQrRow("   "));
        Assert.False(QrView.IsQrRow(QrView.Cell + " http://localhost:8765/"));
        Assert.False(QrView.IsQrRow(WebUiText.TokenWarning));
    }

    [Fact]
    public void ANewToken_IsBase64Url_AndAnOldHexOneStillSignsIn()
    {
        string token = WebUiAuth.NewToken();

        Assert.Equal(WebUiAuth.TokenLength, token.Length);
        Assert.True(WebUiAuth.IsWellFormed(token));
        Assert.DoesNotContain(token, c => c is '+' or '/' or '=');
        Assert.True(WebUiAuth.IsWellFormed(new string('a', 64)));
        Assert.False(WebUiAuth.IsWellFormed(new string('g', 64)));
        Assert.False(WebUiAuth.IsWellFormed(new string('a', 42) + "!"));
        Assert.True(WebUiAuth.SignInLink("192.168.100.200", 65535, token).Length <= 81);   // version 5 at Medium holds 84 bytes
        Assert.True(QrCode.Encode(WebUiAuth.SignInLink("192.168.100.200", 65535, token)).Version <= 5);
    }

    // ── Reading a code back ───────────────────────────────────────────────────

    /// <summary>
    /// The code's text, read as a scanner would: the format bits (their BCH checked against the table), the data modules in the zigzag with
    /// the mask taken off, the codewords de-interleaved into their blocks, each block's error correction checked by its syndromes, then the
    /// byte-mode segment.
    /// </summary>
    private static string ReadBack(QrCode code)
    {
        int format = 0;
        for (int i = 0; i <= 5; i++)
        {
            format |= (code[8, i] ? 1 : 0) << i;
        }

        format |= (code[8, 7] ? 1 : 0) << 6;
        format |= (code[8, 8] ? 1 : 0) << 7;
        format |= (code[7, 8] ? 1 : 0) << 8;
        for (int i = 9; i < 15; i++)
        {
            format |= (code[14 - i, 8] ? 1 : 0) << i;
        }

        int mask = Enumerable.Range(0, 8).Single(m => QrCode.FormatBits(code.Ecc, m) == format);
        Assert.Equal(code.Mask, mask);

        var bits = new List<bool>();
        for (int right = code.Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (int vertical = 0; vertical < code.Size; vertical++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    int y = ((right + 1) & 2) == 0 ? code.Size - 1 - vertical : vertical;
                    if (!code.IsFunction(x, y))
                    {
                        bits.Add(code[x, y] ^ QrCode.Masked(mask, x, y));
                    }
                }
            }
        }

        int raw = QrCode.RawDataModules(code.Version) / 8;
        var codewords = new byte[raw];
        for (int i = 0; i < raw * 8; i++)
        {
            if (bits[i])
            {
                codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
        }

        var (blocks, eccLength) = QrCode.Layout(code.Version, code.Ecc);
        Assert.Equal(raw - QrCode.DataCodewords(code.Version, code.Ecc), blocks * eccLength);
        int shortBlocks = blocks - (raw % blocks);
        int shortData = (raw / blocks) - eccLength;
        var data = new List<byte>[blocks];
        var ecc = new List<byte>[blocks];
        for (int b = 0; b < blocks; b++)
        {
            data[b] = [];
            ecc[b] = [];
        }

        int k = 0;
        for (int i = 0; i < shortData + 1; i++)
        {
            for (int b = 0; b < blocks; b++)
            {
                if (i < shortData || b >= shortBlocks)
                {
                    data[b].Add(codewords[k++]);
                }
            }
        }

        for (int i = 0; i < eccLength; i++)
        {
            for (int b = 0; b < blocks; b++)
            {
                ecc[b].Add(codewords[k++]);
            }
        }

        for (int b = 0; b < blocks; b++)
        {
            Assert.True(SyndromesAreZero([.. data[b], .. ecc[b]], eccLength), "block " + b + "'s error correction does not check");
        }

        var all = data.SelectMany(d => d).ToArray();
        int bit = 0;
        int Read(int length)
        {
            int value = 0;
            for (int i = 0; i < length; i++, bit++)
            {
                value = (value << 1) | ((all[bit >> 3] >> (7 - (bit & 7))) & 1);
            }

            return value;
        }

        Assert.Equal(0b0100, Read(4));
        int count = Read(code.Version < 10 ? 8 : 16);
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)Read(8);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Whether the codeword polynomial is divisible by the generator: every syndrome S_i = c(α^i), i &lt; <paramref name="eccLength"/>, is zero.</summary>
    private static bool SyndromesAreZero(byte[] codeword, int eccLength)
    {
        for (int i = 0; i < eccLength; i++)
        {
            int alpha = Power(2, i);
            int value = 0;
            foreach (byte c in codeword)
            {
                value = Multiply(value, alpha) ^ c;
            }

            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int Power(int x, int n)
    {
        int result = 1;
        for (int i = 0; i < n; i++)
        {
            result = Multiply(result, x);
        }

        return result;
    }

    private static int Multiply(int a, int b)
    {
        int result = 0;
        while (b > 0)
        {
            if ((b & 1) != 0)
            {
                result ^= a;
            }

            a <<= 1;
            if ((a & 0x100) != 0)
            {
                a ^= 0x11D;
            }

            b >>= 1;
        }

        return result;
    }
}
