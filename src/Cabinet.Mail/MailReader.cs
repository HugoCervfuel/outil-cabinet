using System.Text.RegularExpressions;
using Cabinet.Core;
using MimeKit;

namespace Cabinet.Mail;

/// <summary>
/// Lit un e-mail déposé par l'avocat : fichier .eml (Outlook pour Mac, la plupart des messageries),
/// fichier .msg (Outlook pour Windows) ou texte copié-collé. Lecture seule, rien n'est envoyé.
/// </summary>
public static partial class MailReader
{
    private const string NoSubject = "(sans objet)";

    public static IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".eml", ".msg" };

    public static MailMessage FromFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".eml" => FromEml(path),
            ".msg" => FromMsg(path),
            _ => throw new NotSupportedException("Seuls les e-mails .eml et .msg sont acceptés."),
        };

    /// <summary>Texte copié depuis Outlook (« De : … Objet : … ») ou simple texte.</summary>
    public static MailMessage FromPastedText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string fromName = "", fromAddress = "", subject = NoSubject;
        var index = 0;
        var sawHeader = false;

        for (; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                if (sawHeader)
                {
                    index++;
                    break;
                }

                continue;
            }

            var match = HeaderLine().Match(line);
            if (!match.Success)
            {
                break;
            }

            sawHeader = true;
            var value = match.Groups["value"].Value.Trim();
            switch (match.Groups["name"].Value.ToLowerInvariant())
            {
                case "de":
                case "from":
                    (fromName, fromAddress) = ParseSender(value);
                    break;
                case "objet":
                case "subject":
                    subject = value.Length > 0 ? value : NoSubject;
                    break;
            }
        }

        if (!sawHeader)
        {
            index = 0;
        }

        var body = string.Join('\n', lines.Skip(index)).Trim();
        return new MailMessage(fromName, fromAddress, subject, null, body, Array.Empty<string>());
    }

    private static MailMessage FromEml(string path)
    {
        var message = MimeMessage.Load(path);
        var sender = message.From.Mailboxes.FirstOrDefault();
        var body = message.TextBody ?? ToText(message.HtmlBody) ?? "";
        var attachments = message.Attachments
            .Select(a => a.ContentDisposition?.FileName ?? a.ContentType?.Name ?? "")
            .Where(n => n.Length > 0)
            .ToList();

        return new MailMessage(
            string.IsNullOrWhiteSpace(sender?.Name) ? sender?.Address ?? "" : sender.Name,
            sender?.Address ?? "",
            string.IsNullOrWhiteSpace(message.Subject) ? NoSubject : message.Subject.Trim(),
            message.Date == DateTimeOffset.MinValue ? null : message.Date,
            body.Trim(),
            attachments);
    }

    private static MailMessage FromMsg(string path)
    {
        using var message = new MsgReader.Outlook.Storage.Message(path);
        var address = message.Sender?.Email ?? "";
        var name = message.Sender?.DisplayName;
        var body = message.BodyText ?? ToText(message.BodyHtml) ?? "";
        var attachments = message.Attachments
            .Select(a => a switch
            {
                MsgReader.Outlook.Storage.Attachment file => file.FileName ?? "",
                MsgReader.Outlook.Storage.Message embedded => (embedded.Subject ?? "e-mail joint") + ".msg",
                _ => "",
            })
            .Where(n => n.Length > 0)
            .ToList();

        return new MailMessage(
            string.IsNullOrWhiteSpace(name) ? address : name,
            address,
            string.IsNullOrWhiteSpace(message.Subject) ? NoSubject : message.Subject.Trim(),
            message.SentOn,
            body.Trim(),
            attachments);
    }

    /// <summary>Texte lisible d'un e-mail HTML : paragraphes et retours à la ligne gardés, balises retirées.</summary>
    private static string? ToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = HiddenBlocks().Replace(html, "");
        text = LineBreaks().Replace(text, "\n");
        text = Tags().Replace(text, "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = SpaceRuns().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        return string.Join('\n', text.Split('\n').Select(l => l.Trim())).Trim();
    }

    [GeneratedRegex(@"<(script|style|head)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|tr|h[1-6])>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex SpaceRuns();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLines();

    private static (string Name, string Address) ParseSender(string value)
    {
        var angle = AngleAddress().Match(value);
        if (angle.Success)
        {
            var name = value[..angle.Index].Trim().Trim('"', '\'');
            var address = angle.Groups[1].Value.Trim();
            return (name.Length > 0 ? name : address, address);
        }

        var bare = BareAddress().Match(value);
        if (bare.Success)
        {
            var name = value.Replace(bare.Value, "").Trim().Trim('"', '\'', '[', ']', '(', ')').Trim();
            return (name.Length > 0 ? name : bare.Value, bare.Value);
        }

        return (value, "");
    }

    [GeneratedRegex(@"^(?<name>De|From|Envoyé|Sent|Date|À|A|To|Cc|Objet|Subject|Importance)\s*:\s*(?<value>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"<\s*(?:mailto:)?([^<>\s]+@[^<>\s]+)\s*>")]
    private static partial Regex AngleAddress();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex BareAddress();
}
