namespace Cabinet.Core.Tests;

public class RecapTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(1));
    private static readonly Dossier Martin = Dossier.Create("MARTIN c/ Axa", new[] { "martin", "axa" }, "2026-0142");
    private static readonly Dossier Dupont = Dossier.Create("DUPONT / SCI Les Lys", new[] { "dupont", "lys" }, null);

    private static Segment Seg(int startMin, int minutes, string app, string title, Guid? assigned = null)
        => new(T0.AddMinutes(startMin), T0.AddMinutes(startMin + minutes), app, title, assigned);

    [Fact]
    public void Build_GroupsByDossierAndKind()
    {
        var lines = RecapBuilder.Build(new[]
        {
            Seg(0, 30, "Microsoft Word", "Conclusions MARTIN.docx"),
            Seg(30, 10, "Microsoft Outlook", "RE: Martin Axa"),
            Seg(40, 20, "Microsoft Word", "Conclusions MARTIN.docx"),
            Seg(60, 25, "Microsoft Word", "Assignation Dupont.docx"),
        }, new[] { Martin, Dupont }, TimeSpan.FromMinutes(5));

        Assert.Equal(3, lines.Count);
        var redactionMartin = lines.Single(l => l.DossierId == Martin.Id && l.Kind == ActivityKind.Redaction);
        Assert.Equal(TimeSpan.FromMinutes(50), redactionMartin.Duration);
        Assert.Contains("Conclusions MARTIN.docx", redactionMartin.Description);
    }

    [Fact]
    public void Build_UnrecognizedSegments_GoToNullDossier()
    {
        var lines = RecapBuilder.Build(new[] { Seg(0, 12, "Finder", "Documents") }, new[] { Martin }, TimeSpan.FromMinutes(5));
        var line = Assert.Single(lines);
        Assert.Null(line.DossierId);
    }

    [Fact]
    public void Build_AssignedDossierWinsOverMatching()
    {
        var lines = RecapBuilder.Build(new[] { Seg(0, 10, "Finder", "Documents", Dupont.Id) }, new[] { Martin, Dupont }, TimeSpan.FromMinutes(5));
        Assert.Equal(Dupont.Id, Assert.Single(lines).DossierId);
    }

    [Fact]
    public void Build_RoundsToNearestUnit()
    {
        var lines = RecapBuilder.Build(new[] { Seg(0, 13, "Microsoft Word", "MARTIN.docx") }, new[] { Martin }, TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(15), Assert.Single(lines).Duration);
    }

    [Fact]
    public void Build_DropsLinesRoundedToZero()
        => Assert.Empty(RecapBuilder.Build(new[] { Seg(0, 2, "Microsoft Word", "MARTIN.docx") }, new[] { Martin }, TimeSpan.FromMinutes(5)));

    [Fact]
    public void Build_DescriptionListsAtMostTwoTitlesThenCount()
    {
        var lines = RecapBuilder.Build(new[]
        {
            Seg(0, 5, "Aperçu", "Pièce 1 Martin.pdf"),
            Seg(5, 5, "Aperçu", "Pièce 2 Martin.pdf"),
            Seg(10, 5, "Aperçu", "Pièce 3 Martin.pdf"),
        }, new[] { Martin }, TimeSpan.FromMinutes(5));
        Assert.Equal("Lecture et analyse : Pièce 1 Martin.pdf, Pièce 2 Martin.pdf et 1 autre", Assert.Single(lines).Description);
    }

    [Fact]
    public void CleanTitle_RemovesApplicationSuffix()
        => Assert.Equal("Conclusions_MARTIN_v3.docx", RecapBuilder.CleanTitle("Conclusions_MARTIN_v3.docx - Word"));

    [Theory]
    [InlineData(13, 15)]
    [InlineData(12, 10)]
    [InlineData(2, 0)]
    [InlineData(100, 100)]
    public void RoundToNearest(int minutes, int expected)
        => Assert.Equal(TimeSpan.FromMinutes(expected), Durations.RoundToNearest(TimeSpan.FromMinutes(minutes), TimeSpan.FromMinutes(5)));

    [Theory]
    [InlineData(100, "1 h 40")]
    [InlineData(35, "0 h 35")]
    [InlineData(5, "0 h 05")]
    public void Format(int minutes, string expected)
        => Assert.Equal(expected, Durations.Format(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData("1 h 40", 100)]
    [InlineData("1h40", 100)]
    [InlineData("1:40", 100)]
    [InlineData("45", 45)]
    [InlineData("2 h", 120)]
    public void Parse(string text, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), Durations.Parse(text));

    [Fact]
    public void Parse_Invalid_IsNull()
        => Assert.Null(Durations.Parse("beaucoup"));

    [Fact]
    public void Kleos_LineFormat()
    {
        var line = new RecapLine(Martin.Id, ActivityKind.Redaction, TimeSpan.FromMinutes(100), "Rédaction : Conclusions.docx");
        Assert.Equal("20/11/2026\tMARTIN c/ Axa\t2026-0142\t1:40\tRédaction\tRédaction : Conclusions.docx",
            KleosFormatter.Line(new DateOnly(2026, 11, 20), line, Martin));
    }

    [Fact]
    public void TotalsByDossier_SumsAndSorts()
    {
        var totals = DaySummary.TotalsByDossier(new[]
        {
            Seg(0, 30, "Microsoft Word", "MARTIN.docx"),
            Seg(30, 60, "Microsoft Word", "Dupont.docx"),
            Seg(90, 10, "Finder", "Documents"),
        }, new[] { Martin, Dupont });
        Assert.Equal(new[] { Dupont.Id, Martin.Id, (Guid?)null }, totals.Select(t => t.DossierId));
        Assert.Equal(TimeSpan.FromMinutes(60), totals[0].Duration);
    }
}

