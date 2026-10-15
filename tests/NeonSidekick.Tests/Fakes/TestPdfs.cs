using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// Hand-written PDFs for the real-browser reads (2026-10-14, the PDF plan's gap 30), made in code rather than committed as files so each
/// says how it is built: an outline with page labels (<see cref="WithOutlineAndLabels"/>), a Japanese page whose font needs a predefined
/// CMap (<see cref="Cjk"/>), and one locked with a user password (<see cref="Locked"/>, the standard security handler, revision 2:
/// RC4 40-bit, as old as PDF 1.1 and still read by every reader). Object numbers are 1-based in the order given; offsets are counted.
/// </summary>
public static class TestPdfs
{
    /// <summary>
    /// Three pages ("one", "two", "three") with an outline — Front (page 1) holding Preface (page 2), then Body (page 3) — and page
    /// labels i, ii, 1 (lowercase roman for the first two, decimal from the third).
    /// </summary>
    public static byte[] WithOutlineAndLabels()
    {
        // 1 catalog, 2 pages, 3 font, 4..9 three page/content pairs, 10 outlines, 11 Front, 12 Preface, 13 Body
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 10 0 R /PageLabels << /Nums [0 << /S /r >> 2 << /S /D >>] >> >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R] /Count 3 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach (string text in new[] { "one", "two", "three" })
        {
            int content = objects.Count + 2;
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents " + N(content) + " 0 R >>");
            objects.Add(Stream("BT /F1 18 Tf 72 720 Td (" + text + ") Tj ET"));
        }

        objects.Add("<< /Type /Outlines /First 11 0 R /Last 13 0 R /Count 3 >>");
        objects.Add("<< /Title (Front) /Parent 10 0 R /Next 13 0 R /First 12 0 R /Last 12 0 R /Count 1 /Dest [4 0 R /Fit] >>");
        objects.Add("<< /Title (Preface) /Parent 11 0 R /Dest [6 0 R /Fit] >>");
        objects.Add("<< /Title (Body) /Parent 10 0 R /Prev 11 0 R /Dest [8 0 R /Fit] >>");
        return Build(objects, "");
    }

    /// <summary>The text <see cref="Cjk"/> shows: "Japanese" in Japanese.</summary>
    public const string CjkText = "日本語";

    /// <summary>
    /// One page of <see cref="CjkText"/> in a Type0 font with the predefined CMap <c>UniJIS-UCS2-H</c> and a non-embedded CID font of
    /// Adobe-Japan1: pdf.js needs that CMap to map the codes and <c>Adobe-Japan1-UCS2</c> to give their text, both from the packed CMaps.
    /// </summary>
    public static byte[] Cjk()
    {
        string hex = string.Concat(CjkText.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("BT /F1 24 Tf 72 720 Td <" + hex + "> Tj ET"),
            "<< /Type /Font /Subtype /Type0 /BaseFont /KozMinPr6N-Regular /Encoding /UniJIS-UCS2-H /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /KozMinPr6N-Regular /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >> /FontDescriptor 7 0 R /DW 1000 >>",
            "<< /Type /FontDescriptor /FontName /KozMinPr6N-Regular /Flags 4 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 >>",
        };
        return Build(objects, "");
    }

    /// <summary>
    /// One page locked with the user password <c>secret</c> (owner <c>owner</c>): an <c>/Encrypt</c> dictionary whose <c>/U</c> no empty
    /// password matches, so pdf.js asks for one (<c>PasswordException</c>) before it reads anything.
    /// </summary>
    public static byte[] Locked()
    {
        byte[] id = MD5.HashData(Encoding.ASCII.GetBytes("NeonSidekick locked fixture"));
        const int permissions = -44;
        byte[] owner = Rc4(MD5.HashData(Padded("owner"))[..5], Padded("secret"));
        byte[] key = MD5.HashData([.. Padded("secret"), .. owner, .. BitConverter.GetBytes(permissions), .. id])[..5];
        byte[] user = Rc4(key, Padding);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            Stream("q Q"),
            "<< /Filter /Standard /V 1 /R 2 /O <" + Convert.ToHexString(owner) + "> /U <" + Convert.ToHexString(user) + "> /P " + N(permissions) + " >>",
        };
        return Build(objects, " /Encrypt 5 0 R /ID [<" + Convert.ToHexString(id) + "> <" + Convert.ToHexString(id) + ">]");
    }

    /// <summary>
    /// One page with an AcroForm (2026-10-14, the PDF plan's phase 4b): a text field <c>name</c>, a check box <c>agree</c> (on value
    /// <c>Yes</c>, with both appearances) and a drop-down <c>color</c> of red, green and blue, all in Helvetica.
    /// </summary>
    public static byte[] Form()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R 7 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 8 0 R >> >> /Contents 4 0 R /Annots [5 0 R 6 0 R 7 0 R] >>",
            Stream("BT /F1 12 Tf 72 740 Td (Form) Tj ET"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 700 300 720] /P 3 0 R /DA (/Helv 12 Tf 0 g) /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /Rect [72 650 90 668] /P 3 0 R /V /Off /AS /Off /AP << /N << /Yes 9 0 R /Off 10 0 R >> >> /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (color) /Opt [(red) (green) (blue)] /Rect [72 600 200 620] /P 3 0 R /DA (/Helv 12 Tf 0 g) /F 4 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Appearance("0 g 4 4 10 10 re f"),
            Appearance(""),
        };
        return Build(objects, "");
    }

    /// <summary>One page whose form is XFA alone (2026-10-14, gap 25): an AcroForm with no fields and an <c>/XFA</c> stream.</summary>
    public static byte[] XfaOnly()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA 5 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            Stream("q Q"),
            Stream("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"></xdp:xdp>"),
        };
        return Build(objects, "");
    }

    private static string Appearance(string content) =>
        "<< /Type /XObject /Subtype /Form /BBox [0 0 18 18] /Length " + N(Encoding.ASCII.GetByteCount(content)) + " >>\nstream\n" + content + "\nendstream";

    private static readonly byte[] Padding =Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

    private static byte[] Padded(string password) => [.. Encoding.ASCII.GetBytes(password).Take(32), .. Padding.Take(32 - Math.Min(32, password.Length))];

    /// <summary>RC4, the cipher of the standard security handler's revision 2 (.NET has none; a test needs only these few lines).</summary>
    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }

    private static string Stream(string content) => "<< /Length " + N(Encoding.ASCII.GetByteCount(content)) + " >>\nstream\n" + content + "\nendstream";

    private static byte[] Build(IReadOnlyList<string> objects, string trailerExtra)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(N(i + 1)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(N(objects.Count + 1)).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(N(objects.Count + 1)).Append(" /Root 1 0 R").Append(trailerExtra).Append(" >>\nstartxref\n").Append(N(xref)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
