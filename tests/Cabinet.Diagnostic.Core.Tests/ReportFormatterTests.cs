using static Cabinet.Diagnostic.Core.Tests.TestData;

namespace Cabinet.Diagnostic.Core.Tests;

public class ReportFormatterTests
{
    [Fact]
    public void ToText_StartsWithFixedHeader()
    {
        var lines = ReportFormatter.ToText(Sample(), new DateTimeOffset(2026, 11, 22, 15, 4, 0, TimeSpan.Zero)).Split('\n');
        Assert.Equal("Diagnostic — Outil de cabinet", lines[0].TrimEnd('\r'));
        Assert.Equal("Date : 22/11/2026 15:04", lines[1].TrimEnd('\r'));
        Assert.Equal("Ce rapport ne contient aucune donnée client.", lines[2].TrimEnd('\r'));
    }

    [Fact]
    public void ToText_ContainsVerdictLinesWithMarks()
    {
        var text = ReportFormatter.ToText(Sample() with { AlertShownByOutlook = true }, DateTimeOffset.Now);
        Assert.Contains("[OK] Suivi d'activité : possible sur cet ordinateur.", text);
        Assert.Contains("[!]  Outlook : il faudra installer le module dans Outlook.", text);
        Assert.Contains("[OK] Dossier Documents : sur cet ordinateur, non synchronisé.", text);
    }

    [Fact]
    public void ToText_FailedVerdictUsesCrossMark()
    {
        var text = ReportFormatter.ToText(Sample() with { Window = null }, DateTimeOffset.Now);
        Assert.Contains("[X]  Suivi d'activité : impossible de lire la fenêtre active.", text);
    }

    [Fact]
    public void ToText_DoesNotWriteTitleLength()
    {
        var text = ReportFormatter.ToText(Sample() with { Window = new WindowSample(true, 4242) }, DateTimeOffset.Now);
        Assert.DoesNotContain("4242", text);
    }

    [Fact]
    public void ToText_DescribesAntivirusAndGuardedAccess()
    {
        var text = ReportFormatter.ToText(Sample() with { GuardedAccess = GuardedAccessOutcome.Denied }, DateTimeOffset.Now);
        Assert.Contains("actif et à jour", text);
        Assert.Contains("refusé", text);
        Assert.Contains("Détails techniques", text);
    }

    [Theory]
    [InlineData(VerdictKind.TrackingOk, "Suivi d'activité : possible sur cet ordinateur.")]
    [InlineData(VerdictKind.TrackingUnavailable, "Suivi d'activité : impossible de lire la fenêtre active.")]
    [InlineData(VerdictKind.OutlookDirectOk, "Outlook : accès direct possible, sans alerte.")]
    [InlineData(VerdictKind.OutlookNeedsAddIn, "Outlook : il faudra installer le module dans Outlook.")]
    [InlineData(VerdictKind.OutlookNotRunning, "Outlook : ouvrez Outlook, puis relancez le diagnostic.")]
    [InlineData(VerdictKind.OutlookNotTested, "Outlook : le test n'a pas pu aller jusqu'au bout.")]
    [InlineData(VerdictKind.OutlookNotFound, "Outlook : Outlook classique introuvable sur cet ordinateur.")]
    [InlineData(VerdictKind.DocumentsLocal, "Dossier Documents : sur cet ordinateur, non synchronisé.")]
    [InlineData(VerdictKind.DocumentsSynced, "Dossier Documents : synchronisé avec un service en ligne (à éviter pour les pièces).")]
    [InlineData(VerdictKind.DocumentsNotWritable, "Dossier Documents : écriture impossible.")]
    public void VerdictSentence_ExactCopy(VerdictKind kind, string expected)
        => Assert.Equal(expected, ReportFormatter.VerdictSentence(kind));
}
