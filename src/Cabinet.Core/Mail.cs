using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cabinet.Core;

public enum Urgency { Basse, Normale, Haute }

public enum MailStatus { ATraiter, Traite }

/// <summary>Un e-mail reçu, tel que l'outil le lit. Donnée client : il reste chiffré sur le poste.</summary>
public sealed record MailMessage(string FromName, string FromAddress, string Subject, DateTimeOffset? Date, string Body, IReadOnlyList<string> Attachments);

/// <summary>Ce que l'assistant propose pour un e-mail.</summary>
public sealed record MailAnalysis(string Summary, string Action, Urgency Urgency, DateOnly? Deadline, string DraftReply);

/// <summary>Un e-mail déposé dans l'outil, avec son dossier, son analyse et son état.</summary>
public sealed record MailItem(
    Guid Id,
    DateTimeOffset AddedAt,
    MailMessage Message,
    Guid? DossierId,
    MailAnalysis? Analysis,
    string? AnalysisError,
    string? EditedDraft,
    MailStatus Status)
{
    /// <summary>Le brouillon à proposer : celui corrigé par l'avocat s'il existe, sinon celui de l'assistant.</summary>
    public string Draft => EditedDraft ?? Analysis?.DraftReply ?? "";
}

/// <summary>Le modèle d'IA local, vu par le cœur : on lui donne des consignes et un texte, il renvoie du texte.</summary>
public interface IMailAssistant
{
    Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken);
}

/// <summary>Rattache un e-mail à un dossier : adresse connue, puis objet, puis corps.</summary>
public static class MailDossierMatcher
{
    private const int BodyCharacters = 3000;

    public static Guid? Match(MailMessage mail, IReadOnlyList<Dossier> dossiers)
    {
        var byAddress = dossiers
            .Where(d => !d.Archived && d.Addresses is not null
                        && d.Addresses.Any(a => string.Equals(a.Trim(), mail.FromAddress.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.Id)
            .ToList();
        if (byAddress.Count == 1)
        {
            return byAddress[0];
        }

        var body = mail.Body.Length > BodyCharacters ? mail.Body[..BodyCharacters] : mail.Body;
        return DossierMatcher.Match(mail.Subject, dossiers)
               ?? DossierMatcher.Match(mail.Subject + " " + mail.FromName + " " + body, dossiers);
    }
}

/// <summary>Les consignes données au modèle local.</summary>
public static class PromptBuilder
{
    private const int MaxBodyCharacters = 8000;
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public const string SystemPrompt =
        "Tu es l'assistant de secrétariat d'un cabinet d'avocats français. Tu lis un e-mail reçu par l'avocat " +
        "et tu prépares son traitement. Réponds UNIQUEMENT par un objet JSON avec exactement ces clés :\n" +
        "- \"resume\" : une phrase qui résume l'e-mail ;\n" +
        "- \"action\" : une phrase, à l'infinitif, qui dit ce que le cabinet doit faire (ex. « Transmettre les conclusions adverses au client et lui demander ses observations ») ;\n" +
        "- \"urgence\" : \"basse\", \"normale\" ou \"haute\" ;\n" +
        "- \"echeance\" : la date limite au format AAAA-MM-JJ si l'e-mail ou le dossier en indique une, sinon null ;\n" +
        "- \"brouillon\" : une réponse prête à relire, en français soutenu, au vouvoiement, adaptée au destinataire (client, confrère, greffe), sans signature nominative.\n" +
        "Règles : n'invente aucun fait, aucune date, aucun montant. Si une information manque pour répondre, " +
        "écris-la entre crochets dans le brouillon, par exemple [date de l'audience]. Ne donne pas d'avis juridique de fond " +
        "qui n'a pas été demandé. Si l'e-mail n'appelle pas de réponse (publicité, simple information), indique-le dans " +
        "l'action et laisse un brouillon vide.";

    public static (string System, string User) ForMail(MailMessage mail, Dossier? dossier, IReadOnlyList<string> recentSummaries, DateOnly today)
    {
        var user = new StringBuilder();
        user.AppendLine($"Date du jour : {today.ToString("dd/MM/yyyy", French)}");
        user.AppendLine();

        if (dossier is null)
        {
            user.AppendLine("Dossier : non identifié.");
        }
        else
        {
            user.AppendLine($"Dossier : {dossier.Name}" + (dossier.KleosReference is { } reference ? $" (référence {reference})" : ""));
            user.AppendLine("Où en est le dossier : " + (string.IsNullOrWhiteSpace(dossier.Notes) ? "non renseigné." : dossier.Notes.Trim()));
            if (recentSummaries.Count > 0)
            {
                user.AppendLine("Derniers échanges dans ce dossier :");
                foreach (var summary in recentSummaries)
                {
                    user.AppendLine("- " + summary);
                }
            }
        }

        user.AppendLine();
        user.AppendLine("E-mail reçu :");
        user.AppendLine($"De : {mail.FromName} <{mail.FromAddress}>");
        if (mail.Date is { } date)
        {
            user.AppendLine($"Date : {date.ToString("dd/MM/yyyy HH:mm", French)}");
        }

        user.AppendLine($"Objet : {mail.Subject}");
        if (mail.Attachments.Count > 0)
        {
            user.AppendLine("Pièces jointes : " + string.Join(", ", mail.Attachments));
        }

        user.AppendLine();
        var body = mail.Body.Trim();
        user.AppendLine(body.Length > MaxBodyCharacters ? body[..MaxBodyCharacters] + "\n[… e-mail tronqué …]" : body);
        return (SystemPrompt, user.ToString());
    }
}

/// <summary>Lit la réponse du modèle, même imparfaite (texte autour, clés accentuées, date à la française).</summary>
public static class AnalysisParser
{
    public static MailAnalysis? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw[start..(end + 1)]);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var fields = document.RootElement.EnumerateObject()
                .GroupBy(p => TextNormalizer.Normalize(p.Name))
                .ToDictionary(g => g.Key, g => g.First().Value);

            string? Text(string key) => fields.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

            var summary = Text("resume");
            var action = Text("action");
            if (string.IsNullOrEmpty(summary) || string.IsNullOrEmpty(action))
            {
                return null;
            }

            var urgency = TextNormalizer.Normalize(Text("urgence")) switch
            {
                "haute" or "elevee" or "urgente" => Urgency.Haute,
                "basse" or "faible" => Urgency.Basse,
                _ => Urgency.Normale,
            };

            return new MailAnalysis(summary, action, urgency, ParseDate(Text("echeance")), Text("brouillon") ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var format in new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" })
        {
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
        }

        return null;
    }
}

/// <summary>Le bureau du courrier : e-mails déposés, analyses, brouillons et état, chiffrés sur le poste.</summary>
public sealed class MailDesk
{
    private const string StoreName = "courrier";
    private const int RecentSummaries = 5;

