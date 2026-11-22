using System.Globalization;
using System.Text.RegularExpressions;

namespace Cabinet.Core;

public static partial class Durations
{
    public static TimeSpan RoundToNearest(TimeSpan duration, TimeSpan unit)
    {
        if (unit <= TimeSpan.Zero)
        {
            return duration;
        }

        var units = Math.Round(duration / unit, MidpointRounding.AwayFromZero);
        return unit * units;
    }

    /// <summary>« 1 h 40 », « 0 h 05 ».</summary>
    public static string Format(TimeSpan duration)
    {
        var totalMinutes = (int)Math.Round(duration.TotalMinutes);
        return $"{totalMinutes / 60} h {totalMinutes % 60:00}";
    }

    /// <summary>Accepte « 1 h 40 », « 1h40 », « 1:40 », « 2 h » ou un nombre de minutes (« 45 »).</summary>
    public static TimeSpan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var hours = HoursPattern().Match(text);
        if (hours.Success)
        {
            var h = int.Parse(hours.Groups[1].Value, CultureInfo.InvariantCulture);
            var m = hours.Groups[2].Success ? int.Parse(hours.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return m < 60 ? TimeSpan.FromMinutes(h * 60 + m) : null;
        }

        var minutes = MinutesPattern().Match(text);
        return minutes.Success ? TimeSpan.FromMinutes(int.Parse(minutes.Groups[1].Value, CultureInfo.InvariantCulture)) : null;
    }

    [GeneratedRegex(@"^\s*(\d{1,2})\s*(?:h|:)\s*(\d{1,2})?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HoursPattern();

    [GeneratedRegex(@"^\s*(\d{1,3})\s*(?:min|mn|m)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MinutesPattern();
}

/// <summary>Construit le récapitulatif du jour à partir des périodes d'activité.</summary>
public static class RecapBuilder
{
    private static readonly string[] AppSuffixes =
    {
        "word", "microsoft word", "outlook", "microsoft outlook", "message (html)", "message (texte brut)", "message (texte enrichi)",
        "adobe acrobat reader", "adobe acrobat", "acrobat reader", "google chrome", "microsoft edge", "mozilla firefox", "safari",
    };

    public static Guid? ResolveDossier(Segment segment, IReadOnlyList<Dossier> dossiers)
        => segment.AssignedDossierId ?? DossierMatcher.Match(segment.Title, dossiers);

    public static IReadOnlyList<RecapLine> Build(IEnumerable<Segment> segments, IReadOnlyList<Dossier> dossiers, TimeSpan roundingUnit)
    {
        var groups = new List<(Guid? Dossier, ActivityKind Kind, TimeSpan Duration, List<string> Titles)>();
        foreach (var segment in segments)
        {
            var dossier = ResolveDossier(segment, dossiers);
            var kind = ActivityClassifier.Classify(segment.App, segment.Title);
            var index = groups.FindIndex(g => g.Dossier == dossier && g.Kind == kind);
            if (index < 0)
            {
                groups.Add((dossier, kind, TimeSpan.Zero, new List<string>()));
                index = groups.Count - 1;
            }

            var group = groups[index];
            var title = CleanTitle(segment.Title);
            if (title.Length > 0 && !group.Titles.Contains(title))
            {
                group.Titles.Add(title);
            }

            groups[index] = group with { Duration = group.Duration + segment.Duration };
        }

        // Les lignes d'un même dossier restent groupées, dans l'ordre de première apparition.
        var dossierOrder = groups.Select(g => g.Dossier).Distinct().ToList();
        return groups
            .OrderBy(g => dossierOrder.IndexOf(g.Dossier))
            .Select(g => new RecapLine(g.Dossier, g.Kind, Durations.RoundToNearest(g.Duration, roundingUnit), Describe(g.Kind, g.Titles)))
            .Where(l => l.Duration > TimeSpan.Zero)
            .ToList();
    }

    public static string CleanTitle(string title)
    {
        var text = title.Trim();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var separator in new[] { " - ", " — ", " – " })
            {
                var index = text.LastIndexOf(separator, StringComparison.Ordinal);
                if (index > 0 && AppSuffixes.Contains(TextNormalizer.Normalize(text[(index + separator.Length)..])))
                {
                    text = text[..index].TrimEnd();
                    changed = true;
                }
            }
        }

        return text;
    }

    private static string Describe(ActivityKind kind, IReadOnlyList<string> titles)
    {
        var label = ActivityClassifier.Label(kind);
        if (titles.Count == 0)
        {
            return label;
        }

        var shown = string.Join(", ", titles.Take(2));
        var rest = titles.Count - 2;
        return rest > 0 ? $"{label} : {shown} et {rest} autre{(rest > 1 ? "s" : "")}" : $"{label} : {shown}";
    }
}

public static class DaySummary
{
    /// <summary>Total par dossier, du plus long au plus court ; le temps non reconnu en dernier.</summary>
    public static IReadOnlyList<(Guid? DossierId, TimeSpan Duration)> TotalsByDossier(IEnumerable<Segment> segments, IReadOnlyList<Dossier> dossiers)
        => segments
            .GroupBy(s => RecapBuilder.ResolveDossier(s, dossiers))
            .Select(g => (DossierId: g.Key, Duration: g.Aggregate(TimeSpan.Zero, (t, s) => t + s.Duration)))
            .OrderBy(t => t.DossierId is null ? 1 : 0)
            .ThenByDescending(t => t.Duration)
            .ToList();

    public static TimeSpan Total(IEnumerable<Segment> segments)
        => segments.Aggregate(TimeSpan.Zero, (t, s) => t + s.Duration);
}

/// <summary>Texte prêt à reporter dans Kleos, une ligne par temps, champs séparés par des tabulations.</summary>
public static class KleosFormatter
{
    public static string Line(DateOnly date, RecapLine line, Dossier? dossier)
    {
        var minutes = (int)Math.Round(line.Duration.TotalMinutes);
        return string.Join('\t',
            date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            dossier?.Name ?? "Non attribué",
            dossier?.KleosReference ?? "",
            $"{minutes / 60}:{minutes % 60:00}",
            ActivityClassifier.Label(line.Kind),
            line.Description);
    }

    public static string Day(DateOnly date, IEnumerable<RecapLine> lines, IReadOnlyList<Dossier> dossiers)
        => string.Join("\r\n", lines.Select(l => Line(date, l, dossiers.FirstOrDefault(d => d.Id == l.DossierId))));
}
