namespace Cabinet.Core.Tests;

public class MailMatchingTests
{
    private static readonly Dossier Martin = Dossier.Create("MARTIN c/ Axa", new[] { "martin", "axa" }, "2026-0142")
        with { Addresses = new[] { "p.martin@exemple.fr" } };
    private static readonly Dossier Dupont = Dossier.Create("DUPONT / SCI Les Lys", new[] { "dupont", "lys" }, null);

    private static MailMessage Mail(string from, string subject, string body = "")
        => new("Expéditeur", from, subject, null, body, Array.Empty<string>());

    [Fact]
    public void KnownAddress_WinsEvenWithoutKeyword()
        => Assert.Equal(Martin.Id, MailDossierMatcher.Match(Mail("P.Martin@exemple.fr", "Question"), new[] { Martin, Dupont }));

    [Fact]
    public void SubjectKeyword_FindsDossier()
        => Assert.Equal(Dupont.Id, MailDossierMatcher.Match(Mail("greffe@tj.fr", "SCI Les Lys - renvoi"), new[] { Martin, Dupont }));

    [Fact]
    public void BodyKeyword_FindsDossierWhenSubjectIsVague()
        => Assert.Equal(Dupont.Id, MailDossierMatcher.Match(Mail("x@y.fr", "Votre courrier", "Concernant le dossier Dupont, je vous informe..."), new[] { Martin, Dupont }));

    [Fact]
    public void NothingKnown_IsNull()
        => Assert.Null(MailDossierMatcher.Match(Mail("x@y.fr", "Newsletter"), new[] { Martin, Dupont }));
}

public class PromptTests
{
    private static readonly MailMessage Mail = new("Paul Martin", "p.martin@exemple.fr", "Mes commentaires", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(1)),
        "Bonjour Maître, voici mes commentaires sur les conclusions adverses.", new[] { "commentaires.docx" });

    [Fact]
    public void Prompt_ContainsDossierContextAndMail()
    {
        var dossier = Dossier.Create("MARTIN c/ Axa", new[] { "martin" }, "2026-0142") with { Notes = "Audience le 15/01/2027. Conclusions à rendre." };
        var (system, user) = PromptBuilder.ForMail(Mail, dossier, new[] { "Envoi de l'argumentaire adverse au client" }, new DateOnly(2026, 11, 22));
        Assert.Contains("JSON", system);
        Assert.Contains("[", system); // consigne : informations manquantes entre crochets
        Assert.Contains("MARTIN c/ Axa", user);
        Assert.Contains("2026-0142", user);
        Assert.Contains("Audience le 15/01/2027", user);
        Assert.Contains("Envoi de l'argumentaire adverse au client", user);
        Assert.Contains("Mes commentaires", user);
        Assert.Contains("commentaires.docx", user);
        Assert.Contains("22/11/2026", user);
    }

    [Fact]
    public void Prompt_WithoutDossier_SaysSo()
        => Assert.Contains("non identifié", PromptBuilder.ForMail(Mail, null, Array.Empty<string>(), new DateOnly(2026, 11, 22)).User);

    [Fact]
    public void Prompt_TruncatesVeryLongBody()
    {
        var longMail = Mail with { Body = new string('a', 50_000) };
        Assert.True(PromptBuilder.ForMail(longMail, null, Array.Empty<string>(), new DateOnly(2026, 11, 22)).User.Length < 12_000);
    }
}

public class AnalysisParserTests
{
    [Fact]
    public void Parse_CleanJson()
    {
        var a = AnalysisParser.Parse("""{"resume":"Le client envoie ses commentaires.","action":"Intégrer les commentaires aux conclusions.","urgence":"haute","echeance":"2026-12-01","brouillon":"Cher Monsieur,\nMerci."}""");
        Assert.NotNull(a);
        Assert.Equal("Le client envoie ses commentaires.", a!.Summary);
        Assert.Equal(Urgency.Haute, a.Urgency);
        Assert.Equal(new DateOnly(2026, 12, 1), a.Deadline);
        Assert.Contains("Merci.", a.DraftReply);
    }

    [Fact]
    public void Parse_JsonInsideText_AndAccentedKeys()
    {
        var a = AnalysisParser.Parse("Voici l'analyse :\n```json\n{\"résumé\":\"R\",\"action\":\"A\",\"urgence\":\"Normale\",\"échéance\":null,\"brouillon\":\"B\"}\n```");
        Assert.Equal("R", a!.Summary);
        Assert.Equal(Urgency.Normale, a.Urgency);
        Assert.Null(a.Deadline);
    }

    [Fact]
    public void Parse_UnknownUrgency_IsNormale()
        => Assert.Equal(Urgency.Normale, AnalysisParser.Parse("""{"resume":"R","action":"A","urgence":"très urgent !!","brouillon":"B"}""")!.Urgency);

