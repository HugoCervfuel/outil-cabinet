namespace Cabinet.Core;

/// <summary>Nature d'une activité, alignée sur les types de prestation saisis dans Kleos.</summary>
public enum ActivityKind { Redaction, Correspondance, Lecture, Recherche, Gestion, Autre }

/// <summary>
/// Un dossier de l'avocat : ce qui sert au rattachement automatique (mots-clés, adresses e-mail connues) et
/// « où en est le dossier » (notes), contexte donné à l'assistant courrier.
/// </summary>
public sealed record Dossier(
    Guid Id,
    string Name,
    IReadOnlyList<string> Keywords,
    string? KleosReference,
    bool Archived = false,
    string? Notes = null,
    IReadOnlyList<string>? Addresses = null)
{
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "c", "contre", "sci", "sas", "sasu", "sarl", "sa", "eurl", "snc", "scp", "selarl",
        "les", "des", "et", "du", "de", "la", "le", "aff", "affaire", "dossier", "epoux", "mme", "mr", "m",
    };

    public static Dossier Create(string name, IEnumerable<string> keywords, string? kleosReference)
        => new(
            Guid.NewGuid(),
            name.Trim(),
            keywords.Select(TextNormalizer.Normalize).Where(k => k.Length > 0).Distinct().ToList(),
            string.IsNullOrWhiteSpace(kleosReference) ? null : kleosReference.Trim());

    /// <summary>Mots-clés proposés à partir du nom : mots d'au moins 3 lettres, hors formes juridiques et mots courants.</summary>
    public static IReadOnlyList<string> SuggestKeywords(string name)
        => TextNormalizer.Tokens(name)
            .Where(t => t.Length >= 3 && !CommonWords.Contains(t))
            .Distinct()
            .ToList();
}

/// <summary>Ce qui est au premier plan à un instant donné. Le titre est une donnée client : il reste chiffré sur le poste.</summary>
public sealed record ActiveWindow(string App, string Title);

public sealed record ActivitySample(DateTimeOffset At, string App, string Title);

/// <summary>Une période continue passée sur une même fenêtre.</summary>
public sealed record Segment(DateTimeOffset Start, DateTimeOffset End, string App, string Title, Guid? AssignedDossierId)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Une ligne du récapitulatif, telle qu'elle sera reportée dans Kleos.</summary>
public sealed record RecapLine(Guid? DossierId, ActivityKind Kind, TimeSpan Duration, string Description);

/// <summary>Une journée : l'activité brute (effacée après 30 jours) et les temps validés (conservés).</summary>
public sealed record DayRecord(DateOnly Date, List<Segment> Segments, List<RecapLine>? Validated)
{
    public static DayRecord Empty(DateOnly date) => new(date, new List<Segment>(), null);

    public bool IsValidated => Validated is not null;
}

/// <summary>Réglages de l'utilisateur.</summary>
public sealed record Settings
{
    public IReadOnlyList<string> Exclusions { get; init; } = Array.Empty<string>();

    public int IdleMinutes { get; init; } = 5;

    public int RoundingMinutes { get; init; } = 5;

    public TimeOnly ReminderTime { get; init; } = new(18, 30);
}
