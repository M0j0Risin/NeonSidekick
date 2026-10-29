using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The Open XML SDK's schema validator over a written file (2026-10-29, the Office writing plan: test-only, the app writes by hand). Opening
/// the file through the SDK proves the package (content types, relationships); the validator the parts against the schemas.
/// </summary>
public static class OfficeSchema
{
    /// <summary>Asserts <paramref name="bytes"/> is a workbook the SDK opens with no schema errors.</summary>
    public static void ValidWorkbook(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        Assert.NotNull(document.WorkbookPart);
        Assert.Empty(Errors(document));
    }

    /// <summary>Asserts <paramref name="bytes"/> is a Word document the SDK opens with no schema errors.</summary>
    public static void ValidDocument(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);
        Assert.NotNull(document.MainDocumentPart);
        Assert.Empty(Errors(document));
    }

    private static List<string> Errors(OpenXmlPackage document)
    {
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        return errors;
    }
}