    [Fact]
    public void Parse_FrenchDate()
        => Assert.Equal(new DateOnly(2027, 1, 15), AnalysisParser.Parse("""{"resume":"R","action":"A","echeance":"15/01/2027","brouillon":"B"}""")!.Deadline);

    [Theory]
    [InlineData("")]
    [InlineData("pas de JSON ici")]
    [InlineData("""{"brouillon":"B"}""")]
    public void Parse_Unusable_IsNull(string raw)
        => Assert.Null(AnalysisParser.Parse(raw));
}

public class MailDeskTests : IDisposable
{
    private sealed class FixedKey : IKeyStore
    {
        public byte[] GetOrCreateKey() => Enumerable.Repeat((byte)3, 32).ToArray();
    }

    private sealed class FakeAssistant(string answer) : IMailAssistant
    {
        public List<(string System, string User)> Calls { get; } = new();

        public bool Throws { get; set; }

        public Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
        {
            Calls.Add((system, user));
            return Throws ? throw new InvalidOperationException("Ollama absent") : Task.FromResult(answer);
        }
    }

    private const string Answer = """{"resume":"Le client envoie ses commentaires.","action":"Intégrer les commentaires.","urgence":"haute","echeance":null,"brouillon":"Cher Monsieur, bien reçu."}""";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cabinet-mail-" + Guid.NewGuid().ToString("N"));
    private readonly Dossier _martin = Dossier.Create("MARTIN c/ Axa", new[] { "martin" }, null) with { Notes = "Audience le 15/01." };

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private MailDesk Desk(IMailAssistant assistant)
        => new(new EncryptedStore(_folder, new FixedKey()), () => new[] { _martin }, assistant, () => new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(1)));

    private static MailMessage Mail(string subject = "Dossier Martin : mes commentaires")
        => new("Paul Martin", "p.martin@exemple.fr", subject, null, "Bonjour Maître", Array.Empty<string>());

    [Fact]
    public void Add_MatchesDossierAndIsPending()
    {
        var desk = Desk(new FakeAssistant(Answer));
        var item = desk.Add(Mail());
        Assert.Equal(_martin.Id, item.DossierId);
        Assert.Equal(MailStatus.ATraiter, item.Status);
        Assert.Equal(1, desk.PendingCount);
    }

    [Fact]
    public async Task Analyze_StoresAnalysisAndUsesDossierNotes()
    {
        var assistant = new FakeAssistant(Answer);
        var desk = Desk(assistant);
        var item = desk.Add(Mail());
        var analyzed = await desk.AnalyzeAsync(item.Id, CancellationToken.None);
        Assert.Equal("Intégrer les commentaires.", analyzed.Analysis!.Action);
        Assert.Contains("Audience le 15/01.", assistant.Calls.Single().User);
    }

    [Fact]
    public async Task Analyze_IncludesPreviousSummariesOfSameDossier()
    {
        var assistant = new FakeAssistant(Answer);
        var desk = Desk(assistant);
        await desk.AnalyzeAsync(desk.Add(Mail()).Id, CancellationToken.None);
        await desk.AnalyzeAsync(desk.Add(Mail("Martin : relance")).Id, CancellationToken.None);
        Assert.Contains("Le client envoie ses commentaires.", assistant.Calls[1].User);
    }

    [Fact]
    public async Task Analyze_AssistantFailure_RecordsErrorWithoutThrowing()
    {
        var desk = Desk(new FakeAssistant(Answer) { Throws = true });
        var item = await desk.AnalyzeAsync(desk.Add(Mail()).Id, CancellationToken.None);
        Assert.Null(item.Analysis);
        Assert.NotNull(item.AnalysisError);
    }

    [Fact]
    public async Task Analyze_UnreadableAnswer_RecordsError()
    {
        var desk = Desk(new FakeAssistant("je ne sais pas"));
        var item = await desk.AnalyzeAsync(desk.Add(Mail()).Id, CancellationToken.None);
        Assert.Null(item.Analysis);
        Assert.NotNull(item.AnalysisError);
    }

    [Fact]
    public void Items_PersistEncrypted()
    {
        Desk(new FakeAssistant(Answer)).Add(Mail("CONFIDENTIEL Martin"));
        var reopened = Desk(new FakeAssistant(Answer));
        Assert.Single(reopened.Items);
        foreach (var file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("CONFIDENTIEL", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        }
    }

    [Fact]
    public void MarkDone_UpdatesPendingCount()
    {
        var desk = Desk(new FakeAssistant(Answer));
        var item = desk.Add(Mail());
        desk.SetStatus(item.Id, MailStatus.Traite);
        Assert.Equal(0, desk.PendingCount);
    }

    [Fact]
    public void UpdateDraft_KeepsLawyerEdits()
    {
        var desk = Desk(new FakeAssistant(Answer));
        var item = desk.Add(Mail());
        desk.UpdateDraft(item.Id, "Mon texte corrigé");
        Assert.Equal("Mon texte corrigé", desk.Items.Single().EditedDraft);
    }
}
