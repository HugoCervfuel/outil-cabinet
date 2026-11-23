using Cabinet.Ai;
using Cabinet.Core;
using Cabinet.MailSync;
using Cabinet.Platform;

namespace Cabinet.App;

/// <summary>Assemble le stockage chiffré, le suivi d'activité, la journée de travail et le courrier.</summary>
internal sealed class AppServices : IDisposable
{
    private readonly FileStream _instanceLock;

    private AppServices(WorkSession session, MailDesk desk, InstructionDesk instruction, MailboxSync mailbox, OllamaClient? ai, FileStream instanceLock, bool demo)
    {
        Session = session;
        Desk = desk;
        Mailbox = mailbox;
        Instruction = instruction;
        Ai = ai;
        _instanceLock = instanceLock;
        Demo = demo;
    }

    public WorkSession Session { get; }

    public MailDesk Desk { get; }

    public InstructionDesk Instruction { get; }

    /// <summary>Le relevé de la boîte de réception (Proton Mail Bridge, sur cet ordinateur).</summary>
    public MailboxSync Mailbox { get; }

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
        ITextModel textModel = ai is null ? new DemoTextModel() : ai;
        var instruction = new InstructionDesk(store, () => session.Dossiers, textModel, () => DateTimeOffset.Now);
        IMailboxSource source = demo ? new DemoMailboxSource() : new ProtonBridgeSource();
        var mailbox = new MailboxSync(store, desk, source, () => DateTimeOffset.Now);
        if (demo)
        {
            DemoData.SeedMail(desk, session);
            DemoData.SeedPieces(instruction, session);
            if (!mailbox.IsConfigured)
            {
                mailbox.SaveAccount(new MailAccount("avocat@exemple.fr", "demonstration"));
            }
        }

        return new AppServices(session, desk, instruction, mailbox, ai, instanceLock, demo);
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

/// <summary>Une boîte de réception fictive, en mode démonstration : un e-mail au premier relevé, puis plus rien.</summary>
internal sealed class DemoMailboxSource : IMailboxSource
{
    public async Task<FetchResult> FetchAsync(MailAccount account, uint? afterUid, uint? uidValidity, DateTimeOffset since, int max, CancellationToken cancellationToken)
    {
        await Task.Delay(700, cancellationToken);
        if (afterUid is not null)
        {
            return new FetchResult(1, Array.Empty<FetchedMail>(), afterUid);
        }

        var mail = new MailMessage(
            "Greffe du tribunal judiciaire",
            "greffe.tj@exemple.fr",
            "MARTIN c/ SCI LES TILLEULS — renvoi de l'audience de mise en état",
            DateTimeOffset.Now.AddMinutes(-12),
            "Maître,\n\nNous vous informons du renvoi de l'audience de mise en état dans l'affaire MARTIN c/ SCI LES TILLEULS au 12 février 2027 à 9 h 30.\n\nLe greffe",
            Array.Empty<string>(),
            "<demo-renvoi@exemple.fr>");
        return new FetchResult(1, new[] { new FetchedMail(1, mail, false) }, 1);
    }

    public Task TestAsync(MailAccount account, CancellationToken cancellationToken) => Task.CompletedTask;
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

/// <summary>Réponses fictives pour l'instruction, en mode démonstration.</summary>
internal sealed class DemoTextModel : ITextModel
{
    public async Task<string> GenerateAsync(string system, string user, bool json, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);
        if (json)
        {
            return user.Contains("Expertise", StringComparison.Ordinal)
                ? """{"resume":"L'expert judiciaire constate des infiltrations dans la cave et chiffre les travaux de reprise à 18 400 euros HT. Il retient une vétusté de 20 %.","evenements":[{"date":"2025-02-03","fait":"Réunion d'expertise contradictoire sur place","page":1},{"date":"2025-06-12","fait":"Dépôt du rapport d'expertise","page":4}]}"""
                : """{"resume":"Axa refuse sa garantie en invoquant l'exclusion pour défaut d'entretien.","evenements":[{"date":"2024-11-18","fait":"Déclaration de sinistre par M. Martin","page":1},{"date":"2025-01-07","fait":"Refus de garantie d'Axa","page":1}]}""";
        }

        if (user.Contains("Question de l'avocat", StringComparison.Ordinal))
        {
            return "L'expert chiffre les travaux de reprise à 18 400 euros HT et retient une vétusté de 20 % [Expertise judiciaire.pdf, p. 3]. "
                   + "Il rattache les infiltrations à un défaut d'étanchéité de la façade, et non à un défaut d'entretien [Expertise judiciaire.pdf, p. 2].";
        }

        if (user.Contains("Document à préparer", StringComparison.Ordinal))
        {
            return "Cher Monsieur,\n\nL'expert judiciaire a déposé son rapport le 12 juin 2025. Il chiffre les travaux de reprise à 18 400 euros HT et retient une vétusté de 20 % [Expertise judiciaire.pdf, p. 3].\n\n"
                   + "Surtout, il rattache les infiltrations à un défaut d'étanchéité de la façade, ce qui contredit l'exclusion invoquée par Axa [Courrier Axa.pdf, p. 1].\n\n"
                   + "Je vous propose d'en discuter lors d'un rendez-vous le [date à convenir].\n\nJe vous prie de croire, Cher Monsieur, en l'assurance de mes sentiments dévoués.";
        }

        return "Les faits\nM. Martin a déclaré un sinistre (infiltrations dans la cave) le 18 novembre 2024 [Courrier Axa.pdf, p. 1]. Axa a refusé sa garantie le 7 janvier 2025 pour défaut d'entretien.\n\n"
               + "Les points en litige\nL'application de l'exclusion pour défaut d'entretien, et le montant de l'indemnité (vétusté de 20 % retenue par l'expert).\n\n"
               + "Les pièces importantes\nLe rapport d'expertise judiciaire [Expertise judiciaire.pdf, p. 2-4].\n\n"
               + "Les points à vérifier\nLa date exacte de la dernière visite d'entretien de la façade [à demander au client].";
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

    public static void SeedPieces(InstructionDesk instruction, WorkSession session)
    {
        if (session.Dossiers.FirstOrDefault(d => d.Name.StartsWith("MARTIN", StringComparison.Ordinal)) is not { } martin
            || instruction.Pieces(martin.Id).Count > 0)
        {
            return;
        }

        instruction.AddPiece(martin.Id, "Expertise judiciaire.pdf", new[]
        {
            new PiecePage(1, "Rapport d'expertise judiciaire. Réunion contradictoire sur place le 3 février 2025."),
            new PiecePage(2, "Les infiltrations proviennent d'un défaut d'étanchéité de la façade."),
            new PiecePage(3, "Coût des travaux de reprise : 18 400 euros HT. Vétusté retenue : 20 %."),
            new PiecePage(4, "Rapport déposé le 12 juin 2025."),
        });
        instruction.AddPiece(martin.Id, "Courrier Axa.pdf", new[]
        {
            new PiecePage(1, "Suite à votre déclaration du 18 novembre 2024, nous refusons notre garantie (exclusion pour défaut d'entretien). Le 7 janvier 2025."),
        });
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
