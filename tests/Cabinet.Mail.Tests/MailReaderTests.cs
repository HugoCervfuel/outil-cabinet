using System.Text;

namespace Cabinet.Mail.Tests;

public class MailReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cabinet-eml-" + Guid.NewGuid().ToString("N"));

    public MailReaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private const string Eml = "From: \"Paul Martin\" <p.martin@exemple.fr>\r\n" +
                               "To: cabinet@exemple.fr\r\n" +
                               "Subject: =?UTF-8?Q?Mes_commentaires_sur_les_conclusions?=\r\n" +
                               "Date: Fri, 20 Nov 2026 10:15:00 +0100\r\n" +
                               "MIME-Version: 1.0\r\n" +
                               "Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n" +
                               "--b1\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n" +
                               "Bonjour Maître,\r\nVoici mes remarques sur les conclusions adverses.\r\nCordialement\r\n" +
                               "--b1\r\nContent-Type: application/pdf; name=\"remarques.pdf\"\r\nContent-Disposition: attachment; filename=\"remarques.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\nJVBERi0xLjQK\r\n" +
                               "--b1--\r\n";

    [Fact]
    public void ReadsEmlFile()
    {
        var path = Path.Combine(_folder, "message.eml");
        File.WriteAllText(path, Eml, new UTF8Encoding(false));
        var mail = MailReader.FromFile(path);
        Assert.Equal("Paul Martin", mail.FromName);
        Assert.Equal("p.martin@exemple.fr", mail.FromAddress);
        Assert.Equal("Mes commentaires sur les conclusions", mail.Subject);
        Assert.Contains("remarques sur les conclusions adverses", mail.Body);
        Assert.Equal(new[] { "remarques.pdf" }, mail.Attachments);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 15, 0, TimeSpan.FromHours(1)), mail.Date);
    }

    [Fact]
    public void HtmlOnlyEml_IsConvertedToText()
    {
        var path = Path.Combine(_folder, "html.eml");
        File.WriteAllText(path, "From: a@b.fr\r\nSubject: Test\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<html><body><p>Bonjour <b>Maître</b></p><p>Audience le 15 janvier.</p></body></html>\r\n");
        var mail = MailReader.FromFile(path);
        Assert.Contains("Bonjour Maître", mail.Body);
        Assert.Contains("Audience le 15 janvier.", mail.Body);
        Assert.DoesNotContain("<p>", mail.Body);
    }

    [Fact]
    public void UnsupportedFile_Throws()
    {
        var path = Path.Combine(_folder, "note.txt");
        File.WriteAllText(path, "x");
        Assert.Throws<NotSupportedException>(() => MailReader.FromFile(path));
    }

    [Fact]
    public void PastedOutlookText_French()
    {
        var mail = MailReader.FromPastedText(
            "De : Paul Martin <p.martin@exemple.fr>\nEnvoyé : vendredi 20 novembre 2026 10:15\nÀ : Cabinet\nObjet : RE: Conclusions\n\nBonjour Maître,\nMes remarques suivent.");
        Assert.Equal("Paul Martin", mail.FromName);
        Assert.Equal("p.martin@exemple.fr", mail.FromAddress);
        Assert.Equal("RE: Conclusions", mail.Subject);
        Assert.StartsWith("Bonjour Maître", mail.Body);
    }

    [Fact]
    public void PastedOutlookText_English()
    {
        var mail = MailReader.FromPastedText("From: greffe@tj-paris.fr\nSent: Friday\nSubject: Renvoi\n\nL'audience est renvoyée.");
        Assert.Equal("greffe@tj-paris.fr", mail.FromAddress);
        Assert.Equal("Renvoi", mail.Subject);
        Assert.Equal("L'audience est renvoyée.", mail.Body);
    }

    [Fact]
    public void PastedPlainText_WithoutHeaders_IsBodyOnly()
    {
        var mail = MailReader.FromPastedText("Bonjour, pouvez-vous me rappeler ?");
        Assert.Equal("", mail.FromAddress);
        Assert.Equal("(sans objet)", mail.Subject);
        Assert.Equal("Bonjour, pouvez-vous me rappeler ?", mail.Body);
    }

    [Fact]
    public void FromMime_KeepsTheMessageId()
    {
        var path = Path.Combine(_folder, "id.eml");
        File.WriteAllText(path, Eml.Replace("MIME-Version: 1.0\r\n", "Message-ID: <abc-123@exemple.fr>\r\nMIME-Version: 1.0\r\n"), new UTF8Encoding(false));
        var mail = MailReader.FromMime(MimeKit.MimeMessage.Load(path));
        Assert.Equal("abc-123@exemple.fr", mail.MessageId);
        Assert.Equal("p.martin@exemple.fr", mail.FromAddress);
    }

    [Fact]
    public void MailAssembly_HasNoNetworkReferences()
        => Cabinet.Diagnostic.Core.Tests.NetworkGuard.AssertNoNetworkReferences(typeof(MailReader).Assembly);
}
