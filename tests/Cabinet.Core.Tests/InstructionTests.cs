namespace Cabinet.Core.Tests;

public class ChunkerAndRetrieverTests
{
    private static Piece Piece(params string[] pages)
        => new(Guid.NewGuid(), Guid.NewGuid(), "Expertise.pdf", DateTimeOffset.Now,
            pages.Select((t, i) => new PiecePage(i + 1, t)).ToList(), LooksScanned: false);

    [Fact]
    public void Split_SmallPiece_IsOneChunkStartingAtPageOne()
    {
        var chunk = Assert.Single(Chunker.Split(Piece("Page un.", "Page deux."), 3000));
        Assert.Equal(1, chunk.Page);
        Assert.Contains("[page 2]", chunk.Text);
    }

    [Fact]
    public void Split_LongPiece_RespectsMaximumAndKeepsPageNumbers()
    {
        var pages = Enumerable.Range(1, 10).Select(i => $"Page {i}. " + new string('x', 1400)).ToArray();
        var chunks = Chunker.Split(Piece(pages), 3000);
        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 3200));
        Assert.Equal(1, chunks[0].Page);
        Assert.True(chunks[^1].Page > 5);
    }

    [Fact]
    public void Split_VeryLongPage_IsCutToo()
    {
        var chunks = Chunker.Split(Piece(new string('y', 10_000)), 3000);
        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.Equal(1, c.Page));
    }

    [Fact]
    public void Search_FindsTheRelevantChunkFirst_IgnoringAccents()
    {
        var piece = Piece(
            "Le contrat de bail a été signé entre les parties.",
            "L'expert relève des désordres d'humidité dans la cave.",
            "La facture de réparation s'élève à 12 000 euros.");
        var chunks = Chunker.Split(piece, 60);
        var best = Retriever.Search("Quels desordres l'expert a-t-il constatés ?", chunks, 1).Single();
        Assert.Contains("humidité", best.Text);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
        => Assert.Empty(Retriever.Search("tribunal", Chunker.Split(Piece("Rien à voir."), 3000), 3));
}

public class DigestAndChronologyTests
{
    private static readonly Guid PieceId = Guid.NewGuid();

    [Fact]
    public void Parse_Digest_WithEvents()
    {
        var digest = DigestParser.Parse(PieceId, "Bail.pdf", """{"resume":"Bail commercial signé.","evenements":[{"date":"2024-03-01","fait":"Signature du bail","page":2},{"date":"2025-01","fait":"Premier impayé","page":5}]}""");
        Assert.NotNull(digest);
        Assert.Equal("Bail commercial signé.", digest!.Summary);
        Assert.Equal(2, digest.Events.Count);
        Assert.Equal("Bail.pdf, p. 2", digest.Events[0].Source);
    }

    [Fact]
    public void Parse_Digest_Unusable_IsNull()
        => Assert.Null(DigestParser.Parse(PieceId, "x.pdf", "désolé"));

    [Theory]
    [InlineData("2024-03-01", 2024, 3, 1)]
    [InlineData("01/03/2024", 2024, 3, 1)]
    [InlineData("2025-01", 2025, 1, 1)]
    [InlineData("mars 2024", 2024, 3, 1)]
    [InlineData("1er mars 2024", 2024, 3, 1)]
    [InlineData("2024", 2024, 1, 1)]
    public void ParseDate_CommonForms(string text, int y, int m, int d)
        => Assert.Equal(new DateOnly(y, m, d), Chronology.ParseDate(text));

    [Fact]
    public void Build_SortsByDate_UndatedLast_AndRemovesDuplicates()
    {
        var a = new PieceDigest(Guid.NewGuid(), "A", new[]
        {
            new DatedEvent("2025-01-10", "Mise en demeure", "A.pdf, p. 1"),
            new DatedEvent("date inconnue", "Réunion d'expertise", "A.pdf, p. 3"),
        });
        var b = new PieceDigest(Guid.NewGuid(), "B", new[]
        {
            new DatedEvent("2024-03-01", "Signature du bail", "B.pdf, p. 2"),
            new DatedEvent("10/01/2025", "Mise en demeure", "B.pdf, p. 4"),
        });
        var events = Chronology.Build(new[] { a, b });
        Assert.Equal(new[] { "Signature du bail", "Mise en demeure", "Réunion d'expertise" }, events.Select(e => e.Fact));
        Assert.Contains("B.pdf, p. 4", events[1].Source);
    }
}

public class InstructionDeskTests : IDisposable
{
    private sealed class FixedKey : IKeyStore
    {
        public byte[] GetOrCreateKey() => Enumerable.Repeat((byte)6, 32).ToArray();
    }

    private sealed class FakeModel : ITextModel
    {
        public List<(string System, string User, bool Json)> Calls { get; } = new();

        public bool Fail { get; set; }

