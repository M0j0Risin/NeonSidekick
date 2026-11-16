using System.Text;
using NeonSidekick.Files;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The made files' allow-list (2026-11-16, plan lucid-roaming-lantern #41): a PDF, pictures and plain text inline (any case); Office files
/// downloaded with their own type, the map zipping OfficeFile's two lists; anything else bytes to download; a text's charset read off every
/// byte, UTF-32 none (a download); every kind sandboxed but a PDF.
/// </summary>
public sealed class MadeFileTypesTests
{
    [Theory]
    [InlineData("SCAN.PDF", MadeFileKind.Pdf, "application/pdf", true)]
    [InlineData("a.png", MadeFileKind.Picture, "image/png", true)]
    [InlineData("a.JPeG", MadeFileKind.Picture, "image/jpeg", true)]
    [InlineData("a.webp", MadeFileKind.Picture, "image/webp", true)]
    [InlineData("a.gif", MadeFileKind.Picture, "image/gif", true)]
    [InlineData("notes.md", MadeFileKind.Text, "text/plain", true)]
    [InlineData("data.csv", MadeFileKind.Text, "text/plain", true)]
    [InlineData("REPORT.DOCX", MadeFileKind.Word, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", false)]
    [InlineData("sheet.xlsx", MadeFileKind.Excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", false)]
    [InlineData("deck.pptx", MadeFileKind.Slides, "application/vnd.openxmlformats-officedocument.presentationml.presentation", false)]
    [InlineData("page.html", MadeFileKind.Other, MadeFileTypes.Bytes, false)]
    [InlineData("pic.svg", MadeFileKind.Other, MadeFileTypes.Bytes, false)]
    [InlineData("run.js", MadeFileKind.Other, MadeFileTypes.Bytes, false)]
    [InlineData("noextension", MadeFileKind.Other, MadeFileTypes.Bytes, false)]
    public void EachKind_HasItsTypeAndDisposition(string name, MadeFileKind kind, string type, bool inline)
    {
        var made = MadeFileTypes.Of(name);
        Assert.Equal((kind, type, inline), (made.Kind, made.ContentType, made.Inline));
        Assert.Equal(kind != MadeFileKind.Pdf, made.Sandboxed);
        Assert.False(string.IsNullOrEmpty(MadeFileTypes.Glyph(kind)));
    }

    [Fact]
    public void TheOfficeMap_ZipsTwoListsOfOneLength()
    {
        Assert.Equal(OfficeFile.Extensions.Count, OfficeFile.MediaTypes.Count);
        Assert.All(OfficeFile.Extensions.Select((e, i) => (e, i)), p => Assert.Equal(OfficeFile.MediaTypes[p.i], MadeFileTypes.Of("x" + p.e).ContentType));
    }

    [Fact]
    public void ATextsCharset_IsReadOffEveryByte_AndUtf32HasNone()
    {
        static string? Of(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            string? charset = MadeFileTypes.Charset(stream);
            Assert.Equal(0, stream.Position);   // left where it was
            return charset;
        }

        Assert.Equal("utf-8", Of(Encoding.UTF8.GetBytes("plain")));
        Assert.Equal("utf-8", Of([0xEF, 0xBB, 0xBF, (byte)'a']));
        Assert.Equal("utf-16le", Of([0xFF, 0xFE, (byte)'a', 0]));
        Assert.Equal("utf-16be", Of([0xFE, 0xFF, 0, (byte)'a']));
        byte[] cafe = [.. Encoding.ASCII.GetBytes("name\ncaf"), 0xE9, (byte)'\n'];
        Assert.Equal(TextFile.LegacyCodec.StreamEncoding.WebName, Of(cafe));
        Assert.Null(Of([0xFF, 0xFE, 0, 0, (byte)'a', 0, 0, 0]));
    }
}