    private readonly EncryptedStore _store;
    private readonly Func<IReadOnlyList<Dossier>> _dossiers;
    private readonly IMailAssistant _assistant;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<MailItem> _items;
    private readonly object _gate = new();

    public MailDesk(EncryptedStore store, Func<IReadOnlyList<Dossier>> dossiers, IMailAssistant assistant, Func<DateTimeOffset> clock)
    {
        _store = store;
        _dossiers = dossiers;
        _assistant = assistant;
        _clock = clock;
        _items = store.Load<List<MailItem>>(StoreName) ?? new List<MailItem>();
    }

    public IReadOnlyList<MailItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.OrderByDescending(i => i.Message.Date ?? i.AddedAt).ToList();
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count(i => i.Status == MailStatus.ATraiter);
            }
        }
    }

    public MailItem Add(MailMessage message)
    {
        var item = new MailItem(Guid.NewGuid(), _clock(), message, MailDossierMatcher.Match(message, _dossiers()), null, null, null, MailStatus.ATraiter);
        Mutate(items => items.Add(item));
        return item;
    }

    public async Task<MailItem> AnalyzeAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = Find(id) ?? throw new KeyNotFoundException();
        var dossier = _dossiers().FirstOrDefault(d => d.Id == item.DossierId);
        var recent = Items
            .Where(i => i.Id != id && i.DossierId is not null && i.DossierId == item.DossierId && i.Analysis is not null)
            .Take(RecentSummaries)
            .Select(i => i.Analysis!.Summary)
            .ToList();
        var (system, user) = PromptBuilder.ForMail(item.Message, dossier, recent, DateOnly.FromDateTime(_clock().Date));

        MailItem updated;
        try
        {
            var answer = await _assistant.CompleteAsync(system, user, cancellationToken);
            var analysis = AnalysisParser.Parse(answer);
            updated = analysis is null
                ? item with { AnalysisError = "La réponse de l'IA n'a pas pu être lue. Relancez l'analyse." }
                : item with { Analysis = analysis, AnalysisError = null, EditedDraft = null };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            updated = item with { AnalysisError = "L'IA locale ne répond pas. Vérifiez qu'Ollama est ouvert, puis relancez l'analyse." };
        }

        Replace(updated);
        return updated;
    }

    public void SetDossier(Guid id, Guid? dossierId) => Update(id, i => i with { DossierId = dossierId });

    public void SetStatus(Guid id, MailStatus status) => Update(id, i => i with { Status = status });

    public void UpdateDraft(Guid id, string draft) => Update(id, i => i with { EditedDraft = draft });

    public void Remove(Guid id) => Mutate(items => items.RemoveAll(i => i.Id == id));

    private MailItem? Find(Guid id)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(i => i.Id == id);
        }
    }

    private void Update(Guid id, Func<MailItem, MailItem> change)
    {
        if (Find(id) is { } item)
        {
            Replace(change(item));
        }
    }

    private void Replace(MailItem item) => Mutate(items =>
    {
        var index = items.FindIndex(i => i.Id == item.Id);
        if (index >= 0)
        {
            items[index] = item;
        }
    });

    private void Mutate(Action<List<MailItem>> change)
    {
        lock (_gate)
        {
            change(_items);
            _store.Save(StoreName, _items);
        }
    }
}
