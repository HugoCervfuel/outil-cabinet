using System.Globalization;
using System.Text;

namespace Cabinet.Core;

/// <summary>Mise en forme commune pour comparer des textes sans tenir compte de la casse ni des accents.</summary>
public static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Découpe en mots (lettres et chiffres), normalisés.</summary>
    public static IReadOnlyList<string> Tokens(string? text)
    {
        var normalized = Normalize(text);
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

/// <summary>Rattache un titre de fenêtre à un dossier d'après ses mots-clés.</summary>
public static class DossierMatcher
{
    /// <summary>
    /// Le dossier dont le plus de mots-clés apparaissent, en mots entiers. En cas d'égalité ou d'absence : null
    /// (jamais de rattachement au hasard).
    /// </summary>
    public static Guid? Match(string title, IEnumerable<Dossier> dossiers)
    {
        var haystack = " " + string.Join(' ', TextNormalizer.Tokens(title)) + " ";
        Guid? best = null;
        var bestScore = 0;
        var tie = false;

        foreach (var dossier in dossiers)
        {
            if (dossier.Archived)
            {
                continue;
            }

            var score = dossier.Keywords
                .Select(k => string.Join(' ', TextNormalizer.Tokens(k)))
                .Where(k => k.Length > 0)
                .Distinct()
                .Count(k => haystack.Contains(" " + k + " ", StringComparison.Ordinal));

            if (score > bestScore)
            {
                best = dossier.Id;
                bestScore = score;
                tie = false;
            }
            else if (score == bestScore && score > 0)
            {
                tie = true;
            }
        }

        return tie ? null : best;
    }
}

/// <summary>Déduit la nature d'une activité de l'application et du titre.</summary>
public static class ActivityClassifier
{
    private static readonly string[] ResearchSites =
    {
        "legifrance", "doctrine", "dalloz", "lexis", "lexbase", "lamyline", "lamy", "judilibre", "cour de cassation", "conseil d'etat", "eur-lex",
    };

    public static ActivityKind Classify(string app, string title)
    {
        var a = TextNormalizer.Normalize(app);
        var t = TextNormalizer.Normalize(title);

        if (a.Contains("word") || a == "pages")
        {
            return ActivityKind.Redaction;
        }

        if (a.Contains("outlook") || a == "mail" || a.Contains("thunderbird"))
        {
            return ActivityKind.Correspondance;
        }

        if (a.Contains("apercu") || a.Contains("preview") || a.Contains("acrobat") || a.Contains("acrord") || a.Contains("pdf"))
        {
            return ActivityKind.Lecture;
        }

        if (t.Contains("kleos"))
        {
            return ActivityKind.Gestion;
        }

        if (ResearchSites.Any(t.Contains))
        {
            return ActivityKind.Recherche;
        }

        return t.Contains(".pdf") ? ActivityKind.Lecture : ActivityKind.Autre;
    }

    public static string Label(ActivityKind kind) => kind switch
    {
        ActivityKind.Redaction => "Rédaction",
        ActivityKind.Correspondance => "Correspondance",
        ActivityKind.Lecture => "Lecture et analyse",
        ActivityKind.Recherche => "Recherche juridique",
        ActivityKind.Gestion => "Gestion du dossier",
        _ => "Autre",
    };
}

/// <summary>Applications et mots qui ne doivent jamais être enregistrés.</summary>
public static class ExclusionFilter
{
    public static bool IsExcluded(string app, string title, IEnumerable<string> rules)
    {
        var a = TextNormalizer.Normalize(app);
        var t = TextNormalizer.Normalize(title);
        return rules
            .Select(TextNormalizer.Normalize)
            .Where(r => r.Length > 0)
            .Any(r => a.Contains(r, StringComparison.Ordinal) || t.Contains(r, StringComparison.Ordinal));
    }
}
