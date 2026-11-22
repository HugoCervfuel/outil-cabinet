using System.IO.Compression;
using System.Text;
using System.Xml;
using Cabinet.Core;
using Cabinet.Mail;
using UglyToad.PdfPig;

namespace Cabinet.Documents;

/// <summary>
/// Lit le texte d'une pièce déposée par l'avocat, sur le poste, sans service extérieur :
/// PDF (page par page), Word (.docx, découpé en sections), texte, e-mails (.eml, .msg).
/// </summary>
public static class DocumentReader
{
    /// <summary>Taille d'une « page » pour les documents sans pagination (Word, texte).</summary>
    private const int SectionCharacters = 3000;

    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".pdf", ".docx", ".txt", ".md", ".eml", ".msg" };

    public static long MaxFileBytes => 200L * 1024 * 1024;

    public static IReadOnlyList<PiecePage> Read(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException("Format non pris en charge : seuls les PDF, Word (.docx), textes et e-mails sont acceptés.");
        }

        if (new FileInfo(path).Length > MaxFileBytes)
        {
            throw new NotSupportedException("Fichier trop volumineux.");
        }

        return extension switch
        {
            ".pdf" => ReadPdf(path),
            ".docx" => Sections(ReadDocxParagraphs(path)),
            ".eml" or ".msg" => Sections(MailParagraphs(MailReader.FromFile(path))),
            _ => Sections(File.ReadAllText(path).Replace("\r\n", "\n").Split('\n')),
        };
    }

    private static IReadOnlyList<PiecePage> ReadPdf(string path)
    {
        using var document = PdfDocument.Open(path);
        return document.GetPages()
            .Select(page => new PiecePage(page.Number, ReadPdfPage(page)))
            .ToList();
    }

    private static string ReadPdfPage(UglyToad.PdfPig.Content.Page page)
    {
        // Les mots dans l'ordre de lecture, ligne par ligne.
        var lines = page.GetWords()
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3))
            .OrderByDescending(g => g.Key)
            .Select(g => string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
        return string.Join('\n', lines);
    }

    private static IEnumerable<string> ReadDocxParagraphs(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new NotSupportedException("Document Word illisible.");
        using var stream = entry.Open();
        var xml = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            xml.Load(reader);
        }

        var names = new XmlNamespaceManager(xml.NameTable);
        names.AddNamespace("w", WordNamespace);
        var paragraphs = new List<string>();
        foreach (XmlNode paragraph in xml.SelectNodes("//w:p", names)!)
        {
            var text = new StringBuilder();
            foreach (XmlNode node in paragraph.SelectNodes(".//w:t | .//w:tab | .//w:br", names)!)
            {
                text.Append(node.LocalName switch
                {
                    "t" => node.InnerText,
                    "tab" => "\t",
                    _ => "\n",
                });
            }

            paragraphs.Add(text.ToString());
        }

        return paragraphs;
    }

    private static IEnumerable<string> MailParagraphs(MailMessage mail)
    {
        yield return $"De : {mail.FromName} <{mail.FromAddress}>";
        if (mail.Date is { } date)
        {
            yield return $"Date : {date:dd/MM/yyyy HH:mm}";
        }

        yield return $"Objet : {mail.Subject}";
        if (mail.Attachments.Count > 0)
        {
            yield return "Pièces jointes : " + string.Join(", ", mail.Attachments);
        }

        yield return "";
        foreach (var line in mail.Body.Replace("\r\n", "\n").Split('\n'))
        {
            yield return line;
        }
    }

    /// <summary>Regroupe des paragraphes en sections numérotées d'environ 3 000 caractères.</summary>
    private static IReadOnlyList<PiecePage> Sections(IEnumerable<string> paragraphs)
    {
        var pages = new List<PiecePage>();
        var current = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            if (current.Length > 0 && current.Length + paragraph.Length > SectionCharacters)
            {
                pages.Add(new PiecePage(pages.Count + 1, current.ToString().TrimEnd()));
                current.Clear();
            }

            current.AppendLine(paragraph);
        }

        if (current.Length > 0 || pages.Count == 0)
        {
            pages.Add(new PiecePage(pages.Count + 1, current.ToString().TrimEnd()));
        }

        return pages;
    }
}
