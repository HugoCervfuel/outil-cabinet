using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Cabinet.Documents.Tests;

public class DocumentReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cabinet-docs-" + Guid.NewGuid().ToString("N"));

    public DocumentReaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Pdf_ReadsTextPageByPage()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Rapport d'expertise du 3 fevrier 2025", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Infiltrations constatees dans la cave", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
        var path = Path.Combine(_folder, "expertise.pdf");
        File.WriteAllBytes(path, builder.Build());

        var pages = DocumentReader.Read(path);
        Assert.Equal(2, pages.Count);
        Assert.Contains("Rapport d'expertise", pages[0].Text);
        Assert.Equal(2, pages[1].Number);
        Assert.Contains("Infiltrations", pages[1].Text);
    }

    [Fact]
    public void Docx_ReadsParagraphs()
    {
        var path = Path.Combine(_folder, "note.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Note de rendez-vous</w:t></w:r></w:p><w:p><w:r><w:t xml:space="preserve">Le client </w:t></w:r><w:r><w:t>conteste la vétusté.</w:t></w:r></w:p></w:body></w:document>""");
        }

        var pages = DocumentReader.Read(path);
        var text = string.Join("\n", pages.Select(p => p.Text));
        Assert.Contains("Note de rendez-vous", text);
        Assert.Contains("Le client conteste la vétusté.", text);
    }

    [Fact]
    public void Text_IsRead()
    {
        var path = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(path, "Appel du client le 4 mars.");
        Assert.Contains("Appel du client", DocumentReader.Read(path).Single().Text);
    }

    [Fact]
    public void Eml_IsReadAsAPiece()
    {
        var path = Path.Combine(_folder, "mail.eml");
        File.WriteAllText(path, "From: Paul <p@exemple.fr>\r\nSubject: Observations\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nJe conteste la vétusté.\r\n", new UTF8Encoding(false));
        var text = DocumentReader.Read(path).Single().Text;
        Assert.Contains("Observations", text);
        Assert.Contains("Je conteste la vétusté.", text);
    }

    [Fact]
    public void Unsupported_Throws()
    {
        var path = Path.Combine(_folder, "photo.jpg");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        Assert.Throws<NotSupportedException>(() => DocumentReader.Read(path));
    }

    [Fact]
    public void LongWordDocument_IsSplitIntoSections()
    {
        var path = Path.Combine(_folder, "long.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            var paragraphs = string.Concat(Enumerable.Range(1, 200).Select(i => $"<w:p><w:r><w:t>Paragraphe {i} {new string('x', 100)}</w:t></w:r></w:p>"));
            writer.Write($"""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{paragraphs}</w:body></w:document>""");
        }

        var pages = DocumentReader.Read(path);
        Assert.True(pages.Count > 3);
        Assert.Equal(Enumerable.Range(1, pages.Count), pages.Select(p => p.Number));
    }

    [Fact]
    public void DocumentsAssembly_HasNoNetworkReferences()
        => Cabinet.Diagnostic.Core.Tests.NetworkGuard.AssertNoNetworkReferences(typeof(DocumentReader).Assembly);
}
