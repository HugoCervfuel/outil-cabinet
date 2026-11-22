namespace Cabinet.Core;

public enum PastilleStatus { Recording, Unrecognized, Paused, Idle }

/// <summary>Ce qu'affiche la pastille.</summary>
public sealed record PastilleState(PastilleStatus Status, string Label, TimeSpan CurrentElapsed, TimeSpan DayTotal);

/// <summary>
/// La journée de travail : suivi d'activité, dossiers, enregistrement régulier, changement de jour et validation.
/// L'interface (pastille, fenêtres) ne parle qu'à cette classe.
/// </summary>
public sealed class WorkSession
{
    private const int RetentionDays = 30;
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(1);
    private static readonly char[] KeywordSeparators = { ',', ';', '\n', '\r' };

    private readonly Journal _journal;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Tracker _tracker;
    private readonly List<Dossier> _dossiers;
    private DateTimeOffset _lastSave;

    public WorkSession(Journal journal, IActivityProbe probe, Func<DateTimeOffset> clock)
    {
        _journal = journal;
        _clock = clock;
        _dossiers = journal.LoadDossiers();
        Settings = journal.LoadSettings();
        _tracker = new Tracker(probe, clock, OptionsFrom(Settings));

        var now = clock();
        Today = DateOnly.FromDateTime(now.Date);
        _tracker.Reset(journal.LoadDay(Today).Segments);
        journal.PurgeRawActivity(Today, RetentionDays);
        _lastSave = now;
    }

    public DateOnly Today { get; private set; }

    public Settings Settings { get; private set; }

    public IReadOnlyList<Dossier> Dossiers => _dossiers;

    public IReadOnlyList<Dossier> ActiveDossiers => _dossiers.Where(d => !d.Archived).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool Paused => _tracker.Paused;

    public IReadOnlyList<DateOnly> PendingDays => _journal.UnvalidatedDaysBefore(Today);

    public void SetPaused(bool paused) => _tracker.Paused = paused;

    public void Tick()
    {
        var now = _clock();
        var date = DateOnly.FromDateTime(now.Date);
        if (date != Today)
        {
            SaveDay();
            Today = date;
            _tracker.Reset(_journal.LoadDay(Today).Segments);
        }

        _tracker.Tick();

        if (now - _lastSave >= AutosaveInterval)
        {
            Save();
        }
    }

    public void Save()
    {
        SaveDay();
        _lastSave = _clock();
    }

    public PastilleState Pastille
    {
        get
        {
            var segments = _tracker.Segments;
            var total = DaySummary.Total(segments);
            if (_tracker.Paused)
            {
                return new PastilleState(PastilleStatus.Paused, "En pause", TimeSpan.Zero, total);
            }

            var current = _tracker.Current;
            if (current is null)
            {
                return new PastilleState(PastilleStatus.Idle, "Inactif", TimeSpan.Zero, total);
            }

            var dossierId = RecapBuilder.ResolveDossier(current, _dossiers);
            var elapsed = TimeSpan.Zero;
            for (var i = segments.Count - 1; i >= 0 && RecapBuilder.ResolveDossier(segments[i], _dossiers) == dossierId; i--)
            {
                elapsed += segments[i].Duration;
            }

            var dossier = _dossiers.FirstOrDefault(d => d.Id == dossierId);
            return dossier is null
                ? new PastilleState(PastilleStatus.Unrecognized, "Dossier ?", elapsed, total)
                : new PastilleState(PastilleStatus.Recording, dossier.Name, elapsed, total);
        }
    }

    public IReadOnlyList<(Dossier? Dossier, TimeSpan Duration)> TodayTotals
        => DaySummary.TotalsByDossier(_tracker.Segments, _dossiers)
            .Select(t => (_dossiers.FirstOrDefault(d => d.Id == t.DossierId), t.Duration))
            .ToList();

    public void AssignCurrent(Guid? dossierId) => _tracker.AssignCurrent(dossierId);

    public Dossier AddDossier(string name, string keywordsText, string? kleosReference)
    {
        var keywords = ParseKeywords(keywordsText);
        var dossier = Dossier.Create(name, keywords.Count > 0 ? keywords : Dossier.SuggestKeywords(name), kleosReference);
        _dossiers.Add(dossier);
        _journal.SaveDossiers(_dossiers);
        return dossier;
    }

    public void UpdateDossier(Guid id, string name, string keywordsText, string? kleosReference)
    {
        var index = _dossiers.FindIndex(d => d.Id == id);
        if (index < 0)
        {
            return;
        }

        var keywords = ParseKeywords(keywordsText);
        var fresh = Dossier.Create(name, keywords.Count > 0 ? keywords : Dossier.SuggestKeywords(name), kleosReference);
        _dossiers[index] = fresh with { Id = id, Archived = _dossiers[index].Archived };
        _journal.SaveDossiers(_dossiers);
    }

    public void ArchiveDossier(Guid id)
    {
        var index = _dossiers.FindIndex(d => d.Id == id);
        if (index >= 0)
        {
            _dossiers[index] = _dossiers[index] with { Archived = true };
            _journal.SaveDossiers(_dossiers);
        }
    }

    public void UpdateSettings(Settings settings)
    {
        Settings = settings;
        _journal.SaveSettings(settings);
        _tracker.Options = OptionsFrom(settings);
    }

    public IReadOnlyList<RecapLine> BuildRecap(DateOnly date)
    {
        var segments = date == Today ? _tracker.Segments : _journal.LoadDay(date).Segments;
        return RecapBuilder.Build(segments, _dossiers, TimeSpan.FromMinutes(Settings.RoundingMinutes));
    }

    public IReadOnlyList<RecapLine>? ValidatedLines(DateOnly date) => _journal.LoadDay(date).Validated;

    public bool IsValidated(DateOnly date) => _journal.LoadDay(date).IsValidated;

    public void ValidateDay(DateOnly date, IEnumerable<RecapLine> lines)
    {
        var day = _journal.LoadDay(date);
        var segments = date == Today ? _tracker.Segments.ToList() : day.Segments;
        _journal.SaveDay(day with { Segments = segments, Validated = lines.ToList() });
    }

    public string KleosText(DateOnly date, IEnumerable<RecapLine> lines) => KleosFormatter.Day(date, lines, _dossiers);

    public static IReadOnlyList<string> ParseKeywords(string? text)
        => (text ?? "")
            .Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TextNormalizer.Normalize)
            .Where(k => k.Length > 0)
            .Distinct()
            .ToList();

    private void SaveDay()
    {
        var day = _journal.LoadDay(Today);
        _journal.SaveDay(day with { Segments = _tracker.Segments.ToList() });
    }

    private static TrackerOptions OptionsFrom(Settings settings) => new()
    {
        IdleThreshold = TimeSpan.FromMinutes(Math.Max(1, settings.IdleMinutes)),
        Exclusions = settings.Exclusions,
    };
}
