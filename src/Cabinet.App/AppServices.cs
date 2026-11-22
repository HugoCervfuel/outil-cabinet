using Cabinet.Ai;
using Cabinet.Core;
using Cabinet.Platform;

namespace Cabinet.App;

/// <summary>Assemble le stockage chiffré, le suivi d'activité, la journée de travail et le courrier.</summary>
internal sealed class AppServices : IDisposable
{
    private readonly FileStream _instanceLock;

    private AppServices(WorkSession session, MailDesk desk, OllamaClient? ai, FileStream instanceLock, bool demo)
    {
        Session = session;
        Desk = desk;
        Ai = ai;
        _instanceLock = instanceLock;
        Demo = demo;
    }

    public WorkSession Session { get; }

    public MailDesk Desk { get; }

    /// <summary>L'IA locale (absente en mode démonstration, où des réponses fictives la remplacent).</summary>
    public OllamaClient? Ai { get; }

    public bool Demo { get; }

    public static string ModelFor(Settings settings)
        => string.IsNullOrWhiteSpace(settings.AiModel)
            ? OllamaClient.RecommendedModel(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, OllamaClient.IsAppleSilicon)
            : settings.AiModel.Trim();

    /// <summary>Null si l'outil est déjà ouvert (une seule instance à la fois).</summary>
    public static AppServices? Create()
    {
        var demo = Environment.GetEnvironmentVariable("CABINET_DEMO") == "1";
        var folder = demo ? Path.Combine(Path.GetTempPath(), "OutilCabinet-demo") : PlatformServices.DataFolder();
        Directory.CreateDirectory(folder);

        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(Path.Combine(folder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        IKeyStore keys = demo ? new FileKeyStore(Path.Combine(folder, "cle.bin")) : PlatformServices.CreateKeyStore(folder);
        var store = new EncryptedStore(folder, keys);
        var journal = new Journal(store);
        IActivityProbe probe = demo ? new DemoProbe() : PlatformServices.CreateActivityProbe();
        if (demo)
        {
            DemoData.Seed(journal);
        }

        var session = new WorkSession(journal, probe, () => DateTimeOffset.Now);
        var ai = demo ? null : new OllamaClient(ModelFor(session.Settings));
        IMailAssistant assistant = ai is null ? new DemoAssistant() : ai;
        var desk = new MailDesk(store, () => session.Dossiers, assistant, () => DateTimeOffset.Now);
        if (demo)
        {
            DemoData.SeedMail(desk, session);
        }

        return new AppServices(session, desk, ai, instanceLock, demo);
    }

    public void Dispose()
    {
        Ai?.Dispose();
        _instanceLock.Dispose();
    }
}

/// <summary>Mode démonstration (variable CABINET_DEMO=1) : données fictives, pour les captures d'écran.</summary>
internal sealed class DemoProbe : IActivityProbe
{
    public ActiveWindow? ReadActiveWindow() => new("Microsoft Word", "Conclusions_MARTIN_v3.docx");

    public TimeSpan? IdleTime() => TimeSpan.Zero;
}

/// <summary>Réponses fictives à la place de l'IA locale, en mode démonstration.</summary>
internal sealed class DemoAssistant : IMailAssistant
{
    public async Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
    {
        await Task.Delay(400, cancellationToken);
        if (user.Contains("commentaires", StringComparison.OrdinalIgnoreCase))
        {
            return """{"resume":"M. Martin transmet ses observations sur les conclusions adverses reçues le 2 novembre.","action":"Intégrer ses observations au projet de conclusions en réplique et lui confirmer la réception.","urgence":"haute","echeance":"2026-12-04","brouillon":"Cher Monsieur,\n\nJe vous remercie pour vos observations sur les conclusions adverses, que j'intègre dès à présent à notre projet de conclusions en réplique.\n\nJe vous adresserai ce projet pour relecture avant le [date d'envoi], afin de respecter le calendrier fixé avant l'audience du 15 janvier 2027.\n\nJe vous prie de croire, Cher Monsieur, en l'assurance de mes sentiments dévoués."}""";
        }

        if (user.Contains("renvoi", StringComparison.OrdinalIgnoreCase))
        {
            return """{"resume":"Le greffe informe du renvoi de l'audience de mise en état.","action":"Mettre à jour l'agenda et informer le client de la nouvelle date.","urgence":"normale","echeance":null,"brouillon":"Madame, Monsieur,\n\nJe vous informe que l'audience de mise en état initialement prévue a été renvoyée au [nouvelle date] par le tribunal.\n\nCe renvoi est sans incidence sur notre stratégie. Je reste à votre disposition pour tout échange.\n\nBien cordialement."}""";
        }

        return """{"resume":"Lettre d'information d'un éditeur juridique.","action":"Aucune réponse nécessaire.","urgence":"basse","echeance":null,"brouillon":""}""";
    }
}

internal static class DemoData
{
    public static void Seed(Journal journal)
    {
        if (journal.LoadDossiers().Count > 0)
        {
            return;
        }

        var martin = Dossier.Create("MARTIN c/ Axa", new[] { "martin", "axa" }, "2026-0142") with
        {
            Notes = "Assignation en paiement de l'indemnité d'assurance. Conclusions adverses reçues le 02/11. Audience de plaidoiries le 15/01/2027. Attente des observations du client.",
            Addresses = new[] { "p.martin@exemple.fr" },
        };
        var dupont = Dossier.Create("DUPONT / SCI Les Lys", new[] { "dupont", "lys" }, "2026-0098") with
        {
            Notes = "Litige locatif. Mise en état en cours.",
        };
        var bernard = Dossier.Create("BERNARD (divorce)", new[] { "bernard" }, "2025-0311");
        journal.SaveDossiers(new[] { martin, dupont, bernard });

        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = new DateTimeOffset(today.ToDateTime(new TimeOnly(9, 0)), DateTimeOffset.Now.Offset);
        Segment S(int from, int minutes, string app, string title) => new(start.AddMinutes(from), start.AddMinutes(from + minutes), app, title, null);
        journal.SaveDay(new DayRecord(today, new List<Segment>
        {
            S(0, 62, "Microsoft Word", "Assignation_DUPONT_SCI_Lys.docx"),
            S(62, 14, "Microsoft Outlook", "RE: SCI Les Lys - pièces"),
            S(80, 38, "Aperçu", "Pièce 12 - Expertise Dupont.pdf"),
            S(130, 27, "Microsoft Outlook", "Martin / Axa : proposition"),
            S(160, 9, "Finder", "Documents"),
            S(170, 25, "Google Chrome", "Légifrance - Code des assurances"),
        }, null));
    }

    public static void SeedMail(MailDesk desk, WorkSession session)
    {
        if (desk.Items.Count > 0)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        desk.Add(new MailMessage("Paul Martin", "p.martin@exemple.fr", "Mes commentaires sur les conclusions d'Axa", now.AddHours(-1),
            "Bonjour Maître,\n\nVoici mes commentaires sur les conclusions adverses. Je conteste en particulier le calcul de la vétusté.\n\nBien à vous,\nPaul Martin",
            new[] { "commentaires-martin.docx" }));
        desk.Add(new MailMessage("Greffe TJ", "greffe@tj-exemple.fr", "SCI Les Lys - renvoi de la mise en état", now.AddHours(-3),
            "Maître,\n\nL'audience de mise en état est renvoyée. Une nouvelle date vous sera communiquée.\n\nLe greffe", Array.Empty<string>()));
        desk.Add(new MailMessage("Éditions Juridiques", "news@editeur-exemple.fr", "Votre lettre d'actualité de novembre", now.AddHours(-5),
            "Découvrez les nouveautés du mois…", Array.Empty<string>()));
    }
}
