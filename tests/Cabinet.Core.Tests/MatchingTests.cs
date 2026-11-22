namespace Cabinet.Core.Tests;

public class MatchingTests
{
    private static readonly Dossier Martin = Dossier.Create("MARTIN c/ Axa", new[] { "martin", "axa" }, "2026-0142");
    private static readonly Dossier Dupont = Dossier.Create("DUPONT / SCI Les Lys", new[] { "dupont", "lys" }, null);
    private static readonly Dossier AutreAxa = Dossier.Create("LEROY c/ Axa", new[] { "leroy", "axa" }, null);

    [Fact]
    public void Normalize_RemovesAccentsAndCase()
        => Assert.Equal("societe generale", TextNormalizer.Normalize("Société  GÉNÉRALE"));

    [Fact]
    public void Match_KeywordInTitle_FindsDossier()
        => Assert.Equal(Martin.Id, DossierMatcher.Match("Conclusions_MARTIN_v3.docx - Word", new[] { Martin, Dupont }));

    [Fact]
    public void Match_IgnoresAccentsAndCase()
        => Assert.Equal(Dupont.Id, DossierMatcher.Match("Assignation Dupônt", new[] { Martin, Dupont }));

    [Fact]
    public void Match_KeywordInsideAnotherWord_DoesNotMatch()
        => Assert.Null(DossierMatcher.Match("Taxation des honoraires", new[] { Martin }));

    [Fact]
    public void Match_NoKeyword_IsNull()
        => Assert.Null(DossierMatcher.Match("Boîte de réception - Outlook", new[] { Martin, Dupont }));

    [Fact]
    public void Match_MoreKeywordsWins()
        => Assert.Equal(Martin.Id, DossierMatcher.Match("Martin contre Axa - courrier", new[] { Martin, AutreAxa }));

    [Fact]
    public void Match_Tie_IsNull()
        => Assert.Null(DossierMatcher.Match("Sinistre Axa", new[] { Martin, AutreAxa }));

    [Fact]
    public void Match_ArchivedDossier_IsIgnored()
        => Assert.Null(DossierMatcher.Match("MARTIN", new[] { Martin with { Archived = true } }));

    [Fact]
    public void SuggestKeywords_FromName_SkipsShortAndCommonWords()
        => Assert.Equal(new[] { "martin", "axa" }, Dossier.SuggestKeywords("MARTIN c/ Axa"));

    [Fact]
    public void SuggestKeywords_SkipsLegalForms()
        => Assert.Equal(new[] { "dupont", "lys" }, Dossier.SuggestKeywords("DUPONT / SCI Les Lys"));
}

public class ClassifierTests
{
    [Theory]
    [InlineData("Microsoft Word", "Conclusions.docx", ActivityKind.Redaction)]
    [InlineData("WINWORD", "Conclusions.docx - Word", ActivityKind.Redaction)]
    [InlineData("Microsoft Outlook", "RE: dossier Martin", ActivityKind.Correspondance)]
    [InlineData("OUTLOOK", "Boîte de réception", ActivityKind.Correspondance)]
    [InlineData("Aperçu", "Pièce 12.pdf", ActivityKind.Lecture)]
    [InlineData("Acrobat", "Pièce 12.pdf", ActivityKind.Lecture)]
    [InlineData("Google Chrome", "Kleos - Dossier MARTIN", ActivityKind.Gestion)]
    [InlineData("msedge", "Légifrance - Code civil", ActivityKind.Recherche)]
    [InlineData("Safari", "Dalloz.fr", ActivityKind.Recherche)]
    [InlineData("Finder", "Documents", ActivityKind.Autre)]
    public void Classify(string app, string title, ActivityKind expected)
        => Assert.Equal(expected, ActivityClassifier.Classify(app, title));

    [Fact]
    public void Label_IsFrench()
        => Assert.Equal("Lecture et analyse", ActivityClassifier.Label(ActivityKind.Lecture));
}

public class ExclusionTests
{
    [Fact]
    public void Excluded_ByAppName()
        => Assert.True(ExclusionFilter.IsExcluded("Banque Populaire", "Comptes", new[] { "banque" }));

    [Fact]
    public void Excluded_ByTitleWord()
        => Assert.True(ExclusionFilter.IsExcluded("Safari", "Ma banque en ligne", new[] { "banque" }));

    [Fact]
    public void NotExcluded()
        => Assert.False(ExclusionFilter.IsExcluded("Microsoft Word", "Conclusions", new[] { "banque" }));

    [Fact]
    public void EmptyRule_ExcludesNothing()
        => Assert.False(ExclusionFilter.IsExcluded("Microsoft Word", "Conclusions", new[] { " " }));
}
