using System.Diagnostics;
using System.Text;

namespace Cabinet.Mail.Tests;

public class MailReaderReviewTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cabinet-eml2-" + Guid.NewGuid().ToString("N"));

    public MailReaderReviewTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void EmptyTextPart_FallsBackToHtml()
    {
        var path = Path.Combine(_folder, "alt.eml");
        File.WriteAllText(path,
            "From: a@b.fr\r\nSubject: Test\r\nMIME-Version: 1.0\r\nContent-Type: multipart/alternative; boundary=\"x\"\r\n\r\n" +
            "--x\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n   \r\n" +
            "--x\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>Audience le 3 décembre.</p>\r\n--x--\r\n", new UTF8Encoding(false));
        Assert.Contains("Audience le 3 décembre.", MailReader.FromFile(path).Body);
    }

    [Fact]
    public void BrokenHtml_DoesNotFreeze()
    {
        var path = Path.Combine(_folder, "broken.eml");
        var html = "<style>" + string.Concat(Enumerable.Repeat("p { color: red; } <b>", 20_000)) + "texte visible";
        File.WriteAllText(path, "From: a@b.fr\r\nSubject: Test\r\nContent-Type: text/html; charset=utf-8\r\n\r\n" + html + "\r\n", new UTF8Encoding(false));
        var watch = Stopwatch.StartNew();
        _ = MailReader.FromFile(path);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"lecture trop lente : {watch.Elapsed}");
    }

    [Fact]
    public void HugeFile_IsRefused()
    {
        var path = Path.Combine(_folder, "huge.eml");
        using (var file = File.Create(path))
        {
            file.SetLength(MailReader.MaxFileBytes + 1);
        }

        Assert.Throws<NotSupportedException>(() => MailReader.FromFile(path));
    }

    [Fact]
    public void PastedSender_WithMailtoBrackets()
    {
        var mail = MailReader.FromPastedText("De : Paul Martin [mailto:p.martin@exemple.fr]\nObjet : Test\n\nCorps");
        Assert.Equal("Paul Martin", mail.FromName);
        Assert.Equal("p.martin@exemple.fr", mail.FromAddress);
    }
}