public class StoreTests : IDisposable
{
    private sealed class FixedKey : IKeyStore
    {
        private readonly byte[] _key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        public byte[] GetOrCreateKey() => _key;
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cabinet-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new EncryptedStore(_folder, new FixedKey());
        var dossiers = new List<Dossier> { Dossier.Create("MARTIN c/ Axa", new[] { "martin" }, null) };
        store.Save("dossiers", dossiers);
        var loaded = store.Load<List<Dossier>>("dossiers");
        Assert.Equal("MARTIN c/ Axa", Assert.Single(loaded!).Name);
    }

    [Fact]
    public void File_DoesNotContainPlainText()
    {
        var store = new EncryptedStore(_folder, new FixedKey());
        store.Save("dossiers", new List<Dossier> { Dossier.Create("CONFIDENTIEL-DUPONT", new[] { "dupont" }, null) });
        var bytes = File.ReadAllBytes(Directory.GetFiles(_folder, "*", SearchOption.AllDirectories).Single());
        Assert.DoesNotContain("DUPONT", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Load_Missing_IsNull()
        => Assert.Null(new EncryptedStore(_folder, new FixedKey()).Load<List<Dossier>>("absent"));

    [Fact]
    public void Load_TamperedFile_IsNull()
    {
        var store = new EncryptedStore(_folder, new FixedKey());
        store.Save("dossiers", new List<Dossier>());
        var file = Directory.GetFiles(_folder, "*", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(file, bytes);
        Assert.Null(store.Load<List<Dossier>>("dossiers"));
    }

    [Fact]
    public void PurgeOldDays_RemovesSegmentsButKeepsValidatedLines()
    {
        var store = new EncryptedStore(_folder, new FixedKey());
        var journal = new Journal(store);
        var old = new DateOnly(2026, 9, 1);
        journal.SaveDay(new DayRecord(old,
            new List<Segment> { new(DateTimeOffset.Now, DateTimeOffset.Now, "Word", "secret", null) },
            new List<RecapLine> { new(null, ActivityKind.Autre, TimeSpan.FromMinutes(10), "x") }));
        journal.PurgeRawActivity(new DateOnly(2026, 11, 20), retentionDays: 30);
        var day = journal.LoadDay(old);
        Assert.Empty(day.Segments);
        Assert.Single(day.Validated!);
    }

    [Fact]
    public void CoreAssembly_HasNoNetworkReferences()
        => Cabinet.Diagnostic.Core.Tests.NetworkGuard.AssertNoNetworkReferences(typeof(EncryptedStore).Assembly);
}
