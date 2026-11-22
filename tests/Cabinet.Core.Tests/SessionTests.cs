namespace Cabinet.Core.Tests;

public class SessionTests : IDisposable
{
    private sealed class FixedKey : IKeyStore
    {
        public byte[] GetOrCreateKey() => Enumerable.Repeat((byte)7, 32).ToArray();
    }

    private sealed class FakeProbe : IActivityProbe
    {
        public ActiveWindow? Window { get; set; } = new("Microsoft Word", "Conclusions MARTIN.docx");
        public ActiveWindow? ReadActiveWindow() => Window;
        public TimeSpan? IdleTime() => TimeSpan.Zero;
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cabinet-session-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(1));
    private readonly FakeProbe _probe = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private WorkSession NewSession() => new(new Journal(new EncryptedStore(_folder, new FixedKey())), _probe, () => _now);

    private void Run(WorkSession session, int seconds)
    {
        for (var i = 0; i < seconds / 2; i++)
        {
            session.Tick();
            _now = _now.AddSeconds(2);
        }

        session.Tick();
    }

    [Fact]
    public void AddDossier_ParsesKeywordsAndPersists()
    {
        var session = NewSession();
        session.AddDossier("MARTIN c/ Axa", "martin, axa ; Sinistre", "2026-0142");
        var reloaded = NewSession();
        var dossier = Assert.Single(reloaded.Dossiers);
        Assert.Equal(new[] { "martin", "axa", "sinistre" }, dossier.Keywords);
    }

    [Fact]
    public void Pastille_RecognizedDossier_ShowsNameAndElapsed()
    {
        var session = NewSession();
        session.AddDossier("MARTIN c/ Axa", "martin", null);
        Run(session, 120);
        var state = session.Pastille;
        Assert.Equal(PastilleStatus.Recording, state.Status);
        Assert.Equal("MARTIN c/ Axa", state.Label);
        Assert.Equal(TimeSpan.FromMinutes(2), state.CurrentElapsed);
        Assert.Equal(TimeSpan.FromMinutes(2), state.DayTotal);
    }

    [Fact]
    public void Pastille_UnknownWindow_ShowsQuestionMark()
    {
        var session = NewSession();
        Run(session, 10);
        Assert.Equal(PastilleStatus.Unrecognized, session.Pastille.Status);
        Assert.Equal("Dossier ?", session.Pastille.Label);
    }

    [Fact]
    public void Pastille_Paused()
    {
        var session = NewSession();
        session.SetPaused(true);
        Run(session, 10);
        Assert.Equal(PastilleStatus.Paused, session.Pastille.Status);
        Assert.Equal("En pause", session.Pastille.Label);
    }

    [Fact]
    public void AssignCurrent_OverridesRecognition()
    {
        var session = NewSession();
        var dupont = session.AddDossier("DUPONT", "dupont", null);
        Run(session, 10);
        session.AssignCurrent(dupont.Id);
        Run(session, 10);
        Assert.Equal("DUPONT", session.Pastille.Label);
    }

    [Fact]
    public void Save_ThenReopen_KeepsTodayActivity()
    {
        var session = NewSession();
        session.AddDossier("MARTIN c/ Axa", "martin", null);
        Run(session, 60);
        session.Save();
        var reopened = NewSession();
        Assert.Equal(TimeSpan.FromMinutes(1), reopened.Pastille.DayTotal);
    }

    [Fact]
    public void DayChange_StartsNewDayAndKeepsYesterdayPending()
    {
        var session = NewSession();
        session.AddDossier("MARTIN c/ Axa", "martin", null);
        Run(session, 60);
        _now = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.FromHours(1));
        Run(session, 10);
        Assert.Equal(new DateOnly(2026, 11, 21), session.Today);
        Assert.Equal(TimeSpan.FromSeconds(10), session.Pastille.DayTotal);
        Assert.Equal(new[] { new DateOnly(2026, 11, 20) }, session.PendingDays);
    }

    [Fact]
    public void ValidateDay_StoresLinesAndClearsPending()
    {
        var session = NewSession();
        session.AddDossier("MARTIN c/ Axa", "martin", "2026-0142");
        Run(session, 600);
        var lines = session.BuildRecap(session.Today);
        var line = Assert.Single(lines);
        Assert.Equal(TimeSpan.FromMinutes(10), line.Duration);

        session.ValidateDay(session.Today, lines);
        Assert.True(session.IsValidated(session.Today));
        Assert.Contains("2026-0142", session.KleosText(session.Today, lines));
    }

    [Fact]
    public void ArchiveDossier_StopsMatching()
    {
        var session = NewSession();
        var martin = session.AddDossier("MARTIN c/ Axa", "martin", null);
        session.ArchiveDossier(martin.Id);
        Run(session, 10);
        Assert.Equal(PastilleStatus.Unrecognized, session.Pastille.Status);
    }

    [Theory]
    [InlineData("martin, axa", new[] { "martin", "axa" })]
    [InlineData("martin;axa\nsinistre", new[] { "martin", "axa", "sinistre" })]
    [InlineData("  ", new string[0])]
    public void ParseKeywords(string text, string[] expected)
        => Assert.Equal(expected, WorkSession.ParseKeywords(text));
}

public class DossierContextTests : IDisposable
{
    private sealed class FixedKey : IKeyStore
    {
        public byte[] GetOrCreateKey() => Enumerable.Repeat((byte)9, 32).ToArray();
    }

    private sealed class NoProbe : IActivityProbe
    {
        public ActiveWindow? ReadActiveWindow() => null;
        public TimeSpan? IdleTime() => null;
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cabinet-ctx-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private WorkSession Session() => new(new Journal(new EncryptedStore(_folder, new FixedKey())), new NoProbe(), () => new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void SetNotes_Persists()
    {
        var session = Session();
        var dossier = session.AddDossier("MARTIN c/ Axa", "martin", null);
        session.SetNotes(dossier.Id, "Audience le 15/01. Attente des commentaires du client.");
        Assert.Equal("Audience le 15/01. Attente des commentaires du client.", Session().Dossiers.Single().Notes);
    }

    [Fact]
    public void AddAddress_IsCaseInsensitiveAndUnique()
    {
        var session = Session();
        var dossier = session.AddDossier("MARTIN c/ Axa", "martin", null);
        session.AddAddress(dossier.Id, "P.Martin@exemple.fr");
        session.AddAddress(dossier.Id, "p.martin@exemple.fr ");
        Assert.Equal(new[] { "p.martin@exemple.fr" }, Session().Dossiers.Single().Addresses);
    }

    [Fact]
    public void UpdateDossier_KeepsNotesAndAddresses()
    {
        var session = Session();
        var dossier = session.AddDossier("MARTIN c/ Axa", "martin", null);
        session.SetNotes(dossier.Id, "Notes");
        session.AddAddress(dossier.Id, "p.martin@exemple.fr");
        session.UpdateDossier(dossier.Id, "MARTIN c/ Axa (appel)", "martin, axa", "2026-0142");
        var updated = Session().Dossiers.Single();
        Assert.Equal("Notes", updated.Notes);
        Assert.Equal(new[] { "p.martin@exemple.fr" }, updated.Addresses);
    }
}