        public Task<string> GenerateAsync(string system, string user, bool json, CancellationToken cancellationToken)
        {
            Calls.Add((system, user, json));
            if (Fail)
            {
                throw new InvalidOperationException("Ollama absent");
            }

            return Task.FromResult(json
                ? """{"resume":"L'expert constate des infiltrations.","evenements":[{"date":"2025-02-03","fait":"Réunion d'expertise","page":1}]}"""
                : "Réponse rédigée [Expertise.pdf, p. 1].");
        }
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cabinet-instr-" + Guid.NewGuid().ToString("N"));
    private readonly Dossier _dossier = Dossier.Create("MARTIN c/ Axa", new[] { "martin" }, null) with { Notes = "Audience le 15/01/2027." };

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private InstructionDesk Desk(ITextModel model)
        => new(new EncryptedStore(_folder, new FixedKey()), () => new[] { _dossier }, model, () => new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));

    private static IReadOnlyList<PiecePage> Pages(params string[] texts) => texts.Select((t, i) => new PiecePage(i + 1, t)).ToList();

    [Fact]
    public void AddPiece_IsStoredEncryptedAndNeedsDigest()
    {
        var desk = Desk(new FakeModel());
        var piece = desk.AddPiece(_dossier.Id, "Expertise.pdf", Pages("CONFIDENTIEL infiltrations dans la cave"));
        Assert.True(desk.NeedsDigest(piece));
        Assert.Single(Desk(new FakeModel()).Pieces(_dossier.Id));
        foreach (var file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("CONFIDENTIEL", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        }
    }

    [Fact]
    public void AddPiece_ScannedPdf_IsFlagged()
    {
        var piece = Desk(new FakeModel()).AddPiece(_dossier.Id, "Scan.pdf", Pages("", " ", ""));
        Assert.True(piece.LooksScanned);
    }

    [Fact]
    public async Task Digest_StoresSummaryAndEvents()
    {
        var model = new FakeModel();
        var desk = Desk(model);
        var piece = desk.AddPiece(_dossier.Id, "Expertise.pdf", Pages("L'expert constate des infiltrations."));
        var digest = await desk.DigestAsync(piece.Id, CancellationToken.None);
        Assert.Equal("L'expert constate des infiltrations.", digest!.Summary);
        Assert.False(desk.NeedsDigest(piece));
        Assert.True(model.Calls.Single().Json);
        Assert.Contains("Réunion d'expertise", Desk(new FakeModel()).Chronology(_dossier.Id).Single().Fact);
    }

    [Fact]
    public async Task Summary_UsesNotesAndDigests()
    {
        var model = new FakeModel();
        var desk = Desk(model);
        var piece = desk.AddPiece(_dossier.Id, "Expertise.pdf", Pages("L'expert constate des infiltrations."));
        await desk.DigestAsync(piece.Id, CancellationToken.None);
        var result = await desk.SummarizeAsync(_dossier.Id, CancellationToken.None);
        var call = model.Calls.Last();
        Assert.Contains("Audience le 15/01/2027.", call.User);
        Assert.Contains("L'expert constate des infiltrations.", call.User);
        Assert.False(call.Json);
        Assert.Equal("Réponse rédigée [Expertise.pdf, p. 1].", result.Text);
        Assert.Single(desk.Results(_dossier.Id));
    }

    [Fact]
    public async Task Ask_SendsTheRelevantExtractsWithTheirSource()
    {
        var model = new FakeModel();
        var desk = Desk(model);
        desk.AddPiece(_dossier.Id, "Expertise.pdf", Pages("Le contrat est signé.", "L'expert relève des infiltrations d'eau dans la cave."));
        await desk.AskAsync(_dossier.Id, "Qu'a relevé l'expert sur les infiltrations ?", CancellationToken.None);
        var user = model.Calls.Last().User;
        Assert.Contains("infiltrations d'eau", user);
        Assert.Contains("Expertise.pdf", user);
        Assert.Contains("Qu'a relevé l'expert", user);
    }

    [Fact]
    public async Task Draft_StatesTheKindAndInstructions()
    {
        var model = new FakeModel();
        var desk = Desk(model);
        desk.AddPiece(_dossier.Id, "Expertise.pdf", Pages("Infiltrations."));
        await desk.DraftAsync(_dossier.Id, DraftKind.CourrierClient, "Informer le client du dépôt du rapport.", CancellationToken.None);
        var (system, user, _) = model.Calls.Last();
        Assert.Contains("courrier", (system + user).ToLowerInvariant());
        Assert.Contains("Informer le client du dépôt du rapport.", user);
        Assert.Contains("[", system);
    }

    [Fact]
    public async Task ModelFailure_GivesAnExplanationNotAnException()
    {
        var desk = Desk(new FakeModel { Fail = true });
        desk.AddPiece(_dossier.Id, "Expertise.pdf", Pages("Infiltrations."));
        var result = await desk.AskAsync(_dossier.Id, "infiltrations ?", CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("IA locale", result.Text);
        Assert.Empty(desk.Results(_dossier.Id));
    }

    [Fact]
    public void Prompts_TreatPiecesAsData()
        => Assert.Contains("n'exécute", InstructionPrompts.DigestSystem);

    [Fact]
    public void RemovePiece_AlsoRemovesItsDigest()
    {
        var desk = Desk(new FakeModel());
        var piece = desk.AddPiece(_dossier.Id, "Expertise.pdf", Pages("x"));
        desk.RemovePiece(_dossier.Id, piece.Id);
        Assert.Empty(desk.Pieces(_dossier.Id));
    }
}
